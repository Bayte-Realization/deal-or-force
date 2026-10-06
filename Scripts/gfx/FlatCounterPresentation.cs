using System;
using System.Collections.Generic;
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

    // Geography is authoritative. Collision avoidance must never move a division
    // counter away from its province or merge it with a neighbouring province.
    private Vector3 ArmyMarkerPosition(ArmyStackMarker marker) => ArmyPresentationPoint(marker.Stack);

    private void DrawFlatCounterLeaders()
    {
        if (!_flatMap || !MilitaryMarkersEnabled || !IsInstanceValid(_armyRoot)) return;
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

    /// <summary>Only auxiliary counters search for free space. Army counters remain
    /// at their geographic anchor in every zoom and selection state.</summary>
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
        _occupiedCounterCells.Clear();
        foreach (var marker in _armyStacks.Values)
        {
            marker.Node.Position = ArmyMarkerPosition(marker);
            RefreshProvinceCounterDisplay(marker, _flatStrategicCounters);
            // Auxiliary base/port cards may fit around armies, never the reverse.
            if (!marker.Node.IsVisibleInTree() || _camera.IsPositionBehind(marker.Node.GlobalPosition)) continue;
            var at = _camera.UnprojectPosition(marker.Node.GlobalPosition);
            if (!new Rect2(-80, -50, Size.X + 160, Size.Y + 100).HasPoint(at)) continue;
            var half = marker.Plate.Texture.GetSize() * marker.Plate.PixelSize * _counterFlatFar * .5f;
            TakeCounterRoom(new Rect2(at - half, half * 2f));
        }
        LayoutFlatAuxiliaryCounters();
        QueueRedraw();
    }

    private void RefreshProvinceCounterDisplay(ArmyStackMarker marker, bool compact)
    {
        marker.DisplayStack.Clear();
        marker.DisplayStack.AddRange(marker.Stack);
        marker.DisplayProvinces = 1;
        marker.Compact = compact;
        marker.Plate.Visible = true;
        marker.Plate.Scale = Vector3.One;
        marker.Count.Visible = !compact;
        var divisions = 0f;
        var personnel = 0f;
        var fingerprint = new HashCode();
        foreach (var army in marker.DisplayStack)
        {
            divisions += army.Divisions;
            personnel += DivisionPersonnel(army);
            fingerprint.Add(army.Id);
        }
        var text = $"{Mathf.CeilToInt(divisions)}" + (compact ? "" : $"\n{personnel / 1000f:0.#}k");
        if (marker.Count.Text != text) marker.Count.Text = text;
        var signature = fingerprint.ToHashCode();
        if (signature != marker.DisplayFingerprint)
        {
            marker.DisplayFingerprint = signature;
            var ids = new Godot.Collections.Array<int>();
            foreach (var army in marker.DisplayStack) ids.Add(army.Id);
            marker.Node.SetMeta("display_army_ids", ids);
        }
        marker.Node.SetMeta("display_provinces", 1);
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

    private bool TryFindFreeCounterOffset(Rect2 room, out Vector2 result)
    {
        result = Vector2.Zero;
        if (!CounterRoomOccupied(room)) return true;
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
                TakeCounterRoom(room);
            }
            if (counter.Support is { } marker) marker.DisplayOffset = offset;
            else _flatFleetOffsets[counter.Id] = offset;
            counter.Node.Position = counter.Anchor + new Vector3(offset.X, -offset.Y, 0f) * (_camera.Size / Math.Max(1f, Size.Y));
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
}
