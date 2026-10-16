using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class StrategicGoalTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS goals: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(805);
        var n = c.NationByTag("ROU");
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        c.Defines.MaxActiveGoals = 2;

        var have = string.Join(" ", GoalCatalog.All.Select(goal => goal.Id).OrderBy(id => id));
        Check(GoalCatalog.All.Count() == 5, $"five shipped personal goals (got {GoalCatalog.All.Count()}: {have})");
        Check(GoalCatalog.All.All(goal => goal.Milestones.Count > 0),
            "every shipped goal has measurable milestones");

        var errors = new List<string>();
        using (var doc = JsonDocument.Parse(
            "{\"test_goal\":{\"name\":\"Test\",\"description\":\"d\",\"milestones\":[" +
            "{\"text\":\"always\",\"trigger\":{\"min_stability\":0.0}}]}}"))
            GoalCatalog.Merge(doc.RootElement, errors);
        Check(errors.Count == 0 && GoalCatalog.Find("test_goal") is not null, "mod goal merged beside the shipped five");

        var plain = CampaignFingerprint.Of(c);
        new AdoptGoalCommand { IssuerId = n.Id, GoalId = "test_goal" }.Apply(c);
        Check(n.Goals.Count == 1 && n.Goals[0].CompletedDay < 0, "a pinned goal is open");
        Check(CampaignFingerprint.Of(c) != plain, "the network fingerprint covers pinned goals");

        new AdoptGoalCommand { IssuerId = n.Id, GoalId = "test_goal" }.Apply(c);
        Check(n.Goals.Count == 1, "the same goal cannot be pinned twice");
        var other = GoalCatalog.All.First(goal => goal.Id != "test_goal").Id;
        new AdoptGoalCommand { IssuerId = n.Id, GoalId = other }.Apply(c);
        new AdoptGoalCommand { IssuerId = n.Id, GoalId = GoalCatalog.All.Skip(2).First().Id }.Apply(c);
        Check(n.Goals.Count == 2, "the concurrency limit refuses a third");
        new AdoptGoalCommand { IssuerId = n.Id, GoalId = "no_such_goal" }.Apply(c);
        Check(n.Goals.Count == 2, "an unknown goal id is a quiet no-op");

        // The always-true milestone completes the goal the same day the tick first runs.
        Day(5); StrategicGoals.Tick(c);
        Check(n.Goals.First(goal => goal.Id == "test_goal").CompletedDay == c.ElapsedDays,
            "a goal whose milestones all stand is completed on the tick");
        var snapshot = CampaignFingerprint.Of(c);
        StrategicGoals.Tick(c);
        Check(CampaignFingerprint.Of(c) == snapshot, "the same-day tick is idempotent");

        // Abandoning removes only open goals; the completed record stays. Completed goals
        // do not count against the concurrency limit, so a fresh one can still be pinned.
        new AbandonGoalCommand { IssuerId = n.Id, GoalId = "test_goal" }.Apply(c);
        Check(n.Goals.Any(goal => goal.Id == "test_goal"), "a completed goal is kept as a record");
        new AdoptGoalCommand { IssuerId = n.Id, GoalId = "an_honest_state" }.Apply(c);
        Check(n.Goals.Any(goal => goal.Id == "an_honest_state" && goal.CompletedDay < 0),
            "completed goals leave the pinning limit to the open ones");
        new AbandonGoalCommand { IssuerId = n.Id, GoalId = "an_honest_state" }.Apply(c);
        Check(!n.Goals.Any(goal => goal.Id == "an_honest_state"), "an open goal is dropped by abandon");

        // Disabled: pinned goals are frozen, not measured; the data keeps them for re-enable.
        c.SetFeature("strategic_goals", false);
        Day(50); StrategicGoals.Tick(c);
        Check(n.Goals.Count == 2, "disabled goals are frozen, not measured");
        c.SetFeature("strategic_goals", true);
        StrategicGoals.Tick(c);
        Check(n.Goals.Count == 2, "re-enabling measures without losing state");

        // Saved state round-trips.
        var options = new JsonSerializerOptions { IncludeFields = true };
        var saved = JsonSerializer.Deserialize<List<ActiveGoal>>(JsonSerializer.Serialize(n.Goals, options), options);
        Check(saved!.Count == n.Goals.Count &&
              saved.Select(g => (g.Id, g.AdoptedDay, g.CompletedDay)).SequenceEqual(
                  n.Goals.Select(g => (g.Id, g.AdoptedDay, g.CompletedDay))),
            "goals survive a save roundtrip");

        // A goal without a measurable milestone is refused atomically.
        var stableCount = GoalCatalog.All.Count();
        using (var bad = JsonDocument.Parse("{\"bad_goal\":{\"name\":\"Wish\",\"description\":\"d\",\"milestones\":[]}}"))
            GoalCatalog.Merge(bad.RootElement, errors);
        Check(errors.Count >= 1 && GoalCatalog.All.Count() == stableCount && GoalCatalog.Find("bad_goal") is null,
            "a milestone-less wish is rejected");

        GoalCatalog.ResetCatalog();
    }
}
