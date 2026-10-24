// SPDX-License-Identifier: GPL-3.0-only
// Tools-only experiment. No campaign state, allocation, callbacks or retained pointers.
package dof_exposure_odin

import "core:math"

Route :: struct { good, seller: i32, volume: f32 }
Region :: struct { start, end: uintptr }
MAX_ROWS :: 262_144

region :: proc "contextless" (p: rawptr, count, size, alignment: uintptr) -> (Region, bool) {
    start := uintptr(p)
    if count == 0 { return Region{}, p == nil || start % alignment == 0 }
    maximum := ~uintptr(0)
    if p == nil || start % alignment != 0 || count > maximum / size { return Region{}, false }
    bytes := count * size
    if bytes > maximum / 2 || start > maximum - bytes { return Region{}, false }
    return Region{start, start + bytes}, true
}

@(export)
dof_exposure_abi_version :: proc "c" () -> u32 { return 1 }

// Status 1: invalid input, 2: insufficient/non-power-of-two capacity.
// Caller owns distinct buffers, each alive for the call. Shape checks cannot prove mapping/lifetime.
@(export)
dof_exposure_sum_v1 :: proc "c" (
    input: [^]Route, count: u32, output: [^]Route, output_capacity: u32,
    sums: [^]f64, sums_capacity: u32, slots: [^]i32, slots_capacity: u32,
    out_count: ^u32,
) -> i32 {
    if count > MAX_ROWS { return 1 }
    if output_capacity < count || sums_capacity < count || slots_capacity > 2 * MAX_ROWS || slots_capacity < max(2 * count, 1) ||
        slots_capacity & (slots_capacity - 1) != 0 { return 2 }
    pointers := [5]rawptr{rawptr(input), rawptr(output), rawptr(sums), rawptr(slots), rawptr(out_count)}
    counts := [5]uintptr{uintptr(count), uintptr(count), uintptr(count), uintptr(slots_capacity), 1}
    sizes := [5]uintptr{size_of(Route), size_of(Route), size_of(f64), size_of(i32), size_of(u32)}
    aligns := [5]uintptr{align_of(Route), align_of(Route), align_of(f64), align_of(i32), align_of(u32)}
    regions: [5]Region
    for pointer, index in pointers {
        valid: bool
        regions[index], valid = region(pointer, counts[index], sizes[index], aligns[index])
        if !valid { return 1 }
        for previous in 0..<index {
            if regions[index].start < regions[previous].end && regions[previous].start < regions[index].end { return 1 }
        }
    }
    rows := input[:int(count)]
    for row in rows {
        if row.seller < 0 || math.is_nan(row.volume) || math.is_inf(row.volume) || row.volume <= 0 { return 1 }
    }
    table := slots[:int(slots_capacity)]
    for &slot in table { slot = 0 }
    written: u32
    for row in rows {
        // Hashing only picks a slot. Results follow first encounter and source-order double sums.
        key := (u64(transmute(u32)row.good) << 32) | u64(u32(row.seller))
        hash := key
        hash ~= hash >> 33
        hash *= 0xff51afd7ed558ccd
        hash ~= hash >> 33
        at := u32(hash) & (slots_capacity - 1)
        for table[at] != 0 {
            prior := u32(table[at] - 1)
            if output[prior].good == row.good && output[prior].seller == row.seller { break }
            at = (at + 1) & (slots_capacity - 1)
        }
        if table[at] == 0 {
            output[written] = Route{row.good, row.seller, 0}
            sums[written] = 0
            table[at] = i32(written + 1)
            written += 1
        }
        sums[table[at] - 1] += f64(row.volume)
    }
    for index in 0..<int(written) { output[index].volume = f32(sums[index]) }
    out_count^ = written
    return 0
}
