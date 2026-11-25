//! The tray's right-click menu: a plain Win32 popup menu, shown where the cursor is, returning the
//! chosen item's id. Built fresh each time, so it always shows the current state.
use super::wide;
use windows_sys::Win32::Foundation::{HWND, POINT};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, DestroyMenu, GetCursorPos, PostMessageW, SetForegroundWindow, SetMenuDefaultItem, TrackPopupMenu, HMENU,
    MF_CHECKED, MF_GRAYED, MF_POPUP, MF_SEPARATOR, MF_STRING, TPM_BOTTOMALIGN, TPM_RETURNCMD, TPM_RIGHTBUTTON, WM_NULL,
};

#[derive(Clone)]
pub enum Item {
    Action { id: u32, text: String, checked: bool, enabled: bool, default: bool },
    Sub { text: String, items: Vec<Item> },
    Separator,
}

impl Item {
    pub fn action(id: u32, text: impl Into<String>) -> Item {
        Item::Action { id, text: text.into(), checked: false, enabled: true, default: false }
    }
    pub fn check(id: u32, text: impl Into<String>, checked: bool) -> Item {
        Item::Action { id, text: text.into(), checked, enabled: true, default: false }
    }
    pub fn label(text: impl Into<String>) -> Item {
        Item::Action { id: 0, text: text.into(), checked: false, enabled: false, default: false }
    }
    pub fn bold(id: u32, text: impl Into<String>) -> Item {
        Item::Action { id, text: text.into(), checked: false, enabled: true, default: true }
    }
}

fn build(items: &[Item]) -> HMENU {
    unsafe {
        let menu = CreatePopupMenu();
        for item in items {
            match item {
                Item::Separator => {
                    AppendMenuW(menu, MF_SEPARATOR, 0, std::ptr::null());
                }
                Item::Sub { text, items } => {
                    AppendMenuW(menu, MF_POPUP, build(items) as usize, wide(text).as_ptr());
                }
                Item::Action { id, text, checked, enabled, default } => {
                    let flags = MF_STRING | if *checked { MF_CHECKED } else { 0 } | if *enabled { 0 } else { MF_GRAYED };
                    // & would underline the next letter.
                    AppendMenuW(menu, flags, *id as usize, wide(&text.replace('&', "&&")).as_ptr());
                    if *default {
                        SetMenuDefaultItem(menu, *id, 0);
                    }
                }
            }
        }
        menu
    }
}

/// Shows the menu at the cursor and waits; Some(id) of the chosen item, None when dismissed.
pub fn show(owner: HWND, items: &[Item]) -> Option<u32> {
    unsafe {
        let menu = build(items);
        let mut pt = POINT { x: 0, y: 0 };
        GetCursorPos(&mut pt);
        // Without the foreground, the menu would not close when the player clicks elsewhere.
        SetForegroundWindow(owner);
        let id = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.x, pt.y, 0, owner, std::ptr::null());
        PostMessageW(owner, WM_NULL, 0, 0);
        DestroyMenu(menu); // submenus go with it
        (id > 0).then_some(id as u32)
    }
}
