using System;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>One connected regional settlement becomes one independent country using the
/// existing secession identity/culture/capital pipeline, but without a revolt roll, militia
/// or forced follow-up war. Cash, debt and arrears are partitioned, never duplicated.</summary>
public static class Dismantlement
{
    public static string Culture(Campaign c, int[] regions) => regions.Select(id => c.States.Find(s => s.MapIndex == id))
        .Where(s => s is not null).OrderByDescending(s => s.Population)
        .Select(s => EconomySystem.Cultures(s).FirstOrDefault().Culture).FirstOrDefault() ?? "";

    public static string Refusal(Campaign c, Nation parent, int[] regions, bool peace = false)
    {
        if (!c.FeatureEnabled("secession")) return Loc.Get("Territorial release is disabled.");
        if (parent is null || c.ProvinceMap is null || regions is null || regions.Length == 0 || regions.Distinct().Count() != regions.Length)
            return Loc.Get("Select whole regions for an independent settlement.");
        if (!RegionalResourcePartition.ValidAccounts(parent)) return Loc.Get("These regions cannot be released right now.");
        var states = regions.Select(id => c.States.Find(s => s.MapIndex == id)).ToArray();
        if (states.Any(s => s is null || s.NationMapIndex != parent.MapIndex || s.Unclaimed || s.Population <= 0))
            return Loc.Get("Only populated regions of that country can be released.");
        var selected = regions.ToHashSet(); var map = c.ProvinceMap;
        var ground = Enumerable.Range(1, map.ProvinceCount).Where(p => selected.Contains(map.ProvinceToState[p])).ToArray();
        if (ground.Length == 0 || ground.Select(p => (int)map.ProvinceToState[p]).Distinct().Count() != regions.Length ||
            ground.Any(p => map.ProvinceToNation[p] != parent.MapIndex || (!peace && (c.ProvinceController ?? map.ProvinceToNation)[p] != parent.MapIndex)))
            return Loc.Get("Release requires entire regions, not occupied fragments.");
        if (!Enumerable.Range(1, map.ProvinceCount).Any(p => map.ProvinceToNation[p] == parent.MapIndex && !selected.Contains(map.ProvinceToState[p])))
            return Loc.Get("The parent country must retain actual territory.");
        var connected = new System.Collections.Generic.HashSet<int> { regions[0] };
        bool changed;
        do
        {
            changed = false;
            foreach (var p in ground.Where(p => connected.Contains(map.ProvinceToState[p])))
                foreach (var other in map.Neighbors[p])
                    if (selected.Contains(map.ProvinceToState[other])) changed |= connected.Add(map.ProvinceToState[other]);
        } while (changed);
        if (connected.Count != regions.Length) return Loc.Get("Released regions must form one connected settlement.");
        if (!SecessionEvents.CanFoundSettlement(c, parent, Culture(c, regions))) return Loc.Get("There is no free country identity for this settlement.");
        return "";
    }

    public static float Share(Campaign c, Nation parent, int[] regions) =>
        (float)Math.Clamp(regions.Sum(id => (double)(c.States.Find(s => s.MapIndex == id)?.Population ?? 0)) /
            Math.Max(1, c.StatesOf(parent).Sum(s => (double)s.Population)), 0, 1);

    internal static Nation Apply(Campaign c, Nation parent, int[] regions, bool voluntary)
    {
        var states = regions.Select(id => c.States.Find(s => s.MapIndex == id)).ToArray();
        var share = Share(c, parent, regions);
        var born = SecessionEvents.FoundSettlement(c, parent, states, Culture(c, regions));
        if (born is null) return null;
        RegionalResourcePartition.Transfer(parent, born, share);
        var provinces = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => regions.Contains(c.ProvinceMap.ProvinceToState[p])).ToArray();
        foreach (var s in states)
        {
            if (voluntary) TerritorialClaims.Renounce(c, parent, s);
            if (!s.CoreTags.Contains(born.Tag)) s.CoreTags.Add(born.Tag);
        }
        StatePartition.Annex(c, provinces, born, asConquest: false);
        RegionalResourcePartition.RecountResidents(c, parent, born);
        var relation = CampaignDiplomacy.Get(c, parent.Id, born.Id);
        relation.Stance = DiplomaticStance.Truce;
        relation.TruceUntil = c.ElapsedDays + 3650;
        c.LocateCapitals(preserveExisting: true); c.MarkStrategicChange();
        return born;
    }
}
