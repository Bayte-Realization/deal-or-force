using System;
using System.IO;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyNetworkRecoveryPeer(DealOrForceGame game, string[] args)
    {
        var role = Array.IndexOf(args, "--verify-scenario-peer");
        var host = role + 1 < args.Length && args[role + 1] == "host";
        var signalAt = Array.IndexOf(args, "--verify-recovery-signal");
        Check(signalAt >= 0 && signalAt + 1 < args.Length && game.VerificationNetworkStarted,
            "recovery proof has an isolated signal path and two started ENet peers");
        if (signalAt < 0 || signalAt + 1 >= args.Length || !game.VerificationNetworkStarted) return;
        var signal = args[signalAt + 1];
        var expected = CampaignFingerprint.Of(game.VerificationWorld);
        var pausedHour = game.VerificationWorld.ElapsedHours;
        Check(game.VerificationWorld.IsPaused, "recovery proof keeps the authoritative clock paused");
        if (host)
        {
            try
            {
                var deadline = Time.GetTicksMsec() + 120_000;
                while (!File.Exists(signal) && Time.GetTicksMsec() < deadline) await Wait(.1);
                Check(File.Exists(signal) && File.ReadAllText(signal) == "PASS", "real ENet client recovered twice while the world stayed paused");
                Check(game.VerificationNetworkRecoveryEntries == 1, "host recovery budget retains only its registered remote peer");
                Check(CampaignFingerprint.Of(game.VerificationWorld) == expected, "recovery requests do not mutate the host's world");
            }
            finally { File.WriteAllText(signal + ".host-done", _failures == 0 ? "PASS" : "FAIL"); }
        }
        else
        {
            try
            {
                var before = game.VerificationWorld;
                before.Player.CustomName = "verification-only divergent client";
                Check(CampaignFingerprint.Of(before) != expected, "the client has a real divergence for snapshot recovery to repair");
                game.VerificationRequestRecovery("verify-recovery-first");
                for (var i = 0; i < 40; i++)
                {
                    game.VerificationRequestRecovery("verify-recovery-client-duplicate");
                    game.VerificationBypassClientRecoveryLimiter("verify-recovery-host-burst");
                }
                Check(game.VerificationNetworkRecoveryEntries == 1, "a client burst reuses one outgoing cooldown entry");
                var deadline = Time.GetTicksMsec() + 90_000;
                while (ReferenceEquals(before, game.VerificationWorld) && Time.GetTicksMsec() < deadline) await Wait(.1);
                Check(!ReferenceEquals(before, game.VerificationWorld) && CampaignFingerprint.Of(game.VerificationWorld) == expected,
                    "the first real authoritative snapshot repairs the divergent client");
                await Wait(NetworkRecoveryThrottle.RetrySeconds + .25);
                before = game.VerificationWorld;
                Check(before.ElapsedHours == pausedHour && before.IsPaused,
                    "recovery retry does not need the campaign calendar to move");
                game.VerificationRequestRecovery("verify-recovery-second");
                deadline = Time.GetTicksMsec() + 90_000;
                while (ReferenceEquals(before, game.VerificationWorld) && Time.GetTicksMsec() < deadline) await Wait(.1);
                Check(!ReferenceEquals(before, game.VerificationWorld) && CampaignFingerprint.Of(game.VerificationWorld) == expected,
                    "a second real snapshot arrives after cooldown at the same paused hour");
            }
            finally { File.WriteAllText(signal, _failures == 0 ? "PASS" : "FAIL"); }
            var hostDeadline = Time.GetTicksMsec() + 30_000;
            while (!File.Exists(signal + ".host-done") && Time.GetTicksMsec() < hostDeadline) await Wait(.1);
            Check(File.Exists(signal + ".host-done") && File.ReadAllText(signal + ".host-done") == "PASS",
                "the host independently verifies unchanged state before either peer closes");
        }
        game.VerificationCloseRecoveryTransport();
        Check(game.VerificationNetworkRecoveryEntries == 0, "closing the real transport clears transient recovery budgets");
    }
}

public partial class DealOrForceGame
{
    internal int VerificationNetworkRecoveryEntries => _networkRecoveryRequests.Count;
    internal void VerificationRequestRecovery(string reason) => RequestNetworkResync(reason);
    internal void VerificationBypassClientRecoveryLimiter(string reason)
        => RpcId(1, nameof(ServerRequestNetworkResync), reason);
    internal void VerificationCloseRecoveryTransport() => ShutdownNetworkPeer();
}
