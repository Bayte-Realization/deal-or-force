using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// One alliance or guarantee edge pointing at a protected nation: <see cref="ProtectorId"/>
/// stands ready to defend the target under <see cref="Relation"/>.
/// </summary>
internal readonly struct ProtectorEdge
{
    public readonly int ProtectorId;
    public readonly DiplomaticRelation Relation;

    public ProtectorEdge(int protectorId, DiplomaticRelation relation)
    {
        ProtectorId = protectorId;
        Relation = relation;
    }
}

/// <summary>
/// Campaign-owned O(1) dense index of alliances and guarantees, keyed on
/// <see cref="Campaign.AllianceRevision"/> and <see cref="Campaign.Relations"/> count.
///
/// Replaces repeated full-table walks of 20k–80k <see cref="Campaign.Relations"/> entries in
/// <see cref="Statecraft.DefendersOf"/>, <see cref="Statecraft.DefensiveCallIds"/>,
/// <see cref="Statecraft.CallableAllies"/>, <see cref="AiStatecraft.AskForShelter"/>,
/// <see cref="AiStatecraft.HelpFor"/>, <see cref="AiStatecraft.PursueWarPlan"/>, and
/// <see cref="WorldWar"/>. Published via an immutable volatile snapshot so concurrent readers
/// never contend or observe partial rebuilds.
/// </summary>
internal sealed class ProtectorIndex
{
    private const int DenseLimit = 65536;

    private sealed class Snapshot
    {
        public readonly Dictionary<long, DiplomaticRelation> RelationsRef;
        public readonly int AllianceRevision;
        public readonly int RelationsCount;
        public readonly int MutationVersion;
        public readonly ProtectorEdge[][] ProtectorsByTarget;
        public readonly int[][] AlliesByNation;
        public readonly Dictionary<int, ProtectorEdge[]> OverflowProtectors;
        public readonly Dictionary<int, int[]> OverflowAllies;

        public Snapshot(
            Dictionary<long, DiplomaticRelation> relationsRef,
            int allianceRevision,
            int relationsCount,
            int mutationVersion,
            ProtectorEdge[][] protectorsByTarget,
            int[][] alliesByNation,
            Dictionary<int, ProtectorEdge[]> overflowProtectors,
            Dictionary<int, int[]> overflowAllies)
        {
            RelationsRef = relationsRef;
            AllianceRevision = allianceRevision;
            RelationsCount = relationsCount;
            MutationVersion = mutationVersion;
            ProtectorsByTarget = protectorsByTarget;
            AlliesByNation = alliesByNation;
            OverflowProtectors = overflowProtectors;
            OverflowAllies = overflowAllies;
        }
    }

    private readonly object _gate = new();
    private volatile Snapshot _snapshot;
    private List<ProtectorEdge>[] _scratchProtLists = Array.Empty<List<ProtectorEdge>>();
    private List<int>[] _scratchAllyLists = Array.Empty<List<int>>();
    private readonly Stack<List<ProtectorEdge>> _protPool = new();
    private readonly Stack<List<int>> _allyPool = new();
    private readonly List<int> _touchedProt = new();
    private readonly List<int> _touchedAlly = new();
    private readonly Dictionary<int, List<ProtectorEdge>> _overProt = new();
    private readonly Dictionary<int, List<int>> _overAlly = new();

    private Snapshot TryCurrent(Campaign c)
    {
        var current = _snapshot;
        if (current is not null &&
            ReferenceEquals(current.RelationsRef, c.Relations) &&
            current.AllianceRevision == c.AllianceRevision &&
            current.MutationVersion == DiplomaticRelation.ProtectorMutationVersion &&
            c.Relations.Count >= current.RelationsCount)
            return current;
        return null;
    }

    private Snapshot Ensure(Campaign c)
    {
        var current = TryCurrent(c);
        if (current is not null) return current;

        lock (_gate)
        {
            current = TryCurrent(c);
            if (current is not null) return current;

            var relDict = c.Relations;
            var rev = c.AllianceRevision;
            var relCount = relDict.Count;
            var mutVer = DiplomaticRelation.ProtectorMutationVersion;

            var maxDense = c.Nations.Count;
            var nations = c.Nations;
            for (var i = 0; i < nations.Count; i++)
            {
                var id = nations[i].Id;
                if ((uint)id < DenseLimit && id >= maxDense) maxDense = id + 1;
            }

            if (_scratchProtLists.Length < maxDense)
            {
                Array.Resize(ref _scratchProtLists, maxDense);
                Array.Resize(ref _scratchAllyLists, maxDense);
            }
            var protLists = _scratchProtLists;
            var allyLists = _scratchAllyLists;
            _touchedProt.Clear();
            _touchedAlly.Clear();
            _overProt.Clear();
            _overAlly.Clear();

            foreach (var rel in relDict.Values)
            {
                var allied = rel.Stance == DiplomaticStance.Allied;
                var aProtectsB = allied || rel.GuaranteeByA;
                var bProtectsA = allied || rel.GuaranteeByB;
                if (!aProtectsB && !bProtectsA) continue;

                if (aProtectsB)
                {
                    if ((uint)rel.B < (uint)maxDense)
                    {
                        var list = protLists[rel.B];
                        if (list is null)
                        {
                            list = _protPool.Count > 0 ? _protPool.Pop() : new List<ProtectorEdge>(4);
                            protLists[rel.B] = list;
                            _touchedProt.Add(rel.B);
                        }
                        list.Add(new ProtectorEdge(rel.A, rel));
                    }
                    else AddOverflowProt(rel.B, new ProtectorEdge(rel.A, rel));
                }
                if (bProtectsA)
                {
                    if ((uint)rel.A < (uint)maxDense)
                    {
                        var list = protLists[rel.A];
                        if (list is null)
                        {
                            list = _protPool.Count > 0 ? _protPool.Pop() : new List<ProtectorEdge>(4);
                            protLists[rel.A] = list;
                            _touchedProt.Add(rel.A);
                        }
                        list.Add(new ProtectorEdge(rel.B, rel));
                    }
                    else AddOverflowProt(rel.A, new ProtectorEdge(rel.B, rel));
                }
                if (allied)
                {
                    AddDenseOrOverAlly(rel.A, rel.B, maxDense, allyLists);
                    AddDenseOrOverAlly(rel.B, rel.A, maxDense, allyLists);
                }
            }

            var protectors = new ProtectorEdge[maxDense][];
            Array.Fill(protectors, Array.Empty<ProtectorEdge>());
            for (var i = 0; i < _touchedProt.Count; i++)
            {
                var id = _touchedProt[i];
                var list = protLists[id];
                protectors[id] = list.ToArray();
                list.Clear();
                if (_protPool.Count < 512) _protPool.Push(list);
                protLists[id] = null;
            }

            var allies = new int[maxDense][];
            Array.Fill(allies, Array.Empty<int>());
            for (var i = 0; i < _touchedAlly.Count; i++)
            {
                var id = _touchedAlly[i];
                var list = allyLists[id];
                allies[id] = list.ToArray();
                list.Clear();
                if (_allyPool.Count < 512) _allyPool.Push(list);
                allyLists[id] = null;
            }

            Dictionary<int, ProtectorEdge[]> frozenOverProt = null;
            if (_overProt.Count > 0)
            {
                frozenOverProt = new Dictionary<int, ProtectorEdge[]>(_overProt.Count);
                foreach (var kv in _overProt)
                {
                    frozenOverProt[kv.Key] = kv.Value.ToArray();
                    kv.Value.Clear();
                    if (_protPool.Count < 512) _protPool.Push(kv.Value);
                }
                _overProt.Clear();
            }

            Dictionary<int, int[]> frozenOverAlly = null;
            if (_overAlly.Count > 0)
            {
                frozenOverAlly = new Dictionary<int, int[]>(_overAlly.Count);
                foreach (var kv in _overAlly)
                {
                    frozenOverAlly[kv.Key] = kv.Value.ToArray();
                    kv.Value.Clear();
                    if (_allyPool.Count < 512) _allyPool.Push(kv.Value);
                }
                _overAlly.Clear();
            }

            var built = new Snapshot(relDict, rev, relCount, mutVer, protectors, allies, frozenOverProt, frozenOverAlly);
            _snapshot = built;
            return built;
        }
    }

    private void AddOverflowProt(int targetId, ProtectorEdge edge)
    {
        if (!_overProt.TryGetValue(targetId, out var list))
        {
            list = _protPool.Count > 0 ? _protPool.Pop() : new List<ProtectorEdge>(4);
            _overProt[targetId] = list;
        }
        list.Add(edge);
    }

    private void AddDenseOrOverAlly(int nationId, int allyId, int maxDense, List<int>[] allyLists)
    {
        if ((uint)nationId < (uint)maxDense)
        {
            var list = allyLists[nationId];
            if (list is null)
            {
                list = _allyPool.Count > 0 ? _allyPool.Pop() : new List<int>(4);
                allyLists[nationId] = list;
                _touchedAlly.Add(nationId);
            }
            list.Add(allyId);
            return;
        }
        if (!_overAlly.TryGetValue(nationId, out var over))
        {
            over = _allyPool.Count > 0 ? _allyPool.Pop() : new List<int>(4);
            _overAlly[nationId] = over;
        }
        over.Add(allyId);
    }

    /// <summary>
    /// Cache-neutral read for UI/preview queries: reuses an already-valid snapshot if present,
    /// otherwise scans live relations without priming <see cref="_snapshot"/>.
    /// </summary>
    public void FillProtectorIdsReadOnly(Campaign c, int targetId, List<int> into)
    {
        into.Clear();
        if (c is null || targetId < 0) return;
        var current = TryCurrent(c);
        if (current is not null)
        {
            ReadOnlySpan<ProtectorEdge> edges = ReadOnlySpan<ProtectorEdge>.Empty;
            if ((uint)targetId < (uint)current.ProtectorsByTarget.Length)
                edges = current.ProtectorsByTarget[targetId];
            else if (current.OverflowProtectors is not null && current.OverflowProtectors.TryGetValue(targetId, out var extra))
                edges = extra;
            for (var i = 0; i < edges.Length; i++) into.Add(edges[i].ProtectorId);
            return;
        }
        foreach (var r in c.Relations.Values)
        {
            if (r.A == targetId && (r.Stance == DiplomaticStance.Allied || r.GuaranteeByB))
                into.Add(r.B);
            else if (r.B == targetId && (r.Stance == DiplomaticStance.Allied || r.GuaranteeByA))
                into.Add(r.A);
        }
    }

    /// <summary>Every ally or guarantor standing behind <paramref name="targetId"/>.</summary>
    public ReadOnlySpan<ProtectorEdge> ProtectorsOf(Campaign c, int targetId)
    {
        if (c is null || targetId < 0) return ReadOnlySpan<ProtectorEdge>.Empty;
        var snap = Ensure(c);
        if ((uint)targetId < (uint)snap.ProtectorsByTarget.Length)
            return snap.ProtectorsByTarget[targetId];
        if (snap.OverflowProtectors is not null && snap.OverflowProtectors.TryGetValue(targetId, out var extra))
            return extra;
        return ReadOnlySpan<ProtectorEdge>.Empty;
    }

    /// <summary>Every formal ally of <paramref name="nationId"/>.</summary>
    public ReadOnlySpan<int> AlliesOf(Campaign c, int nationId)
    {
        if (c is null || nationId < 0) return ReadOnlySpan<int>.Empty;
        var snap = Ensure(c);
        if ((uint)nationId < (uint)snap.AlliesByNation.Length)
            return snap.AlliesByNation[nationId];
        if (snap.OverflowAllies is not null && snap.OverflowAllies.TryGetValue(nationId, out var extra))
            return extra;
        return ReadOnlySpan<int>.Empty;
    }

    /// <summary>Count of living allies and guarantors standing ready to defend <paramref name="targetId"/>.</summary>
    public int CountAliveDefenders(Campaign c, int targetId)
    {
        var edges = ProtectorsOf(c, targetId);
        var count = 0;
        for (var i = 0; i < edges.Length; i++)
        {
            var other = c.NationById(edges[i].ProtectorId);
            if (other is not null && other.Population > 0) count++;
        }
        return count;
    }

    /// <summary>Populate a daily friend-map snapshot from the current protector graph.</summary>
    public void CopyProtectorIdsInto(Campaign c, Dictionary<int, List<int>> into)
    {
        foreach (var list in into.Values) list.Clear();
        if (c is null) return;
        var snap = Ensure(c);
        var dense = snap.ProtectorsByTarget;
        for (var targetId = 0; targetId < dense.Length; targetId++)
        {
            var edges = dense[targetId];
            if (edges.Length == 0) continue;
            if (!into.TryGetValue(targetId, out var list)) into[targetId] = list = new List<int>(edges.Length);
            for (var i = 0; i < edges.Length; i++) list.Add(edges[i].ProtectorId);
        }
        if (snap.OverflowProtectors is not null)
        {
            foreach (var kv in snap.OverflowProtectors)
            {
                var edges = kv.Value;
                if (edges.Length == 0) continue;
                if (!into.TryGetValue(kv.Key, out var list)) into[kv.Key] = list = new List<int>(edges.Length);
                for (var i = 0; i < edges.Length; i++) list.Add(edges[i].ProtectorId);
            }
        }
    }
}
