using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Raising long-range formations and where they stand: basing capacity (what used to be posts,
/// now a price instead of a refusal), the training queue as a define, and the old region orders,
/// which are now relocations - each formation travelling on its own, each refusal named.
/// Run with --support-only; part of the full run.
/// </summary>
static class SupportTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL support: " + name);
        Console.WriteLine("PASS support: " + name);
    }

    static bool Fail(string why) { Console.WriteLine("  " + why); return false; }

    public static void Run(ProvinceTopology map)
    {
        // A world of its own, fresh from the opening: the shared fixture has been ticked and
        // ordered about by the time the full run gets here.
        var campaign = Campaign.CreateBootstrap(20260917);
        campaign.Defines.HoursPerTick = 24;
        campaign.ConfigureSinglePlayer(campaign.NationByTag("RUS").Id);
        campaign.AttachProvinceTopology(map);
        SupportTypeCatalog.ResetCatalog();
        Check(!SupportTypeCatalog.TryGet("missing_support_type", out _) &&
              SupportTypeCatalog.Get("artillery_brigade") is not null,
            "unknown mod type is not silently treated as artillery");
        Check(SupportTypeCatalog.TryGet("strike_drone_group", out var drones) &&
              drones.Missions.Contains(SupportCore.MissionInterdiction),
            "strike drones can receive the interdiction mission");
        Check(SupportTypeCatalog.Get("fighter_wing").Movement == SupportMoveMode.Air &&
              SupportTypeCatalog.Get("strike_drone_group").Movement == SupportMoveMode.Land &&
              SupportTypeCatalog.Get("sam_brigade").Movement == SupportMoveMode.Land,
            "aircraft fly; guns, missile brigades and drone crews drive");
        Check(SupportTypeCatalog.Get("artillery_brigade").Speed == .8f && SupportTypeCatalog.Get("sam_brigade").DeployDays == 2 &&
              SupportTypeCatalog.Get("fighter_wing").Basing == 2f && SupportTypeCatalog.Get("strategic_bomber_regiment").Basing == 3f &&
              SupportTypeCatalog.Get("mlrs_battalion").Basing == 1f,
            "speed, deployment and basing come from the data with kind defaults");
        Check(campaign.SupportFormations.Count > 0,
            "the opening world contains long-range formations");
        Check(campaign.SupportFormations.All(f => f.ProvinceId > 0 && f.ProvinceId == f.BaseProvinceId),
            "every opening formation stands, deployed, in a province");
        Check(campaign.States.All(state =>
                SupportCore.BasingUsed(campaign, state.MapIndex) <= SupportCore.CapacityOf(campaign, state) + 0.001f),
            "the opening world is spread within basing capacity");

        var player = campaign.Player;
        var region = campaign.States.First(state => state.NationMapIndex == player.MapIndex &&
            SupportCore.BaseProvinceOf(campaign, player, state.MapIndex) > 0);
        var barracksBefore = region.Buildings.GetValueOrDefault(BuildingKind.Barracks);
        var capacityBefore = SupportCore.CapacityOf(campaign, region);
        region.Buildings[BuildingKind.Barracks] = barracksBefore + 1;
        Check(SupportCore.CapacityOf(campaign, region) == capacityBefore + campaign.Defines.LongRangeBasingPerBarracks,
            "a barracks adds the define's basing");
        player.Treasury = MathF.Max(player.Treasury, 1000f);
        player.ManpowerReserve = Math.Max(player.ManpowerReserve, 1_000_000);
        Check(SupportCore.Validate(campaign, player.Id, region.MapIndex, "missing_support_type") ==
              "No such formation type.", "invalid training orders are rejected");
        var savedOrders = campaign.SupportOrders.ToList();
        try
        {
            var before = campaign.SupportOrders.Count;
            new RaiseSupportCommand { IssuerId = player.Id, StateMapIndex = region.MapIndex, TypeId = "artillery_brigade" }.Apply(campaign);
            Check(campaign.SupportOrders.Count == before + 1 && campaign.SupportOrders[^1].TypeId == "artillery_brigade",
                "player can train artillery at a controlled base");
            // The training queue is a define, not a literal.
            var slots = campaign.Defines.SupportTrainingSlots;
            campaign.Defines.SupportTrainingSlots = campaign.SupportOrders.Count(o => o.NationId == player.Id);
            Check(SupportCore.Validate(campaign, player.Id, region.MapIndex, "artillery_brigade") == "Training capacity reached.",
                "the training queue is full at SupportTrainingSlots");
            campaign.Defines.SupportTrainingSlots = slots;
            // A full region is not a refusal any more: it is crowding.
            var full = new SupportOrder { Id = -110, NationId = player.Id, StateMapIndex = region.MapIndex, TypeId = "strategic_bomber_regiment" };
            for (var i = 0; i < 6; i++) campaign.SupportOrders.Add(new SupportOrder { Id = -120 - i, NationId = -1, StateMapIndex = region.MapIndex, TypeId = "strategic_bomber_regiment" });
            Check(SupportCore.BasingUsed(campaign, region.MapIndex) > SupportCore.CapacityOf(campaign, region) &&
                  SupportCore.Validate(campaign, player.Id, region.MapIndex, "artillery_brigade").Length == 0,
                "an over-full region still accepts a new formation: crowding is paid for, not refused");
            Check(MathF.Abs(SupportCore.CrowdingOf(5f, 10f) - .5f) < 1e-6f && SupportCore.CrowdingOf(5f, 4f) == 1f,
                "crowding halves recovery in a region twice over capacity, and costs nothing within it");
            campaign.SupportOrders.Remove(full);
        }
        finally
        {
            campaign.SupportOrders.Clear();
            campaign.SupportOrders.AddRange(savedOrders);
            region.Buildings[BuildingKind.Barracks] = barracksBefore;
        }

        // The old region orders are relocations now: each formation goes on its own way, the
        // refusal names who stayed, and nothing jumps.
        var pair = campaign.SupportFormations.Where(f => f.NationId == player.Id && f.Route.Count == 0 &&
            SupportTypeCatalog.Get(f.TypeId).Movement == SupportMoveMode.Land).Take(2).ToArray();
        // A region of our own the first of them can drive to.
        var target = campaign.States.Where(s => s.NationMapIndex == player.MapIndex && pair.All(f => f.StateMapIndex != s.MapIndex))
            .OrderBy(s => s.MapIndex).First(s =>
                SupportCore.RegionOrderProvince(campaign, player.Id, s.MapIndex) is var province and > 0 &&
                LongRangeMovement.PreviewRelocate(campaign, player.Id, new[] { pair[0].Id }, province).Single().Accepted);
        var old = pair.Select(f => (f.ProvinceId, f.BaseProvinceId, f.StateMapIndex)).ToArray();
        var ids = pair.Select(f => f.Id).ToArray();
        try
        {
            var other = campaign.SupportFormations.First(f => f.NationId != player.Id);
            var mixed = new RebaseSupportGroupCommand { IssuerId = player.Id, FormationIds = new[] { ids[0], int.MaxValue, other.Id }, StateMapIndex = target.MapIndex };
            Check(NetworkCommandCodec.TryEncode(mixed, out var wireType, out var wire) &&
                  NetworkCommandCodec.TryDecode(wireType, wire, out var decoded) && decoded is RebaseSupportGroupCommand,
                "the old group order still survives the network/replay codec");
            mixed.Apply(campaign);
            Check(mixed.Moved == 1 && mixed.Refusal.Contains("#" + int.MaxValue) && mixed.Refusal.Contains("#" + other.Id) || Fail(mixed.Moved + " moved; " + mixed.Refusal),
                "a stale or foreign member no longer holds back the group: it is named and the rest go");
            Check(pair[0].ProvinceId == old[0].ProvinceId && pair[0].Route.Count > 0 &&
                  pair[0].Route[^1] == SupportCore.BaseProvinceOf(campaign, player, target.MapIndex),
                "a region order travels to the province the region's bases used to take, from where it stood");
            Check(SupportCore.Displaced(campaign, pair[0]) && pair[0].BaseProvinceId == -1,
                "a formation given an order packs up at once");
            var abroad = campaign.States.First(s => s.NationMapIndex != player.MapIndex && s.NationMapIndex > 0);
            Check(SupportCore.Rebase(campaign, pair[1], abroad.MapIndex).Length > 0, "a foreign region is not a region order's destination");
            pair[1].BaseProvinceId = campaign.ProvinceMap.ProvinceCount + 1;
            pair[1].ProvinceId = 0;
            Check(SupportCore.Displaced(campaign, pair[1]), "an invalid saved base is safe to inspect");
        }
        finally
        {
            for (var i = 0; i < pair.Length; i++)
            {
                pair[i].Route.Clear(); pair[i].RouteModes.Clear(); pair[i].MoveProgress = 0f; pair[i].DeployDaysLeft = 0;
                (pair[i].ProvinceId, pair[i].BaseProvinceId, pair[i].StateMapIndex) = old[i];
            }
        }
    }
}
