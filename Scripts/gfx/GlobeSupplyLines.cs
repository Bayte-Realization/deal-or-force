using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>
/// The supply artery of a division, drawn back to the region that feeds it.
///
/// The logistics pass already tells a player, in the counter's tooltip, whether a
/// formation is fed, thinning or in a cauldron - but a sentence about a road is not a
/// road. A selected division gets a line from its counter back to the hub of the state
/// that supplies it, coloured the way the tooltip reads: fed green, a hub over capacity
/// amber, a pocket red. Cut-off divisions draw their red line even when nobody has
/// selected them, because the cauldron that eats an army is the one nobody clicked on.
///
/// The line is the straight course between the two points, walked a degree at a time so
/// it lies on the globe, in the manner of a fleet's course. It is not the traced corridor
/// the simulation actually walks: that corridor is rebuilt every third day into scratch
/// buffers that each nation overwrites, so the honest cheap thing to draw is the anchor
/// pair the army already carries - where it stands, and the state that feeds it.
/// </summary>
public sealed partial class GlobePresentation
{
    private ImmediateMesh _supplyLineMesh;
    private MeshInstance3D _supplyLinePreview;
    private ShaderMaterial _supplyLineMaterial;
    private int _supplyLineFingerprint = int.MinValue;
    private readonly List<Vector3> _supplyLinePoints = new();
    private readonly List<float> _dateLineXs = new();
    private readonly List<DateLineSeam.Run> _dateLineRuns = new();
    private readonly List<Simulation.Army> _supplyLineArmies = new();
    private readonly Dictionary<int, (float Latitude, float Longitude)> _supplyHubByState = new();

    private const string SupplyLineShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;
void fragment() {
    float flow = fract(UV.x * 1.4 + TIME * 0.85);
    float dash = smoothstep(0.0, 0.10, flow) * (1.0 - smoothstep(0.58, 0.68, flow));
    float across = abs(UV.y * 2.0 - 1.0);
    float core = 1.0 - smoothstep(0.0, 0.62, across);
    float edge = smoothstep(0.62, 0.96, across) * (1.0 - smoothstep(0.96, 1.0, across));
    vec3 col = mix(COLOR.rgb * (0.85 + 0.30 * dash), vec3(0.03, 0.05, 0.07), edge * 0.78);
    ALBEDO = col;
    ALPHA = COLOR.a * max(core * (0.45 + 0.55 * dash), edge * 0.65);
}";

    private void UpdateSupplyLines()
    {
        if (_campaign?.ProvinceMap is null || !IsInstanceValid(_routePreview)) return;
        if (!Simulation.MechanicFeatures.SystemEnabled(_campaign, nameof(Simulation.LogisticsSystem)) ||
            !Simulation.WorldLayers.On("supply_lines"))
        {
            if (IsInstanceValid(_supplyLinePreview)) _supplyLinePreview.Visible = false;
            return;
        }
        if (_supplyLinePreview is null || !IsInstanceValid(_supplyLinePreview))
        {
            if (_routePreview.GetParent() is not Node3D world) return;
            _supplyLineMesh = new ImmediateMesh();
            _supplyLineMaterial = new ShaderMaterial { Shader = new Shader { Code = SupplyLineShader } };
            _supplyLinePreview = new MeshInstance3D
            {
                Name = "ArmySupplyLines",
                Mesh = _supplyLineMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = _supplyLineMaterial,
                Visible = false
            };
            world.AddChild(_supplyLinePreview);
        }

        var player = _campaign.Player;
        _supplyLineArmies.Clear();
        var fingerprint = HashCode.Combine(player?.Id ?? -1, _selectedArmyIds.Count,
            _flatMap, _flatMap ? (int)(_flatX * 4f) : 0);
        foreach (var army in _campaign.Armies)
        {
            if (army.NationId != player?.Id || army.SupplySourceStateMapIndex <= 0) continue;
            var selected = _selectedArmyIds.Contains(army.Id);
            // An unselected division earns its line only by being cut off; a selected one
            // gets it whatever its state, because the player asked about that one.
            if (!selected && !(army.CutOff && army.DaysCutOff > 0)) continue;
            fingerprint = HashCode.Combine(fingerprint, army.Id, army.ProvinceId,
                army.SupplySourceStateMapIndex, army.CutOff, army.DaysCutOff,
                (int)(army.SupplyCongestion * 50f), selected);
            _supplyLineArmies.Add(army);
        }
        if (fingerprint == _supplyLineFingerprint) return;
        _supplyLineFingerprint = fingerprint;
        _supplyLineMesh.ClearSurfaces();
        _supplyLinePreview.Visible = _supplyLineArmies.Count > 0;
        if (_supplyLineArmies.Count == 0) return;

        _supplyHubByState.Clear();
        var drewAny = false;
        foreach (var army in _supplyLineArmies)
            drewAny |= AppendSupplyLine(army);
        _supplyLinePreview.Visible = drewAny;
    }

    private bool AppendSupplyLine(Simulation.Army army)
    {
        var hub = SupplyHubPoint(army.SupplySourceStateMapIndex);
        var colour = army.CutOff
            ? new Color(0.96f, 0.34f, 0.28f, 0.92f)
            : army.SupplyCongestion < 0.999f
                ? new Color(0.96f, 0.73f, 0.34f, 0.85f)
                : new Color(0.52f, 0.82f, 0.58f, 0.72f);

        _supplyLinePoints.Clear();
        _supplyLinePoints.Add(PresentationPoint(army.Latitude, army.Longitude, SurfaceLineRadius));
        var latitude = army.Latitude;
        var longitude = army.Longitude;
        var deltaLongitude = hub.Longitude - longitude;
        while (deltaLongitude > 180f) deltaLongitude -= 360f;
        while (deltaLongitude < -180f) deltaLongitude += 360f;
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(hub.Latitude - latitude),
            MathF.Abs(deltaLongitude))));
        for (var step = 1; step <= steps; step++)
        {
            var t = step / (float)steps;
            _supplyLinePoints.Add(PresentationPoint(latitude + (hub.Latitude - latitude) * t,
                longitude + deltaLongitude * t, SurfaceLineRadius));
        }

        // The ribbon, a strip the width of half a supply line. On the flat map a course
        // over the date line comes out at the other edge; the line breaks there instead
        // of streaking across the whole map, as a fleet's course does. A piece of one
        // point is two vertices, which a triangle strip cannot be, so it is left out.
        const float halfWidth = 0.0028f;
        CollectDateLineRuns(_supplyLinePoints);
        foreach (var run in _dateLineRuns)
        {
            _supplyLineMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
            var cumulative = 0f;
            var endIndex = run.Start + run.Count - 1;
            for (var index = run.Start; index <= endIndex; index++)
            {
                var point = _supplyLinePoints[index];
                if (index > run.Start) cumulative += point.DistanceTo(_supplyLinePoints[index - 1]) * 18f;
                var before = _supplyLinePoints[Math.Max(run.Start, index - 1)];
                var after = _supplyLinePoints[Math.Min(endIndex, index + 1)];
                var tangent = (after - before).Normalized();
                if (tangent == Vector3.Zero) tangent = Vector3.Right;
                var normal = _flatMap ? Vector3.Forward : point.Normalized();
                var side = normal.Cross(tangent).Normalized() * halfWidth;
                _supplyLineMesh.SurfaceSetColor(colour);
                _supplyLineMesh.SurfaceSetUV(new Vector2(cumulative, 0f));
                _supplyLineMesh.SurfaceAddVertex(OnOrderSurface(point + side));
                _supplyLineMesh.SurfaceSetColor(colour);
                _supplyLineMesh.SurfaceSetUV(new Vector2(cumulative, 1f));
                _supplyLineMesh.SurfaceAddVertex(OnOrderSurface(point - side));
            }
            _supplyLineMesh.SurfaceEnd();
        }

        // The hub end gets a hollow square, so the far end of a long artery is findable
        // and reads as a place rather than the line simply stopping.
        var end = _supplyLinePoints[^1];
        var up = _flatMap ? Vector3.Forward : end.Normalized();
        var across = up.Cross(Vector3.Up).LengthSquared() > 0.0001f ? up.Cross(Vector3.Up).Normalized() : Vector3.Right;
        var along = up.Cross(across).Normalized();
        const float size = 0.0072f;
        _supplyLineMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        foreach (var (a, b) in new[]
                 {
                     (across, along), (along, -across), (-across, -along), (-along, across), (across, along)
                 })
        {
            _supplyLineMesh.SurfaceSetColor(colour);
            _supplyLineMesh.SurfaceAddVertex(OnOrderSurface(end + a * size));
            _supplyLineMesh.SurfaceSetColor(colour);
            _supplyLineMesh.SurfaceAddVertex(OnOrderSurface(end + b * size));
        }
        _supplyLineMesh.SurfaceEnd();
        return true;
    }

    /// <summary>
    /// Where a supplying state's artery is anchored: the province nearest the state's
    /// centre, so the line ends inside the region a player would point at on the map.
    /// Averages longitude relative to the first province so a state straddling the date
    /// line does not centre itself on the far side of the world.
    /// </summary>
    private (float Latitude, float Longitude) SupplyHubPoint(int stateMapIndex)
    {
        if (_supplyHubByState.TryGetValue(stateMapIndex, out var cached)) return cached;
        var topology = _campaign.ProvinceMap;
        var first = true;
        float refLongitude = 0f;
        double sumLatitude = 0, sumLongitude = 0, count = 0;
        int nearest = 0;
        var nearestDistance = float.MaxValue;
        float centreLatitude = 0f, centreLongitude = 0f;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != stateMapIndex) continue;
            var centre = topology.CenterOf(province);
            if (first)
            {
                refLongitude = centre.Longitude;
                first = false;
            }
            var delta = centre.Longitude - refLongitude;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            sumLatitude += centre.Latitude;
            sumLongitude += delta;
            count++;
        }
        if (count > 0)
        {
            centreLatitude = (float)(sumLatitude / count);
            centreLongitude = refLongitude + (float)(sumLongitude / count);
            while (centreLongitude > 180f) centreLongitude -= 360f;
            while (centreLongitude < -180f) centreLongitude += 360f;
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                if (topology.ProvinceToState[province] != stateMapIndex) continue;
                var centre = topology.CenterOf(province);
                var delta = centre.Longitude - centreLongitude;
                while (delta > 180f) delta -= 360f;
                while (delta < -180f) delta += 360f;
                var distance = (centre.Latitude - centreLatitude) * (centre.Latitude - centreLatitude) +
                               delta * delta;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = province;
            }
        }
        var hub = nearest > 0 ? topology.CenterOf(nearest) : (0f, 0f);
        _supplyHubByState[stateMapIndex] = hub;
        return hub;
    }

    /// <summary>
    /// Runs of a projected line that stay on one side of the flat sheet. On the
    /// globe the points already take the short arc, so the whole line is one run.
    /// </summary>
    private void CollectDateLineRuns(IReadOnlyList<Vector3> points)
    {
        if (!_flatMap)
        {
            _dateLineRuns.Clear();
            if (points.Count >= 2) _dateLineRuns.Add(new DateLineSeam.Run(0, points.Count));
            return;
        }
        _dateLineXs.Clear();
        for (var i = 0; i < points.Count; i++) _dateLineXs.Add(points[i].X);
        DateLineSeam.Split(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_dateLineXs), FlatMapWidth, _dateLineRuns);
    }
}
