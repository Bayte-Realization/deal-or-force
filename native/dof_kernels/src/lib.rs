// SPDX-License-Identifier: GPL-3.0-only
mod diplomacy;
mod distance;
mod economy_demo;
mod graph;
mod market;
mod raster;
mod raster_blur;
mod simd;
mod trade;

pub use graph::{DofGraphScratch, DofProvinceGraph};
pub use raster::DofRasterBake;
pub use trade::DofTradeArena;

#[no_mangle]
pub extern "C" fn dof_kernels_abi_version() -> u32 {
    1
}

/// Returns active runtime SIMD ISA level:
///   0 = scalar/unknown
///   1 = x86 SSE2
///   2 = x86 AVX2
///   3 = ARM64 NEON
///   4 = ARM64 NEON + DotProd
#[no_mangle]
pub extern "C" fn dof_kernels_simd_level() -> u32 {
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    {
        if std::arch::is_x86_feature_detected!("avx2") {
            return 2;
        }
        return 1;
    }
    #[cfg(target_arch = "aarch64")]
    {
        if std::arch::is_aarch64_feature_detected!("neon") {
            if std::arch::is_aarch64_feature_detected!("dotprod") {
                return 4;
            }
            return 3;
        }
        return 0;
    }
    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    {
        0
    }
}

/// Returns capability bitmask:
///   bit 0 (1):  ABI v1 base (graph, raster, trade)
///   bit 1 (2):  diplomacy vector scoring kernels
///   bit 2 (4):  market clearing kernels
///   bit 3 (8):  distance / range prefilter kernels
///   bit 4 (16): x86 AVX2 detected
///   bit 5 (32):  ARM64 NEON detected
///   bit 6 (64):  render-prep raster pack & political plate paint
///   bit 7 (128): economy & demography batched kernels
#[no_mangle]
pub extern "C" fn dof_kernels_capabilities() -> u32 {
    let mut caps = 1u32 | 2u32 | 4u32 | 8u32 | 64u32 | 128u32;
    let simd = dof_kernels_simd_level();
    if simd == 2 {
        caps |= 16;
    } else if simd >= 3 {
        caps |= 32;
    }
    caps
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
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_raster_pack_overlays_v1(
    province_count: u32,
    owners: *const u16,
    owners_len: u32,
    painted_owners: *const u16,
    painted_owners_len: u32,
    controllers: *const u16,
    controllers_len: u32,
    palette_rgba: *const u32,
    palette_len: u32,
    occupied_alpha: u8,
    unowned_palette_index: u16,
    fallback_rgba: u32,
    out_control_rgba: *mut u8,
    out_control_len: u32,
    out_owner_rgba: *mut u8,
    out_owner_len: u32,
    out_owner_ids_rgba: *mut u8,
    out_owner_ids_len: u32,
    out_owners_moved: *mut i32,
) -> i32 {
    if !out_owners_moved.is_null() {
        *out_owners_moved = 0;
    }
    if out_control_rgba.is_null()
        || out_owner_rgba.is_null()
        || out_owner_ids_rgba.is_null()
        || out_owners_moved.is_null()
        || (owners_len > 0 && owners.is_null())
        || (controllers_len > 0 && controllers.is_null())
        || (palette_len > 0 && palette_rgba.is_null())
    {
        return -1;
    }

    let ow = if owners_len > 0 {
        std::slice::from_raw_parts(owners, owners_len as usize)
    } else {
        &[]
    };
    let pow = if !painted_owners.is_null() && painted_owners_len > 0 {
        std::slice::from_raw_parts(painted_owners, painted_owners_len as usize)
    } else {
        &[]
    };
    let ctl = if controllers_len > 0 {
        std::slice::from_raw_parts(controllers, controllers_len as usize)
    } else {
        &[]
    };
    let pal = if palette_len > 0 {
        std::slice::from_raw_parts(palette_rgba, palette_len as usize)
    } else {
        &[]
    };
    let out_ctl = std::slice::from_raw_parts_mut(out_control_rgba, out_control_len as usize);
    let out_own = std::slice::from_raw_parts_mut(out_owner_rgba, out_owner_len as usize);
    let out_oid = std::slice::from_raw_parts_mut(out_owner_ids_rgba, out_owner_ids_len as usize);

    match raster::pack_province_overlays(
        province_count as usize,
        ow,
        pow,
        ctl,
        pal,
        occupied_alpha,
        unowned_palette_index,
        fallback_rgba,
        out_ctl,
        out_own,
        out_oid,
    ) {
        Ok(moved) => {
            *out_owners_moved = if moved { 1 } else { 0 };
            0
        }
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_raster_paint_plate_v1(
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
    threads: u32,
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

    match raster::paint_political_plate(
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
        threads,
        out,
    ) {
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

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_dip_pair_score_batch_v1(
    pair_count: u32,
    left_econ: *const f32,
    left_auth: *const f32,
    right_econ: *const f32,
    right_auth: *const f32,
    esteem_ab: *const f32,
    esteem_ba: *const f32,
    claim_left: *const f32,
    claim_right: *const f32,
    claim_bonus: *const f32,
    bloc_bias: *const f32,
    base_reason_sum: *const f32,
    witness_against: *const f32,
    witness_for: *const f32,
    identity_affinity: *const f32,
    strength_ratio: *const f32,
    fear_reach: *const f32,
    pair_flags: *const u8,
    ideology_weight: f32,
    social_weight: f32,
    geopolitics_ramp: f32,
    third_party_floor: f32,
    third_party_ceiling: f32,
    out_politics: *mut f32,
    out_social: *mut f32,
    out_claims: *mut f32,
    out_geopolitics: *mut f32,
    out_opinion: *mut f32,
    out_fear: *mut f32,
) -> i32 {
    let n = pair_count as usize;
    if n == 0 {
        return 0;
    }
    if left_econ.is_null()
        || left_auth.is_null()
        || right_econ.is_null()
        || right_auth.is_null()
        || esteem_ab.is_null()
        || esteem_ba.is_null()
        || claim_left.is_null()
        || claim_right.is_null()
        || claim_bonus.is_null()
        || bloc_bias.is_null()
        || base_reason_sum.is_null()
        || witness_against.is_null()
        || witness_for.is_null()
        || identity_affinity.is_null()
        || strength_ratio.is_null()
        || fear_reach.is_null()
        || pair_flags.is_null()
        || out_politics.is_null()
        || out_social.is_null()
        || out_claims.is_null()
        || out_geopolitics.is_null()
        || out_opinion.is_null()
        || out_fear.is_null()
    {
        return -1;
    }

    match diplomacy::pair_score_batch(
        std::slice::from_raw_parts(left_econ, n),
        std::slice::from_raw_parts(left_auth, n),
        std::slice::from_raw_parts(right_econ, n),
        std::slice::from_raw_parts(right_auth, n),
        std::slice::from_raw_parts(esteem_ab, n),
        std::slice::from_raw_parts(esteem_ba, n),
        std::slice::from_raw_parts(claim_left, n),
        std::slice::from_raw_parts(claim_right, n),
        std::slice::from_raw_parts(claim_bonus, n),
        std::slice::from_raw_parts(bloc_bias, n),
        std::slice::from_raw_parts(base_reason_sum, n),
        std::slice::from_raw_parts(witness_against, n),
        std::slice::from_raw_parts(witness_for, n),
        std::slice::from_raw_parts(identity_affinity, n),
        std::slice::from_raw_parts(strength_ratio, n),
        std::slice::from_raw_parts(fear_reach, n),
        std::slice::from_raw_parts(pair_flags, n),
        ideology_weight,
        social_weight,
        geopolitics_ramp,
        third_party_floor,
        third_party_ceiling,
        std::slice::from_raw_parts_mut(out_politics, n),
        std::slice::from_raw_parts_mut(out_social, n),
        std::slice::from_raw_parts_mut(out_claims, n),
        std::slice::from_raw_parts_mut(out_geopolitics, n),
        std::slice::from_raw_parts_mut(out_opinion, n),
        std::slice::from_raw_parts_mut(out_fear, n),
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_dip_reason_decay_batch_v1(
    reason_count: u32,
    base_opinion: *const f32,
    stacks: *const i32,
    scale: *const f32,
    granted_day: *const i32,
    expires_day: *const i32,
    decay_days: *const i32,
    flags: *const u8,
    today: i32,
    out_weight: *mut f32,
    out_prune: *mut u8,
) -> i32 {
    let n = reason_count as usize;
    if n == 0 {
        return 0;
    }
    if base_opinion.is_null()
        || stacks.is_null()
        || scale.is_null()
        || granted_day.is_null()
        || expires_day.is_null()
        || decay_days.is_null()
        || flags.is_null()
        || out_weight.is_null()
        || out_prune.is_null()
    {
        return -1;
    }

    match diplomacy::reason_decay_batch(
        std::slice::from_raw_parts(base_opinion, n),
        std::slice::from_raw_parts(stacks, n),
        std::slice::from_raw_parts(scale, n),
        std::slice::from_raw_parts(granted_day, n),
        std::slice::from_raw_parts(expires_day, n),
        std::slice::from_raw_parts(decay_days, n),
        std::slice::from_raw_parts(flags, n),
        today,
        std::slice::from_raw_parts_mut(out_weight, n),
        std::slice::from_raw_parts_mut(out_prune, n),
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_dip_partner_eval_batch_v1(
    partner_count: u32,
    opinion: *const f32,
    stance: *const u8,
    bloc_bias: *const f32,
    trade_dep: *const f32,
    flags: *const u8,
    reach: f32,
    base_trade_th: f32,
    base_pact_th: f32,
    base_alliance_th: f32,
    era_mode: i32,
    friendship_th: f32,
    rivalry_th: f32,
    embargo_reconcile_op: f32,
    can_mission_budget: u8,
    out_action_mask: *mut u8,
    out_trade_th: *mut f32,
    out_best_friend_idx: *mut i32,
    out_worst_rival_idx: *mut i32,
) -> i32 {
    if out_best_friend_idx.is_null() || out_worst_rival_idx.is_null() {
        return -1;
    }
    let n = partner_count as usize;
    if n == 0 {
        *out_best_friend_idx = -1;
        *out_worst_rival_idx = -1;
        return 0;
    }
    if opinion.is_null()
        || stance.is_null()
        || bloc_bias.is_null()
        || trade_dep.is_null()
        || flags.is_null()
        || out_action_mask.is_null()
        || out_trade_th.is_null()
    {
        return -1;
    }

    match diplomacy::partner_eval_batch(
        std::slice::from_raw_parts(opinion, n),
        std::slice::from_raw_parts(stance, n),
        std::slice::from_raw_parts(bloc_bias, n),
        std::slice::from_raw_parts(trade_dep, n),
        std::slice::from_raw_parts(flags, n),
        reach,
        base_trade_th,
        base_pact_th,
        base_alliance_th,
        era_mode,
        friendship_th,
        rivalry_th,
        embargo_reconcile_op,
        can_mission_budget != 0,
        std::slice::from_raw_parts_mut(out_action_mask, n),
        std::slice::from_raw_parts_mut(out_trade_th, n),
    ) {
        Ok((best, worst)) => {
            *out_best_friend_idx = best;
            *out_worst_rival_idx = worst;
            0
        }
        Err(c) => {
            *out_best_friend_idx = -1;
            *out_worst_rival_idx = -1;
            c
        }
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_market_settle_prices_v1(
    good_count: u32,
    nation_count: u32,
    nation_stride: u32,
    base_price: *const f32,
    traded_world_price: *const u8,
    demand: *const f32,
    production: *const f32,
    imports: *const f32,
    relieved: *const f32,
    adjust_price: u8,
    import_markup_def: f32,
    relief_def: f32,
    price_adjust_speed: f32,
    satisfaction: *mut f32,
    price: *mut f32,
    out_world_scarcity: *mut f32,
) -> i32 {
    let gc = good_count as usize;
    let nc = nation_count as usize;
    let ns = nation_stride as usize;
    if gc == 0 || nc == 0 {
        return 0;
    }
    if base_price.is_null()
        || traded_world_price.is_null()
        || demand.is_null()
        || production.is_null()
        || imports.is_null()
        || relieved.is_null()
        || satisfaction.is_null()
        || price.is_null()
    {
        return -1;
    }
    let total = match gc.checked_mul(ns) {
        Some(v) => v,
        None => return -2,
    };
    let ws = if out_world_scarcity.is_null() {
        &mut [][..]
    } else {
        std::slice::from_raw_parts_mut(out_world_scarcity, gc)
    };
    match market::settle_prices_batch(
        gc,
        nc,
        ns,
        std::slice::from_raw_parts(base_price, gc),
        std::slice::from_raw_parts(traded_world_price, gc),
        std::slice::from_raw_parts(demand, total),
        std::slice::from_raw_parts(production, total),
        std::slice::from_raw_parts(imports, total),
        std::slice::from_raw_parts(relieved, total),
        adjust_price != 0,
        import_markup_def,
        relief_def,
        price_adjust_speed,
        std::slice::from_raw_parts_mut(satisfaction, total),
        std::slice::from_raw_parts_mut(price, total),
        ws,
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_market_clear_nations_v1(
    nation_count: u32,
    good_count: u32,
    good_stride: u32,
    base_price: *const f32,
    essential_weight: *const f32,
    tariff_rate: *const f32,
    production: *const f32,
    demand: *const f32,
    imports: *const f32,
    exports: *const f32,
    relieved: *const f32,
    satisfaction: *const f32,
    price: *const f32,
    market_trade_share: f32,
    gdp_price_share: f32,
    gdp_price_band: f32,
    out_gross_output: *mut f32,
    out_real_output: *mut f32,
    out_shortage_index: *mut f32,
    out_market_access: *mut f32,
    out_weekly_trade_balance: *mut f32,
    out_weekly_tariff_income: *mut f32,
) -> i32 {
    let nc = nation_count as usize;
    let gc = good_count as usize;
    let gs = good_stride as usize;
    if nc == 0 || gc == 0 {
        return 0;
    }
    if base_price.is_null()
        || essential_weight.is_null()
        || tariff_rate.is_null()
        || production.is_null()
        || demand.is_null()
        || imports.is_null()
        || exports.is_null()
        || relieved.is_null()
        || satisfaction.is_null()
        || price.is_null()
        || out_gross_output.is_null()
        || out_real_output.is_null()
        || out_shortage_index.is_null()
        || out_market_access.is_null()
        || out_weekly_trade_balance.is_null()
        || out_weekly_tariff_income.is_null()
    {
        return -1;
    }
    let total = match nc.checked_mul(gs) {
        Some(v) => v,
        None => return -2,
    };
    match market::clear_nations_batch(
        nc,
        gc,
        gs,
        std::slice::from_raw_parts(base_price, gc),
        std::slice::from_raw_parts(essential_weight, gc),
        std::slice::from_raw_parts(tariff_rate, nc),
        std::slice::from_raw_parts(production, total),
        std::slice::from_raw_parts(demand, total),
        std::slice::from_raw_parts(imports, total),
        std::slice::from_raw_parts(exports, total),
        std::slice::from_raw_parts(relieved, total),
        std::slice::from_raw_parts(satisfaction, total),
        std::slice::from_raw_parts(price, total),
        market_trade_share,
        gdp_price_share,
        gdp_price_band,
        std::slice::from_raw_parts_mut(out_gross_output, nc),
        std::slice::from_raw_parts_mut(out_real_output, nc),
        std::slice::from_raw_parts_mut(out_shortage_index, nc),
        std::slice::from_raw_parts_mut(out_market_access, nc),
        std::slice::from_raw_parts_mut(out_weekly_trade_balance, nc),
        std::slice::from_raw_parts_mut(out_weekly_tariff_income, nc),
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_distance_haversine_batch_v1(
    origin_lat: f32,
    origin_lon: f32,
    target_lats: *const f32,
    target_lons: *const f32,
    target_count: u32,
    out_km: *mut f32,
) -> i32 {
    let n = target_count as usize;
    if n == 0 {
        return 0;
    }
    if target_lats.is_null() || target_lons.is_null() || out_km.is_null() {
        return -1;
    }
    match distance::haversine_batch(
        origin_lat,
        origin_lon,
        std::slice::from_raw_parts(target_lats, n),
        std::slice::from_raw_parts(target_lons, n),
        std::slice::from_raw_parts_mut(out_km, n),
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_distance_within_range_batch_v1(
    origin_lat: f32,
    origin_lon: f32,
    target_lats: *const f32,
    target_lons: *const f32,
    target_count: u32,
    reach_km: f32,
    out_indices: *mut i32,
    out_km: *mut f32,
    out_capacity: u32,
    out_count: *mut u32,
) -> i32 {
    if out_count.is_null() {
        return -1;
    }
    let n = target_count as usize;
    let cap = out_capacity as usize;
    if n == 0 {
        *out_count = 0;
        return 0;
    }
    if target_lats.is_null()
        || target_lons.is_null()
        || (cap > 0 && (out_indices.is_null() || out_km.is_null()))
    {
        *out_count = 0;
        return -1;
    }
    let oi = if cap > 0 { std::slice::from_raw_parts_mut(out_indices, cap) } else { &mut [] };
    let ok = if cap > 0 { std::slice::from_raw_parts_mut(out_km, cap) } else { &mut [] };
    match distance::within_range_batch(
        origin_lat,
        origin_lon,
        std::slice::from_raw_parts(target_lats, n),
        std::slice::from_raw_parts(target_lons, n),
        reach_km,
        oi,
        ok,
    ) {
        Ok(cnt) => {
            *out_count = cnt as u32;
            0
        }
        Err(c) => {
            *out_count = 0;
            c
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn dof_distance_pairwise_matrix_v1(
    lats: *const f32,
    lons: *const f32,
    count: u32,
    stride: u32,
    out_km: *mut f32,
) -> i32 {
    let n = count as usize;
    let s = stride as usize;
    if n == 0 {
        return 0;
    }
    if lats.is_null() || lons.is_null() || out_km.is_null() {
        return -1;
    }
    let needed = match n.checked_mul(s) {
        Some(v) => v,
        None => return -2,
    };
    match distance::pairwise_matrix(
        std::slice::from_raw_parts(lats, n),
        std::slice::from_raw_parts(lons, n),
        s,
        std::slice::from_raw_parts_mut(out_km, needed),
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_demo_step_cohorts_batch_v1(
    count: u32,
    population: *const f32,
    review_births: *const i64,
    review_deaths: *const i64,
    young_share: *const f32,
    old_share: *const f32,
    maturation_annual: f32,
    ageing_annual: f32,
    mortality_old_weight: f32,
    interval: f32,
    out_young: *mut f32,
    out_old: *mut f32,
    out_working: *mut f32,
) -> i32 {
    let n = count as usize;
    if n == 0 {
        return 0;
    }
    if population.is_null()
        || review_births.is_null()
        || review_deaths.is_null()
        || young_share.is_null()
        || old_share.is_null()
        || out_young.is_null()
        || out_old.is_null()
        || out_working.is_null()
    {
        return -1;
    }
    match economy_demo::step_cohorts_batch(
        std::slice::from_raw_parts(population, n),
        std::slice::from_raw_parts(review_births, n),
        std::slice::from_raw_parts(review_deaths, n),
        std::slice::from_raw_parts(young_share, n),
        std::slice::from_raw_parts(old_share, n),
        maturation_annual,
        ageing_annual,
        mortality_old_weight,
        interval,
        std::slice::from_raw_parts_mut(out_young, n),
        std::slice::from_raw_parts_mut(out_old, n),
        std::slice::from_raw_parts_mut(out_working, n),
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_demo_aggregate_nations_v1(
    nation_count: u32,
    state_offsets: *const u32,
    state_pop: *const i64,
    state_young: *const f32,
    state_old: *const f32,
    state_births: *const i64,
    state_deaths: *const i64,
    state_migration: *const i64,
    state_count: u32,
    default_young: f32,
    default_old: f32,
    default_working: f32,
    ref_old: f32,
    out_young: *mut f32,
    out_old: *mut f32,
    out_working: *mut f32,
    out_old_excess: *mut f32,
    out_births: *mut i64,
    out_deaths: *mut i64,
    out_migration: *mut i64,
) -> i32 {
    let nc = nation_count as usize;
    let sc = state_count as usize;
    if nc == 0 {
        return 0;
    }
    if state_offsets.is_null()
        || out_young.is_null()
        || out_old.is_null()
        || out_working.is_null()
        || out_old_excess.is_null()
        || out_births.is_null()
        || out_deaths.is_null()
        || out_migration.is_null()
        || (sc > 0
            && (state_pop.is_null()
                || state_young.is_null()
                || state_old.is_null()
                || state_births.is_null()
                || state_deaths.is_null()
                || state_migration.is_null()))
    {
        return -1;
    }
    let sp = if sc > 0 { std::slice::from_raw_parts(state_pop, sc) } else { &[] };
    let sy = if sc > 0 { std::slice::from_raw_parts(state_young, sc) } else { &[] };
    let so = if sc > 0 { std::slice::from_raw_parts(state_old, sc) } else { &[] };
    let sb = if sc > 0 { std::slice::from_raw_parts(state_births, sc) } else { &[] };
    let sd = if sc > 0 { std::slice::from_raw_parts(state_deaths, sc) } else { &[] };
    let sm = if sc > 0 { std::slice::from_raw_parts(state_migration, sc) } else { &[] };

    match economy_demo::aggregate_nations(
        nc,
        std::slice::from_raw_parts(state_offsets, nc + 1),
        sp,
        sy,
        so,
        sb,
        sd,
        sm,
        sc,
        default_young,
        default_old,
        default_working,
        ref_old,
        std::slice::from_raw_parts_mut(out_young, nc),
        std::slice::from_raw_parts_mut(out_old, nc),
        std::slice::from_raw_parts_mut(out_working, nc),
        std::slice::from_raw_parts_mut(out_old_excess, nc),
        std::slice::from_raw_parts_mut(out_births, nc),
        std::slice::from_raw_parts_mut(out_deaths, nc),
        std::slice::from_raw_parts_mut(out_migration, nc),
    ) {
        Ok(()) => 0,
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_econ_household_groups_v1(
    group_count: u32,
    sizes: *const i64,
    is_upper: *const u8,
    anchor_wealth: *const f32,
    employment: *mut f32,
    wealth: *mut f32,
    sol: *mut f32,
    radicalism: *mut f32,
    working_share: f32,
    need_employment: u8,
    building_jobs: f32,
    non_building_jobs_in: f32,
    annual_conditions: f32,
    interval: f32,
    damage: f32,
    food: f32,
    energy: f32,
    medicine: f32,
    scarcity_tolerance: f32,
    employment_rate: f32,
    wealth_rate: f32,
    nation_sol: f32,
    state_rad: f32,
    out_non_building_jobs: *mut f32,
    out_emp_target: *mut f32,
    out_basic_needs: *mut f32,
    out_stress: *mut f32,
    out_living_sum: *mut f64,
) -> i32 {
    let gc = group_count as usize;
    if out_non_building_jobs.is_null()
        || out_emp_target.is_null()
        || out_basic_needs.is_null()
        || out_stress.is_null()
        || out_living_sum.is_null()
        || (gc > 0
            && (sizes.is_null()
                || is_upper.is_null()
                || anchor_wealth.is_null()
                || employment.is_null()
                || wealth.is_null()
                || sol.is_null()
                || radicalism.is_null()))
    {
        return -1;
    }

    let sz = if gc > 0 { std::slice::from_raw_parts(sizes, gc) } else { &[] };
    let iu = if gc > 0 { std::slice::from_raw_parts(is_upper, gc) } else { &[] };
    let aw = if gc > 0 { std::slice::from_raw_parts(anchor_wealth, gc) } else { &[] };
    let em = if gc > 0 { std::slice::from_raw_parts_mut(employment, gc) } else { &mut [] };
    let we = if gc > 0 { std::slice::from_raw_parts_mut(wealth, gc) } else { &mut [] };
    let so = if gc > 0 { std::slice::from_raw_parts_mut(sol, gc) } else { &mut [] };
    let ra = if gc > 0 { std::slice::from_raw_parts_mut(radicalism, gc) } else { &mut [] };

    match economy_demo::household_groups(
        gc,
        sz,
        iu,
        aw,
        em,
        we,
        so,
        ra,
        working_share,
        need_employment != 0,
        building_jobs,
        non_building_jobs_in,
        annual_conditions,
        interval,
        damage,
        food,
        energy,
        medicine,
        scarcity_tolerance,
        employment_rate,
        wealth_rate,
        nation_sol,
        state_rad,
    ) {
        Ok((nb, et, bn, st, living)) => {
            *out_non_building_jobs = nb;
            *out_emp_target = et;
            *out_basic_needs = bn;
            *out_stress = st;
            *out_living_sum = living;
            0
        }
        Err(c) => c,
    }
}

#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn dof_econ_vital_rates_batch_v1(
    count: u32,
    pop: *const i64,
    war_damage: *const f32,
    hospitals: *const i32,
    literacy: *const f32,
    urbanization: *const f32,
    sol: *const f32,
    radicalism: *const f32,
    young_share: *const f32,
    old_share: *const f32,
    welfare_spending: *const f32,
    inflation: *const f32,
    war_support: *const f32,
    nation_stability: *const f32,
    state_stability: *const f32,
    capital_lat: *const f32,
    food: *const f32,
    energy: *const f32,
    medicine: *const f32,
    flags: *const u8,
    scarcity_tolerance: f32,
    birth_base: f32,
    low_lit_birth: f32,
    death_base: f32,
    shortage_death: f32,
    ref_old: f32,
    mort_old_weight: f32,
    interval: f32,
    out_birth: *mut f32,
    out_death: *mut f32,
    out_old_burden: *mut f32,
    out_review_births: *mut i64,
    out_review_deaths: *mut i64,
) -> i32 {
    let n = count as usize;
    if n == 0 {
        return 0;
    }
    if pop.is_null()
        || war_damage.is_null()
        || hospitals.is_null()
        || literacy.is_null()
        || urbanization.is_null()
        || sol.is_null()
        || radicalism.is_null()
        || young_share.is_null()
        || old_share.is_null()
        || welfare_spending.is_null()
        || inflation.is_null()
        || war_support.is_null()
        || nation_stability.is_null()
        || state_stability.is_null()
        || capital_lat.is_null()
        || food.is_null()
        || energy.is_null()
        || medicine.is_null()
        || flags.is_null()
        || out_birth.is_null()
        || out_death.is_null()
        || out_old_burden.is_null()
    {
        return -1;
    }

    let orb = if out_review_births.is_null() {
        None
    } else {
        Some(std::slice::from_raw_parts_mut(out_review_births, n))
    };
    let ord = if out_review_deaths.is_null() {
        None
    } else {
        Some(std::slice::from_raw_parts_mut(out_review_deaths, n))
    };

    match economy_demo::vital_rates_batch(
        std::slice::from_raw_parts(pop, n),
        std::slice::from_raw_parts(war_damage, n),
        std::slice::from_raw_parts(hospitals, n),
        std::slice::from_raw_parts(literacy, n),
        std::slice::from_raw_parts(urbanization, n),
        std::slice::from_raw_parts(sol, n),
        std::slice::from_raw_parts(radicalism, n),
        std::slice::from_raw_parts(young_share, n),
        std::slice::from_raw_parts(old_share, n),
        std::slice::from_raw_parts(welfare_spending, n),
        std::slice::from_raw_parts(inflation, n),
        std::slice::from_raw_parts(war_support, n),
        std::slice::from_raw_parts(nation_stability, n),
        std::slice::from_raw_parts(state_stability, n),
        std::slice::from_raw_parts(capital_lat, n),
        std::slice::from_raw_parts(food, n),
        std::slice::from_raw_parts(energy, n),
        std::slice::from_raw_parts(medicine, n),
        std::slice::from_raw_parts(flags, n),
        scarcity_tolerance,
        birth_base,
        low_lit_birth,
        death_base,
        shortage_death,
        ref_old,
        mort_old_weight,
        interval,
        std::slice::from_raw_parts_mut(out_birth, n),
        std::slice::from_raw_parts_mut(out_death, n),
        std::slice::from_raw_parts_mut(out_old_burden, n),
        orb,
        ord,
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
        assert!(dof_kernels_capabilities() & (1 | 2 | 4 | 8 | 64 | 128) == (1 | 2 | 4 | 8 | 64 | 128));
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
            assert_eq!(
                dof_dip_pair_score_batch_v1(
                    1,
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                ),
                -1
            );
            assert_eq!(
                dof_market_settle_prices_v1(
                    1,
                    1,
                    1,
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    0,
                    1.0,
                    0.0,
                    0.5,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                ),
                -1
            );
            let mut rcnt = 7u32;
            assert_eq!(
                dof_distance_within_range_batch_v1(
                    0.0,
                    0.0,
                    std::ptr::null(),
                    std::ptr::null(),
                    1,
                    100.0,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    0,
                    &mut rcnt,
                ),
                -1
            );
            assert_eq!(rcnt, 0);
            assert_eq!(
                dof_demo_step_cohorts_batch_v1(
                    1,
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                ),
                -1
            );
            assert_eq!(
                dof_demo_aggregate_nations_v1(
                    1,
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    0,
                    0.3,
                    0.09,
                    0.61,
                    0.1,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                ),
                -1
            );
            assert_eq!(
                dof_econ_household_groups_v1(
                    1,
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    0.62,
                    0,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    1.0,
                    1.0,
                    1.0,
                    0.0,
                    0.08,
                    0.025,
                    0.5,
                    0.1,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                ),
                -1
            );
            assert_eq!(
                dof_econ_vital_rates_batch_v1(
                    1,
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    std::ptr::null(),
                    0.0,
                    0.01,
                    0.018,
                    0.006,
                    0.005,
                    0.1,
                    1.4,
                    0.02,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
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

    #[test]
    fn diplomacy_market_and_distance_simd_match_scalar() {
        let n = 19usize;
        let left_econ: Vec<f32> = (0..n).map(|i| -0.8 + (i as f32) * 0.09).collect();
        let left_auth: Vec<f32> = (0..n).map(|i| 0.7 - (i as f32) * 0.08).collect();
        let right_econ: Vec<f32> = (0..n).map(|i| 0.4 - (i as f32) * 0.07).collect();
        let right_auth: Vec<f32> = (0..n).map(|i| -0.5 + (i as f32) * 0.06).collect();
        let esteem_ab: Vec<f32> = (0..n).map(|i| ((i as f32) - 9.0) * 0.1).collect();
        let esteem_ba: Vec<f32> = (0..n).map(|i| (5.0 - (i as f32)) * 0.08).collect();
        let claim_left: Vec<f32> = (0..n).map(|i| if i % 3 == 0 { 0.65 } else { 0.0 }).collect();
        let claim_right: Vec<f32> = (0..n).map(|i| if i % 5 == 0 { 0.5 } else { 0.0 }).collect();
        let claim_bonus: Vec<f32> = (0..n).map(|i| if i % 2 == 0 { 0.15 } else { 0.0 }).collect();
        let bloc_bias: Vec<f32> = (0..n).map(|i| ((i as f32) - 8.0) * 0.05).collect();
        let base_sum: Vec<f32> = (0..n).map(|i| ((i as f32) - 7.0) * 6.5).collect();
        let wa: Vec<f32> = (0..n).map(|i| if i % 4 == 0 { -35.0 } else { -8.0 }).collect();
        let wf: Vec<f32> = (0..n).map(|i| if i % 6 == 0 { 22.0 } else { 4.0 }).collect();
        let id_aff: Vec<f32> = (0..n).map(|i| if i % 2 == 0 { 6.0 } else { 0.0 }).collect();
        let sr: Vec<f32> = (0..n).map(|i| 0.7 + (i as f32) * 0.15).collect();
        let fr: Vec<f32> = (0..n).map(|i| if i % 2 == 0 { 1.0 } else { 0.4 }).collect();
        let flags: Vec<u8> = (0..n).map(|i| ((i % 3 == 0) as u8) | (((i % 4 == 0) as u8) << 1) | 4).collect();

        let (mut op_p, mut op_s, mut op_c, mut op_g, mut op_o, mut op_f) =
            (vec![0f32; n], vec![0f32; n], vec![0f32; n], vec![0f32; n], vec![0f32; n], vec![0f32; n]);
        diplomacy::pair_score_batch(
            &left_econ, &left_auth, &right_econ, &right_auth, &esteem_ab, &esteem_ba,
            &claim_left, &claim_right, &claim_bonus, &bloc_bias, &base_sum, &wa, &wf,
            &id_aff, &sr, &fr, &flags, 18.0, 8.0, 0.75, -25.0, 15.0,
            &mut op_p, &mut op_s, &mut op_c, &mut op_g, &mut op_o, &mut op_f,
        )
        .unwrap();
        assert!(op_o.iter().all(|v| v.is_finite() && *v >= -100.0 && *v <= 100.0));

        // Distance range check
        let t_lats = [55.75f32, 48.85, 35.68, 59.93, -33.86];
        let t_lons = [37.61f32, 2.35, 139.69, 30.33, 151.20];
        let mut idx_out = [0i32; 5];
        let mut km_out = [0f32; 5];
        let cnt = distance::within_range_batch(55.75, 37.61, &t_lats, &t_lons, 800.0, &mut idx_out, &mut km_out).unwrap();
        assert_eq!(cnt, 2);
        assert_eq!(&idx_out[..cnt], &[0, 3]);
    }
}
