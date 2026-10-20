// SPDX-License-Identifier: GPL-3.0-only
// Deal or Force ownership component kernel. See ../../../LICENSE.

use std::mem::{align_of, size_of};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::slice;

const MAX_VERTICES: usize = 1_000_000;
const MAX_NEIGHBORS: usize = 16_000_000;
const INVALID: i32 = 1;
const CAPACITY: i32 = 2;
const PANIC: i32 = 3;

#[derive(Clone, Copy)]
struct Region {
    start: usize,
    end: usize,
}

impl Region {
    fn overlaps(self, other: Self) -> bool {
        self.start < other.end && other.start < self.end
    }
}

// This checks pointer shape, not whether the caller actually owns mapped memory.
fn region<T>(pointer: *const T, length: usize) -> Result<Region, i32> {
    if length == 0 {
        // The sole empty ABI input is neighbors, whose null pointer is legal.
        if !pointer.is_null() && (pointer as usize) % align_of::<T>() != 0 {
            return Err(INVALID);
        }
        return Ok(Region { start: 0, end: 0 });
    }
    let start = pointer as usize;
    if start == 0 || start % align_of::<T>() != 0 {
        return Err(INVALID);
    }
    let bytes = length.checked_mul(size_of::<T>()).ok_or(INVALID)?;
    if bytes > isize::MAX as usize {
        return Err(INVALID);
    }
    let end = start.checked_add(bytes).ok_or(INVALID)?;
    Ok(Region { start, end })
}

#[no_mangle]
pub extern "C" fn dof_graph_abi_version() -> u32 {
    1
}

/// Labels each same-owner land flood in ascending start order, using CSR neighbor order.
///
/// # Safety
/// Nonempty pointers must refer to readable/writable live allocations of the declared
/// lengths. Inputs must stay unchanged until return. Output regions/counts must not
/// alias any inputs or each other. Pointer shape and aliases are checked before use,
/// but allocation validity and concurrent access cannot be established by this ABI.
#[no_mangle]
pub unsafe extern "C" fn dof_owner_components_v1(
    offsets: *const i32,
    offsets_count: u32,
    neighbors: *const i32,
    neighbor_count: u32,
    owners: *const u16,
    vertex_count: u32,
    water: *const u8,
    water_count: u32,
    labels: *mut i32,
    labels_capacity: u32,
    member_order: *mut i32,
    member_capacity: u32,
    component_offsets: *mut i32,
    component_offsets_capacity: u32,
    out_component_count: *mut i32,
    out_member_count: *mut i32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: validation and the public caller contract establish each slice below.
        unsafe {
            run(
                offsets,
                offsets_count,
                neighbors,
                neighbor_count,
                owners,
                vertex_count,
                water,
                water_count,
                labels,
                labels_capacity,
                member_order,
                member_capacity,
                component_offsets,
                component_offsets_capacity,
                out_component_count,
                out_member_count,
            )
        }
    }))
    .unwrap_or(PANIC)
}

#[allow(clippy::too_many_arguments)]
unsafe fn run(
    offsets: *const i32,
    offsets_count: u32,
    neighbors: *const i32,
    neighbor_count: u32,
    owners: *const u16,
    vertex_count: u32,
    water: *const u8,
    water_count: u32,
    labels: *mut i32,
    labels_capacity: u32,
    member_order: *mut i32,
    member_capacity: u32,
    component_offsets: *mut i32,
    component_offsets_capacity: u32,
    out_component_count: *mut i32,
    out_member_count: *mut i32,
) -> i32 {
    let v = vertex_count as usize;
    let e = neighbor_count as usize;
    if v == 0 || v > MAX_VERTICES || e > MAX_NEIGHBORS
        || offsets_count as usize != v + 1 || water_count as usize != v
    {
        return INVALID;
    }
    if (labels_capacity as usize) < v || (member_capacity as usize) < v
        || (component_offsets_capacity as usize) < v + 1
    {
        return CAPACITY;
    }

    let preflight = || -> Result<(), i32> {
        let inputs = [
            region(offsets, v + 1)?,
            region(neighbors, e)?,
            region(owners, v)?,
            region(water, v)?,
        ];
        let outputs = [
            region(labels, v)?,
            region(member_order, v)?,
            region(component_offsets, v + 1)?,
            region(out_component_count, 1)?,
            region(out_member_count, 1)?,
        ];
        for (index, output) in outputs.iter().enumerate() {
            if inputs.iter().any(|input| output.overlaps(*input))
                || outputs[..index].iter().any(|other| output.overlaps(*other))
            {
                return Err(INVALID);
            }
        }
        Ok(())
    };
    if let Err(status) = preflight() {
        return status;
    }

    // SAFETY: all ranges are nonnull/aligned, bounded, and the caller owns them.
    let offsets = unsafe { slice::from_raw_parts(offsets, v + 1) };
    let neighbors = if e == 0 { &[] } else { unsafe { slice::from_raw_parts(neighbors, e) } };
    let owners = unsafe { slice::from_raw_parts(owners, v) };
    let water = unsafe { slice::from_raw_parts(water, v) };
    if offsets[0] != 0 || offsets[v] != e as i32
        || offsets.windows(2).any(|pair| pair[0] < 0 || pair[0] > pair[1])
        || neighbors.iter().any(|&next| next < 0 || next as usize >= v)
        || water.iter().any(|&value| value > 1)
    {
        return INVALID;
    }

    // No output has been written before this point. The output member array is
    // also the FIFO, so a successful call needs no native heap allocations.
    let labels = unsafe { slice::from_raw_parts_mut(labels, v) };
    let members = unsafe { slice::from_raw_parts_mut(member_order, v) };
    let boundaries = unsafe { slice::from_raw_parts_mut(component_offsets, v + 1) };
    labels.fill(0);
    members.fill(0);
    boundaries.fill(0);
    let mut component_count = 0;
    let mut member_count = 0;
    for start in 1..v {
        if labels[start] != 0 || owners[start] == 0 || water[start] != 0 {
            continue;
        }
        component_count += 1;
        let owner = owners[start];
        let mut head = member_count;
        labels[start] = component_count as i32;
        members[member_count] = start as i32;
        member_count += 1;
        while head < member_count {
            let current = members[head] as usize;
            head += 1;
            for &next in &neighbors[offsets[current] as usize..offsets[current + 1] as usize] {
                let next = next as usize;
                if next == 0 || labels[next] != 0 || owners[next] != owner || water[next] != 0 {
                    continue;
                }
                labels[next] = component_count as i32;
                members[member_count] = next as i32;
                member_count += 1;
            }
        }
        boundaries[component_count] = member_count as i32;
    }
    unsafe {
        out_component_count.write(component_count as i32);
        out_member_count.write(member_count as i32);
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Fixture {
        offsets: Vec<i32>,
        neighbors: Vec<i32>,
        owners: Vec<u16>,
        water: Vec<u8>,
        labels: Vec<i32>,
        members: Vec<i32>,
        boundaries: Vec<i32>,
        components: i32,
        count: i32,
    }

    impl Fixture {
        fn new(rows: &[&[i32]], owners: &[u16], water: &[u8]) -> Self {
            let mut offsets = vec![0];
            let mut neighbors = vec![];
            for row in rows {
                neighbors.extend_from_slice(row);
                offsets.push(neighbors.len() as i32);
            }
            Self {
                offsets, neighbors, owners: owners.to_vec(), water: water.to_vec(),
                labels: vec![-1; owners.len()], members: vec![-1; owners.len()],
                boundaries: vec![-1; owners.len() + 1], components: -1, count: -1,
            }
        }

        fn call(&mut self) -> i32 {
            unsafe {
                dof_owner_components_v1(
                    self.offsets.as_ptr(), self.offsets.len() as u32,
                    self.neighbors.as_ptr(), self.neighbors.len() as u32,
                    self.owners.as_ptr(), self.owners.len() as u32,
                    self.water.as_ptr(), self.water.len() as u32,
                    self.labels.as_mut_ptr(), self.labels.len() as u32,
                    self.members.as_mut_ptr(), self.members.len() as u32,
                    self.boundaries.as_mut_ptr(), self.boundaries.len() as u32,
                    &mut self.components, &mut self.count,
                )
            }
        }

        fn assert_untouched(&self) {
            assert!(self.labels.iter().all(|&x| x == -1));
            assert!(self.members.iter().all(|&x| x == -1));
            assert!(self.boundaries.iter().all(|&x| x == -1));
            assert_eq!((self.components, self.count), (-1, -1));
        }
    }

    fn sample() -> Fixture {
        Fixture::new(
            &[&[1], &[3, 2, 0, 3], &[1, 4], &[1], &[2], &[6], &[5], &[]],
            &[9, 1, 1, 1, 1, 65_535, 65_535, 0],
            &[0, 0, 0, 0, 1, 0, 0, 0],
        )
    }

    #[test]
    fn preserves_bfs_order_excludes_sentinel_water_and_unowned() {
        let mut f = sample();
        assert_eq!(dof_graph_abi_version(), 1);
        assert_eq!(f.call(), 0);
        assert_eq!(f.labels, [0, 1, 1, 1, 0, 2, 2, 0]);
        assert_eq!(f.members, [1, 3, 2, 5, 6, 0, 0, 0]);
        assert_eq!(f.boundaries, [0, 3, 5, 0, 0, 0, 0, 0, 0]);
        assert_eq!((f.components, f.count), (2, 5));
    }

    #[test]
    fn directed_edges_use_ascending_floods_not_strong_components() {
        let mut f = Fixture::new(&[&[], &[], &[1], &[4], &[]], &[0, 1, 1, 1, 1], &[0; 5]);
        assert_eq!(f.call(), 0);
        assert_eq!(f.labels, [0, 1, 2, 3, 3]);
        assert_eq!(&f.members[..4], [1, 2, 3, 4]);
        assert_eq!(&f.boundaries[..4], [0, 1, 2, 4]);
    }

    #[test]
    fn handles_empty_land_and_null_empty_neighbors() {
        let mut f = Fixture::new(&[&[]], &[0], &[1]);
        assert_eq!(f.call(), 0);
        assert_eq!((f.components, f.count), (0, 0));
        let status = unsafe {
            dof_owner_components_v1(
                f.offsets.as_ptr(), 2, std::ptr::null(), 0, f.owners.as_ptr(), 1,
                f.water.as_ptr(), 1, f.labels.as_mut_ptr(), 1, f.members.as_mut_ptr(), 1,
                f.boundaries.as_mut_ptr(), 2, &mut f.components, &mut f.count,
            )
        };
        assert_eq!(status, 0);
    }

    #[test]
    fn rejects_csr_and_water_before_writing() {
        for case in 0..7 {
            let mut f = sample();
            match case {
                0 => f.offsets[0] = 1,
                1 => f.offsets[2] = -1,
                2 => f.offsets[3] = 0,
                3 => *f.offsets.last_mut().unwrap() += 1,
                4 => f.neighbors[0] = -1,
                5 => f.neighbors[0] = f.owners.len() as i32,
                6 => f.water[0] = 2,
                _ => unreachable!(),
            }
            assert_eq!(f.call(), INVALID, "case {case}");
            f.assert_untouched();
        }
    }

    #[test]
    fn rejects_capacities_before_writing() {
        for case in 0..3 {
            let mut f = sample();
            match case {
                0 => { f.labels.pop(); }
                1 => { f.members.pop(); }
                2 => { f.boundaries.pop(); }
                _ => unreachable!(),
            }
            assert_eq!(f.call(), CAPACITY);
            f.assert_untouched();
        }
    }

    #[test]
    fn rejects_null_unaligned_aliases_overflow_and_counts() {
        for case in 0..10 {
            let mut f = sample();
            let mut offsets = f.offsets.as_ptr();
            let mut labels = f.labels.as_mut_ptr();
            let mut members = f.members.as_mut_ptr();
            let mut out_members = &mut f.count as *mut i32;
            let mut vertices = f.owners.len() as u32;
            let mut edges = f.neighbors.len() as u32;
            let mut water_count = vertices;
            match case {
                0 => offsets = std::ptr::null(),
                1 => labels = 1usize as *mut i32,
                2 => members = labels,
                3 => labels = offsets as *mut i32,
                4 => out_members = &mut f.components,
                5 => labels = (usize::MAX - 3) as *mut i32,
                6 => vertices = 0,
                7 => vertices = MAX_VERTICES as u32 + 1,
                8 => edges = MAX_NEIGHBORS as u32 + 1,
                9 => water_count -= 1,
                _ => unreachable!(),
            }
            let status = unsafe {
                dof_owner_components_v1(
                    offsets, f.offsets.len() as u32, f.neighbors.as_ptr(), edges,
                    f.owners.as_ptr(), vertices, f.water.as_ptr(), water_count,
                    labels, f.labels.len() as u32, members, f.members.len() as u32,
                    f.boundaries.as_mut_ptr(), f.boundaries.len() as u32,
                    &mut f.components, out_members,
                )
            };
            assert_eq!(status, INVALID, "case {case}");
            f.assert_untouched();
        }
    }

    #[test]
    fn repeated_calls_recompute_after_ownership_changes() {
        let mut f = sample();
        assert_eq!(f.call(), 0);
        let expected = (f.labels.clone(), f.members.clone(), f.boundaries.clone());
        assert_eq!(f.call(), 0);
        assert_eq!((&f.labels, &f.members, &f.boundaries), (&expected.0, &expected.1, &expected.2));
        f.owners[3] = 2;
        assert_eq!(f.call(), 0);
        assert_eq!((f.components, f.count), (3, 5));
    }
}
