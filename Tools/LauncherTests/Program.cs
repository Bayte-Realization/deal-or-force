using System.Text.Json;
using DealOrForce.Modding;
using DealOrForce.Launcher;

var temporary = Path.Combine(Path.GetTempPath(), "dof-launcher-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Json(string relative, object data)
{
    var path = Path.Combine(temporary, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(data));
}
try
{
    var profile = Path.Combine(temporary, "profile");
    var root = Path.Combine(temporary, "install with spaces");
    Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "Deal or Force.exe"), "fixture");
    Directory.CreateDirectory(Path.Combine(root, "data_DealOrForce_windows_x86_64", "game"));
    Json("install with spaces/mods/a/manifest.json", new { id = "a", name = "Alpha", dependencies = new[] { "b" }, enabled = true });
    Json("install with spaces/mods/b/manifest.json", new { id = "b", name = "Beta", enabled = false });
    Json("install with spaces/mods/_templates/c/manifest.json", new { id = "template" });
    Json("install with spaces/data_DealOrForce_windows_x86_64/dlc/free/dlc.json", new { id = "free" });
    Json("install with spaces/dlc/paid/dlc.json", new { id = "paid", entitlement = "paid_key" });
    Json("install with spaces/dlc/child/dlc.json", new { id = "child", requires = new[] { "paid" } });
    Json("profile/mods/a/manifest.json", new { id = "a", name = "User override", dependencies = new[] { "b" } });
    var catalog = new LauncherCatalog(root, profile); catalog.Discover();
    Check(catalog.Packages.Count == 5, "Loose/published/user folders discovered; templates excluded");
    Check(catalog.Packages.Single(p => p.Id == "a").Name == "User override", "User duplicate wins");
    Check(!catalog.Packages.Single(p => p.Id == "a").Active, "Disabled dependency blocks mod");
    Check(!catalog.Packages.Single(p => p.Id == "paid").Active, "Paid DLC cannot be enabled without entitlement");
    Check(!catalog.Packages.Single(p => p.Id == "child").Active, "Locked dependency blocks child DLC");
    var before = File.ReadAllText(Path.Combine(root, "mods/b/manifest.json"));
    catalog.Packages.Single(p => p.Id == "b").Enabled = true;
    catalog.Packages.Single(p => p.Id == "free").Enabled = false;
    catalog.Save(); catalog.Discover();
    Check(catalog.Packages.Single(p => p.Id == "b").Active && catalog.Packages.Single(p => p.Id == "a").Active, "Saved toggle overrides manifest default");
    Check(!catalog.Packages.Single(p => p.Id == "free").Active, "Free DLC can be disabled persistently");
    Check(catalog.EffectiveOrder(false).SequenceEqual(new[] { "b", "a" }), "Dependencies precede requested load order");
    Check(before == File.ReadAllText(Path.Combine(root, "mods/b/manifest.json")), "Manifest unchanged");
    catalog.Save(); Check(File.Exists(catalog.SelectionPath + ".bak"), "Atomic settings backup");
    Json("profile/entitlements.json", new { owned = new[] { "paid_key" } }); catalog.Discover();
    Check(catalog.Packages.Single(p => p.Id == "child").Active, "Owned DLC dependency activated");
    catalog.Packages.Single(p => p.Id == "paid").Enabled = false; catalog.Resolve();
    Check(!catalog.Packages.Single(p => p.Id == "child").Active, "Disabled DLC propagates to child");
    var resolved = ContentDependencies.Resolve(new[] {
        new ContentRequirement("a", true, new[] { "b" }), new ContentRequirement("b", true, new[] { "c" }),
        new ContentRequirement("ok", true, Array.Empty<string>()) }, out var failures);
    Check(resolved.SequenceEqual(new[] { "ok" }) && failures.Count == 2, "Transitive missing dependency rejected");
    resolved = ContentDependencies.Resolve(new[] {
        new ContentRequirement("a", true, new[] { "B" }), new ContentRequirement("b", true, new[] { "a" }) }, out failures);
    Check(resolved.Count == 0 && failures.Count == 2, "Case-insensitive cycles rejected");
    var info = catalog.LaunchInfo(null);
    Check(info.WorkingDirectory == root && info.FileName == Path.Combine(root, "Deal or Force.exe") &&
        info.Environment["DOF_CONTENT_SELECTION"] == catalog.SelectionPath && !info.UseShellExecute, "Export launch path, working dir and settings handoff");
    File.WriteAllText(Path.Combine(root, "project.godot"), "fixture");
    info = catalog.LaunchInfo(Path.Combine(root, "Deal or Force.exe"));
    Check(info.ArgumentList.SequenceEqual(new[] { "--path", root }), "Project launch quotes paths using ArgumentList");
    Check(LauncherCatalog.FindRoot(Path.Combine(root, "Tools")) == root, "Root discovery independent of working directory");
    File.WriteAllText(catalog.SelectionPath, "broken"); catalog.Discover();
    Check(catalog.SettingsError.Length > 0, "Corrupt profile reported");
    bool refused = false; try { catalog.Save(); } catch (InvalidDataException) { refused = true; }
    Check(refused && File.ReadAllText(catalog.SelectionPath) == "broken", "Corrupt profile never silently overwritten");

    // The language button writes the game's own settings file, and nothing else in it.
    var language = Path.Combine(temporary, "language");
    Directory.CreateDirectory(language);
    File.WriteAllText(Path.Combine(language, "settings.json"), "{ \"language\": \"english\", \"map_smoothing\": false }");
    Check(L.Remember(language, "russian"), "Language written into the game's settings");
    L.Choose(language);
    using (var written = JsonDocument.Parse(File.ReadAllText(Path.Combine(language, "settings.json"))))
        Check(L.IsRussian && written.RootElement.GetProperty("map_smoothing").GetBoolean() == false,
            "Language read back, the game's other settings kept");
    File.WriteAllText(Path.Combine(language, "settings.json"), "not json");
    Check(!L.Remember(language, "english") && File.ReadAllText(Path.Combine(language, "settings.json")) == "not json",
        "An unreadable settings file is left alone, not replaced");
    L.Use("english");
    Console.WriteLine($"{checks} launcher checks passed.");
}
finally { Directory.Delete(temporary, true); }
