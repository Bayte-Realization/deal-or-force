using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The conflicts screen's "The world" tab: how close the great powers are to one another's
/// throats (WorldWar.Tension), the rivalries that make it, any world war being fought, and what
/// has lately happened between the blocs - incidents, pacts signed and broken, members walking
/// out, spies caught.
/// </summary>
public partial class DealOrForceGame
{
    private static readonly HashSet<string> WorldOrderKinds = new()
    {
        "world_incident", "world_war_began", "organization_founded", "organization_left", "organization_split",
        "organization_expelled", "organization_dissolved", "spy_network_exposed", "alliance_ended",
        "nuclear_strike", "nuclear_retaliation", "nuclear_devastation", "nuclear_test", "nuclear_incident", "red_telephone"
    };

    /// <summary>The great powers' rivalries, the worst first: each opens the standoff on the right.</summary>
    private void ListWorldOrder()
    {
        var entries = new List<ConflictEntry>();
        foreach (var (a, b, _) in WorldWar.Pairs(_campaign).Take(24))
            if (CampaignDiplomacy.Peek(_campaign, a, b) is { } relation && ConflictEntryFor(relation) is { } entry)
                entries.Add(entry);
        RenderConflictEntries(entries, "The great powers are at peace with one another.");
    }

    private void ShowWorldOrderPage()
    {
        var tension = WorldWar.Tension(_campaign);
        var (word, colour) = tension switch
        {
            >= 80f => (Tr("on the brink"), Ui.Colour("state.critical")),
            >= 60f => (Tr("dangerous"), Ui.Colour("state.warning")),
            >= 40f => (Tr("tense"), Ui.Colour("text.accent")),
            _ => (Tr("calm"), Ui.Colour("text.body_soft"))
        };
        _conflictsPage.AddChild(Title("THE WORLD", 22, Ui.Colour("text.bright"), upper: true));
        var gauge = new VBoxContainer();
        gauge.AddThemeConstantOverride("separation", 5);
        gauge.AddChild(Title(Simulation.Loc.Format("{0:0} / 100 · {1}", tension, word), 15, colour));
        var meter = Gauge(tension, colour, 0);
        meter.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        gauge.AddChild(meter);
        _conflictsPage.AddChild(gauge);
        _conflictsPage.AddChild(Title("How close the great powers are to war with one another. The worst rivalry counts in full, the next two in part. When it runs high, incidents between the blocs become likelier.",
            13, Ui.Colour("text.body_soft")));

        _conflictsPage.AddChild(Caption("WORLD WARS"));
        var worldWars = _campaign.Wars.Where(w => !w.Ended && w.WorldWarDay >= 0).ToList();
        if (worldWars.Count == 0)
            _conflictsPage.AddChild(Title("No world war is being fought.", 13, Ui.Colour("text.caption")));
        foreach (var war in worldWars)
        {
            var line = ActionButton(Simulation.Loc.Format("{0} · {1} countries · since {2} days", WarNames.Of(_campaign, war), war.BelligerentCount,
                _campaign.ElapsedDays - war.WorldWarDay), () => { _conflictWar = war.Id; _conflictTab = 1; RefreshConflictsScreen(force: true); }, 0, 32);
            _conflictsPage.AddChild(line);
        }

        _conflictsPage.AddChild(Caption("BETWEEN THE BLOCS, LATELY"));
        var recent = _campaign.Chronicle.Entries.Where(e => e.Public && WorldOrderKinds.Contains(e.Kind))
            .OrderByDescending(e => e.Sequence).Take(10).Select(e => e.Sequence).ToHashSet();
        var lines = NewsFeed.Lines(_campaign).Where(l => recent.Contains(l.Sequence)).OrderByDescending(l => l.Sequence).ToList();
        if (lines.Count == 0) _conflictsPage.AddChild(Title("Nothing has happened between the blocs yet.", 13, Ui.Colour("text.caption")));
        foreach (var line in lines)
            _conflictsPage.AddChild(Title(Simulation.Loc.Format("{0} days ago · {1}", _campaign.ElapsedDays - line.Day, line.Text), 13, Ui.Colour("text.body")));
    }
}
