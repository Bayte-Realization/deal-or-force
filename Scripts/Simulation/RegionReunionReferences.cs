using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Live references follow a region's surviving identity, not its deleted shard.
/// Province IDs and routes stay unchanged. Historical records are not rewritten.</summary>
internal static class RegionReunionReferences
{
    internal static void Move(Campaign c, ScenarioState parent, ScenarioState child, long here, long there)
    {
        int Map(int value) => value == child.MapIndex ? parent.MapIndex : value;
        foreach (var a in c.Armies)
        {
            a.StateMapIndex = Map(a.StateMapIndex); a.OrderStateMapIndex = Map(a.OrderStateMapIndex);
            a.ObjectiveStateMapIndex = Map(a.ObjectiveStateMapIndex);
            a.SupplySourceStateMapIndex = Map(a.SupplySourceStateMapIndex);
        }
        foreach (var p in c.Construction) p.StateMapIndex = Map(p.StateMapIndex);
        foreach (var p in c.Recruitment) p.StateMapIndex = Map(p.StateMapIndex);
        foreach (var p in c.SupportOrders) p.StateMapIndex = Map(p.StateMapIndex);
        foreach (var p in c.SupportFormations)
        { p.StateMapIndex = Map(p.StateMapIndex); p.TargetStateMapIndex = Map(p.TargetStateMapIndex); }
        foreach (var war in c.Wars) war.ClaimedStateMapIndex = Map(war.ClaimedStateMapIndex);
        foreach (var homeland in c.Homelands.Values)
            if (homeland.Remove(child.MapIndex)) homeland.Add(parent.MapIndex);
        foreach (var n in c.Nations)
        {
            n.CapitalStateMapIndex = Map(n.CapitalStateMapIndex);
            if (n.DomesticCrisis is not null) n.DomesticCrisis.ConciliationRegion = Map(n.DomesticCrisis.ConciliationRegion);
            foreach (var op in n.CovertOperations) op.RegionMapIndex = Map(op.RegionMapIndex);
            foreach (var law in n.Laws.Values) law.Region = Map(law.Region);
            if (n.Movement is not { } m) continue;
            m.HeadquartersStateMapIndex = Map(m.HeadquartersStateMapIndex);
            m.MovingToStateMapIndex = Map(m.MovingToStateMapIndex);
            foreach (var incoming in m.OnTheWay) incoming.Region = Map(incoming.Region);
            // Support is a share of residents; hidden fighters are a head count.
            // Averaging both as rates or summing both as counts invents people.
            var supported = m.Support.ContainsKey(parent.MapIndex) || m.Support.ContainsKey(child.MapIndex);
            var left = m.Support.GetValueOrDefault(parent.MapIndex);
            var right = m.Support.GetValueOrDefault(child.MapIndex);
            m.Support.Remove(child.MapIndex);
            if (supported) m.Support[parent.MapIndex] = here + there > 0
                ? (float)((left * (double)here + right * (double)there) / (here + there)) : Math.Max(left, right);
            if (m.Infiltrated.Remove(child.MapIndex, out var fighters))
                m.Infiltrated[parent.MapIndex] = m.Infiltrated.GetValueOrDefault(parent.MapIndex) + fighters;
            for (var i = 0; i < m.Homeland.Count; i++)
                if (m.Homeland[i] == child.Id) m.Homeland[i] = parent.Id;
        }
        c.MovementRevision++;
    }
}
