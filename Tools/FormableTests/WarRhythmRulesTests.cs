using System;
using DealOrForce.Rules;
using DealOrForce.Simulation;

/// <summary>
/// The F# war rhythm and battle fortune (Scripts/Rules/WarRhythmRules.fs) against the C# reference
/// in WarRhythm, bit for bit, over a grid of inputs - and the installed backend is the F# one.
/// </summary>
static class WarRhythmRulesTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("war rhythm rules: " + name);
        Console.WriteLine("PASS war rhythm rules: " + name);
    }

    public static void Run()
    {
        Check(WarRhythm.IsInstalled, "the F# backend is installed at start");

        float[] rolls = { 0f, 0.001f, 0.25f, 0.4999f, 0.5f, 0.75f, 0.999f };
        float[] strengths = { -0.2f, 0f, 0.3f, 0.6f, 1.2f };
        var pulses = 0;
        foreach (var strength in strengths)
        foreach (var period in rolls)
        foreach (var phase in rolls)
        for (var day = 0; day < 400; day += 7)
        {
            var reference = WarRhythm.ReferencePulse(strength, period, phase, day);
            var fsharp = WarRhythmRules.OffensivePulse(strength, period, phase, day);
            if (BitConverter.SingleToInt32Bits(reference) != BitConverter.SingleToInt32Bits(fsharp))
                throw new Exception($"war rhythm rules: pulse differs at {strength}/{period}/{phase}/{day}: {reference} vs {fsharp}");
            pulses++;
        }
        Check(pulses > 10000, $"offensive pulse: F# and C# agree bit for bit on {pulses:N0} inputs");

        float[] spreads = { -0.1f, 0f, 0.18f, 0.6f, 0.9f };
        float[] chances = { 0f, 0.06f, 1f };
        float[] swings = { 0f, 0.35f, 1.2f };
        var fortunes = 0;
        foreach (var daySpread in spreads)
        foreach (var a in rolls)
        foreach (var b in rolls)
        foreach (var battleSpread in spreads)
        foreach (var c in rolls)
        foreach (var chance in chances)
        foreach (var swing in swings)
        {
            var d = 1f - c;
            var reference = WarRhythm.ReferenceFortune(daySpread, a, b, battleSpread, c, d, chance, a, swing, b);
            var fsharp = WarRhythmRules.BattleFortune(daySpread, a, b, battleSpread, c, d, chance, a, swing, b);
            if (BitConverter.SingleToInt32Bits(reference) != BitConverter.SingleToInt32Bits(fsharp))
                throw new Exception($"war rhythm rules: fortune differs at {daySpread}/{a}/{b}/{battleSpread}/{c}/{chance}/{swing}: {reference} vs {fsharp}");
            fortunes++;
        }
        Check(fortunes > 50000, $"battle fortune: F# and C# agree bit for bit on {fortunes:N0} inputs");

        float[] chances2 = { -0.1f, 0f, 0.06f, 0.5f, 1.2f };
        float[] swings2 = { -0.2f, 0f, 0.35f, 1.5f };
        var spirits = 0;
        foreach (var spread in new[] { -0.1f, 0f, 0.12f, 0.8f })
        foreach (var a in rolls)
        foreach (var b in rolls)
        foreach (var tail in rolls)
        foreach (var surprise in chances2)
        foreach (var collapse in chances2)
        foreach (var swing in swings2)
        {
            var reference = WarRhythm.ReferenceSpirit(spread, a, b, tail, surprise, collapse, swing, 1f - swing);
            var fsharp = WarRhythmRules.FightingSpirit(spread, a, b, tail, surprise, collapse, swing, 1f - swing);
            if (BitConverter.SingleToInt32Bits(reference) != BitConverter.SingleToInt32Bits(fsharp))
                throw new Exception($"war rhythm rules: spirit differs at {spread}/{a}/{b}/{tail}/{surprise}/{collapse}/{swing}: {reference} vs {fsharp}");
            spirits++;
        }
        Check(spirits > 50000, $"fighting spirit: F# and C# agree bit for bit on {spirits:N0} inputs");
        var covers = 0;
        float[] unit = { -0.5f, 0f, 0.2f, 0.5f, 0.8f, 1f, 1.7f };
        foreach (var asymmetry in unit)
        foreach (var rough in unit)
        foreach (var home in unit)
        {
            var reference = WarRhythm.ReferenceCover(asymmetry, rough, home);
            var fsharp = WarRhythmRules.GuerrillaCover(asymmetry, rough, home);
            if (BitConverter.SingleToInt32Bits(reference) != BitConverter.SingleToInt32Bits(fsharp))
                throw new Exception($"war rhythm rules: cover differs at {asymmetry}/{rough}/{home}: {reference} vs {fsharp}");
            covers++;
        }
        Check(covers == 343, "guerrilla cover: F# and C# agree bit for bit on every input");
        Check(WarRhythm.ReferenceSpirit(0f, .5f, .5f, 0.01f, 0.06f, 0.03f, 0.35f, 0.4f) == 1.35f &&
              WarRhythm.ReferenceSpirit(0f, .5f, .5f, 0.99f, 0.06f, 0.03f, 0.35f, 0.4f) == 0.6f &&
              WarRhythm.ReferenceSpirit(0f, .5f, .5f, 0.5f, 0.06f, 0.03f, 0.35f, 0.4f) == 1f,
            "a few side-years surprise, a few break, and the rest fight as their numbers say");
        Check(WarRhythm.ReferenceCover(0f, 1f, 1f) == 0f && WarRhythm.ReferenceCover(1f, 1f, 1f) == 1f &&
              WarRhythm.ReferenceCover(1f, 0f, 1f) == 0.5f, "an even war gives no cover; a lopsided one on rough home ground all of it");

        var desertions = 0;
        float[] levels = { -0.2f, 0f, 0.15f, 0.4f, 0.7f, 1f, 1.3f };
        foreach (var support in levels)
        foreach (var cohesion in levels)
        foreach (var supply in levels)
        foreach (var organisation in new[] { 0f, 0.2f, 0.6f })
        foreach (var unpaid in new[] { 0f, 1f })
        foreach (var spirit in new[] { 0.6f, 1f, 1.35f })
        foreach (var cap in new[] { 0f, 0.03f, 1f })
        {
            var reference = WarRhythm.ReferenceDesertion(support, cohesion, supply, organisation, unpaid, spirit, 0.002f, cap);
            var fsharp = WarRhythmRules.DesertionRate(support, cohesion, supply, organisation, unpaid, spirit, 0.002f, cap);
            if (BitConverter.SingleToInt32Bits(reference) != BitConverter.SingleToInt32Bits(fsharp))
                throw new Exception($"war rhythm rules: desertion differs at {support}/{cohesion}/{supply}/{organisation}/{unpaid}/{spirit}/{cap}: {reference} vs {fsharp}");
            desertions++;
        }
        Check(desertions > 10000, $"desertion: F# and C# agree bit for bit on {desertions:N0} inputs");
        Check(WarRhythm.ReferenceDesertion(1f, 1f, 1f, 1f, 0f, 1.2f, 0.002f, 0.03f) == 0f &&
              WarRhythm.ReferenceDesertion(0.1f, 0.2f, 0.1f, 0.1f, 1f, 0.6f, 0.002f, 0.03f) > 0.02f &&
              WarRhythm.ReferenceDesertion(0f, 0f, 0f, 0f, 1f, 0.2f, 0.002f, 0.03f) == 0.03f,
            "nobody leaves a fed army in a popular war; a starving, unpaid, beaten one loses two or three in a hundred a week");

        Check(WarRhythm.ReferencePulse(0.6f, 0f, 0f, 0) == 1.6f && WarRhythm.ReferencePulse(0.6f, 0f, 0.8f, 0) == 1f - 0.6f * 0.4f,
            "an offensive season sends 1 + strength forward, a lull 1 - 0.4 strength");
        Check(WarRhythm.ReferenceFortune(0f, 0.9f, 0.9f, 0.22f, 0.9f, 0.9f, 0f, 1f, 0f, 0f) > 1.15f,
            "a battle's own fortune applies with the day's switched off");
    }
}
