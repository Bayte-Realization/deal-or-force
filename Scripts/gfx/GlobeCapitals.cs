using System;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    /// <summary>
    /// A capital is a mark on the map, not a sign facing the viewer. Geographic east/north
    /// are its permanent axes; only a projection change replaces that frame with flat axes.
    /// </summary>
    private void PlaceCapitalMarker(Node3D node, Vector3 normal)
    {
        var geographic = GeographicFromNormal(normal);
        node.Position = _flatMap
            ? FlatPoint(geographic.Latitude, geographic.Longitude, FlatMarkerZ)
            : normal * SurfaceMarkerRadius;
        OrientSurfaceCounter(node, normal);
        if (node is Sprite3D star) SizeCapitalMarker(star);
    }

    private void SizeCapitalMarker(Sprite3D star)
    {
        if (!IsInstanceValid(_camera) || star.Texture is null) return;
        star.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
        star.FixedSize = false;
        // One unit across the texture makes Scale the physical width of the whole mark.
        // Unlike FixedSize's shader compensation, this keeps the actual quad tangent.
        star.PixelSize = 1f / Math.Max(1, star.Texture.GetWidth());
        float width;
        if (_flatMap)
        {
            var unitsPerPixel = _camera.Size / Math.Max(1f, _camera.GetViewport().GetVisibleRect().Size.Y);
            width = unitsPerPixel * Math.Clamp(Ui.Metric("map.flat_capital_pixels", 14f), 8f, 24f);
        }
        else
        {
            var depth = Math.Max(.001f, -_camera.ToLocal(star.GlobalPosition).Z);
            var unitsPerPixel = _camera.ProjectPosition(Vector2.Zero, depth)
                .DistanceTo(_camera.ProjectPosition(Vector2.Down, depth));
            var pixels = Math.Clamp(Ui.Metric("map.globe_capital_pixels", 18f), 8f, 30f);
            var ground = Math.Clamp(Ui.Metric("map.globe_capital_world_width", .026f), .008f, .08f);
            width = Math.Min(ground, pixels * unitsPerPixel);
        }
        star.Scale = Vector3.One * width;
    }

    private void UpdateCapitalMarkerScale()
    {
        foreach (var (node, _) in _capitalMarkers)
            if (IsInstanceValid(node) && node is Sprite3D star) SizeCapitalMarker(star);
    }
}
