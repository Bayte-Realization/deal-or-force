using System;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Installs the F# war rhythm and battle fortune rules (Scripts/Rules/WarRhythmRules.fs) into WarRhythm.</summary>
internal static class WarRhythmRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            WarRhythm.Install(DealOrForce.Rules.WarRhythmRules.OffensivePulse, DealOrForce.Rules.WarRhythmRules.BattleFortune);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=war_rhythm");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation", $"RULES_BACKEND install_failed=war_rhythm reason={error.GetType().Name}");
        }
    }
}
