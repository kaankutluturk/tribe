using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Tribe.App
{
    // What tribe.exe knows about the game, as an immutable snapshot the window reads on its timer.
    sealed class Snapshot
    {
        public string Status = "waiting for green hell";
        public bool Running, Linked, Applied;
        public int Lobby, Limit;                          // Lobby: 0 none, 1 member, 2 owner
        public PlayerRow[] Players = new PlayerRow[0];
        public int Unpatched { get { int n = 0; foreach (var p in Players) if (p.Patch == 0) n++; return n; } }
        public string Error = "";
        public static readonly Snapshot Waiting = new Snapshot();
    }

    // One lobby member, as the core reports it.
    sealed class PlayerRow
    {
        public ulong Id;
        public string Name = "";
        public bool Self, Host;
        public int Patch;                                 // 0 no tribe, 1 patched, 2 just joined (not known yet)
        public int Ping = -1;                             // ms to the host, -1 unknown
    }

    // Attach loop (its own thread, every 500 ms): find GH.exe and inject the core into it once. The core patches the
    // game and keeps the patch for as long as the game runs, whether this exe stays open or not; while it is open
    // the core reports status and takes the lobby size. A game that already has the patch (tribe was run before) is
    // injected again all the same: Mono hands back the loaded core, which just reconnects.
    sealed class GameSession : IDisposable
    {
        const string ProcessName = "GH";
        const int MaxAttempts = 3;

        readonly Link link = new Link();
        readonly string coreFile;                          // a core next to the exe (dev builds); otherwise the embedded one
        readonly Thread thread;
        volatile bool disposed;
        volatile Snapshot state = Snapshot.Waiting;
        volatile int lobbySize;

        // The core proves it is the one this exe injected by echoing a token only the two of them know. The link is
        // loopback-only, but any local program could connect to the port; without the token it is ignored and dropped.
        readonly string token = Guid.NewGuid().ToString("N");
        volatile bool trusted;

        Process game;
        int attempts;
        DateTime injectedAt;
        bool pending;
        string status = "waiting for green hell";
        Snapshot last = new Snapshot();

        public Snapshot State { get { return state; } }

        public Action<string> Notice;                      // background thread
        volatile bool menuShown;

        public GameSession(string coreFile, int lobbySize)
        {
            this.coreFile = coreFile;
            this.lobbySize = lobbySize;
            link.Connected = () => { trusted = false; };
            link.Received = OnLine;
            link.Disconnected = () => { if (trusted) Log.Write("core disconnected"); trusted = false; last = new Snapshot(); Publish(); };
            thread = new Thread(Loop) { IsBackground = true, Name = "attach" };
        }

        public void Start() { thread.Start(); }

        bool Send(string line) { return trusted && link.Send(line); }

        public void SetLobbySize(int size)
        {
            lobbySize = size;
            Send("limit\t" + size.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The menu is up: the core has the game free the mouse so it can be clicked.</summary>
        public void SetMenu(bool shown)
        {
            menuShown = shown;
            Send("menu\t" + (shown ? "1" : "0"));
        }

        volatile bool autoKick = true;
        volatile string bans = "";

        /// <summary>Host: remove players without tribe when the lobby holds more than four.</summary>
        public void SetAutoKick(bool on)
        {
            autoKick = on;
            Send("autokick\t" + (on ? "1" : "0"));
        }

        /// <summary>Host: steam ids kicked the moment they join.</summary>
        public void SetBans(IEnumerable<ulong> ids)
        {
            var parts = new List<string>();
            foreach (var id in ids) parts.Add(id.ToString(CultureInfo.InvariantCulture));
            bans = string.Join(",", parts.ToArray());
            Send("bans\t" + bans);
        }

        /// <summary>Host only; the core answers with a notice.</summary>
        public bool Kick(ulong id) { return Send("kick\t" + id.ToString(CultureInfo.InvariantCulture)); }

        public int GamePid { get { var g = game; try { return g != null ? g.Id : 0; } catch { return 0; } } }

        void Loop()
        {
            while (!disposed)
            {
                try { Step(); }
                catch (Exception e) { status = "attach error: " + e.Message; Log.Write("attach loop: " + e); }
                Publish();
                Thread.Sleep(500);
            }
        }

        void Step()
        {
            if (game != null && Exited(game)) { Log.Write("game exited"); game = null; }
            if (game == null)
            {
                var found = Process.GetProcessesByName(ProcessName);
                if (found.Length == 0) { status = "waiting for green hell"; pending = false; attempts = 0; return; }
                game = found[0];
                attempts = 0; pending = false;
                Log.Write("game found");
            }
            if (link.IsConnected && trusted) { pending = false; return; }

            if (pending)
            {
                if ((DateTime.UtcNow - injectedAt).TotalSeconds < 15) { status = "starting"; return; }
                pending = false;
                Log.Write("core never connected back");
            }
            if (attempts >= MaxAttempts) { status = "could not patch · see log"; return; }

            // Give the game time to bring Mono and its own assemblies up before loading ours next to them.
            if ((DateTime.Now - game.StartTime).TotalSeconds < 10 || game.MainWindowHandle == IntPtr.Zero) { game.Refresh(); status = "game starting"; return; }
            if (Injector.FindModule(game, Injector.MonoModule) == IntPtr.Zero) { status = "waiting for mono"; return; }

            byte[] core = CoreBytes();
            if (core == null) { status = "core missing from this build"; return; }
            string name = coreFile != null && File.Exists(coreFile) ? Injector.AssemblyNameFromFile(coreFile) : Injector.EmbeddedAssemblyName();
            attempts++;
            status = "patching";
            Publish();
            string error;
            using (var injector = new Injector(game))
                error = injector.Inject(core, name, link.Port.ToString(CultureInfo.InvariantCulture) + "|" + lobbySize.ToString(CultureInfo.InvariantCulture) + "|" + token);
            if (error != null)
            {
                Log.Write("inject " + name + " failed: " + error);
                status = "could not patch · see log";
                return;
            }
            Log.Write("injected " + name + " (attempt " + attempts + ")");
            pending = true;
            injectedAt = DateTime.UtcNow;
            status = "starting";
        }

        static bool Exited(Process p) { try { return p.HasExited; } catch { return true; } }

        byte[] CoreBytes()
        {
            try { if (coreFile != null && File.Exists(coreFile)) return File.ReadAllBytes(coreFile); } catch { }
            using (var s = typeof(GameSession).Assembly.GetManifestResourceStream("tribe.core.dll"))
            {
                if (s == null) return null;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        void OnLine(string line)
        {
            var f = line.Split('\t');
            if (!trusted)
            {
                // Nothing is accepted, and nothing is sent, until the right token arrives.
                if (f[0] != "hello" || f.Length < 3 || f[2] != token) { link.DropClient(); return; }
                trusted = true;
                pending = false;
                Log.Write("core running: " + f[1]);
                SetLobbySize(lobbySize);
                SetMenu(menuShown);
                Send("autokick\t" + (autoKick ? "1" : "0"));
                Send("bans\t" + bans);
            }
            else if (f[0] == "state" && f.Length >= 5)
            {
                var s = new Snapshot { Applied = f[1] == "1", Lobby = Int(f[2]), Limit = Int(f[3]), Error = f[4], Players = last.Players };
                // Log changes, not every report.
                if (s.Applied != last.Applied || s.Lobby != last.Lobby || s.Limit != last.Limit || s.Error != last.Error)
                    Log.Write("state: patched " + s.Applied + ", lobby " + (s.Lobby == 2 ? "host" : s.Lobby == 1 ? "member" : "none") + ", limit " + s.Limit + (s.Error.Length > 0 ? ", error: " + s.Error : ""));
                last = s;
            }
            else if (f[0] == "players")
            {
                var rows = new List<PlayerRow>();
                for (int i = 1; i < f.Length; i++)
                {
                    var p = f[i].Split('|');
                    if (p.Length < 6) continue;
                    ulong id;
                    ulong.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                    rows.Add(new PlayerRow { Id = id, Name = p[1], Self = p[2] == "1", Host = p[3] == "1", Patch = Int(p[4]), Ping = Int(p[5]) });
                }
                string was = string.Join(",", Array.ConvertAll(last.Players, r => r.Name + ":" + r.Patch)), now = string.Join(",", rows.ConvertAll(r => r.Name + ":" + r.Patch).ToArray());
                if (was != now) Log.Write("players: " + (now.Length > 0 ? now : "none"));
                last = new Snapshot { Applied = last.Applied, Lobby = last.Lobby, Limit = last.Limit, Error = last.Error, Players = rows.ToArray() };
            }
            else if (f[0] == "notice" && f.Length > 1 && Notice != null) Notice(f[1]);
            Publish();
        }

        static int Int(string s) { int v; return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }

        void Publish()
        {
            bool linked = link.IsConnected && trusted;
            var l = last;
            state = new Snapshot
            {
                Running = game != null, Linked = linked, Applied = linked && l.Applied,
                Lobby = linked ? l.Lobby : 0, Limit = l.Limit, Players = linked ? l.Players : new PlayerRow[0], Error = linked ? l.Error : "",
                Status = !linked ? status : l.Error.Length > 0 ? "patch failed" : l.Applied ? "patched" : "starting",
            };
        }

        // Closing tribe.exe leaves the patch in: the game keeps it until it exits.
        public void Dispose()
        {
            disposed = true;
            link.Dispose();
        }
    }
}
