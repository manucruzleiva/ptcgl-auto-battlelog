//! BepInEx 5 .cfg text, read and edited the way BepInEx itself parses it (BepInExConfig.cs):
//! "[Section]" headers, "Key = Value" lines, '#' comments, values trimmed, the last duplicate wins.
//! String values are unescaped (\\, \", \n...) except one that looks like a Windows path
//! ("C:\dir\file"), which BepInEx keeps verbatim; that is also how BepInEx writes paths.

use std::sync::LazyLock;

use regex::Regex;

static DRIVE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"^"?\w:\\"#).expect("valid regex"));

/// The value of `key` in `section`, as BepInEx reads it. None when absent.
pub fn read_value(text: &str, section: &str, key: &str) -> Option<String> {
    let (lines, _, _) = split_lines(text);
    let mut current = "";
    let mut value = None;
    for raw in &lines {
        let line = raw.trim();
        if line.starts_with('#') {
            continue;
        }
        if is_header(line) {
            current = &line[1..line.len() - 1];
            continue;
        }
        if let Some((k, v)) = line.split_once('=') {
            if current == section && k.trim() == key {
                value = Some(v.trim());
            }
        }
    }
    value.map(convert_to_object)
}

/// Sets every occurrence of the key in the section, or adds it right under the section's first
/// header, or appends the section. Other lines, comments, line endings and the trailing newline
/// stay exactly. Err for a value BepInEx would not read back as written (see [`serialize`]).
pub fn write_value(text: &str, section: &str, key: &str, value: &str) -> Result<String, String> {
    let (mut lines, nl, trailing_newline) = split_lines(text);
    let entry = format!("{key} = {}", serialize(value)?);
    let mut current = String::new();
    let mut header = None;
    let mut replaced = false;
    for (i, raw) in lines.iter_mut().enumerate() {
        let line = raw.trim();
        if line.starts_with('#') {
            continue;
        }
        if is_header(line) {
            current = line[1..line.len() - 1].to_string();
            if current == section && header.is_none() {
                header = Some(i);
            }
            continue;
        }
        if line.split_once('=').is_some_and(|(k, _)| current == section && k.trim() == key) {
            *raw = entry.clone();
            replaced = true;
        }
    }
    if !replaced {
        match header {
            Some(h) => lines.insert(h + 1, entry),
            None => {
                if lines.last().is_some_and(|l| !l.trim().is_empty()) {
                    lines.push(String::new());
                }
                lines.push(format!("[{section}]"));
                lines.push(entry);
            }
        }
    }
    let end = if trailing_newline || text.is_empty() { nl } else { "" };
    Ok(lines.join(nl) + end)
}

fn is_header(trimmed: &str) -> bool {
    trimmed.len() >= 2 && trimmed.starts_with('[') && trimmed.ends_with(']')
}

/// Lines split on "\r\n" or "\n", the newline to write back (CRLF unless the text uses only LF)
/// and whether the text ended with one.
fn split_lines(text: &str) -> (Vec<String>, &'static str, bool) {
    let nl = if text.contains("\r\n") || !text.contains('\n') { "\r\n" } else { "\n" };
    let mut lines: Vec<String> = text.split('\n').map(|l| l.strip_suffix('\r').unwrap_or(l).to_string()).collect();
    let trailing = !text.is_empty() && lines.last().is_some_and(|l| l.is_empty());
    if trailing || text.is_empty() {
        lines.pop();
    }
    (lines, nl, trailing)
}

/// The text after "Key = ": exactly what BepInEx itself writes. BepInEx rewrites the cfg at every
/// game start, so a value it would not read back the same (5.4.23 garbles ' and " in a "C:\..."
/// path, and network paths like \\server\share) is refused rather than silently changing later.
pub fn serialize(value: &str) -> Result<String, String> {
    let text = escape(value);
    if convert_to_object(text.trim()) != value {
        return Err(format!(
            "BepInEx 5 cannot keep this folder in its config: {value}. Avoid ' and \" in the path, and network paths (\\\\server\\share); a mapped drive letter works."
        ));
    }
    Ok(text)
}

/// A value as BepInEx reads it: kept verbatim when it looks like an unescaped Windows path, else unescaped.
pub fn convert_to_object(s: &str) -> String {
    if looks_like_raw_path(s) { s.to_string() } else { unescape(s) }
}

/// BepInEx's `^"?\w:\\(?!\\)(?!.+\\\\)`: a drive path whose rest has no doubled backslash.
fn looks_like_raw_path(s: &str) -> bool {
    let Some(m) = DRIVE.find(s) else { return false };
    let rest = &s[m.end()..];
    if rest.starts_with('\\') {
        return false;
    }
    // `.` stops at a newline; the doubled backslash must start after at least one character.
    let line = rest.split('\n').next().unwrap_or("");
    !line.char_indices().any(|(i, _)| i > 0 && line[i..].starts_with("\\\\"))
}

/// BepInEx 5.4.23's own escaping (TomlTypeConverter.Escape): '\' itself is left alone.
pub fn escape(txt: &str) -> String {
    let mut sb = String::with_capacity(txt.len() + 2);
    for c in txt.chars() {
        match c {
            '\0' => sb.push_str("\\0"),
            '\u{7}' => sb.push_str("\\a"),
            '\u{8}' => sb.push_str("\\b"),
            '\t' => sb.push_str("\\t"),
            '\n' => sb.push_str("\\n"),
            '\u{b}' => sb.push_str("\\v"),
            '\u{c}' => sb.push_str("\\f"),
            '\r' => sb.push_str("\\r"),
            '\'' => sb.push_str("\\'"),
            '"' => sb.push_str("\\\""),
            c => sb.push(c),
        }
    }
    sb
}

/// TomlTypeConverter.Unescape: unknown escapes and a lone trailing '\' stay as they are.
pub fn unescape(txt: &str) -> String {
    let mut sb = String::with_capacity(txt.len());
    let mut chars = txt.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            sb.push(c);
            continue;
        }
        let Some(n) = chars.next() else {
            sb.push('\\');
            break;
        };
        match n {
            '0' => sb.push('\0'),
            'a' => sb.push('\u{7}'),
            'b' => sb.push('\u{8}'),
            't' => sb.push('\t'),
            'n' => sb.push('\n'),
            'v' => sb.push('\u{b}'),
            'f' => sb.push('\u{c}'),
            'r' => sb.push('\r'),
            '\'' => sb.push('\''),
            '"' => sb.push('"'),
            '\\' => sb.push('\\'),
            n => {
                sb.push('\\');
                sb.push(n);
            }
        }
    }
    sb
}
