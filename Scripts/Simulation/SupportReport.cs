using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using ZLinq;

namespace DealOrForce.Simulation;

/// <summary>
/// A bug report a player can hand over: one file that carries what a stranger would otherwise
/// have to ask for in four messages - which build, which machine, what the log said, what the
/// settings were, and the save that shows the problem.
///
/// Nothing here touches engine scene types, so the same code can be exercised from the test stand,
/// and a failure to write a report never becomes a second bug: every step is caught and the reason
/// is returned in <see cref="LastFailure"/>.
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

    private const int IoCopyBufferBytes = 64 * 1024;

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
        var zip = Path.Combine(facts.ReportsFolder, "DealOrForce-report-" + stamp + ".zip");
        try
        {
            Directory.CreateDirectory(facts.ReportsFolder);
            using var archiveStream = RecyclableStreams.Get("SupportReport.Zip");
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddTextEntry(archive, "report.txt", Describe(facts));
                AddLogs(archive, facts.LogsFolder);
                AddSettings(archive, facts.SettingsFile);
                AddNewestSave(archive, facts.SavesFolder, facts.Notes);
            }

            archiveStream.Position = 0;
            using var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, IoCopyBufferBytes);
            archiveStream.CopyTo(output);
            output.Flush();
        }
        catch (Exception exception)
        {
            LastFailure = exception.Message;
            return "";
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

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.Write(content);
    }

    private static void TryAddFileEntry(ZipArchive archive, string sourcePath, string entryName)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            IoCopyBufferBytes, FileOptions.SequentialScan);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    /// <summary>Formats an exception using Ben.Demystifier with safe fallback for stripped builds.</summary>
    public static string FormatExceptionDemystified(Exception exception)
    {
        if (exception is null) return "(null)";
        try
        {
            return exception.ToStringDemystified();
        }
        catch
        {
            try { return exception.ToString(); }
            catch { return exception.GetType().FullName ?? "Exception"; }
        }
    }

    /// <summary>Captures the current stack trace using Ben.Demystifier with safe fallback.</summary>
    public static string CaptureStackDemystified()
    {
        try
        {
            return EnhancedStackTrace.Current().ToString();
        }
        catch
        {
            try { return Environment.StackTrace; }
            catch { return ""; }
        }
    }

    /// <summary>The last three logs plus any boot/crash diagnostics so support gets the whole picture.</summary>
    private static void AddLogs(ZipArchive archive, string logsFolder)
    {
        if (string.IsNullOrWhiteSpace(logsFolder) || !Directory.Exists(logsFolder)) return;
        var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(logsFolder).GetFiles("*.*")
                     .AsValueEnumerable()
                     .OrderByDescending(static f => f.LastWriteTimeUtc)
                     .Take(3))
        {
            try
            {
                TryAddFileEntry(archive, file.FullName, "logs/" + file.Name);
                attached.Add(file.Name);
            }
            catch { /* a log being written is not worth failing the report over */ }
        }

        foreach (var diagName in new[] { "last_crash.txt", "boot_trace.txt", "boot_trace.prev.txt", "android_fatal.log", "crash_envelope_latest.json" })
        {
            if (attached.Contains(diagName)) continue;
            var fullPath = Path.Combine(logsFolder, diagName);
            if (!File.Exists(fullPath)) continue;
            try
            {
                TryAddFileEntry(archive, fullPath, "logs/" + diagName);
                attached.Add(diagName);
            }
            catch { /* ignore locked/unreadable diagnostic file */ }
        }
    }

    private static void AddSettings(ZipArchive archive, string settingsFile)
    {
        if (string.IsNullOrWhiteSpace(settingsFile) || !File.Exists(settingsFile)) return;
        try { TryAddFileEntry(archive, settingsFile, "settings.json"); }
        catch { }
    }

    private static void AddNewestSave(ZipArchive archive, string savesFolder, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(savesFolder) || !Directory.Exists(savesFolder)) return;
        var files = new DirectoryInfo(savesFolder).GetFiles("*.*");
        var newest = files.AsValueEnumerable()
            .OrderByDescending(static f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null) return;
        if (newest.Length > SaveLimitBytes)
        {
            notes?.Add($"save {newest.Name} ({newest.Length / (1024 * 1024)} MB) is too large to attach");
            return;
        }
        try { TryAddFileEntry(archive, newest.FullName, newest.Name); }
        catch { }
    }
}
