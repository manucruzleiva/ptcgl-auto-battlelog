//! Ported from tests/TrayTests/TcgkbTests.cs §34–35, against a fake tcgkb.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use pab_core::tcgkb::{ClaimEvidence, Ladder, Request, Response, TcgkbApi, TcgkbConnection, TcgkbError, CLIENT_ID, DEVICE_CODE_GRANT};
use pab_core::tokens::{tcgkb_store, TcgkbTokens, Transform};

const HASH_ASH: &str = "f824fc06aa0f63d3a8b9ba2f7a8a5e3b3e2c6b9f2c8a3f1e6d0b7a9c4e5f6a7b";
const CODE_JSON: &str = r#"{"device_code":"dev-1","user_code":"WDJB-MJHT","verification_uri":"https://tcgkb.app/link","verification_uri_complete":"https://tcgkb.app/link?code=WDJB-MJHT","expires_in":600,"interval":5}"#;

fn token_json(access: &str, refresh: &str) -> String {
    format!(r#"{{"access_token":"{access}","token_type":"Bearer","expires_in":3600,"refresh_token":"{refresh}","scope":"claims:write logs:upload"}}"#)
}

fn me_json() -> String {
    format!(r#"{{"user":{{"name":"Manu"}},"accounts":[{{"accountHash":"{HASH_ASH}","name":"Ash","status":"verified"}},{{"accountHash":"bad","name":"x","status":"claimed"}}]}}"#)
}

fn reply(status: u16, body: &str) -> Result<Response, String> {
    Ok(Response { status, body: body.into() })
}

fn field(r: &Request, name: &str) -> Option<String> {
    r.body.as_deref()?.split('&').find_map(|kv| kv.strip_prefix(&format!("{name}=")).map(|v| v.replace("%3A", ":").replace("%20", " ")))
}

type Handler = Box<dyn Fn(&Request) -> Result<Response, String> + Send>;

#[derive(Clone)]
struct Fake {
    requests: Arc<Mutex<Vec<Request>>>,
    handle: Arc<Mutex<Handler>>,
}

impl Fake {
    fn new() -> Fake {
        Fake { requests: Arc::default(), handle: Arc::new(Mutex::new(Box::new(|_| reply(404, r#"{"error":"not_found"}"#)))) }
    }
    fn set(&self, h: impl Fn(&Request) -> Result<Response, String> + Send + 'static) {
        *self.handle.lock().unwrap() = Box::new(h);
    }
    fn api(&self) -> TcgkbApi<'static> {
        let me = self.clone();
        TcgkbApi::new(move |r: &Request| {
            me.requests.lock().unwrap().push(r.clone());
            (me.handle.lock().unwrap())(r)
        })
    }
    fn last(&self) -> Request {
        self.requests.lock().unwrap().last().unwrap().clone()
    }
    fn count(&self, path: &str) -> usize {
        self.requests.lock().unwrap().iter().filter(|r| r.path == path).count()
    }
}

fn t0() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 10, 21, 12, 0, 0).unwrap()
}

#[test]
fn device_flow_and_accounts() {
    let server = Fake::new();
    let api = server.api();
    server.set(|_| reply(200, CODE_JSON));
    let code = api.start_device_authorization("ASH-PC").unwrap();
    let req = server.last();
    assert!(code.user_code == "WDJB-MJHT" && code.verification_uri_complete.as_deref() == Some("https://tcgkb.app/link?code=WDJB-MJHT"));
    assert!(req.method == "POST" && req.path == "/api/device/code" && req.bearer.is_none() && req.content_type == Some("application/x-www-form-urlencoded"));
    assert_eq!((field(&req, "client_id").as_deref(), field(&req, "scope").as_deref(), field(&req, "device_name").as_deref()),
               (Some(CLIENT_ID), Some("claims:write logs:upload"), Some("ASH-PC")));

    server.set(|_| reply(200, &CODE_JSON.replace("https://tcgkb.app/link\"", "https://evil.example/link\"")));
    assert_eq!(api.start_device_authorization("pc").unwrap_err().code, "bad_response", "a sign-in page elsewhere is refused");
    server.set(|_| reply(200, &CODE_JSON.replace("https://tcgkb.app/link?code", "https://evil.example/?code")));
    assert!(api.start_device_authorization("pc").unwrap().verification_uri_complete.is_none(), "foreign complete URI dropped");
    server.set(|_| reply(200, &CODE_JSON.replace("WDJB-MJHT", "<script>")));
    assert!(api.start_device_authorization("pc").is_err());
    server.set(|_| reply(200, "<html>"));
    assert_eq!(api.start_device_authorization("pc").unwrap_err().code, "bad_response");
    server.set(|_| Err("offline".into()));
    assert_eq!(api.start_device_authorization("pc").unwrap_err().code, "network");

    // Polling: pending, slow_down (interval + 5 s), then the tokens.
    let answers = Mutex::new(VecDeque::from([
        reply(400, r#"{"error":"authorization_pending"}"#),
        reply(400, r#"{"error":"slow_down"}"#),
        reply(200, &token_json("a1", "r1")),
    ]));
    server.set(move |_| answers.lock().unwrap().pop_front().unwrap());
    let now = Arc::new(Mutex::new(t0()));
    let delays = Arc::new(Mutex::new(Vec::new()));
    let clock = { let now = now.clone(); move || *now.lock().unwrap() };
    let mut delay = { let (now, delays) = (now.clone(), delays.clone()); move |d: Duration| { delays.lock().unwrap().push(d.as_secs()); *now.lock().unwrap() += TimeDelta::from_std(d).unwrap(); Ok(()) } };
    let tokens = api.wait_for_approval(&code, &mut delay, &clock).unwrap();
    let req = server.last();
    assert!(tokens.access_token == "a1" && tokens.refresh_token == "r1");
    assert_eq!(*delays.lock().unwrap(), [5, 5, 10]);
    assert!(req.path == "/api/device/token" && field(&req, "grant_type").as_deref() == Some(DEVICE_CODE_GRANT) && field(&req, "device_code").as_deref() == Some("dev-1"));

    server.set(|_| reply(400, r#"{"error":"access_denied"}"#));
    assert_eq!(api.wait_for_approval(&code, &mut delay, &clock).unwrap_err().code, "access_denied");
    server.set(|_| reply(400, r#"{"error":"authorization_pending"}"#));
    let polls = server.count("/api/device/token");
    assert_eq!(api.wait_for_approval(&code, &mut delay, &clock).unwrap_err().code, "expired_token");
    assert_eq!(server.count("/api/device/token") - polls, 119, "gives up when the code expires");
    server.set(|_| reply(200, &token_json("a1", "r1").replace("Bearer", "MAC")));
    assert_eq!(api.wait_for_approval(&code, &mut delay, &clock).unwrap_err().code, "bad_response");
    assert_eq!(api.wait_for_approval(&code, &mut |_| Err(TcgkbError::cancelled()), &clock).unwrap_err().code, "cancelled");

    server.set(|_| reply(400, r#"{"error":"invalid_grant"}"#));
    assert!(api.refresh("r1").unwrap_err().is_auth());
    server.set(|_| reply(503, ""));
    let down = api.refresh("r1").unwrap_err();
    assert!(!down.is_auth() && down.status == 503, "tcgkb down: not a disconnect");

    server.set(|_| reply(200, &me_json()));
    let me = api.get_accounts("a1").unwrap();
    let req = server.last();
    assert!(req.method == "GET" && req.path == "/api/ptcgl/accounts" && req.bearer.as_deref() == Some("a1"));
    assert!(me.user.unwrap().name.as_deref() == Some("Manu") && me.accounts.len() == 1 && me.accounts[0].status.as_deref() == Some("verified"));
    assert!(me.ladder.is_none());
    server.set(|_| reply(200, &(me_json().trim_end_matches('}').to_string() + r#","ladder":{"games":3,"minGames":5,"rank":null,"players":12}}"#)));
    let ladder = api.get_accounts("a1").unwrap().ladder.unwrap();
    assert_eq!(ladder, Ladder { games: 3, min_games: 5, rank: None, players: 12 });
    server.set(|_| reply(401, ""));
    assert!(api.get_accounts("a1").unwrap_err().is_auth());

    server.set(|_| reply(409, r#"{"error":"claimed_by_other"}"#));
    let taken = api.upload_log("a1", &ClaimEvidence { file_name: "a.txt".into(), log: "QQ==".into(), ..Default::default() }).unwrap_err();
    assert!(taken.code == "claimed_by_other" && taken.message.contains("another tcgkb user"));
    let body = server.last().body.unwrap();
    assert!(body.contains("\"fileName\": \"a.txt\"") && body.contains("\"seal\": null") && !body.contains("deck"), "{body}");
}

fn xor() -> Box<Transform> {
    Box::new(|b: &[u8]| Ok(b.iter().map(|x| x ^ 0x5A).collect()))
}

#[test]
fn connect_refresh_disconnect() {
    let dir = tempfile::tempdir().unwrap();
    let server = Fake::new();
    let state = Arc::new(Mutex::new((0u32, "a1".to_string()))); // (refreshes, valid access token)
    let st = state.clone();
    server.set(move |r| {
        let mut s = st.lock().unwrap();
        match r.path {
            "/api/device/code" => reply(200, CODE_JSON),
            "/api/device/token" if field(r, "grant_type").as_deref() == Some("refresh_token") => {
                if field(r, "refresh_token") != Some(format!("r{}", s.0 + 1)) {
                    return reply(400, r#"{"error":"invalid_grant"}"#);
                }
                s.0 += 1;
                s.1 = format!("a{}", s.0 + 1);
                reply(200, &token_json(&s.1, &format!("r{}", s.0 + 1)))
            }
            "/api/device/token" => {
                *s = (0, "a1".into());
                reply(200, &token_json("a1", "r1"))
            }
            "/api/device/revoke" => reply(200, "{}"),
            _ if r.bearer.as_deref() == Some(s.1.as_str()) => reply(200, &me_json()),
            _ => reply(401, ""),
        }
    });
    let now = Arc::new(Mutex::new(t0()));
    let clock = { let now = now.clone(); move || *now.lock().unwrap() };
    let conn = TcgkbConnection::new(server.api(), tcgkb_store(dir.path(), xor(), xor()), clock.clone());
    let mut no_delay = |_: Duration| Ok(());

    assert!(!conn.is_connected() && conn.user_name().is_none());
    assert!(conn.get_accounts().unwrap_err().is_auth());
    let mut shown = None;
    let name = conn.connect("ASH-PC", &mut |a| shown = Some(a.user_code.clone()), &mut no_delay).unwrap();
    assert!(name == "Manu" && conn.user_name().as_deref() == Some("Manu") && shown.as_deref() == Some("WDJB-MJHT"));
    let raw = std::fs::read(conn.store().file_path()).unwrap();
    assert!(!String::from_utf8_lossy(&raw).contains("r1"), "tokens saved only through protect");

    assert!(conn.get_accounts().is_ok() && state.lock().unwrap().0 == 0, "fresh access token used as is");
    *now.lock().unwrap() += TimeDelta::seconds(59 * 60 + 30);
    assert!(conn.get_accounts().is_ok() && state.lock().unwrap().0 == 1);
    assert_eq!(conn.store().load().unwrap().refresh_token.as_deref(), Some("r2"), "rotated refresh token saved");
    state.lock().unwrap().1 = "revoked-elsewhere".into();
    let before = state.lock().unwrap().0;
    assert!(conn.get_accounts().is_ok() && state.lock().unwrap().0 == before + 1, "401: refreshed once and retried");

    conn.store().save(&TcgkbTokens {
        access_token: Some("old".into()), access_expires_utc: clock() - TimeDelta::minutes(1), refresh_token: Some("stale".into()),
        user_name: Some("Manu".into()), connected_utc: clock(),
    }).unwrap();
    assert!(conn.get_accounts().unwrap_err().is_auth() && !conn.is_connected() && !conn.store().file_path().exists(), "refresh refused: disconnected");

    conn.connect("ASH-PC", &mut |_| {}, &mut no_delay).unwrap();
    let revokes = server.count("/api/device/revoke");
    assert!(conn.disconnect() && !conn.is_connected() && server.count("/api/device/revoke") == revokes + 1);
    conn.connect("ASH-PC", &mut |_| {}, &mut no_delay).unwrap();
    let offline = TcgkbConnection::new(TcgkbApi::new(|_: &Request| Err("offline".into())), tcgkb_store(dir.path(), xor(), xor()), clock.clone());
    assert!(!offline.disconnect() && !offline.is_connected(), "disconnect offline: forgotten anyway, reported");

    server.set(|r| match r.path {
        "/api/ptcgl/accounts" => reply(500, ""),
        "/api/device/code" => reply(200, CODE_JSON),
        "/api/device/revoke" => reply(200, "{}"),
        _ => reply(200, &token_json("a1", "r1")),
    });
    let revokes = server.count("/api/device/revoke");
    assert!(conn.connect("pc", &mut |_| {}, &mut no_delay).is_err() && !conn.is_connected() && server.count("/api/device/revoke") == revokes + 1,
            "connect half done: nothing saved, grant revoked");
}
