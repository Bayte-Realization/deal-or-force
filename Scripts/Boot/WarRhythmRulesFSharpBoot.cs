using System;
using System.Runtime.CompilerServices;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Installs the F# war rules (Scripts/Rules/WarRhythmRules.fs) into WarRhythm: rhythm, fortune, spirit and guerrilla cover.</summary>
internal static class WarRhythmRulesFSharpBoot
{
    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            WarRhythm.Install(DealOrForce.Rules.WarRhythmRules.OffensivePulse, DealOrForce.Rules.WarRhythmRules.BattleFortune);
            WarRhythm.Install(DealOrForce.Rules.WarRhythmRules.FightingSpirit, DealOrForce.Rules.WarRhythmRules.GuerrillaCover);
            WarRhythm.Install(DealOrForce.Rules.WarRhythmRules.DesertionRate);
            DiagnosticLogs.Write("simulation", "RULES_BACKEND fsharp installed=war_rhythm");
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("simulation", $"RULES_BACKEND install_failed=war_rhythm reason={error.GetType().Name}");
        }
    }
}
