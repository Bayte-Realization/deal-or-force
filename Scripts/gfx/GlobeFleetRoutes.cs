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
                      GlobeLineSanity.IsValidGeo(fleet.Latitude, fleet.Longitude) &&
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
        if (!TryPresentationPoint(latitude, longitude, SurfaceLineRadius, out var startPt)) return;
        _fleetRoutePoints.Add(startPt);
        for (var index = 0; index + 1 < fleet.Waypoints.Count; index += 2)
        {
            var toLatitude = fleet.Waypoints[index];
            var toLongitude = fleet.Waypoints[index + 1];
            if (!GlobeLineSanity.IsValidGeo(toLatitude, toLongitude)) continue;
            var deltaLongitude = GlobeLineSanity.WrapDeltaDegrees(toLongitude - longitude);
            var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(toLatitude - latitude), MathF.Abs(deltaLongitude))));
            for (var step = 1; step <= steps; step++)
            {
                var t = step / (float)steps;
                if (TryPresentationPoint(latitude + (toLatitude - latitude) * t,
                        longitude + deltaLongitude * t, SurfaceLineRadius, out var pt))
                    _fleetRoutePoints.Add(pt);
            }
            latitude = toLatitude;
            longitude = toLongitude;
        }
        if (_fleetRoutePoints.Count < 2)
        {
            _fleetRoute.Visible = false;
            return;
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
        CollectValidLineRuns(_fleetRoutePoints,
            _flatMap ? GlobeLineSanity.MaxFlatSegmentLength : GlobeLineSanity.MaxGlobeSegmentLength,
            _dateLineRuns);
        foreach (var run in _dateLineRuns)
        {
            var prevPair = -1;
            var endIndex = run.Start + run.Count - 1;
            for (var index = run.Start; index <= endIndex; index++)
            {
                var point = _fleetRoutePoints[index];
                var before = _fleetRoutePoints[Math.Max(run.Start, index - 1)];
                var after = _fleetRoutePoints[Math.Min(endIndex, index + 1)];
                if (!GlobeLineSanity.TrySafeRibbonFrame(ToNum(point), ToNum(before), ToNum(after),
                        _flatMap, flatPositiveZNormal: false, out _, out _, out var sideAxis))
                {
                    prevPair = -1;
                    continue;
                }
                var side = ToGd(sideAxis) * halfWidth;
                if (!TryOnOrderSurface(point + side, SurfaceLineRadius, FlatLineZ, out var left) ||
                    !TryOnOrderSurface(point - side, SurfaceLineRadius, FlatLineZ, out var right) ||
                    !GlobeLineSanity.IsValidEmittedEdge(ToNum(left), ToNum(right), _flatMap, _flatX))
                {
                    prevPair = -1;
                    continue;
                }
                if (prevPair >= 0 &&
                    (!GlobeLineSanity.IsValidEmittedEdge(ToNum(_fleetRouteVerts[prevPair]), ToNum(left), _flatMap, _flatX) ||
                     !GlobeLineSanity.IsValidEmittedEdge(ToNum(_fleetRouteVerts[prevPair + 1]), ToNum(right), _flatMap, _flatX)))
                {
                    prevPair = -1;
                }
                var curr = _fleetRouteVerts.Count;
                _fleetRouteVerts.Add(left);
                _fleetRouteColors.Add(colour);
                _fleetRouteVerts.Add(right);
                _fleetRouteColors.Add(colour);
                if (prevPair >= 0)
                {
                    _fleetRouteIndices.Add(prevPair);
                    _fleetRouteIndices.Add(prevPair + 1);
                    _fleetRouteIndices.Add(curr);
                    _fleetRouteIndices.Add(prevPair + 1);
                    _fleetRouteIndices.Add(curr + 1);
                    _fleetRouteIndices.Add(curr);
                }
                prevPair = curr;
            }
        }

        // Where it stops: a small diamond, so the end of a long course is findable.
        var end = _fleetRoutePoints[^1];
        if (GlobeLineSanity.IsValidEmittedVertex(ToNum(end), _flatMap, _flatX))
        {
            var up = _flatMap ? Vector3.Forward : end.Normalized();
            var across = GlobeLineSanity.TrySafeCrossAxis(ToNum(up), out var acrossNum) ? ToGd(acrossNum) : Vector3.Right;
            var along = GlobeLineSanity.TrySafeNormalize(ToNum(up.Cross(across)), out var alongNum) ? ToGd(alongNum) : Vector3.Up;
            const float size = 0.009f;
            if (TryOnOrderSurface(end, SurfaceLineRadius, FlatLineZ, out var center) &&
                TryOnOrderSurface(end + across * size, SurfaceLineRadius, FlatLineZ, out var pE) &&
                TryOnOrderSurface(end + along * size, SurfaceLineRadius, FlatLineZ, out var pN) &&
                TryOnOrderSurface(end - across * size, SurfaceLineRadius, FlatLineZ, out var pW) &&
                TryOnOrderSurface(end - along * size, SurfaceLineRadius, FlatLineZ, out var pS) &&
                GlobeLineSanity.IsValidEmittedEdge(ToNum(center), ToNum(pE), _flatMap, _flatX) &&
                GlobeLineSanity.IsValidEmittedEdge(ToNum(center), ToNum(pN), _flatMap, _flatX) &&
                GlobeLineSanity.IsValidEmittedEdge(ToNum(center), ToNum(pW), _flatMap, _flatX) &&
                GlobeLineSanity.IsValidEmittedEdge(ToNum(center), ToNum(pS), _flatMap, _flatX))
            {
                var centerIdx = _fleetRouteVerts.Count;
                _fleetRouteVerts.Add(center); _fleetRouteColors.Add(colour);
                _fleetRouteVerts.Add(pE); _fleetRouteColors.Add(colour);
                _fleetRouteVerts.Add(pN); _fleetRouteColors.Add(colour);
                _fleetRouteVerts.Add(pW); _fleetRouteColors.Add(colour);
                _fleetRouteVerts.Add(pS); _fleetRouteColors.Add(colour);
                for (var q = 0; q < 4; q++)
                {
                    _fleetRouteIndices.Add(centerIdx);
                    _fleetRouteIndices.Add(centerIdx + 1 + q);
                    _fleetRouteIndices.Add(centerIdx + 1 + ((q + 1) & 3));
                }
            }
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
        else
        {
            _fleetRoute.Visible = false;
        }
    }
}
