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
    var documents = Path.Combine(temporary,"Documents with spaces");
    var canonicalProfile = LauncherCatalog.ProfileInDocuments(documents);
    Check(canonicalProfile == DealOrForce.Simulation.GameFolders.For(documents)!.Root &&
        canonicalProfile == Path.Combine(documents,"Bayte Realization","Deal or Force"),
        "Launcher and game use the same publisher/game folder under Documents");
    Check(!Directory.Exists(documents),"Resolving the player folder does not create or migrate files");
    bool missingDocuments = false;
    try { LauncherCatalog.ProfileInDocuments(""); } catch(DirectoryNotFoundException) { missingDocuments=true; }
    Check(missingDocuments,"Missing Documents never silently selects a relative installation folder");
    // The release number has had three parts and has four now (0.6.6.0): compare as versions, not strings.
    var release = Version.Parse(DealOrForce.Simulation.GameBuildInfo.Version);
    Check(typeof(LauncherCatalog).Assembly.GetName().Version ==
        new Version(release.Major, release.Minor, Math.Max(release.Build, 0), Math.Max(release.Revision, 0)),
        "Launcher test assembly and shared game release version agree");
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
    var currentSettings = Path.Combine(language,"settings","settings.json");
    using (var written = JsonDocument.Parse(File.ReadAllText(currentSettings)))
        Check(L.IsRussian && written.RootElement.GetProperty("map_smoothing").GetBoolean() == false,
            "Language read back, the game's other settings kept");
    Check(File.ReadAllText(Path.Combine(language,"settings.json")).Contains("english"),
        "Legacy settings are preserved when the current settings folder is written");
    File.WriteAllText(currentSettings, "not json");
    Check(!L.Remember(language, "english") && File.ReadAllText(currentSettings) == "not json",
        "An unreadable settings file is left alone, not replaced");
    L.Use("english");

    // Start options: the renderer as engine flags, the safe map as the game's own flag after `--`.
    var options = new StartOptions();
    Check(!options.EngineArguments().Any() && !options.GameArguments().Any(), "Automatic start passes nothing, so the game's own setting and fallback chain rule");
    options.Renderer = "opengl3_angle"; options.SafeMap = true;
    File.Delete(Path.Combine(root, "project.godot"));
    info = catalog.LaunchInfo(null, options);
    Check(info.ArgumentList.SequenceEqual(new[] { "--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3_angle", "--", "--safe-map" }),
        "ANGLE and the safe map reach the exported game as engine and user arguments");
    File.WriteAllText(Path.Combine(root, "project.godot"), "fixture");
    info = catalog.LaunchInfo(Path.Combine(root, "Deal or Force.exe"), options);
    Check(info.ArgumentList.Take(2).SequenceEqual(new[] { "--path", root }) && info.ArgumentList.Last() == "--safe-map",
        "Project launch keeps --path first and the game's flags last");
    var optionsProfile = Path.Combine(temporary, "options");
    options.Renderer = "vulkan"; options.Save(optionsProfile);
    var loaded = StartOptions.Load(optionsProfile);
    Check(loaded.Renderer == "vulkan" && loaded.SafeMap, "Start options remembered between runs");
    File.WriteAllText(StartOptions.FileIn(optionsProfile), "{\"renderer\":\"metal\",\"safe_map\":\"yes\"}");
    loaded = StartOptions.Load(optionsProfile);
    Check(loaded.Renderer == "auto" && !loaded.SafeMap, "An unknown renderer or a malformed flag falls back to Automatic");
    var windowProfile = Path.Combine(temporary, "window");
    Directory.CreateDirectory(Path.Combine(windowProfile, "settings"));
    File.WriteAllText(GameSettings.FileIn(windowProfile), "{ \"language\": \"russian\", \"max_fps\": \"60\" }");
    Check(GameSettings.Write(windowProfile, "window_mode", "windowed") && GameSettings.Read(windowProfile, "window_mode") == "windowed" &&
        GameSettings.Read(windowProfile, "language") == "russian" && GameSettings.Read(windowProfile, "max_fps") == "60",
        "Window mode written into the game's settings, every other key kept");

    // File check against BUILD_INFO.json.
    var build = Path.Combine(temporary, "build");
    Directory.CreateDirectory(Path.Combine(build, "data"));
    File.WriteAllText(Path.Combine(build, "game.exe"), "game");
    File.WriteAllText(Path.Combine(build, "data", "lib.dll"), "changed");
    File.WriteAllText(Path.Combine(temporary, "outside.txt"), "outside");
    string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    File.WriteAllText(Path.Combine(build, BuildManifest.FileName), JsonSerializer.Serialize(new
    {
        release = "EntertAlpha test", build_id = "b1", engine = "Godot test",
        sha256 = new Dictionary<string, string>
        {
            ["game.exe"] = Sha("game").ToLowerInvariant(), ["data/lib.dll"] = Sha("original"),
            ["gone.pck"] = Sha("pck"), ["../outside.txt"] = Sha("outside"),
        },
    }));
    var manifest = BuildManifest.Load(build)!;
    var checks2 = manifest.Verify(build).ToDictionary(c => c.File);
    Check(manifest.BuildId == "b1" && checks2["game.exe"].Matches, "An intact file matches, whatever the case of its hash");
    Check(!checks2["data/lib.dll"].Matches && !checks2["data/lib.dll"].Missing, "A changed file is reported as different");
    Check(checks2["gone.pck"].Missing, "A deleted file is reported as missing");
    Check(checks2["../outside.txt"].Missing, "A manifest name that leaves the build folder is never read");
    Check(BuildManifest.Load(temporary) is null, "No BUILD_INFO.json: nothing to compare with");

    // What's new: the project's docs first, the build's own copy, then the other language.
    var notes = Path.Combine(temporary, "notes");
    Directory.CreateDirectory(Path.Combine(notes, "docs"));
    File.WriteAllText(Path.Combine(notes, "CHANGELOG.md"), "en");
    Check(Changelog.Find(notes, true, "9.9") == Path.Combine(notes, "CHANGELOG.md"), "Russian falls back to the English notes when it has none");
    File.WriteAllText(Path.Combine(notes, "CHANGELOG.ru.md"), "ru");
    Check(Changelog.Find(notes, true, "9.9") == Path.Combine(notes, "CHANGELOG.ru.md"), "A delivered build's Russian notes are found beside it");
    File.WriteAllText(Path.Combine(notes, "docs", "CHANGELOG_9.9_EN.md"), "docs");
    Check(Changelog.Find(notes, false, "9.9") == Path.Combine(notes, "docs", "CHANGELOG_9.9_EN.md"), "The project's notes for this version come first");
    Check(Changelog.Find(Path.Combine(temporary, "nowhere"), false, "9.9") is null, "No notes anywhere: nothing is invented");

    // Two enabled mods that edit the same data file.
    var mods = Path.Combine(temporary, "conflicts");
    void ModFile(string mod, string relative) { var path = Path.Combine(mods, mod, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{}"); }
    foreach (var mod in new[] { "one", "two", "three" }) ModFile(mod, "manifest.json");
    ModFile("one", "common/defines/50_balance.json"); ModFile("two", "common/defines/50_balance.json");
    ModFile("one", "gfx/flag.json.png"); ModFile("two", "gfx/flag.json.png");
    ModFile("three", "common/ai/10_ai.yaml");
    var conflicts = ModConflicts.Find(new[] { "one", "two", "three" }.Select(id => new Package { Id = id, Path = Path.Combine(mods, id) }));
    Check(conflicts.Count == 1 && conflicts[0].File == "common/defines/50_balance.json" && conflicts[0].Mods.SequenceEqual(new[] { "one", "two" }),
        "Same data file in two mods is a conflict in load order; manifests and artwork are not");

    // The game's own report, written from outside the game.
    var reports = Path.Combine(temporary, "reports");
    var zip = DealOrForce.Simulation.SupportReport.Create(new DealOrForce.Simulation.SupportReport.Facts { GameVersion = "test", ReportsFolder = reports });
    Check(zip.Length > 0 && File.Exists(zip) && Path.GetDirectoryName(zip) == reports, "The launcher can write the game's report zip");
    Console.WriteLine($"{checks} launcher checks passed.");
}
finally { Directory.Delete(temporary, true); }
