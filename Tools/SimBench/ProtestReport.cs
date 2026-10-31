using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--protests`: does the street ever fill, and where. Once a year: the countries with the
/// fullest streets and why (grievance, cut, inflation, stability), how many waves began, and the
/// stability of the rich democracies that used to be the one place nothing happened.
/// </summary>
internal static class ProtestReport
{
    static readonly string[] Watched = { "FRA", "POL", "DEU", "USA", "GBR", "ITA", "ESP", "JPN" };

    /// <summary>`--traits`: which movements took on or shed which traits, and when.</summary>
    public static int Traits(Campaign c, int days)
    {
        c.IsPaused = false;
        // Read as the days pass: the chronicle keeps only its last 2048 entries.
        var changes = new List<ChronicleEntry>();
        var seen = c.Chronicle.Entries.Count == 0 ? 0L : c.Chronicle.Entries[^1].Sequence;
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            foreach (var e in c.Chronicle.Entries)
            {
                if (e.Sequence <= seen) continue;
                seen = e.Sequence;
                if (e.Kind is "movement_trait_gained" or "movement_trait_lost") changes.Add(e);
            }
        }
        foreach (var e in changes)
            Console.WriteLine($"TRAIT day {e.Day} {c.NationById(e.ActorId)?.Tag} {(e.Kind.EndsWith("gained") ? "+" : "-")}{e.Subject}");
        Console.WriteLine($"TRAITS changes={changes.Count} movements={changes.Select(e => e.ActorId).Distinct().Count()}");
        return 0;
    }

    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        MassProtests.Tally.Clear();
        var waves = new Dictionary<string, int>();
        var seen = c.Chronicle.Entries.Count == 0 ? 0L : c.Chronicle.Entries[^1].Sequence;
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            foreach (var e in c.Chronicle.Entries)
            {
                if (e.Sequence <= seen) continue;
                seen = e.Sequence;
                if (e.Kind != "mass_protests") continue;
                var tag = c.NationById(e.ActorId)?.Tag ?? "?";
                waves[tag] = waves.GetValueOrDefault(tag) + 1;
            }
            if (day % 365 != 0 && day != days) continue;
            var hot = c.Nations.Where(n => n.Population > 0 && n.DomesticCrisis.ProtestIntensity > 0.05f)
                .OrderByDescending(n => n.DomesticCrisis.ProtestIntensity).Take(12)
                .Select(n => $"{n.Tag}:{n.DomesticCrisis.ProtestIntensity:0.00}/g{n.DomesticCrisis.ProtestGrievance:0.00}");
            Console.WriteLine($"YEAR {day / 365} streets={c.Nations.Count(n => n.DomesticCrisis.ProtestIntensity >= 0.25f)} " +
                              string.Join(" ", hot));
            Console.WriteLine("  RICH " + string.Join(" ", Watched.Select(c.NationByTag).Where(n => n is not null).Select(n =>
                $"{n.Tag}:stab={n.Stability:0.00},tgt={StabilityCore.Target(c, n):0.00},sol={n.StandardOfLiving:0.00},exp={n.ExpectedLivingStandard:0.00},bias={n.StabilityBias:0.00},gov={n.StabilityGovernance:0.00},inf={n.Inflation:0.00},wel={n.WelfareSpending + n.EducationSpending:0.000}," +
                $"mem={n.DomesticCrisis.SpendingMemory:0.000},g={n.DomesticCrisis.ProtestGrievance:0.00},gov={n.Government},mods={string.Join('+', n.Modifiers.Select(m => m.Id))}")));
        }
        Console.WriteLine("WAVES " + string.Join(" ", waves.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}")));
        Console.WriteLine("TALLY " + string.Join(" ", MassProtests.Tally.Select(p => $"{p.Key}={p.Value}")));
        return 0;
    }
}
