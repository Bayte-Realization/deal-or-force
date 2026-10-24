using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// What the points of a history chart are, in words a reader can act on.
///
/// History is kept by age rather than evenly - every week of the last three years, every fourth
/// week back to ten, a quarter beyond that - so a chart of a long campaign has three different
/// spacings across it. Without a caption the reader sees points bunch up towards the right and
/// cannot tell whether that is the country or the filing cabinet. The caption is derived from
/// the day stamps the chart is drawing, not from the constants, so it cannot disagree with the
/// curve beside it.
///
/// It lives in the simulation folder beside <see cref="ChartScale"/> for the same reason: the
/// headless stand compiles this folder and not the interface, and a rule about what a point means
/// must be checkable without opening a window.
/// </summary>
public static class ChartSpan
{
    /// <summary>
    /// One short line for under the axis: "one point a week", or for a campaign long enough to
    /// have been thinned, "a week, then every fourth week, then a quarter". Empty when there is
    /// nothing to explain - no points, one point, or stamps that do not advance.
    /// </summary>
    public static string Caption(IReadOnlyList<int> days)
    {
        if (days is null || days.Count < 2) return string.Empty;
        var names = new List<string>();
        var onlyGap = -1;
        for (var index = 1; index < days.Count; index++)
        {
            var gap = days[index] - days[index - 1];
            // A save can carry the same day twice; that explains nothing about the axis.
            if (gap <= 0) continue;
            if (onlyGap < 0) onlyGap = gap;
            else if (gap != onlyGap) onlyGap = 0;
            var name = Name(gap);
            if (!names.Contains(name)) names.Add(name);
        }
        if (names.Count == 0) return string.Empty;
        if (names.Count == 1) return onlyGap == 7 ? "one point a week" : "one point " + names[0];
        return string.Join(", then ", names);
    }

    private static string Name(int gap) => gap switch
    {
        7 => "a week",
        28 => "every fourth week",
        91 => "a quarter",
        _ => $"every {gap} days"
    };
}
