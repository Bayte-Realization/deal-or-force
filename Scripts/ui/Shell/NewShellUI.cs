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
    private bool _newShellModalWasOpen;
    private readonly System.Collections.Generic.List<(Control Node, bool ClassicDefaultVisible)> _classicChromeCache = new();
    private bool _classicChromeCached;
    private bool? _classicChromeLastVisible;
    private ulong _uiShellCacheFrame = ulong.MaxValue;
    private bool _uiShellCacheValue;
    private ulong _lastModalSyncFrame = ulong.MaxValue;

    [ToolHook] internal double LastRefreshNewShellMs => _shell?.LastRefreshMs ?? 0.0;

    internal bool IsNewShellActive
    {
        get
        {
            var frame = Engine.GetProcessFrames();
            if (frame == _uiShellCacheFrame) return _uiShellCacheValue;
            _uiShellCacheFrame = frame;
            return _uiShellCacheValue = string.Equals(SettingsCatalog.Chosen("ui_shell"), "new", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal bool IsNewShellEconomyOpen => _shell?.EconomyWorkspace.IsVisible ?? false;
    internal bool IsNewShellGrandProjectsOpen => _shell?.GrandProjects.IsVisible ?? false;
    internal bool IsNewShellNationOpen =>
        (_shell?.WorkPanel.IsVisible ?? false) && _shell.WorkPanel.Mode == ShellWorkPanelView.DossierMode.Country && _openScreen == "Nation";

    internal bool HasOpenShellTransientPopover => _shell?.HasTransientPopover ?? false;

    private static readonly string[] ClassicChromeNodeNames =
    {
        "CampaignTopBar", "CampaignFlagMount", "HudResourceDrawer", "CampaignNavigation", "DatePlate",
        "AlertList", "CampaignAlertPopup", "Outliner", "StrategicOutliner", "SituationDesk", "SituationDeskTab",
        "MapControls", "MapLayerDrawer", "ArmyBar", "TeletypeBand", "HeadlinePopups"
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
        EnsureNewShellFlagClothPulse();
        _shell = new ShellOrchestrator(CreateShellContext(_newShellRoot));
        _shell.Build();
        ApplyUiShellSetting();
    }

    private void EnsureNewShellFlagClothPulse()
    {
        if (_newShellRoot is null) return;
        if (_newShellRoot.GetNodeOrNull<FlagClothSettingsPulse>("NewShellFlagClothPulse") is null)
        {
            _newShellRoot.AddChild(new FlagClothSettingsPulse
            {
                Name = "NewShellFlagClothPulse",
                ProcessMode = ProcessModeEnum.Always
            });
        }
        _ = FlagClothPresentation.Material;
        FlagClothPresentation.Synchronize();
    }

    private ShellContext CreateShellContext(Control root)
    {
        var ctx = new ShellContext
        {
            Root = root,
            GetCampaign = () => _campaign,
            GetHudSnapshot = () => _simRunner.ReadHudSnapshot(_campaign),
            GetPresentationDate = () => _simRunner.ReadPresentationDate(_campaign),
            GetViewportSize = () => _hudLayer?.Size is { X: >= 320f, Y: >= 240f } sz ? sz : GetViewportRect().Size,
            IsPaused = () => _campaign?.IsPaused ?? true,
            GetSpeed = () => _speed,
            IsObserverMode = () => _observerMode || (_campaign?.ObserverMode ?? false),
            IsFreeCameraMode = () => _freeCameraMode,
            IsAnyModalOpen = IsAnyShellModalOpen,
            IsProductionOpen = () => _productionLayer is { Visible: true },
            GetOpenScreen = () => IsNewShellEconomyOpen ? "Economy" : _openScreen,
            GetMapMode = () => _activeMapMode,
            IsFlatMap = () => _globe is not null && _globe.IsFlatMap,
            IsShowcasePan = () => _globe?.ShowcasePan ?? false,
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
        ctx.ResolveFlag = FlagTexture;
        ctx.BuildLeaderPortrait = BuildLeaderPortraitBadge;
        ctx.OpenScreen = OpenScreen;
        ctx.ToggleScreen = ToggleCampaignScreen;
        ctx.SetMapMode = mode => { _globe?.SetMapMode(mode); RefreshNewShell(force: true); };
        ctx.ToggleProjection = () => { ToggleProjection(); RefreshNewShell(force: true); };
        ctx.ToggleShowcasePan = () => { ToggleShowcasePan(); _shell?.MapBar.Refresh(); };
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
        ctx.MergeSelectedArmies = () => OpenScreen("Military");
        ctx.ClearArmySelection = ClearArmySelectionAndClose;
        ctx.ClearRegionSelection = ClearRegionSelection;
        ctx.RefreshAll = () => { Refresh(); RefreshNewShell(force: true); };
        ctx.OpenDealInbox = OpenDealInbox;
        ctx.GetIncomingDiplomaticCount = CountIncomingDiplomaticInbox;
        ctx.DelegatePortfolio = DelegatePortfolio;
        ctx.OpenDoctrineScreen = OpenDoctrineScreen;
        ctx.OpenDiplomacyFor = nation =>
        {
            if (nation is null) { OpenScreen("Diplomacy"); return; }
            _selectedNation = nation;
            _diplomacySelectedId = nation.Id;
            _globe?.SetMapModeSubject(nation);
            OpenScreen("Diplomacy");
        };
        ctx.OpenOrganizations = () => { _conflictTab = 4; OpenScreen("Conflicts"); };
        ctx.OpenPeaceConferenceFor = OpenDiplomaticPeace;
        ctx.OpenWarDeclarationFor = nation => { if (nation is not null) OpenWarDeclaration(nation, -1); };
        ctx.OpenUltimatumFor = nation => { if (nation is not null) OpenUltimatumComposer(nation); };
        ctx.SendOrRecallMission = SendOrRecallDiplomaticMission;
        ctx.ToggleObserverMode = ToggleObserverMode;
        ctx.WatchWorld = WatchWorld;
        ctx.GetObservedNation = () => _observerWatched;
        ctx.IsWatchingWorld = () => WatchingWorld;
        ctx.GetObserverTransientNote = () => _observerTransientNote;
        ctx.OpenGrandProjects = OpenNewShellGrandProjects;
    }

    private void UndoLastBatchForShell()
    {
        if (_campaign?.Player is null || !BatchActionUndo.TryUndo(_campaign, _campaign.Player.Id, out _)) return;
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
        if (IsNewShellGrandProjectsOpen) CloseNewShellGrandProjects();
        HideLegacyLeftAndBottomCards();
        _shell?.WorkPanel.Close();
        _openScreen = "Economy";
        _newShellRoot?.MoveToFront();
        _shell?.EconomyWorkspace.Open();
        SyncNewShellModalState();
        SyncNewShellLeftAndBottomDock();
        RefreshNewShell(force: true);
    }

    internal void CloseNewShellEconomyWorkspace()
    {
        _shell?.EconomyWorkspace.Close();
        if (_openScreen == "Economy") _openScreen = "";
        SyncNewShellModalState();
        SyncNewShellLeftAndBottomDock();
    }

    internal void OpenNewShellGrandProjects()
    {
        if (!_newShellBuilt) BuildNewShell();
        CloseVisibleCampaignScreen();
        if (IsNewShellEconomyOpen) CloseNewShellEconomyWorkspace();
        HideLegacyLeftAndBottomCards();
        _shell?.WorkPanel.Close();
        _openScreen = "GrandProjects";
        _newShellRoot?.MoveToFront();
        _shell?.GrandProjects.Open();
        SyncNewShellModalState();
        SyncNewShellLeftAndBottomDock();
        RefreshNewShell(force: true);
    }

    internal void CloseNewShellGrandProjects()
    {
        _shell?.GrandProjects.Close();
        if (_openScreen == "GrandProjects") _openScreen = "";
        SyncNewShellModalState();
        SyncNewShellLeftAndBottomDock();
    }

    internal void OpenNewShellCountryDossier(Nation nation)
    {
        if (!_newShellBuilt) BuildNewShell();
        if (IsNewShellEconomyOpen) CloseNewShellEconomyWorkspace();
        if (IsNewShellGrandProjectsOpen) CloseNewShellGrandProjects();
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
        if (IsNewShellGrandProjectsOpen) CloseNewShellGrandProjects();
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
        _newShellModalWasOpen = true;
        _shell?.SetModalObscured(true);
        SyncNewShellLeftAndBottomDock();
        layer?.MoveToFront();
    }

    internal void FitNewShellWorkspaceLayer(Control layer) => _shell?.FitWorkspaceLayer(layer);

    internal Control BuildDiplomacyLeaderBadge(Nation nation, LeaderProfile hos, int width, int height)
    {
        if (ShellOrchestrator.HasRealLeaderPortrait(hos) || _shell is null) return BuildLeaderPortraitBadge(hos, nation.Color, width, height);
        return _shell.BuildStateSealBadge(_campaign, nation, width, height);
    }

    private void ToggleMapRegionIconsSetting()
    {
        if (IsNewShellActive && _shell is not null)
        {
            _shell.Context.ExplicitRegionIconsToggle = !_shell.Context.ExplicitRegionIconsToggle;
            SettingsCatalog.Store("map_region_icons", _shell.Context.ExplicitRegionIconsToggle);
        }
        else SettingsCatalog.Store("map_region_icons", !SettingsCatalog.Flag("map_region_icons"));
    }

    private bool TryOpenNewShellScreen(string screen)
    {
        if (!IsNewShellActive) return false;
        if (screen == "Economy") { OpenNewShellEconomyWorkspace(); UpdateScreenButtons(); return true; }
        if (screen == "Nation") { OpenNewShellCountryDossier(_selectedNation ?? _campaign.Player); UpdateScreenButtons(); return true; }
        return false;
    }

    private void SyncPlanningBarDock()
    {
        if (IsNewShellActive) { SyncNewShellLeftAndBottomDock(); return; }
        if (!IsInstanceValid(_armyBar)) return;
        var left = _armyBar.OffsetLeft;
        if (Math.Abs(_planningBar.OffsetLeft - left) > 0.5f)
        {
            _planningBar.OffsetLeft = left;
            _planningBar.OffsetRight = left + _planningBar.GetCombinedMinimumSize().X;
        }
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

        var modalOpen = IsAnyShellModalOpen();
        if (modalOpen)
        {
            if (_planningBar is { Visible: true } && IsInstanceValid(_planningBar))
                _planningBar.Visible = false;
            if (_armyPanel is { Visible: true } && IsInstanceValid(_armyPanel))
                _armyPanel.Visible = false;
        }

        var anyLeft = !modalOpen && ((_armyPanel is { Visible: true }) || (_sidePanel is { Visible: true }) ||
                      (_fleetPanel is { Visible: true }) || (_countryCard is { Visible: true }) || _shell.WorkPanel.IsVisible);
        var pbarW = _planningBar is not null && IsInstanceValid(_planningBar) ? Math.Max(340f, _planningBar.GetCombinedMinimumSize().X) : 520f;
        var dock = ShellDockRules.ComputeLeftAndBottomDock(profile, anyLeft, modalOpen, pbarW);
        var leftRect = dock.LeftPanel with { X = profile.LeftPanel.X };
        if (profile.Compact)
        {
            var maxRight = Math.Min(profile.NewsCard.X - 12f, profile.AlertColumn.X - 12f);
            var cappedW = Math.Max(240f, Math.Min(leftRect.W, maxRight - leftRect.X));
            leftRect = leftRect with { W = cappedW };
        }

        PlaceLeftPanelIfVisible(_armyPanel, leftRect);
        PlaceLeftPanelIfVisible(_sidePanel, leftRect);
        PlaceLeftPanelIfVisible(_fleetPanel, leftRect);
        PlaceLeftPanelIfVisible(_countryCard, leftRect);

        _shell.WorkPanel.ApplyDock(profile, dock with { LeftPanel = leftRect });
        _shell.Strip.ApplyDock(profile, dock);
        if (!modalOpen && _planningBar is not null && IsInstanceValid(_planningBar) && _planningBar.Visible)
            ShellContext.PlaceRect(_planningBar, dock.PlanningBar);
    }

    private static void PlaceLeftPanelIfVisible(Control lp, ShellRect rect)
    {
        if (lp is { Visible: true } && IsInstanceValid(lp))
        {
            lp.ClipContents = true;
            ShellContext.PlaceRect(lp, rect);
        }
    }

    private static bool LeftPanelDrifted(Control lp, float expectedX, bool compact, float compactRightCap) =>
        lp is { Visible: true } && IsInstanceValid(lp) &&
        (MathF.Abs(lp.OffsetLeft - expectedX) > 2f || (compact && lp.OffsetRight > compactRightCap));

    private void RebuildClassicChromeCache()
    {
        _classicChromeCache.Clear();
        _classicChromeCached = true;
        if (_hudLayer is null) return;
        var seen = new System.Collections.Generic.HashSet<Control>();
        foreach (var name in ClassicChromeNodeNames)
        {
            if (_hudLayer.FindChild(name, true, false) is Control node && IsInstanceValid(node) && seen.Add(node))
                _classicChromeCache.Add((node, name is not ("MapLayerDrawer" or "HudResourceDrawer" or "SituationDesk")));
        }
        AddClassicNode(_headlineStack);
        AddClassicNode(_alertListPanel);
        AddClassicNode(_outliner);
        AddClassicNode(_hudTopPanel);
        AddClassicNode(_hudFlagMount);
        AddClassicNode(_hudDatePlate);
        AddClassicNode(_mapControlsPanel);
        AddClassicNode(_mapModeDrawerLayer, showWhenClassic: false);
        AddClassicNode(_armyBar);
        void AddClassicNode(Control c, bool showWhenClassic = true)
        {
            if (c is not null && IsInstanceValid(c) && seen.Add(c))
                _classicChromeCache.Add((c, showWhenClassic));
        }
    }

    private void ApplyUiShellSetting()
    {
        if (_hudLayer is null) return;
        _uiShellCacheFrame = ulong.MaxValue;
        _lastModalSyncFrame = ulong.MaxValue;
        if (!_newShellBuilt) BuildNewShell();
        var isNew = IsNewShellActive;
        if (!isNew) _classicChromeCached = false;
        SetClassicChromeVisible(!isNew);
        if (_newShellRoot is not null && IsInstanceValid(_newShellRoot))
        {
            _newShellRoot.Visible = isNew;
            _newShellRoot.ProcessMode = isNew ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_globe is not null && !isNew) _globe.NewShellVisibleArmyIds = null;
        if (isNew) { CloseMapLayerDrawer(); _shell?.ResetCadence(); LayoutNewShell(); RefreshNewShell(force: true); }
        else { CloseShellTransientPopovers(); _shell?.WorkPanel.Close(); CloseNewShellEconomyWorkspace(); CloseNewShellGrandProjects(); LayoutCampaignShell(); Refresh(); }
    }

    private void SetClassicChromeVisible(bool visible)
    {
        if (_hudLayer is null) return;
        if (!_classicChromeCached) RebuildClassicChromeCache();
        _classicChromeLastVisible = visible;
        var mode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        for (var i = 0; i < _classicChromeCache.Count; i++)
        {
            var (node, showWhenClassic) = _classicChromeCache[i];
            if (!IsInstanceValid(node)) continue;
            var targetVis = visible && showWhenClassic;
            if (node.Visible != targetVis) node.Visible = targetVis;
            if (node.ProcessMode != mode) node.ProcessMode = mode;
        }
        if (!visible && _mapModeDrawerLayer is not null && IsInstanceValid(_mapModeDrawerLayer))
        {
            if (_mapModeDrawerLayer.Visible) _mapModeDrawerLayer.Visible = false;
            if (_mapModeDrawerLayer.ProcessMode != mode) _mapModeDrawerLayer.ProcessMode = mode;
        }
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
        if (_classicChromeLastVisible != false) SetClassicChromeVisible(false);
        SyncNewShellModalState(verifiedActive: true);
        _shell?.Refresh(_campaign, _speed, force);
        if (force) SyncNewShellLeftAndBottomDock();
    }

    private void TickNewShell(double delta, bool allowLiveReads = true)
    {
        if (!_newShellBuilt || !IsNewShellActive || _campaign is null || _hudLayer is not { Visible: true } || _shell is null) return;
        if (_lastModalSyncFrame != Engine.GetProcessFrames())
            SyncNewShellModalState(verifiedActive: true);
        var anyLegacyLeft = (_armyPanel is { Visible: true }) || (_sidePanel is { Visible: true }) ||
                            (_fleetPanel is { Visible: true }) || (_countryCard is { Visible: true });
        var anyLeft = anyLegacyLeft || _shell.WorkPanel.IsVisible;
        var leftDrifted = false;
        if (anyLegacyLeft)
        {
            var profile = _shell.CurrentProfile();
            var compactRightCap = profile.Compact ? Math.Min(profile.NewsCard.X, profile.AlertColumn.X) - 10f : float.PositiveInfinity;
            leftDrifted = LeftPanelDrifted(_armyPanel, profile.LeftPanel.X, profile.Compact, compactRightCap) ||
                          LeftPanelDrifted(_sidePanel, profile.LeftPanel.X, profile.Compact, compactRightCap) ||
                          LeftPanelDrifted(_fleetPanel, profile.LeftPanel.X, profile.Compact, compactRightCap) ||
                          LeftPanelDrifted(_countryCard, profile.LeftPanel.X, profile.Compact, compactRightCap);
        }
        if (anyLeft != _newShellLastLeftVisible || leftDrifted)
        {
            _newShellLastLeftVisible = anyLeft;
            SyncNewShellLeftAndBottomDock();
        }
        _shell.Tick(delta, allowLiveReads);
    }

    private ulong _cachedModalOpenFrame = ulong.MaxValue;
    private bool _cachedModalOpenValue;

    private void SyncNewShellModalState(bool verifiedActive = false)
    {
        if (!_newShellBuilt || (!verifiedActive && !IsNewShellActive) || _shell is null) return;
        var frame = Engine.GetProcessFrames();
        _lastModalSyncFrame = frame;
        var fullCover = IsFullCoverModalOpen();
        if (_newShellRoot is not null && IsInstanceValid(_newShellRoot))
        {
            var wantRoot = !fullCover && _hudLayer is { Visible: true };
            if (_newShellRoot.Visible != wantRoot) _newShellRoot.Visible = wantRoot;
        }
        var modalOpen = EvaluateAnyShellModalOpen();
        _cachedModalOpenFrame = frame;
        _cachedModalOpenValue = modalOpen;
        if (modalOpen != _newShellModalWasOpen)
        {
            _newShellModalWasOpen = modalOpen;
            _shell.SetModalObscured(modalOpen);
            SyncNewShellLeftAndBottomDock();
            if (!modalOpen)
            {
                RefreshArmyPanel();
                RefreshPlanningBar();
            }
        }
        if (modalOpen)
        {
            if (_planningBar is { Visible: true } && IsInstanceValid(_planningBar))
                _planningBar.Visible = false;
            if (_armyPanel is { Visible: true } && IsInstanceValid(_armyPanel))
                _armyPanel.Visible = false;
        }
    }

    private bool IsFullCoverModalOpen() =>
        _pauseMenuLayer is { Visible: true } || _settingsLayer is { Visible: true } ||
        _saveLoadLayer is { Visible: true } || _pickerLayer is { Visible: true } ||
        _handoverLayer is { Visible: true } || _mediaLibraryLayer is { Visible: true };

    private void RefreshNewShellLocalised()
    {
        if (!_newShellBuilt || _shell is null) return;
        _shell.ResetCadence();
        LayoutNewShell();
        RefreshNewShell(force: true);
    }

    internal void CloseShellTransientPopovers(bool includeNews = true) => _shell?.CloseTransientPopovers(includeNews);

    private bool IsAnyShellModalOpen()
    {
        var frame = Engine.GetProcessFrames();
        if (frame == _cachedModalOpenFrame) return _cachedModalOpenValue;
        var open = EvaluateAnyShellModalOpen();
        _cachedModalOpenFrame = frame;
        _cachedModalOpenValue = open;
        return open;
    }

    private bool EvaluateAnyShellModalOpen() =>
        IsNewShellEconomyOpen || IsNewShellGrandProjectsOpen || _handbookLayer is { Visible: true } || _peaceLayer is { Visible: true } ||
        _peaceOfferLayer is { Visible: true } || _defconLayer is { Visible: true } || _pauseMenuLayer is { Visible: true } ||
        _settingsLayer is { Visible: true } || _saveLoadLayer is { Visible: true } || _pickerLayer is { Visible: true } ||
        _stateAffairsLayer is { Visible: true } || _sovereignFinanceLayer is { Visible: true } || _ledgerLayer is { Visible: true } ||
        _warLedgerLayer is { Visible: true } || _diplomacyLayer is { Visible: true } || _conflictsLayer is { Visible: true } ||
        _policyLayer is { Visible: true } || _tradeScreenLayer is { Visible: true } || _productionLayer is { Visible: true } ||
        _supportLayer is { Visible: true } || _researchLayer is { Visible: true } || _doctrineLayer is { Visible: true } ||
        _recruitmentLayer is { Visible: true } || _situationLayer is { Visible: true } || _eventLayer is { Visible: true } ||
        _handoverLayer is { Visible: true } || _countryRankingsLayer is { Visible: true } || _navigatorLayer is { Visible: true } ||
        _subjectsModalLayer is { Visible: true } || _releaseModalLayer is { Visible: true } || _mediaLibraryLayer is { Visible: true } ||
        _dealComposer is { Visible: true } || _ultimatumComposer is { Visible: true } || _warDeclaration is { Visible: true } ||
        _allianceProposalReview is { Visible: true } || _nuclearStrikeComposer is { Visible: true };

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
        // 1) Mandatory dialogs block Pause.
        if (_handoverLayer is { Visible: true } || _eventLayer is { Visible: true }) return true;

        // 2) Close topmost system modal if visible.
        if (_mediaLibraryLayer is { Visible: true }) { CloseMediaLibrary(); return true; }
        if (_settingsLayer is { Visible: true }) { HideSettings(); return true; }
        if (_saveLoadLayer is { Visible: true }) { HideSaveLoadInterface(); return true; }
        if (_networkLayer is { Visible: true }) { CloseNetworkMultiplayer(); return true; }
        if (_handbookLayer is { Visible: true }) { CloseHandbook(); return true; }
        if (_peaceLayer is { Visible: true }) { ClosePeaceConference(); return true; }
        if (_peaceOfferLayer is { Visible: true }) { ClosePeaceOffer(); return true; }
        if (_defconLayer is { Visible: true }) { CloseDefconOverview(); return true; }
        if (_pauseMenuLayer is { Visible: true }) { HidePauseMenu(); return true; }

        // 3) Close topmost composer / popup if visible.
        if (IsInstanceValid(_nuclearStrikeComposer) && _nuclearStrikeComposer.Visible) { CloseNuclearStrikeReview(); return true; }
        if (IsInstanceValid(_warDeclaration) && _warDeclaration.Visible) { CloseWarDeclaration(); return true; }
        if (IsInstanceValid(_allianceProposalReview) && _allianceProposalReview.Visible) { CloseAllianceProposalReview(); return true; }
        if (IsInstanceValid(_dealComposer) && _dealComposer.Visible) { CloseDealComposer(); return true; }
        if (IsInstanceValid(_ultimatumComposer) && _ultimatumComposer.Visible) { CloseUltimatumComposer(); return true; }
        if (_countryRankingsLayer is { Visible: true }) { CloseCountryRankings(); return true; }
        if (_subjectsModalLayer is { Visible: true }) { CloseSubjectsModal(); return true; }
        if (_releaseModalLayer is { Visible: true }) { CloseReleaseCountryModal(); return true; }
        if (_recruitmentLayer is { Visible: true }) { _recruitmentLayer.Hide(); return true; }
        if (_situationLayer is { Visible: true }) { CloseSituationRoom(); return true; }

        // 4) Close shell popovers & panels (Escape NEVER closes the News Card).
        if (_shell.Messages.IsVisible) { _shell.Messages.Close(); return true; }
        if (_shell.MapBar.IsPopoverOpen) { _shell.MapBar.ClosePopover(); return true; }
        if (_shell.GrandProjects.IsVisible) { CloseNewShellGrandProjects(); return true; }
        if (_shell.EconomyWorkspace.IsVisible) { CloseNewShellEconomyWorkspace(); return true; }
        if (_shell.WorkPanel.IsVisible) { CloseNewShellWorkPanel(unpinnedOnly: false); return true; }
        if (CloseVisibleCampaignScreen()) return true;
        if (_splitMovePopup is { IsPopupOpen: true }) { _splitMovePopup.ClosePopup(); return true; }
        if (_quickRecruitLayer is { Visible: true }) { CloseQuickRecruitPopup(); return true; }
        if (_provinceCardLayer is not null && IsInstanceValid(_provinceCardLayer) && _provinceCardLayer.Visible) { CloseProvinceCard(); return true; }
        if (_compactProvincePopover is { Visible: true }) { CloseCompactProvincePopover(); return true; }
        if (_fleetPanel is { Visible: true }) { CloseFleetPanel(); SyncNewShellLeftAndBottomDock(); return true; }
        if (_armyPanel is { Visible: true }) { ClearArmySelectionAndClose(); if (_supportSelected.Count > 0) ClearSupportMapSelection(); SyncNewShellLeftAndBottomDock(); return true; }
        if (_movementPanel is { Visible: true }) { CloseMovementPanel(); return true; }
        if (_sidePanel is { Visible: true }) { if (_dossierReturnArmyIds is not null) ReturnToArmyOrders(); else CloseSidePanel(); SyncNewShellLeftAndBottomDock(); return true; }

        // 5) Only if none of the above were visible -> Pause menu.
        ShowPauseMenu();
        return true;
    }
}
