using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// A country that already is a larger state, or is pursuing one, does not proclaim a smaller name.
/// The holder of only that smaller ground still can. Nation id 0 stays at index 0, and the map is
/// attached by reflection so the 2024 political map is never rewritten.
/// </summary>
static class SmallerPolityTests
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS smaller polity: " + message);
    }

    public static void Run()
    {
        FormableNations.ResetCatalog();
        try
        {
            HeartlandDoesNotBecomeTheStrip();
            AWiderEmptierNameIsStillSmaller();
            PursuingALargerUnionBlocksTheStrip();
            SmallHolderOfOnlyTheStripStillForms();
        }
        finally { FormableNations.ResetCatalog(); }
    }

    static void HeartlandDoesNotBecomeTheStrip()
    {
        var (campaign, holder) = World("RUS", "Russia", 140_000_000,
            new ushort[] { 0, 1, 1, 1, 1, 1, 1 }, heartland: new[] { 1, 2, 3, 4 }, strip: new[] { 5, 6 });
        Check(campaign.Nations[0] == holder && holder.Id == 0, "the heartland holder is nation id 0 at index 0");
        var gone = Vanish(campaign, "PMR", "Transnistria", new[] { 5, 6 });
        var restoration = FormableNations.Find(campaign, "restore_pmr");
        Check(restoration is { Restores: "PMR" }, "the vanished strip is the generated restoration");
        Refused(campaign, holder, restoration, "a heartland holder does not become the smaller strip");
        Check(!FormableProclamationAi.Evaluate(campaign, holder, restoration).CanProclaim &&
                FormableProclamationAi.Evaluate(campaign, holder, restoration).Refusal ==
                Loc.Get("The new state is not smaller than the country already is"),
            "the proclamation AI refuses the same smaller restoration and no other reason");

        var shard = Merge("heartland_shard", "Heartland shard", new[] { "RUS" }, Array.Empty<string>(), new[] { 1, 2 });
        Refused(campaign, holder, shard, "a formable whose cores are a strict subset of the heartland is refused");

        campaign.ScenarioTerritories["ADJ"] = new[] { 5, 6 };
        var union = Merge("heartland_union", "Heartland Union", new[] { "RUS" }, new[] { "RUS", "ADJ" }, Array.Empty<int>());
        var ready = FormableNations.Evaluate(campaign, holder, union);
        Check(!FormableNations.SmallerThanCountry(campaign, holder, union) && ready.CanForm,
            "a union that covers the heartland and adds land is still allowed");
        new FormNationCommand { IssuerId = holder.Id, FormableId = union.Id }.Apply(campaign);
        Check(holder.FormedNationId == union.Id && holder.Tag == "RUS" && holder.Id == 0 &&
                campaign.NationByTag("PMR") == gone,
            "forming the larger union keeps the tag and leaves the vanished strip in the world");
    }

    static void AWiderEmptierNameIsStillSmaller()
    {
        // Two dense heartland provinces against four thinly peopled ones. Land count would not
        // call the wide name smaller; the people on the heartland do.
        var (campaign, holder) = World("RUS", "Russia", 100_000_000,
            new ushort[] { 0, 1, 1, 1, 1, 1, 1 }, heartland: new[] { 1, 2 }, strip: new[] { 3, 4, 5, 6 },
            heartlandPeople: 100_000_000, stripPeople: 1_000_000);
        var wide = Merge("wide_empty", "Wide empty name", new[] { "RUS" }, Array.Empty<string>(), new[] { 3, 4, 5, 6 });
        Check(FormableNations.SmallerThanCountry(campaign, holder, wide),
            "a name with more provinces but fewer people than the heartland is still smaller");
        Refused(campaign, holder, wide, "the wide empty name is not proclaimed");
    }

    static void PursuingALargerUnionBlocksTheStrip()
    {
        var (campaign, holder) = World("BRK", "Breakaway", 400_000,
            new ushort[] { 0, 1, 1, 1, 1, 1, 1 }, heartland: new[] { 5, 6 }, strip: new[] { 1, 2, 3, 4 },
            heartlandPeople: 400_000, stripPeople: 80_000_000);
        // The holder's own bookmark is only the strip (passed as "heartland" above). The wide
        // provinces are ground it occupies, not the state it already is — until it pursues them.
        campaign.ScenarioTerritories["BIG"] = new[] { 1, 2, 3, 4 };
        Vanish(campaign, "PMR", "Transnistria", new[] { 5, 6 });
        var restoration = FormableNations.Find(campaign, "restore_pmr");
        Check(!FormableNations.SmallerThanCountry(campaign, holder, restoration) &&
                FormableNations.Evaluate(campaign, holder, restoration).CanForm,
            "without a larger project, a holder whose identity is only the strip may proclaim it");
        Merge("greater_heartland", "Greater Heartland", new[] { "BRK" }, new[] { "BIG" }, Array.Empty<int>());
        holder.ConsolidationProject = "greater_heartland";
        Refused(campaign, holder, restoration, "a country already pursuing a larger union does not proclaim the strip instead");
    }

    static void SmallHolderOfOnlyTheStripStillForms()
    {
        var (campaign, holder) = World("BRK", "Breakaway", 400_000,
            new ushort[] { 0, 0, 0, 0, 0, 1, 1 }, heartland: new[] { 5, 6 }, strip: Array.Empty<int>(),
            heartlandPeople: 400_000, stripPeople: 0);
        Check(campaign.Nations[0] == holder && holder.Id == 0, "the small holder is nation id 0 at index 0");
        var gone = Vanish(campaign, "PMR", "Transnistria", new[] { 5, 6 });
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        var restoration = FormableNations.Find(campaign, "restore_pmr");
        var before = CampaignFingerprint.Of(campaign);
        var random = campaign.Random.State;
        var status = FormableNations.Evaluate(campaign, holder, restoration);
        Check(restoration is { Restores: "PMR" } && !FormableNations.SmallerThanCountry(campaign, holder, restoration) &&
                status.CanForm,
            "a holder of only the strip is not refused for being smaller than itself");
        Check(CampaignFingerprint.Of(campaign) == before && campaign.Random.State == random,
            "reading that permission changes nothing");
        new FormNationCommand { IssuerId = holder.Id, FormableId = restoration.Id }.Apply(campaign);
        Check(holder.FormedNationId == "restore_pmr" && holder.Id == 0 && holder.Tag == "BRK" &&
                campaign.NationByTag("PMR") == gone && !campaign.HasOwnedLand(gone) &&
                campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners),
            "the small holder proclaims the strip, and the old tag remains without taking any land");
    }

    static void Refused(Campaign campaign, Nation holder, FormableNation definition, string why)
    {
        var before = CampaignFingerprint.Of(campaign);
        var random = campaign.Random.State;
        var status = FormableNations.Evaluate(campaign, holder, definition);
        var label = Loc.Get("The new state is not smaller than the country already is");
        Check(FormableNations.SmallerThanCountry(campaign, holder, definition) && !status.CanForm, why);
        Check(status.Requirements.Count(requirement => !requirement.Met) == 1 &&
                status.Requirements.Single(requirement => !requirement.Met).Label == label,
            why + " and every other founding condition is already met");
        Check(CampaignFingerprint.Of(campaign) == before && campaign.Random.State == random,
            why + " and reading the refusal changes nothing");
        var formed = holder.FormedNationId;
        var name = holder.Name;
        new FormNationCommand { IssuerId = holder.Id, FormableId = definition.Id }.Apply(campaign);
        Check(holder.FormedNationId == formed && holder.Name == name && CampaignFingerprint.Of(campaign) == before,
            why + " and the founding command does not rename the country");
    }

    static (Campaign Campaign, Nation Holder) World(string tag, string name, long people, ushort[] owners,
        int[] heartland, int[] strip, long heartlandPeople = 140_000_000, long stripPeople = 400_000)
    {
        var campaign = new Campaign(20261021) { ScenarioId = "smaller_polity" };
        var holder = new Nation
        {
            Id = 0, MapIndex = 1, Tag = tag, Name = name, Population = people,
            Gdp = 1700f, Treasury = 500f, Stability = 0.9f,
            Government = "presidential_republic", Ideology = "nationalism"
        };
        holder.FoundedCultures.Add("russian");
        campaign.Nations.Add(holder);
        var states = new ushort[owners.Length];
        foreach (var province in heartland) states[province] = 4100;
        foreach (var province in strip) states[province] = 4101;
        if (heartland.Length > 0)
            campaign.States.Add(new ScenarioState
            {
                Id = "heartland", Name = "Heartland", MapIndex = 4100, NationMapIndex = 1,
                NationId = tag, Population = heartlandPeople
            });
        if (strip.Length > 0)
            campaign.States.Add(new ScenarioState
            {
                Id = "strip", Name = "Strip", MapIndex = 4101, NationMapIndex = 1,
                NationId = tag, Population = stripPeople
            });
        campaign.ScenarioTerritories[tag] = heartland.ToArray();
        Attach(campaign, states, owners);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 400L * 24L);
        return (campaign, holder);
    }

    static Nation Vanish(Campaign campaign, string tag, string name, int[] provinces)
    {
        var gone = new Nation { Id = 1, MapIndex = 2, Tag = tag, Name = name, Population = 0, Color = "#a84655" };
        campaign.Nations.Add(gone);
        campaign.ScenarioTerritories[tag] = provinces.ToArray();
        return gone;
    }

    static FormableNation Merge(string id, string name, string[] eligible, string[] required, int[] provinces)
    {
        var errors = new System.Collections.Generic.List<string>();
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
        {
            [id] = new
            {
                name, description = name, eligible_tags = eligible, required_tags = required,
                required_provinces = provinces, minimum_stability = 0.4, gdp_cost_share = 0.01,
                governance_gain = 0.04, productivity_gain = 0.01
            }
        }));
        FormableNations.Merge(data.RootElement, errors);
        if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
        return FormableNations.Find(id);
    }

    static void Attach(Campaign campaign, ushort[] states, ushort[] owners)
    {
        var neighbors = new int[states.Length][];
        for (var i = 0; i < neighbors.Length; i++) neighbors[i] = Array.Empty<int>();
        var topology = (ProvinceTopology)Activator.CreateInstance(
            typeof(ProvinceTopology),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[] { new ushort[1], states, owners, new int[states.Length], neighbors, 1, Math.Max(1, states.Length - 1) },
            null);
        if (topology is null) throw new Exception("the fixture map was not constructed");
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).SetValue(campaign, topology);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).SetValue(campaign, (ushort[])owners.Clone());
    }
}
