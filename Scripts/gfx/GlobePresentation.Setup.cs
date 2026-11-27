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

public sealed partial class GlobePresentation
{
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
        ? _mapWorkerStage
        : _mapApplyStage < 5 ? "Uploading map layers"
        : _mapApplyStage == 5 ? "Preparing terrain" : "Placing armies and country names";
    // Stage completion, not a time estimate. Never pretend to measure the worker's bytes.
    public double MapLoadingProgress => _mapApplied ? 100
        : _pendingMapResult is null ? _mapWorkerProgress : 55 + _mapApplyStage * 6;
    private static bool HasBootTopologyMarkers(ushort[] states, int width, int height)
    {
        var vatIdx = Math.Clamp((int)((90f - 41.9029f) / 180f * height), 0, height - 1) * width +
                     Math.Clamp((int)((12.4534f + 180f) / 360f * width), 0, width - 1);
        var gibIdx = Math.Clamp((int)((90f - 36.1408f) / 180f * height), 0, height - 1) * width +
                     Math.Clamp((int)((-5.3536f + 180f) / 360f * width), 0, width - 1);
        return states[vatIdx] == 1717 && states[gibIdx] == 1719;
    }

    public void BindCampaign(Simulation.Campaign campaign)
    {
        CancelPendingMapSelection();
        _campaign = campaign;
        // A mushroom cloud queued by the last world is not this world's weather: the queue is
        // process-wide and a saved day number means nothing under a new calendar.
        Simulation.NuclearDetonations.Clear();
        // A country picked in the last campaign is an id in somebody else's world.
        _mapModeSubjectId = -1;
        if (!string.IsNullOrEmpty(_builtMapId) &&
            !string.Equals(_builtMapId, Simulation.MapCatalog.ActiveMapId, StringComparison.Ordinal))
        {
            _mapTask = null;
            _pendingMapResult = null;
            _mapApplied = false;
            _mapApplyStage = 0;
            _provinceTopology = null;
            _battleAnchorMap = null;
            _labelRevision = int.MinValue;
            _controlRevision = -1;
            _frontRevision = -1;
            _frontWarRevision = -1;
            _lastCombatSyncHours = -1;
            DisposeGpuMapCompute();
            SourceEarthData.ReleaseDecodedGrids();
        }
        var topologyAlreadyApplied = false;
        if (campaign.ScenarioId == "2024" && _mapTask is null &&
            campaign.States.Count >= Simulation.WorldMapTables.StateRows.Length + Simulation.TerritorialTopology.RegionRuleCount)
        {
            if (SourceEarthData.PeekStatesIfLoaded() is { Length: > 0 } loadedStates &&
                SourceEarthData.Width > 0 && SourceEarthData.Height > 0)
            {
                topologyAlreadyApplied = HasBootTopologyMarkers(loadedStates, SourceEarthData.Width, SourceEarthData.Height);
            }
            else if (Simulation.TerritorialTopology.TryPeekBootCacheRegionFingerprint(
                         SourceEarthData.Width, SourceEarthData.Height, out _) &&
                     Simulation.LakeRegions.CreatedCount > 0)
            {
                topologyAlreadyApplied = true;
            }
            else if (SourceEarthData.States is { Length: > 0 } sourceStates &&
                     SourceEarthData.Width > 0 && SourceEarthData.Height > 0)
            {
                topologyAlreadyApplied = HasBootTopologyMarkers(sourceStates, SourceEarthData.Width, SourceEarthData.Height);
            }
        }
        var repairs = (campaign.ScenarioId != "2024" || _mapTask is not null || topologyAlreadyApplied)
            ? Array.Empty<Simulation.TerritorialTopology.RepairSummary>()
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
            StartMapWorkerIfNeeded();
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
        _frontWarRevision = -1;
        _lastCombatSyncHours = -1;
        RefreshProvinceControllers();
        SourceEarthData.ReleaseDecodedGrids();
    }

    /// <summary>
    /// Start background terrain/water preparation (and political map assembly when a campaign is
    /// already available) early so DealOrForceGame can overlap asset work with interface/campaign boot.
    /// </summary>
    public void PreloadMapAsync(Simulation.Campaign campaign = null)
    {
        StartTerrainImages();
        if (campaign is not null && _mapTask is null)
        {
            if (_colorByMapIndex.Count == 0)
            {
                foreach (var nation in campaign.Nations)
                {
                    _colorByMapIndex[nation.MapIndex] = ParseHex(nation.Color, 0x6E7B8BFFu);
                    _nationColourSources[nation.MapIndex] = nation.Color;
                    _nationByMapIndex[nation.MapIndex] = nation;
                }
            }
            StartMapWorkerIfNeeded();
        }
    }

    private string _builtMapId;

    private void StartMapWorkerIfNeeded()
    {
        StartTerrainImages();
        if (_mapTask is not null) return;
        _builtMapId = Simulation.MapCatalog.ActiveMapId;
        var colors = new Dictionary<int, uint>(_colorByMapIndex);
        // Read here, not on the worker: the skin is a table the main thread may reload.
        var shades = Simulation.MapBorderStyles.PlateShades();
        // Decided here too: the worker must not read the settings while the main thread may write them.
        var buildFrontier = FrontierFieldWanted();
        var reducedMap = ReducedMap;
        var lowMemoryMobile = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var halfResGpuMaps = ShouldUseHalfResMobileMapTextures(Platform.PlatformProfile.Current);
        var compactRivers = lowMemoryMobile || halfResGpuMaps || Array.IndexOf(OS.GetCmdlineUserArgs(), "--verify-compact-rivers") >= 0;
        var cacheDir = Path.Combine(GamePaths.Root, "cache");
        var shippedCache = Simulation.GameDataLoader.FindDataFile("cache", Simulation.MapBootCache.FileName);
        var shippedHydrologyCache = Simulation.GameDataLoader.FindDataFile("cache", Simulation.MapBootCache.HydrologyFileName);
        var shippedPoliticalRasterCache = Simulation.GameDataLoader.FindDataFile("cache", Simulation.MapBootCache.PoliticalRasterFileName);
        _mapWorkerStage = "Checking map boot cache";
        _mapWorkerProgress = 42;
        _mapTask = Task.Run(() => BuildPoliticalMap(
            colors, shades, buildFrontier, reducedMap, lowMemoryMobile, compactRivers, cacheDir, shippedCache, shippedHydrologyCache, shippedPoliticalRasterCache,
            (stage, progress) =>
            {
                _mapWorkerStage = stage;
                _mapWorkerProgress = progress;
            },
            halfResGpuMaps));
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

        var platformProfile = Platform.GodotPlatformBridge.EnsureInitialized(Size.Y > 1f ? Size.Y : 1080f);
        var isTouchPrimary = platformProfile?.IsTouchPrimary == true;
        if (isTouchPrimary)
        {
            CloudsEnabled = false;
            if (!Simulation.PlayerSettings.Has("map_clouds"))
            {
                Simulation.SettingsCatalog.Store("map_clouds", false);
                CloudsShown = false;
            }
        }
        _viewport = new SubViewport
        {
            TransparentBg = false,
            UseHdr2D = false,
            PhysicsObjectPicking = false,
            HandleInputLocally = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Size2DOverrideStretch = true,
            Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            AnisotropicFilteringLevel = isTouchPrimary
                ? Viewport.AnisotropicFiltering.Anisotropy4X
                : Viewport.AnisotropicFiltering.Anisotropy8X,
            TextureMipmapBias = isTouchPrimary ? -0.50f : -0.20f,
            // The planet's silhouette, the counter quads and the order ribbons are the only true
            // geometric edges on this screen, and a staircase along the limb is the first thing
            // a strategy player notices about a map's quality. MobileHigh uses Msaa2X when the
            // renderer is native Vulkan; MobileMedium/MobileLow and compatibility/emulator GPUs
            // rely on analytic shader border AA at native/2048px resolution to avoid MSAA resolve stalls.
            Msaa3D = isTouchPrimary ? MobileAntialiasing() : MapAntialiasing(_quality),
            UseTaa = false
        };
        ConfigureViewportCrispness(isTouchPrimary);
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
        var initialRenderShare = 1f;
        if (float.TryParse(Simulation.SettingsCatalog.Chosen("render_scale"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var renderShare))
            initialRenderShare = renderShare;
        if (isTouchPrimary)
            RenderScale = MobileEffectiveRenderScale(initialRenderShare);
        else if (MathF.Abs(initialRenderShare - 1f) > 0.001f)
            RenderScale = initialRenderShare;
        ResetTurboSimObservationState();

        var world = new Node3D { Name = "GlobeWorld" };
        _viewport.AddChild(world);
        _skyWorld = world;

        var environment = new WorldEnvironment { Environment = new Godot.Environment() };
        environment.Environment.BackgroundMode = Godot.Environment.BGMode.Color;
        environment.Environment.BackgroundColor = Ui.Colour("globe.space");
        environment.Environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.Environment.AmbientLightColor = Ui.Colour("globe.ambient");
        environment.Environment.AmbientLightEnergy = 0.72f;
        environment.Environment.TonemapMode = Godot.Environment.ToneMapper.Linear;
        environment.Environment.GlowEnabled = false;
        environment.Environment.SdfgiEnabled = false;
        environment.Environment.SsaoEnabled = false;
        environment.Environment.SsilEnabled = false;
        environment.Environment.VolumetricFogEnabled = false;
        world.AddChild(environment);
        _skyEnvironment = environment;

        var sun = new DirectionalLight3D
        {
            LightEnergy = 1.15f,
            RotationDegrees = new Vector3(-32f, -38f, 0f),
            ShadowEnabled = !isTouchPrimary
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
        CreateTouchCameraHooks();
        ApplyShaderQualityTier(force: true);
    }

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
        var platformProfile = Platform.GodotPlatformBridge.EnsureInitialized(Size.Y > 1f ? Size.Y : 1080f);
        var isTouchPrimary = platformProfile?.IsTouchPrimary == true;
        // The tier says what this machine can afford; the video setting says how many pixels
        // the player wants. They multiply, so "75 %" on a weak laptop still means 75 % of what
        // the laptop can afford rather than undoing the downgrade.
        var playerShare = 1f;
        if (float.TryParse(Simulation.SettingsCatalog.Chosen("render_scale"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var chosen))
            playerShare = Math.Clamp(chosen, 0.25f, 2f);
        var effectiveScale = _quality.RenderScale * playerShare;
        if (isTouchPrimary)
            effectiveScale = MobileEffectiveRenderScale(effectiveScale);
        RenderScale = effectiveScale;
        WakeGlobeRendering();
        SetMapSmoothing(_quality.MapSmoothing, remember: false);
        var sunShadows = _quality.SunShadows && !isTouchPrimary;
        if (IsInstanceValid(_daySun))
        {
            if (_daySun.ShadowEnabled != sunShadows) _daySun.ShadowEnabled = sunShadows;
            var targetShadowMode = _quality.RenderScale >= 0.99f
                ? DirectionalLight3D.ShadowMode.Parallel4Splits
                : DirectionalLight3D.ShadowMode.Parallel2Splits;
            if (_daySun.DirectionalShadowMode != targetShadowMode)
                _daySun.DirectionalShadowMode = targetShadowMode;
        }
        // Release the directional shadow atlas entirely when sun shadows are off; holding a 512
        // buffer still kept a shadow render target allocated for a disabled pass.
        var targetShadowAtlas = sunShadows
            ? Math.Clamp((int)MathF.Round(2048f * _quality.RenderScale), 512, 2048)
            : 0;
        if (_lastShadowAtlasSize != targetShadowAtlas)
        {
            _lastShadowAtlasSize = targetShadowAtlas;
            RenderingServer.DirectionalShadowAtlasSetSize(targetShadowAtlas, true);
        }
        if (IsInstanceValid(_viewport))
        {
            var targetMsaa = isTouchPrimary ? MobileAntialiasing() : MapAntialiasing(_quality);
            if (_viewport.Msaa3D != targetMsaa) _viewport.Msaa3D = targetMsaa;
            ConfigureViewportCrispness(isTouchPrimary);
        }
        ApplyShaderQualityTier();
        if (isTouchPrimary) ResizeViewport();
    }

    private int _lastShadowAtlasSize = -1;

    public void SetViewportRenderingPaused(bool paused)
    {
        if (_externalViewportPaused == paused && IsInstanceValid(_viewport))
        {
            if (paused && _viewport.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled)
                _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }
        _externalViewportPaused = paused;
        if (!IsInstanceValid(_viewport)) return;
        if (paused)
        {
            _turboThrottlingActive = false;
            _turboRedrawAccumSeconds = 0.0;
            _turboWakePending = false;
            if (_viewport.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled)
                _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }
        WakeGlobeRendering();
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
    private void ConfigureViewportCrispness(bool isTouchPrimary)
    {
        if (!IsInstanceValid(_viewport)) return;
        var targetAniso = isTouchPrimary
            ? Viewport.AnisotropicFiltering.Anisotropy4X
            : Viewport.AnisotropicFiltering.Anisotropy8X;
        if (_viewport.AnisotropicFilteringLevel != targetAniso)
            _viewport.AnisotropicFilteringLevel = targetAniso;
        var targetBias = isTouchPrimary ? -0.50f : -0.20f;
        if (!Mathf.IsEqualApprox(_viewport.TextureMipmapBias, targetBias))
            _viewport.TextureMipmapBias = targetBias;
        if (_viewport.ScreenSpaceAA != Viewport.ScreenSpaceAAEnum.Disabled)
            _viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
    }

    private Viewport.Msaa MobileAntialiasing()
    {
        var chosen = (Simulation.SettingsCatalog.Chosen("map_antialiasing") ?? "").Trim().ToLowerInvariant();
        if (chosen is "off" or "0" or "none")
            return Viewport.Msaa.Disabled;
        if (chosen is "2x" or "4x" or "8x")
            return Viewport.Msaa.Msaa2X;
        if (RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility")
            return Viewport.Msaa.Disabled;
        var adapter = (RenderingServer.GetVideoAdapterName() ?? "").ToLowerInvariant();
        if (adapter.Contains("angle", StringComparison.Ordinal) ||
            adapter.Contains("swiftshader", StringComparison.Ordinal) ||
            adapter.Contains("llvmpipe", StringComparison.Ordinal) ||
            adapter.Contains("bluestacks", StringComparison.Ordinal))
            return Viewport.Msaa.Disabled;
        return CurrentShaderQualityTier() >= 0.5f || _quality.RenderScale < 0.99f
            ? Viewport.Msaa.Disabled
            : Viewport.Msaa.Msaa2X;
    }

    internal const float MobileRenderScaleFloor = 0.50f;
    internal const float MobileMediumRenderScaleCeiling = 1.0f;
    internal const float MobileMediumDefaultHighDpiScale = 1.0f;
    internal const float MobileLowRenderScaleCeiling = 0.82f;
    internal const int HighDpiPhoneWidthThresholdPx = 2000;
    internal const double TurboSimRedrawIntervalSeconds = 1.0 / 30.0;

    internal enum TurboViewportDirective
    {
        Disabled = 0,
        Once = 1,
        Always = 2
    }

    internal readonly record struct TurboSimInputs(
        bool ExternallyPaused,
        bool PlatformEligible,
        bool MapApplied,
        bool FastSimActive,
        bool TouchActive,
        bool CameraInMotion,
        bool CameraTransformDirty,
        bool BoxSelecting);

    private bool _externalViewportPaused;
    private bool _turboThrottlingActive;
    private bool _turboWakePending;
    private double _turboRedrawAccumSeconds;
    private ulong _turboLastWakeProcessFrame = ulong.MaxValue;
    private ulong _turboLastCadenceProcessFrame = ulong.MaxValue;

    private bool _turboObsPaused;
    private int _turboObsSpeed;
    private bool _turboObsFlatMap;
    private float _turboObsYaw;
    private float _turboObsPitch;
    private float _turboObsDistance;
    private float _turboObsTargetDistance;
    private float _turboObsFlatX;
    private float _turboObsFlatY;
    private float _turboObsFlatZoom;
    private float _turboObsTargetFlatX;
    private float _turboObsTargetFlatY;
    private float _turboObsTargetFlatZoom;
    private int _turboObsHighlightedNation = -1;
    private int _turboObsSelectedRegionLive = int.MinValue;
    private int _turboObsSelectedRegionRev = int.MinValue;

    internal double _turboThrottledSkyDelta;
    internal double _turboThrottledWeatherDelta;
    internal double _turboThrottledTradeDelta;
    internal double _turboThrottledPulseDelta;
    internal float _turboThrottledArmyDelta;

    internal static float ComputeMobileEffectiveRenderScale(
        float requestedScale, float shaderQualityTier, int screenWidthPx, bool hasCustomRenderScale)
    {
        // Cap MobileLow at 0.82f and MobileMedium at 0.95f (defaulting high-DPI phones >= 2000px wide
        // to 0.85f (~1741x803 on 2048px-capped MobileMedium phones to keep borders crisp without double-scaling),
        // while allowing FrameRateGovernor and player render_scale settings to step down to 0.50f.
        var ceiling = shaderQualityTier >= 1.5f
            ? MobileLowRenderScaleCeiling
            : (shaderQualityTier >= 0.5f
                ? (!hasCustomRenderScale && screenWidthPx >= HighDpiPhoneWidthThresholdPx
                    ? MobileMediumDefaultHighDpiScale
                    : MobileMediumRenderScaleCeiling)
                : 1.0f);
        return Math.Clamp(requestedScale, MobileRenderScaleFloor, ceiling);
    }

    internal static bool ShouldUseHalfResMobileMapTextures(Platform.PlatformProfile profile) =>
        profile?.GraphicsQualityTier is Platform.PlatformGraphicsTier.MobileMedium
            or Platform.PlatformGraphicsTier.MobileLow;

    internal static bool ShouldThrottleTurboSim(in TurboSimInputs input) =>
        !input.ExternallyPaused &&
        input.PlatformEligible &&
        input.MapApplied &&
        input.FastSimActive &&
        !input.TouchActive &&
        !input.CameraInMotion &&
        !input.CameraTransformDirty &&
        !input.BoxSelecting;

    internal static TurboViewportDirective StepTurboSimCadence(
        ref double accumSeconds, ref bool wakeImmediate, double deltaSeconds,
        in TurboSimInputs input, double intervalSeconds = TurboSimRedrawIntervalSeconds)
    {
        if (input.ExternallyPaused)
        {
            accumSeconds = 0.0;
            wakeImmediate = false;
            return TurboViewportDirective.Disabled;
        }
        if (!ShouldThrottleTurboSim(in input))
        {
            accumSeconds = 0.0;
            wakeImmediate = false;
            return TurboViewportDirective.Always;
        }
        if (wakeImmediate)
        {
            wakeImmediate = false;
            accumSeconds = 0.0;
            return TurboViewportDirective.Once;
        }
        var dt = double.IsFinite(deltaSeconds) && deltaSeconds > 0.0 ? deltaSeconds : 0.0;
        var interval = double.IsFinite(intervalSeconds) && intervalSeconds > 0.0
            ? intervalSeconds
            : TurboSimRedrawIntervalSeconds;
        accumSeconds += dt;
        if (accumSeconds < interval)
            return TurboViewportDirective.Disabled;
        accumSeconds %= interval;
        return TurboViewportDirective.Once;
    }

    private static bool IsTurboSimEligiblePlatform()
    {
        var active = Platform.PlatformProfile.Current;
        if (active?.IsTouchPrimary == true || active?.IsMobile == true ||
            OS.HasFeature("mobile") || OS.GetName() is "Android" or "iOS")
            return true;
        return Simulation.PlayerSettings.Bool("turbo_sim_globe", false) ||
               string.Equals(System.Environment.GetEnvironmentVariable("DOF_TURBO_SIM"), "1", StringComparison.Ordinal);
    }

    private void ResetTurboSimObservationState()
    {
        _turboObsYaw = _yaw;
        _turboObsPitch = _pitch;
        _turboObsDistance = _distance;
        _turboObsTargetDistance = _targetDistance;
        _turboObsFlatMap = _flatMap;
        _turboObsFlatX = _flatX;
        _turboObsFlatY = _flatY;
        _turboObsFlatZoom = _flatZoom;
        _turboObsTargetFlatX = _targetFlatX;
        _turboObsTargetFlatY = _targetFlatY;
        _turboObsTargetFlatZoom = _targetFlatZoom;
        _turboObsHighlightedNation = _highlightedNation;
        _turboObsSelectedRegionLive = _lastSelectedRegionLive;
        _turboObsSelectedRegionRev = _lastSelectedRegionRevision;
    }

    /// <summary>
    /// Wakes the 3D globe SubViewport immediately on Frame 0 so touch events, zoom/pan,
    /// pause/speed changes, focus shifts, and selection changes draw without Turbo Sim lag.
    /// </summary>
    public void WakeGlobeRendering()
    {
        _turboRedrawAccumSeconds = 0.0;
        if (!IsInstanceValid(_viewport) || _externalViewportPaused) return;
        var frame = Engine.GetProcessFrames();
        _turboLastWakeProcessFrame = frame;
        if (_turboLastCadenceProcessFrame != frame)
            _turboWakePending = true;
        if (_viewport.RenderTargetUpdateMode == SubViewport.UpdateMode.Disabled)
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    internal void ObservePreCameraTurboWakeState(bool isPaused, int speed)
    {
        var changed =
            _cameraTransformDirty ||
            isPaused != _turboObsPaused ||
            speed != _turboObsSpeed ||
            _flatMap != _turboObsFlatMap ||
            MathF.Abs(_yaw - _turboObsYaw) > 1e-5f ||
            MathF.Abs(_pitch - _turboObsPitch) > 1e-5f ||
            MathF.Abs(_distance - _turboObsDistance) > 1e-4f ||
            MathF.Abs(_targetDistance - _turboObsTargetDistance) > 1e-4f ||
            MathF.Abs(_flatX - _turboObsFlatX) > 1e-5f ||
            MathF.Abs(_flatY - _turboObsFlatY) > 1e-5f ||
            MathF.Abs(_flatZoom - _turboObsFlatZoom) > 1e-4f ||
            MathF.Abs(_targetFlatX - _turboObsTargetFlatX) > 1e-5f ||
            MathF.Abs(_targetFlatY - _turboObsTargetFlatY) > 1e-5f ||
            MathF.Abs(_targetFlatZoom - _turboObsTargetFlatZoom) > 1e-4f ||
            _highlightedNation != _turboObsHighlightedNation ||
            _lastSelectedRegionLive != _turboObsSelectedRegionLive ||
            _lastSelectedRegionRevision != _turboObsSelectedRegionRev;

        if (changed) WakeGlobeRendering();

        _turboObsPaused = isPaused;
        _turboObsSpeed = speed;
    }

    internal bool UpdateViewportRenderCadence(double delta, bool fastSimActive)
    {
        _turboObsFlatMap = _flatMap;
        _turboObsYaw = _yaw;
        _turboObsPitch = _pitch;
        _turboObsDistance = _distance;
        _turboObsTargetDistance = _targetDistance;
        _turboObsFlatX = _flatX;
        _turboObsFlatY = _flatY;
        _turboObsFlatZoom = _flatZoom;
        _turboObsTargetFlatX = _targetFlatX;
        _turboObsTargetFlatY = _targetFlatY;
        _turboObsTargetFlatZoom = _targetFlatZoom;
        _turboObsHighlightedNation = _highlightedNation;
        _turboObsSelectedRegionLive = _lastSelectedRegionLive;
        _turboObsSelectedRegionRev = _lastSelectedRegionRevision;

        if (!IsInstanceValid(_viewport)) return false;

        var frame = Engine.GetProcessFrames();
        _turboLastCadenceProcessFrame = frame;

        if (_cameraTransformDirty) _turboWakePending = true;

        var inputs = new TurboSimInputs(
            ExternallyPaused: _externalViewportPaused,
            PlatformEligible: IsTurboSimEligiblePlatform(),
            MapApplied: _mapApplied,
            FastSimActive: fastSimActive,
            TouchActive: _touchAdapter?.IsTouchActive == true || _gesture.IsPressed,
            CameraInMotion: GlobeViewInMotion || FlatViewInMotion || _isActivelyPanning,
            CameraTransformDirty: _cameraTransformDirty,
            BoxSelecting: _boxSelecting);

        var directive = StepTurboSimCadence(ref _turboRedrawAccumSeconds, ref _turboWakePending, delta, in inputs);
        _turboThrottlingActive = ShouldThrottleTurboSim(in inputs);
        if (_turboThrottlingActive && _turboLastWakeProcessFrame == frame && directive == TurboViewportDirective.Disabled)
            directive = TurboViewportDirective.Once;

        var targetMode = directive switch
        {
            TurboViewportDirective.Always => SubViewport.UpdateMode.Always,
            TurboViewportDirective.Once => SubViewport.UpdateMode.Once,
            _ => SubViewport.UpdateMode.Disabled
        };

        if (_viewport.RenderTargetUpdateMode != targetMode)
            _viewport.RenderTargetUpdateMode = targetMode;

        return directive != TurboViewportDirective.Disabled;
    }

    private int CurrentMobileScreenWidthPx()
    {
        var maxW = 0;
        try
        {
            if (IsInstanceValid(GetWindow()))
                maxW = Math.Max(maxW, Math.Max(GetWindow().Size.X, GetWindow().Size.Y));
        }
        catch { }
        try
        {
            var screen = DisplayServer.ScreenGetSize();
            maxW = Math.Max(maxW, Math.Max(screen.X, screen.Y));
        }
        catch { }
        if (maxW <= 0 && IsInstanceValid(_viewport))
            maxW = Math.Max(_viewport.Size.X, _viewport.Size.Y);
        if (maxW <= 0)
            maxW = (int)MathF.Round(Math.Max(Size.X, Size.Y));
        return maxW;
    }

    private float MobileEffectiveRenderScale(float requestedScale) =>
        ComputeMobileEffectiveRenderScale(
            requestedScale,
            CurrentShaderQualityTier(),
            CurrentMobileScreenWidthPx(),
            Simulation.PlayerSettings.Has("render_scale"));

    public float RenderScale
    {
        get => IsInstanceValid(_viewport) ? _viewport.Scaling3DScale : 1f;
        set
        {
            if (!IsInstanceValid(_viewport)) return;
            var isTouchPrimary = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
            var scale = isTouchPrimary
                ? MobileEffectiveRenderScale(value)
                : Mathf.Clamp(value, 0.25f, 2f);
            if (MathF.Abs(_viewport.Scaling3DScale - scale) > 0.0005f)
                _viewport.Scaling3DScale = scale;
            // Mobile Vulkan uses Bilinear filtering only (FSR1/FSR2 are Forward+ desktop); desktop Vulkan allows FSR1/FSR2.
            var isCompat = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
            var mode = (Simulation.SettingsCatalog.Chosen("map_upscaling_mode") ?? "auto").ToLowerInvariant();
            var targetMode = isTouchPrimary || isCompat || mode == "bilinear" || scale > 1.001f || (mode == "auto" && scale >= 0.999f)
                ? Viewport.Scaling3DModeEnum.Bilinear
                : mode == "fsr2"
                    ? Viewport.Scaling3DModeEnum.Fsr2
                    : Viewport.Scaling3DModeEnum.Fsr;
            if (_viewport.Scaling3DMode != targetMode)
                _viewport.Scaling3DMode = targetMode;
            var targetSharpness = Simulation.SettingsCatalog.Of("map_fsr_sharpness") is null
                ? 0.2f
                : Math.Clamp(Simulation.SettingsCatalog.Number("map_fsr_sharpness"), 0f, 2f);
            if (MathF.Abs(_viewport.FsrSharpness - targetSharpness) > 0.0005f)
                _viewport.FsrSharpness = targetSharpness;
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
        if (Platform.PlatformProfile.Current?.IsTouchPrimary == true)
        {
            // Cap long edge at 1600 on MobileLow (emulators/budget GPUs) and 2400 on MobileMedium/High (native 2340x1080 S22 clarity).
            var longEdge = Math.Max(pixels.X, pixels.Y);
            if (CurrentShaderQualityTier() >= 1.5f)
            {
                if (longEdge > 1600) pixels = ScaleViewportToLongEdge(pixels, longEdge, 1600);
            }
            else if (longEdge > 2400)
            {
                pixels = ScaleViewportToLongEdge(pixels, longEdge, 2400);
            }
        }
        var units = new Vector2I(Math.Max(1, (int)MathF.Round(logical.X)), Math.Max(1, (int)MathF.Round(logical.Y)));
        if (_viewport.Size != pixels) _viewport.Size = pixels;
        if (_viewport.Size2DOverride != units) _viewport.Size2DOverride = units;
    }

    private static Vector2I ScaleViewportToLongEdge(Vector2I pixels, int longEdge, int maxEdge)
    {
        var ratio = maxEdge / (float)Math.Max(1, longEdge);
        return new Vector2I(
            Math.Max(1, (int)MathF.Round(pixels.X * ratio)),
            Math.Max(1, (int)MathF.Round(pixels.Y * ratio)));
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

}
