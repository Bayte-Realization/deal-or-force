using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class ScenarioState
{
    /// <summary>
    /// Land that belongs to nobody by design, which any country able to reach it may settle.
    /// Set by history ("owner": "none"), cleared the day a colony is founded.
    /// </summary>
    public bool Unclaimed;

    /// <summary>
    /// The map index of the country settling it, or 0 - the map's own "nobody", which a
    /// country's id is not: the first country in the table has id 0. One settler at a time:
    /// whoever lands first has the field.
    /// </summary>
    public int ColonistMapIndex;

    /// <summary>How far the settlement has got, from nothing to a colony.</summary>
    public float ColonisationProgress;

    /// <summary>
    /// The countries that claim this land without holding it, by tag: Antarctica's seven, whose
    /// claims the 1959 Treaty froze rather than ended. From history ("claims"). A claimant
    /// settles its own claim for less and sooner; anybody else who settles it makes an enemy of
    /// every claimant.
    /// </summary>
    public List<string> Claims = new();

    /// <summary>
    /// The treaty the land is held under, if any ("antarctic"), from history ("treaty"). A
    /// government nobody plays does not break it (Defines.AiColonisesTreatyLand): left to the
    /// colonisation roll, Iran had settled Graham Land within two years and the computer had
    /// colonised three and a half thousand provinces of Antarctica in twenty.
    /// </summary>
    public string Treaty = "";

    /// <summary>The day a colony was founded here, or -1 for land that was never anybody's to settle.</summary>
    public int ColonisedDay = -1;
}

public sealed partial class Defines
{
    /// <summary>What founding a colony costs whatever its size, in the treasury's money.</summary>
    public float ColonisationCostBase { get; set; } = 2.0f;

    /// <summary>
    /// And for each province of it, taken as a square root: a region twice the size is not
    /// twice the work to plant a flag in, and Antarctica is nine thousand provinces of ice.
    /// </summary>
    public float ColonisationCostPerProvinceRoot { get; set; } = 0.3f;

    /// <summary>How long a colony takes to found whatever its size, in days.</summary>
    public int ColonisationDaysBase { get; set; } = 180;

    /// <summary>And for each province, again as a square root.</summary>
    public float ColonisationDaysPerProvinceRoot { get; set; } = 4f;

    /// <summary>
    /// The settlers who found it, taken from the colonising country's most populous region -
    /// at most a hundredth of that region, so nobody empties a province to plant a flag.
    /// </summary>
    public int ColonistsOnFounding { get; set; } = 2000;

    /// <summary>How many colonies one country may be founding at once.</summary>
    public int MaxColoniesAtOnce { get; set; } = 1;

    /// <summary>
    /// The chance each month that a country nobody plays sets out to settle land it can reach
    /// and afford. Small, because it is asked of every such country: on 1 January 2024 about
    /// fifty can afford the small Antarctic region and fourteen the ice sheet, which at this
    /// rate leaves the first a year or so and the second some four years before anybody goes.
    /// </summary>
    public float AiColonisationChancePerMonth { get; set; } = 0.0015f;

    /// <summary>How many times the price a country nobody plays wants in the treasury before it spends it on a colony.</summary>
    public float AiColonisationTreasuryMultiple { get; set; } = 4f;

    /// <summary>What settling land it claims costs a claimant, and how long it takes, as shares of the full price and time.</summary>
    public float ColonisationClaimantCostShare { get; set; } = 0.6f;
    public float ColonisationClaimantDaysShare { get; set; } = 0.75f;

    /// <summary>How much dearer a country nobody plays reckons land somebody else claims: the price of the quarrel.</summary>
    public float AiColonisationForeignClaimWeight { get; set; } = 2.5f;

    /// <summary>Whether a country nobody plays settles land a treaty holds for nobody (ScenarioState.Treaty).</summary>
    public bool AiColonisesTreatyLand { get; set; } = false;
}

/// <summary>
/// Land nobody owns, and how somebody comes to own it (item 60).
///
/// Every region on the map belonged to a country, so a world that had anywhere left to claim -
/// a colonial-era scenario, or Antarctica under its treaty today - could not be written at
/// all. A region can now be nobody's: history marks it "owner": "none", its provinces stay
/// unowned when the map is attached, and it is drawn as the grey of ground no flag flies over.
///
/// A country that can reach it may settle it: over land from a province of its own next to
/// it, or by sea from a coast of its own when the land is coastal. It pays once, waits while
/// the settlement grows, and the region is its own the day it is founded, with the settlers
/// who founded it. One settler at a time holds the field. A country nobody plays does the same
/// when it has the money and nothing better to do.
/// </summary>
public static class ColonisationCore
{
    public static bool IsUnclaimed(ScenarioState state) =>
        state is { Unclaimed: true, NationMapIndex: 0 } && !LakeRegions.IsWaterState(state);

    public static int Provinces(Campaign campaign, ScenarioState state) =>
        campaign?.ProvinceMap is null || state is null
            ? 0
            : PeaceConference.ProvincesOfState(campaign.ProvinceMap, state.MapIndex).Count;

    /// <summary>What founding a colony here costs.</summary>
    public static float Cost(Campaign campaign, ScenarioState state)
    {
        var defines = campaign.Defines;
        return defines.ColonisationCostBase +
               defines.ColonisationCostPerProvinceRoot * MathF.Sqrt(Provinces(campaign, state));
    }

    /// <summary>How many days it takes.</summary>
    public static int Days(Campaign campaign, ScenarioState state)
    {
        var defines = campaign.Defines;
        return Math.Max(1, defines.ColonisationDaysBase +
                           (int)MathF.Round(defines.ColonisationDaysPerProvinceRoot * MathF.Sqrt(Provinces(campaign, state))));
    }

    /// <summary>Whether this country claims this land.</summary>
    public static bool Claims(ScenarioState state, Nation nation)
    {
        if (state?.Claims is not { Count: > 0 } claims || nation is null) return false;
        foreach (var tag in claims)
            if (string.Equals(tag, nation.Tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The claimants of this land that are countries in this world, other than this one.</summary>
    public static List<Nation> OtherClaimants(Campaign campaign, ScenarioState state, Nation nation)
    {
        var found = new List<Nation>();
        if (campaign is null || state?.Claims is not { Count: > 0 } claims) return found;
        foreach (var tag in claims)
            if (campaign.NationByTag(tag) is { } claimant && claimant != nation && claimant.Movement is null &&
                campaign.StatesOf(claimant).Length > 0)
                found.Add(claimant);
        return found;
    }

    /// <summary>What founding a colony here costs this country: less, when the land is its claim.</summary>
    public static float CostFor(Campaign campaign, Nation nation, ScenarioState state) =>
        Cost(campaign, state) * (Claims(state, nation) ? campaign.Defines.ColonisationClaimantCostShare : 1f);

    /// <summary>And how long it takes this country.</summary>
    public static int DaysFor(Campaign campaign, Nation nation, ScenarioState state) =>
        Claims(state, nation)
            ? Math.Max(1, (int)MathF.Round(Days(campaign, state) * campaign.Defines.ColonisationClaimantDaysShare))
            : Days(campaign, state);

    /// <summary>
    /// Whether a country can get settlers there: across a land border from ground of its own,
    /// or by sea, when both the land and the country have a coast.
    /// </summary>
    public static bool Reaches(Campaign campaign, Nation nation, ScenarioState state)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || nation is null || state is null) return false;
        var own = (ushort)nation.MapIndex;
        foreach (var province in PeaceConference.ProvincesOfState(map, state.MapIndex))
            foreach (var neighbour in map.Neighbors[province])
                if (map.ProvinceToNation[neighbour] == own) return true;

        if (Waterfront.OfRegion(campaign, state) != WaterAccess.Sea) return false;
        foreach (var region in campaign.StatesOf(nation))
            if (Waterfront.OfRegion(campaign, region) == WaterAccess.Sea) return true;
        return false;
    }

    /// <summary>Who is settling a region, or null.</summary>
    public static Nation Colonist(Campaign campaign, ScenarioState state) =>
        state is null || state.ColonistMapIndex == 0
            ? null
            : campaign.Nations.FirstOrDefault(nation => nation.MapIndex == state.ColonistMapIndex);

    /// <summary>How many colonies a country is founding now.</summary>
    public static int Settling(Campaign campaign, Nation nation) =>
        nation is null ? 0 : campaign.States.Count(state => state.ColonistMapIndex == nation.MapIndex);

    /// <summary>Why this country cannot start settling this region now, or empty when it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (campaign is null || nation is null || state is null) return Loc.Get("Choose a region.");
        if (!IsUnclaimed(state)) return Loc.Get("Only land that belongs to nobody can be settled.");
        if (state.ColonistMapIndex == nation.MapIndex) return Loc.Get("Our settlers are already there.");
        if (state.ColonistMapIndex != 0)
            return Loc.Format("{0} is already settling it.",
                Colonist(campaign, state)?.DisplayName ?? Loc.Get("Somebody"));
        if (Settling(campaign, nation) >= campaign.Defines.MaxColoniesAtOnce)
            return Loc.Get("Our settlers are already busy founding another colony.");
        if (!Reaches(campaign, nation, state))
            return Loc.Get("We cannot reach it: it borders none of our land, and we have no coast to sail from.");
        var cost = CostFor(campaign, nation, state);
        if (nation.Treasury < cost)
            return Loc.Format("Founding a colony here costs {0:0.00}; the treasury holds {1:0.00}.", cost, nation.Treasury);
        return "";
    }

    /// <summary>Send the settlers: paid for now, and a colony in <see cref="Days"/>.</summary>
    public static bool Start(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (Refusal(campaign, nation, state).Length > 0) return false;
        nation.Treasury -= CostFor(campaign, nation, state);
        state.ColonistMapIndex = nation.MapIndex;
        state.ColonisationProgress = 0f;
        // Every other country that claims it hears of it the day the ships sail.
        foreach (var claimant in OtherClaimants(campaign, state, nation))
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, claimant.Id, nation.Id), "settling_our_claim");
        DiagnosticLogs.Write("simulation",
            $"COLONY_STARTED nation={nation.Tag} region={state.Id} days={DaysFor(campaign, nation, state)} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>Bring the settlers home. What was spent is spent.</summary>
    public static bool Abandon(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (nation is null || state is null || state.ColonistMapIndex != nation.MapIndex) return false;
        state.ColonistMapIndex = 0;
        state.ColonisationProgress = 0f;
        DiagnosticLogs.Write("simulation", $"COLONY_ABANDONED nation={nation.Tag} region={state.Id} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>A day of settling, and a month's thinking for the countries nobody plays. Once a day.</summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(ColonisationCore))) return;
        if (campaign?.ProvinceMap is null) return;
        foreach (var state in campaign.States)
        {
            if (state.ColonistMapIndex == 0) continue;
            var nation = Colonist(campaign, state);
            // Somebody else got there, or the settlers' country is gone.
            if (nation is null || !IsUnclaimed(state))
            {
                state.ColonistMapIndex = 0;
                state.ColonisationProgress = 0f;
                continue;
            }
            // A country that has lost its way there - its coast, the border it crossed - waits.
            if (!Reaches(campaign, nation, state)) continue;
            // Half a day's grace on the finish: a day's share added up a few hundred times in
            // single precision can land a hair short of one, and the colony would be a day late.
            var days = DaysFor(campaign, nation, state);
            state.ColonisationProgress += 1f / days;
            if (state.ColonisationProgress >= 1f - 0.5f / days) Found(campaign, nation, state);
        }
        ThinkAboutColonies(campaign);
    }

    /// <summary>The colony is founded: the ground is theirs, and so are the people who came to it.</summary>
    private static void Found(Campaign campaign, Nation nation, ScenarioState state)
    {
        var map = campaign.ProvinceMap;
        var own = (ushort)nation.MapIndex;
        // And every other claimant, the day the flag goes up, for years.
        foreach (var claimant in OtherClaimants(campaign, state, nation))
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, claimant.Id, nation.Id), "took_our_claim");
        foreach (var province in PeaceConference.ProvincesOfState(map, state.MapIndex))
        {
            map.ProvinceToNation[province] = own;
            if (campaign.ProvinceController is { } controllers && province < controllers.Length) controllers[province] = own;
            if (campaign.ProvinceOccupation is { } occupation && province < occupation.Length) occupation[province] = 0f;
        }
        state.NationMapIndex = nation.MapIndex;
        state.NationId = nation.Tag;
        state.Unclaimed = false;
        state.ColonisedDay = campaign.ElapsedDays;
        state.ColonistMapIndex = 0;
        state.ColonisationProgress = 0f;

        // The settlers, from wherever the country has most people to spare.
        var source = campaign.StatesOf(nation).Where(region => region.MapIndex != state.MapIndex)
            .OrderByDescending(region => region.Population).FirstOrDefault();
        var settlers = source is null ? 0 : Math.Min(campaign.Defines.ColonistsOnFounding, source.Population / 100);
        if (settlers > 0)
        {
            EconomySystem.AdjustStatePopulation(source, -settlers);
            var culture = DemographyCatalog.PrimaryCulture(nation.Tag);
            state.PopulationGroups.Add(new PopulationGroup
            {
                Class = SocialClass.Lower,
                Sector = Sector.Industry,
                Culture = culture,
                Religion = DemographyCatalog.FaithOf(culture)[0].Religion,
                Profession = "labourers",
                Size = settlers,
                Employment = 0.90f,
                Wealth = 0.40f,
                StandardOfLiving = nation.StandardOfLiving,
                Radicalism = 0f
            });
            state.Population += settlers;
        }

        DiagnosticLogs.Write("simulation",
            $"COLONY_FOUNDED nation={nation.Tag} region={state.Id} settlers={settlers} day={campaign.ElapsedDays}");
        campaign.InvalidateStateIndex();
        campaign.MarkMapChanged();
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// A country nobody plays settles land it can reach when it is at peace, has several times
    /// the price to spare, and is not already founding a colony - once in a while, not the
    /// moment it becomes possible. Each country thinks on its own day of the month.
    ///
    /// Whether it does this month comes from a hash of the country and the month rather than
    /// from the campaign's random stream: drawing from that stream here would move every
    /// random roll after it, and a world whose other wars turn on a roll is a world that should
    /// not change because somebody considered Antarctica.
    /// </summary>
    private static void ThinkAboutColonies(Campaign campaign)
    {
        var open = campaign.States.Where(state => IsUnclaimed(state) && state.ColonistMapIndex == 0 &&
                                                  (campaign.Defines.AiColonisesTreatyLand || string.IsNullOrEmpty(state.Treaty))).ToList();
        if (open.Count == 0) return;
        var defines = campaign.Defines;
        var month = campaign.ElapsedDays / 30;
        foreach (var nation in campaign.Nations)
        {
            if (nation.IsHuman || nation.Population <= 0) continue;
            if (campaign.ElapsedDays % 30 != Math.Abs(nation.Id) % 30) continue;
            if (Settling(campaign, nation) >= defines.MaxColoniesAtOnce) continue;
            if (campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id))) continue;
            if (Chance(nation.Id, month) >= defines.AiColonisationChancePerMonth) continue;

            // The cheapest it can reach and afford - its own claims cheaper still, and anybody
            // else's dearer by the quarrel it would start.
            ScenarioState best = null;
            var bestCost = float.MaxValue;
            foreach (var state in open)
            {
                if (state.ColonistMapIndex != 0) continue;
                var cost = CostFor(campaign, nation, state);
                if (nation.Treasury < cost * defines.AiColonisationTreasuryMultiple) continue;
                var reckoned = !Claims(state, nation) && OtherClaimants(campaign, state, nation).Count > 0
                    ? cost * defines.AiColonisationForeignClaimWeight
                    : cost;
                if (reckoned >= bestCost) continue;
                if (!Reaches(campaign, nation, state)) continue;
                best = state;
                bestCost = reckoned;
            }
            if (best is not null) Start(campaign, nation, best);
        }
    }

    private static float Chance(int nation, int month)
    {
        unchecked
        {
            var hash = (uint)(nation * 73856093) ^ (uint)(month * 19349663) ^ 0x9E3779B9u;
            hash ^= hash >> 16; hash *= 0x7FEB352Du; hash ^= hash >> 15; hash *= 0x846CA68Bu; hash ^= hash >> 16;
            return (hash >> 8) * (1f / 16777216f);
        }
    }
}

/// <summary>
/// Regions history says belong to nobody, made to belong to nobody on the ground too. Run when
/// the map is attached, after the political map and before the region list is made to agree
/// with it - so the provinces of an unclaimed region are unowned before anything reads them,
/// and a region whose record says "nobody" is not handed back to whoever the raster names.
/// </summary>
public static class UnclaimedLand
{
    public static int ApplyToGround(Campaign campaign, ProvinceTopology topology, ushort[] controllers)
    {
        if (campaign is null || topology is null) return 0;
        EnsureAuthoredRegions(campaign, topology);
        var unclaimed = new HashSet<int>(campaign.States.Where(ColonisationCore.IsUnclaimed).Select(state => state.MapIndex));
        if (unclaimed.Count == 0) return 0;
        var cleared = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (!unclaimed.Contains(topology.ProvinceToState[province])) continue;
            topology.ProvinceToNation[province] = 0;
            if (controllers is not null && province < controllers.Length) controllers[province] = 0;
            cleared++;
        }
        return cleared;
    }

    // Some source polygons have a province but no administrative region (Bir Tawil).
    // Coordinate declarations bind a dossier to actual unowned ground; they do not
    // change ownership or let a file silently steal another country's province.
    // They live with every other region of the bookmark, in history/states/<year> (a line with a
    // point on it); the old map_data/unclaimed_regions folder is still read so a mod written
    // for it keeps working.
    private static void EnsureAuthoredRegions(Campaign c, ProvinceTopology map)
    {
        foreach (var document in GameDataLoader.DataDocuments("history", "states", "2024")
                     .Concat(GameDataLoader.DataDocuments("map_data", "unclaimed_regions")))
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
            foreach (var row in document.RootElement.EnumerateObject())
            {
                var e = row.Value;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("latitude", out var la) || la.ValueKind != JsonValueKind.Number ||
                    !e.TryGetProperty("longitude", out var lo) || lo.ValueKind != JsonValueKind.Number ||
                    !la.TryGetSingle(out var lat) || !lo.TryGetSingle(out var lon) || !float.IsFinite(lat) || !float.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) continue;
                var x = Math.Clamp((int)((lon + 180) / 360 * map.Width), 0, map.Width - 1);
                var y = Math.Clamp((int)((90 - lat) / 180 * map.Height), 0, map.Height - 1);
                var province = map.CellToProvince[y * map.Width + x];
                if (province <= 0 || map.IsWater(province) || map.ProvinceToNation[province] != 0) continue;
                var oldRegion = map.ProvinceToState[province];
                if (c.States.Any(s => s.MapIndex == oldRegion)) continue;
                var state = c.States.Find(s => s.Id == row.Name);
                if (state is null)
                {
                    var index = Math.Max(c.States.Select(s => s.MapIndex).DefaultIfEmpty(0).Max(), map.ProvinceToState.Max(v => (int)v)) + 1;
                    if (index >= ushort.MaxValue) continue;
                    state = new ScenarioState { Id = row.Name, Iso = row.Name, MapIndex = index, NationMapIndex = 0,
                        Unclaimed = true, Name = e.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : row.Name };
                    c.States.Add(state);
                }
                map.ProvinceToState[province] = (ushort)state.MapIndex;
                c.InvalidateStateIndex();
            }
        }
    }
}

/// <summary>Send settlers to a region nobody owns. See ColonisationCore.</summary>
public sealed class ColoniseCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }
    public bool Started { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var state = campaign.States.Find(candidate => candidate.MapIndex == StateMapIndex);
        Started = ColonisationCore.Start(campaign, nation, state);
    }
}

/// <summary>Call the settlers home. See ColonisationCore.</summary>
public sealed class AbandonColonyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var state = campaign.States.Find(candidate => candidate.MapIndex == StateMapIndex);
        ColonisationCore.Abandon(campaign, nation, state);
    }
}
