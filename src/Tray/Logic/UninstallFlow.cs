using System;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>How an uninstall ended (docs/ARCHITECTURE.md, "Install and uninstall").</summary>
    public enum UninstallResult
    {
        /// <summary>Everything was removed.</summary>
        Done,
        /// <summary>The player said no, at the confirmation or while waiting for the game. Nothing changed.</summary>
        Cancelled,
        /// <summary>--quiet with the game running: nothing changed (nobody can be asked to close it).</summary>
        GameRunning,
        /// <summary>A running tray did not exit: nothing changed, or it would put the mod back.</summary>
        TrayStillRunning,
        /// <summary>Some of the mod's files are still in the game: the registration stays, so the uninstall can be run again.</summary>
        GameFilesLeft,
    }

    /// <summary>What an uninstall needs from Windows and from the player. Uninstaller implements it; the tests fake it.</summary>
    public interface IUninstallHost
    {
        /// <summary>--quiet: no dialogs at all.</summary>
        bool Quiet { get; }
        /// <summary>Portable: the mod is removed from the game, the folder stays (the player deletes it).</summary>
        bool Portable { get; }
        /// <summary>Asks the player to confirm. Interactive only.</summary>
        bool Confirm();
        bool IsGameRunning();
        /// <summary>Retry/Cancel until the game is closed; false when the player gave up. Interactive only.</summary>
        bool WaitForGameToClose();
        /// <summary>
        /// Asks a running tray to exit and holds the single-instance mutex until disposed, so no tray
        /// starts (and repairs the mod) while files are removed. <paramref name="stopped"/> is false
        /// when a tray is still running.
        /// </summary>
        IDisposable StopTray(out bool stopped);
        /// <summary>1.0's watchdog and Startup shortcut.</summary>
        void StopLegacy();
        /// <summary>Takes the mod out of the game; null when complete, else what is still there.</summary>
        string RemoveFromGame();
        /// <summary>Start with Windows; installed: also the Apps &amp; Features entry and the shortcuts.</summary>
        void RemoveRegistration();
        /// <summary>Installed only: %LOCALAPPDATA%\PtcglAutoBattleLog except identity\ (and a logs folder inside it).</summary>
        void DeleteInstallFolder();
        /// <summary>Logs the outcome, and tells the player unless quiet. <paramref name="detail"/>: what was left (GameFilesLeft).</summary>
        void Report(UninstallResult result, string detail);
    }

    /// <summary>
    /// The order of an uninstall, and when it stops. Nothing is removed while the game runs or
    /// while another tray is still alive; and when the game folder cannot be cleaned completely,
    /// the registration and the settings (game folder, whether BepInEx was ours) stay, so the
    /// uninstall can simply be run again. Returns the process exit code: 0 only when everything
    /// was removed.
    /// </summary>
    public static class UninstallFlow
    {
        public static int Run(IUninstallHost host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (host.Quiet)
            {
                if (host.IsGameRunning()) return Stop(host, UninstallResult.GameRunning, null);
            }
            else
            {
                if (!host.Confirm() || !host.WaitForGameToClose()) return Stop(host, UninstallResult.Cancelled, null);
            }

            bool stopped;
            using (host.StopTray(out stopped))
            {
                if (!stopped) return Stop(host, UninstallResult.TrayStillRunning, null);
                host.StopLegacy();
                var left = host.RemoveFromGame();
                if (left != null) return Stop(host, UninstallResult.GameFilesLeft, left);
                host.RemoveRegistration();
            }
            host.Report(UninstallResult.Done, null);
            // Last: deleting tray.log and then logging again would bring the folder back.
            if (!host.Portable) host.DeleteInstallFolder();
            return 0;
        }

        private static int Stop(IUninstallHost host, UninstallResult result, string detail)
        {
            host.Report(result, detail);
            return 1;
        }
    }
}
