using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>A spent defender without an escape route can still hold while men and stores remain.</summary>
    public bool PocketResistanceEnabled { get; set; } = true;
    /// <summary>Below this strength, lost organisation is a defeat even in a closed pocket.</summary>
    public float PocketResistanceMinStrength { get; set; } = .25f;
    /// <summary>Minimum remaining delivered supply when local stores have already run out.</summary>
    public float PocketResistanceMinSupply { get; set; } = .10f;
}

/// <summary>
/// No road of retreat is not itself the destruction of a garrison. The existing battle
/// continues, with its ordinary fire, losses and low-organisation penalties; no strength,
/// organisation or stores are granted here. Hunger and a sufficiently weakened defence
/// still end it. Evaluated before Capture changes control or moves either side.
/// </summary>
public static class PocketResistance
{
    public const string BattleNote = "Encircled defenders hold their position; there is no safe retreat.";

    public static bool CanContinue(Campaign c, StrategicWar war, int province, IReadOnlyCollection<Army> defenders)
    {
        if (c?.Defines is not { PocketResistanceEnabled: true } d || war is null || war.Ended ||
            defenders is null || c.ProvinceMap is null || c.ProvinceController is null || province <= 0 ||
            province > c.ProvinceMap.ProvinceCount) return false;
        var strength = MathF.Max(d.DivisionBrokenStrength,
            float.IsFinite(d.PocketResistanceMinStrength) ? M.Clamp01(d.PocketResistanceMinStrength) : .25f);
        var supply = float.IsFinite(d.PocketResistanceMinSupply) ? M.Clamp01(d.PocketResistanceMinSupply) : .10f;
        foreach (var army in defenders)
        {
            if (army.ProvinceId != province || army.EmbarkedFleetId >= 0 || army.Divisions < .1f ||
                army.Strength <= strength || !war.Involves(army.NationId) ||
                !WarCoalition.FriendlyController(c, war, army.NationId, c.ProvinceController[province]) ||
                !(army.PocketStores > 0f || army.SupplyLevel > supply)) continue;
            // Do not trust the three-day logistics trace to decide whether a ring just closed.
            if (WarCoalition.SpreadRetreatProvince(c, war, army, province, beaten: true) <= 0) return true;
        }
        return false;
    }
}
