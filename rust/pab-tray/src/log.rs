//! The app-wide tray.log (Log.cs). Until the player has accepted the disclaimer nothing may be
//! written ("Cancel exits without changing anything"), so lines are held in memory and reach the
//! file on commit; a crash before that still writes them (fatal), a Cancel drops them.
use pab_core::tray_log::{TrayLog, DEFAULT_MAX_BYTES};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

struct State {
    file: Option<TrayLog>,
    /// Some until commit: (is_error, message).
    held: Option<Vec<(bool, String)>>,
}

static STATE: Mutex<State> = Mutex::new(State { file: None, held: Some(Vec::new()) });
const MAX_HELD: usize = 500;

fn state() -> std::sync::MutexGuard<'static, State> {
    STATE.lock().unwrap_or_else(|p| p.into_inner())
}

/// Where lines go (once committed). Lines held so far follow to the new file.
pub fn set_file(path: &Path) {
    state().file = Some(TrayLog::new(path, DEFAULT_MAX_BYTES));
}

pub fn file_path() -> Option<PathBuf> {
    state().file.as_ref().map(|f| f.path.clone())
}

pub fn committed() -> bool {
    state().held.is_none()
}

/// Writes the held lines and every line from now on.
pub fn commit() {
    let mut s = state();
    if s.file.is_none() {
        return;
    }
    if let Some(held) = s.held.take() {
        let file = s.file.as_ref().unwrap();
        for (error, line) in held {
            if error { file.error(&line) } else { file.info(&line) }
        }
    }
}

pub fn info(message: &str) {
    write(false, message);
}

pub fn error(message: &str, cause: impl std::fmt::Display) {
    write(true, &format!("{message}: {cause}"));
}

/// For crashes: whatever the consent state, the lines must not be lost.
pub fn fatal(message: &str, cause: impl std::fmt::Display) {
    commit();
    error(message, cause);
}

fn write(is_error: bool, message: &str) {
    let mut s = state();
    if let Some(held) = s.held.as_mut() {
        if held.len() < MAX_HELD {
            held.push((is_error, message.to_string()));
        }
        return;
    }
    // TrayLog never fails.
    match &s.file {
        Some(f) if is_error => f.error(message),
        Some(f) => f.info(message),
        None => {}
    }
}

#[cfg(test)]
mod tests {
    // One test: the log is process-wide.
    #[test]
    fn held_until_commit() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("tray.log");
        super::info("before a file");
        super::set_file(&path);
        super::error("held", "boom");
        assert!(!path.exists() && !super::committed());
        super::commit();
        super::info("after");
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("INFO  before a file") && text.contains("ERROR held: boom") && text.contains("INFO  after"), "{text}");
        assert!(super::committed() && super::file_path().as_deref() == Some(path.as_path()));
    }
}
