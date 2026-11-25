//! The "tcgkb account" and "Pokematch" submenus (TrayApplicationContext.Tcgkb.cs and .Pokematch.cs).
//! The uploads themselves run on the uploads thread (crate::uploads).
use super::{Click, Tray};
use crate::uploads::{self, Notice};
use crate::win::dialogs;
use crate::win::notify_icon::BalloonIcon;
use crate::win::popup::Item;
use crate::win::shell;
use crate::{log, PRODUCT_NAME};
use chrono::{DateTime, Utc};
use std::rc::Rc;
use std::time::Duration;

#[derive(Clone, Copy)]
pub(super) enum AccountAction {
    TcgkbSignIn,
    TcgkbAuto,
    TcgkbUploadNow,
    TcgkbDisconnect,
    PokematchToken,
    PokematchAuto,
    PokematchPast,
    PokematchRemove,
}

use AccountAction as A;

impl Tray {
    /// The two submenus; `item(text, enabled, checked, action)` gives each an id.
    pub(super) fn account_menus(&self, item: &mut dyn FnMut(&str, bool, bool, AccountAction) -> Item) -> [Item; 2] {
        let s = &self.session;
        let u = &self.uploads;
        let status = u.status();
        let consent = s.consent();

        let connected = u.clients.tcgkb.is_connected();
        let auto = uploads::tcgkb_auto(s);
        let mut tcgkb = vec![Item::label(match u.clients.tcgkb.user_name().filter(|_| connected) {
            Some(name) => crate::clip(&format!("Connected as {name}"), 60),
            None if connected => "Connected".into(),
            None => "Not connected".into(),
        })];
        if connected {
            tcgkb.extend([Item::Separator, item("Upload my matches automatically", true, auto, A::TcgkbAuto)]);
            if auto {
                tcgkb.push(item("Upload matches now", true, false, A::TcgkbUploadNow));
                if !consent {
                    tcgkb.push(Item::label("Waiting: accept the disclaimer"));
                } else if let Some(last) = &status.tcgkb_last {
                    tcgkb.push(Item::label(crate::clip(&format!("Last upload: {last}"), 90)));
                }
            }
            tcgkb.extend([Item::Separator, item("Disconnect", true, false, A::TcgkbDisconnect)]);
        } else {
            tcgkb.push(item("Sign in to tcgkb...", true, false, A::TcgkbSignIn));
        }

        let token = u.clients.has_pokematch_token();
        let auto = s.read(|x| x.pokematch_auto_upload);
        let info = if !token {
            "Not set up".to_string()
        } else if status.pokematch_token_rejected {
            "Pokematch refused the token: set a new one".into()
        } else if !consent {
            "Waiting: accept the disclaimer".into()
        } else if status.pokematch_busy {
            "Uploading...".into()
        } else {
            status.pokematch_last.clone().unwrap_or_else(|| if auto { "Uploads new battle logs" } else { "Automatic upload is off" }.into())
        };
        let mut pokematch = vec![
            Item::label(crate::clip(&info, 80)),
            item(if token { "Change API token..." } else { "Set API token..." }, true, false, A::PokematchToken),
        ];
        if token {
            pokematch.extend([
                item("Upload new battle logs automatically", true, auto, A::PokematchAuto),
                item("Upload past battle logs...", !status.pokematch_busy, false, A::PokematchPast),
                item("Remove API token", true, false, A::PokematchRemove),
            ]);
        }
        [Item::Sub { text: "tcgkb account".into(), items: tcgkb }, Item::Sub { text: "Pokematch".into(), items: pokematch }]
    }

    pub(super) fn run_account(&self, a: AccountAction) {
        match a {
            A::TcgkbSignIn => self.sign_in_tcgkb(),
            A::TcgkbAuto => {
                let on = !uploads::tcgkb_auto(&self.session);
                self.session.update(|x| x.tcgkb_auto_upload = Some(on));
                log::info(&format!("Automatic match upload to tcgkb turned {}.", if on { "on" } else { "off" }));
                if on {
                    self.uploads.upload_tcgkb(true);
                }
            }
            A::TcgkbUploadNow => self.uploads.upload_tcgkb(true),
            A::TcgkbDisconnect => {
                if self.modal(|| dialogs::confirm(self.owner(), "Disconnect from tcgkb? Your PTCGL accounts and matches stay on tcgkb.", false)) {
                    self.uploads.disconnect_tcgkb();
                }
            }
            A::PokematchToken => self.set_pokematch_token(),
            A::PokematchAuto => self.toggle_pokematch_auto(),
            A::PokematchPast => self.upload_past_pokematch(),
            A::PokematchRemove => {
                let text = "Remove the Pokematch API token from this computer? Nothing more is uploaded. Battle logs already on Pokematch stay there; delete the token in Pokematch too if you no longer use it.";
                if !self.modal(|| dialogs::confirm(self.owner(), text, false)) {
                    return;
                }
                self.uploads.clients.pokematch_tokens.delete();
                self.session.update(|x| x.pokematch_auto_upload = false);
                self.uploads.pokematch_token_removed();
                log::info("Pokematch API token removed.");
            }
        }
    }

    pub(super) fn on_upload_notice(&self, n: Notice) {
        let click = n.click.map(|c| -> Click {
            match c {
                uploads::Click::SignIn => Rc::new(|t: &Tray| t.sign_in_tcgkb()),
                uploads::Click::SetPokematchToken => Rc::new(|t: &Tray| t.set_pokematch_token()),
                uploads::Click::Consent => Rc::new(|t: &Tray| t.ask_consent()),
                uploads::Click::OpenAppLog => Rc::new(|t: &Tray| {
                    shell::open_text(&log::file_path().unwrap_or_else(|| t.session.paths().tray_log_file));
                }),
            }
        });
        self.balloon(&n.title, &n.text, if n.warning { BalloonIcon::Warning } else { BalloonIcon::Info }, click);
    }

    // shortcut: the tcgkb sign-in and the Pokematch token box are egui windows
    // (phase 4); until then these say so. A tray signed in by 1.6.x keeps uploading meanwhile.
    fn not_in_this_build(&self, what: &str) {
        self.modal(|| dialogs::info(self.owner(), &format!("{what} is not in this test build of {PRODUCT_NAME} yet. It comes with the app window.")));
    }

    fn sign_in_tcgkb(&self) {
        self.not_in_this_build("Signing in to tcgkb");
    }

    fn set_pokematch_token(&self) {
        self.not_in_this_build("Setting the Pokematch API token");
    }

    /// Nothing goes to Pokematch before the disclaimer that covers it is accepted.
    fn ensure_consent(&self) -> bool {
        if !self.session.consent() {
            self.ask_consent();
        }
        self.session.consent()
    }

    fn toggle_pokematch_auto(&self) {
        let s = &self.session;
        if s.read(|x| x.pokematch_auto_upload) {
            s.update(|x| x.pokematch_auto_upload = false);
            self.uploads.stop_pokematch();
            log::info("Pokematch automatic upload: off");
            return;
        }
        if !self.uploads.clients.has_pokematch_token() {
            return self.set_pokematch_token();
        }
        if !self.ensure_consent() {
            return;
        }
        let text = "Upload your new battle logs to Pokematch automatically?\n\n\
            After each finished match (victory or defeat), the app adds it to your Pokematch logs: your deck name, the result and the date. \
            Your opponent's deck shows as \"Unknown\": fill it in on Pokematch if you like. Your opponent's name and the battle log text stay on this computer.\n\n\
            Only matches played from now on are sent; \"Upload past battle logs...\" sends older ones. Turn this off at any time in the Pokematch menu.";
        if !self.modal(|| dialogs::confirm(self.owner(), text, false)) {
            return;
        }
        let since = uploads::now_iso();
        s.update(|x| {
            x.pokematch_auto_upload = true;
            if x.pokematch_since_utc.as_deref().is_none_or(str::is_empty) {
                x.pokematch_since_utc = Some(since);
            }
        });
        log::info("Pokematch automatic upload: on");
        self.uploads.pokematch_in(Duration::from_secs(5));
    }

    fn upload_past_pokematch(&self) {
        if !self.uploads.clients.has_pokematch_token() {
            return self.set_pokematch_token();
        }
        if self.uploads.status().pokematch_busy || !self.ensure_consent() {
            return;
        }
        let folder = self.session.logs_folder(self.session.game().as_ref());
        // shortcut: counted on the UI thread (reads each log's seal); move to the uploads thread if a big folder stalls the menu.
        let count = self.uploads.clients.pokematch.count_new(&folder, DateTime::<Utc>::MIN_UTC);
        if count == 0 {
            let text = format!("Pokematch already has every finished match in your logs folder.\n\n{}", shell::friendly(&folder));
            return self.modal(|| dialogs::info(self.owner(), &text));
        }
        let text = format!(
            "Upload {count} battle log{} from your logs folder to Pokematch?\n\nEach one adds your deck name, the result and the date to your Pokematch logs. Matches already on Pokematch are not sent again.",
            if count == 1 { "" } else { "s" }
        );
        if self.modal(|| dialogs::confirm(self.owner(), &text, false)) {
            self.uploads.upload_pokematch(true);
        }
    }
}
