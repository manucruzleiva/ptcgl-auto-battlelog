//! Small file helpers (FileOps.cs).

use std::fs;
use std::io;
use std::path::Path;

/// Writes beside (`<path>.tmp`), then renames over: a crash mid-write never leaves a truncated
/// file. std's rename replaces an existing file on Windows too (MoveFileEx REPLACE_EXISTING).
pub fn write_atomic(path: &Path, bytes: &[u8]) -> io::Result<()> {
    if let Some(dir) = path.parent().filter(|d| !d.as_os_str().is_empty()) {
        fs::create_dir_all(dir)?;
    }
    let mut tmp = path.as_os_str().to_owned();
    tmp.push(".tmp");
    let tmp = Path::new(&tmp);
    let result = fs::write(tmp, bytes).and_then(|_| fs::rename(tmp, path));
    if result.is_err() {
        let _ = fs::remove_file(tmp);
    }
    result
}

/// A small text file another process may be replacing; a UTF-8 BOM is dropped. None when absent,
/// unreadable, not UTF-8 or larger than `max_bytes`.
pub fn try_read_small_text(path: &Path, max_bytes: u64) -> Option<String> {
    if fs::metadata(path).ok()?.len() > max_bytes {
        return None;
    }
    let bytes = fs::read(path).ok()?;
    let bytes = bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes);
    String::from_utf8(bytes.to_vec()).ok()
}

/// The absolute form of `p` with "." and ".." resolved by name only, like .NET's Path.GetFullPath
/// (std::path::absolute leaves ".." alone on Unix). None for an empty path.
pub fn full_path(p: &Path) -> Option<std::path::PathBuf> {
    use std::path::Component;
    let abs = std::path::absolute(p).ok()?;
    let mut out = std::path::PathBuf::new();
    for c in abs.components() {
        match c {
            Component::CurDir => {}
            Component::ParentDir => {
                if !matches!(out.components().next_back(), Some(Component::RootDir | Component::Prefix(_)) | None) {
                    out.pop();
                }
            }
            c => out.push(c),
        }
    }
    Some(out)
}

/// `path` is `folder` or inside it, by full path, ignoring case (FileOps.IsSameOrUnder).
pub fn is_same_or_under(path: &Path, folder: &Path) -> bool {
    let full = |p: &Path| -> Option<String> {
        let s = p.to_str()?.trim();
        if s.is_empty() {
            return None;
        }
        let f = full_path(Path::new(s))?;
        Some(f.to_string_lossy().trim_end_matches(['/', '\\']).to_lowercase())
    };
    let (Some(p), Some(f)) = (full(path), full(folder)) else { return false };
    p == f || p.strip_prefix(&f).is_some_and(|rest| rest.starts_with(std::path::MAIN_SEPARATOR))
}

/// Deletes `dir` and everything in it, except `keep` when it is at or under `dir`: that folder, its
/// contents and the folders leading to it stay (battle logs are never deleted). True when something
/// was kept. Stops at the first failure.
pub fn delete_tree_except(dir: &Path, keep: Option<&Path>) -> io::Result<bool> {
    if !dir.is_dir() {
        return Ok(false);
    }
    let Some(keep) = keep.filter(|k| is_same_or_under(k, dir)) else {
        fs::remove_dir_all(dir)?;
        return Ok(false);
    };
    if is_same_or_under(dir, keep) {
        return Ok(true); // dir is the logs folder itself
    }
    for entry in fs::read_dir(dir)?.collect::<Result<Vec<_>, _>>()? {
        let path = entry.path();
        if entry.file_type()?.is_dir() {
            delete_tree_except(&path, Some(keep))?;
        } else {
            fs::remove_file(&path)?;
        }
    }
    Ok(true)
}
