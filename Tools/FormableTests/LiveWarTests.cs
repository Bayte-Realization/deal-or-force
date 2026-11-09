using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// A day's fighting, supply and scores visit wars that are still going.
/// An ended war stays in the record a chronicle, a ledger and an open peace paper can name.
/// </summary>
static class LiveWarTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("live wars: " + note); Console.WriteLine("PASS live wars: " + note); }

    public static void Run()
    {
        var first = World(61109);
        var second = World(61110);
        var alpha = first.Nations[0];
        var beta = first.Nations[1];
        var historical = War(1, "The old war", ended: true);
        var open = War(2, "The open war", ended: false);
        historical.AttackerFront.Add(99);
        historical.LastFrontDay = 4;
        historical.FrontDirty = true;
        historical.PendingOfferFromId = alpha.Id;
        historical.PendingOfferTerms = PeaceTerms.WhitePeace;
        first.Wars.Add(historical);
        first.Wars.Add(open);
        var other = War(7, "The other war", ended: false);
        second.Wars.Add(other);

        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var passes = Warfare.LiveWarPasses;
        var seen = Warfare.LiveWars(first);
        Check(Warfare.LiveWarPasses == passes + 1, "the first look builds the live list once");
        Check(Has(seen, open) && !Has(seen, historical), "the live list has the open war and not the ended one");
        Check(CampaignFingerprint.Of(first) == firstPrint, "reading the live list writes nothing");

        var secondSeen = Warfare.LiveWars(second);
        Check(!ReferenceEquals(seen, secondSeen) && Has(secondSeen, other) && !Has(secondSeen, open),
            "a second campaign keeps its own live list");
        Check(ReferenceEquals(seen, Warfare.LiveWars(first)) && Has(seen, open),
            "reading the second campaign leaves the first list in place");
        Check(CampaignFingerprint.Of(second) == secondPrint, "the second campaign's fingerprint is untouched");

        Warfare.Tick(first);
        Check(Warfare.LiveWarPasses == passes + 2, "the day's fighting reuses the live list");
        Check(Has(Warfare.LiveWars(first), open) && !Has(Warfare.LiveWars(first), historical),
            "the day's walk still does not see the ended war");
        Check(historical.AttackerFront.Contains(99) && historical.LastFrontDay == 4 && historical.FrontDirty,
            "the front rebuild does not visit the ended war");
        Check(!open.FrontDirty && open.LastFrontDay == first.ElapsedDays,
            "the open war's front is the one the walk rebuilt");
        Check(CampaignFingerprint.Of(second) == secondPrint, "ticking the first campaign leaves the second fingerprint");

        first.Armies.Add(new Army { Id = 1, NationId = alpha.Id, ProvinceId = 1, Name = "Alpha" });
        LogisticsSystem.Tick(first);
        Check(Warfare.LiveWarPasses == passes + 2 && !Has(Warfare.LiveWars(first), historical),
            "supply reads the same live list");

        var paper = PeaceOffers.Describe(first, historical);
        Check(paper.Length > 0 && paper.Contains(alpha.Name),
            "an open peace paper can still be read on an ended war");
        PeaceOffers.Expire(first);
        Check(historical.PendingOfferFromId < 0 && first.Wars.Contains(historical),
            "expiring that paper does not delete the ended war");

        open.Ended = true;
        open.PeaceSummary = "The open war";
        first.MarkWarsChanged();
        first.Chronicle.Record(first, "peace_signed", alpha.Id, beta.Id, subject: open.Name, isPublic: false);
        var next = Warfare.LiveWars(first);
        Check(Warfare.LiveWarPasses == passes + 3, "ending a war rebuilds the live list");
        Check(!Has(next, open) && !Has(next, historical), "the next walk sees neither ended war");
        Check(first.Wars.Contains(open) && first.Wars.Contains(historical), "both records stay on the campaign");
        var ledger = new PeaceLedger(first, open, alpha.Id);
        Check(ledger != null && open.PeaceSummary == "The open war" && WarNames.Of(first, open).Length > 0,
            "a ledger can still name the war that left the live list");
        Check(first.Chronicle.Entries.Any(entry => entry.Subject == open.Name),
            "the chronicle still names that war");
        Check(CampaignFingerprint.Of(second) == secondPrint && Has(Warfare.LiveWars(second), other),
            "ending a war in the first campaign does not touch the second");
    }

    static bool Has(System.Collections.Generic.IReadOnlyList<StrategicWar> wars, StrategicWar war)
    {
        for (var index = 0; index < wars.Count; index++)
            if (ReferenceEquals(wars[index], war)) return true;
        return false;
    }

    static StrategicWar War(int id, string name, bool ended) => new()
    {
        Id = id,
        Name = name,
        AttackerId = 0,
        DefenderId = 1,
        Ended = ended,
        PeaceSummary = name,
        FrontDirty = true
    };

    static Campaign World(int seed)
    {
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "ALP", Name = "Alpha", Population = 1000 });
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = 11, Tag = "BET", Name = "Beta", Population = 1000 });
        campaign.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 10, Id = "s1", Name = "One" });
        campaign.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 11, Id = "s2", Name = "Two" });
        var states = new ushort[] { 0, 1, 2 };
        var owners = new ushort[] { 0, 10, 11 };
        var neighbors = new int[states.Length][];
        neighbors[0] = Array.Empty<int>();
        neighbors[1] = new[] { 2 };
        neighbors[2] = new[] { 1 };
        var map = (ProvinceTopology)typeof(ProvinceTopology)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object[] { new ushort[1], states, (ushort[])owners.Clone(), new int[states.Length], neighbors, 1, 1 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, (ushort[])owners.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(campaign, new float[owners.Length]);
        return campaign;
    }
}
