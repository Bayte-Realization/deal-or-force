using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// The rules of the resource plate at the top left (docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md,
/// step 2), kept apart from the controls that draw it: which figures stand on the plate, which
/// wait behind its "…", and when a figure is in trouble.
///
/// Everything here is data a mod can change, read as skin metrics (game/gui/79_hud_resources.json):
///   hud.top_stats.primary.&lt;STAT&gt;  - the figure's place on the plate, 1 first; 0 or absent puts it
///                                    behind "…". At most `hud.resource_slots` of them stand there.
///   hud.stat_state.&lt;STAT&gt;.warn / .bad - where the figure turns to `state.warning` and `state.negative`.
///                                    Below `warn` bad is "low is bad"; above it, "high is bad".
///   hud.stat_state.&lt;STAT&gt;.per_gdp / .per_population - 1 compares the figure as a share of the
///                                    country's GDP or people, so one threshold fits Tuvalu and China.
/// They are metrics rather than a `campaign.*` list because metrics are already layered per key by
/// every mod, era overlay and reload, which is what a threshold needs.
///
/// No engine type is used here, so the same rules can be read without Godot.
/// </summary>
internal static class HudResourcePlate
{
    internal const string PrimaryPrefix = "hud.top_stats.primary.";
    internal const string StatePrefix = "hud.stat_state.";

    internal enum State { None, Ok, Warning, Bad }

    /// <summary>
    /// True on a narrow screen (a phone): the plate then keeps `hud.resource_slots_narrow` figures and
    /// the rest wait behind "…". On a desktop every figure stands on the plate - the owner's friend
    /// called the drawer fine for Android and poor everywhere else. Set by the interface each layout.
    /// </summary>
    internal static bool Narrow;

    /// <summary>How many figures the plate carries of its own, before borrowed and troubled ones.</summary>
    internal static int Slots => (int)Math.Clamp(Finite(InterfaceSkin.Metric(Narrow ? "hud.resource_slots_narrow" : "hud.resource_slots", 5f), 5f), 1f, 12f);

    /// <summary>The plate's own figures, in the order the data gives them.</summary>
    internal static List<string> Primary(IReadOnlyList<string> topStats)
    {
        var ranked = new List<(float Place, int Index, string Stat)>();
        for (var index = 0; index < topStats.Count; index++)
        {
            var place = Finite(InterfaceSkin.Metric(PrimaryPrefix + topStats[index], 0f), 0f);
            if (place > 0f) ranked.Add((place, index, topStats[index]));
        }
        return ranked.OrderBy(entry => entry.Place).ThenBy(entry => entry.Index)
            .Take(Slots).Select(entry => entry.Stat).ToList();
    }

    /// <summary>
    /// Which figures stand on the plate and which wait behind "…". The plate's own figures first;
    /// then any figure an observer or a movement has borrowed to say something else, because a
    /// figure that has been given a new meaning is one the player must see; then any figure in
    /// trouble, because a problem is seen without a click. Everything else, in the order of
    /// `campaign.top_stats`, behind "…". Nothing is lost: plate and drawer together are every figure.
    /// </summary>
    internal static (List<string> Plate, List<string> Drawer) Arrange(IReadOnlyList<string> topStats,
        IEnumerable<string> borrowed, IEnumerable<string> troubled)
    {
        var plate = Primary(topStats);
        var extra = new HashSet<string>(borrowed ?? Array.Empty<string>(), StringComparer.Ordinal);
        extra.UnionWith(troubled ?? Array.Empty<string>());
        foreach (var stat in topStats)
            if (extra.Contains(stat) && !plate.Contains(stat)) plate.Add(stat);
        var drawer = topStats.Where(stat => !plate.Contains(stat)).ToList();
        return (plate, drawer);
    }

    /// <summary>
    /// Where a figure stands against its thresholds. <see cref="State.None"/> when the data gives
    /// it none (GDP, population, experience, political capital: a size is not a state) or when the
    /// basis it is measured against is missing.
    /// </summary>
    internal static State StateOf(string stat, double value, double gdp, double population)
    {
        var prefix = StatePrefix + stat;
        var warn = InterfaceSkin.Metric(prefix + ".warn", float.NaN);
        var bad = InterfaceSkin.Metric(prefix + ".bad", float.NaN);
        if (!float.IsFinite(warn) || !float.IsFinite(bad) || !double.IsFinite(value)) return State.None;
        if (InterfaceSkin.Metric(prefix + ".per_gdp", 0f) > 0f)
        {
            if (!(gdp > 0d) || !double.IsFinite(gdp)) return State.None;
            value /= gdp;
        }
        else if (InterfaceSkin.Metric(prefix + ".per_population", 0f) > 0f)
        {
            if (!(population > 0d) || !double.IsFinite(population)) return State.None;
            value /= population;
        }
        // A threshold is the first value of the worse state: reaching it is enough. Compared in
        // single precision, the precision of both the figure and the threshold: 0.35 in a file and
        // a stability of 0.35 are then the same number, not a hair apart.
        var at = (float)value;
        if (bad <= warn)
            return at <= bad ? State.Bad : at <= warn ? State.Warning : State.Ok;
        return at >= bad ? State.Bad : at >= warn ? State.Warning : State.Ok;
    }

    /// <summary>The colour token a state is drawn in; empty for a figure with no state.</summary>
    internal static string Token(State state) => state switch
    {
        State.Bad => "state.negative",
        State.Warning => "state.warning",
        State.Ok => "state.positive",
        _ => ""
    };

    /// <summary>The worse of two states, for the "…" that speaks for the figures behind it.</summary>
    internal static State Worse(State a, State b) => (State)Math.Max((int)a, (int)b);

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}
