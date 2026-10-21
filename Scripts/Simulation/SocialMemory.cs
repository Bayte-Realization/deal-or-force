using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int SocialReviewDays { get; set; } = 7;
    public float InstitutionHabitYears { get; set; } = 8f;
    public float BorderHabitYears { get; set; } = 12f;
    public float HabitStabilityBonus { get; set; } = 0.035f;
    public float HabitExpectationPenalty { get; set; } = 0.14f;
    public float BorderHabitRelief { get; set; } = 0.25f;
    public float SocialResponseDays { get; set; } = 180f;
    public float SocialEntrenchmentDays { get; set; } = 730f;
    public float SocialIncidentDecayDays { get; set; } = 540f;
    public float SocialOpinionWeight { get; set; } = 8f;
    public float SocialAllianceWeight { get; set; } = 0.10f;
    public float SocialMovementWeight { get; set; } = 0.06f;
    public int SocialLabelMinimumDays { get; set; } = 180;
}

/// <summary>A belief held by one audience, not an objective moral score of a people.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(PublicImageConverter))]
public sealed class PublicImage
{
    public float Esteem;
    public float Incident;
    public float Familiarity;
    public float Entrenchment;
    public float Target;
    public int LastReviewDay = -1;
    public int ConsistentDays;
    /// <summary>-1: stigmatised, 0: open judgement, 1: idealised. Hysteresis prevents flashing labels.</summary>
    public int Status;
    public string LastReason = "";
    public int LastIncidentDay = -1;

    public void Review(int day, float target, Defines rules)
    {
        if (LastReviewDay >= day || !float.IsFinite(target)) return;
        var days = LastReviewDay < 0 ? 1 : Math.Min(3650, day - LastReviewDay);
        LastReviewDay = day;
        Incident *= MathF.Exp(-days / MathF.Max(1f, rules.SocialIncidentDecayDays));
        Target = M.Clamp(target + Incident, -1f, 1f);
        Familiarity = Approach(Familiarity, 1f, days, rules.SocialEntrenchmentDays);
        var previous = Esteem;
        // An established image takes longer to revise, but never becomes permanent.
        Esteem = Approach(Esteem, Target, days, rules.SocialResponseDays * (1f + Entrenchment));
        var consistent = MathF.Abs(Esteem) > 0.35f && MathF.Sign(previous) == MathF.Sign(Esteem);
        ConsistentDays = consistent ? Math.Min(36500, ConsistentDays + days) : 0;
        Entrenchment = Approach(Entrenchment, consistent ? 1f : 0f, days, rules.SocialEntrenchmentDays);
        var minimum = Math.Max(1, rules.SocialLabelMinimumDays);
        if (Status > 0 && Esteem < 0.40f || Status < 0 && Esteem > -0.40f) Status = 0;
        if (Status == 0 && ConsistentDays >= minimum)
            Status = Esteem >= 0.65f ? 1 : Esteem <= -0.65f ? -1 : 0;
    }

    internal static float Approach(float current, float target, int days, float responseDays) =>
        current + (target - current) * (1f - MathF.Exp(-days / MathF.Max(1f, responseDays)));
}

public sealed partial class Nation
{
    // Presentation/cache-only. Private fields are never part of a save or fingerprint.
    private string _socialKeyTag;
    private string _socialKey;
    internal string CachedSocialKey()
    {
        if (_socialKey is null || _socialKeyTag != Tag) { _socialKeyTag = Tag; _socialKey = "nation:" + Tag; }
        return _socialKey;
    }
    // Additive JSON fields; older saves start with unknown images instead of invented history.
    public string RememberedInstitutions = "";
    public int InstitutionAgeDays;
    public int LastSocialReviewDay = -1;
    public int LastInstitutionReviewDay = -1;
    public float InstitutionalHabit;
    public float ExpectedLivingStandard = -1f;
    public PublicImage DomesticImage = new();
    /// <summary>nation:TAG (including movements), culture:ID, coalition:target:formation-day.</summary>
    public Dictionary<string, PublicImage> PublicImages = new(StringComparer.Ordinal);
}

public sealed partial class ScenarioState
{
    public int RememberedOwner = -1;
    public int SettledBorderDays;
    public int LastBorderReviewDay = -1;
    public float BorderHabit;
}

public readonly record struct SocialIncidentDefinition(float Actor, float Target, float Witness, string Name);

/// <summary>JSON/YAML event-to-memory weights. Only successful public facts reach this catalog.</summary>
public static class SocialIncidentCatalog
{
    private static readonly Dictionary<string, SocialIncidentDefinition> Entries = new(StringComparer.OrdinalIgnoreCase);
    static SocialIncidentCatalog() => ResetCatalog();
    public static void ResetCatalog()
    {
        Entries.Clear();
        foreach (var doc in GameDataLoader.DataDocuments("common", "social_memory"))
            using (doc) Merge(doc.RootElement);
    }
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        { errors?.Add("social_memory: expected an object of public facts"); return; }
        foreach (var item in root.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object)
            { errors?.Add($"social_memory: '{item.Name}' must be an object"); continue; }
            Entries.TryGetValue(item.Name, out var previous);
            float Number(string key, float fallback)
            {
                if (!item.Value.TryGetProperty(key, out var number)) return fallback;
                if (number.ValueKind == JsonValueKind.Number && number.TryGetSingle(out var value) && float.IsFinite(value))
                    return M.Clamp(value, -1f, 1f);
                errors?.Add($"social_memory: '{item.Name}.{key}' must be a finite number");
                return fallback;
            }
            Entries[item.Name] = new(Number("actor", previous.Actor), Number("target", previous.Target), Number("witness", previous.Witness),
                item.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString() : previous.Name ?? item.Name);
        }
    }
    public static bool Find(string kind, out SocialIncidentDefinition value) => Entries.TryGetValue(kind, out value);
}

public static class SocialMemory
{
    public static string NationKey(Nation nation) => nation.CachedSocialKey();
    public static string CoalitionKey(Coalition group) => $"coalition:{group.TargetId}:{group.FormedDay}";
    public static bool ImagesEnabled(Campaign c) => c is not null && c.FeatureEnabled("public_reputation") && MechanicFeatures.SystemEnabled(c, nameof(SocialMemory));
    public static bool HabitsEnabled(Campaign c) => c is not null && c.FeatureEnabled("historical_norms") && MechanicFeatures.SystemEnabled(c, nameof(SocialMemory));
    public static PublicImage Image(Nation audience, string subject, bool create = false)
    {
        if (audience is null || string.IsNullOrWhiteSpace(subject)) return null;
        if (audience.PublicImages.TryGetValue(subject, out var image)) return image;
        if (!create) return null;
        image = new PublicImage();
        audience.PublicImages[subject] = image;
        return image;
    }
    public static float Esteem(Campaign c, Nation audience, Nation subject) => ImagesEnabled(c) && subject is not null
        ? (audience == subject ? subject.DomesticImage?.Esteem ?? 0f : Image(audience, NationKey(subject))?.Esteem ?? 0f) : 0f;

    public static float OpinionEffect(Campaign c, DiplomaticRelation r)
    {
        if (!ImagesEnabled(c) || r is null) return 0f;
        var a = c.NationById(r.A); var b = c.NationById(r.B);
        return (Esteem(c, a, b) + Esteem(c, b, a)) * 0.5f * c.Defines.SocialOpinionWeight;
    }
    public static float StabilityEffect(Campaign c, Nation n)
    {
        if (!HabitsEnabled(c) || n is null) return 0f;
        var expectationGap = MathF.Max(0f, n.ExpectedLivingStandard - n.StandardOfLiving);
        return n.InstitutionalHabit * c.Defines.HabitStabilityBonus - expectationGap * c.Defines.HabitExpectationPenalty;
    }
    public static float BorderRelief(Campaign c, ScenarioState s) => HabitsEnabled(c) && s is not null
        ? M.Clamp(s.BorderHabit * c.Defines.BorderHabitRelief, 0f, 0.5f) : 0f;

    private static bool Due(int day, int id, int previous, int interval) =>
        day >= (interval - Math.Abs(id % interval)) % interval &&
        (previous < 0 || day - previous >= interval);

    // Dormant mechanics retain their state, without counting disabled years on reactivation.
    public static void Resume(Campaign c, string feature)
    {
        var system = c.Defines.FeatureSystems.TryGetValue(nameof(SocialMemory), out var gate) &&
            feature.Equals(gate, StringComparison.OrdinalIgnoreCase);
        if (system || feature.Equals("historical_norms", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var n in c.Nations)
                if (n.LastInstitutionReviewDay >= 0) n.LastInstitutionReviewDay = c.ElapsedDays;
            foreach (var s in c.States)
                if (s.LastBorderReviewDay >= 0) s.LastBorderReviewDay = c.ElapsedDays;
        }
        if (!system && !feature.Equals("public_reputation", StringComparison.OrdinalIgnoreCase)) return;
        if (ImagesEnabled(c)) PublicOutreachActions.Resume(c);
        foreach (var n in c.Nations)
        {
            if (n.LastSocialReviewDay >= 0) n.LastSocialReviewDay = c.ElapsedDays;
            if (n.DomesticImage?.LastReviewDay >= 0) n.DomesticImage.LastReviewDay = c.ElapsedDays;
            foreach (var image in n.PublicImages.Values)
                if (image.LastReviewDay >= 0) image.LastReviewDay = c.ElapsedDays;
        }
    }

    private static readonly Dictionary<string, Nation> SubjectsScratch = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, Nation> OwnersScratch = new();
    private static readonly Dictionary<int, Nation> ByIdScratch = new();

    public static void Tick(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(SocialMemory))) return;
        var habits = HabitsEnabled(c); var images = ImagesEnabled(c);
        if (!habits && !images) return;
        if (images) PublicOutreachActions.Tick(c);
        var interval = Math.Max(1, c.Defines.SocialReviewDays);
        var subjects = SubjectsScratch; subjects.Clear();
        var owners = OwnersScratch; owners.Clear();
        var byId = ByIdScratch; byId.Clear();
        foreach (var subject in c.Nations)
        {
            subjects.TryAdd(NationKey(subject), subject);
            owners.TryAdd(subject.MapIndex, subject);
            byId[subject.Id] = subject;
        }
        foreach (var n in c.Nations)
        {
            if (habits && Due(c.ElapsedDays, n.Id, n.LastInstitutionReviewDay, interval))
            {
                var habitDays = n.LastInstitutionReviewDay < 0 ? 1 : Math.Min(3650, c.ElapsedDays - n.LastInstitutionReviewDay);
                n.LastInstitutionReviewDay = c.ElapsedDays;
                ReviewInstitutions(c, n, habitDays);
            }
            if (!images || !Due(c.ElapsedDays, n.Id, n.LastSocialReviewDay, interval)) continue;
            n.LastSocialReviewDay = c.ElapsedDays;
            n.DomesticImage ??= new PublicImage();
            var expectationGap = habits ? MathF.Max(0f, n.ExpectedLivingStandard - n.StandardOfLiving) : 0f;
            var domestic = M.Clamp((n.Stability - 0.5f) * 1.3f + (n.StandardOfLiving - 0.5f) * 0.5f -
                expectationGap * 0.8f, -1f, 1f);
            n.DomesticImage.Review(c.ElapsedDays, domestic, c.Defines);
            ReviewCultures(c, n);
            ReviewOrganisations(c, n);
            // Scripted narratives may concern a known subject without a diplomatic relationship.
            foreach (var pair in n.PublicImages)
                if (subjects.TryGetValue(pair.Key, out var subject) &&
                    subject.Id != n.Id && CampaignDiplomacy.Peek(c, n.Id, subject.Id) is null)
                    ReviewForeign(c, n, subject, null);
        }
        if (habits) foreach (var s in c.States)
        {
            if (!Due(c.ElapsedDays, s.MapIndex, s.LastBorderReviewDay, interval)) continue;
            var days = s.LastBorderReviewDay < 0 ? 1 : Math.Min(3650, c.ElapsedDays - s.LastBorderReviewDay);
            s.LastBorderReviewDay = c.ElapsedDays;
            if (s.RememberedOwner < 0)
            {
                // The border the scenario drew is not a conquest. Only a later change of
                // owner starts the clock again.
                s.RememberedOwner = s.NationMapIndex;
                s.SettledBorderDays = (int)MathF.Round(MathF.Max(1f, c.Defines.BorderHabitYears) * 365f * 3f);
                s.BorderHabit = Habit(s.SettledBorderDays, c.Defines.BorderHabitYears);
            }
            else if (s.RememberedOwner != s.NationMapIndex)
            { s.RememberedOwner = s.NationMapIndex; s.SettledBorderDays = 0; s.BorderHabit = 0f; }
            if (!owners.TryGetValue(s.NationMapIndex, out var owner)) continue;
            var occupied = StabilityCore.OccupiedShare(c, s, owner);
            // Repression, occupation and persistent hardship interrupt habituation.
            if (occupied > 0.05f || s.Radicalism > 0.5f || s.StandardOfLiving < 0.3f)
                s.SettledBorderDays = Math.Max(0, s.SettledBorderDays - days * 2);
            else s.SettledBorderDays = Math.Min(365000, s.SettledBorderDays + days);
            s.BorderHabit = Habit(s.SettledBorderDays, c.Defines.BorderHabitYears);
        }
        if (images) foreach (var relation in c.Relations.Values)
        {
            if (!byId.TryGetValue(relation.A, out var a) || !byId.TryGetValue(relation.B, out var b)) continue;
            ReviewForeign(c, a, b, relation);
            ReviewForeign(c, b, a, relation);
        }
    }

    public static float Habit(int days, float years) => 1f - MathF.Exp(-Math.Max(0, days) / MathF.Max(365f, years * 365f));
    private static void ReviewInstitutions(Campaign c, Nation n, int days)
    {
        var identity = n.Government + ":" + n.Ideology + ":" + n.SubjectType + ":" + n.OverlordId;
        if (n.RememberedInstitutions != identity)
        { n.RememberedInstitutions = identity; n.InstitutionAgeDays = 0; n.InstitutionalHabit = 0f; }
        var disturbed = n.Stability < 0.35f || n.ShortageIndex > 0.6f;
        n.InstitutionAgeDays = disturbed ? Math.Max(0, n.InstitutionAgeDays - days) : Math.Min(365000, n.InstitutionAgeDays + days);
        n.InstitutionalHabit = Habit(n.InstitutionAgeDays, c.Defines.InstitutionHabitYears);
        if (n.ExpectedLivingStandard < 0f) n.ExpectedLivingStandard = n.StandardOfLiving;
        // Expectations grow with prosperity and adjust downward more slowly: a real cost of an old normal.
        var response = n.StandardOfLiving >= n.ExpectedLivingStandard ? 730f : 2920f;
        n.ExpectedLivingStandard = PublicImage.Approach(n.ExpectedLivingStandard, n.StandardOfLiving, days, response);
    }
    private static void ReviewForeign(Campaign c, Nation audience, Nation subject, DiplomaticRelation relation)
    {
        var image = Image(audience, NationKey(subject));
        if (!Due(c.ElapsedDays, audience.Id + subject.Id, image?.LastReviewDay ?? -1,
            Math.Max(1, c.Defines.SocialReviewDays))) return;
        // Never feed the opinion bonus back into its own target.
        var reasons = 0f;
        if (relation is not null) foreach (var reason in relation.Reasons) reasons += OpinionSystem.Weight(c, reason);
        var affinity = PoliticsCore.Affinity(c, audience.Id, subject.Id);
        var target = M.Clamp(reasons + affinity, -100f, 100f) / 100f * 0.55f;
        target += (subject.StandardOfLiving - 0.5f) * 0.15f;
        target -= M.Clamp(subject.Notoriety / 100f, 0f, 1f) * 0.40f;
        if (relation?.Stance == DiplomaticStance.Allied) target += 0.18f;
        if (relation?.Stance == DiplomaticStance.AtWar) target -= 0.25f;
        if (relation?.TradeAgreement == true) target += 0.12f;
        if (subject.Movement is { } movement)
        {
            if (movement.RecognisedBy.Contains(audience.Id)) target += 0.25f;
            if (movement.DesignatedBy.Contains(audience.Id)) target -= 0.40f;
        }
        (image ?? Image(audience, NationKey(subject), true)).Review(c.ElapsedDays, target, c.Defines);
    }
    private static readonly Dictionary<string, long> CulturePopulationsScratch = new(StringComparer.Ordinal);
    private static readonly List<string> CultureSubjectsScratch = new();
    private static readonly HashSet<string> ActiveCoalitionsScratch = new(StringComparer.Ordinal);
    private static readonly List<string> StaleCoalitionsScratch = new();

    private static void ReviewCultures(Campaign c, Nation audience)
    {
        // Familiarity through living together; never transfer a government's crimes to a whole people.
        var populations = CulturePopulationsScratch; populations.Clear();
        foreach (var s in c.StatesOf(audience)) foreach (var group in s.PopulationGroups)
            if (!string.IsNullOrWhiteSpace(group.Culture) && group.Size > 0)
                populations[group.Culture] = populations.GetValueOrDefault(group.Culture) + group.Size;
        var subjects = CultureSubjectsScratch; subjects.Clear();
        foreach (var key in populations.Keys) subjects.Add(key);
        foreach (var key in audience.PublicImages.Keys)
            if (key.StartsWith("culture:", StringComparison.Ordinal))
            {
                var culture = key[8..];
                if (!populations.ContainsKey(culture)) subjects.Add(culture);
            }
        subjects.Sort(StringComparer.Ordinal);
        foreach (var culture in subjects)
        {
            var contact = M.Clamp(populations.GetValueOrDefault(culture) / (float)Math.Max(1, audience.Population), 0f, 1f);
            var shared = DemographyCatalog.IsPrimary(audience.Tag, culture) ? 0.15f : 0f;
            Image(audience, "culture:" + culture, true).Review(c.ElapsedDays,
                shared + contact * 0.3f + (contact > 0f ? (audience.Stability - 0.5f) * 0.15f : 0f), c.Defines);
        }
    }
    private static void ReviewOrganisations(Campaign c, Nation audience)
    {
        var active = ActiveCoalitionsScratch; active.Clear();
        foreach (var group in c.Coalitions)
        {
            var key = CoalitionKey(group); active.Add(key);
            var target = c.NationById(group.TargetId);
            var rating = group.MemberIds.Contains(audience.Id) ? 0.65f : group.TargetId == audience.Id ? -0.7f :
                target is null ? 0f : M.Clamp(target.Notoriety / 100f, 0f, 0.5f) - Esteem(c, audience, target) * 0.4f;
            Image(audience, key, true).Review(c.ElapsedDays, rating, c.Defines);
        }
        // Dissolved organisations cannot accumulate thousands of stale images in century-long games.
        var stale = StaleCoalitionsScratch; stale.Clear();
        foreach (var key in audience.PublicImages.Keys)
            if (key.StartsWith("coalition:", StringComparison.Ordinal) && !active.Contains(key)) stale.Add(key);
        for (var i = 0; i < stale.Count; i++) audience.PublicImages.Remove(stale[i]);
    }

    public static void Witness(Campaign c, string kind, int actorId, int targetId)
    {
        if (!ImagesEnabled(c) || !SocialIncidentCatalog.Find(kind, out var rule)) return;
        var actor = c.NationById(actorId); var target = c.NationById(targetId);
        if (actor is null) return;
        actor.DomesticImage ??= new PublicImage();
        AddIncident(actor.DomesticImage, rule.Actor * 0.25f, rule.Name, c.ElapsedDays);
        if (target is not null && target != actor && rule.Target != 0f)
        {
            target.DomesticImage ??= new PublicImage();
            AddIncident(target.DomesticImage, rule.Target * 0.25f, rule.Name, c.ElapsedDays);
        }
        foreach (var audience in c.Nations)
        {
            var tie = CampaignDiplomacy.Peek(c, audience.Id, actorId);
            // Only direct participants and audiences already in contact, not every pair on Earth.
            if (audience.Id != targetId && audience.Id != actorId && tie is null) continue;
            var exposure = audience.Id == targetId || audience.Id == actorId ? 1f :
                tie?.Stance == DiplomaticStance.Allied ? 0.35f : 0.65f;
            if (audience != actor && rule.Actor * exposure + rule.Witness != 0f)
                AddIncident(Image(audience, NationKey(actor), true), rule.Actor * exposure + rule.Witness, rule.Name, c.ElapsedDays);
            if (target is not null && audience != target && rule.Target != 0f &&
                (audience == actor || CampaignDiplomacy.Peek(c, audience.Id, targetId) is not null))
                AddIncident(Image(audience, NationKey(target), true), rule.Target * exposure, rule.Name, c.ElapsedDays);
        }
    }
    public static bool AddIncident(PublicImage image, float amount, string reason, int day)
    {
        if (image is null || !float.IsFinite(amount)) return false;
        image.Incident = M.Clamp(image.Incident + M.Clamp(amount, -1f, 1f), -1f, 1f);
        image.LastReason = reason ?? ""; image.LastIncidentDay = day;
        return true;
    }

    public static string StatusName(PublicImage image) => Loc.Get(image?.Status switch
    { -1 => "Stigmatised", 1 => "Idealised", _ => "Open judgement" });
}
