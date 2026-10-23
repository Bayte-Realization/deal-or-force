using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DealOrForce.Simulation;

/// <summary>Bounded, engine-independent diagnostic journal. Never feeds back into simulation.
/// Gameplay threads enqueue; one background writer owns files and rotation.</summary>
public static class DiagnosticLogs
{
    private static readonly string[] Channels = { "system", "performance", "simulation", "orders", "errors" };
    private static readonly ConcurrentQueue<(string Channel, string Text)> Queue = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly object FilesLock = new();
    private static readonly Dictionary<string, StreamWriter> Writers = new();
    private static FileStream _sessionLock;
    private static Thread _worker;
    private static volatile bool _running;
    private static int _queued, _dropped, _day = -1;
    private static long _sequence;
    private static int _queueLimit = 2048;
    private static long _fileLimit = 4 * 1024 * 1024;
    public static double SlowScopeMilliseconds { get; private set; } = 40;
    public static string DirectoryPath { get; private set; } = "";
    public static string LastFailure { get; private set; } = "";
    public static bool Enabled => _running;
    public static long WrittenLines { get; private set; }

    /// <summary>
    /// Read what `common/diagnostics` says, without starting anything.
    ///
    /// This used to live inside <see cref="Start"/>, which meant the one way to find out
    /// whether logging is switched on was to switch it on. Separated so the configuration
    /// can be read by anything that wants to know - and so the category shows up in the
    /// loader's record of what is moddable, which is checked.
    /// </summary>
    public static bool ResetCatalog()
    {
        var enabledHere = true;
        foreach (var doc in GameDataLoader.DataDocuments("common", "diagnostics"))
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean()) enabledHere = false;
                if (root.TryGetProperty("max_file_mb", out var mb)) _fileLimit = Math.Clamp(mb.GetInt32(), 1, 32) * 1024L * 1024L;
                if (root.TryGetProperty("queue_limit", out var q)) _queueLimit = Math.Clamp(q.GetInt32(), 128, 16384);
                if (root.TryGetProperty("slow_scope_ms", out var slow)) SlowScopeMilliseconds = Math.Clamp(slow.GetDouble(), 5, 5000);
            }
        return enabledHere;
    }

    public static bool Start(string directory)
    {
        if (_running) return true;
        if (_worker?.IsAlive == true) { LastFailure = "Previous logging session is still flushing."; return false; }
        try
        {
            if (!ResetCatalog()) return false;
            DirectoryPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(DirectoryPath);
            try { _sessionLock = new FileStream(Path.Combine(DirectoryPath,"writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                // A second LAN instance must not rotate or overwrite the first one's files.
                DirectoryPath = Path.Combine(DirectoryPath, "instance-" + Environment.ProcessId);
                Directory.CreateDirectory(DirectoryPath);
                _sessionLock = new FileStream(Path.Combine(DirectoryPath,"writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            lock (FilesLock)
                foreach (var channel in Channels) Writers[channel] = OpenRotated(channel);
            while (Queue.TryDequeue(out _)) { }
            _queued = 0; _dropped = 0; _day = -1;
            _sequence = 0; WrittenLines = 0; LastFailure = "";
            _running = true;
            _worker = new Thread(WriterLoop) { IsBackground = true, Name = "DealOrForce diagnostic writer" };
            _worker.Start();
            AppDomain.CurrentDomain.UnhandledException += Unhandled;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += Unobserved;
            Write("system", $"SESSION_START pid={Environment.ProcessId} runtime={Environment.Version} os={Environment.OSVersion} logs={DirectoryPath}");
            return true;
        }
        catch (Exception e) { LastFailure = e.ToString(); CloseFiles(); return false; }
    }

    public static void SetDay(int day) => Volatile.Write(ref _day, day);
    public static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Line(string text) => $"{DateTime.UtcNow:O} seq={Interlocked.Increment(ref _sequence)} day={Volatile.Read(ref _day)} thread={Environment.CurrentManagedThreadId} " +
        (text ?? "").Replace("\r", "").Replace("\n", " \\n ");

    public static void Write(string channel, string message)
    {
        if (!_running) return;
        if (Array.IndexOf(Channels, channel) < 0) channel = "system";
        if (Interlocked.Increment(ref _queued) > _queueLimit)
        { Interlocked.Decrement(ref _queued); Interlocked.Increment(ref _dropped); return; }
        if (message?.Length > 32768) message = message[..32768] + " [TRUNCATED]";
        Queue.Enqueue((channel, Line(message)));
        if (channel == "errors" || Volatile.Read(ref _queued) >= 64) Wake.Set();
    }

    public static void Fatal(string message)
    {
        if (!_running) return;
        // Best effort for managed crashes; no promise for power loss, native abort or GPU reset.
        // The last words are written and flushed before the dump, and the dump is taken with
        // the file writer released: it takes its own lock, and a second thread crashing while
        // the dump runs must not find itself waiting for a writer that waits for the dump.
        lock (FilesLock)
        {
            try
            {
                WriteLine("errors", Line("FATAL " + message));
                FlushFiles();
            }
            catch (Exception e) { LastFailure = e.Message; }
        }
        CrashEnvelope.Dump(CrashEnvelope.ActiveCampaign, null, message);
    }

    /// <summary>
    /// Write and flush one line now, without the fatal handler's crash dump. A dump that
    /// failed reports itself through here: calling Fatal instead would ask for another dump
    /// from inside the one that just failed - the same request that cannot succeed.
    /// </summary>
    public static void Note(string channel, string message)
    {
        if (!_running) return;
        lock (FilesLock)
        {
            try { WriteLine(channel, Line(message)); FlushFiles(); }
            catch (Exception e) { LastFailure = e.Message; }
        }
    }

    public static void TraceCommand(Campaign c, ICampaignCommand command)
    {
        if (!Enabled) { command.Apply(c); return; }
        SetDay(c.ElapsedDays);
        var type = command.GetType();
        var description = type.Name;
        Nation issuer = null;
        try
        {
            foreach (var key in new[] { "IssuerId", "ArmyId", "FleetId", "TargetProvinceId", "StateMapIndex", "TargetId", "OrderId", "TemplateId", "Divisions", "SupportAttack" })
            {
                var p = type.GetProperty(key);
                if (p is not null) description += $" {key}={p.GetValue(command)}";
            }
            issuer = c.NationById(command.IssuerId);
        }
        catch (Exception e) { Write("errors", $"COMMAND_METADATA_UNAVAILABLE type={type.Name} error={e.Message}"); }
        var treasury = issuer?.Treasury ?? 0;
        var timer = Stopwatch.StartNew();
        Write("orders", "BEGIN " + description);
        try
        {
            command.Apply(c);
            var result = command switch
            {
                MoveArmyCommand move => move.Result.ToString(),
                RaiseArmyCommand raise => raise.FailureReason.Length == 0 ? "TrainingQueued" : "Rejected: " + raise.FailureReason,
                _ => "Returned (command may be a no-op)"
            };
            Write("orders", $"END {description} result={result} duration_ms={N(timer.Elapsed.TotalMilliseconds)} treasury_delta={N((issuer?.Treasury ?? 0)-treasury)}");
        }
        catch (Exception e) { Write("errors", $"COMMAND_EXCEPTION {description} {e}"); throw; }
    }

    private static void WriterLoop()
    {
        try
        {
            while (_running || !Queue.IsEmpty)
            {
                Wake.WaitOne(1000);
                lock (FilesLock)
                {
                    // Bounded drain so a producer flood cannot starve flush/shutdown.
                    for (var i = 0; i < _queueLimit && Queue.TryDequeue(out var entry); i++)
                    { Interlocked.Decrement(ref _queued); WriteLine(entry.Channel, entry.Text); }
                    var dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0) WriteLine("errors", Line($"LOGGER_OVERFLOW dropped={dropped} (game thread was not blocked)"));
                    FlushFiles();
                }
            }
        }
        catch (Exception e) { LastFailure = e.ToString(); _running = false; }
        finally { CloseFiles(); }
    }

    private static StreamWriter OpenRotated(string channel)
    {
        var path = Path.Combine(DirectoryPath, channel + ".log");
        // Only these exact application-owned filenames are touched, never a directory tree.
        if (File.Exists(path + ".2")) File.Delete(path + ".2");
        if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
        if (File.Exists(path)) File.Move(path, path + ".1");
        return new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }
    private static void WriteLine(string channel, string text)
    {
        if (!Writers.TryGetValue(channel, out var writer)) writer = Writers["system"];
        if (writer.BaseStream.Position >= _fileLimit)
        {
            writer.Dispose();
            Writers[channel] = writer = OpenRotated(channel);
        }
        writer.WriteLine(text); WrittenLines++;
    }
    private static void FlushFiles() { foreach (var w in Writers.Values) w.Flush(); }
    private static void CloseFiles()
    {
        lock (FilesLock)
        {
            foreach (var w in Writers.Values) try { w.Dispose(); } catch { }
            Writers.Clear();
            _sessionLock?.Dispose(); _sessionLock = null;
        }
    }
    private static void Unhandled(object sender, UnhandledExceptionEventArgs e) => Fatal(e.ExceptionObject?.ToString());
    private static void Unobserved(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e) => Write("errors", "UNOBSERVED_TASK " + e.Exception);
    public static bool Stop(int timeoutMilliseconds = 2000)
    {
        if (_running) Write("system", "SESSION_END clean_shutdown=true");
        _running = false; Wake.Set();
        var complete = _worker?.Join(Math.Clamp(timeoutMilliseconds, 0, 10000)) ?? true;
        AppDomain.CurrentDomain.UnhandledException -= Unhandled;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= Unobserved;
        return complete;
    }
}
