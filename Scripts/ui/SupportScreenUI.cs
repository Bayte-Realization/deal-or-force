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
// its arm, the selected ones filled.
//
// Since 17 November 2026 the panel is the roster, the raising and the missions, and nothing
// else: formations are selected on the map the way divisions are - a click on the card, a box,
// Shift to add, Ctrl to take away, Esc or a click on empty ground to let go - with or without the
// panel, and the right button sends them to any province they may stand in, by road, rail, sea or
// air (LongRangeMovement). The panel's list and the map's selection are one set,
// `_supportSelected`. With only formations selected and the panel closed, their own bar stands
// where the army bar stands.
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
    private Button _supportHaltButton = null!;
    private Label _supportRebaseBanner = null!;
    // The formations' own command bar, in the army bar's place, for a selection made on the map
    // with the panel closed: what they are doing, their missions, halt, show, the panel.
    private PanelContainer _formationBar = null!;
    private Label _formationBarTitle = null!;
    private VBoxContainer _formationBarRows = null!;
    private GridContainer _formationBarMissions = null!;
    private Label _formationBarFeedback = null!;
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
    // The deep-strike target bar (LongRangeStrikes): shown when the selection holds anything that
    // can strike. A kind of target is one click; a region is named by clicking it on the map.
    private VBoxContainer _supportTargetBar = null!;
    private HBoxContainer _supportTargetKinds = null!;
    private Button _supportTargetPick = null!;
    private Button _supportTargetClear = null!;
    private Label _supportTargetReadout = null!;
    private bool _supportTargetArmed;
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
        _supportLayer = FullLayer("Support"); _supportLayer.Visible = false; _supportLayer.MouseFilter = MouseFilterEnum.Ignore;
        _hudLayer.AddChild(_supportLayer); TouchScrollController.EnsureInstalled(this);
        Resized += () => { if (_supportLayer.Visible) RefreshSupportScreen(); };

        var panel = TexturedPanel(_supportLayer, PanelSurface.Command); panel.Name = "SupportForcesPanel";
        Ui.Place(panel, "hud.support_panel");
        var rows = PanelRows(panel, 16, 12, 7);

        ScreenHeader(rows, "LONG-RANGE FORCES", "Aircraft, drones, long-range artillery and air-defence squadrons.",
            false, HandbookButton("long_range"), ActionButton("×", CloseSupportScreen, 34, 30));

        _supportSummary = Title("", 12, Ui.Colour("text.body_soft")); _supportSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart; _supportSummary.CustomMinimumSize = new Vector2(1, 18);
        rows.AddChild(_supportSummary);
        var help = Title(Platform.PlatformProfile.Current?.IsTouchPrimary == true
            ? Tr("On the map: tap a formation to select · long-press or use Order mode to move")
            : Tr("On the map: click selects · Shift adds · drag a box · right-click moves them there · Shift+right-click adds a waypoint · Esc lets go"), 11, Ui.Colour("text.caption"));
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart; help.CustomMinimumSize = new Vector2(1, 0);
        rows.AddChild(help);

        // What arm of the forces the list shows.
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 4);
        rows.AddChild(filters);
        foreach (var (id, label, _) in SupportFilters)
        {
            var captured = id;
            var chip = ActionButton(label, () => SetSupportFilter(captured), 0, 28);
            chip.SetMeta("atlas_tab_button", true);
            chip.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            chip.ToggleMode = true;
            StyleButton(chip, false);
            filters.AddChild(chip);
            _supportFilterButtons[id] = chip;
        }

        _supportRebaseBanner = Title("", 12, Ui.Colour("text.accent"));
        _supportRebaseBanner.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportRebaseBanner.CustomMinimumSize = new Vector2(1, 18);
        _supportRebaseBanner.Visible = false;
        rows.AddChild(_supportRebaseBanner);

        var supportFrame = new PanelContainer
        {
            Name = "SupportRosterFrame",
            CustomMinimumSize = new Vector2(0, 180),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        supportFrame.SetMeta("atlas_section_ground", "surface.inset");
        supportFrame.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(supportFrame, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        rows.AddChild(supportFrame);
        _supportScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true
        };
        supportFrame.AddChild(_supportScroll);
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
        _supportHaltButton = ActionButton("HALT", HaltSupportSelection, 74, 26);
        Tip(_supportHaltButton, Tr("Stop where they are: a column on the road finishes the step it is making and deploys there. To move them, right-click a province on the map."));
        selectionHeader.AddChild(_supportHaltButton);
        selectionHeader.AddChild(ActionButton("CLEAR", ClearSupportSelection, 64, 26));
        _supportSelectionMissions = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportSelectionMissions.AddThemeConstantOverride("separation", 4);
        selection.AddChild(_supportSelectionMissions);
        BuildSupportTargetBar(selection);
        rows.MoveChild(selection, supportFrame.GetIndex());

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
        BuildFormationBar();
    }

    /// <summary>
    /// The bar a map selection of formations gets with the panel closed, in the army bar's place:
    /// a selection made the way divisions are selected is commanded where divisions are.
    /// </summary>
    private void BuildFormationBar()
    {
        _formationBar = TexturedPanel(_hudLayer, PanelSurface.Command);
        _formationBar.Name = "LongRangeSelectionBar";
        Ui.Place(_formationBar, "hud.army_panel");
        _formationBar.Visible = false;
        var shell = PanelRows(_formationBar, 22, 16, 6);
        shell.ClipContents = true;
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 6);
        shell.AddChild(header);
        _formationBarTitle = Title("", (int)Ui.Metric("ui.font.heading", 16), Ui.Colour("text.accent"));
        _formationBarTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _formationBarTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        _formationBarTitle.ClipText = true;
        header.AddChild(_formationBarTitle);
        var show = ActionButton("SHOW", FocusSupportSelection, 70, 26);
        Tip(show, Tr("Centre the map on the selected formations and their reach."));
        header.AddChild(show);
        var halt = ActionButton("HALT", HaltSupportSelection, 70, 26);
        Tip(halt, Tr("Stop where they are: a column on the road finishes the step it is making and deploys there. To move them, right-click a province on the map."));
        header.AddChild(halt);
        var panel = ActionButton("PANEL", OpenSupportScreen, 74, 26);
        Tip(panel, Tr("Open the long-range forces panel: the whole roster, raising, and strike targets."));
        header.AddChild(panel);
        header.AddChild(ActionButton("×", ClearSupportMapSelection, 34, 26));
        var help = Title(Platform.PlatformProfile.Current?.IsTouchPrimary == true
            ? "Long-press or Order mode: move there · tap ground: clear"
            : "Right-click: move there · Shift+right-click: add a waypoint · Shift+click: add · Esc: let go",
            11, Ui.Colour("text.caption"));
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        help.CustomMinimumSize = new Vector2(1, 0);
        shell.AddChild(help);
        _formationBarRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _formationBarRows.AddThemeConstantOverride("separation", 2);
        shell.AddChild(_formationBarRows);
        _formationBarMissions = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _formationBarMissions.AddThemeConstantOverride("h_separation", 4);
        _formationBarMissions.AddThemeConstantOverride("v_separation", 4);
        shell.AddChild(_formationBarMissions);
        _formationBarFeedback = Title("", 11, Ui.Colour("text.body_soft"));
        _formationBarFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _formationBarFeedback.CustomMinimumSize = new Vector2(1, 0);
        shell.AddChild(_formationBarFeedback);
    }

    /// <summary>Shown only for a selection of formations alone, with the panel closed: armies keep their own bar.</summary>
    private void RefreshFormationBar()
    {
        if (_formationBar is null) return;
        if (ObserverUiRules.IsObserving(_campaign, _observerMode) || _campaign?.Player is not { } player)
        {
            _formationBar.Visible = false;
            return;
        }
        var selected = _campaign.SupportFormations.Where(f => f.NationId == player.Id && _supportSelected.Contains(f.Id) &&
                                                              SupportCore.Enabled(_campaign, f.TypeId)).OrderBy(f => f.Id).ToList();
        var show = selected.Count > 0 && _hudLayer.Visible && _supportLayer is not { Visible: true } &&
                   _selectedArmies.Count == 0 && _selectedArmy is null;
        _formationBar.Visible = show;
        if (!show) return;
        _formationBarTitle.Text = Loc.Format("LONG-RANGE FORCES · {0} selected", selected.Count);
        var shown = selected.Take(8).ToList();
        while (_formationBarRows.GetChildCount() < shown.Count + (selected.Count > shown.Count ? 1 : 0))
        {
            var line = Title("", 11, Ui.Colour("text.bright"));
            line.AutowrapMode = TextServer.AutowrapMode.Off;
            line.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            line.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _formationBarRows.AddChild(line);
        }
        var labels = _formationBarRows.GetChildren().OfType<Label>().ToList();
        for (var index = 0; index < labels.Count; index++)
        {
            labels[index].Visible = index < shown.Count || index == shown.Count && selected.Count > shown.Count;
            if (index < shown.Count)
            {
                var formation = shown[index];
                var type = SupportTypeCatalog.Get(formation.TypeId);
                labels[index].Text = $"{Loc.Get(type?.Name ?? formation.TypeId)} · {formation.Strength:P0} · " +
                                     SupportPositionStatus(formation);
            }
            else if (index == shown.Count) labels[index].Text = Loc.Format("and {0} more", selected.Count - shown.Count);
        }
        var missions = new List<string>();
        foreach (var formation in selected)
            if (SupportTypeCatalog.TryGet(formation.TypeId, out var type))
                foreach (var mission in type.Missions)
                    if (!missions.Contains(mission)) missions.Add(mission);
        var existing = _formationBarMissions.GetChildren().OfType<Button>().Select(b => b.GetMeta("mission", "").AsString()).ToList();
        if (!existing.SequenceEqual(missions))
        {
            foreach (var child in _formationBarMissions.GetChildren()) { _formationBarMissions.RemoveChild(child); child.QueueFree(); }
            foreach (var mission in missions)
            {
                var captured = mission;
                var button = ActionButton(SupportMissionShort(mission), () => SetSupportMission(_supportSelected.ToArray(), captured), 0, 26);
                button.SetMeta("mission", mission);
                button.ToggleMode = true;
                button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                button.ClipText = true;
                button.TooltipText = Ui.Tip(SupportMissionHelp(mission) + "\n" + Tr("Given to every selected formation that can fly it."));
                _formationBarMissions.AddChild(button);
            }
        }
        foreach (var button in _formationBarMissions.GetChildren().OfType<Button>())
        {
            var mission = button.GetMeta("mission", "").AsString();
            var able = selected.Where(f => SupportTypeCatalog.TryGet(f.TypeId, out var type) && type.Missions.Contains(mission)).ToList();
            button.SetPressedNoSignal(able.Count > 0 && able.All(f => f.Mission == mission));
        }
    }

    /// <summary>Where a formation is in its day: working, on the road with the days left, deploying, cut off.</summary>
    private string SupportPositionStatus(SupportFormation formation)
    {
        if (formation.CutOff) return Tr("CUT OFF — no way out");
        if (formation.Route.Count > 0)
        {
            var days = LongRangeMovement.DaysLeft(_campaign, formation);
            return Loc.Format("en route → {0}, {1} d", SupportPlaceName(LongRangeMovement.Destination(formation)), days) +
                   (formation.Overrun ? " · " + Tr("withdrawing") : "");
        }
        if (formation.DeployDaysLeft > 0) return Loc.Format("deploying, {0} d", formation.DeployDaysLeft);
        if (SupportCore.Displaced(_campaign, formation)) return Tr("cannot work from here");
        return Tr(SupportMissionShort(formation.Mission));
    }

    /// <summary>The name a province goes by: its town if it has one, else its region.</summary>
    private string SupportPlaceName(int province)
    {
        var map = _campaign?.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return "";
        var town = CityCatalog.NearestTown(map, province);
        if (town is not null) return Loc.Get(town.Name);
        return _campaign.StateByMapIndex(map.ProvinceToState[province])?.DisplayName ?? "";
    }

    // ------------------------------------------------------------------ open, close, refresh

    private void OpenSupportScreen()
    {
        if (_campaign?.Player is null || ObserverUiRules.IsObserving(_campaign, _observerMode) ||
            !SupportTypeCatalog.All.Any(t => SupportCore.Enabled(_campaign, t)))
            return;
        EnsureScreen("support", BuildSupportScreen);
        EndDrawing();
        CloseFleetPanel();
        ClearArmySelectionAndClose();
        _openScreen = "Support";
        _sidePanel.Visible = false;
        HideMapSelection(true);
        RaiseWorkspaceLayer(_supportLayer);
        _globe.SupportOverlay = true;
        _globe.SupportCommandMode = true;
        _globe.SelectSupportFormations(_supportSelected);
        RefreshSupportScreen();
        UpdateScreenButtons();
        RefreshNewShell(force: true);
        // The selection carries into the panel; its bar on the map gives way to it.
        RefreshFormationBar();
    }

    private void CloseSupportScreen()
    {
        if (_supportLayer is not null) _supportLayer.Visible = false;
        _supportTargetArmed = false;
        _globe.SupportOverlay = false;
        _globe.SupportCommandMode = false;
        _globe.SupportRebaseArmed = false;
        if (_openScreen == "Support") _openScreen = "";
        UpdateScreenButtons();
        RefreshNewShell(force: true);
        // The selection outlives the panel, the way a division's outlives its roster.
        RefreshFormationBar();
    }

    private void OnSupportPicked(SupportFormation formation)
    {
        if (_campaign?.Player is null || formation.NationId != _campaign.Player.Id || !_hudLayer.Visible) return;
        CancelConstructionPlacement(); CloseFleetPanel();
        var (adding, removing) = (AddingToSelection, RemovingFromSelection);
        if (!adding && !removing) DropArmySelection();
        MapSelection.Apply(_supportSelected, new[] { formation.Id }, adding, removing);
        if (!removing) _supportSelectionAnchor = formation.Id;
        var openPanel = _supportSelected.Count > 0 && !(adding && (_selectedArmies.Count > 0 || _selectedArmy is not null));
        if (openPanel && _supportLayer is not { Visible: true }) OpenSupportScreen();
        else { if (_supportSelected.Count > 0 && _formationBar is null) EnsureScreen("support", BuildSupportScreen); SupportSelectionChanged(); }
        if (_supportLayer is { Visible: true } && _supportRowMap.TryGetValue(formation.Id, out var row)) _supportScroll.EnsureControlVisible(row.Row);
    }

    private void DropArmySelection()
    {
        if (_selectedArmy is null && _selectedArmies.Count == 0) return;
        _selectedArmy = null; _selectedArmies.Clear(); _globe.SelectArmy(-1); SetArmyPanelVisible(false); _redeploymentArmed = false;
    }

    private void ClearSupportMapSelection()
    {
        _supportSelected.Clear(); _supportTargetArmed = false; _globe.SupportRebaseArmed = false;
        _globe.SelectSupportFormations(_supportSelected);
        if (_supportFeedback is not null) _supportFeedback.Text = "";
        if (_supportLayer is { Visible: true }) RefreshSupportScreen();
        RefreshFormationBar();
    }

    /// <summary>
    /// A left click on the map's ground. With a strike target being named it is the target. A
    /// click on empty ground lets the selected formations go, as it lets divisions go; with the
    /// panel open and raising unfolded, a region of our own is where the next one is raised. True
    /// when the click was the panel's, so the country card does not open over the panel.
    /// </summary>
    private bool TrySupportMapPick(GlobePick pick)
    {
        if (_campaign?.Player is null) return false;
        var player = _campaign.Player;
        if (_supportTargetArmed && _supportLayer is { Visible: true } && pick.State is not null)
        {
            AimSupportSelection(pick.State);
            return true;
        }
        var hadSelection = _supportSelected.Count > 0;
        if (hadSelection) ClearSupportMapSelection();
        if (_supportLayer is not { Visible: true }) return false;
        if (_supportRaiseOpen && pick.State is not null && pick.State.NationMapIndex == player.MapIndex)
        {
            SelectSupportRaiseRegion(pick.State.MapIndex);
            return true;
        }
        return hadSelection;
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
            (hurt > 0 ? " · " + Loc.Format("{0} took losses yesterday", hurt) : "") +
            SupportStrikeSummary(player);

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
        // Formations on the road are a section of their own at the top: where they are this hour is
        // not where they belong, and a column is what a player looks for first.
        const int onTheMove = -2;
        var byState = shown.GroupBy(f => f.Route.Count > 0 ? onTheMove : f.StateMapIndex)
            .Select(group => (State: _campaign.States.Find(s => s.MapIndex == group.Key) ??
                new ScenarioState { MapIndex = group.Key, Name = Tr(group.Key == onTheMove ? "On the move" : "Awaiting a safe base") }, Formations: group.ToList()))
            .OrderByDescending(entry => entry.State.MapIndex == onTheMove)
            .ThenByDescending(entry => entry.State.NationMapIndex == player.MapIndex)
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
            var lost = state.MapIndex >= 0 && formations.All(f => f.CutOff);
            section.Caption.Disabled = state.MapIndex < 0;
            var used = state.MapIndex >= 0 ? SupportCore.BasingUsed(_campaign, state.MapIndex) : 0f;
            var capacity = state.MapIndex >= 0 ? SupportCore.CapacityOf(_campaign, state) : 0;
            var crowded = used > capacity;
            section.Caption.Text = state.MapIndex < 0 ? state.DisplayName : $"{state.DisplayName} · " + (lost
                ? Tr("CUT OFF")
                : Loc.Format("used {0:0.#} of {1}", used, capacity) +
                  (crowded ? " · " + Loc.Format("crowded: readiness recovers at {0:P0}", SupportCore.CrowdingOf(capacity, used)) : ""));
            section.Caption.TooltipText = Ui.Tip(Tr("Show this base on the map.") + "\n" +
                Tr("Basing: a battery takes 1, an air wing 2, a bomber regiment 3. A region holds 3, 2 more per barracks and 2 per leading city its owner holds. Over that is allowed - the formations there recover readiness more slowly."));
            section.Caption.AddThemeColorOverride("font_color", Ui.Colour(lost || crowded ? "state.negative" : "text.caption"));
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
        section.Caption.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button", 12));
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
            ActionMode = BaseButton.ActionModeEnum.Release
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

        // Where it is in its day, in words: on the road with the days left, deploying, cut off.
        row.Displaced.Text = formation.CutOff || formation.Route.Count > 0 || formation.DeployDaysLeft > 0 ||
                             SupportCore.Displaced(_campaign, formation)
            ? SupportPositionStatus(formation) : "";

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
            missionStatus.Text = Tr(SupportMissionShort(formation.Mission)) +
                (formation.Mission == SupportCore.MissionStrike ? SupportStrikeStatus(formation) : "");
            missionStatus.AddThemeColorOverride("font_color", Ui.Colour(
                formation.Mission == SupportCore.MissionStandby ? "text.caption" : "text.accent"));
        }

        row.Row.TooltipText = Ui.Tip(
            $"{Loc.Get(type.Name)}\n" +
            Loc.Format("reach {0} km from {1}", MathF.Round(SupportCore.ReachKm(_campaign, type)), SupportPlaceName(LongRangeMovement.At(formation))) + "\n" +
            Loc.Format("fire {0:0.#} · air superiority {1:0.#} · interdiction {2:0.#} · air defence {3:0.#} · recon {4:0.#}",
                type.SupportFire, type.AirSuperiority, type.Interdiction, type.AirDefence, type.Recon) + "\n" +
            Loc.Format("experience {0:P0}", formation.Experience) + "\n" +
            SupportMissionHelp(formation.Mission) + "\n" +
            Tr("Click selects one. Ctrl toggles; Shift selects a range. Right-click a province on the map to send them there."));
    }

    /// <summary>A mission's player-facing name, shared by the roster and command bar.</summary>
    private static string SupportMissionShort(string mission) => mission switch
    {
        SupportCore.MissionStandby => "Standby / recover",
        SupportCore.MissionCloseAirSupport => "Fire support",
        SupportCore.MissionAirSuperiority => "Air superiority",
        SupportCore.MissionInterdiction => "Interdiction",
        AirReconnaissance.Mission => "Reconnaissance",
        SupportCore.MissionStrike => "Deep strike",
        _ => mission
    };

    private static string SupportMissionHelp(string mission) => mission switch
    {
        SupportCore.MissionStandby => "Rest: fly nothing, repair and recover twice as fast. Missile brigades defend their ground whatever they are told.",
        SupportCore.MissionCloseAirSupport => "Support: add fire to every battle of ours inside the reach.",
        SupportCore.MissionAirSuperiority => "Sky: sweep the enemy's aircraft from over our battles - their support fire suffers, and our own aircraft lose less.",
        SupportCore.MissionInterdiction => "Strike: hit what is behind the enemy's line, stripping the defence of every battle inside the reach.",
        AirReconnaissance.Mission => "Look: photograph the enemy's ground inside the reach - the fog lifts there, as far as the formation's sensors see. The enemy's fighters and missiles still take their toll.",
        SupportCore.MissionStrike => "Strike deep: one salvo a week at a target behind the line - the enemy's formations, depots, plants or power grid. Fired from the depot of missiles, drones or shells; the enemy's air defence shoots part of it down, and reconnaissance over the target makes the rest land.",
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
            DropArmySelection();
            var b = visible.IndexOf(id);
            if (!ctrl) _supportSelected.Clear();
            if (a >= 0 && b >= 0)
                foreach (var selected in visible.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
                    _supportSelected.Add(selected);
            else _supportSelected.Add(id);
        }
        else
        {
            if (!ctrl) { _supportSelected.Clear(); DropArmySelection(); }
            if (!_supportSelected.Add(id)) _supportSelected.Remove(id);
            _supportSelectionAnchor = id;
        }
        SupportSelectionChanged();
    }

    /// <summary>
    /// A box drawn on the map: divisions and formations together, by the divisions' rule - a plain
    /// box replaces the selection, Shift adds to it, Ctrl takes away from it.
    /// </summary>
    private void OnSelectionBoxPicked(IReadOnlyList<Army> armies, List<SupportFormation> formations)
    {
        if (!_hudLayer.Visible || _campaign?.Player is null) return;
        var own = formations.Where(f => f.NationId == _campaign.Player.Id).Select(f => f.Id).ToList();
        var divisions = armies.Where(army => ArmyInteraction.CanSelect(_campaign, army)).ToList();
        if (own.Count == 0 && divisions.Count == 0) return;
        var removing = RemovingFromSelection;
        var adding = AddingToSelection;
        MapSelection.Apply(_supportSelected, own, adding, removing);
        _globe.SelectSupportFormations(_supportSelected);
        if (divisions.Count > 0) PickArmies(divisions, formationsTaken: true);
        else if (!adding && !removing) DropArmySelection();
        SupportSelectionChanged();
    }

    private void OnSupportBoxPicked(List<SupportFormation> formations) => OnSelectionBoxPicked(Array.Empty<Army>(), formations);

    /// <summary>Ctrl+X on the long-range screen: keep every other selected formation, by id.</summary>
    private bool SplitSupportSelection()
    {
        if (_supportLayer is not { Visible: true } || _supportSelected.Count < 2) return false;
        var ordered = _supportSelected.OrderBy(id => id).ToList();
        var left = ordered.Count / 2;
        for (var index = 1; index < ordered.Count; index += 2) _supportSelected.Remove(ordered[index]);
        SupportSelectionChanged();
        _supportFeedback.Text = Loc.Format("Split in half: {0} selected, {1} left as they were.", _supportSelected.Count, left);
        return true;
    }

    private void ClearSupportSelection() => ClearSupportMapSelection();

    private void SupportSelectionChanged()
    {
        _globe.SelectSupportFormations(_supportSelected);
        if (_supportFeedback is not null) _supportFeedback.Text = "";
        RefreshSupportScreen();
        RefreshFormationBar();
    }

    private void RefreshSupportSelection(List<SupportFormation> owned)
    {
        var selected = owned.Where(f => _supportSelected.Contains(f.Id)).ToList();
        // On short screens recruitment and orders are separate tasks. Preserve selection,
        // but give the roster room instead of crushing it between two command surfaces.
        _supportSelectionBar.Visible = selected.Count > 0 &&
            (!_supportRaiseOpen || Size.Y >= Ui.Metric("support.compact_height", 840));
        if (selected.Count == 0) _supportTargetArmed = false;
        // While a strike target is being named the map's clicks are the panel's, not the cards'.
        _globe.SupportRebaseArmed = _supportTargetArmed;
        _supportRebaseBanner.Visible = _supportTargetArmed;
        _supportRebaseBanner.Text = Tr("Click a region of a country we are at war with: the selected strike formations aim there. Esc cancels.");
        RefreshSupportTargetBar(selected);
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
        if (_formationBarFeedback is not null) _formationBarFeedback.Text = _supportFeedback.Text;
        if (applied > 0) PlayStrategicCue("support_mission");
        _globe.SelectSupportFormations(_supportSelected);
        RefreshSupportScreen();
        RefreshFormationBar();
    }

    private void FocusSupportSelection()
    {
        var first = _campaign?.SupportFormations.FirstOrDefault(f => _supportSelected.Contains(f.Id) && LongRangeMovement.At(f) > 0);
        if (first is null || _campaign.ProvinceMap is null) return;
        // The middle of where the selection stands - on the road too - far enough out to see their reach.
        var bases = _campaign.SupportFormations.Where(f => _supportSelected.Contains(f.Id) && LongRangeMovement.At(f) > 0)
            .Select(f => LongRangeMovement.Position(_campaign, f)).ToList();
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
        var formation = _campaign?.SupportFormations.FirstOrDefault(f =>
            (stateMapIndex == -2 ? f.Route.Count > 0 : f.StateMapIndex == stateMapIndex) && LongRangeMovement.At(f) > 0);
        if (formation is null || _campaign.ProvinceMap is null) return;
        var centre = LongRangeMovement.Position(_campaign, formation);
        _globe.FocusOn(centre.Latitude, centre.Longitude, 8.2f);
    }

    private void CancelSupportRebase()
    {
        _supportTargetArmed = false;
        _supportFeedback.Text = "";
        RefreshSupportScreen();
    }

    /// <summary>
    /// The right button with formations selected: each goes to that province its own way, or says
    /// why it cannot. Shift makes the province a waypoint after the route each already has.
    /// </summary>
    private void RelocateSupportSelection(int province, bool append)
    {
        var player = _campaign?.Player;
        if (player is null || province <= 0) return;
        LongRangeMovement.KeepCommandable(_campaign, player.Id, _supportSelected);
        var ids = _supportSelected.OrderBy(id => id).ToArray();
        if (ids.Length == 0) return;
        // The same answer the command will get, read first for the words: the command's own refusal
        // is English for the log and the replay, this is for the player.
        var previews = LongRangeMovement.PreviewRelocate(_campaign, player.Id, ids, province, append);
        var command = new RelocateSupportCommand { IssuerId = player.Id, FormationIds = ids, ProvinceId = province, Append = append };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        var place = SupportPlaceName(province);
        var lines = new List<string>();
        var going = previews.Where(p => p.Accepted).ToList();
        if (command.Moved > 0)
            lines.Add(Loc.Format("{0} formation(s) on the way to {1}: {2} d.", command.Moved, place,
                going.Count == 0 ? 0 : going.Max(p => p.Days)));
        else if (going.Count > 0)
            lines.Add(Loc.Format("The selected formations are already at {0}.", place));
        foreach (var refused in previews.Where(p => !p.Accepted).Take(4))
        {
            var formation = _campaign.SupportFormations.Find(f => f.Id == refused.FormationId);
            var name = formation is not null ? Loc.Get(SupportTypeCatalog.Get(formation.TypeId)?.Name ?? formation.TypeId) : "#" + refused.FormationId;
            lines.Add(name + ": " + (refused.RefusalArgs.Length == 0 ? Tr(refused.Refusal) : Loc.Format(refused.Refusal, refused.RefusalArgs)));
        }
        var text = string.Join("\n", lines);
        var ok = command.Moved > 0 || previews.All(p => p.Accepted);
        foreach (var label in new[] { _supportFeedback, _formationBarFeedback })
        {
            if (label is null) continue;
            label.AddThemeColorOverride("font_color", Ui.Colour(ok ? "state.positive" : "state.negative"));
            label.Text = text;
        }
        if (command.Moved > 0) PlayStrategicCue("support_order");
        _globe.SelectSupportFormations(_supportSelected);
        if (_supportLayer is { Visible: true }) RefreshSupportScreen();
        RefreshFormationBar();
        if (_supportFeedback is not null) _supportFeedback.Text = text;
        if (_formationBarFeedback is not null) _formationBarFeedback.Text = text;
    }

    /// <summary>
    /// Stop where they are: one halt order for the selection. A column part-way along a step
    /// finishes it and deploys at its end - the simulation works that out when the order lands
    /// (LongRangeMovement.PreviewHalt). Sending each to the province it last reached, as this
    /// once did, turned a ship six days out round at the far port and sailed it home.
    /// </summary>
    private void HaltSupportSelection()
    {
        var player = _campaign?.Player;
        if (player is null) return;
        LongRangeMovement.KeepCommandable(_campaign, player.Id, _supportSelected);
        var ids = _supportSelected.OrderBy(id => id).ToArray();
        var halted = 0;
        if (ids.Length > 0)
        {
            var command = new RelocateSupportCommand { IssuerId = player.Id, FormationIds = ids, Halt = true };
            _campaign.Commands.Enqueue(command);
            _campaign.Commands.ApplyAll(_campaign);
            halted = command.Moved;
        }
        var text = Loc.Format("{0} formation(s) halted.", halted);
        if (_supportFeedback is not null) _supportFeedback.Text = text;
        if (_formationBarFeedback is not null) _formationBarFeedback.Text = text;
        if (_supportLayer is { Visible: true }) RefreshSupportScreen();
        RefreshFormationBar();
        _globe.SelectSupportFormations(_supportSelected);
    }

    // ------------------------------------------------------------------ in training

    private void RefreshSupportTraining(List<SupportOrder> training)
    {
        _supportTrainingCaption.Text = Loc.Format("IN TRAINING · {0} of {1}", training.Count, Math.Max(1, _campaign.Defines.SupportTrainingSlots));
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
        var playerId = _campaign.Player.Id;
        _simRunner.RunWrite(world =>
        {
            world.Commands.Enqueue(new CancelSupportOrderCommand { IssuerId = playerId, OrderId = orderId });
            world.Commands.ApplyAll(world);
        }, refreshSnapshot: true);
        _supportFeedback.Text = "";
        Refresh();
        RefreshSupportScreen();
        RefreshNewShell(force: true);
    }

    // ------------------------------------------------------------------ raising

    private void ToggleSupportRaise()
    {
        _supportRaiseOpen = !_supportRaiseOpen;
        _supportRaiseScroll.Visible = _supportRaiseOpen;
        _supportRaiseBody.Visible = _supportRaiseOpen;
        _supportRaiseToggle.Text = Tr(_supportRaiseOpen ? "▾ RAISE A FORMATION" : "▸ RAISE A FORMATION");
        RefreshSupportScreen();
    }

    private void RefreshSupportRaise(Nation player)
    {
        if (InterfaceInteractionGuard.Busy(_supportLayer)) { RefreshSupportRaiseReadout(); return; }
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
                button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button_small", 11));
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
            _supportRegionPicker.AddItem(Loc.Format("{0} · used {1:0.#} of {2}", state.DisplayName,
                SupportCore.BasingUsed(_campaign, state.MapIndex), SupportCore.CapacityOf(_campaign, state)));
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
        _simRunner.RunWrite(world =>
        {
            world.Commands.Enqueue(command);
            world.Commands.ApplyAll(world);
        }, refreshSnapshot: true);
        PlayStrategicCue("support_order");
        Refresh();
        RefreshSupportScreen();
        RefreshNewShell(force: true);
    }
    // ------------------------------------------------------------------ deep strike

    /// <summary>The kinds of target, in the order of the bar, with their player-facing names.</summary>
    private static readonly (string Id, string Label, string Help)[] SupportTargetKinds =
    {
        ("", "Auto", "The staff picks by weapon: rockets and glide bombs at the line, missiles at the depots, long-range drones at the grid."),
        (LongRangeStrikes.TargetFront, "Front", "The enemy's formations in the line: organisation and equipment out of the strongest stack in reach."),
        (LongRangeStrikes.TargetLogistics, "Logistics", "Depots and railheads: the enemy's ammunition, fuel and arms burn, and the region that feeds the most of their divisions carries less."),
        (LongRangeStrikes.TargetIndustry, "Industry", "Arms plants and refineries: war damage to the enemy's most industrial region in reach, and with it what it makes."),
        (LongRangeStrikes.TargetEnergy, "Energy", "The power grid: war damage and dark winters in the enemy's most populous region in reach, and the country's stability with them.")
    };

    private void BuildSupportTargetBar(VBoxContainer selection)
    {
        _supportTargetBar = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
        _supportTargetBar.AddThemeConstantOverride("separation", 4);
        selection.AddChild(_supportTargetBar);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 6);
        _supportTargetBar.AddChild(header);
        var caption = Title(Tr("STRIKE TARGET"), 11, Ui.Colour("text.caption"));
        caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        caption.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(caption);
        _supportTargetPick = ActionButton("PICK ON MAP", ToggleSupportTargetPick, 110, 24);
        _supportTargetPick.ToggleMode = true;
        Tip(_supportTargetPick, Tr("Then click a region of a country we are at war with: the selected strike formations aim there, if it is in reach."));
        header.AddChild(_supportTargetPick);
        _supportTargetClear = ActionButton("ANY REGION", ClearSupportTargetRegion, 96, 24);
        Tip(_supportTargetClear, Tr("Forget the named region: the staff finds the best target of the chosen kind in reach."));
        header.AddChild(_supportTargetClear);

        _supportTargetKinds = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportTargetKinds.AddThemeConstantOverride("separation", 3);
        _supportTargetBar.AddChild(_supportTargetKinds);
        foreach (var (id, label, help) in SupportTargetKinds)
        {
            var captured = id;
            var chip = ActionButton(label, () => SetSupportStrikeTarget(captured, null), 0, 24);
            chip.SetMeta("target", id);
            chip.ToggleMode = true;
            chip.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            chip.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button_small", 11));
            chip.TooltipText = Ui.Tip(Tr(help));
            _supportTargetKinds.AddChild(chip);
        }

        _supportTargetReadout = Title("", 11, Ui.Colour("text.body_soft"));
        _supportTargetReadout.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportTargetReadout.CustomMinimumSize = new Vector2(1, 0);
        _supportTargetBar.AddChild(_supportTargetReadout);
    }

    private static bool CanStrike(SupportFormation formation) =>
        SupportTypeCatalog.TryGet(formation.TypeId, out var type) && type.Strike > 0f &&
        type.Missions.Contains(SupportCore.MissionStrike);

    /// <summary>
    /// The bar under the missions: which kind of target the selected strike formations aim at,
    /// the region they were told to hit, and - for the first of them - what the staff will
    /// actually strike next and the odds over it, so the choice is made looking at the price.
    /// </summary>
    private void RefreshSupportTargetBar(List<SupportFormation> selected)
    {
        var strikers = selected.Where(CanStrike).ToList();
        _supportTargetBar.Visible = strikers.Count > 0;
        if (strikers.Count == 0) { _supportTargetArmed = false; return; }
        _supportTargetPick.SetPressedNoSignal(_supportTargetArmed);
        foreach (var chip in _supportTargetKinds.GetChildren().OfType<Button>())
        {
            var id = chip.GetMeta("target", "").AsString();
            chip.SetPressedNoSignal(strikers.All(f => (f.StrikeTarget ?? "") == id));
        }
        var lead = strikers.FirstOrDefault(f => f.Mission == SupportCore.MissionStrike) ?? strikers[0];
        var named = lead.TargetStateMapIndex >= 0 ? _campaign.States.Find(s => s.MapIndex == lead.TargetStateMapIndex) : null;
        _supportTargetClear.Disabled = strikers.All(f => f.TargetStateMapIndex < 0);
        var text = named is not null ? Loc.Format("Named region: {0}.", named.DisplayName) + " " : "";
        var plan = LongRangeStrikes.Preview(_campaign, lead);
        if (plan.Province <= 0)
            text += Tr(plan.Refusal.Length > 0 ? plan.Refusal : "Nothing of the enemy's is in reach.");
        else
        {
            var region = _campaign.States.Find(s => s.MapIndex == plan.StateMapIndex)?.DisplayName ?? "";
            var sky = plan.AirControl < 0f ? Tr("nobody flies there")
                : plan.AirControl >= 0.65f ? Tr("our sky")
                : plan.AirControl <= 0.35f ? Tr("the enemy's sky") : Tr("contested sky");
            text += Loc.Format("Next salvo: {0} · {1} · {2:0} km", Tr(SupportTargetName(plan.Target)), region, plan.DistanceKm) + "\n" +
                    Loc.Format("{0} · about {1:P0} shot down · precision {2:P0}", sky, plan.InterceptShare, plan.Precision) +
                    (plan.Spotted ? "" : " · " + Tr("no reconnaissance over the target"));
            if (plan.Refusal.Length > 0) text += "\n" + Tr(plan.Refusal);
        }
        if (lead.Mission != SupportCore.MissionStrike)
            text += "\n" + Tr("Give the Deep strike mission for the salvos to fly.");
        _supportTargetReadout.Text = text;
    }

    private static string SupportTargetName(string target) => target switch
    {
        LongRangeStrikes.TargetFront => "Front",
        LongRangeStrikes.TargetLogistics => "Logistics",
        LongRangeStrikes.TargetIndustry => "Industry",
        LongRangeStrikes.TargetEnergy => "Energy",
        _ => "Auto"
    };

    /// <summary>The row's strike line: what it aims at, and what its last salvo did.</summary>
    private string SupportStrikeStatus(SupportFormation formation)
    {
        var text = " · " + Tr(SupportTargetName(formation.StrikeTarget ?? ""));
        if (formation.TargetStateMapIndex >= 0 &&
            _campaign.States.Find(s => s.MapIndex == formation.TargetStateMapIndex) is { } named)
            text += " → " + named.DisplayName;
        if (_campaign.SupportStrikeReports.TryGetValue(formation.Id, out var report))
            text += report.Launched > 0f
                ? " · " + Loc.Format("last: {0:0.0} landed, {1:P0} shot down", report.Landed,
                    report.Intercepted / MathF.Max(0.01f, report.Launched))
                : report.Note.Length > 0 ? " · " + Tr(report.Note) : "";
        return text;
    }

    /// <summary>The summary's strike half: the munitions in the depots and the week's salvos both ways.</summary>
    private string SupportStrikeSummary(Nation player)
    {
        if (!_campaign.SupportFormations.Any(f => f.NationId == player.Id && CanStrike(f)) &&
            !_campaign.SupportStrikeReports.Values.Any(r => r.TargetNationId == player.Id)) return "";
        var text = "\n" + Loc.Format("Depots: missiles {0:0.#} · drones {1:0.#} · shells {2:0.#}",
            StrategicStockpiles.Peek(_campaign, player, "Missiles"), StrategicStockpiles.Peek(_campaign, player, "Drones"),
            StrategicStockpiles.Peek(_campaign, player, "Ammunition"));
        var week = _campaign.ElapsedDays - Math.Max(1, _campaign.Defines.StrikeIntervalDays);
        var ours = _campaign.SupportFormations.Where(f => f.NationId == player.Id)
            .Select(f => _campaign.SupportStrikeReports.GetValueOrDefault(f.Id))
            .Where(r => r is not null && r.Day > week && r.Launched > 0f).ToList();
        var theirs = _campaign.SupportStrikeReports.Values.Where(r => r.TargetNationId == player.Id && r.Day > week && r.Launched > 0f).ToList();
        if (ours.Count > 0)
            text += " · " + Loc.Format("our salvos this week {0}, {1:P0} shot down", ours.Count,
                ours.Sum(r => r.Intercepted) / MathF.Max(0.01f, ours.Sum(r => r.Launched)));
        if (theirs.Count > 0)
            text += " · " + Loc.Format("incoming {0}, {1:P0} intercepted", theirs.Count,
                theirs.Sum(r => r.Intercepted) / MathF.Max(0.01f, theirs.Sum(r => r.Launched)));
        return text;
    }

    /// <summary>Give the selected strike formations a kind of target and/or a region (null keeps each one's own).</summary>
    private void SetSupportStrikeTarget(string target, int? stateMapIndex)
    {
        if (_campaign?.Player is null) return;
        var commands = new List<SetSupportStrikeTargetCommand>();
        foreach (var formation in _campaign.SupportFormations.Where(f => _supportSelected.Contains(f.Id) &&
                     f.NationId == _campaign.Player.Id && CanStrike(f)).OrderBy(f => f.Id))
        {
            var command = new SetSupportStrikeTargetCommand
            {
                IssuerId = _campaign.Player.Id, FormationId = formation.Id,
                Target = target ?? formation.StrikeTarget ?? "",
                StateMapIndex = stateMapIndex ?? formation.TargetStateMapIndex
            };
            commands.Add(command);
            _campaign.Commands.Enqueue(command);
        }
        if (commands.Count == 0) return;
        _campaign.Commands.ApplyAll(_campaign);
        var applied = commands.Count(c => c.Refusal.Length == 0);
        _supportFeedback.AddThemeColorOverride("font_color", Ui.Colour(applied > 0 ? "state.positive" : "state.negative"));
        _supportFeedback.Text = applied > 0
            ? Loc.Format("Strike target set for {0} formation(s).", applied)
            : Tr(commands[0].Refusal);
        if (applied > 0) PlayStrategicCue("support_mission");
        RefreshSupportScreen();
    }

    private void ToggleSupportTargetPick()
    {
        _supportTargetArmed = !_supportTargetArmed && _supportSelected.Count > 0;
        _supportFeedback.Text = "";
        RefreshSupportScreen();
    }

    private void ClearSupportTargetRegion()
    {
        _supportTargetArmed = false;
        SetSupportStrikeTarget(null, -1);
    }

    /// <summary>A region clicked while naming a target: an enemy's is taken, anything else explains itself.</summary>
    private void AimSupportSelection(ScenarioState region)
    {
        var player = _campaign.Player;
        var anchor = LongRangeStrikes.AnchorOf(_campaign.ProvinceMap, region.MapIndex);
        var controllers = _campaign.ProvinceController ?? _campaign.ProvinceMap.ProvinceToNation;
        var controller = anchor > 0 && anchor < controllers.Length ? controllers[anchor] : -1;
        var enemy = _campaign.Nations.FirstOrDefault(n => n.MapIndex == controller);
        var atWar = enemy is not null && _campaign.Wars.Any(w => !w.Ended && w.Involves(player.Id) && w.Involves(enemy.Id) &&
                                                               w.Side(true).Contains(player.Id) != w.Side(true).Contains(enemy.Id));
        if (!atWar)
        {
            _supportFeedback.AddThemeColorOverride("font_color", Ui.Colour("state.negative"));
            _supportFeedback.Text = region.DisplayName + " · " + Tr("Choose a region held by a country we are at war with.");
            RefreshSupportScreen();
            return;
        }
        _supportTargetArmed = false;
        SetSupportStrikeTarget(null, region.MapIndex);
    }
}
