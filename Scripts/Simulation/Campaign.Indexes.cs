using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using ZeroAlloc.Collections;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    private const int DenseIndexLimit = 65536;

    private sealed class ActiveWarsSnapshot
    {
        public readonly List<StrategicWar> Active;
        public Dictionary<int, StrategicWar> ById;
        public readonly int Revision;
        public readonly int Count;
        public readonly StrategicWar Head;
        public readonly StrategicWar Tail;
        public readonly bool TailWasEnded;

        public ActiveWarsSnapshot(List<StrategicWar> active, int revision, int count, StrategicWar head,
            StrategicWar tail, bool tailWasEnded)
        {
            Active = active;
            Revision = revision;
            Count = count;
            Head = head;
            Tail = tail;
            TailWasEnded = tailWasEnded;
        }
    }

    private readonly object _warSnapshotLock = new();
    private volatile ActiveWarsSnapshot _warSnapshot;

    /// <summary>
    /// All active (non-ended) wars in record order, cached on this campaign and automatically
    /// invalidated when <see cref="WarRevision"/> changes, wars are added/replaced, or an active
    /// war ends directly. Hot paths read this instead of walking historical ended wars.
    /// </summary>
    public IReadOnlyList<StrategicWar> ActiveWars => EnsureActiveWarsSnapshot().Active;

    /// <summary>Internal list view of <see cref="ActiveWars"/> for zero-allocation foreach/indexed iteration.</summary>
    internal List<StrategicWar> ActiveWarsList => EnsureActiveWarsSnapshot().Active;

    /// <summary>Look up any war (active or ended) by id using an invalidated per-campaign index.</summary>
    internal StrategicWar WarById(int id)
    {
        var snap = EnsureActiveWarsSnapshot();
        var byId = System.Threading.Volatile.Read(ref snap.ById);
        if (byId is null)
        {
            lock (_warSnapshotLock)
            {
                snap = EnsureActiveWarsSnapshotLocked();
                byId = snap.ById;
                if (byId is null)
                {
                    var wars = Wars;
                    var made = new Dictionary<int, StrategicWar>(wars.Count);
                    for (var index = 0; index < wars.Count; index++)
                        if (wars[index] is { } war) made.TryAdd(war.Id, war);
                    System.Threading.Volatile.Write(ref snap.ById, byId = made);
                }
            }
        }
        return byId.GetValueOrDefault(id);
    }

    private ActiveWarsSnapshot EnsureActiveWarsSnapshot()
    {
        var snap = _warSnapshot;
        if (snap is not null && !IsWarSnapshotStale(snap)) return snap;
        lock (_warSnapshotLock) return EnsureActiveWarsSnapshotLocked();
    }

    private ActiveWarsSnapshot EnsureActiveWarsSnapshotLocked()
    {
        var snap = _warSnapshot;
        if (snap is not null && !IsWarSnapshotStale(snap)) return snap;
        var wars = Wars;
        var count = wars.Count;
        var active = new List<StrategicWar>();
        for (var i = 0; i < count; i++)
        {
            var war = wars[i];
            if (war is { Ended: false }) active.Add(war);
        }
        var head = count > 0 ? wars[0] : null;
        var tail = count > 0 ? wars[count - 1] : null;
        var built = new ActiveWarsSnapshot(active, WarRevision, count, head, tail, tail?.Ended ?? false);
        _warSnapshot = built;
        return built;
    }

    private bool IsWarSnapshotStale(ActiveWarsSnapshot snap)
    {
        var wars = Wars;
        var count = wars.Count;
        var head = count > 0 ? wars[0] : null;
        var tail = count > 0 ? wars[count - 1] : null;
        if (snap.Revision != WarRevision || snap.Count != count ||
            !ReferenceEquals(snap.Head, head) || !ReferenceEquals(snap.Tail, tail) ||
            snap.TailWasEnded != (tail?.Ended ?? false))
            return true;
        var active = snap.Active;
        for (var i = 0; i < active.Count; i++)
            if (active[i] is null || active[i].Ended) return true;
        return false;
    }

    private sealed class StatesByNationSnapshot
    {
        public readonly ScenarioState[][] Dense;
        public readonly Dictionary<int, ScenarioState[]> Overflow;

        public StatesByNationSnapshot(ScenarioState[][] dense, Dictionary<int, ScenarioState[]> overflow)
        {
            Dense = dense;
            Overflow = overflow;
        }
    }

    private sealed class NationMapIndexSnapshot
    {
        public readonly int Count;
        public readonly Dictionary<int, Nation> Dict;
        public readonly Nation[] Dense;

        public NationMapIndexSnapshot(int count, Dictionary<int, Nation> dict, Nation[] dense)
        {
            Count = count;
            Dict = dict;
            Dense = dense;
        }
    }

    private sealed class StateMapIndexSnapshot
    {
        public readonly int Count;
        public readonly ScenarioState[] Dense;
        public readonly Dictionary<int, ScenarioState> Overflow;

        public StateMapIndexSnapshot(int count, ScenarioState[] dense, Dictionary<int, ScenarioState> overflow)
        {
            Count = count;
            Dense = dense;
            Overflow = overflow;
        }
    }

    /// <summary>
    /// States indexed by the nation that owns them, built once and reused.
    ///
    /// Uses a dense array indexed by <see cref="Nation.MapIndex"/> (published atomically via
    /// volatile snapshot for thread-safe parallel reads) instead of a hash dictionary + LINQ GroupBy.
    /// </summary>
    private volatile StatesByNationSnapshot _statesByNation;

    private static readonly ScenarioState[] NoStates = Array.Empty<ScenarioState>();

    public ScenarioState[] StatesOf(Nation nation) => nation is null ? NoStates : StatesOf(nation.MapIndex);

    /// <summary>
    /// Set BDOF_NO_STATE_INDEX=1 to fall back to the linear scan this index replaced.
    /// Only the bench uses it, to keep the "this optimisation is worth having" claim
    /// measurable rather than asserted.
    /// </summary>
    private static readonly bool DisableStateIndex =
        System.Environment.GetEnvironmentVariable("BDOF_NO_STATE_INDEX") == "1";

    public ScenarioState[] StatesOf(int nationMapIndex)
    {
        // The scan is a method of its own because its lambda captures the parameter, and a
        // closure over a parameter is allocated on entry - on every call, indexed or not,
        // and this is asked for every country by nearly every pass.
        if (DisableStateIndex) return StatesOfByScan(nationMapIndex);

        var snap = _statesByNation ?? BuildStatesByNation();
        if ((uint)nationMapIndex < (uint)snap.Dense.Length)
            return snap.Dense[nationMapIndex] ?? NoStates;
        return snap.Overflow is not null && snap.Overflow.TryGetValue(nationMapIndex, out var states) ? states : NoStates;
    }

    private StatesByNationSnapshot BuildStatesByNation()
    {
        var list = States;
        var count = list.Count;
        var maxDense = -1;
        Dictionary<int, int> overflowCounts = null;
        for (var i = 0; i < count; i++)
        {
            var key = list[i].NationMapIndex;
            if ((uint)key < DenseIndexLimit)
            {
                if (key > maxDense) maxDense = key;
            }
            else
            {
                overflowCounts ??= new Dictionary<int, int>();
                overflowCounts[key] = overflowCounts.GetValueOrDefault(key) + 1;
            }
        }

        var denseLength = maxDense + 1;
        var denseCounts = denseLength > 0 ? ArrayPool<int>.Shared.Rent(denseLength) : Array.Empty<int>();
        var denseOffsets = denseLength > 0 ? ArrayPool<int>.Shared.Rent(denseLength) : Array.Empty<int>();
        try
        {
            if (denseLength > 0)
            {
                Array.Clear(denseCounts, 0, denseLength);
                Array.Clear(denseOffsets, 0, denseLength);
            }
            for (var i = 0; i < count; i++)
            {
                var key = list[i].NationMapIndex;
                if ((uint)key < (uint)denseLength) denseCounts[key]++;
            }

            var dense = denseLength > 0 ? new ScenarioState[denseLength][] : Array.Empty<ScenarioState[]>();
            for (var k = 0; k < denseLength; k++)
                if (denseCounts[k] > 0) dense[k] = new ScenarioState[denseCounts[k]];

            Dictionary<int, ScenarioState[]> overflow = null;
            Dictionary<int, int> overflowOffsets = null;
            if (overflowCounts is not null)
            {
                overflow = new Dictionary<int, ScenarioState[]>(overflowCounts.Count);
                overflowOffsets = new Dictionary<int, int>(overflowCounts.Count);
                foreach (var kv in overflowCounts)
                {
                    overflow[kv.Key] = new ScenarioState[kv.Value];
                    overflowOffsets[kv.Key] = 0;
                }
            }

            for (var i = 0; i < count; i++)
            {
                var state = list[i];
                var key = state.NationMapIndex;
                if ((uint)key < (uint)denseLength)
                    dense[key][denseOffsets[key]++] = state;
                else if (overflow is not null)
                    overflow[key][overflowOffsets[key]++] = state;
            }

            var built = new StatesByNationSnapshot(dense, overflow);
            _statesByNation = built;
            return built;
        }
        finally
        {
            if (denseLength > 0)
            {
                ArrayPool<int>.Shared.Return(denseCounts);
                ArrayPool<int>.Shared.Return(denseOffsets);
            }
        }
    }

    private ScenarioState[] StatesOfByScan(int nationMapIndex)
    {
        using var matches = new HeapPooledList<ScenarioState>(16);
        for (var i = 0; i < States.Count; i++)
        {
            var s = States[i];
            if (s.NationMapIndex == nationMapIndex) matches.Add(s);
        }
        return matches.Count == 0 ? NoStates : matches.ToArray();
    }

    /// <summary>Drop the ownership and state-province indexes. Call after replacing, carving, or re-owning states.</summary>
    public void InvalidateStateIndex()
    {
        _statesByNation = null;
        ProvinceMap?.InvalidateStateProvinces();
        OwnershipRevision++;
    }

    /// <summary>
    /// Put every country the campaign founded back into the demography catalogue.
    ///
    /// The catalogue is loaded from files and knows nothing about a country that did not
    /// exist when they were read. Restoring a save has to say it again, or a country that
    /// seceded in 2031 reloads as a state that is for nobody and comes apart for having
    /// been reloaded - which is the worst class of bug there is, because it only happens to
    /// people who stopped playing and came back.
    /// </summary>
    public void DeclareFoundedPeoples()
    {
        foreach (var nation in Nations)
            if (nation.FoundedCultures.Count > 0)
                DemographyCatalog.Declare(nation.Tag, nation.FoundedCultures);
    }

    private volatile NationMapIndexSnapshot _nationMapIndex;

    private NationMapIndexSnapshot EnsureNationMapIndex()
    {
        var snap = _nationMapIndex;
        var count = Nations.Count;
        if (snap is not null && snap.Count == count) return snap;

        var dict = new Dictionary<int, Nation>(count);
        var maxDense = -1;
        for (var i = 0; i < count; i++)
        {
            var n = Nations[i];
            dict.TryAdd(n.MapIndex, n);
            if ((uint)n.MapIndex < DenseIndexLimit && n.MapIndex > maxDense) maxDense = n.MapIndex;
        }
        var dense = maxDense >= 0 ? new Nation[maxDense + 1] : Array.Empty<Nation>();
        for (var i = 0; i < count; i++)
        {
            var n = Nations[i];
            if ((uint)n.MapIndex < (uint)dense.Length && dense[n.MapIndex] is null)
                dense[n.MapIndex] = n;
        }
        var built = new NationMapIndexSnapshot(count, dict, dense);
        _nationMapIndex = built;
        return built;
    }

    public Dictionary<int, Nation> NationsByMapIndex() => EnsureNationMapIndex().Dict;

    public Nation NationById(int id) => (uint)id < (uint)Nations.Count ? Nations[id] : null;

    public Nation NationByMapIndex(int mapIndex)
    {
        if (mapIndex > 0)
        {
            var snap = EnsureNationMapIndex();
            if ((uint)mapIndex < (uint)snap.Dense.Length) return snap.Dense[mapIndex];
            return snap.Dict.GetValueOrDefault(mapIndex);
        }
        for (var i = 0; i < Nations.Count; i++)
            if (Nations[i].MapIndex == mapIndex) return Nations[i];
        return null;
    }

    private volatile StateMapIndexSnapshot _stateMapIndex;

    public ScenarioState StateByMapIndex(int mapIndex)
    {
        if (mapIndex <= 0) return null;
        var snap = _stateMapIndex;
        var count = States.Count;
        if (snap is null || snap.Count != count)
            snap = BuildStateMapIndex(count);
        if ((uint)mapIndex < (uint)snap.Dense.Length) return snap.Dense[mapIndex];
        return snap.Overflow is not null && snap.Overflow.TryGetValue(mapIndex, out var state) ? state : null;
    }

    private StateMapIndexSnapshot BuildStateMapIndex(int count)
    {
        var maxDense = -1;
        for (var i = 0; i < count; i++)
        {
            var idx = States[i].MapIndex;
            if ((uint)idx < DenseIndexLimit && idx > maxDense) maxDense = idx;
        }
        var dense = maxDense >= 0 ? new ScenarioState[maxDense + 1] : Array.Empty<ScenarioState>();
        Dictionary<int, ScenarioState> overflow = null;
        for (var i = 0; i < count; i++)
        {
            var s = States[i];
            var idx = s.MapIndex;
            if ((uint)idx < (uint)dense.Length)
                dense[idx] ??= s;
            else
            {
                overflow ??= new Dictionary<int, ScenarioState>();
                overflow.TryAdd(idx, s);
            }
        }
        var built = new StateMapIndexSnapshot(count, dense, overflow);
        _stateMapIndex = built;
        return built;
    }

    internal List<DiplomaticRelation> RelationInsertSink;

    public Nation NationByTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        for (var i = 0; i < Nations.Count; i++)
        {
            var n = Nations[i];
            if (string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Iso, tag, StringComparison.OrdinalIgnoreCase))
                return n;
        }
        return null;
    }

    public void AttachProvinceTopology(ProvinceTopology topology)
    {
        ProvinceMap = topology;
        // Who the scenario gave each region to, and each country's figure, before the map says
        // who actually holds what (OpeningOccupation). A new world only.
        var opening = _pendingProvinceController is null && !InitialDeploymentCompleted && ElapsedDays == 0
            ? new OpeningOccupation(this) : null;

        // The political map is data. The raster says where the provinces are; this says who
        // owns them, and a mod that moves one border only has to name that one province.
        // Only for the full 2024 map: synthetic test fixtures build tiny topologies (e.g. 16x8)
        // while keeping the default ScenarioId, and applying 2024's province IDs and lat/lon
        // point entries to a 1-province test grid overwrites the test's own province owner.
        ProvinceController = (ushort[])topology.ProvinceToNation.Clone();
        var politicalOverrides = ScenarioId == "2024" && topology.ProvinceCount > 1000
            ? ProvinceOwnership.Apply(this, topology, ProvinceController) : 0;
        if (politicalOverrides > 0) PoliticalOverridesApplied = politicalOverrides;
        // Water is nobody's, which the file already says by naming none of it - but the raster
        // still painted an owner under sixty-seven sea provinces off the South Orkneys, and it
        // was Iran's.
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToNation[province] == 0 || !topology.IsWater(province)) continue;
            topology.ProvinceToNation[province] = 0;
            if (province < ProvinceController.Length) ProvinceController[province] = 0;
        }
        // And where history says nobody owns the land, nobody does - whatever the raster
        // painted there. Before the region list is reconciled with the ground below, which
        // would otherwise hand the region straight back to the raster's owner.
        UnclaimedLand.ApplyToGround(this, topology, ProvinceController);
        // And where history says somebody else's troops hold a region, they do (Gaza).
        HeldGround.ApplyToGround(this, topology, ProvinceController);
        // And ground held by arrangement - Baikonur, Guantanamo - from the first look, not the first day.
        LandArrangementSystem.LayOnGround(this, topology, ProvinceController);
        ProvinceOccupation = new float[topology.ProvinceCount + 1];

        // A fleet's home port is a province, and provinces only exist once the map is
        // attached. Until then a fleet knows where it is but not what it belongs to.
        FleetHistory.AssignHomePorts(this, keepSaved: _pendingProvinceController is not null);
        // And every state's church and people from the first look: the State Affairs screen of
        // 1 January showed nothing declared until the first day had ticked, as if each
        // country made its mind up on the morning of the 2nd. A new campaign only - a save
        // carries its own declarations, and an empty one there is what was saved.
        if (_pendingProvinceController is null) StateIdentity.EnsureDeclared(this, strain: false);
        var savedTopologyMatches = _pendingProvinceController is not null &&
            _pendingProvinceOccupation is not null &&
            _pendingProvinceController.Length == ProvinceController.Length &&
            _pendingProvinceOccupation.Length == ProvinceOccupation.Length;
        if (savedTopologyMatches)
        {
            ProvinceController = _pendingProvinceController;
            ProvinceOccupation = _pendingProvinceOccupation;
        }
        _pendingProvinceController = null;
        _pendingProvinceOccupation = null;

        // Borders a peace redrew are campaign state, not map data. The raster on disk still
        // says who owned what in 2024; a save that annexed provinces and split regions has
        // to put its own political layer back over it, or loading would quietly hand every
        // conquest back and leave the region list describing a map that no longer exists.
        if (_pendingProvinceOwner is not null && _pendingProvinceOwner.Length == topology.ProvinceToNation.Length)
            Array.Copy(_pendingProvinceOwner, topology.ProvinceToNation, _pendingProvinceOwner.Length);
        if (_pendingProvinceState is not null && _pendingProvinceState.Length == topology.ProvinceToState.Length)
        {
            Array.Copy(_pendingProvinceState, topology.ProvinceToState, _pendingProvinceState.Length);
            // Headquarters placement below reads the saved regions during attachment.
            topology.InvalidateStateProvinces();
        }
        _pendingProvinceOwner = null;
        _pendingProvinceState = null;
        foreach (var province in DestroyedProvinces)
            if (province > 0 && province <= topology.ProvinceCount)
            {
                topology.DestroyedByProvince[province] = true;
                topology.ProvinceToNation[province] = 0;
                ProvinceController[province] = 0;
            }

        // Now that the map knows who owns what, make the region list agree with it. Both
        // passes are no-ops on a world that is already consistent, which is what makes them
        // safe to run on a loaded save as well as a new campaign: a save that was written
        // before regions could be split heals here instead of staying wrong for ever.
        StatePartition.RepartitionWorld(this);
        StatePartition.Reunify(this);
        opening?.KeepControlledFigures(this);

        if (savedTopologyMatches && RepairDiminishedRestorationsAfterAttach)
            FormableRestorations.RepairDiminishedIdentities(this);
        RepairDiminishedRestorationsAfterAttach = false;

        // Where each capital actually stands, and what that makes of the country. Both need
        // the map: a capital is a place on the globe rather than a region a scenario names,
        // and the regions under it can be redrawn by a war.
        LocateCapitals(preserveExisting: true);
        // A save from before capitals were cities has them placed and not yet bound to a city.
        // Here and only here: this is where a loaded world meets its map, and nothing that
        // happens later in a campaign should find a placed capital and quietly move it.
        foreach (var nation in Nations) CapitalCore.BindLoaded(this, nation);
        // Only ever from the authored world. Whether that has happened is read from the
        // world rather than remembered in a field, because a multiplayer client adopts a
        // campaign it did not create: a flag in memory says "not yet" about a world that was
        // calibrated on the host an hour ago, and recalibrating a world at war writes the
        // war into every country's institutions.
        // The de facto powers of 2024 - the Houthis, the Rapid Support Forces, Idlib - cut out of
        // the states they hold ground of, before stability is calibrated so their institutions
        // are measured from the world they open in. A new world only (Factions).
        Factions.Found(this);
        // Initial homeland must exist BEFORE calibrating imperial overstretch;
        // learning it afterwards would change a country's target on day zero.
        // Never re-baseline a restored or network-adopted world's territorial memory.
        if (!InitialDeploymentCompleted) MovementFall.RememberHomelands(this);
        // A country is the people of its regions. The scenario writes each country's figure
        // before the map says who holds what, and the map then hands occupied regions to their
        // holder: Ukraine opened at 25 million and was 20 million on the first morning, which
        // read to a player as five million people lost overnight. A new world only - a save
        // keeps the figures it was saved with.
        if (!InitialDeploymentCompleted && ElapsedDays == 0)
            foreach (var nation in Nations)
            {
                if (nation.Movement is not null) continue;
                var regions = StatesOf(nation);
                if (regions.Length > 0) nation.Population = regions.Sum(region => region.Population);
            }
        if (!StabilityCore.IsCalibrated(this)) StabilityCore.Calibrate(this);

        // The countries without land: founded on a new world once its regions exist, and put
        // back where their headquarters stand on a loaded one (Movements).
        Movements.Found(this);
        Movements.Place(this);
        // A save written before the permanent record existed still knows a live claim and, for a
        // faction, its parent in the data. Adopt it here so an old campaign gains the memory
        // instead of falling back to reading tag prefixes for the rest of its life.
        Reintegration.AdoptLegacyLineage(this);
        if (ElapsedDays == 0 && FeatureEnabled("national_courses"))
            foreach (var nation in Nations) NationalCourses.Initialize(this, nation);
        // Who leads: a person per chair, rolled once at the world's first morning.
        Rulers.Found(this);
        if (FeatureEnabled("politics") && MechanicFeatures.SystemEnabled(this, nameof(Coups)))
            Coups.PreloadCatalog();

        // The first weekly day of a session otherwise compiles every weekly pass inside one
        // player-visible frame. The load thread is already spending seconds nobody plays in;
        // the compilation moves there. Preparation only - nothing in the world is run.
        JitWarmup.PrepareWeeklyPasses();

        var attachedRevision = MapRevision;
        MapRevision++;
        // The corrections above rewrote ProvinceToNation and ProvinceToState wholesale:
        // anything the attach itself already indexed is stale before the world ticks once.
        InvalidateStateIndex();
        // A restored world arrives with its fronts drawn against the very controllers being
        // re-installed above; the bump is transport bookkeeping, not a border that moved.
        // Left alone, every line stamped at the old revision reads stale on the first tick
        // and the posts are re-sorted into a different world than the host's. Lines already
        // behind the revision were stale on the host too and must stay stale, so both sides
        // rebuild them identically.
        foreach (var line in FrontLines)
            if (line.BorderRevision == attachedRevision) line.BorderRevision = MapRevision;

        // Only a world that has not been played yet gets its orders handed out here.
        if (!InitialDeploymentCompleted)
        {
            Warfare.RebuildFronts(this);
            StrategicDeploymentPlanner.Deploy(this);
            InitialDeploymentCompleted = true;
            FrontPlanner.AssignAi(this);
            // The long-range forces the world opens with: the artillery that exists, the
            // air wings that exist, before anybody has built anything. A loaded save has
            // its own - this block runs for a new world only.
            SupportCore.EnsureInitial(this);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Find where each capital stands. A capital is a city (CapitalCore): the one the country
    /// names when that stands on its own ground, else the city of its own nearest the capital's
    /// coordinates - and its province and region are the ones under that city. Only where the
    /// map has no city of the country's there does it fall back to the nearest province of the
    /// country's own ground, which is also how the deployment planner finds it - a capital that
    /// has been overrun still points at the region it is in.
    /// </summary>
    public void LocateCapitals(bool preserveExisting = false)
    {
        var topology = ProvinceMap;
        if (topology is null) return;

        HashSet<ushort> neededOwners = null;
        if (preserveExisting)
        {
            foreach (var nation in Nations)
            {
                if (nation.CapitalProvinceId > 0 && nation.CapitalProvinceId <= topology.ProvinceCount)
                {
                    nation.CapitalStateMapIndex = topology.ProvinceToState[nation.CapitalProvinceId];
                    continue;
                }
                if (!HasOwnedLand(nation))
                {
                    nation.CapitalStateMapIndex = 0;
                    nation.CapitalProvinceId = 0;
                    continue;
                }
                neededOwners ??= new HashSet<ushort>();
                neededOwners.Add((ushort)nation.MapIndex);
            }
            if (neededOwners is null)
            {
                Movements.Place(this);
                return;
            }
        }

        var maxOwner = 0;
        var pCount = topology.ProvinceCount;
        var ptn = topology.ProvinceToNation;
        for (var province = 1; province <= pCount; province++)
        {
            var owner = ptn[province];
            if (owner != 0 && (neededOwners is null || neededOwners.Contains(owner)) && owner > maxOwner)
                maxOwner = owner;
        }
        var ownerSlots = maxOwner + 1;
        var ownerCounts = ArrayPool<int>.Shared.Rent(ownerSlots);
        var ownerCursor = ArrayPool<int>.Shared.Rent(ownerSlots + 1);
        var provincesCsr = pCount > 0 ? ArrayPool<int>.Shared.Rent(pCount) : Array.Empty<int>();
        try
        {
            Array.Clear(ownerCounts, 0, ownerSlots);
            var totalOwned = 0;
            for (var province = 1; province <= pCount; province++)
            {
                var owner = ptn[province];
                if (owner == 0) continue;
                if (neededOwners is not null && !neededOwners.Contains(owner)) continue;
                ownerCounts[owner]++;
                totalOwned++;
            }
            var sum = 0;
            for (var o = 0; o < ownerSlots; o++)
            {
                ownerCursor[o] = sum;
                sum += ownerCounts[o];
            }
            ownerCursor[ownerSlots] = sum;
            // Temporarily store write cursors in ownerCounts (reuse without a 3rd array) by computing starts back or using ownerCursor + offset.
            var writeOffset = ArrayPool<int>.Shared.Rent(ownerSlots);
            try
            {
                Array.Copy(ownerCursor, writeOffset, ownerSlots);
                for (var province = 1; province <= pCount; province++)
                {
                    var owner = ptn[province];
                    if (owner == 0) continue;
                    if (neededOwners is not null && !neededOwners.Contains(owner)) continue;
                    provincesCsr[writeOffset[owner]++] = province;
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(writeOffset);
            }

            foreach (var nation in Nations)
            {
                // A loaded capital is a real saved place, even if ceded or occupied. Rebinding
                // it to the nearest remaining province kept the old city's name forever.
                // Emergency/permanent relocation belongs to CapitalCore, not map attachment
                // (a network snapshot must remain identical until its next authoritative tick).
                if (preserveExisting && nation.CapitalProvinceId > 0 && nation.CapitalProvinceId <= pCount)
                {
                    nation.CapitalStateMapIndex = topology.ProvinceToState[nation.CapitalProvinceId];
                    continue;
                }
                if (neededOwners is not null && !neededOwners.Contains((ushort)nation.MapIndex))
                    continue;
                nation.CapitalStateMapIndex = 0;
                nation.CapitalProvinceId = 0;
                var mapIdx = nation.MapIndex;
                if ((uint)mapIdx >= (uint)ownerSlots) continue;
                var start = ownerCursor[mapIdx];
                var count = ownerCounts[mapIdx];
                if (count == 0) continue;
                var best = provincesCsr[start];
                var bestDistance = float.MaxValue;
                var longitudeScale = MathF.Max(0.12f, MathF.Cos(nation.CapitalLatitude * M.Pi / 180f));
                for (var idx = start; idx < start + count; idx++)
                {
                    var province = provincesCsr[idx];
                    var centre = topology.CenterOf(province);
                    var dx = MathF.Abs(centre.Longitude - nation.CapitalLongitude);
                    dx = MathF.Min(dx, 360f - dx) * longitudeScale;
                    var dy = centre.Latitude - nation.CapitalLatitude;
                    var distance = dx * dx + dy * dy;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = province;
                }
                // Into its city, which then decides the province, the point and the name.
                if (CapitalCore.BindAtPoint(this, nation, best)) continue;
                // No city of its own there: the nearest province, under the name it was given.
                nation.CapitalCityId = "";
                nation.CapitalStateMapIndex = topology.ProvinceToState[best];
                nation.CapitalProvinceId = best;
                var state = StateByMapIndex(nation.CapitalStateMapIndex);
                if (state is not null && !string.IsNullOrWhiteSpace(state.Name) && string.IsNullOrWhiteSpace(nation.CapitalName))
                {
                    nation.CapitalName = state.Name;
                }
                else if (state is not null && !string.IsNullOrWhiteSpace(state.Name) && CapitalCore.SeatNameLost(this, nation))
                {
                    nation.CapitalName = state.Name;
                    var centre = topology.CenterOf(best);
                    nation.CapitalLatitude = centre.Latitude;
                    nation.CapitalLongitude = centre.Longitude;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ownerCounts);
            ArrayPool<int>.Shared.Return(ownerCursor);
            if (pCount > 0) ArrayPool<int>.Shared.Return(provincesCsr);
        }
        // A landless headquarters stands at its host's capital or its region's main city
        // (Movements.Place), so it moves when they do.
        Movements.Place(this);
    }

    internal ushort[] PendingProvinceController => _pendingProvinceController;
    private ushort[] _pendingProvinceOwner;
    private ushort[] _pendingProvinceState;
    // A disk-load migration request, not campaign state. A network snapshot never sets it.
    internal bool RepairDiminishedRestorationsAfterAttach;
}
