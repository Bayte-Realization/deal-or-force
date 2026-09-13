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
        if (!DiagnosticLogs.Start(ProjectSettings.GlobalizePath("user://logs")))
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
        if (_engineLog is not null) { OS.RemoveLogger(_engineLog); _engineLog.Dispose(); _engineLog = null; }
        DiagnosticLogs.Stop();
    }

    private void ReportFrameCost(double delta)
    {
        if (!DiagnosticLogs.Enabled) return;
        DiagnosticLogs.SetDay(_campaign.ElapsedDays);
        _frameReportFrames++;
        _frameReportAccumulator += delta;
        if (_frameDurations.Count < 4096) _frameDurations.Add(delta * 1000);
        if (delta >= .1) DiagnosticLogs.Write("performance", $"FRAME_STALL frame_ms={DiagnosticLogs.N(delta * 1000)} speed={_speed} paused={_campaign.IsPaused} pending_seconds={DiagnosticLogs.N(_dayAccumulator)}");
        if (_frameReportAccumulator < 5) return;
        var seconds = _frameReportAccumulator;
        var days = _campaign.ElapsedDays - _frameReportDay;
        _frameReportDay = _campaign.ElapsedDays;
        _frameDurations.Sort();
        var p95 = _frameDurations[(int)Math.Floor((_frameDurations.Count-1)*.95)];
        var maximum = _frameDurations[^1];
        DiagnosticLogs.Write("performance", $"FRAME_WINDOW seconds={DiagnosticLogs.N(seconds)} frames={_frameReportFrames} fps={DiagnosticLogs.N(_frameReportFrames/seconds)} p95_ms={DiagnosticLogs.N(p95)} max_ms={DiagnosticLogs.N(maximum)} speed={_speed} paused={_campaign.IsPaused} network_client={IsNetworkClient} days_per_second={DiagnosticLogs.N(days/seconds)} pending_seconds={DiagnosticLogs.N(_dayAccumulator)} managed_heap_bytes={GC.GetTotalMemory(false)} engine_bytes={OS.GetStaticMemoryUsage()} gc_counts={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} borders_rebuilt={CampaignDiplomacy.Rebuilds-_frameReportRebuilds} map_revisions={_campaign.MapRevision-_frameReportRevision}");
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
}
