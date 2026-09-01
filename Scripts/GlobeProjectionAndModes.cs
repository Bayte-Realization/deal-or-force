using System;
using System.Collections.Generic;
using System.Linq;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

public enum StrategicMapMode
{
    Political,
    Diplomatic,
    Wars,
    Population,
    Economy
}

public sealed partial class GlobePresentation
{
    private const float FlatMapWidth = 20f;
    private const float FlatMapHeight = 10f;
    private const float MinimumFlatZoom = 2.2f;
    private const float MaximumFlatZoom = 11.5f;

    private MeshInstance3D _globeNode = null!;
    private Node3D _spaceNode = null!;
    private Node3D _flatRoot = null!;
    private bool _flatMap;
    private float _flatX;
    private float _flatY;
    private float _targetFlatX;
    private float _targetFlatY;
    private float _flatZoom = 8.8f;
    private float _targetFlatZoom = 8.8f;
    private StrategicMapMode _mapMode;
    private ImageTexture _mapModeTexture;
    private int _lastMapModeDay = int.MinValue;

    public bool IsFlatMap => _flatMap;
    public StrategicMapMode MapMode => _mapMode;
    public event Action<bool> ProjectionChanged;
    public event Action<StrategicMapMode> MapModeChanged;

    private void CreateFlatMap(Node3D world)
    {
        _flatRoot = new Node3D { Name = "InfiniteFlatMap", Visible = false };
        world.AddChild(_flatRoot);
        for (var copy = -1; copy <= 1; copy++)
        {
            var quad = new QuadMesh
            {
                Size = new Vector2(FlatMapWidth, FlatMapHeight),
                Material = _globeMaterial
            };
            var tile = new MeshInstance3D
            {
                Name = $"WorldCopy_{copy:+0;-0;0}",
                Mesh = quad,
                Position = new Vector3(copy * FlatMapWidth, 0f, 0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            _flatRoot.AddChild(tile);
        }
    }

    public void ToggleProjection() => SetFlatMap(!_flatMap);

    public void SetFlatMap(bool enabled)
    {
        if (_flatMap == enabled || !IsInstanceValid(_camera)) return;
        if (enabled)
        {
            var longitude = Mathf.RadToDeg(_targetYaw) - 180f;
            while (longitude <= -180f) longitude += 360f;
            while (longitude > 180f) longitude -= 360f;
            _targetFlatX = _flatX = longitude / 18f;
            _targetFlatY = _flatY = -Mathf.RadToDeg(_targetPitch) / 18f;
            _targetFlatZoom = _flatZoom = Mathf.Clamp(
                (_targetDistance - MinimumDistance) / (MaximumDistance - MinimumDistance) * 8f + 2.4f,
                MinimumFlatZoom, MaximumFlatZoom);
        }
        else
        {
            var longitude = WrappedFlatX(_flatX) / FlatMapWidth * 360f;
            var latitude = _flatY / FlatMapHeight * 180f;
            _targetYaw = _yaw = Mathf.DegToRad(longitude + 180f);
            _targetPitch = _pitch = Mathf.Clamp(-Mathf.DegToRad(latitude), -1.20f, 1.20f);
            _targetDistance = _distance = Mathf.Clamp(
                (_flatZoom - 2.4f) / 8f * (MaximumDistance - MinimumDistance) + MinimumDistance,
                MinimumDistance, MaximumDistance);
        }

        _flatMap = enabled;
        _globeNode.Visible = !enabled;
        _spaceNode.Visible = !enabled;
        _flatRoot.Visible = enabled;
        _routeFingerprint = int.MinValue;
        ReprojectWorldDecorations();
        ApplyCamera();
        ProjectionChanged?.Invoke(enabled);
    }

    private void ApplyFlatCamera()
    {
        RebaseFlatCamera();
        _camera.Projection = Camera3D.ProjectionType.Orthogonal;
        _camera.Size = _flatZoom;
        _orbit.Rotation = Vector3.Zero;
        _camera.Position = new Vector3(_flatX, _flatY, 12f);
        _camera.LookAt(new Vector3(_flatX, _flatY, 0f), Vector3.Up);
    }

    private void ClampFlatCamera()
    {
        var viewport = ViewportSize();
        var aspect = viewport.X / (float)Math.Max(1, viewport.Y);
        var verticalHalf = _targetFlatZoom * 0.5f;
        var margin = Math.Max(0f, FlatMapHeight * 0.5f - verticalHalf);
        _targetFlatY = Mathf.Clamp(_targetFlatY, -margin, margin);
        while (_targetFlatX - _flatX > FlatMapWidth * 0.5f) _targetFlatX -= FlatMapWidth;
        while (_targetFlatX - _flatX < -FlatMapWidth * 0.5f) _targetFlatX += FlatMapWidth;
        _ = aspect;
    }

    private void RebaseFlatCamera()
    {
        if (_flatX > FlatMapWidth * 0.5f)
        {
            _flatX -= FlatMapWidth;
            _targetFlatX -= FlatMapWidth;
        }
        else if (_flatX < -FlatMapWidth * 0.5f)
        {
            _flatX += FlatMapWidth;
            _targetFlatX += FlatMapWidth;
        }
    }

    private Vector3 PresentationPoint(float latitude, float longitude, float sphereRadius) =>
        _flatMap ? FlatPoint(latitude, longitude, FlatLineZ) : SurfacePoint(latitude, longitude, sphereRadius);

    private Vector3 FlatPoint(float latitude, float longitude, float z)
    {
        var x = longitude / 360f * FlatMapWidth;
        while (x - _flatX > FlatMapWidth * 0.5f) x -= FlatMapWidth;
        while (x - _flatX < -FlatMapWidth * 0.5f) x += FlatMapWidth;
        return new Vector3(x, latitude / 180f * FlatMapHeight, z);
    }

    private static float WrappedFlatX(float x)
    {
        while (x <= -FlatMapWidth * 0.5f) x += FlatMapWidth;
        while (x > FlatMapWidth * 0.5f) x -= FlatMapWidth;
        return x;
    }

    private static (float Latitude, float Longitude) GeographicFromNormal(Vector3 normal)
    {
        normal = normal.Normalized();
        var latitude = Mathf.RadToDeg(Mathf.Asin(normal.Y));
        var longitude = Mathf.RadToDeg(Mathf.Atan2(normal.X, normal.Z)) - 180f;
        while (longitude <= -180f) longitude += 360f;
        while (longitude > 180f) longitude -= 360f;
        return (latitude, longitude);
    }

    private Vector3 ArmyPresentationPoint(IReadOnlyList<Army> stack)
    {
        if (stack is null || stack.Count == 0)
            return Vector3.Zero;
        var location = AverageArmyLocation(stack);
        return PresentationPoint(location.Latitude, location.Longitude, SurfaceMarkerRadius);
    }

    private void UpdateFlatWorldPositions()
    {
        foreach (var (node, normal) in _capitalMarkers)
        {
            if (!IsInstanceValid(node)) continue;
            var geo = GeographicFromNormal(normal);
            node.Position = FlatPoint(geo.Latitude, geo.Longitude, FlatMarkerZ);
        }
        foreach (var (node, normal) in _fleetMarkers)
        {
            if (!IsInstanceValid(node)) continue;
            var geo = GeographicFromNormal(normal);
            node.Position = FlatPoint(geo.Latitude, geo.Longitude, FlatMarkerZ);
        }
        foreach (var (node, army) in _armyByMarker)
            if (IsInstanceValid(node)) node.Position = ArmyPresentationPoint(army);
        foreach (var glyph in _countryGlyphs)
        {
            if (!IsInstanceValid(glyph.Node)) continue;
            glyph.Node.Position = FlatPoint(glyph.Latitude, glyph.Longitude, FlatTextZ);
            glyph.Node.Rotation = new Vector3(0f, 0f, glyph.FlatRotation);
        }
        ReprojectBattleMarkers();
    }

    private void ReprojectWorldDecorations()
    {
        if (_flatMap)
        {
            UpdateFlatWorldPositions();
            return;
        }

        foreach (var (node, normal) in _capitalMarkers)
            if (IsInstanceValid(node)) node.Position = normal * SurfaceMarkerRadius;
        foreach (var (node, normal) in _fleetMarkers)
            if (IsInstanceValid(node)) node.Position = normal * SurfaceMarkerRadius;
        foreach (var (node, army) in _armyByMarker)
            if (IsInstanceValid(node)) node.Position = ArmyPresentationPoint(army);
        foreach (var glyph in _countryGlyphs)
        {
            if (!IsInstanceValid(glyph.Node)) continue;
            glyph.Node.Position = SurfacePoint(glyph.Latitude, glyph.Longitude, SurfaceTextRadius);
            glyph.Node.Basis = glyph.SurfaceBasis;
        }
        ReprojectBattleMarkers();
    }

    public void SetMapMode(StrategicMapMode mode)
    {
        _mapMode = mode;
        _globeMaterial?.SetShaderParameter("map_mode", mode == StrategicMapMode.Political ? 0f : 1f);
        RefreshMapModePalette();
        MapModeChanged?.Invoke(mode);
    }

    public void RefreshMapModePalette()
    {
        if (_campaign is null || _globeMaterial is null) return;
        _lastMapModeDay = _campaign.ElapsedDays;
        if (_mapMode == StrategicMapMode.Political)
        {
            _globeMaterial.SetShaderParameter("map_mode", 0f);
            return;
        }

        var pixels = new byte[256 * 256 * 4];
        var populationLogs = _campaign.Nations.Select(nation => Math.Log10(Math.Max(1, nation.Population))).ToArray();
        var gdpLogs = _campaign.Nations.Select(nation => Math.Log10(Math.Max(0.01, nation.Gdp))).ToArray();
        var minPopulation = populationLogs.Min();
        var maxPopulation = populationLogs.Max();
        var minGdp = gdpLogs.Min();
        var maxGdp = gdpLogs.Max();

        foreach (var nation in _campaign.Nations)
        {
            var color = ModeColor(nation, minPopulation, maxPopulation, minGdp, maxGdp);
            var write = nation.MapIndex * 4;
            if (write < 0 || write + 3 >= pixels.Length) continue;
            pixels[write] = (byte)Mathf.RoundToInt(color.R * 255f);
            pixels[write + 1] = (byte)Mathf.RoundToInt(color.G * 255f);
            pixels[write + 2] = (byte)Mathf.RoundToInt(color.B * 255f);
            pixels[write + 3] = 255;
        }

        var image = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        if (_mapModeTexture is null)
        {
            _mapModeTexture = ImageTexture.CreateFromImage(image);
            _globeMaterial.SetShaderParameter("map_mode_colors", _mapModeTexture);
        }
        else _mapModeTexture.Update(image);
        _globeMaterial.SetShaderParameter("map_mode", 1f);
    }

    private Color ModeColor(Nation nation, double minPopulation, double maxPopulation, double minGdp, double maxGdp)
    {
        var player = _campaign.Player;
        switch (_mapMode)
        {
            case StrategicMapMode.Diplomatic:
                if (nation == player) return Ui.Colour("map.player");
                var relation = CampaignDiplomacy.Get(_campaign, player.Id, nation.Id);
                if (CampaignDiplomacy.AtWar(_campaign, player.Id, nation.Id)) return Ui.Colour("map.at_war");
                return relation.Stance switch
                {
                    DiplomaticStance.Allied => Ui.Colour("map.allied"),
                    DiplomaticStance.Friendly => Ui.Colour("map.friendly"),
                    DiplomaticStance.Rival => Ui.Colour("map.rival"),
                    _ => Color.FromHsv(0.58f, 0.10f, Mathf.Clamp(0.46f + relation.Opinion / 500f, 0.30f, 0.65f))
                };
            case StrategicMapMode.Wars:
                if (nation == player) return Ui.Colour("map.war_self");
                var war = _campaign.Wars.FirstOrDefault(candidate => !candidate.Ended &&
                    (candidate.Involves(player.Id) || candidate.Involves(nation.Id)) && candidate.Involves(nation.Id));
                if (war is null) return Ui.Colour("map.neutral");
                if (war.AttackerId == nation.Id) return Ui.Colour("map.war_attacker");
                if (war.DefenderId == nation.Id) return Ui.Colour("map.war_defender");
                return Ui.Colour("map.war_bystander");
            case StrategicMapMode.Population:
                return HeatRamp((float)((Math.Log10(Math.Max(1, nation.Population)) - minPopulation) /
                    Math.Max(0.001, maxPopulation - minPopulation)));
            case StrategicMapMode.Economy:
                return EconomicRamp((float)((Math.Log10(Math.Max(0.01, nation.Gdp)) - minGdp) /
                    Math.Max(0.001, maxGdp - minGdp)));
            default:
                return new Color(nation.Color);
        }
    }

    private static Color HeatRamp(float value)
    {
        value = Mathf.Clamp(value, 0f, 1f);
        return value < 0.5f
            ? Ui.Colour("map.value_low").Lerp(Ui.Colour("map.value_mid"), value * 2f)
            : Ui.Colour("map.value_mid").Lerp(Ui.Colour("map.value_high"), (value - 0.5f) * 2f);
    }

    private static Color EconomicRamp(float value)
    {
        value = Mathf.Clamp(value, 0f, 1f);
        return value < 0.5f
            ? Ui.Colour("map.terrain_low").Lerp(Ui.Colour("map.terrain_mid"), value * 2f)
            : Ui.Colour("map.terrain_mid").Lerp(Ui.Colour("map.terrain_high"), (value - 0.5f) * 2f);
    }

    private void UpdateMapModePaletteIfNeeded()
    {
        if (_mapMode == StrategicMapMode.Political || _campaign is null) return;
        if (_campaign.ElapsedDays - _lastMapModeDay >= 30) RefreshMapModePalette();
    }
}
