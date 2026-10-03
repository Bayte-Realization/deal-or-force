using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class FleetNameLocalizationTests
{
    public static void Run()
    {
        var previous = Loc.Language;
        Loc.ResetCatalog();
        try
        {
            using var history = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(),
                "history", "units", "2024", "01_fleets.json")), GameDataLoader.ForgivingJson);
            var names = history.RootElement.GetProperty("fleets").EnumerateObject()
                .SelectMany(country => country.Value.EnumerateArray())
                .Select(fleet => fleet.GetProperty("name").GetString()).Distinct().ToArray();
            if (names.Length == 0) throw new Exception("Fleet localization test has no authored fleets");
            Loc.Language = "russian";
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(Loc.TextIn("russian", name)) || Loc.Get(name) == name)
                    throw new Exception("Authored fleet is untranslated in Russian: " + name);
                if (Loc.TextIn("english", name) != name)
                    throw new Exception("Authored fleet English name changed: " + name);
                if (!Loc.Placeholders(name).SetEquals(Loc.Placeholders(Loc.Get(name))))
                    throw new Exception("Fleet localization placeholders differ: " + name);
            }
            Console.WriteLine($"PASS fleet localization: all {names.Length} distinct authored names resolve in Russian");
            Loc.Language = "english";
            foreach (var name in names)
                if (Loc.Get(name) != name) throw new Exception("English fleet fallback changed: " + name);
            const string custom = "My player's custom fleet 123";
            foreach (var language in new[] { "english", "russian" })
            {
                Loc.Language = language;
                if (Loc.Get(custom) != custom) throw new Exception("Custom fleet name was rewritten");
            }
            Console.WriteLine("PASS fleet localization: English and player-defined names preserved");
        }
        finally { Loc.Language = previous; }
    }
}
