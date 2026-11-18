using System;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Single thin integration partial bridging DealOrForceGame to the standalone ShellOrchestrator.
/// </summary>
public partial class DealOrForceGame
{
    private Control _newShellRoot;
    private bool _newShellBuilt;
    private ShellOrchestrator _shell;
    private bool _newShellLastLeftVisible;

    internal double LastRefreshNewShellMs => _shell?.LastRefreshMs ?? 0.0;

    internal bool IsNewShellActive =>
        string.Equals(SettingsCatalog.Chosen("ui_shell"), "new", StringComparison.OrdinalIgnoreCase);

    internal bool IsNewShellEconomyOpen => _shell?.EconomyWorkspace.IsVisible ?? false;
    internal bool IsNewShellNationOpen =>
        (_shell?.WorkPanel.IsVisible ?? false) && _shell.WorkPanel.Mode == ShellWorkPanelView.DossierMode.Country && _openScreen == "Nation";

    internal bool HasOpenShellTransientPopover => _shell?.HasTransientPopover ?? false;

    private static readonly string[] ClassicChromeNodeNames =
    {
        "CampaignTopBar", "HudFlagMount", "HudResourceDrawer", "CampaignNavigation", "DatePlate",
        "AlertList", "CampaignAlertPopup", "Outliner", "SituationDesk", "SituationDeskTab",
        "MapControls", "MapModeDrawer", "ArmyBar", "TeletypeBand", "HeadlinePopups"
    };

    private void BuildNewShell()
    {
        if (_hudLayer is null || _newShellBuilt) return;
        _newShellBuilt = true;
        _newShellRoot = new Control
        {
            Name = "NewCampaignShell", MouseFilter = MouseFilterEnum.Ignore,
            Visible = false, ProcessMode = ProcessModeEnum.Disabled
        };
        _newShellRoot.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _hudLayer.AddChild(_newShellRoot);
        _shell = new ShellOrchestrator(CreateShellContext(_newShellRoot));
        _shell.Build();
        ApplyUiShellSetting();
    }

    private ShellContext CreateShellContext(Control root)
    {
        var ctx = new ShellContext
        {
            Root = root,
            GetCampaign = () => _campaign,
            GetViewportSize = () => _hudLayer?.Size is { X: >= 320f, Y: >= 240f } sz ? sz : GetViewportRect().Size,
            IsPaused = () => _campaign?.IsPaused ?? true,
            GetSpeed = () => _speed,
            IsObserverMode = () => _observerMode,
            IsFreeCameraMode = () => _freeCameraMode,
            IsAnyModalOpen = IsAnyShellModalOpen,
            IsProductionOpen = () => _productionLayer is { Visible: true },
            GetOpenScreen = () => IsNewShellEconomyOpen ? "Economy" : _openScreen,
            GetMapMode = () => _activeMapMode,
            IsFlatMap = () => _globe is not null && _globe.IsFlatMap,
            GetViewDistance = () => _globe?.ShellViewDistance ?? 6.5f,
            GetFlatZoom = () => _globe?.ShellFlatZoom ?? 4f,
            GetCameraMinimapView = () => _globe?.CameraMinimapView ?? (20f, 0f, 60f),
            GetSelectedArmies = () => _selectedArmies,
            GetSelectedNation = () => _selectedNation,
            GetSelectedState = () => _selectedState,
            GetSelectedProvinceId = () => _selectedProvinceId,
            CanNavigateBack = () => _panelHistory.CanGoBack
        };
        BindShellContextActions(ctx);
        return ctx;
    }

    private void BindShellContextActions(ShellContext ctx)
    {
        ctx.OpenScreen = OpenScreen;
        ctx.ToggleScreen = ToggleCampaignScreen;
        ctx.SetMapMode = mode => { _globe?.SetMapMode(mode); RefreshNewShell(force: true); };
        ctx.ToggleProjection = () => { ToggleProjection(); RefreshNewShell(force: true); };
        ctx.TogglePause = () => { TogglePause(); _shell?.DatePlate.Refresh(); };
        ctx.SetSpeed = s => { SetSpeed(s, resume: false); _shell?.DatePlate.Refresh(); };
        ctx.ShowPauseMenu = ShowPauseMenu;
        ctx.OpenDefconOverview = OpenDefconOverview;
        ctx.OpenWarLedger = OpenWarLedger;
        ctx.ShowWarDetail = ShowWarLedgerDetail;
        ctx.OpenSovereignFinance = OpenSovereignFinance;
        ctx.OpenCountryRankings = OpenCountryRankings;
        ctx.OpenRecruitment = () => OpenRecruitment();
        ctx.ToggleNavigator = ToggleNavigator;
        ctx.FocusOwnCapital = FocusOwnCapital;
        ctx.FocusLatLon = (lat, lon, dist) => _globe?.FocusOn(lat, lon, dist);
        ctx.ProjectToScreen = (lat, lon) => _globe?.ProjectMapPointToScreen(lat, lon);
        ctx.SetMergedVisibleArmies = set => { if (_globe is not null) _globe.NewShellVisibleArmyIds = set; };
        ctx.PickArmy = (army, add) => { if (add) OnArmiesBoxPicked(_selectedArmies.Append(army).Distinct().ToArray()); else OnArmyPicked(army); };
        ctx.PickMapRegion = (nation, state, prov) => OnCountryPicked(new GlobePick { Nation = nation, State = state, ProvinceId = prov });
        ctx.ApplySetting = ApplySetting;
        ctx.PlayCue = PlayStrategicCue;
        ctx.ExecuteZeroBudget = ExecuteZeroBudgetAction;
        ctx.PerformBatchUndo = UndoLastBatchForShell;
        ctx.NavigateBack = () => StepHistory(forward: false);
        ctx.SplitSelectionInHalf = SplitSelectionInHalf;
        ctx.MergeSelectedArmies = MergeSelectedArmies;
        ctx.ClearArmySelection = ClearArmySelectionAndClose;
        ctx.RefreshAll = () => { Refresh(); RefreshNewShell(force: true); };
    }

    private void UndoLastBatchForShell()
    {
        if (_campaign?.Player is null || !BatchActionUndo.TryUndo(_campaign, _campaign.Player.Id)) return;
        EconomySystem.Explain(_campaign);
        Refresh();
        RefreshNewShell(force: true);
    }

    internal bool IsNewShellSuppressingRegionIcons() =>
        IsNewShellActive && !ShellDockRules.ShouldShowRegionIcons(
            isNewShell: true,
            explicitUserToggle: _shell?.Context.ExplicitRegionIconsToggle ?? false,
            openScreen: IsNewShellEconomyOpen ? "Economy" : _openScreen,
            productionScreenVisible: _productionLayer is { Visible: true },
            resourcesOrEconomyMapMode: _activeMapMode is StrategicMapMode.Resources or StrategicMapMode.Economy);

    internal void OpenNewShellEconomyWorkspace()
    {
        if (!_newShellBuilt) BuildNewShell();
        CloseVisibleCampaignScreen();
        HideLegacyLeftAndBottomCards();
        _shell?.WorkPanel.Close();
        _openScreen = "Economy";
        _newShellRoot?.MoveToFront();
        _shell?.EconomyWorkspace.Open();
        SyncNewShellLeftAndBottomDock();
        RefreshNewShell(force: true);
    }

    internal void CloseNewShellEconomyWorkspace()
    {
        _shell?.EconomyWorkspace.Close();
        if (_openScreen == "Economy") _openScreen = "";
        SyncNewShellLeftAndBottomDock();
    }

    internal void OpenNewShellCountryDossier(Nation nation)
    {
        if (!_newShellBuilt) BuildNewShell();
        if (IsNewShellEconomyOpen) CloseNewShellEconomyWorkspace();
        HideLegacyLeftAndBottomCards();
        SetArmyPanelVisible(false);
        _openScreen = nation == _campaign?.Player ? "Nation" : "";
        _shell?.WorkPanel.OpenCountry(nation);
        SyncNewShellLeftAndBottomDock();
        RefreshNewShell(force: true);
    }

    internal void OpenNewShellMapSelection(GlobePick pick)
    {
        if (!_newShellBuilt) BuildNewShell();
        if (IsNewShellEconomyOpen) CloseNewShellEconomyWorkspace();
        HideLegacyLeftAndBottomCards();
        SetArmyPanelVisible(false);
        if (pick.State is not null) { _openScreen = ""; _shell?.WorkPanel.OpenRegion(pick.Nation, pick.State, pick.ProvinceId); }
        else if (pick.Nation is not null) { _openScreen = pick.Nation == _campaign?.Player ? "Nation" : ""; _shell?.WorkPanel.OpenCountry(pick.Nation); }
        SyncNewShellLeftAndBottomDock();
        RefreshNewShell(force: true);
    }

    internal void CloseNewShellWorkPanel(bool unpinnedOnly)
    {
        if (_shell is null) return;
        if (unpinnedOnly && _shell.WorkPanel.IsPinned) return;
        _shell.WorkPanel.Close();
        if (_openScreen == "Nation") _openScreen = "";
        SyncNewShellLeftAndBottomDock();
    }

    internal void OnNewShellWorkspaceRaised(Control layer)
    {
        if (!_newShellBuilt) BuildNewShell();
        if (IsNewShellEconomyOpen) CloseNewShellEconomyWorkspace();
        CloseNewShellWorkPanel(unpinnedOnly: true);
        _shell?.Alerts.DismissNewsCard();
        _newShellRoot?.MoveToFront();
        SyncNewShellLeftAndBottomDock();
    }

    internal void FitNewShellWorkspaceLayer(Control layer) => _shell?.FitWorkspaceLayer(layer);

    internal Control BuildDiplomacyLeaderBadge(Nation nation, LeaderProfile hos, int width, int height)
    {
        if (HasRealLeaderPortrait(hos) || _shell is null) return BuildLeaderPortraitBadge(hos, nation.Color, width, height);
        return _shell.BuildStateSealBadge(_campaign, nation, width, height);
    }

    private void HideLegacyLeftAndBottomCards()
    {
        if (_sidePanel is not null) _sidePanel.Visible = false;
        if (_countryCard is not null) _countryCard.Visible = false;
        if (_regionCard is not null) _regionCard.Visible = false;
    }

    internal void SyncNewShellLeftAndBottomDock()
    {
        if (!_newShellBuilt || !IsNewShellActive || _shell is null) return;
        var profile = _shell.CurrentProfile();
        if (_regionCard is { Visible: true }) _regionCard.Visible = false;
        if (_armyPanel is { Visible: true } && _shell.WorkPanel.IsVisible && !_shell.WorkPanel.IsPinned)
            _shell.WorkPanel.Close();

        var anyLeft = (_armyPanel is { Visible: true }) || (_sidePanel is { Visible: true }) ||
                      (_fleetPanel is { Visible: true }) || (_countryCard is { Visible: true }) || _shell.WorkPanel.IsVisible;
        var pbarW = _planningBar is not null && IsInstanceValid(_planningBar) ? Math.Max(340f, _planningBar.GetCombinedMinimumSize().X) : 520f;
        var dock = ShellDockRules.ComputeLeftAndBottomDock(profile, anyLeft, IsAnyShellModalOpen(), pbarW);

        foreach (var lp in new Control[] { _armyPanel, _sidePanel, _fleetPanel, _countryCard })
            if (lp is not null && IsInstanceValid(lp) && lp.Visible) ShellContext.PlaceRect(lp, dock.LeftPanel);

        _shell.WorkPanel.ApplyDock(profile, dock);
        _shell.Strip.ApplyDock(profile, dock);
        if (_planningBar is not null && IsInstanceValid(_planningBar) && _planningBar.Visible)
            ShellContext.PlaceRect(_planningBar, dock.PlanningBar);
    }

    private void ApplyUiShellSetting()
    {
        if (_hudLayer is null) return;
        if (!_newShellBuilt) BuildNewShell();
        var isNew = IsNewShellActive;
        SetClassicChromeVisible(!isNew);
        if (_newShellRoot is not null && IsInstanceValid(_newShellRoot))
        {
            _newShellRoot.Visible = isNew;
            _newShellRoot.ProcessMode = isNew ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_globe is not null && !isNew) _globe.NewShellVisibleArmyIds = null;
        if (isNew) { _shell?.ResetCadence(); LayoutNewShell(); RefreshNewShell(force: true); }
        else { CloseShellTransientPopovers(); _shell?.WorkPanel.Close(); CloseNewShellEconomyWorkspace(); LayoutCampaignShell(); Refresh(); }
    }

    private void SetClassicChromeVisible(bool visible)
    {
        if (_hudLayer is null) return;
        foreach (var name in ClassicChromeNodeNames)
        {
            if (_hudLayer.FindChild(name, true, false) is not Control node || !IsInstanceValid(node)) continue;
            node.Visible = visible && name is not ("MapModeDrawer" or "HudResourceDrawer" or "SituationDesk");
            node.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        foreach (var ctrl in new Control[] { _headlineStack, _alertListPanel, _outliner, _hudTopPanel, _hudDatePlate, _mapControlsPanel, _armyBar })
        {
            if (ctrl is null || !IsInstanceValid(ctrl)) continue;
            ctrl.Visible = visible;
            ctrl.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (!visible && _mapModeDrawerLayer is not null && IsInstanceValid(_mapModeDrawerLayer))
            _mapModeDrawerLayer.Visible = false;
    }

    private void LayoutNewShell()
    {
        if (!_newShellBuilt || !IsNewShellActive || _shell is null) return;
        _shell.LayoutAll();
        SyncNewShellLeftAndBottomDock();
    }

    private void RefreshNewShell(bool force = false)
    {
        if (!_newShellBuilt || !IsNewShellActive || _campaign is null || _hudLayer is not { Visible: true }) return;
        SetClassicChromeVisible(false);
        _shell?.Refresh(_campaign, _speed, force);
        SyncNewShellLeftAndBottomDock();
    }

    private void TickNewShell(double delta)
    {
        if (!_newShellBuilt || !IsNewShellActive || _campaign is null || _hudLayer is not { Visible: true } || _shell is null) return;
        var anyLeft = (_armyPanel is { Visible: true }) || (_sidePanel is { Visible: true }) ||
                      (_fleetPanel is { Visible: true }) || _shell.WorkPanel.IsVisible;
        if (anyLeft != _newShellLastLeftVisible) { _newShellLastLeftVisible = anyLeft; SyncNewShellLeftAndBottomDock(); }
        _shell.Tick(delta);
    }

    private void RefreshNewShellLocalised()
    {
        if (!_newShellBuilt || _shell is null) return;
        _shell.ResetCadence();
        LayoutNewShell();
        RefreshNewShell(force: true);
    }

    internal void CloseShellTransientPopovers() => _shell?.CloseTransientPopovers();

    private bool IsAnyShellModalOpen() =>
        IsNewShellEconomyOpen || _handbookLayer is { Visible: true } || _peaceLayer is { Visible: true } ||
        _defconLayer is { Visible: true } || _pauseMenuLayer is { Visible: true } || _settingsLayer is { Visible: true } ||
        _saveLoadLayer is { Visible: true } || _pickerLayer is { Visible: true } || _stateAffairsLayer is { Visible: true } ||
        _sovereignFinanceLayer is { Visible: true } || _ledgerLayer is { Visible: true } || _warLedgerLayer is { Visible: true } ||
        _diplomacyLayer is { Visible: true } || _conflictsLayer is { Visible: true } || _policyLayer is { Visible: true } ||
        _tradeScreenLayer is { Visible: true } || _productionLayer is { Visible: true } || _supportLayer is { Visible: true } ||
        _researchLayer is { Visible: true } || _doctrineLayer is { Visible: true };

    private bool HandleNewShellHotkey(InputEventKey key)
    {
        if (!IsNewShellActive || !_newShellBuilt || _campaign is null || _hudLayer is not { Visible: true } || _shell is null)
            return false;
        var letter = Letter(key);
        var textFocus = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        if (!key.CtrlPressed && !key.AltPressed && !key.MetaPressed && !textFocus && !IsAnyShellModalOpen())
        {
            if (letter == Key.M) { _shell.MapBar.TogglePopover(); return true; }
            if (letter == Key.J) { _shell.Alerts.ToggleOutliner(); return true; }
        }
        return HotkeyMap.HandleKey(_shell.Context, key, letter, textFocus, HandleNewShellEscape);
    }

    private bool HandleNewShellEscape()
    {
        if (_shell.Messages.IsVisible) { _shell.Messages.Close(); return true; }
        if (_shell.MapBar.IsPopoverOpen) { _shell.MapBar.ClosePopover(); return true; }
        if (_shell.EconomyWorkspace.IsVisible) { CloseNewShellEconomyWorkspace(); return true; }
        if (_shell.Alerts.IsNewsCardVisible) { _shell.Alerts.DismissNewsCard(); return true; }
        if (_shell.WorkPanel.IsVisible) { CloseNewShellWorkPanel(unpinnedOnly: false); return true; }
        if (CloseVisibleCampaignScreen()) return true;
        ShowPauseMenu();
        return true;
    }
}
