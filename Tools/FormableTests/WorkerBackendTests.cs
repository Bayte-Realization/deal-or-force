using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DealOrForce.Modding;
using DealOrForce.Simulation;

static class WorkerBackendTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS worker: " + label); }
    public static void Fixture(string kind)
    {
        Console.In.ReadToEnd();
        if (kind == "hang") { System.Threading.Thread.Sleep(30000); return; }
        if (kind == "oversize") { Console.Write(new string('x', 300000)); return; }
        if (kind == "exit") { Environment.Exit(7); return; }
        Console.Write("""{"protocol":1,"logs":["fixture"],"commands":[{"type":"set_feature","feature":"aviation","enabled":true}]}""");
    }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(802); var logs = new List<string>();
        c.SetFeature("aviation", false);
        var path = Environment.ProcessPath;
        string[] Args(string kind) => Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(WorkerBackendTests).Assembly.Location, "--worker-fixture", kind }
            : new[] { "--worker-fixture", kind };
        var good = new WorkerModBackend(path, Args("ok"), 2500, 7, logs.Add);
        good.Hook(c, "campaign_created");
        Check(!good.Disabled && logs.Contains("fixture"), "real process receives a snapshot and responds: " +
            string.Join(" | ", logs));
        Check(!c.FeatureEnabled("aviation"), "response queues, does not mutate mid-hook");
        c.Commands.ApplyAll(c);
        Check(c.FeatureEnabled("aviation"), "validated command applies on the campaign boundary");
        logs.Clear(); good.Hook(c, "day_advanced"); good.Hook(c, "day_advanced");
        Check(logs.Count == 0, "same day is not dispatched twice");
        foreach (var mode in new[] { "hang", "oversize", "exit" })
        {
            var broken = new WorkerModBackend(path, Args(mode), mode == "hang" ? 150 : 2500, 7, logs.Add);
            var timer = Stopwatch.StartNew(); broken.Hook(c, "load");
            Check(broken.Disabled && timer.ElapsedMilliseconds < 6000, mode + " is bounded and disables the worker");
            var count = logs.Count; broken.Hook(c, "load");
            Check(logs.Count == count, "failed worker does not flood later hooks: " + mode);
        }
        c.Commands.ApplyAll(c); c.SetFeature("aviation", false);
        var rejected = false;
        try { WorkerModBackend.AcceptResponse(c,
            """{"protocol":1,"commands":[{"type":"set_feature","feature":"aviation","enabled":true},{"type":"execute_arbitrary_code","issuer":"GBR","target":"CAN"}]}""", logs.Add); }
        catch (InvalidDataException) { rejected = true; }
        c.Commands.ApplyAll(c);
        Check(rejected && !c.FeatureEnabled("aviation"), "invalid tail rejects the entire batch atomically");
        using (var snapshot = System.Text.Json.JsonDocument.Parse(WorkerModBackend.BuildRequest(c, "test")))
        {
            var nation = snapshot.RootElement.GetProperty("nations")[0];
            Check(nation.TryGetProperty("public_debt", out _) && nation.TryGetProperty("convictions", out _) &&
                nation.TryGetProperty("laws", out _) && snapshot.RootElement.GetProperty("capabilities").GetArrayLength() == 6,
                "all worker languages receive additive finance and political capabilities");
        }
        var korea = c.NationByTag("KOR");
        NationalCourses.Initialize(c, korea);
        var oldDebt = korea.PublicDebt; var oldCash = korea.Treasury; var oldShock = korea.NationalCourse.Shock;
        WorkerModBackend.AcceptResponse(c,
            """{"protocol":1,"commands":[{"type":"manage_debt","issuer":"KOR","action":"borrow","amount":0.1},{"type":"enact_policy","issuer":"KOR","policy":"fiscal_discipline"},{"type":"course_shock","issuer":"KOR","strength":0.4,"reason":"mod event"}]}""", logs.Add);
        Check(korea.PublicDebt == oldDebt && korea.NationalCourse.Shock == oldShock, "new worker operations never mutate during response parsing");
        c.Commands.ApplyAll(c);
        Check(korea.PublicDebt > oldDebt && korea.Treasury > oldCash, "worker borrowing uses the actual bounded sovereign finance command");
        Check(korea.Laws.Values.Any(l => l.Id == "fiscal_discipline"), "worker policy uses the actual reform command");
        Check(korea.NationalCourse.Shock > oldShock && korea.NationalCourse.Dominant.Length > 0,
            "worker shock influences gradual adaptation without replacing the course");
        void RejectBatch(string tail, string label)
        {
            var debtBefore = korea.PublicDebt; var failed = false;
            try { WorkerModBackend.AcceptResponse(c,
                "{\"protocol\":1,\"commands\":[{\"type\":\"manage_debt\",\"issuer\":\"KOR\",\"action\":\"borrow\",\"amount\":0.1}," + tail + "]}", logs.Add); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { failed = true; }
            c.Commands.ApplyAll(c); Check(failed && korea.PublicDebt == debtBefore, label);
        }
        RejectBatch("""{"type":"manage_debt","issuer":"KOR","action":"borrow","amount":-3}""", "invalid debt tail rejects the entire batch");
        RejectBatch("""{"type":"enact_policy","issuer":"KOR","policy":"missing_policy"}""", "unknown policy cannot partially execute earlier borrowing");
        RejectBatch("""{"type":"course_shock","issuer":"KOR","strength":4,"reason":"invalid"}""", "excessive national shock cannot partially execute the batch");
        Check(!ModOperations.QueueDebt(c, "KOR", "borrow", double.NaN) &&
            !ModOperations.QueueCourseShock(c, "KOR", double.PositiveInfinity, "invalid"), "managed and Godot bridges reject non-finite values");
        c.ConfigureSinglePlayer(korea.Id); c.AddHumanSeat(c.NationByTag("JPN").Id);
        var multiplayerRejected = false;
        try { WorkerModBackend.AcceptResponse(c, """{"protocol":1,"commands":[]}""", logs.Add); }
        catch (InvalidOperationException) { multiplayerRejected = true; }
        Check(multiplayerRejected && !ModOperations.QueueCourseShock(c, "KOR", .2, "mod"),
            "local worker and non-network shock operations cannot mutate multiplayer");
        c.ConfigureSinglePlayer(korea.Id);
        var root = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        var go = Path.Combine(root, "build", "example-go.exe");
        var rust = Path.Combine(root, "build", "example-rust.exe");
        var luau = Path.Combine(root, "build", "luau-worker", "Release", "deal-or-force-luau.exe");
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 365L * 24);
        // Restored 2026-10-29 with a working build/example-go.exe (go build of the
        // go_worker template); the loop still skips any fixture whose binary is absent.
        foreach (var (exe, arguments, language) in new[] {
            (go, Array.Empty<string>(), "Go"),
            (rust, Array.Empty<string>(), "Rust"),
            (luau, new[] { Path.Combine(root, "mods", "_templates", "luau_worker", "corecode", "main.luau") }, "Luau") })
        {
            if (!File.Exists(exe)) { Console.WriteLine("SKIP compiled " + language + " fixture: " + exe); continue; }
            c.SetFeature("aviation", false);
            var worker = new WorkerModBackend(exe, arguments, 2500, 1, logs.Add);
            worker.Hook(c, "day_advanced"); c.Commands.ApplyAll(c);
            Check(!worker.Disabled && c.FeatureEnabled("aviation"), language + " example runs through the real backend");
        }
    }
}
