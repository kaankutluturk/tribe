using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Tribe.App
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Per-Monitor-V2 first (live DPI changes); older Windows falls back.
            bool perMonitor = false;
            try { perMonitor = Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            if (!perMonitor) try { Native.SetProcessDPIAware(); } catch { }
            float dpiScale = 1f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f; } catch { }
            Size screen; try { screen = Screen.PrimaryScreen.Bounds.Size; } catch { screen = new Size(1920, 1080); }
            Skin.InitializeScale(ComputeScale(dpiScale, screen));

            if (args.Length > 1 && args[0] == "--render")
            {
                if (args.Length > 2) Skin.InitializeScale(float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
                using (var form = new MainForm(new Settings(), null, null)) form.Render(args[1]);
                return 0;
            }

            // Dev builds run from a copy so bin\tribe.exe stays writable, and reload themselves onto new builds (DevReload).
            string origin = Application.ExecutablePath;
            bool hidden = Array.IndexOf(args, "--hidden") >= 0;
            int shadow = Array.IndexOf(args, "--shadow");
            if (shadow >= 0 && shadow + 1 < args.Length) origin = args[shadow + 1];
            else if (DevReload.IsDevTree(origin))
            {
                var copy = DevReload.Copy(origin);
                if (copy != null) { System.Diagnostics.Process.Start(copy, "--shadow \"" + origin + "\"" + (hidden ? " --hidden" : "")); return 0; }
            }
            bool dev = shadow >= 0;

            bool created;
            using (var single = new Mutex(true, @"Local\tribe-single-instance", out created))
            {
                if (!created)
                {
                    // Already running (probably hidden): bring its menu up instead of starting a second one.
                    try { using (var signal = EventWaitHandle.OpenExisting(@"Local\tribe-show-menu")) signal.Set(); } catch { }
                    return 0;
                }
                using (var reopen = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\tribe-show-menu"))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    var settings = Settings.Load();
                    // A core next to the exe wins (dev builds land in bin\); a released exe carries its own.
                    string core = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? "", "tribe.core.dll");
                    Application.Run(new MainForm(settings, reopen, new GameSession(File.Exists(core) ? core : null, settings.LobbySize)) { DevOrigin = dev ? origin : null, StartHidden = hidden });
                }
            }
            // Only after this instance has let go of the single-instance mutex: start the new build.
            if (ReloadTo != null) System.Diagnostics.Process.Start(ReloadTo, ReloadHidden ? "--hidden" : "");
            return 0;
        }

        public static string ReloadTo; public static bool ReloadHidden;   // set by MainForm when a new dev build lands

        // 1920x1080 is 1x. Windows scaling and screen resolution are independent (a 4K screen at 100% reports
        // 96 DPI), so the larger of the two scales wins rather than their product. Clamped to [1, 3].
        internal static float ComputeScale(float dpiScale, Size screenPixels)
        {
            float resolutionScale = Math.Min(screenPixels.Width / 1920f, screenPixels.Height / 1080f);
            return Math.Max(1f, Math.Min(3f, Math.Max(dpiScale, resolutionScale)));
        }
    }
}
