//! OTA updates (UpdateService.cs, docs/ARCHITECTURE.md "OTA updates"): checks GitHub Releases 1
//! minute after start and every 6 hours, on the player's channel; with autoUpdate on, downloads,
//! verifies, swaps the exe and restarts into it, otherwise offers the update. Runs on its own
//! thread; the UI gets events and sends commands.
use crate::{lock, log, session::Session, PRODUCT_NAME, VERSION};
use pab_core::command_line::{self, PORTABLE, UPDATED_FROM};
use pab_core::update::{UpdateChecker, UpdateError, UpdateManifest, EXE_ASSET_NAME};
use pab_core::self_update;
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{channel, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const FIRST_CHECK: Duration = Duration::from_secs(60);
const EVERY: Duration = Duration::from_secs(6 * 3600);
const AFTER_FAILURE: Duration = Duration::from_secs(30 * 60);
const BUSY_RETRY: Duration = Duration::from_secs(120);
const TICK: Duration = Duration::from_secs(15);

/// Starts the new exe with these arguments (Process.Start).
pub type StartExe = dyn Fn(&Path, &[String]) -> io::Result<()> + Send;

/// GET a URL; `true` for a download (longer timeout).
pub type Net = dyn Fn(&str, bool) -> io::Result<Box<dyn Read>> + Send + Sync;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Click {
    Install,
    OpenReleases,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UpdateEvent {
    Notify { title: String, text: String, click: Option<Click> },
    AvailableChanged,
    /// The new exe was started: the tray must exit now so it can take the mutex.
    Restart,
}

enum Command {
    Check { manual: bool },
    Install,
    Stop,
}

struct State {
    available: Option<UpdateManifest>,
    announced: Option<String>,
    failure_shown: Option<String>,
    next_check: Instant,
}

pub struct Updates {
    tx: Sender<Command>,
    state: Arc<Mutex<State>>,
    thread: Option<std::thread::JoinHandle<()>>,
}

struct Worker {
    session: Arc<Session>,
    checker: UpdateChecker,
    net: Arc<Net>,
    state: Arc<Mutex<State>>,
    emit: Box<dyn Fn(UpdateEvent) + Send>,
    can_restart: Box<dyn Fn() -> bool + Send>,
    start: Box<StartExe>,
}

impl Updates {
    /// `can_restart`: false while something must not be interrupted (a dialog): the update waits.
    pub fn start(
        session: Arc<Session>,
        net: Arc<Net>,
        emit: impl Fn(UpdateEvent) + Send + 'static,
        can_restart: impl Fn() -> bool + Send + 'static,
        start_exe: impl Fn(&Path, &[String]) -> io::Result<()> + Send + 'static,
    ) -> Result<Updates, String> {
        let checker = UpdateChecker::new(crate::REPO_OWNER, crate::REPO_NAME, VERSION)?;
        clean_updates_folder(&session.paths().updates_dir);
        let state = Arc::new(Mutex::new(State { available: None, announced: None, failure_shown: None, next_check: Instant::now() + FIRST_CHECK }));
        let (tx, rx) = channel();
        let mut w = Worker { session, checker, net, state: state.clone(), emit: Box::new(emit), can_restart: Box::new(can_restart), start: Box::new(start_exe) };
        let thread = std::thread::spawn(move || {
            // Again after start: right after an update the previous process may still hold the .old exe.
            let mut old_cleaned = false;
            loop {
                let cmd = rx.recv_timeout(TICK);
                if !old_cleaned {
                    old_cleaned = true;
                    self_update::cleanup_old(&w.session.paths().installed_exe);
                }
                match cmd {
                    Ok(Command::Stop) | Err(RecvTimeoutError::Disconnected) => break,
                    Ok(Command::Check { manual }) => w.check(manual),
                    Ok(Command::Install) => {
                        let m = lock(&w.state).available.clone();
                        if let Some(m) = m {
                            w.install(&m, true);
                        }
                    }
                    Err(RecvTimeoutError::Timeout) => {
                        if Instant::now() >= lock(&w.state).next_check {
                            w.check(false);
                        }
                    }
                }
            }
        });
        Ok(Updates { tx, state, thread: Some(thread) })
    }

    /// A newer release waiting for the player (autoUpdate off, or applying it failed).
    pub fn available(&self) -> Option<UpdateManifest> {
        lock(&self.state).available.clone()
    }

    /// "Check for updates": always says what happened, and ignores a skipped version.
    pub fn check_now(&self) {
        let _ = self.tx.send(Command::Check { manual: true });
    }

    /// After a channel change: a silent check now.
    pub fn check_soon(&self) {
        let _ = self.tx.send(Command::Check { manual: false });
    }

    pub fn install_available(&self) {
        let _ = self.tx.send(Command::Install);
    }

    pub fn skip_available(&self, session: &Session) -> bool {
        let Some(m) = lock(&self.state).available.take() else { return false };
        session.update(|s| s.skipped_version = Some(m.version.clone()));
        log::info(&format!("Update v{} skipped.", m.version));
        true
    }
}

impl Drop for Updates {
    fn drop(&mut self) {
        let _ = self.tx.send(Command::Stop);
        // A download in progress may take long: the thread is left to finish on its own.
        drop(self.thread.take());
    }
}

impl Worker {
    fn notify(&self, title: &str, text: &str, click: Option<Click>) {
        (self.emit)(UpdateEvent::Notify { title: title.into(), text: text.into(), click });
    }

    fn set_available(&self, m: Option<&UpdateManifest>) {
        let mut s = lock(&self.state);
        let same = match (&s.available, m) {
            (None, None) => true,
            (Some(a), Some(b)) => a.version == b.version,
            _ => false,
        };
        if !same {
            s.available = m.cloned();
            drop(s);
            (self.emit)(UpdateEvent::AvailableChanged);
        }
    }

    fn retry_in(&self, d: Duration) {
        lock(&self.state).next_check = Instant::now() + d;
    }

    /// Automatic checks are silent on failure (logged, retried in 30 minutes).
    fn check(&mut self, manual: bool) {
        let beta = self.session.read(|s| s.beta_updates());
        let net = self.net.clone();
        let manifest = match self.checker.fetch_manifest(beta, &mut |url: &str| net(url, false)) {
            Ok(m) => m,
            Err(e) => {
                self.retry_in(AFTER_FAILURE);
                log::error("Update check failed", &e);
                if manual {
                    self.notify("Could not check for updates", &friendly(&e), None);
                }
                return;
            }
        };
        self.retry_in(EVERY);
        self.session.update(|s| s.last_update_check_utc = Some(chrono::Utc::now().format("%Y-%m-%dT%H:%M:%SZ").to_string()));
        let channel = if beta { "beta" } else { "stable" };
        if !self.checker.is_newer(&manifest.version, beta) {
            log::info(&format!("Update check ({channel}): up to date (latest {}).", manifest.version));
            self.set_available(None);
            if manual {
                self.notify("You're up to date", &format!("{PRODUCT_NAME} v{VERSION} is the latest version."), None);
            }
            return;
        }
        let skipped = self.session.read(|s| s.skipped_version.clone());
        if !manual && skipped.is_some_and(|v| v.eq_ignore_ascii_case(&manifest.version)) {
            log::info(&format!("Update check: v{} available, skipped by the player.", manifest.version));
            return;
        }
        log::info(&format!("Update check ({channel}): v{} available.", manifest.version));
        self.set_available(Some(&manifest));
        if self.session.read(|s| s.auto_update) {
            self.install(&manifest, manual);
        } else {
            let mut s = lock(&self.state);
            if manual || s.announced.as_deref() != Some(manifest.version.as_str()) {
                s.announced = Some(manifest.version.clone());
                drop(s);
                self.notify("Update available", &format!("{PRODUCT_NAME} v{} is available. Click to install it.", manifest.version), Some(Click::Install));
            }
        }
    }

    /// Downloads, verifies and applies the update; on success the tray is told to exit.
    fn install(&mut self, m: &UpdateManifest, manual: bool) {
        let Some(asset) = m.find(EXE_ASSET_NAME) else {
            log::error(&format!("Release v{} has no {EXE_ASSET_NAME}", m.version), "");
            if manual {
                self.notify("Update failed", &format!("The release has no {EXE_ASSET_NAME}."), Some(Click::OpenReleases));
            }
            return;
        };
        let net = self.net.clone();
        let result = self
            .checker
            .download(asset, &self.session.paths().updates_dir, &mut |url: &str| net(url, true), &|| false)
            .and_then(|downloaded| {
                log::info(&format!("Downloaded and verified v{}: {}", m.version, downloaded.display()));
                if !(self.can_restart)() {
                    // A dialog is open: try again at the next check.
                    log::info("Update postponed: the app is busy.");
                    self.retry_in(BUSY_RETRY);
                    self.set_available(Some(m));
                    return Ok(false);
                }
                self.apply(&downloaded).map(|_| true).map_err(UpdateError::Io)
            });
        match result {
            Ok(true) => (self.emit)(UpdateEvent::Restart),
            Ok(false) => {}
            Err(e) => {
                self.retry_in(AFTER_FAILURE);
                log::error(&format!("Update to v{} failed", m.version), &e);
                self.set_available(Some(m));
                // A bad release would fail every retry: say so once per version, unless asked.
                let mut s = lock(&self.state);
                if manual || (matches!(e, UpdateError::Verification(_)) && s.failure_shown.as_deref() != Some(m.version.as_str())) {
                    s.failure_shown = Some(m.version.clone());
                    drop(s);
                    self.notify("Update failed", &format!("{} Click to download it from the project page.", friendly(&e)), Some(Click::OpenReleases));
                }
            }
        }
    }

    /// Swaps the exe and starts the new one with --updated-from.
    fn apply(&self, downloaded: &Path) -> io::Result<()> {
        let paths = self.session.paths();
        let exe = paths.installed_exe;
        self_update::swap(downloaded, &exe)?;
        let mut args = vec![UPDATED_FROM.to_string(), VERSION.to_string()];
        if paths.is_portable && self.session.portable_forced {
            args.push(PORTABLE.to_string());
        }
        if let Err(e) = (self.start)(&exe, &args) {
            // The new exe did not start: put the running one back so the next logon still works.
            if std::fs::remove_file(&exe).and_then(|_| std::fs::rename(&paths.old_exe, &exe)).is_err() {
                log::error("Could not restore the previous exe after a failed restart", &e);
            }
            return Err(e);
        }
        log::info(&format!("Started the new version ({}); exiting.", command_line::join(&args)));
        Ok(())
    }
}

fn friendly(e: &UpdateError) -> String {
    match e {
        UpdateError::Verification(_) => "The download could not be verified, so it was not installed.".into(),
        UpdateError::Io(e) if e.kind() == io::ErrorKind::TimedOut => "GitHub did not answer in time.".into(),
        UpdateError::Io(e) if matches!(e.kind(), io::ErrorKind::PermissionDenied | io::ErrorKind::AlreadyExists) => {
            format!("The new version could not be put in place ({e}).")
        }
        UpdateError::Io(_) => "No connection to GitHub.".into(),
        e => e.to_string(),
    }
}

/// Downloads left by an update that went through, failed or was interrupted.
fn clean_updates_folder(dir: &Path) {
    if let Ok(entries) = std::fs::read_dir(dir) {
        for e in entries.flatten().map(|e| e.path()).filter(|p: &PathBuf| p.is_file()) {
            let _ = std::fs::remove_file(e);
        }
    }
}
