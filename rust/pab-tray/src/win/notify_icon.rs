//! The notification-area icon (NotifyIcon in the C# tray): a hidden window that owns the icon,
//! receives its clicks and balloon clicks, and wakes the UI thread for work posted from others.
//! The tray-icon crate has no balloons, so this talks to Shell_NotifyIconW itself.
use super::wide;
use std::cell::RefCell;
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{CreateBitmap, CreateDIBSection, DeleteObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIIF_ERROR, NIIF_INFO, NIIF_WARNING, NIM_ADD, NIM_DELETE, NIM_MODIFY,
    NIM_SETVERSION, NIN_BALLOONUSERCLICK, NOTIFYICONDATAW, NOTIFYICON_VERSION_4,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CreateIconIndirect, CreateWindowExW, DefWindowProcW, DestroyIcon, DestroyWindow, DispatchMessageW, GetMessageW, GetSystemMetrics, PostMessageW,
    PostQuitMessage, RegisterClassW, RegisterWindowMessageW, TranslateMessage, HICON, ICONINFO, MSG, SM_CXSMICON, WM_APP, WM_CONTEXTMENU,
    WM_ENDSESSION, WM_LBUTTONUP, WNDCLASSW,
};

const WM_TRAY: u32 = WM_APP + 1;
const WM_WAKE: u32 = WM_APP + 2;
const ICON_ID: u32 = 1;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrayMsg {
    LeftClick,
    ContextMenu,
    BalloonClick,
    /// Work was posted from another thread.
    Wake,
    /// Windows is ending the session.
    EndSession,
    /// Explorer restarted: the icon must be added again.
    TaskbarCreated,
}

#[derive(Clone, Copy)]
pub enum BalloonIcon {
    Info,
    Warning,
    Error,
}

type Handler = Box<dyn FnMut(TrayMsg)>;

thread_local! {
    static HANDLER: RefCell<Option<Handler>> = const { RefCell::new(None) };
    /// A Wake that arrived while the handler was busy (in a menu or dialog's modal loop).
    static MISSED_WAKE: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
    static TASKBAR_CREATED: std::cell::Cell<u32> = const { std::cell::Cell::new(0) };
}

/// Wakes the UI thread from any thread. Copyable and Send: it only holds the window's handle.
#[derive(Clone, Copy)]
pub struct Waker(isize);

impl Waker {
    pub fn wake(&self) {
        unsafe { PostMessageW(self.0 as HWND, WM_WAKE, 0, 0) };
    }
}

pub struct NotifyIcon {
    pub hwnd: HWND,
    added: bool,
    icon: HICON,
    tip: String,
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    let event = match msg {
        WM_TRAY => match (lparam & 0xFFFF) as u32 {
            WM_LBUTTONUP => Some(TrayMsg::LeftClick),
            WM_CONTEXTMENU => Some(TrayMsg::ContextMenu),
            NIN_BALLOONUSERCLICK => Some(TrayMsg::BalloonClick),
            _ => None,
        },
        WM_WAKE => Some(TrayMsg::Wake),
        WM_ENDSESSION if wparam != 0 => Some(TrayMsg::EndSession),
        m if m != 0 && m == TASKBAR_CREATED.with(|t| t.get()) => Some(TrayMsg::TaskbarCreated),
        _ => None,
    };
    match event {
        Some(e) => {
            // A handler already running (a modal loop inside it) gets nothing re-entrant: the
            // message is dropped, except Wake, which is sent again once the handler returns.
            let ran = HANDLER.with(|h| match h.try_borrow_mut() {
                Ok(mut h) => {
                    if let Some(f) = h.as_mut() {
                        f(e);
                    }
                    true
                }
                Err(_) => false,
            });
            if !ran && e == TrayMsg::Wake {
                MISSED_WAKE.with(|m| m.set(true));
            } else if ran && MISSED_WAKE.with(|m| m.replace(false)) {
                unsafe { PostMessageW(hwnd, WM_WAKE, 0, 0) };
            }
            0
        }
        None => unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) },
    }
}

impl NotifyIcon {
    /// Creates the hidden window on this (the UI) thread. Messages go to `handler`.
    pub fn create(handler: impl FnMut(TrayMsg) + 'static) -> Result<NotifyIcon, String> {
        HANDLER.with(|h| *h.borrow_mut() = Some(Box::new(handler)));
        let class = wide("PtcglAutoBattleLogTray");
        unsafe {
            TASKBAR_CREATED.with(|t| t.set(RegisterWindowMessageW(wide("TaskbarCreated").as_ptr())));
            let hinstance = GetModuleHandleW(std::ptr::null());
            let wc = WNDCLASSW { lpfnWndProc: Some(wndproc), hInstance: hinstance, lpszClassName: class.as_ptr(), ..std::mem::zeroed() };
            RegisterClassW(&wc);
            // A real (never shown) top-level window: a message-only one would miss TaskbarCreated.
            let hwnd = CreateWindowExW(0, class.as_ptr(), class.as_ptr(), 0, 0, 0, 0, 0, std::ptr::null_mut(), std::ptr::null_mut(), hinstance, std::ptr::null());
            if hwnd.is_null() {
                return Err(format!("CreateWindowExW failed: {}", std::io::Error::last_os_error()));
            }
            Ok(NotifyIcon { hwnd, added: false, icon: std::ptr::null_mut(), tip: String::new() })
        }
    }

    pub fn waker(&self) -> Waker {
        Waker(self.hwnd as isize)
    }

    fn data(&self) -> NOTIFYICONDATAW {
        let mut d: NOTIFYICONDATAW = unsafe { std::mem::zeroed() };
        d.cbSize = size_of::<NOTIFYICONDATAW>() as u32;
        d.hWnd = self.hwnd;
        d.uID = ICON_ID;
        d
    }

    /// Shows the icon (again, after Explorer restarted).
    pub fn show(&mut self) {
        let mut d = self.data();
        d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = self.icon;
        copy(&mut d.szTip, &self.tip);
        unsafe {
            Shell_NotifyIconW(NIM_DELETE, &d);
            self.added = Shell_NotifyIconW(NIM_ADD, &d) != 0;
            d.Anonymous.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, &d);
        }
    }

    pub fn set_icon(&mut self, icon: HICON) {
        self.icon = icon;
        let mut d = self.data();
        d.uFlags = NIF_ICON;
        d.hIcon = icon;
        unsafe { Shell_NotifyIconW(NIM_MODIFY, &d) };
    }

    pub fn set_tip(&mut self, tip: &str) {
        if self.tip == tip {
            return;
        }
        self.tip = tip.to_string();
        let mut d = self.data();
        d.uFlags = NIF_TIP | NIF_SHOWTIP;
        copy(&mut d.szTip, tip);
        unsafe { Shell_NotifyIconW(NIM_MODIFY, &d) };
    }

    /// A balloon (a toast on Windows 10/11). Its click arrives as TrayMsg::BalloonClick.
    pub fn balloon(&self, title: &str, text: &str, icon: BalloonIcon) {
        let mut d = self.data();
        d.uFlags = NIF_INFO;
        copy(&mut d.szInfoTitle, title);
        copy(&mut d.szInfo, text);
        d.dwInfoFlags = match icon {
            BalloonIcon::Info => NIIF_INFO,
            BalloonIcon::Warning => NIIF_WARNING,
            BalloonIcon::Error => NIIF_ERROR,
        };
        unsafe { Shell_NotifyIconW(NIM_MODIFY, &d) };
    }

    /// Gone from the notification area at once, not at the next mouse-over.
    pub fn remove(&mut self) {
        if self.added {
            unsafe { Shell_NotifyIconW(NIM_DELETE, &self.data()) };
            self.added = false;
        }
    }
}

impl Drop for NotifyIcon {
    fn drop(&mut self) {
        self.remove();
        unsafe { DestroyWindow(self.hwnd) };
        HANDLER.with(|h| h.borrow_mut().take());
    }
}

/// Copies `s` into a fixed UTF-16 field, clipped with "…" to fit with its NUL.
fn copy(field: &mut [u16], s: &str) {
    let clipped = crate::clip(s, field.len() - 1);
    let w: Vec<u16> = clipped.encode_utf16().take(field.len() - 1).collect();
    field[..w.len()].copy_from_slice(&w);
    field[w.len()] = 0;
}

/// The small-icon size the notification area uses (16 at 100 %, 20, 24, 32 at 200 %).
pub fn small_icon_size() -> u32 {
    (unsafe { GetSystemMetrics(SM_CXSMICON) }).clamp(16, 256) as u32
}

/// An icon that owns its handle, from straight RGBA pixels (status_icons::draw).
pub struct Icon(pub HICON);

impl Icon {
    pub fn from_rgba(size: u32, rgba: &[u8]) -> Option<Icon> {
        unsafe {
            let mut bi: BITMAPINFO = std::mem::zeroed();
            bi.bmiHeader = BITMAPINFOHEADER {
                biSize: size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: size as i32,
                biHeight: -(size as i32), // top-down
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..std::mem::zeroed()
            };
            let mut bits = std::ptr::null_mut();
            let color = CreateDIBSection(std::ptr::null_mut(), &bi, DIB_RGB_COLORS, &mut bits, std::ptr::null_mut(), 0);
            if color.is_null() || bits.is_null() {
                return None;
            }
            let dst = std::slice::from_raw_parts_mut(bits as *mut u8, rgba.len());
            for (d, s) in dst.as_chunks_mut::<4>().0.iter_mut().zip(rgba.as_chunks::<4>().0) {
                d.copy_from_slice(&[s[2], s[1], s[0], s[3]]); // BGRA
            }
            let mask_bits = vec![0u8; (size.div_ceil(16) * 2 * size) as usize];
            let mask = CreateBitmap(size as i32, size as i32, 1, 1, mask_bits.as_ptr().cast());
            let info = ICONINFO { fIcon: 1, xHotspot: 0, yHotspot: 0, hbmMask: mask, hbmColor: color };
            let icon = CreateIconIndirect(&info);
            DeleteObject(color);
            DeleteObject(mask);
            (!icon.is_null()).then_some(Icon(icon))
        }
    }
}

impl Drop for Icon {
    fn drop(&mut self) {
        unsafe { DestroyIcon(self.0) };
    }
}

/// Runs the message loop until quit() (PostQuitMessage) on this thread.
pub fn run_loop() {
    unsafe {
        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

pub fn quit() {
    unsafe { PostQuitMessage(0) };
}
