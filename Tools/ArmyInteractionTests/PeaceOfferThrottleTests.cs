using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A computer government may not pester a player with peace offers (PeaceOffers.ToPlayerBlockReason):
/// one offer per AiPeaceOfferToPlayerIntervalDays per war, and after a refusal or a lapse only an
/// offer that is materially different - until AiPeaceOfferRepeatRefusedDays have passed. Between
/// two computer governments nothing changes. The spacing is saved and fingerprinted.
/// </summary>
static class PeaceOfferThrottleTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL peace offer throttle: " + label);
        Console.WriteLine("PASS: peace offer throttle " + label);
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);

    /// <summary>The two-country fixture with country 1 played by a person and country 0 by the computer.</summary>
    static Campaign Fixture(bool humanRecipient = true)
    {
        var c = PartialFrontWithdrawalTests.Fixture();
        c.Defines.AiPeaceOfferToPlayerIntervalDays = 60;
        c.Defines.AiPeaceOfferRepeatRefusedDays = 365;
        c.Defines.AiPeaceOfferMaterialProvinceShare = 0.2f;
        if (humanRecipient) c.ConfigureSinglePlayer(1);
        return c;
    }

    static PeaceDemand Demand(Campaign c, bool reparations, params int[] provinces)
    {
        var d = new PeaceDemand { WarId = c.Wars[0].Id, ProposerId = 0, Reparations = reparations };
        foreach (var p in provinces) d.Provinces.Add(p);
        return d;
    }

    public static void Run()
    {
        Interval();
        SameTermsAfterRefusal();
        LapseCountsAsRefusal();
        WrittenSettlements();
        ComputerToComputerUnchanged();
        PersistenceAndFingerprint();
        NotificationNotModal();
        Console.WriteLine("peace offer throttle: all passed");
    }

    static void Interval()
    {
        var c = Fixture(); var war = c.Wars[0];
        var computer = c.Nations[0]; var player = c.Nations[1];
        var start = c.ElapsedDays;
        Check(PeaceOffers.Put(c, war, computer, player, PeaceTerms.WhitePeace) &&
              PeaceOffers.AwaitingAnswer(war, player.Id), "first offer reaches the player");
        Check(war.LastOfferToPlayerDay == start, "delivery day is recorded");
        Check(PeaceOffers.Answer(c, war, player.Id, accept: false) && !war.Ended, "player refuses");
        Check(war.RefusedOfferDay == start && !war.RefusedOfferWasWritten && war.RefusedOfferTerms == PeaceTerms.WhitePeace,
            "the refused terms are remembered");

        // Every few days, as players saw: each attempt inside the interval is turned away,
        // whatever the terms.
        for (var day = start + 2; day < start + 60; day += 2)
        {
            SetDay(c, day);
            Check(!PeaceOffers.Put(c, war, computer, player, PeaceTerms.Ceasefire) && war.PendingOfferFromId < 0,
                $"no offer {day - start} days after the last one");
        }
        SetDay(c, start + 60);
        Check(PeaceOffers.Put(c, war, computer, player, PeaceTerms.Ceasefire) && PeaceOffers.AwaitingAnswer(war, player.Id),
            "a different offer is allowed once the interval has passed");
    }

    static void SameTermsAfterRefusal()
    {
        var c = Fixture(); var war = c.Wars[0];
        var computer = c.Nations[0]; var player = c.Nations[1];
        var start = c.ElapsedDays;
        PeaceOffers.Put(c, war, computer, player, PeaceTerms.WhitePeace);
        PeaceOffers.Answer(c, war, player.Id, accept: false);
        SetDay(c, start + 61);
        Check(PeaceOffers.ToPlayerBlockReason(c, war, false, null, PeaceTerms.WhitePeace).Length > 0 &&
              !PeaceOffers.Put(c, war, computer, player, PeaceTerms.WhitePeace),
            "the same terms are not sent again after the interval");
        SetDay(c, start + 364);
        Check(!PeaceOffers.Put(c, war, computer, player, PeaceTerms.WhitePeace), "nor within a year of the refusal");
        SetDay(c, start + 365);
        Check(PeaceOffers.Put(c, war, computer, player, PeaceTerms.WhitePeace), "the same terms may be asked again after a year");

        var never = Fixture(); var neverWar = never.Wars[0];
        never.Defines.AiPeaceOfferRepeatRefusedDays = 0;
        PeaceOffers.Put(never, neverWar, never.Nations[0], never.Nations[1], PeaceTerms.WhitePeace);
        PeaceOffers.Answer(never, neverWar, 1, accept: false);
        SetDay(never, never.ElapsedDays + 5000);
        Check(!PeaceOffers.Put(never, neverWar, never.Nations[0], never.Nations[1], PeaceTerms.WhitePeace) &&
              PeaceOffers.Put(never, neverWar, never.Nations[0], never.Nations[1], PeaceTerms.Capitulate),
            "with no repeat period the refused terms never return, other terms still do");
    }

    static void LapseCountsAsRefusal()
    {
        var c = Fixture(); var war = c.Wars[0];
        var start = c.ElapsedDays;
        PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], PeaceTerms.Ceasefire);
        SetDay(c, war.PendingOfferExpiresDay);
        PeaceOffers.Expire(c);
        Check(war.PendingOfferFromId < 0 && war.LastNegotiation == "The offer lapsed unanswered", "an ignored offer lapses");
        Check(war.RefusedOfferDay == war.PendingOfferExpiresDay || war.RefusedOfferDay == c.ElapsedDays,
            "the lapse is remembered as a refusal");
        Check(war.RefusedOfferTerms == PeaceTerms.Ceasefire, "with the terms that lapsed");
        SetDay(c, c.ElapsedDays + 59);
        Check(!PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], PeaceTerms.WhitePeace), "the interval counts from the lapse");
        SetDay(c, c.ElapsedDays + 1);
        Check(!PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], PeaceTerms.Ceasefire) &&
              PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], PeaceTerms.WhitePeace),
            "silence is not met with the same letter, but other terms may follow");
        Check(start >= 0, "fixture clock");
    }

    static void WrittenSettlements()
    {
        var c = Fixture(); var war = c.Wars[0];
        var ten = Enumerable.Range(100, 10).ToArray();
        var refused = Demand(c, false, ten);
        var oneMore = Demand(c, false, ten.Append(200).ToArray());
        var oneMoved = Demand(c, false, ten.Skip(1).Append(200).ToArray());
        var twoMoved = Demand(c, false, ten.Skip(2).Append(200).Append(201).ToArray());
        var dropped = Demand(c, false, ten.Take(7).ToArray());
        Check(!PeaceOffers.MateriallyDifferent(true, refused, PeaceTerms.WhitePeace, true, Demand(c, false, ten), PeaceTerms.WhitePeace, .2f),
            "the same border is the same paper");
        Check(!PeaceOffers.MateriallyDifferent(true, refused, PeaceTerms.WhitePeace, true, oneMore, PeaceTerms.WhitePeace, .2f),
            "one province more on a border of ten is the same paper (one change, a fifth of eleven is three)");
        Check(PeaceOffers.MateriallyDifferent(true, refused, PeaceTerms.WhitePeace, true, oneMoved, PeaceTerms.WhitePeace, .2f),
            "one province of ten swapped for another is two changes - a fifth - and so a different border");
        Check(PeaceOffers.MateriallyDifferent(true, refused, PeaceTerms.WhitePeace, true, twoMoved, PeaceTerms.WhitePeace, .2f),
            "two provinces of ten swapped is a different border");
        Check(PeaceOffers.MateriallyDifferent(true, refused, PeaceTerms.WhitePeace, true, dropped, PeaceTerms.WhitePeace, .2f),
            "asking for three provinces fewer is a different offer");
        Check(PeaceOffers.MateriallyDifferent(true, refused, PeaceTerms.WhitePeace, true, Demand(c, true, ten), PeaceTerms.WhitePeace, .2f),
            "adding an indemnity is a different offer");
        Check(PeaceOffers.MateriallyDifferent(true, refused, PeaceTerms.WhitePeace, false, null, PeaceTerms.WhitePeace, .2f),
            "a standing term after a written settlement is a different offer");

        // A refused written settlement on the table: the refusal keeps the demand itself.
        war.PendingOffer = refused; war.PendingOfferIsWritten = true; war.PendingOfferFromId = 0;
        war.PendingOfferDay = c.ElapsedDays; war.PendingOfferExpiresDay = c.ElapsedDays + PeaceOffers.OfferStandsForDays;
        Check(PeaceOffers.Answer(c, war, 1, accept: false), "written settlement refused");
        Check(war.RefusedOfferWasWritten && ReferenceEquals(war.RefusedOffer, refused), "the refused border is remembered");
        SetDay(c, c.ElapsedDays + 61);
        Check(PeaceOffers.ToPlayerBlockReason(c, war, true, Demand(c, false, ten), PeaceTerms.WhitePeace).Length > 0,
            "the same border is not put again");
        Check(PeaceOffers.ToPlayerBlockReason(c, war, true, twoMoved, PeaceTerms.WhitePeace).Length == 0,
            "a moved border may be put once the interval has passed");
    }

    static void ComputerToComputerUnchanged()
    {
        // Two identical worlds with nobody playing either side: one settles through Put, the
        // other through the negotiation directly, as before the throttle existed. Both end the
        // same way, and the throttle's fields are never touched - even when they would block a
        // player, because a computer recipient never reaches the throttle.
        var viaPut = Fixture(humanRecipient: false); var direct = Fixture(humanRecipient: false);
        viaPut.Wars[0].LastOfferToPlayerDay = viaPut.ElapsedDays;
        viaPut.Wars[0].RefusedOfferDay = viaPut.ElapsedDays;
        viaPut.Wars[0].RefusedOfferTerms = PeaceTerms.Capitulate;
        var put = PeaceOffers.Put(viaPut, viaPut.Wars[0], viaPut.Nations[0], viaPut.Nations[1], PeaceTerms.Capitulate);
        PeaceNegotiation.Resolve(direct, direct.Wars[0], 0, PeaceTerms.Capitulate);
        Check(put == viaPut.Wars[0].Ended && viaPut.Wars[0].Ended == direct.Wars[0].Ended &&
              viaPut.Wars[0].LastNegotiation == direct.Wars[0].LastNegotiation,
            $"computer-to-computer settlement is unchanged (ended {viaPut.Wars[0].Ended}/{direct.Wars[0].Ended})");
        Check(viaPut.Wars[0].PendingOfferFromId < 0, "nothing is left on a computer's table");
        var fresh = Fixture(humanRecipient: false);
        PeaceOffers.Put(fresh, fresh.Wars[0], fresh.Nations[0], fresh.Nations[1], PeaceTerms.WhitePeace);
        Check(fresh.Wars[0].LastOfferToPlayerDay < 0 && fresh.Wars[0].RefusedOfferDay < 0,
            "an offer between computers records no player spacing");
    }

    static void PersistenceAndFingerprint()
    {
        var c = Fixture(); var war = c.Wars[0];
        var untouched = CampaignFingerprint.Of(c);
        Check(CampaignFingerprint.Of(Fixture()) == untouched, "a world that never offered a player anything fingerprints alike");
        war.PendingOffer = Demand(c, true, 3, 7); war.PendingOfferIsWritten = true; war.PendingOfferFromId = 0;
        war.PendingOfferDay = c.ElapsedDays; war.PendingOfferExpiresDay = c.ElapsedDays + 60;
        war.LastOfferToPlayerDay = c.ElapsedDays;
        PeaceOffers.Answer(c, war, 1, accept: false);
        var refusedPrint = CampaignFingerprint.Of(c);
        Check(refusedPrint != untouched, "the refusal memory is part of the fingerprint");
        var other = CampaignFingerprint.Of(c);
        war.RefusedOffer.Provinces.Add(4);
        Check(CampaignFingerprint.Of(c) != other, "and so is the refused border");
        war.RefusedOffer.Provinces.Remove(4);

        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "peace-throttle"), "peace-throttle");
        Check(loaded.Success, "snapshot imports: " + loaded.Warning);
        var back = loaded.Campaign.Wars.Find(w => w.Id == war.Id);
        Check(back is not null && back.LastOfferToPlayerDay == war.LastOfferToPlayerDay &&
              back.RefusedOfferDay == war.RefusedOfferDay && back.RefusedOfferWasWritten &&
              back.RefusedOffer is { Reparations: true } r && r.Provinces.SetEquals(new[] { 3, 7 }),
            "the spacing and the refused settlement survive a save");
        loaded.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CampaignFingerprint.Of(loaded.Campaign) == refusedPrint, "and fingerprint the same after it");
    }

    static void NotificationNotModal()
    {
        var c = Fixture(); var war = c.Wars[0];
        c.IsPaused = false;
        PeaceOffers.Put(c, war, c.Nations[0], c.Nations[1], PeaceTerms.WhitePeace);
        var desk = SituationReport.Read(c, c.Nations[1]);
        Check(desk.Any(item => item.Route == SituationRoute.PeaceOffer && item.TargetId == war.Id),
            "the offer is an entry on the player's situation desk");
        Check(!c.IsPaused, "an offer arriving does not pause the campaign");
    }
}
