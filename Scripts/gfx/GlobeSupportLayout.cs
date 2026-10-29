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
        foreach(var army in _armyStacks.Values)
            if(TryCounterScreenRoom(army.Plate,out var room))TakeCounterRoom(room.Grow(2));
        foreach(var fleet in _fleetNodes.Values)
            if(TryCounterScreenRoom(fleet.Plate,out var room))TakeCounterRoom(room.Grow(2));
        _supportLayoutOrder.Clear();
        foreach(var marker in _supportMapMarkers.Values)
            if(marker.Node.IsVisibleInTree())_supportLayoutOrder.Add(marker);
        _supportLayoutOrder.Sort((a,b)=>a.Formation.Id.CompareTo(b.Formation.Id));
        foreach(var marker in _supportLayoutOrder)
        {
            var anchor=_flatMap ? FlatPoint(marker.Latitude,marker.Longitude,FlatMarkerZ-SupportMarkerDepthBias)
                : SurfacePoint(marker.Latitude,marker.Longitude,SurfaceMarkerRadius*SupportSurfaceDepthFactor);
            marker.Node.Position=anchor;
            OrientSurfaceCounter(marker.Node,anchor.Normalized());
            marker.DisplayOffset=Vector2.Zero;
            if(!TryCounterScreenRoom(marker.Plate,out var room))continue;
            var at=_camera.UnprojectPosition(marker.Node.GlobalPosition);
            bool InsideBase(Vector2 offset)=>TryLocate(at+offset,out _,out _,out _,out var province) &&
                province==marker.Formation.BaseProvinceId;
            var found=TryFindFreeCounterOffset(room.Grow(2),out var offset,InsideBase);
            if(found && offset!=Vector2.Zero &&
               TryLocate(at+offset,out var latitude,out var longitude,out _,out _))
            {
                marker.Node.Position=_flatMap ? FlatPoint(latitude,longitude,FlatMarkerZ-SupportMarkerDepthBias)
                    : SurfacePoint(latitude,longitude,SurfaceMarkerRadius*SupportSurfaceDepthFactor);
                if(!_flatMap)OrientSurfaceCounter(marker.Node,marker.Node.Position.Normalized());
                // Radial height can move the projected centre by a fraction of a pixel.
                // Reject even that final displacement if it crossed a very narrow edge.
                if(!TryLocate(_camera.UnprojectPosition(marker.Node.GlobalPosition),out _,out _,out _,out var actual) ||
                   actual!=marker.Formation.BaseProvinceId ||
                   TryCounterScreenRoom(marker.Plate,out var finalRoom)&&CounterRoomOccupied(finalRoom.Grow(2)))
                {marker.Node.Position=anchor;OrientSurfaceCounter(marker.Node,anchor.Normalized());found=false;}
                else marker.DisplayOffset=offset;
            }
            marker.DisplayFaded=!found;
            // Alpha scissoring at 0.5 would erase a 0.45-alpha crowded card rather
            // than fade it. Transparent fallback still obeys the division depth.
            marker.Plate.AlphaCut=found?SpriteBase3D.AlphaCutMode.Discard:SpriteBase3D.AlphaCutMode.Disabled;
            marker.Plate.Modulate=new Color(1,1,1,found?1f:.45f);
            if(TryCounterScreenRoom(marker.Plate,out var placed))TakeCounterRoom(placed.Grow(2));
        }
    }

    private bool TryCounterScreenRoom(Sprite3D plate,out Rect2 room)
    {
        room=default;
        if(!IsInstanceValid(plate)||plate.Texture is null||!plate.IsVisibleInTree()||
           _camera.IsPositionBehind(plate.GlobalPosition))return false;
        var bounds=plate.GetAabb();var min=new Vector2(float.MaxValue,float.MaxValue);
        var max=new Vector2(float.MinValue,float.MinValue);
        for(var corner=0;corner<4;corner++)
        {
            var world=plate.GlobalTransform*new Vector3((corner&1)==0?bounds.Position.X:bounds.End.X,
                (corner&2)==0?bounds.Position.Y:bounds.End.Y,0);
            if(_camera.IsPositionBehind(world))return false;
            var screen=_camera.UnprojectPosition(world);min=min.Min(screen);max=max.Max(screen);
        }
        room=new Rect2(min,max-min);
        return room.Intersects(new Rect2(-80,-50,Size.X+160,Size.Y+100));
    }
}
