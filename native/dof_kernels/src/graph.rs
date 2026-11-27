// SPDX-License-Identifier: GPL-3.0-only
use std::cmp::Ordering;
use std::collections::BinaryHeap;

pub struct DofProvinceGraph {
    pub province_count: usize,
    pub csr_offsets: Vec<u32>,
    pub csr_edges: Vec<u32>,
    pub edge_km: Vec<f32>,
    pub land_offsets: Vec<u32>,
    pub land_edges: Vec<u32>,
    pub water: Vec<u8>,
    pub coastal_provinces: Vec<u32>,
    pub harbour_basin: Vec<i32>,
    pub prov_lat: Vec<f32>,
    pub prov_lon: Vec<f32>,
}

#[derive(Copy, Clone)]
struct HeapEntry {
    dist: f32,
    prov: u32,
}

impl PartialEq for HeapEntry {
    #[inline]
    fn eq(&self, other: &Self) -> bool {
        self.dist == other.dist && self.prov == other.prov
    }
}

impl Eq for HeapEntry {}

impl PartialOrd for HeapEntry {
    #[inline]
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for HeapEntry {
    #[inline]
    fn cmp(&self, other: &Self) -> Ordering {
        // Reverse order for BinaryHeap (min-heap on (dist, prov)), matching C# PriorityQueue<int, (float, int)>.
        other
            .dist
            .total_cmp(&self.dist)
            .then_with(|| other.prov.cmp(&self.prov))
    }
}

pub struct DofGraphScratch {
    pub province_count: usize,
    generation: u32,
    seen_gen: Vec<u32>,
    best_dist: Vec<f32>,
    heap: BinaryHeap<HeapEntry>,
    touched: Vec<i32>,
    // Logistics BFS scratch (generation-stamped, zero per-tick full clear)
    log_gen: u32,
    reach_gen: Vec<u32>,
    stamp_gen: Vec<u32>,
    hop_dist: Vec<i32>,
    source_state: Vec<u16>,
    bfs_queue: Vec<u32>,
    // Per-state indexed tables (u16 max 65536)
    state_stamp: Vec<u32>,
    hub_prov: Vec<i32>,
    hub_flags: Vec<u8>, // bit 0: isolated, bit 1: forward
    land_stamp: Vec<u32>,
    land_counts: Vec<i32>,
    held_stamp: Vec<u32>,
    held_counts: Vec<i32>,
    open_basins_stamp: Vec<u32>,
    open_basins_gen: u32,
    hub_states: Vec<u16>,
    pair_scratch: Vec<u32>,
}

const MAX_VERTICES: usize = 1_000_000;
const MAX_EDGES: usize = 16_000_000;
const MAX_U16_STATES: usize = 65_536;

impl DofProvinceGraph {
    #[allow(clippy::too_many_arguments)]
    pub unsafe fn from_raw(
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
    ) -> Option<Self> {
        let n = province_count as usize;
        let e = edge_count as usize;
        let le = land_edge_count as usize;
        if n < 1 || n > MAX_VERTICES || e > MAX_EDGES || le > MAX_EDGES {
            return None;
        }
        if csr_offsets.is_null() || water.is_null() || (e > 0 && (csr_edges.is_null() || edge_km.is_null())) {
            return None;
        }
        if (!land_offsets.is_null()) != (le == 0 || !land_edges.is_null()) {
            // If land_offsets is provided and le > 0, land_edges must be non-null
        }
        if !land_offsets.is_null() && le > 0 && land_edges.is_null() {
            return None;
        }

        let offsets_slice = std::slice::from_raw_parts(csr_offsets, n + 1);
        if offsets_slice[0] != 0 || offsets_slice[n] as usize != e {
            return None;
        }
        for i in 0..n {
            if offsets_slice[i] > offsets_slice[i + 1] {
                return None;
            }
        }
        let edges_vec = if e > 0 {
            let s = std::slice::from_raw_parts(csr_edges, e);
            for &v in s {
                if (v as usize) >= n {
                    return None;
                }
            }
            s.to_vec()
        } else {
            Vec::new()
        };
        let km_vec = if e > 0 {
            let s = std::slice::from_raw_parts(edge_km, e);
            for &d in s {
                if !d.is_finite() || d < 0.0 {
                    return None;
                }
            }
            s.to_vec()
        } else {
            Vec::new()
        };

        let land_offsets_vec = if !land_offsets.is_null() {
            let lo = std::slice::from_raw_parts(land_offsets, n + 1);
            if lo[0] != 0 || lo[n] as usize != le {
                return None;
            }
            for i in 0..n {
                if lo[i] > lo[i + 1] {
                    return None;
                }
            }
            lo.to_vec()
        } else {
            vec![0u32; n + 1]
        };

        let land_edges_vec = if !land_offsets.is_null() && le > 0 {
            let s = std::slice::from_raw_parts(land_edges, le);
            for &v in s {
                if (v as usize) >= n {
                    return None;
                }
            }
            s.to_vec()
        } else {
            Vec::new()
        };

        let water_slice = std::slice::from_raw_parts(water, n);
        for &w in water_slice {
            if w > 1 {
                return None;
            }
        }

        let mut coastal_provinces = Vec::new();
        if !coastal.is_null() {
            let cs = std::slice::from_raw_parts(coastal, n);
            for (p, &flag) in cs.iter().enumerate() {
                if p > 0 && flag != 0 {
                    coastal_provinces.push(p as u32);
                }
            }
        }

        let basin_vec = if !harbour_basin.is_null() {
            std::slice::from_raw_parts(harbour_basin, n).to_vec()
        } else {
            vec![-1i32; n]
        };

        let lat_vec = if !prov_lat.is_null() {
            std::slice::from_raw_parts(prov_lat, n).to_vec()
        } else {
            vec![0.0f32; n]
        };

        let lon_vec = if !prov_lon.is_null() {
            std::slice::from_raw_parts(prov_lon, n).to_vec()
        } else {
            vec![0.0f32; n]
        };

        Some(Self {
            province_count: n,
            csr_offsets: offsets_slice.to_vec(),
            csr_edges: edges_vec,
            edge_km: km_vec,
            land_offsets: land_offsets_vec,
            land_edges: land_edges_vec,
            water: water_slice.to_vec(),
            coastal_provinces,
            harbour_basin: basin_vec,
            prov_lat: lat_vec,
            prov_lon: lon_vec,
        })
    }
}

impl DofGraphScratch {
    pub fn new(province_count: usize) -> Option<Self> {
        if province_count < 1 || province_count > MAX_VERTICES {
            return None;
        }
        Some(Self {
            province_count,
            generation: 0,
            seen_gen: vec![0; province_count],
            best_dist: vec![0.0; province_count],
            heap: BinaryHeap::with_capacity(4096),
            touched: Vec::with_capacity(4096),
            log_gen: 0,
            reach_gen: vec![0; province_count],
            stamp_gen: vec![0; province_count],
            hop_dist: vec![0; province_count],
            source_state: vec![0; province_count],
            bfs_queue: Vec::with_capacity(4096),
            state_stamp: vec![0; MAX_U16_STATES],
            hub_prov: vec![0; MAX_U16_STATES],
            hub_flags: vec![0; MAX_U16_STATES],
            land_stamp: vec![0; MAX_U16_STATES],
            land_counts: vec![0; MAX_U16_STATES],
            held_stamp: vec![0; MAX_U16_STATES],
            held_counts: vec![0; MAX_U16_STATES],
            open_basins_stamp: Vec::with_capacity(64),
            open_basins_gen: 0,
            hub_states: Vec::with_capacity(128),
            pair_scratch: Vec::with_capacity(4096),
        })
    }

    #[inline]
    fn bump_gen(&mut self) -> u32 {
        self.generation = self.generation.wrapping_add(1);
        if self.generation == 0 {
            self.seen_gen.fill(0);
            self.generation = 1;
        }
        self.generation
    }

    #[inline]
    fn bump_log_gen(&mut self) -> u32 {
        self.log_gen = self.log_gen.wrapping_add(1);
        if self.log_gen == 0 {
            self.reach_gen.fill(0);
            self.stamp_gen.fill(0);
            self.state_stamp.fill(0);
            self.land_stamp.fill(0);
            self.held_stamp.fill(0);
            self.log_gen = 1;
        }
        self.log_gen
    }
}

/// Bit-identical persistent arena Dijkstra matching `LongRangeMovement.DistanceField`.
pub fn distance_field(
    graph: &DofProvinceGraph,
    scratch: &mut DofGraphScratch,
    sources: &[i32],
    bound_km: f32,
    out_provinces: &mut [i32],
    out_distances: &mut [f32],
) -> Result<usize, i32> {
    let n = graph.province_count;
    if scratch.province_count < n {
        return Err(-2);
    }
    if out_provinces.len() != out_distances.len() {
        return Err(-2);
    }

    let gen_id = scratch.bump_gen();
    scratch.heap.clear();
    scratch.touched.clear();

    for &src in sources {
        if src < 0 || (src as usize) >= n {
            return Err(-2);
        }
        let u = src as usize;
        scratch.seen_gen[u] = gen_id;
        scratch.best_dist[u] = 0.0;
        scratch.heap.push(HeapEntry {
            dist: 0.0,
            prov: src as u32,
        });
    }

    while let Some(entry) = scratch.heap.pop() {
        let u = entry.prov as usize;
        // C#: if (key.Item1 > distance[province]) continue;
        if entry.dist > scratch.best_dist[u] {
            continue;
        }
        scratch.touched.push(entry.prov as i32);

        let begin = graph.csr_offsets[u] as usize;
        let end = graph.csr_offsets[u + 1] as usize;
        for ei in begin..end {
            let v = graph.csr_edges[ei] as usize;
            let km = entry.dist + graph.edge_km[ei];
            if km > bound_km {
                continue;
            }
            if scratch.seen_gen[v] == gen_id && scratch.best_dist[v] <= km {
                continue;
            }
            scratch.seen_gen[v] = gen_id;
            scratch.best_dist[v] = km;
            scratch.heap.push(HeapEntry {
                dist: km,
                prov: v as u32,
            });
        }
    }

    scratch.touched.sort_unstable();
    let count = scratch.touched.len();
    if count > out_provinces.len() {
        return Err(-3);
    }

    for (i, &p) in scratch.touched.iter().enumerate() {
        out_provinces[i] = p;
        out_distances[i] = scratch.best_dist[p as usize];
    }
    Ok(count)
}

#[inline]
fn bit_contains(words: &[u64], id: u16) -> bool {
    let w = (id as usize) >> 6;
    if w >= words.len() {
        return false;
    }
    ((words[w] >> (id & 63)) & 1) != 0
}

/// Generation-stamped land + coastal BFS matching `LogisticsCore.TraceNetwork` / `HubByState` / `TraceDepots`.
#[allow(clippy::too_many_arguments)]
pub fn solve_supply(
    graph: &DofProvinceGraph,
    scratch: &mut DofGraphScratch,
    controllers: &[u16],
    province_to_nation: &[u16],
    passable_bits: &[u64],
    own_map_index: u16,
    province_to_state: &[u16],
    roots: &[i32],
    territory: &[i32],
    barracks_state_bits: &[u64],
    land_state_ids: &[u16],
    land_state_counts: &[i32],
    held_state_ids: &[u16],
    held_state_counts: &[i32],
    insurgent: bool,
    forward_depot_held_share: f32,
    army_provinces: &[i32],
    out_army_flags: &mut [u8],
    out_army_distance: &mut [i32],
    out_army_source_state: &mut [i32],
) -> Result<(), i32> {
    let n = graph.province_count;
    if scratch.province_count < n
        || controllers.len() < n
        || province_to_nation.len() < n
        || province_to_state.len() < n
    {
        return Err(-2);
    }
    if land_state_ids.len() != land_state_counts.len()
        || held_state_ids.len() != held_state_counts.len()
    {
        return Err(-2);
    }
    if out_army_flags.len() < army_provinces.len()
        || out_army_distance.len() < army_provinces.len()
        || out_army_source_state.len() < army_provinces.len()
    {
        return Err(-3);
    }

    let gen_id = scratch.bump_log_gen();
    let passable = |ctrl: u16| -> bool { ctrl == own_map_index || bit_contains(passable_bits, ctrl) };

    // Populate O(1) land & held state count tables for ForwardDepot check.
    for i in 0..land_state_ids.len() {
        let s = land_state_ids[i] as usize;
        scratch.land_stamp[s] = gen_id;
        scratch.land_counts[s] = land_state_counts[i];
    }
    for i in 0..held_state_ids.len() {
        let s = held_state_ids[i] as usize;
        scratch.held_stamp[s] = gen_id;
        scratch.held_counts[s] = held_state_counts[i];
    }

    // 1. TraceNetwork: BFS from roots
    scratch.bfs_queue.clear();
    for &root in roots {
        if root <= 0 || (root as usize) >= n {
            continue;
        }
        let u = root as usize;
        if scratch.reach_gen[u] == gen_id {
            continue;
        }
        scratch.reach_gen[u] = gen_id;
        scratch.bfs_queue.push(u as u32);
    }

    let mut head = 0usize;
    while head < scratch.bfs_queue.len() {
        let u = scratch.bfs_queue[head] as usize;
        head += 1;
        let begin = graph.csr_offsets[u] as usize;
        let end = graph.csr_offsets[u + 1] as usize;
        for ei in begin..end {
            let v = graph.csr_edges[ei] as usize;
            if scratch.reach_gen[v] == gen_id || !passable(controllers[v]) {
                continue;
            }
            scratch.reach_gen[v] = gen_id;
            scratch.bfs_queue.push(v as u32);
        }
    }

    // Coastal sea-basin propagation matching SeaLanes.CoastalProvinceArray order.
    if !graph.coastal_provinces.is_empty() {
        scratch.open_basins_gen = scratch.open_basins_gen.wrapping_add(1);
        if scratch.open_basins_gen == 0 {
            scratch.open_basins_stamp.fill(0);
            scratch.open_basins_gen = 1;
        }
        let basin_gen = scratch.open_basins_gen;
        let mut any_basin = false;
        for &c in &graph.coastal_provinces {
            let ci = c as usize;
            if scratch.reach_gen[ci] == gen_id && controllers[ci] == own_map_index {
                let basin = graph.harbour_basin[ci];
                if basin >= 0 {
                    let bi = basin as usize;
                    if bi >= scratch.open_basins_stamp.len() {
                        scratch.open_basins_stamp.resize(bi + 16, 0);
                    }
                    scratch.open_basins_stamp[bi] = basin_gen;
                    any_basin = true;
                }
            }
        }
        if any_basin {
            scratch.bfs_queue.clear();
            for &c in &graph.coastal_provinces {
                let ci = c as usize;
                if scratch.reach_gen[ci] != gen_id && controllers[ci] == own_map_index {
                    let basin = graph.harbour_basin[ci];
                    if basin >= 0
                        && (basin as usize) < scratch.open_basins_stamp.len()
                        && scratch.open_basins_stamp[basin as usize] == basin_gen
                    {
                        scratch.reach_gen[ci] = gen_id;
                        scratch.bfs_queue.push(c);
                    }
                }
            }
            let mut h2 = 0usize;
            while h2 < scratch.bfs_queue.len() {
                let u = scratch.bfs_queue[h2] as usize;
                h2 += 1;
                let begin = graph.csr_offsets[u] as usize;
                let end = graph.csr_offsets[u + 1] as usize;
                for ei in begin..end {
                    let v = graph.csr_edges[ei] as usize;
                    if scratch.reach_gen[v] == gen_id || !passable(controllers[v]) {
                        continue;
                    }
                    scratch.reach_gen[v] = gen_id;
                    scratch.bfs_queue.push(v as u32);
                }
            }
        }
    }

    // 2. Build HubByState in territory order (preserving exact priority rules!)
    scratch.hub_states.clear();
    for &prov in territory {
        if prov <= 0 || (prov as usize) >= n {
            continue;
        }
        let p = prov as usize;
        if graph.water[p] != 0 {
            continue;
        }
        let is_connected = scratch.reach_gen[p] == gen_id;
        let state = province_to_state[p];
        let s_idx = state as usize;

        if !insurgent && province_to_nation[p] != own_map_index {
            if !is_connected || forward_depot_held_share > 1.0 {
                continue;
            }
            if scratch.state_stamp[s_idx] == gen_id {
                continue;
            }
            let land = if scratch.land_stamp[s_idx] == gen_id {
                scratch.land_counts[s_idx]
            } else {
                0
            };
            if land <= 0 {
                continue;
            }
            let held = if scratch.held_stamp[s_idx] == gen_id {
                scratch.held_counts[s_idx]
            } else {
                0
            };
            if (held as f32) < (land as f32) * forward_depot_held_share {
                continue;
            }
            scratch.state_stamp[s_idx] = gen_id;
            scratch.hub_prov[s_idx] = prov;
            scratch.hub_flags[s_idx] = 2; // forward
            scratch.hub_states.push(state);
            continue;
        }

        if is_connected {
            if scratch.state_stamp[s_idx] != gen_id {
                scratch.state_stamp[s_idx] = gen_id;
                scratch.hub_states.push(state);
                scratch.hub_prov[s_idx] = prov;
                scratch.hub_flags[s_idx] = 0;
            } else if (scratch.hub_flags[s_idx] & 3) != 0 {
                scratch.hub_prov[s_idx] = prov;
                scratch.hub_flags[s_idx] = 0;
            }
            continue;
        }

        if scratch.state_stamp[s_idx] == gen_id {
            continue;
        }
        if !bit_contains(barracks_state_bits, state) {
            continue;
        }
        scratch.state_stamp[s_idx] = gen_id;
        scratch.hub_prov[s_idx] = prov;
        scratch.hub_flags[s_idx] = 1; // isolated
        scratch.hub_states.push(state);
    }

    // 3. TraceDepots multi-source BFS
    scratch.bfs_queue.clear();
    for &st in &scratch.hub_states {
        let p = scratch.hub_prov[st as usize] as usize;
        scratch.stamp_gen[p] = gen_id;
        scratch.hop_dist[p] = 0;
        scratch.source_state[p] = st;
        scratch.bfs_queue.push(p as u32);
    }
    let run_depot_bfs = |q: &mut Vec<u32>,
                         stamp: &mut [u32],
                         hop_dist: &mut [i32],
                         source_state: &mut [u16]| {
        let mut h = 0usize;
        while h < q.len() {
            let u = q[h] as usize;
            h += 1;
            let begin = graph.csr_offsets[u] as usize;
            let end = graph.csr_offsets[u + 1] as usize;
            let base_d = hop_dist[u];
            let src = source_state[u];
            for ei in begin..end {
                let v = graph.csr_edges[ei] as usize;
                if stamp[v] == gen_id || !passable(controllers[v]) {
                    continue;
                }
                stamp[v] = gen_id;
                hop_dist[v] = base_d + if graph.water[v] != 0 { 0 } else { 1 };
                source_state[v] = src;
                q.push(v as u32);
            }
        }
    };
    run_depot_bfs(
        &mut scratch.bfs_queue,
        &mut scratch.stamp_gen,
        &mut scratch.hop_dist,
        &mut scratch.source_state,
    );

    // 4. Coastal spit home-fragment distribution points
    for &prov in territory {
        if prov <= 0 || (prov as usize) >= n {
            continue;
        }
        let p = prov as usize;
        if scratch.stamp_gen[p] == gen_id
            || scratch.reach_gen[p] != gen_id
            || graph.water[p] != 0
            || province_to_nation[p] != own_map_index
        {
            continue;
        }
        let st = province_to_state[p];
        let s_idx = st as usize;
        if scratch.state_stamp[s_idx] != gen_id || (scratch.hub_flags[s_idx] & 1) != 0 {
            continue;
        }
        scratch.stamp_gen[p] = gen_id;
        scratch.hop_dist[p] = 0;
        scratch.source_state[p] = st;
        scratch.bfs_queue.clear();
        scratch.bfs_queue.push(p as u32);
        run_depot_bfs(
            &mut scratch.bfs_queue,
            &mut scratch.stamp_gen,
            &mut scratch.hop_dist,
            &mut scratch.source_state,
        );
    }

    // 5. Write compact per-army supply outputs
    for (idx, &ap) in army_provinces.iter().enumerate() {
        if ap <= 0 || (ap as usize) >= n {
            out_army_flags[idx] = 0;
            out_army_distance[idx] = -1;
            out_army_source_state[idx] = 0;
            continue;
        }
        let p = ap as usize;
        let mut flags = 0u8;
        if scratch.reach_gen[p] == gen_id {
            flags |= 1;
        }
        if scratch.stamp_gen[p] == gen_id {
            flags |= 2;
            let src = scratch.source_state[p];
            if scratch.state_stamp[src as usize] == gen_id {
                let hf = scratch.hub_flags[src as usize];
                if (hf & 1) != 0 {
                    flags |= 4;
                }
                if (hf & 2) != 0 {
                    flags |= 8;
                }
            }
            out_army_distance[idx] = scratch.hop_dist[p];
            out_army_source_state[idx] = src as i32;
        } else {
            out_army_distance[idx] = -1;
            out_army_source_state[idx] = 0;
        }
        out_army_flags[idx] = flags;
    }

    Ok(())
}

/// Bit-exact connected ownership components matching `OwnershipComponentGraph.FillManaged`.
pub fn owner_components(
    graph: &DofProvinceGraph,
    scratch: &mut DofGraphScratch,
    owners: &[u16],
    out_labels: &mut [i32],
    out_members: &mut [i32],
    out_component_offsets: &mut [i32],
) -> Result<(i32, i32), i32> {
    let n = graph.province_count;
    if scratch.province_count < n || owners.len() != n {
        return Err(-2);
    }
    if out_labels.len() < n || out_members.len() < n || out_component_offsets.len() < n + 1 {
        return Err(-3);
    }

    out_labels[..n].fill(0);
    out_component_offsets[0] = 0;

    let mut components: i32 = 0;
    let mut tail: usize = 0;

    for start in 1..n {
        if out_labels[start] != 0 || owners[start] == 0 || graph.water[start] != 0 {
            continue;
        }
        let owner = owners[start];
        let mut head = tail;
        components += 1;
        out_labels[start] = components;
        out_members[tail] = start as i32;
        tail += 1;

        while head < tail {
            let vertex = out_members[head] as usize;
            head += 1;
            let begin = graph.csr_offsets[vertex] as usize;
            let end = graph.csr_offsets[vertex + 1] as usize;
            for ei in begin..end {
                let next = graph.csr_edges[ei] as usize;
                if next == 0
                    || next >= n
                    || out_labels[next] != 0
                    || owners[next] != owner
                    || graph.water[next] != 0
                {
                    continue;
                }
                out_labels[next] = components;
                out_members[tail] = next as i32;
                tail += 1;
            }
        }
        out_component_offsets[components as usize] = tail as i32;
    }

    Ok((components, tail as i32))
}

/// Fast CSR border adjacency scan matching `CampaignDiplomacy.Borders`.
/// Emits sorted unique `(owner << 16) | other` packed u32 pairs where `owner != 0 && other != 0 && owner != other`.
pub fn border_pairs(
    graph: &DofProvinceGraph,
    scratch: &mut DofGraphScratch,
    controllers: &[u16],
    out_pairs: &mut [u32],
) -> Result<usize, i32> {
    let n = graph.province_count;
    if controllers.len() < n {
        return Err(-2);
    }

    scratch.pair_scratch.clear();
    let offsets = &graph.csr_offsets;
    let edges = &graph.csr_edges;

    let mut last_packed = 0u32;
    for province in 1..n {
        let owner = unsafe { *controllers.get_unchecked(province) };
        if owner == 0 {
            continue;
        }
        let begin = unsafe { *offsets.get_unchecked(province) as usize };
        let end = unsafe { *offsets.get_unchecked(province + 1) as usize };
        let owner_hi = (owner as u32) << 16;
        for ei in begin..end {
            let nb = unsafe { *edges.get_unchecked(ei) as usize };
            let other = unsafe { *controllers.get_unchecked(nb) };
            if other == 0 || other == owner {
                continue;
            }
            let packed = owner_hi | (other as u32);
            if packed != last_packed {
                scratch.pair_scratch.push(packed);
                last_packed = packed;
            }
        }
    }

    scratch.pair_scratch.sort_unstable();
    scratch.pair_scratch.dedup();
    let count = scratch.pair_scratch.len();
    if count > out_pairs.len() {
        return Err(-3);
    }
    out_pairs[..count].copy_from_slice(&scratch.pair_scratch);
    Ok(count)
}

/// Fast occupation census matching `Warfare.MeasureOccupation`.
/// Scans `1..=province_count`, returning sorted `(orig, home_count)` and sorted `((orig << 16) | ctrl, occ_count)`.
#[allow(clippy::too_many_arguments)]
pub fn occupation_census(
    scratch: &mut DofGraphScratch,
    province_to_nation: &[u16],
    controllers: &[u16],
    province_count: usize,
    out_home_keys: &mut [u16],
    out_home_counts: &mut [i32],
    out_occ_pairs: &mut [u32],
    out_occ_counts: &mut [i32],
) -> Result<(usize, usize), i32> {
    if province_count + 1 > province_to_nation.len() || province_count + 1 > controllers.len() {
        return Err(-2);
    }
    if out_home_keys.len() != out_home_counts.len() || out_occ_pairs.len() != out_occ_counts.len() {
        return Err(-2);
    }

    let gen_id = scratch.bump_log_gen();
    scratch.pair_scratch.clear();
    scratch.bfs_queue.clear();

    for p in 1..=province_count {
        let orig = unsafe { *province_to_nation.get_unchecked(p) };
        if orig == 0 {
            continue;
        }
        let oi = orig as usize;
        if scratch.land_stamp[oi] != gen_id {
            scratch.land_stamp[oi] = gen_id;
            scratch.land_counts[oi] = 1;
            scratch.bfs_queue.push(orig as u32);
        } else {
            scratch.land_counts[oi] += 1;
        }
        let ctrl = unsafe { *controllers.get_unchecked(p) };
        if ctrl != 0 && ctrl != orig {
            scratch.pair_scratch.push(((orig as u32) << 16) | (ctrl as u32));
        }
    }

    scratch.bfs_queue.sort_unstable();
    let home_len = scratch.bfs_queue.len();
    if home_len > out_home_keys.len() {
        return Err(-3);
    }
    for (i, &k) in scratch.bfs_queue.iter().enumerate() {
        out_home_keys[i] = k as u16;
        out_home_counts[i] = scratch.land_counts[k as usize];
    }

    scratch.pair_scratch.sort_unstable();
    let mut occ_len = 0usize;
    let mut i = 0usize;
    let plen = scratch.pair_scratch.len();
    while i < plen {
        let key = scratch.pair_scratch[i];
        let mut cnt = 1i32;
        i += 1;
        while i < plen && scratch.pair_scratch[i] == key {
            cnt += 1;
            i += 1;
        }
        if occ_len >= out_occ_pairs.len() {
            return Err(-3);
        }
        out_occ_pairs[occ_len] = key;
        out_occ_counts[occ_len] = cnt;
        occ_len += 1;
    }

    Ok((home_len, occ_len))
}

/// Bit-exact CSR BFS vision flood matching `CampaignIntel.FloodVision`.
/// Applies `(seeds[i], seed_depths[i])` into `reach` and floods outwards along `csr_edges`.
pub fn flood_vision(
    graph: &DofProvinceGraph,
    scratch: &mut DofGraphScratch,
    reach: &mut [u8],
    seeds: &[i32],
    seed_depths: &[u8],
) -> Result<(), i32> {
    let n = graph.province_count;
    if reach.len() < n || seeds.len() != seed_depths.len() {
        return Err(-2);
    }
    scratch.bfs_queue.clear();
    for i in 0..seeds.len() {
        let p = seeds[i];
        if p <= 0 || (p as usize) >= n {
            continue;
        }
        let pu = p as usize;
        let level = seed_depths[i].saturating_add(1);
        if reach[pu] < level {
            reach[pu] = level;
            scratch.bfs_queue.push(pu as u32);
        }
    }

    let offsets = &graph.csr_offsets;
    let edges = &graph.csr_edges;
    let mut head = 0usize;
    while head < scratch.bfs_queue.len() {
        let u = scratch.bfs_queue[head] as usize;
        head += 1;
        let cur = reach[u];
        if cur < 2 {
            continue;
        }
        let next_level = cur - 1;
        let begin = unsafe { *offsets.get_unchecked(u) as usize };
        let end = unsafe { *offsets.get_unchecked(u + 1) as usize };
        for ei in begin..end {
            let v = unsafe { *edges.get_unchecked(ei) as usize };
            if v > 0 && v < n && reach[v] < next_level {
                reach[v] = next_level;
                scratch.bfs_queue.push(v as u32);
            }
        }
    }

    Ok(())
}

/// Clears `reach`, marks `owned_provinces` at `border_depth + 1`, enqueues border tiles only,
/// and floods outwards in flat CSR matching `CampaignIntel.Vision` territory cache build.
pub fn seed_territory_vision(
    graph: &DofProvinceGraph,
    scratch: &mut DofGraphScratch,
    owned_provinces: &[i32],
    border_depth: u8,
    reach: &mut [u8],
) -> Result<(), i32> {
    let n = graph.province_count;
    if reach.len() < n {
        return Err(-2);
    }
    reach[..n].fill(0);
    let own_level = border_depth.saturating_add(1);
    for &p in owned_provinces {
        if p > 0 && (p as usize) < n {
            reach[p as usize] = own_level;
        }
    }
    if border_depth == 0 {
        return Ok(());
    }

    scratch.bfs_queue.clear();
    let offsets = &graph.csr_offsets;
    let edges = &graph.csr_edges;
    for &p in owned_provinces {
        if p <= 0 || (p as usize) >= n {
            continue;
        }
        let u = p as usize;
        let begin = unsafe { *offsets.get_unchecked(u) as usize };
        let end = unsafe { *offsets.get_unchecked(u + 1) as usize };
        for ei in begin..end {
            let v = unsafe { *edges.get_unchecked(ei) as usize };
            if v > 0 && v < n && reach[v] < own_level {
                scratch.bfs_queue.push(u as u32);
                break;
            }
        }
    }

    let mut head = 0usize;
    while head < scratch.bfs_queue.len() {
        let u = scratch.bfs_queue[head] as usize;
        head += 1;
        let cur = reach[u];
        if cur < 2 {
            continue;
        }
        let next_level = cur - 1;
        let begin = unsafe { *offsets.get_unchecked(u) as usize };
        let end = unsafe { *offsets.get_unchecked(u + 1) as usize };
        for ei in begin..end {
            let v = unsafe { *edges.get_unchecked(ei) as usize };
            if v > 0 && v < n && reach[v] < next_level {
                reach[v] = next_level;
                scratch.bfs_queue.push(v as u32);
            }
        }
    }

    Ok(())
}
