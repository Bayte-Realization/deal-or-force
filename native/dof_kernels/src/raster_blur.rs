// SPDX-License-Identifier: GPL-3.0-only
use crate::raster::{SyncConstPtr, SyncUnsafePtr};

#[allow(clippy::too_many_arguments)]
pub(crate) fn smooth_and_pack(
    field: &mut [f32],
    across: &mut [f32],
    w: usize,
    h: usize,
    cap: f32,
    sigma: f32,
    n_threads: usize,
    out_bytes: &mut [u8],
) {
    let radius = (sigma * 3.0).ceil() as isize;
    let klen = (radius * 2 + 1) as usize;
    let mut weights = vec![0.0f32; klen];
    let mut total = 0.0f32;
    let inv_two_var = -1.0f32 / (2.0f32 * sigma * sigma);
    for (idx, i) in (-radius..=radius).enumerate() {
        let fi = (i * i) as f32;
        let wv = (fi * inv_two_var).exp();
        weights[idx] = wv;
        total += wv;
    }
    for wv in &mut weights {
        *wv /= total;
    }

    let r_usize = radius as usize;
    let chunk_rows = (h + n_threads - 1) / n_threads;

    let f_ptr = SyncConstPtr(field.as_ptr());
    let a_ptr = SyncUnsafePtr(across.as_mut_ptr());
    let weights_ref = &weights;

    std::thread::scope(|s| {
        for t in 0..n_threads {
            let y0 = t * chunk_rows;
            if y0 >= h {
                break;
            }
            let y1 = ((t + 1) * chunk_rows).min(h);
            s.spawn(move || {
                let fp = f_ptr;
                let ap = a_ptr;
                let klen = weights_ref.len();
                for y in y0..y1 {
                    let row = y * w;
                    let src = unsafe { std::slice::from_raw_parts(fp.0.add(row), w) };
                    let dst = unsafe { std::slice::from_raw_parts_mut(ap.0.add(row), w) };

                    if w <= r_usize * 2 + 8 {
                        for x in 0..w {
                            let mut sum = 0.0f32;
                            for (ki, k) in (-radius..=radius).enumerate() {
                                let col = (x as isize + k).rem_euclid(w as isize) as usize;
                                sum += src[col] * weights_ref[ki];
                            }
                            dst[x] = sum;
                        }
                        continue;
                    }

                    for x in 0..r_usize {
                        let mut sum = 0.0f32;
                        for (ki, k) in (-radius..=radius).enumerate() {
                            let mut col = x as isize + k;
                            if col < 0 {
                                col += w as isize;
                            }
                            sum += src[col as usize] * weights_ref[ki];
                        }
                        dst[x] = sum;
                    }

                    let end_mid = w - r_usize;
                    let mut x = r_usize;
                    while x + 8 <= end_mid {
                        let mut s0 = 0.0f32;
                        let mut s1 = 0.0f32;
                        let mut s2 = 0.0f32;
                        let mut s3 = 0.0f32;
                        let mut s4 = 0.0f32;
                        let mut s5 = 0.0f32;
                        let mut s6 = 0.0f32;
                        let mut s7 = 0.0f32;
                        for ki in 0..klen {
                            let off = x + ki - r_usize;
                            let wt = unsafe { *weights_ref.get_unchecked(ki) };
                            unsafe {
                                s0 += *src.get_unchecked(off) * wt;
                                s1 += *src.get_unchecked(off + 1) * wt;
                                s2 += *src.get_unchecked(off + 2) * wt;
                                s3 += *src.get_unchecked(off + 3) * wt;
                                s4 += *src.get_unchecked(off + 4) * wt;
                                s5 += *src.get_unchecked(off + 5) * wt;
                                s6 += *src.get_unchecked(off + 6) * wt;
                                s7 += *src.get_unchecked(off + 7) * wt;
                            }
                        }
                        unsafe {
                            *dst.get_unchecked_mut(x) = s0;
                            *dst.get_unchecked_mut(x + 1) = s1;
                            *dst.get_unchecked_mut(x + 2) = s2;
                            *dst.get_unchecked_mut(x + 3) = s3;
                            *dst.get_unchecked_mut(x + 4) = s4;
                            *dst.get_unchecked_mut(x + 5) = s5;
                            *dst.get_unchecked_mut(x + 6) = s6;
                            *dst.get_unchecked_mut(x + 7) = s7;
                        }
                        x += 8;
                    }
                    while x < end_mid {
                        let mut sum = 0.0f32;
                        for ki in 0..klen {
                            sum += src[x + ki - r_usize] * weights_ref[ki];
                        }
                        dst[x] = sum;
                        x += 1;
                    }

                    for xr in end_mid..w {
                        let mut sum = 0.0f32;
                        for (ki, k) in (-radius..=radius).enumerate() {
                            let mut col = xr as isize + k;
                            if col >= w as isize {
                                col -= w as isize;
                            }
                            sum += src[col as usize] * weights_ref[ki];
                        }
                        dst[xr] = sum;
                    }
                }
            });
        }
    });

    let scale = 255.0f32 / cap;
    let ac_ptr = SyncConstPtr(across.as_ptr());
    let ob_ptr = SyncUnsafePtr(out_bytes.as_mut_ptr());

    std::thread::scope(|s| {
        for t in 0..n_threads {
            let y0 = t * chunk_rows;
            if y0 >= h {
                break;
            }
            let y1 = ((t + 1) * chunk_rows).min(h);
            s.spawn(move || {
                let ap = ac_ptr;
                let op = ob_ptr;
                let klen = weights_ref.len();
                let mut row_ptrs = vec![std::ptr::null::<f32>(); klen];

                for y in y0..y1 {
                    for (ki, k) in (-radius..=radius).enumerate() {
                        let line = (y as isize + k).clamp(0, (h as isize) - 1) as usize;
                        row_ptrs[ki] = unsafe { ap.0.add(line * w) };
                    }
                    let dst = unsafe { std::slice::from_raw_parts_mut(op.0.add(y * w), w) };

                    let mut x = 0usize;
                    while x + 8 <= w {
                        let mut s0 = 0.0f32;
                        let mut s1 = 0.0f32;
                        let mut s2 = 0.0f32;
                        let mut s3 = 0.0f32;
                        let mut s4 = 0.0f32;
                        let mut s5 = 0.0f32;
                        let mut s6 = 0.0f32;
                        let mut s7 = 0.0f32;
                        for ki in 0..klen {
                            let rp = row_ptrs[ki];
                            let wt = unsafe { *weights_ref.get_unchecked(ki) };
                            unsafe {
                                s0 += *rp.add(x) * wt;
                                s1 += *rp.add(x + 1) * wt;
                                s2 += *rp.add(x + 2) * wt;
                                s3 += *rp.add(x + 3) * wt;
                                s4 += *rp.add(x + 4) * wt;
                                s5 += *rp.add(x + 5) * wt;
                                s6 += *rp.add(x + 6) * wt;
                                s7 += *rp.add(x + 7) * wt;
                            }
                        }
                        dst[x] = pack_byte(s0 * scale);
                        dst[x + 1] = pack_byte(s1 * scale);
                        dst[x + 2] = pack_byte(s2 * scale);
                        dst[x + 3] = pack_byte(s3 * scale);
                        dst[x + 4] = pack_byte(s4 * scale);
                        dst[x + 5] = pack_byte(s5 * scale);
                        dst[x + 6] = pack_byte(s6 * scale);
                        dst[x + 7] = pack_byte(s7 * scale);
                        x += 8;
                    }
                    while x < w {
                        let mut sum = 0.0f32;
                        for ki in 0..klen {
                            sum += unsafe { *row_ptrs[ki].add(x) } * weights_ref[ki];
                        }
                        dst[x] = pack_byte(sum * scale);
                        x += 1;
                    }
                }
            });
        }
    });
}

pub(crate) fn pack_bytes_only(dist: &[f32], cells: usize, cap: f32, out_bytes: &mut [u8], n_threads: usize) {
    let scale = 255.0f32 / cap;
    let chunk = (cells + n_threads - 1) / n_threads;
    let d_ptr = SyncConstPtr(dist.as_ptr());
    let o_ptr = SyncUnsafePtr(out_bytes.as_mut_ptr());
    std::thread::scope(|s| {
        for t in 0..n_threads {
            let a = t * chunk;
            if a >= cells {
                break;
            }
            let b = ((t + 1) * chunk).min(cells);
            s.spawn(move || {
                let dp = d_ptr;
                let op = o_ptr;
                for i in a..b {
                    let v = unsafe { *dp.0.add(i) } * scale;
                    unsafe { *op.0.add(i) = pack_byte(v) };
                }
            });
        }
    });
}

#[inline(always)]
fn pack_byte(value: f32) -> u8 {
    if value <= 0.0 {
        0
    } else if value >= 255.0 {
        255
    } else {
        (value + 0.5) as i32 as u8
    }
}
