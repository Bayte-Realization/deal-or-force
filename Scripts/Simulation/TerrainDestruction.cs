using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    // Empty in ordinary campaigns. Persisted alongside the political map.
    public readonly HashSet<int> DestroyedProvinces = new();

    public bool DestroyProvinceTerrain(int province)
    {
        var map = ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount ||
            map.IsWater(province) || DestroyedProvinces.Contains(province)) return false;
        var stateId = map.ProvinceToState[province];
        var state = States.Find(s => s.MapIndex == stateId);
        var owner = Nations.Find(n => n.MapIndex == map.ProvinceToNation[province]);
        var remaining = 0;
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (map.ProvinceToState[p] == stateId && !map.IsWater(p)) remaining++;
        if (state is not null && remaining > 0)
        {
            // Regional data has no building/house coordinates. Provinces share regional assets equally.
            var lost = remaining == 1 ? state.Population : state.Population / remaining;
            var oldNationalPopulation = owner?.Population ?? 0;
            state.Population = Math.Max(0, state.Population - lost);
            ResizePopulation(state.PopulationGroups, state.Population);
            foreach (var kind in state.Buildings.Keys.ToArray())
                state.Buildings[kind] = remaining == 1 ? 0 :
                    Math.Max(0, state.Buildings[kind] - (int)Math.Ceiling(state.Buildings[kind] / (double)remaining));
            foreach (var good in state.ResourceDeposits.Keys.ToArray())
                state.ResourceDeposits[good] *= (remaining - 1f) / remaining;
            state.WarDamage = remaining == 1 ? 1f : Math.Clamp(state.WarDamage + 1f / remaining, 0f, 1f);
            if (remaining == 1) Construction.RemoveAll(c => c.StateMapIndex == stateId);
            if (owner is not null)
            {
                owner.Population = Math.Max(0, owner.Population - lost);
                var share = oldNationalPopulation > 0 ? Math.Clamp(lost / (double)oldNationalPopulation, 0, 1) : 0;
                owner.Gdp = (float)Math.Max(0, owner.Gdp * (1 - share));
                owner.Stability = Math.Clamp(owner.Stability - .03f, 0f, 1f);
            }
        }
        foreach (var army in Armies.Where(a => a.ProvinceId == province).ToArray())
            Attrition.Destroy(this, army, "Terrain destroyed by the trickster.");
        SupportFormations.RemoveAll(s => s.BaseProvinceId == province);
        if (remaining == 1) SupportOrders.RemoveAll(o => o.StateMapIndex == stateId);
        foreach (var army in Armies)
        {
            if (army.OrderProvinceId == province) { army.OrderProvinceId = -1; army.PlanActive = false; }
            if (army.Route.Contains(province)) army.Route.Clear();
        }
        foreach (var battle in Battles.Where(b => b.ProvinceId == province && !b.Ended))
        { battle.Ended = true; battle.Outcome = "Terrain destroyed"; }
        DestroyedProvinces.Add(province);
        map.DestroyedByProvince[province] = true;
        map.ProvinceToNation[province] = 0;
        if (ProvinceController is not null) ProvinceController[province] = 0;
        if (ProvinceOccupation is not null) ProvinceOccupation[province] = 0;
        InvalidateStateIndex();
        MarkMapChanged();
        return true;
    }

    private static void ResizePopulation(List<PopulationGroup> groups, long target)
    {
        var total = groups.Sum(g => g.Size);
        if (total <= 0 || groups.Count == 0) return;
        long assigned = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var size = i == groups.Count - 1 ? target - assigned :
                Math.Min(target - assigned, (long)(groups[i].Size * (target / (double)total)));
            groups[i].Size = Math.Max(0, size); assigned += groups[i].Size;
        }
    }
}
