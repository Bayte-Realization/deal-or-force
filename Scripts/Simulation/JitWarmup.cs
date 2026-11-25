using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DealOrForce.Rules;

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
    private const BindingFlags DeclaredFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    internal static (Type Type, string MethodName)[] GetTargets() => new[]
    {
        (typeof(SubjectSystem), nameof(SubjectSystem.TickStages)), (typeof(SubjectInteractions), nameof(SubjectInteractions.TickSubjectWeek)),
        (typeof(NuclearPosture), nameof(NuclearPosture.WeekStages)), (typeof(MovementRules), nameof(MovementRules.TickStages)),
        (typeof(MovementPower), nameof(MovementPower.TickStages)), (typeof(MarketSystem), nameof(MarketSystem.TickStages)),
        (typeof(TradeNetwork), nameof(TradeNetwork.RouteStages)), (typeof(EconomySystem), nameof(EconomySystem.TickStages)),
        (typeof(DemographicCycle), nameof(DemographicCycle.Stages)), (typeof(Warfare), nameof(Warfare.TickStages)),
        (typeof(Warfare), "ResolveAdvancesStages"), (typeof(AiStatecraft), "ThinkTodayStages"),
        (typeof(AiStatecraft), "DiplomacyStages"), (typeof(SocialMemory), nameof(SocialMemory.TickStages)),
        (typeof(Shipbuilding), nameof(Shipbuilding.TickStages)), (typeof(DefconSystem), nameof(DefconSystem.TickStages)),
        (typeof(Coups), nameof(Coups.TickStages)), (typeof(Coups), nameof(Coups.Chance)),
        (typeof(BrainDrain), nameof(BrainDrain.Stages)), (typeof(Consolidation), nameof(Consolidation.TickStages)),
        (typeof(AssaultRules), nameof(AssaultRules.Assess)), (typeof(WarRhythmRules), nameof(WarRhythmRules.OffensivePulse)),
        (typeof(WarRhythmRules), nameof(WarRhythmRules.BattleFortune)),
        (typeof(EconomyRules), nameof(EconomyRules.EvaluateCreditTerms)), (typeof(EconomyRules), nameof(EconomyRules.EvaluateTradeCost)),
        (typeof(DemographyRules), nameof(DemographyRules.StepCohorts)), (typeof(DiplomacyRules), nameof(DiplomacyRules.EvaluateDeterrence)),
        (typeof(CombatTempoRules), nameof(CombatTempoRules.EvaluateSupplyDelivery)),
    };

    internal static void PrepareWeeklyPasses()
    {
        if (_done) return;
        // Attribution lever: BDOF_NO_JIT_WARMUP=1 leaves the first weekly day to compile
        // its passes itself, so the warmup's worth is a measured number, not a claim.
        if (System.Environment.GetEnvironmentVariable("BDOF_NO_JIT_WARMUP") == "1") { _done = true; return; }
        _done = true;
        foreach (var (type, methodName) in GetTargets()) Prepare(type, methodName);
    }

    internal static (MethodInfo[] Methods, MethodInfo[] MoveNexts) ResolveTargetMethods(Type type, string methodName) =>
    (
        Array.FindAll(type.GetMethods(DeclaredFlags), candidate => candidate.Name == methodName),
        Array.FindAll(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic),
                nested => nested.Name.IndexOf("<" + methodName + ">", StringComparison.Ordinal) >= 0)
            .Select(nested => nested.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(moveNext => moveNext is not null).Cast<MethodInfo>().ToArray()
    );

    private static void Prepare(Type type, string methodName)
    {
        try
        {
            var (methods, moveNexts) = ResolveTargetMethods(type, methodName);
            foreach (var method in methods) RuntimeHelpers.PrepareMethod(method.MethodHandle);
            // The body of an iterator pass lives in its compiler-generated state machine:
            // preparing the entry point alone would leave MoveNext to compile on first use.
            foreach (var moveNext in moveNexts) RuntimeHelpers.PrepareMethod(moveNext.MethodHandle);
        }
        catch { /* A renamed pass simply stays cold until its first week; warmup must never be able to fail a load. */ }
    }
}
