using System;
using System.Collections.Generic;

namespace DealOrForce;

/// <summary>
/// The flat map is one sheet repeated side by side, so a course that crosses
/// longitude 180 jumps from one edge to the other. The jump is a break in the
/// drawing, not a second world: the cells on either side stay neighbours, and a
/// piece too short to be a triangle strip is left out rather than sent to the
/// renderer as two vertices.
/// </summary>
public static class DateLineSeam
{
    public readonly record struct Run(int Start, int Count);

    /// <summary>
    /// Splits a polyline where consecutive points leap more than half the sheet.
    /// Runs shorter than two points are omitted: one cross-section is two
    /// vertices, and a triangle strip needs three.
    /// </summary>
    public static void Split(ReadOnlySpan<float> x, float mapWidth, List<Run> into)
    {
        into.Clear();
        if (x.Length < 2 || !(mapWidth > 0f)) return;
        var limit = mapWidth * 0.5f;
        var start = 0;
        for (var i = 1; i <= x.Length; i++)
        {
            if (i < x.Length && MathF.Abs(x[i] - x[i - 1]) <= limit) continue;
            var count = i - start;
            if (count >= 2) into.Add(new Run(start, count));
            start = i;
        }
    }

    /// <summary>
    /// An ocean wave's east-west frequency, rounded to a whole number of cycles
    /// around the world. Longitude 0 and longitude 360 are then the same phase.
    /// The map shader uses this same rounding.
    /// </summary>
    public static float WrappedFrequency(float frequency, float width)
    {
        if (!(width >= 1f) || frequency == 0f) return frequency;
        var turn = MathF.PI * 2f;
        var cycles = MathF.Max(1f, MathF.Round(MathF.Abs(frequency) * width / turn));
        return MathF.Sign(frequency) * cycles * turn / width;
    }
}
