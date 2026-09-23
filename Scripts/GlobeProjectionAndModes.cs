using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public enum StrategicMapMode
{
    Political,
    Diplomatic,
    Wars,
    Population,
    Economy,
    Terrain,
    Relief
}

public sealed partial class GlobePresentation
{
    private const float FlatMapWidth = 20f;
    private const float FlatMapHeight = 10f;
    /// <summary>
    /// The vertical extent of the flat map in world units. Its floor is the same decision
    /// as <c>MinimumDistance</c>: close enough to give an order to one division standing
    /// in one province.
    /// </summary>
    private const float MinimumFlatZoom = 0.6f;
    private const float MaximumFlatZoom = 11.5f;

    /// <summary>
    /// The same altitude expressed the other way, so switching projection keeps the view.
    ///
    /// This conversion used to be spelled out at three call sites with a pair of magic
    /// numbers - times eight plus two point four, and its inverse - which encoded the zoom
    /// limits of the day. Moving either limit silently broke every one of them.
    /// </summary>
    private static float FlatZoomForDistance(float distance) => Mathf.Clamp(
        Mathf.Lerp(MinimumFlatZoom, MaximumFlatZoom,
            Mathf.InverseLerp(MinimumDistance, MaximumDistance, distance)),
        MinimumFlatZoom, MaximumFlatZoom);

    private static float DistanceForFlatZoom(float flatZoom) => Mathf.Clamp(
        Mathf.Lerp(MinimumDistance, MaximumDistance,
            Mathf.InverseLerp(MinimumFlatZoom, MaximumFlatZoom, flatZoom)),
        MinimumDistance, MaximumDistance);

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

    /// <summary>Whether the flat map is the one being drawn. The options screen asks.</summary>
    public bool FlatProjection => _flatMap;

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
            _targetFlatZoom = _flatZoom = FlatZoomForDistance(_targetDistance);
        }
        else
        {
            var longitude = WrappedFlatX(_flatX) / FlatMapWidth * 360f;
            var latitude = _flatY / FlatMapHeight * 180f;
            _targetYaw = _yaw = Mathf.DegToRad(longitude + 180f);
            _targetPitch = _pitch = Mathf.Clamp(-Mathf.DegToRad(latitude), -1.20f, 1.20f);
            _targetDistance = _distance = DistanceForFlatZoom(_flatZoom);
        }

        _flatMap = enabled;
        _globeNode.Visible = !enabled;
        _spaceNode.Visible = !enabled;
        _flatRoot.Visible = enabled;
        _routeFingerprint = int.MinValue;
        ReprojectWorldDecorations();
        BuildFrontPreview();
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
            var position = FlatPoint(geo.Latitude, geo.Longitude, FlatMarkerZ);
            if (node.Position != position) node.Position = position;
        }
        foreach (var (node, normal) in _fleetMarkers)
        {
            if (!IsInstanceValid(node)) continue;
            var geo = GeographicFromNormal(normal);
            var position = FlatPoint(geo.Latitude, geo.Longitude, FlatMarkerZ);
            if (node.Position != position) node.Position = position;
        }
        foreach (var (node, army) in _armyByMarker)
            if (IsInstanceValid(node))
            {
                var position = ArmyPresentationPoint(army);
                if (node.Position != position) node.Position = position;
            }
        foreach (var glyph in _countryGlyphs)
        {
            if (!IsInstanceValid(glyph.Node)) continue;
            var position = FlatPoint(glyph.Latitude, glyph.Longitude, FlatTextZ);
            var rotation = new Vector3(0f, 0f, glyph.FlatRotation);
            if (glyph.Node.Position != position) glyph.Node.Position = position;
            if (glyph.Node.Rotation != rotation) glyph.Node.Rotation = rotation;
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

    /// <summary>
    /// Whose eyes the diplomatic and war layers see through: the country last picked on the
    /// map, or the player's own when nothing is picked. These two layers used to answer only
    /// "who is this to ME" - so a player looking at Brazil's neighbours, or at who is fighting
    /// on Turkey's side, had to work it out from a list. Pick the country and the map says.
    /// </summary>
    private int _mapModeSubjectId = -1;

    /// <summary>Raised when the country the layers are drawn for changes.</summary>
    public event Action<Nation> MapModeSubjectChanged;

    /// <summary>The country the diplomatic and war layers are currently drawn for.</summary>
    public Nation MapModeSubject =>
        _campaign is null ? null : _campaign.NationById(_mapModeSubjectId) ?? _campaign.Player;

    public void SetMapModeSubject(Nation nation)
    {
        var id = nation is null || nation == _campaign?.Player ? -1 : nation.Id;
        if (id == _mapModeSubjectId) return;
        _mapModeSubjectId = id;
        if (_mapMode is StrategicMapMode.Diplomatic or StrategicMapMode.Wars) RefreshMapModePalette();
        MapModeSubjectChanged?.Invoke(MapModeSubject);
    }

    public void SetMapMode(StrategicMapMode mode)
    {
        _mapMode = mode;
        _globeMaterial?.SetShaderParameter("terrain_mode", mode == StrategicMapMode.Terrain ? 1f : mode == StrategicMapMode.Relief ? 2f : 0f);
        _globeMaterial?.SetShaderParameter("map_mode", mode == StrategicMapMode.Political ? 0f : 1f);
        RefreshMapModePalette();
        MapModeChanged?.Invoke(mode);
    }

    public void RefreshMapModePalette()
    {
        if (_campaign is null || _globeMaterial is null) return;
        _lastMapModeDay = _campaign.ElapsedDays;
        if (_mapMode is StrategicMapMode.Political or StrategicMapMode.Terrain or StrategicMapMode.Relief)
        {
            _globeMaterial.SetShaderParameter("map_mode", 0f);
            return;
        }

        var pixels = new byte[256 * 256 * 4];
        var knownNations = _campaign.Nations.Where(n => CanShowMilitary(n.Id)).ToArray();
        var populationLogs = knownNations.Select(nation => Math.Log10(Math.Max(1, nation.Population))).DefaultIfEmpty(0).ToArray();
        var gdpLogs = knownNations.Select(nation => Math.Log10(Math.Max(0.01, nation.Gdp))).DefaultIfEmpty(0).ToArray();
        var minPopulation = populationLogs.Min();
        var maxPopulation = populationLogs.Max();
        var minGdp = gdpLogs.Min();
        var maxGdp = gdpLogs.Max();

        foreach (var nation in _campaign.Nations)
        {
            var color = !CanShowMilitary(nation.Id) && _mapMode is StrategicMapMode.Population or StrategicMapMode.Economy
                ? new Color(.22f,.25f,.28f) : ModeColor(nation, minPopulation, maxPopulation, minGdp, maxGdp);
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
        // "player" is whoever the layer is seen from; the player's own country unless another
        // has been picked on the map.
        var player = MapModeSubject ?? _campaign.Player;
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
        if (_mapMode is StrategicMapMode.Political or StrategicMapMode.Terrain or StrategicMapMode.Relief || _campaign is null) return;
        if (_campaign.ElapsedDays - _lastMapModeDay >= 30) RefreshMapModePalette();
    }
}
