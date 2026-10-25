using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Recently reconquered regions cannot immediately found another armed breakaway. Zero disables the protection.</summary>
    public int ReintegrationRecoveryDays { get; set; } = 730;
    public int SecessionResponseDays { get; set; } = 60;
}

/// <summary>One recovery clock per holder and original region, in the existing saved decision ledger.
/// A split inherits its origin; a peaceful handover/restoration explicitly authorised by the holder is not an armed rising.</summary>
public static class RevoltRecovery
{
    internal const string Prefix = "reintegration_recovery/";
    static string Key(ScenarioState region) => Prefix + TerritorialClaims.Origin(region);

    public static int DaysLeft(Campaign c, Nation holder, ScenarioState region) =>
        c is null || holder is null || region is null || c.Defines.ReintegrationRecoveryDays <= 0 ||
        region.NationMapIndex != holder.MapIndex ? 0 :
        (int)Math.Clamp((long)holder.DecisionAvailableDay.GetValueOrDefault(Key(region)) - c.ElapsedDays, 0, int.MaxValue);

    internal static void Protect(Campaign c, Nation holder, ScenarioState region, int day)
    {
        if (c.Defines.ReintegrationRecoveryDays <= 0 || region is null) return;
        var until = (int)Math.Min(int.MaxValue, (long)day + c.Defines.ReintegrationRecoveryDays);
        if (until > c.ElapsedDays)
            holder.DecisionAvailableDay[Key(region)] = Math.Max(holder.DecisionAvailableDay.GetValueOrDefault(Key(region)), until);
    }

    /// <summary>Older saves already record a crushed breakaway as negative pressure and a dated peace.
    /// Infer only this evidenced recovery, not ownership changes or new wars. Idempotent on snapshot reload.</summary>
    internal static void RestoreLegacy(Campaign c)
    {
        if (c.Defines.ReintegrationRecoveryDays <= 0) return;
        foreach (var war in c.Wars)
        {
            if (!war.Ended || war.EndedDay < 0 || war.EndedDay > c.ElapsedDays ||
                c.ElapsedDays - (long)war.EndedDay >= c.Defines.ReintegrationRecoveryDays) continue;
            var winner = c.NationById(war.WinnerId);
            if (winner is null) continue;
            foreach (var enemyId in war.EnemiesOf(winner.Id))
            {
                var enemy = c.NationById(enemyId);
                if (!Reintegration.IsBreakawayOf(enemy, winner) || !c.Homelands.TryGetValue(enemyId, out var homeland)) continue;
                foreach (var region in c.StatesOf(winner))
                    if (region.SecessionPressure < 0 && (homeland.Contains(region.MapIndex) || homeland.Contains(TerritorialClaims.Origin(region))) &&
                        !winner.DecisionAvailableDay.ContainsKey(Key(region)))
                        Protect(c, winner, region, war.EndedDay);
            }
        }
    }
}
