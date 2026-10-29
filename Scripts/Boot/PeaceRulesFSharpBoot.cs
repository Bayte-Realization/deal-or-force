using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Installs the managed F# rule into the peace graph / boundary arbitration optional hook.
/// </summary>
internal static class PeaceRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            PeaceRulesHook.Install(Leaf, Parts);
            PeaceRulesHook.InstallStalemate(Stalemate);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=peace_rules");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation",
                $"RULES_BACKEND install_failed=peace_rules reason={error.GetType().Name}");
        }
    }

    private static bool Leaf(Func<int, IEnumerable<int>> adjacency, ISet<int> chosen, ISet<int> anchors, int region)
    {
        return DealOrForce.Rules.PeaceRules.IsLeaf(adjacency, chosen, anchors, region);
    }

    private static int[][] Parts(Func<int, IEnumerable<int>> adjacency, IEnumerable<int> nodes)
    {
        return DealOrForce.Rules.PeaceRules.FindParts(adjacency, nodes);
    }

    private static float Stalemate(int durationDays, bool statusQuo, bool stalled,
        float weariness, float responderOccupied)
    {
        return DealOrForce.Rules.PeaceRules.StalemateDiscount(durationDays, statusQuo, stalled,
            weariness, responderOccupied);
    }
}
