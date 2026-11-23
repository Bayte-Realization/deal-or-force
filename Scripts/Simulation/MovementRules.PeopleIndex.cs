using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation;

public static partial class MovementRules
{
    private static int AccumulateRegionScratch(MovementPeopleCache cache, ScenarioState region)
    {
        var stamp = ++cache.CultureStampTick;
        if (stamp == 0)
        {
            Array.Clear(cache.CultureStamp);
            Array.Clear(cache.NewRelStamp);
            stamp = cache.CultureStampTick = 1;
        }
        cache.TouchedCultures.Clear();
        cache.RegionRelCultList.Clear();
        cache.ScratchNewRelIds.Clear();

        var groups = CollectionsMarshal.AsSpan(region.PopulationGroups);
        if (groups.Length == 0) return stamp;

        // The ranks order this region's rows only if they are current now and no new name
        // turns up while it is read; otherwise the rows are compared as text, as they were.
        var ranked = !cache.RawRanksStale;
        var rows = 0;

        string lastCulture = null;
        string lastRawCulture = null;
        var lastCultId = -1;
        var lastCultRank = 0;
        string lastReligion = null;
        var lastRelId = -1;

        for (var gi = 0; gi < groups.Length; gi++)
        {
            var group = groups[gi];
            if (group.Size <= 0) continue;
            if (lastCultId < 0 || !ReferenceEquals(group.Culture, lastCulture))
            {
                lastCulture = group.Culture;
                var raw = FindRawRow(cache, lastCulture);
                if (raw < 0)
                {
                    raw = CanonicalRow(cache, ref lastCulture);
                    group.Culture = lastCulture;
                }
                lastRawCulture = lastCulture ?? "";
                lastCultId = cache.RawCultureId[raw];
                if (lastCultId < 0) cache.RawCultureId[raw] = lastCultId = ResolveCulture(cache, lastRawCulture).Id;
                lastCultRank = cache.RawRank[raw];
            }
            if (lastRelId < 0 || !ReferenceEquals(group.Religion, lastReligion))
            {
                lastReligion = group.Religion;
                var raw = FindRawRow(cache, lastReligion);
                if (raw < 0)
                {
                    raw = CanonicalRow(cache, ref lastReligion);
                    group.Religion = lastReligion;
                }
                lastRelId = cache.RawReligionId[raw];
                if (lastRelId < 0) cache.RawReligionId[raw] = lastRelId = ResolveReligion(cache, lastReligion ?? "");
            }
            var cultId = lastCultId;
            var relId = lastRelId;

            if (cache.CultureStamp[cultId] != stamp)
            {
                cache.CultureStamp[cultId] = stamp;
                cache.CultureSum[cultId] = group.Size;
                cache.TouchedCultures.Add(cultId);
                cache.CultureRowHead[cultId] = -1;
            }
            else
            {
                cache.CultureSum[cultId] += group.Size;
            }

            if (cache.NewRelStamp[relId] != stamp)
            {
                cache.NewRelStamp[relId] = stamp;
                cache.ScratchNewRelIds.Add(relId);
            }

            // This group's (faith, culture) row: one of the few its culture already has here,
            // or a new one, in the order the pairs are first met.
            var row = cache.CultureRowHead[cultId];
            while (row >= 0 && cache.RowRel[row] != relId) row = cache.RowNext[row];
            if (row >= 0)
            {
                cache.RowSize[row] += group.Size;
                continue;
            }
            if (rows == cache.RowRel.Length)
            {
                var capacity = rows * 2;
                Array.Resize(ref cache.RowRel, capacity);
                Array.Resize(ref cache.RowCult, capacity);
                Array.Resize(ref cache.RowRaw, capacity);
                Array.Resize(ref cache.RowSize, capacity);
                Array.Resize(ref cache.RowNext, capacity);
                Array.Resize(ref cache.RowRank, capacity);
            }
            row = rows++;
            cache.RowRel[row] = relId;
            cache.RowCult[row] = cultId;
            cache.RowRaw[row] = lastRawCulture;
            cache.RowSize[row] = group.Size;
            cache.RowRank[row] = lastCultRank;
            cache.RowNext[row] = cache.CultureRowHead[cultId];
            cache.CultureRowHead[cultId] = row;
        }

        var list = cache.RegionRelCultList;
        if (CanonicalPeopleOrder && rows > 1 && ranked && !cache.RawRanksStale)
        {
            // By the name's rank, then by the order met. Two rows of one faith never share a
            // name (one name is one culture, and a faith has one row a culture), so within a
            // faith - the only order the index keeps - this is the order comparing text gives.
            if (cache.RowKeys.Length < rows) cache.RowKeys = new long[cache.RowRel.Length];
            var keys = cache.RowKeys;
            for (var row = 0; row < rows; row++) keys[row] = ((long)cache.RowRank[row] << 32) | (uint)row;
            Array.Sort(keys, 0, rows);
            for (var k = 0; k < rows; k++)
            {
                var row = (int)(uint)keys[k];
                list.Add((cache.RowRel[row], cache.RowCult[row], cache.RowRaw[row], cache.RowSize[row]));
            }
        }
        else
        {
            for (var row = 0; row < rows; row++)
                list.Add((cache.RowRel[row], cache.RowCult[row], cache.RowRaw[row], cache.RowSize[row]));
            if (CanonicalPeopleOrder && rows > 1)
                list.Sort(static (a, b) => string.CompareOrdinal(a.RawCulture, b.RawCulture));
        }
        Array.Clear(cache.RowRaw, 0, rows);
        return stamp;
    }

    private static int[] UpdateIntArray(int[] existing, List<int> source, out int count)
    {
        count = source.Count;
        if (count == 0) return existing ?? Array.Empty<int>();
        var arr = existing is not null && existing.Length >= count ? existing : new int[Math.Max(8, count)];
        CollectionsMarshal.AsSpan(source).CopyTo(arr);
        return arr;
    }

    private static int[] CopyIntPrefix(int[] existing, int[] source, int count, out int outCount)
    {
        outCount = count;
        if (count == 0) return existing ?? Array.Empty<int>();
        var arr = existing is not null && existing.Length >= count ? existing : new int[Math.Max(8, count)];
        source.AsSpan(0, count).CopyTo(arr);
        return arr;
    }

    private const int PeopleIndexChunkSize = 128;

    private sealed class PeopleWorkerScratch
    {
        public int StampTick;
        public int[] CultureStamp = new int[64];
        public long[] CultureSum = new long[64];
        public int[] CultureRowHead = new int[64];
        public int[] NewRelStamp = new int[64];
        public int[] RowRel = new int[64];
        public int[] RowCult = new int[64];
        public string[] RowRaw = new string[64];
        public long[] RowSize = new long[64];
        public int[] RowNext = new int[64];
        public int[] RowRank = new int[64];
        public long[] RowKeys = new long[64];
        public readonly List<int> TouchedCultures = new();
        public readonly List<int> NewRelIds = new();
        public readonly List<(int RelId, string RawCulture, long Size)> RelCultList = new();
    }

    [ThreadStatic] private static PeopleWorkerScratch _peopleWorkerScratch;

    private static void BuildPeopleIndexChunk(Campaign campaign, MovementPeopleCache cache,
        IReadOnlyList<ScenarioState> orderedStates, int start, int end)
    {
        var count = end - start;
        if (count < 48 || !CanonicalPeopleOrder || !DeterministicParallel.Enabled(campaign))
        {
            BuildPeopleIndexBatch(cache, orderedStates, start, end);
            return;
        }
        if (cache.RawRanksStale) RankRawRows(cache);
        if (cache.ParallelSlots.Length < count)
        {
            var oldLen = cache.ParallelSlots.Length;
            Array.Resize(ref cache.ParallelSlots, Math.Max(PeopleIndexChunkSize, count));
            for (var i = oldLen; i < cache.ParallelSlots.Length; i++)
                cache.ParallelSlots[i] = new MovementPeopleCache.ParallelStateSlot();
        }
        var slots = cache.ParallelSlots;
        var cultCap = cache.CanonicalCultures.Count;
        var relCap = cache.ReligionBucketsById.Count;

        DeterministicParallel.ForRange(campaign, start, end, (from, to) =>
        {
            var worker = _peopleWorkerScratch ??= new PeopleWorkerScratch();
            if (worker.CultureStamp.Length < cultCap)
            {
                var nextCap = Math.Max(64, cultCap * 2);
                worker.CultureStamp = new int[nextCap];
                worker.CultureSum = new long[nextCap];
                worker.CultureRowHead = new int[nextCap];
            }
            if (worker.NewRelStamp.Length < relCap)
                worker.NewRelStamp = new int[Math.Max(64, relCap * 2)];

            for (var idx = from; idx < to; idx++)
                AccumulateWorkerSlot(cache, worker, orderedStates[idx], slots[idx - start]);
        }, minItems: 32);

        for (var idx = start; idx < end; idx++)
        {
            var slot = slots[idx - start];
            if (slot.NeedsMainThread)
            {
                BuildPeopleIndexBatch(cache, orderedStates, idx, end);
                break;
            }
            cache.StateDemographySigs[idx] = slot.Sig;
            var region = orderedStates[idx];
            if (idx < cache.StateHeaderPop.Length)
            {
                cache.StateHeaderPop[idx] = region?.Population ?? 0L;
                cache.StateHeaderGroupCount[idx] = region?.PopulationGroups.Count ?? 0;
            }
            if (region is null || region.PopulationGroups.Count == 0)
            {
                cache.StateCultCounts[idx] = 0;
                cache.StateRelCounts[idx] = 0;
                continue;
            }
            for (var ti = 0; ti < slot.CultCount; ti++)
                cache.CultureBucketsById[slot.CultIds[ti]].Add(new CultureRegionBucket(region, slot.CultSums[ti]));
            for (var ri = 0; ri < slot.RelCultCount; ri++)
            {
                var item = slot.RelCults[ri];
                cache.ReligionBucketsById[item.RelId].Add(new ReligionCultureBucket(region, item.RawCulture, item.Size));
            }
            cache.StateCultIds[idx] = CopyIntPrefix(cache.StateCultIds[idx], slot.CultIds, slot.CultCount, out cache.StateCultCounts[idx]);
            cache.StateRelIds[idx] = CopyIntPrefix(cache.StateRelIds[idx], slot.NewRelIds, slot.NewRelCount, out cache.StateRelCounts[idx]);
        }
    }

    private static void AccumulateWorkerSlot(MovementPeopleCache cache, PeopleWorkerScratch worker,
        ScenarioState region, MovementPeopleCache.ParallelStateSlot slot)
    {
        slot.NeedsMainThread = false;
        slot.Sig = StateDemographySignature(cache, region);
        if (region is null || region.PopulationGroups.Count == 0)
        {
            slot.CultCount = 0;
            slot.RelCultCount = 0;
            slot.NewRelCount = 0;
            return;
        }
        var groups = CollectionsMarshal.AsSpan(region.PopulationGroups);
        var stamp = ++worker.StampTick;
        if (stamp == 0)
        {
            Array.Clear(worker.CultureStamp);
            Array.Clear(worker.NewRelStamp);
            stamp = worker.StampTick = 1;
        }
        worker.TouchedCultures.Clear();
        worker.NewRelIds.Clear();
        worker.RelCultList.Clear();

        var rows = 0;
        string lastCulture = null, lastRawCulture = null, lastReligion = null;
        var lastCultId = -1;
        var lastCultRank = 0;
        var lastRelId = -1;
        var rawCultureIds = cache.RawCultureId;
        var rawReligionIds = cache.RawReligionId;
        var rawRanks = cache.RawRank;

        for (var gi = 0; gi < groups.Length; gi++)
        {
            var group = groups[gi];
            if (group.Size <= 0) continue;
            if (lastCultId < 0 || !ReferenceEquals(group.Culture, lastCulture))
            {
                lastCulture = group.Culture;
                var raw = FindRawRow(cache, lastCulture);
                if (raw < 0 || (lastCultId = rawCultureIds[raw]) < 0 || lastCultId >= worker.CultureStamp.Length)
                {
                    slot.NeedsMainThread = true;
                    return;
                }
                lastRawCulture = lastCulture ?? "";
                lastCultRank = rawRanks[raw];
            }
            if (lastRelId < 0 || !ReferenceEquals(group.Religion, lastReligion))
            {
                lastReligion = group.Religion;
                var raw = FindRawRow(cache, lastReligion);
                if (raw < 0 || (lastRelId = rawReligionIds[raw]) < 0 || lastRelId >= worker.NewRelStamp.Length)
                {
                    slot.NeedsMainThread = true;
                    return;
                }
            }
            var cultId = lastCultId;
            var relId = lastRelId;
            if (worker.CultureStamp[cultId] != stamp)
            {
                worker.CultureStamp[cultId] = stamp;
                worker.CultureSum[cultId] = group.Size;
                worker.TouchedCultures.Add(cultId);
                worker.CultureRowHead[cultId] = -1;
            }
            else
            {
                worker.CultureSum[cultId] += group.Size;
            }
            if (worker.NewRelStamp[relId] != stamp)
            {
                worker.NewRelStamp[relId] = stamp;
                worker.NewRelIds.Add(relId);
            }
            var row = worker.CultureRowHead[cultId];
            while (row >= 0 && worker.RowRel[row] != relId) row = worker.RowNext[row];
            if (row >= 0) { worker.RowSize[row] += group.Size; continue; }
            if (rows == worker.RowRel.Length)
            {
                var capacity = rows * 2;
                Array.Resize(ref worker.RowRel, capacity);
                Array.Resize(ref worker.RowCult, capacity);
                Array.Resize(ref worker.RowRaw, capacity);
                Array.Resize(ref worker.RowSize, capacity);
                Array.Resize(ref worker.RowNext, capacity);
                Array.Resize(ref worker.RowRank, capacity);
            }
            row = rows++;
            worker.RowRel[row] = relId;
            worker.RowCult[row] = cultId;
            worker.RowRaw[row] = lastRawCulture;
            worker.RowSize[row] = group.Size;
            worker.RowRank[row] = lastCultRank;
            worker.RowNext[row] = worker.CultureRowHead[cultId];
            worker.CultureRowHead[cultId] = row;
        }

        if (rows > 1)
        {
            if (worker.RowKeys.Length < rows) worker.RowKeys = new long[worker.RowRel.Length];
            var keys = worker.RowKeys;
            for (var row = 0; row < rows; row++) keys[row] = ((long)worker.RowRank[row] << 32) | (uint)row;
            Array.Sort(keys, 0, rows);
            for (var k = 0; k < rows; k++)
            {
                var row = (int)(uint)keys[k];
                worker.RelCultList.Add((worker.RowRel[row], worker.RowRaw[row], worker.RowSize[row]));
            }
        }
        else if (rows == 1)
        {
            worker.RelCultList.Add((worker.RowRel[0], worker.RowRaw[0], worker.RowSize[0]));
        }
        Array.Clear(worker.RowRaw, 0, rows);

        var cultCount = worker.TouchedCultures.Count;
        slot.CultCount = cultCount;
        if (slot.CultIds.Length < cultCount)
        {
            slot.CultIds = new int[cultCount];
            slot.CultSums = new long[cultCount];
        }
        for (var i = 0; i < cultCount; i++)
        {
            var cid = worker.TouchedCultures[i];
            slot.CultIds[i] = cid;
            slot.CultSums[i] = worker.CultureSum[cid];
        }

        var rcCount = worker.RelCultList.Count;
        slot.RelCultCount = rcCount;
        if (slot.RelCults.Length < rcCount) slot.RelCults = new (int, string, long)[rcCount];
        CollectionsMarshal.AsSpan(worker.RelCultList).CopyTo(slot.RelCults);

        var relCount = worker.NewRelIds.Count;
        slot.NewRelCount = relCount;
        if (slot.NewRelIds.Length < relCount) slot.NewRelIds = new int[relCount];
        CollectionsMarshal.AsSpan(worker.NewRelIds).CopyTo(slot.NewRelIds);
    }

    private static void BuildPeopleIndexBatch(MovementPeopleCache cache, IReadOnlyList<ScenarioState> orderedStates, int start, int end)
    {
        if (cache.RawRanksStale) RankRawRows(cache);
        for (var idx = start; idx < end; idx++)
        {
            var region = orderedStates[idx];
            cache.StateDemographySigs[idx] = StateDemographySignature(cache, region);
            if (idx < cache.StateHeaderPop.Length)
            {
                cache.StateHeaderPop[idx] = region?.Population ?? 0L;
                cache.StateHeaderGroupCount[idx] = region?.PopulationGroups.Count ?? 0;
            }
            if (region is null || region.PopulationGroups.Count == 0)
            {
                cache.StateCultCounts[idx] = 0;
                cache.StateRelCounts[idx] = 0;
                continue;
            }

            AccumulateRegionScratch(cache, region);

            var touched = CollectionsMarshal.AsSpan(cache.TouchedCultures);
            for (var ti = 0; ti < touched.Length; ti++)
            {
                var cultId = touched[ti];
                cache.CultureBucketsById[cultId].Add(new CultureRegionBucket(region, cache.CultureSum[cultId]));
            }

            var relCults = CollectionsMarshal.AsSpan(cache.RegionRelCultList);
            for (var ri = 0; ri < relCults.Length; ri++)
            {
                var item = relCults[ri];
                cache.ReligionBucketsById[item.RelId].Add(new ReligionCultureBucket(region, item.RawCulture, item.Size));
            }

            cache.StateCultIds[idx] = UpdateIntArray(cache.StateCultIds[idx], cache.TouchedCultures, out cache.StateCultCounts[idx]);
            cache.StateRelIds[idx] = UpdateIntArray(cache.StateRelIds[idx], cache.ScratchNewRelIds, out cache.StateRelCounts[idx]);
        }
    }

    private static void AddPatchCultOp(MovementPeopleCache cache, int patchTick, int cultId, int mapIndex, ScenarioState region, long size)
    {
        if (cache.PatchCultStamp[cultId] != patchTick)
        {
            cache.PatchCultStamp[cultId] = patchTick;
            cache.PatchCultHead[cultId] = -1;
            cache.PatchCultTail[cultId] = -1;
            cache.TouchedPatchCults.Add(cultId);
        }
        var opIdx = cache.ScratchCultOps.Count;
        cache.ScratchCultOps.Add((mapIndex, region, size));
        cache.ScratchCultOpsNext.Add(-1);
        if (cache.PatchCultHead[cultId] < 0) cache.PatchCultHead[cultId] = opIdx;
        else cache.ScratchCultOpsNext[cache.PatchCultTail[cultId]] = opIdx;
        cache.PatchCultTail[cultId] = opIdx;
    }

    private static void AddPatchRelOp(MovementPeopleCache cache, int patchTick, int relId, int mapIndex, ScenarioState region, string rawCulture, long size)
    {
        if (cache.PatchRelStamp[relId] != patchTick)
        {
            cache.PatchRelStamp[relId] = patchTick;
            cache.PatchRelHead[relId] = -1;
            cache.PatchRelTail[relId] = -1;
            cache.TouchedPatchRels.Add(relId);
        }
        var opIdx = cache.ScratchRelOps.Count;
        cache.ScratchRelOps.Add((mapIndex, region, rawCulture, size));
        cache.ScratchRelOpsNext.Add(-1);
        if (cache.PatchRelHead[relId] < 0) cache.PatchRelHead[relId] = opIdx;
        else cache.ScratchRelOpsNext[cache.PatchRelTail[relId]] = opIdx;
        cache.PatchRelTail[relId] = opIdx;
    }

    private static void AccumulatePatchSlotsBatch(MovementPeopleCache cache, IReadOnlyList<ScenarioState> orderedStates, int patchTick, int start, int end)
    {
        if (cache.RawRanksStale) RankRawRows(cache);
        for (var di = start; di < end; di++)
        {
            var slot = cache.DirtySlots[di];
            var region = orderedStates[slot];
            var stamp = AccumulateRegionScratch(cache, region);
            var mapIndex = region.MapIndex;

            var oldCults = cache.StateCultIds[slot];
            var oldCultCount = cache.StateCultCounts[slot];
            if (oldCults is not null)
            {
                for (var oi = 0; oi < oldCultCount; oi++)
                {
                    var oldCultId = oldCults[oi];
                    if (cache.CultureStamp[oldCultId] != stamp)
                        AddPatchCultOp(cache, patchTick, oldCultId, mapIndex, region, 0L);
                }
            }
            var touched = CollectionsMarshal.AsSpan(cache.TouchedCultures);
            for (var ti = 0; ti < touched.Length; ti++)
            {
                var cultId = touched[ti];
                AddPatchCultOp(cache, patchTick, cultId, mapIndex, region, cache.CultureSum[cultId]);
            }

            var relCults = CollectionsMarshal.AsSpan(cache.RegionRelCultList);
            for (var ri = 0; ri < relCults.Length; ri++)
            {
                var item = relCults[ri];
                AddPatchRelOp(cache, patchTick, item.RelId, mapIndex, region, item.RawCulture, item.Size);
            }
            var oldRels = cache.StateRelIds[slot];
            var oldRelCount = cache.StateRelCounts[slot];
            if (oldRels is not null)
            {
                for (var oi = 0; oi < oldRelCount; oi++)
                {
                    var oldRelId = oldRels[oi];
                    if (cache.NewRelStamp[oldRelId] != stamp)
                        AddPatchRelOp(cache, patchTick, oldRelId, mapIndex, region, null, 0L);
                }
            }

            cache.StateCultIds[slot] = UpdateIntArray(cache.StateCultIds[slot], cache.TouchedCultures, out cache.StateCultCounts[slot]);
            cache.StateRelIds[slot] = UpdateIntArray(cache.StateRelIds[slot], cache.ScratchNewRelIds, out cache.StateRelCounts[slot]);
            cache.StateDemographySigs[slot] = StateDemographySignature(cache, region);
            if (slot < cache.StateHeaderPop.Length)
            {
                cache.StateHeaderPop[slot] = region?.Population ?? 0L;
                cache.StateHeaderGroupCount[slot] = region?.PopulationGroups.Count ?? 0;
            }
        }
    }

    private static void MergePatchCultBatch(MovementPeopleCache cache, int start, int end)
    {
        var scratch = cache.ScratchCultBuckets;
        for (var ci = start; ci < end; ci++)
        {
            var cultId = cache.TouchedPatchCults[ci];
            var list = cache.CultureBucketsById[cultId];
            var count = list.Count;
            var head = cache.PatchCultHead[cultId];

            if (count > 0)
            {
                var lo = 0;
                var hiMax = count - 1;
                var opFast = head;
                var fallback = false;
                var listSpan = CollectionsMarshal.AsSpan(list);
                while (opFast >= 0)
                {
                    var curOp = cache.ScratchCultOps[opFast];
                    if (curOp.Size <= 0) { fallback = true; break; }
                    var targetMap = curOp.MapIndex;
                    var hi = hiMax;
                    var found = -1;
                    while (lo <= hi)
                    {
                        var mid = lo + ((hi - lo) >> 1);
                        var midMap = listSpan[mid].Region.MapIndex;
                        if (midMap == targetMap) { found = mid; break; }
                        if (midMap < targetMap) lo = mid + 1;
                        else hi = mid - 1;
                    }
                    if (found < 0) { fallback = true; break; }
                    listSpan[found] = new CultureRegionBucket(curOp.Region, curOp.Size);
                    lo = found + 1;
                    opFast = cache.ScratchCultOpsNext[opFast];
                }
                if (!fallback) continue;
            }

            scratch.Clear();
            var i = 0;
            var op = head;
            while (i < count && op >= 0)
            {
                var item = list[i];
                var opData = cache.ScratchCultOps[op];
                var itemMap = item.Region.MapIndex;
                var opMap = opData.MapIndex;
                if (itemMap < opMap)
                {
                    scratch.Add(item);
                    i++;
                }
                else if (itemMap == opMap)
                {
                    if (opData.Size > 0)
                        scratch.Add(new CultureRegionBucket(opData.Region, opData.Size));
                    i++;
                    op = cache.ScratchCultOpsNext[op];
                }
                else
                {
                    if (opData.Size > 0)
                        scratch.Add(new CultureRegionBucket(opData.Region, opData.Size));
                    op = cache.ScratchCultOpsNext[op];
                }
            }
            while (i < count) scratch.Add(list[i++]);
            while (op >= 0)
            {
                var opData = cache.ScratchCultOps[op];
                if (opData.Size > 0)
                    scratch.Add(new CultureRegionBucket(opData.Region, opData.Size));
                op = cache.ScratchCultOpsNext[op];
            }
            list.Clear();
            if (list.Capacity < scratch.Count) list.Capacity = scratch.Count;
            var scratchSpan = CollectionsMarshal.AsSpan(scratch);
            for (var si = 0; si < scratchSpan.Length; si++) list.Add(scratchSpan[si]);
        }
    }

    private static void MergePatchRelBatch(MovementPeopleCache cache, int start, int end)
    {
        var scratch = cache.ScratchRelBuckets;
        for (var ri = start; ri < end; ri++)
        {
            var relId = cache.TouchedPatchRels[ri];
            var list = cache.ReligionBucketsById[relId];
            var count = list.Count;
            var head = cache.PatchRelHead[relId];

            if (count > 0)
            {
                var lo = 0;
                var opFast = head;
                var fallback = false;
                var listSpan = CollectionsMarshal.AsSpan(list);
                while (opFast >= 0)
                {
                    var firstOp = cache.ScratchRelOps[opFast];
                    if (firstOp.Size <= 0) { fallback = true; break; }
                    var targetMap = firstOp.MapIndex;
                    var l = lo;
                    var r = count - 1;
                    var any = -1;
                    while (l <= r)
                    {
                        var mid = l + ((r - l) >> 1);
                        var midMap = listSpan[mid].Region.MapIndex;
                        if (midMap == targetMap) { any = mid; break; }
                        if (midMap < targetMap) l = mid + 1;
                        else r = mid - 1;
                    }
                    if (any < 0) { fallback = true; break; }
                    var runStart = any;
                    while (runStart > lo && listSpan[runStart - 1].Region.MapIndex == targetMap) runStart--;
                    var runEnd = any + 1;
                    while (runEnd < count && listSpan[runEnd].Region.MapIndex == targetMap) runEnd++;
                    var listRunLen = runEnd - runStart;

                    var opCursor = opFast;
                    var k = 0;
                    while (opCursor >= 0)
                    {
                        var curOp = cache.ScratchRelOps[opCursor];
                        if (curOp.MapIndex != targetMap) break;
                        if (curOp.Size <= 0 || k >= listRunLen) { fallback = true; break; }
                        opCursor = cache.ScratchRelOpsNext[opCursor];
                        k++;
                    }
                    if (fallback || k != listRunLen) { fallback = true; break; }
                    opCursor = opFast;
                    for (var j = 0; j < listRunLen; j++)
                    {
                        var curOp = cache.ScratchRelOps[opCursor];
                        listSpan[runStart + j] = new ReligionCultureBucket(curOp.Region, curOp.RawCulture, curOp.Size);
                        opCursor = cache.ScratchRelOpsNext[opCursor];
                    }
                    lo = runEnd;
                    opFast = opCursor;
                }
                if (!fallback) continue;
            }

            scratch.Clear();
            var i = 0;
            var op = head;
            while (i < count && op >= 0)
            {
                var item = list[i];
                var opData = cache.ScratchRelOps[op];
                var itemMap = item.Region.MapIndex;
                var opMap = opData.MapIndex;
                if (itemMap < opMap)
                {
                    scratch.Add(item);
                    i++;
                }
                else if (itemMap == opMap)
                {
                    while (i < count && list[i].Region.MapIndex == opMap) i++;
                    while (op >= 0 && cache.ScratchRelOps[op].MapIndex == opMap)
                    {
                        var curOp = cache.ScratchRelOps[op];
                        if (curOp.Size > 0)
                            scratch.Add(new ReligionCultureBucket(curOp.Region, curOp.RawCulture, curOp.Size));
                        op = cache.ScratchRelOpsNext[op];
                    }
                }
                else
                {
                    while (op >= 0 && cache.ScratchRelOps[op].MapIndex == opMap)
                    {
                        var curOp = cache.ScratchRelOps[op];
                        if (curOp.Size > 0)
                            scratch.Add(new ReligionCultureBucket(curOp.Region, curOp.RawCulture, curOp.Size));
                        op = cache.ScratchRelOpsNext[op];
                    }
                }
            }
            while (i < count) scratch.Add(list[i++]);
            while (op >= 0)
            {
                var curOp = cache.ScratchRelOps[op];
                if (curOp.Size > 0)
                    scratch.Add(new ReligionCultureBucket(curOp.Region, curOp.RawCulture, curOp.Size));
                op = cache.ScratchRelOpsNext[op];
            }
            list.Clear();
            if (list.Capacity < scratch.Count) list.Capacity = scratch.Count;
            var scratchSpan = CollectionsMarshal.AsSpan(scratch);
            for (var si = 0; si < scratchSpan.Length; si++) list.Add(scratchSpan[si]);
        }
    }

    internal static int PeopleIndexBuilds(Campaign campaign) =>
        campaign is null ? 0 : campaign.MovementPeople.IndexBuilds;

    /// <summary>
    /// The population stamp the people index already stored: one region's signature per
    /// slot, folded together. Empty when the index has not been built. Not a second census.
    /// </summary>
    internal static ulong PopulationStamp(Campaign campaign)
    {
        var cache = campaign?.MovementPeople;
        if (cache is null || !cache.Indexed) return 0UL;
        var values = cache.StateDemographySigs;
        ulong sig = 14695981039346656037UL;
        sig = Mix(sig, (ulong)values.Length);
        for (var i = 0; i < values.Length; i++)
            sig = Mix(sig, values[i]);
        return sig;
    }

    internal static bool NeedsPeopleIndex(Campaign campaign)
    {
        var cache = campaign.MovementPeople;
        return !cache.Indexed || cache.Day != campaign.ElapsedDays || cache.States != campaign.States.Count ||
            cache.OwnershipRevision != campaign.OwnershipRevision;
    }

    internal static IEnumerable<int> EnsurePeopleIndexedStages(Campaign campaign, bool insideMovRules)
    {
        // The stamps that decide what is dirty were the unnamed twenty milliseconds the profile
        // put "after mov:people": named now, beside the index they decide about.
        if (insideMovRules)
        {
            using (SimProfiler.Measure("movements"))
            using (SimProfiler.Measure("  mov:rules"))
            using (SimProfiler.Measure("    mov:people"))
            using (SimProfiler.Measure("      mov:people:stamp"))
                EnsurePeopleCache(campaign);
        }
        else
        {
            using (SimProfiler.Measure("        mov:people:stamp"))
                EnsurePeopleCache(campaign);
        }
        var cache = campaign.MovementPeople;
        if (cache.Indexed) yield break;
        cache.IndexBuilds++;

        var orderedStates = OrderedStatesOf(cache, campaign);
        var totalStates = orderedStates.Count;

        if (cache.IncrementalReady && cache.DirtySlots.Count > 0 && cache.StateDemographySigs.Length == totalStates)
        {
            cache.IncrementalReady = false;
            var patchTick = ++cache.PatchTick;
            if (patchTick == 0)
            {
                Array.Clear(cache.PatchCultStamp);
                Array.Clear(cache.PatchRelStamp);
                patchTick = cache.PatchTick = 1;
            }
            cache.TouchedPatchCults.Clear();
            cache.TouchedPatchRels.Clear();
            cache.ScratchCultOps.Clear();
            cache.ScratchCultOpsNext.Clear();
            cache.ScratchRelOps.Clear();
            cache.ScratchRelOpsNext.Clear();

            var dirtyCount = cache.DirtySlots.Count;
            var di = 0;
            while (di < dirtyCount)
            {
                var batchEnd = Math.Min(dirtyCount, di + 32);
                if (insideMovRules)
                {
                    using (SimProfiler.Measure("movements"))
                    using (SimProfiler.Measure("  mov:rules"))
                    using (SimProfiler.Measure("    mov:people"))
                    using (SimProfiler.Measure("      mov:people:index"))
                        AccumulatePatchSlotsBatch(cache, orderedStates, patchTick, di, batchEnd);
                }
                else
                {
                    using (SimProfiler.Measure("        mov:people:index"))
                        AccumulatePatchSlotsBatch(cache, orderedStates, patchTick, di, batchEnd);
                }
                di = batchEnd;
                if (di < dirtyCount) yield return 0;
            }

            var ci = 0;
            var cultCount = cache.TouchedPatchCults.Count;
            while (ci < cultCount)
            {
                var batchEnd = Math.Min(cultCount, ci + 48);
                if (insideMovRules)
                {
                    using (SimProfiler.Measure("movements"))
                    using (SimProfiler.Measure("  mov:rules"))
                    using (SimProfiler.Measure("    mov:people"))
                    using (SimProfiler.Measure("      mov:people:index"))
                        MergePatchCultBatch(cache, ci, batchEnd);
                }
                else
                {
                    using (SimProfiler.Measure("        mov:people:index"))
                        MergePatchCultBatch(cache, ci, batchEnd);
                }
                ci = batchEnd;
                if (ci < cultCount || cache.TouchedPatchRels.Count > 0) yield return 0;
            }

            var ri = 0;
            var relCount = cache.TouchedPatchRels.Count;
            while (ri < relCount)
            {
                var batchEnd = Math.Min(relCount, ri + 32);
                if (insideMovRules)
                {
                    using (SimProfiler.Measure("movements"))
                    using (SimProfiler.Measure("  mov:rules"))
                    using (SimProfiler.Measure("    mov:people"))
                    using (SimProfiler.Measure("      mov:people:index"))
                        MergePatchRelBatch(cache, ri, batchEnd);
                }
                else
                {
                    using (SimProfiler.Measure("        mov:people:index"))
                        MergePatchRelBatch(cache, ri, batchEnd);
                }
                ri = batchEnd;
                if (ri < relCount) yield return 0;
            }

            cache.DirtySlots.Clear();
            cache.IncrementalReady = cache.StrictMapIndexOrder && CanonicalPeopleOrder;
            cache.OwnershipRevision = campaign.OwnershipRevision;
            cache.TrackedClockSeq = campaign.SimulationTickClockSeq;
            cache.StepDirtyPrimed = true;
            cache.Indexed = true;
            yield return 0;
            yield break;
        }

        if (cache.StateDemographySigs.Length != totalStates)
        {
            cache.StateDemographySigs = new ulong[totalStates];
            cache.StateHeaderPop = new long[totalStates];
            cache.StateHeaderGroupCount = new int[totalStates];
            cache.StateCultIds = new int[totalStates][];
            cache.StateRelIds = new int[totalStates][];
            cache.StateCultCounts = new int[totalStates];
            cache.StateRelCounts = new int[totalStates];
        }
        else
        {
            if (cache.StateHeaderPop.Length != totalStates)
            {
                cache.StateHeaderPop = new long[totalStates];
                cache.StateHeaderGroupCount = new int[totalStates];
            }
            if (cache.StateCultCounts.Length != totalStates)
            {
                cache.StateCultCounts = new int[totalStates];
                cache.StateRelCounts = new int[totalStates];
            }
        }
        cache.DirtySlots.Clear();
        cache.IncrementalReady = false;

        for (var i = 0; i < cache.CultureBucketsById.Count; i++) cache.CultureBucketsById[i].Clear();
        for (var i = 0; i < cache.ReligionBucketsById.Count; i++) cache.ReligionBucketsById[i].Clear();

        var chunkSize = Campaign.IsUnboundedStep ? Math.Max(totalStates, PeopleIndexChunkSize) : PeopleIndexChunkSize;
        var idx = 0;
        while (idx < totalStates)
        {
            var batchEnd = Math.Min(totalStates, idx + chunkSize);
            if (insideMovRules)
            {
                using (SimProfiler.Measure("movements"))
                using (SimProfiler.Measure("  mov:rules"))
                using (SimProfiler.Measure("    mov:people"))
                using (SimProfiler.Measure("      mov:people:index"))
                    BuildPeopleIndexChunk(campaign, cache, orderedStates, idx, batchEnd);
            }
            else
            {
                using (SimProfiler.Measure("        mov:people:index"))
                    BuildPeopleIndexChunk(campaign, cache, orderedStates, idx, batchEnd);
            }
            idx = batchEnd;
            if (idx < totalStates && Campaign.StageOverdue()) yield return 0;
        }
        cache.IncrementalReady = cache.StrictMapIndexOrder && CanonicalPeopleOrder;
        cache.OwnershipRevision = campaign.OwnershipRevision;
        cache.TrackedClockSeq = campaign.SimulationTickClockSeq;
        cache.StepDirtyPrimed = true;
        cache.Indexed = true;
        yield return 0;
    }
}
