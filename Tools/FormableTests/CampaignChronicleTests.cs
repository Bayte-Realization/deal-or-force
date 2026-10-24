using System;
using System.Text;
using DealOrForce.Simulation;

/// <summary>
/// The ledger's chronicle list is a read of stored lines. An empty book yields the empty
/// sentence. Reading it does not record and does not change the fingerprint.
/// </summary>
public static class CampaignChronicleTests
{
    static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL campaign chronicle: " + text);
        Console.WriteLine("PASS campaign chronicle: " + text);
    }

    static Campaign World()
    {
        var campaign = new Campaign(241024) { ScenarioId = "campaign_chronicle" };
        for (var id = 0; id < 4; id++)
            campaign.Nations.Add(new Nation
            {
                Id = id,
                Tag = "CC" + id,
                Name = id == 0 ? "Reader" : "Harbour" + id,
                MapIndex = id + 1,
                Population = 1000000,
                IsHuman = id == 0
            });
        return campaign;
    }

    static string Joined(System.Collections.Generic.IReadOnlyList<CampaignChronicle.Line> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line.Text).Append('\n');
        return text.ToString();
    }

    public static void Run()
    {
        var language = Loc.Language;
        try
        {
            Loc.Language = "english";
            Empty();
            Listed();
            Capped();
            Loc.Language = "russian";
            Russian();
        }
        finally { Loc.Language = language; }
    }

    static void Empty()
    {
        var campaign = World();
        var nation = campaign.Nations[0];
        Check(campaign.Nations.Count > 0 && nation.Id == 0 && campaign.ProvinceMap is null,
            "nation id 0 stands at index 0, with no province topology");
        var entries = campaign.Chronicle.Entries.Count;
        var hash = CampaignFingerprint.Of(campaign);
        var lines = CampaignChronicle.Read(campaign, nation, nation);
        Check(lines.Count == 1 && lines[0].Text == "Nothing has been written yet." && lines[0].Sequence == 0,
            "an empty chronicle yields the empty sentence");
        Check(campaign.Chronicle.Entries.Count == entries && CampaignFingerprint.Of(campaign) == hash,
            "reading an empty chronicle does not record or change the fingerprint");
    }

    static void Listed()
    {
        var campaign = World();
        var nation = campaign.Nations[0];
        var war = new StrategicWar
        {
            Id = 7, Name = "Harbour war", AttackerId = 0, DefenderId = 1
        };
        war.AttackerAllies.Add(2);
        campaign.Wars.Add(war);
        campaign.Chronicle.Record(campaign, "war_started", 0, 1, subject: "our front");
        campaign.Chronicle.Record(campaign, "shed_note", 0, subject: "shed yard", isPublic: false);
        campaign.Chronicle.Record(campaign, "aid_sent", 2, 1, subject: "allied guns");
        campaign.Chronicle.Record(campaign, "aid_sent", 3, 1, subject: "neutral parcel");
        campaign.Chronicle.Record(campaign, "embargo_started", 3, subject: "distant tariff");
        campaign.Chronicle.Record(campaign, "coup_plot", 1, subject: "quiet cellar", isPublic: false);
        var entries = campaign.Chronicle.Entries.Count;
        var wars = campaign.Wars.Count;
        var hash = CampaignFingerprint.Of(campaign);
        var lines = CampaignChronicle.Read(campaign, nation, nation);
        var again = CampaignChronicle.Read(campaign, nation, nation);
        var text = Joined(lines);
        Check(text.Contains("our front") && text.Contains("declares war") && text.Contains("January") && text.Contains("2024"),
            "a stored entry shows its date, the localised kind, and its subject: " + text.Trim());
        Check(text.Contains("shed yard") && text.Contains("shed_note"),
            "a kind with no translation is shown as stored, with its subject");
        Check(text.Contains("allied guns") && !text.Contains("neutral parcel") && !text.Contains("distant tariff") &&
              !text.Contains("quiet cellar"),
            "a war the country is in is listed; an outsider, a bystander, and a foreign secret are not");
        Check(again.Count == lines.Count && Joined(again) == text &&
              campaign.Chronicle.Entries.Count == entries && campaign.Wars.Count == wars &&
              CampaignFingerprint.Of(campaign) == hash,
            "reading the list does not add an entry and does not change the fingerprint");
    }

    static void Capped()
    {
        var campaign = World();
        var nation = campaign.Nations[0];
        for (var index = 1; index <= CampaignChronicle.Limit + 1; index++)
            campaign.Chronicle.Record(campaign, "folio", nation.Id, subject: "entry-" + index.ToString("00"), isPublic: false);
        var entries = campaign.Chronicle.Entries.Count;
        var lines = CampaignChronicle.Read(campaign, nation, nation);
        var text = Joined(lines);
        Check(entries == CampaignChronicle.Limit + 1 && lines.Count == CampaignChronicle.Limit &&
              text.Contains("entry-13") && text.Contains("entry-02") && !text.Contains("entry-01"),
            "the list keeps the newest " + CampaignChronicle.Limit + " and drops the oldest");
        Check(campaign.Chronicle.Entries.Count == entries,
            "capping the list does not delete stored entries");
    }

    static void Russian()
    {
        var empty = World();
        var nation = empty.Nations[0];
        var blank = CampaignChronicle.Read(empty, nation, nation);
        Check(blank[0].Text == "Пока ничего не написано." && empty.Chronicle.Entries.Count == 0,
            "the empty chronicle in Russian is «Пока ничего не написано.»");
        empty.Chronicle.Record(empty, "war_started", 0, 1, subject: "наш фронт");
        var text = Joined(CampaignChronicle.Read(empty, nation, nation));
        Check(text.Contains("наш фронт") && text.Contains("января"),
            "a Russian reading still names the stored subject and the month: " + text.Trim());
    }
}
