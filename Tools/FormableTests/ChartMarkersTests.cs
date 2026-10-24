using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class ChartMarkersTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        Console.WriteLine("PASS chart marks: " + message);
    }

    public static void Run()
    {
        PositionFollowsTheSamplesNotTheCalendar();
        MarksAreRecordedFactsAndNothingElse();
        TheScaleIsExplainedFromTheStamps();
    }

    /// <summary>
    /// History is kept by age - every week of three years, then four-weekly, then quarterly - so
    /// a mark cannot be placed at its share of elapsed time. A control that did that would draw
    /// the oldest year of a long campaign as a thin band while its events stayed spread over it.
    /// </summary>
    private static void PositionFollowsTheSamplesNotTheCalendar()
    {
        var even = new[] { 0, 7, 14, 21, 28 };
        Check(Math.Abs(ChartMarkers.Position(even, 0)) < 0.0001f, "the first sample is the left edge");
        Check(Math.Abs(ChartMarkers.Position(even, 28) - 1f) < 0.0001f, "the last sample is the right edge");
        Check(Math.Abs(ChartMarkers.Position(even, 14) - 0.5f) < 0.0001f,
            "an evenly spaced day sits at its own fraction");
        Check(ChartMarkers.Position(even, -1) < 0f && ChartMarkers.Position(even, 29) < 0f,
            "a day outside the window is not clamped onto an edge it never reached");

        var thinned = new[] { 0, 7, 14, 200, 400 };
        var placed = ChartMarkers.Position(thinned, 100);
        Check(placed > 0.6f && placed < 0.63f, "a day between two unevenly spaced samples sits between them");
        Check(placed > 100f / 400f + 0.2f, "…and not at its share of the calendar");

        var repeated = new[] { 0, 7, 7, 14 };
        var atRepeat = ChartMarkers.Position(repeated, 7);
        Check(float.IsFinite(atRepeat) && atRepeat > 0f && atRepeat < 1f,
            "a repeated sample day places a mark instead of dividing by zero");
        Check(Math.Abs(ChartMarkers.Position(new[] { 100 }, 100)) < 0.0001f,
            "a single sample is the whole chart");
        Check(ChartMarkers.Position(Array.Empty<int>(), 5) < 0f, "no samples, no position");
    }

    private static void MarksAreRecordedFactsAndNothingElse()
    {
        var campaign = Campaign.CreateBootstrap(7);
        var kor = campaign.NationByTag("KOR");
        var prk = campaign.NationByTag("PRK");
        var bystander = campaign.Nations.First(nation => nation.Id != kor.Id && nation.Id != prk.Id);

        // The chronicle is restored rather than recorded so that each fact can sit on the day it
        // belongs to: Record stamps today, and the whole point of a mark is its date.
        static ChronicleEntry Entry(long sequence, int day, string kind, Nation actor, Nation target, bool isPublic) =>
            new(sequence, day, default, kind, actor.Id, target?.Id ?? -1, actor.Name, target?.Name ?? "",
                "", "", isPublic);
        campaign.Chronicle.Restore(new[]
        {
            Entry(1, 3, "construction_completed", kor, null, true),
            Entry(2, 7, "war_started", kor, prk, true),
            Entry(3, 10, "sovereign_default", kor, null, false),
            Entry(4, 40, "peace_signed", kor, prk, true)
        });

        var days = new[] { 0, 7, 14, 21, 28 };
        var marks = ChartMarkers.For(campaign, kor.Id, days);
        Check(marks.Any(mark => mark.Kind == "war_started"), "a war the country fought is marked");
        Check(marks.All(mark => mark.Kind != "construction_completed"),
            "routine bookkeeping is not history");
        Check(marks.All(mark => mark.Kind != "peace_signed"),
            "an event outside the chart's window is not marked at all");
        Check(marks.Any(mark => mark.Kind == "sovereign_default"),
            "the country's own private act belongs to its own record");
        Check(marks.All(mark => mark.Text.Length > 0 && !mark.Text.StartsWith("chronicle.", StringComparison.Ordinal)),
            "every mark carries a sentence rather than a translation token");
        Check(marks.SequenceEqual(marks.OrderBy(mark => mark.Day)), "marks are ordered left to right");

        var enemyMarks = ChartMarkers.For(campaign, prk.Id, days);
        Check(enemyMarks.Any(mark => mark.Kind == "war_started"),
            "the other side of the same war is marked too");
        Check(enemyMarks.All(mark => mark.Kind != "sovereign_default"),
            "a country's private act stays out of another country's ledger");
        Check(ChartMarkers.For(campaign, bystander.Id, days).Count == 0,
            "a war between two other countries marks neither edge of a bystander's chart");

        Check(ChartMarkers.For(campaign, kor.Id, days, limit: 1).Count == 1,
            "the limit bounds what one chart draws");
        Check(ChartMarkers.For(campaign, kor.Id, days, limit: 1)[0].Importance == 3,
            "over the limit the chart keeps what mattered, not what was merely recent");
        Check(ChartMarkers.For(campaign, kor.Id, days, limit: 0).Count == 0,
            "a zero limit draws nothing rather than everything");
        Check(ChartMarkers.For(campaign, kor.Id, Array.Empty<int>()).Count == 0,
            "a chart with no samples has no marks");

        var described = ChartMarkers.Describe(marks, limit: 1);
        Check(described.StartsWith("• ", StringComparison.Ordinal), "a tooltip lists marks as bullets");
        Check(described.Split('\n').Length == 1, "the tooltip respects its own line limit");
        Check(ChartMarkers.Describe(Array.Empty<ChartMarkers.Mark>()) == "", "no marks, no tooltip lines");
    }

    /// <summary>
    /// History is kept weekly for three years, four-weekly to ten, quarterly to forty, so a long
    /// curve has three spacings across it and a young one has a single spacing. The chart says
    /// which, in the words the reader would use, and reads it off the stamps it is drawing rather
    /// than off a promise about the storage rule.
    /// </summary>
    private static void TheScaleIsExplainedFromTheStamps()
    {
        Check(ChartSpan.Caption(Array.Empty<int>()) == "", "no points, nothing to explain");
        Check(ChartSpan.Caption(new[] { 5 }) == "", "one point is not an axis");

        var weekly = new[] { 0, 7, 14, 21, 28 };
        Check(ChartSpan.Caption(weekly) == "one point a week", "a young campaign says its points are weeks");

        var quarterly = new[] { 0, 91, 182, 273 };
        Check(ChartSpan.Caption(quarterly) == "one point a quarter", "a quarterly window says so");

        var unnamed = new[] { 0, 30, 60 };
        Check(ChartSpan.Caption(unnamed) == "one point every 30 days",
            "a spacing without a name is given its number instead of a wrong word");

        var repeated = new[] { 0, 7, 7, 14 };
        Check(ChartSpan.Caption(repeated) == "one point a week",
            "a day recorded twice does not invent a second spacing");

        // The real rule, read off CampaignHistory itself: weekly to three years, four-weekly to
        // ten, quarterly beyond. Change the thinning and this line changes with it.
        var days = new List<int>();
        for (var day = 0; day <= CampaignHistory.WeeklyDays; day += 7) days.Add(day);
        for (var day = CampaignHistory.WeeklyDays + 28; day <= CampaignHistory.MonthlyDays; day += 28) days.Add(day);
        for (var day = CampaignHistory.MonthlyDays + 91; day <= CampaignHistory.KeptDays; day += 91) days.Add(day);
        Check(ChartSpan.Caption(days) == "a week, then every fourth week, then a quarter",
            "a forty-year window names its three spacings once, in the order they cover");
        Check(ChartSpan.Caption(days.Where(day => day <= CampaignHistory.WeeklyDays).ToArray()) == "one point a week",
            "the same campaign early on is still simply weekly");
    }
}
