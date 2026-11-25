//! settings.json (Settings.cs). Loading never fails: a missing field takes its default and a
//! corrupt file is set aside (settings.json.corrupt) and replaced by defaults. Saved in the C#
//! tray's field order and layout, so 1.6.x and 2.x read each other's file.

use std::path::Path;

use serde::{Deserialize, Serialize};

use crate::file_ops;

pub const STABLE_CHANNEL: &str = "stable";
pub const BETA_CHANNEL: &str = "beta";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Settings {
    pub game_dir: Option<String>,
    pub bep_in_ex_installed_by_us: bool,
    pub auto_update: bool,
    pub notify_on_save: bool,
    pub last_update_check_utc: Option<String>,
    pub skipped_version: Option<String>,
    /// The Disclaimer.Version the user accepted; 0 = never (the only case that asks).
    pub disclaimer_accepted: i32,
    /// Upload each match to tcgkb once signed in: None = never chosen (on).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tcgkb_auto_upload: Option<bool>,
    pub pokematch_auto_upload: bool,
    pub pokematch_since_utc: Option<String>,
    /// "stable" (default) or "beta"; anything else reads as stable.
    pub update_channel: Option<String>,
    #[serde(skip_serializing_if = "std::ops::Not::not")]
    pub tcgkb_sign_in_offered: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Settings {
            game_dir: None,
            bep_in_ex_installed_by_us: false,
            auto_update: true,
            notify_on_save: true,
            last_update_check_utc: None,
            skipped_version: None,
            disclaimer_accepted: 0,
            tcgkb_auto_upload: None,
            pokematch_auto_upload: false,
            pokematch_since_utc: None,
            update_channel: Some(STABLE_CHANNEL.into()),
            tcgkb_sign_in_offered: false,
        }
    }
}

impl Settings {
    pub fn beta_updates(&self) -> bool {
        self.update_channel.as_deref().is_some_and(|c| c.eq_ignore_ascii_case(BETA_CHANNEL))
    }

    pub fn load(path: &Path, log: &dyn Fn(&str)) -> Settings {
        if !path.is_file() {
            return Settings::default();
        }
        let text = match read_with_retry(path) {
            Ok(bytes) => bytes,
            Err(e) => {
                log(&format!("Could not read settings ({e}); using defaults."));
                return Settings::default();
            }
        };
        let text = text.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&text);
        // Every file here is an object; .NET refused anything else too.
        if text.iter().find(|b| !b.is_ascii_whitespace()) == Some(&b'{') {
            if let Ok(s) = serde_json::from_slice::<Settings>(text) {
                return s;
            }
        }
        log("settings.json is corrupt; replaced by defaults (old copy kept as settings.json.corrupt).");
        let defaults = Settings::default();
        let mut corrupt = path.as_os_str().to_owned();
        corrupt.push(".corrupt");
        if let Err(e) = std::fs::copy(path, &corrupt).and_then(|_| defaults.save(path)) {
            log(&format!("Could not replace the corrupt settings file: {e}"));
        }
        defaults
    }

    /// Atomic: written beside, then swapped in. UTF-8 without BOM, indented.
    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        file_ops::write_atomic(path, serde_json::to_string_pretty(self).expect("settings serialize").as_bytes())
    }
}

fn read_with_retry(path: &Path) -> std::io::Result<Vec<u8>> {
    for _ in 0..2 {
        match std::fs::read(path) {
            Ok(b) => return Ok(b),
            // another process is swapping the file in; the next try sees it
            Err(_) => std::thread::sleep(std::time::Duration::from_millis(100)),
        }
    }
    std::fs::read(path)
}
