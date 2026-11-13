using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Independent audit of the authored administration graph, not its runtime output.</summary>
internal static class OpeningTerritoryChecks
{
    internal static string SourceSummary(Campaign campaign, int province)
    {
        var counts = new Dictionary<int, int>();
        var cells = campaign.ProvinceMap.CellToProvince;
        for (var cell = 0; cell < cells.Length; cell++)
            if (cells[cell] == province)
            {
                var ground = (int)WorldMapSource.Control[cell];
                counts[ground] = counts.GetValueOrDefault(ground) + 1;
            }
        var centre = campaign.ProvinceMap.CenterOf(province);
        return $"centre {centre.Longitude:0.#####},{centre.Latitude:0.#####}; source cells " +
               string.Join(", ", counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key)
                   .Select(pair => $"{campaign.NationByMapIndex(pair.Key)?.Tag ?? pair.Key.ToString()}={pair.Value}"));
    }

    internal static string Administrations(Campaign campaign, IReadOnlyDictionary<string, string> declarations,
        out Dictionary<int, int> owners)
    {
        owners = new Dictionary<int, int>();
        var graph = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in declarations) graph[entry.Key] = entry.Value;
        foreach (var source in graph.Keys.OrderBy(tag => tag, StringComparer.Ordinal))
        {
            var nation = campaign.NationByTag(source);
            if (nation is null) continue; // A declaration can belong to another bookmark.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = new List<string>();
            var current = source;
            while (true)
            {
                if (string.IsNullOrWhiteSpace(current)) return $"country_merges: {source} has an empty owner";
                if (!seen.Add(current))
                    return $"country_merges: cycle from {source}: {string.Join(" -> ", path.Take(12))} -> {current}";
                path.Add(current);
                if (string.Equals(current, CountryMerges.Nobody, StringComparison.OrdinalIgnoreCase))
                {
                    owners[nation.MapIndex] = 0;
                    break;
                }
                if (graph.TryGetValue(current, out var next)) { current = next; continue; }
                var owner = campaign.NationByTag(current);
                if (owner is null) return $"country_merges: {source} names missing owner {current}";
                owners[nation.MapIndex] = owner.MapIndex;
                break;
            }
        }
        return "";
    }

    internal static string Regressions(Campaign campaign, Func<Campaign, string> ownershipCheck,
        Func<Campaign, IReadOnlyDictionary<string, string>, string> rosterCheck)
    {
        var count = 0;
        void Require(bool ok, string note)
        {
            if (!ok) throw new InvalidOperationException("opening territory: " + note);
            count++;
            Console.WriteLine("PASS opening territory: " + note);
        }
        var initialError = ownershipCheck(campaign);
        Require(initialError.Length == 0, "the uncorrupted real map passes: " + initialError);
        initialError = rosterCheck(campaign, null);
        Require(initialError.Length == 0, "the real roster respects declared administrations: " + initialError);
        var world = new Campaign(61109);
        world.Nations.Add(new Nation { Id = 0, MapIndex = 1, Tag = "AAA", Population = 100 });
        world.Nations.Add(new Nation { Id = 1, MapIndex = 2, Tag = "BBB", Population = 100 });
        world.Nations.Add(new Nation { Id = 2, MapIndex = 3, Tag = "BASE", Population = 10 });
        world.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "a" });
        world.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 2, Id = "b" });
        var simple = new Dictionary<string, string> { ["BASE"] = "AAA" };
        var before = CampaignFingerprint.Of(world);
        Require(rosterCheck(world, simple).Length == 0, "a declared base needs no independent government");
        Require(rosterCheck(world, new Dictionary<string, string>()).Contains("BASE"),
            "removing the declaration exposes the landless country");
        world.States.RemoveAt(1);
        world.InvalidateStateIndex();
        Require(rosterCheck(world, simple).Contains("BBB"), "a base declaration does not exempt an unrelated country");
        world.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 2, Id = "b" });
        world.InvalidateStateIndex();
        Require(CampaignFingerprint.Of(world) == before, "the roster fixture is restored");
        var chain = new Dictionary<string, string> { ["base"] = "bBb", ["BBB"] = "aaa" };
        Require(Administrations(world, chain, out var owners).Length == 0 && owners[3] == 1 && owners[2] == 1,
            "chains and case-insensitive tags resolve to the final owner");
        Require(Administrations(world, new Dictionary<string, string> { ["BASE"] = "NoNe" }, out owners).Length == 0 && owners[3] == 0,
            "unclaimed administration resolves to nobody");
        Require(Administrations(world, new Dictionary<string, string> { ["BASE"] = "MISSING" }, out _).Contains("missing owner"),
            "a missing administrator is an error, not an exemption");
        Require(Administrations(world, new Dictionary<string, string> { ["BASE"] = "BASE" }, out _).Contains("cycle"),
            "a self-merge is rejected");
        Require(Administrations(world, new Dictionary<string, string> { ["BASE"] = "BBB", ["BBB"] = "BASE" }, out _).Contains("cycle"),
            "a cyclic administration graph is rejected");
        Require(Administrations(world, new Dictionary<string, string> { ["BASE"] = "" }, out _).Contains("empty owner"),
            "an invalid empty graph edge is rejected");
        Require(CampaignFingerprint.Of(world) == before, "reading the graph changes no campaign state or RNG");
        Require(rosterCheck(world, new Dictionary<string, string> { ["BASE"] = "MISSING" }).Contains("missing owner"),
            "the roster gate propagates invalid declarations");

        // Corruption is confined to this diagnostic campaign, never the map files or a
        // player save. Restoration happens even if an assertion throws.
        var map = campaign.ProvinceMap;
        var source = campaign.NationByTag("IOT");
        var wrong = campaign.NationByTag("FRA");
        Require(source is not null && wrong is not null, "real-map negative fixtures are available");
        int Ground(int p) => map.ProvinceCenterCell[p] >= 0 && map.ProvinceCenterCell[p] < WorldMapSource.Control.Length
            ? WorldMapSource.Control[map.ProvinceCenterCell[p]] : -1;
        var merged = Enumerable.Range(1, map.ProvinceCount).FirstOrDefault(p =>
            Ground(p) == source.MapIndex && map.ProvinceToNation[p] != source.MapIndex && !map.IsWater(p));
        Require(merged > 0, "a real merged province exists");
        var fingerprint = CampaignFingerprint.Of(campaign);
        var saved = map.ProvinceToNation[merged];
        try
        {
            map.ProvinceToNation[merged] = (ushort)source.MapIndex;
            Require(ownershipCheck(campaign).Length > 0, "a merged province retaining its obsolete source owner fails");
            map.ProvinceToNation[merged] = (ushort)wrong.MapIndex;
            Require(ownershipCheck(campaign).Length > 0, "giving the merged province to an unrelated real country fails");
        }
        finally { map.ProvinceToNation[merged] = saved; }
        var ordinary = Enumerable.Range(1, map.ProvinceCount).FirstOrDefault(p =>
            Ground(p) == wrong.MapIndex && map.ProvinceToNation[p] == wrong.MapIndex && !map.IsWater(p));
        Require(ordinary > 0, "a real ordinary province exists");
        saved = map.ProvinceToNation[ordinary];
        try
        {
            map.ProvinceToNation[ordinary] = (ushort)campaign.NationByTag("USA").MapIndex;
            Require(ownershipCheck(campaign).Length > 0, "an ordinary province assigned to the wrong country still fails");
        }
        finally { map.ProvinceToNation[ordinary] = saved; }
        Require(CampaignFingerprint.Of(campaign) == fingerprint, "all deliberate corruption is restored exactly");
        Console.WriteLine($"  opening territory: {count} regression assertions passed.");
        return "";
    }
}
