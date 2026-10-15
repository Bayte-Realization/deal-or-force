using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class RegionalDemographyTests
{
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS regional demography: " + label); }

    public static void Run()
    {
        RegionalDemography.ResetCatalog();
        var expected = new[] {
            ("by_br", 1348115L, 1171835L, 97936L, 14893L, 37648L),
            ("by_vi", 1135731L, 934925L, 138075L, 9806L, 17993L),
            ("by_ho", 1388512L, 1211234L, 108712L, 2572L, 25085L),
            ("by_hr", 1026816L, 701190L, 65550L, 223119L, 10767L),
            ("by_mi", 3489521L, 3055902L, 234487L, 35182L, 55935L),
            ("by_ma", 1024751L, 915633L, 62232L, 2121L, 12228L)
        };
        foreach (var (id, total, belarusian, russian, polish, ukrainian) in expected)
        {
            var reference = RegionalDemography.Find(id);
            long Count(string culture) => reference.Areas.Sum(area => area.Counts.GetValueOrDefault(culture));
            Check(reference.Population == total && reference.Date == new DateOnly(2019, 10, 4), "source year and total " + id);
            Check(Count("belarusian") == belarusian && Count("russian") == russian &&
                Count("polish") == polish && Count("ukrainian") == ukrainian, "published major-group counts " + id);
            Check(reference.Coverage > .97 && reference.Coverage < 1 && reference.Unlisted > 0 &&
                reference.Mapped + reference.Unmapped + reference.Unlisted == reference.Population,
                "unlisted responses are not hidden " + id);
            Check(reference.CultureMix.Count == 19 && Math.Abs(reference.CultureMix.Sum(item => item.Share) - 1) < .000001,
                "nineteen mapped cultures normalise without an invented remainder " + id);
        }
        var minsk = RegionalDemography.Find("by_mi");
        Check(minsk.Areas.Count == 2 && minsk.Areas[0].Page == 249 && minsk.Areas[1].Page == 252 &&
            Math.Abs(minsk.CultureMix.First(item => item.Culture == "russian").Share - 234487d / minsk.Mapped) < .000001,
            "Minsk city and oblast add head-counts before normalisation");
        Check(RegionalDemography.Find("by_hr").CultureMix.First(item => item.Culture == "polish").Share > .22f &&
            RegionalDemography.Find("by_ho").CultureMix.First(item => item.Culture == "polish").Share < .003f,
            "regional geography is no longer a repeated national average");

        var campaign = Campaign.CreateBootstrap(601);
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var topology, out var mapError),
            "save fixture attaches production province geography: " + mapError);
        campaign.AttachProvinceTopology(topology);
        foreach (var (id, _, _, _, _, _) in expected)
        {
            var state = campaign.States.First(item => item.Id == id);
            var reference = RegionalDemography.Find(id);
            Check(state.PopulationGroups.All(group => group.Size > 0) && state.PopulationGroups.Sum(group => group.Size) == state.Population,
                "startup keeps every person " + id);
            Check(reference.CultureMix.All(item => Math.Abs(state.PopulationGroups.Where(group => group.Culture == item.Culture)
                .Sum(group => group.Size) / (double)state.Population - item.Share) < .0001),
                "profile reaches initial trade/culture seeding " + id);
        }
        var grodno = campaign.States.First(state => state.Id == "by_hr");
        var before = CampaignFingerprint.Of(campaign);
        Check(RegionalDemography.ReferenceFor(campaign, grodno).Population == 1026816 &&
            CampaignFingerprint.Of(campaign) == before, "source lookup is read-only");
        var split = new ScenarioState { Id = "split_fixture", MapIndex = 60001, ParentMapIndex = grodno.MapIndex };
        Check(ReferenceEquals(RegionalDemography.ReferenceFor(campaign, split), RegionalDemography.Find("by_hr")),
            "split region discloses its parent-scale source");
        Check(RegionalDemography.Find("unknown_region") is null && RegionalDemography.ReferenceFor(campaign, null) is null,
            "missing data remains missing");

        // Exercise the history override after seeding: population and occupation totals stay put.
        var trades = grodno.PopulationGroups.GroupBy(group => (group.Profession, group.Class, group.Sector))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Size));
        var errors = new List<string>();
        HistoryDataLoader.ApplyStates(campaign, "{\"by_hr\":{\"culture_profile\":\"by_vi\"}}", errors);
        Check(errors.Count == 0 && grodno.PopulationGroups.Sum(group => group.Size) == grodno.Population &&
            trades.All(trade => Math.Abs(grodno.PopulationGroups.Where(group =>
                (group.Profession, group.Class, group.Sector) == trade.Key).Sum(group => group.Size) - trade.Value) <= 100),
            "history profile replacement preserves population and trades within rounding");
        HistoryDataLoader.ApplyStates(campaign,
            "{\"by_hr\":{\"cultures\":{\"belarusian\":1},\"culture_profile\":\"by_vi\"}}", errors);
        Check(grodno.PopulationGroups.All(group => group.Culture == "belarusian"), "explicit mod mix wins over a profile");
        before = CampaignFingerprint.Of(campaign);
        HistoryDataLoader.ApplyStates(campaign, "{\"by_hr\":{\"culture_profile\":\"missing\"}}", errors);
        Check(errors.Count == 1 && before == CampaignFingerprint.Of(campaign), "unknown profile reports an error without erasing people");
        errors.Clear();
        HistoryDataLoader.ApplyStates(campaign, "{\"by_hr\":{\"cultures\":{\"polish\":\"bad\"}}}", errors);
        Check(errors.Count == 1 && before == CampaignFingerprint.Of(campaign), "malformed history mix is nonfatal and non-destructive");
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(campaign, "demography"), "demography");
        Check(loaded.Success, "snapshot loads: " + loaded.Warning);
        loaded.Campaign.AttachProvinceTopology(campaign.ProvinceMap.CloneForOverrides());
        Check(loaded.Success && loaded.Campaign.States.First(state => state.Id == "by_hr").PopulationGroups
            .All(group => group.Culture == "belarusian"), "save load keeps evolved composition instead of reseeding census data");
        Check(loaded.Success && CampaignFingerprint.Of(loaded.Campaign) == before, "snapshot fingerprint survives the data correction");

        foreach (var people in new long[] { 0, 1, 17, 500, 7000003 })
        {
            var tiny = new Campaign(77);
            var region = new ScenarioState { Id = "rounding_fixture", MapIndex = 1234, Population = people };
            tiny.States.Add(region);
            for (var trade = 0; trade < 10; trade++)
                region.PopulationGroups.Add(new PopulationGroup {
                    Culture = "belarusian", Religion = "orthodox", Profession = "trade_" + trade,
                    Size = people / 10 + (trade < people % 10 ? 1 : 0)
                });
            errors.Clear();
            HistoryDataLoader.ApplyStates(tiny, "{\"rounding_fixture\":{\"culture_profile\":\"by_hr\"}}", errors);
            Check(errors.Count == 0 && region.PopulationGroups.All(group => group.Size > 0) &&
                region.PopulationGroups.Sum(group => group.Size) == people,
                "largest-remainder apportionment loses nobody and creates no negative cells at population " + people);
        }

        var path = Path.Combine(GameDataLoader.DataFolder(), "common", "regional_demography", "00_belarus_2019.json");
        var original = JsonNode.Parse(File.ReadAllText(path))["by_hr"];
        void Reject(Action<JsonNode> change, string label)
        {
            var replacement = original.DeepClone(); change(replacement);
            var prior = RegionalDemography.Find("by_hr"); errors.Clear();
            using var document = JsonDocument.Parse(new JsonObject { ["by_hr"] = replacement }.ToJsonString());
            RegionalDemography.Merge(document.RootElement, errors);
            Check(errors.Count == 1 && ReferenceEquals(prior, RegionalDemography.Find("by_hr")), label);
        }
        Reject(record => record["date"] = "not-a-date", "invalid dates cannot replace a source");
        Reject(record => record["url"] = "file:///private", "source links accept only HTTP(S)");
        Reject(record => record["areas"][0]["counts"]["polish"] = -1, "negative counts are rejected atomically");
        Reject(record => record["areas"][0]["counts"]["polish"] = "223119", "string counts are rejected atomically");
        Reject(record => record["areas"][0]["counts"]["not_a_culture"] = 1, "unmapped names cannot become game cultures");
        Reject(record => record["areas"][0]["population"] = 1, "over-counted observations are rejected");
        Reject(record => record["areas"][0]["counts"]["polish"] = long.MaxValue, "overflow is rejected atomically");
        Reject(record => record["areas"] = new JsonArray(), "empty observations preserve existing data");
        Reject(record => ((JsonArray)record["areas"]).Add(record["areas"][0].DeepClone()), "duplicate areas cannot count people twice");
        errors.Clear();
        using (var replacement = JsonDocument.Parse(new JsonObject { ["fixture_region"] = original.DeepClone() }.ToJsonString()))
            RegionalDemography.Merge(replacement.RootElement, errors);
        Check(errors.Count == 0 && RegionalDemography.Find("fixture_region") is not null, "mods can add a complete new regional source");
        RegionalDemography.ResetCatalog();
        Check(RegionalDemography.Find("fixture_region") is null && RegionalDemography.Find("by_hr") is not null,
            "catalog reload clears a disabled mod and restores base sources");
    }
}
