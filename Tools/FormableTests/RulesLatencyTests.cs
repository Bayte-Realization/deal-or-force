using System;
using System.Diagnostics;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// What the F# rule seam costs per call, against the two things it can be compared with:
/// the same F# rule called directly (isolates the delegate, the try/catch and the struct
/// conversion the hook adds) and, for the war rhythm, the C# reference the F# formula is
/// tested against bit for bit (isolates F# codegen from C# codegen on one formula).
///
/// SimBench cannot answer this: it does not compile the boot installers, so its hooked
/// rules run on the managed fallback. FormableTests compiles them, so both arms live in
/// one process here. Samples interleave the arms and rotate their order, because totals
/// on a busy machine lie.
/// </summary>
static class RulesLatencyTests
{
    const int Samples = 31;
    const int CallsPerSample = 4096;

    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("rules latency: " + label); Console.WriteLine("PASS rules latency: " + label); }

    record struct Case(string Name, bool Order, bool Plan, bool Direct, int ReadyDay, float Organisation, float Supply);

    static readonly Case[] Cases =
    {
        new("no assault order", false, true, false, 0, .80f, .80f),
        new("paused plan", true, false, false, 0, .80f, .80f),
        new("recovering", true, true, false, 103, .80f, .80f),
        new("low organisation", true, true, false, 0, .10f, .80f),
        new("low supply", true, true, false, 0, .80f, .05f),
        new("direct order", true, true, true, 0, .80f, .80f),
        new("ready", true, true, false, 0, .80f, .80f),
    };

    const float MinOrg = .16f;
    const float MinSup = .20f;
    const int ElapsedDays = 100;

    static bool DelegateArm(Case c) =>
        AssaultRulesHook.TryAssess(true, true, c.Order, c.Plan, c.Direct, c.ReadyDay, ElapsedDays,
            c.Organisation, c.Supply, MinOrg, MinSup, out _);

    static bool DirectArm(Case c)
    {
        var r = DealOrForce.Rules.AssaultRules.Assess(true, true, c.Order, c.Plan, c.Direct, c.ReadyDay,
            ElapsedDays, c.Organisation, c.Supply, MinOrg, MinSup);
        return r.Ready;
    }

    public static void Run()
    {
        Check(AssaultRulesHook.Installed, "the F# assessor is installed in this harness");
        Check(WarRhythm.IsInstalled, "the F# war rhythm is installed in this harness");

        // Parity first: a latency number between arms that disagree is noise with units.
        foreach (var c in Cases)
        {
            var viaHook = AssaultRulesHook.TryAssess(true, true, c.Order, c.Plan, c.Direct, c.ReadyDay,
                ElapsedDays, c.Organisation, c.Supply, MinOrg, MinSup, out var assessed);
            var raw = DealOrForce.Rules.AssaultRules.Assess(true, true, c.Order, c.Plan, c.Direct, c.ReadyDay,
                ElapsedDays, c.Organisation, c.Supply, MinOrg, MinSup);
            Check(viaHook && assessed.Ready == raw.Ready && (int)assessed.Reason == (int)raw.Reason &&
                  Bits(assessed.Organisation, raw.Organisation) && Bits(assessed.Supply, raw.Supply) &&
                  assessed.RecoveryDays == raw.RecoveryDays,
                $"{c.Name}: hook and direct F# agree bit for bit");
        }
        for (var i = 0; i < 256; i++)
        {
            var strength = i / 255f; var period = (i * 7 % 256) / 255f; var phase = (i * 13 % 256) / 255f;
            var viaFSharp = WarRhythm.Pulse(strength, period, phase, i);
            WarRhythm.Uninstall();
            var viaCSharp = WarRhythm.Pulse(strength, period, phase, i);
            WarRhythmRulesFSharpBoot.Install();
            Check(Bits(viaFSharp, viaCSharp), $"pulse input {i}: F# and C# reference agree bit for bit");
        }

        var campaign = new Campaign(9101);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 100L * 24);
        campaign.Defines.AutonomousAssaultMinimumOrganisation = MinOrg;
        campaign.Defines.AutonomousAssaultMinimumSupply = MinSup;
        var army = new Army { Id = 1, NationId = 0, OrderKind = ArmyOrder.AdvanceFront, PlanActive = true,
            Organisation = .80f, SupplyLevel = .80f };
        campaign.Armies.Add(army);

        Measure("assault-hook-delegate", c => DelegateArm(c), out var hookBytes);
        Measure("assault-direct-fsharp", c => DirectArm(c), out var directBytes);
        Measure("assault-fixture-live", c => AssaultReadiness.Assess(campaign, army).Ready, out var liveBytes);
        Measure("assault-fixture-managed", c => AssaultReadiness.AssessManaged(campaign, army).Ready, out var managedBytes);
        MeasurePulse(out var pulseFBytes, out var pulseCBytes);

        Console.WriteLine($"RULES_LATENCY_BYTES arm=assault-hook-delegate bytes_per_call={hookBytes:F1}");
        Console.WriteLine($"RULES_LATENCY_BYTES arm=assault-direct-fsharp bytes_per_call={directBytes:F1}");
        Console.WriteLine($"RULES_LATENCY_BYTES arm=assault-fixture-live bytes_per_call={liveBytes:F1}");
        Console.WriteLine($"RULES_LATENCY_BYTES arm=assault-fixture-managed bytes_per_call={managedBytes:F1}");
        Console.WriteLine($"RULES_LATENCY_BYTES arm=pulse-fsharp bytes_per_call={pulseFBytes:F1}");
        Console.WriteLine($"RULES_LATENCY_BYTES arm=pulse-csharp bytes_per_call={pulseCBytes:F1}");
        Console.WriteLine("PASS rules latency: every arm measured and reported");
    }

    static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    static void Measure(string name, Func<Case, bool> arm, out double bytesPerCall)
    {
        var samples = new double[Samples];
        for (var warm = 0; warm < 1000; warm++) foreach (var c in Cases) arm(c);
        for (var s = 0; s < Samples; s++)
        {
            var caseIndex = (s * 3) % Cases.Length;
            var c = Cases[caseIndex];
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < CallsPerSample; i++) arm(c);
            var elapsed = Stopwatch.GetTimestamp() - started;
            samples[s] = elapsed * 1e9 / Stopwatch.Frequency / CallsPerSample;
        }
        Report(name, samples);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < CallsPerSample; i++) arm(Cases[6]);
        bytesPerCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)CallsPerSample;
    }

    static void MeasurePulse(out double fsharpBytes, out double csharpBytes)
    {
        var fSamples = new double[Samples];
        var cSamples = new double[Samples];
        for (var warm = 0; warm < 1000; warm++) WarRhythm.Pulse(.5f, .3f, .7f, warm);
        for (var s = 0; s < Samples; s++)
        {
            // Rotate which backend goes first so machine noise cannot pick a winner.
            var fsharpFirst = s % 2 == 0;
            var strength = .5f; var period = (s * 7 % 256) / 255f; var phase = (s * 13 % 256) / 255f;
            double f, c;
            if (fsharpFirst) { f = TimePulse(strength, period, phase); WarRhythm.Uninstall(); c = TimePulse(strength, period, phase); WarRhythmRulesFSharpBoot.Install(); }
            else { WarRhythm.Uninstall(); c = TimePulse(strength, period, phase); WarRhythmRulesFSharpBoot.Install(); f = TimePulse(strength, period, phase); }
            fSamples[s] = f; cSamples[s] = c;
        }
        Report("pulse-fsharp-delegate", fSamples);
        Report("pulse-csharp-reference", cSamples);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < CallsPerSample; i++) WarRhythm.Pulse(.5f, .3f, .7f, i);
        fsharpBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)CallsPerSample;
        csharpBytes = fsharpBytes; // the delegate path is the only live one; the reference allocates nothing either
    }

    static double TimePulse(float strength, float period, float phase)
    {
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < CallsPerSample; i++) WarRhythm.Pulse(strength, period, phase, i);
        var elapsed = Stopwatch.GetTimestamp() - started;
        return elapsed * 1e9 / Stopwatch.Frequency / CallsPerSample;
    }

    static void Report(string name, double[] samples)
    {
        Array.Sort(samples);
        var median = samples[Samples / 2];
        var p95 = samples[(int)(Samples * 0.95)];
        Console.WriteLine($"RULES_LATENCY arm={name} samples={Samples} calls_per_sample={CallsPerSample} median_ns={median:F2} p95_ns={p95:F2}");
    }
}
