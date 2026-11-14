using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// The far country line is drawn from a distance field, and the field says where the frontier
/// is by the same rule as everything else on the map.
///
/// Pins: the distance is half a cell on the frontier and grows by about a cell a cell; a frontier
/// moved by a treaty (owners differing from the authored owners) moves the field with it; the sea
/// is a holder like any other, so a coast is a frontier; east and west wrap; the smoothing rounds
/// a one-cell staircase off rather than following it; and the shader reads the field only where
/// it is ready, while the Compatibility build, which has no texture unit to spare, never does.
/// Run: FormableTests --frontier-field-only
/// </summary>
static class FrontierFieldTests
{
    private static int _passed;

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("frontier field: " + label);
        _passed++;
        Console.WriteLine("PASS frontier field: " + label);
    }

    public static void Run()
    {
        _passed = 0;
        Distances();
        Owners();
        Smoothing();
        Shader();
        Console.WriteLine($"frontier field: {_passed} PASS");
    }

    // Two countries, 1 west and 2 east of column 10, on a 32 by 12 world. No provinces moved.
    private static (ushort[] Holders, ushort[] Provinces) Split(int width, int height, int column)
    {
        var holders = new ushort[width * height];
        var provinces = new ushort[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                holders[y * width + x] = (ushort)(x < column ? 1 : 2);
                provinces[y * width + x] = (ushort)(x < column ? 1 : 2);
            }
        return (holders, provinces);
    }

    private static void Distances()
    {
        const int width = 32, height = 12;
        var (holders, _) = Split(width, height, 10);
        var d = FrontierField.Distances(holders, width, height);
        float At(int x, int y) => d[y * width + x];
        Check(Math.Abs(At(9, 5) - 0.5f) < 1e-4 && Math.Abs(At(10, 5) - 0.5f) < 1e-4,
            "a cell on either side of the frontier is half a cell from it");
        Check(Math.Abs(At(13, 5) - 3.5f) < 1e-4 && Math.Abs(At(6, 5) - 3.5f) < 1e-4,
            "three cells further on it is three and a half");
        Check(Math.Abs(At(20, 5) - FrontierField.Cap) < 1e-4, "far inland it holds the cap");
        // Column 31 is east of column 0: the world wraps, so the western country's west edge is a
        // frontier with the eastern country too.
        Check(Math.Abs(At(0, 5) - 0.5f) < 1e-4 && Math.Abs(At(31, 5) - 0.5f) < 1e-4,
            "east and west wrap: the last column meets the first");
        var sea = new ushort[width * height];
        Array.Fill(sea, (ushort)1);
        for (var x = 0; x < width; x++) sea[x] = MapBorders.Sea;
        var coast = FrontierField.Distances(sea, width, height);
        Check(Math.Abs(coast[width + 7] - 0.5f) < 1e-4, "the sea is a holder: a coast is a frontier");
    }

    private static void Owners()
    {
        const int width = 32, height = 12;
        var (holders, provinces) = Split(width, height, 10);
        // Province 2 (the east) passes from country 2 to country 1 at a peace: no frontier left
        // between the two halves, only the wrap.
        var authored = new ushort[] { 0, 1, 2 };
        var owners = new ushort[] { 0, 1, 1 };
        var before = FrontierField.Build(holders, provinces, width, height, authored, authored);
        var after = FrontierField.Build(holders, provinces, width, height, owners, authored);
        Check(before[5 * width + 10] < 40, "on the authored frontier the field is near nought");
        Check(after[5 * width + 10] == 255 && after[5 * width + 0] == 255,
            "once one country holds both halves, the frontier is gone from the field");
        Check(FrontierField.Build(holders, provinces, width, height, authored, authored).SequenceEqual(before),
            "the same owners give the same bytes");
    }

    private static void Smoothing()
    {
        // A frontier that runs at a shallow angle, digitised as a staircase: three cells along,
        // one down. Unsmoothed, the half-cell contour follows every step; smoothed, the cells a
        // step pokes out of the line and the cells beside it read nearly alike.
        const int width = 48, height = 24;
        var holders = new ushort[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                holders[y * width + x] = (ushort)(y < 6 + x / 3 ? 1 : 2);
        var raw = FrontierField.Distances(holders, width, height);
        var smooth = (float[])raw.Clone();
        FrontierField.Smooth(smooth, width, height, FrontierField.Smoothing);
        // Where a stroke would end: in each column, the height at which the field, read upwards
        // from the frontier on the northern side, rises through 1.5 cells. Along a straight
        // frontier those heights lie on a straight line; along a staircase they step. How far
        // they stray from the straight line through them is the staircase that would be drawn.
        float Stray(float[] field)
        {
            var xs = new System.Collections.Generic.List<float>();
            var ys = new System.Collections.Generic.List<float>();
            for (var x = 12; x < 36; x++)
            {
                var frontier = 6 + x / 3;
                for (var y = frontier - 1; y > 0; y--)
                {
                    var here = field[y * width + x];
                    var above = field[(y - 1) * width + x];
                    if (here < 1.5f && above >= 1.5f)
                    {
                        xs.Add(x);
                        ys.Add(y - (1.5f - here) / (above - here));
                        break;
                    }
                }
            }
            var n = xs.Count;
            var mx = xs.Average(); var my = ys.Average();
            var slope = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / xs.Sum(x => (x - mx) * (x - mx));
            return (float)Math.Sqrt(xs.Zip(ys, (x, y) => Math.Pow(y - (my + slope * (x - mx)), 2)).Sum() / n);
        }
        var rawStray = Stray(raw);
        var smoothStray = Stray(smooth);
        Console.WriteLine($"  staircase at 1.5 cells: {rawStray:0.000} cells raw, {smoothStray:0.000} smoothed");
        Check(smoothStray < rawStray * 0.6f,
            "the smoothing rounds a staircase off: the stroke's edge strays less than half as far from straight");
        Check(smooth.All(v => v >= 0f && v <= FrontierField.Cap + 1e-3f), "smoothing keeps the field inside its range");
    }

    private static void Shader()
    {
        var scripts = Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts");
        var globe = File.ReadAllText(Path.Combine(scripts, "gfx", "GlobePresentation.cs"));
        var plate = File.ReadAllText(Path.Combine(scripts, "gfx", "GlobePlateRepaint.cs"));
        Check(Regex.IsMatch(globe, @"uniform\s+sampler2D\s+frontier_field\s*:\s*filter_linear_mipmap") &&
              globe.Contains("uniform float frontier_ready = 0.0;"),
            "the map declares a filtered, mipmapped field that starts switched off");
        Check(globe.Contains("float frontier_weight = frontier_ready * smoothstep(") &&
              globe.Contains("nation_edge_pixels = mix(nation_edge_pixels, frontier_pixels, frontier_weight);"),
            "the far stroke and everything drawn off the frontier distance read the field by the same weight");
        Check(plate.Contains("Simulation.FrontierField.Build(authored, topology.CellToProvince, width, height, owners, authoredOwners)") &&
              plate.Contains("FrontierImage(frontierAuthored, topology, owners, authoredOwners)") &&
              plate.Contains("ApplyFrontier(done.Result.Frontier);"),
            "every plate repaint builds the field from the same owners and swaps both in together");
        Check(globe.Contains("ApplyFrontier(result.Frontier);"), "the opening map uploads the field with the plate");
        var start = globe.IndexOf("Code = @\"shader_type spatial;", StringComparison.Ordinal) + "Code = @\"".Length;
        var end = globe.IndexOf("\n}\"", start, StringComparison.Ordinal);
        var packed = PackedMapShader.Build(MapColorSpaceShader.Build(globe[start..(end + 2)]));
        Check(!packed.Contains("frontier_field,") && !Regex.IsMatch(packed, @"uniform\s+sampler2D\s+frontier_field") &&
              Regex.Matches(packed, @"uniform\s+sampler\w+\s+").Count == PackedMapShader.TextureUnits,
            "the Compatibility build drops the field and keeps its twelve texture units");
    }
}
