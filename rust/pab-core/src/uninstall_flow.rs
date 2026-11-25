//! The order of an uninstall, and when it stops (UninstallFlow.cs). Nothing is removed while the
//! game runs or while another tray is still alive; when the game folder cannot be cleaned
//! completely, the registration and the settings stay, so the uninstall can simply be run again.

/// How an uninstall ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UninstallResult {
    /// Everything was removed.
    Done,
    /// The player said no, at the confirmation or while waiting for the game. Nothing changed.
    Cancelled,
    /// --quiet with the game running: nothing changed (nobody can be asked to close it).
    GameRunning,
    /// A running tray did not exit: nothing changed, or it would put the mod back.
    TrayStillRunning,
    /// Some of the mod's files are still in the game: the registration stays.
    GameFilesLeft,
}

/// What an uninstall needs from Windows and from the player.
pub trait UninstallHost {
    /// --quiet: no dialogs at all.
    fn quiet(&self) -> bool;
    /// Portable: the mod is removed from the game, the folder stays.
    fn portable(&self) -> bool;
    fn confirm(&mut self) -> bool;
    fn is_game_running(&mut self) -> bool;
    /// Retry/Cancel until the game is closed; false when the player gave up.
    fn wait_for_game_to_close(&mut self) -> bool;
    /// Asks a running tray to exit and holds the single-instance mutex until release_tray, so no
    /// tray starts (and repairs the mod) while files are removed. False when a tray still runs.
    fn stop_tray(&mut self) -> bool;
    fn release_tray(&mut self);
    /// 1.0's watchdog and Startup shortcut.
    fn stop_legacy(&mut self);
    /// Takes the mod out of the game; None when complete, else what is still there.
    fn remove_from_game(&mut self) -> Option<String>;
    /// Start with Windows; installed: also the Apps & Features entry and the shortcuts.
    fn remove_registration(&mut self);
    /// Installed only: %LOCALAPPDATA%\PtcglAutoBattleLog except identity\ (and a logs folder inside it).
    fn delete_install_folder(&mut self);
    /// Logs the outcome, and tells the player unless quiet.
    fn report(&mut self, result: UninstallResult, detail: Option<&str>);
}

/// Runs the uninstall; the process exit code, 0 only when everything was removed.
pub fn run(host: &mut dyn UninstallHost) -> i32 {
    if host.quiet() {
        if host.is_game_running() {
            return stop(host, UninstallResult::GameRunning, None);
        }
    } else if !host.confirm() || !host.wait_for_game_to_close() {
        return stop(host, UninstallResult::Cancelled, None);
    }

    let stopped = host.stop_tray();
    let outcome = if !stopped {
        Some((UninstallResult::TrayStillRunning, None))
    } else {
        host.stop_legacy();
        match host.remove_from_game() {
            Some(left) => Some((UninstallResult::GameFilesLeft, Some(left))),
            None => {
                host.remove_registration();
                None
            }
        }
    };
    if let Some((result, detail)) = outcome {
        // The C# flow reports from inside the mutex's using block, then releases.
        host.report(result, detail.as_deref());
        host.release_tray();
        return 1;
    }
    host.release_tray();
    host.report(UninstallResult::Done, None);
    // Last: deleting tray.log and then logging again would bring the folder back.
    if !host.portable() {
        host.delete_install_folder();
    }
    0
}

fn stop(host: &mut dyn UninstallHost, result: UninstallResult, detail: Option<&str>) -> i32 {
    host.report(result, detail);
    1
}
