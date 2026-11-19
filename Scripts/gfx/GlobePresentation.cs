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
        public int LastMetaProvinceId = -1;
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

    private readonly Stack<ArmyStackMarker> _armyMarkerPool = new();
    private double _controllerUploadTimer;

    private void ClearArmyMarkerPool()
    {
        while (_armyMarkerPool.Count > 0)
        {
            var marker = _armyMarkerPool.Pop();
            if (marker.Node is not null && IsInstanceValid(marker.Node))
            {
                marker.Node.GetParent()?.RemoveChild(marker.Node);
                marker.Node.QueueFree();
            }
        }
    }

    // Army marker pool cleanup called during presentation reset


    private readonly Dictionary<(int, int, int, int), ArmyStackMarker> _armyStacks = new();
    private readonly List<(int, int, int, int)> _vanishedStacks = new();
    private readonly Dictionary<string, int> _templateVotes = new();
    private readonly HashSet<int> _selectedArmyIds = new();
    private readonly Dictionary<(int Nation, string Template), Texture2D> _armyCounterTextures = new();
    private readonly List<(int MapIndex, Node3D Node, Vector3 Normal, int Cells)> _countryLabels = new();
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

    private bool _interfaceHidden;
    /// <summary>The interface is off the screen (Ctrl+F9): the map says nothing under the pointer either.</summary>
    public bool InterfaceHidden
    {
        get => _interfaceHidden;
        set { _interfaceHidden = value; if (value) DismissMapTooltips(); }
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
    private float _cachedDetailMix = -1f;
    private float _cachedProvinceDetail = -1f;
    private int _cachedFogViewerParam = -999;
    private readonly Simulation.Controls.MapGestureSession _gesture = new();
    private bool _globeZoomAnchorActive;
    private (double X, double Y, double Z) _globeZoomAnchorWorld;
    private Vector2 _globeZoomAnchorPixel;

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
        // A mushroom cloud queued by the last world is not this world's weather: the queue is
        // process-wide and a saved day number means nothing under a new calendar.
        Simulation.NuclearDetonations.Clear();
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

        ClearArmyMarkerPool();
        _colorByMapIndex.Clear();
        _nationColourSources.Clear();
        _nationByMapIndex.Clear();
        _stateByMapIndex.Clear();

        foreach (var nation in campaign.Nations)
        {
            _colorByMapIndex[nation.MapIndex] = ParseHex(nation.Color, 0x6E7B8BFFu);
            _nationColourSources[nation.MapIndex] = nation.Color;
            _nationByMapIndex[nation.MapIndex] = nation;
        }

        foreach (var state in campaign.States)
            _stateByMapIndex[state.MapIndex] = state;
        _stateIndexRevision = campaign.MapRevision;
        _nationIndexCount = campaign.Nations.Count;
        _nationColourRevision = campaign.MapRevision;

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
            // Read here, not on the worker: the skin is a table the main thread may reload.
            var shades = Simulation.MapBorderStyles.PlateShades();
            // Decided here too: the worker must not read the settings while the main thread may write them.
            var buildFrontier = FrontierFieldWanted();
            _mapTask = Task.Run(() => BuildPoliticalMap(colors, shades, buildFrontier));
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
            Msaa3D = MapAntialiasing(_quality),
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
        CreateRegionLabelLayer(world);
        CreateCityLayer(world);
        CreateRoutePreview(world);
        CreateSeaLiftPreview(world);
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
        var fogViewer = FogOfWarActive && _campaign?.Player is { IsHuman: true } ? _campaign.Player.Id : -1;
        if (_fogViewer != fogViewer) { _fogViewer = fogViewer; _lastMapModeDay = -100; }
        var fogViewerParam = FogOfWarActive && _campaign?.Player is { IsHuman: true } ? _campaign.Player.MapIndex : -1;
        if (_cachedFogViewerParam != fogViewerParam)
        {
            _cachedFogViewerParam = fogViewerParam;
            _globeMaterial?.SetShaderParameter("fog_viewer", fogViewerParam);
        }
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
        _militaryVisualAccumulator += delta;
        var armySyncInterval = (_campaign is { IsPaused: false, Speed: >= 5 }) ? 0.22 : 0.12;
        if (MilitaryMarkersEnabled && _mapApplied && _campaign is not null && _militaryVisualAccumulator >= armySyncInterval)
        {
            _militaryVisualAccumulator = 0;
            using (Simulation.SimProfiler.Measure("f:army sync")) SyncArmyMarkers();
            using (Simulation.SimProfiler.Measure("f:army look")) UpdateMilitaryPresentation();
        }
        if (visualDue)
            using (Simulation.SimProfiler.Measure("f:army vis"))
            {
                RefreshArmyMarkerVisibility();
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
        using (Simulation.SimProfiler.Measure("f:sealift")) UpdateSeaLiftPreview();
        using (Simulation.SimProfiler.Measure("f:supply lines")) UpdateSupplyLines();
        using (Simulation.SimProfiler.Measure("f:strait crossings")) UpdateStraitCrossings();
        _controllerUploadTimer += delta;
        var fastSim = _campaign is { IsPaused: false, Speed: >= 5 };
        var uploadDue = !fastSim || _controllerUploadTimer >= 0.17;
        if (_mapApplied && _campaign is not null && _controlRevision != _campaign.MapRevision && uploadDue)
        {
            _controllerUploadTimer = 0;
            using (Simulation.SimProfiler.Measure("f:map paint")) RefreshProvinceControllers();
        }
        if (_mapApplied && _campaign is not null &&
            (_labelTask is not null || _labelRevision != _campaign.MapRevision))
            using (Simulation.SimProfiler.Measure("f:label place")) RefreshCountryLabelPlacement();
        if (IsInstanceValid(_frontPreview)) _frontPreview.Visible = MilitaryMarkersEnabled && _frontMesh.GetSurfaceCount() > 0;
        if (visualDue)
        {
            UpdateCityLabels(visualStep);
            UpdateSettlementModels(visualStep);
            using (Simulation.SimProfiler.Measure("f:labels"))
            {
                UpdateCountryLabelVisibility(visualStep);
                UpdateRegionLabels(visualStep);
            }
        }
        if (_battleMarkers.Count > 0 && (visualDue || _dragging || _boxSelecting || FlatViewInMotion)) QueueRedraw();
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
                _frontierAuthored = result.FrontierAuthored;
                ApplyFrontier(result.Frontier);
                result.Frontier = null;
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
                AdoptRegionLabelAnchors(result.RegionAnchors);
                AdoptRiverAnchors(result.RiverAnchors);
                result.RegionAnchors = null;
                result.RiverAnchors = null;
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

    internal bool VerificationPickAt(Vector2 pixel, out GlobePick pick, bool updateSelection = false) =>
        TryPick(pixel, out pick, updateSelection);

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

    /// <summary>Unified camera distance across orbital and flat projections.</summary>
    private float ViewDistance => _flatMap ? _flatZoom * 1.6f - 1.04f : _distance;
    internal float ShellViewDistance => ViewDistance;
    internal float ShellFlatZoom => _flatZoom;
    internal IReadOnlySet<int> NewShellVisibleArmyIds { get; set; }
    internal Vector2? ProjectMapPointToScreen(float latitude, float longitude) => BadgeScreenPoint(latitude, longitude);
    internal (float Latitude, float Longitude, float SpanDegrees) CameraMinimapView =>
        _flatMap
            ? (Math.Clamp(_flatY * 18f, -85f, 85f), Math.Clamp(_flatX * 18f, -180f, 180f), Math.Clamp(_flatZoom * 14f, 12f, 180f))
            : (Math.Clamp(-Mathf.RadToDeg(_pitch), -85f, 85f), Mathf.PosMod(Mathf.RadToDeg(_yaw), 360f) - 180f, Math.Clamp(_distance * 6.5f, 14f, 160f));

    private void ApplyNewShellArmyVisibility(IReadOnlySet<int> visibleArmies)
    {
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
            var stack = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
            var keep = false;
            for (var i = 0; i < stack.Count; i++)
            {
                if (visibleArmies.Contains(stack[i].Id) || _selectedArmyIds.Contains(stack[i].Id))
                {
                    keep = true;
                    break;
                }
            }
            if (!keep) marker.Node.Visible = false;
        }
    }

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
        if (IsInstanceValid(_daySun))
        {
            _daySun.ShadowEnabled = _quality.SunShadows;
            _daySun.DirectionalShadowMode = _quality.RenderScale >= 0.99f
                ? DirectionalLight3D.ShadowMode.Parallel4Splits
                : DirectionalLight3D.ShadowMode.Parallel2Splits;
        }
        // Release the directional shadow atlas entirely when sun shadows are off; holding a 512
        // buffer still kept a shadow render target allocated for a disabled pass.
        RenderingServer.DirectionalShadowAtlasSetSize(
            _quality.SunShadows
                ? Math.Clamp((int)MathF.Round(2048f * _quality.RenderScale), 512, 2048)
                : 0, true);
        if (IsInstanceValid(_viewport))
            _viewport.Msaa3D = MapAntialiasing(_quality);
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
        DrawSeaLiftBadges();
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
            var scheme = Simulation.Controls.ControlSchemeRules.ActiveMouseScheme;
            var threshPx = Simulation.Controls.ControlSchemeRules.DragThresholdPx;
            var threshMs = Simulation.Controls.ControlSchemeRules.DragThresholdMs;
            var nowMs = Time.GetTicksMsec();

            if (button.ButtonIndex == MouseButton.Left)
            {
                if (button.Pressed)
                {
                    _gesture.BeginPress(scheme, Simulation.Controls.MapPointerButton.Left,
                        button.Position.X, button.Position.Y, nowMs,
                        button.CtrlPressed, button.AltPressed, button.ShiftPressed,
                        allowRegionBox: !CountrySelectionOnly && ProvincesBoxSelected is not null);

                    if (_gesture.BoxStarted)
                    {
                        _boxSelecting = true;
                        _boxRegions = _gesture.BoxRegions;
                        _boxStart = button.Position;
                        _boxCurrent = button.Position;
                        _dragging = false;
                    }
                    else
                    {
                        _boxSelecting = false;
                        _boxRegions = false;
                        _cameraDragButton = MouseButton.Left;
                        _previousPointer = button.Position;
                        _dragDistance = 0f;
                        _flatGlide = Vector2.Zero;
                        _orbitGlide = Vector2.Zero;
                        _lastFlatDragUsec = Time.GetTicksUsec();
                        _globeZoomAnchorActive = false;
                        _flatZoomAnchorActive = false;
                        _dragging = false;
                    }
                    QueueRedraw();
                }
                else if (_gesture.IsPressed && _gesture.Button == Simulation.Controls.MapPointerButton.Left)
                {
                    var outcome = _gesture.ReleaseLeft(button.Position.X, button.Position.Y, threshPx);
                    _boxSelecting = false;
                    _boxRegions = false;
                    _dragging = false;
                    _boxCurrent = button.Position;

                    if (outcome == Simulation.Controls.LmbReleaseOutcome.BoxRegionSelect)
                    {
                        var area = RectFromPoints(_boxStart, _boxCurrent);
                        var provinces = area.Size.Length() >= threshPx ? ProvincesInScreenRect(area) : new List<int>();
                        if (provinces.Count == 0 && TryPick(button.Position, out var single, updateSelection: false) && single.ProvinceId > 0)
                            provinces.Add(single.ProvinceId);
                        if (provinces.Count > 0) ProvincesBoxSelected?.Invoke(provinces);
                    }
                    else if (outcome == Simulation.Controls.LmbReleaseOutcome.BoxArmySelect)
                    {
                        DispatchBoxUnitSelection(RectFromPoints(_boxStart, _boxCurrent));
                    }
                    else if (outcome == Simulation.Controls.LmbReleaseOutcome.ClickSelect)
                    {
                        if (!CountrySelectionOnly && _movementLayer is not null && _movementLayer.TryPickAt(button.Position, out var movement))
                            MovementPicked?.Invoke(movement);
                        else if (CountrySelectionOnly)
                        {
                            if (TryPick(button.Position, out var country)) CountryPicked?.Invoke(country);
                        }
                        else if (SupportRebaseArmed)
                        {
                            // A strike target is being named in the long-range panel: the click is its.
                            if (TryPick(button.Position, out var destination)) CountryPicked?.Invoke(destination);
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
                        else
                            EmptyMapClicked?.Invoke();
                    }
                    QueueRedraw();
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex is MouseButton.Right or MouseButton.Middle)
            {
                var ptrBtn = button.ButtonIndex == MouseButton.Right
                    ? Simulation.Controls.MapPointerButton.Right
                    : Simulation.Controls.MapPointerButton.Middle;

                if (button.Pressed)
                {
                    _gesture.BeginPress(scheme, ptrBtn, button.Position.X, button.Position.Y, nowMs,
                        button.CtrlPressed, button.AltPressed, button.ShiftPressed, allowRegionBox: false);
                    if (_gesture.BoxStarted)
                    {
                        _boxSelecting = true;
                        _boxRegions = false;
                        _boxStart = button.Position;
                        _boxCurrent = button.Position;
                        _dragging = false;
                    }
                    else
                    {
                        BeginCameraDrag(button.Position, button.ButtonIndex);
                        _dragging = _gesture.PanStarted;
                        _boxSelecting = false;
                    }
                    QueueRedraw();
                }
                else
                {
                    if (button.ButtonIndex == MouseButton.Middle && _gesture.IsPressed &&
                        _gesture.Button == Simulation.Controls.MapPointerButton.Middle)
                    {
                        var outcome = _gesture.ReleaseMiddle(button.Position.X, button.Position.Y, threshPx);
                        _boxSelecting = false;
                        _dragging = false;
                        _boxCurrent = button.Position;
                        if (outcome == Simulation.Controls.MmbReleaseOutcome.BoxArmySelect && !CountrySelectionOnly)
                        {
                            DispatchBoxUnitSelection(RectFromPoints(_boxStart, _boxCurrent));
                        }
                        else if (outcome == Simulation.Controls.MmbReleaseOutcome.ClickArmySelect && !CountrySelectionOnly)
                        {
                            if (TryPickArmyStack(button.Position, out var mmbStack))
                            {
                                if (mmbStack.Count == 1) ArmyPicked?.Invoke(mmbStack[0]);
                                else if (mmbStack.Count > 1) ArmiesBoxPicked?.Invoke(mmbStack);
                            }
                        }
                        QueueRedraw();
                    }
                    else if (button.ButtonIndex == MouseButton.Right && _gesture.IsPressed &&
                             _gesture.Button == Simulation.Controls.MapPointerButton.Right)
                    {
                        var outcome = _gesture.ReleaseRight(button.Position.X, button.Position.Y, nowMs,
                            threshPx, threshMs, button.AltPressed);
                        _dragging = false;
                        if (!PlanEraseMode && !CountrySelectionOnly)
                        {
                            if (outcome == Simulation.Controls.RmbReleaseOutcome.AltSplitMove &&
                                TryPick(button.Position, out var altCommanded, updateSelection: false))
                                AltProvinceSplitCommanded?.Invoke(altCommanded);
                            else if (outcome == Simulation.Controls.RmbReleaseOutcome.CommandOrContextMenu &&
                                     TryPick(button.Position, out var commanded, updateSelection: false))
                                ProvinceCommanded?.Invoke(commanded);
                        }
                    }
                    else if (button.ButtonIndex == _cameraDragButton)
                    {
                        _dragging = false;
                    }
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown)
            {
                _flatGlide = Vector2.Zero; _orbitGlide = Vector2.Zero;
                var sign = button.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
                if (_flatMap)
                    ZoomFlatMapAt(button.Position, sign, button.ShiftPressed);
                else
                    ZoomGlobeAt(button.Position, sign, button.ShiftPressed);
                AcceptEvent();
                return;
            }
        }

        if (inputEvent is InputEventMouseMotion gestureMotion && _gesture.IsPressed)
        {
            var threshPx = Simulation.Controls.ControlSchemeRules.DragThresholdPx;
            var threshMs = Simulation.Controls.ControlSchemeRules.DragThresholdMs;
            var nowMs = Time.GetTicksMsec();
            var shouldPan = _gesture.UpdateMotion(gestureMotion.Position.X, gestureMotion.Position.Y,
                nowMs, threshPx, threshMs, out var panDx, out var panDy);

            if (_gesture.BoxStarted)
            {
                _boxCurrent = gestureMotion.Position;
                QueueRedraw();
                AcceptEvent();
                return;
            }

            if (shouldPan)
            {
                if (!_dragging)
                {
                    _dragging = true;
                    _previousPointer = gestureMotion.Position - new Vector2(panDx, panDy);
                    _lastFlatDragUsec = Time.GetTicksUsec();
                    _globeZoomAnchorActive = false;
                    _flatZoomAnchorActive = false;
                    _targetFlatX = _flatX;
                    _targetFlatY = _flatY;
                    _targetFlatZoom = _flatZoom;
                    _targetYaw = _yaw;
                    _targetPitch = _pitch;
                    _targetDistance = _distance;
                }
                DragCamera(new Vector2(panDx, panDy));
                _previousPointer = gestureMotion.Position;
            }
            AcceptEvent();
            return;
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
        InvalidateCities();
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
        if (LoadingInputBlocked || MediaLibraryInputBlocked || MapLayerDrawerInputBlocked || InterfaceHidden) return "";
        var badgeTip = RegionBadgeTooltip(atPosition);
        if (badgeTip.Length > 0) return badgeTip;
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

    public IReadOnlyList<Simulation.Army> VisiblePlayerArmiesOnScreen(int playerId)
    {
        var result = new List<Simulation.Army>();
        if (_campaign is null || !IsInstanceValid(_camera)) return result;
        var vp = ViewportSize();
        var rect = new Rect2(Vector2.Zero, new Vector2(vp.X, vp.Y));
        var viewNormal = _camera.GlobalPosition.Normalized();
        var seen = new HashSet<int>();
        foreach (var marker in _armyStacks.Values)
        {
            var node = marker.Node;
            if (!IsInstanceValid(node) || !marker.Plate.Visible || _camera.IsPositionBehind(node.GlobalPosition))
                continue;
            if (!_flatMap && node.GlobalPosition.Normalized().Dot(viewNormal) <= 0.05f)
                continue;
            if (!rect.HasPoint(_camera.UnprojectPosition(node.GlobalPosition)))
                continue;
            var stack = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
            foreach (var army in stack)
                if (army.NationId == playerId && Simulation.ArmyInteraction.CanSelect(_campaign, army) && seen.Add(army.Id))
                    result.Add(army);
        }
        return result;
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

    private void DispatchBoxUnitSelection(Rect2 rect)
    {
        if (CountrySelectionOnly) return;
        // Divisions and long-range formations in one box, as one selection -
        // the panel no longer has to be open for a box to take formations.
        var selected = ArmiesInScreenRect(rect);
        var formations = SupportInScreenRect(rect);
        if (selected.Count > 0 || formations.Count > 0)
        {
            if (SelectionBoxPicked is not null) SelectionBoxPicked.Invoke(selected, formations);
            else if (selected.Count > 0) ArmiesBoxPicked?.Invoke(selected);
        }
        else if (FleetsInScreenRect(rect) is { Count: > 0 } fleets)
            FleetsBoxPicked?.Invoke(fleets);
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
            if (!IsInstanceValid(_viewport)) return;
            var scale = Mathf.Clamp(value, 0.25f, 2f);
            _viewport.Scaling3DScale = scale;
            // Fewer pixels than the screen, upscaled with FSR's sharpening rather than stretched:
            // a plain bilinear stretch is what made a lowered tier look smeared.
            _viewport.Scaling3DMode = scale < 0.999f ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
            _viewport.FsrSharpness = 0.2f;
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

    private void ZoomGlobeAt(Vector2 pixel, float direction, bool fine)
    {
        var vp = ViewportSize();
        var fov = IsInstanceValid(_camera) ? _camera.Fov : RigFieldOfView;
        var baseAltitude = Mathf.Max(0.10f, _targetDistance - GlobeRadius);
        var baseDelta = direction * baseAltitude * 0.22f;
        var delta = Simulation.Controls.ControlSchemeRules.ComputeZoomStep(baseDelta, fine, 0.35f);
        var nextDistance = Mathf.Clamp(_targetDistance + delta, MinimumDistance, MaximumDistance);

        if (vp.X >= 2 && vp.Y >= 2 &&
            Simulation.Controls.ControlSchemeRules.TryGlobeRayHitLocal(pixel.X, pixel.Y, vp.X, vp.Y, fov, GlobeRadius, _distance, out var hitLocal))
        {
            _globeZoomAnchorWorld = Simulation.Controls.ControlSchemeRules.OrbitLocalToWorld(hitLocal, _pitch, _yaw);
            _globeZoomAnchorPixel = pixel;
            _globeZoomAnchorActive = true;
            _targetDistance = nextDistance;
            if (Simulation.Controls.ControlSchemeRules.SolveGlobeZoomAnchor(
                pixel.X, pixel.Y, vp.X, vp.Y, fov, GlobeRadius, MaxPitch,
                _globeZoomAnchorWorld.X, _globeZoomAnchorWorld.Y, _globeZoomAnchorWorld.Z,
                _pitch, _yaw, nextDistance, out var nextPitch, out var nextYaw))
            {
                _targetPitch = nextPitch;
                _targetYaw = nextYaw;
            }
            return;
        }
        _globeZoomAnchorActive = false;
        _targetDistance = nextDistance;
    }

    private void UpdateGlobeZoomAnchor()
    {
        if (_flatMap || !_globeZoomAnchorActive) return;
        var vp = ViewportSize();
        var fov = IsInstanceValid(_camera) ? _camera.Fov : RigFieldOfView;
        if (vp.X >= 2 && vp.Y >= 2 &&
            Simulation.Controls.ControlSchemeRules.SolveGlobeZoomAnchor(
                _globeZoomAnchorPixel.X, _globeZoomAnchorPixel.Y, vp.X, vp.Y, fov, GlobeRadius, MaxPitch,
                _globeZoomAnchorWorld.X, _globeZoomAnchorWorld.Y, _globeZoomAnchorWorld.Z,
                _pitch, _yaw, _distance, out var p, out var y))
        {
            _pitch = p;
            _yaw = y;
        }
        if (MathF.Abs(_distance - _targetDistance) < 0.0005f)
            _globeZoomAnchorActive = false;
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
        UpdateGlobeZoomAnchor();
        if (_flatMap) UpdateFlatGlide((float)delta);
        else UpdateOrbitGlide((float)delta);

        var typing = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        var cameraKeysBlocked = typing || DealOrForceGame.CameraKeyboardBlocked;
        if (_flatMap && !cameraKeysBlocked)
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
        else if (!_flatMap && !cameraKeysBlocked)
        {
            // The keys move the camera, not the globe.
            if (DealOrForceGame.Held(Key.W) || DealOrForceGame.Held(Key.S) ||
                DealOrForceGame.Held(Key.A) || DealOrForceGame.Held(Key.D)) _globeZoomAnchorActive = false;
            if (DealOrForceGame.Held(Key.W)) _targetPitch = Mathf.Clamp(_targetPitch - (float)delta * 0.7f, -MaxPitch, MaxPitch);
            if (DealOrForceGame.Held(Key.S)) _targetPitch = Mathf.Clamp(_targetPitch + (float)delta * 0.7f, -MaxPitch, MaxPitch);
            if (DealOrForceGame.Held(Key.A)) _targetYaw -= (float)delta * 0.7f;
            if (DealOrForceGame.Held(Key.D)) _targetYaw += (float)delta * 0.7f;
        }

        if (!LoadingInputBlocked && !_dragging && !_boxSelecting && !cameraKeysBlocked &&
            Simulation.Controls.ControlSchemeRules.EdgePanEnabled && GetWindow() is { } win && win.HasFocus())
        {
            var vp = ViewportSize();
            var localMouse = GetLocalMousePosition();
            if (localMouse.X >= 0f && localMouse.Y >= 0f && localMouse.X <= vp.X && localMouse.Y <= vp.Y)
            {
                var (edx, edy) = Simulation.Controls.ControlSchemeRules.ComputeEdgePanDelta(
                    localMouse.X, localMouse.Y, vp.X, vp.Y, deltaSec: (float)delta);
                if (edx != 0f || edy != 0f)
                {
                    _flatZoomAnchorActive = false;
                    _globeZoomAnchorActive = false;
                    if (_flatMap)
                    {
                        var upp = MapPanScale.FlatUnitsPerPixel(_flatZoom, vp.Y);
                        _targetFlatX -= edx * upp;
                        _targetFlatY += edy * upp;
                        ClampFlatCamera();
                    }
                    else
                    {
                        var rad = MapPanScale.OrbitRadiansPerPixel(_distance, GlobeRadius, _camera.Fov, vp.Y);
                        var lonScale = Mathf.Max(0.28f, Mathf.Cos(_pitch));
                        _targetYaw -= edx * rad / lonScale;
                        _targetPitch = Mathf.Clamp(_targetPitch - edy * rad, -MaxPitch, MaxPitch);
                    }
                }
            }
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


    private void RefreshArmyMarkerVisibility()
    {
        if (!IsInstanceValid(_camera)) return;
        UpdateMarkerVisibility(_armyRoot, _armyMarkers, MilitaryMarkersEnabled && (_flatMap || ViewDistance <= 10.8f),
            _flatMap ? 0f : GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length()) + .002f);
        if (NewShellVisibleArmyIds is { } visibleArmies)
            ApplyNewShellArmyVisibility(visibleArmies);
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
            // Aboard a ship it is the fleet's to show (an anchor on the fleet says how many).
            if (army.EmbarkedFleetId >= 0) continue;
            var bucket = ArmyPresentationBucket(campaign, army);
            if (!CanShowArmy(army) || !_shownNations.Contains(army.NationId)) continue;
            if (!_armyStacks.TryGetValue(bucket, out var marker))
            {
                marker = _armyMarkerPool.Count > 0 ? _armyMarkerPool.Pop() : new ArmyStackMarker();
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
                if (_armyMarkerPool.Count < 512)
                {
                    marker.Node.Visible = false;
                    if (marker.Glow is not null && IsInstanceValid(marker.Glow))
                        marker.Glow.Visible = false;
                    marker.Stack.Clear();
                    marker.DisplayStack.Clear();
                    marker.Seen = false;
                    marker.CountText = "";
                    marker.ShownDivisions = -1;
                    marker.ShownMenTenths = -1;
                    marker.ConditionKey = -1;
                    marker.LastMetaProvinceId = -1;
                    marker.ActualFingerprint = 0;
                    marker.DisplayFingerprint = 0;
                    marker.LayoutInitialized = false;
                    marker.LayoutOffset = Vector2.Zero;
                    marker.MotionSettled = false;
                    _armyMarkerPool.Push(marker);
                }
                else
                {
                    // Detach now so a replacement with the same lead ID does not
                    // inherit an anonymous name while the old node awaits deletion.
                    marker.Node.GetParent()?.RemoveChild(marker.Node);
                    marker.Node.QueueFree();
                }
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
                InvalidateFlatCounterDensity();
            }
            else
            {
                if (marker.Node.GetParent() != _armyRoot)
                {
                    marker.Node.GetParent()?.RemoveChild(marker.Node);
                    _armyRoot.AddChild(marker.Node);
                }
                marker.Node.Name = $"DivisionStack_{nation.Tag}_{lead.ProvinceId}_{lead.Id}";

                var textureChanged = marker.NationId != nation.Id ||
                    marker.FlagVisualId != NationalFlagVisualId(nation) ||
                    !string.Equals(marker.TemplateId, templateId, StringComparison.Ordinal);
                if (textureChanged)
                {
                    marker.FullPlateTexture = ArmyCounterTexture(nation, templateId);
                    marker.NationId = nation.Id;
                    marker.TemplateId = templateId;
                }
                if (textureChanged || !marker.LayoutInitialized || marker.FlatLayout != _flatMap)
                    SizeArmyCounter(marker);
            }
            marker.FlagVisualId = NationalFlagVisualId(nation);
            if (marker.Node is null) continue;

            var location = ArmyPresentationLocation(marker.Stack);
            var position = ArmyMarkerPosition(marker);
            marker.TargetPosition = position;
            if (!marker.MotionSettled)
            {
                // A new province bucket starts at the formation's last drawn position
                // when available. An unrelated node from the pool starts at the new target.
                marker.DisplayPosition = _previousArmyPositions.TryGetValue(lead.Id, out var previous)
                    && previous != Vector3.Zero ? previous : position;
                marker.Node.Position = marker.DisplayPosition;
                marker.MotionSettled = true;
                InvalidateFlatCounterDensity();
            }
            marker.Normal = SurfacePoint(location.Latitude, location.Longitude, 1f);
            OrientSurfaceArmyCard(marker);
            if (!_flatMap) SizeSurfaceArmyCounter(marker);
            // Diagnostic geography is independent of the rendered position.
            if (marker.LastMetaProvinceId != lead.ProvinceId &&
                _campaign.ProvinceMap is { } topology && lead.ProvinceId > 0 &&
                lead.ProvinceId <= topology.ProvinceCount)
            {
                marker.LastMetaProvinceId = lead.ProvinceId;
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
        // Reconciliation and the visibility tick run at different rates. Cull new and
        // reused counters before rendering, including the overlays that ignore depth.
        RefreshArmyMarkerVisibility();

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
                Position = initialPos
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
        if (ArmyStructureFingerprint(_campaign) == _armyStructureFingerprint)
        {
            SyncArmyMarkerReadouts();
            return;
        }
        ReconcileArmyMarkers(_campaign);
    }

    private void SyncArmyMarkerReadouts()
    {
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
            if (marker.Plate is not null && !marker.Plate.Visible) continue;
            var stack = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
            if (stack.Count == 0) continue;
            var divisions = 0f;
            var personnel = 0f;
            for (var i = 0; i < stack.Count; i++)
            {
                divisions += stack[i].Divisions;
                personnel += DivisionPersonnel(stack[i]);
            }
            var divCount = Mathf.CeilToInt(divisions);
            var menTenths = (int)MathF.Round(personnel / 100f);
            if (marker.ShownDivisions == divCount && marker.ShownMenTenths == menTenths) continue;
            var divChanged = marker.ShownDivisions != divCount;
            marker.ShownDivisions = divCount;
            marker.ShownMenTenths = menTenths;
            var text = marker.ShownCompact
                ? divCount.ToString()
                : $"{divCount}\n{menTenths / 10f:0.#}k";
            if (!string.Equals(marker.CountText, text, StringComparison.Ordinal))
            {
                marker.CountText = text;
                if (marker.Count is not null && marker.Count.Visible &&
                    (!_flatMap || marker.DisplayProvinces <= 1 || marker.Plate?.Visible == true))
                    marker.Count.Text = text;
                if (divChanged) InvalidateFlatCounterDensity();
            }
        }
    }

    private readonly HashSet<int> _liveArmyIds = new();

    private int ArmyStructureFingerprint(Simulation.Campaign campaign)
    {
        var hash = new HashCode();
        hash.Add(FlagCatalog.VisualStateHash(campaign));
        hash.Add(SettingsCatalog.Flag("movement_flags"));
        hash.Add(SettingsCatalog.Chosen("map_divisions"));
        hash.Add(ShownCountryId);
        hash.Add(campaign.Armies.Count);
        foreach (var army in campaign.Armies)
        {
            hash.Add(army.Id);
            hash.Add(army.NationId);
            hash.Add(army.TemplateId);
            hash.Add(army.EmbarkedFleetId >= 0);
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
        var brassRim = Ui.Colour("map.counter_rim", 0.96f);
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
        if (_campaign is null) return;
        var newCountries = _campaign.Nations.Count != _nationIndexCount;
        if (!newCountries && _nationColourRevision == _campaign.MapRevision) return;
        var coloursChanged = false;
        foreach (var nation in _campaign.Nations)
        {
            _nationByMapIndex[nation.MapIndex] = nation;
            if (_nationColourSources.TryGetValue(nation.MapIndex, out var source) && source == nation.Color) continue;
            _nationColourSources[nation.MapIndex] = nation.Color;
            _colorByMapIndex[nation.MapIndex] = ParseHex(nation.Color, 0x6E7B8BFFu);
            coloursChanged = true;
        }
        _nationColourRevision = _campaign.MapRevision;
        _nationIndexCount = _campaign.Nations.Count;
        if (coloursChanged) BuildNationPalette();
        if (newCountries)
        {
            BuildCapitalMarkers(_campaign);
            GD.Print($"[dof] political map now holds {_nationIndexCount:N0} countries");
        }
    }

    /// <summary>How many countries the colour and name indexes were built for.</summary>
    private int _nationIndexCount;
    private long _nationColourRevision = -1;
    private readonly Dictionary<int, string> _nationColourSources = new();

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
        // The far plate carries these colours baked in, so it has to be painted again - and asked
        // for here, not left to "the next map refresh": that refresh only comes when a province
        // changes hands, and a country that changes its colour without gaining or losing ground
        // (a new tag on the same land) would keep its old colour at the world view, beside its
        // new one up close. The request is throttled like any other, so asking is cheap.
        _plateColoursStale = true;
        RequestPlateRepaint(_campaign?.ProvinceMap);
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
        _globeMaterial.SetShaderParameter("owners_moved", ownersMoved ? 1.0f : 0.0f);
        // Asked every time, not only when something differs from the authored world: owners
        // that have come BACK to it leave the picture on screen just as wrong. The request
        // itself is free when the plate already shows today's owners.
        RequestPlateRepaint(topology);
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
        // Which cities carry the capital's mark and colour is decided when the cities are placed,
        // and with the camera at rest they are not placed again: the star moved to Yekaterinburg
        // and Moscow kept a golden name with no dot under it.
        _cityDirty = true;
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
                glyph.Modulate = CountryFillColour(0.88f);
                glyph.OutlineModulate = CountryOutlineColour(0.92f);
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
            _countryLabels.Add((mapIndex, group, center.Normalized(), anchor.Cells));
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

    private void UpdateCountryLabelVisibility(double delta = 0)
    {
        if (!IsInstanceValid(_countryLabelRoot) || !IsInstanceValid(_camera)) return;
        if (!CountryLabelsEnabled || !Simulation.WorldLayers.On("country_labels")) { _countryLabelRoot.Visible = false; return; }
        if (_mapMode is StrategicMapMode.Terrain or StrategicMapMode.Relief) { _countryLabelRoot.Visible = false; return; }

        var measure = _flatMap ? _flatZoom : _distance;
        if (!_mapLookAtlas)
        {
            _countryLabelRoot.Visible = _flatMap ? _flatZoom >= 0.85f : _distance >= 7.9f;
            if (!_countryLabelRoot.Visible) return;
        }
        else
        {
            var bandAny = MapLabelPriority.CountryTargetAlpha(true, _flatMap, measure, 100000, true, true);
            if (bandAny <= 0.005f && _countryAlphas.Values.All(a => a <= 0.01f))
            {
                _countryLabelRoot.Visible = false;
                return;
            }
            _countryLabelRoot.Visible = true;
        }

        // Closer in than 1.7 the letters keep their places - spread across the country - and
        // grow no larger: at full size POLAND and UKRAINE ran into each other and out of their
        // borders. Smaller letters spaced wide are how an atlas names a country seen close.
        // Past the strategic view the globe's letters grow a little as it draws away, so the
        // names left on a whole hemisphere are still big enough to read.
        var glyphScale = _flatMap ? Math.Clamp(_flatZoom / 1.7f, 0.5f, 1f) : Math.Clamp(_distance / 16.5f, 1f, 1.3f);
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
        var playerMapIndex = _campaign?.Player?.MapIndex ?? -1;
        var stepDt = (float)Math.Max(0.016, delta);
        var atlasFill = _mapLookAtlas ? Color.FromHtml(Simulation.MapLook.ColourHex("map.look_label_fill")) : default;
        var atlasOutline = _mapLookAtlas ? Color.FromHtml(Simulation.MapLook.ColourHex("map.look_label_outline")) : default;

        foreach (var label in _countryLabels)
            if (IsInstanceValid(label.Node))
            {
                // Stored normals are globe-local. Use the label root's inherited
                // rotation before comparing with the camera; otherwise rotating the
                // globe leaves labels from the old hemisphere visible at its rim.
                var worldNormal = (rootBasis * label.Normal).Normalized();
                var horizonOk = _flatMap || worldNormal.Dot(viewNormal) > horizonThreshold;
                if (!_mapLookAtlas)
                {
                    var visible = label.Cells >= minimumCells && horizonOk;
                    if (label.Node.Visible != visible) label.Node.Visible = visible;
                    continue;
                }
                var priority = label.MapIndex == _highlightedNation || (playerMapIndex > 0 && label.MapIndex == playerMapIndex);
                var target = MapLabelPriority.CountryTargetAlpha(true, _flatMap, measure, label.Cells, priority, horizonOk);
                var prev = _countryAlphas.TryGetValue(label.MapIndex, out var current) ? current : 0f;
                var next = horizonOk ? MapLabelPriority.StepOpacity(prev, target, stepDt) : 0f;
                _countryAlphas[label.MapIndex] = next;
                var visibleAtlas = next > 0.01f;
                if (label.Node.Visible != visibleAtlas) label.Node.Visible = visibleAtlas;
                if (visibleAtlas && Math.Abs(next - prev) > 0.005f)
                {
                    var fillCol = new Color(atlasFill, next);
                    var outlineCol = new Color(atlasOutline, Math.Min(0.94f, next * 1.06f));
                    foreach (var child in label.Node.GetChildren())
                    {
                        if (child is not Label3D glyph) continue;
                        glyph.Modulate = fillCol;
                        glyph.OutlineModulate = outlineCol;
                    }
                }
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

    private static MapBuildResult BuildPoliticalMap(Dictionary<int, uint> colors, Simulation.MapPlateShades shades,
        bool buildFrontier)
    {
        var control = SourceEarthData.Control;
        var states = SourceEarthData.States;
        var sourceWidth = SourceEarthData.Width;
        var sourceHeight = SourceEarthData.Height;
        var politicalWidth = MapWidth / PoliticalScale;
        var politicalHeight = MapHeight / PoliticalScale;
        // What the far plate is painted from (PoliticalPlateSource): for each of its cells the
        // province that speaks for the block, the country the raster gives it, and the two
        // facts of geography no treaty moves. `plateClass` only picks the speaker.
        var plateProvinces = new ushort[politicalWidth * politicalHeight];
        var plateHolders = new ushort[politicalWidth * politicalHeight];
        var plateGeography = new byte[politicalWidth * politicalHeight];
        var plateClass = new byte[politicalWidth * politicalHeight];
        var regionIds = new byte[MapWidth * MapHeight * 2];
        var provinceIds = new byte[MapWidth * MapHeight * 2];
        var nationIds = new byte[MapWidth * MapHeight * 2];
        // The rivers and lakes and the label anchors need nothing from the province map and
        // nothing from each other: each on a core of its own while the provinces are read.
        // Each only reads the source rasters, which nothing here writes.
        var hydrologyWork = Task.Run(() => Simulation.HydrologyCatalog.BuildRaster(sourceWidth, sourceHeight));
        var anchorWork = Task.Run(() => BuildTerritoryLabelAnchors(control, sourceWidth, sourceHeight));
        var regionAnchorWork = Task.Run(() => BuildRegionAnchorsDownsampled(states, sourceWidth, sourceHeight));
        var riverAnchorWork = Task.Run(() => RiverLabelLayout.BuildAnchors(Simulation.HydrologyCatalog.RiverFeatures));
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
        var regionAnchors = regionAnchorWork.GetAwaiter().GetResult();
        var riverAnchors = riverAnchorWork.GetAwaiter().GetResult();
        // The frontier field (Simulation.FrontierField) for the owners the raster was drawn
        // with: on a core of its own, and made into a mipmapped picture there, so the main
        // thread only uploads it. The control grid is kept by reference for the repaints that
        // follow a moving border - the source data lets its own copy go once the map is up.
        var frontierAuthored = control;
        // Not at all in Compatibility under the atlas look, which cannot read it (FrontierFieldWanted).
        var frontierWork = buildFrontier
            ? Task.Run(() => FrontierImage(frontierAuthored, provinces, provinces.ProvinceToNation, provinces.ProvinceToNation))
            : Task.FromResult<Image>(null);

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

                // The plate's alpha is a land mask; the shader supplies exactly the original
                // ocean texture where the source raster says water. Water does not paint the plate.
                if (controlId == 0) continue;

                var east = row + ((sourceX + 1) % sourceWidth);
                var west = row + (sourceX == 0 ? sourceWidth - 1 : sourceX - 1);
                var south = southRow + sourceX;
                var north = (sourceY == 0 ? row : row - sourceWidth) + sourceX;

                var shore = control[east] == 0 || control[west] == 0 || control[south] == 0 || control[north] == 0;
                var frontier = control[east] != controlId || control[west] != controlId ||
                    control[south] != controlId || control[north] != controlId;
                var regionLine = states[east] != states[index] || states[west] != states[index] ||
                    states[south] != states[index] || states[north] != states[index];
                var cell = (y / PoliticalScale) * politicalWidth + x / PoliticalScale;
                // Geography is kept for the whole block, whichever of its cells speaks for it:
                // a shore stays a shore and a region line a region line whoever comes to own them.
                if (shore) plateGeography[cell] |= Simulation.PoliticalPlate.Coast;
                if (regionLine) plateGeography[cell] |= Simulation.PoliticalPlate.RegionLine;
                // A cell on a frontier speaks for its block before one on a region line, and that
                // before one inland, so a thin country is not averaged out of the far map. This
                // only chooses the speaker: whether the block is DRAWN as a border is decided by
                // the painter, from who owns the speakers now.
                var cellClass = (byte)(frontier ? 3 : regionLine ? 2 : 1);
                if (cellClass <= plateClass[cell]) continue;
                plateClass[cell] = cellClass;
                plateProvinces[cell] = (ushort)provinceId;
                plateHolders[cell] = controlId;
            }
        }

        // The opening plate is the painter's first run, on the owners the raster was drawn with -
        // not a separate picture. Every later plate is the same routine on the owners of the day
        // (GlobePlateRepaint), so a border is shaded by one rule whenever it was drawn.
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
            PlateSource = plateSource,
            PlateOwners = plateOwners,
            FrontierAuthored = frontierAuthored,
            Frontier = FrontierImageOrNull(frontierWork),
            RegionIds = regionIds,
            ProvinceIds = provinceIds,
            NationIds = nationIds,
            HydrologyPixels = surfaceWork.GetAwaiter().GetResult(),
            RiverCentrePixels = centreWork.GetAwaiter().GetResult(),
            Provinces = provinces,
            LabelAnchors = labelAnchors,
            RegionAnchors = regionAnchors,
            RiverAnchors = riverAnchors,
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
        public Simulation.PoliticalPlateSource PlateSource;
        public ushort[] PlateOwners;
        public ushort[] FrontierAuthored;
        public Image Frontier;
        public byte[] RegionIds;
        public byte[] ProvinceIds;
        public byte[] NationIds;
        public byte[] HydrologyPixels;
        public byte[] RiverCentrePixels;
        public Simulation.ProvinceTopology Provinces;
        public Dictionary<int, TerritoryLabelAnchor> LabelAnchors;
        public Dictionary<int, TerritoryLabelAnchor> RegionAnchors;
        public IReadOnlyList<RiverLabelAnchor> RiverAnchors;
        public bool ProvinceRasterLoaded;
        public string ProvinceSource = "";
        public string HydrologySource = "";
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
