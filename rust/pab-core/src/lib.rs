//! Platform-independent logic of the tray app, ported from src/Tray/Logic (C#) with the same
//! behavior, so the Rust tray reads the plugin's files, its own 1.6.x files and GitHub's releases
//! exactly as the C# tray does.

#[cfg(windows)]
pub mod dpapi;
pub mod file_ops;
pub mod hashing;
pub mod log_file_name;
pub mod net_json;
pub mod paths;
pub mod pokematch;
pub mod seal_summary;
pub mod settings;
pub mod tcgkb;
pub mod tokens;
pub mod update;
pub mod uploads;
