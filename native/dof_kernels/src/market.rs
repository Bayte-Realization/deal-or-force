// SPDX-License-Identifier: GPL-3.0-only
use crate::simd::{clamp01_f32, clamp_f32, lerp_clamped_f32, max0_f32};

/// Bit-exact batched `MarketSystem.SettleSingleGoodPrice` across `good_count` goods x `nation_count` nations.
/// Data layout: `[good * nation_stride + nation_idx]`.
#[allow(clippy::too_many_arguments)]
pub fn settle_prices_batch(
    good_count: usize,
    nation_count: usize,
    nation_stride: usize,
    base_price: &[f32],
    traded_world_price: &[u8],
    demand: &[f32],
    production: &[f32],
    imports: &[f32],
    relieved: &[f32],
    adjust_price: bool,
    import_markup_def: f32,
    relief_def: f32,
    price_adjust_speed: f32,
    satisfaction: &mut [f32],
    price: &mut [f32],
    out_world_scarcity: &mut [f32],
) -> Result<(), i32> {
    if good_count == 0 || nation_count == 0 {
        return Ok(());
    }
    if nation_stride < nation_count
        || base_price.len() < good_count
        || traded_world_price.len() < good_count
    {
        return Err(-2);
    }
    let total_len = good_count.checked_mul(nation_stride).ok_or(-2)?;
    if demand.len() < total_len
        || production.len() < total_len
        || imports.len() < total_len
        || relieved.len() < total_len
    {
        return Err(-2);
    }
    if satisfaction.len() < total_len
        || price.len() < total_len
        || (!out_world_scarcity.is_empty() && out_world_scarcity.len() < good_count)
    {
        return Err(-3);
    }

    let adjust_speed = clamp01_f32(price_adjust_speed);

    for g in 0..good_count {
        let row = g * nation_stride;
        let d_row = &demand[row..row + nation_count];
        let p_row = &production[row..row + nation_count];
        let imp_row = &imports[row..row + nation_count];
        let rel_row = &relieved[row..row + nation_count];
        let sat_row = &mut satisfaction[row..row + nation_count];
        let pr_row = &mut price[row..row + nation_count];

        // Pass 1: strict sequential summation preserving float addition order bit-for-bit.
        let mut total_demand = 0.0f32;
        let mut total_production = 0.0f32;
        let mut wanted_imports = 0.0f32;
        let mut spare_exports = 0.0f32;
        for i in 0..nation_count {
            let d = d_row[i];
            let p = p_row[i];
            total_demand += d;
            total_production += p;
            if d > p {
                wanted_imports += d - p;
            } else {
                spare_exports += p - d;
            }
        }
        total_demand = max0_f32(total_demand);
        if total_demand < 0.01f32 {
            total_demand = 0.01f32;
        }
        total_production = max0_f32(total_production);
        if total_production < 0.01f32 {
            total_production = 0.01f32;
        }

        let world_scarcity = if traded_world_price[g] != 0 {
            let wi = if wanted_imports > 0.01f32 {
                wanted_imports
            } else if wanted_imports.is_nan() {
                wanted_imports
            } else {
                0.01f32
            };
            let se = if spare_exports > 0.01f32 {
                spare_exports
            } else if spare_exports.is_nan() {
                spare_exports
            } else {
                0.01f32
            };
            clamp_f32(wi / se, 0.55f32, 2.75f32)
        } else {
            clamp_f32(total_demand / total_production, 0.55f32, 2.75f32)
        };
        if !out_world_scarcity.is_empty() {
            out_world_scarcity[g] = world_scarcity;
        }

        let bp = base_price[g];

        #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
        let done = if std::arch::is_x86_feature_detected!("avx2") {
            unsafe {
                settle_prices_row_avx2(
                    d_row,
                    p_row,
                    imp_row,
                    rel_row,
                    sat_row,
                    pr_row,
                    bp,
                    world_scarcity,
                    adjust_price,
                    import_markup_def,
                    relief_def,
                    adjust_speed,
                )
            }
        } else {
            unsafe {
                settle_prices_row_sse2(
                    d_row,
                    p_row,
                    imp_row,
                    rel_row,
                    sat_row,
                    pr_row,
                    bp,
                    world_scarcity,
                    adjust_price,
                    import_markup_def,
                    relief_def,
                    adjust_speed,
                )
            }
        };

        #[cfg(target_arch = "aarch64")]
        let done = if std::arch::is_aarch64_feature_detected!("neon") {
            unsafe {
                settle_prices_row_neon(
                    d_row,
                    p_row,
                    imp_row,
                    rel_row,
                    sat_row,
                    pr_row,
                    bp,
                    world_scarcity,
                    adjust_price,
                    import_markup_def,
                    relief_def,
                    adjust_speed,
                )
            }
        } else {
            0usize
        };

        #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
        let done = 0usize;

        for i in done..nation_count {
            let d = d_row[i];
            let p = p_row[i];
            let imp = imp_row[i];
            let rel = rel_row[i];
            let sat = if d <= 0.001f32 {
                1.0f32
            } else {
                clamp01_f32((p + imp) / d)
            };
            sat_row[i] = sat;

            let local_scarcity = 1.0f32 + (1.0f32 - sat) * 1.40f32;
            let import_markup = if imp > 0.001f32 {
                1.0f32
                    + (import_markup_def - 1.0f32)
                        * (1.0f32 - clamp01_f32(relief_def * clamp01_f32(rel / imp)))
            } else {
                1.0f32
            };
            let target_price = bp * world_scarcity * local_scarcity * import_markup;
            pr_row[i] = if adjust_price {
                let cur = pr_row[i];
                let from = if cur <= 0.0f32 { bp } else { cur };
                lerp_clamped_f32(from, target_price, adjust_speed)
            } else {
                target_price
            };
        }
    }

    Ok(())
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn settle_prices_row_avx2(
    demand: &[f32],
    production: &[f32],
    imports: &[f32],
    relieved: &[f32],
    satisfaction: &mut [f32],
    price: &mut [f32],
    base_price: f32,
    world_scarcity: f32,
    adjust_price: bool,
    import_markup_def: f32,
    relief_def: f32,
    adjust_speed: f32,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_avx2::clamp01;

    let n = demand.len();
    let zero = _mm256_setzero_ps();
    let one = _mm256_set1_ps(1.0f32);
    let c_001 = _mm256_set1_ps(0.001f32);
    let c_140 = _mm256_set1_ps(1.40f32);
    let markup_delta = _mm256_set1_ps(import_markup_def - 1.0f32);
    let relief_v = _mm256_set1_ps(relief_def);
    let bp_v = _mm256_set1_ps(base_price);
    let bp_ws = _mm256_set1_ps(base_price * world_scarcity);
    let speed_v = _mm256_set1_ps(adjust_speed);

    let mut i = 0usize;
    while i + 8 <= n {
        let d = _mm256_loadu_ps(demand.as_ptr().add(i));
        let p = _mm256_loadu_ps(production.as_ptr().add(i));
        let imp = _mm256_loadu_ps(imports.as_ptr().add(i));
        let rel = _mm256_loadu_ps(relieved.as_ptr().add(i));

        let d_le = _mm256_cmp_ps(d, c_001, _CMP_LE_OQ);
        let sat_raw = clamp01(_mm256_div_ps(_mm256_add_ps(p, imp), d));
        let sat = _mm256_blendv_ps(sat_raw, one, d_le);
        _mm256_storeu_ps(satisfaction.as_mut_ptr().add(i), sat);

        let local_scarcity = _mm256_add_ps(one, _mm256_mul_ps(_mm256_sub_ps(one, sat), c_140));
        let imp_gt = _mm256_cmp_ps(imp, c_001, _CMP_GT_OQ);
        let cut = clamp01(_mm256_mul_ps(relief_v, clamp01(_mm256_div_ps(rel, imp))));
        let mk_val = _mm256_add_ps(one, _mm256_mul_ps(markup_delta, _mm256_sub_ps(one, cut)));
        let mk = _mm256_blendv_ps(one, mk_val, imp_gt);

        let target = _mm256_mul_ps(_mm256_mul_ps(bp_ws, local_scarcity), mk);
        let out_p = if adjust_price {
            let cur = _mm256_loadu_ps(price.as_ptr().add(i));
            let cur_le = _mm256_cmp_ps(cur, zero, _CMP_LE_OQ);
            let from = _mm256_blendv_ps(cur, bp_v, cur_le);
            _mm256_add_ps(from, _mm256_mul_ps(_mm256_sub_ps(target, from), speed_v))
        } else {
            target
        };
        _mm256_storeu_ps(price.as_mut_ptr().add(i), out_p);
        i += 8;
    }
    i
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn settle_prices_row_sse2(
    demand: &[f32],
    production: &[f32],
    imports: &[f32],
    relieved: &[f32],
    satisfaction: &mut [f32],
    price: &mut [f32],
    base_price: f32,
    world_scarcity: f32,
    adjust_price: bool,
    import_markup_def: f32,
    relief_def: f32,
    adjust_speed: f32,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    use crate::simd::x86_sse2::{clamp01, select};

    let n = demand.len();
    let zero = _mm_setzero_ps();
    let one = _mm_set1_ps(1.0f32);
    let c_001 = _mm_set1_ps(0.001f32);
    let c_140 = _mm_set1_ps(1.40f32);
    let markup_delta = _mm_set1_ps(import_markup_def - 1.0f32);
    let relief_v = _mm_set1_ps(relief_def);
    let bp_v = _mm_set1_ps(base_price);
    let bp_ws = _mm_set1_ps(base_price * world_scarcity);
    let speed_v = _mm_set1_ps(adjust_speed);

    let mut i = 0usize;
    while i + 4 <= n {
        let d = _mm_loadu_ps(demand.as_ptr().add(i));
        let p = _mm_loadu_ps(production.as_ptr().add(i));
        let imp = _mm_loadu_ps(imports.as_ptr().add(i));
        let rel = _mm_loadu_ps(relieved.as_ptr().add(i));

        let d_le = _mm_cmple_ps(d, c_001);
        let sat_raw = clamp01(_mm_div_ps(_mm_add_ps(p, imp), d));
        let sat = select(d_le, one, sat_raw);
        _mm_storeu_ps(satisfaction.as_mut_ptr().add(i), sat);

        let local_scarcity = _mm_add_ps(one, _mm_mul_ps(_mm_sub_ps(one, sat), c_140));
        let imp_gt = _mm_cmpgt_ps(imp, c_001);
        let cut = clamp01(_mm_mul_ps(relief_v, clamp01(_mm_div_ps(rel, imp))));
        let mk_val = _mm_add_ps(one, _mm_mul_ps(markup_delta, _mm_sub_ps(one, cut)));
        let mk = select(imp_gt, mk_val, one);

        let target = _mm_mul_ps(_mm_mul_ps(bp_ws, local_scarcity), mk);
        let out_p = if adjust_price {
            let cur = _mm_loadu_ps(price.as_ptr().add(i));
            let cur_le = _mm_cmple_ps(cur, zero);
            let from = select(cur_le, bp_v, cur);
            _mm_add_ps(from, _mm_mul_ps(_mm_sub_ps(target, from), speed_v))
        } else {
            target
        };
        _mm_storeu_ps(price.as_mut_ptr().add(i), out_p);
        i += 4;
    }
    i
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn settle_prices_row_neon(
    demand: &[f32],
    production: &[f32],
    imports: &[f32],
    relieved: &[f32],
    satisfaction: &mut [f32],
    price: &mut [f32],
    base_price: f32,
    world_scarcity: f32,
    adjust_price: bool,
    import_markup_def: f32,
    relief_def: f32,
    adjust_speed: f32,
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::clamp01;

    let n = demand.len();
    let zero = vdupq_n_f32(0.0f32);
    let one = vdupq_n_f32(1.0f32);
    let c_001 = vdupq_n_f32(0.001f32);
    let c_140 = vdupq_n_f32(1.40f32);
    let markup_delta = vdupq_n_f32(import_markup_def - 1.0f32);
    let relief_v = vdupq_n_f32(relief_def);
    let bp_v = vdupq_n_f32(base_price);
    let bp_ws = vdupq_n_f32(base_price * world_scarcity);
    let speed_v = vdupq_n_f32(adjust_speed);

    let mut i = 0usize;
    while i + 4 <= n {
        let d = vld1q_f32(demand.as_ptr().add(i));
        let p = vld1q_f32(production.as_ptr().add(i));
        let imp = vld1q_f32(imports.as_ptr().add(i));
        let rel = vld1q_f32(relieved.as_ptr().add(i));

        let d_le = vcleq_f32(d, c_001);
        let sat_raw = clamp01(vdivq_f32(vaddq_f32(p, imp), d));
        let sat = vbslq_f32(d_le, one, sat_raw);
        vst1q_f32(satisfaction.as_mut_ptr().add(i), sat);

        let local_scarcity = vaddq_f32(one, vmulq_f32(vsubq_f32(one, sat), c_140));
        let imp_gt = vcgtq_f32(imp, c_001);
        let cut = clamp01(vmulq_f32(relief_v, clamp01(vdivq_f32(rel, imp))));
        let mk_val = vaddq_f32(one, vmulq_f32(markup_delta, vsubq_f32(one, cut)));
        let mk = vbslq_f32(imp_gt, mk_val, one);

        let target = vmulq_f32(vmulq_f32(bp_ws, local_scarcity), mk);
        let out_p = if adjust_price {
            let cur = vld1q_f32(price.as_ptr().add(i));
            let cur_le = vcleq_f32(cur, zero);
            let from = vbslq_f32(cur_le, bp_v, cur);
            vaddq_f32(from, vmulq_f32(vsubq_f32(target, from), speed_v))
        } else {
            target
        };
        vst1q_f32(price.as_mut_ptr().add(i), out_p);
        i += 4;
    }
    i
}

/// Bit-exact batched `MarketSystem.SettleSingleNation` across `nation_count` nations x `good_count` goods.
/// Data layout: `[nation * good_stride + good]`.
#[allow(clippy::too_many_arguments)]
pub fn clear_nations_batch(
    nation_count: usize,
    good_count: usize,
    good_stride: usize,
    base_price: &[f32],
    essential_weight: &[f32],
    tariff_rate: &[f32],
    production: &[f32],
    demand: &[f32],
    imports: &[f32],
    exports: &[f32],
    relieved: &[f32],
    satisfaction: &[f32],
    price: &[f32],
    market_trade_share: f32,
    gdp_price_share: f32,
    gdp_price_band: f32,
    out_gross_output: &mut [f32],
    out_real_output: &mut [f32],
    out_shortage_index: &mut [f32],
    out_market_access: &mut [f32],
    out_weekly_trade_balance: &mut [f32],
    out_weekly_tariff_income: &mut [f32],
) -> Result<(), i32> {
    if nation_count == 0 || good_count == 0 {
        return Ok(());
    }
    if good_stride < good_count
        || base_price.len() < good_count
        || essential_weight.len() < good_count
        || tariff_rate.len() < nation_count
    {
        return Err(-2);
    }
    let total_len = nation_count.checked_mul(good_stride).ok_or(-2)?;
    if production.len() < total_len
        || demand.len() < total_len
        || imports.len() < total_len
        || exports.len() < total_len
        || relieved.len() < total_len
        || satisfaction.len() < total_len
        || price.len() < total_len
    {
        return Err(-2);
    }
    if out_gross_output.len() < nation_count
        || out_real_output.len() < nation_count
        || out_shortage_index.len() < nation_count
        || out_market_access.len() < nation_count
        || out_weekly_trade_balance.len() < nation_count
        || out_weekly_tariff_income.len() < nation_count
    {
        return Err(-3);
    }

    let price_share = clamp01_f32(gdp_price_share);
    let band = max0_f32(gdp_price_band);
    let band_low = max0_f32(1.0f32 - band);
    let band_high = 1.0f32 + band;

    let mut total_weight = 0.0f32;
    for g in 0..good_count {
        total_weight += essential_weight[g];
    }

    for ni in 0..nation_count {
        let row = ni * good_stride;
        let tariff = tariff_rate[ni];
        let mut shortages = 0.0f32;
        let mut trade = 0.0f32;
        let mut tariffs = 0.0f32;
        let mut gross_output = 0.0f32;
        let mut real = 0.0f32;

        for g in 0..good_count {
            let idx = row + g;
            let prod = production[idx];
            let dem = demand[idx];
            let imp = imports[idx];
            let exp = exports[idx];
            let rel = relieved[idx];
            let sat = satisfaction[idx];
            let pr = price[idx];
            let bp = base_price[g];
            let ew = essential_weight[g];

            shortages += (1.0f32 - sat) * ew;
            trade += (exp - imp) * pr;
            tariffs += max0_f32(imp - rel) * pr * tariff;

            let clamped_p = clamp_f32(pr, bp * band_low, bp * band_high);
            let domestic = max0_f32(dem - imp) + exp;
            let sold = if prod < domestic {
                prod
            } else if prod.is_nan() || domestic.is_nan() {
                f32::NAN
            } else {
                domestic
            };
            gross_output += sold * (bp + price_share * (clamped_p - bp));
            real += prod * bp;
        }

        let shortage_idx = if total_weight <= 0.0f32 {
            0.0f32
        } else {
            clamp01_f32(shortages / total_weight)
        };
        out_gross_output[ni] = gross_output;
        out_real_output[ni] = real;
        out_shortage_index[ni] = shortage_idx;
        out_market_access[ni] = 1.0f32 - shortage_idx;
        out_weekly_trade_balance[ni] = trade * market_trade_share;
        out_weekly_tariff_income[ni] = tariffs * market_trade_share;
    }

    Ok(())
}
