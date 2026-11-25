//! The two upload queues (LogUploader.cs, PokematchUploader.cs, PtcglAccountScanner.cs): every
//! tracked match goes to tcgkb once, and finished matches go to Pokematch once (updated once if the
//! plugin later corrects the result). Their state files are the C# tray's, byte for byte, so an
//! upgrade sends nothing twice.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, SystemTime};

use chrono::{DateTime, Local, NaiveDateTime, TimeZone, Utc};
use serde::{Deserialize, Serialize};

use crate::hashing::{sha256, to_hex};
use crate::log_file_name;
use crate::pokematch::{self, PokematchApi};
use crate::seal_summary::SealSummary;
use crate::tcgkb::{self, TcgkbConnection, TcgkbError, UploadResponse};
use crate::tokens::net_date;
use crate::{file_ops, net_json};

/// A battle log is a few dozen KB; a bigger file is never sent as evidence.
pub const MAX_EVIDENCE_BYTES: u64 = tcgkb::MAX_EVIDENCE_BYTES;
/// The client's name for Build & Battle, as a seal's queue or format.
pub const BUILD_AND_BATTLE: &str = "BuildAndBattle";

/// A tracked match (a match id and a result other than EXPORTED) whose files are small enough to send.
pub fn is_evidence(s: &SealSummary) -> bool {
    if s.match_id.as_deref().is_none_or(str::is_empty) || s.result.as_deref().is_none_or(|r| r == "EXPORTED") || s.player_name.as_deref().is_none_or(str::is_empty) {
        return false;
    }
    let small = |p: &Path| std::fs::metadata(p).is_ok_and(|m| m.len() <= MAX_EVIDENCE_BYTES);
    small(&s.log_file) && s.seal_file.as_deref().is_none_or(small)
}

/// Build & Battle matches are never sent to tcgkb (it keeps no record of them); the local log stays.
pub fn is_build_and_battle(s: &SealSummary) -> bool {
    s.queue.as_deref() == Some(BUILD_AND_BATTLE) || s.format.as_deref() == Some(BUILD_AND_BATTLE)
}

fn txt_files(folder: &Path) -> Vec<PathBuf> {
    let Ok(dir) = std::fs::read_dir(folder) else { return Vec::new() };
    dir.flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("txt")) && p.is_file())
        .collect()
}

fn load_json<T: for<'de> Deserialize<'de> + Default>(path: &Path) -> T {
    file_ops::try_read_small_text(path, 16 * 1024 * 1024)
        .filter(|t| t.trim_start().starts_with('{'))
        .and_then(|t| serde_json::from_str(&t).ok())
        .unwrap_or_default()
}

// ---------------------------------------------------------------------------------------- tcgkb

pub const TCGKB_STATE_FILE_NAME: &str = "tcgkb-uploads.json";
pub const TCGKB_MAX_PER_RUN: usize = 50;
/// Keys kept in the state file: far more than a player's logs folder holds.
const MAX_KEYS: usize = 50_000;

/// tcgkb-uploads.json: the sealed logs already sent, by key, so each is sent once.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct LogUploadState {
    /// Accepted by tcgkb (new or already there).
    pub uploaded: Vec<String>,
    /// Refused as invalid: sending it again would be refused again.
    pub rejected: Vec<String>,
    /// deckSha256 of the decks tcgkb has: later matches with one send only the seal's hash.
    pub decks: Vec<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UploadRun {
    pub uploaded: usize,
    pub already_there: usize,
    pub rejected: usize,
    /// Eligible logs left for a later run (the per-run limit, or a stop).
    pub left: usize,
    /// The run stopped at the limit with logs left: the caller may run again right away.
    pub hit_limit: bool,
    /// Why the run stopped early (tcgkb unreachable, busy...).
    pub stopped_because: Option<String>,
    /// tcgkb's answer to the newest log sent in this run.
    pub newest: Option<UploadResponse>,
}

/// One sealed log as tcgkb stores it: match, seat and sealing key.
pub fn tcgkb_key(s: &SealSummary) -> String {
    format!("{}|{}|{}", s.match_id.as_deref().unwrap_or(""), s.player_account_hash.as_deref().unwrap_or(""), s.key_id)
}

/// The folder's sealed logs of a tracked match, not Build & Battle, not in done, newest first.
pub fn tcgkb_pending(logs_folder: &Path, done: &HashSet<String>) -> Vec<SealSummary> {
    let mut found: Vec<SealSummary> = txt_files(logs_folder)
        .iter()
        .filter(|f| log_file_name::parse(&f.to_string_lossy()).is_some())
        .filter_map(|f| SealSummary::try_read(f))
        .filter(|s| s.player_account_hash.is_some() && is_evidence(s) && !is_build_and_battle(s) && !done.contains(&tcgkb_key(s)))
        .collect();
    found.sort_by(|a, b| b.sealed_utc.cmp(&a.sealed_utc).then_with(|| b.log_file.cmp(&a.log_file)));
    found
}

pub struct LogUploader {
    state_path: PathBuf,
    running: Mutex<()>,
}

impl LogUploader {
    pub fn new(state_folder: &Path) -> Self {
        LogUploader { state_path: state_folder.join(TCGKB_STATE_FILE_NAME), running: Mutex::new(()) }
    }

    pub fn state_path(&self) -> &Path {
        &self.state_path
    }

    /// The saved state, or an empty one when missing or unreadable (logs are then offered again: harmless).
    pub fn load_state(&self) -> LogUploadState {
        load_json(&self.state_path)
    }

    /// Uploads what is pending. Ok(None) when a run is already going. Err only when the connection
    /// is gone; other tcgkb errors end the run early (stopped_because).
    pub fn sync(&self, connection: &TcgkbConnection, logs_folder: &Path) -> Result<Option<UploadRun>, TcgkbError> {
        let Ok(_running) = self.running.try_lock() else { return Ok(None) };
        let mut run = UploadRun::default();
        let mut state = self.load_state();
        let done: HashSet<String> = state.uploaded.iter().chain(&state.rejected).cloned().collect();
        let pending = tcgkb_pending(logs_folder, &done);
        // Where the run stopped: that log and the ones after it are left for later.
        let mut stop = pending.len();
        for (i, s) in pending.iter().enumerate() {
            if i >= TCGKB_MAX_PER_RUN {
                run.hit_limit = true;
                stop = i;
                break;
            }
            let known = s.deck_sha256.as_ref().is_some_and(|d| state.decks.contains(d));
            // moved or deleted since the scan
            let Ok(log) = tcgkb::build_evidence(s, !known) else { continue };
            let outcome = connection.upload_log(&log);
            match outcome {
                Ok(r) => {
                    if r.added { run.uploaded += 1 } else { run.already_there += 1 }
                    state.uploaded.push(tcgkb_key(s));
                    // Each deck is sent once; if tcgkb turns out not to have it any more, the next match sends it again.
                    if let Some(d) = &s.deck_sha256 {
                        state.decks.retain(|x| x != d);
                        if r.deck_ref.is_some() {
                            state.decks.push(d.clone());
                        }
                    }
                    run.newest.get_or_insert(r);
                }
                Err(e) if e.is_auth() => {
                    self.save_state(&mut state);
                    return Err(e);
                }
                // Refused for good: a broken log, or a PTCGL account that is another tcgkb user's.
                Err(e) if e.code == "invalid_log" || e.code == "claimed_by_other" => {
                    run.rejected += 1;
                    state.rejected.push(tcgkb_key(s));
                }
                Err(e) if e.code == "not_claimed" => {} // a tcgkb from before automatic claims: kept for later
                Err(e) => {
                    run.stopped_because = Some(e.message);
                    self.save_state(&mut state);
                    stop = i;
                    break;
                }
            }
            self.save_state(&mut state);
        }
        run.left = pending.len() - stop;
        Ok(Some(run))
    }

    fn save_state(&self, state: &mut LogUploadState) {
        for list in [&mut state.uploaded, &mut state.rejected, &mut state.decks] {
            if list.len() > MAX_KEYS {
                list.drain(..list.len() - MAX_KEYS);
            }
        }
        // not saved: the same logs are offered again next run, and tcgkb takes them again
        let _ = file_ops::write_atomic(&self.state_path, net_json::to_string(state).as_bytes());
    }
}

// ------------------------------------------------------------------------------------ Pokematch

pub const POKEMATCH_STATE_FILE_NAME: &str = "pokematch-uploads.json";
/// The plugin may rewrite a log shortly after the match.
pub const SETTLE_TIME: Duration = Duration::from_secs(120);
pub const POKEMATCH_MAX_LOG_BYTES: u64 = 1024 * 1024;
pub const POKEMATCH_MAX_PER_RUN: usize = 25;
pub const UPLOADED: &str = "uploaded";
pub const REJECTED: &str = "rejected";
pub const DELETED: &str = "deleted";

/// What happened to one battle log on Pokematch.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct PokematchUpload {
    /// "match:" + the seal's match id, else "file:" + the file name.
    pub key: Option<String>,
    pub status: Option<String>,
    /// Pokematch's id for the match; None when its answer named none (then it is never updated).
    pub id: Option<String>,
    /// SHA-256 of the JSON last sent: when it changes (a corrected result), it is sent again, once.
    pub sha256: Option<String>,
    pub file: Option<String>,
    #[serde(with = "net_date")]
    pub utc: DateTime<Utc>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct PokematchUploadState {
    pub uploads: Vec<PokematchUpload>,
}

impl PokematchUploadState {
    /// Never fails: a missing or corrupt file is an empty state (Pokematch's own ids are lost, nothing else).
    pub fn load(path: &Path) -> Self {
        let mut s: PokematchUploadState = load_json(path);
        s.uploads.retain(|u| u.key.as_deref().is_some_and(|k| !k.is_empty()));
        s
    }

    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        file_ops::write_atomic(path, net_json::to_string(self).as_bytes())
    }

    pub fn find(&self, key: &str) -> Option<usize> {
        self.uploads.iter().position(|u| u.key.as_deref() == Some(key))
    }
}

/// A battle log ready to go to Pokematch.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PokematchCandidate {
    pub log_file: PathBuf,
    pub key: String,
    pub sha256: String,
    pub matched: pokematch::Match,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PokematchSyncResult {
    pub uploaded: usize,
    pub updated: usize,
    pub rejected: usize,
    pub waiting: usize,
    /// Pokematch refused the token: nothing more is sent until the player sets a new one.
    pub token_rejected: bool,
    /// The newest error worth telling the player about.
    pub error: Option<String>,
    /// Logs left for a later run.
    pub more: bool,
}

/// Our tag, then the seal's queue and format, lowercase as Pokematch keeps tags.
pub fn pokematch_tags(seal: Option<&SealSummary>) -> Vec<String> {
    let mut tags = vec![pokematch::TAG.to_string()];
    for t in [seal.and_then(|s| s.queue.as_deref()), seal.and_then(|s| s.format.as_deref())].into_iter().flatten() {
        let tag = t.trim().to_lowercase();
        if !tag.is_empty() && tag.chars().count() <= pokematch::MAX_TAG_LENGTH && !tags.contains(&tag) {
            tags.push(tag);
        }
    }
    tags
}

fn local_to_utc(t: NaiveDateTime) -> DateTime<Utc> {
    Local.from_local_datetime(&t).earliest().map_or_else(|| t.and_utc(), |l| l.with_timezone(&Utc))
}

/// A battle log as Pokematch takes it; None when it is not one of the plugin's logs.
pub fn pokematch_read(log_file: &Path) -> Option<PokematchCandidate> {
    let name = log_file_name::parse(&log_file.to_string_lossy())?;
    if !log_file.is_file() {
        return None;
    }
    let seal = SealSummary::try_read(log_file);
    let date = match seal.as_ref().and_then(|s| s.ended_utc) {
        Some(ended) => ended.with_timezone(&Local).date_naive(),
        None => name.local_time.date(),
    };
    let result = seal.as_ref().and_then(|s| s.result.as_deref()).unwrap_or(name.result);
    let deck = seal.as_ref().and_then(|s| s.deck.as_deref()).map(str::trim).filter(|d| !d.is_empty()).unwrap_or(pokematch::UNKNOWN);
    let matched = pokematch::Match {
        active_deck: Some(pokematch::Deck { name: Some(deck.to_string()) }),
        // The client does not tell the player the opponent's archetype; the player can fill it in on Pokematch.
        opponent_deck: Some(pokematch::UNKNOWN.into()),
        format: Some("Bo1".into()),
        games: Some(vec![pokematch::Game { result: Some(if result == "VICTORY" { "win" } else { "loss" }.into()) }]),
        date: Some(date.format("%Y-%m-%d").to_string()),
        notes: Some(format!("Added by {}.", pokematch::SOURCE)),
        tags: Some(pokematch_tags(seal.as_ref())),
    };
    let file_name = log_file.file_name()?.to_string_lossy().into_owned();
    Some(PokematchCandidate {
        log_file: log_file.to_path_buf(),
        key: match seal.as_ref().and_then(|s| s.match_id.as_deref()).filter(|m| !m.is_empty()) {
            Some(m) => format!("match:{m}"),
            None => format!("file:{file_name}"),
        },
        sha256: to_hex(&sha256(net_json::to_string(&matched).as_bytes())),
        matched,
    })
}

pub struct PokematchUploader {
    state_file: PathBuf,
    now: Box<dyn Fn() -> DateTime<Utc> + Send + Sync>,
    gate: Mutex<()>,
}

impl PokematchUploader {
    pub fn new(state_file: PathBuf, now: impl Fn() -> DateTime<Utc> + Send + Sync + 'static) -> Self {
        PokematchUploader { state_file, now: Box::new(now), gate: Mutex::new(()) }
    }

    pub fn state_file(&self) -> &Path {
        &self.state_file
    }

    /// The battle logs a run would look at: finished matches (VICTORY, DEFEAT) of at least since_utc
    /// that the plugin has stopped writing, oldest first.
    pub fn eligible(&self, logs_folder: &Path, since_utc: DateTime<Utc>) -> Vec<PathBuf> {
        let settled: SystemTime = ((self.now)() - chrono::Duration::from_std(SETTLE_TIME).expect("settle")).into();
        let mut found: Vec<(PathBuf, DateTime<Utc>)> = txt_files(logs_folder)
            .into_iter()
            .filter_map(|f| {
                let name = log_file_name::parse(&f.to_string_lossy())?;
                if name.result != "VICTORY" && name.result != "DEFEAT" {
                    return None;
                }
                let utc = local_to_utc(name.local_time);
                let meta = std::fs::metadata(&f).ok()?;
                (utc >= since_utc && meta.modified().ok()? <= settled && meta.len() <= POKEMATCH_MAX_LOG_BYTES).then_some((f, utc))
            })
            .collect();
        found.sort_by(|a, b| a.1.cmp(&b.1).then_with(|| a.0.cmp(&b.0)));
        found.into_iter().map(|(f, _)| f).collect()
    }

    /// How many logs of at least since_utc Pokematch does not have yet (for "Upload past logs...").
    pub fn count_new(&self, logs_folder: &Path, since_utc: DateTime<Utc>) -> usize {
        let state = PokematchUploadState::load(&self.state_file);
        self.eligible(logs_folder, since_utc).iter().filter_map(|f| pokematch_read(f)).filter(|c| state.find(&c.key).is_none()).count()
    }

    /// Sends what Pokematch is missing, at most POKEMATCH_MAX_PER_RUN logs. One run at a time: a
    /// second caller waits. `cancelled` is polled between logs.
    pub fn sync(&self, api: &PokematchApi, token: &str, logs_folder: &Path, since_utc: DateTime<Utc>, cancelled: &dyn Fn() -> bool) -> PokematchSyncResult {
        let mut result = PokematchSyncResult::default();
        let _gate = self.gate.lock().unwrap_or_else(|p| p.into_inner());
        let mut state = PokematchUploadState::load(&self.state_file);
        let mut sent = 0;
        for file in self.eligible(logs_folder, since_utc) {
            if cancelled() {
                break;
            }
            let Some(c) = pokematch_read(&file) else { continue };
            let known = state.find(&c.key);
            if let Some(k) = known.map(|i| &state.uploads[i]) {
                if k.status.as_deref() == Some(DELETED) || k.sha256.as_deref() == Some(c.sha256.as_str()) {
                    continue;
                }
                if k.status.as_deref() == Some(UPLOADED) && k.id.is_none() {
                    continue; // cannot be replaced
                }
            }
            if sent >= POKEMATCH_MAX_PER_RUN {
                result.more = true;
                break;
            }
            sent += 1;
            let mut entry = known.map(|i| state.uploads[i].clone()).unwrap_or_else(|| PokematchUpload { key: Some(c.key.clone()), ..Default::default() });
            let replacing = entry.status.as_deref() == Some(UPLOADED);
            let outcome = if replacing {
                api.update(token, entry.id.as_deref().unwrap_or(""), &c.matched).map(|still_there| {
                    if still_there { result.updated += 1 } else { entry.status = Some(DELETED.into()) }
                })
            } else {
                api.create(token, &c.matched).map(|id| {
                    entry.id = id;
                    entry.status = Some(UPLOADED.into());
                    result.uploaded += 1;
                })
            };
            match outcome {
                Ok(()) => entry.error = None,
                Err(e) if e.auth => {
                    result.token_rejected = true;
                    result.error = Some(e.message);
                    break;
                }
                Err(e) if e.transient() => {
                    // Pokematch is down or busy: the next run tries again
                    result.waiting += 1;
                    result.more = true;
                    result.error = Some(e.message);
                    break;
                }
                Err(e) => {
                    // Refused for this log: not sent again until what it would send changes.
                    entry.status.get_or_insert_with(|| REJECTED.into());
                    entry.error = Some(e.message.clone());
                    result.rejected += 1;
                    result.error = Some(e.message);
                }
            }
            entry.sha256 = Some(c.sha256.clone());
            entry.file = c.log_file.file_name().map(|n| n.to_string_lossy().into_owned());
            entry.utc = (self.now)();
            match known {
                Some(i) => state.uploads[i] = entry,
                None => state.uploads.push(entry),
            }
            let _ = state.save(&self.state_file);
        }
        result
    }
}
