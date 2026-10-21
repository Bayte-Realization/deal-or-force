using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

static class CrisisGrantTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("crisis grant: " + name);
        Console.WriteLine("PASS crisis grant: " + name);
    }

    public static void Run()
    {
        HumanKeepsTheLandUntilTheySign();
        AiClientIsPaidWithoutMovingABorder();
        AStrangerAndAHealthyClientAreLeftAlone();
        TheRuleCanBeSwitchedOff();
        ASecondAskWaits();
        OnlyAGrantClockChangesTheFingerprint();
    }

    static Campaign World()
    {
        var campaign = new Campaign(21);
        var patron = new Nation { Id = 0, MapIndex = 1, Tag = "PAT", Name = "Patron", Population = 5_000_000,
            Stability = 0.9f, Gdp = 200f, Treasury = 500f, CapitalStateMapIndex = 10 };
        var client = new Nation { Id = 1, MapIndex = 2, Tag = "CLI", Name = "Client", Population = 5_000_000,
            Stability = 0.1f, Gdp = 200f, Treasury = 40f, CapitalStateMapIndex = 20 };
        campaign.Nations.Add(patron);
        campaign.Nations.Add(client);
        campaign.States.Add(new ScenarioState { MapIndex = 10, NationMapIndex = 1, Population = 5_000_000, Name = "Patron home" });
        campaign.States.Add(new ScenarioState { MapIndex = 20, NationMapIndex = 2, Population = 5_000_000, Name = "Client home" });
        campaign.Defines.CivilWarMinimumRegions = 1;
        campaign.Defines.CivilWarMinimumPopulation = 1000;
        campaign.Defines.CivilWarStabilityThreshold = 0.32f;
        var state = new ushort[] { 0, 10, 20 };
        var owner = new ushort[] { 0, 1, 2 };
        SetMap(campaign, state, owner);
        var relation = CampaignDiplomacy.Get(campaign, patron.Id, client.Id);
        relation.Stance = DiplomaticStance.Neutral;
        relation.Opinion = 20f;
        relation.GuaranteeByA = true;
        return campaign;
    }

    static void SetMap(Campaign campaign, ushort[] state, ushort[] owner)
    {
        var count = state.Length;
        var neighbors = new int[count][];
        for (var i = 0; i < count; i++) neighbors[i] = Array.Empty<int>();
        var constructor = typeof(ProvinceTopology).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var map = (ProvinceTopology)constructor.Invoke(new object[]
        {
            new ushort[1], state, owner, new int[count], neighbors, 1, 1
        });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).SetValue(campaign, (ushort[])owner.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation)).SetValue(campaign, new float[count]);
    }

    static void HumanKeepsTheLandUntilTheySign()
    {
        var campaign = World();
        var patron = campaign.NationById(0);
        var client = campaign.NationById(1);
        Check(patron.Id == 0 && campaign.Nations[0] == patron, "nation id 0 stays at index 0");
        client.IsHuman = true;
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        var patronCash = patron.Treasury;
        var clientCash = client.Treasury;
        Check(AiStatecraft.TryOfferCrisisGrant(campaign, patron), "a guarantor offers when the client's institutions are failing");
        var paper = campaign.DealOffers.Single(offer => offer.Pending && offer.ToId == client.Id);
        Check(paper.From.Cash > 0f && (paper.From.Regions?.Length ?? 0) == 0 && (paper.To.Regions?.Length ?? 0) == 0
            && paper.FromProvinces.Length == 0 && paper.ToProvinces.Length == 0,
            "the paper is cash and no land");
        Check(patron.Treasury == patronCash && client.Treasury == clientCash, "nothing is paid before the player signs");
        Check(campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners), "the player's provinces stay where they were");
    }

    static void AiClientIsPaidWithoutMovingABorder()
    {
        var campaign = World();
        var patron = campaign.NationById(0);
        var client = campaign.NationById(1);
        var owners = (ushort[])campaign.ProvinceMap.ProvinceToNation.Clone();
        var before = patron.Treasury + client.Treasury;
        Check(AiStatecraft.TryOfferCrisisGrant(campaign, patron), "an AI client can accept the same grant");
        Check(client.Treasury > 40f && patron.Treasury < 500f, "the accepted grant moves cash");
        Check(MathF.Abs(patron.Treasury + client.Treasury - before) < 0.001f, "the grant does not create or destroy money");
        Check(campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owners), "an accepted grant does not move a border");
        Check(campaign.DealOffers.All(offer => offer.FromProvinces.Length == 0 && offer.ToProvinces.Length == 0),
            "the signed paper still names no provinces");
    }

    static void AStrangerAndAHealthyClientAreLeftAlone()
    {
        var stranger = World();
        CampaignDiplomacy.Get(stranger, 0, 1).GuaranteeByA = false;
        Check(!AiStatecraft.TryOfferCrisisGrant(stranger, stranger.NationById(0)), "a country that does not stand behind the client offers nothing");
        Check(stranger.DealOffers.Count == 0, "no paper is written for a stranger");

        var healthy = World();
        healthy.NationById(1).Stability = 0.8f;
        Check(!AiStatecraft.TryOfferCrisisGrant(healthy, healthy.NationById(0)), "a steady client is not offered a stabilization grant");
    }

    static void TheRuleCanBeSwitchedOff()
    {
        var campaign = World();
        campaign.Defines.AiCrisisGrants = false;
        Check(!AiStatecraft.TryOfferCrisisGrant(campaign, campaign.NationById(0)), "the define turns the grant off");
        Check(campaign.DealOffers.Count == 0, "a switched-off rule writes no offer");
    }

    static void ASecondAskWaits()
    {
        var campaign = World();
        var patron = campaign.NationById(0);
        Check(AiStatecraft.TryOfferCrisisGrant(campaign, patron), "the first grant is made");
        var cash = patron.Treasury;
        Check(!AiStatecraft.TryOfferCrisisGrant(campaign, patron), "the same client is not paid again inside the cooldown");
        Check(patron.Treasury == cash, "the cooldown keeps the treasury still");
    }

    static void OnlyAGrantClockChangesTheFingerprint()
    {
        var plain = new Campaign(3);
        plain.Nations.Add(new Nation { Id = 0, Tag = "PAT" });
        var kindred = new Campaign(3);
        var withKindred = new Nation { Id = 0, Tag = "PAT" };
        withKindred.DecisionAvailableDay["kindred/1"] = 40;
        kindred.Nations.Add(withKindred);
        var granted = new Campaign(3);
        var withGrant = new Nation { Id = 0, Tag = "PAT" };
        withGrant.DecisionAvailableDay["crisis_grant/1"] = 40;
        granted.Nations.Add(withGrant);
        Check(CampaignFingerprint.Of(plain) == CampaignFingerprint.Of(kindred),
            "an unrelated diplomatic clock still hashes as it did");
        Check(CampaignFingerprint.Of(plain) != CampaignFingerprint.Of(granted),
            "a stabilization cooldown is part of the fingerprint");
        granted.Defines.AiCrisisGrants = false;
        var tuned = CampaignFingerprint.Of(granted);
        granted.Defines.AiCrisisGrants = true;
        granted.Defines.AiCrisisGrantRetryDays = 360;
        Check(tuned != CampaignFingerprint.Of(granted), "switching the grant rule off is visible to peers");
    }
}
