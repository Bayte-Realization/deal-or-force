using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>
/// The course of the selected fleet, drawn on the sea.
///
/// A fleet follows the sea lanes now - round the Cape, through Suez, up the Kattegat - and a
/// course that bends round a continent is a course a player wants to see before the fleet
/// is three weeks into it. The army's order arrows answer the same question for a division,
/// so this is their thinner cousin: one pale line from the fleet along every leg still to
/// sail, and a mark where it stops.
/// </summary>
public sealed partial class GlobePresentation
{
    private ImmediateMesh _fleetRouteMesh;
    private MeshInstance3D _fleetRoute;
    private int _fleetRouteFingerprint = int.MinValue;
    private readonly List<Vector3> _fleetRoutePoints = new();

    /// <summary>Draw this fleet's course, or clear it when handed nothing.</summary>
    public void ShowFleetRoute(Simulation.Fleet fleet)
    {
        if (!IsInstanceValid(_routePreview)) return;
        if (_fleetRoute is null || !IsInstanceValid(_fleetRoute))
        {
            if (_routePreview.GetParent() is not Node3D world) return;
            _fleetRouteMesh = new ImmediateMesh();
            _fleetRoute = new MeshInstance3D
            {
                Name = "SelectedFleetRoute",
                Mesh = _fleetRouteMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha
                },
                Visible = false
            };
            world.AddChild(_fleetRoute);
        }

        var drawing = fleet is not null && fleet.HasOrder && fleet.Waypoints.Count >= 2 &&
                      CanShowMilitary(fleet.NationId);
        var fingerprint = drawing
            ? HashCode.Combine(fleet.Id, fleet.Waypoints.Count, (int)(fleet.Latitude * 20f), (int)(fleet.Longitude * 20f),
                _flatMap, _flatMap ? (int)(_flatX * 4f) : 0, fleet.Mission)
            : int.MinValue + 1;
        if (fingerprint == _fleetRouteFingerprint) return;
        _fleetRouteFingerprint = fingerprint;
        _fleetRouteMesh.ClearSurfaces();
        _fleetRoute.Visible = drawing;
        if (!drawing) return;

        // A fleet sails each leg as a straight line in latitude and longitude, so the line is
        // drawn the same way, a degree at a time, to lie on the globe rather than cut under it.
        _fleetRoutePoints.Clear();
        var latitude = fleet.Latitude;
        var longitude = fleet.Longitude;
        _fleetRoutePoints.Add(PresentationPoint(latitude, longitude, SurfaceLineRadius));
        for (var index = 0; index + 1 < fleet.Waypoints.Count; index += 2)
        {
            var toLatitude = fleet.Waypoints[index];
            var toLongitude = fleet.Waypoints[index + 1];
            var deltaLongitude = toLongitude - longitude;
            while (deltaLongitude > 180f) deltaLongitude -= 360f;
            while (deltaLongitude < -180f) deltaLongitude += 360f;
            var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(toLatitude - latitude), MathF.Abs(deltaLongitude))));
            for (var step = 1; step <= steps; step++)
            {
                var t = step / (float)steps;
                _fleetRoutePoints.Add(PresentationPoint(latitude + (toLatitude - latitude) * t,
                    longitude + deltaLongitude * t, SurfaceLineRadius));
            }
            latitude = toLatitude;
            longitude = toLongitude;
        }

        var colour = fleet.Mission == Simulation.FleetMission.Invasion
            ? new Color(0.95f, 0.62f, 0.38f, 0.9f)
            : new Color(0.78f, 0.9f, 1f, 0.85f);
        const float halfWidth = 0.0032f;
        var open = false;
        for (var index = 0; index < _fleetRoutePoints.Count; index++)
        {
            var point = _fleetRoutePoints[index];
            // On the flat map a course over the date line comes out at the other edge; the
            // line breaks there instead of streaking across the whole map.
            if (index > 0 && _flatMap && MathF.Abs(point.X - _fleetRoutePoints[index - 1].X) > FlatMapWidth * 0.5f)
            {
                if (open) _fleetRouteMesh.SurfaceEnd();
                open = false;
            }
            if (!open)
            {
                _fleetRouteMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
                open = true;
            }
            var before = _fleetRoutePoints[Math.Max(0, index - 1)];
            var after = _fleetRoutePoints[Math.Min(_fleetRoutePoints.Count - 1, index + 1)];
            var tangent = (after - before).Normalized();
            if (tangent == Vector3.Zero) tangent = Vector3.Right;
            var normal = _flatMap ? Vector3.Forward : point.Normalized();
            var side = normal.Cross(tangent).Normalized() * halfWidth;
            _fleetRouteMesh.SurfaceSetColor(colour);
            _fleetRouteMesh.SurfaceAddVertex(OnOrderSurface(point + side));
            _fleetRouteMesh.SurfaceSetColor(colour);
            _fleetRouteMesh.SurfaceAddVertex(OnOrderSurface(point - side));
        }
        if (open) _fleetRouteMesh.SurfaceEnd();

        // Where it stops: a small diamond, so the end of a long course is findable.
        var end = _fleetRoutePoints[^1];
        var up = _flatMap ? Vector3.Forward : end.Normalized();
        var across = up.Cross(Vector3.Up).LengthSquared() > 0.0001f ? up.Cross(Vector3.Up).Normalized() : Vector3.Right;
        var along = up.Cross(across).Normalized();
        const float size = 0.009f;
        _fleetRouteMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (a, b) in new[] { (across, along), (along, -across), (-across, -along), (-along, across) })
        {
            _fleetRouteMesh.SurfaceSetColor(colour);
            _fleetRouteMesh.SurfaceAddVertex(OnOrderSurface(end));
            _fleetRouteMesh.SurfaceSetColor(colour);
            _fleetRouteMesh.SurfaceAddVertex(OnOrderSurface(end + a * size));
            _fleetRouteMesh.SurfaceSetColor(colour);
            _fleetRouteMesh.SurfaceAddVertex(OnOrderSurface(end + b * size));
        }
        _fleetRouteMesh.SurfaceEnd();
    }
}
