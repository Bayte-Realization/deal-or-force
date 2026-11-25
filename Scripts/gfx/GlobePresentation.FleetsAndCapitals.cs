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
    private readonly List<(Node3D Node, Vector3 Normal)> _capitalMarkers = new();
    private RBush.RBush<SpatialGeoItem<(Node3D Node, Vector3 Normal)>> _capitalRTree;
    private int _capitalRTreeVersion;
    private readonly List<SpatialGeoItem<(Node3D Node, Vector3 Normal)>> _capitalBuildScratch = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _capitalQueryScratch = new();
    private readonly List<(Node3D Node, Vector3 Normal)> _capitalFrustumVisible = new();
    private readonly HashSet<Node3D> _capitalPrevVisible = new();
    private (bool Flat, int LatBucket, int LonBucket, int SpanBucket, int TreeVersion) _capitalBucketKey;
    private readonly List<(Node3D Node, Vector3 Normal)> _fleetMarkers = new();
    private readonly Dictionary<Node3D, Simulation.Fleet> _fleetByMarker = new();
    private readonly Dictionary<int, (Node3D Node, Label3D Count, Sprite3D Plate)> _fleetNodes = new();
    private int _fleetRosterFingerprint = int.MinValue;
    private int _selectedFleetId = -1;
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
            marker.Position = LocalSurfacePoint(fleet.Latitude, fleet.Longitude, SurfaceMarkerRadius);
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
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
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
                Position = Vector3.Zero, NoDepthTest = true,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic, RenderPriority = 20
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
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

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
        _capitalBuildScratch.Clear();
        _capitalPrevVisible.Clear();
        _capitalFrustumVisible.Clear();

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
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Modulate = Ui.Colour("globe.selection")
            };

            marker.Position = LocalSurfacePoint(latitude, longitude, SurfaceMarkerRadius);
            OrientSurfaceCounter(marker, marker.Position.Normalized());
            _capitalRoot.AddChild(marker);
            var normal = marker.Position.Normalized();
            var entry = (marker, normal);
            _capitalMarkers.Add(entry);
            _capitalPrevVisible.Add(marker);
            _capitalBuildScratch.Add(new SpatialGeoItem<(Node3D Node, Vector3 Normal)>(entry, latitude, longitude));
            PlaceCapitalMarker(marker, normal);
        }
        _capitalRTree = new RBush.RBush<SpatialGeoItem<(Node3D Node, Vector3 Normal)>>();
        if (_capitalBuildScratch.Count > 0)
            _capitalRTree.BulkLoad(_capitalBuildScratch);
        _capitalBuildScratch.Clear();
        _capitalRTreeVersion++;
        _capitalBucketKey = default;
        // Which cities carry the capital's mark and colour is decided when the cities are placed,
        // and with the camera at rest they are not placed again: the star moved to Yekaterinburg
        // and Moscow kept a golden name with no dot under it.
        _cityDirty = true;
    }

    /// <summary>The campaign's capital revision the markers were last built for.</summary>
    private int _capitalRevision = -1;

    private void UpdateMarkerVisibility(
        Node3D root,
        List<(Node3D Node, Vector3 Normal)> markers,
        bool withinLod,
        float horizon = 0.18f)
    {
        if (!IsInstanceValid(root) || !IsInstanceValid(_camera)) return;
        root.Visible = withinLod;
        if (!withinLod) return;

        if (ReferenceEquals(markers, _capitalMarkers) && _capitalRTree is { Count: > 0 })
        {
            var vpSize = ViewportSize();
            ComputeGeoFrustumBounds(vpSize, root.GlobalTransform.Basis, 5f,
                out var minLat, out var maxLat, out var minLon, out var maxLon, out var fullLon,
                out var latB, out var lonB, out var spanB);
            var capKey = (_flatMap, latB, lonB, spanB, _capitalRTreeVersion);
            if (capKey != _capitalBucketKey)
            {
                _capitalBucketKey = capKey;
                SearchGeoRTree(_capitalRTree, _capitalQueryScratch, minLat, maxLat, minLon, maxLon, fullLon);
                _capitalFrustumVisible.Clear();
                _capitalFrustumVisible.AddRange(_capitalQueryScratch);
            }
            for (var i = 0; i < _capitalMarkers.Count && _capitalPrevVisible.Count > 0; i++)
            {
                var node = _capitalMarkers[i].Node;
                if (IsInstanceValid(node) && node.Visible && _capitalPrevVisible.Contains(node))
                    node.Visible = false;
            }
            _capitalPrevVisible.Clear();
            var capHalfH = _camera.Size * .5f;
            var capVpSize = _camera.GetViewport().GetVisibleRect().Size;
            var capHalfW = capHalfH * capVpSize.X / MathF.Max(1f, capVpSize.Y);
            var capViewNormal = _camera.GlobalPosition.Normalized();
            for (var i = 0; i < _capitalFrustumVisible.Count; i++)
            {
                var marker = _capitalFrustumVisible[i];
                if (!IsInstanceValid(marker.Node)) continue;
                bool vis;
                if (_flatMap)
                {
                    var off = marker.Node.Position - _camera.Position;
                    vis = MathF.Abs(off.X) < capHalfW + .1f && MathF.Abs(off.Y) < capHalfH + .1f;
                }
                else
                {
                    vis = marker.Normal.Dot(capViewNormal) > horizon && !_coveredCounters.Contains(marker.Node);
                }
                if (marker.Node.Visible != vis) marker.Node.Visible = vis;
                if (vis) _capitalPrevVisible.Add(marker.Node);
            }
            return;
        }

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
}
