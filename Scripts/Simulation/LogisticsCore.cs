using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>
/// Province-graph logistics shared by player and AI. Home infrastructure creates hubs;
/// distance, local throughput and formation demand determine the supply that reaches an army.
///
/// This pass used to make three quarters of the whole simulation's garbage - sixteen
/// megabytes a campaign day, eleven gigabytes over two years - because it gave every one of
/// the two hundred and sixty-one nations two fresh arrays the length of the province map,
/// three hundred kilobytes a nation, and then filled both with -1 before using perhaps a
/// few hundred entries of either. At speed five that is a hundred megabytes a second handed
/// to the collector, which is what a player feels as the game stuttering and the memory
/// climbing.
///
/// Nothing about the algorithm needed that. The buffers are scratch: each nation overwrites
/// what the last one wrote. So they are allocated once and kept, and instead of clearing
/// them a generation stamp says which entries belong to the nation being worked on now -
/// which also removes the seventy-five thousand writes per nation that the clearing was.
/// Everything else here works the same way it did, to the last digit of the bench.
/// </summary>
public static class LogisticsSystem
{
    /// <summary>Men a day each barracks adds to the country's reinforcement capacity, before the military budget scales it.</summary>
    public const float ReinforcementPerBarracks = 120f;

    /// <summary>
    /// The supply throughput a home region's buildings add to Defines.BaseStateSupplyThroughput:
    /// infrastructure most, then the rail network, then barracks. One sum, read by this pass and
    /// by the army disposition staff (ArmyDisposition.Measure), so the two never disagree about
    /// what a hub can carry and a build list can quote the same constants.
    /// </summary>
    public static float BuildingThroughput(Defines defines, ScenarioState state) =>
        state is null || defines is null ? 0f :
            state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure) * defines.SupplyPerInfrastructure +
            state.Buildings.GetValueOrDefault(BuildingKind.RailNetwork) * defines.SupplyPerRailNetwork +
            state.Buildings.GetValueOrDefault(BuildingKind.Barracks) * defines.SupplyPerBarracks;

    /// <summary>What the military budget, against the baseline, makes of that capacity (before the reinforcement_rate modifier).</summary>
    public static float ReinforcementBudget(Campaign campaign, Nation nation) =>
        M.Clamp(nation.MilitarySpending / MathF.Max(0.01f, campaign.Defines.BaselineMilitarySpending), 0.25f, 2.5f);

    // Scratch, reused across nations and days of ONE campaign. A second Campaign in the
    // same process used to clear these lists and then read the first campaign's provinces.
    // The live tick stays single-threaded. A later pure job would still need its own scratch.
    private sealed class Scratch
    {
        public int[] Distance = Array.Empty<int>();
        public int[] Source = Array.Empty<int>();
        public int[] Stamp = Array.Empty<int>();
        // Which provinces the network reaches from its roots, stamped like the trace below.
        public int[] Reach = Array.Empty<int>();
        public int Generation;
        public readonly Queue<int> Frontier = new();
        public readonly List<int> Roots = new();
        public readonly Dictionary<int, int> HubByState = new();
        public readonly Dictionary<int, float> DemandBySource = new();
        public readonly Dictionary<int, ScenarioState> StateById = new();
        public readonly Dictionary<ushort, List<int>> Controlled = new();
        public readonly Dictionary<int, List<Army>> ArmiesByNation = new();
        public readonly Dictionary<int, bool> IsIsolatedHub = new();
        public readonly Dictionary<int, bool> IsForwardHub = new();
        public readonly Dictionary<int, int> HeldInState = new();
        public readonly Dictionary<int, int> LandInState = new();
        public ProvinceTopology LandCounted;
        public readonly HashSet<ushort> Corridors = new();
        public readonly HashSet<ushort> Patrons = new();
    }

    private static readonly ConditionalWeakTable<Campaign, Scratch> Boards = new();

    /// <summary>
    /// Provinces the last supply trace recorded for one map flag in this campaign.
    /// Empty when this world has not traced. Reading does not trace and does not write the world.
    /// </summary>
    internal static IReadOnlyList<int> ControlledProvinces(Campaign campaign, int mapIndex)
    {
        if (campaign is null || mapIndex <= 0 || mapIndex > ushort.MaxValue ||
            !Boards.TryGetValue(campaign, out var board)) return Array.Empty<int>();
        return board.Controlled.TryGetValue((ushort)mapIndex, out var list) ? list : Array.Empty<int>();
    }

    /// <summary>How many land provinces of each region the country holds now, and (once per map) how many each region has.</summary>
    private static void CountHeld(Campaign campaign, List<int> territory, Scratch board)
    {
        var topology = campaign.ProvinceMap;
        if (!ReferenceEquals(board.LandCounted, topology))
        {
            board.LandInState.Clear();
            for (var province = 1; province <= topology.ProvinceCount; province++)
                if (!topology.IsWater(province))
                {
                    var state = topology.ProvinceToState[province];
                    board.LandInState[state] = board.LandInState.GetValueOrDefault(state) + 1;
                }
            board.LandCounted = topology;
        }
        board.HeldInState.Clear();
        foreach (var province in territory)
            if (!topology.IsWater(province))
            {
                var state = topology.ProvinceToState[province];
                board.HeldInState[state] = board.HeldInState.GetValueOrDefault(state) + 1;
            }
    }

    /// <summary>
    /// An improvised depot on ground the country holds but does not own. Once it holds most of an
    /// enemy region and the road behind it is open, the army repairs a railhead there: a forward
    /// depot carrying a share of what a home depot would (<see cref="Defines.ForwardDepotThroughputShare"/>).
    /// Without it an offensive that took a region two dozen provinces from home fed every
    /// formation in it from the far side of the old border, and the front there sat
    /// under-supplied - unreinforced, worn out - for as long as the war lasted.
    /// </summary>
    private static void ForwardDepot(Campaign campaign, int province, bool connected, Scratch board)
    {
        if (!connected || campaign.Defines.ForwardDepotHeldShare > 1f) return;
        var topology = campaign.ProvinceMap;
        var state = topology.ProvinceToState[province];
        if (board.HubByState.ContainsKey(state)) return;
        var land = board.LandInState.GetValueOrDefault(state);
        if (land <= 0 || board.HeldInState.GetValueOrDefault(state) < land * campaign.Defines.ForwardDepotHeldShare) return;
        board.HubByState[state] = province;
        board.IsForwardHub[state] = true;
    }

    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(LogisticsSystem))) return;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;
        // Every third day, and once on it. Without the first half of this a campaign
        // stepping an hour at a time rebuilt the whole supply network twenty-four times on
        // the third day and not at all on the other two.
        if (!campaign.StepOpensNewDay) return;
        // Every third day the roads are traced again. The days inside a pocket are still
        // days, so the clock below runs on the days the trace sits out as well.
        if (campaign.ElapsedDays <= 1 || campaign.ElapsedDays % 3 == 0)
        {

        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var board = Boards.GetOrCreateValue(campaign);

        // Who holds what. The lists are kept between ticks and emptied rather than dropped,
        // so the province scan costs the scan and nothing else.
        foreach (var list in board.Controlled.Values) list.Clear();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var map = controllers[province];
            if (map == 0) continue;
            if (!board.Controlled.TryGetValue(map, out var list)) board.Controlled[map] = list = new List<int>();
            list.Add(province);
        }

        // One pass over the armies instead of one pass per nation. With two hundred and
        // sixty-one nations and fourteen hundred formations that is three hundred and
        // sixty-five thousand comparisons a tick turned into fourteen hundred.
        foreach (var list in board.ArmiesByNation.Values) list.Clear();
        foreach (var army in campaign.Armies)
        {
            if (!board.ArmiesByNation.TryGetValue(army.NationId, out var list))
                board.ArmiesByNation[army.NationId] = list = new List<Army>();
            list.Add(army);
        }

        var length = topology.ProvinceCount + 1;
        if (board.Distance.Length < length)
        {
            board.Distance = new int[length];
            board.Source = new int[length];
            board.Stamp = new int[length];
            board.Reach = new int[length];
            board.Generation = 0;
        }

        foreach (var nation in campaign.Nations)
        {
            if (!board.ArmiesByNation.TryGetValue(nation.Id, out var armies) || armies.Count == 0) continue;
            UpdateNation(campaign, nation, armies, board.Controlled.GetValueOrDefault((ushort)nation.MapIndex), board);
        }
        } // The trace above is every third day. The clock below is every day.

        AgeCutOff(campaign);
    }

    private static void UpdateNation(Campaign campaign, Nation nation, List<Army> armies, List<int> territory, Scratch board)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null || territory is null || territory.Count == 0)
        {
            foreach (var army in armies)
                if (!ProvisionVisitor(campaign, nation, army)) SetStranded(army);
            return;
        }

        board.StateById.Clear();
        foreach (var state in campaign.StatesOf(nation)) board.StateById[state.MapIndex] = state;

        // A new generation is this nation's claim on the scratch: an entry counts as
        // written only if it carries the number below. Nothing has to be cleared.
        var generation = ++board.Generation;
        var distance = board.Distance;
        var source = board.Source;
        var stamp = board.Stamp;

        // Which of the country's ground the network actually reaches. Only that ground can
        // hold a depot, which is a different question from which ground it owns - see
        // CollectRoots and TraceNetwork.
        var reach = board.Reach;
        CollectCorridors(campaign, nation, board);
        // A rising holds ground it does not own: the map still says whose the provinces are,
        // and a rebel army that looked only at owned ground found no depot anywhere and starved
        // in its own heartland (Sakha, 2034). For a side with no land of its own, the ground it
        // holds is where its depots stand.
        var insurgent = true;
        foreach (var province in territory)
            if (topology.ProvinceToNation[province] == nation.MapIndex) { insurgent = false; break; }
        CollectRoots(campaign, nation, territory, board);
        if (insurgent && board.Roots.Count == 0 && InsurgentSeat(campaign, nation, territory) is var seat and > 0) board.Roots.Add(seat);
        TraceNetwork(nation, topology, controllers, generation, board);

        board.HubByState.Clear();
        board.IsIsolatedHub.Clear();
        board.IsForwardHub.Clear();
        CountHeld(campaign, territory, board);
        foreach (var province in territory)
        {
            // A lake keeps no depot, however much of it a country owns.
            if (topology.IsWater(province)) continue;
            if (!insurgent && topology.ProvinceToNation[province] != nation.MapIndex)
            {
                ForwardDepot(campaign, province, reach[province] == generation, board);
                continue;
            }
            var isConnected = reach[province] == generation;
            var state = topology.ProvinceToState[province];
            if (isConnected)
            {
                // A region may straddle a pocket. Prefer its connected depot regardless
                // of province order; an isolated fragment must not cut off the whole region.
                if (!board.HubByState.ContainsKey(state) || board.IsIsolatedHub.GetValueOrDefault(state) ||
                    board.IsForwardHub.GetValueOrDefault(state))
                    board.HubByState[state] = province;
                board.IsIsolatedHub[state] = false;
                board.IsForwardHub[state] = false;
                continue;
            }
            if (board.HubByState.ContainsKey(state)) continue;
            if (!board.StateById.TryGetValue(state, out var st) || st.Buildings.GetValueOrDefault(BuildingKind.Barracks) <= 0)
                continue;
            board.IsIsolatedHub[state] = true;
            board.HubByState[state] = province;
        }

        board.Frontier.Clear();
        foreach (var (state, province) in board.HubByState)
        {
            stamp[province] = generation;
            distance[province] = 0;
            source[province] = state;
            board.Frontier.Enqueue(province);
        }
        // No hop budget on the walk itself any more. A province past the trace range is at the
        // end of a long road, not inside a ring: it is charged for the distance (the distance
        // factor below bottoms out at a fifth of the supply) but it is not cut off. Cutting it off
        // marooned whole fronts on their own connected ground - the far north of Russia, two
        // dozen provinces from the nearest depot - where no breakout could help, because there was
        // nothing to break out of, and the formations sat worn out and unreinforced for years.
        TraceDepots(nation, topology, controllers, generation, board);

        // A region is not necessarily one land component (Kaliningrad includes detached
        // coastal spits). The root trace correctly reached every sea entrance, but the
        // single regional depot could sit on a spit whose land walk reached none of the
        // mainland. SupplyBySea then rescued coast *armies* only, leaving inland troops
        // falsely encircled. Give each root-reachable home fragment a distribution point.
        // These points share the SAME regional source and demand/capacity, not free new
        // throughput. A lost harbour still fails the root trace, so a real pocket stays cut.
        foreach (var province in territory)
        {
            if (stamp[province] == generation || reach[province] != generation || topology.IsWater(province) ||
                topology.ProvinceToNation[province] != nation.MapIndex) continue;
            var state = topology.ProvinceToState[province];
            if (!board.HubByState.ContainsKey(state) || board.IsIsolatedHub.GetValueOrDefault(state)) continue;
            stamp[province] = generation;
            distance[province] = 0;
            source[province] = state;
            board.Frontier.Enqueue(province);
            TraceDepots(nation, topology, controllers, generation, board);
        }

        SupplyBySea(campaign, nation, armies, controllers, generation, board);

        board.DemandBySource.Clear();
        foreach (var army in armies)
        {
            var at = army.ProvinceId;
            if (at <= 0 || at >= stamp.Length || stamp[at] != generation) continue;
            var sourceState = source[at];
            board.DemandBySource[sourceState] = board.DemandBySource.GetValueOrDefault(sourceState) +
                army.Divisions * campaign.Defines.SupplyThroughputPerDivision *
                UnitTypeCatalog.Get(army.TemplateId).SupplyUse;
        }

        foreach (var army in armies)
        {
            if (ProvisionVisitor(campaign, nation, army)) continue;
            // A formation at sea is supplied by the ship it is on, not by the province
            // network. Marking it cut off would starve an invasion before it landed.
            if (army.EmbarkedFleetId >= 0)
            {
                army.CutOff = false;
                army.DaysCutOff = 0;
                army.SupplyDistance = 0;
                army.SupplyCongestion = 1f;
                continue;
            }

            var province = army.ProvinceId;
            if (province <= 0 || province >= stamp.Length || stamp[province] != generation)
            {
                SetStranded(army);
                continue;
            }
            var sourceState = source[province];
            board.StateById.TryGetValue(sourceState, out var state);
            // A modern state moves its army on railways and roads, not on the three
            // divisions' worth of throughput a bare province used to allow. Infrastructure
            // is what a player builds to widen this, so it carries the most weight; a rail
            // network adds a little over half as much, barracks less (BuildingThroughput).
            // Winter narrows the same road. It does not invent a second supply network.
            var centre = topology.CenterOf(province);
            var damageFactor = state is not null ? (1f - MathF.Min(0.85f, state.WarDamage * 0.9f)) : 1f;
            var enclaveFactor = board.IsIsolatedHub.GetValueOrDefault(sourceState) ? 0.35f :
                board.IsForwardHub.GetValueOrDefault(sourceState) ? campaign.Defines.ForwardDepotThroughputShare : 1f;
            var capacity = (campaign.Defines.BaseStateSupplyThroughput + BuildingThroughput(campaign.Defines, state)) *
                          damageFactor * enclaveFactor *
                          WaterConditions.RoadThroughput(campaign, centre.Latitude);
            var demand = MathF.Max(0.01f, board.DemandBySource.GetValueOrDefault(sourceState));
            var congestion = M.Clamp(capacity / demand, 0.08f, 1f);
            var traceRange = Math.Max(1, campaign.Defines.SupplyTraceRange);
            var distanceFactor = M.Clamp(1f - distance[province] / (float)traceRange * 0.62f, 0.22f, 1f);
            var lakeMultiplier = WaterConditions.LakeSupplyMultiplier(campaign, province);

            // A fleet sitting off the coast takes what arrives by sea. This is the one
            // thing a navy does that a land player feels immediately: an army on a
            // blockaded shore stops being supplied, and stops being able to attack.
            var blockade = NavalOperations.BlockadePressure(campaign, nation, centre.Latitude, centre.Longitude);
            var blockadeFactor = 1f - blockade * campaign.Defines.FleetBlockadeSupplyPenalty;
            var target = M.Clamp01(congestion * distanceFactor * lakeMultiplier * blockadeFactor *
                                   topology.TerrainAt(province).Supply * NationalModifiers.Factor(nation, "supply"));
            army.SupplyDistance = distance[province];
            army.SupplySourceStateMapIndex = sourceState;
            army.SupplyCongestion = congestion;
            // Disconnected local stores buy time; they cannot draw from national depots.
            // Only a restored route may reset the daily reserve clock.
            if (board.IsIsolatedHub.GetValueOrDefault(sourceState))
            {
                army.CutOff = true;
                continue;
            }
            army.CutOff = false;
            army.DaysCutOff = 0;
            army.SupplyLevel = M.Lerp(army.SupplyLevel, target, 0.34f);
        }

        var barracksTotal = 0;
        foreach (var state in board.StateById.Values)
            barracksTotal += state.Buildings.GetValueOrDefault(BuildingKind.Barracks);
        nation.ReinforcementCapacity = (MathF.Max(80f, MilitaryBootstrap.Personnel(nation.Tag, nation.Population) *
                                            campaign.Defines.ReinforcementCapacityShare) +
                                        barracksTotal * ReinforcementPerBarracks) * ReinforcementBudget(campaign, nation) *
                                       NationalModifiers.Factor(nation, "reinforcement_rate");
        if (campaign.ElapsedDays % 7 == 0) nation.ReinforcedLastWeek = 0f;
    }

    /// <summary>
    /// Walk from the queued distribution points without crossing hostile controllers.
    /// </summary>
    private static void TraceDepots(Nation nation, ProvinceTopology topology, ushort[] controllers, int generation, Scratch board)
    {
        while (board.Frontier.Count > 0)
        {
            var province = board.Frontier.Dequeue();
            foreach (var neighbor in topology.Neighbors[province])
            {
                if (board.Stamp[neighbor] == generation || !Passable(nation, controllers[neighbor], board)) continue;
                board.Stamp[neighbor] = generation;
                // Lakes do not charge extra road hops; columns still cannot march across them.
                board.Distance[neighbor] = board.Distance[province] + (topology.IsWater(neighbor) ? 0 : 1);
                board.Source[neighbor] = board.Source[province];
                board.Frontier.Enqueue(neighbor);
            }
        }
    }

    /// <summary>
    /// Supply by sea, for ground of one's own that no road reaches.
    ///
    /// The trace above walks the land, so every island that is not a region of its own was
    /// cut off from the moment the campaign began - and the deployment put divisions on
    /// them: six of Japan's seventeen, on Rishiri, Shikoku's islets and the Amakusa chain,
    /// starved to six per cent and were buried inside six weeks of peace. The same trace
    /// starved every beachhead an invasion took, because a beachhead is exactly ground no
    /// road reaches.
    ///
    /// So a formation on a coastal province its country holds, that the land trace did not
    /// reach, is supplied through the nearest port the trace did reach on the same sea,
    /// SeaSupplyHops further away than that port is. A blockade still bites: the supply
    /// that arrives is charged for any enemy fleet sitting off it, below, like any coast's.
    /// </summary>
    private static void SupplyBySea(Campaign campaign, Nation nation, List<Army> armies, ushort[] controllers,
        int generation, Scratch board)
    {
        var topology = campaign.ProvinceMap;
        var stamp = board.Stamp;
        List<int> ports = null;
        foreach (var army in armies)
        {
            if (army.EmbarkedFleetId >= 0) continue;
            var province = army.ProvinceId;
            if (province <= 0 || province >= stamp.Length) continue;
            // Reached over land within the trace range: the road is the answer. Reached only past
            // it, a ship from a port of its own may still be the shorter way (below).
            var byLand = stamp[province] == generation;
            if (byLand && board.Distance[province] <= campaign.Defines.SupplyTraceRange) continue;
            if (controllers[province] != nation.MapIndex) continue;
            if (!SeaLanes.TryHarbourPoint(topology, province, out var latitude, out var longitude)) continue;
            var basin = SeaLanes.HarbourBasin(topology, province);

            // The ports the land trace reached, found once for the country and only if some
            // formation needs one.
            if (ports is null)
            {
                ports = new List<int>();
                foreach (var coast in SeaLanes.CoastalProvinces(topology))
                    if (coast < stamp.Length && stamp[coast] == generation && controllers[coast] == nation.MapIndex &&
                        !board.IsIsolatedHub.GetValueOrDefault(board.Source[coast]))
                        ports.Add(coast);
            }

            var best = -1;
            var bestDistance = float.MaxValue;
            foreach (var port in ports)
            {
                if (SeaLanes.HarbourBasin(topology, port) != basin) continue;
                if (!SeaLanes.TryHarbourPoint(topology, port, out var portLatitude, out var portLongitude)) continue;
                var distance = NavalOperations.Distance(latitude, longitude, portLatitude, portLongitude);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = port;
            }
            if (best < 0) continue;
            var bySea = board.Distance[best] + Math.Max(0, campaign.Defines.SeaSupplyHops);
            if (byLand && bySea >= board.Distance[province]) continue;
            stamp[province] = generation;
            board.Distance[province] = board.Distance[best] + Math.Max(0, campaign.Defines.SeaSupplyHops);
            board.Source[province] = board.Source[best];
        }
    }

    /// <summary>
    /// Where a country's supply network begins: one usable government seat. After the
    /// capital falls this is its evacuated rear, not every disconnected barracks at once.
    ///
    /// A province used to be a source of supply the moment the country owned it, at distance
    /// zero. Two things followed, and a player feels both. A division encircled on a scrap of
    /// national soil was fed from that scrap for as long as it stood there, so closing a pocket
    /// bought nothing until the ground itself fell; and cutting the corridor between a capital
    /// and its front cost the enemy nothing at all, because every province behind the cut was
    /// still a source of its own. Supply begins at a root and travels over ground the country
    /// controls, which is what makes a corridor worth taking.
    /// </summary>
    /// <summary>
    /// Ground a country's supply may cross without owning it: a co-belligerent's, or a host's
    /// that has granted it military access. A column fighting beyond an ally used to be cut off
    /// the day it crossed the ally's border, however open the road behind it - a stack on the
    /// far side of Saudi Arabia starved with the Saudi roads open to it. Crossing is all it is:
    /// no depot stands on someone else's ground (only the country's own provinces become hubs),
    /// and the trace's distance still counts every province of the way. Built only for a
    /// country at war; at peace supply never needs to leave home.
    /// </summary>
    private static void CollectCorridors(Campaign campaign, Nation nation, Scratch board)
    {
        board.Corridors.Clear();
        var atWar = false;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            atWar = true;
            foreach (var id in war.Side(war.IsAttacking(nation.Id)))
                if (id != nation.Id && campaign.NationById(id) is { } friend) board.Corridors.Add((ushort)friend.MapIndex);
        }
        if (!atWar) return;
        foreach (var host in campaign.Nations)
            if (host.Id != nation.Id && host.Population > 0 && MilitaryAccess.Granted(campaign, nation.Id, host.Id) &&
                !MilitaryAccess.Hostile(campaign, nation.Id, host.Id))
                board.Corridors.Add((ushort)host.MapIndex);
    }

    private static bool Passable(Nation nation, ushort controller, Scratch board) =>
        controller == nation.MapIndex || controller != 0 && board.Corridors.Contains(controller);

    /// <summary>
    /// Where a side with no land of its own keeps its stores: its movement's headquarters when it
    /// holds them, otherwise the held province in the most populous region it holds.
    /// </summary>
    private static int InsurgentSeat(Campaign campaign, Nation nation, List<int> territory)
    {
        var topology = campaign.ProvinceMap;
        var hq = nation.Movement?.HeadquartersProvinceId ?? 0;
        if (hq > 0 && territory.Contains(hq) && !topology.IsWater(hq)) return hq;
        var regions = new Dictionary<int, long>();
        foreach (var state in campaign.States) regions[state.MapIndex] = state.Population;
        var best = 0;
        long most = -1;
        foreach (var province in territory)
        {
            if (topology.IsWater(province) || campaign.DestroyedProvinces.Contains(province)) continue;
            var people = regions.GetValueOrDefault(topology.ProvinceToState[province]);
            if (people <= most) continue;
            most = people;
            best = province;
        }
        return best;
    }

    private static void CollectRoots(Campaign campaign, Nation nation, List<int> territory, Scratch board)
    {
        board.Roots.Clear();
        var origin = CapitalCore.SupplyOriginProvinceId(campaign, nation);
        if (origin > 0) board.Roots.Add(origin);
        PatronBorders(campaign, nation, territory, board);
    }

    /// <summary>
    /// A patron that ships weapons ships them over its own border. Ground the client holds
    /// against the land of a backer at level two or more is a root of its own: the pocket
    /// is fed from the far side of the frontier, as Idlib was through Turkey for five years
    /// and Ukraine through Poland. Without it a besieged client with a patron next door
    /// starved as fast as one alone on an island, and surrendered with the arms still waiting
    /// at the crossing. The same for any country - nobody is named here.
    /// </summary>
    private static void PatronBorders(Campaign campaign, Nation nation, List<int> territory, Scratch board)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        board.Patrons.Clear();
        foreach (var backing in campaign.Sponsorships)
            if (backing.ClientId == nation.Id && backing.Level >= 2 &&
                campaign.NationById(backing.SponsorId) is { Population: > 0 } sponsor &&
                !CampaignDiplomacy.AtWar(campaign, sponsor.Id, nation.Id))
                board.Patrons.Add((ushort)sponsor.MapIndex);
        if (board.Patrons.Count == 0) return;
        foreach (var province in territory)
        {
            if (controllers[province] != nation.MapIndex || topology.IsWater(province)) continue;
            foreach (var neighbor in topology.Neighbors[province])
                if (board.Patrons.Contains(controllers[neighbor]) && !topology.IsWater(neighbor))
                {
                    board.Roots.Add(province);
                    break;
                }
        }
    }

    /// <summary>
    /// The ground the network reaches from its roots: over land the country controls, and then,
    /// if it holds a port, along its own coast.
    ///
    /// No hop budget here. The budget belongs to distance - a trace twenty provinces long is
    /// still a trace - and spending it here would cut every country wider than twenty provinces
    /// in half on the first day of the campaign.
    ///
    /// A held coast in the same sea basin is another entry into the land network. Flood
    /// inland from that entry as well: an island depot behind its harbour is connected,
    /// not an isolated emergency depot. Blockade still reduces the delivered throughput.
    /// </summary>
    private static void TraceNetwork(Nation nation, ProvinceTopology topology, ushort[] controllers,
        int generation, Scratch board)
    {
        var reach = board.Reach;
        board.Frontier.Clear();
        foreach (var root in board.Roots)
        {
            if (reach[root] == generation) continue;
            reach[root] = generation;
            board.Frontier.Enqueue(root);
        }
        while (board.Frontier.Count > 0)
        {
            var province = board.Frontier.Dequeue();
            foreach (var neighbor in topology.Neighbors[province])
            {
                if (reach[neighbor] == generation || !Passable(nation, controllers[neighbor], board)) continue;
                reach[neighbor] = generation;
                board.Frontier.Enqueue(neighbor);
            }
        }

        var coasts = SeaLanes.CoastalProvinces(topology);
        if (coasts.Count == 0) return;
        var openBasins = new HashSet<int>();
        foreach (var coast in coasts)
        {
            if (reach[coast] == generation && controllers[coast] == nation.MapIndex)
            {
                var basin = SeaLanes.HarbourBasin(topology, coast);
                if (basin >= 0) openBasins.Add(basin);
            }
        }
        if (openBasins.Count == 0) return;
        foreach (var coast in coasts)
        {
            if (reach[coast] != generation && controllers[coast] == nation.MapIndex &&
                openBasins.Contains(SeaLanes.HarbourBasin(topology, coast)))
            {
                reach[coast] = generation;
                board.Frontier.Enqueue(coast);
            }
        }
        while (board.Frontier.Count > 0)
        {
            var province = board.Frontier.Dequeue();
            foreach (var neighbor in topology.Neighbors[province])
            {
                if (reach[neighbor] == generation || !Passable(nation, controllers[neighbor], board)) continue;
                reach[neighbor] = generation;
                board.Frontier.Enqueue(neighbor);
            }
        }
    }

    private static void SetStranded(Army army)
    {
        army.CutOff = true;
        army.SupplyDistance = -1;
        army.SupplySourceStateMapIndex = -1;
        army.SupplyCongestion = 0f;
        // The stores and the organisation fall on the daily clock (AgeCutOff), not on this
        // trace. The trace only says the road is gone. Counting a day here as well made
        // every third day worth two, and a lerp toward empty ignored the dry-out define.
    }

    private static bool ProvisionVisitor(Campaign c, Nation nation, Army army)
    {
        var at = army.ProvinceId;
        if (army.EmbarkedFleetId >= 0 || at <= 0 || at >= c.ProvinceController.Length) return false;
        var controller = c.ProvinceController[at];
        if (controller == nation.MapIndex || controller == 0 ||
            !c.NationsByMapIndex().TryGetValue(controller, out var host) || MilitaryAccess.Hostile(c, nation.Id, host.Id)) return false;
        // The host provisions an authorised visitor or an interned column awaiting return;
        // this is not a friendly land trace into an enemy pocket.
        MilitaryAccess.Provision(army);
        return true;
    }

    /// <summary>
    /// One campaign day inside a closed pocket. Supply and organisation close on the floor
    /// across <see cref="Defines.SupplyCutoffDays"/>; the formation is not removed.
    ///
    /// Not from the first morning. A pocket lives first off what was stockpiled where it stands
    /// (StrandedGarrisons.AssessStores): the day the ring closes its stores are counted, each day
    /// takes its share of them - less where an air bridge or a held port or town brings something
    /// in - and only when they are gone does the hunger clock, <see cref="Army.DaysCutOff"/>,
    /// start. A pocket that was a death sentence from the first hour gave nobody time to relieve
    /// it and made every rising's garrison starve where it stood.
    /// </summary>
    private static void AgeCutOff(Campaign campaign)
    {
        var span = Math.Max(1, campaign.Defines.SupplyCutoffDays);
        var add = Math.Max(1, campaign.TickHours / 24);
        var orgFloor = campaign.Defines.BattleBreakOrganisation;
        var storesOn = campaign.Defines.StrandedGarrisonsEnabled;
        Dictionary<int, float> cutOffDivisions = null;
        foreach (var army in campaign.Armies)
        {
            if (!army.CutOff)
            {
                // The road is open: the next pocket counts its stores afresh.
                army.PocketStores = -1f;
                army.PocketSinceDay = -1;
                continue;
            }
            if (army.Divisions <= 0f || army.EmbarkedFleetId >= 0) continue;
            if (MilitaryAccess.MustReturn(campaign, army)) continue;
            if (storesOn)
            {
                if (army.PocketStores < 0f)
                {
                    army.PocketStores = StrandedGarrisons.AssessStores(campaign, army);
                    army.PocketSinceDay = campaign.ElapsedDays;
                }
                if (army.PocketStores > 0f)
                {
                    cutOffDivisions ??= StrandedGarrisons.CutOffDivisions(campaign);
                    var draw = add * StrandedGarrisons.DailyDraw(campaign, army, cutOffDivisions);
                    army.PocketStores = MathF.Max(0f, army.PocketStores - draw);
                    // Rationed, not starving: the stores keep it near a fixed level of supply.
                    army.SupplyLevel = M.Lerp(army.SupplyLevel, campaign.Defines.PocketStoresSupplyLevel, 0.25f);
                    continue;
                }
            }
            var before = army.DaysCutOff;
            army.DaysCutOff = before + add;
            if (before >= span)
            {
                army.SupplyLevel = 0f;
                if (army.Organisation > orgFloor) army.Organisation = orgFloor;
                continue;
            }
            var close = Math.Clamp(add / (float)(span - before), 0f, 1f);
            army.SupplyLevel = BleedDown(army.SupplyLevel, 0f, close);
            army.Organisation = BleedDown(army.Organisation, orgFloor, close);
        }
    }

    /// <summary>
    /// Keep a closed pocket from climbing back above the dry-out. Recovery and a delivery
    /// both run after the morning clock; neither may put a spent grouping back into an attack.
    /// </summary>
    public static void HoldCauldron(Campaign campaign, Army army)
    {
        if (campaign is null || army is null || !army.CutOff || army.DaysCutOff <= 0) return;
        var span = Math.Max(1, campaign.Defines.SupplyCutoffDays);
        var spent = Math.Clamp(army.DaysCutOff / (float)span, 0f, 1f);
        var orgFloor = campaign.Defines.BattleBreakOrganisation;
        var orgCeiling = orgFloor + (1f - orgFloor) * (1f - spent);
        var supplyCeiling = 1f - spent;
        if (army.Organisation > orgCeiling) army.Organisation = orgCeiling;
        if (army.SupplyLevel > supplyCeiling) army.SupplyLevel = supplyCeiling;
    }

    /// <summary>Move toward a lower target. A value already under the target stays there.</summary>
    private static float BleedDown(float current, float floor, float close)
    {
        var next = current + (floor - current) * close;
        return next < current ? next : current;
    }

    /// <summary>
    /// What a player reads about one formation's supply. A pure read: it does not trace,
    /// spend, or write the campaign.
    /// </summary>
    public static string DescribeSupply(Campaign campaign, Army army)
    {
        if (army is null) return "";
        var span = Math.Max(1, campaign?.Defines.SupplyCutoffDays ?? 14);
        if (army.CutOff)
        {
            var inPocket = army.PocketSinceDay >= 0 && campaign is not null
                ? Math.Max(0, campaign.ElapsedDays - army.PocketSinceDay)
                : army.DaysCutOff;
            var days = Loc.Format("Days in the cauldron: {0}.", inPocket);
            var head = Loc.Get("Cut off.") + " " + Loc.Get("No path to a supply hub.") + " " + days;
            var pocket = StrandedGarrisons.PocketLine(campaign, army);
            if (pocket.Length > 0) head += " " + pocket;
            var relief = StrandedGarrisons.ReliefLine(campaign, army);
            if (relief.Length > 0) head += " " + relief;
            var stores = DepotDays(campaign, army);
            if (stores.Length > 0) head += " " + stores;
            // Living off local stores: the hunger clock has not started.
            if (army.PocketStores > 0f) return Finish(campaign, army, head);
            if (army.DaysCutOff >= span)
                return Finish(campaign, army, head + " " + Loc.Get("Organisation is spent and it cannot keep attacking."));
            return Finish(campaign, army, head + " " + Loc.Format("{0} days until supply and organisation are spent.",
                Math.Max(0, span - army.DaysCutOff)));
        }
        var openStores = DepotDays(campaign, army);
        var openTail = openStores.Length > 0 ? " " + openStores : "";
        if (army.SupplyCongestion < 0.999f)
            return Finish(campaign, army, Loc.Get("Thinning.") + " " + Loc.Get("The supply hub is over capacity.") + openTail);
        return Finish(campaign, army, Loc.Get("Fed.") + " " + Loc.Get("The supply line reaches a hub.") + openTail);
    }

    /// <summary>
    /// Hub, road length and a blockade, from fields and fleet pressure the trace already computed.
    /// A read. There is no stored sea-route flag, so a sea leg is not invented here.
    /// </summary>
    private static string Finish(Campaign campaign, Army army, string head)
    {
        var road = Road(campaign, army);
        var blockade = BlockadeLine(campaign, army);
        if (road.Length > 0) head += " " + road;
        if (blockade.Length > 0) head += " " + blockade;
        return head;
    }

    private static string Road(Campaign campaign, Army army)
    {
        if (campaign is null || army.SupplySourceStateMapIndex < 0 || army.SupplyDistance < 0) return "";
        var hub = campaign.States.Find(state => state.MapIndex == army.SupplySourceStateMapIndex);
        var name = hub is null || string.IsNullOrEmpty(hub.Name)
            ? army.SupplySourceStateMapIndex.ToString()
            : hub.Name;
        return Loc.Format("The hub is {0}, {1} provinces along the road.", name, army.SupplyDistance);
    }

    private static string BlockadeLine(Campaign campaign, Army army)
    {
        if (campaign?.ProvinceMap is not { } map || army.ProvinceId <= 0 || army.ProvinceId > map.ProvinceCount)
            return "";
        var nation = campaign.Nations.Find(candidate => candidate.Id == army.NationId);
        if (nation is null) return "";
        var centre = map.CenterOf(army.ProvinceId);
        if (NavalOperations.BlockadePressure(campaign, nation, centre.Latitude, centre.Longitude) <= 0f) return "";
        return Loc.Get("A blockade is sitting on this coast.");
    }

    /// <summary>
    /// Ammunition and oil still in the national depot, at yesterday's draw. Shown with the
    /// cauldron clock. A read: it does not seed the depot or spend it.
    /// </summary>
    private static string DepotDays(Campaign campaign, Army army)
    {
        if (campaign is null || army is null || !campaign.Defines.StrategicStockpilesEnabled) return "";
        var nation = campaign.NationById(army.NationId);
        if (nation is null) return "";
        return OnHand(nation, "Ammunition",
                "Ammunition in the depot lasts {0} days.", "The depot is not drawing ammunition.")
            + " " + OnHand(nation, "Oil",
                "Oil in the depot lasts {0} days.", "The depot is not drawing oil.");
    }

    private static string OnHand(Nation nation, string good, string lasts, string idle)
    {
        var days = StrategicStockpiles.DaysOnHand(nation, good);
        return days is int n ? Loc.Format(lasts, n) : Loc.Get(idle);
    }

    private static readonly string[] AttackCoverGoods = { "Ammunition", "Oil" };

    /// <summary>
    /// Yesterday's ammunition or oil cover was recorded under <see cref="Defines.SupplyAttackCoverageFloor"/>.
    /// A missing record is a full cover: a quiet depot is not a refusal. A read. It does not
    /// seed the depot, spend it, or move the formation. Nation id is not a list index.
    /// </summary>
    public static bool DepotShortOfAttack(Campaign campaign, Army army)
    {
        if (campaign is null || army is null || !campaign.Defines.StrategicStockpilesEnabled) return false;
        var floor = campaign.Defines.SupplyAttackCoverageFloor;
        if (!float.IsFinite(floor) || floor <= 0f) return false;
        var nation = campaign.Nations.Find(candidate => candidate.Id == army.NationId);
        if (nation is null) return false;
        foreach (var good in AttackCoverGoods)
        {
            if (!nation.StockpileCoverage.TryGetValue(good, out var met) || !float.IsFinite(met)) continue;
            if (met < floor) return true;
        }
        return false;
    }

    /// <summary>The lower recorded ammunition or oil cover, as a whole percent. 100 when neither is recorded.</summary>
    public static int ShortCoverPercent(Campaign campaign, Army army)
    {
        var nation = campaign?.Nations.Find(candidate => candidate.Id == army?.NationId);
        if (nation is null) return 100;
        var lowest = 100;
        var any = false;
        foreach (var good in AttackCoverGoods)
        {
            if (!nation.StockpileCoverage.TryGetValue(good, out var met) || !float.IsFinite(met)) continue;
            any = true;
            var percent = WholePercent(met);
            if (percent < lowest) lowest = percent;
        }
        return any ? lowest : 100;
    }

    /// <summary>The attack floor as a whole percent. Half is 50. A read of the define.</summary>
    public static int AttackCoverPercent(Campaign campaign)
    {
        var floor = campaign?.Defines.SupplyAttackCoverageFloor ?? 0.5f;
        if (!float.IsFinite(floor)) floor = 0.5f;
        return (int)MathF.Round(Math.Clamp(floor, 0f, 1f) * 100f);
    }

    private static int WholePercent(float share) =>
        (int)MathF.Floor(Math.Clamp(share, 0f, 1f) * 100f + 0.0001f);
}
