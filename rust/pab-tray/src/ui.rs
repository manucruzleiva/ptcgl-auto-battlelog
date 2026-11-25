//! The app's windows (egui): the tcgkb sign-in (TcgkbConnectDialog), the Pokematch API token box
//! (PokematchTokenDialog) and the text windows (disclaimer, licenses). They run one at a time on a
//! thread of their own, so the tray icon keeps answering while one is open; winit allows one event
//! loop per process, which eframe keeps on that thread and reuses for every window.
use crate::uploads::Clients;
use crate::win::dialogs;
use crate::{lock, log, PRODUCT_NAME};
use eframe::egui;
use pab_core::tcgkb::{self, DeviceAuthorization, TcgkbError};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{channel, Sender};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

type Job = Box<dyn FnOnce() + Send>;

static UI: OnceLock<Mutex<Sender<Job>>> = OnceLock::new();
static BUSY: AtomicBool = AtomicBool::new(false);

/// A window is open (or queued): the tray does not open a second one.
pub fn busy() -> bool {
    BUSY.load(Ordering::SeqCst)
}

/// Runs `job` on the window thread, after any window already open. `job` opens its windows with the
/// functions below and hands its result back itself (the tray posts it to its own thread).
pub fn spawn(job: impl FnOnce() + Send + 'static) {
    let tx = UI.get_or_init(|| {
        let (tx, rx) = channel::<Job>();
        std::thread::spawn(move || {
            for job in rx {
                BUSY.store(true, Ordering::SeqCst);
                // A panic in one window must not take the others with it.
                if std::panic::catch_unwind(std::panic::AssertUnwindSafe(job)).is_err() {
                    log::info("A window closed after an internal error.");
                }
                BUSY.store(false, Ordering::SeqCst);
            }
        });
        Mutex::new(tx)
    });
    BUSY.store(true, Ordering::SeqCst);
    let _ = lock(tx).send(Box::new(job));
}

fn options(title: &str, size: [f32; 2]) -> eframe::NativeOptions {
    let mut viewport = egui::ViewportBuilder::default()
        .with_title(format!("{PRODUCT_NAME} - {title}"))
        .with_inner_size(size)
        .with_resizable(false)
        .with_minimize_button(false)
        .with_maximize_button(false)
        .with_active(true);
    if let Some(icon) = app_icon() {
        viewport = viewport.with_icon(icon);
    }
    eframe::NativeOptions {
        viewport,
        centered: true,
        // Only the first window builds the event loop: it lives on this thread, not the main one.
        event_loop_builder: Some(Box::new(|b| {
            use winit::platform::windows::EventLoopBuilderExtWindows;
            b.with_any_thread(true);
        })),
        ..Default::default()
    }
}

fn app_icon() -> Option<egui::IconData> {
    let rgba = crate::status_icons::draw(crate::embedded::APP_ICON, 32, None)?;
    Some(egui::IconData { rgba, width: 32, height: 32 })
}

/// Runs one window until it closes. `ui` draws it; it closes the window with `close(ui)`.
fn show(title: &str, size: [f32; 2], mut draw: impl FnMut(&mut egui::Ui) + 'static) {
    run(options(title, size), title, move |ui| {
        egui::CentralPanel::default().show(ui, |ui| draw(ui));
    });
}

/// The status window: resizable, titled with the product's name; `draw` gets the whole window.
pub fn show_main(size: [f32; 2], min: [f32; 2], draw: impl FnMut(&mut egui::Ui) + 'static) {
    let mut o = options("", size);
    o.viewport = o.viewport.with_title(PRODUCT_NAME).with_resizable(true).with_min_inner_size(min);
    run(o, PRODUCT_NAME, draw);
}

fn run(options: eframe::NativeOptions, title: &str, draw: impl FnMut(&mut egui::Ui) + 'static) {
    struct App<F>(F);
    impl<F: FnMut(&mut egui::Ui)> eframe::App for App<F> {
        fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
            (self.0)(ui);
        }
    }
    let result = eframe::run_native(
        PRODUCT_NAME,
        options,
        Box::new(move |cc| {
            cc.egui_ctx.set_visuals(egui::Visuals::dark());
            Ok(Box::new(App(draw)))
        }),
    );
    if let Err(e) = result {
        log::error(&format!("Could not open the window \"{title}\""), e);
    }
}

pub fn close(ui: &egui::Ui) {
    ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
}

fn buttons(ui: &mut egui::Ui, add: impl FnOnce(&mut egui::Ui)) {
    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), add);
}

/// A text to read (the disclaimer as accepted, the licenses), with OK.
pub fn show_text(title: &str, intro: &str, text: String) {
    let intro = intro.to_string();
    show(title, [620.0, 520.0], move |ui| {
        ui.label(&intro);
        ui.add_space(8.0);
        egui::ScrollArea::vertical().max_height(400.0).auto_shrink([false, false]).show(ui, |ui| {
            ui.label(&text);
        });
        ui.add_space(8.0);
        buttons(ui, |ui| {
            if ui.button("OK").clicked() || ui.input(|i| i.key_pressed(egui::Key::Escape) || i.key_pressed(egui::Key::Enter)) {
                close(ui);
            }
        });
    });
}

/// "Set API token...": the player pastes the token made on Pokematch's API Tokens page. The box
/// hides what is typed. The cleaned token, or None when cancelled.
pub fn ask_pokematch_token() -> Option<String> {
    let result: Arc<Mutex<Option<String>>> = Arc::default();
    let out = result.clone();
    let (mut text, mut visible, mut error) = (String::new(), false, false);
    show("Pokematch API token", [480.0, 300.0], move |ui| {
        ui.label(
            "Make a token on Pokematch (Profile Settings > Account > API Tokens, for example named \"PTCGL uploader\") and paste it here. \
             It is kept encrypted on this computer for your Windows user, and sent only to Pokematch.\n\n\
             Changing your Pokematch password revokes the token: then set a new one here.",
        );
        ui.add_space(8.0);
        let edit = ui.add(egui::TextEdit::singleline(&mut text).password(!visible).desired_width(f32::INFINITY).hint_text("API token"));
        edit.request_focus();
        ui.checkbox(&mut visible, "Show the token");
        if error {
            ui.colored_label(egui::Color32::from_rgb(230, 90, 80), "That does not look like a token: paste only the token itself, without spaces.");
        }
        ui.add_space(8.0);
        let mut save = ui.input(|i| i.key_pressed(egui::Key::Enter));
        buttons(ui, |ui| {
            if ui.button("Cancel").clicked() || ui.input(|i| i.key_pressed(egui::Key::Escape)) {
                close(ui);
            }
            save |= ui.button("Save").clicked();
            if ui.button("Open Pokematch").clicked() {
                crate::win::shell::open(pab_core::pokematch::TOKENS_PAGE);
            }
        });
        if save {
            match pab_core::tokens::clean_pokematch_token(&text) {
                Some(t) => {
                    *lock(&out) = Some(t);
                    close(ui);
                }
                None => error = true,
            }
        }
    });
    let token = lock(&result).take();
    token
}

#[derive(Default)]
struct Connect {
    code: Option<DeviceAuthorization>,
    /// Ok(user name) or the error, once the flow ended.
    done: Option<Result<String, TcgkbError>>,
}

/// "Sign in to tcgkb...": shows the device code, opens tcgkb.app/link in the browser and waits for
/// the player to approve it there. The app never sees the tcgkb password. Closes by itself once
/// connected; Cancel stops waiting. The tcgkb user's name, or None (cancelled, or failed: already
/// shown and logged).
pub fn connect_tcgkb(clients: Arc<Clients>) -> Option<String> {
    let state: Arc<Mutex<Connect>> = Arc::default();
    let cancel = Arc::new(AtomicBool::new(false));
    let ctx_slot: Arc<Mutex<Option<egui::Context>>> = Arc::default();
    let repaint = {
        let ctx_slot = ctx_slot.clone();
        move || {
            if let Some(c) = lock(&ctx_slot).as_ref() {
                c.request_repaint();
            }
        }
    };

    let worker = {
        let (state, cancel, repaint) = (state.clone(), cancel.clone(), repaint.clone());
        std::thread::spawn(move || {
            let device = std::env::var("COMPUTERNAME").unwrap_or_else(|_| "Windows".into());
            let mut on_code = |c: &DeviceAuthorization| {
                open_page(c);
                lock(&state).code = Some(c.clone());
                repaint();
            };
            let mut delay = |d: Duration| -> tcgkb::Result<()> {
                let end = std::time::Instant::now() + d;
                while std::time::Instant::now() < end {
                    if cancel.load(Ordering::SeqCst) {
                        return Err(TcgkbError::cancelled());
                    }
                    std::thread::sleep(Duration::from_millis(100));
                }
                Ok(())
            };
            let r = clients.tcgkb.connect(&device, &mut on_code, &mut delay);
            lock(&state).done = Some(r);
            repaint();
        })
    };

    let (s, c) = (state.clone(), cancel.clone());
    show("Connect to tcgkb", [440.0, 230.0], move |ui| {
        lock(&ctx_slot).get_or_insert_with(|| ui.ctx().clone());
        let st = lock(&s);
        if st.done.is_some() {
            return close(ui);
        }
        match &st.code {
            None => {
                ui.label("Asking tcgkb for a code...");
            }
            Some(code) => {
                ui.label("Sign in to tcgkb in your browser and approve this code. It must match the code on the page:");
                ui.add_space(12.0);
                ui.vertical_centered(|ui| ui.label(egui::RichText::new(&code.user_code).monospace().size(28.0).strong()));
            }
        }
        let code = st.code.clone();
        drop(st);
        ui.add_space(12.0);
        buttons(ui, |ui| {
            if ui.button("Cancel").clicked() || ui.input(|i| i.key_pressed(egui::Key::Escape)) {
                c.store(true, Ordering::SeqCst);
                close(ui);
            }
            if ui.add_enabled(code.is_some(), egui::Button::new("Open tcgkb.app")).clicked() {
                if let Some(code) = &code {
                    open_page(code);
                }
            }
        });
    });
    // Closed with the window's X: same as Cancel.
    cancel.store(true, Ordering::SeqCst);
    let _ = worker.join();
    let done = lock(&state).done.take();
    match done {
        Some(Ok(name)) => {
            log::info(&format!("Connected to tcgkb as {name}."));
            Some(name)
        }
        Some(Err(e)) if e.code != "cancelled" => {
            log::error("Connecting to tcgkb failed", &e);
            dialogs::warning(std::ptr::null_mut(), &format!("Could not connect to tcgkb.\n\n{e}"));
            None
        }
        _ => None,
    }
}

fn open_page(code: &DeviceAuthorization) {
    let page = code.verification_uri_complete.as_deref().unwrap_or(&code.verification_uri);
    // Checked by TcgkbApi already; never opens anything else.
    if tcgkb::is_own_page(page) {
        crate::win::shell::open(page);
    }
}
