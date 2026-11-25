//! DPAPI CurrentUser (ProtectedData.Protect/Unprotect with DataProtectionScope.CurrentUser), so the
//! Rust tray reads the token files the C# tray saved and the other way round.

use std::io;
use std::ptr;

use windows_sys::Win32::Foundation::LocalFree;
use windows_sys::Win32::Security::Cryptography::{CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN};

fn blob(data: &[u8]) -> CRYPT_INTEGER_BLOB {
    CRYPT_INTEGER_BLOB { cbData: data.len() as u32, pbData: data.as_ptr() as *mut u8 }
}

fn take(out: CRYPT_INTEGER_BLOB) -> Vec<u8> {
    // SAFETY: on success DPAPI hands back a LocalAlloc'd buffer of cbData bytes, freed here once copied.
    unsafe {
        let v = std::slice::from_raw_parts(out.pbData, out.cbData as usize).to_vec();
        LocalFree(out.pbData as _);
        v
    }
}

pub fn protect(data: &[u8], entropy: &[u8]) -> io::Result<Vec<u8>> {
    let (input, ent) = (blob(data), blob(entropy));
    let mut out = CRYPT_INTEGER_BLOB { cbData: 0, pbData: ptr::null_mut() };
    // SAFETY: input and entropy point at live slices for the call; out is written by the API.
    let ok = unsafe { CryptProtectData(&input, ptr::null(), &ent, ptr::null(), ptr::null(), CRYPTPROTECT_UI_FORBIDDEN, &mut out) };
    if ok == 0 { Err(io::Error::last_os_error()) } else { Ok(take(out)) }
}

pub fn unprotect(data: &[u8], entropy: &[u8]) -> io::Result<Vec<u8>> {
    let (input, ent) = (blob(data), blob(entropy));
    let mut out = CRYPT_INTEGER_BLOB { cbData: 0, pbData: ptr::null_mut() };
    // SAFETY: as in protect.
    let ok = unsafe { CryptUnprotectData(&input, ptr::null_mut(), &ent, ptr::null(), ptr::null(), CRYPTPROTECT_UI_FORBIDDEN, &mut out) };
    if ok == 0 { Err(io::Error::last_os_error()) } else { Ok(take(out)) }
}

#[cfg(test)]
mod tests {
    #[test]
    fn round_trip_and_entropy_matters() {
        let sealed = super::protect(b"secret", b"PtcglAutoBattleLog/tcgkb-connection/v1").unwrap();
        assert_ne!(sealed, b"secret");
        assert_eq!(super::unprotect(&sealed, b"PtcglAutoBattleLog/tcgkb-connection/v1").unwrap(), b"secret");
        assert!(super::unprotect(&sealed, b"other").is_err());
    }
}
