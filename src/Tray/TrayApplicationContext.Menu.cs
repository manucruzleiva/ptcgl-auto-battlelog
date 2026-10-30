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

        private ToolStripMenuItem _statusItem, _modVersionItem, _lastSavedItem, _logsInfoItem, _dataInfoItem, _findGameItem;
        private ToolStripMenuItem _openLastItem, _changeFolderItem, _defaultFolderItem;
        private ToolStripMenuItem _notifyItem, _autoUpdateItem, _updateItem, _startupItem, _repairItem;
        private ToolStripMenuItem _sealItem, _bepInExLogItem, _gameFolderItem, _channelItem;
        private ToolStripMenuItem _openLogsItem, _settingsMenu, _updatesMenu, _helpMenu;
        private StatusWindow _window;
        private StatusWindow.Match[] _matches = new StatusWindow.Match[0];
        private string _matchesKey;
        private ToolStripMenuItem _consentItem;

        /// <summary>A menu item that needs the player: the updated disclaimer. Windows' own error red.</summary>
        private static readonly Color AttentionColor = Color.FromArgb(196, 43, 28);

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
            _boldFont = new Font(menu.Font, FontStyle.Bold);

            var title = Info(MenuText(AppInfo.ProductName + " v" + AppInfo.Version + (_session.Portable ? " (portable)" : "")));
            title.Font = _boldFont;
            _statusItem = Info("Starting...");
            _modVersionItem = Info("");   // the window's header only: the menu stays short
            _lastSavedItem = Info("");
            _logsInfoItem = Info("");
            _dataInfoItem = Info(MenuText("Settings kept in " + PathUtil.Friendly(_session.Paths.Root) + " (this folder is read-only)"));
            _dataInfoItem.Visible = _session.Paths.UsesFallbackData;
            _findGameItem = Item("Choose the game folder...", ChooseGameFolder);
            // Shown only while the current disclaimer is not accepted: the balloon that offers it is easy to miss.
            _consentItem = Item("Review the updated disclaimer...", AskConsent);
            _consentItem.Font = _boldFont;
            _consentItem.ForeColor = AttentionColor;
            _consentItem.Visible = false;

            var openLogs = _openLogsItem = Item("Open logs folder", OpenLogsFolder);
            openLogs.Font = _boldFont;   // the double-click action
            _openLastItem = Item("Open last log", OpenLastLog);
            _changeFolderItem = Item("Change logs folder...", ChangeLogsFolder);
            _defaultFolderItem = Item("Use the default logs folder", ResetLogsFolder);

            _notifyItem = Toggle("Notify when a log is saved", ToggleNotify);
            _autoUpdateItem = Toggle("Install updates automatically", ToggleAutoUpdate);
            var checkNow = Item("Check for updates now", () => { var _ = _updates.CheckAsync(manual: true); });
            _updateItem = new ToolStripMenuItem("Update available");
            _updateItem.DropDownItems.Add(Item("Install now", () => { var _ = _updates.InstallAsync(_updates.Available, manual: true); }));
            _updateItem.DropDownItems.Add(Item("What's new", () => Shell.Open(_updates.Available?.NotesUrl ?? AppInfo.ReleasesUrl)));
            _updateItem.DropDownItems.Add(Item("Skip this version", () => _updates.SkipAvailable()));
            _updateItem.Visible = false;
            _channelItem = new ToolStripMenuItem("Update channel");
            _channelItem.DropDownItems.Add(ChannelOption(Settings.StableChannel, "Stable (recommended)"));
            _channelItem.DropDownItems.Add(ChannelOption(Settings.BetaChannel, "Beta (test versions before release)"));
            _startupItem = Toggle("Start with Windows", ToggleStartWithWindows);
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

            // Only what is used often stays at the top: the rest is grouped in three submenus.
            var settings = _settingsMenu = Submenu("Settings", true,
                _notifyItem, _startupItem,
                new ToolStripSeparator(),
                _logsInfoItem, _changeFolderItem, _defaultFolderItem,
                new ToolStripSeparator(),
                _sealItem);
            var updates = _updatesMenu = Submenu("Updates", true, _autoUpdateItem, _channelItem, new ToolStripSeparator(), checkNow);
            var help = _helpMenu = Submenu("Help", false,
                diagnostics, _repairItem,
                new ToolStripSeparator(),
                Item("Project page", () => Shell.Open(AppInfo.ProjectUrl)),
                Item("View disclaimer", ViewDisclaimer),
                Item("Licenses", ViewLicenses),
                new ToolStripSeparator(),
                uninstall);

            menu.Items.AddRange(new ToolStripItem[]
            {
                title, _statusItem, _consentItem, _lastSavedItem, _dataInfoItem, _findGameItem,
                new ToolStripSeparator(),
                openLogs, _openLastItem, _updateItem,
                new ToolStripSeparator(),
                BuildTcgkbMenu(), BuildPokematchMenu(),
                new ToolStripSeparator(),
                Item("Status and settings...", ShowWindow), settings, updates, help,
                new ToolStripSeparator(),
                Item("Exit", () => ExitThread()),
            });
            menu.Opening += (s, e) => Run("menu refresh", RefreshMenu);
            return menu;
        }

        /// <summary>A submenu; checkMargin for one whose items are ticked on and off.</summary>
        private static ToolStripMenuItem Submenu(string text, bool checkMargin, params ToolStripItem[] items)
        {
            var item = new ToolStripMenuItem(text);
            item.DropDownItems.AddRange(items);
            if (item.DropDown is ToolStripDropDownMenu dropDown) { dropDown.ShowImageMargin = false; dropDown.ShowCheckMargin = checkMargin; }
            return item;
        }

        private static ToolStripMenuItem Info(string text) => new ToolStripMenuItem(text) { Enabled = false, Tag = StatusWindow.InfoTag };

        /// <summary>An item that is ticked on and off (RefreshMenu sets Checked); a check box in the window.</summary>
        private ToolStripMenuItem Toggle(string text, Action onClick)
        {
            var item = Item(text, onClick);
            item.Tag = StatusWindow.ToggleTag;
            return item;
        }

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

        private ToolStripMenuItem ChannelOption(string channel, string text)
        {
            var item = Item(text, () => SetUpdateChannel(channel));
            item.Tag = channel;
            return item;
        }

        /// <summary>Everything that can change between two openings of the menu. Cheap: a few file reads.</summary>
        private void RefreshMenu()
        {
            var game = _session.Game;
            var r = _last;
            _statusItem.Text = MenuText(Clip(r?.Decision?.Summary ?? "Starting...", 90));
            _modVersionItem.Text = MenuText(PluginStatus.ModVersionLine(r?.Status, AppInfo.Version, DateTime.UtcNow));
            _findGameItem.Visible = game == null;
            _consentItem.Visible = !HasConsent();

            _lastSavedItem.Text = MenuText(Clip("Last saved: " + _lastSavedLabel, 90));
            _lastSavedItem.Visible = _lastSavedLabel != null;
            var folder = _session.LogsFolder(game);
            _logsInfoItem.Text = MenuText("Logs: " + Clip(PathUtil.Friendly(folder), 80));
            _openLastItem.Enabled = FindLastLog() != null;
            _changeFolderItem.Enabled = game != null;
            _defaultFolderItem.Visible = _session.HasCustomLogsFolder(game);

            _notifyItem.Checked = _session.Read(s => s.NotifyOnSave);
            _autoUpdateItem.Checked = _session.Read(s => s.AutoUpdate);
            var channel = _session.Read(s => s.BetaUpdates) ? Settings.BetaChannel : Settings.StableChannel;
            foreach (ToolStripMenuItem option in _channelItem.DropDownItems) option.Checked = (string)option.Tag == channel;
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
            RefreshPokematchItems();
        }

        private void RefreshUpdateItems()
        {
            var m = _updates?.Available;
            _updateItem.Visible = m != null;
            if (m != null) _updateItem.Text = "Update v" + m.Version + " available";
        }

        // ---- Window ----------------------------------------------------------------------------

        /// <summary>The status and settings window: the same items as the menu, laid out in pages, with the matches and the tcgkb ladder.</summary>
        private void ShowWindow()
        {
            if (_exiting) return;
            if (_window == null || _window.IsDisposed)
            {
                RefreshMenu();
                _window = new StatusWindow(
                    new StatusWindow.Items
                    {
                        Version = "Version " + AppInfo.Version + (_session.Portable ? " · portable" : ""),
                        Status = _statusItem,
                        Update = _updateItem,
                        StatusLines = new ToolStripItem[] { _modVersionItem, _lastSavedItem, _dataInfoItem },
                        Actions = new[] { _consentItem, _findGameItem, _openLogsItem, _openLastItem },
                        Tcgkb = _tcgkbItem,
                        TcgkbConnect = _tcgkbConnectItem,
                        TcgkbAutoUpload = _tcgkbAutoUploadItem,
                        TcgkbLastUpload = _tcgkbLastUploadItem,
                        Settings = _settingsMenu.DropDownItems.Cast<ToolStripItem>().Concat(new ToolStripItem[] { new ToolStripSeparator(), _pokematchItem }).ToArray(),
                        Updates = new ToolStripItem[] { _updateItem }.Concat(_updatesMenu.DropDownItems.Cast<ToolStripItem>()).ToArray(),
                        Help = _helpMenu.DropDownItems.Cast<ToolStripItem>().ToArray(),
                    },
                    () => _last?.Decision?.Health ?? TrayHealth.Idle,
                    WindowData,
                    item =>
                    {
                        item.PerformClick();
                        Post(RefreshWindow);   // after the action, which may have changed what the window shows
                    },
                    path => Shell.OpenText(path),
                    url => Shell.Open(url));
                _window.FormClosed += (s, e) => _window = null;   // a closed modeless form disposes itself
                _window.Activated += (s, e) => RefreshTcgkbStanding(false);
            }
            _window.Raise();
        }

        private StatusWindow.Data WindowData()
        {
            var user = Tcgkb.Connection.UserName;
            return new StatusWindow.Data { UserName = user, Me = user == null ? null : _tcgkbMe, Matches = LocalMatches() };
        }

        /// <summary>The battle logs in the logs folder, newest first; read again only when the folder changed.</summary>
        private IReadOnlyList<StatusWindow.Match> LocalMatches()
        {
            try
            {
                var folder = _session.LogsFolder(_session.Game);
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists) return _matches = new StatusWindow.Match[0];
                var key = folder + "|" + dir.LastWriteTimeUtc.Ticks;   // a file added, renamed or deleted changes the folder's time
                if (key == _matchesKey) return _matches;
                _matches = dir.EnumerateFiles("*.txt")
                    .Select(f => LogFileName.TryParse(f.Name, out var result, out var opponent, out var time)
                        ? new StatusWindow.Match { Result = result, Opponent = opponent, LocalTime = time, Path = f.FullName } : null)
                    .Where(m => m != null)
                    .OrderByDescending(m => m.LocalTime)
                    .ToArray();
                _matchesKey = key;
            }
            catch (Exception e) { Log.Error("Could not list the battle logs", e); }
            return _matches;
        }

        /// <summary>Refreshes an open window (and the menu items it mirrors). Cheap enough for every daemon report.</summary>
        private void RefreshWindow()
        {
            if (_window == null || _window.IsDisposed || !_window.Visible) return;
            RefreshMenu();
            _window.Sync();
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

        private void SetUpdateChannel(string channel)
        {
            bool beta = channel == Settings.BetaChannel;
            if (_session.Read(s => s.BetaUpdates) == beta) return;
            _session.Update(s => s.UpdateChannel = channel);
            Log.Info("Update channel: " + channel);
            if (beta)
                Balloon("Beta updates on", "You'll get test versions before they are released. They may have bugs; switch back to Stable any time.", ToolTipIcon.Info, null);
            else
                Balloon("Stable updates", "A test version you already have stays until the next stable release, which replaces it.", ToolTipIcon.Info, null);
            var _ = _updates.CheckAsync(manual: false);
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
            sb.AppendLine("Auto update: " + _session.Read(s => s.AutoUpdate) + ", channel " + (_session.Read(s => s.BetaUpdates) ? "beta" : "stable") + ", last check " + (_session.Read(s => s.LastUpdateCheckUtc) ?? "never") +
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

        /// <summary>The accepted terms, read-only; while an updated version waits, that one with its Accept button.</summary>
        private void ViewDisclaimer()
        {
            if (!HasConsent()) { AskConsent(); return; }
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
