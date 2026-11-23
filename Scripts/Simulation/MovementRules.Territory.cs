using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class MovementRules
{
    [ThreadStatic] private static List<ScenarioState> _zoneCandidatesScratch;

    // ------------------------------------------------------------------ uprisings ------

    /// <summary>The chance an uprising in this region succeeds now, 0 to 1.</summary>
    public static float UprisingChance(Campaign campaign, Nation nation, ScenarioState region)
    {
        var owner = OwnerOf(campaign, region);
        if (RevoltRecovery.DaysLeft(campaign, owner, region) > 0) return 0f;
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null || region is null || !MovementMethods.WillRise(campaign, nation, definition) ||
            !MovementReach.CanClaimTerritory(campaign, nation, region)) return 0f;
        var support = state.Support.GetValueOrDefault(region.MapIndex);
        if (!float.IsFinite(support) || !float.IsFinite(state.Cadres) || !float.IsFinite(state.Legitimacy) ||
            !float.IsFinite(region.Stability)) return 0f;
        var own = PeopleOf(region, definition, out _);
        var share = region.Population > 0 ? own / (float)region.Population : 0f;

        // Who is there to stop it: the owner's divisions standing in the region.
        var topology = campaign.ProvinceMap;
        var garrison = 0f;
        if (topology is not null && owner is not null)
        {
            var armies = campaign.Armies;
            var provinceToState = topology.ProvinceToState;
            var provinceCount = topology.ProvinceCount;
            for (var i = 0; i < armies.Count; i++)
            {
                var army = armies[i];
                if (army.NationId == owner.Id && army.ProvinceId > 0 && army.ProvinceId <= provinceCount &&
                    provinceToState[army.ProvinceId] == region.MapIndex)
                    garrison += army.Divisions * army.Strength;
            }
        }

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
        var owner = campaign.NationByMapIndex(region.NationMapIndex);
        if (RevoltTerritory.HasForeignOccupation(campaign, owner, nation, region))
        {
            state.LastReport = Loc.Get("An uprising cannot displace foreign occupation without fighting.");
            return false;
        }
        var chance = UprisingChance(campaign, nation, region);
        if (!float.IsFinite(chance) || chance <= 0f || !float.IsFinite(nation.Treasury) ||
            nation.Treasury < defines.MovementUprisingCost) return false;
        var roll = Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, region.MapIndex);
        { var ledgerBefore = nation.Treasury; nation.Treasury -= defines.MovementUprisingCost; MoneyLedger.Book(nation, MoneyFlow.Movements, nation.Treasury - ledgerBefore); }
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
        var ownerStates = campaign.StatesOf(owner);
        var ownerTotalStates = ownerStates.Length;
        // Most votes are one question asked of the whole homeland - Scotland in 2014, Quebec in
        // 1995 - and a carried vote takes the whole of it, the regions that voted no along with
        // the ones that voted yes. Asked region by region it left one-county Silesias and
        // Scotlands for decades. Some settlements are drawn county by county all the same
        // (Ireland's partition, the 1920 Schleswig plebiscites): a quarter of them, decided
        // once per movement and flashpoint so a campaign replays its own map.
        var oneConstituency = peacefulVote &&
            Roll(campaign.Seed, nation.Id, seed.MapIndex, 0x5C0) >= campaign.Defines.CivilReferendumByRegionShare &&
            HomelandCarries(campaign, nation, definition, seed, owner);
        // Map order over only the owner's states (stable insertion sort preserves campaign.States
        // tie order identically to LINQ OrderBy without scanning/sorting all 2,100 world states).
        var candidates = _zoneCandidatesScratch ??= new List<ScenarioState>();
        candidates.Clear();
        for (var i = 0; i < ownerStates.Length; i++)
        {
            var r = ownerStates[i];
            if (r is not null) candidates.Add(r);
        }
        for (var i = 1; i < candidates.Count; i++)
        {
            var cur = candidates[i];
            var curMap = cur.MapIndex;
            var j = i - 1;
            while (j >= 0 && candidates[j].MapIndex > curMap)
            {
                candidates[j + 1] = candidates[j];
                j--;
            }
            candidates[j + 1] = cur;
        }
        campaign.Homelands.TryGetValue(nation.Id, out var homes);
        for (var ci = 0; ci < candidates.Count; ci++)
        {
            var region = candidates[ci];
            if (region.MapIndex == seed.MapIndex) continue;
            if (region.Unclaimed || region.Population <= 0) continue;
            var isHome = MovementReach.IsHomeland(campaign, definition, region) ||
                (homes is not null && (homes.Contains(region.MapIndex) || homes.Contains(TerritorialClaims.Origin(region))));
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
        candidates.Clear();
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
        var ownerStates = campaign.StatesOf(owner);
        for (var i = 0; i < ownerStates.Length; i++)
        {
            var region = ownerStates[i];
            if (region is null || region.Unclaimed || region.Population <= 0) continue;
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
        var armies = campaign.Armies;
        var provinceToState = topology.ProvinceToState;
        var provinceCount = topology.ProvinceCount;
        for (var i = 0; i < armies.Count; i++)
        {
            var army = armies[i];
            if (army.NationId != owner.Id || army.InBattle || army.ProvinceId <= 0 ||
                army.ProvinceId > provinceCount) continue;
            if (provinceToState[army.ProvinceId] != region.MapIndex) continue;
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
        var wanted = new HashSet<int>(zone.Count);
        for (var i = 0; i < zone.Count; i++) wanted.Add(zone[i].MapIndex);
        var nationStates = campaign.StatesOf(nation);
        var held = new HashSet<int>(nationStates.Length);
        for (var i = 0; i < nationStates.Length; i++) held.Add(nationStates[i].MapIndex);
        var links = new Dictionary<int, HashSet<int>>();
        var provinceToState = map.ProvinceToState;
        var landNeighbors = map.LandNeighbors;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            int here = provinceToState[province];
            if (!wanted.Contains(here) && !held.Contains(here)) continue;
            // Drawn borders: a homeland is claimed as ground that hangs together on the map, and
            // a strait crossing is not a shared border (ProvinceTopology.LandNeighbors).
            var neighbors = landNeighbors[province];
            for (var ni = 0; ni < neighbors.Length; ni++)
            {
                int there = provinceToState[neighbors[ni]];
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
        var result = new List<ScenarioState>(zone.Count);
        for (var i = 0; i < zone.Count; i++)
            if (reached.Contains(zone[i].MapIndex)) result.Add(zone[i]);
        return result;
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
        for (var i = 0; i < zone.Count; i++)
        {
            var region = zone[i];
            if (region.MapIndex != seed.MapIndex &&
                TakeRegion(campaign, nation, region, owner, peacefully, restoration, settle: false))
                gained.Add(region);
        }
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
        var set = provinces as HashSet<int> ?? new HashSet<int>(provinces);
        var defected = Reintegration.Defect(campaign, owner, nation, set,
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
        var provinceToState = topology.ProvinceToState;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (provinceToState[province] == region.MapIndex) provinces.Add(province);

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
        long totalPop = 0;
        double solWeighted = 0, litWeighted = 0;
        for (var i = 0; i < heldStates.Length; i++)
        {
            var s = heldStates[i];
            totalPop += s.Population;
            solWeighted += s.StandardOfLiving * (double)s.Population;
            litWeighted += s.Literacy * (double)s.Population;
        }
        nation.Population = totalPop;
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
            nation.StandardOfLiving = (float)(solWeighted / nation.Population);
            nation.Literacy = (float)(litWeighted / nation.Population);
        }
        if (owner is not null && owner != nation)
        {
            owner.Gdp = MathF.Max(.01f, owner.Gdp - regionalGdp);
            var ownerStates = campaign.StatesOf(owner);
            long ownerPop = 0;
            for (var i = 0; i < ownerStates.Length; i++) ownerPop += ownerStates[i].Population;
            owner.Population = ownerPop;
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
        var seat = LargestPopState(heldStates);
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
            var regions = new HashSet<ushort>(gained.Count);
            for (var i = 0; i < gained.Count; i++)
                if (gained[i] is not null) regions.Add((ushort)gained[i].MapIndex);
            if (topology is not null)
            {
                var provinceToState = topology.ProvinceToState;
                for (var province = 1; province <= topology.ProvinceCount; province++)
                    if (regions.Contains(provinceToState[province])) ground.Add(province);
            }
            DefectGarrison(campaign, nation, owner, ground, peacefully);
        }

        // The cadres become the country's militia, and so does the street: every supporter in
        // the ground that came over is somebody who can be handed a rifle, and the fighters
        // sent in ahead are already there. The tunnels and the camps give them ground to hold.
        var perFormation = defines.MovementMilitiaPerCadres * (definition.Has("armed_wing") ? 0.7f : 1f);
        var street = 0f;
        var hidden = 0f;
        for (var i = 0; i < gained.Count; i++)
        {
            var region = gained[i];
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
        var heldStates = campaign.StatesOf(nation);
        for (var hi = 0; hi < heldStates.Length; hi++)
        {
            var held = heldStates[hi];
            for (var attempt = 0; attempt < formations && raised < formations; attempt++)
                if (MilitiaCore.Found(campaign, nation, held)) raised++;
        }
        if (definition.Has("tunnel_network"))
        {
            var armies = campaign.Armies;
            for (var i = 0; i < armies.Count; i++)
                if (armies[i].NationId == nation.Id) armies[i].Entrenchment = 1f;
        }
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
            {
                long gainedPop = 0;
                for (var i = 0; i < gained.Count; i++) gainedPop += gained[i].Population;
                SecessionResponse.Ask(campaign, owner, nation,
                    string.Join(", ", gained.Take(4).Select(r => r.DisplayName)), gainedPop, owner.Population + nation.Population);
            }
            else if (CampaignDiplomacy.DeclareWar(campaign, owner.Id, nation.Id, seed.MapIndex, "uprising", onOwnInitiative: false) is not null)
                // The garrison that did not go over goes home or holds (StrandedGarrisons).
                StrandedGarrisons.AtRising(campaign, owner, nation, ground);
        }
        // Remember the newly liberated regions as the nation's homeland so restorations and title checks recognize them.
        if (!campaign.Homelands.TryGetValue(nation.Id, out var homes))
        {
            homes = new List<int>(gained.Count);
            for (var i = 0; i < gained.Count; i++) homes.Add(gained[i].MapIndex);
            campaign.Homelands[nation.Id] = homes;
        }
        else
        {
            for (var i = 0; i < gained.Count; i++)
                if (!homes.Contains(gained[i].MapIndex)) homes.Add(gained[i].MapIndex);
        }
        // The borders are on the map, so the movement is done being a movement: ground of
        // its own makes it a country at once (the same rule SynchroniseTerritory applies a
        // week later to ground gained any other way). What follows - wars, peace, exile -
        // happens to it as to any other state.
        GraduateToStatehood(campaign, nation, restoration);
        campaign.MovementRevision++;
        campaign.MarkStrategicChange();
    }

    private static ScenarioState LargestPopState(ScenarioState[] states)
    {
        ScenarioState best = null;
        for (var i = 0; i < states.Length; i++)
            if (best is null || states[i].Population > best.Population) best = states[i];
        return best;
    }
}
