using System;
using System.Linq;
namespace DealOrForce.Simulation;

/// <summary>National internal statistics remain private. ProvinceVision handles local
/// reconnaissance separately; planners retain priors for unobserved foreign forces.</summary>
public static partial class CampaignIntel
{
    public static bool CanInspect(int viewerId, int nationId) => viewerId >= 0 && viewerId == nationId;
    public static bool PlayerCanInspect(Campaign c, int nationId) => c?.Player is not null &&
        (!c.Player.IsHuman || CanInspect(c.Player.Id, nationId));
    public static float EstimatedStrength(Campaign c, int viewerId, Nation target) => target is null ? 0 :
        CanInspect(viewerId, target.Id) ? AiStatecraft.StrengthOf(c, target) :
        Math.Clamp(c.States.Count(s => s.NationMapIndex == target.MapIndex) * 1.6f, 5f, 250f);
    public static float EstimatedNavy(Campaign c, Nation target) =>
        Math.Clamp(c.States.Count(s => s.NationMapIndex == target.MapIndex) * 3f, 10f, 180f);
}
