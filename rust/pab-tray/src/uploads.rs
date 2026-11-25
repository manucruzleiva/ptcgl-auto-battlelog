//! Match uploads to tcgkb and Pokematch (TrayApplicationContext.Tcgkb.cs and .Pokematch.cs,
//! TcgkbService.cs, PokematchService.cs; docs/ARCHITECTURE.md "tcgkb account" and "Pokematch").
//! A tray that was never connected, or has no Pokematch token, sends nothing. Runs on its own
//! thread; the UI gets balloons as events and sends commands.
use crate::{lock, log, session::Session};
use chrono::{DateTime, Utc};
use pab_core::pokematch::{self, PokematchApi};
use pab_core::tcgkb::{self, TcgkbApi, TcgkbConnection};
use pab_core::tokens::{self, PokematchToken, TokenStore, POKEMATCH_ENTROPY, TCGKB_ENTROPY};
use pab_core::uploads::{LogUploader, PokematchUploader, POKEMATCH_STATE_FILE_NAME, SETTLE_TIME};
use std::io;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{channel, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

/// Uploads that failed for a passing reason (offline, tcgkb busy) are retried this often.
const TCGKB_EVERY: Duration = Duration::from_secs(30 * 60);
const POKEMATCH_FIRST_RUN: Duration = Duration::from_secs(60);
const POKEMATCH_EVERY: Duration = Duration::from_secs(10 * 60);
const POKEMATCH_RETRY: Duration = Duration::from_secs(30 * 60);
/// tcgkb answers within 30 s; Pokematch's host sleeps when idle and can take most of a minute to wake.
const TCGKB_TIMEOUT: Duration = Duration::from_secs(30);
const POKEMATCH_TIMEOUT: Duration = Duration::from_secs(90);

/// One API call: method, full URL, headers, body, timeout. Status and body text, Err when nothing came back.
pub type Http = dyn Fn(&str, &str, &[(&str, &str)], Option<&str>, Duration) -> Result<(u16, String), String> + Send + Sync;
/// DPAPI CurrentUser protect or unprotect, with entropy.
pub type Crypt = fn(&[u8], &[u8]) -> io::Result<Vec<u8>>;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Click {
    SignIn,
    SetPokematchToken,
    Consent,
    OpenAppLog,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Notice {
    pub title: String,
    pub text: String,
    pub warning: bool,
    pub click: Option<Click>,
}

/// What the menu shows.
#[derive(Debug, Clone, Default)]
pub struct Status {
    pub tcgkb_last: Option<String>,
    pub pokematch_last: Option<String>,
    pub pokematch_busy: bool,
    pub pokematch_token_rejected: bool,
}

/// The connections, shared by the UI (menu state, token changes) and the worker.
pub struct Clients {
    pub tcgkb: TcgkbConnection<'static>,
    pub tcgkb_uploader: LogUploader,
    pub pokematch_api: PokematchApi<'static>,
    pub pokematch_tokens: TokenStore<PokematchToken>,
    pub pokematch: PokematchUploader,
}

enum Command {
    Tcgkb { announce: bool },
    Pokematch { manual: bool },
    PokematchIn(Duration),
    PokematchStop,
    Disconnect,
}

pub struct Uploads {
    pub clients: Arc<Clients>,
    session: Arc<Session>,
    status: Arc<Mutex<Status>>,
    tx: Sender<Command>,
    stop: Arc<AtomicBool>,
}

struct Worker {
    session: Arc<Session>,
    c: Arc<Clients>,
    status: Arc<Mutex<Status>>,
    emit: Box<dyn Fn(Notice) + Send>,
    stop: Arc<AtomicBool>,
    next_tcgkb: Instant,
    next_pokematch: Option<Instant>,
    tcgkb_consent_logged: bool,
    pokematch_consent_warned: bool,
    pokematch_warned: Option<String>,
}

/// Uploads are on unless the player turned them off (None: never chosen, which means on).
pub fn tcgkb_auto(session: &Session) -> bool {
    session.read(|s| s.tcgkb_auto_upload) != Some(false)
}

fn plural(n: usize, one: &str) -> String {
    format!("{n} {one}{}", if n == 1 { "" } else { "s" })
}

/// The C# tray's DateTime.ToString("o") for UTC, kept in settings.json.
pub fn now_iso() -> String {
    Utc::now().format("%Y-%m-%dT%H:%M:%S%.7fZ").to_string()
}

impl Clients {
    pub fn new(session: &Session, http: Arc<Http>, protect: Crypt, unprotect: Crypt) -> Clients {
        let root = session.paths().root;
        let h = http.clone();
        let tcgkb_api = TcgkbApi::new(move |r: &tcgkb::Request| {
            let bearer = r.bearer.as_ref().map(|b| format!("Bearer {b}"));
            let mut headers = vec![("Accept", "application/json")];
            headers.extend(bearer.as_deref().map(|b| ("Authorization", b)));
            headers.extend(r.content_type.map(|c| ("Content-Type", c)));
            h(r.method, &format!("{}{}", tcgkb::BASE_URL, r.path), &headers, r.body.as_deref(), TCGKB_TIMEOUT)
                .map(|(status, body)| tcgkb::Response { status, body })
        });
        let pokematch_api = PokematchApi::new(move |r: &pokematch::Request| {
            let bearer = format!("Bearer {}", r.token);
            let mut headers = vec![("Accept", "application/json"), ("Authorization", bearer.as_str())];
            if r.body.is_some() {
                headers.push(("Content-Type", "application/json"));
            }
            http(r.method, &format!("{}{}", pokematch::BASE_URL, r.path), &headers, r.body.as_deref(), POKEMATCH_TIMEOUT)
                .map(|(status, body)| pokematch::Response { status, body })
        });
        let store = tokens::tcgkb_store(&root, Box::new(move |d| protect(d, TCGKB_ENTROPY)), Box::new(move |d| unprotect(d, TCGKB_ENTROPY)));
        Clients {
            tcgkb: TcgkbConnection::new(tcgkb_api, store, Utc::now),
            tcgkb_uploader: LogUploader::new(&root),
            pokematch_api,
            pokematch_tokens: tokens::pokematch_store(&root, Box::new(move |d| protect(d, POKEMATCH_ENTROPY)), Box::new(move |d| unprotect(d, POKEMATCH_ENTROPY))),
            pokematch: PokematchUploader::new(root.join(POKEMATCH_STATE_FILE_NAME), Utc::now),
        }
    }

    pub fn has_pokematch_token(&self) -> bool {
        self.pokematch_tokens.load().is_some()
    }
}

impl Uploads {
    /// Starts the worker: tcgkb right away and every 30 minutes, Pokematch a minute after start.
    pub fn start(session: Arc<Session>, clients: Arc<Clients>, emit: impl Fn(Notice) + Send + 'static) -> Uploads {
        let status = Arc::new(Mutex::new(Status::default()));
        let stop = Arc::new(AtomicBool::new(false));
        let (tx, rx) = channel();
        let mut w = Worker {
            session: session.clone(),
            c: clients.clone(),
            status: status.clone(),
            emit: Box::new(emit),
            stop: stop.clone(),
            next_tcgkb: Instant::now(),
            next_pokematch: None,
            tcgkb_consent_logged: false,
            pokematch_consent_warned: false,
            pokematch_warned: None,
        };
        if w.pokematch_auto_on() {
            w.next_pokematch = Some(Instant::now() + POKEMATCH_FIRST_RUN);
        }
        // Not joined on exit: a request can take 90 s, and the state files are written atomically.
        std::thread::spawn(move || loop {
            let now = Instant::now();
            let next = w.next_pokematch.map_or(w.next_tcgkb, |p| p.min(w.next_tcgkb));
            let cmd = rx.recv_timeout(next.saturating_duration_since(now));
            if w.stop.load(Ordering::SeqCst) {
                break;
            }
            match cmd {
                Err(RecvTimeoutError::Disconnected) => break,
                Ok(Command::Tcgkb { announce }) => w.tcgkb(announce),
                Ok(Command::Pokematch { manual }) => w.pokematch(manual),
                Ok(Command::PokematchIn(d)) => w.next_pokematch = Some(Instant::now() + d),
                Ok(Command::PokematchStop) => w.next_pokematch = None,
                Ok(Command::Disconnect) => w.disconnect(),
                Err(RecvTimeoutError::Timeout) => {
                    let now = Instant::now();
                    if now >= w.next_tcgkb {
                        w.next_tcgkb = now + TCGKB_EVERY;
                        w.tcgkb(false);
                    }
                    if w.next_pokematch.is_some_and(|p| Instant::now() >= p) {
                        w.pokematch(false);
                    }
                }
            }
        });
        Uploads { clients, session, status, tx, stop }
    }

    pub fn status(&self) -> Status {
        lock(&self.status).clone()
    }

    /// Sends the pending logs to tcgkb. announce: say what was sent (the player asked).
    pub fn upload_tcgkb(&self, announce: bool) {
        let _ = self.tx.send(Command::Tcgkb { announce });
    }

    pub fn disconnect_tcgkb(&self) {
        let _ = self.tx.send(Command::Disconnect);
    }

    /// A Pokematch run: manual sends every log in the folder, automatic only those since uploads were turned on.
    pub fn upload_pokematch(&self, manual: bool) {
        let _ = self.tx.send(Command::Pokematch { manual });
    }

    pub fn pokematch_in(&self, d: Duration) {
        let _ = self.tx.send(Command::PokematchIn(d));
    }

    pub fn stop_pokematch(&self) {
        let _ = self.tx.send(Command::PokematchStop);
    }

    /// A log was saved: upload it once the plugin is done writing it.
    pub fn saved(&self) {
        if pokematch_auto_on(&self.session, &self.clients) && !lock(&self.status).pokematch_token_rejected {
            self.pokematch_in(SETTLE_TIME + Duration::from_secs(15));
        }
    }

    /// The token was removed: nothing more goes to Pokematch.
    pub fn pokematch_token_removed(&self) {
        let mut s = lock(&self.status);
        s.pokematch_last = None;
        s.pokematch_token_rejected = false;
        drop(s);
        self.stop_pokematch();
    }
}

impl Drop for Uploads {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
    }
}

fn pokematch_auto_on(session: &Session, c: &Clients) -> bool {
    session.read(|s| s.pokematch_auto_upload) && c.pokematch_tokens.file_path().exists()
}

impl Worker {
    fn notify(&self, title: &str, text: &str, warning: bool, click: Option<Click>) {
        (self.emit)(Notice { title: title.into(), text: text.into(), warning, click });
    }

    fn pokematch_auto_on(&self) -> bool {
        pokematch_auto_on(&self.session, &self.c)
    }

    fn tcgkb(&mut self, announce: bool) {
        if !tcgkb_auto(&self.session) || !self.c.tcgkb.is_connected() {
            return;
        }
        // The uploads carry the decklist, which the current disclaimer covers: nothing goes before it is accepted.
        if !self.session.consent() {
            if announce {
                self.notify("Please accept the disclaimer", "Accept it once to upload your matches to tcgkb. Click to read it.", true, Some(Click::Consent));
            }
            if !self.tcgkb_consent_logged {
                log::info("tcgkb uploads wait until the disclaimer is accepted.");
            }
            self.tcgkb_consent_logged = true;
            return;
        }
        self.tcgkb_consent_logged = false;
        let folder = self.session.logs_folder(self.session.game().as_ref());
        let run = match self.c.tcgkb_uploader.sync(&self.c.tcgkb, &folder) {
            Ok(Some(run)) => run,
            Ok(None) => return, // a run was already going
            Err(e) if e.is_auth() => {
                log::info("tcgkb no longer accepts this connection; match uploads stop until the player connects again.");
                self.notify(
                    "Sign in to tcgkb again",
                    "tcgkb no longer accepts this computer's connection, so your matches are not uploaded. Click to sign in.",
                    true,
                    Some(Click::SignIn),
                );
                return;
            }
            Err(e) => return log::error("Uploading matches to tcgkb failed", e),
        };
        let sent = run.uploaded + run.already_there;
        if sent > 0 || run.rejected > 0 || run.stopped_because.is_some() {
            log::info(&format!(
                "tcgkb uploads: {} new, {} already there, {} refused, {} left{}{}.",
                run.uploaded,
                run.already_there,
                run.rejected,
                run.left,
                if run.hit_limit { " (next run follows)" } else { "" },
                run.stopped_because.as_ref().map(|s| format!(" ({s})")).unwrap_or_default()
            ));
        }
        if let Some(n) = &run.newest {
            let decks = match (&n.player_deck, &n.opponent_deck) {
                (Some(p), Some(o)) => format!("{p} vs {o}, "),
                _ => String::new(),
            };
            lock(&self.status).tcgkb_last = Some(format!("{decks}{}", chrono::Local::now().format("%H:%M")));
        }
        if announce {
            if sent > 0 {
                let more = if run.left > 0 { format!("; {} more follow over the next runs.", run.left) } else { ".".into() };
                self.notify("Matches uploaded to tcgkb", &format!("{} new {} sent{more}", run.uploaded, if run.uploaded == 1 { "match" } else { "matches" }), false, None);
            } else if let Some(why) = run.stopped_because.as_ref().filter(|_| run.left > 0) {
                self.notify("Matches not uploaded yet", &format!("{why} The app tries again later."), true, None);
            } else {
                self.notify("tcgkb is up to date", "Every match is on tcgkb.", false, None);
            }
        }
        // The per-run limit: carry on right away rather than in 30 minutes.
        if run.hit_limit {
            self.next_tcgkb = Instant::now();
        }
    }

    fn disconnect(&mut self) {
        let revoked = self.c.tcgkb.disconnect();
        log::info(if revoked { "Disconnected from tcgkb." } else { "Disconnected from tcgkb (tcgkb not reached; the connection was forgotten here)." });
        if revoked {
            self.notify("Disconnected from tcgkb", "This computer is no longer connected.", false, None);
        } else {
            self.notify("Disconnected from tcgkb", "Forgotten on this computer. tcgkb could not be reached: remove this computer in your tcgkb profile.", true, None);
        }
    }

    /// One Pokematch run, until nothing is left or Pokematch stops answering.
    fn pokematch(&mut self, manual: bool) {
        self.next_pokematch = None;
        if !self.session.consent() {
            if !self.pokematch_auto_on() {
                return;
            }
            if !self.pokematch_consent_warned {
                // Said once: otherwise automatic uploads would wait for the disclaimer without a word.
                self.pokematch_consent_warned = true;
                log::info("Pokematch upload waits until the disclaimer is accepted.");
                self.notify("Pokematch upload is waiting", "Accept the disclaimer to upload your battle logs to Pokematch. Click to read it.", true, Some(Click::Consent));
            }
            self.next_pokematch = Some(Instant::now() + POKEMATCH_EVERY);
            return;
        }
        self.pokematch_consent_warned = false;
        if !manual && (!self.pokematch_auto_on() || lock(&self.status).pokematch_token_rejected) {
            return;
        }
        let Some(token) = self.c.pokematch_tokens.load().and_then(|t| t.token) else { return };
        let since = if manual {
            DateTime::<Utc>::MIN_UTC
        } else {
            self.session
                .read(|s| s.pokematch_since_utc.clone())
                .and_then(|s| DateTime::parse_from_rfc3339(&s).ok())
                .map_or(DateTime::<Utc>::MIN_UTC, |d| d.with_timezone(&Utc))
        };
        let folder = self.session.logs_folder(self.session.game().as_ref());

        lock(&self.status).pokematch_busy = true;
        let (mut uploaded, mut updated, mut rejected) = (0, 0, 0);
        let stop = self.stop.clone();
        let cancelled = move || stop.load(Ordering::SeqCst);
        let r = loop {
            let r = self.c.pokematch.sync(&self.c.pokematch_api, &token, &folder, since, &cancelled);
            uploaded += r.uploaded;
            updated += r.updated;
            rejected += r.rejected;
            if !r.more || r.waiting > 0 || r.token_rejected || cancelled() {
                break r;
            }
        };
        lock(&self.status).pokematch_busy = false;
        if cancelled() {
            return;
        }

        let sent = uploaded + updated;
        if manual || sent + rejected > 0 || r.error.is_some() {
            log::info(&format!(
                "Pokematch: {uploaded} uploaded, {updated} updated, {rejected} refused{}.",
                r.error.as_ref().map(|e| format!(" ({e})")).unwrap_or_default()
            ));
        }
        if sent > 0 {
            lock(&self.status).pokematch_last = Some(format!("Last upload: {}, {}", plural(sent, "log"), chrono::Local::now().format("%H:%M")));
        }
        let error = r.error.clone().unwrap_or_default();
        if r.token_rejected {
            lock(&self.status).pokematch_token_rejected = true;
            self.notify("Pokematch refused the API token", &format!("{error} Click to set a new one."), true, Some(Click::SetPokematchToken));
            return;
        }
        if manual {
            let refused = if rejected > 0 { format!(" {rejected} refused: {error}") } else { String::new() };
            let waiting = if r.waiting > 0 {
                format!(" Pokematch did not answer; the rest {}", if self.pokematch_auto_on() { "is sent later." } else { "can be sent later." })
            } else {
                String::new()
            };
            self.notify("Pokematch upload", &format!("{} sent to Pokematch.{refused}{waiting}", plural(sent, "battle log")), rejected + r.waiting > 0, None);
        } else if rejected > 0 && r.error != self.pokematch_warned {
            // Once per kind of refusal, not after every match.
            self.pokematch_warned = r.error.clone();
            self.notify("Pokematch refused a battle log", &format!("{error} Details are in the app log."), true, Some(Click::OpenAppLog));
        }
        if self.pokematch_auto_on() {
            self.next_pokematch = Some(Instant::now() + if r.waiting > 0 { POKEMATCH_RETRY } else { POKEMATCH_EVERY });
        }
    }
}
