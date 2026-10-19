using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// Upgrading from 1.0 (the PowerShell installer): keep its game folder and BepInEx ownership,
    /// stop its hidden watchdog (it would put the 1.0 plugin back), remove its Startup shortcut and
    /// files. Runs in both modes, and is a few file checks when there is nothing to do.
    /// </summary>
    internal static class LegacyMigration
    {
        public static void Run(Session session)
        {
            var legacy = new LegacyInstall(session.Paths.LocalRoot);
            bool shortcut = File.Exists(WindowsIntegration.LegacyStartupShortcut);
            if (!legacy.Exists && !shortcut) return;
            Log.Info("Migrating the 1.0 installation in " + legacy.Root);

            var dir = legacy.ReadGameDir();
            if (GameLocator.IsGameDir(dir) && !GameLocator.IsGameDir(session.GameDir))
            {
                bool ours = legacy.BepInExInstalledByUs(dir);
                session.Update(s =>
                {
                    s.GameDir = dir;
                    s.BepInExInstalledByUs = ours;
                });
                Log.Info("1.0 game folder: " + dir + (ours ? " (1.0 added BepInEx)" : ""));
            }

            StopWatchdogs();
            if (shortcut && !PathUtil.TryDeleteFile(WindowsIntegration.LegacyStartupShortcut))
                Log.Info("Could not delete " + WindowsIntegration.LegacyStartupShortcut);

            var failed = legacy.RemoveLeftovers();
            if (failed.Count > 0) Log.Info("1.0 files left behind: " + string.Join(", ", failed));
        }

        /// <summary>Kills 1.0's hidden PowerShell watchdog, found by its command line (WMI).</summary>
        public static void StopWatchdogs()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'powershell.exe' OR Name = 'pwsh.exe'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementBaseObject p in results)
                    {
                        using (p)
                        {
                            var commandLine = p["CommandLine"] as string;   // null for other users' processes
                            if (!LegacyInstall.IsWatchdogCommandLine(commandLine)) continue;
                            var pid = Convert.ToInt32(p["ProcessId"], System.Globalization.CultureInfo.InvariantCulture);
                            try
                            {
                                using (var process = Process.GetProcessById(pid))
                                {
                                    process.Kill();
                                    process.WaitForExit(5000);
                                }
                                Log.Info("Stopped the 1.0 watchdog (pid " + pid + ").");
                            }
                            catch (Exception e) { Log.Error("Could not stop the 1.0 watchdog (pid " + pid + ")", e); }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // WMI can be broken or disabled. The watchdog also quits by itself once its payload is gone.
                Log.Error("Could not look for the 1.0 watchdog", e);
            }
        }
    }
}
