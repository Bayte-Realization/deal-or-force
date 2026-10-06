using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class NationalCourseTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS national course: " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(805); var n = c.NationByTag("ROU");
        n.Stability = .9f; n.StandardOfLiving = .85f; n.PublicDebt = 0;
        NationalCourses.Initialize(c, n); var m = n.NationalCourse;
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        Check(NationalCourseCatalog.All.Count() == 8, "eight mixed societal priorities");
        Check(MathF.Abs(m.Convictions.Values.Sum() - 1) < .00001f && m.Convictions.Values.All(v => v > 0), "initial convictions are a mixture, not a binary national stereotype");
        Check(m.Dominant == "institutional_continuity", "settled prosperous institutions initially favour continuity");
        var initial = new Dictionary<string, float>(m.Convictions);
        n.DebtCondition = DebtCondition.Default; n.PublicDebt = n.Gdp * 2;
        c.Chronicle.Record(c, "sovereign_default", n.Id);
        Check(m.Shock > .6f && m.Convictions.SequenceEqual(initial), "a significant shock does not instantly replace convictions");
        Day(7); NationalCourses.Tick(c);
        Check(m.Dominant == "institutional_continuity" && m.Convictions["fiscal_rehabilitation"] > initial["fiscal_rehabilitation"], "pressure moves convictions gradually without a flashing label");
        var snapshot = CampaignFingerprint.Of(c); NationalCourses.Tick(c);
        Check(CampaignFingerprint.Of(c) == snapshot, "repeated same-day review is idempotent");
        Day(28); NationalCourses.Tick(c);
        Check(m.Dominant == "institutional_continuity", "one bad month does not erase the country's character");
        for (var day = 35; day <= 1463; day += 7) { Day(day); NationalCourses.Tick(c); }
        Check(m.Dominant == "fiscal_rehabilitation" && c.Chronicle.Entries.Any(e => e.Kind == "national_course_changed" && e.ActorId == n.Id),
            "years of sustained fiscal crisis produce an explained organic turn");
        var fiscal = m.Convictions["fiscal_rehabilitation"];
        n.DebtCondition = DebtCondition.Current; n.PublicDebt = 0;
        Day(1470); NationalCourses.Tick(c);
        Check(m.Dominant == "fiscal_rehabilitation" && MathF.Abs(m.Convictions["fiscal_rehabilitation"] - fiscal) < .01f,
            "one good week cannot immediately undo the accumulated national experience");
        c.SetFeature("NATIONAL_COURSES", false); var stopped = new Dictionary<string, float>(m.Convictions); var age = m.AgeDays;
        Day(2200); NationalCourses.Tick(c);
        Check(m.Convictions.SequenceEqual(stopped) && m.AgeDays == age && !NationalCourses.Sources(n).Any(), "disabled course freezes experience and removes effects");
        c.SetFeature("national_courses", true); NationalCourses.Tick(c);
        Check(m.Convictions.SequenceEqual(stopped), "reactivation does not silently simulate disabled years");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var saved = JsonSerializer.Deserialize<NationalCourseMemory>(JsonSerializer.Serialize(m, options), options);
        Check(saved.Dominant == m.Dominant && saved.Convictions.SequenceEqual(m.Convictions) && saved.LastReviewDay == m.LastReviewDay,
            "convictions, inertia and review history survive save roundtrip");
        snapshot = CampaignFingerprint.Of(c); m.ChallengerDays++;
        Check(CampaignFingerprint.Of(c) != snapshot, "network fingerprint covers gradual course transitions");
        var errors = new List<string>(); using var bad = JsonDocument.Parse("{\"national_security\":{\"pressures\":{\"typo\":1}}}");
        var old = NationalCourseCatalog.Find("national_security"); NationalCourseCatalog.Merge(bad.RootElement, errors);
        Check(errors.Count == 1 && ReferenceEquals(old, NationalCourseCatalog.Find("national_security")), "invalid mod pressure rejected atomically");
        var effects = NationalCourses.Sources(n).Single().Modifiers;
        Check(effects["borrowing_cost"] < 0 && effects.ContainsKey("trade_reach"), "country character has mixed real economic and civic consequences");
        n.Government = "federation"; n.Ideology = "liberalism";
        Day(2207); NationalCourses.Tick(c);
        Check(m.Shock >= .5f && m.ShockReason == "course.shock.institutional_change", "institutional upheaval is detected without inventing a revolution");
        NationalCourseCatalog.ResetCatalog();
    }
}
