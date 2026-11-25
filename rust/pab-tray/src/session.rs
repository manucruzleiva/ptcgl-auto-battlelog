//! Everything one run of the app shares (Session.cs): the mode, where its files are, the settings
//! (read and written from the UI thread and the daemon, hence the lock) and the embedded payload.
use crate::{embedded, log, sys};
use pab_core::command_line::{self, CommandLine};
use pab_core::game_install::{GameInstall, Payload};
use pab_core::game_locator;
use pab_core::paths::AppPaths;
use pab_core::settings::Settings;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard};

pub struct Session {
    pub exe_path: PathBuf,
    /// Portable because of --portable, with no portable.txt: restarts and the Run key pass it on.
    pub portable_forced: bool,
    paths: Mutex<AppPaths>,
    settings: Mutex<Option<Settings>>,
    payload: Option<Payload<'static>>,
    local_app_data: PathBuf,
    documents: PathBuf,
}

impl Session {
    /// Installed mode: %LOCALAPPDATA% paths. Portable mode: data next to the exe, or in
    /// %LOCALAPPDATA%\...\portable-data when an earlier run found the exe's folder read-only. Nothing
    /// is created here (that waits for consent: prepare_data_folder), except to carry settings over.
    pub fn create(exe_path: &Path, cli: &CommandLine) -> Session {
        Self::create_in(exe_path, cli, sys::local_app_data(), sys::documents(), embedded::payload())
    }

    pub fn create_in(exe_path: &Path, cli: &CommandLine, local_app_data: PathBuf, documents: PathBuf, payload: Option<Payload<'static>>) -> Session {
        let marker = AppPaths::has_portable_marker(exe_path);
        let mut paths = AppPaths::installed(&local_app_data, &documents);
        if cli.portable || marker {
            paths = AppPaths::portable(exe_path, &local_app_data, &documents);
            let fallback = AppPaths::portable_fallback(exe_path, &local_app_data, &documents);
            if !paths.settings_file.exists() && fallback.settings_file.exists() {
                // An earlier run found this folder read-only (and had consent: it saved settings).
                // Still read-only: keep the fallback. Writable now (moved): bring the settings along.
                if paths.try_prepare_root() {
                    let _ = std::fs::copy(&fallback.settings_file, &paths.settings_file);
                } else {
                    paths = fallback;
                }
            }
        }
        Session {
            exe_path: exe_path.to_path_buf(),
            portable_forced: cli.portable && !marker,
            paths: Mutex::new(paths),
            settings: Mutex::new(None),
            payload,
            local_app_data,
            documents,
        }
    }

    pub fn paths(&self) -> AppPaths {
        self.paths.lock().unwrap_or_else(|p| p.into_inner()).clone()
    }

    pub fn portable(&self) -> bool {
        self.paths().is_portable
    }

    /// The plugin and BepInEx, or why this copy cannot install them.
    pub fn payload(&self) -> Result<Payload<'static>, String> {
        self.payload.ok_or_else(|| embedded::INCOMPLETE.to_string())
    }

    pub fn load_settings(&self) {
        let s = Settings::load(&self.paths().settings_file, &|m| log::info(m));
        *self.lock_settings() = Some(s);
    }

    /// Portable: creates the data folder, or switches to %LOCALAPPDATA%\...\portable-data when the
    /// exe's folder cannot be written. Settings found so far are carried over.
    pub fn prepare_data_folder(&self) {
        let paths = self.paths();
        if !paths.is_portable || paths.try_prepare_root() || paths.uses_fallback_data {
            return;
        }
        let fallback = AppPaths::portable_fallback(&self.exe_path, &self.local_app_data, &self.documents);
        if !fallback.try_prepare_root() {
            return;
        }
        log::set_file(&fallback.tray_log_file);
        let current = self.lock_settings().clone();
        *self.paths.lock().unwrap_or_else(|p| p.into_inner()) = fallback.clone();
        match current {
            Some(s) if !fallback.settings_file.exists() => self.save(&s),
            _ => self.load_settings(),
        }
        log::info(&format!("The portable folder is read-only; data kept in {}", fallback.root.display()));
    }

    fn lock_settings(&self) -> MutexGuard<'_, Option<Settings>> {
        self.settings.lock().unwrap_or_else(|p| p.into_inner())
    }

    pub fn read<T>(&self, read: impl FnOnce(&Settings) -> T) -> T {
        read(self.lock_settings().get_or_insert_with(Settings::default))
    }

    /// Changes the settings and saves them at once. A failed save is logged, never returned.
    pub fn update(&self, change: impl FnOnce(&mut Settings)) {
        let mut guard = self.lock_settings();
        let s = guard.get_or_insert_with(Settings::default);
        change(s);
        self.save(s);
    }

    fn save(&self, s: &Settings) {
        if let Err(e) = s.save(&self.paths().settings_file) {
            log::error("Could not save settings", e);
        }
    }

    pub fn game_dir(&self) -> Option<String> {
        self.read(|s| s.game_dir.clone())
    }

    /// The game install the settings point at, or None when unset or not (any more) the game.
    pub fn game(&self) -> Option<GameInstall> {
        let dir = self.game_dir()?;
        game_locator::is_game_dir(Path::new(&dir)).then(|| GameInstall::new(Path::new(&dir)))
    }

    pub fn consent(&self) -> bool {
        pab_core::disclaimer::is_accepted(self.read(|s| s.disclaimer_accepted))
    }

    /// The folder the plugin will save to: the cfg's OutputFolder, else Documents\PTCGL Battle Logs.
    pub fn logs_folder(&self, game: Option<&GameInstall>) -> PathBuf {
        let default = self.paths().default_logs_folder;
        game.and_then(|g| g.resolve_logs_folder(Some(&default))).unwrap_or(default)
    }

    /// A folder other than the default one is in effect ("Use the default logs folder" is offered).
    pub fn has_custom_logs_folder(&self, game: Option<&GameInstall>) -> bool {
        let (a, b) = (self.logs_folder(game), self.paths().default_logs_folder);
        game.is_some() && !(pab_core::file_ops::is_same_or_under(&a, &b) && pab_core::file_ops::is_same_or_under(&b, &a))
    }

    pub fn run_command(&self) -> String {
        let p = self.paths();
        command_line::run_command(&p.installed_exe.to_string_lossy(), p.is_portable && self.portable_forced)
    }
}
