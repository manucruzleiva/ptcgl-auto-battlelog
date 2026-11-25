//! The game's process (GameProcess in WindowsIntegration.cs), the tray's own token, and the Mark
//! of the Web.
use super::wide;
use pab_core::game_locator::PROCESS_NAME;
use std::path::PathBuf;
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Security::{GetTokenInformation, TokenElevationType, TokenElevationTypeFull, TOKEN_QUERY};
use windows_sys::Win32::Storage::FileSystem::DeleteFileW;
use windows_sys::Win32::System::Diagnostics::ToolHelp::{CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcess, OpenProcessToken, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION};

/// Ids of the processes whose exe is `<name>.exe`, ignoring case (Process.GetProcessesByName).
pub fn ids_by_name(name: &str) -> Vec<u32> {
    let want = format!("{name}.exe").to_lowercase();
    let mut ids = Vec::new();
    unsafe {
        let snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snap == INVALID_HANDLE_VALUE {
            return ids;
        }
        let mut e: PROCESSENTRY32W = std::mem::zeroed();
        e.dwSize = size_of::<PROCESSENTRY32W>() as u32;
        let mut ok = Process32FirstW(snap, &mut e) != 0;
        while ok {
            let len = e.szExeFile.iter().position(|&c| c == 0).unwrap_or(e.szExeFile.len());
            if String::from_utf16_lossy(&e.szExeFile[..len]).to_lowercase() == want {
                ids.push(e.th32ProcessID);
            }
            ok = Process32NextW(snap, &mut e) != 0;
        }
        CloseHandle(snap);
    }
    ids
}

pub fn game_is_running() -> bool {
    !ids_by_name(PROCESS_NAME).is_empty()
}

/// Folder of the running game, or None.
pub fn game_running_dir() -> Option<PathBuf> {
    ids_by_name(PROCESS_NAME).into_iter().find_map(process_path).and_then(|p| p.parent().map(PathBuf::from))
}

/// Full path of a process's exe. Needs only "limited information" rights, so it works for elevated
/// or protected processes too. None when unknown.
pub fn process_path(pid: u32) -> Option<PathBuf> {
    unsafe {
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if h.is_null() {
            return None;
        }
        let mut buf = vec![0u16; 32768];
        let mut size = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut size) != 0;
        CloseHandle(h);
        ok.then(|| PathBuf::from(String::from_utf16_lossy(&buf[..size as usize])))
    }
}

/// Started with "Run as administrator" while UAC is on (TokenElevationTypeFull). With UAC off every
/// process of an administrator has the full token and nothing differs, so that does not count.
pub fn is_elevated_with_uac() -> bool {
    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return false;
        }
        let mut kind = 0i32;
        let mut len = 0u32;
        let ok = GetTokenInformation(token, TokenElevationType, (&mut kind as *mut i32).cast(), size_of::<i32>() as u32, &mut len) != 0;
        CloseHandle(token);
        ok && kind == TokenElevationTypeFull
    }
}

/// Removes the Mark of the Web (the Zone.Identifier stream a browser adds to downloads), so the
/// installed copy starts at logon without an "Open File - Security Warning". Best effort.
pub fn unblock(file: &std::path::Path) {
    let stream = format!("{}:Zone.Identifier", file.display());
    unsafe { DeleteFileW(wide(&stream).as_ptr()) };
}
