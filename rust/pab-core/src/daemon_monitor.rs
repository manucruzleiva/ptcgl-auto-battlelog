//! The daemon's judgement, fed every 5 s (DaemonMonitor.cs): when to repair, what to say, which
//! icon. Repairs wait one more tick after pieces go missing (a game updater may still be writing)
//! and happen only while the game is closed and the player's consent is current; each notice is
//! shown once per episode, not every tick.

use chrono::{DateTime, TimeDelta, Utc};

use crate::game_install::InstallPiece;
use crate::plugin_status::{PluginStatus, ALIVE_WINDOW, STATE_ERROR, STATE_STARTING};

pub const REPAIR_DELAY: TimeDelta = TimeDelta::seconds(5);
pub const REPAIR_RETRY: TimeDelta = TimeDelta::seconds(30);
/// The status while the player has not accepted the current disclaimer and nothing worse is going on.
pub const CONSENT_PENDING_SUMMARY: &str = "Action needed: accept the disclaimer";
/// Added to the status while a newer plugin waits for the game to close.
pub const RESTART_TO_UPDATE_SUFFIX: &str = " · restart the game to finish the mod update";

/// Tray icon state (docs/ARCHITECTURE.md, "Daemon loop").
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TrayHealth {
    /// No dot: healthy, game closed.
    #[default]
    Idle,
    /// Green: the plugin's heartbeat is fresh.
    Active,
    /// Amber: repair pending, game running without the plugin, game not found.
    Attention,
    /// Red: the plugin reports an error, or a repair keeps failing.
    Error,
}

/// One-off notifications the daemon asks the UI to show.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DaemonNotice {
    #[default]
    None,
    /// Pieces are missing while the game runs: restart it once it has finished updating.
    GameNeedsRestart,
    /// The game has run for a while and the plugin never reported in.
    RunningWithoutMod,
    /// The plugin is loaded but says state "error".
    PluginError,
    /// A newer plugin waits for the game to close.
    ModUpdateReady,
}

/// What the daemon saw on one tick. Gathering it needs Windows; judging it does not.
#[derive(Debug, Clone)]
pub struct DaemonObservation {
    pub now: DateTime<Utc>,
    /// None when the game was not found.
    pub game_dir: Option<String>,
    pub game_running: bool,
    pub missing: Vec<InstallPiece>,
    /// The plugin DLL exists (so a reported Plugin piece only means "outdated").
    pub plugin_file_present: bool,
    pub status: Option<PluginStatus>,
    /// The player has not accepted the disclaimer yet: nothing is written into the game until they do.
    pub consent_pending: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DaemonDecision {
    pub health: TrayHealth,
    pub summary: String,
    /// Repair now (after checking once more that the game is closed).
    pub repair: bool,
    pub notice: DaemonNotice,
    pub plugin_alive: bool,
}

#[derive(Debug, Default)]
pub struct DaemonMonitor {
    missing_since: Option<DateTime<Utc>>,
    retry_after: Option<DateTime<Utc>>,
    running_since: Option<DateTime<Utc>>,
    restart_notice_shown: bool,
    no_mod_notice_shown: bool,
    update_notice_shown: bool,
    error_notice_pid: Option<i64>,
    setup_pending: bool,
    pub last_repair_error: Option<String>,
}

impl DaemonMonitor {
    pub fn evaluate(&mut self, o: &DaemonObservation) -> DaemonDecision {
        let now = o.now;
        let missing = o.game_dir.is_some() && !o.missing.is_empty();
        let only_outdated_plugin = missing && o.plugin_file_present && o.missing.iter().all(|p| *p == InstallPiece::Plugin);
        let alive = o.status.as_ref().is_some_and(|s| s.is_alive(now));
        let state_is = |want: &str| o.status.as_ref().and_then(|s| s.state.as_deref()).is_some_and(|s| s.eq_ignore_ascii_case(want));
        let plugin_error = alive && state_is(STATE_ERROR);
        let pid = o.status.as_ref().and_then(|s| s.process_id).unwrap_or(0);

        if o.game_running {
            self.running_since.get_or_insert(now);
        } else {
            self.running_since = None;
            self.no_mod_notice_shown = false;
        }
        let running_for = self.running_since.map_or(TimeDelta::zero(), |s| now - s);
        let alive_window = TimeDelta::from_std(ALIVE_WINDOW).unwrap_or_default();
        let mut d = DaemonDecision { plugin_alive: alive, ..Default::default() };

        // Repair: missing, game closed, still so one delay later, not right after a failure, and
        // never before the player has accepted the disclaimer.
        if missing && !o.game_running {
            let since = *self.missing_since.get_or_insert(now);
            d.repair = !o.consent_pending && now - since >= REPAIR_DELAY && self.retry_after.is_none_or(|r| now >= r);
        } else {
            self.missing_since = None;
        }
        if !missing {
            self.last_repair_error = None;
            self.retry_after = None;
            self.restart_notice_shown = false;
            self.setup_pending = false;
            self.update_notice_shown = false;
        }

        // Notices, most important first; one per tick.
        if plugin_error && self.error_notice_pid != Some(pid) {
            d.notice = DaemonNotice::PluginError;
            self.error_notice_pid = Some(pid);
        } else if missing && o.game_running && !only_outdated_plugin && !self.restart_notice_shown && !self.setup_pending && !o.consent_pending {
            d.notice = DaemonNotice::GameNeedsRestart;
            self.restart_notice_shown = true;
        } else if o.game_running && !alive && !missing && o.game_dir.is_some() && running_for >= alive_window && !self.no_mod_notice_shown {
            d.notice = DaemonNotice::RunningWithoutMod;
            self.no_mod_notice_shown = true;
        } else if only_outdated_plugin && o.game_running && alive && !self.update_notice_shown && !self.setup_pending && !o.consent_pending {
            d.notice = DaemonNotice::ModUpdateReady;
            self.update_notice_shown = true;
        }

        // Icon and status line.
        let (health, summary) = if o.game_dir.is_none() {
            (TrayHealth::Attention, "Pokémon TCG Live not found".to_string())
        } else if missing && o.consent_pending {
            (TrayHealth::Attention, "Accept the disclaimer to keep the mod updated".into())
        } else if let Some(err) = self.last_repair_error.as_ref().filter(|_| missing) {
            (TrayHealth::Error, format!("Could not repair the mod: {err}"))
        } else if missing && !o.game_running {
            let s = if only_outdated_plugin { "Updating the mod..." } else if self.setup_pending { "Installing the mod..." } else { "Repairing the mod..." };
            (TrayHealth::Attention, s.into())
        } else if missing && !only_outdated_plugin && self.setup_pending {
            (TrayHealth::Attention, "Close Pokémon TCG Live once to finish installing the mod".into())
        } else if missing && !only_outdated_plugin {
            (TrayHealth::Attention, "Mod missing: restart the game when it finishes updating".into())
        } else if plugin_error {
            let message = o.status.as_ref().and_then(|s| s.message.as_deref()).map(str::trim).filter(|m| !m.is_empty()).unwrap_or("see the BepInEx log");
            (TrayHealth::Error, format!("Mod error: {message}"))
        } else if o.consent_pending {
            (TrayHealth::Attention, CONSENT_PENDING_SUMMARY.into())
        } else if alive {
            let saved = o.status.as_ref().and_then(|s| s.saved_this_session).unwrap_or(0);
            let mut s = String::from(if state_is(STATE_STARTING) { "Mod starting..." } else { "Saving your matches" });
            if saved > 0 {
                s += &format!(" · {saved} {} this session", if saved == 1 { "log" } else { "logs" });
            }
            if only_outdated_plugin {
                s += RESTART_TO_UPDATE_SUFFIX;
            }
            (TrayHealth::Active, s)
        } else if o.game_running && running_for >= alive_window {
            (TrayHealth::Attention, "Game running without the mod: restart it".into())
        } else if o.game_running {
            (TrayHealth::Idle, "Game starting...".into())
        } else {
            (TrayHealth::Idle, "Ready · waiting for the game".into())
        };
        d.health = health;
        d.summary = summary;
        d
    }

    /// A first install (or an upgrade) that could not be finished because the game was running:
    /// until the mod is complete, the status says to close the game once, and the "a game update
    /// removed it" notice is not shown.
    pub fn setup_pending(&mut self) {
        self.setup_pending = true;
    }

    /// The repair the last decision asked for failed. Retried after REPAIR_RETRY.
    pub fn repair_failed(&mut self, message: &str, now: DateTime<Utc>) {
        let m = message.trim();
        self.last_repair_error = Some(if m.is_empty() { "unknown error".into() } else { m.into() });
        self.retry_after = Some(now + REPAIR_RETRY);
    }

    pub fn repair_succeeded(&mut self) {
        self.last_repair_error = None;
        self.retry_after = None;
        self.missing_since = None;
    }
}
