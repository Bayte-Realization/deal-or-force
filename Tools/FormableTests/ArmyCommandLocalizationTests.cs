using System;
using System.IO;
using System.Text.Json;
using DealOrForce.Simulation;

static class ArmyCommandLocalizationTests
{
    public static void Run()
    {
        var previous = Loc.Language;
        Loc.ResetCatalog();
        try
        {
            Loc.Language = "russian";
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(),
                "localization", "russian", "140_army_commands.json")));
            var count = 0;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(Loc.TextIn("russian", entry.Name)) || Loc.Get(entry.Name) == entry.Name)
                    throw new Exception("Army translation missing or overridden: " + entry.Name);
                if (!Loc.Placeholders(entry.Name).SetEquals(Loc.Placeholders(entry.Value.GetString())))
                    throw new Exception("Army translation placeholders differ: " + entry.Name);
                if (string.IsNullOrWhiteSpace(Loc.TextIn("english", entry.Name)))
                    throw new Exception("Army source key missing from English index: " + entry.Name);
                count++;
            }
            Console.WriteLine($"PASS army localization: {count} commands, tabs and help texts resolve in Russian");
            Loc.Language = "english";
            if (Loc.Get("ORDERS") != "ORDERS" || Loc.Get("Eraser") != "Eraser")
                throw new Exception("English fallback changed");
            Console.WriteLine("PASS army localization: English fallback preserved");
        }
        finally { Loc.Language = previous; }
    }
}
