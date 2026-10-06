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
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(MovementRules))) return;
        if (campaign.ElapsedDays == 0) return;
        MovementLifecycle.Tick(campaign);
        var week = Math.Max(1, campaign.Defines.EconomyTickDays);
        // What the world decides about who has fallen happens on the week, as it did.
        if (campaign.ElapsedDays % week == 0)
        {
            Owners(campaign, rebuild: true);
            using (SimProfiler.Measure("    mov:homelands")) MovementFall.RememberHomelands(campaign);
            using (SimProfiler.Measure("    mov:fall")) MovementFall.Decide(campaign);
        }
        // Each movement's own week on a day of its own. They all used to fall on the same
        // day, so their cost did not spread: one frame in seven carried every movement's week
        // at once, the largest hitch in a late campaign at the top speed. Every movement still
        // has exactly one week in any seven days - on its day rather than on everybody's.
        foreach (var nation in campaign.Nations.ToList())
        {
            if (nation.Movement is not { Dissolved: false } || !OnItsDay(campaign, nation.Id, week)) continue;
            Owners(campaign);
            using (SimProfiler.Measure("    mov:week")) Week(campaign, nation);
            using (SimProfiler.Measure("    mov:endure")) MovementFall.Endure(campaign, nation);
        }
    }

    /// <summary>Whether today is this one's day in a cycle of so many days.</summary>
    internal static bool OnItsDay(Campaign campaign, int id, int cycle) =>
        (campaign.ElapsedDays + (id % cycle + cycle) % cycle) % cycle == 0;

    private static Campaign _ownersFor;
    private static int _ownersDay = -1;
    private static readonly Dictionary<int, Nation> OwnersByIndex = new();

    /// <summary>Every country by its map index, built once a week rather than searched for per region.</summary>
    internal static Dictionary<int, Nation> Owners(Campaign campaign, bool rebuild = false)
    {
        if (!rebuild && ReferenceEquals(_ownersFor, campaign) && _ownersDay == campaign.ElapsedDays) return OwnersByIndex;
        OwnersByIndex.Clear();
        foreach (var candidate in campaign.Nations) OwnersByIndex[candidate.MapIndex] = candidate;
        _ownersFor = campaign;
        _ownersDay = campaign.ElapsedDays;
        return OwnersByIndex;
    }

    internal static Nation OwnerOf(Campaign campaign, ScenarioState region) =>
        region is not null && Owners(campaign).TryGetValue(region.NationMapIndex, out var owner) ? owner : null;

    private static readonly Dictionary<int, bool> OwnStateCache = new();

    internal static void Week(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null) return;
        var defines = campaign.Defines;

        // Land lost is land lost: a movement that had a country and has had it taken back
        // goes on as what it was before - somewhere to be governed from, and a following.
        var holds = campaign.StatesOf(nation).Length > 0;
        if (!holds && !state.Landless) BecomeLandless(campaign, nation);
        if (holds && state.Landless)
        {
            state.Landless = false;
            if (state.StatehoodDay < 0) state.StatehoodDay = campaign.ElapsedDays;
            campaign.MovementRevision++;
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
        OwnStateCache.Clear();
        foreach (var region in campaign.States)
        {
            var own = PeopleOf(campaign, region, definition, out var faithful);
            if (own <= 0 && faithful <= 0) { state.Support.Remove(region.MapIndex); continue; }
            var supportCeiling = MovementReach.SupportCeiling(campaign, nation, region, definition, own, faithful);
            if (supportCeiling <= 0f) { state.Support.Remove(region.MapIndex); continue; }
            believers += faithful;
            var support = state.Support.GetValueOrDefault(region.MapIndex);
            var owner = OwnerOf(campaign, region);
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
            var opinion = CampaignDiplomacy.Get(campaign, sponsor.Id, nation.Id).Opinion;
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
    private static Campaign _peopleCampaign;
    private static int _peopleDay = -1;
    private static int _peopleMapRevision = -1;
    private static int _peopleStates = -1;
    private static readonly Dictionary<MovementDefinition, Dictionary<ScenarioState, (long Own, long Faithful)>> PeopleToday = new();
    private static readonly Dictionary<string, List<PopulationGroupAt>> ByCulture = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<PopulationGroupAt>> ByReligion = new(StringComparer.OrdinalIgnoreCase);
    private static bool _indexed;

    private readonly record struct PopulationGroupAt(ScenarioState Region, PopulationGroup Group);

    /// <summary>
    /// <see cref="PeopleOf(ScenarioState, MovementDefinition, out long)"/>, remembered for the day.
    ///
    /// Answered from an index of who lives where rather than by reading every group of the
    /// region. A movement's week asks this of all eighteen hundred regions, and every
    /// movement asks it - seventeen walks of every population group in the world, a week's
    /// single largest frame thirty-six years in. The index is one walk; each movement then
    /// reads only the groups of its own peoples and faiths. The same sums in the same
    /// integers, so the answer is the one the walk gave.
    /// </summary>
    internal static long PeopleOf(Campaign campaign, ScenarioState region, MovementDefinition definition, out long faithful)
    {
        if (!ReferenceEquals(_peopleCampaign, campaign) || _peopleDay != campaign.ElapsedDays ||
            _peopleMapRevision != campaign.MapRevision || _peopleStates != campaign.States.Count)
        {
            PeopleToday.Clear();
            _indexed = false;
            _peopleCampaign = campaign;
            _peopleDay = campaign.ElapsedDays;
            _peopleMapRevision = campaign.MapRevision;
            _peopleStates = campaign.States.Count;
        }
        if (!PeopleToday.TryGetValue(definition, out var regions))
            PeopleToday[definition] = regions = CountPeople(campaign, definition);
        if (regions.TryGetValue(region, out var known))
        {
            faithful = known.Faithful;
            return known.Own;
        }
        faithful = 0;
        return 0;
    }

    /// <summary>Every region with any of a movement's peoples or believers in it, and how many.</summary>
    private static Dictionary<ScenarioState, (long Own, long Faithful)> CountPeople(Campaign campaign, MovementDefinition definition)
    {
        if (!_indexed)
        {
            foreach (var list in ByCulture.Values) list.Clear();
            foreach (var list in ByReligion.Values) list.Clear();
            foreach (var region in campaign.States)
                foreach (var group in region.PopulationGroups)
                {
                    if (group.Size <= 0) continue;
                    if (!ByCulture.TryGetValue(group.Culture ?? "", out var cultured)) ByCulture[group.Culture ?? ""] = cultured = new List<PopulationGroupAt>();
                    cultured.Add(new PopulationGroupAt(region, group));
                    if (!ByReligion.TryGetValue(group.Religion ?? "", out var believing)) ByReligion[group.Religion ?? ""] = believing = new List<PopulationGroupAt>();
                    believing.Add(new PopulationGroupAt(region, group));
                }
            _indexed = true;
        }
        var counted = new Dictionary<ScenarioState, (long Own, long Faithful)>();
        // Each people once, however the data spells it twice: the walk counted a group once.
        foreach (var people in definition.Peoples.Distinct(StringComparer.OrdinalIgnoreCase))
            if (ByCulture.TryGetValue(people, out var groups))
                foreach (var (region, group) in groups)
                {
                    var known = counted.GetValueOrDefault(region);
                    counted[region] = (known.Own + group.Size, known.Faithful);
                }
        // A believer who is not one of its peoples - the walk's `else`.
        foreach (var faith in definition.Faiths.Distinct(StringComparer.OrdinalIgnoreCase))
            if (ByReligion.TryGetValue(faith, out var groups))
                foreach (var (region, group) in groups)
                {
                    if (MovementDefinition.Listed(definition.Peoples, group.Culture)) continue;
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
            if (MovementDefinition.Listed(definition.Peoples, group.Culture)) own += group.Size;
            else if (MovementDefinition.Listed(definition.Faiths, group.Religion)) faithful += group.Size;
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

    /// <summary>A region of the friendliest country that is not at war with it, not the one throwing it out.</summary>
    internal static ScenarioState BestRefuge(Campaign campaign, Nation nation, Nation except)
    {
        // Whoever holds what it claims is the enemy whose enemies make it welcome.
        var claimants = new List<int>();
        foreach (var id in Movements.DefinitionOf(nation)?.Homeland ?? new List<string>())
            if (Movements.StateById(campaign, id) is { } claimed && OwnerOf(campaign, claimed) is { } holder && holder.Id != nation.Id)
                claimants.Add(holder.Id);
        ScenarioState best = null;
        var bestScore = float.MinValue;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate == except || candidate.Id == nation.Id || candidate.Movement is not null) continue;
            if (CampaignDiplomacy.AtWar(campaign, candidate.Id, nation.Id)) continue;
            var states = campaign.StatesOf(candidate);
            if (states.Length == 0) continue;
            var score = MovementFall.Warmth(campaign, candidate, nation, claimants) +
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

    internal static string RegionName(Campaign campaign, int mapIndex) =>
        campaign.States.Find(state => state.MapIndex == mapIndex)?.DisplayName ?? "";

    // ------------------------------------------------------------------ uprisings ------

    /// <summary>The chance an uprising in this region succeeds now, 0 to 1.</summary>
    public static float UprisingChance(Campaign campaign, Nation nation, ScenarioState region)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null || region is null || !definition.CanStageUprising) return 0f;
        var support = state.Support.GetValueOrDefault(region.MapIndex);
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
        return M.Clamp(chance, 0.02f, 0.9f);
    }

    /// <summary>Rise in this region. True when it now holds it.</summary>
    internal static bool Rise(Campaign campaign, Nation nation, ScenarioState region)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null || region is null || !definition.CanStageUprising) return false;
        var defines = campaign.Defines;
        var chance = UprisingChance(campaign, nation, region);
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

        TakeRegion(campaign, nation, region, owner);
        state.LastReport = Loc.Format("The uprising in {0} succeeded: it is ours.", region.DisplayName);
        return true;
    }

    /// <summary>The region is its own: the ground, a capital, militia, and a war with the country it rose against.</summary>
    internal static void TakeRegion(Campaign campaign, Nation nation, ScenarioState region, Nation owner, bool peacefully = false)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        var defines = campaign.Defines;
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
        var heldStates = campaign.StatesOf(nation);
        nation.Population = heldStates.Sum(s => s.Population);
        nation.Gdp = MathF.Max(.01f, previousGdp + regionalGdp);
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
        if (!string.IsNullOrWhiteSpace(definition.StateName)) nation.Name = definition.StateName;
        var seat = campaign.StatesOf(nation).OrderByDescending(candidate => candidate.Population).FirstOrDefault();
        if (seat is not null) nation.CapitalStateMapIndex = seat.MapIndex;
        campaign.LocateCapitals();
        nation.Government = peacefully ? definition.Government : "provisional_government";
        nation.PoliticsChangedDay = campaign.ElapsedDays;

        // The cadres become the country's militia, and so does the street: every supporter in the
        // region that rose is somebody who can be handed a rifle, and the fighters sent in ahead
        // are already there. The tunnels and the camps give them ground to hold.
        var perFormation = defines.MovementMilitiaPerCadres * (definition.Has("armed_wing") ? 0.7f : 1f);
        var street = MovementRules.PeopleOf(region, definition, out _) * state.Support.GetValueOrDefault(region.MapIndex);
        var hidden = state.Infiltrated.GetValueOrDefault(region.MapIndex);
        state.Infiltrated.Remove(region.MapIndex);
        var formations = Math.Min(defines.MovementUprisingMilitiaMax,
            (int)(state.Cadres / MathF.Max(1f, perFormation)) +
            (int)(street / MathF.Max(1f, defines.MovementMilitiaPerSupporters)) +
            (int)(hidden / MathF.Max(1f, perFormation * 0.5f)));
        if (definition.Has("nonviolence")) formations = Math.Min(formations, 1);
        if (peacefully) formations = 0;
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

        if (owner is not null && !peacefully)
        {
            if (nation.SecededFromId < 0) nation.SecededFromId = owner.Id;
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, owner.Id, nation.Id), "rose_against_us");
            CampaignDiplomacy.DeclareWar(campaign, owner.Id, nation.Id, region.MapIndex, "uprising", onOwnInitiative: false);
        }
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
