using System;

namespace DealOrForce.Simulation;

/// <summary>
/// The rhythm and the fortune of war as two pure formulas: when a side throws its weight forward
/// (WarCore.OffensivePulse) and how a battle's luck falls (CombatCore.Fortune). The callers draw
/// the rolls from the campaign seed and hand them in; nothing here reads the campaign.
///
/// The formulas live in F# (Scripts/Rules/WarRhythmRules.fs), installed at start by
/// WarRhythmRulesFSharpBoot. The C# below is the reference they are tested against bit for bit
/// (WarRhythmRulesTests) and the fallback when no rules backend is installed - a tool that does not
/// reference the F# assembly still runs the same war.
/// </summary>
public static class WarRhythm
{
    public delegate float PulseRule(float strength, float periodRoll, float phaseRoll, int daysSinceStart);

    public delegate float FortuneRule(float daySpread, float dayRollA, float dayRollB,
        float battleSpread, float battleRollA, float battleRollB,
        float decisiveChance, float decisiveRoll, float swing, float swingRoll);

    public delegate float SpiritRule(float spread, float rollA, float rollB, float tailRoll,
        float surpriseChance, float collapseChance, float surpriseSwing, float collapseSwing);

    public delegate float CoverRule(float asymmetry, float roughness, float homeGround);

    public delegate float DesertionRule(float warSupport, float cohesion, float supply, float organisation,
        float unpaid, float spirit, float baseRate, float cap);

    private static PulseRule _pulse;
    private static FortuneRule _fortune;
    private static SpiritRule _spirit;
    private static CoverRule _cover;
    private static DesertionRule _desertion;

    public static void Install(PulseRule pulse, FortuneRule fortune) => (_pulse, _fortune) = (pulse, fortune);
    public static void Install(SpiritRule spirit, CoverRule cover) => (_spirit, _cover) = (spirit, cover);
    public static void Install(DesertionRule desertion) => _desertion = desertion;
    public static void Uninstall() => (_pulse, _fortune, _spirit, _cover, _desertion) = (null, null, null, null, null);
    public static bool IsInstalled => _pulse is not null && _fortune is not null && _spirit is not null && _cover is not null &&
                                      _desertion is not null;

    public static float Pulse(float strength, float periodRoll, float phaseRoll, int daysSinceStart) =>
        _pulse?.Invoke(strength, periodRoll, phaseRoll, daysSinceStart) ??
        ReferencePulse(strength, periodRoll, phaseRoll, daysSinceStart);

    public static float Fortune(float daySpread, float dayRollA, float dayRollB,
        float battleSpread, float battleRollA, float battleRollB,
        float decisiveChance, float decisiveRoll, float swing, float swingRoll) =>
        _fortune?.Invoke(daySpread, dayRollA, dayRollB, battleSpread, battleRollA, battleRollB,
            decisiveChance, decisiveRoll, swing, swingRoll) ??
        ReferenceFortune(daySpread, dayRollA, dayRollB, battleSpread, battleRollA, battleRollB,
            decisiveChance, decisiveRoll, swing, swingRoll);

    public static float Spirit(float spread, float rollA, float rollB, float tailRoll,
        float surpriseChance, float collapseChance, float surpriseSwing, float collapseSwing) =>
        _spirit?.Invoke(spread, rollA, rollB, tailRoll, surpriseChance, collapseChance, surpriseSwing, collapseSwing) ??
        ReferenceSpirit(spread, rollA, rollB, tailRoll, surpriseChance, collapseChance, surpriseSwing, collapseSwing);

    public static float Cover(float asymmetry, float roughness, float homeGround) =>
        _cover?.Invoke(asymmetry, roughness, homeGround) ?? ReferenceCover(asymmetry, roughness, homeGround);

    public static float Desertion(float warSupport, float cohesion, float supply, float organisation,
        float unpaid, float spirit, float baseRate, float cap) =>
        _desertion?.Invoke(warSupport, cohesion, supply, organisation, unpaid, spirit, baseRate, cap) ??
        ReferenceDesertion(warSupport, cohesion, supply, organisation, unpaid, spirit, baseRate, cap);

    /// <summary>A formation's weekly desertion: base times the square of the pressure on it, capped.</summary>
    public static float ReferenceDesertion(float warSupport, float cohesion, float supply, float organisation,
        float unpaid, float spirit, float baseRate, float cap)
    {
        var pressure = (1f - Math.Clamp(warSupport, 0f, 1f)) * 0.9f
                       + (1f - Math.Clamp(cohesion, 0f, 1f)) * 0.6f
                       + MathF.Max(0f, 0.5f - Math.Clamp(supply, 0f, 1f)) * 1.6f
                       + MathF.Max(0f, 0.3f - Math.Clamp(organisation, 0f, 1f))
                       + Math.Clamp(unpaid, 0f, 1f) * 0.5f
                       + MathF.Max(0f, 1f - spirit) * 1.5f;
        return MathF.Max(0f, MathF.Min(MathF.Max(0f, cap), MathF.Max(0f, baseRate) * pressure * pressure));
    }

    /// <summary>A side's fighting spirit for a year of a war: 1 ± spread, or far above or below it for a few.</summary>
    public static float ReferenceSpirit(float spread, float rollA, float rollB, float tailRoll,
        float surpriseChance, float collapseChance, float surpriseSwing, float collapseSwing)
    {
        spread = Math.Clamp(spread, 0f, 0.5f);
        var steady = 1f + spread * (rollA + rollB - 1f);
        var surprise = Math.Clamp(surpriseChance, 0f, 1f);
        var collapse = Math.Clamp(collapseChance, 0f, 1f - surprise);
        if (tailRoll < surprise) return steady * (1f + Math.Clamp(surpriseSwing, 0f, 0.9f));
        if (tailRoll >= 1f - collapse) return steady * (1f - Math.Clamp(collapseSwing, 0f, 0.9f));
        return steady;
    }

    /// <summary>How much ground a weak side can hide in: asymmetry times the mean of roughness and home ground.</summary>
    public static float ReferenceCover(float asymmetry, float roughness, float homeGround) =>
        Math.Clamp(asymmetry, 0f, 1f) * (0.5f * Math.Clamp(roughness, 0f, 1f) + 0.5f * Math.Clamp(homeGround, 0f, 1f));

    /// <summary>A side's share forward, as a factor: a third of its 90-180 day cycle at 1 + strength, a quarter at rest.</summary>
    public static float ReferencePulse(float strength, float periodRoll, float phaseRoll, int daysSinceStart)
    {
        if (strength <= 0f) return 1f;
        var period = 90f + 90f * periodRoll;
        var position = daysSinceStart / period + phaseRoll;
        var t = position - MathF.Floor(position);
        if (t < 0.32f) return 1f + strength;
        if (t > 0.75f) return MathF.Max(0.3f, 1f - strength * 0.4f);
        return 1f;
    }

    /// <summary>A side's fortune for a day of battle: the day's, times the battle's, times its decisive moment if it has one.</summary>
    public static float ReferenceFortune(float daySpread, float dayRollA, float dayRollB,
        float battleSpread, float battleRollA, float battleRollB,
        float decisiveChance, float decisiveRoll, float swing, float swingRoll)
    {
        daySpread = Math.Clamp(daySpread, 0f, 0.6f);
        var today = daySpread <= 0f ? 1f : 1f + daySpread * (dayRollA + dayRollB - 1f);
        battleSpread = Math.Clamp(battleSpread, 0f, 0.6f);
        var whole = 1f + battleSpread * (battleRollA + battleRollB - 1f);
        if (decisiveRoll < Math.Clamp(decisiveChance, 0f, 1f))
        {
            swing = Math.Clamp(swing, 0f, 0.9f);
            whole *= swingRoll < 0.5f ? 1f - swing : 1f + swing;
        }
        return today * whole;
    }
}
