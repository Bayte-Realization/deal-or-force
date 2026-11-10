using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The army bar: every army of the player's as a card along the bottom of the map.
///
/// The army - a headquarters, its commander and its divisions - is what a player commands; a box
/// drawn round counters is not. Each card says which army it is, who commands it, how many
/// divisions it has, how fit they are and what it is doing (holding a front, attacking, in battle,
/// marching, at sea, idle), so the state of the whole war reads at a glance without opening
/// anything. A click takes the army into the orders panel, a double click flies the camera to it,
/// Ctrl+click moves the selected divisions into it, and the last card raises a new army from the
/// selection. Divisions in no army are the Reserve.
///
/// Everything the eye sees is data: the bar's place is `hud.army_bar` (game/gui layout), its
/// colours the `army_bar.*` tokens, its sizes the `army_bar.*` metrics - a mod restyles or moves
/// it without code.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _armyBar;
    private HBoxContainer _armyBarCards;
    private int _armyBarFingerprint;
    private double _armyBarClock;
    private float _armyBarBaseLeft = float.NaN;
    private ulong _armyBarLastClick;
    private int _armyBarLastClickGroup = int.MinValue;

    /// <summary>The army id the bar uses for divisions in no army.</summary>
    private const int ReserveArmyId = -1;

    private enum ArmyBarStatus { Idle, Moving, Holding, Attacking, Battle, Sea }

    private void BuildArmyBar()
    {
        _armyBar = new PanelContainer { Name = "ArmyBar", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _armyBar.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        _hudLayer.AddChild(_armyBar);
        Ui.Place(_armyBar, "hud.army_bar");
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        _armyBar.AddChild(scroll);
        _armyBarCards = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
        _armyBarCards.AddThemeConstantOverride("separation", Ui.Gap(Ui.Metric("army_bar.gap", 6)));
        scroll.AddChild(_armyBarCards);
        BuildPlanningBar();
    }

    /// <summary>A few times a second: cheap unless an army, its state or the selection changed.</summary>
    private void MaintainArmyBar(double delta)
    {
        if (_armyBar is null || _campaign?.Player is null) return;
        _armyBarClock += delta;
        if (_armyBarClock < 0.25) return;
        _armyBarClock = 0;
        RefreshArmyBar();
        RefreshPlanningBar();
    }

    private void RefreshArmyBar(bool force = false)
    {
        if (_armyBar is null || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var show = _hudLayer is { Visible: true } && player.IsHuman &&
                   _campaign.Armies.Any(army => army.NationId == player.Id);
        if (_armyBar.Visible != show) _armyBar.Visible = show;
        if (!show) return;

        // Beside the orders panel or an open dossier rather than over it.
        if (float.IsNaN(_armyBarBaseLeft)) _armyBarBaseLeft = _armyBar.OffsetLeft;
        var left = _armyBarBaseLeft;
        var origin = _hudLayer.GetGlobalRect().Position.X;
        foreach (var panel in new Control[] { _armyPanel, _sidePanel })
            // Only what stands on the left half: a panel on the right is not in the bar's way.
            if (IsInstanceValid(panel) && panel.Visible && panel.GetGlobalRect().Position.X - origin < _hudLayer.Size.X * 0.5f)
                left = MathF.Max(left, panel.GetGlobalRect().End.X - origin + Ui.Gap(8));
        if (Math.Abs(_armyBar.OffsetLeft - left) > 0.5f) _armyBar.OffsetLeft = left;

        var groups = ArmyBarGroups();
        var fingerprint = HashCode.Combine(groups.Count, _selectedArmies.Count, _selectedArmies.Count == 0 ? -1 : _selectedArmies[0].Id);
        foreach (var (id, divisions) in groups)
        {
            fingerprint = HashCode.Combine(fingerprint, id, divisions.Count, (int)StatusOf(divisions),
                Mathf.RoundToInt(divisions.Average(a => a.Organisation) * 20), Mathf.RoundToInt(divisions.Average(a => a.Strength) * 20),
                divisions.Count(a => _selectedArmies.Contains(a)));
        }
        if (!force && fingerprint == _armyBarFingerprint) return;
        _armyBarFingerprint = fingerprint;

        foreach (var child in _armyBarCards.GetChildren()) child.QueueFree();
        foreach (var (id, divisions) in groups) _armyBarCards.AddChild(ArmyCard(id, divisions));
        _armyBarCards.AddChild(NewArmyCard());
    }

    /// <summary>The player's armies in id order, then the Reserve - each with its divisions.</summary>
    private List<(int Id, List<Army> Divisions)> ArmyBarGroups()
    {
        var player = _campaign.Player;
        var known = _campaign.ArmyGroups.Where(group => group.NationId == player.Id).Select(group => group.Id).ToHashSet();
        var byGroup = new SortedDictionary<int, List<Army>>();
        foreach (var army in _campaign.Armies)
        {
            if (army.NationId != player.Id) continue;
            var id = known.Contains(army.ArmyGroupId) ? army.ArmyGroupId : ReserveArmyId;
            if (!byGroup.TryGetValue(id, out var list)) byGroup[id] = list = new List<Army>();
            list.Add(army);
        }
        var result = byGroup.Where(pair => pair.Key != ReserveArmyId).Select(pair => (pair.Key, pair.Value)).ToList();
        if (byGroup.TryGetValue(ReserveArmyId, out var reserve)) result.Add((ReserveArmyId, reserve));
        return result;
    }

    private ArmyBarStatus StatusOf(List<Army> divisions)
    {
        if (divisions.Any(a => a.EmbarkedFleetId >= 0 || SeaLift.Of(_campaign, a) is not null)) return ArmyBarStatus.Sea;
        if (divisions.Any(a => a.InBattle)) return ArmyBarStatus.Battle;
        if (divisions.Any(a => a.PlanActive && a.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead)) return ArmyBarStatus.Attacking;
        if (divisions.Any(a => a.FrontLineId >= 0 || a.OrderKind == ArmyOrder.DefendFront)) return ArmyBarStatus.Holding;
        if (divisions.Any(a => a.IsMoving)) return ArmyBarStatus.Moving;
        return ArmyBarStatus.Idle;
    }

    private static string StatusKey(ArmyBarStatus status) => status switch
    {
        ArmyBarStatus.Sea => "sea",
        ArmyBarStatus.Battle => "battle",
        ArmyBarStatus.Attacking => "attacking",
        ArmyBarStatus.Holding => "holding",
        ArmyBarStatus.Moving => "moving",
        _ => "idle"
    };

    private static string StatusCaption(ArmyBarStatus status) => status switch
    {
        ArmyBarStatus.Sea => Loc.Get("At sea"),
        ArmyBarStatus.Battle => Loc.Get("In battle"),
        ArmyBarStatus.Attacking => Loc.Get("Attacking"),
        ArmyBarStatus.Holding => Loc.Get("Holding the front"),
        ArmyBarStatus.Moving => Loc.Get("On the march"),
        _ => Loc.Get("Idle")
    };

    private Control ArmyCard(int id, List<Army> divisions)
    {
        var group = _campaign.ArmyGroups.Find(candidate => candidate.Id == id);
        var status = StatusOf(divisions);
        var selected = divisions.Count > 0 && divisions.All(a => _selectedArmies.Contains(a));
        var accent = Ui.Colour("army_bar.status." + StatusKey(status));

        var card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(Ui.Metric("army_bar.card_width", 172), Ui.Metric("army_bar.card_height", 70)),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour(selected ? "army_bar.card_selected" : "army_bar.card"),
            BorderColor = selected ? Ui.Colour("army_bar.border_selected") : Ui.Colour("army_bar.border"),
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginLeft = 10, ContentMarginRight = 8, ContentMarginTop = 5, ContentMarginBottom = 5
        };
        style.SetBorderWidthAll(1);
        style.BorderWidthLeft = 4;
        // The left edge is the army's state: the bar reads as a row of colours before any word.
        style.BorderColor = selected ? Ui.Colour("army_bar.border_selected") : style.BorderColor;
        card.AddThemeStyleboxOverride("panel", style);
        var accentEdge = new ColorRect { Color = accent, CustomMinimumSize = new Vector2(4, 0), MouseFilter = Control.MouseFilterEnum.Ignore };

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        card.AddChild(row);
        row.AddChild(accentEdge);
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 2);
        row.AddChild(column);

        var name = id == ReserveArmyId ? Loc.Get("Reserve") : group?.Name ?? "?";
        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var title = BarLabel(name, Ui.Metric("army_bar.title_font", 13), Ui.Colour("army_bar.title"));
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(title);
        var count = BarLabel("×" + divisions.Count.ToString("N0"), Ui.Metric("army_bar.title_font", 13), Ui.Colour("army_bar.count"));
        count.AddThemeFontOverride("font", Ui.NumberFont);
        // A number is never cut: the name gives way to it.
        count.ClipText = false;
        count.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        head.AddChild(count);
        column.AddChild(head);

        var line = BarLabel(StatusCaption(status) + (group is null ? "" : " · " + group.Commander),
            Ui.Metric("army_bar.body_font", 11), Ui.Colour("army_bar.body"));
        column.AddChild(line);

        column.AddChild(ArmyBarGauge(divisions.Average(a => a.Organisation), Ui.Colour("combat.organisation")));
        column.AddChild(ArmyBarGauge(divisions.Average(a => a.Strength), Ui.Colour("combat.strength")));

        card.TooltipText = Ui.Tip(name + "\n" + StatusCaption(status) +
                                  (group is null ? "" : "\n" + group.Commander) + "\n" +
                                  Loc.Format("{0:N0} divisions · {1:N0} men", divisions.Count, divisions.Sum(a => (long)(a.Personnel * a.Strength))) + "\n\n" +
                                  Tr("Click: command this army · double-click: go to it · Ctrl+click: move the selected divisions into it"));
        card.GuiInput += input => OnArmyCardInput(input, id, divisions);
        return card;
    }

    /// <summary>One line of a card: never wraps, cut with an ellipsis when the name is long.</summary>
    private static Label BarLabel(string text, float size, Color colour)
    {
        var label = new Label
        {
            Text = text, AutowrapMode = TextServer.AutowrapMode.Off, ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeFontSizeOverride("font_size", Ui.TypeStep((int)size));
        label.AddThemeColorOverride("font_color", colour);
        return label;
    }

    private static Control ArmyBarGauge(float value, Color colour)
    {
        var height = Mathf.Max(2f, Ui.Metric("army_bar.gauge_height", 3));
        var back = new ColorRect { Color = Ui.Colour("combat.bar_background"), CustomMinimumSize = new Vector2(0, height), MouseFilter = Control.MouseFilterEnum.Ignore };
        var fill = new ColorRect { Color = colour, MouseFilter = Control.MouseFilterEnum.Ignore };
        fill.AnchorRight = Mathf.Clamp(value, 0f, 1f);
        fill.AnchorBottom = 1f;
        back.AddChild(fill);
        return back;
    }

    private Control NewArmyCard()
    {
        var hasSelection = SelectedPlayerDivisions().Length > 0;
        var card = ActionButton(Tr("+ New army"), () =>
        {
            if (SelectedPlayerDivisions().Length == 0)
            {
                SetArmyFeedback(Tr("Select divisions first: a new army is raised from the selection."));
                return;
            }
            CreateGroupFromSelection();
            RefreshArmyBar(force: true);
        }, Ui.Metric("army_bar.new_width", 96), Ui.Metric("army_bar.card_height", 70));
        card.Disabled = !hasSelection;
        card.TooltipText = Ui.Tip(Tr("New army\nRaise a new army headquarters from the selected divisions."));
        return card;
    }

    private void OnArmyCardInput(InputEvent input, int id, List<Army> divisions)
    {
        if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
        GetViewport().SetInputAsHandled();
        var live = divisions.Where(army => _campaign.Armies.Contains(army)).ToList();
        if (click.CtrlPressed)
        {
            // Ctrl+click: the selection joins this army (the Reserve: leaves its army).
            if (SelectedPlayerDivisions().Length == 0) return;
            AssignSelectionToGroup(id == ReserveArmyId ? -1 : id);
            RefreshArmyBar(force: true);
            return;
        }
        var now = Time.GetTicksMsec();
        var twice = _armyBarLastClickGroup == id && now - _armyBarLastClick < 450;
        _armyBarLastClick = now;
        _armyBarLastClickGroup = id;
        if (live.Count == 0) return;
        OnArmiesBoxPicked(live);
        if (twice)
        {
            // Double click: the camera goes to the army - the middle of its divisions.
            var on = live.Where(army => army.ProvinceId > 0 || army.EmbarkedFleetId >= 0).ToList();
            if (on.Count > 0) _globe.FocusOn(on.Average(a => a.Latitude), on.Average(a => a.Longitude), 6.5f);
        }
        RefreshArmyBar(force: true);
    }
}
