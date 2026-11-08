using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Player-controlled long-range formations on the operational map. The catalogue owns
/// the icons and reach; this layer only turns their actual bases into readable counters.
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
    private ImmediateMesh _supportRangeMesh = null!;
    private readonly Dictionary<string, Texture2D> _supportPlates = new();
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
    private bool _supportRangeProjection;
    private Transform3D _supportRangeView;
    private Vector2 _supportRangeViewport;
    private ulong _nextSupportRangeRefreshMs;

    public int SelectedSupportId => _selectedSupportId;
    public IReadOnlyCollection<int> SelectedSupportIds => _selectedSupportIds;
    public bool SupportCommandMode { get; set; }
    public bool SupportRebaseArmed { get; set; }
    public event Action<List<SupportFormation>> SupportBoxPicked;

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
        _supportRangeMesh = new ImmediateMesh();
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
        if (_campaign?.ProvinceMap is null) return;
        var formation = _campaign.SupportFormations.Find(f => f.Id == formationId && f.NationId == _campaign.Player?.Id);
        if (formation is null || formation.BaseProvinceId <= 0) return;
        SelectSupportFormation(formationId);
        var center = _campaign.ProvinceMap.CenterOf(formation.BaseProvinceId);
        FocusOn(center.Latitude, center.Longitude, 7.8f);
    }

    private void UpdateSupportPresentation(double delta)
    {
        if (!IsInstanceValid(_supportMarkerRoot)) return;
        var show = MilitaryMarkersEnabled && _campaign?.Player is not null && _campaign.FeatureEnabled("long_range_forces") &&
                   (_flatMap || ViewDistance <= Ui.Metric(_supportOverlay ? "support.overlay_distance" : "support.marker_distance", _supportOverlay ? 17f : 9.2f));
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
        // Screen-width ribbons must follow perspective zoom too. Rebuild only while
        // the view actually changes, at most twenty times a second; no roster rebuild.
        if (_supportRangeNode.Visible && Time.GetTicksMsec() >= _nextSupportRangeRefreshMs &&
            (_supportRangeView != _camera.GlobalTransform || _supportRangeViewport != Size ||
             _supportRangeFlatZoom != _flatZoom ||
             float.IsNaN(_supportRangeFlatX))) BuildSupportRange();
    }

    private void SyncSupportMarkers()
    {
        var player = _campaign?.Player;
        var map = _campaign?.ProvinceMap;
        if (player is null || map is null) return;
        var owned = _campaign.SupportFormations
            .Where(f => f.NationId == player.Id && SupportCore.Enabled(_campaign, f.TypeId) && f.BaseProvinceId > 0 && f.BaseProvinceId <= map.ProvinceCount)
            .OrderBy(f => f.StateMapIndex).ThenBy(f => f.Id).ToArray();
        var hash = new HashCode();
        hash.Add(player.Id);
        foreach (var formation in owned)
        {
            hash.Add(formation.Id);
            hash.Add(formation.TypeId);
            hash.Add(formation.BaseProvinceId);
            hash.Add(formation.StateMapIndex);
        }
        var fingerprint = hash.ToHashCode();
        var reachHash = new HashCode();
        foreach (var formation in owned)
        {
            reachHash.Add(formation.Id); reachHash.Add(formation.BaseProvinceId);
            reachHash.Add(formation.Mission); reachHash.Add(SupportCore.Displaced(_campaign, formation));
        }
        var reachFingerprint = reachHash.ToHashCode();
        var reachChanged = _supportRangeFingerprint != reachFingerprint;
        _supportRangeFingerprint = reachFingerprint;
        if (ReferenceEquals(_supportMarkedCampaign, _campaign) && fingerprint == _supportRosterFingerprint)
        {
            foreach (var formation in owned)
                if (_supportMapMarkers.TryGetValue(formation.Id, out var marker))
                    PaintSupportMarker(marker, formation);
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
            var center = map.CenterOf(formation.BaseProvinceId);
            // Geography is not a collision-avoidance slot. Keep the actual base anchor;
            // the bounded screen layout may only use room inside this same province.
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
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                NoDepthTest = false
            };
            node.AddChild(plate);
            var readiness = new Label3D
            {
                Font = PresentationFonts.For("ui.default"),
                FontSize = 20, OutlineSize = 3, Offset = new Vector2(16, 0),
                RenderPriority = 8, OutlineRenderPriority = 7,
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

    private void PlaceSupportMarker(SupportMapMarker marker)
    {
        marker.Node.Position = _flatMap
            ? FlatPoint(marker.Latitude, marker.Longitude, FlatMarkerZ - SupportMarkerDepthBias)
            : SurfacePoint(marker.Latitude, marker.Longitude, SurfaceMarkerRadius * SupportSurfaceDepthFactor);
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
        var displaced = SupportCore.Displaced(_campaign, formation);
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
        var mission = displaced ? "!" : formation.Mission switch
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
                        ? FlatPoint(marker.Latitude, marker.Longitude, FlatMarkerZ - SupportMarkerDepthBias)
                        : SurfacePoint(marker.Latitude, marker.Longitude, SurfaceMarkerRadius * SupportSurfaceDepthFactor);
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
        if (SupportRebaseArmed && TryPick(pixel, out var destination, updateSelection: false) && destination.State is not null)
        {
            var plan = SupportCore.PreviewRebase(_campaign, _campaign.Player.Id, _selectedSupportIds, destination.State.MapIndex);
            return destination.State.DisplayName + "\n" +
                Loc.Format("Need {0} posts · {1} free · {2} reserved for training.", plan.Moving, plan.Free, plan.Reserved) + "\n" +
                (plan.Refusal.Length > 0 ? Loc.Get(plan.Refusal) : Loc.Get("Click to rebase the whole group. Esc cancels."));
        }
        if (!TryPickSupport(pixel, out var formation) || !SupportTypeCatalog.TryGet(formation.TypeId, out var type)) return "";
        var state = _campaign.States.Find(s => s.MapIndex == formation.StateMapIndex);
        return Loc.Get(type.Name) + "\n" + state?.DisplayName + "\n" +
            Loc.Format("Readiness {0:P0} · reach {1:0} km", formation.Strength, type.RangeKm) + "\n" +
            Loc.Get(formation.Mission switch
            {
                SupportCore.MissionCloseAirSupport => "Fire support",
                SupportCore.MissionAirSuperiority => "Air superiority",
                SupportCore.MissionInterdiction => "Interdiction",
                AirReconnaissance.Mission => "Reconnaissance",
                _ => "Standby / recover"
            }) + (SupportCore.Displaced(_campaign, formation) ? "\n" + Loc.Get("BASE LOST — REBASE IT") : "");
    }

    private void BuildSupportRange()
    {
        if (!IsInstanceValid(_supportRangeNode) || _campaign?.ProvinceMap is null) return;
        _supportRangeMesh.ClearSurfaces();
        var player = _campaign.Player;
        var rings = new List<(SupportFormation Formation, SupportTypeDefinition Type, bool Selected)>();
        var deduplicate = new HashSet<(int Base, string Type, string Mission, bool Selected)>();
        foreach (var candidate in _campaign.SupportFormations)
        {
            if (!SupportCore.Enabled(_campaign, candidate.TypeId)) continue;
            if (candidate.NationId != player?.Id || candidate.BaseProvinceId <= 0 || candidate.BaseProvinceId > _campaign.ProvinceMap.ProvinceCount) continue;
            var selected = _selectedSupportIds.Contains(candidate.Id);
            if (!selected && !_supportOverlay) continue;
            if (!SupportTypeCatalog.TryGet(candidate.TypeId, out var candidateType)) continue;
            if (!deduplicate.Add((candidate.BaseProvinceId, candidate.TypeId, candidate.Mission, selected))) continue;
            rings.Add((candidate, candidateType, selected));
        }
        _supportRangeProjection = _flatMap;
        _supportRangeFlatX = _flatX;
        _supportRangeFlatZoom = _flatZoom;
        _supportRangeView = _camera.GlobalTransform;
        _supportRangeViewport = Size;
        _nextSupportRangeRefreshMs = Time.GetTicksMsec() + 50;
        if (rings.Count == 0)
        {
            _supportRangeNode.Visible = false;
            return;
        }
        const int steps = 120;
        const int bands = 6;
        float latitude = 0f, longitude = 0f, angularReach = 0f;

        // What the selected formations touch, filled faintly so it reads as ground rather than
        // as a line. Bands from the base outwards along great circles, so the fill follows the
        // globe instead of cutting under it the way a flat fan would for a bomber's reach.
        var surfaceOpen = false;
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
        if (surfaceOpen) { _supportRangeMesh.SurfaceEnd(); surfaceOpen = false; }
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
        if (surfaceOpen) _supportRangeMesh.SurfaceEnd();
        _supportRangeNode.Visible = MilitaryMarkersEnabled && (_flatMap || ViewDistance <=
            Ui.Metric(_supportOverlay ? "support.overlay_distance" : "support.marker_distance", _supportOverlay ? 17f : 9.2f));

        void Aim(SupportFormation formation, SupportTypeDefinition type)
        {
            var center = _campaign.ProvinceMap.CenterOf(formation.BaseProvinceId);
            latitude = Mathf.DegToRad(center.Latitude);
            longitude = Mathf.DegToRad(center.Longitude);
            angularReach = Math.Clamp(type.RangeKm / 6371f, .002f, MathF.PI);
        }

        void Triangle(Vector3 p, Vector3 q, Vector3 r, Color colour)
        {
            // Horizon/seam clipping may discard every segment during a projection change.
            // ImmediateMesh rejects empty surfaces, so open only on the first real triangle.
            if (!surfaceOpen) { _supportRangeMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles); surfaceOpen = true; }
            _supportRangeMesh.SurfaceSetColor(colour);
            _supportRangeMesh.SurfaceAddVertex(p);
            _supportRangeMesh.SurfaceSetColor(colour);
            _supportRangeMesh.SurfaceAddVertex(q);
            _supportRangeMesh.SurfaceSetColor(colour);
            _supportRangeMesh.SurfaceAddVertex(r);
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
            return _flatMap ? FlatPoint(lat, lon, FlatLineZ + .001f) :
                SurfacePoint(lat, lon, SurfaceLineRadius * 1.0007f);
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
        if (_supportPlates.TryGetValue(key, out var cached)) return cached;
        const int width = 96, height = 56;
        const int s = 3;
        var w = width * s;
        var h = height * s;
        var national = nation is not null ? new Color(nation.Color) : accent;
        var brass = new Color(0.84f, 0.72f, 0.44f);
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

        var card = Image.CreateFromData(w, h, false, Image.Format.Rgba8, data);
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
                icon = icon.Duplicate() as Image;
                icon.Resize(Mathf.RoundToInt(iconWidth * slot / span), Mathf.RoundToInt(iconHeight * slot / span),
                    Image.Interpolation.Lanczos);
                iconWidth = icon.GetWidth();
                iconHeight = icon.GetHeight();
            }
            card.BlendRect(icon, new Rect2I(0, 0, iconWidth, iconHeight),
                new Vector2I(22 - iconWidth / 2, 34 - iconHeight / 2));
        }
        return _supportPlates[key] = ImageTexture.CreateFromImage(card);
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
            image = image.GetRegion(new Rect2I(left, top, right - left + 1, bottom - top + 1));
        return _supportCutoutIcons[path] = image;
    }
}
