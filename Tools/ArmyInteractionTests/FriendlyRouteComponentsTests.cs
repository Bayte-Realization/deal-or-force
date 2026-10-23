using System;
using System.Buffers;
using System.Collections.Generic;
using DealOrForce.Simulation;

static class FriendlyRouteComponentsTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL route components: " + label);
        Console.WriteLine("PASS route components: " + label);
    }

    public static void Run()
    {
        foreach (var water in new[] { false, true })
        foreach (var directed in new[] { false, true })
        foreach (var coalition in new[] { false, true })
        {
            var map = FriendlyRouteSearchTests.Map(water: water, isolated: true);
            if (directed) { map.Neighbors[1] = Array.Empty<int>(); map.Neighbors[5] = new[] { 2 }; }
            var controls = (ushort[])map.ProvinceToNation.Clone(); controls[6] = 501;
            var allowed = new HashSet<ushort> { 500 }; if (coalition) allowed.Add(501);
            using var partition = new FriendlyRouteComponents(map, controls, allowed);
            var exact = true;
            for (var from = 1; from <= 8; from++) for (var to = 1; to <= 8; to++)
            {
                var route = map.FindOperationalRoute(from, to, allowed, controls);
                if (route.Length > 0 && !partition.MayReach(from, to)) exact = false;
            }
            Check(exact, $"never rejects a real forward route: water={water}, directed={directed}, coalition={coalition}");
            Check(!partition.MayReach(1, 8) && !partition.MayReach(8, 1), "disconnected island is refused in either direction");
            Check(!partition.MayReach(-1, 2) && !partition.MayReach(1, 0) && !partition.MayReach(1, int.MaxValue), "invalid indices are inert");
            if (water) Check(!partition.MayReach(4, 4), "water cannot join a component");
            if (!coalition) Check(!partition.MayReach(6, 6), "neutral land does not join the access graph");
        }
        var fixture = FriendlyRouteSearchTests.Map(isolated: true);
        var layer = fixture.ProvinceToNation; var own = new HashSet<ushort> { 500 };
        for (var i = 0; i < 24; i++)
        {
            var dirty = ArrayPool<int>.Shared.Rent(fixture.Neighbors.Length);
            Array.Fill(dirty, int.MaxValue); ArrayPool<int>.Shared.Return(dirty);
            var partition = new FriendlyRouteComponents(fixture, layer, own);
            Check(partition.MayReach(1, 7) && !partition.MayReach(1, 8), "dirty pool rental has no stale connections " + i);
            partition.Dispose(); partition.Dispose();
            Check(!partition.MayReach(1, 7), "disposed partition cannot read pooled memory " + i);
        }
        using var missing = new FriendlyRouteComponents(null, layer, own);
        using var shortLayer = new FriendlyRouteComponents(fixture, new ushort[1], own);
        using var missingAccess = new FriendlyRouteComponents(fixture, layer, null);
        Check(!missing.MayReach(1, 2) && !shortLayer.MayReach(1, 2) && !missingAccess.MayReach(1, 2), "missing inputs are inert");
    }
}
