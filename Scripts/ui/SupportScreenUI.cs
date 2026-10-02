using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

// The long-range forces panel (item 87): the artillery, aircraft, drones and missile brigades a
// country reaches into the war with.
//
// It was a page laid over the map, and the one thing a player needs while giving these forces
// orders is the map: where the bases are, what they reach, where the battles are. So it is a
// panel docked beside the map instead, the way Hearts of Iron keeps its air wings, and the map
// answers it - every base shown from further out, every formation's reach drawn in the colour of
// its arm, the selected ones filled. Formations are chosen in the list (a click adds or removes
// one, "all" takes a base's whole establishment) and given a mission with one button for all of
// them; they are rebased by clicking a region of the country on the map. A new formation is
// raised into the region last clicked on the map, so where it goes is a decision taken looking at
// the front rather than at a list of names.
public partial class DealOrForceGame
{
    private Control _supportLayer = null!;
    private Label _supportSummary = null!;
    private ScrollContainer _supportScroll = null!;
    private VBoxContainer _supportRows = null!;
    private Label _supportFeedback = null!;
    private Control _supportSelectionBar = null!;
    private Label _supportSelectionCaption = null!;
    private GridContainer _supportSelectionMissions = null!;
    private Button _supportRebaseButton = null!;
    private Label _supportRebaseBanner = null!;
    private Button _supportRaiseToggle = null!;
    private Button _supportRaiseButton = null!;
    private ScrollContainer _supportRaiseScroll = null!;
    private VBoxContainer _supportRaiseBody = null!;
    private GridContainer _supportTypeGrid = null!;
    private Label _supportTypeDetail = null!;
    private Label _supportCostReadout = null!;
    private OptionButton _supportRegionPicker = null!;
    private VBoxContainer _supportTrainingRows = null!;
    private Label _supportTrainingCaption = null!;
    private string _supportFilter = "all";
    private string _supportRaiseType = "";
    private bool _supportRebaseArmed;
    private bool _supportRaiseOpen;
    private int _supportSelectionAnchor = -1;
    private readonly HashSet<int> _supportSelected = new();
    private readonly Dictionary<string, Button> _supportFilterButtons = new();
    private readonly Dictionary<string, Button> _supportTypeButtons = new();
    private readonly Dictionary<int, SupportRow> _supportRowMap = new();
    private readonly Dictionary<int, SupportTrainingRow> _supportTrainingMap = new();
    private readonly Dictionary<int, SupportSection> _supportRegionSections = new();

    private sealed class SupportRow
    {
        public Button Row;
        public TextureRect Icon;
        public Label Name;
        public Label Loss;
        public ColorRect StrengthFill;
        public Label Strength;
        public HBoxContainer Missions;
        public Label Displaced;
        public SupportFormation Formation;
        public string MissionSet = "";
    }

    private sealed class SupportSection
    {
        public VBoxContainer Box;
        public Button Caption;
        public Button SelectAll;
        public VBoxContainer Formations;
    }

    private sealed class SupportTrainingRow
    {
        public Label Name;
        public ColorRect ProgressFill;
        public Label Progress;
        public Button Cancel;
        public SupportOrder Order;
    }

    /// <summary>The filters above the list: what arm of the long-range forces is shown.</summary>
    private static readonly (string Id, string Label, SupportKind? Kind)[] SupportFilters =
    {
        ("all", "All", null),
        ("air", "Aircraft", SupportKind.Air),
        ("drone", "Drones", SupportKind.Drone),
        ("ground", "Artillery", SupportKind.Ground),
        ("air_defence", "Air defence", SupportKind.AirDefence)
    };

    // ------------------------------------------------------------------ build

    private void BuildSupportScreen()
    {
        // No shade: the map stays live behind the panel, and clicks on it reach the globe.
        _supportLayer = FullLayer("Support");
        _supportLayer.Visible = false;
        _supportLayer.MouseFilter = MouseFilterEnum.Ignore;
        _hudLayer.AddChild(_supportLayer);
        Resized += () => { if (_supportLayer.Visible) RefreshSupportScreen(); };

        var panel = TexturedPanel(_supportLayer);
        Ui.Place(panel, "hud.support_panel");
        var rows = PanelRows(panel, 16, 12, 7);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        var title = Title("LONG-RANGE FORCES", 21, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(ActionButton("×", CloseSupportScreen, 34, 30));

        _supportSummary = Title("", 12, Ui.Colour("text.body_soft"));
        _supportSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportSummary.CustomMinimumSize = new Vector2(1, 18);
        rows.AddChild(_supportSummary);
        var help = Title("Click: select · Ctrl: toggle · Shift: range · drag: group · right-click: rebase", 11, Ui.Colour("text.caption"));
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        help.CustomMinimumSize = new Vector2(1, 0);
        rows.AddChild(help);

        // What arm of the forces the list shows.
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 4);
        rows.AddChild(filters);
        foreach (var (id, label, _) in SupportFilters)
        {
            var captured = id;
            var chip = ActionButton(label, () => SetSupportFilter(captured), 0, 26);
            chip.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            chip.ToggleMode = true;
            filters.AddChild(chip);
            _supportFilterButtons[id] = chip;
        }

        _supportRebaseBanner = Title("", 12, Ui.Colour("text.accent"));
        _supportRebaseBanner.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportRebaseBanner.CustomMinimumSize = new Vector2(1, 18);
        _supportRebaseBanner.Visible = false;
        rows.AddChild(_supportRebaseBanner);

        _supportScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true
        };
        rows.AddChild(_supportScroll);
        _supportRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportRows.AddThemeConstantOverride("separation", 6);
        _supportScroll.AddChild(_supportRows);

        // ---- what to do with the selected formations, above the scrollable roster.
        var selection = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
        selection.AddThemeConstantOverride("separation", 5);
        rows.AddChild(selection);
        _supportSelectionBar = selection;
        var selectionHeader = new HBoxContainer();
        selectionHeader.AddThemeConstantOverride("separation", 6);
        selection.AddChild(selectionHeader);
        _supportSelectionCaption = Title("", 12, Ui.Colour("text.caption"));
        _supportSelectionCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _supportSelectionCaption.VerticalAlignment = VerticalAlignment.Center;
        selectionHeader.AddChild(_supportSelectionCaption);
        var reach = ActionButton("SHOW REACH", FocusSupportSelection, 110, 26);
        Tip(reach, Tr("Centre the map on the selected formations and their reach."));
        selectionHeader.AddChild(reach);
        _supportRebaseButton = ActionButton("REBASE ON MAP", ToggleSupportRebase, 130, 26);
        _supportRebaseButton.ToggleMode = true;
        Tip(_supportRebaseButton, Tr("Then click a region of the country on the map: the selected formations are based there, if it has posts free."));
        selectionHeader.AddChild(_supportRebaseButton);
        selectionHeader.AddChild(ActionButton("CLEAR", ClearSupportSelection, 64, 26));
        _supportSelectionMissions = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportSelectionMissions.AddThemeConstantOverride("separation", 4);
        selection.AddChild(_supportSelectionMissions);
        rows.MoveChild(selection, _supportScroll.GetIndex());

        _supportFeedback = Title("", 11, Ui.Colour("state.negative"));
        _supportFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportFeedback.CustomMinimumSize = new Vector2(1, 16);
        rows.AddChild(_supportFeedback);

        // ---- raising a new formation, folded away until it is wanted.
        _supportRaiseToggle = ActionButton("▸ RAISE A FORMATION", ToggleSupportRaise, 0, 28);
        _supportRaiseToggle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _supportRaiseToggle.Alignment = HorizontalAlignment.Left;
        rows.AddChild(_supportRaiseToggle);
        _supportRaiseBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
        _supportRaiseBody.AddThemeConstantOverride("separation", 6);
        _supportRaiseScroll = new ScrollContainer
        {
            Visible = false, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, Ui.Metric("support.recruitment_height", 210)),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _supportRaiseScroll.AddChild(_supportRaiseBody);
        rows.AddChild(_supportRaiseScroll);

        _supportTypeGrid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportTypeGrid.AddThemeConstantOverride("h_separation", 4);
        _supportTypeGrid.AddThemeConstantOverride("v_separation", 4);
        _supportRaiseBody.AddChild(_supportTypeGrid);

        _supportTypeDetail = Title("", 11, Ui.Colour("text.body_soft"));
        _supportTypeDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportTypeDetail.CustomMinimumSize = new Vector2(1, 16);
        _supportRaiseBody.AddChild(_supportTypeDetail);

        var regionRow = new HBoxContainer();
        regionRow.AddThemeConstantOverride("separation", 6);
        _supportRaiseBody.AddChild(regionRow);
        var regionCaption = RowLabel(Tr("BASE"), 11, Ui.Colour("text.caption"), 48);
        regionCaption.VerticalAlignment = VerticalAlignment.Center;
        regionRow.AddChild(regionCaption);
        _supportRegionPicker = new OptionButton
        {
            CustomMinimumSize = new Vector2(200, 28),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FitToLongestItem = false
        };
        _supportRegionPicker.TooltipText = Ui.Tip("Or click a region of the country on the map.");
        _supportRegionPicker.ItemSelected += _ => RefreshSupportRaiseReadout();
        regionRow.AddChild(_supportRegionPicker);

        var raiseActions = new HBoxContainer();
        raiseActions.AddThemeConstantOverride("separation", 7);
        _supportRaiseBody.AddChild(raiseActions);
        _supportCostReadout = Title("", 12, Ui.Colour("text.accent_dim"));
        _supportCostReadout.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _supportCostReadout.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportCostReadout.CustomMinimumSize = new Vector2(1, 0);
        _supportCostReadout.VerticalAlignment = VerticalAlignment.Center;
        raiseActions.AddChild(_supportCostReadout);
        _supportRaiseButton = ActionButton("RAISE", RaiseSupportFormation, 96, 30, true);
        raiseActions.AddChild(_supportRaiseButton);

        _supportTrainingCaption = Title("", 11, Ui.Colour("text.caption"));
        _supportRaiseBody.AddChild(_supportTrainingCaption);
        _supportTrainingRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportTrainingRows.AddThemeConstantOverride("separation", 3);
        _supportRaiseBody.AddChild(_supportTrainingRows);
    }

    // ------------------------------------------------------------------ open, close, refresh

    private void OpenSupportScreen()
    {
        if (_campaign is null || !SupportTypeCatalog.All.Any(t => SupportCore.Enabled(_campaign, t))) return;
        EndDrawing();
        CloseFleetPanel();
        ClearArmySelectionAndClose();
        _openScreen = "Support";
        _sidePanel.Visible = false;
        HideMapSelection(true);
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_warLedgerLayer is not null) _warLedgerLayer.Visible = false;
        if (_stateAffairsLayer is not null) _stateAffairsLayer.Visible = false;
        if (_productionLayer is not null) _productionLayer.Visible = false;
        if (_diplomacyLayer is not null) _diplomacyLayer.Visible = false;
        if (_tradeScreenLayer is not null) _tradeScreenLayer.Visible = false;
        _supportLayer.Visible = true;
        _supportLayer.MoveToFront();
        _globe.SupportOverlay = true;
        _globe.SupportCommandMode = true;
        _globe.SelectSupportFormations(_supportSelected);
        RefreshSupportScreen();
        UpdateScreenButtons();
    }

    private void CloseSupportScreen()
    {
        _supportLayer.Visible = false;
        _supportRebaseArmed = false;
        _globe.SupportOverlay = false;
        _globe.SupportCommandMode = false;
        if (_openScreen == "Support") _openScreen = "";
        UpdateScreenButtons();
    }

    /// <summary>A formation clicked on the map: the panel opens with it selected.</summary>
    private void OnSupportPicked(SupportFormation formation)
    {
        if (_campaign?.Player is null || formation.NationId != _campaign.Player.Id) return;
        if (!_supportLayer.Visible) _supportSelected.Clear();
        SelectSupportRow(formation.Id, false);
        OpenSupportScreen();
        if (_supportRowMap.TryGetValue(formation.Id, out var row))
            _supportScroll.EnsureControlVisible(row.Row);
    }

    /// <summary>
    /// A click on the map while the panel is open. With a rebase armed it is where the selected
    /// formations go; otherwise it chooses the base a new formation is raised into. True when the
    /// click was the panel's, so the country card does not open over it.
    /// </summary>
    private bool TrySupportMapPick(GlobePick pick)
    {
        if (_supportLayer is not { Visible: true } || _campaign?.Player is null || pick.State is null) return false;
        var player = _campaign.Player;
        if (_supportRebaseArmed)
        {
            RebaseSupportSelection(pick.State);
            return true;
        }
        if (_supportRaiseOpen && pick.State.NationMapIndex == player.MapIndex)
            SelectSupportRaiseRegion(pick.State.MapIndex);
        return true;
    }

    private void RefreshSupportScreen()
    {
        if (_supportLayer is null || !_supportLayer.Visible || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var owned = _campaign.SupportFormations.Where(f => f.NationId == player.Id && SupportCore.Enabled(_campaign, f.TypeId)).ToList();
        var training = _campaign.SupportOrders.Where(o => o.NationId == player.Id && SupportCore.Enabled(_campaign, o.TypeId)).ToList();
        _supportSelected.RemoveWhere(id => owned.All(f => f.Id != id));

        var working = owned.Count(f => f.Mission != SupportCore.MissionStandby);
        var hurt = owned.Count(f => _campaign.SupportLossesToday.ContainsKey(f.Id));
        var readiness = owned.Count == 0 ? 0f : owned.Average(f => f.Strength);
        _supportSummary.Text = Loc.Format("{0} · {1} · {2} · readiness {3:P0}",
            Loc.Format(Plural.Key("{0} formation", owned.Count), owned.Count),
            Loc.Format("{0} on missions", working),
            Loc.Format(Plural.Key("{0} in training", training.Count), training.Count),
            readiness) +
            (hurt > 0 ? " · " + Loc.Format("{0} took losses yesterday", hurt) : "");

        foreach (var (id, _, kind) in SupportFilters)
        {
            var chip = _supportFilterButtons[id];
            chip.Visible = kind is null || SupportTypeCatalog.All.Any(t => t.Kind == kind && SupportCore.Enabled(_campaign, t));
            if (!chip.Visible && id == _supportFilter) _supportFilter = "all";
        }
        foreach (var (id, chip) in _supportFilterButtons) chip.SetPressedNoSignal(id == _supportFilter);
        _globe.SelectSupportFormations(_supportSelected);

        RefreshSupportEstablishment(player, owned);
        RefreshSupportSelection(owned);
        RefreshSupportTraining(training);
        RefreshSupportRaise(player);
    }

    // ------------------------------------------------------------------ the establishment

    private bool SupportFilterAdmits(SupportTypeDefinition type)
    {
        foreach (var (id, _, kind) in SupportFilters)
            if (id == _supportFilter) return kind is null || type.Kind == kind;
        return true;
    }

    private void SetSupportFilter(string filter)
    {
        _supportFilter = filter;
        RefreshSupportScreen();
    }

    private void RefreshSupportEstablishment(Nation player, List<SupportFormation> owned)
    {
        // Rows are kept across refreshes and repainted: a list a player is reading should not
        // be torn out from under their eyes every day the campaign steps. Sections are the bases
        // the country holds, by name, and after them the ones the war has taken - a formation
        // standing on lost ground is the one the player most needs to find and move.
        var shown = owned.Where(f => SupportTypeCatalog.TryGet(f.TypeId, out var type) && SupportFilterAdmits(type)).ToList();
        var byState = shown.GroupBy(f => f.StateMapIndex)
            .Select(group => (State: _campaign.States.Find(s => s.MapIndex == group.Key), Formations: group.ToList()))
            .Where(entry => entry.State is not null)
            .OrderByDescending(entry => entry.State.NationMapIndex == player.MapIndex)
            .ThenBy(entry => entry.State.DisplayName)
            .ToList();

        var wanted = new HashSet<int>();
        var order = 0;
        foreach (var (state, formations) in byState)
        {
            wanted.Add(state.MapIndex);
            if (!_supportRegionSections.TryGetValue(state.MapIndex, out var section))
            {
                section = BuildSupportSection(state.MapIndex);
                _supportRegionSections[state.MapIndex] = section;
                _supportRows.AddChild(section.Box);
            }
            _supportRows.MoveChild(section.Box, order++);
            var lost = formations.All(f => SupportCore.Displaced(_campaign, f));
            section.Caption.Text = $"{state.DisplayName} · " + (lost
                ? Tr("BASE LOST")
                : Loc.Format("posts {0}/{1}", SupportCore.PostsUsed(_campaign, state.MapIndex), SupportCore.CapacityOf(_campaign, state)));
            section.Caption.AddThemeColorOverride("font_color", Ui.Colour(lost ? "state.negative" : "text.caption"));
            var allSelected = formations.All(f => _supportSelected.Contains(f.Id));
            section.SelectAll.Text = allSelected ? Tr("NONE") : Tr("ALL");
            section.SelectAll.SetMeta("ids", string.Join(",", formations.Select(f => f.Id)));

            var index = 0;
            foreach (var formation in formations)
            {
                if (!_supportRowMap.TryGetValue(formation.Id, out var row))
                {
                    row = BuildSupportRow(formation);
                    _supportRowMap[formation.Id] = row;
                }
                if (row.Row.GetParent() != section.Formations)
                {
                    row.Row.GetParent()?.RemoveChild(row.Row);
                    section.Formations.AddChild(row.Row);
                }
                section.Formations.MoveChild(row.Row, index++);
                PaintSupportRow(row, formation, state);
            }
        }
        foreach (var (mapIndex, section) in _supportRegionSections.ToArray())
        {
            if (wanted.Contains(mapIndex)) continue;
            _supportRegionSections.Remove(mapIndex);
            // Rows in a section going away are kept if their formation is still shown elsewhere.
            foreach (var child in section.Formations.GetChildren().OfType<Button>().ToArray())
                section.Formations.RemoveChild(child);
            section.Box.QueueFree();
        }
        foreach (var (id, row) in _supportRowMap.ToArray())
        {
            if (row.Row.GetParent() is not null && shown.Any(f => f.Id == id)) continue;
            _supportRowMap.Remove(id);
            row.Row.QueueFree();
        }

        if (byState.Count == 0 && _supportRows.GetNodeOrNull("Empty") is null)
        {
            var empty = Title(owned.Count == 0
                ? Tr("The country has no long-range forces yet. Raise a formation below - then click a region on the map to base it.")
                : Tr("No formations of this kind."), 12, Ui.Colour("text.body_soft"));
            empty.Name = "Empty";
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _supportRows.AddChild(empty);
        }
        else if (byState.Count > 0) _supportRows.GetNodeOrNull("Empty")?.QueueFree();
    }

    private SupportSection BuildSupportSection(int stateMapIndex)
    {
        var section = new SupportSection { Box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill } };
        section.Box.AddThemeConstantOverride("separation", 3);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 6);
        section.Box.AddChild(header);
        // The base's name is a way to it: a click centres the map on the base.
        section.Caption = new Button
        {
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            ClipText = true
        };
        section.Caption.AddThemeFontSizeOverride("font_size", 12);
        section.Caption.Pressed += () => FocusSupportBase(stateMapIndex);
        section.Caption.TooltipText = Ui.Tip("Show this base on the map.");
        header.AddChild(section.Caption);
        section.SelectAll = ActionButton("ALL", () => ToggleSupportSection(section), 58, 22);
        section.SelectAll.TooltipText = Ui.Tip("Select or deselect every formation at this base.");
        header.AddChild(section.SelectAll);
        section.Formations = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        section.Formations.AddThemeConstantOverride("separation", 3);
        section.Box.AddChild(section.Formations);
        return section;
    }

    private void ToggleSupportSection(SupportSection section)
    {
        var ids = section.SelectAll.GetMeta("ids", "").AsString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        if (ids.Count == 0) return;
        var all = ids.All(_supportSelected.Contains);
        foreach (var id in ids)
            if (all) _supportSelected.Remove(id); else _supportSelected.Add(id);
        SupportSelectionChanged();
    }

    private static StyleBoxFlat SupportRowStyle(bool selected, bool hover)
    {
        var style = ShellRowStyle(hover);
        if (selected)
        {
            style.BgColor = style.BgColor.Lerp(Ui.Colour("text.accent"), 0.10f);
            style.BorderColor = Ui.Colour("text.accent");
            style.BorderWidthLeft = 3;
            style.BorderWidthTop = style.BorderWidthRight = style.BorderWidthBottom = 1;
        }
        return style;
    }

    private SupportRow BuildSupportRow(SupportFormation formation)
    {
        var row = new SupportRow { Formation = formation };
        var button = new Button
        {
            Name = $"SupportRow_{formation.Id}",
            CustomMinimumSize = new Vector2(0, Ui.Metric("support.roster_row_height", 58)),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            ActionMode = BaseButton.ActionModeEnum.Press
        };
        button.Pressed += () => SelectSupportRow(row.Formation.Id, true);
        row.Row = button;

        var inner = new VBoxContainer();
        inner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        inner.OffsetLeft = 9;
        inner.OffsetTop = 4;
        inner.OffsetRight = -8;
        inner.OffsetBottom = -4;
        inner.AddThemeConstantOverride("separation", 3);
        inner.MouseFilter = MouseFilterEnum.Ignore;
        button.AddChild(inner);

        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 7);
        inner.AddChild(top);
        row.Icon = new TextureRect
        {
            CustomMinimumSize = new Vector2(22, 22),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        top.AddChild(row.Icon);
        row.Name = Title("", 12, Ui.Colour("text.bright"));
        row.Name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.Name.AutowrapMode = TextServer.AutowrapMode.Off;
        row.Name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.Name.MouseFilter = MouseFilterEnum.Ignore;
        top.AddChild(row.Name);
        row.Loss = Title("", 10, Ui.Colour("state.negative"));
        row.Loss.AutowrapMode = TextServer.AutowrapMode.Off;
        row.Loss.MouseFilter = MouseFilterEnum.Ignore;
        top.AddChild(row.Loss);
        var strengthBox = new VBoxContainer { CustomMinimumSize = new Vector2(92, 0), MouseFilter = MouseFilterEnum.Ignore };
        strengthBox.AddThemeConstantOverride("separation", 1);
        top.AddChild(strengthBox);
        var track = new ColorRect { Color = new Color(1, 1, 1, 0.08f), CustomMinimumSize = new Vector2(92, 5), MouseFilter = MouseFilterEnum.Ignore };
        strengthBox.AddChild(track);
        row.StrengthFill = new ColorRect { Color = Ui.Colour("state.positive"), MouseFilter = MouseFilterEnum.Ignore };
        row.StrengthFill.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        track.AddChild(row.StrengthFill);
        row.Strength = Title("", 10, Ui.Colour("text.body_soft"));
        row.Strength.AutowrapMode = TextServer.AutowrapMode.Off;
        row.Strength.MouseFilter = MouseFilterEnum.Ignore;
        strengthBox.AddChild(row.Strength);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 4);
        bottom.MouseFilter = MouseFilterEnum.Ignore;
        inner.AddChild(bottom);
        row.Missions = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.Missions.AddThemeConstantOverride("separation", 3);
        bottom.AddChild(row.Missions);
        row.Displaced = Title("", 10, Ui.Colour("state.negative"));
        row.Displaced.AutowrapMode = TextServer.AutowrapMode.Off;
        row.Displaced.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.Displaced.HorizontalAlignment = HorizontalAlignment.Right;
        row.Displaced.VerticalAlignment = VerticalAlignment.Center;
        row.Displaced.MouseFilter = MouseFilterEnum.Ignore;
        bottom.AddChild(row.Displaced);
        return row;
    }

    private void PaintSupportRow(SupportRow row, SupportFormation formation, ScenarioState state)
    {
        if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) return;
        row.Formation = formation;
        var selected = _supportSelected.Contains(formation.Id);
        row.Row.AddThemeStyleboxOverride("normal", SupportRowStyle(selected, false));
        row.Row.AddThemeStyleboxOverride("hover", SupportRowStyle(selected, true));
        row.Row.AddThemeStyleboxOverride("pressed", SupportRowStyle(selected, true));

        var nth = _campaign.SupportFormations
            .Count(f => f.NationId == formation.NationId && f.TypeId == formation.TypeId && f.Id <= formation.Id);
        row.Name.Text = $"{Loc.Get(type.Name)} {ToRomanOrNumber(nth)}";
        row.Icon.Texture = Art($"icon_{type.Icon}", $"res://game/gfx/interface/icons/icon_{type.Icon}.png");

        row.StrengthFill.AnchorRight = M.Clamp01(formation.Strength);
        row.StrengthFill.OffsetRight = 0;
        row.StrengthFill.Color = Ui.Colour(formation.Strength > 0.75f ? "state.positive" :
            formation.Strength > 0.35f ? "state.warning" : "state.negative");
        row.Strength.Text = Loc.Format("{0:P0} · {1:0} km", formation.Strength, type.RangeKm);
        row.Loss.Text = _campaign.SupportLossesToday.TryGetValue(formation.Id, out var loss) && loss > 0.0005f
            ? Loc.Format("−{0:0.0}%", loss * 100f) : "";
        row.Loss.TooltipText = Ui.Tip("Strength lost over the front yesterday: the enemy's fighters, missile brigades and guns.");

        var displaced = SupportCore.Displaced(_campaign, formation);
        row.Displaced.Text = displaced ? Tr("BASE LOST — REBASE IT") : "";

        var missionSet = string.Join(",", type.Missions);
        if (row.MissionSet != missionSet)
        {
            row.MissionSet = missionSet;
            foreach (var child in row.Missions.GetChildren()) { row.Missions.RemoveChild(child); child.QueueFree(); }
            // One status line, not four competing miniature command bars per row.
            var status = Title("", 11, Ui.Colour("text.body_soft"));
            status.Name = "MissionStatus";
            status.AutowrapMode = TextServer.AutowrapMode.Off;
            status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            status.MouseFilter = MouseFilterEnum.Ignore;
            row.Missions.AddChild(status);
        }
        if (row.Missions.GetNodeOrNull<Label>("MissionStatus") is { } missionStatus)
        {
            missionStatus.Text = Tr(SupportMissionShort(formation.Mission));
            missionStatus.AddThemeColorOverride("font_color", Ui.Colour(
                formation.Mission == SupportCore.MissionStandby ? "text.caption" : "text.accent"));
        }

        row.Row.TooltipText = Ui.Tip(
            $"{Loc.Get(type.Name)}\n" +
            Loc.Format("reach {0} km from {1}", type.RangeKm, state.DisplayName) + "\n" +
            Loc.Format("fire {0:0.#} · air superiority {1:0.#} · interdiction {2:0.#} · air defence {3:0.#} · recon {4:0.#}",
                type.SupportFire, type.AirSuperiority, type.Interdiction, type.AirDefence, type.Recon) + "\n" +
            Loc.Format("experience {0:P0}", formation.Experience) + "\n" +
            SupportMissionHelp(formation.Mission) + "\n" +
            Tr("Click selects one. Ctrl toggles; Shift selects a range. Use the command bar for missions."));
    }

    /// <summary>A mission's player-facing name, shared by the roster and command bar.</summary>
    private static string SupportMissionShort(string mission) => mission switch
    {
        SupportCore.MissionStandby => "Standby / recover",
        SupportCore.MissionCloseAirSupport => "Fire support",
        SupportCore.MissionAirSuperiority => "Air superiority",
        SupportCore.MissionInterdiction => "Interdiction",
        _ => mission
    };

    private static string SupportMissionHelp(string mission) => mission switch
    {
        SupportCore.MissionStandby => "Rest: fly nothing, repair and recover twice as fast. Missile brigades defend their ground whatever they are told.",
        SupportCore.MissionCloseAirSupport => "Support: add fire to every battle of ours inside the reach.",
        SupportCore.MissionAirSuperiority => "Sky: sweep the enemy's aircraft from over our battles - their support fire suffers, and our own aircraft lose less.",
        SupportCore.MissionInterdiction => "Strike: hit what is behind the enemy's line, stripping the defence of every battle inside the reach.",
        _ => mission
    };

    private static string ToRomanOrNumber(int n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        9 => "IX", 10 => "X", _ => n.ToString()
    };

    // ------------------------------------------------------------------ selection and orders

    private void SelectSupportRow(int id, bool allowRange)
    {
        var ctrl = Input.IsKeyPressed(Key.Ctrl);
        if (allowRange && Input.IsKeyPressed(Key.Shift) && _supportSelectionAnchor > 0)
        {
            var visible = _supportRegionSections.Values.OrderBy(s => s.Box.GetIndex())
                .SelectMany(s => s.Formations.GetChildren().OfType<Button>())
                .Select(b => b.Name.ToString()).Where(n => n.StartsWith("SupportRow_"))
                .Select(n => int.Parse(n[11..])).ToList();
            var a = visible.IndexOf(_supportSelectionAnchor);
            var b = visible.IndexOf(id);
            if (!ctrl) _supportSelected.Clear();
            if (a >= 0 && b >= 0)
                foreach (var selected in visible.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
                    _supportSelected.Add(selected);
            else _supportSelected.Add(id);
        }
        else
        {
            if (!ctrl) _supportSelected.Clear();
            if (!_supportSelected.Add(id)) _supportSelected.Remove(id);
            _supportSelectionAnchor = id;
        }
        _supportRebaseArmed = false;
        SupportSelectionChanged();
    }

    private void OnSupportBoxPicked(List<SupportFormation> formations)
    {
        if (_supportLayer is not { Visible: true }) return;
        if (!Input.IsKeyPressed(Key.Ctrl) && !Input.IsKeyPressed(Key.Shift)) _supportSelected.Clear();
        foreach (var formation in formations) _supportSelected.Add(formation.Id);
        _supportRebaseArmed = false;
        SupportSelectionChanged();
    }

    private void ClearSupportSelection()
    {
        _supportSelected.Clear();
        _supportRebaseArmed = false;
        SupportSelectionChanged();
    }

    private void SupportSelectionChanged()
    {
        _globe.SelectSupportFormations(_supportSelected);
        _supportFeedback.Text = "";
        RefreshSupportScreen();
    }

    private void RefreshSupportSelection(List<SupportFormation> owned)
    {
        var selected = owned.Where(f => _supportSelected.Contains(f.Id)).ToList();
        // On short screens recruitment and orders are separate tasks. Preserve selection,
        // but give the roster room instead of crushing it between two command surfaces.
        _supportSelectionBar.Visible = selected.Count > 0 &&
            (!_supportRaiseOpen || Size.Y >= Ui.Metric("support.compact_height", 840));
        if (selected.Count == 0) _supportRebaseArmed = false;
        _supportRebaseButton.SetPressedNoSignal(_supportRebaseArmed);
        _globe.SupportRebaseArmed = _supportRebaseArmed;
        _supportRebaseBanner.Visible = _supportRebaseArmed;
        _supportRebaseBanner.Text = Loc.Format(
            "Click a region of the country on the map to base the {0} selected formation(s) there. Esc cancels.",
            selected.Count);
        if (selected.Count == 0) return;
        _supportSelectionCaption.Text = Loc.Format("{0} selected", selected.Count);

        // The missions any of them can fly, in the order the catalogue gives them.
        var missions = new List<string>();
        foreach (var formation in selected)
            if (SupportTypeCatalog.TryGet(formation.TypeId, out var type))
                foreach (var mission in type.Missions)
                    if (!missions.Contains(mission)) missions.Add(mission);
        var existing = _supportSelectionMissions.GetChildren().OfType<Button>().Select(b => b.GetMeta("mission", "").AsString()).ToList();
        if (!existing.SequenceEqual(missions))
        {
            foreach (var child in _supportSelectionMissions.GetChildren()) { _supportSelectionMissions.RemoveChild(child); child.QueueFree(); }
            foreach (var mission in missions)
            {
                var captured = mission;
                var button = ActionButton(SupportMissionShort(mission),
                    () => SetSupportMission(_supportSelected.ToArray(), captured), 0, 26);
                button.SetMeta("mission", mission);
                button.ToggleMode = true;
                button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                button.TooltipText = Ui.Tip(SupportMissionHelp(mission) + "\n" +
                    Tr("Given to every selected formation that can fly it."));
                _supportSelectionMissions.AddChild(button);
            }
        }
        foreach (var button in _supportSelectionMissions.GetChildren().OfType<Button>())
        {
            var mission = button.GetMeta("mission", "").AsString();
            var compatible = selected.Count(f => SupportTypeCatalog.TryGet(f.TypeId, out var type) && type.Missions.Contains(mission));
            button.Text = Tr(SupportMissionShort(mission)) + $" · {compatible}/{selected.Count}";
            button.SetPressedNoSignal(compatible > 0 && selected.Where(f => SupportTypeCatalog.TryGet(f.TypeId, out var type) && type.Missions.Contains(mission)).All(f => f.Mission == mission));
        }
    }

    private void SetSupportMission(IEnumerable<int> formationIds, string mission)
    {
        if (_campaign?.Player is null) return;
        var commands = new List<SetSupportMissionCommand>();
        foreach (var id in formationIds.Distinct())
        {
            var formation = _campaign.SupportFormations.Find(f => f.Id == id && f.NationId == _campaign.Player.Id);
            if (formation is null || !SupportTypeCatalog.TryGet(formation.TypeId, out var type) || !type.Missions.Contains(mission)) continue;
            var command = new SetSupportMissionCommand
            {
                IssuerId = _campaign.Player.Id, FormationId = id, Mission = mission
            };
            commands.Add(command);
            _campaign.Commands.Enqueue(command);
        }
        if (commands.Count == 0) return;
        _campaign.Commands.ApplyAll(_campaign);
        var applied = commands.Count(c => c.Refusal.Length == 0);
        _supportFeedback.AddThemeColorOverride("font_color", Ui.Colour(applied > 0 ? "state.positive" : "state.negative"));
        _supportFeedback.Text = Loc.Format("{0}: ordered {1} of {2} selected.", Tr(SupportMissionShort(mission)), applied, _supportSelected.Count);
        if (applied > 0) PlayStrategicCue("support_mission");
        _globe.SelectSupportFormations(_supportSelected);
        RefreshSupportScreen();
    }

    private void FocusSupportSelection()
    {
        var first = _campaign?.SupportFormations.FirstOrDefault(f => _supportSelected.Contains(f.Id) && f.BaseProvinceId > 0);
        if (first is null || _campaign.ProvinceMap is null) return;
        // The middle of the selection's bases, far enough out to see their reach.
        var bases = _campaign.SupportFormations.Where(f => _supportSelected.Contains(f.Id) && f.BaseProvinceId > 0)
            .Select(f => _campaign.ProvinceMap.CenterOf(f.BaseProvinceId)).ToList();
        var reach = _campaign.SupportFormations.Where(f => _supportSelected.Contains(f.Id))
            .Select(f => SupportTypeCatalog.Get(f.TypeId)?.RangeKm ?? 100f).DefaultIfEmpty(100f).Max();
        // Spherical mean: bases on either side of the date line are neighbours, not
        // opposite sides of a longitude average. Include their spread in the framing.
        Vector3 Direction(float lat, float lon)
        {
            var a = Mathf.DegToRad(lat); var b = Mathf.DegToRad(lon);
            return new Vector3(Mathf.Cos(a) * Mathf.Cos(b), Mathf.Sin(a), Mathf.Cos(a) * Mathf.Sin(b));
        }
        var directions = bases.Select(b => Direction(b.Latitude, b.Longitude)).ToArray();
        var mean = directions.Aggregate(Vector3.Zero, (sum, direction) => sum + direction);
        if (mean.LengthSquared() < .000001f) mean = directions[0];
        mean = mean.Normalized();
        var latitude = Mathf.RadToDeg(Mathf.Asin(mean.Y));
        var longitude = Mathf.RadToDeg(Mathf.Atan2(mean.Z, mean.X));
        var spread = directions.Max(d => Mathf.Acos(Mathf.Clamp(mean.Dot(d), -1f, 1f))) * 6371f;
        var distance = Mathf.Clamp(6.2f + (reach + spread) / 260f, 6.2f, 17f);
        _globe.FocusOn(latitude, longitude, distance);
    }

    private void FocusSupportBase(int stateMapIndex)
    {
        var formation = _campaign?.SupportFormations.FirstOrDefault(f => f.StateMapIndex == stateMapIndex && f.BaseProvinceId > 0);
        if (formation is null || _campaign.ProvinceMap is null) return;
        var centre = _campaign.ProvinceMap.CenterOf(formation.BaseProvinceId);
        _globe.FocusOn(centre.Latitude, centre.Longitude, 8.2f);
    }

    private void ToggleSupportRebase()
    {
        _supportRebaseArmed = !_supportRebaseArmed && _supportSelected.Count > 0;
        _supportFeedback.Text = "";
        RefreshSupportScreen();
    }

    private void CancelSupportRebase()
    {
        _supportRebaseArmed = false;
        _supportFeedback.Text = "";
        RefreshSupportScreen();
    }

    private void RebaseSupportSelection(ScenarioState target)
    {
        var player = _campaign.Player;
        var plan = SupportCore.PreviewRebase(_campaign, player.Id, _supportSelected, target.MapIndex);
        if (plan.Refusal.Length > 0)
        {
            _supportRebaseArmed = true;
            _supportFeedback.AddThemeColorOverride("font_color", Ui.Colour("state.negative"));
            _supportFeedback.Text = target.DisplayName + " · " + Tr(plan.Refusal) +
                (plan.Moving > 0 ? "\n" + Loc.Format("Need {0} posts · {1} free · {2} reserved for training. Nothing moved.", plan.Moving, plan.Free, plan.Reserved) : "");
            RefreshSupportScreen();
            return;
        }
        var command = new RebaseSupportGroupCommand
        {
            IssuerId = player.Id, FormationIds = _supportSelected.OrderBy(id => id).ToArray(), StateMapIndex = target.MapIndex
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _supportRebaseArmed = command.Refusal.Length > 0;
        _supportFeedback.AddThemeColorOverride("font_color", Ui.Colour(_supportRebaseArmed ? "state.negative" : "state.positive"));
        _supportFeedback.Text = _supportRebaseArmed ? Tr(command.Refusal) : command.Moved == 0
            ? Loc.Format("The selected formations are already based in {0}.", target.DisplayName)
            : Loc.Format("{0} formation(s) based in {1}.", command.Moved, target.DisplayName);
        if (command.Moved > 0) PlayStrategicCue("support_order");
        _globe.SelectSupportFormations(_supportSelected);
        RefreshSupportScreen();
    }

    // ------------------------------------------------------------------ in training

    private void RefreshSupportTraining(List<SupportOrder> training)
    {
        _supportTrainingCaption.Text = training.Count == 0 ? "" : Loc.Format("IN TRAINING · {0}", training.Count);
        foreach (var order in training)
        {
            if (!_supportTrainingMap.TryGetValue(order.Id, out var row))
            {
                var box = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 24) };
                box.AddThemeConstantOverride("separation", 7);
                var name = Title("", 11, Ui.Colour("text.bright"));
                name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                name.AutowrapMode = TextServer.AutowrapMode.Off;
                name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                box.AddChild(name);
                var track = new ColorRect { Color = new Color(1, 1, 1, 0.08f), CustomMinimumSize = new Vector2(74, 5), SizeFlagsVertical = SizeFlags.ShrinkCenter };
                box.AddChild(track);
                var progress = new ColorRect { Color = Ui.Colour("state.positive") };
                progress.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                track.AddChild(progress);
                var percent = Title("", 10, Ui.Colour("text.body_soft"));
                percent.AutowrapMode = TextServer.AutowrapMode.Off;
                percent.CustomMinimumSize = new Vector2(38, 14);
                percent.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                box.AddChild(percent);
                var captured = order.Id;
                var cancel = ActionButton("×", () => CancelSupportOrder(captured), 26, 22);
                cancel.TooltipText = Ui.Tip("Cancel: most of what is left of the money comes back, and all of the men.");
                box.AddChild(cancel);
                _supportTrainingRows.AddChild(box);
                row = new SupportTrainingRow { Name = name, ProgressFill = progress, Progress = percent, Cancel = cancel, Order = order };
                _supportTrainingMap[order.Id] = row;
            }
            var state = _campaign.States.Find(s => s.MapIndex == order.StateMapIndex);
            var type = SupportTypeCatalog.Get(order.TypeId);
            row.Name.Text = $"{Loc.Get(type?.Name ?? order.TypeId)} · {state?.DisplayName ?? ""}";
            var fraction = M.Clamp01(order.TrainedDays / (float)Math.Max(1, order.DurationDays));
            row.ProgressFill.AnchorRight = fraction;
            row.ProgressFill.OffsetRight = 0;
            row.Progress.Text = $"{fraction:P0}";
            row.Order = order;
        }
        foreach (var (id, row) in _supportTrainingMap.ToArray())
        {
            if (training.Any(o => o.Id == id)) continue;
            _supportTrainingMap.Remove(id);
            row.Cancel.GetParent().QueueFree();
        }
    }

    private void CancelSupportOrder(int orderId)
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new CancelSupportOrderCommand { IssuerId = _campaign.Player.Id, OrderId = orderId });
        _campaign.Commands.ApplyAll(_campaign);
        _supportFeedback.Text = "";
        RefreshSupportScreen();
    }

    // ------------------------------------------------------------------ raising

    private void ToggleSupportRaise()
    {
        _supportRaiseOpen = !_supportRaiseOpen;
        if (_supportRaiseOpen) _supportRebaseArmed = false;
        _supportRaiseScroll.Visible = _supportRaiseOpen;
        _supportRaiseBody.Visible = _supportRaiseOpen;
        _supportRaiseToggle.Text = Tr(_supportRaiseOpen ? "▾ RAISE A FORMATION" : "▸ RAISE A FORMATION");
        RefreshSupportScreen();
    }

    private void RefreshSupportRaise(Nation player)
    {
        if (_supportTypeButtons.Count == 0)
        {
            foreach (var type in SupportTypeCatalog.All.OrderBy(t => t.Kind).ThenBy(t => t.RangeKm))
            {
                var captured = type.Id;
                var button = ActionButton(Loc.Get(type.Name), () => PickSupportType(captured), 0, 40, false,
                    SpriteCatalog.TextureOf($"icon_{type.Icon}", $"res://game/gfx/interface/icons/icon_{type.Icon}.png"));
                button.ToggleMode = true;
                button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                button.ClipText = true;
                button.AddThemeFontSizeOverride("font_size", 11);
                button.ExpandIcon = false;
                button.AddThemeConstantOverride("icon_max_width", 22);
                _supportTypeGrid.AddChild(button);
                _supportTypeButtons[type.Id] = button;
            }
            if (_supportRaiseType.Length == 0) _supportRaiseType = SupportTypeCatalog.All.FirstOrDefault()?.Id ?? "";
        }
        if (!SupportCore.Enabled(_campaign, _supportRaiseType))
            _supportRaiseType = SupportTypeCatalog.All.FirstOrDefault(t => SupportCore.Enabled(_campaign, t))?.Id ?? "";
        foreach (var (id, button) in _supportTypeButtons)
        {
            button.Visible = SupportCore.Enabled(_campaign, id);
            button.SetPressedNoSignal(id == _supportRaiseType);
        }

        // The base picker follows the map: regions are gained and lost, and one that offered a
        // region the country no longer holds would be a promise the campaign refuses at the door.
        var selected = _supportRegionPicker.Selected >= 0 &&
                       _supportRegionPicker.GetItemMetadata(_supportRegionPicker.Selected).VariantType == Variant.Type.Int
            ? _supportRegionPicker.GetItemMetadata(_supportRegionPicker.Selected).AsInt32() : -1;
        _supportRegionPicker.Clear();
        var option = 0;
        foreach (var state in _campaign.States.Where(s => s.NationMapIndex == player.MapIndex).OrderBy(s => s.DisplayName))
        {
            var free = SupportCore.CapacityOf(_campaign, state) - SupportCore.PostsUsed(_campaign, state.MapIndex) -
                       _campaign.SupportOrders.Count(o => o.NationId == player.Id && o.StateMapIndex == state.MapIndex && SupportCore.Enabled(_campaign, o.TypeId));
            _supportRegionPicker.AddItem(Loc.Format("{0} · {1} free", state.DisplayName, Math.Max(0, free)));
            _supportRegionPicker.SetItemMetadata(option, state.MapIndex);
            if (state.MapIndex == selected) _supportRegionPicker.Select(option);
            option++;
        }
        if (_supportRegionPicker.Selected < 0 && _supportRegionPicker.ItemCount > 0) _supportRegionPicker.Select(0);
        if (_supportRegionPicker.ItemCount == 0) _supportRegionPicker.AddItem(Tr("No regions held"));
        RefreshSupportRaiseReadout();
    }

    private void SelectSupportRaiseRegion(int stateMapIndex)
    {
        for (var index = 0; index < _supportRegionPicker.ItemCount; index++)
            if (_supportRegionPicker.GetItemMetadata(index).VariantType == Variant.Type.Int &&
                _supportRegionPicker.GetItemMetadata(index).AsInt32() == stateMapIndex)
            {
                _supportRegionPicker.Select(index);
                break;
            }
        RefreshSupportRaiseReadout();
    }

    private void PickSupportType(string typeId)
    {
        _supportRaiseType = typeId;
        foreach (var (id, button) in _supportTypeButtons) button.SetPressedNoSignal(id == typeId);
        RefreshSupportRaiseReadout();
    }

    private int SupportRaiseRegion() =>
        _supportRegionPicker.Selected >= 0 &&
        _supportRegionPicker.GetItemMetadata(_supportRegionPicker.Selected).VariantType == Variant.Type.Int
            ? _supportRegionPicker.GetItemMetadata(_supportRegionPicker.Selected).AsInt32() : -1;

    private void RefreshSupportRaiseReadout()
    {
        if (_campaign?.Player is null || !SupportTypeCatalog.TryGet(_supportRaiseType, out var type) || !SupportCore.Enabled(_campaign, type))
        {
            _supportRaiseButton.Disabled = true;
            _supportTypeDetail.Text = "";
            _supportCostReadout.Text = Tr("This mechanic is disabled in this scenario.");
            return;
        }
        var player = _campaign.Player;
        var missions = string.Join(", ", type.Missions.Where(m => m != SupportCore.MissionStandby).Select(m => Tr(SupportMissionShort(m))));
        _supportTypeDetail.Text =
            Loc.Format("reach {0:0} km · fire {1:0.#}", type.RangeKm, type.SupportFire) +
            (type.AirSuperiority > 0 ? " · " + Loc.Format("air superiority {0:0.#}", type.AirSuperiority) : "") +
            (type.Interdiction > 0 ? " · " + Loc.Format("interdiction {0:0.#}", type.Interdiction) : "") +
            (type.AirDefence > 0 ? " · " + Loc.Format("air defence {0:0.#}", type.AirDefence) : "") +
            (missions.Length > 0 ? "\n" + Loc.Format("Missions: {0}", missions) : "");
        var cost = SupportCore.CostOf(player, type);
        var days = SupportCore.BuildDaysOf(player, type);
        var refusal = SupportCore.Validate(_campaign, player.Id, SupportRaiseRegion(), type.Id);
        _supportRaiseButton.Disabled = refusal.Length > 0;
        _supportCostReadout.Text = Loc.Format("{0} · {1} · {2} days", Fmt.Money(cost), Fmt.People(type.Personnel), days) +
                                   (refusal.Length > 0 ? " · " + Tr(refusal) : "");
        _supportCostReadout.AddThemeColorOverride("font_color", Ui.Colour(refusal.Length == 0 ? "text.accent_dim" : "state.negative"));
    }

    private void RaiseSupportFormation()
    {
        if (_campaign?.Player is null) return;
        var command = new RaiseSupportCommand
        {
            IssuerId = _campaign.Player.Id,
            StateMapIndex = SupportRaiseRegion(),
            TypeId = _supportRaiseType
        };
        // Validate before the wire: a refusal the player can read now beats one that arrives
        // with the next campaign step.
        var refusal = SupportCore.Validate(_campaign, command.IssuerId, command.StateMapIndex, command.TypeId);
        _supportFeedback.AddThemeColorOverride("font_color", Ui.Colour("state.negative"));
        _supportFeedback.Text = refusal.Length > 0 ? Tr(refusal) : "";
        if (refusal.Length > 0) return;
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("support_order");
        RefreshSupportScreen();
    }
}
