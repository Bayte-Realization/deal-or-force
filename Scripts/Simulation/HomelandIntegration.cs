using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class ScenarioState
{
    /// <summary>The owner (map index) this region was last seen under, for HomelandIntegration; 0 = not yet looked at.</summary>
    public int TrackedOwnerMapIndex;
    /// <summary>The day that owner was first seen holding it; -1 = since before the campaign began.</summary>
    public int OwnerSinceDay = -1;
}

public sealed partial class Campaign
{
    /// <summary>How many times a region became a country's homeland or stopped being one in play (HomelandIntegration).</summary>
    public int HomelandChanges;
}

public sealed partial class Defines
{
    /// <summary>Years a country must hold a region before it can become part of its homeland. Zero switches integration off.</summary>
    public int HomelandIntegrationYears { get; set; } = 25;
    /// <summary>The share of the region's people that must be the owner's own for integration at that age.</summary>
    public float HomelandIntegrationOwnShare { get; set; } = 0.5f;
    /// <summary>Or, held twice as long, a region this calm (radicalism below) integrates whoever lives there.</summary>
    public float HomelandIntegrationCalmRadicalism { get; set; } = 0.25f;
    /// <summary>Years a lost homeland region must be held by others before the memory fades to a claim. Zero: never.</summary>
    public int HomelandFadeYears { get; set; } = 60;
    /// <summary>The fade happens only where the claimant's own people are fewer than this share of the residents.</summary>
    public float HomelandFadeOwnShare { get; set; } = 0.1f;
}

/// <summary>
/// Homelands that move with history instead of staying the 2024 snapshot.
///
/// A homeland was the ground a country held when the world first saw it, and only renouncing it
/// ever changed that: Crimea held for a century by people of the holder's own was never theirs,
/// and Prussia lost for a lifetime, with nobody left there who spoke the language, was a war aim
/// for ever. Now a region a country has held for a generation, among its own people (or for two
/// generations and calm), becomes part of its homeland; and a homeland held by others for a
/// lifetime, where hardly any of the claimant's own people remain, fades to a claim - remembered,
/// argued over, worth less in a peace and a deal and no longer first in the queue of wars.
///
/// Monthly, each region on its own day. Owners are noticed at that granularity, which a quarter
/// of a century does not mind.
/// </summary>
public static class HomelandIntegration
{
    public const int Cycle = 30;

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same monthly pass, yielded between regions so a newly independent world's
    /// first integration sweep does not land in one frame.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(HomelandIntegration))) yield break;
        var d = c.Defines;
        if (d.HomelandIntegrationYears <= 0 && d.HomelandFadeYears <= 0) yield break;
        var owners = c.NationsByMapIndex();
        var scanned = 0;
        foreach (var region in c.States)
        {
            if (++scanned % 64 == 0) yield return 0;
            if (!MovementRules.OnItsDay(c, region.MapIndex, Cycle)) continue;
            if (region.Unclaimed || region.Population <= 0) continue;
            if (region.TrackedOwnerMapIndex != region.NationMapIndex)
            {
                // The first look is not a change of hands: whoever holds it at the start has held
                // it since before the campaign (-1). Any later change starts the clock.
                var first = region.TrackedOwnerMapIndex == 0;
                region.TrackedOwnerMapIndex = region.NationMapIndex;
                region.OwnerSinceDay = first ? -1 : c.ElapsedDays;
                continue;
            }
            if (!owners.TryGetValue(region.NationMapIndex, out var owner) || owner.Movement is not null) continue;
            // A first owner has held it at least since the campaign began; the years before it are
            // the scenario's business (its CoreTags), so the clock starts on day 0.
            var heldDays = c.ElapsedDays - Math.Max(0, region.OwnerSinceDay);
            Integrate(c, owner, region, heldDays);
            Fade(c, owner, region, heldDays);
        }
    }

    private static void Integrate(Campaign c, Nation owner, ScenarioState region, int heldDays)
    {
        var d = c.Defines;
        if (d.HomelandIntegrationYears <= 0 || heldDays < d.HomelandIntegrationYears * 365) return;
        if (TerritorialClaims.TitleOf(c, owner, region) == TerritorialTitle.Homeland) return;
        if (owner.RenouncedTerritoryOrigins.Contains(TerritorialClaims.Origin(region))) return;
        var own = 1f - EconomySystem.ForeignShare(region, owner.Tag);
        var settled = own >= d.HomelandIntegrationOwnShare ||
                      heldDays >= 2 * d.HomelandIntegrationYears * 365 && region.Radicalism < d.HomelandIntegrationCalmRadicalism;
        if (!settled) return;
        if (!c.Homelands.TryGetValue(owner.Id, out var home)) c.Homelands[owner.Id] = home = new List<int>();
        // This piece, not the region it was cut from: integrating the half a country holds must
        // not hand it a title to the half its neighbour holds.
        home.Add(region.MapIndex);
        c.HomelandChanges++;
        c.Chronicle.Record(c, "homeland_integrated", owner.Id, -1, place: region.Name);
        c.MarkStrategicChange();
    }

    private static void Fade(Campaign c, Nation owner, ScenarioState region, int heldDays)
    {
        var d = c.Defines;
        if (d.HomelandFadeYears <= 0 || heldDays < d.HomelandFadeYears * 365) return;
        var origin = TerritorialClaims.Origin(region);
        foreach (var claimant in c.Nations)
        {
            if (claimant.Id == owner.Id || claimant.Population <= 0 || claimant.Movement is not null) continue;
            if (TerritorialClaims.TitleOf(c, claimant, region) != TerritorialTitle.Homeland) continue;
            if (1f - EconomySystem.ForeignShare(region, claimant.Tag) >= d.HomelandFadeOwnShare) continue;
            // Never while it still holds a piece of the same region: the memory is of the place.
            if (c.States.Any(s => s.NationMapIndex == claimant.MapIndex && TerritorialClaims.Origin(s) == origin)) continue;
            if (c.Homelands.TryGetValue(claimant.Id, out var home))
                home.RemoveAll(id => id == origin || id == region.MapIndex);
            region.CoreTags.RemoveAll(tag => string.Equals(tag, claimant.Tag, StringComparison.OrdinalIgnoreCase));
            if (!region.Claims.Contains(claimant.Tag, StringComparer.OrdinalIgnoreCase)) region.Claims.Add(claimant.Tag);
            c.HomelandChanges++;
            c.Chronicle.Record(c, "homeland_faded", claimant.Id, owner.Id, place: region.Name);
            c.MarkStrategicChange();
        }
    }
}
