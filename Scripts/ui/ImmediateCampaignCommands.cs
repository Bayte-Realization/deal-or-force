using System;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Shared immediate-commit helpers for UI and map-click actions (recruitment, construction,
/// demolition, cancellation, batch undo) so mutations execute immediately even while paused,
/// respect multiplayer <see cref="CampaignCommandQueue.SubmitInterceptor"/> authority, and
/// refresh <see cref="CampaignHudSnapshot"/> + HUD readouts without waiting for the clock to unpause.
/// </summary>
internal static class ImmediateCampaignCommands
{
    /// <summary>
    /// Enqueues and immediately applies a command on <paramref name="world"/> for standalone
    /// views that do not own a <see cref="DealOrForceGame"/> reference. Callers must still
    /// invoke <see cref="ShellContext.RefreshAll"/> so paused HUD snapshots resync.
    /// </summary>
    public static void Commit(Campaign world, ICampaignCommand command)
    {
        if (world is null || command is null) return;
        world.Commands.Enqueue(command);
        world.Commands.ApplyAll(world);
    }
}

public partial class DealOrForceGame
{
    /// <summary>
    /// Commits a single immediate player command inside <see cref="BackgroundSimRunner.RunWrite"/>
    /// (with safe fallback when the background runner is inactive), publishes a fresh
    /// <see cref="CampaignHudSnapshot"/> so TopBar Treasury / Manpower update while paused,
    /// and refreshes the HUD + globe.
    /// </summary>
    internal void CommitImmediateCampaignCommand(ICampaignCommand command, bool refreshHud = true, bool wakeGlobe = false)
    {
        if (_campaign is null || command is null) return;
        CommitImmediateCampaignWrite(c =>
        {
            c.Commands.Enqueue(command);
            c.Commands.ApplyAll(c);
        }, refreshHud, wakeGlobe);
    }

    /// <summary>
    /// Commits a multi-command or batch write inside <see cref="BackgroundSimRunner.RunWrite"/>
    /// with immediate snapshot publication and HUD refresh while paused.
    /// </summary>
    internal void CommitImmediateCampaignWrite(Action<Campaign> write, bool refreshHud = true, bool wakeGlobe = false)
    {
        if (_campaign is null || write is null) return;
        if (_simRunner is not null && _simRunner.IsActive &&
            ReferenceEquals(_simRunner.Campaign, _campaign) &&
            !_campaign.AccessGate.IsWriterHeldByCurrentThread)
        {
            _simRunner.RunWrite(write, refreshSnapshot: true);
        }
        else
        {
            write(_campaign);
            if (_simRunner is not null && ReferenceEquals(_simRunner.Campaign, _campaign) &&
                !_campaign.AccessGate.IsHeldByCurrentThread)
            {
                _simRunner.RefreshSnapshotNow();
            }
        }
        if (wakeGlobe) _globe?.WakeGlobeRendering();
        if (refreshHud)
        {
            Refresh();
            if (IsNewShellActive) RefreshNewShell(force: true);
        }
    }

    /// <summary>
    /// Commits a value-returning batch write inside <see cref="BackgroundSimRunner.RunWrite"/>
    /// with immediate snapshot publication and HUD refresh while paused.
    /// </summary>
    internal TResult CommitImmediateCampaignWrite<TResult>(Func<Campaign, TResult> write, bool refreshHud = true, bool wakeGlobe = false)
    {
        if (_campaign is null || write is null) return default;
        TResult result;
        if (_simRunner is not null && _simRunner.IsActive &&
            ReferenceEquals(_simRunner.Campaign, _campaign) &&
            !_campaign.AccessGate.IsWriterHeldByCurrentThread)
        {
            result = _simRunner.RunWrite(write, refreshSnapshot: true);
        }
        else
        {
            result = write(_campaign);
            if (_simRunner is not null && ReferenceEquals(_simRunner.Campaign, _campaign) &&
                !_campaign.AccessGate.IsHeldByCurrentThread)
            {
                _simRunner.RefreshSnapshotNow();
            }
        }
        if (wakeGlobe) _globe?.WakeGlobeRendering();
        if (refreshHud)
        {
            Refresh();
            if (IsNewShellActive) RefreshNewShell(force: true);
        }
        return result;
    }

    /// <summary>
    /// Ensures the cached <see cref="CampaignHudSnapshot"/> never lags behind live player
    /// Treasury / Manpower / PoliticalPoints while the simulation clock is paused.
    /// </summary>
    private void SyncPausedHudSnapshotIfStale()
    {
        if (_campaign is null || !_campaign.IsPaused || _campaign.Player is not { } player) return;
        if (_simRunner is null || !_simRunner.IsActive || !ReferenceEquals(_simRunner.Campaign, _campaign)) return;
        if (_campaign.AccessGate.IsHeldByCurrentThread) return;

        var hs = _simRunner.ReadHudSnapshot(_campaign);
        if (hs.Version <= 0 ||
            hs.PlayerId != player.Id ||
            MathF.Abs(hs.Treasury - player.Treasury) > 1e-6f ||
            MathF.Abs(hs.ManpowerReserve - player.ManpowerReserve) > 1e-4f ||
            MathF.Abs(hs.PoliticalPoints - player.PoliticalPoints) > 1e-5f ||
            hs.ElapsedDays != _campaign.ElapsedDays ||
            hs.MapRevision != _campaign.MapRevision ||
            hs.OwnershipRevision != _campaign.OwnershipRevision)
        {
            _simRunner.RefreshSnapshotNow();
        }
    }
}
