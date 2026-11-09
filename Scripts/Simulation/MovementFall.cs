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
    /// <summary>
    /// Added for a country a human plays. Nought: a player's government goes abroad on the
    /// same chances as anybody's (the rule of parity), and a country that does not still
    /// leaves the player its movements and its exile's comeback to play.
    /// </summary>
    public float MovementExilePlayer { get; set; } = 0f;
    /// <summary>Support for a government in exile among its own people in its homeland, when it goes.</summary>
    public float MovementExileSupport { get; set; } = 0.45f;
    /// <summary>Cadres per division it had in the field, which go abroad with it rather than into captivity.</summary>
    public float MovementExileCadresPerDivision { get; set; } = 2500f;
    /// <summary>A movement with nowhere to stand for this many weeks is no more.</summary>
    public int MovementDissolveHomelessWeeks { get; set; } = 12;
    /// <summary>A landless movement with fewer cadres than this, no money and no following is no more.</summary>
    public float MovementDissolveCadres { get; set; } = 300f;
    /// <summary>
    /// A movement (not a government in exile) beaten down this far - cadres under this, a following
    /// under MovementCrushedFollowing - is broken: leaders in prison, the rank and file gone home.
    /// </summary>
    public float MovementCrushedCadres { get; set; } = 1500f;
    public float MovementCrushedFollowing { get; set; } = 0.06f;
    /// <summary>A peaceful movement this marginal and this tame folds into ordinary politics.</summary>
    public float MovementFoldFollowing { get; set; } = 0.04f;
    /// <summary>
    /// How settled under its new rulers every region of a government in exile's homeland must be
    /// (ScenarioState.BorderHabit, 0..1) before, with its following gone, it fades into history.
    /// 0.9 is about 28 years at BorderHabitYears 12. Above 1 keeps every exile for ever.
    /// </summary>
    public float MovementExileForgottenHabit { get; set; } = 0.9f;
    public float MovementFoldMilitancy { get; set; } = 0.08f;
    /// <summary>
    /// The chance a beaten rising or breakaway goes underground rather than ending, before what
    /// raises it: the radicalism of its homeland (MovementUndergroundRadicalism per unit of it,
    /// weighted by people) and a friend abroad who would shelter it.
    /// </summary>
    public float MovementUndergroundBase { get; set; } = 0.25f;
    public float MovementUndergroundRadicalism { get; set; } = 0.9f;
    public float MovementUndergroundSanctuary { get; set; } = 0.15f;
    /// <summary>Its following at home when it goes: this much, plus as much again as the region is radical.</summary>
    public float MovementUndergroundSupport { get; set; } = 0.2f;
    /// <summary>Where its methods start: well into armed struggle.</summary>
    public float MovementUndergroundMilitancy { get; set; } = 0.72f;
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
            // A map artefact (common/country_merges) is not a people's state: no homeland to return to.
            if (CountryMerges.IsMerged(owner.Tag)) continue;
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
            // A country that never held ground of its own was a line on a map, not a state: the
            // Siachen Glacier and Baikonur are drawn as countries by the map source and go to
            // whoever governs them on the first day. It has no government to send abroad.
            // So is an entry of the merge table, whatever was remembered for it: no exile, no
            // underground, nothing for a later rising to revive.
            if (!campaign.Homelands.TryGetValue(nation.Id, out var held) || held.Count == 0 || CountryMerges.IsMerged(nation.Tag))
            {
                nation.Population = 0;
                if (CountryMerges.IsMerged(nation.Tag)) campaign.Homelands.Remove(nation.Id);
                continue;
            }
            var chance = ExileChance(campaign, nation, out var refuge);
            if (refuge is not null && MovementRules.Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, 91) < chance)
            {
                GoIntoExile(campaign, nation, refuge);
                continue;
            }
            if (Insurgent(nation) &&
                MovementRules.Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, 92) < UndergroundChance(campaign, nation, refuge))
                GoUnderground(campaign, nation);
        }
    }

    /// <summary>
    /// A war is over when one side has nothing left to fight from. A leader with no region of
    /// its own and no ally holding any - a rising whose last town has been taken, a country
    /// overrun - surrenders that week, on the map as it stands, and what is left of its army
    /// lays down its arms. Before this the war went on with nobody on the other side: the
    /// state that had taken back every province of its breakaway kept a war open against a
    /// country without an inch of ground, and in the end signed a peace with it.
    /// A side whose allies still hold ground fights on: a government overrun in a coalition
    /// war is liberated or not by the coalition, as Norway and the Netherlands were.
    /// </summary>
    public static void EndLostWars(Campaign campaign)
    {
        for (var index = 0; index < campaign.Wars.Count; index++)
        {
            var war = campaign.Wars[index];
            if (war.Ended) continue;
            foreach (var attackers in new[] { true, false })
            {
                var leader = campaign.NationById(attackers ? war.AttackerId : war.DefenderId);
                var victor = campaign.NationById(attackers ? war.DefenderId : war.AttackerId);
                if (leader is null || victor is null || campaign.StatesOf(leader).Length > 0) continue;
                // A movement that never had ground fights from among its people; this is for
                // the country that lost what it had - or the government it sent abroad.
                if (leader.Movement is not null && !campaign.Fallen.Contains(leader.Id)) continue;
                var standing = false;
                foreach (var ally in war.Side(attackers))
                    if (ally != leader.Id && campaign.NationById(ally) is { } friend && campaign.StatesOf(friend).Length > 0)
                    {
                        standing = true;
                        break;
                    }
                if (standing) continue;
                PeaceNegotiation.Resolve(campaign, war, leader.Id, PeaceTerms.Capitulate);
                if (!war.Ended) continue;
                campaign.Chronicle.Record(campaign, "peace_signed", victor.Id, leader.Id);
                foreach (var army in campaign.Armies.Where(army => army.NationId == leader.Id).ToList())
                {
                    WarLosses.Surrender(campaign, army);
                    Attrition.Destroy(campaign, army, "laid down its arms: its country holds no ground");
                }
                campaign.MarkStrategicChange();
                break;
            }
        }
    }

    /// <summary>A country that was born fighting: a rising, a breakaway, a movement that took ground.</summary>
    private static bool Insurgent(Nation nation) =>
        nation.SecededFromId >= 0 || !string.IsNullOrEmpty(nation.FoundedInRevolt) ||
        nation.Name.StartsWith("rising:", StringComparison.Ordinal);

    /// <summary>
    /// How likely a beaten rising is to go underground: the Taliban of 2001 rather than the
    /// Confederacy of 1865. A furious homeland hides its fighters and a friend abroad shelters
    /// their leaders; a calm one hands them over. The roll against it is what makes one
    /// insurgency vanish and the next come back twenty years later.
    /// </summary>
    public static float UndergroundChance(Campaign campaign, Nation nation, ScenarioState sanctuary)
    {
        var defines = campaign.Defines;
        double people = 0, radical = 0;
        if (campaign.Homelands.TryGetValue(nation.Id, out var homeland))
            foreach (var index in homeland)
                if (campaign.States.Find(region => region.MapIndex == index) is { Population: > 0 } region)
                {
                    people += region.Population;
                    radical += region.Population * (double)M.Clamp01(region.Radicalism);
                }
        if (people <= 0) return 0f;
        var chance = defines.MovementUndergroundBase + defines.MovementUndergroundRadicalism * (float)(radical / people);
        if (sanctuary is not null) chance += defines.MovementUndergroundSanctuary;
        return M.Clamp(chance, 0f, 0.95f);
    }

    /// <summary>
    /// A beaten rising goes to ground in its own homeland: a movement of its own peoples with
    /// its headquarters in the region that hates the victor most, its fighters hidden among
    /// the people as cadres, a following as deep as the region is radical, and its methods well
    /// into armed struggle. Nothing about it is a country any more; everything about it can
    /// rise again, once the victor's hold on the ground has stopped being new (RevoltRecovery).
    /// </summary>
    /// <summary>This session's count of risings that went underground, for the bench. Not campaign state.</summary>
    public static int WentUnderground;

    public static void GoUnderground(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;
        if (!campaign.Homelands.TryGetValue(nation.Id, out var homeland) || homeland.Count == 0) return;
        var regions = homeland.Select(index => campaign.States.Find(region => region.MapIndex == index))
            .Where(region => region is { Population: > 0 }).ToList();
        if (regions.Count == 0) return;
        var seat = regions.OrderByDescending(region => region.Radicalism).ThenByDescending(region => region.Population)
            .ThenBy(region => region.MapIndex).First();
        var state = new MovementState
        {
            Id = MovementCatalog.UndergroundPrefix + nation.Tag.ToLowerInvariant(),
            Exile = true,
            Landless = true,
            HeadquartersStateMapIndex = seat.MapIndex,
            Legitimacy = MovementCatalog.Of(MovementCatalog.UndergroundTemplate)?.Legitimacy ?? 0.2f,
            Militancy = defines.MovementUndergroundMilitancy
        };
        // Its peoples: the ones it was founded for, else whoever is most of its homeland.
        var peoples = nation.FoundedCultures is { Count: > 0 }
            ? nation.FoundedCultures.ToList()
            : regions.SelectMany(region => region.PopulationGroups)
                .GroupBy(group => group.Culture, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Culture: group.Key, Size: group.Sum(each => each.Size)))
                .OrderByDescending(group => group.Size).ThenBy(group => group.Culture, StringComparer.Ordinal)
                .Take(2).Where(group => group.Culture.Length > 0).Select(group => group.Culture).ToList();
        state.Peoples.AddRange(peoples.Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var region in regions)
        {
            state.Homeland.Add(region.Id);
            state.Support[region.MapIndex] = M.Clamp(defines.MovementUndergroundSupport + region.Radicalism, 0f, 0.85f);
        }

        // The fighters do not surrender; they go home with their rifles.
        var divisions = 0f;
        foreach (var army in campaign.Armies.Where(army => army.NationId == nation.Id).ToList())
        {
            divisions += army.Divisions * M.Clamp01(army.Strength);
            Attrition.Destroy(campaign, army, "went underground", lost: false);
        }
        state.Cadres = MathF.Max(1500f, divisions * defines.MovementExileCadresPerDivision + nation.ManpowerReserve * 0.03f);
        state.Infiltrated[seat.MapIndex] = state.Cadres * 0.3f;

        nation.Movement = state;
        nation.Treasury *= 0.3f;
        nation.Population = 0;
        nation.ManpowerReserve = state.Cadres;
        if (state.Peoples.Count > 0 && nation.FoundedCultures.Count == 0) nation.FoundedCultures.AddRange(state.Peoples);
        state.LastReport = Loc.Format("Beaten in the field, it has gone underground in {0}.", seat.DisplayName);
        WentUnderground++;
        campaign.Chronicle.Record(campaign, "went_underground", nation.Id,
            MovementRules.OwnerOf(campaign, seat)?.Id ?? -1, place: seat.DisplayName);
        Movements.Place(campaign);
        campaign.MarkStrategicChange();
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

        // A government of no country: a save from before Decide knew the difference sent the
        // Siachen Glacier's into exile in Illinois. Nobody followed it, because nobody lived there.
        if (state.Exile && state.Homeland.Count == 0)
        {
            Dissolve(campaign, nation, Loc.Get("Nobody is left to carry it on: the movement is no more."));
            return;
        }

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
        // The ways movements really end. A government in exile lives on its legitimacy and
        // outlasts all of this; an organisation of people at home does not - but not in its
        // first year: a movement is judged on what it does, not on the day it was written down.
        else if (!state.Exile && Matured(campaign, state) && state.Cadres < defines.MovementCrushedCadres &&
                 following < defines.MovementCrushedFollowing && !campaign.Armies.Exists(a => a.NationId == nation.Id))
            Dissolve(campaign, nation, Loc.Get("Its leaders are in prison or dead, and the rank and file have gone home: the movement is broken."));
        else if (!state.Exile && Matured(campaign, state) && state.Militancy >= 0f && state.Militancy < defines.MovementFoldMilitancy &&
                 following < defines.MovementFoldFollowing && !campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id)))
            Dissolve(campaign, nation, Loc.Get("What is left of it has joined ordinary politics: the movement has folded into the parties."));
        // A government in exile outlives everything but its people's forgetting: a generation
        // grown up at home under other flags, and almost nobody there still following it.
        else if (state.Exile && Matured(campaign, state) && following < defines.MovementFoldFollowing &&
                 HomelandSettled(campaign, state, defines.MovementExileForgottenHabit) &&
                 !campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id)))
            Dissolve(campaign, nation, Loc.Get("A generation has grown up at home under other flags: the government in exile is a memory now."));
    }

    private static bool HomelandSettled(Campaign campaign, MovementState state, float habit)
    {
        if (habit > 1f || state.Homeland.Count == 0) return false;
        foreach (var id in state.Homeland)
            if (Movements.StateById(campaign, id) is not { } region || region.BorderHabit < habit) return false;
        return true;
    }

    /// <summary>A year since it was founded, or since the world began for one that was there already.</summary>
    private static bool Matured(Campaign campaign, MovementState state) =>
        campaign.ElapsedDays - Math.Max(0, state.ReadyOnDay.GetValueOrDefault("lifecycle_born")) >= 365;

    /// <summary>The end of it.</summary>
    public static void Dissolve(Campaign campaign, Nation nation, string why, bool record = true)
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
        if (record) campaign.Chronicle.Record(campaign, "movement_dissolved", nation.Id);
    }
}
