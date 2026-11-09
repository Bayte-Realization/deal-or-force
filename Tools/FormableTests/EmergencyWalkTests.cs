using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The emergency staff visits wars that are still being fought.
/// Ended wars stay in the record. A quiet day does not walk them.
/// </summary>
static class EmergencyWalkTests
{
    const int EndedCount = 48;

    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("emergency walk: " + note); Console.WriteLine("PASS emergency walk: " + note); }

    public static void Run()
    {
        var first = World(91109);
        var second = World(91110);
        var open = War(100, "The open war", ended: false);
        var later = War(101, "The later war", ended: false);
        open.AttackerFront.Add(1);
        open.PeaceSummary = "The open war";
        later.PeaceSummary = "The later war";
        for (var index = 0; index < EndedCount; index++)
        {
            var ended = War(index + 1, "Ended " + index, ended: true);
            ended.ScoreBackDay = 3;
            ended.AttackerFront.Add(99);
            ended.PeaceSummary = "Ended " + index;
            first.Wars.Add(ended);
        }
        first.Wars.Add(open);
        first.Wars.Add(later);

        var otherEnded = War(8, "The other old war", ended: true);
        otherEnded.ScoreBackDay = 5;
        otherEnded.PeaceSummary = "The other old war";
        var other = War(7, "The other war", ended: false);
        other.PeaceSummary = "The other war";
        for (var index = 0; index < 36; index++) second.Wars.Add(War(200 + index, "Other ended " + index, ended: true));
        second.Wars.Add(otherEnded);
        second.Wars.Add(other);

        var seen = Warfare.LiveWars(first);
        var secondSeen = Warfare.LiveWars(second);
        Check(seen.Count == 2 && Has(seen, open) && Has(seen, later) &&
              secondSeen.Count == 1 && Has(secondSeen, other) && !Has(secondSeen, otherEnded),
            "each campaign's live list has its open wars and not its ended ones");
        Check(first.Wars[0].ScoreBackDay == 3 && first.Wars[0].AttackerFront.Contains(99) &&
              otherEnded.ScoreBackDay == 5,
            "building the live list does not write an ended war");

        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var passes = Warfare.LiveWarPasses;
        var before = WarEmergency.WarsWalked;
        WarEmergency.Tick(first);
        var walked = WarEmergency.WarsWalked - before;
        Console.WriteLine($"EMERGENCY_WALK walked={walked} history={first.Wars.Count}");
        Check(walked == 2 && first.Wars.Count == EndedCount + 2 && WarEmergency.LastWarId == later.Id,
            "a quiet day walks the two open wars, not the ended archive");
        Check(Warfare.LiveWarPasses == passes && open.AttackerFront.Contains(1) && !open.Ended &&
              first.Wars[0].ScoreBackDay == 3 && first.Wars[0].AttackerFront.Contains(99) &&
              first.Wars[0].PeaceSummary == "Ended 0",
            "the quiet day does not rebuild the list or write an ended war");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "the quiet day writes nothing in either campaign");

        before = WarEmergency.WarsWalked;
        var secondPasses = Warfare.LiveWarPasses;
        WarEmergency.Tick(second);
        var secondWalked = WarEmergency.WarsWalked - before;
        Console.WriteLine($"EMERGENCY_WALK walked={secondWalked} history={second.Wars.Count}");
        Check(secondWalked == 1 && second.Wars.Count == 38 && WarEmergency.LastWarId == other.Id &&
              Warfare.LiveWarPasses == secondPasses,
            "the other campaign walks its own open war and not the first campaign's archive");
        Check(otherEnded.ScoreBackDay == 5 && otherEnded.PeaceSummary == "The other old war" &&
              CampaignFingerprint.Of(first) == firstPrint,
            "that walk leaves the first campaign and the other ended war");

        open.Ended = true;
        later.Ended = true;
        first.MarkWarsChanged();
        before = WarEmergency.WarsWalked;
        WarEmergency.Tick(first);
        walked = WarEmergency.WarsWalked - before;
        Console.WriteLine($"EMERGENCY_WALK walked={walked} history={first.Wars.Count}");
        Check(walked == 0 && first.Wars.Count == EndedCount + 2 && first.Wars.Contains(open) &&
              first.Wars.Contains(later) && open.PeaceSummary == "The open war" &&
              later.PeaceSummary == "The later war" && first.Wars[0].PeaceSummary == "Ended 0",
            "after both wars end, the next day walks nothing and keeps the record");
        Check(Has(Warfare.LiveWars(second), other) && !Has(Warfare.LiveWars(second), open) &&
              CampaignFingerprint.Of(second) == secondPrint,
            "ending wars in the first campaign does not touch the second");
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
        PeaceSummary = name
    };

    static Campaign World(int seed)
    {
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "ALP", Name = "Alpha", Population = 1000 });
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = 11, Tag = "BET", Name = "Beta", Population = 1000 });
        var states = new ushort[] { 0, 1, 2 };
        var owners = new ushort[] { 0, 10, 11 };
        var neighbors = new int[states.Length][];
        for (var index = 0; index < neighbors.Length; index++) neighbors[index] = Array.Empty<int>();
        var map = (ProvinceTopology)typeof(ProvinceTopology)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object[] { new ushort[1], states, (ushort[])owners.Clone(), new int[states.Length], neighbors, 1, 1 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, new ushort[] { 0, 10, 11 });
        return campaign;
    }
}
