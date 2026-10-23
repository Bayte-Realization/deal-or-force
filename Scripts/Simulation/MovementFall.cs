using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The chance a fallen country's government goes into exile rather than simply ending, before what raises it.</summary>
    public float MovementExileBase { get; set; } = 0.2f;
    /// <summary>Added for a country a friend will take in (opinion above 30, not at war with its conqueror).</summary>
    public float MovementExileFriend { get; set; } = 0.25f;
    /// <summary>Added per unit of the country's stability when it fell, and once for a country of more than five million.</summary>
    public float MovementExileStability { get; set; } = 0.25f;
    public float MovementExileLarge { get; set; } = 0.15f;
    /// <summary>Added for a country a human plays: losing the war should not end the game as well.</summary>
    public float MovementExilePlayer { get; set; } = 0.6f;
    /// <summary>Support for a government in exile among its own people in its homeland, when it goes.</summary>
    public float MovementExileSupport { get; set; } = 0.45f;
    /// <summary>Cadres per division it had in the field, which go abroad with it rather than into captivity.</summary>
    public float MovementExileCadresPerDivision { get; set; } = 2500f;
    /// <summary>A movement with nowhere to stand for this many weeks is no more.</summary>
    public int MovementDissolveHomelessWeeks { get; set; } = 12;
    /// <summary>A landless movement with fewer cadres than this, no money and no following is no more.</summary>
    public float MovementDissolveCadres { get; set; } = 300f;
}

/// <summary>
/// The two ends of a movement's life that are not an uprising: a country that loses the last of
/// its ground and carries on as a government in exile - or does not, and is gone - and a
/// movement that is ground down until there is nothing left of it.
///
/// A fall is decided once, the week the country is found with no ground. Whether it goes into
/// exile is a draw against a chance that a friend abroad, a settled country and a large one all
/// raise, and a human player's country nearly always makes: the war being lost should not also
/// be the game being over. The exile is a movement made from the `government_in_exile` template:
/// its own peoples, a homeland of the regions it held when it was first seen, a headquarters in
/// the friendliest country that will have it, the respect a government has, the recognition of
/// everybody who thought well of it, and cadres out of the army it had.
/// </summary>
public static class MovementFall
{
    /// <summary>The ground each country holds the first time it is seen holding any.</summary>
    internal static void RememberHomelands(Campaign campaign)
    {
        // Almost always everybody is known already, and then there is nothing to walk.
        var anyone = false;
        foreach (var nation in campaign.Nations)
            if (nation.Movement is null && !campaign.Homelands.ContainsKey(nation.Id) && campaign.StatesOf(nation).Length > 0)
            {
                anyone = true;
                break;
            }
        if (!anyone) return;
        Dictionary<int, List<int>> held = null;
        foreach (var region in campaign.States)
        {
            var owner = MovementRules.OwnerOf(campaign, region);
            if (owner is null || owner.Movement is not null || campaign.Homelands.ContainsKey(owner.Id)) continue;
            held ??= new Dictionary<int, List<int>>();
            if (!held.TryGetValue(owner.Id, out var list)) held[owner.Id] = list = new List<int>();
            list.Add(region.MapIndex);
        }
        if (held is null) return;
        foreach (var (id, regions) in held) campaign.Homelands[id] = regions;
    }

    /// <summary>Every country found with no ground this week has its fall decided, once.</summary>
    internal static void Decide(Campaign campaign)
    {
        // Nothing in this walk founds or removes a nation, so a pinned-count index walk is
        // the list walk without the copy of it.
        for (var id = 0; id < campaign.Nations.Count; id++)
        {
            var nation = campaign.Nations[id];
            if (nation.Movement is not null || campaign.Fallen.Contains(nation.Id)) continue;
            // A real country (people, a tag, a founding) holding no ground at the weekly
            // review is a ghost: every founding path seeds homelands now, so a missing
            // entry can no longer exempt a stateless straggler from the fall decision.
            if ((nation.Population <= 0 && (!campaign.Homelands.ContainsKey(nation.Id) || nation.CollapseAnnounced)) ||
                campaign.StatesOf(nation).Length > 0) continue;
            campaign.Fallen.Add(nation.Id);
            var chance = ExileChance(campaign, nation, out var refuge);
            if (refuge is null) continue;
            if (MovementRules.Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, 91) >= chance) continue;
            GoIntoExile(campaign, nation, refuge);
        }
    }

    /// <summary>How likely this country's government is to carry on abroad, and where it would go.</summary>
    public static float ExileChance(Campaign campaign, Nation nation, out ScenarioState refuge)
    {
        var defines = campaign.Defines;
        var found = Refuge(campaign, nation);
        refuge = found;
        if (found is null) return 0f;
        var host = campaign.Nations.Find(candidate => candidate.MapIndex == found.NationMapIndex);
        var chance = defines.MovementExileBase + defines.MovementExileStability * nation.Stability;
        if (host is not null && CampaignDiplomacy.Get(campaign, host.Id, nation.Id).Opinion >= 30f) chance += defines.MovementExileFriend;
        if (campaign.Homelands.TryGetValue(nation.Id, out var homeland) &&
            homeland.Sum(index => campaign.States.Find(region => region.MapIndex == index)?.Population ?? 0L) > 5_000_000L)
            chance += defines.MovementExileLarge;
        if (nation.IsHuman) chance += defines.MovementExilePlayer;
        return M.Clamp01(chance);
    }

    /// <summary>The countries that took its ground, and anybody allied to one of them.</summary>
    private static HashSet<int> ConquerorsOf(Campaign campaign, Nation nation)
    {
        var conquerors = new HashSet<int>();
        if (campaign.Homelands.TryGetValue(nation.Id, out var homeland))
            foreach (var index in homeland)
                if (campaign.States.Find(region => region.MapIndex == index) is { } region &&
                    MovementRules.OwnerOf(campaign, region) is { } owner && owner.Id != nation.Id)
                    conquerors.Add(owner.Id);
        foreach (var candidate in campaign.Nations)
            foreach (var conqueror in conquerors.ToList())
                if (candidate.Id != conqueror && CampaignDiplomacy.Get(campaign, candidate.Id, conqueror)?.Stance == DiplomaticStance.Allied)
                    conquerors.Add(candidate.Id);
        return conquerors;
    }

    /// <summary>
    /// How much a country is a friend to this one - what a government in exile or a movement looks
    /// for in a host, and what makes a government recognise one: what it thinks of it, whether they
    /// are allies or friends, whether it has promised to defend it, how alike their politics are.
    /// Opinion alone is nought between most countries on the first day, which is how a Ukrainian
    /// government in exile ended up in Java.
    /// </summary>
    public static float Warmth(Campaign campaign, Nation host, Nation guest, IEnumerable<int> against = null)
    {
        var relation = CampaignDiplomacy.Get(campaign, host.Id, guest.Id);
        if (relation is null) return 0f;
        // And the enemy of its enemy: a rival of whoever took its ground, or holds what it claims.
        var enmity = 0f;
        if (against is not null)
            foreach (var enemyId in against)
            {
                if (enemyId == host.Id) return -200f;
                var toEnemy = CampaignDiplomacy.Get(campaign, host.Id, enemyId);
                if (toEnemy is null) continue;
                // The enemy's own ally never takes them in, and its friends think twice.
                if (toEnemy.Stance == DiplomaticStance.Allied) return -200f;
                var felt = toEnemy.Stance switch
                {
                    DiplomaticStance.Rival or DiplomaticStance.AtWar => 35f,
                    DiplomaticStance.Friendly => -30f,
                    _ => 0f
                };
                felt -= toEnemy.Opinion * 0.4f;
                // And politics far from the enemy's - or close to it: nobody has to have quarrelled
                // yet on the first day for a democracy to take in the government a dictatorship threw out.
                felt -= PoliticsCore.Affinity(campaign, host.Id, enemyId) * 0.6f;
                enmity += felt;
            }
        var warmth = relation.Opinion + PoliticsCore.Affinity(campaign, host.Id, guest.Id) * 0.5f;
        warmth += relation.Stance switch
        {
            DiplomaticStance.Allied => 60f,
            DiplomaticStance.Friendly => 30f,
            DiplomaticStance.Rival => -40f,
            DiplomaticStance.AtWar => -200f,
            _ => 0f
        };
        if (relation.GuaranteeByA || relation.GuaranteeByB) warmth += 30f;
        if (relation.TradeAgreement) warmth += 8f;
        if (CampaignDiplomacy.SharesLandBorder(campaign, host.MapIndex, guest.MapIndex)) warmth += 6f;
        // And nearness: a government that has to flee goes next door or to its own part of the
        // world far more often than across it - London and Warsaw, not Havana.
        var apart = NavalOperations.Distance(host.CapitalLatitude, host.CapitalLongitude, guest.CapitalLatitude, guest.CapitalLongitude);
        warmth += 30f * MathF.Exp(-apart / 15f);
        return warmth + enmity;
    }

    /// <summary>The friendliest country not at war with it and not one of those that took its ground, and a city there.</summary>
    private static ScenarioState Refuge(Campaign campaign, Nation nation)
    {
        var conquerors = ConquerorsOf(campaign, nation);
        Nation best = null;
        var bestWarmth = float.MinValue;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Id == nation.Id || candidate.Movement is not null || conquerors.Contains(candidate.Id)) continue;
            if (CampaignDiplomacy.AtWar(campaign, candidate.Id, nation.Id)) continue;
            if (campaign.StatesOf(candidate).Length == 0) continue;
            // A bigger country has more room for a government that is not its own.
            // A bigger country has room for a government that is not its own; a sliver of one has not.
            var warmth = Warmth(campaign, candidate, nation, conquerors) + MathF.Min(20f, MathF.Sqrt(MathF.Max(0f, candidate.Gdp)) / 3f);
            if (warmth <= bestWarmth) continue;
            bestWarmth = warmth;
            best = candidate;
        }
        return best is null ? null : SeatRegion(campaign, best, nation);
    }

    /// <summary>
    /// Which of a host's regions a headquarters goes to: one of its five biggest - where the offices,
    /// the embassies and the airports are - and likelier the more of the guest's own people live
    /// there, chosen once per guest and host so it does not wander between loads.
    /// </summary>
    public static ScenarioState SeatRegion(Campaign campaign, Nation host, Nation guest)
    {
        var regions = campaign.StatesOf(host).OrderByDescending(region => region.Population).Take(5).ToList();
        if (regions.Count == 0) return null;
        var peoples = Movements.DefinitionOf(guest) is { } definition
            ? definition.Peoples
            : guest.FoundedCultures ?? new List<string>();
        var weights = new List<float>();
        foreach (var region in regions)
        {
            var weight = MathF.Sqrt(MathF.Max(1f, region.Population));
            foreach (var (culture, size) in EconomySystem.Cultures(region))
                if (MovementDefinition.Listed(peoples, culture) && region.Population > 0)
                    weight *= 1f + 4f * size / (float)region.Population;
            weights.Add(weight);
        }
        var roll = MovementRules.Roll(campaign.Seed, guest.Id, host.Id, 5) * weights.Sum();
        for (var index = 0; index < regions.Count; index++)
        {
            roll -= weights[index];
            if (roll <= 0f) return regions[index];
        }
        return regions[^1];
    }

    /// <summary>The government leaves and carries on: the country becomes a movement.</summary>
    internal static void GoIntoExile(Campaign campaign, Nation nation, ScenarioState refuge)
    {
        var defines = campaign.Defines;
        var state = new MovementState
        {
            Id = "exile_" + nation.Tag.ToLowerInvariant(),
            Exile = true,
            Landless = true,
            HeadquartersStateMapIndex = refuge.MapIndex,
            Legitimacy = MovementCatalog.Of(MovementCatalog.ExileTemplate)?.Legitimacy ?? 0.5f
        };
        var peoples = nation.FoundedCultures is { Count: > 0 }
            ? nation.FoundedCultures
            : (IEnumerable<string>)DemographyCatalog.PrimarySet(nation.Tag) ?? Array.Empty<string>();
        state.Peoples.AddRange(peoples.Distinct(StringComparer.OrdinalIgnoreCase));
        if (campaign.Homelands.TryGetValue(nation.Id, out var homeland))
            foreach (var index in homeland)
                if (campaign.States.Find(region => region.MapIndex == index) is { } region)
                {
                    state.Homeland.Add(region.Id);
                    state.Support[region.MapIndex] = defines.MovementExileSupport;
                }

        // What is left of the army goes abroad as cadres, not into captivity.
        var divisions = 0f;
        foreach (var army in campaign.Armies.Where(army => army.NationId == nation.Id).ToList())
        {
            divisions += army.Divisions;
            Attrition.Destroy(campaign, army, "went into exile", lost: false);
        }
        state.Cadres = MathF.Max(2000f, divisions * defines.MovementExileCadresPerDivision + nation.ManpowerReserve * 0.02f);

        // Its friends go on recognising it; those who took its ground, and their allies, do not.
        // Everybody warm enough, and its five warmest friends in any case: a government in exile
        // is recognised at least by the countries nearest to it in every sense.
        var conquerors = ConquerorsOf(campaign, nation);
        var friends = new List<(Nation Other, float Warmth)>();
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id || other.Movement is not null || campaign.StatesOf(other).Length == 0) continue;
            if (conquerors.Contains(other.Id)) continue;
            var warmth = Warmth(campaign, other, nation, conquerors);
            if (warmth > 0f) friends.Add((other, warmth));
        }
        friends.Sort((left, right) => right.Warmth.CompareTo(left.Warmth));
        for (var index = 0; index < friends.Count; index++)
            if (index < 5 || friends[index].Warmth >= 20f) state.RecognisedBy.Add(friends[index].Other.Id);

        nation.Movement = state;
        nation.Treasury *= 0.5f;
        nation.Population = 0;
        nation.ManpowerReserve = state.Cadres;
        if (state.Peoples.Count > 0 && nation.FoundedCultures.Count == 0) nation.FoundedCultures.AddRange(state.Peoples);
        state.LastReport = Loc.Format("The government has left the country and carries on from {0}.", refuge.DisplayName);
        Movements.Place(campaign);
        campaign.MarkStrategicChange();
    }

    /// <summary>Whether a landless movement has come to its end this week, and ends it if so.</summary>
    internal static void Endure(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        if (state is null || state.Dissolved || !state.Landless) return;
        var defines = campaign.Defines;

        // Nowhere to stand: every week it looks for somewhere, and in the end it gives up.
        var host = Movements.HostOf(campaign, nation);
        if (host is null || host.Movement is not null || CampaignDiplomacy.AtWar(campaign, host.Id, nation.Id))
        {
            var refuge = MovementRules.BestRefuge(campaign, nation, host);
            if (refuge is not null && CampaignDiplomacy.Get(campaign,
                    campaign.Nations.Find(candidate => candidate.MapIndex == refuge.NationMapIndex)?.Id ?? -1, nation.Id).Opinion > -25f)
            {
                state.HeadquartersStateMapIndex = refuge.MapIndex;
                state.HomelessWeeks = 0;
                Movements.Place(campaign);
                state.LastReport = Loc.Format("The headquarters has found a home in {0}.", refuge.DisplayName);
            }
            else state.HomelessWeeks++;
        }
        else state.HomelessWeeks = 0;

        var following = state.Support.Count == 0 ? 0f : state.Support.Values.Max();
        if (state.HomelessWeeks >= defines.MovementDissolveHomelessWeeks)
            Dissolve(campaign, nation, Loc.Get("With nowhere left that would have it, the movement has broken up."));
        else if (state.Cadres < defines.MovementDissolveCadres && nation.Treasury < 0.002f && following < 0.05f)
            Dissolve(campaign, nation, Loc.Get("Nobody is left to carry it on: the movement is no more."));
    }

    /// <summary>The end of it.</summary>
    public static void Dissolve(Campaign campaign, Nation nation, string why)
    {
        var state = nation.Movement;
        if (state is null || state.Dissolved) return;
        state.Dissolved = true;
        state.ReadyOnDay["lifecycle_rebirth"] = campaign.ElapsedDays + Math.Max(30, campaign.Defines.MovementRebirthDays);
        state.Support.Clear();
        state.Infiltrated.Clear();
        state.OnTheWay.Clear();
        state.SponsoredBy.Clear();
        state.RecognisedBy.Clear();
        state.InvitedBy.Clear();
        state.MovingToStateMapIndex = -1;
        state.HeadquartersStateMapIndex = -1;
        state.Cadres = 0f;
        nation.ManpowerReserve = 0f;
        state.LastReport = why;
        foreach (var army in campaign.Armies.Where(army => army.NationId == nation.Id).ToList())
            Attrition.Destroy(campaign, army, "movement dissolved", lost: false);
        campaign.MovementRevision++;
        campaign.MarkStrategicChange();
        campaign.Chronicle.Record(campaign, "movement_dissolved", nation.Id);
    }
}
