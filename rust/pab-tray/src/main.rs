//! PtcglAutoBattleLog.exe in Rust: the tray app, installer, repair daemon and updater (rust/README.md).
//! Phase 3 in progress: this exe does not replace the C# tray yet.
#![cfg_attr(windows, windows_subsystem = "windows")]
// shortcut: the Windows layer lands before the tray that calls it; drop this once phase 3 wires it up.
#![allow(dead_code)]

#[cfg(windows)]
mod app;
mod daemon;
mod embedded;
mod game_setup;
mod log;
mod logs_watcher;
mod session;
mod status_icons;
mod sys;
#[cfg(test)]
mod tests;
mod updates;
#[cfg(windows)]
mod win;

pub const PRODUCT_NAME: &str = "PTCGL Auto Battle Log";
pub const PUBLISHER: &str = "manucruzleiva";
pub const GAME_NAME: &str = "Pokémon TCG Live";
/// build.sh passes the release's version and repository (forks update from themselves).
pub const VERSION: &str = match option_env!("PAB_VERSION") {
    Some(v) => v,
    None => env!("CARGO_PKG_VERSION"),
};
pub const REPO_OWNER: &str = match option_env!("PAB_REPO_OWNER") {
    Some(v) => v,
    None => "manucruzleiva",
};
pub const REPO_NAME: &str = match option_env!("PAB_REPO_NAME") {
    Some(v) => v,
    None => "ptcgl-auto-battlelog",
};

pub fn project_url() -> String {
    format!("https://github.com/{REPO_OWNER}/{REPO_NAME}")
}

/// `s` cut to `max` characters, ending in "…" when cut (TrayApplicationContext.Clip).
pub fn clip(s: &str, max: usize) -> String {
    if s.chars().count() <= max {
        return s.to_string();
    }
    s.chars().take(max.saturating_sub(1)).chain(std::iter::once('…')).collect()
}

/// A lock that survives a panic elsewhere: the tray keeps running on the data as it was.
pub fn lock<T>(m: &std::sync::Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|p| p.into_inner())
}

/// Where Pokémon TCG Live is, from every Windows source GameLocator ranks (GameFinder).
#[cfg(windows)]
pub fn find_game(saved_dir: Option<&str>) -> Option<std::path::PathBuf> {
    use pab_core::game_locator;
    use win::shell::{folder, Folder};
    let s = |f| folder(f).map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
    let candidates = game_locator::default_candidates(&s(Folder::Profile), &s(Folder::LocalAppData), &s(Folder::ProgramFiles), &s(Folder::ProgramFilesX86));
    let candidates: Vec<String> = candidates.iter().map(|p| p.to_string_lossy().into_owned()).collect();
    let registry = win::registry::game_locations();
    let running = win::process::game_running_dir().map(|p| p.to_string_lossy().into_owned());
    let inputs = saved_dir.into_iter().chain(candidates.iter().map(String::as_str)).chain(registry.iter().map(String::as_str)).chain(running.as_deref());
    game_locator::find(inputs)
}

#[cfg(not(windows))]
fn main() {
    eprintln!("{PRODUCT_NAME} runs on Windows only.");
    std::process::exit(1);
}

#[cfg(windows)]
fn main() {
    std::process::exit(run());
}

/// Program.cs: one exe that is the tray, the installer and the uninstaller.
#[cfg(windows)]
fn run() -> i32 {
    use pab_core::command_line::CommandLine;
    use std::sync::Arc;
    use std::time::Duration;
    use win::instance::{self, SingleInstance, TraySignals};

    std::panic::set_hook(Box::new(|p| log::fatal("Fatal unhandled panic", p)));
    let args: Vec<String> = std::env::args().skip(1).collect();
    let cli = CommandLine::parse(&args);
    let exe = std::env::current_exe().unwrap_or_default();
    let session = Arc::new(session::Session::create(&exe, &cli));
    log::set_file(&session.paths().tray_log_file);
    session.load_settings();

    let commit = |s: &session::Session| {
        if s.portable() {
            s.prepare_data_folder();
        }
        log::commit();
    };
    let consent_pending = !session.consent() && !cli.uninstall && !cli.background && !cli.updated();
    if !consent_pending {
        commit(&session);
    }
    log::info(&format!(
        "Start: v{VERSION} {} mode, exe {}{}",
        if session.portable() { "portable" } else { "installed" },
        exe.display(),
        if args.is_empty() { String::new() } else { format!(", args: {}", args.join(" ")) }
    ));
    if !cli.unknown.is_empty() {
        log::info(&format!("Ignored arguments: {}", cli.unknown.join(" ")));
    }
    // shortcut: --uninstall and the installer come in phase 3c; until then only a portable copy
    // (or one already at the install path) runs.
    if cli.uninstall || (!session.portable() && !win::registry::same_path(&exe.to_string_lossy(), &session.paths().installed_exe.to_string_lossy())) {
        log::info("Installing and uninstalling are not in the Rust tray yet; run it with --portable.");
        return 1;
    }

    let wait = Duration::from_secs(if cli.updated() { 10 } else if cli.first_run { 5 } else { 0 });
    let single = SingleInstance::acquire(wait);
    if !single.owned {
        if single.access_denied {
            log::info("A copy started as administrator is running; this one cannot reach it.");
            return 1;
        }
        if !cli.background {
            instance::signal(&instance::show_event_name());
        }
        log::info(if cli.background { "Already running." } else { "Already running; asked it to show itself." });
        return 0;
    }
    let signals = TraySignals::create();
    let exit_requested = || {
        let r = signals.exit_requested();
        if r {
            log::info("Another process asked the tray to exit while it was starting; exiting.");
        }
        r
    };
    commit(&session);
    if exit_requested() {
        return 0;
    }
    pab_core::self_update::cleanup_old(&session.paths().installed_exe);
    let mut first_run = cli.first_run;
    if session.portable() {
        if session.consent() {
            win::process::unblock(&session.exe_path);
        }
        if session.game().is_none() && !cli.background && !cli.updated() {
            if game_setup::locate(&session).is_some() {
                game_setup::try_repair(&session);
            }
            first_run = true;
        }
    } else if let Err(e) = win::registry::register_uninstall_entry(&session.paths()) {
        log::error("Could not refresh the Apps & Features entry", e);
    }
    if exit_requested() {
        return 0;
    }
    match app::run(session, &cli, first_run, &signals) {
        Ok(()) => {
            log::info("Exited.");
            0
        }
        Err(e) => {
            log::fatal("Startup failed", e);
            1
        }
    }
}
