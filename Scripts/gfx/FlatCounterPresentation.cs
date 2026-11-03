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
    private void UpdateArmyMarkerMotion(float delta)
    {
        if (!IsInstanceValid(_armyRoot) || _armyStacks.Count == 0) return;
        // Frame-rate independent ease: the same glide at 30fps and at 144fps. The constant is
        // how much of the remaining distance is covered per 60th of a second.
        var blend = 1f - MathF.Exp(-delta * 6.5f);
        blend = Mathf.Clamp(blend, 0f, 1f);
        foreach (var marker in _armyStacks.Values)
        {
            var node = marker.Node;
            if (node is null || !IsInstanceValid(node)) continue;
            if (!marker.MotionSettled)
            {
                // A brand-new counter appears where it belongs. Nothing flies in from nowhere.
                marker.DisplayPosition = marker.TargetPosition;
                marker.MotionSettled = true;
                node.Position = marker.DisplayPosition;
                continue;
            }
            var isMoving = false;
            for (var i = 0; i < marker.Stack.Count; i++)
            {
                var army = marker.Stack[i];
                if (army.IsMoving && army.Route.Count > 0)
                {
                    isMoving = true;
                    break;
                }
            }
            if (isMoving)
            {
                marker.TargetPosition = ArmyMarkerPosition(marker);
            }
            var remaining = marker.TargetPosition - marker.DisplayPosition;
            if (remaining.LengthSquared() < 1e-10f) continue;
            // A camera pan or zoom moves the target of a stationary counter every frame. That
            // is not the formation marching, and easing it would make the whole map swim.
            if (!isMoving)
            {
                marker.DisplayPosition = marker.TargetPosition;
                node.Position = marker.DisplayPosition;
                OrientSurfaceArmyCard(marker);
                if (!_flatMap) SizeSurfaceArmyCounter(marker);
                continue;
            }
            marker.DisplayPosition += remaining * blend;
            // On the globe the card lies on the surface, so the eased point is pushed back out
            // to the sphere rather than cutting the chord through the planet.
            if (!_flatMap)
            {
                var length = marker.DisplayPosition.Length();
                if (length > 0.0001f) marker.DisplayPosition *= SurfaceMarkerRadius / length;
            }
            node.Position = marker.DisplayPosition;
            // Orientation follows the ground under the card, so it has to be recomputed as the
            // card slides rather than left at the bearing it was created with.
            OrientSurfaceArmyCard(marker);
            if (!_flatMap) SizeSurfaceArmyCounter(marker);
        }
    }

    /// <summary>Projection switches and fresh maps place counters immediately; normal motion
    /// is eased by <see cref="UpdateArmyMarkerMotion"/>.</summary>
    private void SnapArmyMarkerPositions()
    {
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
            if (marker.Stack.Count == 0 || marker.Stack[0].ProvinceId <= 0) continue;
            var provinceId = marker.Stack[0].ProvinceId;
            if (!_provinceStackScratch.TryGetValue(provinceId, out var list))
            {
                if (poolIndex < _provinceStackPool.Count)
                {
                    list = _provinceStackPool[poolIndex++];
                    list.Clear();
                }
                else
                {
                    list = new List<ArmyStackMarker>(4);
                    _provinceStackPool.Add(list);
                    poolIndex++;
                }
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
                return c != 0 ? c : a.Stack[0].Id.CompareTo(b.Stack[0].Id);
            });
            var columns = markers.Count <= 2 ? markers.Count : 2;
            var rows = (markers.Count + columns - 1) / columns;
            var width = 64f;
            var height = 40f;
            if (markers[0].Plate?.Texture is { } texture)
            {
                width = texture.GetWidth() * markers[0].Plate.PixelSize * _counterFlatFar;
                height = texture.GetHeight() * markers[0].Plate.PixelSize * _counterFlatFar;
            }
            var gap = Math.Clamp(Ui.Metric("map.flat_counter_gap", 4f), 0f, 16f);
            for (var index = 0; index < markers.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                markers[index].LayoutOffset = new Vector2(
                    (column - (columns - 1) * .5f) * (width + gap),
                    (row - (rows - 1) * .5f) * (height + gap));
            }
        }
    }

    private void DrawFlatCounterLeaders()
    {
        if (!_flatMap || !MilitaryMarkersEnabled || !IsInstanceValid(_armyRoot)) return;
        foreach (var counter in _auxiliaryCounters)
        {
            if (!IsInstanceValid(counter.Node) || !counter.Node.IsVisibleInTree()) continue;
            var anchor = counter.Support is { } support
                ? _supportMarkerRoot.ToGlobal(FlatPoint(support.Latitude, support.Longitude, FlatMarkerZ - .002f))
                : _fleetRoot.ToGlobal(counter.Anchor);
            var at = _camera.UnprojectPosition(anchor);
            var center = _camera.UnprojectPosition(counter.Node.GlobalPosition);
            var delta = at - center;
            var half = new Vector2(counter.Plate.Texture.GetWidth(), counter.Plate.Texture.GetHeight()) *
                counter.Plate.PixelSize * _counterFlatFar * .5f;
            var ratio = Math.Min(half.X / Math.Max(.01f, Math.Abs(delta.X)), half.Y / Math.Max(.01f, Math.Abs(delta.Y)));
            if (ratio >= 1f || delta.LengthSquared() < 36f) continue;
            DrawLine(at, center + delta * ratio, new Color(.83f, .87f, .78f, .3f), 1f, true);
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
        if (!_counterDensityDirty && _densityViewport == Size &&
            moved < unitsPerPixel * 3f && zoomed < _flatZoom * 0.0015f) return;
        if (now < _nextCounterDensityMs) return;
        _nextCounterDensityMs = now + 50;
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
            var group = marker.Stack.Count > 0 ? marker.Stack[0].ArmyGroupId : -1;
            ArmyStackMarker host = null;
            foreach (var (candidate, taken) in _strategicHosts)
            {
                if (candidate.NationId != marker.NationId) continue;
                if (taken.Grow(reach).Intersects(room)) { host = candidate; break; }
                if (operational && group >= 0 && candidate.Stack.Count > 0 && candidate.Stack[0].ArmyGroupId == group &&
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
        foreach (var (host, room) in _strategicHosts)
        {
            _strategicMerges.TryGetValue(host, out var merged);
            RefreshProvinceCounterDisplay(host, _flatStrategicCounters, merged);
            TakeCounterRoom(room);
            if (merged is null) continue;
            var ids = host.Node.GetMeta("display_army_ids");
            foreach (var taken in merged)
            {
                taken.Plate.Visible = false;
                taken.Count.Visible = false;
                if (taken.ConditionBars is not null) taken.ConditionBars.Visible = false;
                taken.DisplayFingerprint = 0;
                taken.Node.SetMeta("display_army_ids", ids);
            }
        }
        LayoutFlatAuxiliaryCounters();
        QueueRedraw();
    }

    private void RefreshProvinceCounterDisplay(ArmyStackMarker marker, bool compact, List<ArmyStackMarker> merged = null)
    {
        marker.DisplayStack.Clear();
        marker.DisplayStack.AddRange(marker.Stack);
        if (merged is not null)
            foreach (var taken in merged) marker.DisplayStack.AddRange(taken.Stack);
        marker.DisplayProvinces = 1;
        marker.Compact = compact;
        marker.Plate.Visible = true;
        marker.Plate.Scale = Vector3.One;
        marker.Count.Visible = true;
        var divisions = 0f;
        var personnel = 0f;
        var fingerprint = new HashCode();
        foreach (var army in marker.DisplayStack)
        {
            divisions += army.Divisions;
            personnel += DivisionPersonnel(army);
            fingerprint.Add(army.Id);
        }
        var divCount = Mathf.CeilToInt(divisions);
        var menTenths = (int)MathF.Round(personnel / 100f);
        var textChanged = marker.ShownDivisions != divCount ||
                          (!compact && marker.ShownMenTenths != menTenths) ||
                          marker.ShownCompact != compact;
        if (textChanged)
        {
            marker.ShownDivisions = divCount;
            marker.ShownMenTenths = menTenths;
            marker.ShownCompact = compact;
            var text = compact
                ? divCount.ToString()
                : $"{divCount}\n{menTenths / 10f:0.#}k";
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

    private Vector2 FindFreeCounterOffset(Rect2 room)
    {
        return TryFindFreeCounterOffset(room, out var offset) ? offset : Vector2.Zero;
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
                if (offset.LengthSquared() > limit * limit) continue;
                if (offset.LengthSquared() >= bestDistance) continue;
                var test = new Rect2(room.Position + offset, room.Size);
                if (CounterRoomOccupied(test)) continue;
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
        var anchor = FlatPoint(latitude, longitude, FlatMarkerZ);
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
            _auxiliaryCounters.Add((parts.Node, parts.Plate, FlatPoint(fleet.Latitude, fleet.Longitude, FlatMarkerZ), id, null));
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
            var plate = counter.Plate;
            plate.Modulate = new Color(1f, 1f, 1f, crowded ? 0.45f : 1f);
            if (counter.Support is { } supportMarker) supportMarker.DisplayFaded = crowded;
        }
    }

    private void TakeCounterRoom(Rect2 room)
    {
        for (var y = Mathf.FloorToInt(room.Position.Y / 64); y <= Mathf.FloorToInt(room.End.Y / 64); y++)
        for (var x = Mathf.FloorToInt(room.Position.X / 64); x <= Mathf.FloorToInt(room.End.X / 64); x++)
        {
            var cell = new Vector2I(x, y);
            if (!_occupiedCounterCells.TryGetValue(cell, out var entries))
                _occupiedCounterCells[cell] = entries = new();
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
