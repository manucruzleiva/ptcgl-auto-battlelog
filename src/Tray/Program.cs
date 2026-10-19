using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// Entry point: decides between install, uninstall and running the tray (docs/ARCHITECTURE.md,
    /// "Command line", "Portable mode", "Disclaimer and consent").
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var cli = CommandLine.Parse(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // A bug in one menu action is logged and the tray keeps running; it must not take the
            // repair daemon down with it.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Error("Unhandled exception on the UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Fatal("Fatal unhandled exception", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error("Unobserved task exception", e.Exception);
                e.SetObserved();
            };
            UpdateService.ConfigureTls();

            Session session;
            try
            {
                session = Session.Create(Application.ExecutablePath, cli);
                Log.SetFile(session.Paths.TrayLogFile);
                session.LoadSettings();
            }
            catch (Exception e)
            {
                Dialogs.Error(AppInfo.ProductName + " could not start:\n" + e.Message);
                return 1;
            }

            // Lines wait in memory while this run may still ask for consent (and Cancel must leave no trace).
            bool consentPending = session.Read(s => s.DisclaimerAccepted) < Disclaimer.Version && !cli.Uninstall && !cli.Background && !cli.Updated;
            if (!consentPending) Commit(session);
            Log.Info("Start: v" + AppInfo.Version + " " + (session.Portable ? "portable" : "installed") + " mode, exe " + session.ExePath +
                     (args.Length > 0 ? ", args: " + string.Join(" ", args) : ""));
            if (cli.Unknown.Count > 0) Log.Info("Ignored arguments: " + string.Join(" ", cli.Unknown));

            try
            {
                if (cli.Uninstall) return Uninstaller.Run(session, cli.Quiet);

                if (!session.Portable && !PathUtil.Same(session.ExePath, session.Paths.InstalledExe))
                {
                    if (cli.Background)
                    {
                        Log.Info("--background from outside the install folder: nothing to do.");
                        return 0;
                    }
                    return Installer.Run(session);
                }
                return RunTray(session, cli);
            }
            catch (Exception e)
            {
                Log.Fatal("Startup failed", e);
                Dialogs.Error(AppInfo.ProductName + " stopped because of an unexpected error:\n" + e.Message + "\n\nDetails are in " + session.Paths.TrayLogFile);
                return 1;
            }
        }

        private static void Commit(Session session)
        {
            if (session.Portable) session.PrepareDataFolder();
            Log.Commit();
        }

        private static int RunTray(Session session, CommandLine cli)
        {
            // After a self-update the old process is still exiting; after an install, the installer just let go.
            var wait = cli.Updated ? TimeSpan.FromSeconds(10) : cli.FirstRun ? TimeSpan.FromSeconds(5) : TimeSpan.Zero;
            using (var instance = SingleInstance.Acquire(wait))
            {
                if (!instance.Owned)
                {
                    if (instance.AccessDenied)
                    {
                        Log.Info("A copy started as administrator is running; this one cannot reach it.");
                        if (!cli.Background)
                            Dialogs.Warning(AppInfo.ProductName + " is already running as administrator, so this copy cannot reach it.\n\n" +
                                            "Right-click its icon in the notification area and choose Exit (or sign out of Windows), " +
                                            "then start " + AppInfo.ProductName + " again without \"Run as administrator\".");
                        return 1;
                    }
                    if (!cli.Background) SingleInstance.Signal(AppInfo.ShowEventName);
                    Log.Info("Already running" + (cli.Background ? "." : "; asked it to show itself."));
                    return 0;
                }

                // Listening from here on: an installer or uninstaller that asks this tray to exit while
                // a dialog below is open is answered as soon as the dialog closes, not ignored.
                using (var signals = TraySignals.Create())
                {
                    bool firstRun = cli.FirstRun;
                    if (!cli.Background && !cli.Updated && session.Read(s => s.DisclaimerAccepted) < Disclaimer.Version)
                    {
                        if (!DisclaimerDialog.EnsureAccepted(session, DisclaimerDialog.Purpose.Continue)) return 1;
                        firstRun = true;
                    }
                    Commit(session);
                    if (ExitRequested(signals)) return 0;

                    SelfUpdate.CleanupOld(session.Paths.InstalledExe);
                    LegacyMigration.Run(session);
                    if (ExitRequested(signals)) return 0;
                    if (!session.Portable)
                    {
                        // Keeps DisplayVersion in Apps & Features right after OTA updates.
                        try { WindowsIntegration.RegisterUninstallEntry(session.Paths); }
                        catch (Exception e) { Log.Error("Could not refresh the Apps & Features entry", e); }
                    }
                    else
                    {
                        // Like the installed copy: no "Open File - Security Warning" at every start or logon
                        // for an exe the player extracted from the downloaded zip and chose to run.
                        if (session.Read(s => s.DisclaimerAccepted) >= Disclaimer.Version) WindowsIntegration.Unblock(session.ExePath);
                        if (session.Game == null && !cli.Background && !cli.Updated)
                        {
                            // Portable has no installer: its first interactive run sets the game up.
                            var found = GameSetup.Locate(session, askPlayer: true) != null;
                            if (ExitRequested(signals)) return 0;
                            if (found) GameSetup.TryRepair(session, out _, out _);
                            firstRun = true;
                        }
                    }
                    if (ExitRequested(signals)) return 0;

                    using (var context = new TrayApplicationContext(session, cli, firstRun, signals))
                        Application.Run(context);
                    Log.Info("Exited.");
                    return 0;
                }
            }
        }

        private static bool ExitRequested(TraySignals signals)
        {
            if (!signals.ExitRequested()) return false;
            Log.Info("Another process asked the tray to exit while it was starting; exiting.");
            return true;
        }
    }
}
