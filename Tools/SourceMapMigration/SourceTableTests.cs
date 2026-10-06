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
    }
}
