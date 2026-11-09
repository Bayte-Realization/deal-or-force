using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// Weariness, the occupation refresh and the weekly history sample read wars that are still going.
/// An ended war stays in the record a ledger can name.
/// </summary>
static class EndedWarWalkTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("ended war walk: " + note); Console.WriteLine("PASS ended war walk: " + note); }

    public static void Run()
    {
        var first = World(91109, day: 182);
        var second = World(91110, day: 181);
        var alpha = first.Nations[0];
        var gamma = first.Nations[2];
        alpha.WarSupport = 0.8f;

        var historical = War(1, "The old war", ended: true, attacker: 0, defender: 2);
        historical.AttackerFront.Add(99);
        historical.LastFrontDay = 4;
        historical.ScoreBackDay = 0;
        historical.ScoreBack = 0f;
        historical.WarScore = 40f;
        historical.AttackerOccupiedValue = 0.5f;
        historical.AttackerWarSupport = 0.1f;
        historical.DefenderWarSupport = 0.1f;
        historical.PeaceSummary = "The old war";
        var open = War(2, "The open war", ended: false, attacker: 0, defender: 1);
        open.StartedDay = 182 - 815;
        open.AttackerLosses = 1f;
        open.AttackerWarSupport = 0.5f;
        open.ScoreBackDay = -1;
        open.PeaceSummary = "The open war";
        first.Wars.Add(historical);
        first.Wars.Add(open);
        first.ProvinceOccupation[1] = 0.4f;

        var other = War(7, "The other war", ended: false, attacker: 0, defender: 1);
        var otherEnded = War(8, "The other old war", ended: true, attacker: 0, defender: 1);
        otherEnded.ScoreBackDay = 3;
        otherEnded.PeaceSummary = "The other old war";
        second.Wars.Add(otherEnded);
        second.Wars.Add(other);

        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var passes = Warfare.LiveWarPasses;
        var seen = Warfare.LiveWars(first);
        var secondSeen = Warfare.LiveWars(second);
        Check(Warfare.LiveWarPasses == passes + 2 && Has(seen, open) && !Has(seen, historical) &&
              Has(secondSeen, other) && !Has(secondSeen, otherEnded),
            "each campaign's live list has its open war and not its ended one");
        Check(historical.ScoreBackDay == 0 && historical.AttackerFront.Contains(99) && historical.LastFrontDay == 4 &&
              otherEnded.ScoreBackDay == 3,
            "building the live list does not write an ended war");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "building the live list writes nothing");

        Check(WarWeariness.NationalFatigue(first, alpha) == 1f - alpha.WarSupport &&
              WarWeariness.NationalFatigue(first, gamma) == 0f,
            "fatigue is one minus the open war's support, and a country only in an ended war is not weary of it");
        Check(!WarWeariness.Overrun(first, alpha),
            "ground lost in an ended war does not count as overrun");
        var age = first.ElapsedDays - open.StartedDay;
        var years = MathF.Max(0f, age - first.Defines.PeaceWearinessAfterDays) / 365f;
        var pinned = MathF.Min(first.Defines.PeaceWearinessCap, years * first.Defines.PeaceWearinessPerYear);
        Check(age == 815 && pinned == 15f && WarWeariness.Weariness(first, open) == 15f,
            "the open war's weariness is 15, one year after day 450 at 15 a year");
        WarWeariness.Explain(first, alpha);
        Check(Warfare.LiveWarPasses == passes + 2 && CampaignFingerprint.Of(first) == firstPrint &&
              CampaignFingerprint.Of(second) == secondPrint,
            "reading weariness does not rebuild the list or change either fingerprint");

        second.History.Capture(second);
        Check(second.History.ForNation(0).Count == 0 && Warfare.LiveWarPasses == passes + 2,
            "a day that is not a sample day does not write history or walk the war record");
        WarWeariness.Tick(second);
        OccupationResistance.Tick(second);
        Check(otherEnded.ScoreBackDay == 3 && Warfare.LiveWarPasses == passes + 2,
            "a quiet day does not tick weariness or occupation slip, and does not touch the ended war");

        first.History.Capture(first);
        var alphaSample = first.History.ForNation(alpha.Id);
        var gammaSample = first.History.ForNation(gamma.Id);
        Check(alphaSample.Count == 1 && alphaSample[0].WarSupport == 0.5f &&
              gammaSample.Count == 1 && gammaSample[0].WarSupport == 1f,
            "the weekly sample takes the open war's support, not the ended war's");
        first.History.Capture(first);
        Check(first.History.ForNation(alpha.Id).Count == 1 && Warfare.LiveWarPasses == passes + 2,
            "the same sample day does not walk the record again");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "the weekly sample does not change either fingerprint");

        WarWeariness.Tick(first);
        Check(Warfare.LiveWarPasses == passes + 2 && Has(Warfare.LiveWars(first), open) && !Has(Warfare.LiveWars(first), historical),
            "the weariness week reuses the live list and still does not see the ended war");
        Check(historical.ScoreBackDay == 0 && historical.ScoreBack == 0f && historical.LastFrontDay == 4 &&
              historical.AttackerFront.Contains(99) && first.Wars.Contains(historical),
            "the weariness week does not write the ended war");
        Check(open.ScoreBackDay == 182 && !open.Ended,
            "the open war is the one the week recorded");
        OccupationResistance.Tick(first);
        Check(first.ProvinceOccupation[1] == 0.4f && Warfare.LiveWarPasses == passes + 2,
            "occupation slip waits for its weekday and does not touch a quiet day");
        Check(CampaignFingerprint.Of(second) == secondPrint && Has(Warfare.LiveWars(second), other),
            "the weariness week leaves the other campaign");

        first.ProvinceController[3] = 10;
        var lifted = Occupations.Tick(first);
        Check(lifted == 1 && first.ProvinceController[3] == 12 && first.ProvinceController[1] == 10,
            "ground an ended war no longer covers goes home, and the open war's hold stays");
        Check(historical.ScoreBackDay == 0 && historical.LastFrontDay == 4 && historical.AttackerFront.Contains(99) &&
              historical.PeaceSummary == "The old war" && first.Wars.Contains(historical) &&
              Warfare.LiveWarPasses == passes + 2 && !Has(Warfare.LiveWars(first), historical),
            "returning that ground does not write or revisit the ended war");
        Check(CampaignFingerprint.Of(second) == secondPrint,
            "returning that ground leaves the other campaign's fingerprint");

        open.Ended = true;
        first.MarkWarsChanged();
        var next = Warfare.LiveWars(first);
        Check(Warfare.LiveWarPasses == passes + 3 && !Has(next, open) && !Has(next, historical),
            "ending the open war drops it from the next walk");
        var ledger = new PeaceLedger(first, open, alpha.Id);
        Check(first.Wars.Contains(open) && first.Wars.Contains(historical) && ledger != null &&
              open.PeaceSummary == "The open war" && historical.PeaceSummary == "The old war",
            "the ledger can still name the war that left the live list");
        Check(CampaignFingerprint.Of(second) == secondPrint && Has(Warfare.LiveWars(second), other) &&
              !Has(Warfare.LiveWars(second), open),
            "ending a war in the first campaign does not touch the second");

        var slip = SlipWorld();
        var beforeSlip = Warfare.LiveWarPasses;
        var expected = OccupationResistance.Slip(1f, 0f, 0f, slip.Defines);
        OccupationResistance.Tick(slip);
        Check(expected > 0f && slip.ProvinceOccupation[2] == 1f - expected &&
              slip.ProvinceOccupation[1] == 1f && slip.ProvinceOccupation[3] == 1f,
            "an open civil war slips by the existing weekly rate, and an ended war does not");
        Check(Warfare.LiveWarPasses == beforeSlip + 1 && Has(Warfare.LiveWars(slip), slip.Wars[1]) &&
              !Has(Warfare.LiveWars(slip), slip.Wars[0]) && Has(Warfare.LiveWars(slip), slip.Wars[2]),
            "the slip walks the open wars and not the ended one");
    }

    static bool Has(System.Collections.Generic.IReadOnlyList<StrategicWar> wars, StrategicWar war)
    {
        for (var index = 0; index < wars.Count; index++)
            if (ReferenceEquals(wars[index], war)) return true;
        return false;
    }

    static StrategicWar War(int id, string name, bool ended, int attacker, int defender) => new()
    {
        Id = id,
        Name = name,
        AttackerId = attacker,
        DefenderId = defender,
        Ended = ended,
        PeaceSummary = name
    };

    static void SetDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, day * 24L);

    static Campaign World(int seed, int day)
    {
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "ALP", Name = "Alpha", Population = 1000 });
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = 11, Tag = "BET", Name = "Beta", Population = 1000 });
        campaign.Nations.Add(new Nation { Id = 2, MapIndex = 12, Tag = "GAM", Name = "Gamma", Population = 1000 });
        campaign.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 11, Id = "s1", Name = "One" });
        campaign.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 11, Id = "s2", Name = "Two" });
        campaign.States.Add(new ScenarioState { MapIndex = 3, NationMapIndex = 12, Id = "s3", Name = "Three" });
        var states = new ushort[] { 0, 1, 2, 3 };
        var owners = new ushort[] { 0, 11, 11, 12 };
        var neighbors = new int[states.Length][];
        for (var index = 0; index < neighbors.Length; index++) neighbors[index] = Array.Empty<int>();
        var map = (ProvinceTopology)typeof(ProvinceTopology)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object[] { new ushort[1], states, (ushort[])owners.Clone(), new int[states.Length], neighbors, 1, 1 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, new ushort[] { 0, 10, 11, 12 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(campaign, new float[owners.Length]);
        SetDay(campaign, day);
        return campaign;
    }

    static Campaign SlipWorld()
    {
        var campaign = World(91111, day: 5);
        var ended = War(1, "Ended civil", ended: true, attacker: 0, defender: 1);
        ended.CivilConflict = true;
        ended.OriginalController[1] = 10;
        var civil = War(2, "Open civil", ended: false, attacker: 0, defender: 1);
        civil.CivilConflict = true;
        civil.OriginalController[2] = 10;
        var regular = War(3, "Open war", ended: false, attacker: 0, defender: 1);
        regular.OriginalController[3] = 10;
        campaign.Wars.Add(ended);
        campaign.Wars.Add(civil);
        campaign.Wars.Add(regular);
        campaign.ProvinceController[1] = 11;
        campaign.ProvinceController[2] = 11;
        campaign.ProvinceController[3] = 11;
        campaign.ProvinceOccupation[1] = 1f;
        campaign.ProvinceOccupation[2] = 1f;
        campaign.ProvinceOccupation[3] = 1f;
        return campaign;
    }
}
