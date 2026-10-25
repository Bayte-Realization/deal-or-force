using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The agreements short of an alliance, asked for by a player (item 65): a trade agreement and a
/// non-aggression pact answered on terms the interface can show, both of them breakable at a
/// price, and aid that moves money for goodwill.
/// </summary>
static class AgreementTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    static bool Remembers(DiplomaticRelation relation, string id) =>
        relation.Reasons.Any(reason => string.Equals(reason.Id, id, StringComparison.OrdinalIgnoreCase));

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260926);
        c.AttachProvinceTopology(map);
        var player = c.NationByTag("FRA");
        c.ConfigureSinglePlayer(player.Id);
        player.Treasury = 1_000f;

        // Somebody the player is at peace with, not allied to - an alliance is its own reason a
        // war cannot be declared - and has signed nothing with yet.
        var other = c.Nations
            .Where(n => n.Id != player.Id && !n.IsHuman && n.Gdp > 50f && c.StatesOf(n).Length > 0 &&
                        !CampaignDiplomacy.AtWar(c, player.Id, n.Id) &&
                        CampaignDiplomacy.Peek(c, player.Id, n.Id) is not { TradeAgreement: true } and
                            not { NonAggressionPact: true } and not { Stance: DiplomaticStance.Allied })
            .OrderByDescending(n => n.Gdp).First();
        Console.WriteLine($"  asking {other.Tag}");
        var relation = CampaignDiplomacy.Get(c, player.Id, other.Id);
        relation.LastActionDay = -9999;

        // --- A: who may be asked -------------------------------------------------------------
        Check(AgreementSystem.CanPropose(c, player, other, AgreementKind.TradeAgreement, out var refusal),
            $"the player may ask {other.Tag} for a trade agreement ({refusal})");
        Check(AgreementSystem.CanPropose(c, other, player, AgreementKind.TradeAgreement, out _),
            "a player may receive an agreement offer and answers for themselves");
        var incoming = new ProposeAgreementCommand { IssuerId = other.Id, TargetId = player.Id, Kind = AgreementKind.TradeAgreement };
        incoming.Apply(c);
        Check(incoming.Result == AgreementResult.Offered && !relation.TradeAgreement && relation.AgreementOfferedBy == other.Id,
            "an offer to a player never signs an agreement without consent");
        var decline = new AnswerAgreementCommand { IssuerId = player.Id, TargetId = other.Id, Accept = false };
        decline.Apply(c);
        Check(decline.Result == AgreementResult.Refused && !relation.TradeAgreement && relation.AgreementOfferedBy < 0,
            "the player can decline the incoming offer without being committed");
        // The remaining cases independently exercise the outgoing proposal and its cooldown.
        relation.AgreementRefusedDay = -9999;
        relation.LastActionDay = -9999;

        // --- B: the terms are named, and an enemy says no ------------------------------------
        relation.Opinion = -60f;
        var why = new Breakdown();
        var cold = AgreementSystem.Willingness(c, other, player, AgreementKind.TradeAgreement, why);
        Console.WriteLine("  terms: " + string.Join("; ", why.Entries.Select(e => $"{e.Source} {e.Value:+0.00;-0.00}")));
        Check(why.Entries.Count >= 1 && MathF.Abs(why.Total - cold) < 0.0001f,
            "the answer is the sum of terms the interface can show");
        var ask = new ProposeAgreementCommand { IssuerId = player.Id, TargetId = other.Id, Kind = AgreementKind.TradeAgreement };
        ask.Apply(c);
        Check(ask.Result == AgreementResult.Refused && !relation.TradeAgreement, "a country that dislikes us refuses");
        Check(relation.AgreementRefusedDay == c.ElapsedDays &&
              !AgreementSystem.CanPropose(c, player, other, AgreementKind.NonAggressionPact, out _),
            "and is not asked again the same season");

        // --- C: a friend signs ---------------------------------------------------------------
        relation.AgreementRefusedDay = -9999;
        relation.LastActionDay = -9999;
        relation.Opinion = 60f;
        ask = new ProposeAgreementCommand { IssuerId = player.Id, TargetId = other.Id, Kind = AgreementKind.TradeAgreement };
        ask.Apply(c);
        Check(ask.Result == AgreementResult.Signed && relation.TradeAgreement && Remembers(relation, "trade_agreement"),
            "a friend signs, and the agreement is remembered in their opinion");

        // --- D: a pact forbids a war, and breaking it frees one at a price --------------------
        relation.LastActionDay = -9999;
        relation.Opinion = 80f;
        ask = new ProposeAgreementCommand { IssuerId = player.Id, TargetId = other.Id, Kind = AgreementKind.NonAggressionPact };
        ask.Apply(c);
        Check(ask.Result == AgreementResult.Signed && relation.NonAggressionPact, "a close friend promises not to attack");
        CampaignDiplomacy.CanDeclareWar(c, player.Id, other.Id, out var blocked);
        Check(blocked == Loc.Get("A non-aggression pact is active."), $"and the pact stands in the way of a war ({blocked})");
        var notoriety = player.Notoriety;
        var opinion = relation.Opinion;
        var breakPact = new CancelAgreementCommand { IssuerId = player.Id, TargetId = other.Id, Kind = AgreementKind.NonAggressionPact };
        breakPact.Apply(c);
        Check(breakPact.Cancelled && !relation.NonAggressionPact &&
              MathF.Abs(player.Notoriety - notoriety - c.Defines.NonAggressionPactBreakNotoriety) < 0.001f,
            "a pact can be broken, and the world learns what our word is worth");
        Check(relation.Opinion < opinion && Remembers(relation, "broke_non_aggression_pact") &&
              !Remembers(relation, "non_aggression_pact"),
            "they remember the broken promise instead of the promise");
        CampaignDiplomacy.CanDeclareWar(c, player.Id, other.Id, out var after);
        Check(after != Loc.Get("A non-aggression pact is active."), $"and it no longer stands in the way ({after})");

        // --- E: a trade agreement can be walked out of -----------------------------------------
        var walk = new CancelAgreementCommand { IssuerId = player.Id, TargetId = other.Id, Kind = AgreementKind.TradeAgreement };
        walk.Apply(c);
        Check(walk.Cancelled && !relation.TradeAgreement && Remembers(relation, "cancelled_trade_agreement") &&
              !Remembers(relation, "trade_agreement"),
            "a trade agreement ends, and the walking out is remembered");

        // --- F: aid moves money for goodwill, once a season --------------------------------------
        var amount = AgreementSystem.AidAmount(c, player, other, out var aidRefusal);
        Check(amount > 0f, $"a package can be sent ({aidRefusal})");
        var ours = player.Treasury;
        var theirs = other.Treasury;
        var aid = new SendAidCommand { IssuerId = player.Id, TargetId = other.Id };
        aid.Apply(c);
        Check(MathF.Abs(aid.Sent - amount) < 0.001f && MathF.Abs(ours - amount - player.Treasury) < 0.01f &&
              MathF.Abs(theirs + amount - other.Treasury) < 0.01f,
            $"the money changes hands ({amount:0.00})");
        Check(Remembers(relation, "economic_aid"), "and is remembered in their opinion");
        Check(AgreementSystem.AidAmount(c, player, other, out _) <= 0f, "and the next package waits for the season");
    }
}
