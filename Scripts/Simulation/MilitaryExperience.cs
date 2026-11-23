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

    [ThreadStatic] private static int[] _engagedDenseScratch;
    [ThreadStatic] private static int[] _drillingDenseScratch;
    [ThreadStatic] private static int[] _flyingDenseScratch;
    [ThreadStatic] private static int[] _atSeaDenseScratch;
    [ThreadStatic] private static Dictionary<int, int> _engagedOverflowScratch;
    [ThreadStatic] private static Dictionary<int, int> _drillingOverflowScratch;
    [ThreadStatic] private static Dictionary<int, int> _flyingOverflowScratch;
    [ThreadStatic] private static Dictionary<int, int> _atSeaOverflowScratch;

    private static int[] EnsureCountBuffer(ref int[] buf, int len)
    {
        if (buf is null || buf.Length < len) buf = new int[Math.Max(len, 64)];
        Array.Clear(buf, 0, len);
        return buf;
    }

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

        var maxId = 0;
        var living = campaign.ActiveNations;
        for (var i = 0; i < living.Count; i++)
            if (living[i].Id > maxId) maxId = living[i].Id;
        var denseLen = Math.Min(Math.Max(maxId + 1, campaign.Nations.Count), 16384);

        var engagedArr = EnsureCountBuffer(ref _engagedDenseScratch, denseLen);
        var drillingArr = EnsureCountBuffer(ref _drillingDenseScratch, denseLen);
        var engagedOverflow = _engagedOverflowScratch; engagedOverflow?.Clear();
        var drillingOverflow = _drillingOverflowScratch; drillingOverflow?.Clear();

        var scanned = 0;
        var armies = campaign.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            if (++scanned % 2048 == 0 && Campaign.StageOverdue()) yield return 0;
            var army = armies[i];
            var nid = army.NationId;
            if (army.Training)
            {
                if (army.InBattle) army.Training = false;   // the order yields to the shooting
                else if ((uint)nid < (uint)denseLen) drillingArr[nid]++;
                else (drillingOverflow ??= _drillingOverflowScratch = new())[nid] = drillingOverflow.GetValueOrDefault(nid) + 1;
            }
            if (army.InBattle)
            {
                if ((uint)nid < (uint)denseLen) engagedArr[nid]++;
                else (engagedOverflow ??= _engagedOverflowScratch = new())[nid] = engagedOverflow.GetValueOrDefault(nid) + 1;
            }
        }
        var flyingArr = EnsureCountBuffer(ref _flyingDenseScratch, denseLen);
        var flyingOverflow = _flyingOverflowScratch; flyingOverflow?.Clear();
        var formations = campaign.SupportFormations;
        for (var i = 0; i < formations.Count; i++)
        {
            var formation = formations[i];
            if (formation.Strength > 0.05f && !string.IsNullOrEmpty(formation.Mission) &&
                formation.Mission != SupportCore.MissionStandby)
            {
                var nid = formation.NationId;
                if ((uint)nid < (uint)denseLen) flyingArr[nid]++;
                else (flyingOverflow ??= _flyingOverflowScratch = new())[nid] = flyingOverflow.GetValueOrDefault(nid) + 1;
            }
        }
        var atSeaArr = EnsureCountBuffer(ref _atSeaDenseScratch, denseLen);
        var atSeaOverflow = _atSeaOverflowScratch; atSeaOverflow?.Clear();
        var fleets = campaign.Fleets;
        for (var i = 0; i < fleets.Count; i++)
        {
            var fleet = fleets[i];
            if (fleet.Hulls > 0 && (fleet.InAction || fleet.Mission != FleetMission.Anchored))
            {
                var nid = fleet.NationId;
                if ((uint)nid < (uint)denseLen) atSeaArr[nid]++;
                else (atSeaOverflow ??= _atSeaOverflowScratch = new())[nid] = atSeaOverflow.GetValueOrDefault(nid) + 1;
            }
        }

        var peacetime = defines.ExperiencePeacetimePerWeek / 7f * scale;
        var political = defines.PoliticalPointsPerWeek / 7f * scale;
        var perStability = defines.PoliticalPointsStabilityWeight / 7f * scale;
        var baseline = MathF.Max(0.0001f, defines.BaselineMilitarySpending);
        var fundingWeightScaled = defines.ArmyExperienceFundingWeight / 7f * scale;
        var battleXpScaled = defines.ArmyExperiencePerDivisionDay * scale;
        var trainXpScaled = defines.TrainingExperiencePerDivisionDay * scale;
        var airXpScaled = defines.AirExperiencePerMissionDay * scale;
        var navalXpScaled = defines.NavalExperiencePerHullDay * scale;
        var trainCostScaled = defines.TrainingCostPerDivisionWeek / 7f * scale;
        if (Campaign.StageOverdue()) yield return 0;

        // Every living country side by side: a country's lesson is read off its own budget,
        // strategy and four counts. At normal country counts (<= 1024), running serially avoids
        // allocating closure objects and waking pool threads 24 times every game day.
        var livingCount = living.Count;
        if (livingCount >= 2048 && DeterministicParallel.Enabled(campaign))
        {
            DeterministicParallel.ForRange(campaign, 0, livingCount, (from, to) =>
            {
                for (var index = from; index < to; index++)
                    ApplyHourlyCountryExperience(living[index], denseLen,
                        drillingArr, engagedArr, flyingArr, atSeaArr,
                        drillingOverflow, engagedOverflow, flyingOverflow, atSeaOverflow,
                        baseline, fundingWeightScaled, peacetime, battleXpScaled, trainXpScaled,
                        airXpScaled, navalXpScaled, political, perStability, trainCostScaled);
            }, minItems: 256);
        }
        else
        {
            for (var index = 0; index < livingCount; index++)
                ApplyHourlyCountryExperience(living[index], denseLen,
                    drillingArr, engagedArr, flyingArr, atSeaArr,
                    drillingOverflow, engagedOverflow, flyingOverflow, atSeaOverflow,
                    baseline, fundingWeightScaled, peacetime, battleXpScaled, trainXpScaled,
                    airXpScaled, navalXpScaled, political, perStability, trainCostScaled);
        }

        // A week's cabinet business, not a daily one: which governments keep their army sharp.
        if (Campaign.StageOverdue()) yield return 0;
        if (campaign.ElapsedDays % 7 == 0) AiTraining(campaign);
    }

    private static void ApplyHourlyCountryExperience(
        Nation nation, int denseLen,
        int[] drillingArr, int[] engagedArr, int[] flyingArr, int[] atSeaArr,
        Dictionary<int, int> drillingOverflow, Dictionary<int, int> engagedOverflow,
        Dictionary<int, int> flyingOverflow, Dictionary<int, int> atSeaOverflow,
        float baseline, float fundingWeightScaled, float peacetime, float battleXpScaled,
        float trainXpScaled, float airXpScaled, float navalXpScaled, float political,
        float perStability, float trainCostScaled)
    {
        var nid = nation.Id;
        var funding = MathF.Max(0f, nation.MilitarySpending / baseline - 1f) * fundingWeightScaled;
        var drills = (uint)nid < (uint)denseLen ? drillingArr[nid] : (drillingOverflow?.GetValueOrDefault(nid) ?? 0);
        var battle = (uint)nid < (uint)denseLen ? engagedArr[nid] : (engagedOverflow?.GetValueOrDefault(nid) ?? 0);
        var missions = (uint)nid < (uint)denseLen ? flyingArr[nid] : (flyingOverflow?.GetValueOrDefault(nid) ?? 0);
        var hulls = (uint)nid < (uint)denseLen ? atSeaArr[nid] : (atSeaOverflow?.GetValueOrDefault(nid) ?? 0);
        var strategy = StrategicDoctrines.Of(nation);
        nation.ArmyExperience += peacetime + funding +
            battle * battleXpScaled * (strategy?.BattleExperience ?? 1f) +
            drills * trainXpScaled * (strategy?.DrillExperience ?? 1f);
        nation.AirExperience += peacetime +
            missions * airXpScaled * (strategy?.AirExperience ?? 1f);
        nation.NavalExperience += peacetime +
            hulls * navalXpScaled * (strategy?.NavalExperience ?? 1f);
        nation.PoliticalPoints += political + perStability * M.Clamp01(nation.Stability);
        if (drills > 0)
        {
            var ledgerBefore = nation.Treasury;
            nation.Treasury -= drills * trainCostScaled;
            MoneyLedger.Book(nation, MoneyFlow.Military, nation.Treasury - ledgerBefore);
        }
    }

    /// <summary>
    /// The two questions the weekly cabinet asked of the whole world once per country, answered
    /// for every country in one walk each: who is in a war that has not ended, and which
    /// formations are whose (kept in the order of the campaign's list, so a country's own are
    /// met in the order they always were).
    ///
    /// The cabinet's day is the seventh, and the pass runs on every hour of it. Each run asked
    /// each of five hundred countries about each of three hundred wars - nine in ten of them
    /// long ended - and then walked two thousand formations for every country that was at war
    /// or had room to drill: 0.73 ms a run, 35 ms over fourteen days of a save from 2050.
    /// Nothing the cabinet does here declares or ends a war or raises, disbands or hands over
    /// a formation (it sets the drill flag, and its two commands spend experience on a course
    /// or on one formation), so one walk stands for the whole run. Scratch of the thread.
    /// </summary>
    private sealed class TrainingScratch
    {
        public bool[] AtWar = Array.Empty<bool>();
        public int[] Start = Array.Empty<int>();
        public int[] Next = Array.Empty<int>();
        public Army[] Formations = Array.Empty<Army>();
    }

    [ThreadStatic] private static TrainingScratch _training;

    private static TrainingScratch IndexForTraining(Campaign campaign)
    {
        var scratch = _training ??= new TrainingScratch();
        var nations = campaign.Nations.Count;
        if (scratch.AtWar.Length < nations)
        {
            scratch.AtWar = new bool[nations];
            scratch.Start = new int[nations + 1];
            scratch.Next = new int[nations];
        }
        Array.Clear(scratch.AtWar, 0, nations);
        Array.Clear(scratch.Start, 0, nations + 1);
        var liveWars = Warfare.LiveWars(campaign);
        for (var wi = 0; wi < liveWars.Count; wi++)
        {
            var war = liveWars[wi];
            if (war.Ended) continue;
            if ((uint)war.AttackerId < (uint)nations) scratch.AtWar[war.AttackerId] = true;
            if ((uint)war.DefenderId < (uint)nations) scratch.AtWar[war.DefenderId] = true;
            foreach (var ally in war.AttackerAllies) if ((uint)ally < (uint)nations) scratch.AtWar[ally] = true;
            foreach (var ally in war.DefenderAllies) if ((uint)ally < (uint)nations) scratch.AtWar[ally] = true;
        }

        var armies = campaign.Armies;
        if (scratch.Formations.Length < armies.Count) scratch.Formations = new Army[armies.Count + 64];
        foreach (var army in armies)
            if ((uint)army.NationId < (uint)nations) scratch.Start[army.NationId + 1]++;
        for (var index = 0; index < nations; index++)
        {
            scratch.Start[index + 1] += scratch.Start[index];
            scratch.Next[index] = scratch.Start[index];
        }
        foreach (var army in armies)
            if ((uint)army.NationId < (uint)nations) scratch.Formations[scratch.Next[army.NationId]++] = army;
        return scratch;
    }

    /// <summary>
    /// Governments at peace keep their army sharp. A government at war stops drilling at once -
    /// the divisions are needed where they stand, and the training order clears itself the day a
    /// division fights - and a government without money does not drill at all. Up to
    /// AiTrainingMaxArmies formations per country, the least organised first, because they are
    /// the ones a season of drill changes most. A country's grand strategy can widen that bench
    /// (a cadre army drills more) or narrow it (a levy has no time for it).
    /// </summary>
    [ThreadStatic] private static List<Army> _trainingOwnScratch;

    private static void AiTraining(Campaign campaign)
    {
        var baseLimit = Math.Max(0, campaign.Defines.AiTrainingMaxArmies);
        var world = IndexForTraining(campaign);
        var indexedNations = campaign.Nations.Count;
        var own = _trainingOwnScratch ??= new List<Army>();
        var active = campaign.ActiveNations;
        for (var ni = 0; ni < active.Count; ni++)
        {
            var nation = active[ni];
            if (!Delegation.RunByAi(nation)) continue;
            var limit = Math.Max(0, (int)MathF.Round(baseLimit * StrategicDoctrines.TrainingSlots(nation)));
            // The two tables are kept by id, as the walks they stand for matched by id; a
            // country whose id is outside them is asked the long way.
            var indexed = (uint)nation.Id < (uint)indexedNations;
            var atWar = false;
            if (indexed) atWar = world.AtWar[nation.Id];
            else
            {
                var liveWars = Warfare.LiveWars(campaign);
                for (var wi = 0; wi < liveWars.Count; wi++)
                    if (!liveWars[wi].Ended && liveWars[wi].Involves(nation.Id)) { atWar = true; break; }
            }
            if (atWar)
            {
                if (indexed)
                {
                    for (var at = world.Start[nation.Id]; at < world.Start[nation.Id + 1]; at++)
                        if (world.Formations[at].Training) world.Formations[at].Training = false;
                }
                else
                    for (var index = 0; index < campaign.Armies.Count; index++)
                        if (campaign.Armies[index].NationId == nation.Id && campaign.Armies[index].Training)
                            campaign.Armies[index].Training = false;
                continue;
            }
            var drilledCount = 0;
            own.Clear();
            if (indexed)
            {
                for (var at = world.Start[nation.Id]; at < world.Start[nation.Id + 1]; at++)
                {
                    var army = world.Formations[at];
                    if (army.InBattle) continue;
                    if (army.Training) drilledCount++;
                    else own.Add(army);
                }
            }
            else
            {
                for (var index = 0; index < campaign.Armies.Count; index++)
                {
                    var army = campaign.Armies[index];
                    if (army.NationId != nation.Id || army.InBattle) continue;
                    if (army.Training) drilledCount++;
                    else own.Add(army);
                }
            }
            if (limit == 0 || drilledCount >= limit) { own.Clear(); continue; }
            if (nation.Treasury <= campaign.Defines.AiTreasuryFloor) { own.Clear(); continue; }
            var room = limit - drilledCount;
            // The least organised divisions first among the country's own formations.
            own.Sort(static (left, right) =>
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
                Army candidate = null;
                for (var index = 0; index < own.Count; index++)
                {
                    var army = own[index];
                    if (army.Experience < 0.40f || army.Cohesion < 0.60f) { candidate = army; break; }
                }
                if (candidate is not null)
                {
                    new RetrainArmyCommand { IssuerId = nation.Id, ArmyId = candidate.Id }.Apply(campaign);
                }
            }
            own.Clear();
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
