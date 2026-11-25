//! JSON exactly as the C# tray's Json.Write (DataContractJsonSerializer through an indented
//! JsonReaderWriterFactory writer) spells it: CRLF, two-space indent, `"key": value`, empty lists
//! as `[ ]`, and `/`, U+0085, U+2028, U+2029 and every UTF-16 surrogate escaped. Byte-for-byte
//! matters: the Pokematch uploader keys "already sent" on the SHA-256 of this text.
//! Checked against tests/golden/escape.json, written by the C# code (tests/RustGoldens).

use serde::Serialize;
use serde_json::Value;

const NL: &str = "\r\n";

pub fn to_string<T: Serialize>(value: &T) -> String {
    let mut out = String::new();
    write(&serde_json::to_value(value).expect("serializable"), 0, &mut out);
    out
}

fn indent(level: usize, out: &mut String) {
    for _ in 0..level {
        out.push_str("  ");
    }
}

fn write(v: &Value, level: usize, out: &mut String) {
    match v {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Value::Number(n) => out.push_str(&n.to_string()),
        Value::String(s) => string(s, out),
        Value::Array(items) if items.is_empty() => out.push_str("[ ]"),
        Value::Object(map) if map.is_empty() => out.push_str("{ }"),
        Value::Array(items) => {
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                out.push_str(if i == 0 { NL } else { ",\r\n" });
                indent(level + 1, out);
                write(item, level + 1, out);
            }
            out.push_str(NL);
            indent(level, out);
            out.push(']');
        }
        Value::Object(map) => {
            out.push('{');
            for (i, (k, item)) in map.iter().enumerate() {
                out.push_str(if i == 0 { NL } else { ",\r\n" });
                indent(level + 1, out);
                string(k, out);
                out.push_str(": ");
                write(item, level + 1, out);
            }
            out.push_str(NL);
            indent(level, out);
            out.push('}');
        }
    }
}

fn string(s: &str, out: &mut String) {
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '/' => out.push_str("\\/"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 || matches!(c, '\u{85}' | '\u{2028}' | '\u{2029}') => out.push_str(&format!("\\u{:04x}", c as u32)),
            c if (c as u32) > 0xFFFF => {
                let mut buf = [0u16; 2];
                for unit in c.encode_utf16(&mut buf) {
                    out.push_str(&format!("\\u{unit:04x}"));
                }
            }
            c => out.push(c),
        }
    }
    out.push('"');
}
