using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// The day's manoeuvre: what each commander did with the morning.
///
/// A battle day is already a real thing here - frontage, hardness, planning, fortune -
/// but both sides always fought the same fight. A tactic is the day's answer to that:
/// each side's staff picks one manoeuvre from its book, weighted by what its army is
/// (armour picks blitzes, dug-in infantry picks delays), and the pair of them scales the
/// day's arithmetic. Four manoeuvres form a counter-cycle - assault beats ambush, ambush
/// beats blitz, blitz beats delay, delay beats assault - so a staff that reads its enemy
/// (or rolls well) fights its own fight on the enemy's terms.
///
/// The pick is drawn from the campaign seed, the battle, the side and the day, the same
/// way <see cref="Warfare"/>'s fortune is: a seed replays its war exactly, and no random
/// state advances, so isolation holds to the bit. With the define off every multiplier
/// is exactly one and the arithmetic is the old arithmetic.
/// </summary>
public sealed record CombatTactic(string Id, string Name, string Description, float Attack, float Defence,
    float OrgDamage, float EnemyAttack, float EnemyDefence, string CounterId, float BaseWeight)
{
    public static readonly CombatTactic Skirmish = new("skirmish", "Meeting engagement",
        "Both sides push patrols forward and shoot it out at contact. Nobody's plan is better than nobody's plan.",
        1f, 1f, 1f, 1f, 1f, null, 1.4f);
    public static readonly CombatTactic Assault = new("assault", "Relentless assault",
        "Everyone walks behind the barrage. It costs; it also ends arguments.",
        1.16f, 0.88f, 1.05f, 1f, 1f, "ambush", 0.8f);
    public static readonly CombatTactic Blitz = new("blitz", "Blitz",
        "The armoured fist at one seam, everything else noise. The seam or nothing.",
        1.24f, 0.80f, 1.08f, 1f, 1f, "delay", 0.6f);
    public static readonly CombatTactic Delay = new("delay", "Delay",
        "Trade a mile for a morning and a morning for a regiment. The line bends; it does not break.",
        0.84f, 1.20f, 0.90f, 1f, 1f, "assault", 0.7f);
    public static readonly CombatTactic Ambush = new("ambush", "Ambush",
        "Let them take the road, then take the road back. Ground that helps hiding helps this.",
        0.95f, 1.14f, 1.16f, 1f, 1f, "blitz", 0.5f);
    public static readonly CombatTactic Feint = new("feint", "Feint",
        "Commit to nothing convincingly. The enemy fires at where the attack is not.",
        0.94f, 1f, 0.95f, 0.90f, 1f, null, 0.5f);
    public static readonly CombatTactic Hold = new("hold", "Hold at all costs",
        "Nobody moves a step. Behind this ground there is no ground left worth naming.",
        0.70f, 1.30f, 0.88f, 1f, 1f, null, 0.4f);
    public static readonly CombatTactic Encirclement = new("encirclement", "Encirclement",
        "Two axes and a closing hand. The defender's front is suddenly twice as long as the ground.",
        1.05f, 1f, 1.10f, 1f, 0.92f, null, 0.8f);
    public static readonly CombatTactic Withdrawal = new("withdrawal", "Planned withdrawal",
        "Fall back to the next line on the schedule, not under fire. Mornings are cheaper than afternoons.",
        0.68f, 1.24f, 0.78f, 1f, 1f, null, 0.25f);
    public static readonly CombatTactic BreakthroughTactic = new("breakthrough_tactic", "Breakthrough",
        "Concentrated armour and storm regiments punch straight through the forward defensive belt into the artillery line.",
        1.28f, 0.85f, 1.15f, 1f, 0.90f, "elastic_defense", 0.5f);
    public static readonly CombatTactic ElasticDefense = new("elastic_defense", "Elastic defence",
        "Yield the front trench to the barrage, then catch the advancing columns in crossfire from the second belt.",
        0.92f, 1.26f, 1.08f, 0.88f, 1f, "shock_assault", 0.55f);
    public static readonly CombatTactic ShockAssault = new("shock_assault", "Shock assault",
        "Close to grenade and point-blank range behind a creeping barrage before the defenders can man their parapets.",
        1.22f, 0.90f, 1.14f, 1f, 1f, "tactical_counterattack", 0.65f);
    public static readonly CombatTactic TacticalCounterattack = new("tactical_counterattack", "Tactical counter-attack",
        "Local reserves strike the flanks of the attacking wedges the moment their momentum stalls.",
        1.14f, 1.16f, 1.18f, 0.92f, 1f, "breakthrough_tactic", 0.5f);
    public static readonly CombatTactic SuppressiveBarrage = new("suppressive_barrage", "Suppressive barrage",
        "Pin the enemy in their dugouts with overwhelming shell weight while infantry methodically clear strongpoints.",
        1.14f, 1.05f, 1.08f, 0.90f, 0.95f, "infiltration_assault", 0.65f);
    public static readonly CombatTactic InfiltrationAssault = new("infiltration_assault", "Infiltration assault",
        "Small storm detachments slip between strongpoints at night or through cover, unhinging the line from within.",
        1.16f, 1.08f, 1.14f, 0.92f, 0.92f, "hold", 0.55f);

    public bool Counters(CombatTactic other) =>
        CounterId is not null && other is not null && string.Equals(CounterId, other.Id, StringComparison.Ordinal);
}

public static class CombatTactics
{
    public static readonly CombatTactic[] All =
        { CombatTactic.Skirmish, CombatTactic.Assault, CombatTactic.Blitz, CombatTactic.Delay,
            CombatTactic.Ambush, CombatTactic.Feint, CombatTactic.Hold, CombatTactic.Encirclement,
            CombatTactic.Withdrawal, CombatTactic.BreakthroughTactic, CombatTactic.ElasticDefense,
            CombatTactic.ShockAssault, CombatTactic.TacticalCounterattack,
            CombatTactic.SuppressiveBarrage, CombatTactic.InfiltrationAssault };

    // The scratch of the weighted draw, reused by every battle day.
    private static readonly float[] Cumulative = new float[All.Length];

    /// <summary>
    /// How much each manoeuvre recommends itself to this staff today. A pure function of
    /// what the army is made of and where it stands: armour, entrenchment, remaining
    /// organisation, the number of axes the attack comes on, the ground, the plan.
    /// </summary>
    public static float[] Weights(bool attacker, float armour, float entrenchment, float organisation,
        int directions, string terrainId, float planning)
    {
        var weights = new float[All.Length];
        var concealing = terrainId is "forest" or "marsh" or "jungle" or "mountains" or "hills";
        for (var index = 0; index < All.Length; index++)
        {
            var tactic = All[index];
            var weight = tactic.BaseWeight;
            switch (tactic.Id)
            {
                case "assault":
                    weight += armour * 3f;
                    break;
                case "blitz":
                    if (armour < 0.45f) weight = 0f;         // no fist, no blitz
                    else weight += armour * 3.5f;
                    break;
                case "delay":
                    weight += entrenchment * 2.2f;
                    if (attacker) weight *= 0.5f;            // a delay is what a defence does
                    break;
                case "ambush":
                    if (concealing) weight += 1.1f;
                    break;
                case "feint":
                    weight += MathF.Max(0f, planning) * 2f;
                    break;
                case "hold":
                    if (entrenchment > 0.3f) weight += entrenchment * 1.8f;
                    if (attacker) weight *= 0.25f;
                    break;
                case "encirclement":
                    weight = attacker && directions >= 2 ? weight + 0.6f : 0f;
                    break;
                case "withdrawal":
                    if (organisation < 0.45f) weight += 1.55f;
                    break;
                case "breakthrough_tactic":
                    if (!attacker || armour < 0.35f) weight = 0f;
                    else weight += armour * 2.8f + MathF.Max(0f, planning) * 1.4f;
                    break;
                case "elastic_defense":
                    if (attacker || entrenchment < 0.25f) weight = 0f;
                    else weight += entrenchment * 2.0f + (organisation > 0.5f ? 0.6f : 0f);
                    break;
                case "shock_assault":
                    if (!attacker) weight *= 0.25f;
                    else weight += MathF.Max(0f, planning) * 1.8f + (directions >= 2 ? 0.5f : 0f);
                    break;
                case "tactical_counterattack":
                    if (attacker) weight = 0f;
                    else if (organisation > 0.55f && entrenchment > 0.2f) weight += entrenchment * 1.6f + organisation * 0.8f;
                    else weight *= 0.2f;
                    break;
                case "suppressive_barrage":
                    weight += MathF.Max(0f, planning) * 1.5f + (attacker ? 0.4f : entrenchment * 0.8f);
                    break;
                case "infiltration_assault":
                    if (concealing) weight += 1.2f;
                    if (attacker) weight += MathF.Max(0f, planning) * 1.2f;
                    else weight *= 0.4f;
                    break;
            }
            weights[index] = MathF.Max(0f, weight);
        }
        return weights;
    }

    /// <summary>
    /// The day's manoeuvre, drawn from the seed, the battle, the side and the day - never
    /// from random state - so a replayed campaign picks the same pair on the same morning.
    /// Null when tactics are off or the book holds no weight at all.
    /// </summary>
    public static CombatTactic Pick(Campaign campaign, int battleId, bool attacker, int day,
        float armour, float entrenchment, float organisation, int directions, string terrainId, float planning,
        Nation nation = null, float staffSkill = 0f, float recon = 0f)
    {
        if (campaign is null || !campaign.Defines.CombatTacticsEnabled) return null;
        var weights = Weights(attacker, armour, entrenchment, organisation, directions, terrainId, planning);
        if (nation is not null || staffSkill > 0f || recon > 0f)
            ApplyDoctrineAndInitiative(weights, attacker, nation, staffSkill, recon);
        return Pick(campaign.Seed, battleId, attacker, day, weights);
    }

    /// <summary>
    /// HOI4-inspired doctrine &amp; reconnaissance initiative: a general staff's tactical doctrine,
    /// commander staff skill, and division reconnaissance weight the staff toward its signature
    /// manoeuvres (blitz/breakthrough for Manoeuvre, suppressive barrage for Fire, elastic defense
    /// for Fortress, shock assault for Shock, infiltration assault for Infiltration).
    /// </summary>
    public static void ApplyDoctrineAndInitiative(float[] weights, bool attacker, Nation nation,
        float staffSkill, float recon)
    {
        if (weights is null || weights.Length != All.Length) return;
        var initiative = 1f + Math.Clamp(staffSkill * 0.35f + recon * 0.45f, 0f, 0.65f);
        var groundBook = nation is null ? "" : TacticalDoctrines.BookOn(nation, DoctrineSlots.Ground)?.Id ?? "";
        for (var i = 0; i < All.Length; i++)
        {
            if (weights[i] <= 0f) continue;
            var id = All[i].Id;
            if (id != "skirmish") weights[i] *= initiative;
            switch (groundBook)
            {
                case "manoeuvre" when id is "blitz" or "breakthrough_tactic" or "encirclement":
                    weights[i] *= 1.35f;
                    break;
                case "fire" when id is "suppressive_barrage" or "assault":
                    weights[i] *= 1.35f;
                    break;
                case "fortress" when id is "elastic_defense" or "hold" or "delay" or "tactical_counterattack":
                    weights[i] *= 1.35f;
                    break;
                case "shock" when id is "shock_assault" or "breakthrough_tactic":
                    weights[i] *= 1.35f;
                    break;
                case "infiltration" when id is "infiltration_assault" or "ambush":
                    weights[i] *= 1.35f;
                    break;
                case "levy" when id is "assault" or "tactical_counterattack":
                    weights[i] *= 1.25f;
                    break;
            }
        }
    }

    public static CombatTactic Pick(int seed, int battleId, bool attacker, int day, float[] weights)
    {
        if (weights is null || weights.Length != All.Length) return null;
        var total = 0f;
        for (var index = 0; index < weights.Length; index++)
        {
            if (weights[index] <= 0f || !float.IsFinite(weights[index])) weights[index] = 0f;
            total += weights[index];
            Cumulative[index] = total;
        }
        if (total <= 0f) return null;
        var draw = Escalation.Roll(seed, battleId, attacker ? 3 : 4, day, 0x7A1) * total;
        for (var index = 0; index < All.Length; index++)
            if (draw < Cumulative[index]) return All[index];
        return All[^1];
    }

    public static CombatTactic Find(string id) =>
        id is null ? null : Array.Find(All, tactic => string.Equals(tactic.Id, id, StringComparison.Ordinal));

    /// <summary>The day's fire, on the scale of the manoeuvre and the counter-manoeuvre.</summary>
    public static float Attack(CombatTactic own, CombatTactic enemy, Defines defines) =>
        own is null ? 1f : own.Attack * (own.Counters(enemy) ? CounterBonus(defines) : 1f);

    /// <summary>The day's staying power.</summary>
    public static float Defence(CombatTactic own) => own is null ? 1f : own.Defence;

    /// <summary>The organisation damage this side deals today.</summary>
    public static float OrgDamage(CombatTactic own) => own is null ? 1f : own.OrgDamage;

    /// <summary>What the enemy's manoeuvre does to this side's fire: a feint fires at nothing.</summary>
    public static float EnemyAttack(CombatTactic enemy) => enemy is null ? 1f : enemy.EnemyAttack;

    /// <summary>What the enemy's manoeuvre does to this side's footing: an encirclement stretches it.</summary>
    public static float EnemyDefence(CombatTactic enemy) => enemy is null ? 1f : enemy.EnemyDefence;

    private static float CounterBonus(Defines defines) =>
        defines is null ? 1.15f : MathF.Max(1f, defines.CombatCounterTacticBonus);
}

public sealed partial class Defines
{
    /// <summary>
    /// Whether battle days are fought as a pair of manoeuvres (CombatTactics). Off, the
    /// arithmetic is exactly the old arithmetic: every multiplier is one.
    /// </summary>
    public bool CombatTacticsEnabled { get; set; } = true;

    /// <summary>What countering the enemy's manoeuvre is worth to the day's fire.</summary>
    public float CombatCounterTacticBonus { get; set; } = 1.15f;
}
