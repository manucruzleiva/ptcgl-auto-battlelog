//! The plugin's footprint in the game folder (GameInstall.cs): what is missing, putting it back,
//! removing it, and the plugin's cfg.
//!
//! Game updates can delete BepInEx's loader files, so the daemon calls missing/repair over and over:
//! both are cheap and idempotent, and repair never overwrites a BepInEx file that is already there
//! (the player may have their own BepInEx and other mods).

use std::fs;
use std::io::{self, Cursor, Read};
use std::path::{Path, PathBuf};

use crate::file_ops::{self, delete_tree_except, is_same_or_under};
use crate::hashing::sha256;
use crate::{bepinex_config, game_locator};

pub const PLUGIN_FOLDER_NAME: &str = "PtcglAutoBattleLog";
pub const PLUGIN_FILE_NAME: &str = "PtcglAutoBattleLog.Plugin.dll";
pub const LEGACY_PLUGIN_FILE_NAME: &str = "PtcglAutoBattleLog.dll";
pub const CONFIG_FILE_NAME: &str = "ptcgl.autobattlelog.cfg";
pub const CONFIG_SECTION: &str = "General";
pub const OUTPUT_FOLDER_KEY: &str = "OutputFolder";
pub const METADATA_SECTION: &str = "Metadata";
pub const SEAL_MODE_KEY: &str = "Mode";
/// [Metadata] Mode values, in menu order (the plugin's enum). The plugin's default is the first.
pub const SEAL_MODES: [&str; 3] = ["Footer", "Sidecar", "Off"];
pub const DEFAULT_SEAL_MODE: &str = "Footer";

/// Top-level files of the BepInEx 5 win x64 zip, removed with BepInEx on uninstall (winhttp.dll first).
const BEPINEX_ROOT_FILES: [&str; 4] = ["winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt"];

/// The parts that must be in the game folder for the plugin to load.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallPiece {
    /// winhttp.dll: the Doorstop proxy that starts BepInEx.
    WinHttp,
    DoorstopConfig,
    /// BepInEx\core\BepInEx.Preloader.dll
    BepInExPreloader,
    /// BepInEx\core\BepInEx.dll
    BepInExCore,
    /// Our plugin DLL, absent or not the one this tray ships.
    Plugin,
}

const LOADER: [InstallPiece; 4] = [InstallPiece::WinHttp, InstallPiece::DoorstopConfig, InstallPiece::BepInExPreloader, InstallPiece::BepInExCore];

/// What the tray installs into the game; in the shipped exe both are embedded.
#[derive(Clone, Copy)]
pub struct Payload<'a> {
    pub plugin_dll: &'a [u8],
    pub bepinex_zip: &'a [u8],
}

#[derive(Debug, thiserror::Error)]
pub enum InstallError {
    /// A value or folder the cfg or the plugin cannot take; the message is for people.
    #[error("{0}")]
    Invalid(String),
    #[error(transparent)]
    Io(#[from] io::Error),
}

pub struct GameInstall {
    pub game_dir: PathBuf,
    pub plugin_path: PathBuf,
    pub config_path: PathBuf,
    pub bepinex_log_path: PathBuf,
}

impl GameInstall {
    pub fn new(game_dir: &Path) -> GameInstall {
        assert!(!game_dir.as_os_str().is_empty(), "game folder is required");
        GameInstall {
            game_dir: game_dir.to_path_buf(),
            plugin_path: game_dir.join("BepInEx").join("plugins").join(PLUGIN_FOLDER_NAME).join(PLUGIN_FILE_NAME),
            config_path: game_dir.join("BepInEx").join("config").join(CONFIG_FILE_NAME),
            bepinex_log_path: game_dir.join("BepInEx").join("LogOutput.log"),
        }
    }

    pub fn plugin_dir(&self) -> PathBuf {
        self.game_dir.join("BepInEx").join("plugins").join(PLUGIN_FOLDER_NAME)
    }

    fn legacy_plugin_path(&self) -> PathBuf {
        self.plugin_dir().join(LEGACY_PLUGIN_FILE_NAME)
    }

    pub fn has_bepinex(&self) -> bool {
        self.piece_path(InstallPiece::WinHttp).is_file() && self.piece_path(InstallPiece::BepInExCore).is_file()
    }

    pub fn piece_path(&self, piece: InstallPiece) -> PathBuf {
        let core = self.game_dir.join("BepInEx").join("core");
        match piece {
            InstallPiece::WinHttp => self.game_dir.join("winhttp.dll"),
            InstallPiece::DoorstopConfig => self.game_dir.join("doorstop_config.ini"),
            InstallPiece::BepInExPreloader => core.join("BepInEx.Preloader.dll"),
            InstallPiece::BepInExCore => core.join("BepInEx.dll"),
            InstallPiece::Plugin => self.plugin_path.clone(),
        }
    }

    /// Pieces that are absent. Plugin also when its SHA-256 differs from the payload's, it cannot be
    /// read, or 1.0's PtcglAutoBattleLog.dll still sits beside it (both would load).
    pub fn missing(&self, payload: &Payload) -> Vec<InstallPiece> {
        let mut missing: Vec<InstallPiece> = LOADER.into_iter().filter(|p| !self.piece_path(*p).is_file()).collect();
        let current = fs::read(&self.plugin_path).is_ok_and(|b| sha256(&b) == sha256(payload.plugin_dll));
        if !current || self.legacy_plugin_path().is_file() {
            missing.push(InstallPiece::Plugin);
        }
        missing
    }

    /// Puts back what is missing and returns what was missing before. BepInEx files are extracted
    /// only when a loader piece is missing, and only where no file exists. Err when the folder is not
    /// (or no longer) the game's, the zip is unsafe, or a file cannot be written.
    pub fn repair(&self, payload: &Payload) -> io::Result<Vec<InstallPiece>> {
        let before = self.missing(payload);
        if before.is_empty() {
            return Ok(before);
        }
        if !game_locator::is_game_dir(&self.game_dir) {
            return Err(io::Error::new(io::ErrorKind::NotFound, format!("Pokemon TCG Live is not installed in {}", self.game_dir.display())));
        }
        if before.iter().any(|p| *p != InstallPiece::Plugin) {
            self.extract_missing_bepinex_files(payload)?;
        }
        if before.contains(&InstallPiece::Plugin) {
            file_ops::write_atomic(&self.plugin_path, payload.plugin_dll)?;
            let legacy = self.legacy_plugin_path();
            if legacy.is_file() {
                fs::remove_file(legacy)?;
            }
        }
        Ok(before)
    }

    fn extract_missing_bepinex_files(&self, payload: &Payload) -> io::Result<()> {
        let mut zip = zip::ZipArchive::new(Cursor::new(payload.bepinex_zip)).map_err(invalid_zip)?;
        // Map every entry before writing anything: one bad entry means a bad payload.
        let mut plan = Vec::new();
        for i in 0..zip.len() {
            let name = zip.by_index_raw(i).map_err(invalid_zip)?.name().map_err(invalid_zip)?.into_owned();
            if let Some(target) = map_entry(&self.game_dir, &name)? {
                plan.push((i, target));
            }
        }
        for (i, target) in plan {
            if target.exists() {
                continue; // never overwrite
            }
            let mut bytes = Vec::new();
            zip.by_index(i).map_err(invalid_zip)?.read_to_end(&mut bytes)?;
            let mut tmp = target.clone().into_os_string();
            tmp.push(".tmp");
            let tmp = PathBuf::from(tmp);
            if let Some(dir) = target.parent() {
                fs::create_dir_all(dir)?;
            }
            // shortcut: exists-then-rename can overwrite a file that appears in between (C# File.Move refuses); the window is microseconds.
            let result = fs::write(&tmp, &bytes).and_then(|_| if target.exists() { Ok(()) } else { fs::rename(&tmp, &target) });
            let _ = fs::remove_file(&tmp);
            result?;
        }
        Ok(())
    }

    /// Any other mod that needs BepInEx: a .dll under BepInEx\plugins outside our folder, or any
    /// preloader patcher under BepInEx\patchers. When unsure (unreadable folder), says yes.
    pub fn has_other_plugins(&self) -> bool {
        let check = || -> io::Result<bool> {
            let plugins = self.game_dir.join("BepInEx").join("plugins");
            if plugins.is_dir() {
                for e in fs::read_dir(&plugins)? {
                    let e = e?;
                    let path = e.path();
                    if e.file_type()?.is_dir() {
                        if !e.file_name().to_string_lossy().eq_ignore_ascii_case(PLUGIN_FOLDER_NAME) && any_dll(&path, true)? {
                            return Ok(true);
                        }
                    } else if is_dll(&path) {
                        return Ok(true);
                    }
                }
            }
            let patchers = self.game_dir.join("BepInEx").join("patchers");
            Ok(patchers.is_dir() && any_dll(&patchers, true)?)
        };
        check().unwrap_or(true)
    }

    /// Removes our plugin folder and its cfg; BepInEx only if asked and no other mod uses it. A
    /// logs folder the cfg puts inside one of those folders is kept, with its logs; the folders kept
    /// for it are returned. Tries every step, then fails naming what could not be removed.
    pub fn uninstall(&self, remove_bepinex: bool) -> io::Result<Vec<PathBuf>> {
        let mut failures = Vec::new();
        let mut kept = Vec::new();
        let logs = self.resolve_logs_folder(None);
        let mut attempt = |what: &Path, r: io::Result<()>| {
            if let Err(e) = r {
                failures.push(format!("{} ({e})", what.display()));
            }
        };
        let remove_file = |f: &Path| if f.is_file() { fs::remove_file(f) } else { Ok(()) };

        // The plugin itself first: it goes even when its folder has to stay for the logs in it.
        for dll in [self.plugin_path.clone(), self.legacy_plugin_path()] {
            attempt(&dll, remove_file(&dll));
        }
        let mut delete = |dir: PathBuf, attempt: &mut dyn FnMut(&Path, io::Result<()>)| match delete_tree_except(&dir, logs.as_deref()) {
            Ok(true) => kept.push(dir),
            Ok(false) => {}
            Err(e) => attempt(&dir, Err(e)),
        };
        delete(self.plugin_dir(), &mut attempt);
        attempt(&self.config_path, remove_file(&self.config_path));

        if remove_bepinex && !self.has_other_plugins() {
            for name in BEPINEX_ROOT_FILES {
                let f = self.game_dir.join(name);
                attempt(&f, remove_file(&f));
            }
            delete(self.game_dir.join("BepInEx"), &mut attempt);
        }
        if !failures.is_empty() {
            return Err(io::Error::other(format!("Could not remove: {}", failures.join("; "))));
        }
        Ok(kept)
    }

    /// The logs folder from the plugin's cfg, as BepInEx reads it. None when not set, empty (the
    /// plugin's default: Documents\PTCGL Battle Logs, resolved at runtime) or unreadable.
    pub fn read_output_folder(&self) -> Option<String> {
        self.read_config_value(CONFIG_SECTION, OUTPUT_FOLDER_KEY).filter(|v| !v.trim().is_empty())
    }

    /// Sets [General] OutputFolder so BepInEx reads back exactly this folder, creating the file or
    /// section when missing. The plugin reads it at the next game start. Invalid for a value the cfg
    /// cannot hold, one the plugin would refuse, or a folder an uninstall would delete (`app_folders`
    /// are the app's own data folders).
    pub fn write_output_folder(&self, folder: &str, app_folders: &[&Path]) -> Result<(), InstallError> {
        if folder.trim().is_empty() {
            return Err(InstallError::Invalid("Folder is required.".into()));
        }
        if let Some(why) = why_plugin_refuses(folder).map(String::from).or_else(|| self.why_not_logs_folder(folder, app_folders)) {
            return Err(InstallError::Invalid(format!("The mod cannot use this folder: {folder} ({why}).")));
        }
        self.write_config_value(CONFIG_SECTION, OUTPUT_FOLDER_KEY, folder)
    }

    /// Why `folder` must not hold battle logs: uninstalling removes it, because it is at or under
    /// this game's BepInEx folder or one of `app_folders`. A relative folder is taken against the
    /// game folder, like the plugin does. None when it may.
    pub fn why_not_logs_folder(&self, folder: &str, app_folders: &[&Path]) -> Option<String> {
        let folder = folder.trim();
        if folder.is_empty() {
            return None;
        }
        let full = self.game_dir.join(folder); // join keeps an absolute folder as is
        if is_same_or_under(&full, &self.game_dir.join("BepInEx")) {
            return Some("it is inside the game's BepInEx folder, which uninstalling removes".into());
        }
        app_folders.iter().find(|app| is_same_or_under(&full, app)).map(|app| format!("it is inside {}, which uninstalling removes", app.display()))
    }

    /// The folder the plugin saves to, decided as the plugin decides it at game start: the cfg's
    /// OutputFolder (a relative one against the game folder), or `default_folder` when it is empty
    /// or a value BepInEx garbled or would garble (the tray must not open or create such a folder).
    pub fn resolve_logs_folder(&self, default_folder: Option<&Path>) -> Option<PathBuf> {
        let Some(configured) = self.read_output_folder() else { return default_folder.map(Path::to_path_buf) };
        let value = configured.trim();
        if why_plugin_refuses(value).is_some() {
            return default_folder.map(Path::to_path_buf);
        }
        file_ops::full_path(&self.game_dir.join(value)).or_else(|| default_folder.map(Path::to_path_buf))
    }

    /// Back to the default logs folder: OutputFolder becomes empty (never the default path itself,
    /// which BepInEx may not round-trip). No cfg yet = already default.
    pub fn reset_output_folder(&self) -> Result<(), InstallError> {
        if !self.config_path.is_file() {
            return Ok(());
        }
        self.write_config_value(CONFIG_SECTION, OUTPUT_FOLDER_KEY, "")
    }

    /// [Metadata] Mode as its canonical name, or None when absent or not a mode (the plugin then
    /// uses Footer). Read like BepInEx's Enum.Parse: names ignoring case, or the mode's number.
    pub fn read_seal_mode(&self) -> Option<&'static str> {
        let value = self.read_config_value(METADATA_SECTION, SEAL_MODE_KEY)?;
        let value = value.trim();
        if let Some(m) = SEAL_MODES.iter().find(|m| m.eq_ignore_ascii_case(value)) {
            return Some(m);
        }
        match value.as_bytes() {
            [d @ b'0'..=b'2'] => Some(SEAL_MODES[(d - b'0') as usize]),
            _ => None,
        }
    }

    /// Writes [Metadata] Mode; takes effect at the next game start.
    pub fn write_seal_mode(&self, mode: &str) -> Result<(), InstallError> {
        let canonical = SEAL_MODES.iter().find(|m| m.eq_ignore_ascii_case(mode.trim())).ok_or_else(|| InstallError::Invalid(format!("Unknown seal mode: {mode}")))?;
        self.write_config_value(METADATA_SECTION, SEAL_MODE_KEY, canonical)
    }

    /// Any value of the plugin's cfg, as BepInEx reads it. None if absent or unreadable.
    pub fn read_config_value(&self, section: &str, key: &str) -> Option<String> {
        let text = file_ops::try_read_small_text(&self.config_path, 1024 * 1024)?;
        bepinex_config::read_value(&text, section, key)
    }

    /// Sets one value of the plugin's cfg, keeping every other line, the BOM and the line endings.
    pub fn write_config_value(&self, section: &str, key: &str, value: &str) -> Result<(), InstallError> {
        if section.trim().is_empty() || key.trim().is_empty() {
            return Err(InstallError::Invalid("Section and key are required.".into()));
        }
        let bytes = if self.config_path.is_file() { fs::read(&self.config_path)? } else { Vec::new() };
        let bom = bytes.starts_with(b"\xEF\xBB\xBF");
        let text = String::from_utf8_lossy(&bytes[if bom { 3 } else { 0 }..]);
        let updated = bepinex_config::write_value(&text, section, key, value).map_err(InstallError::Invalid)?;
        let mut out = if bom { b"\xEF\xBB\xBF".to_vec() } else { Vec::new() };
        out.extend_from_slice(updated.as_bytes());
        Ok(file_ops::write_atomic(&self.config_path, &out)?)
    }
}

fn invalid_zip(e: zip::result::ZipError) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, format!("The BepInEx payload is not a valid zip: {e}"))
}

fn is_dll(p: &Path) -> bool {
    p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dll")) && p.is_file()
}

fn any_dll(dir: &Path, recursive: bool) -> io::Result<bool> {
    for e in fs::read_dir(dir)? {
        let e = e?;
        let path = e.path();
        if e.file_type()?.is_dir() {
            if recursive && any_dll(&path, true)? {
                return Ok(true);
            }
        } else if is_dll(&path) {
            return Ok(true);
        }
    }
    Ok(false)
}

/// Zip entry name ('/' separated, '\' tolerated) to a path inside `root`. None for directory
/// entries. InvalidData for anything that could land outside the root (rooted, "..", drive letters,
/// invalid characters), by Windows' rules whatever the OS.
pub fn map_entry(root: &Path, entry_name: &str) -> io::Result<Option<PathBuf>> {
    if entry_name.is_empty() || entry_name.ends_with(['/', '\\']) {
        return Ok(None);
    }
    let unsafe_entry = || io::Error::new(io::ErrorKind::InvalidData, format!("Unsafe entry in the BepInEx zip: {entry_name}"));
    let mut target = root.to_path_buf();
    for p in entry_name.split(['/', '\\']) {
        // Windows also drops trailing dots and spaces, which would turn "..." or "a." into something else.
        if p.is_empty() || p.ends_with(['.', ' ']) || p.chars().any(|c| "<>:\"|?*".contains(c) || (c as u32) < 32) {
            return Err(unsafe_entry());
        }
        target.push(p);
    }
    if !is_same_or_under(&target, root) || file_ops::full_path(&target) == file_ops::full_path(root) {
        return Err(unsafe_entry());
    }
    Ok(Some(target))
}

/// Why the plugin would not use this OutputFolder value and falls back to the default folder
/// (LogsFolder.WhyUnkeepable in the plugin): what BepInEx leaves of a path it garbled, or a value
/// BepInEx's own rewrite of the cfg would change. None when the plugin uses it.
pub fn why_plugin_refuses(value: &str) -> Option<&'static str> {
    if value.contains('"') || value.contains("\\'") {
        return Some("BepInEx cannot keep ' or \" in a folder path");
    }
    if value.starts_with('\\') && !value.starts_with("\\\\") {
        return Some("a network path garbled by BepInEx");
    }
    if value.chars().any(|c| c < ' ') {
        return Some("control characters");
    }
    (bepinex_config::convert_to_object(bepinex_config::escape(value).trim()) != value).then_some("BepInEx would change it in its cfg")
}
