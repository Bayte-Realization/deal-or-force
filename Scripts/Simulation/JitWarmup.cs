using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>
/// Compiles the weekly simulation passes on the load thread, so the player's first weekly
/// day does not pay their JIT all at once.
///
/// The first day-7 frame of a session used to be the worst frame of the early campaign
/// (54 ms against a 1.16 ms median): every weekly pass - subjects, markets, movements,
/// the nuclear posture, the AI's weekly diplomacy - compiled inside that one frame. The
/// load thread already spends seconds on the save and the map; spending a fraction of a
/// second more here moves the same compilation to a moment nobody is playing in.
///
/// Preparation only: RuntimeHelpers.PrepareMethod compiles a method without running it,
/// so nothing in the world is touched, read or rolled. Iterator passes are prepared by
/// compiling their state machine's MoveNext, which is where the pass body actually lives.
/// </summary>
internal static class JitWarmup
{
    private static bool _done;

    internal static void PrepareWeeklyPasses()
    {
        if (_done) return;
        // Attribution lever: BDOF_NO_JIT_WARMUP=1 leaves the first weekly day to compile
        // its passes itself, so the warmup's worth is a measured number, not a claim.
        if (System.Environment.GetEnvironmentVariable("BDOF_NO_JIT_WARMUP") == "1") { _done = true; return; }
        _done = true;
        Prepare(typeof(SubjectSystem), "TickStages");
        Prepare(typeof(SubjectInteractions), "TickSubjectWeek");
        Prepare(typeof(NuclearPosture), "WeekStages");
        Prepare(typeof(MovementRules), "TickStages");
        Prepare(typeof(MovementPower), "TickStages");
        Prepare(typeof(MarketSystem), "TickStages");
        Prepare(typeof(MarketSystem), "RouteOneStages");
        Prepare(typeof(EconomySystem), "TickStages");
        Prepare(typeof(DemographicCycle), "TickStages");
        Prepare(typeof(Warfare), "TickStages");
        Prepare(typeof(Warfare), "ResolveAdvancesStages");
        Prepare(typeof(AiStatecraft), "ThinkTodayStages");
        Prepare(typeof(AiStatecraft), "DiplomacyStages");
        Prepare(typeof(SocialMemory), "TickStages");
        Prepare(typeof(Shipbuilding), "TickStages");
        Prepare(typeof(DefconSystem), "TickStages");
        Prepare(typeof(BrainDrain), "TickStages");
        Prepare(typeof(Consolidation), "TickStages");
        Prepare(typeof(DealOrForce.Rules.AssaultRules), "Assess");
        Prepare(typeof(DealOrForce.Rules.WarRhythmRules), "OffensivePulse");
        Prepare(typeof(DealOrForce.Rules.WarRhythmRules), "BattleFortune");
        Prepare(typeof(DealOrForce.Rules.EconomyRules), "EvaluateCreditTerms");
        Prepare(typeof(DealOrForce.Rules.EconomyRules), "EvaluateTradeCost");
        Prepare(typeof(DealOrForce.Rules.DemographyRules), "StepCohorts");
        Prepare(typeof(DealOrForce.Rules.DiplomacyRules), "EvaluateDeterrence");
        Prepare(typeof(DealOrForce.Rules.CombatTempoRules), "EvaluateSupplyDelivery");
    }

    private static void Prepare(Type type, string methodName)
    {
        try
        {
            var method = type.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (method is not null) RuntimeHelpers.PrepareMethod(method.MethodHandle);
            // The body of an iterator pass lives in its compiler-generated state machine:
            // preparing the entry point alone would leave MoveNext to compile on first use.
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (nested.Name.IndexOf(">" + methodName, StringComparison.Ordinal) < 0) continue;
                var moveNext = nested.GetMethod("MoveNext",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (moveNext is not null) RuntimeHelpers.PrepareMethod(moveNext.MethodHandle);
            }
        }
        catch
        {
            // A renamed pass simply stays cold until its first week; warmup must never
            // be able to fail a load.
        }
    }
}
