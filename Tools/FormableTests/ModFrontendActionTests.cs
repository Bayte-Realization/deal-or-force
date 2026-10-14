using System;
using DealOrForce.Modding;

static class ModFrontendActionTests
{
    static ModFrontendActionDeclaration Decl(string id, string caption) => new() { Id = id, Caption = caption };
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS mod frontend actions: " + label); }
    public static void Run()
    {
        var catalog = new ModFrontendActionCatalog();
        catalog.ResetDeclarations(new[] { ("sandbox", (System.Collections.Generic.IEnumerable<ModFrontendActionDeclaration>)new[] { Decl("launch", "Open companion") }) });
        Check(catalog.Actions.Count == 1 && catalog.Actions[0] == new ModFrontendActionDescriptor("sandbox:launch", "sandbox", "launch", "Open companion"),
            "discovery exposes a namespaced caption without loading or executing code");
        Check(!catalog.TryRun("sandbox:launch", out _), "metadata alone cannot run a declared action");
        catalog.BeginRuntime();
        using var loaded = catalog.RegisterFor("sandbox");
        var invoked = 0;
        Check(loaded.Register("launch", () => { invoked++; return "Companion requested."; }) && invoked == 0,
            "loading a handler does not execute it");
        Check(catalog.TryRun("sandbox:launch", out var note) && note == "Companion requested." && invoked == 1,
            "explicit dispatch invokes the loaded handler and returns its feedback");
        Check(!loaded.Register("other:launch", () => "wrong") && !loaded.Register("launch", () => "replaced"),
            "a package cannot register undeclared or replace an owned action");
        catalog.StopRuntime();
        Check(!catalog.TryRun("sandbox:launch", out _) && !loaded.Register("launch", () => "stale"),
            "stopped code loses its actions and retained contexts cannot register again");
        catalog.BeginRuntime();
        Check(!loaded.Register("launch", () => "stale"), "a previous session context cannot hijack the new runtime");
        using var reloaded = catalog.RegisterFor("sandbox");
        Check(reloaded.Register("launch", () => "Reloaded.") && catalog.TryRun("sandbox:launch", out note) && note == "Reloaded.",
            "a newly loaded module can register its declared action in a fresh session");
        loaded.Dispose();
        Check(catalog.TryRun("sandbox:launch", out _), "disposing an old registration cannot remove a newer handler");
        reloaded.Dispose();
        Check(!catalog.TryRun("sandbox:launch", out _), "disposing the loaded module removes its owned handler");
        catalog.ResetDeclarations(Array.Empty<(string, System.Collections.Generic.IEnumerable<ModFrontendActionDeclaration>)>());
        Check(catalog.Actions.Count == 0 && !catalog.TryRun("sandbox:launch", out _), "disabled or removed packages have no visible or executable action");
        var errors = new System.Collections.Generic.List<string>();
        catalog.ResetDeclarations(new[] { ("sandbox", (System.Collections.Generic.IEnumerable<ModFrontendActionDeclaration>)new[] {
            Decl("launch", "  Open companion  "), Decl("Launch", "Duplicate"), Decl("other:launch", "Foreign"),
            Decl("../launch", "Traversal"), Decl("blank", "   "), Decl("control", "bad\ncaption"), null
        }) }, (_, error) => errors.Add(error));
        Check(catalog.Actions.Count == 1 && catalog.Actions[0].Caption == "Open companion" && errors.Count == 6,
            "ambiguous identifiers, duplicate actions and malformed captions cannot enter the menu");
        catalog.BeginRuntime();
        using var faulty = catalog.RegisterFor("sandbox");
        faulty.Register("launch", () => throw new InvalidOperationException("private-token-value"));
        var contained = false;
        try { contained = !catalog.TryRun("sandbox:launch", out note) && note == "Mod action failed."; } catch { }
        Check(contained, "a failing module action reports a safe refusal instead of crashing the interface or exposing its exception");
        var tooMany = new System.Collections.Generic.List<ModFrontendActionDeclaration>();
        for (var i = 0; i < 100; i++) tooMany.Add(Decl("action_" + i, "Caption"));
        catalog.ResetDeclarations(new[] { ("sandbox", (System.Collections.Generic.IEnumerable<ModFrontendActionDeclaration>)tooMany) });
        Check(catalog.Actions.Count == 32, "a package cannot flood the main menu with unbounded action descriptors");
        catalog.ResetDeclarations(new[] {
            ("first_mod", (System.Collections.Generic.IEnumerable<ModFrontendActionDeclaration>)new[] { Decl("launch", "First companion") }),
            ("second_mod", (System.Collections.Generic.IEnumerable<ModFrontendActionDeclaration>)new[] { Decl("launch", "Second companion") })
        });
        catalog.BeginRuntime();
        using var first = catalog.RegisterFor("first_mod");
        using var second = catalog.RegisterFor("second_mod");
        Check(first.Register("launch", () => "First") && second.Register("launch", () => "Second") &&
            catalog.TryRun("first_mod:launch", out note) && note == "First" && catalog.TryRun("second_mod:launch", out note) && note == "Second",
            "two packages may use the same local action name without stealing each other's handler");
        first.Dispose();
        Check(!catalog.TryRun("first_mod:launch", out _) && catalog.TryRun("second_mod:launch", out _),
            "unloading one module does not disable another package's action");
        catalog.ResetDeclarations(Array.Empty<(string, System.Collections.Generic.IEnumerable<ModFrontendActionDeclaration>)>());
        catalog.BeginRuntime();
        Check(!second.Register("launch", () => "stale") && !catalog.TryRun("second_mod:launch", out _),
            "rediscovery and a profile change revoke active and retained registrations");
        var packages = new System.Collections.Generic.List<(string, System.Collections.Generic.IEnumerable<ModFrontendActionDeclaration>)>();
        for (var i = 0; i < 20; i++) packages.Add(("pack_" + i, tooMany));
        catalog.ResetDeclarations(packages);
        Check(catalog.Actions.Count == 512, "resolved packages share a bounded overall frontend action catalogue");
    }
}
