using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The Godot side of <see cref="InterfaceSkin"/>: turns a named token into a real
/// <see cref="Color"/> and caches the result, so drawing code can ask for a role by name
/// on every frame without re-parsing a hex string each time.
///
/// The split matters. <see cref="InterfaceSkin"/> holds the data and knows nothing about
/// the engine, which is what lets the headless bench load and validate a mod's skin without
/// a renderer; this class is the only place that turns that data into engine types.
/// </summary>
internal static class Ui
{
    private static readonly Dictionary<string, Color> Cache = new();

    static Ui() => InterfaceSkin.Changed += Invalidate;

    /// <summary>A token's colour. Unknown tokens come back magenta, loudly and without throwing.</summary>
    internal static Color Colour(string token)
    {
        if (Cache.TryGetValue(token, out var cached)) return cached;
        var colour = new Color(InterfaceSkin.Colour(token));
        Cache[token] = colour;
        return colour;
    }

    /// <summary>A token's colour at a different opacity, without inventing a second token for it.</summary>
    internal static Color Colour(string token, float alpha)
    {
        var colour = Colour(token);
        colour.A = alpha;
        return colour;
    }

    internal static float Metric(string token, float fallback) => InterfaceSkin.Metric(token, fallback);

    /// <summary>
    /// Put a panel where the layout says it goes. Sixty-four anchor and offset assignments
    /// used to be spread across a dozen files; a panel is a name now, and the rectangle
    /// behind the name is data a mod can move.
    /// </summary>
    internal static T Place<T>(T control, string key) where T : Control
    {
        var rect = InterfaceSkin.Layout(key);
        control.AnchorLeft = rect.AnchorLeft;
        control.AnchorTop = rect.AnchorTop;
        control.AnchorRight = rect.AnchorRight;
        control.AnchorBottom = rect.AnchorBottom;
        control.OffsetLeft = rect.OffsetLeft;
        control.OffsetTop = rect.OffsetTop;
        control.OffsetRight = rect.OffsetRight;
        control.OffsetBottom = rect.OffsetBottom;
        return control;
    }

    /// <summary>Drop every cached brush. Called automatically when a mod reloads the skin.</summary>
    internal static void Invalidate() => Cache.Clear();
}
