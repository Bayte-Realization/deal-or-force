using System;
using System.IO;
using DealOrForce.Simulation;

/// <summary>
/// The one game folder: the layout, the first-run migration, and the promise that running it
/// twice is not a way to lose a save.
///
/// These checks exist because a path is the one thing that fails silently. A save written three
/// folders away from the settings file looks like nothing at all until a player cannot find
/// their campaign, and a migration that copies in the wrong direction looks like nothing until
/// somebody's quicksave is gone. Both are cheap to prove here.
/// </summary>
public static class GameFolderTests
{
    private static int _pass, _fail;

    public static void Run()
    {
        _pass = _fail = 0;
        var sandbox = Path.Combine(Path.GetTempPath(), "dof-folders-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Layout(sandbox);
            CreatesAndMigrates(sandbox);
            NeverOverwrites(sandbox);
            LeavesItsOwnRootAlone(sandbox);
            NoDocumentsMeansNoLayout();
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch (Exception) { }
        }
        Console.WriteLine();
        Console.WriteLine($"game-folders: {_pass} PASS, {_fail} FAIL");
        if (_fail > 0) Environment.Exit(1);
    }

    private static void Check(bool ok, string label)
    {
        if (ok) { _pass++; Console.WriteLine("PASS " + label); }
        else { _fail++; Console.WriteLine("FAIL " + label); }
    }

    private static void Layout(string sandbox)
    {
        var documents = Path.Combine(sandbox, "Documents");
        Directory.CreateDirectory(documents);
        var folders = GameFolders.For(documents);
        Check(folders is not null, "a documents folder yields a layout");
        var expected = Path.GetFullPath(Path.Combine(documents, "Bayte Realization", "Deal or Force"));
        Check(folders!.Root == expected, "the root is Documents/Bayte Realization/Deal or Force");
        Check(folders.Saves == Path.Combine(expected, "saves"), "saves live in saves/");
        Check(folders.SettingsFile == Path.Combine(expected, "settings", "settings.json"),
            "settings live in settings/settings.json");
        Check(folders.Logs == Path.Combine(expected, "logs"), "logs live in logs/");
        Check(folders.Screenshots == Path.Combine(expected, "screenshots"), "screenshots live in screenshots/");
        // One root, said once: the folders must not escape it, whatever they are called.
        foreach (var path in new[] { folders.Saves, folders.Settings, folders.Logs, folders.Mods, folders.Dlc })
            Check(path.StartsWith(expected, StringComparison.OrdinalIgnoreCase), "inside the root: " + Path.GetFileName(path));
    }

    private static void NoDocumentsMeansNoLayout()
    {
        Check(GameFolders.For(null) is null && GameFolders.For("") is null && GameFolders.For("   ") is null,
            "a system that will not name a documents folder gets no layout, and the caller falls back");
    }

    private static void CreatesAndMigrates(string sandbox)
    {
        var documents = Path.Combine(sandbox, "Documents2");
        var engine = Path.Combine(sandbox, "AppData", "Deal or Force");
        var legacyDocuments = Path.Combine(documents, "Deal or Force");
        Directory.CreateDirectory(documents);
        Directory.CreateDirectory(Path.Combine(engine, "save_games"));
        Directory.CreateDirectory(Path.Combine(engine, "logs"));
        Directory.CreateDirectory(Path.Combine(legacyDocuments, "save games"));
        File.WriteAllText(Path.Combine(engine, "settings.json"), "{\"language\":\"russian\"}");
        File.WriteAllText(Path.Combine(engine, "save_games", "quicksave.dof"), "quick");
        File.WriteAllText(Path.Combine(engine, "logs", "godot.log"), "engine log");
        File.WriteAllText(Path.Combine(legacyDocuments, "save games", "campaign.dof"), "campaign");

        var folders = GameFolders.For(documents)!;
        Check(folders.Prepare(engine, legacyDocuments), "the folders are created for a first run");

        Check(Directory.Exists(folders.Saves) && Directory.Exists(folders.Settings) &&
              Directory.Exists(folders.Logs) && Directory.Exists(folders.Screenshots),
            "saves, settings, logs and screenshots exist");
        Check(File.Exists(Path.Combine(folders.Root, "README.txt")), "the folder explains itself in README.txt");
        Check(File.Exists(folders.SettingsFile), "the settings file moves into settings/");
        Check(File.ReadAllText(folders.SettingsFile).Contains("russian"), "and it is the file the player had");
        Check(File.Exists(Path.Combine(folders.Saves, "quicksave.dof")), "engine-era saves land in saves/");
        Check(File.Exists(Path.Combine(folders.Saves, "campaign.dof")), "Documents-era saves land in saves/ too");
        Check(File.Exists(Path.Combine(folders.Logs, "godot.log")), "the engine log joins the folder's logs");
        Check(!Directory.Exists(legacyDocuments), "the old Documents/Deal or Force folder is gone once empty");

        // Second run: nothing copied twice, nothing reported as moved.
        Check(folders.Prepare(engine, legacyDocuments), "a second prepare succeeds");
        Check(folders.Moved.Count == 0, "a second prepare copies nothing (idempotent)");
        Check(folders.Skipped.Count == 0, "and reports nothing skipped");
    }

    private static void NeverOverwrites(string sandbox)
    {
        var documents = Path.Combine(sandbox, "Documents3");
        var engine = Path.Combine(sandbox, "AppData3");
        Directory.CreateDirectory(documents);
        Directory.CreateDirectory(engine);
        var folders = GameFolders.For(documents)!;
        folders.Prepare();
        File.WriteAllText(folders.SettingsFile, "{\"language\":\"english\"}");
        File.WriteAllText(Path.Combine(engine, "settings.json"), "{\"language\":\"russian\"}");

        folders.Prepare(engine);
        Check(File.ReadAllText(folders.SettingsFile).Contains("english"),
            "a settings file already in the game folder is the newer decision and is kept");
        Check(folders.Skipped.Count > 0, "the file left behind is reported rather than silently ignored");
    }

    private static void LeavesItsOwnRootAlone(string sandbox)
    {
        var documents = Path.Combine(sandbox, "Documents4");
        Directory.CreateDirectory(documents);
        var folders = GameFolders.For(documents)!;
        folders.Prepare();
        Check(folders.EngineSharesRoot(folders.Root),
            "when user:// is the game folder - the junction case - the script knows they are one place");
        File.WriteAllText(Path.Combine(folders.Logs, "godot.log"), "kept");
        folders.Prepare(folders.Root, null);
        Check(File.Exists(Path.Combine(folders.Logs, "godot.log")),
            "preparing with the engine folder equal to the root neither copies nor deletes");
        Check(folders.Moved.Count == 0, "and it moves nothing onto itself");
    }

}
