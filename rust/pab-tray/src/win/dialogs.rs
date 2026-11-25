//! Message boxes, the folder picker and the clipboard (Dialogs.cs). Owned by the tray's hidden
//! window and made topmost, so they open in front instead of behind whatever window is active.
use super::wide;
use std::path::{Path, PathBuf};
use windows_sys::Win32::Foundation::{GlobalFree, HWND, LPARAM};
use windows_sys::Win32::System::Com::CoTaskMemFree;
use windows_sys::Win32::System::DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData};
use windows_sys::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
use windows_sys::Win32::System::Ole::CF_UNICODETEXT;
use windows_sys::Win32::UI::Shell::{SHBrowseForFolderW, SHGetPathFromIDListW, BFFM_INITIALIZED, BFFM_SETSELECTIONW, BIF_NEWDIALOGSTYLE, BIF_NONEWFOLDERBUTTON, BIF_RETURNONLYFSDIRS, BROWSEINFOW};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    MessageBoxW, SendMessageW, IDRETRY, IDYES, MB_DEFBUTTON2, MB_ICONERROR, MB_ICONINFORMATION, MB_ICONQUESTION, MB_ICONWARNING, MB_OK, MB_RETRYCANCEL,
    MB_SETFOREGROUND, MB_TOPMOST, MB_YESNO, MESSAGEBOX_STYLE,
};

fn show(owner: HWND, text: &str, style: MESSAGEBOX_STYLE) -> i32 {
    unsafe { MessageBoxW(owner, wide(text).as_ptr(), wide(crate::PRODUCT_NAME).as_ptr(), style | MB_SETFOREGROUND | MB_TOPMOST) }
}

pub fn info(owner: HWND, text: &str) {
    show(owner, text, MB_OK | MB_ICONINFORMATION);
}

pub fn warning(owner: HWND, text: &str) {
    show(owner, text, MB_OK | MB_ICONWARNING);
}

pub fn error(owner: HWND, text: &str) {
    show(owner, text, MB_OK | MB_ICONERROR);
}

/// Yes or No, No by default; true for Yes.
pub fn confirm(owner: HWND, text: &str, warn: bool) -> bool {
    show(owner, text, MB_YESNO | MB_DEFBUTTON2 | if warn { MB_ICONWARNING } else { MB_ICONQUESTION }) == IDYES
}

/// Retry or Cancel; true for Retry.
pub fn retry_cancel(owner: HWND, text: &str) -> bool {
    show(owner, text, MB_RETRYCANCEL | MB_ICONWARNING) == IDRETRY
}

unsafe extern "system" fn browse_callback(hwnd: HWND, msg: u32, _: LPARAM, data: LPARAM) -> i32 {
    if msg == BFFM_INITIALIZED && data != 0 {
        unsafe { SendMessageW(hwnd, BFFM_SETSELECTIONW, 1, data) };
    }
    0
}

/// A folder chosen by the player, or None when cancelled. Needs COM on this thread (main does it).
pub fn pick_folder(owner: HWND, description: &str, initial: Option<&Path>, allow_new_folder: bool) -> Option<PathBuf> {
    let title = wide(description);
    let start = initial.filter(|p| p.is_dir()).map(|p| wide(&p.to_string_lossy()));
    let mut bi: BROWSEINFOW = unsafe { std::mem::zeroed() };
    bi.hwndOwner = owner;
    bi.lpszTitle = title.as_ptr();
    bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | if allow_new_folder { 0 } else { BIF_NONEWFOLDERBUTTON };
    bi.lpfn = Some(browse_callback);
    bi.lParam = start.as_ref().map_or(0, |s| s.as_ptr() as LPARAM);
    unsafe {
        let pidl = SHBrowseForFolderW(&bi);
        if pidl.is_null() {
            return None;
        }
        let mut buf = vec![0u16; 32768];
        let ok = SHGetPathFromIDListW(pidl, buf.as_mut_ptr()) != 0;
        CoTaskMemFree(pidl as *const _);
        let len = buf.iter().position(|&c| c == 0).unwrap_or(0);
        (ok && len > 0).then(|| PathBuf::from(String::from_utf16_lossy(&buf[..len])))
    }
}

/// Puts text on the clipboard. False when another app holds it.
pub fn set_clipboard_text(owner: HWND, text: &str) -> bool {
    let w = wide(text);
    unsafe {
        for _ in 0..5 {
            if OpenClipboard(owner) != 0 {
                let mem = GlobalAlloc(GMEM_MOVEABLE, w.len() * 2);
                let mut ok = false;
                if !mem.is_null() {
                    let p = GlobalLock(mem) as *mut u16;
                    if !p.is_null() {
                        std::ptr::copy_nonoverlapping(w.as_ptr(), p, w.len());
                        GlobalUnlock(mem);
                        EmptyClipboard();
                        ok = !SetClipboardData(CF_UNICODETEXT as u32, mem).is_null();
                    }
                    if !ok {
                        GlobalFree(mem); // the clipboard owns it only once SetClipboardData succeeded
                    }
                }
                CloseClipboard();
                return ok;
            }
            std::thread::sleep(std::time::Duration::from_millis(100));
        }
    }
    false
}
