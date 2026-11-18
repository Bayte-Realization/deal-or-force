using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// What a long campaign weighs, counted the same way before and after its diet (LongCampaign).
///
/// `--profile-save file N --diet` counts the loaded world, writes it compressed and as text,
/// runs the sweep once whatever the day, counts and writes it again, loads the compressed file
/// back to see that it is the same world, and only then profiles the N days. Without `--diet`
/// the world is only counted, and the sweep comes on its own day inside the profile.
/// </summary>
internal static class LongCampaignReport
{
    public static void Census(Campaign c, string label)
    {
        var images = 0;
        foreach (var n in c.Nations) images += n.PublicImages.Count;
        var groups = 0L;
        foreach (var s in c.States) groups += s.PopulationGroups.Count;
        var (series, samples) = c.History.Size();
        var active = c.ActiveNations.Count;
        var landed = c.Nations.Count(n => c.HasOwnedLand(n));
        var movements = c.Nations.Count(n => n.Movement is { Dissolved: false } && !c.HasOwnedLand(n));
        var heap = GC.GetTotalMemory(forceFullCollection: true);
        // What must not move when the dead are retired: the people, and the living countries' money.
        var people = 0L;
        foreach (var s in c.States) people += s.Population;
        double gdp = 0, treasury = 0, debt = 0;
        foreach (var n in c.Nations) { gdp += n.Gdp; treasury += n.Treasury; debt += n.PublicDebt; }
        Console.WriteLine($"WORLD {label}: people {people:N0}, GDP {gdp:N1}, treasuries {treasury:N2}, public debt {debt:N1}, " +
                          $"wars open {c.Wars.Count(w => !w.Ended):N0}, divisions {c.Armies.Count:N0}, hulls {c.Fleets.Sum(f => f.Hulls):N0}.");
        Console.WriteLine($"CENSUS {label}: nations {c.Nations.Count:N0} (walked {active:N0}, retired {c.RetiredNations:N0}; " +
                          $"with land {landed:N0}, living landless movements {movements:N0}), " +
                          $"relations {c.Relations.Count + c.DormantRelations.Count:N0} ({c.Relations.Count:N0} awake + {c.DormantRelations.Count:N0} asleep), " +
                          $"public images {images:N0}, ledger {series:N0} series / {samples:N0} samples, " +
                          $"population groups {groups:N0}, wars {c.Wars.Count:N0} (with province record {c.Wars.Count(w => w.OriginalController.Count > 0):N0}), " +
                          $"fleets {c.Fleets.Count:N0}, army groups {c.ArmyGroups.Count:N0}, training {c.Recruitment.Count:N0}, building {c.Construction.Count:N0}, " +
                          $"managed heap after a full collection {heap / 1048576.0:N0} MB; " +
                          $"retired records holding anything again: {c.Nations.Count(n => n.RetiredDay >= 0 && (n.Market.Count > 0 || n.PublicImages.Count > 0 || n.TradePartners.Count > 0 || n.LedgerWeek is not null || c.History.ForNation(n.Id).Count > 0))}.");
        // The other lists that only grow unless something empties them: what is still open in each.
        Console.WriteLine($"LISTS {label}: battles {c.Battles.Count:N0} (open {c.Battles.Count(b => !b.Ended):N0}), " +
                          $"sponsorships {c.Sponsorships.Count:N0}, armies {c.Armies.Count:N0}, chronicle {c.Chronicle.Entries.Count:N0}.");
        if (c.FoldedGroups > 0)
            Console.WriteLine($"FOLDED {label}: {c.FoldedGroups:N0} group(s) since the load; counted under another trade {c.FoldedTradePeople:N0}, " +
                              $"another faith {c.FoldedFaithPeople:N0}, another people {c.FoldedCulturePeople:N0} " +
                              $"({c.FoldedCulturePeople * 100.0 / Math.Max(1, people):0.0000} % of the world).");
    }

    /// <summary>The world written both ways into a scratch folder; sizes printed. Returns the folder.</summary>
    public static string Sizes(Campaign c, string folder, string label)
    {
        Directory.CreateDirectory(folder);
        var text = c.Defines.SaveTextFormat;
        try
        {
            var clock = Stopwatch.StartNew();
            c.Defines.SaveTextFormat = false;
            SaveSystem.Save(folder, label, c, "bench");
            var packedMs = clock.ElapsedMilliseconds;
            clock.Restart();
            c.Defines.SaveTextFormat = true;
            SaveSystem.Save(folder, label + "_text", c, "bench");
            var textMs = clock.ElapsedMilliseconds;
            long Length(string slot) => new FileInfo(Path.Combine(folder, slot + ".bdfsave")) is { Exists: true } file ? file.Length : 0;
            Console.WriteLine($"SAVE {label}: compressed {Length(label) / 1048576.0:N1} MB in {packedMs:N0} ms, text {Length(label + "_text") / 1048576.0:N1} MB in {textMs:N0} ms.");
        }
        finally { c.Defines.SaveTextFormat = text; }
        return folder;
    }

    public static void Diet(Campaign c, string folder)
    {
        Census(c, "as loaded");
        Sizes(c, folder, "before");
        var clock = Stopwatch.StartNew();
        var report = LongCampaign.Run(c);
        Console.WriteLine($"DIET sweep in {clock.ElapsedMilliseconds:N0} ms: {report}.");
        // A second sweep the same day finds nothing left to do: the first one was complete.
        var again = LongCampaign.Run(c);
        Console.WriteLine($"DIET second sweep: {again}.");
        Census(c, "after the sweep");
        Sizes(c, folder, "after");
        var print = CampaignFingerprint.Of(c);
        var back = SaveSystem.Load(folder, "after", "bench");
        if (back.Campaign is null) { Console.WriteLine("DIET round trip: could not load - " + back.Warning); return; }
        back.Campaign.AttachProvinceTopology(c.ProvinceMap);
        var loaded = CampaignFingerprint.Of(back.Campaign);
        Console.WriteLine($"DIET round trip: saved {print:X16}, loaded {loaded:X16} - {(print == loaded ? "the same world" : "DIFFERENT")}; " +
                          $"retired after load {back.Campaign.RetiredNations:N0} of {back.Campaign.Nations.Count:N0}.");
    }

    /// <summary>
    /// `--group-census`: what the population table is made of. Groups are a region's people by
    /// culture, faith, trade and class; this says how many of them there are at each of the three
    /// coarser grains - which is what decides how far slivers can be folded without anybody
    /// changing people or faith.
    /// </summary>
    public static void GroupCensus(Campaign c)
    {
        long groups = 0, people = 0;
        var pairs = new System.Collections.Generic.Dictionary<(int, string, string), long>();
        var cultures = new System.Collections.Generic.Dictionary<(int, string), long>();
        var world = new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var worldFaith = new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var perRegion = new System.Collections.Generic.List<int>();
        var sizes = new System.Collections.Generic.List<long>();
        for (var index = 0; index < c.States.Count; index++)
        {
            var state = c.States[index];
            perRegion.Add(state.PopulationGroups.Count);
            foreach (var g in state.PopulationGroups)
            {
                groups++; people += g.Size; sizes.Add(g.Size);
                var cu = (g.Culture ?? "").ToLowerInvariant(); var re = (g.Religion ?? "").ToLowerInvariant();
                pairs[(index, cu, re)] = pairs.GetValueOrDefault((index, cu, re)) + g.Size;
                cultures[(index, cu)] = cultures.GetValueOrDefault((index, cu)) + g.Size;
                world[cu] = world.GetValueOrDefault(cu) + g.Size;
                worldFaith[re] = worldFaith.GetValueOrDefault(re) + g.Size;
            }
        }
        perRegion.Sort();
        Console.WriteLine($"GROUPS: {groups:N0} groups of {people:N0} people in {c.States.Count:N0} regions; " +
                          $"a region holds median {perRegion[perRegion.Count / 2]}, p90 {perRegion[perRegion.Count * 9 / 10]}, most {perRegion[^1]}. " +
                          $"By (region, culture, faith): {pairs.Count:N0}; by (region, culture): {cultures.Count:N0}; " +
                          $"cultures in the world {world.Count:N0}, faiths {worldFaith.Count:N0}.");
        void Under(string label, System.Collections.Generic.IEnumerable<long> of)
        {
            var list = of.ToList();
            Console.WriteLine($"  {label}: " + string.Join("; ", new long[] { 25, 100, 250, 1000, 2500, 10000, 100000 }.Select(limit =>
                $"<{limit:N0}: {list.Count(v => v < limit):N0} ({list.Where(v => v < limit).Sum() * 100.0 / Math.Max(1, people):0.0000} % of people)")));
        }
        Under("groups", sizes);
        Under("(region, culture, faith)", pairs.Values);
        Under("(region, culture)", cultures.Values);
        Console.WriteLine($"  cultures of the world under 10,000 people: {world.Count(w => w.Value < 10000)}; under 100,000: {world.Count(w => w.Value < 100000)}.");
    }

    /// <summary>
    /// `--fold-now`: fold the loaded world's slivers once, as the weekly cycle would, and say
    /// what went and who is now counted differently (PopulationFolding). With `--define`s for
    /// the thresholds this is how they were chosen.
    /// </summary>
    public static void FoldNow(Campaign c)
    {
        var d = c.Defines;
        var cultures = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var faiths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Count(Dictionary<string, long> byCulture, Dictionary<string, long> byFaith, out long people, out long groups)
        {
            people = 0; groups = 0;
            foreach (var s in c.States)
            foreach (var g in s.PopulationGroups)
            {
                groups++; people += g.Size;
                byCulture[g.Culture ?? ""] = byCulture.GetValueOrDefault(g.Culture ?? "") + g.Size;
                byFaith[g.Religion ?? ""] = byFaith.GetValueOrDefault(g.Religion ?? "") + g.Size;
            }
        }
        Count(cultures, faiths, out var peopleBefore, out var groupsBefore);
        var clock = Stopwatch.StartNew();
        var gone = PopulationFolding.FoldWorld(c);
        clock.Stop();
        var after = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var faithsAfter = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        Count(after, faithsAfter, out var peopleAfter, out var groupsAfter);
        var perRegion = c.States.Select(s => s.PopulationGroups.Count).OrderBy(n => n).ToList();
        long cultureShift = 0; double worstCulture = 0; var worstName = "";
        foreach (var (name, was) in cultures)
        {
            var now = after.GetValueOrDefault(name);
            cultureShift += Math.Abs(now - was);
            var loss = was > 0 ? (was - now) / (double)was : 0;
            if (loss > worstCulture) { worstCulture = loss; worstName = name; }
        }
        long faithShift = 0;
        foreach (var (name, was) in faiths) faithShift += Math.Abs(faithsAfter.GetValueOrDefault(name) - was);
        Console.WriteLine($"FOLD trade<={d.FoldTradeSliverPeople:N0}&{d.FoldTradeSliverShare:P3} people<={d.FoldPeopleSliverPeople:N0}&{d.FoldPeopleSliverShare:P3} " +
                          $"keep>={d.FoldKeepCulturePeople:N0}: groups {groupsBefore:N0} -> {groupsAfter:N0} ({gone:N0} gone) in {clock.ElapsedMilliseconds} ms; " +
                          $"people {peopleBefore:N0} -> {peopleAfter:N0} ({(peopleBefore == peopleAfter ? "SAME" : "DIFFERENT")}); " +
                          $"a region now holds median {perRegion[perRegion.Count / 2]}, p90 {perRegion[perRegion.Count * 9 / 10]}, most {perRegion[^1]}.");
        Console.WriteLine($"  counted under another trade {c.FoldedTradePeople:N0} ({c.FoldedTradePeople * 100.0 / peopleBefore:0.000} %), " +
                          $"another faith {c.FoldedFaithPeople:N0} ({c.FoldedFaithPeople * 100.0 / peopleBefore:0.0000} %), " +
                          $"another people {c.FoldedCulturePeople:N0} ({c.FoldedCulturePeople * 100.0 / peopleBefore:0.0000} %). " +
                          $"World totals: peoples {cultures.Count} -> {after.Count(a => a.Value > 0)}, net moved between peoples {cultureShift / 2:N0}, " +
                          $"between faiths {faithShift / 2:N0}; the people that lost most lost {worstCulture:P3} ({worstName}).");
    }
}
