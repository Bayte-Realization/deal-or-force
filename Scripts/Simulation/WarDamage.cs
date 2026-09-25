using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class ScenarioState
{
    /// <summary>
    /// How much of the region the fighting has wrecked, from nothing to all of it. Every day of
    /// battle in one of its provinces adds to it, and it comes back down week by week as the
    /// region is rebuilt. While it lasts the region makes less, lives worse, and people leave it.
    /// See <see cref="WarDamage"/>.
    /// </summary>
    public float WarDamage;

    /// <summary>The last day this region's reconstruction is paid for; -1 for none (<see cref="RebuildRegionCommand"/>).</summary>
    public int RebuildingUntilDay = -1;

    /// <summary>The last day a battle was fought in one of the region's provinces.</summary>
    public int LastFoughtDay = -9999;

    /// <summary>
    /// People who fled this region for the rest of their country, by people (the culture id),
    /// waiting to come home once it is rebuilt. Those who went abroad are not counted: they
    /// live in the neighbours' regions now.
    /// </summary>
    public readonly Dictionary<string, long> Displaced = new(StringComparer.Ordinal);

    /// <summary>The map index of the country they fled into - the region's owner when they left.</summary>
    public int DisplacedOwner;
}

public sealed partial class Defines
{
    /// <summary>
    /// What one day of an ordinary battle does to the region it is fought in, before it is
    /// shared over the region's provinces: a battle in one of Donetsk's provinces wrecks a
    /// share of Donetsk, and the same battle in one of Siberia's does not wreck Siberia.
    /// </summary>
    public float WarDamagePerBattleDay { get; set; } = 0.03f;

    /// <summary>
    /// The day's casualties, both sides together, that make an ordinary battle. A day with
    /// three times as many dead counts three times, and a skirmish a quarter; no further.
    /// </summary>
    public float WarDamageBattleLosses { get; set; } = 1500f;

    /// <summary>The share of the damage rebuilt in a week, before infrastructure and modifiers.</summary>
    public float WarDamageRecoveryPerWeek { get; set; } = 0.012f;

    /// <summary>How much faster each level of infrastructure in the region rebuilds it.</summary>
    public float WarDamageRecoveryPerInfrastructure { get; set; } = 0.25f;

    /// <summary>The share of the region's output a wholly wrecked region loses.</summary>
    public float WarDamageOutputLoss { get; set; } = 0.5f;

    /// <summary>Standard of living a wholly wrecked region loses in a week.</summary>
    public float WarDamageLivingPerWeek { get; set; } = 0.003f;

    /// <summary>The share of a wholly wrecked region's people who leave it in a week.</summary>
    public float WarDamageFleePerWeek { get; set; } = 0.004f;

    /// <summary>
    /// The share of those who leave who settle in the rest of their own country; the others go
    /// abroad. Nobody settles in a region as wrecked as <see cref="WarDamageSafeBelow"/> or more.
    /// </summary>
    public float WarDamageRefugeesStayHome { get; set; } = 0.75f;

    public float WarDamageSafeBelow { get; set; } = 0.10f;

    /// <summary>
    /// The share of a region's displaced who come home in a week, once it is damaged less than
    /// <see cref="WarDamageSafeBelow"/> and no battle has been fought in it for
    /// <see cref="WarDamageSafeDays"/>.
    /// </summary>
    public float WarDamageReturnPerWeek { get; set; } = 0.04f;

    /// <summary>
    /// A paid reconstruction of one region (<see cref="RebuildRegionCommand"/>): how long it
    /// lasts, and how many times faster than on its own the region is rebuilt meanwhile, when
    /// the country's market has all the industrial materials it wants - less when it has less.
    /// </summary>
    public int WarDamageRebuildDays { get; set; } = 180;

    public float WarDamageRebuildBoost { get; set; } = 2f;

    /// <summary>
    /// What it costs: this share of the region's part of the country's GDP (by its people),
    /// times the damage. Rebuilding a tenth of a country wholly in ruins costs a fortieth of GDP.
    /// </summary>
    public float WarDamageRebuildCost { get; set; } = 0.25f;

    /// <summary>Days without a battle in the region before anybody will rebuild it.</summary>
    public int WarDamageSafeDays { get; set; } = 14;

    /// <summary>Damage below which there is nothing worth paying to rebuild.</summary>
    public float WarDamageRebuildMinimum { get; set; } = 0.05f;

    /// <summary>
    /// A country nobody plays rebuilds its worst-hit region when it is this damaged and the
    /// treasury holds this many times the cost.
    /// </summary>
    public float WarDamageAiRebuildAbove { get; set; } = 0.20f;

    public float WarDamageAiRebuildReserve { get; set; } = 4f;
}

/// <summary>
/// Pay to rebuild one region: for <c>WarDamageRebuildDays</c> it comes back
/// <c>1 + WarDamageRebuildBoost</c> times as fast, as long as no battle is fought in it and in
/// proportion to the industrial materials the country's market can supply. A player orders it
/// from the region card, and a country nobody plays orders it for its worst-hit region.
/// </summary>
public sealed class RebuildRegionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        var state = campaign.States.Find(candidate => candidate.MapIndex == StateMapIndex);
        if (nation is null || state is null || WarDamage.RebuildRefusal(campaign, nation, state).Length > 0) return;
        nation.Treasury -= WarDamage.RebuildCost(campaign, nation, state);
        state.RebuildingUntilDay = campaign.ElapsedDays + Math.Max(1, campaign.Defines.WarDamageRebuildDays);
    }
}

/// <summary>
/// The ruins a war leaves. A region's <see cref="ScenarioState.WarDamage"/> climbs with every day
/// of fighting inside it - more for a bloody day, shared over the region's provinces - and
/// saturates rather than overflowing, so a region fought over for a year is mostly ruins and
/// never more than ruins.
///
/// Every economy week, for every damaged region:
/// - its farms, mines and factories make less, by <c>WarDamageOutputLoss</c> x the damage;
/// - its standard of living falls a little;
/// - a share of its people leave - most for the undamaged regions of their own country, weighted
///   by how many people already live there, the rest for the neighbours at peace - and they keep
///   who they are; those who stayed in the country come home once the region is rebuilt and safe;
/// - and it is rebuilt a little, faster with infrastructure and with the
///   <c>reconstruction_speed</c> modifier a reconstruction programme brings.
///
/// The damage is saved with the region. What is rebuilt each time the campaign loads - which
/// state a province belongs to, how many provinces each region has - is kept per campaign.
/// </summary>
public static class WarDamage
{
    private sealed class Ledger
    {
        public readonly Dictionary<int, ScenarioState> StateByMap = new();
        public int[] Provinces = Array.Empty<int>();
        public int CountedOnDay = -1;
    }

    private static readonly ConditionalWeakTable<Campaign, Ledger> Ledgers = new();

    /// <summary>The share of its usual output a region still makes: one for an untouched region.</summary>
    public static float OutputFactor(Campaign campaign, ScenarioState state) =>
        state is null || state.WarDamage <= 0f ? 1f :
            MathF.Max(0f, 1f - M.Clamp01(state.WarDamage) * campaign.Defines.WarDamageOutputLoss);

    /// <summary>
    /// A day of battle in this province: the region it is in takes its share of the damage.
    /// Called once per battle per day, from where the day's casualties are counted.
    /// </summary>
    public static void Fought(Campaign campaign, int province, float casualties)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || province <= 0 || province > topology.ProvinceCount) return;
        var ledger = LedgerOf(campaign);
        var map = topology.ProvinceToState[province];
        if (!ledger.StateByMap.TryGetValue(map, out var state) || ledger.Provinces.Length <= map ||
            ledger.Provinces[map] <= 0)
        {
            // A region split since the last count, or ground that is nobody's region. Counted
            // again at most once a day, so fighting on the latter does not recount every battle.
            if (ledger.CountedOnDay == campaign.ElapsedDays) return;
            Recount(campaign, ledger);
            if (!ledger.StateByMap.TryGetValue(map, out state) || ledger.Provinces.Length <= map) return;
        }

        var defines = campaign.Defines;
        state.LastFoughtDay = campaign.ElapsedDays;
        var intensity = M.Clamp(casualties / MathF.Max(1f, defines.WarDamageBattleLosses), 0.25f, 3f);
        var share = defines.WarDamagePerBattleDay * intensity / Math.Max(1, ledger.Provinces[map]);
        state.WarDamage = M.Clamp01(state.WarDamage + share * (1f - state.WarDamage));
    }

    /// <summary>The week's rebuilding, and what the ruins cost in the meantime. Runs on the economy's week.</summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) return;
        Week(campaign);
    }

    /// <summary>One week of it, whatever the day - the bench runs a year of rebuilding without a year of the world.</summary>
    internal static void Week(Campaign campaign)
    {
        var defines = campaign.Defines;
        var ledger = LedgerOf(campaign);
        var recounted = false;
        HashSet<int> atWar = null;
        var abroad = new List<ScenarioState>();

        foreach (var nation in campaign.Nations)
        {
            var states = campaign.StatesOf(nation);
            var anyDamage = false;
            var anyDisplaced = false;
            foreach (var state in states)
            {
                if (state.WarDamage > 0f) anyDamage = true;
                if (state.Displaced.Count > 0) anyDisplaced = true;
            }
            if (anyDisplaced)
                foreach (var state in states) Return(campaign, states, state);
            if (!anyDamage) continue;
            if (!recounted) { Recount(campaign, ledger); recounted = true; }
            if (atWar is null)
            {
                atWar = new HashSet<int>();
                foreach (var war in campaign.Wars)
                {
                    if (war.Ended) continue;
                    foreach (var other in campaign.Nations)
                        if (war.Involves(other.Id)) atWar.Add(other.Id);
                }
            }
            NeighboursAtPeace(campaign, nation, atWar, abroad);

            ScenarioState worst = null;
            foreach (var state in states)
            {
                var damage = state.WarDamage;
                if (damage <= 0f) continue;

                state.StandardOfLiving = M.Clamp01(state.StandardOfLiving - damage * defines.WarDamageLivingPerWeek);
                Flee(campaign, states, state, (long)(state.Population * damage * defines.WarDamageFleePerWeek), abroad);

                // Proportional, so a ruin is rebuilt fastest where there is most to rebuild, and
                // a last thousandth a week, so the last of it is rebuilt at all.
                damage = damage * (1f - RebuiltPerWeek(campaign, nation, state)) - 0.001f;
                state.WarDamage = damage < 0.0005f ? 0f : damage;
                if (worst is null || state.WarDamage > worst.WarDamage) worst = state;
            }

            // A country nobody plays pays to rebuild its worst ruin once it can spare the money.
            if (!nation.IsHuman && worst is not null && worst.WarDamage >= defines.WarDamageAiRebuildAbove &&
                RebuildRefusal(campaign, nation, worst).Length == 0 &&
                nation.Treasury >= RebuildCost(campaign, nation, worst) * defines.WarDamageAiRebuildReserve)
                new RebuildRegionCommand { IssuerId = nation.Id, StateMapIndex = worst.MapIndex }.Apply(campaign);
        }
    }

    /// <summary>Whether the region's paid reconstruction is running this week: paid for, and safe.</summary>
    public static bool Rebuilding(Campaign campaign, ScenarioState state) =>
        state is not null && campaign.ElapsedDays <= state.RebuildingUntilDay && Safe(campaign, state);

    /// <summary>No battle in the region for <c>WarDamageSafeDays</c>.</summary>
    public static bool Safe(Campaign campaign, ScenarioState state) =>
        campaign.ElapsedDays - state.LastFoughtDay >= campaign.Defines.WarDamageSafeDays;

    /// <summary>What rebuilding this region costs now, in billions.</summary>
    public static float RebuildCost(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (nation is null || state is null) return 0f;
        var share = nation.Population > 0 ? state.Population / (float)nation.Population : 0f;
        return campaign.Defines.WarDamageRebuildCost * M.Clamp01(state.WarDamage) * M.Clamp01(share) * nation.Gdp;
    }

    /// <summary>Why this country cannot pay to rebuild this region now; empty when it can.</summary>
    public static string RebuildRefusal(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (nation is null || state is null || state.NationMapIndex != nation.MapIndex)
            return Loc.Get("Only a region of one's own can be rebuilt.");
        if (state.WarDamage < campaign.Defines.WarDamageRebuildMinimum) return Loc.Get("There is nothing here worth rebuilding.");
        if (campaign.ElapsedDays <= state.RebuildingUntilDay)
            return Loc.Format("It is being rebuilt for {0} more days.", state.RebuildingUntilDay - campaign.ElapsedDays);
        if (!Safe(campaign, state))
            return Loc.Format("Nobody rebuilds under fire: there was a battle here {0} days ago.",
                campaign.ElapsedDays - state.LastFoughtDay);
        var cost = RebuildCost(campaign, nation, state);
        if (nation.Treasury < cost) return Loc.Format("The treasury cannot pay {0}.", Fmt.Money(cost));
        return "";
    }

    /// <summary>The share of the industrial materials the country's market wants that it has, 0 to 1.</summary>
    public static float Materials(Nation nation) =>
        M.Clamp01(nation?.Market.GetValueOrDefault(Commodity.IndustrialMaterials)?.Satisfaction ?? 1f);

    /// <summary>The worst-hit of a country's regions, for triggers and the AI.</summary>
    public static float Worst(Campaign campaign, Nation nation)
    {
        var worst = 0f;
        foreach (var state in campaign.StatesOf(nation)) worst = MathF.Max(worst, state.WarDamage);
        return worst;
    }

    /// <summary>People a week who leave this region at its present damage.</summary>
    public static long Leaving(Campaign campaign, ScenarioState state) =>
        state is null ? 0 : (long)(state.Population * M.Clamp01(state.WarDamage) * campaign.Defines.WarDamageFleePerWeek);

    /// <summary>
    /// The share of the damage rebuilt next week: the region's own pace, faster with
    /// infrastructure and the reconstruction_speed modifier, and faster again while a paid
    /// reconstruction runs - by as much of the boost as the market has materials for.
    /// </summary>
    public static float RebuiltPerWeek(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (state is null) return 0f;
        var defines = campaign.Defines;
        var infrastructure = state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);
        var paid = Rebuilding(campaign, state) ? 1f + defines.WarDamageRebuildBoost * Materials(nation) : 1f;
        return M.Clamp01(defines.WarDamageRecoveryPerWeek *
                         (1f + infrastructure * defines.WarDamageRecoveryPerInfrastructure) *
                         NationalModifiers.Factor(nation, "reconstruction_speed") * paid);
    }

    private static void Flee(Campaign campaign, ScenarioState[] states, ScenarioState from, long leaving,
        List<ScenarioState> abroad)
    {
        if (leaving <= 0 || from.Population <= 0 || from.PopulationGroups.Count == 0) return;
        leaving = Math.Min(leaving, from.Population / 2);
        var defines = campaign.Defines;

        // Where they can go at home: the country's regions the war has not reached, by how many
        // already live there - people go where there are towns, work and family. The rest go
        // abroad, to the regions of the neighbours at peace, by the same rule; with no neighbour
        // at peace they leave for somewhere further off, and the map loses them.
        var home = Destinations(states, from, defines.WarDamageSafeBelow, out var room) ? M.Clamp01(defines.WarDamageRefugeesStayHome) : 0f;
        long roomAbroad = 0;
        foreach (var state in abroad) roomAbroad += state.Population;

        // The displaced are counted by their people, to come home when the region is rebuilt -
        // and forgotten when the region has changed hands since they left.
        if (from.DisplacedOwner != from.NationMapIndex)
        {
            from.Displaced.Clear();
            from.DisplacedOwner = from.NationMapIndex;
        }

        // Every group gives its share, and keeps who it is where it arrives.
        long gone = 0;
        var groups = from.PopulationGroups;
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            if (group.Size <= 0) continue;
            var share = (long)Math.Round(leaving * (group.Size / (double)from.Population));
            share = Math.Min(share, group.Size);
            if (share <= 0) continue;
            group.Size -= share;
            gone += share;

            var settling = (long)(share * home);
            if (settling > 0)
            {
                Spread(states, from, defines.WarDamageSafeBelow, room, group, settling);
                from.Displaced[group.Culture] = from.Displaced.GetValueOrDefault(group.Culture) + settling;
            }
            if (roomAbroad > 0 && share > settling)
                Spread(abroad, null, float.PositiveInfinity, roomAbroad, group, share - settling);
        }

        from.Population = Math.Max(0, from.Population - gone);
    }

    /// <summary>Whether there is anywhere at home to go, and how many people already live there.</summary>
    private static bool Destinations(ScenarioState[] states, ScenarioState from, float safeBelow, out long room)
    {
        room = 0;
        foreach (var state in states)
            if (state != from && state.WarDamage < safeBelow && state.Population > 0)
                room += state.Population;
        return room > 0;
    }

    /// <summary>Settle these people over the regions by how many live in each; rounding goes to the biggest.</summary>
    private static void Spread(IReadOnlyList<ScenarioState> states, ScenarioState except, float safeBelow, long room,
        PopulationGroup like, long people)
    {
        long placed = 0;
        ScenarioState largest = null;
        foreach (var state in states)
        {
            if (state == except || state.WarDamage >= safeBelow || state.Population <= 0) continue;
            if (largest is null || state.Population > largest.Population) largest = state;
            var part = (long)(people * (state.Population / (double)room));
            if (part <= 0) continue;
            Arrive(state, like, part);
            placed += part;
        }
        if (people > placed && largest is not null) Arrive(largest, like, people - placed);
    }

    /// <summary>
    /// The displaced come home, a share a week, once their region is rebuilt past the ruins and
    /// safe from the fighting: people of each of its peoples are found in the rest of the country
    /// and moved back. Some are never found - they have moved on, or been counted as locals -
    /// and the count falls by the share either way, so it ends.
    /// </summary>
    private static void Return(Campaign campaign, ScenarioState[] states, ScenarioState home)
    {
        if (home.Displaced.Count == 0) return;
        var defines = campaign.Defines;
        if (home.DisplacedOwner != home.NationMapIndex) { home.Displaced.Clear(); return; }
        if (home.WarDamage >= defines.WarDamageSafeBelow || !Safe(campaign, home)) return;

        foreach (var culture in new List<string>(home.Displaced.Keys))
        {
            var waiting = home.Displaced[culture];
            var coming = Math.Min(waiting, Math.Max(1L, (long)MathF.Ceiling(waiting * defines.WarDamageReturnPerWeek)));
            long found = 0;
            foreach (var state in states)
            {
                if (state == home) continue;
                foreach (var group in state.PopulationGroups)
                    if (group.Size > 0 && string.Equals(group.Culture, culture, StringComparison.Ordinal)) found += group.Size;
            }
            // Never more than a tenth of that people elsewhere in the country in one week.
            var moving = Math.Min(coming, found / 10);
            if (moving > 0)
            {
                long moved = 0;
                foreach (var state in states)
                {
                    if (state == home || moved >= moving) continue;
                    foreach (var group in state.PopulationGroups)
                    {
                        if (group.Size <= 0 || !string.Equals(group.Culture, culture, StringComparison.Ordinal)) continue;
                        var part = Math.Min(group.Size, (long)Math.Ceiling(moving * (group.Size / (double)found)));
                        part = Math.Min(part, moving - moved);
                        if (part <= 0) continue;
                        group.Size -= part;
                        state.Population = Math.Max(0, state.Population - part);
                        Arrive(home, group, part);
                        moved += part;
                        if (moved >= moving) break;
                    }
                }
            }
            if (waiting - coming <= 0) home.Displaced.Remove(culture);
            else home.Displaced[culture] = waiting - coming;
        }
    }

    /// <summary>People of this region waiting elsewhere in the country to come home.</summary>
    public static long DisplacedTotal(ScenarioState state)
    {
        long total = 0;
        if (state is null) return 0;
        foreach (var count in state.Displaced.Values) total += count;
        return total;
    }

    /// <summary>People a week who come home to this region now: none while it is in ruins or under fire.</summary>
    public static long Returning(Campaign campaign, ScenarioState state)
    {
        if (state is null || state.Displaced.Count == 0 || state.DisplacedOwner != state.NationMapIndex ||
            state.WarDamage >= campaign.Defines.WarDamageSafeBelow || !Safe(campaign, state)) return 0;
        return (long)MathF.Ceiling(DisplacedTotal(state) * campaign.Defines.WarDamageReturnPerWeek);
    }

    /// <summary>The regions of this country's neighbours that are at no war, where its refugees abroad go.</summary>
    private static void NeighboursAtPeace(Campaign campaign, Nation nation, HashSet<int> atWar, List<ScenarioState> into)
    {
        into.Clear();
        foreach (var index in CampaignDiplomacy.Neighbours(campaign, nation.MapIndex))
        {
            var neighbour = campaign.Nations.Find(candidate => candidate.MapIndex == index);
            if (neighbour is null || atWar.Contains(neighbour.Id)) continue;
            into.AddRange(campaign.StatesOf(neighbour));
        }
    }

    private static void Arrive(ScenarioState state, PopulationGroup like, long people)
    {
        state.Population += people;
        foreach (var group in state.PopulationGroups)
        {
            if (group.Class != like.Class || group.Sector != like.Sector ||
                !string.Equals(group.Culture, like.Culture, StringComparison.Ordinal) ||
                !string.Equals(group.Religion, like.Religion, StringComparison.Ordinal) ||
                !string.Equals(group.Profession, like.Profession, StringComparison.Ordinal)) continue;
            group.Size += people;
            return;
        }
        state.PopulationGroups.Add(new PopulationGroup
        {
            Class = like.Class, Sector = like.Sector, Culture = like.Culture, Religion = like.Religion,
            Profession = like.Profession, Size = people, Employment = like.Employment, Wealth = like.Wealth,
            StandardOfLiving = like.StandardOfLiving, Radicalism = like.Radicalism,
        });
    }

    private static Ledger LedgerOf(Campaign campaign) => Ledgers.GetValue(campaign, _ => new Ledger());

    /// <summary>Which state a map index is, and how many provinces each has - both change when a region is split.</summary>
    private static void Recount(Campaign campaign, Ledger ledger)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        ledger.StateByMap.Clear();
        foreach (var state in campaign.States) ledger.StateByMap[state.MapIndex] = state;
        ledger.CountedOnDay = campaign.ElapsedDays;

        var size = 1;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            size = Math.Max(size, topology.ProvinceToState[province] + 1);
        if (ledger.Provinces.Length < size) ledger.Provinces = new int[size];
        else Array.Clear(ledger.Provinces, 0, ledger.Provinces.Length);
        for (var province = 1; province <= topology.ProvinceCount; province++)
            ledger.Provinces[topology.ProvinceToState[province]]++;
    }
}
