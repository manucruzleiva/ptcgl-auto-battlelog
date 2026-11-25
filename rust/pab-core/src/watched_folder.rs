//! The logs folder as it was when a watcher started on it (WatchedFolder.cs). A watcher holds the
//! folder itself open, not its path: renamed, or deleted to the Recycle Bin (a move), it is still
//! watched where it went while the plugin saves into a folder made again at the old path.
//! still_there notices, so the tray can start a watcher on the folder that is there now.

use std::path::{Path, PathBuf};
use std::time::SystemTime;

pub struct WatchedFolder {
    pub path: PathBuf,
    created: Option<SystemTime>,
}

impl WatchedFolder {
    pub fn new(path: &Path) -> WatchedFolder {
        WatchedFolder { path: path.to_path_buf(), created: created(path) }
    }

    /// The folder at `path` is the one first seen: it exists with the same creation time (a folder
    /// made again has a new one, unless the file system cannot tell).
    pub fn still_there(&self) -> bool {
        self.path.is_dir() && created(&self.path) == self.created
    }
}

fn created(path: &Path) -> Option<SystemTime> {
    // shortcut: where the OS gives no creation time (some Linux file systems) this falls back to "same folder"; the tray runs on Windows.
    std::fs::metadata(path).ok().filter(|m| m.is_dir()).and_then(|m| m.created().ok())
}
