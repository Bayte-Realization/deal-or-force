using System;
using DealOrForce;

internal static class MapUsabilityTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS map usability: " + message);
    }

    internal static void Run()
    {
        var near = MapPanScale.OrbitRadiansPerPixel(5.25f, 5, 38, 1080);
        var far = MapPanScale.OrbitRadiansPerPixel(15, 5, 38, 1080);
        Check(Math.Abs(far / near - 40) < .001f, "globe dragging scales with altitude above the ground");
        Check(Math.Abs(MapPanScale.OrbitRadiansPerPixel(15, 5, 38, 2160) / far - .5f) < .0001f,
            "the same screen fraction pans equally at double resolution");
        Check(Math.Abs(MapPanScale.FlatUnitsPerPixel(4, 1080) / MapPanScale.FlatUnitsPerPixel(.1f, 1080) - 40) < .001f,
            "flat dragging keeps proportional tactical zoom sensitivity");
        Check(float.IsFinite(MapPanScale.OrbitRadiansPerPixel(5, 5, 38, 0)), "zero-size startup viewport is safe");

        const int w = 160, h = 160;
        var map = new ushort[w * h];
        void Fill(int left, int top, int width, int height, ushort owner)
        { for (var y = top; y < top + height; y++) for (var x = left; x < left + width; x++) map[y * w + x] = owner; }
        Fill(40, 80, 16, 25, 1);   // South Korea before unification, independent northern neighbour.
        Fill(40, 15, 16, 65, 2);
        var before = TerritoryLabelLayout.BuildTerritoryLabelAnchors(map, w, h)[1];
        Fill(40, 15, 16, 65, 1);
        var united = TerritoryLabelLayout.BuildTerritoryLabelAnchors(map, w, h)[1];
        Check(united.CenterY < before.CenterY - 15 && united.Cells == 16 * 90,
            "annexation recentres the name on the united ownership footprint");
        Check(Math.Abs(united.AxisY) > .8f, "a narrow north-south peninsula can carry a north-south name");
        AssertInside(map, w, h, united);

        Array.Clear(map);
        Fill(8, 8, 10, 10, 1); Fill(70, 60, 20, 20, 1);
        var full = TerritoryLabelLayout.BuildTerritoryLabelAnchors(map, w, h)[1];
        var scaled = TerritoryLabelLayout.BuildTerritoryLabelAnchors(map, w, h, 2)[1];
        Check(full.CenterX > 60 && scaled.CenterX > 120 && scaled.Cells == full.Cells * 4,
            "post-annexation downsampling still selects the largest connected landmass");

        Array.Clear(map);
        Fill(65, 30, 45, 12, 1); Fill(65, 42, 12, 70, 1);
        var coast = TerritoryLabelLayout.BuildTerritoryLabelAnchors(map, w, h)[1];
        AssertInside(map, w, h, coast);
        Check(coast.ThicknessPixels < 30, "a coastal baseline cannot borrow all its height from just one side");
    }

    private static void AssertInside(ushort[] map, int width, int height, TerritoryLabelLayout.TerritoryLabelAnchor anchor)
    {
        for (var i = -10; i <= 10; i++)
        {
            var along = anchor.HalfSpanPixels * i / 10;
            var x = (int)Math.Round(anchor.CenterX + anchor.AxisX * along);
            var y = (int)Math.Round(anchor.CenterY + anchor.AxisY * along);
            Check(x >= 0 && x < width && y >= 0 && y < height && map[y * width + x] == 1,
                "baseline remains on owned land at sample " + i);
        }
    }
}
