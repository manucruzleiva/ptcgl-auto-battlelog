//! The status and settings window (StatusWindow.cs), in egui: the menu's items laid out in pages,
//! plus a home page with the player's matches and their place on the tcgkb ladder. The tray builds
//! a snapshot (the same items as the menu) and hands it to the window thread; a click hands the
//! item's action back, so the menu and the window cannot disagree. The window keeps no data.
use super::menu::{Action, Builder};
use super::{Event, Poster, Tray};
use crate::app::accounts::AccountAction;
use crate::win::popup::Item;
use crate::win::shell;
use crate::{lock, log, status_icons, VERSION};
use chrono::{Duration as Days, Local, NaiveDateTime};
use eframe::egui::{self, Color32, CornerRadius, Frame, Margin, RichText, Sense, Stroke};
use pab_core::daemon_monitor::TrayHealth;
use pab_core::ladder_status::{LadderStatus, DISPUTED};
use pab_core::log_file_name;
use pab_core::tcgkb::{Account, Accounts};
use std::cell::{Cell, RefCell};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime};

const MAX_LISTED_MATCHES: usize = 100;
const LADDER_URL: &str = "https://tcgkb.app/leaderboard";
const VERIFIED: &str = "verified";

// StatusWindow.Palette: dark, with tcgkb's yellow as the accent.
const SURFACE: Color32 = Color32::from_rgb(0x12, 0x16, 0x1C);
const CARD: Color32 = Color32::from_rgb(0x17, 0x1C, 0x23);
const RAISE: Color32 = Color32::from_rgb(0x1E, 0x24, 0x2D);
const BORDER: Color32 = Color32::from_rgb(0x23, 0x2A, 0x34);
const TEXT: Color32 = Color32::from_rgb(0xE8, 0xEC, 0xF2);
const MUTED: Color32 = Color32::from_rgb(0xC9, 0xD1, 0xDB);
const SECONDARY: Color32 = Color32::from_rgb(0x9A, 0xA4, 0xB2);
const ACCENT: Color32 = Color32::from_rgb(0xF5, 0xC5, 0x18);
const ON_ACCENT: Color32 = Color32::from_rgb(0x14, 0x17, 0x1C);
const TRACK: Color32 = Color32::from_rgb(0x2C, 0x34, 0x40);
const GOOD: Color32 = Color32::from_rgb(0x56, 0xD3, 0x64);
const BAD: Color32 = Color32::from_rgb(0xF4, 0x70, 0x67);

/// A battle log in the logs folder, from its file name.
#[derive(Clone)]
pub(super) struct Match {
    result: &'static str,
    opponent: Option<String>,
    local_time: NaiveDateTime,
    path: PathBuf,
}

/// What the window shows, made on the tray thread.
struct Snapshot {
    version: String,
    dot: [u8; 3],
    status: String,
    lines: Vec<String>,
    /// Disclaimer, game folder, open logs folder, open last log; the first id is in the accent colour.
    home: Vec<Item>,
    primary: u32,
    update: Option<String>,
    /// Signed in to tcgkb (the name may be empty).
    user: Option<String>,
    sign_in: Option<u32>,
    auto_upload: Option<(u32, bool)>,
    last_upload: Option<String>,
    me: Option<Accounts>,
    matches: Arc<Vec<Match>>,
    tcgkb: Item,
    settings: Vec<Item>,
    updates: Vec<Item>,
    help: Vec<Item>,
    /// By item id - 1.
    actions: Vec<Action>,
}

/// The window's side of an open window.
struct Shared {
    snap: Mutex<Arc<Snapshot>>,
    ctx: Mutex<Option<egui::Context>>,
    close: AtomicBool,
    focus: AtomicBool,
}

impl Shared {
    fn repaint(&self) {
        if let Some(c) = lock(&self.ctx).as_ref() {
            c.request_repaint();
        }
    }
}

/// A listed folder, its time and its matches.
type Listed = (PathBuf, SystemTime, Arc<Vec<Match>>);

/// The tray's side.
#[derive(Default)]
pub(super) struct WindowState {
    open: RefCell<Option<Arc<Shared>>>,
    /// The listed folder and its time: a file added, renamed or deleted changes the folder's time.
    matches: RefCell<Option<Listed>>,
    me: RefCell<Option<Accounts>>,
    me_at: Cell<Option<Instant>>,
    me_busy: Cell<bool>,
}

impl Tray {
    pub(super) fn show_window(&self) {
        if !self.session.consent() {
            return self.ask_consent();
        }
        if let Some(w) = self.win.open.borrow().as_ref() {
            w.focus.store(true, Ordering::SeqCst);
            return w.repaint();
        }
        let shared = Arc::new(Shared { snap: Mutex::new(Arc::new(self.snapshot())), ctx: Mutex::default(), close: AtomicBool::new(false), focus: AtomicBool::new(false) });
        let s = shared.clone();
        let opened = self.window(move |p| {
            let mut view = View { page: Page::Home, filter: None, focused: false, opened: Some(Instant::now()), poster: p.clone() };
            crate::ui::show_main([880.0, 600.0], [820.0, 500.0], move |ui| view.draw(ui, &s));
            p.post(Event::Run(Box::new(|t: &Tray| drop(t.win.open.borrow_mut().take()))));
        });
        if opened {
            *self.win.open.borrow_mut() = Some(shared);
            self.refresh_standing(false);
        }
    }

    /// A window may open now; with the status window open it closes for the other and comes back after.
    pub(super) fn can_open_window(&self) -> bool {
        !crate::ui::busy() || self.win.open.borrow().is_some()
    }

    /// Closes the status window to make way for another; true when it was open.
    pub(super) fn make_way(&self) -> bool {
        let open = self.win.open.borrow();
        if let Some(w) = open.as_ref() {
            w.close.store(true, Ordering::SeqCst);
            w.repaint();
        }
        open.is_some()
    }

    /// Shows the current state in an open window. Cheap enough for every event.
    pub(super) fn refresh_window(&self) {
        let Some(w) = self.win.open.borrow().clone() else { return };
        *lock(&w.snap) = Arc::new(self.snapshot());
        w.repaint();
    }

    /// The PTCGL accounts and ladder from tcgkb, at most once a minute unless forced.
    // shortcut: not refreshed after an upload run like the C# tray; the window asks again when it gets focus.
    pub(super) fn refresh_standing(&self, force: bool) {
        let w = &self.win;
        if !self.uploads.clients.tcgkb.is_connected() {
            *w.me.borrow_mut() = None;
            return;
        }
        if w.me_busy.get() || (!force && w.me_at.get().is_some_and(|t| t.elapsed() < Duration::from_secs(60))) {
            return;
        }
        w.me_busy.set(true);
        let (clients, poster) = (self.uploads.clients.clone(), self.poster.clone());
        std::thread::spawn(move || {
            let r = clients.tcgkb.get_accounts();
            poster.post(Event::Run(Box::new(move |t: &Tray| {
                t.win.me_busy.set(false);
                match r {
                    Ok(me) => {
                        *t.win.me.borrow_mut() = Some(me);
                        t.win.me_at.set(Some(Instant::now()));
                    }
                    Err(e) if e.is_auth() => *t.win.me.borrow_mut() = None,
                    Err(e) => log::info(&format!("tcgkb accounts not read: {e}")),
                }
                t.refresh_window();
            })));
        });
    }

    fn snapshot(&self) -> Snapshot {
        let s = &self.session;
        let mut b = Builder::default();
        let p = self.menu_parts(&mut b);
        let id_of = |b: &Builder, a: Action| b.actions.iter().position(|x| *x == a).map(|i| i as u32 + 1);
        let health = self.last.borrow().as_ref().map_or(TrayHealth::Idle, |r| r.decision.health);
        let status = self.last.borrow().as_ref().map_or_else(|| "Starting...".to_string(), |r| r.decision.summary.clone());
        let connected = self.uploads.clients.tcgkb.is_connected();
        let auto = crate::uploads::tcgkb_auto(s);
        let mut home: Vec<Item> = p.consent.into_iter().chain(p.choose_game).collect();
        let primary = item_id(&p.open_logs);
        home.extend([p.open_logs, p.open_last]);
        let [tcgkb, pokematch] = p.accounts;
        let mut settings = p.settings;
        settings.extend([Item::Separator, pokematch]);
        let update = p.update.as_ref().map(|u| match u {
            Item::Sub { text, .. } => text.clone(),
            _ => String::new(),
        });
        let mut updates: Vec<Item> = p.update.into_iter().collect();
        updates.extend(p.updates);
        Snapshot {
            version: format!("Version {VERSION}{}", if s.portable() { " · portable" } else { "" }),
            dot: status_icons::dot_color(health).unwrap_or([0x9E, 0x9E, 0x9E]),
            status,
            lines: p.lines,
            home,
            primary,
            update,
            user: connected.then(|| self.uploads.clients.tcgkb.user_name().unwrap_or_default()),
            sign_in: id_of(&b, Action::Account(AccountAction::TcgkbSignIn)),
            auto_upload: id_of(&b, Action::Account(AccountAction::TcgkbAuto)).map(|id| (id, auto)),
            last_upload: self.uploads.status().tcgkb_last.filter(|_| auto),
            me: self.win.me.borrow().clone(),
            matches: self.local_matches(),
            tcgkb,
            settings,
            updates,
            help: p.help,
            actions: b.actions,
        }
    }

    /// The battle logs in the logs folder, newest first; read again only when the folder changed.
    fn local_matches(&self) -> Arc<Vec<Match>> {
        let folder = self.session.logs_folder(self.session.game().as_ref());
        let Ok(time) = std::fs::metadata(&folder).and_then(|m| m.modified()) else { return Arc::default() };
        if let Some((f, t, m)) = self.win.matches.borrow().as_ref() {
            if *f == folder && *t == time {
                return m.clone();
            }
        }
        let mut list: Vec<Match> = match std::fs::read_dir(&folder) {
            Ok(dir) => dir
                .flatten()
                .filter_map(|e| {
                    let path = e.path();
                    let n = log_file_name::parse(path.file_name()?.to_str()?)?;
                    Some(Match { result: n.result, opponent: n.opponent, local_time: n.local_time, path })
                })
                .collect(),
            Err(e) => {
                log::error("Could not list the battle logs", e);
                return Arc::default();
            }
        };
        list.sort_by_key(|m| std::cmp::Reverse(m.local_time));
        let list = Arc::new(list);
        *self.win.matches.borrow_mut() = Some((folder, time, list.clone()));
        list
    }
}

fn item_id(item: &Item) -> u32 {
    match item {
        Item::Action { id, .. } => *id,
        _ => 0,
    }
}

/// The items shown as switches (StatusWindow's ToggleTag).
fn is_toggle(a: Action) -> bool {
    matches!(
        a,
        Action::ToggleNotify | Action::ToggleStartWithWindows | Action::ToggleAutoUpdate | Action::Account(AccountAction::TcgkbAuto) | Action::Account(AccountAction::PokematchAuto)
    )
}

/// One choice out of several (Battle log seal, Update channel): every item an option, one ticked.
fn is_choice(items: &[Item]) -> bool {
    items.len() > 1
        && items.iter().all(|i| matches!(i, Item::Action { id, .. } if *id != 0))
        && items.iter().filter(|i| matches!(i, Item::Action { checked: true, .. })).count() == 1
}

/// "2 min ago", "3 h ago", "Yesterday", "Monday", "12 Oct".
fn when(local: NaiveDateTime, now: NaiveDateTime) -> String {
    let ago = now - local;
    if ago < Days::minutes(1) {
        "Just now".into()
    } else if ago < Days::hours(1) {
        format!("{} min ago", ago.num_minutes())
    } else if local.date() == now.date() {
        format!("{} h ago", ago.num_hours())
    } else if Some(local.date()) == now.date().pred_opt() {
        "Yesterday".into()
    } else if ago < Days::days(6) {
        local.format("%A").to_string()
    } else if local.format("%Y").to_string() == now.format("%Y").to_string() {
        local.format("%-d %b").to_string()
    } else {
        local.format("%-d %b %Y").to_string()
    }
}

/// The last seven days: matches, win rate (None without a decided match), victories.
fn week(matches: &[Match], now: NaiveDateTime) -> (usize, Option<i64>, usize) {
    let since = now - Days::days(7);
    let week: Vec<&Match> = matches.iter().filter(|m| m.local_time >= since).collect();
    let won = week.iter().filter(|m| m.result == "VICTORY").count();
    let lost = week.iter().filter(|m| m.result == "DEFEAT").count();
    let rate = (won + lost > 0).then(|| (100.0 * won as f64 / (won + lost) as f64).round() as i64);
    (week.len(), rate, won)
}

fn result_text(result: &str) -> (&'static str, Color32) {
    match result {
        "VICTORY" => ("Victory", GOOD),
        "DEFEAT" => ("Defeat", BAD),
        "NO-RESULT" => ("No result", SECONDARY),
        _ => ("Exported", SECONDARY),
    }
}

fn status_word(a: &Account) -> &'static str {
    match a.status.as_deref() {
        Some(VERIFIED) => "verified",
        Some(DISPUTED) => "disputed",
        _ => "not verified",
    }
}

#[derive(Clone, Copy, PartialEq)]
enum Page {
    Home,
    Matches,
    Tcgkb,
    Settings,
    Updates,
    Help,
}

/// The window thread's state: which page, which filter.
struct View {
    page: Page,
    /// None: all results; else VICTORY or DEFEAT.
    filter: Option<&'static str>,
    focused: bool,
    opened: Option<Instant>,
    poster: Poster,
}

impl View {
    fn run(&self, f: impl FnOnce(&Tray) + Send + 'static) {
        self.poster.post(Event::Run(Box::new(f)));
    }

    /// Runs an item's action on the tray thread, then shows what it changed.
    fn click(&self, snap: &Snapshot, id: u32) {
        if let Some(a) = snap.actions.get(id as usize - 1).copied() {
            self.run(move |t| {
                t.run(a);
                t.refresh_window();
            });
        }
    }

    fn draw(&mut self, ui: &mut egui::Ui, w: &Shared) {
        let ctx = ui.ctx().clone();
        lock(&w.ctx).get_or_insert_with(|| {
            ctx.style_mut_of(egui::Theme::Dark, |s| s.visuals.override_text_color = Some(TEXT));
            ctx.clone()
        });
        if w.close.load(Ordering::SeqCst) || ui.input(|i| i.key_pressed(egui::Key::Escape)) {
            return crate::ui::close(ui);
        }
        if w.focus.swap(false, Ordering::SeqCst) {
            ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(false));
            ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
        }
        if let Some(t) = self.opened.take() {
            let ms = t.elapsed().as_millis();
            self.run(move |_| log::info(&format!("Status window opened in {ms} ms.")));
        }
        let focused = ui.input(|i| i.viewport().focused.unwrap_or(false));
        if focused && !self.focused {
            self.run(|t| t.refresh_standing(false));
        }
        self.focused = focused;
        ctx.request_repaint_after(Duration::from_secs(30)); // the matches' "2 min ago"

        let snap = lock(&w.snap).clone();
        egui::Panel::left("nav")
            .exact_size(190.0)
            .resizable(false)
            .show_separator_line(false)
            .frame(Frame::new().fill(SURFACE).inner_margin(Margin { left: 10, right: 8, top: 12, bottom: 8 }))
            .show(ui, |ui| self.nav(ui, &snap));
        egui::CentralPanel::default()
            .frame(Frame::new().fill(SURFACE).inner_margin(Margin { left: 8, right: 20, top: 12, bottom: 8 }))
            .show(ui, |ui| {
                egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| match self.page {
                    Page::Home => self.home(ui, &snap),
                    Page::Matches => self.matches(ui, &snap),
                    Page::Tcgkb => self.tcgkb(ui, &snap),
                    Page::Settings => self.items_page(ui, &snap, "Settings", &snap.settings),
                    Page::Updates => self.items_page(ui, &snap, "Updates", &snap.updates),
                    Page::Help => self.items_page(ui, &snap, "Help", &snap.help),
                });
            });
    }

    fn nav(&mut self, ui: &mut egui::Ui, snap: &Snapshot) {
        ui.spacing_mut().item_spacing.y = 2.0;
        for (name, page) in [("Home", Page::Home), ("Matches", Page::Matches), ("tcgkb", Page::Tcgkb), ("Settings", Page::Settings), ("Updates", Page::Updates), ("Help", Page::Help)] {
            let b = egui::Button::selectable(self.page == page, RichText::new(name).size(15.0)).min_size(egui::vec2(ui.available_width(), 36.0));
            if ui.add(b).clicked() {
                self.page = page;
            }
        }
        ui.with_layout(egui::Layout::bottom_up(egui::Align::Min), |ui| {
            if let Some(u) = &snap.update {
                if ui.link(RichText::new(u).size(12.0).color(ACCENT)).clicked() {
                    self.page = Page::Updates;
                }
            }
            ui.label(RichText::new(&snap.version).size(12.0).color(SECONDARY));
        });
    }

    // ---- Home ----------------------------------------------------------------------------

    fn home(&mut self, ui: &mut egui::Ui, snap: &Snapshot) {
        card(ui, |ui| {
            ui.horizontal_top(|ui| {
                let (rect, _) = ui.allocate_exact_size(egui::vec2(44.0, 44.0), Sense::hover());
                let [r, g, b] = snap.dot;
                let c = Color32::from_rgb(r, g, b);
                ui.painter().circle_filled(rect.center(), 21.0, c.gamma_multiply(0.25));
                ui.painter().circle_filled(rect.center(), 11.0, c);
                ui.vertical(|ui| {
                    ui.label(RichText::new(&snap.status).size(20.0).strong());
                    for l in &snap.lines {
                        ui.label(RichText::new(l).color(SECONDARY));
                    }
                    ui.add_space(8.0);
                    ui.horizontal_wrapped(|ui| {
                        for item in &snap.home {
                            if let Item::Action { id, text, enabled, .. } = item {
                                if ui.add_enabled(*enabled, button(text, *id == snap.primary)).clicked() {
                                    self.click(snap, *id);
                                }
                            }
                        }
                    });
                });
            });
        });
        ui.columns(2, |c| {
            self.tcgkb_summary(&mut c[0], snap);
            week_card(&mut c[1], &snap.matches);
        });
        card(ui, |ui| {
            ui.horizontal(|ui| {
                section(ui, "Last matches");
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.button("See all").clicked() {
                        self.page = Page::Matches;
                    }
                });
            });
            self.match_list(ui, snap.matches.iter().take(5), "No battle log yet. Play a match and it shows up here.");
        });
    }

    fn tcgkb_summary(&mut self, ui: &mut egui::Ui, snap: &Snapshot) {
        card(ui, |ui| {
            section(ui, "tcgkb");
            let Some(user) = &snap.user else {
                ui.label(RichText::new("Your matches stay on this PC").strong());
                ui.label(RichText::new("Sign in once and every match goes to tcgkb.app by itself: your stats, the meta and the ladder.").color(SECONDARY));
                ui.add_space(8.0);
                if let Some(id) = snap.sign_in {
                    if ui.add(button("Sign in to tcgkb...", true)).clicked() {
                        self.click(snap, id);
                    }
                }
                return;
            };
            ui.label(RichText::new(if user.is_empty() { "Signed in".to_string() } else { format!("Signed in as {user}") }).strong());
            if let Some(l) = &snap.last_upload {
                ui.label(RichText::new(format!("Last upload: {l}")).color(SECONDARY));
            }
            let accounts = snap.me.as_ref().map(|m| m.accounts.as_slice()).unwrap_or_default();
            if !accounts.is_empty() {
                ui.add_space(6.0);
                ui.horizontal_wrapped(|ui| {
                    for a in accounts {
                        chip(ui, &format!("{} · {}", a.name.as_deref().unwrap_or("PTCGL account"), status_word(a)), a.status.as_deref() == Some(VERIFIED));
                    }
                });
                if ui.link(RichText::new("What does verified mean?").color(ACCENT)).clicked() {
                    self.page = Page::Tcgkb;
                }
            }
            if let Some(l) = LadderStatus::from(snap.me.as_ref()) {
                ui.add_space(8.0);
                ui.horizontal(|ui| {
                    ui.label(&l.title);
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| ui.label(RichText::new(&l.count).color(SECONDARY)));
                });
                bar(ui, l.progress);
            }
            if let Some((id, on)) = snap.auto_upload {
                ui.add_space(6.0);
                if toggle(ui, on, "Upload my matches automatically").clicked() {
                    self.click(snap, id);
                }
            }
        });
    }

    fn match_list<'a>(&self, ui: &mut egui::Ui, matches: impl Iterator<Item = &'a Match>, empty: &str) {
        let now = Local::now().naive_local();
        let mut any = false;
        for m in matches {
            any = true;
            let (word, color) = result_text(m.result);
            let row = ui
                .horizontal(|ui| {
                    ui.set_min_height(30.0);
                    ui.add_sized([80.0, 20.0], egui::Label::new(RichText::new(word).color(color).strong()).selectable(false));
                    ui.add(egui::Label::new(m.opponent.as_deref().map_or_else(|| "Unknown opponent".to_string(), |o| format!("vs {o}"))).selectable(false).truncate());
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.add(egui::Label::new(RichText::new(when(m.local_time, now)).color(SECONDARY)).selectable(false));
                    });
                })
                .response
                .interact(Sense::click())
                .on_hover_cursor(egui::CursorIcon::PointingHand)
                .on_hover_text("Opens the battle log");
            if row.clicked() {
                let path = m.path.clone();
                self.run(move |_| {
                    shell::open_text(&path);
                });
            }
        }
        if !any {
            ui.label(RichText::new(empty).color(SECONDARY));
        }
    }

    // ---- Matches -------------------------------------------------------------------------

    fn matches(&mut self, ui: &mut egui::Ui, snap: &Snapshot) {
        ui.horizontal(|ui| {
            heading(ui, "Matches");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                for (label, key) in [("Defeats", Some("DEFEAT")), ("Victories", Some("VICTORY")), ("All", None)] {
                    if ui.selectable_label(self.filter == key, label).clicked() {
                        self.filter = key;
                    }
                }
            });
        });
        let filter = self.filter;
        let shown = || snap.matches.iter().filter(move |m| filter.is_none_or(|f| m.result == f));
        card(ui, |ui| {
            self.match_list(ui, shown().take(MAX_LISTED_MATCHES), "No battle log here yet.");
            if shown().count() > MAX_LISTED_MATCHES {
                ui.add_space(8.0);
                ui.label(RichText::new("Older matches are in your logs folder.").color(SECONDARY));
            }
        });
    }

    // ---- tcgkb ---------------------------------------------------------------------------

    fn tcgkb(&mut self, ui: &mut egui::Ui, snap: &Snapshot) {
        heading(ui, "tcgkb");
        if snap.user.is_none() {
            card(ui, |ui| {
                ui.label(RichText::new("Sign in once. That's it.").strong());
                ui.label(
                    RichText::new(
                        "Your browser opens tcgkb.app. Approve the code and every match goes up by itself, with every PTCGL account you play on this PC. The app never sees your password.",
                    )
                    .color(SECONDARY),
                );
                ui.add_space(10.0);
                if let Some(id) = snap.sign_in {
                    if ui.add(button("Sign in to tcgkb...", true)).clicked() {
                        self.click(snap, id);
                    }
                }
            });
            return;
        }
        let list = snap.me.as_ref().map(|m| &m.accounts);
        let l = LadderStatus::from(snap.me.as_ref());
        card(ui, |ui| {
            section(ui, "Your place on the ladder");
            step(ui, true, "Signed in to tcgkb", "Only players with a tcgkb account get a row.", None);
            let has_account = l.as_ref().map(|l| l.has_account).or_else(|| list.map(|a| a.iter().any(|a| a.status.as_deref() != Some(DISPUTED)))).unwrap_or(false);
            let detail = if has_account { "Every account you play on this PC counts as yours, merged into one row." } else { "Added by itself with your first uploaded match." };
            step(ui, has_account, "PTCGL account added", detail, None);
            match &l {
                Some(l) => step(ui, l.ranked, &l.step, &l.why, Some(l.progress)),
                None => step(
                    ui,
                    false,
                    "Play 5 matches",
                    "Until then you show up at the bottom of the ladder as \"Not ranked yet\", so a few lucky wins can't top the board.",
                    Some(0.0),
                ),
            }
            ui.add_space(4.0);
            if ui.link(RichText::new("Open the ladder on tcgkb.app").color(ACCENT)).clicked() {
                self.run(|_| {
                    shell::open(LADDER_URL);
                });
            }
        });
        card(ui, |ui| {
            section(ui, "Your PTCGL accounts");
            match list {
                None => drop(ui.label(RichText::new("Asking tcgkb...").color(SECONDARY))),
                Some(a) if a.is_empty() => drop(ui.label(RichText::new("None yet: your account is added with your first uploaded match.").color(SECONDARY))),
                Some(a) => {
                    for a in a {
                        account_row(ui, a);
                    }
                }
            }
            ui.add_space(8.0);
            Frame::new().fill(RAISE).corner_radius(6).inner_margin(Margin::symmetric(14, 12)).show(ui, |ui| {
                ui.set_width(ui.available_width());
                ui.label(RichText::new("What does verified mean?").strong());
                ui.label(
                    RichText::new(
                        "An account is verified once someone you played, who also uses tcgkb, uploads the same match from their side. It proves the account is really yours.\n\n\
                         You don't need it to be on the ladder: verified only adds the check next to your name. If another tcgkb user ever claims the same PTCGL account, the one with a verified match keeps it.",
                    )
                    .color(MUTED),
                );
            });
        });
        // The connection itself last: the ladder is what players come here for.
        if let Item::Sub { text, items } = &snap.tcgkb {
            self.sub_card(ui, snap, text, items);
        }
    }

    // ---- Pages of menu items --------------------------------------------------------------

    /// The page's name, then its items in cards: a separator starts a new card, a submenu gets its own.
    fn items_page(&self, ui: &mut egui::Ui, snap: &Snapshot, name: &str, items: &[Item]) {
        heading(ui, name);
        let mut pending: Vec<&Item> = Vec::new();
        let flush = |ui: &mut egui::Ui, pending: &mut Vec<&Item>| {
            if !pending.is_empty() {
                card(ui, |ui| self.rows(ui, snap, pending));
                pending.clear();
            }
        };
        for item in items {
            match item {
                Item::Separator => flush(ui, &mut pending),
                Item::Sub { text, items } => {
                    flush(ui, &mut pending);
                    self.sub_card(ui, snap, text, items);
                }
                _ => pending.push(item),
            }
        }
        flush(ui, &mut pending);
    }

    fn sub_card(&self, ui: &mut egui::Ui, snap: &Snapshot, text: &str, items: &[Item]) {
        card(ui, |ui| {
            ui.label(RichText::new(text).strong());
            ui.add_space(4.0);
            if is_choice(items) {
                for item in items {
                    if let Item::Action { id, text, checked, enabled, .. } = item {
                        if ui.add_enabled(*enabled, egui::RadioButton::new(*checked, text)).clicked() && !checked {
                            self.click(snap, *id);
                        }
                    }
                }
            } else {
                self.rows(ui, snap, &items.iter().collect::<Vec<_>>());
            }
        });
    }

    fn rows(&self, ui: &mut egui::Ui, snap: &Snapshot, items: &[&Item]) {
        for item in items {
            match item {
                Item::Separator => drop(ui.separator()),
                Item::Sub { text, items } => {
                    ui.add_space(4.0);
                    ui.label(RichText::new(text.as_str()).strong());
                    self.rows(ui, snap, &items.iter().collect::<Vec<_>>());
                }
                Item::Action { id: 0, text, default, .. } => {
                    let t = RichText::new(text.as_str()).color(SECONDARY);
                    ui.label(if *default { t.strong() } else { t });
                }
                Item::Action { id, text, checked, enabled, default } => {
                    let toggle_item = snap.actions.get(*id as usize - 1).is_some_and(|a| is_toggle(*a));
                    let r = if toggle_item {
                        ui.add_enabled_ui(*enabled, |ui| toggle(ui, *checked, text)).inner
                    } else {
                        ui.add_enabled(*enabled, button(text, *default))
                    };
                    if r.clicked() {
                        self.click(snap, *id);
                    }
                }
            }
        }
    }
}

// ---- Look ---------------------------------------------------------------------------------

fn card<R>(ui: &mut egui::Ui, add: impl FnOnce(&mut egui::Ui) -> R) -> R {
    Frame::new()
        .fill(CARD)
        .stroke(Stroke::new(1.0, BORDER))
        .corner_radius(8)
        .inner_margin(Margin::symmetric(16, 14))
        .outer_margin(Margin { left: 0, right: 0, top: 0, bottom: 12 })
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            add(ui)
        })
        .inner
}

fn heading(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(text).size(20.0).strong());
    ui.add_space(8.0);
}

/// A card's small upper-case heading.
fn section(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(text.to_uppercase()).size(11.5).strong().color(SECONDARY));
    ui.add_space(4.0);
}

fn button(text: &str, accent: bool) -> egui::Button<'_> {
    let b = egui::Button::new(if accent { RichText::new(text).color(ON_ACCENT).strong() } else { RichText::new(text) }).min_size(egui::vec2(0.0, 28.0));
    if accent {
        b.fill(ACCENT)
    } else {
        b
    }
}

/// A switch: shows `on` and reports the click; the action itself flips the setting.
fn toggle(ui: &mut egui::Ui, on: bool, text: &str) -> egui::Response {
    let mut v = on;
    ui.checkbox(&mut v, text)
}

fn bar(ui: &mut egui::Ui, progress: f64) {
    ui.add(egui::ProgressBar::new(progress as f32).desired_height(6.0).fill(ACCENT).corner_radius(CornerRadius::same(3)));
}

fn chip(ui: &mut egui::Ui, text: &str, highlight: bool) {
    Frame::new().fill(if highlight { ACCENT.gamma_multiply(0.2) } else { RAISE }).corner_radius(10).inner_margin(Margin::symmetric(8, 3)).show(ui, |ui| {
        ui.label(RichText::new(text).size(12.0).color(if highlight { ACCENT } else { MUTED }));
    });
}

/// A step towards the ladder: a check or a dot, the line, its explanation and maybe a progress bar.
fn step(ui: &mut egui::Ui, done: bool, title: &str, detail: &str, progress: Option<f64>) {
    ui.add_space(4.0);
    ui.horizontal_top(|ui| {
        ui.label(RichText::new(if done { "✔" } else { "○" }).size(16.0).color(if done { GOOD } else { SECONDARY }));
        ui.vertical(|ui| {
            ui.label(title);
            ui.label(RichText::new(detail).color(SECONDARY));
            if let Some(p) = progress.filter(|_| !done) {
                ui.add_space(4.0);
                ui.scope(|ui| {
                    ui.set_max_width(260.0);
                    bar(ui, p);
                });
            }
        });
    });
    ui.add_space(4.0);
}

fn account_row(ui: &mut egui::Ui, a: &Account) {
    ui.add_space(4.0);
    ui.horizontal_top(|ui| {
        ui.vertical(|ui| {
            ui.set_max_width(ui.available_width() - 120.0);
            ui.label(a.name.as_deref().unwrap_or("PTCGL account"));
            let detail = match a.status.as_deref() {
                Some(VERIFIED) => "Confirmed by an opponent",
                Some(DISPUTED) => "Another tcgkb user claims it too: it counts for no one until one of you has a verified match",
                _ => "Verified once an opponent on tcgkb uploads the same match",
            };
            ui.label(RichText::new(detail).color(SECONDARY));
        });
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Min), |ui| chip(ui, status_word(a), a.status.as_deref() == Some(VERIFIED)));
    });
    ui.add_space(4.0);
}

fn week_card(ui: &mut egui::Ui, matches: &[Match]) {
    let (count, rate, won) = week(matches, Local::now().naive_local());
    card(ui, |ui| {
        section(ui, "This week");
        ui.columns(3, |c| {
            tile(&mut c[0], &count.to_string(), "matches", TEXT);
            tile(&mut c[1], &rate.map_or_else(|| "–".to_string(), |r| format!("{r}%")), "win rate", GOOD);
            tile(&mut c[2], &won.to_string(), "victories", TEXT);
        });
        ui.add_space(8.0);
        let note = if matches.is_empty() { "From the battle logs on this PC.".to_string() } else { format!("{} battle logs on this PC in all.", matches.len()) };
        ui.label(RichText::new(note).color(SECONDARY));
    });
}

fn tile(ui: &mut egui::Ui, number: &str, caption: &str, color: Color32) {
    Frame::new().fill(RAISE).corner_radius(6).inner_margin(Margin::symmetric(10, 8)).show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.label(RichText::new(number).size(20.0).strong().color(color));
        ui.label(RichText::new(caption).size(12.0).color(SECONDARY));
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(s: &str) -> NaiveDateTime {
        NaiveDateTime::parse_from_str(s, "%Y-%m-%d %H:%M").unwrap()
    }

    #[test]
    fn when_reads_like_the_csharp_window() {
        let now = at("2026-11-25 18:00");
        assert_eq!(when(at("2026-11-25 17:59"), now), "Just now");
        assert_eq!(when(at("2026-11-25 17:30"), now), "30 min ago");
        assert_eq!(when(at("2026-11-25 15:00"), now), "3 h ago");
        assert_eq!(when(at("2026-11-24 23:00"), now), "Yesterday");
        assert_eq!(when(at("2026-11-23 12:00"), now), "Monday");
        assert_eq!(when(at("2026-10-12 12:00"), now), "12 Oct");
        assert_eq!(when(at("2025-10-12 12:00"), now), "12 Oct 2025");
    }

    #[test]
    fn week_counts_the_last_seven_days() {
        let m = |r: &'static str, t: &str| Match { result: r, opponent: None, local_time: at(t), path: PathBuf::new() };
        let now = at("2026-11-25 18:00");
        let list = [m("VICTORY", "2026-11-25 10:00"), m("DEFEAT", "2026-11-24 10:00"), m("VICTORY", "2026-11-20 10:00"), m("NO-RESULT", "2026-11-20 11:00"), m("VICTORY", "2026-11-01 10:00")];
        assert_eq!(week(&list, now), (4, Some(67), 2));
        assert_eq!(week(&[], now), (0, None, 0));
    }

    #[test]
    fn only_one_ticked_option_lists_are_choices() {
        let opt = |id, checked| Item::Action { id, text: String::new(), checked, enabled: true, default: false };
        assert!(is_choice(&[opt(1, true), opt(2, false)]));
        assert!(!is_choice(&[opt(1, false), opt(2, false)]));
        assert!(!is_choice(&[Item::label("x"), opt(1, true), opt(2, false)]));
    }
}
