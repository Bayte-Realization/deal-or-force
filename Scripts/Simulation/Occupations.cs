using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// An occupation lasts as long as the war it belongs to.
///
/// A province held by a country that does not own it is a war's business, and every peace
/// hands back what it does not transfer. But a war can also end without a peace - two
/// countries become overlord and subject, a treaty ends a war while one side is still
/// standing on a third country's ground, a country leaves by a road nobody thought to give
/// back the land on - and every such road used to leave the troops where they were: a
/// province of Turkey held for years by a Georgian provisional government nobody was
/// fighting, the stripes of an occupation on a map with no war on it.
///
/// So once a day, any province held by a country that is not at war with its owner goes
/// back to the owner. Not by a treaty, not by a rule for each road - by the plain fact
/// that there is no war left to hold it for. The one exception is ground a scenario opens
/// occupied on purpose (a region's "controller" in its history): that stands until a war
/// changes it, because that is what its author asked for.
/// </summary>
public static class Occupations
{
    private static readonly HashSet<long> Enemies = new();
    private static readonly Dictionary<int, string> WrittenHolder = new();

    private static long Pair(int holder, int owner) => ((long)holder << 32) | (uint)owner;

    public static int Tick(Campaign campaign)
    {
        var lifted = 0;
        foreach (var stage in TickStages(campaign)) lifted = stage;
        return lifted;
    }

    /// <summary>The same sweep, yielded between province chunks so a full-map walk does not
    /// land in one frame. Each yield carries the count lifted so far, so draining the stages
    /// still answers <see cref="Tick"/>'s question.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign campaign)
    {
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (map is null || controllers is null) yield break;

        Enemies.Clear();
        // Ground is held for a war that is still going. An ended war stays in the record
        // and does not keep a province, and a quiet day does not walk it to learn that.
        var live = Warfare.LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war is null || war.Ended) continue;
            foreach (var attacker in war.Side(true))
                foreach (var defender in war.Side(false))
                {
                    Enemies.Add(Pair(attacker, defender));
                    Enemies.Add(Pair(defender, attacker));
                }
        }
        yield return 0;

        var written = false;
        var lifted = 0;
        var last = System.Math.Min(map.ProvinceCount, controllers.Length - 1);
        for (var province = 1; province <= last; province++)
        {
            if (province % 2048 == 0) { yield return lifted; }
            var owner = map.ProvinceToNation[province];
            var holder = controllers[province];
            if (holder == owner || owner == 0 || map.IsWater(province)) continue;
            // Land whose owner no longer exists stays with whoever holds it.
            var owning = NavalOperations.NationAt(campaign, owner);
            if (owning is null) continue;
            var holding = NavalOperations.NationAt(campaign, holder);
            if (holding is not null)
            {
                if (Enemies.Contains(Pair(holding.Id, owning.Id))) continue;
                // A lease, or troops in without a war: held by arrangement, not by a war.
                if (LandArrangementSystem.Covers(campaign, province, holding.Id)) continue;
                if (!written)
                {
                    written = true;
                    WrittenHolder.Clear();
                    foreach (var state in campaign.States)
                        if (!string.IsNullOrEmpty(state.StartController)) WrittenHolder[state.MapIndex] = state.StartController;
                }
                if (WrittenHolder.TryGetValue(map.ProvinceToState[province], out var tag) &&
                    string.Equals(tag, holding.Tag, System.StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (campaign.SetProvinceController(province, owner)) lifted++;
        }
        if (lifted > 0)
        {
            campaign.MarkMapChanged();
            DiagnosticLogs.Write("simulation", $"OCCUPATION_LIFTED provinces={lifted} day={campaign.ElapsedDays}");
        }
        yield return lifted;
    }
}
