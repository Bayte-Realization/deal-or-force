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

    private static PulseRule _pulse;
    private static FortuneRule _fortune;

    public static void Install(PulseRule pulse, FortuneRule fortune) => (_pulse, _fortune) = (pulse, fortune);
    public static void Uninstall() => (_pulse, _fortune) = (null, null);
    public static bool IsInstalled => _pulse is not null && _fortune is not null;

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
