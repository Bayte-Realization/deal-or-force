using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The situation room names the player's hottest crisis or clashes, and says nothing
/// when there is none. Reading that line does not write the campaign.
/// </summary>
public static class SituationStandoffTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL situation standoff: " + text);
        Console.WriteLine("PASS situation standoff: " + text);
    }

    public static void Run()
    {
        var quiet = Pair();
        Check(quiet.Nations.Count > 0 && quiet.Nations[0].Id == 0 && quiet.ProvinceMap is null,
            "nation id 0 stands at index 0, with no province topology");
        var before = CampaignFingerprint.Of(quiet);
        Check(SituationReport.Read(quiet, quiet.Nations[0]).All(item => !item.Id.StartsWith("standoff:")),
            "a campaign with no tension produces no standoff");
        Check(CampaignFingerprint.Of(quiet) == before, "reading a calm room does not change the fingerprint");

        var foreign = Pair();
        var outsiders = Relation(foreign, 2, 3);
        outsiders.Tension = 90f;
        outsiders.TensionStage = (int)TensionStage.Clashes;
        before = CampaignFingerprint.Of(foreign);
        Check(SituationReport.Read(foreign, foreign.Nations[0]).All(item => !item.Id.StartsWith("standoff:")),
            "a crisis that does not involve the player is not added for the player");
        Check(CampaignFingerprint.Of(foreign) == before, "reading past a foreign crisis does not change the fingerprint");

        var crisis = Pair();
        var relation = Relation(crisis, 0, 1);
        relation.Tension = 55f;
        relation.TensionStage = (int)TensionStage.Crisis;
        before = CampaignFingerprint.Of(crisis);
        var item = SituationReport.Read(crisis, crisis.Nations[0]).Single(entry => entry.Id == "standoff:1");
        var text = Blob(item);
        Check(text.Contains("Crisis") && item.TargetId == 1 && item.Action.Contains("Hold talks"),
            "a crisis names the stage and offers talks: " + item.Summary + " / " + item.Action);
        var again = SituationReport.Read(crisis, crisis.Nations[0]).Single(entry => entry.Id == "standoff:1");
        Check(CampaignFingerprint.Of(crisis) == before && again.Summary == item.Summary,
            "reading the crisis does not change the fingerprint");

        relation.Tension = 80f;
        relation.TensionStage = (int)TensionStage.Clashes;
        relation.LastIncident = "artillery_duel";
        relation.TalksUntilDay = crisis.ElapsedDays + 9;
        before = CampaignFingerprint.Of(crisis);
        item = SituationReport.Read(crisis, crisis.Nations[0]).Single(entry => entry.Id == "standoff:1");
        text = Blob(item);
        Check(text.Contains("Armed clashes") && text.Contains("9") &&
               (text.Contains("Artillery duel") || text.Contains("artillery_duel")),
            "clashes name the stage, the talks clock and the last incident: " + item.Summary);
        Check(item.Action.Contains("deal inbox"),
            "talks already under way offer the deal inbox: " + item.Action);
        Check(CampaignFingerprint.Of(crisis) == before, "reading the clashes line does not change the fingerprint");

        relation.Tension = 30f;
        relation.TensionStage = (int)TensionStage.Friction;
        relation.TalksUntilDay = -1;
        relation.LastIncident = "";
        Check(SituationReport.Read(crisis, crisis.Nations[0]).All(entry => !entry.Id.StartsWith("standoff:")),
            "friction below a crisis is not a situation item");

        var border = Pair();
        var borderRelation = Relation(border, 0, 1);
        borderRelation.Tension = 10f;
        borderRelation.TensionStage = (int)TensionStage.War;
        border.Wars.Add(new StrategicWar
        {
            Id = 1, Name = "Border", AttackerId = 1, DefenderId = 0,
            Limited = true, LimitedUntilDay = border.ElapsedDays + 11, StartedDay = 0
        });
        before = CampaignFingerprint.Of(border);
        item = SituationReport.Read(border, border.Nations[0]).Single(entry => entry.Id == "standoff:1");
        text = Blob(item);
        Check((text.Contains("At war") || text.Contains("Armed clashes")) && text.Contains("11") &&
               item.Action.Contains("deal inbox"),
            "a limited war names the ceasefire day and opens the inbox: " + item.Summary + " / " + item.Action);
        Check(CampaignFingerprint.Of(border) == before, "reading the ceasefire line does not change the fingerprint");

        crisis.SetFeature("escalation", false);
        Check(SituationReport.Read(crisis, crisis.Nations[0]).All(entry => !entry.Id.StartsWith("standoff:")),
            "disabled escalation produces no standoff");
    }

    private static string Blob(SituationItem item) =>
        item.Title + "\n" + item.Summary + "\n" + item.Action + "\n" +
        string.Join("\n", item.Facts.Select(fact => fact.Label + " " + fact.Value)) + "\n" +
        string.Join("\n", item.Causes);

    private static Campaign Pair()
    {
        var campaign = new Campaign(24024) { ScenarioId = "situation_standoff" };
        campaign.Nations.Add(new Nation
        {
            Id = 0, Tag = "P0", Name = "Playerland", CustomName = "Playerland",
            MapIndex = 1, Population = 2000000, Gdp = 200, Treasury = 50, Stability = 0.9f, IsHuman = true
        });
        campaign.Nations.Add(new Nation
        {
            Id = 1, Tag = "R1", Name = "Rivalia", CustomName = "Rivalia",
            MapIndex = 2, Population = 2000000, Gdp = 180, Treasury = 40, Stability = 0.9f
        });
        return campaign;
    }

    private static DiplomaticRelation Relation(Campaign campaign, int a, int b)
    {
        if (campaign.NationById(a) is null)
            campaign.Nations.Add(new Nation
            {
                Id = a, Tag = "N" + a, Name = "Outsider " + a, CustomName = "Outsider " + a,
                MapIndex = 10 + a, Population = 1000000, Stability = 0.9f
            });
        if (campaign.NationById(b) is null)
            campaign.Nations.Add(new Nation
            {
                Id = b, Tag = "N" + b, Name = "Outsider " + b, CustomName = "Outsider " + b,
                MapIndex = 10 + b, Population = 1000000, Stability = 0.9f
            });
        return CampaignDiplomacy.Get(campaign, a, b);
    }
}
