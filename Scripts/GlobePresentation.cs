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
    private const float MinimumDistance = 6.2f;
    private const float MaximumDistance = 24f;

    private readonly Dictionary<int, uint> _colorByMapIndex = new();
    private readonly Dictionary<int, Simulation.Nation> _nationByMapIndex = new();
    private readonly Dictionary<int, Simulation.ScenarioState> _stateByMapIndex = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _capitalMarkers = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _armyMarkers = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _fleetMarkers = new();
    private readonly Dictionary<Node3D, IReadOnlyList<Simulation.Army>> _armyByMarker = new();
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
    private Texture2D _moveOrderTexture = null!;
    private Texture2D _preparedOrderTexture = null!;
    private Texture2D _attackOrderTexture = null!;
    private ImmediateMesh _frontMesh = null!;
    private MeshInstance3D _frontPreview = null!;
    private ImageTexture _controllerTexture;
    private ImageTexture _frontTexture;
    private int _controlRevision = -1;
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
    private int _selectedArmyId = -1;
    private int _armyStructureFingerprint = int.MinValue;
    private int _routeFingerprint = int.MinValue;
    private Texture2D _divisionTexture = null!;
    private Task<MapBuildResult> _mapTask;
    private MapBuildResult _pendingMapResult;
    private bool _mapFailureReported;
    private int _mapApplyStage;
    private double _militaryVisualAccumulator;
    private Simulation.ProvinceTopology _provinceTopology;
    private Simulation.Campaign _campaign;
    private bool _mapApplied;
    private bool _dragging;
    private bool _boxSelecting;
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
    public event Action<Simulation.Army> ArmyPicked;
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

    private bool _painting;
    private bool _paintingClaims;
    private int _lastPaintedProvince = -1;
    private readonly HashSet<int> _peacePreview = new();
    private ushort _peacePreviewOwner;
    private readonly HashSet<int> _planPreview = new();
    private int _planPreviewKind;

    /// <summary>
    /// The stroke in progress. A drawn line only means anything if the player can see it
    /// while they are drawing it, so the brush writes into the same overlay the committed
    /// lines use and the map answers under the cursor rather than after an Apply.
    /// </summary>
    public void SetPlanPreview(IEnumerable<int> provinces, int kind)
    {
        _planPreview.Clear();
        if (provinces is not null) foreach (var province in provinces) _planPreview.Add(province);
        _planPreviewKind = kind;
        BuildFrontPreview();
    }

    public void ClearPlanPreview()
    {
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
        var repairs = Simulation.TerritorialTopology.Apply(
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

        BuildCapitalMarkers(campaign);
        BuildArmyMarkers(campaign);
        BuildFleetMarkers(campaign);
        if (campaign.Player is { } player && !string.IsNullOrWhiteSpace(player.CapitalName))
            FocusOn(player.CapitalLatitude, player.CapitalLongitude, 15.8f);

        if (_mapTask is null)
        {
            var colors = new Dictionary<int, uint>(_colorByMapIndex);
            _mapTask = Task.Run(() => BuildPoliticalMap(colors));
        }
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
            Fov = 38f,
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
        _capitalTexture = GD.Load<Texture2D>("res://Assets/Textures/LegacyGlobality/capital_star.png");
        _armyRoot = new Node3D { Name = "ArmyMarkers" };
        world.AddChild(_armyRoot);
        _divisionTexture = GD.Load<Texture2D>("res://Assets/Textures/LegacyGlobality/division_camo.png");
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
        if (_freeCamera) UpdateFreeCamera(delta);
        else UpdateOrbitCamera(delta);

        var detailMix = _flatMap
            ? Mathf.Clamp((11.5f - _flatZoom) / 4.2f, 0f, 1f)
            : Mathf.Clamp((15f - _distance) / 4f, 0f, 1f);
        var provinceDetail = _flatMap
            ? Mathf.Clamp((6.8f - _flatZoom) / 2.6f, 0f, 1f)
            : Mathf.Clamp((9.3f - _distance) / 2.8f, 0f, 1f);
        _globeMaterial?.SetShaderParameter("detail_mix", detailMix);
        // Tactical boundaries should emerge only at operational zoom. Keeping them
        // faint at country-selection scale avoids turning the map into a crosshatch.
        _globeMaterial?.SetShaderParameter("province_detail", provinceDetail);
        UpdateMarkerVisibility(_capitalRoot, _capitalMarkers, _distance <= 13.8f);
        var armyLod = _flatMap ? _flatZoom <= 7.4f : _distance <= 10.8f;
        _militaryVisualAccumulator += delta;
        if (_mapApplied && _campaign is not null && _militaryVisualAccumulator >= 0.12)
        {
            _militaryVisualAccumulator = 0;
            SyncArmyMarkers();
            UpdateArmyMarkerPositions();
            UpdateMilitaryPresentation();
        }
        UpdateMarkerVisibility(_armyRoot, _armyMarkers, armyLod);
        UpdateMarkerVisibility(_fleetRoot, _fleetMarkers, _distance <= 13.5f);
        UpdateRoutePreview();
        if (_mapApplied && _campaign is not null && _controlRevision != _campaign.MapRevision)
            RefreshProvinceControllers();
        if (IsInstanceValid(_frontPreview)) _frontPreview.Visible = false;
        UpdateCountryLabelVisibility();
        if (_flatMap) UpdateFlatWorldPositions();
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
            GD.PushError($"[dof] authored province map is required: {reason}");
        }
        if (!_mapApplied && _pendingMapResult is not null) ApplyPoliticalMapStage();
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
                var crisp = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.Rgba8, result.PoliticalPixels);
                _globeMaterial.SetShaderParameter("political_detail", ImageTexture.CreateFromImage(crisp));
                // The distant layer does not need the full tactical resolution. A
                // compact mipmapped copy removes shimmer without duplicating an
                // 88 MB mip chain for the close-range political texture.
                var smooth = crisp.Duplicate() as Image;
                smooth.Resize(MapWidth / 4, MapHeight / 4, Image.Interpolation.Lanczos);
                smooth.GenerateMipmaps();
                _globeMaterial.SetShaderParameter("political_map", ImageTexture.CreateFromImage(smooth));
                break;
            }
            case 1:
                _globeMaterial.SetShaderParameter("region_ids", ImageTexture.CreateFromImage(Image.CreateFromData(
                    MapWidth, MapHeight, false, Image.Format.Rg8, result.RegionIds)));
                break;
            case 2:
                _globeMaterial.SetShaderParameter("province_ids", ImageTexture.CreateFromImage(Image.CreateFromData(
                    MapWidth, MapHeight, false, Image.Format.Rg8, result.ProvinceIds)));
                break;
            case 3:
                _globeMaterial.SetShaderParameter("nation_ids", ImageTexture.CreateFromImage(Image.CreateFromData(
                    MapWidth, MapHeight, false, Image.Format.Rg8, result.NationIds)));
                break;
            case 4:
                _globeMaterial.SetShaderParameter("hydrology_map", ImageTexture.CreateFromImage(Image.CreateFromData(
                    MapWidth, MapHeight, false, Image.Format.Rg8, result.HydrologyPixels)));
                break;
            default:
                _provinceTopology = result.Provinces;
                if (result.ProvinceRasterLoaded) GD.Print($"[dof] province data map loaded: {result.ProvinceSource}");
                else GD.PushWarning($"[dof] province data map fallback: {result.ProvinceSource}");
                GD.Print($"[dof] hydrology data source: {result.HydrologySource}");
                BindArmyTopology();
                BuildArmyMarkers(_campaign);
                BuildCountryLabels(result.LabelAnchors);
                BuildFrontPreview();
                RefreshProvinceControllers();
                _pendingMapResult = null;
                _mapApplied = true;
                GD.Print($"[dof] tactical province topology ready: {_provinceTopology.ProvinceCount:N0} land provinces, " +
                         $"{_provinceTopology.RiverCrossingCount:N0} river crossings, " +
                         $"{Simulation.HydrologyCatalog.RiverFeatures.Count:N0} rivers and {Simulation.HydrologyCatalog.LakeFeatures.Count:N0} lakes");
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
            _targetFlatZoom = Mathf.Clamp((distance - MinimumDistance) / (MaximumDistance - MinimumDistance) * 8f + 2.4f,
                MinimumFlatZoom, MaximumFlatZoom);
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

        _stateByMapIndex.TryGetValue(SourceEarthData.States[cell], out var state);
        if (updateSelection)
        {
            _globeMaterial.SetShaderParameter("selected_region_id", (float)SourceEarthData.States[cell]);
            _globeMaterial.SetShaderParameter("selected_province_id", (float)provinceId);
            _globeMaterial.SetShaderParameter("selection_active", 1f);
        }
        pick = new GlobePick(nation, state, provinceId, latitude, longitude);
        return true;
    }

    private bool TryPickArmyStack(Vector2 pixel, out IReadOnlyList<Simulation.Army> armies)
    {
        armies = null;
        if (_distance > 13.5f || !IsInstanceValid(_camera)) return false;
        var bestDistance = 28f * 28f;
        foreach (var (node, candidates) in _armyByMarker)
        {
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            var screen = _camera.UnprojectPosition(node.GlobalPosition);
            var distance = screen.DistanceSquaredTo(pixel);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            armies = candidates;
        }
        return armies is not null && armies.Count > 0;
    }

    public void SelectArmy(int armyId)
    {
        SelectArmies(new[] { armyId });
    }

    public void SelectArmies(IEnumerable<int> armyIds)
    {
        var selected = armyIds.ToHashSet();
        _selectedArmyIds.Clear();
        _selectedArmyIds.UnionWith(selected);
        _selectedArmyId = selected.Count == 1 ? selected.First() : -1;
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
        var atlas = GD.Load<Texture2D>("res://Assets/Textures/UI/army_plan_arrow_textures.png");
        _moveOrderTexture = AtlasSlice(atlas, new Rect2(0f, 42f, 1672f, 270f));
        _preparedOrderTexture = AtlasSlice(atlas, new Rect2(0f, 322f, 1672f, 270f));
        _attackOrderTexture = AtlasSlice(atlas, new Rect2(0f, 604f, 1672f, 275f));
        _routePreview = new MeshInstance3D
        {
            Name = "SelectedArmyRoute",
            Mesh = _routeMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        world.AddChild(_routePreview);
    }

    private static AtlasTexture AtlasSlice(Texture2D atlas, Rect2 region) => new()
    {
        Atlas = atlas,
        Region = region,
        FilterClip = true
    };

    private void CreateFrontPreview(Node3D world)
    {
        _frontMesh = new ImmediateMesh();
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            NoDepthTest = false,
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
            var code = PlanCode(line.Kind);
            foreach (var province in line.Provinces)
            {
                if (province <= 0 || province >= 256 * 256) continue;
                pixels[province * 4 + 2] = code;
            }
        }
        if (_planPreview.Count > 0)
        {
            var code = (byte)(Mathf.Clamp(_planPreviewKind + 1, 1, 3) * 64);
            foreach (var province in _planPreview)
            {
                if (province <= 0 || province >= 256 * 256) continue;
                pixels[province * 4 + 2] = code;
            }
        }

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
        _ => 192
    };

    private void UpdateRoutePreview()
    {
        if (_campaign?.ProvinceMap is null || !IsInstanceValid(_routePreview)) return;
        var army = _campaign.Armies.Find(candidate => candidate.Id == _selectedArmyId);
        var fingerprint = army is null ? -1 : HashCode.Combine(army.ProvinceId, army.Route.Count,
            army.Route.Count == 0 ? -1 : army.Route[^1], army.OrderKind, army.ObjectiveStateMapIndex,
            army.PlanActive, army.OrderProvinceId);
        if (fingerprint == _routeFingerprint) return;
        _routeFingerprint = fingerprint;
        _routeMesh.ClearSurfaces();
        var directObjective = army is not null &&
            army.OrderKind is Simulation.ArmyOrder.AdvanceFront or Simulation.ArmyOrder.Spearhead &&
            army.OrderProvinceId > 0;
        _routePreview.Visible = army is not null && (army.Route.Count > 0 || directObjective);
        if (!_routePreview.Visible) return;

        var routeColor = army.OrderKind switch
        {
            Simulation.ArmyOrder.DefendFront => new Color(0.30f, 0.68f, 1f, 0.92f),
            Simulation.ArmyOrder.ReserveFront => new Color(0.70f, 0.74f, 0.78f, 0.84f),
            _ => Colors.White
        };
        var texture = army.OrderKind is Simulation.ArmyOrder.AdvanceFront or Simulation.ArmyOrder.Spearhead
            ? army.PlanActive ? _attackOrderTexture : _preparedOrderTexture
            : _moveOrderTexture;
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            NoDepthTest = false,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoTexture = texture,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var points = new List<Vector3> { PresentationPoint(army.Latitude, army.Longitude, SurfaceLineRadius) };
        foreach (var province in army.Route)
        {
            var center = _campaign.ProvinceMap.CenterOf(province);
            points.Add(PresentationPoint(center.Latitude, center.Longitude, SurfaceLineRadius));
        }
        if (directObjective && (army.Route.Count == 0 || army.Route[^1] != army.OrderProvinceId))
        {
            var objective = _campaign.ProvinceMap.CenterOf(army.OrderProvinceId);
            points.Add(PresentationPoint(objective.Latitude, objective.Longitude, SurfaceLineRadius));
        }
        if (points.Count < 2) return;

        _routeMesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, material);
        for (var index = 0; index < points.Count; index++)
        {
            var previous = points[Math.Max(0, index - 1)];
            var next = points[Math.Min(points.Count - 1, index + 1)];
            var tangent = (next - previous).Normalized();
            var normal = _flatMap ? Vector3.Forward : points[index].Normalized();
            var side = normal.Cross(tangent).Normalized() * (_flatMap ? 0.020f : 0.018f);
            var u = index / (float)(points.Count - 1);
            _routeMesh.SurfaceSetColor(routeColor);
            _routeMesh.SurfaceSetUV(new Vector2(u, 0f));
            _routeMesh.SurfaceAddVertex(points[index] + side);
            _routeMesh.SurfaceSetColor(routeColor);
            _routeMesh.SurfaceSetUV(new Vector2(u, 1f));
            _routeMesh.SurfaceAddVertex(points[index] - side);
        }
        _routeMesh.SurfaceEnd();
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (_freeCamera && HandleFreeCameraInput(inputEvent)) { AcceptEvent(); return; }
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
                        if (TryPickBattle(button.Position, out var battle)) BattlePicked?.Invoke(battle);
                        else if (TryPickArmyStack(button.Position, out var stack))
                        {
                            if (stack.Count == 1) ArmyPicked?.Invoke(stack[0]);
                            else ArmiesBoxPicked?.Invoke(stack);
                        }
                        else if (TryPick(button.Position, out var pick)) CountryPicked?.Invoke(pick);
                    }
                    else
                    {
                        var selected = ArmiesInScreenRect(rect);
                        if (selected.Count > 0) ArmiesBoxPicked?.Invoke(selected);
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
                    if (wasClick && TryPick(button.Position, out var commanded, updateSelection: false))
                        ProvinceCommanded?.Invoke(commanded);
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown)
            {
                var sign = button.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
                if (_flatMap)
                    _targetFlatZoom = Mathf.Clamp(_targetFlatZoom + sign * 0.85f, MinimumFlatZoom, MaximumFlatZoom);
                else
                    _targetDistance = Mathf.Clamp(_targetDistance + sign * 1.25f, MinimumDistance, MaximumDistance);
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
            var delta = motion.Position - _previousPointer;
            _previousPointer = motion.Position;
            _dragDistance += delta.Length();
            if (_flatMap)
            {
                var unitsPerPixel = _targetFlatZoom / Math.Max(1f, Size.Y);
                _targetFlatX -= delta.X * unitsPerPixel;
                _targetFlatY += delta.Y * unitsPerPixel;
                ClampFlatCamera();
            }
            else
            {
                var scale = Mathf.Lerp(0.22f, 0.65f, (_targetDistance - MinimumDistance) / (MaximumDistance - MinimumDistance));
                _targetYaw -= delta.X * 0.006f * scale;
                _targetPitch = Mathf.Clamp(_targetPitch - delta.Y * 0.006f * scale, -1.20f, 1.20f);
            }
            AcceptEvent();
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (TryPickBattle(atPosition, out var battle))
        {
            var attacker = _campaign?.NationById(battle.AttackerId);
            var defender = _campaign?.NationById(battle.DefenderId);
            var latest = battle.Rounds.LastOrDefault();
            return $"BATTLE · province #{battle.ProvinceId:N0}\n" +
                   $"{attacker?.Name ?? "?"} vs {defender?.Name ?? "?"}\n" +
                   $"Losses {battle.AttackerLosses:N0} / {battle.DefenderLosses:N0}" +
                   (latest is null ? "" : $"\nPower {latest.AttackerPower:0.0} / {latest.DefenderPower:0.0} · plan {latest.AttackerPlanning:P0}");
        }
        if (TryPickArmyStack(atPosition, out var stack))
        {
            var lead = stack[0];
            var owner = _campaign?.NationById(lead.NationId);
            var personnel = stack.Sum(DivisionPersonnel);
            return $"{owner?.Name ?? "Unknown"} · {stack.Count} division{(stack.Count == 1 ? "" : "s")}\n" +
                   $"Personnel {personnel:N0} · organisation {stack.Average(army => army.Organisation):P0}\n" +
                   $"Equipment {stack.Average(army => army.Equipment):P0} · experience {stack.Average(army => army.Experience):P0} · " +
                   $"supply {stack.Average(army => army.SupplyLevel):P0}\n" +
                   (stack.Count == 1 ? $"{lead.OrderKind} · {lead.Stance} posture · planning {lead.Planning:P0}" :
                       "Click to select every independent division in this province.");
        }
        if (!TryPick(atPosition, out var pick)) return "";
        var state = pick.State is null ? "Unmapped administrative area" : $"{pick.State.Name} · {pick.State.Kind}";
        var controller = pick.ProvinceId > 0 && _campaign?.ProvinceController is { } controllers
            ? _nationByMapIndex.GetValueOrDefault(controllers[pick.ProvinceId])
            : pick.Nation;
        var occupation = pick.ProvinceId > 0 && _campaign?.ProvinceOccupation is { } occupations
            ? occupations[pick.ProvinceId]
            : 0f;
        var hydrology = pick.ProvinceId > 0 && _provinceTopology is not null
            ? (_provinceTopology.HasRiver(pick.ProvinceId) ? "\nRiver terrain · crossings affect march and attack" : "") +
              (_provinceTopology.HasLakeAccess(pick.ProvinceId) ? "\nLake shore · local supply access" : "")
            : "";
        return $"{pick.Nation.Name} ({pick.Nation.Tag})\n{state}\nProvince #{pick.ProvinceId:N0}" +
               (controller is null || controller == pick.Nation ? "" : $"\nControlled by {controller.Name} · occupation {occupation:P0}") +
               hydrology;
    }

    private List<Simulation.Army> ArmiesInScreenRect(Rect2 rect)
    {
        var selected = new List<Simulation.Army>();
        if (!IsInstanceValid(_camera) || _distance > 13.5f) return selected;
        foreach (var (node, armies) in _armyByMarker)
        {
            if (!IsInstanceValid(node) || !node.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (rect.HasPoint(_camera.UnprojectPosition(node.GlobalPosition))) selected.AddRange(armies);
        }
        return selected;
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
        if (IsInstanceValid(_viewport))
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
uniform sampler2D political_detail : source_color, filter_nearest;
uniform sampler2D region_ids : filter_nearest;
uniform sampler2D province_ids : filter_nearest;
uniform sampler2D nation_ids : filter_nearest;
uniform sampler2D hydrology_map : filter_linear, repeat_enable;
uniform sampler2D province_control_colors : source_color, filter_nearest, repeat_disable;
uniform sampler2D front_membership : filter_nearest, repeat_disable;
uniform sampler2D map_mode_colors : source_color, filter_nearest, repeat_disable;
// Matches the source scenario's Ocean terrain (#0f4a6b). The relief texture stays
// deliberately disabled: at strategic scale it reads as visual noise, not water.
// Darkened here to compensate for Godot's directional + ambient lighting; the final
// lit result is the source scenario's #0f4a6b rather than a washed-out cyan.
uniform vec3 ocean_color = vec3(0.012, 0.065, 0.105);
uniform float selection_active = 0.0;
uniform float selected_region_id = -1.0;
uniform float selected_province_id = -1.0;
uniform float detail_mix = 0.0;
uniform float province_detail = 0.0;
uniform float map_mode = 0.0;
float map_id(sampler2D map_texture, vec2 uv) {
    vec4 encoded = texture(map_texture, vec2(fract(uv.x), clamp(uv.y, 0.0, 1.0)));
    return floor(encoded.r * 255.0 + 0.5) + floor(encoded.g * 255.0 + 0.5) * 256.0;
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
void fragment() {
    vec4 political_smooth = texture(political_map, UV);
    vec4 political_crisp = texture(political_detail, UV);
    vec4 political = mix(political_smooth, political_crisp, detail_mix);
    float rim = pow(1.0 - max(dot(normalize(NORMAL), normalize(VIEW)), 0.0), 2.0);
    vec3 water = ocean_color * (0.72 + rim * 0.28);
    vec2 texel = 1.0 / vec2(textureSize(province_ids, 0));
    float region = map_id(region_ids, UV);
    float province = map_id(province_ids, UV);
    float nation = map_id(nation_ids, UV);
    vec4 live_control = control_color(province);
    float political_light = clamp(max(max(political.r, political.g), political.b) * 1.25, 0.58, 0.94);
    political.rgb = mix(political.rgb, live_control.rgb * political_light, live_control.a);
    vec4 strategic = map_mode_color(nation);
    political.rgb = mix(political.rgb, strategic.rgb * mix(0.84, 1.02, political_light),
        map_mode * strategic.a);
    vec3 base = mix(water, political.rgb, political.a);
    vec2 hydrography = texture(hydrology_map, UV).rg;
    float lake_water = smoothstep(0.14, 0.62, hydrography.r);
    float river_water = smoothstep(0.18, 0.70, hydrography.g);
    vec3 inland_water = vec3(0.018, 0.17, 0.25);
    base = mix(base, inland_water, lake_water * 0.94);
    base = mix(base, vec3(0.025, 0.24, 0.34), river_water * mix(0.42, 0.88, detail_mix));
    float region_selected = (abs(region - selected_region_id) < 0.5 ? 1.0 : 0.0) * selection_active * political.a;
    float province_selected = (abs(province - selected_province_id) < 0.5 ? 1.0 : 0.0) * selection_active * political.a;
    float region_edge = max(max(
        step(0.5, abs(region - map_id(region_ids, UV + vec2(texel.x, 0.0)))),
        step(0.5, abs(region - map_id(region_ids, UV - vec2(texel.x, 0.0))))), max(
        step(0.5, abs(region - map_id(region_ids, UV + vec2(0.0, texel.y)))),
        step(0.5, abs(region - map_id(region_ids, UV - vec2(0.0, texel.y))))));
    float province_edge = max(max(
        step(0.5, abs(province - map_id(province_ids, UV + vec2(texel.x, 0.0)))),
        step(0.5, abs(province - map_id(province_ids, UV - vec2(texel.x, 0.0))))), max(
        step(0.5, abs(province - map_id(province_ids, UV + vec2(0.0, texel.y)))),
        step(0.5, abs(province - map_id(province_ids, UV - vec2(0.0, texel.y))))));
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

    // Tactical borders should read as fine cartography, not dark fish-scales.
    // Selection remains strong below; the idle network stays deliberately subtle.
    base = mix(base, vec3(0.10, 0.13, 0.16), province_edge * province_detail * political.a * 0.21);
    // A line the player drew. Blue holds it, amber takes it, grey falls back to it.
    float plan = round(front.b * 4.0);
    vec3 plan_color = plan < 1.5 ? vec3(0.24, 0.56, 0.96)
        : (plan < 2.5 ? vec3(0.96, 0.52, 0.16) : vec3(0.70, 0.73, 0.78));
    float plan_on = step(0.5, plan) * max(province_detail, detail_mix * 0.70);
    base = mix(base, plan_color, plan_on * 0.40);
    base = mix(base, vec3(1.0, 0.22, 0.10), front_edge * max(province_detail, detail_mix * 0.70) * 0.94);
    base = mix(base, vec3(0.93, 0.16, 0.07), front_outer * max(province_detail, detail_mix * 0.70) * 0.38);
    base = mix(base, vec3(0.95, 0.76, 0.25), region_selected * 0.10);
    base = mix(base, vec3(1.0, 0.84, 0.30), province_selected * province_detail * 0.16);
    float selection_edge = max(region_edge * region_selected, province_edge * province_selected * province_detail);
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
        _globeMaterial.SetShaderParameter("political_detail", placeholder);
        var emptyIds = ImageTexture.CreateFromImage(Image.CreateEmpty(2, 2, false, Image.Format.Rg8));
        _globeMaterial.SetShaderParameter("region_ids", emptyIds);
        _globeMaterial.SetShaderParameter("province_ids", emptyIds);
        _globeMaterial.SetShaderParameter("nation_ids", emptyIds);
        _globeMaterial.SetShaderParameter("hydrology_map", emptyIds);
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

    private static Node3D CreateSpace(Node3D world)
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
            AlbedoTexture = GD.Load<Texture2D>("res://Assets/Textures/LegacyGlobality/space_background.png"),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Front,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic
        };
        space.Material = material;
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

        if (_flatMap)
        {
            var flatSpeed = _flatZoom * 0.62f * (float)delta;
            if (Input.IsKeyPressed(Key.W)) _targetFlatY += flatSpeed;
            if (Input.IsKeyPressed(Key.S)) _targetFlatY -= flatSpeed;
            if (Input.IsKeyPressed(Key.A)) _targetFlatX -= flatSpeed;
            if (Input.IsKeyPressed(Key.D)) _targetFlatX += flatSpeed;
            ClampFlatCamera();
        }
        else
        {
            if (Input.IsKeyPressed(Key.W)) _targetPitch = Mathf.Clamp(_targetPitch + (float)delta * 0.7f, -1.20f, 1.20f);
            if (Input.IsKeyPressed(Key.S)) _targetPitch = Mathf.Clamp(_targetPitch - (float)delta * 0.7f, -1.20f, 1.20f);
            if (Input.IsKeyPressed(Key.A)) _targetYaw += (float)delta * 0.7f;
            if (Input.IsKeyPressed(Key.D)) _targetYaw -= (float)delta * 0.7f;
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
            _camera.TopLevel = true;
            Input.MouseMode = Input.MouseModeEnum.Captured;
            ApplyFreeCamera();
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
        var speed = _freeSpeed * Mathf.Clamp(altitude / 6f, 0.10f, 6f);
        if (Input.IsKeyPressed(Key.Shift)) speed *= 4.5f;
        if (Input.IsKeyPressed(Key.Alt)) speed *= 0.22f;

        if (move.LengthSquared() > 0.0001f)
            _freePosition += move.Normalized() * speed * (float)delta;

        // Everything downstream sizes labels, markers and province detail by _distance.
        // Feeding it the flying camera's own height keeps the map behaving the way it
        // does under the orbit rig instead of freezing at whatever zoom it left.
        _distance = _freePosition.Length();
        _targetDistance = _distance;
        ApplyFreeCamera();
    }

    private void ApplyFreeCamera()
    {
        if (!IsInstanceValid(_camera)) return;
        _camera.Projection = Camera3D.ProjectionType.Perspective;
        _camera.GlobalTransform = new Transform3D(
            new Basis(Quaternion.FromEuler(new Vector3(_freePitch, _freeYaw, 0f))), _freePosition);
    }

    /// <summary>
    /// Look and throttle. Returns true when the event belonged to the free camera, so
    /// picking, box-select and orbit dragging stay out of the way while it is flying.
    /// </summary>
    private bool HandleFreeCameraInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseMotion look && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _freeYaw -= look.Relative.X * FreeLookSensitivity;
            _freePitch = Mathf.Clamp(_freePitch - look.Relative.Y * FreeLookSensitivity, -1.45f, 1.45f);
            ApplyFreeCamera();
            return true;
        }

        if (inputEvent is InputEventMouseButton wheel && wheel.Pressed &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            var step = wheel.ButtonIndex == MouseButton.WheelUp ? 1.22f : 1f / 1.22f;
            _freeSpeed = Mathf.Clamp(_freeSpeed * step, 0.4f, 90f);
            return true;
        }

        return false;
    }

    private const float FreeLookSensitivity = 0.0032f;

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
            foreach (var marker in markers)
                if (IsInstanceValid(marker.Node)) marker.Node.Visible = true;
            return;
        }
        var viewNormal = _camera.GlobalPosition.Normalized();
        foreach (var marker in markers)
            if (IsInstanceValid(marker.Node))
                marker.Node.Visible = marker.Normal.Dot(viewNormal) > 0.18f;
    }

    private void BuildArmyMarkers(Simulation.Campaign campaign)
    {
        if (!IsInstanceValid(_armyRoot) || _divisionTexture is null) return;

        foreach (var child in _armyRoot.GetChildren())
            child.QueueFree();
        _armyMarkers.Clear();
        _armyByMarker.Clear();

        // Divisions are independent simulation entities. Presentation is deliberately
        // coarser for foreign forces: otherwise modern active-personnel totals create
        // thousands of Label3D/material instances and exhaust the renderer descriptor
        // heap. The player's divisions retain province precision for direct control;
        // foreign divisions form one readable operational stack per state.
        var stacks = campaign.Armies.GroupBy(army => ArmyPresentationBucket(campaign, army));
        foreach (var grouped in stacks)
        {
            var stack = grouped.OrderBy(army => army.Id).ToArray();
            var lead = stack[0];
            var nation = campaign.NationById(lead.NationId);
            if (nation is null) continue;
            var location = AverageArmyLocation(stack);
            var templateId = stack.GroupBy(army => army.TemplateId)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First().Key;

            var marker = new Node3D { Name = $"DivisionStack_{nation.Tag}_{lead.ProvinceId}_{lead.Id}" };
            marker.Position = PresentationPoint(location.Latitude, location.Longitude, SurfaceMarkerRadius);
            _armyRoot.AddChild(marker);

            var plate = new Sprite3D
            {
                Texture = ArmyCounterTexture(nation, templateId),
                PixelSize = 0.000115f,
                FixedSize = true,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = Colors.White
            };
            marker.AddChild(plate);

            var divisions = new Label3D
            {
                Name = "DivisionCount",
                Text = Mathf.CeilToInt(stack.Sum(army => army.Divisions)).ToString(),
                FontSize = 31,
                PixelSize = 0.00030f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                OutlineSize = 8,
                Modulate = Ui.Colour("globe.label"),
                OutlineModulate = Ui.Colour("globe.label_outline"),
                Position = new Vector3(0f, 0f, 0.004f),
                NoDepthTest = true
            };
            marker.AddChild(divisions);

            var status = new Label3D
            {
                Name = "OperationalStatus",
                Text = $"{stack.Sum(DivisionPersonnel) / 1000f:0.#}k",
                FontSize = 18,
                PixelSize = 0.00023f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                OutlineSize = 6,
                Modulate = Ui.Colour("globe.label_minor"),
                OutlineModulate = Ui.Colour("globe.label_minor_outline"),
                Position = new Vector3(0f, -0.025f, 0.005f),
                NoDepthTest = true
            };
            marker.AddChild(status);
            _armyMarkers.Add((marker, marker.Position.Normalized()));
            _armyByMarker[marker] = stack;
        }
        _armyStructureFingerprint = ArmyStructureFingerprint(campaign);
    }

    private static (int NationId, int LocationId, int FallbackLatitude, int FallbackLongitude)
        ArmyPresentationBucket(Simulation.Campaign campaign, Simulation.Army division)
    {
        if (division.NationId == campaign.Player?.Id && division.ProvinceId > 0)
            return (division.NationId, division.ProvinceId, 0, 0);
        if (division.ArmyGroupId >= 0)
            return (division.NationId, -2_000_000 - division.ArmyGroupId, 0, 0);
        if (division.StateMapIndex >= 0)
            return (division.NationId, -(division.StateMapIndex + 1), 0, 0);
        return (division.NationId, 0, Mathf.RoundToInt(division.Latitude * 20f),
            Mathf.RoundToInt(division.Longitude * 20f));
    }

    private static (float Latitude, float Longitude) AverageArmyLocation(IReadOnlyList<Simulation.Army> stack)
    {
        if (stack.Count == 0) return (0f, 0f);
        var latitude = stack.Average(division => division.Latitude);
        var longitudeSin = stack.Average(division => MathF.Sin(Mathf.DegToRad(division.Longitude)));
        var longitudeCos = stack.Average(division => MathF.Cos(Mathf.DegToRad(division.Longitude)));
        return ((float)latitude, Mathf.RadToDeg(MathF.Atan2((float)longitudeSin, (float)longitudeCos)));
    }

    private void SyncArmyMarkers()
    {
        var fingerprint = ArmyStructureFingerprint(_campaign);
        if (fingerprint == _armyStructureFingerprint) return;
        _selectedArmyIds.RemoveWhere(id => _campaign.Armies.All(army => army.Id != id));
        BuildArmyMarkers(_campaign);
    }

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
        foreach (var fleet in campaign.Fleets)
        {
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null) continue;
            var marker = new Node3D { Name = $"Fleet_{fleet.Id}_{nation.Tag}" };
            marker.Position = SurfacePoint(fleet.Latitude, fleet.Longitude, SurfaceMarkerRadius);
            _fleetRoot.AddChild(marker);
            var plate = new Sprite3D
            {
                Texture = _fleetTexture,
                PixelSize = 0.00011f,
                FixedSize = true,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = new Color(0.60f, 0.82f, 0.90f, 0.86f)
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
        }
    }

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

    private void BindArmyTopology()
    {
        if (_campaign is null || _provinceTopology is null) return;
        foreach (var army in _campaign.Armies)
        {
            var x = Mathf.Clamp((int)((army.Longitude + 180f) / 360f * SourceEarthData.Width), 0, SourceEarthData.Width - 1);
            var y = Mathf.Clamp((int)((90f - army.Latitude) / 180f * SourceEarthData.Height), 0, SourceEarthData.Height - 1);
            var cell = y * SourceEarthData.Width + x;
            army.StateMapIndex = SourceEarthData.States[cell];
            army.ProvinceId = _provinceTopology.CellToProvince[cell];
        }
        _campaign.AttachProvinceTopology(_provinceTopology);
        GD.Print($"[dof] province graph ready: {_provinceTopology.ProvinceCount:N0} nodes, {_provinceTopology.EdgeCount:N0} edges");
        foreach (var war in _campaign.Wars)
            GD.Print($"[dof] front {war.Name}: {war.AttackerFront.Count:N0}/{war.DefenderFront.Count:N0} provinces");
    }

    private void RefreshProvinceControllers()
    {
        if (_campaign?.ProvinceMap is null || _campaign.ProvinceController is null || _globeMaterial is null) return;
        var topology = _campaign.ProvinceMap;
        var pixels = _controlPixels ??= new byte[256 * 256 * 4];
        Array.Clear(pixels);
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var controller = _campaign.ProvinceController[province];
            if (controller == topology.ProvinceToNation[province]) continue;
            var packed = _colorByMapIndex.TryGetValue(controller, out var color) ? color : 0x6E7B8BFFu;
            var write = province * 4;
            pixels[write] = (byte)((packed >> 24) & 0xFF);
            pixels[write + 1] = (byte)((packed >> 16) & 0xFF);
            pixels[write + 2] = (byte)((packed >> 8) & 0xFF);
            pixels[write + 3] = 255;
        }
        // The peace being negotiated is drawn over the controller map, so the player sees
        // the border they are proposing rather than the one that exists.
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
        _controlRevision = _campaign.MapRevision;
        BuildFrontPreview();
        _frontRevision = _controlRevision;
    }

    private void UpdateArmyMarkerPositions()
    {
        foreach (var (marker, armies) in _armyByMarker)
        {
            if (!IsInstanceValid(marker) || armies.Count == 0) continue;
            var count = marker.GetNodeOrNull<Label3D>("DivisionCount");
            var personnel = marker.GetNodeOrNull<Label3D>("OperationalStatus");
            var location = AverageArmyLocation(armies);
            marker.Position = PresentationPoint(location.Latitude, location.Longitude, SurfaceMarkerRadius);
            var countText = armies.Count.ToString();
            var personnelText = $"{armies.Sum(DivisionPersonnel) / 1000f:0.#}k";
            if (count is not null && count.Text != countText) count.Text = countText;
            if (personnel is not null && personnel.Text != personnelText) personnel.Text = personnelText;
        }
    }

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

    private void BuildCountryLabels(Dictionary<int, TerritoryLabelAnchor> anchors)
    {
        if (!IsInstanceValid(_countryLabelRoot)) return;
        foreach (var child in _countryLabelRoot.GetChildren()) child.QueueFree();
        _countryLabels.Clear();
        _countryGlyphs.Clear();

        foreach (var (mapIndex, anchor) in anchors)
        {
            if (!_nationByMapIndex.TryGetValue(mapIndex, out var nation) || anchor.Cells < 80)
                continue;

            var text = nation.Name.ToUpperInvariant();
            var group = new Node3D { Name = $"CountryLabel_{nation.Tag}" };
            _countryLabelRoot.AddChild(group);

            // The label occupies the largest connected land mass only. Every glyph
            // receives its own geographic coordinate and tangent basis, so the name
            // is physically attached to the map instead of acting as a camera-facing
            // nameplate. The same coordinates form a static baseline on the flat map.
            var latitudeRadians = Mathf.DegToRad(anchor.Latitude);
            var longitudeDegreesPerCell = 360f / SourceEarthData.Width;
            var latitudeDegreesPerCell = 180f / SourceEarthData.Height;
            var axisLongitudeDegrees = anchor.AxisX * anchor.HalfSpanPixels * longitudeDegreesPerCell;
            var axisLatitudeDegrees = -anchor.AxisY * anchor.HalfSpanPixels * latitudeDegreesPerCell;
            var physicalSpanRadians = Mathf.DegToRad(2f * Mathf.Sqrt(
                Mathf.Pow(axisLongitudeDegrees * Mathf.Cos(latitudeRadians), 2f) +
                axisLatitudeDegrees * axisLatitudeDegrees));
            var physicalSpan = GlobeRadius * physicalSpanRadians;
            var minorDegrees = anchor.MinorSpanPixels * latitudeDegreesPerCell;
            var desiredHeight = GlobeRadius * Mathf.DegToRad(Mathf.Clamp(minorDegrees * 0.15f, 0.26f, 1.05f));
            const int fontSize = 64;
            var targetSpan = Math.Max(0.025f, physicalSpan * 0.88f);
            var pixelSize = Mathf.Clamp(desiredHeight / fontSize, 0.00030f, 0.00190f);
            var visibleGlyphs = Math.Max(1, text.Count(character => character != ' '));
            pixelSize = Mathf.Min(pixelSize, targetSpan / (fontSize * visibleGlyphs * 0.72f));
            pixelSize = Mathf.Max(pixelSize, 0.00022f);
            var flatRotation = -Mathf.Atan2(anchor.AxisY, anchor.AxisX);

            // One tangent-space label per country is both more readable and vastly
            // cheaper than one Label3D per glyph. Horizontal scale stretches the
            // normal word across the dominant land component without inserting
            // artificial spaces or rotating individual letters diagonally.
            var surfaceBasis = CountryLabelSurfaceBasis(
                anchor.Latitude, anchor.Longitude, anchor.AxisX, anchor.AxisY);
            var naturalWidth = Math.Max(0.02f, fontSize * pixelSize * Math.Max(1, text.Length) * 0.56f);
            var horizontalScale = Mathf.Clamp(targetSpan / naturalWidth, 0.62f, 3.2f);
            var label = new Label3D
            {
                Name = "CountryName",
                Text = text,
                FontSize = fontSize,
                PixelSize = pixelSize,
                FixedSize = false,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                OutlineSize = 9,
                Modulate = new Color(0.87f, 0.89f, 0.92f, 0.84f),
                OutlineModulate = new Color(0.025f, 0.038f, 0.055f, 0.94f),
                NoDepthTest = false,
                Position = SurfacePoint(anchor.Latitude, anchor.Longitude, SurfaceTextRadius),
                Basis = surfaceBasis,
                Scale = new Vector3(horizontalScale, 1f, 1f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            group.AddChild(label);
            _countryGlyphs.Add((label, anchor.Latitude, anchor.Longitude, surfaceBasis, flatRotation));

            var center = SurfacePoint(anchor.Latitude, anchor.Longitude, SurfaceTextRadius);
            _countryLabels.Add((group, center.Normalized(), anchor.Cells));
        }
    }

    private void UpdateCountryLabelVisibility()
    {
        if (!IsInstanceValid(_countryLabelRoot) || !IsInstanceValid(_camera)) return;
        // At tactical zoom, country names yield to states/provinces and unit
        // counters. Keeping continent-scale typography there would cover the map.
        _countryLabelRoot.Visible = _flatMap
            ? _flatZoom is >= 4.4f and <= 10.8f
            : _distance is >= 7.9f and <= 16.5f;
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
        return new MapBuildResult(pixels, regionIds, provinceIds, nationIds, hydrology.Pixels, provinces, labelAnchors,
            true, provinceSource, hydrology.Source);
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
            var targetX = ((int)Math.Round(meanX) % width + width) % width;
            var targetY = (int)Math.Round(meanY);
            var best = queue[0];
            var bestDistance = long.MaxValue;
            foreach (var cell in queue)
            {
                var x = cell % width;
                var y = cell / width;
                var dx = Math.Abs(x - targetX);
                dx = Math.Min(dx, width - dx);
                var dy = y - targetY;
                var score = (long)dx * dx + (long)dy * dy;
                if (score >= bestDistance) continue;
                bestDistance = score;
                best = cell;
            }
            var bestX = best % width;
            var bestY = best / width;

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
            // Grand-strategy cartography keeps names readable left-to-right even for
            // tall countries. The territory shape may bend the baseline, but it must
            // never become a vertical stack of letters.
            // A small cartographic slant is useful, but steep PCA baselines make
            // individual glyphs read like a staircase. Paradox-style map text stays
            // overwhelmingly horizontal and lets font size do the fitting instead.
            angle = Math.Clamp(angle, -0.12, 0.12);
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

            var bestDx = bestX - startX;
            if (bestDx > width / 2) bestDx -= width;
            else if (bestDx < -width / 2) bestDx += width;
            anchors[nation] = new TerritoryLabelAnchor(
                90f - (bestY + 0.5f) * 180f / height,
                -180f + (bestX + 0.5f) * 360f / width,
                startX + bestDx,
                bestY,
                axisX,
                axisY,
                Math.Max(4f, (float)(maxMajor - minMajor) * 0.42f),
                Math.Max(2f, (float)(maxMinor - minMinor)),
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

    private sealed record MapBuildResult(
        byte[] PoliticalPixels,
        byte[] RegionIds,
        byte[] ProvinceIds,
        byte[] NationIds,
        byte[] HydrologyPixels,
        Simulation.ProvinceTopology Provinces,
        Dictionary<int, TerritoryLabelAnchor> LabelAnchors,
        bool ProvinceRasterLoaded,
        string ProvinceSource,
        string HydrologySource);

    private readonly record struct TerritoryLabelAnchor(
        float Latitude,
        float Longitude,
        float CenterX,
        float CenterY,
        float AxisX,
        float AxisY,
        float HalfSpanPixels,
        float MinorSpanPixels,
        int Cells);

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
