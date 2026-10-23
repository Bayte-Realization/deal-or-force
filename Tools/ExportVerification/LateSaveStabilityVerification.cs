using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    // Explicit development-only probe of a private copied fixture. Normal launches
    // neither read a save nor advance it, and the player's archive is never a target.
    private async Task VerifyLateSaveStability(DealOrForceGame game, string path, int days)
    {
        var fullPath = Path.GetFullPath(path);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Check(fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath),
            "late-save renderer probe only accepts an existing private temporary copy");
        if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return;
        await game.VerificationRestoreCopiedSave(fullPath);
        var world = game.VerificationWorld;
        var start = world.ElapsedDays;
        Check(world.IsPaused && world.ProvinceMap is not null, "copied late campaign loads paused with the real province map");
        if (world.ProvinceMap is null) return;
        if (world.NationByTag("RUS") is { } identity)
            GD.Print($"LATE_SAVE_IDENTITY tag={identity.Tag} name={identity.Name} display={identity.DisplayName} " +
                $"formed={identity.FormedNationId} colour={identity.Color} people={identity.Population}");
        game.VerificationFocusAdministration(world.Player);
        GetTree().Root.Size = new(1920, 1080);
        await Wait(.5);
        if (OS.GetCmdlineUserArgs().Contains("--verify-late-save-realtime"))
        {
            // Drive the actual _Process/StepFor loop, not synchronous TickDay calls.
            // Keep diagnostic output alongside the private fixture as well.
            DiagnosticLogs.Stop();
            DiagnosticLogs.Start(Path.Combine(Path.GetDirectoryName(fullPath)!, "native-realtime-logs"));
            game.VerificationStartMaximumSpeed();
            var realtime = Stopwatch.StartNew();
            while (world.ElapsedDays < start + days && world.SimulationFault.Length == 0 && realtime.Elapsed.TotalSeconds < 120)
                await Wait(.2);
            world.IsPaused = true;
            world.FinishStep();
            Check(world.ElapsedDays >= start + days && world.SimulationFault.Length == 0,
                "late save advances through actual high-speed frames without a campaign fault");
            using var process = Process.GetCurrentProcess(); process.Refresh();
            GD.Print($"LATE_SAVE_REALTIME_RESULT start={start} end={world.ElapsedDays} seconds={realtime.Elapsed.TotalSeconds:0.###} " +
                $"speed=5 private_bytes={process.PrivateMemorySize64} managed_bytes={GC.GetTotalMemory(false)} " +
                $"logs={DiagnosticLogs.DirectoryPath} failures={_failures}");
            await Capture("late-save-realtime");
            return;
        }
        var repairsBefore = Warfare.RepairedBattleAssignments;
        for (var day = 0; day < days; day++)
        {
            world.IsPaused = false;
            try { world.TickDay(); world.FinishStep(); }
            finally { world.IsPaused = true; }
            // Draw every simulated day. This exercises actual labels, counters and
            // deferred frees instead of calling a headless simulation a renderer test.
            await Wait(.04);
            if (world.ElapsedDays != start + day + 1 || world.SimulationFault.Length > 0)
            {
                Check(false, "late-save native day completes without simulation fault");
                break;
            }
            if ((day + 1) % 10 == 0 || day + 1 == days)
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                GD.Print($"LATE_SAVE_NATIVE day={world.ElapsedDays} days={day + 1} formations={world.Armies.Count} " +
                    $"private_bytes={process.PrivateMemorySize64} working_set={process.WorkingSet64} managed_bytes={GC.GetTotalMemory(false)}");
            }
        }
        Check(world.ElapsedDays == start + days && world.SimulationFault.Length == 0,
            "late-save native renderer completes requested days without a campaign fault");
        Check(world.IsPaused, "late-save probe leaves the private copied campaign paused");
        GD.Print($"LATE_SAVE_NATIVE_RESULT start={start} end={world.ElapsedDays} requested_days={days} " +
            $"repaired_assignments={Warfare.RepairedBattleAssignments - repairsBefore} failures={_failures}");
        await Capture("late-save-stability");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationStartMaximumSpeed()
    {
        _frameReportAccumulator = 0; _frameReportFrames = 0; _frameDurations.Clear();
        _frameReportDay = _campaign.ElapsedDays; _lastProfiles.Clear();
        SetSpeed(5);
    }

    internal async Task VerificationRestoreCopiedSave(string path)
    {
        // Override BEFORE the real load path. Even an autosave or UI callback in
        // this process can write only beside the disposable fixture, not Documents.
        _saveRoot = Path.GetDirectoryName(path)!;
        var restored = await RunLoadingOperation("Restoring a campaign", async () =>
        {
            await RestoreCampaignFromSlot(Path.GetFileNameWithoutExtension(path));
            _campaign.IsPaused = true;
            await WaitForMapPresentation();
        }, canReturn: true);
        if (!restored) throw new InvalidOperationException("Private late-save fixture could not be restored.");
        Refresh();
    }
}
