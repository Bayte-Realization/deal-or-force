// SPDX-License-Identifier: GPL-3.0-only
use crate::raster_blur::{pack_bytes_only, smooth_and_pack};

pub struct DofRasterBake {
    pub width: usize,
    pub height: usize,
    pub cells: usize,
    authored_holders: Vec<u16>,
    cell_to_province: Vec<u16>,
    prov_lut: Vec<u16>, // 65,536 LUT
    distance: Vec<f32>,
    across: Vec<f32>,
    seam_snap: Vec<f32>,
}

pub(crate) struct SyncUnsafePtr<T>(pub(crate) *mut T);
unsafe impl<T> Send for SyncUnsafePtr<T> {}
unsafe impl<T> Sync for SyncUnsafePtr<T> {}

impl<T> Copy for SyncUnsafePtr<T> {}
impl<T> Clone for SyncUnsafePtr<T> {
    fn clone(&self) -> Self {
        *self
    }
}

#[derive(Copy, Clone)]
pub(crate) struct SyncConstPtr<T>(pub(crate) *const T);
unsafe impl<T> Send for SyncConstPtr<T> {}
unsafe impl<T> Sync for SyncConstPtr<T> {}

impl DofRasterBake {
    pub unsafe fn from_raw(
        width: u32,
        height: u32,
        authored_holders: *const u16,
        cell_to_province: *const u16,
    ) -> Option<Self> {
        let w = width as usize;
        let h = height as usize;
        let cells = w.checked_mul(h)?;
        if cells == 0 || cells > 64_000_000 {
            return None;
        }
        if authored_holders.is_null() {
            return None;
        }

        let holders = std::slice::from_raw_parts(authored_holders, cells).to_vec();
        let provs = if cell_to_province.is_null() {
            Vec::new()
        } else {
            std::slice::from_raw_parts(cell_to_province, cells).to_vec()
        };

        Some(Self {
            width: w,
            height: h,
            cells,
            authored_holders: holders,
            cell_to_province: provs,
            prov_lut: vec![0u16; 65_536],
            distance: vec![0.0f32; cells],
            across: vec![0.0f32; cells],
            seam_snap: Vec::new(),
        })
    }

    pub fn bake_frontier(
        &mut self,
        owners: &[u16],
        authored_owners: &[u16],
        cap: f32,
        smoothing: f32,
        threads: u32,
        out_bytes: &mut [u8],
    ) -> Result<(), i32> {
        if out_bytes.len() < self.cells {
            return Err(-3);
        }
        if !cap.is_finite() || cap <= 0.0 || !smoothing.is_finite() {
            return Err(-2);
        }

        let w = self.width;
        let h = self.height;
        let n_threads = resolve_threads(threads, h);
        let has_prov = !self.cell_to_province.is_empty() && !owners.is_empty();
        let mut has_override = false;

        if has_prov {
            self.prov_lut.fill(0);
            let limit = owners.len().min(65_536);
            for p in 1..limit {
                let owner = owners[p];
                let same_authored =
                    !authored_owners.is_empty() && p < authored_owners.len() && authored_owners[p] == owner;
                if !same_authored {
                    self.prov_lut[p] = if owner == 0 { 65_535 } else { owner };
                    has_override = true;
                }
            }
        }

        // Step 1: Initialize frontier distances (0.5f or cap) reading effective holder directly from authored_holders + prov_lut
        // WITHOUT allocating or writing a separate 33 MB holders[] array!
        init_distances(
            &self.authored_holders,
            &self.cell_to_province,
            &self.prov_lut,
            has_override,
            w,
            h,
            cap,
            &mut self.distance,
            n_threads,
        );

        // Step 2 & 3: Exact 2-pass Chamfer (sequential when small/large-cap, exact halo-seeded parallel otherwise).
        let halo = (cap.ceil() as usize).saturating_add(1);
        let band_rows_min = halo.saturating_mul(2).saturating_add(16);
        let use_parallel_chamfer =
            n_threads > 1 && h >= n_threads.saturating_mul(band_rows_min) && halo <= 128;

        if use_parallel_chamfer {
            parallel_chamfer(
                w,
                h,
                cap,
                halo,
                n_threads,
                &mut self.distance,
                &mut self.seam_snap,
            );
        } else {
            sequential_chamfer(w, h, cap, &mut self.distance);
        }

        // Step 4 & 5: Gaussian smoothing (if > 0) + fused u8 byte conversion.
        if smoothing > 0.0 {
            smooth_and_pack(
                &mut self.distance,
                &mut self.across,
                w,
                h,
                cap,
                smoothing,
                n_threads,
                out_bytes,
            );
        } else {
            pack_bytes_only(&self.distance, self.cells, cap, out_bytes, n_threads);
        }

        Ok(())
    }
}

fn resolve_threads(requested: u32, height: usize) -> usize {
    let base = if requested == 0 {
        std::thread::available_parallelism()
            .map(|p| p.get())
            .unwrap_or(1)
    } else {
        requested as usize
    };
    base.clamp(1, 16).min(height.max(1))
}

#[inline(always)]
fn effective_holder(
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

#[allow(clippy::too_many_arguments)]
fn init_distances(
    authored: &[u16],
    provs: &[u16],
    lut: &[u16],
    has_override: bool,
    w: usize,
    h: usize,
    cap: f32,
    dist: &mut [f32],
    n_threads: usize,
) {
    if !has_override && w >= 4 {
        // Ultra-fast branchless path when no overrides (or pre-resolved map)
        init_distances_direct(authored, w, h, cap, dist, n_threads);
        return;
    }

    let chunk_rows = (h + n_threads - 1) / n_threads;
    let dist_ptr = SyncUnsafePtr(dist.as_mut_ptr());
    std::thread::scope(|s| {
        for t in 0..n_threads {
            let y0 = t * chunk_rows;
            if y0 >= h {
                break;
            }
            let y1 = ((t + 1) * chunk_rows).min(h);
            s.spawn(move || {
                let d_ptr = dist_ptr;
                for y in y0..y1 {
                    let row = y * w;
                    let has_up = y > 0;
                    let has_down = y + 1 < h;
                    for x in 0..w {
                        let cell = row + x;
                        let here = effective_holder(authored, provs, lut, has_override, cell);
                        let east_x = if x + 1 == w { 0 } else { x + 1 };
                        let west_x = if x == 0 { w - 1 } else { x - 1 };
                        let frontier = effective_holder(authored, provs, lut, has_override, row + east_x)
                            != here
                            || effective_holder(authored, provs, lut, has_override, row + west_x)
                                != here
                            || (has_up
                                && effective_holder(authored, provs, lut, has_override, cell - w)
                                    != here)
                            || (has_down
                                && effective_holder(authored, provs, lut, has_override, cell + w)
                                    != here);
                        unsafe {
                            *d_ptr.0.add(cell) = if frontier { 0.5 } else { cap };
                        }
                    }
                }
            });
        }
    });
}

fn init_distances_direct(
    authored: &[u16],
    w: usize,
    h: usize,
    cap: f32,
    dist: &mut [f32],
    n_threads: usize,
) {
    let chunk_rows = (h + n_threads - 1) / n_threads;
    let dist_ptr = SyncUnsafePtr(dist.as_mut_ptr());
    std::thread::scope(|s| {
        for t in 0..n_threads {
            let y0 = t * chunk_rows;
            if y0 >= h {
                break;
            }
            let y1 = ((t + 1) * chunk_rows).min(h);
            s.spawn(move || {
                let d_ptr = dist_ptr;
                for y in y0..y1 {
                    let row = y * w;
                    let r = &authored[row..row + w];
                    let has_up = y > 0;
                    let has_down = y + 1 < h;
                    // Handle x = 0 wrap
                    {
                        let here = r[0];
                        let f = r[1] != here
                            || r[w - 1] != here
                            || (has_up && authored[row - w] != here)
                            || (has_down && authored[row + w] != here);
                        unsafe {
                            *d_ptr.0.add(row) = if f { 0.5 } else { cap };
                        }
                    }
                    // Interior 1..w-1 (bounds-check free!)
                    let up_slice = if has_up {
                        &authored[row - w..row]
                    } else {
                        r
                    };
                    let dn_slice = if has_down {
                        &authored[row + w..row + 2 * w]
                    } else {
                        r
                    };
                    for x in 1..w - 1 {
                        let here = unsafe { *r.get_unchecked(x) };
                        let f = unsafe { *r.get_unchecked(x + 1) } != here
                            || unsafe { *r.get_unchecked(x - 1) } != here
                            || (has_up && unsafe { *up_slice.get_unchecked(x) } != here)
                            || (has_down && unsafe { *dn_slice.get_unchecked(x) } != here);
                        unsafe {
                            *d_ptr.0.add(row + x) = if f { 0.5 } else { cap };
                        }
                    }
                    // Handle x = w - 1 wrap
                    {
                        let x = w - 1;
                        let here = r[x];
                        let f = r[0] != here
                            || r[x - 1] != here
                            || (has_up && authored[row - w + x] != here)
                            || (has_down && authored[row + w + x] != here);
                        unsafe {
                            *d_ptr.0.add(row + x) = if f { 0.5 } else { cap };
                        }
                    }
                }
            });
        }
    });
}

#[inline(always)]
fn chamfer_forward_row(prev_row: Option<&[f32]>, curr_row: &mut [f32], w: usize) {
    const STRAIGHT: f32 = 1.0;
    const DIAGONAL: f32 = 1.41421356;

    if w == 0 {
        return;
    }

    // x = 0 reads pre-update curr_row[w - 1] for west!
    {
        let mut best = curr_row[0];
        if best > 0.5 {
            let west_val = curr_row[w - 1];
            best = best.min(west_val + STRAIGHT);
            if let Some(prev) = prev_row {
                best = best.min(prev[0] + STRAIGHT);
                best = best.min(prev[w - 1] + DIAGONAL);
                if w > 1 {
                    best = best.min(prev[1] + DIAGONAL);
                } else {
                    best = best.min(prev[0] + DIAGONAL);
                }
            }
            curr_row[0] = best;
        }
    }

    if w == 1 {
        return;
    }

    let mut west_best = curr_row[0];
    if let Some(prev) = prev_row {
        for x in 1..w - 1 {
            let mut best = unsafe { *curr_row.get_unchecked(x) };
            if best > 0.5 {
                best = best.min(west_best + STRAIGHT);
                best = best.min(unsafe { *prev.get_unchecked(x) } + STRAIGHT);
                best = best.min(unsafe { *prev.get_unchecked(x - 1) } + DIAGONAL);
                best = best.min(unsafe { *prev.get_unchecked(x + 1) } + DIAGONAL);
                unsafe { *curr_row.get_unchecked_mut(x) = best };
                west_best = best;
            } else {
                west_best = best;
            }
        }
        let x = w - 1;
        let mut best = curr_row[x];
        if best > 0.5 {
            best = best.min(west_best + STRAIGHT);
            best = best.min(prev[x] + STRAIGHT);
            best = best.min(prev[x - 1] + DIAGONAL);
            best = best.min(prev[0] + DIAGONAL);
            curr_row[x] = best;
        }
    } else {
        for x in 1..w {
            let mut best = unsafe { *curr_row.get_unchecked(x) };
            if best > 0.5 {
                best = best.min(west_best + STRAIGHT);
                unsafe { *curr_row.get_unchecked_mut(x) = best };
                west_best = best;
            } else {
                west_best = best;
            }
        }
    }
}

#[inline(always)]
fn chamfer_backward_row(next_row: Option<&[f32]>, curr_row: &mut [f32], w: usize, cap: f32) {
    const STRAIGHT: f32 = 1.0;
    const DIAGONAL: f32 = 1.41421356;

    if w == 0 {
        return;
    }

    // x = w - 1 reads pre-update curr_row[0] for east!
    {
        let x = w - 1;
        let mut best = curr_row[x];
        if best > 0.5 {
            best = best.min(curr_row[0] + STRAIGHT);
            if let Some(next) = next_row {
                best = best.min(next[x] + STRAIGHT);
                let west_x = if x == 0 { w - 1 } else { x - 1 };
                best = best.min(next[west_x] + DIAGONAL);
                best = best.min(next[0] + DIAGONAL);
            }
            curr_row[x] = best.min(cap);
        }
    }

    if w == 1 {
        return;
    }

    let mut east_best = curr_row[w - 1];
    if let Some(next) = next_row {
        for x in (1..w - 1).rev() {
            let mut best = unsafe { *curr_row.get_unchecked(x) };
            if best > 0.5 {
                best = best.min(east_best + STRAIGHT);
                best = best.min(unsafe { *next.get_unchecked(x) } + STRAIGHT);
                best = best.min(unsafe { *next.get_unchecked(x - 1) } + DIAGONAL);
                best = best.min(unsafe { *next.get_unchecked(x + 1) } + DIAGONAL);
                best = best.min(cap);
                unsafe { *curr_row.get_unchecked_mut(x) = best };
                east_best = best;
            } else {
                east_best = best;
            }
        }
        // x = 0
        let mut best = curr_row[0];
        if best > 0.5 {
            best = best.min(east_best + STRAIGHT);
            best = best.min(next[0] + STRAIGHT);
            best = best.min(next[w - 1] + DIAGONAL);
            best = best.min(next[1] + DIAGONAL);
            curr_row[0] = best.min(cap);
        }
    } else {
        for x in (0..w - 1).rev() {
            let mut best = unsafe { *curr_row.get_unchecked(x) };
            if best > 0.5 {
                best = best.min(east_best + STRAIGHT).min(cap);
                unsafe { *curr_row.get_unchecked_mut(x) = best };
                east_best = best;
            } else {
                east_best = best;
            }
        }
    }
}

fn sequential_chamfer(w: usize, h: usize, cap: f32, d: &mut [f32]) {
    for y in 0..h {
        let row = y * w;
        if y == 0 {
            chamfer_forward_row(None, &mut d[row..row + w], w);
        } else {
            let (above, curr_slice) = d.split_at_mut(row);
            let prev = &above[row - w..row];
            let curr = &mut curr_slice[..w];
            chamfer_forward_row(Some(prev), curr, w);
        }
    }
    for y in (0..h).rev() {
        let row = y * w;
        if y + 1 == h {
            chamfer_backward_row(None, &mut d[row..row + w], w, cap);
        } else {
            let (curr_slice, below) = d.split_at_mut(row + w);
            let curr = &mut curr_slice[row..row + w];
            let next = &below[..w];
            chamfer_backward_row(Some(next), curr, w, cap);
        }
    }
}

/// Bit-exact halo-seeded parallel Chamfer.
/// Before running parallel bands in-place, saves ONLY the `halo` overlap rows per band seam
/// so each band executes its halo locally into two `w`-element scratch rows with zero races.
fn parallel_chamfer(
    w: usize,
    h: usize,
    cap: f32,
    halo: usize,
    n_threads: usize,
    d: &mut [f32],
    seam_snap: &mut Vec<f32>,
) {
    let chunk_rows = (h + n_threads - 1) / n_threads;
    let needed_snap = n_threads * halo * w;
    if seam_snap.len() < needed_snap {
        seam_snap.resize(needed_snap, 0.0);
    }

    // --- Forward pass: snapshot [y0 - halo .. y0) for threads > 0
    for t in 1..n_threads {
        let y0 = t * chunk_rows;
        if y0 >= h {
            break;
        }
        let h_start = y0.saturating_sub(halo);
        let h_len = y0 - h_start;
        let dst_off = t * halo * w;
        seam_snap[dst_off..dst_off + h_len * w].copy_from_slice(&d[h_start * w..y0 * w]);
    }

    let d_ptr = SyncUnsafePtr(d.as_mut_ptr());
    let snap_ptr = SyncConstPtr(seam_snap.as_ptr());

    std::thread::scope(|s| {
        for t in 0..n_threads {
            let y0 = t * chunk_rows;
            if y0 >= h {
                break;
            }
            let y1 = ((t + 1) * chunk_rows).min(h);
            s.spawn(move || {
                let dp = d_ptr;
                let sp = snap_ptr;
                if t == 0 {
                    for y in y0..y1 {
                        let row_slice = unsafe { std::slice::from_raw_parts_mut(dp.0.add(y * w), w) };
                        let prev_slice = if y == 0 {
                            None
                        } else {
                            Some(unsafe { std::slice::from_raw_parts(dp.0.add((y - 1) * w), w) })
                        };
                        chamfer_forward_row(prev_slice, row_slice, w);
                    }
                } else {
                    let h_start = y0.saturating_sub(halo);
                    let h_len = y0 - h_start;
                    let mut prev_buf = vec![0.0f32; w];
                    let mut curr_buf = vec![0.0f32; w];
                    let band_snap = unsafe {
                        std::slice::from_raw_parts(sp.0.add(t * halo * w), h_len * w)
                    };
                    for hi in 0..h_len {
                        curr_buf.copy_from_slice(&band_snap[hi * w..(hi + 1) * w]);
                        let prev = if hi == 0 && h_start == 0 {
                            None
                        } else if hi == 0 {
                            // If h_start > 0, row h_start is >= halo+1 away from y0;
                            // feeding cap for unseen row h_start - 1 still guarantees exact values by y0.
                            prev_buf.fill(cap);
                            Some(prev_buf.as_slice())
                        } else {
                            Some(prev_buf.as_slice())
                        };
                        chamfer_forward_row(prev, &mut curr_buf, w);
                        std::mem::swap(&mut prev_buf, &mut curr_buf);
                    }
                    // prev_buf now holds exact row (y0 - 1) after forward pass!
                    for y in y0..y1 {
                        let row_slice = unsafe { std::slice::from_raw_parts_mut(dp.0.add(y * w), w) };
                        let prev_slice = if y == y0 {
                            Some(prev_buf.as_slice())
                        } else {
                            Some(unsafe { std::slice::from_raw_parts(dp.0.add((y - 1) * w), w) })
                        };
                        chamfer_forward_row(prev_slice, row_slice, w);
                    }
                }
            });
        }
    });

    // --- Backward pass: snapshot [y1 .. min(h, y1 + halo)) for every band whose y1 < h
    for t in 0..n_threads {
        let y0 = t * chunk_rows;
        if y0 >= h {
            break;
        }
        let y1 = ((t + 1) * chunk_rows).min(h);
        if y1 < h {
            let h_end = (y1 + halo).min(h);
            let h_len = h_end - y1;
            let dst_off = t * halo * w;
            seam_snap[dst_off..dst_off + h_len * w].copy_from_slice(&d[y1 * w..h_end * w]);
        }
    }

    let snap_ptr_b = SyncConstPtr(seam_snap.as_ptr());
    std::thread::scope(|s| {
        for t in 0..n_threads {
            let y0 = t * chunk_rows;
            if y0 >= h {
                break;
            }
            let y1 = ((t + 1) * chunk_rows).min(h);
            s.spawn(move || {
                let dp = d_ptr;
                let sp = snap_ptr_b;
                if y1 == h {
                    for y in (y0..y1).rev() {
                        let row_slice = unsafe { std::slice::from_raw_parts_mut(dp.0.add(y * w), w) };
                        let next_slice = if y + 1 == h {
                            None
                        } else {
                            Some(unsafe { std::slice::from_raw_parts(dp.0.add((y + 1) * w), w) })
                        };
                        chamfer_backward_row(next_slice, row_slice, w, cap);
                    }
                } else {
                    let h_end = (y1 + halo).min(h);
                    let h_len = h_end - y1;
                    let mut next_buf = vec![0.0f32; w];
                    let mut curr_buf = vec![0.0f32; w];
                    let band_snap = unsafe {
                        std::slice::from_raw_parts(sp.0.add(t * halo * w), h_len * w)
                    };
                    for hi in (0..h_len).rev() {
                        curr_buf.copy_from_slice(&band_snap[hi * w..(hi + 1) * w]);
                        let next = if hi + 1 == h_len && h_end == h {
                            None
                        } else if hi + 1 == h_len {
                            next_buf.fill(cap);
                            Some(next_buf.as_slice())
                        } else {
                            Some(next_buf.as_slice())
                        };
                        chamfer_backward_row(next, &mut curr_buf, w, cap);
                        std::mem::swap(&mut next_buf, &mut curr_buf);
                    }
                    // next_buf now holds exact row y1 after backward pass!
                    for y in (y0..y1).rev() {
                        let row_slice = unsafe { std::slice::from_raw_parts_mut(dp.0.add(y * w), w) };
                        let next_slice = if y + 1 == y1 {
                            Some(next_buf.as_slice())
                        } else {
                            Some(unsafe { std::slice::from_raw_parts(dp.0.add((y + 1) * w), w) })
                        };
                        chamfer_backward_row(next_slice, row_slice, w, cap);
                    }
                }
            });
        }
    });
}

