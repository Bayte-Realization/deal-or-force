using System;
using Godot;

namespace DealOrForce;

/// <summary>
/// How big a fleet's counter is drawn. Kept beside the army counter's own constants in
/// spirit: the globe uses a bounded surface footprint; the flat map uses a continuous
/// detail scale shared with armies. Flat-map selection changes the frame, not its size.
/// </summary>
public sealed partial class GlobePresentation
{
    private void SizeFleetCounter((Node3D Node, Label3D Count, Sprite3D Plate) parts)
    {
        var (node, count, plate) = parts;
        if (!IsInstanceValid(node) || !IsInstanceValid(count) || !IsInstanceValid(plate)) return;
        node.Scale = Vector3.One * (_flatMap ? _counterWorldPixel * _counterFlatFar : SurfaceCounterWidth(node, "fleet"));
        OrientSurfaceCounter(node, node.Position.Normalized());
        plate.Texture = _flatMap && _flatStrategicCounters
            ? CompactCounterTexture(_fleetTexture, new Rect2I(34, 61, 96, 26)) : _fleetTexture;
        plate.FixedSize = count.FixedSize = false;
        plate.Billboard = count.Billboard = BaseMaterial3D.BillboardModeEnum.Disabled;
        plate.PixelSize = _flatMap
            ? Math.Clamp(Ui.Metric("map.flat_fleet_counter_width", 64f), 40f, 96f) / plate.Texture.GetWidth()
            : 1f / plate.Texture.GetWidth();
        count.PixelSize = plate.PixelSize * 1.45f;
        count.Position = Vector3.Zero;
        count.RenderPriority = 20;
        count.Width = 0f;
        count.Visible = true;
        if (plate.GetChildOrNull<Sprite3D>(0) is { } glow)
        {
            glow.FixedSize = plate.FixedSize;
            glow.PixelSize = plate.PixelSize;
            glow.Billboard = plate.Billboard;
            glow.Scale = _flatMap && _flatStrategicCounters
                ? new Vector3(plate.Texture.GetWidth() / 160f, plate.Texture.GetHeight() / 96f, 1f)
                : Vector3.One;
        }
    }

    /// <summary>The fleet plate at the far zoom; multiplied by the same counter scale armies use.</summary>
    private const float FleetPlatePixelSize = 0.00011f;

    /// <summary>
    /// The hull count on it, likewise - sized to read as a number on a card rather than a
    /// headline across it, in the clear middle the ship's silhouette leaves free.
    /// </summary>
    private const float FleetTextPixelSize = 0.00012f;

}
