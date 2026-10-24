using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// What a history chart marks on its timeline, and where on that timeline each mark falls.
///
/// This lives in the simulation beside <see cref="ChartScale"/> for the same reason: it is a
/// decision about numbers, not about drawing them, and the headless test stand compiles this
/// folder and not the interface. Two rules here are easy to get wrong inside a control, and
/// both are wrong in a way a screenshot cannot show.
///
/// The first is position. History is kept by age rather than by count - every week of the
/// last three years, every fourth week back to ten, a point a quarter back to forty - so the
/// distance between two samples is not constant. A mark placed at the fraction of elapsed
/// time therefore drifts away from the curve beside it, worst in the oldest three quarters of
/// the chart. A day is placed between the two samples that bracket it instead, which is where
/// the reader's eye already is.
///
/// The second is the edge. An event older than the chart's first sample, or newer than its
/// last, is not drawn at all: clamping it to the edge would claim it happened on a day it did
/// not happen, and a timeline that lies about dates is worse than an empty one.
/// </summary>
public static class ChartMarkers
{
    /// <summary>One recorded fact, reduced to what a timeline needs to place and explain it.</summary>
    public readonly record struct Mark(int Day, string Kind, int Importance, string Text);

    /// <summary>
    /// How many marks one chart carries. A nine-year war can record hundreds of public facts;
    /// the chart wants the ones that shaped the curve, and the rest are still in the chronicle.
    /// </summary>
    public const int DefaultLimit = 48;

    /// <summary>
    /// Where a day falls on a series of samples, as a fraction of the plotted width, or -1 when
    /// it falls outside the series. The samples must be in ascending day order, which is how
    /// <see cref="CampaignHistory"/> keeps them.
    /// </summary>
    public static float Position(IReadOnlyList<int> days, int day)
    {
        if (days is null || days.Count == 0) return -1f;
        if (day < days[0] || day > days[^1]) return -1f;
        if (days.Count == 1) return 0f;
        // The last sample at or before `day`; the mark is then somewhere between it and the next.
        var low = 0;
        for (var high = days.Count - 1; low < high;)
        {
            var middle = (low + high + 1) / 2;
            if (days[middle] <= day) low = middle;
            else high = middle - 1;
        }
        if (low >= days.Count - 1) return 1f;
        var span = days[low + 1] - days[low];
        // A repeated day would be a broken series rather than a point in time; let it sit on
        // the earlier sample rather than divide by zero.
        var within = span <= 0 ? 0f : (day - days[low]) / (float)span;
        return (low + within) / (days.Count - 1);
    }

    /// <summary>
    /// The chronicle's marks for one country inside a chart's own day range, oldest first.
    ///
    /// Only what that country can see is read: <see cref="WorldChronicle.Read"/> already keeps
    /// another country's private decisions out, so a foreign ledger chart cannot show what its
    /// government did in secret. Routine rows - a building finished, aid sent - carry importance
    /// zero and are bookkeeping rather than history, so they do not become marks.
    /// </summary>
    public static IReadOnlyList<Mark> For(Campaign campaign, int nationId, IReadOnlyList<int> days,
        int limit = DefaultLimit)
    {
        if (campaign is null || days is null || days.Count == 0) return Array.Empty<Mark>();
        var first = days[0];
        var last = days[^1];
        var kept = new List<Mark>();
        // Newest first from the chronicle, which is also the order that survives the cut below.
        foreach (var entry in campaign.Chronicle.Read(nationId, nationId, world: false))
        {
            if (entry.Day < first || entry.Day > last) continue;
            var importance = NewsFeed.Importance(entry.Kind);
            if (importance <= 0) continue;
            kept.Add(new Mark(entry.Day, entry.Kind, importance, NewsFeed.TextFor(campaign, entry)));
        }
        if (kept.Count > limit && limit > 0)
        {
            // Over the limit, the chart keeps what shaped the curve first: a war outranks a
            // scandal, and of two wars the newer one is the one a reader is looking for.
            kept.Sort((left, right) => left.Importance != right.Importance
                ? right.Importance.CompareTo(left.Importance)
                : right.Day.CompareTo(left.Day));
            kept.RemoveRange(limit, kept.Count - limit);
        }
        else if (limit <= 0)
        {
            kept.Clear();
        }
        kept.Sort((left, right) => left.Day.CompareTo(right.Day));
        return kept;
    }

    /// <summary>Marks that share a day, for a tooltip that has room for one line per fact.</summary>
    public static string Describe(IEnumerable<Mark> marks, int limit = 4)
    {
        var text = "";
        var written = 0;
        foreach (var mark in marks ?? Array.Empty<Mark>())
        {
            if (limit > 0 && written >= limit) break;
            text += (text.Length == 0 ? "" : "\n") + "• " + mark.Text;
            written++;
        }
        return text;
    }
}
