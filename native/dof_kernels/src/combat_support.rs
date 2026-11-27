// SPDX-License-Identifier: GPL-3.0-only
//! Bit-exact SIMD kernels (AVX2 / SSE2 / ARM64 NEON + scalar tail) for
//! `CombatCore`, `SupportCore`, and `EconomyCore` hot loops.

use crate::distance::great_circle_km;
use crate::simd::{clamp01_f32, lerp_clamped_f32, m_max_f32, m_min_f32, max0_f32};

/// Bit-exact `CombatCore.ComputeLineProfile` across `count` line formations.
pub fn combat_line_profile(
    divisions: &[f32],
    hardness: &[f32],
    armour: &[f32],
    piercing: &[f32],
    supply: &[f32],
    recon: &[f32],
) -> Result<(f32, f32, f32, f32, f32), i32> {
    let n = divisions.len();
    if hardness.len() < n
        || armour.len() < n
        || piercing.len() < n
        || supply.len() < n
        || recon.len() < n
    {
        return Err(-2);
    }
    if n == 0 {
        return Ok((0.0f32, 0.0f32, 0.0f32, 0.0f32, 0.0f32));
    }

    let mut sum_div = 0.0f32;
    let mut sum_hard = 0.0f32;
    let mut sum_arm = 0.0f32;
    let mut sum_prc = 0.0f32;
    let mut sum_sup = 0.0f32;
    let mut sum_rec = 0.0f32;

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let mut i = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe {
            combat_line_profile_avx2(
                divisions,
                hardness,
                armour,
                piercing,
                supply,
                recon,
                &mut sum_div,
                &mut sum_hard,
                &mut sum_arm,
                &mut sum_prc,
                &mut sum_sup,
                &mut sum_rec,
            )
        }
    } else {
        unsafe {
            combat_line_profile_sse2(
                divisions,
                hardness,
                armour,
                piercing,
                supply,
                recon,
                &mut sum_div,
                &mut sum_hard,
                &mut sum_arm,
                &mut sum_prc,
                &mut sum_sup,
                &mut sum_rec,
            )
        }
    };

    #[cfg(target_arch = "aarch64")]
    let mut i = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe {
            combat_line_profile_neon(
                divisions,
                hardness,
                armour,
                piercing,
                supply,
                recon,
                &mut sum_div,
                &mut sum_hard,
                &mut sum_arm,
                &mut sum_prc,
                &mut sum_sup,
                &mut sum_rec,
            )
        }
    } else {
        0
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let mut i = 0usize;

    // First pass in ComputeLineProfile sums divisions cleanly in strict order, then second loop sums weighted terms.
    //Wait: because C# ComputeLineProfile runs two loops:
    //  Loop 1: for i in 0..n { divisions += M.Max(0.01f, side[i].Divisions); }
    //  Loop 2: for i in 0..n { sumHardness += ...; ... }
    // Because sum_div only depends on divisions[i] and IEEE float addition is identical per accumulator,
    // computing the two accumulators in one SIMD pass and accumulating each stream in strict order 0..n
    // produces the EXACT same IEEE-754 float bits as two separate loops!
    while i < n {
        let w = m_max_f32(0.01f32, divisions[i]);
        sum_div += w;
        sum_hard += hardness[i] * w;
        sum_arm += armour[i] * w;
        sum_prc += piercing[i] * w;
        sum_sup += clamp01_f32(supply[i]) * w;
        sum_rec += recon[i] * w;
        i += 1;
    }

    if sum_div <= 0.0f32 {
        Ok((0.0f32, 0.0f32, 0.0f32, 0.0f32, 0.0f32))
    } else {
        Ok((
            sum_hard / sum_div,
            sum_arm / sum_div,
            sum_prc / sum_div,
            sum_sup / sum_div,
            sum_rec / sum_div,
        ))
    }
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn combat_line_profile_avx2(
    divisions: &[f32],
    hardness: &[f32],
    armour: &[f32],
    piercing: &[f32],
    supply: &[f32],
    recon: &[f32],
    sum_div: &mut f32,
    sum_hard: &mut f32,
    sum_arm: &mut f32,
    sum_prc: &mut f32,
    sum_sup: &mut f32,
    sum_rec: &mut f32,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_avx2::{clamp01, m_max};

    let n = divisions.len();
    let min_div = _mm256_set1_ps(0.01f32);
    let mut b_w = [0.0f32; 8];
    let mut b_h = [0.0f32; 8];
    let mut b_a = [0.0f32; 8];
    let mut b_p = [0.0f32; 8];
    let mut b_s = [0.0f32; 8];
    let mut b_r = [0.0f32; 8];

    let mut i = 0usize;
    while i + 8 <= n {
        let d = _mm256_loadu_ps(divisions.as_ptr().add(i));
        let w = m_max(min_div, d);
        let h = _mm256_mul_ps(_mm256_loadu_ps(hardness.as_ptr().add(i)), w);
        let a = _mm256_mul_ps(_mm256_loadu_ps(armour.as_ptr().add(i)), w);
        let p = _mm256_mul_ps(_mm256_loadu_ps(piercing.as_ptr().add(i)), w);
        let s = _mm256_mul_ps(clamp01(_mm256_loadu_ps(supply.as_ptr().add(i))), w);
        let r = _mm256_mul_ps(_mm256_loadu_ps(recon.as_ptr().add(i)), w);

        _mm256_storeu_ps(b_w.as_mut_ptr(), w);
        _mm256_storeu_ps(b_h.as_mut_ptr(), h);
        _mm256_storeu_ps(b_a.as_mut_ptr(), a);
        _mm256_storeu_ps(b_p.as_mut_ptr(), p);
        _mm256_storeu_ps(b_s.as_mut_ptr(), s);
        _mm256_storeu_ps(b_r.as_mut_ptr(), r);

        for k in 0..8 {
            *sum_div += b_w[k];
            *sum_hard += b_h[k];
            *sum_arm += b_a[k];
            *sum_prc += b_p[k];
            *sum_sup += b_s[k];
            *sum_rec += b_r[k];
        }
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn combat_line_profile_sse2(
    divisions: &[f32],
    hardness: &[f32],
    armour: &[f32],
    piercing: &[f32],
    supply: &[f32],
    recon: &[f32],
    sum_div: &mut f32,
    sum_hard: &mut f32,
    sum_arm: &mut f32,
    sum_prc: &mut f32,
    sum_sup: &mut f32,
    sum_rec: &mut f32,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_sse2::{clamp01, m_max};

    let n = divisions.len();
    let min_div = _mm_set1_ps(0.01f32);
    let mut b_w = [0.0f32; 4];
    let mut b_h = [0.0f32; 4];
    let mut b_a = [0.0f32; 4];
    let mut b_p = [0.0f32; 4];
    let mut b_s = [0.0f32; 4];
    let mut b_r = [0.0f32; 4];

    let mut i = 0usize;
    while i + 4 <= n {
        let d = _mm_loadu_ps(divisions.as_ptr().add(i));
        let w = m_max(min_div, d);
        let h = _mm_mul_ps(_mm_loadu_ps(hardness.as_ptr().add(i)), w);
        let a = _mm_mul_ps(_mm_loadu_ps(armour.as_ptr().add(i)), w);
        let p = _mm_mul_ps(_mm_loadu_ps(piercing.as_ptr().add(i)), w);
        let s = _mm_mul_ps(clamp01(_mm_loadu_ps(supply.as_ptr().add(i))), w);
        let r = _mm_mul_ps(_mm_loadu_ps(recon.as_ptr().add(i)), w);

        _mm_storeu_ps(b_w.as_mut_ptr(), w);
        _mm_storeu_ps(b_h.as_mut_ptr(), h);
        _mm_storeu_ps(b_a.as_mut_ptr(), a);
        _mm_storeu_ps(b_p.as_mut_ptr(), p);
        _mm_storeu_ps(b_s.as_mut_ptr(), s);
        _mm_storeu_ps(b_r.as_mut_ptr(), r);

        for k in 0..4 {
            *sum_div += b_w[k];
            *sum_hard += b_h[k];
            *sum_arm += b_a[k];
            *sum_prc += b_p[k];
            *sum_sup += b_s[k];
            *sum_rec += b_r[k];
        }
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn combat_line_profile_neon(
    divisions: &[f32],
    hardness: &[f32],
    armour: &[f32],
    piercing: &[f32],
    supply: &[f32],
    recon: &[f32],
    sum_div: &mut f32,
    sum_hard: &mut f32,
    sum_arm: &mut f32,
    sum_prc: &mut f32,
    sum_sup: &mut f32,
    sum_rec: &mut f32,
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::{clamp01, m_max};

    let n = divisions.len();
    let min_div = vdupq_n_f32(0.01f32);
    let mut b_w = [0.0f32; 4];
    let mut b_h = [0.0f32; 4];
    let mut b_a = [0.0f32; 4];
    let mut b_p = [0.0f32; 4];
    let mut b_s = [0.0f32; 4];
    let mut b_r = [0.0f32; 4];

    let mut i = 0usize;
    while i + 4 <= n {
        let d = vld1q_f32(divisions.as_ptr().add(i));
        let w = m_max(min_div, d);
        let h = vmulq_f32(vld1q_f32(hardness.as_ptr().add(i)), w);
        let a = vmulq_f32(vld1q_f32(armour.as_ptr().add(i)), w);
        let p = vmulq_f32(vld1q_f32(piercing.as_ptr().add(i)), w);
        let s = vmulq_f32(clamp01(vld1q_f32(supply.as_ptr().add(i))), w);
        let r = vmulq_f32(vld1q_f32(recon.as_ptr().add(i)), w);

        vst1q_f32(b_w.as_mut_ptr(), w);
        vst1q_f32(b_h.as_mut_ptr(), h);
        vst1q_f32(b_a.as_mut_ptr(), a);
        vst1q_f32(b_p.as_mut_ptr(), p);
        vst1q_f32(b_s.as_mut_ptr(), s);
        vst1q_f32(b_r.as_mut_ptr(), r);

        for k in 0..4 {
            *sum_div += b_w[k];
            *sum_hard += b_h[k];
            *sum_arm += b_a[k];
            *sum_prc += b_p[k];
            *sum_sup += b_s[k];
            *sum_rec += b_r[k];
        }
        i += 4;
    }
    i
}

/// Bit-exact `CombatCore.ResolveBattlesStages` unit product sums + weighted diagnostics for one side.
/// `out_totals` layout (length >= 9):
///   0: offence_sum (`f64`)
///   1: protection_sum (`f64`)
///   2: dug_in_sum (`f64`)
///   3: power_weight (`f64`)
///   4: weighted_train (`f64`) — `atk_train` when `weight_offence_telemetry != 0`, else `def_train`
///   5: weighted_stance (`f64`) — `atk_stance` or `def_stance`
///   6: weighted_cmd (`f64`) — `cmd_atk` or `cmd_def`
///   7: weighted_res (`f64`) — `atk_res` or `def_res`
///   8: weighted_enc (`f64`) — `enc`
#[allow(clippy::too_many_arguments)]
pub fn combat_side_totals(
    raw_power: &[f32],
    offence: &[f32],
    atk_train: &[f32],
    atk_stance: &[f32],
    atk_res: &[f32],
    munitions: &[f32],
    enc: &[f32],
    spirit: &[f32],
    cmd_atk: &[f32],
    def_stat: &[f32],
    def_train: &[f32],
    def_stance: &[f32],
    def_res: &[f32],
    cmd_def: &[f32],
    entrenchment: &[f32],
    weight_offence_telemetry: bool,
    out_totals: &mut [f64],
) -> Result<(), i32> {
    let n = raw_power.len();
    if offence.len() < n
        || atk_train.len() < n
        || atk_stance.len() < n
        || atk_res.len() < n
        || munitions.len() < n
        || enc.len() < n
        || spirit.len() < n
        || cmd_atk.len() < n
        || def_stat.len() < n
        || def_train.len() < n
        || def_stance.len() < n
        || def_res.len() < n
        || cmd_def.len() < n
        || entrenchment.len() < n
    {
        return Err(-2);
    }
    if out_totals.len() < 9 {
        return Err(-3);
    }
    out_totals[..9].fill(0.0f64);
    if n == 0 {
        return Ok(());
    }

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let mut i = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe {
            combat_side_totals_avx2(
                raw_power,
                offence,
                atk_train,
                atk_stance,
                atk_res,
                munitions,
                enc,
                spirit,
                cmd_atk,
                def_stat,
                def_train,
                def_stance,
                def_res,
                cmd_def,
                entrenchment,
                weight_offence_telemetry,
                out_totals,
            )
        }
    } else {
        unsafe {
            combat_side_totals_sse2(
                raw_power,
                offence,
                atk_train,
                atk_stance,
                atk_res,
                munitions,
                enc,
                spirit,
                cmd_atk,
                def_stat,
                def_train,
                def_stance,
                def_res,
                cmd_def,
                entrenchment,
                weight_offence_telemetry,
                out_totals,
            )
        }
    };

    #[cfg(target_arch = "aarch64")]
    let mut i = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe {
            combat_side_totals_neon(
                raw_power,
                offence,
                atk_train,
                atk_stance,
                atk_res,
                munitions,
                enc,
                spirit,
                cmd_atk,
                def_stat,
                def_train,
                def_stance,
                def_res,
                cmd_def,
                entrenchment,
                weight_offence_telemetry,
                out_totals,
            )
        }
    } else {
        0
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let mut i = 0usize;

    while i < n {
        let power = m_max_f32(0.01f32, raw_power[i]);
        let at = atk_train[i];
        let ast = atk_stance[i];
        let ar = atk_res[i];
        let mun = munitions[i];
        let ec = enc[i];
        let sp = spirit[i];
        let ca = cmd_atk[i];

        let dt = def_train[i];
        let dst = def_stance[i];
        let dr = def_res[i];
        let cd = cmd_def[i];

        let off_term = (((((((power * offence[i]) * at) * ast) * ar) * mun) * ec) * sp) * ca;
        let prot_term = ((((((power * def_stat[i]) * dt) * dst) * dr) * ec) * sp) * cd;
        let w = power as f64;
        out_totals[0] += off_term as f64;
        out_totals[1] += prot_term as f64;
        out_totals[2] += entrenchment[i] as f64;
        out_totals[3] += w;
        if weight_offence_telemetry {
            out_totals[4] += w * (at as f64);
            out_totals[5] += w * (ast as f64);
            out_totals[6] += w * (ca as f64);
            out_totals[7] += w * (ar as f64);
        } else {
            out_totals[4] += w * (dt as f64);
            out_totals[5] += w * (dst as f64);
            out_totals[6] += w * (cd as f64);
            out_totals[7] += w * (dr as f64);
        }
        out_totals[8] += w * (ec as f64);
        i += 1;
    }

    Ok(())
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn combat_side_totals_avx2(
    raw_power: &[f32],
    offence: &[f32],
    atk_train: &[f32],
    atk_stance: &[f32],
    atk_res: &[f32],
    munitions: &[f32],
    enc: &[f32],
    spirit: &[f32],
    cmd_atk: &[f32],
    def_stat: &[f32],
    def_train: &[f32],
    def_stance: &[f32],
    def_res: &[f32],
    cmd_def: &[f32],
    entrenchment: &[f32],
    weight_offence_telemetry: bool,
    out_totals: &mut [f64],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_avx2::m_max;

    let n = raw_power.len();
    let min_p = _mm256_set1_ps(0.01f32);
    let mut b_p = [0.0f32; 8];
    let mut b_off = [0.0f32; 8];
    let mut b_prot = [0.0f32; 8];
    let mut b_ent = [0.0f32; 8];
    let mut b_tr = [0.0f32; 8];
    let mut b_st = [0.0f32; 8];
    let mut b_cm = [0.0f32; 8];
    let mut b_rs = [0.0f32; 8];
    let mut b_ec = [0.0f32; 8];

    let mut i = 0usize;
    while i + 8 <= n {
        let power = m_max(min_p, _mm256_loadu_ps(raw_power.as_ptr().add(i)));
        let off_v = _mm256_loadu_ps(offence.as_ptr().add(i));
        let at_v = _mm256_loadu_ps(atk_train.as_ptr().add(i));
        let ast_v = _mm256_loadu_ps(atk_stance.as_ptr().add(i));
        let ar_v = _mm256_loadu_ps(atk_res.as_ptr().add(i));
        let mun_v = _mm256_loadu_ps(munitions.as_ptr().add(i));
        let ec_v = _mm256_loadu_ps(enc.as_ptr().add(i));
        let sp_v = _mm256_loadu_ps(spirit.as_ptr().add(i));
        let ca_v = _mm256_loadu_ps(cmd_atk.as_ptr().add(i));

        let def_v = _mm256_loadu_ps(def_stat.as_ptr().add(i));
        let dt_v = _mm256_loadu_ps(def_train.as_ptr().add(i));
        let dst_v = _mm256_loadu_ps(def_stance.as_ptr().add(i));
        let dr_v = _mm256_loadu_ps(def_res.as_ptr().add(i));
        let cd_v = _mm256_loadu_ps(cmd_def.as_ptr().add(i));
        let ent_v = _mm256_loadu_ps(entrenchment.as_ptr().add(i));

        let off_term = _mm256_mul_ps(
            _mm256_mul_ps(
                _mm256_mul_ps(
                    _mm256_mul_ps(
                        _mm256_mul_ps(
                            _mm256_mul_ps(
                                _mm256_mul_ps(_mm256_mul_ps(power, off_v), at_v),
                                ast_v,
                            ),
                            ar_v,
                        ),
                        mun_v,
                    ),
                    ec_v,
                ),
                sp_v,
            ),
            ca_v,
        );
        let prot_term = _mm256_mul_ps(
            _mm256_mul_ps(
                _mm256_mul_ps(
                    _mm256_mul_ps(
                        _mm256_mul_ps(_mm256_mul_ps(_mm256_mul_ps(power, def_v), dt_v), dst_v),
                        dr_v,
                    ),
                    ec_v,
                ),
                sp_v,
            ),
            cd_v,
        );

        _mm256_storeu_ps(b_p.as_mut_ptr(), power);
        _mm256_storeu_ps(b_off.as_mut_ptr(), off_term);
        _mm256_storeu_ps(b_prot.as_mut_ptr(), prot_term);
        _mm256_storeu_ps(b_ent.as_mut_ptr(), ent_v);
        _mm256_storeu_ps(b_ec.as_mut_ptr(), ec_v);
        if weight_offence_telemetry {
            _mm256_storeu_ps(b_tr.as_mut_ptr(), at_v);
            _mm256_storeu_ps(b_st.as_mut_ptr(), ast_v);
            _mm256_storeu_ps(b_cm.as_mut_ptr(), ca_v);
            _mm256_storeu_ps(b_rs.as_mut_ptr(), ar_v);
        } else {
            _mm256_storeu_ps(b_tr.as_mut_ptr(), dt_v);
            _mm256_storeu_ps(b_st.as_mut_ptr(), dst_v);
            _mm256_storeu_ps(b_cm.as_mut_ptr(), cd_v);
            _mm256_storeu_ps(b_rs.as_mut_ptr(), dr_v);
        }

        for k in 0..8 {
            let w = b_p[k] as f64;
            out_totals[0] += b_off[k] as f64;
            out_totals[1] += b_prot[k] as f64;
            out_totals[2] += b_ent[k] as f64;
            out_totals[3] += w;
            out_totals[4] += w * (b_tr[k] as f64);
            out_totals[5] += w * (b_st[k] as f64);
            out_totals[6] += w * (b_cm[k] as f64);
            out_totals[7] += w * (b_rs[k] as f64);
            out_totals[8] += w * (b_ec[k] as f64);
        }
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn combat_side_totals_sse2(
    raw_power: &[f32],
    offence: &[f32],
    atk_train: &[f32],
    atk_stance: &[f32],
    atk_res: &[f32],
    munitions: &[f32],
    enc: &[f32],
    spirit: &[f32],
    cmd_atk: &[f32],
    def_stat: &[f32],
    def_train: &[f32],
    def_stance: &[f32],
    def_res: &[f32],
    cmd_def: &[f32],
    entrenchment: &[f32],
    weight_offence_telemetry: bool,
    out_totals: &mut [f64],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_sse2::m_max;

    let n = raw_power.len();
    let min_p = _mm_set1_ps(0.01f32);
    let mut b_p = [0.0f32; 4];
    let mut b_off = [0.0f32; 4];
    let mut b_prot = [0.0f32; 4];
    let mut b_ent = [0.0f32; 4];
    let mut b_tr = [0.0f32; 4];
    let mut b_st = [0.0f32; 4];
    let mut b_cm = [0.0f32; 4];
    let mut b_rs = [0.0f32; 4];
    let mut b_ec = [0.0f32; 4];

    let mut i = 0usize;
    while i + 4 <= n {
        let power = m_max(min_p, _mm_loadu_ps(raw_power.as_ptr().add(i)));
        let off_v = _mm_loadu_ps(offence.as_ptr().add(i));
        let at_v = _mm_loadu_ps(atk_train.as_ptr().add(i));
        let ast_v = _mm_loadu_ps(atk_stance.as_ptr().add(i));
        let ar_v = _mm_loadu_ps(atk_res.as_ptr().add(i));
        let mun_v = _mm_loadu_ps(munitions.as_ptr().add(i));
        let ec_v = _mm_loadu_ps(enc.as_ptr().add(i));
        let sp_v = _mm_loadu_ps(spirit.as_ptr().add(i));
        let ca_v = _mm_loadu_ps(cmd_atk.as_ptr().add(i));

        let def_v = _mm_loadu_ps(def_stat.as_ptr().add(i));
        let dt_v = _mm_loadu_ps(def_train.as_ptr().add(i));
        let dst_v = _mm_loadu_ps(def_stance.as_ptr().add(i));
        let dr_v = _mm_loadu_ps(def_res.as_ptr().add(i));
        let cd_v = _mm_loadu_ps(cmd_def.as_ptr().add(i));
        let ent_v = _mm_loadu_ps(entrenchment.as_ptr().add(i));

        let off_term = _mm_mul_ps(
            _mm_mul_ps(
                _mm_mul_ps(
                    _mm_mul_ps(
                        _mm_mul_ps(
                            _mm_mul_ps(
                                _mm_mul_ps(_mm_mul_ps(power, off_v), at_v),
                                ast_v,
                            ),
                            ar_v,
                        ),
                        mun_v,
                    ),
                    ec_v,
                ),
                sp_v,
            ),
            ca_v,
        );
        let prot_term = _mm_mul_ps(
            _mm_mul_ps(
                _mm_mul_ps(
                    _mm_mul_ps(
                        _mm_mul_ps(_mm_mul_ps(_mm_mul_ps(power, def_v), dt_v), dst_v),
                        dr_v,
                    ),
                    ec_v,
                ),
                sp_v,
            ),
            cd_v,
        );

        _mm_storeu_ps(b_p.as_mut_ptr(), power);
        _mm_storeu_ps(b_off.as_mut_ptr(), off_term);
        _mm_storeu_ps(b_prot.as_mut_ptr(), prot_term);
        _mm_storeu_ps(b_ent.as_mut_ptr(), ent_v);
        _mm_storeu_ps(b_ec.as_mut_ptr(), ec_v);
        if weight_offence_telemetry {
            _mm_storeu_ps(b_tr.as_mut_ptr(), at_v);
            _mm_storeu_ps(b_st.as_mut_ptr(), ast_v);
            _mm_storeu_ps(b_cm.as_mut_ptr(), ca_v);
            _mm_storeu_ps(b_rs.as_mut_ptr(), ar_v);
        } else {
            _mm_storeu_ps(b_tr.as_mut_ptr(), dt_v);
            _mm_storeu_ps(b_st.as_mut_ptr(), dst_v);
            _mm_storeu_ps(b_cm.as_mut_ptr(), cd_v);
            _mm_storeu_ps(b_rs.as_mut_ptr(), dr_v);
        }

        for k in 0..4 {
            let w = b_p[k] as f64;
            out_totals[0] += b_off[k] as f64;
            out_totals[1] += b_prot[k] as f64;
            out_totals[2] += b_ent[k] as f64;
            out_totals[3] += w;
            out_totals[4] += w * (b_tr[k] as f64);
            out_totals[5] += w * (b_st[k] as f64);
            out_totals[6] += w * (b_cm[k] as f64);
            out_totals[7] += w * (b_rs[k] as f64);
            out_totals[8] += w * (b_ec[k] as f64);
        }
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn combat_side_totals_neon(
    raw_power: &[f32],
    offence: &[f32],
    atk_train: &[f32],
    atk_stance: &[f32],
    atk_res: &[f32],
    munitions: &[f32],
    enc: &[f32],
    spirit: &[f32],
    cmd_atk: &[f32],
    def_stat: &[f32],
    def_train: &[f32],
    def_stance: &[f32],
    def_res: &[f32],
    cmd_def: &[f32],
    entrenchment: &[f32],
    weight_offence_telemetry: bool,
    out_totals: &mut [f64],
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::m_max;

    let n = raw_power.len();
    let min_p = vdupq_n_f32(0.01f32);
    let mut b_p = [0.0f32; 4];
    let mut b_off = [0.0f32; 4];
    let mut b_prot = [0.0f32; 4];
    let mut b_ent = [0.0f32; 4];
    let mut b_tr = [0.0f32; 4];
    let mut b_st = [0.0f32; 4];
    let mut b_cm = [0.0f32; 4];
    let mut b_rs = [0.0f32; 4];
    let mut b_ec = [0.0f32; 4];

    let mut i = 0usize;
    while i + 4 <= n {
        let power = m_max(min_p, vld1q_f32(raw_power.as_ptr().add(i)));
        let off_v = vld1q_f32(offence.as_ptr().add(i));
        let at_v = vld1q_f32(atk_train.as_ptr().add(i));
        let ast_v = vld1q_f32(atk_stance.as_ptr().add(i));
        let ar_v = vld1q_f32(atk_res.as_ptr().add(i));
        let mun_v = vld1q_f32(munitions.as_ptr().add(i));
        let ec_v = vld1q_f32(enc.as_ptr().add(i));
        let sp_v = vld1q_f32(spirit.as_ptr().add(i));
        let ca_v = vld1q_f32(cmd_atk.as_ptr().add(i));

        let def_v = vld1q_f32(def_stat.as_ptr().add(i));
        let dt_v = vld1q_f32(def_train.as_ptr().add(i));
        let dst_v = vld1q_f32(def_stance.as_ptr().add(i));
        let dr_v = vld1q_f32(def_res.as_ptr().add(i));
        let cd_v = vld1q_f32(cmd_def.as_ptr().add(i));
        let ent_v = vld1q_f32(entrenchment.as_ptr().add(i));

        let off_term = vmulq_f32(
            vmulq_f32(
                vmulq_f32(
                    vmulq_f32(
                        vmulq_f32(
                            vmulq_f32(
                                vmulq_f32(vmulq_f32(power, off_v), at_v),
                                ast_v,
                            ),
                            ar_v,
                        ),
                        mun_v,
                    ),
                    ec_v,
                ),
                sp_v,
            ),
            ca_v,
        );
        let prot_term = vmulq_f32(
            vmulq_f32(
                vmulq_f32(
                    vmulq_f32(
                        vmulq_f32(vmulq_f32(vmulq_f32(power, def_v), dt_v), dst_v),
                        dr_v,
                    ),
                    ec_v,
                ),
                sp_v,
            ),
            cd_v,
        );

        vst1q_f32(b_p.as_mut_ptr(), power);
        vst1q_f32(b_off.as_mut_ptr(), off_term);
        vst1q_f32(b_prot.as_mut_ptr(), prot_term);
        vst1q_f32(b_ent.as_mut_ptr(), ent_v);
        vst1q_f32(b_ec.as_mut_ptr(), ec_v);
        if weight_offence_telemetry {
            vst1q_f32(b_tr.as_mut_ptr(), at_v);
            vst1q_f32(b_st.as_mut_ptr(), ast_v);
            vst1q_f32(b_cm.as_mut_ptr(), ca_v);
            vst1q_f32(b_rs.as_mut_ptr(), ar_v);
        } else {
            vst1q_f32(b_tr.as_mut_ptr(), dt_v);
            vst1q_f32(b_st.as_mut_ptr(), dst_v);
            vst1q_f32(b_cm.as_mut_ptr(), cd_v);
            vst1q_f32(b_rs.as_mut_ptr(), dr_v);
        }

        for k in 0..4 {
            let w = b_p[k] as f64;
            out_totals[0] += b_off[k] as f64;
            out_totals[1] += b_prot[k] as f64;
            out_totals[2] += b_ent[k] as f64;
            out_totals[3] += w;
            out_totals[4] += w * (b_tr[k] as f64);
            out_totals[5] += w * (b_st[k] as f64);
            out_totals[6] += w * (b_cm[k] as f64);
            out_totals[7] += w * (b_rs[k] as f64);
            out_totals[8] += w * (b_ec[k] as f64);
        }
        i += 4;
    }
    i
}

/// Bit-exact `SupportCore.Attrition` across exposed support formations.
#[allow(clippy::too_many_arguments)]
pub fn support_attrition_batch(
    threat: &[f32],
    mod_factor: &[f32],
    attrition_per_day: f32,
    saturation: f32,
    floor: f32,
    strength: &mut [f32],
    experience: &mut [f32],
    out_loss: &mut [f32],
) -> Result<(), i32> {
    let n = threat.len();
    if mod_factor.len() < n || strength.len() < n || experience.len() < n || out_loss.len() < n {
        return Err(-2);
    }
    if n == 0 {
        return Ok(());
    }

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let mut i = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe {
            support_attrition_avx2(
                threat,
                mod_factor,
                attrition_per_day,
                saturation,
                floor,
                strength,
                experience,
                out_loss,
            )
        }
    } else {
        unsafe {
            support_attrition_sse2(
                threat,
                mod_factor,
                attrition_per_day,
                saturation,
                floor,
                strength,
                experience,
                out_loss,
            )
        }
    };

    #[cfg(target_arch = "aarch64")]
    let mut i = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe {
            support_attrition_neon(
                threat,
                mod_factor,
                attrition_per_day,
                saturation,
                floor,
                strength,
                experience,
                out_loss,
            )
        }
    } else {
        0
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let mut i = 0usize;

    while i < n {
        let th = threat[i];
        if th <= 0.0f32 {
            out_loss[i] = 0.0f32;
            i += 1;
            continue;
        }
        let raw_loss = ((attrition_per_day * th) / (th + saturation)) * mod_factor[i];
        let avail = max0_f32(strength[i] - floor);
        let loss = m_min_f32(raw_loss, avail);
        if loss <= 0.0f32 || loss.is_nan() {
            out_loss[i] = 0.0f32;
        } else {
            strength[i] -= loss;
            out_loss[i] = loss;
            experience[i] = clamp01_f32(experience[i] + loss * 0.08f32);
        }
        i += 1;
    }

    Ok(())
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn support_attrition_avx2(
    threat: &[f32],
    mod_factor: &[f32],
    attrition_per_day: f32,
    saturation: f32,
    floor: f32,
    strength: &mut [f32],
    experience: &mut [f32],
    out_loss: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_avx2::{clamp01, m_min, max0};

    let n = threat.len();
    let zero = _mm256_setzero_ps();
    let apd = _mm256_set1_ps(attrition_per_day);
    let sat = _mm256_set1_ps(saturation);
    let flr = _mm256_set1_ps(floor);
    let c08 = _mm256_set1_ps(0.08f32);

    let mut i = 0usize;
    while i + 8 <= n {
        let th = _mm256_loadu_ps(threat.as_ptr().add(i));
        let mf = _mm256_loadu_ps(mod_factor.as_ptr().add(i));
        let str0 = _mm256_loadu_ps(strength.as_ptr().add(i));
        let exp0 = _mm256_loadu_ps(experience.as_ptr().add(i));

        let raw = _mm256_mul_ps(_mm256_div_ps(_mm256_mul_ps(apd, th), _mm256_add_ps(th, sat)), mf);
        let avail = max0(_mm256_sub_ps(str0, flr));
        let loss = m_min(raw, avail);

        let th_gt = _mm256_cmp_ps(th, zero, _CMP_GT_OQ);
        let loss_gt = _mm256_cmp_ps(loss, zero, _CMP_GT_OQ);
        let active = _mm256_and_ps(th_gt, loss_gt);

        let out_l = _mm256_blendv_ps(zero, loss, active);
        let str1 = _mm256_blendv_ps(str0, _mm256_sub_ps(str0, loss), active);
        let exp1 = _mm256_blendv_ps(exp0, clamp01(_mm256_add_ps(exp0, _mm256_mul_ps(loss, c08))), active);

        _mm256_storeu_ps(out_loss.as_mut_ptr().add(i), out_l);
        _mm256_storeu_ps(strength.as_mut_ptr().add(i), str1);
        _mm256_storeu_ps(experience.as_mut_ptr().add(i), exp1);
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn support_attrition_sse2(
    threat: &[f32],
    mod_factor: &[f32],
    attrition_per_day: f32,
    saturation: f32,
    floor: f32,
    strength: &mut [f32],
    experience: &mut [f32],
    out_loss: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_sse2::{clamp01, m_min, max0, select};

    let n = threat.len();
    let zero = _mm_setzero_ps();
    let apd = _mm_set1_ps(attrition_per_day);
    let sat = _mm_set1_ps(saturation);
    let flr = _mm_set1_ps(floor);
    let c08 = _mm_set1_ps(0.08f32);

    let mut i = 0usize;
    while i + 4 <= n {
        let th = _mm_loadu_ps(threat.as_ptr().add(i));
        let mf = _mm_loadu_ps(mod_factor.as_ptr().add(i));
        let str0 = _mm_loadu_ps(strength.as_ptr().add(i));
        let exp0 = _mm_loadu_ps(experience.as_ptr().add(i));

        let raw = _mm_mul_ps(_mm_div_ps(_mm_mul_ps(apd, th), _mm_add_ps(th, sat)), mf);
        let avail = max0(_mm_sub_ps(str0, flr));
        let loss = m_min(raw, avail);

        let th_gt = _mm_cmpgt_ps(th, zero);
        let loss_gt = _mm_cmpgt_ps(loss, zero);
        let active = _mm_and_ps(th_gt, loss_gt);

        let out_l = select(active, loss, zero);
        let str1 = select(active, _mm_sub_ps(str0, loss), str0);
        let exp1 = select(active, clamp01(_mm_add_ps(exp0, _mm_mul_ps(loss, c08))), exp0);

        _mm_storeu_ps(out_loss.as_mut_ptr().add(i), out_l);
        _mm_storeu_ps(strength.as_mut_ptr().add(i), str1);
        _mm_storeu_ps(experience.as_mut_ptr().add(i), exp1);
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn support_attrition_neon(
    threat: &[f32],
    mod_factor: &[f32],
    attrition_per_day: f32,
    saturation: f32,
    floor: f32,
    strength: &mut [f32],
    experience: &mut [f32],
    out_loss: &mut [f32],
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::{clamp01, m_min, max0};

    let n = threat.len();
    let zero = vdupq_n_f32(0.0f32);
    let apd = vdupq_n_f32(attrition_per_day);
    let sat = vdupq_n_f32(saturation);
    let flr = vdupq_n_f32(floor);
    let c08 = vdupq_n_f32(0.08f32);

    let mut i = 0usize;
    while i + 4 <= n {
        let th = vld1q_f32(threat.as_ptr().add(i));
        let mf = vld1q_f32(mod_factor.as_ptr().add(i));
        let str0 = vld1q_f32(strength.as_ptr().add(i));
        let exp0 = vld1q_f32(experience.as_ptr().add(i));

        let raw = vmulq_f32(vdivq_f32(vmulq_f32(apd, th), vaddq_f32(th, sat)), mf);
        let avail = max0(vsubq_f32(str0, flr));
        let loss = m_min(raw, avail);

        let active = vandq_u32(vcgtq_f32(th, zero), vcgtq_f32(loss, zero));
        let out_l = vbslq_f32(active, loss, zero);
        let str1 = vbslq_f32(active, vsubq_f32(str0, loss), str0);
        let exp1 = vbslq_f32(active, clamp01(vaddq_f32(exp0, vmulq_f32(loss, c08))), exp0);

        vst1q_f32(out_loss.as_mut_ptr().add(i), out_l);
        vst1q_f32(strength.as_mut_ptr().add(i), str1);
        vst1q_f32(experience.as_mut_ptr().add(i), exp1);
        i += 4;
    }
    i
}

/// Bit-exact `SupportCore.InRange` + engagement weight over `n` bases vs `(target_lat, target_lon)`.
/// Writes `-1.0` when outside latitude band or `great_circle_km > reach[i]`, else `strength[i] * (1.0 + exp[i] * 0.35)`.
pub fn support_reach_batch(
    target_lat: f32,
    target_lon: f32,
    base_lat: &[f32],
    base_lon: &[f32],
    reach_km: &[f32],
    strength: &[f32],
    experience: &[f32],
    out_weight: &mut [f32],
) -> Result<(), i32> {
    let n = base_lat.len();
    if base_lon.len() < n || reach_km.len() < n || strength.len() < n || experience.len() < n {
        return Err(-2);
    }
    if out_weight.len() < n {
        return Err(-3);
    }
    if n == 0 {
        return Ok(());
    }

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let mut i = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe {
            support_reach_avx2(
                target_lat,
                target_lon,
                base_lat,
                base_lon,
                reach_km,
                strength,
                experience,
                out_weight,
            )
        }
    } else {
        unsafe {
            support_reach_sse2(
                target_lat,
                target_lon,
                base_lat,
                base_lon,
                reach_km,
                strength,
                experience,
                out_weight,
            )
        }
    };

    #[cfg(target_arch = "aarch64")]
    let mut i = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe {
            support_reach_neon(
                target_lat,
                target_lon,
                base_lat,
                base_lon,
                reach_km,
                strength,
                experience,
                out_weight,
            )
        }
    } else {
        0
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let mut i = 0usize;

    while i < n {
        let rk = reach_km[i];
        let blat = base_lat[i];
        if (blat - target_lat).abs() * 111.0f32 > rk {
            out_weight[i] = -1.0f32;
        } else if great_circle_km(blat, base_lon[i], target_lat, target_lon) <= rk {
            out_weight[i] = strength[i] * (1.0f32 + experience[i] * 0.35f32);
        } else {
            out_weight[i] = -1.0f32;
        }
        i += 1;
    }

    Ok(())
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn support_reach_avx2(
    target_lat: f32,
    target_lon: f32,
    base_lat: &[f32],
    base_lon: &[f32],
    reach_km: &[f32],
    strength: &[f32],
    experience: &[f32],
    out_weight: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_avx2::abs;

    let n = base_lat.len();
    let tlat = _mm256_set1_ps(target_lat);
    let c111 = _mm256_set1_ps(111.0f32);
    let one = _mm256_set1_ps(1.0f32);
    let c35 = _mm256_set1_ps(0.35f32);
    let neg1 = _mm256_set1_ps(-1.0f32);

    let mut i = 0usize;
    while i + 8 <= n {
        let blat = _mm256_loadu_ps(base_lat.as_ptr().add(i));
        let rk = _mm256_loadu_ps(reach_km.as_ptr().add(i));
        let band = _mm256_mul_ps(abs(_mm256_sub_ps(blat, tlat)), c111);
        let out_band = _mm256_cmp_ps(band, rk, _CMP_GT_OQ);
        let mask = _mm256_movemask_ps(out_band) as u32;
        if mask == 0xFF {
            _mm256_storeu_ps(out_weight.as_mut_ptr().add(i), neg1);
        } else {
            let st = _mm256_loadu_ps(strength.as_ptr().add(i));
            let ex = _mm256_loadu_ps(experience.as_ptr().add(i));
            let w_vec = _mm256_mul_ps(st, _mm256_add_ps(one, _mm256_mul_ps(ex, c35)));
            let mut weights = [0.0f32; 8];
            _mm256_storeu_ps(weights.as_mut_ptr(), w_vec);
            for k in 0..8 {
                if (mask & (1u32 << k)) != 0 {
                    out_weight[i + k] = -1.0f32;
                } else if great_circle_km(base_lat[i + k], base_lon[i + k], target_lat, target_lon) <= reach_km[i + k] {
                    out_weight[i + k] = weights[k];
                } else {
                    out_weight[i + k] = -1.0f32;
                }
            }
        }
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn support_reach_sse2(
    target_lat: f32,
    target_lon: f32,
    base_lat: &[f32],
    base_lon: &[f32],
    reach_km: &[f32],
    strength: &[f32],
    experience: &[f32],
    out_weight: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_sse2::abs;

    let n = base_lat.len();
    let tlat = _mm_set1_ps(target_lat);
    let c111 = _mm_set1_ps(111.0f32);
    let one = _mm_set1_ps(1.0f32);
    let c35 = _mm_set1_ps(0.35f32);
    let neg1 = _mm_set1_ps(-1.0f32);

    let mut i = 0usize;
    while i + 4 <= n {
        let blat = _mm_loadu_ps(base_lat.as_ptr().add(i));
        let rk = _mm_loadu_ps(reach_km.as_ptr().add(i));
        let band = _mm_mul_ps(abs(_mm_sub_ps(blat, tlat)), c111);
        let out_band = _mm_cmpgt_ps(band, rk);
        let mask = _mm_movemask_ps(out_band) as u32;
        if mask == 0xF {
            _mm_storeu_ps(out_weight.as_mut_ptr().add(i), neg1);
        } else {
            let st = _mm_loadu_ps(strength.as_ptr().add(i));
            let ex = _mm_loadu_ps(experience.as_ptr().add(i));
            let w_vec = _mm_mul_ps(st, _mm_add_ps(one, _mm_mul_ps(ex, c35)));
            let mut weights = [0.0f32; 4];
            _mm_storeu_ps(weights.as_mut_ptr(), w_vec);
            for k in 0..4 {
                if (mask & (1u32 << k)) != 0 {
                    out_weight[i + k] = -1.0f32;
                } else if great_circle_km(base_lat[i + k], base_lon[i + k], target_lat, target_lon) <= reach_km[i + k] {
                    out_weight[i + k] = weights[k];
                } else {
                    out_weight[i + k] = -1.0f32;
                }
            }
        }
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn support_reach_neon(
    target_lat: f32,
    target_lon: f32,
    base_lat: &[f32],
    base_lon: &[f32],
    reach_km: &[f32],
    strength: &[f32],
    experience: &[f32],
    out_weight: &mut [f32],
) -> usize {
    use std::arch::aarch64::*;

    let n = base_lat.len();
    let tlat = vdupq_n_f32(target_lat);
    let c111 = vdupq_n_f32(111.0f32);
    let one = vdupq_n_f32(1.0f32);
    let c35 = vdupq_n_f32(0.35f32);
    let neg1 = vdupq_n_f32(-1.0f32);

    let mut i = 0usize;
    while i + 4 <= n {
        let blat = vld1q_f32(base_lat.as_ptr().add(i));
        let rk = vld1q_f32(reach_km.as_ptr().add(i));
        let band = vmulq_f32(vabsq_f32(vsubq_f32(blat, tlat)), c111);
        let out_band = vcgtq_f32(band, rk);
        let mut out_lanes = [0u32; 4];
        vst1q_u32(out_lanes.as_mut_ptr(), out_band);
        if (out_lanes[0] & out_lanes[1] & out_lanes[2] & out_lanes[3]) != 0 {
            vst1q_f32(out_weight.as_mut_ptr().add(i), neg1);
        } else {
            let st = vld1q_f32(strength.as_ptr().add(i));
            let ex = vld1q_f32(experience.as_ptr().add(i));
            let w_vec = vmulq_f32(st, vaddq_f32(one, vmulq_f32(ex, c35)));
            let mut weights = [0.0f32; 4];
            vst1q_f32(weights.as_mut_ptr(), w_vec);
            for k in 0..4 {
                if out_lanes[k] != 0 {
                    out_weight[i + k] = -1.0f32;
                } else if great_circle_km(base_lat[i + k], base_lon[i + k], target_lat, target_lon) <= reach_km[i + k] {
                    out_weight[i + k] = weights[k];
                } else {
                    out_weight[i + k] = -1.0f32;
                }
            }
        }
        i += 4;
    }
    i
}

/// Bit-exact `EconomyCore.WeekState` post-review state SoL lerp + radicalism step.
pub fn econ_state_week_post(
    nation_sol: &[f32],
    weekly_rad_delta: &[f32],
    cycle_rad: &[f32],
    state_sol: &mut [f32],
    state_rad: &mut [f32],
) -> Result<(), i32> {
    let n = nation_sol.len();
    if weekly_rad_delta.len() < n
        || cycle_rad.len() < n
        || state_sol.len() < n
        || state_rad.len() < n
    {
        return Err(-2);
    }
    if n == 0 {
        return Ok(());
    }

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let mut i = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe { econ_state_week_post_avx2(nation_sol, weekly_rad_delta, cycle_rad, state_sol, state_rad) }
    } else {
        unsafe { econ_state_week_post_sse2(nation_sol, weekly_rad_delta, cycle_rad, state_sol, state_rad) }
    };

    #[cfg(target_arch = "aarch64")]
    let mut i = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe { econ_state_week_post_neon(nation_sol, weekly_rad_delta, cycle_rad, state_sol, state_rad) }
    } else {
        0
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let mut i = 0usize;

    while i < n {
        state_sol[i] = lerp_clamped_f32(state_sol[i], nation_sol[i], 0.04f32);
        state_rad[i] = clamp01_f32((state_rad[i] + weekly_rad_delta[i]) + cycle_rad[i]);
        i += 1;
    }

    Ok(())
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
unsafe fn econ_state_week_post_avx2(
    nation_sol: &[f32],
    weekly_rad_delta: &[f32],
    cycle_rad: &[f32],
    state_sol: &mut [f32],
    state_rad: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_avx2::clamp01;

    let n = nation_sol.len();
    let w = _mm256_set1_ps(0.04f32);
    let mut i = 0usize;
    while i + 8 <= n {
        let s0 = _mm256_loadu_ps(state_sol.as_ptr().add(i));
        let ns = _mm256_loadu_ps(nation_sol.as_ptr().add(i));
        let s1 = _mm256_add_ps(s0, _mm256_mul_ps(_mm256_sub_ps(ns, s0), w));
        _mm256_storeu_ps(state_sol.as_mut_ptr().add(i), s1);

        let r0 = _mm256_loadu_ps(state_rad.as_ptr().add(i));
        let wr = _mm256_loadu_ps(weekly_rad_delta.as_ptr().add(i));
        let cr = _mm256_loadu_ps(cycle_rad.as_ptr().add(i));
        let r1 = clamp01(_mm256_add_ps(_mm256_add_ps(r0, wr), cr));
        _mm256_storeu_ps(state_rad.as_mut_ptr().add(i), r1);
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
unsafe fn econ_state_week_post_sse2(
    nation_sol: &[f32],
    weekly_rad_delta: &[f32],
    cycle_rad: &[f32],
    state_sol: &mut [f32],
    state_rad: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_sse2::clamp01;

    let n = nation_sol.len();
    let w = _mm_set1_ps(0.04f32);
    let mut i = 0usize;
    while i + 4 <= n {
        let s0 = _mm_loadu_ps(state_sol.as_ptr().add(i));
        let ns = _mm_loadu_ps(nation_sol.as_ptr().add(i));
        let s1 = _mm_add_ps(s0, _mm_mul_ps(_mm_sub_ps(ns, s0), w));
        _mm_storeu_ps(state_sol.as_mut_ptr().add(i), s1);

        let r0 = _mm_loadu_ps(state_rad.as_ptr().add(i));
        let wr = _mm_loadu_ps(weekly_rad_delta.as_ptr().add(i));
        let cr = _mm_loadu_ps(cycle_rad.as_ptr().add(i));
        let r1 = clamp01(_mm_add_ps(_mm_add_ps(r0, wr), cr));
        _mm_storeu_ps(state_rad.as_mut_ptr().add(i), r1);
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
unsafe fn econ_state_week_post_neon(
    nation_sol: &[f32],
    weekly_rad_delta: &[f32],
    cycle_rad: &[f32],
    state_sol: &mut [f32],
    state_rad: &mut [f32],
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::clamp01;

    let n = nation_sol.len();
    let w = vdupq_n_f32(0.04f32);
    let mut i = 0usize;
    while i + 4 <= n {
        let s0 = vld1q_f32(state_sol.as_ptr().add(i));
        let ns = vld1q_f32(nation_sol.as_ptr().add(i));
        let s1 = vaddq_f32(s0, vmulq_f32(vsubq_f32(ns, s0), w));
        vst1q_f32(state_sol.as_mut_ptr().add(i), s1);

        let r0 = vld1q_f32(state_rad.as_ptr().add(i));
        let wr = vld1q_f32(weekly_rad_delta.as_ptr().add(i));
        let cr = vld1q_f32(cycle_rad.as_ptr().add(i));
        let r1 = clamp01(vaddq_f32(vaddq_f32(r0, wr), cr));
        vst1q_f32(state_rad.as_mut_ptr().add(i), r1);
        i += 4;
    }
    i
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn combat_line_profile_matches_scalar_bit_exact() {
        let n = 19;
        let div: Vec<f32> = (0..n).map(|i| if i == 5 { 0.002 } else { 0.5 + (i as f32) * 0.7 }).collect();
        let hard: Vec<f32> = (0..n).map(|i| (i as f32) * 0.05).collect();
        let arm: Vec<f32> = (0..n).map(|i| (i as f32) * 2.1).collect();
        let prc: Vec<f32> = (0..n).map(|i| (i as f32) * 1.9).collect();
        let sup: Vec<f32> = (0..n).map(|i| -0.1 + (i as f32) * 0.09).collect();
        let rec: Vec<f32> = (0..n).map(|i| (i as f32) * 0.04).collect();

        let simd = combat_line_profile(&div, &hard, &arm, &prc, &sup, &rec).unwrap();

        let mut sd = 0.0f32;
        let (mut sh, mut sa, mut sp, mut ss, mut sr) = (0.0f32, 0.0f32, 0.0f32, 0.0f32, 0.0f32);
        for i in 0..n {
            let w = m_max_f32(0.01f32, div[i]);
            sd += w;
            sh += hard[i] * w;
            sa += arm[i] * w;
            sp += prc[i] * w;
            ss += clamp01_f32(sup[i]) * w;
            sr += rec[i] * w;
        }
        let exp = (sh / sd, sa / sd, sp / sd, ss / sd, sr / sd);
        assert_eq!(simd.0.to_bits(), exp.0.to_bits());
        assert_eq!(simd.1.to_bits(), exp.1.to_bits());
        assert_eq!(simd.2.to_bits(), exp.2.to_bits());
        assert_eq!(simd.3.to_bits(), exp.3.to_bits());
        assert_eq!(simd.4.to_bits(), exp.4.to_bits());
    }

    #[test]
    fn combat_side_totals_matches_scalar_bit_exact() {
        let n = 17;
        let raw_p: Vec<f32> = (0..n).map(|i| if i == 0 { 0.001 } else { 1.0 + (i as f32) * 0.85 }).collect();
        let off: Vec<f32> = (0..n).map(|i| 12.0 + (i as f32) * 1.3).collect();
        let atr: Vec<f32> = (0..n).map(|i| 0.9 + (i as f32) * 0.02).collect();
        let ast: Vec<f32> = (0..n).map(|i| 1.0 + (i as f32) * 0.01).collect();
        let ar: Vec<f32> = (0..n).map(|i| 0.8 + (i as f32) * 0.03).collect();
        let mun: Vec<f32> = (0..n).map(|i| 0.75 + (i as f32) * 0.02).collect();
        let enc: Vec<f32> = (0..n).map(|i| 1.0 - (i as f32) * 0.02).collect();
        let spr: Vec<f32> = (0..n).map(|i| 0.95 + (i as f32) * 0.015).collect();
        let ca: Vec<f32> = (0..n).map(|i| 1.05 + (i as f32) * 0.01).collect();
        let ds: Vec<f32> = (0..n).map(|i| 14.0 + (i as f32) * 1.1).collect();
        let dtr: Vec<f32> = (0..n).map(|i| 1.0 + (i as f32) * 0.015).collect();
        let dst: Vec<f32> = (0..n).map(|i| 1.1 - (i as f32) * 0.01).collect();
        let dr: Vec<f32> = (0..n).map(|i| 0.85 + (i as f32) * 0.025).collect();
        let cd: Vec<f32> = (0..n).map(|i| 0.98 + (i as f32) * 0.012).collect();
        let ent: Vec<f32> = (0..n).map(|i| (i as f32) * 0.05).collect();

        for w_off in [true, false] {
            let mut out = [0.0f64; 9];
            combat_side_totals(
                &raw_p, &off, &atr, &ast, &ar, &mun, &enc, &spr, &ca, &ds, &dtr, &dst, &dr, &cd,
                &ent, w_off, &mut out,
            )
            .unwrap();

            let mut exp = [0.0f64; 9];
            for i in 0..n {
                let power = m_max_f32(0.01f32, raw_p[i]);
                let ot = (((((((power * off[i]) * atr[i]) * ast[i]) * ar[i]) * mun[i]) * enc[i]) * spr[i]) * ca[i];
                let pt = ((((((power * ds[i]) * dtr[i]) * dst[i]) * dr[i]) * enc[i]) * spr[i]) * cd[i];
                let w = power as f64;
                exp[0] += ot as f64;
                exp[1] += pt as f64;
                exp[2] += ent[i] as f64;
                exp[3] += w;
                exp[4] += w * (if w_off { atr[i] } else { dtr[i] } as f64);
                exp[5] += w * (if w_off { ast[i] } else { dst[i] } as f64);
                exp[6] += w * (if w_off { ca[i] } else { cd[i] } as f64);
                exp[7] += w * (if w_off { ar[i] } else { dr[i] } as f64);
                exp[8] += w * (enc[i] as f64);
            }
            for k in 0..9 {
                assert_eq!(out[k].to_bits(), exp[k].to_bits(), "mismatch at k={k}, w_off={w_off}");
            }
        }
    }

    #[test]
    fn support_attrition_reach_and_econ_post_match_scalar_exact() {
        let n = 19;
        let threat: Vec<f32> = (0..n).map(|i| if i % 4 == 0 { 0.0 } else { (i as f32) * 1.4 }).collect();
        let mf: Vec<f32> = (0..n).map(|i| 0.8 + (i as f32) * 0.03).collect();
        let mut s1: Vec<f32> = (0..n).map(|i| 0.05 + (i as f32) * 0.06).collect();
        let mut e1: Vec<f32> = (0..n).map(|i| 0.1 + (i as f32) * 0.05).collect();
        let mut s2 = s1.clone();
        let mut e2 = e1.clone();
        let mut l1 = vec![0.0f32; n];
        let mut l2 = vec![0.0f32; n];

        support_attrition_batch(&threat, &mf, 0.12, 8.0, 0.1, &mut s1, &mut e1, &mut l1).unwrap();
        for i in 0..n {
            let th = threat[i];
            if th <= 0.0 {
                l2[i] = 0.0;
                continue;
            }
            let raw = ((0.12f32 * th) / (th + 8.0f32)) * mf[i];
            let loss = m_min_f32(raw, max0_f32(s2[i] - 0.1f32));
            if loss > 0.0 {
                s2[i] -= loss;
                l2[i] = loss;
                e2[i] = clamp01_f32(e2[i] + loss * 0.08f32);
            }
        }
        for i in 0..n {
            assert_eq!(s1[i].to_bits(), s2[i].to_bits());
            assert_eq!(e1[i].to_bits(), e2[i].to_bits());
            assert_eq!(l1[i].to_bits(), l2[i].to_bits());
        }

        let blat: Vec<f32> = (0..n).map(|i| 40.0 + (i as f32) * 1.5).collect();
        let blon: Vec<f32> = (0..n).map(|i| 10.0 + (i as f32) * 2.0).collect();
        let reach: Vec<f32> = (0..n).map(|i| 250.0 + (i as f32) * 60.0).collect();
        let mut rw = vec![0.0f32; n];
        support_reach_batch(48.0, 20.0, &blat, &blon, &reach, &s2, &e2, &mut rw).unwrap();
        for i in 0..n {
            let exp_w = if (blat[i] - 48.0f32).abs() * 111.0f32 > reach[i] {
                -1.0f32
            } else if great_circle_km(blat[i], blon[i], 48.0, 20.0) <= reach[i] {
                s2[i] * (1.0f32 + e2[i] * 0.35f32)
            } else {
                -1.0f32
            };
            assert_eq!(rw[i].to_bits(), exp_w.to_bits());
        }

        let nsol: Vec<f32> = (0..n).map(|i| 0.3 + (i as f32) * 0.03).collect();
        let wr: Vec<f32> = (0..n).map(|i| -0.02 + (i as f32) * 0.01).collect();
        let cr: Vec<f32> = (0..n).map(|i| (i as f32) * 0.004).collect();
        let mut ss1 = s1.clone();
        let mut sr1 = e1.clone();
        let mut ss2 = ss1.clone();
        let mut sr2 = sr1.clone();
        econ_state_week_post(&nsol, &wr, &cr, &mut ss1, &mut sr1).unwrap();
        for i in 0..n {
            ss2[i] = lerp_clamped_f32(ss2[i], nsol[i], 0.04f32);
            sr2[i] = clamp01_f32((sr2[i] + wr[i]) + cr[i]);
            assert_eq!(ss1[i].to_bits(), ss2[i].to_bits());
            assert_eq!(sr1[i].to_bits(), sr2[i].to_bits());
        }
    }
}
