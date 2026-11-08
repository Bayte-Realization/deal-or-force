using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using DealOrForce.Simulation;

internal static class NetworkCatchUpTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL network catch-up: " + label);
        Console.WriteLine("PASS network catch-up: " + label);
    }

    public static void Run()
    {
        var clock = new NetworkCatchUp();
        var all = true;
        for (var hour = 1; hour <= 20000; hour++) all &= clock.Offer(hour, 1, 0);
        Check(all && clock.Count == 1 && clock.LatestHours == 20000, "twenty thousand hourly targets coalesce into one segment, not twenty thousand jobs");
        Check(clock.TryNext(20000, true, out var held) && held.StepHours == 1,
            "an advanced clock does not retire an unfinished staged step");
        Check(!clock.TryNext(20000, false, out _) && clock.Count == 0, "only a stable completed world retires the segment");
        Check(clock.Offer(19999, 24, 20000) && clock.Count == 0 && clock.LatestHours == 20000, "obsolete messages never rewind or change cadence");
        clock.Clear();
        Check(clock.Count == 0 && clock.LatestHours == -1 && clock.Offer(1, 1, 0), "snapshot/session clear forgets the old target horizon");
        Check(clock.Offer(25, 24, 0) && clock.Offer(26, 1, 0) && clock.Count == 3, "cadence transitions are not collapsed across each other");
        Check(clock.TryNext(0, false, out var one) && one == new NetworkCatchUp.Target(1, 1), "the first hour retains its original step");
        Check(clock.TryNext(1, false, out var day) && day == new NetworkCatchUp.Target(25, 24), "a daily step starts only after the earlier hourly step");
        Check(clock.TryNext(25, false, out var last) && last == new NetworkCatchUp.Target(26, 1), "switching back to hourly steps preserves the next boundary");
        Check(!clock.Offer(28, 3, 25) && clock.LatestHours == 26, "an unreachable target does not poison the accepted horizon");
        Check(!clock.Offer(-1, 1, 0) && !clock.Offer(27, 0, 0) && !clock.Offer(27, 241, 0) && !clock.Offer(27, 1, -1),
            "invalid hours and unsupported step sizes are refused");
        clock.Clear();
        Check(clock.Offer(480, 24, 0) && clock.NeedsSnapshot(0, 20) && !clock.NeedsSnapshot(1, 20), "the snapshot horizon is exact, not rounded campaign days");
        clock.Clear();
        long target = 0;
        for (var i = 0; i < NetworkCatchUp.MaximumSegments; i++) { var step = 1 + i % 2; target += step; all &= clock.Offer(target, step, 0); }
        Check(all && !clock.Offer(target + 1, 1, 0) && clock.Count == NetworkCatchUp.MaximumSegments && clock.LatestHours == target,
            "a pathological cadence burst is bounded without silently dropping an earlier segment");
        Check(NetworkCatchUp.FrameBudget(0) == 0 && NetworkCatchUp.FrameBudget(3.5) == 3.5 &&
              NetworkCatchUp.FrameBudget(double.NaN) == 9 && NetworkCatchUp.FrameBudget(-1) == 9,
            "frame budgets preserve a forced-yield fixture and reject invalid configuration");
        var orders = new NetworkHeldOrders(); var ran = 0;
        orders.Hold(24, () => ran++); orders.Hold(24, null);
        Check(ran == 0 && orders.Count == 1, "receipt into an unstable world does not run a due command");
        Check(orders.ApplyDue(24) == 1 && ran == 1 && orders.Count == 0, "the command runs once after the stable boundary");
        StagedReplay();
    }

    private static Campaign World()
    {
        var world = Campaign.CreateBootstrap(61109);
        var country = world.NationByTag("KOR"); world.ConfigureSinglePlayer(country.Id);
        var state = world.StatesOf(country).First();
        world.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)state.MapIndex, 16).ToArray(),
            Enumerable.Repeat((ushort)country.MapIndex, 16).ToArray(), 4, 4, _ => false));
        world.Armies.Clear(); world.ObserverMode = true; world.Defines.EventIntervalDays = 0;
        world.IsPaused = false;
        return world;
    }

    private static void StagedReplay()
    {
        var host = World();
        var client = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(host, "catch-up"), "catch-up").Campaign;
        client.AttachProvinceTopology(host.ProvinceMap); // the real client's globe reattaches its local immutable map after import
        Check(client is not null && CampaignFingerprint.Of(client) == CampaignFingerprint.Of(host), "replay begins from the real save/network snapshot contract");
        var targets = new NetworkCatchUp(); var deferred = new NetworkHeldOrders();
        var hostHooks = 0; var clientHooks = 0; var compared = 0;
        host.StepCompleted += world => { if (world.StepOpensNewDay) { hostHooks++; world.Player.CustomName = "day " + world.ElapsedDays; } };
        client.StepCompleted += world =>
        {
            if (world.StepOpensNewDay) { clientHooks++; world.Player.CustomName = "day " + world.ElapsedDays; }
            deferred.ApplyDue(world.ElapsedHours);
        };
        var cadence = Enumerable.Repeat(1, 12).Concat(new[] { 6, 6, 24, 1, 1 }).ToArray();
        for (var i = 0; i < cadence.Length; i++)
        {
            if (i is 0 or 5 or 14)
            {
                var tax = .21f + i * .001f;
                host.Commands.Enqueue(new SetTaxRateCommand { IssuerId = host.Player.Id, Value = tax }); host.Commands.ApplyAll(host);
                deferred.ApplyOrHold(host.ElapsedHours, client.ElapsedHours, () =>
                { client.Commands.Enqueue(new SetTaxRateCommand { IssuerId = client.Player.Id, Value = tax }); client.Commands.ApplyAll(client); });
            }
            host.Defines.HoursPerTick = cadence[i]; host.Tick();
            var hour = host.ElapsedHours; var fingerprint = CampaignFingerprint.Of(host);
            var reference = SaveSystem.ExportScenarioJson(host, "catchup-fixture");
            Check(targets.Offer(hour, cadence[i], client.ElapsedHours), "authoritative mixed-cadence target accepted at hour " + hour);
            deferred.Hold(hour, () =>
            {
                if (CampaignFingerprint.Of(client) != fingerprint)
                {
                    var evidence = Path.Combine(Path.GetTempPath(), "dof-catchup-diff-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(evidence);
                    File.WriteAllText(Path.Combine(evidence, "host.json"), reference);
                    File.WriteAllText(Path.Combine(evidence, "client.json"), SaveSystem.ExportScenarioJson(client, "catchup-fixture"));
                    Console.WriteLine("Catch-up mismatch evidence: " + evidence);
                }
                Check(!client.MidStep && client.ElapsedHours == hour && CampaignFingerprint.Of(client) == fingerprint,
                    "deferred checksum matches only the fully completed authoritative hour " + hour);
                compared++;
            });
        }
        var resumes = 0;
        while (targets.TryNext(client.ElapsedHours, client.MidStep, out var next))
        {
            if (!client.MidStep) client.Defines.HoursPerTick = next.StepHours;
            client.StepFor(0); // deterministic forced yield, not a hardware timing assertion
            if (++resumes > 20000) throw new Exception("catch-up replay exceeded its bounded phase count");
        }
        Check(resumes > cadence.Length && compared == cadence.Length && deferred.Count == 0,
            "catch-up spans many phase slices without skipping or repeating checks/commands");
        Check(hostHooks == 2 && clientHooks == hostHooks && CampaignFingerprint.Of(client) == CampaignFingerprint.Of(host) && client.Random.State == host.Random.State,
            "mixed hourly/daily replay preserves world, RNG and exactly-once daily hooks");
    }
}
