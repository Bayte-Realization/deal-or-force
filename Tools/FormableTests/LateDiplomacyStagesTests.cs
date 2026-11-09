using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class LateDiplomacyStagesTests
{
    private static void Check(bool condition, string note)
    {
        if (!condition) throw new Exception("late diplomacy: " + note);
        Console.WriteLine("PASS late diplomacy: " + note);
    }

    public static void Run()
    {
        MissionScanMatchesLiveRefusal();
        MissionCommandsStillCheckLiveCapacity();
        AllianceAssessmentMatchesLiveScore();
        MilitaryPermissionsIgnoreArchivedWars();
        ReviewsYieldWithoutChangingResults();
        InterleavedReviewsStayInTheirCampaign();
    }

    private static Campaign World(int seed = 6613)
    {
        var c = new Campaign(seed);
        for (var i = 0; i < 8; i++)
            c.AddNation(new Nation { Tag = "L" + i, Name = "Late " + i, MapIndex = 500 + i,
                Population = 1_000_000, Gdp = 100, Treasury = 100, PoliticalPoints = 100,
                Ideology = "liberalism", OutputScale = 1 });
        DomesticCrisesTests.Day(c, 140);
        return c;
    }

    private static void MissionScanMatchesLiveRefusal()
    {
        foreach (var language in new[] { "english", "russian" })
        foreach (var full in new[] { false, true })
        {
            var oldLanguage = Loc.Language;
            try
            {
                Loc.Language = language;
                var c = World();
                c.Defines.DiplomaticMissionLimit = 2;
                // Both directions and unrelated missions. Orphaned relations count exactly
                // as before; do not assume that every saved participant is still alive.
                CampaignDiplomacy.Get(c, 0, 1).MissionByA = new DiplomaticMission();
                CampaignDiplomacy.Get(c, 2, 3).MissionByA = new DiplomaticMission();
                if (full) CampaignDiplomacy.Get(c, 0, 999).MissionByA = new DiplomaticMission();
                CampaignDiplomacy.Get(c, 2, 4).MissionByB = new DiplomaticMission();
                CampaignDiplomacy.Get(c, 0, 5).EmbargoByA = true;
                CampaignDiplomacy.Get(c, 0, 6).LastActionDay = c.ElapsedDays;
                var before = CampaignFingerprint.Of(c);
                var rng = c.Random.State;
                var count = c.Relations.Count;
                var scan = new DiplomaticOutreach.OfferScan(c, 0);
                foreach (var issuer in new[] { 0, 4 })
                foreach (var target in new[] { -1, 0, 1, 2, 3, 4, 5, 6, 7, 999 })
                foreach (var active in new[] { false, true })
                    Check(DiplomaticOutreach.Refusal(c, issuer, target, active, scan) ==
                          DiplomaticOutreach.Refusal(c, issuer, target, active),
                        $"{language} full={full} issuer={issuer} target={target} active={active}: same refusal");
                Check(before == CampaignFingerprint.Of(c) && rng == c.Random.State && count == c.Relations.Count,
                    "the whole candidate scan is read-only, with no relation creation or random draw");
                var other = World(7711);
                Check(DiplomaticOutreach.Refusal(other, 0, 7, true, scan) == "",
                    "a scan passed another campaign falls back to that campaign's live count");
            }
            finally { Loc.Language = oldLanguage; }
        }
    }

    private static void MissionCommandsStillCheckLiveCapacity()
    {
        var c = World();
        c.Defines.DiplomaticMissionLimit = 1;
        var scan = new DiplomaticOutreach.OfferScan(c, 0);
        Check(DiplomaticOutreach.Refusal(c, 0, 1, true, scan) == "", "empty offer scan permits its first embassy");
        var first = new ImproveRelationsCommand { IssuerId = 0, TargetId = 1 };
        first.Apply(c);
        Check(first.Changed, "the first embassy starts normally");
        var before = CampaignFingerprint.Of(c);
        var second = new ImproveRelationsCommand { IssuerId = 0, TargetId = 2 };
        second.Apply(c);
        Check(!second.Changed && second.Refusal.Length > 0 && CampaignFingerprint.Of(c) == before,
            "a command rechecks live capacity, not the earlier offer scan");
        Check(DiplomaticOutreach.Refusal(c, 0, 2, true, new DiplomaticOutreach.OfferScan(c, 0)) == second.Refusal,
            "the next scan sees the accepted mission immediately");
        new ImproveRelationsCommand { IssuerId = 0, TargetId = 1, Active = false }.Apply(c);
        Check(DiplomaticOutreach.Refusal(c, 0, 2, true, new DiplomaticOutreach.OfferScan(c, 0)) == "",
            "a new scan sees recalled missions in the same day");
    }

    private static Campaign ReviewWorld(int seed = 6613, bool strained = true)
    {
        var c = World(seed);
        c.Defines.AllianceCohesionResponse = 0;
        for (var a = 0; a < 8; a++)
        for (var b = a + 1; b < 8; b++)
        {
            var r = CampaignDiplomacy.Get(c, a, b);
            r.Stance = DiplomaticStance.Allied;
            r.Opinion = a == 0 ? -90 : 80;
            r.AllianceSinceDay = 0;
            r.AllianceAssessmentDay = 0;
            r.AllianceCohesion = strained && a == 0 ? 0 : .8f;
            r.AllianceStrainedDays = strained && a == 0 ? 56 : 0;
        }
        return c;
    }

    private static void MilitaryPermissionsIgnoreArchivedWars()
    {
        var c = World();
        for (var i = 0; i < 2000; i++)
        {
            var old = new StrategicWar { Id = i, AttackerId = 0, DefenderId = 1 + i % 7, Ended = true };
            old.AttackerAllies.Add(2);
            c.Wars.Add(old);
        }
        var active = new StrategicWar { Id = 2000, AttackerId = 0, DefenderId = 3 };
        active.AttackerAllies.Add(1);
        c.Wars.Add(active);
        CampaignDiplomacy.Get(c, 0, 2).MilitaryAccessByB = true;
        CampaignDiplomacy.Get(c, 1, 3).Stance = DiplomaticStance.Rival;
        foreach (var ended in new[] { false, true })
        {
            active.Ended = ended; // Also exercise invalidation of a previously warm live list.
            var before = CampaignFingerprint.Of(c);
            for (var guest = 0; guest < 8; guest++)
            for (var host = 0; host < 8; host++)
            {
                Check(MilitaryAccess.Hostile(c, guest, host) == OldHostile(c, guest, host),
                    $"ended={ended} {guest}/{host}: hostility matches the full archive oracle");
                var expected = guest == host ||
                    (!OldHostile(c, guest, host) &&
                     (MilitaryAccess.Granted(c, guest, host) || c.Nations[guest].AnswersTo(host) ||
                      c.Nations[host].AnswersTo(guest) ||
                      c.Wars.Any(w => !w.Ended && WarCoalition.SameSide(w, guest, host))));
                Check(MilitaryAccess.CanEnter(c, guest, (ushort)c.Nations[host].MapIndex) == expected,
                    $"ended={ended} {guest}/{host}: entry matches live directional, subject and coalition permissions");
                var relation = CampaignDiplomacy.Peek(c, guest, host);
                var value = (relation?.Opinion ?? 0f) / 100f - MathF.Min(.35f, MathF.Max(0, c.Nations[guest].Notoriety) * .005f);
                if (relation?.Stance == DiplomaticStance.Allied) value += .5f;
                if (relation?.Stance == DiplomaticStance.Rival) value -= .6f;
                if (c.Wars.Any(w => !w.Ended && w.Involves(guest) && w.EnemiesOf(guest).Any(enemy =>
                        CampaignDiplomacy.Peek(c, host, enemy)?.Stance == DiplomaticStance.Rival))) value += .2f;
                Check(MilitaryAccess.Willingness(c, c.Nations[guest], c.Nations[host]) == value,
                    $"ended={ended} {guest}/{host}: willingness matches the archive oracle");
            }
            Check(before == CampaignFingerprint.Of(c), "permission queries leave the world unchanged");
        }
    }

    private static void AllianceAssessmentMatchesLiveScore()
    {
        var c = ReviewWorld();
        c.Wars.Add(new StrategicWar { Id = 9, AttackerId = 1, DefenderId = 3 });
        c.Wars.Add(new StrategicWar { Id = 10, AttackerId = 2, DefenderId = 4, Ended = true });
        CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.Rival;
        CampaignDiplomacy.Get(c, 2, 3).Stance = DiplomaticStance.Neutral;
        c.Nations[4].Notoriety = 60;
        c.Nations[5].Stability = .1f;
        var before = CampaignFingerprint.Of(c);
        var rng = c.Random.State;
        var count = c.Relations.Count;
        var scan = new AllianceSystem.OfferScan(c);
        for (var a = 0; a < 8; a++)
        for (var b = 0; b < 8; b++)
            if (a != b)
                Check(AllianceSystem.Willingness(c, c.Nations[a], c.Nations[b], null, false, scan) ==
                      AllianceSystem.Willingness(c, c.Nations[a], c.Nations[b], refreshAssessment: false),
                    $"alliance offer {a}/{b}: cached membership preserves the exact live score");
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng && c.Relations.Count == count,
            "the alliance assessment creates no relation, intelligence refresh or random draw");
        var other = World(7711);
        Check(AllianceSystem.Willingness(other, other.Nations[0], other.Nations[1], null, false, scan) ==
              AllianceSystem.Willingness(other, other.Nations[0], other.Nations[1], refreshAssessment: false),
            "an assessment cannot lend another campaign its alliances");
        CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.Allied;
        Check(AllianceSystem.Willingness(c, c.Nations[0], c.Nations[2], null, false, new AllianceSystem.OfferScan(c)) ==
              AllianceSystem.Willingness(c, c.Nations[0], c.Nations[2], refreshAssessment: false),
            "a new decision observes a same-day new alliance instead of a day-stamped stale cache");
    }

    private static bool OldHostile(Campaign c, int first, int second) => first != second &&
        (CampaignDiplomacy.Peek(c, first, second)?.Stance == DiplomaticStance.AtWar ||
         c.Wars.Any(w => !w.Ended && w.Opposes(first, second)));

    private static void ReviewsYieldWithoutChangingResults()
    {
        var direct = ReviewWorld();
        var staged = ReviewWorld();
        var before = CampaignFingerprint.Of(staged);
        using (var steps = AllianceMaintenance.TickStages(staged).GetEnumerator())
        {
            Check(steps.MoveNext() && before == CampaignFingerprint.Of(staged),
                "review yields after preparing its sorted graph, before changing the first alliance");
            var calls = 1;
            while (steps.MoveNext()) calls++;
            Check(calls == 29, "28 due alliances have 28 separate work slices plus the final yield");
        }
        AllianceMaintenance.Tick(direct);
        Check(CampaignFingerprint.Of(direct) == CampaignFingerprint.Of(staged),
            "staged maintenance preserves ordered ruptures, reasons and chronicle");

        direct = ReviewWorld(strained: false);
        staged = ReviewWorld(strained: false);
        before = CampaignFingerprint.Of(staged);
        using (var steps = AllyRivalShift.TickStages(staged).GetEnumerator())
        {
            Check(steps.MoveNext() && CampaignFingerprint.Of(staged) == before,
                "rivalry scan yields before considering its first due pair");
            var calls = 1;
            while (steps.MoveNext()) calls++;
            Check(calls == 29, "rivalry also separates all 28 due pairs, including quiet pairs");
        }
        AllyRivalShift.Tick(direct);
        Check(CampaignFingerprint.Of(direct) == CampaignFingerprint.Of(staged) &&
              staged.Relations.Values.Any(r => r.Stance == DiplomaticStance.Rival),
            "yielding changes no rivalry, reason or random state");
        staged.Defines.AllyRivalShift = false;
        Check(!AllyRivalShift.TickStages(staged).Any(), "disabled rivalry does no staged work");
        staged.Defines.DynamicAlliances = false;
        Check(!AllianceMaintenance.TickStages(staged).Any(), "disabled maintenance retains the pause path");
    }

    private static void InterleavedReviewsStayInTheirCampaign()
    {
        var first = ReviewWorld();
        var second = ReviewWorld(7711, false);
        var firstSolo = ReviewWorld();
        var secondSolo = ReviewWorld(7711, false);
        using var a = AllianceMaintenance.TickStages(first).GetEnumerator();
        using var b = AllianceMaintenance.TickStages(second).GetEnumerator();
        DrainAlternating(a, b);
        AllianceMaintenance.Tick(firstSolo);
        AllianceMaintenance.Tick(secondSolo);
        Check(CampaignFingerprint.Of(first) == CampaignFingerprint.Of(firstSolo) &&
              CampaignFingerprint.Of(second) == CampaignFingerprint.Of(secondSolo),
            "two interleaved maintenance graphs do not share due lists or ruptures");
        using var x = AllyRivalShift.TickStages(first).GetEnumerator();
        using var y = AllyRivalShift.TickStages(second).GetEnumerator();
        DrainAlternating(x, y);
        AllyRivalShift.Tick(firstSolo);
        AllyRivalShift.Tick(secondSolo);
        Check(CampaignFingerprint.Of(first) == CampaignFingerprint.Of(firstSolo) &&
              CampaignFingerprint.Of(second) == CampaignFingerprint.Of(secondSolo),
            "two interleaved rivalry scans retain their own sorted due list");
    }

    private static void DrainAlternating(IEnumerator<int> first, IEnumerator<int> second)
    {
        var moreFirst = true;
        var moreSecond = true;
        while (moreFirst || moreSecond)
        {
            if (moreFirst) moreFirst = first.MoveNext();
            if (moreSecond) moreSecond = second.MoveNext();
        }
    }
}
