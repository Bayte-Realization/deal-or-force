using System;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Installs the managed F# rule into the war scoring optional hook.
/// </summary>
internal static class WarScoreRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            WarScoreRulesHook.Install(Evaluate);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=war_score");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation",
                $"RULES_BACKEND install_failed=war_score reason={error.GetType().Name}");
        }
    }

    private static bool Evaluate(
        float occupationScore, float capitalScore, float armyScore,
        float battleScore, float objectiveScore, float tickingScore, float supportScore,
        float defenderOccupied, float defenderStrengthLeft,
        float attackerOccupied, float attackerStrengthLeft,
        float collapseOccupation, float collapseOccupationBroken, float collapseArmy,
        float minScore, float maxScore,
        out int collapsed, out float finalScore)
    {
        var outcome = DealOrForce.Rules.WarScoreRules.Evaluate(
            occupationScore, capitalScore, armyScore,
            battleScore, objectiveScore, tickingScore, supportScore,
            defenderOccupied, defenderStrengthLeft,
            attackerOccupied, attackerStrengthLeft,
            collapseOccupation, collapseOccupationBroken, collapseArmy,
            minScore, maxScore);
        collapsed = outcome.Collapsed;
        finalScore = outcome.FinalScore;
        return true;
    }
}
