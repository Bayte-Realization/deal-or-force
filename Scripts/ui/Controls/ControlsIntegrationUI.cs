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

    private void MaintainControlsErgonomics()
    {
        if (_hudLayer is not { Visible: true })
        {
            _altHotkeyOverlay?.SetShowing(false);
            return;
        }

        if (IsInstanceValid(_undoToast) && _undoToast.Visible)
            _undoToast.UpdateAnchorPosition();

        var modalBlocked = (_pauseMenuLayer is { Visible: true }) ||
                           (_settingsLayer is { Visible: true }) ||
                           (_saveLoadLayer is { Visible: true }) ||
                           (_handbookLayer is { Visible: true }) ||
                           (_navigatorLayer is { Visible: true }) ||
                           (_developerConsoleLayer is { Visible: true }) ||
                           (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit);

        var showAlt = !modalBlocked && Input.IsKeyPressed(Key.Alt) && !_freeCameraMode;
        _altHotkeyOverlay?.SetShowing(showAlt);
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
            ("Economy", "nav_economy"),
            ("Military", "nav_military"),
            ("Diplomacy", "nav_diplomacy"),
            ("Wars", "nav_wars"),
            ("Decisions", "nav_decisions"),
            ("State Affairs", "nav_state_affairs"),
            ("Trade", "nav_trade"),
            ("Production", "nav_production"),
            ("Ledger", "nav_ledger")
        };
        foreach (var (screen, target) in screenToTarget)
        {
            var captured = screen;
            _altHotkeyOverlay.RegisterTarget(target, () => _screenButtons.GetValueOrDefault(captured));
        }

        var modeToTarget = new (StrategicMapMode Mode, string Target)[]
        {
            (StrategicMapMode.Political, "map_mode_political"),
            (StrategicMapMode.Diplomatic, "map_mode_diplomatic"),
            (StrategicMapMode.Terrain, "map_mode_terrain"),
            (StrategicMapMode.Population, "map_mode_population"),
            (StrategicMapMode.Economy, "map_mode_economy"),
            (StrategicMapMode.Wars, "map_mode_military")
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
        _compactProvincePopover = TexturedPanel(_hudLayer, PanelSurface.Command);
        _compactProvincePopover.Name = "CompactProvinceInfo";
        _compactProvincePopover.Visible = false;
        _compactProvincePopover.MouseFilter = MouseFilterEnum.Stop;
        _compactProvincePopover.CustomMinimumSize = new Vector2(300, 0);

        var rows = PanelRows(_compactProvincePopover, 12, 10, 6);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        rows.AddChild(head);

        _compactProvinceTitle = Title("", 16, Ui.Colour("text.bright"));
        _compactProvinceTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_compactProvinceTitle);

        var closeBtn = ActionButton("×", CloseCompactProvincePopover, 28, 24);
        closeBtn.TooltipText = Ui.Tip(Tr("Close"));
        head.AddChild(closeBtn);

        _compactProvinceRows = new VBoxContainer();
        _compactProvinceRows.AddThemeConstantOverride("separation", 3);
        rows.AddChild(_compactProvinceRows);

        var hint = Title(Tr("Armies remain selected. Right-click to march, or Esc / click ocean to deselect."),
            11, Ui.Colour("text.caption"));
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(hint);

        var openBtn = ActionButton(Tr("Open full region card"), () =>
        {
            var pick = _compactProvincePick;
            CloseCompactProvincePopover();
            if (pick.Nation is not null)
            {
                _selectedArmies.Clear();
                _selectedArmy = null;
                _globe?.SelectArmy(-1);
                SetArmyPanelVisible(false);
                OnCountryPicked(pick);
            }
        }, 0, 28);
        rows.AddChild(openBtn);
    }

    private void CloseCompactProvincePopover()
    {
        if (IsInstanceValid(_compactProvincePopover))
            _compactProvincePopover.Visible = false;
    }

    private void ShowCompactProvinceInfoPopover(GlobePick pick)
    {
        if (_compactProvincePopover is null || !IsInstanceValid(_compactProvincePopover)) return;
        _compactProvincePick = pick;
        ClearRuntimeChildren(_compactProvinceRows);

        var state = pick.State;
        var owner = pick.Nation ?? (state is not null ? _campaign.Nations.Find(n => n.MapIndex == state.NationMapIndex) : null);
        _compactProvinceTitle.Text = state?.DisplayName ?? owner?.DisplayName ?? Tr("Select province for details");

        if (owner is not null)
            _compactProvinceRows.AddChild(Title(Loc.Format("{0}", owner.DisplayName), 12, Ui.Colour("text.accent")));
        if (state is not null)
        {
            _compactProvinceRows.AddChild(Title(Loc.Format("Population: {0} · Stability: {1}",
                Fmt.People(state.Population), Fmt.Percent(state.Stability)), 12, Ui.Colour("text.body_soft")));
            var divsHere = _campaign.Armies.Count(a => a.ProvinceId == pick.ProvinceId && CampaignIntel.PlayerCanSeeArmy(_campaign, a));
            if (divsHere > 0)
                _compactProvinceRows.AddChild(Title(Loc.Format("Divisions here: {0}", divsHere), 12, Ui.Colour("text.bright")));
        }

        _compactProvincePopover.Visible = true;
        _compactProvincePopover.MoveToFront();
        var vp = GetViewport().GetVisibleRect().Size;
        var w = MathF.Max(300f, _compactProvincePopover.GetCombinedMinimumSize().X);
        var h = MathF.Max(140f, _compactProvincePopover.GetCombinedMinimumSize().Y);
        var leftEdge = _armyPanel is { Visible: true }
            ? _armyPanel.GetGlobalRect().End.X + 12f
            : 28f;
        _compactProvincePopover.Position = new Vector2(
            Math.Clamp(leftEdge, 16f, MathF.Max(16f, vp.X - w - 16f)),
            MathF.Max(80f, vp.Y - h - 116f));
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

        // Campaign screens (Q E R T Y U I O P L) and Map modes (J Z X C V N M) — physical QWERTY/JCUKEN
        if (!ctrl && !shift && !alt)
        {
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
                    case "screen_economy": ToggleCampaignScreen("Economy"); return true;
                    case "screen_military": ToggleCampaignScreen("Military"); return true;
                    case "screen_diplomacy": ToggleCampaignScreen("Diplomacy"); return true;
                    case "screen_wars": ToggleCampaignScreen("Wars"); return true;
                    case "screen_decisions": ToggleCampaignScreen("Decisions"); return true;
                    case "screen_state_affairs": ToggleCampaignScreen("State Affairs"); return true;
                    case "screen_trade": ToggleCampaignScreen("Trade"); return true;
                    case "screen_production": ToggleCampaignScreen("Production"); return true;
                    case "screen_ledger": ToggleCampaignScreen("Ledger"); return true;
                    case "toggle_map_layers": ToggleMapLayerDrawer(); return true;
                    case "map_mode_political": _globe.SetMapMode(StrategicMapMode.Political); CloseMapLayerDrawer(); return true;
                    case "map_mode_diplomatic": _globe.SetMapMode(StrategicMapMode.Diplomatic); CloseMapLayerDrawer(); return true;
                    case "map_mode_terrain": _globe.SetMapMode(StrategicMapMode.Terrain); CloseMapLayerDrawer(); return true;
                    case "map_mode_population": _globe.SetMapMode(StrategicMapMode.Population); CloseMapLayerDrawer(); return true;
                    case "map_mode_economy": _globe.SetMapMode(StrategicMapMode.Economy); CloseMapLayerDrawer(); return true;
                    case "map_mode_military": _globe.SetMapMode(StrategicMapMode.Wars); CloseMapLayerDrawer(); return true;
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
        var hadCashBefore = _campaign.Player.Treasury;
        if (!BatchActionUndo.TryUndo(_campaign, _campaign.Player.Id, out var executed))
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
        var plan = BatchActionUndo.ExecuteEverywhereConstruction(_campaign, _campaign.Player, scopeRegions, kind, countPerRegion);
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
                _campaign.Commands.Enqueue(new RaiseArmyCommand
                {
                    IssuerId = player.Id,
                    StateMapIndex = state.MapIndex,
                    TemplateId = templateId,
                    Divisions = count,
                    Mode = mode
                });
                _campaign.Commands.ApplyAll(_campaign);
                CloseQuickRecruitPopup();
                PlayStrategicCue("ui_select");
                Refresh();
            },
            onEverywhere: val =>
            {
                var count = Math.Max(1, (int)MathF.Round(val));
                var plan = BatchActionUndo.ExecuteEverywhereRecruitment(_campaign, player, null, templateId, mode, count, isQuarter: false);
                if (plan.Items.Count == 0)
                {
                    SetArmyFeedback(plan.Refusal);
                    PlayStrategicCue("ui_error");
                    return;
                }
                CloseQuickRecruitPopup();
                PlayStrategicCue("ui_select");
                Refresh();
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
