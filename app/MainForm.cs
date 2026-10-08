using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Tribe.App
{
    // The menu: borderless, topmost 480x274, shown on the taskbar while open and kept in the tray while hidden,
    // with tabs and a one-line status footer. While it is up the core has the game free the mouse, so it
    // can be clicked without pausing. "unload" closes tribe; the patch itself stays in the game until the game exits.
    //
    // The role follows the game, nobody picks it: in someone else's lobby you are a guest and the lobby tab (size
    // slider, auto-kick) isn't there, nor are kick and ban; hosting, or not in a lobby yet, you get everything.
    // State is said once, in the footer; the tabs don't repeat it.
    sealed class MainForm : Form
    {
        const int Lobby = 0, Players = 1, Misc = 2, PageCount = 3;
        const int TabsX = 190;
        const int MaxRows = 16, RowsPerColumn = 8, MaxBans = 8;
        const int WM_DPICHANGED = 0x02E0;

        readonly Settings settings;
        readonly GameSession session;
        readonly EventWaitHandle reopen;
        readonly Panel[] pages = new Panel[PageCount];
        readonly FooterBar footer = new FooterBar();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        MenuTabs tabs;
        bool guest;
        int page;
        KeyMonitor keys;
        IntPtr previous;
        bool closing;
        NotifyTray tray;
        string notice = "";
        DateTime noticeUntil;
        Label noPlayers, noBans;
        readonly Label[] rowName = new Label[MaxRows], rowPing = new Label[MaxRows], banName = new Label[MaxBans];
        readonly MicroButton[] rowKick = new MicroButton[MaxRows], rowBan = new MicroButton[MaxRows], banLift = new MicroButton[MaxBans];
        readonly ulong[] rowId = new ulong[MaxRows];
        readonly string[] rowLabel = new string[MaxRows];
        string rowsSignature = "", bansSignature = "?";
        int confirm = -1;            // row * 2 + (ban ? 1 : 0), armed by the first click
        DateTime confirmUntil;

        public MainForm(Settings s, EventWaitHandle showSignal, GameSession game)
        {
            settings = s;
            reopen = showSignal;
            session = game;
            Text = "tribe"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = session != null; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(480, 274); BackColor = Skin.Background; ForeColor = Skin.Text; Font = Skin.Font; DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (session != null)
            {
                try { keys = new KeyMonitor(); keys.Watch(settings.MenuKey); keys.Pressed += OnBinding; } catch (Exception e) { Notice(e.Message); }
                session.Notice = text => { try { BeginInvoke(new Action(() => Notice(text))); } catch { } };
                session.SetAutoKick(settings.AutoKick);
                session.SetBans(BanIds());
            }

            MakeTabs();
            Controls.Add(footer);
            footer.MouseDown += (o, e) => DragMenu(e);
            BuildAll();
            if (session != null)
            {
                try { tray = new NotifyTray(() => ToggleMenu(true), Close); tray.ToggleText = Visible ? "hide menu" : "show menu"; }
                catch (Exception e) { Log.Write("tray unavailable: " + e.Message); }
            }

            timer.Tick += (o, e) => Tick();
            Load += (o, e) =>
            {
                timer.Start();
                if (session == null) return;
                Log.Write("tribe " + Application.ProductVersion + " opened");
                session.Start();
                if (StartHidden) BeginInvoke(new Action(() => HideMenu(false)));   // a dev reload keeps a hidden menu hidden
                else session.SetMenu(true);                       // it opens shown
            };
            FormClosing += (o, e) => Shutdown();
        }

        // Keep this style for the form's lifetime; Hide removes its taskbar button without recreating the handle.
        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                if (ShowInTaskbar)
                {
                    p.ExStyle = (p.ExStyle & ~Native.WS_EX_TOOLWINDOW) | Native.WS_EX_APPWINDOW;
                    p.Style |= Native.WS_MINIMIZEBOX;
                }
                else p.ExStyle = (p.ExStyle & ~Native.WS_EX_APPWINDOW) | Native.WS_EX_TOOLWINDOW;
                p.Style &= ~Native.WS_MAXIMIZEBOX;
                return p;
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (tray != null) tray.ToggleText = Visible ? "hide menu" : "show menu";
        }

        void DisposeTray()
        {
            var gone = tray;
            tray = null;
            if (gone != null) gone.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeTray();
            base.Dispose(disposing);
        }

        Snapshot preview;   // --render only: stands in for the session's state
        Snapshot State { get { return preview != null ? preview : session == null ? Snapshot.Waiting : session.State; } }

        // ---- tabs follow the role --------------------------------------------------------------------------------

        void MakeTabs()
        {
            if (tabs != null) { Controls.Remove(tabs); tabs.Dispose(); }
            tabs = guest ? new MenuTabs("players", "misc") : new MenuTabs("lobby", "players", "misc");
            tabs.SetBounds(TabsX, 5, 465 - TabsX, 40);
            tabs.Change = index => SelectPage(guest ? index + 1 : index);
            Controls.Add(tabs);
        }

        void SetRole(bool isGuest)
        {
            if (guest == isGuest) return;
            guest = isGuest;
            if (guest && page == Lobby) page = Players;
            MakeTabs();
            RebuildUI(Skin.Scale);
        }

        void BuildAll()
        {
            for (int i = 0; i < PageCount; i++)
            {
                if (pages[i] != null) { Controls.Remove(pages[i]); pages[i].Dispose(); }
                pages[i] = new Panel { Location = new Point(16, 57), Size = new Size(448, 179), BackColor = Skin.Background, Visible = i == page };
                Controls.Add(pages[i]);
            }
            footer.SetBounds(18, 245, 444, 17);
            rowsSignature = ""; bansSignature = "?";
            BuildLobby(); BuildPlayers(); BuildMisc();
            tabs.Selected = guest ? page - 1 : page;
            if (Skin.Scale != 1f) Scale(new SizeF(Skin.Scale, Skin.Scale));
            UpdateText();
        }

        const int WM_SYSCOMMAND = 0x0112, WM_SIZE = 0x0005;
        const int SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SIZE_MINIMIZED = 1, SIZE_MAXIMIZED = 2;

        // Taskbar minimize and direct shell minimize both become a hide, with the tray kept.
        void SettleWindow()
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            if (Native.IsZoomed(Handle)) Native.ShowWindow(Handle, Native.SW_RESTORE);
            if (!Native.IsIconic(Handle)) return;
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            HideMenu(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && ((long)m.WParam & 0xFFF0) == SC_MINIMIZE)
            {
                if (!closing) HideMenu();
                return;
            }
            if (m.Msg == WM_SYSCOMMAND && ((long)m.WParam & 0xFFF0) == SC_MAXIMIZE) return;
            if (m.Msg == WM_SIZE && ((int)m.WParam == SIZE_MINIMIZED || (int)m.WParam == SIZE_MAXIMIZED))
            {
                base.WndProc(ref m);
                if (!closing && !IsDisposed && IsHandleCreated) BeginInvoke(new Action(SettleWindow));
                return;
            }
            if (m.Msg == WM_DPICHANGED)
            {
                float dpiScale = ((int)((long)m.WParam & 0xFFFF)) / 96f;
                var r = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                var target = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                Size screen; try { screen = Screen.FromRectangle(target).Bounds.Size; } catch { screen = new Size(1920, 1080); }
                float scale = Program.ComputeScale(dpiScale, screen);
                if (Math.Abs(scale - Skin.Scale) > 0.001f) RebuildUI(scale);
                Location = new Point(r.Left, r.Top);
            }
            base.WndProc(ref m);
        }

        // Rescaling an already scaled control tree drifts from rounding, so the tree is rebuilt from the
        // 480x274 baseline instead.
        void RebuildUI(float scale)
        {
            Skin.InitializeScale(scale);
            if (tray != null) tray.MenuFont = Skin.Font;
            ClientSize = new Size(480, 274); Font = Skin.Font;
            tabs.SetBounds(TabsX, 5, 465 - TabsX, 40);
            BuildAll();
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int i3 = Skin.D(3), i7 = Skin.D(7), i10 = Skin.D(10), i50 = Skin.D(50), i20 = Skin.D(20), i88 = Skin.D(88), i5 = Skin.D(5), i11 = Skin.D(11);
            using (var p = new Pen(Color.Black)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var p = new Pen(Skin.Border)) { g.DrawRectangle(p, i3, i3, Width - i7, Height - i7); g.DrawRectangle(p, i10, i50, Width - i20, Height - i88); }
            using (var p = new Pen(Skin.Inner)) g.DrawRectangle(p, i5, i5, Width - i11, Height - i11);
            var flags = TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, "tribe", Skin.Logo, new Point(Skin.D(40), Skin.D(17)), Color.White, flags);
        }

        void DragMenu(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.X < Skin.D(TabsX) && e.Y < Skin.D(50)) DragMenu(e); base.OnMouseDown(e); }

        void SelectPage(int index)
        {
            if (index < 0 || index >= PageCount) return;
            page = index;
            tabs.Selected = guest ? page - 1 : page; tabs.Invalidate();
            for (int i = 0; i < PageCount; i++) pages[i].Visible = i == page;
            UpdateText();
            Invalidate(true);
        }

        // ---- pages -----------------------------------------------------------------------------------------------

        FieldGroup Group(Control parent, string title, int x, int y, int w, int h) { var g = new FieldGroup(title, x, y, w, h); parent.Controls.Add(g); return g; }

        Label Label(Control parent, string text, int x, int y, int w, int h = 18)
        {
            var l = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = Skin.Muted, Font = Skin.Font, BackColor = Color.Transparent, AutoEllipsis = true };
            parent.Controls.Add(l);
            return l;
        }

        MicroButton Button(Control parent, string text, int x, int y, int w, Action action, int h = 24)
        {
            var b = new MicroButton(text, () => { try { action(); } catch (Exception e) { Log.Write("'" + text + "' failed: " + e.Message); Notice(e.Message); } }) { Location = new Point(x, y), Size = new Size(w, h) };
            parent.Controls.Add(b);
            return b;
        }

        // Host (or about to be): how big the lobby is, and what happens to players who don't have tribe.
        void BuildLobby()
        {
            var size = Group(pages[Lobby], "lobby size", 0, 0, 448, 56);
            var slider = new MicroSlider("players", Rules.MinLobby, Rules.MaxLobby, 1, () => settings.LobbySize, v =>
            {
                // Open lobbies follow the slider live, so it can grow mid-session. It can't go under the people
                // already in: "8/5" would read as someone about to be thrown out.
                var s = State;
                int floor = s.Lobby == 2 ? Math.Max(Rules.MinLobby, s.Players.Length) : Rules.MinLobby;
                int wanted = Rules.Clamp((int)v, Rules.MinLobby, Rules.MaxLobby);
                if (wanted < floor) { wanted = floor; Notice(s.Players.Length + " players are in"); }
                settings.LobbySize = wanted;
                settings.Save();
                if (session != null) session.SetLobbySize(settings.LobbySize);
                UpdateText();
            }) { Location = new Point(12, 18), Width = 424 };
            size.Controls.Add(slider);

            var guard = Group(pages[Lobby], "without tribe", 0, 62, 448, 46);
            var check = new MicroCheck("auto-kick above 4 players", () => settings.AutoKick, v =>
            {
                settings.AutoKick = v;
                settings.Save();
                if (session != null) session.SetAutoKick(v);
            }) { Location = new Point(14, 20), Width = 300 };
            guard.Controls.Add(check);
        }

        // Two columns of eight: sixteen players without scrolling. The name's colour is the patch state (white
        // patched, red no tribe, grey not known yet, accent you), so there is no separate status column.
        void BuildPlayers()
        {
            var list = Group(pages[Players], "players", 0, 0, 448, 174);
            noPlayers = Label(list, "", 14, 24, 420);
            for (int i = 0; i < MaxRows; i++)
            {
                int x = (i / RowsPerColumn) * 220, y = 22 + (i % RowsPerColumn) * 18, row = i;
                rowName[i] = Label(list, "", x + 14, y, 82, 17);
                rowPing[i] = Label(list, "", x + 96, y, 50, 17);
                rowPing[i].TextAlign = ContentAlignment.TopRight;
                rowKick[i] = Button(list, "kick", x + 150, y - 2, 32, () => Arm(row, false), 18);
                rowBan[i] = Button(list, "ban", x + 184, y - 2, 30, () => Arm(row, true), 18);
            }
        }

        void BuildMisc()
        {
            var binds = Group(pages[Misc], "binding", 0, 0, 218, 60);
            Label(binds, "menu", 14, 29, 87);
            binds.Controls.Add(new BindingEditor(() => settings.MenuKey, () => settings.MenuModifiers, (k, m) => { settings.MenuKey = k; settings.MenuModifiers = m; if (keys != null) keys.Watch(k); settings.Save(); UpdateText(); }, keys) { Location = new Point(105, 24), Width = 99 });

            var app = Group(pages[Misc], "app", 230, 0, 218, 60);
            Button(app, "logs", 14, 24, 92, () => { Directory.CreateDirectory(Log.Folder); Process.Start(Log.Folder); });
            Button(app, "unload", 112, 24, 92, Close);

            var banned = Group(pages[Misc], "banned", 0, 66, 448, 108);
            banned.Visible = !guest;   // the ban list is the host's; a guest has no use for it
            noBans = Label(banned, "none", 14, 24, 200);
            for (int i = 0; i < MaxBans; i++)
            {
                int x = (i / 4) * 220, y = 22 + (i % 4) * 20, slot = i;
                banName[i] = Label(banned, "", x + 14, y, 138, 17);
                banName[i].ForeColor = Skin.Text;
                banLift[i] = Button(banned, "unban", x + 158, y - 2, 52, () => Unban(slot), 18);
            }
        }

        // ---- players, kick, ban --------------------------------------------------------------------------------------

        IEnumerable<ulong> BanIds() { foreach (var b in settings.Bans) yield return b.Id; }

        void UpdateRows(Snapshot s)
        {
            if (noPlayers == null) return;
            bool host = s.Lobby == 2;
            if (confirm >= 0 && DateTime.UtcNow > confirmUntil) confirm = -1;
            var sig = new System.Text.StringBuilder(s.Lobby + "/" + confirm);
            foreach (var p in s.Players) sig.Append('|').Append(p.Id).Append(',').Append(p.Name).Append(',').Append(p.Patch).Append(',').Append(p.Ping).Append(',').Append(p.Host);
            if (sig.ToString() != rowsSignature)
            {
                rowsSignature = sig.ToString();
                string empty = s.Players.Length == 0 ? "no lobby" : "";
                if (noPlayers.Text != empty) noPlayers.Text = empty;
                noPlayers.Visible = empty.Length > 0;
                for (int i = 0; i < MaxRows; i++)
                {
                    bool shown = i < s.Players.Length;
                    rowName[i].Visible = rowPing[i].Visible = shown;
                    rowKick[i].Visible = rowBan[i].Visible = shown && host && !s.Players[i].Self;
                    if (!shown) continue;
                    var p = s.Players[i];
                    rowId[i] = p.Id; rowLabel[i] = p.Name;
                    Set(rowName[i], p.Name, p.Patch == 0 ? Skin.Bad : p.Patch == 2 ? Skin.Muted : p.Self ? Skin.Accent : Skin.Text);
                    Set(rowPing[i], p.Host ? "host" : p.Ping >= 0 ? p.Ping + " ms" : p.Patch == 0 ? "no tribe" : "-",
                        p.Host ? Skin.Muted : p.Ping < 0 ? (p.Patch == 0 ? Skin.Bad : Skin.Muted) : PingColour(p.Ping));
                    Caption(rowKick[i], confirm == i * 2 ? "ok?" : "kick");
                    Caption(rowBan[i], confirm == i * 2 + 1 ? "ok?" : "ban");
                }
            }

            var bans = new System.Text.StringBuilder();
            foreach (var b in settings.Bans) bans.Append(b.Id).Append(',').Append(b.Name).Append('|');
            if (noBans == null || bans.ToString() == bansSignature) return;
            bansSignature = bans.ToString();
            noBans.Visible = settings.Bans.Count == 0;
            for (int i = 0; i < MaxBans; i++)
            {
                bool shown = i < settings.Bans.Count;
                banName[i].Visible = banLift[i].Visible = shown;
                if (shown && banName[i].Text != settings.Bans[i].Name) banName[i].Text = settings.Bans[i].Name;
            }
        }

        static Color PingColour(int ms) { return ms < 80 ? Skin.Good : ms < 160 ? Skin.Warn : Skin.Bad; }

        static void Caption(MicroButton b, string text) { if (b.Text != text) { b.Text = text; b.Invalidate(); } }

        // Kick and ban take two clicks: the first arms the button for three seconds.
        void Arm(int row, bool ban)
        {
            if (session == null) return;
            int key = row * 2 + (ban ? 1 : 0);
            if (confirm != key) { confirm = key; confirmUntil = DateTime.UtcNow.AddSeconds(3); UpdateText(); return; }
            confirm = -1;
            if (ban)
            {
                if (!settings.Bans.Exists(b => b.Id == rowId[row])) settings.Bans.Add(new Settings.Ban { Id = rowId[row], Name = rowLabel[row] ?? "" });
                settings.Save();
                session.SetBans(BanIds());   // the core removes them at once, and again whenever they come back
                Notice("banned " + rowLabel[row]);
            }
            else if (!session.Kick(rowId[row])) Notice("not attached");
            UpdateText();
        }

        void Unban(int slot)
        {
            if (slot >= settings.Bans.Count) return;
            string name = settings.Bans[slot].Name;
            settings.Bans.RemoveAt(slot);
            settings.Save();
            if (session != null) session.SetBans(BanIds());
            Notice("unbanned " + name);
        }

        // ---- menu key ----------------------------------------------------------------------------------------------

        static bool Matches(int assigned, int modifiers, int key, int observed) { return assigned != 0 && assigned == key && modifiers == observed; }

        void OnBinding(int key, int mods, Point point)
        {
            if (closing || BindingEditor.Capturing) return;
            if (!Matches(settings.MenuKey, settings.MenuModifiers, key, mods)) return;
            BeginInvoke(new Action(() =>
            {
                if (closing || BindingEditor.Capturing) return;
                ToggleMenu();
            }));
        }

        void ToggleMenu(bool fromTray = false)
        {
            if (closing || IsDisposed) return;
            if (Visible) HideMenu(); else ShowMenu(fromTray);
        }

        void HideMenu(bool giveFocusBack = true)
        {
            if (closing || IsDisposed) return;
            Hide();
            if (session != null) session.SetMenu(false);
            // Back to the game, which takes the mouse again now that nothing requests the cursor.
            if (giveFocusBack && Native.IsWindow(previous)) Native.SetForegroundWindow(previous);
        }

        void ShowMenu(bool fromTray = false)
        {
            if (closing || IsDisposed) return;
            UpdateText();
            // A repeated show request must not replace the return window with this menu's own handle.
            if (!Visible) previous = fromTray ? IntPtr.Zero : Native.GetForegroundWindow();
            var area = (fromTray ? Screen.FromPoint(Cursor.Position) : Screen.FromHandle(previous)).WorkingArea;
            if (IsHandleCreated && (Native.IsIconic(Handle) || Native.IsZoomed(Handle)))
                Native.ShowWindow(Handle, Native.SW_RESTORE);
            if (!area.IntersectsWith(Bounds))
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            if (session != null) session.SetMenu(true);
            // Windows may refuse the focus change; the freed cursor still lets a click bring the menu forward.
            Show(); Activate(); Native.SetForegroundWindow(Handle);
        }

        // Dev reload (DevReload.cs): the bin\tribe.exe this copy was started from, and when that build was written.
        public string DevOrigin;
        public bool StartHidden;
        DateTime devBuilt, devSeen, devSeenAt;

        void Tick()
        {
            if (reopen != null && reopen.WaitOne(0)) ShowMenu();   // tribe.exe was started again: come back up
            if (DevOrigin != null && !closing)
            {
                if (devBuilt == DateTime.MinValue) try { devBuilt = File.GetLastWriteTimeUtc(DevOrigin); } catch { }
                if (DevReload.Ready(DevOrigin, devBuilt, ref devSeen, ref devSeenAt))
                {
                    Log.Write("dev reload: new build");
                    Program.ReloadTo = DevOrigin; Program.ReloadHidden = !Visible;
                    Close();   // the patch stays; the new build injects its own core, which retires the old one
                    return;
                }
            }
            if (Visible) UpdateText();
        }

        // ---- footer ------------------------------------------------------------------------------------------------

        void Notice(string message) { notice = message; noticeUntil = DateTime.UtcNow.AddSeconds(6); UpdateText(); }

        // A state word only until there are numbers:
        //   waiting for green hell -> patching -> patched (no lobby) -> hosting 5/8 | guest 5/8 · 34 ms
        // then anything wrong in red, then the passing notice.
        void UpdateText()
        {
            if (noPlayers == null) return;
            var s = State;
            SetRole(s.Lobby == 1);
            UpdateRows(s);

            var parts = new List<FooterBar.Part>();
            int count = s.Players.Length, unpatched = s.Unpatched;
            if (s.Error.Length > 0) parts.Add(new FooterBar.Part("patch failed · see logs", Skin.Bad));
            else if (!s.Applied) parts.Add(new FooterBar.Part(s.Status, Skin.Muted));
            else if (s.Lobby == 0) parts.Add(new FooterBar.Part("patched", Skin.Good));
            else
            {
                // Held: someone without tribe is in, so the real limit is the game's own four for now. The count alone
                // reads better than "4/4" next to a slider that says 8.
                bool held = s.Lobby == 2 && unpatched > 0 && s.Limit <= 4;
                parts.Add(new FooterBar.Part((s.Lobby == 2 ? "hosting " : "guest ") + count + (held ? "" : "/" + s.Limit), Skin.Text));
                if (s.Lobby == 1)
                    foreach (var p in s.Players) if (p.Self && p.Ping >= 0) parts.Add(new FooterBar.Part(p.Ping + " ms", PingColour(p.Ping)));
                if (unpatched > 0) parts.Add(new FooterBar.Part(unpatched + " without tribe" + (held ? ", held at 4" : ""), Skin.Bad));
            }
            if (DateTime.UtcNow < noticeUntil) parts.Add(new FooterBar.Part(notice, Skin.Accent));
            footer.SetParts(parts);
        }

        static void Set(Label l, string text, Color colour)
        {
            if (l.Text != text) l.Text = text;
            if (l.ForeColor != colour) l.ForeColor = colour;
        }

        // ---- --render ----------------------------------------------------------------------------------------------

        // Saves every state the menu can be in as an image, with made-up lobbies, so everything that only appears in
        // some situation can be looked at without a game.
        public void Render(string directory)
        {
            Directory.CreateDirectory(directory);
            Opacity = 0; Show();
            var clean = new[]
            {
                new PlayerRow { Id = 1, Name = "you", Self = true, Host = true, Patch = 1 },
                new PlayerRow { Id = 2, Name = "mia", Patch = 1, Ping = 34 },
                new PlayerRow { Id = 3, Name = "jake", Patch = 1, Ping = 112 },
                new PlayerRow { Id = 4, Name = "someone with a long name", Patch = 1, Ping = 240 },
                new PlayerRow { Id = 5, Name = "newcomer", Patch = 2 },
            };
            var small = new[] { clean[0], clean[1], clean[2], new PlayerRow { Id = 6, Name = "vanilla", Patch = 0 } };
            var asGuest = new List<PlayerRow>();
            foreach (var p in clean) asGuest.Add(new PlayerRow { Id = p.Id, Name = p.Name, Host = p.Host, Patch = 1, Ping = p.Host ? -1 : p.Ping < 0 ? 58 : p.Ping, Self = p.Id == 2 });
            var full = new List<PlayerRow>();
            for (int i = 0; i < MaxRows; i++) full.Add(new PlayerRow { Id = (ulong)(i + 1), Name = i == 0 ? "you" : "player " + (i + 1), Self = i == 0, Host = i == 0, Patch = 1, Ping = i == 0 ? -1 : 20 + i * 13 });
            Func<int, int, PlayerRow[], Snapshot> lobby = (role, limit, rows) => new Snapshot { Status = "patched", Running = true, Linked = true, Applied = true, Lobby = role, Limit = limit, Players = rows };

            Snap(directory, "01 waiting for game", Lobby, null, null);
            Snap(directory, "02 patching", Lobby, new Snapshot { Status = "patching", Running = true }, null);
            Snap(directory, "03 patched, no lobby", Lobby, lobby(0, 0, new PlayerRow[0]), null);
            Snap(directory, "04 patch failed", Lobby, new Snapshot { Status = "patch failed", Running = true, Linked = true, Error = "x" }, null);
            Snap(directory, "05 hosting, lobby tab", Lobby, lobby(2, 8, clean), null);
            // Eight are in and the host dragged the slider down: it stops at 8 and the footer says why for a few seconds.
            var eight = full.GetRange(0, 8).ToArray();
            Snap(directory, "06 slider dragged below 8 players", Lobby, lobby(2, 8, eight), "8 players are in");
            Snap(directory, "07 hosting, players", Players, lobby(2, 8, clean), null);
            confirm = 2 * 2; confirmUntil = DateTime.UtcNow.AddMinutes(1);
            Snap(directory, "08 kick armed", Players, lobby(2, 8, clean), null);
            confirm = 2 * 2 + 1;
            Snap(directory, "09 ban armed", Players, lobby(2, 8, clean), null);
            confirm = -1;
            Snap(directory, "10 held at 4 (no tribe, 4 or fewer)", Players, lobby(2, 4, small), null);
            Snap(directory, "11 auto-kick notice", Players, lobby(2, 8, clean), "vanilla has no tribe · kicked");
            Snap(directory, "12 hosting 16", Players, lobby(2, 16, full.ToArray()), null);
            Snap(directory, "13 guest, players", Players, lobby(1, 8, asGuest.ToArray()), null);
            Snap(directory, "14 guest, misc", Misc, lobby(1, 8, asGuest.ToArray()), null);
            Snap(directory, "15 misc, empty ban list", Misc, lobby(2, 8, clean), null);
            string[] sampleBanNames = new[] { "griefer", "former guest", "red fox", "quiet river", "someone else", "late joiner", "unknown player", "test player" };
            for (int i = 0; i < sampleBanNames.Length; i++)
                settings.Bans.Add(new Settings.Ban { Id = (ulong)(7 + i), Name = sampleBanNames[i] });
            Snap(directory, "16 misc, full ban list", Misc, lobby(2, 8, clean), null);
            settings.Bans.Clear();
            preview = null;
            Hide();
        }

        void Snap(string directory, string name, int index, Snapshot state, string note)
        {
            preview = state ?? Snapshot.Waiting;
            notice = note ?? ""; noticeUntil = note != null ? DateTime.UtcNow.AddMinutes(1) : DateTime.MinValue;
            rowsSignature = "?"; bansSignature = "?";
            UpdateText();          // may change the role, and with it the tabs
            SelectPage(guest && index == Lobby ? Players : index);
            Application.DoEvents();
            using (var b = new Bitmap(Width, Height)) { DrawToBitmap(b, ClientRectangle); b.Save(Path.Combine(directory, name + ".png")); }
        }

        void Shutdown()
        {
            if (closing) return;
            closing = true;
            try
            {
                timer.Stop();
                if (session != null) { session.SetMenu(false); session.Dispose(); Log.Write("unloaded (the patch stays in the game)"); }
                if (keys != null) keys.Dispose();
                settings.Save();
            }
            finally { DisposeTray(); }
        }
    }

    // One-line status with coloured segments.
    sealed class FooterBar : Control
    {
        public struct Part
        {
            public readonly string Text; public readonly Color Colour;
            public Part(string text, Color colour) { Text = text; Colour = colour; }
        }

        List<Part> parts = new List<Part>();
        string signature = "";

        public FooterBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Skin.Background; }

        public void SetParts(List<Part> next)
        {
            var sig = new System.Text.StringBuilder();
            foreach (var p in next) sig.Append(p.Text).Append('\u0001').Append(p.Colour.ToArgb()).Append('\u0002');
            if (sig.ToString() == signature) return;
            signature = sig.ToString();
            parts = next;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Skin.Background);
            const TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            int x = 0;
            for (int i = 0; i < parts.Count && x < Width; i++)
            {
                if (i > 0) x = Draw(g, " · ", Skin.Muted, x, f);
                x = Draw(g, parts[i].Text, parts[i].Colour, x, f);
            }
        }

        int Draw(Graphics g, string text, Color colour, int x, TextFormatFlags f)
        {
            TextRenderer.DrawText(g, text, Skin.Font, new Rectangle(x, 0, Width - x, Height), colour, f | TextFormatFlags.EndEllipsis);
            return x + TextRenderer.MeasureText(g, text, Skin.Font, Size.Empty, f).Width;
        }
    }
}
