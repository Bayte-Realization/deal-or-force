// SPDX-License-Identifier: GPL-3.0-only
// LanguageBench kernels in Zig, behind the same C ABI as native/rust (see ../../README.md).
// Line-by-line ports of FrontierField.Build (sequential) and BudgetCore.Evaluate (flat).
// Built ReleaseFast for a baseline x86-64 CPU by ../../build-native.ps1: no bounds checks
// (that is what ReleaseFast means), strict IEEE floats (Zig never relaxes them unasked).
const std = @import("std");

const cap: f32 = 8.0;
const sea: u16 = 0;
const unowned: u16 = 65535;

inline fn minf(a: f32, b: f32) f32 {
    return if (a < b) a else b;
}

inline fn max0(x: f32) f32 {
    if (x > 0.0) return x;
    if (x != x) return x;
    return 0.0;
}

export fn lb_abi_version() u32 {
    return 1;
}

export fn lb_noop16(a0: usize, a1: usize, a2: usize, a3: usize, a4: usize, a5: usize, a6: usize, a7: usize, a8: usize, a9: usize, a10: usize, a11: usize, a12: usize, a13: usize, a14: usize, a15: usize) i32 {
    _ = .{ a0, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15 };
    return 0;
}

inline fn currentHolder(authored: u16, province: i32, owners: []const u16, authored_owners: []const u16) u16 {
    if (province <= 0 or @as(usize, @intCast(province)) >= owners.len) return authored;
    const p: usize = @intCast(province);
    const owner = owners[p];
    if (p >= authored_owners.len or authored_owners[p] == owner) return authored;
    return if (owner == 0) unowned else owner;
}

export fn lb_frontier_v1(authored_ptr: [*]const u16, c2p_ptr: [*]const u16, width_i: i32, height_i: i32, owners_ptr: [*]const u16, owners_len: u32, authored_owners_ptr: ?[*]const u16, authored_owners_len: u32, out_ptr: [*]u8) i32 {
    if (width_i <= 0 or height_i <= 0) return 1;
    const width: usize = @intCast(width_i);
    const height: usize = @intCast(height_i);
    const cells = width * height;
    const authored = authored_ptr[0..cells];
    const c2p = c2p_ptr[0..cells];
    const owners = owners_ptr[0..owners_len];
    const authored_owners: []const u16 = if (authored_owners_ptr) |p| p[0..authored_owners_len] else &[_]u16{};
    const out = out_ptr[0..cells];
    const alloc = std.heap.page_allocator;

    const holders = alloc.alloc(u16, cells) catch return 2;
    defer alloc.free(holders);
    var y: usize = 0;
    while (y < height) : (y += 1) {
        const row = y * width;
        var x: usize = 0;
        while (x < width) : (x += 1) {
            const cell = row + x;
            const a = authored[cell];
            holders[cell] = if (a == sea) sea else currentHolder(a, @intCast(c2p[cell]), owners, authored_owners);
        }
    }

    const d = alloc.alloc(f32, cells) catch return 2;
    defer alloc.free(d);
    y = 0;
    while (y < height) : (y += 1) {
        const row = y * width;
        var x: usize = 0;
        while (x < width) : (x += 1) {
            const cell = row + x;
            const here = holders[cell];
            const frontier = holders[row + (if (x + 1 == width) 0 else x + 1)] != here or
                holders[row + (if (x == 0) width - 1 else x - 1)] != here or
                (y > 0 and holders[cell - width] != here) or
                (y + 1 < height and holders[cell + width] != here);
            d[cell] = if (frontier) 0.5 else cap;
        }
    }

    const straight: f32 = 1.0;
    const diagonal: f32 = 1.41421356;
    y = 0;
    while (y < height) : (y += 1) {
        const row = y * width;
        var x: usize = 0;
        while (x < width) : (x += 1) {
            const cell = row + x;
            var best = d[cell];
            if (best <= 0.5) continue;
            const west = row + (if (x == 0) width - 1 else x - 1);
            best = minf(best, d[west] + straight);
            if (y > 0) {
                const up = cell - width;
                best = minf(best, d[up] + straight);
                best = minf(best, d[row - width + (if (x == 0) width - 1 else x - 1)] + diagonal);
                best = minf(best, d[row - width + (if (x + 1 == width) 0 else x + 1)] + diagonal);
            }
            d[cell] = best;
        }
    }
    var yy: usize = height;
    while (yy > 0) {
        yy -= 1;
        const row = yy * width;
        var xx: usize = width;
        while (xx > 0) {
            xx -= 1;
            const cell = row + xx;
            var best = d[cell];
            if (best <= 0.5) continue;
            const east = row + (if (xx + 1 == width) 0 else xx + 1);
            best = minf(best, d[east] + straight);
            if (yy + 1 < height) {
                const down = cell + width;
                best = minf(best, d[down] + straight);
                best = minf(best, d[row + width + (if (xx == 0) width - 1 else xx - 1)] + diagonal);
                best = minf(best, d[row + width + (if (xx + 1 == width) 0 else xx + 1)] + diagonal);
            }
            d[cell] = minf(best, cap);
        }
    }

    // Smooth, sigma 1.
    const sigma: f32 = 1.0;
    const radius: i32 = @intFromFloat(@ceil(sigma * 3.0));
    var weights: [7]f32 = undefined;
    var total: f32 = 0.0;
    var i: i32 = -radius;
    while (i <= radius) : (i += 1) {
        const v = @exp(@as(f32, @floatFromInt(-i * i)) / (2.0 * sigma * sigma));
        weights[@intCast(i + radius)] = v;
        total += v;
    }
    for (&weights) |*w| w.* /= total;

    const across = alloc.alloc(f32, cells) catch return 2;
    defer alloc.free(across);
    const wi: i32 = @intCast(width);
    y = 0;
    while (y < height) : (y += 1) {
        const row = y * width;
        var x: usize = 0;
        while (x < width) : (x += 1) {
            var sum: f32 = 0.0;
            i = -radius;
            while (i <= radius) : (i += 1) {
                var column: i32 = @as(i32, @intCast(x)) + i;
                if (column < 0) column += wi else if (column >= wi) column -= wi;
                sum += d[row + @as(usize, @intCast(column))] * weights[@intCast(i + radius)];
            }
            across[row + x] = sum;
        }
    }
    const hi: i32 = @intCast(height);
    y = 0;
    while (y < height) : (y += 1) {
        const row = y * width;
        var x: usize = 0;
        while (x < width) : (x += 1) {
            var sum: f32 = 0.0;
            i = -radius;
            while (i <= radius) : (i += 1) {
                const line: usize = @intCast(std.math.clamp(@as(i32, @intCast(y)) + i, 0, hi - 1));
                sum += across[line * width + x] * weights[@intCast(i + radius)];
            }
            d[row + x] = sum;
        }
    }

    const scale: f32 = 255.0 / cap;
    for (out, d) |*o, v| {
        const value = v * scale;
        o.* = if (value <= 0.0) 0 else if (value >= 255.0) 255 else @intCast(@as(i32, @intFromFloat(value + 0.5)));
    }
    return 0;
}

inline fn scaleOf(lf: []const f32, offsets: []const i32, fs: []const i32, fw: []const f32, slots: []const f32, line: usize, slot_base: usize) f32 {
    var scale = lf[line * 12 + 2];
    var k: usize = @intCast(offsets[line]);
    const end: usize = @intCast(offsets[line + 1]);
    while (k < end) : (k += 1) scale += fw[k] * slots[slot_base + @as(usize, @intCast(fs[k]))];
    return scale;
}

export fn lb_budget_v1(nations: i32, lines: i32, tax_tolerance: f32, tax_evasion: f32, line_floats: [*]const f32, line_ints: [*]const i32, factor_offsets: [*]const i32, factor_slots: [*]const i32, factor_weights: [*]const f32, slots_ptr: [*]const f32, nation_floats: [*]const f32, rates_ptr: [*]const f32, out_ptr: [*]f32) i32 {
    if (nations < 0 or lines < 0) return 1;
    const nn: usize = @intCast(nations);
    const ll: usize = @intCast(lines);
    const offsets = factor_offsets[0 .. ll + 1];
    const factors: usize = @intCast(offsets[ll]);
    const lf = line_floats[0 .. ll * 12];
    const li = line_ints[0 .. ll * 4];
    const fs = factor_slots[0..factors];
    const fw = factor_weights[0..factors];
    const slots = slots_ptr[0 .. nn * 8];
    const nf = nation_floats[0 .. nn * 8];
    const rates = rates_ptr[0 .. nn * ll];
    const out = out_ptr[0 .. nn * 8];

    var c: usize = 0;
    while (c < nn) : (c += 1) {
        const slot_base = c * 8;
        const n = c * 8;
        const rate_base = c * ll;
        const weekly_output = nf[n];
        var compliance: f32 = 1.0;
        if (!(weekly_output <= 0.0 or tax_evasion <= 0.0)) {
            var burden: f32 = 0.0;
            var line: usize = 0;
            while (line < ll) : (line += 1) {
                if (!(li[line * 4] == 0 and li[line * 4 + 1] == 0)) continue;
                const rate = rates[rate_base + line];
                burden += rate * lf[line * 12] * scaleOf(lf, offsets, fs, fw, slots, line, slot_base);
            }
            compliance = 1.0 / (1.0 + tax_evasion * max0(burden - tax_tolerance));
        }
        var revenue: f32 = 0.0;
        var taxation: f32 = 0.0;
        var expenses: f32 = 0.0;
        var growth: f32 = 0.0;
        var living: f32 = 0.0;
        var literacy: f32 = 0.0;
        var radicalism: f32 = 0.0;
        var stability: f32 = 0.0;
        var line: usize = 0;
        while (line < ll) : (line += 1) {
            const f = line * 12;
            const side = li[line * 4];
            const charge = li[line * 4 + 1];
            const basis = switch (charge) {
                1 => nf[n + 1],
                2 => nf[n + 2],
                3 => nf[n + 3],
                else => weekly_output,
            };
            const rate = rates[rate_base + line];
            const loss_yield = lf[f + 1];
            const yld = if (!std.math.isNan(loss_yield) and basis < 0.0) loss_yield else lf[f];
            var amount = basis * rate * yld * scaleOf(lf, offsets, fs, fw, slots, line, slot_base);
            if (nf[n + 7] != 0.0 and li[line * 4 + 3] != 0) amount += nf[n + 4] * nf[n + 5] / 52.0;
            if (side == 0 and charge == 0) amount *= compliance * nf[n + 6];
            amount = if (charge == 3) amount else max0(amount);
            if (side == 0) {
                revenue += amount;
                if (charge == 0 or charge == 1) taxation += amount;
            } else expenses += amount;
            if (li[line * 4 + 2] == 0) continue;
            const above = rate - lf[f + 3];
            growth += above * lf[f + 4];
            living += above * lf[f + 5];
            literacy += above * lf[f + 6];
            radicalism += above * lf[f + 7];
            stability += above * lf[f + 8];
            const resented_above = lf[f + 9];
            if (resented_above <= 0.0) continue;
            const resented = max0(rate - resented_above);
            if (resented <= 0.0) continue;
            living += resented * lf[f + 10];
            radicalism += resented * lf[f + 11];
        }
        const o = c * 8;
        out[o] = revenue;
        out[o + 1] = taxation;
        out[o + 2] = expenses;
        out[o + 3] = growth;
        out[o + 4] = living;
        out[o + 5] = literacy;
        out[o + 6] = radicalism;
        out[o + 7] = stability;
    }
    return 0;
}
