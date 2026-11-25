using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Who holds the late-campaign heap. Counts every large collection a campaign owns, at
/// load and after twelve more days, with a rough byte estimate per item so the holders
/// can be ranked. The estimates are sizes of the obvious object graph (object header plus
/// fields plus a dictionary or list entry per element); they are for ranking, not for
/// accounting - the forced-collection total at the top is the accounting.
///
/// Run: FormableTests --heap-census [days]
/// </summary>
static class HeapCensusTests
{
    // Rough bytes per item: object header + fields + one list or dictionary entry.
    const double NationBytes = 90_000;        // market quotes, partners, images, modifiers, queues
    const double RelationBytes = 450;         // the relation itself plus an average of its reasons
    const double ReasonBytes = 64;
    const double StateBytes = 1_600;          // population groups, buildings, per-state caches
    const double ArmyBytes = 2_600;           // divisions and orders
    const double RouteBytes = 96;
    const double ImageBytes = 96;
    const double SampleBytes = 64;

    public static void Run(string[] args)
    {
        var slot = Array.IndexOf(args, "--heap-census");
        var days = 12;
        if (slot >= 0 && slot + 1 < args.Length && int.TryParse(args[slot + 1], out var parsed)) days = parsed;
        // SafeSlot strips spaces, so staged copies carry sanitized slot names; the third
        // argument picks which staged save to count.
        var staged = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dof-staged-save");
        var file = slot >= 0 && slot + 2 < args.Length && !args[slot + 2].StartsWith("-") ? args[slot + 2] : "50sofslowtimes.dfsave";
        var path = System.IO.Path.Combine(staged, file);
        var loaded = SaveSystem.Load(System.IO.Path.GetDirectoryName(path)!, SaveSystem.SlotFromPath(path), "");
        if (!loaded.Success || loaded.Campaign is null) throw new Exception("heap census: could not load " + loaded.Warning);
        var campaign = loaded.Campaign;
        Census(campaign, 0);
        for (var day = 0; day < days; day++) campaign.TickDay();
        Census(campaign, days);
        Console.WriteLine("PASS heap census: counted at load and after the run");
    }

    static void Census(Campaign c, int day)
    {
        var live = GC.GetTotalMemory(true) / 1048576.0;
        var relations = c.Relations.Count;
        var reasons = c.Relations.Values.Sum(r => r.Reasons.Count);
        var images = c.Nations.Sum(n => n.PublicImages.Count);
        var samples = c.Nations.Sum(n => c.History.ForNation(n.Id).Count);
        var groups = c.States.Sum(s => s.PopulationGroups.Count);
        var divisions = c.Armies.Sum(a => a.Divisions);
        var routes = c.TradeRoutes.Count;
        var partners = c.Nations.Sum(n => n.TradePartners.Count);
        var quotes = c.Nations.Sum(n => n.Market.Count);
        Console.WriteLine($"CENSUS day={day} live_mb={live:0}");
        Console.WriteLine($"CENSUS day={day} nations={c.Nations.Count} est_mb={c.Nations.Count * NationBytes / 1048576.0:0}");
        Console.WriteLine($"CENSUS day={day} relations={relations} est_mb={relations * RelationBytes / 1048576.0:0}");
        Console.WriteLine($"CENSUS day={day} opinion_reasons={reasons} est_mb={reasons * ReasonBytes / 1048576.0:0}");
        Console.WriteLine($"CENSUS day={day} states={c.States.Count} pop_groups={groups} est_mb={c.States.Count * StateBytes / 1048576.0:0}");
        Console.WriteLine($"CENSUS day={day} armies={c.Armies.Count} divisions={divisions:0} est_mb={c.Armies.Count * ArmyBytes / 1048576.0:0}");
        Console.WriteLine($"CENSUS day={day} public_images={images} est_mb={images * ImageBytes / 1048576.0:0}");
        Console.WriteLine($"CENSUS day={day} history_samples={samples} est_mb={samples * SampleBytes / 1048576.0:0}");
        Console.WriteLine($"CENSUS day={day} trade_routes={routes} est_mb={routes * RouteBytes / 1048576.0:0}");
        Console.WriteLine($"CENSUS day={day} trade_partners={partners} market_quotes={quotes}");
        Console.WriteLine($"CENSUS day={day} wars={c.Wars.Count} battles={c.Battles.Count} fleets={c.Fleets.Count} chronicle={c.Chronicle.Entries.Count}");
    }
}
