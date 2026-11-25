//! Ported from tests/TrayTests/UpdateTests.cs §8–12.

use std::cell::RefCell;
use std::io::{self, Cursor, Read};
use std::rc::Rc;

use pab_core::hashing::{sha256, to_hex};
use pab_core::update::*;

const OWNER: &str = "manucruzleiva";
const REPO: &str = "ptcgl-auto-battlelog";
const GOOD_URL: &str = "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe";

fn contract_manifest() -> String {
    format!(
        r#"{{
  "version": "1.2.0",
  "tag": "v1.2.0",
  "releasedUtc": "2026-11-01T12:00:00Z",
  "notesUrl": "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/tag/v1.2.0",
  "assets": [
    {{
      "name": "PtcglAutoBattleLog.exe",
      "url": "{GOOD_URL}",
      "size": 812345,
      "sha256": "{}"
    }}
  ]
}}"#,
        "a".repeat(64)
    )
}

fn checker(current: &str) -> UpdateChecker {
    UpdateChecker::new(OWNER, REPO, current).unwrap()
}

#[test]
fn manifest() {
    let m = UpdateManifest::parse(&contract_manifest()).unwrap();
    assert_eq!(m.version, "1.2.0");
    assert_eq!(m.tag.as_deref(), Some("v1.2.0"));
    assert_eq!(m.released_utc.as_deref(), Some("2026-11-01T12:00:00Z"));
    let a = m.find(EXE_ASSET_NAME).unwrap();
    assert_eq!((a.url.as_deref(), a.size, a.sha256.clone()), (Some(GOOD_URL), 812345, Some("a".repeat(64))));
    assert!(m.find("update.json").is_none() && m.find("ptcglautobattlelog.exe").is_none(), "find is exact");
    assert!(UpdateManifest::parse(r#"{"version":"1.0.0"}"#).unwrap().assets.is_empty());
    assert_eq!(UpdateManifest::parse(r#"{"version":"1.0.0","assets":[null,{"name":"x"}],"extra":1}"#).unwrap().assets.len(), 1);
    for bad in ["", "{", "null", "[]", "{}", r#"{"version":""}"#, r#"{"version":"1","assets":7}"#, r#"{"version":"1","assets":[{"size":"big"}]}"#,
                // .NET's serializer read a number as a version that was then never offered; here it is refused outright.
                r#"{"version":1.2}"#] {
        assert!(matches!(UpdateManifest::parse(bad), Err(UpdateError::Format(_))), "invalid manifest: {bad}");
    }
    assert!(!checker("1.1.0").is_newer(&UpdateManifest::parse(r#"{"version":" 9.9.9"}"#).unwrap().version, false));
}

#[test]
fn versions() {
    for (s, v) in [("1.2.3", (1, 2, 3)), ("v1.2.3", (1, 2, 3)), ("V1.2.3", (1, 2, 3)), ("1.2.3.0", (1, 2, 3)), ("0.0.1", (0, 0, 1)),
                   ("10.20.30", (10, 20, 30)), ("v0.0.0.0", (0, 0, 0)), ("999999999.0.0", (999999999, 0, 0))] {
        assert_eq!(parse_version(s), Some(v), "{s}");
    }
    for s in ["", "v", "1", "1.2", "1.2.3.4", "1.2.3.00", "1.2.3.0.0", "1.2.3-beta", "1.2.3-rc.1", "1.2.3+build", "1.2.3 ", " 1.2.3",
              "01.2.3", "1.02.3", "1.2.03", "1.2.3.", ".1.2.3", "1..3", "-1.2.3", "+1.2.3", "1.-2.3", "1.2.x", "vv1.2.3", "v 1.2.3",
              "\u{0661}.\u{0662}.\u{0663}", "\u{FF11}.\u{FF12}.\u{FF13}", "1.2.3\0", "1.2.9999999999", "1,2,3", "1.2.3\n"] {
        assert_eq!(parse_version(s), None, "{s:?}");
    }

    let newer = |cur: &str, v: &str| checker(cur).is_newer(v, false);
    let beta = |cur: &str, v: &str| checker(cur).is_newer(v, true);
    assert!(newer("1.1.0", "1.2.0") && newer("1.1.0", "1.1.1") && newer("1.1.0", "2.0.0") && newer("1.1.0", "v1.1.1") && newer("1.1.0", "1.10.0"));
    assert!(!newer("1.1.0", "1.1.0") && !newer("1.1.0", "1.1.0.0") && !newer("1.1.0", "v1.1.0") && !newer("1.1.0", "1.0.9") && !newer("1.1.0", "0.9.9"));
    assert!(!newer("1.1.0", "1.2.0-beta") && !newer("1.1.0", "9.0.0-rc1") && !newer("1.1.0", "1.2.0+x") && !newer("1.1.0", "garbage") && !newer("1.1.0", ""));
    assert!(newer("1.2.0-rc1", "1.2.0") && !newer("1.2.0-rc1", "1.1.9") && newer("1.2.0-rc1", "1.2.1"), "a pre-release build updates to its release");
    assert!(!newer("1.2.0+abc", "1.2.0") && newer("1.2.0+abc", "1.2.1"), "build metadata does not change precedence");
    assert!(!newer("dev", "9.9.9") && !newer("1.2.0-", "1.2.0"), "unparseable current version never updates");

    assert!(beta("1.3.1", "1.3.2-beta.1") && beta("1.3.1", "v1.3.2-beta.1") && beta("1.3.1", "1.3.2"));
    assert!(!newer("1.3.1", "1.3.2-beta.1"));
    assert!(beta("1.3.2-beta.1", "1.3.2-beta.2") && beta("1.3.2-beta.2", "1.3.2-beta.10") && beta("1.3.2-beta.1", "1.3.2"));
    assert!(!beta("1.3.2-beta.2", "1.3.2-beta.1") && !beta("1.3.2-beta.1", "1.3.2-beta.1") && !beta("1.3.2", "1.3.2-beta.9") && !beta("1.3.2", "1.3.1"));
    assert!(beta("1.3.2-alpha", "1.3.2-beta") && beta("1.3.2-beta", "1.3.2-beta.1") && beta("1.3.2-1", "1.3.2-alpha") && beta("1.3.2-rc.1", "1.3.2-rc.1.1"));
    assert!(!beta("1.3.1", "1.3.2-beta.01") && !beta("1.3.1", "1.3.2-") && !beta("1.3.1", "1.3.2-beta..1") && !beta("1.3.1", "1.3.2-b\u{e9}ta")
        && !beta("1.3.1", "1.3.2-beta+x") && !beta("1.3.1", "1.3.2-beta_1"));
    assert!(newer("1.3.2-beta.1", "1.3.2") && !newer("1.3.2-beta.1", "1.3.1"));
    // The C# 1.6.x tray must see the Rust 2.0 releases as newer.
    assert!(newer("1.6.3", "2.0.0") && beta("1.6.3", "2.0.0-beta.1") && !newer("1.6.3", "2.0.0-beta.1"));
    let sv = SemVer::parse("v1.3.2-beta.1").unwrap();
    assert!(sv.is_pre_release() && sv.to_string() == "1.3.2-beta.1");
    let rel = SemVer::parse("1.3.2+abc").unwrap();
    assert!(!rel.is_pre_release() && rel.to_string() == "1.3.2");

    assert!(is_installed_newer("1.2.0", "1.1.0") && is_installed_newer("1.2.0+16fdb0f52796cca94b7a9eb700cc6ae08187c70f", "1.1.0")
        && is_installed_newer("1.10.0", "1.9.9") && is_installed_newer(" 2.0.0 ", "1.1.0+abc"));
    assert!(!is_installed_newer("1.1.0", "1.1.0") && !is_installed_newer("1.1.0+aaa", "1.1.0+bbb") && !is_installed_newer("1.0.9", "1.1.0"));
    assert!(is_installed_newer("1.2.0", "1.2.0-rc1") && !is_installed_newer("1.2.0-rc1", "1.2.0")
        && !is_installed_newer("1.2.0-rc1", "1.2.0-rc2") && is_installed_newer("1.3.0-rc1", "1.2.0"));
    assert!(!is_installed_newer("", "1.1.0") && !is_installed_newer("dev", "1.1.0") && !is_installed_newer("9.9.9", "dev")
        && !is_installed_newer("1.2.0-", "1.1.0") && !is_installed_newer("1.2", "1.1.0"));
}

#[test]
fn trusted_urls() {
    let c = checker("1.1.0");
    assert_eq!(c.manifest_url, "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest/download/update.json");
    for bad in ["", "a/b", "evil.com/x", "..", ".", "o?x", "o#x", "o@x", "o x", "o\\x", "ö"] {
        assert!(UpdateChecker::new(bad, REPO, "1.0.0").is_err() && UpdateChecker::new(OWNER, bad, "1.0.0").is_err(), "{bad}");
    }
    for good in [
        GOOD_URL,
        "https://github.com/ManuCruzLeiva/PTCGL-Auto-BattleLog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/update.json",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/1.2.0/PtcglAutoBattleLog.exe",
    ] {
        assert!(c.is_trusted_asset_url(good), "{good}");
    }
    const P: &str = "/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe";
    let mut bad: Vec<String> = [
        "", " ", "not a url", P, "file:///C:/PtcglAutoBattleLog.exe",
        "https://github.com/someone/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog-fork/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelo/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com/evil/repo/releases/download/../../../manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/../../../../evil/x/releases/download/v1/x.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/../../../../evil.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/./v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/%2e%2e/%2e%2e/evil/x.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/..%2F..%2Fevil.exe",
        "https://github.com/manucruzleiva%2Fptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com\\manucruzleiva\\ptcgl-auto-battlelog\\releases\\download\\v1.2.0\\PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest/download/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/tag/v1.2.0",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/archive/refs/tags/v1.2.0.zip",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/Releases/Download/v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/sub/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe/",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/.../PtcglAutoBattleLog.exe",
        "https://github.com//manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.exe;.txt",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/Ptcgl AutoBattleLog.exe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/PtcglAutoBattleLog.\u{e9}xe",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.2.0/Ptcgl\tAutoBattleLog.exe",
    ].iter().map(|s| s.to_string()).collect();
    for prefix in ["github.com", "//github.com", "https:github.com", "https:/github.com", "http://github.com", "ftp://github.com", "HTTPS://github.com",
                   "https://GitHub.com", "https://github.com.", "https://www.github.com", "https://api.github.com", "https://github.com.evil.com",
                   "https://evilgithub.com", "https://evil.com", "https://raw.githubusercontent.com", "https://objects.githubusercontent.com",
                   "https://140.82.112.3", "https://[::1]", "https://g\u{456}thub.com", "https://github.com@evil.com", "https://evil.com@github.com",
                   "https://user:pass@github.com", "https://:@github.com", "https://github.com:443", "https://github.com:8443", "https://github.com:",
                   "https://evil.com\\@github.com", "https://evil.com/?https://github.com", "https://evil.com/#https://github.com",
                   "https://evil.com/https://github.com", " https://github.com"] {
        bad.push(format!("{prefix}{P}"));
    }
    for suffix in ["?raw=true", "?", "#frag", "#", " ", "\n", "\0"] {
        bad.push(format!("https://github.com{P}{suffix}"));
    }
    bad.push(format!("https://github.com{}", P.replace("v1.2.0", &"v".repeat(3000))));
    for b in &bad {
        assert!(!c.is_trusted_asset_url(b), "untrusted: {b:?}");
    }
}

/// Serves `data` (cycled up to `length` bytes) in 1000-byte chunks, like a network.
struct FakeDownload {
    data: Vec<u8>,
    length: u64,
    pos: u64,
    served: Rc<RefCell<u64>>,
    fail_after: Option<u64>,
}

impl Read for FakeDownload {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if self.fail_after.is_some_and(|f| self.pos > f) {
            return Err(io::Error::new(io::ErrorKind::ConnectionReset, "reset"));
        }
        let n = (buf.len() as u64).min(self.length - self.pos).min(1000) as usize;
        for (i, b) in buf[..n].iter_mut().enumerate() {
            *b = self.data[((self.pos + i as u64) % self.data.len() as u64) as usize];
        }
        self.pos += n as u64;
        *self.served.borrow_mut() = self.pos;
        Ok(n)
    }
}

fn asset_for(content: &[u8], url: &str) -> UpdateAsset {
    UpdateAsset { name: Some(EXE_ASSET_NAME.into()), url: Some(url.into()), size: content.len() as i64, sha256: Some(to_hex(&sha256(content))) }
}

#[test]
fn download() {
    let exe: Vec<u8> = format!("MZ new tray exe {}", (0..30000).map(|i| i.to_string()).collect::<Vec<_>>().join(",")).into_bytes();
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("updates");
    let c = checker("1.1.0");
    let never = || false;
    let opens = Rc::new(RefCell::new(0));
    let served = Rc::new(RefCell::new(0u64));
    let serve = |data: Vec<u8>, length: u64, fail_after: Option<u64>| {
        let (opens, served) = (opens.clone(), served.clone());
        move |url: &str| -> io::Result<Box<dyn Read>> {
            assert_eq!(url.split('/').nth(2), Some("github.com"));
            *opens.borrow_mut() += 1;
            Ok(Box::new(FakeDownload { data: data.clone(), length, pos: 0, served: served.clone(), fail_after }))
        }
    };
    let clean = || !dir.exists() || std::fs::read_dir(&dir).unwrap().all(|e| !e.unwrap().path().to_string_lossy().ends_with(".partial"));
    let exes = || if dir.exists() { std::fs::read_dir(&dir).unwrap().filter(|e| e.as_ref().unwrap().path().extension().is_some_and(|x| x == "exe")).count() } else { 0 };

    let mut asset = asset_for(&exe, GOOD_URL);
    asset.sha256 = asset.sha256.map(|s| s.to_ascii_uppercase());
    let final_path = c.download(&asset, &dir, &mut serve(exe.clone(), exe.len() as u64, None), &never).unwrap();
    assert_eq!(final_path, dir.join("PtcglAutoBattleLog-1.2.0.exe"));
    assert_eq!(std::fs::read(&final_path).unwrap(), exe);
    assert!(clean());
    let again = c.download(&asset_for(&exe, GOOD_URL), &dir, &mut serve(exe.clone(), exe.len() as u64, None), &never).unwrap();
    assert_eq!(again, final_path, "a previous download is replaced");

    let refused = |what: &str, a: UpdateAsset, data: Vec<u8>, length: u64, expect_open: bool, part: &str| {
        let _ = std::fs::remove_file(&final_path);
        *opens.borrow_mut() = 0;
        let e = c.download(&a, &dir, &mut serve(data, length, None), &never).unwrap_err();
        assert!(matches!(e, UpdateError::Verification(_)) && e.to_string().contains(part), "{what}: {e}");
        assert!(clean() && exes() == 0, "{what}: nothing left");
        assert_eq!(*opens.borrow(), expect_open as i32, "{what}: opens");
    };
    refused("too few bytes", asset_for(&exe, GOOD_URL), exe[..exe.len() - 1].to_vec(), exe.len() as u64 - 1, true, "declares");
    refused("more bytes than declared", asset_for(&exe, GOOD_URL), exe.clone(), 50 * 1024 * 1024, true, "larger");
    assert!(*served.borrow() <= exe.len() as u64 + 1000, "stopped reading right after the declared size");
    let mut tampered = exe.clone();
    tampered[100] ^= 1;
    refused("hash mismatch", asset_for(&exe, GOOD_URL), tampered, exe.len() as u64, true, "SHA-256");
    refused("untrusted URL", asset_for(&exe, "https://evil.com/PtcglAutoBattleLog.exe"), exe.clone(), exe.len() as u64, false, "untrusted");
    refused("redirect-looking URL", asset_for(&exe, "https://github.com@evil.com/x.exe"), exe.clone(), exe.len() as u64, false, "");
    for size in [MAX_ASSET_BYTES + 1, 0, -5] {
        let mut a = asset_for(&exe, GOOD_URL);
        a.size = size;
        refused("bad size", a, exe.clone(), exe.len() as u64, false, "bytes");
    }
    for sha in [None, Some(String::new()), Some("<64 lowercase hex chars>".into()), Some("a".repeat(63)), Some("a".repeat(65)), Some("g".repeat(64)),
                Some(format!("0x{}", "a".repeat(62))), Some(" ".repeat(64))] {
        let mut a = asset_for(&exe, GOOD_URL);
        a.sha256 = sha;
        refused("bad sha256", a, exe.clone(), exe.len() as u64, false, "");
    }
    for name in [None, Some(""), Some(".."), Some("../PtcglAutoBattleLog.exe"), Some("..\\PtcglAutoBattleLog.exe"), Some("sub/x.exe"), Some("C:x.exe"), Some(".exe")] {
        let mut a = asset_for(&exe, GOOD_URL);
        a.name = name.map(String::from);
        refused("unsafe name", a, exe.clone(), exe.len() as u64, false, "");
    }

    // Cancellation mid-download, and an error mid-stream: no partial left.
    let reads = Rc::new(RefCell::new(0));
    let r2 = reads.clone();
    let cancel_later = move || { *r2.borrow_mut() += 1; *r2.borrow() > 5 };
    let e = c.download(&asset_for(&exe, GOOD_URL), &dir, &mut serve(exe.clone(), exe.len() as u64, None), &cancel_later).unwrap_err();
    assert!(matches!(e, UpdateError::Cancelled) && clean() && exes() == 0);
    let e = c.download(&asset_for(&exe, GOOD_URL), &dir, &mut serve(exe.clone(), exe.len() as u64, Some(3000)), &never).unwrap_err();
    assert!(matches!(e, UpdateError::Io(_)) && clean() && exes() == 0, "error mid-stream deletes the partial");
    let e = c.download(&asset_for(&exe, GOOD_URL), &dir, &mut |_: &str| -> io::Result<Box<dyn Read>> { Err(io::Error::other("dns")) }, &never).unwrap_err();
    assert!(matches!(e, UpdateError::Io(_)) && clean());

    // A stale .partial from a crash is overwritten, not appended to.
    std::fs::write(dir.join("PtcglAutoBattleLog-1.2.0.exe.partial"), "stale junk").unwrap();
    let ok2 = c.download(&asset_for(&exe, GOOD_URL), &dir, &mut serve(exe.clone(), exe.len() as u64, None), &never).unwrap();
    assert!(std::fs::read(ok2).unwrap() == exe && clean());

    // A tag that is not a version still gets a unique, safe name.
    let odd = asset_for(&exe, "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/nightly/PtcglAutoBattleLog.exe");
    let odd_path = c.download(&odd, &dir, &mut serve(exe.clone(), exe.len() as u64, None), &never).unwrap();
    assert_eq!(odd_path.file_name().unwrap().to_string_lossy(), format!("PtcglAutoBattleLog-{}.exe", &odd.sha256.unwrap()[..12]));
    let beta = asset_for(&exe, "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v2.0.0-beta.1/PtcglAutoBattleLog.exe");
    let beta_path = c.download(&beta, &dir, &mut serve(exe.clone(), exe.len() as u64, None), &never).unwrap();
    assert_eq!(beta_path.file_name().unwrap().to_string_lossy(), "PtcglAutoBattleLog-2.0.0-beta.1.exe");
}

const RELEASE_LIST: &str = r#"[
  {"tag_name": "v1.4.0-beta.1", "draft": true, "prerelease": true, "assets": []},
  {"tag_name": "v1.3.2-beta.2", "draft": false, "prerelease": true, "body": "notes \u00e9", "author": {"login": "x"}},
  {"tag_name": "v1.3.2-beta.10", "draft": false, "prerelease": true},
  {"tag_name": "v1.3.1", "draft": false, "prerelease": false},
  {"tag_name": "nightly", "draft": false, "prerelease": true},
  {"tag_name": "v9.9.9+x", "draft": false, "prerelease": false},
  {"tag_name": "../v9.9.9", "draft": false, "prerelease": false},
  {"tag_name": null}
]"#;

fn site(list: &'static str, manifest: String, asked: Rc<RefCell<Vec<String>>>) -> impl FnMut(&str) -> io::Result<Box<dyn Read>> {
    move |url: &str| {
        asked.borrow_mut().push(url.to_string());
        let body = if url.starts_with("https://api.github.com/") { list.to_string() } else { manifest.clone() };
        Ok(Box::new(Cursor::new(body.into_bytes())) as Box<dyn Read>)
    }
}

#[test]
fn fetch_manifest() {
    let asked = Rc::new(RefCell::new(Vec::new()));
    let m = checker("1.1.0").fetch_manifest(false, &mut site("[]", contract_manifest(), asked.clone())).unwrap();
    assert_eq!(m.version, "1.2.0");
    assert_eq!(asked.borrow().as_slice(), ["https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/latest/download/update.json"]);
    assert!(checker("1.1.0").is_newer(&m.version, false) && !checker("1.2.0").is_newer(&m.version, false));

    let mut bom = b"\xEF\xBB\xBF".to_vec();
    bom.extend(contract_manifest().into_bytes());
    let m = checker("1.1.0").fetch_manifest(false, &mut move |_: &str| Ok(Box::new(Cursor::new(bom.clone())) as Box<dyn Read>)).unwrap();
    assert_eq!(m.version, "1.2.0", "BOM tolerated");
    let big = format!(r#"{{"version":"1.2.0","pad":"{}"}}"#, "x".repeat(2 * 1024 * 1024));
    let e = checker("1.1.0").fetch_manifest(false, &mut site("[]", big, asked.clone())).unwrap_err();
    assert!(matches!(e, UpdateError::Verification(_)), "oversize manifest refused");
    let e = checker("1.1.0").fetch_manifest(false, &mut site("[]", "<html>rate limited</html>".into(), asked.clone())).unwrap_err();
    assert!(matches!(e, UpdateError::Format(_)));

    let tags = parse_release_list(RELEASE_LIST).unwrap();
    assert!(!tags.contains(&"v1.4.0-beta.1".to_string()) && tags.contains(&"v1.3.1".to_string()) && tags.len() == 6);
    assert_eq!(newest_tag(tags.iter().map(String::as_str)), Some("v1.3.2-beta.10"));
    assert_eq!(newest_tag(["v1.3.2-beta.1", "v1.3.2"]), Some("v1.3.2"));
    assert_eq!(newest_tag([]), None);
    for bad in ["", "{}", "<html>", r#"[{"tag_name": 5,"#, "null"] {
        assert!(matches!(parse_release_list(bad), Err(UpdateError::Format(_))), "{bad}");
    }

    let beta_manifest = contract_manifest().replace("1.2.0", "1.3.2-beta.10");
    asked.borrow_mut().clear();
    let bm = checker("1.3.1").fetch_manifest(true, &mut site(RELEASE_LIST, beta_manifest.clone(), asked.clone())).unwrap();
    assert!(bm.version == "1.3.2-beta.10" && checker("1.3.1").is_newer(&bm.version, true) && !checker("1.3.1").is_newer(&bm.version, false));
    assert_eq!(asked.borrow().as_slice(), [
        "https://api.github.com/repos/manucruzleiva/ptcgl-auto-battlelog/releases?per_page=30",
        "https://github.com/manucruzleiva/ptcgl-auto-battlelog/releases/download/v1.3.2-beta.10/update.json",
    ]);
    let e = checker("1.3.1").fetch_manifest(true, &mut site(RELEASE_LIST, contract_manifest(), asked.clone())).unwrap_err();
    assert!(matches!(e, UpdateError::Verification(_)), "an update.json for another version than its tag is refused");
    let e = checker("1.3.1").fetch_manifest(true, &mut site("[]", beta_manifest, asked.clone())).unwrap_err();
    assert!(matches!(e, UpdateError::Format(_)));
    assert!(checker("1.1.0").manifest_url_for("../x").is_err() && checker("1.1.0").manifest_url_for("").is_err());
}
