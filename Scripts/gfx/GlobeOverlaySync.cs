using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Frame-0, lock-free synchronization for camera-locked map overlays (cities, rivers,
/// capital stars, country/region labels, military/fleet/battle/movement markers, and 2D _Draw() badges).
/// </summary>
public sealed partial class GlobePresentation
{
    private bool _overlayHadValidPose;
    private Transform3D _overlayLastCamTransform;
    private float _overlayLastCamSize = -1f;
    private float _overlayLastDistance = -1f;
    private float _overlayLastFlatZoom = -1f;
    private bool _overlayLastFlatMap;
    private Vector2I _overlayLastViewport;

    private ulong _cityLastProcessFrame = ulong.MaxValue;
    private ulong _cityLastClockFrame = ulong.MaxValue;
    private ulong _cityLastFadeFrame = ulong.MaxValue;
    private Basis _lastCityCamBasis = Basis.Identity;
    private int _citySnapPlayerMapIndex = -1;
    private int _citySnapOwnerRev = -1;
    private ushort[] _citySnapOwners = Array.Empty<ushort>();
    private Campaign _cityDestroyedCacheCampaign;
    private int _cityDestroyedCacheCount = -1;
    private HashSet<int> _cityDestroyedProvincesCache;

    private ulong _countryVisLastProcessFrame = ulong.MaxValue;
    private ulong _regionLastProcessFrame = ulong.MaxValue;
    private ulong _regionLastDeltaFrame = ulong.MaxValue;
    private Vector3 _regionLastLayoutCamNormal;

    private ulong _badgeVisualSyncFrame = ulong.MaxValue;
    private bool _badgeLastFlatMap;
    private bool _badgeMovedSinceLayoutTick;

    partial void SyncCameraLockedMapOverlays(double delta, CampaignRenderSnapshot snap)
    {
        if (!_mapApplied || !IsInsideTree() || !IsInstanceValid(_camera) || !_camera.IsInsideTree()) return;
        if (IsInstanceValid(_orbit) && _orbit.IsInsideTree()) _orbit.ForceUpdateTransform();
        _camera.ForceUpdateTransform();

        var camTransform = _camera.GlobalTransform;
        var viewport = ViewportSize();
        var projectionSwitched = !_overlayHadValidPose || _overlayLastFlatMap != _flatMap;
        var camMoved = projectionSwitched ||
                       _overlayLastViewport != viewport ||
                       camTransform.Origin.DistanceSquaredTo(_overlayLastCamTransform.Origin) > 1e-7f ||
                       !camTransform.Basis.IsEqualApprox(_overlayLastCamTransform.Basis) ||
                       MathF.Abs(_camera.Size - _overlayLastCamSize) > 1e-4f ||
                       MathF.Abs(_distance - _overlayLastDistance) > 1e-4f ||
                       MathF.Abs(_flatZoom - _overlayLastFlatZoom) > 1e-4f;
        var orbitTransformChanged = !_overlayHadValidPose ||
                                    projectionSwitched ||
                                    !camTransform.IsEqualApprox(_overlayLastCamTransform) ||
                                    MathF.Abs(_camera.Size - _overlayLastCamSize) > 1e-4f;

        if (camMoved && !_turboRenderThisFrame)
            WakeGlobeRendering();

        if (camMoved && !_flatMap)
            HideCitiesPastHorizon();

        if (camMoved || projectionSwitched)
        {
            var horizon = _flatMap ? 0f : CityHorizon();
            UpdateMarkerVisibility(_capitalRoot, _capitalMarkers,
                ViewDistance <= 13.8f && WorldLayers.On("capitals"), horizon);
            UpdateMarkerVisibility(_fleetRoot, _fleetMarkers,
                MilitaryMarkersEnabled && (_flatMap || ViewDistance <= 13.5f), horizon);
            RefreshArmyMarkerVisibility();
            if (orbitTransformChanged && (!_flatMap || projectionSwitched))
                UpdateCounterScale();
            SyncBattleMarkerCameraState();
        }

        if (_movementLayer is not null && IsInstanceValid(_movementLayer) &&
            (camMoved || projectionSwitched || _movementLayer.NeedsCameraSync()))
        {
            _movementLayer.SyncCameraLockedTransforms(_camera);
        }

        UpdateCityLabels(delta, snap, allowCampaignRead: false);

        var snapPlayerMapIndex = snap is { Version: > 0 }
            ? snap.PlayerMapIndex
            : (_countryVisCachedPlayer >= -1 ? _countryVisCachedPlayer : -1);
        UpdateCountryLabelVisibility(delta, snapPlayerMapIndex);
        CullRegionLabelsPastHorizon();
        UpdateRegionLabels(delta);

        KeepRegionIconsVisualSync(delta);

        _overlayHadValidPose = true;
        _overlayLastCamTransform = camTransform;
        _overlayLastCamSize = _camera.Size;
        _overlayLastDistance = _distance;
        _overlayLastFlatZoom = _flatZoom;
        _overlayLastFlatMap = _flatMap;
        _overlayLastViewport = viewport;
    }

    private void SyncBattleMarkerCameraState()
    {
        if (!IsInstanceValid(_battleRoot) || !IsInstanceValid(_camera)) return;
        FinishBattleMarkers();
        if (!_battleRoot.Visible) return;
        var flatScale = _flatMap ? _counterWorldPixel * _counterFlatFar * BattleMarkerRadius() * 2f : 0f;
        foreach (var marker in _battleMarkers.Values)
        {
            if (!IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
            var width = _flatMap ? flatScale : SurfaceCounterWidth(marker.Node, "battle");
            if (MathF.Abs(marker.Node.Scale.X - width) > 1e-6f)
                marker.Node.Scale = Vector3.One * width;
        }
    }

    private void CullRegionLabelsPastHorizon()
    {
        if (_flatMap || !IsInsideTree() || !IsInstanceValid(_regionLabelRoot) || !_regionLabelRoot.IsInsideTree() ||
            !IsInstanceValid(_camera) || !_camera.IsInsideTree() || !_regionLabelRoot.Visible)
            return;
        var rootTransform = _regionLabelRoot.GlobalTransform;
        var rootBasis = rootTransform.Basis;
        var cameraNormal = _camera.GlobalPosition.Normalized();
        var horizon = Math.Clamp(CityHorizon() + 0.06f, 0.28f, 0.992f);
        var anyVisible = false;
        for (var i = 0; i < _regionLastUsed && i < _regionLabelPool.Count; i++)
        {
            var slotState = _regionSlotStates[i];
            if (!slotState.Visible) continue;
            var beyond = MapOverlaySyncRules.IsBeyondGlobeHorizon(
                             ToNum(rootBasis * slotState.Normal), ToNum(cameraNormal), horizon) ||
                         (slotState.HasPosition && _camera.IsPositionBehind(rootTransform * slotState.Position));
            if (beyond)
            {
                slotState.Visible = false;
                _regionLabelPool[i].Visible = false;
            }
            else
                anyVisible = true;
        }
        if (_regionLabelRoot.Visible != anyVisible)
            _regionLabelRoot.Visible = anyVisible;
    }

    private bool ShouldDeferMobileRegionLayout(bool mobileTier, bool inMotion, float measure, Vector3 cameraNormal)
    {
        if (!mobileTier || !inMotion || _regionLastUsed <= 0 || _regionLastAlpha <= 0.01f)
            return false;
        if (_flatMap)
        {
            return float.IsFinite(_regionLastFlatX) && float.IsFinite(_regionLastFlatY) &&
                   MathF.Abs(_flatX - _regionLastFlatX) < FlatMapWidth * 0.25f &&
                   MathF.Abs(_flatY - _regionLastFlatY) < _camera.Size * 0.5f;
        }
        var sweepAngle = _regionLastLayoutCamNormal.LengthSquared() > 0.5f && cameraNormal.LengthSquared() > 0.5f
            ? MathF.Acos(Math.Clamp(_regionLastLayoutCamNormal.Dot(cameraNormal), -1f, 1f))
            : float.MaxValue;
        var measureJump = _regionLastMeasure > 0f &&
                          MathF.Abs(measure - _regionLastMeasure) / _regionLastMeasure > 0.18f;
        return sweepAngle <= 0.14f && !measureJump;
    }

    private sealed partial class MovementLayer
    {
        internal bool NeedsCameraSync() => _markers.Count > 0 && _lastCameraSyncProcessFrame == ulong.MaxValue;

        internal void SyncCameraLockedTransforms(Camera3D camera)
        {
            if (_globe is null || !IsInstanceValid(camera)) return;
            if (_globe._campaign is null || _globe.CampaignInView?.Invoke() == false)
            {
                Visible = false;
                _placed.Clear();
                _medallions.Clear();
                return;
            }
            Visible = true;
            _lastCameraSyncProcessFrame = Engine.GetProcessFrames();
            UpdateCachedMarkerTransforms(camera);
        }

        private void UpdateCachedMarkerTransforms(Camera3D camera)
        {
            foreach (var marker in _markers)
            {
                if (!IsInstanceValid(marker.Node)) continue;
                var shown = marker.IsStanding && _globe.ViewDistance <= MedallionDistance;
                if (shown) UpdateSingleMarkerTransform(marker, camera, ref shown);
                if (marker.Node.Visible != shown) marker.Node.Visible = shown;
            }
            Spread(camera);
        }
    }

    private void CullCitiesPastHorizonImmediate()
    {
        if (_flatMap || !IsInsideTree() || !IsInstanceValid(_cityRoot) || !_cityRoot.IsInsideTree() ||
            !IsInstanceValid(_camera) || !_camera.IsInsideTree()) return;
        var camPos = _camera.GlobalPosition;
        var camBasis = _camera.GlobalTransform.Basis;
        const float posTolSq = 1e-6f;
        const float distTol = 1e-4f;
        if (float.IsFinite(_lastCityCamPos.X) &&
            camPos.DistanceSquaredTo(_lastCityCamPos) < posTolSq &&
            MathF.Abs(_distance - _lastCityDistance) < distTol &&
            camBasis.IsEqualApprox(_lastCityCamBasis))
            return;
        _lastCityCamPos = camPos;
        _lastCityDistance = _distance;
        _lastCityCamBasis = camBasis;

        var cameraNormal = camPos.Normalized();
        var rootTransform = _cityRoot.GlobalTransform;
        var rootBasis = rootTransform.Basis;
        var horizon = CityHorizon();
        foreach (var pin in _cityPins)
        {
            if (!pin.CachedNodeVisible) continue;
            if (!TryResolveCityPinNormal(pin, out var pinNormal) ||
                !IsInstanceValid(pin.Node) ||
                MapOverlaySyncRules.IsBeyondGlobeHorizon(ToNum(rootBasis * pinNormal), ToNum(cameraNormal), horizon) ||
                _camera.IsPositionBehind(rootTransform * pin.Node.Position))
            {
                pin.Alpha = 0f;
                pin.TargetAlpha = 0f;
                if (pin.TargetKey != -1) _pinAlphas.Remove(pin.TargetKey);
                SetPinNodeVisible(pin, false);
            }
        }
    }

    private bool TryResolveCityPinNormal(CityPin pin, out Vector3 normal)
    {
        if (pin.City >= 0 && pin.City < _cityNormals.Length)
        {
            normal = _cityNormals[pin.City];
            return true;
        }
        if (pin.AnchorNormal.LengthSquared() > 0.5f)
        {
            normal = pin.AnchorNormal;
            return true;
        }
        var starIndex = -2 - pin.City;
        if (pin.Star is not null && starIndex >= 0 && starIndex < _starNames.Count &&
            IsInstanceValid(pin.Star) && pin.Star.IsVisibleInTree())
        {
            normal = _starNames[starIndex].Normal;
            return true;
        }
        if (pin.Node is not null && IsInstanceValid(pin.Node) && pin.Node.Position.LengthSquared() > 0.5f)
        {
            normal = pin.Node.Position.Normalized();
            return true;
        }
        normal = default;
        return false;
    }

    private void ResolveCitySnapshotContext(
        CampaignRenderSnapshot snap,
        bool allowCampaignRead,
        out int playerMapIndex,
        out ushort[] provinceOwners,
        out int ownerRev)
    {
        if (snap is { Version: > 0 })
        {
            playerMapIndex = snap.PlayerMapIndex;
            provinceOwners = snap.ProvinceOwner;
            ownerRev = snap.OwnerRevision;
            _citySnapPlayerMapIndex = playerMapIndex;
            _citySnapOwners = provinceOwners;
            _citySnapOwnerRev = ownerRev;
            return;
        }
        if (allowCampaignRead && _campaign is not null)
        {
            playerMapIndex = _campaign.Player?.MapIndex ?? -1;
            provinceOwners = _campaign.ProvinceMap?.ProvinceToNation ?? Array.Empty<ushort>();
            ownerRev = _campaign.OwnershipRevision;
            _citySnapPlayerMapIndex = playerMapIndex;
            _citySnapOwners = provinceOwners;
            _citySnapOwnerRev = ownerRev;
            return;
        }
        playerMapIndex = _citySnapPlayerMapIndex;
        provinceOwners = _citySnapOwners;
        ownerRev = _citySnapOwnerRev;
    }

    private void SyncCityDestroyedCache(CampaignRenderSnapshot snap, bool allowCampaignRead)
    {
        if (!ReferenceEquals(_cityDestroyedCacheCampaign, _campaign))
        {
            _cityDestroyedCacheCampaign = _campaign;
            _cityDestroyedCacheCount = -1;
            _cityDestroyedProvincesCache?.Clear();
        }
        if (snap is { Version: > 0, DestroyedProvinceCount: 0 })
        {
            if (_cityDestroyedCacheCount != 0)
            {
                _cityDestroyedCacheCount = 0;
                _cityDestroyedProvincesCache?.Clear();
            }
            return;
        }
        if (!allowCampaignRead || _campaign is null) return;
        var count = _campaign.DestroyedProvinces.Count;
        if (count == 0)
        {
            _cityDestroyedCacheCount = 0;
            _cityDestroyedProvincesCache?.Clear();
            return;
        }
        if (_cityDestroyedCacheCount == count && _cityDestroyedProvincesCache is not null) return;
        _cityDestroyedProvincesCache ??= new HashSet<int>(count);
        _cityDestroyedProvincesCache.Clear();
        foreach (var p in _campaign.DestroyedProvinces)
            _cityDestroyedProvincesCache.Add(p);
        _cityDestroyedCacheCount = count;
    }

    private bool CityPinDestroyed(float latitude, float longitude)
    {
        if (_cityDestroyedCacheCount <= 0 || _cityDestroyedProvincesCache is null || _provinceTopology is null)
            return false;
        var x = Math.Clamp((int)((longitude + 180f) / 360f * _provinceTopology.Width), 0, _provinceTopology.Width - 1);
        var y = Math.Clamp((int)((90f - latitude) / 180f * _provinceTopology.Height), 0, _provinceTopology.Height - 1);
        return _cityDestroyedProvincesCache.Contains(_provinceTopology.CellToProvince[y * _provinceTopology.Width + x]);
    }

    private bool ShouldRunCountryVisibilityThisFrame()
    {
        if (!IsInstanceValid(_countryLabelRoot) || !IsInstanceValid(_camera)) return false;
        var frame = Engine.GetProcessFrames();
        if (_countryVisLastProcessFrame == frame && !_countryVisNeedsInitialSync)
            return false;
        _countryVisLastProcessFrame = frame;
        return true;
    }

    private bool TryPrepareMarkerVisibilityRoot(
        Node3D root, bool withinLod, ref float horizon, out Transform3D rootTransform, out Basis rootBasis)
    {
        rootTransform = default;
        rootBasis = Basis.Identity;
        if (!IsInsideTree() || !IsInstanceValid(root) || !root.IsInsideTree() ||
            !IsInstanceValid(_camera) || !_camera.IsInsideTree()) return false;
        root.Visible = withinLod;
        if (!withinLod) return false;
        if (!_flatMap) horizon = MathF.Max(horizon, CityHorizon());
        rootTransform = root.GlobalTransform;
        rootBasis = rootTransform.Basis;
        return true;
    }

    private bool IsOverlaySurfaceMarkerVisible(
        Basis rootBasis, Transform3D rootTransform, Vector3 normal, Node3D node, Vector3 viewNormal, float horizon)
        => !MapOverlaySyncRules.IsBeyondGlobeHorizon(ToNum(rootBasis * normal), ToNum(viewNormal), horizon) &&
           !_camera.IsPositionBehind(rootTransform * node.Position) &&
           !_coveredCounters.Contains(node);

    private void RebuildStarNamesCore()
    {
        _starNames.Clear();
        const float beside = 0.99995f;
        if (_cityIsCapital.Length != _cities.Count) _cityIsCapital = new bool[_cities.Count];
        else Array.Clear(_cityIsCapital);
        _cityCapitals.Clear();
        var capitals = new List<(Nation Nation, Vector3 Normal, int City)>();
        if (_campaign is not null)
        {
            foreach (var nation in _campaign.Nations)
            {
                if (!CapitalCore.HasSeat(_campaign, nation)) continue;
                var seat = CapitalCore.SeatCity(_campaign, nation) is { } city ? CityCatalog.IndexOf(city.Id) : -1;
                if (seat >= _cities.Count) seat = -1;
                if (seat >= 0 && !_cityIsCapital[seat])
                {
                    _cityIsCapital[seat] = true;
                    _cityCapitals.Add(seat);
                }
                capitals.Add((nation, SurfacePoint(CapitalCore.EffectiveLatitude(_campaign, nation),
                    CapitalCore.EffectiveLongitude(_campaign, nation), 1f).Normalized(), seat));
            }
        }
        _cityCapitals.Sort();
        foreach (var (marker, normal) in _capitalMarkers)
        {
            if (!IsInstanceValid(marker)) continue;
            // Whose star: the one country whose capital is at that point. Not by the node's name,
            // which the rebuild cannot keep - the old stars are still there, only queued to go.
            Nation owner = null;
            var standsOn = -1;
            var ownerCloseness = 0.9999f;
            foreach (var (nation, capitalNormal, seat) in capitals)
            {
                var closeness = capitalNormal.Dot(normal);
                if (closeness > ownerCloseness) { ownerCloseness = closeness; owner = nation; standsOn = seat; }
            }
            if (standsOn < 0)
            {
                var nearestCloseness = beside;
                for (var index = 0; index < _cityNormals.Length; index++)
                {
                    var closeness = _cityNormals[index].Dot(normal);
                    if (closeness > nearestCloseness) { nearestCloseness = closeness; standsOn = index; }
                }
            }
            _starNames.Add((marker, normal, standsOn, owner, StarCapitalName(owner)));
        }
    }
}
