//! Ported from tests/TrayTests/AppTests.cs §17–21, §30, §31, StateTests.cs §15, UpdateTests.cs §13
//! and the ladder part of TcgkbTests.cs §34. The disclaimer goldens are the C# dialog text of the
//! repo's DISCLAIMER.md and THIRD-PARTY-NOTICES.md: after editing either, regenerate them
//! (tests/RustGoldens).

use std::cell::RefCell;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::Duration;

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use pab_core::command_line::{self, CommandLine};
use pab_core::daemon_monitor::*;
use pab_core::disclaimer;
use pab_core::game_install::InstallPiece::{self, *};
use pab_core::ladder_status::LadderStatus;
use pab_core::legacy_install::{self, LegacyInstall};
use pab_core::plugin_status::PluginStatus;
use pab_core::save_notification_queue::SaveNotificationQueue;
use pab_core::self_update;
use pab_core::tcgkb::{Account, Accounts, Ladder};
use pab_core::tray_log::TrayLog;
use pab_core::uninstall_flow::{self, UninstallHost, UninstallResult};
use pab_core::watched_folder::WatchedFolder;

fn repo(p: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").join(p)
}

#[test]
fn disclaimer_text() {
    assert_eq!(disclaimer::VERSION, 5);
    assert!(!disclaimer::is_accepted(0) && disclaimer::is_accepted(1) && disclaimer::is_accepted(6));
    for (md, golden) in [("DISCLAIMER.md", "disclaimer-plain.txt"), ("THIRD-PARTY-NOTICES.md", "notices-plain.txt")] {
        let md = fs::read_to_string(repo(md)).unwrap();
        let want = fs::read_to_string(repo("rust/pab-core/tests/golden").join(golden)).unwrap();
        assert_eq!(disclaimer::to_plain_text(&md), want, "{golden}: the C# dialog text (regenerate it if the .md changed)");
    }
    assert_eq!(disclaimer::to_plain_text(""), "");
    let sample = "\u{feff}# Title\r\n\r\nSome **bold** and *em* and `code`,\nwrapped line.\n\n* one\n  continued\n- two\n\n\
                  See [the site](https://example.com) and [LICENSE](LICENSE).\n<!-- hidden -->\n\n## Sub ##\n```\nkeep   this\n  as is\n```\nsnake_case and 2 * 3 * 4\n";
    let expected = ["Title", "Some bold and em and code, wrapped line.", "• one continued\r\n• two", "See the site (https://example.com) and LICENSE.", "Sub",
                    "keep   this\r\n  as is", "snake_case and 2 * 3 * 4"].join("\r\n\r\n");
    assert_eq!(disclaimer::to_plain_text(sample), expected);
    let table = "Before\n\n| Files | Component | License |\n|---|:---:|---|\n| `a.dll`, `b.dll` | [BepInEx](https://x.example) | **MIT** |\n| `c.dll` | Doorstop | LGPL-2.1 |\nAfter\n";
    assert_eq!(disclaimer::to_plain_text(table), "Before\r\n\r\nFiles | Component | License\r\na.dll, b.dll | BepInEx (https://x.example) | MIT\r\nc.dll | Doorstop | LGPL-2.1\r\n\r\nAfter");
}

/// The MSVCRT / CommandLineToArgvW rules for arguments after the program name.
fn parse_windows_args(s: &str) -> Vec<String> {
    let c: Vec<char> = s.chars().collect();
    let (mut args, mut cur, mut in_quotes, mut any) = (Vec::new(), String::new(), false, false);
    let mut i = 0;
    while i < c.len() {
        if c[i] == '\\' {
            let mut n = 0;
            while i < c.len() && c[i] == '\\' {
                n += 1;
                i += 1;
            }
            if i < c.len() && c[i] == '"' {
                cur.push_str(&"\\".repeat(n / 2));
                if n % 2 == 1 { cur.push('"') } else { in_quotes = !in_quotes }
                i += 1;
            } else {
                cur.push_str(&"\\".repeat(n));
            }
            any = true;
            continue;
        }
        if c[i] == '"' {
            in_quotes = !in_quotes;
            any = true;
        } else if (c[i] == ' ' || c[i] == '\t') && !in_quotes {
            if any {
                args.push(std::mem::take(&mut cur));
                any = false;
            }
        } else {
            cur.push(c[i]);
            any = true;
        }
        i += 1;
    }
    if any {
        args.push(cur);
    }
    args
}

#[test]
fn command_line() {
    let none = CommandLine::parse(Vec::<String>::new());
    assert_eq!(none, CommandLine::default());
    let all = CommandLine::parse(["--BACKGROUND", " --first-run ", "--Portable", "--uninstall", "--quiet", "--updated-from", "1.0.0", "/x", ""]);
    assert!(all.background && all.first_run && all.portable && all.uninstall && all.quiet && all.updated_from.as_deref() == Some("1.0.0"));
    assert_eq!(all.unknown, ["/x"]);
    let no_value = CommandLine::parse(["--updated-from", "--portable"]);
    assert!(no_value.updated() && no_value.updated_from.as_deref() == Some("") && no_value.portable);
    assert_eq!(CommandLine::parse(["--updated-from"]).updated_from.as_deref(), Some(""));
    assert_eq!(CommandLine::parse(["--updated-from=1.2.3"]).updated_from.as_deref(), Some("1.2.3"));

    assert_eq!(command_line::quote("1.1.0"), "1.1.0");
    assert_eq!(command_line::quote(""), "\"\"");
    assert_eq!(command_line::quote(r"C:\Program Files\x"), "\"C:\\Program Files\\x\"");
    assert_eq!(command_line::quote(r"C:\dir with space\"), "\"C:\\dir with space\\\\\"");
    assert_eq!(command_line::quote("say \"hi\""), "\"say \\\"hi\\\"\"");
    assert_eq!(command_line::quote("a\\\"b"), "\"a\\\\\\\"b\"");
    assert_eq!(command_line::join(["--updated-from", "1.1.0", "--portable"]), "--updated-from 1.1.0 --portable");
    assert_eq!(command_line::run_command(r"C:\Users\Ash\AppData\Local\PtcglAutoBattleLog\PtcglAutoBattleLog.exe", false),
               "\"C:\\Users\\Ash\\AppData\\Local\\PtcglAutoBattleLog\\PtcglAutoBattleLog.exe\" --background");
    assert_eq!(command_line::run_command(r"D:\Tools\abl.exe", true), "\"D:\\Tools\\abl.exe\" --background --portable");
    for arg in ["plain", "with space", "trail\\", "trail space\\", "q\"uote", "\\\\server\\share", "a\\\\\"b", ""] {
        assert_eq!(parse_windows_args(&command_line::quote(arg)), [arg], "round trip by Windows rules: {arg}");
    }
}

#[test]
fn legacy_install() {
    let tmp = tempfile::tempdir().unwrap();
    let root = tmp.path().join("PtcglAutoBattleLog");
    fs::create_dir_all(&root).unwrap();
    let legacy = LegacyInstall::new(&root);
    assert!(!legacy.exists() && legacy.read_game_dir().is_none() && !legacy.bepinex_installed_by_us(r"C:\Game"));

    let game = r"C:\Users\Ash\The Pokémon Company International\Pokémon Trading Card Game Live";
    fs::write(root.join("game-dir.txt"), format!("\u{feff}{game}\r\n")).unwrap();
    fs::write(root.join("bepinex-installed-by-us.flag"), format!("\u{feff}{game}\r\n")).unwrap();
    fs::create_dir_all(root.join("payload")).unwrap();
    fs::write(root.join("payload/PtcglAutoBattleLog.dll"), "MZ").unwrap();
    for f in ["watchdog.ps1", "common.ps1", "uninstall.ps1", "watchdog.log", "watchdog.log.old"] {
        fs::write(root.join(f), "x").unwrap();
    }
    fs::write(root.join("settings.json"), "{}").unwrap();
    assert!(legacy.exists());
    assert_eq!(legacy.read_game_dir().as_deref(), Some(game));
    assert!(legacy.bepinex_installed_by_us(game) && legacy.bepinex_installed_by_us(&format!("{}\\", game.to_uppercase())));
    assert!(!legacy.bepinex_installed_by_us(r"D:\Other"));
    fs::write(root.join("bepinex-installed-by-us.flag"), "").unwrap();
    assert!(legacy.bepinex_installed_by_us(r"D:\Other"), "an empty flag counts for any folder");
    assert!(legacy.remove_leftovers().is_empty() && !legacy.exists());
    assert_eq!(fs::read_dir(&root).unwrap().count(), 1, "settings.json kept");
    assert!(legacy.remove_leftovers().is_empty());
    fs::write(root.join("game-dir.txt"), "\r\n   \r\n").unwrap();
    assert!(legacy.read_game_dir().is_none());

    let cmd = "\"C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"C:\\Users\\Ash\\AppData\\Local\\PtcglAutoBattleLog\\watchdog.ps1\"";
    assert!(legacy_install::is_watchdog_command_line(cmd) && legacy_install::is_watchdog_command_line(&cmd.to_lowercase()));
    for other in ["powershell.exe -File C:\\other\\watchdog.ps1", "powershell.exe -File C:\\x\\PtcglAutoBattleLog\\install.ps1", "watchdog.ps1 PtcglAutoBattleLog", ""] {
        assert!(!legacy_install::is_watchdog_command_line(other), "{other}");
    }
}

fn t0() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 10, 19, 20, 0, 0).unwrap()
}

fn beat(at: DateTime<Utc>, state: &str, saved: i64, pid: i64, message: Option<&str>) -> PluginStatus {
    PluginStatus { state: Some(state.into()), heartbeat_utc: Some(at.format("%Y-%m-%dT%H:%M:%SZ").to_string()), saved_this_session: Some(saved), process_id: Some(pid),
                   message: message.map(String::from), ..Default::default() }
}

fn ok(at: DateTime<Utc>) -> PluginStatus {
    beat(at, "ok", 0, 100, None)
}

fn obs(sec: i64, running: bool, missing: &[InstallPiece], plugin_file: bool, status: Option<PluginStatus>) -> DaemonObservation {
    DaemonObservation { now: t0() + TimeDelta::seconds(sec), game_dir: Some(r"C:\Game".into()), game_running: running, missing: missing.to_vec(),
                        plugin_file_present: plugin_file, status, consent_pending: false }
}

fn pending(mut o: DaemonObservation) -> DaemonObservation {
    o.consent_pending = true;
    o
}

#[test]
fn daemon_monitor() {
    let all = [WinHttp, DoorstopConfig, BepInExPreloader, BepInExCore, Plugin];
    let at = |s: i64| t0() + TimeDelta::seconds(s);

    let mut m = DaemonMonitor::default();
    let d = m.evaluate(&obs(0, false, &[], true, None));
    assert!(d.health == TrayHealth::Idle && !d.repair && d.notice == DaemonNotice::None && d.summary.starts_with("Ready"));
    assert_eq!(m.evaluate(&DaemonObservation { game_dir: None, ..obs(0, false, &[], true, None) }).health, TrayHealth::Attention);

    let mut m = DaemonMonitor::default();
    let d = m.evaluate(&obs(0, false, &all, false, None));
    assert!(!d.repair && d.health == TrayHealth::Attention && d.notice == DaemonNotice::None, "first sighting: wait");
    assert!(!m.evaluate(&obs(3, false, &all, false, None)).repair);
    assert!(m.evaluate(&obs(5, false, &all, false, None)).repair, "5 s later: repair");
    m.repair_failed("Access denied", at(5));
    let d = m.evaluate(&obs(10, false, &all, false, None));
    assert!(!d.repair && d.health == TrayHealth::Error && d.summary.contains("Access denied"));
    assert!(m.evaluate(&obs(35, false, &all, false, None)).repair, "retried 30 s after the failure");
    m.repair_succeeded();
    let d = m.evaluate(&obs(40, false, &[], true, None));
    assert!(!d.repair && d.health == TrayHealth::Idle && m.last_repair_error.is_none());

    let mut m = DaemonMonitor::default();
    m.evaluate(&obs(0, false, &all, false, None));
    let d = m.evaluate(&obs(5, true, &all, false, None));
    assert!(!d.repair && d.notice == DaemonNotice::GameNeedsRestart && d.health == TrayHealth::Attention);
    let d = m.evaluate(&obs(10, true, &all, false, None));
    assert!(!d.repair && d.notice == DaemonNotice::None, "notice once");
    assert!(!m.evaluate(&obs(15, false, &all, false, None)).repair, "closed: wait again first");
    assert!(m.evaluate(&obs(20, false, &all, false, None)).repair);
    m.repair_succeeded();
    m.evaluate(&obs(25, false, &[], true, None));
    assert_eq!(m.evaluate(&obs(30, true, &all, false, None)).notice, DaemonNotice::GameNeedsRestart, "a new episode notifies again");

    let mut m = DaemonMonitor::default();
    let d = m.evaluate(&obs(0, true, &[Plugin], true, Some(ok(t0()))));
    assert!(d.notice == DaemonNotice::ModUpdateReady && d.health == TrayHealth::Active && !d.repair && d.summary.ends_with(RESTART_TO_UPDATE_SUFFIX), "{}", d.summary);
    let d = m.evaluate(&obs(5, true, &[Plugin], true, Some(ok(at(5)))));
    assert!(d.notice == DaemonNotice::None && d.summary.ends_with(RESTART_TO_UPDATE_SUFFIX));
    let d = m.evaluate(&obs(60, false, &[Plugin], true, None));
    assert!(!d.repair && d.summary.starts_with("Updating"));
    assert!(m.evaluate(&obs(65, false, &[Plugin], true, None)).repair);
    m.repair_succeeded();
    let d = m.evaluate(&obs(70, true, &[], true, Some(ok(at(70)))));
    assert!(d.health == TrayHealth::Active && !d.summary.contains("restart"));
    assert_eq!(m.evaluate(&obs(80, true, &[Plugin], true, Some(ok(at(80))))).notice, DaemonNotice::ModUpdateReady);
    let d = DaemonMonitor::default().evaluate(&pending(obs(0, true, &[Plugin], true, Some(ok(t0())))));
    assert!(d.notice == DaemonNotice::None && d.summary.contains("disclaimer"));
    assert_eq!(DaemonMonitor::default().evaluate(&obs(0, true, &[Plugin], false, None)).notice, DaemonNotice::GameNeedsRestart);

    let mut m = DaemonMonitor::default();
    let d = m.evaluate(&obs(10, true, &[], true, Some(beat(t0(), "ok", 2, 100, None))));
    assert!(d.health == TrayHealth::Active && d.plugin_alive && d.summary.contains("2 logs this session"), "{}", d.summary);
    assert!(m.evaluate(&obs(10, true, &[], true, Some(beat(t0(), "ok", 1, 100, None)))).summary.contains("1 log this session"));
    assert!(m.evaluate(&obs(10, true, &[], true, Some(beat(t0(), "starting", 0, 100, None)))).summary.starts_with("Mod starting"));
    let d = m.evaluate(&obs(200, false, &[], true, Some(ok(t0()))));
    assert!(d.health == TrayHealth::Idle && !d.plugin_alive);

    let mut m = DaemonMonitor::default();
    let d = m.evaluate(&obs(0, true, &[], true, None));
    assert!(d.health == TrayHealth::Idle && d.notice == DaemonNotice::None && d.summary.starts_with("Game starting"));
    assert_eq!(m.evaluate(&obs(60, true, &[], true, None)).notice, DaemonNotice::None);
    let d = m.evaluate(&obs(90, true, &[], true, None));
    assert!(d.notice == DaemonNotice::RunningWithoutMod && d.health == TrayHealth::Attention);
    let d = m.evaluate(&obs(95, true, &[], true, None));
    assert!(d.notice == DaemonNotice::None && d.health == TrayHealth::Attention);
    m.evaluate(&obs(100, false, &[], true, None));
    m.evaluate(&obs(105, true, &[], true, None));
    assert_eq!(m.evaluate(&obs(200, true, &[], true, None)).notice, DaemonNotice::RunningWithoutMod, "next game session");
    let d = m.evaluate(&obs(205, true, &[], true, Some(ok(at(204)))));
    assert!(d.health == TrayHealth::Active && d.notice == DaemonNotice::None);

    let mut m = DaemonMonitor::default();
    let no_game = |s| DaemonObservation { game_dir: None, ..obs(s, true, &[], true, None) };
    m.evaluate(&no_game(0));
    let d = m.evaluate(&no_game(120));
    assert!(d.notice == DaemonNotice::None && d.health == TrayHealth::Attention);

    let mut m = DaemonMonitor::default();
    let d = m.evaluate(&obs(10, true, &[], true, Some(beat(t0(), "error", 0, 100, Some("Game classes not found.")))));
    assert!(d.health == TrayHealth::Error && d.notice == DaemonNotice::PluginError && d.summary.contains("Game classes not found."));
    let d = m.evaluate(&obs(15, true, &[], true, Some(beat(at(15), "error", 0, 100, None))));
    assert!(d.notice == DaemonNotice::None && d.summary.contains("BepInEx log"));
    assert_eq!(m.evaluate(&obs(300, true, &[], true, Some(beat(at(300), "error", 0, 200, None)))).notice, DaemonNotice::PluginError, "new game process");

    let mut m = DaemonMonitor::default();
    m.evaluate(&pending(obs(0, false, &[Plugin], true, None)));
    let d = m.evaluate(&pending(obs(60, false, &[Plugin], true, None)));
    assert!(!d.repair && d.health == TrayHealth::Attention && d.summary.contains("disclaimer"));
    let d = m.evaluate(&pending(obs(65, true, &all, false, None)));
    assert!(!d.repair && d.notice == DaemonNotice::None);
    m.evaluate(&pending(obs(70, false, &all, false, None)));
    assert!(m.evaluate(&obs(80, false, &all, false, None)).repair, "accepted: repaired at the next tick");

    let mut m = DaemonMonitor::default();
    let d = m.evaluate(&pending(obs(0, false, &[], true, None)));
    assert!(d.health == TrayHealth::Attention && d.summary == CONSENT_PENDING_SUMMARY);
    let d = m.evaluate(&pending(obs(10, true, &[], true, Some(beat(at(10), "ok", 2, 100, None)))));
    assert!(d.health == TrayHealth::Attention && d.summary == CONSENT_PENDING_SUMMARY && d.plugin_alive);
    assert_eq!(m.evaluate(&obs(20, true, &[], true, Some(beat(at(20), "ok", 2, 100, None)))).health, TrayHealth::Active);

    let mut m = DaemonMonitor::default();
    m.setup_pending();
    let d = m.evaluate(&obs(0, true, &all, false, None));
    assert!(d.notice == DaemonNotice::None && d.health == TrayHealth::Attention && d.summary.contains("finish installing"));
    let d = m.evaluate(&obs(10, false, &all, false, None));
    assert!(d.summary.starts_with("Installing") && !d.repair);
    assert!(m.evaluate(&obs(15, false, &all, false, None)).repair);
    m.repair_succeeded();
    m.evaluate(&obs(20, false, &[], true, None));
    let d = m.evaluate(&obs(30, true, &all, false, None));
    assert!(d.notice == DaemonNotice::GameNeedsRestart && d.summary.starts_with("Mod missing"));
}

#[derive(Default)]
struct FakeUninstall {
    steps: RefCell<Vec<String>>,
    quiet: bool,
    portable: bool,
    refuses: bool,
    game_running: bool,
    gives_up_on_game: bool,
    tray_stays: bool,
    left: Option<String>,
    result: Option<(UninstallResult, Option<String>)>,
}

impl FakeUninstall {
    fn step(&self, s: &str) {
        self.steps.borrow_mut().push(s.into());
    }
    fn run(&mut self) -> (i32, String) {
        let code = uninstall_flow::run(self);
        (code, self.steps.borrow().join(" > "))
    }
}

impl UninstallHost for FakeUninstall {
    fn quiet(&self) -> bool { self.quiet }
    fn portable(&self) -> bool { self.portable }
    fn confirm(&mut self) -> bool { self.step("confirm"); !self.refuses }
    fn is_game_running(&mut self) -> bool { self.step("game?"); self.game_running }
    fn wait_for_game_to_close(&mut self) -> bool { self.step("wait game"); !self.gives_up_on_game }
    fn stop_tray(&mut self) -> bool { self.step("stop tray"); !self.tray_stays }
    fn release_tray(&mut self) { self.step("release mutex") }
    fn stop_legacy(&mut self) { self.step("legacy") }
    fn remove_from_game(&mut self) -> Option<String> { self.step("game files"); self.left.clone() }
    fn remove_registration(&mut self) { self.step("registration") }
    fn delete_install_folder(&mut self) { self.step("install folder") }
    fn report(&mut self, result: UninstallResult, detail: Option<&str>) {
        self.step(&format!("report {result:?}"));
        self.result = Some((result, detail.map(String::from)));
    }
}

#[test]
fn uninstall_flow() {
    let (code, steps) = FakeUninstall::default().run();
    assert_eq!((code, steps.as_str()), (0, "confirm > wait game > stop tray > legacy > game files > registration > release mutex > report Done > install folder"));
    let (code, steps) = FakeUninstall { portable: true, ..Default::default() }.run();
    assert!(code == 0 && !steps.contains("install folder"));
    assert_eq!(FakeUninstall { refuses: true, ..Default::default() }.run(), (1, "confirm > report Cancelled".into()));
    assert_eq!(FakeUninstall { gives_up_on_game: true, ..Default::default() }.run(), (1, "confirm > wait game > report Cancelled".into()));
    assert_eq!(FakeUninstall { quiet: true, ..Default::default() }.run(),
               (0, "game? > stop tray > legacy > game files > registration > release mutex > report Done > install folder".into()));
    assert_eq!(FakeUninstall { quiet: true, game_running: true, ..Default::default() }.run(), (1, "game? > report GameRunning".into()));
    assert_eq!(FakeUninstall { tray_stays: true, ..Default::default() }.run(), (1, "confirm > wait game > stop tray > report TrayStillRunning > release mutex".into()));
    let mut h = FakeUninstall { left: Some("winhttp.dll (in use)".into()), ..Default::default() };
    let (code, steps) = h.run();
    assert!(code == 1 && h.result == Some((UninstallResult::GameFilesLeft, Some("winhttp.dll (in use)".into()))) && !steps.contains("registration") && !steps.contains("install folder"));
}

#[test]
fn watched_folder() {
    let tmp = tempfile::tempdir().unwrap();
    let logs = tmp.path().join("PTCGL Battle Logs");
    fs::create_dir_all(&logs).unwrap();
    let w = WatchedFolder::new(&logs);
    assert!(w.path == logs && w.still_there());
    let renamed = tmp.path().join("renamed in Explorer");
    fs::rename(&logs, &renamed).unwrap();
    assert!(!w.still_there(), "renamed away");
    fs::rename(&renamed, &logs).unwrap();
    assert!(w.still_there(), "renamed back");
    let created = fs::metadata(&logs).unwrap().created().ok();
    fs::remove_dir(&logs).unwrap();
    std::thread::sleep(Duration::from_millis(20));
    fs::create_dir_all(&logs).unwrap();
    if fs::metadata(&logs).unwrap().created().ok() != created {
        assert!(!w.still_there(), "made again at the same path: another folder");
    }
    assert!(!WatchedFolder::new(&tmp.path().join("missing")).still_there());
}

#[test]
fn save_notification_queue() {
    let dir = Path::new("logs");
    let present = RefCell::new(Vec::<PathBuf>::new());
    let exists = |p: &Path| present.borrow().iter().any(|x| x == p);
    let mut q = SaveNotificationQueue::new(Duration::from_millis(1500), 3);
    let at = |ms: i64| t0() + TimeDelta::milliseconds(ms);
    let p = |n: &str| dir.join(n);

    let victory = p("2026-10-19 21.34.05 - VICTORY vs Gary.txt");
    for ignored in ["2026-10-19 21.34.05 - VICTORY vs Gary.txt.tmp", "index.csv", "2026-10-19 21.34.05 - VICTORY vs Gary.seal", "notes.txt"] {
        assert!(!q.add(&p(ignored), t0()), "{ignored}");
    }
    assert!(q.add(&victory, t0()) && q.pending_count() == 1);
    present.borrow_mut().push(victory.clone());
    assert!(q.take_due(at(1000), &exists).is_empty(), "not before the delay");
    q.add(&victory, at(1000));
    assert!(q.take_due(at(2000), &exists).is_empty(), "a new event restarts the delay");
    assert_eq!(q.take_due(at(2500), &exists), std::slice::from_ref(&victory));
    assert!(q.take_due(at(10_000), &exists).is_empty(), "only once");
    assert!(!q.add(&victory, at(20_000)) && q.pending_count() == 0, "never again");

    let d0 = p("2026-10-19 22.00.00 - NO-RESULT vs Misty.txt");
    let d1 = p("2026-10-19 22.00.00 - DEFEAT vs Misty.txt");
    q.add(&d0, at(30_000));
    q.add(&d1, at(30_500));
    present.borrow_mut().push(d1.clone());
    assert_eq!(q.take_due(at(32_500), &exists), [d1], "only the name that still exists");

    let a = p("2026-10-19 23.00.00 - VICTORY vs A.txt");
    let b = p("2026-10-19 23.00.01 - EXPORTED.txt");
    present.borrow_mut().extend([a.clone(), b.clone()]);
    q.add(&b, at(40_000));
    q.add(&a, at(40_000));
    assert_eq!(q.take_due(at(45_000), &exists), [a, b], "name order");
    assert!(q.add(&victory, at(50_000)), "oldest announced name forgotten past the limit (3)");
    q.clear_pending();
    assert_eq!(q.pending_count(), 0);
    let _ = SaveNotificationQueue::default().take_due(t0(), &|p: &Path| p.is_file());
}

#[test]
fn tray_log() {
    let tmp = tempfile::tempdir().unwrap();
    let path = tmp.path().join("sub").join("tray.log");
    let log = TrayLog::new(&path, 0);
    log.info("hello");
    log.error("it failed: boom");
    let text = fs::read_to_string(&path).unwrap();
    let lines: Vec<&str> = text.lines().collect();
    let shape = regex::Regex::new(r"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} (INFO |ERROR) ").unwrap();
    assert!(lines.len() == 2 && shape.is_match(lines[0]) && lines[0].ends_with("INFO  hello") && lines[1].ends_with("ERROR it failed: boom"), "{text}");

    let small = tmp.path().join("small.log");
    let sl = TrayLog::new(&small, 4096);
    for i in 0..500 {
        sl.info(&format!("line {i:04} {}", ".".repeat(40)));
    }
    let rotated = tmp.path().join("small.log.1");
    assert!(rotated.is_file() && fs::metadata(&small).unwrap().len() <= 4096 && fs::metadata(&rotated).unwrap().len() <= 4096);
    assert!(fs::read_to_string(&small).unwrap().lines().last().unwrap().contains("line 0499"));
    assert!(!tmp.path().join("small.log.2").exists());

    let conc = tmp.path().join("conc.log");
    let (a, b) = (TrayLog::new(&conc, 0), TrayLog::new(&conc, 0));
    std::thread::scope(|s| {
        for t in 0..8 {
            let (a, b) = (&a, &b);
            s.spawn(move || {
                for i in 0..400 {
                    (if i % 2 == 0 { a } else { b }).info(&format!("t{t} i{i} {}", "x".repeat(t * 10)));
                }
            });
        }
    });
    let text = fs::read_to_string(&conc).unwrap();
    let well_formed = regex::Regex::new(r"^\S+ \S+ INFO  t(\d) i(\d+) (x*)$").unwrap();
    assert_eq!(text.lines().count(), 3200);
    assert!(text.lines().all(|l| well_formed.captures(l).is_some_and(|c| c[3].len() == c[1].parse::<usize>().unwrap() * 10)), "no torn lines");
}

#[test]
fn self_update() {
    let tmp = tempfile::tempdir().unwrap();
    let installed = tmp.path().join("PtcglAutoBattleLog.exe");
    let old = tmp.path().join("PtcglAutoBattleLog.exe.old");
    let updates = tmp.path().join("updates");
    fs::create_dir_all(&updates).unwrap();
    let new_exe = |c: &str| {
        let p = updates.join("PtcglAutoBattleLog-1.2.0.exe");
        fs::write(&p, c).unwrap();
        p
    };
    let read = |p: &Path| fs::read_to_string(p).unwrap();

    fs::write(&installed, "v1.1").unwrap();
    let n = new_exe("v1.2");
    self_update::swap(&n, &installed).unwrap();
    assert!(read(&installed) == "v1.2" && read(&old) == "v1.1" && !n.exists());
    self_update::swap(&new_exe("v1.3"), &installed).unwrap();
    assert!(read(&installed) == "v1.3" && read(&old) == "v1.2", "stale .old replaced");
    self_update::cleanup_old(&installed);
    assert!(!old.exists());
    self_update::cleanup_old(&installed);
    self_update::cleanup_old(Path::new(""));

    assert!(self_update::swap(&updates.join("missing.exe"), &installed).is_err() && read(&installed) == "v1.3" && !old.exists());
    assert!(self_update::swap(&installed, &installed).is_err() && read(&installed) == "v1.3");
    let fresh = tmp.path().join("fresh").join("PtcglAutoBattleLog.exe");
    fs::create_dir_all(fresh.parent().unwrap()).unwrap();
    self_update::swap(&new_exe("first"), &fresh).unwrap();
    assert!(read(&fresh) == "first" && !fresh.with_extension("exe.old").exists());

    let n = new_exe("v1.4");
    let fail_new = |src: &Path, dst: &Path| if src == n { Err(std::io::Error::other("disk full")) } else { fs::rename(src, dst) };
    assert!(self_update::swap_with(&n, &installed, &fail_new).is_err());
    assert!(read(&installed) == "v1.3" && !old.exists() && n.exists(), "rolled back, download kept");
    let fail_all_but_first = |src: &Path, dst: &Path| if src == installed { fs::rename(src, dst) } else { Err(std::io::Error::other("locked")) };
    assert!(self_update::swap_with(&n, &installed, &fail_all_but_first).is_err() && read(&installed) == "v1.3", "rollback by copying");
}

#[test]
fn ladder_status() {
    let me = |accounts: Vec<Account>, ladder: Option<Ladder>| Accounts { accounts, ladder, ..Default::default() };
    let claimed = || Account { status: Some("verified".into()), ..Default::default() };
    assert!(LadderStatus::from(Some(&me(vec![claimed()], None))).is_none() && LadderStatus::from(None).is_none());
    let l = LadderStatus::from(Some(&me(vec![claimed()], Some(Ladder { games: 3, min_games: 5, rank: None, players: 12 })))).unwrap();
    assert!(!l.ranked && l.has_account && l.title == "Ladder: 3 of 5 matches" && l.count == "2 more to go" && l.why.contains("Not ranked yet") && (l.progress - 0.6).abs() < 1e-9);
    let l = LadderStatus::from(Some(&me(vec![claimed()], Some(Ladder { games: 9, min_games: 5, rank: Some(4), players: 12 })))).unwrap();
    assert!(l.ranked && l.count == "#4 of 12" && l.progress == 1.0);
    let l = LadderStatus::from(Some(&me(vec![], Some(Ladder { min_games: 5, ..Default::default() })))).unwrap();
    assert!(!l.has_account && l.why.starts_with("Play a match"));
    let disputed = Account { status: Some("disputed".into()), ..Default::default() };
    let l = LadderStatus::from(Some(&me(vec![disputed], Some(Ladder { min_games: 5, ..Default::default() })))).unwrap();
    assert!(!l.has_account && l.why.contains("claims your PTCGL account too"));
}
