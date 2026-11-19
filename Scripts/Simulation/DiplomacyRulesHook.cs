// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;

namespace DealOrForce.Simulation;

internal delegate float DiplomacyAmbitionEvaluator(float authority, bool isNationalist, float stability, float restraint);
internal delegate float DiplomacyWarValueEvaluator(float ambition, float forceAmbition, float theirShare, float kinship, float appetite);
internal delegate float DiplomacyBuyPeaceEvaluator(float treasury, float discretionaryCash, float ourStrength, float theirStrength);
internal delegate void DiplomacyDeterrenceEvaluator(int tierA, int tierB, float mutualPriceDefine, bool enabled, out float mutualPrice, out float brinkmanship);

/// <summary>
/// Optional F# pure-rule hook for union consolidation, peace purchase, and bilateral deterrence,
/// with bit-identical pure C# fallbacks.
/// </summary>
internal static class DiplomacyRulesHook
{
    private static DiplomacyAmbitionEvaluator? _ambition;
    private static DiplomacyWarValueEvaluator? _warValue;
    private static DiplomacyBuyPeaceEvaluator? _buyPeace;
    private static DiplomacyDeterrenceEvaluator? _deterrence;

    internal static bool Installed =>
        _ambition is not null && _warValue is not null &&
        _buyPeace is not null && _deterrence is not null;

    internal static bool UsingFSharp => Installed;

    internal static void Install(
        DiplomacyAmbitionEvaluator ambition,
        DiplomacyWarValueEvaluator warValue,
        DiplomacyBuyPeaceEvaluator buyPeace,
        DiplomacyDeterrenceEvaluator deterrence)
    {
        _ambition = ambition;
        _warValue = warValue;
        _buyPeace = buyPeace;
        _deterrence = deterrence;
    }

    internal static float ConsolidationAmbition(float authority, bool isNationalist, float stability, float restraint)
    {
        var hook = _ambition;
        if (hook is not null)
        {
            try { return hook(authority, isNationalist, stability, restraint); }
            catch (Exception error)
            {
                _ambition = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=diplomacy_ambition reason={error.GetType().Name}");
            }
        }
        return ManagedConsolidationAmbition(authority, isNationalist, stability, restraint);
    }

    internal static float ConsolidationWarValue(float ambition, float forceAmbition, float theirShare, float kinship, float appetite)
    {
        var hook = _warValue;
        if (hook is not null)
        {
            try { return hook(ambition, forceAmbition, theirShare, kinship, appetite); }
            catch (Exception error)
            {
                _warValue = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=diplomacy_war_value reason={error.GetType().Name}");
            }
        }
        return ManagedConsolidationWarValue(ambition, forceAmbition, theirShare, kinship, appetite);
    }

    internal static float BuyPeaceOffer(float treasury, float discretionaryCash, float ourStrength, float theirStrength)
    {
        var hook = _buyPeace;
        if (hook is not null)
        {
            try { return hook(treasury, discretionaryCash, ourStrength, theirStrength); }
            catch (Exception error)
            {
                _buyPeace = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=diplomacy_buy_peace reason={error.GetType().Name}");
            }
        }
        return ManagedBuyPeaceOffer(treasury, discretionaryCash, ourStrength, theirStrength);
    }

    internal static void EvaluateDeterrence(
        int tierA, int tierB, float mutualPriceDefine, bool enabled,
        out float mutualPrice, out float brinkmanship)
    {
        var hook = _deterrence;
        if (hook is not null)
        {
            try
            {
                hook(tierA, tierB, mutualPriceDefine, enabled, out mutualPrice, out brinkmanship);
                return;
            }
            catch (Exception error)
            {
                _deterrence = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=diplomacy_deterrence reason={error.GetType().Name}");
            }
        }
        ManagedEvaluateDeterrence(tierA, tierB, mutualPriceDefine, enabled, out mutualPrice, out brinkmanship);
    }

    private static float Clamp01(float v) => !float.IsFinite(v) ? 0f : Math.Clamp(v, 0f, 1f);

    internal static float ManagedConsolidationAmbition(float authority, bool isNationalist, float stability, float restraint)
    {
        var auth = Math.Clamp(authority, -1f, 1f);
        var natBonus = isNationalist ? 0.25f : 0f;
        var settled = Math.Clamp((stability - 0.40f) * 0.8f, -0.3f, 0.4f);
        return Clamp01(0.36f + 0.35f * auth + natBonus + settled - 0.30f * restraint);
    }

    internal static float ManagedConsolidationWarValue(float ambition, float forceAmbition, float theirShare, float kinship, float appetite)
    {
        if (ambition < forceAmbition || theirShare <= 0f || kinship < 0.5f) return 0f;
        return Clamp01(ambition) * Clamp01(theirShare * 2.0f) * kinship * appetite;
    }

    internal static float ManagedBuyPeaceOffer(float treasury, float discretionaryCash, float ourStrength, float theirStrength)
    {
        if (ourStrength >= theirStrength || treasury <= 0f || discretionaryCash <= 0f) return 0f;
        var gap = Math.Clamp((theirStrength - ourStrength) / Math.Max(0.01f, theirStrength), 0f, 1f);
        return Math.Clamp(treasury * (0.1f + 0.4f * gap), 0f, discretionaryCash);
    }

    internal static void ManagedEvaluateDeterrence(
        int tierA, int tierB, float mutualPriceDefine, bool enabled,
        out float mutualPrice, out float brinkmanship)
    {
        var bothHold = tierA >= 1 && tierB >= 1;
        mutualPrice = bothHold ? mutualPriceDefine : 0f;
        brinkmanship = enabled && bothHold ? (float)Math.Min(4, tierA * tierB) : 0f;
    }
}
