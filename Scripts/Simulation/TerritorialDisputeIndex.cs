using System;
using System.Collections.Generic;
using System.Threading;

namespace DealOrForce.Simulation;

internal sealed partial class CampaignCaches
{
    private TerritorialDisputeIndex _territorialDisputes;
    public TerritorialDisputeIndex TerritorialDisputes => LazyInitializer.EnsureInitialized(ref _territorialDisputes)!;
}

/// <summary>
/// Read-only per-tick lookup of every ordered <c>(claimantId, holderId)</c> pair that can
/// possibly hold a core, claim, or homeland dispute on any state.
///
/// Built once per <see cref="AiStatecraft"/> pass in O(S) (~0.08 ms over 1,847 states) and
/// consulted conservatively inside <see cref="OpinionSystem.StandingClaimAndCycle"/>:
/// a miss guarantees zero foreign cores/claims/homelands in O(1), while a hit (~0.2% of pairs)
/// executes the exact existing <see cref="LostLands"/> and <see cref="TerritorialClaims"/> logic.
/// Outside an active statecraft pass (including direct unit-test mutations), <see cref="IsActiveFor"/>
/// returns <c>false</c> so callers always evaluate live state directly.
/// </summary>
internal sealed class TerritorialDisputeIndex
{
    private readonly HashSet<long> _pairs = new(128);
    private readonly Dictionary<string, List<int>> _nationsByTag = new(256, StringComparer.OrdinalIgnoreCase);
    private readonly List<List<int>> _tagListsPool = new(256);
    private readonly Dictionary<int, List<int>> _originHolders = new(256);
    private readonly List<List<int>> _originListsPool = new(256);
    private readonly Dictionary<int, int> _stateHolderByMap = new(2048);

    private bool _active;
    private int _elapsedDays = int.MinValue;
    private int _ownershipRevision = -1;
    private int _regionLayoutRevision = -1;
    private int _strategicRevision = -1;
    private int _nationsCount = -1;
    private int _statesCount = -1;
    private int _homelandsCount = -1;

    private static long PairKey(int claimantId, int holderId) =>
        ((long)claimantId << 32) | (uint)holderId;

    public void WarmForTick(Campaign campaign)
    {
        if (campaign is null) { _active = false; return; }
        if (_elapsedDays == campaign.ElapsedDays &&
            _ownershipRevision == campaign.OwnershipRevision &&
            _regionLayoutRevision == campaign.RegionLayoutRevision &&
            _strategicRevision == campaign.StrategicRevision &&
            _nationsCount == campaign.Nations.Count &&
            _statesCount == campaign.States.Count &&
            _homelandsCount == campaign.Homelands.Count)
        {
            _active = true;
            return;
        }
        Rebuild(campaign);
        _active = true;
    }

    public void EndTick() => _active = false;

    public void Invalidate()
    {
        _active = false;
        _ownershipRevision = -1;
    }

    public bool IsActiveFor(Campaign campaign) =>
        _active &&
        campaign is not null &&
        _elapsedDays == campaign.ElapsedDays &&
        _ownershipRevision == campaign.OwnershipRevision &&
        _regionLayoutRevision == campaign.RegionLayoutRevision &&
        _strategicRevision == campaign.StrategicRevision &&
        _nationsCount == campaign.Nations.Count &&
        _statesCount == campaign.States.Count &&
        _homelandsCount == campaign.Homelands.Count;

    public bool MayHaveDispute(int claimantId, int holderId) =>
        _pairs.Contains(PairKey(claimantId, holderId));

    private void Rebuild(Campaign campaign)
    {
        _pairs.Clear();
        foreach (var list in _nationsByTag.Values) list.Clear();
        _nationsByTag.Clear();
        foreach (var list in _originHolders.Values) list.Clear();
        _originHolders.Clear();
        _stateHolderByMap.Clear();

        var tagPoolIdx = 0;
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var nation = nations[i];
            if (nation is null || string.IsNullOrEmpty(nation.Tag)) continue;
            if (!_nationsByTag.TryGetValue(nation.Tag, out var ids))
            {
                ids = tagPoolIdx < _tagListsPool.Count ? _tagListsPool[tagPoolIdx] : CreatePooled(_tagListsPool);
                tagPoolIdx++;
                ids.Clear();
                _nationsByTag[nation.Tag] = ids;
            }
            ids.Add(nation.Id);
        }

        var originPoolIdx = 0;
        var states = campaign.States;
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state is null || state.Unclaimed) continue;
            var holder = campaign.NationByMapIndex(state.NationMapIndex);
            if (holder is null) continue;
            var holderId = holder.Id;
            _stateHolderByMap[state.MapIndex] = holderId;

            var origin = TerritorialClaims.Origin(state);
            if (origin != state.MapIndex)
            {
                if (!_originHolders.TryGetValue(origin, out var holders))
                {
                    holders = originPoolIdx < _originListsPool.Count ? _originListsPool[originPoolIdx] : CreatePooled(_originListsPool);
                    originPoolIdx++;
                    holders.Clear();
                    _originHolders[origin] = holders;
                }
                holders.Add(holderId);
            }

            var cores = state.CoreTags;
            for (var ci = 0; ci < cores.Count; ci++)
            {
                var tag = cores[ci];
                if (string.IsNullOrEmpty(tag) || !_nationsByTag.TryGetValue(tag, out var claimants)) continue;
                for (var ni = 0; ni < claimants.Count; ni++)
                {
                    var cid = claimants[ni];
                    if (cid != holderId) _pairs.Add(PairKey(cid, holderId));
                }
            }

            var claims = state.Claims;
            for (var ci = 0; ci < claims.Count; ci++)
            {
                var tag = claims[ci];
                if (string.IsNullOrEmpty(tag) || !_nationsByTag.TryGetValue(tag, out var claimants)) continue;
                for (var ni = 0; ni < claimants.Count; ni++)
                {
                    var cid = claimants[ni];
                    if (cid != holderId) _pairs.Add(PairKey(cid, holderId));
                }
            }
        }

        foreach (var (claimantId, homeRegions) in campaign.Homelands)
        {
            if (homeRegions is null) continue;
            for (var hi = 0; hi < homeRegions.Count; hi++)
            {
                var regionIdx = homeRegions[hi];
                if (_stateHolderByMap.TryGetValue(regionIdx, out var directHolder) && directHolder != claimantId)
                    _pairs.Add(PairKey(claimantId, directHolder));
                if (_originHolders.TryGetValue(regionIdx, out var derivedHolders))
                {
                    for (var di = 0; di < derivedHolders.Count; di++)
                    {
                        var dh = derivedHolders[di];
                        if (dh != claimantId) _pairs.Add(PairKey(claimantId, dh));
                    }
                }
            }
        }

        _elapsedDays = campaign.ElapsedDays;
        _ownershipRevision = campaign.OwnershipRevision;
        _regionLayoutRevision = campaign.RegionLayoutRevision;
        _strategicRevision = campaign.StrategicRevision;
        _nationsCount = nations.Count;
        _statesCount = states.Count;
        _homelandsCount = campaign.Homelands.Count;
    }

    private static List<int> CreatePooled(List<List<int>> pool)
    {
        var list = new List<int>(2);
        pool.Add(list);
        return list;
    }
}
