using System;

namespace DealOrForce.Simulation;

/// <summary>
/// Natural macro-historical waves of international tension and détente (~5-7 year cycle).
/// History moves in tides: periods of relative relaxation and multilateral agreements
/// alternate with periods of heightened geopolitical friction, polarization, and vigilance.
/// </summary>
public static class GeopoliticalCycle
{
    /// <summary>
    /// Global drift offset in points (-8f .. +10f) for international diplomatic tension.
    /// Positive values represent an era of heightened geopolitical friction / polarization;
    /// negative values represent an era of détente / multilateral relaxation.
    /// </summary>
    public static float GlobalTensionOffset(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays <= 0) return 0f;
        // Primary wavelength ~ 6 years (2190 days)
        var phase = ((campaign.Seed % 2190) + campaign.ElapsedDays) * (2.0 * Math.PI / 2190.0);
        var wave = (float)Math.Sin(phase);
        var secondaryWave = (float)Math.Sin(phase * 2.3 + 1.2) * 0.35f;

        // Feedback from global major conflicts: if the world is engulfed in large-scale wars, global tension rises
        var activeMajorWars = 0;
        foreach (var war in campaign.Wars)
        {
            if (!war.Ended && (war.Intensity >= 1 || war.AttackerLosses + war.DefenderLosses > 50_000))
                activeMajorWars++;
        }
        var warFeedback = MathF.Min(5f, activeMajorWars * 1.5f);

        return Math.Clamp((wave + secondaryWave) * 6.0f + warFeedback, -8.0f, 10.0f);
    }

    /// <summary>Whether the international system is currently in a relaxed détente phase.</summary>
    public static bool IsDetenteEra(Campaign campaign) => GlobalTensionOffset(campaign) < -2.0f;

    /// <summary>Whether the international system is currently in an era of heightened geopolitical friction.</summary>
    public static bool IsFrictionEra(Campaign campaign) => GlobalTensionOffset(campaign) > 2.0f;
}
