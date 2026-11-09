using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Escalation: the ladder from peace to war, climbed and descended, between every pair
/// of countries in the world.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — the escalation ladder explained:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// Every pair of countries has a tension number from 0 to 100 and a stage:
///   0–24  Peace      — nothing happens.
///  25–49 Friction   — border incidents, spy exposure, words at the UN.
///  50–69 Crisis     — ambassadors recalled, mobilisation on the news.
///  70+   Armed clashes — shooting on the border, casualties, rally.
///
/// Each stage begins at the defines below and, once entered, requires tension to
/// fall BELOW (stage threshold − hysteresis) to leave: a crisis does not end
/// because tension dipped to 49 for a week.
///
/// WHAT PUSHES TENSION UP: hostility, grievances (lost land, lost people), rivalry
/// stance, proxy wars against each other, opposing ideologies. Neighbours feel all
/// of it; countries an ocean apart feel half. Allies feel almost none.
///
/// WHAT PUSHES IT DOWN: time (the fall rate), a non-aggression pact, trade, and
/// the peace negotiations the situation room offers.
///
/// WHAT TO CHANGE:
///   • `common/escalation/*.json` — incidents (what happens at friction/crisis).
///   • The defines below — every threshold, rate and weight.
///   • To make the world calmer: raise TensionFallRate, lower IncidentChance.
///   • To make it hair-trigger: raise TensionCrisisAt to 40 and IncidentChance to 0.15.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed partial class Defines
{
    /// <summary>How often the tension between two countries is weighed, in days.</summary>
    public int EscalationReviewDays { get; set; } = 7;
    /// <summary>The share of the way to where tension is heading that it covers in a review, rising and falling.</summary>
    public float TensionRiseRate { get; set; } = 0.12f;
    public float TensionFallRate { get; set; } = 0.06f;
    /// <summary>Where the stages begin: friction, crisis, armed clashes. Leaving a stage takes the hysteresis less.</summary>
    public float TensionFrictionAt { get; set; } = 25f;
    public float TensionCrisisAt { get; set; } = 50f;
    public float TensionClashesAt { get; set; } = 70f;
    public float TensionHysteresis { get; set; } = 6f;
    /// <summary>What each reason adds to where tension is heading, in points of 100.</summary>
    public float TensionFromHostility { get; set; } = 0.20f;
    public float TensionFromGrievance { get; set; } = 0.45f;
    public float TensionFromLostLand { get; set; } = 12f;
    public float TensionFromRivalry { get; set; } = 10f;
    public float TensionFromProxy { get; set; } = 10f;
    public float TensionFromIdeology { get; set; } = 8f;
    /// <summary>Neighbours feel all of it; countries an ocean apart feel this much of it.</summary>
    public float TensionBorderFactor { get; set; } = 1.0f;
    public float TensionDistantFactor { get; set; } = 0.5f;
    public float TensionAllianceFactor { get; set; } = 0.15f;
    public float TensionPactFactor { get; set; } = 0.6f;
    public float TensionTradeFactor { get; set; } = 0.85f;
    /// <summary>The weekly chance of an incident at full tension; it falls with the square of tension.</summary>
    public float IncidentChance { get; set; } = 0.07f;
    /// <summary>A grievance halves in this many years: long enough for a war to be the next generation's.</summary>
    public float GrievanceHalfLifeYears { get; set; } = 30f;
    /// <summary>
    /// How fast where a standoff stood on the first day stops mattering: the difference between
    /// the history's tension and what the reasons alone give halves in this many years.
    /// </summary>
    public float TensionBaselineHalfLifeYears { get; set; } = 10f;
    public float GrievancePerProvinceLost { get; set; } = 1.5f;
    public float GrievancePerLossShare { get; set; } = 2500f;
    public float GrievanceCap { get; set; } = 100f;
    /// <summary>Men lost on each side a week while two countries are exchanging fire, and the rally it brings.</summary>
    public float ClashLossesPerWeek { get; set; } = 12f;
    public float ClashWarSupportPerWeek { get; set; } = 0.001f;
    /// <summary>How much a crisis argues for a war in the AI's sum, at full tension.</summary>
    public float AiEscalationAppetite { get; set; } = 0.9f;
    /// <summary>How long a shared border has to be shooting before a border war can come of it, the chance of one a week after that, and how long it is fought before the ceasefire.</summary>
    public int BorderWarAfterDays { get; set; } = 60;
    public float BorderWarChancePerWeek { get; set; } = 0.025f;
    /// <summary>How strong a side has to be, against the other and the friends behind it, to open a border war.</summary>
    public float BorderWarStrengthRatio { get; set; } = 0.6f;
    public int BorderWarDays { get; set; } = 45;
    /// <summary>How long after one border war the same two countries can have another: India and
    /// Pakistan were fighting the same seven-week war every five years for a century.</summary>
    public int BorderWarCooldownDays { get; set; } = 2920;
    /// <summary>Talks: how long, how much they cost, how much tension they take off a week, how often.</summary>
    public int TalksDays { get; set; } = 120;
    public float TalksCost { get; set; } = 0.25f;
    public float TalksTensionPerWeek { get; set; } = 2.5f;
    public int TalksCooldownDays { get; set; } = 365;
    /// <summary>
    /// A pair that is not at war, and has a rival stance, a refused ultimatum, a claim on land
    /// the other holds, or opinion at or under <see cref="AiRivalryThreshold"/> on a shared
    /// border, takes one step up the tension ladder. A player is handed the existing ultimatum
    /// instead of a war. False leaves that pair to the weekly drift above.
    /// </summary>
    public bool QuarrelStep { get; set; } = true;
    /// <summary>How long the same pair waits before another step for the same cause. A different cause is not this wait.</summary>
    public int QuarrelStepCooldownDays { get; set; } = 120;

    /// <summary>How a war grows: the age and the losses, as a share of the people, at which it becomes a major war and then a total one.</summary>
    public int WarMajorDays { get; set; } = 150;
    public float WarMajorLossShare { get; set; } = 0.0012f;
    public int WarTotalDays { get; set; } = 420;
    public float WarTotalLossShare { get; set; } = 0.006f;
    /// <summary>What a larger war takes off the acceptance of a compromise peace, per stage: the dead have to have been for something.</summary>
    public float WarSunkCostAcceptance { get; set; } = 7f;
    /// <summary>How much longer a stalled war goes on before the side not winning asks to stop, per stage, and for a civil war.</summary>
    public int WarStageStalemateDays { get; set; } = 180;
    public int CivilWarStalemateDays { get; set; } = 730;
    /// <summary>
    /// How much of a long war's time pressure towards a compromise a civil war feels, and how
    /// much acceptance of one the winning side loses for each point of its lead.
    /// </summary>
    public float CivilWarTimePressureShare { get; set; } = 0.5f;
    public float CivilWarWinnerResolve { get; set; } = 1.2f;
    /// <summary>The call-up of a total war: extra men a week as a share of the people, while under the ceiling.</summary>
    public float TotalWarMobilisation { get; set; } = 0.0002f;
    /// <summary>Great Power mediation and intervention settings during international crises.</summary>
    public int CrisisMediationCooldownDays { get; set; } = 90;
    public float CrisisMediationCost { get; set; } = 0.5f;
    public float CrisisMediationTensionReduction { get; set; } = 20f;
    public float CrisisInterventionDeterrenceBonus { get; set; } = 1.5f;
}

/// <summary>What stands between two countries, beyond the opinion they hold of each other.</summary>
public sealed partial class DiplomaticRelation
{
    /// <summary>How close the two are to fighting, 0 to 100. 100 while they are at war.</summary>
    public float Tension;

    /// <summary>
    /// The part of it no week of good behaviour removes: a divided peninsula, a border
    /// nobody agreed, an armistice instead of a peace. Set by history; a treaty can lower it.
    /// </summary>
    public float StructuralTension;

    /// <summary>What A holds against B, and B against A, after the wars between them. It fades over a generation.</summary>
    public float GrievanceA;
    public float GrievanceB;

    /// <summary>The stage the tension is at (<see cref="TensionStage"/>) and the day it got there.</summary>
    public int TensionStage;
    public int TensionStageDay = -9999;
    /// <summary>
    /// The day this pair last stepped, or was handed an ultimatum, for that cause. 0 means
    /// never, including a save from before the field existed. The same cause waits
    /// <see cref="Defines.QuarrelStepCooldownDays"/>; a different cause does not.
    /// </summary>
    public int QuarrelRefusalDay;
    public int QuarrelClaimDay;
    public int QuarrelRivalDay;
    public int QuarrelBorderDay;
    public int LastIncidentDay = -9999;
    public string LastIncident = "";
    public int LastIncidentBy = -1;
    /// <summary>The men both sides have lost to shooting short of a war.</summary>
    public float ClashLosses;
    /// <summary>
    /// Where the standoff stood on the first day, by history, against what its reasons alone
    /// give: Russia and Poland were a crisis in 2024 and not a border that shot every week,
    /// though hostility, the war in Ukraine and a rivalry add up to more than that. What the
    /// reasons cannot see - deterrence, an alliance's weight, back channels - is this, and it
    /// fades (TensionBaselineHalfLifeYears). The history's level waits in TensionAnchor until
    /// the first review, when every reason is in place to measure it against.
    /// </summary>
    public float TensionBaseline;
    public float TensionAnchor = -1f;
    /// <summary>Talks under way: who asked for them and until when, and when they last were.</summary>
    public int TalksBy = -1;
    public int TalksUntilDay = -1;
    public int LastTalksDay = -9999;
    /// <summary>Great Power mediation and intervention during an international crisis.</summary>
    public int MediatorId = -1;
    public int LastMediationDay = -9999;
    public int InterveningPowerId = -1;
    public int InterventionTargetId = -1;
    public int InterventionUntilDay = -1;

    public float GrievanceOf(int holderId) => holderId == A ? GrievanceA : holderId == B ? GrievanceB : 0f;
}

public sealed partial class StrategicWar
{
    /// <summary>0 a limited war, 1 a major war, 2 a total war; and the day it reached that.</summary>
    public int Intensity;
    public int IntensityDay = -1;
    /// <summary>
    /// A border war: fought by the two of them alone, for a few weeks, and ended by a ceasefire
    /// on the old lines on <see cref="LimitedUntilDay"/> unless somebody refuses it (Escalation).
    /// </summary>
    public bool Limited;
    public int LimitedUntilDay = -1;
}

public enum TensionStage
{
    Calm,
    Friction,
    Crisis,
    Clashes,
    War
}

/// <summary>Something that happened between two countries short of a war (common/escalation).</summary>
public sealed class IncidentDefinition
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    /// <summary>The lowest and highest stage it can happen at.</summary>
    public int Stage = 1;
    public int MaxStage = 3;
    public float Weight = 1f;
    /// <summary>What it does to the tension (negative for the things that calm it), and to the victim's grievance.</summary>
    public float Tension;
    public float Grievance;
    /// <summary>Men lost on each side.</summary>
    public float Losses;
    /// <summary>War support the victim gains: a rally, or for a calming incident none.</summary>
    public float WarSupport;
    /// <summary>The opinion reason it leaves (common/opinion_modifiers), or nothing.</summary>
    public string Opinion = "";
    /// <summary>Needs a land border; needs both to have a navy.</summary>
    public bool Border;
    public bool Naval;
}

public static class IncidentCatalog
{
    private static readonly Dictionary<string, IncidentDefinition> ById = new(StringComparer.OrdinalIgnoreCase);
    private static IncidentDefinition[] _ordered = Array.Empty<IncidentDefinition>();

    static IncidentCatalog() => ResetCatalog();

    public static IReadOnlyList<IncidentDefinition> All => _ordered;

    public static IncidentDefinition Find(string id) =>
        id is not null && ById.TryGetValue(id, out var found) ? found : null;

    public static void ResetCatalog(List<string> errors = null)
    {
        ById.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "escalation"))
            using (document)
                Merge(document.RootElement, errors);
        Order();
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
        Order();
    }

    private static void Order() =>
        _ordered = ById.Values.OrderBy(definition => definition.Id, StringComparer.Ordinal).ToArray();

    private static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var incidents = root.TryGetProperty("incidents", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        foreach (var entry in incidents.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (entry.Value.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.True)
            {
                ById.Remove(entry.Name);
                continue;
            }
            var definition = ById.TryGetValue(entry.Name, out var existing) ? existing : new IncidentDefinition { Id = entry.Name };
            foreach (var field in entry.Value.EnumerateObject())
            {
                switch (field.Name)
                {
                    case "name": definition.Name = field.Value.GetString() ?? ""; break;
                    case "description": definition.Description = field.Value.GetString() ?? ""; break;
                    case "opinion": definition.Opinion = field.Value.GetString() ?? ""; break;
                    case "stage" when field.Value.TryGetInt32(out var stage): definition.Stage = Math.Clamp(stage, 0, 3); break;
                    case "max_stage" when field.Value.TryGetInt32(out var max): definition.MaxStage = Math.Clamp(max, 0, 3); break;
                    case "weight" when field.Value.TryGetSingle(out var weight): definition.Weight = MathF.Max(0f, weight); break;
                    case "tension" when field.Value.TryGetSingle(out var tension): definition.Tension = tension; break;
                    case "grievance" when field.Value.TryGetSingle(out var grievance): definition.Grievance = grievance; break;
                    case "losses" when field.Value.TryGetSingle(out var losses): definition.Losses = MathF.Max(0f, losses); break;
                    case "war_support" when field.Value.TryGetSingle(out var support): definition.WarSupport = support; break;
                    case "border": definition.Border = field.Value.ValueKind == JsonValueKind.True; break;
                    case "naval": definition.Naval = field.Value.ValueKind == JsonValueKind.True; break;
                    default: errors?.Add($"common/escalation: '{entry.Name}.{field.Name}' is not something this game reads."); break;
                }
            }
            if (definition.Name.Length == 0) definition.Name = entry.Name;
            ById[entry.Name] = definition;
        }
    }
}

/// <summary>What puts two countries where they are, for the screen that explains it.</summary>
public readonly record struct TensionDrivers(
    float Structural, float Hostility, float Grievance, float LostLand, float Rivalry,
    float Proxy, float Ideology, float Factor, float Target, float Baseline = 0f);

/// <summary>
/// How small things become wars.
///
/// Wars used to come out of one weekly sum - strength, appetite, price - and out of nothing
/// else. Two countries could despise each other for thirty years without a shot being fired
/// until the day the sum tipped, and then the army crossed the border with nothing before it.
/// And a war, once over, was over: the side that lost its provinces went back to the opinion
/// it would have had of a stranger once the treaty's reasons expired, so nothing a war did
/// ever came back as the next one.
///
/// Tension is the thing in between. Every pair of countries with a reason to - a shared border,
/// a bad opinion, a grievance, land one holds of the other's, a proxy war on opposite sides,
/// a structural standoff the bookmark opens with - has a tension that moves towards where those
/// reasons put it. As it rises it passes through friction, crisis and armed clashes, and the
/// higher it is the likelier an incident: shooting at the border, a ship rammed, diplomats sent
/// home, a saboteur, an assassination. Each incident moves it again and is remembered. In the
/// clashes stage the two exchange fire every week, lose men and rally their people - and the
/// AI weighs the crisis in the sum that decides whether to go to war.
///
/// A war that ends leaves a grievance in the side that lost: provinces taken and the dead. It
/// halves over thirty years, and while it lasts it holds the tension up, which is how a war's
/// peace becomes the cause of the next one. Talks take tension off, for money and for a while.
///
/// And a war itself grows (<see cref="Intensity"/>): limited, then major, then total, by its
/// age and what it has cost. A larger war calls up more men, rallies more, and makes the
/// compromise that would end it harder to accept - the dead have to have been for something.
/// </summary>
public static class Escalation
{
    public static TensionStage StageOf(DiplomaticRelation relation) =>
        relation is null ? TensionStage.Calm : (TensionStage)Math.Clamp(relation.TensionStage, 0, 4);

    /// <summary>Once a review: every pair with a reason has its tension moved, staged and possibly an incident.</summary>
    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The review in stages, a batch of relations at a time, for the smoothed step.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || c.ElapsedDays <= 0) yield break;
        if (!MechanicFeatures.SystemEnabled(c, nameof(Escalation))) yield break;
        var defines = c.Defines;
        var every = Math.Max(1, defines.EscalationReviewDays);
        if (c.ElapsedDays % every != 2 % every) yield break;

        List<DiplomaticRelation> candidates;
        using (SimProfiler.Measure("escalation")) candidates = Candidates(c);
        for (var start = 0; start < candidates.Count; start += 64)
        {
            using (SimProfiler.Measure("escalation"))
                for (var index = start; index < Math.Min(candidates.Count, start + 64); index++)
                    Review(c, candidates[index], every);
            yield return 0;
        }
        using (SimProfiler.Measure("escalation"))
        {
            GrowWars(c, every);
            EndBorderWars(c, every);
            ReviewCrises(c, every);
        }
    }

    /// <summary>
    /// Every pair worth weighing, in a fixed order: the relations that already carry something,
    /// and every pair of neighbours, which are the pairs most wars are between.
    /// </summary>
    private static List<DiplomaticRelation> Candidates(Campaign c)
    {
        var keys = new SortedSet<long>();
        foreach (var (key, relation) in c.Relations)
            if (relation.Tension > 0.01f || relation.StructuralTension > 0f || relation.GrievanceA > 0.01f ||
                relation.GrievanceB > 0.01f || relation.Opinion < -20f ||
                relation.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar ||
                OpinionSystem.Has(relation, "refused_our_ultimatum") ||
                OpinionSystem.Has(relation, "took_our_land"))
                keys.Add(key);
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not null || nation.Population <= 0) continue;
            foreach (var mapIndex in CampaignDiplomacy.Neighbours(c, nation.MapIndex))
            {
                var other = NationAt(c, mapIndex);
                if (other is null || other.Id <= nation.Id || other.Movement is not null || other.Population <= 0) continue;
                var low = Math.Min(nation.Id, other.Id);
                var high = Math.Max(nation.Id, other.Id);
                keys.Add(((long)low << 32) | (uint)high);
            }
        }
        var result = new List<DiplomaticRelation>(keys.Count);
        foreach (var key in keys)
        {
            var low = (int)(key >> 32);
            var high = (int)(uint)key;
            var relation = CampaignDiplomacy.Get(c, low, high);
            if (relation is not null) result.Add(relation);
        }
        return result;
    }

    private static Nation NationAt(Campaign c, int mapIndex)
    {
        foreach (var nation in c.Nations)
            if (nation.MapIndex == mapIndex) return nation;
        return null;
    }

    private static void Review(Campaign c, DiplomaticRelation relation, int days)
    {
        var a = c.NationById(relation.A);
        var b = c.NationById(relation.B);
        if (a is null || b is null || a.Movement is not null || b.Movement is not null ||
            a.Population <= 0 || b.Population <= 0) return;
        var defines = c.Defines;

        // A grievance is a generation's, not a year's.
        var fade = MathF.Pow(0.5f, days / (365f * MathF.Max(1f, defines.GrievanceHalfLifeYears)));
        relation.GrievanceA *= fade;
        relation.GrievanceB *= fade;
        if (relation.GrievanceA < 0.01f) relation.GrievanceA = 0f;
        if (relation.GrievanceB < 0.01f) relation.GrievanceB = 0f;

        if (CampaignDiplomacy.AtWar(c, a.Id, b.Id))
        {
            relation.TensionAnchor = -1f;
            relation.Tension = 100f;
            SetStage(c, relation, a, b, TensionStage.War);
            return;
        }

        if (relation.TensionAnchor >= 0f)
        {
            relation.TensionBaseline = 0f;
            relation.TensionBaseline = relation.TensionAnchor - Drivers(c, relation).Target;
            relation.TensionAnchor = -1f;
        }
        else if (relation.TensionBaseline != 0f)
        {
            relation.TensionBaseline *= MathF.Pow(0.5f, days / (365f * MathF.Max(0.5f, defines.TensionBaselineHalfLifeYears)));
            if (MathF.Abs(relation.TensionBaseline) < 0.05f) relation.TensionBaseline = 0f;
        }

        var drivers = Drivers(c, relation);
        var rate = drivers.Target > relation.Tension ? defines.TensionRiseRate : defines.TensionFallRate;
        var share = 1f - MathF.Pow(1f - M.Clamp01(rate), days / 7f);
        relation.Tension += (drivers.Target - relation.Tension) * share;
        if (relation.TalksUntilDay > c.ElapsedDays)
            relation.Tension -= defines.TalksTensionPerWeek * days / 7f;
        relation.Tension = M.Clamp(relation.Tension, 0f, 100f);
        var border = CampaignDiplomacy.SharesLandBorder(c, a.MapIndex, b.MapIndex);
        var facts = QuarrelFacts.Read(c, relation, border, drivers);
        // A step already taken stays on its rung. The weekly drift would walk a rivalry of ten
        // points back under the friction line before the same cause is allowed to climb again.
        HoldQuarrel(c, relation, a, b, facts);
        // Shooting needs somebody willing to shoot first. Russia does not fire across a border
        // with the whole of NATO behind the Finns, Finland's parliament does not fire across one
        // at all, and Ukraine, fighting for its life against Russia, does not open a second front
        // on Belarus. A standoff in which neither side would fire first stays a crisis however
        // bad it gets.
        // Clashes already under way are held to the same test: an exchange of fire nobody is
        // willing to open is a crisis by next week - the scenario opened the United States and
        // Russia at "border clashes" and nothing ever asked again who was shooting.
        var deterred = relation.Tension >= defines.TensionClashesAt && StageOf(relation) <= TensionStage.Clashes &&
                       Deterred(c, a, b);
        if (deterred) relation.Tension = defines.TensionClashesAt - 0.5f;

        var stage = StageFor(defines, relation);
        // Hysteresis keeps a stage until tension is well below its line; nobody shooting is not
        // a matter of degree.
        if (deterred && stage >= TensionStage.Clashes) stage = TensionStage.Crisis;
        SetStage(c, relation, a, b, stage);

        if (StageOf(relation) >= TensionStage.Clashes) Clash(c, relation, a, b, border, days);
        MaybeIncident(c, relation, a, b, border, days);
        // An incident does not get it past the line either: a crisis that reads 82 is a crisis
        // that says it is about to shoot.
        if (deterred) relation.Tension = MathF.Min(relation.Tension, defines.TensionClashesAt - 0.5f);
        MaybeBorderWar(c, relation, a, b, border, days);
        // One rung, or the existing ultimatum. Not a new crisis, and not a war declared here.
        PressQuarrel(c, relation, a, b, border, facts);
    }

    /// <summary>
    /// Why a pair that is not at war is allowed one rung. An ended war is not one of these.
    /// The live list already answered "are they fighting" before <see cref="Review"/> got here.
    /// </summary>
    private readonly struct QuarrelFacts
    {
        public readonly bool Refusal, Claim, Rival, Border;
        public bool Any => Refusal || Claim || Rival || Border;
        public QuarrelFacts(bool refusal, bool claim, bool rival, bool border)
        {
            Refusal = refusal; Claim = claim; Rival = rival; Border = border;
        }

        public static QuarrelFacts Read(Campaign c, DiplomaticRelation relation, bool border, TensionDrivers drivers)
        {
            var rival = relation.Stance == DiplomaticStance.Rival;
            return new QuarrelFacts(
                OpinionSystem.Has(relation, "refused_our_ultimatum"),
                OpinionSystem.Has(relation, "took_our_land") || drivers.LostLand > 0.01f,
                rival,
                !rival && border && relation.Opinion <= c.Defines.AiRivalryThreshold);
        }
    }

    private static bool QuarrelOpen(Campaign c, Nation a, Nation b) =>
        c.Defines.QuarrelStep && !(a.IsHuman && b.IsHuman) &&
        !(c.ObserverMode && (a.IsHuman || b.IsHuman)) &&
        !a.AnswersTo(b.Id) && !b.AnswersTo(a.Id);

    private static bool CauseCool(int stamped, int today, int wait) =>
        stamped <= 0 || today - stamped >= wait;

    private static bool CauseHeld(QuarrelFacts facts, DiplomaticRelation relation) =>
        (relation.QuarrelRefusalDay > 0 && facts.Refusal) ||
        (relation.QuarrelClaimDay > 0 && facts.Claim) ||
        (relation.QuarrelRivalDay > 0 && facts.Rival) ||
        (relation.QuarrelBorderDay > 0 && facts.Border);

    private static float StageLine(Defines defines, TensionStage stage) => stage switch
    {
        TensionStage.Friction => defines.TensionFrictionAt,
        TensionStage.Crisis => defines.TensionCrisisAt,
        TensionStage.Clashes => defines.TensionClashesAt,
        _ => 0f
    };

    /// <summary>Keep the rung a cause already earned. The next review asks for a new step separately.</summary>
    private static void HoldQuarrel(Campaign c, DiplomaticRelation relation, Nation a, Nation b, QuarrelFacts facts)
    {
        if (!facts.Any || !QuarrelOpen(c, a, b) || !CauseHeld(facts, relation)) return;
        var stage = StageOf(relation);
        if (stage <= TensionStage.Calm || stage >= TensionStage.War) return;
        var floor = StageLine(c.Defines, stage);
        if (relation.Tension < floor) relation.Tension = floor;
    }

    /// <summary>
    /// One step on the ladder already in this file, or the ultimatum already on the player's desk.
    /// Two computers may climb. A player is not declared on and does not lose land here.
    /// </summary>
    private static void PressQuarrel(Campaign c, DiplomaticRelation relation, Nation a, Nation b, bool border,
        QuarrelFacts facts)
    {
        if (!facts.Any || !QuarrelOpen(c, a, b)) return;
        if (CampaignDiplomacy.AtWar(c, a.Id, b.Id)) return;
        var wait = Math.Max(1, c.Defines.QuarrelStepCooldownDays);
        var today = c.ElapsedDays;
        var cause = "";
        if (facts.Refusal && CauseCool(relation.QuarrelRefusalDay, today, wait)) cause = "refusal";
        else if (facts.Claim && CauseCool(relation.QuarrelClaimDay, today, wait)) cause = "claim";
        else if (facts.Rival && CauseCool(relation.QuarrelRivalDay, today, wait)) cause = "rival";
        else if (facts.Border && CauseCool(relation.QuarrelBorderDay, today, wait)) cause = "border";
        if (cause.Length == 0) return;

        var actor = a.IsHuman ? b : a;
        var target = ReferenceEquals(actor, a) ? b : a;
        if (target.IsHuman && DeliverExistingUltimatum(c, actor, target))
        {
            StampQuarrel(relation, cause, today);
            return;
        }
        if (!RaiseOneRung(c, relation, a, b, border)) return;
        StampQuarrel(relation, cause, today);
    }

    private static void StampQuarrel(DiplomaticRelation relation, string cause, int today)
    {
        switch (cause)
        {
            case "refusal": relation.QuarrelRefusalDay = today; break;
            case "claim": relation.QuarrelClaimDay = today; break;
            case "rival": relation.QuarrelRivalDay = today; break;
            case "border": relation.QuarrelBorderDay = today; break;
        }
    }

    /// <summary>
    /// The region <see cref="AiStatecraft.TryUltimatumFirst"/> would demand: lost homeland or
    /// our own people, touching our ground. Delivery puts the paper on a player's desk.
    /// It does not answer the paper and it does not read the war list.
    /// </summary>
    private static bool DeliverExistingUltimatum(Campaign c, Nation actor, Nation target)
    {
        if (actor is null || target is null || actor.IsHuman || !target.IsHuman) return false;
        if (!c.Defines.AiUltimatumsBeforeWar || c.ProvinceMap is null) return false;
        if (Ultimatum.PendingBetween(c, actor.Id, target.Id)) return true;
        var key = "ultimatum/" + target.Id;
        if (actor.DecisionAvailableDay.GetValueOrDefault(key) > c.ElapsedDays) return false;

        var lost = new List<int>();
        foreach (var (region, holder) in LostLands.Of(c, actor))
            if (holder?.Id == target.Id) lost.Add(region.MapIndex);

        ScenarioState best = null;
        var bestScore = 0f;
        foreach (var state in c.StatesOf(target))
        {
            if (state.Population <= 0) continue;
            if (!Ultimatum.CanDemand(c, target, state.MapIndex)) continue;
            if (!TerritorialCoherence.TouchesOwnedGround(c, actor, state.MapIndex)) continue;
            long ours = 0;
            foreach (var group in state.PopulationGroups)
                if (DemographyCatalog.IsPrimary(actor.Tag, group.Culture)) ours += group.Size;
            var kin = ours / (float)Math.Max(1, state.Population);
            var claimed = false;
            for (var index = 0; index < lost.Count; index++)
                if (lost[index] == state.MapIndex) { claimed = true; break; }
            if (kin < 0.5f && !claimed) continue;
            var score = state.Population * (1f + 4f * kin) * (claimed ? 2f : 1f);
            if (score <= bestScore) continue;
            bestScore = score;
            best = state;
        }
        if (best is null) return false;
        var demand = new Ultimatum.Demand { Regions = new[] { best.MapIndex } };
        if (!Ultimatum.ValidateTerms(c, actor, target, demand, out _)) return false;
        actor.DecisionAvailableDay[key] = c.ElapsedDays + Math.Max(30, c.Defines.AiUltimatumIntervalDays);
        return Ultimatum.Deliver(c, actor, target, demand).Accepted;
    }

    /// <summary>The next stage this file already has. Clashes stay behind the existing deterrent, and off a player's border.</summary>
    private static bool RaiseOneRung(Campaign c, DiplomaticRelation relation, Nation a, Nation b, bool border)
    {
        var stage = StageOf(relation);
        if (stage >= TensionStage.Clashes) return false;
        var next = stage + 1;
        if (next >= TensionStage.Clashes &&
            (a.IsHuman || b.IsHuman || !border || Deterred(c, a, b))) return false;
        var line = StageLine(c.Defines, next);
        if (line <= 0f) return false;
        if (relation.Tension < line) relation.Tension = line;
        SetStage(c, relation, a, b, next);
        return StageOf(relation) == next;
    }

    private static TensionStage StageFor(Defines defines, DiplomaticRelation relation)
    {
        var tension = relation.Tension;
        var current = StageOf(relation);
        if (current == TensionStage.War) current = TensionStage.Clashes;
        var hysteresis = MathF.Max(0f, defines.TensionHysteresis);
        float Enter(TensionStage stage) => stage switch
        {
            TensionStage.Friction => defines.TensionFrictionAt,
            TensionStage.Crisis => defines.TensionCrisisAt,
            TensionStage.Clashes => defines.TensionClashesAt,
            _ => 0f
        };
        // Up as soon as the line is crossed; down only once it is well behind.
        var stage = current;
        while (stage < TensionStage.Clashes && tension >= Enter(stage + 1)) stage++;
        while (stage > TensionStage.Calm && tension < Enter(stage) - hysteresis) stage--;
        return stage;
    }

    private static void SetStage(Campaign c, DiplomaticRelation relation, Nation a, Nation b, TensionStage stage)
    {
        var before = StageOf(relation);
        if (before == stage) return;
        relation.TensionStage = (int)stage;
        relation.TensionStageDay = c.ElapsedDays;
        if (stage == TensionStage.War || before == TensionStage.War) return;
        if (stage > before && stage >= TensionStage.Crisis)
            c.Chronicle.Record(c, stage == TensionStage.Clashes ? "tension_clashes" : "tension_crisis", a.Id, b.Id);
        else if (stage < before && before >= TensionStage.Crisis && stage <= TensionStage.Friction)
            c.Chronicle.Record(c, "tension_eased", a.Id, b.Id);
    }

    /// <summary>Where the tension between these two is heading, and why.</summary>
    public static TensionDrivers Drivers(Campaign c, DiplomaticRelation relation)
    {
        var a = c?.NationById(relation?.A ?? -1);
        var b = c?.NationById(relation?.B ?? -1);
        if (a is null || b is null) return default;
        var defines = c.Defines;
        var structural = MathF.Max(0f, relation.StructuralTension);
        var hostility = MathF.Max(0f, -relation.Opinion) * defines.TensionFromHostility;
        var grievance = MathF.Max(relation.GrievanceA, relation.GrievanceB) * defines.TensionFromGrievance;
        var lostLand = MathF.Min(40f, (LostLands.Weight(c, a, b) + LostLands.Weight(c, b, a)) * defines.TensionFromLostLand);
        var rivalry = relation.Stance == DiplomaticStance.Rival ? defines.TensionFromRivalry : 0f;
        var proxy = ProxyWars.Opposition(c, a, b) * defines.TensionFromProxy;
        var ideology = PoliticsCore.IdeologicalDistance(a, b) * defines.TensionFromIdeology * (relation.Opinion < 0f ? 1f : 0.3f);

        var border = CampaignDiplomacy.SharesLandBorder(c, a.MapIndex, b.MapIndex);
        var factor = border ? defines.TensionBorderFactor : defines.TensionDistantFactor;
        if (relation.Stance == DiplomaticStance.Allied) factor *= defines.TensionAllianceFactor;
        if (relation.NonAggressionPact) factor *= defines.TensionPactFactor;
        if (relation.TradeAgreement) factor *= defines.TensionTradeFactor;
        if (relation.GuaranteeByA || relation.GuaranteeByB) factor *= 0.4f;
        if (a.AnswersTo(b.Id) || b.AnswersTo(a.Id)) factor *= 0.2f;
        // Structural tension is not softened by distance: an armistice line is an armistice line.
        var globalCycle = GeopoliticalCycle.GlobalTensionOffset(c);
        var reasons = structural * MathF.Max(factor, MathF.Min(1f, factor * 2f)) +
                      (hostility + grievance + lostLand + rivalry + proxy + ideology) * factor;
        var target = M.Clamp(reasons + relation.TensionBaseline + globalCycle, 0f, 100f);
        return new TensionDrivers(structural, hostility, grievance, lostLand, rivalry, proxy, ideology, factor, target,
            relation.TensionBaseline);
    }

    /// <summary>Shooting short of a war: a few hundred men a week, and a people rallying on both sides.</summary>
    private static void Clash(Campaign c, DiplomaticRelation relation, Nation a, Nation b, bool border, int days)
    {
        var defines = c.Defines;
        var weeks = days / 7f;
        var heat = 1f + MathF.Max(0f, relation.Tension - defines.TensionClashesAt) / 25f;
        var men = defines.ClashLossesPerWeek * weeks * heat * (border ? 1f : 0.35f);
        foreach (var side in new[] { a, b })
        {
            var lost = MathF.Min(MathF.Max(0f, side.ManpowerReserve), men);
            side.ManpowerReserve -= lost;
            relation.ClashLosses += lost;
            side.WarSupport = M.Clamp01(side.WarSupport + defines.ClashWarSupportPerWeek * weeks);
            if (border) ModifierSystem.Grant(c, side, "border_clashes", days * 2);
        }
    }

    private static void MaybeIncident(Campaign c, DiplomaticRelation relation, Nation a, Nation b, bool border, int days)
    {
        var defines = c.Defines;
        var stage = (int)StageOf(relation);
        if (stage <= 0) return;
        var heat = relation.Tension / 100f;
        var chance = defines.IncidentChance * heat * heat * (border ? 1f : 0.4f) * days / 7f;
        if (Roll(c.Seed, relation.A, relation.B, c.ElapsedDays, 11) >= chance) return;

        var naval = HasNavy(c, a) && HasNavy(c, b);
        var total = 0f;
        foreach (var incident in IncidentCatalog.All)
            if (Fits(incident, stage, border, naval)) total += incident.Weight;
        if (total <= 0f) return;
        var pick = Roll(c.Seed, relation.A, relation.B, c.ElapsedDays, 23) * total;
        IncidentDefinition chosen = null;
        foreach (var incident in IncidentCatalog.All)
        {
            if (!Fits(incident, stage, border, naval)) continue;
            pick -= incident.Weight;
            if (pick <= 0f) { chosen = incident; break; }
        }
        chosen ??= IncidentCatalog.All.LastOrDefault(incident => Fits(incident, stage, border, naval));
        if (chosen is null) return;

        // Who starts it: the side with more against the other, more often.
        var weightA = 5f + relation.GrievanceA + MathF.Max(0f, PoliticsCore.IdeologyOf(a).Authority) * 10f;
        var weightB = 5f + relation.GrievanceB + MathF.Max(0f, PoliticsCore.IdeologyOf(b).Authority) * 10f;
        var byA = Roll(c.Seed, relation.A, relation.B, c.ElapsedDays, 37) * (weightA + weightB) < weightA;
        Apply(c, relation, byA ? a : b, byA ? b : a, chosen);
    }

    private static bool Fits(IncidentDefinition incident, int stage, bool border, bool naval) =>
        incident.Weight > 0f && stage >= incident.Stage && stage <= incident.MaxStage &&
        (!incident.Border || border) && (!incident.Naval || naval);

    private static bool HasNavy(Campaign c, Nation nation)
    {
        foreach (var fleet in c.Fleets)
            if (fleet.NationId == nation.Id && fleet.Hulls > 0) return true;
        return false;
    }

    /// <summary>
    /// One incident, applied. Public: the chronicle, and so the news and the two peoples'
    /// memory of each other, hears of it. Also how history and scripts start one.
    /// </summary>
    public static void Apply(Campaign c, DiplomaticRelation relation, Nation by, Nation against, IncidentDefinition incident)
    {
        if (c is null || relation is null || by is null || against is null || incident is null) return;
        relation.Tension = M.Clamp(relation.Tension + incident.Tension, 0f, 100f);
        if (incident.Grievance != 0f)
        {
            if (against.Id == relation.A) relation.GrievanceA = M.Clamp(relation.GrievanceA + incident.Grievance, 0f, c.Defines.GrievanceCap);
            else relation.GrievanceB = M.Clamp(relation.GrievanceB + incident.Grievance, 0f, c.Defines.GrievanceCap);
        }
        if (incident.Losses > 0f)
            foreach (var side in new[] { by, against })
            {
                var lost = MathF.Min(MathF.Max(0f, side.ManpowerReserve), incident.Losses);
                side.ManpowerReserve -= lost;
                relation.ClashLosses += lost;
            }
        if (incident.WarSupport != 0f) against.WarSupport = M.Clamp01(against.WarSupport + incident.WarSupport);
        if (incident.Opinion.Length > 0) OpinionSystem.Remember(c, relation, incident.Opinion);
        relation.LastIncident = incident.Id;
        relation.LastIncidentDay = c.ElapsedDays;
        relation.LastIncidentBy = by.Id;
        c.Chronicle.Record(c, incident.Tension < 0f ? "incident_calm" : "incident", by.Id, against.Id, incident.Name);
        DiagnosticLogs.Write("simulation",
            $"INCIDENT {incident.Id} by={by.Tag} against={against.Tag} tension={relation.Tension:0.0} stage={StageOf(relation)}");
    }

    /// <summary>A die drawn from the pair and the day rather than the shared stream (see AiStatecraft.Roll).</summary>
    internal static float Roll(int seed, int a, int b, int day, int salt)
    {
        unchecked
        {
            var hash = (uint)seed * 2654435761u;
            hash ^= (uint)a * 2246822519u + 0x9E3779B9u;
            hash = (hash << 13 | hash >> 19) * 5u + 0xE6546B64u;
            hash ^= (uint)b * 3266489917u;
            hash = (hash << 11 | hash >> 21) * 5u + 0x52DCE729u;
            hash ^= (uint)day * 668265263u;
            hash ^= (uint)salt * 374761393u;
            hash ^= hash >> 15; hash *= 2246822519u;
            hash ^= hash >> 13; hash *= 3266489917u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFFF) / (float)0x1000000;
        }
    }

    // --------------------------------------------------------------------- the AI and peace

    /// <summary>
    /// What a crisis adds to this country's case for a war on that one: nothing below a crisis,
    /// up to one at full tension, and half as much again for what it holds against them.
    /// </summary>
    public static float WarAppetite(Campaign c, Nation nation, Nation other)
    {
        if (c is null || nation is null || other is null) return 0f;
        if (!MechanicFeatures.SystemEnabled(c, nameof(Escalation))) return 0f;
        var relation = CampaignDiplomacy.Peek(c, nation.Id, other.Id);
        if (relation is null) return 0f;
        var defines = c.Defines;
        // Shooting at the border is a reason; a war of words is not. Below armed clashes a
        // crisis adds nothing - the Korean armistice line was "in crisis" for seventy years
        // without either side marching - and grievance counts for half only while it lasts.
        var clashes = M.Clamp01((relation.Tension - defines.TensionClashesAt) / MathF.Max(1f, 100f - defines.TensionClashesAt));
        return clashes + 0.5f * M.Clamp01(relation.GrievanceOf(nation.Id) / 100f);
    }

    /// <summary>What the size of a war takes off the acceptance of anything short of a surrender.</summary>
    public static float SunkCost(Campaign c, StrategicWar war) =>
        c is null || war is null || !MechanicFeatures.SystemEnabled(c, nameof(Escalation))
            ? 0f : Math.Clamp(war.Intensity, 0, 2) * c.Defines.WarSunkCostAcceptance;

    /// <summary>How much longer than an ordinary war this one stands still before it is settled as a deadlock.</summary>
    public static int StalemateDelay(Campaign c, StrategicWar war)
    {
        if (c is null || war is null || !MechanicFeatures.SystemEnabled(c, nameof(Escalation))) return 0;
        var defines = c.Defines;
        return Math.Clamp(war.Intensity, 0, 2) * Math.Max(0, defines.WarStageStalemateDays) +
               (war.CivilConflict ? Math.Max(0, defines.CivilWarStalemateDays) : 0);
    }

    /// <summary>
    /// What a war leaves when it ends: a grievance in each country on the losing end of it, for
    /// the provinces it lost and the dead it buried, and less of one in the side that won.
    /// Called from the peace, once the border has been redrawn.
    /// </summary>
    public static void Settle(Campaign c, StrategicWar war)
    {
        if (c?.ProvinceMap is null || war is null || !MechanicFeatures.SystemEnabled(c, nameof(Escalation))) return;
        var defines = c.Defines;
        var owners = c.ProvinceMap.ProvinceToNation;
        foreach (var attackerId in war.Side(true))
        foreach (var defenderId in war.Side(false))
        {
            var attacker = c.NationById(attackerId);
            var defender = c.NationById(defenderId);
            if (attacker is null || defender is null || attacker.Id == defender.Id) continue;
            var relation = CampaignDiplomacy.Get(c, attacker.Id, defender.Id);
            if (relation is null) continue;
            var lostByAttacker = 0;
            var lostByDefender = 0;
            foreach (var (province, was) in war.OriginalController)
            {
                if (province <= 0 || province >= owners.Length) continue;
                var now = owners[province];
                if (was == attacker.MapIndex && now == defender.MapIndex) lostByAttacker++;
                else if (was == defender.MapIndex && now == attacker.MapIndex) lostByDefender++;
            }
            var attackerShare = war.AttackerLosses / MathF.Max(1f, attacker.Population);
            var defenderShare = war.DefenderLosses / MathF.Max(1f, defender.Population);
            var attackerGrievance = lostByAttacker * defines.GrievancePerProvinceLost +
                                    attackerShare * defines.GrievancePerLossShare * (war.WinnerId == attacker.Id ? 0.3f : 1f);
            var defenderGrievance = lostByDefender * defines.GrievancePerProvinceLost +
                                    defenderShare * defines.GrievancePerLossShare * (war.WinnerId == defender.Id ? 0.3f : 1f);
            Add(relation, attacker.Id, attackerGrievance, defines.GrievanceCap);
            Add(relation, defender.Id, defenderGrievance, defines.GrievanceCap);
            relation.Tension = MathF.Max(relation.Tension, defines.TensionCrisisAt + 10f);
        }
    }

    private static void Add(DiplomaticRelation relation, int holderId, float amount, float cap)
    {
        if (amount <= 0f) return;
        if (holderId == relation.A) relation.GrievanceA = M.Clamp(relation.GrievanceA + amount, 0f, cap);
        else relation.GrievanceB = M.Clamp(relation.GrievanceB + amount, 0f, cap);
    }

    // ----------------------------------------------------------------- how a war grows

    /// <summary>
    /// Limited, major, total. A war grows with its age and with what it has cost the side that
    /// has lost most, as a share of its people; it does not shrink back, because the dead do not
    /// come back either. Each stage is announced and worn as a modifier by everybody in it.
    /// </summary>
    private static void GrowWars(Campaign c, int days)
    {
        var defines = c.Defines;
        var live = Warfare.LiveWars(c);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Limited) continue;
            var age = c.ElapsedDays - war.StartedDay;
            var attacker = c.NationById(war.AttackerId);
            var defender = c.NationById(war.DefenderId);
            if (attacker is null || defender is null) continue;
            var share = MathF.Max(war.AttackerLosses / MathF.Max(1f, attacker.Population),
                                  war.DefenderLosses / MathF.Max(1f, defender.Population));
            var due = age >= defines.WarTotalDays && share >= defines.WarTotalLossShare ? 2
                : age >= defines.WarMajorDays && share >= defines.WarMajorLossShare ? 1 : 0;
            if (due > war.Intensity)
            {
                war.Intensity = due;
                war.IntensityDay = c.ElapsedDays;
                c.Chronicle.Record(c, due == 2 ? "war_total" : "war_major", war.AttackerId, war.DefenderId, war.Name);
                // A total war mobilises the countries in it; it does not call in new ones. It used
                // to ask the allies and the coalition again, and three countries joined Ukraine
                // in the war's second year in every run - the escalation 2024 to 2026 did not have.
            }
            if (war.Intensity <= 0) continue;
            var modifier = war.Intensity >= 2 ? "total_war" : "major_war";
            foreach (var id in war.Side(true).Concat(war.Side(false)))
            {
                var nation = c.NationById(id);
                if (nation is null || nation.Movement is not null) continue;
                ModifierSystem.Grant(c, nation, modifier, days * 2);
                if (war.Intensity < 2) continue;
                // The call-up of a total war, up to the ceiling everyone has.
                var ceiling = nation.Population * defines.MobilisationCeilingShare * 1.5f;
                if (nation.ManpowerReserve < ceiling)
                    nation.ManpowerReserve = MathF.Min(ceiling,
                        nation.ManpowerReserve + nation.Population * defines.TotalWarMobilisation * days / 7f);
            }
        }
    }

    // -------------------------------------------------------------------- border wars

    /// <summary>
    /// What a border that has been shooting for months turns into when one side decides to
    /// settle a point with an army: a few weeks of real war between the two of them, and then a
    /// ceasefire on the old lines. India and Pakistan in May 2025, Thailand and Cambodia that
    /// July, Armenia and Azerbaijan in September 2022 - the wars of the 2020s that did happen,
    /// as against the invasions of quiet neighbours the computer used to start for want of a
    /// better reason. Nobody's allies are called; the first to refuse the ceasefire when it falls
    /// due turns it into an ordinary war.
    /// </summary>
    private static void MaybeBorderWar(Campaign c, DiplomaticRelation relation, Nation a, Nation b, bool border, int days)
    {
        var defines = c.Defines;
        if (!border || defines.BorderWarChancePerWeek <= 0f || StageOf(relation) != TensionStage.Clashes) return;
        if (c.ElapsedDays - relation.TensionStageDay < defines.BorderWarAfterDays) return;
        if (relation.TruceUntil > c.ElapsedDays || relation.Stance == DiplomaticStance.Allied ||
            relation.NonAggressionPact || relation.TalksUntilDay > c.ElapsedDays) return;
        if (a.AnswersTo(b.Id) || b.AnswersTo(a.Id) || a.Movement is not null || b.Movement is not null) return;
        if (Roll(c.Seed, relation.A, relation.B, c.ElapsedDays, 0x60D) >= defines.BorderWarChancePerWeek * days / 7f) return;
        // Finished border wars are the cooldown. They stay on the full record.
        foreach (var past in c.Wars)
            if (past.Limited && past.Ended && c.ElapsedDays - past.EndedDay < defines.BorderWarCooldownDays &&
                past.Involves(a.Id) && past.Involves(b.Id)) return;

        // Who fires first: the side with the larger grievance, and between equals the stronger.
        var aFirst = relation.GrievanceOf(a.Id) > relation.GrievanceOf(b.Id) + 5f ||
                     MathF.Abs(relation.GrievanceOf(a.Id) - relation.GrievanceOf(b.Id)) <= 5f &&
                     AiStatecraft.CachedStrength(c, a) >= AiStatecraft.CachedStrength(c, b);
        var attacker = aFirst ? a : b;
        var defender = aFirst ? b : a;
        // Nobody fires first at a country it could not stand up to, counting the friends who stand
        // behind it: Georgia does not open a border war on Russia for Abkhazia, and Pyongyang does
        // not shell its way across a line with American soldiers on the other side of it. If the
        // aggrieved side is the weaker, the stronger may still decide to teach it a lesson.
        if (!CanFace(c, attacker, defender))
        {
            (attacker, defender) = (defender, attacker);
            if (!CanFace(c, attacker, defender)) return;
        }
        // A player starts its own wars; and nobody opens a second front while a big war of its own is on.
        if (attacker.IsHuman) return;
        var fighting = Warfare.LiveWars(c);
        for (var index = 0; index < fighting.Count; index++)
            if (fighting[index].Intensity >= 1 && fighting[index].Involves(attacker.Id)) return;

        var claimed = BorderRegion(c, attacker, defender);
        var war = CampaignDiplomacy.DeclareWar(c, attacker.Id, defender.Id, claimed, "border_war",
            onOwnInitiative: false, limited: true);
        if (war is null) return;
        war.LimitedUntilDay = c.ElapsedDays + Math.Max(7, defines.BorderWarDays);
        war.Name = $"{attacker.DisplayName}–{defender.DisplayName} Border War";
        c.Chronicle.Record(c, "border_war", attacker.Id, defender.Id);
        DiagnosticLogs.Write("simulation", $"BORDER_WAR attacker={attacker.Tag} defender={defender.Tag} tension={relation.Tension:0}");
    }

    /// <summary>Whether neither of the two would open fire on the other (<see cref="WouldFireFirst"/>).</summary>
    public static bool Deterred(Campaign c, Nation a, Nation b) => Deterred(c, a, b, true);

    /// <summary>A screen must not write saved intelligence just to explain why nobody fires.</summary>
    public static bool Deterred(Campaign c, Nation a, Nation b, bool refreshAssessment) =>
        a is not null && b is not null && !WouldFireFirst(c, a, b, refreshAssessment) && !WouldFireFirst(c, b, a, refreshAssessment);

    /// <summary>
    /// A government that would start shooting at a border: one that could stand up to the other
    /// side and the friends behind it, that answers to nobody who would stop it - a parliament,
    /// a press and an election restrain as much as the democratic peace does (AiStatecraft.Restraint) -
    /// and that is not already fighting a major war somewhere else.
    /// </summary>
    private static bool WouldFireFirst(Campaign c, Nation shooter, Nation target, bool refreshAssessment = true)
    {
        if (!CanFace(c, shooter, target, refreshAssessment)) return false;
        if (AiStatecraft.Restraint(c, shooter) >= c.Defines.AiDemocraticPeaceRestraint) return false;
        // Two deterrent holders shoot at each other only across a border they share - Galwan,
        // the Line of Control, the Ussuri. Without one, opening fire is a decision to fight a
        // nuclear power far from home, and no government has taken it: the United States and
        // Russia used to reach "border clashes" in the first week of 2024.
        if (Deterrence.TierOf(c, shooter) >= 1 && Deterrence.TierOf(c, target) >= 1 &&
            !CampaignDiplomacy.SharesLandBorder(c, shooter.MapIndex, target.MapIndex)) return false;
        var live = Warfare.LiveWars(c);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Intensity >= 1 && war.Involves(shooter.Id) && !war.Involves(target.Id)) return false;
        }
        return true;
    }

    private static bool CanFace(Campaign c, Nation attacker, Nation defender, bool refreshAssessment = true)
    {
        var relation = CampaignDiplomacy.Peek(c, attacker.Id, defender.Id);
        var extraDeterrence = 0f;
        if (relation is not null && relation.InterveningPowerId >= 0 && relation.InterventionUntilDay > c.ElapsedDays &&
            relation.InterventionTargetId == attacker.Id)
        {
            var power = c.NationById(relation.InterveningPowerId);
            if (power is not null && power.Population > 0 && AllianceSystem.GreatPowers(c).Contains(power.Id))
                extraDeterrence = AiStatecraft.CachedStrength(c, power) * c.Defines.CrisisInterventionDeterrenceBonus;
        }
        return AiStatecraft.CachedStrength(c, attacker) >=
            c.Defines.BorderWarStrengthRatio * (AiStatecraft.Deterrence(c, attacker, defender, refreshAssessment) + extraDeterrence);
    }

    /// <summary>The defender's region on the shared border with the most people: what a border war is about.</summary>
    private static int BorderRegion(Campaign c, Nation attacker, Nation defender)
    {
        var topology = c.ProvinceMap;
        var controllers = c.ProvinceController;
        if (topology is null || controllers is null) return -1;
        var best = -1;
        var bestPeople = -1L;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (controllers[province] != (ushort)defender.MapIndex) continue;
            var touches = false;
            foreach (var neighbour in topology.Neighbors[province])
                if (controllers[neighbour] == (ushort)attacker.MapIndex) { touches = true; break; }
            if (!touches) continue;
            var state = c.States.Find(s => s.MapIndex == topology.ProvinceToState[province]);
            if (state is null || state.Population <= bestPeople) continue;
            best = state.MapIndex;
            bestPeople = state.Population;
        }
        return best;
    }

    /// <summary>
    /// The ceasefire a border war was always going to end in, when it falls due: written at
    /// once between two governments nobody is playing, offered to a player who is in it.
    /// </summary>
    private static void EndBorderWars(Campaign c, int days)
    {
        // Ending one war rebuilds the live list. This week keeps the wars it started with.
        var live = Warfare.LiveWars(c);
        var open = new List<StrategicWar>(live.Count);
        for (var index = 0; index < live.Count; index++)
            if (live[index].Limited) open.Add(live[index]);
        for (var index = 0; index < open.Count; index++)
        {
            var war = open[index];
            if (!war.Limited || war.LimitedUntilDay < 0 || c.ElapsedDays < war.LimitedUntilDay) continue;
            var attacker = c.NationById(war.AttackerId);
            var defender = c.NationById(war.DefenderId);
            if (attacker is null || defender is null) continue;
            if (attacker.IsHuman || defender.IsHuman)
            {
                // Once: a player who turns it down has an ordinary war on their hands.
                var computer = attacker.IsHuman ? defender : attacker;
                var player = attacker.IsHuman ? attacker : defender;
                if (!computer.IsHuman && war.PendingOfferFromId < 0)
                    PeaceOffers.Put(c, war, computer, player, PeaceTerms.Ceasefire);
                war.Limited = false;
                continue;
            }
            PeaceNegotiation.Resolve(c, war, war.AttackerId, PeaceTerms.Ceasefire);
            // Refused by the rules (a lock, a pending offer): it becomes the war it now is.
            if (!war.Ended) war.Limited = false;
        }
    }

    // ------------------------------------------------------------------------- talks

    /// <summary>Whether this country can open talks with that one now, and why not.</summary>
    public static string TalksBlock(Campaign c, Nation by, Nation with)
    {
        if (c is null || by is null || with is null || by.Id == with.Id) return Loc.Get("Choose another country.");
        if (!MechanicFeatures.SystemEnabled(c, nameof(Escalation))) return Loc.Get("Escalation is disabled.");
        if (CampaignDiplomacy.AtWar(c, by.Id, with.Id)) return Loc.Get("At war: talks are a peace negotiation now.");
        var relation = CampaignDiplomacy.Peek(c, by.Id, with.Id);
        if (relation is null || relation.Tension < c.Defines.TensionFrictionAt) return Loc.Get("There is nothing between you to talk down.");
        if (relation.TalksUntilDay > c.ElapsedDays) return Loc.Get("Talks are already under way.");
        if (c.ElapsedDays - relation.LastTalksDay < c.Defines.TalksCooldownDays)
            return Loc.Format("The last round ended too recently: {0} days to wait.",
                c.Defines.TalksCooldownDays - (c.ElapsedDays - relation.LastTalksDay));
        if (by.Treasury < c.Defines.TalksCost) return Loc.Get("Not enough money in the treasury.");
        return "";
    }

    public static bool StartTalks(Campaign c, Nation by, Nation with)
    {
        if (TalksBlock(c, by, with).Length > 0) return false;
        var relation = CampaignDiplomacy.Get(c, by.Id, with.Id);
        by.Treasury -= c.Defines.TalksCost;
        relation.TalksBy = by.Id;
        relation.TalksUntilDay = c.ElapsedDays + Math.Max(7, c.Defines.TalksDays);
        relation.LastTalksDay = c.ElapsedDays;
        OpinionSystem.Remember(c, relation, "held_talks");
        c.Chronicle.Record(c, "talks_opened", by.Id, with.Id);
        return true;
    }

    /// <summary>The tensest pairs in the world, for the screen that watches them.</summary>

    /// <summary>Whether this Great Power can offer mediation in a crisis between A and B.</summary>
    public static string MediationBlock(Campaign c, Nation mediator, Nation a, Nation b)
    {
        if (c is null || mediator is null || a is null || b is null) return Loc.Get("Choose another country.");
        if (!MechanicFeatures.SystemEnabled(c, nameof(Escalation))) return Loc.Get("Escalation is disabled.");
        if (mediator.Id == a.Id || mediator.Id == b.Id || a.Id == b.Id) return Loc.Get("Choose another country.");
        if (!AllianceSystem.GreatPowers(c).Contains(mediator.Id))
            return Loc.Get("Only a recognized Great Power can mediate crises.");
        if (CampaignDiplomacy.AtWar(c, mediator.Id, a.Id) || CampaignDiplomacy.AtWar(c, mediator.Id, b.Id))
            return Loc.Get("At war: talks are a peace negotiation now.");
        if (CampaignDiplomacy.AtWar(c, a.Id, b.Id))
            return Loc.Get("At war: talks are a peace negotiation now.");

        var relation = CampaignDiplomacy.Peek(c, a.Id, b.Id);
        if (relation is null || relation.Tension < c.Defines.TensionCrisisAt)
            return Loc.Get("Relations are not in crisis.");
        if (relation.TalksUntilDay > c.ElapsedDays)
            return Loc.Get("Talks are already under way.");
        if (c.ElapsedDays - relation.LastMediationDay < c.Defines.CrisisMediationCooldownDays)
            return Loc.Format("The last mediation ended too recently: {0} days to wait.",
                c.Defines.CrisisMediationCooldownDays - (c.ElapsedDays - relation.LastMediationDay));
        if (mediator.Treasury < c.Defines.CrisisMediationCost)
            return Loc.Get("Not enough money in the treasury.");
        return "";
    }

    public static bool StartMediation(Campaign c, Nation mediator, Nation a, Nation b)
    {
        if (MediationBlock(c, mediator, a, b).Length > 0) return false;
        var relation = CampaignDiplomacy.Get(c, a.Id, b.Id);
        mediator.Treasury -= c.Defines.CrisisMediationCost;
        relation.MediatorId = mediator.Id;
        relation.LastMediationDay = c.ElapsedDays;
        relation.TalksBy = mediator.Id;
        relation.TalksUntilDay = c.ElapsedDays + Math.Max(14, c.Defines.TalksDays);
        relation.Tension = MathF.Max(0f, relation.Tension - c.Defines.CrisisMediationTensionReduction);

        var relA = CampaignDiplomacy.Get(c, mediator.Id, a.Id);
        if (relA is not null) OpinionSystem.Remember(c, relA, "held_talks");
        var relB = CampaignDiplomacy.Get(c, mediator.Id, b.Id);
        if (relB is not null) OpinionSystem.Remember(c, relB, "held_talks");

        c.Chronicle.Record(c, "crisis_mediated", mediator.Id, a.Id, b.Name);
        DiagnosticLogs.Write("simulation", $"CRISIS_MEDIATED mediator={mediator.Tag} between={a.Tag} and={b.Tag} tension={relation.Tension:0.0}");
        return true;
    }

    /// <summary>Whether this Great Power can intervene in a crisis to deter an aggressor against a protege.</summary>
    public static string InterventionBlock(Campaign c, Nation power, Nation protege, Nation aggressor)
    {
        if (c is null || power is null || protege is null || aggressor is null) return Loc.Get("Choose another country.");
        if (!MechanicFeatures.SystemEnabled(c, nameof(Escalation))) return Loc.Get("Escalation is disabled.");
        if (power.Id == protege.Id || power.Id == aggressor.Id || protege.Id == aggressor.Id) return Loc.Get("Choose another country.");
        if (!AllianceSystem.GreatPowers(c).Contains(power.Id))
            return Loc.Get("Only a recognized Great Power can intervene in crises.");
        if (CampaignDiplomacy.AtWar(c, power.Id, protege.Id) || CampaignDiplomacy.AtWar(c, power.Id, aggressor.Id) ||
            CampaignDiplomacy.AtWar(c, protege.Id, aggressor.Id))
            return Loc.Get("At war: talks are a peace negotiation now.");

        var relation = CampaignDiplomacy.Peek(c, protege.Id, aggressor.Id);
        if (relation is null || relation.Tension < c.Defines.TensionCrisisAt)
            return Loc.Get("Relations are not in crisis.");
        if (relation.InterveningPowerId >= 0 && relation.InterventionUntilDay > c.ElapsedDays)
            return Loc.Get("Great Power intervention is already active.");
        // A warning has to be one the aggressor believes.
        if (power.Gdp + power.MilitarySpending * 1000f < (aggressor.Gdp + aggressor.MilitarySpending * 1000f) * c.Defines.CrisisInterventionCredibility)
            return Loc.Get("Not strong enough for the warning to be believed.");
        return "";
    }

    public static bool InterveneInCrisis(Campaign c, Nation power, Nation protege, Nation aggressor)
    {
        if (InterventionBlock(c, power, protege, aggressor).Length > 0) return false;
        var relation = CampaignDiplomacy.Get(c, protege.Id, aggressor.Id);
        relation.InterveningPowerId = power.Id;
        relation.InterventionTargetId = aggressor.Id;
        relation.InterventionUntilDay = c.ElapsedDays + 180;
        // Calmed by a measure, not pinned under the line: a crisis a patron leans on can still
        // run on to the border if what drives it does not stop.
        relation.Tension = MathF.Max(0f, relation.Tension - MathF.Max(0f, c.Defines.CrisisInterventionRelief));

        var relP = CampaignDiplomacy.Get(c, power.Id, protege.Id);
        if (relP is not null) OpinionSystem.Remember(c, relP, "held_talks");

        c.Chronicle.Record(c, "great_power_intervened", power.Id, aggressor.Id, protege.Name);
        DiagnosticLogs.Write("simulation", $"CRISIS_INTERVENED power={power.Tag} protege={protege.Tag} aggressor={aggressor.Tag}");
        return true;
    }

    private static void ReviewCrises(Campaign c, int days)
    {
        var powers = AllianceSystem.GreatPowers(c);
        if (powers.Count == 0) return;
        var hot = Hottest(c, c.Defines.TensionCrisisAt).Take(8).ToList();
        if (hot.Count == 0) return;

        foreach (var relation in hot)
        {
            var a = c.NationById(relation.A);
            var b = c.NationById(relation.B);
            if (a is null || b is null || a.Population <= 0 || b.Population <= 0) continue;

            foreach (var powerId in powers)
            {
                if (powerId == a.Id || powerId == b.Id) continue;
                var power = c.NationById(powerId);
                if (power is null || power.IsHuman || power.Movement is not null || power.Population <= 0) continue;

                var relA = CampaignDiplomacy.Peek(c, power.Id, a.Id);
                var relB = CampaignDiplomacy.Peek(c, power.Id, b.Id);
                var backsA = relA is not null && (relA.Stance == DiplomaticStance.Allied || relA.GuaranteedBy(power.Id));
                var backsB = relB is not null && (relB.Stance == DiplomaticStance.Allied || relB.GuaranteedBy(power.Id));
                // A crisis on the far side of the world, between countries it neither borders, backs
                // nor trades with, is not this power's business.
                if (!backsA && !backsB && !HasStake(c, power, a) && !HasStake(c, power, b)) continue;
                var powerA = a.Gdp + a.MilitarySpending * 1000f;
                var powerB = b.Gdp + b.MilitarySpending * 1000f;
                if (!backsA && !backsB)
                {
                    if (powerA < powerB * 0.75f && relA is not null && !relA.GuaranteedBy(power.Id) &&
                        (relA.Opinion >= 10f || (relB is not null && relB.Opinion <= -10f)) &&
                        InterventionBlock(c, power, a, b).Length == 0)
                    {
                        new GuaranteeCommand { IssuerId = power.Id, TargetId = a.Id, Guarantee = true }.Apply(c);
                        c.Chronicle.Record(c, "crisis_guarantee", power.Id, a.Id, b.Name);
                        InterveneInCrisis(c, power, a, b);
                        break;
                    }
                    if (powerB < powerA * 0.75f && relB is not null && !relB.GuaranteedBy(power.Id) &&
                        (relB.Opinion >= 10f || (relA is not null && relA.Opinion <= -10f)) &&
                        InterventionBlock(c, power, b, a).Length == 0)
                    {
                        new GuaranteeCommand { IssuerId = power.Id, TargetId = b.Id, Guarantee = true }.Apply(c);
                        c.Chronicle.Record(c, "crisis_guarantee", power.Id, b.Id, a.Name);
                        InterveneInCrisis(c, power, b, a);
                        break;
                    }
                }
                if (backsA && !backsB && InterventionBlock(c, power, a, b).Length == 0)
                {
                    InterveneInCrisis(c, power, a, b);
                    break;
                }
                if (backsB && !backsA && InterventionBlock(c, power, b, a).Length == 0)
                {
                    InterveneInCrisis(c, power, b, a);
                    break;
                }
                // Talks are offered on some weeks, not the first week of every crisis: a crisis
                // nobody hurries to calm can run on into clashes and a border war (Escalation).
                if (MediationBlock(c, power, a, b).Length == 0 &&
                    AiStatecraft.Restraint(c, power) >= 0.35f &&
                    Roll(c.Seed, power.Id, relation.A * 4099 + relation.B, c.ElapsedDays, 0x3ED1A7) < c.Defines.CrisisMediationChance)
                {
                    StartMediation(c, power, a, b);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Whether a great power has a stake in a country's crisis: a land border, its capital within
    /// Defines.CrisisInterestDegrees of the power's, or a real share of the power's trade.
    /// </summary>
    internal static bool HasStake(Campaign c, Nation power, Nation party)
    {
        if (power.MapIndex > 0 && party.MapIndex > 0 && CampaignDiplomacy.SharesLandBorder(c, power.MapIndex, party.MapIndex)) return true;
        if (float.IsFinite(power.CapitalLatitude) && float.IsFinite(party.CapitalLatitude) &&
            NavalOperations.Distance(power.CapitalLatitude, power.CapitalLongitude, party.CapitalLatitude, party.CapitalLongitude) <=
            c.Defines.CrisisInterestDegrees) return true;
        return power.TradeDependenceOn(party.Id) >= c.Defines.CrisisInterestTradeShare;
    }

    public static IEnumerable<DiplomaticRelation> Hottest(Campaign c, float above = 0f)
    {
        if (c is null) return Array.Empty<DiplomaticRelation>();
        return c.Relations.Values
            .Where(relation => relation.Tension > above && relation.TensionStage < (int)TensionStage.War &&
                               c.NationById(relation.A) is { Population: > 0, Movement: null } &&
                               c.NationById(relation.B) is { Population: > 0, Movement: null })
            .OrderByDescending(relation => relation.Tension)
            .ThenBy(relation => relation.A).ThenBy(relation => relation.B);
    }
}

/// <summary>Open talks to bring the tension with another country down.</summary>
public sealed class HoldTalksCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public bool Started { get; private set; }

    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        Started = Escalation.StartTalks(c, c.NationById(IssuerId), c.NationById(TargetId));
    }
}

/// <summary>Great Power mediation in an international crisis.</summary>
public sealed class MediateCrisisCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int MediatorId { get; init; }
    public int TargetAId { get; init; }
    public int TargetBId { get; init; }
    public bool Mediated { get; private set; }

    public void Apply(Campaign c)
    {
        Mediated = false;
        if (IssuerId != MediatorId || !MechanicFeatures.Allows(c, this)) return;
        Mediated = Escalation.StartMediation(c, c.NationById(MediatorId), c.NationById(TargetAId), c.NationById(TargetBId));
    }
}

/// <summary>Great Power intervention in an international crisis.</summary>
public sealed class InterveneCrisisCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int PowerId { get; init; }
    public int ProtegeId { get; init; }
    public int AggressorId { get; init; }
    public bool Intervened { get; private set; }

    public void Apply(Campaign c)
    {
        Intervened = false;
        if (IssuerId != PowerId || !MechanicFeatures.Allows(c, this)) return;
        Intervened = Escalation.InterveneInCrisis(c, c.NationById(PowerId), c.NationById(ProtegeId), c.NationById(AggressorId));
    }
}
