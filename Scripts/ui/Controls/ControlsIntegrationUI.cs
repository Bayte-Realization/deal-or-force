using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;
using Godot;

namespace DealOrForce;

/// <summary>
/// Central input ergonomics coordinator for Deal or Force:
/// - Physical-layout hotkeys across QWERTY / Russian JCUKEN via HotkeyCatalog
/// - Hold-Alt paper-and-ink keycap overlay on visible HUD controls
/// - Timeless Ctrl+Z UndoToastUI after Everywhere & Zero-budget mass actions
/// - Control groups Ctrl+6..0 / 6..0 (double-tap / repeat glides camera to group centroid)
/// - Period (.) cycles idle player armies + focuses camera
/// - Ctrl+A / Ctrl+Shift+A selects player armies on screen / worldwide
/// - Ctrl+Alt+H halts all player army movements at once (AoH #11)
/// - Alt+RMB "First where, then how many" cursor SplitMovePopup (AoH #8)
/// - Mouse Xbutton1/Xbutton2 Back/Forward panel history
/// - Keeps ArmyPanel open when clicking a region while armies are selected (Compact Province Info Popover)
/// - Unified QuickAmount recruitment popup & Budget "Balance to zero" integration
/// </summary>
public partial class DealOrForceGame
{
    internal static DealOrForceGame ActiveControlsHost { get; private set; }

    internal static bool CameraKeyboardBlocked =>
        QuickAmountManager.HasActiveFocus ||
        (ActiveControlsHost?._splitMovePopup is { IsPopupOpen: true }) ||
        (ActiveControlsHost?._quickRecruitLayer is { Visible: true });

    private readonly ControlGroupTracker _controlGroups = new();
    private readonly PanelHistoryStack _panelHistory = new();
    private UndoToastControl _undoToast;
    private SplitMovePopupControl _splitMovePopup;
    private AltHotkeyOverlay _altHotkeyOverlay;

    private PanelContainer _compactProvincePopover;
    private VBoxContainer _compactProvinceRows;
    private Label _compactProvinceTitle;
    private Label _compactProvinceHint;
    private Button _compactProvinceCloseBtn;
    private Button _compactProvinceOpenBtn;
    private GlobePick _compactProvincePick;

    private Control _quickRecruitLayer;
    private QuickAmountWidget _quickRecruitWidget;
    private OptionButton _quickRecruitTemplatePicker;
    private OptionButton _quickRecruitRegionPicker;
    private OptionButton _quickRecruitModePicker;
    private ScenarioState _quickRecruitState;

    private int _lastIdleCycledArmyId = -1;
    private Button _helpHudButton;

    private void BuildControlsErgonomics()
    {
        ActiveControlsHost = this;

        _undoToast = new UndoToastControl();
        _undoToast.UndoRequested += () => ExecutePendingBatchUndo();
        _undoToast.DismissRequested += () => BatchActionUndo.Clear(_campaign);
        _hudLayer.AddChild(_undoToast);

        _splitMovePopup = new SplitMovePopupControl();
        _hudLayer.AddChild(_splitMovePopup);

        _altHotkeyOverlay = new AltHotkeyOverlay();
        _hudLayer.AddChild(_altHotkeyOverlay);

        BuildCompactProvincePopover();
        BuildQuickRecruitPopup();
        RegisterAltOverlayTargets();

        BatchActionUndo.Changed -= OnBatchUndoChanged;
        BatchActionUndo.Changed += OnBatchUndoChanged;

        _globe.EmptyMapClicked -= OnEmptyMapClicked;
        _globe.EmptyMapClicked += OnEmptyMapClicked;

        _globe.AltProvinceSplitCommanded -= OnAltProvinceSplitCommanded;
        _globe.AltProvinceSplitCommanded += OnAltProvinceSplitCommanded;

        InterfaceSkin.Changed -= OnControlsSkinChanged;
        InterfaceSkin.Changed += OnControlsSkinChanged;
    }

    private void OnControlsSkinChanged()
    {
        if (IsInstanceValid(_altHotkeyOverlay))
            _altHotkeyOverlay.RebuildStyle();
    }

    private void OnBatchUndoChanged(Campaign campaign)
    {
        if (_campaign is null || campaign != _campaign) return;
        if (IsInstanceValid(_undoToast))
            _undoToast.RefreshFromCampaign(_campaign);
    }

    private void ResetControlsSessionState()
    {
        _controlGroups.ClearAll();
        _panelHistory.Clear();
        _lastIdleCycledArmyId = -1;
        CloseCompactProvincePopover();
        CloseQuickRecruitPopup();
        _splitMovePopup?.ClosePopup();
        if (IsInstanceValid(_undoToast))
            _undoToast.Visible = false;
    }

    private Button _cachedOutlinerToggle;

    private void MaintainControlsErgonomics()
    {
        if (_hudLayer is not { Visible: true })
        {
            _altHotkeyOverlay?.SetShowing(false);
            return;
        }

        if (IsInstanceValid(_undoToast) && _undoToast.Visible)
            _undoToast.UpdateAnchorPosition();

        // In NewShell, keycaps live natively on ShellCorner/ShellNavRail/ShellDatePlate.
        // Fast-path before calling GetViewport().GuiGetFocusOwner() every frame when Alt isn't held!
        if (IsNewShellActive || _freeCameraMode || !Input.IsKeyPressed(Key.Alt))
        {
            _altHotkeyOverlay?.SetShowing(false);
            return;
        }

        var modalBlocked = (_pauseMenuLayer is { Visible: true }) ||
                           (_settingsLayer is { Visible: true }) ||
                           (_saveLoadLayer is { Visible: true }) ||
                           (_handbookLayer is { Visible: true }) ||
                           (_navigatorLayer is { Visible: true }) ||
                           (_developerConsoleLayer is { Visible: true }) ||
                           (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit);

        _altHotkeyOverlay?.SetShowing(!modalBlocked);
    }

    private void RegisterAltOverlayTargets()
    {
        if (_altHotkeyOverlay is null) return;
        _altHotkeyOverlay.ClearTargets();
        _altHotkeyOverlay.RegisterTarget("date_plate_pause", () => _pause);
        _altHotkeyOverlay.RegisterTarget("search_button", () => _campaignNavigationFind);
        _altHotkeyOverlay.RegisterTarget("help_button", () => _helpHudButton);
        _altHotkeyOverlay.RegisterTarget("projection_button", () => _projectionButton);
        _altHotkeyOverlay.RegisterTarget("showcase_button", () => _showcasePanButton);
        _altHotkeyOverlay.RegisterTarget("map_layers", () => _mapLayerSelector);

        var screenToTarget = new (string Screen, string Target)[]
        {
            ("Nation", "nav_nation"),
            ("State Affairs", "nav_state_affairs"),
            ("Economy", "nav_economy"),
            ("Trade", "nav_trade"),
            ("Production", "nav_production"),
            ("Research", "nav_research"),
            ("Military", "nav_military"),
            ("Diplomacy", "nav_diplomacy"),
            ("Wars", "nav_wars"),
            ("Ledger", "nav_ledger")
        };
        foreach (var (screen, target) in screenToTarget)
        {
            var captured = screen;
            _altHotkeyOverlay.RegisterTarget(target, () => _screenButtons.GetValueOrDefault(captured));
        }

        _altHotkeyOverlay.RegisterTarget("outliner_toggle", () =>
        {
            if (_cachedOutlinerToggle is not null && IsInstanceValid(_cachedOutlinerToggle))
                return _cachedOutlinerToggle;
            _cachedOutlinerToggle = _outliner?.FindChild("OutlinerToggle", true, false) as Button;
            return _cachedOutlinerToggle;
        });

        var modeToTarget = new (StrategicMapMode Mode, string Target)[]
        {
            (StrategicMapMode.Political, "map_mode_political"),
            (StrategicMapMode.Diplomatic, "map_mode_diplomatic"),
            (StrategicMapMode.Economy, "map_mode_economy"),
            (StrategicMapMode.Resources, "map_mode_resources"),
            (StrategicMapMode.Wars, "map_mode_wars")
        };
        foreach (var (mode, target) in modeToTarget)
        {
            var captured = mode;
            _altHotkeyOverlay.RegisterTarget(target, () => _mapModeButtons.GetValueOrDefault(captured));
        }

        _altHotkeyOverlay.RegisterTarget("map_region_icons", () =>
            _mapVisualToggleButtons.FirstOrDefault(x => x.Setting == "map_region_icons").Button);
        _altHotkeyOverlay.RegisterTarget("army_disband", () => _armyDisbandButton);
    }

    // --------------------------------------------------------- History (Mouse 4/5 & Alt+Left/Right)

    private void RecordHistoryScreen(string screen)
    {
        if (string.IsNullOrEmpty(screen)) return;
        _panelHistory.Record(new PanelHistorySnapshot(PanelHistoryKind.Screen, screen, -1, -1, -1, Array.Empty<int>()));
    }

    private void RecordHistoryRegion(GlobePick pick)
    {
        if (pick.Nation is null && pick.State is null) return;
        _panelHistory.Record(new PanelHistorySnapshot(
            PanelHistoryKind.Region, "", pick.Nation?.Id ?? -1, pick.State?.MapIndex ?? -1, pick.ProvinceId, Array.Empty<int>()));
    }

    private void RecordHistoryArmies()
    {
        if (_selectedArmies.Count == 0) return;
        _panelHistory.Record(new PanelHistorySnapshot(
            PanelHistoryKind.Army, "", -1, -1, -1, _selectedArmies.Select(a => a.Id).OrderBy(id => id).ToArray()));
    }

    private bool HandleMouseHistoryButton(InputEventMouseButton mb)
    {
        if (!mb.Pressed || _campaign is null || _hudLayer is not { Visible: true }) return false;
        if (mb.ButtonIndex == MouseButton.Xbutton1)
            return StepHistory(forward: false);
        if (mb.ButtonIndex == MouseButton.Xbutton2)
            return StepHistory(forward: true);
        return false;
    }

    private bool StepHistory(bool forward)
    {
        var ok = forward
            ? _panelHistory.TryStepForward(out var snap)
            : _panelHistory.TryStepBack(out snap);
        if (!ok || snap is null) return false;

        _panelHistory.RunSuppressed(() =>
        {
            switch (snap.Kind)
            {
                case PanelHistoryKind.Screen:
                    OpenScreen(snap.ScreenName);
                    break;
                case PanelHistoryKind.Region:
                {
                    var nation = snap.NationId >= 0 ? _campaign.NationById(snap.NationId) : null;
                    var state = snap.StateMapIndex >= 0 ? _campaign.StateByMapIndex(snap.StateMapIndex) : null;
                    if (nation is null && state is not null)
                        nation = _campaign.Nations.Find(n => n.MapIndex == state.NationMapIndex);
                    if (nation is not null)
                        OnCountryPicked(new GlobePick { Nation = nation, State = state, ProvinceId = snap.ProvinceId });
                    break;
                }
                case PanelHistoryKind.Army:
                {
                    var armies = snap.ArmyIds
                        .Select(id => _campaign.Armies.Find(a => a.Id == id && ArmyInteraction.CanSelect(_campaign, a)))
                        .Where(a => a is not null)
                        .ToArray();
                    if (armies.Length > 0)
                        OnArmiesBoxPicked(armies);
                    break;
                }
            }
        });
        PlayStrategicCue("ui_select");
        return true;
    }

    // --------------------------------------------------------- Ocean deselect & Compact Province Info (§5.2)

    private void OnEmptyMapClicked()
    {
        QuickAmountManager.DismissAll();
        CloseCompactProvincePopover();
        CloseProvinceCard();
        if (_selectedArmies.Count > 0)
        {
            ClearArmySelectionAndClose();
            return;
        }
        if (_fleetPanel is { Visible: true })
        {
            CloseFleetPanel();
            return;
        }
        if (_mapSelectionOpen)
            CloseMapSelection();
    }

    private void BuildCompactProvincePopover()
    {
        var isTouch = DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var cardW = isTouch ? 368f : 336f;

        _compactProvincePopover = TexturedPanel(_hudLayer, PanelSurface.Command);
        _compactProvincePopover.Name = "CompactProvinceInfo";
        _compactProvincePopover.Visible = false;
        _compactProvincePopover.MouseFilter = MouseFilterEnum.Stop;
        _compactProvincePopover.ClipContents = true;
        _compactProvincePopover.SetAnchorsPreset(LayoutPreset.TopLeft);
        _compactProvincePopover.CustomMinimumSize = new Vector2(cardW, 0f);
        _compactProvincePopover.Size = new Vector2(cardW, 0f);
        _compactProvincePopover.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox("shell.plate_bottom", 0.96f, "shell.brass_hi", 0.90f, cornerRadius: 4, borderWidth: 1));

        var rows = PanelRows(_compactProvincePopover, 12, 10, 6);
        rows.ClipContents = true;
        rows.CustomMinimumSize = new Vector2(cardW - 24f, 0f);

        var head = new HBoxContainer { ClipContents = true };
        head.AddThemeConstantOverride("separation", 6);
        rows.AddChild(head);

        _compactProvinceTitle = Fixed(Title("", 16, Ui.Colour("text.bright")));
        _compactProvinceTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _compactProvinceTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        head.AddChild(_compactProvinceTitle);

        _compactProvinceCloseBtn = ActionButton("×", CloseCompactProvincePopover, isTouch ? 42 : 32, isTouch ? 42 : 30);
        _compactProvinceCloseBtn.Name = "CloseCompactProvinceInfo";
        _compactProvinceCloseBtn.AddThemeFontSizeOverride("font_size", isTouch ? 20 : 16);
        _compactProvinceCloseBtn.TooltipText = Ui.Tip(Tr("Close"));
        head.AddChild(_compactProvinceCloseBtn);

        rows.AddChild(new ColorRect
        {
            CustomMinimumSize = new Vector2(0f, 1f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Color = Ui.Colour("shell.brass_dim"),
            MouseFilter = MouseFilterEnum.Ignore
        });

        _compactProvinceRows = new VBoxContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _compactProvinceRows.AddThemeConstantOverride("separation", 4);
        rows.AddChild(_compactProvinceRows);

        _compactProvinceHint = Title(
            isTouch
                ? Tr("ui.compact_prov.touch_hint")
                : Tr("Armies remain selected. Right-click to march, or Esc / click ocean to deselect."),
            11, Ui.Colour("text.caption"));
        _compactProvinceHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _compactProvinceHint.CustomMinimumSize = new Vector2(cardW - 28f, 0f);
        _compactProvinceHint.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _compactProvinceHint.MaxLinesVisible = 3;
        _compactProvinceHint.ClipText = true;
        _compactProvinceHint.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        rows.AddChild(_compactProvinceHint);

        _compactProvinceOpenBtn = ActionButton(Tr("Open full region card"), () =>
        {
            var pick = _compactProvincePick;
            CloseCompactProvincePopover();
            if (pick.Nation is null && pick.State is null) return;
            var owner = pick.Nation ?? (pick.State is not null
                ? _campaign?.Nations.Find(n => n.MapIndex == pick.State.NationMapIndex)
                : null);
            var resolvedPick = pick.Nation is not null
                ? pick
                : new GlobePick(owner, pick.State, pick.ProvinceId, pick.Latitude, pick.Longitude);
            _selectedArmies.Clear();
            _selectedArmy = null;
            _globe?.SelectArmy(-1);
            SetArmyPanelVisible(false);
            if (IsNewShellActive)
                OpenNewShellMapSelection(resolvedPick);
            else if (resolvedPick.Nation is not null)
                OnCountryPicked(resolvedPick);
        }, 0, isTouch ? 42 : 30, primary: true);
        _compactProvinceOpenBtn.Name = "OpenFullRegionCardBtn";
        _compactProvinceOpenBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rows.AddChild(_compactProvinceOpenBtn);
    }

    private void CloseCompactProvincePopover()
    {
        if (IsInstanceValid(_compactProvincePopover))
            _compactProvincePopover.Visible = false;
    }

    private void ShowCompactProvinceInfoPopover(GlobePick pick)
    {
        if (_compactProvincePopover is null || !IsInstanceValid(_compactProvincePopover) || _campaign is null) return;
        _compactProvincePick = pick;
        ClearRuntimeChildren(_compactProvinceRows);

        var isTouch = DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var cardW = isTouch ? 368f : 336f;

        var state = pick.State;
        var owner = pick.Nation ?? (state is not null ? _campaign.Nations.Find(n => n.MapIndex == state.NationMapIndex) : null);
        _compactProvinceTitle.Text = state?.DisplayName ?? owner?.DisplayName ?? Tr("Select province for details");

        if (_compactProvinceHint is not null)
        {
            _compactProvinceHint.Text = isTouch
                ? Tr("ui.compact_prov.touch_hint")
                : Tr("Armies remain selected. Right-click to march, or Esc / click ocean to deselect.");
            _compactProvinceHint.CustomMinimumSize = new Vector2(cardW - 28f, 0f);
        }
        if (_compactProvinceOpenBtn is not null)
        {
            _compactProvinceOpenBtn.Text = Tr("Open full region card");
            _compactProvinceOpenBtn.CustomMinimumSize = new Vector2(0f, isTouch ? 42f : 30f);
        }
        if (_compactProvinceCloseBtn is not null)
        {
            _compactProvinceCloseBtn.CustomMinimumSize = isTouch ? new Vector2(42, 42) : new Vector2(32, 30);
            _compactProvinceCloseBtn.AddThemeFontSizeOverride("font_size", isTouch ? 20 : 16);
        }

        // Row 1: Owner Flag + Name
        var ownerRow = new HBoxContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ownerRow.AddThemeConstantOverride("separation", 6);
        if (owner is not null)
        {
            ownerRow.AddChild(FlagBadge(owner.Tag, 18, owner.DisplayName));
            var ownerLbl = Fixed(Title(owner.DisplayName, 13, Ui.Colour("shell.brass_hi")));
            ownerLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            ownerLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            ownerRow.AddChild(ownerLbl);
        }
        else
        {
            var unLbl = Fixed(Title(Tr("Unclaimed"), 13, Ui.Colour("text.muted")));
            unLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            unLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            ownerRow.AddChild(unLbl);
        }
        _compactProvinceRows.AddChild(ownerRow);

        // Row 2: Terrain + Supply/Defense
        if (state is not null || pick.ProvinceId > 0)
        {
            var terrain = _campaign.ProvinceMap is not null && pick.ProvinceId > 0 && pick.ProvinceId <= _campaign.ProvinceMap.ProvinceCount
                ? _campaign.ProvinceMap.TerrainAt(pick.ProvinceId)
                : TerrainCatalog.Get("plains");
            var terrainRow = new HBoxContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            terrainRow.AddThemeConstantOverride("separation", 6);
            var terrainName = string.IsNullOrWhiteSpace(terrain.Name) ? terrain.Id : Tr(terrain.Name);
            var tLbl = Fixed(Title(terrainName, 12, Ui.Colour("text.bright")));
            tLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            tLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            terrainRow.AddChild(tLbl);

            var supDef = Fixed(Title(Loc.Format("ui.compact_prov.terrain_stats_fmt",
                terrain.Supply.ToString("0.00"), terrain.Defense.ToString("0.00")), 11, Ui.Colour("text.body_soft")));
            supDef.AddThemeFontOverride("font", PresentationFonts.Tabular());
            supDef.HorizontalAlignment = HorizontalAlignment.Right;
            terrainRow.AddChild(supDef);
            _compactProvinceRows.AddChild(terrainRow);
        }

        // Row 3: Population + Stability
        if (state is not null)
        {
            var popRow = new HBoxContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            popRow.AddThemeConstantOverride("separation", 6);
            var popLbl = Fixed(Title(Loc.Format("ui.compact_prov.pop_fmt", Fmt.People(state.Population)), 12, Ui.Colour("text.body_soft")));
            popLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            popLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            popLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            popRow.AddChild(popLbl);

            var stabTok = state.Stability < 0.35f ? "state.negative" : state.Stability < 0.55f ? "state.warning" : "text.bright";
            var stabLbl = Fixed(Title(Loc.Format("ui.compact_prov.stab_fmt", Fmt.Percent(state.Stability)), 12, Ui.Colour(stabTok)));
            stabLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            stabLbl.HorizontalAlignment = HorizontalAlignment.Right;
            popRow.AddChild(stabLbl);
            _compactProvinceRows.AddChild(popRow);
        }

        // Row 4: Visible Divisions
        var divsHere = 0f;
        for (var i = 0; i < _campaign.Armies.Count; i++)
        {
            var a = _campaign.Armies[i];
            if (!CampaignIntel.PlayerCanSeeArmy(_campaign, a)) continue;
            if (pick.ProvinceId > 0 ? a.ProvinceId == pick.ProvinceId : (state is not null && a.StateMapIndex == state.MapIndex))
                divsHere += MathF.Max(0f, a.Divisions);
        }
        var divCount = (int)MathF.Ceiling(divsHere);
        if (divCount > 0)
        {
            var divLbl = Fixed(Title(Loc.Format("Divisions here: {0}", divCount), 12, Ui.Colour("text.bright")));
            divLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            divLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            _compactProvinceRows.AddChild(divLbl);
        }
        ShellRegionAuditCardsView.AppendCompactProvinceBadges(_campaign, state, pick.ProvinceId, _compactProvinceRows);
        _compactProvincePopover.CustomMinimumSize = new Vector2(cardW, 0f);
        _compactProvincePopover.Size = new Vector2(cardW, 0f);
        _compactProvincePopover.Visible = true;
        _compactProvincePopover.MoveToFront();
        _compactProvincePopover.ResetSize();

        var vp = GetViewport().GetVisibleRect().Size;
        const float minH = 150f;
        var maxH = MathF.Max(minH, vp.Y - 160f);
        var h = Math.Clamp(_compactProvincePopover.GetCombinedMinimumSize().Y, minH, maxH);
        _compactProvincePopover.Size = new Vector2(cardW, h);

        var leftEdge = _armyPanel is { Visible: true } ? _armyPanel.GetGlobalRect().End.X + 12f : (IsNewShellActive ? 76f : 28f);
        var maxX = MathF.Max(16f, vp.X - cardW - 16f);
        var bottomEdge = _planningBar is { Visible: true } ? _planningBar.GetGlobalRect().Position.Y - 10f : vp.Y - 116f;
        var maxY = MathF.Max(80f, MathF.Min(vp.Y - h - 16f, bottomEdge - h));
        _compactProvincePopover.Position = new Vector2(Math.Clamp(leftEdge, 16f, maxX), Math.Clamp(bottomEdge - h, 80f, maxY));
    }

    // --------------------------------------------------------- Alt+RMB Split-Move (AoH #8)

    private void OnAltProvinceSplitCommanded(GlobePick pick)
    {
        if (_campaign?.Player is null || !_hudLayer.Visible || _selectedArmies.Count == 0) return;
        var controllable = _selectedArmies
            .Where(a => a is not null && ArmyInteraction.CanSelect(_campaign, a))
            .ToList();
        if (controllable.Count == 0) return;

        var totalDivs = ArmySelectionRules.TotalWholeDivisions(controllable);
        if (totalDivs <= 1)
        {
            OnProvinceCommanded(pick);
            return;
        }

        var provinceName = pick.State?.DisplayName ?? $"#{pick.ProvinceId}";
        var mouse = GetViewport().GetMousePosition();
        _splitMovePopup.OpenAt(mouse, pick.ProvinceId, provinceName, totalDivs, (targetProvId, wantedDivs) =>
        {
            var res = ArmySelectionRules.ExecuteSplitMove(_campaign, _campaign.Player.Id, controllable,
                targetProvId, wantedDivs, Input.IsKeyPressed(Key.Shift), Input.IsKeyPressed(Key.Ctrl));
            if (!string.IsNullOrEmpty(res.Refusal))
            {
                SetArmyFeedback(res.Refusal);
                PlayStrategicCue("ui_error");
                return;
            }

            var movedArmies = res.MovingArmyIds
                .Select(id => _campaign.Armies.Find(a => a.Id == id))
                .Where(a => a is not null)
                .ToList();
            if (movedArmies.Count > 0)
            {
                _selectedArmies.Clear();
                _selectedArmies.AddRange(movedArmies);
                _selectedArmy = _selectedArmies[0];
                _globe.SelectArmies(_selectedArmies.Select(a => a.Id));
                ShowSelectedArmies();
            }
            SetArmyFeedback(Loc.Format("Dispatched {0} div. toward {1}", res.MovedDivisions, provinceName));
            PlayStrategicCue("army_move");
            Refresh();
        });
    }

    // --------------------------------------------------------- Unified physical shortcut dispatcher

    private bool HandleControlsShortcut(InputEventKey key)
    {
        if (_campaign?.Player is null || _hudLayer is not { Visible: true }) return false;

        // 1. If a focused QuickAmount / SplitMove popup owns A S D F / Enter / Esc, answer it FIRST.
        if (_splitMovePopup is { IsPopupOpen: true } && _splitMovePopup.HandleKey(key))
            return true;
        if (HandleFocusedQuickAmountKey(key))
            return true;

        if (_pauseMenuLayer is { Visible: true } || _settingsLayer is { Visible: true } ||
            _saveLoadLayer is { Visible: true } || _handbookLayer is { Visible: true } ||
            _navigatorLayer is { Visible: true } || _developerConsoleLayer is { Visible: true } ||
            _pickerLayer is { Visible: true })
            return false;

        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)
            return false;

        var letter = Letter(key);
        var ctrl = key.CtrlPressed || Input.IsKeyPressed(Key.Ctrl);
        var shift = key.ShiftPressed || Input.IsKeyPressed(Key.Shift);
        var alt = key.AltPressed || Input.IsKeyPressed(Key.Alt);
        var meta = key.MetaPressed;
        if (meta) return false;

        // 2. Global shortcuts (work regardless of selection)
        if (ctrl && !alt && !shift && letter == Key.Z)
            return ExecutePendingBatchUndo();

        if (ctrl && alt && !shift && letter == Key.H)
            return ExecuteEmergencyHaltAllArmies();

        if (ctrl && !alt && letter == Key.A)
            return shift ? ExecuteSelectAllArmiesWorld() : ExecuteSelectArmiesOnScreen();

        if (alt && !ctrl && !shift && letter == Key.Left)
            return StepHistory(forward: false);

        if (alt && !ctrl && !shift && letter == Key.Right)
            return StepHistory(forward: true);

        if (!ctrl && !alt && !shift && letter == Key.Period)
            return CycleNextIdleArmy();

        // Control groups 6..0 (Ctrl+6..0 binds; 6..0 selects, double-tap / repeat glides camera)
        var groupSlot = ControlGroupSlotOf(letter);
        if (groupSlot >= 0 && !alt && !shift)
        {
            if (ctrl)
                return BindSelectionToControlGroup(groupSlot);
            return ActivateControlGroup(groupSlot);
        }

        // Do not eat screen/map letters while FreeCamera or SovereignFinance modal owns letters.
        if (_freeCameraMode || _sovereignFinanceLayer is { Visible: true })
            return false;

        // 3. Selection-scoped army & fleet actions (only when modifiers/context match HotkeyCatalog)
        if (_selectedArmies.Count > 0 && !ctrl && !shift && !alt && letter == Key.G)
        {
            ConsolidateSelectedDivisions();
            return true;
        }
        if (_selectedArmies.Count == 0 && _selectedFleet is not null && _fleetPanel is { Visible: true } &&
            !ctrl && !shift && !alt && letter == Key.G)
        {
            MergeSelectedFleets();
            return true;
        }

        // Campaign screens (Q E R T Y U I O P L), / search, J outliner, Map modes (Z X C V N M) — physical QWERTY/JCUKEN
        if (!ctrl && !shift && !alt)
        {
            if (letter == Key.Slash)
            {
                ToggleNavigator();
                return true;
            }
            var resolved = HotkeyCatalog.ResolveActive(
                letter.ToString(), ctrl: false, shift: false, alt: false,
                hasArmiesSelected: _selectedArmies.Count > 0,
                hasFleetSelected: _selectedFleet is not null,
                freeCameraActive: _freeCameraMode,
                quickAmountFocused: CameraKeyboardBlocked);

            if (resolved is not null)
            {
                switch (resolved.Id)
                {
                    case "screen_nation": ToggleCampaignScreen("Nation"); return true;
                    case "screen_state_affairs": ToggleCampaignScreen("State Affairs"); return true;
                    case "screen_economy": ToggleCampaignScreen("Economy"); return true;
                    case "screen_trade": ToggleCampaignScreen("Trade"); return true;
                    case "screen_production": ToggleCampaignScreen("Production"); return true;
                    case "screen_research": ToggleCampaignScreen("Research"); return true;
                    case "screen_military": ToggleCampaignScreen("Military"); return true;
                    case "screen_diplomacy": ToggleCampaignScreen("Diplomacy"); return true;
                    case "screen_wars": ToggleCampaignScreen("Wars"); return true;
                    case "screen_ledger": ToggleCampaignScreen("Ledger"); return true;
                    case "toggle_outliner": ToggleOutliner(); return true;
                    case "map_mode_political": _globe.SetMapMode(StrategicMapMode.Political); CloseMapLayerDrawer(); return true;
                    case "map_mode_diplomatic": _globe.SetMapMode(StrategicMapMode.Diplomatic); CloseMapLayerDrawer(); return true;
                    case "map_mode_economy": _globe.SetMapMode(StrategicMapMode.Economy); CloseMapLayerDrawer(); return true;
                    case "map_mode_resources": _globe.SetMapMode(StrategicMapMode.Resources); CloseMapLayerDrawer(); return true;
                    case "map_mode_wars": _globe.SetMapMode(StrategicMapMode.Wars); CloseMapLayerDrawer(); return true;
                    case "toggle_map_layers": ToggleMapLayerDrawer(); return true;
                }
            }
        }
        return false;
    }

    private bool HandleFocusedQuickAmountKey(InputEventKey key)
    {
        if (IsInstanceValid(_quickRecruitWidget) && _quickRecruitWidget.IsVisibleInTree() &&
            _quickRecruitWidget.HandleKeyboardShortcut(key))
            return true;
        return false;
    }

    private static int ControlGroupSlotOf(Key physical) => physical switch
    {
        Key.Key6 or Key.Kp6 => 6,
        Key.Key7 or Key.Kp7 => 7,
        Key.Key8 or Key.Kp8 => 8,
        Key.Key9 or Key.Kp9 => 9,
        Key.Key0 or Key.Kp0 => 0,
        _ => -1
    };

    private bool BindSelectionToControlGroup(int slot)
    {
        var ids = _selectedArmies
            .Where(a => a is not null && ArmyInteraction.CanSelect(_campaign, a))
            .Select(a => a.Id)
            .ToArray();
        _controlGroups.Bind(slot, ids);
        SetArmyFeedback(ids.Length > 0
            ? Loc.Format("Bound {0} army(ies) to group {1}", ids.Length, slot)
            : Loc.Format("Group {0}", slot));
        RefreshArmyBar();
        PlayStrategicCue("ui_select");
        return true;
    }

    private bool ActivateControlGroup(int slot)
    {
        if (_campaign?.Player is null) return false;
        var group = _controlGroups.Resolve(_campaign, _campaign.Player.Id, slot);
        if (group.Count == 0) return false;

        var nowMs = Time.GetTicksMsec();
        var shouldFocus = _controlGroups.RecordActivateAndShouldFocus(slot, nowMs, _selectedArmies, group);
        OnArmiesBoxPicked(group);

        if (shouldFocus && ArmySelectionRules.ComputeCentroid(group, _campaign.ProvinceMap, out var lat, out var lon))
            _globe.FocusOn(lat, lon);
        return true;
    }

    private bool CycleNextIdleArmy()
    {
        if (_campaign?.Player is null) return false;
        var next = ArmySelectionRules.NextIdleArmy(_campaign, _campaign.Player.Id, _lastIdleCycledArmyId);
        if (next is null)
        {
            SetArmyFeedback(Tr("No idle armies right now"));
            return true;
        }
        _lastIdleCycledArmyId = next.Id;
        OnArmyPicked(next);
        if (_campaign.ProvinceMap is { } map && next.ProvinceId > 0 && next.ProvinceId <= map.ProvinceCount)
        {
            var center = map.CenterOf(next.ProvinceId);
            _globe.FocusOn(center.Latitude, center.Longitude);
        }
        else
        {
            _globe.FocusOn(next.Latitude, next.Longitude);
        }
        return true;
    }

    private bool ExecuteSelectArmiesOnScreen()
    {
        if (_campaign?.Player is null || _globe is null) return false;
        var onScreen = _globe.VisiblePlayerArmiesOnScreen(_campaign.Player.Id);
        if (onScreen.Count == 0)
        {
            SetArmyFeedback(Tr("No player armies visible on screen"));
            return true;
        }
        OnArmiesBoxPicked(onScreen);
        SetArmyFeedback(Loc.Format("Selected {0} army(ies) on screen", onScreen.Count));
        return true;
    }

    private bool ExecuteSelectAllArmiesWorld()
    {
        if (_campaign?.Player is null) return false;
        var all = _campaign.Armies
            .Where(a => ArmyInteraction.CanSelect(_campaign, a))
            .OrderBy(a => a.Id)
            .ToArray();
        if (all.Length == 0)
        {
            SetArmyFeedback(Tr("No idle armies right now"));
            return true;
        }
        OnArmiesBoxPicked(all);
        SetArmyFeedback(Loc.Format("Selected all {0} player army(ies)", all.Length));
        return true;
    }

    private bool ExecuteEmergencyHaltAllArmies()
    {
        if (_campaign?.Player is null) return false;
        var cmd = new HaltAllPlayerMovementsCommand { IssuerId = _campaign.Player.Id };
        _campaign.Commands.Enqueue(cmd);
        _campaign.Commands.ApplyAll(_campaign);
        SetArmyFeedback(cmd.HaltedCount > 0
            ? Loc.Format("Emergency brake: halted {0} army(ies)", cmd.HaltedCount)
            : Tr("Emergency brake: no moving armies"));
        PlayStrategicCue(cmd.HaltedCount > 0 ? "ui_select" : "ui_error");
        Refresh();
        return true;
    }

    // --------------------------------------------------------- Batch Undo + Everywhere + QuickRecruit

    private bool ExecutePendingBatchUndo()
    {
        if (_campaign?.Player is null) return false;
        UndoBatchActionCommand executed = null;
        if (!CommitImmediateCampaignWrite(c => BatchActionUndo.TryUndo(c, c.Player.Id, out executed), false, true))
        {
            SetArmyFeedback(Tr("Nothing unstarted left to undo"));
            _undoToast?.RefreshFromCampaign(_campaign);
            return false;
        }
        SetArmyFeedback(Loc.Format("Undone: {0} unstarted item(s), refunded {1}",
            executed.UndoneCount, Fmt.Money(MathF.Max(0f, executed.RefundedCash))));
        PlayStrategicCue("ui_select");
        _undoToast?.RefreshFromCampaign(_campaign);
        Refresh();
        if (IsNewShellActive) RefreshNewShell(force: true);
        return true;
    }

    private void GatherIdleArmiesToProvince(int targetProvinceId, string provinceName)
    {
        if (_campaign?.Player is null) return;
        var idle = ArmySelectionRules.GatherableIdleArmies(_campaign, _campaign.Player, targetProvinceId);
        if (idle.Count == 0)
        {
            SetArmyFeedback(Tr("No idle armies can march here."));
            PlayStrategicCue("ui_error");
            return;
        }
        foreach (var army in idle)
        {
            _campaign.Commands.Enqueue(new MoveArmyCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = army.Id,
                TargetProvinceId = targetProvinceId
            });
        }
        _campaign.Commands.ApplyAll(_campaign);
        SetArmyFeedback(Loc.Format("Gathered {0} idle army(ies) toward {1}", idle.Count, provinceName));
        PlayStrategicCue("army_move");
        Refresh();
    }

    private void ExecuteEverywhereConstructionAction(BuildingKind kind, int countPerRegion,
        IReadOnlyList<ScenarioState> scopeRegions = null)
    {
        if (_campaign?.Player is null) return;
        var plan = CommitImmediateCampaignWrite(
            c => BatchActionUndo.ExecuteEverywhereConstruction(c, c.Player, scopeRegions, kind, countPerRegion),
            refreshHud: false,
            wakeGlobe: true);
        if (plan.Items.Count == 0)
        {
            SetArmyFeedback(plan.Refusal);
            PlayStrategicCue("ui_error");
            return;
        }
        SetArmyFeedback(Loc.Format("Done in {0} region(s) ({1}) — Undo (Ctrl+Z)",
            plan.Items.Count, Loc.Format("{0} × {1}", plan.TotalBuildings, Tr(BuildingCatalog.Get(kind).Name))));
        PlayStrategicCue("ui_select");
        Refresh();
        if (IsNewShellActive) RefreshNewShell(force: true);
    }

    private void ExecuteZeroBudgetAction(bool adjustTaxes)
    {
        if (_campaign?.Player is null) return;
        var plan = BatchActionUndo.ExecuteZeroBudget(_campaign, _campaign.Player, adjustTaxes);
        if (plan.Changes.Count == 0)
        {
            if (_policyFeedback is not null)
                _policyFeedback.Text = string.IsNullOrEmpty(plan.Summary) ? Tr("Budget is already balanced.") : plan.Summary;
            PlayStrategicCue("ui_error");
            return;
        }
        if (_policyFeedback is not null)
            _policyFeedback.Text = Loc.Format("Adjusted {0} line(s): projected weekly balance {1} → {2}. Press Ctrl+Z to revert.",
                plan.Changes.Count, Fmt.MoneyDelta(plan.BalanceBefore), Fmt.MoneyDelta(plan.BalanceAfter));
        PlayStrategicCue("ui_select");
        Refresh();
    }

    // --------------------------------------------------------- Unified Quick Recruit Popup

    private void BuildQuickRecruitPopup()
    {
        _quickRecruitLayer = new Control { Name = "QuickRecruitLayer", Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _quickRecruitLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _quickRecruitLayer.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true })
            {
                CloseQuickRecruitPopup();
                _quickRecruitLayer.AcceptEvent();
            }
        };
        _hudLayer.AddChild(_quickRecruitLayer);

        var holder = TexturedPanel(_quickRecruitLayer, PanelSurface.Command);
        holder.Name = "QuickRecruitPanel";
        holder.CustomMinimumSize = new Vector2(Ui.Metric("controls.quick_amount_width", 344) + 24f, 0);
        holder.MouseFilter = MouseFilterEnum.Stop;

        var rows = PanelRows(holder, 12, 10, 8);
        rows.AddChild(Title(Tr("Quick recruit"), 17, Ui.Colour("text.bright")));

        _quickRecruitRegionPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        _quickRecruitRegionPicker.ItemSelected += _ => RefreshQuickRecruitPopup();
        rows.AddChild(_quickRecruitRegionPicker);

        _quickRecruitTemplatePicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        _quickRecruitTemplatePicker.ItemSelected += _ => RefreshQuickRecruitPopup();
        rows.AddChild(_quickRecruitTemplatePicker);

        _quickRecruitModePicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        _quickRecruitModePicker.AddItem(Tr("Standard training"), (int)MobilisationMode.Standard);
        _quickRecruitModePicker.AddItem(Tr("Rapid call-up"), (int)MobilisationMode.RapidCallUp);
        _quickRecruitModePicker.AddItem(Tr("Emergency mobilisation"), (int)MobilisationMode.TotalMobilisation);
        _quickRecruitModePicker.ItemSelected += _ => RefreshQuickRecruitPopup();
        rows.AddChild(_quickRecruitModePicker);

        _quickRecruitWidget = new QuickAmountWidget(showCloseButton: true);
        rows.AddChild(_quickRecruitWidget);
    }

    private void CloseQuickRecruitPopup()
    {
        if (IsInstanceValid(_quickRecruitLayer) && _quickRecruitLayer.Visible)
        {
            _quickRecruitWidget?.SetFocusHighlight(false);
            _quickRecruitLayer.Visible = false;
        }
    }

    private void OpenQuickRecruitPopup(ScenarioState state = null, string templateId = null, MobilisationMode mode = MobilisationMode.Standard)
    {
        if (_campaign?.Player is null || _quickRecruitLayer is null) return;
        _quickRecruitState = state ?? _selectedState;

        _quickRecruitRegionPicker.Clear();
        var ownedStates = QuickAmountRules.EligibleRecruitRegions(_campaign, _campaign.Player, preferBarracks: false);
        var selectRegionIdx = 0;
        for (var i = 0; i < ownedStates.Count; i++)
        {
            var s = ownedStates[i];
            _quickRecruitRegionPicker.AddItem(s.DisplayName, s.MapIndex);
            if (_quickRecruitState is not null && s.MapIndex == _quickRecruitState.MapIndex)
                selectRegionIdx = i;
        }
        if (ownedStates.Count > 0) _quickRecruitRegionPicker.Select(selectRegionIdx);

        _quickRecruitTemplatePicker.Clear();
        var selectTemplateIdx = 0;
        var allTemplates = UnitTypeCatalog.All.ToArray();
        for (var i = 0; i < allTemplates.Length; i++)
        {
            var t = allTemplates[i];
            _quickRecruitTemplatePicker.AddItem(Tr(t.Name), i);
            _quickRecruitTemplatePicker.SetItemMetadata(i, t.Id);
            if (!string.IsNullOrEmpty(templateId) && string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase))
                selectTemplateIdx = i;
        }
        if (allTemplates.Length > 0) _quickRecruitTemplatePicker.Select(selectTemplateIdx);
        _quickRecruitModePicker.Select((int)mode);

        _quickRecruitLayer.Visible = true;
        _quickRecruitLayer.MoveToFront();
        RefreshQuickRecruitPopup();

        if (_quickRecruitLayer.GetChild(0) is Control holder)
        {
            var vp = GetViewport().GetVisibleRect().Size;
            var mouse = GetViewport().GetMousePosition();
            var w = MathF.Max(368f, holder.GetCombinedMinimumSize().X);
            var h = MathF.Max(310f, holder.GetCombinedMinimumSize().Y);
            holder.Position = new Vector2(
                Math.Clamp(mouse.X + 16f, 20f, MathF.Max(20f, vp.X - w - 20f)),
                Math.Clamp(mouse.Y - 40f, 68f, MathF.Max(68f, vp.Y - h - 20f)));
        }
        _quickRecruitWidget.TakeKeyboardFocus();
    }

    private void RefreshQuickRecruitPopup()
    {
        if (_campaign?.Player is null || _quickRecruitWidget is null) return;
        var player = _campaign.Player;
        var regionId = _quickRecruitRegionPicker.Selected >= 0 ? _quickRecruitRegionPicker.GetSelectedId() : -1;
        var state = _campaign.StateByMapIndex(regionId) ?? _quickRecruitState;
        var tplIdx = Math.Max(0, _quickRecruitTemplatePicker.Selected);
        var templateId = _quickRecruitTemplatePicker.ItemCount > 0
            ? _quickRecruitTemplatePicker.GetItemMetadata(tplIdx).AsString()
            : UnitTypeCatalog.All.FirstOrDefault()?.Id ?? "";
        var mode = (MobilisationMode)Math.Clamp(_quickRecruitModePicker.Selected, 0, 2);
        var template = UnitTypeCatalog.All.FirstOrDefault(t => t.Id == templateId);

        var quote = QuickAmountRules.EvaluateRecruitmentCap(_campaign, player, state, templateId, mode);
        var everyPlan = QuickAmountRules.PlanEverywhereRecruitment(_campaign, player, null, templateId, mode,
            Math.Max(1, _quickRecruitWidget.CurrentIntValue), isQuarter: false);

        var maxCap = Math.Max(1, quote.MaxDivisions);
        var initial = quote.MaxDivisions > 0 ? Math.Clamp(_quickRecruitWidget.CurrentIntValue, 1, quote.MaxDivisions) : 1;

        _quickRecruitWidget.Configure(
            title: Loc.Format("Quick recruit {0}", Tr(template?.Name ?? templateId)),
            min: quote.MaxDivisions > 0 ? 1 : 0,
            max: maxCap,
            step: 1,
            initialValue: initial,
            integerOnly: true,
            valueFormatter: val => $"{Math.Max(0, (int)MathF.Round(val)):N0}",
            previewProvider: val =>
            {
                if (quote.MaxDivisions <= 0) return quote.Refusal;
                var count = Math.Clamp((int)MathF.Round(val), 1, quote.MaxDivisions);
                var cost = RecruitmentSystem.TotalBatchCost(_campaign, player, template, count, mode, out _);
                var manpower = (template?.RecruitmentPersonnel ?? 0) * count;
                return Loc.Format("After this: manpower {0}, cash {1}",
                    Fmt.People(MathF.Max(0f, player.ManpowerReserve - manpower)),
                    Fmt.Money(player.Treasury - cost));
            },
            onApply: quote.MaxDivisions <= 0 ? null : val =>
            {
                if (state is null) return;
                var count = Math.Clamp((int)MathF.Round(val), 1, quote.MaxDivisions);
                CommitImmediateCampaignCommand(new RaiseArmyCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex, TemplateId = templateId, Divisions = count, Mode = mode }, true, true);
                CloseQuickRecruitPopup();
                PlayStrategicCue("ui_select");
            },
            onEverywhere: val =>
            {
                var count = Math.Max(1, (int)MathF.Round(val));
                var plan = CommitImmediateCampaignWrite(c => BatchActionUndo.ExecuteEverywhereRecruitment(c, c.Player, null, templateId, mode, count, isQuarter: false), false, true);
                if (plan.Items.Count == 0) { SetArmyFeedback(plan.Refusal); PlayStrategicCue("ui_error"); return; }
                CloseQuickRecruitPopup();
                PlayStrategicCue("ui_select");
                Refresh();
                if (IsNewShellActive) RefreshNewShell(force: true);
            },
            everywhereCaption: Loc.Format("Everywhere ({0} reg.)", everyPlan.Items.Count),
            everywhereTooltip: () =>
            {
                var count = Math.Max(1, _quickRecruitWidget.CurrentIntValue);
                var plan = QuickAmountRules.PlanEverywhereRecruitment(_campaign, player, null, templateId, mode, count, isQuarter: false);
                return plan.Items.Count == 0
                    ? plan.Refusal
                    : Loc.Format("Apply to {0} eligible region(s): {1} total for {2}\n\nRefundable via Ctrl+Z until your next order.",
                        plan.Items.Count, plan.TotalDivisions, Fmt.Money(plan.TotalCost));
            },
            onClose: CloseQuickRecruitPopup,
            applyCaption: Loc.Get("Apply here"));

        _quickRecruitWidget.SetPresets(new (string, float, string)[]
        {
            ("+1", Math.Clamp(initial + 1, 1, maxCap), ""),
            ("×3", Math.Clamp(3, 1, maxCap), ""),
            ("MAX", quote.MaxDivisions, Loc.Format("Cash cap: {0} · Manpower cap: {1}", quote.MaxByTreasury, quote.MaxByManpower))
        });
    }

    // --------------------------------------------------------- Handbook Controls Table

    private void AppendHandbookControlsTable(VBoxContainer page)
    {
        if (page is null) return;
        foreach (var (categoryTitle, items) in HotkeyCatalog.GroupedForReference())
        {
            var cat = new Label
            {
                Text = categoryTitle.ToUpperInvariant(),
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            cat.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.caption", 12));
            cat.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
            page.AddChild(cat);

            foreach (var entry in items)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);
                page.AddChild(row);

                var badge = new PanelContainer { CustomMinimumSize = new Vector2(108, 24) };
                var keyStyle = new StyleBoxFlat
                {
                    BgColor = Ui.Colour("controls.keycap_paper"),
                    BorderColor = Ui.Colour("controls.keycap_border"),
                    ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2
                };
                keyStyle.SetBorderWidthAll(1);
                keyStyle.SetCornerRadiusAll(3);
                badge.AddThemeStyleboxOverride("panel", keyStyle);
                var keyLbl = new Label
                {
                    Text = entry.DisplayCombo,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                keyLbl.AddThemeFontSizeOverride("font_size", 11);
                keyLbl.AddThemeColorOverride("font_color", Ui.Colour("controls.keycap_ink"));
                badge.AddChild(keyLbl);
                row.AddChild(badge);

                var titleLbl = new Label
                {
                    Text = Tr(entry.Label),
                    CustomMinimumSize = new Vector2(170, 0),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                titleLbl.AddThemeFontSizeOverride("font_size", 13);
                titleLbl.AddThemeColorOverride("font_color", Ui.Colour("text.bright"));
                row.AddChild(titleLbl);

                var descLbl = new Label
                {
                    Text = Tr(entry.Description),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                descLbl.AddThemeFontSizeOverride("font_size", 12);
                descLbl.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
                row.AddChild(descLbl);
            }
        }
    }
}
