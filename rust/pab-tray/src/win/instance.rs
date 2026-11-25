//! One tray per Windows session, installed or portable (a named mutex), plus the two signals other
//! launches send it: "show yourself" and "exit" (named auto-reset events). SingleInstance.cs.
use super::wide;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, ERROR_ACCESS_DENIED, HANDLE, WAIT_ABANDONED, WAIT_OBJECT_0};
use windows_sys::Win32::System::Threading::{CreateEventW, CreateMutexW, OpenEventW, ReleaseMutex, SetEvent, WaitForSingleObject, EVENT_MODIFY_STATE};

pub fn mutex_name() -> String {
    format!(r"Local\{}-tray", pab_core::paths::APP_ID)
}
pub fn show_event_name() -> String {
    format!(r"Local\{}-show", pab_core::paths::APP_ID)
}
pub fn exit_event_name() -> String {
    format!(r"Local\{}-exit", pab_core::paths::APP_ID)
}

const SIGNAL_EVERY: Duration = Duration::from_millis(500);

/// The tray mutex. The owning thread must be the one that drops it (Windows frees it at exit anyway).
pub struct SingleInstance {
    mutex: HANDLE,
    pub owned: bool,
    /// The mutex exists but this process may not open it: a copy started with "Run as administrator"
    /// made it with an administrators-only ACL. That copy can only be stopped from its own icon.
    pub access_denied: bool,
}

impl SingleInstance {
    /// Takes the tray mutex, waiting up to `wait` for a previous instance to let go.
    pub fn acquire(wait: Duration) -> SingleInstance {
        let mutex = unsafe { CreateMutexW(std::ptr::null(), 0, wide(&mutex_name()).as_ptr()) };
        let access_denied = mutex.is_null() && unsafe { GetLastError() } == ERROR_ACCESS_DENIED;
        let mut me = SingleInstance { mutex, owned: false, access_denied };
        me.try_take(wait);
        me
    }

    fn try_take(&mut self, wait: Duration) -> bool {
        if !self.owned && !self.mutex.is_null() {
            let r = unsafe { WaitForSingleObject(self.mutex, wait.as_millis().min(u32::MAX as u128 - 1) as u32) };
            // WAIT_ABANDONED: the previous owner crashed, the mutex is ours now.
            self.owned = r == WAIT_OBJECT_0 || r == WAIT_ABANDONED;
        }
        self.owned
    }

    /// Asks a running tray to exit and takes the mutex once it has (up to `wait`), so no other tray
    /// starts while an install or uninstall works. The request repeats every half second: a tray
    /// that is still starting may not be listening yet.
    pub fn stop_running_and_acquire(wait: Duration) -> SingleInstance {
        let mut me = Self::acquire(Duration::ZERO);
        if me.owned || me.access_denied {
            return me;
        }
        let deadline = Instant::now() + wait;
        loop {
            signal(&exit_event_name());
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() || me.try_take(left.min(SIGNAL_EVERY)) {
                return me;
            }
        }
    }
}

impl Drop for SingleInstance {
    fn drop(&mut self) {
        unsafe {
            if self.owned {
                ReleaseMutex(self.mutex);
            }
            if !self.mutex.is_null() {
                CloseHandle(self.mutex);
            }
        }
    }
}

/// Sets a named event if some process created it. False when nobody listens.
pub fn signal(name: &str) -> bool {
    unsafe {
        let h = OpenEventW(EVENT_MODIFY_STATE, 0, wide(name).as_ptr());
        if h.is_null() {
            return false;
        }
        let ok = SetEvent(h) != 0;
        CloseHandle(h);
        ok
    }
}

/// A named auto-reset event the tray listens on. Created as soon as the tray owns the mutex, before
/// its first dialog: a request sent while it is still starting stays set instead of being lost.
pub struct Event(HANDLE);

// SAFETY: an event handle may be waited on and closed from any thread.
unsafe impl Send for Event {}
unsafe impl Sync for Event {}

impl Event {
    pub fn create(name: &str) -> Option<Event> {
        let h = unsafe { CreateEventW(std::ptr::null(), 0, 0, wide(name).as_ptr()) };
        (!h.is_null()).then_some(Event(h))
    }

    /// Waits up to `timeout` and takes the signal (auto-reset).
    pub fn wait(&self, timeout: Duration) -> bool {
        unsafe { WaitForSingleObject(self.0, timeout.as_millis().min(u32::MAX as u128 - 1) as u32) == WAIT_OBJECT_0 }
    }
}

impl Drop for Event {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

/// The exit and show events (TraySignals).
pub struct TraySignals {
    pub exit: Option<std::sync::Arc<Event>>,
    pub show: Option<std::sync::Arc<Event>>,
}

impl TraySignals {
    pub fn create() -> TraySignals {
        TraySignals { exit: Event::create(&exit_event_name()).map(Into::into), show: Event::create(&show_event_name()).map(Into::into) }
    }

    /// Takes an exit request that arrived meanwhile: startup stops there.
    pub fn exit_requested(&self) -> bool {
        self.exit.as_ref().is_some_and(|e| e.wait(Duration::ZERO))
    }
}

/// Runs `on_signal` on a background thread for every signal of `event` (EventListener). The callback
/// must hand work to the UI thread itself. The thread lives as long as the process.
pub fn listen(event: std::sync::Arc<Event>, on_signal: impl Fn() + Send + 'static) {
    std::thread::spawn(move || loop {
        if event.wait(Duration::from_secs(3600)) {
            on_signal();
        }
    });
}
