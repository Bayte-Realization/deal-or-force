using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class SupportTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL support: " + name);
        Console.WriteLine("PASS support: " + name);
    }

    public static void Run(Campaign campaign)
    {
        SupportTypeCatalog.ResetCatalog();
        Check(!SupportTypeCatalog.TryGet("missing_support_type", out _) &&
              SupportTypeCatalog.Get("artillery_brigade") is not null,
            "unknown mod type is not silently treated as artillery");
        Check(SupportTypeCatalog.TryGet("strike_drone_group", out var drones) &&
              drones.Missions.Contains(SupportCore.MissionInterdiction),
            "strike drones can receive the interdiction mission");
        Check(campaign.SupportFormations.Count > 0,
            "the opening world contains long-range formations");
        Check(campaign.States.All(state =>
                SupportCore.PostsUsed(campaign, state.MapIndex) <= SupportCore.CapacityOf(campaign, state)),
            "initial posts are within regional capacity");

        var player = campaign.Player;
        var region = campaign.States.First(state => state.NationMapIndex == player.MapIndex &&
            SupportCore.BaseProvinceOf(campaign, player, state.MapIndex) > 0);
        region.Buildings[BuildingKind.Barracks] =
            region.Buildings.GetValueOrDefault(BuildingKind.Barracks) + 1;
        player.Treasury = MathF.Max(player.Treasury, 1000f);
        player.ManpowerReserve = Math.Max(player.ManpowerReserve, 1_000_000);
        Check(SupportCore.Validate(campaign, player.Id, region.MapIndex, "missing_support_type") ==
              "No such formation type.", "invalid training orders are rejected");
        var before = campaign.SupportOrders.Count;
        new RaiseSupportCommand
        {
            IssuerId = player.Id,
            StateMapIndex = region.MapIndex,
            TypeId = "artillery_brigade"
        }.Apply(campaign);
        Check(campaign.SupportOrders.Count == before + 1 &&
              campaign.SupportOrders[^1].TypeId == "artillery_brigade",
            "player can train artillery at a controlled base");
    }
}
