using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A signed whole-state surrender uses the existing integration ledger,
/// not just a map repaint. An addressed human never silently loses other hotseat/network seats.</summary>
public static class UltimatumAnnexation
{
    public static bool Eligible(Campaign c, Nation target) => c?.ProvinceMap is not null && target is not null &&
        c.HasOwnedLand(target) && !c.Fallen.Contains(target.Id) &&
        !c.Wars.Any(war => !war.Ended && war.Involves(target.Id)) &&
        Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToNation[p] == target.MapIndex)
            .All(p => (c.ProvinceController ?? c.ProvinceMap.ProvinceToNation)[p] == target.MapIndex);

    internal static void Apply(Campaign c, Nation demander, Nation target)
    {
        // Initialise each country's own historical stores before joining them. Otherwise
        // a later stockpile seed could create a second initial reserve after the merger.
        StrategicStockpiles.EnsureSeeded(c, demander);
        StrategicStockpiles.EnsureSeeded(c, target);
        foreach (var (good, amount) in target.Stockpiles)
            demander.Stockpiles[good] = demander.Stockpiles.GetValueOrDefault(good) + MathF.Max(0, amount);
        target.Stockpiles.Clear();
        PeacefulFormation.IntegratePartner(c, demander, target, transferPlayerSeat: false, combineCultures: false);
    }
}
