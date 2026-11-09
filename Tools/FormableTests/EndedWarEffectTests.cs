using System;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The economy week, recruitment capacity and the escalation review read wars that are still
/// being fought. An ended war stays in the save record and does not keep those effects.
/// </summary>
static class EndedWarEffectTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("ended war effect: " + note); Console.WriteLine("PASS ended war effect: " + note); }

    public static void Run()
    {
        var first = World(91119);
        var second = World(91120);
        var alpha = first.Nations[0];
        var beta = first.Nations[1];
        var gamma = first.Nations[2];
        alpha.Population = beta.Population = 1_000_000;
        alpha.Stability = gamma.Stability = 0.8f;
        alpha.WarSupport = gamma.WarSupport = 1f;
        alpha.Gdp = gamma.Gdp = beta.Gdp = 10f;
        first.Defines.SecularCycleEnabled = false;

        var historical = War(1, "The old war", ended: true, attacker: 0, defender: 2);
        historical.AttackerFront.Add(4);
        historical.PeaceSummary = "The old war";
        historical.StartedDay = -800;
        historical.AttackerLosses = 80_000f;
        var open = War(2, "The open war", ended: false, attacker: 0, defender: 1);
        open.PeaceSummary = "The open war";
        first.Wars.Add(historical);
        first.Wars.Add(open);

        var otherEnded = War(8, "The other old war", ended: true, attacker: 0, defender: 1);
        otherEnded.PeaceSummary = "The other old war";
        var other = War(7, "The other war", ended: false, attacker: 0, defender: 1);
        second.Wars.Add(otherEnded);
        second.Wars.Add(other);

        Check(first.NationById(0) == alpha && first.NationById(2) == gamma, "nation id is the list index");

        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var passes = Warfare.LiveWarPasses;
        var seen = Warfare.LiveWars(first);
        var secondSeen = Warfare.LiveWars(second);
        Check(Warfare.LiveWarPasses == passes + 2 && Has(seen, open) && !Has(seen, historical) &&
              Has(secondSeen, other) && !Has(secondSeen, otherEnded),
            "each campaign's live list has its open war and not its ended one");
        Check(historical.AttackerFront.Contains(4) && historical.PeaceSummary == "The old war",
            "building the live list does not write an ended war");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "building the live list writes nothing");

        Check(RecruitmentSystem.DynamicQueueCap(first, alpha) == 24 &&
              RecruitmentSystem.DynamicQueueCap(first, alpha) == first.Defines.RecruitmentWartimeBatchCap &&
              RecruitmentSystem.DynamicQueueCap(first, gamma) == 12 &&
              RecruitmentSystem.DynamicQueueCap(first, gamma) == first.Defines.RecruitmentBatchCap,
            "an open war still raises the queue to 24, and an ended war leaves the peace cap of 12");
        Check(Warfare.LiveWarPasses == passes + 2 && CampaignFingerprint.Of(first) == firstPrint &&
              CampaignFingerprint.Of(second) == secondPrint,
            "reading the queue does not rebuild the list or change either fingerprint");

        alpha.StabilityBias = gamma.StabilityBias = 0f;
        var pull = StabilityCore.Preview(first, gamma) - StabilityCore.Preview(first, alpha);
        Check(StabilityCore.Weights.War == 0.14f && MathF.Abs(pull - 0.14f) < 0.00001f,
            "an open war still costs 0.14 of stability, and an ended war costs none");
        Check(Warfare.LiveWarPasses == passes + 2 && CampaignFingerprint.Of(first) == firstPrint,
            "reading that cost does not rebuild the list or change the fingerprint");

        alpha.StabilityBias = gamma.StabilityBias = 1f;
        alpha.Stability = gamma.Stability = 0.8f;
        SetDay(first, 7);
        EconomySystem.Tick(first);
        var peaceBias = 0.12f + (1f - 0.12f) * 0.999f;
        Check(alpha.StabilityBias == 1f * 0.996f && gamma.StabilityBias == peaceBias,
            "the economy week still takes the wartime step for the open war, and the peace step for an ended one");
        Check(historical.AttackerFront.Contains(4) && historical.Intensity == 0 && first.Wars.Contains(historical) &&
              Warfare.LiveWarPasses == passes + 2 && Has(Warfare.LiveWars(first), open) && !Has(Warfare.LiveWars(first), historical),
            "the economy week does not write or revisit the ended war");
        Check(CampaignFingerprint.Of(second) == secondPrint && Has(Warfare.LiveWars(second), other),
            "the economy week leaves the other campaign");

        SetDay(first, 8);
        var quiet = Warfare.LiveWarPasses;
        EconomySystem.Tick(first);
        Escalation.Tick(first);
        RecruitmentSystem.Tick(first);
        Check(Warfare.LiveWarPasses == quiet && open.Intensity == 0 && historical.Intensity == 0 &&
              historical.AttackerFront.Contains(4),
            "a day that is not a review day does not walk the war record");

        SetDay(first, 9);
        var defines = first.Defines;
        open.StartedDay = 9 - defines.WarMajorDays;
        open.AttackerLosses = alpha.Population * defines.WarMajorLossShare + 1f;
        open.DefenderLosses = 0f;
        open.Intensity = 0;
        var age = first.ElapsedDays - open.StartedDay;
        var share = MathF.Max(open.AttackerLosses / MathF.Max(1f, alpha.Population),
                              open.DefenderLosses / MathF.Max(1f, beta.Population));
        var due = age >= defines.WarTotalDays && share >= defines.WarTotalLossShare ? 2
            : age >= defines.WarMajorDays && share >= defines.WarMajorLossShare ? 1 : 0;
        Escalation.Tick(first);
        Check(due == 1 && age == 150 && open.Intensity == 1 && historical.Intensity == 0,
            "the open war still becomes major at day 150, and the ended war does not");
        Check(Warfare.LiveWarPasses == quiet && first.Wars.Contains(open) && first.Wars.Contains(historical) &&
              open.PeaceSummary == "The open war",
            "becoming major does not drop the war from the record");
        Check(CampaignFingerprint.Of(second) == secondPrint,
            "the escalation week leaves the other campaign");

        open.Ended = true;
        Check(RecruitmentSystem.DynamicQueueCap(first, alpha) == 12 &&
              Warfare.LiveWarPasses == quiet + 1 && !Has(Warfare.LiveWars(first), open) &&
              first.Wars.Contains(open) && first.Wars.Contains(historical) &&
              historical.AttackerFront.Contains(4) && open.Name == "The open war" && open.Intensity == 1,
            "ending the war drops the wartime queue on the next read and keeps the save record");

        SetDay(first, 14);
        alpha.StabilityBias = 1f;
        alpha.Stability = 0.8f;
        alpha.DebtCondition = DebtCondition.Current;
        EconomySystem.Tick(first);
        Check(alpha.StabilityBias == peaceBias && open.Ended && first.Wars.Contains(open) &&
              historical.PeaceSummary == "The old war",
            "the next economy week no longer treats the ended war as a war");
        Check(Warfare.LiveWarPasses == quiet + 1 && CampaignFingerprint.Of(second) == secondPrint,
            "that week reuses the live list and leaves the other campaign");

        SetDay(first, 16);
        open.StartedDay = 16 - defines.WarTotalDays;
        open.AttackerLosses = alpha.Population * defines.WarTotalLossShare + 1f;
        Escalation.Tick(first);
        Check(open.Intensity == 1 && historical.Intensity == 0 && first.Wars.Contains(open) &&
              first.Wars.Count == 2 && !Has(Warfare.LiveWars(first), open) && Has(Warfare.LiveWars(second), other),
            "the next escalation week does not raise a war that has already ended");
        Check(CampaignFingerprint.Of(second) == secondPrint && otherEnded.PeaceSummary == "The other old war",
            "the other campaign's ended war and fingerprint stay where they were");
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

    static Campaign World(int seed)
    {
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "ALP", Name = "Alpha", Population = 1000 });
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = 11, Tag = "BET", Name = "Beta", Population = 1000 });
        campaign.Nations.Add(new Nation { Id = 2, MapIndex = 12, Tag = "GAM", Name = "Gamma", Population = 1000 });
        return campaign;
    }
}
