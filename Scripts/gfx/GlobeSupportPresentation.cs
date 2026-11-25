using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Player-controlled long-range formations on the operational map. The catalogue owns
/// the icons and reach; this layer turns where each formation actually is into a readable
/// counter - at the province it stands in, or, on the road, at sea or in the air, part-way
/// along the step it is making, with the rest of its route drawn dashed in its arm's colour and
/// the days left on the card. Cards are shown at the zoom divisions are, panel or no panel, and
/// picked, boxed and ordered the way divisions are (GlobePresentation._GuiInput).
/// </summary>
public sealed partial class GlobePresentation
{
    // Long-range cards sit just behind land counters, never in front of them. The flat map
    // and the globe use the same one-pixel-scale depth bias in their own coordinate systems.
    private const float SupportMarkerDepthBias = .002f;
    private const float SupportSurfaceDepthFactor = .9994f;

    private sealed class SupportMapMarker
    {
        public Node3D Node = null!;
        public Sprite3D Plate = null!;
        public Label3D Readiness = null!;
        public SupportFormation Formation = null!;
        public float Latitude;
        public float Longitude;
        public Vector3 Normal;
        public Vector2 DisplayOffset;
        public bool DisplayFaded;
    }

    private Node3D _supportMarkerRoot = null!;
    private MeshInstance3D _supportRangeNode = null!;
    private ArrayMesh _supportRangeMesh = null!;
    private const int SupportPlateCap = 256;
    private readonly Dictionary<string, (Texture2D Texture, LinkedListNode<string> Node)> _supportPlates = new();
    private readonly LinkedList<string> _supportPlatesLru = new();
    private readonly Dictionary<int, SupportMapMarker> _supportMapMarkers = new();
    private readonly Dictionary<string, Image> _supportCutoutIcons = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _supportVisibility = new();
    private Campaign _supportMarkedCampaign;
    private int _supportRosterFingerprint = int.MinValue;
    private int _supportRangeFingerprint = int.MinValue;
    private int _selectedSupportId = -1;
    private readonly HashSet<int> _selectedSupportIds = new();
    private bool _supportOverlay;
    private double _supportPresentationDelay;
    private float _supportRangeFlatX = float.NaN;
    private float _supportRangeFlatZoom = float.NaN;
    private float _supportRangeViewDistance = float.NaN;
    private Vector3 _supportRangeCamNormal;
    private bool _supportRangeProjection;
    private Vector2 _supportRangeViewport;
    private ulong _nextSupportRangeRefreshMs;
    private readonly List<(SupportFormation Formation, SupportTypeDefinition Type, bool Selected)> _supportRingsScratch = new();
    private readonly List<(SupportFormation Formation, bool Selected)> _supportPathsScratch = new();
    private readonly HashSet<(int Base, string Type, string Mission, bool Selected)> _supportDedupScratch = new();
    private readonly List<Vector3> _supportRangeVerts = new();
    private readonly List<Color> _supportRangeColors = new();
    private readonly Godot.Collections.Array _supportRangeArrays = new();

    public int SelectedSupportId => _selectedSupportId;
    public IReadOnlyCollection<int> SelectedSupportIds => _selectedSupportIds;
    public bool SupportCommandMode { get; set; }
    /// <summary>A strike target is being named in the panel: the next left click on the map is its.</summary>
    public bool SupportRebaseArmed { get; set; }
    public event Action<List<SupportFormation>> SupportBoxPicked;
    /// <summary>A box over the map: the divisions and the formations in it, one selection.</summary>
    public event Action<IReadOnlyList<Simulation.Army>, List<SupportFormation>> SelectionBoxPicked;
    // Days left for the cards of moving formations, recomputed when the formation has moved on.
    private readonly Dictionary<int, (long Key, int Days)> _supportEta = new();
    private (int Province, int Selection, long Hours, string Text) _supportMoveTip = (-1, 0, -1, "");

    /// <summary>
    /// The long-range forces panel is open: every base is shown from further out, and every
    /// formation's reach is drawn faintly in the colour of its arm, so the player sees what
    /// the country covers before choosing what to change.
    /// </summary>
    public bool SupportOverlay
    {
        get => _supportOverlay;
        set
        {
            if (_supportOverlay == value) return;
            _supportOverlay = value;
            if (!value) { SupportCommandMode = false; SupportRebaseArmed = false; }
            _supportRangeFlatX = float.NaN;
            BuildSupportRange();
        }
    }

    /// <summary>Several formations selected at once - the panel's selection, mirrored on the map.</summary>
    public void SelectSupportFormations(IEnumerable<int> ids)
    {
        var next = ids.ToHashSet();
        if (_selectedSupportIds.SetEquals(next)) return;
        _selectedSupportIds.Clear();
        foreach (var id in next) _selectedSupportIds.Add(id);
        _selectedSupportId = _selectedSupportIds.Count == 1 ? _selectedSupportIds.First() : _selectedSupportIds.Count > 0 ? _selectedSupportIds.Min() : -1;
        _supportRangeFlatX = float.NaN;
        BuildSupportRange();
        RefreshSupportMarkerSelection();
        InvalidateFlatCounterDensity();
    }

    /// <summary>The colour of an arm: sky blue aircraft, teal drones, amber guns, red missile brigades.</summary>
    private static Color SupportArmColour(SupportKind kind) => kind switch
    {
        SupportKind.Air => Ui.Colour("support.air"),
        SupportKind.Drone => Ui.Colour("support.drone"),
        SupportKind.AirDefence => Ui.Colour("support.air_defence"),
        _ => Ui.Colour("support.artillery")
    };

    private void CreateSupportPresentation(Node3D world)
    {
        _supportMarkerRoot = new Node3D { Name = "LongRangeBases" };
        world.AddChild(_supportMarkerRoot);
        _supportRangeMesh = new ArrayMesh();
        _supportRangeArrays.Resize((int)Mesh.ArrayType.Max);
        _supportRangeNode = new MeshInstance3D
        {
            Name = "SelectedLongRangeReach",
            Mesh = _supportRangeMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        world.AddChild(_supportRangeNode);
    }

    public void SelectSupportFormation(int formationId)
    {
        _selectedSupportId = formationId;
        _selectedSupportIds.Clear();
        if (formationId >= 0) _selectedSupportIds.Add(formationId);
        _supportRangeFlatX = float.NaN;
        BuildSupportRange();
        RefreshSupportMarkerSelection();
        InvalidateFlatCounterDensity();
    }

    public void FocusSupportFormation(int formationId)
    {
        if (!TryBeginActionRead(out var campaign)) return;
        (float Latitude, float Longitude) center;
        try
        {
            if (campaign.ProvinceMap is null) return;
            var formation = campaign.SupportFormations.Find(f => f.Id == formationId && f.NationId == campaign.Player?.Id);
            if (formation is null || LongRangeMovement.At(formation) <= 0) return;
            center = LongRangeMovement.Position(campaign, formation);
        }
        finally { campaign.ExitRead(); }
        SelectSupportFormation(formationId);
        FocusOn(center.Latitude, center.Longitude, 7.8f);
    }

    private void UpdateSupportPresentation(double delta)
    {
        if (!IsInstanceValid(_supportMarkerRoot)) return;
        // Out to the zoom divisions are drawn at, panel or no panel: a formation that cannot be seen
        // where the divisions are cannot be selected beside them.
        var show = MilitaryMarkersEnabled && _campaign?.Player is not null && _campaign.FeatureEnabled("long_range_forces") &&
                   (_flatMap || ViewDistance <= Ui.Metric(_supportOverlay ? "support.overlay_distance" : "support.marker_distance", _supportOverlay ? 17f : 10.8f));
        _supportMarkerRoot.Visible = show;
        _supportRangeNode.Visible = show && (_selectedSupportIds.Count > 0 || _supportOverlay) &&
                                    _supportRangeMesh.GetSurfaceCount() > 0;
        if (!show || _campaign?.ProvinceMap is null) return;
        _supportPresentationDelay += delta;
        if (_supportPresentationDelay >= .35 || !ReferenceEquals(_supportMarkedCampaign, _campaign))
        {
            _supportPresentationDelay = 0;
            SyncSupportMarkers();
        }
        ReprojectSupportMarkers();
        // Slots remain surface-local; use their actual normal for horizon culling.
        var viewNormal = _camera.GlobalPosition.Normalized();
        var horizon = GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length()) + .002f;
        foreach (var marker in _supportMapMarkers.Values)
            if (!_flatMap) marker.Node.Visible = marker.Node.Position.Normalized().Dot(viewNormal) > horizon;
        if (_flatMap) UpdateMarkerVisibility(_supportMarkerRoot, _supportVisibility, true);
        // Screen-width ribbons only need rebuilding when projection/viewport/zoom changes or camera moves past a wrap/horizon angle.
        if (_supportRangeNode.Visible && Time.GetTicksMsec() >= _nextSupportRangeRefreshMs)
        {
            var camNormal = _flatMap ? Vector3.Back : _camera.GlobalPosition.Normalized();
            var dirtyView = _supportRangeProjection != _flatMap ||
                _supportRangeViewport != Size ||
                MathF.Abs(_supportRangeFlatZoom - _flatZoom) > 0.025f ||
                MathF.Abs(_supportRangeViewDistance - ViewDistance) > 0.12f ||
                float.IsNaN(_supportRangeFlatX) ||
                (_flatMap ? MathF.Abs(_supportRangeFlatX - _flatX) >= 1.5f : _supportRangeCamNormal.Dot(camNormal) < 0.9985f);
            if (dirtyView) BuildSupportRange();
        }
    }

    private readonly List<SupportFormation> _supportOwnedScratch = new();

    private static int CompareSupportOrder(SupportFormation a, SupportFormation b)
    {
        var cmp = a.StateMapIndex.CompareTo(b.StateMapIndex);
        return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
    }

    private void SyncSupportMarkers()
    {
        var player = _campaign?.Player;
        var map = _campaign?.ProvinceMap;
        if (player is null || map is null) return;
        // Every formation of ours that is anywhere on the map - deployed, on the road, deploying or
        // cut off. There is no "off the map" any more, so there is nothing to leave out.
        var owned = _supportOwnedScratch;
        owned.Clear();
        var formations = _campaign.SupportFormations;
        for (var i = 0; i < formations.Count; i++)
        {
            var f = formations[i];
            if (f.NationId != player.Id || !SupportCore.Enabled(_campaign, f.TypeId)) continue;
            var at = LongRangeMovement.At(f);
            if (at > 0 && at <= map.ProvinceCount) owned.Add(f);
        }
        owned.Sort(CompareSupportOrder);
        var hash = new HashCode();
        hash.Add(player.Id);
        foreach (var formation in owned)
        {
            hash.Add(formation.Id);
            hash.Add(formation.TypeId);
        }
        var fingerprint = hash.ToHashCode();
        var reachHash = new HashCode();
        foreach (var formation in owned)
        {
            reachHash.Add(formation.Id); reachHash.Add(formation.BaseProvinceId); reachHash.Add(formation.ProvinceId);
            reachHash.Add(formation.Route.Count); reachHash.Add((int)(formation.MoveProgress * 16f));
            reachHash.Add(formation.Mission); reachHash.Add(SupportCore.Displaced(_campaign, formation));
        }
        var reachFingerprint = reachHash.ToHashCode();
        var reachChanged = _supportRangeFingerprint != reachFingerprint;
        _supportRangeFingerprint = reachFingerprint;
        if (ReferenceEquals(_supportMarkedCampaign, _campaign) && fingerprint == _supportRosterFingerprint)
        {
            var moved = false;
            foreach (var formation in owned)
                if (_supportMapMarkers.TryGetValue(formation.Id, out var marker))
                {
                    moved |= MoveSupportMarker(marker, formation);
                    PaintSupportMarker(marker, formation);
                }
            if (moved)
            {
                _supportVisibility.Clear();
                foreach (var marker in _supportMapMarkers.Values) _supportVisibility.Add((marker.Node, marker.Normal));
                InvalidateFlatCounterDensity();
            }
            if (reachChanged) BuildSupportRange();
            return;
        }
        _supportMarkedCampaign = _campaign;
        _supportRosterFingerprint = fingerprint;
        foreach (var marker in _supportMapMarkers.Values)
        {
            _supportMarkerRoot.RemoveChild(marker.Node);
            marker.Node.QueueFree();
        }
        _supportMapMarkers.Clear();
        _supportVisibility.Clear();
        InvalidateFlatCounterDensity();
        foreach (var formation in owned)
        {
            if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) continue;
            var center = LongRangeMovement.Position(_campaign, formation);
            // Geography is not a collision-avoidance slot. Keep the actual anchor - the province
            // it stands in, or the point it has reached on the road; the bounded screen layout may
            // only use room inside that same province.
            var latitude = center.Latitude;
            var longitude = center.Longitude;
            var node = new Node3D { Name = $"LongRange_{formation.Id}" };
            _supportMarkerRoot.AddChild(node);
            // The symbol is part of the card now: cut out and fitted into its slot on the
            // left when the card is built. It used to be a sprite parked over the card at a
            // hard-coded size, so a catalogue tile with a wide drawing on it - a plane, say
            // - simply overran the frame it was supposed to sit in.
            var iconPath = SpriteCatalog.TextureOf($"icon_{type.Icon}", $"res://game/gfx/interface/icons/icon_{type.Icon}.png");
            var plate = new Sprite3D
            {
                Texture = SupportPlate(iconPath, nation: _campaign.NationById(formation.NationId)),
                Name = "SupportCard", Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                NoDepthTest = false
            };
            node.AddChild(plate);
            var readiness = new Label3D
            {
                Font = PresentationFonts.For("ui.default"),
                FontSize = 20, OutlineSize = 3, Offset = new Vector2(16, 0),
                RenderPriority = 8, OutlineRenderPriority = 7,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                Name = "SupportReadiness", Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                NoDepthTest = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = new Color(.91f, .95f, .96f)
            };
            node.AddChild(readiness);
            var normal = SurfacePoint(latitude, longitude, 1f).Normalized();
            var marker = new SupportMapMarker
            {
                Node = node, Plate = plate, Readiness = readiness,
                Formation = formation, Latitude = latitude, Longitude = longitude, Normal = normal
            };
            _supportMapMarkers[formation.Id] = marker;
            _supportVisibility.Add((node, normal));
            PlaceSupportMarker(marker);
            PaintSupportMarker(marker, formation);
        }
        if (!_supportMapMarkers.ContainsKey(_selectedSupportId)) _selectedSupportId = -1;
        _selectedSupportIds.RemoveWhere(id => !_supportMapMarkers.ContainsKey(id));
        RefreshSupportMarkerSelection();
        BuildSupportRange();
    }

    /// <summary>The card follows the formation along its road: true when it moved.</summary>
    private bool MoveSupportMarker(SupportMapMarker marker, SupportFormation formation)
    {
        var (latitude, longitude) = LongRangeMovement.Position(_campaign, formation);
        if (MathF.Abs(latitude - marker.Latitude) < .0001f && MathF.Abs(longitude - marker.Longitude) < .0001f) return false;
        marker.Latitude = latitude;
        marker.Longitude = longitude;
        marker.Normal = SurfacePoint(latitude, longitude, 1f).Normalized();
        marker.DisplayOffset = Vector2.Zero;
        PlaceSupportMarker(marker);
        return true;
    }

    /// <summary>Days until a moving formation works again, kept until it has moved on.</summary>
    private int SupportEta(SupportFormation formation)
    {
        var key = ((long)_campaign.ElapsedDays << 32) ^ ((long)formation.Route.Count << 20) ^
                  BitConverter.SingleToInt32Bits(formation.MoveProgress);
        if (_supportEta.TryGetValue(formation.Id, out var known) && known.Key == key) return known.Days;
        var days = LongRangeMovement.DaysLeft(_campaign, formation);
        _supportEta[formation.Id] = (key, days);
        return days;
    }

    private void PlaceSupportMarker(SupportMapMarker marker)
    {
        marker.Node.Position = _flatMap
            ? LocalFlatPoint(marker.Latitude, marker.Longitude, FlatMarkerZ - SupportMarkerDepthBias)
            : LocalSurfacePoint(marker.Latitude, marker.Longitude, SurfaceMarkerRadius * SupportSurfaceDepthFactor);
        var flatScale = MathF.Max(.00001f, _counterWorldPixel);
        marker.Node.Scale = Vector3.One * (_flatMap ? flatScale * _counterFlatFar : SurfaceCounterWidth(marker.Node, "support"));
        OrientSurfaceCounter(marker.Node, marker.Node.Position.Normalized());
        marker.Readiness.FontSize = _flatMap && _flatStrategicCounters ? 30 : 20;
        marker.Plate.FixedSize = marker.Readiness.FixedSize = false;
        // Everything below is laid out in the card's own pixels (96x56), so the flat map
        // and the globe draw the same marker and one tuning moves both. The readiness
        // number sits in the middle of the card - where the marker is anchored - and the
        // symbol is drawn inside the card to the left of it. A label parked beside its
        // card by an offset drifts away from it as the camera moves; at the origin there
        // is no offset left to drift.
        // Orthographic cards have a fixed screen footprint; only Node.Scale tracks zoom.
        var platePixel = _flatMap
            ? Math.Clamp(Ui.Metric("support.flat_counter_width", 64f), 40f, 96f) / 96f
            : 1f / 96f;
        marker.Plate.PixelSize = platePixel;
        marker.Readiness.PixelSize = platePixel * .85f;
        marker.Readiness.Visible = !_flatMap || (!_flatStrategicCounters && _selectedSupportIds.Contains(marker.Formation.Id));
        marker.Readiness.Position = new Vector3(0f, 0f, .02f);
        marker.Readiness.Width = 0f;
        marker.Readiness.RenderPriority = 8;
        marker.Readiness.HorizontalAlignment = HorizontalAlignment.Center;
        marker.Readiness.VerticalAlignment = VerticalAlignment.Center;
    }

    private void PaintSupportMarker(SupportMapMarker marker, SupportFormation formation)
    {
        marker.Formation = formation;
        // Red for a formation that cannot work where it stands and is not on its way anywhere:
        // cut off, or stopped on ground it may not use. A column on the road keeps its arm's colour.
        var moving = formation.Route.Count > 0 || formation.DeployDaysLeft > 0;
        var displaced = !moving && SupportCore.Displaced(_campaign, formation);
        if (SupportTypeCatalog.TryGet(formation.TypeId, out var type))
        {
            var icon = SpriteCatalog.TextureOf($"icon_{type.Icon}", $"res://game/gfx/interface/icons/icon_{type.Icon}.png");
            marker.Plate.Texture = SupportPlate(icon, _selectedSupportIds.Contains(formation.Id),
                displaced ? Ui.Colour("support.displaced") : SupportArmColour(type.Kind),
                _campaign.NationById(formation.NationId));
            var compact = _flatMap && (_flatStrategicCounters || !_selectedSupportIds.Contains(formation.Id));
            if (compact)
                marker.Plate.Texture = CompactCounterTexture(marker.Plate.Texture, new Rect2I(7, 13, 28, 30));
            marker.Plate.PixelSize = _flatMap
                ? Math.Clamp(Ui.Metric(compact ? "support.flat_idle_width" : "support.flat_counter_width", compact ? 34f : 64f), 24f, 96f) / marker.Plate.Texture.GetWidth()
                : 1f / marker.Plate.Texture.GetWidth();
        }
        marker.Plate.Modulate = Colors.White;
        var mission = formation.CutOff ? Loc.Get("ISOLATED")
            : formation.Route.Count > 0 ? Loc.Format("→ {0} d", SupportEta(formation))
            : formation.DeployDaysLeft > 0 ? Loc.Get("DEPLOY")
            : displaced ? "!" : formation.Mission switch
        {
            SupportCore.MissionCloseAirSupport => Loc.Get("FIRE"),
            SupportCore.MissionAirSuperiority => Loc.Get("AIR"),
            SupportCore.MissionInterdiction => Loc.Get("RAID"),
            AirReconnaissance.Mission => Loc.Get("RECON"),
            _ => Loc.Get("REST")
        };
        var readiness = Math.Clamp((int)MathF.Round(formation.Strength * 100f), 0, 100) + "%" +
            (_flatMap && _flatStrategicCounters ? "" : "\n" + mission);
        if (marker.Readiness.Text != readiness) marker.Readiness.Text = readiness;
        marker.Readiness.Modulate = formation.Strength >= .7f ? Ui.Colour("support.ready") :
            formation.Strength >= .35f ? Ui.Colour("support.depleted") : Ui.Colour("support.critical");
        marker.Readiness.Visible = !_flatMap || (!_flatStrategicCounters && _selectedSupportIds.Contains(formation.Id));
        marker.Readiness.PixelSize = marker.Plate.PixelSize * .85f;
        if (_flatMap)
        {
            marker.Node.SetMeta("flat_counter_width", marker.Plate.Texture.GetWidth() * marker.Plate.PixelSize);
        }
    }

    private void RefreshSupportMarkerSelection()
    {
        foreach (var (id, marker) in _supportMapMarkers)
        {
            var selected = _selectedSupportIds.Contains(id);
            // Selection changes the frame, not the card's size or click footprint.
            marker.Plate.Scale = Vector3.One;
            marker.Readiness.Scale = marker.Plate.Scale;
            PaintSupportMarker(marker, marker.Formation);
        }
    }

    private void ReprojectSupportMarkers(bool forceLayout = false)
    {
        foreach (var marker in _supportMapMarkers.Values)
            if (IsInstanceValid(marker.Node))
            {
                if (forceLayout) PlaceSupportMarker(marker);
                else
                {
                    var position = _flatMap
                        ? LocalFlatPoint(marker.Latitude, marker.Longitude, FlatMarkerZ - SupportMarkerDepthBias)
                        : LocalSurfacePoint(marker.Latitude, marker.Longitude, SurfaceMarkerRadius * SupportSurfaceDepthFactor);
                    if (_flatMap)
                    {
                        position += new Vector3(marker.DisplayOffset.X, -marker.DisplayOffset.Y, 0f) * (_camera.Size / Math.Max(1f, Size.Y));
                        if (marker.Node.Position != position) marker.Node.Position = position;
                        continue;
                    }
                    if (marker.Node.Position != position) marker.Node.Position = position;
                    var card = SurfaceCounterWidth(marker.Node, "support");
                    OrientSurfaceCounter(marker.Node, position.Normalized());
                    marker.Node.Scale = Vector3.One * card;
                }
            }
        if (forceLayout) RefreshSupportMarkerSelection();
        if (_supportRangeProjection != _flatMap) BuildSupportRange();
    }

    private bool TryPickSupport(Vector2 pixel, out SupportFormation formation)
    {
        formation = null!;
        if (!_supportMarkerRoot.Visible || !IsInstanceValid(_camera)) return false;
        var best = float.MaxValue;
        foreach (var marker in _supportMapMarkers.Values)
        {
            if (!IsInstanceValid(marker.Node) || !marker.Node.Visible || _camera.IsPositionBehind(marker.Node.GlobalPosition)) continue;
            if (!TryHitSurfaceCounter(marker.Plate, pixel, out var distance)) continue;
            if (distance >= best) continue;
            best = distance;
            formation = marker.Formation;
        }
        if (formation is null) return false;
        return true;
    }

    private List<SupportFormation> SupportInScreenRect(Rect2 rect) =>
        _supportMapMarkers.Values.Where(m => IsInstanceValid(m.Node) && m.Plate.IsVisibleInTree() &&
            !_camera.IsPositionBehind(m.Node.GlobalPosition) && rect.HasPoint(_camera.UnprojectPosition(m.Node.GlobalPosition)))
            .Select(m => m.Formation).ToList();

    private string SupportMapTooltip(Vector2 pixel)
    {
        if (TryPickSupport(pixel, out var formation) && SupportTypeCatalog.TryGet(formation.TypeId, out var type))
        {
            var state = _campaign.States.Find(s => s.MapIndex == formation.StateMapIndex);
            var where = formation.CutOff ? Loc.Get("Cut off: no road out and no flight long enough. It looks for a way out every day.")
                : formation.Route.Count > 0 ? Loc.Format("On the way: {0} d to go, then deploying.", SupportEta(formation))
                : formation.DeployDaysLeft > 0 ? Loc.Format("Deploying: works in {0} d.", formation.DeployDaysLeft)
                : SupportCore.Displaced(_campaign, formation) ? Loc.Get("Cannot work from this ground - move it.") : "";
            return Loc.Get(type.Name) + "\n" + state?.DisplayName + "\n" +
                Loc.Format("Readiness {0:P0} · reach {1:0} km", formation.Strength, SupportCore.ReachKm(_campaign, type)) + "\n" +
                Loc.Get(formation.Mission switch
                {
                    SupportCore.MissionCloseAirSupport => "Fire support",
                    SupportCore.MissionAirSuperiority => "Air superiority",
                    SupportCore.MissionInterdiction => "Interdiction",
                    AirReconnaissance.Mission => "Reconnaissance",
                    SupportCore.MissionStrike => "Deep strike",
                    _ => "Standby / recover"
                }) + (where.Length > 0 ? "\n" + where : "");
        }
        // Formations selected and the cursor over ground: what a right-click here would do, from
        // the same preview the order is checked against - the way, the days, the room on arrival,
        // or why not.
        if (_selectedSupportIds.Count == 0 || SupportRebaseArmed || _campaign?.Player is null ||
            !TryPick(pixel, out var destination, updateSelection: false) || destination.ProvinceId <= 0) return "";
        var selection = 17;
        foreach (var id in _selectedSupportIds.OrderBy(id => id)) selection = selection * 31 + id;
        if (_supportMoveTip.Province == destination.ProvinceId && _supportMoveTip.Selection == selection &&
            _supportMoveTip.Hours == _campaign.ElapsedHours) return _supportMoveTip.Text;
        var append = Input.IsKeyPressed(Key.Shift);
        var previews = LongRangeMovement.PreviewRelocate(_campaign, _campaign.Player.Id, _selectedSupportIds, destination.ProvinceId, append);
        var lines = new List<string> { destination.State?.DisplayName ?? "" };
        var going = previews.Where(p => p.Accepted).ToList();
        if (going.Count > 0)
        {
            var modes = new List<string>();
            if (going.Any(p => p.Uses(SupportMoveMode.Land))) modes.Add(Loc.Get("by road"));
            if (going.Any(p => p.Uses(SupportMoveMode.Rail))) modes.Add(Loc.Get("by rail"));
            if (going.Any(p => p.Uses(SupportMoveMode.Sea))) modes.Add(Loc.Get("by sea"));
            if (going.Any(p => p.Uses(SupportMoveMode.Air))) modes.Add(Loc.Get("by air"));
            var first = going[0];
            lines.Add(Loc.Format(append ? "Right-click: {0} formation(s) add this waypoint · {1}-{2} d" : "Right-click: {0} formation(s) go here · {1}-{2} d",
                going.Count, going.Min(p => p.Days), going.Max(p => p.Days)) +
                (modes.Count > 0 ? " · " + string.Join(", ", modes) : ""));
            lines.Add(Loc.Format("Basing on arrival: used {0:0.#} of {1}", first.BasingUsed + going.Skip(1).Sum(p =>
                SupportTypeCatalog.TryGet(_campaign.SupportFormations.Find(f => f.Id == p.FormationId)?.TypeId, out var t) ? t.Basing : 1f),
                first.BasingCapacity) +
                (first.BasingUsed > first.BasingCapacity ? " · " + Loc.Get("crowded: readiness recovers more slowly") : ""));
        }
        foreach (var refused in previews.Where(p => !p.Accepted).GroupBy(p => p.RefusalText).Take(3))
        {
            var sample = refused.First();
            var reason = sample.RefusalArgs.Length == 0 ? Loc.Get(sample.Refusal) : Loc.Format(sample.Refusal, sample.RefusalArgs);
            lines.Add(going.Count == 0 && refused.Count() == previews.Count ? reason : Loc.Format("{0} cannot: {1}", refused.Count(), reason));
        }
        var text = string.Join("\n", lines.Where(line => line.Length > 0));
        _supportMoveTip = (destination.ProvinceId, selection, _campaign.ElapsedHours, text);
        return text;
    }

    private readonly List<(float Latitude, float Longitude)> _supportTrail = new();

    private void BuildSupportRange()
    {
        if (_campaign is { } campaign)
        {
            if (!campaign.TryEnterRead(25, true)) return;
            try { BuildSupportRangeCore(); }
            finally { campaign.ExitRead(); }
            return;
        }
        BuildSupportRangeCore();
    }

    private void BuildSupportRangeCore()
    {
        if (!IsInstanceValid(_supportRangeNode) || _campaign?.ProvinceMap is null) return;
        _supportRangeMesh.ClearSurfaces();
        var player = _campaign.Player;
        var rings = _supportRingsScratch;
        var paths = _supportPathsScratch;
        var deduplicate = _supportDedupScratch;
        rings.Clear();
        paths.Clear();
        deduplicate.Clear();
        foreach (var candidate in _campaign.SupportFormations)
        {
            if (!SupportCore.Enabled(_campaign, candidate.TypeId)) continue;
            if (candidate.NationId != player?.Id) continue;
            var selected = _selectedSupportIds.Contains(candidate.Id);
            if (!selected && !_supportOverlay) continue;
            // A column on the road has no ring - it reaches nothing until it has arrived - but it has a path.
            if (candidate.Route.Count > 0) { paths.Add((candidate, selected)); continue; }
            if (candidate.BaseProvinceId <= 0 || candidate.BaseProvinceId > _campaign.ProvinceMap.ProvinceCount) continue;
            if (!SupportTypeCatalog.TryGet(candidate.TypeId, out var candidateType)) continue;
            if (!deduplicate.Add((candidate.BaseProvinceId, candidate.TypeId, candidate.Mission, selected))) continue;
            rings.Add((candidate, candidateType, selected));
        }
        _supportRangeProjection = _flatMap;
        _supportRangeFlatX = _flatX;
        _supportRangeFlatZoom = _flatZoom;
        _supportRangeViewDistance = ViewDistance;
        _supportRangeCamNormal = _flatMap ? Vector3.Back : _camera.GlobalPosition.Normalized();
        _supportRangeViewport = Size;
        _nextSupportRangeRefreshMs = Time.GetTicksMsec() + 50;
        if (rings.Count == 0 && paths.Count == 0)
        {
            _supportRangeNode.Visible = false;
            return;
        }
        const int steps = 120;
        const int bands = 6;
        float latitude = 0f, longitude = 0f, angularReach = 0f;
        _supportRangeVerts.Clear();
        _supportRangeColors.Clear();

        // What the selected formations touch, filled faintly so it reads as ground rather than
        // as a line. Bands from the base outwards along great circles, so the fill follows the
        // globe instead of cutting under it the way a flat fan would for a bomber's reach.
        foreach (var (formation, type, selected) in rings)
        {
            // Reserve/recovery needs an outline to show reach, not a blanket that washes
            // out the whole country. Only an active selected mission fills its footprint.
            if (!selected || formation.Mission == SupportCore.MissionStandby || SupportCore.Displaced(_campaign, formation)) continue;
            Aim(formation, type);
            var fill = SupportArmColour(type.Kind) with { A = Ui.Metric("support.reach_fill_alpha", .08f) };
            for (var band = 0; band < bands; band++)
            {
                var inner = band / (float)bands;
                var outer = (band + 1) / (float)bands;
                for (var segment = 0; segment < steps; segment++)
                {
                    var a = Destination(segment, inner);
                    var b = Destination(segment + 1, inner);
                    var c = Destination(segment, outer);
                    var d = Destination(segment + 1, outer);
                    if (_flatMap && (Wraps(a, b) || Wraps(a, c) || Wraps(c, d))) continue;
                    Triangle(a, c, d, fill);
                    if (band > 0) Triangle(a, d, b, fill);
                }
            }
        }
        foreach (var (formation, type, selected) in rings)
        {
            Aim(formation, type);
            var arm = SupportArmColour(type.Kind);
            var resting = formation.Mission == SupportCore.MissionStandby;
            var colour = SupportCore.Displaced(_campaign, formation) ? new Color(.85f, .35f, .33f, selected ? .9f : .35f)
                : selected ? arm with { A = resting ? .72f : .95f }
                : arm with { A = resting ? .16f : .34f };
            for (var segment = 0; segment < steps; segment++)
            {
                // A resting formation's ring is dashed: it reaches there, but it is not flying.
                if (resting && segment % 4 >= 2) continue;
                {
                    var a = Destination(segment, 1f);
                    var b = Destination(segment + 1, 1f);
                    if (_flatMap && Wraps(a, b)) continue;
                    var worldA = _supportRangeNode.ToGlobal(a);
                    var worldB = _supportRangeNode.ToGlobal(b);
                    if (_camera.IsPositionBehind(worldA) || _camera.IsPositionBehind(worldB)) continue;
                    if (!_flatMap)
                    {
                        var horizon = GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length());
                        var view = _camera.GlobalPosition.Normalized();
                        if (a.Normalized().Dot(view) < horizon && b.Normalized().Dot(view) < horizon) continue;
                    }
                    var screenA = _camera.UnprojectPosition(worldA);
                    var screenB = _camera.UnprojectPosition(worldB);
                    if (screenA.DistanceSquaredTo(screenB) < .01f) continue;
                    var direction = (screenB - screenA).Normalized();
                    var normal = new Vector2(-direction.Y, direction.X) *
                        Math.Clamp(Ui.Metric(selected ? "support.reach_width_pixels" : "support.reach_idle_width_pixels", selected ? 1.2f : .7f), .5f, 3f) * .5f;
                    var depthA = -_camera.ToLocal(_supportRangeNode.ToGlobal(a)).Z;
                    var depthB = -_camera.ToLocal(_supportRangeNode.ToGlobal(b)).Z;
                    var ai = _supportRangeNode.ToLocal(_camera.ProjectPosition(screenA - normal, depthA));
                    var ao = _supportRangeNode.ToLocal(_camera.ProjectPosition(screenA + normal, depthA));
                    var bi = _supportRangeNode.ToLocal(_camera.ProjectPosition(screenB - normal, depthB));
                    var bo = _supportRangeNode.ToLocal(_camera.ProjectPosition(screenB + normal, depthB));
                    Triangle(ai, ao, bo, colour);
                    Triangle(ai, bo, bi, colour);
                }
            }
        }
        // The roads ahead: from where the card is now through every province still to reach,
        // dashed in the arm's colour - bright for the selection, faint for the rest under the
        // panel's overlay. A flight is one straight leg; a sea passage follows the water the
        // ship will sail (LongRangeMovement.Trail), round the coasts and through the straits,
        // never across the land between two ports. Each leg is cut into pieces so the line lies
        // on the globe rather than under it.
        foreach (var (formation, selected) in paths)
        {
            if (!SupportTypeCatalog.TryGet(formation.TypeId, out var pathType)) continue;
            var colour = SupportArmColour(pathType.Kind) with { A = selected ? .92f : .30f };
            LongRangeMovement.Trail(_campaign, formation, _supportTrail);
            var dash = 0;
            for (var leg = 1; leg < _supportTrail.Count; leg++)
            {
                var from = _supportTrail[leg - 1];
                var to = _supportTrail[leg];
                var pieces = Math.Clamp((int)(LegKm(from, to) / 40f), 1, 40);
                for (var piece = 0; piece < pieces; piece++, dash++)
                {
                    if (dash % 2 == 1) continue;
                    var a = PathPoint(from, to, piece / (float)pieces);
                    var b = PathPoint(from, to, (piece + 1) / (float)pieces);
                    if (_flatMap && Wraps(a, b)) continue;
                    Ribbon(a, b, colour, selected ? 1.6f : .8f);
                }
            }
        }
        if (_supportRangeVerts.Count > 0)
        {
            _supportRangeArrays[(int)Mesh.ArrayType.Vertex] = _supportRangeVerts.ToArray();
            _supportRangeArrays[(int)Mesh.ArrayType.Color] = _supportRangeColors.ToArray();
            _supportRangeMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _supportRangeArrays);
            _supportRangeArrays[(int)Mesh.ArrayType.Vertex] = default;
            _supportRangeArrays[(int)Mesh.ArrayType.Color] = default;
        }
        _supportRangeNode.Visible = _supportRangeMesh.GetSurfaceCount() > 0 && MilitaryMarkersEnabled && (_flatMap || ViewDistance <=
            Ui.Metric(_supportOverlay ? "support.overlay_distance" : "support.marker_distance", _supportOverlay ? 17f : 10.8f));

        static float LegKm((float Latitude, float Longitude) a, (float Latitude, float Longitude) b)
        {
            var radians = MathF.PI / 180f;
            var sinLat = MathF.Sin((b.Latitude - a.Latitude) * radians * .5f);
            var sinLon = MathF.Sin((b.Longitude - a.Longitude) * radians * .5f);
            var h = sinLat * sinLat + MathF.Cos(a.Latitude * radians) * MathF.Cos(b.Latitude * radians) * sinLon * sinLon;
            return 6371f * 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(h)));
        }

        Vector3 PathPoint((float Latitude, float Longitude) a, (float Latitude, float Longitude) b, float t)
        {
            // The same great circle the card travels along (LongRangeMovement.Between).
            var (lat, lon) = LongRangeMovement.Between(a, b, t);
            return _flatMap ? LocalFlatPoint(lat, lon, FlatLineZ + .0012f) : LocalSurfacePoint(lat, lon, SurfaceLineRadius * 1.0008f);
        }

        void Ribbon(Vector3 a, Vector3 b, Color colour, float width)
        {
            var worldA = _supportRangeNode.ToGlobal(a);
            var worldB = _supportRangeNode.ToGlobal(b);
            if (_camera.IsPositionBehind(worldA) || _camera.IsPositionBehind(worldB)) return;
            if (!_flatMap)
            {
                var horizon = GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length());
                var view = _camera.GlobalPosition.Normalized();
                if (a.Normalized().Dot(view) < horizon && b.Normalized().Dot(view) < horizon) return;
            }
            var screenA = _camera.UnprojectPosition(worldA);
            var screenB = _camera.UnprojectPosition(worldB);
            if (screenA.DistanceSquaredTo(screenB) < .01f) return;
            var direction = (screenB - screenA).Normalized();
            var normal = new Vector2(-direction.Y, direction.X) * width * .5f;
            var depthA = -_camera.ToLocal(worldA).Z;
            var depthB = -_camera.ToLocal(worldB).Z;
            var ai = _supportRangeNode.ToLocal(_camera.ProjectPosition(screenA - normal, depthA));
            var ao = _supportRangeNode.ToLocal(_camera.ProjectPosition(screenA + normal, depthA));
            var bi = _supportRangeNode.ToLocal(_camera.ProjectPosition(screenB - normal, depthB));
            var bo = _supportRangeNode.ToLocal(_camera.ProjectPosition(screenB + normal, depthB));
            Triangle(ai, ao, bo, colour);
            Triangle(ai, bo, bi, colour);
        }

        void Aim(SupportFormation formation, SupportTypeDefinition type)
        {
            var center = _campaign.ProvinceMap.CenterOf(formation.BaseProvinceId);
            latitude = Mathf.DegToRad(center.Latitude);
            longitude = Mathf.DegToRad(center.Longitude);
            angularReach = Math.Clamp(SupportCore.ReachKm(_campaign, type) / 6371f, .002f, MathF.PI);
        }

        void Triangle(Vector3 p, Vector3 q, Vector3 r, Color colour)
        {
            _supportRangeVerts.Add(p);
            _supportRangeColors.Add(colour);
            _supportRangeVerts.Add(q);
            _supportRangeColors.Add(colour);
            _supportRangeVerts.Add(r);
            _supportRangeColors.Add(colour);
        }

        static bool Wraps(Vector3 p, Vector3 q) => MathF.Abs(p.X - q.X) > FlatMapWidth * .5f;

        Vector3 Destination(int step, float fraction)
        {
            var reach = angularReach * fraction;
            var bearing = MathF.Tau * step / steps;
            var targetLatitude = MathF.Asin(MathF.Sin(latitude) * MathF.Cos(reach) +
                MathF.Cos(latitude) * MathF.Sin(reach) * MathF.Cos(bearing));
            var targetLongitude = longitude + MathF.Atan2(
                MathF.Sin(bearing) * MathF.Sin(reach) * MathF.Cos(latitude),
                MathF.Cos(reach) - MathF.Sin(latitude) * MathF.Sin(targetLatitude));
            var lat = Mathf.RadToDeg(targetLatitude);
            var lon = Mathf.RadToDeg(targetLongitude);
            return _flatMap ? LocalFlatPoint(lat, lon, FlatLineZ + .001f) :
                LocalSurfacePoint(lat, lon, SurfaceLineRadius * 1.0007f);
        }
    }

    /// <summary>
    /// The base card: brass frame and command band over dark slate, 96x56, with the arm's
    /// symbol blended into its slot on the left. The symbol is part of the card so that it
    /// is inside the frame by construction - it used to be a sprite of its own parked over
    /// the card, and a wide drawing simply overran the frame it was meant to sit in.
    /// </summary>
    private Texture2D SupportPlate(string path, bool selected = false, Color? arm = null, Nation nation = null)
    {
        var accent = arm ?? Ui.Colour("support.standby");
        var flagSprite = nation is not null ? FlagSpriteFor(nation) : "";
        var key = path + "|" + selected + "|" + accent.ToHtml() + "|" + (nation is null ? "" : nation.Color) + "|" + flagSprite + "|" + NationalFlagVisualId(nation);
        if (_supportPlates.TryGetValue(key, out var cached))
        {
            if (cached.Node != _supportPlatesLru.First)
            {
                _supportPlatesLru.Remove(cached.Node);
                _supportPlatesLru.AddFirst(cached.Node);
            }
            return cached.Texture;
        }
        const int width = 96, height = 56;
        const int s = 3;
        var w = width * s;
        var h = height * s;
        var national = nation is not null ? new Color(nation.Color) : accent;
        var brass = Ui.Colour("map.counter_rim");
        var line = selected ? Ui.Colour("text.accent") : national.Lightened(0.28f);
        var ink = new Color(0.025f, 0.035f, 0.045f, 0.99f);
        var topBg = new Color(0.13f, 0.165f, 0.20f, 0.96f);
        var botBg = new Color(0.055f, 0.080f, 0.105f, 0.96f);
        const float radius = 5.5f * s;
        const float bandBottom = 15.5f * s;
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var inward = -RoundedBox(x + 0.5f, y + 0.5f, w, h, radius);
            if (inward < 0f)
            {
                WritePixel(data, w, x, y, new Color(ink.R, ink.G, ink.B, 0f));
                continue;
            }
            Color colour;
            if (inward < 0.9f * s) colour = ink;
            else if (inward < 3.6f * s) colour = line;
            else if (inward < 4.5f * s) colour = selected ? Colors.White : brass.Lerp(national.Lightened(0.42f), 0.4f);
            else
            {
                colour = topBg.Lerp(botBg, y / (float)h);
                if (y < bandBottom)
                {
                    colour = national.Lightened(0.10f).Lerp(national.Darkened(0.24f), y / bandBottom);
                    if (x > 36 * s) colour = colour.Lerp(accent.Darkened(0.15f), 0.55f);
                }
                else if (y < bandBottom + 1.2f * s)
                {
                    colour = accent.Lightened(0.25f);
                }
                else if (x >= 4.5f * s && x < 7.5f * s)
                {
                    colour = accent;
                }
            }
            WritePixel(data, w, x, y, colour);
        }

        // Left glowing arm medallion slot (x = 8.5..36.5, y = 18..50).
        const float slotLeft = 8.5f * s, slotTop = 18f * s, slotRight = 36.5f * s, slotBottom = 50f * s;
        for (var y = (int)slotTop; y <= (int)slotBottom; y++)
        for (var x = (int)slotLeft; x <= (int)slotRight; x++)
        {
            var inside = -RoundedBoxAt(x + 0.5f, y + 0.5f, slotLeft, slotTop, slotRight, slotBottom, 3.2f * s);
            if (inside < 0f) continue;
            var col = inside < 1.1f * s ? accent.Lightened(0.32f) : accent.Darkened(0.55f);
            BlendPixel(data, w, x, y, col, 0.94f);
        }

        // Right recessed readiness bay (x = 39..90, y = 18..50).
        const float rLeft = 39f * s, rTop = 18f * s, rRight = 90f * s, rBottom = 50f * s;
        for (var y = (int)rTop; y <= (int)rBottom; y++)
        for (var x = (int)rLeft; x <= (int)rRight; x++)
        {
            var inside = -RoundedBoxAt(x + 0.5f, y + 0.5f, rLeft, rTop, rRight, rBottom, 2.8f * s);
            if (inside < 0f) continue;
            var col = inside < 0.9f * s ? new Color(0.22f, 0.28f, 0.34f, 0.90f) : new Color(0.04f, 0.055f, 0.075f, 0.92f);
            BlendPixel(data, w, x, y, col, 0.92f);
        }

        // Prominent National Flag badge in the top-left canton (x = 6..34, y = 3.5..15.5).
        if (!string.IsNullOrWhiteSpace(flagSprite) || nation is not null)
            PaintFlagChip(data, w, flagSprite, 6f * s, 3.5f * s, 28f * s, 12.5f * s, nation);
        // Long-range support chevron insignia on the top-right header
        var supportGold = new Color(0.96f, 0.88f, 0.52f, 0.95f);
        StrokeLine(data, w, 76f * s, 5.5f * s, 82f * s, 10.5f * s, 1.8f * s, supportGold);
        StrokeLine(data, w, 82f * s, 10.5f * s, 88f * s, 5.5f * s, 1.8f * s, supportGold);
        StrokeLine(data, w, 76f * s, 9.5f * s, 82f * s, 14.5f * s, 1.8f * s, supportGold);
        StrokeLine(data, w, 82f * s, 14.5f * s, 88f * s, 9.5f * s, 1.8f * s, supportGold);

        using var card = Image.CreateFromData(w, h, false, Image.Format.Rgba8, data);
        card.Resize(width, height, Image.Interpolation.Lanczos);
        var icon = SupportCutoutImage(path);
        if (icon is not null)
        {
            const float slot = 24f;
            var iconWidth = icon.GetWidth();
            var iconHeight = icon.GetHeight();
            var span = MathF.Max(iconWidth, iconHeight);
            if (span > slot)
            {
                using var resized = icon.Duplicate() as Image;
                if (resized is not null)
                {
                    resized.Resize(Mathf.RoundToInt(iconWidth * slot / span), Mathf.RoundToInt(iconHeight * slot / span),
                        Image.Interpolation.Lanczos);
                    iconWidth = resized.GetWidth();
                    iconHeight = resized.GetHeight();
                    card.BlendRect(resized, new Rect2I(0, 0, iconWidth, iconHeight),
                        new Vector2I(22 - iconWidth / 2, 34 - iconHeight / 2));
                }
            }
            else
            {
                card.BlendRect(icon, new Rect2I(0, 0, iconWidth, iconHeight),
                    new Vector2I(22 - iconWidth / 2, 34 - iconHeight / 2));
            }
        }
        card.GenerateMipmaps();
        var plateTex = ImageTexture.CreateFromImage(card);
        var lruNode = _supportPlatesLru.AddFirst(key);
        _supportPlates[key] = (plateTex, lruNode);
        while (_supportPlatesLru.Count > SupportPlateCap && _supportPlatesLru.Last is { } oldest)
        {
            _supportPlatesLru.RemoveLast();
            if (_supportPlates.Remove(oldest.Value, out var evicted))
                evicted.Texture?.Dispose();
        }
        return plateTex;
    }

    private Image SupportCutoutImage(string path)
    {
        if (_supportCutoutIcons.TryGetValue(path, out var cached)) return cached;
        var source = GD.Load<Texture2D>(path);
        if (source is null) return null;
        var image = source.GetImage();
        image.Convert(Image.Format.Rgba8);
        // Most catalogue icons are opaque black-on-gold UI tiles. On the world map,
        // their black backing would become a conspicuous square; only the symbol belongs
        // on the marker plate. Mods can supply genuinely transparent icons unchanged.
        if (image.GetPixel(0, 0).A > .95f)
            for (var y = 0; y < image.GetHeight(); y++)
            for (var x = 0; x < image.GetWidth(); x++)
            {
                var color = image.GetPixel(x, y);
                var brightness = MathF.Max(color.R, MathF.Max(color.G, color.B));
                if (brightness < .12f)
                    image.SetPixel(x, y, new Color(color.R, color.G, color.B, 0));
            }
        // Cut to the symbol's own bounds. The tiles are square canvases with the drawing
        // at whatever size suited it, so fitting by canvas alone let a wide plane stride
        // across the frame while a compact bomb sat small in a corner. Cut to the symbol,
        // every icon arrives with the same kind of bounds to be fitted into the slot.
        var width = image.GetWidth();
        var height = image.GetHeight();
        var left = width; var top = height; var right = -1; var bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (image.GetPixel(x, y).A < 0.03f) continue;
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }
        if (right >= left && bottom >= top &&
            (left > 0 || top > 0 || right < width - 1 || bottom < height - 1))
        {
            var cropped = image.GetRegion(new Rect2I(left, top, right - left + 1, bottom - top + 1));
            image.Dispose();
            image = cropped;
        }
        return _supportCutoutIcons[path] = image;
    }
}
