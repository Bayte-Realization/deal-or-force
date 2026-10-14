using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

static class StrategicCueQueueTests
{
    static void Expect(bool condition, string reason)
    { if (!condition) throw new Exception(reason); }
    static SoundCatalog.Cue Cue(string name, string channel, bool yielding = false) => new(name, "test.wav", channel, 0, 0, yielding);
    static StrategicCueQueue.ChannelState State(string channel, int priority, bool playing, bool independent = false) => new(channel, priority, independent, playing);
    static bool Dispatch(StrategicCueQueue queue, Dictionary<string, SoundCatalog.Cue> cues,
        StrategicCueQueue.ChannelState[] states, List<string> played) =>
        queue.TryDispatch(name => cues.TryGetValue(name, out var cue) ? cue : null, states, cue => played.Add(cue.Name));

    public static void Run()
    {
        var cases = new (string Name, Action Test)[]
        {
            ("a lower-channel Finished cannot extract a queued cue whose own strategic channel is busy", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("war_declared"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["war_declared"] = Cue("war_declared", "strategic") };
                Expect(!Dispatch(q, cues, new[] { State("strategic", 2, true), State("notification", 1, false) }, played), "busy strategic channel must defer dispatch");
                Expect(q.Head == "war_declared" && q.Count == 1 && played.Count == 0, "blocked dispatch must preserve the head without invoking playback");
                Expect(Dispatch(q, cues, new[] { State("strategic", 2, false), State("notification", 1, false) }, played) && q.Count == 0 && played.Count == 1,
                    "the strategic Finished can play the same preserved head exactly once");
            }),
            ("a queued head cannot play over an active higher-priority channel", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("policy_changed"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["policy_changed"] = Cue("policy_changed", "notification") };
                Expect(!Dispatch(q, cues, new[] { State("strategic", 2, true), State("notification", 1, false) }, played), "higher strategic priority must defer the notification head");
                Expect(q.Head == "policy_changed" && q.Count == 1 && played.Count == 0, "priority deferral must not lose the queued notification");
                Expect(Dispatch(q, cues, new[] { State("strategic", 2, false), State("notification", 1, false) }, played) && played.Count == 1,
                    "the notification proceeds after the higher channel completes");
            }),
            ("a blocked FIFO head is not bypassed by a later cue", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("first"); q.Enqueue("second"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["first"] = Cue("first", "notification"), ["second"] = Cue("second", "strategic") };
                Expect(!Dispatch(q, cues, new[] { State("strategic", 2, true), State("notification", 1, false) }, played), "blocked first cue stays at the head");
                Expect(q.Count == 2 && q.Head == "first" && played.Count == 0, "head deferral preserves FIFO and every pending cue");
            }),
            ("queue retains its existing three-entry limit, uniqueness and reset", () =>
            {
                var q = new StrategicCueQueue();
                Expect(q.Enqueue("a") && q.Enqueue("b") && q.Enqueue("c") && !q.Enqueue("d") && !q.Enqueue("a") && !q.Enqueue(null), "overflow, duplicate and empty names are rejected");
                Expect(q.Count == 3 && q.Head == "a", "overflow does not displace older requests"); q.Clear();
                Expect(q.Count == 0 && q.Head is null && q.Enqueue("a"), "reset removes history and permits a fresh cue");
            }),
            ("independent channels do not hold the queue behind them", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("war"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["war"] = Cue("war", "strategic") };
                Expect(Dispatch(q, cues, new[] { State("strategic", 2, false), State("interface", 99, true, true) }, played) && played.Count == 1,
                    "independent interface feedback does not reserve strategic playback");
            }),
            ("an available higher-priority head may proceed while a lower channel is active", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("war"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["war"] = Cue("war", "strategic") };
                Expect(Dispatch(q, cues, new[] { State("strategic", 2, false), State("notification", 1, true) }, played), "lower-channel activity does not block a non-yielding strategic head");
            }),
            ("a yielding queued cue stays quiet while any interruptible channel is active", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("report"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["report"] = Cue("report", "strategic", true) };
                Expect(!Dispatch(q, cues, new[] { State("strategic", 2, false), State("notification", 1, true) }, played), "yielding takes precedence even over a lower-ranked active channel");
                Expect(q.Head == "report" && Dispatch(q, cues, new[] { State("strategic", 2, false), State("notification", 1, false) }, played), "a retained yielding head proceeds when the room is quiet");
            }),
            ("equal-priority separate channels do not deadlock each other", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("notice"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["notice"] = Cue("notice", "notification") };
                Expect(Dispatch(q, cues, new[] { State("notification", 1, false), State("battle", 1, true) }, played), "only strictly higher priority blocks a non-yielding head");
            }),
            ("stale entries are removed without stranding a valid next head", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("removed"); q.Enqueue("war"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["war"] = Cue("war", "strategic") };
                Expect(Dispatch(q, cues, new[] { State("strategic", 2, false) }, played) && q.Count == 0 && played.Count == 1 && played[0] == "war",
                    "a disappeared cue cannot leave the queue waiting for another unrelated Finished event");
            }),
            ("an undeclared target channel cannot receive playback", () =>
            {
                var q = new StrategicCueQueue(); q.Enqueue("missing_channel"); var played = new List<string>();
                var cues = new Dictionary<string, SoundCatalog.Cue> { ["missing_channel"] = Cue("missing_channel", "absent") };
                Expect(!Dispatch(q, cues, new[] { State("strategic", 2, false) }, played) && played.Count == 0,
                    "absence of an output channel is not permission to play");
            })
        };
        var failed = new List<string>();
        foreach (var test in cases)
            try { test.Test(); Console.WriteLine("PASS strategic cue queue: " + test.Name); }
            catch (Exception ex) { failed.Add(test.Name + ": " + ex.Message); Console.WriteLine("FAIL strategic cue queue: " + test.Name + ": " + ex.Message); }
        if (failed.Count > 0) throw new Exception(string.Join(Environment.NewLine, failed));
    }
}
