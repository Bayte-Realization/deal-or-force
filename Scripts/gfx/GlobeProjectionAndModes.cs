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
    Relief,
    Resources,
    // What the mechanics are doing, where: every region's stability, the unrest and the pull to
    // leave in it, and how close the country it is seen from is to shooting with every other.
    Stability,
    Unrest,
    Tension
}

public sealed partial class GlobePresentation
{
    private const float FlatMapWidth = 20f;
    private const float FlatMapHeight = 10f;
    /// <summary>
    /// Vertical extent in map units. Tactical 2D can zoom six times closer than the
    /// former .6 floor, independently of the globe's safe camera altitude.
    /// </summary>
    private static float MinimumFlatZoom => Math.Clamp(InterfaceSkin.Metric("map.flat_min_zoom", .10f), .05f, .6f);
    // Preserve the globe's existing altitude conversion. Tactical 2D zoom can now go
    // below this reference without moving every ordinary view when changing projection.
    private const float FlatProjectionReferenceZoom = .6f;
    private const float MaximumFlatZoom = 11.5f;

    /// <summary>
    /// The same altitude expressed the other way, so switching projection keeps the view.
    ///
    /// This conversion used to be spelled out at three call sites with a pair of magic
    /// numbers - times eight plus two point four, and its inverse - which encoded the zoom
    /// limits of the day. Moving either limit silently broke every one of them.
    /// </summary>
    private static float FlatZoomForDistance(float distance) => Mathf.Clamp(
        Mathf.Lerp(FlatProjectionReferenceZoom, MaximumFlatZoom,
            Mathf.InverseLerp(MinimumDistance, MaximumDistance, distance)),
        MinimumFlatZoom, MaximumFlatZoom);

    private static float DistanceForFlatZoom(float flatZoom) => Mathf.Clamp(
        Mathf.Lerp(MinimumDistance, MaximumDistance,
            Mathf.InverseLerp(FlatProjectionReferenceZoom, MaximumFlatZoom, flatZoom)),
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
    private float _resumeTacticalFlatZoom = float.NaN;
    private bool _flatZoomAnchorActive;
    private Vector2 _flatZoomAnchorWorld;
    private Vector2 _flatZoomAnchorPixel;
    private MouseButton _cameraDragButton;
    private bool FlatViewInMotion => _dragging ||
        MathF.Abs(_targetFlatZoom - _flatZoom) > _flatZoom * .0001f ||
        MathF.Abs(_targetFlatX - _flatX) + MathF.Abs(_targetFlatY - _flatY) > _flatZoom * .0001f;

    private void BeginCameraDrag(Vector2 pixel, MouseButton button)
    {
        _dragging = true;
        _cameraDragButton = button;
        _dragDistance = 0f;
        _previousPointer = pixel;
        if (!_flatMap) return;
        _flatZoomAnchorActive = false;
        // Taking hold of the map cancels an unfinished fly-to/zoom. Starting a drag
        // from a pending target used to jump the map before it followed the hand.
        _targetFlatX = _flatX;
        _targetFlatY = _flatY;
        _targetFlatZoom = _flatZoom;
    }

    private Vector2 FlatPointerOffset(Vector2 pixel) =>
        new((pixel.X - Size.X * .5f) / Math.Max(1f, Size.Y),
            (Size.Y * .5f - pixel.Y) / Math.Max(1f, Size.Y));

    private void ZoomFlatMapAt(Vector2 pixel, float direction)
    {
        _flatZoomAnchorPixel = pixel;
        _flatZoomAnchorWorld = new Vector2(_flatX, _flatY) + FlatPointerOffset(pixel) * _flatZoom;
        var step = Math.Clamp(InterfaceSkin.Metric("map.flat_zoom_step", .15f), .04f, .3f);
        _targetFlatZoom = Mathf.Clamp(_targetFlatZoom * (1f + direction * step), MinimumFlatZoom, MaximumFlatZoom);
        var target = _flatZoomAnchorWorld - FlatPointerOffset(pixel) * _targetFlatZoom;
        _targetFlatX = target.X;
        _targetFlatY = target.Y;
        _flatZoomAnchorActive = true;
        ClampFlatCamera();
    }

    private void UpdateFlatZoomAnchor()
    {
        if (!_flatMap || !_flatZoomAnchorActive) return;
        // Follow the same geographic point during the entire smooth zoom, not just
        // at its end. That keeps the ground under the wheel at the pointer.
        var center = _flatZoomAnchorWorld - FlatPointerOffset(_flatZoomAnchorPixel) * _flatZoom;
        _flatX = center.X;
        var margin = Math.Max(0f, FlatMapHeight * .5f - _flatZoom * .5f);
        _flatY = Mathf.Clamp(center.Y, -margin, margin);
        if (Math.Abs(_flatZoom - _targetFlatZoom) < .00001f) _flatZoomAnchorActive = false;
    }
    private StrategicMapMode _mapMode;
    private ImageTexture _mapModeTexture;
    private int _lastMapModeDay = int.MinValue;

    public bool IsFlatMap => _flatMap;
    public StrategicMapMode MapMode => _mapMode;
    private Commodity _resourceGood = Commodity.Coal;
    private ImageTexture _resourceTexture;
    public Commodity ResourceGood => _resourceGood;
    public void SetResourceGood(Commodity good)
    {
        _resourceGood = good;
        if (_mapMode == StrategicMapMode.Resources) RefreshMapModePalette();
    }

    private void RefreshResourcePalette()
    {
        var pixels = new byte[256 * 256 * 4];
        var survey = ResourceAtlas.Read(_campaign, _resourceGood, InterfaceSkin.Metric("resources.minimum_potential", .05f));
        var levels = survey.Regions.ToDictionary(r => r.MapIndex, r => r.Intensity);
        var map = _campaign.ProvinceMap;
        if (map is not null)
            for (var p = 1; p <= map.ProvinceCount && p < 65536; p++)
            {
                if (map.WaterByProvince[p]) continue;
                var value = levels.GetValueOrDefault(map.ProvinceToState[p]);
                var colour = ResourceColour(value);
                var i = p * 4;
                pixels[i] = (byte)Mathf.RoundToInt(colour.R * 255);
                pixels[i + 1] = (byte)Mathf.RoundToInt(colour.G * 255);
                pixels[i + 2] = (byte)Mathf.RoundToInt(colour.B * 255);
                pixels[i + 3] = 255;
            }
        var image = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        if (_resourceTexture is null) _resourceTexture = ImageTexture.CreateFromImage(image);
        else _resourceTexture.Update(image);
        _globeMaterial.SetShaderParameter("resource_colors", _resourceTexture);
        _globeMaterial.SetShaderParameter("map_mode", 0f);
        _globeMaterial.SetShaderParameter("resource_mode", map is null ? 0f : 1f);
    }

    public static Color ResourceColour(float intensity) => intensity <= 0 ? Ui.Colour("resources.none") :
        intensity < .5f ? Ui.Colour("resources.low").Lerp(Ui.Colour("resources.mid"), intensity * 2) :
        Ui.Colour("resources.mid").Lerp(Ui.Colour("resources.high"), (intensity - .5f) * 2);
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
        _flatZoomAnchorActive = false;
        if (enabled)
        {
            var longitude = Mathf.RadToDeg(_targetYaw) - 180f;
            while (longitude <= -180f) longitude += 360f;
            while (longitude > 180f) longitude -= 360f;
            _targetFlatX = _flatX = longitude / 18f;
            _targetFlatY = _flatY = -Mathf.RadToDeg(_targetPitch) / 18f;
            _targetFlatZoom = _flatZoom = float.IsFinite(_resumeTacticalFlatZoom) &&
                Math.Abs(_targetDistance - MinimumDistance) < .0001f
                ? Math.Clamp(_resumeTacticalFlatZoom, MinimumFlatZoom, FlatProjectionReferenceZoom)
                : FlatZoomForDistance(_targetDistance);
            _resumeTacticalFlatZoom = float.NaN;
        }
        else
        {
            _resumeTacticalFlatZoom = _flatZoom < FlatProjectionReferenceZoom ? _flatZoom : float.NaN;
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
        ReprojectSupportMarkers(true);
        BuildFrontPreview();
        ApplyCamera();
        // Apply the new projection's pixel scale immediately, including its label layout.
        // Otherwise one frame can render with the previous camera's counter dimensions.
        UpdateCounterScale(force: true);
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
            _flatZoomAnchorWorld.X -= FlatMapWidth;
        }
        else if (_flatX < -FlatMapWidth * 0.5f)
        {
            _flatX += FlatMapWidth;
            _targetFlatX += FlatMapWidth;
            _flatZoomAnchorWorld.X += FlatMapWidth;
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
        var location = ArmyPresentationLocation(stack);
        return PresentationPoint(location.Latitude, location.Longitude, SurfaceMarkerRadius);
    }

    private (float Latitude, float Longitude) ArmyPresentationLocation(IReadOnlyList<Army> stack)
    {
        var topology = _campaign?.ProvinceMap;
        if (topology is not null && stack.Count > 0 && stack[0].ProvinceId > 0 &&
            stack[0].ProvinceId <= topology.ProvinceCount)
            // A counter represents one occupied province, even when some members
            // are marching. Averaging their interpolated routes would drag the entire
            // stack (including stationary members) away from its authoritative centre.
            return topology.CenterOf(stack[0].ProvinceId);
        return AverageArmyLocation(stack);
    }

    private void UpdateFlatWorldPositions()
    {
        foreach (var (node, normal) in _capitalMarkers)
        {
            if (!IsInstanceValid(node)) continue;
            var geo = GeographicFromNormal(normal);
            var position = FlatPoint(geo.Latitude, geo.Longitude, FlatMarkerZ);
            if (node.Position != position) node.Position = position;
            OrientSurfaceCounter(node, normal);
        }
        foreach (var (node, normal) in _fleetMarkers)
        {
            if (!IsInstanceValid(node)) continue;
            var geo = GeographicFromNormal(normal);
            var position = _fleetByMarker.TryGetValue(node, out var fleet)
                ? FlatFleetPosition(fleet.Id, geo.Latitude, geo.Longitude)
                : FlatPoint(geo.Latitude, geo.Longitude, FlatMarkerZ);
            if (node.Position != position) node.Position = position;
        }
        ReprojectSupportMarkers();
        foreach (var marker in _armyStacks.Values)
            if (IsInstanceValid(marker.Node))
            {
                var position = ArmyMarkerPosition(marker);
                if (marker.Node.Position != position) marker.Node.Position = position;
                OrientSurfaceArmyCard(marker);
            }
        if (FlatViewInMotion) QueueRedraw();
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
            if (IsInstanceValid(node))
            {
                node.Position = normal * SurfaceMarkerRadius;
                OrientSurfaceCounter(node, normal);
            }
        foreach (var (node, normal) in _fleetMarkers)
            if (IsInstanceValid(node)) node.Position = normal * SurfaceMarkerRadius;
        ReprojectSupportMarkers();
        foreach (var marker in _armyStacks.Values)
            if (IsInstanceValid(marker.Node))
            {
                marker.Node.Position = ArmyPresentationPoint(marker.Stack);
                OrientSurfaceArmyCard(marker);
            }
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
        if (_mapMode is StrategicMapMode.Diplomatic or StrategicMapMode.Wars or StrategicMapMode.Tension) RefreshMapModePalette();
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
        _globeMaterial.SetShaderParameter("resource_mode", 0f);
        if (_mapMode == StrategicMapMode.Resources)
        {
            RefreshResourcePalette();
            return;
        }
        if (_mapMode == StrategicMapMode.Stability)
        {
            RefreshRegionalPalette(state => StabilityRamp(state.Stability));
            return;
        }
        if (_mapMode == StrategicMapMode.Unrest)
        {
            // Whichever is worse: how radical the region is, or how far it is towards leaving.
            RefreshRegionalPalette(state => UnrestRamp(MathF.Max(state.Radicalism, Math.Clamp(state.SecessionPressure, 0f, 1f))));
            return;
        }
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
            case StrategicMapMode.Tension:
                if (nation == player) return Ui.Colour("map.player");
                if (CampaignDiplomacy.AtWar(_campaign, player.Id, nation.Id)) return Ui.Colour("map.at_war");
                var standoff = CampaignDiplomacy.Peek(_campaign, player.Id, nation.Id);
                return Escalation.StageOf(standoff) switch
                {
                    TensionStage.Clashes => Ui.Colour("state.critical"),
                    TensionStage.Crisis => Ui.Colour("state.warning"),
                    TensionStage.Friction => Ui.Colour("map.neutral").Lerp(Ui.Colour("state.warning"), 0.45f),
                    _ => Ui.Colour("map.neutral")
                };
            default:
                return new Color(nation.Color);
        }
    }

    /// <summary>
    /// A layer drawn region by region rather than country by country, through the same
    /// per-province palette the resource layer uses. Land nobody owns stays neutral.
    /// </summary>
    private void RefreshRegionalPalette(Func<ScenarioState, Color> colour)
    {
        var pixels = new byte[256 * 256 * 4];
        var map = _campaign.ProvinceMap;
        if (map is not null)
        {
            var byRegion = new Dictionary<int, Color>();
            var hidden = Ui.Colour("map.neutral");
            foreach (var state in _campaign.States)
            {
                var owner = state.NationMapIndex == 0 ? null : _campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex);
                // Every country's: how settled a region is, and how restless, is the news, not a
                // secret - the dossier of every country already says it.
                byRegion[state.MapIndex] = owner is null ? hidden : colour(state);
            }
            for (var p = 1; p <= map.ProvinceCount && p < 65536; p++)
            {
                if (map.WaterByProvince[p]) continue;
                var tone = byRegion.TryGetValue(map.ProvinceToState[p], out var found) ? found : hidden;
                var i = p * 4;
                pixels[i] = (byte)Mathf.RoundToInt(tone.R * 255);
                pixels[i + 1] = (byte)Mathf.RoundToInt(tone.G * 255);
                pixels[i + 2] = (byte)Mathf.RoundToInt(tone.B * 255);
                pixels[i + 3] = 255;
            }
        }
        var image = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        if (_resourceTexture is null) _resourceTexture = ImageTexture.CreateFromImage(image);
        else _resourceTexture.Update(image);
        _globeMaterial.SetShaderParameter("resource_colors", _resourceTexture);
        _globeMaterial.SetShaderParameter("map_mode", 0f);
        _globeMaterial.SetShaderParameter("resource_mode", map is null ? 0f : 1f);
    }

    /// <summary>Red where a region is coming apart, amber in between, green where it holds.</summary>
    private static Color StabilityRamp(float stability)
    {
        stability = Mathf.Clamp(stability, 0f, 1f);
        return stability < 0.5f
            ? Ui.Colour("state.critical").Lerp(Ui.Colour("state.warning"), stability * 2f)
            : Ui.Colour("state.warning").Lerp(Ui.Colour("state.positive"), (stability - 0.5f) * 2f);
    }

    /// <summary>Quiet regions grey, restless ones amber, those close to rising or leaving red.</summary>
    private static Color UnrestRamp(float unrest)
    {
        unrest = Mathf.Clamp(unrest, 0f, 1f);
        return unrest < 0.5f
            ? Ui.Colour("map.neutral").Lerp(Ui.Colour("state.warning"), unrest * 2f)
            : Ui.Colour("state.warning").Lerp(Ui.Colour("state.critical"), (unrest - 0.5f) * 2f);
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
        // The mechanics' layers move weekly; the census and the economy are monthly figures.
        var every = _mapMode is StrategicMapMode.Stability or StrategicMapMode.Unrest or StrategicMapMode.Tension ? 7 : 30;
        if (_campaign.ElapsedDays - _lastMapModeDay >= every) RefreshMapModePalette();
    }
}
