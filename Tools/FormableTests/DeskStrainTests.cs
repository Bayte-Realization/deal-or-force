using System;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The society chair of the desk, which used to leave political strain blank.
/// The line is the report the situation room already files. Reading it changes nothing.
/// </summary>
static class DeskStrainTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("desk strain: " + label);
        Console.WriteLine("PASS desk strain: " + label);
    }

    public static void Run()
    {
        var previous = Loc.Language;
        Loc.Language = "english";
        try
        {
            Check(SaveSystem.FormatVersion == 2, "save format stays 2");
            QuietWithoutTheFact();
            LikedRegionStaysQuiet();
            StrainNamesTheFact();
            RussianLine();
            NotOnTheOtherChairs();
        }
        finally { Loc.Language = previous; }
    }

    static void QuietWithoutTheFact()
    {
        var campaign = Bare();
        var nation = campaign.Nations[0];
        var before = CampaignFingerprint.Of(campaign);
        var line = Find(campaign, nation);
        Check(string.IsNullOrEmpty(line.Id) && string.IsNullOrEmpty(line.Title),
            "a country with no reviewed region has no strain line");
        Check(line.Title != "0" && (line.Tooltip ?? "") != "0",
            "the quiet chair does not invent a zero");
        Check(SituationReport.PoliticalStrain(campaign, nation) is null,
            "the situation report is quiet on the same country");
        Check(CampaignFingerprint.Of(campaign) == before, "reading the quiet line does not change the fingerprint");
        Check(nation.Treasury == 0f, "reading the quiet line does not move money");
    }

    static void LikedRegionStaysQuiet()
    {
        var campaign = Bare();
        var nation = campaign.Nations[0];
        AddRegion(campaign, nation, government: 0.80f, divergence: 0.10f, law: 0.80f);
        var before = CampaignFingerprint.Of(campaign);
        var line = Find(campaign, nation);
        Check(string.IsNullOrEmpty(line.Id),
            "a region people still accept does not put a strain line on the desk");
        Check(CampaignFingerprint.Of(campaign) == before,
            "reading a liked region does not change the fingerprint");
    }

    static void StrainNamesTheFact()
    {
        var campaign = Bare();
        var nation = campaign.Nations[0];
        AddRegion(campaign, nation, government: 0.20f, divergence: 0.60f, law: 0.55f);
        var before = CampaignFingerprint.Of(campaign);
        var treasury = nation.Treasury;
        var owner = campaign.States[0].NationMapIndex;
        var line = Find(campaign, nation);
        Check(line.Id == "political_strain" && line.Title.Length > 0,
            "the strain fact makes a line: " + line.Title);
        Check(line.Title == "Loudest region: Deskedge",
            "the line is the report's loudest-region label: " + line.Title);
        Check(!line.Title.Contains("0") && line.Title != "Loudest region: —",
            "the line is not a fake zero");
        Check(line.Tooltip.Contains("The laws still hold. The government is losing the room."),
            "the tooltip keeps the report's sentence");
        Check(line.Tooltip.Contains("Law approval:") && line.Tooltip.Contains("Government approval:")
              && line.Tooltip.Contains("Political divergence:") && line.Tooltip.Contains("Loudest region:"),
            "the tooltip names the report's labels");
        var copies = 0;
        foreach (var item in SituationDesk.Read(campaign, nation, DeskLens.Society))
            if (item.Id == "political_strain") copies++;
        Check(copies == 1, "the loudest-region line is not repeated");
        Check(line.Lens == DeskLens.Society && line.Screen == "Laws" && line.Detail.Length == 0,
            "the line sits on the society chair and opens Laws, and it does not give an order");
        Check(StrategicHudBrief.Read(campaign, nation).TrueForAll(item => item.Id != "political_strain"),
            "the compact rail does not gain this line");
        Check(nation.Treasury == treasury && campaign.States[0].NationMapIndex == owner,
            "reading the line does not move money or land");
        Check(CampaignFingerprint.Of(campaign) == before, "reading the strain line does not change the fingerprint");
    }

    static void RussianLine()
    {
        var previous = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var campaign = Bare();
            AddRegion(campaign, campaign.Nations[0], government: 0.20f, divergence: 0.60f, law: 0.55f);
            var line = Find(campaign, campaign.Nations[0]);
            var label = Loc.Get("Loudest region");
            Check(line.Title == label + ": Deskedge",
                "the Russian line uses the loudest-region label, got [" + line.Title + "]");
            Check(label == "Самый громкий регион" && line.Title == "Самый громкий регион: Deskedge",
                "the Russian line is Самый громкий регион: Deskedge, label [" + label + "]");
            Check(line.Tooltip.Contains(Loc.Get("The laws still hold. The government is losing the room.")),
                "the Russian tooltip keeps the report's sentence");
            Check(line.Tooltip.Contains(Loc.Get("Law approval") + ":")
                  && line.Tooltip.Contains(Loc.Get("Government approval") + ":")
                  && line.Tooltip.Contains(Loc.Get("Political divergence") + ":")
                  && line.Tooltip.Contains(label + ":"),
                "the Russian tooltip names the report's labels");
            Check(Loc.Get("Law approval") == "Принятие законов"
                  && Loc.Get("Government approval") == "Принятие правительства"
                  && Loc.Get("Political divergence") == "Политическое расхождение"
                  && Loc.Get("The laws still hold. The government is losing the room.")
                      == "Законы ещё держатся. Правительство теряет зал.",
                "the Russian labels are Принятие законов, Принятие правительства, Политическое расхождение");
            var quiet = Bare();
            var missing = Find(quiet, quiet.Nations[0]);
            Check(string.IsNullOrEmpty(missing.Title), "the Russian chair is quiet when the fact is absent");
        }
        finally { Loc.Language = previous; }
    }

    static void NotOnTheOtherChairs()
    {
        var campaign = Bare();
        AddRegion(campaign, campaign.Nations[0], government: 0.20f, divergence: 0.60f, law: 0.55f);
        foreach (var lens in new[] { DeskLens.Security, DeskLens.Economy, DeskLens.Diplomacy, DeskLens.Cabinet })
        {
            var items = SituationDesk.Read(campaign, campaign.Nations[0], lens);
            Check(!items.Exists(item => item.Id == "political_strain"),
                lens + " does not take the strain line");
        }
    }

    static DeskItem Find(Campaign campaign, Nation nation)
    {
        foreach (var item in SituationDesk.Read(campaign, nation, DeskLens.Society))
            if (item.Id == "political_strain") return item;
        return default;
    }

    static void AddRegion(Campaign campaign, Nation nation, float government, float divergence, float law)
    {
        campaign.States.Add(new ScenarioState
        {
            MapIndex = 7,
            NationMapIndex = nation.MapIndex,
            Name = "Deskedge",
            Population = 400_000,
            PoliticalDivergence = divergence,
            LawApproval = law,
            GovernmentApproval = government,
            Radicalism = 0.05f,
            StandardOfLiving = 0.5f
        });
        nation.Stability = 0.80f;
        nation.StandardOfLiving = 0.5f;
        nation.ShortageIndex = 0f;
        campaign.InvalidateStateIndex();
    }

    static Campaign Bare()
    {
        var campaign = new Campaign(30);
        campaign.Nations.Add(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "H0", Name = "Home", Population = 2_000_000,
            Stability = 0.8f, Gdp = 0f, Treasury = 0f, Inflation = 0f, ShortageIndex = 0f,
            StandardOfLiving = 0.5f, IsHuman = false
        });
        Check(campaign.Nations[0].Id == 0 && campaign.NationById(0) == campaign.Nations[0],
            "nation id 0 stands at index 0");
        var flags = BindingFlags.Instance | BindingFlags.Public;
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap), flags)!.SetValue(campaign, null);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController), flags)!.SetValue(campaign, new ushort[1]);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation), flags)!.SetValue(campaign, new float[1]);
        Check(campaign.ProvinceController.Length == 1 && campaign.ProvinceOccupation.Length == 1,
            "the province arrays are attached by reflection and not by AttachProvinceTopology");
        return campaign;
    }
}
