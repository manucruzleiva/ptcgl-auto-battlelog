//! Ported from tests/TrayTests/InstallTests.cs §2, §4, §5, §32 and StateTests.cs §7. The real
//! BepInEx zip is not in the repo, so a small zip with the same layout stands in for it; the cfg
//! string rules are checked against tests/golden/bepinex-config.json, written by the C# code (whose
//! own tests compare it with the real BepInEx.dll).

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use chrono::{TimeDelta, TimeZone, Utc};
use pab_core::bepinex_config::*;
use pab_core::file_ops::{delete_tree_except, is_same_or_under};
use pab_core::game_install::*;
use pab_core::game_locator::{self, GAME_EXE};
use pab_core::plugin_status::PluginStatus;
use serde_json::Value;

const PLUGIN_V2: &[u8] = b"MZ fake plugin build 2 xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
const PLUGIN_V1: &[u8] = b"MZ fake plugin build 1";
const ZIP_FILES: [&str; 7] = ["winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt", "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/BepInEx.dll", "BepInEx/core/0Harmony.dll"];

fn zip_of(entries: &[(&str, &[u8])], dirs: &[&str]) -> Vec<u8> {
    let mut w = zip::ZipWriter::new(std::io::Cursor::new(Vec::new()));
    let opts = zip::write::SimpleFileOptions::default();
    for d in dirs {
        w.add_directory(*d, opts).unwrap();
    }
    for (name, bytes) in entries {
        w.start_file(*name, opts).unwrap();
        w.write_all(bytes).unwrap();
    }
    w.finish().unwrap().into_inner()
}

fn bepinex_zip() -> Vec<u8> {
    let entries: Vec<(&str, &[u8])> = ZIP_FILES.iter().map(|n| (*n, n.as_bytes())).collect();
    zip_of(&entries, &["BepInEx/", "BepInEx/plugins/"])
}

fn fake_game(tmp: &Path, name: &str) -> PathBuf {
    let g = tmp.join(name).join("Pokemon TCG Live");
    fs::create_dir_all(&g).unwrap();
    fs::write(g.join(GAME_EXE), "game").unwrap();
    g
}

/// Files under `root`, '/' separated, sorted.
fn files_under(root: &Path) -> Vec<String> {
    fn walk(dir: &Path, root: &Path, out: &mut Vec<String>) {
        for e in fs::read_dir(dir).unwrap().flatten() {
            if e.path().is_dir() {
                walk(&e.path(), root, out)
            } else {
                out.push(e.path().strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/"));
            }
        }
    }
    let mut out = Vec::new();
    if root.is_dir() {
        walk(root, root, &mut out);
    }
    out.sort();
    out
}

#[test]
fn game_locator() {
    let tmp = tempfile::tempdir().unwrap();
    let game = fake_game(tmp.path(), "a");
    let not_game = tmp.path().join("other");
    fs::create_dir_all(&not_game).unwrap();
    assert!(game_locator::is_game_dir(&game));
    assert!(!game_locator::is_game_dir(&not_game) && !game_locator::is_game_dir(Path::new("")) && !game_locator::is_game_dir(&not_game.join("nope")));
    assert!(!game_locator::is_game_dir(Path::new("bad\"<>|path")), "invalid characters are just not the game");

    let c = game_locator::default_candidates("/u", "", "/pf", " ");
    assert_eq!(c, [Path::new("/u").join("The Pokémon Company International").join("Pokémon Trading Card Game Live"),
                   Path::new("/pf").join("The Pokémon Company International").join("Pokémon Trading Card Game Live")]);
    assert_eq!(game_locator::default_candidates("/x", "/X", "/x", "/x").len(), 1, "duplicates removed, ignoring case");

    let (g, n) = (game.to_str().unwrap(), not_game.to_str().unwrap());
    let exe = game.join(GAME_EXE);
    let icon = format!("\"{}\",0", exe.display());
    assert_eq!(game_locator::find([g, n]).as_deref(), Some(game.as_path()), "saved folder first");
    assert_eq!(game_locator::find([n, n, g]).as_deref(), Some(game.as_path()), "the first valid one");
    assert_eq!(game_locator::find(["", icon.as_str()]).as_deref(), Some(game.as_path()), "DisplayIcon with quotes and an index");
    assert_eq!(game_locator::find([format!("  {g}  ").as_str()]).as_deref(), Some(game.as_path()), "trimmed");
    assert_eq!(game_locator::find([exe.to_str().unwrap()]).as_deref(), Some(game.as_path()), "an exe path is its folder");
    assert_eq!(game_locator::find([n, "garbage\""]), None);
    assert_eq!(game_locator::clean(r#""C:\Games\PTCGL\Pokemon TCG Live.exe",-1"#), Some(PathBuf::from(r"C:\Games\PTCGL")));
    assert_eq!(game_locator::clean("Pokemon TCG Live.exe"), None);
}

#[test]
fn install_repair_uninstall() {
    let tmp = tempfile::tempdir().unwrap();
    let zip = bepinex_zip();
    let payload = Payload { plugin_dll: PLUGIN_V2, bepinex_zip: &zip };
    use InstallPiece::*;
    let all = [WinHttp, DoorstopConfig, BepInExPreloader, BepInExCore, Plugin];

    let g = fake_game(tmp.path(), "g1");
    let gi = GameInstall::new(&g);
    assert_eq!(gi.plugin_path, g.join("BepInEx").join("plugins").join("PtcglAutoBattleLog").join("PtcglAutoBattleLog.Plugin.dll"));
    assert_eq!(gi.config_path, g.join("BepInEx").join("config").join("ptcgl.autobattlelog.cfg"));
    assert_eq!(gi.bepinex_log_path, g.join("BepInEx").join("LogOutput.log"));
    assert!(!gi.has_bepinex());
    assert_eq!(gi.missing(&payload), all);
    assert_eq!(gi.repair(&payload).unwrap(), all, "repair returns what was missing before");
    assert!(gi.missing(&payload).is_empty() && gi.has_bepinex());
    for f in ZIP_FILES {
        assert_eq!(fs::read(g.join(f)).unwrap(), f.as_bytes(), "{f} extracted exactly");
    }
    assert_eq!(fs::read(&gi.plugin_path).unwrap(), PLUGIN_V2);
    assert!(!files_under(&g).iter().any(|f| f.ends_with(".tmp")), "no .tmp files left");
    let stamp = |f: &str| fs::metadata(g.join(f)).unwrap().modified().unwrap();
    let before: Vec<_> = files_under(&g).iter().map(|f| (f.clone(), stamp(f))).collect();
    assert!(gi.repair(&payload).unwrap().is_empty(), "second repair does nothing");
    assert_eq!(files_under(&g).iter().map(|f| (f.clone(), stamp(f))).collect::<Vec<_>>(), before, "files untouched");

    fs::write(&gi.plugin_path, PLUGIN_V1).unwrap();
    assert_eq!(gi.missing(&payload), [Plugin], "an older plugin is reported");
    let broken = Payload { plugin_dll: PLUGIN_V2, bepinex_zip: b"not a zip" };
    assert_eq!(gi.repair(&broken).unwrap(), [Plugin], "only the plugin replaced, the zip not opened");
    assert_eq!(fs::read(&gi.plugin_path).unwrap(), PLUGIN_V2);

    fs::remove_file(g.join("changelog.txt")).unwrap();
    fs::remove_file(&gi.plugin_path).unwrap();
    assert_eq!(gi.missing(&payload), [Plugin]);
    gi.repair(&payload).unwrap();
    assert!(gi.plugin_path.is_file() && !g.join("changelog.txt").exists(), "a complete loader is left alone");

    let legacy = gi.plugin_dir().join("PtcglAutoBattleLog.dll");
    fs::write(&legacy, PLUGIN_V1).unwrap();
    assert_eq!(gi.missing(&payload), [Plugin], "1.0's dll beside the plugin");
    gi.repair(&payload).unwrap();
    assert!(!legacy.exists() && gi.missing(&payload).is_empty(), "legacy dll deleted");

    // A game update wiped the loader but left the player's own doorstop_config.ini and core file.
    let g2 = fake_game(tmp.path(), "g2");
    let gi2 = GameInstall::new(&g2);
    fs::write(g2.join("doorstop_config.ini"), "[General]\nenabled = true\n# mine\n").unwrap();
    fs::create_dir_all(g2.join("BepInEx/core")).unwrap();
    fs::write(g2.join("BepInEx/core/BepInEx.dll"), "player's own build").unwrap();
    assert_eq!(gi2.missing(&payload), [WinHttp, BepInExPreloader, Plugin]);
    gi2.repair(&payload).unwrap();
    assert_eq!(fs::read_to_string(g2.join("doorstop_config.ini")).unwrap(), "[General]\nenabled = true\n# mine\n");
    assert_eq!(fs::read_to_string(g2.join("BepInEx/core/BepInEx.dll")).unwrap(), "player's own build", "never overwritten");
    assert!(g2.join("winhttp.dll").is_file() && gi2.missing(&payload).is_empty());

    let gone = tmp.path().join("uninstalled");
    assert!(GameInstall::new(&gone).repair(&payload).is_err() && !gone.exists(), "a folder without the game is refused");

    let root = tmp.path().join("root");
    assert_eq!(map_entry(&root, "BepInEx/core/BepInEx.dll").unwrap(), Some(root.join("BepInEx").join("core").join("BepInEx.dll")));
    assert_eq!(map_entry(&root, "BepInEx\\core\\x.dll").unwrap(), Some(root.join("BepInEx").join("core").join("x.dll")));
    assert_eq!(map_entry(&root, "BepInEx/plugins/").unwrap(), None);
    for bad in ["../evil.dll", "BepInEx/../../evil.dll", "/etc/evil", "\\evil.dll", "C:/Windows/evil.dll", "C:evil.dll", "BepInEx\\..\\..\\evil.dll",
                "a/./b.dll", "a//b.dll", "...", "a/.../b", "dir./x", "dir /x", "x.dll ", "a/b.dll:ads", "con?.dll", "a<b", "a|b", "a\"b", "a*b", "tab\tname", "nul\0"] {
        assert_eq!(map_entry(&root, bad).unwrap_err().kind(), std::io::ErrorKind::InvalidData, "{bad:?}");
    }
    let evil = zip_of(&[("winhttp.dll", b"x"), ("../../escaped.txt", b"x")], &[]);
    let g3 = fake_game(tmp.path(), "g3");
    assert!(GameInstall::new(&g3).repair(&Payload { plugin_dll: PLUGIN_V2, bepinex_zip: &evil }).is_err());
    assert!(!g3.join("winhttp.dll").exists() && !tmp.path().join("escaped.txt").exists(), "nothing extracted from a bad zip");

    // Other mods, then uninstall.
    let g4 = fake_game(tmp.path(), "g4");
    let gi4 = GameInstall::new(&g4);
    gi4.repair(&payload).unwrap();
    assert!(!gi4.has_other_plugins());
    let other = g4.join("BepInEx/plugins/OtherMod");
    fs::create_dir_all(other.join("lib")).unwrap();
    fs::write(other.join("readme.txt"), "x").unwrap();
    assert!(!gi4.has_other_plugins(), "a folder without dlls does not count");
    fs::write(other.join("lib/OtherMod.dll"), "x").unwrap();
    assert!(gi4.has_other_plugins(), "a nested dll in another plugin folder counts");
    fs::remove_dir_all(&other).unwrap();
    fs::write(gi4.plugin_dir().join("Extra.dll"), "x").unwrap();
    assert!(!gi4.has_other_plugins(), "dlls in our own folder do not count");
    fs::write(g4.join("BepInEx/plugins/Loose.dll"), "x").unwrap();
    assert!(gi4.has_other_plugins());
    fs::remove_file(g4.join("BepInEx/plugins/Loose.dll")).unwrap();
    fs::create_dir_all(g4.join("BepInEx/patchers")).unwrap();
    fs::write(g4.join("BepInEx/patchers/Patcher.dll"), "x").unwrap();
    assert!(gi4.has_other_plugins(), "a preloader patcher counts");
    assert!(!GameInstall::new(&tmp.path().join("none")).has_other_plugins());
    gi4.uninstall(true).unwrap();
    assert!(!gi4.plugin_dir().exists() && !gi4.config_path.exists());
    assert!(g4.join("winhttp.dll").is_file() && g4.join("BepInEx/patchers/Patcher.dll").is_file(), "BepInEx kept: another mod uses it");

    let g5 = fake_game(tmp.path(), "g5");
    let gi5 = GameInstall::new(&g5);
    gi5.repair(&payload).unwrap();
    fs::write(g5.join("player-notes.txt"), "keep").unwrap();
    gi5.uninstall(false).unwrap();
    assert!(!gi5.plugin_path.exists() && gi5.has_bepinex(), "BepInEx kept when not asked");
    gi5.uninstall(true).unwrap();
    assert_eq!(files_under(&g5), [GAME_EXE, "player-notes.txt"]);
    assert!(gi5.uninstall(true).is_ok(), "twice is fine");
}

#[test]
fn battle_logs_are_never_deleted() {
    let tmp = tempfile::tempdir().unwrap();
    let zip = bepinex_zip();
    let payload = Payload { plugin_dll: PLUGIN_V2, bepinex_zip: &zip };
    let g = fake_game(tmp.path(), "g");
    let gi = GameInstall::new(&g);
    gi.repair(&payload).unwrap();
    let app = tmp.path().join("Local").join("PtcglAutoBattleLog");
    let bep_logs = g.join("BepInEx").join("BattleLogs");
    let s = |p: &Path| p.to_str().unwrap().to_string();
    assert!(gi.why_not_logs_folder(&s(&bep_logs), &[]).is_some() && gi.why_not_logs_folder(&s(&g.join("BepInEx")), &[]).is_some()
            && gi.why_not_logs_folder(&s(&gi.plugin_path.join("..").join("logs")), &[]).is_some()
            && gi.why_not_logs_folder(&s(&Path::new("BepInEx").join("logs")), &[]).is_some(), "under BepInEx: refused");
    assert!(gi.why_not_logs_folder(&s(&app.join("logs")), &[&app]).is_some() && gi.why_not_logs_folder(&s(&app).to_uppercase(), &[&app]).is_some());
    assert!(gi.why_not_logs_folder(&s(&g.join("BepInExtra")), &[]).is_none() && gi.why_not_logs_folder(&s(&g), &[]).is_none()
            && gi.why_not_logs_folder(&s(app.parent().unwrap()), &[&app]).is_none() && gi.why_not_logs_folder("", &[]).is_none());
    assert!(gi.write_output_folder(&s(&bep_logs), &[]).is_err() && gi.write_output_folder(&s(&app.join("x")), &[&app]).is_err() && gi.read_output_folder().is_none());

    let sep = std::path::MAIN_SEPARATOR;
    assert!(is_same_or_under(&g.join("a").join("b"), &g) && is_same_or_under(Path::new(&format!("{}{sep}", g.display())), &g)
            && !is_same_or_under(Path::new(&format!("{}x", g.display())), &g) && !is_same_or_under(&g, Path::new(" ")));

    gi.write_config_value("General", "OutputFolder", &s(&bep_logs)).unwrap();
    fs::create_dir_all(&bep_logs).unwrap();
    let log = bep_logs.join("2026-10-19 21.34.05 - VICTORY vs Gary.txt");
    fs::write(&log, "a battle log").unwrap();
    let kept = gi.uninstall(true).unwrap();
    assert_eq!(fs::read_to_string(&log).unwrap(), "a battle log");
    assert_eq!(files_under(&g), ["BepInEx/BattleLogs/2026-10-19 21.34.05 - VICTORY vs Gary.txt", GAME_EXE]);
    assert_eq!(kept, [g.join("BepInEx")]);

    let g3 = fake_game(tmp.path(), "g3");
    let gi3 = GameInstall::new(&g3);
    gi3.repair(&payload).unwrap();
    gi3.write_config_value("General", "OutputFolder", &s(&gi3.plugin_dir())).unwrap();
    fs::write(gi3.plugin_dir().join("log.txt"), "kept").unwrap();
    gi3.uninstall(false).unwrap();
    assert!(!gi3.plugin_path.exists() && gi3.plugin_dir().join("log.txt").is_file(), "the DLL goes, the logs in its folder stay");
    assert!(GameInstall::new(&fake_game(tmp.path(), "g4")).uninstall(true).unwrap().is_empty());

    let tree = tmp.path().join("tree");
    fs::create_dir_all(tree.join("a/keep/deep")).unwrap();
    fs::create_dir_all(tree.join("b")).unwrap();
    for f in ["a/keep/deep/log.txt", "a/other.txt", "b/y.txt", "z.txt"] {
        fs::write(tree.join(f), "x").unwrap();
    }
    assert!(delete_tree_except(&tree, Some(&tree.join("a/keep"))).unwrap());
    assert_eq!(files_under(&tree), ["a/keep/deep/log.txt"]);
    assert!(delete_tree_except(&tree.join("a/keep"), Some(&tree.join("a/keep"))).unwrap() && tree.join("a/keep/deep/log.txt").is_file());
    assert!(!delete_tree_except(&tree, None).unwrap() && !tree.exists() && !delete_tree_except(&tree, None).unwrap());
}

#[test]
fn cfg_output_folder_and_seal_mode() {
    let tmp = tempfile::tempdir().unwrap();
    let g = fake_game(tmp.path(), "g");
    let gi = GameInstall::new(&g);
    let read = || fs::read_to_string(&gi.config_path).unwrap();
    assert_eq!(gi.read_output_folder(), None);

    let win = r"C:\Users\Ash\Documents\PTCGL Battle Logs";
    gi.write_output_folder(win, &[]).unwrap();
    assert_eq!(read(), format!("[General]\r\nOutputFolder = {win}\r\n"), "new cfg, no BOM, CRLF, path as is");
    assert_eq!(gi.read_output_folder().as_deref(), Some(win));

    let generated = [
        "## Settings file was created by plugin PTCGL Auto Battle Log v1.1.0", "## Plugin GUID: ptcgl.autobattlelog", "", "[General]", "",
        "## Folder where every battle log is saved, one .txt per match plus index.csv.", "# Setting type: String",
        &format!("# Default value: {win}"), &format!("OutputFolder = {win}"), "", "## Also save the log when you press Export in game.",
        "# Setting type: Boolean", "# Default value: true", "SaveManualExports = false", "", "[Debug]", "OutputFolder = not ours", "",
    ].join("\r\n");
    fs::write(&gi.config_path, [b"\xEF\xBB\xBF".as_slice(), generated.as_bytes()].concat()).unwrap();
    assert_eq!(gi.read_output_folder().as_deref(), Some(win));
    gi.write_output_folder(r"E:\Battle Logs", &[]).unwrap();
    let bytes = fs::read(&gi.config_path).unwrap();
    assert!(bytes.starts_with(b"\xEF\xBB\xBF"), "BOM kept");
    assert_eq!(std::str::from_utf8(&bytes[3..]).unwrap(), generated.replace(&format!("OutputFolder = {win}"), r"OutputFolder = E:\Battle Logs"), "only that line changed");

    fs::write(&gi.config_path, "# hand made\n[Other]\nKey = 1\n").unwrap();
    gi.write_output_folder("/home/ash/logs", &[]).unwrap();
    assert_eq!(read(), "# hand made\n[Other]\nKey = 1\n\n[General]\nOutputFolder = /home/ash/logs\n", "section appended, LF kept");

    fs::write(&gi.config_path, "[General]\r\n# OutputFolder = commented\r\nSaveManualExports = true\r\n").unwrap();
    gi.write_output_folder(r"D:\L", &[]).unwrap();
    assert_eq!(read(), "[General]\r\nOutputFolder = D:\\L\r\n# OutputFolder = commented\r\nSaveManualExports = true\r\n", "under the header");

    fs::write(&gi.config_path, "[General]\nOutputFolder = A:\\one\n  OutputFolder=A:\\\\two  \n").unwrap();
    assert_eq!(gi.read_output_folder().as_deref(), Some(r"A:\two"), "last duplicate wins, escaped form read");
    gi.write_output_folder(r"B:\x", &[]).unwrap();
    assert_eq!(read(), "[General]\nOutputFolder = B:\\x\nOutputFolder = B:\\x\n", "every duplicate rewritten");

    for (text, expected) in [("[General]\nOutputFolder = C:\\new\\tab\n", Some(r"C:\new\tab")), ("[General]\nOutputFolder = \\\\\\\\server\\\\share\n", Some(r"\\server\share")),
                             ("[General]\nOutputFolder =   \n", None), ("[general]\nOutputFolder = X:\\y\n", None)] {
        fs::write(&gi.config_path, text).unwrap();
        assert_eq!(gi.read_output_folder().as_deref(), expected, "{text:?}");
    }

    for path in [win, r"C:\Users\José\Pokémon logs", r"C:\trailing\", r"relative\dir", "C:/forward/slashes", r"C:\a = b\[x]", r"C:\#notacomment", r"C:\new\tab", r"Z:\", r"E:\Logs (2026) & more!"] {
        fs::remove_file(&gi.config_path).unwrap();
        gi.write_output_folder(path, &[]).unwrap();
        assert_eq!(gi.read_output_folder().as_deref(), Some(path), "round trip");
    }
    fs::write(&gi.config_path, "[General]\nOutputFolder = C:\\kept\n").unwrap();
    for path in [r"\\server\share\PTCGL Logs", r"C:\Users\O'Brien\Documents\PTCGL Battle Logs", r#"D:\My "quoted" logs"#, r"\\?\C:\long", r"relative\new", r" C:\x", "C:\\x\ny",
                 r#"D:\"quoted""#, r"\Logs", r"\server\share", " ", ""] {
        assert!(gi.write_output_folder(path, &[]).is_err() && read() == "[General]\nOutputFolder = C:\\kept\n", "refused, cfg unchanged: {path:?}");
    }

    gi.reset_output_folder().unwrap();
    assert_eq!((gi.read_output_folder(), gi.resolve_logs_folder(Some(Path::new("/default")))), (None, Some(PathBuf::from("/default"))));
    gi.write_config_value("General", "OutputFolder", "logs here").unwrap();
    assert_eq!(gi.resolve_logs_folder(None), Some(g.join("logs here")), "relative: against the game folder");
    gi.write_config_value("General", "OutputFolder", r"\Logs").unwrap();
    assert_eq!(gi.resolve_logs_folder(Some(Path::new("/default"))), Some(PathBuf::from("/default")), "one the plugin refuses: the default");

    assert_eq!(gi.read_seal_mode(), None);
    gi.write_seal_mode("sidecar").unwrap();
    assert_eq!(gi.read_seal_mode(), Some("Sidecar"));
    assert!(read().contains("[Metadata]\r\nMode = Sidecar") || read().contains("[Metadata]\nMode = Sidecar"));
    gi.write_config_value("Metadata", "Mode", "2").unwrap();
    assert_eq!(gi.read_seal_mode(), Some("Off"), "by number, like Enum.Parse");
    gi.write_config_value("Metadata", "Mode", "9").unwrap();
    assert_eq!(gi.read_seal_mode(), None);
    assert!(gi.write_seal_mode("Loud").is_err());
    assert!(!files_under(gi.config_path.parent().unwrap()).iter().any(|f| f.ends_with(".tmp")));
}

#[test]
fn cfg_strings_match_the_csharp_tray() {
    let golden: Vec<Value> = serde_json::from_str(&fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/golden/bepinex-config.json")).unwrap()).unwrap();
    assert!(golden.len() > 30);
    for row in golden {
        let s = |k: &str| row[k].as_str().map(String::from);
        let v = s("value").unwrap();
        assert_eq!(Some(escape(&v)), s("escape"), "escape {v:?}");
        assert_eq!(Some(convert_to_object(&v)), s("convertToObject"), "convert {v:?}");
        assert_eq!(Some(unescape(&v)), s("unescape"), "unescape {v:?}");
        assert_eq!(serialize(&v).ok(), s("serialize"), "serialize {v:?}");
        assert_eq!(why_plugin_refuses(&v).map(String::from), s("whyPluginRefuses"), "why {v:?}");
    }
}

#[test]
fn plugin_status_from_the_plugins_writer() {
    let golden = |name: &str| Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/golden").join(format!("plugin-status-{name}.json"));
    let beat = Utc.with_ymd_and_hms(2026, 10, 19, 21, 35, 0).unwrap();
    let plain = PluginStatus::try_read(&golden("plain")).unwrap();
    assert_eq!((plain.plugin_version.as_deref(), plain.state.as_deref(), plain.message.as_deref()), (Some("1.1.0"), Some("ok"), Some("Watching for matches.")));
    assert_eq!((plain.saved_this_session, plain.process_id), (Some(3), Some(4242)));
    assert_eq!(plain.heartbeat(), Some(beat));
    assert_eq!(plain.last_saved(), Some(beat - TimeDelta::seconds(55)));
    assert!(plain.is_alive(beat) && plain.is_alive(beat + TimeDelta::seconds(90)) && !plain.is_alive(beat + TimeDelta::seconds(91)));
    assert!(plain.is_alive(beat - TimeDelta::seconds(20)) && !plain.is_alive(beat - TimeDelta::minutes(10)), "small clock skew tolerated");

    let nasty = PluginStatus::try_read(&golden("nasty")).unwrap();
    let folder = "C:\\Users\\Zoë \"Q\" 日本語 🔥\\PTCGL\tBattle\nLogs\r\u{1}\u{1f}\u{2028}\u{2029}/end\\";
    assert_eq!((nasty.logs_folder.as_deref(), nasty.last_file.clone()), (Some(folder), Some(format!("{folder}\\x.txt"))));
    let lone = PluginStatus::try_read(&golden("lone-surrogates")).expect("lone surrogates do not make the file unreadable");
    assert_eq!(lone.message.as_deref(), Some("A\u{fffd}B\u{fffd}C\u{fffd}"));
    let nulls = PluginStatus::try_read(&golden("nulls")).unwrap();
    assert!(nulls.logs_folder.is_none() && nulls.last_saved().is_none() && nulls.is_alive(beat));

    assert_eq!(PluginStatus::mod_version_line(None, "1.1.0", beat), "Mod in the game: not loaded yet");
    assert_eq!(PluginStatus::mod_version_line(Some(&PluginStatus { plugin_version: Some(" ".into()), ..Default::default() }), "1.1.0", beat), "Mod in the game: not loaded yet");
    assert_eq!(PluginStatus::mod_version_line(Some(&plain), "1.1.0", beat), "Mod in the game: v1.1.0 (same as the app)");
    assert_eq!(PluginStatus::mod_version_line(Some(&plain), "1.2.0", beat), "Mod in the game: v1.1.0 · not the app's v1.2.0: restart the game to update it");
    assert_eq!(PluginStatus::mod_version_line(Some(&plain), "1.1.0", beat + TimeDelta::hours(1)), "Mod last loaded by the game: v1.1.0");
    assert_eq!(PluginStatus::mod_version_line(Some(&plain), "1.2.0", beat + TimeDelta::hours(1)), "Mod last loaded by the game: v1.1.0 · the game loads v1.2.0 the next time it starts");

    let tmp = tempfile::tempdir().unwrap();
    let path = tmp.path().join("plugin-status.json");
    assert!(PluginStatus::try_read(&path).is_none());
    fs::write(&path, "{\"state\":\"starting\",\"heartbeatUtc\":\"yesterday\"}").unwrap();
    let st = PluginStatus::try_read(&path).unwrap();
    assert!(st.state.as_deref() == Some("starting") && st.heartbeat().is_none() && !st.is_alive(beat));
    fs::write(&path, "{\"heartbeatUtc\":\"2026-10-19T21:35:00.123Z\",\"processId\":null,\"savedThisSession\":null}").unwrap();
    assert!(PluginStatus::try_read(&path).unwrap().is_alive(beat), "fractional seconds, null numbers");
    for bad in ["", "{", "{\"pluginVersion\":", "[1,2]", "garbage", "{\"savedThisSession\":\"two\"}"] {
        fs::write(&path, bad).unwrap();
        assert!(PluginStatus::try_read(&path).is_none(), "corrupt: {bad}");
    }
    assert!(PluginStatus::try_read(tmp.path()).is_none(), "a folder");
    fs::write(&path, "{").unwrap();
    assert_eq!(PluginStatus::try_read_keeping(&path, Some(plain.clone())), Some(plain.clone()), "unreadable but present: previous kept");
    fs::remove_file(&path).unwrap();
    assert_eq!(PluginStatus::try_read_keeping(&path, Some(plain)), None, "gone: none");
}
