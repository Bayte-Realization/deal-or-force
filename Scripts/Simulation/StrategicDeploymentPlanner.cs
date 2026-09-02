using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Data-driven 2024 deployment pass. It derives politically sensible posts from the
/// live province graph instead of baking presentation coordinates: active belligerents
/// mass on their actual fronts, peaceful states cover foreign borders, and every army
/// keeps a capital/heartland reserve. Map mods therefore get correct deployments for
/// free as long as their control raster and capitals are valid.
/// </summary>
public static class StrategicDeploymentPlanner
{
    public static void Deploy(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;

        var owned = new Dictionary<ushort, List<int>>();
        var borders = new Dictionary<ushort, List<int>>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var owner = controllers[province];
            if (owner == 0) continue;
            Add(owned, owner, province);
            if (topology.Neighbors[province].Any(neighbor =>
                    controllers[neighbor] != 0 && controllers[neighbor] != owner))
                Add(borders, owner, province);
        }

        foreach (var nation in campaign.Nations)
        {
            var armies = campaign.Armies.Where(army => army.NationId == nation.Id)
                .OrderByDescending(army => army.Power).ThenBy(army => army.Id).ToArray();
            if (armies.Length == 0 || !owned.TryGetValue((ushort)nation.MapIndex, out var homeland) ||
                homeland.Count == 0)
                continue;

            homeland.Sort();
            var capital = Nearest(homeland, topology, nation.CapitalLatitude, nation.CapitalLongitude);
            var activeWar = campaign.Wars.FirstOrDefault(war => !war.Ended && war.Involves(nation.Id));
            var front = activeWar?.FriendlyFront(nation.Id).OrderBy(value => value).ToList() ?? new List<int>();
            var border = borders.GetValueOrDefault((ushort)nation.MapIndex) ?? new List<int>();
            border.Sort();

            var denseEasternFront = activeWar is not null &&
                ((nation.Tag == "RUS" && activeWar.Involves(campaign.NationByTag("UKR")?.Id ?? -1)) ||
                 (nation.Tag == "UKR" && activeWar.Involves(campaign.NationByTag("RUS")?.Id ?? -1)));
            var wartimeShare = denseEasternFront ? 0.94f : campaign.Defines.WartimeFrontDeploymentShare;
            var operationalCount = activeWar is not null && front.Count > 0
                ? Math.Clamp((int)MathF.Round(armies.Length * wartimeShare), 1, armies.Length)
                : border.Count > 0
                    ? Math.Clamp((int)MathF.Round(armies.Length * campaign.Defines.PeacetimeBorderDeploymentShare), 1, armies.Length)
                    : 0;
            var operationalPosts = activeWar is not null && front.Count > 0 ? front : border;

            for (var index = 0; index < armies.Length; index++)
            {
                int province;
                if (index < operationalCount && operationalPosts.Count > 0)
                    province = EvenlySpaced(operationalPosts, index, operationalCount, nation.MapIndex);
                else if (index == armies.Length - 1 || homeland.Count == 1)
                    province = capital;
                else
                    province = EvenlySpaced(homeland, index - operationalCount,
                        Math.Max(1, armies.Length - operationalCount), nation.MapIndex + 31);

                Place(armies[index], province, topology);

                // A campaign that opens in the third year of a war does not open with
                // everyone standing in the open. Divisions posted to a live front start
                // dug in; the rest have a peacetime spadeful.
                armies[index].Entrenchment = activeWar is not null && index < operationalCount
                    ? campaign.Defines.EntrenchmentAtScenarioStart
                    : 0.20f;
            }
        }
    }

    private static void Place(Army army, int province, ProvinceTopology topology)
    {
        if (province <= 0 || province > topology.ProvinceCount) return;
        var center = topology.CenterOf(province);
        army.ProvinceId = province;
        army.StateMapIndex = topology.ProvinceToState[province];
        army.Latitude = center.Latitude;
        army.Longitude = center.Longitude;
        army.Route.Clear();
        army.MoveProgress = 0f;
        army.OrderProvinceId = -1;
        army.OrderStateMapIndex = -1;
    }

    private static int EvenlySpaced(IReadOnlyList<int> values, int index, int count, int salt)
    {
        if (values.Count == 1) return values[0];
        var normalized = (index + 0.5f) / Math.Max(1, count);
        var offset = Math.Abs(salt * 37) % values.Count;
        return values[(offset + Math.Min(values.Count - 1,
            (int)MathF.Floor(normalized * values.Count))) % values.Count];
    }

    private static int Nearest(IReadOnlyList<int> provinces, ProvinceTopology topology,
        float latitude, float longitude)
    {
        var best = provinces[0];
        var bestDistance = float.MaxValue;
        var longitudeScale = MathF.Max(0.12f, MathF.Cos(latitude * M.Pi / 180f));
        foreach (var province in provinces)
        {
            var center = topology.CenterOf(province);
            var dx = MathF.Abs(center.Longitude - longitude);
            dx = MathF.Min(dx, 360f - dx) * longitudeScale;
            var dy = center.Latitude - latitude;
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = province;
        }
        return best;
    }

    private static void Add(Dictionary<ushort, List<int>> target, ushort nation, int province)
    {
        if (!target.TryGetValue(nation, out var list))
        {
            list = new List<int>();
            target[nation] = list;
        }
        list.Add(province);
    }
}
