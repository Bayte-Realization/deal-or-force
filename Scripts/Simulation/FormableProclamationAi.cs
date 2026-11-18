using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool AiFormableProclamations { get; set; } = true;
    public int AiFormableReviewDays { get; set; } = 30;
    public int AiFormableEarliestDay { get; set; } = 120;
    public int AiFormableMinimumIntervalDays { get; set; } = 1825;
    public int AiFormableCrisisRecoveryDays { get; set; } = 180;
    public float AiFormableTreasuryReserveShare { get; set; } = .02f;
    public float AiFormableMinimumScore { get; set; } = .58f;
}

/// <summary>Soft political preferences, not extra player prerequisites. An overlay may
/// make a republic prefer a federation without forbidding its historical restoration.</summary>
public sealed record FormableAiPreference(float Priority, string[] PreferredIdeologies,
    string[] PreferredGovernments, string[] PreferredCourses);

/// <summary>One explanation of an AI decision, derived entirely from the live world.</summary>
public sealed record FormableAiAssessment(FormableNation Definition, bool CanProclaim,
    float Score, string Refusal, float IdentityShare, float Cost, float Reserve);

/// <summary>
/// Choosing a new state identity is not a sweep of every enabled button. The cabinet first
/// protects bills and recovery, compares the available identities with its actual peoples
/// and political project, then submits ONE ordinary founding command. Invitations and wars
/// remain the work of Consolidation/PeacefulFormationAi; this never fabricates their consent.
/// </summary>
public static class FormableProclamationAi
{
    public const string ReviewKey = "ai_formable_review";
    private static readonly Dictionary<string, FormableAiPreference> Preferences = new(StringComparer.Ordinal);
    private static readonly FormableAiPreference Plain = new(.5f, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    private sealed class ReviewContext
    {
        public Dictionary<int, int> StateCounts;
    }

    static FormableProclamationAi() => ResetCatalog();

    public static void ResetCatalog()
    {
        Preferences.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "formable_ai"))
            using (document) Merge(document.RootElement);
    }

    public static FormableAiPreference Preference(string id) =>
        id is not null ? Preferences.GetValueOrDefault(id, Plain) : Plain;

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("formable_ai: expected object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            try
            {
                if (!MechanicFeatures.ValidId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                    throw new FormatException("invalid formable preference");
                var value = entry.Value;
                if (value.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
                { Preferences.Remove(entry.Name); continue; }
                var old = Preference(entry.Name);
                string[] Ids(string key, string[] fallback) => value.TryGetProperty(key, out var list)
                    ? list.EnumerateArray().Select(x => x.GetString()?.ToLowerInvariant() ?? "")
                        .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray() : fallback;
                var priority = value.TryGetProperty("ai_priority", out var number) ? number.GetSingle() : old.Priority;
                var ideologies = Ids("preferred_ideologies", old.PreferredIdeologies);
                var governments = Ids("preferred_governments", old.PreferredGovernments);
                var courses = Ids("preferred_courses", old.PreferredCourses);
                if (!float.IsFinite(priority) || priority < 0 || priority > 1 ||
                    ideologies.Any(id => !IdeologyCatalog.Knows(id)) ||
                    governments.Any(id => !GovernmentCatalog.Knows(id)) ||
                    courses.Any(id => NationalCourseCatalog.Find(id) is null))
                    throw new FormatException("priority outside 0..1 or unknown ideology/government/course");
                Preferences[entry.Name] = new(priority, ideologies, governments, courses);
            }
            catch (Exception error) when (error is FormatException or InvalidOperationException or ArgumentException)
            { errors?.Add($"formable_ai.{entry.Name}: {error.Message}"); }
        }
    }

    public static FormableAiAssessment Evaluate(Campaign campaign, Nation nation, FormableNation definition) =>
        Evaluate(campaign, nation, definition, new ReviewContext(), needRefusalText: true);

    private static FormableAiAssessment Evaluate(Campaign campaign, Nation nation, FormableNation definition,
        ReviewContext context, bool needRefusalText = true, AiFiscalAssessment? precomputedAccounts = null)
    {
        FormableAiAssessment No(string reason, float cost = 0, float reserve = 0, float score = 0, float identity = 0) =>
            new(definition, false, score, reason, identity, cost, reserve);
        if (campaign is null || nation is null || definition is null) return No("Choose a founding country and a project.");
        var settings = campaign.Defines;
        if (needRefusalText)
        {
            if (!settings.AiFormableProclamations || !campaign.FeatureEnabled("formable_nations") ||
                !MechanicFeatures.SystemEnabled(campaign, nameof(FormableProclamationAi)))
                return No("AI proclamations are disabled.");
            // Diplomacy/cabinet delegation does not authorize renaming a human's sovereign state.
            if (HumanCountry(campaign, nation) || campaign.NationById(nation.Id) != nation)
                return No("The player chooses this country's proclamation.");
            if (nation.Population <= 0 || nation.MapIndex <= 0 || !EstablishedCountry(nation) || campaign.Fallen.Contains(nation.Id) ||
                nation.IsSubject || !campaign.HasOwnedLand(nation))
                return No("A living independent country must lead the proclamation.");
            if (!definition.AiFoundation) return No("This project is not enabled for AI proclamation.");
            if (!FormableNations.Eligible(campaign, nation, definition) || !FormableNations.CompatibleProclamation(nation, definition))
                return No("This country cannot lead the foundation.");
            if (!float.IsFinite(settings.AiFormableTreasuryReserveShare) || settings.AiFormableTreasuryReserveShare < 0 ||
                !float.IsFinite(settings.AiFormableMinimumScore) || !float.IsFinite(nation.Stability) ||
                !float.IsFinite(nation.UnionCohesion)) return No("The founding accounts or policy are invalid.");
            if (campaign.ElapsedDays < Math.Max(0, settings.AiFormableEarliestDay))
                return No("The founding government is still settling in.");
            if (nation.FormedNationDay >= 0 && (long)campaign.ElapsedDays - nation.FormedNationDay < Math.Max(1, settings.AiFormableMinimumIntervalDays))
                return No("The previous proclamation needs time to take root.");
            if ((long)campaign.ElapsedDays - nation.LastSecessionDay < Math.Max(0, settings.AiFormableCrisisRecoveryDays) ||
                (long)campaign.ElapsedDays - nation.UnionLastSecessionDay < Math.Max(0, settings.AiFormableCrisisRecoveryDays) ||
                nation.Movement is { StatehoodDay: >= 0 } movement &&
                (long)campaign.ElapsedDays - movement.StatehoodDay < Math.Max(0, settings.AiFormableCrisisRecoveryDays) ||
                nation.DomesticCrisis is { } crisis && campaign.FeatureEnabled("domestic_crises") &&
                (crisis.CoupPressure >= campaign.Defines.CoupPressureThreshold ||
                    crisis.LastOutcomeDay >= 0 && (long)campaign.ElapsedDays - crisis.LastOutcomeDay < Math.Max(0, settings.AiFormableCrisisRecoveryDays)))
                return No("Resolve the domestic crisis before proclaiming another state.");
            if (!string.IsNullOrEmpty(nation.FormedNationId) && campaign.FeatureEnabled("union_cohesion") &&
                nation.UnionCohesion < Math.Max(.6f, campaign.Defines.UnionCohesionFloor))
                return No("Consolidate the current union before expanding it.");
        }
        // This is the player's actual readiness test, including occupation, partners' loyalty,
        // religious mandates, prior identity, other founders, ideology and the full cash price.
        float statusCost;
        if (needRefusalText)
        {
            var status = FormableNations.Evaluate(campaign, nation, definition);
            if (!status.CanForm) return No(status.Requirements.FirstOrDefault(r => !r.Met)?.Label ?? "The foundation is not ready.", status.Cost);
            statusCost = status.Cost;
        }
        else if (!FormableNations.EvaluateCanForm(campaign, nation, definition, out statusCost))
            return No("", statusCost);

        // Evaluate already checks the retained heartland and any larger project for every
        // caller. Do not compare against the entire original empire a second time: a rump
        // government which really has become a local successor may legitimately restore it.
        // A player's loyal single-player dependency is not consent to have the AI erase
        // their country. The command's older subject route predates that distinction.
        var territory = PeacefulFormation.Territory(campaign, definition);
        var map = campaign.ProvinceMap;
        var lastPartnerOwner = -1;
        for (var i = 0; i < territory.Length; i++)
        {
            var p = territory[i];
            if (p <= 0 || p > map.ProvinceCount || map.WaterByProvince[p]) continue;
            var owner = (int)map.ProvinceToNation[p];
            if (owner <= 0 || owner == nation.MapIndex || owner == lastPartnerOwner) continue;
            lastPartnerOwner = owner;
            var partner = campaign.NationByMapIndex(owner);
            if (partner is not null && HumanCountry(campaign, partner) &&
                PeacefulFormation.Participates(campaign, nation, partner, definition) &&
                !PeacefulFormation.Consented(campaign, nation, partner, definition))
                return No("A human-controlled partner must explicitly consent to the foundation.", statusCost);
        }
        var accounts = precomputedAccounts ?? AiFiscalPolicy.Assess(campaign, nation, settings.AiFormableTreasuryReserveShare);
        if (!accounts.CanSpend || accounts.DiscretionaryCash < statusCost)
            return No("Protect outstanding bills and the treasury reserve before founding.", statusCost, accounts.Reserve);

        // Most monthly projects are nowhere near ready. Build a world-sized index only
        // for the rare score comparison that survived readiness and financing, once per review.
        context.StateCounts ??= FormableNations.StateLandProvinceTotals(campaign);
        var identity = FoundationIdentityShare(campaign, nation, definition, context.StateCounts);
        var preference = Preference(definition.Id);
        var project = nation.ConsolidationProject == definition.Id ? .18f : 0;
        var country = FormableNations.ContainsIgnoreCase(definition.RequiredTags, nation.Tag) ||
            FormableNations.ContainsIgnoreCase(definition.EligibleTags, nation.Tag) ||
            !string.IsNullOrEmpty(definition.Restores) ? .08f : 0;
        var politics = FormableNations.ContainsIgnoreCase(preference.PreferredIdeologies, nation.Ideology) ? .18f :
            preference.PreferredIdeologies.Length > 0 ? -.10f : 0;
        politics += FormableNations.ContainsIgnoreCase(preference.PreferredGovernments, nation.Government) ? .12f :
            preference.PreferredGovernments.Length > 0 ? -.06f : 0;
        var course = 0f;
        if (campaign.FeatureEnabled("national_courses") && nation.NationalCourseActive && nation.NationalCourse is { Initialized: true } memory)
            foreach (var id in preference.PreferredCourses)
                course += Math.Clamp(memory.Convictions.GetValueOrDefault(id), 0f, 1f) * .14f;
        var leader = campaign.FeatureEnabled("rulers") && settings.RulersEnabled
            ? Math.Clamp(Rulers.TemperamentShift(campaign, nation).Foresight, -.5f, .5f) * .10f : 0;
        var gain = Math.Clamp(definition.GovernanceGain * 2 + definition.ProductivityGain * 3, 0, .20f);
        var costWeight = Math.Clamp(statusCost / Math.Max(.01f, nation.Gdp), 0, 1) * 2;
        var score = .10f + identity * .25f + country + project + politics + course + leader + gain +
            M.Clamp01(nation.Stability) * .10f + Consolidation.Ambition(campaign, nation) * .10f +
            (preference.Priority - .5f) * .15f - costWeight;
        if (!float.IsFinite(score)) return No("The founding accounts or policy are invalid.", statusCost, accounts.Reserve);
        return score < settings.AiFormableMinimumScore
            ? No("The government prefers its current state identity.", statusCost, accounts.Reserve, score, identity)
            : new(definition, true, score, "", identity, statusCost, accounts.Reserve);
    }

    public static FormableAiAssessment Choose(Campaign campaign, Nation nation) =>
        Choose(campaign, nation, ProclamationCatalogue(campaign));

    /// <summary>
    /// Every identity this AI may proclaim, in catalogue order - the same list for every country
    /// reviewed today, so it is built once instead of sorted once per country.
    /// </summary>
    public static FormableNation[] ProclamationCatalogue(Campaign campaign) =>
        FormableNations.AvailableIn(campaign).Where(d => d.AiFoundation)
            .OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();

    public static FormableAiAssessment Choose(Campaign campaign, Nation nation, IReadOnlyList<FormableNation> catalogue)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || nation is null || HumanCountry(campaign, nation))
            return null;
        var settings = campaign.Defines;
        if (!settings.AiFormableProclamations || !campaign.FeatureEnabled("formable_nations") ||
            !MechanicFeatures.SystemEnabled(campaign, nameof(FormableProclamationAi))) return null;
        if (campaign.NationById(nation.Id) != nation) return null;
        if (nation.Population <= 0 || nation.MapIndex <= 0 || !EstablishedCountry(nation) || campaign.Fallen.Contains(nation.Id) ||
            nation.IsSubject || !campaign.HasOwnedLand(nation)) return null;
        if (!float.IsFinite(settings.AiFormableTreasuryReserveShare) || settings.AiFormableTreasuryReserveShare < 0 ||
            !float.IsFinite(settings.AiFormableMinimumScore) || !float.IsFinite(nation.Stability) ||
            !float.IsFinite(nation.UnionCohesion)) return null;
        if (campaign.ElapsedDays < Math.Max(0, settings.AiFormableEarliestDay)) return null;
        if (nation.FormedNationDay >= 0 && (long)campaign.ElapsedDays - nation.FormedNationDay < Math.Max(1, settings.AiFormableMinimumIntervalDays)) return null;
        if ((long)campaign.ElapsedDays - nation.LastSecessionDay < Math.Max(0, settings.AiFormableCrisisRecoveryDays) ||
            (long)campaign.ElapsedDays - nation.UnionLastSecessionDay < Math.Max(0, settings.AiFormableCrisisRecoveryDays) ||
            (nation.Movement is { StatehoodDay: >= 0 } movement &&
             (long)campaign.ElapsedDays - movement.StatehoodDay < Math.Max(0, settings.AiFormableCrisisRecoveryDays)) ||
            (nation.DomesticCrisis is { } crisis && campaign.FeatureEnabled("domestic_crises") &&
             (crisis.CoupPressure >= settings.CoupPressureThreshold ||
              (crisis.LastOutcomeDay >= 0 && (long)campaign.ElapsedDays - crisis.LastOutcomeDay < Math.Max(0, settings.AiFormableCrisisRecoveryDays))))) return null;
        if (!string.IsNullOrEmpty(nation.FormedNationId) && campaign.FeatureEnabled("union_cohesion") &&
            nation.UnionCohesion < Math.Max(.6f, settings.UnionCohesionFloor)) return null;
        if (!FormableNations.IsAtPeace(campaign, nation.Id)) return null;
        var accounts = AiFiscalPolicy.Assess(campaign, nation, settings.AiFormableTreasuryReserveShare);
        if (!accounts.CanSpend || accounts.DiscretionaryCash < .05f) return null;

        FormableAiAssessment best = null;
        var context = new ReviewContext();
        // Catalogue order is only a stable tie break, never the reason to form the first button.
        for (var i = 0; i < catalogue.Count; i++)
        {
            var definition = catalogue[i];
            if (!definition.AiFoundation) continue;
            if (!FormableNations.CompatibleProclamation(nation, definition)) continue;
            if (nation.FormedNationHistory.Contains(definition.Id) || nation.Stability < definition.MinimumStability) continue;
            var estimatedCost = Math.Max(.05f, nation.Gdp * definition.CostShare);
            if (nation.Treasury < estimatedCost || accounts.DiscretionaryCash < estimatedCost) continue;
            if (definition.RequiredIdeology.Length > 0 && !FormableNations.ContainsIgnoreCase(definition.RequiredIdeology, nation.Ideology)) continue;
            if (definition.RequiredGovernment.Length > 0 && !FormableNations.ContainsIgnoreCase(definition.RequiredGovernment, nation.Government)) continue;
            if (!FormableNations.Eligible(campaign, nation, definition)) continue;
            var candidate = Evaluate(campaign, nation, definition, context, needRefusalText: false, precomputedAccounts: accounts);
            if (candidate.CanProclaim && (best is null || candidate.Score > best.Score)) best = candidate;
        }
        return best;
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>
    /// The same daily review, a country at a time.
    ///
    /// It used to be one indivisible call, and on a late campaign it measured 58-111 ms of a
    /// single frame: smooth time can only hand a frame back at a stage boundary, so the one
    /// pass that never had one was the one freeze smoothing could not touch. The work and its
    /// order are unchanged - the same countries, the same review, the same stamp.
    /// </summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || !campaign.Defines.AiFormableProclamations ||
            !campaign.FeatureEnabled("formable_nations") || !MechanicFeatures.SystemEnabled(campaign, nameof(FormableProclamationAi))) yield break;
        var interval = campaign.Defines.AiFormableReviewDays;
        if (interval <= 0 || campaign.ElapsedDays < Math.Max(0, campaign.Defines.AiFormableEarliestDay)) yield break;
        // The catalogue is the same for every country reviewed today: built once here instead of
        // filtered and sorted once per country. Rebuilt only if the world's shape changed under
        // the review - a country vanishing mid-pass can add a restoration to it.
        FormableNation[] catalogue = null;
        var catalogueRevision = int.MinValue;
        var catalogueNations = -1;
        List<Nation> due = null;
        var allNations = campaign.Nations;
        for (var i = 0; i < allNations.Count; i++)
        {
            var nation = allNations[i];
            if (HumanCountry(campaign, nation) || nation.Population <= 0 || nation.IsSubject || !EstablishedCountry(nation) ||
                campaign.Fallen.Contains(nation.Id) ||
                (campaign.ElapsedDays + (long)nation.Id) % interval != 0 ||
                nation.DecisionAvailableDay.GetValueOrDefault(ReviewKey) > campaign.ElapsedDays) continue;
            due ??= new List<Nation>();
            due.Add(nation);
        }
        if (due is null) yield break;
        due.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        for (var di = 0; di < due.Count; di++)
        {
            var nation = due[di];
            nation.DecisionAvailableDay[ReviewKey] = (int)Math.Min(int.MaxValue, (long)campaign.ElapsedDays + interval);
            if (catalogue is null || catalogueRevision != campaign.MapRevision || catalogueNations != campaign.Nations.Count)
            {
                catalogueRevision = campaign.MapRevision;
                catalogueNations = campaign.Nations.Count;
                catalogue = ProclamationCatalogue(campaign);
            }
            var choice = Choose(campaign, nation, catalogue);
            if (choice is not null)
                new FormNationCommand { IssuerId = nation.Id, FormableId = choice.Definition.Id }.Apply(campaign);
            yield return 0;
        }
    }

    // MovementRules.GraduateToStatehood retains the movement identity after it becomes
    // a state: an established successor must not remain excluded merely by its origins.
    internal static bool EstablishedCountry(Nation nation) => nation is not null && (nation.Movement is null ||
        nation.Movement is { Landless: false, Dissolved: false, Exile: false, StatehoodDay: >= 0 });

    private static bool HumanCountry(Campaign campaign, Nation nation) =>
        nation.IsHuman || nation.IsPlayer || campaign.HumanSeats.Contains(nation.Id);

    [ThreadStatic] private static HashSet<string> _identityCultures;
    [ThreadStatic] private static HashSet<string> _identityGroups;

    private static (HashSet<int> Provinces, Dictionary<int, int> Foundation) GetDefinitionFoundation(Campaign campaign, FormableNation definition)
    {
        var cache = campaign.Caches.FormableFoundation;
        var revision = campaign.OwnershipRevision;
        // Province-to-region content only (OwnershipRevision); a battle's captures leave it alone.
        if (cache.TryGet(revision, definition.Id, out var cached)) return cached;

        var map = campaign.ProvinceMap;
        var provinces = new HashSet<int>();
        var foundation = new Dictionary<int, int>();
        foreach (var p in PeacefulFormation.Territory(campaign, definition))
        {
            if (p <= 0 || p > map.ProvinceCount || map.WaterByProvince[p]) continue;
            provinces.Add(p);
            var state = map.ProvinceToState[p];
            foundation[state] = foundation.GetValueOrDefault(state) + 1;
        }
        var entry = (provinces, foundation);
        cache.Store(revision, definition.Id, entry);
        return entry;
    }

    private static float FoundationIdentityShare(Campaign campaign, Nation founder, FormableNation definition,
        Dictionary<int, int> counts)
    {
        // A founding review ran 135 times in twelve days and allocated two culture sets each,
        // forty-odd kilobytes a call: a gen1 pause's worth of garbage a week. The scratch is
        // thread-static and cleared on entry; nothing reenters this review.
        var cultures = _identityCultures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cultures.Clear();
        cultures.UnionWith(founder.FoundedCultures);
        cultures.UnionWith(DemographyCatalog.PrimaryCultures(founder.Tag));
        cultures.UnionWith(definition.EligibleCultures);
        foreach (var tag in definition.RequiredTags) cultures.UnionWith(DemographyCatalog.PrimaryCultures(tag));
        var groups = _identityGroups ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        groups.Clear();
        groups.UnionWith(definition.EligibleCultureGroups);
        var (provinces, foundation) = GetDefinitionFoundation(campaign, definition);
        counts ??= FormableNations.StateLandProvinceTotals(campaign);
        double total = 0, represented = 0;
        foreach (var (stateMapIndex, included) in foundation)
        {
            var state = campaign.StateByMapIndex(stateMapIndex);
            if (state is null || !counts.TryGetValue(stateMapIndex, out var count) || count <= 0) continue;
            var fraction = included / (double)count;
            double people = 0, matching = 0;
            foreach (var population in state.PopulationGroups)
            {
                if (population is null || population.Size <= 0) continue;
                people += population.Size;
                var culture = population.Culture ?? "";
                if (cultures.Contains(culture) || groups.Contains(DemographyCatalog.Culture(culture)?.Group ?? "") ||
                    FormableNations.HasFaith(definition.EligibleReligions, population.Religion ?? "")) matching += population.Size;
            }
            total += Math.Max(Math.Max(0L, state.Population), people) * fraction;
            represented += matching * fraction;
        }
        return total > 0 ? (float)Math.Clamp(represented / total, 0, 1) : 0;
    }
}
