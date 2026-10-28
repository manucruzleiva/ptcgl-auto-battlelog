using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// The running tray: the notification-area icon and its menu, the repair daemon, the logs
    /// folder watcher and the updater. Everything that touches UI runs on the UI thread; the
    /// daemon and the cross-process signals arrive on other threads and are posted here (Post).
    /// </summary>
    internal sealed partial class TrayApplicationContext : ApplicationContext
    {
        private const int MaxTrayText = 63;   // NotifyIcon.Text throws above this on .NET Framework

        private readonly Session _session;
        private readonly Control _ui;
        private readonly StatusIcons _icons;
        private readonly NotifyIcon _tray;
        private readonly Daemon _daemon;
        private readonly LogsWatcher _watcher;
        private readonly UpdateService _updates;
        private readonly EventListener _showListener;
        private readonly EventListener _exitListener;

        private volatile bool _exiting;
        private DaemonReport _last;
        private Action _balloonClick;
        private int _modal;
        private string _lastSavedFile;
        private string _lastSavedLabel;

        /// <param name="firstRun">Show the welcome balloon (--first-run, or a first portable run).</param>
        /// <param name="signals">The exit and show events, created by Program before any dialog (they outlive this).</param>
        public TrayApplicationContext(Session session, CommandLine cli, bool firstRun, TraySignals signals)
        {
            _session = session;
            try
            {
                // A parked, never-shown window: a target for BeginInvoke and the watcher's events. Creating
                // it also installs the WinForms SynchronizationContext, so awaits come back to this thread.
                _ui = new Control();
                _ui.CreateControl();
                _ = _ui.Handle;

                _icons = new StatusIcons();
                _menu = BuildMenu();
                _tray = new NotifyIcon
                {
                    Icon = _icons.For(TrayHealth.Idle),
                    Text = Clip(AppInfo.ProductName, MaxTrayText),
                    ContextMenuStrip = _menu,
                };
                _tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenLogsFolder(); };
                // The latest balloon's action. Not cleared when a balloon closes: Windows may report the
                // previous one closing after the next one is up, and a toast can be clicked later from the
                // notification center.
                _tray.BalloonTipClicked += (s, e) => Run("balloon click", _balloonClick);

                _watcher = new LogsWatcher(_ui);
                _watcher.Saved += OnSaved;
                _watcher.Watch(_session.LogsFolder(_session.Game));

                _updates = new UpdateService(_session) { CanRestart = () => _modal == 0 && !_exiting };
                _updates.Notify += (title, text, click) => Balloon(title, text, ToolTipIcon.Info, click);
                _updates.AvailableChanged += RefreshUpdateItems;
                _updates.RestartRequested += () => ExitThread();

                _daemon = new Daemon(_session);
                _daemon.Report += report => Post(() => OnReport(report));

                if (signals?.Show != null) _showListener = new EventListener(signals.Show, AppInfo.ShowEventName, () => Post(OnShowRequested));
                if (signals?.Exit != null)
                    _exitListener = new EventListener(signals.Exit, AppInfo.ExitEventName, () => Post(() =>
                    {
                        Log.Info("Another process asked the tray to exit.");
                        ExitThread();
                    }));

                // A first install (or upgrade) while the game runs is finished once the game closes:
                // the welcome says so, and the daemon does not call it "removed by a game update".
                bool setupPending = firstRun && FinishesWhenGameCloses();
                if (setupPending) _daemon.SetupPending();

                _tray.Visible = true;
                _daemon.Start();
                _updates.Start();
                StartUploads();
                StartPokematch();
                Welcome(cli, firstRun, setupPending);
            }
            catch
            {
                Dispose();   // no icon left behind in the notification area, no orphan daemon
                throw;
            }
            Log.Info("Tray running (" + (_session.Portable ? "portable" : "installed") + ", " + _session.Paths.Root + ").");
        }

        // ---- Daemon reports -------------------------------------------------------------------

        private void OnReport(DaemonReport r)
        {
            _last = r;
            var d = r.Decision;

            var icon = _icons.For(d.Health);
            if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;
            var tip = Clip(AppInfo.ProductName + "\n" + d.Summary, MaxTrayText);
            if (_tray.Text != tip) _tray.Text = tip;

            _watcher.Watch(r.WatchFolder);

            switch (d.Notice)
            {
                case DaemonNotice.GameNeedsRestart:
                    Balloon("Restart " + AppInfo.GameName,
                            "The mod is missing from the game (a game update can remove it). Close the game once it has finished updating; " +
                            "the mod is put back automatically.", ToolTipIcon.Warning, null);
                    break;
                case DaemonNotice.RunningWithoutMod:
                    Balloon("Battle logs are not being saved",
                            AppInfo.GameName + " is running without the mod. Restart the game to start saving your battle logs.",
                            ToolTipIcon.Warning, null);
                    break;
                case DaemonNotice.PluginError:
                    Balloon("The mod reported a problem",
                            (string.IsNullOrWhiteSpace(r.Status?.Message) ? "See the BepInEx log." : r.Status.Message.Trim()) + " Click to open the BepInEx log.",
                            ToolTipIcon.Error, OpenBepInExLog);
                    break;
            }

            if (r.Repaired.Any(p => p != InstallPiece.Plugin))
                Balloon("Mod ready", "The mod is in place in " + AppInfo.GameName + ". Battle logs are saved from your next game.", ToolTipIcon.Info, null);

            if (_menu.Visible) RefreshMenu();
        }

        private void OnSaved(string path, string result, string opponent)
        {
            _lastSavedFile = path;
            _lastSavedLabel = result + (opponent != null ? " vs " + opponent : "") + ", " + DateTime.Now.ToString("t");
            UploadMatches(false);
            OnSavedForPokematch();
            if (!_session.Read(s => s.NotifyOnSave)) return;
            Balloon(result + (opponent != null ? " vs " + opponent : "") + " — battle log saved",
                    "Saved in " + PathUtil.Friendly(Path.GetDirectoryName(path)) + ". Click to open it.",
                    ToolTipIcon.Info, () => Shell.OpenText(path));
        }

        private void OnShowRequested()
        {
            if (!HasConsent()) { AskConsent(); return; }
            Balloon(AppInfo.ProductName + " is running",
                    (_last?.Decision?.Summary ?? "Watching for battle logs.") + "\nRight-click this icon for options; double-click it to open your logs.",
                    ToolTipIcon.Info, OpenLogsFolder);
        }

        /// <summary>The mod is not complete in the game yet and the game runs: the setup could not finish.</summary>
        private bool FinishesWhenGameCloses()
        {
            try
            {
                var game = _session.Game;
                return game != null && game.Missing(_session.Payload).Count > 0 && GameProcess.IsRunning();
            }
            catch (Exception) { return false; }
        }

        private void Welcome(CommandLine cli, bool firstRun, bool setupPending)
        {
            if (cli.Updated)
            {
                var from = string.IsNullOrEmpty(cli.UpdatedFrom) ? "" : " from v" + cli.UpdatedFrom;
                Balloon("Updated to v" + AppInfo.Version, AppInfo.ProductName + " was updated" + from + ". Click to see what's new.",
                        ToolTipIcon.Info, () => Shell.Open(AppInfo.ReleasesUrl + "/tag/v" + AppInfo.Version));
                Log.Info("Updated" + from + " to " + AppInfo.Version);
            }
            else if (firstRun)
            {
                var folder = PathUtil.Friendly(_session.LogsFolder(_session.Game));
                var game = _session.Game == null
                    ? "\n" + AppInfo.GameName + " was not found: right-click this icon and choose Help > Diagnostics > Choose game folder."
                    : "";
                if (setupPending)
                    Balloon(AppInfo.ProductName + " is installed",
                            "Close " + AppInfo.GameName + " once to finish installing: battle logs are saved from your next game, to " + folder + ".",
                            ToolTipIcon.Info, OpenLogsFolder);
                else
                    Balloon(AppInfo.ProductName + " is ready",
                            "Every match is saved to " + folder + ". Right-click this icon for options." + game, ToolTipIcon.Info, OpenLogsFolder);
            }
            else if (!cli.Background) OnShowRequested();

            // --background and --updated-from never show the disclaimer: after a version bump, ask with a balloon.
            if (!HasConsent())
                Balloon("The disclaimer was updated", "Please review it to keep using " + AppInfo.ProductName + ". Click to read it.",
                        ToolTipIcon.Warning, AskConsent);
        }

        private bool HasConsent() => _session.Read(s => s.DisclaimerAccepted) >= Disclaimer.Version;

        /// <summary>
        /// The updated disclaimer, asked from the running tray. Until it is accepted nothing is written
        /// into the game (DaemonMonitor, GameSetup.TryRepair). Declining stops the app for good, not
        /// just until the next logon (the Run value goes), and offers to take the mod out of the game.
        /// </summary>
        private void AskConsent()
        {
            if (HasConsent() || _modal > 0) return;
            bool accepted;
            using (Modal()) accepted = DisclaimerDialog.EnsureAccepted(_session, DisclaimerDialog.Purpose.Continue);
            if (accepted)
            {
                _daemon.Poke();   // repairs and updates the mod again
                OnConsentForPokematch();
                return;
            }
            try { WindowsIntegration.RemoveStartWithWindows(_session.Paths.InstalledExe); }
            catch (Exception e) { Log.Error("Could not turn off Start with Windows", e); }
            bool remove;
            using (Modal())
                remove = Dialogs.Confirm("You did not accept the updated disclaimer, so " + AppInfo.ProductName + " closes now and no longer " +
                                         "starts with Windows.\n\nThe mod is still in " + AppInfo.GameName + " and keeps saving battle logs. " +
                                         "Remove it from the game too?");
            if (remove)
            {
                try { Uninstall(); }   // the --uninstall process asks once more, waits for the game and stops this tray
                catch (Exception e) { Log.Error("Could not start the uninstaller", e); }
            }
            ExitThread();
        }

        // ---- Plumbing -------------------------------------------------------------------------

        /// <summary>Runs on the UI thread later; dropped once the tray is exiting.</summary>
        private void Post(Action action)
        {
            if (_exiting) return;
            try
            {
                if (_ui.IsDisposed || !_ui.IsHandleCreated) return;
                _ui.BeginInvoke(new MethodInvoker(() => { if (!_exiting) Run("UI update", action); }));
            }
            catch (InvalidOperationException) { }   // handle gone (ObjectDisposedException included): shutting down
        }

        private static void Run(string what, Action action)
        {
            if (action == null) return;
            try { action(); }
            catch (Exception e) { Log.Error(what + " failed", e); }
        }

        private void Balloon(string title, string text, ToolTipIcon icon, Action onClick)
        {
            if (_exiting || string.IsNullOrWhiteSpace(text)) return;
            _balloonClick = onClick;
            try { _tray.ShowBalloonTip(10000, Clip(title, 63), Clip(text, 255), icon); }
            catch (Exception e) { Log.Error("Balloon failed", e); }
        }

        /// <summary>Counts open dialogs: an update must not restart the app under one.</summary>
        private IDisposable Modal()
        {
            _modal++;
            return new Release(() => _modal--);
        }

        private sealed class Release : IDisposable
        {
            private Action _release;
            public Release(Action release) { _release = release; }
            public void Dispose() { _release?.Invoke(); _release = null; }
        }

        internal static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max - 1) + "…";
        }

        protected override void ExitThreadCore()
        {
            Shutdown();
            base.ExitThreadCore();
        }

        /// <summary>Stops everything that runs on its own. Idempotent; UI thread.</summary>
        private void Shutdown()
        {
            if (_exiting) return;
            _exiting = true;
            Log.Info("Tray exiting.");
            // Fields may be null when the constructor failed halfway.
            if (_tray != null) _tray.Visible = false;   // gone from the notification area at once, not at the next mouse-over
            _menu?.Close();
            Run("stop listeners", () => { _exitListener?.Dispose(); _showListener?.Dispose(); });
            Run("stop updates", () => _updates?.Dispose());
            Run("stop tcgkb", () => { _uploadTimer?.Dispose(); _tcgkb?.Dispose(); });
            Run("stop tcgkb", () => _tcgkb?.Dispose());
            Run("stop Pokematch", () => { _pokematchTimer?.Stop(); _pokematchTimer?.Dispose(); _pokematch?.Dispose(); });
            Run("stop watcher", () => _watcher?.Dispose());
            Run("stop daemon", () => _daemon?.Dispose());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Shutdown();
                _tray?.Dispose();
                _menu?.Dispose();
                _boldFont?.Dispose();
                _icons?.Dispose();
                _ui?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
