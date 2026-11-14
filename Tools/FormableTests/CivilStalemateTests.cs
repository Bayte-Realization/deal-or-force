using System;
using DealOrForce.Simulation;

/// <summary>
/// A civil war whose front has stood still, and whose status quo the signature would
/// already accept, is asked for. A front that is still moving is not given away on points:
/// that surrender cost the bench eleven of fifteen recoveries.
/// </summary>
static class CivilStalemateTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("civil stalemate: " + label);
        Console.WriteLine("PASS civil stalemate: " + label);
    }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(947986506);
        Check(c.Nations[0].Id == 0 && c.Nations[0] == c.NationById(0),
            "nation id 0 sits at index 0");
        Check(SaveSystem.FormatVersion == 2, "save format stays 2");
        Check(SaveSystem.MapDataVersion == "2024.11.09-a", "map data stays 2024.11.09-a");

        var attacker = c.Nations[0];
        var defender = c.Nations[1];
        var stalled = War(c, attacker, defender, stalled: true, age: 770, score: 0f);
        var before = CampaignFingerprint.Of(c);
        Check(AiStatecraft.CivilFrozenOfferReady(c, stalled, attacker.Id),
            "a stalled civil war whose status quo already clears is asked for");
        Check(CampaignFingerprint.Of(c) == before,
            "asking the question does not change the fingerprint");

        stalled.Stalled = false;
        Check(!AiStatecraft.CivilFrozenOfferReady(c, stalled, attacker.Id),
            "a front that is still moving is not this, whatever the acceptance");
        stalled.Stalled = true;
        stalled.StartedDay = c.ElapsedDays - 400;
        Check(!AiStatecraft.CivilFrozenOfferReady(c, stalled, attacker.Id),
            "four hundred days is inside the ordinary stalemate, not past it");
        stalled.StartedDay = c.ElapsedDays - 770;
        stalled.CivilConflict = false;
        Check(!AiStatecraft.CivilFrozenOfferReady(c, stalled, attacker.Id),
            "a war between states is not offered by the civil rule");

        var crushing = War(c, attacker, defender, stalled: true, age: 770, score: 100f);
        // Acceptance is the RESPONDER's answer, not the proposer's willingness. The
        // attacker ahead on points asks the beaten defender, and vice versa.
        var beatenResponder = PeaceNegotiation.Acceptance(c, crushing, attacker.Id, PeaceTerms.StatusQuo);
        var winningResponder = PeaceNegotiation.Acceptance(c, crushing, defender.Id, PeaceTerms.StatusQuo);
        Check(winningResponder < PeaceTermCatalog.Of(PeaceTerms.StatusQuo).MinimumAcceptance,
            $"the winning responder still refuses the losing side's line ({winningResponder:0.0})");
        Check(!AiStatecraft.CivilFrozenOfferReady(c, crushing, defender.Id),
            "the losing proposer does not offer a line its winning opponent refuses");
        Check(beatenResponder >= PeaceTermCatalog.Of(PeaceTerms.StatusQuo).MinimumAcceptance,
            $"the beaten responder would sign the winning side's line ({beatenResponder:0.0})");
        Check(AiStatecraft.CivilFrozenOfferReady(c, crushing, attacker.Id),
            "the winning side may offer that accepted line because the front has stood still");
        var mirrored = War(c, attacker, defender, stalled: true, age: 770, score: -100f);
        Check(!AiStatecraft.CivilFrozenOfferReady(c, mirrored, attacker.Id) &&
              AiStatecraft.CivilFrozenOfferReady(c, mirrored, defender.Id),
            "reversing the score reverses the eligible proposer, not the acceptance meaning");

        var moving = War(c, attacker, defender, stalled: false, age: 770, score: 100f);
        Check(AiStatecraft.DecisivelyBeaten(c, moving, defender.Id),
            "on points the defender has lost");
        Check(!AiStatecraft.CivilFrozenOfferReady(c, moving, defender.Id),
            "a moving civil war is not surrendered on those points");
        AiStatecraft.Negotiate(c, defender, moving);
        Check(!moving.Ended && moving.PendingOfferFromId < 0,
            "and nobody writes the paper while the front is still moving");

        var signed = War(c, attacker, defender, stalled: true, age: 770, score: 0f);
        AiStatecraft.Negotiate(c, attacker, signed);
        Check(signed.Ended && signed.PeaceProposerId == attacker.Id,
            "two computers sign the status quo the acceptance already cleared");
        Check(signed.PeaceSummary == "A deadlock: each side kept the ground it held, and the line became the border." ||
              signed.PeaceSummary == "Тупик: каждая сторона сохранила землю, которую удерживала, и линия фронта стала границей.",
            "the signed line is the deadlock, not a white peace (" + signed.PeaceSummary + ")");

        defender.IsHuman = true;
        var asked = War(c, attacker, defender, stalled: true, age: 770, score: 0f);
        AiStatecraft.Negotiate(c, attacker, asked);
        Check(!asked.Ended && asked.PendingOfferFromId == attacker.Id &&
              asked.PendingOfferTerms == PeaceTerms.StatusQuo,
            "a person is asked, and their land is not taken by the asking");
        defender.IsHuman = false;
    }

    static StrategicWar War(Campaign c, Nation attacker, Nation defender, bool stalled, int age, float score) =>
        new()
        {
            Id = 9100,
            AttackerId = attacker.Id,
            DefenderId = defender.Id,
            CivilConflict = true,
            Stalled = stalled,
            StartedDay = c.ElapsedDays - age,
            WarScore = score,
            LastPeaceProposalDay = c.ElapsedDays - c.Defines.AiPeaceProposalInterval - 1
        };
}
