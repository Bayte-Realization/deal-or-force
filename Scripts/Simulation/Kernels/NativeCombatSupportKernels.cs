#nullable enable
using System;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Managed wrappers and bit-exact pure-C# fallbacks for <c>combat_support.rs</c>
/// (<c>CapEconCombat</c>).
/// </summary>
public static class NativeCombatSupportKernels
{
    private const string LibraryName = NativeKernelsLoader.LibraryName;

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int dof_combat_line_profile_v1(
        uint count,
        float* divisions,
        float* hardness,
        float* armour,
        float* piercing,
        float* supply,
        float* recon,
        float* outHardness,
        float* outArmour,
        float* outPiercing,
        float* outSupply,
        float* outRecon);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int dof_combat_side_totals_v1(
        uint count,
        float* rawPower,
        float* offence,
        float* atkTrain,
        float* atkStance,
        float* atkRes,
        float* munitions,
        float* enc,
        float* spirit,
        float* cmdAtk,
        float* defStat,
        float* defTrain,
        float* defStance,
        float* defRes,
        float* cmdDef,
        float* entrenchment,
        byte weightOffenceTelemetry,
        double* outTotals);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int dof_support_attrition_batch_v1(
        uint count,
        float* threat,
        float* modFactor,
        float attritionPerDay,
        float saturation,
        float floor,
        float* strength,
        float* experience,
        float* outLoss);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int dof_support_reach_batch_v1(
        uint count,
        float targetLat,
        float targetLon,
        float* baseLat,
        float* baseLon,
        float* reachKm,
        float* strength,
        float* experience,
        float* outWeight);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe int dof_econ_state_week_post_v1(
        uint count,
        float* nationSol,
        float* weeklyRadDelta,
        float* cycleRad,
        float* stateSol,
        float* stateRad);

    public static bool CombatLineProfile(
        ReadOnlySpan<float> divisions,
        ReadOnlySpan<float> hardness,
        ReadOnlySpan<float> armour,
        ReadOnlySpan<float> piercing,
        ReadOnlySpan<float> supply,
        ReadOnlySpan<float> recon,
        out float outHardness,
        out float outArmour,
        out float outPiercing,
        out float outSupply,
        out float outRecon)
    {
        if (NativeKernelsLoader.EnableEconCombatDefault &&
            TryNativeCombatLineProfile(divisions, hardness, armour, piercing, supply, recon,
                out outHardness, out outArmour, out outPiercing, out outSupply, out outRecon))
        {
            return true;
        }
        ManagedCombatLineProfile(divisions, hardness, armour, piercing, supply, recon,
            out outHardness, out outArmour, out outPiercing, out outSupply, out outRecon);
        return false;
    }

    public static unsafe bool TryNativeCombatLineProfile(
        ReadOnlySpan<float> divisions,
        ReadOnlySpan<float> hardness,
        ReadOnlySpan<float> armour,
        ReadOnlySpan<float> piercing,
        ReadOnlySpan<float> supply,
        ReadOnlySpan<float> recon,
        out float outHardness,
        out float outArmour,
        out float outPiercing,
        out float outSupply,
        out float outRecon)
    {
        outHardness = outArmour = outPiercing = outSupply = outRecon = 0f;
        var count = divisions.Length;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconCombat))
            return false;
        if (count == 0) return true;
        if (hardness.Length < count || armour.Length < count || piercing.Length < count ||
            supply.Length < count || recon.Length < count)
            return false;

        fixed (float* pDiv = divisions)
        fixed (float* pHard = hardness)
        fixed (float* pArm = armour)
        fixed (float* pPrc = piercing)
        fixed (float* pSup = supply)
        fixed (float* pRec = recon)
        fixed (float* pOutH = &outHardness)
        fixed (float* pOutA = &outArmour)
        fixed (float* pOutP = &outPiercing)
        fixed (float* pOutS = &outSupply)
        fixed (float* pOutR = &outRecon)
        {
            return dof_combat_line_profile_v1(
                (uint)count, pDiv, pHard, pArm, pPrc, pSup, pRec,
                pOutH, pOutA, pOutP, pOutS, pOutR) == 0;
        }
    }

    public static void ManagedCombatLineProfile(
        ReadOnlySpan<float> divisions,
        ReadOnlySpan<float> hardness,
        ReadOnlySpan<float> armour,
        ReadOnlySpan<float> piercing,
        ReadOnlySpan<float> supply,
        ReadOnlySpan<float> recon,
        out float outHardness,
        out float outArmour,
        out float outPiercing,
        out float outSupply,
        out float outRecon)
    {
        var n = divisions.Length;
        var sumDiv = 0f;
        for (var i = 0; i < n; i++)
            sumDiv += M.Max(0.01f, divisions[i]);
        if (sumDiv <= 0f)
        {
            outHardness = outArmour = outPiercing = outSupply = outRecon = 0f;
            return;
        }
        var sh = 0f;
        var sa = 0f;
        var sp = 0f;
        var ss = 0f;
        var sr = 0f;
        for (var i = 0; i < n; i++)
        {
            var w = M.Max(0.01f, divisions[i]);
            sh += hardness[i] * w;
            sa += armour[i] * w;
            sp += piercing[i] * w;
            ss += M.Clamp01(supply[i]) * w;
            sr += recon[i] * w;
        }
        outHardness = sh / sumDiv;
        outArmour = sa / sumDiv;
        outPiercing = sp / sumDiv;
        outSupply = ss / sumDiv;
        outRecon = sr / sumDiv;
    }

    public static bool CombatSideTotals(
        ReadOnlySpan<float> rawPower,
        ReadOnlySpan<float> offence,
        ReadOnlySpan<float> atkTrain,
        ReadOnlySpan<float> atkStance,
        ReadOnlySpan<float> atkRes,
        ReadOnlySpan<float> munitions,
        ReadOnlySpan<float> enc,
        ReadOnlySpan<float> spirit,
        ReadOnlySpan<float> cmdAtk,
        ReadOnlySpan<float> defStat,
        ReadOnlySpan<float> defTrain,
        ReadOnlySpan<float> defStance,
        ReadOnlySpan<float> defRes,
        ReadOnlySpan<float> cmdDef,
        ReadOnlySpan<float> entrenchment,
        bool weightOffenceTelemetry,
        Span<double> outTotals)
    {
        if (NativeKernelsLoader.EnableEconCombatDefault &&
            TryNativeCombatSideTotals(rawPower, offence, atkTrain, atkStance, atkRes, munitions,
                enc, spirit, cmdAtk, defStat, defTrain, defStance, defRes, cmdDef, entrenchment,
                weightOffenceTelemetry, outTotals))
        {
            return true;
        }
        ManagedCombatSideTotals(rawPower, offence, atkTrain, atkStance, atkRes, munitions,
            enc, spirit, cmdAtk, defStat, defTrain, defStance, defRes, cmdDef, entrenchment,
            weightOffenceTelemetry, outTotals);
        return false;
    }

    public static unsafe bool TryNativeCombatSideTotals(
        ReadOnlySpan<float> rawPower,
        ReadOnlySpan<float> offence,
        ReadOnlySpan<float> atkTrain,
        ReadOnlySpan<float> atkStance,
        ReadOnlySpan<float> atkRes,
        ReadOnlySpan<float> munitions,
        ReadOnlySpan<float> enc,
        ReadOnlySpan<float> spirit,
        ReadOnlySpan<float> cmdAtk,
        ReadOnlySpan<float> defStat,
        ReadOnlySpan<float> defTrain,
        ReadOnlySpan<float> defStance,
        ReadOnlySpan<float> defRes,
        ReadOnlySpan<float> cmdDef,
        ReadOnlySpan<float> entrenchment,
        bool weightOffenceTelemetry,
        Span<double> outTotals)
    {
        if (outTotals.Length < 9) return false;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconCombat)) return false;
        var count = rawPower.Length;
        if (count == 0)
        {
            outTotals[..9].Clear();
            return true;
        }
        if (offence.Length < count || atkTrain.Length < count || atkStance.Length < count ||
            atkRes.Length < count || munitions.Length < count || enc.Length < count ||
            spirit.Length < count || cmdAtk.Length < count || defStat.Length < count ||
            defTrain.Length < count || defStance.Length < count || defRes.Length < count ||
            cmdDef.Length < count || entrenchment.Length < count)
        {
            return false;
        }

        fixed (float* pPow = rawPower)
        fixed (float* pOff = offence)
        fixed (float* pAtr = atkTrain)
        fixed (float* pAst = atkStance)
        fixed (float* pAr = atkRes)
        fixed (float* pMun = munitions)
        fixed (float* pEnc = enc)
        fixed (float* pSp = spirit)
        fixed (float* pCa = cmdAtk)
        fixed (float* pDs = defStat)
        fixed (float* pDtr = defTrain)
        fixed (float* pDst = defStance)
        fixed (float* pDr = defRes)
        fixed (float* pCd = cmdDef)
        fixed (float* pEnt = entrenchment)
        fixed (double* pOut = outTotals)
        {
            return dof_combat_side_totals_v1(
                (uint)count, pPow, pOff, pAtr, pAst, pAr, pMun, pEnc, pSp, pCa,
                pDs, pDtr, pDst, pDr, pCd, pEnt,
                weightOffenceTelemetry ? (byte)1 : (byte)0, pOut) == 0;
        }
    }

    public static void ManagedCombatSideTotals(
        ReadOnlySpan<float> rawPower,
        ReadOnlySpan<float> offence,
        ReadOnlySpan<float> atkTrain,
        ReadOnlySpan<float> atkStance,
        ReadOnlySpan<float> atkRes,
        ReadOnlySpan<float> munitions,
        ReadOnlySpan<float> enc,
        ReadOnlySpan<float> spirit,
        ReadOnlySpan<float> cmdAtk,
        ReadOnlySpan<float> defStat,
        ReadOnlySpan<float> defTrain,
        ReadOnlySpan<float> defStance,
        ReadOnlySpan<float> defRes,
        ReadOnlySpan<float> cmdDef,
        ReadOnlySpan<float> entrenchment,
        bool weightOffenceTelemetry,
        Span<double> outTotals)
    {
        if (outTotals.Length < 9) throw new ArgumentException("outTotals requires length >= 9.");
        outTotals[..9].Clear();
        var count = rawPower.Length;
        for (var i = 0; i < count; i++)
        {
            var power = M.Max(0.01f, rawPower[i]);
            var at = atkTrain[i];
            var ast = atkStance[i];
            var ar = atkRes[i];
            var mun = munitions[i];
            var ec = enc[i];
            var sp = spirit[i];
            var ca = cmdAtk[i];

            var dt = defTrain[i];
            var dst = defStance[i];
            var dr = defRes[i];
            var cd = cmdDef[i];

            outTotals[0] += power * offence[i] * at * ast * ar * mun * ec * sp * ca;
            outTotals[1] += power * defStat[i] * dt * dst * dr * ec * sp * cd;
            outTotals[2] += entrenchment[i];
            double w = power;
            outTotals[3] += w;
            if (weightOffenceTelemetry)
            {
                outTotals[4] += w * at;
                outTotals[5] += w * ast;
                outTotals[6] += w * ca;
                outTotals[7] += w * ar;
            }
            else
            {
                outTotals[4] += w * dt;
                outTotals[5] += w * dst;
                outTotals[6] += w * cd;
                outTotals[7] += w * dr;
            }
            outTotals[8] += w * ec;
        }
    }

    public static bool SupportAttritionBatch(
        ReadOnlySpan<float> threat,
        ReadOnlySpan<float> modFactor,
        float attritionPerDay,
        float saturation,
        float floor,
        Span<float> strength,
        Span<float> experience,
        Span<float> outLoss)
    {
        if (NativeKernelsLoader.EnableEconCombatDefault &&
            TryNativeSupportAttritionBatch(threat, modFactor, attritionPerDay, saturation, floor,
                strength, experience, outLoss))
        {
            return true;
        }
        ManagedSupportAttritionBatch(threat, modFactor, attritionPerDay, saturation, floor,
            strength, experience, outLoss);
        return false;
    }

    public static unsafe bool TryNativeSupportAttritionBatch(
        ReadOnlySpan<float> threat,
        ReadOnlySpan<float> modFactor,
        float attritionPerDay,
        float saturation,
        float floor,
        Span<float> strength,
        Span<float> experience,
        Span<float> outLoss)
    {
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconCombat)) return false;
        var count = threat.Length;
        if (count == 0) return true;
        if (modFactor.Length < count || strength.Length < count || experience.Length < count || outLoss.Length < count)
            return false;

        fixed (float* pTh = threat)
        fixed (float* pMf = modFactor)
        fixed (float* pStr = strength)
        fixed (float* pExp = experience)
        fixed (float* pOut = outLoss)
        {
            return dof_support_attrition_batch_v1(
                (uint)count, pTh, pMf, attritionPerDay, saturation, floor, pStr, pExp, pOut) == 0;
        }
    }

    public static void ManagedSupportAttritionBatch(
        ReadOnlySpan<float> threat,
        ReadOnlySpan<float> modFactor,
        float attritionPerDay,
        float saturation,
        float floor,
        Span<float> strength,
        Span<float> experience,
        Span<float> outLoss)
    {
        var count = threat.Length;
        for (var i = 0; i < count; i++)
        {
            var th = threat[i];
            if (th <= 0f)
            {
                outLoss[i] = 0f;
                continue;
            }
            var loss = attritionPerDay * th / (th + saturation) * modFactor[i];
            loss = MathF.Min(loss, MathF.Max(0f, strength[i] - floor));
            if (loss <= 0f || float.IsNaN(loss))
            {
                outLoss[i] = 0f;
                continue;
            }
            strength[i] -= loss;
            outLoss[i] = loss;
            experience[i] = M.Clamp01(experience[i] + loss * 0.08f);
        }
    }

    public static bool SupportReachBatch(
        float targetLat,
        float targetLon,
        ReadOnlySpan<float> baseLat,
        ReadOnlySpan<float> baseLon,
        ReadOnlySpan<float> reachKm,
        ReadOnlySpan<float> strength,
        ReadOnlySpan<float> experience,
        Span<float> outWeight)
    {
        if (NativeKernelsLoader.EnableEconCombatDefault &&
            TryNativeSupportReachBatch(targetLat, targetLon, baseLat, baseLon, reachKm,
                strength, experience, outWeight))
        {
            return true;
        }
        ManagedSupportReachBatch(targetLat, targetLon, baseLat, baseLon, reachKm,
            strength, experience, outWeight);
        return false;
    }

    public static unsafe bool TryNativeSupportReachBatch(
        float targetLat,
        float targetLon,
        ReadOnlySpan<float> baseLat,
        ReadOnlySpan<float> baseLon,
        ReadOnlySpan<float> reachKm,
        ReadOnlySpan<float> strength,
        ReadOnlySpan<float> experience,
        Span<float> outWeight)
    {
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconCombat)) return false;
        var count = baseLat.Length;
        if (count == 0) return true;
        if (baseLon.Length < count || reachKm.Length < count || strength.Length < count ||
            experience.Length < count || outWeight.Length < count)
            return false;

        fixed (float* pLat = baseLat)
        fixed (float* pLon = baseLon)
        fixed (float* pRk = reachKm)
        fixed (float* pStr = strength)
        fixed (float* pExp = experience)
        fixed (float* pOut = outWeight)
        {
            return dof_support_reach_batch_v1(
                (uint)count, targetLat, targetLon, pLat, pLon, pRk, pStr, pExp, pOut) == 0;
        }
    }

    public static void ManagedSupportReachBatch(
        float targetLat,
        float targetLon,
        ReadOnlySpan<float> baseLat,
        ReadOnlySpan<float> baseLon,
        ReadOnlySpan<float> reachKm,
        ReadOnlySpan<float> strength,
        ReadOnlySpan<float> experience,
        Span<float> outWeight)
    {
        const float Radians = MathF.PI / 180f;
        var count = baseLat.Length;
        var phiT = targetLat * Radians;
        var cosPhiT = MathF.Cos(phiT);
        for (var i = 0; i < count; i++)
        {
            var blat = baseLat[i];
            var rk = reachKm[i];
            if (MathF.Abs(blat - targetLat) * 111.0f > rk)
            {
                outWeight[i] = -1f;
                continue;
            }
            var phiA = blat * Radians;
            var dPhi = (targetLat - blat) * Radians;
            var dLon = (targetLon - baseLon[i]) * Radians;
            var s1 = MathF.Sin(dPhi * 0.5f);
            var s2 = MathF.Sin(dLon * 0.5f);
            var h = s1 * s1 + MathF.Cos(phiA) * cosPhiT * s2 * s2;
            var km = 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(MathF.Max(0f, h))));
            outWeight[i] = km <= rk ? strength[i] * (1f + experience[i] * 0.35f) : -1f;
        }
    }

    public static bool EconStateWeekPost(
        ReadOnlySpan<float> nationSol,
        ReadOnlySpan<float> weeklyRadDelta,
        ReadOnlySpan<float> cycleRad,
        Span<float> stateSol,
        Span<float> stateRad)
    {
        if (NativeKernelsLoader.EnableEconCombatDefault &&
            TryNativeEconStateWeekPost(nationSol, weeklyRadDelta, cycleRad, stateSol, stateRad))
        {
            return true;
        }
        ManagedEconStateWeekPost(nationSol, weeklyRadDelta, cycleRad, stateSol, stateRad);
        return false;
    }

    public static unsafe bool TryNativeEconStateWeekPost(
        ReadOnlySpan<float> nationSol,
        ReadOnlySpan<float> weeklyRadDelta,
        ReadOnlySpan<float> cycleRad,
        Span<float> stateSol,
        Span<float> stateRad)
    {
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconCombat)) return false;
        var count = nationSol.Length;
        if (count == 0) return true;
        if (weeklyRadDelta.Length < count || cycleRad.Length < count ||
            stateSol.Length < count || stateRad.Length < count)
            return false;

        fixed (float* pNS = nationSol)
        fixed (float* pWR = weeklyRadDelta)
        fixed (float* pCR = cycleRad)
        fixed (float* pSS = stateSol)
        fixed (float* pSR = stateRad)
        {
            return dof_econ_state_week_post_v1((uint)count, pNS, pWR, pCR, pSS, pSR) == 0;
        }
    }

    public static void ManagedEconStateWeekPost(
        ReadOnlySpan<float> nationSol,
        ReadOnlySpan<float> weeklyRadDelta,
        ReadOnlySpan<float> cycleRad,
        Span<float> stateSol,
        Span<float> stateRad)
    {
        var count = nationSol.Length;
        for (var i = 0; i < count; i++)
        {
            stateSol[i] = M.Lerp(stateSol[i], nationSol[i], 0.04f);
            stateRad[i] = M.Clamp01(stateRad[i] + weeklyRadDelta[i] + cycleRad[i]);
        }
    }
}
