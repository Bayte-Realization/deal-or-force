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

/// Bit-exact `TradeNetwork.PartnerAffinityOrBlocked` across buyer x seller matrix.
/// `pair_flags`:
///   bit 0 (1):  has_relation
///   bit 1 (2):  blocked (AtWar || Embargo) -> writes `-1.0`
///   bit 2 (4):  shared_customs_union
///   bit 3 (8):  buyer_has_customs_union
///   bit 4 (16): shares_land_border
///   bit 5 (32): self pair (buyer == seller) -> writes `0.0`
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
    use crate::simd::clamp_f32;

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

        #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
        let mut si = if std::arch::is_x86_feature_detected!("avx2") {
            unsafe {
                compute_affinity_row_avx2(
                    bid,
                    b_reach,
                    seller_ids,
                    seller_reach,
                    dist_table,
                    dist_stride,
                    dist_nation_count,
                    &pair_opinion[p_row..p_row + s_cnt],
                    &pair_stance[p_row..p_row + s_cnt],
                    &pair_flags[p_row..p_row + s_cnt],
                    half_reach_km,
                    customs_inside,
                    customs_outside,
                    &mut out_affinity[o_row..o_row + s_cnt],
                )
            }
        } else {
            unsafe {
                compute_affinity_row_sse2(
                    bid,
                    b_reach,
                    seller_ids,
                    seller_reach,
                    dist_table,
                    dist_stride,
                    dist_nation_count,
                    &pair_opinion[p_row..p_row + s_cnt],
                    &pair_stance[p_row..p_row + s_cnt],
                    &pair_flags[p_row..p_row + s_cnt],
                    half_reach_km,
                    customs_inside,
                    customs_outside,
                    &mut out_affinity[o_row..o_row + s_cnt],
                )
            }
        };

        #[cfg(target_arch = "aarch64")]
        let mut si = if std::arch::is_aarch64_feature_detected!("neon") {
            unsafe {
                compute_affinity_row_neon(
                    bid,
                    b_reach,
                    seller_ids,
                    seller_reach,
                    dist_table,
                    dist_stride,
                    dist_nation_count,
                    &pair_opinion[p_row..p_row + s_cnt],
                    &pair_stance[p_row..p_row + s_cnt],
                    &pair_flags[p_row..p_row + s_cnt],
                    half_reach_km,
                    customs_inside,
                    customs_outside,
                    &mut out_affinity[o_row..o_row + s_cnt],
                )
            }
        } else {
            0
        };

        #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
        let mut si = 0usize;

        while si < s_cnt {
            let p_idx = p_row + si;
            let flags = unsafe { *pair_flags.get_unchecked(p_idx) };
            if (flags & 32) != 0 {
                out_affinity[o_row + si] = 0.0f32;
                si += 1;
                continue;
            }
            if (flags & 2) != 0 {
                out_affinity[o_row + si] = -1.0f32;
                si += 1;
                continue;
            }

            let mut score = 1.0f32;
            if (flags & 1) != 0 {
                let op = unsafe { *pair_opinion.get_unchecked(p_idx) };
                let clamped = clamp_f32(1.0f32 + op / 160.0f32, 0.35f32, 1.65f32);
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
                let dist = lookup_dist(bid, sid, dist_table, dist_stride, dist_nation_count);
                let span = dist / half_reach_km;
                let falloff = 1.0f32 / (1.0f32 + span * span);
                let reach = (b_reach + seller_reach[si]) * 0.5f32;
                let term = falloff * reach;
                score *= term;
            }

            out_affinity[o_row + si] = score;
            si += 1;
        }
    }

    Ok(())
}

#[inline(always)]
fn lookup_dist(
    bid: i32,
    sid: i32,
    dist_table: &[f32],
    dist_stride: usize,
    dist_nation_count: usize,
) -> f32 {
    if bid >= 0
        && sid >= 0
        && (bid as usize) < dist_nation_count
        && (sid as usize) < dist_nation_count
    {
        let idx = (bid as usize) * dist_stride + (sid as usize);
        if idx < dist_table.len() {
            let d = dist_table[idx];
            if d > 0.0f32 {
                return d;
            }
        }
    }
    12_000.0f32
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn compute_affinity_row_avx2(
    bid: i32,
    b_reach: f32,
    seller_ids: &[i32],
    seller_reach: &[f32],
    dist_table: &[f32],
    dist_stride: usize,
    dist_nation_count: usize,
    pair_opinion: &[f32],
    pair_stance: &[u8],
    pair_flags: &[u8],
    half_reach_km: f32,
    customs_inside: f32,
    customs_outside: f32,
    out_affinity: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_avx2::clamp;

    let s_cnt = seller_ids.len();
    let zero = _mm256_setzero_ps();
    let neg1 = _mm256_set1_ps(-1.0f32);
    let one = _mm256_set1_ps(1.0f32);
    let c160 = _mm256_set1_ps(160.0f32);
    let lo_op = _mm256_set1_ps(0.35f32);
    let hi_op = _mm256_set1_ps(1.65f32);
    let allied_v = _mm256_set1_ps(1.30f32);
    let rival_v = _mm256_set1_ps(0.68f32);
    let cu_in = _mm256_set1_ps(customs_inside);
    let cu_out = _mm256_set1_ps(customs_outside);
    let hr_v = _mm256_set1_ps(half_reach_km);
    let br_v = _mm256_set1_ps(b_reach);
    let half = _mm256_set1_ps(0.5f32);

    let izero = _mm256_setzero_si256();
    let m1 = _mm256_set1_epi32(1);
    let m2 = _mm256_set1_epi32(2);
    let m4 = _mm256_set1_epi32(4);
    let m8 = _mm256_set1_epi32(8);
    let m16 = _mm256_set1_epi32(16);
    let m32 = _mm256_set1_epi32(32);
    let s1 = _mm256_set1_epi32(1);
    let s2 = _mm256_set1_epi32(2);

    let mut d_buf = [0.0f32; 8];
    let mut si = 0usize;
    while si + 8 <= s_cnt {
        for k in 0..8 {
            d_buf[k] = lookup_dist(bid, seller_ids[si + k], dist_table, dist_stride, dist_nation_count);
        }
        let f_lo = std::ptr::read_unaligned(pair_flags.as_ptr().add(si) as *const i32);
        let f_hi = std::ptr::read_unaligned(pair_flags.as_ptr().add(si + 4) as *const i32);
        let fv = _mm256_cvtepu8_epi32(_mm_set_epi32(0, 0, f_hi, f_lo));

        let t_lo = std::ptr::read_unaligned(pair_stance.as_ptr().add(si) as *const i32);
        let t_hi = std::ptr::read_unaligned(pair_stance.as_ptr().add(si + 4) as *const i32);
        let tv = _mm256_cvtepu8_epi32(_mm_set_epi32(0, 0, t_hi, t_lo));

        let op = _mm256_loadu_ps(pair_opinion.as_ptr().add(si));
        let sr = _mm256_loadu_ps(seller_reach.as_ptr().add(si));
        let dist = _mm256_loadu_ps(d_buf.as_ptr());

        let op_term = clamp(_mm256_add_ps(one, _mm256_div_ps(op, c160)), lo_op, hi_op);
        let is_allied = _mm256_castsi256_ps(_mm256_cmpeq_epi32(tv, s1));
        let is_rival = _mm256_castsi256_ps(_mm256_cmpeq_epi32(tv, s2));
        let stance_mul = _mm256_blendv_ps(_mm256_blendv_ps(one, rival_v, is_rival), allied_v, is_allied);

        let has_rel = _mm256_castsi256_ps(_mm256_cmpgt_epi32(_mm256_and_si256(fv, m1), izero));
        let mut score = _mm256_blendv_ps(one, _mm256_mul_ps(_mm256_mul_ps(one, op_term), stance_mul), has_rel);

        let shared_cu = _mm256_castsi256_ps(_mm256_cmpgt_epi32(_mm256_and_si256(fv, m4), izero));
        let buyer_cu = _mm256_castsi256_ps(_mm256_cmpgt_epi32(_mm256_and_si256(fv, m8), izero));
        let cu_mul = _mm256_blendv_ps(cu_out, cu_in, shared_cu);
        score = _mm256_blendv_ps(score, _mm256_mul_ps(score, cu_mul), buyer_cu);

        let border = _mm256_castsi256_ps(_mm256_cmpgt_epi32(_mm256_and_si256(fv, m16), izero));
        let span = _mm256_div_ps(dist, hr_v);
        let falloff = _mm256_div_ps(one, _mm256_add_ps(one, _mm256_mul_ps(span, span)));
        let reach = _mm256_mul_ps(_mm256_add_ps(br_v, sr), half);
        let term = _mm256_mul_ps(falloff, reach);
        score = _mm256_blendv_ps(_mm256_mul_ps(score, term), score, border);

        let is_blocked = _mm256_castsi256_ps(_mm256_cmpgt_epi32(_mm256_and_si256(fv, m2), izero));
        let is_self = _mm256_castsi256_ps(_mm256_cmpgt_epi32(_mm256_and_si256(fv, m32), izero));
        score = _mm256_blendv_ps(score, neg1, is_blocked);
        score = _mm256_blendv_ps(score, zero, is_self);

        _mm256_storeu_ps(out_affinity.as_mut_ptr().add(si), score);
        si += 8;
    }
    si
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn compute_affinity_row_sse2(
    bid: i32,
    b_reach: f32,
    seller_ids: &[i32],
    seller_reach: &[f32],
    dist_table: &[f32],
    dist_stride: usize,
    dist_nation_count: usize,
    pair_opinion: &[f32],
    pair_stance: &[u8],
    pair_flags: &[u8],
    half_reach_km: f32,
    customs_inside: f32,
    customs_outside: f32,
    out_affinity: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_sse2::{clamp, select};

    let s_cnt = seller_ids.len();
    let zero = _mm_setzero_ps();
    let neg1 = _mm_set1_ps(-1.0f32);
    let one = _mm_set1_ps(1.0f32);
    let c160 = _mm_set1_ps(160.0f32);
    let lo_op = _mm_set1_ps(0.35f32);
    let hi_op = _mm_set1_ps(1.65f32);
    let allied_v = _mm_set1_ps(1.30f32);
    let rival_v = _mm_set1_ps(0.68f32);
    let cu_in = _mm_set1_ps(customs_inside);
    let cu_out = _mm_set1_ps(customs_outside);
    let hr_v = _mm_set1_ps(half_reach_km);
    let br_v = _mm_set1_ps(b_reach);
    let half = _mm_set1_ps(0.5f32);

    let izero = _mm_setzero_si128();
    let m1 = _mm_set1_epi32(1);
    let m2 = _mm_set1_epi32(2);
    let m4 = _mm_set1_epi32(4);
    let m8 = _mm_set1_epi32(8);
    let m16 = _mm_set1_epi32(16);
    let m32 = _mm_set1_epi32(32);
    let s1 = _mm_set1_epi32(1);
    let s2 = _mm_set1_epi32(2);

    let mut d_buf = [0.0f32; 4];
    let mut si = 0usize;
    while si + 4 <= s_cnt {
        for k in 0..4 {
            d_buf[k] = lookup_dist(bid, seller_ids[si + k], dist_table, dist_stride, dist_nation_count);
        }
        let f_raw = std::ptr::read_unaligned(pair_flags.as_ptr().add(si) as *const i32);
        let fv = _mm_unpacklo_epi16(_mm_unpacklo_epi8(_mm_cvtsi32_si128(f_raw), izero), izero);

        let t_raw = std::ptr::read_unaligned(pair_stance.as_ptr().add(si) as *const i32);
        let tv = _mm_unpacklo_epi16(_mm_unpacklo_epi8(_mm_cvtsi32_si128(t_raw), izero), izero);

        let op = _mm_loadu_ps(pair_opinion.as_ptr().add(si));
        let sr = _mm_loadu_ps(seller_reach.as_ptr().add(si));
        let dist = _mm_loadu_ps(d_buf.as_ptr());

        let op_term = clamp(_mm_add_ps(one, _mm_div_ps(op, c160)), lo_op, hi_op);
        let is_allied = _mm_castsi128_ps(_mm_cmpeq_epi32(tv, s1));
        let is_rival = _mm_castsi128_ps(_mm_cmpeq_epi32(tv, s2));
        let stance_mul = select(is_allied, allied_v, select(is_rival, rival_v, one));

        let has_rel = _mm_castsi128_ps(_mm_cmpgt_epi32(_mm_and_si128(fv, m1), izero));
        let mut score = select(has_rel, _mm_mul_ps(_mm_mul_ps(one, op_term), stance_mul), one);

        let shared_cu = _mm_castsi128_ps(_mm_cmpgt_epi32(_mm_and_si128(fv, m4), izero));
        let buyer_cu = _mm_castsi128_ps(_mm_cmpgt_epi32(_mm_and_si128(fv, m8), izero));
        let cu_mul = select(shared_cu, cu_in, cu_out);
        score = select(buyer_cu, _mm_mul_ps(score, cu_mul), score);

        let border = _mm_castsi128_ps(_mm_cmpgt_epi32(_mm_and_si128(fv, m16), izero));
        let span = _mm_div_ps(dist, hr_v);
        let falloff = _mm_div_ps(one, _mm_add_ps(one, _mm_mul_ps(span, span)));
        let reach = _mm_mul_ps(_mm_add_ps(br_v, sr), half);
        let term = _mm_mul_ps(falloff, reach);
        score = select(border, score, _mm_mul_ps(score, term));

        let is_blocked = _mm_castsi128_ps(_mm_cmpgt_epi32(_mm_and_si128(fv, m2), izero));
        let is_self = _mm_castsi128_ps(_mm_cmpgt_epi32(_mm_and_si128(fv, m32), izero));
        score = select(is_blocked, neg1, score);
        score = select(is_self, zero, score);

        _mm_storeu_ps(out_affinity.as_mut_ptr().add(si), score);
        si += 4;
    }
    si
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn compute_affinity_row_neon(
    bid: i32,
    b_reach: f32,
    seller_ids: &[i32],
    seller_reach: &[f32],
    dist_table: &[f32],
    dist_stride: usize,
    dist_nation_count: usize,
    pair_opinion: &[f32],
    pair_stance: &[u8],
    pair_flags: &[u8],
    half_reach_km: f32,
    customs_inside: f32,
    customs_outside: f32,
    out_affinity: &mut [f32],
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::clamp;

    let s_cnt = seller_ids.len();
    let zero = vdupq_n_f32(0.0f32);
    let neg1 = vdupq_n_f32(-1.0f32);
    let one = vdupq_n_f32(1.0f32);
    let c160 = vdupq_n_f32(160.0f32);
    let lo_op = vdupq_n_f32(0.35f32);
    let hi_op = vdupq_n_f32(1.65f32);
    let allied_v = vdupq_n_f32(1.30f32);
    let rival_v = vdupq_n_f32(0.68f32);
    let cu_in = vdupq_n_f32(customs_inside);
    let cu_out = vdupq_n_f32(customs_outside);
    let hr_v = vdupq_n_f32(half_reach_km);
    let br_v = vdupq_n_f32(b_reach);
    let half = vdupq_n_f32(0.5f32);

    let m1 = vdupq_n_u32(1);
    let m2 = vdupq_n_u32(2);
    let m4 = vdupq_n_u32(4);
    let m8 = vdupq_n_u32(8);
    let m16 = vdupq_n_u32(16);
    let m32 = vdupq_n_u32(32);
    let s1 = vdupq_n_u32(1);
    let s2 = vdupq_n_u32(2);

    let mut d_buf = [0.0f32; 4];
    let mut si = 0usize;
    while si + 4 <= s_cnt {
        for k in 0..4 {
            d_buf[k] = lookup_dist(bid, seller_ids[si + k], dist_table, dist_stride, dist_nation_count);
        }
        let fv = vld1q_u32(
            [
                pair_flags[si] as u32,
                pair_flags[si + 1] as u32,
                pair_flags[si + 2] as u32,
                pair_flags[si + 3] as u32,
            ]
            .as_ptr(),
        );
        let tv = vld1q_u32(
            [
                pair_stance[si] as u32,
                pair_stance[si + 1] as u32,
                pair_stance[si + 2] as u32,
                pair_stance[si + 3] as u32,
            ]
            .as_ptr(),
        );

        let op = vld1q_f32(pair_opinion.as_ptr().add(si));
        let sr = vld1q_f32(seller_reach.as_ptr().add(si));
        let dist = vld1q_f32(d_buf.as_ptr());

        let op_term = clamp(vaddq_f32(one, vdivq_f32(op, c160)), lo_op, hi_op);
        let is_allied = vceqq_u32(tv, s1);
        let is_rival = vceqq_u32(tv, s2);
        let stance_mul = vbslq_f32(is_allied, allied_v, vbslq_f32(is_rival, rival_v, one));

        let has_rel = vtstq_u32(fv, m1);
        let mut score = vbslq_f32(has_rel, vmulq_f32(vmulq_f32(one, op_term), stance_mul), one);

        let shared_cu = vtstq_u32(fv, m4);
        let buyer_cu = vtstq_u32(fv, m8);
        let cu_mul = vbslq_f32(shared_cu, cu_in, cu_out);
        score = vbslq_f32(buyer_cu, vmulq_f32(score, cu_mul), score);

        let border = vtstq_u32(fv, m16);
        let span = vdivq_f32(dist, hr_v);
        let falloff = vdivq_f32(one, vaddq_f32(one, vmulq_f32(span, span)));
        let reach = vmulq_f32(vaddq_f32(br_v, sr), half);
        let term = vmulq_f32(falloff, reach);
        score = vbslq_f32(border, score, vmulq_f32(score, term));

        let is_blocked = vtstq_u32(fv, m2);
        let is_self = vtstq_u32(fv, m32);
        score = vbslq_f32(is_blocked, neg1, score);
        score = vbslq_f32(is_self, zero, score);

        vst1q_f32(out_affinity.as_mut_ptr().add(si), score);
        si += 4;
    }
    si
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::simd::clamp_f32;

    #[test]
    fn compute_affinity_simd_matches_scalar_bit_exact() {
        let mut arena = DofTradeArena::new(64).unwrap();
        let b_cnt = 3;
        let s_cnt = 19;
        let b_ids = vec![0, 1, 2];
        let s_ids: Vec<i32> = (0..s_cnt as i32).collect();
        let b_map: Vec<u16> = vec![1, 2, 3];
        let s_map: Vec<u16> = (1..=s_cnt as u16).collect();
        let b_reach = vec![0.6f32, 0.9f32, 1.35f32];
        let s_reach: Vec<f32> = (0..s_cnt).map(|s| 0.45f32 + (s as f32) * 0.05).collect();

        let stride = s_cnt;
        let mut dist_table = vec![0.0f32; b_cnt * stride];
        for b in 0..b_cnt {
            for s in 0..s_cnt {
                dist_table[b * stride + s] = if s % 7 == 6 { 0.0 } else { 400.0 + (b * 300 + s * 250) as f32 };
            }
        }

        let mut op = vec![0.0f32; b_cnt * s_cnt];
        let mut st = vec![0u8; b_cnt * s_cnt];
        let mut fl = vec![0u8; b_cnt * s_cnt];
        for b in 0..b_cnt {
            for s in 0..s_cnt {
                let idx = b * s_cnt + s;
                op[idx] = -120.0 + (idx as f32) * 7.5;
                st[idx] = (idx % 3) as u8;
                let mut f = if idx % 3 != 0 { 1 } else { 0 };
                if idx % 11 == 2 { f |= 2; }
                if idx % 5 == 0 { f |= 8; }
                if idx % 10 == 0 { f |= 4; }
                if idx % 6 == 0 { f |= 16; }
                if b as i32 == s as i32 { f |= 32; }
                fl[idx] = f;
            }
        }

        let mut out = vec![0.0f32; b_cnt * s_cnt];
        compute_affinity(
            &mut arena, &b_ids, &b_map, &b_reach, &s_ids, &s_map, &s_reach,
            &dist_table, stride, s_cnt, &op, &st, &fl, s_cnt,
            4200.0, 1.35, 0.85, &mut out, s_cnt,
        )
        .unwrap();

        for b in 0..b_cnt {
            for s in 0..s_cnt {
                let idx = b * s_cnt + s;
                let f = fl[idx];
                let expected = if (f & 32) != 0 {
                    0.0f32
                } else if (f & 2) != 0 {
                    -1.0f32
                } else {
                    let mut sc = 1.0f32;
                    if (f & 1) != 0 {
                        sc *= clamp_f32(1.0f32 + op[idx] / 160.0f32, 0.35f32, 1.65f32);
                        if st[idx] == 1 { sc *= 1.30f32; }
                        else if st[idx] == 2 { sc *= 0.68f32; }
                    }
                    if (f & 8) != 0 {
                        sc *= if (f & 4) != 0 { 1.35f32 } else { 0.85f32 };
                    }
                    if (f & 16) == 0 {
                        let d = if dist_table[b * stride + s] > 0.0 { dist_table[b * stride + s] } else { 12_000.0f32 };
                        let span = d / 4200.0f32;
                        let falloff = 1.0f32 / (1.0f32 + span * span);
                        let reach = (b_reach[b] + s_reach[s]) * 0.5f32;
                        sc *= falloff * reach;
                    }
                    sc
                };
                assert_eq!(out[idx].to_bits(), expected.to_bits(), "b={b}, s={s}");
            }
        }
    }
}
