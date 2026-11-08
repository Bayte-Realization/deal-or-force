using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyNetworkCatchUpPeer(DealOrForceGame game, string[] args)
    {
        var role = Array.IndexOf(args, "--verify-scenario-peer");
        var host = args[role + 1] == "host";
        var at = Array.IndexOf(args, "--verify-recovery-signal");
        Check(at >= 0 && at + 1 < args.Length && game.VerificationNetworkStarted, "catch-up proof has two started ENet peers and private signals");
        if (at < 0 || at + 1 >= args.Length || !game.VerificationNetworkStarted) return;
        var signal = args[at + 1];
        if (host)
        {
            try
            {
                Check(await PeerSignal(signal + ".ready"), "client installed the forced-yield fixture before the host sends steps");
                game.VerificationSendCatchUpBurst(true);
                File.WriteAllText(signal + ".target", CampaignFingerprint.Of(game.VerificationWorld).ToString());
                Check(await PeerSignal(signal + ".phase-a"), "client replayed the mixed-cadence burst without a snapshot");
                Check(game.VerificationNetworkRecoveryEntries == 0, "host prepared no recovery snapshot for ordinary staged catch-up");
                Check(await PeerSignal(signal + ".phase-b-ready"), "client made the later checksum mismatch before the next burst");
                game.VerificationSendCatchUpBurst(false);
                var expected = CampaignFingerprint.Of(game.VerificationWorld);
                File.WriteAllText(signal + ".final-target", expected.ToString());
                Check(await PeerSignal(signal), "client repaired a deferred-checksum divergence through real ENet recovery");
                Check(game.VerificationNetworkRecoveryEntries == 1 && CampaignFingerprint.Of(game.VerificationWorld) == expected,
                    "the host admitted one recovery and its own state stayed unchanged");
            }
            finally { File.WriteAllText(signal + ".host-done", _failures == 0 ? "PASS" : "FAIL"); }
        }
        else
        {
            try
            {
                Check(game.VerificationCatchUpReceiptIsCheap(), "clock receipt queues a target without executing or partially starting a tick");
                Check(game.VerificationHeartbeatKeepsRecovery(), "an overtaking heartbeat changes neither cadence nor the recovery warning");
                game.VerificationWorld.Defines.FrameSimulationBudgetMs = 0; // force one phase per frame, not a timing threshold
                game.VerificationWorld.Defines.MaxDaysPerFrame = 1;
                File.WriteAllText(signal + ".ready", "PASS");
                Check(await PeerSignalExists(signal + ".target"), "the real host published the first completed target");
                var before = game.VerificationWorld; var sawPartial = false; var sawHeld = false;
                var deadline = Time.GetTicksMsec() + 240_000;
                while (Time.GetTicksMsec() < deadline &&
                       (game.VerificationWorld.ElapsedHours < 50 || game.VerificationWorld.MidStep || game.VerificationCatchUpCount > 0))
                {
                    sawPartial |= game.VerificationWorld.MidStep;
                    sawHeld |= game.VerificationHeldNetworkCount > 0;
                    await Wait(.001);
                }
                Check(sawPartial && sawHeld, "real ENet catch-up yields with a staged world and held ordered items across frames");
                Check(ReferenceEquals(before, game.VerificationWorld) && game.VerificationWorld.ElapsedHours == 50 &&
                      !game.VerificationWorld.MidStep && game.VerificationCatchUpCount == 0 &&
                      game.VerificationHeldNetworkCount == 0 && game.VerificationNetworkRecoveryEntries == 0,
                    "mixed hourly/six-hour/daily replay finishes exactly at hour 50 without recovery or leftover commands");
                Check(CampaignFingerprint.Of(game.VerificationWorld).ToString() == File.ReadAllText(signal + ".target"),
                    "the real staged client matches the host after queued commands and checksums");
                Check(game.VerificationWorld.IsPaused && !game.VerificationApplyingNetworkCommand,
                    "frame catch-up restores the host's pause and the transport guard");
                File.WriteAllText(signal + ".phase-a", _failures == 0 ? "PASS" : "FAIL");
                before = game.VerificationWorld;
                before.Player.CustomName = "verification-only deferred-checksum divergence";
                File.WriteAllText(signal + ".phase-b-ready", "PASS");
                Check(await PeerSignalExists(signal + ".final-target"), "the host supplied the second burst and checksum");
                deadline = Time.GetTicksMsec() + 240_000;
                while (ReferenceEquals(before, game.VerificationWorld) && Time.GetTicksMsec() < deadline) await Wait(.01);
                Check(!ReferenceEquals(before, game.VerificationWorld) &&
                      CampaignFingerprint.Of(game.VerificationWorld).ToString() == File.ReadAllText(signal + ".final-target"),
                    "a checksum received ahead of an unfinished client is compared later and triggers a real repairing snapshot");
                Check(game.VerificationCatchUpCount == 0 && game.VerificationHeldNetworkCount == 0,
                    "snapshot adoption discards all superseded clock segments, checksums and commands");
            }
            finally { File.WriteAllText(signal, _failures == 0 ? "PASS" : "FAIL"); }
            Check(await PeerSignal(signal + ".host-done"), "the host independently confirmed the final state before disconnect");
        }
        game.VerificationCloseRecoveryTransport();
        Check(game.VerificationCatchUpCount == 0 && game.VerificationHeldNetworkCount == 0 && game.VerificationNetworkRecoveryEntries == 0,
            "closing the real transport releases all transient catch-up/recovery queues");
    }

    private async Task<bool> PeerSignalExists(string path)
    {
        var deadline = Time.GetTicksMsec() + 240_000;
        while (Time.GetTicksMsec() < deadline)
        {
            try { if (File.Exists(path) && new FileInfo(path).Length > 0) return true; }
            catch (IOException) { }
            await Wait(.05);
        }
        return false;
    }
    private async Task<bool> PeerSignal(string path) => await PeerSignalExists(path) && File.ReadAllText(path) == "PASS";
}

public partial class DealOrForceGame
{
    internal int VerificationCatchUpCount => _networkCatchUp.Count;
    internal int VerificationHeldNetworkCount => _networkDeferred.Count;
    internal bool VerificationApplyingNetworkCommand => _applyingNetworkCommand;
    internal bool VerificationCatchUpReceiptIsCheap()
    {
        var fingerprint = CampaignFingerprint.Of(_campaign);
        ClientAdvanceNetworkHours(_campaign.ElapsedHours + 1, 1);
        var cheap = _networkCatchUp.Count == 1 && !_campaign.MidStep && CampaignFingerprint.Of(_campaign) == fingerprint;
        _networkCatchUp.Clear();
        return cheap;
    }
    internal bool VerificationHeartbeatKeepsRecovery()
    {
        var step = _campaign.TickHours;
        _networkResyncPending = true;
        _networkSyncNotice = Loc.Format("DESYNCHRONISED on day {0:N0} · asking the host for the world", _campaign.ElapsedDays);
        ClientReceiveNetworkClock(_campaign.ElapsedDays, true, 1, step == 240 ? 1 : 240);
        var kept = _campaign.TickHours == step && _networkHudStatus.Text == _networkSyncNotice;
        _networkResyncPending = false; _networkSyncNotice = ""; _networkClientStatus = null;
        return kept;
    }
    internal void VerificationSendCatchUpBurst(bool mixed)
    {
        BindStepCompletion();
        var cadence = mixed ? Enumerable.Repeat(1, 12).Concat(new[] { 6, 6, 24, 1, 1 }).ToArray() : Enumerable.Repeat(1, 6).ToArray();
        for (var i = 0; i < cadence.Length; i++)
        {
            if (i is 0 or 5 or 14)
            {
                _campaign.Commands.Enqueue(new SetTaxRateCommand { IssuerId = _campaign.Player.Id, Value = .21f + i * .001f });
                _campaign.Commands.ApplyAll(_campaign);
            }
            _campaign.Defines.HoursPerTick = cadence[i];
            _campaign.IsPaused = false;
            _campaign.Tick();
            _campaign.IsPaused = true;
            Rpc(nameof(ClientCheckNetworkSync), _campaign.ElapsedHours, CampaignFingerprint.Of(_campaign).ToString());
        }
    }
}
