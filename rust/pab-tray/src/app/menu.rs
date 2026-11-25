//! The tray menu and what each item does (TrayApplicationContext.Menu.cs). Built fresh at every
//! opening, so it always shows the current state.
use super::accounts::AccountAction;
use super::{Click, Tray};
use crate::game_setup::{self, RepairOutcome};
use crate::win::dialogs;
use crate::win::notify_icon::BalloonIcon;
use crate::win::popup::{self, Item};
use crate::win::{registry, shell};
use crate::{log, GAME_NAME, PRODUCT_NAME, VERSION};
use pab_core::game_install::{GameInstall, InstallError, DEFAULT_SEAL_MODE, SEAL_MODES};
use pab_core::log_file_name;
use pab_core::plugin_status::PluginStatus;
use pab_core::settings::{BETA_CHANNEL, STABLE_CHANNEL};
use std::path::{Path, PathBuf};
use std::rc::Rc;

#[derive(Clone, Copy, PartialEq)]
pub(super) enum Action {
    OpenLogs,
    OpenLast,
    ChooseGameFolder,
    InstallUpdate,
    WhatsNew,
    SkipUpdate,
    ToggleNotify,
    ToggleStartWithWindows,
    ChangeLogsFolder,
    ResetLogsFolder,
    WipeLogs,
    SealMode(&'static str),
    ToggleAutoUpdate,
    Channel(&'static str),
    CheckUpdates,
    OpenAppLog,
    OpenBepInExLog,
    OpenGameFolder,
    OpenDataFolder,
    CopyDiagnostics,
    Repair,
    ProjectPage,
    Account(AccountAction),
    Uninstall,
    Consent,
    ShowWindow,
    ViewDisclaimer,
    Licenses,
    Exit,
}

/// Collects items, giving each action an id.
#[derive(Default)]
pub(super) struct Builder {
    pub actions: Vec<Action>,
}

impl Builder {
    fn id(&mut self, a: Action) -> u32 {
        self.actions.push(a);
        self.actions.len() as u32
    }
    fn item(&mut self, text: impl Into<String>, a: Action) -> Item {
        Item::action(self.id(a), text)
    }
    fn check(&mut self, text: impl Into<String>, on: bool, a: Action) -> Item {
        Item::check(self.id(a), text, on)
    }
    fn enabled(&mut self, text: impl Into<String>, enabled: bool, a: Action) -> Item {
        Item::Action { id: self.id(a), text: text.into(), checked: false, enabled, default: false }
    }
}

/// The menu's items, also laid out by the status window.
pub(super) struct Parts {
    pub consent: Option<Item>,
    pub choose_game: Option<Item>,
    pub open_logs: Item,
    pub open_last: Item,
    /// Under the status: last saved, where settings are kept.
    pub lines: Vec<String>,
    pub update: Option<Item>,
    /// The tcgkb and Pokematch submenus.
    pub accounts: [Item; 2],
    pub settings: Vec<Item>,
    pub updates: Vec<Item>,
    pub help: Vec<Item>,
}

impl Tray {
    pub(super) fn show_menu(&self) {
        let s = &self.session;
        let last = self.last.borrow().clone();
        let mut b = Builder::default();
        let p = self.menu_parts(&mut b);

        let mut items = vec![
            Item::label(format!("{PRODUCT_NAME} v{VERSION}{}", if s.portable() { " (portable)" } else { "" })),
            Item::label(crate::clip(last.as_ref().map_or("Starting...", |r| r.decision.summary.as_str()), 90)),
        ];
        items.extend(p.consent);
        items.extend(p.lines.iter().map(|l| Item::label(crate::clip(l, 90))));
        items.extend(p.choose_game);
        items.extend([
            Item::Separator,
            // What a click on the tray icon opens.
            Item::Action { id: b.id(Action::ShowWindow), text: "Status and settings...".into(), checked: false, enabled: true, default: true },
            p.open_logs,
            p.open_last,
        ]);
        items.extend(p.update);
        items.push(Item::Separator);
        items.extend(p.accounts);
        items.extend([
            Item::Separator,
            Item::Sub { text: "Settings".into(), items: p.settings },
            Item::Sub { text: "Updates".into(), items: p.updates },
            Item::Sub { text: "Help".into(), items: p.help },
            Item::Separator,
            b.item("Exit", Action::Exit),
        ]);

        let hwnd = self.icon.borrow().hwnd;
        let chosen = popup::show(hwnd, &items).and_then(|id| b.actions.get(id as usize - 1).cloned());
        if let Some(a) = chosen {
            self.run(a);
        }
    }

    pub(super) fn menu_parts(&self, b: &mut Builder) -> Parts {
        let s = &self.session;
        let game = s.game();
        let consent = (!s.consent()).then(|| Item::bold(b.id(Action::Consent), "Review the disclaimer..."));
        let mut lines = Vec::new();
        if let Some((_, label)) = self.last_saved.borrow().as_ref() {
            lines.push(format!("Last saved: {label}"));
        }
        if s.paths().uses_fallback_data {
            lines.push(format!("Settings kept in {} (this folder is read-only)", shell::friendly(&s.paths().root)));
        }
        let choose_game = game.is_none().then(|| b.item("Choose the game folder...", Action::ChooseGameFolder));
        let has_last = self.find_last_log().is_some();
        let open_logs = b.item("Open logs folder", Action::OpenLogs);
        let open_last = b.enabled("Open last log", has_last, Action::OpenLast);
        let update = self.updates.as_ref().and_then(|u| u.available()).map(|m| Item::Sub {
            text: format!("Update v{} available", m.version),
            items: vec![b.item("Install now", Action::InstallUpdate), b.item("What's new", Action::WhatsNew), b.item("Skip this version", Action::SkipUpdate)],
        });

        let accounts = self.account_menus(&mut |text, enabled, checked, a| Item::Action {
            id: b.id(Action::Account(a)),
            text: text.into(),
            checked,
            enabled,
            default: false,
        });

        let folder = s.logs_folder(game.as_ref());
        let mut settings = vec![
            b.check("Notify when a log is saved", s.read(|x| x.notify_on_save), Action::ToggleNotify),
            b.check("Start with Windows", registry::is_start_with_windows(&s.paths().installed_exe), Action::ToggleStartWithWindows),
            Item::Separator,
            Item::label(format!("Logs: {}", crate::clip(&shell::friendly(&folder), 80))),
            b.enabled("Change logs folder...", game.is_some(), Action::ChangeLogsFolder),
        ];
        if s.has_custom_logs_folder(game.as_ref()) {
            settings.push(b.item("Use the default logs folder", Action::ResetLogsFolder));
        }
        settings.push(b.enabled("Delete local battle logs...", has_last, Action::WipeLogs));
        settings.push(Item::Separator);
        let mode = game.as_ref().and_then(GameInstall::read_seal_mode).unwrap_or(DEFAULT_SEAL_MODE);
        let seal_text = |m: &str| match m {
            "Footer" => "Footer (in the log file, default)",
            "Sidecar" => "Sidecar (separate .seal file)",
            _ => "Off",
        };
        let seal: Vec<Item> = SEAL_MODES.iter().map(|m| b.check(seal_text(m), *m == mode, Action::SealMode(m))).collect();
        settings.push(if game.is_some() { Item::Sub { text: "Battle log seal".into(), items: seal } } else { Item::label("Battle log seal") });

        let beta = s.read(|x| x.beta_updates());
        let updates = vec![
            b.check("Install updates automatically", s.read(|x| x.auto_update), Action::ToggleAutoUpdate),
            Item::Sub {
                text: "Update channel".into(),
                items: vec![
                    b.check("Stable (recommended)", !beta, Action::Channel(STABLE_CHANNEL)),
                    b.check("Beta (test versions before release)", beta, Action::Channel(BETA_CHANNEL)),
                ],
            },
            Item::Separator,
            b.item("Check for updates now", Action::CheckUpdates),
        ];

        let bepinex_log = game.as_ref().is_some_and(|g| g.bepinex_log_path.is_file());
        let help = vec![
            Item::Sub {
                text: "Diagnostics".into(),
                items: vec![
                    b.item("Open app log", Action::OpenAppLog),
                    b.enabled("Open BepInEx log", bepinex_log, Action::OpenBepInExLog),
                    b.enabled("Open game folder", game.is_some(), Action::OpenGameFolder),
                    b.item("Open app data folder", Action::OpenDataFolder),
                    b.item("Choose game folder...", Action::ChooseGameFolder),
                    b.item("Copy diagnostic info", Action::CopyDiagnostics),
                ],
            },
            b.enabled("Repair now", game.is_some(), Action::Repair),
            Item::Separator,
            b.item("Project page", Action::ProjectPage),
            b.item("View disclaimer", Action::ViewDisclaimer),
            b.item("Licenses", Action::Licenses),
            Item::Separator,
            b.item(if s.portable() { "Remove mod from game..." } else { "Uninstall..." }, Action::Uninstall),
        ];
        Parts { consent, choose_game, open_logs, open_last, lines, update, accounts, settings, updates, help }
    }


    pub(super) fn run(&self, a: Action) {
        let s = &self.session;
        match a {
            Action::OpenLogs => self.open_folder(&game_setup::ensure_logs_folder(s)),
            Action::OpenLast => match self.find_last_log() {
                Some(p) => {
                    shell::open_text(&p);
                }
                None => self.balloon(PRODUCT_NAME, "No battle log has been saved yet.", BalloonIcon::Info, None),
            },
            Action::ChooseGameFolder => self.choose_game_folder(),
            Action::InstallUpdate => {
                if let Some(u) = &self.updates {
                    u.install_available();
                }
            }
            Action::WhatsNew => {
                let url = self.updates.as_ref().and_then(|u| u.available()).and_then(|m| m.notes_url);
                shell::open(&url.unwrap_or_else(|| format!("{}/releases", crate::project_url())));
            }
            Action::SkipUpdate => {
                if let Some(u) = &self.updates {
                    u.skip_available(s);
                }
            }
            Action::ToggleNotify => s.update(|x| x.notify_on_save = !x.notify_on_save),
            Action::ToggleStartWithWindows => {
                let exe = s.paths().installed_exe;
                let enable = !registry::is_start_with_windows(&exe);
                // A portable exe from the downloaded zip still has its Mark of the Web: without
                // this, Windows would show "Open File - Security Warning" at every logon.
                if enable {
                    crate::win::process::unblock(&exe);
                }
                let command = s.run_command();
                match registry::set_start_with_windows(enable.then_some(command.as_str())) {
                    Ok(()) => log::info(&format!("Start with Windows: {}", if enable { "on" } else { "off" })),
                    Err(e) => self.failed("Start with Windows", &e.to_string()),
                }
            }
            Action::ChangeLogsFolder => self.change_logs_folder(),
            Action::ResetLogsFolder => {
                if let Some(g) = s.game() {
                    match g.reset_output_folder() {
                        Ok(()) => {
                            log::info("Logs folder reset to the default.");
                            self.after_logs_folder_changed(&s.paths().default_logs_folder);
                        }
                        Err(e) => self.failed("Use the default logs folder", &e.to_string()),
                    }
                }
            }
            Action::WipeLogs => self.wipe_logs(),
            Action::SealMode(mode) => {
                if let Some(g) = s.game() {
                    if let Err(e) = g.write_seal_mode(mode) {
                        return self.failed("Battle log seal", &e.to_string());
                    }
                    log::info(&format!("Seal mode: {mode}"));
                    if self.last.borrow().as_ref().is_some_and(|r| r.game_running) {
                        self.balloon(&format!("Battle log seal: {mode}"), &format!("Takes effect the next time you start {GAME_NAME}."), BalloonIcon::Info, None);
                    }
                }
            }
            Action::ToggleAutoUpdate => {
                let mut on = false;
                s.update(|x| {
                    x.auto_update = !x.auto_update;
                    on = x.auto_update;
                });
                if on {
                    if let Some(u) = self.updates.as_ref().filter(|u| u.available().is_some()) {
                        u.install_available();
                    }
                }
            }
            Action::Channel(channel) => {
                let beta = channel == BETA_CHANNEL;
                if s.read(|x| x.beta_updates()) == beta {
                    return;
                }
                s.update(|x| x.update_channel = Some(channel.to_string()));
                log::info(&format!("Update channel: {channel}"));
                if beta {
                    self.balloon("Beta updates on", "You'll get test versions before they are released. They may have bugs; switch back to Stable any time.", BalloonIcon::Info, None);
                } else {
                    self.balloon("Stable updates", "A test version you already have stays until the next stable release, which replaces it.", BalloonIcon::Info, None);
                }
                if let Some(u) = &self.updates {
                    u.check_soon();
                }
            }
            Action::CheckUpdates => {
                if let Some(u) = &self.updates {
                    u.check_now();
                }
            }
            Action::OpenAppLog => {
                shell::open_text(&log::file_path().unwrap_or_else(|| s.paths().tray_log_file));
            }
            Action::OpenBepInExLog => self.open_bepinex_log(),
            Action::OpenGameFolder => {
                if let Some(d) = s.game_dir() {
                    self.open_folder(Path::new(&d));
                }
            }
            Action::OpenDataFolder => self.open_folder(&s.paths().root),
            Action::CopyDiagnostics => self.copy_diagnostics(),
            Action::Repair => self.repair_now(),
            Action::ProjectPage => {
                shell::open(&crate::project_url());
            }
            Action::Account(a) => self.run_account(a),
            Action::Consent => self.ask_consent(),
            Action::ShowWindow => self.show_window(),
            Action::ViewDisclaimer if !s.consent() => self.ask_consent(),
            Action::ViewDisclaimer => {
                self.window(|_| crate::ui::show_text("Disclaimer", &format!("The terms you accepted when you installed {PRODUCT_NAME}."), crate::embedded::disclaimer_text()));
            }
            Action::Licenses => {
                self.window(|_| {
                    let intro = format!("{PRODUCT_NAME} is free software under the MIT License. It carries BepInEx and the other components below, which keep their own licenses.");
                    crate::ui::show_text("Licenses", &intro, crate::embedded::licenses_text())
                });
            }
            Action::Uninstall => self.uninstall(),
            Action::Exit => self.exit(),
        }
    }

    /// An item failed: logged, and said in a balloon.
    fn failed(&self, what: &str, error: &str) {
        log::error(&format!("\"{what}\" failed"), error);
        self.balloon("Something went wrong", &format!("{}: {error}", what.trim_end_matches('.')), BalloonIcon::Warning, None);
    }

    pub(super) fn owner(&self) -> windows_sys::Win32::Foundation::HWND {
        self.icon.borrow().hwnd
    }

    pub(super) fn open_folder(&self, folder: &Path) {
        if folder.as_os_str().is_empty() || !folder.is_dir() {
            self.balloon(PRODUCT_NAME, &format!("That folder does not exist: {}", folder.display()), BalloonIcon::Warning, None);
            return;
        }
        shell::open(&folder.to_string_lossy());
    }

    pub(super) fn open_logs_folder(&self) {
        self.open_folder(&game_setup::ensure_logs_folder(&self.session));
    }

    pub(super) fn open_bepinex_log(&self) {
        match self.session.game().map(|g| g.bepinex_log_path).filter(|p| p.is_file()) {
            Some(p) => {
                shell::open_text(&p);
            }
            None => self.balloon(PRODUCT_NAME, "There is no BepInEx log yet: it is written when the game starts with the mod.", BalloonIcon::Info, None),
        }
    }

    /// The newest battle log: this session's, the plugin's last, or the newest in the folder.
    fn find_last_log(&self) -> Option<PathBuf> {
        if let Some((p, _)) = self.last_saved.borrow().as_ref().filter(|(p, _)| p.is_file()) {
            return Some(p.clone());
        }
        let from_status = self.last.borrow().as_ref().and_then(|r| r.status.as_ref()).and_then(|s| s.last_file.clone()).map(PathBuf::from);
        if let Some(p) = from_status.filter(|p| p.is_file()) {
            return Some(p);
        }
        let s = &self.session;
        battle_logs(&s.logs_folder(s.game().as_ref())).into_iter().max_by_key(|p| std::fs::metadata(p).and_then(|m| m.modified()).ok())
    }

    fn choose_game_folder(&self) {
        let dir = self.modal(|| crate::setup::ask_for_game_folder(self.owner(), self.session.game_dir().as_deref().map(Path::new)));
        let Some(dir) = dir else { return };
        game_setup::adopt(&self.session, &dir);
        self.daemon.poke();
        self.balloon("Game folder set", &dir.to_string_lossy(), BalloonIcon::Info, None);
    }

    fn app_folders(&self) -> Vec<PathBuf> {
        let p = self.session.paths();
        let mut folders = vec![p.local_root, p.root];
        if p.is_portable {
            folders.extend(self.session.exe_path.parent().map(Path::to_path_buf));
        }
        folders.dedup_by(|a, b| registry::same_path(&a.to_string_lossy(), &b.to_string_lossy()));
        folders
    }

    fn change_logs_folder(&self) {
        let s = &self.session;
        let Some(game) = s.game() else { return self.choose_game_folder() };
        let current = s.logs_folder(Some(&game));
        let picked = self.modal(|| dialogs::pick_folder(self.owner(), "Choose the folder where your battle logs are saved.", Some(&current), true));
        let Some(picked) = picked else { return };
        let same = |a: &Path, b: &Path| registry::same_path(&a.to_string_lossy(), &b.to_string_lossy());
        if same(&picked, &current) {
            return;
        }
        if same(&picked, &s.paths().default_logs_folder) {
            return self.run(Action::ResetLogsFolder); // stored as "", never spelled out
        }
        let folders = self.app_folders();
        let refs: Vec<&Path> = folders.iter().map(PathBuf::as_path).collect();
        let picked_s = picked.to_string_lossy();
        if let Some(why) = game.why_not_logs_folder(&picked_s, &refs) {
            let mut why = why;
            why[..1].make_ascii_uppercase();
            self.modal(|| dialogs::warning(self.owner(), &format!("This folder cannot hold your battle logs:\n{picked_s}\n\n{why}, and your logs with it. Choose another folder, for example in Documents.")));
            return;
        }
        match game.write_output_folder(&picked_s, &refs) {
            Ok(()) => {
                log::info(&format!("Logs folder set to {picked_s}"));
                self.after_logs_folder_changed(&picked);
            }
            Err(InstallError::Invalid(_)) => self.modal(|| {
                dialogs::warning(
                    self.owner(),
                    &format!("This folder cannot be used:\n{picked_s}\n\nThe mod loader (BepInEx) cannot keep a path with ' or \" in it, or a network path (\\\\server\\share). Choose another folder, or map the network share to a drive letter."),
                )
            }),
            Err(e) => self.failed("Change logs folder", &e.to_string()),
        }
    }

    fn after_logs_folder_changed(&self, folder: &Path) {
        if let Err(e) = std::fs::create_dir_all(folder) {
            log::error(&format!("Could not create {}", folder.display()), e);
        }
        // shortcut: the desktop shortcut to the logs folder is refreshed once the installer (3c) makes it.
        let running = self.last.borrow().as_ref().is_some_and(|r| r.game_running);
        let f = folder.to_path_buf();
        self.balloon(
            "Logs folder changed",
            &format!("New battle logs go to {}{}", shell::friendly(folder), if running { " from the next time you start the game." } else { "." }),
            BalloonIcon::Info,
            Some(Rc::new(move |t: &Tray| t.open_folder(&f)) as Click),
        );
        self.daemon.poke();
    }

    /// Moves every battle log in the logs folder (and its .seal sidecar) to the Recycle Bin, after
    /// asking. Other files there, and the decks, stay.
    fn wipe_logs(&self) {
        let s = &self.session;
        let folder = s.logs_folder(s.game().as_ref());
        let logs = battle_logs(&folder);
        if logs.is_empty() {
            return self.balloon(PRODUCT_NAME, "There are no battle logs to delete.", BalloonIcon::Info, None);
        }
        let n = logs.len();
        let text = format!(
            "Move the {n} {} in {} to the Recycle Bin?\n\nMatches already on tcgkb or Pokematch stay there. A log that was not uploaded is only on this PC: once you empty the Recycle Bin it is gone.",
            if n == 1 { "battle log" } else { "battle logs" },
            shell::friendly(&folder)
        );
        if !self.modal(|| dialogs::confirm(self.owner(), &text, true)) {
            return;
        }
        let mut files = logs.clone();
        files.extend(logs.iter().map(|l| l.with_extension("seal")).filter(|p| p.is_file()));
        let done = shell::recycle(&files, self.owner());
        let left = logs.iter().filter(|p| p.is_file()).count();
        log::info(&format!("Deleted local battle logs: {} moved to the Recycle Bin{}.", n - left, if left > 0 { format!(", {left} left") } else { String::new() }));
        *self.last_saved.borrow_mut() = None;
        if done && left == 0 {
            self.balloon("Battle logs deleted", &format!("{n} moved to the Recycle Bin."), BalloonIcon::Info, None);
        } else {
            self.balloon(
                "Some battle logs were not deleted",
                &format!("{left} are still in the logs folder (in use, or the deletion was cancelled)."),
                BalloonIcon::Warning,
                Some(Rc::new(move |t: &Tray| t.open_folder(&folder)) as Click),
            );
        }
    }

    fn copy_diagnostics(&self) {
        let s = &self.session;
        let game = s.game();
        let last = self.last.borrow().clone();
        let p = s.paths();
        let mut lines = vec![
            format!("{PRODUCT_NAME} {VERSION}{}", if s.portable() { format!(" (portable{})", if s.portable_forced { ", forced" } else { "" }) } else { " (installed)".into() }),
            format!("Exe: {}", s.exe_path.display()),
            format!("Data: {}", p.root.display()),
            format!(
                "Game: {}{}",
                game.as_ref().map_or("not found".into(), |g| g.game_dir.display().to_string()),
                if last.as_ref().is_some_and(|r| r.game_running) { " (running)" } else { "" }
            ),
        ];
        if let Some(g) = &game {
            let missing = last.as_ref().map(|r| game_setup::names(&r.missing)).filter(|m| !m.is_empty()).unwrap_or_else(|| "nothing".into());
            lines.push(format!("Missing: {missing}"));
            lines.push(format!("BepInEx added by this app: {}", if s.read(|x| x.bep_in_ex_installed_by_us) { "yes" } else { "no" }));
            lines.push(format!("Other BepInEx mods: {}", if g.has_other_plugins() { "yes" } else { "no" }));
            lines.push(format!("Seal mode: {}", g.read_seal_mode().map_or(format!("{DEFAULT_SEAL_MODE} (default)"), String::from)));
        }
        lines.push(format!("Logs folder: {}{}", s.logs_folder(game.as_ref()).display(), if s.has_custom_logs_folder(game.as_ref()) { "" } else { " (default)" }));
        lines.push(match &last {
            Some(r) => format!("Status: {} [{:?}]", r.decision.summary, r.decision.health),
            None => "Status: ? [?]".into(),
        });
        let available = self.updates.as_ref().and_then(|u| u.available()).map(|m| format!(", v{} available", m.version)).unwrap_or_default();
        lines.push(format!(
            "Auto update: {}, channel {}, last check {}{available}",
            if s.read(|x| x.auto_update) { "True" } else { "False" },
            if s.read(|x| x.beta_updates()) { "beta" } else { "stable" },
            s.read(|x| x.last_update_check_utc.clone()).unwrap_or_else(|| "never".into())
        ));
        let now = chrono::Utc::now();
        lines.push(match PluginStatus::try_read(&p.plugin_status_file) {
            None => "Plugin: no status file".into(),
            Some(st) => format!(
                "Plugin: v{}, {}, heartbeat {}{}, {} saved, {}",
                st.plugin_version.as_deref().unwrap_or(""),
                st.state.as_deref().unwrap_or(""),
                st.heartbeat_utc.as_deref().unwrap_or(""),
                if st.is_alive(now) { " (alive)" } else { " (stale)" },
                st.saved_this_session.map(|n| n.to_string()).unwrap_or_default(),
                st.message.as_deref().unwrap_or("")
            ),
        });
        lines.push(format!("Generated {}", now.format("%Y-%m-%d %H:%M:%SZ")));
        if dialogs::set_clipboard_text(self.owner(), &(lines.join("\r\n") + "\r\n")) {
            self.balloon("Diagnostic info copied", "Paste it into your bug report.", BalloonIcon::Info, None);
        } else {
            log::error("Clipboard unavailable", "busy");
            self.balloon("Could not copy", "The clipboard is busy. Try again.", BalloonIcon::Warning, None);
        }
    }

    pub(super) fn repair_now(&self) {
        if self.session.game().is_none() {
            return self.choose_game_folder();
        }
        let (title, text, icon, click): (String, String, BalloonIcon, Option<Click>) = match self.daemon.repair_now() {
            RepairOutcome::Done(p) => ("Mod repaired".into(), format!("Put back: {}.", game_setup::names(&p)), BalloonIcon::Info, None),
            RepairOutcome::NothingToDo => ("Nothing to repair".into(), "The mod and BepInEx are in place.".into(), BalloonIcon::Info, None),
            RepairOutcome::GameRunning => (format!("Close {GAME_NAME} first"), "The mod is repaired while the game is closed.".into(), BalloonIcon::Warning, None),
            RepairOutcome::NoGame => return self.choose_game_folder(),
            RepairOutcome::ConsentPending => return self.ask_consent(),
            RepairOutcome::Failed(e) => (
                "Repair failed".into(),
                format!("{e} Details are in the app log."),
                BalloonIcon::Error,
                Some(Rc::new(|_: &Tray| {
                    if let Some(p) = log::file_path() {
                        shell::open_text(&p);
                    }
                }) as Click),
            ),
        };
        self.balloon(&title, &text, icon, click);
    }
}

/// The battle logs (by file name) directly in `folder`.
fn battle_logs(folder: &Path) -> Vec<PathBuf> {
    let Ok(entries) = std::fs::read_dir(folder) else { return Vec::new() };
    entries
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("txt")) && log_file_name::parse(&p.to_string_lossy()).is_some())
        .collect()
}
