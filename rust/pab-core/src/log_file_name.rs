//! Reads back the names the plugin gives its logs (LogFileName.cs):
//! "yyyy-MM-dd HH.mm.ss - RESULT[ vs OPPONENT][ (N)].txt".

use chrono::NaiveDateTime;

pub const RESULTS: [&str; 4] = ["VICTORY", "DEFEAT", "NO-RESULT", "EXPORTED"];

const DATE_LEN: usize = "yyyy-MM-dd HH.mm.ss".len();
const SEPARATOR: &str = " - ";
const VERSUS: &str = " vs ";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedName {
    pub result: &'static str,
    /// None when the name has none; it may itself contain " vs ".
    pub opponent: Option<String>,
    /// The match's end, local time of the computer that saved it.
    pub local_time: NaiveDateTime,
}

/// "2026-10-19 21.34.05 - VICTORY vs Gary.txt" → VICTORY, Gary. A path is accepted too (either
/// separator). Temp files (.txt.tmp) and anything else do not parse.
pub fn parse(file_name: &str) -> Option<ParsedName> {
    let name = file_name.rsplit(['/', '\\']).next()?;
    if name.len() < 4 || !name.is_char_boundary(name.len() - 4) || !name[name.len() - 4..].eq_ignore_ascii_case(".txt") {
        return None;
    }
    let name = &name[..name.len() - 4];
    // An opponent may itself end in " (2)": when the name does not parse without it, it was part of the name.
    let stripped = strip_duplicate_suffix(name);
    parse_stem(stripped).or_else(|| if stripped.len() != name.len() { parse_stem(name) } else { None })
}

fn parse_stem(name: &str) -> Option<ParsedName> {
    if name.len() < DATE_LEN + SEPARATOR.len() || !name.is_char_boundary(DATE_LEN) {
        return None;
    }
    let local_time = parse_date(&name[..DATE_LEN])?;
    let rest = name[DATE_LEN..].strip_prefix(SEPARATOR)?;
    for label in RESULTS {
        if rest == label {
            return Some(ParsedName { result: label, opponent: None, local_time });
        }
        if let Some(opponent) = rest.strip_prefix(label).and_then(|r| r.strip_prefix(VERSUS)) {
            if !opponent.is_empty() {
                return Some(ParsedName { result: label, opponent: Some(opponent.to_string()), local_time });
            }
        }
    }
    None
}

/// Exactly "yyyy-MM-dd HH.mm.ss", ASCII digits, a real date and time.
fn parse_date(s: &str) -> Option<NaiveDateTime> {
    let shape = s.bytes().enumerate().all(|(i, c)| match i {
        4 | 7 => c == b'-',
        10 => c == b' ',
        13 | 16 => c == b'.',
        _ => c.is_ascii_digit(),
    });
    if !shape {
        return None;
    }
    NaiveDateTime::parse_from_str(s, "%Y-%m-%d %H.%M.%S").ok()
}

/// The plugin numbers duplicates " (2)", " (3)"...; one such suffix is removed.
fn strip_duplicate_suffix(name: &str) -> &str {
    let Some(inner) = name.strip_suffix(')') else { return name };
    let Some(open) = inner.rfind(" (") else { return name };
    let digits = &inner[open + 2..];
    if digits.is_empty() || digits.len() > 6 || digits.starts_with('0') || !digits.bytes().all(|c| c.is_ascii_digit()) {
        return name;
    }
    if digits == "1" { name } else { &name[..open] }
}

#[cfg(test)]
mod tests {
    use super::*;

    // The same cases as tests/TrayTests/StateTests.cs §14.
    #[test]
    fn parses_like_the_csharp_tray() {
        let ok = |name: &str, result: &str, opponent: Option<&str>| {
            let p = parse(name).unwrap_or_else(|| panic!("should parse: {name}"));
            assert_eq!((p.result, p.opponent.as_deref()), (result, opponent), "{name}");
        };
        ok("2026-10-19 21.34.05 - VICTORY vs Gary.txt", "VICTORY", Some("Gary"));
        ok("2026-10-19 21.34.05 - DEFEAT vs Ash vs Gary.txt", "DEFEAT", Some("Ash vs Gary"));
        ok("2026-10-19 21.34.05 - VICTORY vs vs.txt", "VICTORY", Some("vs"));
        ok("2026-10-19 21.34.05 - VICTORY vs  vs Gary .txt", "VICTORY", Some(" vs Gary "));
        ok("2026-10-19 21.34.05 - NO-RESULT vs Misty.txt", "NO-RESULT", Some("Misty"));
        ok("2026-10-19 21.34.05 - NO-RESULT.txt", "NO-RESULT", None);
        ok("2026-10-19 21.34.05 - EXPORTED.txt", "EXPORTED", None);
        ok("2026-10-19 21.34.05 - EXPORTED (2).txt", "EXPORTED", None);
        ok("2026-10-19 21.34.05 - VICTORY vs Gary (2).txt", "VICTORY", Some("Gary"));
        ok("2026-10-19 21.34.05 - VICTORY vs Gary (13).txt", "VICTORY", Some("Gary"));
        ok("2026-10-19 21.34.05 - VICTORY vs Gary (2) (3).txt", "VICTORY", Some("Gary (2)"));
        ok("2026-10-19 21.34.05 - VICTORY vs Gary (1).txt", "VICTORY", Some("Gary (1)"));
        ok("2026-10-19 21.34.05 - VICTORY vs Team (Rocket).txt", "VICTORY", Some("Team (Rocket)"));
        ok("2026-10-19 21.34.05 - DEFEAT vs Gary__.TXT", "DEFEAT", Some("Gary__"));
        ok("C:\\Logs\\2026-10-19 21.34.05 - DEFEAT vs Gary.txt", "DEFEAT", Some("Gary"));
        ok("C:/Logs/2026-10-19 21.34.05 - DEFEAT vs Gary.txt", "DEFEAT", Some("Gary"));
        ok("2026-10-19 21.34.05 - VICTORY vs Kétchum ★.txt", "VICTORY", Some("Kétchum ★"));
    }

    #[test]
    fn rejects_like_the_csharp_tray() {
        for name in [
            "", ".txt", "index.csv", "notes.txt",
            "2026-10-19 21.34.05 - VICTORY vs Gary.txt.tmp",
            "2026-10-19 21.34.05 - VICTORY vs Gary",
            "2026-10-19 21.34.05 - VICTORY vs .txt",
            "2026-10-19 21.34.05 - VICTORYvs Gary.txt",
            "2026-10-19 21.34.05 - WIN vs Gary.txt",
            "2026-10-19 21.34.05 - victory vs Gary.txt",
            "2026-10-19 21.34.05 - VICTORIA vs Gary.txt",
            "2026-10-19 21.34.05 -VICTORY.txt",
            "2026-10-19 21:34:05 - VICTORY.txt",
            "2026-13-45 21.34.05 - VICTORY.txt",
            "2026-10-19 21.34.05 - .txt",
            "2026-10-19 21.34.05 - EXPORTED (0).txt",
            "★.txt",
        ] {
            assert_eq!(parse(name), None, "{name}");
        }
    }

    #[test]
    fn keeps_the_date() {
        let p = parse("2026-10-19 21.34.05 - VICTORY vs Gary.txt").unwrap();
        assert_eq!(p.local_time.to_string(), "2026-10-19 21:34:05");
    }
}
