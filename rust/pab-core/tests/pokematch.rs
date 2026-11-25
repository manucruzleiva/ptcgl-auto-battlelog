//! Ported from tests/TrayTests/PokematchTests.cs §37, against a fake Pokematch.

use std::sync::{Arc, Mutex};

use pab_core::pokematch::*;

type Handler = Box<dyn Fn(&Request) -> std::result::Result<Response, String> + Send>;

fn reply(status: u16, body: &str) -> std::result::Result<Response, String> {
    Ok(Response { status, body: body.into() })
}

fn sample() -> Match {
    Match {
        active_deck: Some(Deck { name: Some("Dragapult ex".into()) }),
        opponent_deck: Some("Gardevoir ex".into()),
        format: Some("Bo1".into()),
        games: Some(vec![Game { result: Some("win".into()) }]),
        date: Some("2026-10-21".into()),
        ..Default::default()
    }
}

#[test]
fn add_and_replace_matches() {
    let requests = Arc::new(Mutex::new(Vec::<Request>::new()));
    let handle: Arc<Mutex<Handler>> = Arc::new(Mutex::new(Box::new(|_| reply(404, r#"{"error":"not found"}"#))));
    let set = |h: Handler| *handle.lock().unwrap() = h;
    let api = {
        let (requests, handle) = (requests.clone(), handle.clone());
        PokematchApi::new(move |r: &Request| {
            requests.lock().unwrap().push(r.clone());
            (handle.lock().unwrap())(r)
        })
    };
    let last = || requests.lock().unwrap().last().unwrap().clone();

    set(Box::new(|_| reply(201, r#"{"_id":"65f0c0ffee","result":"win"}"#)));
    assert_eq!(api.create("tok", &sample()).unwrap().as_deref(), Some("65f0c0ffee"));
    let req = last();
    assert!(req.method == "POST" && req.path == "/api/v1/tcg-matches" && req.token == "tok");
    let body: String = req.body.unwrap().chars().filter(|c| !c.is_whitespace()).collect();
    for part in [r#""activeDeck":{"name":"Dragapultex"}"#, r#""opponentDeck":"Gardevoirex""#, r#""games":[{"result":"win"}]"#, r#""format":"Bo1""#, r#""date":"2026-10-21""#] {
        assert!(body.contains(part), "{part} in {body}");
    }
    assert!(!body.contains("\"notes\"") && !body.contains("\"tags\""));

    set(Box::new(|_| reply(200, r#"{"userId":"u1","tokenName":"PTCGL uploader"}"#)));
    assert!(api.check_token("pmk_tok").is_ok() && last().method == "GET" && last().path == "/api/v1/me");
    set(Box::new(|_| reply(401, r#"{"error":"Invalid token"}"#)));
    assert!(api.check_token("pmk_tok").unwrap_err().auth);

    for (body, id) in [(r#"{"id":42}"#, Some("42")), (r#"{"match":{"id":"abc"}}"#, Some("abc")), (r#"{"data":{"_id":"d1"}}"#, Some("d1")), ("", None), (r#"{"id":"../../admin"}"#, None)] {
        let b = body.to_string();
        set(Box::new(move |_| reply(201, &b)));
        assert_eq!(api.create("tok", &sample()).unwrap().as_deref(), id, "{body}");
    }

    set(Box::new(|_| reply(200, "{}")));
    assert!(api.update("tok", "abc", &sample()).unwrap());
    assert!(last().method == "PUT" && last().path == "/api/v1/tcg-matches/abc");
    set(Box::new(|_| reply(404, r#"{"error":"Not found"}"#)));
    assert!(!api.update("tok", "abc", &sample()).unwrap());
    assert!(std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| api.update("tok", "a/b", &sample()))).is_err(), "PUT refuses an id with '/'");

    for status in [401, 403] {
        set(Box::new(move |_| reply(status, r#"{"error":"Invalid token"}"#)));
        assert!(api.create("tok", &sample()).unwrap_err().auth);
    }
    let before = requests.lock().unwrap().len();
    assert!(api.create("bad token", &sample()).unwrap_err().auth && requests.lock().unwrap().len() == before, "a malformed token is never sent");

    set(Box::new(|_| reply(400, r#"{"message":"result is required"}"#)));
    let e = api.create("tok", &sample()).unwrap_err();
    assert!(!e.transient() && e.status == 400 && e.message.contains("result is required"));
    for status in [429, 500, 503] {
        set(Box::new(move |_| reply(status, "<html>")));
        assert!(api.create("tok", &sample()).unwrap_err().transient());
    }
    let down = PokematchApi::new(|_: &Request| Err("No such host".into()));
    let e = down.create("tok", &sample()).unwrap_err();
    assert!(e.transient() && e.status == 0 && e.message.contains("No such host"));
}
