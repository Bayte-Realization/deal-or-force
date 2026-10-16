// SPDX-License-Identifier: GPL-3.0-only. See ../../../../LICENSE.
package ownership_abi_tests

import "core:fmt"
import kernel "../src"

Fixture :: struct {
    offsets: [9]i32,
    neighbors: [11]i32,
    owners: [8]u16,
    water: [8]u8,
    labels, members: [8]i32,
    boundaries: [9]i32,
    components, count: i32,
}

Arguments :: struct {
    offsets: [^]i32, offsets_count: u32,
    neighbors: [^]i32, neighbor_count: u32,
    owners: [^]u16, vertex_count: u32,
    water: [^]u8, water_count: u32,
    labels: [^]i32, labels_capacity: u32,
    members: [^]i32, members_capacity: u32,
    boundaries: [^]i32, boundaries_capacity: u32,
    components, count: ^i32,
}

sample :: proc () -> Fixture {
    f := Fixture{
        offsets = {0, 1, 5, 7, 8, 9, 10, 11, 11},
        neighbors = {1, 3, 2, 0, 3, 1, 4, 1, 2, 6, 5},
        owners = {9, 1, 1, 1, 1, 65_535, 65_535, 0},
        water = {0, 0, 0, 0, 1, 0, 0, 0},
        components = -1, count = -1,
    }
    for index in 0..<len(f.labels) {
        f.labels[index] = -1
        f.members[index] = -1
    }
    for index in 0..<len(f.boundaries) {
        f.boundaries[index] = -1
    }
    return f
}

arguments :: proc (f: ^Fixture) -> Arguments {
    return Arguments{
        offsets = raw_data(f.offsets[:]), offsets_count = u32(len(f.offsets)),
        neighbors = raw_data(f.neighbors[:]), neighbor_count = u32(len(f.neighbors)),
        owners = raw_data(f.owners[:]), vertex_count = u32(len(f.owners)),
        water = raw_data(f.water[:]), water_count = u32(len(f.water)),
        labels = raw_data(f.labels[:]), labels_capacity = u32(len(f.labels)),
        members = raw_data(f.members[:]), members_capacity = u32(len(f.members)),
        boundaries = raw_data(f.boundaries[:]), boundaries_capacity = u32(len(f.boundaries)),
        components = &f.components, count = &f.count,
    }
}

invoke :: proc (a: Arguments) -> i32 {
    return kernel.dof_owner_components_v1(
        a.offsets, a.offsets_count, a.neighbors, a.neighbor_count,
        a.owners, a.vertex_count, a.water, a.water_count,
        a.labels, a.labels_capacity, a.members, a.members_capacity,
        a.boundaries, a.boundaries_capacity, a.components, a.count,
    )
}

unchanged :: proc (f: ^Fixture) {
    for index in 0..<len(f.labels) {
        assert(f.labels[index] == -1 && f.members[index] == -1)
    }
    for value in f.boundaries {
        assert(value == -1)
    }
    assert(f.components == -1 && f.count == -1)
}

main :: proc () {
    assert(kernel.dof_graph_abi_version() == 1)
    {
        f := sample()
        assert(invoke(arguments(&f)) == 0)
        assert(f.labels == [8]i32{0, 1, 1, 1, 0, 2, 2, 0})
        assert(f.members == [8]i32{1, 3, 2, 5, 6, 0, 0, 0})
        assert(f.boundaries == [9]i32{0, 3, 5, 0, 0, 0, 0, 0, 0})
        assert(f.components == 2 && f.count == 5)
    }
    for issue in 0..<7 {
        f := sample()
        switch issue {
        case 0: f.offsets[0] = 1
        case 1: f.offsets[2] = -1
        case 2: f.offsets[3] = 0
        case 3: f.offsets[len(f.offsets) - 1] += 1
        case 4: f.neighbors[0] = -1
        case 5: f.neighbors[0] = i32(len(f.owners))
        case 6: f.water[0] = 2
        }
        assert(invoke(arguments(&f)) == 1)
        unchanged(&f)
    }
    for issue in 0..<10 {
        f := sample()
        a := arguments(&f)
        switch issue {
        case 0: a.offsets = nil
        case 1: a.labels = ([^]i32)(uintptr(1))
        case 2: a.members = a.labels
        case 3: a.labels = a.offsets
        case 4: a.count = a.components
        case 5: a.labels = ([^]i32)(~uintptr(0) - 3)
        case 6: a.vertex_count = 0
        case 7: a.vertex_count = 1_000_001
        case 8: a.neighbor_count = 16_000_001
        case 9: a.water_count -= 1
        }
        assert(invoke(a) == 1)
        unchanged(&f)
    }
    for issue in 0..<3 {
        f := sample()
        a := arguments(&f)
        switch issue {
        case 0: a.labels_capacity -= 1
        case 1: a.members_capacity -= 1
        case 2: a.boundaries_capacity -= 1
        }
        assert(invoke(a) == 2)
        unchanged(&f)
    }
    {
        f := sample()
        a := arguments(&f)
        offsets := [2]i32{}
        owners := [1]u16{}
        water := [1]u8{1}
        a.offsets = raw_data(offsets[:])
        a.offsets_count = 2
        a.neighbors = nil
        a.neighbor_count = 0
        a.owners = raw_data(owners[:])
        a.vertex_count = 1
        a.water = raw_data(water[:])
        a.water_count = 1
        assert(invoke(a) == 0)
        assert(f.components == 0 && f.count == 0)
        assert(f.labels[0] == 0 && f.members[0] == 0 && f.boundaries[0] == 0 && f.boundaries[1] == 0)
        // The ABI may not touch declared extra capacity beyond required ranges.
        assert(f.labels[1] == -1 && f.members[1] == -1 && f.boundaries[2] == -1)
    }
    fmt.println("ODIN_OWNERSHIP_TEST_PASS cases=22")
}
