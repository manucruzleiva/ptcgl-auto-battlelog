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
