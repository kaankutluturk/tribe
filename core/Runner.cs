using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Tribe
{
    // Applies the patch once, then keeps it maintained (new co-op huds, the host's lobby limit, the player list) once
    // a second for as long as the game runs. tribe.exe is optional after that: the link carries status to its window
    // and the host's choices back, and the runner carries on when it closes.
    //   exe -> core   limit<TAB>n   menu<TAB>0|1   kick<TAB>steam id   autokick<TAB>0|1   bans<TAB>id,id,...
    //   core -> exe   hello<TAB>assembly<TAB>token
    //                 state<TAB>applied<TAB>lobby (0 none, 1 member, 2 owner)<TAB>limit<TAB>error
    //                 players<TAB>id|name|self|host|patch (0 no, 1 yes, 2 checking)|ping ms or -1<TAB>...
    //                 notice<TAB>text
    sealed class Runner : MonoBehaviour
    {
        Link link;
        float nextMaintain, toastUntil;
        bool hudDue = true, maintainWarned, menu;
        int unpatchedWas;
        string toastText = "";
        GUIStyle toast;

        public void Attach(int port, string token)
        {
            if (link != null) link.Close();
            link = new Link(port);
            Patch.Apply();
            // The token came in with the injection; echoing it is how tribe.exe knows this connection is its own core.
            link.Send("hello\t" + typeof(Runner).Assembly.GetName().Name + "\t" + token);
            Toast(Patch.Applied ? "tribe · player limit lifted" : "tribe · patch failed: " + Patch.Error, 4f);
        }

        void Awake() { SceneManager.sceneLoaded += OnSceneLoaded; }
        void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; MenuHold.Release(); if (link != null) link.Close(); }
        void OnSceneLoaded(Scene scene, LoadSceneMode mode) { hudDue = true; }

        void Toast(string text, float seconds) { toastText = text; toastUntil = Time.unscaledTime + seconds; }

        void Update()
        {
            string line;
            while (link != null && link.TryReceive(out line))
            {
                try { Handle(line.Split('\t')); }
                catch (Exception e) { Debug.LogWarning("[tribe] a command failed: " + e.Message); }
            }
            // tribe.exe went away: the patch stays, but its menu can't be up any more.
            if (link != null && link.Closed) { link = null; menu = false; }

            // While the menu is up the game frees the mouse so it can be clicked without pausing.
            if (menu) MenuHold.Hold(); else MenuHold.Release();

            try { Players.Pump(); } catch { }

            if (Time.unscaledTime < nextMaintain) return;
            nextMaintain = Time.unscaledTime + 1f;
            try
            {
                if (Patch.Applied && hudDue) { hudDue = false; Patch.GrowHud(); }
                Patch.Maintain();
                // The host is playing, not watching a window: say it in the game when someone turns up unpatched
                // (held lobby) or is removed (auto-kick, ban).
                if (Patch.Lobby == 2 && Patch.Unpatched.Count > unpatchedWas && !(Patch.AutoKick && Patch.Members > Patch.Vanilla))
                    Toast(Patch.Unpatched[Patch.Unpatched.Count - 1] + " has no tribe · lobby held at " + Patch.Vanilla, 6f);
                unpatchedWas = Patch.Unpatched.Count;
                foreach (var what in Patch.Events) { Toast(what, 6f); if (link != null) link.Send("notice\t" + Clean(what)); }
                Patch.Events.Clear();
            }
            catch (Exception e) { if (!maintainWarned) { maintainWarned = true; Debug.LogError("[tribe] maintain failed: " + e); } }
            if (link != null) { link.Send(State()); link.Send(PlayerList()); }
        }

        void Handle(string[] f)
        {
            if (f.Length < 2) return;
            switch (f[0])
            {
                case "limit":
                    Patch.Wanted = Mathf.Clamp(int.Parse(f[1], CultureInfo.InvariantCulture), Patch.Vanilla, Patch.Capacity);
                    break;
                case "menu":
                    menu = f[1] == "1";
                    break;
                case "autokick":
                    Patch.AutoKick = f[1] == "1";
                    break;
                case "bans":
                    Patch.Bans.Clear();
                    foreach (var part in f[1].Split(','))
                    {
                        ulong id;
                        if (ulong.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) Patch.Bans.Add(id);
                    }
                    break;
                case "kick":
                    string error = Players.Kick(ulong.Parse(f[1], CultureInfo.InvariantCulture));
                    if (link != null) link.Send("notice\t" + (error == null ? "kicked" : "kick failed: " + error));
                    break;
            }
        }

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('|', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        static string State()
        {
            return "state\t" + (Patch.Applied ? "1" : "0") + "\t" + Patch.Lobby + "\t" + Patch.Limit + "\t" + Clean(Patch.Error);
        }

        static string PlayerList()
        {
            var sb = new StringBuilder("players");
            foreach (var r in Patch.Rows)
                sb.Append('\t').Append(r.Id).Append('|').Append(Clean(r.Name)).Append('|').Append(r.Self ? '1' : '0').Append('|').Append(r.Host ? '1' : '0')
                  .Append('|').Append(r.Patch).Append('|').Append(r.Ping);
            return sb.ToString();
        }

        // A few seconds of text in the game itself: the patch took, or someone joined without it.
        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || Time.unscaledTime >= toastUntil) return;
            if (toast == null) toast = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(13 * Mathf.Max(1f, Screen.height / 1080f)) };
            var size = toast.CalcSize(new GUIContent(toastText));
            var r = new Rect((Screen.width - size.x) / 2 - 12, 24, size.x + 24, size.y + 10);
            var old = GUI.color;
            GUI.color = new Color(0.035f, 0.05f, 0.04f, 0.92f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
            bool bad = !Patch.Applied || toastText.Contains("no tribe") || toastText.Contains("banned") || toastText.Contains("could not");
            toast.normal.textColor = bad ? new Color(0.93f, 0.35f, 0.28f) : new Color(0.91f, 0.69f, 0.28f);
            GUI.Label(r, toastText, toast);
        }
    }
}
