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

    /// <summary>
    /// The flat map's glide: the hand's speed over the last moments of a drag, in map units a
    /// second, carried on after the button comes up and let die away - the slide the globe has
    /// and the flat map had not, which is what made it feel wooden. A hand that rested before
    /// letting go leaves nothing to carry.
    /// </summary>
    private Vector2 _flatGlide;
    private Vector2 _orbitGlide;
    private ulong _lastFlatDragUsec;
    private bool _cameraInertia = true;
    public bool CameraInertiaEnabled
    {
        get => _cameraInertia;
        set { _cameraInertia = value; if (!value) { _flatGlide = Vector2.Zero; _orbitGlide = Vector2.Zero; } }
    }

    private void BeginCameraDrag(Vector2 pixel, MouseButton button)
    {
        _dragging = true;
        _cameraDragButton = button;
        _dragDistance = 0f;
        _previousPointer = pixel;
        _flatGlide = Vector2.Zero;
        _orbitGlide = Vector2.Zero;
        _lastFlatDragUsec = Time.GetTicksUsec();
        if (!_flatMap)
        {
            _targetYaw = _yaw;
            _targetPitch = _pitch;
            _targetDistance = _distance;
            return;
        }
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
    private Image _mapModeImage;
    private byte[] _mapModePixels;
    private int _lastMapModeDay = int.MinValue;

    public bool IsFlatMap => _flatMap;
    public StrategicMapMode MapMode => _mapMode;
    private Commodity _resourceGood = Commodity.Coal;
    private ImageTexture _resourceTexture;
    private Image _resourceImage;
    private byte[] _resourcePixels;
    public Commodity ResourceGood => _resourceGood;
    public void SetResourceGood(Commodity good)
    {
        _resourceGood = good;
        if (_mapMode == StrategicMapMode.Resources) RefreshMapModePalette();
    }

    private void RefreshResourcePalette()
    {
        var pixels = _resourcePixels ??= new byte[256 * 256 * 4];
        Array.Clear(pixels);
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
        if (_resourceImage is null) _resourceImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _resourceImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("resource_colors", _resourceImage, ref _resourceTexture);
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
                Layers = AtlasSurfaceLayers,
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
        if (_flatMap == enabled || !IsInstanceValid(_camera) || !ProjectionAllowed(enabled)) return;
        _flatGlide = Vector2.Zero; _orbitGlide = Vector2.Zero;
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
            _targetPitch = _pitch = Mathf.Clamp(-Mathf.DegToRad(latitude), -MaxPitch, MaxPitch);
            _targetDistance = _distance = DistanceForFlatZoom(_flatZoom);
        }

        _flatMap = enabled;
        _globeMaterial?.SetShaderParameter("flat_projection", enabled ? 1f : 0f);
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
        {
            // When the entire stack is actively marching along the same route step,
            // smoothly interpolate its visual card position from its province centre
            // toward the next province so divisions glide across the map instead of
            // jumping abruptly between provinces. Stationary stacks stay at the exact
            // province centre.
            var start = topology.CenterOf(stack[0].ProvinceId);
            var nextProvince = stack[0].Route.Count > 0 ? stack[0].Route[0] : -1;
            if (nextProvince > 0 && nextProvince <= topology.ProvinceCount)
            {
                var allMarchingSameStep = true;
                var progressSum = 0f;
                for (var i = 0; i < stack.Count; i++)
                {
                    var a = stack[i];
                    if (a.Route.Count == 0 || a.Route[0] != nextProvince || a.MoveProgress <= 0.001f)
                    {
                        allMarchingSameStep = false;
                        break;
                    }
                    progressSum += a.MoveProgress;
                }
                if (allMarchingSameStep)
                {
                    var next = topology.CenterOf(nextProvince);
                    var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp(progressSum / stack.Count, 0f, 1f));
                    var dLon = next.Longitude - start.Longitude;
                    if (dLon > 180f) dLon -= 360f;
                    else if (dLon < -180f) dLon += 360f;
                    var lon = start.Longitude + dLon * t;
                    if (lon > 180f) lon -= 360f;
                    else if (lon <= -180f) lon += 360f;
                    return (Mathf.Lerp(start.Latitude, next.Latitude, t), lon);
                }
            }
            return start;
        }
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
            node.Quaternion = Quaternion.Identity;
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
                // Per-frame flat updates only write the target; the projection switch snaps
                // both values through SnapArmyMarkerPositions before this method is used.
                marker.TargetPosition = ArmyMarkerPosition(marker);
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
            SnapArmyMarkerPositions();
            return;
        }

        foreach (var (node, normal) in _capitalMarkers)
            if (IsInstanceValid(node)) PlaceCapitalMarker(node, normal);
        foreach (var (node, normal) in _fleetMarkers)
            if (IsInstanceValid(node)) node.Position = normal * SurfaceMarkerRadius;
        ReprojectSupportMarkers();
        SnapArmyMarkerPositions();
        foreach (var glyph in _countryGlyphs)
        {
            if (!IsInstanceValid(glyph.Node)) continue;
            glyph.Node.Position = SurfacePoint(glyph.Latitude, glyph.Longitude, SurfaceTextRadius);
            glyph.Node.Basis = glyph.SurfaceBasis;
        }
        // The basis carries the scale: the letters are full size on the globe again.
        _countryGlyphScale = 1f;
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
            RefreshRegionalPalette(state => UnrestRamp(PoliticalCohorts.UnrestHeat(state)));
            return;
        }
        if (_mapMode is StrategicMapMode.Political or StrategicMapMode.Terrain or StrategicMapMode.Relief)
        {
            _globeMaterial.SetShaderParameter("map_mode", 0f);
            return;
        }

        var pixels = _mapModePixels ??= new byte[256 * 256 * 4];
        Array.Clear(pixels);
        double minPopulation = double.MaxValue, maxPopulation = double.MinValue;
        double minGdp = double.MaxValue, maxGdp = double.MinValue;
        foreach (var nation in _campaign.Nations)
        {
            if (!CanShowMilitary(nation.Id)) continue;
            var popLog = Math.Log10(Math.Max(1, nation.Population));
            var gdpLog = Math.Log10(Math.Max(0.01, nation.Gdp));
            if (popLog < minPopulation) minPopulation = popLog;
            if (popLog > maxPopulation) maxPopulation = popLog;
            if (gdpLog < minGdp) minGdp = gdpLog;
            if (gdpLog > maxGdp) maxGdp = gdpLog;
        }
        if (minPopulation == double.MaxValue) minPopulation = maxPopulation = minGdp = maxGdp = 0;

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

        if (_mapModeImage is null) _mapModeImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _mapModeImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("map_mode_colors", _mapModeImage, ref _mapModeTexture);
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
                var relation = CampaignDiplomacy.Peek(_campaign, player.Id, nation.Id);
                if (CampaignDiplomacy.AtWar(_campaign, player.Id, nation.Id)) return Ui.Colour("map.at_war");
                // A defence bloc is an alliance on the map whatever the treaty table says: NATO's
                // members are bound through the organization, not forty bilateral pacts.
                if (InternationalOrganizations.DefencePartners(_campaign, player.Id).Contains(nation.Id))
                    return Ui.Colour("map.allied");
                var stance = relation?.Stance ?? DiplomaticStance.Neutral;
                var opinion = relation?.Opinion ?? 0f;
                var together = stance != DiplomaticStance.Rival &&
                    InternationalOrganizations.Memberships(_campaign, player.Id).Any(o => o.Members.Contains(nation.Id));
                return stance switch
                {
                    _ when together && stance != DiplomaticStance.Allied && stance != DiplomaticStance.Friendly =>
                        Ui.Colour("map.friendly").Lerp(Ui.Colour("map.neutral"), 0.45f),
                    DiplomaticStance.Allied => Ui.Colour("map.allied"),
                    DiplomaticStance.Friendly => Ui.Colour("map.friendly"),
                    DiplomaticStance.Rival => Ui.Colour("map.rival"),
                    _ => Color.FromHsv(0.58f, 0.10f, Mathf.Clamp(0.46f + opinion / 500f, 0.30f, 0.65f))
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
        using var image = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("resource_colors", image, ref _resourceTexture);
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
