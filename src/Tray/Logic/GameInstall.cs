using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// The plugin's footprint in the game folder: what is missing, putting it back, and removing it.
    ///
    /// Game updates can delete BepInEx's loader files, so the daemon calls Missing/Repair over and
    /// over. Both are therefore cheap and idempotent, and Repair never overwrites a BepInEx file
    /// that is already there: the player may have their own BepInEx and other mods.
    /// </summary>
    public sealed class GameInstall
    {
        public const string PluginFolderName = "PtcglAutoBattleLog";
        public const string PluginFileName = "PtcglAutoBattleLog.Plugin.dll";
        public const string LegacyPluginFileName = "PtcglAutoBattleLog.dll";
        public const string ConfigFileName = "ptcgl.autobattlelog.cfg";

        internal const string ConfigSection = "General";
        internal const string OutputFolderKey = "OutputFolder";
        internal const string MetadataSection = "Metadata";
        internal const string SealModeKey = "Mode";

        /// <summary>[Metadata] Mode values, in menu order. The plugin's default is the first.</summary>
        public static readonly IReadOnlyList<string> SealModes = new[] { "Footer", "Sidecar", "Off" };
        public const string DefaultSealMode = "Footer";

        // Top-level files of the BepInEx 5 win x64 zip, removed with BepInEx on uninstall.
        private static readonly string[] BepInExRootFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };

        private static readonly char[] WindowsInvalidNameChars =
            "<>:\"|?*".Concat(Enumerable.Range(0, 32).Select(i => (char)i)).ToArray();

        public GameInstall(string gameDir)
        {
            if (string.IsNullOrWhiteSpace(gameDir)) throw new ArgumentException("Game folder is required.", nameof(gameDir));
            GameDir = gameDir;
            PluginPath = Path.Combine(PluginDir, PluginFileName);
            ConfigPath = Path.Combine(gameDir, "BepInEx", "config", ConfigFileName);
            BepInExLogPath = Path.Combine(gameDir, "BepInEx", "LogOutput.log");
        }

        public string GameDir { get; }
        public string PluginPath { get; }
        public string ConfigPath { get; }
        public string BepInExLogPath { get; }

        internal string PluginDir => Path.Combine(GameDir, "BepInEx", "plugins", PluginFolderName);
        internal string LegacyPluginPath => Path.Combine(PluginDir, LegacyPluginFileName);

        public bool HasBepInEx => File.Exists(PiecePath(InstallPiece.WinHttp)) && File.Exists(PiecePath(InstallPiece.BepInExCore));

        internal string PiecePath(InstallPiece piece)
        {
            switch (piece)
            {
                case InstallPiece.WinHttp: return Path.Combine(GameDir, "winhttp.dll");
                case InstallPiece.DoorstopConfig: return Path.Combine(GameDir, "doorstop_config.ini");
                case InstallPiece.BepInExPreloader: return Path.Combine(GameDir, "BepInEx", "core", "BepInEx.Preloader.dll");
                case InstallPiece.BepInExCore: return Path.Combine(GameDir, "BepInEx", "core", "BepInEx.dll");
                default: return PluginPath;
            }
        }

        /// <summary>
        /// Pieces that are absent. Plugin is also reported when its SHA-256 differs from the
        /// payload's, cannot be read, or 1.0's PtcglAutoBattleLog.dll still sits beside it (both
        /// would load).
        /// </summary>
        public IReadOnlyList<InstallPiece> Missing(Payload payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var missing = new List<InstallPiece>();
            foreach (var piece in new[] { InstallPiece.WinHttp, InstallPiece.DoorstopConfig, InstallPiece.BepInExPreloader, InstallPiece.BepInExCore })
                if (!File.Exists(PiecePath(piece))) missing.Add(piece);
            if (!PluginIsCurrent(payload) || File.Exists(LegacyPluginPath)) missing.Add(InstallPiece.Plugin);
            return missing;
        }

        private bool PluginIsCurrent(Payload payload)
        {
            if (!File.Exists(PluginPath)) return false;
            byte[] installed;
            try { installed = Hashing.Sha256File(PluginPath); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            return Hashing.BytesEqual(installed, payload.PluginSha256);
        }

        /// <summary>
        /// Puts back what is missing and returns what was missing before. BepInEx files are
        /// extracted only when a loader piece is missing, and only where no file exists. Throws when
        /// the folder is not (or no longer) the game's, or when a file cannot be written.
        /// </summary>
        public IReadOnlyList<InstallPiece> Repair(Payload payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var before = Missing(payload);
            if (before.Count == 0) return before;
            if (!GameLocator.IsGameDir(GameDir))
                throw new DirectoryNotFoundException("Pokemon TCG Live is not installed in " + GameDir);

            if (before.Any(p => p != InstallPiece.Plugin)) ExtractMissingBepInExFiles(payload);

            if (before.Contains(InstallPiece.Plugin))
            {
                Directory.CreateDirectory(PluginDir);
                var tmp = PluginPath + ".tmp";
                try
                {
                    using (var src = payload.OpenPluginDll())
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                        src.CopyTo(dst);
                    FileOps.ReplaceWith(tmp, PluginPath);
                }
                catch
                {
                    FileOps.TryDelete(tmp);
                    throw;
                }
                if (File.Exists(LegacyPluginPath)) File.Delete(LegacyPluginPath);
            }
            return before;
        }

        private void ExtractMissingBepInExFiles(Payload payload)
        {
            using (var zipStream = payload.OpenBepInExZip())
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                // Map every entry before writing anything: one bad entry means a bad payload.
                var plan = new List<KeyValuePair<ZipArchiveEntry, string>>();
                foreach (var entry in zip.Entries)
                {
                    var target = MapEntry(GameDir, entry.FullName);
                    if (target != null) plan.Add(new KeyValuePair<ZipArchiveEntry, string>(entry, target));
                }

                foreach (var item in plan)
                {
                    var target = item.Value;
                    if (File.Exists(target) || Directory.Exists(target)) continue;   // never overwrite
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    var tmp = target + ".tmp";
                    try
                    {
                        using (var src = item.Key.Open())
                        using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                            src.CopyTo(dst);
                        File.Move(tmp, target);   // fails rather than overwrite if it appeared meanwhile
                    }
                    catch (IOException) when (File.Exists(target))
                    {
                        FileOps.TryDelete(tmp);
                    }
                    catch
                    {
                        FileOps.TryDelete(tmp);
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Zip entry name ('/' separated, '\' tolerated) to a path inside <paramref name="root"/>.
        /// Null for directory entries. Throws InvalidDataException for anything that could land
        /// outside the root (rooted, "..", drive letters, invalid characters).
        /// </summary>
        internal static string MapEntry(string root, string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return null;
            if (entryName.EndsWith("/", StringComparison.Ordinal) || entryName.EndsWith("\\", StringComparison.Ordinal)) return null;

            var parts = entryName.Split('/', '\\');
            foreach (var p in parts)
            {
                // Windows' rules even when the tests run on Linux; it also drops trailing dots and
                // spaces, which would turn "..." or "a." into something else.
                if (p.Length == 0 || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) ||
                    p.IndexOfAny(WindowsInvalidNameChars) >= 0 || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Unsafe entry in the BepInEx zip: " + entryName);
            }

            var target = root;
            foreach (var p in parts) target = Path.Combine(target, p);

            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(target).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe entry in the BepInEx zip: " + entryName);
            return target;
        }

        /// <summary>
        /// Any other mod that needs BepInEx: a *.dll under BepInEx\plugins outside our folder, or
        /// any preloader patcher under BepInEx\patchers. When unsure (unreadable folder), says yes.
        /// </summary>
        public bool HasOtherPlugins()
        {
            try
            {
                var plugins = Path.Combine(GameDir, "BepInEx", "plugins");
                if (Directory.Exists(plugins))
                {
                    if (Directory.EnumerateFiles(plugins, "*.dll", SearchOption.TopDirectoryOnly).Any()) return true;
                    foreach (var sub in Directory.EnumerateDirectories(plugins))
                    {
                        if (string.Equals(Path.GetFileName(sub), PluginFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories).Any()) return true;
                    }
                }
                var patchers = Path.Combine(GameDir, "BepInEx", "patchers");
                return Directory.Exists(patchers) && Directory.EnumerateFiles(patchers, "*.dll", SearchOption.AllDirectories).Any();
            }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// Removes our plugin folder and its cfg; BepInEx only if asked and no other mod uses it.
        /// A logs folder the cfg puts inside one of those folders is kept, with its logs (battle
        /// logs are never deleted); the folders kept for it are returned. Tries every step, then
        /// throws an IOException naming what could not be removed (the game is usually running).
        /// </summary>
        public IReadOnlyList<string> Uninstall(bool removeBepInEx)
        {
            var failures = new List<string>();
            var kept = new List<string>();
            void Try(string what, Action a)
            {
                try { a(); }
                catch (Exception e) { failures.Add(what + " (" + e.Message + ")"); }
            }

            string logs = null;
            try { logs = ResolveLogsFolder(null); }   // null: the default folder, in Documents
            catch (Exception) { }
            void Delete(string dir)
            {
                Try(dir, () => { if (FileOps.DeleteTreeExcept(dir, logs)) kept.Add(dir); });
            }

            // The plugin itself first: it goes even when its folder has to stay for the logs in it.
            foreach (var dll in new[] { PluginPath, LegacyPluginPath })
                Try(dll, () => { if (File.Exists(dll)) File.Delete(dll); });
            Delete(PluginDir);
            Try(ConfigPath, () => { if (File.Exists(ConfigPath)) File.Delete(ConfigPath); });

            if (removeBepInEx && !HasOtherPlugins())
            {
                // winhttp.dll first: without it the game no longer loads BepInEx at all.
                foreach (var name in BepInExRootFiles)
                {
                    var f = Path.Combine(GameDir, name);
                    Try(f, () => { if (File.Exists(f)) File.Delete(f); });
                }
                Delete(Path.Combine(GameDir, "BepInEx"));
            }

            if (failures.Count > 0)
                throw new IOException("Could not remove: " + string.Join("; ", failures));
            return kept;
        }

        /// <summary>
        /// The logs folder from the plugin's cfg, as BepInEx would read it. Null if not set, empty
        /// (the plugin's default: Documents\PTCGL Battle Logs, resolved at runtime) or unreadable.
        /// </summary>
        public string ReadOutputFolder()
        {
            var value = ReadConfigValue(ConfigSection, OutputFolderKey);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>
        /// Sets [General] OutputFolder in the cfg, written so BepInEx reads back exactly this folder,
        /// creating the file or the section when missing and keeping every other line. The plugin
        /// reads it on the next game start. Throws ArgumentException for a value a cfg cannot hold,
        /// one the plugin would refuse (<see cref="WhyPluginRefuses"/>), or a folder an uninstall
        /// would delete (<see cref="WhyNotLogsFolder"/>; <paramref name="appFolders"/> are the
        /// app's own data folders).
        /// </summary>
        public void WriteOutputFolder(string folder, params string[] appFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Folder is required.", nameof(folder));
            var why = WhyPluginRefuses(folder) ?? WhyNotLogsFolder(folder, appFolders);
            if (why != null) throw new ArgumentException("The mod cannot use this folder: " + folder + " (" + why + ").", nameof(folder));
            WriteConfigValue(ConfigSection, OutputFolderKey, folder);
        }

        /// <summary>
        /// Why <paramref name="folder"/> must not hold battle logs: uninstalling removes it, because
        /// it is at or under this game's BepInEx folder (our plugin folder, or all of BepInEx when
        /// the app added it) or one of <paramref name="appFolders"/> (%LOCALAPPDATA%\PtcglAutoBattleLog,
        /// a portable copy's folder). A relative folder is taken against the game folder, like the
        /// plugin does. Null when it may.
        /// </summary>
        public string WhyNotLogsFolder(string folder, params string[] appFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(folder.Trim()) ? folder.Trim() : Path.Combine(GameDir, folder.Trim())); }
            catch (Exception) { return null; }   // not a path: WhyPluginRefuses and the plugin deal with it
            if (FileOps.IsSameOrUnder(full, Path.Combine(GameDir, "BepInEx")))
                return "it is inside the game's BepInEx folder, which uninstalling removes";
            foreach (var app in appFolders ?? new string[0])
                if (FileOps.IsSameOrUnder(full, app))
                    return "it is inside " + app + ", which uninstalling removes";
            return null;
        }

        /// <summary>
        /// The folder the plugin saves to, decided the way the plugin decides it at game start
        /// (src/Plugin/Core/LogsFolder.cs): the cfg's OutputFolder (a relative one against the game
        /// folder, BepInEx's GameRootPath), or <paramref name="defaultFolder"/> when it is empty,
        /// not a path, or a value BepInEx garbled or would garble. A 1.0 cfg holds such a value when
        /// the profile path has an apostrophe; the plugin clears it at its next start, and until then
        /// the tray must not open, create or link the garbled folder. Unlike the plugin, it does not
        /// try to create the folder.
        /// </summary>
        public string ResolveLogsFolder(string defaultFolder)
        {
            var configured = ReadOutputFolder();
            if (configured == null) return defaultFolder;
            var value = configured.Trim();
            if (WhyPluginRefuses(value) != null) return defaultFolder;
            try { return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(GameDir, value)); }
            catch (Exception) { return defaultFolder; }
        }

        /// <summary>
        /// Why the plugin would not use this OutputFolder value and fall back to the default folder
        /// (LogsFolder.WhyUnkeepable in the plugin, which tests/TrayTests runs side by side with this
        /// against the real BepInEx): what BepInEx leaves of a path it garbled (\' or '"', a single
        /// leading '\' left of \\server, control characters), or a value BepInEx's own rewrite of the
        /// cfg would change. Null when the plugin uses it.
        /// </summary>
        internal static string WhyPluginRefuses(string value)
        {
            if (value == null) return null;
            if (value.IndexOf('"') >= 0 || value.IndexOf("\\'", StringComparison.Ordinal) >= 0)
                return "BepInEx cannot keep ' or \" in a folder path";
            if (value.StartsWith("\\", StringComparison.Ordinal) && !value.StartsWith("\\\\", StringComparison.Ordinal))
                return "a network path garbled by BepInEx";
            foreach (var c in value)
                if (c < ' ') return "control characters";
            return BepInExConfig.ConvertToObject(BepInExConfig.Escape(value).Trim()) == value ? null : "BepInEx would change it in its cfg";
        }

        /// <summary>
        /// Back to the default logs folder: OutputFolder becomes empty (never the default path itself,
        /// which BepInEx may not round-trip, e.g. C:\Users\O'Brien). No cfg yet = already default.
        /// </summary>
        public void ResetOutputFolder()
        {
            if (!File.Exists(ConfigPath)) return;
            WriteConfigValue(ConfigSection, OutputFolderKey, "");
        }

        /// <summary>
        /// [Metadata] Mode (docs/ARCHITECTURE.md, "Sealed logs") as its canonical name, or null when
        /// absent or not a mode (the plugin then uses Footer). Read like BepInEx's Enum.Parse: names
        /// case-insensitively, and a mode's number (0, 1, 2 in SealModes' order, the plugin's enum).
        /// </summary>
        public string ReadSealMode()
        {
            var value = ReadConfigValue(MetadataSection, SealModeKey)?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            foreach (var m in SealModes)
                if (string.Equals(m, value, StringComparison.OrdinalIgnoreCase)) return m;
            return value.Length == 1 && value[0] >= '0' && value[0] < '0' + SealModes.Count ? SealModes[value[0] - '0'] : null;
        }

        /// <summary>Writes [Metadata] Mode; takes effect at the next game start. ArgumentException for an unknown mode.</summary>
        public void WriteSealMode(string mode)
        {
            string canonical = null;
            foreach (var m in SealModes)
                if (string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase)) canonical = m;
            if (canonical == null) throw new ArgumentException("Unknown seal mode: " + mode, nameof(mode));
            WriteConfigValue(MetadataSection, SealModeKey, canonical);
        }

        /// <summary>Any value of the plugin's cfg, as BepInEx would read it. Null if absent or unreadable.</summary>
        public string ReadConfigValue(string section, string key)
        {
            var text = FileOps.TryReadSmallText(ConfigPath, 1024 * 1024);
            return text == null ? null : BepInExConfig.ReadValue(text, section, key);
        }

        /// <summary>
        /// Sets one value of the plugin's cfg, keeping every other line, the BOM and the line endings.
        /// Throws ArgumentException for a value BepInEx 5.4.23 would not read back as written.
        /// </summary>
        public void WriteConfigValue(string section, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(section)) throw new ArgumentException("Section is required.", nameof(section));
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));

            string text = "";
            bool bom = false;
            if (File.Exists(ConfigPath))
            {
                var bytes = File.ReadAllBytes(ConfigPath);
                bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            }
            var updated = BepInExConfig.WriteValue(text, section, key, value ?? "");
            FileOps.WriteAllTextAtomic(ConfigPath, updated, new UTF8Encoding(bom));
        }
    }
}
