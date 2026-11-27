// SPDX-License-Identifier: GPL-3.0-only
use crate::simd::{clamp01_f32, clamp_f32, m_max_f32, m_min_f32};

const PAIR_FLAG_CLAIM_OR_HOME: u8 = 1 << 0;
const PAIR_FLAG_BOTH_GP: u8 = 1 << 1;
const PAIR_FLAG_IMAGES_ON: u8 = 1 << 2;

const STANCE_AT_WAR: u8 = 4;

const ACTION_ELIGIBLE_FRIEND: u8 = 1 << 0;
const ACTION_LIFT_EMBARGO: u8 = 1 << 1;
const ACTION_RECONCILE_RIVAL: u8 = 1 << 2;
const ACTION_MISSION_CANDIDATE: u8 = 1 << 3;
const ACTION_TRADE_OK: u8 = 1 << 4;
const ACTION_PACT_OK: u8 = 1 << 5;
const ACTION_ALLIANCE_OK: u8 = 1 << 6;
const ACTION_RIVAL_CANDIDATE: u8 = 1 << 7;

#[allow(clippy::too_many_arguments)]
pub fn pair_score_batch(
    left_econ: &[f32],
    left_auth: &[f32],
    right_econ: &[f32],
    right_auth: &[f32],
    esteem_ab: &[f32],
    esteem_ba: &[f32],
    claim_left: &[f32],
    claim_right: &[f32],
    claim_bonus: &[f32],
    bloc_bias: &[f32],
    base_reason_sum: &[f32],
    witness_against: &[f32],
    witness_for: &[f32],
    identity_affinity: &[f32],
    strength_ratio: &[f32],
    fear_reach: &[f32],
    pair_flags: &[u8],
    ideology_weight: f32,
    social_weight: f32,
    geopolitics_ramp: f32,
    third_party_floor: f32,
    third_party_ceiling: f32,
    out_politics: &mut [f32],
    out_social: &mut [f32],
    out_claims: &mut [f32],
    out_geopolitics: &mut [f32],
    out_opinion: &mut [f32],
    out_fear: &mut [f32],
) -> Result<(), i32> {
    let n = left_econ.len();
    if left_auth.len() < n
        || right_econ.len() < n
        || right_auth.len() < n
        || esteem_ab.len() < n
        || esteem_ba.len() < n
        || claim_left.len() < n
        || claim_right.len() < n
        || claim_bonus.len() < n
        || bloc_bias.len() < n
        || base_reason_sum.len() < n
        || witness_against.len() < n
        || witness_for.len() < n
        || identity_affinity.len() < n
        || strength_ratio.len() < n
        || fear_reach.len() < n
        || pair_flags.len() < n
    {
        return Err(-2);
    }
    if out_politics.len() < n
        || out_social.len() < n
        || out_claims.len() < n
        || out_geopolitics.len() < n
        || out_opinion.len() < n
        || out_fear.len() < n
    {
        return Err(-3);
    }
    if n == 0 {
        return Ok(());
    }

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    {
        if std::arch::is_x86_feature_detected!("avx2") {
            let done = unsafe {
                pair_score_batch_avx2(
                    left_econ,
                    left_auth,
                    right_econ,
                    right_auth,
                    esteem_ab,
                    esteem_ba,
                    claim_left,
                    claim_right,
                    claim_bonus,
                    bloc_bias,
                    base_reason_sum,
                    witness_against,
                    witness_for,
                    identity_affinity,
                    strength_ratio,
                    fear_reach,
                    pair_flags,
                    ideology_weight,
                    social_weight,
                    geopolitics_ramp,
                    third_party_floor,
                    third_party_ceiling,
                    out_politics,
                    out_social,
                    out_claims,
                    out_geopolitics,
                    out_opinion,
                    out_fear,
                )
            };
            pair_score_batch_scalar(
                done,
                n,
                left_econ,
                left_auth,
                right_econ,
                right_auth,
                esteem_ab,
                esteem_ba,
                claim_left,
                claim_right,
                claim_bonus,
                bloc_bias,
                base_reason_sum,
                witness_against,
                witness_for,
                identity_affinity,
                strength_ratio,
                fear_reach,
                pair_flags,
                ideology_weight,
                social_weight,
                geopolitics_ramp,
                third_party_floor,
                third_party_ceiling,
                out_politics,
                out_social,
                out_claims,
                out_geopolitics,
                out_opinion,
                out_fear,
            );
            return Ok(());
        }
        let done = unsafe {
            pair_score_batch_sse2(
                left_econ,
                left_auth,
                right_econ,
                right_auth,
                esteem_ab,
                esteem_ba,
                claim_left,
                claim_right,
                claim_bonus,
                bloc_bias,
                base_reason_sum,
                witness_against,
                witness_for,
                identity_affinity,
                strength_ratio,
                fear_reach,
                pair_flags,
                ideology_weight,
                social_weight,
                geopolitics_ramp,
                third_party_floor,
                third_party_ceiling,
                out_politics,
                out_social,
                out_claims,
                out_geopolitics,
                out_opinion,
                out_fear,
            )
        };
        pair_score_batch_scalar(
            done,
            n,
            left_econ,
            left_auth,
            right_econ,
            right_auth,
            esteem_ab,
            esteem_ba,
            claim_left,
            claim_right,
            claim_bonus,
            bloc_bias,
            base_reason_sum,
            witness_against,
            witness_for,
            identity_affinity,
            strength_ratio,
            fear_reach,
            pair_flags,
            ideology_weight,
            social_weight,
            geopolitics_ramp,
            third_party_floor,
            third_party_ceiling,
            out_politics,
            out_social,
            out_claims,
            out_geopolitics,
            out_opinion,
            out_fear,
        );
        return Ok(());
    }

    #[cfg(target_arch = "aarch64")]
    {
        let done = if std::arch::is_aarch64_feature_detected!("neon") {
            unsafe {
                pair_score_batch_neon(
                    left_econ,
                    left_auth,
                    right_econ,
                    right_auth,
                    esteem_ab,
                    esteem_ba,
                    claim_left,
                    claim_right,
                    claim_bonus,
                    bloc_bias,
                    base_reason_sum,
                    witness_against,
                    witness_for,
                    identity_affinity,
                    strength_ratio,
                    fear_reach,
                    pair_flags,
                    ideology_weight,
                    social_weight,
                    geopolitics_ramp,
                    third_party_floor,
                    third_party_ceiling,
                    out_politics,
                    out_social,
                    out_claims,
                    out_geopolitics,
                    out_opinion,
                    out_fear,
                )
            }
        } else {
            0
        };
        pair_score_batch_scalar(
            done,
            n,
            left_econ,
            left_auth,
            right_econ,
            right_auth,
            esteem_ab,
            esteem_ba,
            claim_left,
            claim_right,
            claim_bonus,
            bloc_bias,
            base_reason_sum,
            witness_against,
            witness_for,
            identity_affinity,
            strength_ratio,
            fear_reach,
            pair_flags,
            ideology_weight,
            social_weight,
            geopolitics_ramp,
            third_party_floor,
            third_party_ceiling,
            out_politics,
            out_social,
            out_claims,
            out_geopolitics,
            out_opinion,
            out_fear,
        );
        return Ok(());
    }

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    {
        pair_score_batch_scalar(
            0,
            n,
            left_econ,
            left_auth,
            right_econ,
            right_auth,
            esteem_ab,
            esteem_ba,
            claim_left,
            claim_right,
            claim_bonus,
            bloc_bias,
            base_reason_sum,
            witness_against,
            witness_for,
            identity_affinity,
            strength_ratio,
            fear_reach,
            pair_flags,
            ideology_weight,
            social_weight,
            geopolitics_ramp,
            third_party_floor,
            third_party_ceiling,
            out_politics,
            out_social,
            out_claims,
            out_geopolitics,
            out_opinion,
            out_fear,
        );
        Ok(())
    }
}

#[inline(always)]
#[allow(clippy::too_many_arguments)]
fn pair_score_batch_scalar(
    start: usize,
    end: usize,
    left_econ: &[f32],
    left_auth: &[f32],
    right_econ: &[f32],
    right_auth: &[f32],
    esteem_ab: &[f32],
    esteem_ba: &[f32],
    claim_left: &[f32],
    claim_right: &[f32],
    claim_bonus: &[f32],
    bloc_bias: &[f32],
    base_reason_sum: &[f32],
    witness_against: &[f32],
    witness_for: &[f32],
    identity_affinity: &[f32],
    strength_ratio: &[f32],
    fear_reach: &[f32],
    pair_flags: &[u8],
    ideology_weight: f32,
    social_weight: f32,
    geopolitics_ramp: f32,
    third_party_floor: f32,
    third_party_ceiling: f32,
    out_politics: &mut [f32],
    out_social: &mut [f32],
    out_claims: &mut [f32],
    out_geopolitics: &mut [f32],
    out_opinion: &mut [f32],
    out_fear: &mut [f32],
) {
    let ramp = clamp01_f32(geopolitics_ramp);
    let floor = m_min_f32(0.0f32, third_party_floor);
    let ceiling = m_max_f32(0.0f32, third_party_ceiling);
    let has_ideology = ideology_weight > 0.0f32;
    let has_ramp = ramp > 0.0f32;

    for i in start..end {
        let flags = pair_flags[i];
        let de = left_econ[i] - right_econ[i];
        let da = left_auth[i] - right_auth[i];
        let ideol_dist = clamp01_f32((de * de + da * da).sqrt() / 2.8284f32);
        let politics = if has_ideology {
            (0.38f32 - ideol_dist) * ideology_weight
        } else {
            0.0f32
        };

        let social = if (flags & PAIR_FLAG_IMAGES_ON) != 0 {
            (esteem_ab[i] + esteem_ba[i]) * 0.5f32 * social_weight
        } else {
            0.0f32
        };

        let claims = if (flags & PAIR_FLAG_CLAIM_OR_HOME) != 0 {
            let wl = claim_left[i];
            let wr = claim_right[i];
            let mx = m_max_f32(wl, wr);
            let mn = m_min_f32(wl, wr);
            let intensity = clamp01_f32(mx + mn * 0.5f32 + claim_bonus[i]);
            -(22.0f32 + 13.0f32 * intensity)
        } else {
            0.0f32
        };

        let mut geo = 0.0f32;
        if has_ramp {
            geo = bloc_bias[i] * 18.0f32 * ramp;
            if (flags & PAIR_FLAG_BOTH_GP) != 0 {
                let gp_dist = if ideol_dist < 1.0f32 { ideol_dist } else { 1.0f32 };
                geo -= (8.0f32 + 10.0f32 * gp_dist) * ramp;
            }
        }

        let mut total = base_reason_sum[i];
        let wa = witness_against[i];
        if wa < floor {
            total += floor - wa;
        }
        let wf = witness_for[i];
        if wf > ceiling {
            total -= wf - ceiling;
        }
        if politics.abs() >= 0.05f32 {
            total += politics;
        }
        let id_aff = identity_affinity[i];
        if id_aff >= 0.05f32 {
            total += id_aff;
        }
        if social.abs() >= 0.05f32 {
            total += social;
        }
        if claims.abs() >= 0.05f32 {
            total += claims;
        }
        if geo.abs() >= 0.05f32 {
            total += geo;
        }

        out_politics[i] = politics;
        out_social[i] = social;
        out_claims[i] = claims;
        out_geopolitics[i] = geo;
        out_opinion[i] = clamp_f32(total, -100.0f32, 100.0f32);
        out_fear[i] = clamp_f32(strength_ratio[i] - 1.0f32, 0.0f32, 2.0f32) * fear_reach[i];
    }
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn pair_score_batch_avx2(
    left_econ: &[f32],
    left_auth: &[f32],
    right_econ: &[f32],
    right_auth: &[f32],
    esteem_ab: &[f32],
    esteem_ba: &[f32],
    claim_left: &[f32],
    claim_right: &[f32],
    claim_bonus: &[f32],
    bloc_bias: &[f32],
    base_reason_sum: &[f32],
    witness_against: &[f32],
    witness_for: &[f32],
    identity_affinity: &[f32],
    strength_ratio: &[f32],
    fear_reach: &[f32],
    pair_flags: &[u8],
    ideology_weight: f32,
    social_weight: f32,
    geopolitics_ramp: f32,
    third_party_floor: f32,
    third_party_ceiling: f32,
    out_politics: &mut [f32],
    out_social: &mut [f32],
    out_claims: &mut [f32],
    out_geopolitics: &mut [f32],
    out_opinion: &mut [f32],
    out_fear: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_avx2::{abs, clamp, clamp01, m_max, m_min};

    let n = left_econ.len();
    let zero = _mm256_setzero_ps();
    let one = _mm256_set1_ps(1.0f32);
    let two = _mm256_set1_ps(2.0f32);
    let half = _mm256_set1_ps(0.5f32);
    let diag = _mm256_set1_ps(2.8284f32);
    let neutral_ideol = _mm256_set1_ps(0.38f32);
    let ideol_w = _mm256_set1_ps(ideology_weight);
    let soc_w = _mm256_set1_ps(social_weight);
    let has_ideol = ideology_weight > 0.0f32;
    let ramp_scalar = clamp01_f32(geopolitics_ramp);
    let has_ramp = ramp_scalar > 0.0f32;
    let ramp = _mm256_set1_ps(ramp_scalar);
    let c18 = _mm256_set1_ps(18.0f32);
    let c8 = _mm256_set1_ps(8.0f32);
    let c10 = _mm256_set1_ps(10.0f32);
    let c22 = _mm256_set1_ps(22.0f32);
    let c13 = _mm256_set1_ps(13.0f32);
    let eps05 = _mm256_set1_ps(0.05f32);
    let floor = _mm256_set1_ps(m_min_f32(0.0f32, third_party_floor));
    let ceiling = _mm256_set1_ps(m_max_f32(0.0f32, third_party_ceiling));
    let op_lo = _mm256_set1_ps(-100.0f32);
    let op_hi = _mm256_set1_ps(100.0f32);

    let bit0 = _mm256_set1_epi32(PAIR_FLAG_CLAIM_OR_HOME as i32);
    let bit1 = _mm256_set1_epi32(PAIR_FLAG_BOTH_GP as i32);
    let bit2 = _mm256_set1_epi32(PAIR_FLAG_IMAGES_ON as i32);

    let mut i = 0usize;
    while i + 8 <= n {
        let le = _mm256_loadu_ps(left_econ.as_ptr().add(i));
        let la = _mm256_loadu_ps(left_auth.as_ptr().add(i));
        let re = _mm256_loadu_ps(right_econ.as_ptr().add(i));
        let ra = _mm256_loadu_ps(right_auth.as_ptr().add(i));

        let de = _mm256_sub_ps(le, re);
        let da = _mm256_sub_ps(la, ra);
        let sq = _mm256_add_ps(_mm256_mul_ps(de, de), _mm256_mul_ps(da, da));
        let ideol_dist = clamp01(_mm256_div_ps(_mm256_sqrt_ps(sq), diag));
        let politics = if has_ideol {
            _mm256_mul_ps(_mm256_sub_ps(neutral_ideol, ideol_dist), ideol_w)
        } else {
            zero
        };

        // Load 8 u8 flags -> 8xi32
        let f_bytes = _mm_loadl_epi64(pair_flags.as_ptr().add(i) as *const __m128i);
        let f_i32 = _mm256_cvtepu8_epi32(f_bytes);
        let m_claim = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, bit0), bit0));
        let m_gp = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, bit1), bit1));
        let m_img = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, bit2), bit2));

        let eab = _mm256_loadu_ps(esteem_ab.as_ptr().add(i));
        let eba = _mm256_loadu_ps(esteem_ba.as_ptr().add(i));
        let social_val = _mm256_mul_ps(_mm256_mul_ps(_mm256_add_ps(eab, eba), half), soc_w);
        let social = _mm256_blendv_ps(zero, social_val, m_img);

        let cl = _mm256_loadu_ps(claim_left.as_ptr().add(i));
        let cr = _mm256_loadu_ps(claim_right.as_ptr().add(i));
        let cb = _mm256_loadu_ps(claim_bonus.as_ptr().add(i));
        let mx = m_max(cl, cr);
        let mn = m_min(cl, cr);
        let intensity = clamp01(_mm256_add_ps(_mm256_add_ps(mx, _mm256_mul_ps(mn, half)), cb));
        let claims_val = _mm256_sub_ps(zero, _mm256_add_ps(c22, _mm256_mul_ps(c13, intensity)));
        let claims = _mm256_blendv_ps(zero, claims_val, m_claim);

        let geo = if has_ramp {
            let bb = _mm256_loadu_ps(bloc_bias.as_ptr().add(i));
            let base_geo = _mm256_mul_ps(_mm256_mul_ps(bb, c18), ramp);
            let gp_dist = _mm256_blendv_ps(one, ideol_dist, _mm256_cmp_ps(ideol_dist, one, _CMP_LT_OQ));
            let gp_pen = _mm256_mul_ps(_mm256_add_ps(c8, _mm256_mul_ps(c10, gp_dist)), ramp);
            _mm256_blendv_ps(base_geo, _mm256_sub_ps(base_geo, gp_pen), m_gp)
        } else {
            zero
        };

        let mut total = _mm256_loadu_ps(base_reason_sum.as_ptr().add(i));
        let wa = _mm256_loadu_ps(witness_against.as_ptr().add(i));
        let wf = _mm256_loadu_ps(witness_for.as_ptr().add(i));
        total = _mm256_blendv_ps(
            total,
            _mm256_add_ps(total, _mm256_sub_ps(floor, wa)),
            _mm256_cmp_ps(wa, floor, _CMP_LT_OQ),
        );
        total = _mm256_blendv_ps(
            total,
            _mm256_sub_ps(total, _mm256_sub_ps(wf, ceiling)),
            _mm256_cmp_ps(wf, ceiling, _CMP_GT_OQ),
        );

        total = _mm256_blendv_ps(
            total,
            _mm256_add_ps(total, politics),
            _mm256_cmp_ps(abs(politics), eps05, _CMP_GE_OQ),
        );
        let id_aff = _mm256_loadu_ps(identity_affinity.as_ptr().add(i));
        total = _mm256_blendv_ps(
            total,
            _mm256_add_ps(total, id_aff),
            _mm256_cmp_ps(id_aff, eps05, _CMP_GE_OQ),
        );
        total = _mm256_blendv_ps(
            total,
            _mm256_add_ps(total, social),
            _mm256_cmp_ps(abs(social), eps05, _CMP_GE_OQ),
        );
        total = _mm256_blendv_ps(
            total,
            _mm256_add_ps(total, claims),
            _mm256_cmp_ps(abs(claims), eps05, _CMP_GE_OQ),
        );
        total = _mm256_blendv_ps(
            total,
            _mm256_add_ps(total, geo),
            _mm256_cmp_ps(abs(geo), eps05, _CMP_GE_OQ),
        );
        let opinion = clamp(total, op_lo, op_hi);

        let sr = _mm256_loadu_ps(strength_ratio.as_ptr().add(i));
        let fr = _mm256_loadu_ps(fear_reach.as_ptr().add(i));
        let fear = _mm256_mul_ps(clamp(_mm256_sub_ps(sr, one), zero, two), fr);

        _mm256_storeu_ps(out_politics.as_mut_ptr().add(i), politics);
        _mm256_storeu_ps(out_social.as_mut_ptr().add(i), social);
        _mm256_storeu_ps(out_claims.as_mut_ptr().add(i), claims);
        _mm256_storeu_ps(out_geopolitics.as_mut_ptr().add(i), geo);
        _mm256_storeu_ps(out_opinion.as_mut_ptr().add(i), opinion);
        _mm256_storeu_ps(out_fear.as_mut_ptr().add(i), fear);

        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn pair_score_batch_sse2(
    left_econ: &[f32],
    left_auth: &[f32],
    right_econ: &[f32],
    right_auth: &[f32],
    esteem_ab: &[f32],
    esteem_ba: &[f32],
    claim_left: &[f32],
    claim_right: &[f32],
    claim_bonus: &[f32],
    bloc_bias: &[f32],
    base_reason_sum: &[f32],
    witness_against: &[f32],
    witness_for: &[f32],
    identity_affinity: &[f32],
    strength_ratio: &[f32],
    fear_reach: &[f32],
    pair_flags: &[u8],
    ideology_weight: f32,
    social_weight: f32,
    geopolitics_ramp: f32,
    third_party_floor: f32,
    third_party_ceiling: f32,
    out_politics: &mut [f32],
    out_social: &mut [f32],
    out_claims: &mut [f32],
    out_geopolitics: &mut [f32],
    out_opinion: &mut [f32],
    out_fear: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_sse2::{abs, clamp, clamp01, m_max, m_min, select};

    let n = left_econ.len();
    let zero = _mm_setzero_ps();
    let one = _mm_set1_ps(1.0f32);
    let two = _mm_set1_ps(2.0f32);
    let half = _mm_set1_ps(0.5f32);
    let diag = _mm_set1_ps(2.8284f32);
    let neutral_ideol = _mm_set1_ps(0.38f32);
    let ideol_w = _mm_set1_ps(ideology_weight);
    let soc_w = _mm_set1_ps(social_weight);
    let has_ideol = ideology_weight > 0.0f32;
    let ramp_scalar = clamp01_f32(geopolitics_ramp);
    let has_ramp = ramp_scalar > 0.0f32;
    let ramp = _mm_set1_ps(ramp_scalar);
    let c18 = _mm_set1_ps(18.0f32);
    let c8 = _mm_set1_ps(8.0f32);
    let c10 = _mm_set1_ps(10.0f32);
    let c22 = _mm_set1_ps(22.0f32);
    let c13 = _mm_set1_ps(13.0f32);
    let eps05 = _mm_set1_ps(0.05f32);
    let floor = _mm_set1_ps(m_min_f32(0.0f32, third_party_floor));
    let ceiling = _mm_set1_ps(m_max_f32(0.0f32, third_party_ceiling));
    let op_lo = _mm_set1_ps(-100.0f32);
    let op_hi = _mm_set1_ps(100.0f32);

    let mut i = 0usize;
    while i + 4 <= n {
        let le = _mm_loadu_ps(left_econ.as_ptr().add(i));
        let la = _mm_loadu_ps(left_auth.as_ptr().add(i));
        let re = _mm_loadu_ps(right_econ.as_ptr().add(i));
        let ra = _mm_loadu_ps(right_auth.as_ptr().add(i));

        let de = _mm_sub_ps(le, re);
        let da = _mm_sub_ps(la, ra);
        let sq = _mm_add_ps(_mm_mul_ps(de, de), _mm_mul_ps(da, da));
        let ideol_dist = clamp01(_mm_div_ps(_mm_sqrt_ps(sq), diag));
        let politics = if has_ideol {
            _mm_mul_ps(_mm_sub_ps(neutral_ideol, ideol_dist), ideol_w)
        } else {
            zero
        };

        let f0 = pair_flags[i] as i32;
        let f1 = pair_flags[i + 1] as i32;
        let f2 = pair_flags[i + 2] as i32;
        let f3 = pair_flags[i + 3] as i32;
        let f_i32 = _mm_set_epi32(f3, f2, f1, f0);
        let m_claim = _mm_castsi128_ps(_mm_cmpeq_epi32(
            _mm_and_si128(f_i32, _mm_set1_epi32(PAIR_FLAG_CLAIM_OR_HOME as i32)),
            _mm_set1_epi32(PAIR_FLAG_CLAIM_OR_HOME as i32),
        ));
        let m_gp = _mm_castsi128_ps(_mm_cmpeq_epi32(
            _mm_and_si128(f_i32, _mm_set1_epi32(PAIR_FLAG_BOTH_GP as i32)),
            _mm_set1_epi32(PAIR_FLAG_BOTH_GP as i32),
        ));
        let m_img = _mm_castsi128_ps(_mm_cmpeq_epi32(
            _mm_and_si128(f_i32, _mm_set1_epi32(PAIR_FLAG_IMAGES_ON as i32)),
            _mm_set1_epi32(PAIR_FLAG_IMAGES_ON as i32),
        ));

        let eab = _mm_loadu_ps(esteem_ab.as_ptr().add(i));
        let eba = _mm_loadu_ps(esteem_ba.as_ptr().add(i));
        let social = select(m_img, _mm_mul_ps(_mm_mul_ps(_mm_add_ps(eab, eba), half), soc_w), zero);

        let cl = _mm_loadu_ps(claim_left.as_ptr().add(i));
        let cr = _mm_loadu_ps(claim_right.as_ptr().add(i));
        let cb = _mm_loadu_ps(claim_bonus.as_ptr().add(i));
        let mx = m_max(cl, cr);
        let mn = m_min(cl, cr);
        let intensity = clamp01(_mm_add_ps(_mm_add_ps(mx, _mm_mul_ps(mn, half)), cb));
        let claims = select(m_claim, _mm_sub_ps(zero, _mm_add_ps(c22, _mm_mul_ps(c13, intensity))), zero);

        let geo = if has_ramp {
            let bb = _mm_loadu_ps(bloc_bias.as_ptr().add(i));
            let base_geo = _mm_mul_ps(_mm_mul_ps(bb, c18), ramp);
            let gp_dist = select(_mm_cmplt_ps(ideol_dist, one), ideol_dist, one);
            let gp_pen = _mm_mul_ps(_mm_add_ps(c8, _mm_mul_ps(c10, gp_dist)), ramp);
            select(m_gp, _mm_sub_ps(base_geo, gp_pen), base_geo)
        } else {
            zero
        };

        let mut total = _mm_loadu_ps(base_reason_sum.as_ptr().add(i));
        let wa = _mm_loadu_ps(witness_against.as_ptr().add(i));
        let wf = _mm_loadu_ps(witness_for.as_ptr().add(i));
        total = select(_mm_cmplt_ps(wa, floor), _mm_add_ps(total, _mm_sub_ps(floor, wa)), total);
        total = select(_mm_cmpgt_ps(wf, ceiling), _mm_sub_ps(total, _mm_sub_ps(wf, ceiling)), total);

        total = select(_mm_cmpge_ps(abs(politics), eps05), _mm_add_ps(total, politics), total);
        let id_aff = _mm_loadu_ps(identity_affinity.as_ptr().add(i));
        total = select(_mm_cmpge_ps(id_aff, eps05), _mm_add_ps(total, id_aff), total);
        total = select(_mm_cmpge_ps(abs(social), eps05), _mm_add_ps(total, social), total);
        total = select(_mm_cmpge_ps(abs(claims), eps05), _mm_add_ps(total, claims), total);
        total = select(_mm_cmpge_ps(abs(geo), eps05), _mm_add_ps(total, geo), total);
        let opinion = clamp(total, op_lo, op_hi);

        let sr = _mm_loadu_ps(strength_ratio.as_ptr().add(i));
        let fr = _mm_loadu_ps(fear_reach.as_ptr().add(i));
        let fear = _mm_mul_ps(clamp(_mm_sub_ps(sr, one), zero, two), fr);

        _mm_storeu_ps(out_politics.as_mut_ptr().add(i), politics);
        _mm_storeu_ps(out_social.as_mut_ptr().add(i), social);
        _mm_storeu_ps(out_claims.as_mut_ptr().add(i), claims);
        _mm_storeu_ps(out_geopolitics.as_mut_ptr().add(i), geo);
        _mm_storeu_ps(out_opinion.as_mut_ptr().add(i), opinion);
        _mm_storeu_ps(out_fear.as_mut_ptr().add(i), fear);

        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn pair_score_batch_neon(
    left_econ: &[f32],
    left_auth: &[f32],
    right_econ: &[f32],
    right_auth: &[f32],
    esteem_ab: &[f32],
    esteem_ba: &[f32],
    claim_left: &[f32],
    claim_right: &[f32],
    claim_bonus: &[f32],
    bloc_bias: &[f32],
    base_reason_sum: &[f32],
    witness_against: &[f32],
    witness_for: &[f32],
    identity_affinity: &[f32],
    strength_ratio: &[f32],
    fear_reach: &[f32],
    pair_flags: &[u8],
    ideology_weight: f32,
    social_weight: f32,
    geopolitics_ramp: f32,
    third_party_floor: f32,
    third_party_ceiling: f32,
    out_politics: &mut [f32],
    out_social: &mut [f32],
    out_claims: &mut [f32],
    out_geopolitics: &mut [f32],
    out_opinion: &mut [f32],
    out_fear: &mut [f32],
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::{clamp, clamp01, m_max, m_min};

    let n = left_econ.len();
    let zero = vdupq_n_f32(0.0f32);
    let one = vdupq_n_f32(1.0f32);
    let two = vdupq_n_f32(2.0f32);
    let half = vdupq_n_f32(0.5f32);
    let diag = vdupq_n_f32(2.8284f32);
    let neutral_ideol = vdupq_n_f32(0.38f32);
    let ideol_w = vdupq_n_f32(ideology_weight);
    let soc_w = vdupq_n_f32(social_weight);
    let has_ideol = ideology_weight > 0.0f32;
    let ramp_scalar = clamp01_f32(geopolitics_ramp);
    let has_ramp = ramp_scalar > 0.0f32;
    let ramp = vdupq_n_f32(ramp_scalar);
    let c18 = vdupq_n_f32(18.0f32);
    let c8 = vdupq_n_f32(8.0f32);
    let c10 = vdupq_n_f32(10.0f32);
    let c22 = vdupq_n_f32(22.0f32);
    let c13 = vdupq_n_f32(13.0f32);
    let eps05 = vdupq_n_f32(0.05f32);
    let floor = vdupq_n_f32(m_min_f32(0.0f32, third_party_floor));
    let ceiling = vdupq_n_f32(m_max_f32(0.0f32, third_party_ceiling));
    let op_lo = vdupq_n_f32(-100.0f32);
    let op_hi = vdupq_n_f32(100.0f32);
    let bit0 = vdupq_n_u32(PAIR_FLAG_CLAIM_OR_HOME as u32);
    let bit1 = vdupq_n_u32(PAIR_FLAG_BOTH_GP as u32);
    let bit2 = vdupq_n_u32(PAIR_FLAG_IMAGES_ON as u32);

    let mut i = 0usize;
    while i + 4 <= n {
        let le = vld1q_f32(left_econ.as_ptr().add(i));
        let la = vld1q_f32(left_auth.as_ptr().add(i));
        let re = vld1q_f32(right_econ.as_ptr().add(i));
        let ra = vld1q_f32(right_auth.as_ptr().add(i));

        let de = vsubq_f32(le, re);
        let da = vsubq_f32(la, ra);
        let sq = vaddq_f32(vmulq_f32(de, de), vmulq_f32(da, da));
        let ideol_dist = clamp01(vdivq_f32(vsqrtq_f32(sq), diag));
        let politics = if has_ideol {
            vmulq_f32(vsubq_f32(neutral_ideol, ideol_dist), ideol_w)
        } else {
            zero
        };

        let flags_arr = [
            pair_flags[i] as u32,
            pair_flags[i + 1] as u32,
            pair_flags[i + 2] as u32,
            pair_flags[i + 3] as u32,
        ];
        let f_u32 = vld1q_u32(flags_arr.as_ptr());
        let m_claim = vtstq_u32(f_u32, bit0);
        let m_gp = vtstq_u32(f_u32, bit1);
        let m_img = vtstq_u32(f_u32, bit2);

        let eab = vld1q_f32(esteem_ab.as_ptr().add(i));
        let eba = vld1q_f32(esteem_ba.as_ptr().add(i));
        let social_val = vmulq_f32(vmulq_f32(vaddq_f32(eab, eba), half), soc_w);
        let social = vbslq_f32(m_img, social_val, zero);

        let cl = vld1q_f32(claim_left.as_ptr().add(i));
        let cr = vld1q_f32(claim_right.as_ptr().add(i));
        let cb = vld1q_f32(claim_bonus.as_ptr().add(i));
        let mx = m_max(cl, cr);
        let mn = m_min(cl, cr);
        let intensity = clamp01(vaddq_f32(vaddq_f32(mx, vmulq_f32(mn, half)), cb));
        let claims_val = vsubq_f32(zero, vaddq_f32(c22, vmulq_f32(c13, intensity)));
        let claims = vbslq_f32(m_claim, claims_val, zero);

        let geo = if has_ramp {
            let bb = vld1q_f32(bloc_bias.as_ptr().add(i));
            let base_geo = vmulq_f32(vmulq_f32(bb, c18), ramp);
            let gp_dist = vbslq_f32(vcltq_f32(ideol_dist, one), ideol_dist, one);
            let gp_pen = vmulq_f32(vaddq_f32(c8, vmulq_f32(c10, gp_dist)), ramp);
            vbslq_f32(m_gp, vsubq_f32(base_geo, gp_pen), base_geo)
        } else {
            zero
        };

        let mut total = vld1q_f32(base_reason_sum.as_ptr().add(i));
        let wa = vld1q_f32(witness_against.as_ptr().add(i));
        let wf = vld1q_f32(witness_for.as_ptr().add(i));
        total = vbslq_f32(vcltq_f32(wa, floor), vaddq_f32(total, vsubq_f32(floor, wa)), total);
        total = vbslq_f32(vcgtq_f32(wf, ceiling), vsubq_f32(total, vsubq_f32(wf, ceiling)), total);

        total = vbslq_f32(vcgeq_f32(vabsq_f32(politics), eps05), vaddq_f32(total, politics), total);
        let id_aff = vld1q_f32(identity_affinity.as_ptr().add(i));
        total = vbslq_f32(vcgeq_f32(id_aff, eps05), vaddq_f32(total, id_aff), total);
        total = vbslq_f32(vcgeq_f32(vabsq_f32(social), eps05), vaddq_f32(total, social), total);
        total = vbslq_f32(vcgeq_f32(vabsq_f32(claims), eps05), vaddq_f32(total, claims), total);
        total = vbslq_f32(vcgeq_f32(vabsq_f32(geo), eps05), vaddq_f32(total, geo), total);
        let opinion = clamp(total, op_lo, op_hi);

        let sr = vld1q_f32(strength_ratio.as_ptr().add(i));
        let fr = vld1q_f32(fear_reach.as_ptr().add(i));
        let fear = vmulq_f32(clamp(vsubq_f32(sr, one), zero, two), fr);

        vst1q_f32(out_politics.as_mut_ptr().add(i), politics);
        vst1q_f32(out_social.as_mut_ptr().add(i), social);
        vst1q_f32(out_claims.as_mut_ptr().add(i), claims);
        vst1q_f32(out_geopolitics.as_mut_ptr().add(i), geo);
        vst1q_f32(out_opinion.as_mut_ptr().add(i), opinion);
        vst1q_f32(out_fear.as_mut_ptr().add(i), fear);

        i += 4;
    }
    i
}

/// Bit-exact batched `OpinionSystem.WeightOf` + expiry/inert pruning across flat reason arrays.
#[allow(clippy::too_many_arguments)]
pub fn reason_decay_batch(
    base_opinion: &[f32],
    stacks: &[i32],
    scale: &[f32],
    granted_day: &[i32],
    expires_day: &[i32],
    decay_days: &[i32],
    flags: &[u8],
    today: i32,
    out_weight: &mut [f32],
    out_prune: &mut [u8],
) -> Result<(), i32> {
    let n = base_opinion.len();
    if stacks.len() < n
        || scale.len() < n
        || granted_day.len() < n
        || expires_day.len() < n
        || decay_days.len() < n
        || flags.len() < n
    {
        return Err(-2);
    }
    if out_weight.len() < n || out_prune.len() < n {
        return Err(-3);
    }
    for i in 0..n {
        let base = base_opinion[i];
        let exp = expires_day[i];
        let sc = scale[i];
        let days = decay_days[i];
        let decays = (flags[i] & 1) != 0;

        let expired_or_invalid = (exp > 0 && exp <= today) || !sc.is_finite();
        let eff_stacks = if stacks[i] > 1 { stacks[i] } else { 1 } as f32;
        let eff_scale = if sc <= 0.0f32 { 1.0f32 } else { sc };
        let raw_val = base * eff_stacks * eff_scale;
        let weight = if !decays || days <= 0 || exp <= 0 {
            raw_val
        } else {
            let life = (exp - granted_day[i]).max(1) as f32;
            let left = (exp - today).max(0) as f32;
            raw_val * (left / life)
        };
        let inert = expired_or_invalid || base == 0.0f32 || (decays && days > 0 && exp > 0 && weight.abs() < 0.05f32);
        out_weight[i] = weight;
        out_prune[i] = if inert { 1 } else { 0 };
    }
    Ok(())
}

/// Batched `ai:diplomacy` circle scoring and deterministic best-friend / worst-rival selection.
#[allow(clippy::too_many_arguments)]
pub fn partner_eval_batch(
    opinion: &[f32],
    stance: &[u8],
    bloc_bias: &[f32],
    trade_dep: &[f32],
    flags: &[u8],
    reach: f32,
    base_trade_th: f32,
    base_pact_th: f32,
    base_alliance_th: f32,
    era_mode: i32,
    friendship_th: f32,
    rivalry_th: f32,
    embargo_reconcile_op: f32,
    can_mission_budget: bool,
    out_action_mask: &mut [u8],
    out_trade_th: &mut [f32],
) -> Result<(i32, i32), i32> {
    let n = opinion.len();
    if stance.len() < n || bloc_bias.len() < n || trade_dep.len() < n || flags.len() < n {
        return Err(-2);
    }
    if out_action_mask.len() < n || out_trade_th.len() < n {
        return Err(-3);
    }

    let r = clamp_f32(reach, 0.25f32, 2.5f32);
    let era_trade = match era_mode {
        1 => 0.88f32,
        2 => 1.10f32,
        _ => 1.0f32,
    };
    let era_pact = match era_mode {
        1 => 0.90f32,
        2 => 1.10f32,
        _ => 1.0f32,
    };
    let era_alliance = match era_mode {
        1 => 0.94f32,
        2 => 1.08f32,
        _ => 1.0f32,
    };

    let mut best_idx = -1i32;
    let mut best_op = f32::MIN;
    let mut worst_idx = -1i32;
    let mut worst_op = f32::MAX;

    for i in 0..n {
        let st = stance[i];
        let op = opinion[i];
        if st == STANCE_AT_WAR {
            out_action_mask[i] = 0;
            out_trade_th[i] = 0.0f32;
            continue;
        }
        if op > best_op {
            best_op = op;
            best_idx = i as i32;
        }
        if op < worst_op {
            worst_op = op;
            worst_idx = i as i32;
        }

        let bloc_scale = clamp_f32(1.0f32 - bloc_bias[i] * 0.22f32, 0.72f32, 1.32f32);
        let trade_th = (base_trade_th / r) * bloc_scale * era_trade;
        let pact_th = (base_pact_th / r) * bloc_scale * era_pact;
        let alliance_th = (base_alliance_th / r) * bloc_scale * era_alliance;
        out_trade_th[i] = trade_th;

        let f = flags[i];
        let borders = (f & 1) != 0;
        let embargo_by_us = (f & 2) != 0;
        let has_mission = (f & 4) != 0;

        let mut mask = 0u8;
        if op >= friendship_th {
            mask |= ACTION_ELIGIBLE_FRIEND;
        }
        if embargo_by_us && op >= embargo_reconcile_op {
            mask |= ACTION_LIFT_EMBARGO;
        }
        if st == 3 && op >= 35.0f32 {
            mask |= ACTION_RECONCILE_RIVAL;
        }
        if can_mission_budget
            && op < alliance_th
            && !has_mission
            && (trade_dep[i] > 0.02f32 || borders || op >= 10.0f32)
        {
            mask |= ACTION_MISSION_CANDIDATE;
        }
        if op >= trade_th {
            mask |= ACTION_TRADE_OK;
        }
        if op >= pact_th {
            mask |= ACTION_PACT_OK;
        }
        if st != 2 && op >= alliance_th {
            mask |= ACTION_ALLIANCE_OK;
        }
        if op <= rivalry_th {
            mask |= ACTION_RIVAL_CANDIDATE;
        }
        out_action_mask[i] = mask;
    }

    Ok((best_idx, worst_idx))
}
