//! The running tray (TrayApplicationContext.cs): the notification-area icon and its menu, the
//! repair daemon and the logs watcher. Everything that touches UI runs on the UI thread; the daemon,
//! the watcher and the cross-process signals send events here and wake it.
use crate::daemon::{Daemon, DaemonReport};
use crate::game_setup::{self, RepairOutcome};
use crate::logs_watcher::LogsWatcher;
use crate::session::Session;
use crate::win::instance::{self, TraySignals};
use crate::win::notify_icon::{self, BalloonIcon, Icon, NotifyIcon, TrayMsg, Waker};
use crate::win::popup::{self, Item};
use crate::win::{process, registry, shell};
use crate::{log, status_icons, GAME_NAME, PRODUCT_NAME, VERSION};
use pab_core::command_line::CommandLine;
use pab_core::daemon_monitor::{DaemonNotice, TrayHealth};
use pab_core::game_install::InstallPiece;
use std::cell::RefCell;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::{Arc, Mutex};

/// NotifyIcon tooltips stay short; the C# tray clipped at 63.
const MAX_TIP: usize = 63;

enum Event {
    Report(Box<DaemonReport>),
    Saved(PathBuf, &'static str, Option<String>),
    Show,
    Exit,
}

/// Sends events to the UI thread from anywhere.
#[derive(Clone)]
struct Poster {
    tx: Arc<Mutex<Sender<Event>>>,
    waker: Waker,
}

impl Poster {
    fn post(&self, e: Event) {
        if crate::lock(&self.tx).send(e).is_ok() {
            self.waker.wake();
        }
    }
}

// Menu item ids.
const OPEN_LOGS: u32 = 1;
const OPEN_LAST: u32 = 2;
const NOTIFY_ON_SAVE: u32 = 3;
const START_WITH_WINDOWS: u32 = 4;
const REPAIR_NOW: u32 = 5;
const OPEN_TRAY_LOG: u32 = 6;
const OPEN_BEPINEX_LOG: u32 = 7;
const PROJECT_PAGE: u32 = 8;
const EXIT: u32 = 9;

type Click = Rc<dyn Fn(&Tray)>;

struct Tray {
    session: Arc<Session>,
    icon: RefCell<NotifyIcon>,
    icons: [Option<Icon>; 4],
    daemon: Daemon,
    watcher: RefCell<LogsWatcher>,
    rx: Receiver<Event>,
    last: RefCell<Option<DaemonReport>>,
    last_saved: RefCell<Option<(PathBuf, String)>>,
    balloon_click: RefCell<Option<Click>>,
    exiting: std::cell::Cell<bool>,
}

fn health_index(h: TrayHealth) -> usize {
    match h {
        TrayHealth::Idle => 0,
        TrayHealth::Active => 1,
        TrayHealth::Attention => 2,
        TrayHealth::Error => 3,
    }
}

/// Runs the tray until Exit. `first_run`: show the welcome balloon.
pub fn run(session: Arc<Session>, cli: &CommandLine, first_run: bool, signals: &TraySignals) -> Result<(), String> {
    let (tx, rx) = channel();
    let tray: Rc<RefCell<Option<Rc<Tray>>>> = Rc::new(RefCell::new(None));
    let handler_tray = tray.clone();
    let notify = NotifyIcon::create(move |m| {
        let t = handler_tray.borrow().clone();
        if let Some(t) = t {
            t.on_msg(m);
        }
    })?;
    let poster = Poster { tx: Arc::new(Mutex::new(tx)), waker: notify.waker() };

    let size = notify_icon::small_icon_size();
    let icons = [TrayHealth::Idle, TrayHealth::Active, TrayHealth::Attention, TrayHealth::Error]
        .map(|h| status_icons::draw(crate::embedded::APP_ICON, size, status_icons::dot_color(h)).and_then(|px| Icon::from_rgba(size, &px)));

    let saved_poster = poster.clone();
    let watcher = LogsWatcher::new(Arc::new(move |p, r, o| saved_poster.post(Event::Saved(p, r, o))));
    let t = Rc::new(Tray {
        daemon: Daemon::new(session.clone()),
        session: session.clone(),
        icon: RefCell::new(notify),
        icons,
        watcher: RefCell::new(watcher),
        rx,
        last: RefCell::new(None),
        last_saved: RefCell::new(None),
        balloon_click: RefCell::new(None),
        exiting: std::cell::Cell::new(false),
    });
    *tray.borrow_mut() = Some(t.clone());

    if let Some(show) = signals.show.clone() {
        let p = poster.clone();
        instance::listen(show, move || p.post(Event::Show));
    }
    if let Some(exit) = signals.exit.clone() {
        let p = poster.clone();
        instance::listen(exit, move || {
            log::info("Another process asked the tray to exit.");
            p.post(Event::Exit);
        });
    }

    {
        let mut icon = t.icon.borrow_mut();
        if let Some(i) = &t.icons[0] {
            icon.set_icon(i.0);
        }
        icon.set_tip(PRODUCT_NAME);
        icon.show();
    }
    t.watcher.borrow_mut().watch(&session.logs_folder(session.game().as_ref()));

    // A first install while the game runs finishes once the game closes: the welcome says so, and
    // the daemon does not call it "removed by a game update".
    let setup_pending = first_run && t.finishes_when_game_closes();
    if setup_pending {
        t.daemon.setup_pending();
    }
    let p = poster.clone();
    t.daemon.start(move |r| p.post(Event::Report(Box::new(r))));
    t.welcome(cli, first_run, setup_pending);
    log::info(&format!("Tray running ({}, {}).", if session.portable() { "portable" } else { "installed" }, session.paths().root.display()));

    notify_icon::run_loop();
    t.shutdown();
    *tray.borrow_mut() = None;
    Ok(())
}

impl Tray {
    fn on_msg(&self, m: TrayMsg) {
        if self.exiting.get() {
            return;
        }
        match m {
            TrayMsg::Wake => {
                while let Ok(e) = self.rx.try_recv() {
                    self.on_event(e);
                }
            }
            TrayMsg::LeftClick | TrayMsg::ContextMenu => self.show_menu(),
            TrayMsg::BalloonClick => {
                // Cloned out: the click may show another balloon, which replaces it.
                let click = self.balloon_click.borrow().clone();
                if let Some(click) = click {
                    click(self);
                }
            }
            TrayMsg::TaskbarCreated => self.icon.borrow_mut().show(),
            TrayMsg::EndSession => self.exit(),
        }
    }

    fn on_event(&self, e: Event) {
        match e {
            Event::Report(r) => self.on_report(*r),
            Event::Saved(path, result, opponent) => self.on_saved(path, result, opponent),
            // shortcut: the status window comes in phase 4; until then "show" opens the menu.
            Event::Show => self.show_menu(),
            Event::Exit => self.exit(),
        }
    }

    fn exit(&self) {
        if !self.exiting.get() {
            notify_icon::quit();
        }
    }

    fn shutdown(&self) {
        if self.exiting.replace(true) {
            return;
        }
        log::info("Tray exiting.");
        self.icon.borrow_mut().remove();
        self.daemon.stop();
    }

    fn balloon(&self, title: &str, text: &str, icon: BalloonIcon, click: Option<Click>) {
        if self.exiting.get() || text.trim().is_empty() {
            return;
        }
        // The latest balloon's action. Not cleared when a balloon closes: a toast can be clicked
        // later from the notification center.
        *self.balloon_click.borrow_mut() = click;
        self.icon.borrow().balloon(title, text, icon);
    }

    fn on_report(&self, r: DaemonReport) {
        let d = &r.decision;
        if let Some(i) = &self.icons[health_index(d.health)] {
            self.icon.borrow_mut().set_icon(i.0);
        }
        self.icon.borrow_mut().set_tip(&crate::clip(&format!("{PRODUCT_NAME}\n{}", d.summary), MAX_TIP));
        self.watcher.borrow_mut().watch(&r.watch_folder);

        match d.notice {
            DaemonNotice::GameNeedsRestart => self.balloon(
                &format!("Restart {GAME_NAME}"),
                "The mod is missing from the game (a game update can remove it). Close the game once it has finished updating; the mod is put back automatically.",
                BalloonIcon::Warning,
                None,
            ),
            DaemonNotice::RunningWithoutMod => self.balloon(
                "Battle logs are not being saved",
                &format!("{GAME_NAME} is running without the mod. Restart the game to start saving your battle logs."),
                BalloonIcon::Warning,
                None,
            ),
            DaemonNotice::ModUpdateReady => self.balloon(
                &format!("Restart {GAME_NAME} to finish the update"),
                "The game is still running the previous version of the mod. Battle logs are still saved, but what the new version adds (such as sending your decklist) starts after you close the game and open it again.",
                BalloonIcon::Info,
                None,
            ),
            DaemonNotice::PluginError => {
                let message = r.status.as_ref().and_then(|s| s.message.as_deref()).map(str::trim).filter(|m| !m.is_empty()).unwrap_or("See the BepInEx log.");
                self.balloon("The mod reported a problem", &format!("{message} Click to open the BepInEx log."), BalloonIcon::Error, Some(Rc::new(|t: &Tray| t.open_bepinex_log())));
            }
            DaemonNotice::None => {}
        }
        if r.repaired.iter().any(|p| *p != InstallPiece::Plugin) {
            self.balloon("Mod ready", &format!("The mod is in place in {GAME_NAME}. Battle logs are saved from your next game."), BalloonIcon::Info, None);
        }
        *self.last.borrow_mut() = Some(r);
    }

    fn on_saved(&self, path: PathBuf, result: &str, opponent: Option<String>) {
        let vs = opponent.map(|o| format!(" vs {o}")).unwrap_or_default();
        *self.last_saved.borrow_mut() = Some((path.clone(), format!("{result}{vs}, {}", chrono::Local::now().format("%H:%M"))));
        if !self.session.read(|s| s.notify_on_save) {
            return;
        }
        let folder = path.parent().map(shell::friendly).unwrap_or_default();
        self.balloon(
            &format!("{result}{vs} — battle log saved"),
            &format!("Saved in {folder}. Click to open it."),
            BalloonIcon::Info,
            Some(Rc::new(move |_: &Tray| {
                shell::open_text(&path);
            })),
        );
    }

    fn finishes_when_game_closes(&self) -> bool {
        let Some(game) = self.session.game() else { return false };
        self.session.payload().is_ok_and(|p| !game.missing(&p).is_empty()) && process::game_is_running()
    }

    fn welcome(&self, cli: &CommandLine, first_run: bool, setup_pending: bool) {
        if cli.updated() {
            let from = cli.updated_from.as_deref().filter(|f| !f.is_empty()).map(|f| format!(" from v{f}")).unwrap_or_default();
            let url = format!("{}/releases/tag/v{VERSION}", crate::project_url());
            self.balloon(
                &format!("Updated to v{VERSION}"),
                &format!("{PRODUCT_NAME} was updated{from}. Click to see what's new."),
                BalloonIcon::Info,
                Some(Rc::new(move |_: &Tray| {
                    shell::open(&url);
                })),
            );
            log::info(&format!("Updated{from} to {VERSION}"));
        } else if first_run {
            let folder = shell::friendly(&self.session.logs_folder(self.session.game().as_ref()));
            let open: Option<Click> = Some(Rc::new(|t: &Tray| t.open_logs_folder()));
            if setup_pending {
                self.balloon(
                    &format!("{PRODUCT_NAME} is installed"),
                    &format!("Close {GAME_NAME} once to finish installing: battle logs are saved from your next game, to {folder}."),
                    BalloonIcon::Info,
                    open,
                );
            } else {
                let game = if self.session.game().is_none() {
                    format!("\n{GAME_NAME} was not found: right-click this icon and choose Help > Diagnostics > Choose game folder.")
                } else {
                    String::new()
                };
                self.balloon(&format!("{PRODUCT_NAME} is ready"), &format!("Every match is saved to {folder}. Right-click this icon for options.{game}"), BalloonIcon::Info, open);
            }
        }
        // shortcut: the disclaimer dialog comes with the egui window (phase 4); until then it is
        // asked by a balloon only and the daemon keeps the game untouched (consent_pending).
        if !self.session.consent() {
            self.balloon("Please accept the disclaimer", &format!("Accept it once to keep using {PRODUCT_NAME}."), BalloonIcon::Warning, None);
        }
    }

    fn open_logs_folder(&self) {
        let folder = self.watcher.borrow().folder().map(Path::to_path_buf).unwrap_or_else(|| game_setup::ensure_logs_folder(&self.session));
        let _ = std::fs::create_dir_all(&folder);
        shell::open(&folder.to_string_lossy());
    }

    fn open_bepinex_log(&self) {
        if let Some(g) = self.session.game() {
            if g.bepinex_log_path.is_file() {
                shell::open_text(&g.bepinex_log_path);
            }
        }
    }

    fn show_menu(&self) {
        let summary = self.last.borrow().as_ref().map(|r| r.decision.summary.clone()).unwrap_or_else(|| "Starting…".into());
        let exe = self.session.paths().installed_exe;
        let mut items = vec![Item::label(summary), Item::Separator, Item::bold(OPEN_LOGS, "Open battle logs folder")];
        if let Some((_, label)) = self.last_saved.borrow().as_ref() {
            items.push(Item::action(OPEN_LAST, format!("Last saved: {label}")));
        }
        items.extend([
            Item::Separator,
            Item::check(NOTIFY_ON_SAVE, "Notify when a battle log is saved", self.session.read(|s| s.notify_on_save)),
            Item::check(START_WITH_WINDOWS, "Start with Windows", registry::is_start_with_windows(&exe)),
            Item::Separator,
            Item::Sub {
                text: "Help".into(),
                items: vec![
                    Item::action(REPAIR_NOW, "Repair the mod now"),
                    Item::action(OPEN_TRAY_LOG, "Open the app's log"),
                    Item::action(OPEN_BEPINEX_LOG, "Open the BepInEx log"),
                    Item::action(PROJECT_PAGE, format!("Project page (v{VERSION})")),
                ],
            },
            Item::Separator,
            Item::action(EXIT, "Exit"),
        ]);
        let hwnd = self.icon.borrow().hwnd;
        match popup::show(hwnd, &items) {
            Some(OPEN_LOGS) => self.open_logs_folder(),
            Some(OPEN_LAST) => {
                if let Some((p, _)) = self.last_saved.borrow().as_ref() {
                    shell::open_text(p);
                }
            }
            Some(NOTIFY_ON_SAVE) => self.session.update(|s| s.notify_on_save = !s.notify_on_save),
            Some(START_WITH_WINDOWS) => {
                let on = !registry::is_start_with_windows(&exe);
                let command = self.session.run_command();
                if let Err(e) = registry::set_start_with_windows(on.then_some(command.as_str())) {
                    log::error("Could not change Start with Windows", e);
                }
            }
            Some(REPAIR_NOW) => self.repair_now(),
            Some(OPEN_TRAY_LOG) => {
                if let Some(p) = log::file_path() {
                    shell::open_text(&p);
                }
            }
            Some(OPEN_BEPINEX_LOG) => self.open_bepinex_log(),
            Some(PROJECT_PAGE) => {
                shell::open(&crate::project_url());
            }
            Some(EXIT) => self.exit(),
            _ => {}
        }
    }

    fn repair_now(&self) {
        let open_log: Click = Rc::new(|_: &Tray| {
            if let Some(p) = log::file_path() {
                shell::open_text(&p);
            }
        });
        // shortcut: NoGame and ConsentPending open the folder picker and the disclaimer in C#;
        // both come with the egui window (phase 4), a balloon says it until then.
        let (title, text, icon, click) = match self.daemon.repair_now() {
            RepairOutcome::Done(p) => ("Mod repaired".to_string(), format!("Put back: {}.", game_setup::names(&p)), BalloonIcon::Info, None),
            RepairOutcome::NothingToDo => ("Nothing to repair".into(), "The mod and BepInEx are in place.".into(), BalloonIcon::Info, None),
            RepairOutcome::GameRunning => (format!("Close {GAME_NAME} first"), "The mod is repaired while the game is closed.".into(), BalloonIcon::Warning, None),
            RepairOutcome::NoGame => ("Game not found".into(), format!("{GAME_NAME} was not found."), BalloonIcon::Warning, None),
            RepairOutcome::ConsentPending => ("Please accept the disclaimer".into(), "Nothing is written into the game before that.".into(), BalloonIcon::Warning, None),
            RepairOutcome::Failed(e) => ("Repair failed".into(), format!("{e} Details are in the app log."), BalloonIcon::Error, Some(open_log)),
        };
        self.balloon(&title, &text, icon, click);
    }
}
