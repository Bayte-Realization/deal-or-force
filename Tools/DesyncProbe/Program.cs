using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.DesyncProbe;

/// <summary>
/// A one-purpose probe for the bench's "a client sent the world is in the world it was
/// sent" check: two worlds agree at a snapshot and drift apart within days. The bench
/// only says WHICH NUMBER first disagreed; this says WHAT BEHIND IT did, by comparing the
/// whole long-range establishment - and every day's battles - between host and client
/// until the first difference shows.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var daysBefore = args.Length > 0 && int.TryParse(args[0], out var before) ? before : 120;
        var daysAfter = args.Length > 1 && int.TryParse(args[1], out var after) ? after : 12;

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        MapRasterCatalog.ResetCatalog();
        HydrologyCatalog.ResetCatalog();

        var host = Campaign.CreateBootstrap(20240808);
        if (!Attach(host)) return 1;
        host.IsPaused = false;
        for (var day = 0; day < daysBefore; day++) host.TickDay();
        Console.WriteLine($"host: {daysBefore} days, {host.SupportFormations.Count:N0} long-range formations, " +
                          $"{host.SupportOrders.Count:N0} order(s), {host.Wars.Count(w => !w.Ended)} wars running.");

        var wire = SaveSystem.ExportNetworkSnapshot(host, "bench");
        var result = SaveSystem.ImportNetworkSnapshot(wire, "bench");
        if (result.Campaign is null) { Console.WriteLine("snapshot would not load: " + result.Warning); return 1; }
        var client = result.Campaign;
        if (!Attach(client)) return 1;

        // What came off the wire, before either world steps: the support establishment is
        // the part of the snapshot the fingerprint does not hash, so it is checked by hand.
        var mismatch = CompareSupport(host, client);
        if (mismatch is not null) { Console.WriteLine("SUPPORT MISMATCH AT SNAPSHOT: " + mismatch); return 1; }
        Console.WriteLine("support establishment survived the snapshot exactly.");
        // The experiment arm: zero every define the long-range forces act through, on both
        // worlds alike. If the drift survives this, the support system is not what causes
        // it - and whatever the user's tree already carried is the thing to look at.
        if (args.Contains("noeffects"))
        {
            foreach (var world in new[] { host, client })
            {
                world.Defines.MaxSupportFireBonus = 0f;
                world.Defines.MaxSupportInterdiction = 0f;
                foreach (var formation in world.SupportFormations) formation.Mission = SupportCore.MissionStandby;
            }
            Console.WriteLine("support effects disabled on both worlds.");
        }
        client.IsPaused = false;

        if (args.Contains("engagement"))
        {
            CompareEngagements(host, client);
            return 0;
        }
        for (var day = 0; day < daysAfter; day++)
        {
            host.TickDay();
            client.TickDay();
            mismatch = CompareSupport(host, client);
            if (mismatch is not null)
            {
                Console.WriteLine($"SUPPORT DIVERGED ON DAY {day + 1}: " + mismatch);
                return 1;
            }
            var drift = FirstDrift(host, client);
            if (drift is not null)
            {
                Console.WriteLine($"FIRST NUMERIC DRIFT ON DAY {day + 1}: " + drift);
                Console.WriteLine("(support establishment still identical at this point)");
                return 1;
            }
            Console.WriteLine($"day {day + 1}: identical.");
        }
        Console.WriteLine("no divergence found - rerun the probe, the check may be flaky.");
        return 0;
    }

    private static bool Attach(Campaign campaign)
    {
        if (!MapRasterCatalog.TryBuildProvinceTopology(Globality.EarthData.States, Globality.EarthData.Control,
                Globality.EarthData.Width, Globality.EarthData.Height, out var topology, out var message))
        {
            Console.WriteLine("province map unavailable: " + message);
            return false;
        }
        if (MapRasterCatalog.TryLoadHydrology(Globality.EarthData.Width, Globality.EarthData.Height,
                out var hydrology, out _))
            topology.AttachHydrology(hydrology);
        campaign.AttachProvinceTopology(topology);
        return true;
    }

    /// <summary>Every running battle, the long-range reach each world computes for it. With
    /// identical formations, identical armies and identical maps these must agree to the
    /// last bit - and the first one that does not says where the desync is born.</summary>
    private static void CompareEngagements(Campaign host, Campaign client)
    {
        foreach (var battle in host.Battles.Where(b => !b.Ended).ToList())
        {
            var attackerNations = NationsOf(host, battle.Attackers);
            var defenderNations = NationsOf(host, battle.Defenders);
            var clientBattle = client.Battles.FirstOrDefault(b => b.Id == battle.Id);
            if (clientBattle is null) { Console.WriteLine($"battle {battle.Id}: missing on the client"); continue; }
            var clientAttackers = NationsOf(client, clientBattle.Attackers);
            var clientDefenders = NationsOf(client, clientBattle.Defenders);
            if (!attackerNations.SetEquals(clientAttackers) || !defenderNations.SetEquals(clientDefenders))
            {
                Console.WriteLine($"battle {battle.Id}: engaged nations differ");
                continue;
            }
            var hostReach = SupportCore.Engagement(host, battle.ProvinceId, attackerNations, defenderNations);
            var clientReach = SupportCore.Engagement(client, clientBattle.ProvinceId, clientAttackers, clientDefenders);
            if (hostReach != clientReach)
            {
                Console.WriteLine($"battle {battle.Id} province {battle.ProvinceId}: reach differs " +
                                  $"host {hostReach} client {clientReach}");
                Console.WriteLine("  engaged attackers: " + string.Join(",", attackerNations) +
                                  "  defenders: " + string.Join(",", defenderNations));
                DrillIn(host, client, battle.ProvinceId, attackerNations, defenderNations);
            }
        }
    }

    private static HashSet<int> NationsOf(Campaign campaign, List<int> armies)
    {
        var nations = new HashSet<int>();
        foreach (var armyId in armies)
        {
            var army = campaign.Armies.Find(a => a.Id == armyId);
            if (army is not null) nations.Add(army.NationId);
        }
        return nations;
    }

    private static void DrillIn(Campaign host, Campaign client, int province, HashSet<int> attackerNations, HashSet<int> defenderNations)
    {
        foreach (var nationId in attackerNations.Concat(defenderNations))
        {
            foreach (var world in new[] { host, client })
            {
                if (!world.SupportByNation.TryGetValue(nationId, out var list)) { Console.WriteLine($"  nation {nationId} on {(world == host ? "host" : "client")}: no formations"); continue; }
                foreach (var formation in list)
                {
                    if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) continue;
                    var displaced = SupportCore.Displaced(world, formation);
                    var inRange = SupportCore.InRange(world, formation, type, province);
                    Console.WriteLine($"  nation {nationId} on {(world == host ? "host" : "client")}: formation {formation.Id} {formation.TypeId} " +
                                      $"base {formation.BaseProvinceId} strength {formation.Strength:0.00000} mission {formation.Mission} " +
                                      $"displaced {displaced} in range {inRange}");
                }
            }
        }
    }

    /// <summary>Field-by-field comparison of every long-range formation, host against client.</summary>
    private static string CompareSupport(Campaign host, Campaign client)
    {
        if (host.SupportFormations.Count != client.SupportFormations.Count)
            return $"formation count: host {host.SupportFormations.Count} against client {client.SupportFormations.Count}";
        if (host.SupportOrders.Count != client.SupportOrders.Count)
            return $"order count: host {host.SupportOrders.Count} against client {client.SupportOrders.Count}";
        for (var index = 0; index < host.SupportFormations.Count; index++)
        {
            var a = host.SupportFormations[index];
            var b = client.SupportFormations[index];
            if (a.Id != b.Id) return $"formation {a.Id} id {b.Id}";
            if (a.NationId != b.NationId) return $"formation {a.Id} nation {a.NationId} against {b.NationId}";
            if (a.TypeId != b.TypeId) return $"formation {a.Id} type {a.TypeId} against {b.TypeId}";
            if (a.StateMapIndex != b.StateMapIndex) return $"formation {a.Id} state {a.StateMapIndex} against {b.StateMapIndex}";
            if (a.BaseProvinceId != b.BaseProvinceId) return $"formation {a.Id} base {a.BaseProvinceId} against {b.BaseProvinceId}";
            if (MathF.Abs(a.Strength - b.Strength) > 1e-6f) return $"formation {a.Id} strength {a.Strength} against {b.Strength}";
            if (MathF.Abs(a.Experience - b.Experience) > 1e-6f) return $"formation {a.Id} experience {a.Experience} against {b.Experience}";
            if (a.Mission != b.Mission) return $"formation {a.Id} mission {a.Mission} against {b.Mission}";
        }
        for (var index = 0; index < host.SupportOrders.Count; index++)
        {
            var a = host.SupportOrders[index];
            var b = client.SupportOrders[index];
            if (a.Id != b.Id || a.NationId != b.NationId || a.StateMapIndex != b.StateMapIndex || a.TypeId != b.TypeId ||
                MathF.Abs(a.PaidCost - b.PaidCost) > 1e-6f || a.Personnel != b.Personnel ||
                a.DurationDays != b.DurationDays || a.TrainedDays != b.TrainedDays || a.LastProcessedDay != b.LastProcessedDay)
                return $"order {a.Id} differs (type {a.TypeId}, trained {a.TrainedDays}/{b.TrainedDays}, " +
                       $"last day {a.LastProcessedDay}/{b.LastProcessedDay})";
        }
        return null;
    }

    /// <summary>The first quantity the bench's fingerprint would notice drifting.</summary>
    private static string FirstDrift(Campaign host, Campaign client)
    {
        foreach (var nation in host.Nations)
        {
            var twin = client.Nations.Find(other => other.Id == nation.Id);
            if (twin is null) return $"nation {nation.Id} missing from the client";
            if (MathF.Abs(nation.Treasury - twin.Treasury) > 1e-3f) return $"{nation.Tag} treasury {nation.Treasury} against {twin.Treasury}";
            if (MathF.Abs(nation.ManpowerReserve - twin.ManpowerReserve) > 1e-3f)
                return $"{nation.Tag} manpower {nation.ManpowerReserve} against {twin.ManpowerReserve}";
            if (MathF.Abs(nation.Stability - twin.Stability) > 1e-5f) return $"{nation.Tag} stability {nation.Stability} against {twin.Stability}";
        }
        if (host.Armies.Count != client.Armies.Count)
            return $"army count {host.Armies.Count} against {client.Armies.Count}";
        foreach (var army in host.Armies)
        {
            var twin = client.Armies.Find(other => other.Id == army.Id);
            if (twin is null) return $"army {army.Id} missing from the client";
            if (MathF.Abs(army.Strength - twin.Strength) > 1e-5f)
                return $"army {army.Id} ({army.NationId}) strength {army.Strength:0.####} against {twin.Strength:0.####}";
        }
        return null;
    }
}
