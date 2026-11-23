using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Campaign-owned pooled scratch structures for hourly <see cref="LongRangeMovement.Withdraw"/>
/// and daily <see cref="LongRangeMovement.RepositionStages"/> passes.
///
/// Previously <c>Withdraw</c> allocated a fresh <c>ulong[]</c> battle bitset, a fresh
/// <c>Dictionary&lt;int, Ground&gt;</c>, and per-nation <c>Ground</c> instances with their own
/// <c>HashSet&lt;ushort&gt;</c> and bitset arrays on every single hourly step (8,760 times/year,
/// ~157 MB of garbage). Pooling these per campaign eliminates the hourly allocation storm while
/// preserving strict campaign isolation.
/// </summary>
internal sealed class SupportStepCache
{
    public ulong[] BattleWords = Array.Empty<ulong>();
    public readonly Dictionary<int, LongRangeMovement.Ground> WithdrawGrounds = new();
    public readonly Stack<LongRangeMovement.Ground> GroundPool = new();

    // Fast-path stamp for hourly SupportCore.PrepareStep when neither borders, wars,
    // strategic commands, formations, nor diplomatic access changed since the prior hour.
    public bool PreparedClean;
    public int PreparedDay = int.MinValue;
    public int PreparedMapRevision = int.MinValue;
    public int PreparedWarRevision = int.MinValue;
    public int PreparedStrategicRevision = int.MinValue;
    public int PreparedAllianceRevision = int.MinValue;
    public int PreparedProtectorVersion = int.MinValue;
    public int PreparedSupportCount = -1;
    public int PreparedBattleCount = -1;
    public ProvinceTopology PreparedMap;

    // Reusable per-step AtWar lookup built lazily only when GroundOf runs during Withdraw/Advance.
    public long AtWarBuiltHour = long.MinValue;
    public int AtWarBuiltRevision = int.MinValue;
    public int AtWarBuiltAllianceRevision = int.MinValue;
    public int AtWarBuiltRelationCount = -1;
    public readonly Dictionary<int, List<ushort>> AtWarMapIndicesByNation = new();
    public readonly Stack<List<ushort>> UshortListPool = new();

    // Reusable containers for LongRangeMovement.RepositionStages.
    public readonly Dictionary<int, List<SupportFormation>> RepositionByNation = new();
    public readonly Stack<List<SupportFormation>> FormationListPool = new();
    public readonly Dictionary<int, SupportFormation> RepositionById = new();
    public readonly Dictionary<int, List<int>> RepositionBattlesByWar = new();
    public readonly Stack<List<int>> IntListPool = new();
    public readonly Dictionary<ushort, List<int>> RepositionLandByController = new();
    public readonly List<Nation> RepositionNations = new();

    public LongRangeMovement.Ground RentGround()
    {
        if (GroundPool.Count > 0)
        {
            var g = GroundPool.Pop();
            g.ResetForReuse();
            return g;
        }
        return new LongRangeMovement.Ground();
    }

    public void ReturnGrounds(Dictionary<int, LongRangeMovement.Ground> dict)
    {
        foreach (var g in dict.Values)
        {
            g.ResetForReuse();
            if (GroundPool.Count < 64) GroundPool.Push(g);
        }
        dict.Clear();
    }

    public List<SupportFormation> RentFormationList()
    {
        if (FormationListPool.Count > 0)
        {
            var list = FormationListPool.Pop();
            list.Clear();
            return list;
        }
        return new List<SupportFormation>(8);
    }

    public void ReturnFormationLists(Dictionary<int, List<SupportFormation>> dict)
    {
        foreach (var list in dict.Values)
        {
            list.Clear();
            if (FormationListPool.Count < 256) FormationListPool.Push(list);
        }
        dict.Clear();
    }

    public List<int> RentIntList()
    {
        if (IntListPool.Count > 0)
        {
            var list = IntListPool.Pop();
            list.Clear();
            return list;
        }
        return new List<int>(16);
    }

    public void ReturnIntLists<TKey>(Dictionary<TKey, List<int>> dict)
    {
        foreach (var list in dict.Values)
        {
            list.Clear();
            if (IntListPool.Count < 512) IntListPool.Push(list);
        }
        dict.Clear();
    }
}
