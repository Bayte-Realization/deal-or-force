using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class SourceTableTests
{
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAIL " + label); Console.WriteLine("PASS " + label); }
    static void Merge(object content, List<string> errors = null)
    { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(content)); WorldMapTables.Merge(doc.RootElement, errors); }
    public static void Run()
    {
        WorldMapTables.ResetCatalog();
        var countries = WorldMapTables.CountryRows.ToArray();
        var states = WorldMapTables.StateRows.ToArray();
        Check(countries.SequenceEqual(EarthData.CountryRows), "external country rows preserve every original id and value");
        Check(states.SequenceEqual(EarthData.StateRows), "external administrative rows preserve every original id and value");
        var modifiedCountry = countries.First(row => row.Split('|')[1] == "KOR").Split('|');
        modifiedCountry[6] = "2300000";
        Merge(new { countries = new[] { modifiedCountry } });
        Check(WorldMapTables.CountryRows.Length == countries.Length && WorldMapTables.StateRows.SequenceEqual(states),
            "partial country overlay inherits other countries and all administrative regions");
        var campaign = Campaign.CreateBootstrap(20261006);
        Check(campaign.NationByTag("KOR").Gdp > 2200, "scenario imports editable GDP without a hardcoded Korea checksum");
        var state = states[0].Split('|');
        var yamlCells = state.Select(value => JsonSerializer.Serialize(value)).ToArray();
        yamlCells[1] = JsonSerializer.Serialize("Регион моддера");
        using (var yaml = JsonDocument.Parse(StructuredData.YamlToJson("states:\n  - [" + string.Join(", ", yamlCells) + "]")))
            WorldMapTables.Merge(yaml.RootElement);
        Check(WorldMapTables.StateRows[0].Split('|')[1] == "Регион моддера" &&
            WorldMapTables.StateRows.Length == states.Length, "YAML overlay changes one region without discarding the world");
        var goodCountries = WorldMapTables.CountryRows;
        var goodStates = WorldMapTables.StateRows;
        void Reject(object content, string label)
        {
            var errors = new List<string>(); Merge(content, errors);
            Check(errors.Count == 1 && ReferenceEquals(goodCountries, WorldMapTables.CountryRows) &&
                ReferenceEquals(goodStates, WorldMapTables.StateRows), label);
        }
        Reject(new { states = new[] { state, state } }, "duplicate ids reject the entire overlay atomically");
        var badParent = state.ToArray(); badParent[2] = "65535";
        Reject(new { countries = new[] { modifiedCountry }, states = new[] { badParent } },
            "unknown parent cannot partially replace countries before validation fails");
        var badCountry = modifiedCountry.ToArray(); badCountry[6] = "NaN";
        Reject(new { countries = new[] { badCountry } }, "non-finite GDP is rejected");
        badCountry[6] = "-1";
        Reject(new { countries = new[] { badCountry } }, "negative GDP is rejected");
        var badName = state.ToArray(); badName[1] = "broken|column";
        Reject(new { states = new[] { badName } }, "embedded separators cannot corrupt a region row");
        Reject(new { states = new[] { new[] { "1", "missing columns" } } }, "wrong column count is rejected");
        WorldMapTables.ResetCatalog();
        Check(WorldMapTables.CountryRows.SequenceEqual(countries) && WorldMapTables.StateRows.SequenceEqual(states),
            "catalog reset restores external vanilla data, not compiled fallback");

        // ---- the map's colours, which were the last of the world's tables to be spoken in code
        //
        // Spot values are transcribed by hand from the fourteen families that were deleted from the
        // scenario builder. Hand-transcribed on purpose: a test whose expectations came out of the
        // same file it is checking would agree with any typo in it, and a wrong colour on a map is
        // not an error anybody gets told about.
        Check(WorldMapTables.MapPalette.Length == 14, "the palette carries its fourteen colour families");
        Check(WorldMapTables.MapPalette[0].Length == 1 && WorldMapTables.MapPalette[0][0] == "#949494",
            "the grey family is still the one shade a country with no family gets");
        Check(WorldMapTables.MapPalette[6][0] == "#32814c", "the green family starts where it always did");
        Check(WorldMapTables.MapPalette[13][0] == "#b33262", "and the magenta family still ends the spectrum");
        Check(WorldMapTables.MapPalette.All(family => family.Length is > 0 and <= 4),
            "every family is one to four shades, which is what the id hash picks a shade from");

        static bool SamePalette(string[][] left, string[][] right) =>
            left.Length == right.Length && left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

        var shipped = WorldMapTables.MapPalette;
        var shippedErrors = new List<string>();
        Merge(new { palette = new[] { new[] { "#000000" }, new[] { "#ffffff" } } }, shippedErrors);
        Check(shippedErrors.Count == 0 && WorldMapTables.MapPalette.Length == 2 &&
            WorldMapTables.MapPalette[1][0] == "#ffffff", "a shipped palette replaces the whole spectrum");
        var held = WorldMapTables.MapPalette;
        var shadeErrors = new List<string>();
        Merge(new { palette = new[] { new[] { "not a colour" } } }, shadeErrors);
        Check(shadeErrors.Count == 1 && ReferenceEquals(held, WorldMapTables.MapPalette),
            "a shade that is not a colour rejects the whole palette and leaves the old one standing");
        var shortErrors = new List<string>();
        Merge(new { palette = new[] { new[] { "#12345" } } }, shortErrors);
        Check(shortErrors.Count == 1, "a shade that is the wrong length is refused too");
        var shapeErrors = new List<string>();
        Merge(new { palette = new object[] { new[] { "#ffffff" }, "not an array" } }, shapeErrors);
        Check(shapeErrors.Count == 1, "a family that is not an array is refused");
        WorldMapTables.ResetCatalog();
        Check(SamePalette(shipped, WorldMapTables.MapPalette),
            "and resetting the catalog brings the shipped spectrum back, shade for shade");
    }
}
