using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Political points a week at the plain case: the floor under what a government can push through.</summary>
    public float PoliticalPointsPerWeek { get; set; } = 1.5f;

    /// <summary>What stability adds to the count above, per week: a settled government can do more.</summary>
    public float PoliticalPointsStabilityWeight { get; set; } = 1.5f;

    /// <summary>The trickle every service learns in peace, per week. Small on purpose: the schooling is the war.</summary>
    public float ExperiencePeacetimePerWeek { get; set; } = 0.2f;

    /// <summary>One division's day in battle, in army experience.</summary>
    public float ArmyExperiencePerDivisionDay { get; set; } = 0.02f;

    /// <summary>One air formation's day on a real mission, in air experience.</summary>
    public float AirExperiencePerMissionDay { get; set; } = 0.05f;

    /// <summary>One hull's day at sea or in action, in naval experience.</summary>
    public float NavalExperiencePerHullDay { get; set; } = 0.02f;

    /// <summary>Army experience a week per whole multiple of the baseline military budget spent above it.</summary>
    public float ArmyExperienceFundingWeight { get; set; } = 0.6f;

    /// <summary>One training division's day of drill, in army experience. Worth more than a day of battle.</summary>
    public float TrainingExperiencePerDivisionDay { get; set; } = 0.05f;

    /// <summary>What one division's week of drill costs. Exercises are cheaper than war and not free.</summary>
    public float TrainingCostPerDivisionWeek { get; set; } = 0.005f;

    /// <summary>How many formations an AI government keeps drilling in peace.</summary>
    public int AiTrainingMaxArmies { get; set; } = 3;
}

public sealed partial class Army
{
    /// <summary>
    /// Whether this formation is drilling rather than standing ready (SetTrainingCommand). A
    /// training order clears itself the day the division fights: nobody can drill under fire.
    /// </summary>
    public bool Training;
}

/// <summary>
/// The three services' experience and the government's political credit: who does the work, and
/// who gets to spend what the work earns.
///
/// Measured from what the world is already doing, never granted by a script. An army learns on
/// the days its divisions are in battle, on the days they drill (Training), and - above the
/// baseline - out of what the country pays for it. A wing learns while it flies real missions and
/// not while it sits at standby; a fleet learns while it is at sea or in action. Political points
/// are the odd one out: they accrue from a stable government simply governing, which is why the
/// count is small and the spending - a rewritten war Doctrine, later decisions and laws - is where
/// the scarcity lives.
///
/// Nothing here is a copy of a casualty count. A battle that goes badly for months teaches as much
/// as one that goes well, which is the thing armies actually say about it; weighting by blood
/// spilled is a refinement, not an assumption, and is not claimed here.
/// </summary>
public static class MilitaryExperience
{
    public const string Army = "army";
    public const string Air = "air";
    public const string Navy = "navy";

    /// <summary>
    /// The general staff's own purse, which is the government's political credit. A general book
    /// of war is the one kind of doctrine that is not bought with a service's experience: it is
    /// bought with what the cabinet can push through, which is why it is slow and why it competes
    /// with every other decision a government has to sell.
    /// </summary>
    public const string Command = "command";

    private static readonly Dictionary<int, int> Engaged = new();
    private static readonly Dictionary<int, int> Drilling = new();
    private static readonly Dictionary<int, int> Flying = new();
    private static readonly Dictionary<int, int> AtSea = new();

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same experience pass, yielded between its tally phases and countries.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || campaign.Nations.Count == 0) yield break;
        var defines = campaign.Defines;
        var scale = MathF.Max(0f, campaign.TickScale);

        // One pass over the world, then one over the countries: asking each of two hundred and
        // sixty countries to walk the army list is the mistake the AI tables already fixed once.
        var engaged = Engaged; engaged.Clear();
        var drilling = Drilling; drilling.Clear();
        var scanned = 0;
        foreach (var army in campaign.Armies)
        {
            if (++scanned % 512 == 0) yield return 0;
            if (army.Training)
            {
                if (army.InBattle) army.Training = false;   // the order yields to the shooting
                else drilling[army.NationId] = drilling.GetValueOrDefault(army.NationId) + 1;
            }
            if (army.InBattle) engaged[army.NationId] = engaged.GetValueOrDefault(army.NationId) + 1;
        }
        var flying = Flying; flying.Clear();
        foreach (var formation in campaign.SupportFormations)
            if (formation.Strength > 0.05f && !string.IsNullOrEmpty(formation.Mission) &&
                formation.Mission != SupportCore.MissionStandby)
                flying[formation.NationId] = flying.GetValueOrDefault(formation.NationId) + 1;
        var atSea = AtSea; atSea.Clear();
        foreach (var fleet in campaign.Fleets)
            if (fleet.Hulls > 0 && (fleet.InAction || fleet.Mission != FleetMission.Anchored))
                atSea[fleet.NationId] = atSea.GetValueOrDefault(fleet.NationId) + 1;

        var peacetime = defines.ExperiencePeacetimePerWeek / 7f * scale;
        var political = defines.PoliticalPointsPerWeek / 7f * scale;
        var perStability = defines.PoliticalPointsStabilityWeight / 7f * scale;
        var baseline = MathF.Max(0.0001f, defines.BaselineMilitarySpending);
        yield return 0;
        var ticked = 0;
        foreach (var nation in campaign.Nations)
        {
            if (++ticked % 16 == 0) yield return 0;
            // Money above what the army costs anyway is what buys training value: a budget at
            // the baseline teaches nobody anything new, and a cut teaches nobody faster.
            var funding = MathF.Max(0f, nation.MilitarySpending / baseline - 1f) *
                          defines.ArmyExperienceFundingWeight / 7f * scale;
            var drills = drilling.GetValueOrDefault(nation.Id);
            var battle = engaged.GetValueOrDefault(nation.Id);
            var missions = flying.GetValueOrDefault(nation.Id);
            var hulls = atSea.GetValueOrDefault(nation.Id);
            // The grand strategy's hand on learning: a cadre drills its experience up in peace, a
            // war of attrition learns it in the line, and a country that has chosen no course
            // learns exactly as it always did.
            nation.ArmyExperience += peacetime + funding +
                battle * defines.ArmyExperiencePerDivisionDay * scale * StrategicDoctrines.BattleExperience(nation) +
                drills * defines.TrainingExperiencePerDivisionDay * scale * StrategicDoctrines.DrillExperience(nation);
            nation.AirExperience += peacetime +
                missions * defines.AirExperiencePerMissionDay * scale * StrategicDoctrines.AirExperience(nation);
            nation.NavalExperience += peacetime +
                hulls * defines.NavalExperiencePerHullDay * scale * StrategicDoctrines.NavalExperience(nation);
            nation.PoliticalPoints += political + perStability * M.Clamp01(nation.Stability);
            if (drills > 0)
                nation.Treasury -= drills * defines.TrainingCostPerDivisionWeek / 7f * scale;
        }

        // A week's cabinet business, not a daily one: which governments keep their army sharp.
        yield return 0;
        if (campaign.ElapsedDays % 7 == 0) AiTraining(campaign);
    }

    /// <summary>
    /// Governments at peace keep their army sharp. A government at war stops drilling at once -
    /// the divisions are needed where they stand, and the training order clears itself the day a
    /// division fights - and a government without money does not drill at all. Up to
    /// AiTrainingMaxArmies formations per country, the least organised first, because they are
    /// the ones a season of drill changes most. A country's grand strategy can widen that bench
    /// (a cadre army drills more) or narrow it (a levy has no time for it).
    /// </summary>
    private static void AiTraining(Campaign campaign)
    {
        var baseLimit = Math.Max(0, campaign.Defines.AiTrainingMaxArmies);
        var drilled = new Dictionary<int, int>();
        if (baseLimit > 0)
            foreach (var army in campaign.Armies)
                if (army.Training && !army.InBattle)
                    drilled[army.NationId] = drilled.GetValueOrDefault(army.NationId) + 1;
        foreach (var nation in campaign.Nations)
        {
            if (!Delegation.RunByAi(nation)) continue;
            var limit = Math.Max(0, (int)MathF.Round(baseLimit * StrategicDoctrines.TrainingSlots(nation)));
            var atWar = false;
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
            if (atWar)
            {
                for (var index = 0; index < campaign.Armies.Count; index++)
                    if (campaign.Armies[index].NationId == nation.Id && campaign.Armies[index].Training)
                        campaign.Armies[index].Training = false;
                continue;
            }
            if (limit == 0 || drilled.GetValueOrDefault(nation.Id) >= limit) continue;
            if (nation.Treasury <= campaign.Defines.AiTreasuryFloor) continue;
            var room = limit - drilled.GetValueOrDefault(nation.Id);
            // The least organised divisions first among the country's own formations.
            var own = new List<Army>();
            foreach (var army in campaign.Armies)
                if (army.NationId == nation.Id && !army.Training && !army.InBattle) own.Add(army);
            own.Sort((left, right) =>
            {
                var cmp = left.Organisation.CompareTo(right.Organisation);
                return cmp != 0 ? cmp : left.Id.CompareTo(right.Id);
            });
            for (var index = 0; index < own.Count && room > 0; index++, room--) own[index].Training = true;

            // Put accumulated army experience back into the forces instead of hoarding thousands.
            if (nation.ArmyExperience >= 40f && !ModifierSystem.Has(nation, "intensive_field_course") && nation.Treasury >= 0.1f)
            {
                new ConductMilitaryExerciseCommand { IssuerId = nation.Id }.Apply(campaign);
            }
            else if (nation.ArmyExperience >= 35f)
            {
                var candidate = own.FirstOrDefault(a => a.Experience < 0.40f || a.Cohesion < 0.60f);
                if (candidate is not null)
                {
                    new RetrainArmyCommand { IssuerId = nation.Id, ArmyId = candidate.Id }.Apply(campaign);
                }
            }
        }
    }
}

/// <summary>Drill a formation, or stand it down (SetTrainingCommand). Drilling earns experience and costs money.</summary>
public sealed class SetTrainingCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public bool Training { get; init; } = true;

    /// <summary>Why the order could not be carried out, in the words the interface shows.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null) { Refusal = Loc.Get("That formation no longer exists."); return; }
        if (army.NationId != IssuerId) { Refusal = Loc.Get("That formation is not ours."); return; }
        if (Training && army.InBattle)
        {
            Refusal = Loc.Get("A division in battle cannot drill.");
            return;
        }
        army.Training = Training;
    }
}
