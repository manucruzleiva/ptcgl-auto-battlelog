using System;
using System.Diagnostics;
using System.IO;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// Installed mode, started from anywhere but %LOCALAPPDATA%\PtcglAutoBattleLog: copies itself
    /// there, sets the game up, registers with Windows (all per user) and hands over to the installed
    /// copy with --first-run. Also how a newer download upgrades an older install; an older download
    /// never replaces a newer install (it starts that one instead).
    /// </summary>
    internal static class Installer
    {
        public static int Run(Session session)
        {
            var paths = session.Paths;
            if (WindowsIntegration.IsElevatedWithUac())
            {
                // An elevated tray would hold the mutex with an administrators-only ACL (every later
                // normal start would fail), and an administrator's credentials would put the whole
                // per-user install into the administrator's profile.
                Log.Info("Started with \"Run as administrator\": not installing.");
                Dialogs.Error(AppInfo.ProductName + " installs for your Windows user only and does not need administrator rights.\n\n" +
                              "Please run it again normally (double-click it), not with \"Run as administrator\".");
                return 1;
            }

            var installed = InstalledVersion(paths.InstalledExe);
            if (installed != null && UpdateChecker.IsInstalledNewer(installed, AppInfo.Version)) return StartNewerInstall(paths, installed);

            if (!DisclaimerDialog.EnsureAccepted(session, DisclaimerDialog.Purpose.Install)) return 1;
            Log.Commit();
            Log.Info("Installing " + AppInfo.Version + " from " + session.ExePath + (installed != null ? " over " + installed : ""));

            try
            {
                // A running tray (older version, or a portable copy) keeps its exe locked: ask it to exit,
                // and hold the mutex so nothing starts one while files are moving.
                using (var instance = SingleInstance.StopRunningAndAcquire(TimeSpan.FromSeconds(10)))
                {
                    if (instance.AccessDenied)
                    {
                        Dialogs.Error("A copy of " + AppInfo.ProductName + " started as administrator is running.\n\n" +
                                      "Right-click its icon in the notification area, choose Exit (or sign out of Windows), and run this again.");
                        return 1;
                    }
                    if (!instance.Owned)
                    {
                        Dialogs.Error("Another copy of " + AppInfo.ProductName + " is running and did not close.\n\n" +
                                      "Right-click its icon in the notification area, choose Exit, and run this again.");
                        return 1;
                    }

                    LegacyMigration.Run(session);
                    CopySelf(session.ExePath, paths);

                    var gameDir = GameSetup.Locate(session, askPlayer: true);
                    if (gameDir != null)
                    {
                        var outcome = GameSetup.TryRepair(session, out _, out var error);
                        if (outcome == GameSetup.RepairOutcome.Failed)
                            Dialogs.Warning("The mod could not be added to the game yet:\n" + error + "\n\n" +
                                            AppInfo.ProductName + " will keep trying in the background.");
                    }

                    var logs = GameSetup.EnsureLogsFolder(session);
                    Register(session, logs);
                }

                Process.Start(new ProcessStartInfo(paths.InstalledExe, CommandLine.FirstRunFlag)
                {
                    UseShellExecute = false,   // CreateProcess: no attachment security prompt
                    WorkingDirectory = paths.Root,
                })?.Dispose();
                Log.Info("Installed; started " + paths.InstalledExe);
                return 0;
            }
            catch (Exception e)
            {
                Log.Error("Install failed", e);
                Dialogs.Error("Installation failed:\n" + e.Message + "\n\nDetails are in " + paths.TrayLogFile);
                return 1;
            }
        }

        /// <summary>The installed exe's version (ProductVersion, e.g. "1.2.0+commit"); null when there is none or it cannot be read.</summary>
        private static string InstalledVersion(string exe)
        {
            try { return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null; }
            catch (Exception e)
            {
                Log.Error("Could not read the version of " + exe, e);
                return null;
            }
        }

        /// <summary>
        /// An older download was run (looking for the app, most likely). Installing it would replace a
        /// newer version, and its daemon would put the older plugin into the game. Nothing is written:
        /// the installed copy is shown or started instead.
        /// </summary>
        private static int StartNewerInstall(AppPaths paths, string installed)
        {
            Log.Info("Version " + installed + " is installed, newer than this " + AppInfo.Version + ": not installing; starting the installed copy.");
            try
            {
                if (!SingleInstance.Signal(AppInfo.ShowEventName))
                    Process.Start(new ProcessStartInfo(paths.InstalledExe) { UseShellExecute = false, WorkingDirectory = paths.Root })?.Dispose();
            }
            catch (Exception e) { Log.Error("Could not start " + paths.InstalledExe, e); }
            Dialogs.Info("A newer version of " + AppInfo.ProductName + " (v" + VersionLabel(installed) + ") is already installed, so this " +
                         "older download (v" + AppInfo.Version + ") was not installed.\n\nThe installed version is running: look for its icon in " +
                         "the notification area, next to the clock. You can delete this download.");
            return 0;
        }

        /// <summary>"1.2.0" of "1.2.0+16fdb0f...": the build's commit is noise to a player.</summary>
        private static string VersionLabel(string productVersion)
        {
            int plus = productVersion.IndexOf('+');
            return (plus >= 0 ? productVersion.Substring(0, plus) : productVersion).Trim();
        }

        /// <summary>
        /// Copies the running exe over the installed one. Beside it first, then swapped in, so a
        /// locked or half-copied exe never replaces a working one.
        /// </summary>
        private static void CopySelf(string exe, AppPaths paths)
        {
            Directory.CreateDirectory(paths.Root);
            var staged = paths.InstalledExe + ".new";
            File.Copy(exe, staged, true);
            WindowsIntegration.Unblock(staged);
            try { SelfUpdate.Swap(staged, paths.InstalledExe); }
            finally { PathUtil.TryDeleteFile(staged); }
            SelfUpdate.CleanupOld(paths.InstalledExe);   // fails quietly if the old one is still running
            WindowsIntegration.Unblock(paths.InstalledExe);
        }

        /// <summary>Run key, Apps &amp; Features, Start menu, desktop shortcut to the logs. Each step on its own.</summary>
        private static void Register(Session session, string logsFolder)
        {
            var paths = session.Paths;
            Step("Start with Windows", () => WindowsIntegration.SetStartWithWindows(session.RunCommand, true));
            Step("Apps & Features entry", () => WindowsIntegration.RegisterUninstallEntry(paths));
            Step("Start menu shortcut", () => WindowsIntegration.CreateStartMenuShortcut(paths));
            Step("desktop shortcut", () => WindowsIntegration.CreateLogsShortcut(logsFolder));
        }

        private static void Step(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Log.Error("Could not create the " + what, e); }
        }
    }
}
