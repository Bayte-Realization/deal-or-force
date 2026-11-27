// SPDX-License-Identifier: GPL-3.0-only
use crate::raster_blur::{pack_byte, pack_bytes_only, smooth_and_pack};

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
    rect_dist: Vec<f32>,
    rect_across: Vec<f32>,
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
            rect_dist: Vec::new(),
            rect_across: Vec::new(),
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
        let has_prov = !self.cell_to_province.is_empty() && !owners.is_empty() && !authored_owners.is_empty();
        let mut has_override = false;

        if has_prov {
            self.prov_lut.fill(0);
            let limit = owners.len().min(authored_owners.len()).min(65_536);
            for p in 1..limit {
                let owner = owners[p];
                if authored_owners[p] != owner {
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

    /// Incremental dirty-rect frontier field update, guaranteed 100% bit-exact with `bake_frontier`.
    /// Updates ONLY pixels inside `[x0..x1) x [y0..y1)` of `out_bytes` by computing Chamfer + Gaussian
    /// over a mathematically exact dependency halo `(ceil(cap) + 1 + ceil(3 * smoothing))`.
    /// Falls back automatically to full `bake_frontier` if the padded halo crosses `x = 0` or `x = w`
    /// (preserving exact Chamfer wrap-around order) or if the dirty area exceeds half the planet.
    #[allow(clippy::too_many_arguments)]
    pub fn bake_frontier_rect(
        &mut self,
        owners: &[u16],
        authored_owners: &[u16],
        cap: f32,
        smoothing: f32,
        rect_x0: usize,
        rect_y0: usize,
        rect_x1: usize,
        rect_y1: usize,
        out_bytes: &mut [u8],
    ) -> Result<(), i32> {
        if out_bytes.len() < self.cells {
            return Err(-3);
        }
        if !cap.is_finite() || cap <= 0.0 || !smoothing.is_finite() || smoothing < 0.0 {
            return Err(-2);
        }

        let w = self.width;
        let h = self.height;
        let x0 = rect_x0.min(w);
        let x1 = rect_x1.min(w);
        let y0 = rect_y0.min(h);
        let y1 = rect_y1.min(h);
        if x0 >= x1 || y0 >= y1 {
            return Ok(());
        }

        let r_blur = if smoothing > 0.0 {
            (smoothing * 3.0).ceil() as usize
        } else {
            0
        };
        let r_chamfer = (cap.ceil() as usize).saturating_add(1);
        let halo = r_chamfer.saturating_add(r_blur);

        let rw = x1 - x0;
        let rh = y1 - y0;
        if x0 < halo || x1 + halo > w || rw.saturating_mul(rh).saturating_mul(2) >= self.cells {
            return self.bake_frontier(owners, authored_owners, cap, smoothing, 0, out_bytes);
        }

        let has_prov = !self.cell_to_province.is_empty() && !owners.is_empty() && !authored_owners.is_empty();
        let mut has_override = false;
        if has_prov {
            self.prov_lut.fill(0);
            let limit = owners.len().min(authored_owners.len()).min(65_536);
            for p in 1..limit {
                let owner = owners[p];
                if authored_owners[p] != owner {
                    self.prov_lut[p] = if owner == 0 { 65_535 } else { owner };
                    has_override = true;
                }
            }
        }

        let wx0 = x0 - halo;
        let wx1 = x1 + halo;
        let lw = wx1 - wx0;
        let wy0 = y0.saturating_sub(halo);
        let wy1 = (y1 + halo).min(h);
        let lh = wy1 - wy0;
        let local_cells = lw * lh;
        if self.rect_dist.len() < local_cells {
            self.rect_dist.resize(local_cells, 0.0);
        }
        if smoothing > 0.0 && self.rect_across.len() < local_cells {
            self.rect_across.resize(local_cells, 0.0);
        }

        // Step 1: Init exact frontier distances inside [wx0..wx1) x [wy0..wy1).
        let authored = &self.authored_holders;
        let provs = &self.cell_to_province;
        let lut = &self.prov_lut;
        let rdist = &mut self.rect_dist[..local_cells];

        for ly in 0..lh {
            let gy = wy0 + ly;
            let row = gy * w;
            let lrow = ly * lw;
            let has_up = gy > 0;
            let has_down = gy + 1 < h;
            for lx in 0..lw {
                let gx = wx0 + lx;
                let cell = row + gx;
                let here = effective_holder(authored, provs, lut, has_override, cell);
                let east_x = if gx + 1 == w { 0 } else { gx + 1 };
                let west_x = if gx == 0 { w - 1 } else { gx - 1 };
                let frontier = effective_holder(authored, provs, lut, has_override, row + east_x) != here
                    || effective_holder(authored, provs, lut, has_override, row + west_x) != here
                    || (has_up && effective_holder(authored, provs, lut, has_override, cell - w) != here)
                    || (has_down && effective_holder(authored, provs, lut, has_override, cell + w) != here);
                unsafe {
                    *rdist.get_unchecked_mut(lrow + lx) = if frontier { 0.5 } else { cap };
                }
            }
        }

        // Step 2: Forward Chamfer (exact same arithmetic order as full-planet interior).
        const STRAIGHT: f32 = 1.0;
        const DIAGONAL: f32 = 1.41421356;
        for ly in 0..lh {
            let lrow = ly * lw;
            let prev_row = if ly > 0 { (ly - 1) * lw } else { 0 };
            let mut west_best = unsafe { *rdist.get_unchecked(lrow) };
            if ly > 0 && west_best > 0.5 {
                let mut best = west_best;
                best = best.min(unsafe { *rdist.get_unchecked(prev_row) } + STRAIGHT);
                if lw > 1 {
                    best = best.min(unsafe { *rdist.get_unchecked(prev_row + 1) } + DIAGONAL);
                }
                unsafe { *rdist.get_unchecked_mut(lrow) = best };
                west_best = best;
            }
            if ly > 0 {
                for lx in 1..lw - 1 {
                    let mut best = unsafe { *rdist.get_unchecked(lrow + lx) };
                    if best > 0.5 {
                        best = best.min(west_best + STRAIGHT);
                        best = best.min(unsafe { *rdist.get_unchecked(prev_row + lx) } + STRAIGHT);
                        best = best.min(unsafe { *rdist.get_unchecked(prev_row + lx - 1) } + DIAGONAL);
                        best = best.min(unsafe { *rdist.get_unchecked(prev_row + lx + 1) } + DIAGONAL);
                        unsafe { *rdist.get_unchecked_mut(lrow + lx) = best };
                        west_best = best;
                    } else {
                        west_best = best;
                    }
                }
                if lw > 1 {
                    let lx = lw - 1;
                    let mut best = unsafe { *rdist.get_unchecked(lrow + lx) };
                    if best > 0.5 {
                        best = best.min(west_best + STRAIGHT);
                        best = best.min(unsafe { *rdist.get_unchecked(prev_row + lx) } + STRAIGHT);
                        best = best.min(unsafe { *rdist.get_unchecked(prev_row + lx - 1) } + DIAGONAL);
                        unsafe { *rdist.get_unchecked_mut(lrow + lx) = best };
                    }
                }
            } else {
                for lx in 1..lw {
                    let mut best = unsafe { *rdist.get_unchecked(lrow + lx) };
                    if best > 0.5 {
                        best = best.min(west_best + STRAIGHT);
                        unsafe { *rdist.get_unchecked_mut(lrow + lx) = best };
                        west_best = best;
                    } else {
                        west_best = best;
                    }
                }
            }
        }

        // Step 3: Backward Chamfer (exact same arithmetic order as full-planet interior).
        for ly in (0..lh).rev() {
            let lrow = ly * lw;
            let next_row = if ly + 1 < lh { (ly + 1) * lw } else { 0 };
            let last_x = lw - 1;
            let mut east_best = unsafe { *rdist.get_unchecked(lrow + last_x) };
            if east_best > 0.5 {
                let mut best = east_best;
                if ly + 1 < lh {
                    best = best.min(unsafe { *rdist.get_unchecked(next_row + last_x) } + STRAIGHT);
                    if lw > 1 {
                        best = best.min(unsafe { *rdist.get_unchecked(next_row + last_x - 1) } + DIAGONAL);
                    }
                }
                best = best.min(cap);
                unsafe { *rdist.get_unchecked_mut(lrow + last_x) = best };
                east_best = best;
            }
            if ly + 1 < lh {
                if lw > 2 {
                    for lx in (1..lw - 1).rev() {
                        let mut best = unsafe { *rdist.get_unchecked(lrow + lx) };
                        if best > 0.5 {
                            best = best.min(east_best + STRAIGHT);
                            best = best.min(unsafe { *rdist.get_unchecked(next_row + lx) } + STRAIGHT);
                            best = best.min(unsafe { *rdist.get_unchecked(next_row + lx - 1) } + DIAGONAL);
                            best = best.min(unsafe { *rdist.get_unchecked(next_row + lx + 1) } + DIAGONAL);
                            best = best.min(cap);
                            unsafe { *rdist.get_unchecked_mut(lrow + lx) = best };
                            east_best = best;
                        } else {
                            east_best = best;
                        }
                    }
                }
                if lw > 1 {
                    let mut best = unsafe { *rdist.get_unchecked(lrow) };
                    if best > 0.5 {
                        best = best.min(east_best + STRAIGHT);
                        best = best.min(unsafe { *rdist.get_unchecked(next_row) } + STRAIGHT);
                        best = best.min(unsafe { *rdist.get_unchecked(next_row + 1) } + DIAGONAL);
                        best = best.min(cap);
                        unsafe { *rdist.get_unchecked_mut(lrow) = best };
                    }
                }
            } else {
                for lx in (0..lw - 1).rev() {
                    let mut best = unsafe { *rdist.get_unchecked(lrow + lx) };
                    if best > 0.5 {
                        best = best.min(east_best + STRAIGHT).min(cap);
                        unsafe { *rdist.get_unchecked_mut(lrow + lx) = best };
                        east_best = best;
                    } else {
                        east_best = best;
                    }
                }
            }
        }

        // Step 4 & 5: Gaussian smoothing (if > 0) + fused u8 byte pack ONLY into [x0..x1) x [y0..y1).
        let scale = 255.0f32 / cap;
        if smoothing > 0.0 {
            let radius = r_blur as isize;
            let klen = (radius * 2 + 1) as usize;
            let mut weights = vec![0.0f32; klen];
            let mut total = 0.0f32;
            let inv_two_var = -1.0f32 / (2.0f32 * smoothing * smoothing);
            for (idx, i) in (-radius..=radius).enumerate() {
                let fi = (i * i) as f32;
                let wv = (fi * inv_two_var).exp();
                weights[idx] = wv;
                total += wv;
            }
            for wv in &mut weights {
                *wv /= total;
            }

            let sy0 = y0.saturating_sub(r_blur);
            let sy1 = (y1 + r_blur).min(h);
            let racross = &mut self.rect_across[..local_cells];
            // Horizontal pass on [sy0..sy1) x [x0..x1)
            for gy in sy0..sy1 {
                let ly = gy - wy0;
                let lrow = ly * lw;
                for gx in x0..x1 {
                    let lx = gx - wx0;
                    let mut sum = 0.0f32;
                    for ki in 0..klen {
                        let col = lx + ki - r_blur;
                        sum += unsafe { *rdist.get_unchecked(lrow + col) * *weights.get_unchecked(ki) };
                    }
                    unsafe { *racross.get_unchecked_mut(lrow + lx) = sum };
                }
            }
            // Vertical pass + byte pack on [y0..y1) x [x0..x1)
            let mut line_offs = vec![0usize; klen];
            for gy in y0..y1 {
                for (ki, k) in (-radius..=radius).enumerate() {
                    let line = (gy as isize + k).clamp(0, (h as isize) - 1) as usize;
                    line_offs[ki] = (line - wy0) * lw;
                }
                let out_row = gy * w;
                for gx in x0..x1 {
                    let lx = gx - wx0;
                    let mut sum = 0.0f32;
                    for ki in 0..klen {
                        sum += unsafe { *racross.get_unchecked(line_offs[ki] + lx) * *weights.get_unchecked(ki) };
                    }
                    unsafe { *out_bytes.get_unchecked_mut(out_row + gx) = pack_byte(sum * scale) };
                }
            }
        } else {
            for gy in y0..y1 {
                let ly = gy - wy0;
                let lrow = ly * lw;
                let out_row = gy * w;
                for gx in x0..x1 {
                    let lx = gx - wx0;
                    let v = unsafe { *rdist.get_unchecked(lrow + lx) } * scale;
                    unsafe { *out_bytes.get_unchecked_mut(out_row + gx) = pack_byte(v) };
                }
            }
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

#[inline(always)]
fn lookup_palette_rgba(palette_rgba: &[u32], map_index: u16, fallback_rgba: u32) -> u32 {
    let idx = map_index as usize;
    if idx < palette_rgba.len() {
        let packed = palette_rgba[idx];
        if packed != 0 {
            return packed;
        }
    }
    fallback_rgba
}

#[allow(clippy::too_many_arguments)]
pub fn pack_province_overlays(
    province_count: usize,
    owners: &[u16],
    painted_owners: &[u16],
    controllers: &[u16],
    palette_rgba: &[u32],
    occupied_alpha: u8,
    unowned_palette_index: u16,
    fallback_rgba: u32,
    out_control_rgba: &mut [u8],
    out_owner_rgba: &mut [u8],
    out_owner_ids_rgba: &mut [u8],
) -> Result<bool, i32> {
    const OVERLAY_BUFFER_SIZE: usize = 256 * 256 * 4;
    let ctl_clear = out_control_rgba.len().min(OVERLAY_BUFFER_SIZE);
    let own_clear = out_owner_rgba.len().min(OVERLAY_BUFFER_SIZE);
    let oid_clear = out_owner_ids_rgba.len().min(OVERLAY_BUFFER_SIZE);
    out_control_rgba[..ctl_clear].fill(0);
    out_owner_rgba[..own_clear].fill(0);
    out_owner_ids_rgba[..oid_clear].fill(0);

    let max_slot = out_control_rgba
        .len()
        .min(out_owner_rgba.len())
        .min(out_owner_ids_rgba.len())
        / 4;
    let count = province_count
        .min(owners.len().saturating_sub(1))
        .min(controllers.len().saturating_sub(1))
        .min(max_slot.saturating_sub(1));

    if count == 0 {
        return Ok(false);
    }

    let mut owners_moved = false;
    let has_painted = !painted_owners.is_empty();
    for province in 1..=count {
        let owner = owners[province];
        let write = province * 4;
        if has_painted && province < painted_owners.len() && painted_owners[province] != owner {
            let owner_packed = lookup_palette_rgba(palette_rgba, owner, fallback_rgba);
            out_owner_rgba[write] = ((owner_packed >> 24) & 0xFF) as u8;
            out_owner_rgba[write + 1] = ((owner_packed >> 16) & 0xFF) as u8;
            out_owner_rgba[write + 2] = ((owner_packed >> 8) & 0xFF) as u8;
            out_owner_rgba[write + 3] = 255;

            let drawn_owner = if owner == 0 { unowned_palette_index } else { owner };
            out_owner_ids_rgba[write] = (drawn_owner & 0xFF) as u8;
            out_owner_ids_rgba[write + 1] = ((drawn_owner >> 8) & 0xFF) as u8;
            out_owner_ids_rgba[write + 2] = 255;
            out_owner_ids_rgba[write + 3] = 255;
            owners_moved = true;
        }

        let controller = controllers[province];
        if controller == owner {
            continue;
        }
        let packed = lookup_palette_rgba(palette_rgba, controller, fallback_rgba);
        out_control_rgba[write] = ((packed >> 24) & 0xFF) as u8;
        out_control_rgba[write + 1] = ((packed >> 16) & 0xFF) as u8;
        out_control_rgba[write + 2] = ((packed >> 8) & 0xFF) as u8;
        out_control_rgba[write + 3] = occupied_alpha;
    }

    Ok(owners_moved)
}

#[inline(always)]
#[allow(clippy::too_many_arguments)]
fn paint_plate_row_range(
    y0: usize,
    y1: usize,
    band_y0: usize,
    w: usize,
    h: usize,
    source_holders: &[u16],
    source_provinces: &[u16],
    source_geography: &[u8],
    prov_lut: &[u16],
    has_override: bool,
    palette_rgba: &[u32],
    country_shade: f32,
    region_shade: f32,
    inland_shade: f32,
    out_pixels_rgba: &mut [u8],
) {
    let pal_len = palette_rgba.len();
    for y in y0..y1 {
        let row = y * w;
        let out_row = (y - band_y0) * w;
        let has_up = y > 0;
        let has_down = y + 1 < h;
        for x in 0..w {
            let cell = row + x;
            let write = (out_row + x) * 4;
            let holder = effective_holder(source_holders, source_provinces, prov_lut, has_override, cell);
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
                let east_x = if x + 1 == w { 0 } else { x + 1 };
                let west_x = if x == 0 { w - 1 } else { x - 1 };
                let east = effective_holder(source_holders, source_provinces, prov_lut, has_override, row + east_x);
                let west = effective_holder(source_holders, source_provinces, prov_lut, has_override, row + west_x);
                let is_foreign_ew = (east != 0 && east != holder) || (west != 0 && west != holder);
                let is_foreign_ns = (has_up && {
                    let north = effective_holder(source_holders, source_provinces, prov_lut, has_override, cell - w);
                    north != 0 && north != holder
                }) || (has_down && {
                    let south = effective_holder(source_holders, source_provinces, prov_lut, has_override, cell + w);
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
}

#[allow(clippy::too_many_arguments)]
pub fn paint_political_plate(
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
    threads: u32,
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

    let mut prov_lut = vec![0u16; 65_536];
    let mut has_override = false;
    if !owners.is_empty() && !authored_owners.is_empty() {
        let limit = owners.len().min(authored_owners.len()).min(65_536);
        for p in 1..limit {
            if authored_owners[p] != owners[p] {
                prov_lut[p] = if owners[p] == 0 { 65_535 } else { owners[p] };
                has_override = true;
            }
        }
    }

    let n_threads = if cells < 32_768 || threads == 1 {
        1
    } else {
        resolve_threads(threads, height)
    };

    if n_threads <= 1 {
        paint_plate_row_range(
            0,
            height,
            0,
            width,
            height,
            source_holders,
            source_provinces,
            source_geography,
            &prov_lut,
            has_override,
            palette_rgba,
            country_shade,
            region_shade,
            inland_shade,
            &mut out_pixels_rgba[..needed_bytes],
        );
        return Ok(());
    }

    let chunk_rows = (height + n_threads - 1) / n_threads;
    let chunk_bytes = chunk_rows * width * 4;
    let lut_ref: &[u16] = &prov_lut;
    std::thread::scope(|s| {
        for (t, band) in out_pixels_rgba[..needed_bytes].chunks_mut(chunk_bytes).enumerate() {
            let y0 = t * chunk_rows;
            let y1 = ((t + 1) * chunk_rows).min(height);
            s.spawn(move || {
                paint_plate_row_range(
                    y0,
                    y1,
                    y0,
                    width,
                    height,
                    source_holders,
                    source_provinces,
                    source_geography,
                    lut_ref,
                    has_override,
                    palette_rgba,
                    country_shade,
                    region_shade,
                    inland_shade,
                    band,
                );
            });
        }
    });

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pack_province_overlays_handles_moves_null_painted_and_fallback() {
        let owners = [0u16, 2, 0, 5, 3];
        let painted = [0u16, 1, 1, 5, 3];
        let controllers = [0u16, 2, 4, 99, 3];
        let mut palette = vec![0u32; 16];
        palette[2] = 0x11223344;
        palette[4] = 0xAABBCCDD;
        palette[5] = 0x55667788;
        let fallback = 0xFEDCBA98u32;

        let mut ctl = vec![0x77u8; 64];
        let mut own = vec![0x77u8; 64];
        let mut oid = vec![0x77u8; 64];

        let moved = pack_province_overlays(
            4,
            &owners,
            &painted,
            &controllers,
            &palette,
            180,
            65535,
            fallback,
            &mut ctl,
            &mut own,
            &mut oid,
        )
        .unwrap();
        assert!(moved);
        // Province 1: moved to owner 2 (alpha 255), controller == owner (cleared)
        assert_eq!(&own[4..8], &[0x11, 0x22, 0x33, 255]);
        assert_eq!(&oid[4..8], &[2, 0, 255, 255]);
        assert_eq!(&ctl[4..8], &[0, 0, 0, 0]);
        // Province 2: moved to owner 0 -> fallback RGBA + unowned_palette_index 65535; controller 4 != 0
        assert_eq!(&own[8..12], &[0xFE, 0xDC, 0xBA, 255]);
        assert_eq!(&oid[8..12], &[0xFF, 0xFF, 255, 255]);
        assert_eq!(&ctl[8..12], &[0xAA, 0xBB, 0xCC, 180]);
        // Province 3: unchanged owner, controller 99 (oob) != 5 -> fallback
        assert_eq!(&own[12..16], &[0, 0, 0, 0]);
        assert_eq!(&ctl[12..16], &[0xFE, 0xDC, 0xBA, 180]);

        // Empty painted_owners -> no owner move, control diff still packed
        let moved2 = pack_province_overlays(
            4,
            &owners,
            &[],
            &controllers,
            &palette,
            180,
            65535,
            fallback,
            &mut ctl,
            &mut own,
            &mut oid,
        )
        .unwrap();
        assert!(!moved2);
        assert_eq!(&ctl[8..12], &[0xAA, 0xBB, 0xCC, 180]);
        assert_eq!(&own[8..12], &[0, 0, 0, 0]);

        // Count 0 clears and returns Ok(false)
        let moved0 = pack_province_overlays(
            0,
            &owners,
            &painted,
            &controllers,
            &palette,
            180,
            65535,
            fallback,
            &mut ctl,
            &mut own,
            &mut oid,
        )
        .unwrap();
        assert!(!moved0);
        assert!(ctl.iter().all(|&b| b == 0));
    }

    #[test]
    fn paint_political_plate_edge_cases_and_mt_exactness() {
        // Test width = 1 + sea + region + unowned 65535
        let holders1 = [0u16, 2, 2];
        let provs1 = [0u16, 1, 2];
        let geo1 = [0u8, 0, 2];
        let owners1 = [0u16, 0, 2];
        let auth1 = [0u16, 2, 2];
        let mut pal = vec![0x6E7B8BFFu32; 65_536];
        pal[2] = 0x4080C0FF;
        let mut px1 = vec![0u8; 12];
        paint_political_plate(
            1,
            3,
            &holders1,
            &provs1,
            &geo1,
            &owners1,
            &auth1,
            &pal,
            0.34,
            0.70,
            0.92,
            1,
            &mut px1,
        )
        .unwrap();
        // Row 0 = Sea -> transparent
        assert_eq!(&px1[0..4], &[0, 0, 0, 0]);
        // Row 1 = unowned 65535 adjacent south to holder 2 -> country shade (0.34)
        assert_eq!(px1[7], 255);
        assert_eq!(px1[4], (0x6Eu8 as f32 * 0.34) as u8);
        // Row 2 = holder 2 adjacent north to 65535 -> country shade (0.34)
        assert_eq!(px1[8], (0x40u8 as f32 * 0.34) as u8);

        // Large plate comparing single-thread vs multi-thread bit-exactness + east/west wrap-around
        let (w, h) = (256usize, 192usize);
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
                } else if x >= w - 2 {
                    holders[c] = 3;
                    provs[c] = 3;
                } else {
                    provs[c] = ((c % 5) + 1) as u16;
                    geo[c] = if (c % 17) == 0 { 1 } else if (c % 7) == 0 { 2 } else { 0 };
                }
            }
        }
        let owners = [0u16, 1, 4, 3]; // shorter than 6
        let authored = [0u16, 1, 2, 3, 2, 1];
        pal[1] = 0xC83232FF;
        pal[3] = 0x32C864FF;
        pal[4] = 0x3264DCFF;

        let mut st = vec![0u8; cells * 4];
        let mut mt = vec![0u8; cells * 4];
        paint_political_plate(
            w, h, &holders, &provs, &geo, &owners, &authored, &pal, 0.34, 0.70, 0.92, 1, &mut st,
        )
        .unwrap();
        paint_political_plate(
            w, h, &holders, &provs, &geo, &owners, &authored, &pal, 0.34, 0.70, 0.92, 4, &mut mt,
        )
        .unwrap();
        assert_eq!(st, mt);
        // Check wrap-around border at x = 0 touching x = w - 1 (holder 3 != 1) gets country_shade!
        let cell_wrap = 10 * w;
        assert_eq!(st[cell_wrap * 4], (0xC8u8 as f32 * 0.34) as u8);
    }
}


