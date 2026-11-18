//! The weekly household review of Deal or Force (HouseholdEconomy.Review, the part that walks
//! the population groups), written once more in Rust behind a C ABI, for one question only:
//! does a native kernel beat the same data in C#? Tools-only; the game does not load this.
//!
//! Bit-identity with the C# game code is the entry ticket, so every expression below keeps
//! the C# order of operations and its exact clamp/max semantics:
//!   * no fused multiply-add: Rust never contracts a*b+c without fast-math, and none is used;
//!   * Math.Clamp(v, lo, hi) is "v < lo ? lo : v > hi ? hi : v" - NaN passes through;
//!   * Math.Max(0, v) for float is "+0 if v <= 0, else v" - NaN passes through;
//!   * the two double sums (labour, living) are added strictly in group order, never
//!     reassociated, because a reordered float sum is a different number.

#![allow(clippy::missing_safety_doc)]

use std::slice;

/// Every array the kernel touches, as caller-owned pointers. Layout must match
/// Tools/KernelBench/NativeKernel.cs (KernelArgs) field for field.
#[repr(C)]
pub struct KernelArgs {
    pub state_count: i32,
    pub group_count: i32,
    pub threads: i32,
    pub _pad: i32,
    // per state, read
    pub group_start: *const i32,
    pub group_len: *const i32,
    pub due: *const u8,
    pub share: *const f32,
    pub building_jobs: *const f32,
    pub war_damage: *const f32,
    pub annual: *const f32,
    pub food: *const f32,
    pub energy: *const f32,
    pub medicine: *const f32,
    pub nation_living: *const f32,
    pub state_radicalism: *const f32,
    pub population_f: *const f32,
    pub population_d: *const f64,
    // per state, read and written
    pub non_building_jobs: *mut f32,
    pub state_living: *mut f32,
    pub employment_target: *mut f32,
    pub basic_needs: *mut f32,
    pub stress: *mut f32,
    // per group, read
    pub size_f: *const f32,
    pub size_d: *const f64,
    pub class_factor: *const f64,
    pub anchor: *const f32,
    // per group, read and written
    pub employment: *mut f32,
    pub wealth: *mut f32,
    pub living: *mut f32,
    pub radicalism: *mut f32,
    // constants of the week
    pub interval: f32,
    pub tolerance: f32,
    pub employment_rate: f32,
    pub wealth_rate: f32,
}

#[inline(always)]
fn clamp(v: f32, lo: f32, hi: f32) -> f32 {
    if v < lo {
        lo
    } else if v > hi {
        hi
    } else {
        v
    }
}

#[inline(always)]
fn max0(v: f32) -> f32 {
    if v <= 0.0 {
        0.0
    } else {
        v
    }
}

/// M.Lerp with a weight that is already inside [0, 1] (all four weights are clamped once).
#[inline(always)]
fn lerp(from: f32, to: f32, w: f32) -> f32 {
    from + (to - from) * w
}

#[no_mangle]
pub extern "C" fn dof_household_abi() -> u32 {
    1
}

struct Ptr(*const KernelArgs);
unsafe impl Send for Ptr {}
unsafe impl Sync for Ptr {}

/// One call does the whole week: no per-region crossing of the boundary.
/// Returns 0 on success, 1 on an invalid shape.
#[no_mangle]
pub unsafe extern "C" fn dof_household_review_v1(args: *const KernelArgs) -> i32 {
    if args.is_null() {
        return 1;
    }
    let a = &*args;
    if a.state_count < 0 || a.group_count < 0 {
        return 1;
    }
    let states = a.state_count as usize;
    let threads = a.threads.max(1) as usize;
    if threads == 1 || states < 64 {
        for s in 0..states {
            review_state(a, s);
        }
        return 0;
    }
    // Regions are independent and each owns a contiguous run of groups, so a range of regions
    // per thread writes disjoint memory; the result is the same bytes as the serial loop.
    let per = (states + threads - 1) / threads;
    let shared = Ptr(args);
    std::thread::scope(|scope| {
        for t in 0..threads {
            let from = t * per;
            let to = ((t + 1) * per).min(states);
            if from >= to {
                break;
            }
            let p = &shared;
            scope.spawn(move || {
                let a = unsafe { &*p.0 };
                for s in from..to {
                    unsafe { review_state(a, s) };
                }
            });
        }
    });
    0
}

#[inline(always)]
unsafe fn review_state(a: &KernelArgs, s: usize) {
    if *a.due.add(s) == 0 {
        return;
    }
    let start = *a.group_start.add(s) as usize;
    let len = *a.group_len.add(s) as usize;
    let share = *a.share.add(s);

    let size_f = slice::from_raw_parts(a.size_f.add(start), len);
    let class_factor = slice::from_raw_parts(a.class_factor.add(start), len);
    let size_d = slice::from_raw_parts(a.size_d.add(start), len);
    let anchor = slice::from_raw_parts(a.anchor.add(start), len);
    let emp = slice::from_raw_parts_mut(a.employment.add(start), len);
    let wealth = slice::from_raw_parts_mut(a.wealth.add(start), len);
    let living = slice::from_raw_parts_mut(a.living.add(start), len);
    let rad = slice::from_raw_parts_mut(a.radicalism.add(start), len);

    // HouseholdEconomy.Labour: (float)(Size * share) widened, * 1e-6, * class, summed in order.
    let labour: f32 = if len == 0 {
        *a.population_f.add(s) * share * 1e-6f32
    } else {
        let mut sum = 0.0f64;
        for i in 0..len {
            sum += (size_f[i] * share) as f64 * 1e-6 * class_factor[i];
        }
        sum as f32
    };
    let building_jobs = *a.building_jobs.add(s);
    let mut nbj = *a.non_building_jobs.add(s);
    if nbj < 0.0 {
        // HouseholdEconomy.Employment, before any group has moved.
        let employment = if labour <= 0.0 {
            0.0
        } else {
            let mut sum = 0.0f64;
            for i in 0..len {
                sum += (size_f[i] * share) as f64 * 1e-6 * class_factor[i] * emp[i] as f64;
            }
            sum as f32 / labour
        };
        nbj = max0(labour * employment - building_jobs);
    }
    let damage = clamp(*a.war_damage.add(s), 0.0, 1.0);
    nbj = max0(nbj * (1.0 + clamp(*a.annual.add(s), -0.25, 0.15) * a.interval));
    *a.non_building_jobs.add(s) = nbj;
    let target = if labour <= 0.0 {
        0.0
    } else {
        clamp((nbj + building_jobs) * (1.0 - damage * 0.5) / labour, 0.0, 1.0)
    };
    *a.employment_target.add(s) = target;
    let coverage = *a.food.add(s) * 0.6 + *a.energy.add(s) * 0.25 + *a.medicine.add(s) * 0.15;
    *a.basic_needs.add(s) = coverage;
    let scarcity = max0(1.0 - coverage - a.tolerance);
    let unemployment = max0(0.9 - target);
    let stress = clamp(scarcity * 0.65 + unemployment * 0.25 + damage * 0.25, 0.0, 1.0);
    *a.stress.add(s) = stress;

    let er = a.employment_rate;
    let wr = a.wealth_rate;
    let scarcity18 = scarcity * 0.18;
    let damage12 = damage * 0.12;
    let nation_living = *a.nation_living.add(s);
    let stress25 = stress * 0.25;
    let rad_target = *a.state_radicalism.add(s) + stress * 0.2;

    // The element-wise part: no cross-element dependency, so LLVM may vectorise it.
    for i in 0..len {
        let e = lerp(emp[i], target, er);
        emp[i] = e;
        let tw = clamp(anchor[i] + (e - 0.9) * 0.22 - scarcity18 - damage12, 0.0, 1.0);
        let w = lerp(wealth[i], tw, wr);
        wealth[i] = w;
        let tl = clamp(nation_living + (w - 0.5) * 0.12 - stress25, 0.0, 1.0);
        living[i] = lerp(living[i], tl, 0.04);
        rad[i] = clamp(lerp(rad[i], rad_target, 0.025), 0.0, 1.0);
    }
    if len > 0 {
        let mut sum = 0.0f64;
        for i in 0..len {
            sum += size_d[i] * living[i] as f64;
        }
        let sl = *a.state_living.add(s);
        *a.state_living.add(s) = lerp(sl, (sum / *a.population_d.add(s)) as f32, 0.08);
    }
}
