using System;
using System.Linq;
namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How wrong one general staff's estimate of another country's forces can be, either way.</summary>
    public float IntelEstimateError { get; set; } = 0.25f;
}

/// <summary>National internal statistics remain private. ProvinceVision handles local
/// reconnaissance separately; planners retain priors for unobserved foreign forces.</summary>
public static partial class CampaignIntel
{
    public static bool CanInspect(int viewerId, int nationId) => viewerId >= 0 && viewerId == nationId;
    public static bool PlayerCanInspect(Campaign c, int nationId) => c?.Player is not null &&
        (!c.Player.IsHuman || CanInspect(c.Player.Id, nationId));
    /// <summary>
    /// What a general staff thinks another country's forces are worth.
    ///
    /// It used to be the number of regions the country held, times 1.6, whatever was standing in
    /// them - so South Korea, with half a million soldiers in seventeen regions, looked to
    /// Pyongyang like a twentieth of what it was, and North Korea attacked it in the first year
    /// of most runs. How large another country's army is, and what it is equipped with, is the
    /// most published fact in the world: the estimate is now the real figure, wrong by up to a
    /// quarter either way, the error fixed for each pair of countries and drifting by the year.
    /// </summary>
    public static float EstimatedStrength(Campaign c, int viewerId, Nation target)
    {
        if (target is null) return 0;
        var actual = AiStatecraft.CachedStrength(c, target);
        if (CanInspect(viewerId, target.Id)) return actual;
        return actual * (1f + c.Defines.IntelEstimateError * (2f * Error(c.Seed, viewerId, target.Id, c.ElapsedDays / 365) - 1f));
    }

    private static float Error(int seed, int viewer, int target, int year)
    {
        unchecked
        {
            var hash = (uint)seed * 2654435761u ^ (uint)(viewer + 1) * 2246822519u ^ (uint)(target + 1) * 3266489917u ^ (uint)year * 668265263u;
            hash ^= hash >> 15; hash *= 2246822519u;
            hash ^= hash >> 13; hash *= 3266489917u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFFF) / (float)0x1000000;
        }
    }

    public static float EstimatedNavy(Campaign c, Nation target) =>
        Math.Clamp(AiStatecraft.RegionsHeldBy(c, target.MapIndex) * 3f, 10f, 180f);
}
