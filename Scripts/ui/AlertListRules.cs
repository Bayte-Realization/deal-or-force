using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// The rules of the alert list under the date plate (docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md,
/// step 4), kept apart from the Godot controls so they can be tested without the engine:
/// Tools/FormableTests/AlertListTests.cs holds a word-for-word copy and a source check holds the
/// copy to this file.
///
/// What they decide: the order (must, should, important, can do, and a calm world line last; inside
/// one of them what is happening before what waits, the sooner deadline first), the colour of the dot that says how bad a line is, where DEFCON and world tension stand among the
/// player's own alerts, which line becomes "Next", and how many lines wait behind "+N more".
/// </summary>
internal static class AlertListRules
{
    /// <summary>The rank of a world line that reports nothing wrong: below every alert.</summary>
    internal const int Calm = 4;

    /// <summary>Lower is higher on the list. "Important" (a war, a battle, an election coming) sits above "can do".</summary>
    internal static int Rank(CampaignAlertGroup group) => group switch
    {
        CampaignAlertGroup.Must => 0, CampaignAlertGroup.Should => 1, CampaignAlertGroup.Important => 2, _ => 3
    };

    /// <summary>
    /// The order inside one rank, lower first: what is already happening to us (a critical situation,
    /// a nuclear strike, divisions without orders, a stopped treasury) before what waits for an answer
    /// (an offer, an event, an election result), and the sooner deadline first. The reader sorts a
    /// group by id, which put three trade offers ("deal:") ahead of a nuclear strike and cut the strike
    /// behind "+N more"; the list keeps only a few rows, so this order decides what is seen.
    /// </summary>
    internal static (int Tier, int Deadline) Urgency(CampaignAlert alert) =>
        (alert.Situation?.Priority == SituationPriority.Awaiting || alert.Route is CampaignAlertRoute.Event or CampaignAlertRoute.Elections ? 1 : 0,
            alert.Situation is not null && alert.Situation.DeadlineDay >= 0 ? alert.Situation.DeadlineDay : int.MaxValue);

    /// <summary>The dot at the end of a row: red only for what must be done, amber for should, green for can.</summary>
    internal static string Dot(CampaignAlertGroup group) => group switch
    {
        CampaignAlertGroup.Must => "state.negative", CampaignAlertGroup.Should => "state.warning",
        CampaignAlertGroup.Could => "state.positive", _ => "state.info"
    };

    /// <summary>DEFCON 1 and 2 stand with what must be seen now, 3 with should, 4 as news; 5 is calm.</summary>
    internal static int DefconRank(int level) => level switch { <= 2 => 0, 3 => 1, 4 => 2, _ => Calm };

    /// <summary>
    /// World tension earns a row from "tense" (40 of 100, the conflicts screen's own word) and climbs
    /// with it; below that it has nothing to say and takes no row (-1).
    /// </summary>
    internal static int TensionRank(float tension) => tension >= 80f ? 0 : tension >= 60f ? 1 : tension >= 40f ? 2 : -1;

    /// <summary>The tension row's dot, in the conflicts screen's colours for the same thresholds.</summary>
    internal static string TensionDot(float tension) => tension >= 80f ? "state.critical" : tension >= 60f ? "state.warning" : "state.info";

    /// <summary>`hud.alerts.max_rows`, kept to a list that still fits a 720-pixel screen.</summary>
    internal static int MaxRows(float metric) => float.IsFinite(metric) ? Math.Clamp((int)MathF.Round(metric), 1, 12) : 6;

    /// <summary>
    /// Worst first, in a stable order (lines of one rank keep the order they came in). The first line
    /// that may lead becomes "Next" and is not repeated below it; then at most <paramref name="maxRows"/>
    /// rows, and the count of the rest for "+N more".
    /// </summary>
    internal static (T Next, T[] Rows, int More) Arrange<T>(IEnumerable<T> lines, Func<T, int> rank, Func<T, bool> canLead, int maxRows)
        where T : class
    {
        var ordered = lines.Select((line, index) => (line, index)).OrderBy(pair => rank(pair.line)).ThenBy(pair => pair.index)
            .Select(pair => pair.line).ToList();
        var next = ordered.FirstOrDefault(canLead);
        if (next is not null) ordered.Remove(next);
        var shown = Math.Min(ordered.Count, Math.Max(0, maxRows));
        return (next, ordered.Take(shown).ToArray(), ordered.Count - shown);
    }
}
