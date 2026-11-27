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
    private ArrayMesh _seaLiftMesh;
    private MeshInstance3D _seaLiftPreview;
    private ShaderMaterial _seaLiftMaterial;
    private int _seaLiftFingerprint;
    private readonly List<float> _seaLiftCourse = new();
    private static readonly Color SeaLiftInk = new(0.36f, 0.78f, 0.95f);

    private void CreateSeaLiftPreview(Node3D world)
    {
        _seaLiftMesh = new ArrayMesh();
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

    private bool _seaLiftHasActiveBadges;
    private Transform3D _lastSeaLiftBadgeCam;

    private static Fleet FindFleetById(List<Fleet> fleets, int id)
    {
        for (var i = 0; i < fleets.Count; i++)
            if (fleets[i].Id == id) return fleets[i];
        return null;
    }

    private static Army FindArmyById(List<Army> armies, int id)
    {
        for (var i = 0; i < armies.Count; i++)
            if (armies[i].Id == id) return armies[i];
        return null;
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
            if (FindFleetById(_campaign.Fleets, lift.FleetId) is { } fleet)
                fingerprint = HashCode.Combine(fingerprint,
                    Mathf.RoundToInt(GlobeLineSanity.FiniteOr(fleet.Latitude, 0f) * 4f),
                    Mathf.RoundToInt(GlobeLineSanity.FiniteOr(fleet.Longitude, 0f) * 4f));
            foreach (var id in lift.ArmyIds)
                if (FindArmyById(_campaign.Armies, id) is { } army)
                    fingerprint = HashCode.Combine(fingerprint, army.ProvinceId, army.Route.Count, army.EmbarkedFleetId);
        }
        var camNow = IsInstanceValid(_camera) ? _camera.GlobalTransform : Transform3D.Identity;
        var dirty = fingerprint != _seaLiftFingerprint;
        if (any && (dirty || !_campaign.IsPaused || camNow != _lastSeaLiftBadgeCam))
        {
            _lastSeaLiftBadgeCam = camNow;
            QueueRedraw();
        }
        else if (!any && _seaLiftHasActiveBadges)
        {
            QueueRedraw();
        }
        _seaLiftHasActiveBadges = any;
        if (!dirty) return;
        _seaLiftFingerprint = fingerprint;
        _seaLiftBatch.Clear();
        var drew = false;
        if (any && SeaLiftMaterial() is { } sea)
        {
            foreach (var lift in _campaign.SeaLifts)
                if (lift.NationId == player.Id) drew |= DrawSeaLift(lift);
            drew &= _seaLiftBatch.Commit(_seaLiftMesh, _routeMaterials[0], sea);
        }
        else _seaLiftMesh.ClearSurfaces();
        _seaLiftPreview.Visible = drew;
    }

    private bool DrawSeaLift(SeaLiftOrder lift)
    {
        var map = _campaign?.ProvinceMap;
        if (map is null) return false;
        var fleet = FindFleetById(_campaign.Fleets, lift.FleetId);
        var drew = false;

        // On foot to the quay: each formation still marching, along the road it is taking.
        if (lift.Stage == 0)
            foreach (var id in lift.ArmyIds)
            {
                if (FindArmyById(_campaign.Armies, id) is not { } army ||
                    !GlobeLineSanity.TryValidateProvinceId(army.ProvinceId, map.ProvinceCount, map.ProvinceCenterCell) ||
                    army.ProvinceId == lift.PortProvinceId) continue;
                if (!TryResolveArmyGeoPoint(army, SurfaceLineRadius, out var startPt)) continue;
                _routePoints.Clear();
                _routePoints.Add(startPt);
                var road = army.PlannedRoute.Count > 0 ? army.PlannedRoute : army.Route;
                foreach (var province in road)
                    if (TryPlanPoint(province, out var pt)) AddRoutePoint(pt);
                if ((road.Count == 0 || road[^1] != lift.PortProvinceId) &&
                    TryPlanPoint(lift.PortProvinceId, out var portPt))
                    AddRoutePoint(portPt);
                if (_routePoints.Count >= 2) drew |= DrawRibbon(_seaLiftBatch, 0, 0f);
            }

        // The passage: from the quay while they gather, from the fleet itself once it sails.
        float fromLatitude, fromLongitude;
        if (lift.Stage >= 1 && fleet is not null && GlobeLineSanity.IsValidGeo(fleet.Latitude, fleet.Longitude))
        {
            fromLatitude = fleet.Latitude;
            fromLongitude = fleet.Longitude;
        }
        else if (!SeaLanes.TryHarbourPoint(map, lift.PortProvinceId, out fromLatitude, out fromLongitude) ||
                 !GlobeLineSanity.IsValidGeo(fromLatitude, fromLongitude))
        {
            return drew;
        }

        if (SeaLanes.TryHarbourPoint(map, lift.LandingProvinceId, out var toLatitude, out var toLongitude) &&
            GlobeLineSanity.IsValidGeo(toLatitude, toLongitude) &&
            SeaLanes.Plan(_campaign, lift.NationId, fromLatitude, fromLongitude, toLatitude, toLongitude, _seaLiftCourse, out _) &&
            TryPresentationPoint(fromLatitude, fromLongitude, SurfaceLineRadius, out var passageStart))
        {
            _routePoints.Clear();
            _routePoints.Add(passageStart);
            for (var i = 0; i + 1 < _seaLiftCourse.Count; i += 2)
            {
                if (TryPresentationPoint(_seaLiftCourse[i], _seaLiftCourse[i + 1], SurfaceLineRadius, out var coursePt))
                    AddRoutePoint(coursePt);
            }
            if (TryPlanPoint(lift.LandingProvinceId, out var beachPt)) AddRoutePoint(beachPt);
            if (_routePoints.Count >= 2) drew |= DrawRibbon(_seaLiftBatch, 1, 0f);
        }

        // And on from the beach to where they were sent.
        if (lift.TargetProvinceId != lift.LandingProvinceId &&
            TryPlanPoint(lift.LandingProvinceId, out var landingPt) &&
            TryPlanPoint(lift.TargetProvinceId, out var targetPt))
        {
            _routePoints.Clear();
            _routePoints.Add(landingPt);
            AddRoutePoint(targetPt);
            if (_routePoints.Count >= 2) drew |= DrawRibbon(_seaLiftBatch, 0, 0f);
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
                    if (FindArmyById(_campaign.Armies, id) is not { } army || army.ProvinceId <= 0) continue;
                    var text = army.ProvinceId == lift.PortProvinceId
                        ? Loc.Format("⚓ waiting for ships at {0}", port)
                        : Loc.Format("⚓ to the ships at {0}", port);
                    DrawSeaLiftBadge(army.Latitude, army.Longitude, text, font, size);
                }
            }
            else if (FindFleetById(_campaign.Fleets, lift.FleetId) is { } fleet)
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
