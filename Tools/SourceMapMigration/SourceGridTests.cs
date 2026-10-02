using System;
using System.Collections.Generic;
using System.IO;
using DealOrForce.Simulation;
using Map = Globality.EarthData;

static class SourceGridTests
{
    public static void Run()
    {
        MapRasterCatalog.ResetCatalog();
        Check(File.Exists(MapRasterCatalog.SourceControlPath) && File.Exists(MapRasterCatalog.SourceStatesPath) &&
            File.Exists(MapRasterCatalog.SourceDisputesPath), "all vanilla source layers resolve to external files");
        Check(Map.Control.AsSpan().SequenceEqual(Map.Unpack(Map.PackedControl, Map.CellCount)), "every country id preserved");
        Check(Map.States.AsSpan().SequenceEqual(Map.Unpack(Map.PackedStates, Map.CellCount)), "every region id preserved");
        var disputes = SourceMapGrid.Load(MapRasterCatalog.SourceDisputesPath, Map.Width, Map.Height);
        Check(MapRasterCatalog.RegionFingerprint(disputes) == 1727779104982673975UL, "all disputed cells preserved against export fingerprint");
        var folder = Path.Combine(Path.GetTempPath(), "dof-source-grid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var states = Map.States;
            var cell = Map.CellIndex(100, 100);
            var original = states[cell];
            var rgb = new byte[Map.CellCount * 3];
            for (var i = 0; i < states.Length; i++)
            {
                rgb[i * 3] = (byte)states[i];
                rgb[i * 3 + 1] = (byte)(states[i] >> 8);
            }
            var replacement = original == 1 ? (ushort)2 : (ushort)1;
            rgb[cell * 3] = (byte)replacement;
            rgb[cell * 3 + 1] = (byte)(replacement >> 8);
            var path = Path.Combine(folder, "source_states.png");
            DataMapPng.SaveRgb(path, Map.Width, Map.Height, rgb);
            var errors = new List<string>();
            MapRasterCatalog.ApplyDirectory(folder, errors);
            Check(errors.Count == 0 && Map.States[cell] == replacement && !ReferenceEquals(states, Map.States),
                "mod layer overrides region ids and invalidates decoded caches");
            Check(Map.Control.AsSpan().SequenceEqual(Map.Unpack(Map.PackedControl, Map.CellCount)),
                "partial override inherits the unchanged country layer");
            MapRasterCatalog.ResetCatalog();
            Check(Map.States[cell] == original, "removing mod restores vanilla source ids");
            var basePath = MapRasterCatalog.SourceStatesPath;
            var cached = Map.States;
            DataMapPng.SaveRgb(path, 1, 1, new byte[3]);
            errors.Clear();
            MapRasterCatalog.ApplyDirectory(folder, errors);
            Check(errors.Count == 1 && MapRasterCatalog.SourceStatesPath == basePath && ReferenceEquals(cached, Map.States),
                "wrong dimensions rejected without replacing a working layer/cache");
            rgb[cell * 3 + 2] = 1;
            DataMapPng.SaveRgb(path, Map.Width, Map.Height, rgb);
            errors.Clear();
            MapRasterCatalog.ApplyDirectory(folder, errors);
            Check(errors.Count == 1 && MapRasterCatalog.SourceStatesPath == basePath,
                "invalid colour encoding rejected without silently changing ids");
            var emptyFolder = Path.Combine(folder, "empty-disputes");
            Array.Clear(rgb);
            DataMapPng.SaveRgb(Path.Combine(emptyFolder, "source_disputes.png"), Map.Width, Map.Height, rgb);
            errors.Clear();
            MapRasterCatalog.ApplyDirectory(emptyFolder, errors);
            var emptyValue = Map.DisputeAt(0);
            var cacheField = typeof(Map).GetField("_disputeCells",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var emptyCache = cacheField.GetValue(null);
            Check(errors.Count == 0 && emptyValue == 0 && Map.DisputeAt(Map.CellCount - 1) == 0 &&
                ReferenceEquals(emptyCache, cacheField.GetValue(null)),
                "empty disputes layer is valid and decoded only once");
        }
        finally
        {
            MapRasterCatalog.ResetCatalog();
            var target = Path.GetFullPath(folder);
            var expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(expected, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith("dof-source-grid-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(target, true);
        }
    }

    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL " + description);
        Console.WriteLine("PASS " + description);
    }
}
