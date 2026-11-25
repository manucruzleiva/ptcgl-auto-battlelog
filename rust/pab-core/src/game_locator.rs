//! Finds the Pokémon TCG Live folder (GameLocator.cs). The caller gathers what needs Windows (the
//! registry, the running process); this only decides, so the order of preference is testable.

use std::path::{Path, PathBuf};

pub const GAME_EXE: &str = "Pokemon TCG Live.exe";
pub const PROCESS_NAME: &str = "Pokemon TCG Live";
pub const COMPANY_FOLDER: &str = "The Pokémon Company International";
pub const GAME_FOLDER: &str = "Pokémon Trading Card Game Live";

pub fn is_game_dir(dir: &Path) -> bool {
    !dir.as_os_str().is_empty() && dir.join(GAME_EXE).is_file()
}

/// The launcher's default install places, most likely first. Empty bases are skipped.
pub fn default_candidates(user_profile: &str, local_app_data: &str, program_files: &str, program_files_x86: &str) -> Vec<PathBuf> {
    let mut list: Vec<PathBuf> = Vec::new();
    for root in [user_profile, local_app_data, program_files, program_files_x86] {
        if root.trim().is_empty() {
            continue;
        }
        let dir = Path::new(root).join(COMPANY_FOLDER).join(GAME_FOLDER);
        let lower = dir.to_string_lossy().to_lowercase();
        if !list.iter().any(|d| d.to_string_lossy().to_lowercase() == lower) {
            list.push(dir);
        }
    }
    list
}

/// The first input that is the game folder. Pass, in this order: the saved folder, the default
/// candidates, the registry's install locations (an InstallLocation or a DisplayIcon such as
/// `"...\Pokemon TCG Live.exe",0`) and the running process's folder or exe.
pub fn find<'a>(inputs: impl IntoIterator<Item = &'a str>) -> Option<PathBuf> {
    inputs.into_iter().filter_map(clean).find(|d| is_game_dir(d))
}

/// Strips quotes and an icon index, and turns a path to an exe into its folder.
pub fn clean(s: &str) -> Option<PathBuf> {
    let mut s = s.trim();
    if let Some(comma) = s.rfind(',').filter(|&c| c > 0 && is_digits(&s[c + 1..])) {
        s = &s[..comma];
    }
    let s = s.trim().trim_matches('"').trim();
    let path = if s.to_lowercase().ends_with(".exe") {
        // Both separators: a registry path on Windows, in tests on any OS.
        let cut = s.rfind(['\\', '/'])?;
        &s[..cut]
    } else {
        s
    };
    (!path.trim().is_empty()).then(|| PathBuf::from(path))
}

fn is_digits(s: &str) -> bool {
    let t = s.trim();
    let t = t.strip_prefix('-').unwrap_or(t);
    !t.is_empty() && t.bytes().all(|b| b.is_ascii_digit())
}
