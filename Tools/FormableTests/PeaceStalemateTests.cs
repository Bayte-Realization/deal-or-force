using System;
using DealOrForce.Rules;
using DealOrForce.Simulation;

/// <summary>
/// The stalemate discount, managed and F#, compared case by case.
///
/// The rule left C# for F# in this pass: the arithmetic that decides how much a long or stalling
/// war discounts a winner's resolve now lives in `Scripts/Rules/PeaceRules.fs` and is called
/// through `PeaceRulesHook`. A port like that is only worth doing if the two forms cannot drift,
/// so this compares them over a grid of every input that changes the branch - the exact float
/// value, not an approximation.
/// </summary>
static class PeaceStalemateTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run()
    {
        Check(PeaceRulesHook.TryStalemate(400, true, false, 10f, 0.1f, out _),
            "the F# rule is installed as the active stalemate backend");

        int[] durations = { 0, 29, 180, 364, 365, 366, 400, 548, 730, 1095, 1460, 3650, 40000 };
        float[] weariness = { 0f, 0.5f, 12.5f, 33.3f, 50f, 75f, 99.9f, 100f };
        float[] occupied = { 0f, 0.019f, 0.02f, 0.021f, 0.15f, 0.25f, 0.4f, 0.45f, 0.9f, 1f };
        var cases = 0;
        var mismatches = 0;
        var worst = 0f;
        foreach (var duration in durations)
        foreach (var statusQuo in new[] { false, true })
        foreach (var stalled in new[] { false, true })
        foreach (var tired in weariness)
        foreach (var held in occupied)
        {
            cases++;
            var managed = PeaceNegotiation.ManagedStalemateDiscount(duration, statusQuo, stalled, tired, held);
            PeaceRulesHook.TryStalemate(duration, statusQuo, stalled, tired, held, out var rules);
            if (managed != rules)
            {
                mismatches++;
                if (MathF.Abs(managed - rules) > MathF.Abs(worst)) worst = rules - managed;
            }
        }
        Check(mismatches == 0, $"F# and the managed fallback agree exactly on all {cases} cases");
        if (mismatches > 0) Console.WriteLine($"  worst difference: {worst:0.######}");

        // Where the numbers came from: the branches have to do what the sentences say, whatever
        // the two implementations agree on between themselves.
        Check(PeaceRules.StalemateDiscount(100, true, false, 0f, 0f) == 0f,
            "a short quiet war discounts nothing");
        Check(PeaceRules.StalemateDiscount(100, false, true, 0f, 0f) == 0.40f,
            "a stalled front alone buys a discount on a settlement");
        Check(PeaceRules.StalemateDiscount(100, true, true, 0f, 0f) == 0.65f,
            "and more of one for a return to the status quo");
        Check(PeaceRules.StalemateDiscount(100, true, true, 100f, 0f) == 0.90f,
            "weariness cannot discount a demand by more than ninety per cent");
        Check(PeaceRules.StalemateDiscount(100, true, false, 0f, 0.9f) == 0.90f,
            "occupied ground of the responder does the same under a status quo");
        Check(PeaceRules.StalemateDiscount(100, false, false, 0f, 0.9f) == 0f,
            "and changes nothing at all for any other term");
        Check(PeaceRules.StalemateDiscount(1095, true, false, 0f, 0f) >
              PeaceRules.StalemateDiscount(730, true, false, 0f, 0f),
            "a longer war discounts more than a shorter one");
        Check(PeaceRules.StalemateDiscount(-50, true, true, 10f, 0.3f) >= 0f,
            "a negative duration (a war started after today) cannot produce a negative discount");
    }
}
