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
    private ArrayMesh _fleetRouteMesh;
    private MeshInstance3D _fleetRoute;
    private int _fleetRouteFingerprint = int.MinValue;
    private readonly List<Vector3> _fleetRoutePoints = new();
    private readonly List<Vector3> _fleetRouteVerts = new();
    private readonly List<Color> _fleetRouteColors = new();
    private readonly List<int> _fleetRouteIndices = new();
    private readonly Godot.Collections.Array _fleetRouteArrays = new();

    /// <summary>Draw this fleet's course, or clear it when handed nothing.</summary>
    public void ShowFleetRoute(Simulation.Fleet fleet)
    {
        if (!IsInstanceValid(_routePreview)) return;
        if (_fleetRoute is null || !IsInstanceValid(_fleetRoute))
        {
            if (_routePreview.GetParent() is not Node3D world) return;
            _fleetRouteMesh = new ArrayMesh();
            _fleetRouteArrays.Resize((int)Mesh.ArrayType.Max);
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
        // On the flat map a course over the date line comes out at the other edge; the
        // line breaks there instead of streaking across the whole map. Build every run plus
        // destination diamond into one static cached ArrayMesh surface.
        const float halfWidth = 0.0032f;
        _fleetRouteVerts.Clear();
        _fleetRouteColors.Clear();
        _fleetRouteIndices.Clear();
        CollectDateLineRuns(_fleetRoutePoints);
        foreach (var run in _dateLineRuns)
        {
            var baseVert = _fleetRouteVerts.Count;
            var endIndex = run.Start + run.Count - 1;
            for (var index = run.Start; index <= endIndex; index++)
            {
                var point = _fleetRoutePoints[index];
                var before = _fleetRoutePoints[Math.Max(run.Start, index - 1)];
                var after = _fleetRoutePoints[Math.Min(endIndex, index + 1)];
                var tangent = (after - before).Normalized();
                if (tangent == Vector3.Zero) tangent = Vector3.Right;
                var normal = _flatMap ? Vector3.Forward : point.Normalized();
                var side = normal.Cross(tangent).Normalized() * halfWidth;
                _fleetRouteVerts.Add(OnOrderSurface(point + side));
                _fleetRouteColors.Add(colour);
                _fleetRouteVerts.Add(OnOrderSurface(point - side));
                _fleetRouteColors.Add(colour);
                if (index > run.Start)
                {
                    var prev = baseVert + (index - run.Start - 1) * 2;
                    var curr = prev + 2;
                    _fleetRouteIndices.Add(prev);
                    _fleetRouteIndices.Add(prev + 1);
                    _fleetRouteIndices.Add(curr);
                    _fleetRouteIndices.Add(prev + 1);
                    _fleetRouteIndices.Add(curr + 1);
                    _fleetRouteIndices.Add(curr);
                }
            }
        }

        // Where it stops: a small diamond, so the end of a long course is findable.
        var end = _fleetRoutePoints[^1];
        var up = _flatMap ? Vector3.Forward : end.Normalized();
        var across = up.Cross(Vector3.Up).LengthSquared() > 0.0001f ? up.Cross(Vector3.Up).Normalized() : Vector3.Right;
        var along = up.Cross(across).Normalized();
        const float size = 0.009f;
        var centerIdx = _fleetRouteVerts.Count;
        var center = OnOrderSurface(end);
        _fleetRouteVerts.Add(center); _fleetRouteColors.Add(colour);
        _fleetRouteVerts.Add(OnOrderSurface(end + across * size)); _fleetRouteColors.Add(colour);
        _fleetRouteVerts.Add(OnOrderSurface(end + along * size)); _fleetRouteColors.Add(colour);
        _fleetRouteVerts.Add(OnOrderSurface(end - across * size)); _fleetRouteColors.Add(colour);
        _fleetRouteVerts.Add(OnOrderSurface(end - along * size)); _fleetRouteColors.Add(colour);
        for (var q = 0; q < 4; q++)
        {
            _fleetRouteIndices.Add(centerIdx);
            _fleetRouteIndices.Add(centerIdx + 1 + q);
            _fleetRouteIndices.Add(centerIdx + 1 + ((q + 1) & 3));
        }

        if (_fleetRouteIndices.Count > 0)
        {
            _fleetRouteArrays[(int)Mesh.ArrayType.Vertex] = _fleetRouteVerts.ToArray();
            _fleetRouteArrays[(int)Mesh.ArrayType.Color] = _fleetRouteColors.ToArray();
            _fleetRouteArrays[(int)Mesh.ArrayType.Index] = _fleetRouteIndices.ToArray();
            _fleetRouteMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _fleetRouteArrays);
            _fleetRouteArrays[(int)Mesh.ArrayType.Vertex] = default;
            _fleetRouteArrays[(int)Mesh.ArrayType.Color] = default;
            _fleetRouteArrays[(int)Mesh.ArrayType.Index] = default;
        }
    }
}
