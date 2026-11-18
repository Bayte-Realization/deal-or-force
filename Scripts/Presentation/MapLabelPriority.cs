using System;
using System.Collections.Generic;

namespace DealOrForce;

public enum MapLabelTier
{
    PlayerAndSelected = 0,
    CapitalOrMajorPower = 1,
    ProminentCity = 2,
    Settlement = 3,
    Region = 4,
    MajorRiver = 5
}

public readonly record struct LabelBox(float X, float Y, float Width, float Height)
{
    public bool Intersects(LabelBox other) =>
        X < other.X + other.Width && X + Width > other.X &&
        Y < other.Y + other.Height && Y + Height > other.Y;

    public LabelBox Grow(float padding) =>
        new(X - padding, Y - padding, Width + padding * 2f, Height + padding * 2f);
}

public readonly record struct AtmospherePolishState(
    bool WarmLampRim,
    float RimWarmth,
    float VignetteStrength,
    float VignetteWarmth);

/// <summary>
/// Pure-C# cartographic label hierarchy, collision avoidance, zoom-band alpha ramps,
/// smooth fade stepping, and PC-only atmosphere gating for the atlas map look.
/// </summary>
public static class MapLabelPriority
{
    public const float DefaultFadeSpeed = 6.5f;

    public static bool IsForwardPlusRenderer(string renderingMethod) =>
        string.Equals(renderingMethod?.Trim(), "forward_plus", StringComparison.OrdinalIgnoreCase);

    public static AtmospherePolishState AtmospherePolish(
        bool atlasLook, string renderingMethod, float baseVignetteStrength)
    {
        var clampedVignette = Math.Clamp(baseVignetteStrength, 0f, 1f);
        if (!atlasLook || !IsForwardPlusRenderer(renderingMethod))
            return new AtmospherePolishState(false, 0f, clampedVignette, 0f);
        return new AtmospherePolishState(
            WarmLampRim: true,
            RimWarmth: 0.55f,
            VignetteStrength: Math.Clamp(clampedVignette * 0.90f, 0f, 1f),
            VignetteWarmth: 0.65f);
    }

    public static float CountryMinimumCells(bool flat, float distanceOrZoom) => flat
        ? distanceOrZoom > 8.8f ? 26000f : distanceOrZoom > 6.2f ? 6500f : distanceOrZoom > 4.0f ? 1500f : distanceOrZoom > 2.6f ? 150f : 40f
        : distanceOrZoom > 11.5f ? 26000f : distanceOrZoom > 9.5f ? 6500f : distanceOrZoom > 7.4f ? 1500f : 150f;

    /// <summary>
    /// Smooth zoom-band target opacity for country names (countries mid+far).
    /// Classic keeps binary threshold behaviour; Atlas adds smooth boundary ramps and slightly
    /// preserves the player/selected nation further into strategic zoom.
    /// </summary>
    public static float CountryTargetAlpha(
        bool atlasLook, bool flat, float distanceOrZoom, int cells, bool isPlayerOrSelected, bool horizonVisible)
    {
        if (!horizonVisible || cells <= 0) return 0f;
        var minCells = CountryMinimumCells(flat, distanceOrZoom);
        if (!atlasLook)
        {
            var inBand = flat ? distanceOrZoom >= 0.85f : distanceOrZoom >= 7.9f;
            return inBand && cells >= minCells ? 1f : 0f;
        }
        var bandAlpha = flat
            ? SmoothStep(0.76f, 0.98f, distanceOrZoom)
            : SmoothStep(7.5f, 8.3f, distanceOrZoom);
        if (bandAlpha <= 0f) return 0f;
        var effectiveMin = isPlayerOrSelected ? minCells * 0.55f : minCells;
        var sizeAlpha = SmoothStep(effectiveMin * 0.68f, effectiveMin * 1.18f, cells);
        return bandAlpha * sizeAlpha * 0.90f;
    }

    /// <summary>
    /// Smooth mid-zoom band for regional labels (atlas-only; regions yield to continental
    /// countries far out and to cities/tactical counters up close).
    /// </summary>
    public static float RegionTargetAlpha(
        bool atlasLook, bool flat, float distanceOrZoom, int cells, bool horizonVisible)
    {
        if (!atlasLook || !horizonVisible || cells < 180) return 0f;
        float band;
        if (flat)
        {
            var enter = SmoothStep(0.55f, 0.95f, distanceOrZoom);
            var leave = 1f - SmoothStep(2.2f, 3.2f, distanceOrZoom);
            band = enter * leave;
        }
        else
        {
            var enter = SmoothStep(6.0f, 7.1f, distanceOrZoom);
            var leave = 1f - SmoothStep(9.4f, 11.0f, distanceOrZoom);
            band = enter * leave;
        }
        var areaWeight = SmoothStep(180f, 1600f, cells);
        return band * areaWeight * 0.70f;
    }

    /// <summary>
    /// Smooth near+mid zoom band for named major rivers (atlas-only; yielded on collision).
    /// </summary>
    public static float RiverTargetAlpha(
        bool atlasLook, bool flat, float distanceOrZoom, int rank, bool horizonVisible)
    {
        if (!atlasLook || !horizonVisible || rank > 4) return 0f;
        float band;
        if (flat)
            band = 1f - SmoothStep(2.6f, 4.0f, distanceOrZoom);
        else
            band = 1f - SmoothStep(9.6f, 12.2f, distanceOrZoom);
        var rankCeiling = rank switch { <= 1 => 0.86f, 2 => 0.82f, 3 => 0.76f, _ => 0.70f };
        return band * rankCeiling;
    }

    public static float StepOpacity(float current, float target, float deltaSeconds, float speed = DefaultFadeSpeed)
    {
        if (deltaSeconds <= 0f || speed <= 0f) return Math.Clamp(current, 0f, 1f);
        var clampedTarget = Math.Clamp(target, 0f, 1f);
        var clampedCurrent = Math.Clamp(current, 0f, 1f);
        var maxStep = speed * deltaSeconds;
        var diff = clampedTarget - clampedCurrent;
        if (MathF.Abs(diff) <= maxStep) return clampedTarget;
        return clampedCurrent + MathF.Sign(diff) * maxStep;
    }

    /// <summary>
    /// Deterministic placement priority order:
    /// 1. Player capital + selected country capital
    /// 2. Other national capitals (prominence order)
    /// 3. Player / selected country cities (in current band)
    /// 4. Remaining cities in prominence order
    /// (Major rivers sit in Tier 5 afterwards so they never displace settlements or capitals.)
    /// </summary>
    public static List<int> OrderCityCandidates(
        IReadOnlyList<int> capitals,
        int playerCapital,
        int selectedCapital,
        int candidateLimit,
        Func<int, bool> isPlayerOrSelectedCity)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>(Math.Max(16, capitals.Count + candidateLimit));

        void AddIfValid(int index)
        {
            if (index >= 0 && seen.Add(index)) ordered.Add(index);
        }

        AddIfValid(playerCapital);
        AddIfValid(selectedCapital);
        for (var i = 0; i < capitals.Count; i++) AddIfValid(capitals[i]);

        if (isPlayerOrSelectedCity is not null)
        {
            for (var i = 0; i < candidateLimit; i++)
                if (isPlayerOrSelectedCity(i)) AddIfValid(i);
        }
        for (var i = 0; i < candidateLimit; i++) AddIfValid(i);
        return ordered;
    }

    /// <summary>
    /// Finds a non-overlapping label side around a map mark (1 right, -1 left, 2 above, -2 below)
    /// or 0 if every side collides with a previously placed higher-priority label or star.
    /// </summary>
    public static int SolveSide(
        float screenX, float screenY, float mark, float textWidth, float textHeight,
        IReadOnlyList<LabelBox> taken,
        IReadOnlyList<(float X, float Y, int Key)> stars,
        int selfKey,
        out LabelBox room)
    {
        room = default;
        var width = mark + 5f + textWidth + 4f;
        var top = screenY - textHeight * 0.62f;
        var right = new LabelBox(screenX - mark * 0.5f - 2f, top, width, textHeight * 1.24f);
        var left = new LabelBox(screenX + mark * 0.5f + 2f - width, top, width, textHeight * 1.24f);
        if (IsFree(right)) { room = right; return 1; }
        if (IsFree(left)) { room = left; return -1; }

        var half = MathF.Max(textWidth * 0.5f + 4f, mark * 0.5f + 2f);
        var tall = textHeight * 1.24f + mark * 0.5f + 2f;
        var above = new LabelBox(screenX - half, screenY + mark * 0.5f + 2f - tall - textHeight * 0.1f, half * 2f, tall + textHeight * 0.1f);
        if (IsFree(above)) { room = above; return 2; }
        var below = new LabelBox(screenX - half, screenY - mark * 0.5f - 2f, half * 2f, tall + textHeight * 0.1f);
        if (IsFree(below)) { room = below; return -2; }
        return 0;

        bool IsFree(LabelBox box)
        {
            for (var i = 0; i < taken.Count; i++)
                if (taken[i].Intersects(box)) return false;
            for (var i = 0; i < stars.Count; i++)
            {
                var star = stars[i];
                var dx = star.X - screenX;
                var dy = star.Y - screenY;
                if ((selfKey >= 0 && star.Key == selfKey) || dx * dx + dy * dy < 4f) continue;
                if (new LabelBox(star.X - 6f, star.Y - 6f, 12f, 12f).Intersects(box)) return false;
            }
            return true;
        }
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0) return value >= edge0 ? 1f : 0f;
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
