using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>Current contacts, not permanent exploration or permission to command.</summary>
public sealed class ProvinceVision
{
    internal byte[] Reach = Array.Empty<byte>();
    internal readonly Queue<int> Frontier = new();
    internal readonly HashSet<int> CombatContacts = new();
    internal int Stamp;

    // What the country's own ground reveals, kept between rebuilds. It is most of a rebuild
    // - every province the country holds, flooded outwards - and it only changes when who
    // holds what changes, while everything else here (armies, battles, landings) moves by
    // the hour. Kept with the exact controller map it was built from, compared whole.
    internal byte[] TerritoryReach = Array.Empty<byte>();
    internal ushort[] TerritoryControllers = Array.Empty<ushort>();
    internal int TerritoryMapIndex = -1;
    internal int TerritoryDepth = -1;
    public int Revision { get; internal set; }
    public bool Sees(int province) => province > 0 && province < Reach.Length && Reach[province] > 0;
}

public static partial class CampaignIntel
{
    private sealed class Cache
    {
        public int Epoch;
        public readonly Dictionary<int, ProvinceVision> Viewers = new();
    }
    private static readonly ConditionalWeakTable<Campaign, Cache> Caches = new();

    public static void Invalidate(Campaign c)
    {
        if (c is not null && Caches.TryGetValue(c, out var cache)) cache.Epoch++;
    }

    public static ProvinceVision Vision(Campaign c, int viewerId)
    {
        if (c is null) return null;
        var cache = Caches.GetOrCreateValue(c);
        if (!cache.Viewers.TryGetValue(viewerId, out var view))
            cache.Viewers[viewerId] = view = new ProvinceVision();
        var stamp = HashCode.Combine(c.ElapsedDays, c.MapRevision, cache.Epoch, c.Armies.Count,
            c.Battles.Count, c.Fleets.Count, c.ProvinceMap);
        if (view.Revision > 0 && view.Stamp == stamp) return view;
        view.Stamp = stamp;
        view.Revision++;
        using var timer = SimProfiler.Measure("intel.visibility");
        var map = c.ProvinceMap;
        var nation = c.NationById(viewerId);
        if (map is null || nation is null) { view.Reach = Array.Empty<byte>(); return view; }
        if (view.Reach.Length != map.ProvinceCount + 1) view.Reach = new byte[map.ProvinceCount + 1];
        else Array.Clear(view.Reach);
        view.Frontier.Clear();
        view.CombatContacts.Clear();
        var borderDepth = Math.Clamp(c.Defines.BorderVisionDepth, 0, 6);
        var armyDepth = Math.Clamp(c.Defines.ArmyVisionDepth, 0, 6);
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        void Reveal(int province, int depth)
        {
            if (province <= 0 || province >= view.Reach.Length || view.Reach[province] >= depth + 1) return;
            view.Reach[province] = (byte)(depth + 1);
            view.Frontier.Enqueue(province);
        }
        void Flood()
        {
            while (view.Frontier.Count > 0)
            {
                var p = view.Frontier.Dequeue();
                var nextDepth = view.Reach[p] - 2;
                if (nextDepth < 0) continue;
                foreach (var next in map.Neighbors[p]) Reveal(next, nextDepth);
            }
        }

        // The ground first, from the copy when nobody's ground has changed hands since. The
        // flood is a maximum - each province keeps the deepest reach anything gives it - so
        // flooding the armies over a finished territory arrives exactly where flooding
        // everything at once did.
        if (view.TerritoryMapIndex == nation.MapIndex && view.TerritoryDepth == borderDepth &&
            view.TerritoryReach.Length == view.Reach.Length &&
            controllers.AsSpan().SequenceEqual(view.TerritoryControllers))
        {
            Array.Copy(view.TerritoryReach, view.Reach, view.Reach.Length);
        }
        else
        {
            for (var p = 1; p < controllers.Length; p++)
                if (controllers[p] == nation.MapIndex) Reveal(p, borderDepth);
            Flood();
            if (view.TerritoryReach.Length != view.Reach.Length) view.TerritoryReach = new byte[view.Reach.Length];
            Array.Copy(view.Reach, view.TerritoryReach, view.Reach.Length);
            if (view.TerritoryControllers.Length != controllers.Length) view.TerritoryControllers = new ushort[controllers.Length];
            Array.Copy(controllers, view.TerritoryControllers, controllers.Length);
            view.TerritoryMapIndex = nation.MapIndex;
            view.TerritoryDepth = borderDepth;
        }
        foreach (var army in c.Armies)
            if (army.NationId == viewerId) Reveal(army.ProvinceId, armyDepth);
        foreach (var battle in c.Battles)
        {
            if (battle.Ended || (battle.AttackerId != viewerId && battle.DefenderId != viewerId)) continue;
            Reveal(battle.ProvinceId, armyDepth);
            view.CombatContacts.UnionWith(battle.Attackers);
            view.CombatContacts.UnionWith(battle.Defenders);
        }
        foreach (var army in c.Armies)
            if (view.CombatContacts.Contains(army.Id)) Reveal(army.ProvinceId, 0);
        // An invasion order alone is NOT reconnaissance across the world. The fleet
        // must actually have approached its beach, or combat must already have begun.
        foreach (var fleet in c.Fleets)
        {
            if (fleet.NationId != viewerId || fleet.Hulls <= 0 || fleet.Mission != FleetMission.Invasion ||
                fleet.TargetProvinceId <= 0 || fleet.TargetProvinceId > map.ProvinceCount) continue;
            var beach = map.CenterOf(fleet.TargetProvinceId);
            if (NavalOperations.Distance(fleet.Latitude, fleet.Longitude, beach.Latitude, beach.Longitude) <=
                Math.Clamp(c.Defines.LandingVisionRangeDegrees, .1f, 3f))
                Reveal(fleet.TargetProvinceId, armyDepth);
        }
        Flood();
        return view;
    }

    public static bool CanSeeProvince(Campaign c, int viewerId, int province) => Vision(c, viewerId)?.Sees(province) == true;
    public static bool CanSeeArmy(Campaign c, int viewerId, Army army)
    {
        if (army is null || viewerId < 0) return false;
        if (CanInspect(viewerId, army.NationId)) return true;
        var vision = Vision(c, viewerId);
        return vision is not null && (vision.Sees(army.ProvinceId) || vision.CombatContacts.Contains(army.Id));
    }
    public static bool PlayerCanSeeArmy(Campaign c, Army army) => c?.Player is not null &&
        (!c.Player.IsHuman || CanSeeArmy(c, c.Player.Id, army));
    public static bool PlayerCanSeeProvince(Campaign c, int province) => c?.Player is not null &&
        (!c.Player.IsHuman || CanSeeProvince(c, c.Player.Id, province));
    public static bool PlayerCanSeeBattle(Campaign c, LandBattle battle) => c?.Player is not null && battle is not null &&
        (!c.Player.IsHuman || battle.AttackerId == c.Player.Id || battle.DefenderId == c.Player.Id ||
         CanSeeProvince(c, c.Player.Id, battle.ProvinceId));
}
