//! The running tray (TrayApplicationContext.cs): the notification-area icon and its menu, the
//! repair daemon and the logs watcher. Everything that touches UI runs on the UI thread; the daemon,
//! the watcher and the cross-process signals send events here and wake it.
use crate::daemon::{Daemon, DaemonReport};
use crate::logs_watcher::LogsWatcher;
use crate::session::Session;
use crate::updates::{self, UpdateEvent, Updates};
use crate::uploads::{Clients, Notice, Uploads};
use crate::win::instance::{self, TraySignals};
use crate::win::notify_icon::{self, BalloonIcon, Icon, NotifyIcon, TrayMsg, Waker};
use crate::win::{process, shell};
use crate::{log, status_icons, GAME_NAME, PRODUCT_NAME, VERSION};
use pab_core::command_line::CommandLine;
use pab_core::daemon_monitor::{DaemonNotice, TrayHealth};
use pab_core::game_install::InstallPiece;
use std::cell::RefCell;
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

/// NotifyIcon tooltips stay short; the C# tray clipped at 63.
const MAX_TIP: usize = 63;

mod accounts;
mod menu;

enum Event {
    Update(UpdateEvent),
    Upload(Notice),
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
    updates: Option<Updates>,
    uploads: Uploads,
    /// Open dialogs: an update must not restart the app under one.
    modal: Arc<AtomicUsize>,
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
    let modal = Arc::new(AtomicUsize::new(0));
    let update_poster = poster.clone();
    let m = modal.clone();
    let updates = Updates::start(
        session.clone(),
        Arc::new(|url: &str, download: bool| crate::win::http::open_url(url, download)),
        move |e| update_poster.post(Event::Update(e)),
        move || m.load(Ordering::SeqCst) == 0,
        |exe: &std::path::Path, args: &[String]| {
            std::process::Command::new(exe).args(args).current_dir(exe.parent().unwrap_or(exe)).spawn().map(|_| ())
        },
    )
    .map_err(|e| log::error("Updates are off", e))
    .ok();
    let clients = Clients::new(&session, Arc::new(crate::win::http::send), pab_core::dpapi::protect, pab_core::dpapi::unprotect);
    let upload_poster = poster.clone();
    let uploads = Uploads::start(session.clone(), Arc::new(clients), move |n| upload_poster.post(Event::Upload(n)));
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
        updates,
        uploads,
        modal,
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
            Event::Update(e) => self.on_update(e),
            Event::Upload(n) => self.on_upload_notice(n),
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

    fn on_update(&self, e: UpdateEvent) {
        match e {
            UpdateEvent::Notify { title, text, click } => {
                let click: Option<Click> = click.map(|c| -> Click {
                    match c {
                        updates::Click::Install => Rc::new(|t: &Tray| {
                            if let Some(u) = &t.updates {
                                u.install_available();
                            }
                        }),
                        updates::Click::OpenReleases => Rc::new(|_: &Tray| {
                            shell::open(&format!("{}/releases", crate::project_url()));
                        }),
                    }
                });
                self.balloon(&title, &text, BalloonIcon::Info, click);
            }
            UpdateEvent::AvailableChanged => {}
            UpdateEvent::Restart => self.exit(),
        }
    }

    /// Counts an open dialog while `f` runs: an update must not restart the app under it.
    fn modal<T>(&self, f: impl FnOnce() -> T) -> T {
        self.modal.fetch_add(1, Ordering::SeqCst);
        let r = f();
        self.modal.fetch_sub(1, Ordering::SeqCst);
        r
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
        self.uploads.upload_tcgkb(false);
        self.uploads.saved();
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
        // --background and --updated-from never show the disclaimer: if it was never accepted, ask with a balloon.
        if !self.session.consent() {
            self.balloon(
                "Please accept the disclaimer",
                &format!("Accept it once to keep using {PRODUCT_NAME}. Click to read it."),
                BalloonIcon::Warning,
                Some(Rc::new(|t: &Tray| t.ask_consent())),
            );
        }
    }

    /// The disclaimer, asked from the running tray. Until it is accepted nothing is written into
    /// the game. Declining stops the app for good (the Run value goes) and offers to take the mod
    /// out of the game.
    fn ask_consent(&self) {
        if self.session.consent() || self.modal.load(Ordering::SeqCst) > 0 {
            return;
        }
        if self.modal(|| crate::setup::ensure_accepted(&self.session, false)) {
            self.daemon.poke(); // repairs and updates the mod again
            self.uploads.consent_given();
            return;
        }
        if let Err(e) = crate::win::registry::remove_start_with_windows(&self.session.paths().installed_exe) {
            log::error("Could not turn off Start with Windows", e);
        }
        let text = format!(
            "You did not accept the disclaimer, so {PRODUCT_NAME} closes now and no longer starts with Windows.\n\nThe mod is still in {GAME_NAME} and keeps saving battle logs. Remove it from the game too?"
        );
        if self.modal(|| crate::win::dialogs::confirm(self.icon.borrow().hwnd, &text, false)) {
            // The --uninstall process asks once more, waits for the game and stops this tray.
            self.uninstall();
        }
        self.exit();
    }

    /// Runs this exe with --uninstall, as Apps & Features would: one code path for both.
    fn uninstall(&self) {
        let s = &self.session;
        let mut args = vec![pab_core::command_line::UNINSTALL];
        if s.portable() && s.portable_forced {
            args.push(pab_core::command_line::PORTABLE);
        }
        let exe = if s.portable() { s.exe_path.clone() } else { s.paths().installed_exe };
        if let Err(e) = std::process::Command::new(exe).args(args).current_dir(std::env::temp_dir()).spawn() {
            log::error("Could not start the uninstaller", e);
        }
    }
}
