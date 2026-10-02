using System;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private ulong _nextSurfaceDensityMs;
    private readonly System.Collections.Generic.Dictionary<ArmyStackMarker, Vector2> _retainedSurfaceSlots = new();

    // Presentation-only aggregation. Never move divisions, mix nations, or let an
    // off-screen formation disappear into a visible counter on the opposite horizon.
    private void UpdateSurfaceCounterDensity()
    {
        if (_flatMap || !IsInstanceValid(_armyRoot)) return;
        var now = Time.GetTicksMsec();
        if (!_counterDensityDirty && now < _nextSurfaceDensityMs) return;
        _nextSurfaceDensityMs = now + 100;
        _counterDensityDirty = false;
        _occupiedCounterCells.Clear();
        _placedCounterHosts.Clear();
        _densityCandidates.Clear();
        _retainedSurfaceSlots.Clear();
        var aggregate = !_surfaceIndividualDivisions && ViewDistance > Math.Clamp(Ui.Metric("map.globe_cluster_distance", 9.5f), 5.5f, 12f);
        var angularLimit = Math.Clamp(Ui.Metric("map.globe_cluster_radius_km", 140f), 30f, 300f) / 6371f;
        var chordSquared = 2f - 2f * MathF.Cos(angularLimit);
        foreach (var marker in _armyStacks.Values)
        {
            marker.DisplayStack.Clear();
            marker.DisplayStack.AddRange(marker.Stack);
            marker.DisplayProvinces = 1;
            marker.Plate.Visible = true;
            marker.Count.Visible = true;
            if (!marker.Node.IsVisibleInTree() || marker.Stack.Count == 0 ||
                _camera.IsPositionBehind(marker.Node.GlobalPosition)) continue;
            var selected = false;
            foreach (var army in marker.Stack)
                if (_selectedArmyIds.Contains(army.Id)) { selected = true; break; }
            var screen = _camera.UnprojectPosition(marker.Node.GlobalPosition);
            if (!new Rect2(-80, -50, Size.X + 160, Size.Y + 100).HasPoint(screen)) continue;
            _densityCandidates.Add((marker, screen, selected));
        }
        _densityCandidates.Sort((a, b) => a.Marker.Stack[0].Id.CompareTo(b.Marker.Stack[0].Id));
        foreach (var (marker, _, selected) in _densityCandidates)
        {
            var retained = marker.SurfaceOffset;
            _retainedSurfaceSlots[marker] = retained;
            PlaceSurfaceArmyCounter(marker, Vector2.Zero);
            var room = SurfaceCounterScreenBounds(marker.Plate).Grow(1f);
            ArmyStackMarker host = null;
            if (aggregate)
                for (var y = Mathf.FloorToInt(room.Position.Y / 64); y <= Mathf.FloorToInt(room.End.Y / 64) && host is null; y++)
                for (var x = Mathf.FloorToInt(room.Position.X / 64); x <= Mathf.FloorToInt(room.End.X / 64) && host is null; x++)
                    if (_occupiedCounterCells.TryGetValue(new Vector2I(x, y), out var entries))
                        foreach (var entry in entries)
                        {
                            var candidate = entry.Marker;
                            if (candidate is null || candidate.NationId != marker.NationId || entry.Selected != selected ||
                                candidate.Normal.DistanceSquaredTo(marker.Normal) > chordSquared || !entry.Room.Intersects(room)) continue;
                            host = candidate;
                            break;
                        }
            if (host is not null)
            {
                host.DisplayStack.AddRange(marker.Stack);
                host.DisplayProvinces++;
                marker.DisplayStack.Clear();
                marker.Plate.Visible = marker.Count.Visible = false;
            }
            else
            {
                // Group against geographic anchors, not last frame's displaced
                // cards; otherwise a free slot changes membership on every pass.
                TakeCounterRoom(marker, room, selected);
            }
        }
        _occupiedCounterCells.Clear();
        _placedCounterHosts.Clear();
        // Reserve actual auxiliary footprints only after geographic aggregation.
        foreach (var marker in _supportMapMarkers.Values)
            if (marker.Plate.IsVisibleInTree() && !_camera.IsPositionBehind(marker.Node.GlobalPosition))
                TakeCounterRoom(null, SurfaceCounterScreenBounds(marker.Plate).Grow(1f), false);
        foreach (var parts in _fleetNodes.Values)
            if (parts.Plate.IsVisibleInTree() && !_camera.IsPositionBehind(parts.Node.GlobalPosition))
                TakeCounterRoom(null, SurfaceCounterScreenBounds(parts.Plate).Grow(1f), false);
        foreach (var (marker, _, selected) in _densityCandidates)
        {
            if (marker.DisplayStack.Count == 0) continue;
            PlaceSurfaceCounterLocally(marker, _retainedSurfaceSlots[marker]);
            TakeCounterRoom(marker, SurfaceCounterScreenBounds(marker.Plate).Grow(1f), selected);
        }
        foreach (var (marker, _, _) in _densityCandidates)
        {
            var ids = new Godot.Collections.Array<int>();
            var fingerprint = new HashCode();
            var divisions = 0f;
            var personnel = 0f;
            foreach (var army in marker.DisplayStack)
            {
                ids.Add(army.Id);
                fingerprint.Add(army.Id);
                divisions += army.Divisions;
                personnel += DivisionPersonnel(army);
            }
            var signature = fingerprint.ToHashCode();
            if (signature != marker.DisplayFingerprint)
            {
                marker.DisplayFingerprint = signature;
                marker.Node.SetMeta("display_army_ids", ids);
            }
            marker.Node.SetMeta("display_provinces", marker.DisplayProvinces);
            UpdateArmyConditionBars(marker);
            if (!marker.Plate.Visible) continue;
            var text = $"{Mathf.CeilToInt(divisions)}{(marker.DisplayProvinces > 1 ? "+" : "")}\n{personnel / 1000f:0.#}k";
            if (marker.Count.Text != text) marker.Count.Text = text;
        }
    }

    private void PlaceSurfaceArmyCounter(ArmyStackMarker marker, Vector2 offset)
    {
        marker.SurfaceOffset = offset;
        marker.Node.Position = ArmyMarkerPosition(marker);
        OrientSurfaceArmyCard(marker);
        SizeSurfaceArmyCounter(marker);
    }

    private void PlaceSurfaceCounterLocally(ArmyStackMarker marker, Vector2 retained)
    {
        var limit = Math.Clamp(Ui.Metric("map.globe_counter_local_radius", .025f), 0f, .1f);
        bool Fits(Vector2 offset)
        {
            if (offset.LengthSquared() > limit * limit) return false;
            PlaceSurfaceArmyCounter(marker, offset);
            var topology = _campaign?.ProvinceMap;
            var province = marker.Stack[0].ProvinceId;
            if (offset != Vector2.Zero && topology is not null && province > 0)
            {
                var geo = GeographicFromNormal(marker.Node.Position.Normalized());
                var x = Math.Clamp((int)((geo.Longitude + 180f) / 360f * topology.Width), 0, topology.Width - 1);
                var y = Math.Clamp((int)((90f - geo.Latitude) / 180f * topology.Height), 0, topology.Height - 1);
                if (topology.CellToProvince[y * topology.Width + x] != province) return false;
            }
            var horizon = GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length()) + .002f;
            return marker.Node.Position.Normalized().Dot(_camera.GlobalPosition.Normalized()) > horizon &&
                   !CounterRoomOccupied(SurfaceCounterScreenBounds(marker.Plate).Grow(1f));
        }
        // Retain an existing free surface slot when the camera moves: no per-frame
        // repulsion or screen-space spiral that sends armies across unrelated countries.
        if (Fits(retained) || Fits(Vector2.Zero)) return;
        var step = Math.Min(limit, Math.Max(.012f, marker.Node.Scale.X * 1.08f));
        var height = step * marker.Plate.Texture.GetHeight() / marker.Plate.Texture.GetWidth();
        for (var ring = 1; ring <= 3; ring++)
        {
            if (Fits(new Vector2(0, ring * height)) || Fits(new Vector2(0, -ring * height)) ||
                Fits(new Vector2(ring * step, 0)) || Fits(new Vector2(-ring * step, 0))) return;
            for (var x = -ring; x <= ring; x++)
            for (var y = -ring; y <= ring; y++)
                if ((Math.Abs(x) == ring || Math.Abs(y) == ring) && Fits(new Vector2(x * step, y * height))) return;
        }
        // At a world overview some opposing counters cannot fit inside this local
        // radius. Keep them present at their anchor, never silently hide a hostile army.
        PlaceSurfaceArmyCounter(marker, Vector2.Zero);
    }

    private Rect2 SurfaceCounterScreenBounds(Sprite3D plate)
    {
        var half = plate.Texture.GetSize() * plate.PixelSize * .5f;
        var first = _camera.UnprojectPosition(plate.ToGlobal(new Vector3(-half.X, -half.Y, 0)));
        var room = new Rect2(first, Vector2.Zero);
        room = room.Expand(_camera.UnprojectPosition(plate.ToGlobal(new Vector3(half.X, -half.Y, 0))));
        room = room.Expand(_camera.UnprojectPosition(plate.ToGlobal(new Vector3(-half.X, half.Y, 0))));
        return room.Expand(_camera.UnprojectPosition(plate.ToGlobal(new Vector3(half.X, half.Y, 0))));
    }
}
