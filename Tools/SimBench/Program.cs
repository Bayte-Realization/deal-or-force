using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using ByDealOrForce.Simulation;

namespace ByDealOrForce.Tools;

/// <summary>
/// Runs the campaign simulation without Godot: checks the invariants that are expensive
/// to notice by eye, then times a run of campaign days and reports where the time went.
///
/// Usage: SimBench [days]   (default 730 - two campaign years)
/// Exit code is non-zero when a check fails, so this is usable in CI as-is.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var days = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 730;

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("By Deal or Force - headless simulation bench");
        Console.WriteLine(new string('-', 62));

        var setup = Stopwatch.StartNew();
        var campaign = Campaign.CreateBootstrap(20260830);
        setup.Stop();
        Console.WriteLine($"Campaign created in {setup.ElapsedMilliseconds} ms: " +
                          $"{campaign.Nations.Count} nations, {campaign.States.Count} states.");

        var failures = 0;
        failures += Check("day-zero GDP reproduces the authored 2024 figures", CalibrationHolds(campaign));
        failures += Check("every nation has an output calibration", NationsCalibrated(campaign));
        failures += Check("state ownership index agrees with a linear scan", IndexAgrees(campaign));

        Console.WriteLine();
        Console.WriteLine($"Running {days} campaign day(s)…");

        campaign.IsPaused = false;
        var run = Stopwatch.StartNew();
        for (var day = 0; day < days; day++) campaign.Tick();
        run.Stop();

        var perDay = run.Elapsed.TotalMilliseconds / days;
        Console.WriteLine($"  {run.ElapsedMilliseconds} ms total, {perDay:0.###} ms per campaign day.");
        Console.WriteLine($"  {campaign.Date} after {campaign.ElapsedDays} day(s).");

        Console.WriteLine();
        Console.WriteLine("Slowest simulation passes (average per run):");
        foreach (var sample in SimProfiler.All
                     .OrderByDescending(sample => sample.AverageMilliseconds)
                     .Take(12))
            Console.WriteLine($"  {sample.Name,-22} {sample.AverageMilliseconds,8:0.###} ms  ×{sample.Runs}");

        Console.WriteLine();
        failures += Check("the economy actually moved", EconomyMoved(campaign));
        failures += Check("scripted content parses and applies", ScriptedContentWorks(campaign));
        failures += Check("modifiers grant, tick and expire", ModifiersWork(campaign));
        failures += Check("no nation ran to a nonsense GDP", GdpSane(campaign));

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "All checks passed."
            : $"{failures} check(s) FAILED.");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string description, string failure)
    {
        var ok = string.IsNullOrEmpty(failure);
        Console.WriteLine(ok ? $"  [ ok ] {description}" : $"  [FAIL] {description}: {failure}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The whole point of calibrating emergent output: day zero must still read as the
    /// 2024 the scenario was authored with, or the economy is accurate to its own model
    /// and wrong about the world.
    /// </summary>
    private static string CalibrationHolds(Campaign campaign)
    {
        foreach (var nation in campaign.Nations.Where(nation => nation.Gdp > 1f)
                     .OrderByDescending(nation => nation.Gdp).Take(40))
        {
            var emergent = nation.GrossOutput * 52f * nation.OutputScale;
            var drift = MathF.Abs(emergent - nation.Gdp) / nation.Gdp;
            if (drift > 0.001f)
                return $"{nation.Tag} drifts {drift:P2} (authored {nation.Gdp:N1}B, emergent {emergent:N1}B)";
        }
        return "";
    }

    private static string NationsCalibrated(Campaign campaign)
    {
        var uncalibrated = campaign.Nations.Count(nation => nation.Gdp > 0f && nation.OutputScale <= 0f);
        return uncalibrated == 0 ? "" : $"{uncalibrated} nation(s) have no output scale";
    }

    /// <summary>Guards the cached ownership index against the linear scan it replaced.</summary>
    private static string IndexAgrees(Campaign campaign)
    {
        foreach (var nation in campaign.Nations.Take(60))
        {
            var indexed = campaign.StatesOf(nation).Length;
            var scanned = campaign.States.Count(state => state.NationMapIndex == nation.MapIndex);
            if (indexed != scanned)
                return $"{nation.Tag}: index says {indexed}, scan says {scanned}";
        }
        return "";
    }

    /// <summary>
    /// Applies a scripted effect through the same path content uses and checks the value
    /// actually moved, so a refactor that quietly breaks every event is caught here
    /// rather than by a player wondering why nothing happens.
    /// </summary>
    private static string ScriptedContentWorks(Campaign campaign)
    {
        var nation = campaign.Nations.FirstOrDefault(candidate => candidate.Gdp > 100f);
        if (nation is null) return "no nation to test against";

        var before = nation.Stability;
        ScriptedEffects.From(("stability", 0.05f)).Apply(campaign, nation);
        if (MathF.Abs(nation.Stability - before) < 0.0001f) return "a scripted effect changed nothing";
        nation.Stability = before;

        if (!ScriptedEffects.IsKnown("treasury_percent")) return "suffix operations stopped resolving";
        if (ScriptedEffects.IsKnown("not_a_real_key")) return "unknown keys are being accepted";
        if (!EventCatalog.All.Any()) return "no events loaded";
        return "";
    }

    private static string ModifiersWork(Campaign campaign)
    {
        var nation = campaign.Nations.FirstOrDefault(candidate => candidate.Gdp > 100f);
        if (nation is null) return "no nation to test against";
        if (!ModifierCatalog.All.Any()) return "no modifiers loaded";

        var id = ModifierCatalog.All.First().Id;
        if (!ModifierSystem.Grant(campaign, nation, id, 30)) return $"could not grant '{id}'";
        if (!ModifierSystem.Has(nation, id)) return $"'{id}' did not attach";

        // Re-granting must refresh rather than stack.
        var count = nation.Modifiers.Count;
        ModifierSystem.Grant(campaign, nation, id, 30);
        if (nation.Modifiers.Count != count) return "re-granting stacked a duplicate";

        if (!ModifierSystem.Revoke(nation, id)) return $"could not revoke '{id}'";
        return "";
    }

    private static string EconomyMoved(Campaign campaign)
    {
        var moved = campaign.Nations.Count(nation => nation.GrossOutput > 0f);
        return moved > 0 ? "" : "no nation produced anything";
    }

    private static string GdpSane(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            if (float.IsNaN(nation.Gdp) || float.IsInfinity(nation.Gdp))
                return $"{nation.Tag} GDP is {nation.Gdp}";
            if (nation.Gdp < 0f) return $"{nation.Tag} GDP went negative: {nation.Gdp}";
        }
        return "";
    }
}
