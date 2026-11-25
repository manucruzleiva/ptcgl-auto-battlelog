//! Finding the game and putting the mod into it (GameSetup.cs), shared by the installer, the first
//! portable run, the daemon and the tray's "Choose game folder..." item. The folder picker is the UI's.
use crate::{log, session::Session, sys};
use pab_core::game_install::{GameInstall, InstallPiece};
use pab_core::legacy_install::{same_folder, LegacyInstall};
use std::path::{Path, PathBuf};

/// The game folder from every source (saved, usual places, registry, running game). Adopts it.
pub fn locate(session: &Session) -> Option<PathBuf> {
    let dir = sys::find_game(session.game_dir().as_deref());
    match &dir {
        Some(d) => adopt(session, d),
        None => log::info("Game folder not found."),
    }
    dir
}

/// Remembers the game folder. A folder seen for the first time decides whether uninstalling may
/// remove BepInEx: only if it was not there before us (or 1.0 recorded that it added it).
pub fn adopt(session: &Session, dir: &Path) {
    let dir_s = dir.to_string_lossy().into_owned();
    if session.game_dir().is_some_and(|known| same_folder(&known, &dir_s)) {
        return;
    }
    let game = GameInstall::new(dir);
    let ours = !game.has_bepinex() || LegacyInstall::new(&session.paths().local_root).bepinex_installed_by_us(&dir_s);
    session.update(|s| {
        s.game_dir = Some(dir_s.clone());
        s.bep_in_ex_installed_by_us = ours;
    });
    log::info(&format!("Game folder: {dir_s}{}", if ours { " (BepInEx added by us)" } else { " (BepInEx already there)" }));
}

#[derive(Debug, PartialEq, Eq)]
pub enum RepairOutcome {
    Done(Vec<InstallPiece>),
    NothingToDo,
    GameRunning,
    NoGame,
    Failed(String),
    ConsentPending,
}

/// Repairs now if the game is closed (Windows locks the loaded plugin, and a running game may be
/// mid update) and the player has accepted the disclaimer (nothing is written into the game before).
pub fn try_repair(session: &Session) -> RepairOutcome {
    let Some(game) = session.game() else { return RepairOutcome::NoGame };
    if !session.consent() {
        return RepairOutcome::ConsentPending;
    }
    let payload = match session.payload() {
        Ok(p) => p,
        Err(e) => return RepairOutcome::Failed(e),
    };
    if game.missing(&payload).is_empty() {
        return RepairOutcome::NothingToDo;
    }
    if sys::game_is_running() {
        return RepairOutcome::GameRunning;
    }
    match game.repair(&payload) {
        Ok(repaired) => {
            log::info(&format!("Repaired: {}", names(&repaired)));
            RepairOutcome::Done(repaired)
        }
        Err(e) => {
            log::error("Repair failed", &e);
            RepairOutcome::Failed(e.to_string())
        }
    }
}

/// The logs folder exists (the plugin would create it too, but the shortcut and the watcher need it now).
pub fn ensure_logs_folder(session: &Session) -> PathBuf {
    let folder = session.logs_folder(session.game().as_ref());
    if let Err(e) = std::fs::create_dir_all(&folder) {
        log::error(&format!("Could not create the logs folder {}", folder.display()), e);
    }
    folder
}

/// "WinHttp, Plugin", as the C# tray logs them.
pub fn names(pieces: &[InstallPiece]) -> String {
    pieces.iter().map(|p| format!("{p:?}")).collect::<Vec<_>>().join(", ")
}
