// SPDX-License-Identifier: GPL-3.0-only

pub struct DofTradeArena {
    pub max_slots: usize,
}

impl DofTradeArena {
    pub fn new(max_slots: usize) -> Option<Self> {
        if max_slots == 0 || max_slots > 65_536 {
            return None;
        }
        Some(Self { max_slots })
    }
}

#[inline(always)]
fn great_circle(lat_a: f32, lon_a: f32, lat_b: f32, lon_b: f32) -> f32 {
    const RADIANS: f32 = std::f32::consts::PI / 180.0;
    let phi_a = lat_a * RADIANS;
    let phi_b = lat_b * RADIANS;
    let delta_phi = (lat_b - lat_a) * RADIANS;
    let delta_lambda = (lon_b - lon_a) * RADIANS;
    let sin_dp = (delta_phi * 0.5).sin();
    let sin_dl = (delta_lambda * 0.5).sin();
    let haversine = sin_dp * sin_dp + phi_a.cos() * phi_b.cos() * sin_dl * sin_dl;
    6371.0 * 2.0 * haversine.max(0.0).sqrt().min(1.0).asin()
}

#[allow(clippy::too_many_arguments)]
pub fn update_distances(
    _arena: &mut DofTradeArena,
    nation_ids: &[i32],
    lats: &[f32],
    lons: &[f32],
    dirty_indices: &[i32],
    full_rebuild: bool,
    stride: usize,
    out_dist_table: &mut [f32],
) -> Result<(), i32> {
    let count = nation_ids.len();
    if lats.len() != count || lons.len() != count || stride < count {
        return Err(-2);
    }
    if count == 0 {
        return Ok(());
    }
    if out_dist_table.len() < stride * stride {
        return Err(-3);
    }

    if full_rebuild {
        for i in 0..count {
            let aid = nation_ids[i];
            if aid < 0 || (aid as usize) >= stride {
                continue;
            }
            let a_u = aid as usize;
            let a_lat = lats[i];
            let a_lon = lons[i];
            for j in 0..count {
                let bid = nation_ids[j];
                if bid < aid || (bid as usize) >= stride {
                    continue;
                }
                let b_u = bid as usize;
                let km = great_circle(a_lat, a_lon, lats[j], lons[j]);
                out_dist_table[a_u * stride + b_u] = km;
                out_dist_table[b_u * stride + a_u] = km;
            }
        }
    } else {
        for &di in dirty_indices {
            if di < 0 || (di as usize) >= count {
                return Err(-2);
            }
            let i = di as usize;
            let aid = nation_ids[i];
            if aid < 0 || (aid as usize) >= stride {
                continue;
            }
            let a_u = aid as usize;
            let a_lat = lats[i];
            let a_lon = lons[i];
            for j in 0..count {
                let bid = nation_ids[j];
                if bid < 0 || (bid as usize) >= stride {
                    continue;
                }
                let b_u = bid as usize;
                let km = great_circle(a_lat, a_lon, lats[j], lons[j]);
                out_dist_table[a_u * stride + b_u] = km;
                out_dist_table[b_u * stride + a_u] = km;
            }
        }
    }

    Ok(())
}

/// Bit-exact `TradeNetwork.PartnerAffinity` across buyer x seller matrix.
/// `pair_flags`: bit 0 = has_relation, bit 1 = blocked (AtWar || Embargo),
/// bit 2 = shared_customs_union, bit 3 = buyer_has_customs_union, bit 4 = shares_land_border.
/// `pair_stance`: 1 = Allied, 2 = Rival, 0 = other.
#[allow(clippy::too_many_arguments)]
pub fn compute_affinity(
    _arena: &mut DofTradeArena,
    buyer_ids: &[i32],
    _buyer_map_idx: &[u16],
    buyer_reach: &[f32],
    seller_ids: &[i32],
    _seller_map_idx: &[u16],
    seller_reach: &[f32],
    dist_table: &[f32],
    dist_stride: usize,
    dist_nation_count: usize,
    pair_opinion: &[f32],
    pair_stance: &[u8],
    pair_flags: &[u8],
    pair_stride: usize,
    half_reach_km: f32,
    customs_inside: f32,
    customs_outside: f32,
    out_affinity: &mut [f32],
    out_stride: usize,
) -> Result<(), i32> {
    let b_cnt = buyer_ids.len();
    let s_cnt = seller_ids.len();
    if buyer_reach.len() != b_cnt || seller_reach.len() != s_cnt {
        return Err(-2);
    }
    if b_cnt == 0 || s_cnt == 0 {
        return Ok(());
    }
    if out_stride < s_cnt || out_affinity.len() < b_cnt * out_stride {
        return Err(-3);
    }
    if pair_stride < s_cnt
        || pair_opinion.len() < b_cnt * pair_stride
        || pair_stance.len() < b_cnt * pair_stride
        || pair_flags.len() < b_cnt * pair_stride
    {
        return Err(-2);
    }

    for bi in 0..b_cnt {
        let bid = buyer_ids[bi];
        let b_reach = buyer_reach[bi];
        let p_row = bi * pair_stride;
        let o_row = bi * out_stride;

        for si in 0..s_cnt {
            let p_idx = p_row + si;
            let flags = unsafe { *pair_flags.get_unchecked(p_idx) };
            if (flags & 2) != 0 {
                out_affinity[o_row + si] = 0.0;
                continue;
            }

            let mut score = 1.0f32;
            if (flags & 1) != 0 {
                let op = unsafe { *pair_opinion.get_unchecked(p_idx) };
                let clamped = (1.0f32 + op / 160.0f32).clamp(0.35f32, 1.65f32);
                score *= clamped;
                let stance = unsafe { *pair_stance.get_unchecked(p_idx) };
                if stance == 1 {
                    score *= 1.30f32;
                } else if stance == 2 {
                    score *= 0.68f32;
                }
            }

            if (flags & 8) != 0 {
                score *= if (flags & 4) != 0 {
                    customs_inside
                } else {
                    customs_outside
                };
            }

            if (flags & 16) == 0 {
                let sid = seller_ids[si];
                let mut dist = 20_000.0f32;
                if bid >= 0
                    && sid >= 0
                    && (bid as usize) < dist_nation_count
                    && (sid as usize) < dist_nation_count
                {
                    let idx = (bid as usize) * dist_stride + (sid as usize);
                    if idx < dist_table.len() {
                        let d = dist_table[idx];
                        if d > 0.0 {
                            dist = d;
                        }
                    }
                }
                let span = dist / half_reach_km;
                let falloff = 1.0f32 / (1.0f32 + span * span);
                let reach = (b_reach + seller_reach[si]) * 0.5f32;
                let term = falloff * reach;
                score *= term;
            }

            out_affinity[o_row + si] = score;
        }
    }

    Ok(())
}
