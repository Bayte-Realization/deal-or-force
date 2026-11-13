using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Walking out of an alliance is a public fact. The rupture path used to be the only
/// caller that said so. The diplomacy button goes through the same break.
/// </summary>
static class AllianceBreakTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new System.Exception("alliance break: " + note); System.Console.WriteLine("PASS alliance break: " + note); }

    public static void Run()
    {
        Check(SocialIncidentCatalog.Find("alliance_ended", out var fact) && fact.Actor < 0f &&
              fact.Name == "An alliance ended",
            "the public fact marks the country that walked out");
        Loc.Language = "english";
        Check(Loc.Get("chronicle.alliance_ended") == "{0} leaves its alliance with {1}.",
            "the English chronicle names who left");
        Loc.Language = "russian";
        Check(Loc.Get("chronicle.alliance_ended").Contains("{0}") &&
              Loc.Get("chronicle.alliance_ended").Contains("{1}"),
            "the Russian chronicle names both countries");
        Loc.Language = "english";

        var other = World(2);
        var untouched = CampaignFingerprint.Of(other);
        var campaign = World(1);
        var relation = CampaignDiplomacy.Get(campaign, 0, 1);
        relation.Stance = DiplomaticStance.Allied;
        relation.AllianceSinceDay = 0;
        OpinionSystem.Remember(campaign, relation, "alliance");
        var before = relation.Opinion;
        Check(campaign.Chronicle.Entries.Count == 0 && relation.Stance == DiplomaticStance.Allied,
            "signing the stance has not yet told the world");

        new BreakAllianceCommand { IssuerId = 0, TargetId = 1 }.Apply(campaign);

        Check(relation.Stance == DiplomaticStance.Neutral, "the button ends the alliance");
        Check(OpinionSystem.Has(relation, "broke_our_alliance") && !OpinionSystem.Has(relation, "alliance"),
            "they remember the walk-out and forget the alliance");
        Check(relation.Opinion < before, "walking out costs opinion when the number is not already at the cap");
        Check(campaign.Chronicle.Entries.Count(entry =>
                  entry.Kind == "alliance_ended" && entry.Public && entry.ActorId == 0 && entry.TargetId == 1) == 1,
            "the world hears once that this country left");
        Check(SocialMemory.ImagesEnabled(campaign) &&
              campaign.Nations[0].DomesticImage?.LastReason == "An alliance ended",
            "the country that walked out carries the public mark");
        Check(CampaignFingerprint.Of(other) == untouched && other.Chronicle.Entries.Count == 0,
            "the other campaign is not told and its fingerprint does not move");

        new BreakAllianceCommand { IssuerId = 0, TargetId = 1 }.Apply(campaign);
        Check(campaign.Chronicle.Entries.Count(entry => entry.Kind == "alliance_ended") == 1,
            "a second click, with no alliance left, does not announce another ending");
        Check(campaign.Nations[0].Population == 1_000_000 && campaign.Nations[1].Population == 1_000_000 &&
              campaign.ProvinceController is null,
            "leaving takes no land and no people");
    }

    static Campaign World(int seed)
    {
        var campaign = new Campaign(62000 + seed);
        campaign.Nations.Add(new Nation { Id = 0, Tag = "AAA", Name = "Alpha", Population = 1_000_000, MapIndex = 1 });
        campaign.Nations.Add(new Nation { Id = 1, Tag = "BBB", Name = "Beta", Population = 1_000_000, MapIndex = 2 });
        return campaign;
    }
}
