using System;
using System.Linq;
using System.IO;
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
            RumpGovernmentMayRestoreItsActualGround();
            DynamicMajorDoesNotBecomeTheStrip();
            PriorRestorationKeepsItsLargerIdentity();
            RepairOnlyOffendingIdentity();
            RepairKeepsLocalRumpsAndPriorUnions();
            DiskRepairIsNotANetworkMutation();
            BrokenCountryCanBeReunited();
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

    static void RumpGovernmentMayRestoreItsActualGround()
    {
        var (campaign, holder) = World("RUS", "Russia", 400_000,
            new ushort[] { 0, 0, 0, 0, 0, 1, 1 }, heartland: new[] { 1, 2, 3, 4, 5, 6 },
            strip: Array.Empty<int>(), heartlandPeople: 400_000, stripPeople: 0);
        Vanish(campaign, "PMR", "Transnistria", new[] { 5, 6 });
        campaign.Defines.AiFormableMinimumScore = 0;
        var restoration = FormableNations.Find(campaign, "restore_pmr");
        Check(!FormableNations.SmallerThanCountry(campaign, holder, restoration) &&
                FormableProclamationAi.Evaluate(campaign, holder, restoration).CanProclaim,
            "a rump government holding only the local ground is not compared to its lost empire by the AI");
    }

    static void BrokenCountryCanBeReunited()
    {
        // A successor holding the heartland of a country whose rump keeps only a strip: Texas and
        // a Washington that holds one coast. The rump still stands, so nothing is "vanished".
        var (campaign, holder) = World("TEX", "Texas", 140_000_000,
            new ushort[] { 0, 1, 1, 1, 1, 2, 2 }, heartland: new[] { 1, 2, 3, 4 }, strip: new[] { 5, 6 });
        var rump = new Nation { Id = 1, MapIndex = 2, Tag = "USA", Name = "United States", Population = 400_000,
            Gdp = 10f, Treasury = 5f, Stability = 0.5f, Government = "presidential_republic", Ideology = "liberalism" };
        campaign.Nations.Add(rump);
        campaign.States[1].NationMapIndex = 2;
        campaign.States[1].NationId = "USA";
        campaign.ScenarioTerritories["USA"] = new[] { 1, 2, 3, 4, 5, 6 };
        holder.SecededFromId = rump.Id;
        Check(FormableRestorations.BrokenApart(campaign, rump) && !FormableRestorations.Vanished(campaign, rump),
            "a rump holding a sliver of its people is broken apart, not vanished");
        var reunion = FormableNations.Find(campaign, "reunite_usa");
        Check(reunion is { Restores: "USA" }, "a broken country is offered as a reunion under its own tag");
        Check(FormableRestorations.MayRestore(campaign, holder, "USA"), "a successor that rose on its ground may reunite it");
        var status = FormableNations.Evaluate(campaign, holder, reunion);
        Check(status.Owned >= status.Required && status.Required < status.Total,
            $"holding most of the old ground is enough, not all of it ({status.Owned}/{status.Required}/{status.Total})");
        Check(FormableRestorations.RestoredTag("reunite_usa") == "USA", "a reunion flies the reunited country's flag");
        campaign.States[0].Population = 300_000; campaign.States[1].Population = 400_000;
        Check(!FormableRestorations.BrokenApart(campaign, rump), "a rump holding most of its people is not broken apart");
    }

    static void BadProclamation(Nation holder)
    {
        holder.FormedNationId = "restore_pmr";
        holder.FormedNationDay = 403;
        holder.FormedNationHistory.Add("restore_pmr");
        holder.Name = "Transnistria";
        holder.NameProclaimed = true;
        holder.Color = "#a84655";
    }

    static void DynamicMajorDoesNotBecomeTheStrip()
    {
        var (campaign, holder) = World("NEW", "Dynamic successor", 140_000_000,
            new ushort[] { 0, 1, 1, 1, 1, 1, 1 }, heartland: new[] { 1, 2, 3, 4 }, strip: new[] { 5, 6 });
        campaign.ScenarioTerritories.Remove(holder.Tag);
        Vanish(campaign, "PMR", "Transnistria", new[] { 5, 6 });
        var restoration = FormableNations.Find(campaign, "restore_pmr");
        Refused(campaign, holder, restoration, "a large dynamic successor without a bookmark does not become the strip");
        // Occupation is not ownership. A local state occupying a large neighbour is not
        // made that neighbour's successor by this fallback.
        for (var province = 1; province <= 4; province++) campaign.ProvinceMap.ProvinceToNation[province] = 0;
        Check(!FormableNations.SmallerThanCountry(campaign, holder, restoration) &&
                FormableNations.Evaluate(campaign, holder, restoration).CanForm,
            "the unknown-tag fallback ignores foreign ground merely under military control");
    }

    static void PriorRestorationKeepsItsLargerIdentity()
    {
        var (campaign, holder) = World("BRK", "Local predecessor", 140_000_000,
            new ushort[] { 0, 1, 1, 1, 1, 1, 1, 1 }, heartland: new[] { 1 }, strip: new[] { 2, 3, 4, 5, 6, 7 });
        Vanish(campaign, "BIG", "Restored larger state", new[] { 1, 2, 3, 4, 5, 6 });
        holder.FormedNationId = "restore_big";
        holder.FormedNationHistory.Add("restore_big");
        Check(FormableNations.Find(campaign, holder.FormedNationId) is null,
            "an already proclaimed generated restoration is no longer offered as a vanished country");
        var small = Merge("smaller_after_restoration", "Smaller successor", new[] { "BRK" },
            Array.Empty<string>(), new[] { 1, 2 }, new[] { "restore_big" });
        Refused(campaign, holder, small,
            "the retained larger restoration prevents a downgrade even when the original founding bookmark was tiny");
        var large = Merge("larger_after_restoration", "Larger successor", new[] { "BRK" },
            Array.Empty<string>(), new[] { 1, 2, 3, 4, 5, 6, 7 }, new[] { "restore_big" });
        Check(!FormableNations.SmallerThanCountry(campaign, holder, large) &&
                FormableNations.Evaluate(campaign, holder, large).CanForm,
            "a legitimate expansion covering the retained restored state remains available through its supersedes path");
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        new FormNationCommand { IssuerId = holder.Id, FormableId = large.Id }.Apply(campaign);
        Check(holder.FormedNationId == large.Id && holder.FormedNationHistory.Contains("restore_big") &&
                holder.Tag == "BRK" && holder.Id == 0 && campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners),
            "the larger successor is really proclaimed without losing the restored history or fabricating territory");
    }

    static void RepairOnlyOffendingIdentity()
    {
        var (campaign, holder) = World("RUS", "Russia", 140_000_000,
            new ushort[] { 0, 1, 1, 1, 1, 1, 1 }, heartland: new[] { 1, 2, 3, 4 }, strip: new[] { 5, 6 });
        var gone = Vanish(campaign, "PMR", "Transnistria", new[] { 5, 6 });
        BadProclamation(holder);
        holder.CapitalName = "Leningrad";
        holder.CustomName = "My chosen state name";
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        var controllers = (ushort[])campaign.ProvinceController.Clone();
        var random = campaign.Random.State;
        var cash = holder.Treasury;
        var people = holder.Population;
        Check(FormableRestorations.RepairDiminishedIdentities(campaign) == 1 &&
                holder.FormedNationId == "" && holder.FormedNationDay == -1 &&
                !holder.FormedNationHistory.Contains("restore_pmr") && holder.Name == "Russia" &&
                !holder.NameProclaimed && holder.Color == "#3f6b45",
            "the known first-proclamation bug restores the authored name, flag identity, colour and ledger");
        Check(holder.Id == 0 && holder.Tag == "RUS" && holder.Treasury == cash && holder.Population == people &&
                holder.CapitalName == "Leningrad" && holder.CustomName == "My chosen state name" &&
                holder.DisplayName == "My chosen state name" && campaign.NationByTag("PMR") == gone &&
                campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners) &&
                campaign.ProvinceController.SequenceEqual(controllers) && campaign.Random.State == random,
            "the identity repair preserves custom names, borders, cash, capitals and randomness");
        var after = CampaignFingerprint.Of(campaign);
        Check(FormableRestorations.RepairDiminishedIdentities(campaign) == 0 && CampaignFingerprint.Of(campaign) == after,
            "repairing the restored identity again is a no-op");
    }

    static void RepairKeepsLocalRumpsAndPriorUnions()
    {
        var (campaign, holder) = World("RUS", "Russia", 400_000,
            new ushort[] { 0, 0, 0, 0, 0, 1, 1 }, heartland: new[] { 1, 2, 3, 4, 5, 6 },
            strip: Array.Empty<int>(), heartlandPeople: 400_000, stripPeople: 0);
        Vanish(campaign, "PMR", "Transnistria", new[] { 5, 6 });
        BadProclamation(holder);
        var before = CampaignFingerprint.Of(campaign);
        Check(FormableRestorations.RepairDiminishedIdentities(campaign) == 0 && CampaignFingerprint.Of(campaign) == before,
            "a legitimate local restoration survives repair despite the founder's larger historical bookmark");
        var (large, power) = World("RUS", "Russia", 140_000_000,
            new ushort[] { 0, 1, 1, 1, 1, 1, 1 }, heartland: new[] { 1, 2, 3, 4 }, strip: new[] { 5, 6 });
        Vanish(large, "PMR", "Transnistria", new[] { 5, 6 });
        BadProclamation(power);
        power.FormedNationHistory.Add("prior_union");
        before = CampaignFingerprint.Of(large);
        Check(FormableRestorations.RepairDiminishedIdentities(large) == 0 && CampaignFingerprint.Of(large) == before,
            "a prior union's unknown identity is not guessed away by the legacy first-proclamation repair");
    }

    static void DiskRepairIsNotANetworkMutation()
    {
        var (campaign, holder) = World("RUS", "Russia", 140_000_000,
            new ushort[] { 0, 1, 1, 1, 1, 1, 1 }, heartland: new[] { 1, 2, 3, 4 }, strip: new[] { 5, 6 });
        Vanish(campaign, "PMR", "Transnistria", new[] { 5, 6 });
        BadProclamation(holder);
        campaign.ConfigureSinglePlayer(holder.Id);
        var snapshot = SaveSystem.ExportNetworkSnapshot(campaign, "tests");
        var peer = SaveSystem.ImportNetworkSnapshot(snapshot, "tests");
        Check(peer.Success && peer.Campaign.NationById(holder.Id).FormedNationId == "restore_pmr" &&
                !peer.Campaign.RepairDiminishedRestorationsAfterAttach,
            "joining peers preserve the host's authoritative proclamation and request no local repair");
        var folder = Path.Combine(Path.GetTempPath(), "dof-smaller-save-" + Guid.NewGuid().ToString("N"));
        SaveSystem.Save(folder, "fixture", campaign, "tests");
        var source = File.ReadAllBytes(Path.Combine(folder, "fixture.bdfsave"));
        var disk = SaveSystem.Load(folder, "fixture", "tests");
        Check(disk.Success && disk.Campaign.RepairDiminishedRestorationsAfterAttach &&
                disk.Campaign.NationById(holder.Id).FormedNationId == "restore_pmr",
            "disk loading defers the migration until saved borders are attached");
        Check(source.SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "fixture.bdfsave"))),
            "loading for the identity repair never writes the source save");
        Console.WriteLine("Private smaller-polity save fixture: " + folder);
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

    static FormableNation Merge(string id, string name, string[] eligible, string[] required, int[] provinces,
        string[] supersedes = null)
    {
        var errors = new System.Collections.Generic.List<string>();
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object>
        {
            [id] = new
            {
                name, description = name, eligible_tags = eligible, required_tags = required,
                required_provinces = provinces, minimum_stability = 0.4, gdp_cost_share = 0.01,
                governance_gain = 0.04, productivity_gain = 0.01, supersedes = supersedes ?? Array.Empty<string>()
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
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation)).SetValue(campaign, new float[owners.Length]);
    }
}
