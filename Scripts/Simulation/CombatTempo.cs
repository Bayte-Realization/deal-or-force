using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The pressure at which a prepared attack starts to unhinge a defence rather than merely grind it.</summary>
    public float BreakthroughPressureFloor { get; set; } = 1.35f;

    /// <summary>How much more pressure than the floor turns a grinding attack into a breakthrough.</summary>
    public float BreakthroughPressureSpan { get; set; } = 1.75f;

    /// <summary>The planning a formation needs before it can convert local superiority into a breakthrough.</summary>
    public float BreakthroughPlanningFloor { get; set; } = 0.35f;

    /// <summary>The supply a formation needs before it can sustain a breakthrough.</summary>
    public float BreakthroughSupplyFloor { get; set; } = 0.45f;

    /// <summary>Extra organisation damage a breakthrough deals to the defender.</summary>
    public float BreakthroughOrganisationBonus { get; set; } = 0.45f;

    /// <summary>How much a breakthrough spares the attacker the usual cost of assaulting.</summary>
    public float BreakthroughAttackerRelief { get; set; } = 0.18f;

    /// <summary>The pressure at which a prepared defender can turn a spent attack back.</summary>
    public float CounterattackPressureFloor { get; set; } = 1.15f;

    /// <summary>How much more pressure than the floor makes a counterattack decisive.</summary>
    public float CounterattackPressureSpan { get; set; } = 1.5f;

    /// <summary>The entrenchment a defender needs before it can counterattack rather than merely hold.</summary>
    public float CounterattackEntrenchmentFloor { get; set; } = 0.40f;

    /// <summary>The supply a defender needs before it can counterattack rather than merely hold.</summary>
    public float CounterattackSupplyFloor { get; set; } = 0.45f;

    /// <summary>Extra organisation damage a counterattack deals to a spent attacker.</summary>
    public float CounterattackOrganisationBonus { get; set; } = 0.50f;

    /// <summary>How much a counterattack spares the defender's own organisation.</summary>
    public float CounterattackDefenderRelief { get; set; } = 0.20f;

    /// <summary>Multiplier on defence when fighting on the nation's original home provinces.</summary>
    public float HomelandDefenseBonus { get; set; } = 1.25f;

    /// <summary>Additional defence multiplier when defending the national capital state.</summary>
    public float CapitalDefenseBonus { get; set; } = 1.20f;

    /// <summary>Defence multiplier when the enemy attacks from only a single province (frontal bottleneck without flanking).</summary>
    public float FrontalDefenseBonus { get; set; } = 1.25f;

    /// <summary>How much deep entrenchment protects troops from being stripped by artillery/air shelling (0..1).</summary>
    public float EntrenchmentShellingResistance { get; set; } = 0.60f;

    /// <summary>Extra pressure floor required per point of defender entrenchment before breakthrough occurs.</summary>
    public float BreakthroughEntrenchmentResistance { get; set; } = 0.70f;

    /// <summary>Attacker breakthrough multiplier in dense urban terrain.</summary>
    public float UrbanBreakthroughPenalty { get; set; } = 0.50f;

    /// <summary>Weight given to attacker exhaustion and low organisation in triggering counterattacks.</summary>
    public float CounterattackVulnerabilityWeight { get; set; } = 1.50f;

    /// <summary>Bonus to counterattack drive during desperate homeland/capital defence.</summary>
    public float DesperateCounterattackBonus { get; set; } = 0.30f;
}

/// <summary>
/// Operational tempo mechanics: a prepared, supplied attack can break a line instead of only wearing it down,
/// and a dug-in or resilient defender can punish a spent attack with a decisive counter-offensive.
/// </summary>
public static class CombatTempo
{
    public static float Breakthrough(Defines defines, float pressure, float planning, float supply) =>
        Breakthrough(defines, pressure, planning, supply, 0f, 1, 1f);

    public static float Breakthrough(Defines defines, float pressure, float planning, float supply,
        float defenderEntrenchment, int attackDirections, float terrainBreakthroughFactor)
    {
        if (defines is null || !float.IsFinite(pressure) || !float.IsFinite(planning) || !float.IsFinite(supply))
            return 0f;
        if (planning < defines.BreakthroughPlanningFloor || supply < defines.BreakthroughSupplyFloor)
            return 0f;

        // Entrenched positions require significantly higher superiority to break through,
        // but attacking from multiple directions (flanking) partially bypasses that entrenchment.
        var entrenchmentResistance = MathF.Max(0f, defenderEntrenchment * defines.BreakthroughEntrenchmentResistance);
        var flankingReduction = MathF.Max(0, attackDirections - 1) * 0.20f;
        var effectiveFloor = defines.BreakthroughPressureFloor + MathF.Max(0f, entrenchmentResistance - flankingReduction);

        var superiority = Range(pressure, effectiveFloor, defines.BreakthroughPressureSpan);
        var prepared = Ratio(planning, defines.BreakthroughPlanningFloor);
        var supplied = Ratio(supply, defines.BreakthroughSupplyFloor);
        var terrainFactor = float.IsFinite(terrainBreakthroughFactor) && terrainBreakthroughFactor > 0.01f
            ? MathF.Min(1f, terrainBreakthroughFactor) : 1f;

        return M.Clamp01(superiority * prepared * supplied * terrainFactor);
    }

    public static float Counterattack(Defines defines, float pressure, float entrenchment, float supply) =>
        Counterattack(defines, pressure, entrenchment, supply, 1f, 1f, false);

    public static float Counterattack(Defines defines, float pressure, float entrenchment, float supply,
        float attackerOrg, float defenderOrg, bool desperateDefense)
    {
        if (defines is null || !float.IsFinite(pressure) || !float.IsFinite(entrenchment) || !float.IsFinite(supply))
            return 0f;
        if (entrenchment < defines.CounterattackEntrenchmentFloor || supply < defines.CounterattackSupplyFloor)
            return 0f;

        // Two avenues for counterattack:
        // 1. Direct local superiority (existing path: defender firepower beats attacker breakthrough).
        var superiority = Range(pressure, defines.CounterattackPressureFloor, defines.CounterattackPressureSpan);

        // 2. Punitive counterattack against an exhausted/spent attacker:
        // When the attacker's organisation falls while the defender holds discipline and lines,
        // or during desperate last-stand defence of the homeland.
        var vulnerability = 0f;
        if (float.IsFinite(attackerOrg) && float.IsFinite(defenderOrg))
        {
            var orgDelta = defenderOrg - attackerOrg;
            if (attackerOrg < 0.60f && orgDelta > -0.15f)
            {
                var exhaustion = M.Clamp01((0.60f - attackerOrg) / 0.40f);
                var tenacity = M.Clamp01((defenderOrg + 0.15f) / 0.50f);
                vulnerability = exhaustion * tenacity * defines.CounterattackVulnerabilityWeight;
            }
        }
        if (desperateDefense)
        {
            vulnerability += defines.DesperateCounterattackBonus;
        }

        var effectiveDrive = MathF.Max(superiority, M.Clamp01(vulnerability));
        if (effectiveDrive <= 0.001f) return 0f;

        var prepared = Ratio(entrenchment, defines.CounterattackEntrenchmentFloor);
        var supplied = Ratio(supply, defines.CounterattackSupplyFloor);
        return M.Clamp01(effectiveDrive * prepared * supplied);
    }

    private static float Range(float value, float floor, float span) =>
        M.Clamp01((value - floor) / MathF.Max(0.01f, span));

    private static float Ratio(float value, float floor) =>
        M.Clamp01(value / MathF.Max(0.01f, floor));
}
