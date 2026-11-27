// SPDX-License-Identifier: GPL-3.0-only
#[inline(always)]
pub(crate) fn great_circle_km(lat_a: f32, lon_a: f32, lat_b: f32, lon_b: f32) -> f32 {
    const RADIANS: f32 = std::f32::consts::PI / 180.0;
    let phi_a = lat_a * RADIANS;
    let phi_b = lat_b * RADIANS;
    let delta_phi = (lat_b - lat_a) * RADIANS;
    let delta_lambda = (lon_b - lon_a) * RADIANS;
    let sin_dp = (delta_phi * 0.5f32).sin();
    let sin_dl = (delta_lambda * 0.5f32).sin();
    let haversine = sin_dp * sin_dp + phi_a.cos() * phi_b.cos() * sin_dl * sin_dl;
    let clamped = haversine.max(0.0f32).sqrt().min(1.0f32);
    6371.0f32 * 2.0f32 * clamped.asin()
}

pub fn haversine_batch(
    origin_lat: f32,
    origin_lon: f32,
    target_lats: &[f32],
    target_lons: &[f32],
    out_km: &mut [f32],
) -> Result<(), i32> {
    let n = target_lats.len();
    if target_lons.len() < n {
        return Err(-2);
    }
    if out_km.len() < n {
        return Err(-3);
    }
    for i in 0..n {
        out_km[i] = great_circle_km(origin_lat, origin_lon, target_lats[i], target_lons[i]);
    }
    Ok(())
}

/// Fast SIMD latitude-band prefilter (`abs(origin_lat - target_lat) * 111.0 <= reach_km`)
/// followed by exact great-circle distance evaluation on surviving candidates.
pub fn within_range_batch(
    origin_lat: f32,
    origin_lon: f32,
    target_lats: &[f32],
    target_lons: &[f32],
    reach_km: f32,
    out_indices: &mut [i32],
    out_km: &mut [f32],
) -> Result<usize, i32> {
    let n = target_lats.len();
    if target_lons.len() < n {
        return Err(-2);
    }
    if !reach_km.is_finite() || reach_km < 0.0f32 {
        return Err(-2);
    }
    if out_indices.len() != out_km.len() {
        return Err(-3);
    }
    let cap = out_indices.len();

    #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
    let (i, mut written) = if std::arch::is_x86_feature_detected!("avx2") {
        unsafe {
            within_range_avx2(
                origin_lat,
                origin_lon,
                target_lats,
                target_lons,
                reach_km,
                out_indices,
                out_km,
                cap,
            )
        }?
    } else {
        unsafe {
            within_range_sse2(
                origin_lat,
                origin_lon,
                target_lats,
                target_lons,
                reach_km,
                out_indices,
                out_km,
                cap,
            )
        }?
    };

    #[cfg(target_arch = "aarch64")]
    let (i, mut written) = if std::arch::is_aarch64_feature_detected!("neon") {
        unsafe {
            within_range_neon(
                origin_lat,
                origin_lon,
                target_lats,
                target_lons,
                reach_km,
                out_indices,
                out_km,
                cap,
            )
        }?
    } else {
        (0usize, 0usize)
    };

    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
    let (i, mut written) = (0usize, 0usize);

    for k in i..n {
        let tlat = target_lats[k];
        if (origin_lat - tlat).abs() * 111.0f32 > reach_km {
            continue;
        }
        let km = great_circle_km(origin_lat, origin_lon, tlat, target_lons[k]);
        if km <= reach_km {
            if written >= cap {
                return Err(-3);
            }
            out_indices[written] = k as i32;
            out_km[written] = km;
            written += 1;
        }
    }

    Ok(written)
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn within_range_avx2(
    origin_lat: f32,
    origin_lon: f32,
    target_lats: &[f32],
    target_lons: &[f32],
    reach_km: f32,
    out_indices: &mut [i32],
    out_km: &mut [f32],
    cap: usize,
) -> Result<(usize, usize), i32> {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_avx2::abs;

    let n = target_lats.len();
    let olat = _mm256_set1_ps(origin_lat);
    let km_per_deg = _mm256_set1_ps(111.0f32);
    let reach = _mm256_set1_ps(reach_km);
    let mut written = 0usize;
    let mut i = 0usize;

    while i + 8 <= n {
        let tlat = _mm256_loadu_ps(target_lats.as_ptr().add(i));
        let lat_km = _mm256_mul_ps(abs(_mm256_sub_ps(olat, tlat)), km_per_deg);
        let in_band = _mm256_cmp_ps(lat_km, reach, _CMP_LE_OQ);
        let mut mask = _mm256_movemask_ps(in_band) as u32;
        while mask != 0 {
            let lane = mask.trailing_zeros() as usize;
            let idx = i + lane;
            let km = great_circle_km(origin_lat, origin_lon, target_lats[idx], target_lons[idx]);
            if km <= reach_km {
                if written >= cap {
                    return Err(-3);
                }
                out_indices[written] = idx as i32;
                out_km[written] = km;
                written += 1;
            }
            mask &= mask - 1;
        }
        i += 8;
    }

    Ok((i, written))
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn within_range_sse2(
    origin_lat: f32,
    origin_lon: f32,
    target_lats: &[f32],
    target_lons: &[f32],
    reach_km: f32,
    out_indices: &mut [i32],
    out_km: &mut [f32],
    cap: usize,
) -> Result<(usize, usize), i32> {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_sse2::abs;

    let n = target_lats.len();
    let olat = _mm_set1_ps(origin_lat);
    let km_per_deg = _mm_set1_ps(111.0f32);
    let reach = _mm_set1_ps(reach_km);
    let mut written = 0usize;
    let mut i = 0usize;

    while i + 4 <= n {
        let tlat = _mm_loadu_ps(target_lats.as_ptr().add(i));
        let lat_km = _mm_mul_ps(abs(_mm_sub_ps(olat, tlat)), km_per_deg);
        let in_band = _mm_cmple_ps(lat_km, reach);
        let mut mask = _mm_movemask_ps(in_band) as u32;
        while mask != 0 {
            let lane = mask.trailing_zeros() as usize;
            let idx = i + lane;
            let km = great_circle_km(origin_lat, origin_lon, target_lats[idx], target_lons[idx]);
            if km <= reach_km {
                if written >= cap {
                    return Err(-3);
                }
                out_indices[written] = idx as i32;
                out_km[written] = km;
                written += 1;
            }
            mask &= mask - 1;
        }
        i += 4;
    }

    Ok((i, written))
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn within_range_neon(
    origin_lat: f32,
    origin_lon: f32,
    target_lats: &[f32],
    target_lons: &[f32],
    reach_km: f32,
    out_indices: &mut [i32],
    out_km: &mut [f32],
    cap: usize,
) -> Result<(usize, usize), i32> {
    use std::arch::aarch64::*;

    let n = target_lats.len();
    let olat = vdupq_n_f32(origin_lat);
    let km_per_deg = vdupq_n_f32(111.0f32);
    let reach = vdupq_n_f32(reach_km);
    let weights_arr = [1u32, 2u32, 4u32, 8u32];
    let weights = vld1q_u32(weights_arr.as_ptr());
    let mut written = 0usize;
    let mut i = 0usize;

    while i + 4 <= n {
        let tlat = vld1q_f32(target_lats.as_ptr().add(i));
        let lat_km = vmulq_f32(vabsq_f32(vsubq_f32(olat, tlat)), km_per_deg);
        let in_band = vcleq_f32(lat_km, reach);
        let mut mask = vaddvq_u32(vandq_u32(in_band, weights));
        while mask != 0 {
            let lane = mask.trailing_zeros() as usize;
            let idx = i + lane;
            let km = great_circle_km(origin_lat, origin_lon, target_lats[idx], target_lons[idx]);
            if km <= reach_km {
                if written >= cap {
                    return Err(-3);
                }
                out_indices[written] = idx as i32;
                out_km[written] = km;
                written += 1;
            }
            mask &= mask - 1;
        }
        i += 4;
    }

    Ok((i, written))
}

pub fn pairwise_matrix(
    lats: &[f32],
    lons: &[f32],
    stride: usize,
    out_km: &mut [f32],
) -> Result<(), i32> {
    let n = lats.len();
    if lons.len() < n || stride < n {
        return Err(-2);
    }
    if n == 0 {
        return Ok(());
    }
    let needed = n.checked_mul(stride).ok_or(-2)?;
    if out_km.len() < needed {
        return Err(-3);
    }
    for i in 0..n {
        let la = lats[i];
        let lo = lons[i];
        out_km[i * stride + i] = 0.0f32;
        for j in (i + 1)..n {
            let km = great_circle_km(la, lo, lats[j], lons[j]);
            out_km[i * stride + j] = km;
            out_km[j * stride + i] = km;
        }
    }
    Ok(())
}
