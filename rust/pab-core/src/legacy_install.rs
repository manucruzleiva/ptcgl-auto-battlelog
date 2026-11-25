//! What the 1.0 PowerShell installer left in %LOCALAPPDATA%\PtcglAutoBattleLog (LegacyInstall.cs):
//! its payload, scripts, game-dir.txt and the "we installed BepInEx" flag. The tray reads the last
//! two before removing everything, so an upgrade keeps the game folder and the BepInEx ownership.
//! Stopping the watchdog and deleting its Startup shortcut need Windows and are the app's job.

use std::fs;
use std::path::{Path, PathBuf};

use crate::file_ops;
use crate::paths::APP_ID;

pub const GAME_DIR_FILE_NAME: &str = "game-dir.txt";
pub const BEPINEX_FLAG_FILE_NAME: &str = "bepinex-installed-by-us.flag";
pub const WATCHDOG_SCRIPT_NAME: &str = "watchdog.ps1";
/// In the user's Startup folder; started the watchdog at logon.
pub const STARTUP_SHORTCUT_NAME: &str = "PTCGL Auto Battle Log.lnk";
const PAYLOAD_DIR_NAME: &str = "payload";
const FILE_NAMES: [&str; 8] = [WATCHDOG_SCRIPT_NAME, "common.ps1", "uninstall.ps1", "install.ps1", GAME_DIR_FILE_NAME, BEPINEX_FLAG_FILE_NAME, "watchdog.log", "watchdog.log.old"];

pub struct LegacyInstall {
    pub root: PathBuf,
}

impl LegacyInstall {
    pub fn new(root: &Path) -> LegacyInstall {
        assert!(!root.as_os_str().is_empty(), "folder is required");
        LegacyInstall { root: root.to_path_buf() }
    }

    /// Any 1.0 leftover is present.
    pub fn exists(&self) -> bool {
        self.root.join(PAYLOAD_DIR_NAME).is_dir() || FILE_NAMES.iter().any(|f| self.root.join(f).is_file())
    }

    /// The game folder 1.0 used (written with a BOM); None when absent or blank.
    pub fn read_game_dir(&self) -> Option<String> {
        self.read_line(GAME_DIR_FILE_NAME)
    }

    /// 1.0 added BepInEx to this game folder. Its flag holds the folder it was written for; a flag
    /// for another folder does not count, an unreadable or empty one does.
    pub fn bepinex_installed_by_us(&self, game_dir: &str) -> bool {
        if !self.root.join(BEPINEX_FLAG_FILE_NAME).is_file() {
            return false;
        }
        self.read_line(BEPINEX_FLAG_FILE_NAME).is_none_or(|owner| same_folder(&owner, game_dir))
    }

    /// Deletes every leftover. Best effort: returns the paths that could not be removed.
    pub fn remove_leftovers(&self) -> Vec<PathBuf> {
        let mut failed = Vec::new();
        for f in FILE_NAMES {
            let path = self.root.join(f);
            if path.is_file() && fs::remove_file(&path).is_err() {
                failed.push(path);
            }
        }
        let payload = self.root.join(PAYLOAD_DIR_NAME);
        if payload.is_dir() && fs::remove_dir_all(&payload).is_err() {
            failed.push(payload);
        }
        failed
    }

    fn read_line(&self, name: &str) -> Option<String> {
        let text = file_ops::try_read_small_text(&self.root.join(name), 64 * 1024)?;
        text.split('\n').map(|l| l.trim().trim_matches('\u{feff}').trim()).find(|t| !t.is_empty()).map(String::from)
    }
}

/// The command line of 1.0's hidden PowerShell watchdog (from WMI Win32_Process).
pub fn is_watchdog_command_line(command_line: &str) -> bool {
    let lower = command_line.to_lowercase();
    lower.find(&APP_ID.to_lowercase()).is_some_and(|app| lower[app + 1..].contains(WATCHDOG_SCRIPT_NAME))
}

pub fn same_folder(a: &str, b: &str) -> bool {
    let norm = |s: &str| s.trim().trim_end_matches(['\\', '/']).to_lowercase();
    !a.trim().is_empty() && !b.trim().is_empty() && norm(a) == norm(b)
}
