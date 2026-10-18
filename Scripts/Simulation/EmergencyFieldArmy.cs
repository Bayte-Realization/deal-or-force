using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class FrontPlanner
{
    /// <summary>
    /// A state whose last regular formation was lost can still detach a small field reserve
    /// from its existing local militia. Keep at least one uncommitted guard in each donor
    /// region and at most one field formation per owned region; no new men or equipment.
    /// Existing detached units keep their place as they march, rather than drawing another
    /// village's militia on every reassignment. Ordinary armies never consume local guards.
    /// </summary>
    public static List<Army> FieldArmy(Campaign c, int nationId)
    {
        var regulars = c.Armies.FindAll(a => a.NationId == nationId && a.TemplateId != MilitiaCore.TemplateId);
        var hasRegulars = regulars.Count > 0;
        var nation = c.NationById(nationId);
        if (nation is null || nation.Landless) return regulars;
        var limit = c.StatesOf(nation).Length;
        if (limit == 0) return regulars;
        var militia = c.Armies.Where(a => a.NationId == nationId && a.TemplateId == MilitiaCore.TemplateId &&
            a.EmbarkedFleetId < 0 && !a.DirectOrder && a.FrontLineId < 0).OrderBy(a => a.Id).ToArray();
        // These are already detached. Their current region need not be the one of birth.
        foreach (var army in militia)
            if (army.AssignedWarId >= 0 && c.Wars.Any(w => !w.Ended && w.Id == army.AssignedWarId) &&
                (hasRegulars || regulars.Count < limit))
                regulars.Add(army);
        if (hasRegulars) return regulars;
        foreach (var group in militia.Where(a => !regulars.Contains(a)).GroupBy(a => a.StateMapIndex).OrderBy(g => g.Key))
        {
            if (regulars.Count >= limit) break;
            if (group.Key < 0 || group.Count() < 2) continue;
            var candidate = group.FirstOrDefault(a => !a.InBattle && a.Route.Count == 0 && a.PlannedRoute.Count == 0);
            if (candidate is not null) regulars.Add(candidate);
        }
        return regulars;
    }

    internal static bool NeedsEmergencyStaff(Campaign c, StrategicWar war) =>
        war.Side(true).Concat(war.Side(false)).Any(id =>
            c.NationById(id) is { IsHuman: false } &&
            !c.Armies.Any(a => a.NationId == id && a.TemplateId != MilitiaCore.TemplateId) &&
            c.Armies.Any(a => a.NationId == id && a.TemplateId == MilitiaCore.TemplateId));
}
