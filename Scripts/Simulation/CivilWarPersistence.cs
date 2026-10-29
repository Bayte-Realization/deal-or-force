using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int CivilWarNegotiationDays { get; set; } = 540;
    public int CivilWarEscalationDays { get; set; } = 90;
    public int CivilWarEscalationStages { get; set; } = 3;
    public int CivilWarReserveFormations { get; set; } = 6;
    public float CivilWarReserveCost { get; set; } = .02f;
}

public sealed partial class StrategicWar
{
    public bool CivilConflict;
    public int EscalationStage;
    /// <summary>Who put the peace that ended it, or -1 while it goes on.</summary>
    public int PeaceProposerId = -1;
}

/// <summary>A prolonged civil conflict can mobilise reserves, not spawn endless free armies.</summary>
public static class CivilWarPersistence
{
    public static bool NegotiationLocked(Campaign c, StrategicWar war) =>
        c.ElapsedDays < war.AiNegotiationUnlockDay &&
        (war.CivilConflict || MathF.Abs(war.WarScore) < c.Defines.AiDecisiveWarScore);

    public static int StageDue(Campaign c, StrategicWar war) => Math.Clamp(
        (c.ElapsedDays - war.StartedDay) / Math.Max(1, c.Defines.CivilWarEscalationDays),
        0, Math.Max(0, c.Defines.CivilWarEscalationStages));

    public static void Tick(Campaign c)
    {
        if (!MechanicFeatures.SystemEnabled(c, nameof(CivilWarPersistence)) || !c.FeatureEnabled("militia")) return;
        foreach (var war in c.Wars)
        {
            if (war.Ended || !war.CivilConflict || c.ProvinceMap is null) continue;
            var due = StageDue(c, war);
            if (due <= war.EscalationStage || war.AttackerLosses + war.DefenderLosses <= .001f ||
                war.AttackerFront.Count + war.DefenderFront.Count == 0) continue;
            war.EscalationStage = due; // No backdated bursts when loading an old campaign.
            var raised = 0;
            foreach (var id in new[] { war.AttackerId, war.DefenderId })
            {
                var nation = c.NationById(id);
                // The same reserve for everybody; a player's is raised when the player has
                // handed the army to its staff, since it spends the player's men and money.
                if (nation is null || (nation.IsHuman && !Delegation.Holds(nation, Delegation.Army))) continue;
                var standing = c.Armies.Count(a => a.NationId == id && a.TemplateId == MilitiaCore.TemplateId);
                var ceiling = Math.Min(Math.Max(0, c.Defines.CivilWarReserveFormations),
                    (int)Math.Min(int.MaxValue, nation.Population / Math.Max(1f, MilitiaCore.Weights.PopulationPerFormation)));
                if (standing >= ceiling || nation.ManpowerReserve < MilitiaCore.Weights.Formation ||
                    nation.Treasury < Math.Max(0f, c.Defines.CivilWarReserveCost)) continue;
                foreach (var region in c.StatesOf(nation).OrderByDescending(s => s.Population).ThenBy(s => s.MapIndex))
                {
                    if (!MilitiaCore.Found(c, nation, region)) continue;
                    nation.ManpowerReserve -= MilitiaCore.Weights.Formation;
                    nation.Treasury -= Math.Max(0f, c.Defines.CivilWarReserveCost);
                    raised++;
                    break;
                }
            }
            if (raised == 0) continue;
            FrontPlanner.ReassignAi(c, war);
            c.Chronicle.Record(c, "civil_war_escalated", war.AttackerId, war.DefenderId);
        }
    }
}
