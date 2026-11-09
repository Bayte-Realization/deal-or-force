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
    internal Dictionary<string, float> Modifiers;
}

public sealed partial class Nation
{
    public bool NationalCourseActive = true;
    public NationalCourseMemory NationalCourse = new();
}

public static class NationalCourses
{
    public static Dictionary<string, float> Pressures(Campaign c, Nation n)
    {
        var war = false; var exhaustion = 0f;
        foreach (var w in c.Wars)
            if (!w.Ended && w.Involves(n.Id))
            { war = true; exhaustion = MathF.Max(exhaustion, M.Clamp01((c.ElapsedDays - w.StartedDay) / 730f)); }
        var ideology = IdeologyCatalog.Of(n.Ideology);
        var openness = M.Clamp01(.5f - (ideology?.Authority ?? 0) * .5f);
        var autonomy = M.Clamp01(.35f + (ideology?.Authority ?? 0) * .4f + (n.Ideology == "nationalism" ? .35f : 0));
        float Law(string id) => c.FeatureEnabled("laws") && n.Laws.Values.Any(l => l.Id == id) ? 1 : 0;
        return new(StringComparer.Ordinal)
        {
            ["baseline"] = 1, ["war"] = war ? 1 : 0,
            ["exhaustion"] = MathF.Max(exhaustion, n.NationalCourse.WarExperience),
            ["hardship"] = M.Clamp01((.7f - n.StandardOfLiving) * 2.5f),
            ["fiscal_crisis"] = !c.FeatureEnabled("public_debt") ? 0 : n.DebtCondition >= DebtCondition.Default ? 1 :
                M.Clamp01(n.PublicDebt / MathF.Max(.01f, n.Gdp) - .6f),
            ["unrest"] = M.Clamp01((.75f - n.Stability) * 2),
            ["institutions"] = M.Clamp01(n.Stability * .75f + n.InstitutionalHabit * .25f),
            ["openness"] = M.Clamp01(openness + Law("industry_open") * .2f - Law("migration_closed") * .2f),
            ["autonomy"] = autonomy,
            ["reform"] = M.Clamp01(openness * .4f + Law("rights_association") * .25f + Law("media_plural") * .25f),
            ["shock"] = n.NationalCourse.Shock
        };
    }

    public static Dictionary<string, float> Targets(Campaign c, Nation n)
    {
        var pressures = Pressures(c, n);
        var scores = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var d in NationalCourseCatalog.All)
            scores[d.Id] = MathF.Max(.02f, d.Pressures.Sum(p => p.Value * pressures.GetValueOrDefault(p.Key)));
        var sum = scores.Values.Sum();
        if (sum > 0) foreach (var key in scores.Keys.ToArray()) scores[key] /= sum;
        return scores;
    }

    public static void Initialize(Campaign c, Nation n)
    {
        var m = n.NationalCourse;
        if (m.Initialized) return;
        m.Convictions = Targets(c, n);
        m.Dominant = m.Convictions.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault().Key ?? "";
        m.RememberedPolitics = n.Government + ":" + n.Ideology;
        m.RememberedSecessionDay = n.LastSecessionDay;
        m.PreviousLiving = n.StandardOfLiving;
        m.LastReviewDay = c.ElapsedDays;
        m.Initialized = true;
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
        foreach (var n in c.Nations)
        {
            var m = n.NationalCourse;
            n.NationalCourseActive = coursesEnabled;
            if (!n.NationalCourseActive || c.Fallen.Contains(n.Id))
            { m.LastReviewDay = c.ElapsedDays; continue; }
            if (!m.Initialized) { Initialize(c, n); continue; }
            if (c.ElapsedDays - m.LastReviewDay < reviewDays) continue;
            var days = Math.Min(90, c.ElapsedDays - m.LastReviewDay);
            m.LastReviewDay = c.ElapsedDays;
            var politics = n.Government + ":" + n.Ideology;
            if (politics != m.RememberedPolitics)
            { Shock(c, n, .65f, "course.shock.institutional_change"); m.RememberedPolitics = politics; }
            if (n.LastSecessionDay > m.RememberedSecessionDay)
            { Shock(c, n, .4f, "course.shock.territorial_loss"); m.RememberedSecessionDay = n.LastSecessionDay; }
            if (m.PreviousLiving - n.StandardOfLiving >= .08f) Shock(c, n, .4f, "course.shock.living_collapse");
            m.PreviousLiving = n.StandardOfLiving;
            var fighting = c.Wars.Any(w => !w.Ended && w.Involves(n.Id));
            // Peace does not erase yesterday's years of exhaustion.
            m.WarExperience = PublicImage.Approach(m.WarExperience, fighting ? 1 : 0, days, fighting ? 730 : 1095);
            var target = Targets(c, n);
            if (target.Count == 0) continue;
            foreach (var removed in m.Convictions.Keys.Where(k => !target.ContainsKey(k)).ToArray()) m.Convictions.Remove(removed);
            var response = MathF.Max(28, c.Defines.CourseResponseDays) * (1 + m.Entrenchment);
            var shockResponse = Math.Clamp(c.Defines.CourseShockResponseDays, 28, response);
            response += (shockResponse - response) * m.Shock;
            foreach (var p in target)
                m.Convictions[p.Key] = PublicImage.Approach(m.Convictions.GetValueOrDefault(p.Key), p.Value, days, response);
            var total = m.Convictions.Values.Sum();
            if (total > 0) foreach (var id in m.Convictions.Keys.ToArray()) m.Convictions[id] /= total;
            m.AgeDays = Math.Min(36500, m.AgeDays + days);
            m.Entrenchment = PublicImage.Approach(m.Entrenchment, 1 - m.Shock, days, MathF.Max(28, c.Defines.CourseEntrenchmentDays));
            var strongest = m.Convictions.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
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
            m.Modifiers = null; NationalModifiers.Invalidate(n);
        }
    }

    public static void AddTo(Dictionary<string, float> totals, Nation n)
    {
        var m = n.NationalCourse;
        if (!n.NationalCourseActive || !m.Initialized) return;
        if (m.Modifiers is null || m.ModifierRevision != NationalCourseCatalog.Revision)
        {
            m.Modifiers = new(StringComparer.Ordinal); m.ModifierRevision = NationalCourseCatalog.Revision;
            foreach (var p in m.Convictions)
                if (NationalCourseCatalog.Find(p.Key) is { } d)
                    foreach (var effect in d.Modifiers)
                        m.Modifiers[effect.Key] = m.Modifiers.GetValueOrDefault(effect.Key) + effect.Value * p.Value;
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
