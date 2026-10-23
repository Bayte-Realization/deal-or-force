using System;

namespace DealOrForce.Simulation;

/// <summary>Only presentation: never changes intelligence, ownership or simulation randomness.</summary>
public readonly record struct WeatherReadability(float CloudOpacity, float MistOpacity, float EdgeSoftness, float DetailStrength)
{
    public static WeatherReadability Read(Defines d)
    {
        static float Sane(float? value, float fallback, float min, float max) =>
            value is { } v && float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        return new(Sane(d?.CloudOpacity, .50f, 0, 1), Sane(d?.MistOpacity, .22f, 0, 1),
            Sane(d?.CloudEdgeSoftness, .20f, .02f, .40f), Sane(d?.CloudDetailStrength, .08f, 0, .30f));
    }

    /// <summary>Clouds and mist draw unless this campaign's defines turn <c>weather</c> off. A null campaign is the menu globe.</summary>
    public static bool LayersAllowed(Campaign campaign) => campaign is null || campaign.FeatureEnabled("weather");
}

public sealed partial class Defines
{
    public float CloudOpacity { get; set; } = .50f;
    public float MistOpacity { get; set; } = .22f;
    public float CloudEdgeSoftness { get; set; } = .20f;
    public float CloudDetailStrength { get; set; } = .08f;
}
