using System;
using System.IO;

namespace Tribe.App
{
    // Dev builds only (tribe.exe sitting in bin\ next to build.ps1): run from a copy so bin\tribe.exe is never locked and
    // every build succeeds, then reload onto a new build as soon as one lands. Reloading closes tribe exactly like
    // unload (the patch stays in the game) and starts the new build, which injects its newer core into the running
    // game. Release copies, run from anywhere else, behave as before.
    static class DevReload
    {
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tribe", "run"); } }

        public static bool IsDevTree(string exe)
        {
            try
            {
                string bin = Path.GetDirectoryName(exe), root = bin == null ? null : Path.GetDirectoryName(bin);
                return root != null && File.Exists(Path.Combine(root, "build.ps1")) && Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // A fresh copy to run from; older copies are removed when nothing is running them.
        public static string Copy(string exe)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (var old in Directory.GetFiles(Folder, "tribe-*.exe")) try { File.Delete(old); } catch { }
                string copy = Path.Combine(Folder, "tribe-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".exe");
                File.Copy(exe, copy);
                return copy;
            }
            catch { return null; }
        }

        // A newer build that has finished writing: newer than the one we started from, unchanged for a second, and
        // openable without sharing violations (csc is done with it).
        public static bool Ready(string exe, DateTime startedFrom, ref DateTime seen, ref DateTime seenAt)
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(exe);
                if (written <= startedFrom) return false;
                if (written != seen) { seen = written; seenAt = DateTime.UtcNow; return false; }
                if ((DateTime.UtcNow - seenAt).TotalMilliseconds < 1000) return false;
                using (File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                return true;
            }
            catch { return false; }
        }
    }
}
