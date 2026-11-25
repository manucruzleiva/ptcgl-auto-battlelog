//! Installed mode, started from anywhere but %LOCALAPPDATA%\PtcglAutoBattleLog (Installer.cs):
//! copies itself there, sets the game up, registers with Windows (all per user) and hands over to
//! the installed copy with --first-run. Also how a newer download upgrades an older install; an
//! older download never replaces a newer install (it starts that one instead).
use crate::win::instance::{self, SingleInstance};
use crate::win::{dialogs, process, registry, shell};
use crate::{game_setup, log, session::Session, setup, PRODUCT_NAME, VERSION};
use pab_core::command_line::FIRST_RUN;
use pab_core::paths::{AppPaths, LOGS_FOLDER_NAME};
use pab_core::self_update;
use std::path::{Path, PathBuf};
use std::time::Duration;

fn error(text: &str) {
    dialogs::error(std::ptr::null_mut(), text);
}

pub fn start_menu_shortcut() -> Option<PathBuf> {
    shell::folder(shell::Folder::Programs).map(|f| f.join(format!("{PRODUCT_NAME}.lnk")))
}

pub fn desktop_shortcut() -> Option<PathBuf> {
    shell::folder(shell::Folder::Desktop).map(|f| f.join(format!("{LOGS_FOLDER_NAME}.lnk")))
}

pub fn run(session: &Session) -> i32 {
    let paths = session.paths();
    if process::is_elevated_with_uac() {
        // An elevated tray would hold the mutex with an administrators-only ACL (every later normal
        // start would fail), and an administrator's credentials would put the whole per-user
        // install into the administrator's profile.
        log::info("Started with \"Run as administrator\": not installing.");
        error(&format!("{PRODUCT_NAME} installs for your Windows user only and does not need administrator rights.\n\nPlease run it again normally (double-click it), not with \"Run as administrator\"."));
        return 1;
    }

    let installed = paths.installed_exe.is_file().then(|| process::product_version(&paths.installed_exe)).flatten();
    if let Some(v) = installed.as_deref().filter(|v| pab_core::update::is_installed_newer(v, VERSION)) {
        return start_newer_install(&paths, v);
    }
    if !setup::ensure_accepted(session, true) {
        return 1;
    }
    log::commit();
    log::info(&format!(
        "Installing {VERSION} from {}{}",
        session.exe_path.display(),
        installed.as_deref().map(|v| format!(" over {v}")).unwrap_or_default()
    ));

    match install(session, &paths) {
        Ok(true) => {}
        Ok(false) => return 1, // already told
        Err(e) => {
            log::error("Install failed", &e);
            error(&format!("Installation failed:\n{e}\n\nDetails are in {}", paths.tray_log_file.display()));
            return 1;
        }
    }
    match std::process::Command::new(&paths.installed_exe).arg(FIRST_RUN).current_dir(&paths.root).spawn() {
        Ok(_) => {
            log::info(&format!("Installed; started {}", paths.installed_exe.display()));
            0
        }
        Err(e) => {
            log::error("Install failed", &e);
            error(&format!("Installation failed:\n{e}\n\nDetails are in {}", paths.tray_log_file.display()));
            1
        }
    }
}

/// Everything under the single-instance mutex. Ok(false): stopped, and the player was told why.
fn install(session: &Session, paths: &AppPaths) -> Result<bool, String> {
    // A running tray (older version, or a portable copy) keeps its exe locked: ask it to exit, and
    // hold the mutex so nothing starts one while files are moving.
    let single = SingleInstance::stop_running_and_acquire(Duration::from_secs(10));
    if single.access_denied {
        error(&format!("A copy of {PRODUCT_NAME} started as administrator is running.\n\nRight-click its icon in the notification area, choose Exit (or sign out of Windows), and run this again."));
        return Ok(false);
    }
    if !single.owned {
        error(&format!("Another copy of {PRODUCT_NAME} is running and did not close.\n\nRight-click its icon in the notification area, choose Exit, and run this again."));
        return Ok(false);
    }

    setup::migrate_legacy(session);
    copy_self(&session.exe_path, paths).map_err(|e| e.to_string())?;

    if setup::locate_or_ask(session).is_some() {
        if let game_setup::RepairOutcome::Failed(e) = game_setup::try_repair(session) {
            dialogs::warning(std::ptr::null_mut(), &format!("The mod could not be added to the game yet:\n{e}\n\n{PRODUCT_NAME} will keep trying in the background."));
        }
    }
    let logs = game_setup::ensure_logs_folder(session);
    register(session, paths, &logs);
    drop(single);
    Ok(true)
}

/// An older download was run (looking for the app, most likely). Installing it would replace a
/// newer version, and its daemon would put the older plugin into the game. Nothing is written: the
/// installed copy is shown or started instead.
fn start_newer_install(paths: &AppPaths, installed: &str) -> i32 {
    log::info(&format!("Version {installed} is installed, newer than this {VERSION}: not installing; starting the installed copy."));
    if !instance::signal(&instance::show_event_name()) {
        if let Err(e) = std::process::Command::new(&paths.installed_exe).current_dir(&paths.root).spawn() {
            log::error(&format!("Could not start {}", paths.installed_exe.display()), e);
        }
    }
    let label = installed.split('+').next().unwrap_or(installed).trim();
    dialogs::info(
        std::ptr::null_mut(),
        &format!(
            "A newer version of {PRODUCT_NAME} (v{label}) is already installed, so this older download (v{VERSION}) was not installed.\n\nThe installed version is running: look for its icon in the notification area, next to the clock. You can delete this download."
        ),
    );
    0
}

/// Copies the running exe over the installed one. Beside it first, then swapped in, so a locked or
/// half-copied exe never replaces a working one.
fn copy_self(exe: &Path, paths: &AppPaths) -> std::io::Result<()> {
    std::fs::create_dir_all(&paths.root)?;
    let staged = PathBuf::from(format!("{}.new", paths.installed_exe.display()));
    std::fs::copy(exe, &staged)?;
    process::unblock(&staged);
    let swapped = self_update::swap(&staged, &paths.installed_exe);
    let _ = std::fs::remove_file(&staged);
    swapped?;
    self_update::cleanup_old(&paths.installed_exe); // fails quietly if the old one is still running
    process::unblock(&paths.installed_exe);
    Ok(())
}

/// Run key, Apps & Features, Start menu, desktop shortcut to the logs. Each step on its own.
fn register(session: &Session, paths: &AppPaths, logs: &Path) {
    let step = |what: &str, r: Result<(), String>| {
        if let Err(e) = r {
            log::error(&format!("Could not create the {what}"), e);
        }
    };
    step("Start with Windows", registry::set_start_with_windows(Some(&session.run_command())).map_err(|e| e.to_string()));
    step("Apps & Features entry", registry::register_uninstall_entry(paths).map_err(|e| e.to_string()));
    let exe = &paths.installed_exe;
    step(
        "Start menu shortcut",
        start_menu_shortcut()
            .ok_or_else(|| "no Start menu folder".to_string())
            .and_then(|lnk| shell::create_shortcut(&lnk, exe, Some(&paths.root), "Saves every Pokémon TCG Live battle log automatically.", Some(exe))),
    );
    step(
        "desktop shortcut",
        desktop_shortcut().ok_or_else(|| "no desktop folder".to_string()).and_then(|lnk| shell::create_shortcut(&lnk, logs, None, "Your Pokémon TCG Live battle logs.", None)),
    );
}
