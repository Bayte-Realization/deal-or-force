using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Emergency evacuation, not an AI mission choice. Applies equally to human
/// and AI formations before they can project power from captured or ceded ground.
/// Only owned AND controlled dry homeland with free posts is a destination.</summary>
public static class SupportRepatriation
{
    private sealed class Home
    {
        internal ScenarioState State;
        internal readonly List<int> Provinces = new();
        internal int Free;
        internal int Base = -1;

        internal void Reset(ScenarioState state, int free)
        {
            State = state;
            Provinces.Clear();
            Free = free;
            Base = -1;
        }
    }

    [ThreadStatic] private static List<SupportFormation> _lostScratch;
    [ThreadStatic] private static HashSet<int> _lostNationsScratch;
    [ThreadStatic] private static Dictionary<int, Home> _homesScratch;
    [ThreadStatic] private static List<Home> _homePool;

    public static int Reconcile(Campaign c)
    {
        if (c?.ProvinceMap is not { } map || c.ProvinceController is not { } controls) return 0;
        if (!MechanicFeatures.SystemEnabled(c, nameof(SupportCore))) return 0;
        var lost = _lostScratch ??= new List<SupportFormation>();
        lost.Clear();
        foreach (var f in c.SupportFormations)
        {
            if (!SupportCore.Enabled(c, f.TypeId) || !SupportCore.Displaced(c, f)) continue;
            // Off-map reserves retry daily; new losses are handled on the next step.
            if (f.BaseProvinceId < 0 && f.StateMapIndex < 0 && !c.StepOpensNewDay) continue;
            lost.Add(f);
        }
        if (lost.Count == 0) return 0; // No map scan or temporary indexes in the common case.
        if (lost.Count > 1) lost.Sort((a, b) => a.Id.CompareTo(b.Id));

        var lostNations = _lostNationsScratch ??= new HashSet<int>();
        lostNations.Clear();
        for (var i = 0; i < lost.Count; i++)
        {
            var nation = c.NationById(lost[i].NationId);
            if (nation is not null && nation.MapIndex > 0) lostNations.Add(nation.MapIndex);
        }

        var homes = _homesScratch ??= new Dictionary<int, Home>();
        homes.Clear();
        var pool = _homePool ??= new List<Home>();
        var pooledCount = 0;

        if (lostNations.Count > 0)
        {
            foreach (var s in c.States)
            {
                if (!lostNations.Contains(s.NationMapIndex) || homes.ContainsKey(s.MapIndex)) continue;
                Home home;
                if (pooledCount < pool.Count) home = pool[pooledCount];
                else { home = new Home(); pool.Add(home); }
                pooledCount++;
                home.Reset(s, Math.Max(0, SupportCore.CapacityOf(c, s)));
                homes.Add(s.MapIndex, home);
            }
            if (homes.Count > 0)
            {
                foreach (var f in c.SupportFormations)
                    if (SupportCore.Enabled(c, f.TypeId) && !SupportCore.Displaced(c, f) && homes.TryGetValue(f.StateMapIndex, out var home)) home.Free--;
                foreach (var order in c.SupportOrders)
                    if (SupportCore.Enabled(c, order.TypeId) && homes.TryGetValue(order.StateMapIndex, out var home)) home.Free--;

                var anyFree = false;
                foreach (var home in homes.Values)
                    if (home.Free > 0) { anyFree = true; break; }

                if (anyFree)
                {
                    // One world scan for the whole displaced batch, never one map scan per region/formation.
                    for (var p = 1; p <= map.ProvinceCount; p++)
                    {
                        if (map.IsWater(p) || !homes.TryGetValue(map.ProvinceToState[p], out var home)) continue;
                        if (home.Free <= 0 || home.State.NationMapIndex <= 0 ||
                            controls[p] != home.State.NationMapIndex ||
                            map.ProvinceToNation[p] != home.State.NationMapIndex) continue;
                        home.Provinces.Add(p);
                    }
                }
            }
        }
        var changed = 0;
        foreach (var f in lost)
        {
            var nation = c.NationById(f.NationId);
            Home best = null; var bestDistance = float.MaxValue;
            if (nation is not null && homes.Count > 0)
            {
                foreach (var home in homes.Values)
                {
                    if (home.State.NationMapIndex != nation.MapIndex || home.Free <= 0 || home.Provinces.Count == 0) continue;
                    var distance = f.BaseProvinceId > 0 && f.BaseProvinceId <= map.ProvinceCount
                        ? SupportCore.DistanceKm(c, f.BaseProvinceId, home.Provinces[0]) : 0f;
                    // Prefer a surviving post in the same region, otherwise nearest region;
                    // ties are explicit, not dependent on dictionary or nation-list order.
                    if (home.State.MapIndex == f.StateMapIndex) distance = -1f;
                    if (best is not null && (distance > bestDistance || distance == bestDistance && home.State.MapIndex >= best.State.MapIndex)) continue;
                    best = home; bestDistance = distance;
                }
            }
            var state = -1; var province = -1;
            if (best is not null)
            {
                if (best.Base < 0) best.Base = SupportCore.ChooseBase(map, best.Provinces);
                state = best.State.MapIndex; province = best.Base; best.Free--;
            }
            if (f.StateMapIndex == state && f.BaseProvinceId == province && f.Mission == SupportCore.MissionStandby && f.TargetStateMapIndex == -1) continue;
            f.StateMapIndex = state; f.BaseProvinceId = province;
            f.Mission = SupportCore.MissionStandby; f.TargetStateMapIndex = -1;
            // No healing, refunds, destruction, nationality change or random draws.
            changed++;
            DiagnosticLogs.Write("orders", $"SUPPORT_REPATRIATE formation={f.Id} nation={nation?.Tag} province={province} state={state}");
        }
        lost.Clear();
        if (changed > 0) c.MarkStrategicChange();
        return changed;
    }
}
