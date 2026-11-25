//! The few Windows facts the background services need, with stand-ins elsewhere so their logic
//! runs in `cargo test` on Linux too.
use std::path::PathBuf;

#[cfg(windows)]
pub use crate::win::process::game_is_running;

#[cfg(not(windows))]
pub fn game_is_running() -> bool {
    false
}

#[cfg(windows)]
pub fn find_game(saved: Option<&str>) -> Option<PathBuf> {
    crate::find_game(saved)
}

#[cfg(not(windows))]
pub fn find_game(saved: Option<&str>) -> Option<PathBuf> {
    pab_core::game_locator::find(saved)
}

#[cfg(windows)]
pub fn local_app_data() -> PathBuf {
    crate::win::shell::folder(crate::win::shell::Folder::LocalAppData).unwrap_or_default()
}

#[cfg(windows)]
pub fn documents() -> PathBuf {
    crate::win::shell::folder(crate::win::shell::Folder::Documents).unwrap_or_default()
}

#[cfg(not(windows))]
pub fn local_app_data() -> PathBuf {
    std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default().join(".local/share")
}

#[cfg(not(windows))]
pub fn documents() -> PathBuf {
    std::env::var_os("HOME").map(PathBuf::from).unwrap_or_default().join("Documents")
}
