using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// What a country IS: its name, the colour it is painted on the map, and where it is governed from.
///
/// These live in common/countries, and the folder is not history: what a country is and what it
/// had in the year the bookmark opens are edited by different people for different reasons, and a
/// total conversion usually wants one of them without the other. Because the two folders are both
/// called "countries", both keyed by tag and both JSON, the separation is easy to get wrong and
/// silent when you do - so it is checked here rather than trusted.
///
/// The file is compared against the whole world, not sampled: a colour that a mis-copy changed in
/// a country nobody looks at is exactly the kind of wrong this is meant to catch.
/// </summary>
static class CountryDefinitionTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("country definition: " + label); Console.WriteLine("PASS country definition: " + label); }

    static string Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261016);
        var path = Path.Combine(GameDataLoader.DataFolder("common") ?? "", "countries", "00_countries.json");
        Check(File.Exists(path), "what a country is lives in a file, not in the compiler");
        if (!File.Exists(path)) return;

        using var document = JsonDocument.Parse(File.ReadAllText(path), GameDataLoader.ForgivingJson);
        var named = new List<string>();
        var mismatched = new List<string>();
        var backedByCatalogue = new List<string>();

        // The keys this folder is allowed to carry, written out by hand as the contract rather than
        // copied from the code: a field added to CountryDefinitions without this line being updated
        // should stop the suite and make somebody say what the field is for. A key nothing reads is
        // invisible from the modder's side - no error, no effect, no clue - which is how a colour
        // ended up written into history/countries twice in one week.
        var accepted = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "color", "capital_name", "capital_latitude", "capital_longitude"
        };
        var strangers = new List<string>();

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            named.Add(entry.Name);
            foreach (var key in entry.Value.EnumerateObject())
                if (!accepted.Contains(key.Name)) strangers.Add($"{entry.Name}.{key.Name}");
            var nation = c.NationByTag(entry.Name);
            if (nation is null) continue;
            if (Text(entry.Value, "name") is { Length: > 0 } name && nation.Name != name)
                mismatched.Add($"{entry.Name} name '{nation.Name}' != '{name}'");
            if (Text(entry.Value, "color") is { Length: > 0 } colour &&
                !string.Equals(nation.Color, colour, StringComparison.OrdinalIgnoreCase))
                mismatched.Add($"{entry.Name} colour '{nation.Color}' != '{colour}'");
            if (Text(entry.Value, "capital_name") is { Length: > 0 } capital && nation.CapitalName != capital)
                mismatched.Add($"{entry.Name} capital '{nation.CapitalName}' != '{capital}'");
            // Named but not seated: the definitions give it a colour and no city, so whatever the
            // compiled catalogue had is still what the world opens with.
            if (Text(entry.Value, "capital_name") is null && nation.CapitalName.Length > 0)
                backedByCatalogue.Add(entry.Name);
        }

        Check(named.Count > 200, $"the definitions name the world's countries ({named.Count} of them)");
        var unknown = named.Where(tag => c.NationByTag(tag) is null).ToList();
        Check(unknown.Count == 0, "every country the definitions name is a country in the world" +
            (unknown.Count > 0 ? $": unknown {string.Join(", ", unknown.Take(8))}" : ""));
        Check(mismatched.Count == 0, "and every name, colour and capital in it is what the world opens with" +
            (mismatched.Count > 0 ? $": {string.Join("; ", mismatched.Take(8))}" : ""));
        Check(backedByCatalogue.Count > 0,
            $"a country with no city in the definitions keeps the catalogue's ({backedByCatalogue.FirstOrDefault()})");
        Check(strangers.Count == 0, "and no country in it carries a key nothing reads" +
            (strangers.Count > 0 ? $": {string.Join(", ", strangers.Take(8))}" : ""));

        // Spot values, read off the shipped file by eye rather than by the test, so that a file
        // which agrees with itself but not with the game is still caught.
        void Is(string tag, string name, string colour, string capital)
        {
            var nation = c.NationByTag(tag);
            Check(nation is not null && nation.Name == name &&
                string.Equals(nation.Color, colour, StringComparison.OrdinalIgnoreCase) &&
                nation.CapitalName == capital,
                $"{tag} is {name}, painted {colour}, governed from {capital}");
        }
        Is("KOR", "South Korea", "#5a9bc9", "Seoul");
        Is("CHN", "China", "#d9b32e", "Beijing");
        Is("GBR", "United Kingdom", "#8a2f42", "London");
        Is("PMR", "Transnistria", "#a84655", "Tiraspol");

        // ---- a document of the kind a mod ships, laid over the world
        var afg = c.NationByTag("AFG");
        if (afg is null) return;
        var catalogueLatitude = afg.CapitalLatitude;
        var catalogueLongitude = afg.CapitalLongitude;
        var catalogueColour = afg.Color;
        Check(catalogueLatitude != 0f || catalogueLongitude != 0f,
            $"the catalogue seats {afg.Tag} at a point to be moved ({catalogueLatitude}, {catalogueLongitude})");

        void Apply(string json)
        {
            using var layer = JsonDocument.Parse(json);
            CountryDefinitions.Apply(c, layer.RootElement);
        }

        Apply("{ \"AFG\": { \"name\": \"Testland\" } }");
        Check(afg.Name == "Testland", "a document renames a country");
        Check(afg.Color == catalogueColour, "and saying only a name leaves the colour alone");

        // A rename is the whole job for most mods, and it must not cost the country its seat: the
        // point decides which region the government sits in, and dropping it would move the
        // capital to the Gulf of Guinea.
        Apply("{ \"AFG\": { \"capital_name\": \"Testville\" } }");
        Check(afg.CapitalName == "Testville", "a document renames the capital");
        Check(afg.CapitalLatitude == catalogueLatitude && afg.CapitalLongitude == catalogueLongitude,
            "and leaves the point exactly where the catalogue had it");

        // A move is the job for a mod laying out a different map, and it has no name to keep.
        Apply("{ \"AFG\": { \"capital_latitude\": 12.5, \"capital_longitude\": -30.25 } }");
        Check(afg.CapitalLatitude == 12.5f && afg.CapitalLongitude == -30.25f, "a document moves the capital");
        Check(afg.CapitalName == "Testville", "and a move on its own leaves the name alone");

        // A point in orbit is brought back to the surface: the seat is the region nearest the
        // point, and no region is nearest a point in orbit, so the country would lose its seat.
        Apply("{ \"AFG\": { \"capital_latitude\": 999, \"capital_longitude\": -999 } }");
        Check(afg.CapitalLatitude == 90f && afg.CapitalLongitude == -180f,
            $"a point off the planet is clamped to it ({afg.CapitalLatitude}, {afg.CapitalLongitude})");

        // Something that is not a number is not a coordinate, and is ignored rather than guessed.
        Apply("{ \"AFG\": { \"capital_latitude\": \"north\" } }");
        Check(afg.CapitalLatitude == 90f, "and one that is not a number is left alone rather than read as zero");

        // An empty name is not a name: the country keeps the one it had.
        Apply("{ \"AFG\": { \"capital_name\": \"\" } }");
        Check(afg.CapitalName == "Testville", "an empty name changes nothing");

        // A document about a country that does not exist is not a crash.
        Apply("{ \"ZZZ\": { \"name\": \"Nowhere\" } }");
        Check(c.NationByTag("ZZZ") is null, "a document naming no country in the world changes nothing");
    }
}
