using System;
using System.Linq;
using DealOrForce;
using DealOrForce.Simulation;

static class RasterRiverCourseTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS PNG river courses: " + message); }
    public static void Run()
    {
        const int width = 64, height = 32;
        var data = new byte[width * height * 2];
        // A three-pixel-wide bent main channel and its thin tributary.
        for (var x = 6; x <= 27; x++) for (var y = 8; y <= 10; y++) data[(y * width + x) * 2 + 1] = 225;
        for (var y = 8; y <= 25; y++) for (var x = 25; x <= 27; x++) data[(y * width + x) * 2 + 1] = 225;
        for (var x = 26; x <= 38; x++) data[(18 * width + x) * 2 + 1] = 96;
        var original = (byte[])data.Clone();
        var raster = new HydrologyRaster(width, height, data, 0, 0, "fixture PNG", true);
        var features = RasterRiverCourses.Extract(raster);
        Check(data.SequenceEqual(original), "render extraction never changes authoritative crossing bytes");
        Check(features.Count >= 3 && features.All(f => f.Parts.All(p => p.Length >= 2)), "bends and tributary trace into finite paths");
        var field = HydrologySurfaceData.BuildCentreline(width, height, features);
        Check(field[(9 * width + 15) * 4 + 2] > 0 && field[(22 * width + 26) * 4 + 2] > 0,
            "both sides of the authored bend reach the visible centreline atlas");
        Check(field[(18 * width + 36) * 4 + 2] > 0, "thin tributary remains visible");
        Check(field[(30 * width + 58) * 4 + 2] == 0, "empty PNG ground does not inherit an unrelated JSON river");
        var empty = new HydrologyRaster(width, height, new byte[data.Length], 0, 0, "empty PNG", true);
        Check(RasterRiverCourses.Extract(empty).Count == 0, "an intentionally empty river PNG stays empty");
        var seam = new byte[data.Length];
        foreach (var x in new[] { 62, 63, 0, 1 }) seam[(16 * width + x) * 2 + 1] = 160;
        var joined = RasterRiverCourses.Extract(new HydrologyRaster(width, height, seam, 0, 0));
        Check(joined.Count == 1 && joined[0].Parts[0].Max(p => p.Longitude) - joined[0].Parts[0].Min(p => p.Longitude) < 30,
            "date-line course unwraps locally instead of drawing across the world");
        Check(SaveSystem.FormatVersion == 2, "render-only reconstruction does not change save format");
    }
}
