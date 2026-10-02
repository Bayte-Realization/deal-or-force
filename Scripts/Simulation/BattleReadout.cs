using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Read-only battle telemetry shared by the map, roster and report.
/// Endurance balance is NOT victory probability: 50% means both sides have the same
/// estimated time before breaking at the recent organisation-loss rate.</summary>
public static class BattleReadout
{
    public readonly record struct Assessment(bool Known, float AttackerShare,
        float AttackerDays, float DefenderDays)
    {
        public float ShareFor(bool attacker) => attacker ? AttackerShare : 1f - AttackerShare;
        public string StatusFor(bool attacker) => !Known ? "Assessing battle" :
            ShareFor(attacker) > .55f ? "Holding the advantage" :
            ShareFor(attacker) < .45f ? "Under pressure" : "Contested battle";
    }

    public static Assessment Assess(LandBattle battle, float breakOrganisation)
    {
        if (battle.Ended)
            return battle.WinnerId < 0 ? new(false, .5f, 0, 0) :
                new(true, battle.WinnerId == battle.AttackerId ? 1 : 0, 0, 0);
        if (battle.Rounds.Count == 0) return new(false, .5f, 0, 0);
        var last = battle.Rounds[^1];
        var attackerRate = 0f;
        var defenderRate = 0f;
        var samples = Math.Min(3, battle.Rounds.Count);
        for (var index = battle.Rounds.Count - samples; index < battle.Rounds.Count; index++)
        {
            var round = battle.Rounds[index];
            // For legacy saves only. Damage names describe fire DEALT, not received.
            attackerRate += Finite(round.HasOrganisationTelemetry ?
                round.AttackerOrganisationSpent : round.DefenderDamage);
            defenderRate += Finite(round.HasOrganisationTelemetry ?
                round.DefenderOrganisationSpent : round.AttackerDamage);
        }
        attackerRate /= samples;
        defenderRate /= samples;
        if (attackerRate <= 0 && defenderRate <= 0) return new(false, .5f, 0, 0);
        var a = Math.Max(0, Unit(last.AttackerOrganisation) - Unit(breakOrganisation)) /
                Math.Max(.0001f, attackerRate);
        var d = Math.Max(0, Unit(last.DefenderOrganisation) - Unit(breakOrganisation)) /
                Math.Max(.0001f, defenderRate);
        // Floating point safety also makes hand-edited saves safe to display.
        a = Math.Min(10000, a);
        d = Math.Min(10000, d);
        return new(true, a + d <= .0001f ? .5f : Unit(a / (a + d)), a, d);
    }

    public readonly record struct Condition(float Organisation, float Equipment, float Strength,
        float Supply, float Cohesion, int Personnel, int Formations);

    public static Condition Summarise(IEnumerable<Army> armies)
    {
        var weight = 0f;
        var org = 0f; var equipment = 0f; var strength = 0f; var supply = 0f; var cohesion = 0f;
        var personnel = 0; var count = 0;
        foreach (var army in armies)
        {
            if (army is null || !float.IsFinite(army.Divisions) || army.Divisions < .1f) continue;
            var w = Math.Max(.01f, Finite(army.Divisions));
            weight += w;
            org += Unit(army.Organisation) * w;
            equipment += Unit(army.Equipment) * w;
            strength += Unit(army.Strength) * w;
            supply += Unit(army.SupplyLevel) * w;
            cohesion += Unit(army.Cohesion) * w;
            personnel += Math.Max(0, army.Personnel);
            count++;
        }
        return weight == 0 ? default :
            new(org / weight, equipment / weight, strength / weight,
                supply / weight, cohesion / weight, personnel, count);
    }

    private static float Finite(float value) => float.IsFinite(value) ? Math.Max(0, value) : 0;
    public static float Unit(float value) => Math.Clamp(Finite(value), 0, 1);
}
