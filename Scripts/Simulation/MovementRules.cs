using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Movements: what a rebellion, a resistance, a separatist drive or a civil-rights
/// campaign does, week by week — from leaflets to militias to a state of its own.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — how movements work, from calm to armed:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// A MOVEMENT is what a people becomes when it organises against its government:
/// Hamas, the SNP, the Houthi, Scottish independence. It lives in a region, with
/// supporters, cadres, and a "heat" (how much the host state is cracking down).
///
/// THE LADDER (what a movement can do, in escalating order):
///
///   Leaflets     — support grows by itself if the region is angry.
///   Campaign     — a push for support: costs money and heat, raises support.
///   Fundraise    — donations from supporters, in money.
///   Recruit      — converts supporters into cadres (the fighters and organisers).
///   Recognition  — other countries recognise it as legitimate (opens diplomacy).
///   Uprising     — the armed option: needs support ≥ 40%, ≥ 3000 cadres, costs money.
///                 If it succeeds: the region's ground, militias, and a war.
///   Call to arms — a held region raises more militia from its own supporters.
///   Infiltrate   — sends fighters into a region from outside.
///
/// WHAT MOVES SUPPORT: the region's grievances (radicalism, poverty, discrimination,
/// kin across the border), the movement's campaigns, the state's response. Support
/// drifts toward what the grievances make of it — a quiet region stays quiet.
///
/// WHAT THE STATE CAN DO: crack down (raises heat, which can expel the headquarters),
/// list it as terrorist (cuts donations), or address the grievance (support falls).
///
/// WHAT A MOD ADDS: a movement in `game/common/movements/*.json` — who, where,
/// what it wants, its starting supporters and cadres, and optional traits that
/// bend these rules (non-violence, foreign patron, religious base).
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
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
///
/// Split cleanly across partial modules:
/// - <c>MovementRules.cs</c>: weekly tick orchestration, support drift, finances, cadres, standing.
/// - <c>MovementRules.PeopleCache.cs</c>: per-campaign demography cache, raw string interning &amp; lookup.
/// - <c>MovementRules.PeopleIndex.cs</c>: full &amp; incremental region/culture/religion index builders.
/// - <c>MovementRules.Territory.cs</c>: exile/refuge, statehood graduation, uprisings &amp; territorial transfers.
/// </summary>
public static partial class MovementRules
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
            Movements.RetireDuplicateRepresentations(campaign);
        using (var lifecycle = MovementLifecycle.TickStages(campaign).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("movements"))
                using (SimProfiler.Measure("  mov:rules"))
                    more = lifecycle.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        // Keep the weekly review in order, yielding between falls and whole settlements.
        if (campaign.ElapsedDays % week == 0)
        {
            using (SimProfiler.Measure("movements"))
            using (SimProfiler.Measure("  mov:rules"))
            using (SimProfiler.Measure("    mov:homelands"))
            {
                Owners(campaign, rebuild: true);
                MovementFall.RememberHomelands(campaign);
            }
            yield return 0;
            using var review = WeeklyFallStages(campaign).GetEnumerator();
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("movements"))
                using (SimProfiler.Measure("  mov:rules"))
                using (SimProfiler.Measure("    mov:fall"))
                    more = review.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        yield return 0;
        var hasActiveMovement = false;
        var active = campaign.ActiveNations;
        for (var i = 0; i < active.Count; i++)
            if (active[i].Movement is { Dissolved: false }) { hasActiveMovement = true; break; }
        if (hasActiveMovement)
            foreach (var _ in EnsurePeopleIndexedStages(campaign, insideMovRules: true))
                yield return 0;
        // Each movement's own week on a day of its own. They all used to fall on the same
        // day, so their cost did not spread: one frame in seven carried every movement's week
        // at once, the largest hitch in a late campaign at the top speed. Every movement still
        // has exactly one week in any seven days - on its day rather than on everybody's.
        // An uprising can found a nation mid-pass, so walk the pinned snapshot array;
        // founders join on their next day rather than mid-list.
        for (var i = 0; i < active.Count; i++)
        {
            var nation = active[i];
            if (nation.Movement is not { Dissolved: false } || !OnItsDay(campaign, nation.Id, week)) continue;
            if (NeedsPeopleIndex(campaign))
                foreach (var _ in EnsurePeopleIndexedStages(campaign, insideMovRules: true))
                    yield return 0;
            var first = true;
            using (var weekPass = WeekStages(campaign, nation).GetEnumerator())
            {
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("movements"))
                    using (SimProfiler.Measure("  mov:rules"))
                    {
                        if (first) { Owners(campaign); first = false; }
                        using (SimProfiler.Measure("    mov:week")) more = weekPass.MoveNext();
                    }
                    if (!more) break;
                    yield return 0;
                }
            }
            using (SimProfiler.Measure("movements"))
            using (SimProfiler.Measure("  mov:rules"))
            using (SimProfiler.Measure("    mov:endure"))
                MovementFall.Endure(campaign, nation);
            yield return 0;
        }
    }

    private static IEnumerable<int> WeeklyFallStages(Campaign campaign)
    {
        foreach (var _ in MovementFall.DecideStages(campaign))
            yield return 0;
        yield return 0;
        foreach (var _ in MovementFall.EndLostWarsStages(campaign))
            yield return 0;
    }

    /// <summary>Whether today is this one's day in a cycle of so many days.</summary>
    internal static bool OnItsDay(Campaign campaign, int id, int cycle) =>
        (campaign.ElapsedDays + (id % cycle + cycle) % cycle) % cycle == 0;

    private static readonly Dictionary<int, Nation> EmptyOwners = new();

    internal static Dictionary<int, Nation> Owners(Campaign campaign, bool rebuild = false) =>
        campaign?.NationsByMapIndex() ?? EmptyOwners;

    internal static Nation OwnerOf(Campaign campaign, ScenarioState region) =>
        region is not null && campaign != null ? campaign.NationByMapIndex(region.NationMapIndex) : null;

    [ThreadStatic] private static Dictionary<int, bool> _ownStateCache;
    [ThreadStatic] private static Dictionary<int, bool> _atWarWithCache;
    [ThreadStatic] private static Dictionary<int, float> _esteemCache;
    [ThreadStatic] private static HashSet<int> _liveRegions;
    [ThreadStatic] private static List<int> _staleSupport;
    [ThreadStatic] private static List<int> _sponsorKeysScratch;
    [ThreadStatic] private static List<int> _refugeClaimantsScratch;
    [ThreadStatic] private static List<int> _infiltratedKeys;

    internal static void Week(Campaign campaign, Nation nation)
    {
        foreach (var _ in WeekStages(campaign, nation)) { }
    }

    internal static IEnumerable<int> WeekStages(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null) yield break;
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
        if (state is null) yield break;

        if (NeedsPeopleIndex(campaign))
        {
            yield return 0;
            foreach (var _ in EnsurePeopleIndexedStages(campaign, insideMovRules: true))
                yield return 0;
            yield return 0;
        }

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
        Dictionary<ScenarioState, (long Own, long Faithful)> people;
        using (SimProfiler.Measure("      mov:people"))
            people = PeopleIn(campaign, definition);
        yield return 0;
        var ownStateCache = _ownStateCache ??= new Dictionary<int, bool>();
        ownStateCache.Clear();
        var liveRegions = _liveRegions ??= new HashSet<int>();
        liveRegions.Clear();
        // Which owners it is at war with, remembered for the walk below: nothing in the walk
        // declares or ends a war, and the answer is the owner's, not the region's.
        var atWarWith = _atWarWithCache ??= new Dictionary<int, bool>();
        atWarWith.Clear();
        var esteemCache = _esteemCache ??= new Dictionary<int, float>();
        esteemCache.Clear();
        var isCivicIndependence = definition.Has("civic_independence");
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
                var supportCeiling = MovementReach.SupportCeiling(campaign, nation, region, definition, own, faithful, owner, atWarWith);
                if (supportCeiling <= 0f) continue;
                liveRegions.Add(region.MapIndex);
                believers += faithful;
                var support = state.Support.GetValueOrDefault(region.MapIndex);
                var foreign = false;
                if (owner is not null && owner.Id != nation.Id)
                {
                    if (!ownStateCache.TryGetValue(owner.Id, out var theirs)) ownStateCache[owner.Id] = theirs = OwnState(owner, definition);
                    foreign = !theirs;
                }
                var foreignRule = foreign ? 0.12f : 0f;
                var grievance = M.Clamp01(region.Radicalism * 0.6f + (1f - region.Stability) * 0.25f +
                                          region.WarDamage * 0.4f + foreignRule);
                var civic = isCivicIndependence ? CivilIndependence.AwarenessTarget(campaign, definition, state, region, owner) : 0f;
                var esteemKey = owner?.Id ?? -1;
                if (!esteemCache.TryGetValue(esteemKey, out var rawEsteem))
                    esteemCache[esteemKey] = rawEsteem = SocialMemory.Esteem(campaign, owner, nation);
                var reputation = rawEsteem * defines.SocialMovementWeight;
                var target = M.Clamp(MathF.Max(civic, 0.05f + state.Legitimacy * 0.2f + grievance * 0.5f) + reputation, 0f, MathF.Min(.75f, supportCeiling));
                support += (target - support) * defines.MovementSupportDrift;
                state.Support[region.MapIndex] = M.Clamp(support, 0f, supportCeiling);
                supporters += (long)(own * support);
            }
            if (state.Support.Count > liveRegions.Count)
            {
                var staleSupport = _staleSupport ??= new List<int>();
                staleSupport.Clear();
                foreach (var key in state.Support.Keys)
                    if (!liveRegions.Contains(key)) staleSupport.Add(key);
                for (var si = 0; si < staleSupport.Count; si++) state.Support.Remove(staleSupport[si]);
            }
        }
        var fromFaith = (long)(believers * defines.MovementFaithShare * 0.1f);

        // Fighters sent ahead arrive, and the ones already hidden drift away a little each week.
        for (var index = state.OnTheWay.Count - 1; index >= 0; index--)
        {
            var journey = state.OnTheWay[index];
            if (journey.ArrivesOnDay > campaign.ElapsedDays) continue;
            state.OnTheWay.RemoveAt(index);
            var arrived = campaign.StateByMapIndex(journey.Region);
            if (arrived is null || arrived.NationMapIndex == nation.MapIndex) { state.Cadres += journey.Fighters; continue; }
            state.Infiltrated[journey.Region] = state.Infiltrated.GetValueOrDefault(journey.Region) + journey.Fighters;
            state.LastReport = Loc.Format("{0} fighters are in place in {1}.", Fmt.People(journey.Fighters), arrived.DisplayName);
        }
        if (state.Infiltrated.Count > 0)
        {
            var keys = InfiltratedScratchKeys();
            keys.Clear();
            foreach (var region in state.Infiltrated.Keys) keys.Add(region);
            for (var ki = 0; ki < keys.Count; ki++)
            {
                var region = keys[ki];
                var left = state.Infiltrated[region] * (1f - defines.MovementInfiltrateDecay);
                if (left < 50f) state.Infiltrated.Remove(region);
                else state.Infiltrated[region] = left;
            }
        }

        // Money: donations from its following, and its sponsors while they think well of it.
        var donations = (supporters + fromFaith) * defines.MovementDonationPerSupporter * (0.5f + state.Legitimacy);
        if (definition.Has("diaspora_lobby")) donations *= 1.6f;
        // Every government that lists it as terrorist closes some of the doors money came through.
        var listed = 0;
        foreach (var id in state.DesignatedBy)
            if (campaign.NationById(id) is not null) listed++;
        donations *= MathF.Max(0.3f, 1f - listed * defines.MovementDesignationDonations);
        if (listed > 0) state.Legitimacy = M.Clamp01(state.Legitimacy - 0.0004f * listed);
        var sponsored = 0f;
        // And the countries that have chosen to fund it pay by the week, while they can and are not at war with it.
        if (state.SponsoredBy.Count > 0)
        {
            var sponsorKeys = _sponsorKeysScratch ??= new List<int>();
            sponsorKeys.Clear();
            foreach (var key in state.SponsoredBy.Keys) sponsorKeys.Add(key);
            for (var si = 0; si < sponsorKeys.Count; si++)
            {
                var id = sponsorKeys[si];
                if (!state.SponsoredBy.TryGetValue(id, out var perYear)) continue;
                var sponsor = campaign.NationById(id);
                if (sponsor is null || sponsor.Movement is not null || CampaignDiplomacy.AtWar(campaign, id, nation.Id) ||
                    campaign.StatesOf(sponsor).Length == 0)
                {
                    state.SponsoredBy.Remove(id);
                    continue;
                }
                var paid = MathF.Min(perYear / 52f, MathF.Max(0f, sponsor.Treasury) * 0.01f);
                { var ledgerBefore = sponsor.Treasury; sponsor.Treasury -= paid; MoneyLedger.Book(sponsor, MoneyFlow.Movements, sponsor.Treasury - ledgerBefore); }
                sponsored += paid;
            }
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
                !InLiveWar(campaign, client.Id))
            {
                state.ContractNationId = -1;
                state.ContractUntilDay = -1;
            }
            else
            {
                var fee = MathF.Min(client.Treasury * 0.002f, 0.01f);
                { var ledgerBefore = client.Treasury; client.Treasury -= fee; MoneyLedger.Book(client, MoneyFlow.Movements, client.Treasury - ledgerBefore); }
                sponsored += fee;
            }
        }
        state.DonationsWeekly = donations;
        state.SponsorsWeekly = sponsored;
        state.IncomeWeekly = donations + sponsored;
        { var ledgerBefore = nation.Treasury; nation.Treasury += state.IncomeWeekly; MoneyLedger.Book(nation, MoneyFlow.Movements, nation.Treasury - ledgerBefore); }

        // Cadres come from the following, up to what the following can give.
        var recruiting = supporters * defines.MovementRecruitPerSupporter * (definition.Has("refugee_camps") ? 2f : 1f);
        var ceiling = MathF.Max(state.Cadres, (supporters + fromFaith) * defines.MovementCadresPerSupporter);
        state.Cadres = MathF.Min(ceiling, state.Cadres + recruiting);
        if (state.Landless) nation.ManpowerReserve = state.Cadres;

        // Standing: recognition keeps it up, and a movement nobody recognises fades.
        var recognised = 0;
        foreach (var id in state.RecognisedBy)
            if (campaign.NationById(id) is not null) recognised++;
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

    private static List<int> InfiltratedScratchKeys() => _infiltratedKeys ??= new List<int>();

    private static bool InLiveWar(Campaign campaign, int nationId)
    {
        foreach (var war in Warfare.LiveWars(campaign))
            if (war.Involves(nationId)) return true;
        return false;
    }

    /// <summary>Whether a country is one its peoples would call their own.</summary>
    internal static bool OwnState(Nation owner, MovementDefinition definition)
    {
        var peoples = definition.Peoples;
        var founded = owner.FoundedCultures;
        for (var pi = 0; pi < peoples.Count; pi++)
        {
            var people = peoples[pi];
            if (founded is not null)
            {
                for (var fi = 0; fi < founded.Count; fi++)
                    if (string.Equals(founded[fi], people, StringComparison.OrdinalIgnoreCase)) return true;
            }
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
        var nations = c.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var movement = nations[i];
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
        var claimants = _refugeClaimantsScratch ??= new List<int>();
        claimants.Clear();
        var def = Movements.DefinitionOf(nation);
        var homeland = def?.Homeland;
        if (homeland is not null)
        {
            for (var i = 0; i < homeland.Count; i++)
                if (Movements.StateById(campaign, homeland[i]) is { } claimed && OwnerOf(campaign, claimed) is { } holder && holder.Id != nation.Id)
                    claimants.Add(holder.Id);
        }
        // Where it is from: the middle of the first homeland region, if it has one. A refuge is
        // chosen near that rather than wherever the warmest welcome happens to be - exiles go to
        // the neighbours and to their own part of the world, London and Warsaw, not Havana.
        (float Latitude, float Longitude)? home = null;
        if (homeland is { Count: > 0 } && Movements.StateById(campaign, homeland[0]) is { } homeRegion)
            home = Movements.CentreOf(campaign, homeRegion);
        ScenarioState best = null;
        var bestScore = float.MinValue;
        for (var i = 0; i < campaign.Nations.Count; i++)
        {
            var candidate = campaign.Nations[i];
            if (candidate == except || candidate.Id == nation.Id || candidate.Movement is not null) continue;
            if (CampaignDiplomacy.AtWar(campaign, candidate.Id, nation.Id)) continue;
            if (!campaign.HasOwnedLand(candidate)) continue;
            var distancePenalty = home is { } at
                ? MathF.Min(60f, NavalOperations.Distance(at.Latitude, at.Longitude, candidate.CapitalLatitude, candidate.CapitalLongitude) * 0.9f)
                : 0f;
            var score = MovementFall.Warmth(campaign, candidate, nation, claimants) - distancePenalty +
                        (def?.Sponsors.ContainsKey(candidate.Tag) == true ? 60f : 0f) +
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
        var homeland = Movements.DefinitionOf(nation)?.Homeland;
        if (!campaign.Homelands.TryGetValue(nation.Id, out var homes)) campaign.Homelands[nation.Id] = homes = new List<int>();
        if (homeland is not null)
        {
            for (var i = 0; i < homeland.Count; i++)
                if (Movements.StateById(campaign, homeland[i]) is { } region && !homes.Contains(region.MapIndex))
                    homes.Add(region.MapIndex);
        }
        var held = campaign.StatesOf(nation);
        for (var i = 0; i < held.Length; i++)
            if (!homes.Contains(held[i].MapIndex)) homes.Add(held[i].MapIndex);
        state.DesignatedBy.Clear();
        state.RecognisedBy.Clear();
        campaign.Fallen.Remove(nation.Id);
        if (!state.Exile && !state.Id.StartsWith(MovementCatalog.UndergroundPrefix, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(state.Id))
            nation.GraduatedMovementId = state.Id;
        nation.Movement = null;
        if (!restoration) nation.LastSecessionDay = campaign.ElapsedDays;
        nation.NameProclaimed = true;
        nation.PoliticsChangedDay = campaign.ElapsedDays;
        var seat = LargestPopState(held);
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
        Movements.RegionAt(campaign, mapIndex)?.DisplayName ?? "";
}
