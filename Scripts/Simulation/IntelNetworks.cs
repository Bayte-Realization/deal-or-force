using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>This country's intelligence networks abroad: target country id to strength, 0 to 1.</summary>
    public Dictionary<int, float> IntelNetworks = new();
}

public sealed partial class Defines
{
    /// <summary>What founding a network costs, as a share of the founder's yearly output (billions, with a floor).</summary>
    public float IntelNetworkCostShare { get; set; } = 0.0008f;
    public float IntelNetworkMinimumCost { get; set; } = 0.05f;
    /// <summary>How much a network grows in a week against a target with no counter-intelligence at all.</summary>
    public float IntelNetworkGrowth { get; set; } = 0.012f;
    /// <summary>The most a network can reach: nobody sees everything.</summary>
    public float IntelNetworkCeiling { get; set; } = 0.9f;
    /// <summary>The weekly chance of exposure at full strength against an average counter-intelligence.</summary>
    public float IntelNetworkExposure { get; set; } = 0.004f;
    /// <summary>The strength from which a network opens the target's dossier - its forces and its people.</summary>
    public float IntelInspectStrength { get; set; } = 0.35f;
    /// <summary>The most networks one country runs at once.</summary>
    public int IntelNetworkLimit { get; set; } = 4;
    /// <summary>The grievance an exposed network leaves in the country it was found in.</summary>
    public float IntelExposureGrievance { get; set; } = 10f;
}

/// <summary>
/// Agents in another country, built over months and lost in a day.
///
/// The fog of war says what a country can see from its own ground and its own troops; past that
/// it has guesses - an estimate of the other side's strength wrong by up to a quarter and six
/// months old (CampaignIntel.EstimatedStrength), and a dossier that says "what they have is not
/// known to us". A network is the way past it. Founding one costs a little; it grows a week at a
/// time, slower against a state that watches its own - an authoritarian government, a country at
/// war; and as it grows it shows more: a growing share of the target's provinces through the fog
/// (a stable pattern, not a random flicker), an estimate of its strength that is nearer the truth
/// and fresher, and from a third of the way the dossier itself.
///
/// And it can be found. The stronger it is and the harder the target looks, the likelier: an
/// exposed network is gone, the target remembers it, and both sides have a grievance - which is
/// one of the things tension between two countries is made of (Escalation). Governments run
/// networks too, against their rivals and the countries they are at war with.
/// </summary>
public static class IntelNetworks
{
    /// <summary>How strong one country's network in another is, 0 to 1.</summary>
    public static float Strength(Campaign c, int ownerId, int targetId)
    {
        if (c is null || ownerId < 0 || targetId < 0 || ownerId == targetId || !c.FeatureEnabled("intelligence")) return 0f;
        return c.NationById(ownerId)?.IntelNetworks.GetValueOrDefault(targetId) ?? 0f;
    }

    /// <summary>What founding a network costs this country.</summary>
    public static float Cost(Campaign c, Nation owner) =>
        MathF.Max(c.Defines.IntelNetworkMinimumCost, owner.Gdp * c.Defines.IntelNetworkCostShare);

    /// <summary>How hard a country watches its own, 0 to 1: authority, war, and a settled state's means.</summary>
    public static float CounterIntelligence(Campaign c, Nation target)
    {
        var authority = M.Clamp01(PoliticsCore.IdeologyOf(target).Authority);
        var atWar = c.Wars.Any(w => !w.Ended && w.Involves(target.Id)) ? 0.15f : 0f;
        return M.Clamp01(0.20f + 0.40f * authority + atWar + 0.15f * M.Clamp01(target.Stability));
    }

    /// <summary>Why a country cannot found a network in another, or empty when it can.</summary>
    public static string Refusal(Campaign c, Nation owner, Nation target)
    {
        if (owner is null || target is null || owner.Id == target.Id) return Loc.Get("Choose another country.");
        if (owner.Movement is not null) return Loc.Get("Only a state runs intelligence networks.");
        if (owner.IntelNetworks.ContainsKey(target.Id)) return Loc.Get("A network is already working there.");
        if (owner.IntelNetworks.Count >= Math.Max(1, c.Defines.IntelNetworkLimit))
            return Loc.Format("The service already runs {0} networks.", owner.IntelNetworks.Count);
        if (owner.Treasury < Cost(c, owner)) return Loc.Format("It costs {0}.", Fmt.Money(Cost(c, owner)));
        return "";
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same weekly network pass, yielded between batches of owners.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !c.FeatureEnabled("intelligence") || !MechanicFeatures.SystemEnabled(c, nameof(IntelNetworks))) yield break;
        if (c.ElapsedDays % 7 != 0) yield break;
        var defines = c.Defines;
        var scanned = 0;
        foreach (var owner in c.Nations)
        {
            if (++scanned % 8 == 0) yield return 0;
            if (owner.IntelNetworks.Count == 0) continue;
            foreach (var targetId in owner.IntelNetworks.Keys.OrderBy(id => id).ToList())
            {
                var target = c.NationById(targetId);
                if (target is null || target.Population <= 0 || owner.Population <= 0) { owner.IntelNetworks.Remove(targetId); continue; }
                var strength = owner.IntelNetworks[targetId];
                var counter = CounterIntelligence(c, target);
                var exposure = defines.IntelNetworkExposure * strength * (0.5f + counter);
                if (MovementRules.Roll(c.Seed, owner.Id * 1000 + targetId, c.ElapsedDays, 4501) < exposure)
                {
                    Expose(c, owner, target);
                    continue;
                }
                owner.IntelNetworks[targetId] = MathF.Min(M.Clamp01(defines.IntelNetworkCeiling),
                    strength + defines.IntelNetworkGrowth * (1f - counter));
            }
        }
        if (c.ElapsedDays % 28 == 0)
            foreach (var _ in AiStages(c)) yield return 0;
    }

    private static void Expose(Campaign c, Nation owner, Nation target)
    {
        owner.IntelNetworks.Remove(target.Id);
        var relation = CampaignDiplomacy.Get(c, owner.Id, target.Id);
        if (relation is not null)
        {
            OpinionSystem.Remember(c, relation, "spied_on_us");
            // The aggrieved side is the one spied on.
            if (relation.A == target.Id) relation.GrievanceA += c.Defines.IntelExposureGrievance;
            else relation.GrievanceB += c.Defines.IntelExposureGrievance;
        }
        c.Chronicle.Record(c, "spy_network_exposed", target.Id, owner.Id);
        c.MarkStrategicChange();
    }

    /// <summary>
    /// Governments build networks where they most need to know: the countries they are at war
    /// with first, then rivals and the most hostile of their neighbours - one at a time, while
    /// the treasury allows and the service has room.
    /// </summary>
    private static IEnumerable<int> AiStages(Campaign c)
    {
        var scanned = 0;
        foreach (var owner in c.Nations)
        {
            if (++scanned % 4 == 0) yield return 0;
            if (owner.IsHuman || owner.Movement is not null || owner.Population <= 0 || owner.Gdp < 10f) continue;
            // A service grows with the state that pays for it: one network for a small country,
            // the full complement only for the largest economies.
            var room = Math.Clamp(1 + (int)(owner.Gdp / 400f), 1, Math.Max(1, c.Defines.IntelNetworkLimit));
            if (owner.IntelNetworks.Count >= room) continue;
            if (owner.Treasury < Cost(c, owner) * 4f) continue;
            Nation best = null;
            var bestScore = 0f;
            foreach (var relation in c.Relations.Values)
            {
                if (relation.A != owner.Id && relation.B != owner.Id) continue;
                var otherId = relation.A == owner.Id ? relation.B : relation.A;
                var other = c.NationById(otherId);
                if (other is null || other.Population <= 0 || other.Movement is not null || owner.IntelNetworks.ContainsKey(otherId)) continue;
                var score = relation.Stance switch
                {
                    DiplomaticStance.AtWar => 100f,
                    DiplomaticStance.Rival => 60f,
                    _ => MathF.Max(0f, -relation.Opinion - 40f) * (CampaignDiplomacy.SharesLandBorder(c, owner.MapIndex, other.MapIndex) ? 1f : 0.4f)
                } + relation.Tension * 0.3f;
                if (score <= bestScore) continue;
                bestScore = score;
                best = other;
            }
            if (best is null || bestScore < 25f) continue;
            new IntelNetworkCommand { IssuerId = owner.Id, TargetId = best.Id, Action = "found" }.Apply(c);
        }
    }

    private static readonly Dictionary<int, (int Id, float Share)> SharedNetworkShares = new();

    /// <summary>
    /// The target's provinces a network shows through the fog: a share equal to its strength,
    /// chosen by a fixed pattern per pair, so the picture fills in as the network grows instead
    /// of flickering from week to week. Written as reach one: seen, not seen past.
    /// </summary>
    internal static void RevealInto(Campaign c, int viewerId, byte[] reach)
    {
        var viewer = c.NationById(viewerId);
        if (viewer is null || viewer.IntelNetworks.Count == 0 || c.ProvinceMap is null || !c.FeatureEnabled("intelligence")) return;
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        // One pass over the map for every network at once: the share each target is seen by.
        SharedNetworkShares.Clear();
        foreach (var (targetId, strength) in viewer.IntelNetworks)
            if (strength > 0.01f && c.NationById(targetId) is { } target) SharedNetworkShares[target.MapIndex] = (targetId, M.Clamp01(strength));
        if (SharedNetworkShares.Count == 0) return;
        // Only the targets' own provinces (CampaignIntel.ProvincesHeldBy), not the whole map.
        foreach (var (mapIndex, seen) in SharedNetworkShares)
            foreach (var p in CampaignIntel.ProvincesHeldBy(c, controllers, mapIndex))
            {
                if (p >= reach.Length || reach[p] > 0) continue;
                if (Pattern(c.Seed, viewerId, seen.Id, p) < seen.Share) reach[p] = 1;
            }
    }

    private static float Pattern(int seed, int viewer, int target, int province)
    {
        unchecked
        {
            var hash = (uint)seed * 2654435761u ^ (uint)(viewer + 7) * 2246822519u ^ (uint)(target + 13) * 3266489917u ^ (uint)province * 668265263u;
            hash ^= hash >> 15; hash *= 2246822519u;
            hash ^= hash >> 13; hash *= 3266489917u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFFF) / (float)0x1000000;
        }
    }
}

/// <summary>Found or recall a network in another country.</summary>
public sealed class IntelNetworkCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    /// <summary>"found" or "recall".</summary>
    public string Action { get; init; } = "found";
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var owner = campaign.NationById(IssuerId);
        var target = campaign.NationById(TargetId);
        if (owner is null || target is null) { Refusal = "No such country."; return; }
        if (Action == "recall")
        {
            owner.IntelNetworks.Remove(target.Id);
            campaign.MarkStrategicChange();
            return;
        }
        Refusal = IntelNetworks.Refusal(campaign, owner, target);
        if (Refusal.Length > 0) return;
        owner.Treasury -= IntelNetworks.Cost(campaign, owner);
        owner.IntelNetworks[target.Id] = 0.02f;
        campaign.MarkStrategicChange();
    }
}
