// SPDX-License-Identifier: GPL-3.0-only
// Independent C ABI ownership experiment. See ../../../../LICENSE and ../README.md.
package dof_ownership_odin

MAX_VERTICES :: 1_000_000
MAX_NEIGHBORS :: 16_000_000
INVALID :: i32(1)
CAPACITY :: i32(2)
INTERNAL :: i32(3)

Region :: struct {
    start, end: uintptr,
}

// Pointer shape checks cannot prove that a foreign allocation is mapped/alive.
region :: proc "contextless" (pointer: rawptr, length, element_size, alignment: uintptr) -> (Region, bool) {
    start := uintptr(pointer)
    if length == 0 {
        if pointer != nil && start % alignment != 0 {
            return Region{}, false
        }
        return Region{}, true
    }
    if pointer == nil || start % alignment != 0 {
        return Region{}, false
    }
    maximum := ~uintptr(0)
    if length > maximum / element_size {
        return Region{}, false
    }
    bytes := length * element_size
    // Keep the same signed-pointer-size range restriction as Rust slices.
    if bytes > maximum / 2 || start > maximum - bytes {
        return Region{}, false
    }
    return Region{start, start + bytes}, true
}

overlaps :: proc "contextless" (a, b: Region) -> bool {
    return a.start < b.end && b.start < a.end
}

preflight :: proc "contextless" (
    offsets: [^]i32, neighbors: [^]i32, owners: [^]u16, water: [^]u8,
    labels, member_order, component_offsets: [^]i32,
    out_component_count, out_member_count: ^i32, v, e: int,
) -> bool {
    inputs: [4]Region
    input_pointers := [4]rawptr{rawptr(offsets), rawptr(neighbors), rawptr(owners), rawptr(water)}
    input_lengths := [4]uintptr{uintptr(v + 1), uintptr(e), uintptr(v), uintptr(v)}
    input_sizes := [4]uintptr{size_of(i32), size_of(i32), size_of(u16), size_of(u8)}
    input_alignments := [4]uintptr{align_of(i32), align_of(i32), align_of(u16), align_of(u8)}
    for pointer, index in input_pointers {
        valid: bool
        inputs[index], valid = region(pointer, input_lengths[index], input_sizes[index], input_alignments[index])
        if !valid {
            return false
        }
    }
    outputs: [5]Region
    output_pointers := [5]rawptr{rawptr(labels), rawptr(member_order), rawptr(component_offsets),
        rawptr(out_component_count), rawptr(out_member_count)}
    output_lengths := [5]uintptr{uintptr(v), uintptr(v), uintptr(v + 1), 1, 1}
    for pointer, index in output_pointers {
        valid: bool
        outputs[index], valid = region(pointer, output_lengths[index], size_of(i32), align_of(i32))
        if !valid {
            return false
        }
        for input in inputs {
            if overlaps(outputs[index], input) {
                return false
            }
        }
        for previous in 0..<index {
            if overlaps(outputs[index], outputs[previous]) {
                return false
            }
        }
    }
    return true
}

@(export)
dof_graph_abi_version :: proc "c" () -> u32 {
    return 1
}

// Exact parameter order/types in ../../ownership.h. Output capacity checks refer
// to required regions, not unused caller capacity. No allocation or retained state.
@(export)
dof_owner_components_v1 :: proc "c" (
    offsets: [^]i32, offsets_count: u32,
    neighbors: [^]i32, neighbor_count: u32,
    owners: [^]u16, vertex_count: u32,
    water: [^]u8, water_count: u32,
    labels: [^]i32, labels_capacity: u32,
    member_order: [^]i32, member_capacity: u32,
    component_offsets: [^]i32, component_offsets_capacity: u32,
    out_component_count, out_member_count: ^i32,
) -> i32 {
    if vertex_count == 0 || vertex_count > MAX_VERTICES || neighbor_count > MAX_NEIGHBORS ||
        offsets_count != vertex_count + 1 || water_count != vertex_count {
        return INVALID
    }
    if labels_capacity < vertex_count || member_capacity < vertex_count || component_offsets_capacity < vertex_count + 1 {
        return CAPACITY
    }
    v := int(vertex_count)
    e := int(neighbor_count)
    if !preflight(offsets, neighbors, owners, water, labels, member_order,
        component_offsets, out_component_count, out_member_count, v, e) {
        return INVALID
    }

    // Validate every CSR entry and water flag before writing any output.
    if offsets[0] != 0 || offsets[v] != i32(e) {
        return INVALID
    }
    for vertex in 0..<v {
        if offsets[vertex] < 0 || offsets[vertex] > offsets[vertex + 1] || water[vertex] > 1 {
            return INVALID
        }
    }
    for edge in 0..<e {
        if neighbors[edge] < 0 || neighbors[edge] >= i32(v) {
            return INVALID
        }
    }

    // Slices add normal language bounds checks; the build does not disable them.
    label_slice := labels[:v]
    members := member_order[:v]
    boundaries := component_offsets[:v + 1]
    for index in 0..<v {
        label_slice[index] = 0
        members[index] = 0
        boundaries[index] = 0
    }
    boundaries[v] = 0
    components, tail := 0, 0
    for start in 1..<v {
        if label_slice[start] != 0 || owners[start] == 0 || water[start] != 0 {
            continue
        }
        components += 1
        owner := owners[start]
        head := tail
        if tail >= v || components >= v {
            return INTERNAL
        }
        label_slice[start] = i32(components)
        members[tail] = i32(start)
        tail += 1
        // The caller's member buffer is also the FIFO. Appending at discovery
        // preserves the same order as recording vertices at dequeue.
        for head < tail {
            current := int(members[head])
            head += 1
            for edge := int(offsets[current]); edge < int(offsets[current + 1]); edge += 1 {
                next := int(neighbors[edge])
                if next == 0 || label_slice[next] != 0 || owners[next] != owner || water[next] != 0 {
                    continue
                }
                if tail >= v {
                    return INTERNAL
                }
                label_slice[next] = i32(components)
                members[tail] = i32(next)
                tail += 1
            }
        }
        boundaries[components] = i32(tail)
    }
    out_component_count^ = i32(components)
    out_member_count^ = i32(tail)
    return 0
}
