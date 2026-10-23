using System;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A nationwide field exercise program funded by accumulated military experience and state coffers.
/// Trains officers and staff, boosting reinforcement rates and organization recovery across all fronts.
/// </summary>
public sealed class ConductMilitaryExerciseCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public bool Succeeded { get; private set; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Succeeded = false; Refusal = "";
        if (campaign is null) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) { Refusal = Loc.Get("Select a valid country."); return; }
        if (nation.ArmyExperience < 25f)
        {
            Refusal = Loc.Format("Insufficient army experience: need 25, available {0:0.#}.", nation.ArmyExperience);
            return;
        }
        if (nation.Treasury < 0.05f)
        {
            Refusal = Loc.Get("Insufficient treasury to organize field maneuvers.");
            return;
        }
        nation.ArmyExperience -= 25f;
        nation.Treasury -= 0.05f;
        ModifierSystem.Grant(campaign, nation, "intensive_field_course", 240);
        Succeeded = true;
        campaign.Chronicle.Record(campaign, "military_exercises_conducted", IssuerId, -1);
        campaign.MarkStrategicChange();
    }
}

/// <summary>
/// Drill and reorganize a specific formation using accumulated military experience.
/// Injects veteran staff methods, restoring cohesion and combat readiness.
/// </summary>
public sealed class RetrainArmyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public bool Succeeded { get; private set; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Succeeded = false; Refusal = "";
        if (campaign is null) return;
        var nation = campaign.NationById(IssuerId);
        var army = campaign.Armies.Find(a => a.Id == ArmyId && a.NationId == IssuerId);
        if (nation is null || army is null) { Refusal = Loc.Get("Select a friendly army."); return; }
        if (army.InBattle) { Refusal = Loc.Get("Formations in battle cannot be retrained."); return; }
        var cost = MathF.Max(12f, MathF.Round(army.Divisions * 4f));
        if (nation.ArmyExperience < cost)
        {
            Refusal = Loc.Format("Insufficient army experience: need {0:0}, available {1:0.#}.", cost, nation.ArmyExperience);
            return;
        }
        nation.ArmyExperience -= cost;
        army.Experience = Math.Clamp(army.Experience + 0.12f, 0f, 1f);
        army.Cohesion = Math.Clamp(army.Cohesion + 0.25f, 0f, 1f);
        army.Organisation = Math.Clamp(army.Organisation + 0.20f, 0f, 1f);
        Succeeded = true;
        campaign.MarkStrategicChange();
    }
}
