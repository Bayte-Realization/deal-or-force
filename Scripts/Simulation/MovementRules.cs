using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>What one supporter gives in a week, in billions (5e-10 is fifty cents).</summary>
    public float MovementDonationPerSupporter { get; set; } = 5e-10f;
    /// <summary>How much a believer of one of its faiths counts for, against one of its own peoples.</summary>
    public float MovementFaithShare { get; set; } = 0.08f;
    /// <summary>How fast support moves towards what the region's grievances make of it, a week.</summary>
    public float MovementSupportDrift { get; set; } = 0.015f;
    /// <summary>Cadres a week per supporter, and the most it can have per supporter.</summary>
    public float MovementRecruitPerSupporter { get; set; } = 4e-5f;
    public float MovementCadresPerSupporter { get; set; } = 0.02f;
    public float MovementHeatDecay { get; set; } = 0.03f;
    /// <summary>Heat at which the host expels the headquarters.</summary>
    public float MovementExpelHeat { get; set; } = 0.85f;
    public int MovementRelocationDays { get; set; } = 30;
    public float MovementRelocationCost { get; set; } = 0.02f;
    public float MovementCampaignCost { get; set; } = 0.012f;
    public float MovementCampaignSupport { get; set; } = 0.06f;
    public float MovementCampaignHeat { get; set; } = 0.05f;
    public int MovementCampaignDays { get; set; } = 30;
    public int MovementFundraiseDays { get; set; } = 90;
    public float MovementFundraiseWeeks { get; set; } = 8f;
    public float MovementRecruitCostPerThousand { get; set; } = 0.015f;
    public int MovementRecruitDays { get; set; } = 30;
    public int MovementRecognitionDays { get; set; } = 120;
    public float MovementRecognitionOpinion { get; set; } = 30f;
    public float MovementUprisingSupport { get; set; } = 0.4f;
    public float MovementUprisingCadres { get; set; } = 3000f;
    public float MovementUprisingCost { get; set; } = 0.05f;
    public int MovementUprisingDays { get; set; } = 180;
    /// <summary>One militia formation for this many cadres when an uprising succeeds, up to MovementUprisingMilitiaMax.</summary>
    public float MovementMilitiaPerCadres { get; set; } = 6000f;
    public int MovementUprisingMilitiaMax { get; set; } = 16;
    /// <summary>And one more for this many of its supporters in the region that rose: the street, not only the cadres.</summary>
    public float MovementMilitiaPerSupporters { get; set; } = 120000f;
    public int MovementTraitActionDays { get; set; } = 60;
    /// <summary>Calling a held region to arms: what it costs in money and cadres, how long until the next, and the most it raises.</summary>
    public float MovementCallToArmsCost { get; set; } = 0.01f;
    public float MovementCallToArmsCadres { get; set; } = 1500f;
    public int MovementCallToArmsDays { get; set; } = 45;
    public int MovementCallToArmsMax { get; set; } = 4;
    /// <summary>Fighters sent into a region at a time, days on the road per degree of distance (and at least the minimum), and the share a week that melts away.</summary>
    public float MovementInfiltrateFighters { get; set; } = 3000f;
    public float MovementInfiltrateDaysPerDegree { get; set; } = 0.8f;
    public int MovementInfiltrateMinimumDays { get; set; } = 7;
    public float MovementInfiltrateDecay { get; set; } = 0.03f;
    /// <summary>What a country that chooses to fund a movement pays it, in billions a year.</summary>
    public float MovementSponsorPerYear { get; set; } = 0.05f;
    /// <summary>What each terrorist listing takes off its donations, and the least it leaves.</summary>
    public float MovementDesignationDonations { get; set; } = 0.06f;
}

/// <summary>
/// What a movement does, week by week and when it is told to (<see cref="MovementActionCommand"/>).
/// The traits in common/movements change these rules for the one that has them.
/// </summary>
public static class MovementRules
{
    // ------------------------------------------------------------------ the week -----

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The movements' day in stages, one movement's week at a time, for the smoothed step.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(MovementRules))) yield break;
        if (campaign.ElapsedDays == 0) yield break;
        var week = Math.Max(1, campaign.Defines.EconomyTickDays);
        using (SimProfiler.Measure("movements"))
        using (SimProfiler.Measure("  mov:rules"))
        {
            Movements.RetireDuplicateRepresentations(campaign);
            MovementLifecycle.Tick(campaign);
            // What the world decides about who has fallen happens on the week, as it did.
            if (campaign.ElapsedDays % week == 0)
            {
                Owners(campaign, rebuild: true);
                using (SimProfiler.Measure("    mov:homelands")) MovementFall.RememberHomelands(campaign);
                using (SimProfiler.Measure("    mov:fall")) MovementFall.Decide(campaign);
                using (SimProfiler.Measure("    mov:fall")) MovementFall.EndLostWars(campaign);
            }
        }
        yield return 0;
        // Each movement's own week on a day of its own. They all used to fall on the same
        // day, so their cost did not spread: one frame in seven carried every movement's week
        // at once, the largest hitch in a late campaign at the top speed. Every movement still
        // has exactly one week in any seven days - on its day rather than on everybody's.
        // An uprising can found a nation mid-pass, so the walk pins the count and goes by
        // index (Id == index); founders join on their next day rather than mid-list.
        for (var id = 0; id < campaign.Nations.Count; id++)
        {
            var nation = campaign.Nations[id];
            if (nation.Movement is not { Dissolved: false } || !OnItsDay(campaign, id, week)) continue;
            using (SimProfiler.Measure("movements"))
            using (SimProfiler.Measure("  mov:rules"))
            {
                Owners(campaign);
                using (SimProfiler.Measure("    mov:week")) Week(campaign, nation);
                using (SimProfiler.Measure("    mov:endure")) MovementFall.Endure(campaign, nation);
            }
            yield return 0;
        }
    }

    /// <summary>Whether today is this one's day in a cycle of so many days.</summary>
    internal static bool OnItsDay(Campaign campaign, int id, int cycle) =>
        (campaign.ElapsedDays + (id % cycle + cycle) % cycle) % cycle == 0;

    internal static Dictionary<int, Nation> Owners(Campaign campaign, bool rebuild = false) =>
        campaign?.NationsByMapIndex() ?? new Dictionary<int, Nation>();

    internal static Nation OwnerOf(Campaign campaign, ScenarioState region) =>
        region is not null && campaign != null ? campaign.NationByMapIndex(region.NationMapIndex) : null;

    private static readonly Dictionary<int, bool> OwnStateCache = new();
    private static readonly HashSet<int> LiveRegions = new();
    private static readonly List<int> StaleSupport = new();

    internal static void Week(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null) return;
        var defines = campaign.Defines;

        // Land lost is land lost: a movement that had a country and has had it taken back
        // goes on as what it was before - somewhere to be governed from, and a following.
        // Land HELD is the opposite door: the movement graduates into the state it has
        // become, and everything below stops applying to it.
        SynchroniseTerritory(campaign, nation);
        // Ground of its own makes it a country (GraduateToStatehood) - including a movement a
        // save from before that rule left holding land as a movement.
        if (nation.Movement is { Landless: false, Exile: false } && campaign.HasOwnedLand(nation))
            GraduateToStatehood(campaign, nation);
        state = nation.Movement;
        if (state is null) return;

        // The headquarters arrives where it was going.
        if (state.MovingToStateMapIndex >= 0 && campaign.ElapsedDays >= state.ArrivesOnDay)
        {
            state.HeadquartersStateMapIndex = state.MovingToStateMapIndex;
            state.MovingToStateMapIndex = -1;
            state.ArrivesOnDay = -1;
            state.Heat *= 0.3f;
            Movements.Place(campaign);
            state.LastReport = Loc.Format("The headquarters has moved to {0}.", RegionName(campaign, state.HeadquartersStateMapIndex));
        }

        // The following, region by region: it drifts towards what the region's grievances and
        // the movement's standing make of it.
        long supporters = 0;
        long believers = 0;
        OwnStateCache.Clear();
        LiveRegions.Clear();
        Dictionary<ScenarioState, (long Own, long Faithful)> people;
        using (SimProfiler.Measure("      mov:people"))
            people = PeopleIn(campaign, definition);
        // The regions where this movement has people, not every region on the map. The
        // support dictionary is sparse, and the people index already knows its own keys.
        using (SimProfiler.Measure("      mov:support"))
        {
            foreach (var (region, known) in people)
            {
                var own = known.Own;
                var faithful = known.Faithful;
                if (own <= 0 && faithful <= 0) continue;
                var owner = OwnerOf(campaign, region);
                var supportCeiling = MovementReach.SupportCeiling(campaign, nation, region, definition, own, faithful, owner);
                if (supportCeiling <= 0f) continue;
                LiveRegions.Add(region.MapIndex);
                believers += faithful;
                var support = state.Support.GetValueOrDefault(region.MapIndex);
                var foreign = false;
                if (owner is not null && owner.Id != nation.Id)
                {
                    if (!OwnStateCache.TryGetValue(owner.Id, out var theirs)) OwnStateCache[owner.Id] = theirs = OwnState(owner, definition);
                    foreign = !theirs;
                }
                var foreignRule = foreign ? 0.12f : 0f;
                var grievance = M.Clamp01(region.Radicalism * 0.6f + (1f - region.Stability) * 0.25f +
                                          region.WarDamage * 0.4f + foreignRule);
                var civic = CivilIndependence.AwarenessTarget(campaign, definition, state, region, owner);
                var reputation = SocialMemory.Esteem(campaign, owner, nation) * defines.SocialMovementWeight;
                var target = M.Clamp(MathF.Max(civic, 0.05f + state.Legitimacy * 0.2f + grievance * 0.5f) + reputation, 0f, MathF.Min(.75f, supportCeiling));
                support += (target - support) * defines.MovementSupportDrift;
                state.Support[region.MapIndex] = M.Clamp(support, 0f, supportCeiling);
                supporters += (long)(own * support);
            }
            if (state.Support.Count > LiveRegions.Count)
            {
                StaleSupport.Clear();
                foreach (var key in state.Support.Keys)
                    if (!LiveRegions.Contains(key)) StaleSupport.Add(key);
                foreach (var key in StaleSupport) state.Support.Remove(key);
            }
        }
        var fromFaith = (long)(believers * defines.MovementFaithShare * 0.1f);

        // Fighters sent ahead arrive, and the ones already hidden drift away a little each week.
        for (var index = state.OnTheWay.Count - 1; index >= 0; index--)
        {
            var journey = state.OnTheWay[index];
            if (journey.ArrivesOnDay > campaign.ElapsedDays) continue;
            state.OnTheWay.RemoveAt(index);
            var arrived = campaign.States.Find(region => region.MapIndex == journey.Region);
            if (arrived is null || arrived.NationMapIndex == nation.MapIndex) { state.Cadres += journey.Fighters; continue; }
            state.Infiltrated[journey.Region] = state.Infiltrated.GetValueOrDefault(journey.Region) + journey.Fighters;
            state.LastReport = Loc.Format("{0} fighters are in place in {1}.", Fmt.People(journey.Fighters), arrived.DisplayName);
        }
        if (state.Infiltrated.Count > 0)
        foreach (var region in state.Infiltrated.Keys.ToList())
        {
            var left = state.Infiltrated[region] * (1f - defines.MovementInfiltrateDecay);
            if (left < 50f) state.Infiltrated.Remove(region);
            else state.Infiltrated[region] = left;
        }

        // Money: donations from its following, and its sponsors while they think well of it.
        var donations = (supporters + fromFaith) * defines.MovementDonationPerSupporter * (0.5f + state.Legitimacy);
        if (definition.Has("diaspora_lobby")) donations *= 1.6f;
        // Every government that lists it as terrorist closes some of the doors money came through.
        var listed = 0;
        foreach (var id in state.DesignatedBy) if (campaign.NationById(id) is not null) listed++;
        donations *= MathF.Max(0.3f, 1f - listed * defines.MovementDesignationDonations);
        if (listed > 0) state.Legitimacy = M.Clamp01(state.Legitimacy - 0.0004f * listed);
        var sponsored = 0f;
        // And the countries that have chosen to fund it pay by the week, while they can and are not at war with it.
        if (state.SponsoredBy.Count > 0)
        foreach (var (id, perYear) in state.SponsoredBy.ToList())
        {
            var sponsor = campaign.NationById(id);
            if (sponsor is null || sponsor.Movement is not null || CampaignDiplomacy.AtWar(campaign, id, nation.Id) ||
                campaign.StatesOf(sponsor).Length == 0)
            {
                state.SponsoredBy.Remove(id);
                continue;
            }
            var paid = MathF.Min(perYear / 52f, MathF.Max(0f, sponsor.Treasury) * 0.01f);
            sponsor.Treasury -= paid;
            sponsored += paid;
        }
        foreach (var (tag, perYear) in definition.Sponsors)
        {
            var sponsor = campaign.NationByTag(tag);
            if (sponsor is null || sponsor.Id == nation.Id || sponsor.Movement is not null) continue;
            var opinion = CampaignDiplomacy.Peek(campaign, sponsor.Id, nation.Id)?.Opinion ?? 0f;
            var willing = M.Clamp01(0.5f + opinion / 100f);
            sponsored += perYear / 52f * willing;
        }
        // A company's contract: the client pays by the week while it lasts and while it can.
        if (state.ContractNationId >= 0)
        {
            var client = campaign.NationById(state.ContractNationId);
            if (client is null || campaign.ElapsedDays > state.ContractUntilDay ||
                !campaign.Wars.Any(war => !war.Ended && war.Involves(client.Id)))
            {
                state.ContractNationId = -1;
                state.ContractUntilDay = -1;
            }
            else
            {
                var fee = MathF.Min(client.Treasury * 0.002f, 0.01f);
                client.Treasury -= fee;
                sponsored += fee;
            }
        }
        state.DonationsWeekly = donations;
        state.SponsorsWeekly = sponsored;
        state.IncomeWeekly = donations + sponsored;
        nation.Treasury += state.IncomeWeekly;

        // Cadres come from the following, up to what the following can give.
        var recruiting = supporters * defines.MovementRecruitPerSupporter * (definition.Has("refugee_camps") ? 2f : 1f);
        var ceiling = MathF.Max(state.Cadres, (supporters + fromFaith) * defines.MovementCadresPerSupporter);
        state.Cadres = MathF.Min(ceiling, state.Cadres + recruiting);
        if (state.Landless) nation.ManpowerReserve = state.Cadres;

        // Standing: recognition keeps it up, and a movement nobody recognises fades.
        var recognised = 0;
        foreach (var id in state.RecognisedBy) if (campaign.NationById(id) is not null) recognised++;
        var standing = 0.1f + recognised * (definition.Has("government_in_exile") ? 0.008f : 0.004f);
        if (definition.Has("civic_independence")) standing = MathF.Max(standing, CivilIndependence.StandingTarget(campaign, nation));
        if (definition.Has("recognised_sovereign")) standing = MathF.Max(standing, 0.6f);
        state.Legitimacy = M.Clamp01(state.Legitimacy + (M.Clamp01(standing) - state.Legitimacy) * 0.01f);

        // The host's patience comes back slowly, and runs out.
        state.Heat = M.Clamp01(state.Heat * (1f - defines.MovementHeatDecay));
        if (state.Landless && state.Heat >= defines.MovementExpelHeat && state.MovingToStateMapIndex < 0) Expel(campaign, nation);

        // A landless movement's economy is its following, and the rest of the game reads GDP.
        // Towards it rather than onto it: a country that has just gone into exile had an
        // economy last week, and it does not vanish in a day.
        if (state.Landless) nation.Gdp = MathF.Max(0.01f, M.Lerp(nation.Gdp, state.IncomeWeekly * 52f, 0.3f));
    }

    /// <summary>Its own peoples in a region, and the believers of its faiths who are not.</summary>
    // A movement's people in every region of the world, for the day. Its week counts them and
    // its leadership's week counts them again an hour later - each a walk of every population
    // group of all eighteen hundred regions, which made movements a sixth of a campaign day.
    // A day is the right lifetime: the census moves weekly, before either asks, and whatever
    // else moves people between regions moves a border with them, which clears it.
    internal sealed class MovementPeopleCache
{
    public int Week = -1;
    public int MapRevision = -1;
    public int States = -1;
    public readonly Dictionary<MovementDefinition, Dictionary<ScenarioState, (long Own, long Faithful)>> PeopleToday = new();
    public readonly Dictionary<string, List<PopulationGroupAt>> ByCulture = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, List<PopulationGroupAt>> ByReligion = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Dictionary<ScenarioState, long>> CultureTotals = new(StringComparer.OrdinalIgnoreCase);
    public bool Indexed;
}

    internal readonly record struct PopulationGroupAt(ScenarioState Region, PopulationGroup Group);

    /// <summary>
    /// Remembered for the day.
    /// </summary>
    internal static long PeopleOf(Campaign campaign, ScenarioState region, MovementDefinition definition, out long faithful)
    {
        var regions = PeopleIn(campaign, definition);
        if (regions.TryGetValue(region, out var known))
        {
            faithful = known.Faithful;
            return known.Own;
        }
        faithful = 0;
        return 0;
    }

    /// <summary>
    /// Every region with any of a movement's peoples or believers in it, for the current day.
    /// </summary>
    internal static Dictionary<ScenarioState, (long Own, long Faithful)> PeopleIn(
        Campaign campaign, MovementDefinition definition)
    {
        EnsurePeopleCache(campaign);
        var cache = campaign.MovementPeople;
        if (!cache.PeopleToday.TryGetValue(definition, out var regions))
            cache.PeopleToday[definition] = regions = CountPeople(campaign, definition);
        return regions;
    }

    private static void EnsurePeopleCache(Campaign campaign)
    {
        var cache = campaign.MovementPeople;
        var week = campaign.ElapsedDays / 7;
        if (cache.Week == week && cache.MapRevision == campaign.MapRevision &&
            cache.States == campaign.States.Count)
        {
            return;
        }
        cache.PeopleToday.Clear();
        cache.CultureTotals.Clear();
        cache.Indexed = false;
        cache.Week = week;
        cache.MapRevision = campaign.MapRevision;
        cache.States = campaign.States.Count;
    }

    private static Dictionary<ScenarioState, long> CultureTotalsOf(Campaign campaign, string culture)
    {
        var cache = campaign.MovementPeople;
        if (cache.CultureTotals.TryGetValue(culture, out var totals)) return totals;
        totals = new Dictionary<ScenarioState, long>();
        if (cache.ByCulture.TryGetValue(culture, out var groups))
            foreach (var (region, group) in groups)
                totals[region] = totals.GetValueOrDefault(region) + group.Size;
        cache.CultureTotals[culture] = totals;
        return totals;
    }

    private static Dictionary<ScenarioState, (long Own, long Faithful)> CountPeople(Campaign campaign, MovementDefinition definition)
    {
        var cache = campaign.MovementPeople;
        if (!cache.Indexed)
        {
            foreach (var list in cache.ByCulture.Values) list.Clear();
            foreach (var list in cache.ByReligion.Values) list.Clear();
            foreach (var region in campaign.States)
                foreach (var group in region.PopulationGroups)
                {
                    if (group.Size <= 0) continue;
                    if (!cache.ByCulture.TryGetValue(group.Culture ?? "", out var cultured))
                        cache.ByCulture[group.Culture ?? ""] = cultured = new List<PopulationGroupAt>();
                    cultured.Add(new PopulationGroupAt(region, group));
                    if (!cache.ByReligion.TryGetValue(group.Religion ?? "", out var believing))
                        cache.ByReligion[group.Religion ?? ""] = believing = new List<PopulationGroupAt>();
                    believing.Add(new PopulationGroupAt(region, group));
                }
            cache.Indexed = true;
        }
        var counted = new Dictionary<ScenarioState, (long Own, long Faithful)>();
        foreach (var people in definition.DistinctPeoples)
            foreach (var (region, size) in CultureTotalsOf(campaign, people))
            {
                var known = counted.GetValueOrDefault(region);
                counted[region] = (known.Own + size, known.Faithful);
            }
        foreach (var faith in definition.DistinctFaiths)
            if (cache.ByReligion.TryGetValue(faith, out var groups))
                foreach (var (region, group) in groups)
                {
                    if (definition.HasPeople(group.Culture)) continue;
                    var known = counted.GetValueOrDefault(region);
                    counted[region] = (known.Own, known.Faithful + group.Size);
                }
        return counted;
    }

    internal static long PeopleOf(ScenarioState region, MovementDefinition definition, out long faithful)
    {
        long own = 0;
        faithful = 0;
        foreach (var group in region.PopulationGroups)
        {
            if (group.Size <= 0) continue;
            if (definition.HasPeople(group.Culture)) own += group.Size;
            else if (definition.HasFaith(group.Religion)) faithful += group.Size;
        }
        return own;
    }

    /// <summary>Whether a country is one its peoples would call their own.</summary>
    internal static bool OwnState(Nation owner, MovementDefinition definition)
    {
        foreach (var people in definition.Peoples)
        {
            if (owner.FoundedCultures is { } founded)
                foreach (var culture in founded)
                    if (string.Equals(culture, people, StringComparison.OrdinalIgnoreCase)) return true;
            if (DemographyCatalog.IsPrimary(owner.Tag, people)) return true;
        }
        return false;
    }

    /// <summary>The most support a following reaches: somebody always stays at home.</summary>
    public const float MaxSupport = 0.92f;

    /// <summary>Support won by an action, less the more it already has.</summary>
    internal static void Win(MovementState state, int region, float gain)
    {
        var support = state.Support.GetValueOrDefault(region);
        state.Support[region] = M.Clamp(support + gain * (1f - support), 0f, MaxSupport);
    }

    // ------------------------------------------------------------------ being moved on -

    /// <summary>The host has had enough: the headquarters goes to the friendliest country that will have it.</summary>
    internal static void Expel(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        var host = Movements.HostOf(campaign, nation);
        var refuge = BestRefuge(campaign, nation, host);
        state.Cadres *= 0.8f;
        state.Heat = 0.2f;
        if (host is not null) OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, nation.Id, host.Id), "expelled_us");
        if (refuge is null)
        {
            // Out on the street: nowhere to stand until somebody takes it in (MovementFall.Endure).
            state.HeadquartersStateMapIndex = -1;
            state.MovingToStateMapIndex = -1;
            campaign.MovementRevision++;
            state.LastReport = Loc.Format("{0} has expelled the headquarters, and nobody will take it in.", host?.DisplayName ?? "");
            return;
        }
        state.HeadquartersStateMapIndex = refuge.MapIndex;
        state.MovingToStateMapIndex = -1;
        Movements.Place(campaign);
        state.LastReport = Loc.Format("{0} has expelled the headquarters; it has fled to {1}.",
            host?.DisplayName ?? "", refuge.DisplayName);
    }

    /// <summary>
    /// What the host should hear before the headquarters is thrown out. Empty while the
    /// welcome still has room. Reading it does not cool or spend the patience.
    /// </summary>
    public static string Patience(Campaign c, Nation movement)
    {
        var state = movement?.Movement;
        if (c is null || state is null || state.Dissolved || !state.Landless) return "";
        if (state.HeadquartersStateMapIndex < 0) return "";
        var line = MathF.Max(0.05f, c.Defines.MovementExpelHeat);
        if (state.Heat < line * 0.45f) return "";
        var armed = MovementMethods.StageOf(c, movement) >= MovementStage.Armed;
        if (state.Heat >= line * 0.75f)
            return armed
                ? "The welcome is almost gone: what it will do on this ground has used it up."
                : "The welcome is almost gone. Another hard week and the headquarters is thrown out.";
        return armed
            ? "The host is losing patience with what this movement is willing to do here."
            : "The host is losing patience. A headquarters that works in the open spends it.";
    }

    /// <summary>A short mark for the province card. Empty while <see cref="Patience"/> is empty.</summary>
    public static string PatienceChip(Campaign c, Nation movement)
    {
        var state = movement?.Movement;
        if (c is null || state is null || Patience(c, movement).Length == 0) return "";
        var line = MathF.Max(0.05f, c.Defines.MovementExpelHeat);
        return state.Heat >= line * 0.75f ? "welcome almost gone" : "welcome wearing thin";
    }

    /// <summary>The headquarters on this country's ground that is closest to being thrown out, if any.</summary>
    public static Nation WornWelcome(Campaign c, Nation host)
    {
        if (c is null || host is null) return null;
        Nation worn = null;
        var heat = -1f;
        foreach (var movement in c.Nations)
        {
            var state = movement.Movement;
            if (state is null || state.Dissolved || !state.Landless) continue;
            if (Movements.HostOf(c, movement) != host) continue;
            if (Patience(c, movement).Length == 0) continue;
            if (state.Heat <= heat) continue;
            heat = state.Heat;
            worn = movement;
        }
        return worn;
    }

    /// <summary>A region of the friendliest country that is not at war with it, not the one throwing it out.</summary>
    internal static ScenarioState BestRefuge(Campaign campaign, Nation nation, Nation except)
    {
        // Whoever holds what it claims is the enemy whose enemies make it welcome.
        var claimants = new List<int>();
        foreach (var id in Movements.DefinitionOf(nation)?.Homeland ?? new List<string>())
            if (Movements.StateById(campaign, id) is { } claimed && OwnerOf(campaign, claimed) is { } holder && holder.Id != nation.Id)
                claimants.Add(holder.Id);
        // Where it is from: the middle of the first homeland region, if it has one. A refuge is
        // chosen near that rather than wherever the warmest welcome happens to be - exiles go to
        // the neighbours and to their own part of the world, London and Warsaw, not Havana.
        (float Latitude, float Longitude)? home = null;
        var homeland = Movements.DefinitionOf(nation)?.Homeland;
        if (homeland is { Count: > 0 } && Movements.StateById(campaign, homeland[0]) is { } homeRegion)
            home = Movements.CentreOf(campaign, homeRegion);
        ScenarioState best = null;
        var bestScore = float.MinValue;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate == except || candidate.Id == nation.Id || candidate.Movement is not null) continue;
            if (CampaignDiplomacy.AtWar(campaign, candidate.Id, nation.Id)) continue;
            var states = campaign.StatesOf(candidate);
            if (states.Length == 0) continue;
            var distancePenalty = home is { } at
                ? MathF.Min(60f, NavalOperations.Distance(at.Latitude, at.Longitude, candidate.CapitalLatitude, candidate.CapitalLongitude) * 0.9f)
                : 0f;
            var score = MovementFall.Warmth(campaign, candidate, nation, claimants) - distancePenalty +
                        (Movements.DefinitionOf(nation)?.Sponsors.ContainsKey(candidate.Tag) == true ? 60f : 0f) +
                        (nation.Movement?.InvitedBy.Contains(candidate.Id) == true ? 40f : 0f);
            if (score <= bestScore) continue;
            bestScore = score;
            best = MovementFall.SeatRegion(campaign, candidate, nation);
        }
        return best;
    }

    /// <summary>Its last region gone: back to a headquarters, in the friendliest country that will have it.</summary>
    private static void BecomeLandless(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        state.Landless = true;
        var refuge = BestRefuge(campaign, nation, null);
        if (refuge is not null) state.HeadquartersStateMapIndex = refuge.MapIndex;
        state.LastReport = Loc.Format("The last of its ground is lost; it goes on from {0}.", refuge?.DisplayName ?? "");
        Movements.Place(campaign);
    }

    /// <summary>Keep the status and refuge together when a treaty changes real ownership.</summary>
    internal static void SynchroniseTerritory(Campaign campaign, Nation nation)
    {
        if (nation?.Movement is not { } state) return;
        var landless = !campaign.HasOwnedLand(nation);
        if (!landless && state.Exile)
        {
            // A government in exile that has come home and holds land of its own resumes
            // full sovereign governance as a regular state.
            Restoration.RestoreExiledNation(campaign, nation);
            return;
        }
        if (state.Landless == landless) return;
        if (landless) BecomeLandless(campaign, nation);
        else
        {
            // Marked here, graduated on its own week (or at the end of the transfer that gave
            // it the ground): this runs on every map change, in the middle of transfers that
            // still need the movement to settle the ground they are handing it.
            state.Landless = false;
            if (state.StatehoodDay < 0) state.StatehoodDay = campaign.ElapsedDays;
        }
        campaign.MovementRevision++;
    }

    /// <summary>The transition the map itself declares: a movement with borders is a state.
    /// The tag, the name, the flag and the wars stay; the movement bookkeeping goes - all of
    /// it. A movement used to keep its movement while it held ground: Silesia governed a
    /// region for years as a party with cadres, recognitions and a terrorist listing, out of
    /// reach of everything a state does and with everything a movement does besides. What it
    /// claimed and does not hold yet stays its homeland, so the new state claims the rest of
    /// it as any state claims its irredenta; and the day it gained itself is the day it last
    /// seceded, which every "too young to do that" rule already reads.</summary>
    internal static void GraduateToStatehood(Campaign campaign, Nation nation, bool restoration = false)
    {
        var state = nation.Movement;
        if (state is null) return;
        var homeland = Movements.DefinitionOf(nation)?.Homeland ?? new List<string>();
        if (!campaign.Homelands.TryGetValue(nation.Id, out var homes)) campaign.Homelands[nation.Id] = homes = new List<int>();
        foreach (var id in homeland)
            if (Movements.StateById(campaign, id) is { } region && !homes.Contains(region.MapIndex)) homes.Add(region.MapIndex);
        foreach (var region in campaign.StatesOf(nation))
            if (!homes.Contains(region.MapIndex)) homes.Add(region.MapIndex);
        state.DesignatedBy.Clear();
        state.RecognisedBy.Clear();
        campaign.Fallen.Remove(nation.Id);
        nation.Movement = null;
        if (!restoration) nation.LastSecessionDay = campaign.ElapsedDays;
        nation.NameProclaimed = true;
        nation.PoliticsChangedDay = campaign.ElapsedDays;
        var held = campaign.StatesOf(nation);
        var seat = held.OrderByDescending(s => s.Population).FirstOrDefault();
        if (seat is not null)
        {
            CapitalCore.Establish(campaign, nation, seat);
        }
        campaign.LocateCapitals(preserveExisting: true);
        campaign.MovementRevision++;
        campaign.MarkStrategicChange();
        campaign.MarkMapChanged();
        campaign.Chronicle.Record(campaign, "movement_statehood", nation.Id, -1, nation.Name);
        DiagnosticLogs.Write("simulation",
            $"MOVEMENT_STATEHOOD nation={nation.Tag} regions={held.Length} day={campaign.ElapsedDays}");
    }

    internal static string RegionName(Campaign campaign, int mapIndex) =>
        campaign.States.Find(state => state.MapIndex == mapIndex)?.DisplayName ?? "";

    // ------------------------------------------------------------------ uprisings ------

    /// <summary>The chance an uprising in this region succeeds now, 0 to 1.</summary>
    public static float UprisingChance(Campaign campaign, Nation nation, ScenarioState region)
    {
        if (RevoltRecovery.DaysLeft(campaign, OwnerOf(campaign, region), region) > 0) return 0f;
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null || region is null || !MovementMethods.WillRise(campaign, nation, definition) ||
            !MovementReach.CanClaimTerritory(campaign, nation, region)) return 0f;
        var support = state.Support.GetValueOrDefault(region.MapIndex);
        if (!float.IsFinite(support) || !float.IsFinite(state.Cadres) || !float.IsFinite(state.Legitimacy) ||
            !float.IsFinite(region.Stability)) return 0f;
        var own = PeopleOf(region, definition, out _);
        var share = region.Population > 0 ? own / (float)region.Population : 0f;
        // A plain search, not the weekly table: the interface asks this too, from its own thread.
        Nation owner = null;
        foreach (var candidate in campaign.Nations)
            if (candidate.MapIndex == region.NationMapIndex) { owner = candidate; break; }

        // Who is there to stop it: the owner's divisions standing in the region.
        var topology = campaign.ProvinceMap;
        var garrison = 0f;
        if (topology is not null && owner is not null)
            foreach (var army in campaign.Armies)
                if (army.NationId == owner.Id && army.ProvinceId > 0 && army.ProvinceId <= topology.ProvinceCount &&
                    topology.ProvinceToState[army.ProvinceId] == region.MapIndex)
                    garrison += army.Divisions * army.Strength;

        // Fighters already hidden in the region count three times over: they are where it happens.
        var hidden = state.Infiltrated.GetValueOrDefault(region.MapIndex);
        var strength = MathF.Min(1f, (state.Cadres + hidden * 3f) / MathF.Max(1f, own * 0.01f));
        var chance = support * 0.7f * share + strength * 0.25f + (1f - region.Stability) * 0.25f - garrison * 0.06f;
        if (definition.Has("armed_wing")) chance += 0.08f;
        if (definition.Has("nonviolence")) chance = support * share * 0.5f + state.Legitimacy * 0.4f + (1f - region.Stability) * 0.2f - garrison * 0.02f;
        if (definition.Has("for_hire")) chance = MathF.Min(1f, nation.Treasury / 3f) * 0.6f + (1f - region.Stability) * 0.4f - garrison * 0.05f;
        // Below the following an uprising needs, the streets stay empty however restless the
        // region is: the chance grows with the support towards that line and is whole from it
        // on. A contract army needs no following, and is not held to one.
        else chance *= MathF.Min(1f, support / MathF.Max(0.01f, campaign.Defines.MovementUprisingSupport));
        return float.IsFinite(chance) ? M.Clamp(chance, 0.02f, 0.9f) : 0f;
    }

    /// <summary>Rise in this region. True when it now holds it.</summary>
    internal static bool Rise(Campaign campaign, Nation nation, ScenarioState region)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null || region is null || !MovementMethods.WillRise(campaign, nation, definition)) return false;
        var defines = campaign.Defines;
        var occupyingOwner = campaign.Nations.Find(candidate => candidate.MapIndex == region.NationMapIndex);
        if (RevoltTerritory.HasForeignOccupation(campaign, occupyingOwner, nation, region))
        {
            state.LastReport = Loc.Get("An uprising cannot displace foreign occupation without fighting.");
            return false;
        }
        var chance = UprisingChance(campaign, nation, region);
        if (!float.IsFinite(chance) || chance <= 0f || !float.IsFinite(nation.Treasury) ||
            nation.Treasury < defines.MovementUprisingCost) return false;
        var roll = Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, region.MapIndex);
        var owner = campaign.Nations.Find(candidate => candidate.MapIndex == region.NationMapIndex);
        nation.Treasury -= defines.MovementUprisingCost;
        state.Heat = M.Clamp01(state.Heat + 0.25f);

        if (roll >= chance)
        {
            var lost = definition.Has("tunnel_network") || definition.Has("nonviolence") ? 0.2f : 0.45f;
            state.Cadres *= 1f - lost;
            state.Support[region.MapIndex] = M.Clamp01(state.Support.GetValueOrDefault(region.MapIndex) - 0.12f);
            state.Legitimacy = M.Clamp01(state.Legitimacy - (definition.Has("nonviolence") ? 0.01f : 0.04f));
            region.Radicalism = M.Clamp01(region.Radicalism + 0.05f);
            if (owner is not null) OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, owner.Id, nation.Id), "rose_against_us");
            state.LastReport = Loc.Format("The uprising in {0} was put down.", region.DisplayName);
            return false;
        }

        // The flashpoint carried, so the homeland carries: the rest of the people's zone the
        // same government still holds rises with it instead of leaving confetti on the map.
        var taken = TakeZone(campaign, nation, region, owner);
        if (taken == 0) return false;
        if (state is not null && nation.Movement is not null)
            state.LastReport = taken > 1
                ? Loc.Format("The uprising in {0} succeeded: {1} region(s) are ours.", region.DisplayName, taken)
                : Loc.Format("The uprising in {0} succeeded: it is ours.", region.DisplayName);
        return true;
    }

    /// <summary>
    /// Success in one region of the homeland is success for the homeland. A rising that
    /// wins its flashpoint does not stop at the city limits, a referendum that carries does
    /// not ask the same question county by county for a decade, and an exile that comes
    /// home does not stop at the doorstep: the rest of the people's zone that the same
    /// holder still controls comes with the first region. Three brakes keep it honest —
    /// only regions the definition itself names as homeland (or a partition of one), only
    /// ground the same holder controls, and a vote frees a region only where the movement's
    /// own majority actually carried. A homeland chunk some other country holds stays where
    /// it is; that is a different war, for another day.
    /// </summary>
    /// <returns>The seed first, then the rest of the zone in map order, de-duplicated.</returns>
    internal static List<ScenarioState> ZoneOf(Campaign campaign, Nation nation, MovementDefinition definition,
        ScenarioState seed, Nation owner, bool peacefulVote, bool restoration = false)
    {
        var zone = new List<ScenarioState>();
        if (campaign is null || nation?.Movement is not { Dissolved: false } || definition is null ||
            seed is null || owner is null) return zone;
        zone.Add(seed);
        var ownerTotalStates = campaign.StatesOf(owner).Length;
        // Most votes are one question asked of the whole homeland - Scotland in 2014, Quebec in
        // 1995 - and a carried vote takes the whole of it, the regions that voted no along with
        // the ones that voted yes. Asked region by region it left one-county Silesias and
        // Scotlands for decades. Some settlements are drawn county by county all the same
        // (Ireland's partition, the 1920 Schleswig plebiscites): a quarter of them, decided
        // once per movement and flashpoint so a campaign replays its own map.
        var oneConstituency = peacefulVote &&
            Roll(campaign.Seed, nation.Id, seed.MapIndex, 0x5C0) >= campaign.Defines.CivilReferendumByRegionShare &&
            HomelandCarries(campaign, nation, definition, seed, owner);
        // Map order, so the same campaign always takes the same borders in the same sequence.
        foreach (var region in campaign.States.OrderBy(candidate => candidate.MapIndex))
        {
            if (region is null || region.MapIndex == seed.MapIndex) continue;
            if (region.NationMapIndex != owner.MapIndex) continue;
            if (region.Unclaimed || region.Population <= 0) continue;
            bool isHome = MovementReach.IsHomeland(campaign, definition, region) ||
                (campaign.Homelands.TryGetValue(nation.Id, out var homes) &&
                 (homes.Contains(region.MapIndex) || homes.Contains(TerritorialClaims.Origin(region))));
            if (!isHome) continue;
            // A chunk some other army stands on is a different war, not a free border.
            if (RevoltTerritory.HasForeignOccupation(campaign, owner, nation, region)) continue;
            if (peacefulVote)
            {
                var votes = CivilIndependence.ResidentSupport(nation, region);
                if (!float.IsFinite(votes) || (!oneConstituency && votes <= campaign.Defines.CivilReferendumMajority)) continue;
            }
            // A rising spreads where a rising could start. The flashpoint carries the rest of
            // the homeland only over ground the holder is not actually standing on: the army
            // in a region is what decides whether people there can rise at all, and handing a
            // garrisoned region over because a neighbouring city rose is how a country taken
            // and held by ninety divisions was given back to a movement that had won one town.
            // A restoration is exempt: it is a government returning to its own ground, by
            // agreement or on the bayonets of the army it already has there.
            else if (!restoration && !CanCarry(campaign, nation, region, owner)) continue;
            // Peaceful secession must not dissolve the parent state entirely without a war.
            if (peacefulVote && zone.Count + 1 >= ownerTotalStates)
                continue;
            zone.Add(region);
        }
        // A vote frees each region that carried it, joined up or not; a rising holds what it
        // can reach from the flashpoint.
        return peacefulVote ? zone : Connected(campaign, nation, zone);
    }

    /// <summary>
    /// Whether the homeland this government holds, counted as one constituency, votes for
    /// independence: the residents' support weighted by how many live in each region.
    /// </summary>
    private static bool HomelandCarries(Campaign campaign, Nation nation, MovementDefinition definition,
        ScenarioState seed, Nation owner)
    {
        double people = 0, yes = 0;
        foreach (var region in campaign.States)
        {
            if (region is null || region.NationMapIndex != owner.MapIndex || region.Unclaimed || region.Population <= 0) continue;
            if (region.MapIndex != seed.MapIndex && !MovementReach.IsHomeland(campaign, definition, region)) continue;
            var votes = CivilIndependence.ResidentSupport(nation, region);
            if (!float.IsFinite(votes)) continue;
            people += region.Population;
            yes += region.Population * (double)votes;
        }
        return people > 0 && yes / people > campaign.Defines.CivilReferendumMajority;
    }

    /// <summary>
    /// Whether a rising that won its flashpoint carries this region with it.
    ///
    /// Two things it must be able to do there: start at all - the chance an uprising in the
    /// region succeeds is above nothing - and find the state not standing on it with a formed
    /// division. A garrison is the state's answer to a rising, and ground it is standing on is
    /// taken from it by fighting, not by a neighbouring city's success. A country of twenty-four
    /// regions held by ninety divisions used to come back to its movement whole after one town
    /// changed hands; no army can be said to have defended ground it lost without a battle.
    /// </summary>
    private static bool CanCarry(Campaign campaign, Nation nation, ScenarioState region, Nation owner)
    {
        if (RevoltRecovery.DaysLeft(campaign, owner, region) > 0) return false;
        var def = Movements.DefinitionOf(nation);
        var isHomeland = def is not null && MovementReach.IsHomeland(campaign, def, region);
        if (!isHomeland && UprisingChance(campaign, nation, region) <= 0f) return false;
        var topology = campaign.ProvinceMap;
        if (topology is null || owner is null) return true;
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != owner.Id || army.InBattle || army.ProvinceId <= 0 ||
                army.ProvinceId > topology.ProvinceCount) continue;
            if (topology.ProvinceToState[army.ProvinceId] != region.MapIndex) continue;
            if (army.Divisions * army.Strength < 1f) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The part of a zone that hangs together: the seed and every zone region that can be reached
    /// from it over zone regions or ground the nation already holds. A homeland scattered across
    /// a map - by a war, or by a homeland that drifted - rose as one act and left snakes and
    /// islands; the rest waits for its own flashpoint. A referendum is not held to this: each
    /// region that votes to leave has chosen for itself.
    /// </summary>
    private static List<ScenarioState> Connected(Campaign campaign, Nation nation, List<ScenarioState> zone)
    {
        var map = campaign.ProvinceMap;
        if (map is null || zone.Count <= 1) return zone;
        var wanted = new HashSet<int>();
        foreach (var region in zone) wanted.Add(region.MapIndex);
        var held = new HashSet<int>();
        foreach (var region in campaign.StatesOf(nation)) held.Add(region.MapIndex);
        var links = new Dictionary<int, HashSet<int>>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            int here = map.ProvinceToState[province];
            if (!wanted.Contains(here) && !held.Contains(here)) continue;
            foreach (var next in map.Neighbors[province])
            {
                int there = map.ProvinceToState[next];
                if (there == here || (!wanted.Contains(there) && !held.Contains(there))) continue;
                if (!links.TryGetValue(here, out var set)) links[here] = set = new HashSet<int>();
                set.Add(there);
            }
        }
        var reached = new HashSet<int> { zone[0].MapIndex };
        var queue = new Queue<int>();
        queue.Enqueue(zone[0].MapIndex);
        while (queue.Count > 0)
            if (links.TryGetValue(queue.Dequeue(), out var next))
                foreach (var region in next)
                    if (reached.Add(region)) queue.Enqueue(region);
        return zone.Where(region => reached.Contains(region.MapIndex)).ToList();
    }

    /// <summary>
    /// The whole zone of interest changes hands in one act: the seed region and every other
    /// homeland region the same holder still has. The regions are annexed one by one - each
    /// through the ordinary border machinery, so its people and its output are counted once
    /// under the new flag - and the act is settled once at the end
    /// (<see cref="SettleTransfer"/>): one militia, one war, one proclamation.
    /// </summary>
    /// <returns>How many regions it took; 0 when not even the seed could be taken.</returns>
    internal static int TakeZone(Campaign campaign, Nation nation, ScenarioState seed, Nation owner,
        bool peacefully = false, bool restoration = false)
    {
        var definition = Movements.DefinitionOf(nation);
        if (definition is null || seed is null) return 0;
        var zone = ZoneOf(campaign, nation, definition, seed, owner, peacefulVote: peacefully && !restoration, restoration: restoration);
        // The flashpoint comes first and must take: a rising that lost in its own city did not
        // carry the country, whatever the rest of the zone would have done.
        if (!TakeRegion(campaign, nation, seed, owner, peacefully, restoration, settle: false)) return 0;
        var gained = new List<ScenarioState>(zone.Count) { seed };
        foreach (var region in zone)
            if (region.MapIndex != seed.MapIndex &&
                TakeRegion(campaign, nation, region, owner, peacefully, restoration, settle: false))
                gained.Add(region);
        SettleTransfer(campaign, nation, gained[0], gained, owner, peacefully, restoration);
        return gained.Count;
    }

    /// <summary>
    /// The part of a garrison that changes flags with the ground, divided ONCE for the whole
    /// of what is being taken.
    ///
    /// The share lives in <see cref="Reintegration.Defect"/> and is a share of what actually
    /// stands on the ground being given up - an agreed referendum divides the army as surely
    /// as a contested one, only quietly. It must be asked once per transfer, never once per
    /// region: each call takes its slice of what the last one left, and a call that finds no
    /// garrison on its own region falls back to a cross-section of the owner's whole army,
    /// so a zone of twenty-four regions could empty an occupier's order of battle into a
    /// single uprising. Measured on the 2024 world before this: Russia annexed Ukraine, an
    /// uprising followed, and eighty-three of its ninety-one formations left in one act.
    /// </summary>
    private static int DefectGarrison(Campaign campaign, Nation nation, Nation owner,
        IReadOnlyCollection<int> provinces, bool peacefully)
    {
        if (campaign is null || nation is null || owner is null || provinces is null ||
            provinces.Count == 0) return 0;
        var defected = Reintegration.Defect(campaign, owner, nation, provinces.ToHashSet(),
            peacefully ? campaign.Defines.PeacefulSecessionDefectionShare : null);
        if (defected > 0)
            DiagnosticLogs.Write("simulation",
                $"MOVEMENT_DEFECTION nation={nation.Tag} from={owner.Tag} formations={defected} peaceful={peacefully}");
        return defected;
    }

    /// <summary>
    /// One region changes hands: the ground, the people on it and its output, through the same
    /// border machinery every other transfer uses. What follows a successful act - militia out
    /// of the cadres, the war with the country it rose against, the movement becoming a
    /// country - belongs to the act rather than to one region, so a caller taking a whole zone
    /// passes <paramref name="settle"/> false for every region and settles once at the end
    /// (<see cref="TakeZone"/>).
    /// </summary>
    internal static bool TakeRegion(Campaign campaign, Nation nation, ScenarioState region, Nation owner,
        bool peacefully = false, bool restoration = false, bool settle = true)
    {
        // A restoration is its own kind of return: the country is already the country it was, so
        // it is neither a referendum (peaceful) nor an uprising (armed). Restoration guards it.
        // Whole-region movement settlements must not overwrite a third party's occupation,
        // including restorations and peaceful referendums. They need an actual military settlement.
        if (!peacefully && !restoration && RevoltRecovery.DaysLeft(campaign, owner, region) > 0) return false;
        if (RevoltTerritory.HasForeignOccupation(campaign, owner, nation, region)) return false;
        if (settle)
        {
            if (peacefully && !restoration && CivilIndependence.Refusal(campaign, nation, region).Length != 0) return false;
            if (!peacefully && !restoration && !MovementReach.CanClaimTerritory(campaign, nation, region)) return false;
        }
        else
        {
            // Inside TakeZone: the seed region already verified political/military mandate.
            // Secondary regions in the same homeland zone under the same owner need only
            // verify they are valid homeland ground with no foreign occupation and sane data.
            var def = Movements.DefinitionOf(nation);
            if (!restoration && def is not null && !MovementReach.IsHomeland(campaign, def, region) &&
                !(campaign.Homelands.TryGetValue(nation.Id, out var h) && (h.Contains(region.MapIndex) || h.Contains(TerritorialClaims.Origin(region)))))
                return false;
        }
        if (campaign?.ProvinceMap is null || nation?.Movement is null || region is null ||
            !float.IsFinite(region.Stability) || !float.IsFinite(region.StandardOfLiving) ||
            !float.IsFinite(region.Literacy) || !float.IsFinite(nation.Gdp) ||
            owner is not null && !float.IsFinite(owner.Gdp)) return false;
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        var topology = campaign.ProvinceMap;
        // Record the region's real economy before ownership changes. A movement's
        // donations GDP and zero population are not the tax base of its new country.
        var wasLandless = nation.Landless;
        var regionalGdp = RegionalStatistics.EstimatedGdp(campaign, region);
        var previousGdp = wasLandless ? 0f : nation.Gdp;
        var incomingScale = owner?.OutputScale ?? nation.OutputScale;
        var provinces = new List<int>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToState[province] == region.MapIndex) provinces.Add(province);

        StatePartition.Annex(campaign, provinces, nation, asConquest: false);
        // The ground changes hands, and so does the part of the garrison standing on it: an
        // agreed referendum divides the army as surely as a contested one, only quietly. That
        // division belongs to the ACT, not to the region, and is made once in SettleTransfer -
        // which every path reaches, with one region or with a whole zone. It used to be made
        // here for every region: the shares compounded, and the fallback that hands over a
        // cross-section when no garrison stood in one region fired for every such region in
        // turn, so taking a homeland of twenty-four regions could move most of the old owner's
        // army, including formations nowhere near it.
        var heldStates = campaign.StatesOf(nation);
        nation.Population = heldStates.Sum(s => s.Population);
        nation.Gdp = MathF.Max(.01f, previousGdp + regionalGdp);
        // Donations are not industrial capacity. The first real tax base provides the
        // reference; subsequent price changes must never rebase it each economic week.
        if (wasLandless)
        {
            nation.ProductionReferenceInitialized = false;
            ProductionCapacity.Initialize(nation);
        }
        // Blend calibration for successive acquisitions, preserving the inherited
        // production value instead of converging from a fictitious tiny economy.
        var rawAnnual = previousGdp / MathF.Max(.0001f, nation.OutputScale) +
                        regionalGdp / MathF.Max(.0001f, incomingScale);
        if (rawAnnual > .0001f) nation.OutputScale = nation.Gdp / rawAnnual;
        if (wasLandless && owner is not null) nation.Productivity = owner.Productivity;
        if (nation.Population > 0)
        {
            nation.StandardOfLiving = (float)(heldStates.Sum(s => s.StandardOfLiving * (double)s.Population) / nation.Population);
            nation.Literacy = (float)(heldStates.Sum(s => s.Literacy * (double)s.Population) / nation.Population);
        }
        if (owner is not null && owner != nation)
        {
            owner.Gdp = MathF.Max(.01f, owner.Gdp - regionalGdp);
            owner.Population = campaign.StatesOf(owner).Sum(s => s.Population);
        }
        state.Landless = false;
        if (state.StatehoodDay < 0) state.StatehoodDay = campaign.ElapsedDays;
        // A restoration is not a founding: the country returns under the name it fell with,
        // the way it keeps the government it had. Only a movement that takes ground for the
        // first time is naming something new.
        if (!restoration)
        {
            var renamed = false;
            if (!string.IsNullOrWhiteSpace(definition.StateName))
            {
                nation.Name = definition.StateName;
                renamed = true;
            }
            else if (definition.Peoples.Count > 0 && DemographyCatalog.Culture(definition.Peoples[0]) is { Country.Length: > 0 } peopleState)
            {
                nation.Name = peopleState.Country;
                renamed = true;
            }
            else if (wasLandless && region is not null && !string.IsNullOrWhiteSpace(region.Name))
            {
                nation.Name = region.Name;
                renamed = true;
            }
            if (renamed) nation.NameProclaimed = true;
        }
        var seat = campaign.StatesOf(nation).OrderByDescending(candidate => candidate.Population).FirstOrDefault();
        if (wasLandless && seat is not null) CapitalCore.Establish(campaign, nation, seat);
        campaign.LocateCapitals(preserveExisting: true);
        // A restoration keeps the government the country had when it fell; nothing was founded anew.
        if (!restoration)
        {
            nation.Government = !string.IsNullOrWhiteSpace(definition.Government) && definition.Government != "provisional_government"
                ? definition.Government
                : peacefully ? "parliamentary_republic" : "presidential_republic";
            nation.StartGovernment = "";
            nation.StartIdeology = "";
        }
        nation.PoliticsChangedDay = campaign.ElapsedDays;

        if (settle) SettleTransfer(campaign, nation, region, new[] { region }, owner, peacefully, restoration);
        return true;
    }

    /// <summary>
    /// What a successful act does to a movement the day it wins ground: cadres and the street
    /// become its militia, the tunnels are dug in, the war with the country it rose against is
    /// declared once, and ground of its own makes it a country. It runs once per act however
    /// many regions came with it - a rising that carries a homeland raises one militia, fights
    /// one war and proclaims one state - so the regions are passed whole and their supporters
    /// and hidden fighters are counted together.
    /// </summary>
    private static void SettleTransfer(Campaign campaign, Nation nation, ScenarioState seed,
        IReadOnlyList<ScenarioState> gained, Nation owner, bool peacefully, bool restoration)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null || seed is null) return;
        var defines = campaign.Defines;

        // The garrison changes flags once for the whole transfer, not once per region
        // (DefectGarrison): the share must be a share of the army that stood on the ground
        // given up, not a slice of what the previous region left behind.
        var ground = new HashSet<int>();
        if (owner is not null && owner.Id != nation.Id)
        {
            var topology = campaign.ProvinceMap;
            var regions = new HashSet<ushort>();
            foreach (var region in gained)
                if (region is not null) regions.Add((ushort)region.MapIndex);
            if (topology is not null)
                for (var province = 1; province <= topology.ProvinceCount; province++)
                    if (regions.Contains(topology.ProvinceToState[province])) ground.Add(province);
            DefectGarrison(campaign, nation, owner, ground, peacefully);
        }

        // The cadres become the country's militia, and so does the street: every supporter in
        // the ground that came over is somebody who can be handed a rifle, and the fighters
        // sent in ahead are already there. The tunnels and the camps give them ground to hold.
        var perFormation = defines.MovementMilitiaPerCadres * (definition.Has("armed_wing") ? 0.7f : 1f);
        var street = 0f;
        var hidden = 0f;
        foreach (var region in gained)
        {
            street += MovementRules.PeopleOf(region, definition, out _) * state.Support.GetValueOrDefault(region.MapIndex);
            hidden += state.Infiltrated.GetValueOrDefault(region.MapIndex);
            state.Infiltrated.Remove(region.MapIndex);
        }
        var formations = Math.Min(defines.MovementUprisingMilitiaMax,
            (int)(state.Cadres / MathF.Max(1f, perFormation)) +
            (int)(street / MathF.Max(1f, defines.MovementMilitiaPerSupporters)) +
            (int)(hidden / MathF.Max(1f, perFormation * 0.5f)));
        // A nonviolent movement carries no rifles at all - a ballot campaign that rose at the
        // polls governs by politics, and an empty formation standing in its square is a lie
        // the militia ledger then has to feed.
        if (definition.Has("nonviolence")) formations = 0;
        if (peacefully || restoration) formations = 0;
        var raised = 0;
        foreach (var held in campaign.StatesOf(nation))
            for (var attempt = 0; attempt < formations && raised < formations; attempt++)
                if (MilitiaCore.Found(campaign, nation, held)) raised++;
        if (definition.Has("tunnel_network"))
            foreach (var army in campaign.Armies)
                if (army.NationId == nation.Id) army.Entrenchment = 1f;
        // Only the formations the cadres stood up cost cadres; the street's are the street's.
        var fromCadres = Math.Min(raised, (int)(state.Cadres / MathF.Max(1f, perFormation)));
        state.Cadres = MathF.Max(0f, state.Cadres - fromCadres * perFormation * 0.5f);

        if (owner is not null && !peacefully && !restoration)
        {
            SecessionResponse.Begin(campaign, owner, nation);
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, owner.Id, nation.Id), "rose_against_us");
            // It holds ground of its own since this morning, which the day's border table cannot know.
            CampaignDiplomacy.ForgetBorders();
            if (owner.IsHuman && !campaign.ObserverMode)
                SecessionResponse.Ask(campaign, owner, nation,
                    string.Join(", ", gained.Take(4).Select(r => r.DisplayName)), gained.Sum(r => r.Population), owner.Population + nation.Population);
            else if (CampaignDiplomacy.DeclareWar(campaign, owner.Id, nation.Id, seed.MapIndex, "uprising", onOwnInitiative: false) is not null)
                // The garrison that did not go over goes home or holds (StrandedGarrisons).
                StrandedGarrisons.AtRising(campaign, owner, nation, ground);
        }
        // Remember the newly liberated regions as the nation's homeland so restorations and title checks recognize them.
        if (!campaign.Homelands.TryGetValue(nation.Id, out var homes))
            campaign.Homelands[nation.Id] = gained.Select(r => r.MapIndex).ToList();
        else
            foreach (var r in gained)
                if (!homes.Contains(r.MapIndex)) homes.Add(r.MapIndex);
        // The borders are on the map, so the movement is done being a movement: ground of
        // its own makes it a country at once (the same rule SynchroniseTerritory applies a
        // week later to ground gained any other way). What follows - wars, peace, exile -
        // happens to it as to any other state.
        GraduateToStatehood(campaign, nation, restoration);
        campaign.MovementRevision++;
        campaign.MarkStrategicChange();
    }

    internal static float Roll(int seed, int nationId, int day, int salt)
    {
        var hash = (uint)seed * 2654435761u ^ (uint)nationId * 2246822519u ^ (uint)day * 3266489917u ^ (uint)salt * 668265263u;
        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        hash *= 3266489917u;
        hash ^= hash >> 16;
        return (hash >> 8) * (1f / 16777216f);
    }
}
