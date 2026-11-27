using System;
using System.Collections.Generic;
using System.Threading;

namespace DealOrForce.Simulation;

/// <summary>
/// Wait-free single-producer / single-consumer triple buffer.
/// The producer (<c>DoF-Sim</c>) always writes into its private back slot and publishes it with one
/// <see cref="Interlocked.Exchange(ref int, int)"/>; the consumer (<c>GlobePresentation</c>) swaps
/// its front slot when the dirty bit is set. Neither thread ever blocks the other or allocates.
/// </summary>
public sealed class LockFreeTripleBuffer<T> where T : class
{
    private const int IndexMask = 0x3;
    private const int DirtyBit = 0x4;

    private readonly T[] _slots;
    private int _writerIndex;
    private int _readerIndex;
    private int _sharedState; // bits 0..1 = middle slot index, bit 2 = fresh unread frame available

    public LockFreeTripleBuffer(Func<T> factory)
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        _slots = new[] { factory(), factory(), factory() };
        _writerIndex = 0;
        _sharedState = 1; // clean middle slot
        _readerIndex = 2;
    }

    /// <summary>The writer's exclusive back buffer. Mutate only on the producer thread before <see cref="PublishWrite"/>.</summary>
    public T WriteBuffer => _slots[_writerIndex];

    /// <summary>The consumer's currently acquired front buffer. Safe to read repeatedly without locks.</summary>
    public T CurrentReadBuffer => _slots[_readerIndex];

    /// <summary>True when the producer has published a newer slot that the consumer has not yet swapped in.</summary>
    public bool HasUnread => (Volatile.Read(ref _sharedState) & DirtyBit) != 0;

    /// <summary>Atomically publishes the completed write buffer and obtains the next free back buffer.</summary>
    public void PublishWrite()
    {
        var previous = Interlocked.Exchange(ref _sharedState, _writerIndex | DirtyBit);
        _writerIndex = previous & IndexMask;
    }

    /// <summary>
    /// If a newer published buffer is available, atomically swaps it into the consumer slot and returns <c>true</c>.
    /// Otherwise leaves <paramref name="buffer"/> pointing at the latest already-acquired slot and returns <c>false</c>.
    /// </summary>
    public bool TryAcquireLatest(out T buffer)
    {
        if ((Volatile.Read(ref _sharedState) & DirtyBit) == 0)
        {
            buffer = _slots[_readerIndex];
            return false;
        }
        var previous = Interlocked.Exchange(ref _sharedState, _readerIndex);
        _readerIndex = previous & IndexMask;
        buffer = _slots[_readerIndex];
        return true;
    }
}

/// <summary>Compact leased region entry for lock-free province overlay packing.</summary>
public readonly record struct VisualLeaseItem(int RegionMapIndex, ushort HolderMapIndex);

/// <summary>Compact war belligerent side entry for lock-free live frontline painting.</summary>
public readonly record struct VisualWarBelligerent(int WarId, int WarOrder, byte WarCode, ushort NationMapIndex, byte Side);

/// <summary>Compact player front-line province paint entry for lock-free map overlays.</summary>
public readonly record struct VisualPlayerFrontProvince(
    int ProvinceId, byte PlanCode, ushort OwnerMapIndex, ushort ForeignMapIndex);

/// <summary>
/// Compact visible province-bucketed army stack for Frame-0 lock-free military counters.
/// </summary>
public struct VisualArmyStack
{
    public int NationId;
    public ushort NationMapIndex;
    public int LocationId;
    public int FallbackLatBucket;
    public int FallbackLonBucket;
    public int LeadArmyId;
    public int LeadProvinceId;
    public int NextProvinceId;
    public float MarchProgress;
    public float FallbackLatitude;
    public float FallbackLongitude;
    public int ArmyGroupId;
    public float Divisions;
    public float Personnel;
    public float OrganisationAvg;
    public float PlanningAvg;
    public float SupplyAvg;
    public byte OrgBin;
    public byte StrBin;
    public byte ConditionFlags;
    public byte OmniscientOrgBin;
    public byte OmniscientStrBin;
    public byte OmniscientConditionFlags;
    public byte CounterStance;
    public bool ConditionKnown;
    public bool VisibleToPlayer;
    public bool AnySelectable;
    public bool IsMoving;
    public int ArmyIdOffset;
    public int ArmyIdCount;
    public int ActualFingerprint;
    public string NationTag;
    public string TemplateId;
}

/// <summary>
/// Compact blittable battle readout captured on <c>DoF-Sim</c> so GPU-instanced battle badges
/// update every frame without touching live <see cref="Campaign"/> collections or locks.
/// </summary>
public struct VisualBattleItem
{
    public int BattleId;
    public int ProvinceId;
    public int BestOriginProvinceId;
    public int AttackerId;
    public int DefenderId;
    public float AttackerProgress;
    public float Momentum;
    public float AssessmentAttackerShare;
    public float AssessmentDefenderShare;
    public bool AssessmentKnown;
    public bool HasRounds;
    public bool HasPocketCollapse;
    public bool HasEncircledDefenders;
    public bool VisibleToPlayer;
    public string AttackerColorHex;
    public string DefenderColorHex;
}

/// <summary>
/// Reusable visual state slot stored inside <see cref="LockFreeTripleBuffer{T}"/>.
/// Keeps map borders, live war fronts, army stacks, battle badges, and the clock plate in strict frame-0 lockstep.
/// </summary>
public sealed class WorldVisualState
{
    private const byte ArmyFlagInBattle = 1 << 0;
    private const byte ArmyFlagEncircled = 1 << 1;
    private const byte ArmyFlagLowSupply = 1 << 2;
    private const byte ArmyFlagEntrenched = 1 << 3;
    private const byte ArmyFlagMoving = 1 << 4;

    private const byte StanceNeutral = 0;
    private const byte StancePlayer = 1;
    private const byte StanceAlly = 2;
    private const byte StanceEnemy = 3;

    private readonly Dictionary<int, int> _armyProvinceById = new(256);
    private readonly HashSet<int> _seenNationIds = new();
    private readonly Dictionary<(int NationId, int LocationId, int FallbackLat, int FallbackLon), int> _stackIndexByBucket = new(512);
    private readonly Dictionary<string, int> _templateVotes = new(16, StringComparer.Ordinal);
    private readonly Dictionary<int, ushort> _nationMapIndexById = new(256);
    private readonly Dictionary<int, byte> _nationStanceById = new(256);
    private readonly Dictionary<int, string> _nationTagById = new(256);
    private int[] _bucketFirstArmy = Array.Empty<int>();
    private int[] _bucketLastArmy = Array.Empty<int>();
    private int[] _armyNext = Array.Empty<int>();

    public long Version;
    public int ElapsedDays;
    public long ElapsedHours;
    public GameDate Date;
    public int MapRevision = -1;
    public int OwnershipRevision = -1;
    public int WarRevision = -1;
    public int Speed = 1;
    public bool IsPaused = true;
    public int PlayerId = -1;
    public int PlayerMapIndex = -1;
    public bool ObserverMode = true;
    public bool WorldFrozen;
    public bool HasPlayerFrontLines;
    public int DestroyedProvinceCount;
    public int FlagVisualStateHash;

    public ushort[] ProvinceController = Array.Empty<ushort>();
    public ushort[] ProvinceOwner = Array.Empty<ushort>();
    public NationSnapshot[] Nations = Array.Empty<NationSnapshot>();

    public int[] DestroyedProvinces = Array.Empty<int>();

    public VisualPlayerFrontProvince[] PlayerFrontProvinces = Array.Empty<VisualPlayerFrontProvince>();
    public int PlayerFrontProvinceCount;
    public int PlayerFrontGeometryHash;

    public VisualLeaseItem[] Leases = Array.Empty<VisualLeaseItem>();
    public int LeaseCount;

    public VisualWarBelligerent[] WarBelligerents = Array.Empty<VisualWarBelligerent>();
    public int WarBelligerentCount;

    public VisualBattleItem[] Battles = Array.Empty<VisualBattleItem>();
    public int BattleCount;

    public VisualArmyStack[] ArmyStacks = Array.Empty<VisualArmyStack>();
    public int ArmyStackCount;
    public int[] StackArmyIds = Array.Empty<int>();
    public int StackArmyIdCount;
    public int ArmyStructureVersion;

    /// <summary>
    /// Captures a complete, lock-free-consumable visual frame from <paramref name="campaign"/>
    /// synchronized to <paramref name="renderSnap"/>, reusing internal buffers to avoid steady-state GC.
    /// </summary>
    internal void CaptureFrom(Campaign campaign, CampaignRenderSnapshot renderSnap)
    {
        if (campaign is null || renderSnap is null)
        {
            Clear();
            return;
        }

        Version = renderSnap.Version;
        ElapsedDays = renderSnap.ElapsedDays;
        ElapsedHours = renderSnap.ElapsedHours;
        Date = renderSnap.Date;
        MapRevision = renderSnap.MapRevision;
        OwnershipRevision = renderSnap.OwnershipRevision;
        WarRevision = renderSnap.WarRevision;
        Speed = renderSnap.Speed;
        IsPaused = renderSnap.IsPaused;
        PlayerId = renderSnap.PlayerId;
        PlayerMapIndex = renderSnap.PlayerMapIndex;
        ObserverMode = renderSnap.ObserverMode;
        WorldFrozen = renderSnap.WorldFrozen;
        FlagVisualStateHash = FlagCatalog.VisualStateHash(campaign);
        ProvinceController = renderSnap.ProvinceController;
        ProvinceOwner = renderSnap.ProvinceOwner;
        Nations = renderSnap.Nations;

        CaptureDestroyedProvinces(campaign);
        CapturePlayerFronts(campaign);
        CaptureLeases(campaign);
        CaptureWarBelligerents(campaign);
        CaptureBattles(campaign);
        CaptureArmyStacks(campaign);
    }

    internal void Clear()
    {
        Version = 0;
        ElapsedDays = 0;
        ElapsedHours = 0;
        Date = default;
        MapRevision = -1;
        OwnershipRevision = -1;
        WarRevision = -1;
        Speed = 1;
        IsPaused = true;
        PlayerId = -1;
        PlayerMapIndex = -1;
        ObserverMode = true;
        WorldFrozen = false;
        HasPlayerFrontLines = false;
        DestroyedProvinceCount = 0;
        FlagVisualStateHash = 0;
        ProvinceController = Array.Empty<ushort>();
        ProvinceOwner = Array.Empty<ushort>();
        Nations = Array.Empty<NationSnapshot>();
        PlayerFrontProvinceCount = 0;
        PlayerFrontGeometryHash = 0;
        LeaseCount = 0;
        WarBelligerentCount = 0;
        BattleCount = 0;
        ArmyStackCount = 0;
        StackArmyIdCount = 0;
        ArmyStructureVersion = 0;
    }

    private void CaptureDestroyedProvinces(Campaign campaign)
    {
        var destroyed = campaign.DestroyedProvinces;
        var count = destroyed.Count;
        if (count == 0)
        {
            DestroyedProvinceCount = 0;
            return;
        }
        EnsureCapacity(ref DestroyedProvinces, count);
        var write = 0;
        foreach (var p in destroyed)
            DestroyedProvinces[write++] = p;
        DestroyedProvinceCount = write;
    }

    private void CapturePlayerFronts(Campaign campaign)
    {
        var lines = campaign.FrontLines;
        if (PlayerId < 0 || lines.Count == 0)
        {
            HasPlayerFrontLines = false;
            PlayerFrontProvinceCount = 0;
            PlayerFrontGeometryHash = 0;
            return;
        }

        var count = 0;
        var geoHash = new HashCode();
        var hasPlayer = false;
        var ownerIdx = (ushort)Math.Clamp(campaign.Player?.MapIndex ?? 0, 0, ushort.MaxValue);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.NationId != PlayerId) continue;
            hasPlayer = true;
            BorderFronts.Refresh(campaign, line);
            geoHash.Add(line.Id);
            geoHash.Add((int)line.Kind);
            geoHash.Add(line.ActiveOffensivePhase);
            geoHash.Add(line.BorderNationId);
            if (line.Kind == FrontLineKind.Fallback)
            {
                geoHash.Add(line.DefensiveSequence.Count);
                for (var k = 0; k < line.DefensiveSequence.Count; k++)
                    geoHash.Add(line.DefensiveSequence[k]);
                if (line.DefensiveSequence.Count == 0)
                {
                    geoHash.Add(line.Provinces.Count);
                    foreach (var p in line.Provinces) geoHash.Add(p);
                }
                continue;
            }
            if (line.Kind == FrontLineKind.Offensive && line.ObjectiveSequence.Count > 0)
            {
                var offForeignIdx = ResolveNationMapIndex(campaign, line.BorderNationId);
                foreach (var p in line.SourceProvinces)
                {
                    if (p <= 0 || p >= 256 * 256) continue;
                    EnsureCapacity(ref PlayerFrontProvinces, count + 1);
                    PlayerFrontProvinces[count++] = new VisualPlayerFrontProvince(p, 64, ownerIdx, offForeignIdx);
                }
                geoHash.Add(line.SourceProvinces.Count);
                geoHash.Add(line.ObjectiveSequence.Count);
                for (var k = 0; k < line.ObjectiveSequence.Count; k++)
                    geoHash.Add(line.ObjectiveSequence[k]);
                continue;
            }
            var code = (byte)(line.Kind switch
            {
                FrontLineKind.Defence => 64,
                FrontLineKind.Offensive => 128,
                FrontLineKind.AreaDefence => 224,
                _ => 192
            });
            var fixedForeign = line.Kind == FrontLineKind.Defence && !line.AllEnemies
                ? ResolveNationMapIndex(campaign, line.BorderNationId)
                : (ushort)0;
            foreach (var p in line.Provinces)
            {
                if (p <= 0 || p >= 256 * 256) continue;
                var foreignIdx = line.Kind == FrontLineKind.Defence
                    ? (line.AllEnemies
                        ? ResolveNationMapIndex(campaign, BorderFronts.EnemyAt(campaign, line.NationId, p))
                        : fixedForeign)
                    : (ushort)0;
                EnsureCapacity(ref PlayerFrontProvinces, count + 1);
                PlayerFrontProvinces[count++] = new VisualPlayerFrontProvince(p, code, ownerIdx, foreignIdx);
            }
        }
        HasPlayerFrontLines = hasPlayer;
        PlayerFrontProvinceCount = count;
        PlayerFrontGeometryHash = hasPlayer ? geoHash.ToHashCode() : 0;
    }

    private static ushort ResolveNationMapIndex(Campaign campaign, int nationId)
    {
        if (nationId < 0) return 0;
        var mapIdx = campaign.NationById(nationId)?.MapIndex ?? 0;
        return mapIdx > 0 && mapIdx <= ushort.MaxValue ? (ushort)mapIdx : (ushort)0;
    }

    private void CaptureLeases(Campaign campaign)
    {
        var count = 0;
        var arrangements = campaign.LandArrangements;
        for (var i = 0; i < arrangements.Count; i++)
        {
            var arrangement = arrangements[i];
            if (arrangement.Kind != LandArrangementSystem.Lease) continue;
            var tenant = campaign.NationById(arrangement.HolderId);
            if (tenant is null || tenant.MapIndex <= 0 || tenant.MapIndex > ushort.MaxValue) continue;
            var holderIdx = (ushort)tenant.MapIndex;
            var regions = arrangement.Regions;
            if (regions is null) continue;
            for (var r = 0; r < regions.Count; r++)
            {
                EnsureCapacity(ref Leases, count + 1);
                Leases[count++] = new VisualLeaseItem(regions[r], holderIdx);
            }
        }
        LeaseCount = count;
    }

    private void CaptureWarBelligerents(Campaign campaign)
    {
        var count = 0;
        var wars = Warfare.LiveWars(campaign);
        if (wars.Count == 0)
        {
            WarBelligerentCount = 0;
            return;
        }

        var seen = _seenNationIds;
        for (var w = 0; w < wars.Count; w++)
        {
            var war = wars[w];
            if (war.Ended) continue;
            var warCode = (byte)(1 + (war.Id % 254));
            seen.Clear();
            AppendBelligerent(campaign, war.Id, w, warCode, war.AttackerId, 1, seen, ref count);
            foreach (var ally in war.AttackerAllies)
                AppendBelligerent(campaign, war.Id, w, warCode, ally, 1, seen, ref count);
            AppendBelligerent(campaign, war.Id, w, warCode, war.DefenderId, 2, seen, ref count);
            foreach (var ally in war.DefenderAllies)
                AppendBelligerent(campaign, war.Id, w, warCode, ally, 2, seen, ref count);
        }
        WarBelligerentCount = count;
    }

    private void AppendBelligerent(
        Campaign campaign, int warId, int warOrder, byte warCode, int nationId, byte side,
        HashSet<int> seen, ref int count)
    {
        if (!seen.Add(nationId)) return;
        var nation = campaign.NationById(nationId);
        if (nation is null || nation.MapIndex <= 0 || nation.MapIndex > ushort.MaxValue) return;
        EnsureCapacity(ref WarBelligerents, count + 1);
        WarBelligerents[count++] = new VisualWarBelligerent(warId, warOrder, warCode, (ushort)nation.MapIndex, side);
    }

    private void CaptureBattles(Campaign campaign)
    {
        var battles = campaign.Battles;
        if (battles.Count == 0)
        {
            BattleCount = 0;
            return;
        }

        var activeCount = 0;
        for (var i = 0; i < battles.Count; i++)
            if (!battles[i].Ended) activeCount++;
        if (activeCount == 0)
        {
            BattleCount = 0;
            return;
        }

        var armyProvince = _armyProvinceById;
        armyProvince.Clear();
        var armies = campaign.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var a = armies[i];
            armyProvince[a.Id] = a.ProvinceId;
        }

        var map = campaign.ProvinceMap;
        var neighbors = map?.Neighbors;
        var breakOrg = campaign.Defines.BattleBreakOrganisation;
        EnsureCapacity(ref Battles, activeCount);

        var write = 0;
        for (var i = 0; i < battles.Count; i++)
        {
            var battle = battles[i];
            if (battle.Ended) continue;

            var bestArmyId = int.MaxValue;
            var bestOrigin = 0;
            int[] provNeighbors = neighbors is not null &&
                                  battle.ProvinceId > 0 && battle.ProvinceId < neighbors.Length
                ? neighbors[battle.ProvinceId]
                : null;
            for (var a = 0; a < battle.Attackers.Count; a++)
            {
                var armyId = battle.Attackers[a];
                if (armyId >= bestArmyId) continue;
                if (!armyProvince.TryGetValue(armyId, out var provId) || provId <= 0 || provId == battle.ProvinceId)
                    continue;
                if (provNeighbors is not null && Array.IndexOf(provNeighbors, provId) < 0)
                    continue;
                bestArmyId = armyId;
                bestOrigin = provId;
            }

            var assessment = BattleReadout.Assess(battle, breakOrg);
            var telemetry = BattleTelemetry.Build(campaign, battle);
            var attNation = campaign.NationById(battle.AttackerId);
            var defNation = campaign.NationById(battle.DefenderId);

            Battles[write++] = new VisualBattleItem
            {
                BattleId = battle.Id,
                ProvinceId = battle.ProvinceId,
                BestOriginProvinceId = bestOrigin,
                AttackerId = battle.AttackerId,
                DefenderId = battle.DefenderId,
                AttackerProgress = telemetry.AttackerProgress,
                Momentum = telemetry.Momentum,
                AssessmentAttackerShare = assessment.ShareFor(true),
                AssessmentDefenderShare = assessment.ShareFor(false),
                AssessmentKnown = assessment.Known,
                HasRounds = battle.Rounds.Count > 0,
                HasPocketCollapse = telemetry.HasPocketCollapse,
                HasEncircledDefenders = telemetry.HasEncircledDefenders,
                VisibleToPlayer = CampaignIntel.PlayerCanSeeBattle(campaign, battle),
                AttackerColorHex = attNation?.Color ?? "",
                DefenderColorHex = defNation?.Color ?? ""
            };
        }
        BattleCount = write;
    }

    private void CaptureArmyStacks(Campaign campaign)
    {
        var armies = campaign.Armies;
        if (armies.Count == 0)
        {
            ArmyStackCount = 0;
            StackArmyIdCount = 0;
            ArmyStructureVersion = FlagVisualStateHash;
            return;
        }

        var viewer = campaign.Player;
        var viewerId = viewer is { IsHuman: true } ? viewer.Id : -1;
        var nations = campaign.Nations;
        _nationMapIndexById.Clear();
        _nationStanceById.Clear();
        _nationTagById.Clear();
        for (var i = 0; i < nations.Count; i++)
        {
            var n = nations[i];
            _nationMapIndexById[n.Id] = (ushort)Math.Clamp(n.MapIndex, 0, ushort.MaxValue);
            _nationTagById[n.Id] = n.Tag ?? "";
            byte stance = StanceNeutral;
            if (viewerId >= 0)
            {
                if (n.Id == viewerId) stance = StancePlayer;
                else if (CampaignDiplomacy.AtWar(campaign, viewerId, n.Id)) stance = StanceEnemy;
                else if (n.AnswersTo(viewerId) || viewer!.AnswersTo(n.Id) ||
                         CampaignDiplomacy.Peek(campaign, viewerId, n.Id) is { Stance: DiplomaticStance.Allied })
                    stance = StanceAlly;
            }
            _nationStanceById[n.Id] = stance;
        }

        EnsureCapacity(ref _armyNext, armies.Count);
        _stackIndexByBucket.Clear();
        var stackCount = 0;
        for (var i = 0; i < armies.Count; i++)
        {
            var army = armies[i];
            if (army.EmbarkedFleetId >= 0) continue;
            var locId = army.ProvinceId > 0 ? army.ProvinceId : 0;
            var fLat = locId > 0 ? 0 : (int)Math.Round(army.Latitude * 20f, MidpointRounding.ToEven);
            var fLon = locId > 0 ? 0 : (int)Math.Round(army.Longitude * 20f, MidpointRounding.ToEven);
            var key = (army.NationId, locId, fLat, fLon);
            _armyNext[i] = -1;
            if (!_stackIndexByBucket.TryGetValue(key, out var idx))
            {
                idx = stackCount++;
                _stackIndexByBucket[key] = idx;
                EnsureCapacity(ref _bucketFirstArmy, stackCount);
                EnsureCapacity(ref _bucketLastArmy, stackCount);
                _bucketFirstArmy[idx] = i;
                _bucketLastArmy[idx] = i;
            }
            else
            {
                _armyNext[_bucketLastArmy[idx]] = i;
                _bucketLastArmy[idx] = i;
            }
        }

        if (stackCount == 0)
        {
            ArmyStackCount = 0;
            StackArmyIdCount = 0;
            ArmyStructureVersion = FlagVisualStateHash;
            return;
        }

        EnsureCapacity(ref ArmyStacks, stackCount);
        var map = campaign.ProvinceMap;
        var provCount = map?.ProvinceCount ?? 0;
        var defines = campaign.Defines;
        var omniscientPlayer = viewer is null || !viewer.IsHuman;
        var idWrite = 0;
        var structHash = new HashCode();
        structHash.Add(FlagVisualStateHash);
        structHash.Add(stackCount);

        var outCount = 0;
        foreach (var kv in _stackIndexByBucket)
        {
            var bKey = kv.Key;
            var firstIdx = _bucketFirstArmy[kv.Value];
            var lead = armies[firstIdx];
            if (!_nationMapIndexById.TryGetValue(lead.NationId, out var nationMapIndex)) continue;
            _nationTagById.TryGetValue(lead.NationId, out var nationTag);
            _nationStanceById.TryGetValue(lead.NationId, out var stance);

            _templateVotes.Clear();
            var idOffset = idWrite;
            var stackMembers = 0;
            var visibleToPlayer = false;
            var anySelectable = false;
            var isMoving = false;
            var candidateNext = lead.Route.Count > 0 ? lead.Route[0] : -1;
            if (candidateNext <= 0 || candidateNext > provCount) candidateNext = -1;
            var marchMatching = 0;
            var marchProgressSum = 0f;

            var totalDivs = 0f;
            var totalPers = 0f;
            var orgPlainSum = 0f;
            var planPlainSum = 0f;
            var supplyPlainSum = 0f;

            var fogDivs = 0d;
            var fogOrgSum = 0d;
            var fogEquipSum = 0d;
            var fogStrSum = 0f;
            var fogEntrenchSum = 0f;
            var fogWeightSum = 0f;
            byte fogFlags = 0;
            var fogConditionCount = 0;

            var omniDivs = 0d;
            var omniOrgSum = 0d;
            var omniEquipSum = 0d;
            var omniStrSum = 0f;
            var omniEntrenchSum = 0f;
            var omniWeightSum = 0f;
            byte omniFlags = 0;
            var omniConditionCount = 0;

            var fallbackLatSum = 0f;
            var fallbackSin = 0f;
            var fallbackCos = 0f;
            var fallbackGeoValid = 0;
            var actualFp = new HashCode();

            for (var idx = firstIdx; idx >= 0; idx = _armyNext[idx])
            {
                var army = armies[idx];
                stackMembers++;
                EnsureCapacity(ref StackArmyIds, idWrite + 1);
                StackArmyIds[idWrite++] = army.Id;
                actualFp.Add(army.Id);

                var tid = army.TemplateId ?? "";
                _templateVotes[tid] = _templateVotes.GetValueOrDefault(tid) + 1;

                if (CampaignIntel.PlayerCanSeeArmy(campaign, army)) visibleToPlayer = true;
                if (ArmyInteraction.CanSelect(campaign, army)) anySelectable = true;
                if (army.IsMoving && army.Route.Count > 0) isMoving = true;
                if (candidateNext > 0 && army.Route.Count > 0 && army.Route[0] == candidateNext && army.MoveProgress > 0.001f)
                {
                    marchMatching++;
                    marchProgressSum += army.MoveProgress;
                }

                totalDivs += army.Divisions;
                totalPers += army.Divisions > 1.01f
                    ? MathF.Round(army.Divisions * MilitaryBootstrap.DivisionSize * army.Strength)
                    : Math.Max(0, army.Personnel);
                orgPlainSum += army.Organisation;
                planPlainSum += army.Planning;
                supplyPlainSum += army.SupplyLevel;

                if (bKey.LocationId == 0 &&
                    float.IsFinite(army.Latitude) && float.IsFinite(army.Longitude) &&
                    Math.Abs(army.Latitude) <= 90f && Math.Abs(army.Longitude) <= 180f &&
                    (army.Latitude != 0f || army.Longitude != 0f))
                {
                    fallbackLatSum += army.Latitude;
                    var rad = army.Longitude * (MathF.PI / 180f);
                    fallbackSin += MathF.Sin(rad);
                    fallbackCos += MathF.Cos(rad);
                    fallbackGeoValid++;
                }

                var w = MathF.Max(0.1f, army.Divisions);
                var unitOrg = BattleReadout.Unit(army.Organisation);
                var unitEquip = BattleReadout.Unit(army.Equipment);
                var unitStr = BattleReadout.Unit(army.Strength);
                var entr = Math.Clamp(army.Entrenchment, 0f, 1f);

                if (MilitaryConditionReadout.CanRead(campaign, viewerId, army, omniscient: true))
                {
                    omniDivs += army.Divisions;
                    omniOrgSum += (double)unitOrg * army.Divisions;
                    omniEquipSum += (double)unitEquip * army.Divisions;
                    omniWeightSum += w;
                    omniStrSum += unitStr * w;
                    omniEntrenchSum += entr * w;
                    if (army.InBattle) omniFlags |= ArmyFlagInBattle;
                    if (army.CutOff || BattleTelemetry.IsInPocketCollapse(defines, army)) omniFlags |= ArmyFlagEncircled;
                    else if (army.SupplyLevel < 0.72f) omniFlags |= ArmyFlagLowSupply;
                    if (army.IsMoving || army.Route.Count > 0) omniFlags |= ArmyFlagMoving;
                    omniConditionCount++;
                }
                if (MilitaryConditionReadout.CanRead(campaign, viewerId, army, omniscient: omniscientPlayer))
                {
                    fogDivs += army.Divisions;
                    fogOrgSum += (double)unitOrg * army.Divisions;
                    fogEquipSum += (double)unitEquip * army.Divisions;
                    fogWeightSum += w;
                    fogStrSum += unitStr * w;
                    fogEntrenchSum += entr * w;
                    if (army.InBattle) fogFlags |= ArmyFlagInBattle;
                    if (army.CutOff || BattleTelemetry.IsInPocketCollapse(defines, army)) fogFlags |= ArmyFlagEncircled;
                    else if (army.SupplyLevel < 0.72f) fogFlags |= ArmyFlagLowSupply;
                    if (army.IsMoving || army.Route.Count > 0) fogFlags |= ArmyFlagMoving;
                    fogConditionCount++;
                }
            }

            var bestTemplate = lead.TemplateId ?? "";
            var bestVotes = -1;
            foreach (var vote in _templateVotes)
            {
                if (vote.Value > bestVotes ||
                    (vote.Value == bestVotes && string.CompareOrdinal(vote.Key, bestTemplate) < 0))
                {
                    bestVotes = vote.Value;
                    bestTemplate = vote.Key;
                }
            }

            if ((omniFlags & ArmyFlagEncircled) != 0) omniFlags &= unchecked((byte)~ArmyFlagLowSupply);
            if (omniWeightSum > 0f && omniEntrenchSum / omniWeightSum >= 0.15f) omniFlags |= ArmyFlagEntrenched;
            byte omniOrgBin = 0, omniStrBin = 0;
            if (omniConditionCount > 0 && omniDivs > 0d)
            {
                var equipAvg = (float)(omniEquipSum / omniDivs);
                var avgStr = omniWeightSum > 0f ? MathF.Min(omniStrSum / omniWeightSum, equipAvg) : equipAvg;
                omniOrgBin = (byte)Math.Clamp((int)Math.Round((omniOrgSum / omniDivs) * 10.0), 0, 10);
                omniStrBin = (byte)Math.Clamp((int)Math.Round(avgStr * 10f), 0, 10);
            }

            if ((fogFlags & ArmyFlagEncircled) != 0) fogFlags &= unchecked((byte)~ArmyFlagLowSupply);
            if (fogWeightSum > 0f && fogEntrenchSum / fogWeightSum >= 0.15f) fogFlags |= ArmyFlagEntrenched;
            byte fogOrgBin = 0, fogStrBin = 0;
            var fogKnown = fogConditionCount > 0 && fogDivs > 0d;
            if (fogKnown)
            {
                var equipAvg = (float)(fogEquipSum / fogDivs);
                var avgStr = fogWeightSum > 0f ? MathF.Min(fogStrSum / fogWeightSum, equipAvg) : equipAvg;
                fogOrgBin = (byte)Math.Clamp((int)Math.Round((fogOrgSum / fogDivs) * 10.0), 0, 10);
                fogStrBin = (byte)Math.Clamp((int)Math.Round(avgStr * 10f), 0, 10);
            }

            var nextProvOut = 0;
            var marchProgressOut = 0f;
            if (bKey.LocationId > 0 && candidateNext > 0 && marchMatching > 0 && marchMatching * 2 >= stackMembers)
            {
                nextProvOut = candidateNext;
                marchProgressOut = Math.Clamp(marchProgressSum / marchMatching, 0f, 1f);
            }

            var fbLat = fallbackGeoValid > 0 ? fallbackLatSum / fallbackGeoValid : 0f;
            var fbLon = fallbackGeoValid > 0 ? MathF.Atan2(fallbackSin, fallbackCos) * (180f / MathF.PI) : 0f;
            var actualFingerprint = actualFp.ToHashCode();

            structHash.Add(bKey.NationId);
            structHash.Add(bKey.LocationId);
            structHash.Add(bKey.FallbackLat);
            structHash.Add(bKey.FallbackLon);
            structHash.Add(bestTemplate);
            structHash.Add(actualFingerprint);

            ArmyStacks[outCount++] = new VisualArmyStack
            {
                NationId = bKey.NationId,
                NationMapIndex = nationMapIndex,
                LocationId = bKey.LocationId,
                FallbackLatBucket = bKey.FallbackLat,
                FallbackLonBucket = bKey.FallbackLon,
                LeadArmyId = lead.Id,
                LeadProvinceId = lead.ProvinceId,
                NextProvinceId = nextProvOut,
                MarchProgress = marchProgressOut,
                FallbackLatitude = fbLat,
                FallbackLongitude = fbLon,
                ArmyGroupId = lead.ArmyGroupId,
                Divisions = totalDivs,
                Personnel = totalPers,
                OrganisationAvg = orgPlainSum / stackMembers,
                PlanningAvg = planPlainSum / stackMembers,
                SupplyAvg = supplyPlainSum / stackMembers,
                OrgBin = fogOrgBin,
                StrBin = fogStrBin,
                ConditionFlags = fogFlags,
                OmniscientOrgBin = omniOrgBin,
                OmniscientStrBin = omniStrBin,
                OmniscientConditionFlags = omniFlags,
                CounterStance = stance,
                ConditionKnown = fogKnown,
                VisibleToPlayer = visibleToPlayer,
                AnySelectable = anySelectable,
                IsMoving = isMoving,
                ArmyIdOffset = idOffset,
                ArmyIdCount = stackMembers,
                ActualFingerprint = actualFingerprint,
                NationTag = nationTag ?? "",
                TemplateId = bestTemplate
            };
        }

        ArmyStackCount = outCount;
        StackArmyIdCount = idWrite;
        ArmyStructureVersion = structHash.ToHashCode();
    }

    private static void EnsureCapacity<TItem>(ref TItem[] array, int required)
    {
        if (array.Length >= required) return;
        var next = Math.Max(16, array.Length == 0 ? 16 : array.Length * 2);
        while (next < required) next *= 2;
        Array.Resize(ref array, next);
    }
}
