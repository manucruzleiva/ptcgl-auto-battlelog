//! Where the tray keeps its own files (AppPaths.cs). Installed: everything per user under
//! %LOCALAPPDATA%\PtcglAutoBattleLog. Portable: <exe folder>\data, or
//! %LOCALAPPDATA%\PtcglAutoBattleLog\portable-data when the exe's folder is read-only.

use std::path::{Path, PathBuf};

pub const APP_ID: &str = "PtcglAutoBattleLog";
pub const EXE_NAME: &str = "PtcglAutoBattleLog.exe";
pub const LOGS_FOLDER_NAME: &str = "PTCGL Battle Logs";
pub const PORTABLE_MARKER_FILE_NAME: &str = "portable.txt";
pub const PORTABLE_DATA_FOLDER_NAME: &str = "data";
pub const PORTABLE_FALLBACK_FOLDER_NAME: &str = "portable-data";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppPaths {
    /// Data folder: settings.json, tray.log, updates, the tcgkb and Pokematch token files.
    pub root: PathBuf,
    /// %LOCALAPPDATA%\PtcglAutoBattleLog in both modes: plugin-status.json, the plugin's identity.
    pub local_root: PathBuf,
    /// The exe that runs the tray: the installed copy, or the portable exe itself.
    pub installed_exe: PathBuf,
    pub old_exe: PathBuf,
    pub settings_file: PathBuf,
    /// The plugin knows nothing about portable mode: it always writes here.
    pub plugin_status_file: PathBuf,
    pub tray_log_file: PathBuf,
    pub updates_dir: PathBuf,
    pub default_logs_folder: PathBuf,
    pub is_portable: bool,
    pub uses_fallback_data: bool,
}

impl AppPaths {
    pub fn installed(local_app_data: &Path, documents: &Path) -> AppPaths {
        Self::build(local_app_data, documents, None, None, false)
    }

    pub fn portable(exe: &Path, local_app_data: &Path, documents: &Path) -> AppPaths {
        let data = exe.parent().unwrap_or(Path::new("")).join(PORTABLE_DATA_FOLDER_NAME);
        Self::build(local_app_data, documents, Some(exe), Some(data), false)
    }

    pub fn portable_fallback(exe: &Path, local_app_data: &Path, documents: &Path) -> AppPaths {
        let data = local_app_data.join(APP_ID).join(PORTABLE_FALLBACK_FOLDER_NAME);
        Self::build(local_app_data, documents, Some(exe), Some(data), true)
    }

    fn build(local_app_data: &Path, documents: &Path, portable_exe: Option<&Path>, portable_data: Option<PathBuf>, fallback: bool) -> AppPaths {
        let local_root = local_app_data.join(APP_ID);
        let root = portable_data.unwrap_or_else(|| local_root.clone());
        let installed_exe = portable_exe.map(Path::to_path_buf).unwrap_or_else(|| root.join(EXE_NAME));
        let mut old = installed_exe.as_os_str().to_owned();
        old.push(".old");
        AppPaths {
            settings_file: root.join("settings.json"),
            plugin_status_file: local_root.join("plugin-status.json"),
            tray_log_file: root.join("tray.log"),
            updates_dir: root.join("updates"),
            default_logs_folder: documents.join(LOGS_FOLDER_NAME),
            old_exe: PathBuf::from(old),
            is_portable: portable_exe.is_some(),
            uses_fallback_data: portable_exe.is_some() && fallback,
            installed_exe,
            local_root,
            root,
        }
    }

    /// True when portable.txt sits next to the exe.
    pub fn has_portable_marker(exe: &Path) -> bool {
        exe.parent().is_some_and(|d| !d.as_os_str().is_empty() && d.join(PORTABLE_MARKER_FILE_NAME).is_file())
    }

    /// Creates root and proves a file can be written in it (a folder can exist and be read-only).
    pub fn try_prepare_root(&self) -> bool {
        let probe = self.root.join(format!(".write-test-{}", std::process::id()));
        std::fs::create_dir_all(&self.root).is_ok() && std::fs::write(&probe, b"").is_ok() && std::fs::remove_file(&probe).is_ok()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_layout_as_the_csharp_tray() {
        let (lad, docs) = (Path::new("/lad"), Path::new("/docs"));
        let p = AppPaths::installed(lad, docs);
        assert_eq!(p.root, Path::new("/lad/PtcglAutoBattleLog"));
        assert_eq!(p.installed_exe, Path::new("/lad/PtcglAutoBattleLog/PtcglAutoBattleLog.exe"));
        assert_eq!(p.old_exe, Path::new("/lad/PtcglAutoBattleLog/PtcglAutoBattleLog.exe.old"));
        assert_eq!(p.settings_file, Path::new("/lad/PtcglAutoBattleLog/settings.json"));
        assert_eq!(p.default_logs_folder, Path::new("/docs/PTCGL Battle Logs"));
        assert!(!p.is_portable && !p.uses_fallback_data);

        let exe = Path::new("/usb/pab/Renamed.exe");
        let q = AppPaths::portable(exe, lad, docs);
        assert_eq!((q.root.as_path(), q.installed_exe.as_path()), (Path::new("/usb/pab/data"), exe));
        assert_eq!(q.plugin_status_file, Path::new("/lad/PtcglAutoBattleLog/plugin-status.json"), "the plugin always writes to LOCALAPPDATA");
        assert!(q.is_portable && !q.uses_fallback_data);
        let f = AppPaths::portable_fallback(exe, lad, docs);
        assert_eq!(f.root, Path::new("/lad/PtcglAutoBattleLog/portable-data"));
        assert!(f.uses_fallback_data);
    }
}
