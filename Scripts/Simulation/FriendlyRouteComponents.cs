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
    private readonly int _length;

    internal FriendlyRouteComponents(ProvinceTopology map, ushort[] controllers, IReadOnlySet<ushort> allowed)
    {
        _length = map?.Neighbors?.Length ?? 0;
        if (_length == 0 || controllers is null || controllers.Length < _length || allowed is null) return;
        using var measured = SimProfiler.Measure("          disengage:components");
        var parent = ArrayPool<int>.Shared.Rent(_length);
        try
        {
            Array.Clear(parent, 0, _length);
            for (var p = 1; p < _length; p++)
                if (!map.IsWater(p) && allowed.Contains(controllers[p])) parent[p] = p;
            for (var p = 1; p < _length; p++)
            {
                if (parent[p] == 0) continue;
                foreach (var next in map.Neighbors[p])
                {
                    if (next <= 0 || next >= _length || parent[next] == 0) continue;
                    var a = Root(parent, p); var b = Root(parent, next);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
            }
            for (var p = 1; p < _length; p++)
                if (parent[p] != 0) parent[p] = Root(parent, p);
            _parent = parent;
        }
        catch { ArrayPool<int>.Shared.Return(parent); throw; }
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
