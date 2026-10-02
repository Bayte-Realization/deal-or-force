using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly List<(ArmyStackMarker Marker, Vector2 Screen, bool Selected)> _densityCandidates = new();
    private readonly Dictionary<Vector2I, List<(ArmyStackMarker Marker, Rect2 Room, bool Selected)>> _occupiedCounterCells = new();
    private readonly List<(Node3D Node, Sprite3D Plate, Vector3 Anchor, int Id, SupportMapMarker Support)> _auxiliaryCounters = new();
    private readonly Dictionary<int, Vector2> _flatFleetOffsets = new();
    private readonly Dictionary<ArmyStackMarker, Vector2> _provinceCounterSlots = new();
    private readonly Dictionary<(int Nation, int Province), List<ArmyStackMarker>> _provinceCounterGroups = new();
    private ulong _nextCounterDensityMs;
    private Vector3 _densityView = new(float.NaN, float.NaN, float.NaN);
    private Vector2 _densityViewport;
    private bool _counterDensityDirty = true;
    private bool _flatStrategicCounters;
    private bool _flatIndividualDivisions;
    private bool _surfaceIndividualDivisions;

    private void UpdateFlatDivisionDetail()
    {
        var threshold = Math.Clamp(Simulation.InterfaceSkin.Metric("map.flat_individual_zoom", .40f), .15f, 1.5f);
        var hysteresis = Math.Clamp(Simulation.InterfaceSkin.Metric("map.flat_individual_hysteresis", .05f), .02f, .3f);
        var individual = _flatMap && _flatZoom <= threshold + (_flatIndividualDivisions ? hysteresis : 0f);
        var surfaceThreshold = Math.Clamp(Ui.Metric("map.globe_individual_distance", 5.9f), 5.5f, 7f);
        var surfaceIndividual = !_flatMap && ViewDistance <= surfaceThreshold + (_surfaceIndividualDivisions ? .12f : 0f);
        if (individual == _flatIndividualDivisions && surfaceIndividual == _surfaceIndividualDivisions) return;
        _flatIndividualDivisions = individual;
        _surfaceIndividualDivisions = surfaceIndividual;
        foreach (var marker in _armyStacks.Values) marker.DisplayOffset = Vector2.Zero;
        if (_mapApplied && _campaign is not null) ReconcileArmyMarkers(_campaign);
        InvalidateFlatCounterDensity();
    }

    private Vector3 ArmyMarkerPosition(ArmyStackMarker marker)
    {
        var anchor = ArmyPresentationPoint(marker.Stack);
        if (!_flatMap)
        {
            var geo = GeographicFromNormal(anchor.Normalized());
            var basis = CountryLabelSurfaceBasis(geo.Latitude, geo.Longitude, 1f, 0f);
            return (anchor + basis.X * marker.SurfaceOffset.X + basis.Y * marker.SurfaceOffset.Y).Normalized() * anchor.Length();
        }
        var pixel = _camera.Size / Math.Max(1f, Size.Y);
        return anchor + new Vector3(marker.DisplayOffset.X, -marker.DisplayOffset.Y, 0f) * pixel;
    }

    private void DrawFlatCounterLeaders()
    {
        if (!_flatMap || !MilitaryMarkersEnabled || !IsInstanceValid(_armyRoot)) return;
        foreach (var marker in _armyStacks.Values)
        {
            if (!IsInstanceValid(marker.Node) || !marker.Node.Visible || !marker.Plate.Visible ||
                marker.DisplayOffset.LengthSquared() < 36f) continue;
            // A whole front must not become a web of leader lines. Explain geography
            // only for the selected formations; the other cards stay in local bundles.
            var selected = false;
            foreach (var army in marker.DisplayStack)
                if (_selectedArmyIds.Contains(army.Id)) { selected = true; break; }
            if (!selected) continue;
            var anchor = _camera.UnprojectPosition(_armyRoot.ToGlobal(ArmyPresentationPoint(marker.Stack)));
            var center = _camera.UnprojectPosition(marker.Node.GlobalPosition);
            var half = new Vector2(marker.Plate.Texture.GetWidth(), marker.Plate.Texture.GetHeight()) *
                marker.Plate.PixelSize * _counterFlatFar * .5f;
            var delta = anchor - center;
            var edgeRatio = Math.Min(half.X / Math.Max(.01f, Math.Abs(delta.X)),
                half.Y / Math.Max(.01f, Math.Abs(delta.Y)));
            if (edgeRatio >= 1f) continue;
            DrawLine(anchor, center + delta * edgeRatio, new Color(.83f, .87f, .78f, .35f), 1f, true);
            DrawCircle(anchor, 1.5f, new Color(.83f, .87f, .78f, .5f));
        }
        foreach (var counter in _auxiliaryCounters)
        {
            if (!IsInstanceValid(counter.Node) || !counter.Node.IsVisibleInTree()) continue;
            var anchor = counter.Support is { } support
                ? _supportMarkerRoot.ToGlobal(FlatPoint(support.Latitude, support.Longitude, FlatMarkerZ + .002f))
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
        _cityDirty = true;
    }

    /// <summary>
    /// Overlapping friendly cards represent all nearby divisions instead of silently
    /// dropping them. Simulation stacks and real province positions remain unchanged.
    /// Opposing nations and different selection states never merge. Stable hosts and
    /// rectangle hysteresis avoid grid-boundary flicker during a pan.
    /// </summary>
    private void UpdateFlatCounterDensity()
    {
        // Throttle collision layout, not the per-frame pixel-size compensation.
        var now = Time.GetTicksMsec();
        var unitsPerPixel = _flatZoom / Math.Max(1f, Size.Y);
        var moved = Mathf.Abs(_flatX - _densityView.X) + Mathf.Abs(_flatY - _densityView.Y);
        var zoomed = Mathf.Abs(_flatZoom - _densityView.Z);
        if (!_counterDensityDirty && _densityViewport == Size &&
            moved < unitsPerPixel * 3f && zoomed < _flatZoom * 0.0015f) return;

        // And while it is moving, twenty times a second rather than every frame: fourteen
        // hundred markers unprojected and gridded is not free, and a decision the eye cannot
        // catch up with is not worth making faster.
        if (now < _nextCounterDensityMs) return;
        _nextCounterDensityMs = now + 50;
        _counterDensityDirty = false;
        _densityViewport = Size;
        _densityView = new Vector3(_flatX, _flatY, _flatZoom);
        _densityCandidates.Clear();
        _occupiedCounterCells.Clear();
        _placedCounterHosts.Clear();
        LayoutFlatAuxiliaryCounters();
        foreach (var marker in _armyStacks.Values)
        {
            marker.DisplayStack.Clear();
            marker.DisplayStack.AddRange(marker.Stack);
            marker.DisplayProvinces = 1;
            if (marker.Node is null || !marker.Node.Visible || marker.Stack.Count == 0) continue;
            var selected = false;
            foreach (var army in marker.Stack)
                if (_selectedArmyIds.Contains(army.Id)) { selected = true; break; }
            // Layout always starts from geography, not last frame's visual displacement.
            var screen = _camera.UnprojectPosition(_armyRoot.ToGlobal(ArmyPresentationPoint(marker.Stack)));
            if (screen.X < -80 || screen.Y < -50 || screen.X > Size.X + 80 || screen.Y > Size.Y + 50) continue;
            _densityCandidates.Add((marker, screen, selected));
        }
        _densityCandidates.Sort((a, b) => _flatIndividualDivisions
            ? a.Marker.Stack[0].Id.CompareTo(b.Marker.Stack[0].Id)
            : a.Selected != b.Selected
            ? (a.Selected ? -1 : 1)
            : a.Marker.Plate.Visible != b.Marker.Plate.Visible
                ? (a.Marker.Plate.Visible ? -1 : 1)
                : a.Marker.Stack[0].Id.CompareTo(b.Marker.Stack[0].Id));
        BuildProvinceCounterSlots();
        foreach (var (marker, at, selected) in _densityCandidates)
        {
            var halfWidth = marker.Plate.Texture.GetWidth() * marker.Plate.PixelSize * _counterFlatFar * .5f;
            var halfHeight = marker.Plate.Texture.GetHeight() * marker.Plate.PixelSize * _counterFlatFar * .5f;
            var gap = Math.Clamp(Simulation.InterfaceSkin.Metric("map.flat_counter_gap", 4f), 0f, 16f);
            var half = new Vector2(halfWidth, halfHeight) + Vector2.One * gap * .5f;
            var localSlot = _provinceCounterSlots.GetValueOrDefault(marker);
            var room = new Rect2(at + localSlot - half, half * 2f);
            var hysteresis = Math.Clamp(Simulation.InterfaceSkin.Metric("map.flat_counter_clearance", 6f), 0f, 16f);
            var test = marker.Plate.Visible ? room : room.Grow(hysteresis);
            var host = _flatIndividualDivisions ? null : FindCounterHost(test, marker.NationId, selected);
            var hidden = host is not null;
            marker.DisplayOffset = localSlot;
            if (!hidden)
            {
                if (TryFindFreeCounterOffset(room, out var adjustment))
                    marker.DisplayOffset += adjustment;
                else
                {
                    // When the local space is exhausted, retain membership in a nearby
                    // friendly card instead of spiralling across unrelated provinces.
                    host = FindNearbyCounterHost(at, marker.NationId, selected);
                    hidden = host is not null;
                }
            }
            if (hidden)
            {
                host.DisplayStack.AddRange(marker.Stack);
                host.DisplayProvinces++;
                marker.DisplayStack.Clear();
            }
            else
            {
                // Enemy cards never merge either. Move crowded cards in screen space,
                // retaining geography as their anchor and keeping every division clickable.
                room.Position += marker.DisplayOffset - localSlot;
                TakeCounterRoom(marker, room, selected);
            }
            // Keep these states together even when a sizing pass just ran. Returning only
            // because Compact matched left orphaned numbers above invisible card plates.
            marker.Compact = _flatStrategicCounters;
            marker.Plate.Visible = !hidden;
            marker.Plate.Scale = Vector3.One;
            marker.Count.Visible = !hidden && !_flatStrategicCounters;
            marker.Node.Position = ArmyMarkerPosition(marker);
        }
        QueueRedraw();
        foreach (var (marker, _, _) in _densityCandidates)
        {
            if (!marker.Plate.Visible) continue;
            var divisions = 0f;
            var personnel = 0f;
            var fingerprint = new HashCode();
            foreach (var army in marker.DisplayStack)
            {
                divisions += army.Divisions;
                personnel += DivisionPersonnel(army);
                fingerprint.Add(army.Id);
            }
            var cluster = marker.DisplayProvinces > 1 ? "+" : "";
            var text = $"{Mathf.CeilToInt(divisions)}{cluster}" +
                (_flatStrategicCounters ? "" : $"\n{personnel / 1000f:0.#}k");
            if (marker.Count.Text != text) marker.Count.Text = text;
            // Inspectable display membership; no simulation or save-state mutation.
            var signature = fingerprint.ToHashCode();
            if (signature != marker.DisplayFingerprint)
            {
                marker.DisplayFingerprint = signature;
                var ids = new Godot.Collections.Array<int>();
                foreach (var army in marker.DisplayStack) ids.Add(army.Id);
                marker.Node.SetMeta("display_army_ids", ids);
            }
            marker.Node.SetMeta("display_provinces", marker.DisplayProvinces);
        }
    }

    private void BuildProvinceCounterSlots()
    {
        _provinceCounterSlots.Clear();
        _provinceCounterGroups.Clear();
        if (!_flatIndividualDivisions) return;
        foreach (var (marker, _, _) in _densityCandidates)
        {
            var province = marker.Stack[0].ProvinceId;
            // Unplaced modded formations must not share a fictional province zero.
            var key = (marker.NationId, province > 0 ? province : -marker.Stack[0].Id - 1);
            if (!_provinceCounterGroups.TryGetValue(key, out var group))
                _provinceCounterGroups[key] = group = new();
            group.Add(marker);
        }
        foreach (var group in _provinceCounterGroups.Values)
        {
            group.Sort((a, b) => a.Stack[0].Id.CompareTo(b.Stack[0].Id));
            // A mod may put hundreds of divisions in one province. Overflow reuses
            // these local slots and is merged with its nearby host, never a long train.
            var slots = Math.Min(group.Count, 12);
            var columns = slots <= 4 ? 1 : slots <= 8 ? 2 : 3;
            var rows = (slots + columns - 1) / columns;
            var marker = group[0];
            var gap = Math.Clamp(Ui.Metric("map.flat_counter_gap", 4), 0, 16);
            var step = new Vector2(marker.Plate.Texture.GetWidth(), marker.Plate.Texture.GetHeight()) *
                marker.Plate.PixelSize * _counterFlatFar + Vector2.One * (gap + 1);
            for (var index = 0; index < group.Count; index++)
            {
                var slot = index % slots;
                var row = slot / columns;
                var rowColumns = Math.Min(columns, slots - row * columns);
                _provinceCounterSlots[group[index]] = new Vector2(
                    (slot % columns - (rowColumns - 1) * .5f) * step.X,
                    (row - (rows - 1) * .5f) * step.Y);
            }
        }
    }

    private ArmyStackMarker FindNearbyCounterHost(Vector2 anchor, int nationId, bool selected)
    {
        var limit = Math.Clamp(Ui.Metric("map.flat_counter_local_radius", 96), 48, 160);
        ArmyStackMarker best = null;
        var distance = limit * limit;
        // Inspect anchors, not displaced rectangles: clustering remains geographical.
        foreach (var (candidate, at, candidateSelected) in _densityCandidates)
        {
            if (candidate.NationId != nationId || candidateSelected != selected ||
                candidate.DisplayStack.Count == 0) continue;
            var next = anchor.DistanceSquaredTo(at);
            // Only earlier, already placed cards are valid hosts.
            if (!_placedCounterHosts.Contains(candidate) || next >= distance) continue;
            best = candidate;
            distance = next;
        }
        return best;
    }

    private readonly HashSet<ArmyStackMarker> _placedCounterHosts = new();

    private bool CounterRoomOccupied(Rect2 room)
    {
        for (var y = Mathf.FloorToInt(room.Position.Y / 64); y <= Mathf.FloorToInt(room.End.Y / 64); y++)
        for (var x = Mathf.FloorToInt(room.Position.X / 64); x <= Mathf.FloorToInt(room.End.X / 64); x++)
            if (_occupiedCounterCells.TryGetValue(new Vector2I(x, y), out var entries))
                foreach (var entry in entries)
                    if (entry.Room.Intersects(room)) return true;
        return false;
    }

    private Vector2 FindFreeCounterOffset(Rect2 room)
    {
        return TryFindFreeCounterOffset(room, out var offset) ? offset : Vector2.Zero;
    }

    private bool TryFindFreeCounterOffset(Rect2 room, out Vector2 result)
    {
        result = Vector2.Zero;
        if (!CounterRoomOccupied(room)) return true;
        var limit = Math.Clamp(Ui.Metric("map.flat_counter_local_radius", 96), 48, 160);
        // Bounded rectangular spiral; prefer the nearest free slot on each ring.
        // Stable army-id ordering makes selection and small pans preserve the layout.
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
                best = offset;
                bestDistance = offset.LengthSquared();
            }
            if (!float.IsPositiveInfinity(bestDistance)) { result = best; return true; }
        }
        // Extreme modded crowds can exceed the bounded layout budget; never discard a unit.
        return false;
    }

    private ArmyStackMarker FindCounterHost(Rect2 room, int nationId, bool selected)
    {
        for (var y = Mathf.FloorToInt(room.Position.Y / 64); y <= Mathf.FloorToInt(room.End.Y / 64); y++)
        for (var x = Mathf.FloorToInt(room.Position.X / 64); x <= Mathf.FloorToInt(room.End.X / 64); x++)
            if (_occupiedCounterCells.TryGetValue(new Vector2I(x, y), out var entries))
                foreach (var entry in entries)
                    if (entry.Marker is not null && entry.Marker.NationId == nationId && entry.Selected == selected && entry.Room.Intersects(room))
                        return entry.Marker;
        return null;
    }

    private Vector3 FlatFleetPosition(int id, float latitude, float longitude)
    {
        var anchor = FlatPoint(latitude, longitude, FlatMarkerZ);
        var offset = _flatFleetOffsets.GetValueOrDefault(id);
        return anchor + new Vector3(offset.X, -offset.Y, 0f) * (_camera.Size / Math.Max(1f, Size.Y));
    }

    /// <summary>One occupancy grid across branches. Auxiliary marks reserve their base/port
    /// first; armies then fit around them. Offsets are presentation-only, never save data.</summary>
    private void LayoutFlatAuxiliaryCounters()
    {
        _auxiliaryCounters.Clear();
        foreach (var (id, marker) in _supportMapMarkers)
        {
            if (!marker.Node.IsVisibleInTree()) continue;
            var anchor = FlatPoint(marker.Latitude, marker.Longitude, FlatMarkerZ + .002f);
            _auxiliaryCounters.Add((marker.Node, marker.Plate, anchor, id, marker));
        }
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
            if (at.X >= -80 && at.Y >= -50 && at.X <= Size.X + 80 && at.Y <= Size.Y + 50)
            {
                var gap = Math.Clamp(Ui.Metric("map.flat_counter_gap", 4f), 0f, 16f);
                var size = new Vector2(counter.Plate.Texture.GetWidth(), counter.Plate.Texture.GetHeight()) *
                    counter.Plate.PixelSize * _counterFlatFar + Vector2.One * gap;
                var room = new Rect2(at - size * .5f, size);
                offset = FindFreeCounterOffset(room);
                room.Position += offset;
                TakeCounterRoom(null, room, false);
            }
            if (counter.Support is { } marker) marker.DisplayOffset = offset;
            else _flatFleetOffsets[counter.Id] = offset;
            counter.Node.Position = counter.Anchor + new Vector3(offset.X, -offset.Y, 0f) * (_camera.Size / Math.Max(1f, Size.Y));
        }
    }

    private void TakeCounterRoom(ArmyStackMarker marker, Rect2 room, bool selected)
    {
        if (marker is not null) _placedCounterHosts.Add(marker);
        for (var y = Mathf.FloorToInt(room.Position.Y / 64); y <= Mathf.FloorToInt(room.End.Y / 64); y++)
        for (var x = Mathf.FloorToInt(room.Position.X / 64); x <= Mathf.FloorToInt(room.End.X / 64); x++)
        {
            var cell = new Vector2I(x, y);
            if (!_occupiedCounterCells.TryGetValue(cell, out var entries))
                _occupiedCounterCells[cell] = entries = new();
            entries.Add((marker, room, selected));
        }
    }
}
