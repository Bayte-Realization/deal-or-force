using System;
using DealOrForce.Simulation;

/// <summary>
/// A rail tooltip adds one line of facts only when the news is important and a fact is
/// already on the books. Formatting that line does not record anything.
/// </summary>
public static class NewsFactTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL news facts: " + text);
        Console.WriteLine("PASS news facts: " + text);
    }

    private static Campaign Pair(int seed)
    {
        var campaign = new Campaign(seed) { ScenarioId = "news_facts" };
        campaign.Nations.Add(new Nation
        {
            Id = 0, Tag = "NF0", Name = "Reader", MapIndex = 1, Population = 1000000, IsHuman = true
        });
        campaign.Nations.Add(new Nation
        {
            Id = 1, Tag = "NF1", Name = "Harbour", MapIndex = 2, Population = 1000000
        });
        return campaign;
    }

    private static NewsFeed.Line AsLine(Campaign campaign, string kind)
    {
        ChronicleEntry found = null;
        foreach (var entry in campaign.Chronicle.Entries)
            if (entry.Kind == kind) found = entry;
        if (found is null) throw new Exception("missing chronicle entry " + kind);
        return new NewsFeed.Line(found.Sequence, found.Day, found.Kind, found.Kind, found.ActorId, found.TargetId);
    }

    public static void Run()
    {
        var language = Loc.Language;
        try
        {
            Loc.Language = "english";
            LowAndWar();
            Crisis();
            SubjectAndEmpty();
        }
        finally { Loc.Language = language; }
    }

    private static void LowAndWar()
    {
        var campaign = Pair(4401);
        Check(campaign.Nations.Count > 1 && campaign.Nations[0].Id == 0,
            "nation id 0 stands at index 0");
        campaign.Chronicle.Record(campaign, "aid_sent", 0, 1, subject: "hidden parcel");
        campaign.Chronicle.Record(campaign, "war_started", 0, 1);
        campaign.Wars.Add(new StrategicWar
        {
            Id = 1, Name = "Test war", AttackerId = 0, DefenderId = 1, OccupationScore = 4.5f
        });
        Check(NewsFeed.Importance("aid_sent") < 2 && NewsFeed.Importance("war_started") >= 2,
            "aid stays below the fact line and a war is above it");

        var entries = campaign.Chronicle.Entries.Count;
        var relations = campaign.Relations.Count;
        var wars = campaign.Wars.Count;
        var hash = CampaignFingerprint.Of(campaign);
        var low = NewsFeed.Context(campaign, AsLine(campaign, "aid_sent"));
        var high = NewsFeed.Context(campaign, AsLine(campaign, "war_started"));
        var again = NewsFeed.Context(campaign, AsLine(campaign, "war_started"));
        Check(low.Length == 0, "a low-importance item has no fact line even when a subject was stored: '" + low + "'");
        Check(high.Contains("occupation") && (high.Contains("+4.5") || high.Contains("+4,5")) && high == again,
            "a war headline names the largest ledger part already on the war: " + high);
        Check(CampaignFingerprint.Of(campaign) == hash &&
              campaign.Chronicle.Entries.Count == entries &&
              campaign.Relations.Count == relations &&
              campaign.Wars.Count == wars,
            "formatting the fact line does not change the fingerprint");
    }

    private static void Crisis()
    {
        var campaign = Pair(4402);
        var relation = CampaignDiplomacy.Get(campaign, 0, 1);
        relation.Tension = 55f;
        relation.TensionStage = (int)TensionStage.Crisis;
        campaign.Chronicle.Record(campaign, "tension_crisis", 0, 1);
        var hash = CampaignFingerprint.Of(campaign);
        var text = NewsFeed.Context(campaign, AsLine(campaign, "tension_crisis"));
        Check(text.Contains("Crisis") && text.Contains("55.0"),
            "a crisis headline uses the stage and tension already stored: " + text);
        Check(CampaignFingerprint.Of(campaign) == hash && campaign.Relations.Count == 1,
            "reading the crisis facts does not open another relation or change the fingerprint");
    }

    private static void SubjectAndEmpty()
    {
        var campaign = Pair(4403);
        campaign.Chronicle.Record(campaign, "alliance_ended", 0, 1);
        campaign.Chronicle.Record(campaign, "state_formed", 0, subject: "Recorded subject Northland");
        Check(NewsFeed.Importance("alliance_ended") >= 2 && NewsFeed.Importance("state_formed") >= 2,
            "both of these kinds are important enough for a fact line");
        var hash = CampaignFingerprint.Of(campaign);
        var empty = NewsFeed.Context(campaign, AsLine(campaign, "alliance_ended"));
        var named = NewsFeed.Context(campaign, AsLine(campaign, "state_formed"));
        Check(empty.Length == 0,
            "an important item with no stored fact stays empty: '" + empty + "'");
        Check(named.Contains("Recorded subject Northland"),
            "a stored subject is the fact when nothing else is on the books: " + named);
        Check(CampaignFingerprint.Of(campaign) == hash,
            "reading the subject line does not change the fingerprint");
    }
}
