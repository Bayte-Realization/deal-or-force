using System;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private void OrientSurfaceArmyCard(ArmyStackMarker marker)
    {
        // +X east, +Y north, +Z out of the map. Every child inherits this
        // frame, including the selection glow and the transparent status canvas.
        OrientSurfaceCounter(marker.Node, _flatMap ? marker.Normal : marker.Node.Position.Normalized());
    }

    private void OrientSurfaceCounter(Node3D node, Vector3 normal)
    {
        if (_flatMap) { node.Quaternion = Quaternion.Identity; return; }
        var geo = GeographicFromNormal(normal);
        node.Quaternion = CountryLabelSurfaceBasis(geo.Latitude, geo.Longitude, 1f, 0f).GetRotationQuaternion();
    }

    private void SizeSurfaceArmyCounter(ArmyStackMarker marker)
    {
        var width = SurfaceCounterWidth(marker.Node);
        if (MathF.Abs(marker.Node.Scale.X - width) > 0.00015f)
        {
            if (marker.Node.Scale.X <= 0.0001f || MathF.Abs(marker.Node.Scale.X - width) > 0.25f)
                marker.Node.Scale = Vector3.One * width;
            else
                marker.Node.Scale = marker.Node.Scale.Lerp(Vector3.One * width, 0.30f);
        }
    }

    // One world footprint and one screen ceiling for all military cards. Individual
    // arms may override these limits in gui without adding another billboard shader.
    private float SurfaceCounterWidth(Node3D node, string prefix = "map")
    {
        if (!IsInstanceValid(_camera)) return .06f;
        var camTransform = _camera.GlobalTransform;
        var pos = node is not null && IsInstanceValid(node) && node.IsInsideTree()
            ? node.GlobalPosition
            : node is not null ? node.Position : camTransform.Origin - camTransform.Basis.Z * 5f;
        var rel = pos - camTransform.Origin;
        var depth = Math.Max(.001f, -rel.Dot(camTransform.Basis.Z));
        var viewportH = Math.Max(1f, Size.Y);
        var unitsPerPixel = 2f * depth * MathF.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) / viewportH;
        var defaultPixels = Ui.Metric("map.globe_counter_pixels", 56f);
        var defaultWorld = Ui.Metric("map.globe_counter_world_width", .06f);
        var pixelKey = prefix switch
        {
            "fleet" => "fleet.globe_counter_pixels",
            "support" => "support.globe_counter_pixels",
            "battle" => "combat.map_marker_pixels",
            _ => "map.globe_counter_pixels"
        };
        var worldKey = prefix switch
        {
            "fleet" => "fleet.globe_counter_world_width",
            "support" => "support.globe_counter_world_width",
            _ => "map.globe_counter_world_width"
        };
        var pixelLimit = Math.Clamp(Ui.Metric(pixelKey, defaultPixels), 28f, 80f);
        var worldLimit = Math.Clamp(Ui.Metric(worldKey, defaultWorld), .012f, .3f);
        // Close cards remain readable but never balloon with altitude to cover a
        // country. Foreshortening is intentional: the card lies on the ground.
        return Math.Min(worldLimit, pixelLimit * unitsPerPixel);
    }

    private bool TryHitSurfaceArmyCard(ArmyStackMarker marker, Vector2 pixel, out float distance)
        => TryHitSurfaceCounter(marker.Plate, pixel, out distance);

    private bool TryHitSurfaceCounter(Sprite3D plate, Vector2 pixel, out float distance)
    {
        distance = float.MaxValue;
        if (!plate.IsVisibleInTree() || plate.Texture is null) return false;
        // Intersect the actual tilted quad in its own texture plane. A circular
        // billboard hit target would steal clicks on the ground beside the card.
        var inverse = plate.GlobalTransform.AffineInverse();
        var origin = _camera.ProjectRayOrigin(pixel);
        var localOrigin = inverse * origin;
        var localDirection = inverse.Basis * _camera.ProjectRayNormal(pixel);
        if (Math.Abs(localDirection.Z) < .00001f) return false;
        var travel = -localOrigin.Z / localDirection.Z;
        if (travel < 0f) return false;
        var hit = localOrigin + localDirection * travel;
        var half = plate.Texture.GetSize() * plate.PixelSize * .5f;
        if (Math.Abs(hit.X) > half.X || Math.Abs(hit.Y) > half.Y) return false;
        distance = origin.DistanceSquaredTo(plate.ToGlobal(hit));
        return true;
    }
}
