using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// The country editor's writer: a country edited as a whole is written into a mod folder as the
/// ordinary data files, read back to the same values, and - the part that matters - read by the
/// game's own loaders (CountryDefinitions, HistoryDataLoader) to a country with those values.
/// A writer that only agreed with its own reader would prove nothing about the game.
/// </summary>
static class CountryEditorTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("country editor: " + label);
        Console.WriteLine("PASS country editor: " + label);
    }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20261101);
        var nation = campaign.NationByTag("LUX") ?? campaign.Nations.First(n => n.Tag.Length == 3);
        var originalCultures = DemographyCatalog.PrimaryCultures(nation.Tag).ToList();
        var document = CountryEditorDocument.FromNation(nation, new[] { "english", "russian" });
        Check(document.Tag == nation.Tag && document.Government == nation.Government && document.Names.Count == 2,
            "a document starts from the country as the world has it, in each language asked for");

        var government = GovernmentCatalog.All.First(g => g.Id != nation.Government).Id;
        var ideology = IdeologyCatalog.All.First(i => i.Id != nation.Ideology).Id;
        var cultures = DemographyCatalog.Cultures.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(2).ToList();
        document.Names["english"] = ("Grand Duchy of Testland", "Testlandic");
        document.Names["russian"] = ("Великое герцогство Тестландия", "тестландский");
        document.Colour = "#12AB34";
        document.CapitalName = "Testburg";
        document.CapitalLatitude = 49.5f;
        document.CapitalLongitude = 6.125f;
        document.Government = government;
        document.Ideology = ideology;
        document.HeadOfState = "Head Of-State";
        document.HeadOfGovernment = "Head «Government»";
        document.PrimaryCultures = cultures;
        document.Treasury = 12.345f;
        document.Ownership.Add((1, nation.Tag));
        document.Ownership.Add((2, nation.Tag));
        Check(document.Problems().Count == 0, "the edited document has nothing wrong with it");

        var root = Path.Combine(Path.GetTempPath(), "ClaudeK_country_editor_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var written = document.Write(root);
            var stem = "90_editor_" + nation.Tag.ToLowerInvariant();
            Check(written.Count == 5 && written.All(File.Exists), "five files written: definition, history, two languages, provinces");
            Check(File.Exists(Path.Combine(root, "common", "countries", stem + ".json")) &&
                  File.Exists(Path.Combine(root, "history", "countries", stem + ".json")) &&
                  File.Exists(Path.Combine(root, "localization", "russian", stem + ".json")) &&
                  File.Exists(Path.Combine(root, "history", "provinces", stem + ".csv")),
                "each value in the folder the base game reads it from");
            Check(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(f => !f.StartsWith(root, StringComparison.OrdinalIgnoreCase)),
                "and nothing outside the mod folder");

            // ---- read back by the editor ----
            var back = CountryEditorDocument.Read(root, nation.Tag);
            Check(back is not null, "the editor finds what it wrote");
            Check(back.Names["english"] == document.Names["english"] && back.Names["russian"] == document.Names["russian"],
                "names and adjectives come back in both languages, Cyrillic intact");
            Check(back.Colour == "#12ab34" && back.CapitalName == "Testburg" && back.CapitalLatitude == 49.5f && back.CapitalLongitude == 6.125f,
                "colour (normalised), capital and its coordinates come back");
            Check(back.Government == government && back.Ideology == ideology && back.HeadOfState == document.HeadOfState &&
                  back.HeadOfGovernment == document.HeadOfGovernment, "politics and leaders come back");
            Check(back.PrimaryCultures.SequenceEqual(cultures) && back.Treasury == 12.345f, "cultures and the exact treasury come back");
            Check(back.Ownership.SequenceEqual(document.Ownership), "the regions' provinces come back");

            // ---- read by the game's own loaders ----
            using (var definition = StructuredData.ReadDocument(Path.Combine(root, "common", "countries", stem + ".json")))
                CountryDefinitions.Apply(campaign, definition.RootElement);
            var errors = new List<string>();
            HistoryDataLoader.ApplyCountries(campaign, StructuredData.ReadJson(Path.Combine(root, "history", "countries", stem + ".json")), errors);
            Check(errors.Count == 0, "the history loader accepts the file without a complaint" + (errors.Count > 0 ? ": " + errors[0] : ""));
            Check(nation.Name == "Grand Duchy of Testland" && nation.Color == "#12ab34" && nation.CapitalName == "Testburg" &&
                  nation.CapitalLatitude == 49.5f && nation.CapitalLongitude == 6.125f,
                "common/countries loads to the edited name, colour, capital and coordinates");
            Check(nation.Government == government && nation.Ideology == ideology && nation.Treasury == 12.345f &&
                  nation.HeadOfStateName == document.HeadOfState && nation.HeadOfGovernmentName == document.HeadOfGovernment,
                "history/countries loads to the edited politics, leaders and treasury");
            Check(DemographyCatalog.PrimaryCultures(nation.Tag).SequenceEqual(cultures), "and to the edited primary cultures");
            using (var russian = StructuredData.ReadDocument(Path.Combine(root, "localization", "russian", stem + ".json")))
                Check(russian.RootElement.GetProperty($"country.{nation.Tag}.name").GetString() == "Великое герцогство Тестландия" &&
                      russian.RootElement.GetProperty($"country.{nation.Tag}.adjective").GetString() == "тестландский",
                    "the localisation rows use the keys the game names countries by");
            Check(ContentWorkbench.Validate($"history/provinces/{stem}.csv", File.ReadAllText(Path.Combine(root, "history", "provinces", stem + ".csv")))
                    .StartsWith("Province table valid", StringComparison.Ordinal), "the province table is in the political map's format");

            // ---- saving again ----
            document.Treasury = 20f;
            document.Ownership.Clear();
            document.Write(root);
            Check(CountryEditorDocument.Read(root, nation.Tag).Treasury == 20f, "a second save replaces the first");
            Check(File.Exists(Path.Combine(root, "history", "countries", stem + ".json.bak")), "keeping the previous version as .bak");
            Check(!File.Exists(Path.Combine(root, "history", "provinces", stem + ".csv")),
                "and a region change taken back does not linger in the political map");

            // ---- what it refuses ----
            var bad = CountryEditorDocument.FromNation(nation, new[] { "english" });
            bad.Colour = "blue";
            bad.Government = "no_such_government";
            var problems = bad.Problems();
            Check(problems.Any(p => p.Contains("colour", StringComparison.OrdinalIgnoreCase)) &&
                  problems.Any(p => p.Contains("no_such_government", StringComparison.Ordinal)),
                "a bad colour and an unknown government are named");
            var refused = false;
            try { bad.Write(root); } catch (InvalidDataException) { refused = true; }
            Check(refused, "and nothing is written while they stand");
        }
        finally
        {
            DemographyCatalog.Declare(nation.Tag, originalCultures);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
