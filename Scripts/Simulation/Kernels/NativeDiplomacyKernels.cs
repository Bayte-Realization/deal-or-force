// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Batched native SIMD diplomacy scoring (<c>dip:opinions</c> / <c>ai:diplomacy</c>) wrappers
/// backed by <c>dof_kernels</c> (<c>dof_dip_*</c>) with bit-compatible pure C# fallbacks.
/// </summary>
public static partial class NativeDiplomacyKernels
{
    public const byte PairFlagClaimOrHome = 1 << 0;
    public const byte PairFlagBothGp = 1 << 1;
    public const byte PairFlagImagesOn = 1 << 2;

    public const byte StanceAtWar = 4;

    public const byte ActionEligibleFriend = 1 << 0;
    public const byte ActionLiftEmbargo = 1 << 1;
    public const byte ActionReconcileRival = 1 << 2;
    public const byte ActionMissionCandidate = 1 << 3;
    public const byte ActionTradeOk = 1 << 4;
    public const byte ActionPactOk = 1 << 5;
    public const byte ActionAllianceOk = 1 << 6;
    public const byte ActionRivalCandidate = 1 << 7;

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_dip_pair_score_batch_v1(
        uint pairCount,
        float* leftEcon,
        float* leftAuth,
        float* rightEcon,
        float* rightAuth,
        float* esteemAb,
        float* esteemBa,
        float* claimLeft,
        float* claimRight,
        float* claimBonus,
        float* blocBias,
        float* baseReasonSum,
        float* witnessAgainst,
        float* witnessFor,
        float* identityAffinity,
        float* strengthRatio,
        float* fearReach,
        byte* pairFlags,
        float ideologyWeight,
        float socialWeight,
        float geopoliticsRamp,
        float thirdPartyFloor,
        float thirdPartyCeiling,
        float* outPolitics,
        float* outSocial,
        float* outClaims,
        float* outGeopolitics,
        float* outOpinion,
        float* outFear);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_dip_reason_decay_batch_v1(
        uint reasonCount,
        float* baseOpinion,
        int* stacks,
        float* scale,
        int* grantedDay,
        int* expiresDay,
        int* decayDays,
        byte* flags,
        int today,
        float* outWeight,
        byte* outPrune);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_dip_partner_eval_batch_v1(
        uint partnerCount,
        float* opinion,
        byte* stance,
        float* blocBias,
        float* tradeDep,
        byte* flags,
        float reach,
        float baseTradeTh,
        float basePactTh,
        float baseAllianceTh,
        int eraMode,
        float friendshipTh,
        float rivalryTh,
        float embargoReconcileOp,
        byte canMissionBudget,
        byte* outActionMask,
        float* outTradeTh,
        int* outBestFriendIdx,
        int* outWorstRivalIdx);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp(float v, float lo, float hi)
        => v < lo ? lo : (v > hi ? hi : v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp01(float v) => Clamp(v, 0f, 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float MaxF32(float a, float b) => a > b ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float MinF32(float a, float b) => a < b ? a : b;

    /// <summary>
    /// Scores a batch of diplomatic pairs using native SIMD when enabled and available,
    /// falling back transparently to <see cref="ManagedScorePairs"/>.
    /// </summary>
    public static bool ScorePairs(
        ReadOnlySpan<float> leftEcon,
        ReadOnlySpan<float> leftAuth,
        ReadOnlySpan<float> rightEcon,
        ReadOnlySpan<float> rightAuth,
        ReadOnlySpan<float> esteemAb,
        ReadOnlySpan<float> esteemBa,
        ReadOnlySpan<float> claimLeft,
        ReadOnlySpan<float> claimRight,
        ReadOnlySpan<float> claimBonus,
        ReadOnlySpan<float> blocBias,
        ReadOnlySpan<float> baseReasonSum,
        ReadOnlySpan<float> witnessAgainst,
        ReadOnlySpan<float> witnessFor,
        ReadOnlySpan<float> identityAffinity,
        ReadOnlySpan<float> strengthRatio,
        ReadOnlySpan<float> fearReach,
        ReadOnlySpan<byte> pairFlags,
        float ideologyWeight,
        float socialWeight,
        float geopoliticsRamp,
        float thirdPartyFloor,
        float thirdPartyCeiling,
        Span<float> outPolitics,
        Span<float> outSocial,
        Span<float> outClaims,
        Span<float> outGeopolitics,
        Span<float> outOpinion,
        Span<float> outFear)
    {
        if (NativeKernelsLoader.EnableDiplomacyDefault &&
            TryNativeScorePairs(
                leftEcon, leftAuth, rightEcon, rightAuth, esteemAb, esteemBa,
                claimLeft, claimRight, claimBonus, blocBias, baseReasonSum,
                witnessAgainst, witnessFor, identityAffinity, strengthRatio, fearReach,
                pairFlags, ideologyWeight, socialWeight, geopoliticsRamp,
                thirdPartyFloor, thirdPartyCeiling,
                outPolitics, outSocial, outClaims, outGeopolitics, outOpinion, outFear))
        {
            return true;
        }

        ManagedScorePairs(
            leftEcon, leftAuth, rightEcon, rightAuth, esteemAb, esteemBa,
            claimLeft, claimRight, claimBonus, blocBias, baseReasonSum,
            witnessAgainst, witnessFor, identityAffinity, strengthRatio, fearReach,
            pairFlags, ideologyWeight, socialWeight, geopoliticsRamp,
            thirdPartyFloor, thirdPartyCeiling,
            outPolitics, outSocial, outClaims, outGeopolitics, outOpinion, outFear);
        return false;
    }

    public static unsafe bool TryNativeScorePairs(
        ReadOnlySpan<float> leftEcon,
        ReadOnlySpan<float> leftAuth,
        ReadOnlySpan<float> rightEcon,
        ReadOnlySpan<float> rightAuth,
        ReadOnlySpan<float> esteemAb,
        ReadOnlySpan<float> esteemBa,
        ReadOnlySpan<float> claimLeft,
        ReadOnlySpan<float> claimRight,
        ReadOnlySpan<float> claimBonus,
        ReadOnlySpan<float> blocBias,
        ReadOnlySpan<float> baseReasonSum,
        ReadOnlySpan<float> witnessAgainst,
        ReadOnlySpan<float> witnessFor,
        ReadOnlySpan<float> identityAffinity,
        ReadOnlySpan<float> strengthRatio,
        ReadOnlySpan<float> fearReach,
        ReadOnlySpan<byte> pairFlags,
        float ideologyWeight,
        float socialWeight,
        float geopoliticsRamp,
        float thirdPartyFloor,
        float thirdPartyCeiling,
        Span<float> outPolitics,
        Span<float> outSocial,
        Span<float> outClaims,
        Span<float> outGeopolitics,
        Span<float> outOpinion,
        Span<float> outFear)
    {
        var n = leftEcon.Length;
        if (!ValidatePairScoreSpans(
                n, leftAuth, rightEcon, rightAuth, esteemAb, esteemBa,
                claimLeft, claimRight, claimBonus, blocBias, baseReasonSum,
                witnessAgainst, witnessFor, identityAffinity, strengthRatio, fearReach,
                pairFlags, outPolitics, outSocial, outClaims, outGeopolitics, outOpinion, outFear))
        {
            return false;
        }
        if (n == 0) return true;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapDiplomacy)) return false;

        fixed (float* pLe = leftEcon)
        fixed (float* pLa = leftAuth)
        fixed (float* pRe = rightEcon)
        fixed (float* pRa = rightAuth)
        fixed (float* pEab = esteemAb)
        fixed (float* pEba = esteemBa)
        fixed (float* pCl = claimLeft)
        fixed (float* pCr = claimRight)
        fixed (float* pCb = claimBonus)
        fixed (float* pBb = blocBias)
        fixed (float* pBr = baseReasonSum)
        fixed (float* pWa = witnessAgainst)
        fixed (float* pWf = witnessFor)
        fixed (float* pId = identityAffinity)
        fixed (float* pSr = strengthRatio)
        fixed (float* pFr = fearReach)
        fixed (byte* pPf = pairFlags)
        fixed (float* pOp = outPolitics)
        fixed (float* pOs = outSocial)
        fixed (float* pOc = outClaims)
        fixed (float* pOg = outGeopolitics)
        fixed (float* pOo = outOpinion)
        fixed (float* pOf = outFear)
        {
            return dof_dip_pair_score_batch_v1(
                (uint)n,
                pLe, pLa, pRe, pRa, pEab, pEba, pCl, pCr, pCb, pBb, pBr,
                pWa, pWf, pId, pSr, pFr, pPf,
                ideologyWeight, socialWeight, geopoliticsRamp, thirdPartyFloor, thirdPartyCeiling,
                pOp, pOs, pOc, pOg, pOo, pOf) == 0;
        }
    }

    public static void ManagedScorePairs(
        ReadOnlySpan<float> leftEcon,
        ReadOnlySpan<float> leftAuth,
        ReadOnlySpan<float> rightEcon,
        ReadOnlySpan<float> rightAuth,
        ReadOnlySpan<float> esteemAb,
        ReadOnlySpan<float> esteemBa,
        ReadOnlySpan<float> claimLeft,
        ReadOnlySpan<float> claimRight,
        ReadOnlySpan<float> claimBonus,
        ReadOnlySpan<float> blocBias,
        ReadOnlySpan<float> baseReasonSum,
        ReadOnlySpan<float> witnessAgainst,
        ReadOnlySpan<float> witnessFor,
        ReadOnlySpan<float> identityAffinity,
        ReadOnlySpan<float> strengthRatio,
        ReadOnlySpan<float> fearReach,
        ReadOnlySpan<byte> pairFlags,
        float ideologyWeight,
        float socialWeight,
        float geopoliticsRamp,
        float thirdPartyFloor,
        float thirdPartyCeiling,
        Span<float> outPolitics,
        Span<float> outSocial,
        Span<float> outClaims,
        Span<float> outGeopolitics,
        Span<float> outOpinion,
        Span<float> outFear)
    {
        var n = leftEcon.Length;
        if (!ValidatePairScoreSpans(
                n, leftAuth, rightEcon, rightAuth, esteemAb, esteemBa,
                claimLeft, claimRight, claimBonus, blocBias, baseReasonSum,
                witnessAgainst, witnessFor, identityAffinity, strengthRatio, fearReach,
                pairFlags, outPolitics, outSocial, outClaims, outGeopolitics, outOpinion, outFear))
        {
            throw new ArgumentException("Mismatched span lengths for ManagedScorePairs.");
        }
        if (n == 0) return;

        var ramp = Clamp01(geopoliticsRamp);
        var floor = MinF32(0f, thirdPartyFloor);
        var ceiling = MaxF32(0f, thirdPartyCeiling);
        var hasIdeology = ideologyWeight > 0f;
        var hasRamp = ramp > 0f;

        for (int i = 0; i < n; i++)
        {
            var flags = pairFlags[i];
            var de = leftEcon[i] - rightEcon[i];
            var da = leftAuth[i] - rightAuth[i];
            var ideolDist = Clamp01(MathF.Sqrt(de * de + da * da) / 2.8284f);
            var politics = hasIdeology ? (0.38f - ideolDist) * ideologyWeight : 0f;

            var social = (flags & PairFlagImagesOn) != 0
                ? (esteemAb[i] + esteemBa[i]) * 0.5f * socialWeight
                : 0f;

            float claims;
            if ((flags & PairFlagClaimOrHome) != 0)
            {
                var wl = claimLeft[i];
                var wr = claimRight[i];
                var mx = MaxF32(wl, wr);
                var mn = MinF32(wl, wr);
                var intensity = Clamp01(mx + mn * 0.5f + claimBonus[i]);
                claims = -(22.0f + 13.0f * intensity);
            }
            else
            {
                claims = 0f;
            }

            float geo = 0f;
            if (hasRamp)
            {
                geo = blocBias[i] * 18.0f * ramp;
                if ((flags & PairFlagBothGp) != 0)
                {
                    var gpDist = ideolDist < 1.0f ? ideolDist : 1.0f;
                    geo -= (8.0f + 10.0f * gpDist) * ramp;
                }
            }

            var total = baseReasonSum[i];
            var wa = witnessAgainst[i];
            if (wa < floor) total += floor - wa;
            var wf = witnessFor[i];
            if (wf > ceiling) total -= wf - ceiling;
            if (MathF.Abs(politics) >= 0.05f) total += politics;
            var idAff = identityAffinity[i];
            if (idAff >= 0.05f) total += idAff;
            if (MathF.Abs(social) >= 0.05f) total += social;
            if (MathF.Abs(claims) >= 0.05f) total += claims;
            if (MathF.Abs(geo) >= 0.05f) total += geo;

            outPolitics[i] = politics;
            outSocial[i] = social;
            outClaims[i] = claims;
            outGeopolitics[i] = geo;
            outOpinion[i] = Clamp(total, -100f, 100f);
            outFear[i] = Clamp(strengthRatio[i] - 1.0f, 0f, 2.0f) * fearReach[i];
        }
    }

    public static bool EvalReasonDecay(
        ReadOnlySpan<float> baseOpinion,
        ReadOnlySpan<int> stacks,
        ReadOnlySpan<float> scale,
        ReadOnlySpan<int> grantedDay,
        ReadOnlySpan<int> expiresDay,
        ReadOnlySpan<int> decayDays,
        ReadOnlySpan<byte> flags,
        int today,
        Span<float> outWeight,
        Span<byte> outPrune)
    {
        if (NativeKernelsLoader.EnableDiplomacyDefault &&
            TryNativeEvalReasonDecay(
                baseOpinion, stacks, scale, grantedDay, expiresDay, decayDays, flags,
                today, outWeight, outPrune))
        {
            return true;
        }

        ManagedEvalReasonDecay(
            baseOpinion, stacks, scale, grantedDay, expiresDay, decayDays, flags,
            today, outWeight, outPrune);
        return false;
    }

    public static unsafe bool TryNativeEvalReasonDecay(
        ReadOnlySpan<float> baseOpinion,
        ReadOnlySpan<int> stacks,
        ReadOnlySpan<float> scale,
        ReadOnlySpan<int> grantedDay,
        ReadOnlySpan<int> expiresDay,
        ReadOnlySpan<int> decayDays,
        ReadOnlySpan<byte> flags,
        int today,
        Span<float> outWeight,
        Span<byte> outPrune)
    {
        var n = baseOpinion.Length;
        if (stacks.Length < n || scale.Length < n || grantedDay.Length < n ||
            expiresDay.Length < n || decayDays.Length < n || flags.Length < n ||
            outWeight.Length < n || outPrune.Length < n)
        {
            return false;
        }
        if (n == 0) return true;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapDiplomacy)) return false;

        fixed (float* pBase = baseOpinion)
        fixed (int* pSt = stacks)
        fixed (float* pSc = scale)
        fixed (int* pGd = grantedDay)
        fixed (int* pEd = expiresDay)
        fixed (int* pDd = decayDays)
        fixed (byte* pFl = flags)
        fixed (float* pOw = outWeight)
        fixed (byte* pOp = outPrune)
        {
            return dof_dip_reason_decay_batch_v1(
                (uint)n, pBase, pSt, pSc, pGd, pEd, pDd, pFl, today, pOw, pOp) == 0;
        }
    }

    public static void ManagedEvalReasonDecay(
        ReadOnlySpan<float> baseOpinion,
        ReadOnlySpan<int> stacks,
        ReadOnlySpan<float> scale,
        ReadOnlySpan<int> grantedDay,
        ReadOnlySpan<int> expiresDay,
        ReadOnlySpan<int> decayDays,
        ReadOnlySpan<byte> flags,
        int today,
        Span<float> outWeight,
        Span<byte> outPrune)
    {
        var n = baseOpinion.Length;
        if (stacks.Length < n || scale.Length < n || grantedDay.Length < n ||
            expiresDay.Length < n || decayDays.Length < n || flags.Length < n ||
            outWeight.Length < n || outPrune.Length < n)
        {
            throw new ArgumentException("Mismatched span lengths for ManagedEvalReasonDecay.");
        }

        for (int i = 0; i < n; i++)
        {
            var baseOp = baseOpinion[i];
            var exp = expiresDay[i];
            var sc = scale[i];
            var days = decayDays[i];
            var decays = (flags[i] & 1) != 0;

            var expiredOrInvalid = (exp > 0 && exp <= today) || !float.IsFinite(sc);
            var effStacks = (float)(stacks[i] > 1 ? stacks[i] : 1);
            var effScale = sc <= 0f ? 1.0f : sc;
            var rawVal = baseOp * effStacks * effScale;
            float weight;
            if (!decays || days <= 0 || exp <= 0)
            {
                weight = rawVal;
            }
            else
            {
                var lifeDiff = exp - grantedDay[i];
                var life = (float)(lifeDiff > 1 ? lifeDiff : 1);
                var leftDiff = exp - today;
                var left = (float)(leftDiff > 0 ? leftDiff : 0);
                weight = rawVal * (left / life);
            }
            var inert = expiredOrInvalid || baseOp == 0f || (decays && days > 0 && exp > 0 && MathF.Abs(weight) < 0.05f);
            outWeight[i] = weight;
            outPrune[i] = (byte)(inert ? 1 : 0);
        }
    }

    public static bool EvalPartners(
        ReadOnlySpan<float> opinion,
        ReadOnlySpan<byte> stance,
        ReadOnlySpan<float> blocBias,
        ReadOnlySpan<float> tradeDep,
        ReadOnlySpan<byte> flags,
        float reach,
        float baseTradeTh,
        float basePactTh,
        float baseAllianceTh,
        int eraMode,
        float friendshipTh,
        float rivalryTh,
        float embargoReconcileOp,
        bool canMissionBudget,
        Span<byte> outActionMask,
        Span<float> outTradeTh,
        out int bestFriendIdx,
        out int worstRivalIdx)
    {
        if (NativeKernelsLoader.EnableDiplomacyDefault &&
            TryNativeEvalPartners(
                opinion, stance, blocBias, tradeDep, flags,
                reach, baseTradeTh, basePactTh, baseAllianceTh, eraMode,
                friendshipTh, rivalryTh, embargoReconcileOp, canMissionBudget,
                outActionMask, outTradeTh, out bestFriendIdx, out worstRivalIdx))
        {
            return true;
        }

        ManagedEvalPartners(
            opinion, stance, blocBias, tradeDep, flags,
            reach, baseTradeTh, basePactTh, baseAllianceTh, eraMode,
            friendshipTh, rivalryTh, embargoReconcileOp, canMissionBudget,
            outActionMask, outTradeTh, out bestFriendIdx, out worstRivalIdx);
        return false;
    }

    public static unsafe bool TryNativeEvalPartners(
        ReadOnlySpan<float> opinion,
        ReadOnlySpan<byte> stance,
        ReadOnlySpan<float> blocBias,
        ReadOnlySpan<float> tradeDep,
        ReadOnlySpan<byte> flags,
        float reach,
        float baseTradeTh,
        float basePactTh,
        float baseAllianceTh,
        int eraMode,
        float friendshipTh,
        float rivalryTh,
        float embargoReconcileOp,
        bool canMissionBudget,
        Span<byte> outActionMask,
        Span<float> outTradeTh,
        out int bestFriendIdx,
        out int worstRivalIdx)
    {
        bestFriendIdx = -1;
        worstRivalIdx = -1;
        var n = opinion.Length;
        if (stance.Length < n || blocBias.Length < n || tradeDep.Length < n ||
            flags.Length < n || outActionMask.Length < n || outTradeTh.Length < n)
        {
            return false;
        }
        if (n == 0) return true;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapDiplomacy)) return false;

        int bf = -1;
        int wr = -1;
        fixed (float* pOp = opinion)
        fixed (byte* pSt = stance)
        fixed (float* pBb = blocBias)
        fixed (float* pTd = tradeDep)
        fixed (byte* pFl = flags)
        fixed (byte* pOm = outActionMask)
        fixed (float* pOt = outTradeTh)
        {
            var rc = dof_dip_partner_eval_batch_v1(
                (uint)n, pOp, pSt, pBb, pTd, pFl,
                reach, baseTradeTh, basePactTh, baseAllianceTh, eraMode,
                friendshipTh, rivalryTh, embargoReconcileOp, (byte)(canMissionBudget ? 1 : 0),
                pOm, pOt, &bf, &wr);
            if (rc != 0) return false;
        }
        bestFriendIdx = bf;
        worstRivalIdx = wr;
        return true;
    }

    public static void ManagedEvalPartners(
        ReadOnlySpan<float> opinion,
        ReadOnlySpan<byte> stance,
        ReadOnlySpan<float> blocBias,
        ReadOnlySpan<float> tradeDep,
        ReadOnlySpan<byte> flags,
        float reach,
        float baseTradeTh,
        float basePactTh,
        float baseAllianceTh,
        int eraMode,
        float friendshipTh,
        float rivalryTh,
        float embargoReconcileOp,
        bool canMissionBudget,
        Span<byte> outActionMask,
        Span<float> outTradeTh,
        out int bestFriendIdx,
        out int worstRivalIdx)
    {
        var n = opinion.Length;
        if (stance.Length < n || blocBias.Length < n || tradeDep.Length < n ||
            flags.Length < n || outActionMask.Length < n || outTradeTh.Length < n)
        {
            throw new ArgumentException("Mismatched span lengths for ManagedEvalPartners.");
        }

        var r = Clamp(reach, 0.25f, 2.5f);
        var eraTrade = eraMode == 1 ? 0.88f : (eraMode == 2 ? 1.10f : 1.0f);
        var eraPact = eraMode == 1 ? 0.90f : (eraMode == 2 ? 1.10f : 1.0f);
        var eraAlliance = eraMode == 1 ? 0.94f : (eraMode == 2 ? 1.08f : 1.0f);

        bestFriendIdx = -1;
        var bestOp = float.MinValue;
        worstRivalIdx = -1;
        var worstOp = float.MaxValue;

        for (int i = 0; i < n; i++)
        {
            var st = stance[i];
            var op = opinion[i];
            if (st == StanceAtWar)
            {
                outActionMask[i] = 0;
                outTradeTh[i] = 0f;
                continue;
            }
            if (op > bestOp)
            {
                bestOp = op;
                bestFriendIdx = i;
            }
            if (op < worstOp)
            {
                worstOp = op;
                worstRivalIdx = i;
            }

            var blocScale = Clamp(1.0f - blocBias[i] * 0.22f, 0.72f, 1.32f);
            var tradeTh = (baseTradeTh / r) * blocScale * eraTrade;
            var pactTh = (basePactTh / r) * blocScale * eraPact;
            var allianceTh = (baseAllianceTh / r) * blocScale * eraAlliance;
            outTradeTh[i] = tradeTh;

            var f = flags[i];
            var borders = (f & 1) != 0;
            var embargoByUs = (f & 2) != 0;
            var hasMission = (f & 4) != 0;

            byte mask = 0;
            if (op >= friendshipTh) mask |= ActionEligibleFriend;
            if (embargoByUs && op >= embargoReconcileOp) mask |= ActionLiftEmbargo;
            if (st == 3 && op >= 35.0f) mask |= ActionReconcileRival;
            if (canMissionBudget &&
                op < allianceTh &&
                !hasMission &&
                (tradeDep[i] > 0.02f || borders || op >= 10.0f))
            {
                mask |= ActionMissionCandidate;
            }
            if (op >= tradeTh) mask |= ActionTradeOk;
            if (op >= pactTh) mask |= ActionPactOk;
            if (st != 2 && op >= allianceTh) mask |= ActionAllianceOk;
            if (op <= rivalryTh) mask |= ActionRivalCandidate;
            outActionMask[i] = mask;
        }
    }

    private static bool ValidatePairScoreSpans(
        int n,
        ReadOnlySpan<float> leftAuth,
        ReadOnlySpan<float> rightEcon,
        ReadOnlySpan<float> rightAuth,
        ReadOnlySpan<float> esteemAb,
        ReadOnlySpan<float> esteemBa,
        ReadOnlySpan<float> claimLeft,
        ReadOnlySpan<float> claimRight,
        ReadOnlySpan<float> claimBonus,
        ReadOnlySpan<float> blocBias,
        ReadOnlySpan<float> baseReasonSum,
        ReadOnlySpan<float> witnessAgainst,
        ReadOnlySpan<float> witnessFor,
        ReadOnlySpan<float> identityAffinity,
        ReadOnlySpan<float> strengthRatio,
        ReadOnlySpan<float> fearReach,
        ReadOnlySpan<byte> pairFlags,
        Span<float> outPolitics,
        Span<float> outSocial,
        Span<float> outClaims,
        Span<float> outGeopolitics,
        Span<float> outOpinion,
        Span<float> outFear)
    {
        return leftAuth.Length >= n &&
               rightEcon.Length >= n &&
               rightAuth.Length >= n &&
               esteemAb.Length >= n &&
               esteemBa.Length >= n &&
               claimLeft.Length >= n &&
               claimRight.Length >= n &&
               claimBonus.Length >= n &&
               blocBias.Length >= n &&
               baseReasonSum.Length >= n &&
               witnessAgainst.Length >= n &&
               witnessFor.Length >= n &&
               identityAffinity.Length >= n &&
               strengthRatio.Length >= n &&
               fearReach.Length >= n &&
               pairFlags.Length >= n &&
               outPolitics.Length >= n &&
               outSocial.Length >= n &&
               outClaims.Length >= n &&
               outGeopolitics.Length >= n &&
               outOpinion.Length >= n &&
               outFear.Length >= n;
    }
}
