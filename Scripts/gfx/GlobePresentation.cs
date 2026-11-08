using static DealOrForce.TerritoryLabelLayout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>The colour plate is shown at a third of the id rasters. Borders stay the darkest
    /// pixel of each block, so the main thread never Lanczos-resizes sixteen million pixels.</summary>
    private const int PoliticalScale = 3;
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
        public Texture2D FullPlateTexture;
        public ulong FlagVisualId;
        public Sprite3D Glow;
        public Sprite3D ConditionBars;
        public Label3D Count;
        public readonly List<Simulation.Army> Stack = new();
        // Display aggregation never changes simulation stacks, province positions or saves.
        public readonly List<Simulation.Army> DisplayStack = new();
        public int DisplayProvinces = 1;
        public int DisplayFingerprint;
        public int ActualFingerprint;
        public Vector3 Normal;
        public int NationId = -1;
        public string TemplateId = "";
        public string CountText = "";
        public int ShownDivisions = -1;
        public int ShownMenTenths = -1;
        public bool ShownCompact;
        public int ConditionKey = -1;
        public bool Seen;
        public bool Compact;
        public bool FlatLayout;
        public bool LayoutInitialized;
        /// <summary>Screen-pixel fan-out when more than one nation's army stands in one province.</summary>
        public Vector2 LayoutOffset;
        /// <summary>
        /// Where the counter belongs, and where it is drawn.
        ///
        /// These were one value, written straight into the node every time the army moved,
        /// which is why a division crossing a border teleported: a hundred kilometres in one
        /// frame. The target is geography and is written by the reconcile pass; the display
        /// position eases toward it every frame, so a counter glides along the ground it is
        /// actually on instead of jumping to the next province.
        /// </summary>
        public Vector3 TargetPosition;
        public Vector3 DisplayPosition;
        /// <summary>False until the display position has caught up, so a new counter does not fly in from the origin.</summary>
        public bool MotionSettled;
    }

    private readonly Dictionary<(int, int, int, int), ArmyStackMarker> _armyStacks = new();
    private readonly List<(int, int, int, int)> _vanishedStacks = new();
    private readonly Dictionary<string, int> _templateVotes = new();
    private readonly HashSet<int> _selectedArmyIds = new();
    private readonly Dictionary<(int Nation, string Template), Texture2D> _armyCounterTextures = new();
    private readonly List<(Node3D Node, Vector3 Normal, int Cells)> _countryLabels = new();
    private readonly List<(Label3D Node, float Latitude, float Longitude, Basis SurfaceBasis, float FlatRotation)> _countryGlyphs = new();
    /// <summary>The size the country letters are drawn at on the flat map, against their full size; negative until applied.</summary>
    private float _countryGlyphScale = -1f;
    private readonly Dictionary<int, Node3D> _countryLabelGroups = new();
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
    private byte[] _ownerIdPixels;
    private Image _ownerIdImage;
    private ImageTexture _ownerIdTexture;
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
    private double _visualClock;
    private Simulation.ProvinceTopology _provinceTopology;

    /// <summary>The provinces of the map this globe has built, or 0 before it has one.</summary>
    public int ProvinceCount => _provinceTopology?.ProvinceCount ?? 0;
    private Simulation.Campaign _campaign;
    private bool _mapApplied;
    public bool IsMapPresentationReady => _mapApplied;
    private bool _loadingInputBlocked;
    public bool LoadingInputBlocked
    {
        get => _loadingInputBlocked;
        set
        {
            _loadingInputBlocked = value;
            if (value) { DismissMapTooltips(); _flatGlide = Vector2.Zero; _orbitGlide = Vector2.Zero; _dragging = false; }
        }
    }
    private bool _mediaLibraryInputBlocked, _mapLayerDrawerInputBlocked;
    public bool MediaLibraryInputBlocked
    {
        get => _mediaLibraryInputBlocked;
        set { _mediaLibraryInputBlocked = value; if (value) DismissMapTooltips(); }
    }
    public bool MapLayerDrawerInputBlocked
    {
        get => _mapLayerDrawerInputBlocked;
        set { _mapLayerDrawerInputBlocked = value; if (value) DismissMapTooltips(); }
    }

    private void DismissMapTooltips()
    {
        // Most tooltips are owned by the persistent keeper at the root, not by
        // this Control. Its grace for rebuilt rows must not cross modal boundaries.
        TooltipKeeper.DismissFor(this);
        // Godot attaches its native tooltip PopupPanel directly to the owner Control.
        // Stop already-visible popups too: blocking new input alone leaves an old
        // tooltip above a newly opened modal until the physical pointer moves.
        // Pending tooltip requests see the blocked _GetTooltip and return empty.
        foreach (var child in GetChildren())
            if (child is PopupPanel popup) popup.Hide();
    }
    public string MapLoadingError { get; private set; } = "";
    public string MapLoadingStage => _pendingMapResult is null
        ? "Reading province and terrain maps"
        : _mapApplyStage < 5 ? "Uploading map layers"
        : _mapApplyStage == 5 ? "Preparing terrain" : "Placing armies and country names";
    // Stage completion, not a time estimate. Never pretend to measure the worker's bytes.
    public double MapLoadingProgress => _mapApplied ? 100 : _pendingMapResult is null ? 45 : 55 + _mapApplyStage * 6;
    private bool _dragging;
    private bool _boxSelecting;
    /// <summary>The box was begun with Ctrl held: it picks regions, not forces (ProvincesBoxSelected).</summary>
    private bool _boxRegions;
    private bool _paintBoxing;
    private bool _paintBoxClaims;
    private Vector2 _boxStart;
    private Vector2 _boxCurrent;
    private float _dragDistance;
    private Vector2 _previousPointer;
    private float _yaw = -0.35f;
    // How far north or south the camera may look: 86 degrees, so Greenland, Svalbard and
    // Antarctica can be brought to the middle of the screen. It stopped at 69 degrees,
    // where the far north sat squashed against the top edge; short of 90 so the orbit
    // never flips over the pole.
    internal const float MaxPitch = 1.50f;
    private float _pitch = 0.30f;
    private float _targetYaw = -0.35f;
    private float _targetPitch = 0.30f;
    private float _distance = 12.5f;
    private float _targetDistance = 12.5f;
    private bool? _cachedFlatMapBorder;

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

    /// <summary>
    /// Every drag is a rectangle, with no Shift to hold - the conference's frame button. The
    /// rectangle was only ever behind a modifier the hint mentioned in passing, so a player who
    /// had asked for exactly this could not find it; a button on the table says it exists.
    /// </summary>
    public bool PaintBoxMode { get; set; }

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
        _defensivePreview.Clear();
        _objectivePreview.Clear(); _departurePreview.Clear();
        _planPreviewForeignId = -1;
        _planPreview.Clear();
        if (provinces is not null) foreach (var province in provinces) _planPreview.Add(province);
        _planPreviewKind = kind;
        BuildFrontPreview();
    }

    public void SetBorderPlanPreview(IEnumerable<int> provinces, int foreignId)
    {
        _defensivePreview.Clear();
        _planPreview.Clear();
        _planPreview.UnionWith(provinces);
        _planPreviewKind = 0;
        _planPreviewForeignId = foreignId;
        BuildFrontPreview();
    }

    public void ClearPlanPreview()
    {
        _defensivePreview.Clear();
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
        // A country picked in the last campaign is an id in somebody else's world.
        _mapModeSubjectId = -1;
        var repairs = (campaign.ScenarioId != "2024" || _mapTask is not null) ? Array.Empty<Simulation.TerritorialTopology.RepairSummary>()
            : Simulation.TerritorialTopology.Apply(
                campaign,
                SourceEarthData.Control,
                SourceEarthData.States,
                SourceEarthData.Width,
                SourceEarthData.Height);
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
        if (campaign.Player is { } player && Simulation.CapitalCore.HasSeat(campaign, player))
            FocusOn(Simulation.CapitalCore.EffectiveLatitude(campaign, player), Simulation.CapitalCore.EffectiveLongitude(campaign, player), 15.8f);

        if (_mapTask is null)
        {
            var colors = new Dictionary<int, uint>(_colorByMapIndex);
            _mapTask = Task.Run(() => BuildPoliticalMap(colors));
            // The terrain's pictures beside it, on other cores: they wait on nothing the map makes.
            StartTerrainImages();
            return;
        }
        if (!_mapApplied && _pendingMapResult is null && _mapTask is { IsCompletedSuccessfully: true })
        {
            _pendingMapResult = _mapTask.Result;
            _mapApplyStage = 0;
        }
        if (!_mapApplied && _pendingMapResult is not null)
        {
            while (!_mapApplied && !_mapFailureReported && _pendingMapResult is not null)
                ApplyPoliticalMapStage();
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
        // Not stretched, and the globe's viewport is not this container's child. A stretched
        // container sizes its viewport to its own size in interface units - 1920 by 1080 with
        // the interface laid out for 1080p - so on a 1440p screen the globe was drawn at 1080p
        // and blown up by a third: soft, on exactly the monitors that should look best. The
        // viewport now renders at the window's pixels, its 2D size is overridden to the
        // interface units every camera projection and overlay is written in, and a picture of
        // it fills the container behind the container's own overlays.
        Stretch = false;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _viewport = new SubViewport
        {
            TransparentBg = true,
            HandleInputLocally = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Size2DOverrideStretch = true,
            Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear,
            // The planet's silhouette, the counter quads and the order ribbons are the only true
            // geometric edges on this screen, and a staircase along the limb is the first thing
            // a strategy player notices about a map's quality. Four samples is the cheapest
            // setting that removes it; the map itself is one shader on one sphere, so the cost
            // is a rasterisation pass and not a scene.
            // The player's edge smoothing, not a constant: four samples is the smoothest and
            // costs fill rate, and a machine that can barely draw the map should not pay for it.
            Msaa3D = MapAntialiasing(),
            UseTaa = false
        };
        var host = new Node { Name = "GlobeViewportHost" };
        AddChild(host);
        host.AddChild(_viewport);
        _picture = new TextureRect
        {
            Name = "GlobePicture",
            Texture = _viewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            ShowBehindParent = true
        };
        _picture.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_picture);
        ResizeViewport();
        Resized += ResizeViewport;
        GetViewport().SizeChanged += ResizeViewport;
        if (float.TryParse(Simulation.SettingsCatalog.Chosen("render_scale"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var renderShare))
            RenderScale = renderShare;

        var world = new Node3D { Name = "GlobeWorld" };
        _viewport.AddChild(world);
        _skyWorld = world;

        var environment = new WorldEnvironment { Environment = new Godot.Environment() };
        environment.Environment.BackgroundMode = Godot.Environment.BGMode.Color;
        environment.Environment.BackgroundColor = Ui.Colour("globe.space");
        environment.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.Environment.AmbientLightColor = Ui.Colour("globe.ambient");
        environment.Environment.AmbientLightEnergy = 0.72f;
        world.AddChild(environment);
        _skyEnvironment = environment;

        var sun = new DirectionalLight3D
        {
            LightEnergy = 1.15f,
            RotationDegrees = new Vector3(-32f, -38f, 0f),
            ShadowEnabled = true
        };
        world.AddChild(sun);
        _daySun = sun;
        ConfigureGlobeGrading();
        CreateAtlasLighting();

        _orbit = new Node3D { Name = "Orbit" };
        world.AddChild(_orbit);

        _camera = new Camera3D
        {
            Current = true,
            Fov = RigFieldOfView,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            HOffset = 0f,
            Near = 0.05f,
            Far = 250f
        };
        _orbit.AddChild(_camera);

        _spaceNode = CreateSpace(world);
        CreateGlobe(world);
        CreateAtmosphereRim(world);
        if (CloudsEnabled) CreateCloudLayer(world);
        CreateFlatMap(world);
        CreateWeather(world);
        CreateTradeTraffic(world);
        CreateEventPulses(world);
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
        CreateSupportPresentation(world);
        BuildMovementLayer(world);
        _fleetTexture = CreateFleetMarkerTexture();
        CreateMilitaryOverlays(world);
        _countryLabelRoot = new Node3D { Name = "CountryLabels" };
        world.AddChild(_countryLabelRoot);
        CreateCityLayer(world);
        CreateRoutePreview(world);
        CreateFrontPreview(world);
        ApplyCamera();
    }

    public override void _Process(double delta)
    {
        UpdateSky(delta);
        UpdateCrossoverProjection();
        PollPlateRepaint();
        if (!LoadingInputBlocked && !MediaLibraryInputBlocked && !MapLayerDrawerInputBlocked)
        {
            if (_freeCamera) UpdateFreeCamera(delta);
            else UpdateOrbitCamera(delta);
        }
        // Picking, density and city layout must see the same projected positions that
        // will be rendered this frame, including wrapping at the date line.
        if (_flatMap) UpdateFlatWorldPositions();

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
        _globeMaterial?.SetShaderParameter("detail_mix", detailMix);
        // Tactical boundaries should emerge only at operational zoom. Keeping them
        // faint at country-selection scale avoids turning the map into a crosshatch.
        _globeMaterial?.SetShaderParameter("province_detail", provinceDetail);
        if (_cachedFlatMapBorder != _flatMap)
        {
            _cachedFlatMapBorder = _flatMap;
            _globeMaterial?.SetShaderParameter("front_pixels", Math.Clamp(Ui.Metric("map.front_width_pixels", 1.6f), .75f, 3f));
            _globeMaterial?.SetShaderParameter("province_border_pixels", Math.Clamp(Simulation.InterfaceSkin.Metric(_flatMap ? "map.flat_province_border_width" : "map.province_border_width", _flatMap ? 1.1f : 2.0f), 0.5f, 4f));
            _globeMaterial?.SetShaderParameter("province_border_opacity", Math.Clamp(Simulation.InterfaceSkin.Metric(_flatMap ? "map.flat_province_border_opacity" : "map.province_border_opacity", _flatMap ? 0.52f : 0.72f), 0f, 1f));
            _globeMaterial?.SetShaderParameter("province_border_halo", Math.Clamp(Simulation.InterfaceSkin.Metric(_flatMap ? "map.flat_province_border_halo" : "map.province_border_halo", _flatMap ? 0.025f : 0.15f), 0f, 0.4f));
        }
        var fogViewer = FogOfWarActive && _campaign?.Player is { IsHuman: true } ? _campaign.Player.Id : -1;
        if (_fogViewer != fogViewer) { _fogViewer = fogViewer; _lastMapModeDay = -100; }
        _globeMaterial?.SetShaderParameter("fog_viewer", FogOfWarActive && _campaign?.Player is { IsHuman: true } ? _campaign.Player.MapIndex : -1);
        UpdateFogTint();
        UpdateFogVisibility();
        UpdateWeather(delta);
        UpdateIce(delta);
        // A frozen world's ships stand where the ice caught them, and its news does not pulse.
        var worldDelta = Simulation.WorldCurses.Frozen(_campaign) ? 0.0 : delta;
        UpdateTradeTraffic(worldDelta);
        UpdateEventPulses(worldDelta);
        UpdateNuclearBlasts();
        KeepAllowedProjection();
        // A capital that moved - by choice, or because its region was ceded - moves its marker.
        if (_mapApplied && _campaign is not null && (_capitalRevision != _campaign.CapitalRevision ||
            _capitalDestroyedCount != _campaign.DestroyedProvinces.Count))
            BuildCapitalMarkers(_campaign);
        // The stars go as far round as the capitals' names do (GlobeCities): past that they stood
        // in a nameless row along the limb, where the ground is seen edge-on.
        // LOD is not motion: labels and counters are re-evaluated ten times a second while the
        // camera keeps moving every frame. At sixty times a second this was two milliseconds
        // of every frame spent hiding markers that were already hidden.
        _visualClock += delta;
        var visualStep = _visualClock;
        var visualDue = _visualClock >= 0.1;
        if (visualDue) _visualClock = 0;
        // Counters glide every frame, on the visual cadence nothing else about them runs at.
        // This is the one thing in the frame loop that has to be continuous for the map to
        // read as a place rather than a flipbook.
        if (MilitaryMarkersEnabled && _mapApplied)
            using (Simulation.SimProfiler.Measure("f:army motion")) UpdateArmyMarkerMotion((float)delta);
        if (visualDue)
            UpdateMarkerVisibility(_capitalRoot, _capitalMarkers, ViewDistance <= 13.8f && Simulation.WorldLayers.On("capitals"), CityHorizon());
        var armyLod = _flatMap || ViewDistance <= 10.8f;
        _militaryVisualAccumulator += delta;
        if (MilitaryMarkersEnabled && _mapApplied && _campaign is not null && _militaryVisualAccumulator >= 0.12)
        {
            _militaryVisualAccumulator = 0;
            using (Simulation.SimProfiler.Measure("f:army sync")) SyncArmyMarkers();
            using (Simulation.SimProfiler.Measure("f:army look")) UpdateMilitaryPresentation();
        }
        if (visualDue)
            using (Simulation.SimProfiler.Measure("f:army vis"))
            {
                UpdateMarkerVisibility(_armyRoot, _armyMarkers, armyLod && MilitaryMarkersEnabled,
                    _flatMap ? 0f : GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length()) + .002f);
                UpdateCounterScale();
            }
        if (MilitaryMarkersEnabled && visualDue)
            using (Simulation.SimProfiler.Measure("f:fleets")) UpdateFleetMarkers();
        if (visualDue)
            UpdateMarkerVisibility(_fleetRoot, _fleetMarkers, MilitaryMarkersEnabled && (_flatMap || ViewDistance <= 13.5f),
                _flatMap ? 0f : GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length()) + .002f);
        if (visualDue) UpdateSupportPresentation(visualStep);
        if (visualDue && _flatMap && MilitaryMarkersEnabled)
            using (Simulation.SimProfiler.Measure("f:counter layout")) UpdateFlatCounterDensity();
        else if (visualDue && MilitaryMarkersEnabled)
            using (Simulation.SimProfiler.Measure("f:surface layout")) UpdateSurfaceCounterDensity();
        if (visualDue && MilitaryMarkersEnabled) LayoutSupportCounters();
        if (IsInstanceValid(_battleRoot)) _battleRoot.Visible = MilitaryMarkersEnabled;
        using (Simulation.SimProfiler.Measure("f:routes")) UpdateRoutePreview();
        using (Simulation.SimProfiler.Measure("f:supply lines")) UpdateSupplyLines();
        if (_mapApplied && _campaign is not null && _controlRevision != _campaign.MapRevision)
            using (Simulation.SimProfiler.Measure("f:map paint")) RefreshProvinceControllers();
        if (_mapApplied && _campaign is not null &&
            (_labelTask is not null || _labelRevision != _campaign.MapRevision))
            using (Simulation.SimProfiler.Measure("f:label place")) RefreshCountryLabelPlacement();
        if (IsInstanceValid(_frontPreview)) _frontPreview.Visible = MilitaryMarkersEnabled && _frontMesh.GetSurfaceCount() > 0;
        if (visualDue)
            using (Simulation.SimProfiler.Measure("f:labels")) UpdateCountryLabelVisibility();
        if (_battleMarkers.Count > 0 && (visualDue || _dragging || _boxSelecting || FlatViewInMotion)) QueueRedraw();
        if (visualDue) { UpdateCityLabels(visualStep); UpdateSettlementModels(visualStep); }
        KeepRegionIcons(delta);
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
                // The worker already kept one pixel per 3x3 block, darkest land winning so a
                // country border is not averaged into the interior. Uploading the full plate
                // and Lanczos-shrinking it on this thread was the stall at the start of a campaign.
                using var plate = Image.CreateFromData(MapWidth / PoliticalScale, MapHeight / PoliticalScale,
                    false, Image.Format.Rgba8, result.PoliticalPixels);
                plate.GenerateMipmaps();
                _plateTexture = ImageTexture.CreateFromImage(plate);
                _globeMaterial.SetShaderParameter("political_map", _plateTexture);
                _platePixels = result.PoliticalPixels;
                _plateProvinces = result.PlateProvinces;
                _plateShade = result.PlateShade;
                _platePainted = null;
                result.PoliticalPixels = null;
                break;
            }
            case 1:
            {
                using var img = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.Rg8, result.RegionIds);
                UploadMapImage("region_ids", img);
                result.RegionIds = null;
                break;
            }
            case 2:
            {
                using var img = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.Rg8, result.ProvinceIds);
                UploadMapImage("province_ids", img);
                result.ProvinceIds = null;
                break;
            }
            case 3:
            {
                using var img = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.Rg8, result.NationIds);
                UploadMapImage("nation_ids", img);
                result.NationIds = null;
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
                using var img = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.Rgb8, result.HydrologyPixels);
                _globeMaterial.SetShaderParameter("hydrology_map", ImageTexture.CreateFromImage(img));
                result.HydrologyPixels = null;
                if (result.RiverCentrePixels is { } centre)
                {
                    using var lines = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.Rgba8, centre);
                    _globeMaterial.SetShaderParameter("river_centre_map", ImageTexture.CreateFromImage(lines));
                    result.RiverCentrePixels = null;
                }
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
                BindArmyTopology();
                ReconcileArmyMarkers(_campaign);
                BuildCountryLabels(result.LabelAnchors);
                BuildFrontPreview();
                RefreshProvinceControllers();
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
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                // And from here on, play: full blocking collections are what a frame feels as a
                // hitch, so the runtime is asked to do its oldest-generation work in the
                // background for the rest of the session. Memory may run somewhat higher; a
                // stutter in the middle of a battle is worse than a few more megabytes.
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
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
        _flatGlide = Vector2.Zero; _orbitGlide = Vector2.Zero;
        _flatZoomAnchorActive = false;
        if (_flatMap)
        {
            _targetFlatX = longitude / 18f;
            _targetFlatY = latitude / 18f;
            _targetFlatZoom = FlatZoomForDistance(distance);
            ClampFlatCamera();
            return;
        }
        _targetPitch = Mathf.Clamp(-Mathf.DegToRad(latitude), -MaxPitch, MaxPitch);
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
            if (pan.Pressed) BeginCameraDrag(pan.Position, pan.ButtonIndex);
            else _dragging = false;
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

        // Shift, or the frame button, turns the brush into a rectangle. Left takes what is
        // inside it, right gives it back - the same two hands as the brush, so nothing new has
        // to be learned.
        var framing = PaintBoxMode;
        if (inputEvent is InputEventMouseButton boxButton &&
            boxButton.ButtonIndex is MouseButton.Left or MouseButton.Right &&
            (boxButton.ShiftPressed || framing || _paintBoxing))
        {
            if (boxButton.Pressed && (boxButton.ShiftPressed || framing))
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
        if (!TryLocate(pixel, out var latitude, out var longitude, out var cell, out var provinceId)) return false;
        // A country owning a province and an army occupying it are not the same thing.
        // Picking the controller here also suppressed the tooltip's occupation line, since
        // it then compared that controller to itself and called the occupier the owner.
        var nationMapIndex = provinceId > 0 && _campaign?.ProvinceMap is { } map
            ? map.ProvinceToNation[provinceId]
            : SourceEarthData.Control[cell];
        if (nationMapIndex == 0 || !_nationByMapIndex.TryGetValue(nationMapIndex, out var nation))
            return false;
        return FinishPick(nation, latitude, longitude, cell, provinceId, updateSelection, out pick);
    }

    internal bool VerificationPickAt(Vector2 pixel, out GlobePick pick) => TryPick(pixel, out pick, false);

    /// <summary>
    /// Where on the Earth a pixel of the screen is: latitude and longitude, the raster cell,
    /// and the province under it (0 or less when the map has no provinces yet). False off the
    /// edge of the globe or the flat map.
    /// </summary>
    private bool TryLocate(Vector2 pixel, out float latitude, out float longitude, out int cell, out int provinceId)
    {
        latitude = longitude = 0f;
        cell = 0;
        provinceId = -1;
        if (!IsInstanceValid(_camera)) return false;

        var origin = _camera.ProjectRayOrigin(pixel);
        var direction = _camera.ProjectRayNormal(pixel).Normalized();
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
        cell = y * SourceEarthData.Width + x;
        provinceId = _provinceTopology is null ? -1 : _provinceTopology.CellToProvince[cell];
        return true;
    }

    private bool FinishPick(Simulation.Nation nation, float latitude, float longitude, int cell, int provinceId,
        bool updateSelection, out GlobePick pick)
    {
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
            var rawRegion = _provinceTopology is not null && provinceId > 0 && provinceId < _provinceTopology.ProvinceToState.Length
                ? _provinceTopology.ProvinceToState[provinceId]
                : SourceEarthData.States[cell];
            _globeMaterial.SetShaderParameter("selected_region_id", (float)rawRegion);
            _globeMaterial.SetShaderParameter("selected_province_id", (float)provinceId);
            _globeMaterial.SetShaderParameter("selection_active", 1f);
            SelectedRegionMembers(provinceId > 0 && _campaign?.ProvinceMap is not null ? regionMapIndex : -1);
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

    /// <summary>
    /// Flat counters use screen pixels, never the globe's distance-based scale curve.
    /// This remains one so picking and presentation use the same footprint.
    /// </summary>
    private float _counterFlatFar = 1f;

    public bool MilitaryMarkersEnabled
    {
        // A game without counters (common/world_layers) has none to switch on.
        get => _militaryMarkersEnabled && Simulation.WorldLayers.On("military_counters");
        set
        {
            if (_militaryMarkersEnabled == value) return;
            _militaryMarkersEnabled = value;
            // Turning the layer back on must rebuild the counters even if no army moved
            // while it was hidden; the structure fingerprint alone would otherwise say
            // "nothing changed" and leave the map empty.
            _armyStructureFingerprint = int.MinValue;
        }
    }
    private bool _militaryMarkersEnabled = true;
    private bool _showcasePan;
    public bool ShowcasePan => _showcasePan;

    public void SetShowcasePan(bool enabled) => _showcasePan = enabled;

    private bool _mapSmoothing = Simulation.PlayerSettings.Bool("map_smoothing", true);

    /// <summary>
    /// What the quality profile has allowed this machine to draw. It is deliberately a second
    /// opinion beside the player's own switches: `QualityCityLabels` is false only when the
    /// governor has taken city names away, and the player can still turn weather off on a
    /// machine that could afford it.
    /// </summary>
    private QualityProfile _quality = new();

    public bool QualityCityLabels => _quality.CityLabels;
    public bool QualityTradeTraffic => _quality.TradeTraffic;
    public bool QualityWeather => _quality.Weather;

    /// <summary>The tier the map is currently on, for the settings screen and the governor.</summary>
    public QualityProfile Quality => _quality;

    /// <summary>
    /// Put the map on a quality tier. Called once at start-up and again whenever the governor
    /// finds the machine cannot hold the frame rate - the reason a player whose card fell off
    /// the bus sees a cheaper map instead of eight frames a second.
    /// </summary>
    public void ApplyQuality(QualityProfile profile)
    {
        _quality = profile ?? new QualityProfile();
        // The tier says what this machine can afford; the video setting says how many pixels
        // the player wants. They multiply, so "75 %" on a weak laptop still means 75 % of what
        // the laptop can afford rather than undoing the downgrade.
        var playerShare = 1f;
        if (float.TryParse(Simulation.SettingsCatalog.Chosen("render_scale"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var chosen))
            playerShare = Math.Clamp(chosen, 0.25f, 2f);
        RenderScale = _quality.RenderScale * playerShare;
        SetMapSmoothing(_quality.MapSmoothing, remember: false);
        if (IsInstanceValid(_daySun)) _daySun.ShadowEnabled = _quality.SunShadows;
        // The atlas is a device-wide budget, not a per-light switch: halving it is what makes
        // "no sun shadows" cost less rather than merely look different.
        RenderingServer.DirectionalShadowAtlasSetSize(
            Math.Clamp((int)MathF.Round(4096f * _quality.RenderScale), 512, 4096), true);
    }


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
        set => SetMapSmoothing(value, remember: true);
    }

    /// <summary>
    /// The same picture switch, with the choice of whether the player's saved preference moves
    /// with it. The governor lowering the picture for a weak card must not rewrite the setting
    /// the player will find on a better one.
    /// </summary>
    private void SetMapSmoothing(bool value, bool remember)
    {
        if (remember) Simulation.PlayerSettings.Set("map_smoothing", value);
        _mapSmoothing = value;
        ApplyMapSmoothing();
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
        marker.Plate.Texture = _flatMap && _flatStrategicCounters
            ? CompactCounterTexture(marker.FullPlateTexture, new Rect2I(8, 5, 51, 70))
            : marker.FullPlateTexture;
        // PixelSize/OutlineSize rebuild Label3D meshes. Never animate those properties
        // on every orthographic zoom frame for the entire world's counters.
        if (!marker.LayoutInitialized || marker.FlatLayout != _flatMap)
        {
            marker.LayoutInitialized = true;
            marker.FlatLayout = _flatMap;
            marker.Plate.FixedSize = false;
            marker.Count.FixedSize = false;
            marker.Count.HorizontalAlignment = HorizontalAlignment.Center;
            marker.Count.VerticalAlignment = VerticalAlignment.Center;
            // Godot's Label3D defaults Width to 500 - a half-kilometre of text field the
            // label then centres the text inside of, which parks the number to the right
            // of the counter it belongs to. Zero is "as wide as the text", which is what
            // centres the number on the anchor.
            marker.Count.Width = 0f;
            if (marker.Glow is not null)
            {
                marker.Glow.FixedSize = marker.Plate.FixedSize;
                marker.Glow.PixelSize = marker.Plate.PixelSize;
            }
            marker.Compact = false;
            marker.Plate.Scale = Vector3.One;
            marker.Plate.Visible = true;
            marker.Count.Visible = true;
        }
        if (_flatMap)
        {
            ApplyArmyCounterScale(marker);
            // Density owns visibility and compactness. A zoom must not turn text back on
            // over a hidden plate before the density pass decides where it fits.
        }
        else
        {
            if (marker.Node.Position == Vector3.Zero && marker.Stack.Count > 0)
                marker.Node.Position = ArmyMarkerPosition(marker);
            SizeSurfaceArmyCounter(marker);
        }
        // Shared texture-space layout, with no separate 3D displacement or shader
        // billboard/fixed-size compensation. All four layers inherit the surface frame.
        var platePixel = _flatMap
            ? Math.Clamp(Simulation.InterfaceSkin.Metric("map.flat_counter_width", 64f), 40f, 96f) / marker.Plate.Texture.GetWidth()
            : 1f / 176f;
        marker.Plate.PixelSize = platePixel;
        if (marker.Glow is not null)
        {
            marker.Glow.PixelSize = platePixel;
            marker.Glow.Scale = _flatMap && _flatStrategicCounters
                ? new Vector3(marker.Plate.Texture.GetWidth() / 176f, marker.Plate.Texture.GetHeight() / 104f, 1f)
                : Vector3.One;
        }
        marker.Count.PixelSize = platePixel * 1.45f;
        marker.Count.FontSize = _flatMap && _flatStrategicCounters ? 30 : 21;
        marker.Count.OutlineSize = _flatMap ? 2 : Mathf.Max(1, Mathf.RoundToInt(4 * _counterScale));
        marker.Count.Position = Vector3.Zero;
        // The centred two-line block uses the same texture-space anchor in both
        // projections. A separate flat-map offset put the figures on top of the icon.
        marker.Count.Offset = new Vector2(1f, 4f);
        if (marker.ConditionBars is not null)
        {
            marker.ConditionBars.FixedSize = marker.Plate.FixedSize;
            marker.ConditionBars.PixelSize = platePixel;
            marker.ConditionBars.Offset = Vector2.Zero;
            marker.ConditionBars.Position = Vector3.Zero;
        }
    }

    /// <summary>
    /// Compensate the orthographic camera each frame using transforms only. Quantizing
    /// this compensation made cards breathe/jump during smooth zoom. Expensive Label3D
    /// layout changes remain limited to projection switches and the globe's scale steps.
    /// </summary>
    private void UpdateCounterScale(bool force = false)
    {
        UpdateCapitalMarkerScale();
        if (_flatMap)
        {
            _counterWorldPixel = _camera.Size / Math.Max(1f, _camera.GetViewport().GetVisibleRect().Size.Y);
            var referenceZoom = Math.Clamp(Ui.Metric("map.flat_counter_reference_zoom", 1.7f), .6f, 3f);
            var minimumRatio = Math.Clamp(Ui.Metric("map.flat_counter_min_ratio", .25f), .15f, .5f);
            // Follow the ground on retreat; only retain a small, clickable tactical symbol
            // at the world view. Constant-width cards dwarf countries when zoomed out.
            _counterFlatFar = Math.Clamp(referenceZoom / _flatZoom, minimumRatio, 1f);
            var strategic = _flatStrategicCounters ? _flatZoom > 2.8f : _flatZoom > 3.3f;
            var detailChanged = strategic != _flatStrategicCounters;
            _flatStrategicCounters = strategic;
            foreach (var marker in _armyStacks.Values)
            {
                if (!IsInstanceValid(marker.Node)) continue;
                if (force || detailChanged || !marker.LayoutInitialized || !marker.FlatLayout) SizeArmyCounter(marker);
                marker.Node.Scale = Vector3.One * _counterWorldPixel * _counterFlatFar;
                if (force || detailChanged) marker.Count.FontSize = strategic ? 30 : 21;
            }
            foreach (var parts in _fleetNodes.Values)
            {
                if (force || detailChanged || parts.Plate.FixedSize) SizeFleetCounter(parts);
                parts.Node.Scale = Vector3.One * _counterWorldPixel * _counterFlatFar;
                parts.Count.Visible = !strategic;
                if (force || detailChanged) parts.Count.FontSize = strategic ? 30 : 24;
            }
            foreach (var marker in _supportMapMarkers.Values)
            {
                if (force || detailChanged || marker.Plate.FixedSize) PlaceSupportMarker(marker);
                marker.Node.Scale = Vector3.One * _counterWorldPixel * _counterFlatFar;
                marker.Readiness.Visible = !strategic && _selectedSupportIds.Contains(marker.Formation.Id);
                if (force || detailChanged)
                {
                    marker.Readiness.FontSize = strategic ? 30 : 20;
                    PaintSupportMarker(marker, marker.Formation);
                }
            }
            if (force || detailChanged) InvalidateFlatCounterDensity();
            return;
        }
        var wanted = Mathf.Lerp(CounterCloseScale, 1f,
                Mathf.InverseLerp(MinimumDistance, CounterGrowthDistance, _distance));
        wanted = Mathf.Clamp(wanted, 1f, CounterCloseScale);
        // The entire surface card shares one real transform, not four independent
        // shader distance compensations. Update transforms continuously when the camera moves.
        var camNow = _camera.GlobalTransform;
        if (force || camNow != _lastScaleCamera || _armyStacks.Count != _lastScaleArmyCount || _fleetNodes.Count != _lastScaleFleetCount)
        {
            _lastScaleCamera = camNow;
            _lastScaleArmyCount = _armyStacks.Count;
            _lastScaleFleetCount = _fleetNodes.Count;
            foreach (var marker in _armyStacks.Values)
                if (IsInstanceValid(marker.Node) && marker.Node.Visible) SizeSurfaceArmyCounter(marker);
            foreach (var parts in _fleetNodes.Values)
                if (IsInstanceValid(parts.Node) && parts.Node.Visible) parts.Node.Scale = Vector3.One * SurfaceCounterWidth(parts.Node, "fleet");
        }
        if (!force && Mathf.Abs(wanted - _counterScale) < 0.03f) return;
        _counterScale = wanted;
        _counterWorldPixel = 0f;
        _counterFlatFar = 1f;
        _flatStrategicCounters = false;
        InvalidateFlatCounterDensity();
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node)) continue;
            SizeArmyCounter(marker);
            marker.Count.FontSize = 21;
            marker.Count.Text = marker.CountText;
        }
        foreach (var parts in _fleetNodes.Values) { SizeFleetCounter(parts); parts.Count.FontSize = 24; }
        foreach (var marker in _supportMapMarkers.Values)
            if (IsInstanceValid(marker.Node)) PlaceSupportMarker(marker);
    }

    private bool TryPickArmyStack(Vector2 pixel, out IReadOnlyList<Simulation.Army> armies, bool selectableOnly = true)
    {
        armies = null;
        if (!MilitaryMarkersEnabled || (!_flatMap && _distance > 13.5f) || !IsInstanceValid(_camera)) return false;
        // A bigger counter is a bigger target: the radius that finds it follows the size
        // it is actually drawn at, or clicking a close-up counter near its edge misses.
        var bestDistance = float.MaxValue;
        foreach (var marker in _armyStacks.Values)
        {
            var node = marker.Node;
            var candidates = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (!node.IsVisibleInTree()) continue;
            if (!TryHitSurfaceArmyCard(marker, pixel, out var distance)) continue;
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
        InvalidateFlatCounterDensity();
        _routeFingerprint = int.MinValue;
        foreach (var (node, armies) in _armyByMarker)
        {
            if (!IsInstanceValid(node)) continue;
            var plate = node.GetChildOrNull<Sprite3D>(0);
            if (plate is null) continue;
            var chosen = armies.Any(army => selected.Contains(army.Id));
            plate.Modulate = chosen ? new Color(1f, 0.96f, 0.86f, 1f) : Colors.White;
            if (plate.GetChildOrNull<Sprite3D>(0) is { } glow) glow.Visible = chosen;
        }
    }

    public override void _Draw()
    {
        DrawRegionIcons();
        DrawFlatCounterLeaders();
        DrawBattleReadouts();
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

    private byte[] _borderTargetPixels;
    private byte[] _borderControllerPixels;
    private Image _borderPlanImage;
    private Image _borderControllerImage;

    private void BuildFrontPreview()
    {
        if (_campaign?.ProvinceMap is null || _globeMaterial is null) return;
        _frontMesh.ClearSurfaces();
        _planHitSegments.Clear();
        _drawingPlanId = -1;
        _frontPreview.Visible = false;
        var pixels = _frontPixels ??= new byte[256 * 256 * 4];
        var borderTargets = _borderTargetPixels ??= new byte[256 * 256 * 4];
        var controllers = _borderControllerPixels ??= new byte[256 * 256 * 2];
        Array.Clear(borderTargets);
        Array.Clear(controllers);
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
            _drawingPlanId = line.Id;
            _drawingPlanObjective = line.Kind == Simulation.FrontLineKind.Offensive;
            Simulation.BorderFronts.Refresh(_campaign, line);
            if (line.Kind == Simulation.FrontLineKind.Fallback)
            {
                DrawDefensiveGeometry(line.DefensiveSequence.Count > 0 ? line.DefensiveSequence : line.Provinces.OrderBy(p => p).ToList(), line.NationId, false);
                continue;
            }
            if (line.Kind == Simulation.FrontLineKind.Offensive && line.ObjectiveSequence.Count > 0)
            {
                foreach (var p in line.SourceProvinces)
                {
                    pixels[p * 4 + 2] = 64;
                    PutBorderTarget(borderTargets, p, line.NationId, line.BorderNationId);
                }
                if (_objectivePreview.Count == 0 || !_selectedArmyIds.Any(id => line.ArmyIds.Contains(id)))
                    for (var phase = 0; phase < Simulation.OffensivePlanning.PhaseCount(line); phase++)
                    {
                        IEnumerable<int> departure = phase == 0 ? line.SourceProvinces : Simulation.OffensivePlanning.PhaseGoals(line, phase - 1);
                        DrawOffensiveGeometry(Simulation.OffensivePlanning.PhaseGoals(line, phase), departure, false,
                            phase < line.ActiveOffensivePhase ? -1 : phase == line.ActiveOffensivePhase ? 1 : 2);
                    }
                continue;
            }
            var code = PlanCode(line.Kind);
            foreach (var province in line.Provinces)
            {
                if (province <= 0 || province >= 256 * 256) continue;
                pixels[province * 4 + 2] = code;
                // The whole front faces several countries; each post marks the border with
                // the enemy actually next to it.
                if (line.Kind == Simulation.FrontLineKind.Defence)
                    PutBorderTarget(borderTargets, province, line.NationId, line.AllEnemies
                        ? Simulation.BorderFronts.EnemyAt(_campaign, line.NationId, province) : line.BorderNationId);
            }
        }
        _drawingPlanId = -1;
        if (_objectivePreview.Count > 0) DrawOffensiveGeometry(_objectivePreview, _departurePreview, true);
        if (_defensivePreview.Count > 0) DrawDefensiveGeometry(_defensivePreview, _campaign.Player.Id, true);
        if (_planPreview.Count > 0 && _objectivePreview.Count == 0 && _defensivePreview.Count == 0)
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

        foreach (var province in _campaign.DestroyedProvinces)
            if (province > 0 && province < 256 * 256) pixels[province * 4 + 3] = 255;
        UpdateBorderTexture(ref _borderPlanTexture, ref _borderPlanImage, borderTargets, Image.Format.Rgba8, "border_plan_targets");
        _frontPreview.Visible = _frontMesh.GetSurfaceCount() > 0;
        UpdateBorderTexture(ref _borderControllerTexture, ref _borderControllerImage, controllers, Image.Format.Rg8, "border_controller_ids");

        if (_frontImage is null) _frontImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _frontImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("front_membership", _frontImage, ref _frontTexture);
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

    private void UpdateBorderTexture(ref ImageTexture texture, ref Image image, byte[] pixels, Image.Format format, string uniform)
    {
        if (image is null) image = Image.CreateFromData(256, 256, false, format, pixels);
        else image.SetData(256, 256, false, format, pixels);
        UpdateMapImage(uniform, image, ref texture);
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
        if (LoadingInputBlocked || MediaLibraryInputBlocked || MapLayerDrawerInputBlocked) { AcceptEvent(); return; }
        if (PlanEraseMode && inputEvent is InputEventMouseButton erase && erase.ButtonIndex == MouseButton.Left)
        {
            if (erase.Pressed && TryPickPlan(erase.Position, out var lineId, out var objectiveOnly))
                PlanEraseRequested?.Invoke(lineId, objectiveOnly);
            AcceptEvent(); return;
        }
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
                    _boxRegions = button.CtrlPressed && !CountrySelectionOnly && ProvincesBoxSelected is not null;
                    _boxStart = button.Position;
                    _boxCurrent = button.Position;
                    QueueRedraw();
                }
                else if (_boxSelecting && _boxRegions)
                {
                    _boxCurrent = button.Position;
                    _boxSelecting = false;
                    _boxRegions = false;
                    var area = RectFromPoints(_boxStart, _boxCurrent);
                    var provinces = area.Size.Length() >= 8f ? ProvincesInScreenRect(area) : new List<int>();
                    if (provinces.Count == 0 && TryPick(button.Position, out var single, updateSelection: false) && single.ProvinceId > 0)
                        provinces.Add(single.ProvinceId);
                    if (provinces.Count > 0) ProvincesBoxSelected?.Invoke(provinces);
                    QueueRedraw();
                    AcceptEvent();
                    return;
                }
                else if (_boxSelecting)
                {
                    _boxCurrent = button.Position;
                    _boxSelecting = false;
                    var rect = RectFromPoints(_boxStart, _boxCurrent);
                    if (rect.Size.Length() < 8f)
                    {
                        // Resolve headquarters through the same map GUI input as provinces.
                        // The container consumes this click before child _Input handlers run.
                        if (_movementLayer is not null && _movementLayer.TryPickAt(button.Position, out var movement))
                        {
                            MovementPicked?.Invoke(movement);
                        }
                        else if (CountrySelectionOnly)
                        {
                            if (TryPick(button.Position, out var country)) CountryPicked?.Invoke(country);
                        }
                        else if (SupportCommandMode)
                        {
                            if (!SupportRebaseArmed && TryPickSupport(button.Position, out var formation)) SupportPicked?.Invoke(formation);
                            else if (TryPick(button.Position, out var destination)) CountryPicked?.Invoke(destination);
                        }
                        else if (TryPickBattle(button.Position, out var battle)) BattlePicked?.Invoke(battle);
                        else if (TryPickArmyStack(button.Position, out var stack))
                        {
                            if (stack.Count == 1) ArmyPicked?.Invoke(stack[0]);
                            else if (stack.Count > 1) ArmiesBoxPicked?.Invoke(stack);
                        }
                        else if (TryPickFleet(button.Position, out var fleet)) FleetPicked?.Invoke(fleet);
                        else if (TryPickSupport(button.Position, out var support)) SupportPicked?.Invoke(support);
                        else if (TryPick(button.Position, out var pick)) CountryPicked?.Invoke(pick);
                        else if (TryPickUnclaimed(button.Position, out var unclaimed, out var unclaimedProvince))
                            UnclaimedPicked?.Invoke(unclaimed, unclaimedProvince);
                    }
                    else
                    {
                        if (!CountrySelectionOnly && SupportCommandMode) SupportBoxPicked?.Invoke(SupportInScreenRect(rect));
                        else
                        {
                            var selected = ArmiesInScreenRect(rect);
                            if (!CountrySelectionOnly && selected.Count > 0) ArmiesBoxPicked?.Invoke(selected);
                            else if (!CountrySelectionOnly && FleetsInScreenRect(rect) is { Count: > 0 } fleets)
                                FleetsBoxPicked?.Invoke(fleets);
                        }
                    }
                    QueueRedraw();
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex is MouseButton.Right or MouseButton.Middle)
            {
                if (button.Pressed)
                {
                    BeginCameraDrag(button.Position, button.ButtonIndex);
                    _boxSelecting = false;
                    QueueRedraw();
                }
                else
                {
                    if (button.ButtonIndex != _cameraDragButton) return;
                    var wasClick = _dragging && _dragDistance < 5f && button.ButtonIndex == MouseButton.Right;
                    _dragging = false;
                    if (!PlanEraseMode && !CountrySelectionOnly && wasClick && TryPick(button.Position, out var commanded, updateSelection: false))
                        ProvinceCommanded?.Invoke(commanded);
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown)
            {
                _flatGlide = Vector2.Zero; _orbitGlide = Vector2.Zero;
                // A step proportional to the altitude rather than a fixed one. A quarter
                // of a unit is a nudge from orbit and a lurch from a hundred metres up,
                // and the whole reason for coming this close is to move one division.
                var sign = button.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
                if (_flatMap)
                    ZoomFlatMapAt(button.Position, sign);
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
            _flatZoomAnchorActive = false;
            // The current zoom, not the one being lerped towards: what a pixel is worth is
            // decided by what is on the screen now, so dragging while the wheel is still
            // settling moves the ground by the distance the hand moved rather than by the
            // distance it will be worth in a moment.
            var unitsPerPixel = MapPanScale.FlatUnitsPerPixel(_flatZoom, ViewportSize().Y);
            _targetFlatX -= delta.X * unitsPerPixel;
            _targetFlatY += delta.Y * unitsPerPixel;
            ClampFlatCamera();
            // How fast the hand is moving, smoothed over the last few motion events, for the glide.
            var now = Time.GetTicksUsec();
            var seconds = Mathf.Clamp((now - _lastFlatDragUsec) / 1_000_000f, 0.004f, 0.1f);
            _lastFlatDragUsec = now;
            var speed = new Vector2(-delta.X, delta.Y) * unitsPerPixel / seconds;
            _flatGlide = _flatGlide.Lerp(speed, 0.45f).LimitLength(_flatZoom * 3.5f);

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
        // One pixel of hand movement should move approximately one pixel of ground at
        // the view centre. Use the displayed altitude, never a pending wheel/fly-to target.
        var radians = MapPanScale.OrbitRadiansPerPixel(_distance, GlobeRadius, _camera.Fov, ViewportSize().Y);
        var longitudeScale = Math.Max(.15f, Mathf.Cos(_pitch));
        var movement = new Vector2(-delta.X * radians / longitudeScale, -delta.Y * radians);
        _targetYaw += movement.X;
        _targetPitch = Mathf.Clamp(_targetPitch + movement.Y, -MaxPitch, MaxPitch);
        _yaw = _targetYaw;
        _pitch = _targetPitch;
        var dragNow = Time.GetTicksUsec();
        var dragSeconds = Mathf.Clamp((dragNow - _lastFlatDragUsec) / 1_000_000f, .004f, .1f);
        _lastFlatDragUsec = dragNow;
        _orbitGlide = _orbitGlide.Lerp(movement / dragSeconds, 1f - Mathf.Exp(-dragSeconds * 24f))
            .LimitLength(radians * ViewportSize().Y * .9f / longitudeScale);
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

    /// <summary>
    /// Take the picked region and province off the map. The tint lives in shader uniforms
    /// rather than in a node, so hiding the panel that described it left the ground lit as
    /// if it were still selected. Closing that panel now calls this.
    /// </summary>
    public void ClearSelection()
    {
        _globeMaterial?.SetShaderParameter("selection_active", 0f);
        _globeMaterial?.SetShaderParameter("selected_region_id", -1f);
        _globeMaterial?.SetShaderParameter("selected_province_id", -1f);
        _globeMaterial?.SetShaderParameter("selected_region_live", 0f);
    }

    private Image _selectedRegionImage;
    private ImageTexture _selectedRegionTexture;
    private readonly byte[] _selectedRegionPixels = new byte[256 * 256];

    /// <summary>
    /// Light the live region the player picked, every province of it. The tint used to follow
    /// the authored raster, so a region a border had cut lit up as the whole region it had been
    /// in 2024 - and a region stitched together from pieces lit up as only the piece clicked.
    /// One byte per province, read by the shader the way the front membership is.
    /// </summary>
    private void SelectedRegionMembers(int liveRegion)
    {
        if (_globeMaterial is null) return;
        var map = _campaign?.ProvinceMap;
        if (liveRegion < 0 || map is null)
        {
            _globeMaterial.SetShaderParameter("selected_region_live", 0f);
            return;
        }
        Array.Clear(_selectedRegionPixels);
        var states = map.ProvinceToState;
        for (var province = 1; province < states.Length && province < _selectedRegionPixels.Length; province++)
            if (states[province] == liveRegion) _selectedRegionPixels[province] = 255;
        if (_selectedRegionImage is null) _selectedRegionImage = Image.CreateFromData(256, 256, false, Image.Format.R8, _selectedRegionPixels);
        else _selectedRegionImage.SetData(256, 256, false, Image.Format.R8, _selectedRegionPixels);
        UpdateMapImage("selected_region_members", _selectedRegionImage, ref _selectedRegionTexture);
        _globeMaterial.SetShaderParameter("selected_region_live", 1f);
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (LoadingInputBlocked || MediaLibraryInputBlocked || MapLayerDrawerInputBlocked) return "";
        var supportTip = SupportMapTooltip(atPosition);
        if (supportTip.Length > 0) return supportTip;
        var eventTip = EventPulseTooltip(atPosition);
        if (eventTip.Length > 0) return eventTip;
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
            var battleText = Simulation.Loc.Format("BATTLE · province #{0:N0}\n{1} vs {2}\nLosses {3:N0} / {4:N0}",
                       battle.ProvinceId, attacker?.DisplayName ?? unknown, defender?.DisplayName ?? unknown,
                       battle.AttackerLosses, battle.DefenderLosses) + "\n" + BattleMapTooltip(battle) +
                   (latest is null ? "" : "\n" + Simulation.Loc.Format(
                       "Power {0:0.0} / {1:0.0} · plan {2:P0}",
                       latest.AttackerPower, latest.DefenderPower, latest.AttackerPlanning));
            if (_campaign is not null)
                foreach (var unit in _campaign.Armies)
                    if (unit.BattleId == battle.Id)
                    {
                        battleText += "\n" + Simulation.StaffReasons.Explain(_campaign, unit);
                        break;
                    }
            return battleText;
        }
        if (TryPickArmyStack(atPosition, out var stack, selectableOnly: false))
        {
            if (stack.Count == 0) return "";
            var lead = stack[0];
            var owner = _campaign?.NationById(lead.NationId);
            var name = owner?.DisplayName ?? Simulation.Loc.Get("Unknown");
            var personnel = stack.Sum(DivisionPersonnel);
            var condition = Simulation.MilitaryConditionReadout.ForPlayer(_campaign, stack, !FogOfWarActive);
            var readiness = condition.Known ?
                Simulation.Loc.Format("Organisation: {0:P0}", condition.Organisation) + "\n" +
                Simulation.Loc.Format("Equipment: {0:P0}", condition.Equipment) : "";
            if (!Simulation.CampaignIntel.PlayerCanInspect(_campaign, lead.NationId))
                return Simulation.Loc.Format("{0} · {1:N0} observed divisions", name, stack.Count) + "\n" +
                       Simulation.Loc.Format("Observed personnel: {0:N0}", personnel) + "\n" +
                       (readiness.Length == 0 ? "" : Simulation.Loc.Get("Observed combat readiness") + "\n" + readiness + "\n") +
                       (condition.Formations > 1 ? Simulation.Loc.Get("Division-weighted average of observed formations.") + "\n" : "") +
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
                       : Simulation.Loc.Get("Click to select every division represented by this counter. A + marks nearby provinces grouped at this zoom.")) +
                   "\n" + Simulation.ArmyVocabulary.Situation(_campaign, lead) +
                   "\n" + Simulation.StaffReasons.Explain(_campaign, lead);
        }
        if (!TryPick(atPosition, out var pick, updateSelection: false)) return UnclaimedTooltip(atPosition);
        var state = pick.State is null
            ? Simulation.Loc.Get("Unmapped administrative area")
            : pick.State.DisplayName + " · " + Simulation.Loc.Get(pick.State.Kind);
        var controller = pick.ProvinceId > 0 && _campaign?.ProvinceController is { } controllers
            ? _nationByMapIndex.GetValueOrDefault(controllers[pick.ProvinceId])
            : pick.Nation;
        var occupation = pick.ProvinceId > 0 && _campaign?.ProvinceOccupation is { } occupations
            ? occupations[pick.ProvinceId]
            : 0f;
        var roads = Simulation.WaterConditions.RoadThroughput(_campaign, pick.Latitude);
        var hydrology = pick.ProvinceId > 0 && _provinceTopology is not null
            ? (_provinceTopology.HasRiver(pick.ProvinceId)
                ? "\n" + Simulation.Loc.Get("River terrain · crossings affect march and attack") +
                    "\n" + Simulation.Loc.Format("Seasonal crossing difficulty: {0:P0}", Simulation.WaterConditions.CrossingDifficulty(_campaign, pick.Latitude)) : "") +
              (_provinceTopology.HasLakeAccess(pick.ProvinceId)
                ? "\n" + Simulation.Loc.Get("Lake shore · local supply access") +
                    "\n" + Simulation.Loc.Format("Lake logistics availability: {0:P0}", Simulation.WaterConditions.LakeAvailability(_campaign, pick.Latitude)) : "") +
              (roads < 0.995f
                ? "\n" + Simulation.Loc.Format("Winter roads carry {0:P0} of fair-weather traffic.", roads) : "")
            : "";
        return pick.Nation.DisplayName + " (" + pick.Nation.Tag + ")\n" + state + "\n" +
               Simulation.StateIdentity.Brief(_campaign, pick.Nation) + "\n" +
               Simulation.Loc.Format("Province #{0:N0}", pick.ProvinceId) +
               (controller is null || controller == pick.Nation ? ""
                   // Held by arrangement - a lease, troops without a war - says so, not "occupied".
                   : pick.State is not null && Simulation.LandArrangementSystem.Covering(_campaign, pick.State.MapIndex, controller.Id) is not null
                       ? "\n" + Simulation.LandArrangementSystem.Describe(_campaign, pick.State)
                       : "\n" + Simulation.Loc.Format(
                   "Occupied by {0} · {1:P0} secured\nStill owned by {2}: an occupation is not an annexation. Only a signed peace moves the border.",
                   controller.DisplayName, occupation, pick.Nation.DisplayName)) +
               (_mapMode == StrategicMapMode.Resources && pick.State is not null ? "\n" + Simulation.Loc.Format(
                   "{0} · resource potential {1:0.##}", Simulation.Loc.Get(Simulation.CommodityCatalog.Get(_resourceGood).Name),
                   Simulation.ResourceAtlas.Potential(pick.State, _resourceGood)) : "") +
               hydrology + (pick.ProvinceId > 0 && _provinceTopology is not null
                   ? TerrainDescription(_provinceTopology.TerrainAt(pick.ProvinceId)) : "");
    }

    private List<Simulation.Army> ArmiesInScreenRect(Rect2 rect)
    {
        var selected = new List<Simulation.Army>();
        if (!IsInstanceValid(_camera) || (!_flatMap && _distance > 13.5f)) return selected;
        foreach (var marker in _armyStacks.Values)
        {
            var node = marker.Node;
            // A counter standing behind another country's in the same province is hidden, not
            // gone: a box drawn over the province takes its divisions too (CoverSharedProvinces).
            if (!IsInstanceValid(node) || (!node.IsVisibleInTree() && !_coveredCounters.Contains(node)) ||
                !marker.Plate.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (rect.HasPoint(_camera.UnprojectPosition(node.GlobalPosition)))
                selected.AddRange((marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack)
                    .Where(army => Simulation.ArmyInteraction.CanSelect(_campaign, army)));
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

    private TextureRect _picture = null!;

    private bool _measuringGpu;

    /// <summary>
    /// What the graphics card spent on the globe's last frame, in milliseconds; below zero until
    /// the first measurement. The quality governor reads it to tell a busy card, which fewer
    /// pixels help, from a busy processor, which they do not.
    /// </summary>
    public double MeasuredGpuMs
    {
        get
        {
            if (!IsInstanceValid(_viewport)) return -1;
            var rid = _viewport.GetViewportRid();
            if (!_measuringGpu)
            {
                RenderingServer.ViewportSetMeasureRenderTime(rid, true);
                _measuringGpu = true;
                return -1;
            }
            return RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
        }
    }

    /// <summary>
    /// What share of the window's pixels the globe's 3D is drawn at, set from the video
    /// settings (`render_scale`): 1 is native, and a weaker graphics card can draw fewer and
    /// let them be scaled up.
    /// </summary>
    public float RenderScale
    {
        get => IsInstanceValid(_viewport) ? _viewport.Scaling3DScale : 1f;
        set
        {
            if (IsInstanceValid(_viewport)) _viewport.Scaling3DScale = Mathf.Clamp(value, 0.25f, 2f);
        }
    }

    /// <summary>
    /// The viewport at the window's pixels for the area the globe covers, and its 2D size in
    /// interface units, so `UnprojectPosition`, `ProjectRayOrigin` and every overlay keep the
    /// units they were written in while the picture gets every pixel the screen has.
    /// </summary>
    private void ResizeViewport()
    {
        if (!IsInstanceValid(_viewport)) return;
        var logical = Size.X >= 1f && Size.Y >= 1f ? Size : GetViewport().GetVisibleRect().Size;
        var toScreen = GetViewport().GetScreenTransform().Scale;
        var pixels = new Vector2I(
            Math.Max(1, (int)MathF.Round(logical.X * MathF.Abs(toScreen.X))),
            Math.Max(1, (int)MathF.Round(logical.Y * MathF.Abs(toScreen.Y))));
        var units = new Vector2I(Math.Max(1, (int)MathF.Round(logical.X)), Math.Max(1, (int)MathF.Round(logical.Y)));
        if (_viewport.Size != pixels) _viewport.Size = pixels;
        if (_viewport.Size2DOverride != units) _viewport.Size2DOverride = units;
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
// Where the nearest river's real course lies from each texel (RG, 16ths of a texel from 128) and
// that river's importance (B, 0 = none near). Read texel by texel, never filtered.
uniform sampler2D river_centre_map : filter_nearest, repeat_enable;
uniform sampler2D crossover_excavations : filter_nearest, repeat_enable;
uniform float crossover_excavations_active = 0.0;
uniform sampler2D lake_surface : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D river_surface : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float river_inset = -0.10;
uniform float river_curve_filter = 1.0;
uniform float river_near_opacity = 0.92;
uniform float river_far_opacity = 0.58;
// River width on screen, as half-widths in pixels: a headwater's thread and a great river's line.
// Close in, a river is never wider than water that size is (river_real_half, in map texels).
uniform float river_thread_px = 0.75;
uniform float river_great_px = 1.9;
uniform float river_real_half = 0.11;
uniform float water_shore_width = 0.75;
uniform float water_depth_reach = 5.0;
uniform float water_texture_strength = 0.65;
uniform float water_repeat = 640.0;
uniform float water_ripple_strength = 0.12;
uniform float water_animation_speed = 0.0;
uniform sampler2D province_control_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D province_owner_colors : source_color, filter_nearest, repeat_disable;
// Who owns each province now, for the provinces whose owner has changed since the country id
// map was drawn: owner id in r/g, and b set where the entry means anything. Read as data, so
// no source_color on it.
uniform sampler2D province_owner_ids : filter_nearest, repeat_disable;
uniform float owners_moved = 0.0;
uniform sampler2D nation_palette : source_color, filter_nearest, repeat_disable;
uniform float border_pixels = 1.35;
uniform float province_border_pixels = 2.0;
uniform float province_border_opacity = 0.72;
uniform float province_border_halo = 0.15;
uniform float front_pixels = 1.6;
uniform sampler2D front_membership : filter_nearest, repeat_disable;
uniform sampler2D map_mode_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D resource_colors : source_color, filter_nearest, repeat_disable;
uniform float resource_mode = 0.0;
uniform sampler2DArray terrain_tiles : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D province_terrain : filter_nearest, repeat_disable;
uniform sampler2D terrain_surface : filter_nearest, repeat_enable;
uniform sampler2D border_plan_targets : filter_nearest, repeat_disable;
uniform sampler2D border_controller_ids : filter_nearest, repeat_disable;
uniform sampler2D relief_height : filter_linear_mipmap, repeat_enable;
uniform sampler2D relief_color : source_color, filter_linear_mipmap, repeat_enable;
// The depth of the sea (map_data/bathymetry.png): 1 at the coast and on the shelves, 0 over the abyss.
uniform sampler2D ocean_depth : filter_linear_mipmap, repeat_enable;
uniform float ocean_depth_ready = 0.0;
uniform vec3 ocean_shelf_color = vec3(0.050, 0.185, 0.225);
uniform float terrain_detail = 0.35;
// The atlas hillshade: slopes lit from the north-west, as a printed relief map lights them. It is
// worked out on the surface itself, so the flat map - which the scene's sun meets face on -
// shows its mountains as plainly as the globe does.
uniform float relief_shading = 0.9;
uniform float terrain_ready = 0.0;
uniform float terrain_mode = 0.0;
uniform float political_opacity = 0.62;
// The nation palette a little short of full strength, so the paint reads as ink over ground
// rather than as a poster: the relief under China is a country, not a yellow sheet.
uniform float political_saturation = 0.86;
// 1 on the flat map: an atlas sheet read straight on, without the globe's light and curvature
// to soften it. The terrain tiles showing through the country colours read there as blotches
// and their fine grain as noise, so the flat map wears the colours thicker and the grain thinner.
uniform float flat_projection = 0.0;
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
// The live region the player picked, province by province (SelectedRegionMembers): a region a border
// has cut or a treaty has stitched together is no longer the region the raster was drawn with.
uniform sampler2D selected_region_members : filter_nearest, repeat_disable;
uniform float selected_region_live = 0.0;
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

/// One row of the four-by-four block of ids around a cell: the column before it, its own two
/// columns, and the one after.
vec4 id_row(sampler2D ids, vec2 cell, vec2 size) {
    return vec4(id_at(ids, cell + vec2(-1.0, 0.0), size), id_at(ids, cell, size),
                id_at(ids, cell + vec2(1.0, 0.0), size), id_at(ids, cell + vec2(2.0, 0.0), size));
}

/// Which of four ids are the one asked about, as ones and zeroes.
vec4 same_id(vec4 ids, float id) {
    return step(abs(ids - vec4(id)), vec4(0.5));
}

/// How strongly one label claims a point of a cell, smoothed over the texels around it.
///
/// Each of the cell's four corners is blurred with its eight neighbours - a tent of one texel
/// either way - and the point is interpolated between the blurred corners rather than the raw
/// ones. A blur alone would also dissolve anything a texel wide, and a one-texel island or a
/// city state is exactly the ground a player most wants to see, so a corner the blur would
/// hand to its neighbours is pulled most of the way back to the label it was painted with.
float label_claim(vec4 m0, vec4 m1, vec4 m2, vec4 m3, vec2 f) {
    vec2 r0 = vec2(m0.x * 0.5 + m0.y + m0.z * 0.5, m0.y * 0.5 + m0.z + m0.w * 0.5);
    vec2 r1 = vec2(m1.x * 0.5 + m1.y + m1.z * 0.5, m1.y * 0.5 + m1.z + m1.w * 0.5);
    vec2 r2 = vec2(m2.x * 0.5 + m2.y + m2.z * 0.5, m2.y * 0.5 + m2.z + m2.w * 0.5);
    vec2 r3 = vec2(m3.x * 0.5 + m3.y + m3.z * 0.5, m3.y * 0.5 + m3.z + m3.w * 0.5);
    vec2 top = (r0 * 0.5 + r1 + r2 * 0.5) * 0.25;
    vec2 bottom = (r1 * 0.5 + r2 + r3 * 0.5) * 0.25;
    vec2 own_top = m1.yz;
    vec2 own_bottom = m2.yz;
    top = mix(top, own_top, 0.8 * mix(step(0.48, top), 1.0 - step(0.52, top), own_top));
    bottom = mix(bottom, own_bottom, 0.8 * mix(step(0.48, bottom), 1.0 - step(0.52, bottom), own_bottom));
    return mix(mix(top.x, top.y, f.x), mix(bottom.x, bottom.y, f.x), f.y);
}

/// One label's turn in the vote below: its claim is weighed only if it is not already the
/// leader or the runner-up, which with two labels in a block - nearly every block - means
/// each is weighed once however many of the sixteen texels it holds.
void weigh_label(float label, vec4 r0, vec4 r1, vec4 r2, vec4 r3, vec2 f,
                 inout float best, inout float best_id, inout float second, inout float second_id) {
    if (abs(label - best_id) < 0.5 || abs(label - second_id) < 0.5) return;
    float claim = label_claim(same_id(r0, label), same_id(r1, label), same_id(r2, label), same_id(r3, label), f);
    if (claim > best) { second = best; second_id = best_id; best = claim; best_id = label; }
    else if (claim > second) { second = claim; second_id = label; }
}

/// Boundaries recovered from the four-by-four block of ids around a cell.
///
/// The four-texel reconstruction recovers a boundary to within half a texel, which is exact
/// for a straight edge at forty-five degrees and wrong for everything else: a coast running at
/// a shallow angle was digitised as runs of four texels and a one-texel step, and half a texel
/// of reconstruction keeps every step. Close in a texel is several screen pixels, and the
/// steps are the staircase the map was criticised for.
///
/// So every label in the block makes its smoothed claim, the strongest takes the pixel, and
/// the coverage is how far it leads the runner-up. The half-way line is where the two claims
/// are equal - the coast as it ran before it was digitised, a shallow slope as a slope and a
/// round bay as a round bay. Every label in the block is asked, not only those at the cell's
/// corners: a label two texels away still has a claim here, and leaving it out makes the
/// answer jump at every cell edge, which is a staircase again by another route. With two
/// labels the lead changes by one a texel across a straight edge, as the raw coverage did,
/// so the lines drawn off it keep their width in screen pixels.
///
/// `reach` is how far away that edge is, in texels. The coverage cannot say: it is full half a
/// texel from the boundary, and every pixel beyond that read as standing half a texel off it.
/// Once a texel was a few pixels across, the shoulder and the stroke of every line reached
/// right out to the edge of the block - a pale band two texels wide down each side of each
/// province line, its outer edge the texel grid, and a grey one down every coast. The lead
/// itself keeps growing: one a texel for the first half texel, as the coverage does, then a
/// half a texel as the blur runs out, to one at a texel and a half.
float resolve_block(vec4 r0, vec4 r1, vec4 r2, vec4 r3, vec2 f, out float near_id, out float other_id, out float reach) {
    float best = -1.0;
    float best_id = -1.0;
    float second = 0.0;
    float second_id = -1.0;
    // The cell's own corners first: they are nearly always the two that matter.
    weigh_label(r1.y, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r1.z, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r2.y, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r2.z, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r0.x, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r0.y, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r0.z, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r0.w, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r1.x, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r1.w, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r2.x, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r2.w, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r3.x, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r3.y, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r3.z, r0, r1, r2, r3, f, best, best_id, second, second_id);
    weigh_label(r3.w, r0, r1, r2, r3, f, best, best_id, second, second_id);
    near_id = best_id;
    other_id = second_id < -0.5 ? best_id : second_id;
    float lead = clamp(best - second, 0.0, 1.0);
    reach = lead < 0.5 ? lead : lead * 2.0 - 0.5;
    return clamp(0.5 + best - second, 0.0, 1.0);
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
vec4 slot_row(sampler2D surface, vec2 cell, vec2 size) {
    return vec4(slot_at(surface, cell + vec2(-1.0, 0.0), size), slot_at(surface, cell, size),
                slot_at(surface, cell + vec2(1.0, 0.0), size), slot_at(surface, cell + vec2(2.0, 0.0), size));
}

float terrain_coverage(sampler2D surface, vec2 uv, bool smooth_edges, out float near_slot, out float other_slot) {
    vec2 size = vec2(textureSize(surface, 0));
    vec2 g = uv * size - vec2(0.5);
    vec2 base_cell = floor(g);
    vec2 f = g - base_cell;
    if (smooth_edges) {
        float reach;
        return resolve_block(slot_row(surface, base_cell + vec2(0.0, -1.0), size), slot_row(surface, base_cell, size),
                             slot_row(surface, base_cell + vec2(0.0, 1.0), size), slot_row(surface, base_cell + vec2(0.0, 2.0), size),
                             f, near_slot, other_slot, reach);
    }

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
float boundary_coverage(sampler2D ids, vec2 uv, bool smooth_edges, out float near_id, out float other_id, out float reach) {
    vec2 size = vec2(textureSize(ids, 0));
    vec2 grid = uv * size - vec2(0.5);
    vec2 base_cell = floor(grid);
    vec2 f = grid - base_cell;

    if (smooth_edges) {
        return resolve_block(id_row(ids, base_cell + vec2(0.0, -1.0), size), id_row(ids, base_cell, size),
                             id_row(ids, base_cell + vec2(0.0, 1.0), size), id_row(ids, base_cell + vec2(0.0, 2.0), size),
                             f, near_id, other_id, reach);
    }
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
    reach = abs(coverage - 0.5);
    return coverage;
}

/// The country a map cell belongs to NOW. The id map was drawn from the authored world and is
/// never rewritten - it is sixteen million cells - so a province a peace signed over still
/// says its old owner in it. Everything drawn off the country id (the border stroke, the
/// highlighted country, the map modes) goes through this instead, and a province whose owner
/// has moved answers with its new one.
float nation_at(vec2 cell, vec2 size) {
    float nation = id_at(nation_ids, cell, size);
    if (owners_moved < 0.5) return nation;
    float province = id_at(province_ids, cell, size);
    if (province < 0.5) return nation;
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    vec4 moved = texture(province_owner_ids, (vec2(low, high) + vec2(0.5)) / 256.0);
    return moved.b > 0.5 ? floor(moved.r * 255.0 + 0.5) + floor(moved.g * 255.0 + 0.5) * 256.0 : nation;
}

/// boundary_coverage, for countries: the same reconstruction, read through nation_at.
vec4 nation_row(vec2 cell, vec2 size) {
    return vec4(nation_at(cell + vec2(-1.0, 0.0), size), nation_at(cell, size),
                nation_at(cell + vec2(1.0, 0.0), size), nation_at(cell + vec2(2.0, 0.0), size));
}

/// The same for countries - and, the sea being a country id like any other here, for every
/// shore on the map as well.
float nation_coverage(vec2 uv, bool smooth_edges, out float near_id, out float other_id, out float reach) {
    vec2 size = vec2(textureSize(nation_ids, 0));
    vec2 grid = uv * size - vec2(0.5);
    vec2 base_cell = floor(grid);
    vec2 f = grid - base_cell;

    if (smooth_edges) {
        return resolve_block(nation_row(base_cell + vec2(0.0, -1.0), size), nation_row(base_cell, size),
                             nation_row(base_cell + vec2(0.0, 1.0), size), nation_row(base_cell + vec2(0.0, 2.0), size),
                             f, near_id, other_id, reach);
    }
    float i00 = nation_at(base_cell, size);
    float i10 = nation_at(base_cell + vec2(1.0, 0.0), size);
    float i01 = nation_at(base_cell + vec2(0.0, 1.0), size);
    float i11 = nation_at(base_cell + vec2(1.0, 1.0), size);

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
    reach = abs(coverage - 0.5);
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
" + OccupationShaderFunctions + @"
vec4 map_mode_color(float nation) {
    float low = mod(nation, 256.0);
    float high = floor(nation / 256.0);
    return texture(map_mode_colors, (vec2(low, high) + vec2(0.5)) / 256.0);
}
float selected_member(float province) {
    float low = mod(province, 256.0);
    float high = floor(province / 256.0);
    return texture(selected_region_members, (vec2(low, high) + vec2(0.5)) / 256.0).r;
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
// Positive cubic B-spline weights round raster bends without the overshoot of a
// sharpening cubic. Four hardware-linear fetches reproduce a 4x4 footprint.
// Called only near a river: most globe fragments retain the single-fetch path.
float river_contour(vec2 uv, float linear_distance) {
    if (river_curve_filter < 0.001 || abs(linear_distance) > 2.0) return linear_distance;
    vec2 size = vec2(textureSize(hydrology_map, 0));
    vec2 point = uv * size - 0.5;
    vec2 cell = floor(point);
    vec2 f = fract(point);
    vec2 inverse_f = vec2(1.0) - f;
    vec2 w0 = inverse_f * inverse_f * inverse_f / 6.0;
    vec2 w1 = (3.0 * f * f * f - 6.0 * f * f + 4.0) / 6.0;
    vec2 w2 = (-3.0 * f * f * f + 3.0 * f * f + 3.0 * f + 1.0) / 6.0;
    vec2 w3 = f * f * f / 6.0;
    vec2 first = w0 + w1;
    vec2 second = w2 + w3;
    vec2 a = (cell - 1.0 + w1 / first + 0.5) / size;
    vec2 b = (cell + 1.0 + w3 / second + 0.5) / size;
    // Longitude repeats, but neither pole may sample the opposite hemisphere.
    a.y = clamp(a.y, 0.5 / size.y, 1.0 - 0.5 / size.y);
    b.y = clamp(b.y, 0.5 / size.y, 1.0 - 0.5 / size.y);
    float north = texture(hydrology_map, a).g * first.x +
        texture(hydrology_map, vec2(b.x, a.y)).g * second.x;
    float south = texture(hydrology_map, vec2(a.x, b.y)).g * first.x +
        texture(hydrology_map, b).g * second.x;
    float curved = ((north * first.y + south * second.y) * 255.0 - 128.0) / 16.0;
    // Keep tiny authored headwaters: cubic averaging must not erase a one-cell source.
    curved = min(curved, linear_distance + 0.25);
    return mix(linear_distance, curved, river_curve_filter);
}
// Same rounding as DateLineSeam.WrappedFrequency: a whole number of cycles
// around the world, so the wave at longitude 180 is the wave at -180.
float seam_frequency(float freq, float width) {
    return sign(freq) * max(1.0, round(abs(freq) * width / 6.2831853)) * 6.2831853 / max(width, 1.0);
}
// Mipmaps are averaged inward, so the column at u=0 never meets the column at
// u=1 and the date line reads as a second border. Near that cut, bilinear-sample
// the base level with longitude wrapped and latitude clamped. Poles stay put.
" + SeamSamplingFunctions() + @"
void fragment() {
    float excavation = crossover_excavations_active * texture(crossover_excavations, UV).r;
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
        map_uv = UV + map_drift * id_wander_texels * clamp(3.2 - id_span * 0.4, 0.0, 1.0) / id_size;
    }

    // Far away the authored raster is right: a screen pixel covers many map texels and what
    // the eye wants is the average, mipmapped and anisotropic. Close up the raster is exactly
    // wrong - one texel covers many pixels, so nearest sampling shows the grid it was stored
    // on. So close up the colour is rebuilt from the country id map instead, and the boundary
    // between two countries is recovered as a straight edge at whatever angle it really runs,
    // antialiased over one screen pixel. The raster is not being filtered; it is not being
    // asked.
    vec4 political_smooth = seam_sample_political_map(UV);
    // Smoothed boundaries only where a texel is bigger than a pixel - further out the raster
    // is averaged by its mipmaps and has no staircase to show - and only for a player who
    // has the map smoothed at all.
    bool smooth_edges = map_smoothing > 0.5 && id_span < 8.0;
    float near_nation;
    float other_nation;
    float nation_reach;
    float nation_cover = nation_coverage(map_uv, smooth_edges, near_nation, other_nation, nation_reach);
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
    political.rgb = mix(vec3(dot(political.rgb, vec3(0.2126, 0.7152, 0.0722))), political.rgb, political_saturation);
    float rim = pow(1.0 - max(dot(normalize(NORMAL), normalize(VIEW)), 0.0), 2.0);
    // The sea has a floor. Deep water keeps the strategic navy it always had; the continental
    // slope lifts it a little, and the shelf along every coast - the North Sea, the Yellow Sea,
    // the Sunda shelf, the Grand Banks - is a clear shallow teal, so a coast reads as a coast from
    // orbit, the way it does on every Paradox map, before any border line has been drawn.
    float sea_floor = ocean_depth_ready > 0.5 ? seam_sample_ocean_depth(UV).r : 0.0;
    // A smoothstep over a smooth raster draws contour lines across an ocean that has none in
    // it: the shelf arrives as a band with an edge parallel to the texel rows. The same wander
    // the coast uses is added here, a texel or so wide, so the shelf reads as ground under
    // water rather than as a level in a depth chart.
    vec2 sea_size = vec2(textureSize(ocean_depth, 0));
    float sea_wander = grain(UV * sea_size * 0.85 + 17.0);
    sea_floor = clamp(sea_floor + sea_wander * 0.014, 0.0, 1.0);
    float slope_rise = smoothstep(0.62, 0.955, sea_floor);
    float shelf = smoothstep(0.945, 0.992, sea_floor);
    float littoral = smoothstep(0.982, 0.999, sea_floor);
    vec3 abyss_color = ocean_color * 0.76;
    vec3 sea = mix(abyss_color, ocean_color * 1.24, slope_rise);
    sea = mix(sea, ocean_shelf_color, shelf * 0.82);
    sea = mix(sea, ocean_shelf_color * 1.22 + vec3(0.015, 0.045, 0.055), littoral * 0.55);
    // Living water. Three directional sine waves, crossing at angles, rather than value
    // noise: noise interpolates on its own axis-aligned grid, and on a surface this large
    // the grid arrives as a tiling of diamonds with visible seams. Sines have no grid to
    // show. The directions are deliberately off-axis and incommensurate, so the combined
    // field never lines up with itself into a pattern, and the whole thing drifts slowly.
    // Faded out at strategic zoom, where the mipmaps already average the surface flat and
    // the same field would only add shimmer.
    vec2 sea_texels = vec2(fract(UV.x) * sea_size.x, clamp(UV.y, 0.0, 1.0) * sea_size.y);
    float wave_fade = clamp(1.0 - id_span, 0.0, 1.0);
    // A slow warp of the sampling point, so even the wave lines breathe rather than march.
    // The east-west term is a whole number of cycles, or the warp itself is a seam.
    float warp_x = seam_frequency(0.002, sea_size.x);
    vec2 warp = vec2(sin(TIME * 0.05 + sea_texels.y * 0.002), cos(TIME * 0.04 + sea_texels.x * warp_x));
    vec2 wave_at = sea_texels + warp * 26.0;
    float waves = sin(dot(wave_at, vec2(seam_frequency(0.0105, sea_size.x), 0.0062)) + TIME * 0.55)
                + 0.62 * sin(dot(wave_at, vec2(seam_frequency(-0.0180, sea_size.x), 0.0094)) + TIME * 0.41)
                + 0.38 * sin(dot(wave_at, vec2(seam_frequency(0.0072, sea_size.x), -0.0230)) + TIME * 0.72);
    waves /= 2.0;
    vec3 water = sea * (0.74 + rim * 0.26);
    water *= 1.0 + waves * 0.085 * wave_fade;
    // Sun glint riding the sharpest of the three - the crests catching light, which is what
    // tells the eye this is a surface and not a fill. Kept at low zoom too: a flat map has
    // no rim to speak of, and gating it on the limb would delete it where players live.
    //
    // One crest train with its phase bent by the second wave, not the sum of the two: added
    // together they only reinforce along short diagonals, and the light landed as scattered
    // white scratches across the open sea rather than as a wave line.
    float crest = sin(dot(wave_at, vec2(-0.0180, 0.0094)) + TIME * 0.41
                + 1.1 * sin(dot(wave_at, vec2(0.0330, 0.0195)) + TIME * 0.9));
    float glint = pow(clamp(crest * 0.5 + 0.5, 0.0, 1.0), 6.0);
    water += vec3(0.58, 0.75, 0.84) * glint * 0.18 * wave_fade;
    // Foam & coastal ink: a pale surf line where the sea meets the shore, framed by a subtle
    // cartographic coastal shadow so continents stand out crisply from the ocean.
    float shore_band = max(fwidth(political.a) * 2.2, 0.022);
    float shore_edge = smoothstep(shore_band, 0.0, abs(political.a - 0.5));
    float reef_halo = smoothstep(shore_band * 3.8, 0.0, abs(political.a - 0.5)) * (1.0 - political.a);
    water = mix(water, ocean_shelf_color * 1.38 + vec3(0.03, 0.09, 0.11), reef_halo * 0.42);
    water += vec3(0.84, 0.91, 0.95) * shore_edge * 0.50 * wave_fade;
    water = mix(water, water * 0.58, shore_edge * 0.38 * (1.0 - political.a));
    vec2 texel = 1.0 / vec2(textureSize(province_ids, 0));
    float region = map_id(region_ids, map_uv);
    float province = map_id(province_ids, map_uv);
    vec2 destruction_uv = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    if (texture(front_membership, destruction_uv).a > 0.5) { discard; }
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
    // Owner remains the ground; keylined controller ribbons mean occupation,
    // never annexation. One shared style for flat map and globe.
    float treaty = step(0.95, live_control.a);
    float occupied = occupation_mask(live_control);
    float leased = lease_mask(live_control);
    political.rgb = mix(political.rgb, live_control.rgb * political_light, treaty);
    political.rgb = occupation_ribbons(political.rgb, live_control.rgb, UV,
        province_detail, occupied);
    political.rgb = lease_weave(political.rgb, live_control.rgb, UV, province_detail, leased);
    // The map modes colour the country that owns this ground now (near_nation reads the moved
    // owners), not the one the raster was painted with: the diplomatic view of 2039 drew the
    // borders of 2024, Belarus annexed and all.
    vec4 strategic = map_mode_color(near_nation);
    political.rgb = mix(political.rgb, strategic.rgb * mix(0.84, 1.02, political_light),
        map_mode * strategic.a);
    vec2 resource_uv = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    vec4 resource = texture(resource_colors, resource_uv);
    political.rgb = mix(political.rgb, resource.rgb, resource_mode * resource.a);
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
        float slot_cover = terrain_coverage(terrain_surface, terrain_lookup, smooth_edges, near_slot, other_slot);
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
        // Close in a tile is magnified until its texels show. A second look at the same ground at
        // four times the frequency puts fine grain back under the coarse - as brightness only, so
        // the colours stay the tile's own - and only as close in as the province lines appear.
        if (terrain_detail > 0.001 && province_detail > 0.001 && flat_projection < 0.5) {
            vec3 fine = texture(terrain_tiles, vec3(terrain_uv * 4.0, near_slot)).rgb;
            vec3 tile_mean = textureLod(terrain_tiles, vec3(terrain_uv, near_slot), 7.0).rgb;
            float grain_shift = dot(fine - tile_mean, vec3(0.2126, 0.7152, 0.0722));
            ground *= 1.0 + terrain_detail * province_detail * grain_shift * 2.2;
        }
        vec3 relief_tint = seam_sample_relief_color(UV).rgb;
        ground = mix(ground, relief_tint, relief_color_opacity);
        // Political tint is an albedo blend, not transparent 3D geometry.
        // Keep surface detail while preserving recognisable controller colours.
        float ground_light = clamp(dot(ground, vec3(0.2126, 0.7152, 0.0722)) * 1.4 + 0.50, 0.62, 1.1);
        // On the flat sheet the ground's brightness moves the colour within a narrower band, and
        // the colour covers more of the ground: the relief shows through the hillshade below.
        ground_light = mix(ground_light, clamp(ground_light, 0.84, 1.03), flat_projection);
        float colour_cover = mix(political_opacity, min(0.93, political_opacity + 0.17), flat_projection);
        vec3 colored_ground = mix(ground, political.rgb * ground_light, colour_cover);
        vec3 land_surface = terrain_mode > 1.5 ? relief_tint : (terrain_mode > 0.5 ? ground : colored_ground);
        base = mix(water, land_surface, political.a);
        vec2 ht = 1.0 / vec2(textureSize(relief_height, 0));
        float west = seam_sample_relief_height(vec2(UV.x - ht.x, clamp(UV.y, ht.y, 1.0 - ht.y))).r;
        float east = seam_sample_relief_height(vec2(UV.x + ht.x, clamp(UV.y, ht.y, 1.0 - ht.y))).r;
        float north = seam_sample_relief_height(vec2(UV.x, clamp(UV.y - ht.y, 0.0, 1.0))).r;
        float south = seam_sample_relief_height(vec2(UV.x, clamp(UV.y + ht.y, 0.0, 1.0))).r;
        float elev = (west + east + north + south) * 0.25;
        vec2 slope = clamp(vec2(west - east, south - north) * relief_strength * 8.5, vec2(-0.85), vec2(0.85));
        float slope_mag = length(slope);
        vec3 relief_normal = normalize(vec3(slope * political.a, 1.0));
        NORMAL_MAP = normalize(vec3(slope * political.a * 0.62, 1.0)) * 0.5 + 0.5;
        NORMAL_MAP_DEPTH = 1.0;
        // West and north are -x and +y in the surface's own frame; the light stands high over
        // the north-west. Flat ground keeps its colour exactly: the shade is the difference a
        // slope makes, not a dimmer on the whole map.
        vec3 atlas_light = normalize(vec3(-0.62, 0.62, 0.46));
        float flat_lit = atlas_light.z;
        float lit = dot(relief_normal, atlas_light);
        float valley_ao = clamp(1.0 - slope_mag * 0.16 * (1.0 - smoothstep(0.45, 0.78, elev)), 0.78, 1.0);
        float hillshade = clamp((1.0 + (lit - flat_lit) * 1.48) * valley_ao, 0.52, 1.42);
        // Subtle alpine ridge highlight on high mountain chains so the Himalayas, Andes, Alps
        // and Caucasus read with physical relief even through political colour.
        float alpine_ridge = smoothstep(0.58, 0.86, elev) * smoothstep(0.12, 0.52, slope_mag) * clamp(lit - flat_lit + 0.15, 0.0, 1.0);
        base = mix(base, base * hillshade + vec3(0.09, 0.095, 0.105) * alpine_ridge,
            political.a * relief_shading * (terrain_mode > 1.5 ? 1.0 : 0.88));
    }
    // Distance fields describe the drawn shore, never the simulation's river strength.
    // Rivers of low importance no longer disappear at a hard intensity threshold.
    vec3 hydrography = texture(hydrology_map, vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0))).rgb;
    float lake_distance = (hydrography.r * 255.0 - 128.0) / 16.0;
    // A river is as wide as it is great: a headwater a thread, the Danube a ribbon. One inset
    // for every river drew brooks as wide as the Rhine and both as pale bands of paint.
    // The raster paints a river as a corridor one to five cells wide - seven to thirty-five
    // kilometres - and drawing the corridor drew rivers as fat sausages that swelled as the camera
    // came down. The line is drawn along the river's real course instead (river_centre_map), as
    // wide as the screen needs to see it (river_thread_px .. river_great_px) and close in never
    // wider than water of that size is (river_real_half).
    ivec2 river_map_size = textureSize(river_centre_map, 0);
    vec2 river_point = vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0)) * vec2(river_map_size) - 0.5;
    ivec2 river_cell = ivec2(floor(river_point));
    float river_centre = 1.0e4;
    float river_importance = 0.0;
    vec2 river_course_sum = vec2(0.0);
    float river_weight = 0.0;
    float river_importance_sum = 0.0;
    vec2 river_fraction = fract(river_point);
    vec2 river_first_course = vec2(0.0);
    bool river_has_course = false;
    bool river_coherent = true;
    for (int ry = 0; ry < 2; ry++) {
        for (int rx = 0; rx < 2; rx++) {
            ivec2 at = river_cell + ivec2(rx, ry);
            ivec2 fetch = ivec2((at.x % river_map_size.x + river_map_size.x) % river_map_size.x,
                clamp(at.y, 0, river_map_size.y - 1));
            vec4 entry = texelFetch(river_centre_map, fetch, 0);
            if (entry.b <= 0.0) continue;
            vec2 course = vec2(at) + (entry.rg * 255.0 - 128.0) / 16.0;
            if (!river_has_course) { river_first_course = course; river_has_course = true; }
            else if (length(course - river_first_course) > 1.6) river_coherent = false;
            float weight = (rx == 0 ? 1.0 - river_fraction.x : river_fraction.x) *
                (ry == 0 ? 1.0 - river_fraction.y : river_fraction.y);
            river_course_sum += course * weight;
            river_importance_sum += entry.b * weight;
            river_weight += weight;
            float reach = length(river_point - course);
            if (reach < river_centre) { river_centre = reach; river_importance = entry.b; }
        }
    }
    // Interpolate projected POINTS, not scalar distances or extended tangents. A
    // straight course is reconstructed exactly; bends become one continuous curve,
    // not a union of tangent spikes or pairwise chords (which draw little ladders).
    // Large jumps mean unrelated channels: retain bounded point caps there instead
    // of inventing a third river between them. Same four fetches, no new texture.
    if (river_coherent && river_weight > 0.0001) {
        river_centre = length(river_point - river_course_sum / river_weight);
        river_importance = river_importance_sum / river_weight;
    }
    vec2 river_line = vec2(river_centre, river_importance);
    float river_size = smoothstep(0.55, 0.98, river_importance);
    vec2 river_texels = UV * vec2(river_map_size);
    float river_texel_px = max(max(length(dFdx(river_texels)), length(dFdy(river_texels))), 0.0001);
    float river_half = max(mix(0.25, 1.0, river_size) * river_real_half,
        mix(river_thread_px, river_great_px, river_size) * river_texel_px);
    float river_distance = river_line.g > 0.0 ? river_centre - river_half : 1.0e4;
    float lake_edge = max(fwidth(lake_distance) * 0.65, 0.08);
    float river_edge = max(river_texel_px * 0.75, 0.005);
    float lake_water = 1.0 - smoothstep(-lake_edge, lake_edge, lake_distance);
    float river_water = 1.0 - smoothstep(-river_edge, river_edge, river_distance);
    float prominent_river = smoothstep(0.78, 0.88, river_line.g);
    float inland_coverage = max(lake_water, river_water);
    float lake_depth = smoothstep(0.0, water_depth_reach, -lake_distance);
    float lake_bank = (1.0 - smoothstep(0.0, water_shore_width + lake_edge, abs(lake_distance))) * lake_water;
    vec2 water_uv = vec2(UV.x * 2.0, UV.y) * water_repeat;
    // A static atlas is the default. Optional subtle animation affects material only,
    // never shoreline, ownership, picking or the paused campaign clock.
    vec2 water_motion = vec2(TIME * water_animation_speed, 0.0);
    float water_detail = max(detail_mix, province_detail) * water_texture_strength;
    vec3 inland_water = mix(vec3(0.028, 0.145, 0.185), vec3(0.014, 0.080, 0.125), lake_depth);
    // River water is water: a clear blue that reads against every political colour - thin, but
    // never the same grey as a border line. The near-black
    // teal it was drew the Han and the Dnieper exactly like the grey region lines beside them, so
    // a player could not tell a river from a border. Great channels run lighter, and carry a
    // brighter current down their middle.
    vec3 river_color = mix(vec3(0.045, 0.190, 0.330), vec3(0.065, 0.260, 0.430), prominent_river);
    float river_current = (1.0 - smoothstep(0.0, river_half * 0.45 + river_edge, river_centre)) * prominent_river;
    river_color = mix(river_color, vec3(0.200, 0.470, 0.620), river_current * 0.55);
    float river_bank = (1.0 - smoothstep(0.0, river_edge * 2.5, abs(river_distance))) * (1.0 - river_water);
    if (inland_coverage > 0.001 && water_detail > 0.001) {
        vec3 lake_tile = texture(lake_surface, water_uv + water_motion * 0.30).rgb;
        vec3 river_tile = texture(river_surface, water_uv + water_motion).rgb;
        // Neutral, bounded luminance variation preserves cartographic water colour even
        // when a mod supplies a brighter tile. Mips suppress distant crawling noise.
        float lake_grain = clamp(dot(lake_tile, vec3(0.2126, 0.7152, 0.0722)) * 12.0 - 0.55, -0.5, 0.7);
        float river_grain = clamp(dot(river_tile, vec3(0.2126, 0.7152, 0.0722)) * 12.0 - 0.55, -0.5, 0.7);
        inland_water *= 1.0 + lake_grain * water_detail;
        river_color *= 1.0 + river_grain * water_detail;
        float ripple = sin(water_uv.x * 9.0 + water_uv.y * 11.0 +
            sin(water_uv.y * 2.7) * 1.7 - TIME * water_animation_speed * 24.0);
        float ripple_fade = 1.0 - smoothstep(0.12, 0.45, max(length(dFdx(water_uv)), length(dFdy(water_uv))));
        inland_water *= 1.0 + ripple * water_ripple_strength * water_detail * ripple_fade;
    }
    base = mix(base, inland_water, lake_water);
    base = mix(base, vec3(0.075, 0.175, 0.185), lake_bank * 0.20);
    float river_opacity = mix(river_far_opacity, river_near_opacity, max(detail_mix, province_detail));
    // A line a pixel or two wide has no width to spare for transparency: drawn at the far opacity
    // the rivers of a whole continent faded out of the middle zoom.
    river_opacity = max(river_opacity, 0.85);
    river_opacity = mix(river_opacity, max(river_opacity, 0.92), prominent_river);
    base = mix(base, river_color, river_water * river_opacity * (1.0 - lake_water));
    // A thin green-brown bank, so the water sits in a valley rather than lying on the land like
    // paint - and not a black outline, which is what a border is drawn with.
    base = mix(base, base * vec3(0.78, 0.86, 0.74), river_bank * river_opacity * 0.50 * (1.0 - lake_water));
    // Land relief must not turn a lake into a mountain ridge.
    NORMAL_MAP = mix(NORMAL_MAP, vec3(0.5, 0.5, 1.0), inland_coverage);
    // The region line, the last one still drawn by asking four neighbours whether they
    // differ - which is one texel wide, on or off, and a staircase. Off the same coverage
    // field as the province and country lines.
    float near_region;
    float other_region_id;
    float region_reach;
    float region_cover = boundary_coverage(region_ids, map_uv, smooth_edges, near_region, other_region_id, region_reach);
    float region_aa = id_aa;
    float region_edge = 1.0 - smoothstep(0.0, border_pixels, region_reach / region_aa);
    region_edge *= step(0.5, abs(near_region - other_region_id));
    // A province line drawn by asking four neighbours whether they differ is one texel wide
    // and either on or off, which is a staircase wearing a border's clothes. The same
    // coverage field gives the distance to the boundary in screen pixels, so the line can be
    // a stroke of constant width with soft shoulders - crisp at any zoom, and gone rather
    // than aliased when a pixel covers more of the map than a line could describe.
    float near_province;
    float other_province_id;
    float province_reach;
    float province_cover = boundary_coverage(province_ids, map_uv, smooth_edges, near_province, other_province_id, province_reach);
    float province_aa = id_aa;
    float province_distance = province_reach / province_aa;
    float province_width = mix(border_pixels, province_border_pixels, province_detail);
    float province_edge = 1.0 - smoothstep(province_width * 0.3, province_width, province_distance);
    province_edge *= step(0.5, abs(near_province - other_province_id));
    float province_halo = (1.0 - smoothstep(province_width, province_width + 1.0, province_distance)) *
        step(0.5, abs(near_province - other_province_id));
    // The selection tint and its gold edge read their share from the coverage fields rather
    // than a hard per-texel id test. The mask drew the outline as a staircase on the texel
    // grid at every zoom a texel was still visible - the ugly jagged border on a picked
    // region. A share of the pixel fades the boundary the way every other line on this map
    // fades.
    float region_selected = (abs(near_region - selected_region_id) < 0.5 ? region_cover
        : abs(other_region_id - selected_region_id) < 0.5 ? 1.0 - region_cover : 0.0)
        * selection_active * political.a;
    // The whole live region, by its provinces, and its outline where membership changes - not the
    // authored region the raster still remembers.
    float near_member = selected_member(near_province);
    float other_member = selected_member(other_province_id);
    float live_selected = ((near_member > 0.5 ? province_cover : 0.0) + (other_member > 0.5 ? 1.0 - province_cover : 0.0))
        * selection_active * political.a;
    float live_edge = (1.0 - smoothstep(0.0, border_pixels, province_reach / province_aa))
        * step(0.5, abs(near_member - other_member));
    region_selected = mix(region_selected, live_selected, step(0.5, selected_region_live));
    region_edge = mix(region_edge, live_edge, step(0.5, selected_region_live));
    float province_selected = (abs(near_province - selected_province_id) < 0.5 ? province_cover
        : abs(other_province_id - selected_province_id) < 0.5 ? 1.0 - province_cover : 0.0)
        * selection_active * political.a;

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
    float nation_edge_pixels = nation_reach / nation_aa;
    float has_nation_border = step(0.5, abs(near_nation - other_nation)) * step(0.5, near_nation);
    float country_edge = 1.0 - smoothstep(border_pixels * 0.85, border_pixels * 2.05, nation_edge_pixels);
    country_edge *= has_nation_border * clamp(2.4 - pixel_texels, 0.0, 1.0);
    // Paradox-style inner frontier cushion: deepens and enriches each country's colour along the
    // inside of its international border and coastline, giving territories physical volume.
    float country_cushion = (1.0 - smoothstep(border_pixels * 1.0, border_pixels * 6.8, nation_edge_pixels)) *
        has_nation_border * clamp(2.8 - pixel_texels * 0.85, 0.0, 1.0) * (1.0 - step(0.5, terrain_mode));
    float country_inner_rim = smoothstep(border_pixels * 0.75, border_pixels * 1.35, nation_edge_pixels) *
        (1.0 - smoothstep(border_pixels * 1.35, border_pixels * 2.35, nation_edge_pixels)) *
        has_nation_border * clamp(2.2 - pixel_texels, 0.0, 1.0) * (1.0 - step(0.5, terrain_mode));
    float region_cushion = (1.0 - smoothstep(border_pixels * 0.5, border_pixels * 2.8, region_reach / region_aa)) *
        step(0.5, abs(near_region - other_region_id)) * (1.0 - has_nation_border) * (1.0 - step(0.5, terrain_mode));
    vec3 front = front_data(province);
    // Use the same reconstructed boundary as the province contour, not a stripe
    // one/two source texels wide. Source texels become giant steps at close zoom.
    vec3 near_front = front_data(near_province);
    vec3 other_front = front_data(other_province_id);
    float front_pair = step(0.002, near_front.r) * step(0.002, other_front.r) *
        (1.0 - step(0.002, abs(near_front.r - other_front.r))) *
        step(0.20, abs(near_front.g - other_front.g)) *
        step(0.5, abs(near_province - other_province_id));
    float front_edge = (1.0 - smoothstep(front_pixels * 0.45, front_pixels + 0.55, province_distance)) * front_pair;
    float front_outer = (1.0 - smoothstep(front_pixels + 0.55, front_pixels + 1.45, province_distance)) * front_pair;
    // The terrain/relief views contain no political front shading.
    front_edge *= 1.0 - step(0.5, terrain_mode);
    front_outer *= 1.0 - step(0.5, terrain_mode);

    // Apply the inner border cushion and subtle regional border shading before crisp strokes.
    // Two-stage Paradox-grade frontier gradient: a wide velvet vignette (0..8.5px) that gives
    // territories physical volume, plus a rich saturated inner rim (0.9..2.6px) right inside the
    // dark cartographic ink line.
    float country_wide_glow = (1.0 - smoothstep(border_pixels * 1.2, border_pixels * 8.5, nation_edge_pixels)) *
        has_nation_border * clamp(2.9 - pixel_texels * 0.80, 0.0, 1.0) * (1.0 - step(0.5, terrain_mode));
    vec3 saturated_nation = clamp(mix(vec3(dot(political.rgb, vec3(0.2126, 0.7152, 0.0722))), political.rgb, 1.32) * 0.84, vec3(0.08), vec3(0.95));
    vec3 cushioned = mix(base * 0.74, base * 0.52 + saturated_nation * 0.38, 0.55);
    base = mix(base, cushioned, country_cushion * max(detail_mix, 0.48) * political.a * 0.58);
    base = mix(base, mix(base, saturated_nation, 0.38), country_wide_glow * max(detail_mix, 0.38) * political.a * 0.34);
    base = mix(base, base * 0.87, region_cushion * detail_mix * political.a * 0.32);
    base = mix(base, clamp(base * 1.18 + vec3(0.055, 0.048, 0.032), vec3(0.0), vec3(1.0)),
        country_inner_rim * detail_mix * political.a * 0.40);

    // Zoom-gated, screen-space strokes: readable through relief at tactical distance,
    // absent on the world map. A restrained light shoulder also works on dark terrain.
    base = mix(base, vec3(0.78, 0.80, 0.77), province_halo * province_detail * political.a * province_border_halo);
    base = mix(base, vec3(0.055, 0.075, 0.09), province_edge * province_detail * political.a * province_border_opacity);
    // The country border, drawn rather than baked. It replaces the one-texel darkening the
    // political raster used to carry, which was the most visible pixel grid on the map.
    // Full weight on the world map too: at 0.35 the frontier between two states was a hairline
    // under the province grid, and the strategic view lost the one line it is for.
    base = mix(base, vec3(0.035, 0.05, 0.075), country_edge * max(detail_mix, 0.62) * political.a * 0.86);
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
    // Reuse the reconstructed province boundary; no neighbour raster pass.
    vec4 near_control = control_color(near_province);
    vec4 other_control = control_color(other_province_id);
    float near_occupied = occupation_mask(near_control);
    float other_occupied = occupation_mask(other_control);
    float control_change = 0.0;
    if (near_occupied * other_occupied > 0.5)
        control_change = step(0.5, abs(occupation_controller(near_province) - occupation_controller(other_province_id)));
    float occupation_pair = max(abs(near_occupied - other_occupied), control_change * max(near_occupied, other_occupied));
    float occupation_edge = occupation_pair * step(0.5, abs(near_province - other_province_id)) * political.a *
        (1.0 - step(0.5, terrain_mode)) * (1.0 - resource_mode) * (1.0 - map_mode);
    float occupation_key = 1.0 - smoothstep(1.1, 2.5, province_distance);
    float occupation_stroke = 1.0 - smoothstep(0.35, 1.1, province_distance);
    base = mix(base, vec3(0.045, 0.065, 0.08), occupation_edge * occupation_key * 0.90);
    base = mix(base, vec3(0.92, 0.88, 0.71), occupation_edge * occupation_stroke * 0.94);
    float lease_edge = abs(lease_mask(near_control) - lease_mask(other_control)) *
        step(0.5, abs(near_province - other_province_id)) * political.a *
        (1.0 - step(0.5, terrain_mode)) * (1.0 - resource_mode) * (1.0 - map_mode);
    float lease_dash = step(0.45, fract((UV.x * 2.0 - UV.y) * mix(90.0, 520.0, province_detail)));
    vec3 tenant = max(near_control.a, other_control.a) > 0.0
        ? (lease_mask(near_control) > 0.5 ? near_control.rgb : other_control.rgb) : vec3(0.9);
    base = mix(base, tenant * 1.08, lease_edge * occupation_stroke * lease_dash * 0.95);
    // A live hostile front takes precedence over the pale occupation contour.
    // The same red stroke remains on unoccupied enemy borders; rear occupation
    // boundaries stay pale rather than pretending to be an active front.
    base = mix(base, vec3(0.24, 0.03, 0.025), front_outer * 0.78);
    base = mix(base, vec3(1.0, 0.26, 0.14), front_edge * 0.98);
    // One narrow cartographic stroke on the shared boundary, never a filled province.
    base = mix(base, vec3(0.30, 0.72, 0.96), border_plan * 0.95);
    base = mix(base, vec3(0.95, 0.76, 0.25), region_selected * 0.12);
    base = mix(base, vec3(1.0, 0.84, 0.30), province_selected * province_detail * 0.18);
    float selection_edge = max(region_edge * region_selected, province_edge * province_selected * province_detail);
    // Excavation exposes earth in its exact footprint, never the space behind the globe.
    // The guest renders its real native hole separately; its atlas bake disables this mask.
    base = mix(base, vec3(0.20, 0.13, 0.09), clamp(excavation, 0.0, 1.0));
    vec2 fog_lookup = (vec2(mod(province, 256.0), floor(province / 256.0)) + 0.5) / 256.0;
    float known = texture(fog_visibility, fog_lookup).r;
    float fog = step(0.0, fog_viewer) * (1.0 - step(0.5, known)) * political.a * fog_tint * (1.0 - resource_mode);
    base = mix(base, mix(vec3(dot(base, vec3(0.2126, 0.7152, 0.0722))), base, fog_saturation) * fog_darkness, fog);
    float front_pulse = 0.85 + 0.15 * sin(TIME * 3.0);
    float front_march = 0.78 + 0.22 * sin((UV.x * 420.0 + UV.y * 280.0) - TIME * 4.2);
    ALBEDO = mix(base, vec3(1.0, 0.86, 0.34), selection_edge * 0.94);
    EMISSION = mix(water * 0.025, vec3(0.32, 0.21, 0.035), selection_edge * 0.56) +
        vec3(0.18, 0.14, 0.04) * highlight_edge * 0.38 +
        plan_color * plan_on * 0.16 +
        vec3(0.095, 0.024, 0.006) * front_edge * front_pulse * front_march +
        inland_water * (lake_water * 0.035 + river_water * 0.025);
    // Sea glint as a soft sheen rather than a white disc: the open Pacific read as fog at 0.48/0.36.
    ROUGHNESS = mix(0.56, mix(0.84, 0.40, inland_coverage), political.a);
    METALLIC = 0.0;
    SPECULAR = mix(0.26, mix(0.12, 0.40, inland_coverage), political.a);
    // A flat map is a map: under the orthographic camera the sun's glint on the water landed as
    // a white haze across whole seas (the Sea of Japan, the open Pacific). The globe keeps it.
    SPECULAR *= 1.0 - step(0.5, PROJECTION_MATRIX[3][3]);
}"
        };
        if (ReducedMap) shader.Code = CompatibilityMapShader.Source;
        else
        {
            shader.Code = MapColorSpaceShader.Build(shader.Code);
            if (RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility")
                shader.Code = PackedMapShader.Build(shader.Code);
        }
        _globeMaterial = new ShaderMaterial { Shader = shader };
        InitializePackedTextures();
        ApplyMapVisualStyle();
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
        var dryWater = Image.CreateEmpty(2, 2, false, Image.Format.Rgb8);
        dryWater.Fill(Colors.White);
        _globeMaterial.SetShaderParameter("hydrology_map", ImageTexture.CreateFromImage(dryWater));
        if (_packedLookups is null)
        {
        _globeMaterial.SetShaderParameter("province_owner_colors", placeholder);
        _globeMaterial.SetShaderParameter("province_owner_ids",
            ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8)));
        _globeMaterial.SetShaderParameter("province_control_colors",
            ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8)));
        _globeMaterial.SetShaderParameter("front_membership",
            ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rg8)));
        _globeMaterial.SetShaderParameter("map_mode_colors",
            ImageTexture.CreateFromImage(Image.CreateEmpty(256, 256, false, Image.Format.Rgba8)));
        _globeMaterial.SetShaderParameter("resource_colors", placeholder);
        }
        mesh.Material = _globeMaterial;

        _globeNode = new MeshInstance3D { Name = "PoliticalGlobe", Mesh = mesh, Layers = AtlasSurfaceLayers };
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

    /// <summary>
    /// Carry a released drag on and let it die away. While the hand still holds the map the
    /// glide waits; if the hand has rested more than a moment, there is nothing left to carry.
    /// </summary>
    private void UpdateFlatGlide(float delta)
    {
        if (!_cameraInertia) { _flatGlide = Vector2.Zero; return; }
        if (_dragging)
        {
            if (Time.GetTicksUsec() - _lastFlatDragUsec > 90_000) _flatGlide = Vector2.Zero;
            return;
        }
        if (_flatGlide.LengthSquared() < _flatZoom * _flatZoom * 1e-6f) { _flatGlide = Vector2.Zero; return; }
        _flatZoomAnchorActive = false;
        _targetFlatX += CameraInertia.Travel(_flatGlide.X, delta);
        _targetFlatY += CameraInertia.Travel(_flatGlide.Y, delta);
        ClampFlatCamera();
        _flatX = _targetFlatX;
        _flatY = _targetFlatY;
        _flatGlide *= Mathf.Exp(-CameraInertia.Damping * Math.Clamp(delta, 0, .1f));
    }

    private void UpdateOrbitGlide(float delta)
    {
        if (!_cameraInertia) { _orbitGlide = Vector2.Zero; return; }
        if (_dragging)
        {
            if (Time.GetTicksUsec() - _lastFlatDragUsec > 90_000) _orbitGlide = Vector2.Zero;
            return;
        }
        if (_orbitGlide.LengthSquared() < 1e-9f) { _orbitGlide = Vector2.Zero; return; }
        _targetYaw += CameraInertia.Travel(_orbitGlide.X, delta);
        _targetPitch = Mathf.Clamp(_targetPitch + CameraInertia.Travel(_orbitGlide.Y, delta), -MaxPitch, MaxPitch);
        _yaw = _targetYaw; _pitch = _targetPitch;
        _orbitGlide *= Mathf.Exp(-CameraInertia.Damping * Math.Clamp(delta, 0, .1f));
    }

    /// <summary>The ordinary camera: an orbit rig the player turns, tilts and zooms.</summary>
    private void UpdateOrbitCamera(double delta)
    {
        if (_showcasePan)
        {
            // A complete, level orbit takes roughly 90 seconds in either projection.
            // This moves the camera's target, never the world's geographic coordinates;
            // country labels, unit counters and borders continue to agree with the map.
            const float radiansPerSecond = 0.07f;
            if (_flatMap) _targetFlatX += FlatMapWidth * radiansPerSecond / Mathf.Tau * (float)delta;
            else _targetYaw += radiansPerSecond * (float)delta;
        }
        var weight = 1f - Mathf.Pow(0.0008f, (float)delta);
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, weight);
        _pitch = Mathf.Lerp(_pitch, _targetPitch, weight);
        _distance = Mathf.Lerp(_distance, _targetDistance, weight);
        _flatX = Mathf.Lerp(_flatX, _targetFlatX, weight);
        _flatY = Mathf.Lerp(_flatY, _targetFlatY, weight);
        _flatZoom = Mathf.Lerp(_flatZoom, _targetFlatZoom, weight);
        UpdateFlatZoomAnchor();
        if (_flatMap) UpdateFlatGlide((float)delta);
        else UpdateOrbitGlide((float)delta);

        var typing = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        if (_flatMap && !typing)
        {
            if (DealOrForceGame.Held(Key.W) || DealOrForceGame.Held(Key.S) ||
                DealOrForceGame.Held(Key.A) || DealOrForceGame.Held(Key.D)) _flatZoomAnchorActive = false;
            var flatSpeed = _flatZoom * 0.62f * (float)delta;
            if (DealOrForceGame.Held(Key.W)) _targetFlatY += flatSpeed;
            if (DealOrForceGame.Held(Key.S)) _targetFlatY -= flatSpeed;
            if (DealOrForceGame.Held(Key.A)) _targetFlatX -= flatSpeed;
            if (DealOrForceGame.Held(Key.D)) _targetFlatX += flatSpeed;
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
            if (DealOrForceGame.Held(Key.W)) _targetPitch = Mathf.Clamp(_targetPitch - (float)delta * 0.7f, -MaxPitch, MaxPitch);
            if (DealOrForceGame.Held(Key.S)) _targetPitch = Mathf.Clamp(_targetPitch + (float)delta * 0.7f, -MaxPitch, MaxPitch);
            if (DealOrForceGame.Held(Key.A)) _targetYaw -= (float)delta * 0.7f;
            if (DealOrForceGame.Held(Key.D)) _targetYaw += (float)delta * 0.7f;
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
            _targetPitch = Mathf.Clamp(Mathf.Asin(direction.Y), -MaxPitch, MaxPitch);
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
        if (DealOrForceGame.Held(Key.Q)) _freeRoll -= 1.1f * step * fast * slow;
        if (DealOrForceGame.Held(Key.E)) _freeRoll += 1.1f * step * fast * slow;
        if (DealOrForceGame.Held(Key.R)) _freeRoll = Mathf.MoveToward(_freeRoll, 0f, 2.2f * step);

        // A lens rather than a throttle: moving closer and zooming in look nothing alike,
        // and only one of them can be done from orbit.
        if (DealOrForceGame.Held(Key.Z)) _freeFov = Mathf.Clamp(_freeFov - 26f * step, 12f, 110f);
        if (DealOrForceGame.Held(Key.X)) _freeFov = Mathf.Clamp(_freeFov + 26f * step, 12f, 110f);
        if (DealOrForceGame.Held(Key.F)) _freeFov = Mathf.MoveToward(_freeFov, RigFieldOfView, 60f * step);

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
        if (DealOrForceGame.Held(Key.W)) move += forward;
        if (DealOrForceGame.Held(Key.S)) move -= forward;
        if (DealOrForceGame.Held(Key.D)) move += right;
        if (DealOrForceGame.Held(Key.A)) move -= right;
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
        if (DealOrForceGame.Held(Key.A)) _freeOrbitYaw -= turn;
        if (DealOrForceGame.Held(Key.D)) _freeOrbitYaw += turn;
        if (Input.IsKeyPressed(Key.Space)) _freeOrbitPitch += turn;
        if (Input.IsKeyPressed(Key.Ctrl)) _freeOrbitPitch -= turn;
        _freeOrbitPitch = Mathf.Clamp(_freeOrbitPitch, -1.45f, 1.45f);

        var closing = _freeOrbitRadius * 0.9f * step * scale;
        if (DealOrForceGame.Held(Key.W)) _freeOrbitRadius -= closing;
        if (DealOrForceGame.Held(Key.S)) _freeOrbitRadius += closing;
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
        bool withinLod,
        float horizon = 0.18f)
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
            {
                var visible = marker.Normal.Dot(viewNormal) > horizon && !_coveredCounters.Contains(marker.Node);
                if (marker.Node.Visible != visible) marker.Node.Visible = visible;
            }
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
    private readonly HashSet<int> _shownNations = new();

    /// <summary>
    /// Whose divisions the map draws (setting "map_divisions"). By default the player's own,
    /// their allies' and dependencies', and everybody who is at war: the garrisons of two
    /// hundred countries at peace buried Europe in counters, and none of them was anything a
    /// player had to act on. A country the player has selected is drawn too, so looking at a
    /// neighbour's army is one click.
    /// </summary>
    private void RefreshShownNations(Simulation.Campaign campaign)
    {
        _shownNations.Clear();
        var mode = Simulation.SettingsCatalog.Chosen("map_divisions");
        var player = campaign.Player;
        foreach (var nation in campaign.Nations)
        {
            if (mode == "all") { _shownNations.Add(nation.Id); continue; }
            if (player is not null && nation.Id == player.Id) { _shownNations.Add(nation.Id); continue; }
            if (mode == "ours") continue;
            if (player is not null && (nation.AnswersTo(player.Id) || player.AnswersTo(nation.Id) ||
                    Simulation.CampaignDiplomacy.Peek(campaign, player.Id, nation.Id) is { Stance: Simulation.DiplomaticStance.Allied }))
                _shownNations.Add(nation.Id);
            if (nation.Id == ShownCountryId) _shownNations.Add(nation.Id);
        }
        if (mode == "all" || mode == "ours") return;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            foreach (var id in war.Side(true)) _shownNations.Add(id);
            foreach (var id in war.Side(false)) _shownNations.Add(id);
        }
    }

    /// <summary>The country the interface is looking at, whose army is drawn whatever the setting says.</summary>
    public int ShownCountryId { get; set; } = -1;

    private readonly Dictionary<int, Vector3> _previousArmyPositions = new();

    private void ReconcileArmyMarkers(Simulation.Campaign campaign)
    {
        if (!MilitaryMarkersEnabled || !IsInstanceValid(_armyRoot) || _divisionTexture is null) return;

        _previousArmyPositions.Clear();
        foreach (var m in _armyStacks.Values)
            if (m.Stack.Count > 0 && m.Node is not null && IsInstanceValid(m.Node))
            {
                for (var i = 0; i < m.Stack.Count; i++)
                    _previousArmyPositions[m.Stack[i].Id] = m.DisplayPosition;
            }

        foreach (var marker in _armyStacks.Values)
        {
            marker.Seen = false;
            marker.Stack.Clear();
        }

        RefreshShownNations(campaign);
        foreach (var army in campaign.Armies)
        {
            var bucket = ArmyPresentationBucket(campaign, army);
            if (!CanShowArmy(army) || !_shownNations.Contains(army.NationId)) continue;
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
            if (marker.Node is not null && IsInstanceValid(marker.Node))
            {
                // Detach now so a replacement with the same lead ID does not
                // inherit an anonymous name while the old node awaits deletion.
                marker.Node.GetParent()?.RemoveChild(marker.Node);
                marker.Node.QueueFree();
            }
            _armyStacks.Remove(bucket);
        }
        if (_vanishedStacks.Count > 0) InvalidateFlatCounterDensity();

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
            {
                var initialLoc = ArmyPresentationLocation(marker.Stack);
                marker.Normal = SurfacePoint(initialLoc.Latitude, initialLoc.Longitude, 1f);
                CreateArmyStackMarker(marker, nation, lead, templateId);
                SizeArmyCounter(marker);
                if (_previousArmyPositions.TryGetValue(lead.Id, out var prevPos) && prevPos != Vector3.Zero)
                {
                    marker.DisplayPosition = prevPos;
                    // This is the same formation crossing into a new province bucket. Start
                    // the ease from the pixel it already occupied instead of snapping it to
                    // the new province centre. A genuinely new formation has no previous
                    // position and still appears at its target immediately.
                    marker.MotionSettled = true;
                    marker.Node.Position = prevPos;
                }
                InvalidateFlatCounterDensity();
            }
            else if (marker.NationId != nation.Id ||
                     marker.FlagVisualId != NationalFlagVisualId(nation) ||
                     !string.Equals(marker.TemplateId, templateId, StringComparison.Ordinal))
            {
                marker.FullPlateTexture = ArmyCounterTexture(nation, templateId);
                SizeArmyCounter(marker);
                marker.NationId = nation.Id;
                marker.TemplateId = templateId;
            }
            marker.FlagVisualId = NationalFlagVisualId(nation);
            if (marker.Node is null) continue;

            var location = ArmyPresentationLocation(marker.Stack);
            var position = ArmyMarkerPosition(marker);
            // The target is written here; the drawn node eases toward it in
            // UpdateArmyMarkerMotion. Writing the node directly is what made a division
            // crossing a border teleport one province in a single frame.
            if (marker.TargetPosition != position || !marker.MotionSettled)
            {
                marker.TargetPosition = position;
                if (!marker.MotionSettled) InvalidateFlatCounterDensity();
            }
            marker.Normal = SurfacePoint(location.Latitude, location.Longitude, 1f);
            OrientSurfaceArmyCard(marker);
            if (!_flatMap) SizeSurfaceArmyCounter(marker);
            // Diagnostic geography is independent of the rendered position.
            if (_campaign.ProvinceMap is { } topology && lead.ProvinceId > 0 &&
                lead.ProvinceId <= topology.ProvinceCount)
            {
                var center = topology.CenterOf(lead.ProvinceId);
                marker.Node.SetMeta("province_center_latlon", new Vector2(center.Latitude, center.Longitude));
            }

            var membership = new HashCode();
            foreach (var army in marker.Stack) membership.Add(army.Id);
            var signature = membership.ToHashCode();
            if (signature != marker.ActualFingerprint)
            {
                marker.ActualFingerprint = signature;
                var ids = new Godot.Collections.Array<int>();
                foreach (var army in marker.Stack) ids.Add(army.Id);
                marker.Node.SetMeta("actual_army_ids", ids);
                marker.Node.SetMeta("province_id", lead.ProvinceId);
                InvalidateFlatCounterDensity();
            }

            var divisions = 0f;
            var personnel = 0f;
            foreach (var army in marker.Stack)
            {
                divisions += army.Divisions;
                personnel += DivisionPersonnel(army);
            }
            var divCount = Mathf.CeilToInt(divisions);
            var menTenths = (int)MathF.Round(personnel / 100f);
            if (marker.ShownDivisions != divCount || marker.ShownMenTenths != menTenths)
            {
                var divChanged = marker.ShownDivisions != divCount;
                marker.ShownDivisions = divCount;
                marker.ShownMenTenths = menTenths;
                var countText = $"{divCount}\n{menTenths / 10f:0.#}k";
                if (!string.Equals(marker.CountText, countText, StringComparison.Ordinal))
                {
                    if (!marker.ShownCompact && (!_flatMap || marker.DisplayProvinces <= 1)) marker.Count.Text = countText;
                    marker.CountText = countText;
                    if (divChanged) InvalidateFlatCounterDensity();
                }
            }

            _armyMarkers.Add((marker.Node, marker.Normal));
            _armyByMarker[marker.Node] = marker.Stack;
        }

        PruneArmyCounterTextureCaches();
        CoverSharedProvinces(campaign);

        _armyStructureFingerprint = ArmyStructureFingerprint(campaign);
    }

    private readonly HashSet<Node3D> _coveredCounters = new();
    private readonly Dictionary<int, ArmyStackMarker> _provinceLeader = new();

    /// <summary>
    /// One counter per province on the globe, the way Hearts of Iron draws it.
    ///
    /// Counters are bucketed by country and province, so two countries in one province - allies
    /// side by side, a garrison and a rising - made two counters on the same point. Both are
    /// billboards drawn without a depth test, and which one came out on top changed from frame
    /// to frame: two flags flickering through each other. Now the country with the most
    /// divisions there is drawn and the others stand behind it, hidden; the province, the
    /// roster and a box selection still reach every division. The flat map fans its counters
    /// out instead and is left as it is.
    /// </summary>
    private void CoverSharedProvinces(Simulation.Campaign campaign)
    {
        _coveredCounters.Clear();
        _provinceLeader.Clear();
        var playerId = campaign.Player?.Id ?? -1;
        foreach (var (bucket, marker) in _armyStacks)
        {
            if (bucket.Item2 <= 0 || marker.Node is null || marker.Stack.Count == 0) continue;
            if (!_provinceLeader.TryGetValue(bucket.Item2, out var leader))
            {
                _provinceLeader[bucket.Item2] = marker;
                continue;
            }
            var mine = Strength(marker);
            var theirs = Strength(leader);
            var wins = mine > theirs + 0.01f ||
                       (MathF.Abs(mine - theirs) <= 0.01f &&
                        (marker.Stack[0].NationId == playerId ||
                         (leader.Stack[0].NationId != playerId && marker.Stack[0].NationId < leader.Stack[0].NationId)));
            if (wins)
            {
                _coveredCounters.Add(leader.Node);
                _provinceLeader[bucket.Item2] = marker;
            }
            else _coveredCounters.Add(marker.Node);
        }
        if (_flatMap) _coveredCounters.Clear();
        foreach (var node in _coveredCounters)
            if (IsInstanceValid(node) && node.Visible) node.Visible = false;

        static float Strength(ArmyStackMarker stack)
        {
            var divisions = 0f;
            foreach (var army in stack.Stack) divisions += army.Divisions;
            return divisions;
        }
    }

    private void CreateArmyStackMarker(ArmyStackMarker marker, Simulation.Nation nation,
        Simulation.Army lead, string templateId)
    {

        {
            var initialPos = ArmyMarkerPosition(marker);
            var node = new Node3D
            {
                Name = $"DivisionStack_{nation.Tag}_{lead.ProvinceId}_{lead.Id}",
                Position = initialPos,
                Scale = _flatMap
                    ? Vector3.One * _counterWorldPixel * _counterFlatFar
                    : Vector3.One * SurfaceCounterWidth(null)
            };
            _armyRoot.AddChild(node);
            marker.Node = node;
            marker.TargetPosition = initialPos;
            marker.DisplayPosition = initialPos;
            marker.NationId = nation.Id;
            marker.TemplateId = templateId;

            var plate = new Sprite3D
            {
                Texture = ArmyCounterTexture(nation, templateId),
                PixelSize = CounterPlatePixelSize * _counterScale,
                FixedSize = false,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = Colors.White
            };
            node.AddChild(plate);
            marker.Plate = plate;
            marker.FullPlateTexture = plate.Texture;
            marker.Glow = AttachSelectionGlow(plate, 176, 104, 0);
            marker.ConditionBars = new Sprite3D
            {
                Name = "OrganisationEquipmentBars", Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                RenderPriority = 10,
                NoDepthTest = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest
            };
            // A sibling of the plate: status colours must not inherit its red battle tint.
            node.AddChild(marker.ConditionBars);

            var divisions = new Label3D
            {
                Name = "DivisionCount",
                Text = "",
                FontSize = 21,
                PixelSize = CounterTextPixelSize * _counterScale,
                LineSpacing = -5f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                FixedSize = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                OutlineSize = Mathf.RoundToInt(CounterOutlineSize * _counterScale),
                Modulate = Ui.Colour("military.ready"),
                OutlineModulate = Ui.Colour("globe.label_outline"),
                Position = Vector3.Zero,
                RenderPriority = 20,
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
    private (int NationId, int LocationId, int FallbackLatitude, int FallbackLongitude)
        ArmyPresentationBucket(Simulation.Campaign campaign, Simulation.Army division)
    {
        // Province membership never depends on zoom, projection, visibility or
        // selection. A close view must not fan one province out into dozens of
        // displaced cards. Individual divisions remain accessible in the roster.
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
        // Rebuilding the counters is a full pass over every army: dictionaries, template
        // votes, strings, node transforms. At eight passes a second it was measurable even
        // with the world paused and nothing moving. The fingerprint answers the only
        // question that matters here - did any counter's identity, place or size change -
        // and the expensive pass is skipped when it did not.
        if (ArmyStructureFingerprint(_campaign) == _armyStructureFingerprint) return;
        ReconcileArmyMarkers(_campaign);
    }

    private readonly HashSet<int> _liveArmyIds = new();

    private int ArmyStructureFingerprint(Simulation.Campaign campaign)
    {
        var hash = new HashCode();
        hash.Add(FlagCatalog.VisualStateHash(campaign));
        hash.Add(SettingsCatalog.Flag("movement_flags"));
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
            // The counter's text is divisions over men; reinforcement changes it without a
            // march, and a fingerprint that ignored it would freeze the label.
            hash.Add(army.Divisions);
            hash.Add(DivisionPersonnel(army));
        }
        return hash.ToHashCode();
    }

    private void BuildFleetMarkers(Simulation.Campaign campaign)
    {
        if (!IsInstanceValid(_fleetRoot) || _fleetTexture is null) return;
        foreach (var child in _fleetRoot.GetChildren()) { _fleetRoot.RemoveChild(child); child.QueueFree(); }
        _fleetMarkers.Clear();
        _fleetByMarker.Clear();
        _fleetNodes.Clear();
        _flatFleetOffsets.Clear();
        InvalidateFlatCounterDensity();
        if (!campaign.FeatureEnabled("navy")) return;
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
                PixelSize = FleetPlatePixelSize * _counterScale,
                FixedSize = false,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = flag.Lerp(Colors.White, 0.22f) with { A = 0.94f }
            };
            marker.AddChild(plate);
            var fleetGlow = AttachSelectionGlow(plate, 160, 96, 3);
            fleetGlow.Visible = FleetChosen(fleet.Id);
            var strength = new Label3D
            {
                Name = "FleetHullCount",
                Text = fleet.Hulls.ToString(), FontSize = 24, PixelSize = FleetTextPixelSize * _counterScale,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled, FixedSize = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                OutlineSize = 7, Modulate = Ui.Colour("globe.label_province"), OutlineModulate = Ui.Colour("globe.label_province_outline"),
                Position = Vector3.Zero, NoDepthTest = true, RenderPriority = 20
            };
            marker.AddChild(strength);
            _fleetMarkers.Add((marker, marker.Position.Normalized()));
            _fleetByMarker[marker] = fleet;
            _fleetNodes[fleet.Id] = (marker, strength, plate);
            SizeFleetCounter((marker, strength, plate));
        }
    }

    /// <summary>
    /// Fleets move now, so their counters have to follow them. Rebuilding the markers every
    /// frame would be absurd for something that moves three degrees a day, so the nodes are
    /// kept and only their positions are written - and the roster is rebuilt only when the
    /// set of fleets actually changes.
    /// </summary>
    private Transform3D _lastScaleCamera;
    private int _lastScaleArmyCount = -1;
    private int _lastScaleFleetCount = -1;
    private int _fleetUpdateStateKey = int.MinValue;

    private void UpdateFleetMarkers()
    {
        if (_campaign is null || !IsInstanceValid(_fleetRoot)) return;

        var fingerprint = HashCode.Combine(_campaign.Fleets.Count, FogOfWarActive, _campaign.Player?.Id, _campaign.Player?.IsHuman, _campaign.FeatureEnabled("navy"));
        foreach (var fleet in _campaign.Fleets) fingerprint = HashCode.Combine(fingerprint, fleet.Id, fleet.NationId);
        if (fingerprint != _fleetRosterFingerprint)
        {
            _fleetRosterFingerprint = fingerprint;
            _fleetUpdateStateKey = int.MinValue;
            BuildFleetMarkers(_campaign);
        }
        var stateKey = HashCode.Combine(fingerprint, _campaign.ElapsedHours, _selectedFleetId, _fleetSelectionStamp, _flatMap);
        if (stateKey == _fleetUpdateStateKey) return;
        _fleetUpdateStateKey = stateKey;

        for (var index = 0; index < _fleetMarkers.Count; index++)
        {
            var (node, previousNormal) = _fleetMarkers[index];
            if (!IsInstanceValid(node) || !_fleetByMarker.TryGetValue(node, out var fleet)) continue;
            var normal = SurfacePoint(fleet.Latitude, fleet.Longitude, 1f);
            var point = _flatMap ? FlatFleetPosition(fleet.Id, fleet.Latitude, fleet.Longitude)
                : PresentationPoint(fleet.Latitude, fleet.Longitude, SurfaceMarkerRadius);
            if (node.Position != point)
            {
                node.Position = point;
                OrientSurfaceCounter(node, normal);
            }
            _fleetMarkers[index] = (node, normal);
            if (_flatMap && previousNormal.DistanceSquaredTo(normal) > 0.00000001f) InvalidateFlatCounterDensity();
            if (!_fleetNodes.TryGetValue(fleet.Id, out var parts)) continue;
            if (IsInstanceValid(parts.Plate) && parts.Plate.GetChildOrNull<Sprite3D>(0) is { } fleetGlow)
            {
                var chosen = FleetChosen(fleet.Id);
                if (fleetGlow.Visible != chosen) fleetGlow.Visible = chosen;
                // Selection changes the frame, never the geometry or click footprint.
                var lift = Vector3.One;
                if (parts.Plate.Scale != lift) parts.Plate.Scale = lift;
                if (IsInstanceValid(parts.Count) && parts.Count.Scale != lift) parts.Count.Scale = lift;
            }
            if (IsInstanceValid(parts.Count))
            {
                var hulls = fleet.Hulls;
                if (parts.Count.Text.Length == 0 || !int.TryParse(parts.Count.Text, out var shownHulls) || shownHulls != hulls)
                    parts.Count.Text = hulls.ToString();
                var tint = fleet.InAction
                    ? Ui.Colour("military.battle")
                    : FleetChosen(fleet.Id)
                        ? Ui.Colour("globe.selection")
                        : Ui.Colour("globe.label_province");
                if (parts.Count.Modulate != tint) parts.Count.Modulate = tint;
            }
        }
    }

    private bool TryPickFleet(Vector2 pixel, out Simulation.Fleet fleet)
    {
        fleet = null;
        if ((!_flatMap && ViewDistance > 13.5f) || !IsInstanceValid(_camera)) return false;
        var bestDistance = float.MaxValue;
        foreach (var (node, candidate) in _fleetByMarker)
        {
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (!_fleetNodes.TryGetValue(candidate.Id, out var parts) ||
                !TryHitSurfaceCounter(parts.Plate, pixel, out var distance)) continue;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            fleet = candidate;
        }
        return fleet is not null;
    }

    public void SelectFleet(int fleetId)
    {
        _selectedFleetId = fleetId;
        _selectedFleetIds.Clear();
        _fleetSelectionStamp++;
    }

    /// <summary>Several fleets at once: the first is the one the panel describes.</summary>
    public void SelectFleets(IEnumerable<int> fleetIds)
    {
        _selectedFleetIds.Clear();
        foreach (var id in fleetIds) _selectedFleetIds.Add(id);
        _selectedFleetId = _selectedFleetIds.Count > 0 ? _selectedFleetIds.Min() : -1;
        _fleetSelectionStamp++;
    }

    private readonly HashSet<int> _selectedFleetIds = new();
    private int _fleetSelectionStamp;

    private bool FleetChosen(int fleetId) => fleetId == _selectedFleetId || _selectedFleetIds.Contains(fleetId);

    /// <summary>The player's own fleets whose counters lie inside a screen rectangle.</summary>
    private List<Simulation.Fleet> FleetsInScreenRect(Rect2 rect)
    {
        var found = new List<Simulation.Fleet>();
        if (!IsInstanceValid(_camera) || (!_flatMap && ViewDistance > 13.5f) || _campaign?.Player is null) return found;
        foreach (var (node, candidate) in _fleetByMarker)
        {
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (candidate.NationId != _campaign.Player.Id) continue;
            if (rect.HasPoint(_camera.UnprojectPosition(node.GlobalPosition))) found.Add(candidate);
        }
        return found;
    }

    private static Texture2D CreateFleetMarkerTexture()
    {
        const int width = 160;
        const int height = 96;
        const int scale = 3;
        const int w = width * scale;
        const int h = height * scale;
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var outerDark = new Color(0.03f, 0.06f, 0.09f, 0.98f);
        var frameTeal = new Color(0.24f, 0.46f, 0.56f, 0.98f);
        var brassRim = new Color(0.84f, 0.72f, 0.42f, 0.96f);
        var bodyTop = new Color(0.09f, 0.17f, 0.24f, 0.96f);
        var bodyBot = new Color(0.05f, 0.10f, 0.15f, 0.96f);
        var bayFill = new Color(0.03f, 0.07f, 0.11f, 0.96f);
        var shipLight = new Color(0.86f, 0.93f, 0.97f, 1f);
        var shipMid = new Color(0.58f, 0.74f, 0.82f, 1f);
        var wakeCol = new Color(0.38f, 0.78f, 0.90f, 0.92f);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var ux = x / (float)scale;
            var uy = y / (float)scale;
            if (ux < 3f || ux >= width - 3f || uy < 3f || uy >= height - 3f) continue;

            // Rounded corner distance check
            var cx = ux < 11f ? 11f - ux : ux > width - 12f ? ux - (width - 12f) : 0f;
            var cy = uy < 11f ? 11f - uy : uy > height - 12f ? uy - (height - 12f) : 0f;
            var cDist = MathF.Sqrt(cx * cx + cy * cy);
            if (cDist > 8f) continue;

            var t = (uy - 3f) / (height - 6f);
            Color col;
            if (cDist > 6.2f || ux < 5f || ux >= width - 5f || uy < 5f || uy >= height - 5f)
                col = outerDark;
            else if (cDist > 4.2f || ux < 8f || ux >= width - 8f || uy < 8f || uy >= height - 8f)
                col = frameTeal.Lerp(Colors.White, (1f - t) * 0.22f);
            else if (ux < 10f || ux >= width - 10f || uy < 10f || uy >= height - 10f)
                col = brassRim.Darkened(t * 0.25f);
            else
            {
                col = bodyTop.Lerp(bodyBot, t);
                // Top naval header band
                if (uy < 22f)
                    col = col.Lightened(0.14f);
                else if (uy >= 22f && uy < 23.5f)
                    col = brassRim.Darkened(0.18f);

                // Center recessed hull-count bay (leaves middle clear for FleetHullCount text)
                if (ux >= 46f && ux <= 114f && uy >= 26f && uy <= 62f)
                {
                    var bBorder = ux < 47.5f || ux > 112.5f || uy < 27.5f || uy > 60.5f;
                    col = bBorder ? brassRim.Darkened(0.35f) : bayFill;
                }

                // Left & right maritime compass medallions
                foreach (var medX in new[] { 27f, 133f })
                {
                    var d = MathF.Sqrt((ux - medX) * (ux - medX) + (uy - 44f) * (uy - 44f));
                    if (d <= 11.5f)
                    {
                        if (d >= 9.8f) col = brassRim;
                        else if (d <= 3.2f) col = brassRim.Lightened(0.15f);
                        else col = bayFill.Lightened(0.06f);
                    }
                }
            }

            // Sleek modern destroyer silhouette along the lower deck (uy 64..84)
            // Hull with flared bow on the right and tapered stern on the left
            if (uy >= 73f && uy <= 82f)
            {
                var hullLeft = 26f + (uy - 73f) * 0.7f;
                var hullRight = 136f - (uy - 73f) * 1.4f;
                if (ux >= hullLeft && ux <= hullRight)
                    col = uy < 76f ? shipLight : shipMid;
            }
            // Main stealth superstructure & bridge
            if (uy >= 66f && uy < 73f && ux >= 54f + (73f - uy) * 0.8f && ux <= 102f - (73f - uy) * 0.6f)
                col = shipLight;
            // Forward radar mast & aft mast
            if (uy >= 60f && uy < 66f && ((ux >= 74f && ux <= 82f) || (ux >= 58f && ux <= 63f)))
                col = shipMid;
            // Forward naval gun turret
            if (uy >= 69f && uy < 73f && ux >= 108f && ux <= 118f)
                col = shipLight;
            if (uy >= 70f && uy < 71.5f && ux > 118f && ux <= 126f)
                col = shipLight;
            // Sea wake waves beneath the hull
            if (uy >= 83.5f && uy <= 85.5f && ux >= 22f && ux <= 138f)
            {
                var wave = MathF.Sin((ux - 22f) * 0.35f);
                if (wave > 0.1f) col = col.Lerp(wakeCol, wave * 0.85f);
            }

            image.SetPixel(x, y, col);
        }
        image.Resize(width, height, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(image);
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
            var province = _provinceTopology.CellToProvince[cell];
            army.ProvinceId = province;
            army.StateMapIndex = province > 0 && province < _provinceTopology.ProvinceToState.Length
                ? _provinceTopology.ProvinceToState[province]
                : SourceEarthData.States[cell];
        }
        _campaign.AttachProvinceTopology(_provinceTopology);
        RefreshMapModePalette();
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
    /// And this a lease (LandArrangementSystem): held by agreement, not by a war, so it must not
    /// wear an occupation's stripes - the owner's colour stays, the tenant's shows as a fine
    /// dotted weave and a dashed hem. Between the occupation band and the full transfer of title.
    /// </summary>
    private const byte LeasedAlpha = 214;

    private readonly Dictionary<int, int> _leasedRegionHolder = new();

    /// <summary>
    /// One texel per country, holding the colour the political map paints it.
    ///
    /// The fill used to come from a raster of five thousand by three thousand painted colours,
    /// which is sixty-six megabytes of texture to say two hundred and sixty things. Reading
    /// the colour out of a palette instead lets the shader rebuild the fill from the country
    /// id map - the boundary comes out subpixel rather than stepped - and lets the crisp
    /// raster be dropped altogether.
    /// </summary>
    private byte[] _nationPalettePixels;
    private Image _nationPaletteImage;
    private ImageTexture _nationPaletteTexture;

    private void BuildNationPalette()
    {
        if (_globeMaterial is null) return;
        var pixels = _nationPalettePixels ??= new byte[256 * 256 * 4];
        Array.Clear(pixels);
        foreach (var (mapIndex, packed) in _colorByMapIndex)
        {
            if (mapIndex <= 0 || mapIndex >= 256 * 256) continue;
            var write = mapIndex * 4;
            pixels[write] = (byte)((packed >> 24) & 0xFF);
            pixels[write + 1] = (byte)((packed >> 16) & 0xFF);
            pixels[write + 2] = (byte)((packed >> 8) & 0xFF);
            pixels[write + 3] = 255;
        }
        WriteUnownedPaletteEntry(pixels);
        if (_nationPaletteImage is null) _nationPaletteImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _nationPaletteImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("nation_palette", _nationPaletteImage, ref _nationPaletteTexture);
    }

    private void RefreshProvinceControllers()
    {
        if (_campaign?.ProvinceMap is null || _campaign.ProvinceController is null || _globeMaterial is null) return;
        EnsureNationIndex();
        var topology = _campaign.ProvinceMap;
        var pixels = _controlPixels ??= new byte[256 * 256 * 4];
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
        if (_ownerImage is null) _ownerImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, ownerPixels);
        else _ownerImage.SetData(256, 256, false, Image.Format.Rgba8, ownerPixels);
        UpdateMapImage("province_owner_colors", _ownerImage, ref _ownerTexture);
        if (_ownerIdImage is null) _ownerIdImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, ownerIds);
        else _ownerIdImage.SetData(256, 256, false, Image.Format.Rgba8, ownerIds);
        UpdateMapImage("province_owner_ids", _ownerIdImage, ref _ownerIdTexture);
        _globeMaterial.SetShaderParameter("owners_moved", ownersMoved ? 1.0f : 0.0f);
        if (ownersMoved) RequestPlateRepaint(topology);
        _controlRevision = _campaign.MapRevision;
        BuildFrontPreview();
        _frontRevision = _controlRevision;
    }

    // Positions and labels are written by the reconcile itself now, off the node handles it
    // already holds - there is nothing left for a second pass to do.

    private static int DivisionPersonnel(Simulation.Army division) => division.Divisions > 1.01f
        ? (int)MathF.Round(division.Divisions * Simulation.MilitaryBootstrap.DivisionSize * division.Strength)
        : Math.Max(0, division.Personnel);

    private int _capitalDestroyedCount = -1;

    private void BuildCapitalMarkers(Simulation.Campaign campaign)
    {
        if (!IsInstanceValid(_capitalRoot) || _capitalTexture is null) return;
        _capitalRevision = campaign.CapitalRevision;
        _capitalDestroyedCount = campaign.DestroyedProvinces.Count;

        foreach (var child in _capitalRoot.GetChildren())
        {
            // QueueFree alone retains the old name until frame end. Rebuilding in that
            // frame made every replacement a generic @Sprite3D, losing its country identity.
            _capitalRoot.RemoveChild(child);
            child.QueueFree();
        }
        _capitalMarkers.Clear();

        foreach (var nation in campaign.Nations)
        {
            // Tiny authored countries may have no raster province at all. Retain their
            // authored point, but never use that fallback for a placed, captured capital.
            var unplaced = nation.CapitalProvinceId == 0 && nation.CapitalStateMapIndex == 0 &&
                campaign.StatesOf(nation).Length > 0 && !string.IsNullOrWhiteSpace(nation.CapitalName);
            if (!unplaced && !Simulation.CapitalCore.HasSeat(campaign, nation)) continue;
            var latitude = unplaced ? nation.CapitalLatitude : Simulation.CapitalCore.EffectiveLatitude(campaign, nation);
            var longitude = unplaced ? nation.CapitalLongitude : Simulation.CapitalCore.EffectiveLongitude(campaign, nation);
            if (TerrainGone(latitude, longitude)) continue;
            // A country that has been conquered has nowhere to be governed from, and its
            // marker used to go on standing in the middle of somebody else's country. One
            // the map never placed - too small for the raster - keeps the point it was given.

            var marker = new Sprite3D
            {
                Name = $"Capital_{nation.Tag}",
                Texture = _capitalTexture,
                FixedSize = false,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = Ui.Colour("globe.selection")
            };

            marker.Position = SurfacePoint(latitude, longitude, SurfaceMarkerRadius);
            OrientSurfaceCounter(marker, marker.Position.Normalized());
            _capitalRoot.AddChild(marker);
            var normal = marker.Position.Normalized();
            _capitalMarkers.Add((marker, normal));
            PlaceCapitalMarker(marker, normal);
        }
    }

    /// <summary>The campaign's capital revision the markers were last built for.</summary>
    private int _capitalRevision = -1;

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
    private ushort[] _labelControlBuffer;

    public void RefreshCountryNames()
    {
        if (_lastLabelAnchors is null) return;
        foreach (var (id, name) in _lastLabelNames)
            if (_nationByMapIndex.TryGetValue(id, out var nation) && nation.MapName != name)
            { BuildCountryLabels(_lastLabelAnchors); return; }
    }

    public void RefreshCountryTagDisplay()
    { if (_lastLabelAnchors is not null) BuildCountryLabels(_lastLabelAnchors); }

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
        const int scale = 2;
        var downWidth = width / scale;
        var downHeight = height / scale;
        var downLength = downWidth * downHeight;
        if (_labelControlBuffer is null || _labelControlBuffer.Length != downLength)
            _labelControlBuffer = new ushort[downLength];

        for (var dy = 0; dy < downHeight; dy++)
        {
            var sy = dy * scale;
            var sRow = sy * width;
            var dRow = dy * downWidth;
            for (var dx = 0; dx < downWidth; dx++)
            {
                var sx = dx * scale;
                var province = cells[sRow + sx];
                _labelControlBuffer[dRow + dx] = province > 0 && province < snapshot.Length ? snapshot[province] : (ushort)0;
            }
        }
        var controlSnapshot = (ushort[])_labelControlBuffer.Clone();
        _labelTask = Task.Run(() => BuildTerritoryLabelAnchors(controlSnapshot, downWidth, downHeight, scale));
    }

    private void BuildCountryLabels(Dictionary<int, TerritoryLabelAnchor> anchors)
    {
        if (!IsInstanceValid(_countryLabelRoot)) return;
        _lastLabelAnchors = anchors;
        _lastLabelNames.Clear();
        _countryLabels.Clear();
        _countryGlyphs.Clear();
        _countryGlyphScale = -1f;
        var activeGroups = new HashSet<int>();

        var countryFont = PresentationFonts.For("map.countries");

        foreach (var (mapIndex, anchor) in anchors)
        {
            if (!_nationByMapIndex.TryGetValue(mapIndex, out var nation) || anchor.Cells < 80)
                continue;

            var text = (Simulation.SettingsCatalog.Flag("show_country_tags")
                ? nation.MapName + " (" + nation.Tag + ")" : nation.MapName).ToUpperInvariant();
            _lastLabelNames[mapIndex] = nation.MapName;
            if (text.Length == 0) continue;
            // Retain the glyph instances when names or borders change. Replacing every
            // Label3D at once overlaps old/new GPU resources with resized render targets,
            // which can exhaust D3D12's descriptor heap during a language/window switch.
            if (!_countryLabelGroups.TryGetValue(mapIndex, out var group) || !IsInstanceValid(group))
            {
                group = new Node3D { Name = $"CountryLabel_{nation.Tag}", Visible = false };
                _countryLabelRoot.AddChild(group);
                _countryLabelGroups[mapIndex] = group;
            }
            activeGroups.Add(mapIndex);
            var existingGlyphs = group.GetChildren().OfType<Label3D>().ToArray();
            var usedGlyphs = 0;

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

                var glyph = usedGlyphs < existingGlyphs.Length ? existingGlyphs[usedGlyphs]
                    : new Label3D { Name = $"Glyph{usedGlyphs}" };
                usedGlyphs++;
                glyph.Text = character.ToString();
                glyph.FontSize = fontSize;
                glyph.Font = countryFont;
                glyph.PixelSize = pixelSize;
                glyph.FixedSize = false;
                glyph.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.OutlineSize = outline;
                glyph.Modulate = new Color(0.93f, 0.94f, 0.96f, 0.88f);
                glyph.OutlineModulate = new Color(0.02f, 0.032f, 0.048f, 0.92f);
                glyph.NoDepthTest = false;
                glyph.Position = SurfacePoint(latitude, longitude, SurfaceTextRadius);
                glyph.Basis = surfaceBasis;
                glyph.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                if (glyph.GetParent() is null) group.AddChild(glyph);
                _countryGlyphs.Add((glyph, latitude, longitude, surfaceBasis, flatRotation));
            }
            for (var index = usedGlyphs; index < existingGlyphs.Length; index++)
            {
                group.RemoveChild(existingGlyphs[index]);
                existingGlyphs[index].QueueFree();
            }

            var center = SurfacePoint(centreLatitude, centreLongitude, SurfaceTextRadius);
            _countryLabels.Add((group, center.Normalized(), anchor.Cells));
        }
        foreach (var id in _countryLabelGroups.Keys.Where(id => !activeGroups.Contains(id)).ToArray())
        {
            var group = _countryLabelGroups[id];
            if (IsInstanceValid(group)) { _countryLabelRoot.RemoveChild(group); group.QueueFree(); }
            _countryLabelGroups.Remove(id);
        }
        // Refreshes may run after this frame's normal visibility/projection pass.
        // Never briefly render every country, or globe-space glyphs on the flat map.
        if (_flatMap) UpdateFlatWorldPositions();
        UpdateCountryLabelVisibility();
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
        if (!CountryLabelsEnabled || !Simulation.WorldLayers.On("country_labels")) { _countryLabelRoot.Visible = false; return; }
        // At tactical zoom, country names yield to states/provinces and unit
        // counters. Keeping continent-scale typography there would cover the map.
        // The flat map's range used to end at 4.4, which is wider than Europe: the view a
        // player spends most of the game in had no country names at all, while the globe
        // at the same scale was full of them. It now runs down to the regional view - the
        // whole of Ukraine on the screen still names Ukraine, as a Paradox map does; the
        // letters follow the country's own extent, so they stay inside it.
        _countryLabelRoot.Visible = _mapMode is not (StrategicMapMode.Terrain or StrategicMapMode.Relief) && (_flatMap
            ? _flatZoom is >= 0.85f and <= 10.8f
            : _distance is >= 7.9f and <= 16.5f);
        if (!_countryLabelRoot.Visible) return;
        // Closer in than 1.7 the letters keep their places - spread across the country - and
        // grow no larger: at full size POLAND and UKRAINE ran into each other and out of their
        // borders. Smaller letters spaced wide are how an atlas names a country seen close.
        var glyphScale = _flatMap ? Math.Clamp(_flatZoom / 1.7f, 0.5f, 1f) : 1f;
        if (Math.Abs(glyphScale - _countryGlyphScale) > 0.02f)
        {
            _countryGlyphScale = glyphScale;
            foreach (var glyph in _countryGlyphs)
                if (IsInstanceValid(glyph.Node)) glyph.Node.Scale = Vector3.One * glyphScale;
        }
        var minimumCells = _flatMap
            ? _flatZoom > 8.8f ? 26000 : _flatZoom > 6.2f ? 6500 : _flatZoom > 4.0f ? 1500 : _flatZoom > 2.6f ? 150 : 40
            : _distance > 11.5f ? 26000 : _distance > 9.5f ? 6500 : _distance > 7.4f ? 1500 : 150;
        var viewNormal = _flatMap ? Vector3.Zero : _camera.GlobalPosition.Normalized();
        var horizonThreshold = _distance > 11.5f ? 0.72f : _distance > 9.5f ? 0.62f : _distance > 7.4f ? 0.50f : 0.38f;
        var rootBasis = _countryLabelRoot.GlobalTransform.Basis;
        foreach (var label in _countryLabels)
            if (IsInstanceValid(label.Node))
            {
                // Stored normals are globe-local. Use the label root's inherited
                // rotation before comparing with the camera; otherwise rotating the
                // globe leaves labels from the old hemisphere visible at its rim.
                var worldNormal = (rootBasis * label.Normal).Normalized();
                var visible = label.Cells >= minimumCells && (_flatMap || worldNormal.Dot(viewNormal) > horizonThreshold);
                if (label.Node.Visible != visible) label.Node.Visible = visible;
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
        var politicalWidth = MapWidth / PoliticalScale;
        var politicalHeight = MapHeight / PoliticalScale;
        var political = new byte[politicalWidth * politicalHeight * 4];
        var politicalShade = new byte[politicalWidth * politicalHeight];
        var plateProvinces = new ushort[politicalWidth * politicalHeight];
        var regionIds = new byte[MapWidth * MapHeight * 2];
        var provinceIds = new byte[MapWidth * MapHeight * 2];
        var nationIds = new byte[MapWidth * MapHeight * 2];
        // The rivers and lakes and the label anchors need nothing from the province map and
        // nothing from each other: each on a core of its own while the provinces are read.
        // Each only reads the source rasters, which nothing here writes.
        var hydrologyWork = Task.Run(() => Simulation.HydrologyCatalog.BuildRaster(sourceWidth, sourceHeight));
        var anchorWork = Task.Run(() => BuildTerritoryLabelAnchors(control, sourceWidth, sourceHeight));
        // The cities are read once a session, by whoever asks first - the 2024 factions raising
        // their militia as the campaign takes the map, on the main thread. Read here instead.
        var cityWork = Task.Run(() => Simulation.CityCatalog.All.Count);
        if (!Simulation.MapRasterCatalog.TryBuildProvinceTopology(
                states, control, sourceWidth, sourceHeight, out var provinces, out var provinceSource))
            throw new InvalidDataException(
                $"game/map_data/provinces.png + definition.csv could not be loaded ({provinceSource}). " +
                "The base game never generates replacement province IDs at runtime; rebuild the data map with " +
                "Tools/MapDataCompiler or install a mod that supplies a complete raster pair.");
        // The sea graph is made from the province raster and its water alone, never from who
        // owns what. Made here, the first fleet that asks for a harbour - every navy in the
        // world, as the campaign takes the map on the main thread - finds it built.
        var seaWork = Task.Run(() => Simulation.SeaLanes.CoastalProvinces(provinces));
        var hydrology = hydrologyWork.GetAwaiter().GetResult();
        provinces.AttachHydrology(hydrology);
        var centreWork = Task.Run(() => HydrologySurfaceData.BuildCentreline(MapWidth, MapHeight,
            hydrology.AuthoredRivers ? RasterRiverCourses.Extract(hydrology) : Simulation.HydrologyCatalog.RiverFeatures));
        var surfaceWork = Task.Run(() => HydrologySurfaceData.Build(hydrology,
            hydrology.AuthoredRivers ? null : Simulation.TerrainCatalog.Water.RiverProfiles,
            hydrology.AuthoredRivers ? null : Simulation.HydrologyCatalog.RiverFeatures,
            widthReach: Simulation.TerrainCatalog.Water.RiverWidthReach));
        var labelAnchors = anchorWork.GetAwaiter().GetResult();

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
                // texture where the source raster says water. Water does not paint the plate.
                if (controlId == 0) continue;

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
                var shadeByte = (byte)(shade * 255f);
                var cell = (y / PoliticalScale) * politicalWidth + x / PoliticalScale;
                // The darkest land in the block is the one a player reads as a border.
                if (political[cell * 4 + 3] != 0 && shadeByte >= politicalShade[cell]) continue;
                politicalShade[cell] = shadeByte;
                plateProvinces[cell] = (ushort)provinceId;
                var write = cell * 4;
                political[write] = (byte)(((packed >> 24) & 0xFF) * shade);
                political[write + 1] = (byte)(((packed >> 16) & 0xFF) * shade);
                political[write + 2] = (byte)(((packed >> 8) & 0xFF) * shade);
                political[write + 3] = 255;
            }
        }

        // A sea graph that could not be made here is made again where it is first asked for,
        // and fails there with the same reason; the map itself does not wait on it.
        try { seaWork.Wait(); cityWork.Wait(); }
        catch (AggregateException) { }

        // Preserve the decoded grids: exact country/state picking is a core map feature.
        // This prevents a click from falling back to an approximate rendered-color lookup.
        if (provinces.ProvinceCount > ushort.MaxValue)
            throw new InvalidOperationException($"Province count {provinces.ProvinceCount} exceeds the 16-bit map contract.");
        return new MapBuildResult
        {
            PoliticalPixels = political,
            PlateProvinces = plateProvinces,
            PlateShade = politicalShade,
            RegionIds = regionIds,
            ProvinceIds = provinceIds,
            NationIds = nationIds,
            HydrologyPixels = surfaceWork.GetAwaiter().GetResult(),
            RiverCentrePixels = centreWork.GetAwaiter().GetResult(),
            Provinces = provinces,
            LabelAnchors = labelAnchors,
            ProvinceRasterLoaded = true,
            ProvinceSource = provinceSource,
            HydrologySource = hydrology.Source
        };
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
        public ushort[] PlateProvinces;
        public byte[] PlateShade;
        public byte[] RegionIds;
        public byte[] ProvinceIds;
        public byte[] NationIds;
        public byte[] HydrologyPixels;
        public byte[] RiverCentrePixels;
        public Simulation.ProvinceTopology Provinces;
        public Dictionary<int, TerritoryLabelAnchor> LabelAnchors;
        public bool ProvinceRasterLoaded;
        public string ProvinceSource = "";
        public string HydrologySource = "";
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
