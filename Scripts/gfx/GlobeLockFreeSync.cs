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
    private long _lastLockFreeArmyVersion = -1;
    private long _lastLockFreeArmyHours = -1;
    private int _lastLockFreeArmyMapRev = -1;
    private bool _lastLockFreeArmyFog;
    private int _lastLockFreeArmyShownNation = -1;
    private bool _lastLockFreeArmyFlat;
    private int _lastLockFreeArmySelectionHash;
    private int _lockFreeArmyStructureFingerprint = int.MinValue;
    private long _hydratedLockFreeArmyVersion = -1;
    private bool _lockFreeLabelBudgetPending;
    private int _frontGeometryRevision;
    private readonly Dictionary<int, string> _snapshotColorByMapIndex = new();
    private readonly Dictionary<int, Army> _lockFreeArmyLookup = new();
    private readonly HashSet<ushort> _lockFreeWarMapIndices = new();
    private readonly HashSet<int> _lockFreeVisibleNations = new();

    private int CurrentSelectedArmyHash()
    {
        if (_selectedArmyIds.Count == 0) return 0;
        var hash = _selectedArmyIds.Count;
        foreach (var id in _selectedArmyIds)
            hash ^= HashCode.Combine(id);
        return hash;
    }

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
            ? (!visual.IsPaused && visual.Speed >= 7)
            : (snap is { Version: > 0 } && !snap.IsPaused && snap.Speed >= 7);

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

        // 5. Paint live red/blue war frontlines, player front lines, and destroyed provinces 100% lock-free.
        var frontChanged = _frontRevision != visual.MapRevision || _frontWarRevision != visual.WarRevision;
        if (frontChanged &&
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
             _lastLockFreeBattleFog != FogOfWarActive || _lastLockFreeBattleViewer != visual.PlayerId ||
             mapChanged || frontChanged))
        {
            SyncBattleMarkersLockFree(topology, visual);
        }

        // 7. Reconcile, interpolate, and style army stacks 100% lock-free on Frame 0.
        var fogNow = FogOfWarActive;
        var selHash = CurrentSelectedArmyHash();
        if (MilitaryMarkersEnabled &&
            (_lastLockFreeArmyVersion != visual.Version ||
             _lastLockFreeArmyHours != visual.ElapsedHours ||
             _lastLockFreeArmyFog != fogNow ||
             _lastLockFreeArmyShownNation != ShownCountryId ||
             _lastLockFreeArmyFlat != _flatMap ||
             _lastLockFreeArmySelectionHash != selHash ||
             _lockFreeLabelBudgetPending ||
             mapChanged || frontChanged))
        {
            SyncArmyMarkersLockFree(topology, visual, fogNow, selHash);
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
            var hex = n.Color ?? "";
            _snapshotColorByMapIndex[n.MapIndex] = hex;
            if (_nationColourSources.TryGetValue(n.MapIndex, out var prev) &&
                string.Equals(prev, hex, StringComparison.Ordinal))
                continue;
            _nationColourSources[n.MapIndex] = hex;
            _colorByMapIndex[n.MapIndex] = ParseHex(hex, 0x6E7B8BFFu);
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
        var borderTargets = _borderTargetPixels ??= new byte[256 * 256 * 4];
        var controllers = _borderControllerPixels ??= new byte[256 * 256 * 2];
        Array.Clear(pixels);
        Array.Clear(borderTargets);
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

        for (var i = 0; i < visual.PlayerFrontProvinceCount; i++)
        {
            ref readonly var entry = ref visual.PlayerFrontProvinces[i];
            var p = entry.ProvinceId;
            if (p <= 0 || p >= 256 * 256) continue;
            var write = p * 4;
            pixels[write + 2] = entry.PlanCode;
            borderTargets[write] = (byte)(entry.OwnerMapIndex & 255);
            borderTargets[write + 1] = (byte)(entry.OwnerMapIndex >> 8);
            borderTargets[write + 2] = (byte)(entry.ForeignMapIndex & 255);
            borderTargets[write + 3] = (byte)(entry.ForeignMapIndex >> 8);
        }

        for (var i = 0; i < visual.DestroyedProvinceCount; i++)
        {
            var p = visual.DestroyedProvinces[i];
            if (p > 0 && p < 256 * 256) pixels[p * 4 + 3] = 255;
        }

        if (_frontGeometryRevision != visual.PlayerFrontGeometryHash)
        {
            _frontGeometryRevision = visual.PlayerFrontGeometryHash;
            _frontGeometryDirty = true;
        }

        UpdateBorderTexture(ref _borderPlanTexture, ref _borderPlanImage, borderTargets, Image.Format.Rgba8, "border_plan_targets");
        UpdateBorderTexture(ref _borderControllerTexture, ref _borderControllerImage, controllers, Image.Format.Rg8, "border_controller_ids");
        if (_frontImage is null) _frontImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _frontImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("front_membership", _frontImage, ref _frontTexture);

        _frontRevision = visual.MapRevision;
        _frontWarRevision = visual.WarRevision;
        _frontPreviewDeferred = false;
        WakeGlobeRendering();
    }

    private void SyncArmyMarkersLockFree(
        ProvinceTopology topology, WorldVisualState visual, bool fogActive, int selectionHash)
    {
        if (!IsInstanceValid(_armyRoot) || _divisionTexture is null) return;

        var mode = SettingsCatalog.Chosen("map_divisions");
        var showFlags = SettingsCatalog.Flag("movement_flags");
        var structFp = HashCode.Combine(
            visual.ArmyStructureVersion,
            fogActive,
            ShownCountryId,
            showFlags,
            mode, visual.PlayerId, visual.ObserverMode);
        var structureChanged = _lockFreeArmyStructureFingerprint != structFp;

        if (_selectedArmyIds.Count > 0 && structureChanged)
        {
            _liveArmyIds.Clear();
            for (var i = 0; i < visual.ArmyStackCount; i++)
            {
                ref readonly var st = ref visual.ArmyStacks[i];
                if (!st.AnySelectable) continue;
                for (var k = 0; k < st.ArmyIdCount; k++)
                    _liveArmyIds.Add(visual.StackArmyIds[st.ArmyIdOffset + k]);
            }
            _selectedArmyIds.RemoveWhere(id => !_liveArmyIds.Contains(id));
        }

        var playerId = !visual.ObserverMode ? visual.PlayerId : -1;
        _lockFreeWarMapIndices.Clear();
        if (mode is not ("all" or "ours"))
        {
            for (var i = 0; i < visual.WarBelligerentCount; i++)
                _lockFreeWarMapIndices.Add(visual.WarBelligerents[i].NationMapIndex);
        }

        if (structureChanged)
        {
            _previousArmyPositions.Clear();
            foreach (var m in _armyStacks.Values)
            {
                if (!IsInstanceValid(m.Node)) continue;
                if (m.LockFreeArmyIds.Count > 0)
                {
                    for (var i = 0; i < m.LockFreeArmyIds.Count; i++)
                        _previousArmyPositions[m.LockFreeArmyIds[i]] = m.DisplayPosition;
                }
                else if (m.Stack.Count > 0)
                {
                    for (var i = 0; i < m.Stack.Count; i++)
                        _previousArmyPositions[m.Stack[i].Id] = m.DisplayPosition;
                }
            }
            foreach (var marker in _armyStacks.Values)
                marker.Seen = false;
        }

        _provinceLeader.Clear();
        _lockFreeVisibleNations.Clear();
        var budgetClock = System.Diagnostics.Stopwatch.GetTimestamp();
        var updatedLabels = 0;
        var labelsClipped = false;
        var createdOrRemoved = false;

        for (var i = 0; i < visual.ArmyStackCount; i++)
        {
            ref readonly var st = ref visual.ArmyStacks[i];
            if (!ShouldShowLockFreeStack(in st, playerId, mode, fogActive)) continue;

            var bucket = (st.NationId, st.LocationId, st.FallbackLatBucket, st.FallbackLonBucket);
            if (!_armyStacks.TryGetValue(bucket, out var marker))
            {
                if (!structureChanged) continue;
                marker = _armyMarkerPool.Count > 0 ? _armyMarkerPool.Pop() : new ArmyStackMarker();
                _armyStacks[bucket] = marker;
                createdOrRemoved = true;
            }
            marker.Seen = true;
            marker.IsLockFreeSynced = true;
            marker.LockFreeProvinceId = st.LocationId > 0 ? st.LocationId : st.LeadProvinceId;
            marker.LockFreeLeadArmyId = st.LeadArmyId;
            marker.LockFreeArmyGroupId = st.ArmyGroupId;
            marker.LockFreeNextProvinceId = st.NextProvinceId;
            marker.LockFreeMarchProgress = st.MarchProgress;
            marker.LockFreeDivisions = st.Divisions;
            marker.LockFreePersonnel = st.Personnel;
            marker.LockFreeAnyInBattle = ((fogActive ? st.ConditionFlags : st.OmniscientConditionFlags) & 1) != 0;
            marker.LockFreeAnyCutOff = ((fogActive ? st.ConditionFlags : st.OmniscientConditionFlags) & 2) != 0;
            marker.LockFreeAvgOrganisation = st.OrganisationAvg;
            marker.LockFreeAvgPlanning = st.PlanningAvg;
            marker.LockFreeAvgSupply = st.SupplyAvg;
            marker.LockFreeOrgBin = fogActive ? st.OrgBin : st.OmniscientOrgBin;
            marker.LockFreeStrBin = fogActive ? st.StrBin : st.OmniscientStrBin;
            marker.LockFreeConditionFlags = fogActive ? st.ConditionFlags : st.OmniscientConditionFlags;
            marker.LockFreeCounterStance = st.CounterStance;
            marker.LockFreeConditionKnown = !fogActive || st.ConditionKnown;

            if (marker.ActualFingerprint != st.ActualFingerprint || marker.LockFreeArmyIds.Count != st.ArmyIdCount)
            {
                marker.Stack.Clear();
                marker.DisplayStack.Clear();
                marker.LockFreeArmyIds.Clear();
                for (var k = 0; k < st.ArmyIdCount; k++)
                    marker.LockFreeArmyIds.Add(visual.StackArmyIds[st.ArmyIdOffset + k]);
                marker.ActualFingerprint = st.ActualFingerprint;
                if (IsInstanceValid(marker.Node))
                {
                    var ids = new Godot.Collections.Array<int>();
                    for (var k = 0; k < marker.LockFreeArmyIds.Count; k++) ids.Add(marker.LockFreeArmyIds[k]);
                    marker.Node.SetMeta("actual_army_ids", ids);
                    marker.Node.SetMeta("province_id", marker.LockFreeProvinceId);
                    InvalidateFlatCounterDensity();
                }
            }

            var location = ArmyPresentationLocationFromSnapshot(topology, in st);
            marker.CachedGeoLat = location.Latitude;
            marker.CachedGeoLon = location.Longitude;
            marker.CachedGeoValid = true;
            marker.CachedIsMoving = st.IsMoving;
            marker.Normal = SurfacePoint(location.Latitude, location.Longitude, 1f);

            var colorHex = _snapshotColorByMapIndex.GetValueOrDefault(st.NationMapIndex, "");
            if (marker.Node is null || !IsInstanceValid(marker.Node))
            {
                CreateArmyStackMarkerFromSnapshot(marker, in st, colorHex);
                createdOrRemoved = true;
                InvalidateFlatCounterDensity();
            }
            else if (structureChanged)
            {
                if (marker.Node.GetParent() != _armyRoot)
                {
                    marker.Node.GetParent()?.RemoveChild(marker.Node);
                    _armyRoot.AddChild(marker.Node);
                }
                var desiredName = $"DivisionStack_{st.NationTag}_{st.LeadProvinceId}_{st.LeadArmyId}";
                if (!string.Equals(marker.CachedNodeName, desiredName, StringComparison.Ordinal))
                {
                    marker.CachedNodeName = desiredName;
                    marker.Node.Name = desiredName;
                }
                var flagTex = st.NationTag.Length > 0 ? DealOrForceGame.FlagTextureFor(st.NationTag) : null;
                var flagVisualId = flagTex?.GetInstanceId() ?? 0;
                var textureChanged = marker.NationId != st.NationId ||
                    marker.FlagVisualId != flagVisualId ||
                    !string.Equals(marker.TemplateId, st.TemplateId, StringComparison.Ordinal);
                if (textureChanged)
                {
                    marker.FullPlateTexture = ArmyCounterTextureForSnapshot(st.NationMapIndex, st.NationTag, colorHex, st.TemplateId);
                    marker.NationId = st.NationId;
                    marker.TemplateId = st.TemplateId;
                    marker.FlagVisualId = flagVisualId;
                }
                if (textureChanged || !marker.LayoutInitialized || marker.FlatLayout != _flatMap)
                    SizeArmyCounter(marker);
            }

            if (marker.Node is null || !IsInstanceValid(marker.Node)) continue;

            var targetPos = ArmyMarkerPositionFromCachedGeo(marker);
            marker.TargetPosition = targetPos;
            if (!marker.MotionSettled)
            {
                if (_previousArmyPositions.TryGetValue(st.LeadArmyId, out var previous) &&
                    previous != Vector3.Zero &&
                    previous.DistanceSquaredTo(targetPos) > 1e-8f)
                {
                    marker.DisplayPosition = previous;
                    marker.HasPendingArrivalGlide = true;
                }
                else
                {
                    marker.DisplayPosition = targetPos;
                    marker.HasPendingArrivalGlide = false;
                }
                marker.Node.Position = marker.DisplayPosition;
                marker.MotionSettled = true;
                InvalidateFlatCounterDensity();
            }
            if (structureChanged)
            {
                OrientSurfaceArmyCard(marker);
                if (!_flatMap) SizeSurfaceArmyCounter(marker);
                if (marker.LastMetaProvinceId != st.LeadProvinceId &&
                    st.LeadProvinceId > 0 && st.LeadProvinceId <= topology.ProvinceCount)
                {
                    marker.LastMetaProvinceId = st.LeadProvinceId;
                    var center = topology.CenterOf(st.LeadProvinceId);
                    marker.Node.SetMeta("province_center_latlon", new Vector2(center.Latitude, center.Longitude));
                }
            }

            if (marker.DisplayStack.Count == 0)
            {
                var divCount = Mathf.CeilToInt(st.Divisions);
                var menTenths = (int)MathF.Round(st.Personnel / 100f);
                if (marker.ShownDivisions != divCount || marker.ShownMenTenths != menTenths)
                {
                    if (updatedLabels > 0 &&
                        System.Diagnostics.Stopwatch.GetElapsedTime(budgetClock).TotalMilliseconds >= ActiveLabel3dFrameBudgetMs())
                    {
                        labelsClipped = true;
                    }
                    else
                    {
                        var divChanged = marker.ShownDivisions != divCount;
                        marker.ShownDivisions = divCount;
                        marker.ShownMenTenths = menTenths;
                        var text = marker.ShownCompact
                            ? divCount.ToString()
                            : $"{divCount}\n{menTenths / 10f:0.#}k";
                        if (!string.Equals(marker.CountText, text, StringComparison.Ordinal))
                        {
                            marker.CountText = text;
                            if (marker.Count is not null && marker.Count.Visible &&
                                (!_flatMap || marker.DisplayProvinces <= 1 || marker.Plate?.Visible == true) &&
                                !string.Equals(marker.Count.Text, text, StringComparison.Ordinal))
                            {
                                marker.Count.Text = text;
                                updatedLabels++;
                            }
                            if (divChanged) InvalidateFlatCounterDensity();
                        }
                    }
                }
                ApplyLockFreeArmyConditionBars(marker);
            }

            // Update Glow, Plate tint, and Count tint matching MilitaryPresentation without locks.
            var anySelected = false;
            if (_selectedArmyIds.Count > 0)
            {
                var selList = marker.LockFreeDisplayArmyIds.Count > 0 ? marker.LockFreeDisplayArmyIds : marker.LockFreeArmyIds;
                for (var s = 0; s < selList.Count; s++)
                    if (_selectedArmyIds.Contains(selList[s])) { anySelected = true; break; }
            }
            if (marker.Glow is not null && marker.Glow.Visible != anySelected)
                marker.Glow.Visible = anySelected;
            if (marker.Plate is not null)
            {
                var plateTint = anySelected
                    ? new Color(1f, 0.96f, 0.86f, 1f)
                    : marker.LockFreeAnyInBattle
                        ? new Color(1f, 0.58f, 0.48f, 1f)
                        : st.IsMoving ? new Color(0.76f, 0.92f, 1f, 1f) : Colors.White;
                if (marker.Plate.Modulate != plateTint) marker.Plate.Modulate = plateTint;
            }
            if (marker.Count is not null)
            {
                var textTint = marker.LockFreeAvgSupply < 0.45f || marker.LockFreeAnyCutOff
                    ? Ui.Colour("military.routed")
                    : marker.LockFreeAvgOrganisation < 0.35f ? Ui.Colour("military.shaken")
                    : marker.LockFreeAvgPlanning > 0.70f ? Ui.Colour("military.planning") : Ui.Colour("military.ready");
                if (marker.Count.Modulate != textTint) marker.Count.Modulate = textTint;
            }
        }

        if (structureChanged)
        {
            _vanishedStacks.Clear();
            foreach (var (bucket, marker) in _armyStacks)
                if (!marker.Seen || marker.LockFreeArmyIds.Count == 0) _vanishedStacks.Add(bucket);
            foreach (var bucket in _vanishedStacks)
            {
                var marker = _armyStacks[bucket];
                if (marker.Node is not null && IsInstanceValid(marker.Node))
                {
                    if (_armyMarkerPool.Count < 512)
                    {
                        marker.Node.Visible = false;
                        if (marker.Glow is not null && IsInstanceValid(marker.Glow))
                            marker.Glow.Visible = false;
                        marker.Stack.Clear();
                        marker.DisplayStack.Clear();
                        marker.LockFreeArmyIds.Clear();
                        marker.LockFreeDisplayArmyIds.Clear();
                        marker.IsLockFreeSynced = false;
                        marker.LockFreeProvinceId = 0;
                        marker.LockFreeLeadArmyId = 0;
                        marker.LockFreeArmyGroupId = -1;
                        marker.LockFreeNextProvinceId = 0;
                        marker.LockFreeMarchProgress = 0f;
                        marker.LockFreeDivisions = 0f;
                        marker.LockFreePersonnel = 0f;
                        marker.Seen = false;
                        marker.CountText = "";
                        marker.ShownDivisions = -1;
                        marker.ShownMenTenths = -1;
                        marker.ConditionKey = -1;
                        marker.LastMetaProvinceId = -1;
                        marker.ActualFingerprint = 0;
                        marker.DisplayFingerprint = 0;
                        marker.LayoutInitialized = false;
                        marker.LayoutOffset = Vector2.Zero;
                        marker.MotionSettled = false;
                        marker.CachedGeoLat = 0f;
                        marker.CachedGeoLon = 0f;
                        marker.CachedGeoValid = false;
                        marker.CachedIsMoving = false;
                        marker.HasPendingArrivalGlide = false;
                        marker.CachedNodeName = "";
                        _armyMarkerPool.Push(marker);
                    }
                    else
                    {
                        marker.Node.GetParent()?.RemoveChild(marker.Node);
                        marker.Node.QueueFree();
                    }
                }
                _armyStacks.Remove(bucket);
                createdOrRemoved = true;
            }
            if (_vanishedStacks.Count > 0) InvalidateFlatCounterDensity();

            _armyMarkers.Clear();
            _armyByMarker.Clear();
            foreach (var marker in _armyStacks.Values)
            {
                if (marker.Node is null || !IsInstanceValid(marker.Node)) continue;
                _armyMarkers.Add((marker.Node, marker.Normal));
                _armyByMarker[marker.Node] = marker.Stack;
            }
            PruneArmyCounterTextureCaches();
            CoverSharedProvincesLockFree(playerId);
            RefreshArmyMarkerVisibility();
            _lockFreeArmyStructureFingerprint = structFp;
            _hydratedLockFreeArmyVersion = -1;
        }
        else if (createdOrRemoved)
        {
            CoverSharedProvincesLockFree(playerId);
            RefreshArmyMarkerVisibility();
            _hydratedLockFreeArmyVersion = -1;
        }

        _lockFreeLabelBudgetPending = labelsClipped;
        _lastCombatSyncHours = visual.ElapsedHours;
        _lastLockFreeArmyVersion = visual.Version;
        _lastLockFreeArmyHours = visual.ElapsedHours;
        _lastLockFreeArmyMapRev = visual.MapRevision;
        _lastLockFreeArmyFog = fogActive;
        _lastLockFreeArmyShownNation = ShownCountryId;
        _lastLockFreeArmyFlat = _flatMap;
        _lastLockFreeArmySelectionHash = selectionHash;
        WakeGlobeRendering();
    }

    internal void HydrateLockFreeMarkerStacks(Simulation.Campaign campaign)
    {
        if (campaign is null || _lastLockFreeArmyVersion <= 0 ||
            _hydratedLockFreeArmyVersion == _lastLockFreeArmyVersion)
            return;

        _lockFreeArmyLookup.Clear();
        var armies = campaign.Armies;
        for (var i = 0; i < armies.Count; i++)
            _lockFreeArmyLookup[armies[i].Id] = armies[i];

        foreach (var marker in _armyStacks.Values)
        {
            if (!marker.IsLockFreeSynced || marker.Node is null || !IsInstanceValid(marker.Node)) continue;
            if (marker.Stack.Count != marker.LockFreeArmyIds.Count)
            {
                marker.Stack.Clear();
                for (var k = 0; k < marker.LockFreeArmyIds.Count; k++)
                    if (_lockFreeArmyLookup.TryGetValue(marker.LockFreeArmyIds[k], out var army))
                        marker.Stack.Add(army);
                _armyByMarker[marker.Node] = marker.Stack;
            }
            if (marker.DisplayStack.Count != marker.LockFreeDisplayArmyIds.Count)
            {
                marker.DisplayStack.Clear();
                for (var k = 0; k < marker.LockFreeDisplayArmyIds.Count; k++)
                    if (_lockFreeArmyLookup.TryGetValue(marker.LockFreeDisplayArmyIds[k], out var army))
                        marker.DisplayStack.Add(army);
            }
        }
        _hydratedLockFreeArmyVersion = _lastLockFreeArmyVersion;
    }

    private bool ShouldShowLockFreeStack(in VisualArmyStack stack, int playerId, string mode, bool fogActive)
    {
        if (fogActive && !stack.VisibleToPlayer) return false;
        if (string.Equals(mode, "all", StringComparison.Ordinal)) return true;
        if (string.Equals(mode, "ours", StringComparison.Ordinal))
            return playerId >= 0 && stack.NationId == playerId;
        return (playerId >= 0 && (stack.CounterStance == StancePlayer || stack.CounterStance == StanceAlly)) ||
               stack.NationId == ShownCountryId ||
               _lockFreeWarMapIndices.Contains(stack.NationMapIndex);
    }

    private static (float Latitude, float Longitude) ArmyPresentationLocationFromSnapshot(
        ProvinceTopology topology, in VisualArmyStack stack)
    {
        if (topology is not null && stack.LocationId > 0 && stack.LocationId <= topology.ProvinceCount)
        {
            var start = topology.CenterOf(stack.LocationId);
            if (stack.NextProvinceId > 0 && stack.NextProvinceId <= topology.ProvinceCount)
            {
                var next = topology.CenterOf(stack.NextProvinceId);
                var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(stack.MarchProgress, 0f, 1f));
                var dLon = next.Longitude - start.Longitude;
                if (dLon > 180f) dLon -= 360f;
                else if (dLon < -180f) dLon += 360f;
                var lon = start.Longitude + dLon * t;
                if (lon > 180f) lon -= 360f;
                else if (lon <= -180f) lon += 360f;
                return (Mathf.Lerp(start.Latitude, next.Latitude, t), lon);
            }
            return start;
        }
        return (stack.FallbackLatitude, stack.FallbackLongitude);
    }

    private void CreateArmyStackMarkerFromSnapshot(
        ArmyStackMarker marker, in VisualArmyStack st, string colorHex)
    {
        var initialPos = ArmyMarkerPositionFromCachedGeo(marker);
        var stackName = $"DivisionStack_{st.NationTag}_{st.LeadProvinceId}_{st.LeadArmyId}";
        var node = new Node3D
        {
            Name = stackName,
            Position = initialPos
        };
        _armyRoot.AddChild(node);
        marker.Node = node;
        marker.CachedNodeName = stackName;
        marker.TargetPosition = initialPos;
        marker.DisplayPosition = initialPos;
        marker.NationId = st.NationId;
        marker.TemplateId = st.TemplateId;
        var flagTex = st.NationTag.Length > 0 ? DealOrForceGame.FlagTextureFor(st.NationTag) : null;
        marker.FlagVisualId = flagTex?.GetInstanceId() ?? 0;

        var plate = new Sprite3D
        {
            Texture = ArmyCounterTextureForSnapshot(st.NationMapIndex, st.NationTag, colorHex, st.TemplateId),
            PixelSize = CounterPlatePixelSize * _counterScale,
            FixedSize = false,
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Modulate = Colors.White
        };
        node.AddChild(plate);
        marker.Plate = plate;
        marker.FullPlateTexture = plate.Texture;
        marker.Glow = AttachSelectionGlow(plate, 176, 104, 0);
        marker.ConditionBars = new Sprite3D
        {
            Name = "OrganisationEquipmentBars",
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            RenderPriority = 10,
            NoDepthTest = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear
        };
        node.AddChild(marker.ConditionBars);

        var divisions = new Label3D
        {
            Name = "DivisionCount",
            Text = "",
            FontSize = 44,
            PixelSize = CounterTextPixelSize * _counterScale,
            LineSpacing = -10f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            FixedSize = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            OutlineSize = Mathf.RoundToInt(8 * _counterScale),
            Modulate = Ui.Colour("military.ready"),
            OutlineModulate = Ui.Colour("globe.label_outline"),
            Position = Vector3.Zero,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            RenderPriority = 20,
            NoDepthTest = true
        };
        node.AddChild(divisions);
        marker.Count = divisions;
        SizeArmyCounter(marker);

        var ids = new Godot.Collections.Array<int>();
        for (var k = 0; k < marker.LockFreeArmyIds.Count; k++) ids.Add(marker.LockFreeArmyIds[k]);
        node.SetMeta("actual_army_ids", ids);
        node.SetMeta("province_id", marker.LockFreeProvinceId);
    }

    private void CoverSharedProvincesLockFree(int playerId)
    {
        _coveredCounters.Clear();
        _provinceLeader.Clear();
        foreach (var (bucket, marker) in _armyStacks)
        {
            if (bucket.Item2 <= 0 || marker.Node is null || marker.LockFreeDivisions <= 0f) continue;
            if (!_provinceLeader.TryGetValue(bucket.Item2, out var leader))
            {
                _provinceLeader[bucket.Item2] = marker;
                continue;
            }
            var mine = marker.LockFreeDivisions;
            var theirs = leader.LockFreeDivisions;
            var wins = mine > theirs + 0.01f ||
                       (MathF.Abs(mine - theirs) <= 0.01f &&
                        (marker.NationId == playerId ||
                         (leader.NationId != playerId && marker.NationId < leader.NationId)));
            if (wins)
            {
                _coveredCounters.Add(leader.Node);
                _provinceLeader[bucket.Item2] = marker;
            }
            else
            {
                _coveredCounters.Add(marker.Node);
            }
        }
        if (_flatMap) _coveredCounters.Clear();
        foreach (var node in _coveredCounters)
            if (IsInstanceValid(node) && node.Visible) node.Visible = false;
    }

    private void ApplyLockFreeArmyConditionBars(ArmyStackMarker marker)
    {
        if (marker.ConditionBars is null) return;
        var visible = marker.Plate.Visible && !(_flatMap && _flatStrategicCounters) && marker.LockFreeConditionKnown;
        if (marker.ConditionBars.Visible != visible)
            marker.ConditionBars.Visible = visible;
        if (!visible) { marker.ConditionKey = -1; return; }

        var palette = HashCode.Combine(
            InterfaceSkin.Colour("combat.organisation"),
            InterfaceSkin.Colour("combat.equipment"),
            InterfaceSkin.Colour("combat.bar_background"),
            InterfaceSkin.Colour("state.positive"),
            InterfaceSkin.Colour("state.warning"),
            InterfaceSkin.Colour("state.negative"));
        if (palette != _conditionPalette)
        {
            _conditionPalette = palette;
            DisposeConditionTextures();
        }

        var key = ((int)marker.LockFreeOrgBin, (int)marker.LockFreeStrBin, marker.LockFreeConditionFlags, marker.LockFreeCounterStance);
        var hashKey = HashCode.Combine(key.Item1, key.Item2, key.Item3, key.Item4);
        if (marker.ConditionKey == hashKey && marker.ConditionBars.Texture is not null) return;
        marker.ConditionKey = hashKey;

        Texture2D texture;
        if (_conditionTextures.TryGetValue(key, out var cached))
        {
            texture = cached.Texture;
            if (cached.Node != _conditionLru.First)
            {
                _conditionLru.Remove(cached.Node);
                _conditionLru.AddFirst(cached.Node);
            }
        }
        else
        {
            texture = BuildArmyOverlayTexture(key.Item1, key.Item2, key.Item3, key.Item4);
            var lruNode = _conditionLru.AddFirst(key);
            _conditionTextures[key] = (texture, lruNode);
            while (_conditionLru.Count > ConditionTextureCap && _conditionLru.Last is { } oldest)
            {
                _conditionLru.RemoveLast();
                if (_conditionTextures.Remove(oldest.Value, out var evicted))
                    evicted.Texture?.Dispose();
            }
        }
        if (marker.ConditionBars.Texture != texture) marker.ConditionBars.Texture = texture;
        var targetScale = new Vector3(176f / ConditionCanvasWidth, 104f / ConditionCanvasHeight, 1f);
        if (marker.ConditionBars.Scale != targetScale) marker.ConditionBars.Scale = targetScale;
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
        _lastLockFreeBattleFog = fogActive; _lastLockFreeBattleViewer = visual.PlayerId;
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
        if (_deferredLabelAnchors is { } deferredAnchors && _labelTask is null)
        {
            BuildCountryLabels(deferredAnchors, _deferredLabelNations);
            return;
        }
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
        var fastForward = !snap.IsPaused && snap.Speed >= 7;
        _nextCountryLabelPlaceMsec = nowMsec + (Platform.PlatformProfile.Current?.IsTouchPrimary == true
            ? 850UL
            : (fastForward ? 650UL : 400UL));
        var cells = _provinceTopology.CellToProvince;
        var width = _provinceTopology.Width;
        var height = _provinceTopology.Height;
        _labelTask = Task.Run(() => (BuildCountryLabelAnchorsWorker(owners, cells, width, height), nationSnap));
    }
}
