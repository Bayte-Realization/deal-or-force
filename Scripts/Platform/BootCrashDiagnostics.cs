using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce.Platform;

/// <summary>
/// Early, synchronous boot-step tracer and crash dossier writer for both PC and Android.
/// Designed to run before Campaign/DiagnosticLogs exist (AndroidBoot, MainMenuScene,
/// MenuGlobe, CountryPicker) and to mirror logs to Android app-specific external storage
/// (/storage/emulated/0/Android/data/&lt;pkg&gt;/files/logs/) so players can inspect traces
/// in Samsung "My Files" without ADB or root, alongside user://logs/ and PC Documents/logs/.
/// Never emit GD.Print / GD.PushError from inside this class so GameDiagnosticLogger cannot recurse.
/// </summary>
public static class BootCrashDiagnostics
{
    private const string BootTraceFileName = "boot_trace.txt";
    private const string BootTracePrevFileName = "boot_trace.prev.txt";
    private const string LastCrashFileName = "last_crash.txt";
    private const string AndroidFatalFileName = "android_fatal.log";
    private const string DefaultPackageName = "com.bayterealization.dealorforce";
    private const long MaxBootTraceBytes = 512 * 1024;
    private const long MaxCrashAppendBytes = 64 * 1024;

    private static readonly object Sync = new();
    private static readonly Stopwatch ProcessClock = Stopwatch.StartNew();
    private static readonly List<string> TargetDirs = new();

    private static int _dirsInitialized;
    private static int _sessionStarted;
    private static int _hooksInstalled;
    private static int _crashRecordedInSession;
    private static long _bootTraceWrittenBytes;
    private static string _accessibleLogDirectory = "";
    private static string _internalLogDirectory = "";
    private static string _lastStage = "uninitialized";
    private static string _cachedSnapshot = "";

    public static string AccessibleLogDirectory
    {
        get { EnsureDirectories(); return _accessibleLogDirectory; }
    }

    public static string InternalLogDirectory
    {
        get { EnsureDirectories(); return _internalLogDirectory; }
    }

    public static string BootTracePath =>
        string.IsNullOrEmpty(AccessibleLogDirectory)
            ? ""
            : Path.Combine(AccessibleLogDirectory, BootTraceFileName);

    public static string LastCrashPath =>
        string.IsNullOrEmpty(AccessibleLogDirectory)
            ? ""
            : Path.Combine(AccessibleLogDirectory, LastCrashFileName);

    public static string LastStage => _lastStage;

    public static bool IsAndroidRuntime =>
        OperatingSystem.IsAndroid() || OS.HasFeature("android") ||
        string.Equals(OS.GetName(), "Android", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures log directories and global exception hooks exist, rotates the previous session's
    /// boot_trace.txt once per process, writes environment/GPU header details, and logs an initial milestone.
    /// Safe to call across scene transitions; later calls record milestones without wiping the boot trace.
    /// </summary>
    public static void BeginSession(string entryStage, string detail = "")
    {
        InstallCrashHooks();
        EnsureDirectories();

        bool isFirstStart;
        lock (Sync)
        {
            isFirstStart = Interlocked.Exchange(ref _sessionStarted, 1) == 0;
            if (isFirstStart)
            {
                _crashRecordedInSession = 0;
                _bootTraceWrittenBytes = 0;
                RotateAndStartBootTraceLocked(entryStage, detail);
            }
        }

        if (!isFirstStart)
            Step("session_transition", string.IsNullOrWhiteSpace(detail) ? entryStage : $"{entryStage} {detail}");
    }

    /// <summary>Installs process-wide unhandled exception and unobserved task hooks idempotently.</summary>
    public static void InstallCrashHooks()
    {
        if (Interlocked.Exchange(ref _hooksInstalled, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                if (ex is not null)
                {
                    RecordException("AppDomain.UnhandledException", ex,
                        $"is_terminating={args.IsTerminating}", fatal: true);
                }
                else
                {
                    RecordTextCrash("AppDomain.UnhandledException",
                        $"Non-Exception object (terminating={args.IsTerminating}): {args.ExceptionObject}",
                        fatal: true);
                }
            }
            catch
            {
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                RecordException("TaskScheduler.UnobservedTaskException", args.Exception,
                    "unobserved_task_fault", fatal: false);
            }
            catch
            {
            }
        };
    }

    /// <summary>Appends one synchronously flushed step line to boot_trace.txt across all target log directories.</summary>
    public static void Step(string stage, string detail = "")
    {
        if (string.IsNullOrWhiteSpace(stage)) return;
        EnsureDirectories();

        var nowUtc = DateTime.UtcNow;
        var elapsedMs = ProcessClock.ElapsedMilliseconds;
        var threadId = System.Environment.CurrentManagedThreadId;
        var managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        var staticMb = TryGetStaticMemoryMb();
        var cleanDetail = CollapseWhitespace(detail);
        _lastStage = stage;

        var line = string.IsNullOrEmpty(cleanDetail)
            ? $"[{nowUtc:yyyy-MM-dd HH:mm:ss.fff}Z +{elapsedMs}ms t={threadId} gc={managedMb:0.1}MB nat={staticMb:0.1}MB] {stage}\n"
            : $"[{nowUtc:yyyy-MM-dd HH:mm:ss.fff}Z +{elapsedMs}ms t={threadId} gc={managedMb:0.1}MB nat={staticMb:0.1}MB] {stage}: {cleanDetail}\n";

        lock (Sync)
        {
            AppendBootTraceLocked(line);
        }
    }

    /// <summary>Records a managed exception with Ben.Demystifier stack trace (guarded), GPU, and memory snapshot.</summary>
    public static void RecordException(string source, Exception exception, string context = "", bool fatal = true)
    {
        if (exception is null) return;
        EnsureDirectories();

        string formattedException;
        try
        {
            formattedException = SupportReport.FormatExceptionDemystified(exception);
        }
        catch
        {
            formattedException = exception.ToString();
        }

        var dossier = BuildCrashDossier(source, context, fatal, formattedException);
        lock (Sync)
        {
            AppendBootTraceLocked(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z +{ProcessClock.ElapsedMilliseconds}ms] {(fatal ? "FATAL" : "ERROR")} {source}: {exception.GetType().Name}: {CollapseWhitespace(exception.Message)}\n");
            WriteCrashDossierLocked(dossier, IsAndroidRuntime);
        }
    }

    /// <summary>Records a non-Exception fatal failure, native crash notification, or fatal engine error.</summary>
    public static void RecordTextCrash(string source, string message, bool fatal = true)
    {
        EnsureDirectories();
        var stack = "";
        try
        {
            stack = SupportReport.CaptureStackDemystified();
        }
        catch
        {
            try { stack = System.Environment.StackTrace; } catch { stack = ""; }
        }

        var body = string.IsNullOrWhiteSpace(stack)
            ? message
            : message + "\n\nManaged Stack:\n" + stack;
        var dossier = BuildCrashDossier(source, "", fatal, body);
        lock (Sync)
        {
            AppendBootTraceLocked(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z +{ProcessClock.ElapsedMilliseconds}ms] {(fatal ? "FATAL" : "ERROR")} {source}: {CollapseWhitespace(message)}\n");
            WriteCrashDossierLocked(dossier, IsAndroidRuntime);
        }
    }

    /// <summary>
    /// Called from GameDiagnosticLogger without making any Godot calls that could trigger recursive engine logs.
    /// Records engine error lines into boot_trace.txt and elevates shader/GPU/OOM faults to last_crash.txt.
    /// </summary>
    public static void RecordEngineErrorPureIo(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Volatile.Read(ref _dirsInitialized) == 0) return;

        var collapsed = CollapseWhitespace(text);
        if (collapsed.Length > 900) collapsed = collapsed[..900] + "...";
        var managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z +{ProcessClock.ElapsedMilliseconds}ms t={System.Environment.CurrentManagedThreadId} gc={managedMb:0.1}MB] engine_error: {collapsed}\n";

        var isCritical =
            text.Contains("ENGINE_Shader", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("shader", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GLES", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("OpenGL", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed to load", StringComparison.OrdinalIgnoreCase);

        lock (Sync)
        {
            AppendBootTraceLocked(line);
            if (isCritical)
            {
                var stack = "";
                try { stack = SupportReport.CaptureStackDemystified(); } catch { }
                var body = string.IsNullOrWhiteSpace(stack) ? text : text + "\n\nStack:\n" + stack;
                WriteCrashDossierLocked(
                    BuildCrashDossier("GodotEngineError", "pure-io logger callback", fatal: false, body, includeGodotQueries: false),
                    includeAndroidFatal: IsAndroidRuntime);
            }
        }
    }

    /// <summary>Collects a readable multi-line device, renderer, memory, and log-location summary.</summary>
    public static string BuildRuntimeSnapshot(bool includeGodotQueries = true)
    {
        if (!includeGodotQueries && !string.IsNullOrEmpty(_cachedSnapshot))
            return _cachedSnapshot;

        var sb = new StringBuilder(512);
        sb.AppendLine($"UtcNow: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z | UptimeMs: {ProcessClock.ElapsedMilliseconds}");
        sb.AppendLine($"ManagedHeapMB: {GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.2} | Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)}");

        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            sb.AppendLine($"GcMemoryLoadMB: {gcInfo.MemoryLoadBytes / (1024.0 * 1024.0):0.1} / HighMemThreshMB: {gcInfo.HighMemoryLoadThresholdBytes / (1024.0 * 1024.0):0.1} | TotalAvailMB: {gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0):0.1}");
        }
        catch
        {
        }

        sb.AppendLine($"ProcessArch: {RuntimeInformation.ProcessArchitecture} | Cores: {System.Environment.ProcessorCount} | Framework: {RuntimeInformation.FrameworkDescription}");

        if (!includeGodotQueries)
        {
            if (!string.IsNullOrEmpty(_cachedSnapshot))
            {
                sb.AppendLine("--- Cached Startup Environment ---");
                sb.AppendLine(_cachedSnapshot);
            }
            return sb.ToString();
        }

        var osName = SafeQuery(() => OS.GetName(), "?");
        var osVer = SafeQuery(() => OS.GetVersion(), "?");
        var model = SafeQuery(() => OS.GetModelName(), "?");
        var cpu = SafeQuery(() => OS.GetProcessorName(), "?");
        var version = SafeQuery(() => ProjectSettings.HasSetting("application/config/version")
            ? ProjectSettings.GetSetting("application/config/version").AsString()
            : "?", "?");
        var renderMethod = SafeQuery(() => RenderingServer.GetCurrentRenderingMethod(), "?");
        var renderDriver = SafeQuery(() => RenderingServer.GetCurrentRenderingDriverName(), "?");
        var adapter = SafeQuery(() => RenderingServer.GetVideoAdapterName(), "?");
        var vendor = SafeQuery(() => RenderingServer.GetVideoAdapterVendor(), "?");
        var apiVer = SafeQuery(() => RenderingServer.GetVideoAdapterApiVersion(), "?");
        var staticMemMb = TryGetStaticMemoryMb();
        var (physMb, freeMb, availMb) = TryGetOsMemoryMb();
        var (vramMb, texMb) = TryGetRenderMemoryMb();

        var env = new StringBuilder(384);
        env.AppendLine($"AppVersion: {version} | OS: {osName} {osVer} | Model: {model} | CPU: {cpu}");
        env.AppendLine($"Renderer: method={renderMethod} driver={renderDriver} | Adapter: {adapter} ({vendor}) API={apiVer}");
        env.AppendLine($"MemoryMB: Static={staticMemMb:0.1} | OS Phys={physMb:0.1} Free={freeMb:0.1} Avail={availMb:0.1} | RenderVRAM={vramMb:0.1} TexMem={texMb:0.1}");
        env.AppendLine($"AccessibleLogs: {_accessibleLogDirectory}");
        if (!string.IsNullOrEmpty(_internalLogDirectory) &&
            !string.Equals(_internalLogDirectory, _accessibleLogDirectory, StringComparison.OrdinalIgnoreCase))
        {
            env.AppendLine($"InternalLogs: {_internalLogDirectory}");
        }

        var snapshotText = env.ToString().TrimEnd();
        _cachedSnapshot = snapshotText;
        sb.AppendLine(snapshotText);
        return sb.ToString();
    }

    /// <summary>Formats short human-friendly paths for in-app UI labels (Android My Files vs PC Documents).</summary>
    public static string DescribeAccessiblePathsForUi()
    {
        EnsureDirectories();
        if (string.IsNullOrEmpty(_accessibleLogDirectory))
            return "(log directory unavailable)";
        if (string.IsNullOrEmpty(_internalLogDirectory) ||
            string.Equals(_accessibleLogDirectory, _internalLogDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return _accessibleLogDirectory;
        }
        return $"{_accessibleLogDirectory} (fallback: {_internalLogDirectory})";
    }

    private static void EnsureDirectories()
    {
        if (Volatile.Read(ref _dirsInitialized) == 1) return;
        lock (Sync)
        {
            if (_dirsInitialized == 1) return;
            TargetDirs.Clear();

            var verificationDocs = GetVerificationDocumentsOverride();
            if (!string.IsNullOrWhiteSpace(verificationDocs))
            {
                var verifyLogs = GameFolders.For(verificationDocs).Logs;
                if (TryEnsureWritableDirectory(verifyLogs))
                {
                    TargetDirs.Add(verifyLogs);
                    _accessibleLogDirectory = verifyLogs;
                    _internalLogDirectory = verifyLogs;
                    _dirsInitialized = 1;
                    return;
                }
            }

            var userRoot = SafeQuery(() => ProjectSettings.GlobalizePath("user://"), "");
            if (string.IsNullOrWhiteSpace(userRoot))
                userRoot = SafeQuery(() => OS.GetUserDataDir(), "");
            var userLogs = string.IsNullOrWhiteSpace(userRoot) ? "" : Path.Combine(userRoot, "logs");

            if (IsAndroidRuntime)
            {
                var pkg = InferAndroidPackageName(userRoot);
                var candidates = new List<string>(4);

                var extDocs = SafeQuery(() => OS.GetSystemDir(OS.SystemDir.Documents, false), "");
                if (!string.IsNullOrWhiteSpace(extDocs))
                {
                    if (string.Equals(Path.GetFileName(extDocs.TrimEnd('/', '\\')), "Documents", StringComparison.OrdinalIgnoreCase))
                    {
                        var filesDir = Path.GetDirectoryName(extDocs.TrimEnd('/', '\\'));
                        if (!string.IsNullOrWhiteSpace(filesDir))
                            candidates.Add(Path.Combine(filesDir, "logs"));
                    }
                    candidates.Add(Path.Combine(extDocs, "logs"));
                }

                candidates.Add($"/storage/emulated/0/Android/data/{pkg}/files/logs");
                var extEnv = SafeQuery(() => OS.GetEnvironment("EXTERNAL_STORAGE"), "");
                if (!string.IsNullOrWhiteSpace(extEnv))
                    candidates.Add(Path.Combine(extEnv, "Android", "data", pkg, "files", "logs"));

                foreach (var candidate in candidates)
                {
                    if (!TryEnsureWritableDirectory(candidate)) continue;
                    AddUniqueTargetDir(candidate);
                    if (string.IsNullOrEmpty(_accessibleLogDirectory))
                        _accessibleLogDirectory = candidate;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(userLogs) && TryEnsureWritableDirectory(userLogs))
                {
                    AddUniqueTargetDir(userLogs);
                    _internalLogDirectory = userLogs;
                    if (string.IsNullOrEmpty(_accessibleLogDirectory))
                        _accessibleLogDirectory = userLogs;
                }
            }
            else
            {
                var docs = SafeQuery(() => OS.GetSystemDir(OS.SystemDir.Documents), "");
                if (!string.IsNullOrWhiteSpace(docs))
                {
                    var docLogs = GameFolders.For(docs).Logs;
                    if (TryEnsureWritableDirectory(docLogs))
                    {
                        AddUniqueTargetDir(docLogs);
                        _accessibleLogDirectory = docLogs;
                    }
                }

                if (!string.IsNullOrWhiteSpace(userLogs) && TryEnsureWritableDirectory(userLogs))
                {
                    _internalLogDirectory = userLogs;
                    if (string.IsNullOrEmpty(_accessibleLogDirectory))
                    {
                        AddUniqueTargetDir(userLogs);
                        _accessibleLogDirectory = userLogs;
                    }
                }
            }

            if (TargetDirs.Count == 0)
            {
                var fallback = Path.Combine(AppContext.BaseDirectory ?? ".", "logs");
                if (TryEnsureWritableDirectory(fallback))
                {
                    AddUniqueTargetDir(fallback);
                    _accessibleLogDirectory = fallback;
                    _internalLogDirectory = fallback;
                }
            }

            _dirsInitialized = 1;
        }
    }

    private static void RotateAndStartBootTraceLocked(string entryStage, string detail)
    {
        foreach (var dir in TargetDirs)
        {
            try
            {
                var current = Path.Combine(dir, BootTraceFileName);
                var prev = Path.Combine(dir, BootTracePrevFileName);
                if (File.Exists(current))
                    File.Copy(current, prev, overwrite: true);
            }
            catch
            {
            }
        }

        var header = new StringBuilder(512);
        header.AppendLine("============================================================");
        header.AppendLine($"Deal or Force Boot Trace | Entry={entryStage} | Detail={CollapseWhitespace(detail)}");
        header.AppendLine(BuildRuntimeSnapshot(includeGodotQueries: true));
        header.AppendLine("============================================================");
        var text = header.ToString();

        foreach (var dir in TargetDirs)
        {
            WriteAllTextFlushed(Path.Combine(dir, BootTraceFileName), text);
        }
        _bootTraceWrittenBytes = Encoding.UTF8.GetByteCount(text);
    }

    private static void AppendBootTraceLocked(string text)
    {
        if (TargetDirs.Count == 0 || string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (_bootTraceWrittenBytes + bytes > MaxBootTraceBytes)
        {
            var marker = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] [rotated mid-session after {_bootTraceWrittenBytes} bytes]\n";
            foreach (var dir in TargetDirs)
            {
                try
                {
                    var current = Path.Combine(dir, BootTraceFileName);
                    var prev = Path.Combine(dir, BootTracePrevFileName);
                    if (File.Exists(current))
                        File.Copy(current, prev, overwrite: true);
                    WriteAllTextFlushed(current, marker);
                }
                catch
                {
                }
            }
            _bootTraceWrittenBytes = Encoding.UTF8.GetByteCount(marker);
        }

        foreach (var dir in TargetDirs)
        {
            AppendTextFlushed(Path.Combine(dir, BootTraceFileName), text);
        }
        _bootTraceWrittenBytes += bytes;
    }

    private static void WriteCrashDossierLocked(string dossier, bool includeAndroidFatal)
    {
        var isFirstInSession = Interlocked.Exchange(ref _crashRecordedInSession, 1) == 0;
        foreach (var dir in TargetDirs)
        {
            var crashPath = Path.Combine(dir, LastCrashFileName);
            if (isFirstInSession)
            {
                WriteAllTextFlushed(crashPath, dossier);
            }
            else
            {
                try
                {
                    var existingLen = File.Exists(crashPath) ? new FileInfo(crashPath).Length : 0;
                    if (existingLen < MaxCrashAppendBytes)
                        AppendTextFlushed(crashPath, "\n--- SECONDARY CRASH EVENT IN SAME SESSION ---\n" + dossier);
                }
                catch
                {
                }
            }

            if (includeAndroidFatal)
            {
                AppendTextFlushed(Path.Combine(dir, AndroidFatalFileName), dossier + "\n");
            }
        }
    }

    private static string BuildCrashDossier(
        string source,
        string context,
        bool fatal,
        string body,
        bool includeGodotQueries = true)
    {
        var sb = new StringBuilder(1536);
        sb.AppendLine("============================================================");
        sb.AppendLine($"Deal or Force Crash Dossier ({(fatal ? "FATAL" : "NON-FATAL")})");
        sb.AppendLine($"Source: {source}");
        if (!string.IsNullOrWhiteSpace(context)) sb.AppendLine($"Context: {context}");
        sb.AppendLine($"LastBootStage: {_lastStage}");
        sb.AppendLine(BuildRuntimeSnapshot(includeGodotQueries));
        sb.AppendLine("--- Exception / Diagnostic Detail ---");
        sb.AppendLine(body);
        var tail = ReadBootTraceTail(28);
        if (!string.IsNullOrEmpty(tail))
        {
            sb.AppendLine("--- Boot Trace Tail ---");
            sb.AppendLine(tail);
        }
        sb.AppendLine("============================================================");
        return sb.ToString();
    }

    private static string ReadBootTraceTail(int maxLines)
    {
        try
        {
            if (string.IsNullOrEmpty(_accessibleLogDirectory)) return "";
            var tracePath = Path.Combine(_accessibleLogDirectory, BootTraceFileName);
            if (!File.Exists(tracePath)) return "";
            var lines = File.ReadAllLines(tracePath);
            var start = Math.Max(0, lines.Length - maxLines);
            return string.Join("\n", lines[start..]);
        }
        catch
        {
            return "";
        }
    }

    private static void WriteAllTextFlushed(string path, string content)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.ReadWrite, 4096);
            using var writer = new StreamWriter(fs, Encoding.UTF8);
            writer.Write(content);
            writer.Flush();
            TryFlushToDisk(fs);
        }
        catch
        {
        }
    }

    private static void AppendTextFlushed(string path, string content)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Append, System.IO.FileAccess.Write, FileShare.ReadWrite, 4096);
            using var writer = new StreamWriter(fs, Encoding.UTF8);
            writer.Write(content);
            writer.Flush();
            TryFlushToDisk(fs);
        }
        catch
        {
        }
    }

    private static void TryFlushToDisk(FileStream fs)
    {
        try
        {
            fs.Flush(flushToDisk: true);
        }
        catch
        {
        }
    }

    private static bool TryEnsureWritableDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write_probe_{System.Environment.ProcessId}_{System.Environment.CurrentManagedThreadId}");
            using (var fs = new FileStream(probe, FileMode.Create, System.IO.FileAccess.Write, FileShare.ReadWrite, 256))
            {
                fs.WriteByte(0x20);
            }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddUniqueTargetDir(string dir)
    {
        for (var i = 0; i < TargetDirs.Count; i++)
        {
            if (string.Equals(TargetDirs[i], dir, StringComparison.OrdinalIgnoreCase))
                return;
        }
        TargetDirs.Add(dir);
    }

    private static string GetVerificationDocumentsOverride()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            if (Array.IndexOf(args, "--verify-build") < 0) return "";
            return OS.GetEnvironment("DOF_VERIFICATION_DOCUMENTS") ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string InferAndroidPackageName(string userRoot)
    {
        if (!string.IsNullOrWhiteSpace(userRoot))
        {
            var parts = userRoot.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("data", StringComparison.OrdinalIgnoreCase) &&
                    parts[i + 1].Contains('.', StringComparison.Ordinal))
                    return parts[i + 1];
                if (string.Equals(parts[i], "0", StringComparison.Ordinal) &&
                    parts[i + 1].Contains('.', StringComparison.Ordinal))
                    return parts[i + 1];
            }
        }
        return DefaultPackageName;
    }

    private static double TryGetStaticMemoryMb()
    {
        try { return OS.GetStaticMemoryUsage() / (1024.0 * 1024.0); }
        catch { return 0.0; }
    }

    private static (double physMb, double freeMb, double availMb) TryGetOsMemoryMb()
    {
        try
        {
            var mem = OS.GetMemoryInfo();
            double Mb(string key) => mem.TryGetValue(key, out var v) ? v.AsInt64() / (1024.0 * 1024.0) : 0.0;
            return (Mb("physical"), Mb("free"), Mb("available"));
        }
        catch
        {
            return (0.0, 0.0, 0.0);
        }
    }

    private static (double vramMb, double texMb) TryGetRenderMemoryMb()
    {
        try
        {
            var vram = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);
            var tex = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / (1024.0 * 1024.0);
            return (vram, tex);
        }
        catch
        {
            return (0.0, 0.0);
        }
    }

    private static string SafeQuery(Func<string> getter, string fallback)
    {
        try
        {
            var value = getter();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " | ", StringComparison.Ordinal)
            .Trim();
    }
}
