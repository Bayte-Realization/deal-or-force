using System;
using DealOrForce.Simulation;

/// <summary>
/// A willingness probe reads a number. It does not insert a relation, write the chronicle,
/// or change a stance. The decision that actually asks is what opens the tie, once.
/// </summary>
static class AllianceWillingnessReadTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("alliance read: " + label);
        Console.WriteLine("PASS alliance read: " + label);
    }

    public static void Run()
    {
        ProbeDoesNotCreate();
        ExistingRelationKeepsItsAnswer();
        WarmScanFollowsAllianceChanges();
        DecisionOpensOnce();
    }

    static Campaign World()
    {
        var campaign = new Campaign(61118);
        campaign.AddNation(new Nation
        {
            Tag = "ASK", Name = "Asker", MapIndex = 11, Population = 4_000_000, Gdp = 80,
            Treasury = 20, Stability = 0.7f, Ideology = "liberalism", CapitalLatitude = 10,
            CapitalLongitude = 10
        });
        campaign.AddNation(new Nation
        {
            Tag = "ANS", Name = "Answer", MapIndex = 12, Population = 5_000_000, Gdp = 90,
            Treasury = 20, Stability = 0.7f, Ideology = "liberalism", CapitalLatitude = 12,
            CapitalLongitude = 12
        });
        return campaign;
    }

    static void ProbeDoesNotCreate()
    {
        var campaign = World();
        var asked = campaign.Nations[0];
        var asking = campaign.Nations[1];
        var chronicle = campaign.Chronicle.Entries.Count;
        var assessed = asking.AssessedDay;
        Check(CampaignDiplomacy.Peek(campaign, asked.Id, asking.Id) is null, "the pair starts with no relation");

        var scanned = AllianceSystem.Willingness(campaign, asked, asking, null, false, new AllianceSystem.OfferScan(campaign));
        Check(campaign.Relations.Count == 0 && campaign.DormantRelations.Count == 0, "a scan probe inserts nothing");
        Check(campaign.Chronicle.Entries.Count == chronicle, "a scan probe writes no chronicle");
        Check(asking.AssessedDay == assessed && asked.AssessedDay == assessed, "a scan probe does not refresh an assessment");
        Check(float.IsFinite(scanned), "a scan probe still returns a number");

        AllianceSystem.Willingness(campaign, asked, asking);
        Check(campaign.Relations.Count == 0 && campaign.DormantRelations.Count == 0,
            "refreshing the assessment still does not insert a relation");
        Check(campaign.Chronicle.Entries.Count == chronicle, "refreshing the assessment writes no chronicle");
        Check(CampaignDiplomacy.Peek(campaign, asked.Id, asking.Id) is null, "the pair still has no stance to change");
    }

    static void ExistingRelationKeepsItsAnswer()
    {
        var campaign = World();
        var asked = campaign.Nations[0];
        var asking = campaign.Nations[1];
        var relation = CampaignDiplomacy.Get(campaign, asked.Id, asking.Id);
        relation.Opinion = 40f;
        relation.Stance = DiplomaticStance.Neutral;
        var count = campaign.Relations.Count;

        var refreshed = AllianceSystem.Willingness(campaign, asked, asking);
        var read = AllianceSystem.Willingness(campaign, asked, asking, refreshAssessment: false);
        Check(refreshed == read, "an existing relation has one answer, with or without an assessment refresh: " + refreshed);
        Check(campaign.Relations.Count == count && ReferenceEquals(CampaignDiplomacy.Peek(campaign, asked.Id, asking.Id), relation),
            "reading an existing relation does not replace it");

        relation.Opinion = 80f;
        var warmer = AllianceSystem.Willingness(campaign, asked, asking, refreshAssessment: false);
        Check(MathF.Abs((warmer - read) - 0.4f) < 0.0001f,
            "the existing opinion is what the number is made of: " + read + " -> " + warmer);
        Check(relation.Stance == DiplomaticStance.Neutral, "reading does not change the stance");
    }

    static void DecisionOpensOnce()
    {
        var campaign = World();
        var proposer = campaign.Nations[0];
        var target = campaign.Nations[1];
        target.IsHuman = false;
        campaign.Defines.AllianceWillingness = -100f;
        var chronicle = campaign.Chronicle.Entries.Count;

        AllianceSystem.Willingness(campaign, target, proposer, refreshAssessment: false);
        Check(campaign.Relations.Count == 0, "the probe before the decision still inserts nothing");

        Check(AllianceSystem.Propose(campaign, proposer, target, out var answer), "the decision is accepted: " + answer);
        Check(campaign.Relations.Count == 1, "the decision opens one relation");
        var opened = CampaignDiplomacy.Peek(campaign, proposer.Id, target.Id);
        Check(opened is { Stance: DiplomaticStance.Allied }, "the opened relation is the alliance");

        Check(!AllianceSystem.Propose(campaign, proposer, target, out _), "a second ask does not open another");
        Check(campaign.Relations.Count == 1 && ReferenceEquals(CampaignDiplomacy.Peek(campaign, proposer.Id, target.Id), opened),
            "the alliance is still that one relation");
        AllianceSystem.Willingness(campaign, target, proposer);
        Check(campaign.Relations.Count == 1 && opened.Stance == DiplomaticStance.Allied,
            "a later probe does not open a second relation or drop the stance");
        Check(campaign.Chronicle.Entries.Count == chronicle, "opening the alliance does not invent a chronicle line");
    }

    static void WarmScanFollowsAllianceChanges()
    {
        var campaign = World();
        var asked = campaign.Nations[0];
        var asking = campaign.Nations[1];
        var ally = new Nation { Tag = "ALY", MapIndex = 13, Population = 3_000_000 };
        campaign.AddNation(ally);
        var bond = CampaignDiplomacy.Get(campaign, asking.Id, ally.Id);
        var key = ((long)asking.Id << 32) | (uint)ally.Id;
        campaign.Relations.Remove(key);
        bond.Dormant = true;
        campaign.DormantRelations.Add(key, bond);

        var scan = new AllianceSystem.OfferScan(campaign);
        var baseline = AllianceSystem.Willingness(campaign, asked, asking, null, false, scan);
        Check(scan.Allies(campaign, asking).Count == 0, "a neutral archived relation is not an ally");

        bond.Stance = DiplomaticStance.Allied;
        CampaignDiplomacy.Get(campaign, asked.Id, ally.Id).Opinion = -100f;
        var withAlly = AllianceSystem.Willingness(campaign, asked, asking, null, false, scan);
        Check(withAlly < baseline && withAlly == AllianceSystem.Willingness(campaign, asked, asking, refreshAssessment: false),
            "the warm scan counts an archived ally as soon as the stance changes");
        Check(bond.Dormant && campaign.DormantRelations.ContainsKey(key), "the scan does not wake an archived ally");

        CampaignDiplomacy.Get(campaign, asking.Id, ally.Id).Stance = DiplomaticStance.Neutral;
        Check(scan.Allies(campaign, asking).Count == 0, "the warm scan sees the alliance end after the relation wakes");
        Check(AllianceSystem.Willingness(campaign, asked, asking, null, false, scan) == baseline,
            "the score recovers when the alliance ends");
    }
}
