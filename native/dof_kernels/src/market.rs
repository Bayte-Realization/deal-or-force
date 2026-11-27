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

        #[cfg(not(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64")))]
        let mut g = 0usize;
        #[cfg(any(target_arch = "x86", target_arch = "x86_64", target_arch = "aarch64"))]
        let mut g: usize;
        #[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
        {
            if std::arch::is_x86_feature_detected!("avx2") {
                g = unsafe {
                    clear_nations_row_avx2(
                        good_count,
                        base_price,
                        essential_weight,
                        &production[row..row + good_count],
                        &demand[row..row + good_count],
                        &imports[row..row + good_count],
                        &exports[row..row + good_count],
                        &relieved[row..row + good_count],
                        &satisfaction[row..row + good_count],
                        &price[row..row + good_count],
                        tariff,
                        price_share,
                        band_low,
                        band_high,
                        &mut shortages,
                        &mut trade,
                        &mut tariffs,
                        &mut gross_output,
                        &mut real,
                    )
                };
            } else {
                g = unsafe {
                    clear_nations_row_sse2(
                        good_count,
                        base_price,
                        essential_weight,
                        &production[row..row + good_count],
                        &demand[row..row + good_count],
                        &imports[row..row + good_count],
                        &exports[row..row + good_count],
                        &relieved[row..row + good_count],
                        &satisfaction[row..row + good_count],
                        &price[row..row + good_count],
                        tariff,
                        price_share,
                        band_low,
                        band_high,
                        &mut shortages,
                        &mut trade,
                        &mut tariffs,
                        &mut gross_output,
                        &mut real,
                    )
                };
            }
        }
        #[cfg(target_arch = "aarch64")]
        {
            if std::arch::is_aarch64_feature_detected!("neon") {
                g = unsafe {
                    clear_nations_row_neon(
                        good_count,
                        base_price,
                        essential_weight,
                        &production[row..row + good_count],
                        &demand[row..row + good_count],
                        &imports[row..row + good_count],
                        &exports[row..row + good_count],
                        &relieved[row..row + good_count],
                        &satisfaction[row..row + good_count],
                        &price[row..row + good_count],
                        tariff,
                        price_share,
                        band_low,
                        band_high,
                        &mut shortages,
                        &mut trade,
                        &mut tariffs,
                        &mut gross_output,
                        &mut real,
                    )
                };
            } else {
                g = 0;
            }
        }

        while g < good_count {
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
            g += 1;
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

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "avx2")]
#[allow(clippy::too_many_arguments)]
unsafe fn clear_nations_row_avx2(
    good_count: usize,
    base_price: &[f32],
    essential_weight: &[f32],
    production: &[f32],
    demand: &[f32],
    imports: &[f32],
    exports: &[f32],
    relieved: &[f32],
    satisfaction: &[f32],
    price: &[f32],
    tariff: f32,
    price_share: f32,
    band_low: f32,
    band_high: f32,
    shortages: &mut f32,
    trade: &mut f32,
    tariffs: &mut f32,
    gross_output: &mut f32,
    real: &mut f32,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_avx2::{clamp, max0};

    let one = _mm256_set1_ps(1.0f32);
    let tar_v = _mm256_set1_ps(tariff);
    let ps_v = _mm256_set1_ps(price_share);
    let bl_v = _mm256_set1_ps(band_low);
    let bh_v = _mm256_set1_ps(band_high);
    let nan_v = _mm256_set1_ps(f32::NAN);

    let mut b_sh = [0.0f32; 8];
    let mut b_tr = [0.0f32; 8];
    let mut b_tf = [0.0f32; 8];
    let mut b_go = [0.0f32; 8];
    let mut b_rl = [0.0f32; 8];

    let mut g = 0usize;
    while g + 8 <= good_count {
        let prod = _mm256_loadu_ps(production.as_ptr().add(g));
        let dem = _mm256_loadu_ps(demand.as_ptr().add(g));
        let imp = _mm256_loadu_ps(imports.as_ptr().add(g));
        let exp = _mm256_loadu_ps(exports.as_ptr().add(g));
        let rel = _mm256_loadu_ps(relieved.as_ptr().add(g));
        let sat = _mm256_loadu_ps(satisfaction.as_ptr().add(g));
        let pr = _mm256_loadu_ps(price.as_ptr().add(g));
        let bp = _mm256_loadu_ps(base_price.as_ptr().add(g));
        let ew = _mm256_loadu_ps(essential_weight.as_ptr().add(g));

        let sh_v = _mm256_mul_ps(_mm256_sub_ps(one, sat), ew);
        let tr_v = _mm256_mul_ps(_mm256_sub_ps(exp, imp), pr);
        let tf_v = _mm256_mul_ps(_mm256_mul_ps(max0(_mm256_sub_ps(imp, rel)), pr), tar_v);

        let clamped_p = clamp(pr, _mm256_mul_ps(bp, bl_v), _mm256_mul_ps(bp, bh_v));
        let domestic = _mm256_add_ps(max0(_mm256_sub_ps(dem, imp)), exp);
        let lt = _mm256_cmp_ps(prod, domestic, _CMP_LT_OQ);
        let nan_any = _mm256_or_ps(
            _mm256_cmp_ps(prod, prod, _CMP_UNORD_Q),
            _mm256_cmp_ps(domestic, domestic, _CMP_UNORD_Q),
        );
        let sold = _mm256_blendv_ps(_mm256_blendv_ps(domestic, prod, lt), nan_v, nan_any);
        let adj_bp = _mm256_add_ps(bp, _mm256_mul_ps(ps_v, _mm256_sub_ps(clamped_p, bp)));
        let go_v = _mm256_mul_ps(sold, adj_bp);
        let rl_v = _mm256_mul_ps(prod, bp);

        _mm256_storeu_ps(b_sh.as_mut_ptr(), sh_v);
        _mm256_storeu_ps(b_tr.as_mut_ptr(), tr_v);
        _mm256_storeu_ps(b_tf.as_mut_ptr(), tf_v);
        _mm256_storeu_ps(b_go.as_mut_ptr(), go_v);
        _mm256_storeu_ps(b_rl.as_mut_ptr(), rl_v);

        for k in 0..8 {
            *shortages += b_sh[k];
            *trade += b_tr[k];
            *tariffs += b_tf[k];
            *gross_output += b_go[k];
            *real += b_rl[k];
        }
        g += 8;
    }
    g
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[target_feature(enable = "sse2")]
#[allow(clippy::too_many_arguments)]
unsafe fn clear_nations_row_sse2(
    good_count: usize,
    base_price: &[f32],
    essential_weight: &[f32],
    production: &[f32],
    demand: &[f32],
    imports: &[f32],
    exports: &[f32],
    relieved: &[f32],
    satisfaction: &[f32],
    price: &[f32],
    tariff: f32,
    price_share: f32,
    band_low: f32,
    band_high: f32,
    shortages: &mut f32,
    trade: &mut f32,
    tariffs: &mut f32,
    gross_output: &mut f32,
    real: &mut f32,
) -> usize {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;
    use crate::simd::x86_sse2::{clamp, max0, select};

    let one = _mm_set1_ps(1.0f32);
    let tar_v = _mm_set1_ps(tariff);
    let ps_v = _mm_set1_ps(price_share);
    let bl_v = _mm_set1_ps(band_low);
    let bh_v = _mm_set1_ps(band_high);
    let nan_v = _mm_set1_ps(f32::NAN);

    let mut b_sh = [0.0f32; 4];
    let mut b_tr = [0.0f32; 4];
    let mut b_tf = [0.0f32; 4];
    let mut b_go = [0.0f32; 4];
    let mut b_rl = [0.0f32; 4];

    let mut g = 0usize;
    while g + 4 <= good_count {
        let prod = _mm_loadu_ps(production.as_ptr().add(g));
        let dem = _mm_loadu_ps(demand.as_ptr().add(g));
        let imp = _mm_loadu_ps(imports.as_ptr().add(g));
        let exp = _mm_loadu_ps(exports.as_ptr().add(g));
        let rel = _mm_loadu_ps(relieved.as_ptr().add(g));
        let sat = _mm_loadu_ps(satisfaction.as_ptr().add(g));
        let pr = _mm_loadu_ps(price.as_ptr().add(g));
        let bp = _mm_loadu_ps(base_price.as_ptr().add(g));
        let ew = _mm_loadu_ps(essential_weight.as_ptr().add(g));

        let sh_v = _mm_mul_ps(_mm_sub_ps(one, sat), ew);
        let tr_v = _mm_mul_ps(_mm_sub_ps(exp, imp), pr);
        let tf_v = _mm_mul_ps(_mm_mul_ps(max0(_mm_sub_ps(imp, rel)), pr), tar_v);

        let clamped_p = clamp(pr, _mm_mul_ps(bp, bl_v), _mm_mul_ps(bp, bh_v));
        let domestic = _mm_add_ps(max0(_mm_sub_ps(dem, imp)), exp);
        let lt = _mm_cmplt_ps(prod, domestic);
        let nan_any = _mm_or_ps(_mm_cmpunord_ps(prod, prod), _mm_cmpunord_ps(domestic, domestic));
        let sold = select(nan_any, nan_v, select(lt, prod, domestic));
        let adj_bp = _mm_add_ps(bp, _mm_mul_ps(ps_v, _mm_sub_ps(clamped_p, bp)));
        let go_v = _mm_mul_ps(sold, adj_bp);
        let rl_v = _mm_mul_ps(prod, bp);

        _mm_storeu_ps(b_sh.as_mut_ptr(), sh_v);
        _mm_storeu_ps(b_tr.as_mut_ptr(), tr_v);
        _mm_storeu_ps(b_tf.as_mut_ptr(), tf_v);
        _mm_storeu_ps(b_go.as_mut_ptr(), go_v);
        _mm_storeu_ps(b_rl.as_mut_ptr(), rl_v);

        for k in 0..4 {
            *shortages += b_sh[k];
            *trade += b_tr[k];
            *tariffs += b_tf[k];
            *gross_output += b_go[k];
            *real += b_rl[k];
        }
        g += 4;
    }
    g
}

#[cfg(target_arch = "aarch64")]
#[target_feature(enable = "neon")]
#[allow(clippy::too_many_arguments)]
unsafe fn clear_nations_row_neon(
    good_count: usize,
    base_price: &[f32],
    essential_weight: &[f32],
    production: &[f32],
    demand: &[f32],
    imports: &[f32],
    exports: &[f32],
    relieved: &[f32],
    satisfaction: &[f32],
    price: &[f32],
    tariff: f32,
    price_share: f32,
    band_low: f32,
    band_high: f32,
    shortages: &mut f32,
    trade: &mut f32,
    tariffs: &mut f32,
    gross_output: &mut f32,
    real: &mut f32,
) -> usize {
    use std::arch::aarch64::*;
    use crate::simd::arm64_neon::{clamp, max0};

    let one = vdupq_n_f32(1.0f32);
    let tar_v = vdupq_n_f32(tariff);
    let ps_v = vdupq_n_f32(price_share);
    let bl_v = vdupq_n_f32(band_low);
    let bh_v = vdupq_n_f32(band_high);
    let nan_v = vdupq_n_f32(f32::NAN);

    let mut b_sh = [0.0f32; 4];
    let mut b_tr = [0.0f32; 4];
    let mut b_tf = [0.0f32; 4];
    let mut b_go = [0.0f32; 4];
    let mut b_rl = [0.0f32; 4];

    let mut g = 0usize;
    while g + 4 <= good_count {
        let prod = vld1q_f32(production.as_ptr().add(g));
        let dem = vld1q_f32(demand.as_ptr().add(g));
        let imp = vld1q_f32(imports.as_ptr().add(g));
        let exp = vld1q_f32(exports.as_ptr().add(g));
        let rel = vld1q_f32(relieved.as_ptr().add(g));
        let sat = vld1q_f32(satisfaction.as_ptr().add(g));
        let pr = vld1q_f32(price.as_ptr().add(g));
        let bp = vld1q_f32(base_price.as_ptr().add(g));
        let ew = vld1q_f32(essential_weight.as_ptr().add(g));

        let sh_v = vmulq_f32(vsubq_f32(one, sat), ew);
        let tr_v = vmulq_f32(vsubq_f32(exp, imp), pr);
        let tf_v = vmulq_f32(vmulq_f32(max0(vsubq_f32(imp, rel)), pr), tar_v);

        let clamped_p = clamp(pr, vmulq_f32(bp, bl_v), vmulq_f32(bp, bh_v));
        let domestic = vaddq_f32(max0(vsubq_f32(dem, imp)), exp);
        let lt = vcltq_f32(prod, domestic);
        let nan_any = vorrq_u32(
            vmvnq_u32(vceqq_f32(prod, prod)),
            vmvnq_u32(vceqq_f32(domestic, domestic)),
        );
        let sold = vbslq_f32(nan_any, nan_v, vbslq_f32(lt, prod, domestic));
        let adj_bp = vaddq_f32(bp, vmulq_f32(ps_v, vsubq_f32(clamped_p, bp)));
        let go_v = vmulq_f32(sold, adj_bp);
        let rl_v = vmulq_f32(prod, bp);

        vst1q_f32(b_sh.as_mut_ptr(), sh_v);
        vst1q_f32(b_tr.as_mut_ptr(), tr_v);
        vst1q_f32(b_tf.as_mut_ptr(), tf_v);
        vst1q_f32(b_go.as_mut_ptr(), go_v);
        vst1q_f32(b_rl.as_mut_ptr(), rl_v);

        for k in 0..4 {
            *shortages += b_sh[k];
            *trade += b_tr[k];
            *tariffs += b_tf[k];
            *gross_output += b_go[k];
            *real += b_rl[k];
        }
        g += 4;
    }
    g
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clear_nations_simd_matches_scalar_bit_exact() {
        let nc = 3;
        let gc = 23;
        let bp: Vec<f32> = (0..gc).map(|g| 5.0 + (g as f32) * 1.25).collect();
        let ew: Vec<f32> = (0..gc).map(|g| 0.2 + (g as f32) * 0.1).collect();
        let tar = [0.05f32, 0.15f32, 0.25f32];
        let total = nc * gc;
        let prod: Vec<f32> = (0..total).map(|i| ((i * 7) % 19) as f32 * 3.1).collect();
        let dem: Vec<f32> = (0..total).map(|i| ((i * 5) % 23) as f32 * 2.8).collect();
        let imp: Vec<f32> = (0..total).map(|i| ((i * 3) % 11) as f32 * 1.4).collect();
        let exp: Vec<f32> = (0..total).map(|i| ((i * 11) % 13) as f32 * 0.9).collect();
        let rel: Vec<f32> = (0..total).map(|i| ((i * 2) % 7) as f32 * 0.7).collect();
        let sat: Vec<f32> = (0..total).map(|i| ((i % 10) as f32) * 0.1).collect();
        let pr: Vec<f32> = (0..total).map(|i| 4.0 + (i % 17) as f32 * 0.9).collect();

        let (mut go, mut rl, mut sh, mut ma, mut tb, mut ti) =
            (vec![0.0; nc], vec![0.0; nc], vec![0.0; nc], vec![0.0; nc], vec![0.0; nc], vec![0.0; nc]);
        clear_nations_batch(
            nc, gc, gc, &bp, &ew, &tar, &prod, &dem, &imp, &exp, &rel, &sat, &pr,
            0.35, 0.65, 0.4, &mut go, &mut rl, &mut sh, &mut ma, &mut tb, &mut ti,
        )
        .unwrap();

        let total_w: f32 = ew.iter().sum();
        for ni in 0..nc {
            let mut s_sh = 0.0f32;
            let mut s_tr = 0.0f32;
            let mut s_tf = 0.0f32;
            let mut s_go = 0.0f32;
            let mut s_rl = 0.0f32;
            for g in 0..gc {
                let idx = ni * gc + g;
                s_sh += (1.0f32 - sat[idx]) * ew[g];
                s_tr += (exp[idx] - imp[idx]) * pr[idx];
                s_tf += max0_f32(imp[idx] - rel[idx]) * pr[idx] * tar[ni];
                let cp = clamp_f32(pr[idx], bp[g] * 0.6f32, bp[g] * 1.4f32);
                let dom = max0_f32(dem[idx] - imp[idx]) + exp[idx];
                let sold = if prod[idx] < dom { prod[idx] } else { dom };
                s_go += sold * (bp[g] + 0.65f32 * (cp - bp[g]));
                s_rl += prod[idx] * bp[g];
            }
            let si = clamp01_f32(s_sh / total_w);
            assert_eq!(go[ni].to_bits(), s_go.to_bits());
            assert_eq!(rl[ni].to_bits(), s_rl.to_bits());
            assert_eq!(sh[ni].to_bits(), si.to_bits());
            assert_eq!(ma[ni].to_bits(), (1.0f32 - si).to_bits());
            assert_eq!(tb[ni].to_bits(), (s_tr * 0.35f32).to_bits());
            assert_eq!(ti[ni].to_bits(), (s_tf * 0.35f32).to_bits());
        }
    }
}
