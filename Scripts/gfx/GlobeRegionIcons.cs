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
/// Close in, every region in sight wears a badge of what it is known for, with how many, and
/// beside it the signs of what needs the player's eye: building under way, unrest, people short
/// of what they need. On the resources layer the badge is the region's richest deposit.
///
/// Steadiness is the whole design. Which regions carry a badge is decided only when the camera
/// has come to rest (or the zoom has moved a good way); while the map is being dragged the same
/// badges ride along with it and nothing is re-decided, so nothing pops in and out under the
/// cursor. A badge already shown keeps its place at the next decision before a new one is
/// considered, and every badge fades in and out rather than blinking. Each sits at its region's
/// own middle - the province nearest the region's area-weighted centre - not at the centre of
/// whichever province happened to be largest.
/// </summary>
public sealed partial class GlobePresentation
{
    private sealed class RegionBadge
    {
        public int Region;
        public float Latitude, Longitude;
        public bool Wanted;
        public float Alpha;
        public BuildingKind? Main;
        public int Count;
        public string Ground;
        public string Amount = "";
        public readonly List<string> Signs = new(3);
        public float Width;
        /// <summary>Where the badge sits against its region's middle, chosen at layout and kept until the next.</summary>
        public Vector2 Offset;
    }

    private bool _regionIconsEnabled = true;
    private readonly Dictionary<string, Texture2D> _regionIconTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (float Latitude, float Longitude)> _regionAnchors = new();
    private readonly Dictionary<int, RegionBadge> _regionBadges = new();
    private int _regionAnchorsRevision = -1;
    private Simulation.Campaign _regionAnchorsCampaign;
    private readonly List<ScenarioState> _regionIconOrder = new();
    /// <summary>The average count of each building kind in an inhabited region, for what a region is known for.</summary>
    private readonly Dictionary<BuildingKind, float> _buildingAverage = new();
    private readonly List<Rect2> _regionIconTaken = new();

    // When the layout was last decided, and what the camera looked like then and last frame.
    private Transform3D _badgeLastCamera;
    private float _badgeLastSize = -1f;
    private float _badgeLayoutDistance = -1f;
    private double _badgeStill;
    private double _badgeDataAge;
    private double _badgeMovingClock;
    private bool _badgeLayoutDue = true;
    private StrategicMapMode _badgeMode;

    /// <summary>Whether region icons are drawn - the player's setting ("map_region_icons").</summary>
    public bool RegionIconsEnabled
    {
        get => _regionIconsEnabled;
        set { if (_regionIconsEnabled == value) return; _regionIconsEnabled = value; _badgeLayoutDue = true; QueueRedraw(); }
    }

    /// <summary>The farthest out region icons are drawn, as a view distance (skin: map.region_icon_distance).</summary>
    private static float RegionIconDistance => InterfaceSkin.Metric("map.region_icon_distance", 8.4f);

    private bool RegionIconsWanted => _regionIconsEnabled && _mapApplied && _campaign is not null &&
                                      !CountrySelectionOnly && ViewDistance <= RegionIconDistance;

    /// <summary>
    /// Every frame: decides when the layout may be redone, fades the badges, and asks for a redraw
    /// while anything on the overlay is moving.
    /// </summary>
    private void KeepRegionIcons(double delta)
    {
        var wanted = RegionIconsWanted;
        var transform = _camera.GlobalTransform;
        var moved = _badgeLastSize < 0f || !transform.IsEqualApprox(_badgeLastCamera) || Mathf.Abs(_camera.Size - _badgeLastSize) > 0.0001f;
        _badgeLastCamera = transform;
        _badgeLastSize = _camera.Size;
        _badgeStill = moved ? 0 : _badgeStill + delta;
        _badgeDataAge += delta;

        if (wanted)
        {
            // A new decision once the view has rested a moment, after a long zoom, a new layer,
            // or every few seconds of rest for what changed in the world (a project begun, unrest).
            var distance = ViewDistance;
            if (_badgeMode != _mapMode) { _badgeMode = _mapMode; _badgeLayoutDue = true; }
            if (_badgeLayoutDistance > 0f && MathF.Abs(distance - _badgeLayoutDistance) / _badgeLayoutDistance > 0.18f) _badgeLayoutDue = true;
            if (_badgeStill >= 0.18 && (_badgeLayoutDue || _badgeDataAge >= 3.0))
            {
                LayOutRegionBadges();
                _badgeLayoutDue = false;
                _badgeDataAge = 0;
                _badgeLayoutDistance = distance;
            }
            else if (moved)
            {
                // While the view moves, a lighter pass four times a second: the badges already
                // shown stay exactly where they are, and regions coming into view get theirs at
                // once rather than when the drag ends - which made the icons look as if the right
                // button switched them on.
                _badgeLayoutDue = true;
                _badgeMovingClock += delta;
                if (_badgeMovingClock >= 0.25)
                {
                    _badgeMovingClock = 0;
                    LayOutRegionBadges(moving: true);
                    _badgeLayoutDistance = distance;
                }
            }
        }

        // Fades: in over a fifth of a second, out a little quicker.
        var animating = false;
        foreach (var badge in _regionBadges.Values)
        {
            var target = wanted && badge.Wanted ? 1f : 0f;
            if (Mathf.IsEqualApprox(badge.Alpha, target)) { badge.Alpha = target; continue; }
            badge.Alpha = Mathf.MoveToward(badge.Alpha, target, (float)delta * (target > badge.Alpha ? 5f : 7f));
            animating = true;
        }
        if (animating || moved && wanted) QueueRedraw();
    }

    private Texture2D RegionIcon(string name)
    {
        if (_regionIconTextures.TryGetValue(name, out var texture)) return texture;
        texture = ResourceLoader.Exists(SpriteCatalog.TextureOf(name, $"res://game/gfx/interface/icons/{name}.png"))
            ? GD.Load<Texture2D>(SpriteCatalog.TextureOf(name, $"res://game/gfx/interface/icons/{name}.png"))
            : null;
        _regionIconTextures[name] = texture;
        return texture;
    }

    /// <summary>
    /// Each region's middle: the area-weighted centre of its provinces, moved to the region's own
    /// province nearest it, so a crescent-shaped region is not marked in its neighbour's sea.
    /// </summary>
    private void RefreshRegionAnchors()
    {
        if (ReferenceEquals(_regionAnchorsCampaign, _campaign) && _regionAnchorsRevision == _campaign.MapRevision) return;
        _regionAnchorsCampaign = _campaign;
        _regionAnchorsRevision = _campaign.MapRevision;
        _regionAnchors.Clear();
        _regionIconOrder.Clear();
        _regionIconOrder.AddRange(_campaign.States);
        _regionIconOrder.Sort((left, right) => right.Population != left.Population
            ? right.Population.CompareTo(left.Population) : left.MapIndex.CompareTo(right.MapIndex));
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
        var members = new Dictionary<int, List<int>>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var region = map.ProvinceToState[province];
            if (region == 0 || map.IsWater(province)) continue;
            if (!members.TryGetValue(region, out var list)) members[region] = list = new List<int>();
            list.Add(province);
        }
        foreach (var (region, list) in members)
        {
            // Longitudes unwrapped against the first province, so a region across the date line averages right.
            var first = map.CenterOf(list[0]);
            double lat = 0, lon = 0, weight = 0;
            foreach (var province in list)
            {
                var centre = map.CenterOf(province);
                var w = Math.Max(1, cells[province]);
                var dl = centre.Longitude - first.Longitude;
                if (dl > 180f) dl -= 360f; else if (dl < -180f) dl += 360f;
                lat += centre.Latitude * (double)w;
                lon += (first.Longitude + dl) * (double)w;
                weight += w;
            }
            var meanLat = (float)(lat / weight);
            var meanLon = (float)(lon / weight);
            var best = list[0];
            var bestDistance = float.MaxValue;
            var cos = MathF.Cos(meanLat * MathF.PI / 180f);
            foreach (var province in list)
            {
                var centre = map.CenterOf(province);
                var dl = centre.Longitude - meanLon;
                if (dl > 180f) dl -= 360f; else if (dl < -180f) dl += 360f;
                var d = (centre.Latitude - meanLat) * (centre.Latitude - meanLat) + dl * cos * dl * cos;
                if (d < bestDistance) { bestDistance = d; best = province; }
            }
            var anchor = map.CenterOf(best);
            _regionAnchors[region] = (anchor.Latitude, anchor.Longitude);
        }
    }

    /// <summary>What a region's badge says now: what it is known for, or its richest deposit, and its signs.</summary>
    private void FillRegionBadge(RegionBadge badge, ScenarioState state, Dictionary<int, int> projects, float iconSize, int fontSize, float signSize)
    {
        badge.Main = null; badge.Count = 0; badge.Ground = null; badge.Amount = ""; badge.Signs.Clear();
        if (_mapMode == StrategicMapMode.Resources)
        {
            var best = 0f;
            foreach (var (good, amount) in state.ResourceDeposits)
                if (amount > 0.05f && amount > best) { best = amount; badge.Ground = "icon_" + CommodityCatalog.IconNameOf(good); }
            if (badge.Ground is not null) badge.Amount = best.ToString(best >= 10f ? "0" : "0.0");
        }
        else
        {
            // The kind it has most of against an ordinary region: the port of a port city, not the farms everybody has.
            var bestScore = 0f;
            foreach (var (kind, count) in state.Buildings)
            {
                if (count <= 0 || kind is BuildingKind.Infrastructure or BuildingKind.RailNetwork) continue;
                var score = count / MathF.Max(0.25f, _buildingAverage.GetValueOrDefault(kind));
                if (score > bestScore || score == bestScore && badge.Main is { } held && kind < held)
                { badge.Main = kind; bestScore = score; badge.Count = count; }
            }
        }
        if (projects.ContainsKey(state.MapIndex)) badge.Signs.Add("icon_alert_construction");
        if (state.Radicalism >= 0.45f) badge.Signs.Add("icon_unrest");
        if (state.BasicNeedsCoverage > 0f && state.BasicNeedsCoverage < 0.75f) badge.Signs.Add("icon_alert_supply");
        var label = badge.Main is not null ? (badge.Count > 1 ? badge.Count.ToString() : "") : badge.Amount;
        badge.Width = (badge.Main is not null || badge.Ground is not null ? iconSize + 6f + label.Length * fontSize * 0.62f : 0f) +
                      badge.Signs.Count * (signSize + 2f) + 4f;
    }

    private (float Icon, float Sign, int Font) BadgeMetrics()
    {
        var size = Math.Clamp(InterfaceSkin.Metric("map.region_icon_pixels", 22f), 12f, 48f);
        return (size, size * 0.72f, Math.Max(10, (int)(size * 0.62f)));
    }

    /// <summary>
    /// Which regions carry a badge from now until the next decision. Badges already shown are
    /// kept first, where they still fit; the rest by population. Forces, city names and capitals
    /// keep their room as they stand at this moment.
    /// </summary>
    private void LayOutRegionBadges(bool moving = false)
    {
        RefreshRegionAnchors();
        var (iconSize, signSize, fontSize) = BadgeMetrics();
        var viewport = ViewportSize();
        var screenRect = new Rect2(Vector2.Zero, viewport);
        _regionIconTaken.Clear();
        foreach (var marker in _armyStacks.Values) ReserveIconRoom(marker.Plate);
        foreach (var parts in _fleetNodes.Values) ReserveIconRoom(parts.Plate);
        if (CityLabelsEnabled) _regionIconTaken.AddRange(_cityTaken);
        _regionIconTaken.AddRange(_capitalNameBoxes);

        var projects = new Dictionary<int, int>();
        foreach (var project in _campaign.Construction) projects[project.StateMapIndex] = projects.GetValueOrDefault(project.StateMapIndex) + 1;
        var limit = Math.Clamp((int)InterfaceSkin.Metric("map.region_icon_limit", 160f), 0, 400);
        var placed = 0;
        var keep = new HashSet<int>();

        void Consider(ScenarioState state)
        {
            if (placed >= limit || keep.Contains(state.MapIndex)) return;
            if (state.Unclaimed || state.Population <= 0 || !_regionAnchors.TryGetValue(state.MapIndex, out var anchor)) return;
            if (BadgeScreenPoint(anchor.Latitude, anchor.Longitude) is not { } point || !screenRect.HasPoint(point)) return;
            if (!_regionBadges.TryGetValue(state.MapIndex, out var badge))
                _regionBadges[state.MapIndex] = badge = new RegionBadge { Region = state.MapIndex };
            badge.Latitude = anchor.Latitude; badge.Longitude = anchor.Longitude;
            FillRegionBadge(badge, state, projects, iconSize, fontSize, signSize);
            if (badge.Main is null && badge.Ground is null && badge.Signs.Count == 0) return;
            // The middle first; where a city's name or a force stands there, just below, above or
            // beside it - still on the region - rather than no badge at all. The place found is kept
            // (and tried first next time), so a badge does not hop between them as the view moves.
            if (moving && badge.Wanted && badge.Alpha > 0.5f)
            {
                var kept = new Rect2(point.X + badge.Offset.X - badge.Width / 2f, point.Y + badge.Offset.Y - iconSize / 2f,
                    badge.Width, iconSize).Grow(3f);
                if (screenRect.Encloses(kept)) { _regionIconTaken.Add(kept); keep.Add(state.MapIndex); placed++; return; }
            }
            var step = iconSize + 6f;
            var tries = new[] { badge.Offset, Vector2.Zero, new Vector2(0f, step), new Vector2(0f, -step),
                new Vector2(badge.Width * 0.6f + 4f, 0f), new Vector2(-badge.Width * 0.6f - 4f, 0f) };
            Rect2? found = null;
            foreach (var offset in tries)
            {
                var box = new Rect2(point.X + offset.X - badge.Width / 2f, point.Y + offset.Y - iconSize / 2f, badge.Width, iconSize).Grow(3f);
                if (!screenRect.Encloses(box)) continue;
                var free = true;
                foreach (var other in _regionIconTaken) if (other.Intersects(box)) { free = false; break; }
                if (!free) continue;
                found = box;
                badge.Offset = offset;
                break;
            }
            if (found is not { } placedBox) return;
            _regionIconTaken.Add(placedBox);
            keep.Add(state.MapIndex);
            placed++;
        }

        // The ones on the screen already, first - most populous first among them too - then the rest.
        foreach (var state in _regionIconOrder)
            if (_regionBadges.TryGetValue(state.MapIndex, out var shown) && shown.Wanted && shown.Alpha > 0.5f) Consider(state);
        foreach (var state in _regionIconOrder) Consider(state);

        foreach (var badge in _regionBadges.Values) badge.Wanted = keep.Contains(badge.Region);
    }

    /// <summary>Where a point of the map is on the screen, or null on the far side of the globe or behind the camera.</summary>
    private Vector2? BadgeScreenPoint(float latitude, float longitude)
    {
        if (!IsInstanceValid(_capitalRoot)) return null;
        var world = _capitalRoot.GlobalTransform * PresentationPoint(latitude, longitude, SurfaceMarkerRadius);
        if (!_flatMap && world.Normalized().Dot(_camera.GlobalPosition.Normalized()) < CityHorizon() + 0.02f) return null;
        if (_camera.IsPositionBehind(world)) return null;
        return _camera.UnprojectPosition(world);
    }

    private void DrawRegionIcons()
    {
        if (_regionBadges.Count == 0) return;
        var (iconSize, signSize, fontSize) = BadgeMetrics();
        var font = PresentationFonts.For("map.cities");
        foreach (var badge in _regionBadges.Values)
        {
            if (badge.Alpha <= 0.01f) continue;
            if (BadgeScreenPoint(badge.Latitude, badge.Longitude) is not { } point) continue;
            var a = badge.Alpha;
            point += badge.Offset;
            var box = new Rect2(point.X - badge.Width / 2f, point.Y - iconSize / 2f, badge.Width, iconSize);
            DrawRect(box.Grow(2f), new Color(0.03f, 0.05f, 0.08f, 0.82f * a), true);
            DrawRect(box.Grow(2f), new Color(0.88f, 0.76f, 0.46f, 0.9f * a), false, 1f);
            var x = box.Position.X + 2f;
            var main = badge.Main is { } kind ? RegionIcon(DealOrForceGame.BuildingIconName(kind)) : badge.Ground is not null ? RegionIcon(badge.Ground) : null;
            if (main is not null)
            {
                DrawTextureRect(main, new Rect2(x, box.Position.Y, iconSize, iconSize), false, new Color(1f, 1f, 1f, a));
                x += iconSize + 4f;
                var label = badge.Main is not null ? (badge.Count > 1 ? badge.Count.ToString() : "") : badge.Amount;
                if (label.Length > 0)
                {
                    DrawString(font, new Vector2(x, box.Position.Y + iconSize * 0.78f), label, HorizontalAlignment.Left, -1, fontSize,
                        new Color(0.97f, 0.93f, 0.82f, a));
                    x += label.Length * fontSize * 0.62f;
                }
            }
            foreach (var sign in badge.Signs)
            {
                if (RegionIcon(sign) is { } mark)
                    DrawTextureRect(mark, new Rect2(x, box.Position.Y + (iconSize - signSize) / 2f, signSize, signSize), false,
                        sign == "icon_alert_construction" ? new Color(0.9f, 0.95f, 1f, a) : new Color(1f, 0.55f, 0.45f, a));
                x += signSize + 2f;
            }
        }
    }

    /// <summary>
    /// What a badge under the cursor says, in words: the legend where the player is looking, the
    /// way every icon in a Paradox game answers when hovered.
    /// </summary>
    private string RegionBadgeTooltip(Vector2 at)
    {
        if (_regionBadges.Count == 0 || _campaign is null) return "";
        var (iconSize, _, _) = BadgeMetrics();
        foreach (var badge in _regionBadges.Values)
        {
            if (badge.Alpha < 0.5f || BadgeScreenPoint(badge.Latitude, badge.Longitude) is not { } point) continue;
            point += badge.Offset;
            var box = new Rect2(point.X - badge.Width / 2f, point.Y - iconSize / 2f, badge.Width, iconSize).Grow(2f);
            if (!box.HasPoint(at)) continue;
            var state = _campaign.States.Find(candidate => candidate.MapIndex == badge.Region);
            if (state is null) return "";
            var lines = new List<string> { state.DisplayName };
            if (badge.Main is { } kind)
            {
                lines.Add(Loc.Format("Known for: {0} × {1} (an ordinary region has {2:0.#})",
                    Loc.Get(BuildingCatalog.Get(kind).Name), badge.Count, _buildingAverage.GetValueOrDefault(kind)));
                // A building here is a whole sector of a region, not one plant: say how big.
                var jobs = BuildingCatalog.Get(kind).Jobs;
                if (jobs > 0f) lines.Add(Loc.Format("Each is a whole sector: about {0} jobs; these {1}: {2}",
                    Fmt.People((long)(jobs * 1_000_000)), badge.Count, Fmt.People((long)(jobs * badge.Count * 1_000_000))));
            }
            if (badge.Ground is not null)
            {
                var best = 0f; Commodity? richest = null;
                foreach (var (good, amount) in state.ResourceDeposits) if (amount > best) { best = amount; richest = good; }
                if (richest is { } good2) lines.Add(Loc.Format("Richest deposit: {0} {1:0.0}", Loc.Get(CommodityCatalog.Get(good2).Name), best));
            }
            foreach (var sign in badge.Signs)
                lines.Add(sign switch
                {
                    "icon_alert_construction" => Loc.Format("Building under way: {0} project(s)",
                        _campaign.Construction.FindAll(project => project.StateMapIndex == state.MapIndex).Count),
                    "icon_unrest" => Loc.Format("Unrest: radicalism {0:P0}", state.Radicalism),
                    _ => Loc.Format("Shortages: basic needs covered {0:P0}", state.BasicNeedsCoverage)
                });
            return string.Join("\n", lines);
        }
        return "";
    }

    private void ReserveIconRoom(Sprite3D plate)
    {
        if (!IsInstanceValid(plate) || !plate.IsVisibleInTree() || _camera.IsPositionBehind(plate.GlobalPosition)) return;
        var centre = _camera.UnprojectPosition(plate.GlobalPosition);
        _regionIconTaken.Add(new Rect2(centre - new Vector2(34f, 22f), new Vector2(68f, 44f)));
    }
}
