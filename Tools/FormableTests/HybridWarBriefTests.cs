using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The diplomacy dossier names a sponsorship or covert operation the player is in,
/// and says nothing when there is none. Reading those sentences does not write the campaign.
/// </summary>
public static class HybridWarBriefTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL hybrid war: " + text);
        Console.WriteLine("PASS hybrid war: " + text);
    }

    public static void Run()
    {
        var quiet = World();
        Check(quiet.Nations.Count > 0 && quiet.Nations[0].Id == 0 && quiet.ProvinceMap is null,
            "nation id 0 stands at index 0, with no province topology");
        var before = CampaignFingerprint.Of(quiet);
        Check(HybridWarBrief.Read(quiet, quiet.Nations[0], quiet.Nations[0]).Count == 0,
            "a campaign with no sponsorship and no covert operation produces no row");
        Check(CampaignFingerprint.Of(quiet) == before, "reading an empty dossier does not change the fingerprint");

        var armed = World();
        armed.Wars.Add(new StrategicWar { Id = 3, Name = "Border", AttackerId = 2, DefenderId = 1, StartedDay = 0 });
        armed.Sponsorships.Add(new Sponsorship
        {
            Id = 7, SponsorId = 0, ClientId = 1, WarId = 3, Level = 2, StartedDay = 0
        });
        armed.Chronicle.Record(armed, "proxy_started", 0, 1, "Border");
        var sponsorships = armed.Sponsorships.Count;
        var notes = armed.Chronicle.Entries.Count;
        var nextId = armed.NextSponsorshipId;
        before = CampaignFingerprint.Of(armed);
        var row = HybridWarBrief.Read(armed, armed.Nations[0], armed.Nations[0]).Single();
        Check(row.Id == "proxy:7" && row.OtherName == "Rivalia" && row.Sentence.Contains("Rivalia") &&
               row.KindName.Contains("Weapons") && row.Chronicle.Length > 0 && row.Sentence.Contains(row.Chronicle) &&
               !row.Covert,
            "an active sponsorship names the client and the kind: " + row.Sentence);
        Check(armed.Sponsorships.Count == sponsorships && armed.Chronicle.Entries.Count == notes &&
               armed.NextSponsorshipId == nextId && armed.Nations[0].CovertOperations.Count == 0 &&
               CampaignFingerprint.Of(armed) == before,
            "reading the sponsorship does not start another and does not change the fingerprint");
        var onTheirPage = HybridWarBrief.Read(armed, armed.Nations[0], armed.Nations[1]).Single();
        Check(onTheirPage.Id == row.Id && onTheirPage.Sentence == row.Sentence,
            "the other country's dossier says the same sponsorship once");
        Check(HybridWarBrief.Read(armed, armed.Nations[0], armed.NationById(2)).Count == 0,
            "a country outside the sponsorship gets no row");

        armed.Sponsorships[0].Ended = true;
        Check(HybridWarBrief.Read(armed, armed.Nations[0], armed.Nations[0]).Count == 0,
            "an ended sponsorship produces no row");

        var client = World();
        client.Sponsorships.Add(new Sponsorship
        {
            Id = 4, SponsorId = 1, ClientId = 0, WarId = -1, Level = 1, StartedDay = 0
        });
        row = HybridWarBrief.Read(client, client.Nations[0], client.Nations[0]).Single();
        Check(row.Incoming && row.OtherName == "Rivalia" && row.Sentence.Contains("Rivalia") &&
               (row.KindName.Contains("Advisers") || row.KindName.Contains("money")),
            "a client names the sponsor and the kind: " + row.Sentence);

        var strangers = World();
        strangers.Sponsorships.Add(new Sponsorship
        {
            Id = 9, SponsorId = 2, ClientId = 3, WarId = -1, Level = 3, StartedDay = 0
        });
        Check(HybridWarBrief.Read(strangers, strangers.Nations[0], strangers.Nations[0]).Count == 0,
            "a sponsorship between other countries produces no row");

        var covert = World();
        covert.Nations[0].CovertOperations.Add(new CovertOperation
        {
            Kind = "sabotage", TargetId = 1, RegionMapIndex = -1, StartedDay = 0, DueDay = 21, Spent = 1f
        });
        covert.Chronicle.Record(covert, "covert_sabotage_done", 0, 1, "Sabotage their industry", "Works", isPublic: false);
        var operations = covert.Nations[0].CovertOperations.Count;
        notes = covert.Chronicle.Entries.Count;
        before = CampaignFingerprint.Of(covert);
        row = HybridWarBrief.Read(covert, covert.Nations[0], covert.Nations[0]).Single();
        Check(row.Covert && row.OtherName == "Rivalia" && row.Sentence.Contains("Rivalia") &&
               (row.KindName.Contains("Sabotage") || row.KindName.Contains("sabotage")) &&
               row.Chronicle.Length > 0 && row.Sentence.Contains(row.Chronicle),
            "an operation the player is running names the target and the kind: " + row.Sentence);
        Check(covert.Nations[0].CovertOperations.Count == operations && covert.Chronicle.Entries.Count == notes &&
               CampaignFingerprint.Of(covert) == before,
            "reading the operation does not order another and does not change the fingerprint");

        var against = World();
        against.Nations[1].CovertOperations.Add(new CovertOperation
        {
            Kind = "opposition", TargetId = 0, StartedDay = 0, DueDay = 40, Spent = 1f
        });
        before = CampaignFingerprint.Of(against);
        row = HybridWarBrief.Read(against, against.Nations[0], against.Nations[0]).Single();
        Check(row.Covert && row.Incoming && row.OtherName == "Rivalia" && row.Sentence.Contains("Rivalia") &&
               (row.KindName.Contains("opposition") || row.KindName.Contains("Fund")),
            "an operation against the player names the other country: " + row.Sentence);
        Check(against.Nations[1].CovertOperations.Count == 1 && CampaignFingerprint.Of(against) == before,
            "reading an operation aimed at the player does not add one");

        var elsewhere = World();
        elsewhere.Nations[2].CovertOperations.Add(new CovertOperation
        {
            Kind = "theft", TargetId = 3, StartedDay = 0, DueDay = 30, Spent = 1f
        });
        Check(HybridWarBrief.Read(elsewhere, elsewhere.Nations[0], elsewhere.Nations[0]).Count == 0,
            "an operation between other countries produces no row");

        armed.Sponsorships[0].Ended = false;
        armed.FeatureOverrides["proxy_wars"] = false;
        armed.FeatureOverrides["intelligence"] = false;
        armed.Nations[0].CovertOperations.Add(new CovertOperation
        {
            Kind = "sabotage", TargetId = 1, StartedDay = 0, DueDay = 21, Spent = 1f
        });
        before = CampaignFingerprint.Of(armed);
        Check(HybridWarBrief.Read(armed, armed.Nations[0], armed.Nations[0]).Count == 0,
            "disabled proxy wars and intelligence produce no row");
        Check(CampaignFingerprint.Of(armed) == before, "reading a disabled dossier does not change the fingerprint");
    }

    private static Campaign World()
    {
        var campaign = new Campaign(24024) { ScenarioId = "hybrid_war" };
        campaign.Nations.Add(Nation(0, "P0", "Playerland"));
        campaign.Nations.Add(Nation(1, "R1", "Rivalia"));
        campaign.Nations.Add(Nation(2, "O2", "Outsider"));
        campaign.Nations.Add(Nation(3, "O3", "Farland"));
        return campaign;
    }

    private static Nation Nation(int id, string tag, string name) => new()
    {
        Id = id, Tag = tag, Name = name, CustomName = name,
        MapIndex = id + 1, Population = 2000000, Gdp = 200, Treasury = 40, Stability = 0.9f,
        IsHuman = id == 0
    };
}
