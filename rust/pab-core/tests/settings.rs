//! Ported from tests/TrayTests/StateTests.cs §6.

use std::cell::RefCell;
use std::fs;

use pab_core::settings::*;

const CONTRACT: &str = r#"{
  "gameDir": null,
  "bepInExInstalledByUs": false,
  "autoUpdate": true,
  "notifyOnSave": true,
  "lastUpdateCheckUtc": null,
  "skippedVersion": null,
  "disclaimerAccepted": 0
}"#;

fn is_default(s: &Settings) -> bool {
    s.game_dir.is_none() && !s.bep_in_ex_installed_by_us && s.auto_update && s.notify_on_save && s.last_update_check_utc.is_none()
        && s.skipped_version.is_none() && s.disclaimer_accepted == 0 && !s.beta_updates()
}

#[test]
fn settings_like_the_csharp_tray() {
    let tmp = tempfile::tempdir().unwrap();
    let path = tmp.path().join("settings.json");
    let logged = RefCell::new(Vec::<String>::new());
    let log = |m: &str| logged.borrow_mut().push(m.to_string());

    assert!(is_default(&Settings::default()));
    assert!(is_default(&Settings::load(&tmp.path().join("no/such/dir.json"), &log)));
    for (text, what) in [(CONTRACT, "contract example"), ("{}", "{} keeps true fields true")] {
        fs::write(&path, text).unwrap();
        assert!(is_default(&Settings::load(&path, &log)), "{what}");
    }
    fs::write(&path, r#"{"gameDir":"G:\\PTCGL","autoUpdate":false,"someFutureField":[1,2,{"x":1}]}"#).unwrap();
    let partial = Settings::load(&path, &log);
    assert!(partial.game_dir.as_deref() == Some("G:\\PTCGL") && !partial.auto_update && partial.notify_on_save);
    assert!(logged.borrow().is_empty());

    for corrupt in ["{not json", "", "null", "[]", "\"text\"", r#"{"autoUpdate":"yes"}"#, "\0\0\0"] {
        logged.borrow_mut().clear();
        fs::write(&path, corrupt).unwrap();
        assert!(is_default(&Settings::load(&path, &log)) && logged.borrow().len() == 1, "corrupt {corrupt:?}");
    }
    assert!(tmp.path().join("settings.json.corrupt").exists() && is_default(&Settings::load(&path, &log)));
    assert!(is_default(&Settings::load(tmp.path(), &log)), "a folder instead of a file");

    let full = Settings {
        game_dir: Some("C:\\Game \"x\" é".into()), bep_in_ex_installed_by_us: true, auto_update: false, notify_on_save: false,
        last_update_check_utc: Some("2026-10-19T21:35:00Z".into()), skipped_version: Some("1.2.0".into()), disclaimer_accepted: 1,
        ..Settings::default()
    };
    full.save(&path).unwrap();
    assert_eq!(Settings::load(&path, &log), full);
    let raw = fs::read(&path).unwrap();
    let json = String::from_utf8(raw.clone()).unwrap();
    assert_eq!(raw[0], b'{', "UTF-8 without BOM");
    let at = |k: &str| json.find(k).unwrap_or_else(|| panic!("{k} in {json}"));
    assert!(at("\"gameDir\"") < at("\"bepInExInstalledByUs\"") && at("\"lastUpdateCheckUtc\"") < at("\"skippedVersion\"")
        && at("\"skippedVersion\"") < at("\"disclaimerAccepted\": 1"), "contract order, .NET layout");
    assert!(!json.contains("tcgkbAutoUpload") && !json.contains("tcgkbSignInOffered"), "omitted when unset, as .NET does");
    assert!(json.contains('\n') && !tmp.path().join("settings.json.tmp").exists());

    fs::write(&path, r#"{"disclaimerAccepted":7}"#).unwrap();
    assert_eq!(Settings::load(&path, &log).disclaimer_accepted, 7);
    logged.borrow_mut().clear();
    fs::write(&path, r#"{"disclaimerAccepted":"yes"}"#).unwrap();
    assert!(Settings::load(&path, &log).disclaimer_accepted == 0 && logged.borrow().len() == 1);
    for (text, beta) in [(r#"{"updateChannel":"beta"}"#, true), (r#"{"updateChannel":"BETA"}"#, true), (r#"{"updateChannel":"nightly"}"#, false),
                         (r#"{"updateChannel":null}"#, false), (r#"{"updateChannel":""}"#, false)] {
        fs::write(&path, text).unwrap();
        assert_eq!(Settings::load(&path, &log).beta_updates(), beta, "{text}");
    }
    Settings { update_channel: Some(BETA_CHANNEL.into()), tcgkb_auto_upload: Some(false), ..Settings::default() }.save(&path).unwrap();
    let back = Settings::load(&path, &log);
    assert!(back.beta_updates() && back.tcgkb_auto_upload == Some(false) && fs::read_to_string(&path).unwrap().contains("\"updateChannel\": \"beta\""));
}
