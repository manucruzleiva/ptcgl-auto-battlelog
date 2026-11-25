//! Everything that talks to Windows itself. The logic behind it is in pab-core.
pub mod dialogs;
pub mod http;
pub mod instance;
pub mod notify_icon;
pub mod popup;
pub mod process;
pub mod registry;
pub mod shell;

/// A NUL-terminated UTF-16 string for the W APIs.
pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

// Runs on the windows-latest CI. Nothing here writes the registry: the Run value and the Apps &
// Features entry are the developer's own on a real machine.
#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[test]
    fn single_instance_and_signals() {
        let first = instance::SingleInstance::acquire(Duration::ZERO);
        assert!(first.owned && !first.access_denied);
        // Another thread is another owner for a mutex.
        let second = std::thread::spawn(|| instance::SingleInstance::acquire(Duration::from_millis(50)).owned).join().unwrap();
        assert!(!second);

        assert!(!instance::signal(&instance::show_event_name()), "nobody listens yet");
        let signals = instance::TraySignals::create();
        assert!(!signals.exit_requested());
        assert!(instance::signal(&instance::exit_event_name()));
        assert!(signals.exit_requested() && !signals.exit_requested(), "auto-reset: taken once");

        let (tx, rx) = std::sync::mpsc::channel();
        instance::listen(signals.show.clone().unwrap(), move || tx.send(()).unwrap());
        assert!(instance::signal(&instance::show_event_name()));
        rx.recv_timeout(Duration::from_secs(5)).unwrap();
        drop(first);
        let again = std::thread::spawn(|| instance::SingleInstance::acquire(Duration::from_secs(1)).owned).join().unwrap();
        assert!(again, "released on drop");
    }

    #[test]
    fn processes_and_folders() {
        let me = process::process_path(std::process::id()).unwrap();
        assert_eq!(me, std::env::current_exe().unwrap());
        let name = me.file_stem().unwrap().to_string_lossy().to_uppercase();
        assert!(process::ids_by_name(&name).contains(&std::process::id()));
        assert!(!process::game_is_running());
        for f in [shell::Folder::LocalAppData, shell::Folder::Documents, shell::Folder::Programs, shell::Folder::Profile] {
            assert!(shell::folder(f).unwrap().is_dir());
        }
        let docs = shell::folder(shell::Folder::Documents).unwrap();
        assert_eq!(shell::friendly(&docs.join("PTCGL Battle Logs")), r"Documents\PTCGL Battle Logs");
        assert_eq!(shell::friendly(std::path::Path::new(r"D:\Logs")), r"D:\Logs");
        let _ = process::is_elevated_with_uac();
    }

    #[test]
    fn unblock_and_shortcut() {
        let dir = tempfile::tempdir().unwrap();
        let exe = dir.path().join("x.exe");
        std::fs::write(&exe, b"MZ").unwrap();
        let stream = format!("{}:Zone.Identifier", exe.display());
        std::fs::write(&stream, "[ZoneTransfer]\r\nZoneId=3\r\n").unwrap();
        assert!(std::fs::metadata(&stream).is_ok());
        process::unblock(&exe);
        assert!(std::fs::metadata(&stream).is_err() && exe.is_file());

        let lnk = dir.path().join(r"Start\PTCGL Auto Battle Log.lnk");
        shell::create_shortcut(&lnk, &exe, Some(dir.path()), "Saves every battle log.", Some(&exe)).unwrap();
        let logs = dir.path().join("PTCGL Battle Logs");
        std::fs::create_dir(&logs).unwrap();
        shell::create_shortcut(&dir.path().join("logs.lnk"), &logs, None, "Your battle logs.", None).unwrap();
        let bytes = std::fs::read(&lnk).unwrap();
        assert!(bytes.starts_with(&[0x4C, 0, 0, 0]));
        // The icon location is the bare exe path: an "exe,0" there is a file Windows can't find.
        let utf16 = |s: &str| s.encode_utf16().flat_map(u16::to_le_bytes).collect::<Vec<u8>>();
        let has = |needle: &[u8]| bytes.windows(needle.len()).any(|w| w == needle);
        assert!(has(&utf16(&exe.to_string_lossy())) && !has(&utf16(&format!("{},0", exe.display()))));
    }

    #[test]
    fn same_path_ignores_case_and_trailing_separators() {
        assert!(registry::same_path(r"C:\A\B\x.EXE", r"c:\a\b\.\x.exe "));
        assert!(!registry::same_path(r"C:\A\x.exe", r"C:\A\y.exe"));
        assert!(!registry::same_path("", r"C:\A"));
    }

    #[test]
    fn tray_window_icons_and_wakes() {
        use windows_sys::Win32::UI::WindowsAndMessaging::{DispatchMessageW, PeekMessageW, MSG, PM_REMOVE};
        let size = notify_icon::small_icon_size();
        for h in [pab_core::daemon_monitor::TrayHealth::Idle, pab_core::daemon_monitor::TrayHealth::Error] {
            let px = crate::status_icons::draw(crate::embedded::APP_ICON, size, crate::status_icons::dot_color(h)).unwrap();
            assert!(notify_icon::Icon::from_rgba(size, &px).is_some());
        }
        let seen = std::rc::Rc::new(std::cell::Cell::new(0));
        let s = seen.clone();
        let icon = notify_icon::NotifyIcon::create(move |m| {
            if m == notify_icon::TrayMsg::Wake {
                s.set(s.get() + 1);
            }
        })
        .unwrap();
        let waker = icon.waker();
        std::thread::spawn(move || waker.wake()).join().unwrap();
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while seen.get() == 0 && std::time::Instant::now() < deadline {
            let mut msg: MSG = unsafe { std::mem::zeroed() };
            while unsafe { PeekMessageW(&mut msg, std::ptr::null_mut(), 0, 0, PM_REMOVE) } != 0 {
                unsafe { DispatchMessageW(&msg) };
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(seen.get(), 1, "a wake from another thread reaches the window's thread");
    }
}
