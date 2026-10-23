// SPDX-License-Identifier: GPL-3.0-only
// Deliberately defective native libraries for managed failure/fallback tests only.
// These are a separate crate, never a production dependency or Cargo test target.

#[cfg(not(any(feature = "abi2", feature = "status3", feature = "malformed")))]
compile_error!("Select exactly one test fixture feature: abi2, status3, or malformed");
#[cfg(any(
    all(feature = "abi2", feature = "status3"),
    all(feature = "abi2", feature = "malformed"),
    all(feature = "status3", feature = "malformed")
))]
compile_error!("Test fixtures must be built separately with exactly one feature");

#[no_mangle]
pub extern "C" fn dof_graph_abi_version() -> u32 {
    if cfg!(feature = "abi2") { 2 } else { 1 }
}

/// # Safety
/// Test host supplies valid writable output allocations. These fixtures deliberately
/// violate result/status contracts to prove host rejection; never use for gameplay.
#[no_mangle]
pub unsafe extern "C" fn dof_owner_components_v1(
    _offsets: *const i32,
    _offsets_count: u32,
    _neighbors: *const i32,
    _neighbor_count: u32,
    _owners: *const u16,
    _vertex_count: u32,
    _water: *const u8,
    _water_count: u32,
    labels: *mut i32,
    labels_capacity: u32,
    member_order: *mut i32,
    member_capacity: u32,
    component_offsets: *mut i32,
    component_offsets_capacity: u32,
    out_component_count: *mut i32,
    out_member_count: *mut i32,
) -> i32 {
    if cfg!(feature = "abi2") {
        // The host must refuse this library before any computation is attempted.
        return 1;
    }
    // Corrupt the sentinel and first entries, but stay inside caller-owned buffers.
    // The host must discard all of this scratch on both failure paths.
    unsafe {
        if !labels.is_null() && labels_capacity > 0 { labels.write(99); }
        if !member_order.is_null() && member_capacity > 0 { member_order.write(-7); }
        if !component_offsets.is_null() && component_offsets_capacity > 0 { component_offsets.write(5); }
        if !out_component_count.is_null() { out_component_count.write(1); }
        if !out_member_count.is_null() { out_member_count.write(1); }
    }
    // status3 simulates a caught panic after partial writes; it does not actually
    // unwind. malformed claims success, but its sentinel and offsets are invalid.
    if cfg!(feature = "status3") { 3 } else { 0 }
}
