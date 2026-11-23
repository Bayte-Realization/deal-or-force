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
        public readonly int AllianceRevision;
        public readonly int RelationsCount;
        public readonly int MutationVersion;
        public readonly ProtectorEdge[][] ProtectorsByTarget;
        public readonly int[][] AlliesByNation;
        public readonly Dictionary<int, ProtectorEdge[]> OverflowProtectors;
        public readonly Dictionary<int, int[]> OverflowAllies;

        public Snapshot(
            int allianceRevision,
            int relationsCount,
            int mutationVersion,
            ProtectorEdge[][] protectorsByTarget,
            int[][] alliesByNation,
            Dictionary<int, ProtectorEdge[]> overflowProtectors,
            Dictionary<int, int[]> overflowAllies)
        {
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

    private Snapshot TryCurrent(Campaign c)
    {
        var current = _snapshot;
        if (current is not null &&
            current.AllianceRevision == c.AllianceRevision &&
            current.RelationsCount == c.Relations.Count &&
            current.MutationVersion == DiplomaticRelation.ProtectorMutationVersion)
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

            var rev = c.AllianceRevision;
            var relCount = c.Relations.Count;
            var mutVer = DiplomaticRelation.ProtectorMutationVersion;

            var maxDense = c.Nations.Count;
            foreach (var rel in c.Relations.Values)
            {
                if (rel.Stance != DiplomaticStance.Allied && !rel.GuaranteeByA && !rel.GuaranteeByB) continue;
                if (rel.A >= maxDense && rel.A < DenseLimit) maxDense = rel.A + 1;
                if (rel.B >= maxDense && rel.B < DenseLimit) maxDense = rel.B + 1;
            }

            var protLists = new List<ProtectorEdge>[maxDense];
            var allyLists = new List<int>[maxDense];
            Dictionary<int, List<ProtectorEdge>> overProt = null;
            Dictionary<int, List<int>> overAlly = null;

            foreach (var rel in c.Relations.Values)
            {
                var allied = rel.Stance == DiplomaticStance.Allied;
                var aProtectsB = allied || rel.GuaranteeByA;
                var bProtectsA = allied || rel.GuaranteeByB;
                if (!aProtectsB && !bProtectsA) continue;

                if (aProtectsB) AddProtector(rel.B, new ProtectorEdge(rel.A, rel), protLists, ref overProt);
                if (bProtectsA) AddProtector(rel.A, new ProtectorEdge(rel.B, rel), protLists, ref overProt);
                if (allied)
                {
                    AddAlly(rel.A, rel.B, allyLists, ref overAlly);
                    AddAlly(rel.B, rel.A, allyLists, ref overAlly);
                }
            }

            var protectors = new ProtectorEdge[maxDense][];
            for (var i = 0; i < maxDense; i++)
                protectors[i] = protLists[i] is { Count: > 0 } list ? list.ToArray() : Array.Empty<ProtectorEdge>();

            var allies = new int[maxDense][];
            for (var i = 0; i < maxDense; i++)
                allies[i] = allyLists[i] is { Count: > 0 } list ? list.ToArray() : Array.Empty<int>();

            Dictionary<int, ProtectorEdge[]> frozenOverProt = null;
            if (overProt is not null)
            {
                frozenOverProt = new Dictionary<int, ProtectorEdge[]>(overProt.Count);
                foreach (var kv in overProt) frozenOverProt[kv.Key] = kv.Value.ToArray();
            }

            Dictionary<int, int[]> frozenOverAlly = null;
            if (overAlly is not null)
            {
                frozenOverAlly = new Dictionary<int, int[]>(overAlly.Count);
                foreach (var kv in overAlly) frozenOverAlly[kv.Key] = kv.Value.ToArray();
            }

            var built = new Snapshot(rev, relCount, mutVer, protectors, allies, frozenOverProt, frozenOverAlly);
            _snapshot = built;
            return built;
        }
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

    private static void AddProtector(int targetId, ProtectorEdge edge, List<ProtectorEdge>[] dense,
        ref Dictionary<int, List<ProtectorEdge>> overflow)
    {
        if ((uint)targetId < (uint)dense.Length)
        {
            (dense[targetId] ??= new List<ProtectorEdge>(4)).Add(edge);
            return;
        }
        overflow ??= new Dictionary<int, List<ProtectorEdge>>();
        if (!overflow.TryGetValue(targetId, out var list)) overflow[targetId] = list = new List<ProtectorEdge>(4);
        list.Add(edge);
    }

    private static void AddAlly(int nationId, int allyId, List<int>[] dense,
        ref Dictionary<int, List<int>> overflow)
    {
        if ((uint)nationId < (uint)dense.Length)
        {
            (dense[nationId] ??= new List<int>(4)).Add(allyId);
            return;
        }
        overflow ??= new Dictionary<int, List<int>>();
        if (!overflow.TryGetValue(nationId, out var list)) overflow[nationId] = list = new List<int>(4);
        list.Add(allyId);
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
