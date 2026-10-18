using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// A recorded command entry in the deterministic crash envelope replay ring buffer.
/// </summary>
public readonly record struct CrashCommandEntry(
    [property: JsonPropertyName("day")] int Day,
    [property: JsonPropertyName("hour")] int Hour,
    [property: JsonPropertyName("type")] string TypeName,
    [property: JsonPropertyName("payload")] string Payload
);

/// <summary>
/// Serializable envelope captured on unhandled simulation exceptions or fatal halts.
/// SimBench's `--crash-replay-check` proves the recorded tape rebuilds the same world
/// offline, headless and without the editor.
/// </summary>
public sealed class CrashEnvelopeData
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("timestamp_utc")]
    public string TimestampUtc { get; set; } = "";

    [JsonPropertyName("campaign_seed")]
    public int CampaignSeed { get; set; }

    [JsonPropertyName("scenario_id")]
    public string ScenarioId { get; set; } = "";

    [JsonPropertyName("elapsed_days")]
    public int ElapsedDays { get; set; }

    [JsonPropertyName("elapsed_hours")]
    public long ElapsedHours { get; set; }

    [JsonPropertyName("game_date")]
    public string GameDate { get; set; } = "";

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("simulation_fault")]
    public string SimulationFault { get; set; } = "";

    [JsonPropertyName("exception_type")]
    public string ExceptionType { get; set; } = "";

    [JsonPropertyName("exception_message")]
    public string ExceptionMessage { get; set; } = "";

    [JsonPropertyName("stack_trace")]
    public string StackTrace { get; set; } = "";

    [JsonPropertyName("recent_commands")]
    public List<CrashCommandEntry> RecentCommands { get; set; } = new();
}

/// <summary>
/// Bounded in-memory command tape and crash dumper. Maintains a ring buffer of recent
/// player/AI orders (up to 14 days or 1,024 commands) to dump upon fatal faults.
/// </summary>
public static class CrashEnvelope
{
    private static readonly object SyncLock = new();
    public const int MaxRingCapacity = 1024;
    public const int MaxRetentionDays = 14;

    private static readonly Queue<CrashCommandEntry> Ring = new(MaxRingCapacity);
    private static WeakReference<Campaign> _activeCampaignRef;
    private static bool _dumping;

    /// <summary>Why the last dump did not reach the disk; "" when it did.</summary>
    public static string LastFailure { get; private set; } = "";

    public static Campaign ActiveCampaign =>
        _activeCampaignRef != null && _activeCampaignRef.TryGetTarget(out var c) ? c : null;

    public static void Track(Campaign campaign)
    {
        if (campaign == null) return;
        lock (SyncLock)
        {
            _activeCampaignRef = new WeakReference<Campaign>(campaign);
        }
    }

    public static void Record(Campaign campaign, ICampaignCommand command)
    {
        if (command == null) return;
        Track(campaign);

        if (!NetworkCommandCodec.TryEncode(command, out var typeName, out var payload))
        {
            typeName = command.GetType().Name;
            payload = "{}";
        }

        var day = campaign?.ElapsedDays ?? 0;
        var hour = campaign?.Date.Hour ?? 0;

        lock (SyncLock)
        {
            Ring.Enqueue(new CrashCommandEntry(day, hour, typeName, payload));

            // Evict entries older than MaxRetentionDays from current simulation day, or over capacity
            while (Ring.Count > MaxRingCapacity || (Ring.Count > 0 && (day - Ring.Peek().Day) > MaxRetentionDays))
            {
                Ring.Dequeue();
            }
        }
    }

    public static IReadOnlyList<CrashCommandEntry> GetRecentCommands()
    {
        lock (SyncLock)
        {
            return Ring.ToArray();
        }
    }

    public static void Clear()
    {
        lock (SyncLock)
        {
            Ring.Clear();
        }
    }

    public static string Dump(Campaign campaign, Exception exception, string reason = null, string targetDirectory = null)
    {
        campaign ??= ActiveCampaign;
        lock (SyncLock)
        {
            // The fatal-error handler is one of the callers: DiagnosticLogs.Fatal writes its
            // line and then asks for a dump. A dump that fails must not call that handler
            // back through this same path - the two would re-enter each other until the stack
            // ran out, inside the very handler that exists to leave evidence behind. One dump
            // per failure; anything raised while one is being written is noted, not dumped.
            if (_dumping) return "";
            _dumping = true;
            try
            {
                var envelope = new CrashEnvelopeData
                {
                    SchemaVersion = 1,
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    CampaignSeed = campaign?.Seed ?? 0,
                    ScenarioId = campaign?.ScenarioId ?? "2024",
                    ElapsedDays = campaign?.ElapsedDays ?? 0,
                    ElapsedHours = campaign?.ElapsedHours ?? 0,
                    GameDate = campaign?.Date.ToString() ?? "",
                    Fingerprint = campaign != null ? CampaignFingerprint.Of(campaign).ToString("x16") : "",
                    SimulationFault = campaign?.SimulationFault ?? "",
                    ExceptionType = exception?.GetType().FullName ?? "",
                    ExceptionMessage = reason ?? exception?.Message ?? "Simulation crash or unhandled failure",
                    StackTrace = exception?.StackTrace ?? Environment.StackTrace,
                    RecentCommands = new List<CrashCommandEntry>(Ring)
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                var json = JsonSerializer.Serialize(envelope, options);

                var dir = ResolveDirectory(targetDirectory);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Milliseconds and the faulting type in the name: two dumps in one second used
                // to share a stamp, and the second write silently replaced the first - which
                // is exactly how the dump that carried the real stack is lost.
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff") + "_" +
                            (exception?.GetType().Name ?? "fault");
                var filename = $"crash_envelope_{stamp}.json";
                var fullPath = Path.Combine(dir, filename);
                var unique = 0;
                while (File.Exists(fullPath))
                {
                    unique++;
                    fullPath = Path.Combine(dir, $"crash_envelope_{stamp}_{unique}.json");
                }
                File.WriteAllText(fullPath, json);

                var latestPath = Path.Combine(dir, "crash_envelope_latest.json");
                try { File.WriteAllText(latestPath, json); } catch { }

                return fullPath;
            }
            catch (Exception ex)
            {
                LastFailure = ex.Message;
                try { DiagnosticLogs.Note("errors", "CRASH_ENVELOPE_FAILED " + ex.Message); } catch { }
                return "";
            }
            finally { _dumping = false; }
        }
    }

    private static string ResolveDirectory(string explicitDir)
    {
        // An explicit directory is honoured even before it exists - Dump creates it. Falling
        // back to the journal directory when a named folder was missing silently swallowed
        // the caller's request and left the envelope where nobody would look for it.
        if (!string.IsNullOrWhiteSpace(explicitDir))
            return explicitDir;
        if (!string.IsNullOrWhiteSpace(DiagnosticLogs.DirectoryPath) && Directory.Exists(DiagnosticLogs.DirectoryPath))
            return DiagnosticLogs.DirectoryPath;
        if (Directory.Exists("tmp_playtest"))
            return "tmp_playtest";
        return Directory.GetCurrentDirectory();
    }

    public static CrashEnvelopeData Load(string jsonOrPath)
    {
        if (string.IsNullOrWhiteSpace(jsonOrPath)) return null;
        var json = File.Exists(jsonOrPath) ? File.ReadAllText(jsonOrPath) : jsonOrPath;
        return JsonSerializer.Deserialize<CrashEnvelopeData>(json);
    }

    /// <summary>
    /// Replay the recorded commands sequentially onto a campaign.
    /// </summary>
    public static int Replay(Campaign campaign, CrashEnvelopeData envelope)
    {
        if (campaign == null || envelope?.RecentCommands == null) return 0;
        var applied = 0;
        foreach (var entry in envelope.RecentCommands)
        {
            if (NetworkCommandCodec.TryDecode(entry.TypeName, entry.Payload, out var command))
            {
                command.Apply(campaign);
                applied++;
            }
        }
        return applied;
    }
}
