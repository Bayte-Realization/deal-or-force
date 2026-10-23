using System;

namespace DealOrForce.Simulation;

/// <summary>
/// Which division can be sent to hold a province, and what the order meant.
///
/// This is deliberately not a new source of troops. A garrison is an ordinary division
/// given an ordinary move order; it keeps its upkeep, its strength and its place in the
/// army, and the player can order it away again. The helper lives in the simulation so the
/// province card and the regression tests ask the same question, and so the choice can be
/// checked on a map without Godot.
/// </summary>
public static class GarrisonPlanner
{
    /// <summary>
    /// The nearest formation that may be sent to <paramref name="provinceId"/>, or null when
    /// every formation is fighting, at sea, shattered, or already standing there. Whether a
    /// land corridor exists is answered by the move command itself, not guessed here.
    /// </summary>
    public static Army NearestFree(Campaign campaign, Nation player, int provinceId)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || player is null || provinceId <= 0 || provinceId > map.ProvinceCount) return null;
        var target = map.CenterOf(provinceId);
        Army best = null;
        var bestDistance = float.MaxValue;
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != player.Id || army.InBattle || army.EmbarkedFleetId >= 0 ||
                army.Divisions <= 0f || army.Personnel <= 0 ||
                army.ProvinceId <= 0 || army.ProvinceId == provinceId) continue;
            var here = map.CenterOf(army.ProvinceId);
            var latitude = here.Latitude - target.Latitude;
            var longitude = MathF.Abs(here.Longitude - target.Longitude);
            if (longitude > 180f) longitude = 360f - longitude;
            var distance = latitude * latitude + longitude * longitude;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = army;
        }
        return best;
    }

    /// <summary>The sentence the player reads after the order, in the current language.</summary>
    public static string ResultText(ArmyMoveResult result) => Loc.Get(result switch
    {
        ArmyMoveResult.Issued or ArmyMoveResult.AttackIssued or ArmyMoveResult.QueuedAfterBattle =>
            "A division is marching to garrison this province.",
        ArmyMoveResult.AlreadyAtDestination => "A division is already there.",
        ArmyMoveResult.NoLandRoute => "No way through by land.",
        ArmyMoveResult.TargetNotControlled => "That ground is not under your control.",
        _ => "The garrison order was refused."
    });
}
