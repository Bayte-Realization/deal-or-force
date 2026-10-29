using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The rasterised polygon mask against the per-cell ray test it replaced.
///
/// `PaintRegion` used to ask "is this cell inside the region's polygon?" for every cell of the
/// rule's box, walking every edge of the polygon each time - which is where the minutes of
/// campaign loading went. It now rasterises the polygon once per box. That is a reordered
/// computation, not an approximation, and this proves it over every rule in the shipped
/// catalogue: same float crossings, same comparisons, same answer for every cell.
/// </summary>
static class MapPolygonMaskTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS " + label);
    }

    public static void Run()
    {
        TerritorialTopology.ResetCatalog();
        // The traced contours live in the map's region overrides, not in the base rules: 5 rules
        // and 2 936 cells without them, which would prove nothing about the real map.
        var folder = System.IO.Path.Combine(GameDataLoader.DataFolder(), "map_data", "state_regions");
        var errors = new System.Collections.Generic.List<string>();
        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.json").OrderBy(path => path))
            TerritorialTopology.LoadOverrides(System.IO.File.ReadAllText(file), errors);
        Check(errors.Count == 0, "the region overrides load without complaints");
        var (mismatches, cells, rules) =
            TerritorialTopology.VerifyPolygonMasks(WorldMapSource.Width, WorldMapSource.Height);
        Console.WriteLine($"  {rules} polygon rules, {cells:N0} cells compared");
        Check(rules >= 5, "the catalogue has the traced region polygons");
        Check(mismatches == 0, "the rasterised mask agrees with the per-cell test on every cell");
    }
}
