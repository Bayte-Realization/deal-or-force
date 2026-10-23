using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How much a quiet week adds to a union that is holding together.</summary>
    public float UnionCohesionRecoveryWeekly { get; set; } = 0.014f;

    /// <summary>What a week of war takes out of it: a war makes a country, and a lost one unmakes it.</summary>
    public float UnionCohesionWarWeekly { get; set; } = 0.022f;

    /// <summary>Weight of stability below the calm line, per week.</summary>
    public float UnionCohesionStabilityWeight { get; set; } = 0.07f;

    /// <summary>Where "calm enough to be one country" begins.</summary>
    public float UnionCalmStability { get; set; } = 0.6f;

    /// <summary>What a week of radicalism in its regions takes out of it, at radicalism 1.</summary>
    public float UnionCohesionRadicalismWeekly { get; set; } = 0.05f;

    /// <summary>What one people beyond the first costs a union at the moment it is proclaimed.</summary>
    public float UnionCohesionPerPeople { get; set; } = 0.12f;

    /// <summary>Below this a proclaimed state starts coming apart, region by region.</summary>
    public float UnionCohesionFloor { get; set; } = 0.3f;

    /// <summary>How long a union breathes after losing a region before it may lose another.</summary>
    public int UnionSecessionCooldownDays { get; set; } = 210;

    /// <summary>How many days a proclaimed state is left alone before its hold is tested at all.</summary>
    public int UnionGraceDays { get; set; } = 90;
}

public sealed partial class Nation
{
    /// <summary>
    /// How well a state that was proclaimed holds together: 1 is a country that believes it is one.
    /// Read and written by UnionCohesion, and meaningful only for a nation with a FormedNationId.
    /// </summary>
    public float UnionCohesion = 1f;

    /// <summary>The last day this union lost a region, so it is not taken apart in one sitting.</summary>
    public int UnionLastSecessionDay = -9999;
}

/// <summary>
/// Whether a state that was proclaimed stays proclaimed.
///
/// Every union, restoration and reunification in the catalogue is born of a claim, and the game had
/// no answer to what happens to that claim afterwards: a country proclaimed in 2026 was exactly as
/// whole in 2056, which makes the map's history stop at the moment of the proclamation. A proclaimed
/// state is not a fact of nature; it is a thing a government does every week. A union of several
/// peoples is harder to do than a reunification of one, which is why Korea mends and a two-Congo
/// union has to be worked at.
///
/// So a proclaimed state carries a cohesion: decided when it is proclaimed (from how many peoples it
/// welded together) and moved every day by what is actually happening to it - war takes it out,
/// instability and radicalism take it out, a quiet year puts it back. Below the floor it comes apart
/// through the machinery that was already there (SecessionEvents, the same reports, the same new
/// states) rather than through a rule of its own. What it loses is ground, not the claim: the
/// history is kept (FormedNationHistory), and a country that puts the ground back together can
/// proclaim the same state again - history repeating is the point.
///
/// Trouble is expressed through the ordinary modifier sources, so it shows up in the numbers a
/// player already watches: a union that is coming apart costs stability and feeds radicalism in its
/// regions, and one that holds gives a little stability back.
/// </summary>
public static class UnionCohesion
{
    /// <summary>What a union's state of mind is called in the modifier breakdown.</summary>
    public static string Caption => Loc.Get("Union cohesion");

    /// <summary>
    /// The cohesion a state is proclaimed with. One people is one country and starts whole; every
    /// people after that is a promise to be kept.
    /// </summary>
    public static float Start(Campaign campaign, Nation nation, FormableNation definition)
    {
        // How many peoples it welded together: what the state declared, what the claim itself named,
        // and at least its own. A union of two countries costs one people's worth of penalty.
        var peoples = Math.Max(1, Math.Max(nation.FoundedCultures.Count, definition.RequiredTags.Length));
        peoples = Math.Max(peoples, Peoples(nation));
        var penalty = Math.Max(0, peoples - 1) * campaign.Defines.UnionCohesionPerPeople;
        // A state brought back is not a new state: the ground was one country within living memory,
        // and its own declaration says so (Restores). It pays half of the penalty and no more.
        if (definition is { Restores.Length: > 0 }) penalty *= 0.5f;
        return Math.Clamp(1f - penalty, 0.35f, 1f);
    }

    /// <summary>How many peoples the state's ground speaks: its declared ones, or its own.</summary>
    private static int Peoples(Nation nation)
    {
        var declared = nation.FoundedCultures.Count;
        if (declared == 0) declared = DemographyCatalog.PrimaryCultures(nation.Tag).Count;
        return Math.Max(1, declared);
    }

    /// <summary>
    /// Once a day, a seventh of a week at a time, as the weekly modifier keys are paid. Nothing here
    /// runs for a country that has proclaimed nothing: an ordinary state is as whole as it looks.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(UnionCohesion))) return;
        var defines = campaign.Defines;
        // SecessionEvents.Force appends a new country. Nation IDs keep their list
        // indices, so review only the initial cohort without a mutable enumerator
        // or a daily snapshot allocation. New countries join the next review.
        var nationsAtStart = campaign.Nations.Count;
        for (var index = 0; index < nationsAtStart; index++)
        {
            var nation = campaign.Nations[index];
            var isFormed = !string.IsNullOrEmpty(nation.FormedNationId);
            var isVulnerableFederation = !isFormed && campaign.FeatureEnabled("federal_fracture") &&
                                         (nation.Government is "federation" or "confederation" || nation.Tag is "USA" or "RUS") &&
                                         nation.Population >= 20_000_000 &&
                                         campaign.StatesOf(nation).Length >= 4 &&
                                         (nation.Stability < 0.38f ||
                                          (nation.Stability < 0.48f && nation.DebtCondition >= DebtCondition.Insolvent) ||
                                          nation.DebtCondition >= DebtCondition.Default ||
                                          (nation.DomesticCrisis?.CoupPressure > 0.40f) ||
                                          (WarWeariness.NationalFatigue(campaign, nation) >= 0.55f));
            if (!isFormed && !isVulnerableFederation) continue;
            // A state proclaimed last month is not judged this month: the ink is still wet.
            if (campaign.ElapsedDays < nation.FormedNationDay + defines.UnionGraceDays) continue;
            var atWar = false;
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
            var weekly = defines.UnionCohesionRecoveryWeekly;
            if (atWar) weekly -= defines.UnionCohesionWarWeekly;
            weekly -= Radicalism(campaign, nation) * defines.UnionCohesionRadicalismWeekly;
            weekly -= Math.Max(0f, defines.UnionCalmStability - nation.Stability) *
                defines.UnionCohesionStabilityWeight;
            nation.UnionCohesion = Math.Clamp(nation.UnionCohesion + weekly / 7f, 0f, 1f);
            if (nation.UnionCohesion >= defines.UnionCohesionFloor) continue;
            if (campaign.ElapsedDays - nation.UnionLastSecessionDay < defines.UnionSecessionCooldownDays) continue;
            // A foreign war abroad rallies a country only while stability holds; a broken state collapses under fire.
            if (atWar && nation.Stability >= 0.25f && WarWeariness.NationalFatigue(campaign, nation) < 0.50f) continue;
            if (!LoseTheAngriestRegion(campaign, nation)) continue;
            nation.UnionLastSecessionDay = campaign.ElapsedDays;
            // Losing a region settles the rest for a while, the way a divorce does.
            nation.UnionCohesion = Math.Min(1f, nation.UnionCohesion + 0.25f);
            NationalModifiers.Invalidate(nation);
        }
    }

    /// <summary>Average radicalism of the regions the union actually governs.</summary>
    private static float Radicalism(Campaign campaign, Nation nation)
    {
        var states = campaign.StatesOf(nation);
        if (states.Length == 0) return 0f;
        var total = 0f;
        foreach (var state in states) total += state.Radicalism;
        return total / states.Length;
    }

    /// <summary>
    /// The region that leaves: the angriest one the union holds, because that is the region whose
    /// people have been telling anyone who asks that they are not part of this. Public so that what
    /// the mechanic decides can be checked without a map: forcing an actual secession needs one
    /// (SecessionEvents.Force), and the test campaign does not carry a province map.
    /// </summary>
    public static ScenarioState AngriestRegion(Campaign campaign, Nation nation)
    {
        var states = campaign.StatesOf(nation);
        if (states.Length < 2) return null;      // a country of one region cannot lose a region
        ScenarioState angriest = null;
        var highestScore = -1f;
        foreach (var state in states)
        {
            // Not the capital - a "Republic of Moscow" leaving Russia is not a union coming apart -
            // and not a region just taken back from a crushed breakaway (SecessionCore.CrushedPressure).
            if (state.MapIndex == nation.CapitalStateMapIndex || state.SecessionPressure < 0f) continue;
            // Nor ground whose people have a living state of their own: Ukrainians on land Russia
            // took are Ukraine's lost land, not a "Ukrainian Republic" breaking from the union.
            var people = EconomySystem.Cultures(state).FirstOrDefault().Culture;
            // The union's own peoples are not someone else's lost land, whoever else is of them.
            if (!string.IsNullOrEmpty(people) && !DemographyCatalog.IsPrimary(nation.Tag, people) &&
                SecessionEvents.KinNear(campaign, nation, people, new[] { state })) continue;
            var score = state.Radicalism;
            if (campaign.ProvinceMap is not null)
            {
                foreach (var candidate in campaign.Nations)
                {
                    if (candidate.Movement is { Landless: true } &&
                        ((campaign.Homelands.TryGetValue(candidate.Id, out var h) && h.Contains(state.MapIndex)) ||
                         candidate.Movement.HeadquartersStateMapIndex == state.MapIndex))
                    {
                        score += 0.25f;
                        break;
                    }
                }
            }
            if (angriest is null || score > highestScore)
            {
                angriest = state;
                highestScore = score;
            }
        }
        return angriest;
    }

    private static bool LoseTheAngriestRegion(Campaign campaign, Nation nation)
    {
        var angriest = AngriestRegion(campaign, nation);
        if (angriest is null) return false;
        // Named for what it is: a region leaving a union that cannot hold it is a federal break, and
        // the reports the player reads already know that word (SecessionCause.Federal).
        return SecessionEvents.Force(campaign, angriest, null, SecessionCause.Federal) is not null;
    }

    /// <summary>
    /// What a union's state of mind does to the country, through the ordinary modifier sources: a
    /// union in trouble costs stability and pays radicalism, one that holds gives a little back.
    /// </summary>
    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        if (nation is null || string.IsNullOrEmpty(nation.FormedNationId)) yield break;
        var cohesion = nation.UnionCohesion;
        // The dictionary is the country's own, kept and refilled: this is asked once a day
        // for every formed country, and a fresh dictionary a day was the collector's budget.
        var modifiers = nation.UnionCohesionModifiers ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        modifiers.Clear();
        if (cohesion >= 0.6f)
        {
            // A country that believes it is one is easier to govern than the same country arguing.
            modifiers["stability_weekly"] = 0.0025f + cohesion * 0.0025f;
            yield return (Caption, modifiers);
            yield break;
        }
        var trouble = 0.6f - cohesion;
        modifiers["stability_weekly"] = -trouble * 0.03f;
        modifiers["radicalism_weekly"] = trouble * 0.035f;
        yield return (Caption, modifiers);
    }
}

public sealed partial class Nation
{
    /// <summary>What the union's cohesion adds, kept between asks. Not saved; refilled from scratch.</summary>
    internal Dictionary<string, float> UnionCohesionModifiers;
}
