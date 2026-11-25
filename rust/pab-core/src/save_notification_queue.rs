//! Turns the logs folder watcher's noisy events into one "battle log saved" per file
//! (SaveNotificationQueue.cs). The plugin writes name.tmp, renames it, may overwrite it with a
//! longer read and may move it to a corrected name: a file is announced once it has been quiet for
//! the delay, still exists, and was not announced before.

use std::collections::{HashMap, HashSet, VecDeque};
use std::path::{Path, PathBuf};
use std::time::Duration;

use chrono::{DateTime, Utc};

use crate::log_file_name;

pub const DEFAULT_DELAY: Duration = Duration::from_millis(1500);

pub struct SaveNotificationQueue {
    delay: chrono::TimeDelta,
    remember: usize,
    /// By lowercase path (Windows paths ignore case): the path as first seen and when.
    pending: HashMap<String, (PathBuf, DateTime<Utc>)>,
    announced: HashSet<String>,
    announced_order: VecDeque<String>,
}

impl Default for SaveNotificationQueue {
    fn default() -> Self {
        Self::new(DEFAULT_DELAY, 1000)
    }
}

fn key(p: &Path) -> String {
    p.to_string_lossy().to_lowercase()
}

impl SaveNotificationQueue {
    pub fn new(delay: Duration, remember: usize) -> Self {
        SaveNotificationQueue {
            delay: chrono::TimeDelta::from_std(delay).unwrap_or_default(),
            remember: remember.max(1),
            pending: HashMap::new(),
            announced: HashSet::new(),
            announced_order: VecDeque::new(),
        }
    }

    pub fn pending_count(&self) -> usize {
        self.pending.len()
    }

    /// A file was created or renamed into place. False (ignored) unless its name is a battle log
    /// name, or it was announced already. Restarts its delay.
    pub fn add(&mut self, path: &Path, now: DateTime<Utc>) -> bool {
        if log_file_name::parse(&path.to_string_lossy()).is_none() || self.announced.contains(&key(path)) {
            return false;
        }
        self.pending.insert(key(path), (path.to_path_buf(), now));
        true
    }

    /// Files to announce now, in name order; each is returned once, ever (of the last `remember`).
    pub fn take_due(&mut self, now: DateTime<Utc>, exists: &dyn Fn(&Path) -> bool) -> Vec<PathBuf> {
        let ready: Vec<String> = self.pending.iter().filter(|(_, (_, at))| now - *at >= self.delay).map(|(k, _)| k.clone()).collect();
        let mut due = Vec::new();
        for k in ready {
            let (path, _) = self.pending.remove(&k).expect("pending key");
            if !exists(&path) {
                continue; // moved on (renamed to its final name) or deleted
            }
            self.announced.insert(k.clone());
            self.announced_order.push_back(k);
            while self.announced_order.len() > self.remember {
                if let Some(old) = self.announced_order.pop_front() {
                    self.announced.remove(&old);
                }
            }
            due.push(path);
        }
        due.sort_by_key(|p| key(p));
        due
    }

    /// Forget pending events (the watched folder changed). Announced names are kept.
    pub fn clear_pending(&mut self) {
        self.pending.clear();
    }
}
