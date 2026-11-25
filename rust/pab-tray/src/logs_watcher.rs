//! Watches the logs folder for new battle logs (a .txt created or renamed into place) and reports
//! each one once, after it has been quiet for a moment (LogsWatcher.cs + SaveNotificationQueue).
use crate::{lock, log};
use chrono::Utc;
use notify::event::{CreateKind, ModifyKind, RenameMode};
use notify::{EventKind, RecursiveMode, Watcher};
use pab_core::file_ops::is_same_or_under;
use pab_core::log_file_name;
use pab_core::save_notification_queue::{SaveNotificationQueue, DEFAULT_DELAY};
use pab_core::watched_folder::WatchedFolder;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

/// A battle log was saved: full path, result ("VICTORY"...), opponent. Runs on the watcher's thread.
pub type OnSaved = dyn Fn(PathBuf, &'static str, Option<String>) + Send + Sync;

struct Shared {
    queue: Mutex<SaveNotificationQueue>,
    broken: AtomicBool,
    stopped: AtomicBool,
}

struct Current {
    folder: PathBuf,
    watched: WatchedFolder,
    _watcher: notify::RecommendedWatcher,
}

pub struct LogsWatcher {
    shared: Arc<Shared>,
    current: Option<Current>,
    failed_folder: Option<PathBuf>,
}

fn same(a: &Path, b: &Path) -> bool {
    is_same_or_under(a, b) && is_same_or_under(b, a)
}

impl LogsWatcher {
    pub fn new(on_saved: Arc<OnSaved>) -> LogsWatcher {
        let shared = Arc::new(Shared { queue: Mutex::new(SaveNotificationQueue::new(DEFAULT_DELAY, 200)), broken: AtomicBool::new(false), stopped: AtomicBool::new(false) });
        let s = shared.clone();
        std::thread::spawn(move || {
            while !s.stopped.load(Ordering::Relaxed) {
                std::thread::sleep(Duration::from_millis(500));
                let due = lock(&s.queue).take_due(Utc::now(), &|p: &Path| p.is_file());
                for path in due {
                    let Some(name) = log_file_name::parse(&path.to_string_lossy()) else { continue };
                    if !s.stopped.load(Ordering::Relaxed) {
                        on_saved(path, name.result, name.opponent);
                    }
                }
            }
        });
        LogsWatcher { shared, current: None, failed_folder: None }
    }

    pub fn folder(&self) -> Option<&Path> {
        self.current.as_ref().map(|c| c.folder.as_path())
    }

    /// Watches this folder, (re)creating the watcher when the folder changed, the watcher broke, or
    /// the folder at that path is no longer the one watched (renamed in Explorer, or deleted to the
    /// Recycle Bin: the watcher would follow it there, silently). Cheap when nothing changed: called
    /// on every daemon report.
    pub fn watch(&mut self, folder: &Path) {
        if folder.as_os_str().is_empty() {
            return;
        }
        let broken = self.shared.broken.swap(false, Ordering::Relaxed);
        if let Some(c) = &self.current {
            if same(&c.folder, folder) && !broken {
                if c.watched.still_there() {
                    return;
                }
                log::info(&format!("The logs folder was renamed, moved or made again; watching {} afresh.", folder.display()));
            }
        }
        self.stop();
        match self.start(folder) {
            Ok(c) => {
                log::info(&format!("Watching {}", folder.display()));
                self.current = Some(c);
                self.failed_folder = None;
            }
            Err(e) => {
                // Retried on every report (the drive may come back); logged once per folder.
                if !self.failed_folder.as_deref().is_some_and(|f| same(f, folder)) {
                    log::error(&format!("Cannot watch {}", folder.display()), e);
                }
                self.failed_folder = Some(folder.to_path_buf());
            }
        }
    }

    fn start(&self, folder: &Path) -> Result<Current, String> {
        std::fs::create_dir_all(folder).map_err(|e| e.to_string())?;
        let watched = WatchedFolder::new(folder);
        let s = self.shared.clone();
        let mut watcher = notify::recommended_watcher(move |res: notify::Result<notify::Event>| match res {
            Ok(e) => {
                let new_name = matches!(e.kind, EventKind::Create(CreateKind::File | CreateKind::Any) | EventKind::Modify(ModifyKind::Name(RenameMode::To | RenameMode::Any)));
                let path = match e.kind {
                    EventKind::Modify(ModifyKind::Name(RenameMode::Both)) => e.paths.get(1),
                    _ if new_name => e.paths.first(),
                    _ => None,
                };
                if let Some(p) = path {
                    lock(&s.queue).add(p, Utc::now());
                }
            }
            Err(e) => {
                log::error("Logs folder watcher failed; restarting it", e);
                s.broken.store(true, Ordering::Relaxed); // the next daemon report makes a new one
            }
        })
        .map_err(|e| e.to_string())?;
        watcher.watch(folder, RecursiveMode::NonRecursive).map_err(|e| e.to_string())?;
        Ok(Current { folder: folder.to_path_buf(), watched, _watcher: watcher })
    }

    fn stop(&mut self) {
        self.current = None;
        lock(&self.shared.queue).clear_pending();
    }
}

impl Drop for LogsWatcher {
    fn drop(&mut self) {
        self.shared.stopped.store(true, Ordering::Relaxed);
        self.stop();
    }
}
