using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>
    /// --uninstall (Apps &amp; Features, or the tray's "Uninstall..."): stops the tray, takes the mod out
    /// of the game, removes the per-user registration and the install folder. Battle logs stay.
    /// Portable copies only take the mod out ("Remove mod from game..."); the player deletes the folder.
    /// The order, and when it stops without changing anything, is UninstallFlow's; this is the
    /// Windows side of it.
    /// </summary>
    internal sealed class Uninstaller : IUninstallHost
    {
        /// <summary>The plugin's signing identity (docs/ARCHITECTURE.md, "Sealed logs") outlives an uninstall,
        /// like the sealed logs that name it: a reinstall continues the same chain.</summary>
        internal const string KeptFolderName = "identity";

        private readonly Session _session;
        private readonly string _logs;

        private Uninstaller(Session session, bool quiet)
        {
            _session = session;
            Quiet = quiet;
            _logs = session.LogsFolder(session.Game);
        }

        public bool Quiet { get; }
        public bool Portable => _session.Portable;

        public static int Run(Session session, bool quiet)
        {
            Log.Commit();
            // A working directory inside the install folder (the Start menu shortcut sets one) would keep it from being deleted.
            try { Directory.SetCurrentDirectory(Path.GetTempPath()); }
            catch (Exception) { }
            Log.Info("Uninstall requested (" + (quiet ? "quiet" : "interactive") + (session.Portable ? ", portable" : "") + ").");
            return UninstallFlow.Run(new Uninstaller(session, quiet));
        }

        public bool Confirm()
        {
            return Dialogs.Confirm(Portable
                ? "Remove the mod from " + AppInfo.GameName + "?\n\nYour battle logs stay in:\n" + _logs
                : "Uninstall " + AppInfo.ProductName + "?\n\nThe mod is removed from " + AppInfo.GameName + ". Your battle logs stay in:\n" + _logs);
        }

        public bool IsGameRunning() => GameProcess.IsRunning();

        /// <summary>True once the game is closed; false when the player gave up (nothing was changed).</summary>
        public bool WaitForGameToClose()
        {
            while (GameProcess.IsRunning())
                if (!Dialogs.RetryCancel(AppInfo.GameName + " is running. Close it, then click Retry.")) return false;
            return true;
        }

        public IDisposable StopTray(out bool stopped)
        {
            var instance = SingleInstance.StopRunningAndAcquire(TimeSpan.FromSeconds(10));
            stopped = instance.Owned;
            return instance;
        }

        public void StopLegacy()
        {
            LegacyMigration.StopWatchdogs();
            PathUtil.TryDeleteFile(WindowsIntegration.LegacyStartupShortcut);
        }

        public string RemoveFromGame() => RemoveFromGame(_session);

        public void RemoveRegistration()
        {
            var paths = _session.Paths;
            Try("Start with Windows", () => WindowsIntegration.RemoveStartWithWindows(paths.InstalledExe));
            if (Portable) return;
            Try("Apps & Features entry", WindowsIntegration.RemoveUninstallEntry);
            Try("Start menu shortcut", () => PathUtil.TryDeleteFile(WindowsIntegration.StartMenuShortcut));
            Try("desktop shortcut", () => PathUtil.TryDeleteFile(WindowsIntegration.DesktopShortcut));
        }

        public void DeleteInstallFolder() => DeleteInstallFolder(_session.Paths, _session.ExePath, _logs);

        public void Report(UninstallResult result, string detail)
        {
            switch (result)
            {
                case UninstallResult.Done:
                    Log.Info("Uninstall finished.");
                    if (!Quiet)
                        Dialogs.Info((Portable
                            ? "The mod was removed from the game. You can now delete this folder:\n" + Path.GetDirectoryName(_session.ExePath)
                            : AppInfo.ProductName + " was uninstalled.") + "\n\nYour battle logs are still in:\n" + _logs);
                    break;
                case UninstallResult.Cancelled:
                    Log.Info("Uninstall cancelled; nothing was changed.");
                    break;
                case UninstallResult.GameRunning:
                    Log.Info(AppInfo.GameName + " is running: nothing was removed (exit code 1). Close the game and uninstall again.");
                    break;
                case UninstallResult.TrayStillRunning:
                    Log.Info("The running tray did not exit: nothing was removed (exit code 1).");
                    if (!Quiet)
                        Dialogs.Error("Another copy of " + AppInfo.ProductName + " is running and did not close (it may be waiting for an answer in a window).\n\n" +
                                      "Close that window, or right-click its icon in the notification area and choose Exit, then " +
                                      (Portable ? "remove the mod" : "uninstall") + " again. Nothing was removed.");
                    break;
                case UninstallResult.GameFilesLeft:
                    Log.Info("Some of the mod's files are still in the game; nothing else was removed, so the uninstall can be run again (exit code 1).");
                    if (!Quiet)
                        Dialogs.Warning("Some of the mod's files could not be removed from " + AppInfo.GameName + ":\n" + detail + "\n\n" +
                                        "Nothing else was changed: " + AppInfo.ProductName + " is still " + (Portable ? "set up" : "installed") + ". " +
                                        "Close the game (and anything else that may use its files), then " +
                                        (Portable ? "start this app and choose Remove mod from game... again." : "uninstall again."));
                    break;
            }
        }

        /// <summary>
        /// Our plugin folder and cfg, and BepInEx when we added it and no other mod uses it. Returns
        /// what could not be removed (the game holds its files while running), or null.
        /// </summary>
        public static string RemoveFromGame(Session session)
        {
            var dir = session.GameDir;
            if (!GameLocator.IsGameDir(dir))
            {
                try { dir = GameFinder.Find(dir); }
                catch (Exception) { dir = null; }
            }
            if (dir == null) { Log.Info("No game folder: nothing to remove from the game."); return null; }

            bool removeBepInEx = session.Read(s => s.BepInExInstalledByUs);
            try
            {
                var kept = new GameInstall(dir).Uninstall(removeBepInEx);
                Log.Info("Removed the mod from " + dir + (removeBepInEx ? " (BepInEx too, unless another mod uses it)" : ""));
                foreach (var folder in kept) Log.Info("Kept " + folder + ": the logs folder is inside it.");
                // Forget the game: if this copy runs again, it adopts the folder afresh (and owns the BepInEx it adds).
                session.Update(s =>
                {
                    s.GameDir = null;
                    s.BepInExInstalledByUs = false;
                });
                return null;
            }
            catch (Exception e)
            {
                Log.Error("Removing the mod from " + dir + " was incomplete", e);
                return e.Message;
            }
        }

        private static void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Log.Error("Could not remove the " + what, e); }
        }

        /// <summary>
        /// Deletes %LOCALAPPDATA%\PtcglAutoBattleLog except identity\, and except the logs folder when
        /// someone put it in there (battle logs are never deleted). What is in use (this exe, when
        /// Apps &amp; Features started the installed copy) goes after this process exits.
        /// </summary>
        private static void DeleteInstallFolder(AppPaths paths, string runningExe, string logsFolder)
        {
            var root = paths.Root;
            if (!IsOurInstallFolder(root))
            {
                Log.Error("Refusing to delete an unexpected folder: " + root);
                return;
            }
            if (!Directory.Exists(root)) return;
            if (PathUtil.Same(root, logsFolder))
            {
                Log.Info("Kept " + root + ": it is the logs folder.");
                return;
            }

            bool leftovers = false;
            var keep = new List<string> { KeptFolderName };
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                {
                    var name = Path.GetFileName(entry);
                    if (string.Equals(name, KeptFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(entry))
                    {
                        try
                        {
                            if (FileOps.DeleteTreeExcept(entry, logsFolder))
                            {
                                keep.Add(name);
                                Log.Info("Kept " + logsFolder + " in " + root + ": it is the logs folder.");
                            }
                        }
                        catch (Exception) { leftovers = true; }
                    }
                    else if (!PathUtil.TryDeleteFile(entry)) leftovers = true;   // this exe, while it runs
                }
            }
            catch (Exception e)
            {
                Log.Error("Could not list " + root, e);
                leftovers = true;
            }

            if (leftovers || IsInside(runningExe, root)) DeleteAfterExit(root, keep);
            else TryRemoveIfEmpty(root);
        }

        /// <summary>
        /// Windows cannot delete a running exe, so a hidden Windows PowerShell waits for this process
        /// to exit and deletes the rest. Nothing about the folder is put into its command line: the
        /// script is a constant, the folder and the process id travel in environment variables, so
        /// no path (apostrophes, ampersands, percent signs...) can change what runs. Not
        /// -EncodedCommand either: antivirus products treat hidden encoded PowerShell as suspicious.
        /// </summary>
        private static void DeleteAfterExit(string root, IEnumerable<string> keep)
        {
            // Kept names are joined with '|', which no Windows file name can contain.
            const string script =
                "$ErrorActionPreference = 'SilentlyContinue'; " +
                "$root = $env:PTCGL_ABL_DELETE_ROOT; $keep = @($env:PTCGL_ABL_KEEP -split '\\|'); " +
                "if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) { exit }; " +
                "Wait-Process -Id ([int]$env:PTCGL_ABL_WAIT_PID) -Timeout 600; " +
                "for ($i = 0; $i -lt 40; $i++) { " +
                "  $rest = @(Get-ChildItem -LiteralPath $root -Force | Where-Object { $keep -notcontains $_.Name }); " +
                "  if ($rest.Count -eq 0) { break }; " +
                "  $rest | Remove-Item -Recurse -Force; Start-Sleep -Milliseconds 500 }; " +
                "if (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) { Remove-Item -LiteralPath $root -Force }";
            try
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
                var psi = new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -Command \"" + script + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath(),   // never inside the folder being deleted
                };
                psi.EnvironmentVariables["PTCGL_ABL_DELETE_ROOT"] = root;
                psi.EnvironmentVariables["PTCGL_ABL_KEEP"] = string.Join("|", keep);
                using (var self = Process.GetCurrentProcess())
                    psi.EnvironmentVariables["PTCGL_ABL_WAIT_PID"] = self.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Process.Start(psi)?.Dispose();
                Log.Info("Scheduled the removal of " + root + " after exit.");
            }
            catch (Exception e)
            {
                Log.Error("Could not schedule the removal of " + root + "; delete it by hand", e);
            }
        }

        /// <summary>Only ever %LOCALAPPDATA%\PtcglAutoBattleLog: a bug must never turn into deleting something else.</summary>
        private static bool IsOurInstallFolder(string root)
        {
            try
            {
                var expected = AppPaths.ForCurrentUser().LocalRoot;
                return PathUtil.Same(root, expected) &&
                       string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), AppPaths.AppId, StringComparison.OrdinalIgnoreCase) &&
                       Path.GetPathRoot(Path.GetFullPath(root)) != Path.GetFullPath(root);
            }
            catch (Exception) { return false; }
        }

        private static bool IsInside(string file, string folder)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(file));
                return PathUtil.Same(dir, folder);
            }
            catch (Exception) { return false; }
        }

        private static void TryRemoveIfEmpty(string root)
        {
            try { if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0) Directory.Delete(root); }
            catch (Exception) { }
        }
    }
}
