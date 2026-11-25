//! Ported from tests/TrayTests/TcgkbTests.cs §33, with seal blocks written in the plugin's exact
//! layout (LogSeal.cs: log text, "\n\n", BEGIN, JSON line, signature line, END, "\n").

use std::fs;
use std::path::Path;

use pab_core::hashing::{sha256, to_hex};
use pab_core::seal_summary::*;

const KEY: &str = "E34E3c57abcdEFGH_-12";

fn hash(id: &str) -> String {
    to_hex(&sha256(format!("ptcgl-abl:v1:{id}").as_bytes()))
}

fn json_v2(player: &str, account: Option<&str>, result: &str, deck_sha: Option<&str>) -> String {
    let acc = account.map(|a| format!("\"{}\"", hash(a))).unwrap_or("null".into());
    let deck_sha = deck_sha.map(|d| format!("\"{d}\"")).unwrap_or("null".into());
    format!(
        concat!(r#"{{"v":2,"app":"PTCGL Auto Battle Log","pluginVersion":"1.6.3","gameVersion":"1.24.0","capture":"auto","#,
                r#""matchId":"m-1","startedUtc":"2026-10-19T20:00:00Z","endedUtc":"2026-10-19T20:11:00Z","result":"{result}","#,
                r#""winner":1,"mySeat":1,"player":{{"name":{player},"accountHash":{acc}}},"opponent":{{"name":"Gary","#,
                r#""accountHash":"{gary}"}},"deck":"Charizard ex","deckSha256":{deck_sha},"queue":"Ranked","format":"Standard","logSha256":"{z}","logChars":65,"#,
                r#""sealedUtc":"2026-10-19T20:11:09Z","seq":1,"prev":null,"keyId":"{key}","publicKey":{{"kty":"RSA","n":"28DR","e":"AQAB"}}}}"#),
        result = result, player = player, acc = acc, gary = hash("gary-id"), deck_sha = deck_sha, z = "0".repeat(64), key = KEY
    )
}

fn block(begin: &str, json: &str, end: &str) -> String {
    format!("{begin}\n{json}\nkq3Vx==\n{end}\n")
}

fn write(dir: &Path, name: &str, body: &str) -> std::path::PathBuf {
    let p = dir.join(name);
    fs::write(&p, body).unwrap();
    p
}

#[test]
fn reads_footer_sidecar_and_v1() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path();
    let log = "Setup\nTurn 1";

    let footer = write(dir, "2026-10-19 20.11.00 - VICTORY vs Gary.txt", &format!("{log}\n\n{}", block(BEGIN, &json_v2("\"Ash\"", Some("ash-id"), "VICTORY", None), END)));
    let s = SealSummary::try_read(&footer).unwrap();
    assert_eq!(s.player_name.as_deref(), Some("Ash"));
    assert_eq!(s.player_account_hash, Some(hash("ash-id")));
    assert_eq!((s.match_id.as_deref(), s.result.as_deref(), s.key_id.as_str()), (Some("m-1"), Some("VICTORY"), KEY));
    assert_eq!((s.opponent_name.as_deref(), s.deck.as_deref()), (Some("Gary"), Some("Charizard ex")));
    assert_eq!((s.queue.as_deref(), s.format.as_deref(), s.version), (Some("Ranked"), Some("Standard"), 2));
    assert_eq!(s.sealed_utc.to_rfc3339(), "2026-10-19T20:11:09+00:00");
    assert_eq!(s.ended_utc.unwrap().to_rfc3339(), "2026-10-19T20:11:00+00:00");
    assert!(s.seal_file.is_none() && s.log_file == footer && s.deck_sha256.is_none() && s.read_deck().is_none());

    // v1: same fields, no deck/queue/format.
    let v1_json = json_v2("\"Ash\"", Some("ash-id"), "VICTORY", None).replace("{\"v\":2,", "{\"v\":1,").replace(",\"deckSha256\":null,\"queue\":\"Ranked\",\"format\":\"Standard\"", "");
    let v1 = write(dir, "2026-10-01 10.00.00 - VICTORY vs Gary.txt", &format!("{log}\n\n{}", block(BEGIN_V1, &v1_json, END_V1)));
    let old = SealSummary::try_read(&v1).unwrap();
    assert!(old.version == 1 && old.match_id.as_deref() == Some("m-1") && old.queue.is_none());
    let mismatched = json_v2("\"Ash\"", Some("ash-id"), "VICTORY", None).replace("{\"v\":2,", "{\"v\":1,");
    write(dir, "2026-10-01 10.00.00 - VICTORY vs Gary.txt", &format!("{log}\n\n{}", block(BEGIN, &mismatched, END)));
    assert!(SealSummary::try_read(&v1).is_none(), "a v2 BEGIN line with v 1: not a seal");

    // Sidecar (with a BOM), escaped name.
    let side = write(dir, "2026-10-19 21.11.00 - DEFEAT vs Gary.txt", log);
    fs::write(side.with_extension("seal"), format!("\u{feff}{}", block(BEGIN, &json_v2(r#""Ash \"the\" K\u00e9tchum \u2605""#, Some("ash-id"), "DEFEAT", None), END))).unwrap();
    let s = SealSummary::try_read(&side).unwrap();
    assert_eq!(s.seal_file, Some(side.with_extension("seal")));
    assert_eq!(s.player_name.as_deref(), Some("Ash \"the\" Kétchum ★"));

    assert!(SealSummary::try_read(&write(dir, "2026-10-19 22.00.00 - VICTORY vs Gary.txt", log)).is_none(), "unsealed: None");
    assert!(SealSummary::try_read(&dir.join("missing.txt")).is_none());
    let no_id = write(dir, "n.txt", &format!("{log}\n\n{}", block(BEGIN, &json_v2("\"Ash\"", None, "VICTORY", None), END)));
    assert!(SealSummary::try_read(&no_id).unwrap().player_account_hash.is_none());

    // The last footer counts; a broken block (extra line) is not a seal.
    let two = write(dir, "two.txt", &format!("{log}\n\n{}\n\n{}", block(BEGIN, &json_v2("\"Old\"", None, "VICTORY", None), END), block(BEGIN, &json_v2("\"New\"", None, "VICTORY", None), END)));
    assert_eq!(SealSummary::try_read(&two).unwrap().player_name.as_deref(), Some("New"));
    let broken = write(dir, "broken.txt", &format!("{log}\n\n{}extra\n", block(BEGIN, &json_v2("\"Ash\"", None, "VICTORY", None), END)));
    assert!(SealSummary::try_read(&broken).is_none());
}

#[test]
fn reads_the_deck_only_when_its_hash_matches() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path();
    let deck = br#"{"v":1,"size":60,"cards":{"sv8_174":2}}"#;
    let sha = to_hex(&sha256(deck));
    fs::create_dir(dir.join(DECKS_FOLDER)).unwrap();
    let deck_path = dir.join(DECKS_FOLDER).join(format!("{sha}.deck"));
    fs::write(&deck_path, deck).unwrap();
    let log = write(dir, "d.txt", &format!("x\n\n{}", block(BEGIN, &json_v2("\"Ash\"", None, "VICTORY", Some(&sha)), END)));
    let s = SealSummary::try_read(&log).unwrap();
    assert_eq!(s.deck_sha256.as_deref(), Some(sha.as_str()));
    assert_eq!(s.read_deck().as_deref(), Some(&deck[..]));
    fs::write(&deck_path, b"changed").unwrap();
    assert!(s.read_deck().is_none(), "edited deck file: None");
}

#[test]
fn evidence_is_the_files_as_on_disk() {
    use base64::Engine;
    let tmp = tempfile::tempdir().unwrap();
    let body = format!("\u{feff}x\n\n{}", block(BEGIN, &json_v2("\"Ash\"", None, "VICTORY", None), END));
    let log = write(tmp.path(), "e.txt", &body);
    let e = pab_core::tcgkb::build_evidence(&SealSummary::try_read(&log).unwrap(), true).unwrap();
    assert_eq!(e.file_name, "e.txt");
    assert_eq!(base64::engine::general_purpose::STANDARD.decode(e.log).unwrap(), body.as_bytes(), "BOM and footer included");
    assert!(e.seal.is_none() && e.deck.is_none());
}
