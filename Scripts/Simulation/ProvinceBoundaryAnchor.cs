using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class ProvinceBoundaryAnchor
{
    private readonly ProvinceTopology _map;
    private readonly Dictionary<(int Target, int Source), (float Latitude, float Longitude)?> _anchors = new();
    public ProvinceBoundaryAnchor(ProvinceTopology map) => _map = map ?? throw new ArgumentNullException(nameof(map));

    /// <summary>One actual shared raster edge, nearest the defender's centre, stable across camera changes.</summary>
    public bool TryFind(int target, int source, out (float Latitude, float Longitude) anchor)
    {
        anchor = default;
        if (target <= 0 || source <= 0 || target == source || target > _map.ProvinceCount || source > _map.ProvinceCount)
            return false;
        var key = (target, source);
        if (!_anchors.TryGetValue(key, out var saved)) _anchors[key] = saved = Find(target, source);
        if (saved is not { } point) return false;
        anchor = point;
        return true;
    }

    private (float Latitude, float Longitude)? Find(int target, int source)
    {
        var start = _map.ProvinceCenterCell[target];
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>(); queue.Enqueue(start);
        var cx = start % _map.Width + .5f;
        var cy = start / _map.Width + .5f;
        var best = float.MaxValue;
        var bx = 0f; var by = 0f;
        while (queue.TryDequeue(out var cell))
        {
            var x = cell % _map.Width; var y = cell / _map.Width;
            Visit(y * _map.Width + (x + 1) % _map.Width, x + 1f, y + .5f);
            Visit(y * _map.Width + (x - 1 + _map.Width) % _map.Width, x, y + .5f);
            if (y > 0) Visit(cell - _map.Width, x + .5f, y);
            if (y + 1 < _map.Height) Visit(cell + _map.Width, x + .5f, y + 1f);
        }
        return best == float.MaxValue ? null :
            (90f - by * 180f / _map.Height, -180f + (bx % _map.Width) * 360f / _map.Width);

        void Visit(int other, float edgeX, float edgeY)
        {
            var province = _map.CellToProvince[other];
            if (province == target) { if (seen.Add(other)) queue.Enqueue(other); return; }
            if (province != source) return;
            var dx = MathF.Abs(edgeX - cx); dx = MathF.Min(dx, _map.Width - dx);
            var dy = edgeY - cy;
            var score = dx * dx + dy * dy;
            if (score >= best) return;
            best = score; bx = edgeX; by = edgeY;
        }
    }
}
