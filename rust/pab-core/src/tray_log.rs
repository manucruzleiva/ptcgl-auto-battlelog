//! tray.log, the file to ask for in a bug report (TrayLog.cs). Diagnostics must never take the tray
//! down, so nothing here fails. One line per call, opened and closed each time so another process
//! (the installer, an uninstall) can append too; rotated to tray.log.1 past max_bytes.

use std::collections::HashMap;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Arc, LazyLock, Mutex};

/// Instances writing the same file share one lock, so rotation cannot race an append.
static LOCKS: LazyLock<Mutex<HashMap<String, Arc<Mutex<()>>>>> = LazyLock::new(Default::default);

pub const DEFAULT_MAX_BYTES: u64 = 512 * 1024;

pub struct TrayLog {
    pub path: PathBuf,
    max_bytes: u64,
    gate: Arc<Mutex<()>>,
}

impl TrayLog {
    pub fn new(path: &Path, max_bytes: u64) -> TrayLog {
        let key = crate::file_ops::full_path(path).unwrap_or_else(|| path.to_path_buf()).to_string_lossy().to_lowercase();
        let gate = LOCKS.lock().unwrap_or_else(|p| p.into_inner()).entry(key).or_default().clone();
        TrayLog { path: path.to_path_buf(), max_bytes: if max_bytes > 0 { max_bytes } else { DEFAULT_MAX_BYTES }, gate }
    }

    pub fn info(&self, message: &str) {
        self.write("INFO ", message);
    }

    pub fn error(&self, message: &str) {
        self.write("ERROR", message);
    }

    fn write(&self, level: &str, message: &str) {
        let nl = if cfg!(windows) { "\r\n" } else { "\n" };
        let line = format!("{} {level} {message}{nl}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f"));
        let _guard = self.gate.lock().unwrap_or_else(|p| p.into_inner());
        if let Some(dir) = self.path.parent().filter(|d| !d.as_os_str().is_empty()) {
            let _ = fs::create_dir_all(dir);
        }
        self.rotate_if_needed(line.len() as u64);
        if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(&self.path) {
            let _ = f.write_all(line.as_bytes());
        }
    }

    fn rotate_if_needed(&self, incoming: u64) {
        let Ok(meta) = fs::metadata(&self.path) else { return };
        if meta.len() + incoming <= self.max_bytes {
            return;
        }
        let mut rotated = self.path.as_os_str().to_owned();
        rotated.push(".1");
        // A failed rename keeps appending to the big file rather than lose the line.
        let _ = fs::rename(&self.path, PathBuf::from(rotated));
    }
}
