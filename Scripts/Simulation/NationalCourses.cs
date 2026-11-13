using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int CourseReviewDays { get; set; } = 7;
    public float CourseResponseDays { get; set; } = 730;
    public float CourseShockResponseDays { get; set; } = 120;
    public float CourseShockDecayDays { get; set; } = 180;
    public int CourseMinimumTenureDays { get; set; } = 365;
    public int CourseChallengeDays { get; set; } = 180;
    public int CourseShockChallengeDays { get; set; } = 60;
    public float CourseEntrenchmentDays { get; set; } = 1460;
}

public sealed class NationalCourseMemory
{
    public bool Initialized;
    public Dictionary<string, float> Convictions = new(StringComparer.Ordinal);
    public string Dominant = "";
    public string Challenger = "";
    public int ChallengerDays;
    public int AgeDays;
    public int LastReviewDay = -1;
    public float Entrenchment = .35f;
    public float Shock;
    public string ShockReason = "";
    public string RememberedPolitics = "";
    public int RememberedSecessionDay = -9999;
    public float PreviousLiving = -1;
    public float WarExperience;
    internal int ModifierRevision = -1;
    internal bool ModifiersDirty = true;
    internal Dictionary<string, float> Modifiers;
}

public sealed partial class Nation
{
    public bool NationalCourseActive = true;
    public NationalCourseMemory NationalCourse = new();
}

public static class NationalCourses
{
    private static readonly Dictionary<string, float> ScratchPressures = new(16, StringComparer.Ordinal);
    private static readonly Dictionary<string, float> ScratchTargets = new(16, StringComparer.Ordinal);
    private static readonly List<string> ScratchKeys = new();

    public static Dictionary<string, float> Pressures(Campaign c, Nation n)
    {
        var result = new Dictionary<string, float>(16, StringComparer.Ordinal);
        FillPressures(result, c, n);
        return result;
    }

    private static void FillPressures(Dictionary<string, float> result, Campaign c, Nation n)
    {
        result.Clear();
        var war = false; var exhaustion = 0f;
        for (var i = 0; i < c.Wars.Count; i++)
        {
            var w = c.Wars[i];
            if (!w.Ended && w.Involves(n.Id))
            { war = true; exhaustion = MathF.Max(exhaustion, M.Clamp01((c.ElapsedDays - w.StartedDay) / 730f)); }
        }
        var ideology = IdeologyCatalog.Of(n.Ideology);
        var openness = M.Clamp01(.5f - (ideology?.Authority ?? 0) * .5f);
        var autonomy = M.Clamp01(.35f + (ideology?.Authority ?? 0) * .4f + (n.Ideology == "nationalism" ? .35f : 0));
        result["baseline"] = 1;
        result["war"] = war ? 1 : 0;
        result["exhaustion"] = MathF.Max(exhaustion, n.NationalCourse.WarExperience);
        result["hardship"] = M.Clamp01((.7f - n.StandardOfLiving) * 2.5f);
        result["fiscal_crisis"] = !c.FeatureEnabled("public_debt") ? 0 : n.DebtCondition >= DebtCondition.Default ? 1 :
            M.Clamp01(n.PublicDebt / MathF.Max(.01f, n.Gdp) - .6f);
        result["unrest"] = M.Clamp01((.75f - n.Stability) * 2);
        result["institutions"] = M.Clamp01(n.Stability * .75f + n.InstitutionalHabit * .25f);
        result["openness"] = M.Clamp01(openness + HasLaw(c, n, "industry_open") * .2f - HasLaw(c, n, "migration_closed") * .2f);
        result["autonomy"] = autonomy;
        result["reform"] = M.Clamp01(openness * .4f + HasLaw(c, n, "rights_association") * .25f + HasLaw(c, n, "media_plural") * .25f);
        result["shock"] = n.NationalCourse.Shock;
    }

    private static float HasLaw(Campaign c, Nation n, string id)
    {
        if (!c.FeatureEnabled("laws")) return 0f;
        foreach (var l in n.Laws.Values)
            if (l.Id == id) return 1f;
        return 0f;
    }

    public static Dictionary<string, float> Targets(Campaign c, Nation n)
    {
        var pressures = Pressures(c, n);
        var scores = new Dictionary<string, float>(StringComparer.Ordinal);
        FillTargets(scores, pressures);
        return scores;
    }

    private static void FillTargets(Dictionary<string, float> scores, Dictionary<string, float> pressures)
    {
        scores.Clear();
        var sum = 0f;
        foreach (var d in NationalCourseCatalog.All)
        {
            var val = 0f;
            foreach (var p in d.Pressures)
                val += p.Value * pressures.GetValueOrDefault(p.Key);
            val = MathF.Max(.02f, val);
            scores[d.Id] = val;
            sum += val;
        }
        if (sum > 0)
        {
            ScratchKeys.Clear();
            foreach (var key in scores.Keys) ScratchKeys.Add(key);
            for (var i = 0; i < ScratchKeys.Count; i++)
            {
                var key = ScratchKeys[i];
                scores[key] /= sum;
            }
        }
    }

    private static bool MatchesPolitics(string remembered, string gov, string ideology)
    {
        if (remembered is null || gov is null || ideology is null) return false;
        return remembered.Length == gov.Length + 1 + ideology.Length &&
               remembered.StartsWith(gov, StringComparison.Ordinal) &&
               remembered[gov.Length] == ':' &&
               remembered.EndsWith(ideology, StringComparison.Ordinal);
    }

    public static void Initialize(Campaign c, Nation n)
    {
        var m = n.NationalCourse;
        if (m.Initialized) return;
        m.Convictions = Targets(c, n);
        var bestKey = "";
        var bestVal = float.NegativeInfinity;
        foreach (var pair in m.Convictions)
        {
            if (pair.Value > bestVal || (pair.Value == bestVal && string.CompareOrdinal(pair.Key, bestKey) < 0))
            {
                bestVal = pair.Value;
                bestKey = pair.Key;
            }
        }
        m.Dominant = bestKey;
        m.RememberedPolitics = n.Government + ":" + n.Ideology;
        m.RememberedSecessionDay = n.LastSecessionDay;
        m.PreviousLiving = n.StandardOfLiving;
        m.LastReviewDay = c.ElapsedDays;
        m.Initialized = true;
        m.ModifiersDirty = true;
        NationalModifiers.Invalidate(n);
    }

    public static void Shock(Campaign c, Nation n, float strength, string reason)
    {
        if (n is null || !c.FeatureEnabled("national_courses") || !float.IsFinite(strength) || strength <= 0) return;
        n.NationalCourse.Shock = M.Clamp01(n.NationalCourse.Shock + strength);
        n.NationalCourse.ShockReason = reason;
    }

    public static void Witness(Campaign c, string kind, Nation actor, Nation target)
    {
        var strength = kind switch { "war_started" => .35f, "sovereign_default" => .7f,
            "sovereign_insolvency" => .8f, "peace_signed" => .3f, "peaceful_independence" => .25f, _ => 0 };
        if (strength == 0) return;
        Shock(c, actor, strength, "course.shock." + kind);
        if (target?.Id != actor?.Id) Shock(c, target, strength, "course.shock." + kind);
    }

    public static void Tick(Campaign c)
    {
        var coursesEnabled = c.FeatureEnabled("national_courses");
        var reviewDays = Math.Clamp(c.Defines.CourseReviewDays, 1, 90);
        for (var ni = 0; ni < c.Nations.Count; ni++)
        {
            var n = c.Nations[ni];
            var m = n.NationalCourse;
            n.NationalCourseActive = coursesEnabled;
            if (!n.NationalCourseActive || c.Fallen.Contains(n.Id))
            { m.LastReviewDay = c.ElapsedDays; continue; }
            if (!m.Initialized) { Initialize(c, n); continue; }
            if (c.ElapsedDays - m.LastReviewDay < reviewDays) continue;
            var days = Math.Min(90, c.ElapsedDays - m.LastReviewDay);
            m.LastReviewDay = c.ElapsedDays;
            if (!MatchesPolitics(m.RememberedPolitics, n.Government, n.Ideology))
            { Shock(c, n, .65f, "course.shock.institutional_change"); m.RememberedPolitics = n.Government + ":" + n.Ideology; }
            if (n.LastSecessionDay > m.RememberedSecessionDay)
            { Shock(c, n, .4f, "course.shock.territorial_loss"); m.RememberedSecessionDay = n.LastSecessionDay; }
            if (m.PreviousLiving - n.StandardOfLiving >= .08f) Shock(c, n, .4f, "course.shock.living_collapse");
            m.PreviousLiving = n.StandardOfLiving;
            var fighting = false;
            for (var wi = 0; wi < c.Wars.Count; wi++)
            {
                var w = c.Wars[wi];
                if (!w.Ended && w.Involves(n.Id)) { fighting = true; break; }
            }
            // Peace does not erase yesterday's years of exhaustion.
            m.WarExperience = PublicImage.Approach(m.WarExperience, fighting ? 1 : 0, days, fighting ? 730 : 1095);
            FillPressures(ScratchPressures, c, n);
            FillTargets(ScratchTargets, ScratchPressures);
            if (ScratchTargets.Count == 0) continue;
            ScratchKeys.Clear();
            foreach (var k in m.Convictions.Keys)
                if (!ScratchTargets.ContainsKey(k)) ScratchKeys.Add(k);
            for (var ki = 0; ki < ScratchKeys.Count; ki++) m.Convictions.Remove(ScratchKeys[ki]);
            var response = MathF.Max(28, c.Defines.CourseResponseDays) * (1 + m.Entrenchment);
            var shockResponse = Math.Clamp(c.Defines.CourseShockResponseDays, 28, response);
            response += (shockResponse - response) * m.Shock;
            foreach (var p in ScratchTargets)
                m.Convictions[p.Key] = PublicImage.Approach(m.Convictions.GetValueOrDefault(p.Key), p.Value, days, response);
            var total = 0f;
            foreach (var v in m.Convictions.Values) total += v;
            if (total > 0)
            {
                ScratchKeys.Clear();
                foreach (var k in m.Convictions.Keys) ScratchKeys.Add(k);
                for (var ki = 0; ki < ScratchKeys.Count; ki++)
                {
                    var k = ScratchKeys[ki];
                    m.Convictions[k] /= total;
                }
            }
            m.AgeDays = Math.Min(36500, m.AgeDays + days);
            m.Entrenchment = PublicImage.Approach(m.Entrenchment, 1 - m.Shock, days, MathF.Max(28, c.Defines.CourseEntrenchmentDays));
            var strongest = "";
            var strongestVal = float.MinValue;
            foreach (var p in m.Convictions)
            {
                if (p.Value > strongestVal || (p.Value == strongestVal && string.CompareOrdinal(p.Key, strongest) < 0))
                {
                    strongestVal = p.Value;
                    strongest = p.Key;
                }
            }
            var lead = m.Convictions[strongest] - m.Convictions.GetValueOrDefault(m.Dominant);
            if (strongest == m.Dominant || lead < .04f) { m.Challenger = ""; m.ChallengerDays = 0; }
            else
            {
                if (m.Challenger != strongest) { m.Challenger = strongest; m.ChallengerDays = 0; }
                m.ChallengerDays += days;
                var severe = m.Shock > .3f;
                var required = Math.Max(28, severe ? c.Defines.CourseShockChallengeDays : c.Defines.CourseChallengeDays);
                var tenure = severe ? required : Math.Max(required, c.Defines.CourseMinimumTenureDays);
                if (m.ChallengerDays >= required && m.AgeDays >= tenure)
                {
                    m.Dominant = strongest; m.Challenger = ""; m.ChallengerDays = 0;
                    m.AgeDays = 0; m.Entrenchment *= .5f;
                    c.Chronicle.Record(c, "national_course_changed", n.Id, subject: NationalCourseCatalog.Find(strongest)?.Name ?? strongest);
                    c.MarkStrategicChange();
                }
            }
            m.Shock *= MathF.Exp(-days / MathF.Max(28, c.Defines.CourseShockDecayDays));
            m.ModifiersDirty = true; NationalModifiers.Invalidate(n);
        }
    }

    public static void AddTo(Dictionary<string, float> totals, Nation n)
    {
        var m = n.NationalCourse;
        if (!n.NationalCourseActive || !m.Initialized) return;
        if (m.Modifiers is null || m.ModifiersDirty || m.ModifierRevision != NationalCourseCatalog.Revision)
        {
            var dict = m.Modifiers ?? new Dictionary<string, float>(StringComparer.Ordinal);
            dict.Clear();
            foreach (var p in m.Convictions)
                if (NationalCourseCatalog.Find(p.Key) is { } d)
                    foreach (var effect in d.Modifiers)
                        dict[effect.Key] = dict.GetValueOrDefault(effect.Key) + effect.Value * p.Value;
            m.ModifierRevision = NationalCourseCatalog.Revision;
            m.ModifiersDirty = false;
            m.Modifiers = dict;
        }
        if (m.Modifiers is not null)
            foreach (var (key, value) in m.Modifiers)
                totals[key] = totals.GetValueOrDefault(key) + value;
    }

    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation n)
    {
        var m = n.NationalCourse;
        if (!n.NationalCourseActive || !m.Initialized) yield break;
        if (m.Modifiers is null || m.ModifierRevision != NationalCourseCatalog.Revision)
        {
            m.Modifiers = new(StringComparer.Ordinal); m.ModifierRevision = NationalCourseCatalog.Revision;
            foreach (var p in m.Convictions)
                if (NationalCourseCatalog.Find(p.Key) is { } d)
                    foreach (var effect in d.Modifiers)
                        m.Modifiers[effect.Key] = m.Modifiers.GetValueOrDefault(effect.Key) + effect.Value * p.Value;
        }
        yield return (NationalCourseCatalog.Find(m.Dominant)?.Name ?? "National agenda", m.Modifiers);
    }
}
