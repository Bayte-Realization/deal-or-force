using System;
using System.Linq;
using DealOrForce.Simulation;

static class AiDiplomacyTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run(Campaign c)
    {
        var player = c.NationByTag("FRA");
        c.ConfigureSinglePlayer(player.Id);
        var ai = c.Nations.First(n => n.Id != player.Id && !n.IsHuman && !n.IsSubject &&
            n.Population > 0 && !CampaignDiplomacy.AtWar(c, n.Id, player.Id));
        var humanBond = CampaignDiplomacy.Get(c, ai.Id, player.Id);
        humanBond.TradeAgreement = false;
        humanBond.NonAggressionPact = false;
        humanBond.Stance = DiplomaticStance.Friendly;
        humanBond.Opinion = 80;
        humanBond.LastActionDay = -9999;
        humanBond.AllianceOfferedDay = -9999;
        AiStatecraft.TryFriendlyMove(c, ai, player);
        Check(!humanBond.TradeAgreement && !humanBond.NonAggressionPact,
            "AI cannot sign a trade agreement or pact for a human");
        Check(humanBond.AllianceOfferedBy == ai.Id && humanBond.AllianceOfferedDay == c.ElapsedDays,
            "AI sends an answerable alliance offer to a human");

        var partner = c.Nations.First(n => n.Id != ai.Id && n.Id != player.Id && !n.IsHuman &&
            !n.IsSubject && n.Population > 0 && !CampaignDiplomacy.AtWar(c, n.Id, ai.Id));
        var bond = CampaignDiplomacy.Get(c, ai.Id, partner.Id);
        bond.TradeAgreement = false;
        bond.NonAggressionPact = false;
        bond.Stance = DiplomaticStance.Friendly;
        bond.Opinion = 80;
        bond.LastActionDay = -9999;
        AiStatecraft.TryFriendlyMove(c, ai, partner);
        Check(bond.TradeAgreement && !bond.NonAggressionPact,
            "AI trade agreement uses the same proposal command as the player");
        bond.LastActionDay = -9999;
        bond.Opinion = 80;
        AiStatecraft.TryFriendlyMove(c, ai, partner);
        Check(bond.NonAggressionPact, "AI proposes a non-aggression pact after trade");

        bond.EmbargoByA = ai.Id == bond.A;
        bond.EmbargoByB = ai.Id == bond.B;
        bond.Opinion = 20;
        Check(AiStatecraft.TryFriendlyMove(c, ai, partner) && !bond.EmbargoBy(ai.Id),
            "AI lifts its own embargo after reconciliation");

        bond.Stance = DiplomaticStance.Allied;
        bond.Opinion = 80;
        ai.Gdp = MathF.Max(100, ai.Gdp);
        ai.Treasury = ai.Gdp;
        partner.Gdp = MathF.Max(100, partner.Gdp);
        partner.Treasury = 0;
        bond.AidSentDay = -9999;
        var before = ai.Treasury;
        Check(AiStatecraft.TryFriendlyMove(c, ai, partner) && ai.Treasury < before && partner.Treasury > 0,
            "AI sends a real aid package to a friend in fiscal crisis");
        var after = ai.Treasury;
        Check(!AiStatecraft.TryFriendlyMove(c, ai, partner) && ai.Treasury == after,
            "aid cooldown prevents weekly repeat transfers");

        bond.Stance = DiplomaticStance.Rival;
        bond.Opinion = -60;
        bond.LastActionDay = -9999;
        Check(AiStatecraft.ActShortOfWar(c, ai, partner) && !bond.TradeAgreement && bond.NonAggressionPact,
            "AI ends trade with a new rival through the normal cancellation command");
        bond.LastActionDay = -9999;
        var notoriety = ai.Notoriety;
        Check(AiStatecraft.ActShortOfWar(c, ai, partner) && !bond.NonAggressionPact &&
              ai.Notoriety > notoriety,
            "AI pays the normal reputation cost when breaking a pact");

        bond.Reasons.Clear();
        bond.Reasons.Add(new OpinionReason
        {
            Id = "improved_relations", Stacks = 4, GrantedDay = -1, ExpiresDay = c.ElapsedDays + 900
        });
        OpinionSystem.Settle(c, bond);
        bond.Stance = DiplomaticStance.Rival;
        bond.LastActionDay = -9999;
        // Improving relations is a paid diplomatic mission now (DiplomaticOutreach,
        // docs/DIPLOMATIC_MISSIONS_ORGANIC_BLOCS_2026_10_20_RU.md): it costs political credit, of
        // which a freshly loaded campaign has none, and a rivalry ends at the mission's first
        // weekly payment rather than on the day it is sent.
        ai.PoliticalPoints = 100;
        Check(AiStatecraft.TryFriendlyMove(c, ai, partner) && DiplomaticOutreach.Of(bond, ai.Id) is not null &&
              !bond.TradeAgreement,
            "AI spends its one move on a goodwill mission to a rival whose opinion has recovered");
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)(c.ElapsedDays + 7) * 24);
        DiplomaticOutreach.Tick(c, bond);
        Check(bond.Stance == DiplomaticStance.Friendly,
            "AI reconciles an old rivalry when sustained diplomacy has repaired opinion");

        humanBond.Reasons.Clear();
        humanBond.Reasons.Add(new OpinionReason
        {
            Id = "denounced", Stacks = 3, GrantedDay = -1, ExpiresDay = c.ElapsedDays + 1100
        });
        OpinionSystem.Settle(c, humanBond);
        humanBond.Stance = DiplomaticStance.Friendly;
        humanBond.LastActionDay = -9999;
        new DenounceCommand { IssuerId = ai.Id, TargetId = player.Id }.Apply(c);
        Check(humanBond.Stance == DiplomaticStance.Rival,
            "denouncing a former friend can now turn the relationship into rivalry");
    }
}
