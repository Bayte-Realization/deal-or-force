using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// What each region is and what is happening in it, written on the map in icons - the way
/// Victoria 3 marks its states with their industry and their troubles, and Age of History its
/// provinces with what is built there.
///
/// Close in, every region in sight shows the badge of the industry it has most of, with the
/// number of buildings, and beside it the signs of what needs the player's eye: building under
/// way, unrest, people short of what they need. Nothing is built per region: the icons are
/// drawn over the globe in the overlay, only for regions on the near side and on the screen,
/// and never over a division's card or another icon.
/// </summary>
public sealed partial class GlobePresentation
{
    private bool _regionIconsEnabled = true;
    private readonly Dictionary<string, Texture2D> _regionIconTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (float Latitude, float Longitude)> _regionAnchors = new();
    private int _regionAnchorsRevision = -1;
    private Simulation.Campaign _regionAnchorsCampaign;
    private Transform3D _regionIconsCamera;
    private float _regionIconsSize = -1f;
    private StrategicMapMode _regionIconsMode;
    private readonly List<Rect2> _regionIconTaken = new();
    private readonly List<ScenarioState> _regionIconOrder = new();
    /// <summary>The average count of each building kind in an inhabited region, for what a region is known for.</summary>
    private readonly Dictionary<BuildingKind, float> _buildingAverage = new();

    /// <summary>Whether region icons are drawn - the player's setting ("map_region_icons").</summary>
    public bool RegionIconsEnabled
    {
        get => _regionIconsEnabled;
        set { if (_regionIconsEnabled == value) return; _regionIconsEnabled = value; QueueRedraw(); }
    }

    /// <summary>The farthest out region icons are drawn, as a view distance (skin: map.region_icon_distance).</summary>
    private static float RegionIconDistance => InterfaceSkin.Metric("map.region_icon_distance", 8.4f);

    private bool RegionIconsWanted => _regionIconsEnabled && _mapApplied && _campaign is not null &&
                                      !CountrySelectionOnly && ViewDistance <= RegionIconDistance;

    /// <summary>Asks for a redraw when the view the icons were drawn for has moved.</summary>
    private void KeepRegionIcons()
    {
        if (!RegionIconsWanted)
        {
            if (_regionIconsSize >= 0f) { _regionIconsSize = -1f; QueueRedraw(); }
            return;
        }
        var transform = _camera.GlobalTransform;
        if (_regionIconsSize < 0f || !transform.IsEqualApprox(_regionIconsCamera) || Mathf.Abs(_camera.Size - _regionIconsSize) > 0.0001f ||
            _regionIconsMode != _mapMode)
        {
            _regionIconsMode = _mapMode;
            _regionIconsCamera = transform;
            _regionIconsSize = _camera.Size;
            QueueRedraw();
        }
    }

    private Texture2D RegionIcon(string name)
    {
        if (_regionIconTextures.TryGetValue(name, out var texture)) return texture;
        texture = GD.Load<Texture2D>(SpriteCatalog.TextureOf(name, $"res://game/gfx/interface/icons/{name}.png"));
        _regionIconTextures[name] = texture;
        return texture;
    }

    /// <summary>Each region's anchor: the centre of its largest province, which is always its own ground.</summary>
    private void RefreshRegionAnchors()
    {
        if (ReferenceEquals(_regionAnchorsCampaign, _campaign) && _regionAnchorsRevision == _campaign.MapRevision) return;
        _regionAnchorsCampaign = _campaign;
        _regionAnchorsRevision = _campaign.MapRevision;
        _regionAnchors.Clear();
        _regionIconOrder.Clear();
        _regionIconOrder.AddRange(_campaign.States);
        _regionIconOrder.Sort((left, right) => right.Population.CompareTo(left.Population));
        _buildingAverage.Clear();
        var inhabited = 0;
        foreach (var state in _campaign.States)
        {
            if (state.Population <= 0 || state.Unclaimed) continue;
            inhabited++;
            foreach (var (kind, count) in state.Buildings)
                if (count > 0) _buildingAverage[kind] = _buildingAverage.GetValueOrDefault(kind) + count;
        }
        foreach (var kind in new List<BuildingKind>(_buildingAverage.Keys))
            _buildingAverage[kind] /= Math.Max(1, inhabited);
        var map = _campaign.ProvinceMap;
        if (map is null) return;
        var cells = new int[map.ProvinceCount + 1];
        foreach (var province in map.CellToProvince)
            if (province > 0 && province <= map.ProvinceCount) cells[province]++;
        var best = new Dictionary<int, int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var region = map.ProvinceToState[province];
            if (region == 0 || map.IsWater(province)) continue;
            if (!best.TryGetValue(region, out var current) || cells[province] > cells[current]) best[region] = province;
        }
        foreach (var (region, province) in best)
        {
            var centre = map.CenterOf(province);
            _regionAnchors[region] = (centre.Latitude, centre.Longitude);
        }
    }

    private void DrawRegionIcons()
    {
        if (!RegionIconsWanted || !IsInstanceValid(_capitalRoot)) return;
        RefreshRegionAnchors();
        var viewport = ViewportSize();
        var screenRect = new Rect2(Vector2.Zero, viewport);
        var cameraNormal = _flatMap ? Vector3.Zero : _camera.GlobalPosition.Normalized();
        var horizon = CityHorizon() + 0.02f;
        var size = Math.Clamp(InterfaceSkin.Metric("map.region_icon_pixels", 22f), 12f, 48f);
        var badge = size * 0.72f;
        var font = PresentationFonts.For("map.cities");
        var fontSize = Math.Max(10, (int)(size * 0.62f));
        var ink = new Color(0.03f, 0.05f, 0.08f, 0.82f);
        var rim = new Color(0.88f, 0.76f, 0.46f, 0.9f);

        _regionIconTaken.Clear();
        // Division cards and fleet plates keep their room: an icon never covers a force.
        foreach (var marker in _armyStacks.Values) ReserveIconRoom(marker.Plate);
        foreach (var parts in _fleetNodes.Values) ReserveIconRoom(parts.Plate);
        // And the cities' names: a badge over "Pyongyang" hides the one word the map most needs.
        if (CityLabelsEnabled) _regionIconTaken.AddRange(_cityTaken);
        _regionIconTaken.AddRange(_capitalNameBoxes);

        var projects = new Dictionary<int, int>();
        foreach (var project in _campaign.Construction) projects[project.StateMapIndex] = projects.GetValueOrDefault(project.StateMapIndex) + 1;

        // The most populous regions first, so where the screen is crowded the important ones keep their badge.
        var drawn = 0;
        var limit = Math.Clamp((int)InterfaceSkin.Metric("map.region_icon_limit", 140f), 0, 400);
        foreach (var state in _regionIconOrder)
        {
            if (drawn >= limit) break;
            if (state.Unclaimed || state.Population <= 0 || !_regionAnchors.TryGetValue(state.MapIndex, out var anchor)) continue;
            var local = PresentationPoint(anchor.Latitude, anchor.Longitude, SurfaceMarkerRadius);
            var world = _capitalRoot.GlobalTransform * local;
            if (!_flatMap && world.Normalized().Dot(cameraNormal) < horizon) continue;
            if (_camera.IsPositionBehind(world)) continue;
            var point = _camera.UnprojectPosition(world);
            if (!screenRect.HasPoint(point)) continue;

            // What the region is known for: the kind it has most of against an ordinary region -
            // the port of a port city, the refineries of an oil coast - rather than the farms and
            // workshops every region has, which made the whole map one icon.
            BuildingKind? main = null;
            var mainScore = 0f;
            var total = 0;
            foreach (var (kind, count) in state.Buildings)
            {
                if (count <= 0 || kind is BuildingKind.Infrastructure or BuildingKind.RailNetwork) continue;
                var score = count / MathF.Max(0.25f, _buildingAverage.GetValueOrDefault(kind));
                if (score > mainScore || score == mainScore && main is { } held && kind < held) { main = kind; mainScore = score; total = count; }
            }
            // On the resources layer the badge is the ground, not the works: the region's richest deposit.
            string groundIcon = null;
            var groundAmount = 0f;
            if (_mapMode == StrategicMapMode.Resources)
            {
                main = null;
                foreach (var (good, amount) in state.ResourceDeposits)
                    if (amount > 0.05f && amount > groundAmount) { groundAmount = amount; groundIcon = "icon_" + CommodityCatalog.IconNameOf(good); }
            }
            // And what needs an eye on it.
            var signs = new List<string>(3);
            if (projects.ContainsKey(state.MapIndex)) signs.Add("icon_alert_construction");
            if (state.Radicalism >= 0.45f) signs.Add("icon_unrest");
            if (state.BasicNeedsCoverage > 0f && state.BasicNeedsCoverage < 0.75f) signs.Add("icon_alert_supply");
            if (main is null && groundIcon is null && signs.Count == 0) continue;

            var amountText = groundIcon is null ? "" : groundAmount.ToString(groundAmount >= 10f ? "0" : "0.0");
            var width = (main is null ? 0f : size + 6f + (total > 1 ? fontSize * 1.2f : 0f)) +
                        (groundIcon is null ? 0f : size + 6f + fontSize * 0.6f * amountText.Length) + signs.Count * (badge + 2f);
            var box = new Rect2(point.X - width / 2f, point.Y - size / 2f, width, size);
            if (!screenRect.Encloses(box) || Taken(box)) continue;
            _regionIconTaken.Add(box.Grow(3f));
            drawn++;

            DrawRect(box.Grow(2f), ink, true);
            DrawRect(box.Grow(2f), rim, false, 1f);
            var x = box.Position.X;
            if (main is { } kind2 && RegionIcon(DealOrForceGame.BuildingIconName(kind2)) is { } icon)
            {
                DrawTextureRect(icon, new Rect2(x + 2f, box.Position.Y, size, size), false);
                x += size + 4f;
                if (total > 1)
                {
                    DrawString(font, new Vector2(x, box.Position.Y + size * 0.78f), total.ToString(), HorizontalAlignment.Left, -1, fontSize,
                        new Color(0.97f, 0.93f, 0.82f));
                    x += fontSize * 1.2f;
                }
            }
            if (groundIcon is not null && RegionIcon(groundIcon) is { } ground)
            {
                DrawTextureRect(ground, new Rect2(x + 2f, box.Position.Y, size, size), false);
                x += size + 4f;
                DrawString(font, new Vector2(x, box.Position.Y + size * 0.78f), amountText, HorizontalAlignment.Left, -1, fontSize,
                    new Color(0.97f, 0.93f, 0.82f));
                x += fontSize * 0.6f * amountText.Length;
            }
            foreach (var sign in signs)
            {
                if (RegionIcon(sign) is { } mark)
                    DrawTextureRect(mark, new Rect2(x, box.Position.Y + (size - badge) / 2f, badge, badge), false,
                        sign == "icon_alert_construction" ? new Color(0.9f, 0.95f, 1f) : new Color(1f, 0.55f, 0.45f));
                x += badge + 2f;
            }
        }
    }

    private bool Taken(Rect2 box)
    {
        foreach (var other in _regionIconTaken) if (other.Intersects(box)) return true;
        return false;
    }

    private void ReserveIconRoom(Sprite3D plate)
    {
        if (!IsInstanceValid(plate) || !plate.IsVisibleInTree() || _camera.IsPositionBehind(plate.GlobalPosition)) return;
        var centre = _camera.UnprojectPosition(plate.GlobalPosition);
        _regionIconTaken.Add(new Rect2(centre - new Vector2(34f, 22f), new Vector2(68f, 44f)));
    }
}
