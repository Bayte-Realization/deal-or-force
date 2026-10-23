using System;

namespace DealOrForce.Simulation;

/// <summary>
/// The axis a chart is drawn on: the range rounded out to a step a person would choose, and the
/// number of decimals that step needs.
///
/// This lives in the simulation rather than in the chart control for two reasons. It is a
/// decision about the numbers, not about drawing them - a bar chart and a line chart want the
/// same axis - and the headless test stand compiles this folder and not the interface, so an
/// axis rule that lives beside the drawing cannot be checked without opening a window.
/// </summary>
public static class ChartScale
{
    /// <summary>
    /// The range, rounded out to a step of 1, 2, 2.5 or 5 times a power of ten. Five grid lines
    /// then carry five round numbers of the same shape, rather than five independent roundings
    /// of whatever the data happened to be - which is what made the labels look uneven.
    /// </summary>
    public static (float Minimum, float Maximum, int Decimals) Axis(float minimum, float maximum)
    {
        var span = maximum - minimum;
        // A flat series - every reading the same, which is what the first week of a campaign
        // looks like - must still get an axis with room on both sides of its line.
        if (!float.IsFinite(span) || span <= 0f)
        {
            var room = MathF.Max(1f, MathF.Abs(minimum) * 0.05f);
            return (minimum - room, maximum + room, room >= 1f ? 0 : 1);
        }
        var rough = span / 4f;
        var magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(rough)));
        var normalized = rough / magnitude;
        var step = (normalized <= 1f ? 1f : normalized <= 2f ? 2f : normalized <= 2.5f ? 2.5f : normalized <= 5f ? 5f : 10f) * magnitude;
        var low = MathF.Floor(minimum / step) * step;
        var high = MathF.Ceiling(maximum / step) * step;
        // Rounding must never collapse the axis onto a single line.
        if (high - low < step * 4f) high = low + step * 4f;
        return (low, high, step >= 1f ? 0 : Math.Min(4, (int)MathF.Ceiling(-MathF.Log10(step))));
    }
}
