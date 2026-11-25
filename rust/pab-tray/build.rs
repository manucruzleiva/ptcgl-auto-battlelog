//! Embeds what the exe installs into the game (the plugin DLL and the pinned BepInEx zip), like the
//! C# tray's EmbeddedResources. build.sh and build.ps1 build the plugin and download BepInEx first.
//! PAB_PLUGIN_DLL and PAB_BEPINEX_ZIP override the paths; PAB_REQUIRE_PAYLOAD=1 (release builds)
//! fails the build when one is missing. Without it, a missing file embeds as empty and the exe
//! refuses to install ("this copy of the app is incomplete"), so `cargo test` needs no .NET.
use std::{env, fs, path::PathBuf};

fn main() {
    let repo = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap()).join("../..");
    let out = PathBuf::from(env::var("OUT_DIR").unwrap());
    let require = env::var("PAB_REQUIRE_PAYLOAD").is_ok_and(|v| v == "1");
    for (var, default, name) in [
        ("PAB_PLUGIN_DLL", "src/Plugin/bin/Release/PtcglAutoBattleLog.Plugin.dll", "plugin.dll"),
        ("PAB_BEPINEX_ZIP", "cache/BepInEx_win_x64_5.4.23.3.zip", "bepinex.zip"),
    ] {
        println!("cargo:rerun-if-env-changed={var}");
        let src = env::var_os(var).map(PathBuf::from).unwrap_or_else(|| repo.join(default));
        println!("cargo:rerun-if-changed={}", src.display());
        let bytes = match fs::read(&src) {
            Ok(b) if !b.is_empty() => b,
            _ if require => panic!("{var}: {} not found (PAB_REQUIRE_PAYLOAD=1)", src.display()),
            _ => Vec::new(),
        };
        fs::write(out.join(name), bytes).unwrap();
    }
    for var in ["PAB_REQUIRE_PAYLOAD", "PAB_VERSION", "PAB_REPO_OWNER", "PAB_REPO_NAME"] {
        println!("cargo:rerun-if-env-changed={var}");
    }
}
