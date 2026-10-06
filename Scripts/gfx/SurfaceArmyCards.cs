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
        marker.Node.Scale = Vector3.One * SurfaceCounterWidth(marker.Node);
    }

    // One world footprint and one screen ceiling for all military cards. Individual
    // arms may override these limits in gui without adding another billboard shader.
    private float SurfaceCounterWidth(Node3D node, string prefix = "map")
    {
        if (!IsInstanceValid(_camera)) return .06f;
        var depth = Math.Max(.001f, -_camera.ToLocal(node.GlobalPosition).Z);
        var unitsPerPixel = _camera.ProjectPosition(Vector2.Zero, depth)
            .DistanceTo(_camera.ProjectPosition(Vector2.Down, depth));
        var pixelLimit = Math.Clamp(Ui.Metric(prefix + ".globe_counter_pixels", Ui.Metric("map.globe_counter_pixels", 56f)), 28f, 80f);
        var worldLimit = Math.Clamp(Ui.Metric(prefix + ".globe_counter_world_width", Ui.Metric("map.globe_counter_world_width", .06f)), .012f, .3f);
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
