using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The top left of the campaign (docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md, step 2): the flag on
/// its mount, and one plate of resources - the five figures that matter most, each with the week's
/// change and a half-year thread in the colour of its state, and "…" at the plate's edge for the
/// other seven. A figure in trouble is red on the plate itself, and one in trouble behind "…" comes
/// up onto the plate by itself: a problem is seen without hovering or clicking.
///
/// The rules (which figures, which thresholds) are HudResourcePlate's and live in data,
/// game/gui/79_hud_resources.json; this file only builds and arranges the controls.
/// </summary>
public partial class DealOrForceGame
{
    private readonly Trend _stabilityTrend = new();
    private readonly Trend _manpowerTrend = new();
    // Whose figures the trends and threads hold; another country on the bar starts them afresh.
    private Nation _trendsNation;
    private PanelContainer _hudFlagMount;
    private Button _hudMoreButton;
    private ResourceDrawer _hudResourceDrawer;
    private HBoxContainer _hudDrawerRow;
    private string _hudPlateArrangement = "";
    private bool _hudDrawerByKey;

    /// <summary>
    /// The flag on a plain mount at the top left: the country, said in the one way that needs no
    /// reading, with its world rank under it. The mount's shape is data - the role `flag.plaque` of
    /// the layout key `hud.flag_mount`, a rounded plaque - so an era overlay can put it on a shield
    /// or a disc without code; the base game stays out of heraldry.
    /// </summary>
    private void BuildFlagMount()
    {
        var mount = new PanelContainer { Name = "CampaignFlagMount" };
        _hudFlagMount = mount;
        _hudLayer.AddChild(mount);
        ApplyHudFrame(mount);
        Ui.Place(mount, "hud.flag_mount");
        var margin = Margin(mount, 6, 5);
        var tag = _campaign?.Player?.Tag ?? "";
        _identityFlag = FlagBadge(tag, MountFlagHeight(tag));
        // The flag takes the map home: the capital, framed - what a player reaches for after
        // a long look at the other side of the world (also the Home key).
        _identityFlag.MouseFilter = MouseFilterEnum.Stop;
        _identityFlag.MouseDefaultCursorShape = CursorShape.PointingHand;
        _identityFlag.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _identityFlag.TooltipText = Ui.Tip(Tr("Show my capital on the map (Home)."));
        _identityFlag.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
            PlayStrategicCue("ui_select");
            FocusOwnCapital();
            _identityFlag.AcceptEvent();
        };
        var column = RankedFlag(_identityFlag, "HudCountryRank", () => _campaign?.Player);
        column.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        column.Alignment = BoxContainer.AlignmentMode.Center;
        foreach (var child in column.GetChildren())
            if (child is Control control) control.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        margin.AddChild(column);
    }

    /// <summary>
    /// The flag's height on the mount (`hud.flag_mount_size`), lowered for a flag so wide it would
    /// push the mount into the plate beside it - Qatar is two and a half times as wide as it is tall.
    /// </summary>
    private static float MountFlagHeight(string tag)
    {
        var height = Mathf.Clamp(Ui.Metric("hud.flag_mount_size", 40f), 20f, 72f);
        var texture = FlagTexture(tag);
        if (texture is null || texture.GetHeight() <= 0 || texture.GetWidth() <= 0) return height;
        var widest = height * Mathf.Clamp(Ui.Metric("hud.flag_mount_widest", 1.6f), 1f, 3f);
        var width = height * texture.GetWidth() / texture.GetHeight();
        return width <= widest ? height : Mathf.Max(16f, Mathf.Floor(widest * texture.GetHeight() / texture.GetWidth()));
    }

    /// <summary>
    /// Place the top bar, and dress it as a plate while materials are switched on. Its layout key
    /// declares no material yet (the layout-key pin uses `hud.top` as its example of a key without
    /// one), so the role is chosen here: `plate.strip`, a plate with the narrow margins a bar needs.
    /// A material a skin does declare on `hud.top` wins.
    /// </summary>
    private void PlaceTopPlate()
    {
        if (_hudTopPanel is null) return;
        Ui.Place(_hudTopPanel, "hud.top");
        // Beside the date plate (step 3): on one row the bar stops short of it, whatever width the
        // plate has grown to; on two rows the second runs on below it, to its right end - at 1280
        // the twelve figures need the whole width, and the first row has nothing on its right.
        if (DatePlateSpan(out var plateLeft, out var plateRight, out _))
            _hudTopPanel.OffsetRight = _hudSecondRow
                ? Mathf.Max(_hudTopPanel.OffsetRight, plateRight)
                : Mathf.Min(_hudTopPanel.OffsetRight, plateLeft - DatePlateGap);
        if (InterfaceSkin.LayoutMaterial("hud.top") is null &&
            InterfaceSkin.Metric(InterfaceSkin.MaterialSwitch, 0f) >= .5f)
            MaterialStyles.Dress(_hudTopPanel, "plate.strip");
    }

    /// <summary>One cell's width on the plate: the long form with its week and thread, or the brief one.</summary>
    private static float ResourceCellWidth(bool brief) => brief
        ? Mathf.Clamp(Ui.Metric("hud.resource_cell_width_brief", 88f), 56f, 240f)
        : Mathf.Clamp(Ui.Metric("hud.resource_cell_width", 124f), 68f, 280f);

    /// <summary>"…" at the plate's edge, and the drawer of the other figures it opens.</summary>
    private void BuildResourcePlate()
    {
        _hudMoreButton = ActionButton("…", ToggleResourceDrawer, 30, 28);
        _hudMoreButton.Name = "HudMoreFigures";
        _hudMoreButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        StyleHudButton(_hudMoreButton, false);
        // Remember whether "…" was pressed from the keyboard: a keyboard player is then taken
        // into the drawer, since its figures (the debt among them) are otherwise out of Tab's reach.
        _hudMoreButton.GuiInput += input =>
        {
            if (input.IsPressed()) _hudDrawerByKey = input is InputEventKey or InputEventJoypadButton;
        };
        _hudStatRow.AddChild(_hudMoreButton);

        var drawer = new ResourceDrawer { Name = "HudResourceDrawer", Visible = false, Opener = _hudMoreButton };
        _hudResourceDrawer = drawer;
        _hudLayer.AddChild(drawer);
        ApplyHudFrame(drawer, floating: true);
        DressResourceDrawer();
        var margin = Margin(drawer, 8, 6);
        _hudDrawerRow = new HBoxContainer { Name = "ResourceDrawerRow" };
        _hudDrawerRow.AddThemeConstantOverride("separation", 6);
        margin.AddChild(_hudDrawerRow);
        _hudPlateArrangement = "";
        ArrangeResourcePlate();
    }

    /// <summary>`plate.modal` while materials are switched on, the HUD frame it was built in while they are off.</summary>
    private void DressResourceDrawer()
    {
        if (_hudResourceDrawer is null) return;
        if (InterfaceSkin.Metric(InterfaceSkin.MaterialSwitch, 0f) >= .5f) MaterialStyles.Dress(_hudResourceDrawer, "plate.modal");
        else MaterialStyles.Undress(_hudResourceDrawer);
    }

    private void ToggleResourceDrawer()
    {
        if (_hudResourceDrawer is null || _hudMoreButton is null) return;
        if (_hudResourceDrawer.Visible) { _hudResourceDrawer.Hide(); return; }
        PlayStrategicCue("ui_select");
        // Dressed each time it opens: the drawer has no layout key, so a skin reload or an era
        // overlay that switches ui.materials since the last opening would otherwise not reach it.
        DressResourceDrawer();
        // Last in the layer, so the panels docked after the top bar do not draw over it.
        _hudLayer.MoveChild(_hudResourceDrawer, -1);
        _hudResourceDrawer.Show();
        _hudResourceDrawer.ResetSize();
        var local = _hudLayer.GetGlobalTransformWithCanvas().AffineInverse();
        var opener = _hudMoreButton.GetGlobalTransformWithCanvas();
        var corner = local * (opener * new Vector2(_hudMoreButton.Size.X, _hudMoreButton.Size.Y));
        var size = _hudResourceDrawer.GetCombinedMinimumSize();
        var x = Mathf.Clamp(corner.X - size.X, 8f, Mathf.Max(8f, _hudLayer.Size.X - size.X - 8f));
        _hudResourceDrawer.Position = new Vector2(x, corner.Y + 6f);
        foreach (var stat in _hudStats)
            if (stat?.Box is not null && stat.Box.GetParent() == _hudDrawerRow) FitHudStat(stat);
        if (_hudDrawerByKey)
            _hudDrawerRow.GetChildren().OfType<Control>().FirstOrDefault(cell => cell.Visible && cell.FocusMode != FocusModeEnum.None)?.GrabFocus();
        _hudDrawerByKey = false;
    }

    /// <summary>
    /// The figure a stat's state is judged on: the stock the cell shows, in the units its
    /// thresholds are written in. The weekly balance is judged as a year of it, so its
    /// thresholds read as the deficit a finance minister would quote (a share of GDP a year).
    /// </summary>
    private double? ResourceReading(string stat, Nation player) => stat switch
    {
        "TREASURY" => player.Treasury,
        "DEBT" => SovereignFinance.Outstanding(player),
        "BALANCE" => player.Budget.HasEntries ? player.WeeklyBalance * 52d : null,
        "GDP" => player.Gdp,
        "INFLATION" => player.Inflation,
        "POPULATION" => player.Population,
        "STABILITY" => player.Stability,
        "MANPOWER" => player.ManpowerReserve,
        "ARMY XP" => player.ArmyExperience,
        "AIR XP" => player.AirExperience,
        "NAVY XP" => player.NavalExperience,
        "POLITICAL CAPITAL" => player.PoliticalPoints,
        _ => null
    };

    /// <summary>
    /// Paint each figure in its state: a figure at a warning or worse has its number in that
    /// colour, so trouble reads at a glance with no hover; a figure that is fine keeps the plate's
    /// cream. The half-year thread is always in the state colour - quiet grey for a figure that has
    /// no state. While an observer or a movement has borrowed the bar, nothing is judged: the
    /// places say something else then.
    /// </summary>
    private void PaintResourceStates(Nation player)
    {
        foreach (var stat in _hudStats)
        {
            if (stat?.Box is null || !GodotObject.IsInstanceValid(stat.Box)) continue;
            var state = HudResourcePlate.State.None;
            if (!_topBarShowsMovement && player is not null && ResourceReading(stat.CountryCaption, player) is { } value)
                state = HudResourcePlate.StateOf(stat.CountryCaption, value, player.Gdp, player.Population);
            stat.State = state;
            var token = HudResourcePlate.Token(state);
            var alarming = state is HudResourcePlate.State.Warning or HudResourcePlate.State.Bad;
            stat.Value.AddThemeColorOverride("font_color", Ui.Colour(alarming ? token : "text.bright"));
            if (stat.Spark is not null)
            {
                var thread = Ui.Colour(token.Length > 0 ? token : "text.muted");
                if (stat.Spark.StateColour != thread) { stat.Spark.StateColour = thread; stat.Spark.QueueRedraw(); }
            }
        }
    }

    /// <summary>
    /// Put each figure on the plate or in the drawer behind "…" (HudResourcePlate.Arrange), and let
    /// "…" speak for what is behind it: tinted with the worst state there, and gone when nothing is.
    /// </summary>
    private void ArrangeResourcePlate()
    {
        if (_hudStatRow is null || _hudDrawerRow is null || _hudMoreButton is null) return;
        var cells = _hudStats.Where(stat => stat?.Box is not null && GodotObject.IsInstanceValid(stat.Box))
            .ToDictionary(stat => stat.CountryCaption, StringComparer.Ordinal);
        var order = _hudStats.Where(stat => stat is not null).Select(stat => stat.CountryCaption).ToList();
        var borrowed = _topBarAsCountry.Keys.Where(stat => stat is not null).Select(stat => stat.CountryCaption);
        var troubled = Ui.Metric("hud.promote_bad_stats", 1f) > 0f
            ? _hudStats.Where(stat => stat is { State: HudResourcePlate.State.Bad }).Select(stat => stat.CountryCaption)
            : Enumerable.Empty<string>();
        // A phone-narrow screen keeps a few figures and puts the rest behind "…"; a desktop shows them all.
        HudResourcePlate.Narrow = Size.X < Ui.Metric("hud.resource_drawer_below", 1100f);
        var (plate, drawer) = HudResourcePlate.Arrange(order, borrowed, troubled);
        var arrangement = string.Join("|", plate) + "#" + string.Join("|", drawer);
        if (arrangement != _hudPlateArrangement)
        {
            _hudPlateArrangement = arrangement;
            var index = 0;
            foreach (var key in plate)
            {
                if (!cells.TryGetValue(key, out var stat)) continue;
                if (stat.Box.GetParent() != _hudStatRow) stat.Box.Reparent(_hudStatRow, false);
                _hudStatRow.MoveChild(stat.Box, index++);
            }
            _hudStatRow.MoveChild(_hudMoreButton, _hudStatRow.GetChildCount() - 1);
            if (_hudFiguresButton is not null && _hudFiguresButton.GetParent() == _hudStatRow)
                _hudStatRow.MoveChild(_hudFiguresButton, _hudStatRow.GetChildCount() - 1);
            index = 0;
            foreach (var key in drawer)
            {
                if (!cells.TryGetValue(key, out var stat)) continue;
                if (stat.Box.GetParent() != _hudDrawerRow) stat.Box.Reparent(_hudDrawerRow, false);
                _hudDrawerRow.MoveChild(stat.Box, index++);
            }
        }

        var hidden = drawer.Where(key => cells.TryGetValue(key, out var stat) && stat.Box.Visible).Select(key => cells[key]).ToList();
        _hudMoreButton.Visible = hidden.Count > 0;
        if (hidden.Count == 0 && _hudResourceDrawer is { Visible: true }) _hudResourceDrawer.Hide();
        var worst = hidden.Aggregate(HudResourcePlate.State.None, (state, stat) => HudResourcePlate.Worse(state, stat.State));
        var tip = Tr("The other figures. A figure in trouble also comes up onto the plate by itself.") + "\n" +
            string.Join(" · ", hidden.Select(stat => Tr(stat.CaptionKey)));
        if (worst is HudResourcePlate.State.Warning or HudResourcePlate.State.Bad)
        {
            _hudMoreButton.AddThemeColorOverride("font_color", Ui.Colour(HudResourcePlate.Token(worst)));
            tip += "\n" + Loc.Format("Needs attention: {0}", string.Join(", ", hidden
                .Where(stat => stat.State is HudResourcePlate.State.Warning or HudResourcePlate.State.Bad)
                .Select(stat => Tr(stat.CaptionKey))));
        }
        else _hudMoreButton.RemoveThemeColorOverride("font_color");
        _hudMoreButton.TooltipText = Ui.Tip(tip);
    }

    /// <summary>
    /// The drawer behind "…". Any click outside it closes it, and so does a click on one of its
    /// figures - after the figure has had the click and opened its screen. Only "…" itself is left
    /// to toggle it. From the keyboard the same: Enter on a figure opens its screen and closes the
    /// drawer, Escape closes it and hands focus back to "…", and Tab out of it closes it.
    /// </summary>
    private sealed partial class ResourceDrawer : PanelContainer
    {
        public Control Opener;

        public override void _Ready() => GetViewport().GuiFocusChanged += FocusMoved;

        public override void _ExitTree()
        {
            if (GetViewport() is { } viewport) viewport.GuiFocusChanged -= FocusMoved;
        }

        private void FocusMoved(Control focus)
        {
            if (Visible && focus is not null && focus != Opener && !IsAncestorOf(focus)) Hide();
        }

        public override void _Input(InputEvent input)
        {
            if (!Visible) return;
            if (input is InputEventKey { Pressed: true, Echo: false } key && GetViewport().GuiGetFocusOwner() is { } focus && IsAncestorOf(focus))
            {
                if (key.Keycode is Key.Escape)
                {
                    Hide();
                    if (Opener is not null && IsInstanceValid(Opener) && Opener.IsVisibleInTree()) Opener.GrabFocus();
                    GetViewport().SetInputAsHandled();
                }
                else if (key.Keycode is Key.Enter or Key.KpEnter) CallDeferred(CanvasItem.MethodName.Hide);
                return;
            }
            if (input is not InputEventMouseButton { Pressed: true } press ||
                press.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;
            if (Opener is not null && IsInstanceValid(Opener) && Opener.IsVisibleInTree() &&
                Opener.GetGlobalRect().HasPoint(Opener.GetGlobalMousePosition())) return;
            CallDeferred(CanvasItem.MethodName.Hide);
        }
    }
}
