using System;
using System.Collections.Generic;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// The player's sea lifts on the map (SeaLift): where the formations are walking to board, the
/// passage the fleet takes, and the march on from the beach - and an anchor beside every
/// formation that is part of one, saying what it is waiting for. Drawn whether or not anything is
/// selected: a lift runs for days on its own, and the map is where a player looks for it.
/// </summary>
public sealed partial class GlobePresentation
{
    private ImmediateMesh _seaLiftMesh;
    private MeshInstance3D _seaLiftPreview;
    private ShaderMaterial _seaLiftMaterial;
    private int _seaLiftFingerprint;
    private readonly List<float> _seaLiftCourse = new();
    private static readonly Color SeaLiftInk = new(0.36f, 0.78f, 0.95f);

    private void CreateSeaLiftPreview(Node3D world)
    {
        _seaLiftMesh = new ImmediateMesh();
        _seaLiftPreview = new MeshInstance3D
        {
            Name = "SeaLiftRoutes",
            Mesh = _seaLiftMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        world.AddChild(_seaLiftPreview);
    }

    private ShaderMaterial SeaLiftMaterial()
    {
        if (_seaLiftMaterial is not null) return _seaLiftMaterial;
        if (_routeMaterials[0] is not ShaderMaterial move) return null;
        _seaLiftMaterial = (ShaderMaterial)move.Duplicate();
        _seaLiftMaterial.SetShaderParameter("order_color", SeaLiftInk);
        _seaLiftMaterial.SetShaderParameter("support_order", false);
        return _seaLiftMaterial;
    }

    /// <summary>Rebuilt only when a lift, its fleet or one of its formations has moved on.</summary>
    private void UpdateSeaLiftPreview()
    {
        if (_campaign?.ProvinceMap is null || !IsInstanceValid(_seaLiftPreview)) return;
        var player = _campaign.Player;
        var fingerprint = HashCode.Combine(player?.Id ?? -1, _flatMap);
        var any = false;
        foreach (var lift in _campaign.SeaLifts)
        {
            if (player is null || lift.NationId != player.Id) continue;
            any = true;
            fingerprint = HashCode.Combine(fingerprint, lift.Id, lift.Stage, lift.ArmyIds.Count);
            if (_campaign.Fleets.Find(f => f.Id == lift.FleetId) is { } fleet)
                fingerprint = HashCode.Combine(fingerprint, Mathf.RoundToInt(fleet.Latitude * 4f), Mathf.RoundToInt(fleet.Longitude * 4f));
            foreach (var id in lift.ArmyIds)
                if (_campaign.Armies.Find(a => a.Id == id) is { } army)
                    fingerprint = HashCode.Combine(fingerprint, army.ProvinceId, army.Route.Count, army.EmbarkedFleetId);
        }
        // The anchors are drawn on the overlay, which redraws only when asked.
        if (any) QueueRedraw();
        if (fingerprint == _seaLiftFingerprint) return;
        _seaLiftFingerprint = fingerprint;
        _seaLiftMesh.ClearSurfaces();
        var drew = false;
        if (any && SeaLiftMaterial() is { } sea)
            foreach (var lift in _campaign.SeaLifts)
                if (lift.NationId == player.Id) drew |= DrawSeaLift(lift, sea);
        _seaLiftPreview.Visible = drew;
    }

    private bool DrawSeaLift(SeaLiftOrder lift, ShaderMaterial sea)
    {
        var map = _campaign.ProvinceMap;
        var fleet = _campaign.Fleets.Find(f => f.Id == lift.FleetId);
        var drew = false;
        var move = _routeMaterials[0];

        // On foot to the quay: each formation still marching, along the road it is taking.
        if (lift.Stage == 0)
            foreach (var id in lift.ArmyIds)
            {
                if (_campaign.Armies.Find(a => a.Id == id) is not { } army || army.ProvinceId <= 0 ||
                    army.ProvinceId == lift.PortProvinceId) continue;
                _routePoints.Clear();
                _routePoints.Add(PresentationPoint(army.Latitude, army.Longitude, SurfaceLineRadius));
                var road = army.PlannedRoute.Count > 0 ? army.PlannedRoute : army.Route;
                foreach (var province in road)
                {
                    var centre = map.CenterOf(province);
                    AddRoutePoint(PresentationPoint(centre.Latitude, centre.Longitude, SurfaceLineRadius));
                }
                if (road.Count == 0 || road[^1] != lift.PortProvinceId)
                {
                    var port = map.CenterOf(lift.PortProvinceId);
                    AddRoutePoint(PresentationPoint(port.Latitude, port.Longitude, SurfaceLineRadius));
                }
                if (_routePoints.Count >= 2) drew |= DrawRibbon(_seaLiftMesh, move, 0f);
            }

        // The passage: from the quay while they gather, from the fleet itself once it sails.
        float fromLatitude, fromLongitude;
        if (lift.Stage >= 1 && fleet is not null) { fromLatitude = fleet.Latitude; fromLongitude = fleet.Longitude; }
        else if (!SeaLanes.TryHarbourPoint(map, lift.PortProvinceId, out fromLatitude, out fromLongitude)) return drew;
        if (SeaLanes.TryHarbourPoint(map, lift.LandingProvinceId, out var toLatitude, out var toLongitude) &&
            SeaLanes.Plan(_campaign, lift.NationId, fromLatitude, fromLongitude, toLatitude, toLongitude, _seaLiftCourse, out _))
        {
            _routePoints.Clear();
            _routePoints.Add(PresentationPoint(fromLatitude, fromLongitude, SurfaceLineRadius));
            for (var i = 0; i + 1 < _seaLiftCourse.Count; i += 2)
                AddRoutePoint(PresentationPoint(_seaLiftCourse[i], _seaLiftCourse[i + 1], SurfaceLineRadius));
            var beach = map.CenterOf(lift.LandingProvinceId);
            AddRoutePoint(PresentationPoint(beach.Latitude, beach.Longitude, SurfaceLineRadius));
            if (_routePoints.Count >= 2) drew |= DrawRibbon(_seaLiftMesh, sea, 0f);
        }

        // And on from the beach to where they were sent.
        if (lift.TargetProvinceId > 0 && lift.TargetProvinceId != lift.LandingProvinceId)
        {
            var beach = map.CenterOf(lift.LandingProvinceId);
            var target = map.CenterOf(lift.TargetProvinceId);
            _routePoints.Clear();
            _routePoints.Add(PresentationPoint(beach.Latitude, beach.Longitude, SurfaceLineRadius));
            AddRoutePoint(PresentationPoint(target.Latitude, target.Longitude, SurfaceLineRadius));
            if (_routePoints.Count >= 2) drew |= DrawRibbon(_seaLiftMesh, move, 0f);
        }
        return drew;
    }

    /// <summary>The anchor beside every formation and fleet of a lift, with what it is doing.</summary>
    private void DrawSeaLiftBadges()
    {
        var player = _campaign?.Player;
        var map = _campaign?.ProvinceMap;
        if (player is null || map is null) return;
        var font = PresentationFonts.For("map.cities");
        var size = Mathf.Clamp((int)Ui.Metric("map.sea_lift_badge_font", 12), 9, 18);
        foreach (var lift in _campaign.SeaLifts)
        {
            if (lift.NationId != player.Id) continue;
            var port = RegionCaption(lift.PortProvinceId);
            var landing = RegionCaption(lift.LandingProvinceId);
            if (lift.Stage == 0)
            {
                foreach (var id in lift.ArmyIds)
                {
                    if (_campaign.Armies.Find(a => a.Id == id) is not { } army || army.ProvinceId <= 0) continue;
                    var text = army.ProvinceId == lift.PortProvinceId
                        ? Loc.Format("⚓ waiting for ships at {0}", port)
                        : Loc.Format("⚓ to the ships at {0}", port);
                    DrawSeaLiftBadge(army.Latitude, army.Longitude, text, font, size);
                }
            }
            else if (_campaign.Fleets.Find(f => f.Id == lift.FleetId) is { } fleet)
                DrawSeaLiftBadge(fleet.Latitude, fleet.Longitude,
                    Loc.Format("⚓ {0:N0} aboard, landing at {1}", fleet.Carried.Count, landing), font, size);
        }
        // Any other fleet of the player's with formations aboard.
        foreach (var fleet in _campaign.Fleets)
        {
            if (fleet.NationId != player.Id || fleet.Carried.Count == 0 || SeaLift.Holds(_campaign, fleet)) continue;
            DrawSeaLiftBadge(fleet.Latitude, fleet.Longitude, Loc.Format("⚓ {0:N0} aboard", fleet.Carried.Count), font, size);
        }
    }

    private void DrawSeaLiftBadge(float latitude, float longitude, string text, Font font, int size)
    {
        if (font is null || BadgeScreenPoint(latitude, longitude) is not { } point) return;
        var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        // Under the counter, so it never covers the plate it belongs to.
        var at = point + new Vector2(-width / 2f, size * 2.2f);
        var box = new Rect2(at.X - 5f, at.Y - size, width + 10f, size + 7f);
        DrawRect(box, new Color(0.03f, 0.06f, 0.09f, 0.84f), true);
        DrawRect(box, new Color(SeaLiftInk, 0.85f), false, 1f);
        DrawString(font, at + new Vector2(0f, 1f), text, HorizontalAlignment.Left, -1, size, new Color(0.86f, 0.95f, 1f));
    }

    private string RegionCaption(int province)
    {
        var map = _campaign?.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return "?";
        var index = map.ProvinceToState[province];
        foreach (var state in _campaign.States)
            if (state.MapIndex == index) return state.DisplayName;
        return "?";
    }
}
