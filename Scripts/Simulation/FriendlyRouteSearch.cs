using System;
using System.Buffers;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>A forward, incremental shortest-path tree for one fixed origin and access set.
/// Its borrowed topology/controllers must remain unchanged during this short read-only
/// selection window. Unlike a reverse flood, it preserves the legacy BFS's neighbour-order
/// tie breaks exactly. It owns no campaign state and returns its two scratch arrays on disposal.</summary>
internal sealed class FriendlyRouteSearch : IDisposable
{
    private readonly ProvinceTopology _map;
    private readonly ushort[] _controllers;
    private readonly HashSet<ushort> _allowed;
    private readonly int _origin;
    private readonly int _length;
    private int[] _previous;
    private int[] _queue;
    private int _head;
    private int _tail;
    private bool _disposed;

    internal FriendlyRouteSearch(ProvinceTopology map, ushort[] controllers, int startProvince,
        IReadOnlySet<ushort> allowedControllers)
    {
        _map = map; _controllers = controllers; _origin = startProvince;
        _length = map?.Neighbors?.Length ?? 0;
        _allowed = allowedControllers is null ? null : new HashSet<ushort>(allowedControllers);
        if (!Valid(startProvince) || _allowed is null || !_allowed.Contains(controllers[startProvince])) return;

        // A pool rental is not zero-initialized. Zero means unseen, while origin points to
        // itself; clear precisely the index range this topology can read before first use.
        var previous = ArrayPool<int>.Shared.Rent(_length);
        try
        {
            Array.Clear(previous, 0, _length);
            _queue = ArrayPool<int>.Shared.Rent(_length);
            _previous = previous;
        }
        catch
        {
            ArrayPool<int>.Shared.Return(previous);
            throw;
        }
        _previous[startProvince] = startProvince;
        _queue[_tail++] = startProvince;
    }

    private bool Valid(int province) => _map is not null && _controllers is not null &&
        _controllers.Length >= _length && province > 0 && province < _length && !_map.IsWater(province);

    /// <summary>Return the exact legacy forward-BFS route, excluding the origin. searched
    /// reports actual queue expansion, not a lookup or an invalid/unreachable cached request.</summary>
    internal int[] Route(int targetProvince, out bool searched)
    {
        searched = false;
        if (_disposed || _previous is null || !Valid(targetProvince) ||
            !_allowed.Contains(_controllers[targetProvince]) || targetProvince == _origin)
            return Array.Empty<int>();

        if (_previous[targetProvince] == 0 && _head < _tail)
        {
            searched = true;
            using var measured = SimProfiler.Measure("          disengage:bfs");
            while (_head < _tail && _previous[targetProvince] == 0)
            {
                var current = _queue[_head++];
                // Finish the WHOLE expansion even when target is discovered mid-loop. The
                // legacy finder stops at the next outer condition, not at the first edge.
                foreach (var next in _map.Neighbors[current])
                {
                    if (next <= 0 || next >= _length || _previous[next] != 0 ||
                        _map.IsWater(next) || !_allowed.Contains(_controllers[next])) continue;
                    _previous[next] = current;
                    _queue[_tail++] = next;
                }
            }
        }
        if (_previous[targetProvince] == 0) return Array.Empty<int>();

        var count = 0;
        for (var cursor = targetProvince; cursor != _origin; cursor = _previous[cursor]) count++;
        var route = new int[count];
        for (var cursor = targetProvince; cursor != _origin; cursor = _previous[cursor])
            route[--count] = cursor;
        return route;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var previous = _previous; var queue = _queue;
        _previous = null; _queue = null;
        // These are integer graph indices, not references. No caller reads queue slots
        // beyond head/tail, and every future previous rental clears its effective range.
        if (previous is not null) ArrayPool<int>.Shared.Return(previous);
        if (queue is not null) ArrayPool<int>.Shared.Return(queue);
    }
}
