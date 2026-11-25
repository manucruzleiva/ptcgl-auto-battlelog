//! The Rust tray must read and write what the C# tray does, byte for byte. tests/golden was made by
//! the C# code itself (tests/RustGoldens, in a UTC time zone): regenerate it there, never by hand.

use std::fs;
use std::path::{Path, PathBuf};

use pab_core::hashing::{sha256, to_hex};
use pab_core::pokematch;
use pab_core::seal_summary::SealSummary;
use pab_core::settings::Settings;
use pab_core::uploads::*;

fn golden(name: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/golden").join(name)
}

fn text(name: &str) -> String {
    fs::read_to_string(golden(name)).unwrap()
}

#[test]
fn derived_fields_match_the_csharp_tray() {
    let tsv = text("derived.tsv");
    let rows: Vec<&str> = tsv.lines().collect();
    assert_eq!(rows.len(), 7);
    for row in rows {
        let c: Vec<&str> = row.split('\t').collect();
        let log = golden("logs").join(c[0]);
        let seal = SealSummary::try_read(&log);
        match &seal {
            None => assert_eq!(&c[1..4], ["-", "-", "-"], "{row}"),
            Some(s) => {
                assert_eq!(tcgkb_key(s), c[1], "{row}");
                assert_eq!(if is_evidence(s) { "evidence" } else { "not-evidence" }, c[2], "{row}");
                assert_eq!(if is_build_and_battle(s) { "bnb" } else { "-" }, c[3], "{row}");
            }
        }
        let p = pokematch_read(&log).expect(row);
        assert_eq!(p.key, c[4], "{row}");
        assert_eq!(p.sha256, c[5], "{row}");
        let stem = c[0].strip_suffix(".txt").unwrap();
        let json = pab_core::net_json::to_string(&p.matched);
        assert_eq!(json, text(&format!("{stem}.pokematch.json")), "{row}");
        assert_eq!(to_hex(&sha256(json.as_bytes())), c[5]);
    }
}

#[test]
fn string_escapes_match_the_csharp_writer() {
    let tricky = pokematch::Match {
        active_deck: Some(pokematch::Deck { name: Some("Kétchum ★ \"quoted\" back\\slash / slash <tag> & 😀".into()) }),
        opponent_deck: Some("ctl\u{1}\u{1f}\t\n\r\u{8}\u{c}\u{7f}\u{85}\u{2028}\u{2029}end".into()),
        format: Some("Bo1".into()),
        games: Some(vec![]),
        date: Some("2026-10-20".into()),
        notes: None,
        tags: Some(vec![]),
    };
    assert_eq!(pab_core::net_json::to_string(&tricky), text("escape.json"));
}

#[test]
fn state_files_round_trip_byte_for_byte() {
    let tmp = tempfile::tempdir().unwrap();

    let uploader = LogUploader::new(&golden(""));
    let state = uploader.load_state();
    assert_eq!(state.uploaded.len(), 1);
    assert_eq!(state.decks, ["a".repeat(64)]);
    assert_eq!(pab_core::net_json::to_string(&state), text(TCGKB_STATE_FILE_NAME));

    let pm = PokematchUploadState::load(&golden(POKEMATCH_STATE_FILE_NAME));
    assert_eq!(pm.uploads.len(), 2);
    assert_eq!(pm.uploads[1].error.as_deref(), Some("Pokematch refused"));
    let out = tmp.path().join("pm.json");
    pm.save(&out).unwrap();
    assert_eq!(fs::read_to_string(&out).unwrap(), text(POKEMATCH_STATE_FILE_NAME));

    let settings = Settings::load(&golden("settings.json"), &|m| panic!("{m}"));
    assert_eq!(settings.game_dir.as_deref(), Some("C:\\Games\\PTCGL"));
    let out = tmp.path().join("settings.json");
    settings.save(&out).unwrap();
    assert_eq!(fs::read_to_string(&out).unwrap(), text("settings.json"));
}
