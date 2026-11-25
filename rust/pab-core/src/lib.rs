//! Platform-independent logic of the tray app, ported from src/Tray/Logic (C#) with the same
//! behavior, so the Rust tray reads the plugin's files, its own 1.6.x files and GitHub's releases
//! exactly as the C# tray does.

pub mod bepinex_config;
pub mod command_line;
pub mod daemon_monitor;
pub mod disclaimer;
#[cfg(windows)]
pub mod dpapi;
pub mod file_ops;
pub mod game_install;
pub mod game_locator;
pub mod hashing;
pub mod ladder_status;
pub mod legacy_install;
pub mod log_file_name;
pub mod net_json;
pub mod paths;
pub mod plugin_status;
pub mod pokematch;
pub mod save_notification_queue;
pub mod seal_summary;
pub mod self_update;
pub mod settings;
pub mod tcgkb;
pub mod tokens;
pub mod tray_log;
pub mod uninstall_flow;
pub mod update;
pub mod uploads;
pub mod watched_folder;
