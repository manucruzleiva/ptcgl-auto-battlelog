//! The tcgkb API the tray uses (TcgkbApi.cs, TcgkbConnection.cs; docs/TCGKB-API.md): the OAuth 2.0
//! device authorization grant (RFC 8628), the PTCGL accounts, and the upload of each sealed match.
//! HTTP, the clock and waiting are injected, so tests run against a fake tcgkb.

use std::path::Path;
use std::sync::Mutex;
use std::time::Duration;

use base64::Engine;
use chrono::{DateTime, TimeDelta, Utc};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};

use crate::seal_summary::SealSummary;
use crate::tokens::{TcgkbTokens, TokenStore};

pub const BASE_URL: &str = "https://tcgkb.app";
pub const CLIENT_ID: &str = "ptcgl-auto-battlelog";
pub const SCOPES: &str = "claims:write logs:upload";
pub const DEVICE_CODE_GRANT: &str = "urn:ietf:params:oauth:grant-type:device_code";
/// RFC 8628 §3.5: the default interval, and what slow_down adds to it.
pub const DEFAULT_INTERVAL: Duration = Duration::from_secs(5);
/// PtcglAccountScanner.MaxEvidenceBytes: the largest .txt or .seal sent.
pub const MAX_EVIDENCE_BYTES: u64 = 1024 * 1024;
const REFRESH_MARGIN: TimeDelta = TimeDelta::minutes(1);

/// One HTTP request: a path under BASE_URL, never a full URL.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Request {
    pub method: &'static str,
    pub path: &'static str,
    /// The access token for "Authorization: Bearer".
    pub bearer: Option<String>,
    pub content_type: Option<&'static str>,
    pub body: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Response {
    pub status: u16,
    pub body: String,
}

/// tcgkb answered with an error, or with something that is not the API's answer. The message is for people.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
#[error("{message}")]
pub struct TcgkbError {
    /// The API's "error", or "bad_response" / "network" / "not_connected" / "cancelled".
    pub code: String,
    pub message: String,
    pub status: u16,
}

impl TcgkbError {
    fn new(code: &str, message: impl Into<String>, status: u16) -> Self {
        TcgkbError { code: code.into(), message: message.into(), status }
    }
    /// The connection is gone (revoked, expired, never made): the player has to connect again.
    pub fn auth(message: &str) -> Self {
        Self::new("not_connected", message, 401)
    }
    pub fn cancelled() -> Self {
        Self::new("cancelled", "Cancelled.", 0)
    }
    pub fn is_auth(&self) -> bool {
        self.code == "not_connected"
    }
    fn bad_response(what: &str) -> Self {
        Self::new("bad_response", format!("tcgkb answered with {what}. Try again later, or update this app."), 0)
    }
}

pub type Result<T> = std::result::Result<T, TcgkbError>;

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default)]
pub struct DeviceAuthorization {
    pub device_code: String,
    pub user_code: String,
    pub verification_uri: String,
    pub verification_uri_complete: Option<String>,
    pub expires_in: i64,
    pub interval: i64,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default)]
pub struct TokenResponse {
    pub access_token: String,
    pub token_type: String,
    pub expires_in: i64,
    pub refresh_token: String,
    pub scope: Option<String>,
}

#[derive(Debug, Default, Deserialize)]
#[serde(default)]
struct ApiError {
    error: Option<String>,
    error_description: Option<String>,
    message: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default)]
pub struct User {
    pub name: Option<String>,
}

/// A PTCGL account as tcgkb knows it. Status: "claimed", "verified" or "disputed".
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Account {
    pub account_hash: Option<String>,
    pub name: Option<String>,
    pub status: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default)]
pub struct Accounts {
    pub user: Option<User>,
    pub accounts: Vec<Account>,
    /// Where the user stands on the ranking; None from a tcgkb older than this field.
    pub ladder: Option<Ladder>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Ladder {
    pub games: i64,
    pub min_games: i64,
    /// None while under min_games.
    pub rank: Option<i64>,
    pub players: i64,
}

/// A sealed log as tcgkb takes it: files exactly as on disk, base64.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClaimEvidence {
    pub file_name: String,
    pub log: String,
    pub seal: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub deck: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct UploadResponse {
    pub added: bool,
    pub account: Option<Account>,
    pub player_deck: Option<String>,
    pub opponent_deck: Option<String>,
    pub deck_ref: Option<DeckRef>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct DeckRef {
    pub deck_id: Option<String>,
    pub version: i64,
    pub changed: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PollState {
    Pending,
    SlowDown,
    Granted(TokenResponse),
    Denied,
    Expired,
}

pub type SendFn<'a> = dyn Fn(&Request) -> std::result::Result<Response, String> + Sync + 'a;

pub struct TcgkbApi<'a> {
    send: Box<SendFn<'a>>,
}

impl<'a> TcgkbApi<'a> {
    pub fn new(send: impl Fn(&Request) -> std::result::Result<Response, String> + Sync + 'a) -> Self {
        TcgkbApi { send: Box::new(send) }
    }

    /// Asks for a code. The page it names is always on BASE_URL: anything else is refused, never opened.
    pub fn start_device_authorization(&self, device_name: &str) -> Result<DeviceAuthorization> {
        let r = self.post_form("/api/device/code", &[("client_id", CLIENT_ID), ("scope", SCOPES), ("device_name", clip(device_name, 64))])?;
        let mut a: DeviceAuthorization = ok(&r)?;
        if a.device_code.is_empty() || a.device_code.chars().count() > 512 || !is_user_code(&a.user_code) || a.expires_in <= 0 {
            return Err(TcgkbError::bad_response("an incomplete device code"));
        }
        if !is_own_page(&a.verification_uri) {
            return Err(TcgkbError::bad_response(&format!("a sign-in page outside {BASE_URL}")));
        }
        if a.verification_uri_complete.as_deref().is_some_and(|u| !is_own_page(u)) {
            a.verification_uri_complete = None;
        }
        Ok(a)
    }

    /// One look at whether the player approved the code.
    pub fn poll(&self, device_code: &str) -> Result<PollState> {
        let r = self.post_form("/api/device/token", &[("grant_type", DEVICE_CODE_GRANT), ("device_code", device_code), ("client_id", CLIENT_ID)])?;
        if r.status == 200 {
            return tokens(&r).map(PollState::Granted);
        }
        match error_of(&r).and_then(|e| e.error).as_deref() {
            Some("authorization_pending") => Ok(PollState::Pending),
            Some("slow_down") => Ok(PollState::SlowDown),
            Some("access_denied") => Ok(PollState::Denied),
            Some("expired_token") => Ok(PollState::Expired),
            _ => Err(failure(&r)),
        }
    }

    /// Polls at the server's interval until the code is approved, denied or expires. `delay` waits
    /// (and returns Err to cancel); `now` is the clock.
    pub fn wait_for_approval(
        &self,
        a: &DeviceAuthorization,
        delay: &mut dyn FnMut(Duration) -> Result<()>,
        now: &dyn Fn() -> DateTime<Utc>,
    ) -> Result<TokenResponse> {
        let mut interval = if a.interval > 0 { Duration::from_secs(a.interval.min(60) as u64) } else { DEFAULT_INTERVAL };
        let deadline = now() + TimeDelta::seconds(a.expires_in.min(3600));
        let expired = || TcgkbError::new("expired_token", "The code expired before it was approved. Try connecting again.", 0);
        loop {
            delay(interval)?;
            if now() >= deadline {
                return Err(expired());
            }
            match self.poll(&a.device_code)? {
                PollState::Granted(t) => return Ok(t),
                PollState::SlowDown => interval += DEFAULT_INTERVAL,
                PollState::Pending => {}
                PollState::Denied => return Err(TcgkbError::new("access_denied", "The connection was declined on tcgkb.", 0)),
                PollState::Expired => return Err(expired()),
            }
        }
    }

    /// New tokens for a refresh token (rotated: the old one stops working). invalid_grant is an auth error.
    pub fn refresh(&self, refresh_token: &str) -> Result<TokenResponse> {
        let r = self.post_form("/api/device/token", &[("grant_type", "refresh_token"), ("refresh_token", refresh_token), ("client_id", CLIENT_ID)])?;
        if r.status == 200 {
            return tokens(&r);
        }
        if r.status == 401 || error_of(&r).and_then(|e| e.error).as_deref() == Some("invalid_grant") {
            return Err(TcgkbError::auth("tcgkb no longer accepts this connection. Connect again."));
        }
        Err(failure(&r))
    }

    /// Ends the connection on tcgkb's side (RFC 7009: a token it does not know is not an error).
    pub fn revoke(&self, refresh_token: &str) -> Result<()> {
        let r = self.post_form("/api/device/revoke", &[("token", refresh_token), ("client_id", CLIENT_ID)])?;
        if r.status != 200 && r.status != 204 {
            return Err(failure(&r));
        }
        Ok(())
    }

    /// The connected tcgkb user and the PTCGL accounts they own (malformed hashes dropped).
    pub fn get_accounts(&self, access_token: &str) -> Result<Accounts> {
        let r = self.send(Request { method: "GET", path: "/api/ptcgl/accounts", bearer: Some(access_token.into()), ..Default::default() })?;
        let mut a: Accounts = ok(&r)?;
        if a.user.is_none() {
            return Err(TcgkbError::bad_response("no user"));
        }
        a.accounts.retain(|x| x.account_hash.as_deref().is_some_and(is_account_hash));
        Ok(a)
    }

    /// Uploads one sealed battle log. tcgkb checks the seal; a log it already has is accepted again.
    pub fn upload_log(&self, access_token: &str, log: &ClaimEvidence) -> Result<UploadResponse> {
        assert!(!log.log.is_empty(), "a battle log is required");
        let body = serde_json::to_string_pretty(log).expect("evidence serialize");
        let r = self.send(Request {
            method: "POST",
            path: "/api/ptcgl/logs",
            bearer: Some(access_token.into()),
            content_type: Some("application/json"),
            body: Some(body),
        })?;
        ok(&r)
    }

    fn post_form(&self, path: &'static str, fields: &[(&str, &str)]) -> Result<Response> {
        let body = fields.iter().map(|(k, v)| format!("{}={}", escape_data(k), escape_data(v))).collect::<Vec<_>>().join("&");
        self.send(Request { method: "POST", path, content_type: Some("application/x-www-form-urlencoded"), body: Some(body), bearer: None })
    }

    fn send(&self, request: Request) -> Result<Response> {
        let r = (self.send)(&request).map_err(|e| TcgkbError::new("network", format!("Could not reach tcgkb: {e}"), 0))?;
        if r.status == 401 && request.bearer.is_some() {
            return Err(TcgkbError::auth("tcgkb no longer accepts this connection. Connect again."));
        }
        Ok(r)
    }
}

/// A tcgkb connection over its lifetime: connect with a device code, call the API with a fresh
/// access token (refreshed a minute before it expires, and once more if tcgkb says 401), and
/// disconnect. When tcgkb no longer accepts the refresh token the saved connection is deleted.
pub struct TcgkbConnection<'a> {
    pub api: TcgkbApi<'a>,
    store: TokenStore<TcgkbTokens>,
    now: Box<dyn Fn() -> DateTime<Utc> + Sync + 'a>,
    lock: Mutex<()>,
}

impl<'a> TcgkbConnection<'a> {
    pub fn new(api: TcgkbApi<'a>, store: TokenStore<TcgkbTokens>, now: impl Fn() -> DateTime<Utc> + Sync + 'a) -> Self {
        TcgkbConnection { api, store, now: Box::new(now), lock: Mutex::new(()) }
    }

    pub fn store(&self) -> &TokenStore<TcgkbTokens> {
        &self.store
    }

    /// The connected tcgkb user's name, or None when not connected.
    pub fn user_name(&self) -> Option<String> {
        self.store.load().and_then(|t| t.user_name)
    }

    pub fn is_connected(&self) -> bool {
        self.store.load().is_some()
    }

    /// The whole device flow: a code, `show` (opens the page and shows the code), waiting for the
    /// approval, then the tcgkb user's name. Saved only once everything worked.
    pub fn connect(&self, device_name: &str, show: &mut dyn FnMut(&DeviceAuthorization), delay: &mut dyn FnMut(Duration) -> Result<()>) -> Result<String> {
        let code = self.api.start_device_authorization(device_name)?;
        show(&code);
        let granted = self.api.wait_for_approval(&code, delay, &*self.now)?;
        let mut tokens = self.to_tokens(&granted, None);
        let me = match self.api.get_accounts(tokens.access_token.as_deref().unwrap_or("")) {
            Ok(me) => me,
            Err(e) => {
                let _ = self.api.revoke(&granted.refresh_token);
                return Err(e);
            }
        };
        tokens.user_name = me.user.and_then(|u| u.name);
        tokens.connected_utc = (self.now)();
        let _guard = self.lock.lock().unwrap_or_else(|p| p.into_inner());
        self.store.save(&tokens).map_err(|e| TcgkbError::new("io", format!("Could not save the tcgkb connection: {e}"), 0))?;
        Ok(tokens.user_name.unwrap_or_default())
    }

    /// Forgets the connection here and, best effort, on tcgkb. True when tcgkb confirmed it.
    pub fn disconnect(&self) -> bool {
        let tokens = {
            let _guard = self.lock.lock().unwrap_or_else(|p| p.into_inner());
            let t = self.store.load();
            self.store.delete();
            t
        };
        match tokens.and_then(|t| t.refresh_token) {
            None => true,
            Some(r) => self.api.revoke(&r).is_ok(),
        }
    }

    /// Runs an API call with a valid access token; one refresh and retry when tcgkb answers 401.
    pub fn call<T>(&self, call: impl Fn(&TcgkbApi<'a>, &str) -> Result<T>) -> Result<T> {
        let access = self.access_token(false)?;
        match call(&self.api, &access) {
            Err(e) if e.is_auth() => {}
            other => return other,
        }
        let access = self.access_token(true)?;
        let result = call(&self.api, &access);
        if result.as_ref().is_err_and(TcgkbError::is_auth) {
            let _guard = self.lock.lock().unwrap_or_else(|p| p.into_inner());
            self.store.delete();
        }
        result
    }

    pub fn get_accounts(&self) -> Result<Accounts> {
        self.call(|api, access| api.get_accounts(access))
    }

    pub fn upload_log(&self, log: &ClaimEvidence) -> Result<UploadResponse> {
        self.call(|api, access| api.upload_log(access, log))
    }

    fn access_token(&self, force_refresh: bool) -> Result<String> {
        let _guard = self.lock.lock().unwrap_or_else(|p| p.into_inner());
        let tokens = self.store.load().ok_or_else(|| TcgkbError::auth("Not connected to tcgkb."))?;
        if let Some(access) = tokens.access_token.as_deref().filter(|a| !force_refresh && !a.is_empty() && (self.now)() + REFRESH_MARGIN < tokens.access_expires_utc) {
            return Ok(access.to_string());
        }
        let fresh = match self.api.refresh(tokens.refresh_token.as_deref().unwrap_or("")) {
            Ok(f) => f,
            Err(e) => {
                if e.is_auth() {
                    self.store.delete();
                }
                return Err(e);
            }
        };
        let next = self.to_tokens(&fresh, Some(&tokens));
        self.store.save(&next).map_err(|e| TcgkbError::new("io", format!("Could not save the tcgkb connection: {e}"), 0))?;
        Ok(fresh.access_token)
    }

    fn to_tokens(&self, r: &TokenResponse, previous: Option<&TcgkbTokens>) -> TcgkbTokens {
        let now = (self.now)();
        TcgkbTokens {
            access_token: Some(r.access_token.clone()),
            access_expires_utc: now + TimeDelta::seconds(r.expires_in.min(24 * 3600)),
            refresh_token: Some(r.refresh_token.clone()),
            user_name: previous.and_then(|p| p.user_name.clone()),
            connected_utc: previous.map_or(now, |p| p.connected_utc),
        }
    }
}

/// A sealed log as tcgkb takes it: the .txt and the .seal sidecar (if any), exactly as on disk;
/// with `with_deck` (match uploads), also the deck file the seal names, when it matches.
pub fn build_evidence(s: &SealSummary, with_deck: bool) -> std::io::Result<ClaimEvidence> {
    let b64 = |bytes: &[u8]| base64::engine::general_purpose::STANDARD.encode(bytes);
    Ok(ClaimEvidence {
        file_name: s.log_file.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
        log: b64(&read_capped(&s.log_file)?),
        seal: s.seal_file.as_deref().map(read_capped).transpose()?.map(|b| b64(&b)),
        deck: if with_deck { s.read_deck().map(|d| b64(&d)) } else { None },
    })
}

fn read_capped(path: &Path) -> std::io::Result<Vec<u8>> {
    if std::fs::metadata(path)?.len() > MAX_EVIDENCE_BYTES {
        return Err(std::io::Error::other(format!("{} is too large to send.", path.display())));
    }
    std::fs::read(path)
}

/// 64 lowercase hex: the seal's accountHash (docs/LOG-SEAL.md §3).
pub fn is_account_hash(s: &str) -> bool {
    s.len() == 64 && s.bytes().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
}

/// A page of BASE_URL over https with nothing tricky in it: the only kind of address the tray
/// opens for tcgkb. Canonical form only (no dot segments, case changes, port, userinfo, fragment
/// or percent-encoding), so what is checked is what gets opened.
pub fn is_own_page(url: &str) -> bool {
    if url.is_empty() || url.len() > 512 || url.bytes().any(|c| c <= b' ' || c >= 0x7F || matches!(c, b'\\' | b'@' | b'#' | b'%')) {
        return false;
    }
    let Some(rest) = url.strip_prefix("https://tcgkb.app/") else { return false };
    let path = rest.split('?').next().unwrap_or("");
    !path.split('/').any(|seg| seg == "." || seg == "..")
}

/// What the player types or compares: letters, digits and '-', at most 16.
fn is_user_code(s: &str) -> bool {
    !s.is_empty() && s.len() <= 16 && s.bytes().all(|c| c.is_ascii_uppercase() || c.is_ascii_digit() || c == b'-')
}

/// Uri.EscapeDataString: everything but RFC 3986 unreserved characters, as %XX of its UTF-8.
fn escape_data(s: &str) -> String {
    s.bytes()
        .map(|b| if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') { (b as char).to_string() } else { format!("%{b:02X}") })
        .collect()
}

fn clip(s: &str, max: usize) -> &str {
    s.char_indices().nth(max).map_or(s, |(i, _)| &s[..i])
}

fn tokens(r: &Response) -> Result<TokenResponse> {
    let t: TokenResponse = ok(r)?;
    if t.access_token.is_empty() || t.refresh_token.is_empty() || !t.token_type.eq_ignore_ascii_case("Bearer") || t.expires_in <= 0 {
        return Err(TcgkbError::bad_response("incomplete tokens"));
    }
    Ok(t)
}

fn read_object<T: DeserializeOwned>(body: &str) -> Option<T> {
    body.trim_start().starts_with('{').then(|| serde_json::from_str(body).ok()).flatten()
}

fn ok<T: DeserializeOwned>(r: &Response) -> Result<T> {
    if !(200..=299).contains(&r.status) {
        return Err(failure(r));
    }
    read_object(&r.body).ok_or_else(|| TcgkbError::bad_response("something that is not JSON"))
}

fn error_of(r: &Response) -> Option<ApiError> {
    read_object(&r.body)
}

/// The API's error, worded for the player; the server's own message when it gives one.
fn failure(r: &Response) -> TcgkbError {
    let e = error_of(r).unwrap_or_default();
    let code = e.error.filter(|c| !c.is_empty()).unwrap_or_else(|| format!("http_{}", r.status));
    let detail = e.message.or(e.error_description).map(|d| clip(&d, 300).to_string()).unwrap_or_default();
    let text = match code.as_str() {
        "claimed_by_other" => "This PTCGL account already belongs to another tcgkb user.".to_string(),
        "invalid_evidence" => "tcgkb could not check these battle logs.".into(),
        "invalid_log" => "tcgkb could not check this battle log.".into(),
        "not_claimed" => "This PTCGL account is not claimed on tcgkb by you.".into(),
        "rate_limited" => "tcgkb is busy. Try again in a few minutes.".into(),
        _ if r.status == 404 => "tcgkb does not offer this yet (HTTP 404).".into(),
        _ if r.status == 429 => "tcgkb is busy. Try again in a few minutes.".into(),
        _ if r.status >= 500 => format!("tcgkb had a problem (HTTP {}). Try again later.", r.status),
        _ => format!("tcgkb refused the request ({code})."),
    };
    let message = if detail.is_empty() { text } else { format!("{text} {detail}") };
    TcgkbError::new(&code, message, r.status)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn helpers() {
        assert_eq!(escape_data("claims:write logs:upload"), "claims%3Awrite%20logs%3Aupload");
        assert_eq!(escape_data("Ashé-PC_1.~"), "Ash%C3%A9-PC_1.~");
        for url in ["https://tcgkb.app/link", "https://tcgkb.app/link?code=AB-12"] {
            assert!(is_own_page(url), "{url}");
        }
        for url in ["http://tcgkb.app/link", "https://tcgkb.app.evil.example/link", "https://user@tcgkb.app/link", "https://tcgkb.app:444/link",
                    "https://TCGKB.app/link", "https://tcgkb.app/a/../link", "https://tcgkb.app/link#x", "javascript:alert(1)", "", "https://tcgkb.app"] {
            assert!(!is_own_page(url), "{url}");
        }
        assert_eq!(clip("héllo", 2), "hé");
    }
}
