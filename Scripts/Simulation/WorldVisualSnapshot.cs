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
/// Keeps map borders, live war fronts, battle badges, and the clock plate in strict frame-0 lockstep.
/// </summary>
public sealed class WorldVisualState
{
    private readonly Dictionary<int, int> _armyProvinceById = new(256);
    private readonly HashSet<int> _seenNationIds = new();

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

    public ushort[] ProvinceController = Array.Empty<ushort>();
    public ushort[] ProvinceOwner = Array.Empty<ushort>();
    public NationSnapshot[] Nations = Array.Empty<NationSnapshot>();

    public VisualLeaseItem[] Leases = Array.Empty<VisualLeaseItem>();
    public int LeaseCount;

    public VisualWarBelligerent[] WarBelligerents = Array.Empty<VisualWarBelligerent>();
    public int WarBelligerentCount;

    public VisualBattleItem[] Battles = Array.Empty<VisualBattleItem>();
    public int BattleCount;

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
        DestroyedProvinceCount = renderSnap.DestroyedProvinceCount;
        HasPlayerFrontLines = false;
        if (PlayerId >= 0)
        {
            var lines = campaign.FrontLines;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].NationId == PlayerId) { HasPlayerFrontLines = true; break; }
            }
        }
        ProvinceController = renderSnap.ProvinceController;
        ProvinceOwner = renderSnap.ProvinceOwner;
        Nations = renderSnap.Nations;

        CaptureLeases(campaign);
        CaptureWarBelligerents(campaign);
        CaptureBattles(campaign);
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
        ProvinceController = Array.Empty<ushort>();
        ProvinceOwner = Array.Empty<ushort>();
        Nations = Array.Empty<NationSnapshot>();
        LeaseCount = 0;
        WarBelligerentCount = 0;
        BattleCount = 0;
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

    private static void EnsureCapacity<TItem>(ref TItem[] array, int required)
    {
        if (array.Length >= required) return;
        var next = Math.Max(16, array.Length == 0 ? 16 : array.Length * 2);
        while (next < required) next *= 2;
        Array.Resize(ref array, next);
    }
}
