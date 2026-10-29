using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// One list of everywhere, opened with F1.
///
/// The campaign has more than twenty places a decision can be made in - the map, the situation
/// desk, diplomacy, war, laws, research, doctrines, the ledger, the archive - and they are
/// reached from buttons that live on a top bar, a right-hand dossier, three tabs and a context
/// menu. That is fine while a player knows the game and hostile while they do not: the answer to
/// "where do I change the budget" is a hunt, and the answer to "what is in this game at all" is
/// nothing at all.
///
/// This is the index. One keystroke, a searchable list of every destination, the group it
/// belongs to and the sentence that says what it is for. It is also the honest answer to
/// reachability: a screen reachable from here does not depend on a button being visible, a panel
/// being unfolded, or a tab existing in the build that happens to be running.
///
/// Deliberately not a debug tool. It opens the same methods the buttons open, so it cannot do
/// anything a player could not; it can only find them faster.
/// </summary>
public partial class DealOrForceGame
{
    private Control _navigatorLayer;
    private LineEdit _navigatorSearch;
    private VBoxContainer _navigatorRows;
    private Label _navigatorCount;
    private readonly List<Button> _navigatorButtons = new();
    private int _navigatorIndex;

    /// <summary>What the index is made of: a group, a name, a purpose, and the door it opens.</summary>
    private sealed record NavigatorEntry(string Group, string Name, string Purpose, Action Open);

    /// <summary>
    /// Every destination, in the order a campaign is usually run through them.
    ///
    /// The list is written out rather than discovered because a discovered list would be a list
    /// of whatever happened to be visible: the whole point is that this one is complete.
    /// </summary>
    private List<NavigatorEntry> NavigatorEntries() => new()
    {
        new("Campaign", "Situation", "The week read from five chairs: what is urgent, what can wait.", () => OpenScreen("Situation")),
        new("Campaign", "Ledger", "A record of any country in the world, read-only.", OpenLedger),
        new("Campaign", "Wars", "Every war the world is in, its score and its cost.", OpenWarLedger),
        new("Campaign", "Conflicts", "Standoffs, proxy wars, disputes and organisations.", OpenConflictsScreen),

        new("Government", "State affairs", "Budget, taxes, ministries and what needs a signature.", () => OpenScreen("State Affairs")),
        new("Government", "Laws", "What is on the statute book, and what could be.", () => OpenScreen("Laws")),
        new("Government", "Decisions", "Everything waiting on a decision this week.", () => OpenScreen("Decisions")),

        new("Diplomacy", "Diplomacy", "Seventeen acts, from a state visit to a fleet transfer.", OpenDiplomacyScreen),
        new("Diplomacy", "Trade", "What the country buys, sells and cannot make itself.", OpenTradeScreen),

        new("Military", "Army", "Divisions, their orders, their posture and their supply.", () => OpenScreen("Military")),
        new("Military", "Production", "Two pools, and the conversion between them.", OpenProductionScreen),
        new("Military", "Long-range forces", "Guns, aircraft and drones that reach from bases.", () => OpenScreen("Support")),
        new("Military", "Recruitment", "Raise, mobilise, commission.", () => OpenRecruitment()),

        new("Science", "Research", "The tree, its cost, and what waits behind each node.", OpenResearchScreen),
        new("Science", "Doctrines", "Four shelves of books that change how armies fight.", OpenDoctrineScreen),

        new("Records", "Economy", "Output, prices and the weekly account.", () => OpenScreen("Economy")),
        new("Records", "Nation dossier", "The country under the cursor, in full.", () => OpenScreen("Nation")),

        new("System", "Settings", "Scale, quality, audio, and where the files live.", ShowSettings),
        new("System", "Save and load", "The campaign archive: as many saves as you care to keep.", ShowSaveLoadInterface),
        new("System", "Handbook", "How the game works, in its own words.", () => OpenHandbook())
    };

    private void BuildNavigator()
    {
        _navigatorLayer = FullLayer("Navigator");
        _navigatorLayer.Visible = false;
        _navigatorLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_navigatorLayer);

        var veil = new ColorRect
        {
            Color = Ui.Colour("surface.overlay", 0.82f),
            MouseFilter = MouseFilterEnum.Stop
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _navigatorLayer.AddChild(veil);

        var panel = TexturedPanel(_navigatorLayer, PanelSurface.Modal);
        panel.Name = "NavigatorPanel";
        Ui.Place(panel, "modal.navigator");
        var rows = PanelRows(panel, 22, 16, 10);

        var close = ActionButton("×", CloseNavigator, 36, 32);
        close.Name = "NavigatorClose";
        close.TooltipText = Ui.Tip(Tr("Close the index (Esc)."));
        ScreenHeader(rows, "Where to", "Every screen in the campaign. Type to narrow it down.",
            false, close);

        var searchRow = new HBoxContainer();
        searchRow.AddThemeConstantOverride("separation", Ui.Gap(8));
        rows.AddChild(searchRow);
        _navigatorSearch = new LineEdit
        {
            Name = "NavigatorSearch",
            PlaceholderText = Tr("Search a screen, a tab or an action"),
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 36)
        };
        _navigatorSearch.TextChanged += _ => FillNavigator();
        _navigatorSearch.TextSubmitted += _ => ActivateNavigator();
        searchRow.AddChild(_navigatorSearch);
        _navigatorCount = Title("", (int)Ui.Metric("ui.font.caption", 11), Ui.Colour("text.faint"));
        _navigatorCount.Name = "NavigatorCount";
        _navigatorCount.AddThemeFontOverride("font", Ui.NumberFont);
        searchRow.AddChild(_navigatorCount);

        var scroll = new ScrollContainer
        {
            Name = "NavigatorScroll",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true
        };
        rows.AddChild(scroll);
        _navigatorRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _navigatorRows.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_navigatorRows);

        var hint = Title("↑ ↓ choose · Enter open · Esc close", (int)Ui.Metric("ui.font.caption", 11),
            Ui.Colour("text.faint"));
        hint.Name = "NavigatorHint";
        rows.AddChild(hint);
    }

    private void ToggleNavigator()
    {
        if (_navigatorLayer is null) BuildNavigator();
        if (_navigatorLayer!.Visible) { CloseNavigator(); return; }
        OpenNavigator();
    }

    private void OpenNavigator()
    {
        if (_navigatorLayer is null) BuildNavigator();
        _navigatorSearch!.Text = "";
        FillNavigator();
        _navigatorLayer!.Visible = true;
        _navigatorLayer.MoveToFront();
        _navigatorSearch.GrabFocus();
    }

    private void CloseNavigator()
    {
        if (_navigatorLayer is null) return;
        _navigatorLayer.Visible = false;
    }

    /// <summary>
    /// Rebuild the list for the current search. A few letters narrow twenty entries to one, and
    /// the count on the right says how many are left: a list that silently hides its own
    /// contents is a list that hides the thing being looked for.
    /// </summary>
    private void FillNavigator()
    {
        foreach (var child in _navigatorRows.GetChildren()) child.QueueFree();
        _navigatorButtons.Clear();
        _navigatorIndex = 0;

        var needle = (_navigatorSearch?.Text ?? "").Trim();
        var matches = NavigatorEntries()
            .Where(entry => needle.Length == 0 ||
                            entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                            entry.Group.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                            entry.Purpose.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _navigatorCount!.Text = matches.Count.ToString();

        var group = "";
        foreach (var entry in matches)
        {
            if (entry.Group != group)
            {
                group = entry.Group;
                SectionRule(_navigatorRows, group);
            }
            var captured = entry;
            var (row, _, _, action) = ActionRow("Nav_" + entry.Name.Replace(' ', '_'), entry.Name,
                entry.Purpose, () => Choose(captured), Tr("Open"));
            _navigatorRows.AddChild(row);
            _navigatorButtons.Add(action);
        }
        if (_navigatorButtons.Count > 0) HighlightNavigator(0);
    }

    /// <summary>Which row the keyboard is on. A dimmer modulate, because the arrow keys are not
    /// focus and a navigator that steals the focus ring from the search field would stop the
    /// player from typing.</summary>
    private void HighlightNavigator(int index)
    {
        if (_navigatorButtons.Count == 0) return;
        _navigatorIndex = Math.Clamp(index, 0, _navigatorButtons.Count - 1);
        for (var i = 0; i < _navigatorButtons.Count; i++)
            _navigatorButtons[i].Modulate = i == _navigatorIndex ? Colors.White : new Color(.78f, .78f, .78f);
    }

    private void MoveNavigator(int step)
    {
        if (_navigatorButtons.Count == 0) return;
        HighlightNavigator(_navigatorIndex + step);
    }

    private void ActivateNavigator()
    {
        if (_navigatorButtons.Count == 0) return;
        _navigatorButtons[Math.Clamp(_navigatorIndex, 0, _navigatorButtons.Count - 1)]
            .EmitSignal(Button.SignalName.Pressed);
    }

    /// <summary>Open the thing the row stands for, and get out of the way.</summary>
    private void Choose(NavigatorEntry entry)
    {
        CloseNavigator();
        try { entry.Open(); }
        catch (Exception exception)
        {
            DiagnosticLogs.Write("errors", $"NAVIGATOR_OPEN_FAILED {entry.Name} {exception.Message}");
        }
    }
}
