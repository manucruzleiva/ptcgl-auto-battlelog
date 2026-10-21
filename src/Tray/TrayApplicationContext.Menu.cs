using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using PtcglAutoBattleLog.Tray.Logic;

namespace PtcglAutoBattleLog.Tray
{
    /// <summary>The tray menu (docs/ARCHITECTURE.md, "Tray app") and what each item does.</summary>
    internal sealed partial class TrayApplicationContext
    {
        private readonly ContextMenuStrip _menu;
        private Font _boldFont;

        private ToolStripMenuItem _statusItem, _lastSavedItem, _logsInfoItem, _dataInfoItem, _findGameItem;
        private ToolStripMenuItem _openLastItem, _changeFolderItem, _defaultFolderItem;
        private ToolStripMenuItem _notifyItem, _autoUpdateItem, _updateItem, _startupItem, _repairItem;
        private ToolStripMenuItem _sealItem, _bepInExLogItem, _gameFolderItem;

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
            _boldFont = new Font(menu.Font, FontStyle.Bold);

            var title = Info(MenuText(AppInfo.ProductName + " v" + AppInfo.Version + (_session.Portable ? " (portable)" : "")));
            title.Font = _boldFont;
            _statusItem = Info("Starting...");
            _lastSavedItem = Info("");
            _logsInfoItem = Info("");
            _dataInfoItem = Info(MenuText("Settings kept in " + PathUtil.Friendly(_session.Paths.Root) + " (this folder is read-only)"));
            _dataInfoItem.Visible = _session.Paths.UsesFallbackData;
            _findGameItem = Item("Choose the game folder...", ChooseGameFolder);

            var openLogs = Item("Open logs folder", OpenLogsFolder);
            openLogs.Font = _boldFont;   // the double-click action
            _openLastItem = Item("Open last log", OpenLastLog);
            _changeFolderItem = Item("Change logs folder...", ChangeLogsFolder);
            _defaultFolderItem = Item("Use the default logs folder", ResetLogsFolder);

            _notifyItem = Item("Notify when a log is saved", ToggleNotify);
            _autoUpdateItem = Item("Install updates automatically", ToggleAutoUpdate);
            var checkNow = Item("Check for updates now", () => { var _ = _updates.CheckAsync(manual: true); });
            _updateItem = new ToolStripMenuItem("Update available");
            _updateItem.DropDownItems.Add(Item("Install now", () => { var _ = _updates.InstallAsync(_updates.Available, manual: true); }));
            _updateItem.DropDownItems.Add(Item("What's new", () => Shell.Open(_updates.Available?.NotesUrl ?? AppInfo.ReleasesUrl)));
            _updateItem.DropDownItems.Add(Item("Skip this version", () => _updates.SkipAvailable()));
            _updateItem.Visible = false;
            _startupItem = Item("Start with Windows", ToggleStartWithWindows);
            _repairItem = Item("Repair now", RepairNow);

            // The mode names first: the README and docs/ARCHITECTURE.md call them Footer, Sidecar and Off.
            _sealItem = new ToolStripMenuItem("Battle log seal");
            _sealItem.DropDownItems.Add(SealOption("Footer", "Footer (in the log file, default)"));
            _sealItem.DropDownItems.Add(SealOption("Sidecar", "Sidecar (separate .seal file)"));
            _sealItem.DropDownItems.Add(SealOption("Off", "Off"));

            var diagnostics = new ToolStripMenuItem("Diagnostics");
            _bepInExLogItem = Item("Open BepInEx log", OpenBepInExLog);
            _gameFolderItem = Item("Open game folder", () => OpenFolder(_session.GameDir));
            diagnostics.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("Open app log", () => Shell.OpenText(Log.FilePath ?? _session.Paths.TrayLogFile)),
                _bepInExLogItem,
                _gameFolderItem,
                Item("Open app data folder", () => OpenFolder(_session.Paths.Root)),
                Item("Choose game folder...", ChooseGameFolder),
                Item("Copy diagnostic info", CopyDiagnostics),
            });

            var uninstall = Item(_session.Portable ? "Remove mod from game..." : "Uninstall...", Uninstall);

            menu.Items.AddRange(new ToolStripItem[]
            {
                title, _statusItem, _lastSavedItem, _logsInfoItem, _dataInfoItem, _findGameItem,
                new ToolStripSeparator(),
                openLogs, _openLastItem, _changeFolderItem, _defaultFolderItem,
                new ToolStripSeparator(),
                _notifyItem, _autoUpdateItem, checkNow, _updateItem, _startupItem, _repairItem, _sealItem, BuildTcgkbMenu(), diagnostics,
                new ToolStripSeparator(),
                Item("Project page", () => Shell.Open(AppInfo.ProjectUrl)),
                Item("View disclaimer", ViewDisclaimer),
                Item("Licenses", ViewLicenses),
                new ToolStripSeparator(),
                uninstall,
                Item("Exit", () => ExitThread()),
            });
            menu.Opening += (s, e) => Run("menu refresh", RefreshMenu);
            return menu;
        }

        private static ToolStripMenuItem Info(string text) => new ToolStripMenuItem(text) { Enabled = false };

        /// <summary>Text shown as is: a lone '&amp;' (an opponent's name, a folder) would become a mnemonic underline.</summary>
        private static string MenuText(string s) => (s ?? "").Replace("&", "&&");

        private ToolStripMenuItem Item(string text, Action onClick)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex)
                {
                    Log.Error("\"" + text + "\" failed", ex);
                    Balloon("Something went wrong", text.TrimEnd('.') + ": " + ex.Message, ToolTipIcon.Warning, null);
                }
            };
            return item;
        }

        private ToolStripMenuItem SealOption(string mode, string text)
        {
            var item = Item(text, () => SetSealMode(mode));
            item.Tag = mode;
            return item;
        }

        /// <summary>Everything that can change between two openings of the menu. Cheap: a few file reads.</summary>
        private void RefreshMenu()
        {
            var game = _session.Game;
            var r = _last;
            _statusItem.Text = MenuText(Clip(r?.Decision?.Summary ?? "Starting...", 90));
            _findGameItem.Visible = game == null;

            _lastSavedItem.Text = MenuText(Clip("Last saved: " + _lastSavedLabel, 90));
            _lastSavedItem.Visible = _lastSavedLabel != null;
            var folder = _session.LogsFolder(game);
            _logsInfoItem.Text = MenuText("Logs: " + Clip(PathUtil.Friendly(folder), 80));
            _openLastItem.Enabled = FindLastLog() != null;
            _changeFolderItem.Enabled = game != null;
            _defaultFolderItem.Visible = _session.HasCustomLogsFolder(game);

            _notifyItem.Checked = _session.Read(s => s.NotifyOnSave);
            _autoUpdateItem.Checked = _session.Read(s => s.AutoUpdate);
            try { _startupItem.Checked = WindowsIntegration.IsStartWithWindows(_session.Paths.InstalledExe); }
            catch (Exception) { _startupItem.Checked = false; }
            _repairItem.Enabled = game != null;
            RefreshUpdateItems();

            _sealItem.Enabled = game != null;
            var mode = game?.ReadSealMode() ?? GameInstall.DefaultSealMode;
            foreach (ToolStripMenuItem option in _sealItem.DropDownItems) option.Checked = (string)option.Tag == mode;

            _bepInExLogItem.Enabled = game != null && File.Exists(game.BepInExLogPath);
            _gameFolderItem.Enabled = game != null;
            RefreshTcgkbItems();
        }

        private void RefreshUpdateItems()
        {
            var m = _updates?.Available;
            _updateItem.Visible = m != null;
            if (m != null) _updateItem.Text = "Update v" + m.Version + " available";
        }

        // ---- Logs ------------------------------------------------------------------------------

        private void OpenLogsFolder()
        {
            var folder = GameSetup.EnsureLogsFolder(_session);
            OpenFolder(folder);
        }

        private void OpenFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Balloon(AppInfo.ProductName, "That folder does not exist: " + folder, ToolTipIcon.Warning, null);
                return;
            }
            Shell.Open(folder);
        }

        private void OpenLastLog()
        {
            var last = FindLastLog();
            if (last != null) Shell.OpenText(last);
            else Balloon(AppInfo.ProductName, "No battle log has been saved yet.", ToolTipIcon.Info, null);
        }

        /// <summary>The newest battle log: this session's, the plugin's last, or the newest in the folder.</summary>
        private string FindLastLog()
        {
            if (_lastSavedFile != null && File.Exists(_lastSavedFile)) return _lastSavedFile;
            var status = _last?.Status?.LastFile;
            if (!string.IsNullOrEmpty(status) && File.Exists(status)) return status;
            try
            {
                var folder = _session.LogsFolder(_session.Game);
                if (!Directory.Exists(folder)) return null;
                return new DirectoryInfo(folder).EnumerateFiles("*.txt")
                    .Where(f => LogFileName.TryParse(f.Name, out _, out _))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch (Exception) { return null; }
        }

        private void ChangeLogsFolder()
        {
            var game = _session.Game;
            if (game == null) { ChooseGameFolder(); return; }
            var current = _session.LogsFolder(game);
            string picked;
            using (Modal()) picked = Dialogs.PickFolder("Choose the folder where your battle logs are saved.", current, true);
            if (picked == null || PathUtil.Same(picked, current)) return;
            if (PathUtil.Same(picked, _session.Paths.DefaultLogsFolder)) { ResetLogsFolder(); return; }   // stored as "", never spelled out

            var appFolders = AppFolders();
            var unsafeFolder = game.WhyNotLogsFolder(picked, appFolders);
            if (unsafeFolder != null)
            {
                using (Modal())
                    Dialogs.Warning("This folder cannot hold your battle logs:\n" + picked + "\n\n" + char.ToUpperInvariant(unsafeFolder[0]) + unsafeFolder.Substring(1) +
                                    ", and your logs with it. Choose another folder, for example in Documents.");
                return;
            }
            try { game.WriteOutputFolder(picked, appFolders); }
            catch (ArgumentException)
            {
                using (Modal())
                    Dialogs.Warning("This folder cannot be used:\n" + picked + "\n\nThe mod loader (BepInEx) cannot keep a path with ' or \" in it, " +
                                    "or a network path (\\\\server\\share). Choose another folder, or map the network share to a drive letter.");
                return;
            }
            Log.Info("Logs folder set to " + picked);
            AfterLogsFolderChanged(game, picked);
        }

        /// <summary>Folders an uninstall deletes (or tells the player to delete): never a logs folder.</summary>
        private string[] AppFolders()
        {
            var folders = new List<string> { _session.Paths.LocalRoot, _session.Paths.Root };
            if (_session.Portable) folders.Add(Path.GetDirectoryName(_session.ExePath));
            return folders.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void ResetLogsFolder()
        {
            var game = _session.Game;
            if (game == null) return;
            game.ResetOutputFolder();
            Log.Info("Logs folder reset to the default.");
            AfterLogsFolderChanged(game, _session.Paths.DefaultLogsFolder);
        }

        private void AfterLogsFolderChanged(GameInstall game, string folder)
        {
            try { Directory.CreateDirectory(folder); }
            catch (Exception e) { Log.Error("Could not create " + folder, e); }
            if (!_session.Portable && File.Exists(WindowsIntegration.DesktopShortcut))
            {
                try { WindowsIntegration.CreateLogsShortcut(folder); }
                catch (Exception e) { Log.Error("Could not update the desktop shortcut", e); }
            }
            var running = _last?.GameRunning == true;
            Balloon("Logs folder changed",
                    "New battle logs go to " + PathUtil.Friendly(folder) + (running ? " from the next time you start the game." : "."),
                    ToolTipIcon.Info, () => OpenFolder(folder));
            _daemon.Poke();
        }

        // ---- Settings --------------------------------------------------------------------------

        private void ToggleNotify()
        {
            _session.Update(s => s.NotifyOnSave = !s.NotifyOnSave);
        }

        private void ToggleAutoUpdate()
        {
            bool on = false;
            _session.Update(s => on = s.AutoUpdate = !s.AutoUpdate);
            if (on && _updates.Available != null) { var _ = _updates.InstallAsync(_updates.Available, manual: true); }
        }

        private void ToggleStartWithWindows()
        {
            var exe = _session.Paths.InstalledExe;
            bool enable = !WindowsIntegration.IsStartWithWindows(exe);
            // A portable exe extracted from the downloaded zip still has its Mark of the Web: without
            // this, Windows would show "Open File - Security Warning" at every logon.
            if (enable) WindowsIntegration.Unblock(exe);
            WindowsIntegration.SetStartWithWindows(_session.RunCommand, enable);
            Log.Info("Start with Windows: " + (enable ? "on" : "off"));
        }

        private void SetSealMode(string mode)
        {
            var game = _session.Game;
            if (game == null) return;
            game.WriteSealMode(mode);
            Log.Info("Seal mode: " + mode);
            if (_last?.GameRunning == true)
                Balloon("Battle log seal: " + mode, "Takes effect the next time you start " + AppInfo.GameName + ".", ToolTipIcon.Info, null);
        }

        // ---- Game ------------------------------------------------------------------------------

        private async void RepairNow()
        {
            if (_session.Game == null) { ChooseGameFolder(); return; }
            _repairItem.Enabled = false;
            try
            {
                var (outcome, repaired, error) = await Task.Run(() =>
                {
                    var o = _daemon.RepairNow(out var pieces, out var err);
                    return (o, pieces, err);
                });
                switch (outcome)
                {
                    case GameSetup.RepairOutcome.Done:
                        Balloon("Mod repaired", "Put back: " + string.Join(", ", repaired) + ".", ToolTipIcon.Info, null);
                        break;
                    case GameSetup.RepairOutcome.NothingToDo:
                        Balloon("Nothing to repair", "The mod and BepInEx are in place.", ToolTipIcon.Info, null);
                        break;
                    case GameSetup.RepairOutcome.GameRunning:
                        Balloon("Close " + AppInfo.GameName + " first", "The mod is repaired while the game is closed.", ToolTipIcon.Warning, null);
                        break;
                    case GameSetup.RepairOutcome.NoGame:
                        ChooseGameFolder();
                        break;
                    case GameSetup.RepairOutcome.ConsentPending:
                        AskConsent();
                        break;
                    default:
                        Balloon("Repair failed", error + " Details are in the app log.", ToolTipIcon.Error, () => Shell.OpenText(Log.FilePath));
                        break;
                }
            }
            catch (Exception e) { Log.Error("Repair now failed", e); }
            finally { if (!_exiting) _repairItem.Enabled = true; }
        }

        private void ChooseGameFolder()
        {
            string dir;
            using (Modal()) dir = GameSetup.AskForFolder(_session.GameDir);
            if (dir == null) return;
            GameSetup.Adopt(_session, dir);
            _daemon.Poke();
            Balloon("Game folder set", dir, ToolTipIcon.Info, null);
        }

        private void OpenBepInExLog()
        {
            var game = _session.Game;
            if (game != null && File.Exists(game.BepInExLogPath)) Shell.OpenText(game.BepInExLogPath);
            else Balloon(AppInfo.ProductName, "There is no BepInEx log yet: it is written when the game starts with the mod.", ToolTipIcon.Info, null);
        }

        private void CopyDiagnostics()
        {
            var game = _session.Game;
            var r = _last;
            var sb = new StringBuilder();
            sb.AppendLine(AppInfo.ProductName + " " + AppInfo.Version + (_session.Portable ? " (portable" + (_session.PortableForced ? ", forced" : "") + ")" : " (installed)"));
            sb.AppendLine("Windows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " x64" : " x86") + ", .NET " + Environment.Version);
            sb.AppendLine("Exe: " + _session.ExePath);
            sb.AppendLine("Data: " + _session.Paths.Root);
            sb.AppendLine("Game: " + (game?.GameDir ?? "not found") + (r?.GameRunning == true ? " (running)" : ""));
            if (game != null)
            {
                sb.AppendLine("Missing: " + (r?.Missing == null || r.Missing.Count == 0 ? "nothing" : string.Join(", ", r.Missing)));
                sb.AppendLine("BepInEx added by this app: " + (_session.Read(s => s.BepInExInstalledByUs) ? "yes" : "no"));
                sb.AppendLine("Other BepInEx mods: " + (game.HasOtherPlugins() ? "yes" : "no"));
                sb.AppendLine("Seal mode: " + (game.ReadSealMode() ?? GameInstall.DefaultSealMode + " (default)"));
            }
            sb.AppendLine("Logs folder: " + _session.LogsFolder(game) + (_session.HasCustomLogsFolder(game) ? "" : " (default)"));
            sb.AppendLine("Status: " + (r?.Decision?.Summary ?? "?") + " [" + (r?.Decision?.Health.ToString() ?? "?") + "]");
            sb.AppendLine("Auto update: " + _session.Read(s => s.AutoUpdate) + ", last check " + (_session.Read(s => s.LastUpdateCheckUtc) ?? "never") +
                          (_updates.Available != null ? ", v" + _updates.Available.Version + " available" : ""));
            var status = PluginStatus.TryRead(_session.Paths.PluginStatusFile);
            sb.AppendLine("Plugin: " + (status == null ? "no status file"
                : "v" + status.PluginVersion + ", " + status.State + ", heartbeat " + status.HeartbeatUtc +
                  (status.IsAlive(DateTime.UtcNow) ? " (alive)" : " (stale)") + ", " + status.SavedThisSession + " saved, " + status.Message));
            sb.AppendLine("Generated " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            try
            {
                Clipboard.SetDataObject(sb.ToString(), true, 5, 100);
                Balloon("Diagnostic info copied", "Paste it into your bug report.", ToolTipIcon.Info, null);
            }
            catch (Exception e)
            {
                Log.Error("Clipboard unavailable", e);
                Balloon("Could not copy", "The clipboard is busy. Try again.", ToolTipIcon.Warning, null);
            }
        }

        // ---- App -------------------------------------------------------------------------------

        private void ViewDisclaimer()
        {
            using (Modal()) DisclaimerDialog.Ask(DisclaimerDialog.Purpose.View);
        }

        /// <summary>This app's license and the notices of BepInEx and the other components the exe carries.</summary>
        private void ViewLicenses()
        {
            using (Modal()) DisclaimerDialog.Ask(DisclaimerDialog.Purpose.Licenses);
        }

        /// <summary>
        /// Runs this exe with --uninstall, as Apps &amp; Features would: that process asks, waits for the
        /// game, then tells this tray to exit before it touches anything. One code path for both.
        /// </summary>
        private void Uninstall()
        {
            var exe = _session.Paths.InstalledExe;
            var args = _session.Portable && _session.PortableForced
                ? CommandLine.Join(new[] { CommandLine.UninstallFlag, CommandLine.PortableFlag })
                : CommandLine.UninstallFlag;
            using (Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() })) { }
        }
    }
}
