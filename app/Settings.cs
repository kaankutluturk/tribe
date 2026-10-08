using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace Tribe.App
{
    public class Settings
    {
        public int LobbySize = 8;                    // how many players the lobby takes when you host
        public int MenuKey = 35, MenuModifiers = 0;  // end: show/hide the menu (insert and home are left free for other tools)
        public bool AutoKick = true;                 // host: remove players without tribe once the lobby holds more than four
        public List<Ban> Bans = new List<Ban>();     // host: kicked the moment they join

        public class Ban
        {
            public ulong Id;                         // steam id
            public string Name = "";                 // as they were called when banned, for the list
        }

        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tribe"); } }
        static string FilePath { get { return Path.Combine(Folder, "settings.xml"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var s = File.OpenRead(FilePath))
                    {
                        var loaded = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(s);
                        loaded.LobbySize = Rules.Clamp(loaded.LobbySize, Rules.MinLobby, Rules.MaxLobby);
                        if (loaded.MenuKey < 0 || loaded.MenuKey > 259) loaded.MenuKey = 35;
                        loaded.MenuModifiers &= 15;
                        if (loaded.Bans == null) loaded.Bans = new List<Ban>();
                        return loaded;
                    }
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                using (var s = File.Create(tmp)) new XmlSerializer(typeof(Settings)).Serialize(s, this);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch { }
        }
    }

    static class Rules
    {
        // 4 is the game's own limit, so the slider starts above it. 16 is the capacity the core patches every player's
        // game to (core\Patch.cs): a number we chose, not a limit of the game or of Steam.
        public const int MinLobby = 5, MaxLobby = 16;

        public static int Clamp(int v, int min, int max) { return Math.Max(min, Math.Min(max, v)); }
    }

    static class Log
    {
        static readonly object gate = new object();
        static string path;
        const int KeepLogs = 7;

        public static string Folder { get { return Path.Combine(Settings.Folder, "logs"); } }
        public static string CurrentFile { get { return path; } }

        public static void Write(string msg)
        {
            lock (gate)
            {
                try
                {
                    if (path == null)
                    {
                        Directory.CreateDirectory(Folder);
                        var files = new DirectoryInfo(Folder).GetFiles("tribe-*.log");
                        Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                        for (int i = KeepLogs - 1; i < files.Length; i++) try { files[i].Delete(); } catch { }
                        path = Path.Combine(Folder, "tribe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
