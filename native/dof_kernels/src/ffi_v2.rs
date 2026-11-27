// SPDX-License-Identifier: GPL-3.0-only
use crate::graph::{self, DofGraphScratch, DofProvinceGraph};
use crate::political;
use crate::raster::DofRasterBake;
use crate::raster_incremental;

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_raster_paint_plate_rect_v1(
    width: u32,
    height: u32,
    source_holders: *const u16,
    source_provinces: *const u16,
    source_geography: *const u8,
    owners: *const u16,
    owners_len: u32,
    authored_owners: *const u16,
    authored_owners_len: u32,
    palette_rgba: *const u32,
    palette_len: u32,
    country_shade: f32,
    region_shade: f32,
    inland_shade: f32,
    rect_x0: u32,
    rect_y0: u32,
    rect_x1: u32,
    rect_y1: u32,
    out_pixels_rgba: *mut u8,
    out_pixels_len: u32,
) -> i32 {
    if source_holders.is_null()
        || source_provinces.is_null()
        || source_geography.is_null()
        || out_pixels_rgba.is_null()
        || (owners_len > 0 && owners.is_null())
        || (authored_owners_len > 0 && authored_owners.is_null())
        || (palette_len > 0 && palette_rgba.is_null())
    {
        return -1;
    }
    let w = width as usize;
    let h = height as usize;
    let cells = match w.checked_mul(h) {
        Some(c) if c > 0 && c <= 64_000_000 => c,
        _ => return -2,
    };
    let sh = std::slice::from_raw_parts(source_holders, cells);
    let sp = std::slice::from_raw_parts(source_provinces, cells);
    let sg = std::slice::from_raw_parts(source_geography, cells);
    let ow = if !owners.is_null() && owners_len > 0 {
        std::slice::from_raw_parts(owners, owners_len as usize)
    } else {
        &[]
    };
    let aow = if !authored_owners.is_null() && authored_owners_len > 0 {
        std::slice::from_raw_parts(authored_owners, authored_owners_len as usize)
    } else {
        &[]
    };
    let pal = if !palette_rgba.is_null() && palette_len > 0 {
        std::slice::from_raw_parts(palette_rgba, palette_len as usize)
    } else {
        &[]
    };
    let out = std::slice::from_raw_parts_mut(out_pixels_rgba, out_pixels_len as usize);

    match raster_incremental::paint_political_plate_rect(
        w,
        h,
        sh,
        sp,
        sg,
        ow,
        aow,
        pal,
        country_shade,
        region_shade,
        inland_shade,
        rect_x0 as usize,
        rect_y0 as usize,
        rect_x1 as usize,
        rect_y1 as usize,
        out,
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_raster_bake_frontier_rect_v1(
    bake: *mut DofRasterBake,
    owners: *const u16,
    owners_len: u32,
    authored_owners: *const u16,
    authored_owners_len: u32,
    cap: f32,
    smoothing: f32,
    rect_x0: u32,
    rect_y0: u32,
    rect_x1: u32,
    rect_y1: u32,
    out_bytes: *mut u8,
    out_len: u32,
) -> i32 {
    if bake.is_null() || out_bytes.is_null() {
        return -1;
    }
    let b = &mut *bake;
    let ow = if !owners.is_null() && owners_len > 0 {
        std::slice::from_raw_parts(owners, owners_len as usize)
    } else {
        &[]
    };
    let aow = if !authored_owners.is_null() && authored_owners_len > 0 {
        std::slice::from_raw_parts(authored_owners, authored_owners_len as usize)
    } else {
        &[]
    };
    let ob = std::slice::from_raw_parts_mut(out_bytes, out_len as usize);

    match b.bake_frontier_rect(
        ow,
        aow,
        cap,
        smoothing,
        rect_x0 as usize,
        rect_y0 as usize,
        rect_x1 as usize,
        rect_y1 as usize,
        ob,
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_raster_mipmap_rgba8_rect_v1(
    width: u32,
    height: u32,
    rect_x0: u32,
    rect_y0: u32,
    rect_x1: u32,
    rect_y1: u32,
    mip_buffer: *mut u8,
    mip_len: u32,
) -> i32 {
    if mip_buffer.is_null() {
        return -1;
    }
    let buf = std::slice::from_raw_parts_mut(mip_buffer, mip_len as usize);
    match raster_incremental::mipmap_rgba8_rect(
        width as usize,
        height as usize,
        rect_x0 as usize,
        rect_y0 as usize,
        rect_x1 as usize,
        rect_y1 as usize,
        buf,
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_raster_mipmap_r8_rect_v1(
    width: u32,
    height: u32,
    rect_x0: u32,
    rect_y0: u32,
    rect_x1: u32,
    rect_y1: u32,
    mip_buffer: *mut u8,
    mip_len: u32,
) -> i32 {
    if mip_buffer.is_null() {
        return -1;
    }
    let buf = std::slice::from_raw_parts_mut(mip_buffer, mip_len as usize);
    match raster_incremental::mipmap_r8_rect(
        width as usize,
        height as usize,
        rect_x0 as usize,
        rect_y0 as usize,
        rect_x1 as usize,
        rect_y1 as usize,
        buf,
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_graph_border_pairs_v1(
    graph: *const DofProvinceGraph,
    scratch: *mut DofGraphScratch,
    controllers: *const u16,
    controller_count: u32,
    out_pairs: *mut u32,
    out_capacity: u32,
    out_count: *mut u32,
) -> i32 {
    if !out_count.is_null() {
        *out_count = 0;
    }
    if graph.is_null() || scratch.is_null() || controllers.is_null() || out_count.is_null() {
        return -1;
    }
    let cap = out_capacity as usize;
    if cap > 0 && out_pairs.is_null() {
        return -1;
    }
    let g = &*graph;
    let s = &mut *scratch;
    let ctrl = std::slice::from_raw_parts(controllers, controller_count as usize);
    let op = if cap > 0 {
        std::slice::from_raw_parts_mut(out_pairs, cap)
    } else {
        &mut []
    };
    match graph::border_pairs(g, s, ctrl, op) {
        Ok(cnt) => {
            *out_count = cnt as u32;
            0
        }
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_graph_occupation_census_v1(
    scratch: *mut DofGraphScratch,
    province_to_nation: *const u16,
    controllers: *const u16,
    array_len: u32,
    province_count: u32,
    out_home_keys: *mut u16,
    out_home_counts: *mut i32,
    home_capacity: u32,
    out_home_len: *mut u32,
    out_occ_pairs: *mut u32,
    out_occ_counts: *mut i32,
    occ_capacity: u32,
    out_occ_len: *mut u32,
) -> i32 {
    if !out_home_len.is_null() {
        *out_home_len = 0;
    }
    if !out_occ_len.is_null() {
        *out_occ_len = 0;
    }
    if scratch.is_null()
        || province_to_nation.is_null()
        || controllers.is_null()
        || out_home_len.is_null()
        || out_occ_len.is_null()
    {
        return -1;
    }
    let hcap = home_capacity as usize;
    let ocap = occ_capacity as usize;
    if (hcap > 0 && (out_home_keys.is_null() || out_home_counts.is_null()))
        || (ocap > 0 && (out_occ_pairs.is_null() || out_occ_counts.is_null()))
    {
        return -1;
    }
    let s = &mut *scratch;
    let ptn = std::slice::from_raw_parts(province_to_nation, array_len as usize);
    let ctrl = std::slice::from_raw_parts(controllers, array_len as usize);
    let hk = if hcap > 0 { std::slice::from_raw_parts_mut(out_home_keys, hcap) } else { &mut [] };
    let hc = if hcap > 0 { std::slice::from_raw_parts_mut(out_home_counts, hcap) } else { &mut [] };
    let op = if ocap > 0 { std::slice::from_raw_parts_mut(out_occ_pairs, ocap) } else { &mut [] };
    let oc = if ocap > 0 { std::slice::from_raw_parts_mut(out_occ_counts, ocap) } else { &mut [] };

    match graph::occupation_census(s, ptn, ctrl, province_count as usize, hk, hc, op, oc) {
        Ok((hl, ol)) => {
            *out_home_len = hl as u32;
            *out_occ_len = ol as u32;
            0
        }
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_graph_flood_vision_v1(
    graph: *const DofProvinceGraph,
    scratch: *mut DofGraphScratch,
    reach: *mut u8,
    reach_len: u32,
    seeds: *const i32,
    seed_depths: *const u8,
    seed_count: u32,
) -> i32 {
    if graph.is_null() || scratch.is_null() || reach.is_null() {
        return -1;
    }
    let sc = seed_count as usize;
    if sc > 0 && (seeds.is_null() || seed_depths.is_null()) {
        return -1;
    }
    let g = &*graph;
    let s = &mut *scratch;
    let r = std::slice::from_raw_parts_mut(reach, reach_len as usize);
    let sd = if sc > 0 { std::slice::from_raw_parts(seeds, sc) } else { &[] };
    let dp = if sc > 0 { std::slice::from_raw_parts(seed_depths, sc) } else { &[] };
    match graph::flood_vision(g, s, r, sd, dp) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_graph_seed_territory_vision_v1(
    graph: *const DofProvinceGraph,
    scratch: *mut DofGraphScratch,
    owned_provinces: *const i32,
    owned_count: u32,
    border_depth: u8,
    reach: *mut u8,
    reach_len: u32,
) -> i32 {
    if graph.is_null() || scratch.is_null() || reach.is_null() {
        return -1;
    }
    let oc = owned_count as usize;
    if oc > 0 && owned_provinces.is_null() {
        return -1;
    }
    let g = &*graph;
    let s = &mut *scratch;
    let ow = if oc > 0 { std::slice::from_raw_parts(owned_provinces, oc) } else { &[] };
    let r = std::slice::from_raw_parts_mut(reach, reach_len as usize);
    match graph::seed_territory_vision(g, s, ow, border_depth, r) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_political_cohorts_drift_v1(
    count: u32,
    flags: *const u8,
    step: *const f32,
    bloc_step: *const f32,
    shortage: *const f32,
    stability: *const f32,
    national_living: *const f32,
    alienation: *const f32,
    mil: *const f32,
    tax_stress: *const f32,
    market_access: *const f32,
    literacy: *const f32,
    urbanization: *const f32,
    sol: *const f32,
    radicalism: *const f32,
    law_approval: *mut f32,
    gov_approval: *mut f32,
    pol_divergence: *mut f32,
    blocs: *mut f32,
) -> i32 {
    let n = count as usize;
    if n == 0 {
        return 0;
    }
    if flags.is_null()
        || step.is_null()
        || bloc_step.is_null()
        || shortage.is_null()
        || stability.is_null()
        || national_living.is_null()
        || alienation.is_null()
        || mil.is_null()
        || tax_stress.is_null()
        || market_access.is_null()
        || literacy.is_null()
        || urbanization.is_null()
        || sol.is_null()
        || radicalism.is_null()
        || law_approval.is_null()
        || gov_approval.is_null()
        || pol_divergence.is_null()
        || blocs.is_null()
    {
        return -1;
    }
    match political::drift_cohorts_batch(
        n,
        std::slice::from_raw_parts(flags, n),
        std::slice::from_raw_parts(step, n),
        std::slice::from_raw_parts(bloc_step, n),
        std::slice::from_raw_parts(shortage, n),
        std::slice::from_raw_parts(stability, n),
        std::slice::from_raw_parts(national_living, n),
        std::slice::from_raw_parts(alienation, n),
        std::slice::from_raw_parts(mil, n),
        std::slice::from_raw_parts(tax_stress, n),
        std::slice::from_raw_parts(market_access, n),
        std::slice::from_raw_parts(literacy, n),
        std::slice::from_raw_parts(urbanization, n),
        std::slice::from_raw_parts(sol, n),
        std::slice::from_raw_parts(radicalism, n),
        std::slice::from_raw_parts_mut(law_approval, n),
        std::slice::from_raw_parts_mut(gov_approval, n),
        std::slice::from_raw_parts_mut(pol_divergence, n),
        std::slice::from_raw_parts_mut(blocs, n * 5),
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{dof_graph_create_v1, dof_graph_destroy_v1, dof_graph_scratch_create_v1, dof_graph_scratch_destroy_v1};

    #[test]
    fn border_pairs_occupation_and_vision_roundtrip() {
        // 5 vertices (0 unused, 1-2-3-4 chain)
        let offsets = [0u32, 0, 1, 3, 5, 6];
        let edges = [2u32, 1, 3, 2, 4, 3];
        let edge_km = [10.0f32; 6];
        let water = [0u8; 5];
        unsafe {
            let g = dof_graph_create_v1(
                5,
                offsets.as_ptr(),
                edges.as_ptr(),
                edge_km.as_ptr(),
                6,
                std::ptr::null(),
                std::ptr::null(),
                0,
                water.as_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
            );
            let s = dof_graph_scratch_create_v1(5);
            assert!(!g.is_null() && !s.is_null());

            // Controllers: 1=A(10), 2=A(10), 3=B(20), 4=C(30)
            let orig = [0u16, 10, 10, 20, 20];
            let ctrl = [0u16, 10, 10, 20, 30];
            let mut pairs = [0u32; 16];
            let mut pcnt = 0u32;
            assert_eq!(dof_graph_border_pairs_v1(g, s, ctrl.as_ptr(), 5, pairs.as_mut_ptr(), 16, &mut pcnt), 0);
            assert_eq!(pcnt, 4); // (10,20), (20,10), (20,30), (30,20)
            assert_eq!(&pairs[..4], &[(10 << 16) | 20, (20 << 16) | 10, (20 << 16) | 30, (30 << 16) | 20]);

            let mut hk = [0u16; 8];
            let mut hc = [0i32; 8];
            let mut hlen = 0u32;
            let mut op = [0u32; 8];
            let mut oc = [0i32; 8];
            let mut olen = 0u32;
            assert_eq!(
                dof_graph_occupation_census_v1(
                    s, orig.as_ptr(), ctrl.as_ptr(), 5, 4,
                    hk.as_mut_ptr(), hc.as_mut_ptr(), 8, &mut hlen,
                    op.as_mut_ptr(), oc.as_mut_ptr(), 8, &mut olen
                ),
                0
            );
            assert_eq!(hlen, 2);
            assert_eq!(&hk[..2], &[10, 20]);
            assert_eq!(&hc[..2], &[2, 2]);
            assert_eq!(olen, 1);
            assert_eq!(op[0], (20 << 16) | 30);
            assert_eq!(oc[0], 1);

            // Territory vision: owned [1, 2], border_depth 1 -> reaches 1,2 (lvl 2) and 3 (lvl 1), not 4 (0)
            let owned = [1i32, 2];
            let mut reach = [0u8; 5];
            assert_eq!(dof_graph_seed_territory_vision_v1(g, s, owned.as_ptr(), 2, 1, reach.as_mut_ptr(), 5), 0);
            assert_eq!(reach, [0, 2, 2, 1, 0]);

            // Flood vision from army at 3 with depth 1 -> raises 3 to 2 and reveals 4 at 1
            let seeds = [3i32];
            let depths = [1u8];
            assert_eq!(dof_graph_flood_vision_v1(g, s, reach.as_mut_ptr(), 5, seeds.as_ptr(), depths.as_ptr(), 1), 0);
            assert_eq!(reach, [0, 2, 2, 2, 1]);

            dof_graph_scratch_destroy_v1(s);
            dof_graph_destroy_v1(g);
        }
    }
}
