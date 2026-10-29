using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Cooperation papers: a refusal, a human signature, and an AI that stays quiet when the gate is shut.</summary>
static class DiplomaticActTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL diplomatic acts: " + label);
        Console.WriteLine("PASS diplomatic acts: " + label);
    }

    public static void Run()
    {
        RefusalAndQuietAi();
        HumanSignsBeforeCashMoves();
        DecisionDoesNotMoveTheTaxSlider();
        ManyMoreActs();
    }

    /// <summary>
    /// The cooperation desk is a real list, not a row of two: every act has a name, a price and
    /// a gate, and none of them touches the tax and budget sliders the player already owns.
    /// </summary>
    static void ManyMoreActs()
    {
        Check(DiplomaticActs.All.Length >= 17, "the cooperation desk offers seventeen acts or more");
        Check(DiplomaticActs.Quick.Length >= 4, "the map offers its own short list");
        var (campaign, asker, asked) = Pair(false);
        var relation = CampaignDiplomacy.Get(campaign, 0, 1);
        relation.Opinion = 80f;
        asker.TaxRate = 0.24f;
        asker.MilitarySpending = 0.12f;
        asker.WelfareSpending = 0.09f;
        asker.EducationSpending = 0.06f;
        var sliders = (asker.TaxRate, asker.MilitarySpending, asker.WelfareSpending, asker.EducationSpending);

        var signed = 0;
        foreach (var act in DiplomaticActs.All)
        {
            if (act == DiplomaticAct.LiftEmbargo) continue;   // nothing is embargoed in this fixture
            relation.Opinion = 80f;
            asker.Treasury = Math.Max(asker.Treasury, 40f);
            asker.DecisionAvailableDay.Clear();
            var command = new DiplomaticActCommand
            {
                IssuerId = asker.Id, TargetId = asked.Id, Act = act, Step = DiplomaticActStep.Propose
            };
            command.Apply(campaign);
            Check(command.Result.Length > 0, DiplomaticActs.Name(act) + " answers with a sentence either way");
            if (command.Result == "Signed.") signed++;
        }
        Check(signed >= 14, "fourteen of the acts can be signed by a willing partner");
        Check(asker.TaxRate == sliders.TaxRate && asker.MilitarySpending == sliders.MilitarySpending &&
              asker.WelfareSpending == sliders.WelfareSpending &&
              asker.EducationSpending == sliders.EducationSpending,
            "no diplomatic act moves a tax or budget slider - that is the internal affairs window's job");

        // A relief shipment is money arriving at the other end, and a port visit is two fleets.
        var (c2, giver, sufferer) = Pair(false);
        var r2 = CampaignDiplomacy.Get(c2, 0, 1);
        r2.Opinion = 80f;
        giver.Treasury = 40f; sufferer.Treasury = 10f;
        var relief = new DiplomaticActCommand
        {
            IssuerId = giver.Id, TargetId = sufferer.Id, Act = DiplomaticAct.DisasterRelief, Step = DiplomaticActStep.Propose
        };
        relief.Apply(c2);
        Check(relief.Result == "Signed." && sufferer.Treasury > 10f && giver.Treasury < 40f,
            "disaster relief moves money to the country that needs it");
        Check(sufferer.Modifiers.Count == 0 || sufferer.Modifiers.All(m => m.Id != "disaster_relief"),
            "and it leaves a memory rather than a national modifier");

        var (c3, host, guest) = Pair(false);
        CampaignDiplomacy.Get(c3, 0, 1).Opinion = 80f;
        host.NavalExperience = 0f; guest.NavalExperience = 0f;
        var port = new DiplomaticActCommand
        {
            IssuerId = host.Id, TargetId = guest.Id, Act = DiplomaticAct.PortVisit, Step = DiplomaticActStep.Propose
        };
        port.Apply(c3);
        Check(port.Result == "Signed." && host.NavalExperience > 0f && guest.NavalExperience > 0f,
            "a port visit trains both fleets");

        var (c4, strong, proud) = Pair(false);
        CampaignDiplomacy.Get(c4, 0, 1).Opinion = -30f;
        Check(DiplomaticActs.Refusal(c4, strong, proud, DiplomaticAct.IntelligenceSharing).Length > 0,
            "a country that dislikes us will not take our intelligence");
        Check(DiplomaticActs.Refusal(c4, strong, proud, DiplomaticAct.DisasterRelief).Length == 0,
            "but it will take our relief");
    }

    static (Campaign Campaign, Nation Asker, Nation Asked) Pair(bool askedIsHuman)
    {
        var campaign = new Campaign(26026) { ScenarioId = "diplomatic_acts" };
        var asker = new Nation
        {
            Id = 0, Tag = "ASK", Name = "Asker", CustomName = "Asker", MapIndex = 1,
            Population = 2_000_000, Gdp = 200, Treasury = 40, Stability = 0.7f
        };
        var asked = new Nation
        {
            Id = 1, Tag = "ASKD", Name = "Asked", CustomName = "Asked", MapIndex = 2,
            Population = 2_000_000, Gdp = 180, Treasury = 30, Stability = 0.7f, IsHuman = askedIsHuman
        };
        campaign.Nations.Add(asker);
        campaign.Nations.Add(asked);
        return (campaign, asker, asked);
    }

    static void RefusalAndQuietAi()
    {
        var (campaign, asker, asked) = Pair(false);
        Check(campaign.Nations[0].Id == 0 && campaign.ProvinceMap is null,
            "nation id 0 stands at index 0, with no province topology");
        var war = new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1 };
        campaign.Wars.Add(war);
        CampaignDiplomacy.Get(campaign, 0, 1).Stance = DiplomaticStance.AtWar;
        var treasury = asker.Treasury;
        Check(DiplomaticActs.Refusal(campaign, asker, asked, DiplomaticAct.Subsidy).Length > 0,
            "a subsidy offered in a war is refused");
        Check(!DiplomaticActs.TryAi(campaign, asker, asked),
            "the AI does not offer when the gate fails");
        Check(asker.Treasury == treasury && DiplomaticActs.Incoming(campaign, asked.Id) == 0,
            "a failed gate moves no cash and writes no paper");
        campaign.Wars.Clear();
        var relation = CampaignDiplomacy.Get(campaign, 0, 1);
        relation.Stance = DiplomaticStance.Neutral;
        relation.Opinion = -40f;
        Check(!DiplomaticActs.TryAi(campaign, asker, asked),
            "the AI does not sign when the asked country is unwilling");
        Check(asker.Treasury == treasury && relation.DiplomaticActBy < 0,
            "unwillingness writes neither a paper nor a payment");
    }

    static void HumanSignsBeforeCashMoves()
    {
        var (campaign, asker, asked) = Pair(true);
        var relation = CampaignDiplomacy.Get(campaign, 0, 1);
        relation.Opinion = 60f;
        var askerCash = asker.Treasury;
        var askedCash = asked.Treasury;
        var command = new DiplomaticActCommand
        {
            IssuerId = asker.Id, TargetId = asked.Id, Act = DiplomaticAct.Subsidy, Step = DiplomaticActStep.Propose
        };
        command.Apply(campaign);
        Check(command.Result == "Sent for signature." && relation.DiplomaticActBy == asker.Id,
            "a human target receives an inbox paper");
        Check(asker.Treasury == askerCash && asked.Treasury == askedCash,
            "sending the paper takes no cash");
        var answer = new DiplomaticActCommand
        {
            IssuerId = asked.Id, TargetId = asker.Id, Step = DiplomaticActStep.Accept
        };
        answer.Apply(campaign);
        Check(answer.Result == "Signed." && relation.DiplomaticActBy < 0,
            "the human signature closes the paper");
        Check(asker.Treasury == askerCash - campaign.Defines.DiplomaticSubsidy &&
              asked.Treasury == askedCash + campaign.Defines.DiplomaticSubsidy,
            "the subsidy moves only when it is signed");
    }

    static void DecisionDoesNotMoveTheTaxSlider()
    {
        var campaign = Campaign.CreateBootstrap(1709);
        var nation = campaign.NationByTag("ROU");
        nation.Treasury = 80f;
        nation.TaxRate = 0.22f;
        nation.MilitarySpending = 0.11f;
        nation.WelfareSpending = 0.08f;
        nation.EducationSpending = 0.07f;
        nation.ArmyExperience = 80f;
        nation.PoliticalPoints = 40f;
        nation.WarSupport = 0.4f;
        var command = new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = "general_staff_academy_expansion" };
        command.Apply(campaign);
        Check(nation.TaxRate == 0.22f && nation.MilitarySpending == 0.11f &&
              nation.WelfareSpending == 0.08f && nation.EducationSpending == 0.07f,
            "the staff academy does not move a tax or budget slider");
        Check(nation.Modifiers.Any(modifier => modifier.Id == "staff_academy_programme") && nation.WarSupport > 0.4f,
            "it grants the staff programme and raises war support instead");
    }
}
