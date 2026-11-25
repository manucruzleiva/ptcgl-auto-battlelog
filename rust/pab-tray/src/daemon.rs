//! The repair daemon (Daemon.cs, docs/ARCHITECTURE.md "Daemon loop"): every 5 s on its own thread,
//! looks at the game folder, the game process and the plugin's heartbeat, repairs while the game is
//! closed, and reports. The decisions themselves are pab_core::daemon_monitor's.
use crate::game_setup::{self, RepairOutcome};
use crate::{log, session::Session, sys};
use chrono::Utc;
use pab_core::daemon_monitor::{DaemonDecision, DaemonMonitor, DaemonNotice, DaemonObservation};
use pab_core::game_install::InstallPiece;
use pab_core::plugin_status::PluginStatus;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

pub const INTERVAL: Duration = Duration::from_secs(5);
const FIND_INTERVAL: Duration = Duration::from_secs(60);

/// What the UI needs after each tick.
#[derive(Debug, Clone)]
pub struct DaemonReport {
    pub decision: DaemonDecision,
    pub game_dir: Option<String>,
    pub game_running: bool,
    pub missing: Vec<InstallPiece>,
    pub status: Option<PluginStatus>,
    /// Where new logs will appear: the running plugin's folder, else the configured one.
    pub watch_folder: PathBuf,
    /// Pieces the repair put back on this tick (empty if none).
    pub repaired: Vec<InstallPiece>,
}

#[derive(Default)]
struct Wake {
    poked: bool,
    stop: bool,
}

struct Shared {
    session: Arc<Session>,
    monitor: Mutex<DaemonMonitor>,
    wake: Mutex<Wake>,
    cv: Condvar,
}

pub struct Daemon {
    shared: Arc<Shared>,
    thread: Mutex<Option<JoinHandle<()>>>,
}

struct Ticker {
    last_status: Option<PluginStatus>,
    last_find: Option<Instant>,
}

impl Daemon {
    pub fn new(session: Arc<Session>) -> Daemon {
        let shared = Shared { session, monitor: Mutex::default(), wake: Mutex::default(), cv: Condvar::new() };
        Daemon { shared: Arc::new(shared), thread: Mutex::new(None) }
    }

    /// Starts the loop. `report` runs on the daemon thread: it hands the report to the UI itself.
    pub fn start(&self, report: impl Fn(DaemonReport) + Send + 'static) {
        let mut thread = crate::lock(&self.thread);
        if thread.is_some() {
            return;
        }
        let shared = self.shared.clone();
        *thread = Some(std::thread::spawn(move || {
            log::info("Daemon started.");
            let mut t = Ticker { last_status: None, last_find: None };
            let mut last_error: Option<String> = None;
            loop {
                let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| shared.tick(&mut t)));
                match result.unwrap_or_else(|_| Err("panic".to_string())) {
                    Ok(r) => {
                        last_error = None;
                        report(r);
                    }
                    // Never let one bad tick stop the daemon; log each distinct failure once.
                    Err(e) => {
                        if last_error.as_deref() != Some(e.as_str()) {
                            log::error("Daemon tick failed", &e);
                        }
                        last_error = Some(e);
                    }
                }
                let wake = crate::lock(&shared.wake);
                let (mut wake, _) = shared.cv.wait_timeout_while(wake, INTERVAL, |w| !w.poked && !w.stop).unwrap_or_else(|p| p.into_inner());
                if wake.stop {
                    break;
                }
                wake.poked = false;
            }
        }));
    }

    /// Runs a tick now instead of in up to 5 s (after a setting changed).
    pub fn poke(&self) {
        crate::lock(&self.shared.wake).poked = true;
        self.shared.cv.notify_all();
    }

    /// The first install waits for the game to close (DaemonMonitor.setup_pending). Call before start.
    pub fn setup_pending(&self) {
        crate::lock(&self.shared.monitor).setup_pending();
    }

    /// "Repair now" from the menu: same rules (only with the game closed), on the caller's thread.
    pub fn repair_now(&self) -> RepairOutcome {
        let mut monitor = crate::lock(&self.shared.monitor);
        let outcome = game_setup::try_repair(&self.shared.session);
        if matches!(outcome, RepairOutcome::Done(_)) {
            monitor.repair_succeeded();
        }
        drop(monitor);
        self.poke();
        outcome
    }

    /// Stops the loop and waits for a tick in progress (a repair finishes its file).
    pub fn stop(&self) {
        crate::lock(&self.shared.wake).stop = true;
        self.shared.cv.notify_all();
        if let Some(t) = crate::lock(&self.thread).take() {
            let _ = t.join();
        }
    }
}

impl Drop for Daemon {
    fn drop(&mut self) {
        self.stop();
    }
}

impl Shared {
    fn tick(&self, t: &mut Ticker) -> Result<DaemonReport, String> {
        let session = &self.session;
        let now = Utc::now();
        let mut game = session.game();
        if game.is_none() && t.last_find.is_none_or(|at| at.elapsed() >= FIND_INTERVAL) {
            // The registry walk is not free: when the game is missing, look again once a minute.
            t.last_find = Some(Instant::now());
            if let Some(found) = sys::find_game(session.game_dir().as_deref()) {
                game_setup::adopt(session, &found);
                game = session.game();
            }
        }

        let game_dir = game.as_ref().map(|g| g.game_dir.to_string_lossy().into_owned());
        let mut o = DaemonObservation {
            now,
            game_dir: game_dir.clone(),
            game_running: sys::game_is_running(),
            missing: Vec::new(),
            plugin_file_present: false,
            status: None,
            // Never accepted: the mod is not repaired or updated.
            consent_pending: !session.consent(),
        };
        if let Some(g) = &game {
            o.missing = g.missing(&session.payload()?);
            o.plugin_file_present = g.plugin_path.is_file();
        }
        t.last_status = PluginStatus::try_read_keeping(&session.paths().plugin_status_file, t.last_status.take());
        o.status = t.last_status.clone();

        let mut repaired = Vec::new();
        let decision = {
            let mut monitor = crate::lock(&self.monitor); // shared with repair_now
            let decision = monitor.evaluate(&o);
            match &game {
                // Once more, right before touching the folder.
                Some(g) if decision.repair && !sys::game_is_running() => {
                    let payload = session.payload()?;
                    match g.repair(&payload) {
                        Ok(r) => {
                            monitor.repair_succeeded();
                            log::info(&format!("Repaired: {}", game_setup::names(&r)));
                            repaired = r;
                        }
                        Err(e) => {
                            let msg = e.to_string();
                            if monitor.last_repair_error.as_deref() != Some(msg.as_str()) {
                                log::error("Repair failed", &msg);
                            }
                            monitor.repair_failed(&msg, now);
                        }
                    }
                    o.missing = g.missing(&payload);
                    o.plugin_file_present = g.plugin_path.is_file();
                    let mut after = monitor.evaluate(&o);
                    if after.notice == DaemonNotice::None {
                        after.notice = decision.notice; // keep what the first look decided to say
                    }
                    after
                }
                _ => decision,
            }
        };

        let running_folder = o.status.as_ref().and_then(|s| s.logs_folder.clone()).filter(|f| is_absolute(f));
        let watch_folder = match running_folder {
            Some(f) if decision.plugin_alive => PathBuf::from(f),
            _ => session.logs_folder(game.as_ref()),
        };
        Ok(DaemonReport { decision, game_dir, game_running: o.game_running, missing: o.missing, status: o.status, watch_folder, repaired })
    }
}

fn is_absolute(path: &str) -> bool {
    !path.trim().is_empty() && Path::new(path).is_absolute()
}
