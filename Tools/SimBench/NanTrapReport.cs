using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--nan-trap`: the first stage of the simulation that leaves a number that is not a number.
///
/// The weekly integrity check (NumericIntegrity) says THAT a country's treasury is NaN and stops
/// the campaign, which is right - but by then a week of stages has run over it and seven fields
/// are NaN together, each one the cause of the next. This run asks the question earlier: after
/// every profiled stage it reads a handful of each country's books, and the first time one of
/// them is not finite it names the stage that had just finished, the stage before it, the hour,
/// the country and every non-finite field the country has - then stops.
///
///     SimBench 3725 1201583681 --nan-trap
///
/// Run it with BDOF_NO_PARALLEL=1 so "the stage that had just finished" means one thing. A world
/// nobody plays is not the player's world, so a clean run here is not proof; a caught NaN is.
/// </summary>
internal static class NanTrapReport
{
    private static Campaign _campaign;
    private static string _previous = "(start)";
    private static bool _caught;
    private static long[] _people = Array.Empty<long>();
    private static readonly FieldInfo[] NationNumbers = typeof(Nation)
        .GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(f => f.FieldType == typeof(float) || f.FieldType == typeof(double)).ToArray();

    public static int Run(Campaign c, int days)
    {
        _campaign = c;
        c.IsPaused = false;
        SimProfiler.StageProbe = Probe;
        try
        {
            for (var day = 1; day <= days && !_caught; day++)
            {
                c.TickDay();
                if (day % 365 == 0) Console.WriteLine($"NAN_TRAP day {day}: clean ({c.Nations.Count} nations)");
            }
        }
        catch (Exception exception) when (!_caught)
        {
            Console.WriteLine($"NAN_TRAP stopped by {exception.GetType().Name} on day {c.ElapsedDays}: {exception.Message}");
            return 1;
        }
        finally { SimProfiler.StageProbe = null; }
        if (_caught) return 1;
        Console.WriteLine($"NAN_TRAP clean: {days} days, no non-finite country number");
        return 0;
    }

    /// <summary>
    /// `--diagnose-snapshot <file.diagnostic.json> [days]`: the player's own world, from the
    /// snapshot the game wrote when it stopped. Every non-finite country number is listed, then
    /// set to nought, and the world is run on under the trap - if whatever made the number is
    /// still in that world, it makes it again and the trap names the stage.
    /// </summary>
    public static int RunSnapshot(Campaign fresh, string path, int days)
    {
        var c = SaveSystem.ReadDiagnosticSnapshot(path);
        if (c is null) { Console.WriteLine("NAN_TRAP could not read " + path); return 2; }
        c.AttachProvinceTopology(fresh.ProvinceMap);
        Console.WriteLine($"NAN_TRAP snapshot: day {c.ElapsedDays}, {c.Date.Year}-{c.Date.Month:00}-{c.Date.Day:00}, seed {c.Seed}, {c.Nations.Count} nations");
        foreach (var n in c.Nations)
        {
            var repaired = 0;
            foreach (var field in NationNumbers)
            {
                var value = Convert.ToDouble(field.GetValue(n));
                if (double.IsFinite(value)) continue;
                if (repaired++ == 0)
                    Console.WriteLine($"  country {n.Tag} #{n.Id} \"{n.Name}\" movement={(n.Movement is null ? "no" : n.Movement.Id)} " +
                                      $"regions={c.StatesOf(n).Length} people={n.Population:N0} GDP={n.Gdp:R} debt={n.PublicDebt:R} " +
                                      $"printing={n.MoneyPrinting:R} condition={n.DebtCondition}");
                Console.WriteLine($"    {field.Name} = {value} -> 0");
                if (field.FieldType == typeof(float)) field.SetValue(n, 0f); else field.SetValue(n, 0d);
            }
            if (repaired > 0)
                Console.WriteLine("    PolicyRates: " + string.Join(", ", n.PolicyRates.Select(p => $"{p.Key}={p.Value:R}")));
        }
        Odd(c, "before the run");
        // A region whose head count is already wrecked is put back to a village, so that if
        // whatever wrecked it is still at work in this world it does it again in front of the trap.
        foreach (var region in c.States)
        {
            if (region.Population >= 0 && region.Population < 5_000_000_000) continue;
            Console.WriteLine($"  region {region.Id} \"{region.DisplayName}\": {region.Population:N0} people -> 6,000, {region.PopulationGroups.Count} groups");
            long left = 6000;
            for (var index = 0; index < region.PopulationGroups.Count; index++)
            {
                var share = index == region.PopulationGroups.Count - 1 ? left : Math.Min(left, 6000 / Math.Max(1, region.PopulationGroups.Count));
                region.PopulationGroups[index].Size = share;
                left -= share;
            }
            region.Population = 6000;
        }
        foreach (var n in c.Nations)
        {
            long people = 0;
            foreach (var region in c.StatesOf(n)) people += region.Population;
            if (n.Population < 0 || n.Population > 5_000_000_000) { Console.WriteLine($"  country {n.Tag}: {n.Population:N0} people -> {people:N0}"); n.Population = people; }
        }
        var result = Run(c, days);
        Odd(c, "after the run");
        return result;
    }

    /// <summary>What else in the world is not a number, and which regions hold fewer than no people.</summary>
    private static void Odd(Campaign c, string when)
    {
        foreach (var issue in CampaignIntegrityReport.Numbers(c)) Console.WriteLine($"  NOT FINITE {when}: {issue}");
        var shown = 0;
        foreach (var region in c.States)
            if (!(region.Population >= 0f) && shown++ < 12)
                Console.WriteLine($"  PEOPLE BELOW NOUGHT {when}: region {region.Id} \"{region.DisplayName}\" map index {region.MapIndex} " +
                                  $"people={region.Population:R}");
        if (shown > 12) Console.WriteLine($"  ... and {shown - 12} more regions below nought");
    }

    private static void Probe(string stage)
    {
        if (_caught) return;
        var c = _campaign;
        // A region's people: below nought, or ten times what they were a stage ago and five
        // million more. Nothing that happens in one stage of one day does that to a region.
        var regions = c.States;
        if (_people.Length != regions.Count)
        {
            _people = new long[regions.Count];
            for (var index = 0; index < regions.Count; index++) _people[index] = regions[index].Population;
        }
        for (var index = 0; index < regions.Count; index++)
        {
            var now = regions[index].Population;
            var before = _people[index];
            _people[index] = now;
            if (now >= 0 && (now <= before * 10 || now - before < 5_000_000)) continue;
            _caught = true;
            var region = regions[index];
            Console.WriteLine($"NAN_TRAP people: region {region.Id} \"{region.DisplayName}\" went from {before:N0} to {now:N0} " +
                              $"after stage \"{stage}\" (the stage before it: \"{_previous}\") on day {c.ElapsedDays}, " +
                              $"{c.Date.Year}-{c.Date.Month:00}-{c.Date.Day:00} hour {c.Date.Hour}");
            foreach (var group in region.PopulationGroups)
                Console.WriteLine($"    {group.Culture}/{group.Religion}/{group.Profession}: {group.Size:N0} employment {group.Employment:R}");
            Console.WriteLine(Environment.StackTrace);
            return;
        }
        var nations = c.Nations;
        for (var index = 0; index < nations.Count; index++)
        {
            var n = nations[index];
            if (float.IsFinite(n.Treasury) && float.IsFinite(n.Inflation) && float.IsFinite(n.WeeklyExpenses) &&
                float.IsFinite(n.WeeklyBalance) && float.IsFinite(n.DebtArrears) && float.IsFinite(n.PaymentArrears) &&
                float.IsFinite(n.DebtInterestWeekly) && float.IsFinite(n.Gdp) && float.IsFinite(n.PublicDebt) &&
                float.IsFinite(n.Stability) && float.IsFinite(n.StandardOfLiving) && float.IsFinite(n.WeeklyTaxIncome))
                continue;
            _caught = true;
            Console.WriteLine($"NAN_TRAP caught after stage \"{stage}\" (the stage before it: \"{_previous}\") " +
                              $"on day {c.ElapsedDays}, {c.Date.Year}-{c.Date.Month:00}-{c.Date.Day:00} hour {c.Date.Hour}");
            Console.WriteLine($"  country {n.Tag} #{n.Id} \"{n.Name}\" movement={(n.Movement is null ? "no" : n.Movement.Id)} " +
                              $"regions={c.StatesOf(n).Length} human={n.IsHuman}");
            foreach (var field in NationNumbers)
            {
                var value = Convert.ToDouble(field.GetValue(n));
                if (!double.IsFinite(value)) Console.WriteLine($"  NOT FINITE  {field.Name} = {value}");
            }
            foreach (var field in NationNumbers)
            {
                var value = Convert.ToDouble(field.GetValue(n));
                if (double.IsFinite(value)) Console.WriteLine($"  {field.Name} = {value:R}");
            }
            Console.WriteLine("  PolicyRates: " + string.Join(", ", n.PolicyRates.Select(p => $"{p.Key}={p.Value:R}")));
            Console.WriteLine(Environment.StackTrace);
            return;
        }
        _previous = stage;
    }
}
