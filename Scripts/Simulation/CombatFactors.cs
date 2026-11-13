using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>One reason a side's fire or staying power is what it is: a name and a multiplier.</summary>
public readonly record struct BattleFactor(string Key, float Value);

public sealed partial class LandBattle
{
    /// <summary>
    /// Why the attack lands as hard as it does and why the defence holds as well as it does, as
    /// the multipliers the last round actually used (CombatFactors). Written each round for the
    /// report and never read by the simulation; not saved - a loaded battle shows them again
    /// from its next round.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public readonly List<BattleFactor> AttackFactors = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public readonly List<BattleFactor> DefenceFactors = new();
}

/// <summary>
/// The battle explained. Combat multiplies a dozen things together - ground, rivers, frontage,
/// armour against piercing, guns and aircraft overhead, trenches, a commander, a stance, the
/// day's manoeuvre, luck - and a report that shows only who is winning leaves the player to
/// guess which of them did it. Each round this keeps the ones that moved the result by more
/// than a few per cent, in the words a player uses, so the report can say "the river cost the
/// attack a third, the trenches doubled the defence" instead of a bare balance bar.
///
/// The attack side is what multiplies the attacker's fire; the defence side what multiplies the
/// defender's ability to absorb it. Their ratio is the pressure that decides the round.
/// </summary>
public static class CombatFactors
{
    /// <summary>A factor within this much of one is noise, not a reason.</summary>
    public const float Threshold = 0.03f;

    /// <summary>Power-weighted mean of a per-formation multiplier over the line.</summary>
    public static float Mean(IReadOnlyList<Army> line, Func<Army, float> factor)
    {
        double weight = 0, sum = 0;
        foreach (var army in line)
        {
            var w = Math.Max(0.01f, army.Power);
            weight += w;
            sum += w * factor(army);
        }
        return weight <= 0 ? 1f : (float)(sum / weight);
    }

    public static void Add(List<BattleFactor> into, string key, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || MathF.Abs(value - 1f) < Threshold) return;
        into.Add(new BattleFactor(key, value));
    }

    /// <summary>Their product: how much the listed reasons multiply the side's number in all.</summary>
    public static float Product(IReadOnlyList<BattleFactor> factors)
    {
        var product = 1f;
        foreach (var factor in factors) product *= factor.Value;
        return product;
    }
}
