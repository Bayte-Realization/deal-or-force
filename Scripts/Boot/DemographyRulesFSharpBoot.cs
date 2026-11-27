using System;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Installs the zero-alloc F# [&lt;Struct&gt;] rules (Scripts/Rules/DemographyRules.fs) into DemographyRulesHook.</summary>
internal static class DemographyRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            DemographyRulesHook.Install(
                DealOrForce.Rules.DemographyRules.WorkingShare,
                DealOrForce.Rules.DemographyRules.FertilityFactor,
                DealOrForce.Rules.DemographyRules.MortalityFactor,
                StepCohorts,
                builtInEquivalent: true);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=demography");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation", $"RULES_BACKEND install_failed=demography reason={error.GetType().Name}");
        }
    }

    private static void StepCohorts(
        float population, long reviewBirths, long reviewDeaths,
        float youngShare, float oldShare,
        float maturationAnnual, float ageingAnnual, float mortalityOldWeight, float interval,
        out float nextYoungShare, out float nextOldShare, out float nextWorkingShare)
    {
        var stepped = DealOrForce.Rules.DemographyRules.StepCohorts(
            population, reviewBirths, reviewDeaths, youngShare, oldShare,
            maturationAnnual, ageingAnnual, mortalityOldWeight, interval);
        nextYoungShare = stepped.YoungShare;
        nextOldShare = stepped.OldShare;
        nextWorkingShare = stepped.WorkingShare;
    }
}
