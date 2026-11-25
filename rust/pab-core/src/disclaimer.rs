//! DISCLAIMER.md as the consent dialog shows it, and THIRD-PARTY-NOTICES.md for the Licenses window
//! (Disclaimer.cs). The Markdown is written for GitHub, hard-wrapped at 100 columns; a dialog needs
//! plain paragraphs it can wrap itself.

use std::sync::LazyLock;

use fancy_regex::{Captures, Regex};

/// Stored in settings.disclaimerAccepted on acceptance. Bumping it no longer asks anyone again.
pub const VERSION: i32 = 5;

/// Accepted once (at install or the first portable run), accepted for good: a later text change does
/// not ask again, since every new prompt stalled uploads until the player found it.
pub fn is_accepted(accepted: i32) -> bool {
    accepted > 0
}

const NL: &str = "\r\n";

fn re(p: &str) -> Regex {
    Regex::new(p).expect("valid disclaimer regex")
}

// The C# patterns, unchanged (fancy-regex: they use lookaround and a backreference).
static HEADING: LazyLock<Regex> = LazyLock::new(|| re(r"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$"));
static LIST_ITEM: LazyLock<Regex> = LazyLock::new(|| re(r"^\s{0,3}(?:[*+-]|\d{1,3}[.)])\s+(.*)$"));
static LINK: LazyLock<Regex> = LazyLock::new(|| re(r"\[([^\]]+)\]\(([^)\s]+)\)"));
static STRONG: LazyLock<Regex> = LazyLock::new(|| re(r"(\*\*|__)(?=\S)(.+?)(?<=\S)\1"));
static EMPHASIS: LazyLock<Regex> = LazyLock::new(|| re(r"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])"));
static CODE: LazyLock<Regex> = LazyLock::new(|| re(r"`([^`]+)`"));
static COMMENT: LazyLock<Regex> = LazyLock::new(|| re(r"(?s)<!--.*?-->"));
static TABLE_ROW: LazyLock<Regex> = LazyLock::new(|| re(r"^\s{0,3}\|(.*)\|\s*$"));
static TABLE_RULE: LazyLock<Regex> = LazyLock::new(|| re(r"^\s{0,3}\|[\s:|-]+\|\s*$"));
static ESCAPE: LazyLock<Regex> = LazyLock::new(|| re(r"\\([\\`*_{}\[\]()#+\-.!])"));
static SPACES: LazyLock<Regex> = LazyLock::new(|| re(r" {2,}"));

fn matches(r: &Regex, s: &str) -> bool {
    r.is_match(s).unwrap_or(false)
}

fn caps<'a>(r: &Regex, s: &'a str) -> Option<Captures<'a, str>> {
    r.captures(s).ok().flatten()
}

/// Plain text: headings without '#', hard-wrapped lines joined into one line per paragraph, list
/// items as "• item", table rows one line each ("cell | cell", no rule line), emphasis and code
/// marks removed, links as "text (url)". Paragraphs are separated by a blank line; CRLF line ends.
pub fn to_plain_text(markdown: &str) -> String {
    if markdown.is_empty() {
        return String::new();
    }
    let normalized = markdown.replace("\r\n", "\n").replace('\r', "\n");
    let text = COMMENT.replace_all(normalized.trim_start_matches('\u{feff}'), "").into_owned();

    let mut blocks: Vec<String> = Vec::new();
    let mut paragraph = String::new();
    let mut list: Vec<String> = Vec::new();
    let mut table: Vec<String> = Vec::new();
    let mut in_fence = false;
    let mut fence = String::new();

    fn flush_paragraph(blocks: &mut Vec<String>, paragraph: &mut String) {
        if !paragraph.is_empty() {
            blocks.push(inline(paragraph));
        }
        paragraph.clear();
    }
    fn flush_list(blocks: &mut Vec<String>, list: &mut Vec<String>) {
        if !list.is_empty() {
            blocks.push(list.iter().map(|i| format!("• {}", inline(i))).collect::<Vec<_>>().join(NL));
        }
        list.clear();
    }
    fn flush_table(blocks: &mut Vec<String>, table: &mut Vec<String>) {
        if !table.is_empty() {
            blocks.push(table.join(NL));
        }
        table.clear();
    }

    for raw in text.split('\n') {
        let line = raw.trim_end();
        if line.trim_start().starts_with("```") {
            if in_fence {
                blocks.push(fence.trim_end_matches(['\r', '\n']).to_string());
                fence.clear();
            } else {
                flush_paragraph(&mut blocks, &mut paragraph);
                flush_list(&mut blocks, &mut list);
                flush_table(&mut blocks, &mut table);
            }
            in_fence = !in_fence;
            continue;
        }
        if in_fence {
            fence.push_str(line);
            fence.push_str(NL);
            continue;
        }
        if line.trim().is_empty() {
            flush_paragraph(&mut blocks, &mut paragraph);
            flush_list(&mut blocks, &mut list);
            flush_table(&mut blocks, &mut table);
            continue;
        }
        if let Some(row) = caps(&TABLE_ROW, line) {
            flush_paragraph(&mut blocks, &mut paragraph);
            flush_list(&mut blocks, &mut list);
            if !matches(&TABLE_RULE, line) {
                table.push(row[1].split('|').map(inline).collect::<Vec<_>>().join(" | "));
            }
            continue;
        }
        flush_table(&mut blocks, &mut table); // a table ends at the first line that is not a row

        if let Some(h) = caps(&HEADING, line) {
            flush_paragraph(&mut blocks, &mut paragraph);
            flush_list(&mut blocks, &mut list);
            if !h[1].is_empty() {
                blocks.push(inline(&h[1]));
            }
            continue;
        }
        if let Some(li) = caps(&LIST_ITEM, line) {
            flush_paragraph(&mut blocks, &mut paragraph);
            list.push(li[1].trim().to_string());
            continue;
        }
        if let Some(last) = list.last_mut() {
            last.push(' ');
            last.push_str(line.trim()); // continuation
            continue;
        }
        if !paragraph.is_empty() {
            paragraph.push(' ');
        }
        paragraph.push_str(line.trim());
    }
    if in_fence && !fence.is_empty() {
        blocks.push(fence.trim_end_matches(['\r', '\n']).to_string());
    }
    flush_paragraph(&mut blocks, &mut paragraph);
    flush_list(&mut blocks, &mut list);
    flush_table(&mut blocks, &mut table);
    blocks.join(&format!("{NL}{NL}"))
}

fn inline(s: &str) -> String {
    let s = CODE.replace_all(s, "$1");
    let s = LINK.replace_all(&s, |m: &Captures<str>| {
        let (text, url) = (&m[1], &m[2]);
        let lower = url.to_lowercase();
        let external = lower.starts_with("http://") || lower.starts_with("https://");
        if external && url != text { format!("{text} ({url})") } else { text.to_string() }
    });
    let s = STRONG.replace_all(&s, "$2");
    let s = EMPHASIS.replace_all(&s, "$1");
    let s = ESCAPE.replace_all(&s, "$1");
    let s = SPACES.replace_all(&s, " ");
    s.trim().to_string()
}
