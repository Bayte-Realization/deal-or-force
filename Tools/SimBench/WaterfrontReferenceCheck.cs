using System;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Oracle for the geography-only coast scan: the pre-optimization algorithm.</summary>
public static class WaterfrontReferenceCheck
{
    public static int Run(Campaign campaign)
    {
        var checkedMaps = 0;
        var checkedProvinces = 0;
        void Compare(ProvinceTopology map, string label)
        {
            var expected = OriginalClassification(map);
            for (var province = 0; province <= map.ProvinceCount; province++)
            {
                var actual = Waterfront.OfProvince(map, province);
                if (actual != expected[province])
                    throw new InvalidOperationException($"{label}: province {province}, expected {expected[province]}, got {actual}");
                checkedProvinces++;
            }
            if (Waterfront.OfProvince(map, -1) != WaterAccess.None ||
                Waterfront.OfProvince(map, map.ProvinceCount + 1) != WaterAccess.None)
                throw new InvalidOperationException(label + ": invalid province was not dry");
            checkedMaps++;
        }

        // Every sea/land mask up to 3x3 includes date-line, pole, single-column,
        // single-row and diagonal-only touches. No RNG or platform-specific data.
        for (var width = 1; width <= 3; width++)
        for (var height = 1; height <= 3; height++)
        for (var mask = 0; mask < 1 << (width * height); mask++)
        {
            var states = new ushort[width * height];
            var owners = new ushort[states.Length];
            for (var cell = 0; cell < states.Length; cell++)
                if ((mask & (1 << cell)) != 0)
                {
                    states[cell] = (ushort)(1 + cell % 2);
                    owners[cell] = 1;
                }
            Compare(ProvinceTopology.Build(states, owners, width, height, _ => false),
                $"{width}x{height} mask {mask}");
        }

        // Larger repeated provinces and explicit inland-water flags exercise the skip
        // rule for water provinces plus the priority Sea > river/lake > None.
        var random = new Random(8137);
        ProvinceTopology remembered = null;
        for (var sample = 0; sample < 100; sample++)
        {
            var width = 1 + random.Next(22);
            var height = 1 + random.Next(14);
            var states = new ushort[width * height];
            var owners = new ushort[states.Length];
            for (var cell = 0; cell < states.Length; cell++)
                if (random.Next(5) != 0)
                {
                    states[cell] = (ushort)(1 + random.Next(4));
                    owners[cell] = 1;
                }
            var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                map.WaterByProvince[province] = province % 5 == 0;
                map.LakeAccessByProvince[province] = province % 3 == 0;
                map.RiverIntensityByProvince[province] = province % 2 == 0 ? (byte)80 : (byte)0;
            }
            Compare(map, $"random {sample}");
            if (remembered is not null) Compare(remembered, $"cached {sample}");
            remembered = map;
        }
        Compare(campaign.ProvinceMap, "shipped world");
        Console.WriteLine($"PASS Waterfront reference: {checkedMaps:N0} maps and {checkedProvinces:N0} province classifications; wrapped date-line, no polar wrap, inland flags, campaign switches and shipped raster.");
        return 0;
    }

    private static WaterAccess[] OriginalClassification(ProvinceTopology map)
    {
        var access = new WaterAccess[map.ProvinceCount + 1];
        var cells = map.CellToProvince;
        var width = map.Width;
        var height = map.Height;
        bool Open(int x, int y) => y >= 0 && y < height && cells[y * width + x] == 0;
        for (var cell = 0; cell < cells.Length; cell++)
        {
            var province = cells[cell];
            if (province <= 0 || province >= access.Length ||
                access[province] == WaterAccess.Sea || map.WaterByProvince[province]) continue;
            var x = cell % width;
            var y = cell / width;
            if (Open((x + 1) % width, y) || Open((x - 1 + width) % width, y) ||
                Open(x, y - 1) || Open(x, y + 1)) access[province] = WaterAccess.Sea;
        }
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (access[province] != WaterAccess.Sea &&
                (map.HasLakeAccess(province) || map.HasRiver(province))) access[province] = WaterAccess.Inland;
        return access;
    }
}
