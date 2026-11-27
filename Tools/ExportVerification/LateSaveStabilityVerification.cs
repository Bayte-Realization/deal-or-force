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
        if (OS.GetCmdlineUserArgs().Contains("--verify-late-save-quicksave"))
        {
            await VerifyLateQuicksave(game, Path.GetDirectoryName(fullPath)!);
            return;
        }
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
                $"speed={world.Speed} private_bytes={process.PrivateMemorySize64} managed_bytes={GC.GetTotalMemory(false)} " +
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

    private async Task VerifyLateQuicksave(DealOrForceGame game, string root)
    {
        var world = game.VerificationWorld;
        var deadline = Time.GetTicksMsec() + 10_000;
        while (!game.VerificationOwnsBackgroundSave && Time.GetTicksMsec() < deadline) await Wait(.05);
        Check(game.VerificationOwnsBackgroundSave, "late campaign uses the background save path");
        if (!game.VerificationOwnsBackgroundSave) return;

        var path = Path.Combine(root, SaveSystem.QuickSlot + SaveSystem.Extension);
        Check(!File.Exists(path), "quicksave verification starts with an empty private slot");
        if (File.Exists(path)) return;
        var previous = game.VerificationPendingSave;
        Task save;
        double dispatchMs;
        world.AccessGate.EnterWrite();
        try
        {
            using var key = new InputEventKey { Pressed = true, Keycode = Key.F5 };
            var dispatch = Stopwatch.GetTimestamp();
            game._Input(key);
            dispatchMs = Stopwatch.GetElapsedTime(dispatch).TotalMilliseconds;
            save = game.VerificationPendingSave;
            Check(!ReferenceEquals(save, previous) && !save.IsCompleted && !File.Exists(path),
                "F5 queues the save while the current game frame still owns the campaign");
        }
        finally { world.AccessGate.ExitWrite(); }
        await save.WaitAsync(TimeSpan.FromSeconds(45));

        var loaded = await Task.Run(() => SaveSystem.Load(root, SaveSystem.QuickSlot, "", world.ProvinceMap.ProvinceCount));
        Check(loaded.Success && loaded.Campaign.Date.Equals(world.Date) && loaded.Campaign.ElapsedHours == world.ElapsedHours &&
              loaded.Metadata.NationTag == world.Player.Tag && world.IsPaused,
            "the production quicksave restores the paused late campaign at its captured date");
        GD.Print(FormattableString.Invariant($"LATE_QUICKSAVE dispatch_ms={dispatchMs:F3} bytes={new FileInfo(path).Length}"));
    }
}

public partial class DealOrForceGame
{
    internal bool VerificationOwnsBackgroundSave => WantsBackgroundSimulation && _simRunner.IsActive &&
        ReferenceEquals(_simRunner.Campaign, _campaign);
    internal Task VerificationPendingSave => _pendingSave;

    internal void VerificationStartMaximumSpeed()
    {
        _frameReportAccumulator = 0; _frameReportFrames = 0; _frameDurations.Clear();
        _frameReportDay = _campaign.ElapsedDays; _lastProfiles.Clear();
        SetSpeed(Math.Max(1, _campaign?.Defines?.MaxSpeed ?? 8));
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
