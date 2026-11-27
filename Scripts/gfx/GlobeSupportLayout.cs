using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly List<SupportMapMarker> _supportLayoutOrder = new();

    /// <summary>Actual base wins over free pixels. Divisions retain priority when a
    /// province is too small for separate cards; zooming in reveals more local room.</summary>
    private void LayoutSupportCounters()
    {
        if (_supportMarkerRoot is null || !_supportMarkerRoot.IsVisibleInTree() || _camera is null) return;
        ClearCounterRooms();
        foreach (var army in _armyStacks.Values) if (TryCounterScreenRoom(army.Plate, out var room)) TakeCounterRoom(room.Grow(2));
        foreach (var fleet in _fleetNodes.Values) if (TryCounterScreenRoom(fleet.Plate, out var room)) TakeCounterRoom(room.Grow(2));
        _supportLayoutOrder.Clear();
        foreach (var marker in _supportMapMarkers.Values) if (marker.Node.IsVisibleInTree()) _supportLayoutOrder.Add(marker);
        _supportLayoutOrder.Sort((a, b) => a.Formation.Id.CompareTo(b.Formation.Id));
        foreach (var marker in _supportLayoutOrder) LayoutSingleSupportMarker(marker);
    }

    private void LayoutSingleSupportMarker(SupportMapMarker marker)
    {
        var anchor = SupportPoint(marker.Latitude, marker.Longitude);
        marker.Node.Position = anchor;
        OrientSurfaceCounter(marker.Node, anchor.Normalized());
        marker.DisplayOffset = Vector2.Zero;
        if (!TryCounterScreenRoom(marker.Plate, out var room)) return;
        if (marker.Formation.Route.Count > 0 && marker.Formation.MoveProgress > 0f)
        { ApplySupportFade(marker, anchor, found: !CounterRoomOccupied(room.Grow(2)), offset: Vector2.Zero, takeRoom: room); return; }
        var found = TryOffsetSupportMarker(marker, anchor, Simulation.LongRangeMovement.At(marker.Formation), room, out var offset);
        ApplySupportFade(marker, anchor, found, offset, takeRoom: TryCounterScreenRoom(marker.Plate, out var placed) ? placed : (Rect2?)null);
    }

    private Vector3 SupportPoint(float lat, float lon) => _flatMap
        ? LocalFlatPoint(lat, lon, FlatMarkerZ - SupportMarkerDepthBias)
        : LocalSurfacePoint(lat, lon, SurfaceMarkerRadius * SupportSurfaceDepthFactor);

    private bool TryOffsetSupportMarker(SupportMapMarker m, Vector3 anchor, int standing, Rect2 room, out Vector2 offset)
    {
        var at = _camera.UnprojectPosition(m.Node.GlobalPosition);
        bool InsideBase(Vector2 off) => TryLocate(at + off, out _, out _, out _, out var prov) && prov == standing;
        var found = KeepOrFindSupportOffset(m, room.Grow(2), InsideBase, out offset);
        if (!found || offset == Vector2.Zero || !TryLocate(at + offset, out var lat, out var lon, out _, out _)) return found;
        m.Node.Position = SupportPoint(lat, lon);
        if (!_flatMap) OrientSurfaceCounter(m.Node, m.Node.Position.Normalized());
        if (TryLocate(_camera.UnprojectPosition(m.Node.GlobalPosition), out _, out _, out _, out var actual) && actual == standing &&
            (!TryCounterScreenRoom(m.Plate, out var r) || !CounterRoomOccupied(r.Grow(2)))) return true;
        m.Node.Position = anchor;
        OrientSurfaceCounter(m.Node, anchor.Normalized());
        offset = Vector2.Zero;
        return false;
    }

    private void ApplySupportFade(SupportMapMarker m, Vector3 anchor, bool found, Vector2 offset, Rect2? takeRoom)
    {
        m.DisplayOffset = offset;
        m.LayoutPosition = m.Node.Position;
        m.HasLayoutPosition = found && !_flatMap && offset != Vector2.Zero;
        m.DisplayFaded = !found;
        // Keep AlphaCut=Discard permanently; GLES3 mobile flickers when flipping AlphaCut modes.
        var mod = found ? Colors.White : new Color(.72f,.75f,.78f,1f);
        if (m.Plate.Modulate != mod) m.Plate.Modulate = mod;
        if (IsInstanceValid(m.Readiness)) m.Readiness.Visible = SupportReadinessVisible(m);
        if (takeRoom is { } r) TakeCounterRoom(r.Grow(2));
    }

    private bool KeepOrFindSupportOffset(SupportMapMarker m,Rect2 grown,Func<Vector2,bool> ok,out Vector2 off)
    {
        off=m.DisplayOffset;
        if(off!=Vector2.Zero && !CounterRoomOccupied(new Rect2(grown.Position+off,grown.Size)) && ok(off))return true;
        return TryFindFreeCounterOffset(grown,out off,ok);
    }

    private bool TryCounterScreenRoom(Sprite3D plate,out Rect2 room)
    {
        room=default;
        if(!IsInstanceValid(plate)||plate.Texture is null||!plate.IsVisibleInTree()||_camera.IsPositionBehind(plate.GlobalPosition))return false;
        var bounds=plate.GetAabb();var min=new Vector2(float.MaxValue,float.MaxValue);var max=new Vector2(float.MinValue,float.MinValue);
        for(var c=0;c<4;c++){var w=plate.GlobalTransform*new Vector3((c&1)==0?bounds.Position.X:bounds.End.X,(c&2)==0?bounds.Position.Y:bounds.End.Y,0);if(_camera.IsPositionBehind(w))return false;var s=_camera.UnprojectPosition(w);min=min.Min(s);max=max.Max(s);}
        room=new Rect2(min,max-min);return room.Intersects(new Rect2(-80,-50,Size.X+160,Size.Y+100));
    }
}
