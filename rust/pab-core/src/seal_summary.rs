//! The few seal fields the tray needs (SealSummary.cs): who played, the match, when it was sealed
//! and by which key. It finds the seal as docs/LOG-SEAL.md §2 says (a footer in the .txt, else a
//! .seal sidecar) and reads the metadata line in its canonical v1 or v2 form. It verifies nothing:
//! tcgkb checks the signature of every log it is sent.

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::LazyLock;

use chrono::{DateTime, NaiveDateTime, Utc};
use regex::Regex;

use crate::hashing;

pub const BEGIN: &str = "-----BEGIN PTCGL-ABL SEAL v2-----";
pub const END: &str = "-----END PTCGL-ABL SEAL v2-----";
pub const BEGIN_V1: &str = "-----BEGIN PTCGL-ABL SEAL v1-----";
pub const END_V1: &str = "-----END PTCGL-ABL SEAL v1-----";
pub const SIDECAR_EXTENSION: &str = "seal";
pub const DECK_EXTENSION: &str = "deck";
/// Where the plugin keeps each distinct deck once, by its SHA-256 (docs/LOG-SEAL.md §2a).
pub const DECKS_FOLDER: &str = "decks";
/// A .deck file is one line of about 1 KB; anything far larger is not one.
pub const MAX_DECK_BYTES: u64 = 64 * 1024;
/// Starts a footer seal of any version; the last occurrence counts (docs/LOG-SEAL.md §2).
const FOOTER_PREFIX: &[u8] = b"\n\n-----BEGIN PTCGL-ABL SEAL v";
/// A seal block is at most 16 KiB (docs/LOG-SEAL.md §2), so the footer lies within this tail.
const TAIL_BYTES: u64 = 20 * 1024;

const JSON_STRING: &str = r#""((?:[^"\\]|\\.)*)""#;
const UTC: &str = r"([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z)";

fn re(pattern: String) -> Regex {
    Regex::new(&pattern).expect("valid seal regex")
}

static PLAYER: LazyLock<Regex> = LazyLock::new(|| re(format!(r#","player":\{{"name":(?:null|{JSON_STRING}),"accountHash":(?:null|"([0-9a-f]{{64}})")\}}"#)));
static MATCH_ID: LazyLock<Regex> = LazyLock::new(|| re(format!(r#","matchId":(?:null|{JSON_STRING})"#)));
static RESULT: LazyLock<Regex> = LazyLock::new(|| re(r#","result":"(VICTORY|DEFEAT|NO-RESULT|EXPORTED)""#.into()));
static SEALED: LazyLock<Regex> = LazyLock::new(|| re(format!(r#","sealedUtc":"{UTC}""#)));
static KEY_ID: LazyLock<Regex> = LazyLock::new(|| re(r#","keyId":"([A-Za-z0-9_-]{1,128})""#.into()));
static OPPONENT: LazyLock<Regex> = LazyLock::new(|| re(format!(r#","opponent":\{{"name":(?:null|{JSON_STRING}),"accountHash":"#)));
static DECK: LazyLock<Regex> = LazyLock::new(|| re(format!(r#","deck":(?:null|{JSON_STRING})"#)));
static DECK_SHA: LazyLock<Regex> = LazyLock::new(|| re(r#","deckSha256":(?:null|"([0-9a-f]{64})")"#.into()));
static QUEUE: LazyLock<Regex> = LazyLock::new(|| re(format!(r#","queue":(?:null|{JSON_STRING})"#)));
static FORMAT: LazyLock<Regex> = LazyLock::new(|| re(format!(r#","format":(?:null|{JSON_STRING})"#)));
static ENDED: LazyLock<Regex> = LazyLock::new(|| re(format!(r#","endedUtc":(?:null|"{UTC}")"#)));

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SealSummary {
    /// The .txt the seal belongs to, and its sidecar when the seal is in one.
    pub log_file: PathBuf,
    pub seal_file: Option<PathBuf>,
    pub player_name: Option<String>,
    pub player_account_hash: Option<String>,
    pub match_id: Option<String>,
    pub result: Option<String>,
    pub key_id: String,
    pub opponent_name: Option<String>,
    pub deck: Option<String>,
    pub sealed_utc: DateTime<Utc>,
    /// When the plugin saw the match end; None for EXPORTED.
    pub ended_utc: Option<DateTime<Utc>>,
    /// Seal format version: 1 or 2.
    pub version: u8,
    /// The seal's deckSha256 (v2): names decks/<hash>.deck in the log's folder.
    pub deck_sha256: Option<String>,
    /// The client's names (Ranked, Casual, BuildAndBattle, Friend; Standard, Expanded), v2 only.
    pub queue: Option<String>,
    pub format: Option<String>,
}

impl SealSummary {
    /// The seal of a battle log .txt: its footer, else <name>.seal beside it. None when the log is
    /// unsealed, the seal is not v1 or v2 or the file cannot be read. Never panics.
    pub fn try_read(log_file: &Path) -> Option<SealSummary> {
        let tail = read_tail(log_file).ok()?;
        if let Some(at) = last_index_of(&tail, FOOTER_PREFIX) {
            return parse(&ascii(&tail[at + 2..]), log_file, None);
        }
        let sidecar = log_file.with_extension(SIDECAR_EXTENSION);
        if !sidecar.is_file() {
            return None;
        }
        let block = read_tail(&sidecar).ok()?;
        let block = block.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&block);
        parse(&ascii(block), log_file, Some(sidecar))
    }

    /// The deck the seal names (decks/<deckSha256>.deck in the log's folder), exactly as on disk,
    /// when its hash matches; None otherwise (no deck, a v1 seal, missing, changed or too large).
    pub fn read_deck(&self) -> Option<Vec<u8>> {
        let sha = self.deck_sha256.as_deref()?;
        let path = self.log_file.parent().unwrap_or(Path::new("")).join(DECKS_FOLDER).join(format!("{sha}.{DECK_EXTENSION}"));
        if std::fs::metadata(&path).ok()?.len() > MAX_DECK_BYTES {
            return None;
        }
        let bytes = std::fs::read(&path).ok()?;
        (hashing::to_hex(&hashing::sha256(&bytes)) == sha).then_some(bytes)
    }
}

/// "-----BEGIN…-----\n{json}\n{signature}\n-----END…-----\n" exactly, with the fields above.
pub fn parse(block: &str, log_file: &Path, seal_file: Option<PathBuf>) -> Option<SealSummary> {
    let lines: Vec<&str> = block.split('\n').collect();
    if lines.len() != 5 || !lines[4].is_empty() || lines[2].is_empty() {
        return None;
    }
    let version: u8 = match (lines[0], lines[3]) {
        (BEGIN, END) => 2,
        (BEGIN_V1, END_V1) => 1,
        _ => return None,
    };
    let json = lines[1];
    if !json.starts_with(&format!("{{\"v\":{version},")) {
        return None;
    }
    let player = PLAYER.captures(json)?;
    let sealed = SEALED.captures(json)?;
    let key_id = KEY_ID.captures(json)?;
    let text = |r: &Regex| r.captures(json).and_then(|c| c.get(1)).and_then(|m| unescape(m.as_str()));
    let v2_text = |r: &Regex| if version >= 2 { text(r) } else { None };
    Some(SealSummary {
        log_file: log_file.to_path_buf(),
        seal_file,
        player_name: player.get(1).and_then(|m| unescape(m.as_str())),
        player_account_hash: player.get(2).map(|m| m.as_str().to_string()),
        match_id: text(&MATCH_ID),
        result: RESULT.captures(json).map(|c| c[1].to_string()),
        key_id: key_id[1].to_string(),
        opponent_name: text(&OPPONENT),
        deck: text(&DECK),
        sealed_utc: parse_utc(&sealed[1])?,
        ended_utc: ENDED.captures(json).and_then(|c| c.get(1)).and_then(|m| parse_utc(m.as_str())),
        version,
        deck_sha256: if version >= 2 { DECK_SHA.captures(json).and_then(|c| c.get(1)).map(|m| m.as_str().to_string()) } else { None },
        queue: v2_text(&QUEUE),
        format: v2_text(&FORMAT),
    })
}

fn parse_utc(s: &str) -> Option<DateTime<Utc>> {
    NaiveDateTime::parse_from_str(s, "%Y-%m-%dT%H:%M:%SZ").ok().map(|t| t.and_utc())
}

/// The body of a JSON string literal as the seal writes it (ASCII, \u escapes) → its text.
/// None when malformed. UTF-16 surrogate pairs from \u escapes are joined.
pub fn unescape(s: &str) -> Option<String> {
    let mut units: Vec<u16> = Vec::with_capacity(s.len());
    let mut chars = s.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            let mut buf = [0u16; 2];
            units.extend_from_slice(c.encode_utf16(&mut buf));
            continue;
        }
        let unit = match chars.next()? {
            '"' => b'"' as u16,
            '\\' => b'\\' as u16,
            '/' => b'/' as u16,
            'b' => 8,
            'f' => 12,
            'n' => b'\n' as u16,
            'r' => b'\r' as u16,
            't' => b'\t' as u16,
            'u' => {
                let hex: String = chars.by_ref().take(4).collect();
                if hex.len() != 4 || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
                    return None;
                }
                u16::from_str_radix(&hex, 16).ok()?
            }
            _ => return None,
        };
        units.push(unit);
    }
    Some(String::from_utf16_lossy(&units))
}

/// Bytes as .NET's Encoding.ASCII decodes them: anything above 0x7F becomes '?'.
fn ascii(bytes: &[u8]) -> String {
    bytes.iter().map(|&b| if b < 0x80 { b as char } else { '?' }).collect()
}

fn read_tail(path: &Path) -> std::io::Result<Vec<u8>> {
    let mut f = File::open(path)?;
    let len = f.metadata()?.len();
    let take = len.min(TAIL_BYTES);
    f.seek(SeekFrom::Start(len - take))?;
    let mut tail = Vec::with_capacity(take as usize);
    f.take(take).read_to_end(&mut tail)?;
    Ok(tail)
}

fn last_index_of(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    haystack.windows(needle.len()).rposition(|w| w == needle)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unescape_like_the_csharp_tray() {
        assert_eq!(unescape(r#"aé\\\"\n"#).as_deref(), Some("aé\\\"\n"));
        assert_eq!(unescape(r"bad\x"), None);
        assert_eq!(unescape(r"cut\u12"), None);
        assert_eq!(unescape(r"★ 😀").as_deref(), Some("★ 😀"));
    }
}
