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
    private sealed class SupportMapMarker
    {
        public Node3D Node = null!;
        public Sprite3D Plate = null!;
        public Label3D Readiness = null!;
        public SupportFormation Formation = null!;
        public float Latitude;
        public float Longitude;
        public Vector3 Normal;
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
    private int _selectedSupportId = -1;
    private readonly HashSet<int> _selectedSupportIds = new();
    private bool _supportOverlay;
    private double _supportPresentationDelay;
    private float _supportRangeFlatX = float.NaN;
    private bool _supportRangeProjection;

    public int SelectedSupportId => _selectedSupportId;
    public IReadOnlyCollection<int> SelectedSupportIds => _selectedSupportIds;

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
            _supportRangeFlatX = float.NaN;
            BuildSupportRange();
        }
    }

    /// <summary>Several formations selected at once - the panel's selection, mirrored on the map.</summary>
    public void SelectSupportFormations(IEnumerable<int> ids)
    {
        _selectedSupportIds.Clear();
        foreach (var id in ids) _selectedSupportIds.Add(id);
        _selectedSupportId = _selectedSupportIds.Count == 1 ? _selectedSupportIds.First() : _selectedSupportIds.Count > 0 ? _selectedSupportIds.Min() : -1;
        _supportRangeFlatX = float.NaN;
        BuildSupportRange();
        RefreshSupportMarkerSelection();
    }

    /// <summary>The colour of an arm: sky blue aircraft, teal drones, amber guns, red missile brigades.</summary>
    private static Color SupportArmColour(SupportKind kind) => kind switch
    {
        SupportKind.Air => new Color(.46f, .74f, 1f),
        SupportKind.Drone => new Color(.38f, .90f, .80f),
        SupportKind.AirDefence => new Color(1f, .46f, .42f),
        _ => new Color(1f, .78f, .36f)
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
        var show = MilitaryMarkersEnabled && _campaign?.Player is not null &&
                   ViewDistance <= (_supportOverlay ? 17f : 9.2f);
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
        UpdateMarkerVisibility(_supportMarkerRoot, _supportVisibility, true);
        // Flat-map wrapping changes when the camera crosses the seam. Redraw the small
        // selected ring only then, never the formation roster on every camera frame.
        if (_flatMap && (float.IsNaN(_supportRangeFlatX) || MathF.Abs(_supportRangeFlatX - _flatX) > .12f))
            BuildSupportRange();
    }

    private void SyncSupportMarkers()
    {
        var player = _campaign?.Player;
        var map = _campaign?.ProvinceMap;
        if (player is null || map is null) return;
        var owned = _campaign.SupportFormations
            .Where(f => f.NationId == player.Id && f.BaseProvinceId > 0 && f.BaseProvinceId <= map.ProvinceCount)
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
        if (ReferenceEquals(_supportMarkedCampaign, _campaign) && fingerprint == _supportRosterFingerprint)
        {
            foreach (var formation in owned)
                if (_supportMapMarkers.TryGetValue(formation.Id, out var marker))
                    PaintSupportMarker(marker, formation);
            return;
        }
        _supportMarkedCampaign = _campaign;
        _supportRosterFingerprint = fingerprint;
        foreach (var marker in _supportMapMarkers.Values) marker.Node.QueueFree();
        _supportMapMarkers.Clear();
        _supportVisibility.Clear();
        var withinState = new Dictionary<int, int>();
        foreach (var formation in owned)
        {
            if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) continue;
            var slot = withinState.GetValueOrDefault(formation.StateMapIndex);
            withinState[formation.StateMapIndex] = slot + 1;
            var center = map.CenterOf(formation.BaseProvinceId);
            // Several wings at the same airbase must not become one unclickable icon.
            var latitude = Mathf.Clamp(center.Latitude + (slot / 3) * .18f, -89f, 89f);
            var longitude = center.Longitude + (slot % 3 - 1) * .22f;
            var node = new Node3D { Name = $"LongRange_{formation.Id}" };
            _supportMarkerRoot.AddChild(node);
            // The symbol is part of the card now: cut out and fitted into its slot on the
            // left when the card is built. It used to be a sprite parked over the card at a
            // hard-coded size, so a catalogue tile with a wide drawing on it - a plane, say
            // - simply overran the frame it was supposed to sit in.
            var iconPath = SpriteCatalog.TextureOf($"icon_{type.Icon}", $"res://game/gfx/interface/icons/icon_{type.Icon}.png");
            var plate = new Sprite3D
            {
                Texture = SupportPlate(iconPath),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                NoDepthTest = true
            };
            node.AddChild(plate);
            var readiness = new Label3D
            {
                FontSize = 20, OutlineSize = 5,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
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
            ? FlatPoint(marker.Latitude, marker.Longitude, FlatMarkerZ + .002f)
            : SurfacePoint(marker.Latitude, marker.Longitude, SurfaceMarkerRadius * 1.0004f);
        var flatScale = MathF.Max(.00001f, _counterWorldPixel);
        marker.Node.Scale = _flatMap ? Vector3.One * flatScale : Vector3.One;
        marker.Plate.FixedSize = !_flatMap;
        marker.Readiness.FixedSize = !_flatMap;
        // Everything below is laid out in the card's own pixels (80x48), so the flat map
        // and the globe draw the same marker and one tuning moves both. The readiness
        // number sits in the middle of the card - where the marker is anchored - and the
        // symbol is drawn inside the card to the left of it. A label parked beside its
        // card by an offset drifts away from it as the camera moves; at the origin there
        // is no offset left to drift.
        // The flat map's strategic zoom shrinks the marker with the armies and the fleets,
        // from the same metric - everything on the map heeds the same curve.
        var platePixel = _flatMap ? .72f * _counterFlatFar : .00027f * _counterScale;
        marker.Plate.PixelSize = platePixel;
        marker.Readiness.PixelSize = platePixel * .85f;
        marker.Readiness.Position = new Vector3(0f, 0f, _flatMap ? 3f : .004f);
        marker.Readiness.HorizontalAlignment = HorizontalAlignment.Center;
        marker.Readiness.VerticalAlignment = VerticalAlignment.Center;
    }

    private void PaintSupportMarker(SupportMapMarker marker, SupportFormation formation)
    {
        marker.Formation = formation;
        var displaced = SupportCore.Displaced(_campaign, formation);
        var active = formation.Mission != SupportCore.MissionStandby;
        marker.Plate.Modulate = displaced ? new Color(.76f, .35f, .34f) :
            active ? new Color(.92f, .80f, .49f) : new Color(.77f, .87f, .92f);
        var readiness = Math.Clamp((int)MathF.Round(formation.Strength * 100f), 0, 100).ToString();
        if (marker.Readiness.Text != readiness) marker.Readiness.Text = readiness;
        marker.Readiness.Modulate = formation.Strength >= .7f ? new Color(.80f, .94f, .81f) :
            formation.Strength >= .35f ? new Color(1f, .85f, .53f) : new Color(1f, .56f, .52f);
    }

    private void RefreshSupportMarkerSelection()
    {
        foreach (var (id, marker) in _supportMapMarkers)
        {
            var selected = _selectedSupportIds.Contains(id);
            marker.Plate.Scale = Vector3.One * (selected ? 1.22f : 1f);
            marker.Readiness.Scale = marker.Plate.Scale;
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
                        ? FlatPoint(marker.Latitude, marker.Longitude, FlatMarkerZ + .002f)
                        : SurfacePoint(marker.Latitude, marker.Longitude, SurfaceMarkerRadius * 1.0004f);
                    if (marker.Node.Position != position) marker.Node.Position = position;
                }
            }
        if (_supportRangeProjection != _flatMap) BuildSupportRange();
    }

    private bool TryPickSupport(Vector2 pixel, out SupportFormation formation)
    {
        formation = null!;
        if (!_supportMarkerRoot.Visible || !IsInstanceValid(_camera)) return false;
        var best = 22f * 22f;
        foreach (var marker in _supportMapMarkers.Values)
        {
            if (!IsInstanceValid(marker.Node) || !marker.Node.Visible || _camera.IsPositionBehind(marker.Node.GlobalPosition)) continue;
            var distance = _camera.UnprojectPosition(marker.Node.GlobalPosition).DistanceSquaredTo(pixel);
            if (distance >= best) continue;
            best = distance;
            formation = marker.Formation;
        }
        if (formation is null) return false;
        SelectSupportFormation(formation.Id);
        return true;
    }

    private void BuildSupportRange()
    {
        if (!IsInstanceValid(_supportRangeNode) || _campaign?.ProvinceMap is null) return;
        _supportRangeMesh.ClearSurfaces();
        var player = _campaign.Player;
        var rings = new List<(SupportFormation Formation, SupportTypeDefinition Type, bool Selected)>();
        foreach (var candidate in _campaign.SupportFormations)
        {
            if (candidate.NationId != player?.Id || candidate.BaseProvinceId <= 0) continue;
            var selected = _selectedSupportIds.Contains(candidate.Id);
            if (!selected && !_supportOverlay) continue;
            if (!SupportTypeCatalog.TryGet(candidate.TypeId, out var candidateType)) continue;
            rings.Add((candidate, candidateType, selected));
        }
        _supportRangeProjection = _flatMap;
        _supportRangeFlatX = _flatX;
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
        var anyFill = false;
        foreach (var (formation, type, selected) in rings)
        {
            if (!selected || SupportCore.Displaced(_campaign, formation)) continue;
            if (!anyFill) { _supportRangeMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles); anyFill = true; }
            Aim(formation, type);
            var fill = SupportArmColour(type.Kind) with { A = .07f };
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
        if (anyFill) _supportRangeMesh.SurfaceEnd();

        _supportRangeMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
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
                var start = Destination(segment, 1f);
                var end = Destination(segment + 1, 1f);
                if (_flatMap && Wraps(start, end)) continue;
                _supportRangeMesh.SurfaceSetColor(colour);
                _supportRangeMesh.SurfaceAddVertex(start);
                _supportRangeMesh.SurfaceSetColor(colour);
                _supportRangeMesh.SurfaceAddVertex(end);
            }
        }
        _supportRangeMesh.SurfaceEnd();
        _supportRangeNode.Visible = MilitaryMarkersEnabled && ViewDistance <= (_supportOverlay ? 17f : 9.2f);

        void Aim(SupportFormation formation, SupportTypeDefinition type)
        {
            var center = _campaign.ProvinceMap.CenterOf(formation.BaseProvinceId);
            latitude = Mathf.DegToRad(center.Latitude);
            longitude = Mathf.DegToRad(center.Longitude);
            angularReach = Math.Clamp(type.RangeKm / 6371f, .002f, MathF.PI);
        }

        void Triangle(Vector3 p, Vector3 q, Vector3 r, Color colour)
        {
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
    /// The base card: brass frame and command band over dark slate, 80x48, with the arm's
    /// symbol blended into its slot on the left. The symbol is part of the card so that it
    /// is inside the frame by construction - it used to be a sprite of its own parked over
    /// the card, and a wide drawing simply overran the frame it was meant to sit in.
    /// </summary>
    private Texture2D SupportPlate(string path)
    {
        if (_supportPlates.TryGetValue(path, out var cached)) return cached;
        const int width = 80, height = 48;
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var border = x < 2 || x >= width - 2 || y < 2 || y >= height - 2;
            var top = y < 6;
            var stripe = x >= 70 && x < 75;
            var brass = border || top || stripe;
            var r = brass ? (byte)201 : (byte)15;
            var g = brass ? (byte)166 : (byte)32;
            var b = brass ? (byte)91 : (byte)48;
            var offset = (y * width + x) * 4;
            data[offset] = r; data[offset + 1] = g; data[offset + 2] = b;
            data[offset + 3] = (byte)(border ? 245 : 226);
        }
        var card = Image.CreateFromData(width, height, false, Image.Format.Rgba8, data);
        var icon = SupportCutoutImage(path);
        if (icon is not null)
        {
            // Fitted to the slot whatever bounds the cut-out came to: a wide plane and a
            // compact drone share one footprint here, both wholly inside the frame.
            const float slot = 20f;
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
                new Vector2I(14 - iconWidth / 2, 24 - iconHeight / 2));
        }
        return _supportPlates[path] = ImageTexture.CreateFromImage(card);
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
