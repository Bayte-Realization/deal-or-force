using System;
using System.IO;
using DealOrForce.Simulation;

static class PresentationReloadTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        Console.WriteLine("PASS reload: " + message);
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "dof-reload-" + Guid.NewGuid());
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        var language = Loc.Language;
        Directory.CreateDirectory(Path.Combine(first, "gui"));
        Directory.CreateDirectory(Path.Combine(second, "gui"));
        Directory.CreateDirectory(Path.Combine(second, "localization", "english"));
        try
        {
            File.WriteAllText(Path.Combine(first, "gui", "skin.json"), "{\"metrics\":{\"reload.test\":17}}");
            var skin = Path.Combine(second, "gui", "skin.json");
            File.WriteAllText(skin, "{\"metrics\":{\"reload.test\":29}}");
            File.WriteAllText(Path.Combine(second, "localization", "english", "text.json"),
                "{\"reload.test\":\"Updated translation\"}");
            Loc.Language = "english";
            var errors = PresentationReload.Reload(new[] { first, second });
            Check(errors.Count == 0 && InterfaceSkin.Metric("reload.test", 0) == 29,
                "active packages override in order");
            Check(Loc.Get("reload.test") == "Updated translation", "translation reread from disk");
            File.WriteAllText(skin, "{\"metrics\":{\"reload.test\":31}}");
            PresentationReload.Reload(new[] { first, second });
            Check(InterfaceSkin.Metric("reload.test", 0) == 31, "edited file replaces cached value");
            File.Delete(skin);
            PresentationReload.Reload(new[] { first, second });
            Check(InterfaceSkin.Metric("reload.test", 0) == 17, "removed override falls back to previous package");
            File.WriteAllText(skin, "{");
            errors = PresentationReload.Reload(new[] { first, second });
            Check(errors.Count > 0, "malformed file is reported");
            Check(Loc.Language == "english", "reload preserves selected language");
        }
        finally
        {
            Directory.Delete(root, true);
            InterfaceSkin.ResetCatalog();
            Loc.ResetCatalog();
            Loc.Language = language;
        }
    }
}
