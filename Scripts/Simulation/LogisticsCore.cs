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
    // The traces themselves run side by side, each on a scratch of its own (WorkerScratch);
    // the per-campaign board holds only what all of them read.
    private const int DenseMapLimit = 4096;
    private const int DenseNationLimit = 4096;

    private sealed class Scratch
    {
        public int[] Distance = Array.Empty<int>();
        public int[] Source = Array.Empty<int>();
        public int[] Stamp = Array.Empty<int>();
        // Which provinces the network reaches from its roots, stamped like the trace below.
        public int[] Reach = Array.Empty<int>();
        public int[] QueueBuf = Array.Empty<int>();
        public int Generation;

        public readonly List<int> Roots = new();
        public readonly List<int> ActiveHubStates = new();

        // Per-state indexed generational tables (zero Dictionary overhead during trace)
        public int[] HubProvByState = Array.Empty<int>();
        public int[] HubGenByState = Array.Empty<int>();
        public byte[] HubFlagsByState = Array.Empty<byte>(); // bit 0: isolated, bit 1: forward

        public float[] DemandByState = Array.Empty<float>();
        public int[] DemandGenByState = Array.Empty<int>();
        public int DemandGen;

        public float[] UnreachedByState = Array.Empty<float>();
        public int[] UnreachedGenByState = Array.Empty<int>();
        public int UnreachedGen;

        public ScenarioState[] StateByIdx = Array.Empty<ScenarioState>();
        public int[] StateGenByIdx = Array.Empty<int>();
        public int StateGen;
        public readonly List<ScenarioState> NationStates = new();

        public int[] HeldCountByState = Array.Empty<int>();
        public int[] HeldGenByState = Array.Empty<int>();
        public int HeldGen;

        public int[] LandCountByState = Array.Empty<int>();
        public int MaxStateMapIndex;
        public ProvinceTopology LandCounted;

        // Per-nation map/basin generational stamps (zero HashSet overhead inside BFS)
        public int[] CorridorStamp = Array.Empty<int>();
        public int CorridorGen;
        public int[] PatronStamp = Array.Empty<int>();
        public int PatronGen;
        public bool HasPatrons;
        public int[] OpenBasinStamp = Array.Empty<int>();
        public int OpenBasinGen;

        // Shared board dense tables for provinces & armies (indexed by MapIndex / NationId)
        public readonly List<int>[] ControlledDense = new List<int>[DenseMapLimit];
        public readonly List<int> ControlledUsedMaps = new();
        public readonly Dictionary<ushort, List<int>> ControlledOverflow = new();

        public List<Army>[] ArmiesByNationDense = new List<Army>[DenseNationLimit];
        public readonly List<int> ArmiesUsedNations = new();
        public readonly Dictionary<int, List<Army>> ArmiesOverflow = new();

        public readonly List<int> Ports = new();
        // The fleets blockading the country being traced, found once for all its formations.
        public readonly List<Fleet> Blockaders = new();
        // The countries to trace today, in the list's order (shared board only).
        public readonly List<(Nation Nation, List<Army> Armies, List<int> Held)> Due = new();
        public readonly List<Nation> LivingNations = new();
        public readonly List<Sponsorship> Level2Sponsorships = new();

        public void EnsureStateCapacity(int stateIndex)
        {
            if ((uint)stateIndex < (uint)HubProvByState.Length) return;
            var cap = Math.Max(stateIndex + 128, Math.Max(256, HubProvByState.Length * 2));
            Array.Resize(ref HubProvByState, cap);
            Array.Resize(ref HubGenByState, cap);
            Array.Resize(ref HubFlagsByState, cap);
            Array.Resize(ref DemandByState, cap);
            Array.Resize(ref DemandGenByState, cap);
            Array.Resize(ref UnreachedByState, cap);
            Array.Resize(ref UnreachedGenByState, cap);
            Array.Resize(ref StateByIdx, cap);
            Array.Resize(ref StateGenByIdx, cap);
            Array.Resize(ref HeldCountByState, cap);
            Array.Resize(ref HeldGenByState, cap);
        }

        public void EnsureMapStampCapacity(int mapIndex)
        {
            if ((uint)mapIndex < (uint)CorridorStamp.Length) return;
            var cap = Math.Max(mapIndex + 64, Math.Max(512, CorridorStamp.Length * 2));
            Array.Resize(ref CorridorStamp, cap);
            Array.Resize(ref PatronStamp, cap);
        }
    }

    private static readonly ConditionalWeakTable<Campaign, Scratch> Boards = new();

    /// <summary>Countries traced between two seams of the stage.</summary>
    private const int TraceHandful = 64;

    /// <summary>
    /// The per-country half of the scratch, one per thread that traces. The shared board keeps
    /// who holds what and whose armies are where, which every trace reads; this keeps what one
    /// trace writes. Its generation stamp is its own, so a scratch that traced another campaign
    /// on the same thread (a test, a bench) carries nothing over: an entry counts only if it
    /// bears the stamp of the trace now running, and the regions' land count is kept per map.
    /// </summary>
    [ThreadStatic] private static Scratch _worker;

    private static Scratch WorkerScratch(int length, int maxStateIndex, int maxMapIndex)
    {
        var work = _worker ??= new Scratch();
        if (work.Distance.Length < length)
        {
            work.Distance = new int[length];
            work.Source = new int[length];
            work.Stamp = new int[length];
            work.Reach = new int[length];
            work.QueueBuf = new int[length];
            work.Generation = 0;
        }
        if (maxStateIndex >= 0) work.EnsureStateCapacity(maxStateIndex);
        if (maxMapIndex >= 0) work.EnsureMapStampCapacity(maxMapIndex);
        return work;
    }

    /// <summary>
    /// Provinces the last supply trace recorded for one map flag in this campaign.
    /// Empty when this world has not traced. Reading does not trace and does not write the world.
    /// </summary>
    internal static IReadOnlyList<int> ControlledProvinces(Campaign campaign, int mapIndex)
    {
        if (campaign is null || mapIndex <= 0 || mapIndex > ushort.MaxValue ||
            !Boards.TryGetValue(campaign, out var board)) return Array.Empty<int>();
        if (mapIndex < DenseMapLimit)
            return board.ControlledDense[mapIndex] ?? (IReadOnlyList<int>)Array.Empty<int>();
        return board.ControlledOverflow.TryGetValue((ushort)mapIndex, out var list) ? list : Array.Empty<int>();
    }

    /// <summary>
    /// How many land provinces each region has, counted once per map on the campaign's shared
    /// board. Asked on the stepping thread before the traces are handed out, and only on a day
    /// a trace would have asked it, so it is counted on the same day it always was: a count
    /// kept per worker would be taken whenever that worker first traced, and two workers could
    /// then disagree about the same region.
    /// </summary>
    private static void EnsureLandCounted(Campaign campaign, Scratch shared)
    {
        var topology = campaign.ProvinceMap;
        if (ReferenceEquals(shared.LandCounted, topology)) return;
        var p2s = topology.ProvinceToState;
        var maxState = 0;
        for (var p = 1; p <= topology.ProvinceCount; p++)
            if (p2s[p] > maxState) maxState = p2s[p];
        if (shared.LandCountByState.Length <= maxState)
            shared.LandCountByState = new int[maxState + 1];
        else
            Array.Clear(shared.LandCountByState, 0, maxState + 1);
        var landArr = shared.LandCountByState;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (!topology.IsWater(province))
                landArr[p2s[province]]++;
        shared.MaxStateMapIndex = maxState;
        shared.LandCounted = topology;
    }

    /// <summary>How many land provinces of each region the country holds now.</summary>
    private static void CountHeld(Campaign campaign, List<int> territory, Scratch board)
    {
        var topology = campaign.ProvinceMap;
        if (++board.HeldGen == int.MaxValue)
        {
            Array.Clear(board.HeldGenByState);
            board.HeldGen = 1;
        }
        var hGen = board.HeldGen;
        var p2s = topology.ProvinceToState;
        for (var ti = 0; ti < territory.Count; ti++)
        {
            var province = territory[ti];
            if (topology.IsWater(province)) continue;
            var state = p2s[province];
            if (state >= board.HeldGenByState.Length) board.EnsureStateCapacity(state);
            if (board.HeldGenByState[state] != hGen)
            {
                board.HeldGenByState[state] = hGen;
                board.HeldCountByState[state] = 1;
            }
            else
            {
                board.HeldCountByState[state]++;
            }
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
    private static void ForwardDepot(Campaign campaign, int province, int generation, bool connected, Scratch board, Scratch shared)
    {
        if (!connected || campaign.Defines.ForwardDepotHeldShare > 1f) return;
        var topology = campaign.ProvinceMap;
        var state = topology.ProvinceToState[province];
        if (state >= board.HubGenByState.Length) board.EnsureStateCapacity(state);
        if (board.HubGenByState[state] == generation) return;
        var land = state < shared.LandCountByState.Length ? shared.LandCountByState[state] : 0;
        if (land <= 0) return;
        var held = board.HeldGenByState[state] == board.HeldGen ? board.HeldCountByState[state] : 0;
        if (held < land * campaign.Defines.ForwardDepotHeldShare) return;
        board.HubGenByState[state] = generation;
        board.HubProvByState[state] = province;
        board.HubFlagsByState[state] = 2; // forward
        board.ActiveHubStates.Add(state);
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same supply pass, yielded inside the province scan and between countries.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(LogisticsSystem))) yield break;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) yield break;
        // Every third day, and once on it. Without the first half of this a campaign
        // stepping an hour at a time rebuilt the whole supply network twenty-four times on
        // the third day and not at all on the other two.
        if (!campaign.StepOpensNewDay) yield break;
        // Every third day the roads are traced again. The days inside a pocket are still
        // days, so the clock below runs on the days the trace sits out as well.
        if (campaign.ElapsedDays <= 1 || campaign.ElapsedDays % 3 == 0)
        {

        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var board = Boards.GetOrCreateValue(campaign);

        // Who holds what. The lists are kept between ticks and emptied rather than dropped,
        // so the province scan costs the scan and nothing else.
        for (var i = 0; i < board.ControlledUsedMaps.Count; i++)
            board.ControlledDense[board.ControlledUsedMaps[i]]!.Clear();
        board.ControlledUsedMaps.Clear();
        if (board.ControlledOverflow.Count > 0)
            foreach (var list in board.ControlledOverflow.Values) list.Clear();
        var scanned = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (++scanned % 2048 == 0 && Campaign.StageOverdue()) yield return 0;
            var map = controllers[province];
            if (map == 0) continue;
            if (map < DenseMapLimit)
            {
                var list = board.ControlledDense[map];
                if (list is null) board.ControlledDense[map] = list = new List<int>();
                if (list.Count == 0) board.ControlledUsedMaps.Add(map);
                list.Add(province);
            }
            else
            {
                if (!board.ControlledOverflow.TryGetValue(map, out var list))
                    board.ControlledOverflow[map] = list = new List<int>();
                list.Add(province);
            }
        }

        // One pass over the armies instead of one pass per nation. With two hundred and
        // sixty-one nations and fourteen hundred formations that is three hundred and
        // sixty-five thousand comparisons a tick turned into fourteen hundred.
        for (var i = 0; i < board.ArmiesUsedNations.Count; i++)
            board.ArmiesByNationDense[board.ArmiesUsedNations[i]]!.Clear();
        board.ArmiesUsedNations.Clear();
        if (board.ArmiesOverflow.Count > 0)
            foreach (var list in board.ArmiesOverflow.Values) list.Clear();
        var indexed = 0;
        var allArmies = campaign.Armies;
        for (var ai = 0; ai < allArmies.Count; ai++)
        {
            if (++indexed % 512 == 0 && Campaign.StageOverdue()) yield return 0;
            var army = allArmies[ai];
            var nid = army.NationId;
            if ((uint)nid < (uint)board.ArmiesByNationDense.Length)
            {
                var list = board.ArmiesByNationDense[nid];
                if (list is null) board.ArmiesByNationDense[nid] = list = new List<Army>();
                if (list.Count == 0) board.ArmiesUsedNations.Add(nid);
                list.Add(army);
            }
            else
            {
                if (!board.ArmiesOverflow.TryGetValue(nid, out var list))
                    board.ArmiesOverflow[nid] = list = new List<Army>();
                list.Add(army);
            }
        }

        var length = topology.ProvinceCount + 1;

        if (Campaign.StageOverdue()) yield return 0;
        campaign.NationsByMapIndex();
        campaign.StateByMapIndex(1);
        if (campaign.Nations.Count > 0) campaign.StatesOf(campaign.Nations[0]);
        Warfare.LiveWars(campaign);
        Warfare.OpenWars(campaign);
        MilitaryBootstrap.Personnel("", 0);
        SeaLanes.CoastalProvinceArray(topology);
        var due = board.Due;
        due.Clear();
        var nations = campaign.ActiveNations;
        var livingNations = board.LivingNations;
        livingNations.Clear();
        var maxMapIndex = 0;
        for (var i = 0; i < nations.Count; i++)
        {
            var nation = nations[i];
            if (nation.MapIndex > maxMapIndex) maxMapIndex = nation.MapIndex;
            if (nation.Population > 0 && nation.MapIndex > 0) livingNations.Add(nation);
            var nid = nation.Id;
            var armies = (uint)nid < (uint)board.ArmiesByNationDense.Length
                ? board.ArmiesByNationDense[nid]
                : board.ArmiesOverflow.GetValueOrDefault(nid);
            if (armies is null || armies.Count == 0) continue;
            var nMap = nation.MapIndex;
            List<int> held = null;
            if (nMap > 0)
            {
                held = nMap < DenseMapLimit
                    ? board.ControlledDense[nMap]
                    : board.ControlledOverflow.GetValueOrDefault((ushort)nMap);
            }
            due.Add((nation, armies, held));
            if (held is { Count: > 0 }) EnsureLandCounted(campaign, board);
        }
        var sponsorships = board.Level2Sponsorships;
        sponsorships.Clear();
        if (due.Count > 0)
        {
            var allSponsorships = campaign.Sponsorships;
            for (var i = 0; i < allSponsorships.Count; i++)
                if (allSponsorships[i].Level >= 2) sponsorships.Add(allSponsorships[i]);
        }
        var maxStateIndex = board.MaxStateMapIndex;
        // In handfuls, with a seam between them: a late world has three hundred countries in the
        // field, and the whole trace in one piece was a stage of its own on one thread.
        for (var start = 0; start < due.Count; start += TraceHandful)
        {
            var end = Math.Min(due.Count, start + TraceHandful);
            DeterministicParallel.ForRange(campaign, start, end, (from, to) =>
            {
                var work = WorkerScratch(length, maxStateIndex, maxMapIndex);
                for (var index = from; index < to; index++)
                {
                    var (nation, armies, held) = due[index];
                    UpdateNation(campaign, nation, armies, held, work, board);
                }
            }, minItems: 4);
            if (end < due.Count && Campaign.StageOverdue()) yield return 0;
        }
        } // The trace above is every third day. The clock below is every day.

        if (Campaign.StageOverdue()) yield return 0;
        AgeCutOff(campaign);
    }

    private static void UpdateNation(Campaign campaign, Nation nation, List<Army> armies, List<int> territory, Scratch board,
        Scratch shared)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null || territory is null || territory.Count == 0)
        {
            // A side holding no ground at all has no road to trace. A rising in that state is
            // not therefore starving: its men are in the villages (see below, and Partisans).
            var landless = topology is not null && controllers is not null && Partisans.MayForage(campaign, nation);
            if (landless)
            {
                CollectCorridors(campaign, nation, board, shared);
                if (++board.UnreachedGen == int.MaxValue) { Array.Clear(board.UnreachedGenByState); board.UnreachedGen = 1; }
                for (var ai = 0; ai < armies.Count; ai++)
                    if (!Visiting(campaign, nation, armies[ai])) CountUnreached(topology, armies[ai], board);
            }
            for (var ai = 0; ai < armies.Count; ai++)
            {
                var army = armies[ai];
                if (ProvisionVisitor(campaign, nation, army)) continue;
                if (landless && Forage(campaign, nation, army, board)) continue;
                SetStranded(army);
            }
            return;
        }

        if (++board.StateGen == int.MaxValue) { Array.Clear(board.StateGenByIdx); board.StateGen = 1; }
        var sGen = board.StateGen;
        var nationStates = board.NationStates;
        nationStates.Clear();
        var statesArr = campaign.StatesOf(nation);
        for (var si = 0; si < statesArr.Length; si++)
        {
            var st = statesArr[si];
            var idx = st.MapIndex;
            if (idx >= board.StateGenByIdx.Length) board.EnsureStateCapacity(idx);
            if (board.StateGenByIdx[idx] != sGen) nationStates.Add(st);
            board.StateGenByIdx[idx] = sGen;
            board.StateByIdx[idx] = st;
        }

        // A new generation is this nation's claim on the scratch: an entry counts as
        // written only if it carries the number below. Nothing has to be cleared.
        if (++board.Generation == int.MaxValue)
        {
            Array.Clear(board.Stamp);
            Array.Clear(board.Reach);
            Array.Clear(board.HubGenByState);
            board.Generation = 1;
        }
        var generation = board.Generation;
        var distance = board.Distance;
        var source = board.Source;
        var stamp = board.Stamp;

        var reach = board.Reach;
        CollectCorridors(campaign, nation, board, shared);
        var insurgent = true;
        var p2n = topology.ProvinceToNation;
        var p2s = topology.ProvinceToState;
        for (var ti = 0; ti < territory.Count; ti++)
            if (p2n[territory[ti]] == nation.MapIndex) { insurgent = false; break; }
        CollectRoots(campaign, nation, territory, board, shared);
        if (insurgent && board.Roots.Count == 0 && InsurgentSeat(campaign, nation, territory) is var seat and > 0) board.Roots.Add(seat);
        TraceNetwork(nation, topology, controllers, generation, board);

        board.ActiveHubStates.Clear();
        CountHeld(campaign, territory, board);
        for (var ti = 0; ti < territory.Count; ti++)
        {
            var province = territory[ti];
            // A lake keeps no depot, however much of it a country owns.
            if (topology.IsWater(province)) continue;
            if (!insurgent && p2n[province] != nation.MapIndex)
            {
                ForwardDepot(campaign, province, generation, reach[province] == generation, board, shared);
                continue;
            }
            var isConnected = reach[province] == generation;
            var state = p2s[province];
            if (state >= board.HubGenByState.Length) board.EnsureStateCapacity(state);
            if (isConnected)
            {
                if (board.HubGenByState[state] != generation)
                {
                    board.HubGenByState[state] = generation;
                    board.ActiveHubStates.Add(state);
                    board.HubProvByState[state] = province;
                    board.HubFlagsByState[state] = 0;
                }
                else if (board.HubFlagsByState[state] != 0)
                {
                    board.HubProvByState[state] = province;
                    board.HubFlagsByState[state] = 0;
                }
                continue;
            }
            if (board.HubGenByState[state] == generation) continue;
            var st = board.StateGenByIdx[state] == sGen ? board.StateByIdx[state] : null;
            if (st is null || st.Buildings.GetValueOrDefault(BuildingKind.Barracks) <= 0)
                continue;
            board.HubGenByState[state] = generation;
            board.ActiveHubStates.Add(state);
            board.HubProvByState[state] = province;
            board.HubFlagsByState[state] = 1; // isolated
        }

        var qBuf = board.QueueBuf;
        var qTail = 0;
        for (var hi = 0; hi < board.ActiveHubStates.Count; hi++)
        {
            var state = board.ActiveHubStates[hi];
            var province = board.HubProvByState[state];
            stamp[province] = generation;
            distance[province] = 0;
            source[province] = state;
            qBuf[qTail++] = province;
        }
        TraceDepots(nation, topology, controllers, generation, board, qTail);

        for (var ti = 0; ti < territory.Count; ti++)
        {
            var province = territory[ti];
            if (stamp[province] == generation || reach[province] != generation || topology.IsWater(province) ||
                p2n[province] != nation.MapIndex) continue;
            var state = p2s[province];
            if (state >= board.HubGenByState.Length || board.HubGenByState[state] != generation ||
                (board.HubFlagsByState[state] & 1) != 0) continue;
            stamp[province] = generation;
            distance[province] = 0;
            source[province] = state;
            qBuf[0] = province;
            TraceDepots(nation, topology, controllers, generation, board, 1);
        }

        SupplyBySea(campaign, nation, armies, controllers, generation, board);

        if (++board.DemandGen == int.MaxValue) { Array.Clear(board.DemandGenByState); board.DemandGen = 1; }
        var dGen = board.DemandGen;
        var throughputPerDiv = campaign.Defines.SupplyThroughputPerDivision;
        for (var ai = 0; ai < armies.Count; ai++)
        {
            var army = armies[ai];
            var at = army.ProvinceId;
            if (at <= 0 || at >= stamp.Length || stamp[at] != generation) continue;
            var sourceState = source[at];
            if (sourceState >= board.DemandGenByState.Length) board.EnsureStateCapacity(sourceState);
            var addDemand = army.Divisions * throughputPerDiv * UnitTypeCatalog.Get(army.TemplateId).SupplyUse;
            if (board.DemandGenByState[sourceState] != dGen)
            {
                board.DemandGenByState[sourceState] = dGen;
                board.DemandByState[sourceState] = addDemand;
            }
            else
            {
                board.DemandByState[sourceState] += addDemand;
            }
        }

        var forage = Partisans.MayForage(campaign, nation);
        if (forage)
        {
            if (++board.UnreachedGen == int.MaxValue) { Array.Clear(board.UnreachedGenByState); board.UnreachedGen = 1; }
            for (var ai = 0; ai < armies.Count; ai++)
            {
                var army = armies[ai];
                if (Visiting(campaign, nation, army)) continue;
                var at = army.ProvinceId;
                if (at > 0 && at < stamp.Length && stamp[at] == generation)
                {
                    var src = source[at];
                    if (!(src < board.HubGenByState.Length && board.HubGenByState[src] == generation &&
                          (board.HubFlagsByState[src] & 1) != 0))
                        continue;
                }
                CountUnreached(topology, army, board);
            }
        }

        var blockadersKnown = false;
        for (var ai = 0; ai < armies.Count; ai++)
        {
            var army = armies[ai];
            if (ProvisionVisitor(campaign, nation, army)) continue;
            if (army.EmbarkedFleetId >= 0)
            {
                army.CutOff = false;
                army.LivingOffLand = false;
                army.DaysCutOff = 0;
                army.SupplyDistance = 0;
                army.SupplyCongestion = 1f;
                continue;
            }

            var province = army.ProvinceId;
            if (province <= 0 || province >= stamp.Length || stamp[province] != generation)
            {
                if (!(forage && Forage(campaign, nation, army, board))) SetStranded(army);
                continue;
            }
            var sourceState = source[province];
            var state = (uint)sourceState < (uint)board.StateGenByIdx.Length && board.StateGenByIdx[sourceState] == sGen
                ? board.StateByIdx[sourceState]
                : null;
            var hubFlags = (uint)sourceState < (uint)board.HubGenByState.Length && board.HubGenByState[sourceState] == generation
                ? board.HubFlagsByState[sourceState]
                : (byte)0;
            var isIsolated = (hubFlags & 1) != 0;
            var isForward = (hubFlags & 2) != 0;

            var centre = topology.CenterOf(province);
            var damageFactor = state is not null ? (1f - MathF.Min(0.85f, state.WarDamage * 0.9f)) : 1f;
            var enclaveFactor = isIsolated ? 0.35f :
                isForward ? campaign.Defines.ForwardDepotThroughputShare : 1f;
            var capacity = (campaign.Defines.BaseStateSupplyThroughput + BuildingThroughput(campaign.Defines, state)) *
                          damageFactor * enclaveFactor *
                          WaterConditions.RoadThroughput(campaign, centre.Latitude);
            var rawDemand = (uint)sourceState < (uint)board.DemandGenByState.Length && board.DemandGenByState[sourceState] == dGen
                ? board.DemandByState[sourceState]
                : 0f;
            var demand = MathF.Max(0.01f, rawDemand);
            var traceRange = Math.Max(1, campaign.Defines.SupplyTraceRange);
            var lakeMultiplier = WaterConditions.LakeSupplyMultiplier(campaign, province);

            if (!blockadersKnown)
            {
                NavalOperations.Blockaders(campaign, nation, board.Blockaders);
                blockadersKnown = true;
            }
            var blockade = NavalOperations.BlockadePressure(campaign, board.Blockaders, centre.Latitude, centre.Longitude);
            CombatTempoRulesHook.EvaluateSupplyDelivery(
                capacity, demand, distance[province], traceRange, lakeMultiplier,
                blockade, campaign.Defines.FleetBlockadeSupplyPenalty,
                topology.TerrainAt(province).Supply, NationalModifiers.Factor(nation, "supply"),
                army.SupplyLevel,
                out var congestion, out _, out _, out _, out var nextSupplyLevel);
            army.SupplyDistance = distance[province];
            army.SupplySourceStateMapIndex = sourceState;
            army.SupplyCongestion = congestion;
            if (isIsolated)
            {
                if (forage && Forage(campaign, nation, army, board)) continue;
                army.LivingOffLand = false;
                army.CutOff = true;
                continue;
            }
            army.CutOff = false;
            army.LivingOffLand = false;
            army.DaysCutOff = 0;
            army.SupplyLevel = nextSupplyLevel;
        }

        var barracksTotal = 0;
        for (var si = 0; si < nationStates.Count; si++)
            barracksTotal += nationStates[si].Buildings.GetValueOrDefault(BuildingKind.Barracks);
        nation.ReinforcementCapacity = (MathF.Max(80f, MilitaryBootstrap.Personnel(nation.Tag, nation.Population) *
                                            campaign.Defines.ReinforcementCapacityShare) +
                                        barracksTotal * ReinforcementPerBarracks) * ReinforcementBudget(campaign, nation) *
                                       NationalModifiers.Factor(nation, "reinforcement_rate");
        if (campaign.ElapsedDays % 7 == 0) nation.ReinforcedLastWeek = 0f;
    }

    /// <summary>
    /// Walk from the queued distribution points without crossing hostile controllers.
    /// </summary>
    private static void TraceDepots(Nation nation, ProvinceTopology topology, ushort[] controllers, int generation, Scratch board, int queueTail)
    {
        var qBuf = board.QueueBuf;
        var qHead = 0;
        var stamp = board.Stamp;
        var dist = board.Distance;
        var src = board.Source;
        var neighbors = topology.Neighbors;
        var ownMap = (ushort)nation.MapIndex;
        var corrStamp = board.CorridorStamp;
        var corrGen = board.CorridorGen;
        while (qHead < queueTail)
        {
            var province = qBuf[qHead++];
            var adj = neighbors[province];
            var baseD = dist[province];
            var baseS = src[province];
            for (var ai = 0; ai < adj.Length; ai++)
            {
                var neighbor = adj[ai];
                if (stamp[neighbor] == generation) continue;
                var ctrl = controllers[neighbor];
                if (ctrl != ownMap && (ctrl == 0 || ctrl >= corrStamp.Length || corrStamp[ctrl] != corrGen))
                    continue;
                stamp[neighbor] = generation;
                // Lakes do not charge extra road hops; columns still cannot march across them.
                dist[neighbor] = baseD + (topology.IsWater(neighbor) ? 0 : 1);
                src[neighbor] = baseS;
                qBuf[queueTail++] = neighbor;
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
                ports = board.Ports;
                ports.Clear();
                var coasts = SeaLanes.CoastalProvinceArray(topology);
                for (var ci = 0; ci < coasts.Length; ci++)
                {
                    var coast = coasts[ci];
                    if (coast >= stamp.Length || stamp[coast] != generation || controllers[coast] != nation.MapIndex) continue;
                    var srcState = board.Source[coast];
                    var isIso = (uint)srcState < (uint)board.HubGenByState.Length &&
                                board.HubGenByState[srcState] == generation &&
                                (board.HubFlagsByState[srcState] & 1) != 0;
                    if (!isIso) ports.Add(coast);
                }
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

    private static int InsurgentSeat(Campaign campaign, Nation nation, List<int> territory)
    {
        var topology = campaign.ProvinceMap;
        var hq = nation.Movement?.HeadquartersProvinceId ?? 0;
        if (hq > 0 && territory.Contains(hq) && !topology.IsWater(hq)) return hq;
        var best = 0;
        long most = -1;
        for (var i = 0; i < territory.Count; i++)
        {
            var province = territory[i];
            if (topology.IsWater(province) || campaign.DestroyedProvinces.Contains(province)) continue;
            var people = campaign.StateByMapIndex(topology.ProvinceToState[province])?.Population ?? 0L;
            if (people <= most) continue;
            most = people;
            best = province;
        }
        return best;
    }

    private static void CollectCorridors(Campaign campaign, Nation nation, Scratch board, Scratch shared)
    {
        if (++board.CorridorGen == int.MaxValue) { Array.Clear(board.CorridorStamp); board.CorridorGen = 1; }
        var cGen = board.CorridorGen;
        var livingNations = shared.LivingNations;
        for (var i = 0; i < livingNations.Count; i++)
        {
            var host = livingNations[i];
            if (host.Id != nation.Id &&
                (host.AnswersTo(nation.Id) || nation.AnswersTo(host.Id)) &&
                !MilitaryAccess.Hostile(campaign, nation.Id, host.Id))
            {
                var mi = host.MapIndex;
                if (mi > 0)
                {
                    if (mi >= board.CorridorStamp.Length) board.EnsureMapStampCapacity(mi);
                    board.CorridorStamp[mi] = cGen;
                }
            }
        }
        var atWar = false;
        var wars = Warfare.LiveWars(campaign);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (war.Ended || !war.Involves(nation.Id)) continue;
            atWar = true;
            foreach (var id in war.Side(war.IsAttacking(nation.Id)))
            {
                if (id != nation.Id && campaign.NationById(id) is { } friend && friend.MapIndex > 0)
                {
                    var mi = friend.MapIndex;
                    if (mi >= board.CorridorStamp.Length) board.EnsureMapStampCapacity(mi);
                    board.CorridorStamp[mi] = cGen;
                }
            }
        }
        if (!atWar) return;
        for (var i = 0; i < livingNations.Count; i++)
        {
            var host = livingNations[i];
            if (host.Id != nation.Id && MilitaryAccess.Granted(campaign, nation.Id, host.Id) &&
                !MilitaryAccess.Hostile(campaign, nation.Id, host.Id))
            {
                var mi = host.MapIndex;
                if (mi > 0)
                {
                    if (mi >= board.CorridorStamp.Length) board.EnsureMapStampCapacity(mi);
                    board.CorridorStamp[mi] = cGen;
                }
            }
        }
    }

    private static bool Passable(Nation nation, ushort controller, Scratch board) =>
        controller == nation.MapIndex ||
        (controller != 0 && controller < board.CorridorStamp.Length && board.CorridorStamp[controller] == board.CorridorGen);

    private static void CollectRoots(Campaign campaign, Nation nation, List<int> territory, Scratch board, Scratch shared)
    {
        board.Roots.Clear();
        var origin = CapitalCore.SupplyOriginProvinceId(campaign, nation);
        if (origin > 0) board.Roots.Add(origin);
        PatronBorders(campaign, nation, territory, board, shared);
    }

    private static void PatronBorders(Campaign campaign, Nation nation, List<int> territory, Scratch board, Scratch shared)
    {
        var sponsorships = shared.Level2Sponsorships;
        if (sponsorships.Count == 0) return;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (++board.PatronGen == int.MaxValue) { Array.Clear(board.PatronStamp); board.PatronGen = 1; }
        var pGen = board.PatronGen;
        var anyPatron = false;
        for (var i = 0; i < sponsorships.Count; i++)
        {
            var backing = sponsorships[i];
            if (backing.ClientId == nation.Id &&
                campaign.NationById(backing.SponsorId) is { Population: > 0 } sponsor &&
                !CampaignDiplomacy.AtWar(campaign, sponsor.Id, nation.Id))
            {
                var mi = sponsor.MapIndex;
                if (mi > 0)
                {
                    if (mi >= board.PatronStamp.Length) board.EnsureMapStampCapacity(mi);
                    board.PatronStamp[mi] = pGen;
                    anyPatron = true;
                }
            }
        }
        if (!anyPatron) return;
        var patronStamp = board.PatronStamp;
        var ownMap = (ushort)nation.MapIndex;
        var neighbors = topology.Neighbors;
        for (var ti = 0; ti < territory.Count; ti++)
        {
            var province = territory[ti];
            if (controllers[province] != ownMap || topology.IsWater(province)) continue;
            var adj = neighbors[province];
            for (var ai = 0; ai < adj.Length; ai++)
            {
                var neighbor = adj[ai];
                var nCtrl = controllers[neighbor];
                if (nCtrl < patronStamp.Length && patronStamp[nCtrl] == pGen && !topology.IsWater(neighbor))
                {
                    board.Roots.Add(province);
                    break;
                }
            }
        }
    }

    private static void TraceNetwork(Nation nation, ProvinceTopology topology, ushort[] controllers,
        int generation, Scratch board)
    {
        var reach = board.Reach;
        var qBuf = board.QueueBuf;
        var qHead = 0;
        var qTail = 0;
        var roots = board.Roots;
        for (var ri = 0; ri < roots.Count; ri++)
        {
            var root = roots[ri];
            if (reach[root] == generation) continue;
            reach[root] = generation;
            qBuf[qTail++] = root;
        }
        var ownMap = (ushort)nation.MapIndex;
        var corrStamp = board.CorridorStamp;
        var corrGen = board.CorridorGen;
        var neighbors = topology.Neighbors;
        while (qHead < qTail)
        {
            var province = qBuf[qHead++];
            var adj = neighbors[province];
            for (var ai = 0; ai < adj.Length; ai++)
            {
                var neighbor = adj[ai];
                if (reach[neighbor] == generation) continue;
                var ctrl = controllers[neighbor];
                if (ctrl != ownMap && (ctrl == 0 || ctrl >= corrStamp.Length || corrStamp[ctrl] != corrGen))
                    continue;
                reach[neighbor] = generation;
                qBuf[qTail++] = neighbor;
            }
        }

        var coasts = SeaLanes.CoastalProvinceArray(topology);
        if (coasts.Length == 0) return;
        if (++board.OpenBasinGen == int.MaxValue) { Array.Clear(board.OpenBasinStamp); board.OpenBasinGen = 1; }
        var bGen = board.OpenBasinGen;
        var anyBasin = false;
        for (var ci = 0; ci < coasts.Length; ci++)
        {
            var coast = coasts[ci];
            if (reach[coast] == generation && controllers[coast] == ownMap)
            {
                var basin = SeaLanes.HarbourBasin(topology, coast);
                if (basin >= 0)
                {
                    if (basin >= board.OpenBasinStamp.Length)
                        Array.Resize(ref board.OpenBasinStamp, Math.Max(basin + 32, board.OpenBasinStamp.Length * 2));
                    board.OpenBasinStamp[basin] = bGen;
                    anyBasin = true;
                }
            }
        }
        if (!anyBasin) return;
        qHead = 0;
        qTail = 0;
        var basinStamp = board.OpenBasinStamp;
        for (var ci = 0; ci < coasts.Length; ci++)
        {
            var coast = coasts[ci];
            if (reach[coast] != generation && controllers[coast] == ownMap)
            {
                var basin = SeaLanes.HarbourBasin(topology, coast);
                if (basin >= 0 && basin < basinStamp.Length && basinStamp[basin] == bGen)
                {
                    reach[coast] = generation;
                    qBuf[qTail++] = coast;
                }
            }
        }
        while (qHead < qTail)
        {
            var province = qBuf[qHead++];
            var adj = neighbors[province];
            for (var ai = 0; ai < adj.Length; ai++)
            {
                var neighbor = adj[ai];
                if (reach[neighbor] == generation) continue;
                var ctrl = controllers[neighbor];
                if (ctrl != ownMap && (ctrl == 0 || ctrl >= corrStamp.Length || corrStamp[ctrl] != corrGen))
                    continue;
                reach[neighbor] = generation;
                qBuf[qTail++] = neighbor;
            }
        }
    }

    private static void CountUnreached(ProvinceTopology topology, Army army, Scratch board)
    {
        var at = army.ProvinceId;
        if (army.EmbarkedFleetId >= 0 || at <= 0 || at > topology.ProvinceCount) return;
        var region = topology.ProvinceToState[at];
        if (region >= board.UnreachedGenByState.Length) board.EnsureStateCapacity(region);
        if (board.UnreachedGenByState[region] != board.UnreachedGen)
        {
            board.UnreachedGenByState[region] = board.UnreachedGen;
            board.UnreachedByState[region] = army.Divisions;
        }
        else
        {
            board.UnreachedByState[region] += army.Divisions;
        }
    }

    private static bool Forage(Campaign campaign, Nation nation, Army army, Scratch board)
    {
        var topology = campaign.ProvinceMap;
        var at = army.ProvinceId;
        if (at <= 0 || at > topology.ProvinceCount || at >= campaign.ProvinceController.Length) return false;
        var region = topology.ProvinceToState[at];
        var standing = (uint)region < (uint)board.UnreachedGenByState.Length && board.UnreachedGenByState[region] == board.UnreachedGen
            ? board.UnreachedByState[region]
            : 0f;
        var amongFriends = Passable(nation, campaign.ProvinceController[at], board);
        return Partisans.LiveOffTheLand(campaign, nation, army, standing, amongFriends);
    }

    /// <summary>
    /// Whether the country is in a war today. A pocket is a thing of war: a formation of a
    /// country at peace with everybody, standing in an enclave no road of its own reaches, is
    /// not being starved by anyone, and does not go hungry or eat its stores (AgeCutOff).
    /// </summary>
    public static bool Besieged(Campaign campaign, int nationId)
    {
        var wars = Warfare.LiveWars(campaign);
        for (var index = 0; index < wars.Count; index++)
            if (!wars[index].Ended && wars[index].Involves(nationId)) return true;
        return false;
    }

    private static void SetStranded(Army army)
    {
        army.CutOff = true;
        army.LivingOffLand = false;
        army.SupplyDistance = -1;
        army.SupplySourceStateMapIndex = -1;
        army.SupplyCongestion = 0f;
        // The stores and the organisation fall on the daily clock (AgeCutOff), not on this
        // trace. The trace only says the road is gone. Counting a day here as well made
        // every third day worth two, and a lerp toward empty ignored the dry-out define.
    }

    private static bool ProvisionVisitor(Campaign c, Nation nation, Army army)
    {
        if (!Visiting(c, nation, army)) return false;
        // The host provisions an authorised visitor or an interned column awaiting return;
        // this is not a friendly land trace into an enemy pocket.
        MilitaryAccess.Provision(army);
        return true;
    }

    /// <summary>Whether the formation stands on the ground of a host that is not its enemy. A read.</summary>
    private static bool Visiting(Campaign c, Nation nation, Army army)
    {
        var at = army.ProvinceId;
        if (army.EmbarkedFleetId >= 0 || at <= 0 || at >= c.ProvinceController.Length) return false;
        var controller = c.ProvinceController[at];
        return controller != nation.MapIndex && controller != 0 &&
               c.NationsByMapIndex().TryGetValue(controller, out var host) && !MilitaryAccess.Hostile(c, nation.Id, host.Id);
    }

    /// <summary>
    /// One campaign day inside a closed pocket. Supply and organisation close on their hunger
    /// floors across <see cref="Defines.SupplyCutoffDays"/> and stop there
    /// (PocketHungerSupplyFloor, PocketHungerOrganisationFloor); the formation is not removed.
    /// The floors used to be nothing and the rout threshold, which is a formation with no
    /// fighting power standing one shove from a rout: it could not be said to be holding
    /// anything, and with the daily wasting (Attrition) it was gone in three weeks untouched.
    ///
    /// Not from the first morning. A pocket lives first off what was stockpiled where it stands
    /// (StrandedGarrisons.AssessStores): the day the ring closes its stores are counted, each day
    /// takes its share of them - less where an air bridge or a held port or town brings something
    /// in - and only when they are gone does the hunger clock, <see cref="Army.DaysCutOff"/>,
    /// start. A pocket that was a death sentence from the first hour gave nobody time to relieve
    /// it and made every rising's garrison starve where it stood.
    ///
    /// And only in a war. A country fighting nobody draws nothing from its stores, and its
    /// hunger clock runs backwards a day at a time: an enclave in peacetime trades through the
    /// neighbour around it, and the first morning of the next war must not find it already
    /// starved by a siege that ended years ago.
    /// </summary>
    [ThreadStatic] private static GenerationalStampSet _besiegedNations;

    private static void AgeCutOff(Campaign campaign)
    {
        var span = Math.Max(1, campaign.Defines.SupplyCutoffDays);
        var add = Math.Max(1, campaign.TickHours / 24);
        var orgFloor = M.Clamp01(campaign.Defines.PocketHungerOrganisationFloor);
        var supplyFloor = M.Clamp01(campaign.Defines.PocketHungerSupplyFloor);
        var storesOn = campaign.Defines.StrandedGarrisonsEnabled;
        Dictionary<int, float> cutOffDivisions = null;
        var besiegedBuilt = false;
        GenerationalStampSet besieged = null;
        var armies = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(campaign.Armies);
        for (var i = 0; i < armies.Length; i++)
        {
            var army = armies[i];
            if (!army.CutOff)
            {
                // The road is open: the next pocket counts its stores afresh.
                army.PocketStores = -1f;
                army.PocketSinceDay = -1;
                continue;
            }
            if (army.Divisions <= 0f || army.EmbarkedFleetId >= 0) continue;
            if (MilitaryAccess.MustReturn(campaign, army)) continue;
            if (storesOn && army.PocketStores < 0f)
            {
                army.PocketStores = StrandedGarrisons.AssessStores(campaign, army);
                army.PocketSinceDay = campaign.ElapsedDays;
            }
            // Nobody is besieging a country at peace: the stores keep, the clock unwinds, and
            // the formation is kept where rationing would keep it.
            if (!besiegedBuilt)
            {
                besiegedBuilt = true;
                besieged = _besiegedNations ??= new GenerationalStampSet();
                besieged.Clear();
                var wars = Warfare.LiveWars(campaign);
                for (var wi = 0; wi < wars.Count; wi++)
                {
                    var war = wars[wi];
                    if (war.Ended) continue;
                    besieged.Add(war.AttackerId);
                    besieged.Add(war.DefenderId);
                    foreach (var ally in war.AttackerAllies) besieged.Add(ally);
                    foreach (var ally in war.DefenderAllies) besieged.Add(ally);
                }
            }
            if (!besieged.Contains(army.NationId))
            {
                army.DaysCutOff = Math.Max(0, army.DaysCutOff - add);
                army.SupplyLevel = M.Lerp(army.SupplyLevel, campaign.Defines.PocketStoresSupplyLevel, 0.25f);
                continue;
            }
            if (storesOn && army.PocketStores > 0f)
            {
                cutOffDivisions ??= StrandedGarrisons.CutOffDivisions(campaign);
                var draw = add * StrandedGarrisons.DailyDraw(campaign, army, cutOffDivisions);
                army.PocketStores = MathF.Max(0f, army.PocketStores - draw);
                // Rationed, not starving: the stores keep it near a fixed level of supply.
                army.SupplyLevel = M.Lerp(army.SupplyLevel, campaign.Defines.PocketStoresSupplyLevel, 0.25f);
                continue;
            }
            var before = army.DaysCutOff;
            army.DaysCutOff = before + add;
            if (before >= span)
            {
                // What is found, bartered and taken from the dead: never less, never more.
                army.SupplyLevel = supplyFloor;
                if (army.Organisation > orgFloor) army.Organisation = orgFloor;
                continue;
            }
            var close = Math.Clamp(add / (float)(span - before), 0f, 1f);
            army.SupplyLevel = BleedDown(army.SupplyLevel, supplyFloor, close);
            army.Organisation = BleedDown(army.Organisation, orgFloor, close);
        }
    }

    /// <summary>
    /// Keep a closed pocket from climbing back above the dry-out. Recovery and a delivery
    /// both run after the morning clock; neither may put a spent grouping back into an attack.
    /// The ceilings come down to the hunger floors and rest there, so a pocket that has beaten
    /// off an assault comes round to its floor again - and no higher.
    /// </summary>
    public static void HoldCauldron(Campaign campaign, Army army)
    {
        if (campaign is null || army is null || !army.CutOff || army.DaysCutOff <= 0) return;
        var span = Math.Max(1, campaign.Defines.SupplyCutoffDays);
        var spent = Math.Clamp(army.DaysCutOff / (float)span, 0f, 1f);
        var orgFloor = M.Clamp01(campaign.Defines.PocketHungerOrganisationFloor);
        var orgCeiling = orgFloor + (1f - orgFloor) * (1f - spent);
        var supplyCeiling = MathF.Max(M.Clamp01(campaign.Defines.PocketHungerSupplyFloor), 1f - spent);
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
        // Fed by the people around it: there is no hub and no road to describe.
        if (army.LivingOffLand && !army.CutOff) return Partisans.ForageLine(campaign, army);
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
            // What happens next and what ends it, in so many words: a pocket is the one
            // place a player has to choose between waiting, relieving and breaking out, and
            // the old line ("days until supply and organisation are spent") read as a death
            // sentence with a date on it.
            var fate = StrandedGarrisons.FateLine(campaign, army);
            if (fate.Length > 0) head += " " + fate;
            return Finish(campaign, army, head);
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
        var hub = campaign.StateByMapIndex(army.SupplySourceStateMapIndex);
        var name = hub is null || string.IsNullOrEmpty(hub.Name)
            ? army.SupplySourceStateMapIndex.ToString()
            : hub.Name;
        return Loc.Format("The hub is {0}, {1} provinces along the road.", name, army.SupplyDistance);
    }

    private static string BlockadeLine(Campaign campaign, Army army)
    {
        if (campaign?.ProvinceMap is not { } map || army.ProvinceId <= 0 || army.ProvinceId > map.ProvinceCount)
            return "";
        var nation = campaign.NationById(army.NationId);
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
        var nation = campaign.NationById(army.NationId);
        if (nation is null || nation.StockpileCoverage.Count == 0) return false;
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
        var nation = army is null ? null : campaign?.NationById(army.NationId);
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
