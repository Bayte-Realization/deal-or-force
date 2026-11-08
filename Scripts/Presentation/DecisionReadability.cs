using System;

namespace DealOrForce;

/// <summary>Pure screen-pixel policy; no simulation state or engine objects.
/// The canvas is not the window: 12 canvas units become 8 pixels at 720p.
/// Keep authored sizes on large windows, but put a floor under reading and hit targets.
/// Deliberately do not snap down to the nearest typography step after applying that floor.</summary>
public static class DecisionReadability
{
    public static int FontSize(int authored, float pixelsPerUnit, float interfaceScale, float minimumPixels = 14) =>
        Math.Max(authored, (int)MathF.Ceiling(minimumPixels * MathF.Max(1, interfaceScale) / SafeScale(pixelsPerUnit)));

    public static float TargetHeight(float authored, float pixelsPerUnit, float interfaceScale, float minimumPixels = 32) =>
        MathF.Max(authored, minimumPixels * MathF.Max(1, interfaceScale) / SafeScale(pixelsPerUnit));

    public static (float X, float Y, float Width, float Height) Fit(
        float canvasWidth, float canvasHeight, float pixelsPerUnit,
        float x, float y, float width, float height,
        float minimumWidth = 1100, float minimumHeight = 760, float margin = 20)
    {
        var scale = SafeScale(pixelsPerUnit);
        var gutter = MathF.Min(margin / scale, MathF.Min(canvasWidth, canvasHeight) / 8);
        var centerX = x + width / 2; var centerY = y + height / 2;
        width = Math.Clamp(MathF.Max(width, minimumWidth / scale), 1, MathF.Max(1, canvasWidth - 2 * gutter));
        height = Math.Clamp(MathF.Max(height, minimumHeight / scale), 1, MathF.Max(1, canvasHeight - 2 * gutter));
        x = Math.Clamp(centerX - width / 2, gutter, MathF.Max(gutter, canvasWidth - gutter - width));
        y = Math.Clamp(centerY - height / 2, gutter, MathF.Max(gutter, canvasHeight - gutter - height));
        return (x, y, width, height);
    }

    private static float SafeScale(float scale) => float.IsFinite(scale) && scale > .01f ? scale : 1;
}
