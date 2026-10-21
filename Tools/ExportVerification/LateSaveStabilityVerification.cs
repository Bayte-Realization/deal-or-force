using System;
using System.Diagnostics;
using System.IO;
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
        game.VerificationFocusAdministration(world.Player);
        GetTree().Root.Size = new(1920, 1080);
        await Wait(.5);
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
