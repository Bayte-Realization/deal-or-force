// SPDX-License-Identifier: GPL-3.0-only
//! LanguageBench kernels in Rust, behind a C ABI (see ../../README.md, "Native ABI").
//! Line-by-line ports of Scripts/Simulation/FrontierField.cs (sequential) and of
//! BudgetCore.Evaluate over flat arrays; plus the frontier with std threads, labelled.
//! Safe indexing throughout, as the C# has bounds checks too. No retained pointers.

use std::slice;

const CAP: f32 = 8.0;
const SEA: u16 = 0;
const UNOWNED: u16 = 65535;

#[inline(always)]
fn minf(a: f32, b: f32) -> f32 {
    if a < b { a } else { b }
}

#[inline(always)]
fn max0(x: f32) -> f32 {
    // MathF.Max(0f, x): +0 for x <= 0 (including -0), NaN stays NaN.
    if x > 0.0 { x } else if x != x { x } else { 0.0 }
}

#[no_mangle]
pub extern "C" fn lb_abi_version() -> u32 {
    1
}

/// Sixteen pointer-sized arguments and nothing else: the cost of the crossing itself.
#[no_mangle]
pub extern "C" fn lb_noop16(
    _a0: usize, _a1: usize, _a2: usize, _a3: usize, _a4: usize, _a5: usize, _a6: usize, _a7: usize,
    _a8: usize, _a9: usize, _a10: usize, _a11: usize, _a12: usize, _a13: usize, _a14: usize, _a15: usize,
) -> i32 {
    0
}

#[inline(always)]
fn current_holder(authored: u16, province: i32, owners: &[u16], authored_owners: &[u16]) -> u16 {
    if province <= 0 || province as usize >= owners.len() {
        return authored;
    }
    let p = province as usize;
    let owner = owners[p];
    if p >= authored_owners.len() || authored_owners[p] == owner {
        return authored;
    }
    if owner == 0 { UNOWNED } else { owner }
}

fn holder_rows(holders: &mut [u16], y0: usize, authored: &[u16], c2p: &[u16], width: usize,
               owners: &[u16], authored_owners: &[u16]) {
    let rows = holders.len() / width;
    for dy in 0..rows {
        let row = (y0 + dy) * width;
        for x in 0..width {
            let cell = row + x;
            let a = authored[cell];
            holders[dy * width + x] = if a == SEA { SEA } else { current_holder(a, c2p[cell] as i32, owners, authored_owners) };
        }
    }
}

fn init_rows(d: &mut [f32], y0: usize, holders: &[u16], width: usize, height: usize) {
    let rows = d.len() / width;
    for dy in 0..rows {
        let y = y0 + dy;
        let row = y * width;
        for x in 0..width {
            let cell = row + x;
            let here = holders[cell];
            let frontier = holders[row + if x + 1 == width { 0 } else { x + 1 }] != here
                || holders[row + if x == 0 { width - 1 } else { x - 1 }] != here
                || (y > 0 && holders[cell - width] != here)
                || (y + 1 < height && holders[cell + width] != here);
            d[dy * width + x] = if frontier { 0.5 } else { CAP };
        }
    }
}

fn chamfer(d: &mut [f32], width: usize, height: usize) {
    let straight = 1.0f32;
    let diagonal = 1.41421356f32;
    for y in 0..height {
        let row = y * width;
        for x in 0..width {
            let cell = row + x;
            let mut best = d[cell];
            if best <= 0.5 { continue; }
            let west = row + if x == 0 { width - 1 } else { x - 1 };
            best = minf(best, d[west] + straight);
            if y > 0 {
                let up = cell - width;
                best = minf(best, d[up] + straight);
                best = minf(best, d[row - width + if x == 0 { width - 1 } else { x - 1 }] + diagonal);
                best = minf(best, d[row - width + if x + 1 == width { 0 } else { x + 1 }] + diagonal);
            }
            d[cell] = best;
        }
    }
    for y in (0..height).rev() {
        let row = y * width;
        for x in (0..width).rev() {
            let cell = row + x;
            let mut best = d[cell];
            if best <= 0.5 { continue; }
            let east = row + if x + 1 == width { 0 } else { x + 1 };
            best = minf(best, d[east] + straight);
            if y + 1 < height {
                let down = cell + width;
                best = minf(best, d[down] + straight);
                best = minf(best, d[row + width + if x == 0 { width - 1 } else { x - 1 }] + diagonal);
                best = minf(best, d[row + width + if x + 1 == width { 0 } else { x + 1 }] + diagonal);
            }
            d[cell] = minf(best, CAP);
        }
    }
}

fn weights(sigma: f32) -> (Vec<f32>, i32) {
    let radius = (sigma * 3.0).ceil() as i32;
    let mut w = vec![0.0f32; (radius * 2 + 1) as usize];
    let mut total = 0.0f32;
    for i in -radius..=radius {
        let v = ((-i * i) as f32 / (2.0 * sigma * sigma)).exp();
        w[(i + radius) as usize] = v;
        total += v;
    }
    for v in w.iter_mut() {
        *v /= total;
    }
    (w, radius)
}

fn horizontal_rows(across: &mut [f32], y0: usize, field: &[f32], width: usize, w: &[f32], radius: i32) {
    let rows = across.len() / width;
    let wi = width as i32;
    for dy in 0..rows {
        let row = (y0 + dy) * width;
        for x in 0..width {
            let mut sum = 0.0f32;
            for i in -radius..=radius {
                let mut column = x as i32 + i;
                if column < 0 { column += wi; } else if column >= wi { column -= wi; }
                sum += field[row + column as usize] * w[(i + radius) as usize];
            }
            across[dy * width + x] = sum;
        }
    }
}

fn vertical_rows(field: &mut [f32], y0: usize, across: &[f32], width: usize, height: usize, w: &[f32], radius: i32) {
    let rows = field.len() / width;
    for dy in 0..rows {
        let y = (y0 + dy) as i32;
        for x in 0..width {
            let mut sum = 0.0f32;
            for i in -radius..=radius {
                let line = (y + i).clamp(0, height as i32 - 1) as usize;
                sum += across[line * width + x] * w[(i + radius) as usize];
            }
            field[dy * width + x] = sum;
        }
    }
}

fn quantize(out: &mut [u8], d: &[f32]) {
    let scale = 255.0f32 / CAP;
    for (o, &v) in out.iter_mut().zip(d.iter()) {
        let value = v * scale;
        *o = if value <= 0.0 { 0 } else if value >= 255.0 { 255 } else { (value + 0.5) as i32 as u8 };
    }
}

/// Runs `work(chunk, first_row)` over `rows`-row chunks of `data` on `threads` threads.
fn par_rows<T: Send, F>(data: &mut [T], width: usize, threads: usize, work: F)
where
    F: Fn(&mut [T], usize) + Sync,
{
    let height = data.len() / width;
    if threads <= 1 {
        work(data, 0);
        return;
    }
    let rows_per = (height + threads - 1) / threads;
    std::thread::scope(|scope| {
        for (index, chunk) in data.chunks_mut(rows_per * width).enumerate() {
            let work = &work;
            scope.spawn(move || work(chunk, index * rows_per));
        }
    });
}

unsafe fn frontier(authored: *const u16, c2p: *const u16, width: i32, height: i32,
                   owners: *const u16, owners_len: u32, authored_owners: *const u16, authored_owners_len: u32,
                   out: *mut u8, threads: usize) -> i32 {
    if authored.is_null() || c2p.is_null() || owners.is_null() || out.is_null() || width <= 0 || height <= 0 {
        return 1;
    }
    let (w, h) = (width as usize, height as usize);
    let cells = w * h;
    let authored = slice::from_raw_parts(authored, cells);
    let c2p = slice::from_raw_parts(c2p, cells);
    let owners = slice::from_raw_parts(owners, owners_len as usize);
    let authored_owners: &[u16] = if authored_owners.is_null() { &[] } else { slice::from_raw_parts(authored_owners, authored_owners_len as usize) };
    let out = slice::from_raw_parts_mut(out, cells);

    let mut holders = vec![0u16; cells];
    par_rows(&mut holders, w, threads, |chunk, y0| holder_rows(chunk, y0, authored, c2p, w, owners, authored_owners));
    let mut d = vec![0.0f32; cells];
    par_rows(&mut d, w, threads, |chunk, y0| init_rows(chunk, y0, &holders, w, h));
    drop(holders);
    chamfer(&mut d, w, h);
    let (wt, radius) = weights(1.0);
    let mut across = vec![0.0f32; cells];
    par_rows(&mut across, w, threads, |chunk, y0| horizontal_rows(chunk, y0, &d, w, &wt, radius));
    par_rows(&mut d, w, threads, |chunk, y0| vertical_rows(chunk, y0, &across, w, h, &wt, radius));
    drop(across);
    if threads <= 1 {
        quantize(out, &d);
    } else {
        let rows_per = (h + threads - 1) / threads;
        std::thread::scope(|scope| {
            for (o, s) in out.chunks_mut(rows_per * w).zip(d.chunks(rows_per * w)) {
                scope.spawn(move || quantize(o, s));
            }
        });
    }
    0
}

/// FrontierField.Build, single-threaded, line by line. `out` holds width*height bytes.
#[no_mangle]
pub unsafe extern "C" fn lb_frontier_v1(authored: *const u16, c2p: *const u16, width: i32, height: i32,
                                        owners: *const u16, owners_len: u32, authored_owners: *const u16,
                                        authored_owners_len: u32, out: *mut u8) -> i32 {
    frontier(authored, c2p, width, height, owners, owners_len, authored_owners, authored_owners_len, out, 1)
}

/// The same, with every row-parallel stage spread over `threads` std threads (the C# game
/// code is threaded the same way; the chamfer stays sequential in both).
#[no_mangle]
pub unsafe extern "C" fn lb_frontier_parallel_v1(authored: *const u16, c2p: *const u16, width: i32, height: i32,
                                                 owners: *const u16, owners_len: u32, authored_owners: *const u16,
                                                 authored_owners_len: u32, out: *mut u8, threads: i32) -> i32 {
    frontier(authored, c2p, width, height, owners, owners_len, authored_owners, authored_owners_len, out,
             threads.max(1) as usize)
}

// ------------------------------------------------------------------------- tuned (the native
// counterpart of the host's cs_tuned): row stages on threads, scratch kept between calls, the
// chamfer's row-above/below candidates made for a whole row in a loop LLVM vectorises, and both
// Gaussian passes written tap-outer so every cell still sums its taps in the game's order.

struct Scratch {
    holders: Vec<u16>,
    d: Vec<f32>,
    across: Vec<f32>,
    candidate: Vec<f32>,
}

static SCRATCH: std::sync::Mutex<Option<Scratch>> = std::sync::Mutex::new(None);

fn chamfer_tuned(d: &mut [f32], width: usize, height: usize, candidate: &mut [f32]) {
    let straight = 1.0f32;
    let diagonal = 1.41421356f32;
    let w = width;
    for y in 0..height {
        let (before, rest) = d.split_at_mut(y * w);
        let row = &mut rest[..w];
        if y > 0 {
            let up = &before[(y - 1) * w..y * w];
            candidate[0] = minf(minf(up[0] + straight, up[w - 1] + diagonal), up[1] + diagonal);
            for x in 1..w - 1 {
                candidate[x] = minf(minf(up[x] + straight, up[x - 1] + diagonal), up[x + 1] + diagonal);
            }
            candidate[w - 1] = minf(minf(up[w - 1] + straight, up[w - 2] + diagonal), up[0] + diagonal);
            let mut west = row[w - 1];
            for x in 0..w {
                let mut best = row[x];
                if best <= 0.5 { west = best; continue; }
                best = minf(minf(best, west + straight), candidate[x]);
                row[x] = best;
                west = best;
            }
        } else {
            let mut west = row[w - 1];
            for x in 0..w {
                let mut best = row[x];
                if best <= 0.5 { west = best; continue; }
                best = minf(best, west + straight);
                row[x] = best;
                west = best;
            }
        }
    }
    for y in (0..height).rev() {
        let (head, after) = d.split_at_mut((y + 1) * w);
        let row = &mut head[y * w..];
        if y + 1 < height {
            let down = &after[..w];
            candidate[0] = minf(minf(down[0] + straight, down[w - 1] + diagonal), down[1] + diagonal);
            for x in 1..w - 1 {
                candidate[x] = minf(minf(down[x] + straight, down[x - 1] + diagonal), down[x + 1] + diagonal);
            }
            candidate[w - 1] = minf(minf(down[w - 1] + straight, down[w - 2] + diagonal), down[0] + diagonal);
            let mut east = row[0];
            for x in (0..w).rev() {
                let mut best = row[x];
                if best <= 0.5 { east = best; continue; }
                best = minf(minf(minf(best, east + straight), candidate[x]), CAP);
                row[x] = best;
                east = best;
            }
        } else {
            let mut east = row[0];
            for x in (0..w).rev() {
                let mut best = row[x];
                if best <= 0.5 { east = best; continue; }
                best = minf(minf(best, east + straight), CAP);
                row[x] = best;
                east = best;
            }
        }
    }
}

fn horizontal_tuned(across: &mut [f32], y0: usize, field: &[f32], width: usize, w: &[f32], radius: usize) {
    let rows = across.len() / width;
    for dy in 0..rows {
        let src = &field[(y0 + dy) * width..(y0 + dy + 1) * width];
        let dst = &mut across[dy * width..(dy + 1) * width];
        // Interior, tap-outer: dst[x] = ((0 + s[x-r]*w0) + s[x-r+1]*w1) + ... in the game's order.
        let interior = &mut dst[radius..width - radius];
        interior.fill(0.0);
        for (tap, &weight) in w.iter().enumerate() {
            let s = &src[tap..tap + width - 2 * radius];
            for (o, &v) in interior.iter_mut().zip(s.iter()) {
                *o += v * weight;
            }
        }
        let wi = width as i32;
        let r = radius as i32;
        for x in (0..radius).chain(width - radius..width) {
            let mut sum = 0.0f32;
            for i in -r..=r {
                let mut column = x as i32 + i;
                if column < 0 { column += wi; } else if column >= wi { column -= wi; }
                sum += src[column as usize] * w[(i + r) as usize];
            }
            dst[x] = sum;
        }
    }
}

fn vertical_tuned(field: &mut [f32], y0: usize, across: &[f32], width: usize, height: usize, w: &[f32], radius: usize) {
    let rows = field.len() / width;
    let r = radius as i32;
    for dy in 0..rows {
        let y = (y0 + dy) as i32;
        let dst = &mut field[dy * width..(dy + 1) * width];
        dst.fill(0.0);
        for i in -r..=r {
            let line = (y + i).clamp(0, height as i32 - 1) as usize;
            let src = &across[line * width..(line + 1) * width];
            let weight = w[(i + r) as usize];
            for (o, &v) in dst.iter_mut().zip(src.iter()) {
                *o += v * weight;
            }
        }
    }
}

/// FrontierField.Build, tuned: threads, vector-friendly loops, scratch kept between calls.
#[no_mangle]
pub unsafe extern "C" fn lb_frontier_tuned_v1(authored: *const u16, c2p: *const u16, width: i32, height: i32,
                                              owners: *const u16, owners_len: u32, authored_owners: *const u16,
                                              authored_owners_len: u32, out: *mut u8, threads: i32) -> i32 {
    if authored.is_null() || c2p.is_null() || owners.is_null() || out.is_null() || width < 8 || height <= 0 {
        return 1;
    }
    let threads = threads.max(1) as usize;
    let (w, h) = (width as usize, height as usize);
    let cells = w * h;
    let authored = slice::from_raw_parts(authored, cells);
    let c2p = slice::from_raw_parts(c2p, cells);
    let owners = slice::from_raw_parts(owners, owners_len as usize);
    let authored_owners: &[u16] = if authored_owners.is_null() { &[] } else { slice::from_raw_parts(authored_owners, authored_owners_len as usize) };
    let out = slice::from_raw_parts_mut(out, cells);

    let mut guard = SCRATCH.lock().unwrap_or_else(|e| e.into_inner());
    let s = guard.get_or_insert_with(|| Scratch { holders: Vec::new(), d: Vec::new(), across: Vec::new(), candidate: Vec::new() });
    if s.holders.len() != cells { s.holders = vec![0; cells]; s.d = vec![0.0; cells]; s.across = vec![0.0; cells]; }
    if s.candidate.len() != w { s.candidate = vec![0.0; w]; }
    let Scratch { holders, d, across, candidate } = s;

    par_rows(holders, w, threads, |chunk, y0| holder_rows(chunk, y0, authored, c2p, w, owners, authored_owners));
    {
        let holders: &[u16] = holders;
        par_rows(d, w, threads, |chunk, y0| init_rows(chunk, y0, holders, w, h));
    }
    chamfer_tuned(d, w, h, candidate);
    let (wt, radius) = weights(1.0);
    {
        let dd: &[f32] = d;
        par_rows(across, w, threads, |chunk, y0| horizontal_tuned(chunk, y0, dd, w, &wt, radius as usize));
    }
    {
        let aa: &[f32] = across;
        par_rows(d, w, threads, |chunk, y0| vertical_tuned(chunk, y0, aa, w, h, &wt, radius as usize));
    }
    let rows_per = (h + threads - 1) / threads;
    let dd: &[f32] = d;
    std::thread::scope(|scope| {
        for (o, src) in out.chunks_mut(rows_per * w).zip(dd.chunks(rows_per * w)) {
            scope.spawn(move || quantize(o, src));
        }
    });
    0
}

#[inline(always)]
fn scale_of(line_floats: &[f32], offsets: &[i32], fslots: &[i32], fweights: &[f32], slots: &[f32],
            line: usize, slot_base: usize) -> f32 {
    let mut scale = line_floats[line * 12 + 2];
    for k in offsets[line] as usize..offsets[line + 1] as usize {
        scale += fweights[k] * slots[slot_base + fslots[k] as usize];
    }
    scale
}

/// BudgetCore.Evaluate for every country over the flat layout (see BudgetInput in the host).
#[no_mangle]
pub unsafe extern "C" fn lb_budget_v1(nations: i32, lines: i32, tax_tolerance: f32, tax_evasion: f32,
                                      line_floats: *const f32, line_ints: *const i32, factor_offsets: *const i32,
                                      factor_slots: *const i32, factor_weights: *const f32, slots: *const f32,
                                      nation_floats: *const f32, rates: *const f32, out: *mut f32) -> i32 {
    if nations < 0 || lines < 0 { return 1; }
    let (nn, ll) = (nations as usize, lines as usize);
    let offsets = slice::from_raw_parts(factor_offsets, ll + 1);
    let factors = offsets[ll] as usize;
    let lf = slice::from_raw_parts(line_floats, ll * 12);
    let li = slice::from_raw_parts(line_ints, ll * 4);
    let fs: &[i32] = if factors == 0 { &[] } else { slice::from_raw_parts(factor_slots, factors) };
    let fw: &[f32] = if factors == 0 { &[] } else { slice::from_raw_parts(factor_weights, factors) };
    let slots = slice::from_raw_parts(slots, nn * 8);
    let nf = slice::from_raw_parts(nation_floats, nn * 8);
    let rates = slice::from_raw_parts(rates, nn * ll);
    let out = slice::from_raw_parts_mut(out, nn * 8);

    for c in 0..nn {
        let slot_base = c * 8;
        let n = c * 8;
        let rate_base = c * ll;
        let weekly_output = nf[n];
        let compliance = if weekly_output <= 0.0 || tax_evasion <= 0.0 {
            1.0f32
        } else {
            let mut burden = 0.0f32;
            for line in 0..ll {
                if !(li[line * 4] == 0 && li[line * 4 + 1] == 0) { continue; }
                let rate = rates[rate_base + line];
                burden += rate * lf[line * 12] * scale_of(lf, offsets, fs, fw, slots, line, slot_base);
            }
            1.0 / (1.0 + tax_evasion * max0(burden - tax_tolerance))
        };
        let (mut revenue, mut taxation, mut expenses) = (0.0f32, 0.0f32, 0.0f32);
        let (mut growth, mut living, mut literacy, mut radicalism, mut stability) = (0.0f32, 0.0f32, 0.0f32, 0.0f32, 0.0f32);
        for line in 0..ll {
            let f = line * 12;
            let side = li[line * 4];
            let charge = li[line * 4 + 1];
            let basis = match charge { 1 => nf[n + 1], 2 => nf[n + 2], 3 => nf[n + 3], _ => weekly_output };
            let rate = rates[rate_base + line];
            let loss_yield = lf[f + 1];
            let yld = if !loss_yield.is_nan() && basis < 0.0 { loss_yield } else { lf[f] };
            let mut amount = basis * rate * yld * scale_of(lf, offsets, fs, fw, slots, line, slot_base);
            if nf[n + 7] != 0.0 && li[line * 4 + 3] != 0 {
                amount += nf[n + 4] * nf[n + 5] / 52.0;
            }
            if side == 0 && charge == 0 { amount *= compliance * nf[n + 6]; }
            amount = if charge == 3 { amount } else { max0(amount) };
            if side == 0 {
                revenue += amount;
                if charge == 0 || charge == 1 { taxation += amount; }
            } else {
                expenses += amount;
            }
            if li[line * 4 + 2] == 0 { continue; }
            let above = rate - lf[f + 3];
            growth += above * lf[f + 4];
            living += above * lf[f + 5];
            literacy += above * lf[f + 6];
            radicalism += above * lf[f + 7];
            stability += above * lf[f + 8];
            let resented_above = lf[f + 9];
            if resented_above <= 0.0 { continue; }
            let resented = max0(rate - resented_above);
            if resented <= 0.0 { continue; }
            living += resented * lf[f + 10];
            radicalism += resented * lf[f + 11];
        }
        let o = c * 8;
        out[o] = revenue;
        out[o + 1] = taxation;
        out[o + 2] = expenses;
        out[o + 3] = growth;
        out[o + 4] = living;
        out[o + 5] = literacy;
        out[o + 6] = radicalism;
        out[o + 7] = stability;
    }
    0
}
