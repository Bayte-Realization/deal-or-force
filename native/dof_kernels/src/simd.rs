// SPDX-License-Identifier: GPL-3.0-only
//! Cross-platform bit-exact IEEE-754 scalar and SIMD (AVX2 / SSE2 / ARM64 NEON) primitives.
//! Rules for strict C# parity:
//!   - Multiply and add remain separate IEEE operations (never FMA contraction).
//!   - Clamp matches `M.Clamp` (`v < lo ? lo : v > hi ? hi : v`), preserving NaN and -0.0.
//!   - `max0` matches `MathF.Max(0f, v)` (`+0.0` for `-0.0`, NaN passes through).

#[inline(always)]
pub(crate) fn clamp_f32(v: f32, lo: f32, hi: f32) -> f32 {
    if v < lo {
        lo
    } else if v > hi {
        hi
    } else {
        v
    }
}

#[inline(always)]
pub(crate) fn clamp01_f32(v: f32) -> f32 {
    clamp_f32(v, 0.0f32, 1.0f32)
}

#[inline(always)]
pub(crate) fn max0_f32(v: f32) -> f32 {
    if v > 0.0f32 {
        v
    } else if v.is_nan() {
        v
    } else {
        0.0f32
    }
}

#[inline(always)]
pub(crate) fn m_max_f32(a: f32, b: f32) -> f32 {
    if a > b {
        a
    } else {
        b
    }
}

#[inline(always)]
pub(crate) fn m_min_f32(a: f32, b: f32) -> f32 {
    if a < b {
        a
    } else {
        b
    }
}

#[inline(always)]
pub(crate) fn lerp_clamped_f32(from: f32, to: f32, weight_clamped: f32) -> f32 {
    from + (to - from) * weight_clamped
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[allow(dead_code)]
pub(crate) mod x86_avx2 {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    #[target_feature(enable = "avx2")]
    pub(crate) unsafe fn clamp(v: __m256, lo: __m256, hi: __m256) -> __m256 {
        let lt = _mm256_cmp_ps(v, lo, _CMP_LT_OQ);
        let gt = _mm256_cmp_ps(v, hi, _CMP_GT_OQ);
        let hi_clamped = _mm256_blendv_ps(v, hi, gt);
        _mm256_blendv_ps(hi_clamped, lo, lt)
    }

    #[target_feature(enable = "avx2")]
    pub(crate) unsafe fn clamp01(v: __m256) -> __m256 {
        clamp(v, _mm256_setzero_ps(), _mm256_set1_ps(1.0f32))
    }

    #[target_feature(enable = "avx2")]
    pub(crate) unsafe fn max0(v: __m256) -> __m256 {
        let zero = _mm256_setzero_ps();
        let gt = _mm256_cmp_ps(v, zero, _CMP_GT_OQ);
        let nan = _mm256_cmp_ps(v, v, _CMP_UNORD_Q);
        let sel = _mm256_blendv_ps(zero, v, gt);
        _mm256_blendv_ps(sel, v, nan)
    }

    #[target_feature(enable = "avx2")]
    pub(crate) unsafe fn abs(v: __m256) -> __m256 {
        _mm256_andnot_ps(_mm256_set1_ps(-0.0f32), v)
    }

    #[target_feature(enable = "avx2")]
    pub(crate) unsafe fn m_max(a: __m256, b: __m256) -> __m256 {
        let gt = _mm256_cmp_ps(a, b, _CMP_GT_OQ);
        _mm256_blendv_ps(b, a, gt)
    }

    #[target_feature(enable = "avx2")]
    pub(crate) unsafe fn m_min(a: __m256, b: __m256) -> __m256 {
        let lt = _mm256_cmp_ps(a, b, _CMP_LT_OQ);
        _mm256_blendv_ps(b, a, lt)
    }

    #[inline(always)]
    pub(crate) unsafe fn select(mask: __m256, if_true: __m256, if_false: __m256) -> __m256 {
        _mm256_blendv_ps(if_false, if_true, mask)
    }
}

#[cfg(any(target_arch = "x86", target_arch = "x86_64"))]
#[allow(dead_code)]
pub(crate) mod x86_sse2 {
    #[cfg(target_arch = "x86")]
    use std::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use std::arch::x86_64::*;

    #[inline(always)]
    pub(crate) unsafe fn select(mask: __m128, if_true: __m128, if_false: __m128) -> __m128 {
        _mm_or_ps(_mm_and_ps(mask, if_true), _mm_andnot_ps(mask, if_false))
    }

    #[target_feature(enable = "sse2")]
    pub(crate) unsafe fn clamp(v: __m128, lo: __m128, hi: __m128) -> __m128 {
        let lt = _mm_cmplt_ps(v, lo);
        let gt = _mm_cmpgt_ps(v, hi);
        let hi_clamped = select(gt, hi, v);
        select(lt, lo, hi_clamped)
    }

    #[target_feature(enable = "sse2")]
    pub(crate) unsafe fn clamp01(v: __m128) -> __m128 {
        clamp(v, _mm_setzero_ps(), _mm_set1_ps(1.0f32))
    }

    #[target_feature(enable = "sse2")]
    pub(crate) unsafe fn max0(v: __m128) -> __m128 {
        let zero = _mm_setzero_ps();
        let gt = _mm_cmpgt_ps(v, zero);
        let nan = _mm_cmpunord_ps(v, v);
        let sel = select(gt, v, zero);
        select(nan, v, sel)
    }

    #[target_feature(enable = "sse2")]
    pub(crate) unsafe fn abs(v: __m128) -> __m128 {
        _mm_andnot_ps(_mm_set1_ps(-0.0f32), v)
    }

    #[target_feature(enable = "sse2")]
    pub(crate) unsafe fn m_max(a: __m128, b: __m128) -> __m128 {
        select(_mm_cmpgt_ps(a, b), a, b)
    }

    #[target_feature(enable = "sse2")]
    pub(crate) unsafe fn m_min(a: __m128, b: __m128) -> __m128 {
        select(_mm_cmplt_ps(a, b), a, b)
    }
}

#[cfg(target_arch = "aarch64")]
#[allow(dead_code)]
pub(crate) mod arm64_neon {
    use std::arch::aarch64::*;

    #[target_feature(enable = "neon")]
    pub(crate) unsafe fn clamp(v: float32x4_t, lo: float32x4_t, hi: float32x4_t) -> float32x4_t {
        let lt = vcltq_f32(v, lo);
        let gt = vcgtq_f32(v, hi);
        let hi_clamped = vbslq_f32(gt, hi, v);
        vbslq_f32(lt, lo, hi_clamped)
    }

    #[target_feature(enable = "neon")]
    pub(crate) unsafe fn clamp01(v: float32x4_t) -> float32x4_t {
        clamp(v, vdupq_n_f32(0.0f32), vdupq_n_f32(1.0f32))
    }

    #[target_feature(enable = "neon")]
    pub(crate) unsafe fn max0(v: float32x4_t) -> float32x4_t {
        let zero = vdupq_n_f32(0.0f32);
        let gt = vcgtq_f32(v, zero);
        let ord = vceqq_f32(v, v);
        let nan = vmvnq_u32(ord);
        let sel = vbslq_f32(gt, v, zero);
        vbslq_f32(nan, v, sel)
    }

    #[target_feature(enable = "neon")]
    pub(crate) unsafe fn m_max(a: float32x4_t, b: float32x4_t) -> float32x4_t {
        vbslq_f32(vcgtq_f32(a, b), a, b)
    }

    #[target_feature(enable = "neon")]
    pub(crate) unsafe fn abs(v: float32x4_t) -> float32x4_t {
        vabsq_f32(v)
    }

    #[target_feature(enable = "neon")]
    pub(crate) unsafe fn m_min(a: float32x4_t, b: float32x4_t) -> float32x4_t {
        vbslq_f32(vcltq_f32(a, b), a, b)
    }
}
