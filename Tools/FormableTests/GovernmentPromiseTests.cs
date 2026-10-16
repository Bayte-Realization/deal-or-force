using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class GovernmentPromiseTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS promises: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(805);
        var n = c.NationByTag("ROU");
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        c.Defines.MaxConcurrentPromises = 2;

        Check(PromiseCatalog.All.Count() == 7, "seven shipped public promises");
        Check(PromiseCatalog.All.All(promise => promise.Check is { IsEmpty: false }),
            "every shipped promise has a checkable outcome");

        // Injected promises in the same shape a mod writes: one trivially true, one never
        // true, one spare for the concurrency limit, all observable through the treasury.
        var errors = new List<string>();
        using (var doc = JsonDocument.Parse(
            "{\"test_keep\":{\"name\":\"Test keep\",\"description\":\"d\",\"duration_days\":30," +
            "\"check\":{\"min_stability\":0.0},\"effects\":{\"treasury_percent\":0.02}}," +
            "\"test_break\":{\"name\":\"Test break\",\"description\":\"d\",\"duration_days\":20," +
            "\"check\":{\"max_stability\":-1.0},\"failure_effects\":{\"treasury_percent\":-0.02}}," +
            "\"test_third\":{\"name\":\"Test third\",\"description\":\"d\",\"duration_days\":90," +
            "\"check\":{\"min_stability\":0.0},\"effects\":{}}}"))
            PromiseCatalog.Merge(doc.RootElement, errors);
        Check(errors.Count == 0 && PromiseCatalog.All.Count() == 10, "injected promises merged beside the shipped seven");

        void Make(string id)
        {
            c.Commands.Enqueue(new MakePromiseCommand { IssuerId = n.Id, PromiseId = id });
            c.Commands.ApplyAll(c);
        }
        var plain = CampaignFingerprint.Of(c);
        Make("test_keep");
        Check(n.Promises.Count == 1 && n.Promises[0].DueDay == c.ElapsedDays + 30,
            "a promise records its deadline");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "promise_made" && e.ActorId == n.Id),
            "making a promise is public news");
        Check(CampaignFingerprint.Of(c) != plain, "the network fingerprint covers open promises");

        Make("test_keep");
        Check(n.Promises.Count == 1, "the same promise cannot be made twice");
        Make("test_break");
        Check(n.Promises.Count == 2, "a second distinct promise is allowed");
        Make("test_third");
        Check(n.Promises.Count == 2, "the concurrency limit refuses a third");
        Make("test_missing");
        Check(n.Promises.Count == 2, "an unknown promise id is a quiet no-op, not a crash");

        // Early fulfilment: the outcome is already true on day five, so the promise is kept
        // five days early and the keeping effect lands once.
        var before = n.Treasury;
        Day(5); GovernmentPromises.Tick(c);
        Check(n.Promises.Count == 1 && n.Promises[0].Id == "test_break",
            "a promise whose outcome is true is concluded before its deadline");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "promise_kept" && e.ActorId == n.Id),
            "keeping a promise is public news");
        Check(MathF.Abs(n.Treasury - before * 1.02f) < MathF.Abs(before) * 0.0001f + 0.01f,
            "the kept-promise effect applied exactly once");

        // The never-true one rides out its full deadline and breaks the day after.
        before = n.Treasury;
        Day(25); GovernmentPromises.Tick(c);
        Check(n.Promises.Count == 0, "a promise still false past its deadline is broken");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "promise_broken" && e.ActorId == n.Id),
            "breaking a promise is public news");
        Check(MathF.Abs(n.Treasury - before * 0.98f) < MathF.Abs(before) * 0.0001f + 0.01f,
            "the failure effect applied exactly once");
        var afterBreak = c.Chronicle.Entries.Count(e => e.Kind is "promise_kept" or "promise_broken" or "promise_made");
        GovernmentPromises.Tick(c);
        Check(c.Chronicle.Entries.Count(e => e.Kind is "promise_kept" or "promise_broken" or "promise_made") == afterBreak,
            "concluding is idempotent on the same day");

        // Disabled: nothing new can be promised and nothing out standing is concluded, so a
        // save predating the mechanic agrees with the data, and re-enabling does not silently
        // skip the missed years - the overdue promise concludes on the next evaluation.
        Make("test_keep");
        c.SetFeature("government_promises", false);
        Make("test_break");
        Check(n.Promises.Count == 1 && GovernmentPromises.BlockReason(c, n, PromiseCatalog.Find("test_break")!.Value)
                == DealOrForce.Simulation.Loc.Get("This mechanic is disabled in this scenario."),
            "a disabled mechanic refuses new promises with its sentence");
        Day(200); GovernmentPromises.Tick(c);
        Check(n.Promises.Count == 1, "an open promise is frozen, not concluded, while disabled");
        var keptSoFar = c.Chronicle.Entries.Count(e => e.Kind == "promise_kept" && e.ActorId == n.Id);
        c.SetFeature("government_promises", true);
        GovernmentPromises.Tick(c);
        Check(n.Promises.Count == 0 &&
            c.Chronicle.Entries.Count(e => e.Kind == "promise_kept" && e.ActorId == n.Id) == keptSoFar + 1,
            "re-enabling concludes the overdue promise on the next evaluation, once");

        // Saved state: the promise list round-trips field for field.
        Make("test_keep"); Make("test_third");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var saved = JsonSerializer.Deserialize<List<ActivePromise>>(JsonSerializer.Serialize(n.Promises, options), options);
        Check(saved!.Count == n.Promises.Count &&
            saved.Select(p => (p.Id, p.MadeDay, p.DueDay)).SequenceEqual(
                n.Promises.Select(p => (p.Id, p.MadeDay, p.DueDay))),
            "open promises survive a save roundtrip");

        // A promise nobody wrote a check for is refused, and the old definitions survive.
        var stable = PromiseCatalog.All.Count();
        using (var bad = JsonDocument.Parse("{\"bad_promise\":{\"name\":\"No check\",\"duration_days\":30}}"))
            PromiseCatalog.Merge(bad.RootElement, errors);
        Check(errors.Count >= 1 && PromiseCatalog.All.Count() == stable &&
            PromiseCatalog.Find("bad_promise") is null,
            "a promise without a checkable outcome is rejected atomically");

        PromiseCatalog.ResetCatalog();
    }
}
