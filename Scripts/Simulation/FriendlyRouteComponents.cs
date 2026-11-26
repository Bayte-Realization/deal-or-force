using System;
using System.Buffers;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>A short-lived negative reachability filter. Different weak components cannot
/// have a directed route. Same components still need the ordinary forward BFS, so even a
/// mod's asymmetric graph cannot turn this cheap filter into a false path or a false refusal.</summary>
internal sealed class FriendlyRouteComponents : IDisposable
{
    private int[] _parent;
    private int _length;

    internal FriendlyRouteComponents(ProvinceTopology map, ushort[] controllers, IReadOnlySet<ushort> allowed) =>
        Rebuild(map, controllers, allowed);

    internal void Rebuild(ProvinceTopology map, ushort[] controllers, IReadOnlySet<ushort> allowed)
    {
        var nextLength = map?.Neighbors?.Length ?? 0;
        if (nextLength == 0 || controllers is null || controllers.Length < nextLength || allowed is null)
        {
            _length = 0;
            return;
        }
        using var measured = SimProfiler.Measure("          disengage:components");
        if (_parent is null || _parent.Length < nextLength)
        {
            var old = _parent;
            _parent = null;
            if (old is not null) ArrayPool<int>.Shared.Return(old);
            _parent = ArrayPool<int>.Shared.Rent(nextLength);
        }
        _length = nextLength;
        var parent = _parent;
        Array.Clear(parent, 0, nextLength);
        for (var p = 1; p < nextLength; p++)
            if (!map.IsWater(p) && allowed.Contains(controllers[p])) parent[p] = p;
        for (var p = 1; p < nextLength; p++)
        {
            if (parent[p] == 0) continue;
            foreach (var next in map.Neighbors[p])
            {
                if (next <= 0 || next >= nextLength || parent[next] == 0) continue;
                var a = Root(parent, p); var b = Root(parent, next);
                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }
        for (var p = 1; p < nextLength; p++)
            if (parent[p] != 0) parent[p] = Root(parent, p);
    }

    private static int Root(int[] parent, int p)
    {
        while (parent[p] != p) { parent[p] = parent[parent[p]]; p = parent[p]; }
        return p;
    }

    internal bool MayReach(int from, int to) => _parent is not null && from > 0 && from < _length &&
        to > 0 && to < _length && _parent[from] != 0 && _parent[from] == _parent[to];

    public void Dispose()
    {
        var parent = _parent; _parent = null;
        if (parent is not null) ArrayPool<int>.Shared.Return(parent);
    }
}
