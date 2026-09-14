using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>One kind of dependency, as `common/subjects` describes it.</summary>
public sealed class SubjectType
{
    public string Id = "";
    public string Name = "";
    public string Description = "";

    /// <summary>Share of the subject's weekly tax that goes to the overlord.</summary>
    public float Tribute;

    /// <summary>Share of the subject's manpower growth that goes with it.</summary>
    public float Manpower;

    public bool JoinsWars = true;
    public bool DraggedIntoWars;
    public bool MayDeclareWar;

    /// <summary>What it wants before circumstances are counted. See <see cref="SubjectSystem"/>.</summary>
    public float LibertyDesire;

    /// <summary>How long it is given before it begins wanting out at all.</summary>
    public int GraceDays = 365;
}

/// <summary>The numbers that decide how a dependency ages, all of them from the same file.</summary>
public sealed class SubjectWeights
{
    public float SizePull = 0.55f;
    public float OverlordInstabilityPull = 0.40f;
    public float OverlordAtWarPull = 0.20f;
    public float OverlordStrengthHold = 0.35f;
    public float BreakAt = 1.0f;
    public float DriftPerWeek = 0.02f;
}

/// <summary>
/// What kinds of dependency exist, read from `common/subjects`.
///
/// Nothing in the code names a kind. A mod that wants a Commonwealth realm, a tributary or
/// an occupation zone writes a row and it exists; the only thing the simulation knows is
/// the shape of a row.
/// </summary>
public static class SubjectCatalog
{
    private static readonly Dictionary<string, SubjectType> Types = new(StringComparer.OrdinalIgnoreCase);

    public static SubjectWeights Weights { get; private set; } = new();
    public static IReadOnlyCollection<SubjectType> All => Types.Values;

    static SubjectCatalog() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Types.Clear();
        Weights = new SubjectWeights();
        foreach (var document in GameDataLoader.DataDocuments("common", "subjects"))
            using (document)
                Merge(document.RootElement, errors);
    }

    public static SubjectType Of(string id) =>
        id is not null && Types.TryGetValue(id, out var type) ? type : null;

    public static bool Knows(string id) => id is not null && Types.ContainsKey(id);

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal) ||
                entry.Value.ValueKind != JsonValueKind.Object) continue;

            if (string.Equals(entry.Name, "weights", StringComparison.OrdinalIgnoreCase))
            {
                var weights = Weights;
                Read(entry.Value, "size_pull", value => weights.SizePull = value);
                Read(entry.Value, "overlord_instability_pull", value => weights.OverlordInstabilityPull = value);
                Read(entry.Value, "overlord_at_war_pull", value => weights.OverlordAtWarPull = value);
                Read(entry.Value, "overlord_strength_hold", value => weights.OverlordStrengthHold = value);
                Read(entry.Value, "break_at", value => weights.BreakAt = value);
                Read(entry.Value, "drift_per_week", value => weights.DriftPerWeek = value);
                continue;
            }

            if (!Types.TryGetValue(entry.Name, out var type))
                Types[entry.Name] = type = new SubjectType { Id = entry.Name, Name = Titled(entry.Name) };

            var value2 = entry.Value;
            if (value2.TryGetProperty("name", out var name)) type.Name = name.GetString() ?? type.Name;
            if (value2.TryGetProperty("description", out var about))
                type.Description = about.GetString() ?? type.Description;
            Read(value2, "tribute", v => type.Tribute = M.Clamp01(v));
            Read(value2, "manpower", v => type.Manpower = M.Clamp01(v));
            Read(value2, "liberty_desire", v => type.LibertyDesire = M.Clamp01(v));
            if (value2.TryGetProperty("grace_days", out var grace) && grace.TryGetInt32(out var days))
                type.GraceDays = Math.Max(0, days);
            Flag(value2, "joins_wars", v => type.JoinsWars = v);
            Flag(value2, "dragged_into_wars", v => type.DraggedIntoWars = v);
            Flag(value2, "may_declare_war", v => type.MayDeclareWar = v);

            if (type.Tribute > 0.9f)
                errors?.Add($"subjects/{entry.Name}: a tribute of {type.Tribute:P0} leaves the subject nothing.");
        }
    }

    private static void Read(JsonElement element, string key, Action<float> set)
    {
        if (element.TryGetProperty(key, out var value) && value.TryGetSingle(out var number)) set(number);
    }

    private static void Flag(JsonElement element, string key, Action<bool> set)
    {
        if (element.TryGetProperty(key, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            set(value.GetBoolean());
    }

    private static string Titled(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" :
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
}

/// <summary>
/// The week in the life of a country that answers to another one.
///
/// A dependency is a bargain about power, and the whole of this is the observation that the
/// bargain is renegotiated the moment the power changes. A subject pays its tribute and
/// turns up to its overlord's wars while the arrangement holds; what decides whether it
/// holds is worked out weekly from things that move - how big the subject has grown against
/// its overlord, whether the overlord can govern itself, whether the overlord is winning.
///
/// So nothing here is a timer. A satellite of a stable, enormous overlord can sit for a
/// century; the same satellite of an overlord that has just lost half a war and a third of
/// its stability will be gone inside two years, and it will have been visible coming.
/// </summary>
public static class SubjectSystem
{
    /// <summary>Every country that answers to this one.</summary>
    public static List<Nation> SubjectsOf(Campaign campaign, Nation overlord)
    {
        var found = new List<Nation>();
        if (campaign is null || overlord is null) return found;
        foreach (var nation in campaign.Nations)
            if (nation.OverlordId == overlord.Id) found.Add(nation);
        return found;
    }

    public static Nation OverlordOf(Campaign campaign, Nation subject) =>
        subject is null || subject.OverlordId == 0 ? null : campaign?.NationById(subject.OverlordId);

    /// <summary>
    /// Make one country answer to another.
    ///
    /// Refuses the arrangements that are not arrangements: a country cannot be its own
    /// subject, a subject cannot be handed to its own subject, and a country already
    /// answering to somebody has to be released first. The last one is deliberate - a
    /// dependency changing hands is a thing that happens, and it should happen as two
    /// decisions rather than one silent one.
    /// </summary>
    public static bool Subjugate(Campaign campaign, Nation overlord, Nation subject, string typeId,
        out string refusal)
    {
        refusal = "";
        if (campaign is null || overlord is null || subject is null) { refusal = "no such country"; return false; }
        if (overlord.Id == subject.Id) { refusal = "a country cannot answer to itself"; return false; }
        if (!SubjectCatalog.Knows(typeId))
        {
            refusal = $"no such kind of dependency as '{typeId}'";
            return false;
        }
        if (subject.OverlordId != 0)
        {
            refusal = $"{subject.Tag} already answers to " +
                      (campaign.NationById(subject.OverlordId)?.Tag ?? "somebody");
            return false;
        }
        if (overlord.OverlordId == subject.Id)
        {
            refusal = $"{overlord.Tag} already answers to {subject.Tag}";
            return false;
        }

        subject.OverlordId = overlord.Id;
        subject.SubjectType = typeId;
        subject.SubjectSinceDay = campaign.ElapsedDays;
        subject.LibertyDesire = 0f;

        // A war between the two is over by definition; one of them has just won it.
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(overlord.Id) && war.Involves(subject.Id))
            {
                war.Ended = true;
                war.EndedDay = campaign.ElapsedDays;
                war.WinnerId = overlord.Id;
                war.PeaceSummary = $"{subject.Name} became a {SubjectCatalog.Of(typeId)?.Name ?? typeId}.";
            }

        OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, subject.Id, overlord.Id),
            "made_us_a_subject");
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"SUBJECT overlord={overlord.Tag} subject={subject.Tag} kind={typeId} day={campaign.ElapsedDays}");
        return true;
    }

    /// <summary>Let one go. `broke` records whether it left rather than being released.</summary>
    public static void Release(Campaign campaign, Nation subject, bool broke = false)
    {
        if (campaign is null || subject is null || subject.OverlordId == 0) return;
        var overlord = campaign.NationById(subject.OverlordId);
        subject.OverlordId = 0;
        subject.SubjectType = "";
        subject.SubjectSinceDay = -9999;
        subject.LibertyDesire = 0f;

        if (overlord is not null)
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, subject.Id, overlord.Id),
                broke ? "broke_away_from_us" : "let_us_go");
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"SUBJECT_END subject={subject.Tag} overlord={overlord?.Tag} broke={broke} day={campaign.ElapsedDays}");
    }

    /// <summary>
    /// What a subject wants this week, on a scale where one is "gone".
    ///
    /// Public because the interface has to be able to show it. A dependency the player
    /// cannot watch decaying is one that ends as a surprise, and a surprise is the one
    /// thing a mechanic about power should never be.
    /// </summary>
    public static float LibertyDesireOf(Campaign campaign, Nation subject)
    {
        var type = SubjectCatalog.Of(subject?.SubjectType);
        var overlord = OverlordOf(campaign, subject);
        if (type is null || overlord is null) return 0f;

        var weights = SubjectCatalog.Weights;
        var want = type.LibertyDesire;

        // How big it has grown against the country holding it.
        var theirs = Math.Max(1L, overlord.Population);
        want += M.Clamp01(subject.Population / (float)theirs) * weights.SizePull;

        // Whether that country can govern itself.
        want += (1f - M.Clamp01(overlord.Stability)) * weights.OverlordInstabilityPull;

        // And whether it is busy.
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(overlord.Id)) { want += weights.OverlordAtWarPull; break; }

        // Against all of which: being small and held by something enormous is an argument.
        var ratio = subject.Population <= 0 ? 1f : M.Clamp01(subject.Population / (float)theirs);
        want -= (1f - ratio) * weights.OverlordStrengthHold;

        return Math.Max(0f, want);
    }

    /// <summary>The weekly pass: tribute, manpower, and the slow drift towards leaving.</summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign is null) return;

        // Collected before anything is changed, because a subject that breaks free inside
        // the loop would otherwise be walked over while the list it is in is being edited.
        var leaving = new List<Nation>();

        foreach (var nation in campaign.Nations)
        {
            nation.TributePaidWeekly = 0f;
            nation.TributeTakenWeekly = 0f;
        }

        foreach (var subject in campaign.Nations)
        {
            if (subject.OverlordId == 0) continue;
            var type = SubjectCatalog.Of(subject.SubjectType);
            var overlord = campaign.NationById(subject.OverlordId);
            if (type is null || overlord is null)
            {
                // The overlord is gone, or the kind was removed by a mod between saves.
                // Either way this country answers to nobody now.
                leaving.Add(subject);
                continue;
            }

            // Tribute is taken from what the subject actually raised, not from what it has.
            // A dependency that is not earning does not pay, which is what makes holding a
            // ruined country a cost rather than a windfall.
            var tribute = Math.Max(0f, subject.WeeklyTaxIncome) * type.Tribute;
            if (tribute > 0f)
            {
                subject.Treasury -= tribute;
                overlord.Treasury += tribute;
                subject.TributePaidWeekly += tribute;
                overlord.TributeTakenWeekly += tribute;
            }

            var levy = Math.Max(0f, subject.ManpowerReserve) * type.Manpower * 0.01f;
            if (levy > 0f)
            {
                subject.ManpowerReserve -= levy;
                overlord.ManpowerReserve += levy;
            }

            // And what it thinks about all that.
            if (campaign.ElapsedDays - subject.SubjectSinceDay < type.GraceDays) continue;
            var want = LibertyDesireOf(campaign, subject);
            var weights = SubjectCatalog.Weights;
            subject.LibertyDesire = want > subject.LibertyDesire
                ? Math.Min(want, subject.LibertyDesire + weights.DriftPerWeek)
                : Math.Max(want, subject.LibertyDesire - weights.DriftPerWeek);

            if (subject.LibertyDesire >= weights.BreakAt) leaving.Add(subject);
        }

        foreach (var subject in leaving) Release(campaign, subject, broke: true);
    }
}
