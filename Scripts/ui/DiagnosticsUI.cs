using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private GameDiagnosticLogger _engineLog;
    private readonly List<double> _frameDurations = new(600);
    private readonly Dictionary<string, (double Time, long Allocations, int Runs)> _lastProfiles = new();

    private void StartDiagnostics()
    {
        // The journal goes where the player's other files are, so `logs/` holds the engine log
        // and these channels side by side instead of the two folders it used to be.
        if (!DiagnosticLogs.Start(GamePaths.Logs))
        {
            if (DiagnosticLogs.LastFailure.Length > 0) GD.PushWarning("Diagnostic files unavailable: " + DiagnosticLogs.LastFailure);
            return;
        }
        _engineLog = new GameDiagnosticLogger();
        OS.AddLogger(_engineLog);
        DiagnosticLogs.Write("system", $"ENGINE version={Engine.GetVersionInfo()} renderer={RenderingServer.GetCurrentRenderingMethod()} gpu={RenderingServer.GetVideoAdapterName()} cpu={OS.GetProcessorName()} viewport={GetViewport().GetVisibleRect().Size} game={SaveSystem.GameVersion} map={SaveSystem.MapDataVersion}");
    }

    public override void _ExitTree()
    {
        Loc.LanguageChanged -= RefreshHandbookLanguage;
        if (_stepCompletionCampaign is not null)
            _stepCompletionCampaign.StepCompleted -= CampaignStepCompleted;
        _stepCompletionCampaign = null;
        // A save still being written finishes before the game goes: it is the one the player
        // just asked for, or the month's autosave, and quitting is not a reason to lose it.
        WaitForPendingSave();
        _modLoader.StopCode();
        if (_engineLog is not null) { OS.RemoveLogger(_engineLog); _engineLog.Dispose(); _engineLog = null; }
        DiagnosticLogs.Stop();
    }

    private ulong _lastFrameUsec;

    private void ReportFrameCost(double delta)
    {
        if (!DiagnosticLogs.Enabled) return;
        DiagnosticLogs.SetDay(_campaign.ElapsedDays);
        _frameReportFrames++;
        _frameReportAccumulator += delta;
        if (_frameDurations.Count < 4096) _frameDurations.Add(delta * 1000);
        // Wall time, not delta: the engine caps delta, so every stall longer than 150 ms was
        // written as 150 and the worst hitches - the ones a player calls a freeze - looked alike.
        var now = Time.GetTicksUsec();
        var wall = _lastFrameUsec == 0 ? delta * 1000 : (now - _lastFrameUsec) / 1000.0;
        _lastFrameUsec = now;
        if (wall >= 100) DiagnosticLogs.Write("performance", $"FRAME_STALL frame_ms={DiagnosticLogs.N(wall)} speed={_speed} paused={_campaign.IsPaused} pending_seconds={DiagnosticLogs.N(_dayAccumulator)} " +
            $"after={ButtonMotion.LastPressed} {DiagnosticLogs.N((now - ButtonMotion.LastPressedUsec) / 1000.0)}ms_ago");
        if (_frameReportAccumulator < 5) return;
        var seconds = _frameReportAccumulator;
        var days = _campaign.ElapsedDays - _frameReportDay;
        _frameReportDay = _campaign.ElapsedDays;
        _frameDurations.Sort();
        var p95 = _frameDurations[(int)Math.Floor((_frameDurations.Count-1)*.95)];
        var maximum = _frameDurations[^1];
        DiagnosticLogs.Write("performance", $"FRAME_WINDOW seconds={DiagnosticLogs.N(seconds)} frames={_frameReportFrames} fps={DiagnosticLogs.N(_frameReportFrames/seconds)} p95_ms={DiagnosticLogs.N(p95)} max_ms={DiagnosticLogs.N(maximum)} speed={_speed} paused={_campaign.IsPaused} network_client={IsNetworkClient} days_per_second={DiagnosticLogs.N(days/seconds)} pending_seconds={DiagnosticLogs.N(_dayAccumulator)} managed_heap_bytes={GC.GetTotalMemory(false)} engine_bytes={OS.GetStaticMemoryUsage()} video_bytes={(long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed)} texture_bytes={(long)Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed)} buffer_bytes={(long)Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed)} objects={(long)Performance.GetMonitor(Performance.Monitor.ObjectCount)} resources={(long)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount)} nodes={(long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)} orphans={(long)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount)} process_bytes={PrivateBytes()} gc_counts={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} borders_rebuilt={CampaignDiplomacy.Rebuilds-_frameReportRebuilds} map_revisions={_campaign.MapRevision-_frameReportRevision}");
        _frameReportRebuilds = CampaignDiplomacy.Rebuilds;
        _frameReportRevision = _campaign.MapRevision;
        foreach (var sample in SimProfiler.All.OrderBy(s => s.Name))
        {
            var last = _lastProfiles.GetValueOrDefault(sample.Name);
            var calls = sample.Runs-last.Runs;
            var elapsed = sample.TotalMilliseconds-last.Time;
            if (calls > 0) DiagnosticLogs.Write("performance", $"CALC_WINDOW name={sample.Name} calls={calls} total_ms={DiagnosticLogs.N(elapsed)} mean_ms={DiagnosticLogs.N(elapsed/calls)} last_ms={DiagnosticLogs.N(sample.LastMilliseconds)} allocated_bytes={sample.AllocatedBytes-last.Allocations}");
            _lastProfiles[sample.Name] = (sample.TotalMilliseconds, sample.AllocatedBytes, sample.Runs);
        }
        _frameReportAccumulator = 0;
        _frameReportFrames = 0;
        _frameDurations.Clear();
    }

    /// <summary>
    /// The whole process's private memory - what the player sees in the task manager. The heap
    /// and the engine's own allocator were a tenth of it in a long 2046 campaign; the rest is the
    /// renderer and the driver, so the log carries their counters beside it to show which grows.
    /// </summary>
    /// <summary>Where the memory is (MemoryReport); for tools that drive the game, and the console's `memory`.</summary>
    public string MemoryReport(int top = 25) => DealOrForce.MemoryReport.Build(top);

    private static long PrivateBytes()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }
}
