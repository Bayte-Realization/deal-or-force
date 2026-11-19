// SPDX-License-Identifier: GPL-3.0-only
mod graph;
mod raster;
mod raster_blur;
mod trade;

pub use graph::{DofGraphScratch, DofProvinceGraph};
pub use raster::DofRasterBake;
pub use trade::DofTradeArena;

#[no_mangle]
pub extern "C" fn dof_kernels_abi_version() -> u32 {
    1
}

#[no_mangle]
pub extern "C" fn dof_kernels_simd_level() -> u32 {
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    {
        if std::arch::is_x86_feature_detected!("avx2") {
            return 2;
        }
        return 1;
    }
    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64")))]
    {
        0
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_graph_create_v1(
    province_count: u32,
    csr_offsets: *const u32,
    csr_edges: *const u32,
    edge_km: *const f32,
    edge_count: u32,
    land_offsets: *const u32,
    land_edges: *const u32,
    land_edge_count: u32,
    water: *const u8,
    coastal: *const u8,
    harbour_basin: *const i32,
    prov_lat: *const f32,
    prov_lon: *const f32,
) -> *mut DofProvinceGraph {
    match DofProvinceGraph::from_raw(
        province_count,
        csr_offsets,
        csr_edges,
        edge_km,
        edge_count,
        land_offsets,
        land_edges,
        land_edge_count,
        water,
        coastal,
        harbour_basin,
        prov_lat,
        prov_lon,
    ) {
        Some(g) => Box::into_raw(Box::new(g)),
        None => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_graph_destroy_v1(graph: *mut DofProvinceGraph) {
    if !graph.is_null() {
        drop(Box::from_raw(graph));
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_graph_scratch_create_v1(province_count: u32) -> *mut DofGraphScratch {
    match DofGraphScratch::new(province_count as usize) {
        Some(s) => Box::into_raw(Box::new(s)),
        None => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_graph_scratch_destroy_v1(scratch: *mut DofGraphScratch) {
    if !scratch.is_null() {
        drop(Box::from_raw(scratch));
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_graph_distance_field_v1(
    graph: *const DofProvinceGraph,
    scratch: *mut DofGraphScratch,
    sources: *const i32,
    source_count: u32,
    bound_km: f32,
    out_provinces: *mut i32,
    out_distances: *mut f32,
    out_capacity: u32,
    out_count: *mut u32,
) -> i32 {
    if graph.is_null() || scratch.is_null() || out_count.is_null() {
        return -1;
    }
    let sc = source_count as usize;
    let cap = out_capacity as usize;
    if (sc > 0 && sources.is_null()) || (cap > 0 && (out_provinces.is_null() || out_distances.is_null())) {
        return -1;
    }

    let g = &*graph;
    let s = &mut *scratch;
    let src_slice = if sc > 0 {
        std::slice::from_raw_parts(sources, sc)
    } else {
        &[]
    };
    let op = if cap > 0 {
        std::slice::from_raw_parts_mut(out_provinces, cap)
    } else {
        &mut []
    };
    let od = if cap > 0 {
        std::slice::from_raw_parts_mut(out_distances, cap)
    } else {
        &mut []
    };

    match graph::distance_field(g, s, src_slice, bound_km, op, od) {
        Ok(cnt) => {
            *out_count = cnt as u32;
            0
        }
        Err(code) => {
            *out_count = 0;
            code
        }
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_graph_solve_supply_v1(
    graph: *const DofProvinceGraph,
    scratch: *mut DofGraphScratch,
    controllers: *const u16,
    controller_count: u32,
    province_to_nation: *const u16,
    passable_bits: *const u64,
    passable_word_count: u32,
    own_map_index: u16,
    province_to_state: *const u16,
    roots: *const i32,
    root_count: u32,
    territory: *const i32,
    territory_count: u32,
    barracks_state_bits: *const u64,
    barracks_word_count: u32,
    land_state_ids: *const u16,
    land_state_counts: *const i32,
    land_state_len: u32,
    held_state_ids: *const u16,
    held_state_counts: *const i32,
    held_state_len: u32,
    insurgent: u8,
    forward_depot_held_share: f32,
    army_provinces: *const i32,
    army_count: u32,
    out_army_flags: *mut u8,
    out_army_distance: *mut i32,
    out_army_source_state: *mut i32,
) -> i32 {
    if graph.is_null()
        || scratch.is_null()
        || controllers.is_null()
        || province_to_nation.is_null()
        || province_to_state.is_null()
    {
        return -1;
    }
    let cc = controller_count as usize;
    let pwc = passable_word_count as usize;
    let rc = root_count as usize;
    let tc = territory_count as usize;
    let bwc = barracks_word_count as usize;
    let lsl = land_state_len as usize;
    let hsl = held_state_len as usize;
    let ac = army_count as usize;

    if (pwc > 0 && passable_bits.is_null())
        || (rc > 0 && roots.is_null())
        || (tc > 0 && territory.is_null())
        || (bwc > 0 && barracks_state_bits.is_null())
        || (lsl > 0 && (land_state_ids.is_null() || land_state_counts.is_null()))
        || (hsl > 0 && (held_state_ids.is_null() || held_state_counts.is_null()))
        || (ac > 0
            && (army_provinces.is_null()
                || out_army_flags.is_null()
                || out_army_distance.is_null()
                || out_army_source_state.is_null()))
    {
        return -1;
    }

    let g = &*graph;
    let s = &mut *scratch;
    let ctrl = std::slice::from_raw_parts(controllers, cc);
    let ptn = std::slice::from_raw_parts(province_to_nation, cc);
    let pts = std::slice::from_raw_parts(province_to_state, cc);
    let pb = if pwc > 0 { std::slice::from_raw_parts(passable_bits, pwc) } else { &[] };
    let rs = if rc > 0 { std::slice::from_raw_parts(roots, rc) } else { &[] };
    let ts = if tc > 0 { std::slice::from_raw_parts(territory, tc) } else { &[] };
    let bs = if bwc > 0 { std::slice::from_raw_parts(barracks_state_bits, bwc) } else { &[] };
    let lsi = if lsl > 0 { std::slice::from_raw_parts(land_state_ids, lsl) } else { &[] };
    let lsc = if lsl > 0 { std::slice::from_raw_parts(land_state_counts, lsl) } else { &[] };
    let hsi = if hsl > 0 { std::slice::from_raw_parts(held_state_ids, hsl) } else { &[] };
    let hsc = if hsl > 0 { std::slice::from_raw_parts(held_state_counts, hsl) } else { &[] };
    let ap = if ac > 0 { std::slice::from_raw_parts(army_provinces, ac) } else { &[] };
    let oaf = if ac > 0 { std::slice::from_raw_parts_mut(out_army_flags, ac) } else { &mut [] };
    let oad = if ac > 0 { std::slice::from_raw_parts_mut(out_army_distance, ac) } else { &mut [] };
    let oas = if ac > 0 { std::slice::from_raw_parts_mut(out_army_source_state, ac) } else { &mut [] };

    match graph::solve_supply(
        g,
        s,
        ctrl,
        ptn,
        pb,
        own_map_index,
        pts,
        rs,
        ts,
        bs,
        lsi,
        lsc,
        hsi,
        hsc,
        insurgent != 0,
        forward_depot_held_share,
        ap,
        oaf,
        oad,
        oas,
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_graph_owner_components_v1(
    graph: *const DofProvinceGraph,
    scratch: *mut DofGraphScratch,
    owners: *const u16,
    owner_count: u32,
    out_labels: *mut i32,
    out_members: *mut i32,
    out_component_offsets: *mut i32,
    out_component_count: *mut i32,
    out_member_count: *mut i32,
) -> i32 {
    if graph.is_null()
        || scratch.is_null()
        || owners.is_null()
        || out_labels.is_null()
        || out_members.is_null()
        || out_component_offsets.is_null()
        || out_component_count.is_null()
        || out_member_count.is_null()
    {
        return -1;
    }

    let g = &*graph;
    let s = &mut *scratch;
    let oc = owner_count as usize;
    let ow = std::slice::from_raw_parts(owners, oc);
    let ol = std::slice::from_raw_parts_mut(out_labels, g.province_count);
    let om = std::slice::from_raw_parts_mut(out_members, g.province_count);
    let oco = std::slice::from_raw_parts_mut(out_component_offsets, g.province_count + 1);

    match graph::owner_components(g, s, ow, ol, om, oco) {
        Ok((cc, mc)) => {
            *out_component_count = cc;
            *out_member_count = mc;
            0
        }
        Err(code) => {
            *out_component_count = 0;
            *out_member_count = 0;
            code
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_raster_create_v1(
    width: u32,
    height: u32,
    authored_holders: *const u16,
    cell_to_province: *const u16,
) -> *mut DofRasterBake {
    match DofRasterBake::from_raw(width, height, authored_holders, cell_to_province) {
        Some(r) => Box::into_raw(Box::new(r)),
        None => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_raster_destroy_v1(bake: *mut DofRasterBake) {
    if !bake.is_null() {
        drop(Box::from_raw(bake));
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_raster_bake_frontier_v1(
    bake: *mut DofRasterBake,
    owners: *const u16,
    owners_len: u32,
    authored_owners: *const u16,
    authored_owners_len: u32,
    cap: f32,
    smoothing: f32,
    threads: u32,
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

    match b.bake_frontier(ow, aow, cap, smoothing, threads, ob) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_trade_create_v1(max_slots: u32) -> *mut DofTradeArena {
    match DofTradeArena::new(max_slots as usize) {
        Some(a) => Box::into_raw(Box::new(a)),
        None => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_trade_destroy_v1(arena: *mut DofTradeArena) {
    if !arena.is_null() {
        drop(Box::from_raw(arena));
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_trade_update_distances_v1(
    arena: *mut DofTradeArena,
    nation_ids: *const i32,
    lats: *const f32,
    lons: *const f32,
    nation_count: u32,
    dirty_indices: *const i32,
    dirty_count: u32,
    full_rebuild: u32,
    stride: u32,
    out_dist_table: *mut f32,
    out_dist_len: u32,
) -> i32 {
    if arena.is_null() || out_dist_table.is_null() {
        return -1;
    }
    let nc = nation_count as usize;
    let dc = dirty_count as usize;
    if (nc > 0 && (nation_ids.is_null() || lats.is_null() || lons.is_null()))
        || (dc > 0 && dirty_indices.is_null())
    {
        return -1;
    }

    let ar = &mut *arena;
    let ids = if nc > 0 { std::slice::from_raw_parts(nation_ids, nc) } else { &[] };
    let la = if nc > 0 { std::slice::from_raw_parts(lats, nc) } else { &[] };
    let lo = if nc > 0 { std::slice::from_raw_parts(lons, nc) } else { &[] };
    let di = if dc > 0 { std::slice::from_raw_parts(dirty_indices, dc) } else { &[] };
    let dt = std::slice::from_raw_parts_mut(out_dist_table, out_dist_len as usize);

    match trade::update_distances(ar, ids, la, lo, di, full_rebuild != 0, stride as usize, dt) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_trade_compute_affinity_v1(
    arena: *mut DofTradeArena,
    buyer_ids: *const i32,
    buyer_map_idx: *const u16,
    buyer_reach: *const f32,
    buyer_count: u32,
    seller_ids: *const i32,
    seller_map_idx: *const u16,
    seller_reach: *const f32,
    seller_count: u32,
    dist_table: *const f32,
    dist_stride: u32,
    dist_nation_count: u32,
    pair_opinion: *const f32,
    pair_stance: *const u8,
    pair_flags: *const u8,
    pair_stride: u32,
    half_reach_km: f32,
    customs_inside: f32,
    customs_outside: f32,
    out_affinity: *mut f32,
    out_stride: u32,
) -> i32 {
    if arena.is_null() || out_affinity.is_null() {
        return -1;
    }
    let bc = buyer_count as usize;
    let sc = seller_count as usize;
    if (bc > 0 && (buyer_ids.is_null() || buyer_map_idx.is_null() || buyer_reach.is_null()))
        || (sc > 0 && (seller_ids.is_null() || seller_map_idx.is_null() || seller_reach.is_null()))
    {
        return -1;
    }
    if bc > 0 && sc > 0 && (pair_opinion.is_null() || pair_stance.is_null() || pair_flags.is_null()) {
        return -1;
    }

    let ar = &mut *arena;
    let bi = if bc > 0 { std::slice::from_raw_parts(buyer_ids, bc) } else { &[] };
    let bm = if bc > 0 { std::slice::from_raw_parts(buyer_map_idx, bc) } else { &[] };
    let br = if bc > 0 { std::slice::from_raw_parts(buyer_reach, bc) } else { &[] };
    let si = if sc > 0 { std::slice::from_raw_parts(seller_ids, sc) } else { &[] };
    let sm = if sc > 0 { std::slice::from_raw_parts(seller_map_idx, sc) } else { &[] };
    let sr = if sc > 0 { std::slice::from_raw_parts(seller_reach, sc) } else { &[] };

    let dt = if !dist_table.is_null() {
        let len = (dist_stride as usize).saturating_mul(dist_nation_count as usize);
        std::slice::from_raw_parts(dist_table, len)
    } else {
        &[]
    };

    let ps = pair_stride as usize;
    let pair_len = bc.saturating_mul(ps);
    let po = if pair_len > 0 { std::slice::from_raw_parts(pair_opinion, pair_len) } else { &[] };
    let pst = if pair_len > 0 { std::slice::from_raw_parts(pair_stance, pair_len) } else { &[] };
    let pf = if pair_len > 0 { std::slice::from_raw_parts(pair_flags, pair_len) } else { &[] };

    let os = out_stride as usize;
    let out_len = bc.saturating_mul(os);
    let oa = if out_len > 0 { std::slice::from_raw_parts_mut(out_affinity, out_len) } else { &mut [] };

    match trade::compute_affinity(
        ar,
        bi,
        bm,
        br,
        si,
        sm,
        sr,
        dt,
        dist_stride as usize,
        dist_nation_count as usize,
        po,
        pst,
        pf,
        ps,
        half_reach_km,
        customs_inside,
        customs_outside,
        oa,
        os,
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_and_null_guards() {
        assert_eq!(dof_kernels_abi_version(), 1);
        unsafe {
            assert!(dof_graph_create_v1(
                0,
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                0,
                std::ptr::null(),
                std::ptr::null(),
                0,
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null()
            )
            .is_null());
            dof_graph_destroy_v1(std::ptr::null_mut());
            dof_graph_scratch_destroy_v1(std::ptr::null_mut());
            dof_raster_destroy_v1(std::ptr::null_mut());
            dof_trade_destroy_v1(std::ptr::null_mut());
            let mut cnt = 99u32;
            assert_eq!(
                dof_graph_distance_field_v1(
                    std::ptr::null(),
                    std::ptr::null_mut(),
                    std::ptr::null(),
                    0,
                    100.0,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    0,
                    &mut cnt
                ),
                -1
            );
        }
    }

    #[test]
    fn parallel_chamfer_matches_sequential_exact() {
        let w = 640usize;
        let h = 320usize;
        let mut holders = vec![1u16; w * h];
        for y in 0..h {
            for x in 0..w {
                if ((x * 7 + y * 13) % 97) == 0 || (x > 250 && x < 310 && y > 100 && y < 210) {
                    holders[y * w + x] = 2;
                }
            }
        }
        unsafe {
            let bake_seq = dof_raster_create_v1(w as u32, h as u32, holders.as_ptr(), std::ptr::null());
            let bake_par = dof_raster_create_v1(w as u32, h as u32, holders.as_ptr(), std::ptr::null());
            assert!(!bake_seq.is_null() && !bake_par.is_null());

            let mut out_seq = vec![0u8; w * h];
            let mut out_par = vec![0u8; w * h];

            assert_eq!(
                dof_raster_bake_frontier_v1(
                    bake_seq,
                    std::ptr::null(),
                    0,
                    std::ptr::null(),
                    0,
                    20.0,
                    1.0,
                    1,
                    out_seq.as_mut_ptr(),
                    out_seq.len() as u32
                ),
                0
            );
            assert_eq!(
                dof_raster_bake_frontier_v1(
                    bake_par,
                    std::ptr::null(),
                    0,
                    std::ptr::null(),
                    0,
                    20.0,
                    1.0,
                    4,
                    out_par.as_mut_ptr(),
                    out_par.len() as u32
                ),
                0
            );
            assert_eq!(out_seq, out_par);
            dof_raster_destroy_v1(bake_seq);
            dof_raster_destroy_v1(bake_par);
        }
    }

    #[test]
    fn distance_field_and_owner_components_roundtrip() {
        let offsets = [0u32, 0, 2, 3, 4];
        let edges = [2u32, 3, 3, 1];
        let edge_km = [10.0f32, 25.0, 5.0, 25.0];
        let water = [0u8, 0, 0, 0];
        unsafe {
            let g = dof_graph_create_v1(
                4,
                offsets.as_ptr(),
                edges.as_ptr(),
                edge_km.as_ptr(),
                4,
                std::ptr::null(),
                std::ptr::null(),
                0,
                water.as_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
            );
            let s = dof_graph_scratch_create_v1(4);
            assert!(!g.is_null() && !s.is_null());
            let src = [1i32];
            let mut op = [0i32; 4];
            let mut od = [0.0f32; 4];
            let mut count = 0u32;
            assert_eq!(
                dof_graph_distance_field_v1(
                    g,
                    s,
                    src.as_ptr(),
                    1,
                    20.0,
                    op.as_mut_ptr(),
                    od.as_mut_ptr(),
                    4,
                    &mut count
                ),
                0
            );
            assert_eq!(count, 3);
            assert_eq!(&op[..3], &[1, 2, 3]);
            assert_eq!(&od[..3], &[0.0, 10.0, 15.0]);

            let owners = [0u16, 7, 7, 7];
            let mut labels = [0i32; 4];
            let mut members = [0i32; 4];
            let mut coff = [0i32; 5];
            let mut cc = 0i32;
            let mut mc = 0i32;
            assert_eq!(
                dof_graph_owner_components_v1(
                    g,
                    s,
                    owners.as_ptr(),
                    4,
                    labels.as_mut_ptr(),
                    members.as_mut_ptr(),
                    coff.as_mut_ptr(),
                    &mut cc,
                    &mut mc
                ),
                0
            );
            assert_eq!(cc, 1);
            assert_eq!(mc, 3);
            assert_eq!(&labels[1..4], &[1, 1, 1]);

            dof_graph_scratch_destroy_v1(s);
            dof_graph_destroy_v1(g);
        }
    }
}
