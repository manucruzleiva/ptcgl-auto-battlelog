//! plugin-status.json, written by the plugin inside the game (PluginStatus.cs): the only channel
//! from the plugin to the tray. A fresh heartbeat means the game is running with the plugin loaded.

use std::path::Path;
use std::time::Duration;

use chrono::{DateTime, NaiveDateTime, Utc};
use serde::Deserialize;

use crate::file_ops;

pub const STATE_STARTING: &str = "starting";
pub const STATE_OK: &str = "ok";
pub const STATE_ERROR: &str = "error";
/// The plugin writes at least every 30 s; three missed beats means it is gone.
pub const ALIVE_WINDOW: Duration = Duration::from_secs(90);

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct PluginStatus {
    pub plugin_version: Option<String>,
    pub state: Option<String>,
    pub message: Option<String>,
    pub logs_folder: Option<String>,
    /// Missing or null reads as 0.
    pub saved_this_session: Option<i64>,
    pub last_file: Option<String>,
    pub last_saved_utc: Option<String>,
    pub heartbeat_utc: Option<String>,
    pub process_id: Option<i64>,
}

impl PluginStatus {
    /// None when the file is absent, being replaced, or not valid JSON.
    pub fn try_read(path: &Path) -> Option<PluginStatus> {
        let text = file_ops::try_read_small_text(path, 256 * 1024)?;
        if text.trim().is_empty() {
            return None;
        }
        serde_json::from_str(&replace_lone_surrogates(&text)).ok()
    }

    /// try_read, but keeps `previous` while the file exists and cannot be read: on Windows a read
    /// that lands on the plugin's replace fails for an instant, which must not look like "no plugin".
    /// A kept value still ages out through is_alive.
    pub fn try_read_keeping(path: &Path, previous: Option<PluginStatus>) -> Option<PluginStatus> {
        Self::try_read(path).or_else(|| if path.exists() { previous } else { None })
    }

    pub fn heartbeat(&self) -> Option<DateTime<Utc>> {
        parse_utc(self.heartbeat_utc.as_deref()?)
    }

    pub fn last_saved(&self) -> Option<DateTime<Utc>> {
        parse_utc(self.last_saved_utc.as_deref()?)
    }

    /// Heartbeat within 90 s of `now`, either way (to survive small clock changes).
    pub fn is_alive(&self, now: DateTime<Utc>) -> bool {
        self.heartbeat().is_some_and(|hb| (now - hb).abs() <= chrono::TimeDelta::from_std(ALIVE_WINDOW).unwrap_or_default())
    }

    /// The status window's mod line: which plugin version the game has loaded (or last loaded) and
    /// whether it is the app's. The DLL is only replaced while the game is closed.
    pub fn mod_version_line(status: Option<&PluginStatus>, app_version: &str, now: DateTime<Utc>) -> String {
        let version = status.and_then(|s| s.plugin_version.as_deref()).map(|v| v.trim().trim_start_matches(['v', 'V'])).unwrap_or("");
        let Some(status) = status.filter(|_| !version.is_empty()) else { return "Mod in the game: not loaded yet".into() };
        let same = version.eq_ignore_ascii_case(app_version);
        if status.is_alive(now) {
            let tail = if same { " (same as the app)".to_string() } else { format!(" · not the app's v{app_version}: restart the game to update it") };
            format!("Mod in the game: v{version}{tail}")
        } else {
            let tail = if same { String::new() } else { format!(" · the game loads v{app_version} the next time it starts") };
            format!("Mod last loaded by the game: v{version}{tail}")
        }
    }
}

/// "yyyy-MM-ddTHH:mm:ssZ", with up to 7 fraction digits.
pub fn parse_utc(s: &str) -> Option<DateTime<Utc>> {
    let s = s.trim();
    let (main, frac) = match s.split_once('.') {
        Some((m, f)) => (m, Some(f.strip_suffix('Z')?)),
        None => (s.strip_suffix('Z')?, None),
    };
    if frac.is_some_and(|f| f.is_empty() || f.len() > 7 || !f.bytes().all(|b| b.is_ascii_digit())) {
        return None;
    }
    let t = NaiveDateTime::parse_from_str(main, "%Y-%m-%dT%H:%M:%S").ok()?;
    let nanos = frac.map_or(0, |f| format!("{f:0<9}").parse::<i64>().unwrap_or(0));
    Some((t + chrono::TimeDelta::nanoseconds(nanos)).and_utc())
}

/// The plugin writes .NET strings, which may hold lone UTF-16 surrogates ("\\ud800"); serde_json
/// refuses those, which would make the whole file unreadable. Each becomes U+FFFD; pairs are kept.
fn replace_lone_surrogates(json: &str) -> std::borrow::Cow<'_, str> {
    if !json.contains("\\u") {
        return json.into();
    }
    let unit = |s: &str| s.get(..6).filter(|e| e.starts_with("\\u")).and_then(|e| u16::from_str_radix(&e[2..], 16).ok());
    let mut out = String::with_capacity(json.len());
    let mut rest = json;
    while let Some(at) = rest.find('\\') {
        out.push_str(&rest[..at]);
        rest = &rest[at..];
        match unit(rest) {
            Some(0xD800..=0xDBFF) if unit(&rest[6..]).is_some_and(|lo| (0xDC00..=0xDFFF).contains(&lo)) => {
                out.push_str(&rest[..12]);
                rest = &rest[12..];
            }
            Some(0xD800..=0xDFFF) => {
                out.push_str("\\ufffd");
                rest = &rest[6..];
            }
            _ => {
                // any other escape, copied with the character it escapes (so "\\\\" never starts one)
                let len = rest[1..].chars().next().map_or(1, |c| 1 + c.len_utf8());
                out.push_str(&rest[..len]);
                rest = &rest[len..];
            }
        }
    }
    out.push_str(rest);
    out.into()
}
