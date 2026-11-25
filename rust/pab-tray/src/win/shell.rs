//! Opening things, the Recycle Bin, shortcuts and well-known folders (Shell and the shortcut part of
//! WindowsIntegration.cs).
use super::wide;
use std::path::{Path, PathBuf};
use windows_sys::core::GUID;
use windows_sys::Win32::System::Com::CoTaskMemFree;
use windows_sys::Win32::UI::Shell::{
    FOLDERID_Desktop, FOLDERID_Documents, FOLDERID_LocalAppData, FOLDERID_Profile, FOLDERID_ProgramFiles, FOLDERID_ProgramFilesX86,
    FOLDERID_Programs, FOLDERID_Startup, SHFileOperationW, SHGetKnownFolderPath, ShellExecuteW, FOF_ALLOWUNDO, FOF_NOCONFIRMATION,
    FOF_NOERRORUI, FO_DELETE, SHFILEOPSTRUCTW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

#[derive(Clone, Copy)]
pub enum Folder {
    LocalAppData,
    Documents,
    Desktop,
    /// Start menu\Programs.
    Programs,
    Startup,
    Profile,
    ProgramFiles,
    ProgramFilesX86,
}

/// A well-known folder of the current user, or None when Windows has none (Environment.GetFolderPath).
pub fn folder(f: Folder) -> Option<PathBuf> {
    let id: &GUID = match f {
        Folder::LocalAppData => &FOLDERID_LocalAppData,
        Folder::Documents => &FOLDERID_Documents,
        Folder::Desktop => &FOLDERID_Desktop,
        Folder::Programs => &FOLDERID_Programs,
        Folder::Startup => &FOLDERID_Startup,
        Folder::Profile => &FOLDERID_Profile,
        Folder::ProgramFiles => &FOLDERID_ProgramFiles,
        Folder::ProgramFilesX86 => &FOLDERID_ProgramFilesX86,
    };
    unsafe {
        let mut p = std::ptr::null_mut();
        let hr = SHGetKnownFolderPath(id, 0, std::ptr::null_mut(), &mut p);
        let out = (hr >= 0 && !p.is_null()).then(|| {
            let len = (0..).take_while(|&i| *p.add(i) != 0).count();
            PathBuf::from(String::from_utf16_lossy(std::slice::from_raw_parts(p, len)))
        });
        CoTaskMemFree(p.cast());
        out
    }
}

/// Opens a folder, file or URL with whatever Windows associates with it. False when nothing could.
pub fn open(target: &str) -> bool {
    shell_execute(target, None)
}

fn shell_execute(file: &str, params: Option<&str>) -> bool {
    let params = params.map(wide);
    let r = unsafe {
        ShellExecuteW(
            std::ptr::null_mut(),
            wide("open").as_ptr(),
            wide(file).as_ptr(),
            params.as_ref().map_or(std::ptr::null(), |p| p.as_ptr()),
            std::ptr::null(),
            SW_SHOWNORMAL,
        )
    };
    r as isize > 32
}

/// A text file, in Notepad when nothing is associated with its extension (.log often is not).
pub fn open_text(path: &Path) -> bool {
    let p = path.to_string_lossy();
    open(&p) || shell_execute("notepad.exe", Some(&format!("\"{p}\"")))
}

/// Moves files to the Recycle Bin in one shell operation, without its own confirmation (the caller
/// asked). False when Windows reports an error or the player cancelled.
pub fn recycle(paths: &[PathBuf], owner: windows_sys::Win32::Foundation::HWND) -> bool {
    let mut from: Vec<u16> = Vec::new();
    for p in paths {
        from.extend(p.as_os_str().to_string_lossy().encode_utf16());
        from.push(0);
    }
    from.push(0); // double-null-terminated list
    let mut op: SHFILEOPSTRUCTW = unsafe { std::mem::zeroed() };
    op.hwnd = owner;
    op.wFunc = FO_DELETE;
    op.pFrom = from.as_ptr();
    op.fFlags = (FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI) as u16;
    unsafe { SHFileOperationW(&mut op) == 0 && op.fAnyOperationsAborted == 0 }
}

/// Writes a .lnk file. mslnk writes the format itself: no COM, no WScript.Shell.
pub fn create_shortcut(lnk: &Path, target: &Path, working_dir: Option<&Path>, description: &str, icon: Option<&str>) -> Result<(), String> {
    if let Some(dir) = lnk.parent() {
        std::fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    let mut link = mslnk::ShellLink::new(target).map_err(|e| e.to_string())?;
    link.set_name(Some(description.to_string()));
    if let Some(d) = working_dir {
        link.set_working_dir(Some(d.to_string_lossy().into_owned()));
    }
    if let Some(i) = icon {
        link.set_icon_location(Some(i.to_string()));
    }
    link.create_lnk(lnk).map_err(|e| e.to_string())
}

/// A folder as the player thinks of it: "Documents\PTCGL Battle Logs" rather than the full path.
pub fn friendly(path: &Path) -> String {
    let s = path.to_string_lossy();
    if let Some(docs) = folder(Folder::Documents) {
        let docs = docs.to_string_lossy().trim_end_matches('\\').to_string();
        let prefix = format!("{docs}\\");
        if !docs.is_empty() && s.len() > prefix.len() && s.get(..prefix.len()).is_some_and(|head| head.eq_ignore_ascii_case(&prefix)) {
            return format!("Documents\\{}", &s[prefix.len()..]);
        }
    }
    s.into_owned()
}
