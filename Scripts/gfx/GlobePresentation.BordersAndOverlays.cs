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
    private int _routeFingerprint = int.MinValue;
    private readonly List<Vector3> _routePoints = new();
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

    private bool _frontPreviewDeferred;

    private void BuildFrontPreview()
    {
        if (_campaign is { } campaign)
        {
            if (!campaign.TryEnterRead(0, true))
            {
                _frontPreviewDeferred = true;
                return;
            }
            try { BuildFrontPreviewCore(); }
            finally { campaign.ExitRead(); }
            return;
        }
        BuildFrontPreviewCore();
    }

    private void BuildFrontPreviewCore()
    {
        _frontPreviewDeferred = false;
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
    private ShaderMaterial _cachedFogTintMaterial;
    private float _cachedFogTint = float.NaN;
    private float _cachedFogDarkness = float.NaN;
    private float _cachedFogSaturation = float.NaN;

    private void UpdateFogTint()
    {
        if (_globeMaterial is null) return;
        var defines = _campaign?.Defines;
        var full = defines?.FogTintFullDistance ?? 7f;
        var clear = defines?.FogTintClearDistance ?? 12f;

        // A mod that puts the two the wrong way round gets no tint rather than an inverted
        // one, which is the failure that would be hardest to recognise as a mistake.
        var tint = clear <= full ? 0f : 1f - Mathf.SmoothStep(full, clear, ViewDistance);
        var darkness = defines?.FogTintDarkness ?? 0.74f;
        var saturation = defines?.FogTintSaturation ?? 0.65f;
        if (ReferenceEquals(_cachedFogTintMaterial, _globeMaterial) &&
            darkness == _cachedFogDarkness &&
            saturation == _cachedFogSaturation &&
            (tint == _cachedFogTint || (MathF.Abs(tint - _cachedFogTint) <= 0.0005f && tint > 0f && tint < 1f)))
            return;

        _cachedFogTintMaterial = _globeMaterial;
        _cachedFogTint = tint;
        _cachedFogDarkness = darkness;
        _cachedFogSaturation = saturation;
        _globeMaterial.SetShaderParameter("fog_tint", tint);
        _globeMaterial.SetShaderParameter("fog_darkness", darkness);
        _globeMaterial.SetShaderParameter("fog_saturation", saturation);
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

}
