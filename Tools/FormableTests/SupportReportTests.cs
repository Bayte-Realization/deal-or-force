using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The bug report a player hands over. What matters is not that a zip appears but that it holds
/// what a stranger needs to reproduce a problem - and that a missing folder, a locked log or an
/// empty install still produces a file instead of a second bug.
/// </summary>
public static class SupportReportTests
{
    private static int _pass, _fail;

    public static void Run()
    {
        _pass = _fail = 0;
        var sandbox = Path.Combine(Path.GetTempPath(), "dof-report-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(sandbox);
            CollectsWhatIsNeeded(sandbox);
            SurvivesMissingPieces(sandbox);
            RefusesWithoutAFolder(sandbox);
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch (Exception) { }
        }
        Console.WriteLine();
        Console.WriteLine($"support-report: {_pass} PASS, {_fail} FAIL");
        if (_fail > 0) Environment.Exit(1);
    }

    private static void Check(bool ok, string label)
    {
        if (ok) { _pass++; Console.WriteLine("PASS " + label); }
        else { _fail++; Console.WriteLine("FAIL " + label); }
    }

    private static string Sandbox(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static SupportReport.Facts FactsFor(string root) => new()
    {
        GameVersion = "0.0.0-test",
        MapDataVersion = "test-map",
        Engine = "4.8.test",
        Renderer = "forward_plus",
        Gpu = "Test GPU",
        Cpu = "Test CPU",
        System = "TestOS",
        Language = "russian",
        Screen = "1920x1080",
        World = "Poland · day 42",
        SettingsFile = Path.Combine(root, "settings", "settings.json"),
        LogsFolder = Path.Combine(root, "logs"),
        SavesFolder = Path.Combine(root, "saves"),
        ReportsFolder = Path.Combine(root, "reports"),
    };

    private static void CollectsWhatIsNeeded(string root)
    {
        var facts = FactsFor(root);
        var settings = Sandbox(root, "settings");
        File.WriteAllText(Path.Combine(settings, "settings.json"), "{\"language\":\"russian\"}");
        var logs = Sandbox(root, "logs");
        File.WriteAllText(Path.Combine(logs, "performance.log"), "FRAME_WINDOW fps=60");
        File.WriteAllText(Path.Combine(logs, "game.log"), "boot");
        var saves = Sandbox(root, "saves");
        File.WriteAllText(Path.Combine(saves, "campaign-2024.sav"), "not really a save");

        var path = SupportReport.Create(facts);
        Check(path.Length > 0 && File.Exists(path), "a report is written where it says it is");
        if (path.Length == 0) return;

        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(entry => entry.FullName).ToArray();
        Check(names.Contains("report.txt"), "the report carries the text a person reads first");
        Check(names.Any(name => name.StartsWith("logs/")), "the logs come along");
        Check(names.Contains("settings.json"), "so do the settings");
        Check(names.Contains("campaign-2024.sav"), "and the newest save");

        var text = ReadEntry(zip, "report.txt");
        Check(text.Contains("0.0.0-test"), "the build is named");
        Check(text.Contains("Poland"), "so is the world being played");
        Check(text.Contains("Test GPU"), "and the machine it is played on");
    }

    private static void SurvivesMissingPieces(string root)
    {
        var bare = Sandbox(root, "bare");
        var facts = FactsFor(bare);
        var path = SupportReport.Create(facts);
        Check(path.Length > 0 && File.Exists(path), "an empty install still gets a report");
        if (path.Length > 0)
        {
            using var zip = ZipFile.OpenRead(path);
            Check(zip.Entries.Count == 1 && zip.GetEntry("report.txt") is not null,
                "with nothing to attach it is just the text");
        }

        // A folder that cannot exist is the one honest failure: `|` is not a path character.
        var broken = FactsFor(bare);
        broken.ReportsFolder = Path.Combine(bare, "in|valid", "reports");
        var failed = SupportReport.Create(broken);
        Check(failed.Length == 0 && SupportReport.LastFailure.Length > 0, "a folder that cannot be written says why");
    }

    private static void RefusesWithoutAFolder(string root)
    {
        var facts = FactsFor(root);
        facts.ReportsFolder = "";
        Check(SupportReport.Create(facts).Length == 0, "no folder, no report");
        Check(SupportReport.Create(null).Length == 0, "no facts, no report");
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return "";
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
