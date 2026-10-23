using System.Linq;
using DealOrForce.Simulation;

static class PlayerUltimatumTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("player ultimatum: " + label);
        System.Console.WriteLine("PASS player ultimatum: " + label);
    }

    public static void Run()
    {
        var (campaign, map, buyer, seller) = World(sellerHuman: true);
        var before = CampaignFingerprint.Of(campaign);
        Check(AiStatecraft.TryUltimatumFirst(campaign, buyer, seller), "the demand is put on the desk");
        Check(Owner(map, 2) == 2 && Owner(map, 1) == 1 && Owner(map, 9) == 2, "nothing moves when the paper arrives");
        Check(campaign.UltimatumPapers.Count(paper => paper.Pending && paper.ToId == seller.Id) == 1, "one paper is waiting");
        var after = CampaignFingerprint.Of(campaign);
        Check(after != before, "a waiting ultimatum is part of the world");
        var room = SituationReport.Read(campaign, seller);
        Check(room.Any(item => item.Id.StartsWith("ultimatum:")) && CampaignFingerprint.Of(campaign) == after,
            "reading the situation does not answer it");

        seller.IsHuman = false;
        Ultimatum.Tick(campaign);
        Check(Owner(map, 2) == 2 && campaign.UltimatumPapers.Single().Pending,
            "losing the player's hand does not sign the land over");

        var refused = new AnswerUltimatumCommand { IssuerId = seller.Id, PaperId = campaign.UltimatumPapers.Single().Id, Reply = Ultimatum.Answer.Refuse };
        refused.Apply(campaign);
        Check(Owner(map, 2) == 2, "a refusal keeps the region");
        Check(Statecraft.CasusBelli(campaign, buyer, seller) == "ultimatum_refusal", "the refusal is a reason for war");

        var (yielded, yieldMap, demander, holder) = World(sellerHuman: true);
        Check(AiStatecraft.TryUltimatumFirst(yielded, demander, holder), "a second desk is laid");
        var yield = new AnswerUltimatumCommand
        {
            IssuerId = holder.Id, PaperId = yielded.UltimatumPapers.Single().Id, Reply = Ultimatum.Answer.Yield
        };
        yield.Apply(yielded);
        Check(Owner(yieldMap, 2) == 1 && Owner(yieldMap, 9) == 2, "yielding gives the asked region and leaves the capital");

        var (quiet, quietMap, from, human) = World(sellerHuman: true);
        Check(AiStatecraft.TryUltimatumFirst(quiet, from, human), "a third desk is laid");
        quiet.UltimatumPapers.Single().ExpiresDay = quiet.ElapsedDays;
        Ultimatum.Tick(quiet);
        Check(Owner(quietMap, 2) == 2 && Statecraft.CasusBelli(quiet, from, human) == "ultimatum_refusal",
            "silence refuses the demand and leaves the land");

        var (governments, governmentMap, left, right) = World(sellerHuman: false);
        Check(AiStatecraft.TryUltimatumFirst(governments, left, right), "a government is answered at once");
        Check(!governments.UltimatumPapers.Any(paper => paper.Pending), "a government is not left a paper");
        var moved = Owner(governmentMap, 2) == 1;
        var refusedCause = Statecraft.CasusBelli(governments, left, right) == "ultimatum_refusal";
        Check(moved || refusedCause, "they yield the region or refuse it: moved=" + moved + " refused=" + refusedCause);
        Check(Owner(governmentMap, 9) == 2 && Owner(governmentMap, 1) == 1, "the countries around the region stay");

        seller.IsHuman = true;
        Check(!Ultimatum.Resolve(campaign, buyer, seller, new Ultimatum.Demand { Regions = new[] { 2 } }, Ultimatum.Answer.Yield).Accepted,
            "the automatic answer still refuses to speak for a player");
    }

    static int Owner(ProvinceTopology map, ushort region)
    {
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToState[province] == region) return map.ProvinceToNation[province];
        return 0;
    }

    static (Campaign Campaign, ProvinceTopology Map, Nation Buyer, Nation Seller) World(bool sellerHuman)
    {
        var campaign = new Campaign(20261015);
        var buyer = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "POL", Name = "Poland", Population = 100_000,
            Treasury = 40f, Gdp = 80f, IsHuman = false, CapitalStateMapIndex = 1
        };
        var seller = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BLR", Name = "Belarus", Population = 100_000,
            Treasury = 40f, Gdp = 40f, IsHuman = sellerHuman, CapitalStateMapIndex = 9
        };
        campaign.Nations.Add(buyer);
        campaign.Nations.Add(seller);
        const int width = 5, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        Cell(1, 1, 1, 1);
        Cell(2, 1, 2, 2);
        Cell(4, 1, 9, 2);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, (ushort[])map.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(campaign, new float[map.ProvinceCount + 1]);
        CampaignDiplomacy.ForgetBorders();
        var kin = new ScenarioState { MapIndex = 2, NationMapIndex = 2, Id = "lviv", Name = "Lviv", Population = 10 };
        kin.PopulationGroups.Add(new PopulationGroup { Culture = "polish", Size = 9 });
        kin.PopulationGroups.Add(new PopulationGroup { Culture = "belarusian", Size = 1 });
        campaign.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "warsaw", Name = "Warsaw", Population = 40 });
        campaign.States.Add(kin);
        campaign.States.Add(new ScenarioState { MapIndex = 9, NationMapIndex = 2, Id = "minsk", Name = "Minsk", Population = 30 });
        return (campaign, map, buyer, seller);
    }
}
