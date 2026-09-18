using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SourceEarthData = Globality.EarthData;

namespace DealOrForce;

public readonly record struct GlobePick(
    Simulation.Nation Nation,
    Simulation.ScenarioState State,
    int ProvinceId,
    float Latitude,
    float Longitude
);

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
    private const float GlobeRadius = 5f;
    // One common, almost flush overlay shell. The previous 1.016-1.034 radii made
    // counters and battles visibly hover when the globe was rotated at close zoom.
    private const float SurfaceLineRadius = GlobeRadius * 1.0012f;
    private const float SurfaceMarkerRadius = GlobeRadius * 1.0022f;
    private const float SurfaceTextRadius = GlobeRadius * 1.0010f;
    private const float FlatLineZ = 0.004f;
    private const float FlatMarkerZ = 0.007f;
    private const float FlatTextZ = 0.009f;
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
    /// How big a division counter is drawn, and how that changes with the altitude.
    ///
    /// Counters are fixed-size: they hold the same number of pixels however far away the
    /// ground is. That is right for reading a map and wrong for working one - at
    /// operational altitude the counter is about the width of two provinces, which is fine
    /// because the provinces are dots, and at the new close zoom a province is three
    /// hundred pixels tall and the counter is still a thumbnail.
    ///
    /// So the size follows the camera down - but only over the last stretch of it. Growing
    /// across the whole operational range made counters large at the zoom a player actually
    /// works at, and a front is dozens of counters in adjacent provinces: they piled into
    /// each other and the map became unreadable at exactly the scale it has to be read.
    /// They stay at their normal size until the camera is nearly on the ground, and grow a
    /// third from there.
    /// </summary>
    private const float CounterPlatePixelSize = 0.000115f;
    private const float CounterTextPixelSize = 0.00025f;
    private const int CounterOutlineSize = 6;
    private const float CounterCloseScale = 1.35f;

    /// <summary>Above this the counters are simply their normal size.</summary>
    private const float CounterGrowthDistance = 6.6f;
    private const float CounterGrowthFlatZoom = 1.6f;

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
    private readonly List<(Node3D Node, Vector3 Normal)> _capitalMarkers = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _armyMarkers = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _fleetMarkers = new();
    private readonly Dictionary<Node3D, Simulation.Fleet> _fleetByMarker = new();
    private readonly Dictionary<int, (Node3D Node, Label3D Count, Sprite3D Plate)> _fleetNodes = new();
    private int _fleetRosterFingerprint = int.MinValue;
    private int _selectedFleetId = -1;
    private readonly Dictionary<Node3D, IReadOnlyList<Simulation.Army>> _armyByMarker = new();

    /// <summary>
    /// One counter on the map, and the handles into it.
    ///
    /// The node references are kept rather than looked up by name every frame: with a
    /// thousand counters on the map, three GetNodeOrNull calls each was three thousand
    /// string path resolutions a frame to write numbers that had usually not changed.
    /// </summary>
    private sealed class ArmyStackMarker
    {
        public Node3D Node;
        public Sprite3D Plate;
        public Label3D Count;
        public readonly List<Simulation.Army> Stack = new();
        public Vector3 Normal;
        public int NationId = -1;
        public string TemplateId = "";
        public string CountText = "";
        public bool Seen;
        public bool Compact;
        public bool FlatLayout;
        public bool LayoutInitialized;
    }

    private readonly Dictionary<(int, int, int, int), ArmyStackMarker> _armyStacks = new();
    private readonly List<(int, int, int, int)> _vanishedStacks = new();
    private readonly Dictionary<string, int> _templateVotes = new();
    private readonly HashSet<int> _selectedArmyIds = new();
    private readonly Dictionary<(int Nation, string Template), Texture2D> _armyCounterTextures = new();
    private readonly List<(Node3D Node, Vector3 Normal, int Cells)> _countryLabels = new();
    private readonly List<(Label3D Node, float Latitude, float Longitude, Basis SurfaceBasis, float FlatRotation)> _countryGlyphs = new();
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private Node3D _orbit = null!;
    private ShaderMaterial _globeMaterial = null!;
    private Node3D _capitalRoot = null!;
    private Texture2D _capitalTexture = null!;
    private Node3D _armyRoot = null!;
    private Node3D _fleetRoot = null!;
    private Texture2D _fleetTexture = null!;
    private Node3D _countryLabelRoot = null!;
    private ImmediateMesh _routeMesh = null!;
    private MeshInstance3D _routePreview = null!;
    private readonly ShaderMaterial[] _routeMaterials = new ShaderMaterial[3];
    private ImmediateMesh _frontMesh = null!;
    private MeshInstance3D _frontPreview = null!;
    private ImageTexture _controllerTexture;
    private ImageTexture _frontTexture;
    private int _controlRevision = -1;

    /// <summary>
    /// Who owned each province when the political raster was painted.
    ///
    /// The raster is built once and never rebuilt - it is five thousand pixels wide and
    /// repainting it would cost a visible stall every time a treaty is signed. Keeping what
    /// it was painted with lets the cheap 256x256 overlay say exactly which provinces have
    /// changed hands since, which is all the shader needs to draw them correctly.
    /// </summary>
    private ushort[] _paintedOwner;

    private byte[] _ownerPixels;
    private Image _ownerImage;
    private ImageTexture _ownerTexture;
    private int _frontRevision = -1;

    // The two overlay textures are rewritten whenever a province changes hands, which in a
    // world of a hundred and forty wars is most frames the game is running at speed. Each
    // rewrite was a quarter of a megabyte of pixels and a fresh Image on top of it, twice
    // over - a megabyte a frame handed to the collector for two textures that are always
    // the same size. The buffers and the images are made once and overwritten.
    private byte[] _controlPixels;
    private byte[] _frontPixels;
    private Image _controlImage;
    private Image _frontImage;
    private int _armyStructureFingerprint = int.MinValue;
    private int _routeFingerprint = int.MinValue;
    private readonly List<Vector3> _routePoints = new();
    private Texture2D _divisionTexture = null!;
    private Task<MapBuildResult> _mapTask;
    private MapBuildResult _pendingMapResult;
    private bool _mapFailureReported;
    private int _mapApplyStage;
    private double _militaryVisualAccumulator;
    private Simulation.ProvinceTopology _provinceTopology;
    private Simulation.Campaign _campaign;
    private bool _mapApplied;
    public bool IsMapPresentationReady => _mapApplied;
    public bool LoadingInputBlocked { get; set; }
    public string MapLoadingError { get; private set; } = "";
    public string MapLoadingStage => _pendingMapResult is null
        ? "Reading province and terrain maps"
        : _mapApplyStage < 5 ? "Uploading map layers"
        : _mapApplyStage == 5 ? "Preparing terrain" : "Placing armies and country names";
    // Stage completion, not a time estimate. Never pretend to measure the worker's bytes.
    public double MapLoadingProgress => _mapApplied ? 100 : _pendingMapResult is null ? 45 : 55 + _mapApplyStage * 6;
    private bool _dragging;
    private bool _boxSelecting;
    private bool _paintBoxing;
    private bool _paintBoxClaims;
    private Vector2 _boxStart;
    private Vector2 _boxCurrent;
    private float _dragDistance;
    private Vector2 _previousPointer;
    private float _yaw = -0.35f;
    private float _pitch = 0.30f;
    private float _targetYaw = -0.35f;
    private float _targetPitch = 0.30f;
    private float _distance = 12.5f;
    private float _targetDistance = 12.5f;

    public event Action<GlobePick> CountryPicked;
    public bool CountrySelectionOnly { get; set; }
    public event Action<Simulation.Army> ArmyPicked;
    public event Action<Simulation.Fleet> FleetPicked;
    public event Action<Simulation.LandBattle> BattlePicked;
    public event Action<IReadOnlyList<Simulation.Army>> ArmiesBoxPicked;
    public event Action<GlobePick> ProvinceCommanded;

    /// <summary>
    /// Painting a peace. While this is on, the map is a canvas: dragging the left button
    /// claims the provinces under the cursor and the right button gives them back, which is
    /// the same gesture a player already uses to draw a battle plan.
    /// </summary>
    public bool PaintMode { get; set; }

    /// <summary>A painted province, and whether the stroke was claiming or releasing it.</summary>
    public event Action<GlobePick, bool> ProvincePainted;

    /// <summary>
    /// A whole rectangle of ground, claimed or given back in one gesture.
    ///
    /// The brush is right for a border and wrong for a province of Siberia: painting the
    /// eastern half of a country one stroke at a time is a chore, and a chore in a peace
    /// conference is where a player stops negotiating and starts taking whatever is easiest
    /// to reach. Shift and a drag take everything inside the rectangle instead.
    /// </summary>
    public event Action<IReadOnlyList<int>, bool> ProvincesBoxPainted;

    private bool _painting;
    private bool _paintingClaims;
    private int _lastPaintedProvince = -1;
    private readonly HashSet<int> _peacePreview = new();
    private ushort _peacePreviewOwner;
    private readonly HashSet<int> _planPreview = new();
    private int _planPreviewKind;
    private int _planPreviewForeignId = -1;
    private ImageTexture _borderPlanTexture;
    private ImageTexture _borderControllerTexture;

    /// <summary>
    /// The stroke in progress. A drawn line only means anything if the player can see it
    /// while they are drawing it, so the brush writes into the same overlay the committed
    /// lines use and the map answers under the cursor rather than after an Apply.
    /// </summary>
    public void SetPlanPreview(IEnumerable<int> provinces, int kind)
    {
        _objectivePreview.Clear(); _departurePreview.Clear();
        _planPreviewForeignId = -1;
        _planPreview.Clear();
        if (provinces is not null) foreach (var province in provinces) _planPreview.Add(province);
        _planPreviewKind = kind;
        BuildFrontPreview();
    }

    public void SetBorderPlanPreview(IEnumerable<int> provinces, int foreignId)
    {
        _planPreview.Clear();
        _planPreview.UnionWith(provinces);
        _planPreviewKind = 0;
        _planPreviewForeignId = foreignId;
        BuildFrontPreview();
    }

    public void ClearPlanPreview()
    {
        _objectivePreview.Clear(); _departurePreview.Clear();
        _planPreviewForeignId = -1;
        if (_planPreview.Count == 0) return;
        _planPreview.Clear();
        BuildFrontPreview();
    }

    /// <summary>Redraw the overlay after a line was committed or dropped.</summary>
    public void RefreshBattlePlans() => BuildFrontPreview();

    /// <summary>
    /// Show a demand on the map as the border it would produce. The provinces are drawn in
    /// the claimant's own colour, so the player is looking at the peace rather than at a
    /// list describing one.
    /// </summary>
    public void SetPeacePreview(IEnumerable<int> provinces, int ownerMapIndex)
    {
        _peacePreview.Clear();
        if (provinces is not null) foreach (var province in provinces) _peacePreview.Add(province);
        _peacePreviewOwner = (ushort)ownerMapIndex;
        RefreshProvinceControllers();
    }

    public void ClearPeacePreview()
    {
        if (_peacePreview.Count == 0) return;
        _peacePreview.Clear();
        RefreshProvinceControllers();
    }

    public void BindCampaign(Simulation.Campaign campaign)
    {
        _campaign = campaign;
        var repairs = campaign.ScenarioId == "2024" ? Simulation.TerritorialTopology.Apply(
            campaign,
            SourceEarthData.Control,
            SourceEarthData.States,
            SourceEarthData.Width,
            SourceEarthData.Height) : Array.Empty<Simulation.TerritorialTopology.RepairSummary>();
        foreach (var repair in repairs)
        {
            if (repair.ChangedCells > 0)
                GD.Print($"[dof] topology repair {repair.SubjectTag}->{repair.NeighborTag}: {repair.ChangedCells} cells");
            if (repair.ConnectedComponents > 0)
                GD.Print($"[dof] topology region {repair.SubjectTag}: {repair.ConnectedComponents} connected component(s)");
        }

        _colorByMapIndex.Clear();
        _nationByMapIndex.Clear();
        _stateByMapIndex.Clear();

        foreach (var nation in campaign.Nations)
        {
            _colorByMapIndex[nation.MapIndex] = ParseHex(nation.Color, 0x6E7B8BFFu);
            _nationByMapIndex[nation.MapIndex] = nation;
        }

        foreach (var state in campaign.States)
            _stateByMapIndex[state.MapIndex] = state;
        _stateIndexRevision = campaign.MapRevision;
        _nationIndexCount = campaign.Nations.Count;

        BuildNationPalette();
        ApplyMapSmoothing();
        BuildCapitalMarkers(campaign);
        ReconcileArmyMarkers(campaign);
        BuildFleetMarkers(campaign);
        if (campaign.Player is { } player && !string.IsNullOrWhiteSpace(player.CapitalName))
            FocusOn(player.CapitalLatitude, player.CapitalLongitude, 15.8f);

        if (_mapTask is null)
        {
            var colors = new Dictionary<int, uint>(_colorByMapIndex);
            _mapTask = Task.Run(() => BuildPoliticalMap(colors));
            return;
        }

        // A second campaign in the same session, which is what returning to the menu and
        // starting again produces.
        //
        // The map is built once and applied once, and everything that happens in that apply
        // - the province topology reaching the campaign, and with it the deployment of every
        // army in the world - happened to the FIRST campaign and to nothing else. A campaign
        // started afterwards got no province map at all: no fronts, no movement, no combat,
        // and every division in the world still standing on the capital it was created at,
        // which is what a hundred stacked counters on Moscow actually were.
        //
        // Nothing needs rebuilding. The raster is the same raster; what has to happen is
        // that this campaign is given it, in the state the scenario authored rather than the
        // state the last campaign left it in.
        if (!_mapApplied || _provinceTopology is null) return;
        _provinceTopology.RestoreAuthoredBorders();
        BindArmyTopology();
        ReconcileArmyMarkers(campaign);
        BuildFrontPreview();
        // The overlay is keyed to a map revision, and the new campaign's starts again at a
        // number this has already drawn. Forcing a repaint is what stops the second campaign
        // wearing the first one's borders.
        _controlRevision = -1;
        _frontRevision = -1;
        RefreshProvinceControllers();
    }

    public override void _Ready()
    {
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _viewport = new SubViewport
        {
            TransparentBg = true,
            HandleInputLocally = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Size = ViewportSize()
        };
        AddChild(_viewport);
        GetViewport().SizeChanged += ResizeViewport;

        var world = new Node3D { Name = "GlobeWorld" };
        _viewport.AddChild(world);

        var environment = new WorldEnvironment { Environment = new Godot.Environment() };
        environment.Environment.BackgroundMode = Godot.Environment.BGMode.Color;
        environment.Environment.BackgroundColor = Ui.Colour("globe.space");
        environment.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.Environment.AmbientLightColor = Ui.Colour("globe.ambient");
        environment.Environment.AmbientLightEnergy = 0.72f;
        world.AddChild(environment);

        var sun = new DirectionalLight3D
        {
            LightEnergy = 1.15f,
            RotationDegrees = new Vector3(-32f, -38f, 0f),
            ShadowEnabled = true
        };
        world.AddChild(sun);

        _orbit = new Node3D { Name = "Orbit" };
        world.AddChild(_orbit);

        _camera = new Camera3D
        {
            Current = true,
            Fov = RigFieldOfView,
            HOffset = 0f,
            Near = 0.05f,
            Far = 250f
        };
        _orbit.AddChild(_camera);

        _spaceNode = CreateSpace(world);
        CreateGlobe(world);
        CreateFlatMap(world);
        _capitalRoot = new Node3D { Name = "CapitalMarkers" };
        world.AddChild(_capitalRoot);
        _capitalTexture = GD.Load<Texture2D>(Simulation.SpriteCatalog.TextureOf("globe_capital_star",
            "res://game/gfx/globe/globe_capital_star.png"));
        _armyRoot = new Node3D { Name = "ArmyMarkers" };
        world.AddChild(_armyRoot);
        _divisionTexture = GD.Load<Texture2D>(Simulation.SpriteCatalog.TextureOf("globe_division_marker",
            "res://game/gfx/globe/globe_division_marker.png"));
        _fleetRoot = new Node3D { Name = "FleetMarkers" };
        world.AddChild(_fleetRoot);
        _fleetTexture = CreateFleetMarkerTexture();
        CreateMilitaryOverlays(world);
        _countryLabelRoot = new Node3D { Name = "CountryLabels" };
        world.AddChild(_countryLabelRoot);
        CreateRoutePreview(world);
        CreateFrontPreview(world);
        ApplyCamera();
    }

    public override void _Process(double delta)
    {
        if (!LoadingInputBlocked)
        {
            if (_freeCamera) UpdateFreeCamera(delta);
            else UpdateOrbitCamera(delta);
        }

        // Keep the authored political raster legible at strategic distance. The old
        // quarter-resolution layer supplied almost forty percent of the default globe,
        // which made country silhouettes and coastlines look out of focus. A small
        // mipmapped contribution remains to suppress shimmer while the full authored
        // layer carries the actual line work.
        var detailMix = _flatMap
            ? Mathf.Clamp((13f - _flatZoom) / 4.8f, 0.15f, 1f)
            : Mathf.Clamp((17f - _distance) / 5.5f, 0.12f, 1f);
        var provinceDetail = _flatMap
            ? Mathf.Clamp((6.8f - _flatZoom) / 2.6f, 0f, 1f)
            : Mathf.Clamp((9.3f - _distance) / 2.8f, 0f, 1f);
        _globeMaterial?.SetShaderParameter("detail_mix", detailMix);
        // Tactical boundaries should emerge only at operational zoom. Keeping them
        // faint at country-selection scale avoids turning the map into a crosshatch.
        _globeMaterial?.SetShaderParameter("province_detail", provinceDetail);
        _globeMaterial?.SetShaderParameter("province_border_pixels", Math.Clamp(Simulation.InterfaceSkin.Metric("map.province_border_width", 2.0f), 0.5f, 4f));
        _globeMaterial?.SetShaderParameter("province_border_opacity", Math.Clamp(Simulation.InterfaceSkin.Metric("map.province_border_opacity", 0.72f), 0f, 1f));
        _globeMaterial?.SetShaderParameter("province_border_halo", Math.Clamp(Simulation.InterfaceSkin.Metric("map.province_border_halo", 0.15f), 0f, 0.4f));
        var fogViewer = FogOfWarActive && _campaign?.Player is { IsHuman: true } ? _campaign.Player.Id : -1;
        if (_fogViewer != fogViewer) { _fogViewer = fogViewer; _lastMapModeDay = -100; }
        _globeMaterial?.SetShaderParameter("fog_viewer", FogOfWarActive && _campaign?.Player is { IsHuman: true } ? _campaign.Player.MapIndex : -1);
        UpdateFogTint();
        UpdateFogVisibility();
        UpdateMarkerVisibility(_capitalRoot, _capitalMarkers, ViewDistance <= 13.8f);
        var armyLod = ViewDistance <= 10.8f;
        _militaryVisualAccumulator += delta;
        if (MilitaryMarkersEnabled && _mapApplied && _campaign is not null && _militaryVisualAccumulator >= 0.12)
        {
            _militaryVisualAccumulator = 0;
            using (Simulation.SimProfiler.Measure("f:army sync")) SyncArmyMarkers();
            using (Simulation.SimProfiler.Measure("f:army look")) UpdateMilitaryPresentation();
        }
        using (Simulation.SimProfiler.Measure("f:army vis"))
        {
            UpdateCounterScale();
            UpdateMarkerVisibility(_armyRoot, _armyMarkers, armyLod && MilitaryMarkersEnabled);
            if (_flatMap && armyLod && MilitaryMarkersEnabled) UpdateFlatCounterDensity();
        }
        if (MilitaryMarkersEnabled)
            using (Simulation.SimProfiler.Measure("f:fleets")) UpdateFleetMarkers();
        UpdateMarkerVisibility(_fleetRoot, _fleetMarkers, MilitaryMarkersEnabled && ViewDistance <= 13.5f);
        if (IsInstanceValid(_battleRoot)) _battleRoot.Visible = MilitaryMarkersEnabled;
        using (Simulation.SimProfiler.Measure("f:routes")) UpdateRoutePreview();
        if (_mapApplied && _campaign is not null && _controlRevision != _campaign.MapRevision)
            using (Simulation.SimProfiler.Measure("f:map paint")) RefreshProvinceControllers();
        if (_mapApplied && _campaign is not null &&
            (_labelTask is not null || _labelRevision != _campaign.MapRevision))
            using (Simulation.SimProfiler.Measure("f:label place")) RefreshCountryLabelPlacement();
        if (IsInstanceValid(_frontPreview)) _frontPreview.Visible = MilitaryMarkersEnabled && _frontMesh.GetSurfaceCount() > 0;
        using (Simulation.SimProfiler.Measure("f:labels")) UpdateCountryLabelVisibility();
        if (_flatMap)
            using (Simulation.SimProfiler.Measure("f:flat positions")) UpdateFlatWorldPositions();
        UpdateMapModePaletteIfNeeded();

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

    /// <summary>
    /// GPU uploads are deliberately split across frames. Uploading the political,
    /// region, province, nation and hydrology rasters in one frame used to make the
    /// window look hung for several seconds even though the data worker had finished.
    /// </summary>
    private void ApplyPoliticalMapStage()
    {
        var result = _pendingMapResult;
        switch (_mapApplyStage++)
        {
            case 0:
            {
                // Built, downscaled, and then thrown away. Nothing samples the full-resolution
                // political raster any more - the fill is rebuilt from the country id map -
                // so uploading it would cost sixty-six megabytes of texture to be read by
                // nobody. That matters most on the hardware this game wants to reach.
                var crisp = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.Rgba8, result.PoliticalPixels);
                // One third of the authored width matches a 1920 px viewport. That is
                // still compact enough to mipmap cheaply, but unlike the previous
                // 1440x720 quarter copy it does not discard visible coastline detail.
                var smooth = crisp.Duplicate() as Image;
                smooth.Resize(MapWidth / 3, MapHeight / 3, Image.Interpolation.Lanczos);
                smooth.GenerateMipmaps();
                _globeMaterial.SetShaderParameter("political_map", ImageTexture.CreateFromImage(smooth));
                result.PoliticalPixels = null;
                break;
            }
            case 1:
                _globeMaterial.SetShaderParameter("region_ids", ImageTexture.CreateFromImage(Image.CreateFromData(
                    MapWidth, MapHeight, false, Image.Format.Rg8, result.RegionIds)));
                result.RegionIds = null;
                break;
            case 2:
                _globeMaterial.SetShaderParameter("province_ids", ImageTexture.CreateFromImage(Image.CreateFromData(
                    MapWidth, MapHeight, false, Image.Format.Rg8, result.ProvinceIds)));
                result.ProvinceIds = null;
                break;
            case 3:
                _globeMaterial.SetShaderParameter("nation_ids", ImageTexture.CreateFromImage(Image.CreateFromData(
                    MapWidth, MapHeight, false, Image.Format.Rg8, result.NationIds)));
                result.NationIds = null;
                break;
            case 4:
                _globeMaterial.SetShaderParameter("hydrology_map", ImageTexture.CreateFromImage(Image.CreateFromData(
                    MapWidth, MapHeight, false, Image.Format.Rg8, result.HydrologyPixels)));
                result.HydrologyPixels = null;
                break;
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
                BindArmyTopology();
                ReconcileArmyMarkers(_campaign);
                BuildCountryLabels(result.LabelAnchors);
                BuildFrontPreview();
                RefreshProvinceControllers();
                _pendingMapResult = null;

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
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GD.Print($"[dof] map applied; managed heap {GC.GetTotalMemory(false) / (1024 * 1024):N0} MB");
                GD.Print($"[dof] tactical province topology ready: {_provinceTopology.ProvinceCount:N0} land provinces, " +
                         $"{_provinceTopology.RiverCrossingCount:N0} river crossings, " +
                         $"{Simulation.HydrologyCatalog.RiverFeatures.Count:N0} rivers and {Simulation.HydrologyCatalog.LakeFeatures.Count:N0} lakes");
                _mapApplied = true;
                break;
        }
    }

    /// <summary>Frames a geographic point exactly as the source globe's country focus does.</summary>
    public void FocusOn(float latitude, float longitude, float distance = 8f)
    {
        if (_flatMap)
        {
            _targetFlatX = longitude / 18f;
            _targetFlatY = latitude / 18f;
            _targetFlatZoom = FlatZoomForDistance(distance);
            ClampFlatCamera();
            return;
        }
        _targetPitch = Mathf.Clamp(-Mathf.DegToRad(latitude), -1.20f, 1.20f);
        _targetYaw = Mathf.DegToRad(longitude + 180f);
        _targetDistance = Mathf.Clamp(distance, MinimumDistance, MaximumDistance);
    }

    /// <summary>
    /// The brush. A press starts a stroke, motion continues it, and a release ends it;
    /// each province is reported once per stroke so dragging slowly does not send the same
    /// province forty times. Returns true when the event was a stroke, so ordinary picking
    /// and camera dragging stay out of the way while a peace is being drawn.
    /// </summary>
    private bool HandlePaintInput(InputEvent inputEvent)
    {
        // Both mouse buttons are the brush here, which leaves nothing to move the map with -
        // and the ground being argued over can be on the other side of the planet from
        // whatever the camera happens to be pointed at. The middle button drags instead.
        // WASD keeps working throughout, because it is polled rather than routed, so there
        // is never a settlement the player can price but not look at.
        if (inputEvent is InputEventMouseButton pan && pan.ButtonIndex == MouseButton.Middle)
        {
            _dragging = pan.Pressed;
            _previousPointer = pan.Position;
            AcceptEvent();
            return true;
        }
        if (inputEvent is InputEventMouseMotion panning && _dragging && !_painting)
        {
            DragCamera(panning.Position - _previousPointer);
            _previousPointer = panning.Position;
            AcceptEvent();
            return true;
        }

        // Shift turns the brush into a rectangle. Left takes what is inside it, right gives
        // it back - the same two hands as the brush, so nothing new has to be learned.
        if (inputEvent is InputEventMouseButton boxButton &&
            boxButton.ButtonIndex is MouseButton.Left or MouseButton.Right &&
            (boxButton.ShiftPressed || _paintBoxing))
        {
            if (boxButton.Pressed && boxButton.ShiftPressed)
            {
                _paintBoxing = true;
                _paintBoxClaims = boxButton.ButtonIndex == MouseButton.Left;
                _boxSelecting = true;
                _boxStart = boxButton.Position;
                _boxCurrent = boxButton.Position;
                QueueRedraw();
            }
            else if (_paintBoxing && !boxButton.Pressed)
            {
                _boxCurrent = boxButton.Position;
                _paintBoxing = false;
                _boxSelecting = false;
                var claimed = ProvincesInScreenRect(RectFromPoints(_boxStart, _boxCurrent));
                if (claimed.Count > 0) ProvincesBoxPainted?.Invoke(claimed, _paintBoxClaims);
                QueueRedraw();
            }
            AcceptEvent();
            return true;
        }
        if (inputEvent is InputEventMouseMotion boxDrag && _paintBoxing)
        {
            _boxCurrent = boxDrag.Position;
            QueueRedraw();
            AcceptEvent();
            return true;
        }

        if (inputEvent is InputEventMouseButton button &&
            button.ButtonIndex is MouseButton.Left or MouseButton.Right)
        {
            if (button.Pressed)
            {
                _painting = true;
                _paintingClaims = button.ButtonIndex == MouseButton.Left;
                _lastPaintedProvince = -1;
                Paint(button.Position);
            }
            else
            {
                _painting = false;
                _lastPaintedProvince = -1;
            }
            AcceptEvent();
            return true;
        }

        if (inputEvent is InputEventMouseMotion motion && _painting)
        {
            Paint(motion.Position);
            AcceptEvent();
            return true;
        }

        return false;
    }

    private void Paint(Vector2 pixel)
    {
        if (!TryPick(pixel, out var pick, updateSelection: false)) return;
        if (pick.ProvinceId <= 0 || pick.ProvinceId == _lastPaintedProvince) return;
        _lastPaintedProvince = pick.ProvinceId;
        ProvincePainted?.Invoke(pick, _paintingClaims);
    }

    private bool TryPick(Vector2 pixel, out GlobePick pick, bool updateSelection = true)
    {
        pick = default;
        if (!IsInstanceValid(_camera)) return false;

        var origin = _camera.ProjectRayOrigin(pixel);
        var direction = _camera.ProjectRayNormal(pixel).Normalized();
        float latitude;
        float longitude;
        if (_flatMap)
        {
            if (Mathf.Abs(direction.Z) < 0.0001f) return false;
            var rayDistance = -origin.Z / direction.Z;
            if (rayDistance < 0f) return false;
            var surface = origin + direction * rayDistance;
            if (surface.Y < -FlatMapHeight * 0.5f || surface.Y > FlatMapHeight * 0.5f) return false;
            longitude = surface.X / FlatMapWidth * 360f;
            latitude = surface.Y / FlatMapHeight * 180f;
        }
        else
        {
            var b = origin.Dot(direction);
            var c = origin.LengthSquared() - GlobeRadius * GlobeRadius;
            var discriminant = b * b - c;
            if (discriminant < 0f) return false;
            var rayDistance = -b - Mathf.Sqrt(discriminant);
            if (rayDistance < 0f) return false;
            var surface = (origin + direction * rayDistance).Normalized();
            latitude = Mathf.RadToDeg(Mathf.Asin(surface.Y));
            longitude = Mathf.RadToDeg(Mathf.Atan2(surface.X, surface.Z)) - 180f;
        }
        while (longitude <= -180f) longitude += 360f;
        while (longitude > 180f) longitude -= 360f;

        var x = Mathf.Clamp((int)((longitude + 180f) / 360f * SourceEarthData.Width), 0, SourceEarthData.Width - 1);
        var y = Mathf.Clamp((int)((90f - latitude) / 180f * SourceEarthData.Height), 0, SourceEarthData.Height - 1);
        var cell = y * SourceEarthData.Width + x;
        var provinceId = _provinceTopology is null ? -1 : _provinceTopology.CellToProvince[cell];
        var nationMapIndex = provinceId > 0 && _campaign?.ProvinceController is { } liveControllers
            ? liveControllers[provinceId]
            : SourceEarthData.Control[cell];
        if (nationMapIndex == 0 || !_nationByMapIndex.TryGetValue(nationMapIndex, out var nation))
            return false;

        // The live region, not the authored one. `SourceEarthData.States` is the raster as
        // it was drawn: it still says the whole of Luhansk is one region long after a border
        // has cut it in two, so reading the panel off it answers about a region the player
        // is not standing in - which is how clicking on ground Russia holds produced
        // Ukraine's fog of war.
        var regionMapIndex = provinceId > 0 && _campaign?.ProvinceMap is { } liveRegions &&
                             provinceId < liveRegions.ProvinceToState.Length
            ? liveRegions.ProvinceToState[provinceId]
            : SourceEarthData.States[cell];
        EnsureStateIndex();
        _stateByMapIndex.TryGetValue(regionMapIndex, out var state);
        if (updateSelection)
        {
            _globeMaterial.SetShaderParameter("selected_region_id", (float)SourceEarthData.States[cell]);
            _globeMaterial.SetShaderParameter("selected_province_id", (float)provinceId);
            _globeMaterial.SetShaderParameter("selection_active", 1f);
        }
        pick = new GlobePick(nation, state, provinceId, latitude, longitude);
        return true;
    }

    /// <summary>
    /// How far away the camera is, on a scale both projections agree on.
    ///
    /// A dozen visibility thresholds in this file are written as "closer than 13.5", and
    /// several of them were reading `_distance` - the orbit rig's distance - while the flat
    /// map was the thing on screen. `_distance` does not change in flat mode: the wheel
    /// drives `_flatZoom` instead. So whether the flat map showed its capital markers and
    /// its fleets depended on how far out the globe happened to be when the player left it,
    /// and no amount of zooming in on the flat map would bring them back.
    ///
    /// Rather than write `_flatMap ? a : b` at each site and let the pairs drift out of step
    /// again, there is one conversion. Its two constants are read off the two thresholds
    /// somebody had already tuned by eye in both projections - army counters at 7.4 flat
    /// against 10.8 orbital, battle markers at 9.4 against 14.0 - so the line through them is
    /// the game's own answer to what a flat zoom is worth in orbital distance, rather than a
    /// number I chose. The two pairs that calibrated it come out unchanged, which is the
    /// arithmetic checking itself.
    /// </summary>
    private float ViewDistance => _flatMap ? _flatZoom * 1.6f - 1.04f : _distance;

    private float _counterScale = 1f;
    private float _counterWorldPixel;
    public bool MilitaryMarkersEnabled { get; set; } = true;

    private bool _mapSmoothing = Simulation.PlayerSettings.Bool("map_smoothing", true);

    /// <summary>
    /// Whether the map is smoothed, or shown as the raster it is.
    ///
    /// Off is a different picture rather than a worse one: the ids sampled at the nearest
    /// texel, the ground switching at the texel edge, and the grid visible - which is a
    /// legitimate thing to want on a game whose data is meant to be legible to the person
    /// modding it. It is also the cheaper picture: the noise that takes the boundaries off
    /// the grid is not evaluated, and the ground costs one texture fetch instead of two.
    /// </summary>
    public bool MapSmoothing
    {
        get => _mapSmoothing;
        set
        {
            if (_mapSmoothing == value) return;
            _mapSmoothing = value;
            Simulation.PlayerSettings.Set("map_smoothing", value);
            ApplyMapSmoothing();
        }
    }

    private void ApplyMapSmoothing() =>
        _globeMaterial?.SetShaderParameter("map_smoothing", _mapSmoothing ? 1f : 0f);

    private bool _fogOfWarActive;
    private int _fogViewer = -1;
    public bool FogOfWarActive
    {
        get => _fogOfWarActive;
        set
        {
            if (_fogOfWarActive == value) return;
            _fogOfWarActive = value;
            _lastMapModeDay = -100;
            _battleMarkerDay = -1;
        }
    }
    private bool CanShowMilitary(int nationId) => !FogOfWarActive || Simulation.CampaignIntel.PlayerCanInspect(_campaign, nationId);

    private void SizeArmyCounter(ArmyStackMarker marker)
    {
        if (marker.Plate is null || marker.Count is null) return;
        // PixelSize/OutlineSize rebuild Label3D meshes. Never animate those properties
        // on every orthographic zoom frame for the entire world's counters.
        if (!marker.LayoutInitialized || marker.FlatLayout != _flatMap)
        {
            marker.LayoutInitialized = true;
            marker.FlatLayout = _flatMap;
            marker.Plate.FixedSize = !_flatMap;
            marker.Count.FixedSize = !_flatMap;
            marker.Plate.PixelSize = _flatMap ? 52f / 176f : CounterPlatePixelSize * _counterScale;
            marker.Count.PixelSize = _flatMap ? .60f : CounterTextPixelSize * _counterScale;
            marker.Count.OutlineSize = _flatMap ? 2 : Mathf.RoundToInt(CounterOutlineSize * _counterScale);
            marker.Count.Position = _flatMap ? new Vector3(10f, -1f, 4f) : new Vector3(0f, -.004f, .004f);
            marker.Compact = false;
            marker.Plate.Scale = Vector3.One;
            marker.Plate.Visible = true;
            marker.Count.Visible = true;
        }
        if (_flatMap) marker.Node.Scale = Vector3.One * _counterWorldPixel;
        else
        {
            marker.Node.Scale = Vector3.One;
            marker.Plate.PixelSize = CounterPlatePixelSize * _counterScale;
            marker.Count.PixelSize = CounterTextPixelSize * _counterScale;
            marker.Count.OutlineSize = Mathf.RoundToInt(CounterOutlineSize * _counterScale);
        }
    }

    /// <summary>
    /// Resize the counters when the camera has moved far enough to matter.
    ///
    /// Twelve hundred markers is too many to touch every frame for a size that only
    /// changes while the player is turning the wheel, so nothing happens until the factor
    /// has moved a few percent. A zoom gesture costs a handful of passes rather than one
    /// per frame.
    /// </summary>
    private void UpdateCounterScale()
    {
        var wanted = _flatMap
            ? Mathf.Lerp(CounterCloseScale, 1f,
                Mathf.InverseLerp(MinimumFlatZoom, CounterGrowthFlatZoom, _flatZoom))
            : Mathf.Lerp(CounterCloseScale, 1f,
                Mathf.InverseLerp(MinimumDistance, CounterGrowthDistance, _distance));
        wanted = Mathf.Clamp(wanted, 1f, CounterCloseScale);
        var worldPixel = _flatMap ? _camera.Size / Math.Max(1f, _camera.GetViewport().GetVisibleRect().Size.Y) : 0f;
        if (Mathf.Abs(wanted - _counterScale) < 0.03f &&
            Math.Abs(worldPixel - _counterWorldPixel) <= Math.Max(.0000001f, worldPixel * .01f)) return;
        _counterScale = wanted;
        _counterWorldPixel = worldPixel;

        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node)) continue;
            SizeArmyCounter(marker);
        }
    }

    private bool TryPickArmyStack(Vector2 pixel, out IReadOnlyList<Simulation.Army> armies, bool selectableOnly = true)
    {
        armies = null;
        if (!MilitaryMarkersEnabled || (!_flatMap && _distance > 13.5f) || !IsInstanceValid(_camera)) return false;
        // A bigger counter is a bigger target: the radius that finds it follows the size
        // it is actually drawn at, or clicking a close-up counter near its edge misses.
        var reach = _flatMap ? 26f : 28f * _counterScale;
        var bestDistance = reach * reach;
        var expandedHit = false;
        foreach (var marker in _armyStacks.Values)
        {
            var node = marker.Node;
            var candidates = marker.Stack;
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (!node.IsVisibleInTree()) continue;
            var screen = _camera.UnprojectPosition(node.GlobalPosition);
            var distance = screen.DistanceSquaredTo(pixel);
            if (_flatMap)
            {
                if (marker.Compact)
                {
                    if (expandedHit || distance > 64f) continue;
                }
                else
                {
                    var offset = screen - pixel;
                    if (Mathf.Abs(offset.X) > 27f || Mathf.Abs(offset.Y) > 17f) continue;
                    // A pin behind a readable counter must not steal that counter's click.
                    if (!expandedHit) { expandedHit = true; bestDistance = float.MaxValue; }
                }
            }
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            // The counter still blocks a click on the province behind it, but foreign
            // formations never enter selection or expose their detailed army tooltip.
            armies = selectableOnly
                ? candidates.Where(army => Simulation.ArmyInteraction.CanSelect(_campaign, army)).ToArray()
                : candidates;
        }
        return armies is not null;
    }

    public void SelectArmy(int armyId)
    {
        SelectArmies(new[] { armyId });
    }

    public void SelectArmies(IEnumerable<int> armyIds)
    {
        var selected = armyIds.ToHashSet();
        if (_campaign is null) selected.Clear();
        else selected.IntersectWith(_campaign.Armies
            .Where(army => Simulation.ArmyInteraction.CanSelect(_campaign, army)).Select(army => army.Id));
        _selectedArmyIds.Clear();
        _selectedArmyIds.UnionWith(selected);
        _nextCounterDensityMs = 0;
        _routeFingerprint = int.MinValue;
        foreach (var (node, armies) in _armyByMarker)
        {
            if (!IsInstanceValid(node)) continue;
            var plate = node.GetChildOrNull<Sprite3D>(0);
            if (plate is null) continue;
            plate.Modulate = armies.Any(army => selected.Contains(army.Id))
                ? new Color(1f, 0.86f, 0.42f, 1f)
                : Colors.White;
        }
    }

    public override void _Draw()
    {
        if (!_boxSelecting) return;
        var rect = RectFromPoints(_boxStart, _boxCurrent);
        DrawRect(rect, new Color(0.91f, 0.77f, 0.31f, 0.12f), true);
        DrawRect(rect, new Color(0.96f, 0.82f, 0.38f, 0.92f), false, 2f);
    }

    private void CreateRoutePreview(Node3D world)
    {
        _routeMesh = new ImmediateMesh();
        CreateOrderMaterials();
        _routePreview = new MeshInstance3D
        {
            Name = "SelectedArmyRoute",
            Mesh = _routeMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        world.AddChild(_routePreview);
    }

    private void CreateFrontPreview(Node3D world)
    {
        _frontMesh = new ImmediateMesh();
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            NoDepthTest = false,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };
        _frontPreview = new MeshInstance3D
        {
            Name = "ActiveWarFronts",
            Mesh = _frontMesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        world.AddChild(_frontPreview);
    }

    private void BuildFrontPreview()
    {
        if (_campaign?.ProvinceMap is null || _globeMaterial is null) return;
        _frontMesh.ClearSurfaces();
        _frontPreview.Visible = false;
        var pixels = _frontPixels ??= new byte[256 * 256 * 4];
        var borderTargets = new byte[256 * 256 * 4];
        var controllers = new byte[256 * 256 * 2];
        var liveControl = _campaign.ProvinceController ?? _campaign.ProvinceMap.ProvinceToNation;
        for (var p = 1; p < liveControl.Length; p++)
        {
            controllers[p * 2] = (byte)(liveControl[p] & 255);
            controllers[p * 2 + 1] = (byte)(liveControl[p] >> 8);
        }
        Array.Clear(pixels);
        foreach (var war in _campaign.Wars)
        {
            if (war.Ended) continue;
            var warCode = (byte)(war.Id % 254 + 1);
            foreach (var province in war.AttackerFront)
            {
                var write = province * 4;
                pixels[write] = warCode;
                pixels[write + 1] = 85;
            }
            foreach (var province in war.DefenderFront)
            {
                var write = province * 4;
                pixels[write] = warCode;
                pixels[write + 1] = 170;
            }
        }

        // The blue channel carries what the player drew: which kind of line a province
        // belongs to, committed lines first and the stroke in progress painted over them,
        // so correcting a line reads immediately under the brush.
        foreach (var line in _campaign.FrontLines)
        {
            if (line.NationId != _campaign.Player?.Id) continue;
            Simulation.BorderFronts.Refresh(_campaign, line);
            if (line.Kind == Simulation.FrontLineKind.Offensive && line.ObjectiveSequence.Count > 0)
            {
                foreach (var p in line.SourceProvinces)
                {
                    pixels[p * 4 + 2] = 64;
                    PutBorderTarget(borderTargets, p, line.NationId, line.BorderNationId);
                }
                if (_objectivePreview.Count == 0 || !_selectedArmyIds.Any(id => line.ArmyIds.Contains(id)))
                    DrawOffensiveGeometry(line.ObjectiveSequence, line.SourceProvinces, false);
                continue;
            }
            var code = PlanCode(line.Kind);
            foreach (var province in line.Provinces)
            {
                if (province <= 0 || province >= 256 * 256) continue;
                pixels[province * 4 + 2] = code;
                if (line.Kind == Simulation.FrontLineKind.Defence)
                    PutBorderTarget(borderTargets, province, line.NationId, line.BorderNationId);
            }
        }
        if (_objectivePreview.Count > 0) DrawOffensiveGeometry(_objectivePreview, _departurePreview, true);
        if (_planPreview.Count > 0 && _objectivePreview.Count == 0)
        {
            var code = (byte)(Mathf.Clamp(_planPreviewKind + 1, 1, 3) * 64);
            foreach (var province in _planPreview)
            {
                if (province <= 0 || province >= 256 * 256) continue;
                pixels[province * 4 + 2] = code;
                if (_planPreviewForeignId >= 0)
                    PutBorderTarget(borderTargets, province, _campaign.Player.Id, _planPreviewForeignId);
            }
        }

        UpdateBorderTexture(ref _borderPlanTexture, borderTargets, Image.Format.Rgba8, "border_plan_targets");
        _frontPreview.Visible = _frontMesh.GetSurfaceCount() > 0;
        UpdateBorderTexture(ref _borderControllerTexture, controllers, Image.Format.Rg8, "border_controller_ids");

        if (_frontTexture is null)
        {
            _frontImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
            _frontTexture = ImageTexture.CreateFromImage(_frontImage);
            _globeMaterial.SetShaderParameter("front_membership", _frontTexture);
        }
        else
        {
            _frontImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
            _frontTexture.Update(_frontImage);
        }
    }

    private static byte PlanCode(Simulation.FrontLineKind kind) => kind switch
    {
        Simulation.FrontLineKind.Defence => 64,
        Simulation.FrontLineKind.Offensive => 128,
        Simulation.FrontLineKind.AreaDefence => 224,
        _ => 192
    };

    private void PutBorderTarget(byte[] pixels, int province, int ownerId, int foreignId)
    {
        var own = _campaign.NationById(ownerId)?.MapIndex ?? 0;
        var other = _campaign.NationById(foreignId)?.MapIndex ?? 0;
        var write = province * 4;
        pixels[write] = (byte)(own & 255); pixels[write + 1] = (byte)(own >> 8);
        pixels[write + 2] = (byte)(other & 255); pixels[write + 3] = (byte)(other >> 8);
    }

    private void UpdateBorderTexture(ref ImageTexture texture, byte[] pixels, Image.Format format, string uniform)
    {
        using var image = Image.CreateFromData(256, 256, false, format, pixels);
        if (texture is null)
        {
            texture = ImageTexture.CreateFromImage(image);
            _globeMaterial.SetShaderParameter(uniform, texture);
        }
        else texture.Update(image);
    }

    /// <summary>
    /// One clean textured arrow per selected division. The old sprite atlas contained several
    /// strokes inside every arrow and made even one order look like a bundle of routes. The
    /// ribbon is now single-stroke geometry, while separate divisions keep separate routes.
    /// </summary>
    private void UpdateRoutePreview()
    {
        if (_campaign?.ProvinceMap is null || !IsInstanceValid(_routePreview)) return;

        var fingerprint = HashCode.Combine(_selectedArmyIds.Count, _campaign.Player?.Id ?? -1);
        if (_selectedArmyIds.Count > 0)
            foreach (var candidate in _campaign.Armies)
            {
                if (!_selectedArmyIds.Contains(candidate.Id) || candidate.NationId != _campaign.Player?.Id) continue;
                fingerprint = HashCode.Combine(fingerprint, candidate.Id, candidate.ProvinceId,
                    candidate.Route.Count, candidate.PlannedRoute.Count,
                    candidate.PlannedRoute.Count == 0 ? -1 : candidate.PlannedRoute[^1],
                    candidate.OrderKind, candidate.OrderProvinceId);
                fingerprint = HashCode.Combine(fingerprint,
                    candidate.PlanActive, candidate.SupportOnly,
                    candidate.OrderRouteSteps, candidate.OrderStepsCompleted,
                    Mathf.RoundToInt(candidate.MoveProgress * 20f));
            }
        if (fingerprint == _routeFingerprint) return;
        _routeFingerprint = fingerprint;
        _routeMesh.ClearSurfaces();

        var selected = _campaign.Armies
            .Where(candidate => candidate.NationId == _campaign.Player?.Id &&
                                _selectedArmyIds.Contains(candidate.Id) && HasRouteIntent(candidate))
            .OrderBy(candidate => candidate.Id)
            .ToArray();
        var drewAny = false;
        foreach (var army in selected)
            drewAny |= AppendRouteArrow(army);
        _routePreview.Visible = drewAny;
    }

    private static bool HasRouteIntent(Simulation.Army army) => army.Route.Count > 0 || army.PlannedRoute.Count > 0 ||
        (army.OrderProvinceId > 0 &&
         army.OrderKind is Simulation.ArmyOrder.AdvanceFront or Simulation.ArmyOrder.Spearhead);

    private static bool IsAttackIntent(Simulation.Army army) => army.AssignedWarId >= 0 && army.PlanActive &&
        army.OrderKind is Simulation.ArmyOrder.AdvanceFront or Simulation.ArmyOrder.Spearhead;

    /// <summary>Draws the complete current route of one selected division.</summary>
    private bool AppendRouteArrow(Simulation.Army army)
    {
        if (army.NationId != _campaign?.Player?.Id) return false;
        if (!army.PlanActive && army.Route.Count == 0 &&
            Simulation.BattlePlanning.LineFor(_campaign, army) is { Kind: Simulation.FrontLineKind.Offensive, ObjectiveSequence.Count: > 0 }) return false;
        var attack = IsAttackIntent(army);
        var route = PresentationRoute(army, attack);
        var directObjective = army.OrderProvinceId > 0;

        _routePoints.Clear();
        _routePoints.Add(PresentationPoint(army.Latitude, army.Longitude, SurfaceLineRadius));
        foreach (var province in route)
        {
            var center = _campaign.ProvinceMap.CenterOf(province);
            AddRoutePoint(PresentationPoint(center.Latitude, center.Longitude, SurfaceLineRadius));
        }
        // A manual order may come from an old save which predates PlannedRoute. Offensive
        // orders never receive a ruler-straight fallback: their corridor is reconstructed
        // through the two belligerents' province graph below.
        if (!attack && directObjective && (route.Count == 0 || route[^1] != army.OrderProvinceId))
        {
            var objective = _campaign.ProvinceMap.CenterOf(army.OrderProvinceId);
            AddRoutePoint(PresentationPoint(objective.Latitude, objective.Longitude, SurfaceLineRadius));
        }
        if (_routePoints.Count < 2) return false;

        return DrawOrderRibbon(army, attack);
    }

    private IReadOnlyList<int> PresentationRoute(Simulation.Army army, bool attack)
    {
        if (army.PlannedRoute.Count > 0) return army.PlannedRoute;
        if (!attack || army.OrderProvinceId <= 0) return army.Route;

        // Compatibility for campaigns saved before complete operational routes became
        // persistent. The reconstruction is deterministic and restricted to belligerents.
        var war = _campaign.Wars.Find(candidate => candidate.Id == army.AssignedWarId && !candidate.Ended);
        var nation = _campaign.NationById(army.NationId);
        if (war is null || nation is null) return army.Route;
        var enemyId = army.NationId == war.AttackerId ? war.DefenderId : war.AttackerId;
        var enemy = _campaign.NationById(enemyId);
        if (enemy is null) return army.Route;
        var controllers = _campaign.ProvinceController ?? _campaign.ProvinceMap.ProvinceToNation;
        return _campaign.ProvinceMap.FindOperationalRoute(army.ProvinceId, army.OrderProvinceId,
            (ushort)nation.MapIndex, (ushort)enemy.MapIndex, controllers);
    }

    private void AddRoutePoint(Vector3 point)
    {
        if (_routePoints.Count == 0 || _routePoints[^1].DistanceSquaredTo(point) > 0.000004f)
            _routePoints.Add(point);
    }

    private static float OrderProgress(Simulation.Army army)
    {
        if (army.OrderRouteSteps <= 0) return Mathf.Clamp(army.MoveProgress, 0f, 1f);
        return Mathf.Clamp((army.OrderStepsCompleted + army.MoveProgress) / army.OrderRouteSteps, 0f, 1f);
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (LoadingInputBlocked) { AcceptEvent(); return; }
        if (_freeCamera && HandleFreeCameraInput(inputEvent)) { AcceptEvent(); return; }
        if (BorderPaintMode && HandleBorderInput(inputEvent)) return;
        if (PaintMode && HandlePaintInput(inputEvent)) return;

        if (inputEvent is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Left)
            {
                if (button.Pressed)
                {
                    _boxSelecting = true;
                    _boxStart = button.Position;
                    _boxCurrent = button.Position;
                    QueueRedraw();
                }
                else if (_boxSelecting)
                {
                    _boxCurrent = button.Position;
                    _boxSelecting = false;
                    var rect = RectFromPoints(_boxStart, _boxCurrent);
                    if (rect.Size.Length() < 8f)
                    {
                        if (CountrySelectionOnly)
                        {
                            if (TryPick(button.Position, out var country)) CountryPicked?.Invoke(country);
                        }
                        else if (TryPickBattle(button.Position, out var battle)) BattlePicked?.Invoke(battle);
                        else if (TryPickFleet(button.Position, out var fleet)) FleetPicked?.Invoke(fleet);
                        else if (TryPickArmyStack(button.Position, out var stack))
                        {
                            if (stack.Count == 1) ArmyPicked?.Invoke(stack[0]);
                            else if (stack.Count > 1) ArmiesBoxPicked?.Invoke(stack);
                        }
                        else if (TryPick(button.Position, out var pick)) CountryPicked?.Invoke(pick);
                    }
                    else
                    {
                        var selected = ArmiesInScreenRect(rect);
                        if (!CountrySelectionOnly && selected.Count > 0) ArmiesBoxPicked?.Invoke(selected);
                    }
                    QueueRedraw();
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex == MouseButton.Right)
            {
                if (button.Pressed)
                {
                    _dragging = true;
                    _dragDistance = 0f;
                    _previousPointer = button.Position;
                }
                else
                {
                    var wasClick = _dragging && _dragDistance < 5f;
                    _dragging = false;
                    if (!CountrySelectionOnly && wasClick && TryPick(button.Position, out var commanded, updateSelection: false))
                        ProvinceCommanded?.Invoke(commanded);
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown)
            {
                // A step proportional to the altitude rather than a fixed one. A quarter
                // of a unit is a nudge from orbit and a lurch from a hundred metres up,
                // and the whole reason for coming this close is to move one division.
                var sign = button.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
                if (_flatMap)
                    _targetFlatZoom = Mathf.Clamp(_targetFlatZoom * (1f + sign * 0.15f),
                        MinimumFlatZoom, MaximumFlatZoom);
                else
                {
                    var altitude = Mathf.Max(0.10f, _targetDistance - GlobeRadius);
                    _targetDistance = Mathf.Clamp(_targetDistance + sign * altitude * 0.22f,
                        MinimumDistance, MaximumDistance);
                }
                AcceptEvent();
                return;
            }
        }

        if (inputEvent is InputEventMouseMotion boxMotion && _boxSelecting)
        {
            _boxCurrent = boxMotion.Position;
            QueueRedraw();
            AcceptEvent();
            return;
        }

        if (inputEvent is InputEventMouseMotion motion && _dragging)
        {
            DragCamera(motion.Position - _previousPointer);
            _previousPointer = motion.Position;
            AcceptEvent();
        }
    }

    /// <summary>
    /// Move the world under the pointer by a mouse delta - a yaw and a pitch on the globe,
    /// a pan on the flat map. Its own method because the paint modes need it too: while a
    /// peace is being drawn both mouse buttons are the brush, and a camera the player cannot
    /// move is a peace conference they cannot finish when the war is on the far side of the
    /// world.
    /// </summary>
    private void DragCamera(Vector2 delta)
    {
        _dragDistance += delta.Length();
        if (_flatMap)
        {
            // The current zoom, not the one being lerped towards: what a pixel is worth is
            // decided by what is on the screen now, so dragging while the wheel is still
            // settling moves the ground by the distance the hand moved rather than by the
            // distance it will be worth in a moment.
            var unitsPerPixel = _flatZoom / Math.Max(1f, Size.Y);
            _targetFlatX -= delta.X * unitsPerPixel;
            _targetFlatY += delta.Y * unitsPerPixel;
            ClampFlatCamera();

            // And the camera goes there, rather than being asked to.
            //
            // A drag is direct manipulation: the ground you took hold of has to stay under
            // the pointer. Moving only the target left the camera closing eleven per cent of
            // the gap per frame - a hundred and forty milliseconds of lag, measured, with the
            // whole of a drag's distance still outstanding at the end of it - so the map
            // trailed the hand and kept sliding after the button came up. Which is exactly
            // what a map that "moves more slowly" feels like, even though the arithmetic
            // moving the target was right to six decimal places.
            //
            // The wheel and the keyboard keep their smoothing: nobody expects a zoom to
            // snap, and WASD adds a little every frame, which is what the lerp is for.
            _flatX = _targetFlatX;
            _flatY = _targetFlatY;
            return;
        }
        var scale = Mathf.Lerp(0.22f, 0.65f, (_targetDistance - MinimumDistance) / (MaximumDistance - MinimumDistance));
        _targetYaw -= delta.X * 0.006f * scale;
        _targetPitch = Mathf.Clamp(_targetPitch - delta.Y * 0.006f * scale, -1.20f, 1.20f);
    }

    /// <summary>
    /// Outline one country on the map, or none.
    ///
    /// Takes a map index rather than a nation so that clearing it is a number rather than a
    /// null, and so the globe does not have to know what a Nation is to draw one.
    /// </summary>
    public void HighlightNation(int mapIndex)
    {
        if (_highlightedNation == mapIndex) return;
        _highlightedNation = mapIndex;
        _globeMaterial?.SetShaderParameter("highlighted_nation", (float)mapIndex);
    }

    private int _highlightedNation = -1;

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (TryPickBattle(atPosition, out var battle))
        {
            var attacker = _campaign?.NationById(battle.AttackerId);
            var defender = _campaign?.NationById(battle.DefenderId);
            var latest = battle.Rounds.LastOrDefault();
            var unknown = Simulation.Loc.Get("Unknown");
            if (FogOfWarActive && _campaign?.Player is { IsHuman: true } player &&
                battle.AttackerId != player.Id && battle.DefenderId != player.Id)
                return Simulation.Loc.Format(
                    "Observed battle · province #{0:N0}\n{1} vs {2}\nDetailed foreign combat reports unavailable.",
                    battle.ProvinceId, attacker?.DisplayName ?? unknown, defender?.DisplayName ?? unknown);
            return Simulation.Loc.Format("BATTLE · province #{0:N0}\n{1} vs {2}\nLosses {3:N0} / {4:N0}",
                       battle.ProvinceId, attacker?.DisplayName ?? unknown, defender?.DisplayName ?? unknown,
                       battle.AttackerLosses, battle.DefenderLosses) +
                   (latest is null ? "" : "\n" + Simulation.Loc.Format(
                       "Power {0:0.0} / {1:0.0} · plan {2:P0}",
                       latest.AttackerPower, latest.DefenderPower, latest.AttackerPlanning));
        }
        if (TryPickArmyStack(atPosition, out var stack, selectableOnly: false))
        {
            if (stack.Count == 0) return "";
            var lead = stack[0];
            var owner = _campaign?.NationById(lead.NationId);
            var name = owner?.DisplayName ?? Simulation.Loc.Get("Unknown");
            var personnel = stack.Sum(DivisionPersonnel);
            if (!Simulation.CampaignIntel.PlayerCanInspect(_campaign, lead.NationId))
                return Simulation.Loc.Format("{0} · {1:N0} observed divisions", name, stack.Count) + "\n" +
                       Simulation.Loc.Format("Observed personnel: {0:N0}", personnel) + "\n" +
                       Simulation.Loc.Get("Foreign formation — orders and route are not available.");
            // Russian counts in three - 1 дивизия, 2 дивизии, 5 дивизий - so the count and
            // its noun have to travel together through the table rather than have an "s"
            // stitched onto the end of one language's plural.
            return Simulation.Loc.Format(Simulation.Plural.Key("{0} · {1:N0} division", stack.Count),
                       name, stack.Count) + "\n" +
                   Simulation.Loc.Format("Personnel {0:N0} · organisation {1:P0}",
                       personnel, stack.Average(army => army.Organisation)) + "\n" +
                   Simulation.Loc.Format("Equipment {0:P0} · experience {1:P0} · supply {2:P0}",
                       stack.Average(army => army.Equipment), stack.Average(army => army.Experience),
                       stack.Average(army => army.SupplyLevel)) + "\n" +
                   (stack.Count == 1
                       ? Simulation.Loc.Format("{0} · {1} posture · planning {2:P0}",
                           Simulation.ArmyVocabulary.OrderName(lead.OrderKind),
                           Simulation.ArmyVocabulary.StanceName(lead.Stance), lead.Planning)
                       : Simulation.Loc.Get("Click to select every independent division in this province."));
        }
        if (!TryPick(atPosition, out var pick)) return "";
        var state = pick.State is null
            ? Simulation.Loc.Get("Unmapped administrative area")
            : pick.State.DisplayName + " · " + Simulation.Loc.Get(pick.State.Kind);
        var controller = pick.ProvinceId > 0 && _campaign?.ProvinceController is { } controllers
            ? _nationByMapIndex.GetValueOrDefault(controllers[pick.ProvinceId])
            : pick.Nation;
        var occupation = pick.ProvinceId > 0 && _campaign?.ProvinceOccupation is { } occupations
            ? occupations[pick.ProvinceId]
            : 0f;
        var hydrology = pick.ProvinceId > 0 && _provinceTopology is not null
            ? (_provinceTopology.HasRiver(pick.ProvinceId)
                ? "\n" + Simulation.Loc.Get("River terrain · crossings affect march and attack") : "") +
              (_provinceTopology.HasLakeAccess(pick.ProvinceId)
                ? "\n" + Simulation.Loc.Get("Lake shore · local supply access") : "")
            : "";
        return pick.Nation.DisplayName + " (" + pick.Nation.Tag + ")\n" + state + "\n" +
               Simulation.Loc.Format("Province #{0:N0}", pick.ProvinceId) +
               (controller is null || controller == pick.Nation ? "" : "\n" + Simulation.Loc.Format(
                   "Controlled by {0} · occupation {1:P0}", controller.DisplayName, occupation)) +
               hydrology + (pick.ProvinceId > 0 && _provinceTopology is not null
                   ? TerrainDescription(_provinceTopology.TerrainAt(pick.ProvinceId)) : "");
    }

    private List<Simulation.Army> ArmiesInScreenRect(Rect2 rect)
    {
        var selected = new List<Simulation.Army>();
        if (!IsInstanceValid(_camera) || (!_flatMap && _distance > 13.5f)) return selected;
        foreach (var (node, armies) in _armyByMarker)
        {
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (rect.HasPoint(_camera.UnprojectPosition(node.GlobalPosition)))
                selected.AddRange(armies.Where(army => Simulation.ArmyInteraction.CanSelect(_campaign, army)));
        }
        return selected;
    }

    /// <summary>
    /// Every province the screen rectangle covers.
    ///
    /// By sampling rather than by projecting each province, because the map has thirty-eight
    /// thousand of them and the rectangle has a few hundred thousand pixels: walking the
    /// smaller of the two is the cheaper question, and the same ray-pick the brush already
    /// uses answers it. The step widens with the rectangle so that selecting a continent
    /// costs about what selecting a county does; three pixels is finer than any province is
    /// small at a zoom where a player would draw a box at all.
    /// </summary>
    private List<int> ProvincesInScreenRect(Rect2 rect)
    {
        var found = new List<int>();
        if (rect.Size.X < 2f || rect.Size.Y < 2f) return found;

        var seen = new HashSet<int>();
        var step = Mathf.Max(3f, rect.Size.Length() / 420f);
        for (var y = rect.Position.Y; y <= rect.End.Y; y += step)
        for (var x = rect.Position.X; x <= rect.End.X; x += step)
        {
            if (!TryPick(new Vector2(x, y), out var pick, updateSelection: false)) continue;
            if (pick.ProvinceId <= 0 || !seen.Add(pick.ProvinceId)) continue;
            found.Add(pick.ProvinceId);
        }
        return found;
    }

    private static Rect2 RectFromPoints(Vector2 a, Vector2 b)
    {
        var min = new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y));
        var max = new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y));
        return new Rect2(min, max - min);
    }

    private Vector2I ViewportSize()
    {
        var size = GetViewport().GetVisibleRect().Size;
        return new Vector2I(Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y));
    }

    private void ResizeViewport()
    {
        if (IsInstanceValid(_viewport) && !Stretch)
            _viewport.Size = ViewportSize();
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

        var shader = new Shader
        {
            Code = @"shader_type spatial;
render_mode diffuse_burley, cull_back;
uniform sampler2D political_map : source_color, filter_linear_mipmap_anisotropic;
uniform sampler2D region_ids : filter_nearest;
uniform sampler2D province_ids : filter_nearest;
uniform sampler2D nation_ids : filter_nearest;
uniform sampler2D hydrology_map : filter_linear, repeat_enable;
uniform sampler2D province_control_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D province_owner_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D nation_palette : source_color, filter_nearest, repeat_disable;
uniform float border_pixels = 1.35;
uniform float province_border_pixels = 2.0;
uniform float province_border_opacity = 0.72;
uniform float province_border_halo = 0.15;
uniform sampler2D front_membership : filter_nearest, repeat_disable;
uniform sampler2D map_mode_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2DArray terrain_tiles : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D province_terrain : filter_nearest, repeat_disable;
uniform sampler2D terrain_surface : filter_nearest, repeat_enable;
uniform sampler2D border_plan_targets : filter_nearest, repeat_disable;
uniform sampler2D border_controller_ids : filter_nearest, repeat_disable;
uniform sampler2D relief_height : filter_linear_mipmap, repeat_enable;
uniform sampler2D relief_color : source_color, filter_linear_mipmap, repeat_enable;
uniform float terrain_ready = 0.0;
uniform float terrain_mode = 0.0;
uniform float political_opacity = 0.68;
uniform float terrain_repeat = 180.0;
uniform float terrain_blend_texels = 1.15;
uniform float id_wander_texels = 0.62;

// Whether the map is smoothed at all, as a switch the player owns.
//
// Off is not a degraded version of on, it is a different picture: the raster exactly as it
// was authored, sampled at the nearest texel, with the texel grid visible. That is a real
// preference and this project has reasons to respect it - a map whose pixels you can count
// is a map whose data a modder can see - and it is also the cheaper picture, because the two
// octaves of value noise that take the boundaries off the grid are not evaluated at all.
uniform float map_smoothing = 1.0;
uniform float terrain_warp_texels = 2.20;
uniform float relief_strength = 0.8;
uniform float relief_color_opacity = 0.0;
// Matches the source scenario's Ocean terrain (#0f4a6b). The relief texture stays
// deliberately disabled: at strategic scale it reads as visual noise, not water.
// Darkened here to compensate for Godot's directional + ambient lighting; the final
// lit result is the source scenario's #0f4a6b rather than a washed-out cyan.
uniform vec3 ocean_color = vec3(0.012, 0.065, 0.105);
uniform float selection_active = 0.0;
// The country the player last right-clicked, by map index, or -1 for none.
uniform float highlighted_nation = -1.0;
uniform float selected_region_id = -1.0;
uniform float selected_province_id = -1.0;
uniform float detail_mix = 0.0;
uniform float province_detail = 0.0;
uniform float map_mode = 0.0;
float map_id(sampler2D map_texture, vec2 uv) {
    vec4 encoded = texture(map_texture, vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)));
    return floor(encoded.r * 255.0 + 0.5) + floor(encoded.g * 255.0 + 0.5) * 256.0;
}
float id_at(sampler2D ids, vec2 cell, vec2 size) {
    return map_id(ids, (cell + vec2(0.5)) / size);
}
vec4 nation_paint(float nation) {
    float low = mod(nation, 256.0);
    float high = floor(nation / 256.0);
    return texture(nation_palette, (vec2(low, high) + vec2(0.5)) / 256.0);
}

/// A number between zero and one that depends only on where you are, and looks like nothing.
///
/// Two of these steer the terrain boundary off the grid it was stored on. A boundary
/// reconstructed from the raster alone is exact but straight, and a coastline of exact
/// straight segments meeting at texel corners reads as a machine drew it. Pushing the point
/// of sampling around by a texel or two first makes the same boundary wander the way ground
/// actually does, at no cost in fidelity: it is the same line, walked unevenly.
float hash21(vec2 p) {
    p = fract(p * vec2(127.31, 311.7));
    p += dot(p, p + 34.53);
    return fract(p.x * p.y);
}

float value_noise(vec2 p) {
    vec2 cell = floor(p);
    vec2 f = p - cell;
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash21(cell), hash21(cell + vec2(1.0, 0.0)), f.x),
               mix(hash21(cell + vec2(0.0, 1.0)), hash21(cell + vec2(1.0, 1.0)), f.x), f.y);
}

/// Two octaves, centred on zero: a slow wander with a finer one riding on it.
float grain(vec2 p) {
    return value_noise(p) * 0.66 + value_noise(p * 2.7 + 11.3) * 0.34 - 0.5;
}

/// The terrain type this point stands on, as the slot number of its ground texture.
float slot_at(sampler2D surface, vec2 cell, vec2 size) {
    vec2 uv = (cell + vec2(0.5)) / size;
    return floor(texture(surface, vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0))).r * 255.0 + 0.5);
}

/// The same reconstruction the borders use, applied to which ground is underfoot.
///
/// The terrain map is an id map like any other - the number in it selects a texture, and the
/// average of two texture numbers is not a texture number - so it was read nearest and
/// rounded, and the ground changed abruptly at the edge of a texel. That is the single most
/// visible grid on the map: not a line anywhere, but every square of grass meeting every
/// square of rock, over the whole world.
float terrain_coverage(sampler2D surface, vec2 uv, out float near_slot, out float other_slot) {
    vec2 size = vec2(textureSize(surface, 0));
    vec2 g = uv * size - vec2(0.5);
    vec2 base_cell = floor(g);
    vec2 f = g - base_cell;

    float s00 = slot_at(surface, base_cell, size);
    float s10 = slot_at(surface, base_cell + vec2(1.0, 0.0), size);
    float s01 = slot_at(surface, base_cell + vec2(0.0, 1.0), size);
    float s11 = slot_at(surface, base_cell + vec2(1.0, 1.0), size);

    float w00 = (1.0 - f.x) * (1.0 - f.y);
    float w10 = f.x * (1.0 - f.y);
    float w01 = (1.0 - f.x) * f.y;
    float w11 = f.x * f.y;

    near_slot = f.x < 0.5 ? (f.y < 0.5 ? s00 : s01) : (f.y < 0.5 ? s10 : s11);

    float coverage = 0.0;
    coverage += abs(s00 - near_slot) < 0.5 ? w00 : 0.0;
    coverage += abs(s10 - near_slot) < 0.5 ? w10 : 0.0;
    coverage += abs(s01 - near_slot) < 0.5 ? w01 : 0.0;
    coverage += abs(s11 - near_slot) < 0.5 ? w11 : 0.0;

    other_slot = near_slot;
    float claim = 0.0;
    if (abs(s00 - near_slot) >= 0.5 && w00 > claim) { claim = w00; other_slot = s00; }
    if (abs(s10 - near_slot) >= 0.5 && w10 > claim) { claim = w10; other_slot = s10; }
    if (abs(s01 - near_slot) >= 0.5 && w01 > claim) { claim = w01; other_slot = s01; }
    if (abs(s11 - near_slot) >= 0.5 && w11 > claim) { claim = w11; other_slot = s11; }
    return coverage;
}

/// Coverage of the nearest id over this pixel, and the neighbour with the next best claim.
///
/// This is the whole of the technique. An id map sampled nearest gives a hard staircase at
/// the texel grid; sampled linearly it gives mush, because ids are labels and the average of
/// two labels is not a label. So the ids are read at the four texels around the point and
/// turned into a coverage instead: how much of this pixel belongs to the label the pixel is
/// nearest to. That is a smooth field whose half-way line is a straight edge cutting through
/// the texel grid at any angle, which is exactly the boundary the raster was drawn from -
/// recovered rather than approximated. Everything else here is reading off that one number.
float boundary_coverage(sampler2D ids, vec2 uv, out float near_id, out float other_id) {
    vec2 size = vec2(textureSize(ids, 0));
    vec2 grid = uv * size - vec2(0.5);
    vec2 base_cell = floor(grid);
    vec2 f = grid - base_cell;

    float i00 = id_at(ids, base_cell, size);
    float i10 = id_at(ids, base_cell + vec2(1.0, 0.0), size);
    float i01 = id_at(ids, base_cell + vec2(0.0, 1.0), size);
    float i11 = id_at(ids, base_cell + vec2(1.0, 1.0), size);

    float w00 = (1.0 - f.x) * (1.0 - f.y);
    float w10 = f.x * (1.0 - f.y);
    float w01 = (1.0 - f.x) * f.y;
    float w11 = f.x * f.y;

    near_id = f.x < 0.5 ? (f.y < 0.5 ? i00 : i01) : (f.y < 0.5 ? i10 : i11);

    float coverage = 0.0;
    coverage += abs(i00 - near_id) < 0.5 ? w00 : 0.0;
    coverage += abs(i10 - near_id) < 0.5 ? w10 : 0.0;
    coverage += abs(i01 - near_id) < 0.5 ? w01 : 0.0;
    coverage += abs(i11 - near_id) < 0.5 ? w11 : 0.0;

    other_id = near_id;
    float claim = 0.0;
    if (abs(i00 - near_id) >= 0.5 && w00 > claim) { claim = w00; other_id = i00; }
    if (abs(i10 - near_id) >= 0.5 && w10 > claim) { claim = w10; other_id = i10; }
    if (abs(i01 - near_id) >= 0.5 && w01 > claim) { claim = w01; other_id = i01; }
    if (abs(i11 - near_id) >= 0.5 && w11 > claim) { claim = w11; other_id = i11; }
    return coverage;
}

vec4 owner_color(float province) {
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    return texture(province_owner_colors, (vec2(low, high) + vec2(0.5)) / 256.0);
}
vec4 control_color(float province) {
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    return texture(province_control_colors, (vec2(low, high) + vec2(0.5)) / 256.0);
}
vec4 map_mode_color(float nation) {
    float low = mod(nation, 256.0);
    float high = floor(nation / 256.0);
    return texture(map_mode_colors, (vec2(low, high) + vec2(0.5)) / 256.0);
}
vec3 front_data(float province) {
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    return texture(front_membership, (vec2(low, high) + vec2(0.5)) / 256.0).rgb;
}
float front_crossing(float province, vec3 here, vec2 uv) {
    float other_province = map_id(province_ids, uv);
    vec3 other = front_data(other_province);
    float both = step(0.002, here.r) * step(0.002, other.r);
    float same_war = 1.0 - step(0.002, abs(here.r - other.r));
    float opposite_sides = step(0.20, abs(here.g - other.g));
    return both * same_war * opposite_sides * step(0.5, abs(province - other_province));
}
float planned_border(float province, vec2 uv) {
    vec2 lookup = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    vec4 target = round(texture(border_plan_targets, lookup) * 255.0);
    float own = target.r + target.g * 256.0;
    float enemy = target.b + target.a * 256.0;
    float neighbor = map_id(province_ids, uv);
    vec2 neighbor_lookup = (vec2(mod(neighbor, 256.0), floor(neighbor / 256.0)) + 0.5) / 256.0;
    // Decode this lookup directly: map_id's repeating raster sampler is incompatible
    // with repeat_disable controller lookups in Godot's shader compiler.
    vec2 here_code = round(texture(border_controller_ids, lookup).rg * 255.0);
    vec2 other_code = round(texture(border_controller_ids, neighbor_lookup).rg * 255.0);
    float here_owner = here_code.r + here_code.g * 256.0;
    float other_owner = other_code.r + other_code.g * 256.0;
    return step(0.5, enemy) * (1.0 - step(0.5, abs(own - here_owner))) *
        (1.0 - step(0.5, abs(enemy - other_owner)));
}
uniform float fog_viewer = -1.0;
uniform sampler2D fog_visibility : filter_nearest, repeat_disable;
// How much of the tint to apply, which is a function of how close the camera is. What fog
// HIDES is not here and never was - that is the vision model, and it applies at every zoom.
// This is only the grey wash, and a grey wash over half a globe helps nobody read it.
uniform float fog_tint = 1.0;
uniform float fog_darkness = 0.74;
uniform float fog_saturation = 0.65;
void fragment() {
    // One wander, and every boundary on the map reads through it.
    //
    // Reconstructing a boundary from the four ids around a point puts it within half a texel
    // of the truth, which is exact enough at any zoom where a texel is smaller than a pixel.
    // Close up it is not: a texel of a 5760-wide world covers ten screen pixels, half a texel
    // is five, and five pixels of error laid along a grid is a staircase. No amount of
    // sharpening helps, because there is nothing left in the raster to sharpen - the
    // information simply is not there.
    //
    // So the boundary is moved off the grid instead. The point of sampling is pushed around
    // by rather less than a texel, which is inside the error the reconstruction already has,
    // and the staircase becomes a coastline. It is the same thing the ground does where two
    // terrains meet, and the same thing a Paradox map does with a baked noise layer.
    //
    // Shared, and that is the whole reason it lives here rather than in three places:
    // country, region and province all read through this one offset, so they wander together
    // and continue to agree. Warped separately, a country border and the province border
    // that runs along it would sit a texel apart and the map would look torn.
    //
    // Faded out as the camera pulls back: once a texel is smaller than a pixel the wander
    // would only be a way of making small islands flicker.
    vec2 id_size = vec2(textureSize(nation_ids, 0));
    float id_span = max(length(fwidth(UV) * id_size), 0.0001);
    // How wide a screen pixel is, measured in coverage. A coverage field crosses from one
    // to zero over about a texel, so a pixel that spans `id_span` texels spans about that
    // much coverage - and that is the width every soft edge on this map wants to be.
    //
    // It cannot be had from `fwidth` of the coverage itself once the lookup below is warped:
    // fwidth would then be measuring how fast the noise wiggles, which is fast, and every
    // border on the map would come out several pixels wide and washed out instead of one
    // pixel wide and crisp. The camera says how big a pixel is; the noise does not get a
    // vote.
    float id_aa = max(id_span, 0.0001);
    // Four hashes apiece and two octaves each, so this pair is the most expensive thing on
    // the map that is not a texture fetch - and it is wanted three times over: by the
    // political boundary, by the ground, and by the shore. It is a pair of numbers between
    // minus a half and a half, in no units at all, so each of the three can scale it into
    // its own texels and they all end up wandering the same way. Computed three times it
    // cost forty hashes a pixel; computed once it costs sixteen.
    vec2 map_drift = vec2(0.0);
    vec2 map_uv = UV;
    if (map_smoothing > 0.5) {
        map_drift = vec2(grain(UV * id_size * 0.9), grain(UV * id_size * 0.9 + 71.3));
        map_uv = UV + map_drift * id_wander_texels * clamp(1.0 - id_span, 0.0, 1.0) / id_size;
    }

    // Far away the authored raster is right: a screen pixel covers many map texels and what
    // the eye wants is the average, mipmapped and anisotropic. Close up the raster is exactly
    // wrong - one texel covers many pixels, so nearest sampling shows the grid it was stored
    // on. So close up the colour is rebuilt from the country id map instead, and the boundary
    // between two countries is recovered as a straight edge at whatever angle it really runs,
    // antialiased over one screen pixel. The raster is not being filtered; it is not being
    // asked.
    vec4 political_smooth = texture(political_map, UV);
    float near_nation;
    float other_nation;
    float nation_cover = boundary_coverage(nation_ids, map_uv, near_nation, other_nation);
    float nation_aa = id_aa;
    float nation_share = smoothstep(0.5 - nation_aa, 0.5 + nation_aa, nation_cover);

    // One screen pixel, measured in texels. Wanted for deciding when a line is finer than
    // the screen can carry.
    float pixel_texels = id_span;

    // A real edge, one pixel wide.
    //
    // This used to fade the fill over most of a texel - nine screen pixels close in - and let
    // a stroke on top carry the boundary. The reason was sound at the time: a hard threshold
    // laid on a reconstruction that is only accurate to a texel shows the texel pattern, and
    // a soft fade at least hid it. What it actually produced was a grey halo round every
    // coast and every border, which is how the whole map came to look slightly out of focus.
    //
    // The wander is what makes the compromise unnecessary. The boundary is no longer on the
    // grid, so there is no grid left to show, and the edge can be exactly as sharp as the
    // screen allows.
    // Smoothed, the colour is rebuilt across the reconstructed boundary. Unsmoothed, it is
    // simply the nearest texel's country - which is what nearest sampling means, and what
    // puts the texel grid back on the screen for anybody who wants it there.
    vec4 political_crisp = map_smoothing > 0.5
        ? mix(nation_paint(other_nation), nation_paint(near_nation), nation_share)
        : nation_paint(near_nation);
    vec4 political = mix(political_smooth, political_crisp, detail_mix);
    float rim = pow(1.0 - max(dot(normalize(NORMAL), normalize(VIEW)), 0.0), 2.0);
    vec3 water = ocean_color * (0.72 + rim * 0.28);
    vec2 texel = 1.0 / vec2(textureSize(province_ids, 0));
    float region = map_id(region_ids, map_uv);
    float province = map_id(province_ids, map_uv);
    float nation = map_id(nation_ids, map_uv);
    vec4 live_control = control_color(province);
    float political_light = clamp(max(max(political.r, political.g), political.b) * 1.25, 0.58, 0.94);

    // Who owns this ground now, when that is no longer who owned it when the map was
    // painted.
    //
    // The political raster is painted once, at load, from the scenario. Everything after
    // that - every province a treaty signs over, every border a war moves, every save loaded
    // in the middle of a conquest - changes ownership in the simulation and left the picture
    // alone, so land taken at a peace conference kept the colour of the country that had
    // lost it. The overlay that draws occupation could not help: it deliberately says nothing
    // when the occupier and the owner are the same country, which is exactly what annexation
    // makes them.
    //
    // So a second overlay carries the owner, and it is applied first: it repaints the ground
    // itself, and any occupation stripes below are then drawn over the new owner rather than
    // the old one.
    //
    // Painted at the shade this pixel already had RELATIVE to its own country's colour,
    // rather than multiplied by that country's brightness. The difference is the whole of
    // why land won at a peace conference used to read as duller than land held since the
    // first morning: the new owner's colour was arriving scaled by how bright the loser's
    // colour happened to be - 0.58 for a dark one, 0.94 for a pale one - while the same
    // country's own provinces were painted at full strength. As a ratio it is one at full
    // detail, where the pixel simply is its country's colour, and it is the raster's own
    // border shading when zoomed out, which is what the multiplier was reaching for.
    vec4 owner_paint = owner_color(province);
    vec3 flat_here = nation_paint(near_nation).rgb;
    float painted_flat = max(max(flat_here.r, flat_here.g), flat_here.b);
    float painted_here = max(max(political.r, political.g), political.b);
    float owner_shade = painted_flat > 0.001 ? clamp(painted_here / painted_flat, 0.30, 1.0) : 1.0;
    political.rgb = mix(political.rgb, owner_paint.rgb * owner_shade, owner_paint.a);
    political_light = clamp(max(max(political.r, political.g), political.b) * 1.25, 0.58, 0.94);
    // Who owns ground and who is standing on it are two different questions, and a map that
    // answers only the second is why a peace conference is unreadable: every province the
    // enemy overran wore the enemy's colour, so a player could not tell what a treaty would
    // annex from what it would merely hand back. Paradox answers both at once - the owner
    // keeps the colour, the occupier wears stripes over it - and so does this now. Alpha
    // says which of the two a colour is: a full byte replaces the province outright, which
    // is the treaty being drafted, and anything less is an occupation drawn over an owner
    // who has not signed anything away yet.
    float hatch = (UV.x * 2.0 + UV.y) * 190.0;
    float hatch_width = clamp(fwidth(hatch), 0.0005, 0.45);
    float stripe = smoothstep(0.5 - hatch_width, 0.5 + hatch_width, fract(hatch));
    // Zoomed far enough out that one stripe is thinner than one pixel, stripes are moire and
    // nothing else. There they collapse into a flat half-blend, which still reads at a glance
    // as ground somebody else is holding.
    float hatch_visible = clamp(1.0 - hatch_width * 2.5, 0.0, 1.0);
    float treaty = step(0.75, live_control.a);
    float occupied = step(0.10, live_control.a) * (1.0 - treaty);
    vec3 other_flag = live_control.rgb * political_light;
    political.rgb = mix(political.rgb, other_flag, treaty);
    political.rgb = mix(political.rgb, other_flag,
        occupied * mix(0.46, mix(0.08, 0.88, stripe), hatch_visible));
    vec4 strategic = map_mode_color(nation);
    political.rgb = mix(political.rgb, strategic.rgb * mix(0.84, 1.02, political_light),
        map_mode * strategic.a);
    vec3 base = mix(water, political.rgb, political.a);
    if (terrain_ready > 0.5) {
        vec2 province_uv = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
        // Where one ground meets another, reconstructed rather than stepped, and walked off
        // the grid first so the meeting is ragged instead of ruled. Two ground samples: the
        // type this pixel mostly stands on and the one with the next best claim on it, mixed
        // across a band a texel or so wide - and never narrower than a screen pixel, so a
        // coastline seen from orbit fades rather than sparkles.
        vec2 terrain_size = vec2(textureSize(terrain_surface, 0));
        vec2 terrain_lookup = UV + map_drift * terrain_warp_texels / terrain_size;

        float near_slot;
        float other_slot;
        float slot_cover = terrain_coverage(terrain_surface, terrain_lookup, near_slot, other_slot);
        float slot_span = max(length(fwidth(UV) * terrain_size), 0.0001);
        float slot_band = max(terrain_blend_texels * 0.25, slot_span);
        float slot_share = smoothstep(0.5 - slot_band, 0.5 + slot_band, slot_cover);

        vec2 terrain_uv = UV * vec2(terrain_repeat, terrain_repeat * 0.5);
        // One ground sample when unsmoothed rather than two mixed: the saving is a texture
        // fetch per pixel on the largest surface the game draws, which is the half of this
        // switch that matters on a machine that needed it.
        vec3 ground;
        if (map_smoothing > 0.5)
            ground = mix(texture(terrain_tiles, vec3(terrain_uv, other_slot)).rgb,
                         texture(terrain_tiles, vec3(terrain_uv, near_slot)).rgb, slot_share);
        else
            ground = texture(terrain_tiles, vec3(terrain_uv, near_slot)).rgb;
        vec3 relief_tint = texture(relief_color, UV).rgb;
        ground = mix(ground, relief_tint, relief_color_opacity);
        // Political tint is an albedo blend, not transparent 3D geometry.
        // Keep surface detail while preserving recognisable controller colours.
        float ground_light = clamp(dot(ground, vec3(0.2126, 0.7152, 0.0722)) * 1.4 + 0.50, 0.62, 1.1);
        vec3 colored_ground = mix(ground, political.rgb * ground_light, political_opacity);
        vec3 land_surface = terrain_mode > 1.5 ? relief_tint : (terrain_mode > 0.5 ? ground : colored_ground);
        base = mix(water, land_surface, political.a);
        vec2 ht = 1.0 / vec2(textureSize(relief_height, 0));
        float west = texture(relief_height, vec2(fract(UV.x - ht.x), clamp(UV.y, ht.y, 1.0 - ht.y))).r;
        float east = texture(relief_height, vec2(fract(UV.x + ht.x), clamp(UV.y, ht.y, 1.0 - ht.y))).r;
        float north = texture(relief_height, vec2(UV.x, clamp(UV.y - ht.y, 0.0, 1.0))).r;
        float south = texture(relief_height, vec2(UV.x, clamp(UV.y + ht.y, 0.0, 1.0))).r;
        vec2 slope = clamp(vec2(west - east, south - north) * relief_strength * 8.0, vec2(-0.8), vec2(0.8));
        NORMAL_MAP = normalize(vec3(slope * political.a, 1.0)) * 0.5 + 0.5;
        NORMAL_MAP_DEPTH = 1.0;
    }
    // A shore drawn by thresholding a filtered raster takes the shape of the filter: the
    // diamonds bilinear interpolation makes between four texels, which is a grid again by
    // another name. The threshold is nudged by the same wander the ground uses, so the water
    // finds an uneven edge, and its width follows the screen rather than the texture, so it
    // stays one pixel of shore at any zoom instead of a wide gradient close up.
    vec2 hydrography = texture(hydrology_map, UV).rg;
    float shore_wander = map_drift.x * 0.26;
    float lake_edge = max(fwidth(hydrography.r), 0.004);
    float river_edge = max(fwidth(hydrography.g), 0.004);
    float lake_water = smoothstep(0.38 - lake_edge, 0.38 + lake_edge, hydrography.r + shore_wander);
    float river_water = smoothstep(0.44 - river_edge, 0.44 + river_edge, hydrography.g + shore_wander);
    vec3 inland_water = vec3(0.018, 0.17, 0.25);
    base = mix(base, inland_water, lake_water * 0.94);
    base = mix(base, vec3(0.025, 0.24, 0.34), river_water * mix(0.42, 0.88, detail_mix));
    float region_selected = (abs(region - selected_region_id) < 0.5 ? 1.0 : 0.0) * selection_active * political.a;
    float province_selected = (abs(province - selected_province_id) < 0.5 ? 1.0 : 0.0) * selection_active * political.a;
    // The region line, the last one still drawn by asking four neighbours whether they
    // differ - which is one texel wide, on or off, and a staircase. Off the same coverage
    // field as the province and country lines.
    float near_region;
    float other_region_id;
    float region_cover = boundary_coverage(region_ids, map_uv, near_region, other_region_id);
    float region_aa = id_aa;
    float region_edge = 1.0 - smoothstep(0.0, border_pixels, abs(region_cover - 0.5) / region_aa);
    region_edge *= step(0.5, abs(near_region - other_region_id));
    // A province line drawn by asking four neighbours whether they differ is one texel wide
    // and either on or off, which is a staircase wearing a border's clothes. The same
    // coverage field gives the distance to the boundary in screen pixels, so the line can be
    // a stroke of constant width with soft shoulders - crisp at any zoom, and gone rather
    // than aliased when a pixel covers more of the map than a line could describe.
    float near_province;
    float other_province_id;
    float province_cover = boundary_coverage(province_ids, map_uv, near_province, other_province_id);
    float province_aa = id_aa;
    float province_distance = abs(province_cover - 0.5) / province_aa;
    float province_width = mix(border_pixels, province_border_pixels, province_detail);
    float province_edge = 1.0 - smoothstep(province_width * 0.3, province_width, province_distance);
    province_edge *= step(0.5, abs(near_province - other_province_id));
    float province_halo = (1.0 - smoothstep(province_width, province_width + 1.0, province_distance)) *
        step(0.5, abs(near_province - other_province_id));

    // The country line, off the same coverage field as the province and region lines rather
    // than off a distance field.
    //
    // A distance field gives a smoother zero line, which is why it was here - but its value
    // is a distance, and a distance read through a warped lookup is no longer a distance. The
    // wander changes the field's gradient as well as its position, so wherever the wander
    // happened to run along the border the stroke swelled into a blotch. Coverage does not
    // have that problem: it is recomputed from the ids at the warped point, so it is a
    // correct reconstruction of a boundary that has been moved, rather than a distorted
    // reading of one that has not.
    //
    // Wider than a province line, and it fades out rather than aliasing once a screen pixel
    // covers more map than a line could carry.
    // Solid to a pixel and a bit, then two more pixels of shoulder. Written as a distance
    // in screen pixels rather than in coverage, so the line is the same weight at every zoom
    // - which is what makes it read as a drawn border and not as an artefact of the data.
    float nation_edge_pixels = abs(nation_cover - 0.5) / nation_aa;
    float country_edge = 1.0 - smoothstep(border_pixels * 0.9, border_pixels * 2.1, nation_edge_pixels);
    country_edge *= step(0.5, abs(near_nation - other_nation)) *
                    step(0.5, near_nation) * clamp(2.2 - pixel_texels, 0.0, 1.0);
    vec3 front = front_data(province);
    float front_edge = max(max(
        front_crossing(province, front, UV + vec2(texel.x, 0.0)),
        front_crossing(province, front, UV - vec2(texel.x, 0.0))), max(
        front_crossing(province, front, UV + vec2(0.0, texel.y)),
        front_crossing(province, front, UV - vec2(0.0, texel.y))));
    // A restrained second pixel gives the active front enough weight at
    // operational zoom without detached 3D geometry above the globe.
    float front_outer = max(max(
        front_crossing(province, front, UV + vec2(texel.x * 2.0, 0.0)),
        front_crossing(province, front, UV - vec2(texel.x * 2.0, 0.0))), max(
        front_crossing(province, front, UV + vec2(0.0, texel.y * 2.0)),
        front_crossing(province, front, UV - vec2(0.0, texel.y * 2.0))));
    // The terrain/relief views contain no political front shading.
    front_edge *= 1.0 - step(0.5, terrain_mode);
    front_outer *= 1.0 - step(0.5, terrain_mode);

    // Zoom-gated, screen-space strokes: readable through relief at tactical distance,
    // absent on the world map. A restrained light shoulder also works on dark terrain.
    base = mix(base, vec3(0.78, 0.80, 0.77), province_halo * province_detail * political.a * province_border_halo);
    base = mix(base, vec3(0.055, 0.075, 0.09), province_edge * province_detail * political.a * province_border_opacity);
    // The country border, drawn rather than baked. It replaces the one-texel darkening the
    // political raster used to carry, which was the most visible pixel grid on the map.
    base = mix(base, vec3(0.05, 0.07, 0.10), country_edge * detail_mix * political.a * 0.78);
    // The country the player has asked about, drawn as one. Off the same coverage field as
    // the border above - the same stroke at a different weight and colour, rather than a
    // second idea of where a country ends - with a faint wash inside it so the outline reads
    // as a country rather than as a region that happens to be lit.
    float highlighted = step(0.0, highlighted_nation) *
                        (abs(near_nation - highlighted_nation) < 0.5 ? 1.0 : 0.0) * political.a;
    float highlight_edge = 1.0 - smoothstep(border_pixels * 0.5, border_pixels * 3.2, nation_edge_pixels);
    highlight_edge *= step(0.5, abs(near_nation - other_nation)) * highlighted;
    base = mix(base, vec3(0.91, 0.79, 0.42), highlighted * 0.085);
    base = mix(base, vec3(1.00, 0.87, 0.48), highlight_edge * 0.88);
    // A line the player drew. Blue holds it, amber takes it, grey falls back to it.
    float plan = round(front.b * 4.0);
    vec3 plan_color = plan < 1.5 ? vec3(0.24, 0.56, 0.96)
        : (plan < 2.5 ? vec3(0.96, 0.52, 0.16) : vec3(0.70, 0.73, 0.78));
    float plan_on = step(0.5, plan) * max(province_detail, detail_mix * 0.70);
    if (plan < 1.5) plan_on = 0.0;
    float border_plan = 0.0;
    if (plan > 0.5 && plan < 1.5) border_plan = max(max(planned_border(province, UV + vec2(texel.x, 0.0)),
        planned_border(province, UV - vec2(texel.x, 0.0))),
        max(planned_border(province, UV + vec2(0.0, texel.y)), planned_border(province, UV - vec2(0.0, texel.y))));
    base = mix(base, plan_color, plan_on * 0.40);
    base = mix(base, vec3(1.0, 0.22, 0.10), front_edge * max(province_detail, detail_mix * 0.70) * 0.94);
    base = mix(base, vec3(0.93, 0.16, 0.07), front_outer * max(province_detail, detail_mix * 0.70) * 0.38);
    // One narrow cartographic stroke on the shared boundary, never a filled province.
    base = mix(base, vec3(0.30, 0.72, 0.96), border_plan * 0.95);
    base = mix(base, vec3(0.95, 0.76, 0.25), region_selected * 0.10);
    base = mix(base, vec3(1.0, 0.84, 0.30), province_selected * province_detail * 0.16);
    float selection_edge = max(region_edge * region_selected, province_edge * province_selected * province_detail);
    vec2 fog_lookup = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    float known = texture(fog_visibility, fog_lookup).r;
    float fog = step(0.0, fog_viewer) * (1.0 - step(0.5, known)) * political.a * fog_tint;
    base = mix(base, mix(vec3(dot(base, vec3(0.2126, 0.7152, 0.0722))), base, fog_saturation) * fog_darkness, fog);
    ALBEDO = mix(base, vec3(1.0, 0.84, 0.30), selection_edge * 0.92);
    EMISSION = mix(water * 0.025, vec3(0.28, 0.18, 0.025), selection_edge * 0.48) +
        plan_color * plan_on * 0.14 +
        vec3(0.30, 0.025, 0.008) * front_edge * max(province_detail, detail_mix * 0.70) +
        vec3(0.08, 0.008, 0.002) * front_outer * max(province_detail, detail_mix * 0.70) +
        inland_water * (lake_water * 0.035 + river_water * 0.025);
    ROUGHNESS = mix(0.92, 0.82, political.a);
    METALLIC = 0.0;
    SPECULAR = 0.0;
}"
        };
        _globeMaterial = new ShaderMaterial { Shader = shader };
        // Until the asynchronous political map has finished, the shader sees transparent
        // political pixels and presents the deliberately flat strategic ocean.
        var transparent = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        var placeholder = ImageTexture.CreateFromImage(transparent);
        _globeMaterial.SetShaderParameter("political_map", placeholder);
        var emptyIds = ImageTexture.CreateFromImage(Image.CreateEmpty(2, 2, false, Image.Format.Rg8));
        _globeMaterial.SetShaderParameter("region_ids", emptyIds);
        _globeMaterial.SetShaderParameter("province_ids", emptyIds);
        _globeMaterial.SetShaderParameter("nation_ids", emptyIds);
        _globeMaterial.SetShaderParameter("hydrology_map", emptyIds);
        _globeMaterial.SetShaderParameter("province_owner_colors", placeholder);
        _globeMaterial.SetShaderParameter("province_control_colors",
            ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8)));
        _globeMaterial.SetShaderParameter("front_membership",
            ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rg8)));
        _globeMaterial.SetShaderParameter("map_mode_colors",
            ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8)));
        mesh.Material = _globeMaterial;

        _globeNode = new MeshInstance3D { Name = "PoliticalGlobe", Mesh = mesh };
        world.AddChild(_globeNode);
    }

    /// <summary>
    /// Whether the globe hangs in the sky the skin supplies or against flat `shell.space`.
    ///
    /// Two things have to agree before anything is drawn, and they are asking different
    /// questions. `shell.space_texture` is the skin saying whether it HAS a sky - a mod that
    /// wants a plain void sets it to zero, and no player setting should be able to hang a
    /// photograph behind its globe anyway. This is the player saying whether to draw the one
    /// that exists.
    /// </summary>
    public bool SpaceBackdropEnabled
    {
        get => _spaceBackdrop;
        set
        {
            if (_spaceBackdrop == value) return;
            _spaceBackdrop = value;
            ApplySpaceBackdrop();
        }
    }

    /// <summary>
    /// Hang the sky, or take it down and paint the sphere instead.
    ///
    /// The two have to be set together. `albedo_color` is multiplied into `albedo_texture`,
    /// so the dark tint that makes a bare sphere look like space makes a photograph of space
    /// look like nothing: measured at 0.04 of 255 against 5.24 for the same picture untinted.
    /// The colour belongs to the case where there is no picture.
    /// </summary>
    private void ApplySpaceBackdrop()
    {
        if (_spaceMaterial is null) return;
        var showing = _spaceBackdrop && _spaceTexture is not null;
        _spaceMaterial.AlbedoTexture = showing ? _spaceTexture : null;
        _spaceMaterial.AlbedoColor = showing ? Colors.White : FlatSpaceColour();
    }

    /// <summary>
    /// The colour behind the globe when no picture is hung there. Falls back to the globe's
    /// own token, because an unknown one resolves to magenta and a magenta sky is worse than
    /// a slightly different blue.
    /// </summary>
    private static Color FlatSpaceColour() => new(Simulation.InterfaceSkin.Colour(
        Simulation.InterfaceSkin.Has("shell.space") ? "shell.space" : "globe.space"));

    private bool _spaceBackdrop = true;
    private StandardMaterial3D _spaceMaterial;
    private Texture2D _spaceTexture;

    private Node3D CreateSpace(Node3D world)
    {
        var space = new SphereMesh
        {
            Radius = 90f,
            Height = 180f,
            RadialSegments = 128,
            Rings = 64
        };
        var material = new StandardMaterial3D
        {
            // Both of these are set by ApplySpaceBackdrop below, together, because the
            // colour is multiplied into the texture and the two cannot be chosen apart.
            AlbedoColor = Colors.White,
            AlbedoTexture = null,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Front,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic
        };
        space.Material = material;
        _spaceMaterial = material;
        _spaceTexture = Simulation.InterfaceSkin.Metric("shell.space_texture", 0) > 0
            ? GD.Load<Texture2D>(Simulation.SpriteCatalog.TextureOf("globe_space_background",
                "res://game/gfx/globe/globe_space_background.png"))
            : null;
        // Applied here as well as in the setter, so it does not matter whether the globe is
        // built before the settings are read or after.
        ApplySpaceBackdrop();
        var node = new MeshInstance3D { Name = "SpaceBackground", Mesh = space };
        world.AddChild(node);
        return node;
    }

    /// <summary>The ordinary camera: an orbit rig the player turns, tilts and zooms.</summary>
    private void UpdateOrbitCamera(double delta)
    {
        var weight = 1f - Mathf.Pow(0.0008f, (float)delta);
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, weight);
        _pitch = Mathf.Lerp(_pitch, _targetPitch, weight);
        _distance = Mathf.Lerp(_distance, _targetDistance, weight);
        _flatX = Mathf.Lerp(_flatX, _targetFlatX, weight);
        _flatY = Mathf.Lerp(_flatY, _targetFlatY, weight);
        _flatZoom = Mathf.Lerp(_flatZoom, _targetFlatZoom, weight);

        var typing = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        if (_flatMap && !typing)
        {
            var flatSpeed = _flatZoom * 0.62f * (float)delta;
            if (Input.IsKeyPressed(Key.W)) _targetFlatY += flatSpeed;
            if (Input.IsKeyPressed(Key.S)) _targetFlatY -= flatSpeed;
            if (Input.IsKeyPressed(Key.A)) _targetFlatX -= flatSpeed;
            if (Input.IsKeyPressed(Key.D)) _targetFlatX += flatSpeed;
            ClampFlatCamera();
        }
        else if (!_flatMap && !typing)
        {
            // The keys move the camera, not the globe.
            //
            // They used to do the opposite, and the proof that it was wrong is six lines
            // above: on the flat map W walks the camera north and D walks it east, and on the
            // globe the same two keys did the reverse. One screen, one pair of hands, two
            // meanings - which is what "on WASD it is the globe that turns" describes.
            //
            // The right-drag is deliberately not touched. Dragging is direct manipulation of
            // the thing under the pointer and reads correctly as taking hold of the globe;
            // the keyboard is not holding anything, and a key that walks the viewpoint is
            // what every map in the genre does.
            if (Input.IsKeyPressed(Key.W)) _targetPitch = Mathf.Clamp(_targetPitch - (float)delta * 0.7f, -1.20f, 1.20f);
            if (Input.IsKeyPressed(Key.S)) _targetPitch = Mathf.Clamp(_targetPitch + (float)delta * 0.7f, -1.20f, 1.20f);
            if (Input.IsKeyPressed(Key.A)) _targetYaw -= (float)delta * 0.7f;
            if (Input.IsKeyPressed(Key.D)) _targetYaw += (float)delta * 0.7f;
        }

        ApplyCamera();
    }

    // ---- Free camera -----------------------------------------------------------------
    //
    // The console's freecam used to hide the HUD and leave the orbit rig exactly where it
    // was, so "free" meant a clean screenshot of the same locked-down view. This is the
    // real thing: the camera comes off the rig entirely and flies. Mouse looks, WASD moves
    // along the direction it is looking, Space and Ctrl climb and descend, Shift is faster
    // and Alt is slower. Nothing clamps it - it can sit on a province, fly the length of a
    // front, or pull back until the planet is a marble, which is what makes it useful for
    // showing the game to somebody rather than playing it.

    private bool _freeCamera;
    private Vector3 _freePosition;
    private float _freeYaw;
    private float _freePitch;
    private float _freeSpeed = 4f;

    // ---- what the camera is being asked to do, as opposed to what it is doing -----------
    //
    // Everything above and below is a *target*. What the viewer sees is `_shown*`, which
    // chases it. That split is the whole reason this camera can shoot usable footage: a
    // hand on a mouse produces a jittery signal, and a camera that follows it exactly
    // produces jittery film. With the chase switched off the two are the same thing and it
    // behaves as it always did.
    private float _freeRoll;
    private float _freeFov = RigFieldOfView;
    private bool _freeSmoothing = true;
    private Vector3 _shownPosition;
    private Quaternion _shownRotation = Quaternion.Identity;
    private float _shownFov = RigFieldOfView;

    /// <summary>
    /// Turntable mode: the camera hangs at a fixed distance from a point on the globe and
    /// swings around it, always looking at it.
    ///
    /// This is the shot that a flying camera cannot make. Circling something by hand means
    /// steering position and aim at once and getting both wrong; here the aim is not a thing
    /// anyone has to hold, so the only inputs left are which way round and how far out.
    /// </summary>
    private bool _freeOrbiting;
    private Vector3 _freeOrbitCentre;
    private float _freeOrbitRadius = 12f;
    private float _freeOrbitYaw;
    private float _freeOrbitPitch;

    /// <summary>Radians a second the turntable turns on its own. Zero is a still camera.</summary>
    private float _freeAutoOrbit;

    public bool FreeOrbiting => _freeOrbiting;
    public float FreeFieldOfView => _freeFov;
    public float FreeRoll => _freeRoll;
    public bool FreeSmoothing => _freeSmoothing;
    public float FreeAutoOrbit => _freeAutoOrbit;

    /// <summary>Whether the camera has come off its rig and is flying.</summary>
    public bool FreeCamera => _freeCamera;

    /// <summary>Metres per second at the current setting, for a status line.</summary>
    public float FreeCameraSpeed => _freeSpeed;

    public void SetFreeCamera(bool enabled)
    {
        if (_freeCamera == enabled || !IsInstanceValid(_camera)) return;
        _freeCamera = enabled;

        if (enabled)
        {
            // Start exactly where the player was looking, so turning it on does not
            // teleport them somewhere else.
            var transform = _camera.GlobalTransform;
            _freePosition = transform.Origin;
            var euler = transform.Basis.GetEuler();
            _freeYaw = euler.Y;
            _freePitch = Mathf.Clamp(euler.X, -1.45f, 1.45f);
            // A fresh flight every time. A roll, a lens or a turntable left over from the
            // last one is a camera that starts somewhere nobody asked it to be.
            _freeRoll = 0f;
            _freeFov = RigFieldOfView;
            _freeOrbiting = false;
            _freeAutoOrbit = 0f;
            _camera.TopLevel = true;
            Input.MouseMode = Input.MouseModeEnum.Captured;
            SnapFreeCamera();
        }
        else
        {
            // Land where it was flying. Without this the orbit rig snaps back to whatever
            // angle it held before take-off, which throws the player across the planet at
            // exactly the moment they wanted to look at what they had just flown to.
            var direction = _freePosition.LengthSquared() > 0.0001f
                ? _freePosition.Normalized()
                : Vector3.Back;
            _targetPitch = Mathf.Clamp(Mathf.Asin(direction.Y), -1.20f, 1.20f);
            _targetYaw = Mathf.Atan2(direction.X, direction.Z);
            _pitch = _targetPitch;
            _yaw = _targetYaw;
            _targetDistance = Mathf.Clamp(_freePosition.Length(), MinimumDistance, MaximumDistance);
            _distance = _targetDistance;

            _camera.TopLevel = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            // The lens goes back with it. A camera landed at a 12-degree field of view would
            // leave the whole game zoomed into a telephoto shot with nothing to explain it.
            _camera.Fov = RigFieldOfView;
            _freeOrbiting = false;
            _freeAutoOrbit = 0f;
            ApplyCamera();
        }
    }

    /// <summary>
    /// One frame of flight. Speed scales with height above the surface, so the same stick
    /// deflection crawls along a front line and crosses an ocean, which is the difference
    /// between a camera that is usable at both scales and one that is usable at neither.
    /// </summary>
    private void UpdateFreeCamera(double delta)
    {
        var step = (float)delta;
        var fast = Input.IsKeyPressed(Key.Shift) ? 4.5f : 1f;
        var slow = Input.IsKeyPressed(Key.Alt) ? 0.22f : 1f;

        // ---- the two rotations a flying camera never had -------------------------------
        // Roll, because a horizon that is allowed to tilt is the difference between footage
        // and a map screenshot; and R to put it back, because finding level again by hand
        // is the part nobody enjoys.
        if (Input.IsKeyPressed(Key.Q)) _freeRoll -= 1.1f * step * fast * slow;
        if (Input.IsKeyPressed(Key.E)) _freeRoll += 1.1f * step * fast * slow;
        if (Input.IsKeyPressed(Key.R)) _freeRoll = Mathf.MoveToward(_freeRoll, 0f, 2.2f * step);

        // A lens rather than a throttle: moving closer and zooming in look nothing alike,
        // and only one of them can be done from orbit.
        if (Input.IsKeyPressed(Key.Z)) _freeFov = Mathf.Clamp(_freeFov - 26f * step, 12f, 110f);
        if (Input.IsKeyPressed(Key.X)) _freeFov = Mathf.Clamp(_freeFov + 26f * step, 12f, 110f);
        if (Input.IsKeyPressed(Key.F)) _freeFov = Mathf.MoveToward(_freeFov, RigFieldOfView, 60f * step);

        if (_freeOrbiting) UpdateFreeOrbit(step, fast * slow);
        else UpdateFreeFlight(step, fast * slow);

        // Everything downstream sizes labels, markers and province detail by _distance.
        // Feeding it the flying camera's own height keeps the map behaving the way it
        // does under the orbit rig instead of freezing at whatever zoom it left.
        _distance = _freePosition.Length();
        _targetDistance = _distance;
        ApplyFreeCamera(step);
    }

    private void UpdateFreeFlight(float step, float scale)
    {
        var forward = -new Basis(Quaternion.FromEuler(new Vector3(_freePitch, _freeYaw, 0f))).Z;
        var right = new Basis(Quaternion.FromEuler(new Vector3(0f, _freeYaw, 0f))).X;

        var move = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) move += forward;
        if (Input.IsKeyPressed(Key.S)) move -= forward;
        if (Input.IsKeyPressed(Key.D)) move += right;
        if (Input.IsKeyPressed(Key.A)) move -= right;
        if (Input.IsKeyPressed(Key.Space)) move += Vector3.Up;
        if (Input.IsKeyPressed(Key.Ctrl)) move -= Vector3.Up;

        var altitude = Mathf.Max(0.35f, _freePosition.Length() - GlobeRadius);
        var speed = _freeSpeed * Mathf.Clamp(altitude / 6f, 0.10f, 6f) * scale;

        if (move.LengthSquared() > 0.0001f)
            _freePosition += move.Normalized() * speed * step;
    }

    /// <summary>
    /// One frame of turntable. The keys keep their meanings as far as they can: W and S
    /// close on the subject and back away from it, A and D go round it, Space and Ctrl
    /// raise and lower the shot.
    /// </summary>
    private void UpdateFreeOrbit(float step, float scale)
    {
        _freeOrbitYaw += _freeAutoOrbit * step;

        var turn = 0.85f * step * scale;
        if (Input.IsKeyPressed(Key.A)) _freeOrbitYaw -= turn;
        if (Input.IsKeyPressed(Key.D)) _freeOrbitYaw += turn;
        if (Input.IsKeyPressed(Key.Space)) _freeOrbitPitch += turn;
        if (Input.IsKeyPressed(Key.Ctrl)) _freeOrbitPitch -= turn;
        _freeOrbitPitch = Mathf.Clamp(_freeOrbitPitch, -1.45f, 1.45f);

        var closing = _freeOrbitRadius * 0.9f * step * scale;
        if (Input.IsKeyPressed(Key.W)) _freeOrbitRadius -= closing;
        if (Input.IsKeyPressed(Key.S)) _freeOrbitRadius += closing;
        _freeOrbitRadius = Mathf.Clamp(_freeOrbitRadius, GlobeRadius * 0.06f, MaximumDistance * 2.5f);

        _freePosition = _freeOrbitCentre + OrbitOffset();
    }

    private Vector3 OrbitOffset() => new(
        _freeOrbitRadius * Mathf.Cos(_freeOrbitPitch) * Mathf.Sin(_freeOrbitYaw),
        _freeOrbitRadius * Mathf.Sin(_freeOrbitPitch),
        _freeOrbitRadius * Mathf.Cos(_freeOrbitPitch) * Mathf.Cos(_freeOrbitYaw));

    /// <summary>Where the camera is pointing, on the globe, or the nearest point to it.</summary>
    private Vector3 AimedSurfacePoint()
    {
        var forward = -new Basis(Quaternion.FromEuler(new Vector3(_freePitch, _freeYaw, 0f))).Z;
        // Ray against the sphere. The near root is the visible face; a ray that misses
        // entirely still has a closest approach, and the point of the globe beside it is a
        // far better thing to circle than nothing at all.
        var toCentre = -_freePosition;
        var along = toCentre.Dot(forward);
        var closest = _freePosition + forward * along;
        var missBy = closest.Length();
        if (missBy < GlobeRadius && along > 0f)
        {
            var half = Mathf.Sqrt(GlobeRadius * GlobeRadius - missBy * missBy);
            return _freePosition + forward * (along - half);
        }
        return (closest.LengthSquared() > 0.0001f ? closest.Normalized() : Vector3.Back) * GlobeRadius;
    }

    /// <summary>Hang the camera off the point it is looking at, or let it go again.</summary>
    public void SetFreeOrbit(bool orbiting)
    {
        if (!_freeCamera || _freeOrbiting == orbiting) return;
        _freeOrbiting = orbiting;
        if (orbiting)
        {
            _freeOrbitCentre = AimedSurfacePoint();
            var offset = _freePosition - _freeOrbitCentre;
            _freeOrbitRadius = Mathf.Max(GlobeRadius * 0.06f, offset.Length());
            _freeOrbitYaw = Mathf.Atan2(offset.X, offset.Z);
            _freeOrbitPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(offset.Y / Mathf.Max(0.0001f, offset.Length()), -1f, 1f)),
                -1.45f, 1.45f);
            return;
        }
        // Carry the aim back into free flight, so letting go does not snap the view round.
        var direction = (_freeOrbitCentre - _freePosition).Normalized();
        _freePitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(direction.Y, -1f, 1f)), -1.45f, 1.45f);
        _freeYaw = Mathf.Atan2(-direction.X, -direction.Z);
        _freeAutoOrbit = 0f;
    }

    public void SetFreeAutoOrbit(float radiansPerSecond) => _freeAutoOrbit = radiansPerSecond;

    public void SetFreeSmoothing(bool smoothing) => _freeSmoothing = smoothing;

    /// <summary>What the camera should look like this instant, before any chasing.</summary>
    private (Vector3 Position, Quaternion Rotation, float Fov) FreeCameraTarget()
    {
        var roll = Quaternion.FromEuler(new Vector3(0f, 0f, _freeRoll));
        if (!_freeOrbiting)
            return (_freePosition, Quaternion.FromEuler(new Vector3(_freePitch, _freeYaw, 0f)) * roll, _freeFov);

        var direction = _freeOrbitCentre - _freePosition;
        if (direction.LengthSquared() < 0.0001f) direction = Vector3.Back;
        direction = direction.Normalized();
        // LookingAt is undefined when the direction and the up vector are the same line,
        // which is exactly what a shot from directly overhead is.
        var up = Mathf.Abs(direction.Dot(Vector3.Up)) > 0.999f ? Vector3.Back : Vector3.Up;
        var aim = Basis.LookingAt(direction, up).GetRotationQuaternion();
        return (_freePosition, aim * roll, _freeFov);
    }

    private void ApplyFreeCamera(float step)
    {
        if (!IsInstanceValid(_camera)) return;
        var (position, rotation, fov) = FreeCameraTarget();

        // A time constant rather than a per-frame fraction, so the feel of it does not
        // change with the frame rate - which for a camera whose whole job is footage would
        // mean the shot looking different on the machine that records it.
        var weight = _freeSmoothing ? 1f - Mathf.Pow(0.0016f, step) : 1f;
        _shownPosition = _shownPosition.Lerp(position, weight);
        _shownRotation = _shownRotation.Slerp(rotation, weight).Normalized();
        _shownFov = Mathf.Lerp(_shownFov, fov, weight);

        _camera.Projection = Camera3D.ProjectionType.Perspective;
        _camera.Fov = _shownFov;
        _camera.GlobalTransform = new Transform3D(new Basis(_shownRotation), _shownPosition);
    }

    /// <summary>Put the camera exactly where it is being asked to be, with no chase.</summary>
    private void SnapFreeCamera()
    {
        var (position, rotation, fov) = FreeCameraTarget();
        _shownPosition = position;
        _shownRotation = rotation;
        _shownFov = fov;
        ApplyFreeCamera(0f);
    }

    /// <summary>
    /// Look and throttle. Returns true when the event belonged to the free camera, so
    /// picking, box-select and orbit dragging stay out of the way while it is flying.
    /// </summary>
    private bool HandleFreeCameraInput(InputEvent inputEvent)
    {
        // Input only ever moves the target. What the viewer sees chases it a frame at a
        // time in ApplyFreeCamera, which is what turns a hand on a mouse into a shot.
        if (inputEvent is InputEventMouseMotion look && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            if (_freeOrbiting)
            {
                // On the turntable the mouse swings the camera around the subject rather
                // than turning its head, because the head is already pointed at it.
                _freeOrbitYaw -= look.Relative.X * FreeLookSensitivity;
                _freeOrbitPitch = Mathf.Clamp(
                    _freeOrbitPitch + look.Relative.Y * FreeLookSensitivity, -1.45f, 1.45f);
                return true;
            }
            _freeYaw -= look.Relative.X * FreeLookSensitivity;
            _freePitch = Mathf.Clamp(_freePitch - look.Relative.Y * FreeLookSensitivity, -1.45f, 1.45f);
            return true;
        }

        if (inputEvent is InputEventMouseButton wheel && wheel.Pressed &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            var step = wheel.ButtonIndex == MouseButton.WheelUp ? 1.22f : 1f / 1.22f;
            // The wheel means the same thing in both modes - how fast the shot changes -
            // and on a turntable that is the distance it is orbiting at.
            if (_freeOrbiting)
                _freeOrbitRadius = Mathf.Clamp(_freeOrbitRadius / step, GlobeRadius * 0.06f,
                    MaximumDistance * 2.5f);
            else
                _freeSpeed = Mathf.Clamp(_freeSpeed * step, 0.4f, 90f);
            return true;
        }

        return false;
    }

    private const float FreeLookSensitivity = 0.0032f;

    /// <summary>
    /// The lens the orbit rig looks through, and what a flying camera starts with.
    ///
    /// One number rather than two: a free camera that began at a different focal length
    /// would reframe the shot the instant it was switched on, which is the one thing
    /// taking the camera off its rig is supposed not to do.
    /// </summary>
    private const float RigFieldOfView = 38f;

    private void ApplyCamera()
    {
        if (!IsInstanceValid(_orbit) || !IsInstanceValid(_camera)) return;
        if (_flatMap)
        {
            ApplyFlatCamera();
            return;
        }
        _camera.Projection = Camera3D.ProjectionType.Perspective;
        _orbit.Rotation = new Vector3(_pitch, _yaw, 0f);
        _camera.Position = new Vector3(0f, 0f, _distance);
        _camera.LookAt(Vector3.Zero, Vector3.Up);
    }


    private void UpdateMarkerVisibility(
        Node3D root,
        List<(Node3D Node, Vector3 Normal)> markers,
        bool withinLod)
    {
        if (!IsInstanceValid(root) || !IsInstanceValid(_camera)) return;
        root.Visible = withinLod;
        if (!withinLod) return;

        // A billboard can protrude around the limb even while its centre is on
        // the hidden hemisphere. Cull by the true surface normal so counters
        // never bleed through the globe or leave a noisy halo around it.
        if (_flatMap)
        {
            var halfHeight = _camera.Size * .5f;
            var viewportSize = _camera.GetViewport().GetVisibleRect().Size;
            var halfWidth = halfHeight * viewportSize.X / Mathf.Max(1f, viewportSize.Y);
            foreach (var marker in markers)
                if (IsInstanceValid(marker.Node))
                {
                    var offset = marker.Node.Position - _camera.Position;
                    var visible = Mathf.Abs(offset.X) < halfWidth + .1f && Mathf.Abs(offset.Y) < halfHeight + .1f;
                    if (marker.Node.Visible != visible) marker.Node.Visible = visible;
                }
            return;
        }
        var viewNormal = _camera.GlobalPosition.Normalized();
        foreach (var marker in markers)
            if (IsInstanceValid(marker.Node))
                marker.Node.Visible = marker.Normal.Dot(viewNormal) > 0.18f;
    }

    /// <summary>
    /// Bring the counters on the map in line with where the armies are.
    ///
    /// This used to be a full teardown: a fingerprint over every division in the world was
    /// compared against the last one, and if any single division had moved to another
    /// province - which, with fourteen hundred of them at speed five, is every time - every
    /// counter was freed and rebuilt. That is four thousand nodes destroyed and created
    /// eight times a second, and it is the whole of why starting time made the game stutter.
    ///
    /// Now the counters persist. A stack that is still there is updated in place, a new one
    /// is built, and one whose armies have all left is freed. In a normal second that is a
    /// handful of nodes rather than four thousand.
    /// </summary>
    private void ReconcileArmyMarkers(Simulation.Campaign campaign)
    {
        if (!MilitaryMarkersEnabled || !IsInstanceValid(_armyRoot) || _divisionTexture is null) return;

        foreach (var marker in _armyStacks.Values)
        {
            marker.Seen = false;
            marker.Stack.Clear();
        }

        foreach (var army in campaign.Armies)
        {
            var bucket = ArmyPresentationBucket(campaign, army);
            if (!CanShowArmy(army)) continue;
            if (!_armyStacks.TryGetValue(bucket, out var marker))
            {
                marker = new ArmyStackMarker();
                _armyStacks[bucket] = marker;
            }
            marker.Seen = true;
            marker.Stack.Add(army);
        }

        _vanishedStacks.Clear();
        foreach (var (bucket, marker) in _armyStacks)
            if (!marker.Seen || marker.Stack.Count == 0) _vanishedStacks.Add(bucket);
        foreach (var bucket in _vanishedStacks)
        {
            var marker = _armyStacks[bucket];
            if (marker.Node is not null && IsInstanceValid(marker.Node)) marker.Node.QueueFree();
            _armyStacks.Remove(bucket);
        }

        _armyMarkers.Clear();
        _armyByMarker.Clear();
        foreach (var marker in _armyStacks.Values)
        {
            var lead = marker.Stack[0];
            var nation = campaign.NationById(lead.NationId);
            if (nation is null) continue;

            // The counter shows the template most of the stack is made of.
            _templateVotes.Clear();
            foreach (var army in marker.Stack)
            {
                var id = army.TemplateId ?? "";
                _templateVotes[id] = _templateVotes.GetValueOrDefault(id) + 1;
            }
            var templateId = lead.TemplateId ?? "";
            var votes = -1;
            foreach (var (id, count) in _templateVotes)
                if (count > votes || (count == votes && string.CompareOrdinal(id, templateId) < 0))
                {
                    votes = count;
                    templateId = id;
                }

            if (marker.Node is null || !IsInstanceValid(marker.Node))
                CreateArmyStackMarker(marker, nation, lead, templateId);
            else if (marker.NationId != nation.Id ||
                     !string.Equals(marker.TemplateId, templateId, StringComparison.Ordinal))
            {
                marker.Plate.Texture = ArmyCounterTexture(nation, templateId);
                marker.NationId = nation.Id;
                marker.TemplateId = templateId;
            }
            if (marker.Node is null) continue;

            var location = AverageArmyLocation(marker.Stack);
            var position = PresentationPoint(location.Latitude, location.Longitude, SurfaceMarkerRadius);
            if (marker.Node.Position != position) marker.Node.Position = position;
            marker.Normal = SurfacePoint(location.Latitude, location.Longitude, 1f);

            var divisions = 0f;
            var personnel = 0f;
            foreach (var army in marker.Stack)
            {
                divisions += army.Divisions;
                personnel += DivisionPersonnel(army);
            }
            // Divisions over men, on one label rather than two. Every counter used to carry
            // a second Label3D for the personnel line, and a Label3D is some sixty kilobytes
            // of mesh and material - affordable at two hundred counters, fifty megabytes at
            // twelve hundred. The line below is the same two numbers in the same order.
            var countText = $"{Mathf.CeilToInt(divisions)}\n{personnel / 1000f:0.#}k";
            if (!string.Equals(marker.CountText, countText, StringComparison.Ordinal))
            {
                marker.Count.Text = countText;
                marker.CountText = countText;
            }

            _armyMarkers.Add((marker.Node, marker.Normal));
            _armyByMarker[marker.Node] = marker.Stack;
        }
    }

    private void CreateArmyStackMarker(ArmyStackMarker marker, Simulation.Nation nation,
        Simulation.Army lead, string templateId)
    {

        {
            var node = new Node3D { Name = $"DivisionStack_{nation.Tag}_{lead.ProvinceId}_{lead.Id}" };
            _armyRoot.AddChild(node);
            marker.Node = node;
            marker.NationId = nation.Id;
            marker.TemplateId = templateId;

            var plate = new Sprite3D
            {
                Texture = ArmyCounterTexture(nation, templateId),
                PixelSize = CounterPlatePixelSize * _counterScale,
                FixedSize = true,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = Colors.White
            };
            node.AddChild(plate);
            marker.Plate = plate;

            var divisions = new Label3D
            {
                Name = "DivisionCount",
                Text = "",
                FontSize = 21,
                PixelSize = CounterTextPixelSize * _counterScale,
                LineSpacing = -5f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                OutlineSize = Mathf.RoundToInt(CounterOutlineSize * _counterScale),
                Modulate = Ui.Colour("military.ready"),
                OutlineModulate = Ui.Colour("globe.label_outline"),
                Position = new Vector3(0f, -0.004f, 0.004f),
                NoDepthTest = true
            };
            node.AddChild(divisions);
            marker.Count = divisions;
            SizeArmyCounter(marker);

        }
    }

    /// <summary>
    /// Which counter a division belongs to: its country and the province it stands in.
    ///
    /// One rule for everybody. The player's own divisions were bucketed by province and
    /// everybody else's by army group or by whole state, so a foreign army spread across
    /// thirty provinces of front drew as a single counter reading x122 somewhere in the
    /// middle of the country. The map was telling the player something that was not true:
    /// those divisions were on the line, in a line, and the map said they were in a heap.
    ///
    /// A division with no province falls back to its own position rounded to a twentieth
    /// of a degree, which is fine ground - two divisions merge only if they are within a
    /// few kilometres of each other, and they have nowhere else to be drawn anyway.
    /// </summary>
    private static (int NationId, int LocationId, int FallbackLatitude, int FallbackLongitude)
        ArmyPresentationBucket(Simulation.Campaign campaign, Simulation.Army division)
    {
        if (division.ProvinceId > 0) return (division.NationId, division.ProvinceId, 0, 0);
        return (division.NationId, 0, Mathf.RoundToInt(division.Latitude * 20f),
            Mathf.RoundToInt(division.Longitude * 20f));
    }

    private static (float Latitude, float Longitude) AverageArmyLocation(IReadOnlyList<Simulation.Army> stack)
    {
        if (stack.Count == 0) return (0f, 0f);
        var latitude = 0f;
        var sin = 0f;
        var cos = 0f;
        for (var index = 0; index < stack.Count; index++)
        {
            var division = stack[index];
            latitude += division.Latitude;
            var radians = Mathf.DegToRad(division.Longitude);
            sin += MathF.Sin(radians);
            cos += MathF.Cos(radians);
        }
        return (latitude / stack.Count, Mathf.RadToDeg(MathF.Atan2(sin, cos)));
    }

    private void SyncArmyMarkers()
    {
        // Dropping a selection whose division no longer exists. All() over the army list
        // per selected id was quadratic in the size of a box selection.
        if (_selectedArmyIds.Count > 0)
        {
            _liveArmyIds.Clear();
            foreach (var army in _campaign.Armies)
                if (Simulation.ArmyInteraction.CanSelect(_campaign, army)) _liveArmyIds.Add(army.Id);
            _selectedArmyIds.RemoveWhere(id => !_liveArmyIds.Contains(id));
        }
        ReconcileArmyMarkers(_campaign);
    }

    private readonly HashSet<int> _liveArmyIds = new();

    private static int ArmyStructureFingerprint(Simulation.Campaign campaign)
    {
        var hash = new HashCode();
        hash.Add(campaign.Armies.Count);
        foreach (var army in campaign.Armies)
        {
            hash.Add(army.Id);
            hash.Add(army.NationId);
            hash.Add(army.TemplateId);
            var bucket = ArmyPresentationBucket(campaign, army);
            hash.Add(bucket.LocationId);
            hash.Add(bucket.FallbackLatitude);
            hash.Add(bucket.FallbackLongitude);
        }
        return hash.ToHashCode();
    }

    private void BuildFleetMarkers(Simulation.Campaign campaign)
    {
        if (!IsInstanceValid(_fleetRoot) || _fleetTexture is null) return;
        foreach (var child in _fleetRoot.GetChildren()) child.QueueFree();
        _fleetMarkers.Clear();
        _fleetByMarker.Clear();
        _fleetNodes.Clear();
        foreach (var fleet in campaign.Fleets)
        {
            if (!CanShowMilitary(fleet.NationId)) continue;
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null) continue;
            var marker = new Node3D { Name = $"Fleet_{fleet.Id}_{nation.Tag}" };
            marker.Position = SurfacePoint(fleet.Latitude, fleet.Longitude, SurfaceMarkerRadius);
            _fleetRoot.AddChild(marker);
            // A fleet flies its country's colours, the way its provinces are painted in
            // them. A sea full of identical blue counters tells a player nothing about
            // whose sea it is.
            var packed = _colorByMapIndex.TryGetValue(nation.MapIndex, out var owned) ? owned : 0x6E7B8BFFu;
            var flag = new Color(
                ((packed >> 24) & 0xFF) / 255f,
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f, 1f);
            var plate = new Sprite3D
            {
                Texture = _fleetTexture,
                PixelSize = 0.00011f,
                FixedSize = true,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = flag.Lerp(Colors.White, 0.22f) with { A = 0.94f }
            };
            marker.AddChild(plate);
            var strength = new Label3D
            {
                Text = fleet.Hulls.ToString(), FontSize = 24, PixelSize = 0.00024f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true,
                OutlineSize = 7, Modulate = Ui.Colour("globe.label_province"), OutlineModulate = Ui.Colour("globe.label_province_outline"),
                Position = new Vector3(0f, 0f, 0.004f), NoDepthTest = true
            };
            marker.AddChild(strength);
            _fleetMarkers.Add((marker, marker.Position.Normalized()));
            _fleetByMarker[marker] = fleet;
            _fleetNodes[fleet.Id] = (marker, strength, plate);
        }
    }

    /// <summary>
    /// Fleets move now, so their counters have to follow them. Rebuilding the markers every
    /// frame would be absurd for something that moves three degrees a day, so the nodes are
    /// kept and only their positions are written - and the roster is rebuilt only when the
    /// set of fleets actually changes.
    /// </summary>
    private void UpdateFleetMarkers()
    {
        if (_campaign is null || !IsInstanceValid(_fleetRoot)) return;

        var fingerprint = HashCode.Combine(_campaign.Fleets.Count, FogOfWarActive, _campaign.Player?.Id, _campaign.Player?.IsHuman);
        foreach (var fleet in _campaign.Fleets) fingerprint = HashCode.Combine(fingerprint, fleet.Id, fleet.NationId);
        if (fingerprint != _fleetRosterFingerprint)
        {
            _fleetRosterFingerprint = fingerprint;
            BuildFleetMarkers(_campaign);
        }

        for (var index = 0; index < _fleetMarkers.Count; index++)
        {
            var (node, _) = _fleetMarkers[index];
            if (!IsInstanceValid(node) || !_fleetByMarker.TryGetValue(node, out var fleet)) continue;
            var point = SurfacePoint(fleet.Latitude, fleet.Longitude, SurfaceMarkerRadius);
            node.Position = point;
            _fleetMarkers[index] = (node, point.Normalized());
            if (!_fleetNodes.TryGetValue(fleet.Id, out var parts)) continue;
            if (IsInstanceValid(parts.Count))
            {
                var text = fleet.Hulls.ToString();
                if (parts.Count.Text != text) parts.Count.Text = text;
                parts.Count.Modulate = fleet.InAction
                    ? Ui.Colour("military.battle")
                    : fleet.Id == _selectedFleetId
                        ? Ui.Colour("globe.selection")
                        : Ui.Colour("globe.label_province");
            }
        }
    }

    private bool TryPickFleet(Vector2 pixel, out Simulation.Fleet fleet)
    {
        fleet = null;
        if (ViewDistance > 13.5f || !IsInstanceValid(_camera)) return false;
        var bestDistance = 26f * 26f;
        foreach (var (node, candidate) in _fleetByMarker)
        {
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            var screen = _camera.UnprojectPosition(node.GlobalPosition);
            var distance = screen.DistanceSquaredTo(pixel);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            fleet = candidate;
        }
        return fleet is not null;
    }

    public void SelectFleet(int fleetId) => _selectedFleetId = fleetId;

    private static Texture2D CreateFleetMarkerTexture()
    {
        const int width = 160;
        const int height = 96;
        var pixels = new byte[width * height * 4];
        var background = new Color(0.07f, 0.13f, 0.18f, 0.88f);
        var border = new Color(0.48f, 0.72f, 0.80f, 0.96f);
        var ship = new Color(0.78f, 0.89f, 0.92f, 1f);
        var wave = new Color(0.26f, 0.66f, 0.78f, 1f);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = new Color(0f, 0f, 0f, 0f);
            var inside = x >= 3 && x < width - 3 && y >= 3 && y < height - 3;
            if (inside) color = x < 8 || x >= width - 8 || y < 8 || y >= height - 8 ? border : background;
            if (y >= 55 && y <= 72 && x >= 27 + (y - 55) && x <= 133 - (y - 55)) color = ship;
            if (x >= 58 && x <= 101 && y >= 36 && y < 56) color = ship;
            if (x >= 71 && x <= 86 && y >= 22 && y < 37) color = ship;
            if (x >= 77 && x <= 81 && y >= 12 && y < 23) color = ship;
            if (y >= 80 && y <= 84 && ((x / 18) + (y - 80)) % 2 == 0) color = wave;
            var write = (y * width + x) * 4;
            pixels[write] = (byte)(color.R * 255f);
            pixels[write + 1] = (byte)(color.G * 255f);
            pixels[write + 2] = (byte)(color.B * 255f);
            pixels[write + 3] = (byte)(color.A * 255f);
        }
        return ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels));
    }

    /// <summary>
    /// How much of the fog tint to draw at this zoom.
    ///
    /// Fog does two jobs and they used to be one number. The first is hiding: which
    /// formations are drawn, what a province report says, whether a battle is on the map at
    /// all. That is the vision model, it has nothing to do with the camera, and it must not
    /// - a player who could learn something by pulling the camera back would not be playing
    /// with fog at all.
    ///
    /// The second is the grey wash, which is only ever a legibility device: it says "you are
    /// not being shown this" about ground the player is looking at. Up close that is worth
    /// saying. At globe distance it is a grey blanket over most of the world, obscuring the
    /// one thing a world map is for and telling the player nothing they did not know.
    ///
    /// So the wash fades out with distance. The hiding does not.
    /// </summary>
    private void UpdateFogTint()
    {
        if (_globeMaterial is null) return;
        var defines = _campaign?.Defines;
        var full = defines?.FogTintFullDistance ?? 7f;
        var clear = defines?.FogTintClearDistance ?? 12f;

        // A mod that puts the two the wrong way round gets no tint rather than an inverted
        // one, which is the failure that would be hardest to recognise as a mistake.
        var tint = clear <= full ? 0f : 1f - Mathf.SmoothStep(full, clear, ViewDistance);
        _globeMaterial.SetShaderParameter("fog_tint", tint);
        _globeMaterial.SetShaderParameter("fog_darkness", defines?.FogTintDarkness ?? 0.74f);
        _globeMaterial.SetShaderParameter("fog_saturation", defines?.FogTintSaturation ?? 0.65f);
    }

    /// <summary>Rebuild the region index when the campaign has redrawn the map since.</summary>
    private void EnsureStateIndex()
    {
        if (_campaign is null || _campaign.MapRevision == _stateIndexRevision) return;
        _stateByMapIndex.Clear();
        foreach (var state in _campaign.States) _stateByMapIndex[state.MapIndex] = state;
        _stateIndexRevision = _campaign.MapRevision;
    }

    /// <summary>
    /// Pick up countries the campaign founded after the map was bound.
    ///
    /// The country lists were read once, at bind, because the set of countries was fixed for
    /// the length of a campaign - and it no longer is: a region can secede and what comes out
    /// is a country nothing here has heard of. Without this it holds real ground and paints
    /// in the fallback grey, has no name under the cursor, and is invisible on every map
    /// mode that reads a colour.
    ///
    /// Cheap enough to do on the ordinary map-repaint path: it compares two counts, and does
    /// real work only in the handful of frames across a whole campaign where a border was
    /// drawn by something other than a war.
    /// </summary>
    private void EnsureNationIndex()
    {
        if (_campaign is null || _campaign.Nations.Count == _nationIndexCount) return;
        foreach (var nation in _campaign.Nations)
        {
            if (_nationByMapIndex.ContainsKey(nation.MapIndex)) continue;
            _colorByMapIndex[nation.MapIndex] = ParseHex(nation.Color, 0x6E7B8BFFu);
            _nationByMapIndex[nation.MapIndex] = nation;
        }
        _nationIndexCount = _campaign.Nations.Count;
        BuildNationPalette();
        BuildCapitalMarkers(_campaign);
        GD.Print($"[dof] political map now holds {_nationIndexCount:N0} countries");
    }

    /// <summary>How many countries the colour and name indexes were built for.</summary>
    private int _nationIndexCount;

    private void BindArmyTopology()
    {
        if (_campaign is null || _provinceTopology is null) return;
        foreach (var army in _campaign.Armies)
        {
            if (_campaign.ScenarioId != "2024" && army.ProvinceId > 0) continue;
            var x = Mathf.Clamp((int)((army.Longitude + 180f) / 360f * SourceEarthData.Width), 0, SourceEarthData.Width - 1);
            var y = Mathf.Clamp((int)((90f - army.Latitude) / 180f * SourceEarthData.Height), 0, SourceEarthData.Height - 1);
            var cell = y * SourceEarthData.Width + x;
            army.StateMapIndex = SourceEarthData.States[cell];
            army.ProvinceId = _provinceTopology.CellToProvince[cell];
        }
        _campaign.AttachProvinceTopology(_provinceTopology);
        if (_campaign.ScenarioId != "2024")
            foreach (var army in _campaign.Armies)
                if (army.ProvinceId > 0 && army.ProvinceId <= _provinceTopology.ProvinceCount)
                    army.StateMapIndex = _provinceTopology.ProvinceToState[army.ProvinceId];
        GD.Print($"[dof] province graph ready: {_provinceTopology.ProvinceCount:N0} nodes, {_provinceTopology.EdgeCount:N0} edges");
        foreach (var war in _campaign.Wars)
            GD.Print($"[dof] front {war.Name}: {war.AttackerFront.Count:N0}/{war.DefenderFront.Count:N0} provinces");
    }

    /// <summary>
    /// What the control texture's alpha means to the shader: full is a transfer of title,
    /// this is an occupation. Kept a constant rather than a literal because the shader reads
    /// the same threshold from the other side, and the two have to agree.
    /// </summary>
    private const byte OccupiedStripeAlpha = 160;

    /// <summary>
    /// One texel per country, holding the colour the political map paints it.
    ///
    /// The fill used to come from a raster of five thousand by three thousand painted colours,
    /// which is sixty-six megabytes of texture to say two hundred and sixty things. Reading
    /// the colour out of a palette instead lets the shader rebuild the fill from the country
    /// id map - the boundary comes out subpixel rather than stepped - and lets the crisp
    /// raster be dropped altogether.
    /// </summary>
    private void BuildNationPalette()
    {
        if (_globeMaterial is null) return;
        var pixels = new byte[256 * 256 * 4];
        foreach (var (mapIndex, packed) in _colorByMapIndex)
        {
            if (mapIndex <= 0 || mapIndex >= 256 * 256) continue;
            var write = mapIndex * 4;
            pixels[write] = (byte)((packed >> 24) & 0xFF);
            pixels[write + 1] = (byte)((packed >> 16) & 0xFF);
            pixels[write + 2] = (byte)((packed >> 8) & 0xFF);
            pixels[write + 3] = 255;
        }
        var image = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        _globeMaterial.SetShaderParameter("nation_palette", ImageTexture.CreateFromImage(image));
    }

    private void RefreshProvinceControllers()
    {
        if (_campaign?.ProvinceMap is null || _campaign.ProvinceController is null || _globeMaterial is null) return;
        EnsureNationIndex();
        var topology = _campaign.ProvinceMap;
        var pixels = _controlPixels ??= new byte[256 * 256 * 4];
        var ownerPixels = _ownerPixels ??= new byte[256 * 256 * 4];
        Array.Clear(pixels);
        Array.Clear(ownerPixels);
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var owner = topology.ProvinceToNation[province];

            // Ownership that has moved since the raster was painted. This is what makes a
            // signed peace visible: the ground is repainted in the colour of whoever owns it
            // now, whether it changed hands at a conference, at the end of a war, or in a
            // save that was loaded halfway through one.
            if (_paintedOwner is not null && province < _paintedOwner.Length && _paintedOwner[province] != owner)
            {
                var ownerPacked = _colorByMapIndex.TryGetValue(owner, out var ownerColor) ? ownerColor : 0x6E7B8BFFu;
                var ownerWrite = province * 4;
                ownerPixels[ownerWrite] = (byte)((ownerPacked >> 24) & 0xFF);
                ownerPixels[ownerWrite + 1] = (byte)((ownerPacked >> 16) & 0xFF);
                ownerPixels[ownerWrite + 2] = (byte)((ownerPacked >> 8) & 0xFF);
                ownerPixels[ownerWrite + 3] = 255;
            }

            var controller = _campaign.ProvinceController[province];
            if (controller == owner) continue;
            var packed = _colorByMapIndex.TryGetValue(controller, out var color) ? color : 0x6E7B8BFFu;
            var write = province * 4;
            pixels[write] = (byte)((packed >> 24) & 0xFF);
            pixels[write + 1] = (byte)((packed >> 16) & 0xFF);
            pixels[write + 2] = (byte)((packed >> 8) & 0xFF);
            // Held, not owned. The shader reads this alpha as an instruction rather than an
            // opacity: short of full it wears the occupier's colour as stripes over whoever
            // the province still legally belongs to, so an advance never looks like an
            // annexation and a peace conference can be read.
            pixels[write + 3] = OccupiedStripeAlpha;
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

        if (_controllerTexture is null)
        {
            _controlImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
            _controllerTexture = ImageTexture.CreateFromImage(_controlImage);
            _globeMaterial.SetShaderParameter("province_control_colors", _controllerTexture);
        }
        else
        {
            _controlImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
            _controllerTexture.Update(_controlImage);
        }

        if (_ownerTexture is null)
        {
            _ownerImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, ownerPixels);
            _ownerTexture = ImageTexture.CreateFromImage(_ownerImage);
            _globeMaterial.SetShaderParameter("province_owner_colors", _ownerTexture);
        }
        else
        {
            _ownerImage.SetData(256, 256, false, Image.Format.Rgba8, ownerPixels);
            _ownerTexture.Update(_ownerImage);
        }
        _controlRevision = _campaign.MapRevision;
        BuildFrontPreview();
        _frontRevision = _controlRevision;
    }

    // Positions and labels are written by the reconcile itself now, off the node handles it
    // already holds - there is nothing left for a second pass to do.

    private static int DivisionPersonnel(Simulation.Army division) => division.Divisions > 1.01f
        ? (int)MathF.Round(division.Divisions * Simulation.MilitaryBootstrap.DivisionSize * division.Strength)
        : Math.Max(0, division.Personnel);

    private void BuildCapitalMarkers(Simulation.Campaign campaign)
    {
        if (!IsInstanceValid(_capitalRoot) || _capitalTexture is null) return;

        foreach (var child in _capitalRoot.GetChildren())
            child.QueueFree();
        _capitalMarkers.Clear();

        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrWhiteSpace(nation.CapitalName)) continue;

            var marker = new Sprite3D
            {
                Name = $"Capital_{nation.Tag}",
                Texture = _capitalTexture,
                PixelSize = 0.00016f,
                FixedSize = true,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = Ui.Colour("globe.selection")
            };

            marker.Position = SurfacePoint(nation.CapitalLatitude, nation.CapitalLongitude, SurfaceMarkerRadius);
            _capitalRoot.AddChild(marker);
            _capitalMarkers.Add((marker, marker.Position.Normalized()));
        }
    }

    private Dictionary<int, TerritoryLabelAnchor> _lastLabelAnchors;
    private readonly Dictionary<int, string> _lastLabelNames = new();

    /// <summary>
    /// Who owned what when the names were last placed, and the campaign revision that scan
    /// was made at.
    ///
    /// Separate from `_paintedOwner`, which is deliberately frozen at what the raster was
    /// painted with and is how a moved border is coloured in. This one moves forward every
    /// time the names are re-laid, because its only job is to answer "has anything changed
    /// since the last time we asked".
    /// </summary>
    private ushort[] _labelOwner;

    private int _labelRevision = -1;
    private Task<Dictionary<int, TerritoryLabelAnchor>> _labelTask;

    public void RefreshCountryNames()
    {
        if (_lastLabelAnchors is null) return;
        foreach (var (id, name) in _lastLabelNames)
            if (_nationByMapIndex.TryGetValue(id, out var nation) && nation.MapName != name)
            { BuildCountryLabels(_lastLabelAnchors); return; }
    }

    /// <summary>
    /// Put the names back where the borders are now.
    ///
    /// Two passes, because they cost different amounts. Noticing that ownership moved is a
    /// walk over thirty-eight thousand province owners and happens only when the campaign
    /// says its map changed. Working out where a name goes is a flood fill over sixteen
    /// million cells, and that goes on a task - a peace treaty should not drop a frame, and
    /// a name arriving a moment after the colour does is not something anybody can see.
    /// </summary>
    private void RefreshCountryLabelPlacement()
    {
        if (_labelTask is not null)
        {
            if (!_labelTask.IsCompleted) return;
            if (_labelTask.IsCompletedSuccessfully) BuildCountryLabels(_labelTask.Result);
            else if (_labelTask.IsFaulted)
                GD.PushWarning("[dof] country names could not be re-placed: " +
                               _labelTask.Exception?.GetBaseException().Message);
            _labelTask = null;
            return;
        }

        if (_campaign is null || _provinceTopology is null || _labelOwner is null) return;
        _labelRevision = _campaign.MapRevision;

        var owners = _provinceTopology.ProvinceToNation;
        var moved = false;
        for (var province = 1; province < owners.Length && province < _labelOwner.Length; province++)
            if (owners[province] != _labelOwner[province]) { moved = true; break; }
        if (!moved) return;

        // Copied here, on the main thread, so the task reads a world that cannot move
        // underneath it - and recorded as the new baseline now rather than when the task
        // finishes, so a border that moves again while it runs is still noticed afterwards.
        var snapshot = (ushort[])owners.Clone();
        _labelOwner = snapshot;
        var cells = _provinceTopology.CellToProvince;
        var width = _provinceTopology.Width;
        var height = _provinceTopology.Height;
        _labelTask = Task.Run(() =>
        {
            var control = new ushort[cells.Length];
            for (var index = 0; index < cells.Length; index++)
            {
                var province = cells[index];
                control[index] = province > 0 && province < snapshot.Length ? snapshot[province] : (ushort)0;
            }
            return BuildTerritoryLabelAnchors(control, width, height);
        });
    }

    private void BuildCountryLabels(Dictionary<int, TerritoryLabelAnchor> anchors)
    {
        if (!IsInstanceValid(_countryLabelRoot)) return;
        _lastLabelAnchors = anchors;
        _lastLabelNames.Clear();
        foreach (var child in _countryLabelRoot.GetChildren()) child.QueueFree();
        _countryLabels.Clear();
        _countryGlyphs.Clear();

        var countryFont = PresentationFonts.For("map.countries");

        foreach (var (mapIndex, anchor) in anchors)
        {
            if (!_nationByMapIndex.TryGetValue(mapIndex, out var nation) || anchor.Cells < 80)
                continue;

            var text = nation.MapName.ToUpperInvariant();
            _lastLabelNames[mapIndex] = nation.MapName;
            if (text.Length == 0) continue;
            var group = new Node3D { Name = $"CountryLabel_{nation.Tag}" };
            _countryLabelRoot.AddChild(group);

            // One letter at a time, along the chord.
            //
            // The name used to be a single label with its horizontal scale stretched to fill
            // the country, which is not what an atlas does and does not look like one: it
            // makes the glyphs themselves wide, so RUSSIA came out as a smeared word rather
            // than a spaced one. Paradox tracks the letters apart and leaves their shapes
            // alone, and a letter placed on its own also gets its own tangent to the globe -
            // so the name curves with the planet instead of lying flat across it, and a
            // slanted baseline turns each letter rather than shearing the word.
            var longitudeDegreesPerCell = 360f / SourceEarthData.Width;
            var latitudeDegreesPerCell = 180f / SourceEarthData.Height;

            var centreLatitude = 90f - (anchor.CenterY + 0.5f) * latitudeDegreesPerCell;
            var centreLongitude = -180f + (WrapCell(anchor.CenterX, SourceEarthData.Width) + 0.5f) * longitudeDegreesPerCell;
            var latitudeRadians = Mathf.DegToRad(centreLatitude);

            // The chord in map cells, turned into a length on the globe. Longitude degrees
            // shrink with the cosine of latitude, which is why Norway's name is not the
            // width its pixel count suggests.
            var spanLongitudeDegrees = anchor.AxisX * anchor.HalfSpanPixels * longitudeDegreesPerCell;
            var spanLatitudeDegrees = -anchor.AxisY * anchor.HalfSpanPixels * latitudeDegreesPerCell;
            var halfSpanRadians = Mathf.DegToRad(Mathf.Sqrt(
                Mathf.Pow(spanLongitudeDegrees * Mathf.Cos(latitudeRadians), 2f) +
                spanLatitudeDegrees * spanLatitudeDegrees));
            var span = Mathf.Max(0.02f, GlobeRadius * halfSpanRadians * 2f);

            // How tall the letters may be: what the country has room for across the
            // baseline, and never so tall that they would spill over the edge of it.
            var thicknessDegrees = anchor.ThicknessPixels * latitudeDegreesPerCell;
            var roomForHeight = GlobeRadius * Mathf.DegToRad(thicknessDegrees) * 0.42f;

            var fontSize = Math.Clamp((int)Simulation.InterfaceSkin.Metric("map.country_font_size", 64), 24, 128);
            // How far apart tracked capitals sit, as a share of their height. Atlas names are
            // set loose; this is what makes them read as cartography rather than as a caption.
            var tracking = Math.Clamp(Simulation.InterfaceSkin.Metric("map.country_tracking", 0.86f), 0.4f, 1.5f);
            var glyphCount = text.Length;

            // Size the letters first, then space them - not the other way round.
            //
            // Deriving the height from the spacing, and the spacing from "fill the whole
            // chord", is what scattered the first attempt: a thin country got tiny letters
            // flung from one end of itself to the other, because the width was fixed and the
            // height had to give. Height comes from the room across the baseline, spacing
            // follows from height, and if the word is then longer than the country the whole
            // thing shrinks together - which is what an atlas does with Liechtenstein.
            // Two ceilings, and the lower one wins: as tall as the country is thick across the
            // baseline, and as tall as filling the chord's length would allow. Using only the
            // first made continental names small - India had six times the width it was
            // using - and using only the second would write across countries too thin to
            // carry the letters.
            var fromSpan = span / Mathf.Max(1, glyphCount) / tracking;
            var height = Mathf.Clamp(Mathf.Min(roomForHeight, fromSpan), 0.010f, 0.340f);
            var advance = height * tracking;

            // Room for the glyph boxes at the ends, not only for their centres. n letters
            // at this advance occupy n advances of chord once the outer halves are counted,
            // so that is the constraint - and it is why names stopped ending in the sea.
            var widest = span / Mathf.Max(1, glyphCount);
            if (advance > widest)
            {
                advance = widest;
                height = advance / tracking;
            }
            var pixelSize = Mathf.Clamp(height / fontSize, 0.00016f, 0.00260f);

            var flatRotation = -Mathf.Atan2(anchor.AxisY, anchor.AxisX);
            var outline = Math.Max(4, (int)Mathf.Round(fontSize * 0.15f));

            // Back into map cells: what share of the chord the word actually needs, centred
            // on the chord's own middle.
            var occupied = glyphCount <= 1 ? 0f : Mathf.Min(1f, advance * (glyphCount - 1) / Mathf.Max(0.0001f, span));
            var reachPixels = anchor.HalfSpanPixels * occupied;
            var startX = anchor.CenterX - anchor.AxisX * reachPixels;
            var startY = anchor.CenterY - anchor.AxisY * reachPixels;
            var stepX = glyphCount <= 1 ? 0f : anchor.AxisX * reachPixels * 2f / (glyphCount - 1);
            var stepY = glyphCount <= 1 ? 0f : anchor.AxisY * reachPixels * 2f / (glyphCount - 1);

            // Where every letter lands, worked out before any of them is built, because a
            // letter's heading comes from its neighbours rather than from itself.
            var latitudes = new float[glyphCount];
            var longitudes = new float[glyphCount];
            var points = new Vector3[glyphCount];
            for (var index = 0; index < glyphCount; index++)
            {
                var cellX = glyphCount <= 1 ? anchor.CenterX : startX + stepX * index;
                var cellY = glyphCount <= 1 ? anchor.CenterY : startY + stepY * index;
                latitudes[index] = Mathf.Clamp(90f - (cellY + 0.5f) * latitudeDegreesPerCell, -88f, 88f);
                longitudes[index] = -180f + (WrapCell(cellX, SourceEarthData.Width) + 0.5f) * longitudeDegreesPerCell;
                points[index] = SurfacePoint(latitudes[index], longitudes[index], SurfaceTextRadius);
            }

            // The heading of the word, taken once at its middle. Every letter then turns to
            // the direction of the line it is standing on rather than to its own local east.
            //
            // That distinction is the whole of the fix. A tangent basis built from the local
            // east and north at each letter's own position looks right at the equator and
            // falls apart towards the poles, because meridians converge: at sixty degrees
            // north the same map-space direction points somewhere measurably different two
            // letters along. Sweden and Finland came out as fans of tilted letters. Carrying
            // one heading along the path and projecting it into each letter's tangent plane
            // keeps the word straight, and lets it curve with the planet the way an atlas
            // name does instead of shearing.
            var wordHeading = CountryLabelSurfaceBasis(
                latitudes[glyphCount / 2], longitudes[glyphCount / 2], anchor.AxisX, anchor.AxisY).X;

            for (var index = 0; index < glyphCount; index++)
            {
                var character = text[index];
                if (character == ' ') continue;

                var latitude = latitudes[index];
                var longitude = longitudes[index];
                var position = points[index];

                // Prefer the direction the word is actually travelling here - the chord
                // between the neighbouring letters - and fall back to the shared heading at
                // the ends and for one-letter names.
                var heading = wordHeading;
                if (glyphCount > 1)
                {
                    var before = points[Math.Max(0, index - 1)];
                    var after = points[Math.Min(glyphCount - 1, index + 1)];
                    var along = after - before;
                    if (along.LengthSquared() > 0.0000001f) heading = along.Normalized();
                }

                var normal = position.Normalized();
                var baseline = (heading - normal * heading.Dot(normal));
                baseline = baseline.LengthSquared() > 0.0000001f ? baseline.Normalized() : wordHeading;
                var up = normal.Cross(baseline).Normalized();
                var surfaceBasis = new Basis(baseline, up, normal).Orthonormalized();

                var glyph = new Label3D
                {
                    Name = $"Glyph{index}",
                    Text = character.ToString(),
                    FontSize = fontSize,
                    Font = countryFont,
                    PixelSize = pixelSize,
                    FixedSize = false,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    OutlineSize = outline,
                    Modulate = new Color(0.93f, 0.94f, 0.96f, 0.88f),
                    OutlineModulate = new Color(0.02f, 0.032f, 0.048f, 0.92f),
                    NoDepthTest = false,
                    Position = SurfacePoint(latitude, longitude, SurfaceTextRadius),
                    Basis = surfaceBasis,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                group.AddChild(glyph);
                _countryGlyphs.Add((glyph, latitude, longitude, surfaceBasis, flatRotation));
            }

            var center = SurfacePoint(centreLatitude, centreLongitude, SurfaceTextRadius);
            _countryLabels.Add((group, center.Normalized(), anchor.Cells));
        }
    }

    /// <summary>A map column, wrapped round the date line.</summary>
    private static float WrapCell(float x, int width)
    {
        var wrapped = x % width;
        return wrapped < 0 ? wrapped + width : wrapped;
    }

    /// <summary>
    /// Whether country names are drawn across the map at all. Some players want the
    /// typography and some want to see the borders under it; it is not a decision the game
    /// should make for them.
    /// </summary>
    public bool CountryLabelsEnabled { get; set; } = true;

    private void UpdateCountryLabelVisibility()
    {
        if (!IsInstanceValid(_countryLabelRoot) || !IsInstanceValid(_camera)) return;
        if (!CountryLabelsEnabled) { _countryLabelRoot.Visible = false; return; }
        // At tactical zoom, country names yield to states/provinces and unit
        // counters. Keeping continent-scale typography there would cover the map.
        _countryLabelRoot.Visible = _mapMode is not (StrategicMapMode.Terrain or StrategicMapMode.Relief) && (_flatMap
            ? _flatZoom is >= 4.4f and <= 10.8f
            : _distance is >= 7.9f and <= 16.5f);
        if (!_countryLabelRoot.Visible) return;
        var minimumCells = _flatMap
            ? _flatZoom > 8.8f ? 26000 : _flatZoom > 6.2f ? 6500 : _flatZoom > 4.0f ? 1500 : 150
            : _distance > 11.5f ? 26000 : _distance > 9.5f ? 6500 : _distance > 7.4f ? 1500 : 150;
        var viewNormal = _flatMap ? Vector3.Zero : _camera.GlobalPosition.Normalized();
        var horizonThreshold = _distance > 11.5f ? 0.72f : _distance > 9.5f ? 0.62f : _distance > 7.4f ? 0.50f : 0.38f;
        foreach (var label in _countryLabels)
            if (IsInstanceValid(label.Node))
            {
                // Stored normals are globe-local. Use the label root's inherited
                // rotation before comparing with the camera; otherwise rotating the
                // globe leaves labels from the old hemisphere visible at its rim.
                var worldNormal = (label.Node.GlobalTransform.Basis * label.Normal).Normalized();
                label.Node.Visible = label.Cells >= minimumCells && (_flatMap || worldNormal.Dot(viewNormal) > horizonThreshold);
            }
    }

    private static Basis CountryLabelSurfaceBasis(
        float latitude, float longitude, float mapAxisX, float mapAxisY)
    {
        var latitudeRadians = Mathf.DegToRad(latitude);
        var longitudeRadians = Mathf.DegToRad(longitude + 180f);
        var normal = SurfacePoint(latitude, longitude, 1f).Normalized();
        var east = new Vector3(Mathf.Cos(longitudeRadians), 0f, -Mathf.Sin(longitudeRadians)).Normalized();
        var north = new Vector3(
            -Mathf.Sin(latitudeRadians) * Mathf.Sin(longitudeRadians),
            Mathf.Cos(latitudeRadians),
            -Mathf.Sin(latitudeRadians) * Mathf.Cos(longitudeRadians)).Normalized();
        var baseline = (east * mapAxisX - north * mapAxisY).Normalized();
        var up = normal.Cross(baseline).Normalized();
        return new Basis(baseline, up, normal).Orthonormalized();
    }

    private static Vector3 SurfacePoint(float latitude, float longitude, float radius)
    {
        var lat = Mathf.DegToRad(latitude);
        // The source map's seam is at -180 degrees; SphereMesh uses 0 at its own seam.
        var lon = Mathf.DegToRad(longitude + 180f);
        var horizontal = Mathf.Cos(lat);
        return new Vector3(horizontal * Mathf.Sin(lon), Mathf.Sin(lat), horizontal * Mathf.Cos(lon)) * radius;
    }

    private static MapBuildResult BuildPoliticalMap(Dictionary<int, uint> colors)
    {
        var control = SourceEarthData.Control;
        var states = SourceEarthData.States;
        var sourceWidth = SourceEarthData.Width;
        var sourceHeight = SourceEarthData.Height;
        var pixels = new byte[MapWidth * MapHeight * 4];
        var regionIds = new byte[MapWidth * MapHeight * 2];
        var provinceIds = new byte[MapWidth * MapHeight * 2];
        var nationIds = new byte[MapWidth * MapHeight * 2];
        if (!Simulation.MapRasterCatalog.TryBuildProvinceTopology(
                states, control, sourceWidth, sourceHeight, out var provinces, out var provinceSource))
            throw new InvalidDataException(
                $"game/map_data/provinces.png + definition.csv could not be loaded ({provinceSource}). " +
                "The base game never generates replacement province IDs at runtime; rebuild the data map with " +
                "Tools/MapDataCompiler or install a mod that supplies a complete raster pair.");
        var hydrology = Simulation.HydrologyCatalog.BuildRaster(sourceWidth, sourceHeight);
        provinces.AttachHydrology(hydrology);
        var labelAnchors = BuildTerritoryLabelAnchors(control, sourceWidth, sourceHeight);

        for (var y = 0; y < MapHeight; y++)
        {
            var sourceY = y * sourceHeight / MapHeight;
            var row = sourceY * sourceWidth;
            var southRow = Math.Min(sourceY + 1, sourceHeight - 1) * sourceWidth;

            for (var x = 0; x < MapWidth; x++)
            {
                var sourceX = x * sourceWidth / MapWidth;
                var index = row + sourceX;
                var controlId = control[index];
                var write = (y * MapWidth + x) * 4;
                var idWrite = (y * MapWidth + x) * 2;
                var regionId = states[index];
                var provinceId = provinces.CellToProvince[index];
                nationIds[idWrite] = (byte)(controlId & 0xFF);
                nationIds[idWrite + 1] = (byte)((controlId >> 8) & 0xFF);
                regionIds[idWrite] = (byte)(regionId & 0xFF);
                regionIds[idWrite + 1] = (byte)((regionId >> 8) & 0xFF);
                if (provinceId > 0)
                {
                    provinceIds[idWrite] = (byte)(provinceId & 0xFF);
                    provinceIds[idWrite + 1] = (byte)((provinceId >> 8) & 0xFF);
                }

                // Alpha is a land mask; the shader supplies exactly the original ocean
                // texture where the source raster says water.
                if (controlId == 0)
                {
                    pixels[write + 3] = 0;
                    continue;
                }

                var packed = colors.TryGetValue(controlId, out var color) ? color : 0x6E7B8BFFu;
                var east = row + ((sourceX + 1) % sourceWidth);
                var west = row + (sourceX == 0 ? sourceWidth - 1 : sourceX - 1);
                var south = southRow + sourceX;
                var north = (sourceY == 0 ? row : row - sourceWidth) + sourceX;

                var shade = control[east] != controlId || control[west] != controlId ||
                    control[south] != controlId || control[north] != controlId
                    ? 0.34f
                    : states[east] != states[index] || states[west] != states[index] ||
                      states[south] != states[index] || states[north] != states[index]
                        ? 0.70f
                        : 0.92f;

                pixels[write] = (byte)(((packed >> 24) & 0xFF) * shade);
                pixels[write + 1] = (byte)(((packed >> 16) & 0xFF) * shade);
                pixels[write + 2] = (byte)(((packed >> 8) & 0xFF) * shade);
                pixels[write + 3] = 255;
            }
        }

        // Preserve the decoded grids: exact country/state picking is a core map feature.
        // This prevents a click from falling back to an approximate rendered-color lookup.
        if (provinces.ProvinceCount > ushort.MaxValue)
            throw new InvalidOperationException($"Province count {provinces.ProvinceCount} exceeds the 16-bit map contract.");
        return new MapBuildResult
        {
            PoliticalPixels = pixels,
            RegionIds = regionIds,
            ProvinceIds = provinceIds,
            NationIds = nationIds,
            HydrologyPixels = hydrology.Pixels,
            Provinces = provinces,
            LabelAnchors = labelAnchors,
            ProvinceRasterLoaded = true,
            ProvinceSource = provinceSource,
            HydrologySource = hydrology.Source
        };
    }

    private static Dictionary<int, TerritoryLabelAnchor> BuildTerritoryLabelAnchors(
        ushort[] control, int width, int height)
    {
        var visited = new System.Collections.BitArray(control.Length);
        var queue = new List<int>(32768);
        var anchors = new Dictionary<int, TerritoryLabelAnchor>();

        for (var start = 0; start < control.Length; start++)
        {
            var nation = control[start];
            if (nation == 0 || visited[start]) continue;
            queue.Clear();
            queue.Add(start);
            visited[start] = true;
            var startX = start % width;
            long sumX = 0;
            long sumY = 0;

            for (var head = 0; head < queue.Count; head++)
            {
                var cell = queue[head];
                var x = cell % width;
                var y = cell / width;
                var dx = x - startX;
                if (dx > width / 2) dx -= width;
                else if (dx < -width / 2) dx += width;
                sumX += startX + dx;
                sumY += y;
                TryAdd(cell - width);
                TryAdd(cell + width);
                TryAdd(y * width + (x == 0 ? width - 1 : x - 1));
                TryAdd(y * width + (x + 1 == width ? 0 : x + 1));
            }

            if (anchors.TryGetValue(nation, out var old) && old.Cells >= queue.Count) continue;
            var meanX = (double)sumX / queue.Count;
            var meanY = (double)sumY / queue.Count;
            // Principal-component orientation produces the familiar strategy-map
            // baseline: Russia and Chile follow their long land axis instead of
            // receiving a detached horizontal nameplate. Sampling caps startup cost
            // for continental countries while retaining the actual component shape.
            var sampleStep = Math.Max(1, queue.Count / 8192);
            double covXX = 0, covXY = 0, covYY = 0;
            var samples = 0;
            for (var index = 0; index < queue.Count; index += sampleStep)
            {
                var cell = queue[index];
                var x = cell % width;
                var y = cell / width;
                var dx = x - startX;
                if (dx > width / 2) dx -= width;
                else if (dx < -width / 2) dx += width;
                var ux = startX + dx;
                var px = ux - meanX;
                var py = y - meanY;
                covXX += px * px;
                covXY += px * py;
                covYY += py * py;
                samples++;
            }
            covXX /= Math.Max(1, samples);
            covXY /= Math.Max(1, samples);
            covYY /= Math.Max(1, samples);
            var angle = 0.5 * Math.Atan2(2.0 * covXY, covXX - covYY);
            // The old rule pinned every baseline to within seven degrees of horizontal,
            // because one stretched label cannot rotate its own letters and a steep one
            // read like a staircase. Each letter is now placed and turned separately, so
            // the baseline may follow the country the way an atlas does - down Chile, along
            // Norway - and stop short of vertical, which nobody can read.
            angle = Math.Clamp(angle, -0.60, 0.60);
            var axisX = (float)Math.Cos(angle);
            var axisY = (float)Math.Sin(angle);
            if (axisX < -0.05f)
            {
                axisX = -axisX;
                axisY = -axisY;
            }

            var minMajor = double.MaxValue;
            var maxMajor = double.MinValue;
            var minMinor = double.MaxValue;
            var maxMinor = double.MinValue;
            for (var index = 0; index < queue.Count; index += sampleStep)
            {
                var cell = queue[index];
                var x = cell % width;
                var y = cell / width;
                var dx = x - startX;
                if (dx > width / 2) dx -= width;
                else if (dx < -width / 2) dx += width;
                var px = startX + dx - meanX;
                var py = y - meanY;
                var major = px * axisX + py * axisY;
                var minor = -px * axisY + py * axisX;
                minMajor = Math.Min(minMajor, major);
                maxMajor = Math.Max(maxMajor, major);
                minMinor = Math.Min(minMinor, minor);
                maxMinor = Math.Max(maxMinor, minor);
            }

            // Where the name will actually fit. The extents above are a bounding box in the
            // principal frame and are used only to bound the search; what comes back is a
            // line that lies inside this country and nowhere else.
            var padMajor = (maxMajor - minMajor) * 0.12;
            var padMinor = (maxMinor - minMinor) * 0.12;
            var chord = FindLabelChord(control, width, height, nation, meanX, meanY, angle,
                minMajor - padMajor, maxMajor + padMajor,
                minMinor - padMinor, maxMinor + padMinor);

            // The largest piece keeps the name, not the last one scanned.
            //
            // This used to assign unconditionally, which is the same thing while a country is
            // in one piece and stops being the same thing the moment one is not: a China that
            // has annexed North America has two pieces, and row-major scanning would have
            // handed the name to whichever of them ended furthest down the raster.
            if (!anchors.TryGetValue(nation, out var existing) || queue.Count > existing.Cells)
                anchors[nation] = new TerritoryLabelAnchor(
                    (float)chord.CentreX,
                    (float)chord.CentreY,
                    chord.AxisX,
                    chord.AxisY,
                    Math.Max(3f, chord.Length * 0.5f),
                    Math.Max(2f, chord.Thickness),
                    queue.Count);

            void TryAdd(int next)
            {
                if (next < 0 || next >= control.Length || visited[next] || control[next] != nation) return;
                visited[next] = true;
                queue.Add(next);
            }
        }
        return anchors;
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
        public byte[] RegionIds;
        public byte[] ProvinceIds;
        public byte[] NationIds;
        public byte[] HydrologyPixels;
        public Simulation.ProvinceTopology Provinces;
        public Dictionary<int, TerritoryLabelAnchor> LabelAnchors;
        public bool ProvinceRasterLoaded;
        public string ProvinceSource = "";
        public string HydrologySource = "";
    }

    /// <summary>
    /// Where a country's name goes, and how much room it actually has.
    ///
    /// <see cref="HalfSpanPixels"/> and <see cref="ThicknessPixels"/> are measured along a
    /// line that lies entirely inside the country - see <see cref="FindLabelChord"/> - rather
    /// than taken from its bounding box. The distinction is the whole of why names used to
    /// run off into the sea and across their neighbours: the bounding box of Norway is most
    /// of Scandinavia, and almost none of it is Norway.
    /// </summary>
    private readonly record struct TerritoryLabelAnchor(
        float CenterX,
        float CenterY,
        float AxisX,
        float AxisY,
        float HalfSpanPixels,
        float ThicknessPixels,
        int Cells);

    /// <summary>The longest straight run of one country's own ground, and how thick it is there.</summary>
    private readonly record struct LabelChord(
        double CentreX, double CentreY, float AxisX, float AxisY, float Length, float Thickness);

    /// <summary>
    /// The widest place a name will fit without leaving the country.
    ///
    /// A country's name should sit on the country. That sounds too obvious to need code, and
    /// it needed code: the span came from the bounding box of the largest land component, so
    /// Chile's name was as wide as Chile is tall, Norway's crossed Sweden, and Indonesia's
    /// lay mostly on the Java Sea. A bounding box is a statement about extremes, and a
    /// country is the ground between them.
    ///
    /// So the line is searched for instead. Candidate baselines are swept through a range of
    /// angles around the country's principal axis and a range of parallel offsets across it;
    /// each is walked cell by cell, and what is measured is the longest **contiguous** run of
    /// this country's own ground. Contiguity does the work of several special cases at once -
    /// a run cannot jump a strait, so Java and Sumatra cannot share a baseline, and it cannot
    /// cross a border, so nothing lands on a neighbour.
    ///
    /// The best few runs by length are then measured for thickness, because a name also has
    /// height: the longest chord across Chile is a hairline, and a name written on it would
    /// spill into Argentina and the Pacific in the other direction. What comes back is the
    /// line with the best area of text it could carry.
    /// </summary>
    private static LabelChord FindLabelChord(
        ushort[] control, int width, int height, ushort nation,
        double meanX, double meanY, double principalAngle,
        double minMajor, double maxMajor, double minMinor, double maxMinor)
    {
        const int angleSteps = 9;
        const int offsetSteps = 17;
        const double angleSweep = 0.38;
        const int keepBest = 10;

        var majorSpan = Math.Max(4.0, maxMajor - minMajor);
        var minorSpan = Math.Max(2.0, maxMinor - minMinor);

        // Long walks are sampled coarsely. A continental country is thousands of cells
        // across and the answer does not change for looking at every one of them.
        var step = Math.Max(1.0, majorSpan / 520.0);

        Span<double> bestLength = stackalloc double[keepBest];
        Span<double> bestStart = stackalloc double[keepBest];
        Span<double> bestAngle = stackalloc double[keepBest];
        Span<double> bestOffset = stackalloc double[keepBest];
        for (var slot = 0; slot < keepBest; slot++) bestLength[slot] = 0;

        for (var angleIndex = 0; angleIndex < angleSteps; angleIndex++)
        {
            var angle = principalAngle +
                        (angleSteps == 1 ? 0 : (angleIndex / (double)(angleSteps - 1) - 0.5) * 2 * angleSweep);
            var axisX = Math.Cos(angle);
            var axisY = Math.Sin(angle);

            for (var offsetIndex = 0; offsetIndex < offsetSteps; offsetIndex++)
            {
                var offset = minMinor + minorSpan * (offsetIndex + 0.5) / offsetSteps;
                var originX = meanX - axisY * offset;
                var originY = meanY + axisX * offset;

                var runStart = 0.0;
                var runLength = 0.0;
                var currentStart = double.NaN;
                for (var t = minMajor - step; t <= maxMajor + step; t += step)
                {
                    var px = originX + axisX * t;
                    var py = originY + axisY * t;
                    var cellY = (int)Math.Round(py);
                    var inside = false;
                    if (cellY >= 0 && cellY < height)
                    {
                        var cellX = ((int)Math.Round(px) % width + width) % width;
                        inside = control[cellY * width + cellX] == nation;
                    }

                    if (inside)
                    {
                        if (double.IsNaN(currentStart)) currentStart = t;
                        var length = t - currentStart;
                        if (length > runLength)
                        {
                            runLength = length;
                            runStart = currentStart;
                        }
                    }
                    else currentStart = double.NaN;
                }
                if (runLength <= 0) continue;

                // Keep the best few by length; thickness costs another walk and is only
                // worth paying for on candidates that could win.
                for (var slot = 0; slot < keepBest; slot++)
                {
                    if (runLength <= bestLength[slot]) continue;
                    for (var shift = keepBest - 1; shift > slot; shift--)
                    {
                        bestLength[shift] = bestLength[shift - 1];
                        bestStart[shift] = bestStart[shift - 1];
                        bestAngle[shift] = bestAngle[shift - 1];
                        bestOffset[shift] = bestOffset[shift - 1];
                    }
                    bestLength[slot] = runLength;
                    bestStart[slot] = runStart;
                    bestAngle[slot] = angle;
                    bestOffset[slot] = offset;
                    break;
                }
            }
        }

        var chord = new LabelChord(meanX, meanY,
            (float)Math.Cos(principalAngle), (float)Math.Sin(principalAngle),
            (float)(majorSpan * 0.35), (float)(minorSpan * 0.35));
        var bestScore = 0.0;

        for (var slot = 0; slot < keepBest && bestLength[slot] > 0; slot++)
        {
            var angle = bestAngle[slot];
            var axisX = Math.Cos(angle);
            var axisY = Math.Sin(angle);
            var offset = bestOffset[slot];
            var originX = meanX - axisY * offset;
            var originY = meanY + axisX * offset;
            var middle = bestStart[slot] + bestLength[slot] * 0.5;
            var centreX = originX + axisX * middle;
            var centreY = originY + axisY * middle;

            var thickness = Thickness(control, width, height, nation, centreX, centreY, -axisY, axisX,
                Math.Max(2.0, minorSpan));
            // Length times height, because what a name needs is area - and a hairline chord
            // across a long thin country is worse than a shorter one with room to breathe.
            var score = bestLength[slot] * Math.Min(thickness, bestLength[slot] * 0.5);

            // And a thumb on the scale for the horizontal. A country that is genuinely long
            // and thin - Chile, Norway, Vietnam - has a diagonal so much better than its
            // horizontal that this cannot overturn it, which is the point. A roughly square
            // country like France has several near-equal chords, and an atlas picks the flat
            // one every time; without this the winner was whichever diagonal happened to be
            // a few cells longer, and France read as though it were falling over.
            score *= 1.0 - 0.38 * Math.Abs(Math.Sin(angle));
            if (score <= bestScore) continue;
            bestScore = score;
            chord = new LabelChord(centreX, centreY, (float)axisX, (float)axisY,
                (float)bestLength[slot], (float)thickness);
        }

        return chord;
    }

    /// <summary>How much of the country there is above and below a point, across the baseline.</summary>
    private static double Thickness(ushort[] control, int width, int height, ushort nation,
        double centreX, double centreY, double normalX, double normalY, double cap)
    {
        var reach = 0.0;
        for (var side = -1; side <= 1; side += 2)
        {
            var walked = 0.0;
            while (walked < cap)
            {
                walked += 1.0;
                var px = centreX + normalX * walked * side;
                var py = centreY + normalY * walked * side;
                var cellY = (int)Math.Round(py);
                if (cellY < 0 || cellY >= height) break;
                var cellX = ((int)Math.Round(px) % width + width) % width;
                if (control[cellY * width + cellX] != nation) break;
            }
            reach += walked;
        }
        return Math.Max(2.0, reach);
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
