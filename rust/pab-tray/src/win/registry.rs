//! Per-user registry entries (WindowsIntegration.cs): start with Windows, the Apps & Features entry,
//! and the game's own Apps & Features entries. Everything lives under HKCU: no admin rights.
use pab_core::{command_line, file_ops, paths::{AppPaths, APP_ID}};
use std::path::Path;
use winreg::enums::{HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY, KEY_WRITE};
use winreg::RegKey;

const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const STARTUP_APPROVED_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
const UNINSTALL_ROOT: &str = r"Software\Microsoft\Windows\CurrentVersion\Uninstall";

fn hkcu() -> RegKey {
    RegKey::predef(HKEY_CURRENT_USER)
}

fn uninstall_key() -> String {
    format!(r"{UNINSTALL_ROOT}\{APP_ID}")
}

/// Same file by full path, ignoring case (PathUtil.Same).
pub fn same_path(a: &str, b: &str) -> bool {
    let (a, b) = (Path::new(a.trim()), Path::new(b.trim()));
    file_ops::is_same_or_under(a, b) && file_ops::is_same_or_under(b, a)
}

fn run_value_points_to(command: Option<String>, exe: &Path) -> bool {
    let target = command.as_deref().and_then(command_line::run_value_target);
    target.is_some_and(|t| exe.to_str().is_some_and(|e| same_path(t, e)))
}

fn run_value() -> Option<String> {
    hkcu().open_subkey(RUN_KEY).ok()?.get_value(APP_ID).ok()
}

/// The Run value starts this exe and Task Manager has not disabled it. The value name is shared by
/// the installed and portable copies, so it must point at `exe`.
pub fn is_start_with_windows(exe: &Path) -> bool {
    if !run_value_points_to(run_value(), exe) {
        return false;
    }
    // Task Manager's switch: first byte even (02, 06) = enabled, odd (03) = disabled.
    let data = hkcu().open_subkey(STARTUP_APPROVED_KEY).and_then(|k| k.get_raw_value(APP_ID)).map(|v| v.bytes);
    data.map_or(true, |d| d.first().is_none_or(|b| b & 1 == 0))
}

/// Writes the Run value (a full command line, command_line::run_command) or deletes it.
pub fn set_start_with_windows(command: Option<&str>) -> std::io::Result<()> {
    match command {
        Some(c) => hkcu().create_subkey(RUN_KEY)?.0.set_value(APP_ID, &c)?,
        None => {
            if let Ok(run) = hkcu().open_subkey_with_flags(RUN_KEY, KEY_WRITE) {
                let _ = run.delete_value(APP_ID);
            }
        }
    }
    // A "disabled" left behind by Task Manager would silently outrank the menu's choice.
    if let Ok(approved) = hkcu().open_subkey_with_flags(STARTUP_APPROVED_KEY, KEY_WRITE) {
        let _ = approved.delete_value(APP_ID);
    }
    Ok(())
}

/// Removes the Run value only when it starts `exe` (not another copy).
pub fn remove_start_with_windows(exe: &Path) -> std::io::Result<()> {
    if run_value_points_to(run_value(), exe) {
        set_start_with_windows(None)?;
    }
    Ok(())
}

/// Written at install and refreshed at every start, so DisplayVersion follows OTA updates.
pub fn register_uninstall_entry(paths: &AppPaths) -> std::io::Result<()> {
    let exe = paths.installed_exe.to_string_lossy().into_owned();
    let size_kb = std::fs::metadata(&paths.installed_exe).map_or(0, |m| m.len() / 1024);
    let (k, _) = hkcu().create_subkey(uninstall_key())?;
    let url = crate::project_url();
    for (name, value) in [
        ("DisplayName", crate::PRODUCT_NAME.to_string()),
        ("DisplayVersion", crate::VERSION.to_string()),
        ("Publisher", crate::PUBLISHER.to_string()),
        ("DisplayIcon", format!("{exe},0")),
        ("InstallLocation", paths.root.to_string_lossy().into_owned()),
        ("UninstallString", format!("\"{exe}\" --uninstall")),
        ("QuietUninstallString", format!("\"{exe}\" --uninstall --quiet")),
        ("URLInfoAbout", url.clone()),
        ("URLUpdateInfo", format!("{url}/releases")),
    ] {
        k.set_value(name, &value)?;
    }
    k.set_value("NoModify", &1u32)?;
    k.set_value("NoRepair", &1u32)?;
    k.set_value("EstimatedSize", &(size_kb.clamp(1, i32::MAX as u64) as u32))?;
    if k.get_raw_value("InstallDate").is_err() {
        k.set_value("InstallDate", &local_date())?;
    }
    Ok(())
}

fn local_date() -> String {
    // yyyyMMdd in local time, like DateTime.Now.
    let mut st = unsafe { std::mem::zeroed::<windows_sys::Win32::Foundation::SYSTEMTIME>() };
    unsafe { windows_sys::Win32::System::SystemInformation::GetLocalTime(&mut st) };
    format!("{:04}{:02}{:02}", st.wYear, st.wMonth, st.wDay)
}

pub fn has_uninstall_entry() -> bool {
    hkcu().open_subkey(uninstall_key()).is_ok()
}

pub fn remove_uninstall_entry() -> std::io::Result<()> {
    match hkcu().delete_subkey_all(uninstall_key()) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        r => r,
    }
}

/// InstallLocation and DisplayIcon of every Apps & Features entry that looks like the game (HKCU,
/// HKLM and HKLM's 32-bit view). A locked-down or missing hive is just one source fewer.
pub fn game_locations() -> Vec<String> {
    let mut found = Vec::new();
    let roots = [(HKEY_CURRENT_USER, 0), (HKEY_LOCAL_MACHINE, KEY_WOW64_64KEY), (HKEY_LOCAL_MACHINE, KEY_WOW64_32KEY)];
    for (hive, view) in roots {
        let Ok(uninstall) = RegKey::predef(hive).open_subkey_with_flags(UNINSTALL_ROOT, KEY_READ | view) else { continue };
        for name in uninstall.enum_keys().flatten() {
            let Ok(entry) = uninstall.open_subkey_with_flags(&name, KEY_READ | view) else { continue };
            let Ok(display) = entry.get_value::<String, _>("DisplayName") else { continue };
            let lower = display.to_lowercase();
            if !lower.contains("tcg live") && !lower.contains("trading card game live") {
                continue;
            }
            found.extend(["InstallLocation", "DisplayIcon"].into_iter().filter_map(|v| entry.get_value::<String, _>(v).ok()));
        }
    }
    found
}
