using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Frame-0 lock-free world visual synchronizer for <see cref="GlobePresentation"/>.
/// Consumes triple-buffered <see cref="WorldVisualState"/> and <see cref="CampaignRenderSnapshot"/>
/// without ever acquiring <see cref="CampaignAccessGate"/>, eliminating visual lag behind time
/// for political borders, occupation stripes, leased regions, live war frontlines, GPU-instanced
/// battle badges, GPU compute / incremental plate repaints, and country label anchors.
/// </summary>
public sealed partial class GlobePresentation
{
    private long _lastLockFreeVisualVersion = -1;
    private long _lastLockFreeBattleHours = -1;
    private int _lastLockFreeBattleCount = -1;
    private bool _lastLockFreeBattleFog;
    private int _lastLockFreeNationPaletteHash;

    /// <summary>
    /// Applies the newest published <see cref="WorldVisualState"/> on the render thread with zero locks.
    /// Returns the snapshot version presented (or 0 if no visual snapshot is published yet) so the caller
    /// can notify <see cref="BackgroundSimRunner.NotifyVisualFramePresented(long)"/>.
    /// </summary>
    internal long SyncLockFreeWorldVisuals(BackgroundSimRunner simRunner)
    {
        if (!_mapApplied || _provinceTopology is null || _globeMaterial is null) return 0;

        WorldVisualState visual = null;
        if (simRunner is not null && simRunner.IsActive)
        {
            simRunner.TryAcquireLatestVisualSnapshot(out visual);
            if (visual is null || visual.Version <= 0)
                visual = simRunner.CurrentVisualSnapshot;
        }
        var snap = CurrentRenderSnapshot();
        var ownersForPlate = visual is { Version: > 0, ProvinceOwner.Length: > 0 }
            ? visual.ProvinceOwner
            : (snap is { Version: > 0, ProvinceOwner.Length: > 0 } ? snap.ProvinceOwner : null);
        var fastSim = visual is { Version: > 0 }
            ? (!visual.IsPaused && visual.Speed >= 4)
            : (snap is { Version: > 0 } && !snap.IsPaused && snap.Speed >= 4);

        // 1. Poll GPU Compute binding / finished CPU incremental plate repaints lock-free on frame 0.
        if (PollPlateRepaint(ownersForPlate, fastSim))
            WakeGlobeRendering();

        // 2. Poll or schedule country label placement lock-free from snapshot owners + nation names.
        if (snap is { Version: > 0 })
            RefreshCountryLabelPlacementLockFree(snap);

        if (visual is null || visual.Version <= 0)
            return snap is { Version: > 0 } ? snap.Version : 0;

        if (_campaign is not null)
            ResetRevisionTrackingIfCampaignChanged(_campaign);

        // 3. Refresh nation colour palette lock-free if any country colour changed or a new country spawned.
        SyncNationPaletteFromSnapshot(visual.Nations);

        // 4. Pack & upload province control/owner 256x256 shader tables lock-free on MapRevision change.
        var topology = _provinceTopology;
        var mapChanged = _controlRevision != visual.MapRevision;
        if (mapChanged &&
            _peacePreview.Count == 0 &&
            visual.ProvinceController.Length > topology.ProvinceCount &&
            visual.ProvinceOwner.Length > topology.ProvinceCount)
        {
            RefreshProvinceControllersLockFree(topology, visual, fastSim);
        }

        // 5. Paint live red/blue war frontlines lock-free when no custom player battle plan is drawn.
        var frontChanged = _frontRevision != visual.MapRevision || _frontWarRevision != visual.WarRevision;
        if (frontChanged &&
            !visual.HasPlayerFrontLines &&
            visual.DestroyedProvinceCount == 0 &&
            _planPreview.Count == 0 &&
            _defensivePreview.Count == 0 &&
            _objectivePreview.Count == 0 &&
            _departurePreview.Count == 0 &&
            visual.ProvinceController.Length > topology.ProvinceCount)
        {
            PaintLiveWarFrontsLockFree(topology, visual);
        }

        // 6. Update GPU-instanced battle badges lock-free (honoring Fog of War via VisualBattleItem.VisibleToPlayer).
        if (MilitaryMarkersEnabled &&
            (_lastLockFreeBattleHours != visual.ElapsedHours ||
             _lastLockFreeBattleCount != visual.BattleCount ||
             _lastLockFreeBattleFog != FogOfWarActive ||
             mapChanged || frontChanged))
        {
            SyncBattleMarkersLockFree(topology, visual);
        }

        _lastLockFreeVisualVersion = visual.Version;
        return visual.Version;
    }

    private void SyncNationPaletteFromSnapshot(NationSnapshot[] nations)
    {
        if (nations is null || nations.Length == 0) return;
        var hash = nations.Length;
        for (var i = 0; i < nations.Length; i++)
            hash = HashCode.Combine(hash, nations[i].MapIndex, nations[i].Color);
        if (hash == _lastLockFreeNationPaletteHash) return;
        _lastLockFreeNationPaletteHash = hash;

        var colorsChanged = false;
        for (var i = 0; i < nations.Length; i++)
        {
            var n = nations[i];
            if (n.MapIndex <= 0 || n.MapIndex >= 256 * 256) continue;
            if (_nationColourSources.TryGetValue(n.MapIndex, out var prev) &&
                string.Equals(prev, n.Color, StringComparison.Ordinal))
                continue;
            _nationColourSources[n.MapIndex] = n.Color ?? "";
            _colorByMapIndex[n.MapIndex] = ParseHex(n.Color, 0x6E7B8BFFu);
            colorsChanged = true;
        }
        if (colorsChanged)
            BuildNationPalette();
    }

    private void RefreshProvinceControllersLockFree(
        ProvinceTopology topology, WorldVisualState visual, bool fastSim)
    {
        WakeGlobeRendering();
        var pixels = _controlPixels ??= new byte[256 * 256 * 4];
        var ownerPixels = _ownerPixels ??= new byte[256 * 256 * 4];
        var ownerIds = _ownerIdPixels ??= new byte[256 * 256 * 4];

        var ownersMoved = RasterOverlayKernel.PackProvinceOverlays(
            topology.ProvinceCount,
            visual.ProvinceOwner,
            _paintedOwner,
            visual.ProvinceController,
            _colorByMapIndex,
            OccupiedStripeAlpha,
            UnownedPaletteIndex,
            0x6E7B8BFFu,
            pixels,
            ownerPixels,
            ownerIds);

        if (visual.LeaseCount > 0)
        {
            _leasedRegionHolder.Clear();
            for (var i = 0; i < visual.LeaseCount; i++)
            {
                var item = visual.Leases[i];
                _leasedRegionHolder[item.RegionMapIndex] = item.HolderMapIndex;
            }
            var states = topology.ProvinceToState;
            var ctrl = visual.ProvinceController;
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                var write = province * 4;
                if (pixels[write + 3] != OccupiedStripeAlpha) continue;
                if (_leasedRegionHolder.TryGetValue(states[province], out var tenantIndex) &&
                    tenantIndex == ctrl[province])
                    pixels[write + 3] = LeasedAlpha;
            }
        }

        if (_controlImage is null) _controlImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _controlImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("province_control_colors", _controlImage, ref _controllerTexture);

        UploadPlateCatchUp(visual.ProvinceOwner);

        if (_ownerIdImage is null) _ownerIdImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, ownerIds);
        else _ownerIdImage.SetData(256, 256, false, Image.Format.Rgba8, ownerIds);
        UpdateMapImage("province_owner_ids", _ownerIdImage, ref _ownerIdTexture);

        var movedVal = ownersMoved ? 1.0f : 0.0f;
        if (!ReferenceEquals(_cachedOwnersMovedMaterial, _globeMaterial) || _cachedOwnersMoved != ownersMoved)
        {
            _cachedOwnersMovedMaterial = _globeMaterial;
            _cachedOwnersMoved = ownersMoved;
            _globeMaterial.SetShaderParameter("owners_moved", movedVal);
        }

        RequestPlateRepaint(topology, visual.ProvinceOwner, fastSim);
        _controlRevision = visual.MapRevision;
    }

    private void PaintLiveWarFrontsLockFree(ProvinceTopology topology, WorldVisualState visual)
    {
        var pixels = _frontPixels ??= new byte[256 * 256 * 4];
        var controllers = _borderControllerPixels ??= new byte[256 * 256 * 2];
        Array.Clear(pixels);
        Array.Clear(controllers);

        var liveControl = visual.ProvinceController;
        var limit = Math.Min(liveControl.Length, 256 * 256);
        for (var p = 1; p < limit; p++)
        {
            controllers[p * 2] = (byte)(liveControl[p] & 255);
            controllers[p * 2 + 1] = (byte)(liveControl[p] >> 8);
        }

        for (var i = 0; i < visual.WarBelligerentCount; i++)
        {
            var entry = visual.WarBelligerents[i];
            var sideCode = entry.Side == 1 ? (byte)85 : (byte)170;
            RegisterFrontBelligerent(entry.NationMapIndex, entry.WarOrder, entry.WarId, entry.WarCode, sideCode);
        }
        if (_frontTouchedMapIndices.Count > 0)
            SweepLiveWarFronts(pixels, liveControl, topology);

        UpdateBorderTexture(ref _borderControllerTexture, ref _borderControllerImage, controllers, Image.Format.Rg8, "border_controller_ids");
        if (_frontImage is null) _frontImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _frontImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("front_membership", _frontImage, ref _frontTexture);

        _frontRevision = visual.MapRevision;
        _frontWarRevision = visual.WarRevision;
        _frontPreviewDeferred = false;
        WakeGlobeRendering();
    }

    private void SyncBattleMarkersLockFree(ProvinceTopology topology, WorldVisualState visual)
    {
        if (!IsInstanceValid(_battleRoot)) return;
        EnsureBattleBadgeMultiMesh();
        if (!ReferenceEquals(_battleAnchorMap, topology))
        {
            _battleAnchorMap = topology;
            _battleAnchors = new ProvinceBoundaryAnchor(topology);
        }

        var fogActive = FogOfWarActive;
        _activeBattleIds.Clear();
        for (var i = 0; i < visual.BattleCount; i++)
        {
            ref readonly var item = ref visual.Battles[i];
            if (!fogActive || item.VisibleToPlayer)
                _activeBattleIds.Add(item.BattleId);
        }

        _finishedBattleIds.Clear();
        foreach (var id in _battleMarkers.Keys)
            if (!_activeBattleIds.Contains(id)) _finishedBattleIds.Add(id);
        foreach (var old in _finishedBattleIds)
        {
            _battleMarkers.Remove(old);
            _battleReadouts.Remove(old);
        }

        _battleBadgeMaterial?.SetShaderParameter("quality_tier", CurrentShaderQualityTier());

        var playerId = !visual.ObserverMode ? visual.PlayerId : -1;
        for (var i = 0; i < visual.BattleCount; i++)
        {
            ref readonly var item = ref visual.Battles[i];
            if (!_activeBattleIds.Contains(item.BattleId)) continue;
            if (!GlobeLineSanity.TryValidateProvinceId(item.ProvinceId, topology.ProvinceCount, topology.ProvinceCenterCell))
                continue;
            if (!_battleMarkers.TryGetValue(item.BattleId, out var marker))
            {
                marker = new BattleMarkerEntry { BattleId = item.BattleId };
                _battleMarkers[item.BattleId] = marker;
            }
            UpdateLockFreeBattleMarker(marker, in item, topology, playerId, fogActive);
        }

        _lastLockFreeBattleHours = visual.ElapsedHours;
        _lastLockFreeBattleCount = visual.BattleCount;
        _lastLockFreeBattleFog = fogActive;
        FinishBattleMarkers();
        WakeGlobeRendering();
    }

    private void UpdateLockFreeBattleMarker(
        BattleMarkerEntry marker,
        in Simulation.VisualBattleItem item,
        Simulation.ProvinceTopology topology,
        int playerId,
        bool fogActive)
    {
        var location = topology.CenterOf(item.ProvinceId);
        if (!GlobeLineSanity.IsValidGeo(location.Latitude, location.Longitude)) return;
        var origin = 0;
        if (item.BestOriginProvinceId > 0 &&
            GlobeLineSanity.TryValidateProvinceId(item.BestOriginProvinceId, topology.ProvinceCount, topology.ProvinceCenterCell) &&
            _battleAnchors.TryFind(item.ProvinceId, item.BestOriginProvinceId, out var border) &&
            GlobeLineSanity.IsValidGeo(border.Latitude, border.Longitude))
        {
            location = border;
            origin = item.BestOriginProvinceId;
        }

        marker.ProvinceId = item.ProvinceId;
        marker.AttackingProvinceId = origin;
        marker.Latitude = location.Latitude;
        marker.Longitude = location.Longitude;
        marker.Normal = GlobeLineSanity.TrySafeNormalize(ToNum(SurfacePoint(location.Latitude, location.Longitude, 1f)), out var unitNorm)
            ? ToGd(unitNorm)
            : Vector3.Up;

        var own = playerId >= 0 && (item.AttackerId == playerId || item.DefenderId == playerId);
        var detailed = !fogActive || own;
        var attacks = !own || item.AttackerId == playerId;
        var share = detailed
            ? (attacks ? item.AttackerProgress : 1f - item.AttackerProgress)
            : (attacks ? item.AssessmentAttackerShare : item.AssessmentDefenderShare);
        var momentum = detailed ? (attacks ? item.Momentum : -item.Momentum) : 0f;
        var pocketAlert = detailed && (item.HasPocketCollapse || item.HasEncircledDefenders);
        var known = detailed && (item.AssessmentKnown || item.HasRounds);
        var tone = !detailed || !item.AssessmentKnown ? "text.muted" : !own ? "state.info" :
            share > .55f ? "state.positive" : share < .45f ? "state.negative" : "state.warning";
        _battleReadouts[item.BattleId] = (known ? $"{share * 100:0}" : "?", share, tone);

        var attColor = !string.IsNullOrEmpty(item.AttackerColorHex)
            ? Color.FromString(item.AttackerColorHex, Ui.Colour("state.negative")) : Ui.Colour("state.negative");
        var defColor = !string.IsNullOrEmpty(item.DefenderColorHex)
            ? Color.FromString(item.DefenderColorHex, Ui.Colour("state.info")) : Ui.Colour("state.info");

        marker.Share = known ? Math.Clamp(share, 0f, 1f) : -1f;
        marker.DisplayPercent = known ? MathF.Round(Math.Clamp(share, 0f, 1f) * 100f) : -1f;
        marker.Flags = BattleBadgeGpu.PackFlags(known, pocketAlert, thirdPartyInfo: known && !own, momentum);
        marker.AttackerColor = attColor;
        marker.PackedDefenderColor = BattleBadgeGpu.PackDefenderColor(defColor);
    }

    private void RefreshCountryLabelPlacementLockFree(CampaignRenderSnapshot snap)
    {
        if (_labelTask is not null)
        {
            if (!_labelTask.IsCompleted) return;
            if (_labelTask.IsCompletedSuccessfully)
                BuildCountryLabels(_labelTask.Result.Anchors, _labelTask.Result.Nations);
            else if (_labelTask.IsFaulted)
                GD.PushWarning("[dof] country names could not be re-placed: " +
                               _labelTask.Exception?.GetBaseException().Message);
            _labelTask = null;
            return;
        }

        if (_provinceTopology is null || _labelOwner is null) return;
        if (snap.ProvinceOwner is not { Length: > 0 } owners || snap.Nations.Length == 0) return;
        if (_labelRevision == snap.MapRevision) return;
        var nowMsec = Time.GetTicksMsec();
        if (nowMsec < _nextCountryLabelPlaceMsec) return;
        _labelRevision = snap.MapRevision;

        var moved = false;
        var len = Math.Min(owners.Length, _labelOwner.Length);
        for (var province = 1; province < len; province++)
            if (owners[province] != _labelOwner[province]) { moved = true; break; }
        if (!moved) return;

        var nationSnap = new NationLabelSnapshot[snap.Nations.Length];
        for (var i = 0; i < snap.Nations.Length; i++)
        {
            var n = snap.Nations[i];
            nationSnap[i] = new NationLabelSnapshot(n.MapIndex, n.Tag, n.MapName);
        }
        _labelOwner = owners;
        var fastForward = !snap.IsPaused && snap.Speed >= 4;
        _nextCountryLabelPlaceMsec = nowMsec + (Platform.PlatformProfile.Current?.IsTouchPrimary == true
            ? 850UL
            : (fastForward ? 650UL : 400UL));
        var cells = _provinceTopology.CellToProvince;
        var width = _provinceTopology.Width;
        var height = _provinceTopology.Height;
        _labelTask = Task.Run(() => (BuildCountryLabelAnchorsWorker(owners, cells, width, height), nationSnap));
    }
}
