// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Batched native Rust SIMD kernel for <c>PoliticalCohorts.Drift</c> and <c>PoliticalCohorts.DriftBlocs</c>.
/// Falls back cleanly to bit-identical pure C# when unavailable or disabled.
/// </summary>
public static partial class NativePoliticalKernels
{
    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_political_cohorts_drift_v1(
        uint count,
        byte* flags,
        float* step,
        float* blocStep,
        float* shortage,
        float* stability,
        float* nationalLiving,
        float* alienation,
        float* mil,
        float* taxStress,
        float* marketAccess,
        float* literacy,
        float* urbanization,
        float* sol,
        float* radicalism,
        float* lawApproval,
        float* govApproval,
        float* polDivergence,
        float* blocs);

    public static unsafe bool TryDriftCohortsBatch(
        int count,
        ReadOnlySpan<byte> flags,
        ReadOnlySpan<float> step,
        ReadOnlySpan<float> blocStep,
        ReadOnlySpan<float> shortage,
        ReadOnlySpan<float> stability,
        ReadOnlySpan<float> nationalLiving,
        ReadOnlySpan<float> alienation,
        ReadOnlySpan<float> mil,
        ReadOnlySpan<float> taxStress,
        ReadOnlySpan<float> marketAccess,
        ReadOnlySpan<float> literacy,
        ReadOnlySpan<float> urbanization,
        ReadOnlySpan<float> sol,
        ReadOnlySpan<float> radicalism,
        Span<float> lawApproval,
        Span<float> govApproval,
        Span<float> polDivergence,
        Span<float> blocs)
    {
        if (count <= 0) return true;
        if (!NativeKernelsLoader.EnablePoliticalCohortsDefault ||
            !NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapWartimeGraph))
        {
            return false;
        }
        if (flags.Length < count || step.Length < count || blocStep.Length < count ||
            shortage.Length < count || stability.Length < count || nationalLiving.Length < count ||
            alienation.Length < count || mil.Length < count || taxStress.Length < count ||
            marketAccess.Length < count || literacy.Length < count || urbanization.Length < count ||
            sol.Length < count || radicalism.Length < count || lawApproval.Length < count ||
            govApproval.Length < count || polDivergence.Length < count || blocs.Length < count * 5)
        {
            return false;
        }

        fixed (byte* pFlags = flags)
        fixed (float* pStep = step)
        fixed (float* pBlocStep = blocStep)
        fixed (float* pShort = shortage)
        fixed (float* pStab = stability)
        fixed (float* pNat = nationalLiving)
        fixed (float* pAlien = alienation)
        fixed (float* pMil = mil)
        fixed (float* pTax = taxStress)
        fixed (float* pMa = marketAccess)
        fixed (float* pLit = literacy)
        fixed (float* pUrb = urbanization)
        fixed (float* pSol = sol)
        fixed (float* pRad = radicalism)
        fixed (float* pLaw = lawApproval)
        fixed (float* pGov = govApproval)
        fixed (float* pDiv = polDivergence)
        fixed (float* pBlocs = blocs)
        {
            return dof_political_cohorts_drift_v1(
                (uint)count,
                pFlags, pStep, pBlocStep, pShort, pStab, pNat, pAlien,
                pMil, pTax, pMa, pLit, pUrb, pSol, pRad,
                pLaw, pGov, pDiv, pBlocs) == 0;
        }
    }
}
