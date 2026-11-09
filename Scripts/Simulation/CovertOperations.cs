using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Covert operations: sabotage, funding the opposition, stealing technology, and the
/// intelligence networks they ride on.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — how covert operations work and where to change them:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// 1. ADD AN OPERATION TYPE → `game/common/covert_operations/*.json`.
///    Each key is an id; the fields are `name`, `description`, `days` (how long it
///    runs), `cost` (in billions), and `effects` (what happens when it succeeds:
///    the same scripted-effects grammar as events and decisions).
///
/// 2. THE ROLL → each operation succeeds with chance `CovertBaseSuccess` plus the
///    network's strength times `CovertStrengthSuccess`, minus the target's
///    watchfulness times `CovertCounterSuccess`. All three are defines below.
///
/// 3. EXPOSURE → whether the target finds out who did it. Works and fails each
///    carry their own chance, modified by the target's counter-intelligence.
///    An exposure creates a grievance (worsens relations) and forces the network
///    to lie low — a different duration for a success and a failure.
///
/// 4. THE NETWORK → built week by week by the intelligence system (IntelNetwork).
///    A stronger network makes operations more likely to succeed and less likely
///    to be traced. A mod that wants easy espionage lowers the counter weights.
///
/// 5. WHAT SABOTAGE DOES → `CovertSabotageDamage` is the war damage added to the
///    target region; a stronger network does proportionally more. The damage is
///    the same kind a war leaves, so reconstruction fixes it.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>

/// <summary>One operation under way: what, against whom, where, and the day it comes due.</summary>
public sealed class CovertOperation
{
    /// <summary>The id of its kind in common/covert_operations.</summary>
    public string Kind = "";
    public int TargetId = -1;
    /// <summary>The region it is aimed at; -1 for one aimed at the country as a whole.</summary>
    public int RegionMapIndex = -1;
    public int StartedDay;
    public int DueDay;
    /// <summary>What it cost, in billions: part of it reaches the opposition it funds.</summary>
    public float Spent;
}

public sealed partial class Nation
{
    /// <summary>This country's covert operations under way, in the order they were ordered.</summary>
    public List<CovertOperation> CovertOperations = new();
    /// <summary>Target country id to the first day its network may be used again.</summary>
    public Dictionary<int, int> CovertQuietUntil = new();
}

public sealed partial class Defines
{
    /// <summary>The most operations one country runs at once.</summary>
    public int CovertOperationLimit { get; set; } = 2;
    /// <summary>The chance an operation works: a base, plus the network's strength, less the target's watchfulness.</summary>
    public float CovertBaseSuccess { get; set; } = 0.30f;
    public float CovertStrengthSuccess { get; set; } = 0.70f;
    public float CovertCounterSuccess { get; set; } = 0.35f;
    /// <summary>The chance it is traced back to us when it works, and when it fails - each plus a share of the target's watchfulness.</summary>
    public float CovertExposedOnSuccess { get; set; } = 0.06f;
    public float CovertExposedOnSuccessCounter { get; set; } = 0.14f;
    public float CovertExposedOnFailure { get; set; } = 0.35f;
    public float CovertExposedOnFailureCounter { get; set; } = 0.45f;
    /// <summary>The grievance an exposed operation leaves, and the share of the network that survives it.</summary>
    public float CovertExposureGrievance { get; set; } = 16f;
    public float CovertExposureNetworkKept { get; set; } = 0.5f;
    /// <summary>How long the network lies low after an operation, and after one that was exposed.</summary>
    public int CovertQuietDays { get; set; } = 90;
    public int CovertQuietAfterExposureDays { get; set; } = 365;
    /// <summary>What sabotage does to a region: war damage, plus more for a stronger network.</summary>
    public float CovertSabotageDamage { get; set; } = 0.04f;
    public float CovertSabotageDamagePerStrength { get; set; } = 0.05f;
    /// <summary>What stirred unrest does to a region: radicalism up, and with it, week by week, its order down.</summary>
    public float CovertUnrestRadicalism { get; set; } = 0.05f;
    public float CovertUnrestRadicalismPerStrength { get; set; } = 0.08f;
    /// <summary>What funding the opposition does to a country, and the share of the money its strongest movement receives.</summary>
    public float CovertOppositionStability { get; set; } = 0.02f;
    public float CovertOppositionStabilityPerStrength { get; set; } = 0.03f;
    public float CovertOppositionRadicalism { get; set; } = 0.03f;
    public float CovertOppositionFunding { get; set; } = 0.5f;
    /// <summary>The most one payment can be, as a share of the target's own output: a superpower's petty cash is not a small country's revolution.</summary>
    public float CovertOppositionFundingCap { get; set; } = 0.002f;
    /// <summary>The share of a technology's cost stolen research is worth, plus more for a stronger network.</summary>
    public float CovertTheftShare { get; set; } = 0.25f;
    public float CovertTheftSharePerStrength { get; set; } = 0.35f;
    /// <summary>How far funding a separatist cause moves one region away from its centre,
    /// and how much it lowers that region's trust in the government and in the laws. Opinion
    /// drifts back toward what the region lives through, so this hurts where the ground is soft.</summary>
    public float CovertSeparatismDivergence { get; set; } = 0.06f;
    public float CovertSeparatismDivergencePerStrength { get; set; } = 0.08f;
    public float CovertSeparatismGovernment { get; set; } = 0.05f;
    public float CovertSeparatismLaw { get; set; } = 0.03f;
    /// <summary>What arming insurgents does to a region's order and radicalism.</summary>
    public float CovertArmInsurgentsOrder { get; set; } = 0.14f;
    public float CovertArmInsurgentsOrderPerStrength { get; set; } = 0.18f;
    public float CovertArmInsurgentsRadicalism { get; set; } = 0.08f;
    public float CovertArmInsurgentsRadicalismPerStrength { get; set; } = 0.12f;
    /// <summary>What a cyber blackout does: temporary infrastructure devastation and stability loss.</summary>
    public float CovertCyberBlackoutDamage { get; set; } = 0.06f;
    public float CovertCyberBlackoutDamagePerStrength { get; set; } = 0.08f;
    /// <summary>How often a government nobody plays considers an operation, and the chance it orders one when it does.</summary>
    public int CovertAiReviewDays { get; set; } = 28;
    public float CovertAiChance { get; set; } = 0.15f;
}

/// <summary>A kind of covert operation (common/covert_operations).</summary>
public sealed class CovertOperationDefinition
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    /// <summary>What success does: "sabotage", "unrest", "opposition", "theft" or "separatism".</summary>
    public string Effect = "";
    /// <summary>The network strength it needs.</summary>
    public float MinimumStrength = 0.3f;
    /// <summary>The days it takes to prepare.</summary>
    public int Days = 28;
    /// <summary>What it costs, in multiples of founding a network.</summary>
    public float Cost = 1f;
    /// <summary>How hard it hits: 1 is the figures in the defines.</summary>
    public float Power = 1f;
    /// <summary>Added to the chance it is traced back: a loud operation is harder to deny.</summary>
    public float Risk;

    /// <summary>Whether it is aimed at one region rather than the country.</summary>
    public bool Regional => Effect is "sabotage" or "unrest" or "separatism" or "arm_insurgents" or "cyber_blackout";
}

/// <summary>
/// What a network in another country is for, past seeing.
///
/// A network (IntelNetworks) shows a rival's provinces and its forces. Once it is strong enough it
/// can act: wreck the works of one region, stir one region's anger, fund the people who want the
/// government gone, carry home research the country has paid for, or pay the people who want a
/// region to leave. An operation takes weeks to
/// prepare and costs a few times what founding the network did; when it comes due it works or it
/// does not - a strong network against a careless state usually works, a young one against a
/// police state usually does not - and either way it may be traced back. A traced operation costs
/// half the network, a grievance that feeds the tension between the two countries, and the news.
/// One that works unseen leaves the target a fire it cannot explain.
///
/// Unrest and a separatist cause are deliberately different purchases. Unrest is the economic
/// anger (radicalism) of one region; a separatist cause is the civic road - how far that region's
/// opinion has left its centre, and whether it still trusts that centre's government and laws
/// (PoliticalCohorts). A country can be quiet and still be pulled apart this way, which is what
/// SecessionCore's federal road is built to read.
///
/// The kinds are data: a mod changes what one costs or how hard it hits, or adds another with one
/// of the five effects, without code. Governments nobody plays use them too - on the enemy they
/// are at war with, the rival they fear, the neighbour they hate.
/// </summary>
public static class CovertOperations
{
    public static readonly string[] Effects = { "sabotage", "unrest", "opposition", "theft", "separatism", "arm_insurgents", "cyber_blackout", "coup" };
    private static readonly Dictionary<string, CovertOperationDefinition> Entries = new(StringComparer.Ordinal);

    static CovertOperations() => ResetCatalog();

    public static IEnumerable<CovertOperationDefinition> All => Entries.Values.OrderBy(d => d.Id, StringComparer.Ordinal);
    public static CovertOperationDefinition Find(string id) => id is not null ? Entries.GetValueOrDefault(id) : null;

    public static void ResetCatalog()
    {
        Entries.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "covert_operations"))
            using (document) Merge(document.RootElement);
    }

    /// <summary>Key by key: a mod naming an existing operation changes only what it names.</summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object || !MechanicFeatures.ValidId(property.Name))
            {
                errors?.Add($"covert_operations: {property.Name} is not an operation");
                continue;
            }
            var entry = Entries.GetValueOrDefault(property.Name) ?? new CovertOperationDefinition { Id = property.Name, Name = property.Name };
            string Text(string key, string fallback) =>
                value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : fallback;
            float Number(string key, float fallback) =>
                value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var figure) && float.IsFinite(figure) ? figure : fallback;
            entry.Name = Text("name", entry.Name);
            entry.Description = Text("description", entry.Description);
            var effect = Text("effect", entry.Effect);
            if (Array.IndexOf(Effects, effect) < 0)
            {
                errors?.Add($"covert_operations: {property.Name} needs an effect of sabotage, unrest, opposition, theft or separatism");
                if (entry.Effect.Length == 0) continue;
            }
            else entry.Effect = effect;
            entry.MinimumStrength = Math.Clamp(Number("minimum_strength", entry.MinimumStrength), 0.05f, 0.9f);
            entry.Days = Math.Clamp((int)Number("days", entry.Days), 7, 365);
            entry.Cost = Math.Clamp(Number("cost", entry.Cost), 0.1f, 20f);
            entry.Power = Math.Clamp(Number("power", entry.Power), 0.1f, 3f);
            entry.Risk = Math.Clamp(Number("risk", entry.Risk), -0.3f, 0.5f);
            Entries[property.Name] = entry;
        }
    }

    /// <summary>What it costs this country, in billions.</summary>
    public static float Cost(Campaign c, Nation owner, CovertOperationDefinition definition) =>
        IntelNetworks.Cost(c, owner) * (definition?.Cost ?? 1f);

    /// <summary>The operation this country has under way against another, or null.</summary>
    public static CovertOperation UnderWay(Nation owner, int targetId)
    {
        if (owner is null) return null;
        foreach (var operation in owner.CovertOperations)
            if (operation.TargetId == targetId) return operation;
        return null;
    }

    /// <summary>The chance it works today: the network's strength against the target's watchfulness.</summary>
    public static float SuccessChance(Campaign c, Nation owner, Nation target)
    {
        var defines = c.Defines;
        var strength = IntelNetworks.Strength(c, owner.Id, target.Id);
        var counter = IntelNetworks.CounterIntelligence(c, target);
        return Math.Clamp(defines.CovertBaseSuccess + defines.CovertStrengthSuccess * strength - defines.CovertCounterSuccess * counter, 0.05f, 0.92f);
    }

    /// <summary>The chance it is traced back to us, having worked or not.</summary>
    public static float ExposureChance(Campaign c, Nation target, CovertOperationDefinition definition, bool succeeded)
    {
        var defines = c.Defines;
        var counter = IntelNetworks.CounterIntelligence(c, target);
        var chance = succeeded
            ? defines.CovertExposedOnSuccess + defines.CovertExposedOnSuccessCounter * counter
            : defines.CovertExposedOnFailure + defines.CovertExposedOnFailureCounter * counter;
        return M.Clamp01(chance + (definition?.Risk ?? 0f));
    }

    /// <summary>Why this operation cannot be ordered now, or empty when it can.</summary>
    public static string Refusal(Campaign c, Nation owner, Nation target, CovertOperationDefinition definition, ScenarioState region)
    {
        if (c is null || !c.FeatureEnabled("intelligence")) return Loc.Get("There are no intelligence services in this world.");
        if (definition is null) return Loc.Get("No such operation.");
        if (owner is null || target is null || owner.Id == target.Id || target.Movement is not null) return Loc.Get("Choose another country.");
        if (owner.Movement is not null) return Loc.Get("Only a state runs intelligence networks.");
        var strength = IntelNetworks.Strength(c, owner.Id, target.Id);
        if (strength <= 0f) return Loc.Get("It needs a network of ours in their country first.");
        if (strength < definition.MinimumStrength)
            return Loc.Format("The network there is too young for this: it needs {0}, it has {1}.", Fmt.Percent(definition.MinimumStrength), Fmt.Percent(strength));
        if (UnderWay(owner, target.Id) is { } running)
            return Loc.Format("An operation is already under way there, {0} days from now.", Math.Max(0, running.DueDay - c.ElapsedDays));
        if (owner.CovertQuietUntil.TryGetValue(target.Id, out var quiet) && quiet > c.ElapsedDays)
            return Loc.Format("The network is lying low for another {0} days.", quiet - c.ElapsedDays);
        if (owner.CovertOperations.Count >= Math.Max(1, c.Defines.CovertOperationLimit))
            return Loc.Format("The service already runs {0} operations, as many as it can.", owner.CovertOperations.Count);
        if (definition.Regional)
        {
            if (region is null || region.NationMapIndex != target.MapIndex) return Loc.Get("Choose one of their regions.");
            if (definition.Effect == "sabotage" && region.WarDamage >= 0.35f) return Loc.Get("There is little left there to wreck.");
            // The civic road needs the region's politics to exist at all: the opinion is read
            // by PoliticalCohorts, and money aimed at a number nobody keeps would vanish.
            if (definition.Effect == "separatism" && !c.FeatureEnabled("political_cohorts"))
                return Loc.Get("Their regions' politics are not read in this world.");
            if (definition.Effect == "separatism" && region.PoliticalDivergence < 0f)
                return Loc.Get("Nothing has been read about that region's politics yet.");
        }
        if (definition.Effect == "theft" && !c.FeatureEnabled("research")) return Loc.Get("There is no research in this world.");
        // Officers move against a government only when it is already falling: a coup needs a
        // country that would have had one anyway, and the money only chooses which officers.
        if (definition.Effect == "coup" && target.Stability >= c.Defines.CoupStabilityThreshold * 1.25f)
            return Loc.Format("Their government is too steady: no officer would risk it above {0} stability.", Fmt.Percent(c.Defines.CoupStabilityThreshold * 1.25f));
        if (definition.Effect == "coup" && (target.IsSubject || c.ElapsedDays - target.LastCoupDay < c.Defines.CoupGraceDays))
            return Loc.Get("The army there is in no state to move against anyone now.");
        if (definition.Effect == "theft" && TheftTarget(owner, target) is null) return Loc.Get("They know nothing we could use.");
        var cost = Cost(c, owner, definition);
        if (owner.Treasury < cost) return Loc.Format("It costs {0}.", Fmt.Money(cost));
        return "";
    }

    /// <summary>
    /// What stolen research would be: what we are researching now, if they have it; else the
    /// costliest of what they have and we could start.
    /// </summary>
    public static TechnologyDefinition TheftTarget(Nation owner, Nation target)
    {
        if (owner is null || target is null || target.Technologies.Count == 0) return null;
        var current = TechnologyCatalog.Of(owner.CurrentResearch);
        if (current is not null && target.HasTechnology(current.Id) && ResearchSystem.Available(owner, current)) return current;
        TechnologyDefinition best = null;
        foreach (var technology in TechnologyCatalog.All)
        {
            if (!target.HasTechnology(technology.Id) || !ResearchSystem.Available(owner, technology)) continue;
            if (best is null || technology.Cost > best.Cost ||
                technology.Cost == best.Cost && string.CompareOrdinal(technology.Id, best.Id) < 0)
                best = technology;
        }
        return best;
    }

    /// <summary>A region's name as the chronicle keeps it: a key the reader's language resolves.</summary>
    internal static string PlaceOf(ScenarioState region)
    {
        if (region is null) return "";
        if (!string.IsNullOrEmpty(region.Id))
        {
            var key = "region." + region.Id + ".name";
            if (Loc.Has(key)) return key;
        }
        return region.Name;
    }

    /// <summary>The chronicle kind for an effect's outcome, spelled out so every one of them is a key the translations can be checked against.</summary>
    private static string NewsKind(string effect, string outcome) => (effect, outcome) switch
    {
        ("sabotage", "done") => "covert_sabotage_done",
        ("sabotage", "suspected") => "covert_sabotage_suspected",
        ("sabotage", "exposed") => "covert_sabotage_exposed",
        ("unrest", "done") => "covert_unrest_done",
        ("unrest", "suspected") => "covert_unrest_suspected",
        ("unrest", "exposed") => "covert_unrest_exposed",
        ("opposition", "done") => "covert_opposition_done",
        ("opposition", "suspected") => "covert_opposition_suspected",
        ("opposition", "exposed") => "covert_opposition_exposed",
        ("theft", "done") => "covert_theft_done",
        ("theft", "exposed") => "covert_theft_exposed",
        ("separatism", "done") => "covert_separatism_done",
        ("separatism", "suspected") => "covert_separatism_suspected",
        ("separatism", "exposed") => "covert_separatism_exposed",
        ("arm_insurgents", "done") => "covert_insurgents_armed",
        ("arm_insurgents", "suspected") => "covert_insurgents_suspected",
        ("arm_insurgents", "exposed") => "covert_insurgents_exposed",
        ("cyber_blackout", "done") => "covert_cyber_blackout",
        ("cyber_blackout", "suspected") => "covert_cyber_blackout_suspected",
        ("cyber_blackout", "exposed") => "covert_cyber_blackout_exposed",
        ("coup", "done") => "covert_coup_done",
        ("coup", "suspected") => "covert_coup_suspected",
        ("coup", "exposed") => "covert_coup_exposed",
        _ => "covert_operation_failed"
    };

    /// <summary>Whether a private chronicle entry of this kind is news to the country it names first.</summary>
    public static bool TellsActor(string kind) => kind is not null && kind.StartsWith("covert_", StringComparison.Ordinal);

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same daily pass, yielded between countries so a day of plots does not
    /// resolve in one frame.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !c.FeatureEnabled("intelligence") || !MechanicFeatures.SystemEnabled(c, nameof(CovertOperations))) yield break;
        var day = c.ElapsedDays;
        var scanned = 0;
        foreach (var owner in c.Nations)
        {
            if (++scanned % 16 == 0) yield return 0;
            if (owner.CovertQuietUntil.Count > 0 && day % 7 == 0)
                foreach (var targetId in owner.CovertQuietUntil.Where(p => p.Value <= day).Select(p => p.Key).ToList())
                    owner.CovertQuietUntil.Remove(targetId);
            if (owner.CovertOperations.Count == 0) continue;
            // Recalled or exposed meanwhile: the agents who were to do it are gone, and the
            // slot it held is free the same day.
            for (var index = owner.CovertOperations.Count - 1; index >= 0; index--)
            {
                var operation = owner.CovertOperations[index];
                if (IntelNetworks.Strength(c, owner.Id, operation.TargetId) > 0f) continue;
                owner.CovertOperations.RemoveAt(index);
                var lost = Find(operation.Kind);
                var where = operation.RegionMapIndex >= 0 ? c.States.Find(s => s.MapIndex == operation.RegionMapIndex) : null;
                c.Chronicle.Record(c, "covert_operation_abandoned", owner.Id, operation.TargetId, lost?.Name ?? operation.Kind, PlaceOf(where), isPublic: false);
            }
            if (owner.CovertOperations.Count == 0) continue;
            CovertOperation due = null;
            foreach (var operation in owner.CovertOperations)
                if (operation.DueDay <= day) { due = operation; break; }
            if (due is null) continue;
            // One a day per country is plenty: two that fall due together resolve on successive days.
            owner.CovertOperations.Remove(due);
            Resolve(c, owner, due);
        }
        var every = Math.Max(7, c.Defines.CovertAiReviewDays);
        if (day % every == every / 2) Ai(c);
    }

    private static void Resolve(Campaign c, Nation owner, CovertOperation operation)
    {
        var target = c.NationById(operation.TargetId);
        var definition = Find(operation.Kind);
        if (target is null || target.Population <= 0 || definition is null || owner.Population <= 0) return;
        var region = operation.RegionMapIndex >= 0 ? c.States.Find(s => s.MapIndex == operation.RegionMapIndex) : null;
        // An operation against the country as a whole is placed at its capital.
        var place = PlaceOf(region ?? c.States.Find(s => s.MapIndex == target.CapitalStateMapIndex && s.NationMapIndex == target.MapIndex));
        var strength = IntelNetworks.Strength(c, owner.Id, target.Id);
        if (strength <= 0f || definition.Regional && (region is null || region.NationMapIndex != target.MapIndex))
        {
            // The network was lost, or the region changed hands, before the day came.
            c.Chronicle.Record(c, "covert_operation_abandoned", owner.Id, target.Id, definition.Name, place, isPublic: false);
            return;
        }
        var key = owner.Id * 1000 + target.Id;
        var succeeded = MovementRules.Roll(c.Seed, key, c.ElapsedDays, 4631) < SuccessChance(c, owner, target);
        var exposed = MovementRules.Roll(c.Seed, key, c.ElapsedDays, 4632) < ExposureChance(c, target, definition, succeeded);
        var subject = definition.Name;
        if (succeeded) subject = Strike(c, owner, target, definition, region, strength, operation.Spent);
        if (subject is null) { succeeded = false; subject = definition.Name; }
        owner.CovertQuietUntil[target.Id] = c.ElapsedDays + Math.Max(0, exposed ? c.Defines.CovertQuietAfterExposureDays : c.Defines.CovertQuietDays);
        if (succeeded)
        {
            c.Chronicle.Record(c, NewsKind(definition.Effect, "done"), owner.Id, target.Id, subject, place, isPublic: false);
            // Stolen research leaves nothing to see; the rest leaves a fire nobody can explain.
            if (!exposed && definition.Effect != "theft")
                c.Chronicle.Record(c, NewsKind(definition.Effect, "suspected"), target.Id, -1, subject, place, isPublic: false);
        }
        else if (!exposed) c.Chronicle.Record(c, "covert_operation_failed", owner.Id, target.Id, subject, place, isPublic: false);
        if (exposed) Expose(c, owner, target, definition, place);
        c.MarkStrategicChange();
    }

    /// <summary>What success does. The chronicle's subject for it, or null when there was nothing to take.</summary>
    internal static string Strike(Campaign c, Nation owner, Nation target, CovertOperationDefinition definition, ScenarioState region, float strength, float spent)
    {
        var d = c.Defines;
        var power = definition.Power;
        if (definition.Effect is "sabotage" or "unrest" or "opposition")
        {
            if (target.IsSubject)
            {
                var agitate = 0.18f * power;
                target.SubjectLibertyModifier = MathF.Min(0.95f, target.SubjectLibertyModifier + agitate);
                target.LibertyDesire = M.Clamp01(target.LibertyDesire + agitate);
                if (!target.SubjectForeignBackers.Contains(owner.Id))
                    target.SubjectForeignBackers.Add(owner.Id);
                target.LastForeignAgitationDay = c.ElapsedDays;
            }
            else
            {
                foreach (var subject in SubjectSystem.SubjectsOf(c, target))
                {
                    var agitate = (definition.Effect == "opposition" ? 0.16f : 0.10f) * power;
                    subject.SubjectLibertyModifier = MathF.Min(0.95f, subject.SubjectLibertyModifier + agitate);
                    subject.LibertyDesire = M.Clamp01(subject.LibertyDesire + agitate);
                    if (definition.Effect is "opposition" or "unrest" && !subject.SubjectForeignBackers.Contains(owner.Id))
                        subject.SubjectForeignBackers.Add(owner.Id);
                }
            }
        }
        switch (definition.Effect)
        {
            case "sabotage":
                // Never below what it already is: a region the war wrecked further while the
                // operation was prepared is not mended by it.
                region.WarDamage = MathF.Max(region.WarDamage,
                    MathF.Min(0.6f, region.WarDamage + power * (d.CovertSabotageDamage + d.CovertSabotageDamagePerStrength * strength)));
                return definition.Name;
            case "unrest":
                // Radicalism is what lasts: a region's order is worked out from it every week.
                region.Radicalism = M.Clamp01(region.Radicalism + power * (d.CovertUnrestRadicalism + d.CovertUnrestRadicalismPerStrength * strength));
                return definition.Name;
            case "opposition":
            {
                target.Stability = M.Clamp01(target.Stability - power * (d.CovertOppositionStability + d.CovertOppositionStabilityPerStrength * strength));
                var capital = c.States.Find(s => s.MapIndex == target.CapitalStateMapIndex && s.NationMapIndex == target.MapIndex);
                if (capital is not null) capital.Radicalism = M.Clamp01(capital.Radicalism + power * d.CovertOppositionRadicalism);
                // The money goes to whoever has most of the country's people behind them.
                var movement = StrongestMovementIn(c, target);
                if (movement is not null)
                    movement.Treasury += MathF.Min(spent * M.Clamp01(d.CovertOppositionFunding), MathF.Max(0f, target.Gdp) * MathF.Max(0f, d.CovertOppositionFundingCap));
                return definition.Name;
            }
            case "coup":
            {
                // The officers we paid take power, and the new government looks to the ones who
                // paid them: our creed, if it is not already theirs, and a debt of gratitude.
                if (target.Stability >= d.CoupStabilityThreshold * 1.25f || target.IsSubject) return null;
                Coups.Seize(c, target);
                var creed = PoliticsCore.IdeologyOf(owner);
                if (creed is not null && !string.IsNullOrEmpty(creed.Id)) target.Ideology = creed.Id;
                if (CampaignDiplomacy.Get(c, target.Id, owner.Id) is { } grateful)
                    grateful.Opinion = MathF.Min(100f, grateful.Opinion + 40f * power);
                return definition.Name;
            }
            case "theft":
            {
                var technology = TheftTarget(owner, target);
                if (technology is null) return null;
                var cost = ResearchSystem.CostFor(c, owner, technology);
                var gain = cost * power * (d.CovertTheftShare + d.CovertTheftSharePerStrength * strength);
                owner.ResearchProgress[technology.Id] = MathF.Min(cost, owner.ResearchProgress.GetValueOrDefault(technology.Id) + gain);
                return technology.Name;
            }
            case "separatism":
            {
                // A region whose politics have never been read has no opinion to move, and a
                // negative seed is not zero: refuse rather than invent a starting point.
                if (region.PoliticalDivergence < 0f || region.GovernmentApproval < 0f || region.LawApproval < 0f) return null;
                // The civic road only: distance from the centre and trust in that centre's
                // government and laws. Radicalism is left to unrest, so the two operations
                // stay different tools and the player can tell which one he bought.
                region.PoliticalDivergence = M.Clamp01(M.Clamp01(region.PoliticalDivergence) +
                    power * (d.CovertSeparatismDivergence + d.CovertSeparatismDivergencePerStrength * strength));
                region.GovernmentApproval = M.Clamp01(M.Clamp01(region.GovernmentApproval) - power * d.CovertSeparatismGovernment);
                region.LawApproval = M.Clamp01(M.Clamp01(region.LawApproval) - power * d.CovertSeparatismLaw);
                return definition.Name;
            }
            case "arm_insurgents":
            {
                region.Radicalism = M.Clamp01(region.Radicalism + power * (d.CovertArmInsurgentsRadicalism + d.CovertArmInsurgentsRadicalismPerStrength * strength));
                region.Stability = M.Clamp01(region.Stability - power * (d.CovertArmInsurgentsOrder + d.CovertArmInsurgentsOrderPerStrength * strength));
                if (target.IsSubject)
                {
                    target.LibertyDesire = M.Clamp01(target.LibertyDesire + 0.20f * power);
                    target.SubjectLibertyModifier = MathF.Min(0.95f, target.SubjectLibertyModifier + 0.20f * power);
                    if (!target.SubjectForeignBackers.Contains(owner.Id)) target.SubjectForeignBackers.Add(owner.Id);
                }
                var movement = StrongestMovementIn(c, target);
                if (movement is not null)
                {
                    movement.Treasury += MathF.Min(spent * 0.6f, MathF.Max(0f, target.Gdp) * 0.003f);
                    if (movement.Movement is not null)
                    {
                        movement.Movement.Militancy = M.Clamp01(MathF.Max(0f, movement.Movement.Militancy) + 0.15f * power);
                        movement.Movement.Cadres += 250f * power;
                    }
                }
                return definition.Name;
            }
            case "cyber_blackout":
            {
                region.WarDamage = MathF.Max(region.WarDamage,
                    MathF.Min(0.7f, region.WarDamage + power * (d.CovertCyberBlackoutDamage + d.CovertCyberBlackoutDamagePerStrength * strength)));
                region.Stability = M.Clamp01(region.Stability - 0.10f * power);
                region.Radicalism = M.Clamp01(region.Radicalism + 0.08f * power);
                target.Stability = M.Clamp01(target.Stability - 0.02f * power);
                return definition.Name;
            }
        }
        return null;
    }

    /// <summary>The movement with most of a country's people behind it, or null.</summary>
    internal static Nation StrongestMovementIn(Campaign c, Nation country)
    {
        Nation best = null;
        var bestWeight = 0f;
        var regions = c.StatesOf(country);
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not { Dissolved: false } state || state.Support.Count == 0) continue;
            var weight = 0f;
            foreach (var region in regions)
                weight += state.Support.GetValueOrDefault(region.MapIndex) * region.Population;
            if (weight > bestWeight) { bestWeight = weight; best = nation; }
        }
        return best;
    }

    private static void Expose(Campaign c, Nation owner, Nation target, CovertOperationDefinition definition, string place)
    {
        var kept = IntelNetworks.Strength(c, owner.Id, target.Id) * M.Clamp01(c.Defines.CovertExposureNetworkKept);
        if (kept < 0.05f) owner.IntelNetworks.Remove(target.Id);
        else owner.IntelNetworks[target.Id] = kept;
        var relation = CampaignDiplomacy.Get(c, owner.Id, target.Id);
        if (relation is not null)
        {
            OpinionSystem.Remember(c, relation, "covert_operation_against_us");
            if (relation.A == target.Id) relation.GrievanceA += c.Defines.CovertExposureGrievance;
            else relation.GrievanceB += c.Defines.CovertExposureGrievance;
        }
        c.Chronicle.Record(c, NewsKind(definition.Effect, "exposed"), target.Id, owner.Id, definition.Name, place);
    }

    /// <summary>
    /// Governments nobody plays act through the networks they have: sabotage against the enemy
    /// they are at war with, theft from the rival who is ahead, unrest where a hated neighbour is
    /// already angry, and - for a government that does not itself answer to voters - money for
    /// the opposition of a shaky rival. Now and then, not every month.
    /// </summary>
    private static void Ai(Campaign c)
    {
        var defines = c.Defines;
        foreach (var owner in c.Nations)
        {
            if (owner.IsHuman || owner.Movement is not null || owner.Population <= 0 || owner.IntelNetworks.Count == 0) continue;
            if (owner.CovertOperations.Count >= Math.Max(1, defines.CovertOperationLimit)) continue;
            if (MovementRules.Roll(c.Seed, owner.Id, c.ElapsedDays, 4621) >= defines.CovertAiChance) continue;
            var authoritarian = PoliticsCore.IdeologyOf(owner).Authority > 0.5f;
            CovertOperationDefinition bestKind = null;
            Nation bestTarget = null;
            ScenarioState bestRegion = null;
            var bestScore = 0f;
            foreach (var (targetId, strength) in owner.IntelNetworks.OrderBy(p => p.Key))
            {
                var target = c.NationById(targetId);
                if (target is null || target.Movement is not null || target.Population <= 0) continue;
                var relation = CampaignDiplomacy.Peek(c, owner.Id, targetId);
                var atWar = CampaignDiplomacy.AtWar(c, owner.Id, targetId);
                var rival = relation?.Stance == DiplomaticStance.Rival;
                var hostile = relation is not null && relation.Opinion <= -50f;
                // Our own people, or our faith, as a minority under a government that does not like
                // us: the oldest reason to pay for somebody else's separatists and partisans.
                var kin = (relation?.Opinion ?? 0f) < 10f ? KinShare(c, owner, target) : 0f;
                if (!atWar && !rival && !hostile && kin < 0.03f) continue;
                foreach (var kind in All)
                {
                    var want = kind.Effect switch
                    {
                        "sabotage" => atWar ? 3f : rival ? 0.8f : 0f,
                        "theft" => rival || hostile ? 2f : 1f,
                        "unrest" => atWar || rival ? 1f : 0.6f,
                        "opposition" => authoritarian && target.Stability < 0.55f ? 1.6f : 0f,
                        "separatism" => (rival ? (atWar ? 0.5f : 1.2f) : 0f) + kin * 6f,
                        "arm_insurgents" => (atWar ? 2.5f : rival ? 1.4f : 0f) + kin * 4f,
                        "cyber_blackout" => atWar ? 2.8f : rival ? 1.0f : 0f,
                        // A rival's government on the edge of falling: push it, and choose who catches it.
                        "coup" => !atWar && (rival || hostile) && target.Stability < defines.CoupStabilityThreshold * 1.25f ? 2.2f : 0f,
                        _ => 0f
                    };
                    if (want <= 0f) continue;
                    var region = kind.Regional ? AiRegion(c, target, kind, kin >= 0.03f ? owner : null) : null;
                    if (kind.Regional && region is null) continue;
                    if (Refusal(c, owner, target, kind, region).Length > 0) continue;
                    if (owner.Treasury < Cost(c, owner, kind) * 5f) continue;
                    var score = want * strength * SuccessChance(c, owner, target);
                    if (score <= bestScore) continue;
                    (bestScore, bestKind, bestTarget, bestRegion) = (score, kind, target, region);
                }
            }
            if (bestKind is null) continue;
            new CovertOperationCommand
            {
                IssuerId = owner.Id, TargetId = bestTarget.Id, OperationId = bestKind.Id, RegionMapIndex = bestRegion?.MapIndex ?? -1
            }.Apply(c);
        }
    }

    /// <summary>Where a government nobody plays aims: the busiest region left standing for sabotage,
    /// the angriest for unrest, the one furthest from its centre for a separatist cause.</summary>
    /// <summary>The share of a country's people who are the other's own people or of its declared faith.</summary>
    public static float KinShare(Campaign c, Nation owner, Nation target)
    {
        var culture = DemographyCatalog.PrimaryCulture(owner.Tag);
        var faith = StateIdentity.IsSecular(owner.DeclaredReligion) ? "" : owner.DeclaredReligion;
        if (string.IsNullOrEmpty(culture) && string.IsNullOrEmpty(faith) || target.Population <= 0) return 0f;
        // The target's own majority is not a minority of ours: kin across a border only.
        if (!string.IsNullOrEmpty(culture) && DemographyCatalog.IsPrimary(target.Tag, culture)) culture = null;
        if (!string.IsNullOrEmpty(faith) && string.Equals(target.DeclaredReligion, faith, StringComparison.OrdinalIgnoreCase)) faith = null;
        long kin = 0;
        foreach (var region in c.StatesOf(target)) kin += KinIn(region, culture, faith);
        return M.Clamp01(kin / (float)Math.Max(1L, target.Population));
    }

    private static long KinIn(ScenarioState region, string culture, string faith)
    {
        long kin = 0;
        foreach (var group in region.PopulationGroups)
            if (!string.IsNullOrEmpty(culture) && string.Equals(group.Culture, culture, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(faith) && string.Equals(group.Religion, faith, StringComparison.OrdinalIgnoreCase))
                kin += Math.Max(0, group.Size);
        return kin;
    }

    private static ScenarioState AiRegion(Campaign c, Nation target, CovertOperationDefinition kind, Nation kinOf = null)
    {
        ScenarioState best = null;
        var bestScore = 0f;
        string culture = null, faith = null;
        if (kinOf is not null)
        {
            culture = DemographyCatalog.PrimaryCulture(kinOf.Tag);
            faith = StateIdentity.IsSecular(kinOf.DeclaredReligion) ? "" : kinOf.DeclaredReligion;
        }
        foreach (var region in c.StatesOf(target))
        {
            if (region.Population <= 0) continue;
            // A sponsor of its own kin aims where they live.
            if (kinOf is not null && kind.Effect is "separatism" or "arm_insurgents")
            {
                var share = KinIn(region, culture, faith) / (float)region.Population;
                if (share >= 0.2f && share > bestScore) { bestScore = share; best = region; }
                continue;
            }
            if (kind.Effect == "separatism")
            {
                // Money does not create a movement, it feeds one that exists: a region that has
                // never been read, or still likes its government, is not worth the network.
                if (region.PoliticalDivergence < 0.15f || region.GovernmentApproval < 0f) continue;
                var split = M.Clamp01(region.PoliticalDivergence) * (1f - M.Clamp01(region.GovernmentApproval));
                if (split > bestScore) { bestScore = split; best = region; }
                continue;
            }
            var score = kind.Effect == "sabotage" || kind.Effect == "cyber_blackout"
                ? (region.WarDamage >= 0.3f ? 0f : region.Population * (0.3f + region.Urbanization) * (1f - region.WarDamage))
                : region.Population * (0.05f + region.Radicalism);
            if (score > bestScore) { bestScore = score; best = region; }
        }
        return best;
    }
}

/// <summary>Order a covert operation against another country, or call off the one under way.</summary>
public sealed class CovertOperationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public string OperationId { get; init; } = "";
    /// <summary>The region it is aimed at, for the operations that are; -1 otherwise.</summary>
    public int RegionMapIndex { get; init; } = -1;
    /// <summary>"start" or "cancel". A cancelled operation's money is spent.</summary>
    public string Action { get; init; } = "start";
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var owner = campaign.NationById(IssuerId);
        var target = campaign.NationById(TargetId);
        if (owner is null || target is null) { Refusal = "No such country."; return; }
        if (Action == "cancel")
        {
            owner.CovertOperations.RemoveAll(operation => operation.TargetId == target.Id);
            campaign.MarkStrategicChange();
            return;
        }
        var definition = CovertOperations.Find(OperationId);
        var region = RegionMapIndex >= 0 ? campaign.States.Find(s => s.MapIndex == RegionMapIndex) : null;
        Refusal = CovertOperations.Refusal(campaign, owner, target, definition, region);
        if (Refusal.Length > 0) return;
        var cost = CovertOperations.Cost(campaign, owner, definition);
        owner.Treasury -= cost;
        owner.CovertOperations.Add(new CovertOperation
        {
            Kind = definition.Id, TargetId = target.Id, RegionMapIndex = definition.Regional ? region.MapIndex : -1,
            StartedDay = campaign.ElapsedDays, DueDay = campaign.ElapsedDays + Math.Max(1, definition.Days), Spent = cost
        });
        campaign.MarkStrategicChange();
    }
}
