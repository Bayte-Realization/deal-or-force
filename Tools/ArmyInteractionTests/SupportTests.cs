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

        var target = campaign.States.First(s => s.NationMapIndex == player.MapIndex &&
            SupportCore.PostsUsed(campaign, s.MapIndex) == 0 && s.MapIndex != region.MapIndex &&
            SupportCore.BaseProvinceOf(campaign, player, s.MapIndex) > 0);
        var pair = campaign.SupportFormations.Where(f => f.NationId == player.Id && f.StateMapIndex != target.MapIndex).Take(2).ToArray();
        var oldLocations = pair.Select(f => (f.StateMapIndex, f.BaseProvinceId)).ToArray();
        var oldBarracks = target.Buildings.GetValueOrDefault(BuildingKind.Barracks);
        target.Buildings[BuildingKind.Barracks] = 0;
        var reserve = new SupportOrder { Id = -110, NationId = player.Id, StateMapIndex = target.MapIndex };
        campaign.SupportOrders.Add(reserve);
        var ids = pair.Select(f => f.Id).ToArray();
        try
        {
            var plan = SupportCore.PreviewRebase(campaign, player.Id, ids, target.MapIndex);
            Check(plan.Moving == 2 && plan.Free == 1 && plan.Reserved == 1 && plan.Refusal.Length > 0,
                "preview includes posts reserved for training");
            Check(pair.Select(f => (f.StateMapIndex, f.BaseProvinceId)).SequenceEqual(oldLocations), "preview is read-only");
            var blocked = new RebaseSupportGroupCommand { IssuerId = player.Id, FormationIds = ids, StateMapIndex = target.MapIndex };
            blocked.Apply(campaign);
            Check(blocked.Refusal.Length > 0 && blocked.Moved == 0 &&
                pair.Select(f => (f.StateMapIndex, f.BaseProvinceId)).SequenceEqual(oldLocations), "over-capacity group moves nobody");
            campaign.SupportOrders.Remove(reserve);
            var stale = new RebaseSupportGroupCommand { IssuerId = player.Id, FormationIds = new[] { ids[0], int.MaxValue }, StateMapIndex = target.MapIndex };
            stale.Apply(campaign);
            Check(stale.Refusal.Length > 0 && pair[0].StateMapIndex == oldLocations[0].StateMapIndex, "stale member refuses entire group");
            var other = campaign.SupportFormations.First(f => f.NationId != player.Id);
            var foreign = new RebaseSupportGroupCommand { IssuerId = player.Id, FormationIds = new[] { ids[0], other.Id }, StateMapIndex = target.MapIndex };
            foreign.Apply(campaign);
            Check(foreign.Refusal.Length > 0 && foreign.Moved == 0, "foreign member refuses entire group");
            var abroad = campaign.States.First(s => s.NationMapIndex != player.MapIndex);
            Check(SupportCore.PreviewRebase(campaign, player.Id, ids, abroad.MapIndex).Refusal.Length > 0,
                "foreign region is not a friendly base");
            var group = new RebaseSupportGroupCommand { IssuerId = player.Id, FormationIds = new[] { ids[1], ids[0], ids[0] }, StateMapIndex = target.MapIndex };
            Check(NetworkCommandCodec.TryEncode(group, out var wireType, out var wire) &&
                NetworkCommandCodec.TryDecode(wireType, wire, out var decoded) && decoded is RebaseSupportGroupCommand,
                "group rebase survives the network/replay codec");
            group.Apply(campaign);
            Check(group.Refusal.Length == 0 && group.Moved == 2 && pair.All(f => f.StateMapIndex == target.MapIndex) &&
                SupportCore.PostsUsed(campaign, target.MapIndex) == 2, "whole group moves, duplicate ids consume no extra posts");
            group.Apply(campaign);
            Check(group.Refusal.Length == 0 && group.Moved == 0, "repeat order is idempotent at a full base");
            pair[0].BaseProvinceId = -1;
            group.Apply(campaign);
            Check(group.Refusal.Length == 0 && group.Moved == 1 && !SupportCore.Displaced(campaign, pair[0]),
                "lost base can be repaired inside its full existing region");
            campaign.SupportOrders.Add(reserve);
            pair[0].StateMapIndex = oldLocations[0].StateMapIndex;
            pair[0].BaseProvinceId = oldLocations[0].BaseProvinceId;
            Check(SupportCore.Rebase(campaign, pair[0], target.MapIndex).Length > 0, "single rebase also respects training reservations");
            pair[0].BaseProvinceId = campaign.ProvinceMap.ProvinceCount + 1;
            Check(SupportCore.Displaced(campaign, pair[0]), "invalid saved base is safe to inspect");
        }
        finally
        {
            campaign.SupportOrders.Remove(reserve);
            target.Buildings[BuildingKind.Barracks] = oldBarracks;
            for (var i = 0; i < pair.Length; i++)
            {
                pair[i].StateMapIndex = oldLocations[i].StateMapIndex;
                pair[i].BaseProvinceId = oldLocations[i].BaseProvinceId;
            }
        }
    }
}
