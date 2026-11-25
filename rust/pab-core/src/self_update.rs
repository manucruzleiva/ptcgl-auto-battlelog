//! Replaces the installed exe with a verified download (SelfUpdate.cs). Windows lets a running exe
//! be renamed but not overwritten, hence the .old dance. Starting the new exe is the caller's job.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use crate::file_ops::full_path;

fn old_of(installed: &Path) -> PathBuf {
    let mut s = installed.as_os_str().to_owned();
    s.push(".old");
    PathBuf::from(s)
}

/// installed → installed.old (a stale .old is deleted first), new → installed. If the second step
/// fails, the old exe is put back before the error is returned, so there is always an exe to start.
pub fn swap(new_exe: &Path, installed: &Path) -> io::Result<()> {
    swap_with(new_exe, installed, &|a, b| fs::rename(a, b))
}

/// swap, with the rename injectable so tests can make it fail.
pub fn swap_with(new_exe: &Path, installed: &Path, mv: &dyn Fn(&Path, &Path) -> io::Result<()>) -> io::Result<()> {
    let same = |a: &Path, b: &Path| full_path(a).map(|p| p.to_string_lossy().to_lowercase()) == full_path(b).map(|p| p.to_string_lossy().to_lowercase());
    if new_exe.as_os_str().is_empty() || installed.as_os_str().is_empty() || same(new_exe, installed) {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "The new exe is the installed exe."));
    }
    if !new_exe.is_file() {
        return Err(io::Error::new(io::ErrorKind::NotFound, format!("The downloaded update is missing: {}", new_exe.display())));
    }
    let old = old_of(installed);
    if old.exists() {
        fs::remove_file(&old)?; // fails before anything moved if it is still in use
    }
    let moved_aside = installed.exists();
    if moved_aside {
        mv(installed, &old)?;
    }
    match mv(new_exe, installed) {
        Ok(()) => Ok(()),
        Err(cause) if moved_aside => Err(restore(&old, installed, cause, mv)),
        Err(cause) => Err(cause),
    }
}

fn restore(old: &Path, installed: &Path, cause: io::Error, mv: &dyn Fn(&Path, &Path) -> io::Result<()>) -> io::Error {
    if installed.exists() {
        let _ = fs::remove_file(installed); // a partial copy across volumes
    }
    if mv(old, installed).is_ok() {
        return cause;
    }
    // A copy works where a rename does not: the player keeps a working exe either way.
    match fs::copy(old, installed) {
        Ok(_) => cause,
        Err(e2) => io::Error::other(format!("Update failed ({cause}) and the previous version could not be restored from {} ({e2}).", old.display())),
    }
}

/// Deletes installed.old left by the previous update. Best effort.
pub fn cleanup_old(installed: &Path) {
    if !installed.as_os_str().is_empty() {
        let _ = fs::remove_file(old_of(installed));
    }
}
