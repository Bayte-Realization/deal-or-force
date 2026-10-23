using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Stable geography ids in editable JSON/YAML. Partial packages overlay rows by id.</summary>
public static class WorldMapTables
{
    public static string[] CountryRows { get; private set; } = Array.Empty<string>();
    public static string[] StateRows { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// The map's colour families, in the order the country table's mapColour column counts them.
    ///
    /// A country row carries a family index, and which shade of that family it gets is decided by
    /// its own id, so two neighbours in one family are related but not identical. The spectrum
    /// runs grey, red, orange, yellow, green, teal, blue, violet, magenta - which is why a map
    /// drawn with it reads as regions rather than as confetti.
    ///
    /// It was a table in the scenario builder, and this is where the rest of the map's tables
    /// live. A mod that wants a different map recolours it by shipping this file, the same way it
    /// moves a border.
    /// </summary>
    public static string[][] MapPalette { get; private set; } = Array.Empty<string[]>();
    static WorldMapTables() => ResetCatalog();
    public static void ResetCatalog()
    {
        CountryRows = StateRows = Array.Empty<string>();
        MapPalette = Array.Empty<string[]>();
        var path = GameDataLoader.FindDataFile("map_data", "source_tables.json");
        if (path is null) throw new InvalidDataException("Missing game/map_data/source_tables.json. Restore the external source tables; there is no compiled game-data fallback.");
        using var document = StructuredData.ReadDocument(path); Merge(document.RootElement);
    }
    public static void ApplyDirectory(string directory, List<string> errors)
    {
        foreach (var path in StructuredData.Files(directory).Where(p => Path.GetFileNameWithoutExtension(p) == "source_tables"))
            try { using var document = StructuredData.ReadDocument(path); Merge(document.RootElement, errors); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { errors?.Add(ex.Message); }
    }
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("source_tables: expected an object");
            var countries = CountryRows.ToDictionary(Id); var states = StateRows.ToDictionary(Id);
            void Rows(string key, int columns, Dictionary<int, string> destination)
            {
                if (!root.TryGetProperty(key, out var list)) return;
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > ushort.MaxValue)
                    throw new InvalidDataException($"source_tables.{key}: expected a bounded array");
                var seen = new HashSet<int>();
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != columns ||
                        item.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString().Contains('|') || v.GetString().Length > 512))
                        throw new InvalidDataException($"source_tables.{key}: each row requires {columns} string columns without '|'");
                    var cells = item.EnumerateArray().Select(v => v.GetString()).ToArray();
                    if (!int.TryParse(cells[0], out var id) || id <= 0 || id > ushort.MaxValue || !seen.Add(id))
                        throw new InvalidDataException($"source_tables.{key}: invalid or duplicate map id");
                    if (key == "countries" && (!long.TryParse(cells[5], out var population) || population < 0 ||
                        !float.TryParse(cells[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var gdp) ||
                        !float.IsFinite(gdp) || (gdp < 0 && gdp != -99)))
                        throw new InvalidDataException("source_tables.countries: invalid population or GDP");
                    destination[id] = string.Join('|', cells);
                }
            }
            Rows("countries", 10, countries); Rows("states", 5, states);
            // The map's colours, replaced whole rather than shade by shade. A palette is a
            // spectrum: one with three families swapped and eleven left is not a palette anybody
            // meant, and a mod wanting a different map means a different map. Stating the whole
            // list is fourteen short arrays, which is not a burden worth a partial-merge rule that
            // would surprise whoever used it.
            var palette = MapPalette;
            if (root.TryGetProperty("palette", out var families))
            {
                if (families.ValueKind != JsonValueKind.Array || families.GetArrayLength() == 0)
                    throw new InvalidDataException("source_tables.palette: expected a non-empty array of colour families");
                var built = new List<string[]>();
                foreach (var family in families.EnumerateArray())
                {
                    if (family.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("source_tables.palette: each family is an array of colours");
                    var shades = new List<string>();
                    foreach (var shade in family.EnumerateArray())
                    {
                        if (shade.ValueKind != JsonValueKind.String || !StructuredData.IsHexColour(shade.GetString()!))
                            throw new InvalidDataException("source_tables.palette: every shade has to be a #rrggbb colour");
                        shades.Add(shade.GetString()!);
                    }
                    if (shades.Count == 0)
                        throw new InvalidDataException("source_tables.palette: every family needs at least one colour");
                    built.Add(shades.ToArray());
                }
                palette = built.ToArray();
            }
            if (countries.Count == 0 || states.Count == 0) throw new InvalidDataException("source_tables: countries and states cannot be empty");
            if (palette.Length == 0) throw new InvalidDataException("source_tables: the palette cannot be empty");
            foreach (var row in states.Values)
                if (!int.TryParse(row.Split('|')[2], out var parent) || !countries.ContainsKey(parent))
                    throw new InvalidDataException("source_tables.states: unknown parent country id");
            CountryRows = countries.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
            StateRows = states.OrderBy(p => p.Key).Select(p => p.Value).ToArray();
            MapPalette = palette;
            TerritorialTopology.InvalidateSourceIndices();
        }
        catch (InvalidDataException ex) { if (errors is null) throw; errors.Add(ex.Message); }
    }
    private static int Id(string row) => int.Parse(row.AsSpan(0, row.IndexOf('|')), CultureInfo.InvariantCulture);
}
