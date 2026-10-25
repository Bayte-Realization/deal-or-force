using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The same diplomatic move from both chairs (Guarantee, AgreementSystem offers).
///
/// Two asymmetries are being closed, so each test exists twice where it can: once with the
/// player asking and the computer answering, once with the computer asking and the player
/// answering. A test that only ever puts the player on one side of the table cannot see the
/// thing being fixed - which is how both of these survived in the first place.
/// </summary>
static class DiplomaticSymmetryTests
{
    static void Check(bool condition, string caption)
    { if (!condition) throw new Exception("FAIL diplomatic symmetry: " + caption); Console.WriteLine("PASS diplomatic symmetry: " + caption); }

    /// <summary>Four countries in a line, one of them the player. No wars, no organizations.</summary>
    static Campaign World(int playerId = 0)
    {
        var c = new Campaign(771033) { ScenarioId = "symmetry_fixture" };
        for (var i = 0; i < 4; i++)
        {
            var n = c.AddNation(new Nation
            {
                Tag = "SY" + i, Name = "Country " + i, MapIndex = 700 + i,
                Population = 1_000_000, Gdp = 100, Treasury = 40, PoliticalPoints = 100,
                Stability = .8f, Region = "symmetry"
            });
            c.States.Add(new ScenarioState
            {
                Id = "sy_" + i, MapIndex = 4001 + i, NationId = n.Tag,
                NationMapIndex = n.MapIndex, Population = n.Population
            });
        }
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(
            Enumerable.Range(0, 16).Select(i => (ushort)(4001 + i / 4)).ToArray(),
            Enumerable.Range(0, 16).Select(i => (ushort)(700 + i / 4)).ToArray(), 16, 1, _ => false));
        c.ConfigureSinglePlayer(playerId);
        foreach (var n in c.Nations) n.IsHuman = n.Id == playerId;
        for (var a = 0; a < 4; a++)
            for (var b = a + 1; b < 4; b++)
                CampaignDiplomacy.Get(c, a, b).Opinion = 60;
        return c;
    }

    static void Day(Campaign c, int day) => DomesticCrisesTests.Day(c, day);
    static void Apply(Campaign c, ICampaignCommand command)
    { c.Commands.Enqueue(command); c.Commands.ApplyAll(c); }

    public static void Run()
    {
        GuaranteeSymmetry();
        HumanPatronSymmetry();
        GuaranteeAnswerTargets();
        AgreementOfferSymmetry();
    }


    static void GuaranteeSymmetry()
    {
        // The player asks a computer and is answered on the spot.
        var c = World();
        var patron = c.NationById(1);
        var relation = CampaignDiplomacy.Get(c, 0, 1);
        var request = new RequestGuaranteeCommand { IssuerId = 0, TargetId = patron.Id };
        Apply(c, request);
        Check(request.Granted && relation.GuaranteedBy(patron.Id) && !request.Waiting,
            "a player may ask for a guarantee and the computer answers at once");
        Check(relation.GuaranteeRequestedBy < 0, "an answered request leaves nothing pending");

        var again = new RequestGuaranteeCommand { IssuerId = 0, TargetId = patron.Id };
        Apply(c, again);
        Check(!again.Granted && again.Answer.Length > 0,
            "asking a country that already guarantees you is refused with a reason");

        // The same arithmetic decides it either way: too little regard is a no.
        var cold = World();
        cold.NationById(1).Stability = .35f;
        CampaignDiplomacy.Get(cold, 0, 1).Opinion = -20f;
        var coldRequest = new RequestGuaranteeCommand { IssuerId = 0, TargetId = 1 };
        Apply(cold, coldRequest);
        Check(!coldRequest.Granted && !CampaignDiplomacy.Get(cold, 0, 1).GuaranteedBy(1),
            "a patron that does not want the promise says no instead of being committed");

        // A shared rival is worth more than fondness: same opinion, different answer.
        var without = World();
        without.NationById(1).Stability = .5f;
        CampaignDiplomacy.Get(without, 0, 1).Opinion = 0f;
        var with = World();
        with.NationById(1).Stability = .5f;
        CampaignDiplomacy.Get(with, 0, 1).Opinion = 0f;
        var rival = with.NationById(3);
        CampaignDiplomacy.Get(with, 1, 3).Opinion = -80f;
        CampaignDiplomacy.Get(with, 0, 3).Opinion = -80f;
        var needed = with.Defines.GuaranteeWillingness;
        Check(Guarantee.Willingness(without, without.NationById(1), without.NationById(0)) < needed,
            "without a common rival the answer is no");
        Check(Guarantee.Willingness(with, with.NationById(1), with.NationById(0)) >= needed,
            "a shared rival is what buys the promise, exactly as when the computer asks");
        Check(Guarantee.SharedRival(with, with.NationById(1), with.NationById(0))?.Id == rival.Id,
            "the shared rival is found from the tie, not from a caller's hint");
    }

    static void HumanPatronSymmetry()
    {
        // A human patron is asked, never committed, and the question can be answered.
        var human = World(playerId: 1);
        var ask = new RequestGuaranteeCommand { IssuerId = 0, TargetId = 1 };
        Apply(human, ask);
        Check(!ask.Granted && ask.Waiting && !CampaignDiplomacy.Get(human, 0, 1).GuaranteedBy(1),
            "a request to a player waits instead of promising on their behalf");
        Check(Guarantee.RequesterTo(human, human.NationById(1))?.Id == 0, "the player is shown who is waiting");
        // A player is not buried: the limit is what stops the whole world asking at once.
        human.Defines.GuaranteeInboxLimit = 1;
        var blocked = new RequestGuaranteeCommand { IssuerId = 2, TargetId = 1 };
        Apply(human, blocked);
        Check(!blocked.Granted && !blocked.Waiting && Guarantee.Outstanding(human, 1) == 1,
            "a player already holding the limit of requests is not asked again");
        Apply(human, new AnswerGuaranteeRequestCommand { IssuerId = 1, TargetId = 0, Accept = true });
        Check(CampaignDiplomacy.Get(human, 0, 1).GuaranteedBy(1) &&
              !Guarantee.Pending(human, CampaignDiplomacy.Get(human, 0, 1)),
            "the player's yes is the promise, and it clears the request");

        // Refusing and silence are different: a refusal is a date, silence is not a no.
        var refusal = World(playerId: 1);
        Apply(refusal, new RequestGuaranteeCommand { IssuerId = 0, TargetId = 1 });
        Apply(refusal, new AnswerGuaranteeRequestCommand { IssuerId = 1, TargetId = 0, Accept = false });
        Check(!CampaignDiplomacy.Get(refusal, 0, 1).GuaranteedBy(1) &&
              Guarantee.Refusal(refusal, refusal.NationById(0), refusal.NationById(1)).Length > 0,
            "a refused ask is remembered and cannot be repeated at once");

        var lapse = World(playerId: 1);
        Apply(lapse, new RequestGuaranteeCommand { IssuerId = 0, TargetId = 1 });
        Day(lapse, lapse.Defines.GuaranteeReplyDays + 1);
        Guarantee.Tick(lapse);
        Check(!Guarantee.Pending(lapse, CampaignDiplomacy.Get(lapse, 0, 1)) &&
              !CampaignDiplomacy.Get(lapse, 0, 1).GuaranteedBy(1),
            "an unanswered request lapses and promises nothing");
    }

    static void GuaranteeAnswerTargets()
    {
        var c = World(playerId: 1);
        Apply(c, new RequestGuaranteeCommand { IssuerId = 0, TargetId = 1 });
        Apply(c, new RequestGuaranteeCommand { IssuerId = 2, TargetId = 1 });
        Apply(c, new AnswerGuaranteeRequestCommand { IssuerId = 1, TargetId = 2, Accept = true });
        Check(CampaignDiplomacy.Peek(c, 1, 2).GuaranteedBy(1) && !CampaignDiplomacy.Peek(c, 0, 1).GuaranteedBy(1),
            "answering the selected requester does not accidentally guarantee the oldest one");
        Check(Guarantee.Pending(c, CampaignDiplomacy.Peek(c, 0, 1)), "the other request stays pending");
        var before = CampaignFingerprint.Of(c);
        Apply(c, new AnswerGuaranteeRequestCommand { IssuerId = 1, TargetId = 3, Accept = true });
        Check(CampaignFingerprint.Of(c) == before, "answering a country with no request changes nothing");
        CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.AtWar;
        before = CampaignFingerprint.Of(c);
        Apply(c, new AnswerGuaranteeRequestCommand { IssuerId = 1, TargetId = 0, Accept = true });
        Check(CampaignFingerprint.Of(c) == before, "war invalidates an old guarantee request before acceptance");
    }

    static void AgreementOfferSymmetry()
    {
        // The gate that used to stop every computer offer to a player. It is the same gate the
        // computer passes for another computer, so it is the one thing worth asserting first.
        var c = World();
        Check(AgreementSystem.CanPropose(c, c.NationById(1), c.NationById(0), AgreementKind.TradeAgreement, out _),
            "a computer may ask a player for a trade agreement at all");

        var offer = new ProposeAgreementCommand { IssuerId = 1, TargetId = 0, Kind = AgreementKind.TradeAgreement };
        Apply(c, offer);
        var relation = CampaignDiplomacy.Get(c, 0, 1);
        Check(offer.Result == AgreementResult.Offered && !relation.TradeAgreement,
            "the computer's offer waits on the player's table and signs nothing");
        Check(AgreementSystem.OfferedBy(c, c.NationById(0), 1, out var offeredKind) &&
              offeredKind == AgreementKind.TradeAgreement,
            "the player is shown the offer, with what it is an offer of");
        Check(!AgreementSystem.CanPropose(c, c.NationById(0), c.NationById(1),
                AgreementKind.TradeAgreement, out var busy) && busy.Length > 0,
            "the player cannot propose over an offer that is already on the table");

        var answer = new AnswerAgreementCommand { IssuerId = 0, TargetId = 1, Accept = true };
        Apply(c, answer);
        Check(answer.Result == AgreementResult.Signed && relation.TradeAgreement,
            "answering yes signs exactly what was offered");
        var twice = new AnswerAgreementCommand { IssuerId = 0, TargetId = 1, Accept = true };
        Apply(c, twice);
        Check(twice.Result == AgreementResult.NotAsked, "an answered offer cannot be answered again");

        // A refusal is a real answer, and it buys the asker the usual silence.
        var no = World();
        Apply(no, new ProposeAgreementCommand { IssuerId = 1, TargetId = 0, Kind = AgreementKind.NonAggressionPact });
        var declined = new AnswerAgreementCommand { IssuerId = 0, TargetId = 1, Accept = false };
        Apply(no, declined);
        Check(declined.Result == AgreementResult.Refused &&
              !CampaignDiplomacy.Get(no, 0, 1).NonAggressionPact &&
              CampaignDiplomacy.Get(no, 0, 1).AgreementRefusedDay == no.ElapsedDays,
            "refusing an offer is remembered as a date, not a grudge");

        // Silence is not a refusal: the offer simply stops waiting.
        var lapse = World();
        Apply(lapse, new ProposeAgreementCommand { IssuerId = 1, TargetId = 0, Kind = AgreementKind.NonAggressionPact });
        Day(lapse, lapse.Defines.AgreementReplyDays + 1);
        AgreementSystem.Tick(lapse);
        Check(!AgreementSystem.OfferedBy(lapse, lapse.NationById(0), 1, out _) &&
              !CampaignDiplomacy.Get(lapse, 0, 1).NonAggressionPact &&
              CampaignDiplomacy.Get(lapse, 0, 1).AgreementRefusedDay == -9999,
            "an unanswered offer lapses without becoming a refusal");
    }
}
