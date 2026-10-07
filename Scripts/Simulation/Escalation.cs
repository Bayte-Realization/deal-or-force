using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How often the tension between two countries is weighed, in days.</summary>
    public int EscalationReviewDays { get; set; } = 7;
    /// <summary>The share of the way to where tension is heading that it covers in a review, rising and falling.</summary>
    public float TensionRiseRate { get; set; } = 0.10f;
    public float TensionFallRate { get; set; } = 0.06f;
    /// <summary>Where the stages begin: friction, crisis, armed clashes. Leaving a stage takes the hysteresis less.</summary>
    public float TensionFrictionAt { get; set; } = 25f;
    public float TensionCrisisAt { get; set; } = 50f;
    public float TensionClashesAt { get; set; } = 75f;
    public float TensionHysteresis { get; set; } = 6f;
    /// <summary>What each reason adds to where tension is heading, in points of 100.</summary>
    public float TensionFromHostility { get; set; } = 0.20f;
    public float TensionFromGrievance { get; set; } = 0.35f;
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
    public float IncidentChance { get; set; } = 0.05f;
    /// <summary>A grievance halves in this many years: long enough for a war to be the next generation's.</summary>
    public float GrievanceHalfLifeYears { get; set; } = 30f;
    public float GrievancePerProvinceLost { get; set; } = 1.5f;
    public float GrievancePerLossShare { get; set; } = 2500f;
    public float GrievanceCap { get; set; } = 100f;
    /// <summary>Men lost on each side a week while two countries are exchanging fire, and the rally it brings.</summary>
    public float ClashLossesPerWeek { get; set; } = 12f;
    public float ClashWarSupportPerWeek { get; set; } = 0.001f;
    /// <summary>How much a crisis argues for a war in the AI's sum, at full tension.</summary>
    public float AiEscalationAppetite { get; set; } = 0.9f;
    /// <summary>How long a shared border has to be shooting before a border war can come of it, the chance of one a week after that, and how long it is fought before the ceasefire.</summary>
    public int BorderWarAfterDays { get; set; } = 120;
    public float BorderWarChancePerWeek { get; set; } = 0.007f;
    /// <summary>How strong a side has to be, against the other and the friends behind it, to open a border war.</summary>
    public float BorderWarStrengthRatio { get; set; } = 0.6f;
    public int BorderWarDays { get; set; } = 45;
    /// <summary>Talks: how long, how much they cost, how much tension they take off a week, how often.</summary>
    public int TalksDays { get; set; } = 120;
    public float TalksCost { get; set; } = 0.25f;
    public float TalksTensionPerWeek { get; set; } = 2.5f;
    public int TalksCooldownDays { get; set; } = 365;

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
    /// <summary>The call-up of a total war: extra men a week as a share of the people, while under the ceiling.</summary>
    public float TotalWarMobilisation { get; set; } = 0.0002f;
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
    public int LastIncidentDay = -9999;
    public string LastIncident = "";
    public int LastIncidentBy = -1;
    /// <summary>The men both sides have lost to shooting short of a war.</summary>
    public float ClashLosses;
    /// <summary>Talks under way: who asked for them and until when, and when they last were.</summary>
    public int TalksBy = -1;
    public int TalksUntilDay = -1;
    public int LastTalksDay = -9999;

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
    float Proxy, float Ideology, float Factor, float Target);

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
        if (c is null || c.ElapsedDays <= 0) return;
        if (!MechanicFeatures.SystemEnabled(c, nameof(Escalation))) return;
        var defines = c.Defines;
        var every = Math.Max(1, defines.EscalationReviewDays);
        if (c.ElapsedDays % every != 2 % every) return;

        foreach (var relation in Candidates(c))
            Review(c, relation, every);
        GrowWars(c, every);
        EndBorderWars(c, every);
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
                relation.GrievanceB > 0.01f || relation.Opinion < -20f || relation.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar)
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
            relation.Tension = 100f;
            SetStage(c, relation, a, b, TensionStage.War);
            return;
        }

        var drivers = Drivers(c, relation);
        var rate = drivers.Target > relation.Tension ? defines.TensionRiseRate : defines.TensionFallRate;
        var share = 1f - MathF.Pow(1f - M.Clamp01(rate), days / 7f);
        relation.Tension += (drivers.Target - relation.Tension) * share;
        if (relation.TalksUntilDay > c.ElapsedDays)
            relation.Tension -= defines.TalksTensionPerWeek * days / 7f;
        relation.Tension = M.Clamp(relation.Tension, 0f, 100f);

        var border = CampaignDiplomacy.SharesLandBorder(c, a.MapIndex, b.MapIndex);
        SetStage(c, relation, a, b, StageFor(defines, relation));

        if (StageOf(relation) >= TensionStage.Clashes) Clash(c, relation, a, b, border, days);
        MaybeIncident(c, relation, a, b, border, days);
        MaybeBorderWar(c, relation, a, b, border, days);
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
        var target = M.Clamp(structural * MathF.Max(factor, MathF.Min(1f, factor * 2f)) +
                             (hostility + grievance + lostLand + rivalry + proxy + ideology) * factor, 0f, 100f);
        return new TensionDrivers(structural, hostility, grievance, lostLand, rivalry, proxy, ideology, factor, target);
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
        foreach (var war in c.Wars)
        {
            if (war.Ended || war.Limited) continue;
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
                if (due == 2) Statecraft.CallToArms(c, war);
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
        foreach (var other in c.Wars)
            if (!other.Ended && other.Intensity >= 1 && other.Involves(attacker.Id)) return;

        var claimed = BorderRegion(c, attacker, defender);
        var war = CampaignDiplomacy.DeclareWar(c, attacker.Id, defender.Id, claimed, "border_war",
            onOwnInitiative: false, limited: true);
        if (war is null) return;
        war.LimitedUntilDay = c.ElapsedDays + Math.Max(7, defines.BorderWarDays);
        war.Name = $"{attacker.Name}–{defender.Name} Border War";
        c.Chronicle.Record(c, "border_war", attacker.Id, defender.Id);
        DiagnosticLogs.Write("simulation", $"BORDER_WAR attacker={attacker.Tag} defender={defender.Tag} tension={relation.Tension:0}");
    }

    private static bool CanFace(Campaign c, Nation attacker, Nation defender) =>
        AiStatecraft.CachedStrength(c, attacker) >=
        c.Defines.BorderWarStrengthRatio * AiStatecraft.Deterrence(c, attacker, defender);

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
        foreach (var war in c.Wars.ToArray())
        {
            if (war.Ended || !war.Limited || war.LimitedUntilDay < 0 || c.ElapsedDays < war.LimitedUntilDay) continue;
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
