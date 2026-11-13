using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Entries the map source draws as countries that are not countries (common/country_merges):
/// on the real 2024 map their ground belongs to the state that administers it, or to nobody,
/// and with no ground they are not listed as countries (CountryMerges.IsArtefact).
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
        foreach (var (tag, ownerTag) in CountryMerges.All)
        {
            var nation = c.NationByTag(tag);
            if (nation is null) continue;
            checkedTags++;
            var resolved = CountryMerges.OwnerOf(tag);
            var owner = resolved == CountryMerges.Nobody ? null : c.NationByTag(resolved);
            Check(resolved == CountryMerges.Nobody || owner is not null, $"{tag} merges into a country that exists ({ownerTag})");
            Check(!c.HasOwnedLand(nation), $"{tag} holds no ground on the first day");
            Check(CountryMerges.IsArtefact(c, nation), $"{tag} is not listed as a country");
            var expected = owner is null ? 0 : owner.MapIndex;
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
            Check(nation is not null && c.HasOwnedLand(nation) && !CountryMerges.IsArtefact(c, nation), $"{tag} is still a country");
        }
        var exile = c.Nations.FirstOrDefault(n => n.Movement is not null);
        if (exile is not null) Check(!CountryMerges.IsArtefact(c, exile), "a landless movement is never an artefact");

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

        // Four weeks on: the Siachen Glacier did not go into exile as a "state" with Kashmir for a
        // homeland, to come back as the Military Command of Kashmir when the region rose.
        c.IsPaused = false;
        for (var day = 0; day < 28; day++) c.TickDay();
        foreach (var tag in CountryMerges.All.Keys)
        {
            var gone = c.NationByTag(tag);
            if (gone is null) continue;
            Check(gone.Population <= 0 && gone.Movement is null && !c.Homelands.ContainsKey(gone.Id) && !c.HasOwnedLand(gone),
                $"{tag} has simply ended: no people, no movement, no homeland");
        }
        Console.WriteLine("country merges: all passed");
    }
}
