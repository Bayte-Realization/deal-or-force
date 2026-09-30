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
        public Sprite3D Icon = null!;
        public Label3D Readiness = null!;
        public SupportFormation Formation = null!;
        public float Latitude;
        public float Longitude;
        public Vector3 Normal;
    }

    private Node3D _supportMarkerRoot = null!;
    private MeshInstance3D _supportRangeNode = null!;
    private ImmediateMesh _supportRangeMesh = null!;
    private Texture2D _supportPlateTexture = null!;
    private readonly Dictionary<int, SupportMapMarker> _supportMapMarkers = new();
    private readonly Dictionary<string, Texture2D> _supportCutoutIcons = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _supportVisibility = new();
    private Campaign _supportMarkedCampaign;
    private int _supportRosterFingerprint = int.MinValue;
    private int _selectedSupportId = -1;
    private double _supportPresentationDelay;
    private float _supportRangeFlatX = float.NaN;
    private bool _supportRangeProjection;

    public int SelectedSupportId => _selectedSupportId;

    private void CreateSupportPresentation(Node3D world)
    {
        _supportMarkerRoot = new Node3D { Name = "LongRangeBases" };
        world.AddChild(_supportMarkerRoot);
        _supportPlateTexture = CreateSupportPlate();
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
        var show = MilitaryMarkersEnabled && _campaign?.Player is not null && ViewDistance <= 9.2f;
        _supportMarkerRoot.Visible = show;
        _supportRangeNode.Visible = show && _selectedSupportId >= 0 && _supportRangeMesh.GetSurfaceCount() > 0;
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
            var plate = new Sprite3D
            {
                Texture = _supportPlateTexture,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                NoDepthTest = true
            };
            node.AddChild(plate);
            var iconPath = SpriteCatalog.TextureOf($"icon_{type.Icon}", $"res://game/gfx/interface/icons/icon_{type.Icon}.png");
            var icon = new Sprite3D
            {
                Texture = SupportCutoutIcon(iconPath),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                NoDepthTest = true
            };
            node.AddChild(icon);
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
                Node = node, Plate = plate, Icon = icon, Readiness = readiness,
                Formation = formation, Latitude = latitude, Longitude = longitude, Normal = normal
            };
            _supportMapMarkers[formation.Id] = marker;
            _supportVisibility.Add((node, normal));
            PlaceSupportMarker(marker);
            PaintSupportMarker(marker, formation);
        }
        if (!_supportMapMarkers.ContainsKey(_selectedSupportId)) _selectedSupportId = -1;
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
        marker.Icon.FixedSize = !_flatMap;
        marker.Readiness.FixedSize = !_flatMap;
        marker.Plate.PixelSize = _flatMap ? .72f : .00027f * _counterScale;
        marker.Icon.PixelSize = _flatMap ? .24f : .000085f * _counterScale;
        marker.Readiness.PixelSize = _flatMap ? .50f : .00022f * _counterScale;
        marker.Icon.Position = _flatMap ? new Vector3(-8, 2, 2) : new Vector3(-.0045f, .001f, .003f);
        marker.Readiness.Position = _flatMap ? new Vector3(10, -5, 3) : new Vector3(.005f, -.003f, .004f);
    }

    private void PaintSupportMarker(SupportMapMarker marker, SupportFormation formation)
    {
        marker.Formation = formation;
        var displaced = SupportCore.Displaced(_campaign, formation);
        var active = formation.Mission != SupportCore.MissionStandby;
        marker.Plate.Modulate = displaced ? new Color(.76f, .35f, .34f) :
            active ? new Color(.92f, .80f, .49f) : new Color(.77f, .87f, .92f);
        marker.Icon.Modulate = displaced ? new Color(1f, .76f, .70f) : Colors.White;
        var readiness = Math.Clamp((int)MathF.Round(formation.Strength * 100f), 0, 100).ToString();
        if (marker.Readiness.Text != readiness) marker.Readiness.Text = readiness;
        marker.Readiness.Modulate = formation.Strength >= .7f ? new Color(.80f, .94f, .81f) :
            formation.Strength >= .35f ? new Color(1f, .85f, .53f) : new Color(1f, .56f, .52f);
    }

    private void RefreshSupportMarkerSelection()
    {
        foreach (var (id, marker) in _supportMapMarkers)
        {
            var selected = id == _selectedSupportId;
            marker.Plate.Scale = Vector3.One * (selected ? 1.22f : 1f);
            marker.Icon.Scale = marker.Plate.Scale;
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
        var formation = _campaign.SupportFormations.Find(f => f.Id == _selectedSupportId && f.NationId == _campaign.Player?.Id);
        if (formation is null || !SupportTypeCatalog.TryGet(formation.TypeId, out var type) || formation.BaseProvinceId <= 0)
        {
            _supportRangeNode.Visible = false;
            return;
        }
        var center = _campaign.ProvinceMap.CenterOf(formation.BaseProvinceId);
        var latitude = Mathf.DegToRad(center.Latitude);
        var longitude = Mathf.DegToRad(center.Longitude);
        var angularReach = Math.Clamp(type.RangeKm / 6371f, .002f, MathF.PI);
        _supportRangeMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        const int steps = 120;
        var colour = formation.Mission == SupportCore.MissionStandby
            ? new Color(.65f, .81f, .87f, .72f) : new Color(1f, .81f, .38f, .88f);
        for (var segment = 0; segment < steps; segment++)
        {
            var start = Destination(segment);
            var end = Destination(segment + 1);
            if (_flatMap && MathF.Abs(start.X - end.X) > FlatMapWidth * .5f) continue;
            _supportRangeMesh.SurfaceSetColor(colour);
            _supportRangeMesh.SurfaceAddVertex(start);
            _supportRangeMesh.SurfaceSetColor(colour);
            _supportRangeMesh.SurfaceAddVertex(end);
        }
        _supportRangeMesh.SurfaceEnd();
        _supportRangeProjection = _flatMap;
        _supportRangeFlatX = _flatX;
        _supportRangeNode.Visible = MilitaryMarkersEnabled && ViewDistance <= 9.2f;

        Vector3 Destination(int step)
        {
            var bearing = MathF.Tau * step / steps;
            var targetLatitude = MathF.Asin(MathF.Sin(latitude) * MathF.Cos(angularReach) +
                MathF.Cos(latitude) * MathF.Sin(angularReach) * MathF.Cos(bearing));
            var targetLongitude = longitude + MathF.Atan2(
                MathF.Sin(bearing) * MathF.Sin(angularReach) * MathF.Cos(latitude),
                MathF.Cos(angularReach) - MathF.Sin(latitude) * MathF.Sin(targetLatitude));
            var lat = Mathf.RadToDeg(targetLatitude);
            var lon = Mathf.RadToDeg(targetLongitude);
            return _flatMap ? FlatPoint(lat, lon, FlatLineZ + .001f) :
                SurfacePoint(lat, lon, SurfaceLineRadius * 1.0007f);
        }
    }

    private static Texture2D CreateSupportPlate()
    {
        const int width = 64, height = 48;
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var border = x < 2 || x >= width - 2 || y < 2 || y >= height - 2;
            var top = y < 6;
            var r = border || top ? (byte)201 : (byte)15;
            var g = border || top ? (byte)166 : (byte)32;
            var b = border || top ? (byte)91 : (byte)48;
            var offset = (y * width + x) * 4;
            data[offset] = r; data[offset + 1] = g; data[offset + 2] = b;
            data[offset + 3] = (byte)(border ? 245 : 226);
        }
        return ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, data));
    }

    private Texture2D SupportCutoutIcon(string path)
    {
        if (_supportCutoutIcons.TryGetValue(path, out var cached)) return cached;
        var source = GD.Load<Texture2D>(path);
        if (source is null) return _supportCutoutIcons[path] = _supportPlateTexture;
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
        return _supportCutoutIcons[path] = ImageTexture.CreateFromImage(image);
    }
}
