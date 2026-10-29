using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DealOrForce.Simulation;

/// <summary>
/// A bug report a player can hand over: one file that carries what a stranger would otherwise
/// have to ask for in four messages - which build, which machine, what the log said, what the
/// settings were, and the save that shows the problem.
///
/// Nothing here touches Godot, so the same code can be exercised from the test stand, and a
/// failure to write a report never becomes a second bug: every step is caught and the reason is
/// returned in <see cref="LastFailure"/>.
/// </summary>
public static class SupportReport
{
    /// <summary>What the machine and the session are, as the caller can see them.</summary>
    public sealed class Facts
    {
        public string GameVersion = "";
        public string MapDataVersion = "";
        public string Engine = "";
        public string Renderer = "";
        public string Gpu = "";
        public string Cpu = "";
        public string System = "";
        public string Language = "";
        public string Screen = "";
        public string World = "";
        public string SettingsFile = "";
        public string LogsFolder = "";
        public string SavesFolder = "";
        public string ReportsFolder = "";
        public List<string> Notes = new();
    }

    /// <summary>Where the last attempt failed, empty when it worked.</summary>
    public static string LastFailure { get; private set; } = "";

    /// <summary>The largest save copied whole; a bigger one is named but not taken.</summary>
    public const long SaveLimitBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Writes `reports/DealOrForce-report-YYYYMMDD-HHMMSS.zip` and returns its path, or an empty
    /// string when nothing could be written.
    /// </summary>
    public static string Create(Facts facts)
    {
        LastFailure = "";
        if (facts is null) { LastFailure = "no facts"; return ""; }
        if (string.IsNullOrWhiteSpace(facts.ReportsFolder)) { LastFailure = "no reports folder"; return ""; }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stage = Path.Combine(Path.GetTempPath(), "dof-report-" + stamp);
        var zip = Path.Combine(facts.ReportsFolder, "DealOrForce-report-" + stamp + ".zip");
        try
        {
            Directory.CreateDirectory(facts.ReportsFolder);
            Directory.CreateDirectory(stage);
            WriteText(Path.Combine(stage, "report.txt"), Describe(facts));
            CopyLogs(facts.LogsFolder, Path.Combine(stage, "logs"));
            CopySettings(facts.SettingsFile, stage);
            CopyNewestSave(facts.SavesFolder, stage, facts.Notes);
            ZipFile.CreateFromDirectory(stage, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        catch (Exception exception)
        {
            LastFailure = exception.Message;
            return "";
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
        }

        if (!File.Exists(zip)) { LastFailure = "the archive was not written"; return ""; }
        return zip;
    }

    /// <summary>The text file at the root of the report: the part a person reads first.</summary>
    public static string Describe(Facts facts)
    {
        var text = new StringBuilder();
        text.AppendLine("Deal or Force - report");
        text.AppendLine("Written " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " local time");
        text.AppendLine();
        text.AppendLine("[build]");
        text.AppendLine("game        " + facts.GameVersion);
        text.AppendLine("map data    " + facts.MapDataVersion);
        text.AppendLine("engine      " + facts.Engine);
        text.AppendLine();
        text.AppendLine("[machine]");
        text.AppendLine("system      " + facts.System);
        text.AppendLine("cpu         " + facts.Cpu);
        text.AppendLine("gpu         " + facts.Gpu);
        text.AppendLine("renderer    " + facts.Renderer);
        text.AppendLine("screen      " + facts.Screen);
        text.AppendLine();
        text.AppendLine("[session]");
        text.AppendLine("language    " + facts.Language);
        text.AppendLine("world       " + facts.World);
        text.AppendLine();
        text.AppendLine("[folders]");
        text.AppendLine("settings    " + facts.SettingsFile);
        text.AppendLine("logs        " + facts.LogsFolder);
        text.AppendLine("saves       " + facts.SavesFolder);
        text.AppendLine("reports     " + facts.ReportsFolder);
        if (facts.Notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("[notes]");
            foreach (var note in facts.Notes) text.AppendLine("- " + note);
        }
        text.AppendLine();
        text.AppendLine("Attached: the log files, the settings, and the newest save when it is not too large.");
        return text.ToString();
    }

    private static void WriteText(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(true));
    }

    /// <summary>The last three logs: enough to show the minutes before a crash, not a history.</summary>
    private static void CopyLogs(string logsFolder, string destination)
    {
        if (string.IsNullOrWhiteSpace(logsFolder) || !Directory.Exists(logsFolder)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in new DirectoryInfo(logsFolder).GetFiles("*.*")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
        {
            try { file.CopyTo(Path.Combine(destination, file.Name), overwrite: true); }
            catch { /* a log being written is not worth failing the report over */ }
        }
    }

    private static void CopySettings(string settingsFile, string destination)
    {
        if (string.IsNullOrWhiteSpace(settingsFile) || !File.Exists(settingsFile)) return;
        try { File.Copy(settingsFile, Path.Combine(destination, "settings.json"), overwrite: true); }
        catch { }
    }

    private static void CopyNewestSave(string savesFolder, string destination, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(savesFolder) || !Directory.Exists(savesFolder)) return;
        var files = new DirectoryInfo(savesFolder).GetFiles("*.*");
        var newest = files.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return;
        if (newest.Length > SaveLimitBytes)
        {
            notes?.Add($"save {newest.Name} ({newest.Length / (1024 * 1024)} MB) is too large to attach");
            return;
        }
        try { newest.CopyTo(Path.Combine(destination, newest.Name), overwrite: true); }
        catch { }
    }
}
