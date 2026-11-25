//! The tray icon in its four states (StatusIcons.cs): the app icon, or the icon with a green, amber
//! or red dot in the corner. Pixels only; turning them into an HICON is win::notify_icon's job.
use pab_core::daemon_monitor::TrayHealth;

/// The dot's colour for a state, None for Idle. The status window's dot uses the same.
pub fn dot_color(health: TrayHealth) -> Option<[u8; 3]> {
    match health {
        TrayHealth::Active => Some([0x2E, 0xB8, 0x4F]),
        TrayHealth::Attention => Some([0xF5, 0xA6, 0x23]),
        TrayHealth::Error => Some([0xE0, 0x3A, 0x3A]),
        _ => None,
    }
}

/// The PNG frame of an .ico nearest `size`: the smallest at least that big, else the largest.
fn png_frame(ico: &[u8], size: u32) -> Option<&[u8]> {
    let count = u16::from_le_bytes(ico.get(4..6)?.try_into().ok()?) as usize;
    let mut best: Option<(u32, &[u8])> = None;
    for i in 0..count {
        let e = ico.get(6 + 16 * i..22 + 16 * i)?;
        let side = if e[0] == 0 { 256 } else { e[0] as u32 };
        let len = u32::from_le_bytes(e[8..12].try_into().ok()?) as usize;
        let off = u32::from_le_bytes(e[12..16].try_into().ok()?) as usize;
        let data = ico.get(off..off.checked_add(len)?)?;
        if !data.starts_with(b"\x89PNG") {
            continue;
        }
        let better = match best {
            None => true,
            Some((b, _)) if b < size => side > b,
            Some((b, _)) => side >= size && side < b,
        };
        if better {
            best = Some((side, data));
        }
    }
    best.map(|(_, d)| d)
}

fn decode_rgba(png_bytes: &[u8]) -> Option<(u32, Vec<u8>)> {
    let mut decoder = png::Decoder::new(std::io::Cursor::new(png_bytes));
    decoder.set_transformations(png::Transformations::EXPAND | png::Transformations::ALPHA | png::Transformations::STRIP_16);
    let mut reader = decoder.read_info().ok()?;
    let mut buf = vec![0; reader.output_buffer_size()?];
    let info = reader.next_frame(&mut buf).ok()?;
    if info.width != info.height || info.color_type != png::ColorType::Rgba {
        return None;
    }
    buf.truncate(info.buffer_size());
    Some((info.width, buf))
}

/// The icon at `size`×`size` as straight RGBA, top row first, with the state's dot. A box filter
/// when the frame is larger; None when the icon has no usable frame.
pub fn draw(ico: &[u8], size: u32, dot: Option<[u8; 3]>) -> Option<Vec<u8>> {
    let (src, art) = decode_rgba(png_frame(ico, size)?)?;
    let mut out = vec![0u8; (size * size * 4) as usize];
    for y in 0..size {
        for x in 0..size {
            // Premultiplied average of the source pixels this one covers.
            let (x0, x1) = (x * src / size, ((x + 1) * src / size).max(x * src / size + 1));
            let (y0, y1) = (y * src / size, ((y + 1) * src / size).max(y * src / size + 1));
            let mut acc = [0f32; 4];
            for sy in y0..y1.min(src) {
                for sx in x0..x1.min(src) {
                    let p = &art[((sy * src + sx) * 4) as usize..][..4];
                    let a = p[3] as f32 / 255.0;
                    for c in 0..3 {
                        acc[c] += p[c] as f32 * a;
                    }
                    acc[3] += a;
                }
            }
            let n = ((x1.min(src) - x0) * (y1.min(src) - y0)).max(1) as f32;
            let px = [acc[0] / n, acc[1] / n, acc[2] / n, acc[3] / n];
            put(&mut out, size, x, y, px);
        }
    }
    if let Some(color) = dot {
        // Half the icon, bottom-right, with a white ring so it reads on dark and light taskbars.
        let s = size as f32;
        let (d, ring) = (s * 0.5, (s / 16.0).max(1.0));
        let (cx, cy, r) = (s - d / 2.0 - ring / 2.0, s - d / 2.0 - ring / 2.0, d / 2.0);
        const SS: u32 = 4; // supersampling for an anti-aliased edge
        for y in 0..size {
            for x in 0..size {
                let (mut fill, mut white) = (0u32, 0u32);
                for sy in 0..SS {
                    for sx in 0..SS {
                        let px = x as f32 + (sx as f32 + 0.5) / SS as f32;
                        let py = y as f32 + (sy as f32 + 0.5) / SS as f32;
                        let dist = ((px - cx).powi(2) + (py - cy).powi(2)).sqrt();
                        if (dist - r).abs() <= ring / 2.0 {
                            white += 1;
                        } else if dist < r {
                            fill += 1;
                        }
                    }
                }
                let total = (SS * SS) as f32;
                let (f, w) = (fill as f32 / total, white as f32 / total);
                if f + w == 0.0 {
                    continue;
                }
                let i = ((y * size + x) * 4) as usize;
                let under_a = out[i + 3] as f32 / 255.0;
                let cover = f + w;
                let mut px = [0f32; 4];
                for c in 0..3 {
                    let top = color[c] as f32 * f + 255.0 * w;
                    px[c] = top + out[i + c] as f32 * under_a * (1.0 - cover);
                }
                px[3] = cover + under_a * (1.0 - cover);
                put(&mut out, size, x, y, px);
            }
        }
    }
    Some(out)
}

/// Writes a premultiplied pixel (colour 0..255 × alpha, alpha 0..1) as straight RGBA.
fn put(out: &mut [u8], size: u32, x: u32, y: u32, px: [f32; 4]) {
    let i = ((y * size + x) * 4) as usize;
    let a = px[3].clamp(0.0, 1.0);
    for c in 0..3 {
        out[i + c] = if a > 0.0 { (px[c] / a).round().clamp(0.0, 255.0) as u8 } else { 0 };
    }
    out[i + 3] = (a * 255.0).round() as u8;
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::embedded::APP_ICON;

    #[test]
    fn icons_for_every_state_and_size() {
        assert_eq!(png_frame(APP_ICON, 16).map(|f| decode_rgba(f).unwrap().0), Some(16));
        assert_eq!(png_frame(APP_ICON, 18).map(|f| decode_rgba(f).unwrap().0), Some(20));
        assert_eq!(png_frame(APP_ICON, 999).map(|f| decode_rgba(f).unwrap().0), Some(256));
        for size in [16, 20, 24, 32, 18] {
            let idle = draw(APP_ICON, size, None).unwrap();
            assert_eq!(idle.len() as u32, size * size * 4);
            let red = draw(APP_ICON, size, dot_color(TrayHealth::Error)).unwrap();
            // Inside the dot: the dot's colour, opaque. Top-left: the icon, untouched.
            let c = (((size * 3 / 4) * size + size * 3 / 4 - 1) * 4) as usize;
            assert_eq!(&red[c..c + 4], &[0xE0, 0x3A, 0x3A, 255], "size {size}");
            assert_eq!(&red[..4 * 4], &idle[..4 * 4]);
        }
        assert!(draw(b"not an icon", 16, None).is_none());
        assert_eq!(dot_color(TrayHealth::Idle), None);
    }
}
