// SPDX-License-Identifier: GPL-3.0-only

#[inline(always)]
fn clamp01(v: f32) -> f32 {
    if v < 0.0 {
        0.0
    } else if v > 1.0 {
        1.0
    } else {
        v
    }
}

#[inline(always)]
fn move_towards(value: f32, target: f32, amount: f32) -> f32 {
    if value < target {
        target.min(value + amount)
    } else {
        target.max(value - amount)
    }
}

#[inline(always)]
fn normalize_blocs(blocs: &mut [f32; 5]) {
    let mut sum = 0.0f32;
    for b in blocs.iter_mut() {
        let v = if b.is_finite() { (*b).max(0.02f32) } else { 0.20f32 };
        *b = v;
        sum += v;
    }
    if sum <= 0.0f32 {
        *blocs = [0.20f32; 5];
        return;
    }
    let inv = 1.0f32 / sum;
    let mut running = 0.0f32;
    for i in 0..4 {
        blocs[i] *= inv;
        running += blocs[i];
    }
    blocs[4] = (1.0f32 - running).max(0.001f32);
}

/// Bit-exact batched update matching `PoliticalCohorts.Drift` + `PoliticalCohorts.DriftBlocs`.
/// Processes `count` non-fresh states in one flat pass with zero managed overhead.
///
/// `flags[i]` bit layout:
///   bit 0 (1): debt
///   bit 1 (2): losing
///   bit 2 (4): recent_politics
///   bit 3 (8): capital
///   bit 4 (16): at_war
///   bit 5 (32): has_blocs (when set, updates `blocs[i*5 .. i*5+5]`)
#[allow(clippy::too_many_arguments)]
pub fn drift_cohorts_batch(
    count: usize,
    flags: &[u8],
    step: &[f32],
    bloc_step: &[f32],
    shortage: &[f32],
    stability: &[f32],
    national_living: &[f32],
    alienation: &[f32],
    mil: &[f32],
    tax_stress: &[f32],
    market_access: &[f32],
    literacy: &[f32],
    urbanization: &[f32],
    sol: &[f32],
    radicalism: &[f32],
    law_approval: &mut [f32],
    gov_approval: &mut [f32],
    pol_divergence: &mut [f32],
    blocs: &mut [f32],
) -> Result<(), i32> {
    if count == 0 {
        return Ok(());
    }
    if flags.len() < count
        || step.len() < count
        || bloc_step.len() < count
        || shortage.len() < count
        || stability.len() < count
        || national_living.len() < count
        || alienation.len() < count
        || mil.len() < count
        || tax_stress.len() < count
        || market_access.len() < count
        || literacy.len() < count
        || urbanization.len() < count
        || sol.len() < count
        || radicalism.len() < count
        || law_approval.len() < count
        || gov_approval.len() < count
        || pol_divergence.len() < count
        || blocs.len() < count * 5
    {
        return Err(-2);
    }

    for i in 0..count {
        let f = flags[i];
        let debt = (f & 1) != 0;
        let losing = (f & 2) != 0;
        let recent_politics = (f & 4) != 0;
        let capital = (f & 8) != 0;
        let at_war = (f & 16) != 0;
        let has_blocs = (f & 32) != 0;

        let st = step[i];
        let sh = shortage[i];
        let stab = stability[i];
        let nat_sol = national_living[i];
        let alien = alienation[i];
        let s_sol = sol[i];
        let radical = clamp01(radicalism[i]);
        let debt_penalty = if debt { 0.12f32 } else { 0.0f32 };

        let law = clamp01(
            0.40f32 + s_sol * 0.40f32 + stab * 0.15f32
                - sh * 0.40f32
                - debt_penalty * 0.5f32
                - radical * 0.25f32,
        );
        let mut government = 0.42f32 + stab * 0.35f32 + s_sol * 0.18f32
            - sh * 0.35f32
            - debt_penalty
            - radical * 0.22f32
            - alien * 0.14f32;
        if losing {
            government -= 0.10f32;
        }
        if recent_politics {
            government -= 0.08f32;
        }
        government = clamp01(government);

        law_approval[i] = move_towards(clamp01(law_approval[i]), law, st);
        gov_approval[i] = move_towards(clamp01(gov_approval[i]), government, st);

        let rejection = clamp01(
            (1.0f32 - government) * 0.55f32 + (1.0f32 - law) * 0.35f32 + radical * 0.25f32,
        );
        let mut poorer = if nat_sol > s_sol { nat_sol - s_sol } else { 0.0f32 };
        if poorer > 1.0f32 {
            poorer = 1.0f32;
        }
        let mut divergence = clamp01(rejection + poorer * 0.20f32 + alien * 0.16f32);
        if capital {
            divergence *= 0.45f32;
        }
        pol_divergence[i] = move_towards(clamp01(pol_divergence[i]), divergence, st * 0.65f32);

        let b_step = bloc_step[i];
        if has_blocs && b_step > 0.0f32 {
            let lit = clamp01(literacy[i]);
            let urb = clamp01(urbanization[i]);
            let sol_c = clamp01(s_sol);
            let sol_gap = (nat_sol - sol_c).max(0.0f32);
            let m = mil[i];
            let tx = tax_stress[i];
            let ma = market_access[i];

            let t_reform = 0.14f32 + lit * 0.24f32 + urb * 0.14f32 + sol_c * 0.12f32
                - if at_war { 0.08f32 } else { 0.0f32 };
            let t_security = 0.12f32 + m * 0.22f32
                + if at_war { 0.20f32 } else { 0.0f32 }
                + if losing { 0.10f32 } else { 0.0f32 }
                + radical * 0.08f32;
            let t_indust = 0.14f32 + urb * 0.22f32 + ma * 0.14f32
                - tx * 0.10f32
                - if debt { 0.06f32 } else { 0.0f32 };
            let t_trad =
                0.14f32 + (1.0f32 - urb) * 0.22f32 + (1.0f32 - lit) * 0.14f32 + stab * 0.10f32;
            let mut t_reg = 0.06f32
                + alien * 0.34f32
                + sol_gap * 0.45f32
                + radical * 0.20f32
                + sh * 0.18f32
                + if debt { 0.08f32 } else { 0.0f32 }
                + tx * 0.08f32;
            if capital {
                t_reg *= 0.40f32;
            }

            let mut target = [
                t_reform.max(0.03f32),
                t_security.max(0.03f32),
                t_indust.max(0.03f32),
                t_trad.max(0.03f32),
                t_reg.max(0.02f32),
            ];
            let sum = target[0] + target[1] + target[2] + target[3] + target[4];
            let inv = if sum > 0.0f32 { 1.0f32 / sum } else { 0.20f32 };
            for t in &mut target {
                *t *= inv;
            }

            let base = i * 5;
            let mut cur = [
                move_towards(blocs[base], target[0], b_step),
                move_towards(blocs[base + 1], target[1], b_step),
                move_towards(blocs[base + 2], target[2], b_step),
                move_towards(blocs[base + 3], target[3], b_step),
                move_towards(blocs[base + 4], target[4], b_step),
            ];
            normalize_blocs(&mut cur);
            blocs[base..base + 5].copy_from_slice(&cur);
        }
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn drift_cohorts_batch_normalizes_and_drifts_exact() {
        let flags = [1u8 | 2 | 16 | 32, 8 | 32];
        let step = [0.0012f32, 0.0012f32];
        let bstep = [0.0018f32, 0.0018f32];
        let shortage = [0.25f32, 0.02f32];
        let stability = [0.45f32, 0.80f32];
        let nat_sol = [0.60f32, 0.72f32];
        let alien = [0.35f32, 0.05f32];
        let mil = [0.40f32, 0.15f32];
        let tax = [0.30f32, 0.10f32];
        let ma = [0.65f32, 0.90f32];
        let lit = [0.70f32, 0.95f32];
        let urb = [0.55f32, 0.85f32];
        let sol = [0.42f32, 0.78f32];
        let rad = [0.30f32, 0.05f32];
        let mut law = [0.55f32, 0.65f32];
        let mut gov = [0.50f32, 0.70f32];
        let mut div = [0.20f32, 0.06f32];
        let mut blocs = [0.20f32; 10];

        drift_cohorts_batch(
            2,
            &flags,
            &step,
            &bstep,
            &shortage,
            &stability,
            &nat_sol,
            &alien,
            &mil,
            &tax,
            &ma,
            &lit,
            &urb,
            &sol,
            &rad,
            &mut law,
            &mut gov,
            &mut div,
            &mut blocs,
        )
        .unwrap();

        assert!(law[0] < 0.55f32);
        assert!(gov[0] < 0.50f32);
        assert!(div[0] > 0.20f32);
        let sum0: f32 = blocs[0..5].iter().sum();
        let sum1: f32 = blocs[5..10].iter().sum();
        assert!((sum0 - 1.0).abs() < 1e-5);
        assert!((sum1 - 1.0).abs() < 1e-5);
    }
}
