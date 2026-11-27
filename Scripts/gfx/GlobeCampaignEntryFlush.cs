using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Immediate Frame-0 visual flush on campaign entry (new campaign from Country Picker,
/// hotseat seat handover, observer start, scenario start, or save load).
/// Bypasses incremental frame-budget deferral ONLY for that single campaign-entry call
/// while keeping all ongoing gameplay / Speed 7-8 pacing and throttles intact.
/// </summary>
public sealed partial class GlobePresentation
{
    private bool _campaignEntryFlushActive;
    private int _lastLockFreeBattleViewer = -999;

    private double ActiveLabel3dFrameBudgetMs() =>
        _campaignEntryFlushActive ? double.PositiveInfinity : Label3dFrameBudgetMs;

    /// <summary>Target unified camera distance (before lerp settles), used for campaign-entry framing.</summary>
    internal float TargetViewDistance => _flatMap ? _targetFlatZoom * 1.6f - 1.04f : _targetDistance;

    /// <summary>
    /// Synchronously builds and settles all campaign visuals on Frame 0 so armies, fleets,
    /// long-range support units, battles, war fronts, fog of war, capitals, cities, country/region
    /// labels, region icons, and movement headquarters are already drawn on the very first frame.
    /// </summary>
    internal void FlushCampaignEntryVisuals(BackgroundSimRunner simRunner, bool snapCamera = true)
    {
        if (_campaign is null || !_mapApplied || !IsInsideTree()) return;
        ResetRevisionTrackingIfCampaignChanged(_campaign);
        NewShellVisibleArmyIds = null;

        if (snapCamera && !_freeCamera)
            SnapCameraToTargetImmediate();
        ApplyCamera();
        if (IsInstanceValid(_orbit) && _orbit.IsInsideTree()) _orbit.ForceUpdateTransform();
        if (IsInstanceValid(_camera) && _camera.IsInsideTree()) _camera.ForceUpdateTransform();
        if (_flatMap) UpdateFlatWorldPositions();
        UpdateShaderZoomAndSurfaceState(0.0);

        ResetCampaignEntryVisualGates();

        _campaignEntryFlushActive = true;
        try
        {
            if (simRunner is { IsActive: true })
            {
                var presentedVer = SyncLockFreeWorldVisuals(simRunner);
                if (presentedVer > 0) simRunner.NotifyVisualFramePresented(presentedVer);
            }

            if (_campaign.TryEnterRead(250, requestWindowOnMiss: true))
            {
                try { FlushCampaignEntryUnderReadLock(_campaign); }
                finally { _campaign.ExitRead(); }
            }
        }
        finally
        {
            _campaignEntryFlushActive = false;
        }

        _militaryVisualAccumulator = 0.0;
        _routeVisualAccumulator = 0.0;
        _visualClock = 0.0;
        _sinceLastFencedSync = 0.0;
        _lastFencedSyncFrame = Engine.GetProcessFrames();
        QueueRedraw();
        WakeGlobeRendering();
    }

    private void SnapCameraToTargetImmediate()
    {
        _flatGlide = Vector2.Zero;
        _orbitGlide = Vector2.Zero;
        _flatZoomAnchorActive = false;
        _globeZoomAnchorActive = false;
        _yaw = _targetYaw;
        _pitch = _targetPitch;
        _distance = _targetDistance;
        _flatX = _targetFlatX;
        _flatY = _targetFlatY;
        _flatZoom = _targetFlatZoom;
        _cameraTransformDirty = true;
    }

    private void ResetCampaignEntryVisualGates()
    {
        _militaryVisualAccumulator = 1.0;
        _routeVisualAccumulator = 1.0;
        _visualClock = 1.0;
        _sinceLastFencedSync = 1.0;
        _labelRefreshTimer = 1.0;

        _lastFogCheckMsec = 0;
        _lastFogMapRevision = int.MinValue;
        _uploadedVision = null;
        _uploadedVisionRevision = -1;
        _uploadedVisionViewer = -1;
        _hasUploadedVisionBytes = false;
        _cachedFogViewerParam = -999;
        _fogViewer = -999;

        _lastLockFreeVisualVersion = -1;
        _lastLockFreeArmyVersion = -1;
        _lastLockFreeArmyHours = -1;
        _lastLockFreeArmyMapRev = -1;
        _lastLockFreeBattleHours = -1;
        _lastLockFreeBattleCount = -1;
        _lastLockFreeBattleViewer = -999;
        _lockFreeArmyStructureFingerprint = int.MinValue;
        _hydratedLockFreeArmyVersion = -1;
        _lockFreeLabelBudgetPending = false;

        _armyStructureFingerprint = int.MinValue;
        _militaryPresentationFingerprint = int.MinValue;
        _battleMarkerDay = -1;
        _battleMarkerCount = -1;
        _battleViewer = -1;
        _lastCombatSyncHours = -1;

        _fleetRosterFingerprint = int.MinValue;
        _fleetUpdateStateKey = int.MinValue;

        _supportPresentationDelay = 1.0;
        _supportMarkedCampaign = null;
        _supportRosterFingerprint = int.MinValue;
        _supportRangeFingerprint = int.MinValue;
        _supportRangeFlatX = float.NaN;
        _nextSupportRangeRefreshMs = 0;

        _routeFingerprint = int.MinValue;
        _fleetRouteFingerprint = int.MinValue;
        _nextSurfaceDensityMs = 0;
        _nextCounterDensityMs = 0;
        _counterDensityDirty = true;
        _lastCounterScaleDistance = -1f;
        _lastCounterScaleFlatZoom = -1f;

        _cityDirty = true;
        _cityClock = 2.0;
        _cityLastProcessFrame = ulong.MaxValue;
        _cityLastClockFrame = ulong.MaxValue;
        _cityLastFadeFrame = ulong.MaxValue;

        _countryVisLastProcessFrame = ulong.MaxValue;
        _countryVisAllSettled = false;
        _countryVisNeedsInitialSync = true;
        _regionLastProcessFrame = ulong.MaxValue;
        _regionLastDeltaFrame = ulong.MaxValue;
        _regionVisSettled = false;

        _badgeLayoutDue = true;
        _badgeStill = 1.0;
        _badgeDataAge = 10.0;
        _badgeVisualSyncFrame = ulong.MaxValue;
        _overlayHadValidPose = false;
    }

    private void FlushCampaignEntryUnderReadLock(Simulation.Campaign campaign)
    {
        var fogViewer = FogOfWarActive && campaign.Player is { IsHuman: true } ? campaign.Player.Id : -1;
        _fogViewer = fogViewer;
        _lastMapModeDay = -100;
        var fogViewerParam = FogOfWarActive && campaign.Player is { IsHuman: true } ? campaign.Player.MapIndex : -1;
        _cachedFogViewerParam = fogViewerParam;
        _globeMaterial?.SetShaderParameter("fog_viewer", fogViewerParam);
        UpdateFogTint();
        if (FogOfWarActive && campaign.Player is { IsHuman: true } && campaign.ProvinceMap is not null)
            UpdateFogVisibility();

        if (_controlRevision != campaign.MapRevision)
            RefreshProvinceControllersCore();
        if (_frontGeometryDirty || _frontWarRevision != campaign.WarRevision ||
            _frontRevision != campaign.MapRevision || _frontPreviewDeferred)
        {
            BuildFrontPreviewCore();
            _frontRevision = campaign.MapRevision;
            _frontWarRevision = campaign.WarRevision;
            _frontPreviewDeferred = false;
        }
        if (IsInstanceValid(_frontPreview))
            _frontPreview.Visible = MilitaryMarkersEnabled && _frontMesh.GetSurfaceCount() > 0;

        if (MilitaryMarkersEnabled)
        {
            if (_lastLockFreeArmyVersion > 0)
            {
                HydrateLockFreeMarkerStacks(campaign);
                UpdateBattleMarkers(forceNow: true);
            }
            else
            {
                SyncArmyMarkers();
                UpdateMilitaryPresentation(forceNow: true);
            }
            SnapAllArmyMarkersToTarget();
        }
        RefreshArmyMarkerVisibility();
        SyncBattleMarkerCameraState();

        if (_capitalRevision != campaign.CapitalRevision ||
            _capitalDestroyedCount != campaign.DestroyedProvinces.Count ||
            _capitalMarkers.Count == 0)
            BuildCapitalMarkers(campaign);
        UpdateMarkerVisibility(_capitalRoot, _capitalMarkers,
            ViewDistance <= 13.8f && Simulation.WorldLayers.On("capitals"), CityHorizon());

        if (MilitaryMarkersEnabled)
            UpdateFleetMarkers();
        var fleetHorizon = _flatMap ? 0f : GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length()) + 0.002f;
        UpdateMarkerVisibility(_fleetRoot, _fleetMarkers,
            MilitaryMarkersEnabled && (_flatMap || ViewDistance <= 13.5f), fleetHorizon);

        UpdateCounterScale(force: true);
        _lastCounterScaleFlatMap = _flatMap;
        _lastCounterScaleDistance = ViewDistance;
        _lastCounterScaleFlatZoom = _flatZoom;

        UpdateSupportPresentation(0.35);
        if (MilitaryMarkersEnabled)
        {
            UpdateCounterScale(force: true);
            if (_flatMap) UpdateFlatCounterDensity();
            else UpdateSurfaceCounterDensity();
            LayoutSupportCounters();
        }

        UpdateRoutePreview();
        UpdateSeaLiftPreview();
        UpdateSupplyLines();
        UpdateStraitCrossings();

        var drainGuard = 0;
        while (_deferredLabelAnchors is { } deferred && drainGuard++ < 256)
            BuildCountryLabels(deferred, _deferredLabelNations);
        if (_flatMap) UpdateFlatWorldPositions();

        _cityLastProcessFrame = ulong.MaxValue;
        UpdateCityLabels(5.0, CurrentRenderSnapshot(), allowCampaignRead: true);
        StepCityPinFades(5.0f);
        if (CurrentShaderQualityTier() < 0.5f)
            UpdateSettlementModels(1.0);

        _countryVisLastProcessFrame = ulong.MaxValue;
        _countryVisNeedsInitialSync = true;
        UpdateCountryLabelVisibility(5.0, campaign.Player?.MapIndex ?? -1);

        _regionLastProcessFrame = ulong.MaxValue;
        _regionLastDeltaFrame = ulong.MaxValue;
        UpdateRegionLabels(5.0);

        _movementLayer?.FlushImmediate(campaign, _camera);

        if (RegionIconsWanted)
        {
            KeepRegionIcons(0.25);
            KeepRegionIconsVisualSync(5.0);
        }

        if (_mapMode is not (StrategicMapMode.Political or StrategicMapMode.Terrain or StrategicMapMode.Relief))
            UpdateMapModePaletteIfNeeded();
    }

    private void SnapAllArmyMarkersToTarget()
    {
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node) || !marker.CachedGeoValid) continue;
            var pos = ArmyMarkerPositionFromCachedGeo(marker);
            marker.TargetPosition = pos;
            marker.DisplayPosition = pos;
            marker.Node.Position = pos;
            marker.HasPendingArrivalGlide = false;
            marker.MotionSettled = true;
        }
    }

    private sealed partial class MovementLayer
    {
        internal void FlushImmediate(Simulation.Campaign campaign, Camera3D camera)
        {
            if (_globe is null || campaign is null || !IsInstanceValid(camera)) return;
            _syncCooldown = 0;
            _sinceLastMovementSync = 0;
            _flagPoll = 0;
            _revision = -1;
            _lastCameraSyncProcessFrame = ulong.MaxValue;
            if (_globe.CampaignInView?.Invoke() == false || _globe.ViewDistance > MedallionDistance)
            {
                HideLayer();
                return;
            }
            Visible = true;
            SyncUnderReadLock(campaign, camera, fastSim: false);
            if (_pendingRoundels) FlushPendingRoundels();
        }
    }
}
