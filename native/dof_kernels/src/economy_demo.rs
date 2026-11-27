// SPDX-License-Identifier: GPL-3.0-only
use crate::simd::{clamp01_f32, clamp_f32, lerp_clamped_f32, m_max_f32, m_min_f32, max0_f32};

pub const VITAL_FLAG_AT_WAR: u8 = 1 << 0;
pub const VITAL_FLAG_TRADITION_BONUS: u8 = 1 << 1;
pub const VITAL_FLAG_SECULAR_PENALTY: u8 = 1 << 2;
pub const VITAL_FLAG_PRONATALIST: u8 = 1 << 3;
pub const VITAL_FLAG_DEMO_BOOM: u8 = 1 << 4;
pub const VITAL_FLAG_AGE_ENABLED: u8 = 1 << 5;

#[inline(always)]
fn round_ties_even_i64(pop: i64, annual_rate: f32, interval: f32) -> i64 {
    let val = ((pop as f64) * (annual_rate as f64)) * (interval as f64);
    val.round_ties_even() as i64
}

/// Bit-exact batched `DemographyRulesHook.ManagedStepCohorts`.
#[allow(clippy::too_many_arguments)]
pub fn step_cohorts_batch(
    population: &[f32],
    review_births: &[i64],
    review_deaths: &[i64],
    young_share: &[f32],
    old_share: &[f32],
    maturation_annual: f32,
    ageing_annual: f32,
    mortality_old_weight: f32,
    interval: f32,
    out_young: &mut [f32],
    out_old: &mut [f32],
    out_working: &mut [f32],
) -> Result<(), i32> {
    let n = population.len();
    if review_births.len() < n
        || review_deaths.len() < n
        || young_share.len() < n
        || old_share.len() < n
    {
        return Err(-2);
    }
    if out_young.len() < n || out_old.len() < n || out_working.len() < n {
        return Err(-3);
    }
    if n == 0 {
        return Ok(());
    }

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let done = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe {
            step_cohorts_avx2(
                population,
                review_births,
                review_deaths,
                young_share,
                old_share,
                maturation_annual,
                ageing_annual,
                mortality_old_weight,
                interval,
                out_young,
                out_old,
                out_working,
            )
        }
    } else {
        unsafe {
            step_cohorts_sse2(
                population,
                review_births,
                review_deaths,
                young_share,
                old_share,
                maturation_annual,
                ageing_annual,
                mortality_old_weight,
                interval,
                out_young,
                out_old,
                out_working,
            )
        }
    };

    #[cfg(target_arch = "aarch64")]
    let done = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe {
            step_cohorts_neon(
                population,
                review_births,
                review_deaths,
                young_share,
                old_share,
                maturation_annual,
                ageing_annual,
                mortality_old_weight,
                interval,
                out_young,
                out_old,
                out_working,
            )
        }
    } else {
        0usize
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let done = 0usize;

    step_cohorts_scalar(
        done,
        n,
        population,
        review_births,
        review_deaths,
        young_share,
        old_share,
        maturation_annual,
        ageing_annual,
        mortality_old_weight,
        interval,
        out_young,
        out_old,
        out_working,
    );
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn step_cohorts_scalar(
    start: usize,
    end: usize,
    population: &[f32],
    review_births: &[i64],
    review_deaths: &[i64],
    young_share: &[f32],
    old_share: &[f32],
    maturation_annual: f32,
    ageing_annual: f32,
    mortality_old_weight: f32,
    interval: f32,
    out_young: &mut [f32],
    out_old: &mut [f32],
    out_working: &mut [f32],
) {
    for i in start..end {
        let pop = m_max_f32(1.0f32, population[i]);
        let born = clamp_f32((review_births[i] as f32) / pop, 0.0f32, 0.2f32);
        let died = clamp_f32((review_deaths[i] as f32) / pop, 0.0f32, 0.2f32);
        let ys = young_share[i];
        let os = old_share[i];
        let working = clamp_f32((1.0f32 - ys) - os, 0.05f32, 0.95f32);
        let matured = (ys * maturation_annual) * interval;
        let aged = (working * ageing_annual) * interval;
        let old_deaths = m_min_f32(os, died * mortality_old_weight);
        let rest = max0_f32(died - old_deaths);
        let next_y = clamp_f32(((ys - matured) - (rest * 0.5f32)) + born, 0.05f32, 0.62f32);
        let next_o = clamp_f32((os + aged) - old_deaths, 0.02f32, 0.45f32);
        let next_w = clamp_f32((1.0f32 - next_y) - next_o, 0.05f32, 0.95f32);
        out_young[i] = next_y;
        out_old[i] = next_o;
        out_working[i] = next_w;
    }
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn step_cohorts_avx2(
    population: &[f32],
    review_births: &[i64],
    review_deaths: &[i64],
    young_share: &[f32],
    old_share: &[f32],
    maturation_annual: f32,
    ageing_annual: f32,
    mortality_old_weight: f32,
    interval: f32,
    out_young: &mut [f32],
    out_old: &mut [f32],
    out_working: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_avx2::{clamp, m_max, m_min, max0};

    let n = population.len();
    let zero = _mm256_setzero_ps();
    let one = _mm256_set1_ps(1.0f32);
    let half = _mm256_set1_ps(0.5f32);
    let c02 = _mm256_set1_ps(0.2f32);
    let c05 = _mm256_set1_ps(0.05f32);
    let c95 = _mm256_set1_ps(0.95f32);
    let c62 = _mm256_set1_ps(0.62f32);
    let c02_lo = _mm256_set1_ps(0.02f32);
    let c45 = _mm256_set1_ps(0.45f32);
    let mat_v = _mm256_set1_ps(maturation_annual);
    let age_v = _mm256_set1_ps(ageing_annual);
    let mow_v = _mm256_set1_ps(mortality_old_weight);
    let int_v = _mm256_set1_ps(interval);

    let mut i = 0usize;
    while i + 8 <= n {
        let pop = m_max(one, _mm256_loadu_ps(population.as_ptr().add(i)));
        let rb = _mm256_set_ps(
            review_births[i + 7] as f32,
            review_births[i + 6] as f32,
            review_births[i + 5] as f32,
            review_births[i + 4] as f32,
            review_births[i + 3] as f32,
            review_births[i + 2] as f32,
            review_births[i + 1] as f32,
            review_births[i] as f32,
        );
        let rd = _mm256_set_ps(
            review_deaths[i + 7] as f32,
            review_deaths[i + 6] as f32,
            review_deaths[i + 5] as f32,
            review_deaths[i + 4] as f32,
            review_deaths[i + 3] as f32,
            review_deaths[i + 2] as f32,
            review_deaths[i + 1] as f32,
            review_deaths[i] as f32,
        );
        let born = clamp(_mm256_div_ps(rb, pop), zero, c02);
        let died = clamp(_mm256_div_ps(rd, pop), zero, c02);
        let ys = _mm256_loadu_ps(young_share.as_ptr().add(i));
        let os = _mm256_loadu_ps(old_share.as_ptr().add(i));
        let working = clamp(_mm256_sub_ps(_mm256_sub_ps(one, ys), os), c05, c95);
        let matured = _mm256_mul_ps(_mm256_mul_ps(ys, mat_v), int_v);
        let aged = _mm256_mul_ps(_mm256_mul_ps(working, age_v), int_v);
        let old_deaths = m_min(os, _mm256_mul_ps(died, mow_v));
        let rest = max0(_mm256_sub_ps(died, old_deaths));
        let next_y = clamp(
            _mm256_add_ps(_mm256_sub_ps(_mm256_sub_ps(ys, matured), _mm256_mul_ps(rest, half)), born),
            c05,
            c62,
        );
        let next_o = clamp(_mm256_sub_ps(_mm256_add_ps(os, aged), old_deaths), c02_lo, c45);
        let next_w = clamp(_mm256_sub_ps(_mm256_sub_ps(one, next_y), next_o), c05, c95);
        _mm256_storeu_ps(out_young.as_mut_ptr().add(i), next_y);
        _mm256_storeu_ps(out_old.as_mut_ptr().add(i), next_o);
        _mm256_storeu_ps(out_working.as_mut_ptr().add(i), next_w);
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn step_cohorts_sse2(
    population: &[f32],
    review_births: &[i64],
    review_deaths: &[i64],
    young_share: &[f32],
    old_share: &[f32],
    maturation_annual: f32,
    ageing_annual: f32,
    mortality_old_weight: f32,
    interval: f32,
    out_young: &mut [f32],
    out_old: &mut [f32],
    out_working: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_sse2::{clamp, m_max, m_min, max0};

    let n = population.len();
    let zero = _mm_setzero_ps();
    let one = _mm_set1_ps(1.0f32);
    let half = _mm_set1_ps(0.5f32);
    let c02 = _mm_set1_ps(0.2f32);
    let c05 = _mm_set1_ps(0.05f32);
    let c95 = _mm_set1_ps(0.95f32);
    let c62 = _mm_set1_ps(0.62f32);
    let c02_lo = _mm_set1_ps(0.02f32);
    let c45 = _mm_set1_ps(0.45f32);
    let mat_v = _mm_set1_ps(maturation_annual);
    let age_v = _mm_set1_ps(ageing_annual);
    let mow_v = _mm_set1_ps(mortality_old_weight);
    let int_v = _mm_set1_ps(interval);

    let mut i = 0usize;
    while i + 4 <= n {
        let pop = m_max(one, _mm_loadu_ps(population.as_ptr().add(i)));
        let rb = _mm_set_ps(
            review_births[i + 3] as f32,
            review_births[i + 2] as f32,
            review_births[i + 1] as f32,
            review_births[i] as f32,
        );
        let rd = _mm_set_ps(
            review_deaths[i + 3] as f32,
            review_deaths[i + 2] as f32,
            review_deaths[i + 1] as f32,
            review_deaths[i] as f32,
        );
        let born = clamp(_mm_div_ps(rb, pop), zero, c02);
        let died = clamp(_mm_div_ps(rd, pop), zero, c02);
        let ys = _mm_loadu_ps(young_share.as_ptr().add(i));
        let os = _mm_loadu_ps(old_share.as_ptr().add(i));
        let working = clamp(_mm_sub_ps(_mm_sub_ps(one, ys), os), c05, c95);
        let matured = _mm_mul_ps(_mm_mul_ps(ys, mat_v), int_v);
        let aged = _mm_mul_ps(_mm_mul_ps(working, age_v), int_v);
        let old_deaths = m_min(os, _mm_mul_ps(died, mow_v));
        let rest = max0(_mm_sub_ps(died, old_deaths));
        let next_y = clamp(
            _mm_add_ps(_mm_sub_ps(_mm_sub_ps(ys, matured), _mm_mul_ps(rest, half)), born),
            c05,
            c62,
        );
        let next_o = clamp(_mm_sub_ps(_mm_add_ps(os, aged), old_deaths), c02_lo, c45);
        let next_w = clamp(_mm_sub_ps(_mm_sub_ps(one, next_y), next_o), c05, c95);
        _mm_storeu_ps(out_young.as_mut_ptr().add(i), next_y);
        _mm_storeu_ps(out_old.as_mut_ptr().add(i), next_o);
        _mm_storeu_ps(out_working.as_mut_ptr().add(i), next_w);
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn step_cohorts_neon(
    population: &[f32],
    review_births: &[i64],
    review_deaths: &[i64],
    young_share: &[f32],
    old_share: &[f32],
    maturation_annual: f32,
    ageing_annual: f32,
    mortality_old_weight: f32,
    interval: f32,
    out_young: &mut [f32],
    out_old: &mut [f32],
    out_working: &mut [f32],
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::{clamp, m_max, m_min, max0};

    let n = population.len();
    let zero = vdupq_n_f32(0.0f32);
    let one = vdupq_n_f32(1.0f32);
    let half = vdupq_n_f32(0.5f32);
    let c02 = vdupq_n_f32(0.2f32);
    let c05 = vdupq_n_f32(0.05f32);
    let c95 = vdupq_n_f32(0.95f32);
    let c62 = vdupq_n_f32(0.62f32);
    let c02_lo = vdupq_n_f32(0.02f32);
    let c45 = vdupq_n_f32(0.45f32);
    let mat_v = vdupq_n_f32(maturation_annual);
    let age_v = vdupq_n_f32(ageing_annual);
    let mow_v = vdupq_n_f32(mortality_old_weight);
    let int_v = vdupq_n_f32(interval);

    let mut i = 0usize;
    while i + 4 <= n {
        let pop = m_max(one, vld1q_f32(population.as_ptr().add(i)));
        let rb_buf = [
            review_births[i] as f32,
            review_births[i + 1] as f32,
            review_births[i + 2] as f32,
            review_births[i + 3] as f32,
        ];
        let rd_buf = [
            review_deaths[i] as f32,
            review_deaths[i + 1] as f32,
            review_deaths[i + 2] as f32,
            review_deaths[i + 3] as f32,
        ];
        let rb = vld1q_f32(rb_buf.as_ptr());
        let rd = vld1q_f32(rd_buf.as_ptr());
        let born = clamp(vdivq_f32(rb, pop), zero, c02);
        let died = clamp(vdivq_f32(rd, pop), zero, c02);
        let ys = vld1q_f32(young_share.as_ptr().add(i));
        let os = vld1q_f32(old_share.as_ptr().add(i));
        let working = clamp(vsubq_f32(vsubq_f32(one, ys), os), c05, c95);
        let matured = vmulq_f32(vmulq_f32(ys, mat_v), int_v);
        let aged = vmulq_f32(vmulq_f32(working, age_v), int_v);
        let old_deaths = m_min(os, vmulq_f32(died, mow_v));
        let rest = max0(vsubq_f32(died, old_deaths));
        let next_y = clamp(
            vaddq_f32(vsubq_f32(vsubq_f32(ys, matured), vmulq_f32(rest, half)), born),
            c05,
            c62,
        );
        let next_o = clamp(vsubq_f32(vaddq_f32(os, aged), old_deaths), c02_lo, c45);
        let next_w = clamp(vsubq_f32(vsubq_f32(one, next_y), next_o), c05, c95);
        vst1q_f32(out_young.as_mut_ptr().add(i), next_y);
        vst1q_f32(out_old.as_mut_ptr().add(i), next_o);
        vst1q_f32(out_working.as_mut_ptr().add(i), next_w);
        i += 4;
    }
    i
}

/// Bit-exact `DemographicCycle.Aggregate` across CSR nation -> state slices.
#[allow(clippy::too_many_arguments)]
pub fn aggregate_nations(
    nation_count: usize,
    state_offsets: &[u32],
    state_pop: &[i64],
    state_young: &[f32],
    state_old: &[f32],
    state_births: &[i64],
    state_deaths: &[i64],
    state_migration: &[i64],
    state_count: usize,
    default_young: f32,
    default_old: f32,
    default_working: f32,
    ref_old: f32,
    out_young: &mut [f32],
    out_old: &mut [f32],
    out_working: &mut [f32],
    out_old_excess: &mut [f32],
    out_births: &mut [i64],
    out_deaths: &mut [i64],
    out_migration: &mut [i64],
) -> Result<(), i32> {
    if nation_count == 0 {
        return Ok(());
    }
    if state_offsets.len() <= nation_count
        || state_pop.len() < state_count
        || state_young.len() < state_count
        || state_old.len() < state_count
        || state_births.len() < state_count
        || state_deaths.len() < state_count
        || state_migration.len() < state_count
    {
        return Err(-2);
    }
    if out_young.len() < nation_count
        || out_old.len() < nation_count
        || out_working.len() < nation_count
        || out_old_excess.len() < nation_count
        || out_births.len() < nation_count
        || out_deaths.len() < nation_count
        || out_migration.len() < nation_count
    {
        return Err(-3);
    }

    let mut prev = state_offsets[0] as usize;
    if prev > state_count {
        return Err(-2);
    }
    for &off in &state_offsets[1..=nation_count] {
        let cur = off as usize;
        if cur < prev || cur > state_count {
            return Err(-2);
        }
        prev = cur;
    }

    for n in 0..nation_count {
        let start = state_offsets[n] as usize;
        let end = state_offsets[n + 1] as usize;
        if start == end {
            out_young[n] = default_young;
            out_old[n] = default_old;
            out_working[n] = default_working;
            out_old_excess[n] = 0.0f32;
            out_births[n] = 0;
            out_deaths[n] = 0;
            out_migration[n] = 0;
            continue;
        }

        let mut people = 0.0f64;
        let mut young = 0.0f64;
        let mut old = 0.0f64;
        let mut births = 0i64;
        let mut deaths = 0i64;
        let mut migration = 0i64;

        let mut s = start;
        #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
        {
            if std::arch::is_x86_feature_detected!("avx2") {
                s = unsafe {
                    aggregate_nations_chunk_avx2(
                        s,
                        end,
                        state_pop,
                        state_young,
                        state_old,
                        state_births,
                        state_deaths,
                        state_migration,
                        &mut people,
                        &mut young,
                        &mut old,
                        &mut births,
                        &mut deaths,
                        &mut migration,
                    )
                };
            } else {
                s = unsafe {
                    aggregate_nations_chunk_sse2(
                        s,
                        end,
                        state_pop,
                        state_young,
                        state_old,
                        state_births,
                        state_deaths,
                        state_migration,
                        &mut people,
                        &mut young,
                        &mut old,
                        &mut births,
                        &mut deaths,
                        &mut migration,
                    )
                };
            }
        }
        #[cfg(target_arch = "aarch64")]
        {
            if std::arch::is_aarch64_feature_detected!("neon") {
                s = unsafe {
                    aggregate_nations_chunk_neon(
                        s,
                        end,
                        state_pop,
                        state_young,
                        state_old,
                        state_births,
                        state_deaths,
                        state_migration,
                        &mut people,
                        &mut young,
                        &mut old,
                        &mut births,
                        &mut deaths,
                        &mut migration,
                    )
                };
            }
        }

        while s < end {
            let size = state_pop[s].max(0);
            let size_f32 = size as f32;
            people += size as f64;
            // C# `size * state.YoungShare` widens `long` -> `float`, multiplies in `f32`, then widens to `double`.
            young += (size_f32 * state_young[s]) as f64;
            old += (size_f32 * state_old[s]) as f64;
            births = births.wrapping_add(state_births[s]);
            deaths = deaths.wrapping_add(state_deaths[s]);
            migration = migration.wrapping_add(state_migration[s]);
            s += 1;
        }

        let total = if people > 1.0f64 { people } else { 1.0f64 };
        let y = (young / total) as f32;
        let o = (old / total) as f32;
        out_young[n] = y;
        out_old[n] = o;
        out_working[n] = clamp_f32((1.0f32 - y) - o, 0.05f32, 0.95f32);
        out_old_excess[n] = max0_f32(o - ref_old);
        out_births[n] = births;
        out_deaths[n] = deaths;
        out_migration[n] = migration;
    }

    Ok(())
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn aggregate_nations_chunk_avx2(
    mut s: usize,
    end: usize,
    state_pop: &[i64],
    state_young: &[f32],
    state_old: &[f32],
    state_births: &[i64],
    state_deaths: &[i64],
    state_migration: &[i64],
    people: &mut f64,
    young: &mut f64,
    old: &mut f64,
    births: &mut i64,
    deaths: &mut i64,
    migration: &mut i64,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    let mut p_f = [0.0f32; 8];
    let mut y_d = [0.0f64; 4];
    let mut o_d = [0.0f64; 4];
    let mut p_d = [0.0f64; 8];

    while s + 8 <= end {
        for k in 0..8 {
            let sz = state_pop[s + k].max(0);
            p_d[k] = sz as f64;
            p_f[k] = sz as f32;
            *births = births.wrapping_add(state_births[s + k]);
            *deaths = deaths.wrapping_add(state_deaths[s + k]);
            *migration = migration.wrapping_add(state_migration[s + k]);
        }
        let pv = _mm256_loadu_ps(p_f.as_ptr());
        let yv = _mm256_mul_ps(pv, _mm256_loadu_ps(state_young.as_ptr().add(s)));
        let ov = _mm256_mul_ps(pv, _mm256_loadu_ps(state_old.as_ptr().add(s)));

        _mm256_storeu_pd(y_d.as_mut_ptr(), _mm256_cvtps_pd(_mm256_castps256_ps128(yv)));
        _mm256_storeu_pd(o_d.as_mut_ptr(), _mm256_cvtps_pd(_mm256_castps256_ps128(ov)));
        for k in 0..4 {
            *people += p_d[k];
            *young += y_d[k];
            *old += o_d[k];
        }
        _mm256_storeu_pd(y_d.as_mut_ptr(), _mm256_cvtps_pd(_mm256_extractf128_ps(yv, 1)));
        _mm256_storeu_pd(o_d.as_mut_ptr(), _mm256_cvtps_pd(_mm256_extractf128_ps(ov, 1)));
        for k in 0..4 {
            *people += p_d[4 + k];
            *young += y_d[k];
            *old += o_d[k];
        }
        s += 8;
    }
    s
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn aggregate_nations_chunk_sse2(
    mut s: usize,
    end: usize,
    state_pop: &[i64],
    state_young: &[f32],
    state_old: &[f32],
    state_births: &[i64],
    state_deaths: &[i64],
    state_migration: &[i64],
    people: &mut f64,
    young: &mut f64,
    old: &mut f64,
    births: &mut i64,
    deaths: &mut i64,
    migration: &mut i64,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    let mut p_f = [0.0f32; 4];
    let mut y_f = [0.0f32; 4];
    let mut o_f = [0.0f32; 4];
    let mut p_d = [0.0f64; 4];

    while s + 4 <= end {
        for k in 0..4 {
            let sz = state_pop[s + k].max(0);
            p_d[k] = sz as f64;
            p_f[k] = sz as f32;
            *births = births.wrapping_add(state_births[s + k]);
            *deaths = deaths.wrapping_add(state_deaths[s + k]);
            *migration = migration.wrapping_add(state_migration[s + k]);
        }
        let pv = _mm_loadu_ps(p_f.as_ptr());
        let yv = _mm_mul_ps(pv, _mm_loadu_ps(state_young.as_ptr().add(s)));
        let ov = _mm_mul_ps(pv, _mm_loadu_ps(state_old.as_ptr().add(s)));
        _mm_storeu_ps(y_f.as_mut_ptr(), yv);
        _mm_storeu_ps(o_f.as_mut_ptr(), ov);
        for k in 0..4 {
            *people += p_d[k];
            *young += y_f[k] as f64;
            *old += o_f[k] as f64;
        }
        s += 4;
    }
    s
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn aggregate_nations_chunk_neon(
    mut s: usize,
    end: usize,
    state_pop: &[i64],
    state_young: &[f32],
    state_old: &[f32],
    state_births: &[i64],
    state_deaths: &[i64],
    state_migration: &[i64],
    people: &mut f64,
    young: &mut f64,
    old: &mut f64,
    births: &mut i64,
    deaths: &mut i64,
    migration: &mut i64,
) -> usize {
    use std::arch::aarch64::*;

    let mut p_f = [0.0f32; 4];
    let mut y_d = [0.0f64; 2];
    let mut o_d = [0.0f64; 2];
    let mut p_d = [0.0f64; 4];

    while s + 4 <= end {
        for k in 0..4 {
            let sz = state_pop[s + k].max(0);
            p_d[k] = sz as f64;
            p_f[k] = sz as f32;
            *births = births.wrapping_add(state_births[s + k]);
            *deaths = deaths.wrapping_add(state_deaths[s + k]);
            *migration = migration.wrapping_add(state_migration[s + k]);
        }
        let pv = vld1q_f32(p_f.as_ptr());
        let yv = vmulq_f32(pv, vld1q_f32(state_young.as_ptr().add(s)));
        let ov = vmulq_f32(pv, vld1q_f32(state_old.as_ptr().add(s)));

        vst1q_f64(y_d.as_mut_ptr(), vcvt_f64_f32(vget_low_f32(yv)));
        vst1q_f64(o_d.as_mut_ptr(), vcvt_f64_f32(vget_low_f32(ov)));
        for k in 0..2 {
            *people += p_d[k];
            *young += y_d[k];
            *old += o_d[k];
        }
        vst1q_f64(y_d.as_mut_ptr(), vcvt_f64_f32(vget_high_f32(yv)));
        vst1q_f64(o_d.as_mut_ptr(), vcvt_f64_f32(vget_high_f32(ov)));
        for k in 0..2 {
            *people += p_d[2 + k];
            *young += y_d[k];
            *old += o_d[k];
        }
        s += 4;
    }
    s
}

/// Bit-exact `HouseholdEconomy.ReviewCore` group labour aggregation + household group dynamics.
#[allow(clippy::too_many_arguments)]
pub fn household_groups(
    group_count: usize,
    sizes: &[i64],
    is_upper: &[u8],
    anchor_wealth: &[f32],
    employment: &mut [f32],
    wealth: &mut [f32],
    sol: &mut [f32],
    radicalism: &mut [f32],
    working_share: f32,
    need_employment: bool,
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
) -> Result<(f32, f32, f32, f32, f64), i32> {
    if sizes.len() < group_count
        || is_upper.len() < group_count
        || anchor_wealth.len() < group_count
        || employment.len() < group_count
        || wealth.len() < group_count
        || sol.len() < group_count
        || radicalism.len() < group_count
    {
        return Err(-2);
    }

    // Pass 1: exact strict-order labour/employment sum.
    let mut sum_labour = 0.0f64;
    let mut sum_employed = 0.0f64;
    if need_employment {
        for i in 0..group_count {
            let class_weight = if is_upper[i] != 0 { 0.15f64 } else { 1.0f64 };
            // C# `g.Size * share * 1e-6 * ...`: `long * float` -> `f32`, then `* 1e-6` -> `f64`.
            let active = (((sizes[i] as f32) * working_share) as f64) * 1e-6_f64 * class_weight;
            sum_labour += active;
            sum_employed += active * (employment[i] as f64);
        }
    } else {
        for i in 0..group_count {
            let class_weight = if is_upper[i] != 0 { 0.15f64 } else { 1.0f64 };
            sum_labour += (((sizes[i] as f32) * working_share) as f64) * 1e-6_f64 * class_weight;
        }
    }

    let labour = sum_labour as f32;
    let mut non_building = non_building_jobs_in;
    if need_employment {
        let emp = if labour <= 0.0f32 {
            0.0f32
        } else {
            (sum_employed as f32) / labour
        };
        non_building = max0_f32((labour * emp) - building_jobs);
    }

    let dmg = clamp01_f32(damage);
    non_building = max0_f32(
        non_building * (1.0f32 + clamp_f32(annual_conditions, -0.25f32, 0.15f32) * interval),
    );
    let emp_target = if labour <= 0.0f32 {
        0.0f32
    } else {
        clamp01_f32(((non_building + building_jobs) * (1.0f32 - dmg * 0.5f32)) / labour)
    };
    let basic_needs = ((food * 0.6f32) + (energy * 0.25f32)) + (medicine * 0.15f32);
    let scarcity = max0_f32((1.0f32 - basic_needs) - scarcity_tolerance);
    let unemployment = max0_f32(0.9f32 - emp_target);
    let stress = clamp01_f32(((scarcity * 0.65f32) + (unemployment * 0.25f32)) + (dmg * 0.25f32));
    let emp_rate = clamp01_f32(employment_rate);
    let w_rate = clamp01_f32(wealth_rate);
    let rad_target = state_rad + (stress * 0.2f32);

    if group_count == 0 {
        return Ok((non_building, emp_target, basic_needs, stress, 0.0f64));
    }

    // Pass 2: vectorized per-group updates.
    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let done = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe {
            household_groups_pass2_avx2(
                group_count,
                anchor_wealth,
                employment,
                wealth,
                sol,
                radicalism,
                emp_target,
                emp_rate,
                w_rate,
                scarcity,
                dmg,
                nation_sol,
                stress,
                rad_target,
            )
        }
    } else {
        unsafe {
            household_groups_pass2_sse2(
                group_count,
                anchor_wealth,
                employment,
                wealth,
                sol,
                radicalism,
                emp_target,
                emp_rate,
                w_rate,
                scarcity,
                dmg,
                nation_sol,
                stress,
                rad_target,
            )
        }
    };

    #[cfg(target_arch = "aarch64")]
    let done = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe {
            household_groups_pass2_neon(
                group_count,
                anchor_wealth,
                employment,
                wealth,
                sol,
                radicalism,
                emp_target,
                emp_rate,
                w_rate,
                scarcity,
                dmg,
                nation_sol,
                stress,
                rad_target,
            )
        }
    } else {
        0usize
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let done = 0usize;

    household_groups_pass2_scalar(
        done,
        group_count,
        anchor_wealth,
        employment,
        wealth,
        sol,
        radicalism,
        emp_target,
        emp_rate,
        w_rate,
        scarcity,
        dmg,
        nation_sol,
        stress,
        rad_target,
    );

    // Strict index-order f64 reduction for living sum parity.
    let mut living_sum = 0.0f64;
    for i in 0..group_count {
        living_sum += (sizes[i] as f64) * (sol[i] as f64);
    }

    Ok((non_building, emp_target, basic_needs, stress, living_sum))
}

#[allow(clippy::too_many_arguments)]
fn household_groups_pass2_scalar(
    start: usize,
    end: usize,
    anchor_wealth: &[f32],
    employment: &mut [f32],
    wealth: &mut [f32],
    sol: &mut [f32],
    radicalism: &mut [f32],
    emp_target: f32,
    emp_rate: f32,
    wealth_rate: f32,
    scarcity: f32,
    damage: f32,
    nation_sol: f32,
    stress: f32,
    rad_target: f32,
) {
    let sc_pen = scarcity * 0.18f32;
    let dmg_pen = damage * 0.12f32;
    let stress_pen = stress * 0.25f32;
    for i in start..end {
        let e = lerp_clamped_f32(employment[i], emp_target, emp_rate);
        employment[i] = e;
        let tw = clamp01_f32(((anchor_wealth[i] + ((e - 0.9f32) * 0.22f32)) - sc_pen) - dmg_pen);
        let w = lerp_clamped_f32(wealth[i], tw, wealth_rate);
        wealth[i] = w;
        let tl = clamp01_f32((nation_sol + ((w - 0.5f32) * 0.12f32)) - stress_pen);
        let s_val = lerp_clamped_f32(sol[i], tl, 0.04f32);
        sol[i] = s_val;
        radicalism[i] = clamp01_f32(lerp_clamped_f32(radicalism[i], rad_target, 0.025f32));
    }
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn household_groups_pass2_avx2(
    n: usize,
    anchor_wealth: &[f32],
    employment: &mut [f32],
    wealth: &mut [f32],
    sol: &mut [f32],
    radicalism: &mut [f32],
    emp_target: f32,
    emp_rate: f32,
    wealth_rate: f32,
    scarcity: f32,
    damage: f32,
    nation_sol: f32,
    stress: f32,
    rad_target: f32,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_avx2::clamp01;

    let et_v = _mm256_set1_ps(emp_target);
    let er_v = _mm256_set1_ps(emp_rate);
    let wr_v = _mm256_set1_ps(wealth_rate);
    let c09 = _mm256_set1_ps(0.9f32);
    let c22 = _mm256_set1_ps(0.22f32);
    let sc_pen = _mm256_set1_ps(scarcity * 0.18f32);
    let dmg_pen = _mm256_set1_ps(damage * 0.12f32);
    let nsol_v = _mm256_set1_ps(nation_sol);
    let c05 = _mm256_set1_ps(0.5f32);
    let c12 = _mm256_set1_ps(0.12f32);
    let stress_pen = _mm256_set1_ps(stress * 0.25f32);
    let r_sol = _mm256_set1_ps(0.04f32);
    let rad_t = _mm256_set1_ps(rad_target);
    let r_rad = _mm256_set1_ps(0.025f32);

    let mut i = 0usize;
    while i + 8 <= n {
        let e0 = _mm256_loadu_ps(employment.as_ptr().add(i));
        let e = _mm256_add_ps(e0, _mm256_mul_ps(_mm256_sub_ps(et_v, e0), er_v));
        _mm256_storeu_ps(employment.as_mut_ptr().add(i), e);

        let anc = _mm256_loadu_ps(anchor_wealth.as_ptr().add(i));
        let tw = clamp01(_mm256_sub_ps(
            _mm256_sub_ps(
                _mm256_add_ps(anc, _mm256_mul_ps(_mm256_sub_ps(e, c09), c22)),
                sc_pen,
            ),
            dmg_pen,
        ));

        let w0 = _mm256_loadu_ps(wealth.as_ptr().add(i));
        let w = _mm256_add_ps(w0, _mm256_mul_ps(_mm256_sub_ps(tw, w0), wr_v));
        _mm256_storeu_ps(wealth.as_mut_ptr().add(i), w);

        let tl = clamp01(_mm256_sub_ps(
            _mm256_add_ps(nsol_v, _mm256_mul_ps(_mm256_sub_ps(w, c05), c12)),
            stress_pen,
        ));
        let s0 = _mm256_loadu_ps(sol.as_ptr().add(i));
        let s = _mm256_add_ps(s0, _mm256_mul_ps(_mm256_sub_ps(tl, s0), r_sol));
        _mm256_storeu_ps(sol.as_mut_ptr().add(i), s);

        let r0 = _mm256_loadu_ps(radicalism.as_ptr().add(i));
        let r = clamp01(_mm256_add_ps(r0, _mm256_mul_ps(_mm256_sub_ps(rad_t, r0), r_rad)));
        _mm256_storeu_ps(radicalism.as_mut_ptr().add(i), r);
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn household_groups_pass2_sse2(
    n: usize,
    anchor_wealth: &[f32],
    employment: &mut [f32],
    wealth: &mut [f32],
    sol: &mut [f32],
    radicalism: &mut [f32],
    emp_target: f32,
    emp_rate: f32,
    wealth_rate: f32,
    scarcity: f32,
    damage: f32,
    nation_sol: f32,
    stress: f32,
    rad_target: f32,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_sse2::clamp01;

    let et_v = _mm_set1_ps(emp_target);
    let er_v = _mm_set1_ps(emp_rate);
    let wr_v = _mm_set1_ps(wealth_rate);
    let c09 = _mm_set1_ps(0.9f32);
    let c22 = _mm_set1_ps(0.22f32);
    let sc_pen = _mm_set1_ps(scarcity * 0.18f32);
    let dmg_pen = _mm_set1_ps(damage * 0.12f32);
    let nsol_v = _mm_set1_ps(nation_sol);
    let c05 = _mm_set1_ps(0.5f32);
    let c12 = _mm_set1_ps(0.12f32);
    let stress_pen = _mm_set1_ps(stress * 0.25f32);
    let r_sol = _mm_set1_ps(0.04f32);
    let rad_t = _mm_set1_ps(rad_target);
    let r_rad = _mm_set1_ps(0.025f32);

    let mut i = 0usize;
    while i + 4 <= n {
        let e0 = _mm_loadu_ps(employment.as_ptr().add(i));
        let e = _mm_add_ps(e0, _mm_mul_ps(_mm_sub_ps(et_v, e0), er_v));
        _mm_storeu_ps(employment.as_mut_ptr().add(i), e);

        let anc = _mm_loadu_ps(anchor_wealth.as_ptr().add(i));
        let tw = clamp01(_mm_sub_ps(
            _mm_sub_ps(_mm_add_ps(anc, _mm_mul_ps(_mm_sub_ps(e, c09), c22)), sc_pen),
            dmg_pen,
        ));
        let w0 = _mm_loadu_ps(wealth.as_ptr().add(i));
        let w = _mm_add_ps(w0, _mm_mul_ps(_mm_sub_ps(tw, w0), wr_v));
        _mm_storeu_ps(wealth.as_mut_ptr().add(i), w);

        let tl = clamp01(_mm_sub_ps(
            _mm_add_ps(nsol_v, _mm_mul_ps(_mm_sub_ps(w, c05), c12)),
            stress_pen,
        ));
        let s0 = _mm_loadu_ps(sol.as_ptr().add(i));
        let s = _mm_add_ps(s0, _mm_mul_ps(_mm_sub_ps(tl, s0), r_sol));
        _mm_storeu_ps(sol.as_mut_ptr().add(i), s);

        let r0 = _mm_loadu_ps(radicalism.as_ptr().add(i));
        let r = clamp01(_mm_add_ps(r0, _mm_mul_ps(_mm_sub_ps(rad_t, r0), r_rad)));
        _mm_storeu_ps(radicalism.as_mut_ptr().add(i), r);
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn household_groups_pass2_neon(
    n: usize,
    anchor_wealth: &[f32],
    employment: &mut [f32],
    wealth: &mut [f32],
    sol: &mut [f32],
    radicalism: &mut [f32],
    emp_target: f32,
    emp_rate: f32,
    wealth_rate: f32,
    scarcity: f32,
    damage: f32,
    nation_sol: f32,
    stress: f32,
    rad_target: f32,
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::clamp01;

    let et_v = vdupq_n_f32(emp_target);
    let er_v = vdupq_n_f32(emp_rate);
    let wr_v = vdupq_n_f32(wealth_rate);
    let c09 = vdupq_n_f32(0.9f32);
    let c22 = vdupq_n_f32(0.22f32);
    let sc_pen = vdupq_n_f32(scarcity * 0.18f32);
    let dmg_pen = vdupq_n_f32(damage * 0.12f32);
    let nsol_v = vdupq_n_f32(nation_sol);
    let c05 = vdupq_n_f32(0.5f32);
    let c12 = vdupq_n_f32(0.12f32);
    let stress_pen = vdupq_n_f32(stress * 0.25f32);
    let r_sol = vdupq_n_f32(0.04f32);
    let rad_t = vdupq_n_f32(rad_target);
    let r_rad = vdupq_n_f32(0.025f32);

    let mut i = 0usize;
    while i + 4 <= n {
        let e0 = vld1q_f32(employment.as_ptr().add(i));
        let e = vaddq_f32(e0, vmulq_f32(vsubq_f32(et_v, e0), er_v));
        vst1q_f32(employment.as_mut_ptr().add(i), e);

        let anc = vld1q_f32(anchor_wealth.as_ptr().add(i));
        let tw = clamp01(vsubq_f32(
            vsubq_f32(vaddq_f32(anc, vmulq_f32(vsubq_f32(e, c09), c22)), sc_pen),
            dmg_pen,
        ));
        let w0 = vld1q_f32(wealth.as_ptr().add(i));
        let w = vaddq_f32(w0, vmulq_f32(vsubq_f32(tw, w0), wr_v));
        vst1q_f32(wealth.as_mut_ptr().add(i), w);

        let tl = clamp01(vsubq_f32(
            vaddq_f32(nsol_v, vmulq_f32(vsubq_f32(w, c05), c12)),
            stress_pen,
        ));
        let s0 = vld1q_f32(sol.as_ptr().add(i));
        let s = vaddq_f32(s0, vmulq_f32(vsubq_f32(tl, s0), r_sol));
        vst1q_f32(sol.as_mut_ptr().add(i), s);

        let r0 = vld1q_f32(radicalism.as_ptr().add(i));
        let r = clamp01(vaddq_f32(r0, vmulq_f32(vsubq_f32(rad_t, r0), r_rad)));
        vst1q_f32(radicalism.as_mut_ptr().add(i), r);
        i += 4;
    }
    i
}

/// Bit-exact batched `HouseholdEconomy.VitalRatesCore` + `ReviewBirths`/`ReviewDeaths`.
#[allow(clippy::too_many_arguments)]
pub fn vital_rates_batch(
    pop: &[i64],
    war_damage: &[f32],
    hospitals: &[i32],
    literacy: &[f32],
    urbanization: &[f32],
    sol: &[f32],
    radicalism: &[f32],
    young_share: &[f32],
    old_share: &[f32],
    welfare_spending: &[f32],
    inflation: &[f32],
    war_support: &[f32],
    nation_stability: &[f32],
    state_stability: &[f32],
    capital_lat: &[f32],
    food: &[f32],
    energy: &[f32],
    medicine: &[f32],
    flags: &[u8],
    scarcity_tolerance: f32,
    birth_base: f32,
    low_lit_birth: f32,
    death_base: f32,
    shortage_death: f32,
    ref_old: f32,
    mort_old_weight: f32,
    interval: f32,
    out_birth: &mut [f32],
    out_death: &mut [f32],
    out_old_burden: &mut [f32],
    mut out_review_births: Option<&mut [i64]>,
    mut out_review_deaths: Option<&mut [i64]>,
) -> Result<(), i32> {
    let n = pop.len();
    if war_damage.len() < n
        || hospitals.len() < n
        || literacy.len() < n
        || urbanization.len() < n
        || sol.len() < n
        || radicalism.len() < n
        || young_share.len() < n
        || old_share.len() < n
        || welfare_spending.len() < n
        || inflation.len() < n
        || war_support.len() < n
        || nation_stability.len() < n
        || state_stability.len() < n
        || capital_lat.len() < n
        || food.len() < n
        || energy.len() < n
        || medicine.len() < n
        || flags.len() < n
    {
        return Err(-2);
    }
    if out_birth.len() < n
        || out_death.len() < n
        || out_old_burden.len() < n
        || out_review_births.as_ref().is_some_and(|s| s.len() < n)
        || out_review_deaths.as_ref().is_some_and(|s| s.len() < n)
    {
        return Err(-3);
    }
    if n == 0 {
        return Ok(());
    }

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let done = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe {
            vital_rates_avx2(
                pop,
                war_damage,
                hospitals,
                literacy,
                urbanization,
                sol,
                radicalism,
                young_share,
                old_share,
                welfare_spending,
                inflation,
                war_support,
                nation_stability,
                state_stability,
                capital_lat,
                food,
                energy,
                medicine,
                flags,
                scarcity_tolerance,
                birth_base,
                low_lit_birth,
                death_base,
                shortage_death,
                ref_old,
                mort_old_weight,
                out_birth,
                out_death,
                out_old_burden,
            )
        }
    } else {
        unsafe {
            vital_rates_sse2(
                pop,
                war_damage,
                hospitals,
                literacy,
                urbanization,
                sol,
                radicalism,
                young_share,
                old_share,
                welfare_spending,
                inflation,
                war_support,
                nation_stability,
                state_stability,
                capital_lat,
                food,
                energy,
                medicine,
                flags,
                scarcity_tolerance,
                birth_base,
                low_lit_birth,
                death_base,
                shortage_death,
                ref_old,
                mort_old_weight,
                out_birth,
                out_death,
                out_old_burden,
            )
        }
    };

    #[cfg(target_arch = "aarch64")]
    let done = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe {
            vital_rates_neon(
                pop,
                war_damage,
                hospitals,
                literacy,
                urbanization,
                sol,
                radicalism,
                young_share,
                old_share,
                welfare_spending,
                inflation,
                war_support,
                nation_stability,
                state_stability,
                capital_lat,
                food,
                energy,
                medicine,
                flags,
                scarcity_tolerance,
                birth_base,
                low_lit_birth,
                death_base,
                shortage_death,
                ref_old,
                mort_old_weight,
                out_birth,
                out_death,
                out_old_burden,
            )
        }
    } else {
        0usize
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let done = 0usize;

    vital_rates_scalar_rates(
        done,
        n,
        pop,
        war_damage,
        hospitals,
        literacy,
        urbanization,
        sol,
        radicalism,
        young_share,
        old_share,
        welfare_spending,
        inflation,
        war_support,
        nation_stability,
        state_stability,
        capital_lat,
        food,
        energy,
        medicine,
        flags,
        scarcity_tolerance,
        birth_base,
        low_lit_birth,
        death_base,
        shortage_death,
        ref_old,
        mort_old_weight,
        out_birth,
        out_death,
        out_old_burden,
    );

    if out_review_births.is_some() || out_review_deaths.is_some() {
        for i in 0..n {
            let rb = round_ties_even_i64(pop[i], out_birth[i], interval);
            if let Some(rb_slice) = out_review_births.as_deref_mut() {
                rb_slice[i] = rb;
            }
            if let Some(rd_slice) = out_review_deaths.as_deref_mut() {
                let raw_rd = round_ties_even_i64(pop[i], out_death[i], interval);
                let cap = pop[i].saturating_add(rb);
                rd_slice[i] = if cap < raw_rd { cap } else { raw_rd };
            }
        }
    }

    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn vital_rates_scalar_rates(
    start: usize,
    end: usize,
    pop: &[i64],
    war_damage: &[f32],
    hospitals: &[i32],
    literacy: &[f32],
    urbanization: &[f32],
    sol: &[f32],
    radicalism: &[f32],
    young_share: &[f32],
    old_share: &[f32],
    welfare_spending: &[f32],
    inflation: &[f32],
    war_support: &[f32],
    nation_stability: &[f32],
    state_stability: &[f32],
    capital_lat: &[f32],
    food: &[f32],
    energy: &[f32],
    medicine: &[f32],
    flags: &[u8],
    scarcity_tolerance: f32,
    birth_base: f32,
    low_lit_birth: f32,
    death_base: f32,
    shortage_death: f32,
    ref_old: f32,
    mort_old_weight: f32,
    out_birth: &mut [f32],
    out_death: &mut [f32],
    out_old_burden: &mut [f32],
) {
    for i in start..end {
        let dmg = clamp01_f32(war_damage[i]);
        let fd = food[i];
        let en = energy[i];
        let md = medicine[i];
        let coverage = ((fd * 0.6f32) + (en * 0.25f32)) + (md * 0.15f32);
        let scarcity = max0_f32((1.0f32 - coverage) - scarcity_tolerance);

        let denom = m_max_f32(0.1f32, (pop[i] as f32) / 1e6_f32);
        let capacity = (hospitals[i] as f32) / denom;
        let health = (capacity / (1.0f32 + capacity)) * md;

        let lit = literacy[i];
        let urb = urbanization[i];
        let wel = welfare_spending[i];
        let inf = inflation[i];
        let ws = war_support[i];
        let n_stab = nation_stability[i];
        let s_stab = state_stability[i];
        let rad = radicalism[i];
        let ys = young_share[i];
        let os = old_share[i];
        let flg = flags[i];
        let at_war = (flg & VITAL_FLAG_AT_WAR) != 0;

        let low_lit_b = (1.0f32 - lit) * low_lit_birth;
        let urban_squeeze = if urb > 0.55f32 {
            -(urb - 0.55f32) * 0.010f32
        } else {
            0.0f32
        };
        let post_industrial = if lit > 0.82f32 && urb > 0.60f32 {
            ((-(lit - 0.82f32)) * (urb - 0.60f32)) * 0.032f32
        } else {
            0.0f32
        };
        let family_welfare = if urb > 0.01f32 && wel > 0.06f32 {
            (wel - 0.06f32) * 0.042f32
        } else {
            0.0f32
        };
        let tradition_bonus = if urb > 0.01f32 && (flg & VITAL_FLAG_TRADITION_BONUS) != 0 {
            0.0035f32
        } else {
            0.0f32
        };
        let secular_penalty = if urb > 0.01f32 && (flg & VITAL_FLAG_SECULAR_PENALTY) != 0 {
            -0.0020f32
        } else {
            0.0f32
        };
        let pronatalist = if (flg & VITAL_FLAG_PRONATALIST) != 0 {
            0.0065f32
        } else if (flg & VITAL_FLAG_DEMO_BOOM) != 0 {
            0.0085f32
        } else {
            0.0f32
        };
        let inflation_drag = if urb > 0.01f32 && inf > 0.08f32 {
            (-clamp_f32(inf - 0.08f32, 0.0f32, 0.30f32)) * 0.014f32
        } else {
            0.0f32
        };

        let exhaustion = clamp01_f32(1.0f32 - ws);
        let mut mob_shock = if exhaustion > 0.08f32 {
            -m_min_f32(0.0075f32, (exhaustion - 0.08f32) * 0.02f32)
        } else {
            0.0f32
        };
        if at_war {
            mob_shock -= 0.0020f32;
        }
        let war_nesting = if dmg > 0.04f32 {
            -m_min_f32(0.006f32, (dmg - 0.04f32) * 0.012f32)
        } else {
            0.0f32
        };
        let peace_boom = if !at_war && n_stab > 0.72f32 && dmg < 0.02f32 {
            (n_stab - 0.72f32) * 0.008f32
        } else {
            0.0f32
        };

        let pre_age_birth = clamp_f32(
            ((((((((((birth_base + low_lit_b) + urban_squeeze) + post_industrial)
                + family_welfare)
                + tradition_bonus)
                + secular_penalty)
                + pronatalist)
                + inflation_drag)
                + mob_shock)
                + war_nesting)
                + peace_boom,
            0.0f32,
            0.08f32,
        );
        let fertility_factor = if (flg & VITAL_FLAG_AGE_ENABLED) != 0 {
            clamp_f32(1.0f32 - (0.28f32 - ys) * 2.0f32, 0.35f32, 1.25f32)
        } else {
            1.0f32
        };
        let age_birth_effect = pre_age_birth * (fertility_factor - 1.0f32);
        let birth = clamp_f32(pre_age_birth + age_birth_effect, 0.0f32, 0.08f32);

        let poverty_d = (1.0f32 - sol[i]) * 0.004f32;
        let shortage_d = scarcity * shortage_death;
        let war_d = dmg * 0.012f32;
        let famine_d = if fd < 0.40f32 {
            (0.40f32 - fd) * 0.045f32
        } else {
            0.0f32
        };
        let winter_d = if capital_lat[i].abs() > 35.0f32 && en < 0.45f32 {
            (0.45f32 - en) * 0.015f32
        } else {
            0.0f32
        };
        let epidemic_d = if md < 0.35f32 && (dmg > 0.10f32 || scarcity > 0.15f32) {
            (0.35f32 - md) * 0.020f32
        } else {
            0.0f32
        };
        let violence_d = if rad > 0.65f32 && s_stab < 0.25f32 {
            ((rad - 0.65f32) * (0.25f32 - s_stab)) * 0.035f32
        } else {
            0.0f32
        };

        let pre_health_death = clamp_f32(
            ((((((death_base + poverty_d) + shortage_d) + war_d) + famine_d) + winter_d)
                + epidemic_d)
                + violence_d,
            0.0f32,
            0.08f32,
        );
        let hospital_relief = (pre_health_death * health) * 0.25f32;
        let post_health_death = pre_health_death - hospital_relief;
        let mortality_factor = if (flg & VITAL_FLAG_AGE_ENABLED) != 0 {
            let excess = max0_f32(os - ref_old);
            1.0f32 + (excess * mort_old_weight) * 3.0f32
        } else {
            1.0f32
        };
        let old_age_death_effect = post_health_death * (mortality_factor - 1.0f32);
        let death = clamp_f32(post_health_death + old_age_death_effect, 0.0f32, 0.08f32);

        let old_burden = clamp01_f32(
            max0_f32(os - ref_old) * 4.5f32
                + if death > birth {
                    (death - birth) * 35.0f32
                } else {
                    0.0f32
                },
        );

        out_birth[i] = birth;
        out_death[i] = death;
        out_old_burden[i] = old_burden;
    }
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn vital_rates_avx2(
    pop: &[i64],
    war_damage: &[f32],
    hospitals: &[i32],
    literacy: &[f32],
    urbanization: &[f32],
    sol: &[f32],
    radicalism: &[f32],
    young_share: &[f32],
    old_share: &[f32],
    welfare_spending: &[f32],
    inflation: &[f32],
    war_support: &[f32],
    nation_stability: &[f32],
    state_stability: &[f32],
    capital_lat: &[f32],
    food: &[f32],
    energy: &[f32],
    medicine: &[f32],
    flags: &[u8],
    scarcity_tolerance: f32,
    birth_base: f32,
    low_lit_birth: f32,
    death_base: f32,
    shortage_death: f32,
    ref_old: f32,
    mort_old_weight: f32,
    out_birth: &mut [f32],
    out_death: &mut [f32],
    out_old_burden: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_avx2::{abs, clamp, clamp01, m_max, m_min, max0};

    let n = pop.len();
    let zero = _mm256_setzero_ps();
    let neg_zero = _mm256_set1_ps(-0.0f32);
    let one = _mm256_set1_ps(1.0f32);
    let two = _mm256_set1_ps(2.0f32);
    let three = _mm256_set1_ps(3.0f32);
    let c_001 = _mm256_set1_ps(0.01f32);
    let c_002 = _mm256_set1_ps(0.02f32);
    let c_004 = _mm256_set1_ps(0.04f32);
    let c_006 = _mm256_set1_ps(0.06f32);
    let c_008 = _mm256_set1_ps(0.08f32);
    let c_010 = _mm256_set1_ps(0.10f32);
    let c_015 = _mm256_set1_ps(0.15f32);
    let c_025 = _mm256_set1_ps(0.25f32);
    let c_028 = _mm256_set1_ps(0.28f32);
    let c_030 = _mm256_set1_ps(0.30f32);
    let c_035 = _mm256_set1_ps(0.35f32);
    let c_040 = _mm256_set1_ps(0.40f32);
    let c_045 = _mm256_set1_ps(0.45f32);
    let c_055 = _mm256_set1_ps(0.55f32);
    let c_060 = _mm256_set1_ps(0.60f32);
    let c_065 = _mm256_set1_ps(0.65f32);
    let c_072 = _mm256_set1_ps(0.72f32);
    let c_082 = _mm256_set1_ps(0.82f32);
    let c_125 = _mm256_set1_ps(1.25f32);
    let c_1e6 = _mm256_set1_ps(1e6_f32);
    let c_01 = _mm256_set1_ps(0.1f32);
    let c_35 = _mm256_set1_ps(35.0f32);
    let c_45 = _mm256_set1_ps(4.5f32);

    let sc_tol = _mm256_set1_ps(scarcity_tolerance);
    let b_base = _mm256_set1_ps(birth_base);
    let low_lit_w = _mm256_set1_ps(low_lit_birth);
    let d_base = _mm256_set1_ps(death_base);
    let short_d_w = _mm256_set1_ps(shortage_death);
    let ref_old_v = _mm256_set1_ps(ref_old);
    let mow_v = _mm256_set1_ps(mort_old_weight);

    let c_0010 = _mm256_set1_ps(0.010f32);
    let c_0032 = _mm256_set1_ps(0.032f32);
    let c_0042 = _mm256_set1_ps(0.042f32);
    let c_0035_pos = _mm256_set1_ps(0.0035f32);
    let c_0020_neg = _mm256_set1_ps(-0.0020f32);
    let c_0020_pos = _mm256_set1_ps(0.0020f32);
    let c_0065 = _mm256_set1_ps(0.0065f32);
    let c_0085 = _mm256_set1_ps(0.0085f32);
    let c_0014 = _mm256_set1_ps(0.014f32);
    let c_00075 = _mm256_set1_ps(0.0075f32);
    let c_0020 = _mm256_set1_ps(0.02f32);
    let c_00060 = _mm256_set1_ps(0.006f32);
    let c_0012 = _mm256_set1_ps(0.012f32);
    let c_0008 = _mm256_set1_ps(0.008f32);
    let c_0004 = _mm256_set1_ps(0.004f32);
    let c_0045 = _mm256_set1_ps(0.045f32);
    let c_0015 = _mm256_set1_ps(0.015f32);
    let c_0020d = _mm256_set1_ps(0.020f32);
    let c_0035d = _mm256_set1_ps(0.035f32);

    let b0 = _mm256_set1_epi32(VITAL_FLAG_AT_WAR as i32);
    let b1 = _mm256_set1_epi32(VITAL_FLAG_TRADITION_BONUS as i32);
    let b2 = _mm256_set1_epi32(VITAL_FLAG_SECULAR_PENALTY as i32);
    let b3 = _mm256_set1_epi32(VITAL_FLAG_PRONATALIST as i32);
    let b4 = _mm256_set1_epi32(VITAL_FLAG_DEMO_BOOM as i32);
    let b5 = _mm256_set1_epi32(VITAL_FLAG_AGE_ENABLED as i32);

    let mut i = 0usize;
    while i + 8 <= n {
        let dmg = clamp01(_mm256_loadu_ps(war_damage.as_ptr().add(i)));
        let fd = _mm256_loadu_ps(food.as_ptr().add(i));
        let en = _mm256_loadu_ps(energy.as_ptr().add(i));
        let md = _mm256_loadu_ps(medicine.as_ptr().add(i));
        let coverage = _mm256_add_ps(
            _mm256_add_ps(_mm256_mul_ps(fd, c_060), _mm256_mul_ps(en, c_025)),
            _mm256_mul_ps(md, c_015),
        );
        let scarcity = max0(_mm256_sub_ps(_mm256_sub_ps(one, coverage), sc_tol));

        let pop_f = _mm256_set_ps(
            pop[i + 7] as f32,
            pop[i + 6] as f32,
            pop[i + 5] as f32,
            pop[i + 4] as f32,
            pop[i + 3] as f32,
            pop[i + 2] as f32,
            pop[i + 1] as f32,
            pop[i] as f32,
        );
        let denom = m_max(c_01, _mm256_div_ps(pop_f, c_1e6));
        let hosp_f = _mm256_cvtepi32_ps(_mm256_loadu_si256(hospitals.as_ptr().add(i) as *const __m256i));
        let capacity = _mm256_div_ps(hosp_f, denom);
        let health = _mm256_mul_ps(_mm256_div_ps(capacity, _mm256_add_ps(one, capacity)), md);

        let lit = _mm256_loadu_ps(literacy.as_ptr().add(i));
        let urb = _mm256_loadu_ps(urbanization.as_ptr().add(i));
        let wel = _mm256_loadu_ps(welfare_spending.as_ptr().add(i));
        let inf = _mm256_loadu_ps(inflation.as_ptr().add(i));
        let ws = _mm256_loadu_ps(war_support.as_ptr().add(i));
        let n_stab = _mm256_loadu_ps(nation_stability.as_ptr().add(i));
        let s_stab = _mm256_loadu_ps(state_stability.as_ptr().add(i));
        let rad = _mm256_loadu_ps(radicalism.as_ptr().add(i));
        let ys = _mm256_loadu_ps(young_share.as_ptr().add(i));
        let os = _mm256_loadu_ps(old_share.as_ptr().add(i));
        let sol_v = _mm256_loadu_ps(sol.as_ptr().add(i));
        let lat_v = _mm256_loadu_ps(capital_lat.as_ptr().add(i));

        let f_bytes = _mm_loadl_epi64(flags.as_ptr().add(i) as *const __m128i);
        let f_i32 = _mm256_cvtepu8_epi32(f_bytes);
        let m_war = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, b0), b0));
        let m_trad = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, b1), b1));
        let m_sec = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, b2), b2));
        let m_pro = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, b3), b3));
        let m_boom = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, b4), b4));
        let m_age = _mm256_castsi256_ps(_mm256_cmpeq_epi32(_mm256_and_si256(f_i32, b5), b5));

        let low_lit_b = _mm256_mul_ps(_mm256_sub_ps(one, lit), low_lit_w);
        let m_urb55 = _mm256_cmp_ps(urb, c_055, _CMP_GT_OQ);
        let urban_squeeze = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(_mm256_xor_ps(_mm256_sub_ps(urb, c_055), neg_zero), c_0010),
            m_urb55,
        );
        let m_post = _mm256_and_ps(
            _mm256_cmp_ps(lit, c_082, _CMP_GT_OQ),
            _mm256_cmp_ps(urb, c_060, _CMP_GT_OQ),
        );
        let post_industrial = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(
                _mm256_mul_ps(
                    _mm256_xor_ps(_mm256_sub_ps(lit, c_082), neg_zero),
                    _mm256_sub_ps(urb, c_060),
                ),
                c_0032,
            ),
            m_post,
        );
        let m_urb01 = _mm256_cmp_ps(urb, c_001, _CMP_GT_OQ);
        let m_wel = _mm256_and_ps(m_urb01, _mm256_cmp_ps(wel, c_006, _CMP_GT_OQ));
        let family_welfare = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(_mm256_sub_ps(wel, c_006), c_0042),
            m_wel,
        );
        let tradition_bonus = _mm256_blendv_ps(zero, c_0035_pos, _mm256_and_ps(m_urb01, m_trad));
        let secular_penalty = _mm256_blendv_ps(zero, c_0020_neg, _mm256_and_ps(m_urb01, m_sec));
        let pronatalist = _mm256_blendv_ps(
            _mm256_blendv_ps(zero, c_0085, m_boom),
            c_0065,
            m_pro,
        );
        let m_inf = _mm256_and_ps(m_urb01, _mm256_cmp_ps(inf, c_008, _CMP_GT_OQ));
        let inf_clamp = clamp(_mm256_sub_ps(inf, c_008), zero, c_030);
        let inflation_drag = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(_mm256_xor_ps(inf_clamp, neg_zero), c_0014),
            m_inf,
        );

        let exhaustion = clamp01(_mm256_sub_ps(one, ws));
        let m_exh = _mm256_cmp_ps(exhaustion, c_008, _CMP_GT_OQ);
        let mob_base = _mm256_blendv_ps(
            zero,
            _mm256_xor_ps(
                m_min(c_00075, _mm256_mul_ps(_mm256_sub_ps(exhaustion, c_008), c_0020)),
                neg_zero,
            ),
            m_exh,
        );
        let mob_shock = _mm256_blendv_ps(mob_base, _mm256_sub_ps(mob_base, c_0020_pos), m_war);
        let m_nest = _mm256_cmp_ps(dmg, c_004, _CMP_GT_OQ);
        let war_nesting = _mm256_blendv_ps(
            zero,
            _mm256_xor_ps(
                m_min(c_00060, _mm256_mul_ps(_mm256_sub_ps(dmg, c_004), c_0012)),
                neg_zero,
            ),
            m_nest,
        );
        let m_peace = _mm256_andnot_ps(
            m_war,
            _mm256_and_ps(
                _mm256_cmp_ps(n_stab, c_072, _CMP_GT_OQ),
                _mm256_cmp_ps(dmg, c_002, _CMP_LT_OQ),
            ),
        );
        let peace_boom = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(_mm256_sub_ps(n_stab, c_072), c_0008),
            m_peace,
        );

        let mut pre_age = _mm256_add_ps(b_base, low_lit_b);
        pre_age = _mm256_add_ps(pre_age, urban_squeeze);
        pre_age = _mm256_add_ps(pre_age, post_industrial);
        pre_age = _mm256_add_ps(pre_age, family_welfare);
        pre_age = _mm256_add_ps(pre_age, tradition_bonus);
        pre_age = _mm256_add_ps(pre_age, secular_penalty);
        pre_age = _mm256_add_ps(pre_age, pronatalist);
        pre_age = _mm256_add_ps(pre_age, inflation_drag);
        pre_age = _mm256_add_ps(pre_age, mob_shock);
        pre_age = _mm256_add_ps(pre_age, war_nesting);
        pre_age = clamp(_mm256_add_ps(pre_age, peace_boom), zero, c_008);

        let fert_raw = clamp(
            _mm256_sub_ps(one, _mm256_mul_ps(_mm256_sub_ps(c_028, ys), two)),
            c_035,
            c_125,
        );
        let fert = _mm256_blendv_ps(one, fert_raw, m_age);
        let age_eff = _mm256_mul_ps(pre_age, _mm256_sub_ps(fert, one));
        let birth = clamp(_mm256_add_ps(pre_age, age_eff), zero, c_008);

        let poverty_d = _mm256_mul_ps(_mm256_sub_ps(one, sol_v), c_0004);
        let shortage_d = _mm256_mul_ps(scarcity, short_d_w);
        let war_d = _mm256_mul_ps(dmg, c_0012);
        let famine_d = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(_mm256_sub_ps(c_040, fd), c_0045),
            _mm256_cmp_ps(fd, c_040, _CMP_LT_OQ),
        );
        let m_winter = _mm256_and_ps(
            _mm256_cmp_ps(abs(lat_v), c_35, _CMP_GT_OQ),
            _mm256_cmp_ps(en, c_045, _CMP_LT_OQ),
        );
        let winter_d = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(_mm256_sub_ps(c_045, en), c_0015),
            m_winter,
        );
        let m_epi = _mm256_and_ps(
            _mm256_cmp_ps(md, c_035, _CMP_LT_OQ),
            _mm256_or_ps(
                _mm256_cmp_ps(dmg, c_010, _CMP_GT_OQ),
                _mm256_cmp_ps(scarcity, c_015, _CMP_GT_OQ),
            ),
        );
        let epidemic_d = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(_mm256_sub_ps(c_035, md), c_0020d),
            m_epi,
        );
        let m_viol = _mm256_and_ps(
            _mm256_cmp_ps(rad, c_065, _CMP_GT_OQ),
            _mm256_cmp_ps(s_stab, c_025, _CMP_LT_OQ),
        );
        let violence_d = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(
                _mm256_mul_ps(_mm256_sub_ps(rad, c_065), _mm256_sub_ps(c_025, s_stab)),
                c_0035d,
            ),
            m_viol,
        );

        let mut pre_health = _mm256_add_ps(d_base, poverty_d);
        pre_health = _mm256_add_ps(pre_health, shortage_d);
        pre_health = _mm256_add_ps(pre_health, war_d);
        pre_health = _mm256_add_ps(pre_health, famine_d);
        pre_health = _mm256_add_ps(pre_health, winter_d);
        pre_health = _mm256_add_ps(pre_health, epidemic_d);
        pre_health = clamp(_mm256_add_ps(pre_health, violence_d), zero, c_008);

        let hosp_relief = _mm256_mul_ps(_mm256_mul_ps(pre_health, health), c_025);
        let post_health = _mm256_sub_ps(pre_health, hosp_relief);
        let excess = max0(_mm256_sub_ps(os, ref_old_v));
        let mort_raw = _mm256_add_ps(one, _mm256_mul_ps(_mm256_mul_ps(excess, mow_v), three));
        let mort = _mm256_blendv_ps(one, mort_raw, m_age);
        let death = clamp(
            _mm256_add_ps(post_health, _mm256_mul_ps(post_health, _mm256_sub_ps(mort, one))),
            zero,
            c_008,
        );

        let burden_age = _mm256_mul_ps(excess, c_45);
        let burden_dep = _mm256_blendv_ps(
            zero,
            _mm256_mul_ps(_mm256_sub_ps(death, birth), c_35),
            _mm256_cmp_ps(death, birth, _CMP_GT_OQ),
        );
        let old_burden = clamp01(_mm256_add_ps(burden_age, burden_dep));

        _mm256_storeu_ps(out_birth.as_mut_ptr().add(i), birth);
        _mm256_storeu_ps(out_death.as_mut_ptr().add(i), death);
        _mm256_storeu_ps(out_old_burden.as_mut_ptr().add(i), old_burden);
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn vital_rates_sse2(
    pop: &[i64],
    war_damage: &[f32],
    hospitals: &[i32],
    literacy: &[f32],
    urbanization: &[f32],
    sol: &[f32],
    radicalism: &[f32],
    young_share: &[f32],
    old_share: &[f32],
    welfare_spending: &[f32],
    inflation: &[f32],
    war_support: &[f32],
    nation_stability: &[f32],
    state_stability: &[f32],
    capital_lat: &[f32],
    food: &[f32],
    energy: &[f32],
    medicine: &[f32],
    flags: &[u8],
    scarcity_tolerance: f32,
    birth_base: f32,
    low_lit_birth: f32,
    death_base: f32,
    shortage_death: f32,
    ref_old: f32,
    mort_old_weight: f32,
    out_birth: &mut [f32],
    out_death: &mut [f32],
    out_old_burden: &mut [f32],
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_sse2::{abs, clamp, clamp01, m_max, m_min, max0, select};

    let n = pop.len();
    let zero = _mm_setzero_ps();
    let neg_zero = _mm_set1_ps(-0.0f32);
    let one = _mm_set1_ps(1.0f32);
    let two = _mm_set1_ps(2.0f32);
    let three = _mm_set1_ps(3.0f32);
    let c_001 = _mm_set1_ps(0.01f32);
    let c_002 = _mm_set1_ps(0.02f32);
    let c_004 = _mm_set1_ps(0.04f32);
    let c_006 = _mm_set1_ps(0.06f32);
    let c_008 = _mm_set1_ps(0.08f32);
    let c_010 = _mm_set1_ps(0.10f32);
    let c_015 = _mm_set1_ps(0.15f32);
    let c_025 = _mm_set1_ps(0.25f32);
    let c_028 = _mm_set1_ps(0.28f32);
    let c_030 = _mm_set1_ps(0.30f32);
    let c_035 = _mm_set1_ps(0.35f32);
    let c_040 = _mm_set1_ps(0.40f32);
    let c_045 = _mm_set1_ps(0.45f32);
    let c_055 = _mm_set1_ps(0.55f32);
    let c_060 = _mm_set1_ps(0.60f32);
    let c_065 = _mm_set1_ps(0.65f32);
    let c_072 = _mm_set1_ps(0.72f32);
    let c_082 = _mm_set1_ps(0.82f32);
    let c_125 = _mm_set1_ps(1.25f32);
    let c_1e6 = _mm_set1_ps(1e6_f32);
    let c_01 = _mm_set1_ps(0.1f32);
    let c_35 = _mm_set1_ps(35.0f32);
    let c_45 = _mm_set1_ps(4.5f32);

    let sc_tol = _mm_set1_ps(scarcity_tolerance);
    let b_base = _mm_set1_ps(birth_base);
    let low_lit_w = _mm_set1_ps(low_lit_birth);
    let d_base = _mm_set1_ps(death_base);
    let short_d_w = _mm_set1_ps(shortage_death);
    let ref_old_v = _mm_set1_ps(ref_old);
    let mow_v = _mm_set1_ps(mort_old_weight);

    let c_0010 = _mm_set1_ps(0.010f32);
    let c_0032 = _mm_set1_ps(0.032f32);
    let c_0042 = _mm_set1_ps(0.042f32);
    let c_0035_pos = _mm_set1_ps(0.0035f32);
    let c_0020_neg = _mm_set1_ps(-0.0020f32);
    let c_0020_pos = _mm_set1_ps(0.0020f32);
    let c_0065 = _mm_set1_ps(0.0065f32);
    let c_0085 = _mm_set1_ps(0.0085f32);
    let c_0014 = _mm_set1_ps(0.014f32);
    let c_00075 = _mm_set1_ps(0.0075f32);
    let c_0020 = _mm_set1_ps(0.02f32);
    let c_00060 = _mm_set1_ps(0.006f32);
    let c_0012 = _mm_set1_ps(0.012f32);
    let c_0008 = _mm_set1_ps(0.008f32);
    let c_0004 = _mm_set1_ps(0.004f32);
    let c_0045 = _mm_set1_ps(0.045f32);
    let c_0015 = _mm_set1_ps(0.015f32);
    let c_0020d = _mm_set1_ps(0.020f32);
    let c_0035d = _mm_set1_ps(0.035f32);

    let b0 = _mm_set1_epi32(VITAL_FLAG_AT_WAR as i32);
    let b1 = _mm_set1_epi32(VITAL_FLAG_TRADITION_BONUS as i32);
    let b2 = _mm_set1_epi32(VITAL_FLAG_SECULAR_PENALTY as i32);
    let b3 = _mm_set1_epi32(VITAL_FLAG_PRONATALIST as i32);
    let b4 = _mm_set1_epi32(VITAL_FLAG_DEMO_BOOM as i32);
    let b5 = _mm_set1_epi32(VITAL_FLAG_AGE_ENABLED as i32);

    let mut i = 0usize;
    while i + 4 <= n {
        let dmg = clamp01(_mm_loadu_ps(war_damage.as_ptr().add(i)));
        let fd = _mm_loadu_ps(food.as_ptr().add(i));
        let en = _mm_loadu_ps(energy.as_ptr().add(i));
        let md = _mm_loadu_ps(medicine.as_ptr().add(i));
        let coverage = _mm_add_ps(
            _mm_add_ps(_mm_mul_ps(fd, c_060), _mm_mul_ps(en, c_025)),
            _mm_mul_ps(md, c_015),
        );
        let scarcity = max0(_mm_sub_ps(_mm_sub_ps(one, coverage), sc_tol));

        let pop_f = _mm_set_ps(
            pop[i + 3] as f32,
            pop[i + 2] as f32,
            pop[i + 1] as f32,
            pop[i] as f32,
        );
        let denom = m_max(c_01, _mm_div_ps(pop_f, c_1e6));
        let hosp_f = _mm_cvtepi32_ps(_mm_loadu_si128(hospitals.as_ptr().add(i) as *const __m128i));
        let capacity = _mm_div_ps(hosp_f, denom);
        let health = _mm_mul_ps(_mm_div_ps(capacity, _mm_add_ps(one, capacity)), md);

        let lit = _mm_loadu_ps(literacy.as_ptr().add(i));
        let urb = _mm_loadu_ps(urbanization.as_ptr().add(i));
        let wel = _mm_loadu_ps(welfare_spending.as_ptr().add(i));
        let inf = _mm_loadu_ps(inflation.as_ptr().add(i));
        let ws = _mm_loadu_ps(war_support.as_ptr().add(i));
        let n_stab = _mm_loadu_ps(nation_stability.as_ptr().add(i));
        let s_stab = _mm_loadu_ps(state_stability.as_ptr().add(i));
        let rad = _mm_loadu_ps(radicalism.as_ptr().add(i));
        let ys = _mm_loadu_ps(young_share.as_ptr().add(i));
        let os = _mm_loadu_ps(old_share.as_ptr().add(i));
        let sol_v = _mm_loadu_ps(sol.as_ptr().add(i));
        let lat_v = _mm_loadu_ps(capital_lat.as_ptr().add(i));

        let f_i32 = _mm_set_epi32(
            flags[i + 3] as i32,
            flags[i + 2] as i32,
            flags[i + 1] as i32,
            flags[i] as i32,
        );
        let m_war = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(f_i32, b0), b0));
        let m_trad = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(f_i32, b1), b1));
        let m_sec = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(f_i32, b2), b2));
        let m_pro = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(f_i32, b3), b3));
        let m_boom = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(f_i32, b4), b4));
        let m_age = _mm_castsi128_ps(_mm_cmpeq_epi32(_mm_and_si128(f_i32, b5), b5));

        let low_lit_b = _mm_mul_ps(_mm_sub_ps(one, lit), low_lit_w);
        let urban_squeeze = select(
            _mm_cmpgt_ps(urb, c_055),
            _mm_mul_ps(_mm_xor_ps(_mm_sub_ps(urb, c_055), neg_zero), c_0010),
            zero,
        );
        let m_post = _mm_and_ps(_mm_cmpgt_ps(lit, c_082), _mm_cmpgt_ps(urb, c_060));
        let post_industrial = select(
            m_post,
            _mm_mul_ps(
                _mm_mul_ps(
                    _mm_xor_ps(_mm_sub_ps(lit, c_082), neg_zero),
                    _mm_sub_ps(urb, c_060),
                ),
                c_0032,
            ),
            zero,
        );
        let m_urb01 = _mm_cmpgt_ps(urb, c_001);
        let family_welfare = select(
            _mm_and_ps(m_urb01, _mm_cmpgt_ps(wel, c_006)),
            _mm_mul_ps(_mm_sub_ps(wel, c_006), c_0042),
            zero,
        );
        let tradition_bonus = select(_mm_and_ps(m_urb01, m_trad), c_0035_pos, zero);
        let secular_penalty = select(_mm_and_ps(m_urb01, m_sec), c_0020_neg, zero);
        let pronatalist = select(m_pro, c_0065, select(m_boom, c_0085, zero));
        let inf_clamp = clamp(_mm_sub_ps(inf, c_008), zero, c_030);
        let inflation_drag = select(
            _mm_and_ps(m_urb01, _mm_cmpgt_ps(inf, c_008)),
            _mm_mul_ps(_mm_xor_ps(inf_clamp, neg_zero), c_0014),
            zero,
        );

        let exhaustion = clamp01(_mm_sub_ps(one, ws));
        let mob_base = select(
            _mm_cmpgt_ps(exhaustion, c_008),
            _mm_xor_ps(
                m_min(c_00075, _mm_mul_ps(_mm_sub_ps(exhaustion, c_008), c_0020)),
                neg_zero,
            ),
            zero,
        );
        let mob_shock = select(m_war, _mm_sub_ps(mob_base, c_0020_pos), mob_base);
        let war_nesting = select(
            _mm_cmpgt_ps(dmg, c_004),
            _mm_xor_ps(
                m_min(c_00060, _mm_mul_ps(_mm_sub_ps(dmg, c_004), c_0012)),
                neg_zero,
            ),
            zero,
        );
        let m_peace = _mm_andnot_ps(
            m_war,
            _mm_and_ps(_mm_cmpgt_ps(n_stab, c_072), _mm_cmplt_ps(dmg, c_002)),
        );
        let peace_boom = select(m_peace, _mm_mul_ps(_mm_sub_ps(n_stab, c_072), c_0008), zero);

        let mut pre_age = _mm_add_ps(b_base, low_lit_b);
        pre_age = _mm_add_ps(pre_age, urban_squeeze);
        pre_age = _mm_add_ps(pre_age, post_industrial);
        pre_age = _mm_add_ps(pre_age, family_welfare);
        pre_age = _mm_add_ps(pre_age, tradition_bonus);
        pre_age = _mm_add_ps(pre_age, secular_penalty);
        pre_age = _mm_add_ps(pre_age, pronatalist);
        pre_age = _mm_add_ps(pre_age, inflation_drag);
        pre_age = _mm_add_ps(pre_age, mob_shock);
        pre_age = _mm_add_ps(pre_age, war_nesting);
        pre_age = clamp(_mm_add_ps(pre_age, peace_boom), zero, c_008);

        let fert_raw = clamp(
            _mm_sub_ps(one, _mm_mul_ps(_mm_sub_ps(c_028, ys), two)),
            c_035,
            c_125,
        );
        let fert = select(m_age, fert_raw, one);
        let birth = clamp(_mm_add_ps(pre_age, _mm_mul_ps(pre_age, _mm_sub_ps(fert, one))), zero, c_008);

        let poverty_d = _mm_mul_ps(_mm_sub_ps(one, sol_v), c_0004);
        let shortage_d = _mm_mul_ps(scarcity, short_d_w);
        let war_d = _mm_mul_ps(dmg, c_0012);
        let famine_d = select(_mm_cmplt_ps(fd, c_040), _mm_mul_ps(_mm_sub_ps(c_040, fd), c_0045), zero);
        let winter_d = select(
            _mm_and_ps(_mm_cmpgt_ps(abs(lat_v), c_35), _mm_cmplt_ps(en, c_045)),
            _mm_mul_ps(_mm_sub_ps(c_045, en), c_0015),
            zero,
        );
        let epidemic_d = select(
            _mm_and_ps(
                _mm_cmplt_ps(md, c_035),
                _mm_or_ps(_mm_cmpgt_ps(dmg, c_010), _mm_cmpgt_ps(scarcity, c_015)),
            ),
            _mm_mul_ps(_mm_sub_ps(c_035, md), c_0020d),
            zero,
        );
        let violence_d = select(
            _mm_and_ps(_mm_cmpgt_ps(rad, c_065), _mm_cmplt_ps(s_stab, c_025)),
            _mm_mul_ps(_mm_mul_ps(_mm_sub_ps(rad, c_065), _mm_sub_ps(c_025, s_stab)), c_0035d),
            zero,
        );

        let mut pre_health = _mm_add_ps(d_base, poverty_d);
        pre_health = _mm_add_ps(pre_health, shortage_d);
        pre_health = _mm_add_ps(pre_health, war_d);
        pre_health = _mm_add_ps(pre_health, famine_d);
        pre_health = _mm_add_ps(pre_health, winter_d);
        pre_health = _mm_add_ps(pre_health, epidemic_d);
        pre_health = clamp(_mm_add_ps(pre_health, violence_d), zero, c_008);

        let hosp_relief = _mm_mul_ps(_mm_mul_ps(pre_health, health), c_025);
        let post_health = _mm_sub_ps(pre_health, hosp_relief);
        let excess = max0(_mm_sub_ps(os, ref_old_v));
        let mort = select(
            m_age,
            _mm_add_ps(one, _mm_mul_ps(_mm_mul_ps(excess, mow_v), three)),
            one,
        );
        let death = clamp(_mm_add_ps(post_health, _mm_mul_ps(post_health, _mm_sub_ps(mort, one))), zero, c_008);

        let old_burden = clamp01(_mm_add_ps(
            _mm_mul_ps(excess, c_45),
            select(_mm_cmpgt_ps(death, birth), _mm_mul_ps(_mm_sub_ps(death, birth), c_35), zero),
        ));

        _mm_storeu_ps(out_birth.as_mut_ptr().add(i), birth);
        _mm_storeu_ps(out_death.as_mut_ptr().add(i), death);
        _mm_storeu_ps(out_old_burden.as_mut_ptr().add(i), old_burden);
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn vital_rates_neon(
    pop: &[i64],
    war_damage: &[f32],
    hospitals: &[i32],
    literacy: &[f32],
    urbanization: &[f32],
    sol: &[f32],
    radicalism: &[f32],
    young_share: &[f32],
    old_share: &[f32],
    welfare_spending: &[f32],
    inflation: &[f32],
    war_support: &[f32],
    nation_stability: &[f32],
    state_stability: &[f32],
    capital_lat: &[f32],
    food: &[f32],
    energy: &[f32],
    medicine: &[f32],
    flags: &[u8],
    scarcity_tolerance: f32,
    birth_base: f32,
    low_lit_birth: f32,
    death_base: f32,
    shortage_death: f32,
    ref_old: f32,
    mort_old_weight: f32,
    out_birth: &mut [f32],
    out_death: &mut [f32],
    out_old_burden: &mut [f32],
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::{clamp, clamp01, m_max, m_min, max0};

    let n = pop.len();
    let zero = vdupq_n_f32(0.0f32);
    let one = vdupq_n_f32(1.0f32);
    let two = vdupq_n_f32(2.0f32);
    let three = vdupq_n_f32(3.0f32);
    let c_001 = vdupq_n_f32(0.01f32);
    let c_002 = vdupq_n_f32(0.02f32);
    let c_004 = vdupq_n_f32(0.04f32);
    let c_006 = vdupq_n_f32(0.06f32);
    let c_008 = vdupq_n_f32(0.08f32);
    let c_010 = vdupq_n_f32(0.10f32);
    let c_015 = vdupq_n_f32(0.15f32);
    let c_025 = vdupq_n_f32(0.25f32);
    let c_028 = vdupq_n_f32(0.28f32);
    let c_030 = vdupq_n_f32(0.30f32);
    let c_035 = vdupq_n_f32(0.35f32);
    let c_040 = vdupq_n_f32(0.40f32);
    let c_045 = vdupq_n_f32(0.45f32);
    let c_055 = vdupq_n_f32(0.55f32);
    let c_060 = vdupq_n_f32(0.60f32);
    let c_065 = vdupq_n_f32(0.65f32);
    let c_072 = vdupq_n_f32(0.72f32);
    let c_082 = vdupq_n_f32(0.82f32);
    let c_125 = vdupq_n_f32(1.25f32);
    let c_1e6 = vdupq_n_f32(1e6_f32);
    let c_01 = vdupq_n_f32(0.1f32);
    let c_35 = vdupq_n_f32(35.0f32);
    let c_45 = vdupq_n_f32(4.5f32);

    let sc_tol = vdupq_n_f32(scarcity_tolerance);
    let b_base = vdupq_n_f32(birth_base);
    let low_lit_w = vdupq_n_f32(low_lit_birth);
    let d_base = vdupq_n_f32(death_base);
    let short_d_w = vdupq_n_f32(shortage_death);
    let ref_old_v = vdupq_n_f32(ref_old);
    let mow_v = vdupq_n_f32(mort_old_weight);

    let c_0010 = vdupq_n_f32(0.010f32);
    let c_0032 = vdupq_n_f32(0.032f32);
    let c_0042 = vdupq_n_f32(0.042f32);
    let c_0035_pos = vdupq_n_f32(0.0035f32);
    let c_0020_neg = vdupq_n_f32(-0.0020f32);
    let c_0020_pos = vdupq_n_f32(0.0020f32);
    let c_0065 = vdupq_n_f32(0.0065f32);
    let c_0085 = vdupq_n_f32(0.0085f32);
    let c_0014 = vdupq_n_f32(0.014f32);
    let c_00075 = vdupq_n_f32(0.0075f32);
    let c_0020 = vdupq_n_f32(0.02f32);
    let c_00060 = vdupq_n_f32(0.006f32);
    let c_0012 = vdupq_n_f32(0.012f32);
    let c_0008 = vdupq_n_f32(0.008f32);
    let c_0004 = vdupq_n_f32(0.004f32);
    let c_0045 = vdupq_n_f32(0.045f32);
    let c_0015 = vdupq_n_f32(0.015f32);
    let c_0020d = vdupq_n_f32(0.020f32);
    let c_0035d = vdupq_n_f32(0.035f32);

    let b0 = vdupq_n_u32(VITAL_FLAG_AT_WAR as u32);
    let b1 = vdupq_n_u32(VITAL_FLAG_TRADITION_BONUS as u32);
    let b2 = vdupq_n_u32(VITAL_FLAG_SECULAR_PENALTY as u32);
    let b3 = vdupq_n_u32(VITAL_FLAG_PRONATALIST as u32);
    let b4 = vdupq_n_u32(VITAL_FLAG_DEMO_BOOM as u32);
    let b5 = vdupq_n_u32(VITAL_FLAG_AGE_ENABLED as u32);

    let mut i = 0usize;
    while i + 4 <= n {
        let dmg = clamp01(vld1q_f32(war_damage.as_ptr().add(i)));
        let fd = vld1q_f32(food.as_ptr().add(i));
        let en = vld1q_f32(energy.as_ptr().add(i));
        let md = vld1q_f32(medicine.as_ptr().add(i));
        let coverage = vaddq_f32(
            vaddq_f32(vmulq_f32(fd, c_060), vmulq_f32(en, c_025)),
            vmulq_f32(md, c_015),
        );
        let scarcity = max0(vsubq_f32(vsubq_f32(one, coverage), sc_tol));

        let pop_buf = [pop[i] as f32, pop[i + 1] as f32, pop[i + 2] as f32, pop[i + 3] as f32];
        let pop_f = vld1q_f32(pop_buf.as_ptr());
        let denom = m_max(c_01, vdivq_f32(pop_f, c_1e6));
        let hosp_f = vcvtq_f32_s32(vld1q_s32(hospitals.as_ptr().add(i)));
        let capacity = vdivq_f32(hosp_f, denom);
        let health = vmulq_f32(vdivq_f32(capacity, vaddq_f32(one, capacity)), md);

        let lit = vld1q_f32(literacy.as_ptr().add(i));
        let urb = vld1q_f32(urbanization.as_ptr().add(i));
        let wel = vld1q_f32(welfare_spending.as_ptr().add(i));
        let inf = vld1q_f32(inflation.as_ptr().add(i));
        let ws = vld1q_f32(war_support.as_ptr().add(i));
        let n_stab = vld1q_f32(nation_stability.as_ptr().add(i));
        let s_stab = vld1q_f32(state_stability.as_ptr().add(i));
        let rad = vld1q_f32(radicalism.as_ptr().add(i));
        let ys = vld1q_f32(young_share.as_ptr().add(i));
        let os = vld1q_f32(old_share.as_ptr().add(i));
        let sol_v = vld1q_f32(sol.as_ptr().add(i));
        let lat_v = vld1q_f32(capital_lat.as_ptr().add(i));

        let f_buf = [flags[i] as u32, flags[i + 1] as u32, flags[i + 2] as u32, flags[i + 3] as u32];
        let f_u32 = vld1q_u32(f_buf.as_ptr());
        let m_war = vceqq_u32(vandq_u32(f_u32, b0), b0);
        let m_trad = vceqq_u32(vandq_u32(f_u32, b1), b1);
        let m_sec = vceqq_u32(vandq_u32(f_u32, b2), b2);
        let m_pro = vceqq_u32(vandq_u32(f_u32, b3), b3);
        let m_boom = vceqq_u32(vandq_u32(f_u32, b4), b4);
        let m_age = vceqq_u32(vandq_u32(f_u32, b5), b5);

        let low_lit_b = vmulq_f32(vsubq_f32(one, lit), low_lit_w);
        let urban_squeeze = vbslq_f32(
            vcgtq_f32(urb, c_055),
            vmulq_f32(vnegq_f32(vsubq_f32(urb, c_055)), c_0010),
            zero,
        );
        let m_post = vandq_u32(vcgtq_f32(lit, c_082), vcgtq_f32(urb, c_060));
        let post_industrial = vbslq_f32(
            m_post,
            vmulq_f32(vmulq_f32(vnegq_f32(vsubq_f32(lit, c_082)), vsubq_f32(urb, c_060)), c_0032),
            zero,
        );
        let m_urb01 = vcgtq_f32(urb, c_001);
        let family_welfare = vbslq_f32(
            vandq_u32(m_urb01, vcgtq_f32(wel, c_006)),
            vmulq_f32(vsubq_f32(wel, c_006), c_0042),
            zero,
        );
        let tradition_bonus = vbslq_f32(vandq_u32(m_urb01, m_trad), c_0035_pos, zero);
        let secular_penalty = vbslq_f32(vandq_u32(m_urb01, m_sec), c_0020_neg, zero);
        let pronatalist = vbslq_f32(m_pro, c_0065, vbslq_f32(m_boom, c_0085, zero));
        let inf_clamp = clamp(vsubq_f32(inf, c_008), zero, c_030);
        let inflation_drag = vbslq_f32(
            vandq_u32(m_urb01, vcgtq_f32(inf, c_008)),
            vmulq_f32(vnegq_f32(inf_clamp), c_0014),
            zero,
        );

        let exhaustion = clamp01(vsubq_f32(one, ws));
        let mob_base = vbslq_f32(
            vcgtq_f32(exhaustion, c_008),
            vnegq_f32(m_min(c_00075, vmulq_f32(vsubq_f32(exhaustion, c_008), c_0020))),
            zero,
        );
        let mob_shock = vbslq_f32(m_war, vsubq_f32(mob_base, c_0020_pos), mob_base);
        let war_nesting = vbslq_f32(
            vcgtq_f32(dmg, c_004),
            vnegq_f32(m_min(c_00060, vmulq_f32(vsubq_f32(dmg, c_004), c_0012))),
            zero,
        );
        let m_peace = vandq_u32(vmvnq_u32(m_war), vandq_u32(vcgtq_f32(n_stab, c_072), vcltq_f32(dmg, c_002)));
        let peace_boom = vbslq_f32(m_peace, vmulq_f32(vsubq_f32(n_stab, c_072), c_0008), zero);

        let mut pre_age = vaddq_f32(b_base, low_lit_b);
        pre_age = vaddq_f32(pre_age, urban_squeeze);
        pre_age = vaddq_f32(pre_age, post_industrial);
        pre_age = vaddq_f32(pre_age, family_welfare);
        pre_age = vaddq_f32(pre_age, tradition_bonus);
        pre_age = vaddq_f32(pre_age, secular_penalty);
        pre_age = vaddq_f32(pre_age, pronatalist);
        pre_age = vaddq_f32(pre_age, inflation_drag);
        pre_age = vaddq_f32(pre_age, mob_shock);
        pre_age = vaddq_f32(pre_age, war_nesting);
        pre_age = clamp(vaddq_f32(pre_age, peace_boom), zero, c_008);

        let fert_raw = clamp(vsubq_f32(one, vmulq_f32(vsubq_f32(c_028, ys), two)), c_035, c_125);
        let fert = vbslq_f32(m_age, fert_raw, one);
        let birth = clamp(vaddq_f32(pre_age, vmulq_f32(pre_age, vsubq_f32(fert, one))), zero, c_008);

        let poverty_d = vmulq_f32(vsubq_f32(one, sol_v), c_0004);
        let shortage_d = vmulq_f32(scarcity, short_d_w);
        let war_d = vmulq_f32(dmg, c_0012);
        let famine_d = vbslq_f32(vcltq_f32(fd, c_040), vmulq_f32(vsubq_f32(c_040, fd), c_0045), zero);
        let winter_d = vbslq_f32(
            vandq_u32(vcgtq_f32(vabsq_f32(lat_v), c_35), vcltq_f32(en, c_045)),
            vmulq_f32(vsubq_f32(c_045, en), c_0015),
            zero,
        );
        let epidemic_d = vbslq_f32(
            vandq_u32(vcltq_f32(md, c_035), vorrq_u32(vcgtq_f32(dmg, c_010), vcgtq_f32(scarcity, c_015))),
            vmulq_f32(vsubq_f32(c_035, md), c_0020d),
            zero,
        );
        let violence_d = vbslq_f32(
            vandq_u32(vcgtq_f32(rad, c_065), vcltq_f32(s_stab, c_025)),
            vmulq_f32(vmulq_f32(vsubq_f32(rad, c_065), vsubq_f32(c_025, s_stab)), c_0035d),
            zero,
        );

        let mut pre_health = vaddq_f32(d_base, poverty_d);
        pre_health = vaddq_f32(pre_health, shortage_d);
        pre_health = vaddq_f32(pre_health, war_d);
        pre_health = vaddq_f32(pre_health, famine_d);
        pre_health = vaddq_f32(pre_health, winter_d);
        pre_health = vaddq_f32(pre_health, epidemic_d);
        pre_health = clamp(vaddq_f32(pre_health, violence_d), zero, c_008);

        let hosp_relief = vmulq_f32(vmulq_f32(pre_health, health), c_025);
        let post_health = vsubq_f32(pre_health, hosp_relief);
        let excess = max0(vsubq_f32(os, ref_old_v));
        let mort = vbslq_f32(m_age, vaddq_f32(one, vmulq_f32(vmulq_f32(excess, mow_v), three)), one);
        let death = clamp(vaddq_f32(post_health, vmulq_f32(post_health, vsubq_f32(mort, one))), zero, c_008);

        let old_burden = clamp01(vaddq_f32(
            vmulq_f32(excess, c_45),
            vbslq_f32(vcgtq_f32(death, birth), vmulq_f32(vsubq_f32(death, birth), c_35), zero),
        ));

        vst1q_f32(out_birth.as_mut_ptr().add(i), birth);
        vst1q_f32(out_death.as_mut_ptr().add(i), death);
        vst1q_f32(out_old_burden.as_mut_ptr().add(i), old_burden);
        i += 4;
    }
    i
}

#[cfg(test)]
mod tests {
    use super::*;

    fn assert_f32_bit_exact(a: &[f32], b: &[f32], label: &str) {
        assert_eq!(a.len(), b.len(), "{label} length");
        for i in 0..a.len() {
            assert_eq!(
                a[i].to_bits(),
                b[i].to_bits(),
                "{label}[{i}]: simd={} (0x{:08x}) != scalar={} (0x{:08x})",
                a[i],
                a[i].to_bits(),
                b[i],
                b[i].to_bits()
            );
        }
    }

    #[test]
    fn step_cohorts_simd_matches_scalar_bit_exact() {
        for &n in &[0usize, 1, 3, 4, 7, 8, 9, 15, 16, 17, 65] {
            let pop: Vec<f32> = (0..n).map(|i| if i == 0 { 0.0 } else { 50_000.0 + (i as f32) * 123_456.7 }).collect();
            let births: Vec<i64> = (0..n).map(|i| ((i as i64) * 313) + 12).collect();
            let deaths: Vec<i64> = (0..n).map(|i| ((i as i64) * 197) + (if i % 7 == 0 { 99_999 } else { 8 })).collect();
            let ys: Vec<f32> = (0..n).map(|i| 0.08 + ((i % 15) as f32) * 0.035).collect();
            let os: Vec<f32> = (0..n).map(|i| 0.03 + ((i % 13) as f32) * 0.025).collect();

            let (mut sy, mut so, mut sw) = (vec![0f32; n], vec![0f32; n], vec![0f32; n]);
            let (mut ny, mut no, mut nw) = (vec![0f32; n], vec![0f32; n], vec![0f32; n]);

            step_cohorts_scalar(0, n, &pop, &births, &deaths, &ys, &os, 0.055, 0.018, 1.4, 7.0 / 365.0, &mut sy, &mut so, &mut sw);
            step_cohorts_batch(&pop, &births, &deaths, &ys, &os, 0.055, 0.018, 1.4, 7.0 / 365.0, &mut ny, &mut no, &mut nw).unwrap();

            assert_f32_bit_exact(&ny, &sy, "step_cohorts.young");
            assert_f32_bit_exact(&no, &so, "step_cohorts.old");
            assert_f32_bit_exact(&nw, &sw, "step_cohorts.working");
        }
    }

    #[test]
    fn aggregate_nations_handles_empty_negative_and_bounds() {
        let offsets = [0u32, 0, 2, 4];
        let pop = [1_000_000i64, -50, 3_333_333_333i64, 500_000];
        let ys = [0.28f32, 0.40, 0.21, 0.35];
        let os = [0.11f32, 0.05, 0.17, 0.09];
        let births = [120i64, 5, 800, 90];
        let deaths = [80i64, 9, 400, 60];
        let mig = [15i64, -4, -300, 12];
        let mut oy = [0f32; 3];
        let mut oo = [0f32; 3];
        let mut ow = [0f32; 3];
        let mut ox = [0f32; 3];
        let mut ob = [0i64; 3];
        let mut od = [0i64; 3];
        let mut om = [0i64; 3];

        aggregate_nations(
            3, &offsets, &pop, &ys, &os, &births, &deaths, &mig, 4,
            0.30, 0.09, 0.61, 0.10,
            &mut oy, &mut oo, &mut ow, &mut ox, &mut ob, &mut od, &mut om,
        ).unwrap();
        assert_eq!(oy[0].to_bits(), 0.30f32.to_bits());
        assert_eq!(oo[0].to_bits(), 0.09f32.to_bits());
        assert_eq!(ow[0].to_bits(), 0.61f32.to_bits());
        assert_eq!((ob[0], od[0], om[0]), (0, 0, 0));
        assert_eq!(oy[1].to_bits(), 0.28f32.to_bits());
        assert_eq!(ob[1], 125);
        assert_eq!(od[1], 89);
        assert_eq!(om[1], 11);
        assert!(ox[2] > 0.05);

        let bad_non_mono = [0u32, 3, 2, 4];
        assert_eq!(
            aggregate_nations(
                3, &bad_non_mono, &pop, &ys, &os, &births, &deaths, &mig, 4,
                0.30, 0.09, 0.61, 0.10,
                &mut oy, &mut oo, &mut ow, &mut ox, &mut ob, &mut od, &mut om,
            ),
            Err(-2)
        );
    }

    #[test]
    fn household_groups_simd_matches_scalar_and_handles_zero_labour() {
        for &n in &[0usize, 1, 3, 4, 8, 17, 67] {
            for &need_emp in &[false, true] {
                let sizes: Vec<i64> = (0..n).map(|i| 10_000 + (i as i64) * 7_531).collect();
                let is_upper: Vec<u8> = (0..n).map(|i| if i % 5 == 0 { 1 } else { 0 }).collect();
                let anchor: Vec<f32> = (0..n).map(|i| 0.3 + ((i % 4) as f32) * 0.18).collect();
                let mut e_s: Vec<f32> = (0..n).map(|i| 0.72 + ((i % 5) as f32) * 0.05).collect();
                let mut w_s: Vec<f32> = (0..n).map(|i| 0.40 + ((i % 6) as f32) * 0.07).collect();
                let mut s_s: Vec<f32> = (0..n).map(|i| 0.45 + ((i % 7) as f32) * 0.06).collect();
                let mut r_s: Vec<f32> = (0..n).map(|i| 0.05 + ((i % 4) as f32) * 0.10).collect();

                let (mut e_n, mut w_n, mut s_n, mut r_n) = (e_s.clone(), w_s.clone(), s_s.clone(), r_s.clone());
                let res_n = household_groups(
                    n, &sizes, &is_upper, &anchor, &mut e_n, &mut w_n, &mut s_n, &mut r_n,
                    0.62, need_emp, 1.8, -1.0, 0.04, 7.0 / 365.0, 0.12, 0.88, 0.76, 0.64,
                    0.05, 0.08, 0.025, 0.56, 0.14,
                ).unwrap();

                // Independent scalar calculation of pass 1 + pass 2.
                let mut sl = 0.0f64;
                let mut se = 0.0f64;
                for i in 0..n {
                    let cw = if is_upper[i] != 0 { 0.15 } else { 1.0 };
                    let a = (((sizes[i] as f32) * 0.62) as f64) * 1e-6 * cw;
                    sl += a;
                    if need_emp {
                        se += a * (e_s[i] as f64);
                    }
                }
                let lab = sl as f32;
                let mut nb = -1.0f32;
                if need_emp {
                    let emp = if lab <= 0.0 { 0.0 } else { (se as f32) / lab };
                    nb = max0_f32((lab * emp) - 1.8);
                }
                nb = max0_f32(nb * (1.0 + 0.04 * (7.0 / 365.0)));
                let et = if lab <= 0.0 { 0.0 } else { clamp01_f32(((nb + 1.8) * (1.0 - 0.12 * 0.5)) / lab) };
                let bn = ((0.88f32 * 0.6) + (0.76 * 0.25)) + (0.64 * 0.15);
                let sc = max0_f32((1.0 - bn) - 0.05);
                let un = max0_f32(0.9 - et);
                let st = clamp01_f32(((sc * 0.65) + (un * 0.25)) + (0.12 * 0.25));
                household_groups_pass2_scalar(
                    0, n, &anchor, &mut e_s, &mut w_s, &mut s_s, &mut r_s,
                    et, 0.08, 0.025, sc, 0.12, 0.56, st, 0.14 + st * 0.2,
                );
                let mut ls = 0.0f64;
                for i in 0..n {
                    ls += (sizes[i] as f64) * (s_s[i] as f64);
                }
                assert_eq!(res_n.0.to_bits(), nb.to_bits());
                assert_eq!(res_n.1.to_bits(), et.to_bits());
                assert_eq!(res_n.2.to_bits(), bn.to_bits());
                assert_eq!(res_n.3.to_bits(), st.to_bits());
                assert_eq!(res_n.4.to_bits(), ls.to_bits());
                assert_f32_bit_exact(&e_n, &e_s, "hh.emp");
                assert_f32_bit_exact(&w_n, &w_s, "hh.wealth");
                assert_f32_bit_exact(&s_n, &s_s, "hh.sol");
                assert_f32_bit_exact(&r_n, &r_s, "hh.rad");
            }
        }
    }

    #[test]
    fn vital_rates_simd_matches_scalar_bit_exact_and_rounds_ties_even() {
        for &n in &[0usize, 1, 3, 4, 7, 8, 9, 15, 16, 17, 65] {
            let pop: Vec<i64> = (0..n).map(|i| 250_000 + (i as i64) * 333_333).collect();
            let wd: Vec<f32> = (0..n).map(|i| if i % 4 == 0 { 0.28 } else { (i % 6) as f32 * 0.01 }).collect();
            let hosp: Vec<i32> = (0..n).map(|i| (i % 7) as i32).collect();
            let lit: Vec<f32> = (0..n).map(|i| 0.30 + (i % 7) as f32 * 0.10).collect();
            let urb: Vec<f32> = (0..n).map(|i| 0.005 + (i % 8) as f32 * 0.11).collect();
            let sol: Vec<f32> = (0..n).map(|i| 0.25 + (i % 6) as f32 * 0.12).collect();
            let rad: Vec<f32> = (0..n).map(|i| if i % 5 == 0 { 0.80 } else { 0.15 }).collect();
            let ys: Vec<f32> = (0..n).map(|i| 0.14 + (i % 9) as f32 * 0.03).collect();
            let os: Vec<f32> = (0..n).map(|i| 0.06 + (i % 7) as f32 * 0.035).collect();
            let wel: Vec<f32> = (0..n).map(|i| 0.02 + (i % 5) as f32 * 0.06).collect();
            let inf: Vec<f32> = (0..n).map(|i| 0.03 + (i % 6) as f32 * 0.07).collect();
            let ws: Vec<f32> = (0..n).map(|i| 0.45 + (i % 5) as f32 * 0.12).collect();
            let ns: Vec<f32> = (0..n).map(|i| 0.40 + (i % 5) as f32 * 0.14).collect();
            let ss: Vec<f32> = (0..n).map(|i| if i % 5 == 0 { 0.15 } else { 0.70 }).collect();
            let lat: Vec<f32> = (0..n).map(|i| if i % 2 == 0 { -52.3 } else { 21.5 }).collect();
            let fd: Vec<f32> = (0..n).map(|i| if i % 9 == 0 { 0.25 } else { 0.90 }).collect();
            let en: Vec<f32> = (0..n).map(|i| if i % 6 == 0 { 0.30 } else { 0.85 }).collect();
            let md: Vec<f32> = (0..n).map(|i| if i % 4 == 0 { 0.20 } else { 0.80 }).collect();
            let flg: Vec<u8> = (0..n).map(|i| (i as u8) & 63).collect();

            let (mut sb, mut sd, mut sob) = (vec![0f32; n], vec![0f32; n], vec![0f32; n]);
            let (mut nb, mut nd, mut nob) = (vec![0f32; n], vec![0f32; n], vec![0f32; n]);
            let (mut nrb, mut nrd) = (vec![0i64; n], vec![0i64; n]);

            vital_rates_scalar_rates(
                0, n, &pop, &wd, &hosp, &lit, &urb, &sol, &rad, &ys, &os,
                &wel, &inf, &ws, &ns, &ss, &lat, &fd, &en, &md, &flg,
                0.05, 0.010, 0.018, 0.006, 0.005, 0.10, 1.4,
                &mut sb, &mut sd, &mut sob,
            );
            vital_rates_batch(
                &pop, &wd, &hosp, &lit, &urb, &sol, &rad, &ys, &os,
                &wel, &inf, &ws, &ns, &ss, &lat, &fd, &en, &md, &flg,
                0.05, 0.010, 0.018, 0.006, 0.005, 0.10, 1.4, 7.0 / 365.0,
                &mut nb, &mut nd, &mut nob, Some(&mut nrb), Some(&mut nrd),
            ).unwrap();

            assert_f32_bit_exact(&nb, &sb, "vital.birth");
            assert_f32_bit_exact(&nd, &sd, "vital.death");
            assert_f32_bit_exact(&nob, &sob, "vital.old_burden");
        }
        // Verify IEEE-754 ties-to-even rounding directly: 0.5 -> 0, 1.5 -> 2, 2.5 -> 2.
        assert_eq!(round_ties_even_i64(2, 0.5, 0.5), 0);
        assert_eq!(round_ties_even_i64(6, 0.5, 0.5), 2);
        assert_eq!(round_ties_even_i64(10, 0.5, 0.5), 2);
    }
}
