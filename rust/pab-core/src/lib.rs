//! Platform-independent logic of the tray app, ported from src/Tray/Logic (C#) with the same
//! behavior, so the Rust tray reads the plugin's files and GitHub's releases exactly as 1.6.x does.

pub mod hashing;
pub mod log_file_name;
pub mod seal_summary;
pub mod update;
