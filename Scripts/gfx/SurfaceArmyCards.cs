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
        node.Quaternion = SurfaceCounterQuaternion(normal);
    }

    private Quaternion SurfaceCounterQuaternion(Vector3 normal)
    {
        if (_flatMap) return Quaternion.Identity;
        var geo = GeographicFromNormal(normal);
        return CountryLabelSurfaceBasis(geo.Latitude, geo.Longitude, 1f, 0f).GetRotationQuaternion();
    }

    private void SizeSurfaceArmyCounter(ArmyStackMarker marker)
    {
        var width = SurfaceCounterWidth(marker.Node);
        // Camera and position already move smoothly. Derive the scale directly so an
        // extra reconcile or density pass cannot change it at the same position.
        if (MathF.Abs(marker.Node.Scale.X - width) > 0.000001f)
            marker.Node.Scale = Vector3.One * width;
    }

    private ulong _surfaceCamFrame = ulong.MaxValue;
    private float _surfaceCamFov = -1f;
    private float _surfaceCamViewportH = -1f;
    private Vector3 _surfaceCamOrigin;
    private Vector3 _surfaceCamForward;
    private float _surfaceUnitsPerPixelScale;
    private ulong _surfaceMetricCacheMsec;
    private bool _surfaceMetricsCached;
    private (float Pixel, float World) _surfaceMapLimits;
    private (float Pixel, float World) _surfaceFleetLimits;
    private (float Pixel, float World) _surfaceSupportLimits;
    private (float Pixel, float World) _surfaceBattleLimits;

    private (float Pixel, float World) SurfaceLimitsForPrefix(string prefix)
    {
        var nowMs = Time.GetTicksMsec();
        if (!_surfaceMetricsCached || nowMs - _surfaceMetricCacheMsec >= 250UL)
        {
            _surfaceMetricsCached = true;
            _surfaceMetricCacheMsec = nowMs;
            var defaultPixels = Ui.Metric("map.globe_counter_pixels", 56f);
            var defaultWorld = Ui.Metric("map.globe_counter_world_width", .06f);
            _surfaceMapLimits = (Math.Clamp(defaultPixels, 28f, 80f), Math.Clamp(defaultWorld, .012f, .3f));
            _surfaceFleetLimits = (Math.Clamp(Ui.Metric("fleet.globe_counter_pixels", defaultPixels), 28f, 80f),
                Math.Clamp(Ui.Metric("fleet.globe_counter_world_width", defaultWorld), .012f, .3f));
            _surfaceSupportLimits = (Math.Clamp(Ui.Metric("support.globe_counter_pixels", defaultPixels), 28f, 80f),
                Math.Clamp(Ui.Metric("support.globe_counter_world_width", defaultWorld), .012f, .3f));
            _surfaceBattleLimits = (Math.Clamp(Ui.Metric("combat.map_marker_pixels", defaultPixels), 28f, 80f),
                _surfaceMapLimits.World);
        }
        return prefix switch
        {
            "fleet" => _surfaceFleetLimits,
            "support" => _surfaceSupportLimits,
            "battle" => _surfaceBattleLimits,
            _ => _surfaceMapLimits
        };
    }

    // One world footprint and one screen ceiling for all military cards. Individual
    // arms may override these limits in gui without adding another billboard shader.
    private float SurfaceCounterWidth(Node3D node, string prefix = "map")
        => SurfaceCounterWidthAt(node.IsInsideTree() ? node.GlobalPosition : node.Position, prefix);

    private float SurfaceCounterWidthAt(Vector3 globalPos, string prefix = "map")
    {
        if (!IsInstanceValid(_camera)) return .06f;
        var frame = Engine.GetProcessFrames();
        var viewportH = Math.Max(1f, Size.Y);
        var fov = _camera.Fov;
        if (frame != _surfaceCamFrame || MathF.Abs(fov - _surfaceCamFov) > 0.001f || MathF.Abs(viewportH - _surfaceCamViewportH) > 0.5f)
        {
            var camTransform = _camera.GlobalTransform;
            _surfaceCamFrame = frame;
            _surfaceCamFov = fov;
            _surfaceCamViewportH = viewportH;
            _surfaceCamOrigin = camTransform.Origin;
            _surfaceCamForward = camTransform.Basis.Z;
            _surfaceUnitsPerPixelScale = 2f * MathF.Tan(Mathf.DegToRad(fov * 0.5f)) / viewportH;
        }
        var rel = globalPos - _surfaceCamOrigin;
        var depth = Math.Max(.001f, -rel.Dot(_surfaceCamForward));
        var unitsPerPixel = depth * _surfaceUnitsPerPixelScale;
        var (pixelLimit, worldLimit) = SurfaceLimitsForPrefix(prefix);
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
        var half = plate.Texture.GetSize() * plate.PixelSize * .5f;
        return TryHitSurfaceQuad(plate.GlobalTransform, half, pixel, out distance);
    }

    private bool TryHitSurfaceQuad(Transform3D globalTransform, Vector2 half, Vector2 pixel, out float distance)
    {
        distance = float.MaxValue;
        if (!IsInstanceValid(_camera)) return false;
        // Intersect the actual tilted quad in its own local plane. A circular
        // billboard hit target would steal clicks on the ground beside the card.
        var inverse = globalTransform.AffineInverse();
        var origin = _camera.ProjectRayOrigin(pixel);
        var localOrigin = inverse * origin;
        var localDirection = inverse.Basis * _camera.ProjectRayNormal(pixel);
        if (Math.Abs(localDirection.Z) < .00001f) return false;
        var travel = -localOrigin.Z / localDirection.Z;
        if (travel < 0f) return false;
        var hit = localOrigin + localDirection * travel;
        if (Math.Abs(hit.X) > half.X || Math.Abs(hit.Y) > half.Y) return false;
        distance = origin.DistanceSquaredTo(globalTransform * hit);
        return true;
    }
}
