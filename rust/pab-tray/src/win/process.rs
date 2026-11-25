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

/// The exe's ProductVersion string ("1.6.3+commit"), as FileVersionInfo reads it; None when absent.
pub fn product_version(exe: &std::path::Path) -> Option<String> {
    use windows_sys::Win32::Storage::FileSystem::{GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW};
    let path = wide(&exe.to_string_lossy());
    unsafe {
        let size = GetFileVersionInfoSizeW(path.as_ptr(), std::ptr::null_mut());
        if size == 0 {
            return None;
        }
        let mut data = vec![0u8; size as usize];
        if GetFileVersionInfoW(path.as_ptr(), 0, size, data.as_mut_ptr().cast()) == 0 {
            return None;
        }
        let query = |sub: &str| -> Option<(*const u8, u32)> {
            let mut p = std::ptr::null_mut();
            let mut len = 0u32;
            (VerQueryValueW(data.as_ptr().cast(), wide(sub).as_ptr(), &mut p, &mut len) != 0 && !p.is_null() && len > 0).then_some((p as *const u8, len))
        };
        // The first language and code page the file lists, else US English / Unicode.
        let (lang, cp) = match query(r"\VarFileInfo\Translation") {
            Some((p, len)) if len >= 4 => (u16::from_le_bytes([*p, *p.add(1)]), u16::from_le_bytes([*p.add(2), *p.add(3)])),
            _ => (0x0409, 0x04b0),
        };
        let (p, len) = query(&format!(r"\StringFileInfo\{lang:04x}{cp:04x}\ProductVersion"))?;
        let chars = std::slice::from_raw_parts(p as *const u16, len as usize);
        let end = chars.iter().position(|&c| c == 0).unwrap_or(chars.len());
        Some(String::from_utf16_lossy(&chars[..end]).trim().to_string()).filter(|v| !v.is_empty())
    }
}

/// Ends a process and waits up to 5 s for it (Process.Kill + WaitForExit).
pub fn kill(pid: u32) -> std::io::Result<()> {
    use windows_sys::Win32::System::Threading::{TerminateProcess, WaitForSingleObject, PROCESS_SYNCHRONIZE, PROCESS_TERMINATE};
    unsafe {
        let h = OpenProcess(PROCESS_TERMINATE | PROCESS_SYNCHRONIZE, 0, pid);
        if h.is_null() {
            return Err(std::io::Error::last_os_error());
        }
        let ok = TerminateProcess(h, 1) != 0;
        let err = std::io::Error::last_os_error();
        if ok {
            WaitForSingleObject(h, 5000);
        }
        CloseHandle(h);
        if ok { Ok(()) } else { Err(err) }
    }
}
