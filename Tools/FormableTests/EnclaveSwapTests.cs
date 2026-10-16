using System.Linq;
using DealOrForce.Simulation;

static class EnclaveSwapTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("enclave swap: " + label);
        System.Console.WriteLine("PASS enclave swap: " + label);
    }

    public static void Run()
    {
        var (campaign, map, home, other) = World(giveBack: true);
        var before = CampaignFingerprint.Of(campaign);
        Check(Corridors.TryReportSwap(campaign, home, out var report)
            && report.OurProvinces == 1 && report.TheirProvinces == 1 && report.Partner.Length > 0,
            "the two enclaves are named");
        Check(CampaignFingerprint.Of(campaign) == before, "naming the exchange does not sign it");
        var room = SituationReport.Read(campaign, home);
        Check(room.Any(item => item.Id == "enclave_swap") && CampaignFingerprint.Of(campaign) == before,
            "the situation names the exchange and still signs nothing");

        var (oneSided, oneMap, oneHome, _) = World(giveBack: false);
        Check(!Corridors.TryReportSwap(oneSided, oneHome, out _),
            "a dot with nothing to give back is not an exchange");
        Check(Owner(oneMap, 3) == 2, "refusing the exchange leaves their dot");

        other.IsHuman = true;
        var asked = Corridors.ProposeSwap(campaign, home);
        Check(Owner(map, 1) == 1 && Owner(map, 9) == 2, "the countries around the dots stay: " + asked);
        Check(Owner(map, 3) == 2 && Owner(map, 4) == 1, "a player is not stripped of the dot: " + asked);
        Check(campaign.DealOffers.Any(offer => offer.Pending && offer.FromId == home.Id && offer.ToId == other.Id
            && offer.From.Regions.Contains(4) && offer.To.Regions.Contains(3)),
            "the paper asks for both pieces: " + asked);

        var (governments, governmentMap, buyer, _) = World(giveBack: true);
        var signed = Corridors.ProposeSwap(governments, buyer);
        Check(signed.Length == 0, "two governments sign: " + signed);
        Check(Owner(governmentMap, 3) == 1 && Owner(governmentMap, 4) == 2, "the dots change hands");
        Check(Owner(governmentMap, 1) == 1 && Owner(governmentMap, 9) == 2, "the mainlands stay");
    }

    static int Owner(ProvinceTopology map, ushort region)
    {
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToState[province] == region) return map.ProvinceToNation[province];
        return 0;
    }

    static (Campaign Campaign, ProvinceTopology Map, Nation Home, Nation Other) World(bool giveBack)
    {
        var campaign = new Campaign(20261015);
        var home = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "AAA", Name = "Home", Population = 100,
            Treasury = 40f, IsHuman = false, CapitalStateMapIndex = 1
        };
        var other = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BBB", Name = "Other", Population = 100,
            Treasury = 40f, IsHuman = false, CapitalStateMapIndex = 9
        };
        campaign.Nations.Add(home);
        campaign.Nations.Add(other);
        const int width = 9, height = 7;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        for (var y = 1; y <= 3; y++)
            for (var x = 1; x <= 4; x++)
                Cell(x, y, 1, 1);
        Cell(2, 2, 3, 2);
        for (var y = 4; y <= 6; y++)
            for (var x = 5; x <= 7; x++)
                Cell(x, y, 9, 2);
        if (giveBack) Cell(6, 5, 4, 1);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        campaign.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "home", Name = "Home", Population = 40 });
        campaign.States.Add(new ScenarioState { MapIndex = 3, NationMapIndex = 2, Id = "dot", Name = "Dot", Population = 8 });
        campaign.States.Add(new ScenarioState { MapIndex = 9, NationMapIndex = 2, Id = "kept", Name = "Kept", Population = 40 });
        if (giveBack)
            campaign.States.Add(new ScenarioState { MapIndex = 4, NationMapIndex = 1, Id = "scrap", Name = "Scrap", Population = 8 });
        return (campaign, map, home, other);
    }
}
