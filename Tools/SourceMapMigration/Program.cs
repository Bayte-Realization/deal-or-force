using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using Map = Globality.EarthData;
using System.Text.Json;

// Mechanical, lossless conversion of the former compiled map to mod-authorable PNGs.
// Existing outputs are never overwritten. Run from the project root with --export.
if (args.Length == 1 && args[0] == "--verify") { SourceGridTests.Run(); SourceTableTests.Run(); return; }
if (args.Length == 1 && args[0] == "--export-tables")
{
    var tableRoot = Path.GetFullPath("game/map_data");
    var destination = Path.Combine(tableRoot, "source_tables.json");
    if (!File.Exists(Path.Combine(tableRoot, "default.map"))) throw new Exception("Run from the project root.");
    if (File.Exists(destination)) throw new IOException("Refusing to overwrite existing source tables.");
    var tableData = new { country_columns = "index|iso|name|continent|region|population|gdpMillions|mapColour|type|adm0a3",
        state_columns = "index|name|countryIndex|isoCode|type",
        countries = EarthData.CountryRows.Select(row => row.Split('|')).ToArray(),
        states = EarthData.StateRows.Select(row => row.Split('|')).ToArray() };
    File.WriteAllText(destination, JsonSerializer.Serialize(tableData, new JsonSerializerOptions { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine($"Exported {tableData.countries.Length} countries and {tableData.states.Length} regions without changing ids.");
    return;
}
if (args.Length != 1 || args[0] != "--export")
    throw new ArgumentException("Usage: dotnet run --project Tools/SourceMapMigration -- --export|--export-tables|--verify");
var root = Path.GetFullPath("game/map_data");
if (!File.Exists(Path.Combine(root, "default.map"))) throw new Exception("Run from the project root.");
var names = new[] { "source_control.png", "source_states.png", "source_disputes.png" };
foreach (var name in names)
    if (File.Exists(Path.Combine(root, name))) throw new IOException($"Refusing to overwrite {name}.");
for (var layer = 0; layer < names.Length; layer++)
{
    var original = layer switch
    {
        0 => Map.Unpack(Map.PackedControl, Map.CellCount),
        1 => Map.Unpack(Map.PackedStates, Map.CellCount),
        _ => Enumerable.Range(0, Map.CellCount).Select(Map.DisputeAt).ToArray()
    };
    var rgb = new byte[Map.CellCount * 3];
    for (var cell = 0; cell < original.Length; cell++)
    {
        rgb[cell * 3] = (byte)original[cell];
        rgb[cell * 3 + 1] = (byte)(original[cell] >> 8);
    }
    var path = Path.Combine(root, names[layer]);
    DataMapPng.SaveRgb(path, Map.Width, Map.Height, rgb);
    var restored = SourceMapGrid.Load(path, Map.Width, Map.Height);
    if (!original.AsSpan().SequenceEqual(restored)) throw new Exception($"Lossy migration: {names[layer]}");
    Console.WriteLine($"PASS {names[layer]}: {original.Length:N0} identical ids, fingerprint {MapRasterCatalog.RegionFingerprint(restored)}, {new FileInfo(path).Length:N0} bytes.");
}
