using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        ? EsteemOf(audience, subject) : 0f;

    /// <summary>The same number for a caller that has already read the switch and holds both countries.</summary>
    private static float EsteemOf(Nation audience, Nation subject) =>
        audience == subject ? subject.DomesticImage?.Esteem ?? 0f : Image(audience, NationKey(subject))?.Esteem ?? 0f;

    public static float OpinionEffect(Campaign c, DiplomaticRelation r)
    {
        if (!ImagesEnabled(c) || r is null) return 0f;
        return OpinionEffect(c, c.NationById(r.A), c.NationById(r.B));
    }

    internal static float OpinionEffect(Campaign c, Nation a, Nation b)
    {
        if (!ImagesEnabled(c) || a is null || b is null) return 0f;
        // The switch is read once. Each Esteem call read it again - four case-insensitive
        // string lookups apiece - and this is asked for every relation the opinion pass settles
        // and every pair the weekly diplomacy scan compares: the two extra reads were a third
        // of a microsecond on a path walked sixty thousand times a fortnight.
        return (EsteemOf(a, b) + EsteemOf(b, a)) * 0.5f * c.Defines.SocialOpinionWeight;
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

    /// <summary>A relation whose day it is, between two living countries, and where it stands in the table's walk.</summary>
    private readonly struct DueRelation
    {
        public readonly Nation A;
        public readonly Nation B;
        public readonly DiplomaticRelation Relation;
        public readonly int Position;

        public DueRelation(Nation a, Nation b, DiplomaticRelation relation, int position)
        {
            A = a;
            B = b;
            Relation = relation;
            Position = position;
        }
    }

    /// <summary>
    /// The day's relations and what each side's image of the other is drawn towards, for one
    /// campaign: filled at the head of the day's review and read until its end, across the
    /// pauses between stages - so it belongs to the campaign and not to the thread.
    /// </summary>
    private sealed class RelationReviewTable
    {
        public readonly Dictionary<string, Nation> Subjects = new(StringComparer.Ordinal);
        public readonly DenseIntTable<Nation> Owners = new();
        public readonly DenseIntTable<Nation> ById = new();

        public readonly List<DueRelation> Due = new();

        /// <summary>By the same index as <see cref="Due"/>: what A's image of B and B's of A are drawn towards.</summary>
        public RelationReviewResult[] Targets = new RelationReviewResult[1024];

        /// <summary>
        /// Every country with an image to review today, in the order the walk first met it, and
        /// its reviews in the order the walk met them: twice the index into <see cref="Due"/>,
        /// plus one when the country is the relation's B.
        /// </summary>
        public readonly List<(Nation Audience, List<int> Reviews)> Audiences = new();

        private readonly Dictionary<Nation, List<int>> _lists = new(ReferenceEqualityComparer.Instance);

        public void Begin()
        {
            Due.Clear();
            foreach (var (_, list) in Audiences) list.Clear();
            Audiences.Clear();
        }

        /// <summary>This country's list for today; the caller adds to it at once.</summary>
        public List<int> Of(Nation nation)
        {
            if (!_lists.TryGetValue(nation, out var list)) _lists[nation] = list = new List<int>();
            if (list.Count == 0) Audiences.Add((nation, list));
            return list;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, RelationReviewTable> RelationReviews = new();

    private readonly struct RelationReviewResult
    {
        public readonly float TargetAB;
        public readonly float TargetBA;

        public RelationReviewResult(float targetAB, float targetBA)
        {
            TargetAB = targetAB;
            TargetBA = targetBA;
        }
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    // Countries reviewed side by side in one stage. It was 64, which is over before the thread
    // pool's workers have woken: with the pool, soc:nations cost 85 ms of the 14 late days and
    // 55 at 256 - 512 keeps all ~444 countries in a single parallel barrier.
    private const int NationBatchSize = 512;

    // The stretch of the relation table whose images are asked "are you due" together, before
    // any of them is reviewed (ReviewAudience). Not a batch for the pool any more.
    private const int RelationBatchSize = 2048;

    private static void ReviewNation(Campaign c, Nation n, Dictionary<string, Nation> subjects, int interval, bool habits, bool images)
    {
        var living = n.Population > 0 || n.Movement is { Dissolved: false };
        if (!living && !c.HasOwnedLand(n))
        {
            if (images && n.PublicImages.Count > 0) n.PublicImages.Clear();
            return;
        }
        if (habits && Due(c.ElapsedDays, n.Id, n.LastInstitutionReviewDay, interval))
        {
            var habitDays = n.LastInstitutionReviewDay < 0 ? 1 : Math.Min(3650, c.ElapsedDays - n.LastInstitutionReviewDay);
            n.LastInstitutionReviewDay = c.ElapsedDays;
            ReviewInstitutions(c, n, habitDays);
        }
        if (!living || !images) return;
        if (Due(c.ElapsedDays, n.Id, n.LastSocialReviewDay, interval))
        {
            n.LastSocialReviewDay = c.ElapsedDays;
            n.DomesticImage ??= new PublicImage();
            var expectationGap = habits ? MathF.Max(0f, n.ExpectedLivingStandard - n.StandardOfLiving) : 0f;
            var domestic = M.Clamp((n.Stability - 0.5f) * 1.3f + (n.StandardOfLiving - 0.5f) * 0.5f -
                expectationGap * 0.8f, -1f, 1f);
            n.DomesticImage.Review(c.ElapsedDays, domestic, c.Defines);
            ReviewCultures(c, n);
            ReviewOrganisations(c, n);
            if (((c.ElapsedDays / interval) + n.Id) % 13 == 0) ForgetTheGone(c, n, subjects);
        }
        // Scripted narratives may concern a known subject without a diplomatic relationship.
        // Rare, and the walk is long in a long campaign: every fourth review.
        // Skip completely inert faded images without a relation so ForgetTheGone can prune them cleanly,
        // and collect active candidates into thread-local scratch before ReviewForeign mutates PublicImages.
        if (((c.ElapsedDays / interval) + n.Id) % 4 == 0)
        {
            var pending = UninvolvedScratch; pending.Clear();
            foreach (var pair in n.PublicImages)
            {
                var existing = pair.Value;
                if (existing.Status == 0 && MathF.Abs(existing.Esteem) < 0.015f &&
                    MathF.Abs(existing.Incident) < 0.01f &&
                    (existing.LastIncidentDay < 0 || c.ElapsedDays - existing.LastIncidentDay > 365))
                    continue;
                if (subjects.TryGetValue(pair.Key, out var subject) &&
                    subject.Id != n.Id &&
                    (subject.Population > 0 || subject.Movement is { Dissolved: false }) &&
                    CampaignDiplomacy.Peek(c, n.Id, subject.Id) is null)
                    pending.Add(subject);
            }
            for (var i = 0; i < pending.Count; i++)
                ReviewForeign(c, n, pending[i], null);
            pending.Clear();
        }
    }

    [ThreadStatic] private static List<Nation> _uninvolvedScratch;
    private static List<Nation> UninvolvedScratch => _uninvolvedScratch ??= new();

    /// <summary>The same reviews, yielded between countries, regions and relationships.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(SocialMemory))) yield break;
        var habits = HabitsEnabled(c); var images = ImagesEnabled(c);
        if (!habits && !images) yield break;
        if (images) PublicOutreachActions.Tick(c);
        var interval = Math.Max(1, c.Defines.SocialReviewDays);
        var reviews = RelationReviews.GetValue(c, static _ => new RelationReviewTable());
        var subjects = reviews.Subjects; subjects.Clear();
        var owners = reviews.Owners;
        var byId = reviews.ById;
        var nations = c.Nations;
        var maxMap = 0;
        var maxId = 0;
        for (var i = 0; i < nations.Count; i++)
        {
            var n = nations[i];
            if (n.MapIndex > maxMap) maxMap = n.MapIndex;
            if (n.Id > maxId) maxId = n.Id;
        }
        owners.Reset(maxMap);
        byId.Reset(maxId);
        for (var i = 0; i < nations.Count; i++)
        {
            var subject = nations[i];
            subjects.TryAdd(NationKey(subject), subject);
            owners.TryAdd(subject.MapIndex, subject);
            byId.TryAdd(subject.Id, subject);
        }
        // Warm lazy campaign caches on the coordinator thread before parallel workers read them.
        c.StatesOf(0);
        if (nations.Count > 0) c.HasOwnedLand(nations[0]);
        if (Campaign.StageOverdue()) yield return 0;
        var nationCount = nations.Count;
        for (var start = 0; start < nationCount; start += NationBatchSize)
        {
            var end = Math.Min(nationCount, start + NationBatchSize);
            using (SimProfiler.Measure("  soc:nations"))
            {
                DeterministicParallel.ForRange(c, start, end, (from, to) =>
                {
                    for (var idx = from; idx < to; idx++)
                        ReviewNation(c, nations[idx], subjects, interval, habits, images);
                });
            }
            if (end < nationCount && Campaign.StageOverdue()) yield return 0;
        }
        if (Campaign.StageOverdue()) yield return 0;
        if (habits && c.States.Count > 0)
        {
            ControlTally.World(c, 0, 0);
            var states = c.States;
            var stateCount = states.Count;
            var elapsed = c.ElapsedDays;
            var borderHabitYears = c.Defines.BorderHabitYears;
            const int StateBatchSize = 512;
            for (var start = 0; start < stateCount; start += StateBatchSize)
            {
                var end = Math.Min(stateCount, start + StateBatchSize);
                using (SimProfiler.Measure("  soc:borders"))
                {
                    for (var si = start; si < end; si++)
                    {
                        var s = states[si];
                        if (!Due(elapsed, s.MapIndex, s.LastBorderReviewDay, interval)) continue;
                        var days = s.LastBorderReviewDay < 0 ? 1 : Math.Min(3650, elapsed - s.LastBorderReviewDay);
                        s.LastBorderReviewDay = elapsed;
                        if (s.RememberedOwner < 0)
                        {
                            // The border the scenario drew is not a conquest. Only a later change of
                            // owner starts the clock again.
                            s.RememberedOwner = s.NationMapIndex;
                            s.SettledBorderDays = (int)MathF.Round(MathF.Max(1f, borderHabitYears) * 365f * 3f);
                            s.BorderHabit = Habit(s.SettledBorderDays, borderHabitYears);
                        }
                        else if (s.RememberedOwner != s.NationMapIndex)
                        { s.RememberedOwner = s.NationMapIndex; s.SettledBorderDays = 0; s.BorderHabit = 0f; }
                        var owner = owners.GetOrDefault(s.NationMapIndex);
                        if (owner is null) continue;
                        var occupied = StabilityCore.OccupiedShare(c, s, owner);
                        // Repression, occupation and persistent hardship interrupt habituation.
                        if (occupied > 0.05f || s.Radicalism > 0.5f || s.StandardOfLiving < 0.3f)
                            s.SettledBorderDays = Math.Max(0, s.SettledBorderDays - days * 2);
                        else s.SettledBorderDays = Math.Min(365000, s.SettledBorderDays + days);
                        s.BorderHabit = Habit(s.SettledBorderDays, borderHabitYears);
                    }
                }
                if (end < stateCount && Campaign.StageOverdue()) yield return 0;
            }
        }
        if (Campaign.StageOverdue()) yield return 0;
        if (images)
        {
            // The day's relations, found once, and then two loops long enough to be shared.
            //
            // The walk used to take 2,048 relations at a time and hand the three hundred of them
            // that were due to the thread pool on the spot: 448 small loops in the 14 late days,
            // each over before the pool's workers had woken, so the work was in effect done on
            // this thread with the cost of asking added (soc:relations 228 ms, no faster than
            // with the pool switched off).
            //
            // First, what each side's image of the other is drawn towards. A target reads the
            // relation's reasons, the two countries and a movement's recognitions - nothing a
            // review of an image writes - so all nine thousand of the day are worked out in one
            // loop before the first image is touched.
            //
            // Six relations in seven are not due today, and the table's key already says which:
            // it is the two ids (CampaignDiplomacy.Get), so the day's sum is read off the key and
            // a relation that is not due is never opened.
            var due = reviews.Due;
            using (SimProfiler.Measure("  soc:relations"))
            {
                reviews.Begin();
                var position = -1;
                foreach (var entry in c.Relations)
                {
                    position++;
                    if (((c.ElapsedDays + (int)(entry.Key >> 32) + (int)entry.Key) % interval) != 0) continue;
                    var relation = entry.Value;
                    var a = byId.GetOrDefault(relation.A);
                    var b = byId.GetOrDefault(relation.B);
                    if (a is null || b is null) continue;
                    var liveA = a.Population > 0 || a.Movement is { Dissolved: false };
                    var liveB = b.Population > 0 || b.Movement is { Dissolved: false };
                    if (!liveA || !liveB) continue;
                    var slot = due.Count;
                    due.Add(new DueRelation(a, b, relation, position));
                    reviews.Of(a).Add(slot << 1);
                    reviews.Of(b).Add((slot << 1) | 1);
                }
                if (reviews.Targets.Length < due.Count)
                    reviews.Targets = new RelationReviewResult[Math.Max(due.Count, reviews.Targets.Length * 2)];
                var targets = reviews.Targets;
                DeterministicParallel.ForRange(c, 0, due.Count, (from, to) =>
                {
                    for (var i = from; i < to; i++)
                    {
                        var item = due[i];
                        var reasons = 0f;
                        var itemReasons = item.Relation.Reasons;
                        for (var r = 0; r < itemReasons.Count; r++) reasons += OpinionSystem.Weight(c, itemReasons[r]);
                        var affinity = PoliticsCore.Affinity(c, item.A.Id, item.B.Id);
                        targets[i] = new RelationReviewResult(
                            ComputeForeignTarget(c, item.A, item.B, item.Relation, reasons, affinity),
                            ComputeForeignTarget(c, item.B, item.A, item.Relation, reasons, affinity));
                    }
                });
            }
            if (Campaign.StageOverdue()) yield return 0;
            // Then the images, a country's at a time and the countries side by side: an image
            // is kept by the country that holds it, so every review of the day that touches one
            // country's table is that country's own, and is made here in the order the walk
            // met them (ReviewAudience).
            var audiences = reviews.Audiences;
            for (var start = 0; start < audiences.Count; start += NationBatchSize)
            {
                var end = Math.Min(audiences.Count, start + NationBatchSize);
                using (SimProfiler.Measure("  soc:relations"))
                {
                    DeterministicParallel.ForRange(c, start, end, (from, to) =>
                    {
                        for (var index = from; index < to; index++)
                            ReviewAudience(c, reviews, audiences[index].Audience, audiences[index].Reviews, interval);
                    });
                }
                if (end < audiences.Count && Campaign.StageOverdue()) yield return 0;
            }
        }
    }

    [ThreadStatic] private static List<(PublicImage Image, bool Due)> _audienceScanScratch;

    /// <summary>
    /// One country's reviews of the day, in the order the walk of the relations met them.
    ///
    /// The walk asked the relations of one stretch of 2,048 of the table which of their images
    /// were due, reviewed those, and went on to the next stretch. That order matters to one
    /// table at a time and to nothing else: two countries under one tag share the image a third
    /// holds of them, and whether the second finds it already reviewed depends on which stretch
    /// each stands in. So it is replayed here stretch by stretch for this country's table alone.
    /// </summary>
    private static void ReviewAudience(Campaign c, RelationReviewTable reviews, Nation audience, List<int> mine, int interval)
    {
        var due = reviews.Due;
        var targets = reviews.Targets;
        var scan = _audienceScanScratch ??= new();
        var next = 0;
        while (next < mine.Count)
        {
            var stretch = due[mine[next] >> 1].Position / RelationBatchSize;
            var first = next;
            scan.Clear();
            for (; next < mine.Count && due[mine[next] >> 1].Position / RelationBatchSize == stretch; next++)
            {
                var item = due[mine[next] >> 1];
                var image = Image(audience, NationKey((mine[next] & 1) == 0 ? item.B : item.A));
                scan.Add((image, Due(c.ElapsedDays, item.A.Id + item.B.Id, image?.LastReviewDay ?? -1, interval)));
            }
            // The image the scan found is the one the review settles, unless a review before it
            // in this stretch made an image or forgot one - the shared image again - after which
            // the rest of the stretch looks its own up afresh.
            var changed = false;
            for (var index = first; index < next; index++)
            {
                var (image, isDue) = scan[index - first];
                if (!isDue) continue;
                var slot = mine[index] >> 1;
                var forward = (mine[index] & 1) == 0;
                var key = NationKey(forward ? due[slot].B : due[slot].A);
                changed |= ApplyForeignReview(c, audience, key, changed ? Image(audience, key) : image,
                    forward ? targets[slot].TargetAB : targets[slot].TargetBA);
            }
        }
    }

    public static float Habit(int days, float years) => 1f - MathF.Exp(-Math.Max(0, days) / MathF.Max(365f, years * 365f));
    private static bool MatchesInstitutionIdentity(string remembered, Nation n)
    {
        if (string.IsNullOrEmpty(remembered)) return false;
        var gov = n.Government ?? "";
        var ideo = n.Ideology ?? "";
        var subj = n.SubjectType.ToString();
        var pos = 0;
        if (!remembered.AsSpan(pos).StartsWith(gov.AsSpan(), StringComparison.Ordinal)) return false;
        pos += gov.Length;
        if (pos >= remembered.Length || remembered[pos++] != ':') return false;
        if (!remembered.AsSpan(pos).StartsWith(ideo.AsSpan(), StringComparison.Ordinal)) return false;
        pos += ideo.Length;
        if (pos >= remembered.Length || remembered[pos++] != ':') return false;
        if (!remembered.AsSpan(pos).StartsWith(subj.AsSpan(), StringComparison.Ordinal)) return false;
        pos += subj.Length;
        if (pos >= remembered.Length || remembered[pos++] != ':') return false;
        return int.TryParse(remembered.AsSpan(pos), out var overlord) && overlord == n.OverlordId &&
               remembered.IndexOf(':', pos) < 0;
    }

    private static void ReviewInstitutions(Campaign c, Nation n, int days)
    {
        if (!MatchesInstitutionIdentity(n.RememberedInstitutions, n))
        { n.RememberedInstitutions = n.Government + ":" + n.Ideology + ":" + n.SubjectType + ":" + n.OverlordId; n.InstitutionAgeDays = 0; n.InstitutionalHabit = 0f; }
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
        var key = NationKey(subject);
        var image = Image(audience, key);
        if (!Due(c.ElapsedDays, audience.Id + subject.Id, image?.LastReviewDay ?? -1,
            Math.Max(1, c.Defines.SocialReviewDays))) return;
        // Never feed the opinion bonus back into its own target.
        var reasons = 0f;
        if (relation is not null) foreach (var reason in relation.Reasons) reasons += OpinionSystem.Weight(c, reason);
        var affinity = PoliticsCore.Affinity(c, audience.Id, subject.Id);
        var target = ComputeForeignTarget(c, audience, subject, relation, reasons, affinity);
        ApplyForeignReview(c, audience, key, image, target);
    }

    private static float ComputeForeignTarget(Campaign c, Nation audience, Nation subject,
        DiplomaticRelation relation, float reasons, float affinity)
    {
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
        return target;
    }

    /// <summary>Settle one image against its target. True when an image was made or a faded one forgotten.</summary>
    private static bool ApplyForeignReview(Campaign c, Nation audience, string key, PublicImage image, float target)
    {
        var made = false;
        if (image is null)
        {
            if (MathF.Abs(target) < 0.02f) return false;
            image = Image(audience, key, true);
            made = true;
        }
        image.Review(c.ElapsedDays, target, c.Defines);
        if (image.Status == 0 && MathF.Abs(image.Esteem) < 0.01f && MathF.Abs(image.Incident) < 0.01f &&
            MathF.Abs(target) < 0.02f && (image.LastIncidentDay < 0 || c.ElapsedDays - image.LastIncidentDay > 365))
        {
            audience.PublicImages.Remove(key);
            return true;
        }
        return made;
    }

    [ThreadStatic] private static List<string> _forgottenScratch;
    private static List<string> ForgottenScratch => _forgottenScratch ??= new();

    /// <summary>
    /// The images of countries that are gone, unreviewed for five years and never strong enough to
    /// become a stigma or an ideal, are forgotten: a campaign of centuries is not carried by every
    /// opinion of every rising that ever lived.
    /// </summary>
    private static void ForgetTheGone(Campaign c, Nation audience, Dictionary<string, Nation> subjects)
    {
        var forgotten = ForgottenScratch; forgotten.Clear();
        foreach (var (key, image) in audience.PublicImages)
        {
            if (!key.StartsWith("nation:", StringComparison.Ordinal) || image.Status != 0) continue;
            var hasSubject = subjects.TryGetValue(key, out var subject);
            var alive = hasSubject && (subject.Population > 0 || subject.Movement is { Dissolved: false });
            if (hasSubject && subject.RetiredDay >= 0)
            {
                forgotten.Add(key);
                continue;
            }
            if (!alive)
            {
                if (image.LastReviewDay < 0 || c.ElapsedDays - image.LastReviewDay >= 365)
                    forgotten.Add(key);
                continue;
            }
            // Prune completely inert faded images of uninvolved living nations so PublicImages
            // never balloons to 60k+ near-zero rows in multi-decade saves.
            if (MathF.Abs(image.Esteem) < 0.015f && MathF.Abs(image.Incident) < 0.01f &&
                (image.LastIncidentDay < 0 || c.ElapsedDays - image.LastIncidentDay > 365) &&
                (image.LastReviewDay < 0 || c.ElapsedDays - image.LastReviewDay > 180))
                forgotten.Add(key);
        }
        foreach (var key in forgotten) audience.PublicImages.Remove(key);
    }

    [ThreadStatic] private static Dictionary<string, long> _culturePopulationsScratch;
    [ThreadStatic] private static Dictionary<string, string> _cultureKeysScratch;
    [ThreadStatic] private static List<string> _cultureSubjectsScratch;
    [ThreadStatic] private static HashSet<string> _activeCoalitionsScratch;
    [ThreadStatic] private static List<string> _staleCoalitionsScratch;

    private static Dictionary<string, long> CulturePopulationsScratch => _culturePopulationsScratch ??= new(StringComparer.Ordinal);
    private static Dictionary<string, string> CultureKeysScratch => _cultureKeysScratch ??= new(StringComparer.Ordinal);
    private static List<string> CultureSubjectsScratch => _cultureSubjectsScratch ??= new();
    private static HashSet<string> ActiveCoalitionsScratch => _activeCoalitionsScratch ??= new(StringComparer.Ordinal);
    private static List<string> StaleCoalitionsScratch => _staleCoalitionsScratch ??= new();

    private static string CultureKey(string culture)
    {
        var cache = CultureKeysScratch;
        if (!cache.TryGetValue(culture, out var key))
            cache[culture] = key = "culture:" + culture;
        return key;
    }

    private static void ReviewCultures(Campaign c, Nation audience)
    {
        // Familiarity through living together; never transfer a government's crimes to a whole people.
        var populations = CulturePopulationsScratch; populations.Clear();
        var states = c.StatesOf(audience);
        for (var si = 0; si < states.Length; si++)
        {
            var groups = states[si].PopulationGroups;
            for (var gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                if (!string.IsNullOrWhiteSpace(group.Culture) && group.Size > 0)
                    CollectionsMarshal.GetValueRefOrAddDefault(populations, group.Culture, out _) += group.Size;
            }
        }
        var subjects = CultureSubjectsScratch; subjects.Clear();
        foreach (var key in populations.Keys) subjects.Add(key);
        foreach (var key in audience.PublicImages.Keys)
            if (key.StartsWith("culture:", StringComparison.Ordinal))
            {
                var culture = key[8..];
                if (!populations.ContainsKey(culture)) subjects.Add(culture);
            }
        subjects.Sort(StringComparer.Ordinal);
        var popScale = (float)Math.Max(1, audience.Population);
        for (var i = 0; i < subjects.Count; i++)
        {
            var culture = subjects[i];
            var contact = M.Clamp(populations.GetValueOrDefault(culture) / popScale, 0f, 1f);
            var shared = DemographyCatalog.IsPrimary(audience.Tag, culture) ? 0.15f : 0f;
            var key = CultureKey(culture);
            var image = Image(audience, key, true);
            image.Review(c.ElapsedDays,
                shared + contact * 0.3f + (contact > 0f ? (audience.Stability - 0.5f) * 0.15f : 0f), c.Defines);
            // A people it no longer lives with, once its image has faded to nothing, is forgotten
            // rather than reviewed every week for the rest of the campaign.
            if (contact <= 0f && shared <= 0f && image.Status == 0 && MathF.Abs(image.Esteem) < 0.02f && MathF.Abs(image.Incident) < 0.01f)
                audience.PublicImages.Remove(key);
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
            if (audience.Population <= 0 && audience.Movement is not { Dissolved: false } && !c.HasOwnedLand(audience))
                continue;
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
