using System;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class Movements
{
    /// <summary>
    /// An explicit data link, not a name match: a political bureau and a territorial faction
    /// can be distinct actors, even when their names happen to be similar.
    /// </summary>
    public static Nation TerritorialActor(Campaign campaign, MovementDefinition definition)
    {
        if (campaign is null || string.IsNullOrWhiteSpace(definition?.TerritorialFaction)) return null;
        foreach (var nation in campaign.Nations)
            if (string.Equals(nation.FactionId, definition.TerritorialFaction, StringComparison.Ordinal) &&
                !nation.Landless && campaign.StatesOf(nation).Length > 0)
                return nation;
        return null;
    }

    /// <summary>
    /// Retire an unused duplicate from an older world on a simulation step, never while
    /// loading a save/network snapshot or drawing the map. Keep its id for old references.
    /// Do not erase a player's organisation, an exile or a campaign actor that has acquired
    /// territory, a military commitment or a war of its own.
    /// </summary>
    public static int RetireDuplicateRepresentations(Campaign campaign)
    {
        if (campaign is null) return 0;
        var retired = 0;
        foreach (var nation in campaign.Nations)
        {
            if (nation.Movement is not { Landless: true, Exile: false, Dissolved: false } ||
                nation.IsHuman || nation.IsPlayer || campaign.HumanSeats.Contains(nation.Id)) continue;
            var definition = MovementCatalog.Of(nation.Movement.Id);
            var actor = TerritorialActor(campaign, definition);
            if (actor is null || actor == nation || campaign.StatesOf(nation).Length > 0) continue;
            if (campaign.Armies.Any(a => a.NationId == nation.Id) ||
                campaign.Fleets.Any(f => f.NationId == nation.Id) ||
                campaign.SupportFormations.Any(f => f.NationId == nation.Id) ||
                campaign.Recruitment.Any(o => o.NationId == nation.Id) ||
                campaign.SupportOrders.Any(o => o.NationId == nation.Id) ||
                campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id))) continue;
            MovementFall.Dissolve(campaign, nation,
                Loc.Get("This organisation is already represented by its territorial administration."));
            retired++;
        }
        return retired;
    }
}
