using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Entries the map source draws as countries that are not countries (common/country_merges):
/// on the real 2024 map their ground belongs to the state that administers it, or to nobody,
/// and they are not in the nation list at all (ScenarioCatalog reads the country table past them).
/// </summary>
static class CountryMergeTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL country merges: " + label);
        Console.WriteLine("PASS: country merges " + label);
    }

    public static void Run(Campaign c)
    {
        var map = c.ProvinceMap;
        Check(map is not null && map.ProvinceCount > 1000, "the real map is attached");
        foreach (var tag in new[] { "SCR", "KAS", "BRT", "UMI", "CNM", "ESB", "WSB", "CLP", "CSI", "ATC", "BJN", "SER", "PGA", "SPI", "ATA" })
            Check(CountryMerges.IsMerged(tag), $"{tag} is in the table");

        var checkedTags = 0;
        var ground = CountryMerges.GroundByMapIndex();
        foreach (var (tag, ownerTag) in CountryMerges.All)
        {
            // A line of the table can belong to another bookmark's country table.
            var tableIndex = CountryMerges.MapIndexOf(tag);
            if (tableIndex <= 0) continue;
            checkedTags++;
            var resolved = CountryMerges.OwnerOf(tag);
            var owner = resolved == CountryMerges.Nobody ? null : c.NationByTag(resolved);
            Check(resolved == CountryMerges.Nobody || owner is not null, $"{tag} merges into a country that exists ({ownerTag})");
            Check(c.NationByTag(tag) is null && c.Nations.All(n => n.MapIndex != tableIndex), $"{tag} is not a country in the world");
            var expected = owner is null ? 0 : owner.MapIndex;
            Check(ground.TryGetValue(tableIndex, out var held) && held == expected, $"{tag}'s row maps to {(owner?.Tag ?? "nobody")}'s");
            var painted = Enumerable.Range(1, map.ProvinceCount).Count(p => map.ProvinceToNation[p] == tableIndex || c.ProvinceController[p] == tableIndex);
            Check(painted == 0, $"no province is left in {tag}'s colour");
            var bookmark = ProvinceOwnership.ProvincesOf(tag).Where(p => p > 0 && p <= map.ProvinceCount && !map.IsWater(p)).ToArray();
            var corrections = ProvinceOwnership.PointCorrections(map);
            var moved = bookmark.Where(p => !corrections.ContainsKey(p)).ToArray();
            Check(moved.All(p => map.ProvinceToNation[p] == expected && c.ProvinceController[p] == expected),
                $"{tag}'s {moved.Length} province(s) belong to {(owner?.Tag ?? "nobody")}");
        }
        Check(checkedTags >= 15, $"the table covers the country table's entries ({checkedTags})");

        foreach (var tag in new[] { "CHN", "IND", "GBR", "FRA", "AUS", "USA", "CYP", "COL" })
        {
            var nation = c.NationByTag(tag);
            Check(nation is not null && c.HasOwnedLand(nation) && !CountryMerges.IsLegacyEntry(c, nation), $"{tag} is still a country");
        }
        var exile = c.Nations.FirstOrDefault(n => n.Movement is not null);
        if (exile is not null) Check(!CountryMerges.IsLegacyEntry(c, exile), "a landless movement is never a leftover entry");

        // Siachen's ground, by point, is India's; the British base areas are Britain's.
        var gbr = c.NationByTag("GBR");
        var bases = ProvinceOwnership.ProvincesOf("ESB").Concat(ProvinceOwnership.ProvincesOf("WSB"))
            .Where(p => p > 0 && p <= map.ProvinceCount && !map.IsWater(p)).ToArray();
        Check(bases.Length > 0 && bases.All(p => map.ProvinceToNation[p] == gbr.MapIndex), "Akrotiri and Dhekelia are British ground");
        var cyp = c.NationByTag("CYP");
        var buffer = ProvinceOwnership.ProvincesOf("CNM").Where(p => p > 0 && p <= map.ProvinceCount && !map.IsWater(p)).ToArray();
        Check(buffer.Length > 0 && buffer.All(p => map.ProvinceToNation[p] == cyp.MapIndex), "the Cyprus buffer zone is the Republic's");

        // A mod taking an entry off the list: the empty owner removes it, and a reload restores the base table.
        using (var mod = JsonDocument.Parse("{ \"SCR\": { \"into\": \"\" } }"))
            CountryMerges.Merge(mod.RootElement);
        Check(!CountryMerges.IsMerged("SCR"), "a mod can take an entry off the list");
        CountryMerges.ResetCatalog();
        Check(CountryMerges.IsMerged("SCR") && CountryMerges.OwnerOf("SCR") == "CHN", "the base table reloads");
        Check(CountryMerges.OwnerOf("CHN") == "CHN", "a tag not in the table is its own owner");

        // Four weeks on: nothing has brought one back. The Siachen Glacier once went into exile
        // as a "state" with Kashmir for a homeland, and came back as the Military Command of
        // Kashmir when the region rose; there is no such country to do it now.
        c.IsPaused = false;
        for (var day = 0; day < 28; day++) c.TickDay();
        foreach (var tag in CountryMerges.All.Keys)
            Check(c.NationByTag(tag) is null, $"{tag} is still not a country four weeks on");
        Console.WriteLine("country merges: all passed");
    }
}
