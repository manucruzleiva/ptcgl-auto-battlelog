//! PtcglAutoBattleLog.exe in Rust: the tray app, installer, repair daemon and updater (rust/README.md).
//! Phase 3 in progress: this exe does not replace the C# tray yet.
#![cfg_attr(windows, windows_subsystem = "windows")]
// shortcut: the Windows layer lands before the tray that calls it; drop this once phase 3 wires it up.
#![allow(dead_code)]

mod embedded;
#[cfg(windows)]
mod win;

pub const PRODUCT_NAME: &str = "PTCGL Auto Battle Log";
pub const PUBLISHER: &str = "manucruzleiva";
pub const GAME_NAME: &str = "Pokémon TCG Live";
/// build.sh passes the release's version and repository (forks update from themselves).
pub const VERSION: &str = match option_env!("PAB_VERSION") {
    Some(v) => v,
    None => env!("CARGO_PKG_VERSION"),
};
pub const REPO_OWNER: &str = match option_env!("PAB_REPO_OWNER") {
    Some(v) => v,
    None => "manucruzleiva",
};
pub const REPO_NAME: &str = match option_env!("PAB_REPO_NAME") {
    Some(v) => v,
    None => "ptcgl-auto-battlelog",
};

pub fn project_url() -> String {
    format!("https://github.com/{REPO_OWNER}/{REPO_NAME}")
}

/// Where Pokémon TCG Live is, from every Windows source GameLocator ranks (GameFinder).
#[cfg(windows)]
pub fn find_game(saved_dir: Option<&str>) -> Option<std::path::PathBuf> {
    use pab_core::game_locator;
    use win::shell::{folder, Folder};
    let s = |f| folder(f).map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
    let candidates = game_locator::default_candidates(&s(Folder::Profile), &s(Folder::LocalAppData), &s(Folder::ProgramFiles), &s(Folder::ProgramFilesX86));
    let candidates: Vec<String> = candidates.iter().map(|p| p.to_string_lossy().into_owned()).collect();
    let registry = win::registry::game_locations();
    let running = win::process::game_running_dir().map(|p| p.to_string_lossy().into_owned());
    let inputs = saved_dir.into_iter().chain(candidates.iter().map(String::as_str)).chain(registry.iter().map(String::as_str)).chain(running.as_deref());
    game_locator::find(inputs)
}

fn main() {
    let args = pab_core::command_line::CommandLine::parse(std::env::args().skip(1));
    let _ = args;
}
