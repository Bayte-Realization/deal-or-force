using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly Dictionary<Vector2I, List<Rect2>> _occupiedCounterCells = new();
    private readonly List<(Node3D Node, Sprite3D Plate, Vector3 Anchor, int Id, SupportMapMarker Support)> _auxiliaryCounters = new();
    private readonly Dictionary<int, Vector2> _flatFleetOffsets = new();
    private ulong _nextCounterDensityMs;
    private Vector3 _densityView = new(float.NaN, float.NaN, float.NaN);
    private Vector2 _densityViewport;
    private bool _counterDensityDirty = true;
    private bool _flatStrategicCounters;
    private readonly Dictionary<int, List<ArmyStackMarker>> _provinceStackScratch = new();
    // The strategic view's merged counters: a host and the counters of the same country it took in.
    private readonly List<(ArmyStackMarker Marker, Rect2 Room)> _strategicHosts = new();
    private readonly Dictionary<ArmyStackMarker, List<ArmyStackMarker>> _strategicMerges = new();
    private readonly List<List<ArmyStackMarker>> _provinceStackPool = new();
    private readonly List<ArmyStackMarker> _motionActiveStacks = new();
    private int _motionScanFp = int.MinValue, _motionScanCount = -1;
    private long _motionScanHours = long.MinValue;
    private bool _motionScanFlat;
    private float _motionScanFlatX = float.NaN;
    private ulong _nextMotionScanMs;
    private long _lastObservedSimHours = -1;
    private ulong _lastObservedSimHoursMsec;
    private float _simStepIntervalEma = 0.14f;

    // Geography is authoritative. Collision avoidance must never move a division
    // counter away from its province or merge it with a neighbouring province.
    private Vector3 ArmyMarkerPosition(ArmyStackMarker marker)
    {
        var position = ArmyPresentationPoint(marker.Stack);
        // Several countries can stand in one province. Their counters stay anchored to that
        // province, but a small deterministic fan-out keeps two flags from occupying exactly
        // the same pixels. The offset is presentation only; click, province and save data
        // continue to use the army's real province.
        if (_flatMap && marker.LayoutOffset != Vector2.Zero)
            position += new Vector3(marker.LayoutOffset.X, -marker.LayoutOffset.Y, 0f) *
                (_camera.Size / Math.Max(1f, Size.Y));
        return position;
    }

    private Vector3 ArmyMarkerPositionFromCachedGeo(ArmyStackMarker marker)
    {
        var position = marker.CachedGeoValid
            ? PresentationPoint(marker.CachedGeoLat, marker.CachedGeoLon, SurfaceMarkerRadius)
            : ArmyPresentationPoint(marker.Stack);
        if (_flatMap && marker.LayoutOffset != Vector2.Zero)
            position += new Vector3(marker.LayoutOffset.X, -marker.LayoutOffset.Y, 0f) *
                (_camera.Size / Math.Max(1f, Size.Y));
        return position;
    }

    /// <summary>
    /// The one place a counter's size is decided, so a counter is never drawn at a size the
    /// frame's own layout pass would not choose.
    ///
    /// `SizeArmyCounter` used to scale by `_counterWorldPixel` alone while `UpdateCounterScale`
    /// scaled by `_counterWorldPixel * _counterFlatFar`. A counter created or re-laid-out
    /// between the two was therefore drawn at the full, un-foreshortened size for a frame or
    /// two before the next pass pulled it back - which is exactly the momentary swell a player
    /// sees when a division crosses a border and a new counter appears next to its neighbours.
    /// </summary>
    private void ApplyArmyCounterScale(ArmyStackMarker marker)
    {
        if (marker.Node is null || !IsInstanceValid(marker.Node)) return;
        marker.Node.Scale = Vector3.One * _counterWorldPixel * _counterFlatFar;
    }

    /// <summary>
    /// Ease every counter toward where it belongs. Called every frame, not on the tenth-of-a-
    /// second visual cadence, because the whole point is that motion is continuous.
    ///
    /// The counter's target is geography and is written by the reconcile pass; this only moves
    /// the drawn node toward it. A counter that is already there costs one comparison, and one
    /// that has just been created snaps rather than flying in from wherever it was spawned.
    /// </summary>
    private static bool HasMovingArmy(ArmyStackMarker marker)
    {
        if (marker.IsLockFreeSynced) return marker.CachedIsMoving;
        for (var i = 0; i < marker.Stack.Count; i++)
            if (marker.Stack[i].IsMoving && marker.Stack[i].Route.Count > 0) return true;
        return false;
    }

    private void StepArmyMarkerMotion(ArmyStackMarker marker, float blend, bool isMoving)
    {
        var node = marker.Node;
        var arrivalGlide = marker.HasPendingArrivalGlide;
        if (!marker.MotionSettled)
        {
            // A brand-new counter appears where it belongs. Nothing flies in from nowhere.
            marker.DisplayPosition = marker.TargetPosition;
            marker.MotionSettled = true;
            marker.HasPendingArrivalGlide = false;
            node.Position = marker.DisplayPosition;
            return;
        }
        if ((isMoving || arrivalGlide || _flatMap) && marker.CachedGeoValid)
            marker.TargetPosition = ArmyMarkerPositionFromCachedGeo(marker);
        if (_flatMap)
        {
            // Horizontal panning changes the nearest repeated sheet, including when
            // the camera rebases at 180 degrees. Do not ease that whole-world offset
            // as though the division were marching across every intervening country.
            var rebasedX = DateLineSeam.NearestCopy(marker.DisplayPosition.X, marker.TargetPosition.X, FlatMapWidth);
            if (rebasedX != marker.DisplayPosition.X)
                node.Position = marker.DisplayPosition = new Vector3(rebasedX, marker.DisplayPosition.Y, marker.DisplayPosition.Z);
        }
        var remaining = marker.TargetPosition - marker.DisplayPosition;
        if (remaining.LengthSquared() < 1e-10f)
        {
            marker.HasPendingArrivalGlide = false;
            return;
        }
        // A camera pan or zoom moves the target of a stationary counter every frame. That
        // is not the formation marching, and easing it would make the whole map swim.
        if (!isMoving && !arrivalGlide)
            node.Position = marker.DisplayPosition = marker.TargetPosition;
        else
        {
            marker.DisplayPosition += remaining * blend;
            if ((marker.TargetPosition - marker.DisplayPosition).LengthSquared() < 1e-8f)
            {
                marker.DisplayPosition = marker.TargetPosition;
                marker.HasPendingArrivalGlide = false;
            }
            // On the globe the card lies on the surface, so the eased point is pushed back out
            // to the local terrain surface rather than cutting the chord through the planet.
            if (!_flatMap) marker.DisplayPosition = LocalSurfacePointFromNormal(marker.DisplayPosition, SurfaceMarkerRadius);
            node.Position = marker.DisplayPosition;
        }
        // Orientation follows the ground under the card, so it has to be recomputed as the
        // card slides rather than left at the bearing it was created with.
        OrientSurfaceArmyCard(marker);
        if (!_flatMap) SizeSurfaceArmyCounter(marker);
    }

    private void UpdateArmyMarkerMotion(float delta)
    {
        if (!IsInstanceValid(_armyRoot) || _armyStacks.Count == 0) return;
        var now = Time.GetTicksMsec();
        var snap = CurrentRenderSnapshot();
        var speed = snap is { Version: > 0 }
            ? (snap.IsPaused ? 1 : snap.Speed)
            : (_campaign is { IsPaused: false } ? _campaign.Speed : 1);
        var hours = snap is { Version: > 0 } ? snap.ElapsedHours : (_campaign?.ElapsedHours ?? -1);
        if (hours != _lastObservedSimHours)
        {
            if (_lastObservedSimHours >= 0 && _lastObservedSimHoursMsec > 0 && now > _lastObservedSimHoursMsec)
            {
                var dtStep = Math.Clamp((now - _lastObservedSimHoursMsec) / 1000.0f, 0.05f, 0.35f);
                _simStepIntervalEma = _simStepIntervalEma * 0.75f + dtStep * 0.25f;
            }
            _lastObservedSimHours = hours;
            _lastObservedSimHoursMsec = now;
        }
        // Velocity-adaptive damping: stretch glide smoothly across the measured step interval
        // so divisions glide silkily rather than sprinting and freezing on each tick.
        var rate = speed >= 8
            ? Math.Clamp(2.8f / MathF.Max(0.06f, _simStepIntervalEma), 8.0f, 18.0f)
            : speed == 7 ? 14.0f : 6.5f;
        var blend = Mathf.Clamp(1f - MathF.Exp(-delta * rate), 0f, 1f);
        var isMobileOrTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true ||
                              Platform.PlatformProfile.Current?.IsMobile == true;
        var flatWrapDue = _flatMap && (!float.IsFinite(_motionScanFlatX) ||
            MathF.Abs(_flatX - _motionScanFlatX) >= (_isActivelyPanning ? 0.25f : 0.001f));
        var structureFp = HashCode.Combine(_armyStructureFingerprint, _lockFreeArmyStructureFingerprint);
        if (_armyStacks.Count != _motionScanCount || structureFp != _motionScanFp ||
            _motionScanFlat != _flatMap || hours != _motionScanHours || flatWrapDue || now >= _nextMotionScanMs)
        {
            _motionScanCount = _armyStacks.Count; _motionScanFp = structureFp;
            _motionScanFlat = _flatMap; _motionScanHours = hours; _motionScanFlatX = _flatX;
            _nextMotionScanMs = now + (_isActivelyPanning ? 250UL : (isMobileOrTouch ? 160UL : 50UL));
            _motionActiveStacks.Clear();
            foreach (var marker in _armyStacks.Values)
            {
                var node = marker.Node;
                if (node is null || !IsInstanceValid(node) || !node.Visible) continue;
                var moving = marker.CachedIsMoving;
                var glide = moving || marker.HasPendingArrivalGlide;
                var unsettled = !marker.MotionSettled || (marker.TargetPosition - marker.DisplayPosition).LengthSquared() >= 1e-10f;
                if (!glide && !unsettled && !_flatMap) continue;
                if (glide || unsettled) _motionActiveStacks.Add(marker);
                StepArmyMarkerMotion(marker, blend, moving);
            }
            return;
        }
        for (var i = 0; i < _motionActiveStacks.Count; i++)
        {
            var marker = _motionActiveStacks[i];
            if (marker.Node is null || !IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
            StepArmyMarkerMotion(marker, blend, marker.CachedIsMoving);
        }
    }

    /// <summary>Projection switches and fresh maps place counters immediately; normal motion
    /// is eased by <see cref="UpdateArmyMarkerMotion"/>.</summary>
    private void SnapArmyMarkerPositions()
    {
        _motionActiveStacks.Clear();
        _motionScanFp = int.MinValue;
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node)) continue;
            var position = ArmyMarkerPosition(marker);
            marker.TargetPosition = position;
            marker.DisplayPosition = position;
            marker.MotionSettled = true;
            marker.Node.Position = position;
            OrientSurfaceArmyCard(marker);
            if (!_flatMap) SizeSurfaceArmyCounter(marker);
        }
    }

    /// <summary>
    /// Splits only the pixels, never the geography. One stack keeps the province centre; two
    /// or more stacks of different nations in the same province are arranged in a compact
    /// two-column grid around it. The grid is recomputed with the same cadence as auxiliary
    /// card layout, so zooming does not leave stale gaps.
    /// </summary>
    private void LayoutFlatArmyStackOffsets()
    {
        foreach (var marker in _armyStacks.Values) marker.LayoutOffset = Vector2.Zero;
        if (!_flatMap) return;
        var poolIndex = 0;
        foreach (var list in _provinceStackScratch.Values) list.Clear();
        _provinceStackScratch.Clear();

        foreach (var marker in _armyStacks.Values)
        {
            var provinceId = marker.EffectiveProvinceId;
            if (provinceId <= 0) continue;
            if (!_provinceStackScratch.TryGetValue(provinceId, out var list))
            {
                list = poolIndex < _provinceStackPool.Count ? _provinceStackPool[poolIndex++] : null;
                if (list is not null) list.Clear();
                else { list = new List<ArmyStackMarker>(4); _provinceStackPool.Add(list); poolIndex++; }
                _provinceStackScratch[provinceId] = list;
            }
            list.Add(marker);
        }

        foreach (var markers in _provinceStackScratch.Values)
        {
            if (markers.Count < 2) continue;
            markers.Sort(static (a, b) =>
            {
                var c = a.NationId.CompareTo(b.NationId);
                return c != 0 ? c : a.EffectiveLeadArmyId.CompareTo(b.EffectiveLeadArmyId);
            });
            var columns = markers.Count <= 2 ? markers.Count : 2;
            var rows = (markers.Count + columns - 1) / columns;
            var (width, height) = (64f, 40f);
            if (markers[0].Plate?.Texture is { } texture)
            {
                width = texture.GetWidth() * markers[0].Plate.PixelSize * _counterFlatFar;
                height = texture.GetHeight() * markers[0].Plate.PixelSize * _counterFlatFar;
            }
            var gap = Math.Clamp(Ui.Metric("map.flat_counter_gap", 4f), 0f, 16f);
            for (var index = 0; index < markers.Count; index++)
                markers[index].LayoutOffset = new Vector2(
                    (index % columns - (columns - 1) * .5f) * (width + gap),
                    (index / columns - (rows - 1) * .5f) * (height + gap));
        }
    }

    private void DrawFlatCounterLeaders()
    {
        if (!_flatMap || !MilitaryMarkersEnabled || !IsInstanceValid(_armyRoot) ||
            !IsInstanceValid(_camera) || !IsInstanceValid(_supportMarkerRoot) || !IsInstanceValid(_fleetRoot))
            return;
        var minDepth = MathF.Max(0.05f, GlobeLineSanity.FiniteOr(_camera.Near, 0.05f));
        var maxScreenSpan = MathF.Max(Size.X, Size.Y) * 1.5f + 512f;
        foreach (var counter in _auxiliaryCounters)
        {
            if (!IsInstanceValid(counter.Node) || !counter.Node.IsVisibleInTree() ||
                counter.Plate?.Texture is null)
                continue;
            Vector3 localAnchor;
            if (counter.Support is { } support)
            {
                if (!GlobeLineSanity.IsValidGeo(support.Latitude, support.Longitude)) continue;
                localAnchor = LocalFlatPoint(support.Latitude, support.Longitude, FlatMarkerZ - .002f);
            }
            else
            {
                if (!GlobeLineSanity.IsFinite(ToNum(counter.Anchor))) continue;
                localAnchor = counter.Anchor with
                {
                    X = DateLineSeam.NearestCopy(counter.Anchor.X, counter.Node.Position.X, FlatMapWidth)
                };
            }
            if (!GlobeLineSanity.IsValidEmittedVertex(ToNum(localAnchor), true, _flatX)) continue;
            var anchor = counter.Support is not null ? _supportMarkerRoot.ToGlobal(localAnchor) : _fleetRoot.ToGlobal(localAnchor);
            var nodeWorld = counter.Node.GlobalPosition;
            if (!GlobeLineSanity.IsFinite(ToNum(anchor)) || !GlobeLineSanity.IsFinite(ToNum(nodeWorld))) continue;
            if (_camera.IsPositionBehind(anchor) || _camera.IsPositionBehind(nodeWorld)) continue;
            var depthA = -_camera.ToLocal(anchor).Z;
            var depthB = -_camera.ToLocal(nodeWorld).Z;
            var at = _camera.UnprojectPosition(anchor);
            var center = _camera.UnprojectPosition(nodeWorld);
            if (!GlobeLineSanity.IsValidScreenRibbonSegment(ToNum(at), ToNum(center), depthA, depthB, minDepth, maxScreenSpan))
                continue;
            var delta = at - center;
            var half = new Vector2(counter.Plate.Texture.GetWidth(), counter.Plate.Texture.GetHeight()) *
                GlobeLineSanity.FiniteOr(counter.Plate.PixelSize * _counterFlatFar * .5f, 1f);
            var ratio = Math.Min(half.X / Math.Max(.01f, Math.Abs(delta.X)), half.Y / Math.Max(.01f, Math.Abs(delta.Y)));
            if (!float.IsFinite(ratio) || ratio >= 1f || delta.LengthSquared() < 36f) continue;
            var edgePt = center + delta * ratio;
            if (!GlobeLineSanity.IsFinite(ToNum(edgePt))) continue;
            DrawLine(at, edgePt, new Color(.83f, .87f, .78f, .3f), 1f, true);
        }
    }

    private void InvalidateFlatCounterDensity()
    {
        _counterDensityDirty = true;
        _nextCounterDensityMs = 0;
    }

    /// <summary>Only auxiliary counters search for arbitrary free space. Army counters keep
    /// their province as the anchor; when several nations share one province they are fanned
    /// out inside it by a deterministic screen-pixel grid, never moved to another province.</summary>
    private void UpdateFlatCounterDensity()
    {
        var now = Time.GetTicksMsec();
        var unitsPerPixel = _flatZoom / Math.Max(1f, Size.Y);
        var moved = Mathf.Abs(_flatX - _densityView.X) + Mathf.Abs(_flatY - _densityView.Y);
        var zoomed = Mathf.Abs(_flatZoom - _densityView.Z);
        var panBucket = _isActivelyPanning ? 12f : 3f;
        var zoomBucket = _isActivelyPanning ? 0.008f : 0.0015f;
        if (!_counterDensityDirty && _densityViewport == Size &&
            moved < unitsPerPixel * panBucket && zoomed < _flatZoom * zoomBucket) return;
        if (now < _nextCounterDensityMs) return;
        _nextCounterDensityMs = now + (_isActivelyPanning ? 140UL : 50UL);
        _counterDensityDirty = false;
        _densityViewport = Size;
        _densityView = new Vector3(_flatX, _flatY, _flatZoom);
        ClearCounterRooms();
        LayoutFlatArmyStackOffsets();
        var screenBounds = new Rect2(-80, -50, Size.X + 160, Size.Y + 100);
        _strategicHosts.Clear();
        foreach (var list in _strategicMerges.Values) list.Clear();
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
            marker.TargetPosition = ArmyMarkerPosition(marker);
            // The room a counter takes up is the room it is drawn in, which is where the
            // eased position is rather than where the province is - otherwise two counters
            // mid-glide overlap for the length of the glide.
            marker.Node.Position = marker.DisplayPosition;
            // Auxiliary base/port cards may fit around armies, never the reverse.
            if (!marker.Node.IsVisibleInTree() || _camera.IsPositionBehind(marker.Node.GlobalPosition)) continue;
            var at = _camera.UnprojectPosition(marker.Node.GlobalPosition);
            if (!screenBounds.HasPoint(at)) continue;
            var half = marker.Plate.Texture.GetSize() * marker.Plate.PixelSize * _counterFlatFar * .5f;
            var room = new Rect2(at - half, half * 2f);
            // The counters of one country that would overlap become one counter with all their
            // divisions - as a Paradox map does - instead of forty plates and forty labels piled
            // on each other until only the labels could be read. At every zoom, not only the
            // farthest: South Korea's thirty-four divisions covered the whole country, its name
            // included, at the zoom a player reads a region at. Close in they stand apart and
            // nothing merges; at the strategic distance a near miss merges too.
            var reach = _flatStrategicCounters ? half.X * 0.35f : -half.X * 0.25f;
            // Between the tactical close-up and the world view, a country's army reads by army
            // group, as a staff map does: every division of one group within a few counters of
            // another merges into it, whatever the gap. A front where two countries' counters
            // alternate never overlapped enough to merge, and stood as a solid wall of plates from
            // Kharkiv to Kherson. Close in, every division is its own counter again.
            var operational = !_flatStrategicCounters && _flatZoom > Ui.Metric("map.flat_counter_group_zoom", 1.15f);
            var groupRadius = half.X * 2f * Ui.Metric("map.flat_counter_group_radius", 4.5f);
            var group = marker.EffectiveArmyGroupId;
            ArmyStackMarker host = null;
            foreach (var (candidate, taken) in _strategicHosts)
            {
                if (candidate.NationId != marker.NationId) continue;
                if (taken.Grow(reach).Intersects(room)) { host = candidate; break; }
                if (operational && group >= 0 && candidate.EffectiveArmyGroupId == group &&
                    taken.GetCenter().DistanceTo(room.GetCenter()) <= groupRadius) { host = candidate; break; }
            }
            if (host is not null)
            {
                if (!_strategicMerges.TryGetValue(host, out var merged)) _strategicMerges[host] = merged = new List<ArmyStackMarker>();
                merged.Add(marker);
                continue;
            }
            _strategicHosts.Add((marker, room));
        }
        // The hosts show everything they took in; the counters taken in show nothing, and a click
        // on one selects the whole merged group.
        ApplyStrategicFlatCounterHosts();
        LayoutFlatAuxiliaryCounters();
        QueueRedraw();
    }

    private void ApplyStrategicFlatCounterHosts()
    {
        foreach (var (host, room) in _strategicHosts)
        {
            _strategicMerges.TryGetValue(host, out var merged);
            RefreshProvinceCounterDisplay(host, _flatStrategicCounters, merged);
            TakeCounterRoom(room);
            if (merged is null) continue;
            var ids = host.Node.GetMeta("display_army_ids");
            foreach (var taken in merged)
            {
                taken.Plate.Visible = false; taken.Count.Visible = false;
                if (taken.ConditionBars is not null) taken.ConditionBars.Visible = false;
                taken.DisplayFingerprint = 0; taken.Node.SetMeta("display_army_ids", ids);
            }
        }
    }

    private void RefreshProvinceCounterDisplay(ArmyStackMarker marker, bool compact, List<ArmyStackMarker> merged = null)
    {
        marker.DisplayStack.Clear();
        marker.LockFreeDisplayArmyIds.Clear();
        if (marker.IsLockFreeSynced && marker.LockFreeArmyIds.Count > 0)
        {
            marker.LockFreeDisplayArmyIds.AddRange(marker.LockFreeArmyIds);
            if (merged is not null)
            {
                foreach (var taken in merged)
                {
                    if (taken.LockFreeArmyIds.Count > 0) marker.LockFreeDisplayArmyIds.AddRange(taken.LockFreeArmyIds);
                    else for (var i = 0; i < taken.Stack.Count; i++) marker.LockFreeDisplayArmyIds.Add(taken.Stack[i].Id);
                }
            }
            if (marker.Stack.Count > 0) marker.DisplayStack.AddRange(marker.Stack);
            if (merged is not null)
                foreach (var taken in merged)
                    if (taken.Stack.Count > 0) marker.DisplayStack.AddRange(taken.Stack);
            marker.DisplayProvinces = 1;
            marker.Compact = compact;
            marker.Plate.Visible = true;
            marker.Plate.Scale = Vector3.One;
            marker.Count.Visible = true;
            var lfDivs = marker.LockFreeDivisions;
            var lfPers = marker.LockFreePersonnel;
            if (merged is not null)
                foreach (var taken in merged) { lfDivs += taken.LockFreeDivisions; lfPers += taken.LockFreePersonnel; }
            var lfDivCount = Mathf.CeilToInt(lfDivs);
            var lfMenTenths = (int)MathF.Round(lfPers / 100f);
            if (marker.ShownDivisions != lfDivCount || (!compact && marker.ShownMenTenths != lfMenTenths) || marker.ShownCompact != compact)
            {
                marker.ShownDivisions = lfDivCount; marker.ShownMenTenths = lfMenTenths; marker.ShownCompact = compact;
                var text = compact ? lfDivCount.ToString() : $"{lfDivCount}\n{lfMenTenths / 10f:0.#}k";
                marker.CountText = text;
                if (!string.Equals(marker.Count.Text, text, StringComparison.Ordinal)) marker.Count.Text = text;
            }
            var lfFp = new HashCode();
            for (var i = 0; i < marker.LockFreeDisplayArmyIds.Count; i++) lfFp.Add(marker.LockFreeDisplayArmyIds[i]);
            var lfSig = lfFp.ToHashCode();
            if (lfSig != marker.DisplayFingerprint)
            {
                marker.DisplayFingerprint = lfSig;
                var ids = new Godot.Collections.Array<int>();
                for (var i = 0; i < marker.LockFreeDisplayArmyIds.Count; i++) ids.Add(marker.LockFreeDisplayArmyIds[i]);
                marker.Node.SetMeta("display_army_ids", ids);
                marker.Node.SetMeta("display_provinces", 1);
            }
            ApplyLockFreeArmyConditionBars(marker);
            return;
        }
        marker.DisplayStack.AddRange(marker.Stack);
        if (merged is not null)
            foreach (var taken in merged) marker.DisplayStack.AddRange(taken.Stack);
        marker.DisplayProvinces = 1;
        marker.Compact = compact;
        marker.Plate.Visible = true;
        marker.Plate.Scale = Vector3.One;
        marker.Count.Visible = true;
        float divisions = 0f, personnel = 0f;
        var fingerprint = new HashCode();
        foreach (var army in marker.DisplayStack)
        {
            divisions += army.Divisions;
            personnel += DivisionPersonnel(army);
            fingerprint.Add(army.Id);
        }
        var divCount = Mathf.CeilToInt(divisions);
        var menTenths = (int)MathF.Round(personnel / 100f);
        if (marker.ShownDivisions != divCount || (!compact && marker.ShownMenTenths != menTenths) || marker.ShownCompact != compact)
        {
            marker.ShownDivisions = divCount; marker.ShownMenTenths = menTenths; marker.ShownCompact = compact;
            var text = compact ? divCount.ToString() : $"{divCount}\n{menTenths / 10f:0.#}k";
            marker.CountText = text;
            if (marker.Count.Text != text) marker.Count.Text = text;
        }
        var signature = fingerprint.ToHashCode();
        if (signature != marker.DisplayFingerprint)
        {
            marker.DisplayFingerprint = signature;
            var ids = new Godot.Collections.Array<int>();
            foreach (var army in marker.DisplayStack) ids.Add(army.Id);
            marker.Node.SetMeta("display_army_ids", ids);
            marker.Node.SetMeta("display_provinces", 1);
        }
        UpdateArmyConditionBars(marker);
    }

    private bool CounterRoomOccupied(Rect2 room)
    {
        for (var y = Mathf.FloorToInt(room.Position.Y / 64); y <= Mathf.FloorToInt(room.End.Y / 64); y++)
        for (var x = Mathf.FloorToInt(room.Position.X / 64); x <= Mathf.FloorToInt(room.End.X / 64); x++)
            if (_occupiedCounterCells.TryGetValue(new Vector2I(x, y), out var entries))
                foreach (var entry in entries)
                    if (entry.Intersects(room)) return true;
        return false;
    }

    private bool TryFindFreeCounterOffset(Rect2 room, out Vector2 result, Func<Vector2,bool> allowed = null)
    {
        result = Vector2.Zero;
        if (!CounterRoomOccupied(room) && (allowed is null || allowed(Vector2.Zero))) return true;
        var limit = Math.Clamp(Ui.Metric("map.flat_counter_local_radius", 96), 48, 160);
        // Bounded rectangular spiral; prefer the nearest free slot on each ring.
        // Stable auxiliary-id ordering keeps base/port layout deterministic.
        for (var ring = 1; ring <= 3; ring++)
        {
            var best = Vector2.Zero;
            var bestDistance = float.PositiveInfinity;
            for (var y = -ring; y <= ring; y++)
            for (var x = -ring; x <= ring; x++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) != ring) continue;
                var offset = new Vector2(x * (room.Size.X + 1f), y * (room.Size.Y + 1f));
                if (offset.LengthSquared() > limit * limit || offset.LengthSquared() >= bestDistance) continue;
                if (CounterRoomOccupied(new Rect2(room.Position + offset, room.Size))) continue;
                if (allowed is not null && !allowed(offset)) continue;
                best = offset;
                bestDistance = offset.LengthSquared();
            }
            if (!float.IsPositiveInfinity(bestDistance)) { result = best; return true; }
        }
        // Extreme modded crowds can exceed the bounded layout budget; never discard a unit.
        return false;
    }

    private Vector3 FlatFleetPosition(int id, float latitude, float longitude)
    {
        var anchor = LocalFlatPoint(latitude, longitude, FlatMarkerZ);
        var offset = _flatFleetOffsets.GetValueOrDefault(id);
        return anchor + new Vector3(offset.X, -offset.Y, 0f) * (_camera.Size / Math.Max(1f, Size.Y));
    }

    /// <summary>Auxiliary cards fit around anchored province stacks. These offsets
    /// affect only base/port presentation, never army geography or save data.</summary>
    private void LayoutFlatAuxiliaryCounters()
    {
        _auxiliaryCounters.Clear();
        // Support uses a province-constrained layout in both projections, after army
        // placement. The unconstrained fleet/port spiral must never move an airbase.
        foreach (var (id, parts) in _fleetNodes)
        {
            if (!parts.Node.IsVisibleInTree() || !_fleetByMarker.TryGetValue(parts.Node, out var fleet)) continue;
            _auxiliaryCounters.Add((parts.Node, parts.Plate, LocalFlatPoint(fleet.Latitude, fleet.Longitude, FlatMarkerZ), id, null));
        }
        _auxiliaryCounters.Sort((a, b) => (a.Support is null) != (b.Support is null)
            ? (a.Support is null ? 1 : -1) : a.Id.CompareTo(b.Id));
        foreach (var counter in _auxiliaryCounters)
        {
            var root = counter.Support is null ? _fleetRoot : _supportMarkerRoot;
            var at = _camera.UnprojectPosition(root.ToGlobal(counter.Anchor));
            if (counter.Support is { } support) support.DisplayOffset = Vector2.Zero;
            else _flatFleetOffsets[counter.Id] = Vector2.Zero;
            var offset = Vector2.Zero;
            var crowded = false;
            if (at.X >= -80 && at.Y >= -50 && at.X <= Size.X + 80 && at.Y <= Size.Y + 50)
            {
                var gap = Math.Clamp(Ui.Metric("map.flat_counter_gap", 4f), 0f, 16f);
                var size = new Vector2(counter.Plate.Texture.GetWidth(), counter.Plate.Texture.GetHeight()) *
                    counter.Plate.PixelSize * _counterFlatFar + Vector2.One * gap;
                var room = new Rect2(at - size * .5f, size);
                // Long-range support belongs behind the line, not on top of it. Its card gets a
                // one-card tail bias only when a land counter already holds the province centre;
                // a base with no army still draws its support card at the base itself.
                var supportBias = counter.Support is null || !CounterRoomOccupied(room)
                    ? Vector2.Zero
                    : new Vector2(0f, -size.Y * Math.Clamp(Ui.Metric("map.flat_support_bias", 1f), 0f, 2.5f));
                room.Position += supportBias;
                var found = TryFindFreeCounterOffset(room, out var slot);
                offset = found ? slot : supportBias;
                if (!found && counter.Support is not null) crowded = true;
                room.Position += offset;
                TakeCounterRoom(room);
                offset += supportBias;
            }
            if (counter.Support is { } marker) marker.DisplayOffset = offset;
            else _flatFleetOffsets[counter.Id] = offset;
            counter.Node.Position = counter.Anchor + new Vector3(offset.X, -offset.Y, 0f) * (_camera.Size / Math.Max(1f, Size.Y));
            // A crowded auxiliary card recedes instead of masking a division: it draws
            // behind the line already, and the fade keeps even that behind-line legible.
            counter.Plate.Modulate = Colors.White with { A = crowded ? 0.45f : 1f };
            if (counter.Support is { } supportMarker) supportMarker.DisplayFaded = crowded;
        }
    }

    private void TakeCounterRoom(Rect2 room)
    {
        for (var y = Mathf.FloorToInt(room.Position.Y / 64); y <= Mathf.FloorToInt(room.End.Y / 64); y++)
        for (var x = Mathf.FloorToInt(room.Position.X / 64); x <= Mathf.FloorToInt(room.End.X / 64); x++)
        {
            if (!_occupiedCounterCells.TryGetValue(new Vector2I(x, y), out var entries))
                _occupiedCounterCells[new Vector2I(x, y)] = entries = new();
            entries.Add(room);
        }
    }

    private void ClearCounterRooms()
    {
        // Screen-space buckets are bounded by viewport size. Reuse their lists rather
        // than allocate them again for every camera/layout refresh.
        if(_occupiedCounterCells.Count>4096)_occupiedCounterCells.Clear();
        else foreach(var entries in _occupiedCounterCells.Values)entries.Clear();
    }
}
