using System;
using System.IO;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>
    /// Where the tray keeps its own files. Installed: everything per user under %LOCALAPPDATA%, so
    /// nothing needs admin rights. Portable: next to the exe (docs/ARCHITECTURE.md, "Portable mode").
    /// Built from explicit folders so tests can point it at a temp dir.
    /// </summary>
    public sealed class AppPaths
    {
        public const string AppId = "PtcglAutoBattleLog";
        public const string ExeName = AppId + ".exe";
        public const string LogsFolderName = "PTCGL Battle Logs";
        public const string PortableMarkerFileName = "portable.txt";
        public const string PortableDataFolderName = "data";
        public const string PortableFallbackFolderName = "portable-data";

        public AppPaths(string localAppData, string documents) : this(localAppData, documents, null, null, false) { }

        private AppPaths(string localAppData, string documents, string portableExe, string portableData, bool fallback)
        {
            if (string.IsNullOrEmpty(localAppData)) throw new ArgumentException("Local app data folder is required.", nameof(localAppData));
            if (string.IsNullOrEmpty(documents)) throw new ArgumentException("Documents folder is required.", nameof(documents));

            LocalRoot = Path.Combine(localAppData, AppId);
            IsPortable = portableExe != null;
            Root = portableData ?? LocalRoot;
            InstalledExe = portableExe ?? Path.Combine(Root, ExeName);
            OldExe = InstalledExe + ".old";
            SettingsFile = Path.Combine(Root, "settings.json");
            // The plugin knows nothing about portable mode: it always writes here.
            PluginStatusFile = Path.Combine(LocalRoot, "plugin-status.json");
            TrayLogFile = Path.Combine(Root, "tray.log");
            UpdatesDir = Path.Combine(Root, "updates");
            DefaultLogsFolder = Path.Combine(documents, LogsFolderName);
            UsesFallbackData = IsPortable && fallback;
        }

        /// <summary>The real folders. Falls back to the profile when one is unset (broken folder redirection, Linux tests).</summary>
        public static AppPaths ForCurrentUser()
        {
            return new AppPaths(CurrentLocalAppData(), CurrentDocuments());
        }

        /// <summary>True when portable.txt sits next to the exe. Never throws.</summary>
        public static bool HasPortableMarker(string exePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(exePath);
                return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, PortableMarkerFileName));
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// A portable copy: data in &lt;exe folder&gt;\data, the exe updated in place wherever it is and
        /// whatever it is called. plugin-status.json stays in %LOCALAPPDATA%.
        /// </summary>
        public static AppPaths ForPortable(string exePath, string localAppData, string documents)
        {
            return new AppPaths(localAppData, documents, ExeOrThrow(exePath), Path.Combine(ExeDir(exePath), PortableDataFolderName), false);
        }

        /// <summary>
        /// The same portable copy with its data in %LOCALAPPDATA%\PtcglAutoBattleLog\portable-data, for
        /// when the exe's folder cannot be written (read-only media, Program Files).
        /// </summary>
        public static AppPaths ForPortableFallback(string exePath, string localAppData, string documents)
        {
            if (string.IsNullOrEmpty(localAppData)) throw new ArgumentException("Local app data folder is required.", nameof(localAppData));
            return new AppPaths(localAppData, documents, ExeOrThrow(exePath), Path.Combine(localAppData, AppId, PortableFallbackFolderName), true);
        }

        public static AppPaths ForPortable(string exePath) => ForPortable(exePath, CurrentLocalAppData(), CurrentDocuments());
        public static AppPaths ForPortableFallback(string exePath) => ForPortableFallback(exePath, CurrentLocalAppData(), CurrentDocuments());

        public static string CurrentLocalAppData() => Folder(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData);
        public static string CurrentDocuments() => Folder(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile);

        private static string Folder(Environment.SpecialFolder preferred, Environment.SpecialFolder fallback)
        {
            var f = Environment.GetFolderPath(preferred);
            return string.IsNullOrEmpty(f) ? Environment.GetFolderPath(fallback) : f;
        }

        private static string ExeOrThrow(string exePath)
        {
            ExeDir(exePath);
            return exePath;
        }

        private static string ExeDir(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentException("Exe path is required.", nameof(exePath));
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) throw new ArgumentException("Exe path must include its folder: " + exePath, nameof(exePath));
            return dir;
        }

        /// <summary>Data folder: %LOCALAPPDATA%\PtcglAutoBattleLog installed, &lt;exe folder&gt;\data portable.</summary>
        public string Root { get; }
        /// <summary>%LOCALAPPDATA%\PtcglAutoBattleLog in both modes: plugin-status.json, the plugin's identity, 1.0 leftovers.</summary>
        public string LocalRoot { get; }
        /// <summary>The exe that runs the tray: the installed copy, or the portable exe itself.</summary>
        public string InstalledExe { get; }
        public string OldExe { get; }
        public string SettingsFile { get; }
        public string PluginStatusFile { get; }
        public string TrayLogFile { get; }
        public string UpdatesDir { get; }
        public string DefaultLogsFolder { get; }
        public bool IsPortable { get; }
        /// <summary>Portable, with data in %LOCALAPPDATA% because the exe's folder is read-only.</summary>
        public bool UsesFallbackData { get; }

        /// <summary>
        /// Creates Root and proves a file can be written in it (a folder can exist and still be
        /// read-only). False instead of throwing.
        /// </summary>
        public bool TryPrepareRoot()
        {
            try
            {
                Directory.CreateDirectory(Root);
                var probe = Path.Combine(Root, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
