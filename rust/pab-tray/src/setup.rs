//! The steps the installer, a portable first run and the tray share: the disclaimer
//! (DisclaimerDialog.EnsureAccepted), the game folder when it is not found (GameSetup.Locate with
//! askPlayer), and upgrading from 1.0 (LegacyMigration.cs).
use crate::win::{dialogs, process, shell};
use crate::{embedded, game_setup, log, session::Session, GAME_NAME, PRODUCT_NAME};
use pab_core::game_locator::{self, GAME_EXE};
use pab_core::legacy_install::{self, LegacyInstall, STARTUP_SHORTCUT_NAME};
use std::path::{Path, PathBuf};
use windows_sys::Win32::Foundation::HWND;

/// Asks for consent unless it was ever accepted; stores it on acceptance. False: the player said
/// no, and the caller stops without changing anything.
// shortcut: a Yes/No message box with the whole text (3.6 KB fits); the egui window of phase 4 can
// replace it if a scrolling page reads better.
pub fn ensure_accepted(session: &Session, install: bool) -> bool {
    if session.consent() {
        return true;
    }
    let text = format!(
        "Please read this before {} {PRODUCT_NAME}.\n\n{}\n\nDo you accept it? Yes: I understand, {}.",
        if install { "installing" } else { "using" },
        embedded::disclaimer_text().trim(),
        if install { "install" } else { "continue" }
    );
    if !dialogs::confirm(std::ptr::null_mut(), &text, false) {
        log::info("Disclaimer declined.");
        return false;
    }
    // First, so the held log lines land in the right folder.
    if session.portable() {
        session.prepare_data_folder();
    }
    log::commit();
    session.update(|s| s.disclaimer_accepted = pab_core::disclaimer::VERSION);
    log::info(&format!("Disclaimer v{} accepted.", pab_core::disclaimer::VERSION));
    true
}

/// The game folder from every source; when none has it, the player is asked to pick it.
pub fn locate_or_ask(session: &Session) -> Option<PathBuf> {
    if let Some(dir) = game_setup::locate(session) {
        return Some(dir);
    }
    dialogs::info(std::ptr::null_mut(), &format!("{GAME_NAME} was not found in its usual place.\n\nPlease select the game's folder: the one that contains \"{GAME_EXE}\"."));
    let dir = ask_for_game_folder(std::ptr::null_mut(), None)?;
    game_setup::adopt(session, &dir);
    Some(dir)
}

/// A folder picker that only accepts the game's folder (GameSetup.AskForFolder); None when cancelled.
pub fn ask_for_game_folder(owner: HWND, initial: Option<&Path>) -> Option<PathBuf> {
    let mut initial = initial.map(Path::to_path_buf);
    loop {
        let picked = dialogs::pick_folder(owner, &format!("Select the {GAME_NAME} folder (the one that contains \"{GAME_EXE}\")."), initial.as_deref(), false)?;
        let inside = picked.join("Pokemon TCG Live");
        if let Some(dir) = game_locator::find([picked.to_string_lossy().as_ref(), inside.to_string_lossy().as_ref()]) {
            return Some(dir);
        }
        if !dialogs::retry_cancel(owner, &format!("\"{GAME_EXE}\" is not in that folder.\n\n{}", picked.display())) {
            return None;
        }
        initial = Some(picked);
    }
}

/// 1.0 started its PowerShell watchdog from this shortcut at logon.
pub fn legacy_startup_shortcut() -> Option<PathBuf> {
    shell::folder(shell::Folder::Startup).map(|f| f.join(STARTUP_SHORTCUT_NAME))
}

/// Upgrading from 1.0 (the PowerShell installer): keep its game folder and BepInEx ownership, stop
/// its hidden watchdog (it would put the 1.0 plugin back), remove its Startup shortcut and files.
/// A few file checks when there is nothing to do.
pub fn migrate_legacy(session: &Session) {
    let legacy = LegacyInstall::new(&session.paths().local_root);
    let shortcut = legacy_startup_shortcut().filter(|s| s.is_file());
    if !legacy.exists() && shortcut.is_none() {
        return;
    }
    log::info(&format!("Migrating the 1.0 installation in {}", legacy.root.display()));
    if let Some(dir) = legacy.read_game_dir().filter(|d| game_locator::is_game_dir(Path::new(d))) {
        if !session.game_dir().is_some_and(|g| game_locator::is_game_dir(Path::new(&g))) {
            let ours = legacy.bepinex_installed_by_us(&dir);
            session.update(|s| {
                s.game_dir = Some(dir.clone());
                s.bep_in_ex_installed_by_us = ours;
            });
            log::info(&format!("1.0 game folder: {dir}{}", if ours { " (1.0 added BepInEx)" } else { "" }));
        }
    }
    stop_legacy_watchdogs();
    if let Some(s) = shortcut {
        if std::fs::remove_file(&s).is_err() {
            log::info(&format!("Could not delete {}", s.display()));
        }
    }
    let failed = legacy.remove_leftovers();
    if !failed.is_empty() {
        log::info(&format!("1.0 files left behind: {}", failed.iter().map(|p| p.display().to_string()).collect::<Vec<_>>().join(", ")));
    }
}

/// Kills 1.0's hidden PowerShell watchdog, found by its command line. Windows shows command lines
/// only through WMI, so a hidden Windows PowerShell lists them (a constant script: nothing of ours
/// is put into it); the match and the kill are here.
pub fn stop_legacy_watchdogs() {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    const SCRIPT: &str = "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe' OR Name='pwsh.exe'\" | ForEach-Object { \"$($_.ProcessId)`t$($_.CommandLine)\" }";
    let Some(system) = std::env::var_os("SystemRoot").map(PathBuf::from) else { return };
    let powershell = system.join(r"System32\WindowsPowerShell\v1.0\powershell.exe");
    let out = match std::process::Command::new(powershell)
        .args(["-NoProfile", "-NonInteractive", "-Command", SCRIPT])
        .current_dir(std::env::temp_dir())
        .creation_flags(CREATE_NO_WINDOW)
        .output()
    {
        Ok(o) => o,
        // WMI can be broken or disabled. The watchdog also quits by itself once its payload is gone.
        Err(e) => return log::error("Could not look for the 1.0 watchdog", e),
    };
    let me = std::process::id();
    for line in String::from_utf8_lossy(&out.stdout).lines() {
        let Some((pid, command_line)) = line.split_once('\t') else { continue };
        let Ok(pid) = pid.trim().parse::<u32>() else { continue };
        if pid == me || !legacy_install::is_watchdog_command_line(command_line) {
            continue;
        }
        match process::kill(pid) {
            Ok(()) => log::info(&format!("Stopped the 1.0 watchdog (pid {pid}).")),
            Err(e) => log::error(&format!("Could not stop the 1.0 watchdog (pid {pid})"), e),
        }
    }
}
