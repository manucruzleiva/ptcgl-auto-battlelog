//! --uninstall (Apps & Features, or the tray's "Uninstall..."), Uninstaller.cs: stops the tray, takes
//! the mod out of the game, removes the per-user registration and the install folder. Battle logs
//! stay. Portable copies only take the mod out ("Remove mod from game..."); the player deletes the
//! folder. The order, and when it stops without changing anything, is pab_core::uninstall_flow's;
//! this is the Windows side of it.
use crate::win::instance::SingleInstance;
use crate::win::{dialogs, process, registry};
use crate::{installer, log, session::Session, setup, sys, GAME_NAME, PRODUCT_NAME};
use pab_core::file_ops;
use pab_core::game_install::GameInstall;
use pab_core::game_locator;
use pab_core::paths::{AppPaths, APP_ID};
use pab_core::uninstall_flow::{self, UninstallHost, UninstallResult};
use std::path::{Path, PathBuf};
use std::time::Duration;

/// The plugin's signing identity outlives an uninstall, like the sealed logs that name it: a
/// reinstall continues the same chain.
const KEPT_FOLDER_NAME: &str = "identity";

struct Host<'a> {
    session: &'a Session,
    quiet: bool,
    logs: PathBuf,
    instance: Option<SingleInstance>,
}

pub fn run(session: &Session, quiet: bool) -> i32 {
    log::commit();
    // A working directory inside the install folder (the Start menu shortcut sets one) would keep it from being deleted.
    let _ = std::env::set_current_dir(std::env::temp_dir());
    log::info(&format!(
        "Uninstall requested ({}{}).",
        if quiet { "quiet" } else { "interactive" },
        if session.portable() { ", portable" } else { "" }
    ));
    let logs = session.logs_folder(session.game().as_ref());
    uninstall_flow::run(&mut Host { session, quiet, logs, instance: None })
}

fn none() -> windows_sys::Win32::Foundation::HWND {
    std::ptr::null_mut()
}

impl UninstallHost for Host<'_> {
    fn quiet(&self) -> bool {
        self.quiet
    }

    fn portable(&self) -> bool {
        self.session.portable()
    }

    fn confirm(&mut self) -> bool {
        let logs = self.logs.display();
        let text = if self.portable() {
            format!("Remove the mod from {GAME_NAME}?\n\nYour battle logs stay in:\n{logs}")
        } else {
            format!("Uninstall {PRODUCT_NAME}?\n\nThe mod is removed from {GAME_NAME}. Your battle logs stay in:\n{logs}")
        };
        dialogs::confirm(none(), &text, false)
    }

    fn is_game_running(&mut self) -> bool {
        process::game_is_running()
    }

    fn wait_for_game_to_close(&mut self) -> bool {
        while process::game_is_running() {
            if !dialogs::retry_cancel(none(), &format!("{GAME_NAME} is running. Close it, then click Retry.")) {
                return false;
            }
        }
        true
    }

    fn stop_tray(&mut self) -> bool {
        let i = SingleInstance::stop_running_and_acquire(Duration::from_secs(10));
        let owned = i.owned;
        self.instance = Some(i);
        owned
    }

    fn release_tray(&mut self) {
        self.instance = None;
    }

    fn stop_legacy(&mut self) {
        setup::stop_legacy_watchdogs();
        if let Some(s) = setup::legacy_startup_shortcut() {
            let _ = std::fs::remove_file(s);
        }
    }

    fn remove_from_game(&mut self) -> Option<String> {
        remove_from_game(self.session)
    }

    fn remove_registration(&mut self) {
        let paths = self.session.paths();
        let report = |what: &str, r: std::io::Result<()>| {
            if let Err(e) = r {
                log::error(&format!("Could not remove the {what}"), e);
            }
        };
        report("Start with Windows", registry::remove_start_with_windows(&paths.installed_exe));
        if self.portable() {
            return;
        }
        report("Apps & Features entry", registry::remove_uninstall_entry());
        for lnk in [installer::start_menu_shortcut(), installer::desktop_shortcut()].into_iter().flatten() {
            let _ = std::fs::remove_file(lnk);
        }
    }

    fn delete_install_folder(&mut self) {
        delete_install_folder(&self.session.paths(), &self.session.exe_path, &self.logs);
    }

    fn report(&mut self, result: UninstallResult, detail: Option<&str>) {
        let logs = self.logs.display();
        let portable = self.portable();
        match result {
            UninstallResult::Done => {
                log::info("Uninstall finished.");
                if !self.quiet {
                    let head = if portable {
                        let dir = self.session.exe_path.parent().map(|d| d.display().to_string()).unwrap_or_default();
                        format!("The mod was removed from the game. You can now delete this folder:\n{dir}")
                    } else {
                        format!("{PRODUCT_NAME} was uninstalled.")
                    };
                    dialogs::info(none(), &format!("{head}\n\nYour battle logs are still in:\n{logs}"));
                }
            }
            UninstallResult::Cancelled => log::info("Uninstall cancelled; nothing was changed."),
            UninstallResult::GameRunning => log::info(&format!("{GAME_NAME} is running: nothing was removed (exit code 1). Close the game and uninstall again.")),
            UninstallResult::TrayStillRunning => {
                log::info("The running tray did not exit: nothing was removed (exit code 1).");
                if !self.quiet {
                    dialogs::error(
                        none(),
                        &format!(
                            "Another copy of {PRODUCT_NAME} is running and did not close (it may be waiting for an answer in a window).\n\nClose that window, or right-click its icon in the notification area and choose Exit, then {} again. Nothing was removed.",
                            if portable { "remove the mod" } else { "uninstall" }
                        ),
                    );
                }
            }
            UninstallResult::GameFilesLeft => {
                log::info("Some of the mod's files are still in the game; nothing else was removed, so the uninstall can be run again (exit code 1).");
                if !self.quiet {
                    dialogs::warning(
                        none(),
                        &format!(
                            "Some of the mod's files could not be removed from {GAME_NAME}:\n{}\n\nNothing else was changed: {PRODUCT_NAME} is still {}. Close the game (and anything else that may use its files), then {}",
                            detail.unwrap_or(""),
                            if portable { "set up" } else { "installed" },
                            if portable { "start this app and choose Remove mod from game... again." } else { "uninstall again." }
                        ),
                    );
                }
            }
        }
    }
}

/// Our plugin folder and cfg, and BepInEx when we added it and no other mod uses it. None when
/// complete, else what could not be removed (the game holds its files while running).
pub fn remove_from_game(session: &Session) -> Option<String> {
    let dir = session.game_dir().filter(|d| game_locator::is_game_dir(Path::new(d))).map(PathBuf::from).or_else(|| sys::find_game(session.game_dir().as_deref()));
    let Some(dir) = dir else {
        log::info("No game folder: nothing to remove from the game.");
        return None;
    };
    let remove_bepinex = session.read(|s| s.bep_in_ex_installed_by_us);
    match GameInstall::new(&dir).uninstall(remove_bepinex) {
        Ok(kept) => {
            log::info(&format!("Removed the mod from {}{}", dir.display(), if remove_bepinex { " (BepInEx too, unless another mod uses it)" } else { "" }));
            for folder in kept {
                log::info(&format!("Kept {}: the logs folder is inside it.", folder.display()));
            }
            // Forget the game: if this copy runs again, it adopts the folder afresh (and owns the BepInEx it adds).
            session.update(|s| {
                s.game_dir = None;
                s.bep_in_ex_installed_by_us = false;
            });
            None
        }
        Err(e) => {
            log::error(&format!("Removing the mod from {} was incomplete", dir.display()), &e);
            Some(e.to_string())
        }
    }
}

/// Only ever %LOCALAPPDATA%\PtcglAutoBattleLog: a bug must never turn into deleting something else.
fn is_our_install_folder(root: &Path) -> bool {
    let expected = AppPaths::installed(&sys::local_app_data(), &sys::documents()).local_root;
    registry::same_path(&root.to_string_lossy(), &expected.to_string_lossy())
        && root.file_name().is_some_and(|n| n.to_string_lossy().eq_ignore_ascii_case(APP_ID))
        && root.parent().is_some_and(|p| p.parent().is_some())
}

/// Deletes %LOCALAPPDATA%\PtcglAutoBattleLog except identity\, and except the logs folder when
/// someone put it in there (battle logs are never deleted). What is in use (this exe, when Apps &
/// Features started the installed copy) goes after this process exits.
fn delete_install_folder(paths: &AppPaths, running_exe: &Path, logs: &Path) {
    let root = &paths.root;
    if !is_our_install_folder(root) {
        return log::error("Refusing to delete an unexpected folder", root.display());
    }
    if !root.is_dir() {
        return;
    }
    if registry::same_path(&root.to_string_lossy(), &logs.to_string_lossy()) {
        return log::info(&format!("Kept {}: it is the logs folder.", root.display()));
    }
    let mut leftovers = false;
    let mut keep = vec![KEPT_FOLDER_NAME.to_string()];
    match std::fs::read_dir(root) {
        Ok(entries) => {
            for e in entries.flatten() {
                let name = e.file_name().to_string_lossy().into_owned();
                if name.eq_ignore_ascii_case(KEPT_FOLDER_NAME) {
                    continue;
                }
                let path = e.path();
                if path.is_dir() {
                    match file_ops::delete_tree_except(&path, Some(logs)) {
                        Ok(true) => {
                            log::info(&format!("Kept {} in {}: it is the logs folder.", logs.display(), root.display()));
                            keep.push(name);
                        }
                        Ok(false) => {}
                        Err(_) => leftovers = true,
                    }
                } else if std::fs::remove_file(&path).is_err() {
                    leftovers = true; // this exe, while it runs
                }
            }
        }
        Err(e) => {
            log::error(&format!("Could not list {}", root.display()), e);
            leftovers = true;
        }
    }
    let inside = running_exe.parent().is_some_and(|d| registry::same_path(&d.to_string_lossy(), &root.to_string_lossy()));
    if leftovers || inside {
        delete_after_exit(root, &keep);
    } else if std::fs::read_dir(root).is_ok_and(|mut d| d.next().is_none()) {
        let _ = std::fs::remove_dir(root);
    }
}

/// Windows cannot delete a running exe, so a hidden Windows PowerShell waits for this process to
/// exit and deletes the rest. Nothing about the folder is put into its command line: the script is a
/// constant, the folder and the process id travel in environment variables, so no path can change
/// what runs. Not -EncodedCommand either: antivirus products treat hidden encoded PowerShell as suspicious.
fn delete_after_exit(root: &Path, keep: &[String]) {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    // Kept names are joined with '|', which no Windows file name can contain.
    const SCRIPT: &str = "$ErrorActionPreference = 'SilentlyContinue'; \
        $root = $env:PTCGL_ABL_DELETE_ROOT; $keep = @($env:PTCGL_ABL_KEEP -split '\\|'); \
        if (-not $root -or -not (Test-Path -LiteralPath $root -PathType Container)) { exit }; \
        Wait-Process -Id ([int]$env:PTCGL_ABL_WAIT_PID) -Timeout 600; \
        for ($i = 0; $i -lt 40; $i++) { \
          $rest = @(Get-ChildItem -LiteralPath $root -Force | Where-Object { $keep -notcontains $_.Name }); \
          if ($rest.Count -eq 0) { break }; \
          $rest | Remove-Item -Recurse -Force; Start-Sleep -Milliseconds 500 }; \
        if (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) { Remove-Item -LiteralPath $root -Force }";
    let Some(system) = std::env::var_os("SystemRoot").map(PathBuf::from) else {
        return log::error("Could not schedule the removal of the install folder; delete it by hand", root.display());
    };
    let started = std::process::Command::new(system.join(r"System32\WindowsPowerShell\v1.0\powershell.exe"))
        .args(["-NoProfile", "-NonInteractive", "-Command", SCRIPT])
        .current_dir(std::env::temp_dir()) // never inside the folder being deleted
        .env("PTCGL_ABL_DELETE_ROOT", root)
        .env("PTCGL_ABL_KEEP", keep.join("|"))
        .env("PTCGL_ABL_WAIT_PID", std::process::id().to_string())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn();
    match started {
        Ok(_) => log::info(&format!("Scheduled the removal of {} after exit.", root.display())),
        Err(e) => log::error(&format!("Could not schedule the removal of {}; delete it by hand", root.display()), e),
    }
}
