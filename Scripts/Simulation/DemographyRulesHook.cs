// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;

namespace DealOrForce.Simulation;

internal delegate float DemographyWorkingShareEvaluator(float youngShare, float oldShare);
internal delegate float DemographyFertilityEvaluator(float youngShare);
internal delegate float DemographyMortalityEvaluator(float oldShare, float referenceOldShare, float mortalityOldWeight);
internal delegate void DemographyCohortStepper(
    float population, long reviewBirths, long reviewDeaths,
    float youngShare, float oldShare,
    float maturationAnnual, float ageingAnnual, float mortalityOldWeight, float interval,
    out float nextYoungShare, out float nextOldShare, out float nextWorkingShare);

/// <summary>
/// Optional F# pure-rule hook for demographic cohorts and vital multipliers,
/// with bit-identical pure C# fallbacks.
/// </summary>
internal static class DemographyRulesHook
{
    private static DemographyWorkingShareEvaluator? _workingShare;
    private static DemographyFertilityEvaluator? _fertility;
    private static DemographyMortalityEvaluator? _mortality;
    private static DemographyCohortStepper? _stepCohorts;

    internal static bool Installed =>
        _workingShare is not null && _fertility is not null &&
        _mortality is not null && _stepCohorts is not null;

    internal static bool UsingFSharp => Installed;

    internal static void Install(
        DemographyWorkingShareEvaluator workingShare,
        DemographyFertilityEvaluator fertility,
        DemographyMortalityEvaluator mortality,
        DemographyCohortStepper stepCohorts)
    {
        _workingShare = workingShare;
        _fertility = fertility;
        _mortality = mortality;
        _stepCohorts = stepCohorts;
    }

    internal static float WorkingShare(float youngShare, float oldShare)
    {
        var hook = _workingShare;
        if (hook is not null)
        {
            try { return hook(youngShare, oldShare); }
            catch (Exception error)
            {
                _workingShare = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=demography_working reason={error.GetType().Name}");
            }
        }
        return ManagedWorkingShare(youngShare, oldShare);
    }

    internal static float FertilityFactor(float youngShare)
    {
        var hook = _fertility;
        if (hook is not null)
        {
            try { return hook(youngShare); }
            catch (Exception error)
            {
                _fertility = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=demography_fertility reason={error.GetType().Name}");
            }
        }
        return ManagedFertilityFactor(youngShare);
    }

    internal static float MortalityFactor(float oldShare, float referenceOldShare, float mortalityOldWeight)
    {
        var hook = _mortality;
        if (hook is not null)
        {
            try { return hook(oldShare, referenceOldShare, mortalityOldWeight); }
            catch (Exception error)
            {
                _mortality = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=demography_mortality reason={error.GetType().Name}");
            }
        }
        return ManagedMortalityFactor(oldShare, referenceOldShare, mortalityOldWeight);
    }

    internal static void StepCohorts(
        float population, long reviewBirths, long reviewDeaths,
        float youngShare, float oldShare,
        float maturationAnnual, float ageingAnnual, float mortalityOldWeight, float interval,
        out float nextYoungShare, out float nextOldShare, out float nextWorkingShare)
    {
        var hook = _stepCohorts;
        if (hook is not null)
        {
            try
            {
                hook(population, reviewBirths, reviewDeaths, youngShare, oldShare,
                    maturationAnnual, ageingAnnual, mortalityOldWeight, interval,
                    out nextYoungShare, out nextOldShare, out nextWorkingShare);
                return;
            }
            catch (Exception error)
            {
                _stepCohorts = null;
                DiagnosticLogs.Write("simulation",
                    $"RULES_BACKEND fallback=managed rule=demography_step reason={error.GetType().Name}");
            }
        }
        ManagedStepCohorts(population, reviewBirths, reviewDeaths, youngShare, oldShare,
            maturationAnnual, ageingAnnual, mortalityOldWeight, interval,
            out nextYoungShare, out nextOldShare, out nextWorkingShare);
    }

    internal static float ManagedWorkingShare(float youngShare, float oldShare) =>
        Math.Clamp(1f - youngShare - oldShare, 0.05f, 0.95f);

    internal static float ManagedFertilityFactor(float youngShare) =>
        Math.Clamp(1f - (0.28f - youngShare) * 2f, 0.35f, 1.25f);

    internal static float ManagedMortalityFactor(float oldShare, float referenceOldShare, float mortalityOldWeight)
    {
        var excess = Math.Max(0f, oldShare - referenceOldShare);
        return 1f + excess * mortalityOldWeight * 3f;
    }

    internal static void ManagedStepCohorts(
        float population, long reviewBirths, long reviewDeaths,
        float youngShare, float oldShare,
        float maturationAnnual, float ageingAnnual, float mortalityOldWeight, float interval,
        out float nextYoungShare, out float nextOldShare, out float nextWorkingShare)
    {
        var pop = Math.Max(1f, population);
        var born = Math.Clamp((float)reviewBirths / pop, 0f, 0.2f);
        var died = Math.Clamp((float)reviewDeaths / pop, 0f, 0.2f);
        var working = ManagedWorkingShare(youngShare, oldShare);
        var matured = youngShare * maturationAnnual * interval;
        var aged = working * ageingAnnual * interval;
        var oldDeaths = Math.Min(oldShare, died * mortalityOldWeight);
        var rest = Math.Max(0f, died - oldDeaths);
        nextYoungShare = Math.Clamp(youngShare - matured - rest * 0.5f + born, 0.05f, 0.62f);
        nextOldShare = Math.Clamp(oldShare + aged - oldDeaths, 0.02f, 0.45f);
        nextWorkingShare = ManagedWorkingShare(nextYoungShare, nextOldShare);
    }
}
