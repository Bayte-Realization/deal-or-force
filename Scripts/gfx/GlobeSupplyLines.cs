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
    private ArrayMesh _supplyLineMesh;
    private MeshInstance3D _supplyLinePreview;
    private ShaderMaterial _supplyLineMaterial;
    private int _supplyLineFingerprint = int.MinValue;
    private readonly List<Vector3> _supplyLinePoints = new();
    private readonly List<DateLineSeam.Run> _dateLineRuns = new();
    private readonly List<Simulation.Army> _supplyLineArmies = new();
    private readonly Dictionary<int, (float Latitude, float Longitude)> _supplyHubByState = new();
    private readonly List<Vector3> _supplyRibbonVerts = new();
    private readonly List<Vector2> _supplyRibbonUvs = new();
    private readonly List<Color> _supplyRibbonColors = new();
    private readonly List<int> _supplyRibbonIndices = new();
    private readonly List<Vector3> _supplyMarkerVerts = new();
    private readonly List<Vector2> _supplyMarkerUvs = new();
    private readonly List<Color> _supplyMarkerColors = new();
    private readonly Godot.Collections.Array _supplySurfaceArrays = new();

    private const string SupplyLineShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;
uniform float quality_tier = 0.0;
void fragment() {
    float across = abs(UV.y * 2.0 - 1.0);
    float speed = quality_tier < 1.5 ? 0.95 : 0.45;
    // Directional cartographic ink chevron pointing from supply hub toward the division.
    float chevron_u = UV.x * 1.45 + (quality_tier < 1.5 ? across * 0.32 : 0.0) + TIME * speed;
    float flow = fract(chevron_u);
    float dash = smoothstep(0.0, 0.10, flow) * (1.0 - smoothstep(0.56, 0.68, flow));
    float core = 1.0 - smoothstep(0.0, 0.64, across);
    float edge = smoothstep(0.64, 0.96, across) * (1.0 - smoothstep(0.96, 1.0, across));
    ALBEDO = mix(COLOR.rgb * 0.86, vec3(0.05, 0.06, 0.07), edge * 0.78);
    ALPHA = COLOR.a * 0.68 * max(core * dash, edge * 0.55);
}";

    private void ApplySupplyLineQualityTier(float tier) =>
        _supplyLineMaterial?.SetShaderParameter("quality_tier", tier);

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
            _supplyLineMesh = new ArrayMesh();
            _supplySurfaceArrays.Resize((int)Mesh.ArrayType.Max);
            _supplyLineMaterial = new ShaderMaterial { Shader = new Shader { Code = SupplyLineShader } };
            _supplyLineMaterial.SetShaderParameter("quality_tier", CurrentShaderQualityTier());
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
        var zoomBucket = CurrentBattleArrowZoomBucket();
        var fingerprint = HashCode.Combine(player?.Id ?? -1, _selectedArmyIds.Count,
            _flatMap, _flatMap ? (int)(_flatX * 4f) : 0, _mapMode, zoomBucket);
        var economyMode = _mapMode == StrategicMapMode.Economy;
        foreach (var army in _campaign.Armies)
        {
            if (army.NationId != player?.Id || army.SupplySourceStateMapIndex <= 0) continue;
            var selected = _selectedArmyIds.Contains(army.Id);
            var eligible = economyMode
                ? (selected || (army.CutOff && army.DaysCutOff > 0))
                : ((army.CutOff && army.DaysCutOff > 0) ||
                   (selected && (army.CutOff || army.SupplyCongestion < 0.92f || army.SupplyLevel < 0.85f)));
            if (!eligible) continue;
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
        _supplyRibbonVerts.Clear();
        _supplyRibbonUvs.Clear();
        _supplyRibbonColors.Clear();
        _supplyRibbonIndices.Clear();
        _supplyMarkerVerts.Clear();
        _supplyMarkerUvs.Clear();
        _supplyMarkerColors.Clear();
        var drewAny = false;
        foreach (var army in _supplyLineArmies)
            drewAny |= AppendSupplyLine(army);
        if (_supplyRibbonIndices.Count > 0)
        {
            _supplySurfaceArrays[(int)Mesh.ArrayType.Vertex] = _supplyRibbonVerts.ToArray();
            _supplySurfaceArrays[(int)Mesh.ArrayType.TexUV] = _supplyRibbonUvs.ToArray();
            _supplySurfaceArrays[(int)Mesh.ArrayType.Color] = _supplyRibbonColors.ToArray();
            _supplySurfaceArrays[(int)Mesh.ArrayType.Index] = _supplyRibbonIndices.ToArray();
            _supplyLineMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _supplySurfaceArrays);
            _supplySurfaceArrays[(int)Mesh.ArrayType.Index] = default;
        }
        if (_supplyMarkerVerts.Count > 0)
        {
            _supplySurfaceArrays[(int)Mesh.ArrayType.Vertex] = _supplyMarkerVerts.ToArray();
            _supplySurfaceArrays[(int)Mesh.ArrayType.TexUV] = _supplyMarkerUvs.ToArray();
            _supplySurfaceArrays[(int)Mesh.ArrayType.Color] = _supplyMarkerColors.ToArray();
            _supplyLineMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, _supplySurfaceArrays);
        }
        _supplySurfaceArrays[(int)Mesh.ArrayType.Vertex] = default;
        _supplySurfaceArrays[(int)Mesh.ArrayType.TexUV] = default;
        _supplySurfaceArrays[(int)Mesh.ArrayType.Color] = default;
        _supplySurfaceArrays[(int)Mesh.ArrayType.Index] = default;
        _supplyLinePreview.Visible = drewAny && _supplyLineMesh.GetSurfaceCount() > 0;
    }

    private bool AppendSupplyLine(Simulation.Army army)
    {
        var map = _campaign?.ProvinceMap;
        if (map is null) return false;
        if (!GlobeLineSanity.TryResolveEntityGeo(army.Latitude, army.Longitude, army.ProvinceId,
                map.ProvinceCount, map.ProvinceCenterCell, map.CenterOf, out var armyGeo))
            return false;
        if (!TrySupplyHubPoint(army.SupplySourceStateMapIndex, out var hub))
            return false;
        var colour = army.CutOff
            ? new Color(0.96f, 0.34f, 0.28f, 0.92f)
            : army.SupplyCongestion < 0.999f
                ? new Color(0.96f, 0.73f, 0.34f, 0.85f)
                : new Color(0.52f, 0.82f, 0.58f, 0.72f);

        _supplyLinePoints.Clear();
        if (!TryPresentationPoint(armyGeo.Latitude, armyGeo.Longitude, SurfaceLineRadius, out var originPt))
            return false;
        _supplyLinePoints.Add(originPt);
        var latitude = armyGeo.Latitude;
        var longitude = armyGeo.Longitude;
        var deltaLongitude = GlobeLineSanity.WrapDeltaDegrees(hub.Longitude - longitude);
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(hub.Latitude - latitude),
            MathF.Abs(deltaLongitude))));
        for (var step = 1; step <= steps; step++)
        {
            var t = step / (float)steps;
            if (TryPresentationPoint(latitude + (hub.Latitude - latitude) * t,
                    longitude + deltaLongitude * t, SurfaceLineRadius, out var stepPt))
                _supplyLinePoints.Add(stepPt);
        }
        if (_supplyLinePoints.Count < 2) return false;

        // Batch every army's strip into a single indexed Triangles surface (zero per-army draw calls).
        var worldPx = GlobeLineSanity.SafeClamp(BattleArrowUnitsPerPixel(), 0.00018f, 0.0016f, 0.0006f);
        var halfWidth = GlobeLineSanity.SafeClamp(worldPx * 2.2f, 0.00055f, 0.0020f, 0.0011f);
        CollectValidLineRuns(_supplyLinePoints,
            _flatMap ? GlobeLineSanity.MaxFlatSegmentLength : GlobeLineSanity.MaxGlobeSegmentLength,
            _dateLineRuns);
        var drewStrip = false;
        foreach (var run in _dateLineRuns)
        {
            var prevPair = -1;
            var cumulative = 0f;
            var endIndex = run.Start + run.Count - 1;
            for (var index = run.Start; index <= endIndex; index++)
            {
                var point = _supplyLinePoints[index];
                if (index > run.Start) cumulative += point.DistanceTo(_supplyLinePoints[index - 1]) * 18f;
                var before = _supplyLinePoints[Math.Max(run.Start, index - 1)];
                var after = _supplyLinePoints[Math.Min(endIndex, index + 1)];
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
                    (!GlobeLineSanity.IsValidEmittedEdge(ToNum(_supplyRibbonVerts[prevPair]), ToNum(left), _flatMap, _flatX) ||
                     !GlobeLineSanity.IsValidEmittedEdge(ToNum(_supplyRibbonVerts[prevPair + 1]), ToNum(right), _flatMap, _flatX)))
                {
                    prevPair = -1;
                }
                var curr = _supplyRibbonVerts.Count;
                _supplyRibbonVerts.Add(left);
                _supplyRibbonUvs.Add(new Vector2(cumulative, 0f));
                _supplyRibbonColors.Add(colour);
                _supplyRibbonVerts.Add(right);
                _supplyRibbonUvs.Add(new Vector2(cumulative, 1f));
                _supplyRibbonColors.Add(colour);
                if (prevPair >= 0)
                {
                    _supplyRibbonIndices.Add(prevPair);
                    _supplyRibbonIndices.Add(prevPair + 1);
                    _supplyRibbonIndices.Add(curr);
                    _supplyRibbonIndices.Add(prevPair + 1);
                    _supplyRibbonIndices.Add(curr + 1);
                    _supplyRibbonIndices.Add(curr);
                    drewStrip = true;
                }
                prevPair = curr;
            }
        }
        if (!drewStrip) return false;

        // The hub end gets a crisp diamond + inner cross depot beacon batched into the Lines surface.
        var end = _supplyLinePoints[^1];
        if (GlobeLineSanity.IsValidEmittedVertex(ToNum(end), _flatMap, _flatX))
        {
            var up = _flatMap ? Vector3.Forward : end.Normalized();
            var across = GlobeLineSanity.TrySafeCrossAxis(ToNum(up), out var acrossNum) ? ToGd(acrossNum) : Vector3.Right;
            var along = GlobeLineSanity.TrySafeNormalize(ToNum(up.Cross(across)), out var alongNum) ? ToGd(alongNum) : Vector3.Up;
            var size = GlobeLineSanity.SafeClamp(worldPx * 4.8f, 0.0012f, 0.0048f, 0.0024f);
            var inner = size * 0.48f;
            if (TryOnOrderSurface(end + across * size, SurfaceLineRadius, FlatLineZ, out var pE) &&
                TryOnOrderSurface(end + along * size, SurfaceLineRadius, FlatLineZ, out var pN) &&
                TryOnOrderSurface(end - across * size, SurfaceLineRadius, FlatLineZ, out var pW) &&
                TryOnOrderSurface(end - along * size, SurfaceLineRadius, FlatLineZ, out var pS))
            {
                AddSupplyLineSegment(pE, pN, colour);
                AddSupplyLineSegment(pN, pW, colour);
                AddSupplyLineSegment(pW, pS, colour);
                AddSupplyLineSegment(pS, pE, colour);
            }
            if (TryOnOrderSurface(end - across * inner, SurfaceLineRadius, FlatLineZ, out var hW) &&
                TryOnOrderSurface(end + across * inner, SurfaceLineRadius, FlatLineZ, out var hE))
                AddSupplyLineSegment(hW, hE, colour);
            if (TryOnOrderSurface(end - along * inner, SurfaceLineRadius, FlatLineZ, out var vS) &&
                TryOnOrderSurface(end + along * inner, SurfaceLineRadius, FlatLineZ, out var vN))
                AddSupplyLineSegment(vS, vN, colour);
        }
        return true;
    }

    private void AddSupplyLineSegment(Vector3 a, Vector3 b, Color colour)
    {
        if (!GlobeLineSanity.IsValidEmittedEdge(ToNum(a), ToNum(b), _flatMap, _flatX)) return;
        var uv = new Vector2(0f, 0.5f);
        _supplyMarkerVerts.Add(a);
        _supplyMarkerUvs.Add(uv);
        _supplyMarkerColors.Add(colour);
        _supplyMarkerVerts.Add(b);
        _supplyMarkerUvs.Add(uv);
        _supplyMarkerColors.Add(colour);
    }

    /// <summary>
    /// Where a supplying state's artery is anchored: the province nearest the state's
    /// centre, so the line ends inside the region a player would point at on the map.
    /// Averages longitude relative to the first province so a state straddling the date
    /// line does not centre itself on the far side of the world.
    /// </summary>
    private bool TrySupplyHubPoint(int stateMapIndex, out (float Latitude, float Longitude) hub)
    {
        if (_supplyHubByState.TryGetValue(stateMapIndex, out hub))
            return GlobeLineSanity.IsValidGeo(hub.Latitude, hub.Longitude);
        var topology = _campaign.ProvinceMap;
        var first = true;
        float refLongitude = 0f;
        double sumLatitude = 0, sumLongitude = 0, count = 0;
        int nearest = 0;
        var nearestDistance = float.MaxValue;
        float centreLatitude = 0f, centreLongitude = 0f;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != stateMapIndex ||
                !GlobeLineSanity.TryValidateProvinceId(province, topology.ProvinceCount, topology.ProvinceCenterCell))
                continue;
            var centre = topology.CenterOf(province);
            if (!GlobeLineSanity.IsValidGeo(centre.Latitude, centre.Longitude)) continue;
            if (first)
            {
                refLongitude = centre.Longitude;
                first = false;
            }
            var delta = GlobeLineSanity.WrapDeltaDegrees(centre.Longitude - refLongitude);
            sumLatitude += centre.Latitude;
            sumLongitude += delta;
            count++;
        }
        if (count > 0)
        {
            centreLatitude = (float)(sumLatitude / count);
            centreLongitude = GlobeLineSanity.WrapLongitude(refLongitude + (float)(sumLongitude / count));
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                if (topology.ProvinceToState[province] != stateMapIndex ||
                    !GlobeLineSanity.TryValidateProvinceId(province, topology.ProvinceCount, topology.ProvinceCenterCell))
                    continue;
                var centre = topology.CenterOf(province);
                if (!GlobeLineSanity.IsValidGeo(centre.Latitude, centre.Longitude)) continue;
                var delta = GlobeLineSanity.WrapDeltaDegrees(centre.Longitude - centreLongitude);
                var distance = (centre.Latitude - centreLatitude) * (centre.Latitude - centreLatitude) +
                               delta * delta;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = province;
            }
        }
        if (nearest > 0)
        {
            hub = topology.CenterOf(nearest);
            if (GlobeLineSanity.IsValidGeo(hub.Latitude, hub.Longitude))
            {
                _supplyHubByState[stateMapIndex] = hub;
                return true;
            }
        }
        hub = (float.NaN, float.NaN);
        _supplyHubByState[stateMapIndex] = hub;
        return false;
    }
}
