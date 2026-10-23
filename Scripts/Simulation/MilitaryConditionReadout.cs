using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Read-only local-contact telemetry, not permission to inspect another country's
/// orders or economy. Uses the same current contacts as military planners. Opening
/// a tooltip neither records intelligence nor draws a random number.
/// </summary>
public static class MilitaryConditionReadout
{
    public readonly record struct Condition(bool Known, float Organisation, float Equipment,
        float Divisions, int Formations);

    public static bool CanRead(Campaign campaign, int viewerId, Army army, bool omniscient = false) =>
        campaign is not null && army is not null && float.IsFinite(army.Divisions) &&
        army.Divisions >= .1f && (omniscient || CampaignIntel.CanSeeArmy(campaign, viewerId, army));

    public static Condition Summarise(Campaign campaign, int viewerId, IEnumerable<Army> armies,
        bool omniscient = false)
    {
        if (campaign is null || armies is null) return default;
        var divisions = 0d;
        var organisation = 0d;
        var equipment = 0d;
        var count = 0;
        foreach (var army in armies)
        {
            // Test contact before reading condition. A mixed roster must not include
            // unseen reinforcements in either its values or its formation count.
            if (!CanRead(campaign, viewerId, army, omniscient)) continue;
            divisions += army.Divisions;
            organisation += (double)BattleReadout.Unit(army.Organisation) * army.Divisions;
            equipment += (double)BattleReadout.Unit(army.Equipment) * army.Divisions;
            count++;
        }
        return count == 0 ? default : new(true, (float)(organisation / divisions),
            (float)(equipment / divisions), (float)Math.Min(float.MaxValue, divisions), count);
    }

    public static Condition ForPlayer(Campaign campaign, IEnumerable<Army> armies, bool ignoreFog = false) =>
        campaign?.Player is not { } player ? default :
            Summarise(campaign, player.Id, armies, ignoreFog || !player.IsHuman);

    /// <summary>
    /// The condition of the formations standing in one province, through the same contact
    /// test as everything else here. <paramref name="foreignOnly"/> leaves the player's own
    /// divisions out, so a card that speaks about the enemy never averages them with the
    /// troops the player already commands.
    ///
    /// Called when a province card opens or a selection changes, not every frame.
    /// </summary>
    public static Condition ForPlayerInProvince(Campaign campaign, int province,
        bool foreignOnly = false, bool ignoreFog = false)
    {
        var player = campaign?.Player;
        if (player is null || province <= 0) return default;
        var here = new List<Army>();
        foreach (var army in campaign.Armies)
            if (army.ProvinceId == province && (!foreignOnly || army.NationId != player.Id))
                here.Add(army);
        return Summarise(campaign, player.Id, here, ignoreFog || !player.IsHuman);
    }
}
