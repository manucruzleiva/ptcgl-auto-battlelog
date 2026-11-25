//! The background services against a fake game and data folder (any OS).
use crate::daemon::{Daemon, DaemonReport};
use crate::game_setup::{self, RepairOutcome};
use crate::logs_watcher::LogsWatcher;
use crate::session::Session;
use pab_core::command_line::CommandLine;
use pab_core::daemon_monitor::TrayHealth;
use pab_core::game_install::{InstallPiece, Payload};
use pab_core::game_locator::GAME_EXE;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{mpsc, Arc};
use std::time::Duration;

fn payload() -> Payload<'static> {
    let mut w = zip::ZipWriter::new(std::io::Cursor::new(Vec::new()));
    let opts = zip::write::SimpleFileOptions::default();
    for name in ["winhttp.dll", "doorstop_config.ini", "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/BepInEx.dll"] {
        w.start_file(name, opts).unwrap();
        w.write_all(name.as_bytes()).unwrap();
    }
    let zip: &'static [u8] = Box::leak(w.finish().unwrap().into_inner().into_boxed_slice());
    Payload { plugin_dll: b"plugin v2", bepinex_zip: zip }
}

fn fake_game(root: &Path) -> PathBuf {
    let g = root.join("Pokemon TCG Live");
    std::fs::create_dir_all(&g).unwrap();
    std::fs::write(g.join(GAME_EXE), "game").unwrap();
    g
}

fn session(tmp: &Path, portable: bool) -> Session {
    let exe = tmp.join("app").join("PtcglAutoBattleLog.exe");
    std::fs::create_dir_all(exe.parent().unwrap()).unwrap();
    let cli = CommandLine { portable, ..Default::default() };
    let s = Session::create_in(&exe, &cli, tmp.join("local"), tmp.join("docs"), Some(payload()));
    s.load_settings();
    s
}

#[test]
fn session_modes_and_settings() {
    let tmp = tempfile::tempdir().unwrap();
    let installed = session(tmp.path(), false);
    assert!(!installed.portable() && installed.paths().root == tmp.path().join("local").join("PtcglAutoBattleLog"));
    assert!(installed.run_command().ends_with("PtcglAutoBattleLog.exe\" --background"));

    let portable = session(tmp.path(), true);
    assert!(portable.portable() && portable.portable_forced && portable.run_command().ends_with("--background --portable"));
    assert!(!portable.paths().root.exists(), "nothing written before consent");
    portable.prepare_data_folder();
    portable.update(|s| s.notify_on_save = false);
    assert!(portable.paths().settings_file.is_file());
    let again = session(tmp.path(), true);
    assert!(!again.read(|s| s.notify_on_save), "settings saved and read back");
    assert_eq!(again.logs_folder(None), tmp.path().join("docs").join("PTCGL Battle Logs"));
    assert!(!again.has_custom_logs_folder(None));
}

#[test]
fn adopt_repair_and_daemon_reports() {
    let tmp = tempfile::tempdir().unwrap();
    let s = Arc::new(session(tmp.path(), false));
    let game = fake_game(tmp.path());
    assert_eq!(game_setup::try_repair(&s), RepairOutcome::NoGame);
    game_setup::adopt(&s, &game);
    assert!(s.read(|x| x.bep_in_ex_installed_by_us), "no BepInEx before us");
    assert_eq!(game_setup::try_repair(&s), RepairOutcome::ConsentPending);

    let (tx, rx) = mpsc::channel::<DaemonReport>();
    let daemon = Daemon::new(s.clone());
    daemon.start(move |r| {
        let _ = tx.send(r);
    });
    let first = rx.recv_timeout(Duration::from_secs(5)).unwrap();
    assert!(first.repaired.is_empty() && first.missing.contains(&InstallPiece::Plugin), "no consent: nothing written");
    assert!(!game.join("winhttp.dll").exists());
    assert_eq!(first.watch_folder, s.logs_folder(s.game().as_ref()));

    s.update(|x| x.disclaimer_accepted = pab_core::disclaimer::VERSION);
    daemon.poke();
    let repaired = (0..5).map(|_| rx.recv_timeout(Duration::from_secs(10)).unwrap()).find(|r| !r.repaired.is_empty()).expect("a repair");
    assert!(repaired.missing.is_empty() && repaired.repaired.contains(&InstallPiece::WinHttp));
    assert_eq!(std::fs::read(&s.game().unwrap().plugin_path).ok().as_deref(), Some(&b"plugin v2"[..]));
    assert_ne!(repaired.decision.health, TrayHealth::Error);
    assert_eq!(daemon.repair_now(), RepairOutcome::NothingToDo);
    daemon.stop();
}

#[test]
fn watcher_reports_each_new_log_once() {
    let tmp = tempfile::tempdir().unwrap();
    let folder = tmp.path().join("PTCGL Battle Logs");
    let (tx, rx) = mpsc::channel();
    let tx = std::sync::Mutex::new(tx);
    let mut w = LogsWatcher::new(Arc::new(move |p: PathBuf, result: &'static str, opp: Option<String>| {
        let _ = crate::lock(&tx).send((p, result, opp));
    }));
    w.watch(&folder);
    assert!(folder.is_dir() && w.folder() == Some(folder.as_path()));
    w.watch(&folder); // cheap: same folder
    std::fs::write(folder.join("notes.txt"), "x").unwrap();
    let name = "2026-10-20 13.10.00 - VICTORY vs Gary.txt";
    std::fs::write(folder.join("tmp.part"), "log").unwrap();
    std::fs::rename(folder.join("tmp.part"), folder.join(name)).unwrap();
    let (path, result, opp) = rx.recv_timeout(Duration::from_secs(10)).unwrap();
    assert_eq!((path.file_name().unwrap().to_str().unwrap(), result, opp.as_deref()), (name, "VICTORY", Some("Gary")));
    std::fs::write(folder.join(name), "rewritten").unwrap();
    assert!(rx.recv_timeout(Duration::from_secs(3)).is_err(), "once per file, and not for other names");
}

#[test]
fn updates_offer_install_and_restart() {
    use crate::updates::{Click, UpdateEvent, Updates};
    let tmp = tempfile::tempdir().unwrap();
    let s = Arc::new(session(tmp.path(), true));
    s.prepare_data_folder();
    let exe = s.paths().installed_exe;
    std::fs::write(&exe, b"old exe").unwrap();
    let new_exe = b"new exe bytes".to_vec();
    let hex: String = pab_core::hashing::sha256(&new_exe).iter().map(|b| format!("{b:02x}")).collect();
    let base = format!("https://github.com/{}/{}/releases", crate::REPO_OWNER, crate::REPO_NAME);
    let manifest = format!(
        r#"{{"version":"99.0.0","tag":"v99.0.0","assets":[{{"name":"PtcglAutoBattleLog.exe","url":"{base}/download/v99.0.0/PtcglAutoBattleLog.exe","size":{},"sha256":"{hex}"}}]}}"#,
        new_exe.len()
    );
    let net = Arc::new(move |url: &str, _download: bool| -> std::io::Result<Box<dyn std::io::Read>> {
        let body = if url.ends_with("update.json") { manifest.clone().into_bytes() } else { new_exe.clone() };
        Ok(Box::new(std::io::Cursor::new(body)))
    });
    let (tx, rx) = mpsc::channel();
    let started = Arc::new(std::sync::Mutex::new(Vec::<String>::new()));
    let st = started.clone();
    s.update(|x| x.auto_update = false);
    let updates = Updates::start(s.clone(), net, move |e| tx.send(e).unwrap(), || true, move |_exe: &Path, args: &[String]| {
        *crate::lock(&st) = args.to_vec();
        Ok(())
    })
    .unwrap();
    let next = || rx.recv_timeout(Duration::from_secs(10)).unwrap();

    updates.check_now();
    assert_eq!(next(), UpdateEvent::AvailableChanged);
    assert!(matches!(next(), UpdateEvent::Notify { click: Some(Click::Install), .. }), "autoUpdate off: offered");
    assert_eq!(updates.available().unwrap().version, "99.0.0");

    updates.install_available();
    assert_eq!(next(), UpdateEvent::Restart);
    assert_eq!(std::fs::read(&exe).unwrap(), b"new exe bytes");
    assert_eq!(*crate::lock(&started), ["--updated-from", crate::VERSION, "--portable"], "forced portable passes --portable on");

    assert!(updates.skip_available(&s) && s.read(|x| x.skipped_version.as_deref() == Some("99.0.0")));
}
