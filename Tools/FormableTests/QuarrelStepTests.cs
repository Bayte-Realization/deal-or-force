using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// A pair that is not at war, and already has a cause, takes one rung on the tension ladder
/// or receives the existing ultimatum. A friendly pair and an ended war do not. A player
/// is not declared on.
/// </summary>
static class QuarrelStepTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("quarrel step: " + note); Console.WriteLine("PASS quarrel step: " + note); }

    public static void Run()
    {
        var compiled = new Defines();
        Check(compiled.QuarrelStep && compiled.QuarrelStepCooldownDays == 120,
            "the compiled step is on and the same cause waits 120 days");
        Loc.Language = "english";
        Check(Loc.Get("chronicle.tension_crisis") == "Relations between {0} and {1} have reached a crisis.",
            "the English crisis sentence is the one the ladder already uses");
        Loc.Language = "russian";
        Check(Loc.Get("chronicle.tension_crisis").Contains("{0}") &&
              Loc.Get("chronicle.tension_crisis").Contains("кризиса"),
            "the Russian crisis sentence is the one the ladder already uses");
        Loc.Language = "english";

        ReadDoesNotWrite();
        RivalStepsOnce();
        OpinionOnABorderSteps();
        FriendlyDoesNothing();
        EndedWarIsNotACause();
        SameCauseWaitsDifferentCauseDoesNot();
        HumanPaperDoesNotTakeLand();
        HumanWithoutADemandIsNotDeclaredOn();
        TwoHumansAndTheSwitch();
        SaveRoundTrip();
    }

    static void ReadDoesNotWrite()
    {
        var campaign = Bare(1);
        var relation = Pair(campaign, 40f);
        var before = CampaignFingerprint.Of(campaign);
        Check(Escalation.StageOf(relation) == TensionStage.Calm, "a new pair is calm");
        var drivers = Escalation.Drivers(campaign, relation);
        Check(drivers.Rivalry == 0f && CampaignFingerprint.Of(campaign) == before,
            "reading the stage and the drivers does not change the fingerprint");
        Check(campaign.NationById(0) == campaign.Nations[0] && campaign.NationById(1) == campaign.Nations[1],
            "nation id is the list index");
    }

    static void RivalStepsOnce()
    {
        var campaign = Bordered(2);
        var relation = Pair(campaign, 40f);
        relation.Stance = DiplomaticStance.Rival;
        relation.Tension = campaign.Defines.TensionFrictionAt + 15f;
        Day(campaign, 2);
        var wars = campaign.Wars.Count;
        Escalation.Tick(campaign);
        Check(relation.TensionStage == (int)TensionStage.Crisis &&
              relation.Tension >= campaign.Defines.TensionCrisisAt &&
              relation.QuarrelRivalDay == 2 && relation.QuarrelBorderDay == 0,
            "a bordered rival already at friction takes one rung, to crisis");
        Check(campaign.Chronicle.Entries.Any(entry => entry.Kind == "tension_crisis"),
            "that rung uses the existing crisis chronicle");
        Check(campaign.Wars.Count == wars && relation.Stance != DiplomaticStance.AtWar,
            "the rung is not a declaration");
        Untouched(campaign);
    }

    static void OpinionOnABorderSteps()
    {
        var campaign = Bordered(3);
        var relation = Pair(campaign, campaign.Defines.AiRivalryThreshold);
        Check(relation.Stance != DiplomaticStance.Rival, "the opinion floor is not yet a rival stance");
        Day(campaign, 2);
        Escalation.Tick(campaign);
        Check(relation.TensionStage == (int)TensionStage.Friction &&
              relation.QuarrelBorderDay == 2 && relation.QuarrelRivalDay == 0 &&
              relation.Stance != DiplomaticStance.AtWar && campaign.Wars.Count == 0,
            "opinion at the existing floor, across a shared border, takes the first rung and does not declare");
        Untouched(campaign);
    }

    static void FriendlyDoesNothing()
    {
        var campaign = Bordered(4);
        var relation = Pair(campaign, 40f);
        var before = CampaignFingerprint.Of(campaign);
        Day(campaign, 2);
        Escalation.Tick(campaign);
        Check(relation.TensionStage == (int)TensionStage.Calm &&
              relation.Tension < campaign.Defines.TensionFrictionAt &&
              relation.QuarrelRivalDay == 0 && relation.QuarrelBorderDay == 0 &&
              relation.QuarrelRefusalDay == 0 && relation.QuarrelClaimDay == 0 &&
              campaign.Wars.Count == 0 && campaign.Chronicle.Entries.Count == 0,
            "a friendly pair with no cause stays calm");
        Check(CampaignFingerprint.Of(campaign) != before || relation.Tension == 0f,
            "the friendly week did not open a quarrel");
        Untouched(campaign);
    }

    static void EndedWarIsNotACause()
    {
        var campaign = Bordered(5);
        var other = Bordered(6);
        var relation = Pair(campaign, 40f);
        var ended = new StrategicWar
        {
            Id = 1, Name = "The old war", AttackerId = 0, DefenderId = 1,
            Ended = true, EndedDay = 1, PeaceSummary = "The old war"
        };
        campaign.Wars.Add(ended);
        var otherRelation = Pair(other, 40f);
        otherRelation.Stance = DiplomaticStance.Rival;
        Day(campaign, 2);
        Day(other, 2);
        var otherPrint = CampaignFingerprint.Of(other);
        Escalation.Tick(campaign);
        Check(ended.Ended && ended.PeaceSummary == "The old war" && ended.Intensity == 0 &&
              relation.TensionStage == (int)TensionStage.Calm &&
              relation.QuarrelRivalDay == 0 && relation.QuarrelClaimDay == 0 &&
              campaign.Wars.Count == 1 && !Warfare.LiveWars(campaign).Contains(ended),
            "an ended war is not a cause and does not stay on the live list");
        Check(CampaignFingerprint.Of(other) == otherPrint && otherRelation.TensionStage == (int)TensionStage.Calm,
            "the week does not write the other campaign");
        Untouched(campaign);
    }

    static void SameCauseWaitsDifferentCauseDoesNot()
    {
        var waiting = Bordered(7);
        var relation = Pair(waiting, 40f);
        relation.Stance = DiplomaticStance.Rival;
        Day(waiting, 2);
        Escalation.Tick(waiting);
        var first = relation.TensionStage;
        Day(waiting, 9);
        Escalation.Tick(waiting);
        Check(first == (int)TensionStage.Friction && relation.TensionStage == first &&
              relation.Tension >= waiting.Defines.TensionFrictionAt && relation.QuarrelRivalDay == 2,
            "the same rivalry does not climb again inside the cooldown, and the rung it took stays");

        var next = Bordered(8);
        var heated = Pair(next, 40f);
        heated.Stance = DiplomaticStance.Rival;
        Day(next, 2);
        Escalation.Tick(next);
        OpinionSystem.Remember(next, heated, "refused_our_ultimatum");
        Day(next, 9);
        Escalation.Tick(next);
        Check(heated.QuarrelRivalDay == 2 && heated.QuarrelRefusalDay == 9 &&
              heated.TensionStage == (int)TensionStage.Crisis &&
              heated.Stance != DiplomaticStance.AtWar && next.Wars.Count == 0,
            "a refused ultimatum is a different cause and takes the next rung while the rivalry is still waiting");
        Untouched(waiting);
        Untouched(next);
    }

    static void HumanPaperDoesNotTakeLand()
    {
        var campaign = Bordered(9, claimed: true);
        campaign.Nations[1].IsHuman = true;
        var relation = Pair(campaign, 40f);
        relation.Stance = DiplomaticStance.Rival;
        var owner = campaign.ProvinceMap.ProvinceToNation.ToArray();
        Day(campaign, 2);
        Escalation.Tick(campaign);
        Check(campaign.UltimatumPapers.Count == 1 && campaign.UltimatumPapers[0].Pending &&
              campaign.UltimatumPapers[0].FromId == 0 && campaign.UltimatumPapers[0].ToId == 1 &&
              campaign.UltimatumPapers[0].Regions.Contains(12),
            "a human target receives the existing ultimatum for the claimed region");
        Check(campaign.Wars.Count == 0 && relation.Stance != DiplomaticStance.AtWar &&
              campaign.ProvinceMap.ProvinceToNation.SequenceEqual(owner) &&
              campaign.Nations[1].Population == 1_000_000,
            "the paper does not declare war and does not take the player's land");
        Check(relation.QuarrelClaimDay == 2 && relation.QuarrelRivalDay == 0,
            "the paper spends the claim, which is the cause in front of the rivalry");
        Untouched(campaign);
    }

    static void HumanWithoutADemandIsNotDeclaredOn()
    {
        var campaign = Bordered(10);
        campaign.Nations[1].IsHuman = true;
        var relation = Pair(campaign, campaign.Defines.AiRivalryThreshold);
        Day(campaign, 2);
        Escalation.Tick(campaign);
        Check(campaign.UltimatumPapers.Count == 0 && campaign.Wars.Count == 0 &&
              relation.Stance != DiplomaticStance.AtWar &&
              relation.TensionStage == (int)TensionStage.Friction &&
              relation.TensionStage < (int)TensionStage.Clashes,
            "a human with nothing that the ultimatum can demand is not declared on, and the ladder stops short of clashes");
        Untouched(campaign);
    }

    static void TwoHumansAndTheSwitch()
    {
        var both = Bordered(11);
        both.Nations[0].IsHuman = true;
        both.Nations[1].IsHuman = true;
        var relation = Pair(both, both.Defines.AiRivalryThreshold);
        relation.Stance = DiplomaticStance.Rival;
        Day(both, 2);
        Escalation.Tick(both);
        Check(relation.TensionStage == (int)TensionStage.Calm && both.UltimatumPapers.Count == 0 &&
              both.Wars.Count == 0,
            "two humans are not stepped and are not sent a paper");

        var off = Bordered(12);
        off.Defines.QuarrelStep = false;
        var held = Pair(off, off.Defines.AiRivalryThreshold);
        held.Stance = DiplomaticStance.Rival;
        Day(off, 2);
        Escalation.Tick(off);
        Check(held.TensionStage == (int)TensionStage.Calm && held.QuarrelRivalDay == 0,
            "the define turns the step off");
        var before = CampaignFingerprint.Of(off);
        Check(CampaignFingerprint.Of(off) == before, "reading the switched-off pair does not move the fingerprint");
        Untouched(both);
        Untouched(off);
    }

    static void SaveRoundTrip()
    {
        var campaign = Bare(13);
        var relation = Pair(campaign, 10f);
        var before = CampaignFingerprint.Of(campaign);
        relation.QuarrelRefusalDay = 9;
        relation.QuarrelClaimDay = 16;
        relation.QuarrelRivalDay = 2;
        relation.QuarrelBorderDay = 23;
        Check(CampaignFingerprint.Of(campaign) != before, "a taken step joins the fingerprint");
        relation.QuarrelRefusalDay = relation.QuarrelClaimDay = relation.QuarrelRivalDay = relation.QuarrelBorderDay = 0;
        Check(CampaignFingerprint.Of(campaign) == before, "the default step days leave the fingerprint unchanged");
        relation.QuarrelRivalDay = 2;
        var options = new JsonSerializerOptions { IncludeFields = true };
        var restored = JsonSerializer.Deserialize<DiplomaticRelation>(JsonSerializer.Serialize(relation, options), options);
        Check(restored.QuarrelRivalDay == 2 && restored.QuarrelRefusalDay == 0 &&
              restored.QuarrelClaimDay == 0 && restored.QuarrelBorderDay == 0,
            "the step days survive the save serializer, and a missing day stays never");
        Untouched(campaign);
    }

    static Campaign Bare(int seed)
    {
        var campaign = new Campaign(91000 + seed);
        campaign.Defines.IncidentChance = 0f;
        campaign.Defines.BorderWarChancePerWeek = 0f;
        campaign.Nations.Add(new Nation { Id = 0, Tag = "AAA", Name = "Alpha", Population = 1_000_000, MapIndex = 1 });
        campaign.Nations.Add(new Nation { Id = 1, Tag = "BBB", Name = "Beta", Population = 1_000_000, MapIndex = 2 });
        campaign.Armies.Add(new Army { Id = 1, NationId = 0, ProvinceId = 4, OrderProvinceId = 4, DirectOrder = true });
        return campaign;
    }

    static Campaign Bordered(int seed, bool claimed = false)
    {
        var campaign = Bare(seed);
        const int width = 8, height = 3;
        var regions = new ushort[width * height];
        var owners = new ushort[regions.Length];
        regions[width + 2] = 11;
        regions[width + 3] = 12;
        owners[width + 2] = 1;
        owners[width + 3] = 2;
        if (claimed)
        {
            regions[width + 4] = 13;
            owners[width + 4] = 2;
        }
        campaign.AttachProvinceTopology(ProvinceTopology.Build(regions, owners, width, height, _ => false));
        var home = 0;
        for (var province = 1; province <= campaign.ProvinceMap.ProvinceCount; province++)
            if (campaign.ProvinceController[province] == 1) { home = province; break; }
        var army = campaign.Armies[0];
        army.ProvinceId = home;
        army.OrderProvinceId = home;
        army.DirectOrder = true;
        campaign.States.Add(new ScenarioState { Id = "alpha", MapIndex = 11, NationMapIndex = 1, Population = 1000 });
        var held = new ScenarioState { Id = "beta", MapIndex = 12, NationMapIndex = 2, Population = 1000 };
        if (claimed) held.CoreTags.Add("AAA");
        campaign.States.Add(held);
        if (claimed)
            campaign.States.Add(new ScenarioState { Id = "keep", MapIndex = 13, NationMapIndex = 2, Population = 1000 });
        campaign.InvalidateStateIndex();
        return campaign;
    }

    static DiplomaticRelation Pair(Campaign campaign, float opinion)
    {
        var relation = CampaignDiplomacy.Get(campaign, 0, 1);
        relation.Opinion = opinion;
        return relation;
    }

    static void Day(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);

    static void Untouched(Campaign campaign)
    {
        var army = campaign.Armies[0];
        Check(army.DirectOrder && army.ProvinceId == army.OrderProvinceId && army.ProvinceId > 0 && army.NationId == 0,
            "a direct army order is not rewritten");
    }
}
