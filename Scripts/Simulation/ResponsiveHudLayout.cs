using System;

namespace DealOrForce.Simulation;

/// <summary>Deterministic screen-space budgets, independent of the renderer and the campaign.</summary>
public static class ResponsiveHudLayout
{
    public readonly record struct NavigationPlan(bool CategoryMode, float ButtonHeight);
    public readonly record struct Bounds(float X, float Y, float Width, float Height);

    /// <summary>
    /// Keep the full rail when readable buttons fit. A short display discloses one category
    /// instead of shrinking every destination to an unreadable icon or hiding the last entries.
    /// </summary>
    public static NavigationPlan Navigation(float rowsHeight, int destinations, int categories,
        int selectedDestinations, float preferredHeight = 50, float captionHeight = 16, float gap = 4)
    {
        rowsHeight = Finite(rowsHeight, 0);
        preferredHeight = Math.Clamp(Finite(preferredHeight, 50), 28, 96);
        captionHeight = Math.Clamp(Finite(captionHeight, 16), 0, 64);
        gap = Math.Clamp(Finite(gap, 4), 0, 24);
        destinations = Math.Max(0, destinations);
        categories = Math.Clamp(categories, 0, destinations);
        var allSpace = Math.Max(0, rowsHeight - categories * captionHeight - Math.Max(0, destinations + categories - 1) * gap);
        var allHeight = destinations == 0 ? preferredHeight : Math.Min(preferredHeight, MathF.Floor(allSpace / destinations));
        if (allHeight >= Math.Min(preferredHeight, 38)) return new(false, Math.Max(28, allHeight));
        // The category selector itself is outside the rows area. Scrolling stays available
        // for a very large modded category; a permanent scroll thumb is never required.
        selectedDestinations = Math.Clamp(selectedDestinations, 0, destinations);
        var selectedSpace = Math.Max(0, rowsHeight - 36 - Math.Max(0, selectedDestinations - 1) * gap);
        var height = selectedDestinations == 0 ? preferredHeight : MathF.Floor(selectedSpace / selectedDestinations);
        return new(true, Math.Clamp(height, 28, preferredHeight));
    }

    /// <summary>Clamp an authored/modded rectangle without allowing its minimum size off-screen.</summary>
    public static Bounds Fit(Bounds desired, float viewportWidth, float viewportHeight, float inset = 8)
    {
        viewportWidth = Math.Max(1, Finite(viewportWidth, 1));
        viewportHeight = Math.Max(1, Finite(viewportHeight, 1));
        inset = Math.Clamp(Finite(inset, 8), 0, Math.Max(0, Math.Min(viewportWidth - 1, viewportHeight - 1) * .5f));
        var width = Math.Clamp(Finite(desired.Width, 1), 1, Math.Max(1, viewportWidth - 2 * inset));
        var height = Math.Clamp(Finite(desired.Height, 1), 1, Math.Max(1, viewportHeight - 2 * inset));
        return new(Math.Clamp(Finite(desired.X, inset), inset, viewportWidth - inset - width),
            Math.Clamp(Finite(desired.Y, inset), inset, viewportHeight - inset - height), width, height);
    }

    public static int DrawerColumns(float width, float desiredCellWidth = 190, int maximum = 3) =>
        Math.Clamp((int)MathF.Floor(Math.Max(1, Finite(width, 1)) / Math.Max(80, Finite(desiredCellWidth, 190))), 1, Math.Clamp(maximum, 1, 8));

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}
