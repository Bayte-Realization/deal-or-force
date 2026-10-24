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
    private int _supplyLineFingerprint = int.MinValue;
    private readonly List<Vector3> _supplyLinePoints = new();
    private readonly List<Simulation.Army> _supplyLineArmies = new();
    private readonly Dictionary<int, (float Latitude, float Longitude)> _supplyHubByState = new();

    private void UpdateSupplyLines()
    {
        if (_campaign?.ProvinceMap is null || !IsInstanceValid(_routePreview)) return;
        if (!Simulation.MechanicFeatures.SystemEnabled(_campaign, nameof(Simulation.LogisticsSystem)))
        {
            if (IsInstanceValid(_supplyLinePreview)) _supplyLinePreview.Visible = false;
            return;
        }
        if (_supplyLinePreview is null || !IsInstanceValid(_supplyLinePreview))
        {
            if (_routePreview.GetParent() is not Node3D world) return;
            _supplyLineMesh = new ImmediateMesh();
            _supplyLinePreview = new MeshInstance3D
            {
                Name = "ArmySupplyLines",
                Mesh = _supplyLineMesh,
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
        // of streaking across the whole map, as a fleet's course does.
        const float halfWidth = 0.0024f;
        var open = false;
        for (var index = 0; index < _supplyLinePoints.Count; index++)
        {
            var point = _supplyLinePoints[index];
            if (index > 0 && _flatMap && MathF.Abs(point.X - _supplyLinePoints[index - 1].X) > FlatMapWidth * 0.5f)
            {
                if (open) _supplyLineMesh.SurfaceEnd();
                open = false;
            }
            if (!open)
            {
                _supplyLineMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
                open = true;
            }
            var before = _supplyLinePoints[Math.Max(0, index - 1)];
            var after = _supplyLinePoints[Math.Min(_supplyLinePoints.Count - 1, index + 1)];
            var tangent = (after - before).Normalized();
            if (tangent == Vector3.Zero) tangent = Vector3.Right;
            var normal = _flatMap ? Vector3.Forward : point.Normalized();
            var side = normal.Cross(tangent).Normalized() * halfWidth;
            _supplyLineMesh.SurfaceSetColor(colour);
            _supplyLineMesh.SurfaceAddVertex(OnOrderSurface(point + side));
            _supplyLineMesh.SurfaceSetColor(colour);
            _supplyLineMesh.SurfaceAddVertex(OnOrderSurface(point - side));
        }
        if (open) _supplyLineMesh.SurfaceEnd();

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
}
