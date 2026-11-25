//! The files baked into the exe (Embedded.cs): what gets installed into the game, the disclaimer,
//! the license texts and the icon. One download, nothing to lose next to it.
use pab_core::{disclaimer, game_install::Payload};

const PLUGIN_DLL: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/plugin.dll"));
const BEPINEX_ZIP: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/bepinex.zip"));
pub const APP_ICON: &[u8] = include_bytes!("../../../src/Tray/app.ico");
const DISCLAIMER: &str = include_str!("../../../DISCLAIMER.md");
const NOTICES: &str = include_str!("../../../THIRD-PARTY-NOTICES.md");
const LICENSE: &str = include_str!("../../../LICENSE");
const LGPL: &str = include_str!("../../../licenses/LGPL-2.1.txt");

pub const INCOMPLETE: &str = "This copy of the app is incomplete (missing its payload). Download it again.";

/// The plugin and BepInEx, or None in a build made without them (see build.rs).
pub fn payload() -> Option<Payload<'static>> {
    (!PLUGIN_DLL.is_empty() && !BEPINEX_ZIP.is_empty()).then_some(Payload { plugin_dll: PLUGIN_DLL, bepinex_zip: BEPINEX_ZIP })
}

/// DISCLAIMER.md as plain paragraphs for the consent dialog.
pub fn disclaimer_text() -> String {
    disclaimer::to_plain_text(DISCLAIMER)
}

/// The "Licenses" window: THIRD-PARTY-NOTICES.md, then this app's LICENSE and the full LGPL-2.1.
pub fn licenses_text() -> String {
    let crlf = |t: &str| t.replace("\r\n", "\n").replace('\n', "\r\n").trim_end().to_string();
    format!(
        "{}\r\n\r\nLICENSE of {}\r\n\r\n{}\r\n\r\nGNU Lesser General Public License, version 2.1 (Unity Doorstop)\r\n\r\n{}",
        disclaimer::to_plain_text(NOTICES),
        crate::PRODUCT_NAME,
        crlf(LICENSE),
        crlf(LGPL)
    )
}

#[cfg(test)]
mod tests {
    #[test]
    fn texts_are_embedded() {
        assert!(super::disclaimer_text().len() > 500);
        let licenses = super::licenses_text();
        assert!(licenses.contains("LICENSE of PTCGL Auto Battle Log") && licenses.contains("GNU LESSER GENERAL PUBLIC LICENSE"));
        assert!(!licenses.contains("\r\r") && super::APP_ICON.starts_with(&[0, 0, 1, 0]));
    }
}
