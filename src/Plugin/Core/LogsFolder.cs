using System;
using System.IO;

namespace PtcglAutoBattleLog.Core
{
    /// <summary>What the plugin does with the OutputFolder setting.</summary>
    public sealed class LogsFolderChoice
    {
        /// <summary>The absolute folder logs are saved to this session.</summary>
        public string Folder;
        /// <summary>Write "" back to the cfg: the value was garbled, or was the default spelled out.</summary>
        public bool ClearSetting;
        /// <summary>For the BepInEx log as a warning; null when the setting was fine.</summary>
        public string Warning;
        /// <summary>For the BepInEx log as information.</summary>
        public string Note;
    }

    /// <summary>
    /// Turns the cfg's [General] OutputFolder into the folder logs are saved to.
    ///
    /// Empty (the plugin's default value) means Documents\PTCGL Battle Logs, resolved at every
    /// start rather than written to the cfg: BepInEx 5.4.23 cannot keep every path in its cfg. It
    /// rewrites the cfg at every game start; a "C:\..." path containing ' or " comes back with a
    /// backslash before it (C:\Users\O\'Brien), and "\\server\share" comes back as "\server\share".
    /// Versions before 1.1 wrote the default folder into the cfg, so a player whose profile path has
    /// an apostrophe (C:\Users\O'Brien) has a garbled value there. Such values, and folders that
    /// cannot be created, fall back to the default folder with a warning, so a log is never lost.
    /// </summary>
    public static class LogsFolder
    {
        public const string DefaultFolderName = "PTCGL Battle Logs";

        public static string DefaultFor(string documents)
        {
            return Path.Combine(documents ?? "", DefaultFolderName);
        }

        /// <param name="configured">The OutputFolder value as BepInEx read it.</param>
        /// <param name="defaultFolder">Documents\PTCGL Battle Logs.</param>
        /// <param name="bepInExRoundTrip">BepInEx's own write + read of a string value (null: not checked).</param>
        /// <param name="tryCreate">Creates the folder; returns why it failed, or null (null: not checked).</param>
        /// <param name="baseFolder">
        /// What a relative value is relative to: the game folder, as the tray app resolves it too
        /// (GameInstall.ResolveLogsFolder). Null: the process's working directory.
        /// </param>
        public static LogsFolderChoice Choose(string configured, string defaultFolder,
                                              Func<string, string> bepInExRoundTrip, Func<string, string> tryCreate,
                                              string baseFolder = null)
        {
            var c = new LogsFolderChoice { Folder = defaultFolder };
            if (string.IsNullOrWhiteSpace(configured)) return c;
            var value = configured.Trim();

            var garbled = WhyUnkeepable(value, bepInExRoundTrip);
            if (garbled != null)
            {
                c.ClearSetting = true;
                c.Warning = "OutputFolder \"" + configured + "\" " + garbled + ". Saving to the default folder " + defaultFolder +
                            " and clearing the setting. To use another folder, pick one without ' or \" (for a network share, a mapped drive letter).";
                return c;
            }

            string full;
            try { full = Path.GetFullPath(string.IsNullOrEmpty(baseFolder) || Path.IsPathRooted(value) ? value : Path.Combine(baseFolder, value)); }
            catch (Exception e)
            {
                c.Warning = "OutputFolder \"" + configured + "\" is not a usable path (" + e.Message + "); saving to the default folder " + defaultFolder + ".";
                return c;
            }

            if (SameFolder(full, defaultFolder))
            {
                c.ClearSetting = true;
                c.Note = "OutputFolder held the default folder spelled out; cleared, so it keeps following the Documents folder.";
                return c;
            }

            var error = tryCreate == null ? null : tryCreate(full);
            if (error != null)
            {
                c.Warning = "OutputFolder \"" + full + "\" cannot be created (" + error + "); saving to the default folder " + defaultFolder + " instead.";
                return c;
            }
            c.Folder = full;
            return c;
        }

        /// <summary>Why BepInEx has garbled, or will garble, this value; null when it keeps it.</summary>
        internal static string WhyUnkeepable(string value, Func<string, string> bepInExRoundTrip)
        {
            // What BepInEx left of C:\Users\O'Brien (C:\Users\O\'Brien) or of a path with '"' in it,
            // which no Windows path has.
            if (value.IndexOf('"') >= 0 || value.IndexOf("\\'", StringComparison.Ordinal) >= 0)
                return "was garbled by BepInEx, which cannot keep ' or \" in a folder path";
            // What BepInEx left of \\server\share: rooted on whatever drive the game runs from (and
            // "\a", "\t"... in the rest of it turned into control characters).
            if (value.StartsWith("\\", StringComparison.Ordinal) && !value.StartsWith("\\\\", StringComparison.Ordinal))
                return "looks like a network path (\\\\server\\share) garbled by BepInEx";
            foreach (var ch in value)
                if (ch < ' ') return "has control characters, as BepInEx leaves a path it garbled";
            if (bepInExRoundTrip == null) return null;
            string back;
            try { back = bepInExRoundTrip(value); }
            catch (Exception) { return null; }
            return back == null || back == value ? null : "cannot be kept by BepInEx 5 (its next start would read it as \"" + back + "\")";
        }

        private static bool SameFolder(string a, string b)
        {
            try
            {
                var sep = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
                return string.Equals(Path.GetFullPath(a).TrimEnd(sep), Path.GetFullPath(b).TrimEnd(sep), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }
    }
}
