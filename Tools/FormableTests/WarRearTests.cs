using System;
using DealOrForce.Simulation;

/// <summary>
/// The country card names a long war, a collapsing front, faster call-up, or a peace
/// Tick would force, and a coup line only when Coups already has one. Reading those
/// sentences does not write the campaign. At peace the weariness sentence is empty.
/// </summary>
public static class WarRearTests
{
    static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL war rear: " + text);
        Console.WriteLine("PASS war rear: " + text);
    }

    static Campaign World()
    {
        var campaign = new Campaign(241024) { ScenarioId = "war_rear" };
        campaign.Nations.Add(new Nation
        {
            Id = 0, Tag = "WR0", Name = "Reader", MapIndex = 1, IsHuman = true, Population = 4000000
        });
        campaign.Nations.Add(new Nation
        {
            Id = 1, Tag = "WR1", Name = "Neighbour", MapIndex = 2, Population = 4000000
        });
        return campaign;
    }

    static StrategicWar Open(Campaign campaign, int startedDay, int attacker = 0, int defender = 1)
    {
        var war = new StrategicWar
        {
            Id = 1, Name = "Rear war", AttackerId = attacker, DefenderId = defender,
            StartedDay = startedDay, AttackerLosses = 1f
        };
        campaign.Wars.Add(war);
        return war;
    }

    static bool ClaimsWar(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("war", StringComparison.OrdinalIgnoreCase)
            || text.Contains("front", StringComparison.OrdinalIgnoreCase)
            || text.Contains("peace can be forced", StringComparison.OrdinalIgnoreCase)
            || text.Contains("call-up", StringComparison.OrdinalIgnoreCase)
            || text.Contains("войн", StringComparison.OrdinalIgnoreCase)
            || text.Contains("фронт", StringComparison.OrdinalIgnoreCase)
            || text.Contains("призыв", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Мир можно", StringComparison.Ordinal);
    }

    public static void Run()
    {
        var quiet = World();
        Check(quiet.Nations.Count > 0 && quiet.Nations[0].Id == 0 && quiet.ProvinceMap is null,
            "nation id 0 stands at index 0, with no province topology");
        var before = CampaignFingerprint.Of(quiet);
        var peace = WarWeariness.Explain(quiet, quiet.Nations[0]);
        var beside = WarWeariness.BesideWarSupport(quiet, quiet.Nations[0]);
        Check(peace.Length == 0 && beside.Text.Length == 0 && beside.Level == 0 && !ClaimsWar(peace),
            "at peace the explanation does not claim a war");
        Check(CampaignFingerprint.Of(quiet) == before && quiet.Wars.Count == 0,
            "reading the peace line does not change the fingerprint");

        var fresh = World();
        Open(fresh, 0);
        Check(WarWeariness.Explain(fresh, fresh.Nations[0]).Length == 0,
            "a war that has only just begun does not invent weariness");

        var elsewhere = World();
        Open(elsewhere, -2000, attacker: 1, defender: 2);
        elsewhere.Wars[0].WarScore = -90f;
        elsewhere.Wars[0].AttackerOccupiedValue = 0.8f;
        Check(WarWeariness.Explain(elsewhere, elsewhere.Nations[0]).Length == 0,
            "another country's war does not put a sentence on this one");

        var length = World();
        var longWar = Open(length, -length.Defines.PeaceWearinessAfterDays);
        Check(WarWeariness.LengthWeight(length.Defines, length.Defines.PeaceWearinessAfterDays) == "long_war",
            "the existing length rule already calls this a long war");
        before = CampaignFingerprint.Of(length);
        var reserve = length.Nations[0].ManpowerReserve;
        var hours = length.ElapsedHours;
        var named = WarWeariness.Explain(length, length.Nations[0]);
        var shown = WarWeariness.BesideWarSupport(length, length.Nations[0]);
        Check(named.Contains("A long war") && shown.Text.Contains("A long war") &&
              !named.Contains("A peace can be forced") && !named.Contains("collaps"),
            "a long war is named, and a player's war is not said to be forced: " + named);
        Check(CampaignFingerprint.Of(length) == before && length.Nations[0].ManpowerReserve == reserve &&
              length.Nations[0].Modifiers.Count == 0 && !longWar.Ended && length.ElapsedHours == hours &&
              length.Wars.Count == 1,
            "reading the long-war line does not change the fingerprint or the war");

        var collapse = World();
        var collapsing = Open(collapse, 0);
        collapsing.WarScore = collapse.Defines.FrontCollapsingScore;
        Check(WarWeariness.HomeFrontMood(collapse.Defines, collapsing.WarScore, 0f) == "front_collapsing",
            "the existing mood rule already calls this front collapsing");
        before = CampaignFingerprint.Of(collapse);
        named = WarWeariness.Explain(collapse, collapse.Nations[0]);
        Check(named.Contains("The front is collapsing") && !named.Contains("A long war") &&
              !named.Contains("call-up") && !ClaimsWar(WarWeariness.Explain(World(), World().Nations[0])),
            "a collapsing front is named, and peace still is not a war: " + named);
        Check(CampaignFingerprint.Of(collapse) == before && !collapsing.Ended,
            "reading the collapsing-front line does not change the fingerprint");

        var badly = World();
        var losing = Open(badly, 0);
        losing.WarScore = badly.Defines.WarGoingBadlyScore;
        Check(WarWeariness.HomeFrontMood(badly.Defines, losing.WarScore, 0f) == "war_going_badly" &&
              WarWeariness.Explain(badly, badly.Nations[0]).Contains("The war is going badly") &&
              !WarWeariness.Explain(badly, badly.Nations[0]).Contains("collaps"),
            "a front that is losing, and not yet collapsing, is named as going badly");

        var defending = World();
        var against = Open(defending, 0, attacker: 1, defender: 0);
        against.WarScore = -defending.Defines.FrontCollapsingScore;
        Check(WarWeariness.Explain(defending, defending.Nations[0]).Contains("The front is collapsing"),
            "the defender's collapsing front is named from that side's score");

        var call = World();
        var overrun = Open(call, 0);
        overrun.AttackerOccupiedValue = 0.05f;
        var men = call.Nations[0].ManpowerReserve;
        before = CampaignFingerprint.Of(call);
        Check(WarWeariness.FasterCallUp(call, call.Nations[0]) && WarWeariness.Overrun(call, call.Nations[0]),
            "the existing overrun rule already calls this country up faster");
        named = WarWeariness.Explain(call, call.Nations[0]);
        Check(named.Contains("call-up is running faster") && !named.Contains("collaps") && !named.Contains("A long war"),
            "lost ground names the faster call-up and does not invent a collapse: " + named);
        Check(CampaignFingerprint.Of(call) == before && call.Nations[0].ManpowerReserve == men &&
              call.Nations[0].Modifiers.Count == 0,
            "reading the call-up line does not call anyone up");

        var both = World();
        var occupied = Open(both, 0);
        occupied.AttackerOccupiedValue = both.Defines.FrontCollapsingOccupied;
        named = WarWeariness.Explain(both, both.Nations[0]);
        Check(named.Contains("The front is collapsing") && named.Contains("call-up is running faster") &&
              named.IndexOf("collaps", StringComparison.Ordinal) < named.IndexOf("call-up", StringComparison.Ordinal),
            "a collapsing occupation names the front first and the call-up with it: " + named);

        var quietWar = World();
        quietWar.Nations[0].IsHuman = false;
        quietWar.Nations[1].IsHuman = false;
        var untouched = Open(quietWar, -quietWar.Defines.AiNoContactPeaceDays);
        untouched.AttackerLosses = 0f;
        Check(WarWeariness.NoContactPeaceReady(quietWar, untouched) && WarWeariness.PeaceCanBeForced(quietWar, untouched),
            "the existing no-contact rule already says a peace can be forced");
        before = CampaignFingerprint.Of(quietWar);
        named = WarWeariness.Explain(quietWar, quietWar.Nations[0]);
        Check(named.Contains("A peace can be forced.") && !named.Contains("A long war") && !untouched.Ended &&
              CampaignFingerprint.Of(quietWar) == before,
            "that flag is named, and reading it does not end the war: " + named);

        var played = World();
        var ours = Open(played, -played.Defines.AiNoContactPeaceDays);
        ours.AttackerLosses = 0f;
        Check(!WarWeariness.NoContactPeaceReady(played, ours) &&
              !WarWeariness.Explain(played, played.Nations[0]).Contains("A peace can be forced"),
            "a war with a player in it is not described as a peace that can be forced");

        var old = World();
        old.Nations[0].IsHuman = false;
        old.Nations[1].IsHuman = false;
        var exhausted = Open(old, -old.Defines.PeaceForcedEndDays);
        Check(WarWeariness.ExhaustionPeaceDue(old, exhausted) &&
              WarWeariness.LengthWeight(old.Defines, old.Defines.PeaceForcedEndDays) == "exhausted_by_war",
            "the existing exhaustion rule already ends an AI war and calls it exhausted");
        named = WarWeariness.Explain(old, old.Nations[0]);
        Check(named.Contains("Exhausted by war") && named.Contains("A peace can be forced.") && !exhausted.Ended,
            "an exhausted AI war names both facts and is still being fought: " + named);
        exhausted.PendingOfferFromId = 0;
        Check(!WarWeariness.ExhaustionPeaceDue(old, exhausted) &&
              !WarWeariness.Explain(old, old.Nations[0]).Contains("A peace can be forced"),
            "an offer already on the table is not described as a peace that can be forced");

        var off = World();
        Open(off, -off.Defines.PeaceWearinessAfterDays);
        off.FeatureOverrides["war_weariness"] = false;
        before = CampaignFingerprint.Of(off);
        Check(WarWeariness.Explain(off, off.Nations[0]).Length == 0 && CampaignFingerprint.Of(off) == before,
            "a switched-off weariness system adds no sentence and reading the silence changes nothing");

        var plot = World();
        plot.Nations[0].CoupPlotDay = 3;
        before = CampaignFingerprint.Of(plot);
        var coup = Coups.Notice(plot, plot.Nations[0]);
        var withCoup = WarWeariness.BesideWarSupport(plot, plot.Nations[0]);
        Check(coup.Text.Contains("Officers are plotting against the government.") && coup.Level == 2 &&
              withCoup.Text == coup.Text && !ClaimsWar(withCoup.Text) && plot.Nations[0].CoupPlotDay == 3 &&
              CampaignFingerprint.Of(plot) == before,
            "an existing plot is one line beside war support and is not rolled again: " + coup.Text);

        var ripe = World();
        ripe.Nations[0].Stability = 0f;
        ripe.Nations[0].CoupPlotDay = -1;
        before = CampaignFingerprint.Of(ripe);
        coup = Coups.Notice(ripe, ripe.Nations[0]);
        Check(Coups.Outlook(ripe, ripe.Nations[0]) == "high" && coup.Text.Contains("A coup is possible.") &&
              coup.Level == 1 && ripe.Nations[0].CoupPlotDay == -1 && ripe.Nations[0].Stability == 0f &&
              CampaignFingerprint.Of(ripe) == before,
            "a coup Outlook already calls high is named without starting a plot: " + coup.Text);

        var faint = World();
        faint.Nations[0].Stability = 0.34f;
        Check(Coups.Outlook(faint, faint.Nations[0]) == "low" && Coups.Notice(faint, faint.Nations[0]).Text.Length == 0,
            "a faint coup chance stays quiet, as the alert rail already does");

        var low = World();
        var unsupported = Open(low, 0);
        low.Nations[0].WarSupport = 0.10f;
        named = WarWeariness.BesideWarSupport(low, low.Nations[0]).Text;
        Check(!unsupported.Ended && named.IndexOf("desert", StringComparison.OrdinalIgnoreCase) < 0,
            "defections expose no read-only line, so none is invented");

        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var russianPeace = WarWeariness.Explain(World(), World().Nations[0]);
            var russianLong = WarWeariness.Explain(length, length.Nations[0]);
            var russianCollapse = WarWeariness.Explain(collapse, collapse.Nations[0]);
            var russianCall = WarWeariness.Explain(call, call.Nations[0]);
            var russianForce = WarWeariness.Explain(quietWar, quietWar.Nations[0]);
            var russianPlot = Coups.Notice(plot, plot.Nations[0]).Text;
            var russianRipe = Coups.Notice(ripe, ripe.Nations[0]).Text;
            before = CampaignFingerprint.Of(length);
            Check(russianPeace.Length == 0 && !ClaimsWar(russianPeace) &&
                  russianLong.Contains("Затяжная война") && russianCollapse.Contains("Фронт рушится") &&
                  russianCall.Contains("призыв идёт быстрее") && russianForce.Contains("Мир можно навязать") &&
                  russianPlot.Contains("заговор") && russianRipe.Contains("Переворот возможен") &&
                  CampaignFingerprint.Of(length) == before,
                "the same facts read in Russian without changing the fingerprint: " + russianLong);
        }
        finally { Loc.Language = language; }
    }
}
