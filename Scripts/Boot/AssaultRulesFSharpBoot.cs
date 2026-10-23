using System;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Installs the managed F# rule into the simulation's optional hook.
///
/// This file is game boot code, not simulation: the headless tools compile
/// Scripts/Simulation alone and keep the managed fallback. A module initializer means the
/// rule is present before the first campaign tick, without a settings screen or a static
/// call order to remember. The assignment is wrapped because a damaged export must degrade
/// to the C# rule, not fail to load the game assembly.
/// </summary>
internal static class AssaultRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            AssaultRulesHook.Install(Assess);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=assault_readiness");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation",
                $"RULES_BACKEND install_failed=assault_readiness reason={error.GetType().Name}");
        }
    }

    private static bool Assess(bool hasCampaign, bool hasArmy, bool hasAssaultOrder,
        bool planActive, bool directOrder, int readyDay, int elapsedDays,
        float organisation, float supply, float minimumOrganisation, float minimumSupply,
        out AssaultAssessment assessment)
    {
        var result = DealOrForce.Rules.AssaultRules.Assess(
            hasCampaign, hasArmy, hasAssaultOrder, planActive, directOrder,
            readyDay, elapsedDays, organisation, supply, minimumOrganisation, minimumSupply);
        assessment = new AssaultAssessment(result.Ready,
            (AssaultReadiness.Reason)(int)result.Reason,
            result.Organisation, result.MinimumOrganisation,
            result.Supply, result.MinimumSupply, result.RecoveryDays);
        return true;
    }
}
