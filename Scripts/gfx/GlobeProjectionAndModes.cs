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
    Tension,
    // What each region believes: its largest denomination, in a shade of its family's colour.
    Religion
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
    private QuadMesh[] _flatQuads;
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
        _globeZoomAnchorActive = false;
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

    private void ZoomFlatMapAt(Vector2 pixel, float direction, bool fine = false)
    {
        _flatZoomAnchorPixel = pixel;
        _flatZoomAnchorWorld = new Vector2(_flatX, _flatY) + FlatPointerOffset(pixel) * _flatZoom;
        var baseStep = Math.Clamp(InterfaceSkin.Metric("map.flat_zoom_step", .15f), .04f, .3f);
        var step = Simulation.Controls.ControlSchemeRules.ComputeZoomStep(baseStep, fine, 0.35f);
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
        _flatQuads = new QuadMesh[3];
        for (var copy = -1; copy <= 1; copy++)
        {
            var quad = new QuadMesh
            {
                Size = new Vector2(FlatMapWidth, FlatMapHeight),
                Material = _globeMaterial
            };
            _flatQuads[copy + 1] = quad;
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
        // Apply initial 3D relief subdivision to the newly created flat quads.
        _cachedReliefMeshEnabled = null;
        ApplyTerrainRelief3D();
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
        SyncWeatherReliefShells();
        _globeNode.Visible = !enabled;
        _spaceNode.Visible = !enabled;
        _flatRoot.Visible = enabled;
        _routeFingerprint = int.MinValue;
        _cameraTransformDirty = true;
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
        _flatMap ? LocalFlatPoint(latitude, longitude, FlatLineZ) : LocalSurfacePoint(latitude, longitude, sphereRadius);

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

    private bool _forceFlatReproject;
    private float _lastFlatCapX = float.NaN;
    private int _lastFlatCapRev = int.MinValue;
    private int _lastFlatDestroyedCount = int.MinValue;
    private int _lastFlatCapCount = -1;
    private float _lastFlatCapRelief = -1f;
    private byte[] _lastFlatCapReliefGrid;
    private readonly List<Vector3> _flatCapitalBasePos = new();
    private readonly List<float> _flatCapitalAppliedX = new();
    private float _lastFlatGlyphX = float.NaN;
    private float _lastFlatGlyphRelief = -1f;
    private byte[] _lastFlatGlyphReliefGrid;
    private object _lastFlatGlyphsRef;
    private int _lastFlatGlyphsCount = -1;
    private readonly List<Vector3> _flatGlyphBasePos = new();
    private readonly List<float> _flatGlyphAppliedX = new();
    private float _lastFlatMobileX = float.NaN;
    private float _lastFlatMobileCamSize = float.NaN;
    private int _lastFlatMobileMapRev = int.MinValue;
    private long _lastFlatMobileHours = long.MinValue;
    private int _lastFlatMobileArmyCount = -1;
    private int _lastFlatMobileFleetCount = -1;
    private int _lastFlatMobileBattleCount = -1;
    private int _lastFlatMobileSupportCount = -1;
    private readonly List<(Node3D Node, float BaseX, float WrappedX)> _flatFleetWrapCache = new();
    private readonly List<(SupportMapMarker Marker, float BaseX, float WrappedX)> _flatSupportWrapCache = new();
    private readonly List<(Node3D Node, Vector3 Normal, float BaseX, float WrappedX)> _flatBattleWrapCache = new();
    private readonly List<(ArmyStackMarker Marker, float BaseX, float WrappedX)> _flatArmyWrapCache = new();
    private ulong _lastFlatPausedArmyMsec;
    private ulong _nextFlatLeaderRedrawMsec;
    private bool _flatLeaderRedrawPending;

    private void UpdateFlatWorldPositions()
    {
        var force = _forceFlatReproject;
        _forceFlatReproject = false;
        var flatInMotion = FlatViewInMotion;
        UpdateFlatCapitals(force);
        UpdateFlatMobileMarkers(force);
        UpdateFlatCountryGlyphs(force);
        UpdateFlatLeaderRedraw(flatInMotion, Time.GetTicksMsec());
    }

    private void UpdateFlatCapitals(bool force)
    {
        var destroyedCount = _campaign?.DestroyedProvinces.Count ?? -1;
        var cacheInvalid = _lastFlatCapRev != _capitalRevision ||
            _lastFlatDestroyedCount != destroyedCount ||
            _lastFlatCapCount != _capitalMarkers.Count ||
            _flatCapitalBasePos.Count != _capitalMarkers.Count ||
            _flatCapitalAppliedX.Count != _capitalMarkers.Count ||
            !Mathf.IsEqualApprox(_lastFlatCapRelief, _reliefDisplacement) ||
            !ReferenceEquals(_lastFlatCapReliefGrid, _reliefHeightGrid);

        if (!force && !cacheInvalid && float.IsFinite(_lastFlatCapX) && MathF.Abs(_flatX - _lastFlatCapX) <= 1e-5f)
            return;

        if (cacheInvalid)
        {
            var reliefActive = _reliefDisplacement > 0.0001f;
            var reliefBias = reliefActive ? 0.004f : 0f;
            var footprint = reliefActive ? 0.045f : 0f;
            _flatCapitalBasePos.Clear();
            _flatCapitalAppliedX.Clear();
            _flatCapitalBasePos.Capacity = Math.Max(_flatCapitalBasePos.Capacity, _capitalMarkers.Count);
            _flatCapitalAppliedX.Capacity = Math.Max(_flatCapitalAppliedX.Capacity, _capitalMarkers.Count);
            for (var i = 0; i < _capitalMarkers.Count; i++)
            {
                var geo = GeographicFromNormal(_capitalMarkers[i].Normal);
                var z = FlatMarkerZ + reliefBias + MaxReliefLiftAround(geo.Latitude, geo.Longitude, footprint, flat: true);
                _flatCapitalBasePos.Add(new Vector3(geo.Longitude / 360f * FlatMapWidth, geo.Latitude / 180f * FlatMapHeight, z));
                _flatCapitalAppliedX.Add(float.NaN);
            }
            _lastFlatCapRev = _capitalRevision;
            _lastFlatDestroyedCount = destroyedCount;
            _lastFlatCapCount = _capitalMarkers.Count;
            _lastFlatCapRelief = _reliefDisplacement;
            _lastFlatCapReliefGrid = _reliefHeightGrid;
        }
        else if (force)
        {
            for (var i = 0; i < _flatCapitalAppliedX.Count; i++)
                _flatCapitalAppliedX[i] = float.NaN;
        }

        _lastFlatCapX = _flatX;
        for (var i = 0; i < _capitalMarkers.Count; i++)
        {
            var basePos = _flatCapitalBasePos[i];
            var wrappedX = DateLineSeam.NearestCopy(basePos.X, _flatX, FlatMapWidth);
            if (!force && !cacheInvalid && wrappedX == _flatCapitalAppliedX[i]) continue;
            var node = _capitalMarkers[i].Node;
            if (!IsInstanceValid(node)) continue;
            _flatCapitalAppliedX[i] = wrappedX;
            var position = new Vector3(wrappedX, basePos.Y, basePos.Z);
            if (node.Position != position) node.Position = position;
            if ((force || cacheInvalid) && node.Quaternion != Quaternion.Identity) node.Quaternion = Quaternion.Identity;
        }
    }

    private void UpdateFlatMobileMarkers(bool force)
    {
        var mapRev = _campaign?.MapRevision ?? -1;
        var hours = _campaign?.ElapsedHours ?? -1;
        var unpaused = _campaign is { IsPaused: false };
        var camSize = _flatZoom;
        var simOrCountDirty = force ||
            _lastFlatMobileMapRev != mapRev ||
            (unpaused && hours != _lastFlatMobileHours) ||
            _lastFlatMobileArmyCount != _armyStacks.Count ||
            _lastFlatMobileFleetCount != _fleetMarkers.Count ||
            _lastFlatMobileBattleCount != _battleMarkers.Count ||
            _lastFlatMobileSupportCount != _supportMapMarkers.Count;
        var zoomChanged = !float.IsFinite(_lastFlatMobileCamSize) ||
            MathF.Abs(camSize - _lastFlatMobileCamSize) > MathF.Max(0.0002f, camSize * 0.001f);
        var xChanged = !float.IsFinite(_lastFlatMobileX) || MathF.Abs(_flatX - _lastFlatMobileX) > 1e-5f;
        var nowMsec = Time.GetTicksMsec();
        var unpausedArmyTick = unpaused && nowMsec - _lastFlatPausedArmyMsec >= 120UL;

        if (!simOrCountDirty && !zoomChanged && !xChanged && !unpausedArmyTick)
            return;

        if (simOrCountDirty || zoomChanged)
        {
            _lastFlatMobileX = _flatX;
            _lastFlatMobileCamSize = camSize;
            _lastFlatMobileMapRev = mapRev;
            _lastFlatMobileHours = hours;
            _lastFlatMobileArmyCount = _armyStacks.Count;
            _lastFlatMobileFleetCount = _fleetMarkers.Count;
            _lastFlatMobileBattleCount = _battleMarkers.Count;
            _lastFlatMobileSupportCount = _supportMapMarkers.Count;

            _flatFleetWrapCache.Clear();
            _flatFleetWrapCache.Capacity = Math.Max(_flatFleetWrapCache.Capacity, _fleetMarkers.Count);
            foreach (var (node, normal) in _fleetMarkers)
            {
                if (!IsInstanceValid(node)) continue;
                var geo = GeographicFromNormal(normal);
                var baseX = geo.Longitude / 360f * FlatMapWidth;
                var position = _fleetByMarker.TryGetValue(node, out var fleet)
                    ? FlatFleetPosition(fleet.Id, geo.Latitude, geo.Longitude)
                    : LocalFlatPoint(geo.Latitude, geo.Longitude, FlatMarkerZ);
                if (node.Position != position) node.Position = position;
                _flatFleetWrapCache.Add((node, baseX, DateLineSeam.NearestCopy(baseX, _flatX, FlatMapWidth)));
            }

            ReprojectSupportMarkers();
            _flatSupportWrapCache.Clear();
            _flatSupportWrapCache.Capacity = Math.Max(_flatSupportWrapCache.Capacity, _supportMapMarkers.Count);
            foreach (var marker in _supportMapMarkers.Values)
            {
                if (!IsInstanceValid(marker.Node)) continue;
                var baseX = marker.Longitude / 360f * FlatMapWidth;
                _flatSupportWrapCache.Add((marker, baseX, DateLineSeam.NearestCopy(baseX, _flatX, FlatMapWidth)));
            }

            ReprojectBattleMarkers();
            _flatBattleWrapCache.Clear();
            _flatBattleWrapCache.Capacity = Math.Max(_flatBattleWrapCache.Capacity, _battleMarkers.Count);
            foreach (var marker in _battleMarkers.Values)
            {
                if (!IsInstanceValid(marker.Node)) continue;
                var geo = GeographicFromNormal(marker.Normal);
                var baseX = geo.Longitude / 360f * FlatMapWidth;
                _flatBattleWrapCache.Add((marker.Node, marker.Normal, baseX, DateLineSeam.NearestCopy(baseX, _flatX, FlatMapWidth)));
            }
        }
        else if (xChanged)
        {
            _lastFlatMobileX = _flatX;

            for (var i = 0; i < _flatFleetWrapCache.Count; i++)
            {
                var entry = _flatFleetWrapCache[i];
                var wrappedX = DateLineSeam.NearestCopy(entry.BaseX, _flatX, FlatMapWidth);
                if (wrappedX == entry.WrappedX) continue;
                _flatFleetWrapCache[i] = (entry.Node, entry.BaseX, wrappedX);
                if (!IsInstanceValid(entry.Node)) continue;
                var pos = entry.Node.Position;
                var shifted = new Vector3(pos.X + (wrappedX - entry.WrappedX), pos.Y, pos.Z);
                if (entry.Node.Position != shifted) entry.Node.Position = shifted;
            }

            for (var i = 0; i < _flatSupportWrapCache.Count; i++)
            {
                var entry = _flatSupportWrapCache[i];
                var wrappedX = DateLineSeam.NearestCopy(entry.BaseX, _flatX, FlatMapWidth);
                if (wrappedX == entry.WrappedX) continue;
                _flatSupportWrapCache[i] = (entry.Marker, entry.BaseX, wrappedX);
                if (!IsInstanceValid(entry.Marker.Node)) continue;
                var pos = entry.Marker.Node.Position;
                var shifted = new Vector3(pos.X + (wrappedX - entry.WrappedX), pos.Y, pos.Z);
                if (entry.Marker.Node.Position != shifted) entry.Marker.Node.Position = shifted;
            }

            var anyBattleWrapped = false;
            for (var i = 0; i < _flatBattleWrapCache.Count; i++)
            {
                var entry = _flatBattleWrapCache[i];
                var wrappedX = DateLineSeam.NearestCopy(entry.BaseX, _flatX, FlatMapWidth);
                if (wrappedX == entry.WrappedX) continue;
                _flatBattleWrapCache[i] = (entry.Node, entry.Normal, entry.BaseX, wrappedX);
                if (!IsInstanceValid(entry.Node)) continue;
                PositionBattleMarker(entry.Node, entry.Normal);
                anyBattleWrapped = true;
            }
            if (anyBattleWrapped) _battleMarkerDay = -1;
        }

        if (simOrCountDirty || zoomChanged || unpausedArmyTick)
        {
            _lastFlatPausedArmyMsec = nowMsec;
            _flatArmyWrapCache.Clear();
            _flatArmyWrapCache.Capacity = Math.Max(_flatArmyWrapCache.Capacity, _armyStacks.Count);
            foreach (var marker in _armyStacks.Values)
            {
                if (!IsInstanceValid(marker.Node)) continue;
                // Per-frame flat updates only write the target; the projection switch snaps
                // both values through SnapArmyMarkerPositions before this method is used.
                var targetPos = ArmyMarkerPosition(marker);
                marker.TargetPosition = targetPos;
                var loc = ArmyPresentationLocation(marker.Stack);
                var baseX = loc.Longitude / 360f * FlatMapWidth;
                _flatArmyWrapCache.Add((marker, baseX, DateLineSeam.NearestCopy(baseX, _flatX, FlatMapWidth)));
            }
        }
        else if (xChanged)
        {
            for (var i = 0; i < _flatArmyWrapCache.Count; i++)
            {
                var entry = _flatArmyWrapCache[i];
                var wrappedX = DateLineSeam.NearestCopy(entry.BaseX, _flatX, FlatMapWidth);
                if (wrappedX == entry.WrappedX) continue;
                _flatArmyWrapCache[i] = (entry.Marker, entry.BaseX, wrappedX);
                if (!IsInstanceValid(entry.Marker.Node)) continue;
                entry.Marker.TargetPosition = ArmyMarkerPosition(entry.Marker);
            }
        }
    }

    private void UpdateFlatLeaderRedraw(bool flatInMotion, ulong nowMsec)
    {
        if (flatInMotion && _auxiliaryCounters.Count > 0 && MilitaryMarkersEnabled)
        {
            if (nowMsec >= _nextFlatLeaderRedrawMsec)
            {
                _nextFlatLeaderRedrawMsec = nowMsec + (Platform.PlatformProfile.Current?.IsTouchPrimary == true ? 66UL : 50UL);
                _flatLeaderRedrawPending = false;
                QueueRedraw();
            }
            else
            {
                _flatLeaderRedrawPending = true;
            }
        }
        else if (!flatInMotion && _flatLeaderRedrawPending)
        {
            _flatLeaderRedrawPending = false;
            QueueRedraw();
        }
    }

    private void UpdateFlatCountryGlyphs(bool force)
    {
        var cacheInvalid = !ReferenceEquals(_lastFlatGlyphsRef, _lastLabelAnchors) ||
            _lastFlatGlyphsCount != _countryGlyphs.Count ||
            _flatGlyphBasePos.Count != _countryGlyphs.Count ||
            _flatGlyphAppliedX.Count != _countryGlyphs.Count ||
            !Mathf.IsEqualApprox(_lastFlatGlyphRelief, _reliefDisplacement) ||
            !ReferenceEquals(_lastFlatGlyphReliefGrid, _reliefHeightGrid);

        if (!force && !cacheInvalid && float.IsFinite(_lastFlatGlyphX) && MathF.Abs(_flatX - _lastFlatGlyphX) <= 1e-5f)
            return;

        if (cacheInvalid)
        {
            var glyphBias = _reliefDisplacement > 0.0001f ? 0.008f : 0f;
            _flatGlyphBasePos.Clear();
            _flatGlyphAppliedX.Clear();
            _flatGlyphBasePos.Capacity = Math.Max(_flatGlyphBasePos.Capacity, _countryGlyphs.Count);
            _flatGlyphAppliedX.Capacity = Math.Max(_flatGlyphAppliedX.Capacity, _countryGlyphs.Count);
            for (var i = 0; i < _countryGlyphs.Count; i++)
            {
                var glyph = _countryGlyphs[i];
                var z = FlatTextZ + glyphBias + MaxReliefLiftAround(glyph.Latitude, glyph.Longitude, glyph.FootprintRadius, flat: true);
                _flatGlyphBasePos.Add(new Vector3(glyph.Longitude / 360f * FlatMapWidth, glyph.Latitude / 180f * FlatMapHeight, z));
                _flatGlyphAppliedX.Add(float.NaN);
            }
            _lastFlatGlyphsRef = _lastLabelAnchors;
            _lastFlatGlyphsCount = _countryGlyphs.Count;
            _lastFlatGlyphRelief = _reliefDisplacement;
            _lastFlatGlyphReliefGrid = _reliefHeightGrid;
        }
        else if (force)
        {
            for (var i = 0; i < _flatGlyphAppliedX.Count; i++)
                _flatGlyphAppliedX[i] = float.NaN;
        }

        _lastFlatGlyphX = _flatX;
        for (var i = 0; i < _countryGlyphs.Count; i++)
        {
            var basePos = _flatGlyphBasePos[i];
            var wrappedX = DateLineSeam.NearestCopy(basePos.X, _flatX, FlatMapWidth);
            if (!force && !cacheInvalid && wrappedX == _flatGlyphAppliedX[i]) continue;
            var glyph = _countryGlyphs[i];
            if (!IsInstanceValid(glyph.Node)) continue;
            _flatGlyphAppliedX[i] = wrappedX;
            var position = new Vector3(wrappedX, basePos.Y, basePos.Z);
            if (glyph.Node.Position != position) glyph.Node.Position = position;
            if (cacheInvalid || force)
            {
                var rotation = new Vector3(0f, 0f, glyph.FlatRotation);
                if (glyph.Node.Rotation != rotation) glyph.Node.Rotation = rotation;
            }
        }
    }

    private void ReprojectWorldDecorations()
    {
        if (_flatMap)
        {
            _forceFlatReproject = true;
            UpdateFlatWorldPositions();
            SnapArmyMarkerPositions();
            return;
        }

        var glyphBias = _reliefDisplacement > 0.0001f ? 0.006f : 0f;
        foreach (var (node, normal) in _capitalMarkers)
            if (IsInstanceValid(node)) PlaceCapitalMarker(node, normal);
        foreach (var (node, normal) in _fleetMarkers)
            if (IsInstanceValid(node)) node.Position = LocalSurfacePointFromNormal(normal, SurfaceMarkerRadius);
        ReprojectSupportMarkers();
        SnapArmyMarkerPositions();
        foreach (var glyph in _countryGlyphs)
        {
            if (!IsInstanceValid(glyph.Node)) continue;
            glyph.Node.Position = LocalSurfaceLabelPoint(glyph.Latitude, glyph.Longitude, SurfaceTextRadius + glyphBias, glyph.FootprintRadius);
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
    public Nation MapModeSubject
    {
        get
        {
            if (!TryBeginActionRead(out var campaign, 25)) return null;
            try { return campaign.NationById(_mapModeSubjectId) ?? campaign.Player; }
            finally { campaign.ExitRead(); }
        }
    }

    public void SetMapModeSubject(Nation nation)
    {
        Nation subject;
        if (_campaign is { } campaign)
        {
            if (!campaign.TryEnterRead(25, true)) return;
            try
            {
                var id = nation is null || nation == campaign.Player ? -1 : nation.Id;
                if (id == _mapModeSubjectId) return;
                _mapModeSubjectId = id;
                if (_mapMode is StrategicMapMode.Diplomatic or StrategicMapMode.Wars or StrategicMapMode.Tension) RefreshMapModePalette();
                subject = campaign.NationById(_mapModeSubjectId) ?? campaign.Player;
            }
            finally { campaign.ExitRead(); }
        }
        else
        {
            if (_mapModeSubjectId == -1) return;
            _mapModeSubjectId = -1;
            subject = null;
        }
        MapModeSubjectChanged?.Invoke(subject);
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
        if (!_campaign.TryEnterRead(25, true))
        {
            _lastMapModeDay = -100;
            return;
        }
        try { RefreshMapModePaletteCore(); }
        finally { _campaign.ExitRead(); }
    }

    private void RefreshMapModePaletteCore()
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
        if (_mapMode == StrategicMapMode.Religion)
        {
            RefreshReligionPalette();
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
            {
                if (nation == player) return Ui.Colour("map.war_self");
                foreach (var candidate in _campaign.Wars)
                {
                    if (candidate.Ended || !candidate.Involves(nation.Id)) continue;
                    if (candidate.AttackerId == nation.Id) return Ui.Colour("map.war_attacker");
                    if (candidate.DefenderId == nation.Id) return Ui.Colour("map.war_defender");
                    return Ui.Colour("map.war_bystander");
                }
                return Ui.Colour("map.neutral");
            }
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
    /// <summary>The religion layer's legend, as last drawn: families by their followers on the
    /// map, each followed by its denominations. Built with the palette, read by the guide.</summary>
    private static (string Label, Color Colour)[] _religionLegend = Array.Empty<(string, Color)>();

    /// <summary>
    /// Every region in the colour of its largest denomination - a Lutheran north and a Baptist
    /// south in two blues that both read as Protestant. Read from the census the population
    /// screen draws, divided by history/faiths; it costs the simulation nothing.
    /// </summary>
    private void RefreshReligionPalette()
    {
        var followers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        RefreshRegionalPalette(state =>
        {
            var held = DemographyCatalog.DenominationsIn(state, state.NationId);
            if (held.Count == 0) return Ui.Colour("map.neutral");
            foreach (var (denomination, size) in held)
                followers[denomination] = followers.GetValueOrDefault(denomination) + size;
            return ReligionColour(held[0].Denomination);
        });

        // Grouped by family, the largest families first; a few of each, so the guide stays a guide.
        var families = followers
            .GroupBy(entry => DemographyCatalog.FamilyOf(entry.Key), StringComparer.OrdinalIgnoreCase)
            .Select(family => (Family: family.Key, Total: family.Sum(entry => entry.Value),
                Members: family.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal).ToList()))
            .OrderByDescending(family => family.Total).ThenBy(family => family.Family, StringComparer.Ordinal)
            .Take(8);
        var legend = new List<(string, Color)>();
        foreach (var (family, _, members) in families)
            foreach (var (denomination, _) in members.Take(4))
                legend.Add((string.Equals(denomination, family, StringComparison.OrdinalIgnoreCase)
                        ? DemographyCatalog.ReligionName(family)
                        : Loc.Format("{0} · {1}", DemographyCatalog.ReligionName(denomination), DemographyCatalog.ReligionName(family)),
                    ReligionColour(denomination)));
        _religionLegend = legend.ToArray();
    }

    private static Color ReligionColour(string id)
    {
        var hex = DemographyCatalog.Religion(id)?.Colour;
        return !string.IsNullOrWhiteSpace(hex) && Color.HtmlIsValid(hex) ? Color.FromHtml(hex) : Ui.Colour("map.neutral");
    }

    private readonly Dictionary<int, Color> _regionalByStateScratch = new();

    private void RefreshRegionalPalette(Func<ScenarioState, Color> colour)
    {
        var pixels = _resourcePixels ??= new byte[256 * 256 * 4];
        Array.Clear(pixels);
        var map = _campaign.ProvinceMap;
        if (map is not null)
        {
            if (_nationByMapIndex.Count < _campaign.Nations.Count)
            {
                _nationByMapIndex.Clear();
                foreach (var n in _campaign.Nations) _nationByMapIndex[n.MapIndex] = n;
            }
            var byRegion = _regionalByStateScratch;
            byRegion.Clear();
            var hidden = Ui.Colour("map.neutral");
            foreach (var state in _campaign.States)
            {
                var owned = state.NationMapIndex != 0 && _nationByMapIndex.ContainsKey(state.NationMapIndex);
                // Every country's: how settled a region is, and how restless, is the news, not a
                // secret - the dossier of every country already says it.
                byRegion[state.MapIndex] = owned ? colour(state) : hidden;
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
        if (_resourceImage is null) _resourceImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _resourceImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("resource_colors", _resourceImage, ref _resourceTexture);
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
