//! Ported from tests/TrayTests/TcgkbTests.cs §36 and PokematchTests.cs §38, over the logs the C#
//! plugin sealed in tests/golden/logs (Rust has no sealer, so the cases need no new logs).

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use base64::Engine;
use chrono::{DateTime, TimeDelta, Utc};
use pab_core::{pokematch, tcgkb};
use pab_core::tokens::{tcgkb_store, TcgkbTokens, Transform};
use pab_core::uploads::*;
use serde_json::Value;

const ASH: &str = "4380be85652989f14787282baa9cad8e3744d43c5835ee1ca46bc12e251d5447";

/// A copy of the golden logs (with decks/ and the .seal sidecar), oldest file names first.
fn logs() -> (tempfile::TempDir, PathBuf) {
    let tmp = tempfile::tempdir().unwrap();
    let to = tmp.path().join("PTCGL Battle Logs");
    copy_dir(&Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/golden/logs"), &to);
    (tmp, to)
}

/// A copy written now: on Windows fs::copy keeps the source's mtime (the checkout's), which would
/// make "written in the last 2 minutes" depend on how long the build took.
fn copy_fresh(from: &Path, to: &Path) {
    fs::copy(from, to).unwrap();
    fs::File::options().write(true).open(to).unwrap().set_modified(std::time::SystemTime::now()).unwrap();
}

fn copy_dir(from: &Path, to: &Path) {
    fs::create_dir_all(to).unwrap();
    for e in fs::read_dir(from).unwrap().flatten() {
        if e.path().is_dir() { copy_dir(&e.path(), &to.join(e.file_name())) } else { copy_fresh(&e.path(), &to.join(e.file_name())) }
    }
}

fn xor() -> Box<Transform> {
    Box::new(|b: &[u8]| Ok(b.iter().map(|x| x ^ 0x5a).collect()))
}

type Handler = Box<dyn Fn(&tcgkb::Request) -> Result<tcgkb::Response, String> + Send>;

fn tcgkb_reply(status: u16, body: &str) -> Result<tcgkb::Response, String> {
    Ok(tcgkb::Response { status, body: body.into() })
}

/// tcgkb took the log; it has the deck the seal names (the uploader ignores deckRef without one).
fn uploaded() -> Result<tcgkb::Response, String> {
    tcgkb_reply(200, &format!(r#"{{"added":true,"account":{{"accountHash":"{ASH}","name":"Ash","status":"claimed"}},"playerDeck":"Dragapult ex","opponentDeck":"Alakazam","deckRef":{{"deckId":"d1","version":1,"changed":false}}}}"#))
}

#[test]
fn tcgkb_each_match_once() {
    let (tmp, folder) = logs();
    let requests = Arc::new(Mutex::new(Vec::<tcgkb::Request>::new()));
    let handle: Arc<Mutex<Handler>> = Arc::new(Mutex::new(Box::new(|_| uploaded())));
    let set = |h: Handler| *handle.lock().unwrap() = h;
    let api = {
        let (requests, handle) = (requests.clone(), handle.clone());
        tcgkb::TcgkbApi::new(move |r: &tcgkb::Request| {
            requests.lock().unwrap().push(r.clone());
            (handle.lock().unwrap())(r)
        })
    };
    let store = tcgkb_store(tmp.path(), xor(), xor());
    let now = Utc::now();
    store.save(&TcgkbTokens { access_token: Some("a1".into()), access_expires_utc: now + TimeDelta::hours(1), refresh_token: Some("r1".into()), user_name: Some("Manu".into()), connected_utc: now }).unwrap();
    let conn = tcgkb::TcgkbConnection::new(api, store, move || now);
    let uploader = LogUploader::new(tmp.path());
    let sent = || requests.lock().unwrap().len();

    let run = uploader.sync(&conn, &folder).unwrap().unwrap();
    let bodies: Vec<Value> = requests.lock().unwrap().iter().map(|r| serde_json::from_str(r.body.as_deref().unwrap()).unwrap()).collect();
    let names: Vec<&str> = bodies.iter().map(|b| b["fileName"].as_str().unwrap()).collect();
    // m-1 and m-2 (Ash), m-6 (Misty, sidecar); not B&B, not the log without an account, not the Export, not the unsealed one.
    assert_eq!(names, ["2026-10-20 18.10.00 - VICTORY vs Gary.txt", "2026-10-20 14.10.00 - DEFEAT vs Gary _ Oak.txt", "2026-10-20 13.10.00 - VICTORY vs Gary.txt"], "newest first");
    assert_eq!((run.uploaded, run.left, run.hit_limit, run.stopped_because.as_deref()), (3, 0, false, None));
    assert_eq!(run.newest.as_ref().and_then(|r| r.player_deck.as_deref()), Some("Dragapult ex"));
    let req = requests.lock().unwrap()[0].clone();
    assert!(req.method == "POST" && req.path == "/api/ptcgl/logs" && req.bearer.as_deref() == Some("a1"));
    assert!(bodies[0]["seal"].is_string() && bodies[2]["seal"].is_null(), "a sidecar seal is sent, a footer stays in the .txt");
    let log = base64::engine::general_purpose::STANDARD.decode(bodies[2]["log"].as_str().unwrap()).unwrap();
    assert_eq!(log, fs::read(folder.join(names[2])).unwrap(), "the log exactly as on disk");
    let m1 = pab_core::seal_summary::SealSummary::try_read(&folder.join(names[2])).unwrap();
    // Misty's newer log and m-1 have the same deck: it goes once, with the newest.
    assert!(bodies[0]["deck"].is_string() && bodies[1].get("deck").is_none() && bodies[2].get("deck").is_none() && m1.deck_sha256.is_some(),
            "the deck file goes once, with the newest log that has it");
    assert_eq!(uploader.load_state().decks, [m1.deck_sha256.clone().unwrap()], "the deck tcgkb has is remembered");
    assert_eq!(uploader.load_state().uploaded.len(), 3);

    let calls = sent();
    assert_eq!(uploader.sync(&conn, &folder).unwrap().unwrap().uploaded, 0);
    assert_eq!(sent(), calls, "a second run sends nothing");

    // Forget m-1: it is pending again, now without its deck (tcgkb has it).
    let forget = |key: &str| {
        let mut s = uploader.load_state();
        s.uploaded.retain(|k| k != key);
        fs::write(uploader.state_path(), pab_core::net_json::to_string(&s)).unwrap();
    };
    let m1_key = tcgkb_key(&m1);
    forget(&m1_key);
    uploader.sync(&conn, &folder).unwrap().unwrap();
    let body: Value = serde_json::from_str(requests.lock().unwrap().last().unwrap().body.as_deref().unwrap()).unwrap();
    assert!(body.get("deck").is_none(), "a deck tcgkb has is not sent again (\"deck\" left out)");

    forget(&m1_key);
    set(Box::new(|_| tcgkb_reply(503, "")));
    let run = uploader.sync(&conn, &folder).unwrap().unwrap();
    assert!(run.uploaded == 0 && run.left == 1 && run.stopped_because.is_some(), "tcgkb down: stops, the log left for later");

    set(Box::new(|_| tcgkb_reply(403, r#"{"error":"not_claimed"}"#)));
    let run = uploader.sync(&conn, &folder).unwrap().unwrap();
    assert!(run.uploaded == 0 && run.stopped_because.is_none() && !uploader.load_state().uploaded.contains(&m1_key), "not_claimed: skipped, not remembered");

    set(Box::new(|_| tcgkb_reply(422, r#"{"error":"invalid_log","message":"bad signature"}"#)));
    let run = uploader.sync(&conn, &folder).unwrap().unwrap();
    assert!(run.rejected == 1 && uploader.load_state().rejected == [m1_key.clone()], "an invalid log: refused once");
    let calls = sent();
    uploader.sync(&conn, &folder).unwrap().unwrap();
    assert_eq!(sent(), calls, "never sent again");

    forget(uploader.load_state().uploaded[0].clone().as_str());
    set(Box::new(|_| tcgkb_reply(401, "")));
    assert!(uploader.sync(&conn, &folder).unwrap_err().is_auth(), "connection gone: an auth error");

    fs::write(uploader.state_path(), "not json").unwrap();
    assert!(uploader.load_state().uploaded.is_empty(), "an unreadable state file: empty, logs offered again");
    assert!(tcgkb_pending(&folder.join("nope"), &Default::default()).is_empty(), "no folder: nothing pending");
}

type PmHandler = Box<dyn Fn(&pokematch::Request) -> Result<pokematch::Response, String> + Send>;

fn pm_reply(status: u16, body: &str) -> Result<pokematch::Response, String> {
    Ok(pokematch::Response { status, body: body.into() })
}

#[test]
fn pokematch_each_finished_match_once() {
    let (tmp, folder) = logs();
    let requests = Arc::new(Mutex::new(Vec::<pokematch::Request>::new()));
    let next = Arc::new(Mutex::new(0));
    let ok: PmHandler = {
        let next = next.clone();
        Box::new(move |r| if r.method == "POST" { let mut n = next.lock().unwrap(); *n += 1; pm_reply(201, &format!(r#"{{"_id":"pm{n}"}}"#)) } else { pm_reply(200, "{}") })
    };
    let handle: Arc<Mutex<PmHandler>> = Arc::new(Mutex::new(ok));
    let set = |h: PmHandler| *handle.lock().unwrap() = h;
    let api = {
        let (requests, handle) = (requests.clone(), handle.clone());
        pokematch::PokematchApi::new(move |r: &pokematch::Request| {
            requests.lock().unwrap().push(r.clone());
            (handle.lock().unwrap())(r)
        })
    };
    let sent = || requests.lock().unwrap().len();
    let state_file = tmp.path().join(POKEMATCH_STATE_FILE_NAME);
    let settled: DateTime<Utc> = Utc::now() + TimeDelta::minutes(10);
    let up = PokematchUploader::new(state_file.clone(), move || settled);
    let never = || false;
    let name = |p: &PathBuf| p.file_name().unwrap().to_string_lossy().into_owned();

    let eligible: Vec<String> = up.eligible(&folder, DateTime::<Utc>::MIN_UTC).iter().map(name).collect();
    assert_eq!(eligible, ["2026-10-20 13.10.00 - VICTORY vs Gary.txt", "2026-10-20 14.10.00 - DEFEAT vs Gary _ Oak.txt", "2026-10-20 15.10.00 - VICTORY vs Gary.txt",
                          "2026-10-20 18.10.00 - VICTORY vs Gary.txt", "2026-10-20 19.10.00 - DEFEAT vs Gary.txt"], "VICTORY and DEFEAT, oldest first");
    assert!(PokematchUploader::new(state_file.clone(), Utc::now).eligible(&folder, DateTime::<Utc>::MIN_UTC).is_empty(), "a log written in the last 2 minutes waits");
    assert!(up.eligible(&folder.join("missing"), DateTime::<Utc>::MIN_UTC).is_empty());
    assert_eq!(up.count_new(&folder, DateTime::<Utc>::MIN_UTC), 5);

    let c = pokematch_read(&folder.join("2026-10-20 13.10.00 - VICTORY vs Gary.txt")).unwrap();
    assert_eq!(c.matched.tags.as_deref(), Some(&[pokematch::TAG.to_string(), "ranked".into(), "standard".into()][..]));
    let json = pab_core::net_json::to_string(&c.matched);
    assert!(!json.contains("Gary") && !json.contains("Ash"), "neither name is sent");
    let unsealed = pokematch_read(&folder.join("2026-10-20 19.10.00 - DEFEAT vs Gary.txt")).unwrap();
    assert_eq!((unsealed.key.as_str(), unsealed.matched.tags.as_deref()), ("file:2026-10-20 19.10.00 - DEFEAT vs Gary.txt", Some(&[pokematch::TAG.to_string()][..])));

    let r = up.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    assert!(r.uploaded == 5 && r.updated == 0 && r.error.is_none() && !r.more, "{r:?}");
    assert!(requests.lock().unwrap().iter().all(|x| x.token == "tok" && x.method == "POST"));
    let state = PokematchUploadState::load(&state_file);
    let m1 = &state.uploads[state.find("match:m-1").unwrap()];
    assert_eq!((m1.id.as_deref(), m1.status.as_deref()), (Some("pm1"), Some(UPLOADED)));
    assert_eq!(up.count_new(&folder, DateTime::<Utc>::MIN_UTC), 0);
    let calls = sent();
    up.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    assert_eq!(sent(), calls, "nothing sent twice");

    // What m-1 would send changed (a corrected result): PUT on the same id; deleted there: never again.
    let edit = |key: &str, f: &dyn Fn(&mut PokematchUpload)| {
        let mut s = PokematchUploadState::load(&state_file);
        let i = s.find(key).unwrap();
        f(&mut s.uploads[i]);
        s.save(&state_file).unwrap();
    };
    edit("match:m-1", &|u| u.sha256 = Some("0".repeat(64)));
    let r = up.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    let put = requests.lock().unwrap().last().unwrap().clone();
    assert!(r.updated == 1 && r.uploaded == 0 && put.method == "PUT" && put.path == "/api/v1/tcg-matches/pm1");
    edit("match:m-1", &|u| u.sha256 = Some("0".repeat(64)));
    set(Box::new(|r| if r.method == "PUT" { pm_reply(404, "{}") } else { pm_reply(201, r#"{"_id":"pmX"}"#) }));
    up.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    let s = PokematchUploadState::load(&state_file);
    assert_eq!(s.uploads[s.find("match:m-1").unwrap()].status.as_deref(), Some(DELETED), "PUT 404: marked deleted");
    edit("match:m-1", &|u| u.sha256 = Some("0".repeat(64)));
    let calls = sent();
    up.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    assert_eq!(sent(), calls, "deleted on Pokematch: not sent again");

    // A fresh state: 401 stops at once, 503 waits, 422 refuses only that log.
    let state2 = tmp.path().join("second.json");
    let up2 = PokematchUploader::new(state2.clone(), move || settled);
    set(Box::new(|_| pm_reply(401, "{}")));
    let calls = sent();
    let r = up2.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    assert!(r.token_rejected && sent() == calls + 1 && PokematchUploadState::load(&state2).uploads.is_empty(), "401: stops, nothing recorded");
    set(Box::new(|_| pm_reply(503, "")));
    let r = up2.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    assert!(r.waiting == 1 && r.more && PokematchUploadState::load(&state2).uploads.is_empty(), "503: tries again next run");
    set(Box::new(|r| if r.body.as_deref().unwrap().contains("Dragapult") { pm_reply(422, r#"{"message":"bad log"}"#) } else { pm_reply(201, r#"{"_id":"ok"}"#) }));
    let r = up2.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    assert!(r.rejected >= 1 && r.uploaded >= 1 && r.error.as_deref().unwrap_or("").contains("bad log"), "a refused log does not stop the others: {r:?}");
    let calls = sent();
    up2.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &never);
    assert_eq!(sent(), calls, "refused: not retried while unchanged");

    let up3 = PokematchUploader::new(tmp.path().join("third.json"), move || settled);
    assert_eq!(up3.sync(&api, "tok", &folder, DateTime::<Utc>::MIN_UTC, &|| true), PokematchSyncResult::default(), "cancelled: nothing sent");

    fs::write(&state_file, "{ not json").unwrap();
    assert!(PokematchUploadState::load(&state_file).uploads.is_empty(), "corrupt state: empty");
}
