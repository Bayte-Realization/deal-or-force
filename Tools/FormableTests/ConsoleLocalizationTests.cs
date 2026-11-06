using System;
using System.IO;
using DealOrForce.Simulation;

static class ConsoleLocalizationTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception(text); Console.WriteLine("PASS console localization: " + text); }
    public static void Run()
    {
        var language = Loc.Language;
        var root = Path.Combine(Path.GetTempPath(), "dof-console-loc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Loc.Language = "english"; var english = Loc.Get("console.touchgrass.verse");
            Loc.Language = "russian"; var russian = Loc.Get("console.touchgrass.verse");
            Check(english.Contains("Outside, there is grass") && russian.Contains("Она живая"), "both verses use the normal language tables");
            Loc.Language = "missing_language";
            Check(Loc.Get("console.touchgrass.verse") == english, "a missing translation falls back to English");
            var folder = Path.Combine(root, "english"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "90_console.yml"), "console.touchgrass.verse: A modded verse\n");
            Loc.LoadDirectory(root, new()); Loc.Language = "english";
            Check(Loc.Get("console.touchgrass.verse") == "A modded verse", "a mod overrides the text through a YAML localization file");
        }
        finally { Loc.ResetCatalog(); Loc.Language = language; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
