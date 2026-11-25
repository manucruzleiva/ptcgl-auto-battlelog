//! The part of Pokematch's API the tray uses (PokematchApi.cs; docs/POKEMATCH-API.md): add a match
//! and replace one it added before, with the player's own API token. Ported as it is: the
//! PAB↔Pokematch integration is maintained separately, so behavior must not drift here.

use serde::Serialize;
use serde_json::Value;

use crate::tokens::clean_pokematch_token;

pub const BASE_URL: &str = "https://pokematch-vslogger.onrender.com";
pub const MATCHES_PATH: &str = "/api/v1/tcg-matches";
pub const ME_PATH: &str = "/api/v1/me";
/// The page where players make API tokens.
pub const TOKENS_PAGE: &str = "https://pokematch-vslogger.onrender.com/";
pub const SOURCE: &str = "PTCGL Auto Battle Log";
pub const UNKNOWN: &str = "Unknown";
/// Lowercase, at most 30 characters: Pokematch's tag rules.
pub const TAG: &str = "ptcgl-auto-battle-log";
pub const MAX_TAG_LENGTH: usize = 30;

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Request {
    pub method: &'static str,
    pub path: String,
    pub token: String,
    /// JSON, or None.
    pub body: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Response {
    pub status: u16,
    pub body: String,
}

/// Pokematch answered with an error, or could not be reached. The message is for people.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("{message}")]
pub struct PokematchError {
    pub message: String,
    /// The HTTP status, 0 when Pokematch was not reached.
    pub status: u16,
    /// Pokematch does not accept the token (wrong, deleted, or revoked by a password change).
    pub auth: bool,
}

impl PokematchError {
    /// Worth trying again later: not reached, busy, or a server error.
    pub fn transient(&self) -> bool {
        !self.auth && (self.status == 0 || self.status == 408 || self.status == 429 || self.status >= 500)
    }
    fn auth(status: u16) -> Self {
        PokematchError {
            message: "Pokematch does not accept the API token. Make a new one in Pokematch (changing your password revokes them) and set it again.".into(),
            status,
            auth: true,
        }
    }
}

pub type Result<T> = std::result::Result<T, PokematchError>;

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct Deck {
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct Game {
    /// "win", "loss" or "tie".
    pub result: Option<String>,
}

/// A TCG log as Pokematch's POST /api/v1/tcg-matches takes it.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Match {
    pub active_deck: Option<Deck>,
    pub opponent_deck: Option<String>,
    /// "Bo1", "Bo3" or "Bo5".
    pub format: Option<String>,
    pub games: Option<Vec<Game>>,
    /// "yyyy-MM-dd".
    pub date: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub notes: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tags: Option<Vec<String>>,
}

pub type SendFn<'a> = dyn Fn(&Request) -> std::result::Result<Response, String> + Sync + 'a;

pub struct PokematchApi<'a> {
    send: Box<SendFn<'a>>,
}

impl<'a> PokematchApi<'a> {
    pub fn new(send: impl Fn(&Request) -> std::result::Result<Response, String> + Sync + 'a) -> Self {
        PokematchApi { send: Box::new(send) }
    }

    /// GET /api/v1/me: Ok when Pokematch accepts the token.
    pub fn check_token(&self, token: &str) -> Result<()> {
        let r = self.send(Request { method: "GET", path: ME_PATH.into(), token: token.into(), body: None })?;
        if !(200..=299).contains(&r.status) {
            return Err(failure(&r));
        }
        Ok(())
    }

    /// Adds the match; returns Pokematch's id for it, or None when the answer names none.
    pub fn create(&self, token: &str, m: &Match) -> Result<Option<String>> {
        let r = self.send(Request { method: "POST", path: MATCHES_PATH.into(), token: token.into(), body: Some(to_json(m)) })?;
        if !(200..=299).contains(&r.status) {
            return Err(failure(&r));
        }
        let created: Option<Value> = r.body.trim_start().starts_with('{').then(|| serde_json::from_str(&r.body).ok()).flatten();
        Ok(created.as_ref().and_then(any_id).filter(|id| clean_id(id)))
    }

    /// Replaces a match it added before. Ok(false) when Pokematch no longer has it.
    pub fn update(&self, token: &str, id: &str, m: &Match) -> Result<bool> {
        assert!(clean_id(id), "invalid Pokematch id");
        let r = self.send(Request { method: "PUT", path: format!("{MATCHES_PATH}/{id}"), token: token.into(), body: Some(to_json(m)) })?;
        if r.status == 404 || r.status == 410 {
            return Ok(false);
        }
        if !(200..=299).contains(&r.status) {
            return Err(failure(&r));
        }
        Ok(true)
    }

    fn send(&self, request: Request) -> Result<Response> {
        if clean_pokematch_token(&request.token).as_deref() != Some(request.token.as_str()) {
            return Err(PokematchError::auth(0));
        }
        let r = (self.send)(&request)
            .map_err(|e| PokematchError { message: format!("Could not reach Pokematch: {e}"), status: 0, auth: false })?;
        if r.status == 401 || r.status == 403 {
            return Err(PokematchError::auth(r.status));
        }
        Ok(r)
    }
}

fn to_json(m: &Match) -> String {
    serde_json::to_string_pretty(m).expect("match serialize")
}

/// The new match's id: "id" or "_id" (a string or a number), at the top or under "match" or "data".
fn any_id(v: &Value) -> Option<String> {
    let text = |k: &str| match v.get(k)? {
        Value::String(s) => Some(s.clone()),
        Value::Number(n) => Some(n.to_string()),
        _ => None,
    };
    text("id").or_else(|| text("_id")).or_else(|| v.get("match").and_then(any_id)).or_else(|| v.get("data").and_then(any_id))
}

/// An id fit to go back into a URL path: letters, digits, '-' and '_', at most 128.
fn clean_id(id: &str) -> bool {
    !id.is_empty() && id.len() <= 128 && id.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-' || c == b'_')
}

fn failure(r: &Response) -> PokematchError {
    let detail = r.body.trim_start().starts_with('{').then(|| serde_json::from_str::<Value>(&r.body).ok()).flatten().and_then(|v| {
        let s = |k: &str| v.get(k).and_then(Value::as_str).map(String::from);
        s("message").or_else(|| s("error"))
    });
    let detail = detail.map(|d| d.chars().take(300).collect::<String>()).unwrap_or_default();
    let text = match r.status {
        429 => "Pokematch is busy (HTTP 429).".to_string(),
        s if s >= 500 => format!("Pokematch had a problem (HTTP {s})."),
        s => format!("Pokematch refused the battle log (HTTP {s})."),
    };
    let message = if detail.trim().is_empty() { text } else { format!("{text} {}", detail.trim()) };
    PokematchError { message, status: r.status, auth: false }
}
