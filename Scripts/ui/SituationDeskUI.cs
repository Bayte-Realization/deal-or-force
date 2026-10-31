using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The situation desk: the country read from one chair at a time, docked where the player
/// wants it.
///
/// It is deliberately not the alert rail. The rail answers "what is on fire"; the desk answers
/// "what does this week look like from where I am sitting", and lets the chair be changed. A
/// soldier, a treasurer, a minister of the interior and a diplomat all look at the same world
/// and should not be shown the same five lines about it.
///
/// The dock side, the lens and whether it is folded are the player's, and they are kept in the
/// campaign's own settings so a reload opens the desk the way it was left.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _deskPanel, _deskTab;
    private VBoxContainer _deskRows;
    private Label _deskHeading, _deskEmpty, _deskPurpose;
    private readonly Dictionary<DeskLens, Button> _deskLenses = new();
    private DeskLens _deskLens = DeskLens.Security;
    private bool _deskOnLeft;
    private bool _deskOpen = true;
    private bool _deskRefreshing;
    private int _deskDay = -1;
    private int _deskNation = -1;
    private string _deskSignature = "";

    private static string DeskLensName(DeskLens lens) => lens switch
    {
        DeskLens.Security => "Security",
        DeskLens.Economy => "Economy",
        DeskLens.Society => "Society",
        DeskLens.Diplomacy => "Diplomacy",
        _ => "Cabinet"
    };

    /// <summary>What the lens watches, in the words a player needs once.</summary>
    /// <summary>A purpose line shown as a sentence: capital first, full stop last, in any language.</summary>
    private static string Sentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        text = text.Trim();
        text = char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];
        return text.EndsWith('.') ? text : text + ".";
    }

    private static string DeskLensPurpose(DeskLens lens) => lens switch
    {
        DeskLens.Security => "the front, the pockets, and whether anything stands in the way",
        DeskLens.Economy => "the balance, the debt, and the price of money",
        DeskLens.Society => "the street, the mood, and how the country carries the war",
        DeskLens.Diplomacy => "the papers on the table and the friends who are counting",
        _ => "what is ready to be decided, and with what credit"
    };

    private static string DeskSeverityColour(DeskSeverity severity) => severity switch
    {
        DeskSeverity.Must => "state.negative",
        DeskSeverity.Should => "state.warning",
        DeskSeverity.Could => "state.info",
        _ => "surface.line_faint"
    };

    private static string DeskSeverityName(DeskSeverity severity) => severity switch
    {
        DeskSeverity.Must => "Now",
        DeskSeverity.Should => "This week",
        DeskSeverity.Could => "When convenient",
        _ => "Watching"
    };

    private void BuildSituationDesk()
    {
        _deskPanel = TexturedPanel(_hudLayer, PanelSurface.Command);
        _deskPanel.Name = "SituationDesk";
        _deskPanel.GrowHorizontal = GrowDirection.Begin;
        _deskPanel.Visible = false;
        _deskPanel.VisibilityChanged += SyncDeskDock;
        Ui.Place(_deskPanel, _deskOnLeft ? "hud.desk_left" : "hud.desk_right");
        ShiftHudDock(_deskPanel);
        var body = PanelRows(_deskPanel, 10, 8, 6);

        var head = new HBoxContainer { Name = "DeskHead" };
        head.AddThemeConstantOverride("separation", 6);
        body.AddChild(head);
        _deskHeading = Title("SITUATION DESK", (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.accent"), true);
        _deskHeading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_deskHeading);
        var side = ActionButton("⇄", () => SetDeskSide(!_deskOnLeft), 28, 26);
        side.Name = "DeskSide";
        side.TooltipText = Ui.Tip(Tr("Dock the desk on the other side."));
        head.AddChild(side);
        var fold = ActionButton("–", () => SetDeskOpen(false), 28, 26);
        fold.Name = "DeskFold";
        fold.TooltipText = Ui.Tip(Tr("Fold the desk away."));
        head.AddChild(fold);

        // Five forced equal columns silently cut translated captions and counts, and a free flow
        // left the fifth lens alone on a second line, which read as a mistake. Three columns:
        // two even rows, every caption whole in any language.
        var lenses = new GridContainer { Name = "DeskLenses", Columns = 3 };
        lenses.AddThemeConstantOverride("h_separation", 4);
        lenses.AddThemeConstantOverride("v_separation", 4);
        body.AddChild(lenses);
        foreach (var lens in Enum.GetValues<DeskLens>())
        {
            var captured = lens;
            var chip = ActionButton(DeskLensName(lens), () => SetDeskLens(captured), 0, 24);
            chip.Name = "DeskLens" + lens;
            chip.SetMeta("atlas_tab_button", true);
            chip.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            chip.ClipText = false;
            chip.TooltipText = Ui.Tip(Tr(DeskLensName(lens)) + ": " + Tr(DeskLensPurpose(lens)));
            chip.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.micro", 10));
            StyleButton(chip, lens == _deskLens);
            lenses.AddChild(chip);
            _deskLenses[lens] = chip;
        }

        _deskPurpose = Title(DeskLensPurpose(_deskLens), (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.muted"));
        _deskPurpose.Text = Sentence(Tr(DeskLensPurpose(_deskLens)));
        _deskPurpose.Name = "DeskPurpose";
        _deskPurpose.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_deskPurpose);

        var scroll = new ScrollContainer
        {
            Name = "DeskScroll",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true
        };
        body.AddChild(scroll);
        _deskRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _deskRows.AddThemeConstantOverride("separation", Ui.Gap(6));
        scroll.AddChild(_deskRows);
        // The rows learn their height only once they know their width - a wrapped line measured at
        // no width is a word to a line. Fit the desk again whenever that settles, or it keeps the
        // height of the first guess (655 pixels for two rows) until the next day.
        _deskRows.MinimumSizeChanged += () => Callable.From(FitSituationDesk).CallDeferred();
        _deskEmpty = Title("", (int)Ui.Metric("ui.font.body", 12), Ui.Colour("text.body_soft"));
        _deskEmpty.Name = "DeskEmpty";
        _deskEmpty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_deskEmpty);

        // The tab that brings it back: the desk can be folded away, not lost.
        _deskTab = new PanelContainer { Name = "SituationDeskTab", Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _deskTab.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        if (_outlinerHeader is not null) _outlinerHeader.AddChild(_deskTab);
        else _hudLayer.AddChild(_deskTab);
        var tab = ActionButton("DESK", () => SetDeskOpen(true), 0, 30);
        tab.Name = "DeskTabButton";
        tab.TooltipText = Ui.Tip(Tr("Open the situation desk."));
        _deskTab.AddChild(tab);
        if (_deskTab.GetParent() is not Container)
            Ui.Place(_deskTab, _deskOnLeft ? "hud.desk_tab_left" : "hud.desk_tab_right");
    }

    /// <summary>
    /// Whether another dock stands where the desk would. It steps aside for whatever was opened
    /// over its ground, on either side: docked right it used to sit on top of the army, fleet and
    /// dossier panels that open there. Measured, not assumed, because the skin moves the docks.
    /// </summary>
    private bool DeskCovered()
    {
        if (_deskPanel is null) return false;
        var area = _deskPanel.GetGlobalRect();
        foreach (var dock in new Control[] { _sidePanel, _armyPanel, _fleetPanel, _movementPanel, _resourceAtlasPanel })
        {
            if (dock is null || !GodotObject.IsInstanceValid(dock)) continue;
            // Every dock that can cover the desk brings it back the moment it closes.
            if (!dock.HasMeta("desk_watch"))
            {
                dock.SetMeta("desk_watch", true);
                dock.VisibilityChanged += () => Callable.From(RefreshSituationDesk).CallDeferred();
            }
            if (dock.IsVisibleInTree() && dock.GetGlobalRect().Intersects(area)) return true;
        }
        return false;
    }

    /// <summary>
    /// The right-hand report and the desk share one dock, and the button that opens the desk
    /// lives in the report's header. Both followed the desk only when the desk refreshed itself,
    /// so when anything else hid the desk - a closing window, a layout pass - on a paused day
    /// the report, the desk and the button to bring it back were all gone at once. They follow
    /// the desk's own visibility now, and a desk hidden from outside counts as closed.
    /// </summary>
    private void SyncDeskDock()
    {
        if (_deskPanel is null || !GodotObject.IsInstanceValid(_deskPanel)) return;
        if (!_deskPanel.Visible && _deskOpen && !_deskRefreshing && !_observerMode && !DeskCovered())
        {
            _deskOpen = false;
            if (_campaign is not null) _campaign.Settings["desk.open"] = "0";
        }
        if (_outliner is not null) _outliner.Visible = !_observerMode && (!_deskPanel.Visible || _deskOnLeft);
        if (_deskTab is not null) _deskTab.Visible = !_deskOpen && !_observerMode;
    }

    private void SetDeskSide(bool left)
    {
        _deskOnLeft = left;
        if (_campaign is not null) _campaign.Settings["desk.side"] = left ? "left" : "right";
        if (_deskPanel is not null) { Ui.Place(_deskPanel, left ? "hud.desk_left" : "hud.desk_right"); ShiftHudDock(_deskPanel); }
        if (_deskTab is not null && _deskTab.GetParent() is not Container)
            Ui.Place(_deskTab, left ? "hud.desk_tab_left" : "hud.desk_tab_right");
        _deskDay = -1;
        RefreshSituationDesk();
    }

    private void SetDeskLens(DeskLens lens)
    {
        _deskLens = lens;
        if (_campaign is not null) _campaign.Settings["desk.lens"] = lens.ToString();
        _deskDay = -1;
        RefreshSituationDesk();
    }

    private void SetDeskOpen(bool open)
    {
        _deskOpen = open;
        if (_campaign is not null) _campaign.Settings["desk.open"] = open ? "1" : "0";
        if (_deskTab is not null) _deskTab.Visible = !open;
        _deskDay = -1;
        RefreshSituationDesk();
    }

    /// <summary>
    /// Bring the desk up to date. Rebuilt when the day, the chair or the country changes, and
    /// otherwise only repainted, because a list that rebuilds under the pointer is a list that
    /// cannot be read.
    /// </summary>
    private void RefreshSituationDesk()
    {
        var player = _campaign?.Player;
        if (player is null) return;
        if (_deskPanel is null || !GodotObject.IsInstanceValid(_deskPanel)) BuildSituationDesk();
        if (_deskPanel is null) return;

        if (_campaign.Settings.TryGetValue("desk.side", out var side)) _deskOnLeft = side == "left";
        if (_campaign.Settings.TryGetValue("desk.lens", out var lens) &&
            Enum.TryParse<DeskLens>(lens, true, out var parsed) && Enum.IsDefined(parsed)) _deskLens = parsed;
        if (_campaign.Settings.TryGetValue("desk.open", out var open)) _deskOpen = open != "0";

        _deskRefreshing = true;
        _deskPanel.Visible = _deskOpen && !DeskCovered() && !_observerMode;
        _deskRefreshing = false;
        _deskTab.Visible = !_deskOpen && !_observerMode;
        // One right-hand dock, not two independent floating panels. The folded command is
        // part of the report's real header layout; the open desk replaces the report.
        if (_outliner is not null) _outliner.Visible = !_deskPanel.Visible || _deskOnLeft;
        var dock = _deskOnLeft ? "hud.desk_left" : "hud.desk_right";
        if (!_deskPanel.HasMeta("skin_layout") || _deskPanel.GetMeta("skin_layout").AsString() != dock)
        { Ui.Place(_deskPanel, dock); ShiftHudDock(_deskPanel); }
        FitSituationDesk();
        if (_outliner is not null) _outliner.Visible = !_deskPanel.Visible || _deskOnLeft;
        if (!_deskOpen) return;

        var signature = player.Id + "/" + _campaign.ElapsedDays + "/" + _deskLens + "/" + (_deskOnLeft ? "L" : "R") + "/" + Loc.Language;
        if (signature != _deskSignature || _deskNation != player.Id)
        {
            _deskSignature = signature;
            _deskNation = player.Id;
            RebuildDeskRows(player);
        }
        _deskDay = _campaign.ElapsedDays;

        var urgent = SituationDesk.Count(_campaign, player, _deskLens);
        _deskHeading.Text = Tr("SITUATION DESK") + (urgent > 0 ? "  ·  " + urgent : "");
        _deskPurpose.Text = Sentence(Tr(DeskLensPurpose(_deskLens)));

        foreach (var (lensId, chip) in _deskLenses)
        {
            if (!GodotObject.IsInstanceValid(chip)) continue;
            var count = SituationDesk.Count(_campaign, player, lensId);
            chip.Text = Tr(DeskLensName(lensId)) + (count > 0 ? " " + count : "");
            chip.TooltipText = Ui.Tip(Tr(DeskLensName(lensId)) + ": " + Tr(DeskLensPurpose(lensId)));
            StyleButton(chip, lensId == _deskLens);
        }
    }

    /// <summary>The open desk shares the outliner's free map-side space, not the selection card.</summary>
    private void FitSituationDesk()
    {
        if (_deskPanel is null) return;
        // Reset from the authored dock so closing a card restores the full height. Never
        // subtract from yesterday's clamped rectangle (which would progressively shrink it).
        ShiftHudDock(_deskPanel);
        ClampAboveMapBar(_deskPanel);
        if (_regionCard is not { Visible: true }) return;
        var desk = _deskPanel.GetGlobalRect();
        var card = _regionCard.GetGlobalRect();
        if (desk.End.X <= card.Position.X || desk.Position.X >= card.End.X) return;
        var bottom = Mathf.Min(desk.End.Y, card.Position.Y - Ui.Metric("hud.dock_gap", 8));
        if (bottom - desk.Position.Y < _deskPanel.GetCombinedMinimumSize().Y)
        {
            // Extreme modded layouts may leave no usable room. Keep the saved open state,
            // temporarily yield the dock, and restore it once the card closes.
            _deskPanel.Visible = false;
            return;
        }
        _deskPanel.AnchorBottom = 0;
        _deskPanel.OffsetBottom = bottom - _deskPanel.GetParent<Control>().GlobalPosition.Y;
    }

    /// <summary>
    /// A dock never comes down onto the map bar. The desk's authored rectangle ran to the foot
    /// of the screen and sat on top of FOG, EVENTS and TRADE; it now stops a gap above the bar
    /// wherever the two share a column.
    /// </summary>
    private void ClampAboveMapBar(Control dock)
    {
        if (dock is null || _mapControlsPanel is not { Visible: true } bar || dock.GetParent() is not Control parent) return;
        var area = dock.GetGlobalRect();
        var strip = bar.GetGlobalRect();
        if (area.End.X <= strip.Position.X || area.Position.X >= strip.End.X || area.End.Y <= strip.Position.Y) return;
        dock.AnchorBottom = 0;
        dock.OffsetBottom = strip.Position.Y - Ui.Metric("hud.dock_gap", 8) - parent.GlobalPosition.Y;
    }

    private void RebuildDeskRows(Nation player)
    {
        foreach (var child in _deskRows.GetChildren()) child.QueueFree();
        var items = SituationDesk.Read(_campaign, player, _deskLens);
        foreach (var item in items) AddDeskRow(item);
        _deskEmpty.Text = items.Count == 0 ? Tr("Nothing on this desk this week.") : "";
    }

    /// <summary>One line: its urgency as an edge, what it is, the number, and where to go.</summary>
    private void AddDeskRow(DeskItem item)
    {
        var row = new HBoxContainer { Name = "Desk_" + item.Id, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", Ui.Gap(6));
        var edge = new Panel { CustomMinimumSize = new Vector2(3, 0), MouseFilter = MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat { BgColor = Ui.Colour(DeskSeverityColour(item.Severity)) };
        style.SetCornerRadiusAll(1);
        edge.AddThemeStyleboxOverride("panel", style);
        row.AddChild(edge);

        var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        words.AddThemeConstantOverride("separation", 0);
        row.AddChild(words);
        var title = Title(item.Title, (int)Ui.Metric("ui.font.body", 12), Ui.Colour("text.body"));
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        words.AddChild(title);
        var meta = (item.Detail ?? "") + (string.IsNullOrEmpty(item.Trend) ? "" : " · " + item.Trend);
        if (!string.IsNullOrEmpty(meta))
        {
            var line = Title(meta, (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"));
            line.AddThemeFontOverride("font", Ui.NumberFont);
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            words.AddChild(line);
        }
        words.AddChild(Title(DeskSeverityName(item.Severity), (int)Ui.Metric("ui.font.micro", 10),
            Ui.Colour(DeskSeverityColour(item.Severity)), true));

        var go = ActionButton("→", () =>
        {
            // A world line points at a country, so it opens that country rather than the list
            // it is listed in: the reader asked "who", and the answer is a dossier.
            if (item.HasNation && _campaign?.NationById(item.NationId) is { } target)
            {
                SelectDiplomacyNation(target);
                OpenScreen("Diplomacy");
                return;
            }
            OpenScreen(item.Screen);
        }, 30, 26);
        go.Name = "DeskGo_" + item.Id;
        go.TooltipText = Ui.Tip(item.HasNation && _campaign?.NationById(item.NationId) is { } named
            ? named.DisplayName
            : Tr(item.Screen));
        row.AddChild(go);
        _deskRows.AddChild(row);
    }
}
