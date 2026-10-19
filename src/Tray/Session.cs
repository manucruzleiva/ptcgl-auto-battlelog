using System;
using System.IO;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// Everything one run of the app shares: which mode it is in, where its files are, the settings
    /// (read and written from the UI thread and the daemon, hence the lock) and the embedded payload.
    /// </summary>
    internal sealed class Session
    {
        private readonly object _gate = new object();
        private Settings _settings;

        private Session(string exePath, AppPaths paths, bool portableForced)
        {
            ExePath = exePath;
            Paths = paths;
            PortableForced = portableForced;
            Payload = Embedded.CreatePayload();
        }

        /// <summary>
        /// Installed mode: %LOCALAPPDATA% paths. Portable mode: data next to the exe, or in
        /// %LOCALAPPDATA%\...\portable-data when a previous run found the exe's folder read-only.
        /// Nothing is created here (that waits for consent: PrepareDataFolder), except when an earlier
        /// run already had consent.
        /// </summary>
        public static Session Create(string exePath, CommandLine cli)
        {
            bool marker = AppPaths.HasPortableMarker(exePath);
            if (!cli.Portable && !marker) return new Session(exePath, AppPaths.ForCurrentUser(), false);

            var paths = AppPaths.ForPortable(exePath);
            var fallback = AppPaths.ForPortableFallback(exePath);
            if (!File.Exists(paths.SettingsFile) && File.Exists(fallback.SettingsFile))
            {
                // An earlier run found this folder read-only (and had consent to write: it saved settings).
                // Still read-only: keep using the fallback. Writable now (the folder was moved): bring the settings along.
                if (paths.TryPrepareRoot()) TryCopy(fallback.SettingsFile, paths.SettingsFile);
                else paths = fallback;
            }
            return new Session(exePath, paths, !marker);
        }

        public string ExePath { get; }
        public AppPaths Paths { get; private set; }
        public bool Portable => Paths.IsPortable;
        /// <summary>Portable because of --portable, with no portable.txt: restarts and the Run key must pass it on.</summary>
        public bool PortableForced { get; }
        public Payload Payload { get; }

        public void LoadSettings()
        {
            var s = Settings.Load(Paths.SettingsFile, Log.Info);
            lock (_gate) _settings = s;
        }

        /// <summary>
        /// Portable: creates the data folder, or switches to %LOCALAPPDATA%\...\portable-data when the
        /// exe's folder cannot be written. Settings found so far are carried over.
        /// </summary>
        public void PrepareDataFolder()
        {
            if (!Portable || Paths.TryPrepareRoot() || Paths.UsesFallbackData) return;
            var fallback = AppPaths.ForPortableFallback(ExePath);
            if (!fallback.TryPrepareRoot()) return;
            Settings current;
            lock (_gate) current = _settings;
            Paths = fallback;
            Log.SetFile(Paths.TrayLogFile);
            if (current != null && !File.Exists(Paths.SettingsFile)) Save(current);
            else LoadSettings();
            Log.Info("The portable folder is read-only; data kept in " + Paths.Root);
        }

        private static void TryCopy(string from, string to)
        {
            try { File.Copy(from, to, false); }
            catch (Exception) { }
        }

        // ---- Settings ---------------------------------------------------------------------------

        public T Read<T>(Func<Settings, T> read)
        {
            lock (_gate) return read(_settings ?? (_settings = new Settings()));
        }

        /// <summary>Changes the settings and saves them at once. A failed save is logged, never thrown.</summary>
        public void Update(Action<Settings> change)
        {
            lock (_gate)
            {
                if (_settings == null) _settings = new Settings();
                change(_settings);
                Save(_settings);
            }
        }

        private void Save(Settings s)
        {
            try { s.Save(Paths.SettingsFile); }
            catch (Exception e) { Log.Error("Could not save settings", e); }
        }

        public string GameDir => Read(s => s.GameDir);

        /// <summary>The game install the settings point at, or null when unset or not (any more) the game.</summary>
        public GameInstall Game
        {
            get
            {
                var dir = GameDir;
                return GameLocator.IsGameDir(dir) ? new GameInstall(dir) : null;
            }
        }

        // ---- Logs folder ------------------------------------------------------------------------

        /// <summary>
        /// The folder the plugin will save to: the cfg's OutputFolder (relative paths resolve against
        /// the game folder) or Documents\PTCGL Battle Logs, with the plugin's own fallbacks
        /// (GameInstall.ResolveLogsFolder).
        /// </summary>
        public string LogsFolder(GameInstall game)
        {
            if (game == null) return Paths.DefaultLogsFolder;
            try { return game.ResolveLogsFolder(Paths.DefaultLogsFolder); }
            catch (Exception) { return Paths.DefaultLogsFolder; }
        }

        /// <summary>A folder other than the default one is in effect ("Use the default logs folder" is offered).</summary>
        public bool HasCustomLogsFolder(GameInstall game) => game != null && !PathUtil.Same(LogsFolder(game), Paths.DefaultLogsFolder);

        public string RunCommand => CommandLine.RunCommand(Paths.InstalledExe, Portable && PortableForced);
    }
}
