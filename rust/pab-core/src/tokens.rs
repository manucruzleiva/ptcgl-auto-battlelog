//! tcgkb-connection.dat and pokematch-token.dat (TcgkbTokenStore, PokematchTokenStore): JSON
//! encrypted by `protect` (DPAPI CurrentUser in the tray, see `dpapi`). A file that does not decrypt
//! or parse is deleted: the player is simply not connected any more.
//!
//! The C# tray wrote these with DataContractJsonSerializer, whose dates are "\/Date(ms)\/" (UTC
//! milliseconds, maybe followed by a "+hhmm" offset that does not change the instant). Both are
//! read here, and written the same way (net_json escapes '/') so a downgrade to 1.6.x still reads them.

use std::path::{Path, PathBuf};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

use crate::file_ops;

pub const TCGKB_FILE_NAME: &str = "tcgkb-connection.dat";
pub const POKEMATCH_FILE_NAME: &str = "pokematch-token.dat";
/// The DPAPI entropy of each file: it ties the file to this app as well as to the Windows user.
pub const TCGKB_ENTROPY: &[u8] = b"PtcglAutoBattleLog/tcgkb-connection/v1";
pub const POKEMATCH_ENTROPY: &[u8] = b"PtcglAutoBattleLog/pokematch-token/v1";
pub const POKEMATCH_MAX_TOKEN_LENGTH: usize = 512;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TcgkbTokens {
    #[serde(default)]
    pub access_token: Option<String>,
    #[serde(with = "net_date", default = "epoch")]
    pub access_expires_utc: DateTime<Utc>,
    #[serde(default)]
    pub refresh_token: Option<String>,
    #[serde(default)]
    pub user_name: Option<String>,
    #[serde(with = "net_date", default = "epoch")]
    pub connected_utc: DateTime<Utc>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PokematchToken {
    #[serde(default)]
    pub token: Option<String>,
    #[serde(with = "net_date", default = "epoch")]
    pub set_utc: DateTime<Utc>,
}

fn epoch() -> DateTime<Utc> {
    DateTime::UNIX_EPOCH
}

/// PokematchApi.CleanToken: trimmed, without "Bearer ", 1..=512 printable ASCII characters.
pub fn clean_pokematch_token(pasted: &str) -> Option<String> {
    let t = pasted.trim();
    let t = if t.len() >= 7 && t[..7].eq_ignore_ascii_case("Bearer ") { t[7..].trim() } else { t };
    (!t.is_empty() && t.len() <= POKEMATCH_MAX_TOKEN_LENGTH && t.bytes().all(|c| c > b' ' && c < 0x7F)).then(|| t.to_string())
}

pub type Transform = dyn Fn(&[u8]) -> std::io::Result<Vec<u8>> + Send + Sync;

/// One encrypted JSON file in the data folder.
pub struct TokenStore<T> {
    path: PathBuf,
    protect: Box<Transform>,
    unprotect: Box<Transform>,
    valid: fn(&T) -> bool,
}

impl<T: Serialize + for<'de> Deserialize<'de>> TokenStore<T> {
    pub fn file_path(&self) -> &Path {
        &self.path
    }

    /// The saved value, or None. A file that does not decrypt, parse or validate is deleted.
    pub fn load(&self) -> Option<T> {
        let data = std::fs::read(&self.path).ok()?;
        let value = (self.unprotect)(&data)
            .ok()
            .and_then(|plain| serde_json::from_slice::<T>(plain.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&plain)).ok())
            .filter(|t| (self.valid)(t));
        if value.is_none() {
            self.delete();
        }
        value
    }

    /// Atomic: written beside, then swapped in.
    pub fn save(&self, value: &T) -> std::io::Result<()> {
        if !(self.valid)(value) {
            return Err(std::io::Error::new(std::io::ErrorKind::InvalidInput, "invalid token"));
        }
        let json = crate::net_json::to_string(value);
        file_ops::write_atomic(&self.path, &(self.protect)(json.as_bytes())?)
    }

    pub fn delete(&self) -> bool {
        match std::fs::remove_file(&self.path) {
            Ok(()) => true,
            Err(e) => e.kind() == std::io::ErrorKind::NotFound,
        }
    }
}

pub fn tcgkb_store(folder: &Path, protect: Box<Transform>, unprotect: Box<Transform>) -> TokenStore<TcgkbTokens> {
    TokenStore {
        path: folder.join(TCGKB_FILE_NAME),
        protect,
        unprotect,
        valid: |t| t.refresh_token.as_deref().is_some_and(|r| !r.is_empty()),
    }
}

pub fn pokematch_store(folder: &Path, protect: Box<Transform>, unprotect: Box<Transform>) -> TokenStore<PokematchToken> {
    TokenStore {
        path: folder.join(POKEMATCH_FILE_NAME),
        protect,
        unprotect,
        valid: |t| t.token.as_deref().is_some_and(|tok| clean_pokematch_token(tok).as_deref() == Some(tok)),
    }
}

/// DataContractJsonSerializer's DateTime: "/Date(ms)/" or "/Date(ms+hhmm)/" once JSON-decoded.
pub mod net_date {
    use super::*;

    pub fn serialize<S: Serializer>(t: &DateTime<Utc>, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&format!("/Date({})/", t.timestamp_millis()))
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<DateTime<Utc>, D::Error> {
        let s = String::deserialize(d)?;
        parse(&s).ok_or_else(|| serde::de::Error::custom(format!("not a .NET JSON date: {s}")))
    }

    pub fn parse(s: &str) -> Option<DateTime<Utc>> {
        let inner = s.strip_prefix("/Date(")?.strip_suffix(")/")?;
        // The offset (local-kind dates) follows the digits; the milliseconds are UTC either way.
        let end = inner.char_indices().skip(1).find(|&(_, c)| c == '+' || c == '-').map_or(inner.len(), |(i, _)| i);
        DateTime::from_timestamp_millis(inner[..end].parse().ok()?)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn identity() -> (Box<Transform>, Box<Transform>) {
        (Box::new(|b: &[u8]| Ok(b.iter().map(|x| x ^ 0x5A).collect())), Box::new(|b: &[u8]| Ok(b.iter().map(|x| x ^ 0x5A).collect())))
    }

    #[test]
    fn reads_what_the_csharp_tray_wrote() {
        // DataContractJsonSerializer output for a TcgkbTokens with UTC dates.
        let json = r#"{"accessToken":"at","accessExpiresUtc":"\/Date(1764072000000)\/","refreshToken":"rt","userName":"Ash","connectedUtc":"\/Date(1764000000000+0100)\/"}"#;
        let t: TcgkbTokens = serde_json::from_str(json).unwrap();
        assert_eq!(t.access_expires_utc.to_rfc3339(), "2025-11-25T12:00:00+00:00");
        assert_eq!(t.connected_utc.timestamp_millis(), 1764000000000);
        assert_eq!(t.user_name.as_deref(), Some("Ash"));
        assert_eq!(net_date::parse("/Date(-62135596800000)/").unwrap().to_rfc3339(), "0001-01-01T00:00:00+00:00", "DateTime.MinValue");
        assert!(net_date::parse("2026-01-01").is_none() && net_date::parse("/Date(x)/").is_none());
    }

    #[test]
    fn store_round_trip_and_cleanup() {
        let tmp = tempfile::tempdir().unwrap();
        let (p, u) = identity();
        let store = tcgkb_store(tmp.path(), p, u);
        assert!(store.load().is_none());
        let t = TcgkbTokens {
            access_token: Some("at".into()),
            access_expires_utc: DateTime::from_timestamp_millis(1764072000000).unwrap(),
            refresh_token: Some("rt".into()),
            user_name: Some("Ash \"K\" é".into()),
            connected_utc: epoch(),
        };
        store.save(&t).unwrap();
        let raw: Vec<u8> = std::fs::read(store.file_path()).unwrap().iter().map(|x| x ^ 0x5A).collect();
        assert!(String::from_utf8(raw).unwrap().contains(r#""accessExpiresUtc": "\/Date(1764072000000)\/""#), "written as .NET writes it");
        assert_eq!(store.load(), Some(t.clone()));
        assert!(store.save(&TcgkbTokens { refresh_token: None, ..t }).is_err(), "no refresh token: refused");

        std::fs::write(store.file_path(), b"garbage").unwrap();
        assert!(store.load().is_none() && !store.file_path().exists(), "undecryptable file deleted");

        let (p, u) = identity();
        let pm = pokematch_store(tmp.path(), p, u);
        let tok = PokematchToken { token: Some("abc".into()), set_utc: epoch() };
        pm.save(&tok).unwrap();
        assert_eq!(pm.load(), Some(tok));
        assert!(pm.save(&PokematchToken { token: Some("a b".into()), set_utc: epoch() }).is_err());
    }

    #[test]
    fn cleans_pokematch_tokens() {
        assert_eq!(clean_pokematch_token("  Bearer abc  ").as_deref(), Some("abc"));
        assert_eq!(clean_pokematch_token("bearer  abc").as_deref(), Some("abc"));
        assert_eq!(clean_pokematch_token("").as_deref(), None);
        assert_eq!(clean_pokematch_token("a b"), None);
        assert_eq!(clean_pokematch_token("tökén"), None);
        assert_eq!(clean_pokematch_token(&"x".repeat(513)), None);
    }
}
