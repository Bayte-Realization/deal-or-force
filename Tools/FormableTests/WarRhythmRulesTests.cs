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

        Check(WarRhythm.ReferencePulse(0.6f, 0f, 0f, 0) == 1.6f && WarRhythm.ReferencePulse(0.6f, 0f, 0.8f, 0) == 1f - 0.6f * 0.4f,
            "an offensive season sends 1 + strength forward, a lull 1 - 0.4 strength");
        Check(WarRhythm.ReferenceFortune(0f, 0.9f, 0.9f, 0.22f, 0.9f, 0.9f, 0f, 1f, 0f, 0f) > 1.15f,
            "a battle's own fortune applies with the day's switched off");
    }
}
