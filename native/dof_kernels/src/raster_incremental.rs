// SPDX-License-Identifier: GPL-3.0-only

/// Computes total byte size of a full Godot mipmap pyramid (level 0 down to 1x1 inclusive).
pub fn mipmap_chain_bytes(width: usize, height: usize, bpp: usize) -> Option<usize> {
    if width == 0 || height == 0 || bpp == 0 {
        return None;
    }
    let mut w = width;
    let mut h = height;
    let mut total = 0usize;
    loop {
        let level = w.checked_mul(h)?.checked_mul(bpp)?;
        total = total.checked_add(level)?;
        if w == 1 && h == 1 {
            break;
        }
        w = (w >> 1).max(1);
        h = (h >> 1).max(1);
    }
    Some(total)
}

#[inline(always)]
fn effective_holder_inc(
    authored: &[u16],
    provs: &[u16],
    lut: &[u16],
    has_override: bool,
    idx: usize,
) -> u16 {
    let ah = unsafe { *authored.get_unchecked(idx) };
    if ah == 0 || !has_override {
        return ah;
    }
    let prov = unsafe { *provs.get_unchecked(idx) as usize };
    let ov = unsafe { *lut.get_unchecked(prov) };
    if ov == 0 {
        ah
    } else {
        ov
    }
}

/// Incremental dirty-rect political plate repaint, guaranteed 100% bit-exact with `paint_political_plate`.
/// Writes ONLY pixels inside `[rect_x0..rect_x1) x [rect_y0..rect_y1)` directly into `out_pixels_rgba`.
#[allow(clippy::too_many_arguments)]
pub fn paint_political_plate_rect(
    width: usize,
    height: usize,
    source_holders: &[u16],
    source_provinces: &[u16],
    source_geography: &[u8],
    owners: &[u16],
    authored_owners: &[u16],
    palette_rgba: &[u32],
    country_shade: f32,
    region_shade: f32,
    inland_shade: f32,
    rect_x0: usize,
    rect_y0: usize,
    rect_x1: usize,
    rect_y1: usize,
    out_pixels_rgba: &mut [u8],
) -> Result<(), i32> {
    if width == 0 || height == 0 {
        return Err(-2);
    }
    let cells = width.checked_mul(height).ok_or(-2)?;
    if cells > 64_000_000 {
        return Err(-2);
    }
    if source_holders.len() < cells
        || source_provinces.len() < cells
        || source_geography.len() < cells
    {
        return Err(-2);
    }
    let needed_bytes = cells.checked_mul(4).ok_or(-3)?;
    if out_pixels_rgba.len() < needed_bytes {
        return Err(-3);
    }
    if !country_shade.is_finite()
        || country_shade < 0.0
        || !region_shade.is_finite()
        || region_shade < 0.0
        || !inland_shade.is_finite()
        || inland_shade < 0.0
    {
        return Err(-2);
    }

    let x0 = rect_x0.min(width);
    let x1 = rect_x1.min(width);
    let y0 = rect_y0.min(height);
    let y1 = rect_y1.min(height);
    if x0 >= x1 || y0 >= y1 {
        return Ok(());
    }

    let mut prov_lut = vec![0u16; 65_536];
    let mut has_override = false;
    if !owners.is_empty() && !authored_owners.is_empty() {
        let limit = owners.len().min(authored_owners.len()).min(65_536);
        for p in 1..limit {
            let owner = owners[p];
            if authored_owners[p] != owner {
                prov_lut[p] = if owner == 0 { 65_535 } else { owner };
                has_override = true;
            }
        }
    }

    let pal_len = palette_rgba.len();
    for y in y0..y1 {
        let row = y * width;
        let has_up = y > 0;
        let has_down = y + 1 < height;
        for x in x0..x1 {
            let cell = row + x;
            let write = cell * 4;
            let holder = effective_holder_inc(source_holders, source_provinces, &prov_lut, has_override, cell);
            if holder == 0 {
                out_pixels_rgba[write] = 0;
                out_pixels_rgba[write + 1] = 0;
                out_pixels_rgba[write + 2] = 0;
                out_pixels_rgba[write + 3] = 0;
                continue;
            }

            let geo = unsafe { *source_geography.get_unchecked(cell) };
            let shade = if (geo & 1) != 0 {
                country_shade
            } else {
                let east_x = if x + 1 == width { 0 } else { x + 1 };
                let west_x = if x == 0 { width - 1 } else { x - 1 };
                let east = effective_holder_inc(source_holders, source_provinces, &prov_lut, has_override, row + east_x);
                let west = effective_holder_inc(source_holders, source_provinces, &prov_lut, has_override, row + west_x);
                let is_foreign_ew = (east != 0 && east != holder) || (west != 0 && west != holder);
                let is_foreign_ns = (has_up && {
                    let north = effective_holder_inc(source_holders, source_provinces, &prov_lut, has_override, cell - width);
                    north != 0 && north != holder
                }) || (has_down && {
                    let south = effective_holder_inc(source_holders, source_provinces, &prov_lut, has_override, cell + width);
                    south != 0 && south != holder
                });
                if is_foreign_ew || is_foreign_ns {
                    country_shade
                } else if (geo & 2) != 0 {
                    region_shade
                } else {
                    inland_shade
                }
            };

            let packed = if (holder as usize) < pal_len {
                unsafe { *palette_rgba.get_unchecked(holder as usize) }
            } else {
                0x6E7B8BFFu32
            };
            out_pixels_rgba[write] = (((packed >> 24) & 0xFF) as f32 * shade) as u8;
            out_pixels_rgba[write + 1] = (((packed >> 16) & 0xFF) as f32 * shade) as u8;
            out_pixels_rgba[write + 2] = (((packed >> 8) & 0xFF) as f32 * shade) as u8;
            out_pixels_rgba[write + 3] = 255u8;
        }
    }

    Ok(())
}

/// Incrementally updates mipmap levels 1..N of a tightly packed Godot RGBA8 mipmap chain
/// for the dirty rectangle `[rect_x0..rect_x1) x [rect_y0..rect_y1)` in level-0 pixels.
pub fn mipmap_rgba8_rect(
    width: usize,
    height: usize,
    rect_x0: usize,
    rect_y0: usize,
    rect_x1: usize,
    rect_y1: usize,
    mip_buffer: &mut [u8],
) -> Result<(), i32> {
    let total = mipmap_chain_bytes(width, height, 4).ok_or(-2)?;
    if mip_buffer.len() < total {
        return Err(-3);
    }
    let mut sx0 = rect_x0.min(width);
    let mut sx1 = rect_x1.min(width);
    let mut sy0 = rect_y0.min(height);
    let mut sy1 = rect_y1.min(height);
    if sx0 >= sx1 || sy0 >= sy1 {
        return Ok(());
    }

    let mut pw = width;
    let mut ph = height;
    let mut prev_off = 0usize;
    while pw > 1 || ph > 1 {
        let nw = (pw >> 1).max(1);
        let nh = (ph >> 1).max(1);
        let next_off = prev_off + pw * ph * 4;
        let dx0 = sx0 >> 1;
        let dx1 = ((sx1 + 1) >> 1).min(nw);
        let dy0 = sy0 >> 1;
        let dy1 = ((sy1 + 1) >> 1).min(nh);

        let (head, tail) = mip_buffer.split_at_mut(next_off);
        let src = &head[prev_off..];
        let dst = &mut tail[..nw * nh * 4];

        for dy in dy0..dy1 {
            let y0 = (dy * 2).min(ph - 1);
            let y1 = (y0 + 1).min(ph - 1);
            let srow0 = y0 * pw * 4;
            let srow1 = y1 * pw * 4;
            let drow = dy * nw * 4;
            for dx in dx0..dx1 {
                let x0 = (dx * 2).min(pw - 1);
                let x1 = (x0 + 1).min(pw - 1);
                let p00 = srow0 + x0 * 4;
                let p10 = srow0 + x1 * 4;
                let p01 = srow1 + x0 * 4;
                let p11 = srow1 + x1 * 4;
                let doff = drow + dx * 4;
                unsafe {
                    for ch in 0..4 {
                        let sum = *src.get_unchecked(p00 + ch) as u32
                            + *src.get_unchecked(p10 + ch) as u32
                            + *src.get_unchecked(p01 + ch) as u32
                            + *src.get_unchecked(p11 + ch) as u32;
                        *dst.get_unchecked_mut(doff + ch) = ((sum + 2) >> 2) as u8;
                    }
                }
            }
        }

        prev_off = next_off;
        pw = nw;
        ph = nh;
        sx0 = dx0;
        sx1 = dx1;
        sy0 = dy0;
        sy1 = dy1;
    }

    Ok(())
}

/// Incrementally updates mipmap levels 1..N of a tightly packed Godot R8 mipmap chain
/// for the dirty rectangle `[rect_x0..rect_x1) x [rect_y0..rect_y1)` in level-0 pixels.
pub fn mipmap_r8_rect(
    width: usize,
    height: usize,
    rect_x0: usize,
    rect_y0: usize,
    rect_x1: usize,
    rect_y1: usize,
    mip_buffer: &mut [u8],
) -> Result<(), i32> {
    let total = mipmap_chain_bytes(width, height, 1).ok_or(-2)?;
    if mip_buffer.len() < total {
        return Err(-3);
    }
    let mut sx0 = rect_x0.min(width);
    let mut sx1 = rect_x1.min(width);
    let mut sy0 = rect_y0.min(height);
    let mut sy1 = rect_y1.min(height);
    if sx0 >= sx1 || sy0 >= sy1 {
        return Ok(());
    }

    let mut pw = width;
    let mut ph = height;
    let mut prev_off = 0usize;
    while pw > 1 || ph > 1 {
        let nw = (pw >> 1).max(1);
        let nh = (ph >> 1).max(1);
        let next_off = prev_off + pw * ph;
        let dx0 = sx0 >> 1;
        let dx1 = ((sx1 + 1) >> 1).min(nw);
        let dy0 = sy0 >> 1;
        let dy1 = ((sy1 + 1) >> 1).min(nh);

        let (head, tail) = mip_buffer.split_at_mut(next_off);
        let src = &head[prev_off..];
        let dst = &mut tail[..nw * nh];

        for dy in dy0..dy1 {
            let y0 = (dy * 2).min(ph - 1);
            let y1 = (y0 + 1).min(ph - 1);
            let srow0 = y0 * pw;
            let srow1 = y1 * pw;
            let drow = dy * nw;
            for dx in dx0..dx1 {
                let x0 = (dx * 2).min(pw - 1);
                let x1 = (x0 + 1).min(pw - 1);
                unsafe {
                    let sum = *src.get_unchecked(srow0 + x0) as u32
                        + *src.get_unchecked(srow0 + x1) as u32
                        + *src.get_unchecked(srow1 + x0) as u32
                        + *src.get_unchecked(srow1 + x1) as u32;
                    *dst.get_unchecked_mut(drow + dx) = ((sum + 2) >> 2) as u8;
                }
            }
        }

        prev_off = next_off;
        pw = nw;
        ph = nh;
        sx0 = dx0;
        sx1 = dx1;
        sy0 = dy0;
        sy1 = dy1;
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::raster::{paint_political_plate, DofRasterBake};

    #[test]
    fn incremental_plate_and_mipmaps_match_full_bit_exact() {
        let (w, h) = (192usize, 96usize);
        let cells = w * h;
        let mut holders = vec![1u16; cells];
        let mut provs = vec![1u16; cells];
        let mut geo = vec![0u8; cells];
        for y in 0..h {
            for x in 0..w {
                let c = y * w + x;
                if y == 0 || y + 1 == h {
                    holders[c] = 0;
                    provs[c] = 0;
                } else if x >= 60 && x < 100 && y >= 25 && y < 65 {
                    holders[c] = 2;
                    provs[c] = 2;
                } else {
                    provs[c] = 1;
                    if (x + y) % 13 == 0 {
                        geo[c] = 2;
                    }
                }
            }
        }
        let authored = [0u16, 1, 2];
        let owners_before = [0u16, 1, 2];
        let owners_after = [0u16, 1, 3]; // province 2 changed from 2 -> 3
        let mut pal = vec![0x6E7B8BFFu32; 65_536];
        pal[1] = 0xC83232FF;
        pal[2] = 0x32C864FF;
        pal[3] = 0x3264DCFF;

        let mip_bytes = mipmap_chain_bytes(w, h, 4).unwrap();
        let mut inc = vec![0u8; mip_bytes];
        let mut full = vec![0u8; mip_bytes];

        // Start inc from owners_before + full mipmaps
        paint_political_plate(w, h, &holders, &provs, &geo, &owners_before, &authored, &pal, 0.34, 0.70, 0.92, 1, &mut inc[..cells * 4]).unwrap();
        mipmap_rgba8_rect(w, h, 0, 0, w, h, &mut inc).unwrap();

        // Incrementally repaint dirty bbox of province 2 padded by 1 cell: [59..101) x [24..66)
        paint_political_plate_rect(w, h, &holders, &provs, &geo, &owners_after, &authored, &pal, 0.34, 0.70, 0.92, 59, 24, 101, 66, &mut inc[..cells * 4]).unwrap();
        mipmap_rgba8_rect(w, h, 59, 24, 101, 66, &mut inc).unwrap();

        // Full repaint + full mipmap of owners_after
        paint_political_plate(w, h, &holders, &provs, &geo, &owners_after, &authored, &pal, 0.34, 0.70, 0.92, 1, &mut full[..cells * 4]).unwrap();
        mipmap_rgba8_rect(w, h, 0, 0, w, h, &mut full).unwrap();

        assert_eq!(inc, full);
    }

    #[test]
    fn incremental_plate_rect_matches_full_when_authored_owners_empty() {
        let (w, h) = (64usize, 32usize);
        let cells = w * h;
        let mut holders = vec![1u16; cells];
        let mut provs = vec![1u16; cells];
        let geo = vec![0u8; cells];
        for y in 4..28 {
            for x in 16..48 {
                holders[y * w + x] = 2;
                provs[y * w + x] = 2;
            }
        }
        let owners = [0u16, 1, 3];
        let mut pal = vec![0x6E7B8BFFu32; 65_536];
        pal[1] = 0xC83232FF;
        pal[2] = 0x32C864FF;
        pal[3] = 0x3264DCFF;

        let mut full = vec![0u8; cells * 4];
        let mut rect = vec![0u8; cells * 4];
        paint_political_plate(w, h, &holders, &provs, &geo, &owners, &[], &pal, 0.34, 0.70, 0.92, 1, &mut full).unwrap();
        paint_political_plate_rect(w, h, &holders, &provs, &geo, &owners, &[], &pal, 0.34, 0.70, 0.92, 0, 0, w, h, &mut rect).unwrap();
        assert_eq!(rect, full, "empty authored_owners must preserve authored holders identically across plate paths");

        unsafe {
            let mut bake = DofRasterBake::from_raw(w as u32, h as u32, holders.as_ptr(), provs.as_ptr()).unwrap();
            let mut f_empty_auth = vec![0u8; cells];
            let mut f_no_owners = vec![0u8; cells];
            bake.bake_frontier(&owners, &[], 20.0, 1.0, 1, &mut f_empty_auth).unwrap();
            bake.bake_frontier(&[], &[], 20.0, 1.0, 1, &mut f_no_owners).unwrap();
            assert_eq!(f_empty_auth, f_no_owners, "empty authored_owners must match MapBorders.CurrentHolder (no override) in frontier bake");
        }
    }

    #[test]
    fn incremental_frontier_rect_matches_full_bit_exact() {
        let (w, h) = (320usize, 180usize);
        let cells = w * h;
        let mut holders = vec![1u16; cells];
        let mut provs = vec![1u16; cells];
        for y in 0..h {
            for x in 0..w {
                let c = y * w + x;
                if x >= 110 && x < 170 && y >= 50 && y < 120 {
                    holders[c] = 2;
                    provs[c] = 2;
                } else if x >= 170 && x < 220 && y >= 60 && y < 130 {
                    holders[c] = 3;
                    provs[c] = 3;
                }
            }
        }
        let authored = [0u16, 1, 2, 3];
        let owners_before = [0u16, 1, 2, 3];
        let owners_after = [0u16, 1, 1, 3]; // province 2 conquered by 1!

        unsafe {
            let mut bake = DofRasterBake::from_raw(w as u32, h as u32, holders.as_ptr(), provs.as_ptr()).unwrap();
            let mip_bytes = mipmap_chain_bytes(w, h, 1).unwrap();
            let mut inc = vec![0u8; mip_bytes];
            let mut full = vec![0u8; mip_bytes];

            // Initial bake with owners_before
            bake.bake_frontier(&owners_before, &authored, 20.0, 1.0, 2, &mut inc[..cells]).unwrap();
            mipmap_r8_rect(w, h, 0, 0, w, h, &mut inc).unwrap();

            // Dirty bbox of province 2 is [110..170) x [50..120).
            // Affected output region extends by ceil(cap) + ceil(3*smoothing) + 2 = 26 cells around changed cells!
            let pad = 26usize;
            let rx0 = 110 - pad;
            let ry0 = 50 - pad;
            let rx1 = 170 + pad;
            let ry1 = 120 + pad;
            bake.bake_frontier_rect(&owners_after, &authored, 20.0, 1.0, rx0, ry0, rx1, ry1, &mut inc[..cells]).unwrap();
            mipmap_r8_rect(w, h, rx0, ry0, rx1, ry1, &mut inc).unwrap();

            // Full bake with owners_after
            bake.bake_frontier(&owners_after, &authored, 20.0, 1.0, 2, &mut full[..cells]).unwrap();
            mipmap_r8_rect(w, h, 0, 0, w, h, &mut full).unwrap();

            assert_eq!(inc[..cells], full[..cells], "level 0 frontier bytes must match bit-for-bit");
            assert_eq!(inc, full, "entire mipmap pyramid must match bit-for-bit");
        }
    }
}
