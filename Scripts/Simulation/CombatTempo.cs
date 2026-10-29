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

    /// <summary>Maximum combat effectiveness penalty suffered by an encircled/cut-off formation once supply cutoff matures (HOI4 cauldron penalty).</summary>
    public float EncirclementMaxCombatPenalty { get; set; } = 0.30f;

    /// <summary>Attack bonus per additional province direction beyond the first when enveloping an enemy sector from multiple axes.</summary>
    public float FlankingAttackBonusPerDirection { get; set; } = 0.10f;
}

/// <summary>
/// Operational tempo mechanics: a prepared, supplied attack can break a line instead of only wearing it down,
/// and a dug-in or resilient defender can punish a spent attack with a decisive counter-offensive.
/// </summary>
public static class CombatTempo
{
    /// <summary>
    /// Cauldron / encirclement combat multiplier (0.70..1.00 by default). Formations cut off from national supply
    /// lose operational cohesion as their pocket isolation deepens across <see cref="Defines.SupplyCutoffDays"/>.
    /// </summary>
    public static float EncirclementFactor(Defines defines, bool cutOff, int daysCutOff) =>
        defines is null
            ? 1f
            : DealOrForce.Rules.CombatTempoRules.EncirclementFactor(
                true, cutOff, daysCutOff, defines.EncirclementMaxCombatPenalty, defines.SupplyCutoffDays);

    /// <summary>
    /// Multi-directional envelopment multiplier for attackers striking a single province from multiple adjacent axes.
    /// </summary>
    public static float EnvelopmentFactor(Defines defines, int attackDirections) =>
        defines is null
            ? 1f
            : DealOrForce.Rules.CombatTempoRules.EnvelopmentFactor(
                true, attackDirections, defines.MaxAttackDirections, defines.FlankingAttackBonusPerDirection);

    public static float Breakthrough(Defines defines, float pressure, float planning, float supply) =>
        Breakthrough(defines, pressure, planning, supply, 0f, 1, 1f);

    public static float Breakthrough(Defines defines, float pressure, float planning, float supply,
        float defenderEntrenchment, int attackDirections, float terrainBreakthroughFactor) =>
        defines is null
            ? 0f
            : DealOrForce.Rules.CombatTempoRules.Breakthrough(
                true, pressure, planning, supply, defenderEntrenchment, attackDirections, terrainBreakthroughFactor,
                defines.BreakthroughPlanningFloor, defines.BreakthroughSupplyFloor,
                defines.BreakthroughPressureFloor, defines.BreakthroughPressureSpan,
                defines.BreakthroughEntrenchmentResistance);

    public static float Counterattack(Defines defines, float pressure, float entrenchment, float supply) =>
        Counterattack(defines, pressure, entrenchment, supply, 1f, 1f, false);

    public static float Counterattack(Defines defines, float pressure, float entrenchment, float supply,
        float attackerOrg, float defenderOrg, bool desperateDefense) =>
        defines is null
            ? 0f
            : DealOrForce.Rules.CombatTempoRules.Counterattack(
                true, pressure, entrenchment, supply, attackerOrg, defenderOrg, desperateDefense,
                defines.CounterattackEntrenchmentFloor, defines.CounterattackSupplyFloor,
                defines.CounterattackPressureFloor, defines.CounterattackPressureSpan,
                defines.CounterattackVulnerabilityWeight, defines.DesperateCounterattackBonus);
}
