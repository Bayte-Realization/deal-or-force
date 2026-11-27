using static DealOrForce.TerritoryLabelLayout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using DealOrForce.Simulation;
using SourceEarthData = DealOrForce.Simulation.WorldMapSource;

namespace DealOrForce;

public readonly record struct GlobePick(
    Simulation.Nation Nation,
    Simulation.ScenarioState State,
    int ProvinceId,
    float Latitude,
    float Longitude
)
{
    internal Vector2? PointerPosition { get; init; }
}

/// <summary>
/// Godot rendering vertical slice of the original globe.  It uses the exact bundled
/// political rasters from s&box (control + state layers) and the original water/space
/// textures.  The raster work happens off the rendering thread; only a finished
/// ImageTexture is handed to Godot's renderer.
/// </summary>
public sealed partial class GlobePresentation : SubViewportContainer
{
    // Exact s&box base-map resolution: one output pixel per baked Earth cell.
    private const int MapWidth = 5760;
    private const int MapHeight = 2880;
    /// <summary>The colour plate is shown at a third of the id rasters. Borders stay the darkest
    /// pixel of each block, so the main thread never Lanczos-resizes sixteen million pixels.</summary>
    private const int PoliticalScale = 3;
    private const float GlobeRadius = 5f;
    // One common, almost flush overlay shell; physical 3D relief samples the actual
    // terrain heightmap at each overlay's geographic coordinate so coastal/lowland points
    // sit flush on the coast while mountain cities, divisions, and front lines climb peaks.
    private byte[] _reliefHeightGrid;
    private int _reliefHeightWidth;
    private int _reliefHeightHeight;
    private float _reliefDisplacement;
    private float _reliefOceanDepth;
    private ImageTexture _reliefHeightTexture;
    private bool? _cachedReliefMeshEnabled;
    private float _cachedReliefMeshTier = -1f;
    private const float SurfaceLineRadius = GlobeRadius * 1.0012f;
    private const float SurfaceMarkerRadius = GlobeRadius * 1.0022f;
    private const float SurfaceTextRadius = GlobeRadius * 1.0010f;
    private const float FlatLineZ = 0.004f;
    private const float FlatMarkerZ = 0.007f;
    private const float FlatTextZ = 0.009f;

    private void ApplyReliefMeshSubdivision(bool enabled, float tier)
    {
        if (_cachedReliefMeshEnabled == enabled && Mathf.IsEqualApprox(_cachedReliefMeshTier, tier))
            return;
        _cachedReliefMeshEnabled = enabled;
        _cachedReliefMeshTier = tier;

        var high = enabled && tier < 0.5f;
        var medium = enabled && !high && tier < 1.5f;
        if (IsInstanceValid(_globeNode) && _globeNode.Mesh is SphereMesh sphere)
        {
            var targetRadial = high ? 768 : medium ? 384 : 256;
            var targetRings = high ? 384 : medium ? 192 : 128;
            if (sphere.RadialSegments != targetRadial) sphere.RadialSegments = targetRadial;
            if (sphere.Rings != targetRings) sphere.Rings = targetRings;
        }
        if (_flatQuads is not null)
        {
            var subW = high ? 512 : medium ? 256 : 0;
            var subD = high ? 256 : medium ? 128 : 0;
            for (var i = 0; i < _flatQuads.Length; i++)
            {
                var quad = _flatQuads[i];
                if (quad is null) continue;
                if (quad.SubdivideWidth != subW) quad.SubdivideWidth = subW;
                if (quad.SubdivideDepth != subD) quad.SubdivideDepth = subD;
            }
        }
    }

    private static float SmoothStepGlsl(float edge0, float edge1, float x)
    {
        var denom = edge1 - edge0;
        var t = MathF.Abs(denom) < 1e-6f
            ? (x >= edge1 ? 1f : 0f)
            : Math.Clamp((x - edge0) / denom, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private float SampleReliefRaw(float u, float v)
    {
        var grid = _reliefHeightGrid;
        var w = _reliefHeightWidth;
        var h = _reliefHeightHeight;
        if (grid is null || w <= 0 || h <= 0) return 0f;
        u -= MathF.Floor(u);
        v = Math.Clamp(v, 0f, 1f);
        var fx = u * (w - 1);
        var fy = v * (h - 1);
        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = x0 + 1 < w ? x0 + 1 : 0;
        var y1 = y0 + 1 < h ? y0 + 1 : y0;
        var tx = fx - x0;
        var ty = fy - y0;
        var s00 = grid[y0 * w + x0];
        var s10 = grid[y0 * w + x1];
        var s01 = grid[y1 * w + x0];
        var s11 = grid[y1 * w + x1];
        var top = s00 + (s10 - s00) * tx;
        var bot = s01 + (s11 - s01) * tx;
        return (top + (bot - top) * ty) / 255f;
    }

    private float ReliefLiftFromHeight(float h, float v, bool flat)
    {
        var hLand = MathF.Max(0f, h - 0.015f) / 0.985f;
        if (hLand <= 0f) return 0f;
        var vClamped = Math.Clamp(v, 0f, 1f);
        var poleFade = flat ? 1f : SmoothStepGlsl(0.495f, 0.435f, MathF.Abs(vClamped - 0.5f));
        var projScale = flat ? 1.95f : 1.0f;
        return MathF.Pow(hLand, 0.85f) * _reliefDisplacement * poleFade * projScale;
    }

    private float LocalReliefLift(float latitude, float longitude, bool flat)
    {
        if (_reliefDisplacement <= 0.0001f || _reliefHeightGrid is null || _reliefHeightWidth <= 0 || _reliefHeightHeight <= 0)
            return 0f;
        var u = (longitude + 180f) / 360f;
        var v = (90f - latitude) / 180f;
        var hCenter = SampleReliefRaw(u, v);
        // Tiny half-triangle neighborhood keeps markers on steep slopes clear of chord faces
        // while keeping sea level and coastal plains strictly at 0.
        var du = 0.5f / _reliefHeightWidth;
        var dv = 0.5f / _reliefHeightHeight;
        var hNb = MathF.Max(
            MathF.Max(SampleReliefRaw(u + du, v), SampleReliefRaw(u - du, v)),
            MathF.Max(SampleReliefRaw(u, v + dv), SampleReliefRaw(u, v - dv)));
        var h = MathF.Max(hCenter, hCenter + (hNb - hCenter) * 0.45f);
        return ReliefLiftFromHeight(h, v, flat);
    }

    /// <summary>
    /// Peak terrain relief across a label's physical footprint on the globe or flat map,
    /// plus spherical tangent-quad sagitta compensation so multi-letter glyphs and offset
    /// labels clear mountain ridges instead of clipping into adjacent peaks.
    /// </summary>
    private float MaxReliefLiftAround(float latitude, float longitude, float radiusWorldUnits, bool flat)
    {
        if (_reliefDisplacement <= 0.0001f || _reliefHeightGrid is null || _reliefHeightWidth <= 0 || _reliefHeightHeight <= 0)
            return 0f;
        if (radiusWorldUnits <= 0f)
            return LocalReliefLift(latitude, longitude, flat);

        var dLatDeg = Math.Clamp(radiusWorldUnits / GlobeRadius * (180f / MathF.PI), 0.12f, 4.5f);
        var cosLat = MathF.Max(0.22f, MathF.Cos(Mathf.DegToRad(latitude)));
        var dLonDeg = Math.Clamp(dLatDeg / cosLat, 0.12f, 8.0f);

        var uniformPoleFade = flat || MathF.Abs(latitude) + dLatDeg <= 78.3f;
        var maxH = 0f;
        var maxLift = 0f;
        for (var iy = -2; iy <= 2; iy++)
        {
            var sLat = Math.Clamp(latitude + iy * 0.5f * dLatDeg, -89.5f, 89.5f);
            var v = (90f - sLat) / 180f;
            for (var ix = -2; ix <= 2; ix++)
            {
                var sLon = longitude + ix * 0.5f * dLonDeg;
                var sample = SampleReliefRaw((sLon + 180f) / 360f, v);
                if (uniformPoleFade)
                {
                    if (sample > maxH) maxH = sample;
                }
                else
                {
                    var lift = ReliefLiftFromHeight(sample, v, flat);
                    if (lift > maxLift) maxLift = lift;
                }
            }
        }
        if (uniformPoleFade)
            maxLift = ReliefLiftFromHeight(maxH, (90f - latitude) / 180f, flat);

        var sagitta = flat ? 0f : (radiusWorldUnits * radiusWorldUnits) / (2f * GlobeRadius);
        return maxLift + sagitta;
    }

    private float LocalGlobeRadius(float latitude, float longitude, float baseRadius) =>
        baseRadius + LocalReliefLift(latitude, longitude, flat: false);

    private float LocalFlatZ(float latitude, float longitude, float baseZ) =>
        baseZ + LocalReliefLift(latitude, longitude, flat: true);

    private Vector3 LocalSurfacePoint(float latitude, float longitude, float baseRadius) =>
        SurfacePoint(latitude, longitude, LocalGlobeRadius(latitude, longitude, baseRadius));

    private Vector3 LocalFlatPoint(float latitude, float longitude, float baseZ) =>
        FlatPoint(latitude, longitude, LocalFlatZ(latitude, longitude, baseZ));

    private Vector3 LocalSurfaceLabelPoint(float latitude, float longitude, float baseRadius, float footprintRadius) =>
        SurfacePoint(latitude, longitude, baseRadius + MaxReliefLiftAround(latitude, longitude, footprintRadius, flat: false));

    private Vector3 LocalFlatLabelPoint(float latitude, float longitude, float baseZ, float footprintRadius) =>
        FlatPoint(latitude, longitude, baseZ + MaxReliefLiftAround(latitude, longitude, footprintRadius, flat: true));

    private Vector3 LocalSurfacePointFromNormal(Vector3 normal, float baseRadius)
    {
        if (normal.LengthSquared() < 1e-8f) return Vector3.Zero;
        var unit = normal.Normalized();
        if (_reliefDisplacement <= 0.0001f || _reliefHeightGrid is null)
            return unit * baseRadius;
        var geo = GeographicFromNormal(unit);
        return unit * LocalGlobeRadius(geo.Latitude, geo.Longitude, baseRadius);
    }

    private Vector3 LocalFlatPointFromWorld(Vector3 point, float baseZ)
    {
        if (_reliefDisplacement <= 0.0001f || _reliefHeightGrid is null)
            return point with { Z = baseZ };
        var lon = WrappedFlatX(point.X) / FlatMapWidth * 360f;
        var lat = Math.Clamp(point.Y / FlatMapHeight * 180f, -89.9f, 89.9f);
        return new Vector3(point.X, point.Y, baseZ + LocalReliefLift(lat, lon, flat: true));
    }

    private void ApplyReliefState(float displacement, float oceanDepth)
    {
        var changed = !Mathf.IsEqualApprox(_reliefDisplacement, displacement) ||
                      !Mathf.IsEqualApprox(_reliefOceanDepth, oceanDepth);
        _reliefDisplacement = displacement;
        _reliefOceanDepth = oceanDepth;
        SyncWeatherReliefShells();
        if (!changed) return;
        _cityDirty = true;
        _settlementClock = 1.0;
        if (!_mapApplied) return;
        _routeFingerprint = int.MinValue;
        _fleetRouteFingerprint = int.MinValue;
        _tradeLaneFingerprint = int.MinValue;
        ReprojectWorldDecorations();
        ReprojectSupportMarkers(true);
        BuildFrontPreview();
    }
    /// <summary>
    /// How close the camera may come to a globe whose radius is five.
    ///
    /// Six point two left it a fifth of the planet's radius above the ground, which is
    /// fine for looking at a war and useless for conducting one: a province was a few
    /// pixels across and the counter standing on it covered its neighbours. A quarter of a
    /// unit is close enough that a province is a place rather than a dot, and the near
    /// plane is five hundredths, so there is room to spare.
    /// </summary>
    private const float MinimumDistance = 5.25f;
    private const float MaximumDistance = 24f;

    /// <summary>
    /// Historical billboard constants still used by fleet and support presentation.
    /// Division cards now use a shared tangent-plane transform and bounded physical
    /// footprint in SurfaceArmyCards; they no longer use shader fixed-size compensation.
    /// </summary>
    private const float CounterPlatePixelSize = 0.00016f;
    private const float CounterTextPixelSize = 0.00025f;
    private const int CounterOutlineSize = 6;
    private const float CounterCloseScale = 1.35f;

    /// <summary>Above this the counters are simply their normal size.</summary>
    private const float CounterGrowthDistance = 6.6f;

    private readonly Dictionary<int, uint> _colorByMapIndex = new();
    private readonly Dictionary<int, Simulation.Nation> _nationByMapIndex = new();
    private readonly Dictionary<int, Simulation.ScenarioState> _stateByMapIndex = new();

    /// <summary>
    /// Which revision of the map the region index was built for.
    ///
    /// The list of regions is no longer fixed at load: a border cut through one makes two,
    /// and a border that goes away makes one again. An index built once and never looked at
    /// again cannot find a region the campaign created this afternoon, and the panel simply
    /// says nothing about the ground under the cursor.
    /// </summary>
    private int _stateIndexRevision = -1;
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private Node3D _orbit = null!;
    private ShaderMaterial _globeMaterial = null!;
    private ShaderMaterial _cachedOwnersMovedMaterial;
    private bool? _cachedOwnersMoved;
    private Node3D _capitalRoot = null!;
    private Texture2D _capitalTexture = null!;
    private Node3D _armyRoot = null!;
    private Node3D _fleetRoot = null!;
    private Texture2D _fleetTexture = null!;
    private Task<MapBuildResult> _mapTask;
    private MapBuildResult _pendingMapResult;
    private bool _mapFailureReported;
    private int _mapApplyStage;
    private volatile string _mapWorkerStage = "Reading province and terrain maps";
    private volatile int _mapWorkerProgress = 42;
    private long _mapUploadStartTicks;
    private double _militaryVisualAccumulator;
    private double _routeVisualAccumulator;
    private double _visualClock;
    private int _visualPhase;
    private readonly double[] _visualPhaseAccum = new double[4];
    private Simulation.ProvinceTopology _provinceTopology;

    internal bool GlobeViewInMotion =>
        _dragging ||
        _orbitGlide.LengthSquared() > 0.0025f ||
        MathF.Abs(_distance - _targetDistance) > 0.04f ||
        (_touchAdapter?.IsTouchActive == true);

    /// <summary>The provinces of the map this globe has built, or 0 before it has one.</summary>
    public int ProvinceCount => _provinceTopology?.ProvinceCount ?? 0;
    private Simulation.Campaign _campaign;
    private bool _mapApplied;
    public bool IsMapPresentationReady => _mapApplied;
    public bool IsTurboSimThrottling => _turboThrottlingActive;
    private bool? _cachedFlatMapBorder;
    private float _cachedDetailMix = -1f;
    private float _cachedProvinceDetail = -1f;
    private int _cachedFogViewerParam = -999;
    private double _pendingWorldDelta;
    private double _sinceLastFencedSync;
    private double _labelRefreshTimer;
    private string _cachedHoverTooltip = "";
    private bool _lastCounterScaleFlatMap;
    private float _lastCounterScaleDistance = -1f;
    private float _lastCounterScaleFlatZoom = -1f;
    private ulong _fencedTimersAccumFrame = ulong.MaxValue;
    private ulong _lastFencedSyncFrame = ulong.MaxValue;
    private ulong _cameraAdvancedFrame = ulong.MaxValue;
    private Simulation.Campaign _syncedCampaign;
    private int _frontWarRevision = -1;
    private long _lastCombatSyncHours = -1;

    private void ResetRevisionTrackingIfCampaignChanged(Simulation.Campaign campaign)
    {
        if (ReferenceEquals(_syncedCampaign, campaign)) return;
        _syncedCampaign = campaign;
        _controlRevision = -1;
        _frontRevision = -1;
        _frontWarRevision = -1;
        _lastCombatSyncHours = -1;
    }

    internal bool NeedsFencedSync(bool fastSim)
    {
        if (_campaign is null) return false;
        ResetRevisionTrackingIfCampaignChanged(_campaign);
        var isMobileOrTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true ||
                              Platform.PlatformProfile.Current?.IsMobile == true;
        var snap = CurrentRenderSnapshot();
        var mapBehind = snap is { Version: > 0 } &&
                        (snap.MapRevision > _controlRevision || snap.MapRevision > _frontRevision || snap.WarRevision > _frontWarRevision);
        var hoursBehind = snap is { Version: > 0 } && snap.ElapsedHours > _lastCombatSyncHours;
        var syncCap = isMobileOrTouch
            ? (fastSim ? 0.16 : 0.045)
            : (fastSim ? 0.033 : 0.016);
        return !_mapApplied || _controlRevision < 0 || _routeFingerprint == int.MinValue ||
               mapBehind || (!isMobileOrTouch && hoursBehind) || _sinceLastFencedSync >= syncCap;
    }

    internal bool NeedsUrgentFencedSync(bool fastSim)
    {
        if (_campaign is null) return false;
        ResetRevisionTrackingIfCampaignChanged(_campaign);
        var isMobileOrTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true ||
                              Platform.PlatformProfile.Current?.IsMobile == true;
        var snap = CurrentRenderSnapshot();
        var mapBehind = snap is { Version: > 0 } &&
                        (snap.MapRevision > _controlRevision || snap.MapRevision > _frontRevision || snap.WarRevision > _frontWarRevision);
        var hoursBehind = snap is { Version: > 0 } && snap.ElapsedHours > _lastCombatSyncHours;
        var syncCap = isMobileOrTouch
            ? (fastSim ? 0.16 : 0.045)
            : (fastSim ? 0.033 : 0.016);
        return !_mapApplied || _controlRevision < 0 || _routeFingerprint == int.MinValue ||
               (!isMobileOrTouch && (mapBehind || (hoursBehind && _sinceLastFencedSync >= 0.033))) ||
               _sinceLastFencedSync >= syncCap;
    }

    internal void SyncWorldVisualsUnderReadLock(Simulation.Campaign fencedCampaign, double delta)
    {
        if (fencedCampaign is null || !ReferenceEquals(fencedCampaign, _campaign)) return;
        var frame = Engine.GetProcessFrames();
        if (_lastFencedSyncFrame == frame) return;
        AdvanceCameraForFrame(delta);
        AccumulateFencedSyncTimers(delta);
        var snap = CurrentRenderSnapshot();
        var fastSim = snap is { Version: > 0 }
            ? snap is { IsPaused: false, Speed: >= 4 }
            : fencedCampaign is { IsPaused: false, Speed: >= 4 };
        SyncFencedWorldVisuals(snap, fencedCampaign, fastSim);
        _lastFencedSyncFrame = frame;
    }

    private void AccumulateFencedSyncTimers(double delta)
    {
        var frame = Engine.GetProcessFrames();
        if (_fencedTimersAccumFrame == frame) return;
        _fencedTimersAccumFrame = frame;
        _pendingWorldDelta += delta;
        _sinceLastFencedSync += delta;
        _visualClock += delta;
        for (var p = 0; p < 4; p++) _visualPhaseAccum[p] += delta;
        _militaryVisualAccumulator += delta;
        _routeVisualAccumulator += delta;
        _controllerUploadTimer += delta;
    }

    private bool TryBeginFrameRead(bool fastSim, out Simulation.Campaign campaign)
    {
        campaign = _campaign;
        if (campaign is null) return true;
        ResetRevisionTrackingIfCampaignChanged(campaign);
        var isMobileOrTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true ||
                              Platform.PlatformProfile.Current?.IsMobile == true;
        var snap = CurrentRenderSnapshot();
        var mapBehind = snap is { Version: > 0 } &&
                        (snap.MapRevision > _controlRevision || snap.MapRevision > _frontRevision || snap.WarRevision > _frontWarRevision);
        var syncCap = isMobileOrTouch
            ? (fastSim ? 0.16 : 0.045)
            : (fastSim ? 0.033 : 0.016);
        var wantWindow = !_mapApplied || _controlRevision < 0 || _routeFingerprint == int.MinValue ||
                         (!isMobileOrTouch && mapBehind && _sinceLastFencedSync >= 0.033) ||
                         _sinceLastFencedSync >= syncCap;
        return campaign.TryEnterRead(0, wantWindow);
    }

    private bool TryBeginActionRead(out Simulation.Campaign campaign, int timeoutMs = 150)
    {
        campaign = _campaign;
        return campaign is not null && campaign.TryEnterRead(timeoutMs, true);
    }

    /// <summary>
    /// A read for an interactive action (a tap, a long press): the background worker preempts at
    /// the next stage boundary and stays off the gate while this waits.
    /// </summary>
    private bool TryBeginPriorityActionRead(out Simulation.Campaign campaign, int timeoutMs = 45)
    {
        campaign = _campaign;
        return campaign is not null && campaign.AccessGate.TryEnterPriorityRead(timeoutMs);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationExitTree)
        {
            // A queued tap holds a priority request on the gate; never leave one behind.
            CancelPendingMapSelection();
            _lastSelectedRegionLive = int.MinValue;
            _lastSelectedRegionCampaign = null;
            _lastSelectedRegionMap = null;
        }
    }

    private Simulation.CampaignRenderSnapshot CurrentRenderSnapshot() =>
        _campaign?.RenderSnapshot ?? Simulation.CampaignRenderSnapshot.Empty;

    public event Action<GlobePick> CountryPicked;
    public bool CountrySelectionOnly { get; set; }
    public event Action<Simulation.Army> ArmyPicked;
    public event Action<Simulation.Fleet> FleetPicked;
    public event Action<Simulation.SupportFormation> SupportPicked;
    public event Action<Simulation.LandBattle> BattlePicked;
    public event Action<IReadOnlyList<Simulation.Army>> ArmiesBoxPicked;
    /// <summary>A box drawn over fleets and no divisions: the player's own fleets inside it.</summary>
    public event Action<IReadOnlyList<Simulation.Fleet>> FleetsBoxPicked;
    /// <summary>
    /// A Ctrl-drawn box over the map, as the provinces under it - an area picked for one order
    /// to all of it (a building in every region), the way Age of History's selection tool works.
    /// </summary>
    public event Action<IReadOnlyList<int>> ProvincesBoxSelected;
    public event Action<GlobePick> ProvinceCommanded;
    public event Action EmptyMapClicked;
    public event Action<GlobePick> AltProvinceSplitCommanded;

    partial void SyncCameraLockedMapOverlays(double delta, Simulation.CampaignRenderSnapshot snap);

    private bool _turboRenderThisFrame = true;

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventScreenTouch or InputEventScreenDrag or InputEventMagnifyGesture or InputEventPanGesture or InputEventMouseButton)
            WakeGlobeRendering();
    }

    internal void AdvanceCameraForFrame(double delta)
    {
        if (!IsInsideTree()) return;
        var frame = Engine.GetProcessFrames();
        if (_cameraAdvancedFrame == frame) return;
        _cameraAdvancedFrame = frame;

        var snap = CurrentRenderSnapshot();
        var isPaused = snap is { Version: > 0 } ? snap.IsPaused : (_campaign?.IsPaused ?? false);
        var speed = snap is { Version: > 0 } ? snap.Speed : (_campaign?.Speed ?? 1);

        ObservePreCameraTurboWakeState(isPaused, speed);
        UpdateCrossoverProjection();
        var inputBlocked = LoadingInputBlocked || MediaLibraryInputBlocked || MapLayerDrawerInputBlocked;
        TickTouchCamera(delta, inputBlocked);
        if (!inputBlocked && !_freeCamera)
        {
            UpdateOrbitCamera(delta);
        }
        else
        {
            if (!inputBlocked) UpdateFreeCamera(delta);
            _isActivelyPanning = false;
        }
        // Picking, density and city layout must see the same projected positions that
        // will be rendered this frame, including wrapping at the date line.
        if (_flatMap) UpdateFlatWorldPositions();
        RetryMapSelection(inputBlocked);
    }

    public override void _Process(double delta)
    {
        var snap = CurrentRenderSnapshot();
        var fastSim = snap is { Version: > 0 }
            ? snap is { IsPaused: false, Speed: >= 4 }
            : _campaign is { IsPaused: false, Speed: >= 4 };

        AdvanceCameraForFrame(delta);

        _turboRenderThisFrame = UpdateViewportRenderCadence(delta, fastSim);
        _turboThrottledSkyDelta += delta;
        if (_turboRenderThisFrame)
        {
            var skyDelta = Math.Min(_turboThrottledSkyDelta, 0.25);
            _turboThrottledSkyDelta = 0.0;
            UpdateSky(skyDelta);
        }
        UpdateShaderZoomAndSurfaceState(delta);
        SyncCameraLockedMapOverlays(delta, snap);

        // Smooth counter scaling directly on camera distance/zoom changes instead of stepping at 120-180ms visual phases.
        if (_mapApplied &&
            (_flatMap != _lastCounterScaleFlatMap ||
             (_flatMap ? Mathf.Abs(_flatZoom - _lastCounterScaleFlatZoom) > 1e-4f
                       : Mathf.Abs(ViewDistance - _lastCounterScaleDistance) > 1e-4f)))
        {
            _lastCounterScaleFlatMap = _flatMap;
            _lastCounterScaleDistance = ViewDistance;
            _lastCounterScaleFlatZoom = _flatZoom;
            UpdateCounterScale();
        }

        // Counters glide every frame, on the visual cadence nothing else about them runs at.
        // This is the one thing in the frame loop that has to be continuous for the map to
        // read as a place rather than a flipbook. On 3D Turbo Sim throttled frames, accumulate
        // army delta until the 3D SubViewport redraws; flat 2D counters draw at full 60 Hz.
        if (MilitaryMarkersEnabled && _mapApplied)
        {
            _turboThrottledArmyDelta += (float)delta;
            if (_turboRenderThisFrame || _flatMap)
            {
                var armyDelta = MathF.Min(_turboThrottledArmyDelta, 0.25f);
                _turboThrottledArmyDelta = 0f;
                using (Simulation.SimProfiler.Measure("f:army motion")) UpdateArmyMarkerMotion(armyDelta);
            }
        }

        // Accumulate wall time outside the read fence so frames skipped while DoF-Sim is mid-tick
        // catch up cleanly on the very next step-boundary read window without stalling 60 FPS.
        AccumulateFencedSyncTimers(delta);

        var processFrame = Engine.GetProcessFrames();
        if (_lastFencedSyncFrame != processFrame && TryBeginFrameRead(fastSim, out var fencedCampaign))
        {
            try
            {
                SyncFencedWorldVisuals(snap, fencedCampaign, fastSim);
                _lastFencedSyncFrame = processFrame;
            }
            finally
            {
                if (fencedCampaign is not null) fencedCampaign.ExitRead();
            }
        }

        if (!_mapApplied && _pendingMapResult is null && _mapTask is { IsCompletedSuccessfully: true })
        {
            _pendingMapResult = _mapTask.Result;
            _mapApplyStage = 0;
        }
        else if (!_mapApplied && !_mapFailureReported && _mapTask is { IsFaulted: true })
        {
            _mapFailureReported = true;
            var reason = _mapTask.Exception?.GetBaseException().Message ?? "unknown map-data error";
            MapLoadingError = reason;
            GD.PushError($"[dof] authored province map is required: {reason}");
        }
        if (!_mapApplied && !_mapFailureReported && _pendingMapResult is not null)
        {
            try { ApplyPoliticalMapStage(); }
            catch (Exception exception)
            {
                _mapFailureReported = true;
                MapLoadingError = exception.Message;
                GD.PushError($"[dof] map upload failed: {exception}");
            }
        }
    }

    private void UpdateShaderZoomAndSurfaceState(double delta)
    {
        // Keep the authored political raster legible at strategic distance. The old
        // quarter-resolution layer supplied almost forty percent of the default globe,
        // which made country silhouettes and coastlines look out of focus. A small
        // mipmapped contribution remains to suppress shimmer while the full authored
        // layer carries the actual line work.
        var detailMix = _flatMap
            ? Mathf.Clamp((13f - _flatZoom) / 4.8f, 0.15f, 1f)
            : Mathf.Clamp((17f - _distance) / 5.5f, 0.12f, 1f);
        var provinceDetail = _flatMap
            ? 1f - Mathf.SmoothStep(
                Math.Clamp(Simulation.InterfaceSkin.Metric("map.flat_province_full_zoom", 0.95f), MinimumFlatZoom, 3f),
                Math.Clamp(Simulation.InterfaceSkin.Metric("map.flat_province_hide_zoom", 3.8f), 3.1f, MaximumFlatZoom), _flatZoom)
            : Mathf.Clamp((9.3f - _distance) / 2.8f, 0f, 1f);
        if (Mathf.Abs(detailMix - _cachedDetailMix) > 0.002f)
        {
            _cachedDetailMix = detailMix;
            _globeMaterial?.SetShaderParameter("detail_mix", detailMix);
        }
        // Tactical boundaries should emerge only at operational zoom. Keeping them
        // faint at country-selection scale avoids turning the map into a crosshatch.
        if (Mathf.Abs(provinceDetail - _cachedProvinceDetail) > 0.002f)
        {
            _cachedProvinceDetail = provinceDetail;
            _globeMaterial?.SetShaderParameter("province_detail", provinceDetail);
        }
        // The far regime of the atlas look: the map becomes a sheet of paper. One number from
        // the camera, sent only when it moves (GlobeMapLook).
        UpdatePaperMix();
        if (_cachedFlatMapBorder != _flatMap)
        {
            _cachedFlatMapBorder = _flatMap;
            _globeMaterial?.SetShaderParameter("front_pixels", Math.Clamp(Ui.Metric("map.front_width_pixels", 1.6f), .75f, 3f));
            ApplyMapBorderStyle();
        }
        UpdateIce(delta);
    }

    private bool AnyRegionBadgeActive()
    {
        if (_regionBadges.Count == 0) return false;
        foreach (var badge in _regionBadges.Values)
            if (badge.Alpha > 0f) return true;
        return false;
    }

    private void SyncFencedWorldVisuals(Simulation.CampaignRenderSnapshot snap, Simulation.Campaign fencedCampaign, bool fastSim)
    {
        _sinceLastFencedSync = 0;
        var heavyGpuUploadedThisFrame = PollPlateRepaint();
        var fogViewer = FogOfWarActive && fencedCampaign?.Player is { IsHuman: true } ? fencedCampaign.Player.Id : -1;
        if (_fogViewer != fogViewer) { _fogViewer = fogViewer; _lastMapModeDay = -100; }
        var fogViewerParam = FogOfWarActive && fencedCampaign?.Player is { IsHuman: true } ? fencedCampaign.Player.MapIndex : -1;
        if (_cachedFogViewerParam != fogViewerParam)
        {
            _cachedFogViewerParam = fogViewerParam;
            _globeMaterial?.SetShaderParameter("fog_viewer", fogViewerParam);
        }
        UpdateFogTint();
        if (FogOfWarActive && _mapApplied && fencedCampaign?.Player is { IsHuman: true } && fencedCampaign.ProvinceMap is not null &&
            (!heavyGpuUploadedThisFrame || _uploadedVisionRevision < 0))
            UpdateFogVisibility();
        var syncDelta = Math.Min(_pendingWorldDelta, 0.25);
        _pendingWorldDelta = 0.0;
        _labelRefreshTimer += syncDelta;
        if (heavyGpuUploadedThisFrame)
            WakeGlobeRendering();
        // A frozen world's ships stand where the ice caught them, and its news does not pulse.
        var worldDelta = Simulation.WorldCurses.Frozen(fencedCampaign) ? 0.0 : syncDelta;
        _turboThrottledWeatherDelta += syncDelta;
        _turboThrottledTradeDelta += worldDelta;
        _turboThrottledPulseDelta += worldDelta;
        if (_turboRenderThisFrame || heavyGpuUploadedThisFrame)
        {
            var weatherDt = Math.Min(_turboThrottledWeatherDelta, 0.25);
            var tradeDt = Math.Min(_turboThrottledTradeDelta, 0.25);
            var pulseDt = Math.Min(_turboThrottledPulseDelta, 0.25);
            _turboThrottledWeatherDelta = 0.0;
            _turboThrottledTradeDelta = 0.0;
            _turboThrottledPulseDelta = 0.0;
            UpdateWeather(weatherDt);
            UpdateTradeTraffic(tradeDt);
            UpdateEventPulses(pulseDt);
        }
        UpdateNuclearBlasts();
        KeepAllowedProjection();
        // A capital that moved - by choice, or because its region was ceded - moves its marker.
        if (_mapApplied && fencedCampaign is not null && (_capitalRevision != fencedCampaign.CapitalRevision ||
            _capitalDestroyedCount != fencedCampaign.DestroyedProvinces.Count))
            BuildCapitalMarkers(fencedCampaign);

        // Stagger heavy visual passes across 4 rotating sub-phases so capitals/fleets,
        // army density, cities/settlements, and country/region labels never spike one frame.
        var isTouchPrimary = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var isMobileOrTouch = isTouchPrimary || Platform.PlatformProfile.Current?.IsMobile == true;
        var speed5Running = snap is { Version: > 0 }
            ? snap is { IsPaused: false, Speed: >= 5 }
            : fencedCampaign is { IsPaused: false, Speed: >= 5 };
        var isPausedNow = snap is { Version: > 0 } ? snap.IsPaused : fencedCampaign?.IsPaused == true;
        var phaseInterval = isMobileOrTouch && speed5Running ? 0.085 : (isTouchPrimary ? 0.045 : 0.030);
        if (_visualClock >= phaseInterval)
        {
            _visualClock = 0;
            var phase = _visualPhase;
            _visualPhase = (_visualPhase + 1) & 3;
            var visualStep = Math.Min(_visualPhaseAccum[phase], 0.25);
            _visualPhaseAccum[phase] = 0.0;
            StepStaggeredVisualPhase(phase, visualStep);
        }

        var controllerUploadedThisFrame = false;
        var mapChanged = _mapApplied && fencedCampaign is not null && _controlRevision != fencedCampaign.MapRevision;
        var warChanged = _mapApplied && fencedCampaign is not null && _frontWarRevision != fencedCampaign.WarRevision;
        // Mobile/touch at uncapped Speed 5: keep 220ms owner/controller plate cadence; desktop
        // updates immediately on MapRevision or WarRevision changes without deferring behind plate uploads.
        var uploadInterval = isMobileOrTouch
            ? (speed5Running && _peacePreview.Count == 0 && _lastMapModeDay > -100 ? 0.22 : 0.0)
            : 0.0;
        var gpuGateOk = !isMobileOrTouch || !heavyGpuUploadedThisFrame;
        var uploadDue = _controlRevision < 0 || (gpuGateOk && (uploadInterval <= 0.0 || _controllerUploadTimer >= uploadInterval));
        if (_mapApplied && fencedCampaign is not null && (mapChanged || warChanged) && uploadDue)
        {
            _controllerUploadTimer = 0;
            if (mapChanged)
            {
                controllerUploadedThisFrame = true;
                using (Simulation.SimProfiler.Measure("f:map paint")) RefreshProvinceControllers();
            }
            if (_controlRevision == fencedCampaign.MapRevision || warChanged)
            {
                using (Simulation.SimProfiler.Measure("f:front preview")) BuildFrontPreviewCore();
                _frontRevision = fencedCampaign.MapRevision;
                _frontWarRevision = fencedCampaign.WarRevision;
                _frontPreviewDeferred = false;
            }
        }
        if (_frontPreviewDeferred && gpuGateOk && _mapApplied && fencedCampaign is not null)
        {
            _frontPreviewDeferred = false;
            using (Simulation.SimProfiler.Measure("f:front preview")) BuildFrontPreviewCore();
            _frontRevision = fencedCampaign.MapRevision;
            _frontWarRevision = fencedCampaign.WarRevision;
        }
        if (IsInstanceValid(_frontPreview))
        {
            var showFront = MilitaryMarkersEnabled && _frontMesh.GetSurfaceCount() > 0;
            if (_frontPreview.Visible != showFront) _frontPreview.Visible = showFront;
        }

        var armySyncInterval = isPausedNow
            ? 0.0
            : (isMobileOrTouch
                ? (speed5Running ? 0.25 : fastSim ? 0.12 : 0.10)
                : 0.033);
        var routeSelectionDirty = _routeFingerprint == int.MinValue;
        var routeInterval = isPausedNow ? 0.0 : (isMobileOrTouch && speed5Running ? 0.30 : isTouchPrimary ? 0.22 : 0.033);
        var desktopTopologyMoved = !isMobileOrTouch && (controllerUploadedThisFrame || warChanged);
        var hoursChanged = fencedCampaign is not null && fencedCampaign.ElapsedHours != _lastCombatSyncHours;
        var combatDueDesktop = !isMobileOrTouch &&
            (desktopTopologyMoved || isPausedNow ||
             (hoursChanged && _militaryVisualAccumulator >= armySyncInterval) ||
             _militaryVisualAccumulator >= armySyncInterval);
        var armyDue = isMobileOrTouch ? _militaryVisualAccumulator >= armySyncInterval : combatDueDesktop;
        if (MilitaryMarkersEnabled && _mapApplied && fencedCampaign is not null && armyDue)
        {
            _militaryVisualAccumulator = 0;
            _lastCombatSyncHours = fencedCampaign.ElapsedHours;
            using (Simulation.SimProfiler.Measure("f:army sync")) SyncArmyMarkers();
            using (Simulation.SimProfiler.Measure("f:army look")) UpdateMilitaryPresentation(forceNow: desktopTopologyMoved);
            if (desktopTopologyMoved)
            {
                RefreshArmyMarkerVisibility();
                InvalidateFlatCounterDensity();
            }
            WakeGlobeRendering();
        }
        else if (MilitaryMarkersEnabled && _mapApplied && fencedCampaign is not null && desktopTopologyMoved)
        {
            _lastCombatSyncHours = fencedCampaign.ElapsedHours;
        }
        if (IsInstanceValid(_battleRoot) && _battleRoot.Visible != MilitaryMarkersEnabled)
            _battleRoot.Visible = MilitaryMarkersEnabled;
        var routeDue = routeSelectionDirty || desktopTopologyMoved || _routeVisualAccumulator >= routeInterval;
        if (routeDue)
        {
            _routeVisualAccumulator = 0;
            using (Simulation.SimProfiler.Measure("f:routes")) UpdateRoutePreview();
            using (Simulation.SimProfiler.Measure("f:sealift")) UpdateSeaLiftPreview();
            using (Simulation.SimProfiler.Measure("f:supply lines")) UpdateSupplyLines();
            using (Simulation.SimProfiler.Measure("f:strait crossings")) UpdateStraitCrossings();
            WakeGlobeRendering();
        }
        var labelInterval = isMobileOrTouch ? 0.45 : 0.25;
        if (_mapApplied && fencedCampaign is not null &&
            (_labelTask is not null || (gpuGateOk && _labelRevision != fencedCampaign.MapRevision &&
                                        (_labelRefreshTimer >= labelInterval || isPausedNow))))
        {
            if (_labelTask is null) _labelRefreshTimer = 0.0;
            using (Simulation.SimProfiler.Measure("f:label place")) RefreshCountryLabelPlacement();
        }
        if (RegionIconsWanted || _badgePendingRedraw || AnyRegionBadgeActive())
            KeepRegionIcons(syncDelta);
        if (fencedCampaign is not null && _mapMode is not (StrategicMapMode.Political or StrategicMapMode.Terrain or StrategicMapMode.Relief) &&
            (!controllerUploadedThisFrame && !heavyGpuUploadedThisFrame || _lastMapModeDay <= -100))
            UpdateMapModePaletteIfNeeded();
    }

    private void StepStaggeredVisualPhase(int phase, double visualStep)
    {
        switch (phase)
        {
            case 0:
                UpdateMarkerVisibility(_capitalRoot, _capitalMarkers, ViewDistance <= 13.8f && Simulation.WorldLayers.On("capitals"), CityHorizon());
                if (MilitaryMarkersEnabled)
                    using (Simulation.SimProfiler.Measure("f:fleets")) UpdateFleetMarkers();
                UpdateMarkerVisibility(_fleetRoot, _fleetMarkers, MilitaryMarkersEnabled && (_flatMap || ViewDistance <= 13.5f),
                    _flatMap ? 0f : GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length()) + .002f);
                UpdateSupportPresentation(visualStep);
                break;
            case 1:
                using (Simulation.SimProfiler.Measure("f:army vis"))
                {
                    RefreshArmyMarkerVisibility();
                    UpdateCounterScale();
                }
                if (MilitaryMarkersEnabled)
                {
                    if (_flatMap)
                        using (Simulation.SimProfiler.Measure("f:counter layout")) UpdateFlatCounterDensity();
                    else
                        using (Simulation.SimProfiler.Measure("f:surface layout")) UpdateSurfaceCounterDensity();
                    LayoutSupportCounters();
                }
                break;
            case 2:
                UpdateCityLabels(visualStep);
                if (CurrentShaderQualityTier() < 0.5f)
                    UpdateSettlementModels(visualStep);
                break;
            case 3:
                using (Simulation.SimProfiler.Measure("f:labels"))
                {
                    UpdateCountryLabelVisibility(visualStep);
                    UpdateRegionLabels(visualStep);
                }
                break;
        }
    }

    /// <summary>
    /// GPU uploads are deliberately split across frames during live play, and batched up to a
    /// 12 ms per-frame budget while the loading screen covers the map so boot finishes in 1-2 frames.
    /// </summary>
    private void ApplyPoliticalMapStage()
    {
        var budgetWatch = LoadingInputBlocked ? System.Diagnostics.Stopwatch.StartNew() : null;
        while (!_mapApplied && !_mapFailureReported && _pendingMapResult is not null)
        {
            var stage = _mapApplyStage;
            ApplySinglePoliticalMapStage();
            if (!LoadingInputBlocked || _mapApplied || _pendingMapResult is null || stage >= 5)
                break;
            if (budgetWatch is not null && budgetWatch.Elapsed.TotalMilliseconds >= 12.0)
                break;
        }
    }

    private void ApplySinglePoliticalMapStage()
    {
        var result = _pendingMapResult;
        if (_mapApplyStage == 0) _mapUploadStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        switch (_mapApplyStage++)
        {
            case 0:
            {
                // The worker already painted the plate at its own size, one cell per 3x3 block of
                // the raster. Uploading a full-size plate and Lanczos-shrinking it on this thread
                // was the stall at the start of a campaign.
                using var plate = Image.CreateFromData(MapWidth / PoliticalScale, MapHeight / PoliticalScale,
                    false, Image.Format.Rgba8, result.PoliticalPixels);
                plate.GenerateMipmaps();
                _plateTexture = ImageTexture.CreateFromImage(plate);
                _globeMaterial.SetShaderParameter("political_map", _plateTexture);
                PlateApplied(result.PlateSource, result.PlateOwners);
                result.PoliticalPixels = null;
                _frontierAuthored = FrontierFieldWanted() && Platform.PlatformProfile.Current?.IsTouchPrimary != true
                    ? result.FrontierAuthored : null;
                result.FrontierAuthored = null;
                ApplyFrontier(result.Frontier);
                result.Frontier?.Dispose();
                result.Frontier = null;
                WakeGlobeRendering();
                break;
            }
            case 1:
            {
                if (_packedLookups is null)
                {
                    using var img = Image.CreateFromData(result.GpuMapWidth, result.GpuMapHeight, false, Image.Format.Rg8, result.RegionIds);
                    UploadMapImage("region_ids", img);
                    result.RegionIds = null;
                }
                break;
            }
            case 2:
            {
                if (_packedLookups is null)
                {
                    using var img = Image.CreateFromData(result.GpuMapWidth, result.GpuMapHeight, false, Image.Format.Rg8, result.ProvinceIds);
                    UploadMapImage("province_ids", img);
                    result.ProvinceIds = null;
                }
                break;
            }
            case 3:
            {
                if (_packedLookups is not null && result.RegionIds is not null && result.ProvinceIds is not null)
                {
                    using var regionImg = Image.CreateFromData(result.GpuMapWidth, result.GpuMapHeight, false, Image.Format.Rg8, result.RegionIds);
                    using var provinceImg = Image.CreateFromData(result.GpuMapWidth, result.GpuMapHeight, false, Image.Format.Rg8, result.ProvinceIds);
                    using var nationImg = Image.CreateFromData(result.GpuMapWidth, result.GpuMapHeight, false, Image.Format.Rg8, result.NationIds);
                    UploadPackedIds(regionImg, provinceImg, nationImg);
                    result.RegionIds = null;
                    result.ProvinceIds = null;
                    result.NationIds = null;
                }
                else
                {
                    using var img = Image.CreateFromData(result.GpuMapWidth, result.GpuMapHeight, false, Image.Format.Rg8, result.NationIds);
                    UploadMapImage("nation_ids", img);
                    result.NationIds = null;
                }
                WakeGlobeRendering();
                break;
            }
            case 4:
            {
                _crossoverHydrology = null;
                if (ReducedMap)
                {
                    // The lightweight material has no hydrology/river-centre samplers.
                    // Release worker buffers without allocating invisible GPU copies.
                    result.HydrologyPixels = null;
                    result.RiverCentrePixels = null;
                    break;
                }
                // R8 lake SDF saves 47.5 MiB VRAM vs RGB8 at 5760x2880 (and mobile uploads at 2880x1440).
                using var img = Image.CreateFromData(result.GpuHydrologyWidth, result.GpuHydrologyHeight, false, Image.Format.R8, result.HydrologyPixels);
                _globeMaterial.SetShaderParameter("hydrology_map", ImageTexture.CreateFromImage(img));
                result.HydrologyPixels = null;
                if (result.RiverCentrePixels is { } centre)
                {
                    using var lines = Image.CreateFromData(result.RiverTextureWidth, result.RiverTextureHeight,
                        false, Image.Format.Rgba8, centre);
                    _globeMaterial.SetShaderParameter("river_centre_map", ImageTexture.CreateFromImage(lines));
                    _globeMaterial.SetShaderParameter("river_atlas_header_rows", result.RiverAtlasHeaderRows);
                    var riverSourceW = result.RiverAtlasHeaderRows > 0 ? MapWidth : result.RiverTextureWidth;
                    var riverSourceH = result.RiverAtlasHeaderRows > 0 ? MapHeight : result.RiverTextureHeight;
                    _globeMaterial.SetShaderParameter("river_source_size", new Vector2(riverSourceW, riverSourceH));
                    result.RiverCentrePixels = null;
                }
                WakeGlobeRendering();
                break;
            }
            case 5:
                ApplyTerrainPresentation(result.Provinces);
                break;
            default:
                _provinceTopology = result.Provinces;
                // What the political raster was actually painted with. Copied rather than
                // referenced: the simulation mutates this same array every time a border
                // moves, and the whole point of keeping it is to be able to tell that it did.
                _paintedOwner = (ushort[])result.Provinces.ProvinceToNation.Clone();
                _labelOwner = (ushort[])result.Provinces.ProvinceToNation.Clone();

                // And the topology keeps its own copy of the authored world, for the next
                // campaign started this session: the simulation writes its borders into
                // these same arrays, so without a copy taken now - before anything has
                // played on the map - there is nothing left to start a second campaign in.
                result.Provinces.RememberAuthoredBorders();
                if (result.ProvinceRasterLoaded) GD.Print($"[dof] province data map loaded: {result.ProvinceSource}");
                else GD.PushWarning($"[dof] province data map fallback: {result.ProvinceSource}");
                GD.Print($"[dof] hydrology data source: {result.HydrologySource}");
                if (_campaign is { } initCampaign && initCampaign.TryEnterRead(250, true))
                {
                    try
                    {
                        BindArmyTopology();
                        ReconcileArmyMarkers(initCampaign);
                        BuildCountryLabels(result.LabelAnchors);
                        AdoptRegionLabelAnchors(result.RegionAnchors);
                        AdoptRiverAnchors(result.RiverAnchors);
                        result.RegionAnchors = null;
                        result.RiverAnchors = null;
                        BuildFrontPreview();
                        RefreshProvinceControllers();
                    }
                    finally { initCampaign.ExitRead(); }
                }
                else
                {
                    BindArmyTopology();
                    ReconcileArmyMarkers(_campaign);
                    BuildCountryLabels(result.LabelAnchors);
                    AdoptRegionLabelAnchors(result.RegionAnchors);
                    AdoptRiverAnchors(result.RiverAnchors);
                    result.RegionAnchors = null;
                    result.RiverAnchors = null;
                    BuildFrontPreview();
                    RefreshProvinceControllers();
                }
                _pendingMapResult = null;
                SourceEarthData.ReleaseDecodedGrids();
                Simulation.LakeRegions.ReleaseWaterCache();

                // One collection, here and nowhere else.
                //
                // Building the map is the largest allocation the game ever makes: five
                // rasters of 5760x2880 plus everything the decoders needed to produce them,
                // all of it on the large-object heap, which a generation-zero or -one pass
                // never touches. Left alone the heap sits at two thirds of a gigabyte for the
                // rest of the session, having handed the card its copies long ago.
                //
                // Asking for a compacting pass in the middle of play would be indefensible.
                // Asking for one at the end of loading, which the player is already waiting
                // through, is the opposite: it is the one moment in the game where a pause
                // costs nothing and the amount to be reclaimed is at its largest.
                // One-shot LOH compaction at the end of loading; never pin SustainedLowLatency permanently
                // (leaving default Interactive prevents 1.78 GiB managed-heap spikes in long sessions).
                // Android/iOS Mono runtimes throw PlatformNotSupportedException on LargeObjectHeapCompactionMode.
                var gcWatch = System.Diagnostics.Stopwatch.StartNew();
                if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
                {
                    try
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        GC.Collect();
                    }
                }
                else
                {
                    try { GC.Collect(2, GCCollectionMode.Forced, blocking: true); }
                    catch { GC.Collect(); }
                }
                gcWatch.Stop();
                var uploadMs = _mapUploadStartTicks > 0
                    ? (System.Diagnostics.Stopwatch.GetTimestamp() - _mapUploadStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency
                    : 0.0;
                GD.Print($"[dof-boot] gpu_upload_ms={uploadMs:F0} gc_ms={gcWatch.Elapsed.TotalMilliseconds:F0}");
                GD.Print($"[dof] map applied; managed heap {GC.GetTotalMemory(false) / (1024 * 1024):N0} MB");
                GD.Print($"[dof] tactical province topology ready: {_provinceTopology.ProvinceCount:N0} land provinces, " +
                         $"{_provinceTopology.RiverCrossingCount:N0} river crossings, {_riverAnchors.Count:N0} river labels");
                _lastSelectedRegionLive = int.MinValue;
                _lastSelectedRegionCampaign = null;
                _lastSelectedRegionMap = null;
                _mapApplied = true;
                break;
        }
    }

    private static string LoadGlobeShaderSource()
    {
        if (Godot.FileAccess.FileExists(MapShaderSource.ResourcePath))
            return Godot.FileAccess.GetFileAsString(MapShaderSource.ResourcePath).Replace("\r\n", "\n");
        var disk = GameDataLoader.FindDataFile("shaders", "globe_map.gdshader");
        if (!string.IsNullOrEmpty(disk)) return File.ReadAllText(disk).Replace("\r\n", "\n");
        return GD.Load<Shader>(MapShaderSource.ResourcePath)?.Code?.Replace("\r\n", "\n")
            ?? throw new FileNotFoundException($"Missing globe shader: {MapShaderSource.ResourcePath}");
    }

    private void CreateGlobe(Node3D world)
    {
        var mesh = new SphereMesh
        {
            Radius = GlobeRadius,
            Height = GlobeRadius * 2f,
            RadialSegments = 256,
            Rings = 128
        };

        var shader = new Shader { Code = LoadGlobeShaderSource() };
        if (ReducedMap) shader.Code = CompatibilityMapShader.Source;
        else
        {
            shader.Code = MapColorSpaceShader.Build(shader.Code);
            if (UsePackedMapShader)
                shader.Code = PackedMapShader.Build(shader.Code);
        }
        _globeMaterial = new ShaderMaterial { Shader = shader };
        _cachedOwnersMovedMaterial = null;
        _cachedOwnersMoved = null;
        InitializePackedTextures();
        ApplyMapVisualStyle();
        ApplyShaderQualityTier(force: true);
        GD.Print("[dof] map material=" + (ReducedMap ? "safe-atlas (reduced visuals)" :
            _packedLookups is not null ? "full-packed-atlas (12 texture units)" : "full-atlas") + " renderer=" + RenderingServer.GetCurrentRenderingMethod());
        // Until the asynchronous political map has finished, the shader sees transparent
        // political pixels and presents the deliberately flat strategic ocean.
        var transparent = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        var placeholder = ImageTexture.CreateFromImage(transparent);
        _globeMaterial.SetShaderParameter("political_map", placeholder);
        var emptyIds = ImageTexture.CreateFromImage(Image.CreateEmpty(2, 2, false, Image.Format.Rg8));
        if (_packedIds is null)
        {
            _globeMaterial.SetShaderParameter("region_ids", emptyIds);
            _globeMaterial.SetShaderParameter("province_ids", emptyIds);
            _globeMaterial.SetShaderParameter("nation_ids", emptyIds);
        }
        var dryWater = Image.CreateEmpty(2, 2, false, Image.Format.R8);
        dryWater.Fill(Colors.White);
        _globeMaterial.SetShaderParameter("hydrology_map", ImageTexture.CreateFromImage(dryWater));
        if (!ReducedMap) _globeMaterial.SetShaderParameter("river_centre_map", placeholder);
        if (_packedLookups is null)
        {
            _ownerIdTexture = ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8));
            _globeMaterial.SetShaderParameter("province_owner_ids", _ownerIdTexture);
            _controllerTexture = ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8));
            _globeMaterial.SetShaderParameter("province_control_colors", _controllerTexture);
            _frontTexture = ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8));
            _globeMaterial.SetShaderParameter("front_membership", _frontTexture);
            _mapModeTexture = ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8));
            _globeMaterial.SetShaderParameter("map_mode_colors", _mapModeTexture);
            _globeMaterial.SetShaderParameter("province_owner_colors", placeholder);
            _globeMaterial.SetShaderParameter("resource_colors", placeholder);
        }
        mesh.Material = _globeMaterial;

        _globeNode = new MeshInstance3D { Name = "PoliticalGlobe", Mesh = mesh, Layers = AtlasSurfaceLayers };
        world.AddChild(_globeNode);
    }

    private void RefreshProvinceControllers()
    {
        if (_campaign is { } campaign)
        {
            if (!campaign.TryEnterRead(0, true))
            {
                _controlRevision = -1;
                return;
            }
            try { RefreshProvinceControllersCore(); }
            finally { campaign.ExitRead(); }
            return;
        }
        RefreshProvinceControllersCore();
    }

    private void RefreshProvinceControllersCore()
    {
        if (_campaign?.ProvinceMap is null || _campaign.ProvinceController is null || _globeMaterial is null) return;
        WakeGlobeRendering();
        EnsureNationIndex();
        var topology = _campaign.ProvinceMap;
        var pixels = _controlPixels ??= new byte[256 * 256 * 4];
        // The kernel still fills a colour for every province moved since the raster was drawn.
        // Nothing draws that any more - close up the colour comes from the owner ids, like all
        // other land - so the buffer is repacked below as the far plate's catch-up table.
        var ownerPixels = _ownerPixels ??= new byte[256 * 256 * 4];
        var ownerIds = _ownerIdPixels ??= new byte[256 * 256 * 4];
        // The regions held on lease, and by whom - read once, not once a province.
        _leasedRegionHolder.Clear();
        foreach (var arrangement in _campaign.LandArrangements)
            if (arrangement.Kind == Simulation.LandArrangementSystem.Lease &&
                _campaign.NationById(arrangement.HolderId) is { } tenant)
                foreach (var region in arrangement.Regions) _leasedRegionHolder[region] = tenant.MapIndex;
        var ownersMoved = Simulation.RasterOverlayKernel.PackProvinceOverlays(
            topology.ProvinceCount,
            topology.ProvinceToNation,
            _paintedOwner,
            _campaign.ProvinceController,
            _colorByMapIndex,
            OccupiedStripeAlpha,
            UnownedPaletteIndex,
            0x6E7B8BFFu,
            pixels,
            ownerPixels,
            ownerIds);
        // A lease wears its own code rather than an occupation's stripes (LeasedAlpha).
        if (_leasedRegionHolder.Count > 0)
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                var write = province * 4;
                if (pixels[write + 3] != OccupiedStripeAlpha) continue;
                if (_leasedRegionHolder.TryGetValue(topology.ProvinceToState[province], out var tenantIndex) &&
                    tenantIndex == _campaign.ProvinceController[province])
                    pixels[write + 3] = LeasedAlpha;
            }
        // The peace being negotiated is drawn over the controller map, so the player sees
        // the border they are proposing rather than the one that exists. This one is solid -
        // a full alpha - because a province inside the draft treaty is not being occupied,
        // it is being signed over, and the two must not look alike while a player decides.
        if (_peacePreview.Count > 0)
        {
            var packed = _colorByMapIndex.TryGetValue(_peacePreviewOwner, out var claimColor)
                ? claimColor : 0xE7C966FFu;
            foreach (var province in _peacePreview)
            {
                if (province <= 0 || province > topology.ProvinceCount) continue;
                var write = province * 4;
                pixels[write] = (byte)((packed >> 24) & 0xFF);
                pixels[write + 1] = (byte)((packed >> 16) & 0xFF);
                pixels[write + 2] = (byte)((packed >> 8) & 0xFF);
                pixels[write + 3] = 255;
            }
        }

        if (_controlImage is null) _controlImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _controlImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("province_control_colors", _controlImage, ref _controllerTexture);
        UploadPlateCatchUp();
        if (_ownerIdImage is null) _ownerIdImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, ownerIds);
        else _ownerIdImage.SetData(256, 256, false, Image.Format.Rgba8, ownerIds);
        UpdateMapImage("province_owner_ids", _ownerIdImage, ref _ownerIdTexture);
        var movedVal = ownersMoved ? 1.0f : 0.0f;
        if (!ReferenceEquals(_cachedOwnersMovedMaterial, _globeMaterial) || _cachedOwnersMoved != ownersMoved)
        {
            _cachedOwnersMovedMaterial = _globeMaterial;
            _cachedOwnersMoved = ownersMoved;
            _globeMaterial.SetShaderParameter("owners_moved", movedVal);
        }
        // Asked every time, not only when something differs from the authored world: owners
        // that have come BACK to it leave the picture on screen just as wrong. The request
        // itself is free when the plate already shows today's owners.
        RequestPlateRepaint(topology);
        _controlRevision = _campaign.MapRevision;
        // Standalone callers schedule a front refresh; SyncFencedWorldVisuals rebuilds atomically and clears this.
        _frontPreviewDeferred = true;
    }

    // Positions and labels are written by the reconcile itself now, off the node handles it
    // already holds - there is nothing left for a second pass to do.

    private static MapBuildResult BuildPoliticalMap(
        Dictionary<int, uint> colors,
        Simulation.MapPlateShades shades,
        bool buildFrontier,
        bool reducedMap,
        bool lowMemoryMobile,
        bool compactRivers,
        string cacheDirectory,
        string shippedCachePath,
        string shippedHydrologyCachePath,
        string shippedPoliticalRasterCachePath,
        Action<string, int> reportProgress,
        bool halfResGpuMaps = false)
    {
        var totalWatch = System.Diagnostics.Stopwatch.StartNew();
        var stageWatch = new System.Diagnostics.Stopwatch();
        double cacheIoMs = 0, topoMs = 0, hydroMs = 0, anchorMs = 0, pixelMs = 0, plateMs = 0;

        ushort[] control = null;
        var states = SourceEarthData.PeekStatesIfLoaded();
        var sourceWidth = SourceEarthData.Width;
        var sourceHeight = SourceEarthData.Height;
        ulong regionFingerprint;
        if (states is { Length: > 0 })
        {
            regionFingerprint = Simulation.MapRasterCatalog.RegionFingerprint(states);
        }
        else if (!Simulation.TerritorialTopology.TryPeekBootCacheRegionFingerprint(
                     sourceWidth, sourceHeight, out regionFingerprint))
        {
            states = SourceEarthData.States;
            regionFingerprint = Simulation.MapRasterCatalog.RegionFingerprint(states);
        }

        Simulation.ProvinceTopology provinces;
        string provinceSource;
        string hydrologySource;
        Dictionary<int, TerritoryLabelAnchor> labelAnchors;
        Dictionary<int, TerritoryLabelAnchor> regionAnchors;
        IReadOnlyList<RiverLabelAnchor> riverAnchors;
        byte[] hydrologyPixels = null;
        byte[] riverCentrePixels = null;
        bool cacheHit;
        var hydroCacheHit = false;
        var rasterCacheHit = false;

        reportProgress?.Invoke("Checking map boot cache", 42);
        stageWatch.Restart();
        if (Simulation.MapBootCache.TryRead(
                cacheDirectory, regionFingerprint, sourceWidth, sourceHeight,
                out var cached, out var cacheReason, shippedCachePath))
        {
            cacheHit = true;
            Simulation.MapRasterCatalog.ReleasePrefetch();
            cacheIoMs += stageWatch.Elapsed.TotalMilliseconds;
            provinces = cached.Provinces;
            provinceSource = cached.ProvinceSource;
            hydrologySource = cached.HydrologySource;
            labelAnchors = FromCachedAnchors(cached.CountryAnchors);
            regionAnchors = FromCachedAnchors(cached.RegionAnchors);
            riverAnchors = FromCachedRiverAnchors(cached.RiverAnchors);

            // A cache saved during a reduced/safe-map session omits terrain surface slots;
            // desktop full-atlas sessions transparently restore them without invalidating the cache.
            if (!reducedMap && provinces.TerrainSurfaceSlots.Length != sourceWidth * sourceHeight)
            {
                stageWatch.Restart();
                provinces.AttachTerrain();
                topoMs += stageWatch.Elapsed.TotalMilliseconds;
            }

            if (!reducedMap)
            {
                reportProgress?.Invoke("Loading cached river and lake layers", 47);
                stageWatch.Restart();
                if (Simulation.MapBootCache.TryReadHydrology(
                        cacheDirectory, regionFingerprint, MapWidth, MapHeight,
                        out var cachedHydro, out _, shippedHydrologyCachePath))
                {
                    hydroCacheHit = true;
                    hydrologyPixels = cachedHydro.LakeSdfR8;
                    riverCentrePixels = cachedHydro.RiverCentreRgba8.Length > 0 ? cachedHydro.RiverCentreRgba8 : null;
                    if (!string.IsNullOrWhiteSpace(cachedHydro.HydrologySource))
                        hydrologySource = cachedHydro.HydrologySource;
                    cacheIoMs += stageWatch.Elapsed.TotalMilliseconds;
                }
                else
                {
                    reportProgress?.Invoke("Building river and lake layers", 47);
                    var hydrology = Simulation.HydrologyCatalog.BuildRaster(sourceWidth, sourceHeight);
                    hydrologySource = hydrology.Source;
                    BuildHydrologySurfaceLayers(hydrology, lowMemoryMobile, out hydrologyPixels, out riverCentrePixels);
                    hydroMs += stageWatch.Elapsed.TotalMilliseconds;

                    stageWatch.Restart();
                    Simulation.MapBootCache.TryWriteHydrology(
                        cacheDirectory, regionFingerprint, MapWidth, MapHeight,
                        new Simulation.HydrologyBootCacheData
                        {
                            LakeSdfR8 = hydrologyPixels ?? Array.Empty<byte>(),
                            RiverCentreRgba8 = riverCentrePixels ?? Array.Empty<byte>(),
                            HydrologySource = hydrologySource ?? ""
                        },
                        out _);
                    cacheIoMs += stageWatch.Elapsed.TotalMilliseconds;
                }
            }
        }
        else
        {
            cacheHit = false;
            cacheIoMs += stageWatch.Elapsed.TotalMilliseconds;
            states ??= SourceEarthData.States;
            control = SourceEarthData.Control;

            if (lowMemoryMobile)
            {
                // Bounded sequential phases on phone hardware avoid 6 simultaneous 50+ MB buffers thrashing RAM/swap.
                reportProgress?.Invoke("Loading province topology", 44);
                stageWatch.Restart();
                if (!Simulation.MapRasterCatalog.TryBuildProvinceTopology(
                        states, control, sourceWidth, sourceHeight, out provinces, out provinceSource))
                    throw new InvalidDataException(
                        $"game/map_data/provinces.png + definition.csv could not be loaded ({provinceSource}). " +
                        "The base game never generates replacement province IDs at runtime; rebuild the data map with " +
                        "Tools/MapDataCompiler or install a mod that supplies a complete raster pair.");
                topoMs += stageWatch.Elapsed.TotalMilliseconds;

                reportProgress?.Invoke("Building river and lake layers", 47);
                stageWatch.Restart();
                var hydrology = Simulation.HydrologyCatalog.BuildRaster(sourceWidth, sourceHeight);
                hydrologySource = hydrology.Source;
                provinces.AttachHydrology(hydrology);
                if (!reducedMap)
                {
                    if (Simulation.MapBootCache.TryReadHydrology(
                            cacheDirectory, regionFingerprint, MapWidth, MapHeight,
                            out var cachedHydro, out _, shippedHydrologyCachePath))
                    {
                        hydroCacheHit = true;
                        hydrologyPixels = cachedHydro.LakeSdfR8;
                        riverCentrePixels = cachedHydro.RiverCentreRgba8.Length > 0 ? cachedHydro.RiverCentreRgba8 : null;
                        if (!string.IsNullOrWhiteSpace(cachedHydro.HydrologySource))
                            hydrologySource = cachedHydro.HydrologySource;
                    }
                    else
                    {
                        BuildHydrologySurfaceLayers(hydrology, lowMemoryMobile: true, out hydrologyPixels, out riverCentrePixels);
                    }
                }
                hydroMs += stageWatch.Elapsed.TotalMilliseconds;

                reportProgress?.Invoke("Computing territory labels", 49);
                stageWatch.Restart();
                labelAnchors = BuildTerritoryLabelAnchors(control, sourceWidth, sourceHeight);
                regionAnchors = BuildRegionAnchorsDownsampled(states, sourceWidth, sourceHeight);
                riverAnchors = RiverLabelLayout.BuildAnchors(Simulation.HydrologyCatalog.RiverFeatures);
                anchorMs += stageWatch.Elapsed.TotalMilliseconds;
            }
            else
            {
                reportProgress?.Invoke("Loading province topology", 44);
                stageWatch.Restart();
                var hydrologyWork = Task.Run(() => Simulation.HydrologyCatalog.BuildRaster(sourceWidth, sourceHeight));
                var anchorWork = Task.Run(() => BuildTerritoryLabelAnchors(control, sourceWidth, sourceHeight));
                var regionAnchorWork = Task.Run(() => BuildRegionAnchorsDownsampled(states, sourceWidth, sourceHeight));
                var riverAnchorWork = Task.Run(() => RiverLabelLayout.BuildAnchors(Simulation.HydrologyCatalog.RiverFeatures));
                if (!Simulation.MapRasterCatalog.TryBuildProvinceTopology(
                        states, control, sourceWidth, sourceHeight, out provinces, out provinceSource))
                    throw new InvalidDataException(
                        $"game/map_data/provinces.png + definition.csv could not be loaded ({provinceSource}). " +
                        "The base game never generates replacement province IDs at runtime; rebuild the data map with " +
                        "Tools/MapDataCompiler or install a mod that supplies a complete raster pair.");
                topoMs += stageWatch.Elapsed.TotalMilliseconds;

                reportProgress?.Invoke("Building river and lake layers", 47);
                stageWatch.Restart();
                var hydrology = hydrologyWork.GetAwaiter().GetResult();
                hydrologySource = hydrology.Source;
                provinces.AttachHydrology(hydrology);
                Task<byte[]> centreWork = null;
                Task<byte[]> surfaceWork = null;
                if (!reducedMap)
                {
                    centreWork = Task.Run(() => HydrologySurfaceData.BuildCentreline(MapWidth, MapHeight,
                        hydrology.AuthoredRivers ? RasterRiverCourses.Extract(hydrology) : Simulation.HydrologyCatalog.RiverFeatures));
                    surfaceWork = Task.Run(() => HydrologySurfaceData.BuildLakeSdf(hydrology));
                }

                var anchorWatch = System.Diagnostics.Stopwatch.StartNew();
                labelAnchors = anchorWork.GetAwaiter().GetResult();
                regionAnchors = regionAnchorWork.GetAwaiter().GetResult();
                riverAnchors = riverAnchorWork.GetAwaiter().GetResult();
                anchorMs += anchorWatch.Elapsed.TotalMilliseconds;

                if (surfaceWork is not null) hydrologyPixels = surfaceWork.GetAwaiter().GetResult();
                if (centreWork is not null) riverCentrePixels = centreWork.GetAwaiter().GetResult();
                hydroMs += stageWatch.Elapsed.TotalMilliseconds;
            }

            // Persist map_boot_v2.bin and hydrology_v2.bin for instant subsequent launches (bypassed when mod overlays are active).
            stageWatch.Restart();
            var cacheBundle = new Simulation.MapBootCacheData
            {
                Provinces = provinces,
                ProvinceSource = provinceSource,
                HydrologySource = hydrologySource,
                CountryAnchors = ToCachedAnchors(labelAnchors),
                RegionAnchors = ToCachedAnchors(regionAnchors),
                RiverAnchors = ToCachedRiverAnchors(riverAnchors),
                HasTerrainSurfaceSlots = !reducedMap && provinces.TerrainSurfaceSlots.Length == sourceWidth * sourceHeight
            };
            Simulation.MapBootCache.TryWrite(
                cacheDirectory, regionFingerprint, cacheBundle, out _, includeTerrainSurfaceSlots: !reducedMap);
            if (!reducedMap && hydrologyPixels is not null)
            {
                Simulation.MapBootCache.TryWriteHydrology(
                    cacheDirectory, regionFingerprint, MapWidth, MapHeight,
                    new Simulation.HydrologyBootCacheData
                    {
                        LakeSdfR8 = hydrologyPixels,
                        RiverCentreRgba8 = riverCentrePixels ?? Array.Empty<byte>(),
                        HydrologySource = hydrologySource ?? ""
                    },
                    out _);
            }
            cacheIoMs += stageWatch.Elapsed.TotalMilliseconds;
        }

        // Warm sea graph and cities lazily in background without blocking map display.
        var seaTopology = provinces;
        _ = Task.Run(() => { try { Simulation.SeaLanes.CoastalProvinces(seaTopology); } catch { } });
        _ = Task.Run(() => { try { _ = Simulation.CityCatalog.All.Count; } catch { } });

        var politicalWidth = MapWidth / PoliticalScale;
        var politicalHeight = MapHeight / PoliticalScale;
        ushort[] plateProvinces = null;
        ushort[] plateHolders = null;
        byte[] plateGeography = null;
        byte[] plateClass = null;
        byte[] regionIds = null;
        byte[] provinceIds = null;
        byte[] nationIds = null;

        if (cacheHit)
        {
            reportProgress?.Invoke("Checking packed raster cache", 50);
            stageWatch.Restart();
            if (Simulation.MapBootCache.TryReadPoliticalRasters(
                    cacheDirectory, regionFingerprint, MapWidth, MapHeight, PoliticalScale,
                    out var cachedRasters, out _, shippedPoliticalRasterCachePath))
            {
                rasterCacheHit = true;
                plateProvinces = cachedRasters.PlateProvinces;
                plateHolders = cachedRasters.PlateHolders;
                plateGeography = cachedRasters.PlateGeography;
                plateClass = cachedRasters.PlateClass;
                regionIds = cachedRasters.RegionIds;
                provinceIds = cachedRasters.ProvinceIds;
                nationIds = cachedRasters.NationIds;
            }
            cacheIoMs += stageWatch.Elapsed.TotalMilliseconds;
        }

        ushort[] frontierAuthored = null;
        if (rasterCacheHit)
        {
            if (buildFrontier && !lowMemoryMobile)
            {
                frontierAuthored = sourceWidth == MapWidth && sourceHeight == MapHeight
                    ? UnpackLittleEndianUShorts(nationIds)
                    : SourceEarthData.Control;
            }
        }
        else
        {
            states ??= SourceEarthData.States;
            control ??= SourceEarthData.Control;
            frontierAuthored = control;
        }

        Task<Image> frontierWork = (!lowMemoryMobile && buildFrontier && frontierAuthored is not null)
            ? Task.Run(() => FrontierImage(frontierAuthored, provinces, provinces.ProvinceToNation, provinces.ProvinceToNation))
            : null;

        if (!rasterCacheHit)
        {
            plateProvinces = new ushort[politicalWidth * politicalHeight];
            plateHolders = new ushort[politicalWidth * politicalHeight];
            plateGeography = new byte[politicalWidth * politicalHeight];
            plateClass = new byte[politicalWidth * politicalHeight];
            regionIds = new byte[MapWidth * MapHeight * 2];
            provinceIds = new byte[MapWidth * MapHeight * 2];
            nationIds = new byte[MapWidth * MapHeight * 2];

            reportProgress?.Invoke("Packing political map rasters", 51);
            stageWatch.Restart();
            var cellToProvince = provinces.CellToProvince;
            var provinceToNation = provinces.ProvinceToNation;
            var provinceToState = provinces.ProvinceToState;
            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4)
            };
            if (sourceWidth == MapWidth && sourceHeight == MapHeight)
            {
                // Fast 1:1 path: strip-aligned to PoliticalScale (3-row bands) so each band owns
                // disjoint plate/id slices with zero synchronization and zero inner-loop divisions.
                Parallel.For(0, politicalHeight, parallelOpts, band =>
                {
                    var yStart = band * PoliticalScale;
                    var yEnd = Math.Min(yStart + PoliticalScale, MapHeight);
                    var plateRow = band * politicalWidth;
                    for (var y = yStart; y < yEnd; y++)
                    {
                        var row = y * MapWidth;
                        var southRow = (y + 1 < MapHeight ? y + 1 : MapHeight - 1) * MapWidth;
                        var northRow = (y > 0 ? y - 1 : 0) * MapWidth;
                        var idWrite = row * 2;
                        for (var x = 0; x < MapWidth; x++, idWrite += 2)
                        {
                            var index = row + x;
                            var provinceId = cellToProvince[index];
                            var controlId = provinceId > 0 ? provinceToNation[provinceId] : control[index];
                            var regionId = provinceId > 0 ? provinceToState[provinceId] : states[index];
                            nationIds[idWrite] = (byte)(controlId & 0xFF);
                            nationIds[idWrite + 1] = (byte)((controlId >> 8) & 0xFF);
                            regionIds[idWrite] = (byte)(regionId & 0xFF);
                            regionIds[idWrite + 1] = (byte)((regionId >> 8) & 0xFF);
                            if (provinceId > 0)
                            {
                                provinceIds[idWrite] = (byte)(provinceId & 0xFF);
                                provinceIds[idWrite + 1] = (byte)((provinceId >> 8) & 0xFF);
                            }
                            if (controlId == 0) continue;

                            var east = row + (x + 1 == MapWidth ? 0 : x + 1);
                            var west = row + (x == 0 ? MapWidth - 1 : x - 1);
                            var south = southRow + x;
                            var north = northRow + x;
                            var shore = control[east] == 0 || control[west] == 0 || control[south] == 0 || control[north] == 0;
                            var frontier = control[east] != controlId || control[west] != controlId ||
                                control[south] != controlId || control[north] != controlId;
                            var regionLine = states[east] != regionId || states[west] != regionId ||
                                states[south] != regionId || states[north] != regionId;
                            var cell = plateRow + (x / PoliticalScale);
                            if (shore) plateGeography[cell] |= Simulation.PoliticalPlate.Coast;
                            if (regionLine) plateGeography[cell] |= Simulation.PoliticalPlate.RegionLine;
                            var cellClass = (byte)(frontier ? 3 : regionLine ? 2 : 1);
                            if (cellClass <= plateClass[cell]) continue;
                            plateClass[cell] = cellClass;
                            plateProvinces[cell] = provinceId;
                            plateHolders[cell] = controlId;
                        }
                    }
                });
            }
            else
            {
                Parallel.For(0, politicalHeight, parallelOpts, band =>
                {
                    var yStart = band * PoliticalScale;
                    var yEnd = Math.Min(yStart + PoliticalScale, MapHeight);
                    var plateRow = band * politicalWidth;
                    for (var y = yStart; y < yEnd; y++)
                    {
                        var sourceY = y * sourceHeight / MapHeight;
                        var row = sourceY * sourceWidth;
                        var southRow = Math.Min(sourceY + 1, sourceHeight - 1) * sourceWidth;
                        var idWrite = y * MapWidth * 2;
                        for (var x = 0; x < MapWidth; x++, idWrite += 2)
                        {
                            var sourceX = x * sourceWidth / MapWidth;
                            var index = row + sourceX;
                            var provinceId = cellToProvince[index];
                            var controlId = provinceId > 0 ? provinceToNation[provinceId] : control[index];
                            var regionId = provinceId > 0 ? provinceToState[provinceId] : states[index];
                            nationIds[idWrite] = (byte)(controlId & 0xFF);
                            nationIds[idWrite + 1] = (byte)((controlId >> 8) & 0xFF);
                            regionIds[idWrite] = (byte)(regionId & 0xFF);
                            regionIds[idWrite + 1] = (byte)((regionId >> 8) & 0xFF);
                            if (provinceId > 0)
                            {
                                provinceIds[idWrite] = (byte)(provinceId & 0xFF);
                                provinceIds[idWrite + 1] = (byte)((provinceId >> 8) & 0xFF);
                            }
                            if (controlId == 0) continue;

                            var east = row + (sourceX + 1 == sourceWidth ? 0 : sourceX + 1);
                            var west = row + (sourceX == 0 ? sourceWidth - 1 : sourceX - 1);
                            var south = southRow + sourceX;
                            var north = (sourceY == 0 ? row : row - sourceWidth) + sourceX;
                            var shore = control[east] == 0 || control[west] == 0 || control[south] == 0 || control[north] == 0;
                            var frontier = control[east] != controlId || control[west] != controlId ||
                                control[south] != controlId || control[north] != controlId;
                            var regionLine = states[east] != regionId || states[west] != regionId ||
                                states[south] != regionId || states[north] != regionId;
                            var cell = plateRow + (x / PoliticalScale);
                            if (shore) plateGeography[cell] |= Simulation.PoliticalPlate.Coast;
                            if (regionLine) plateGeography[cell] |= Simulation.PoliticalPlate.RegionLine;
                            var cellClass = (byte)(frontier ? 3 : regionLine ? 2 : 1);
                            if (cellClass <= plateClass[cell]) continue;
                            plateClass[cell] = cellClass;
                            plateProvinces[cell] = provinceId;
                            plateHolders[cell] = controlId;
                        }
                    }
                });
            }
            pixelMs = stageWatch.Elapsed.TotalMilliseconds;

            stageWatch.Restart();
            Simulation.MapBootCache.TryWritePoliticalRasters(
                cacheDirectory, regionFingerprint, MapWidth, MapHeight, PoliticalScale,
                new Simulation.PoliticalRasterBootCacheData
                {
                    PlateProvinces = plateProvinces,
                    PlateHolders = plateHolders,
                    PlateGeography = plateGeography,
                    PlateClass = plateClass,
                    RegionIds = regionIds,
                    ProvinceIds = provinceIds,
                    NationIds = nationIds
                },
                out _);
            cacheIoMs += stageWatch.Elapsed.TotalMilliseconds;
        }

        reportProgress?.Invoke("Painting strategic map plate", 53);
        stageWatch.Restart();
        var plateSource = new Simulation.PoliticalPlateSource
        {
            Width = politicalWidth,
            Height = politicalHeight,
            Provinces = plateProvinces,
            Holders = plateHolders,
            Geography = plateGeography
        };
        var plateOwners = (ushort[])provinces.ProvinceToNation.Clone();
        var political = Simulation.PoliticalPlate.Paint(plateSource, plateOwners, plateOwners, colors, shades);
        Image frontierImage = frontierWork is not null
            ? FrontierImageOrNull(frontierWork)
            : (lowMemoryMobile && buildFrontier && frontierAuthored is not null
                ? FrontierImageOrNull(Task.Run(() => FrontierImage(frontierAuthored, provinces, provinces.ProvinceToNation, provinces.ProvinceToNation)))
                : null);
        plateMs = stageWatch.Elapsed.TotalMilliseconds;

        if (provinces.ProvinceCount > ushort.MaxValue)
            throw new InvalidOperationException($"Province count {provinces.ProvinceCount} exceeds the 16-bit map contract.");

        var riverAtlas = compactRivers ? HydrologySurfaceData.PackRiverCentreline(riverCentrePixels, MapWidth, MapHeight) : null;
        var riverPixelsOut = riverAtlas?.Pixels ?? riverCentrePixels;
        var riverWidthOut = riverAtlas?.Width ?? MapWidth;
        var riverHeightOut = riverAtlas?.Height ?? MapHeight;
        var riverHeaderRows = riverAtlas?.HeaderRows ?? 0;

        var gpuMapWidth = MapWidth;
        var gpuMapHeight = MapHeight;
        var gpuHydrologyWidth = MapWidth;
        var gpuHydrologyHeight = MapHeight;
        if (halfResGpuMaps)
        {
            DownsampleMobileGpuIdLayersHalfRes(
                regionIds, provinceIds, nationIds, MapWidth, MapHeight,
                out regionIds, out provinceIds, out nationIds,
                out gpuMapWidth, out gpuMapHeight);
            if (!reducedMap && hydrologyPixels is not null)
            {
                hydrologyPixels = DownsampleMobileHydrologyHalfRes(
                    hydrologyPixels, MapWidth, MapHeight,
                    out gpuHydrologyWidth, out gpuHydrologyHeight);
            }
            if (!reducedMap && riverAtlas is null && riverPixelsOut is not null &&
                riverWidthOut == MapWidth && riverHeightOut == MapHeight)
            {
                riverPixelsOut = DownsampleMobileRiverCentreHalfRes(
                    riverPixelsOut, riverWidthOut, riverHeightOut,
                    out riverWidthOut, out riverHeightOut);
            }
        }

        GD.Print($"[dof-boot] map_worker total_ms={totalWatch.Elapsed.TotalMilliseconds:F0} cache_hit={cacheHit} hydro_cache={hydroCacheHit} raster_cache={rasterCacheHit} " +
                 $"cache_io_ms={cacheIoMs:F0} topology_ms={topoMs:F0} hydrology_ms={hydroMs:F0} anchors_ms={anchorMs:F0} " +
                 $"pixel_loop_ms={pixelMs:F0} plate_ms={plateMs:F0} reduced={reducedMap} mobile={lowMemoryMobile} half_res_gpu={halfResGpuMaps} gpu_map={gpuMapWidth}x{gpuMapHeight} ({cacheReason})");
        GD.Print($"[dof-boot] river_texture_bytes={riverPixelsOut?.Length ?? 0} compact={riverAtlas.HasValue}");

        return new MapBuildResult
        {
            PoliticalPixels = political,
            PlateSource = plateSource,
            PlateOwners = plateOwners,
            FrontierAuthored = frontierAuthored,
            Frontier = frontierImage,
            RegionIds = regionIds,
            ProvinceIds = provinceIds,
            NationIds = nationIds,
            GpuMapWidth = gpuMapWidth,
            GpuMapHeight = gpuMapHeight,
            HydrologyPixels = hydrologyPixels,
            GpuHydrologyWidth = gpuHydrologyWidth,
            GpuHydrologyHeight = gpuHydrologyHeight,
            RiverCentrePixels = riverPixelsOut,
            RiverTextureWidth = riverWidthOut,
            RiverTextureHeight = riverHeightOut,
            RiverAtlasHeaderRows = riverHeaderRows,
            Provinces = provinces,
            LabelAnchors = labelAnchors,
            RegionAnchors = regionAnchors,
            RiverAnchors = riverAnchors,
            ProvinceRasterLoaded = true,
            ProvinceSource = provinceSource,
            HydrologySource = hydrologySource
        };
    }

    private static ushort[] UnpackLittleEndianUShorts(byte[] rgBytes)
    {
        var count = rgBytes.Length / 2;
        var result = new ushort[count];
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, ushort>(rgBytes.AsSpan(0, count * 2)).CopyTo(result);
        }
        else
        {
            for (var i = 0; i < count; i++)
                result[i] = (ushort)(rgBytes[i * 2] | (rgBytes[i * 2 + 1] << 8));
        }
        return result;
    }

    private static void BuildHydrologySurfaceLayers(
        Simulation.HydrologyRaster hydrology,
        bool lowMemoryMobile,
        out byte[] hydrologyPixels,
        out byte[] riverCentrePixels)
    {
        if (lowMemoryMobile)
        {
            hydrologyPixels = HydrologySurfaceData.BuildLakeSdf(hydrology);
            riverCentrePixels = HydrologySurfaceData.BuildCentreline(MapWidth, MapHeight,
                hydrology.AuthoredRivers ? RasterRiverCourses.Extract(hydrology) : Simulation.HydrologyCatalog.RiverFeatures);
            return;
        }
        var centreTask = Task.Run(() => HydrologySurfaceData.BuildCentreline(MapWidth, MapHeight,
            hydrology.AuthoredRivers ? RasterRiverCourses.Extract(hydrology) : Simulation.HydrologyCatalog.RiverFeatures));
        hydrologyPixels = HydrologySurfaceData.BuildLakeSdf(hydrology);
        riverCentrePixels = centreTask.GetAwaiter().GetResult();
    }

    private static Simulation.CachedLabelAnchor[] ToCachedAnchors(Dictionary<int, TerritoryLabelAnchor> source)
    {
        if (source is null || source.Count == 0) return Array.Empty<Simulation.CachedLabelAnchor>();
        var list = new Simulation.CachedLabelAnchor[source.Count];
        var i = 0;
        foreach (var kv in source)
        {
            var a = kv.Value;
            list[i++] = new Simulation.CachedLabelAnchor(
                kv.Key, a.CenterX, a.CenterY, a.AxisX, a.AxisY, a.HalfSpanPixels, a.ThicknessPixels, a.Cells);
        }
        return list;
    }

    private static Dictionary<int, TerritoryLabelAnchor> FromCachedAnchors(Simulation.CachedLabelAnchor[] cached)
    {
        var dict = new Dictionary<int, TerritoryLabelAnchor>(cached?.Length ?? 0);
        if (cached is null) return dict;
        for (var i = 0; i < cached.Length; i++)
        {
            var a = cached[i];
            dict[a.Key] = new TerritoryLabelAnchor(
                a.CenterX, a.CenterY, a.AxisX, a.AxisY, a.HalfSpanPixels, a.ThicknessPixels, a.Cells);
        }
        return dict;
    }

    private static Simulation.CachedRiverAnchor[] ToCachedRiverAnchors(IReadOnlyList<RiverLabelAnchor> source)
    {
        if (source is null || source.Count == 0) return Array.Empty<Simulation.CachedRiverAnchor>();
        var list = new Simulation.CachedRiverAnchor[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var a = source[i];
            list[i] = new Simulation.CachedRiverAnchor(
                a.Key, a.DefaultName, a.Rank, a.Latitude, a.Longitude, a.AxisX, a.AxisY, a.SpanDegrees);
        }
        return list;
    }

    private static IReadOnlyList<RiverLabelAnchor> FromCachedRiverAnchors(Simulation.CachedRiverAnchor[] cached)
    {
        if (cached is null || cached.Length == 0) return Array.Empty<RiverLabelAnchor>();
        var list = new RiverLabelAnchor[cached.Length];
        for (var i = 0; i < cached.Length; i++)
        {
            var a = cached[i];
            list[i] = new RiverLabelAnchor(
                a.Key, a.DefaultName, a.Rank, a.Latitude, a.Longitude, a.AxisX, a.AxisY, a.SpanDegrees);
        }
        return list;
    }


    /// <summary>
    /// What the map worker built, and what it lets go of on the way in.
    ///
    /// A class with settable rasters rather than a record with init-only ones, for one
    /// reason: the five rasters are 199 MB between them at 5760x2880, and every one of them
    /// is wanted exactly once - to hand to `Image.CreateFromData`, which copies it. Holding
    /// all five until the last of the six upload stages finishes is 199 MB of peak that
    /// nothing is reading, on the hardware this game most wants to reach.
    ///
    /// So each one is dropped the moment its stage has uploaded it. They are large enough to
    /// go straight to the large-object heap, which is collected only on a generation-two
    /// pass, so being dropped early is what makes the one collection at the end able to
    /// return them.
    /// </summary>
    private sealed class MapBuildResult
    {
        public byte[] PoliticalPixels;
        public Simulation.PoliticalPlateSource PlateSource;
        public ushort[] PlateOwners;
        public ushort[] FrontierAuthored;
        public Image Frontier;
        public byte[] RegionIds;
        public byte[] ProvinceIds;
        public byte[] NationIds;
        public int GpuMapWidth = MapWidth;
        public int GpuMapHeight = MapHeight;
        public byte[] HydrologyPixels;
        public int GpuHydrologyWidth = MapWidth;
        public int GpuHydrologyHeight = MapHeight;
        public byte[] RiverCentrePixels;
        public int RiverTextureWidth, RiverTextureHeight, RiverAtlasHeaderRows;
        public Simulation.ProvinceTopology Provinces;
        public Dictionary<int, TerritoryLabelAnchor> LabelAnchors;
        public Dictionary<int, TerritoryLabelAnchor> RegionAnchors;
        public IReadOnlyList<RiverLabelAnchor> RiverAnchors;
        public bool ProvinceRasterLoaded;
        public string ProvinceSource = "";
        public string HydrologySource = "";
    }

    internal static void DownsampleMobileGpuIdLayersHalfRes(
        byte[] regionIds,
        byte[] provinceIds,
        byte[] nationIds,
        int sourceWidth,
        int sourceHeight,
        out byte[] halfRegionIds,
        out byte[] halfProvinceIds,
        out byte[] halfNationIds,
        out int halfWidth,
        out int halfHeight)
    {
        if (sourceWidth < 2 || sourceHeight < 2)
        {
            halfWidth = sourceWidth;
            halfHeight = sourceHeight;
            halfRegionIds = regionIds;
            halfProvinceIds = provinceIds;
            halfNationIds = nationIds;
            return;
        }

        halfWidth = sourceWidth / 2;
        halfHeight = sourceHeight / 2;
        var expectedSourceBytes = sourceWidth * sourceHeight * 2;
        var halfBytes = halfWidth * halfHeight * 2;
        var hasRegion = regionIds is not null && regionIds.Length == expectedSourceBytes;
        var hasProvince = provinceIds is not null && provinceIds.Length == expectedSourceBytes;
        var hasNation = nationIds is not null && nationIds.Length == expectedSourceBytes;

        halfRegionIds = hasRegion ? new byte[halfBytes] : null;
        halfProvinceIds = hasProvince ? new byte[halfBytes] : null;
        halfNationIds = hasNation ? new byte[halfBytes] : null;
        var driver = hasProvince ? provinceIds : (hasNation ? nationIds : regionIds);
        if (driver is null) return;

        var outRegion = halfRegionIds;
        var outProvince = halfProvinceIds;
        var outNation = halfNationIds;
        var dstW = halfWidth;
        var srcW = sourceWidth;

        Parallel.For(0, halfHeight, hy =>
        {
            var sy0 = hy * 2;
            var sy1 = sy0 + 1;
            var row0 = sy0 * srcW;
            var row1 = sy1 * srcW;
            var dstRow = hy * dstW;
            for (var hx = 0; hx < dstW; hx++)
            {
                var sx0 = hx * 2;
                var sx1 = sx0 + 1;
                var i00 = (row0 + sx0) * 2;
                var i10 = (row0 + sx1) * 2;
                var i01 = (row1 + sx0) * 2;
                var i11 = (row1 + sx1) * 2;

                var a = (ushort)(driver[i00] | (driver[i00 + 1] << 8));
                var b = (ushort)(driver[i10] | (driver[i10 + 1] << 8));
                var c = (ushort)(driver[i01] | (driver[i01 + 1] << 8));
                var d = (ushort)(driver[i11] | (driver[i11 + 1] << 8));

                int chosen;
                if (a == b && a == c && a == d)
                {
                    chosen = i00;
                }
                else
                {
                    chosen = PickWinningQuadOffset(a, b, c, d, i00, i10, i01, i11, (hx ^ hy) & 1);
                }

                var dstOff = (dstRow + hx) * 2;
                if (outRegion is not null)
                {
                    outRegion[dstOff] = regionIds[chosen];
                    outRegion[dstOff + 1] = regionIds[chosen + 1];
                }
                if (outProvince is not null)
                {
                    outProvince[dstOff] = provinceIds[chosen];
                    outProvince[dstOff + 1] = provinceIds[chosen + 1];
                }
                if (outNation is not null)
                {
                    outNation[dstOff] = nationIds[chosen];
                    outNation[dstOff + 1] = nationIds[chosen + 1];
                }
            }
        });
    }

    private static int PickWinningQuadOffset(
        ushort a, ushort b, ushort c, ushort d,
        int i00, int i10, int i01, int i11, int checker)
    {
        var c0 = 1 + (a == b ? 1 : 0) + (a == c ? 1 : 0) + (a == d ? 1 : 0);
        var c1 = 1 + (b == a ? 1 : 0) + (b == c ? 1 : 0) + (b == d ? 1 : 0);
        var c2 = 1 + (c == a ? 1 : 0) + (c == b ? 1 : 0) + (c == d ? 1 : 0);
        var c3 = 1 + (d == a ? 1 : 0) + (d == b ? 1 : 0) + (d == c ? 1 : 0);

        // Score ties favor non-zero land/province IDs over ocean (0) on 2-vs-2 borders, and
        // alternate checkerboard diagonals so borders stay symmetric without directional drift.
        static int Score(int count, ushort id, bool preferredParity) =>
            (count << 3) + (id != 0 ? 4 : 0) + (preferredParity ? 1 : 0);

        var bestOff = i00;
        var bestScore = Score(c0, a, checker == 0);
        var s1 = Score(c1, b, checker == 1);
        if (s1 > bestScore) { bestScore = s1; bestOff = i10; }
        var s2 = Score(c2, c, checker == 1);
        if (s2 > bestScore) { bestScore = s2; bestOff = i01; }
        var s3 = Score(c3, d, checker == 0);
        if (s3 > bestScore) bestOff = i11;
        return bestOff;
    }

    internal static byte[] DownsampleMobileHydrologyHalfRes(
        byte[] source, int sourceWidth, int sourceHeight, out int halfWidth, out int halfHeight)
    {
        if (source is null || sourceWidth < 2 || sourceHeight < 2 || source.Length != sourceWidth * sourceHeight)
        {
            halfWidth = sourceWidth;
            halfHeight = sourceHeight;
            return source;
        }

        halfWidth = sourceWidth / 2;
        halfHeight = sourceHeight / 2;
        var dst = new byte[halfWidth * halfHeight];
        var dstW = halfWidth;
        var srcW = sourceWidth;

        Parallel.For(0, halfHeight, hy =>
        {
            var sy0 = hy * 2;
            var row0 = sy0 * srcW;
            var row1 = (sy0 + 1) * srcW;
            var dstRow = hy * dstW;
            for (var hx = 0; hx < dstW; hx++)
            {
                var sx0 = hx * 2;
                var sum = source[row0 + sx0] + source[row0 + sx0 + 1] + source[row1 + sx0] + source[row1 + sx0 + 1];
                dst[dstRow + hx] = (byte)((sum + 2) >> 2);
            }
        });
        return dst;
    }

    internal static byte[] DownsampleMobileRiverCentreHalfRes(
        byte[] source, int sourceWidth, int sourceHeight, out int halfWidth, out int halfHeight)
    {
        if (source is null || sourceWidth < 2 || sourceHeight < 2 || source.Length != sourceWidth * sourceHeight * 4)
        {
            halfWidth = sourceWidth;
            halfHeight = sourceHeight;
            return source;
        }

        halfWidth = sourceWidth / 2;
        halfHeight = sourceHeight / 2;
        var dst = new byte[halfWidth * halfHeight * 4];
        var dstW = halfWidth;
        var srcW = sourceWidth;

        Parallel.For(0, halfHeight, hy =>
        {
            var sy0 = hy * 2;
            var row0 = sy0 * srcW;
            var row1 = (sy0 + 1) * srcW;
            var dstRow = hy * dstW;
            for (var hx = 0; hx < dstW; hx++)
            {
                var sx0 = hx * 2;
                var i00 = (row0 + sx0) * 4;
                var i10 = (row0 + sx0 + 1) * 4;
                var i01 = (row1 + sx0) * 4;
                var i11 = (row1 + sx0 + 1) * 4;
                var chosen = i00;
                var bestImp = source[i00 + 2];
                if (source[i10 + 2] > bestImp) { bestImp = source[i10 + 2]; chosen = i10; }
                if (source[i01 + 2] > bestImp) { bestImp = source[i01 + 2]; chosen = i01; }
                if (source[i11 + 2] > bestImp) chosen = i11;
                var dstOff = (dstRow + hx) * 4;
                dst[dstOff] = source[chosen];
                dst[dstOff + 1] = source[chosen + 1];
                dst[dstOff + 2] = source[chosen + 2];
                dst[dstOff + 3] = source[chosen + 3];
            }
        });
        return dst;
    }


    private static Dictionary<int, TerritoryLabelAnchor> BuildRegionAnchorsDownsampled(
        ushort[] states, int width, int height)
    {
        const int scale = 4;
        var downW = Math.Max(1, width / scale);
        var downH = Math.Max(1, height / scale);
        var down = new ushort[downW * downH];
        for (var dy = 0; dy < downH; dy++)
        {
            var sRow = (dy * scale) * width;
            var dRow = dy * downW;
            for (var dx = 0; dx < downW; dx++)
                down[dRow + dx] = states[sRow + dx * scale];
        }
        return BuildTerritoryLabelAnchors(down, downW, downH, scale);
    }

    private static uint ParseHex(string value, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.TrimStart('#');
        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return fallback;
        return (rgb << 8) | 0xFFu;
    }
}
