use sha2::{Digest, Sha256};

pub fn sha256(bytes: &[u8]) -> [u8; 32] {
    Sha256::digest(bytes).into()
}

pub fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// 64 hex digits, either case → the 32 bytes. Anything else is None.
pub fn parse_sha256(hex: &str) -> Option<[u8; 32]> {
    let h = hex.as_bytes();
    if h.len() != 64 {
        return None;
    }
    let mut out = [0u8; 32];
    for (i, pair) in h.chunks(2).enumerate() {
        let v = |c: u8| (c as char).to_digit(16).map(|d| d as u8);
        out[i] = (v(pair[0])? << 4) | v(pair[1])?;
    }
    Some(out)
}
