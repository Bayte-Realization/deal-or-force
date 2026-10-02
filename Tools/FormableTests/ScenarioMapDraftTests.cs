using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class ScenarioMapDraftTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS map editor " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(222); c.ScenarioId = "editor_fixture";
        var kor = c.NationByTag("KOR"); var jpn = c.NationByTag("JPN");
        var kr = c.States.First(s => s.NationMapIndex == kor.MapIndex);
        var jr = c.States.First(s => s.NationMapIndex == jpn.MapIndex);
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)kr.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)kor.MapIndex, 512).ToArray(), 32, 16, _ => false));
        c.Armies.Clear();
        var before = CampaignFingerprint.Of(c);
        var d = new ScenarioMapDraft(SaveSystem.ExportScenarioJson(c, "editor_copy"), c.ProvinceMap.ProvinceCount, "editor_copy");
        var p = 1; var originalOwner = d.OwnerAt(p); var originalState = d.StateAt(p);
        var identity = JsonNode.Parse(d.ReadEntity(true, kor.MapIndex)).AsObject();
        var oldName = identity["Name"].GetValue<string>();
        identity["Name"] = "Edited Korea"; identity["Color"] = "#456789";
        d.ReplaceEntity(true, kor.MapIndex, identity.ToJsonString());
        Check(d.Countries.First(n => n.MapIndex == kor.MapIndex).Name == "Edited Korea" && d.Dirty, "country inspector changes draft identity");
        d.Undo(); Check(d.Countries.First(n => n.MapIndex == kor.MapIndex).Name == oldName && !d.Dirty, "identity undo restores clean revision");
        d.Redo(); Check(JsonNode.Parse(d.ReadEntity(true, kor.MapIndex))["Color"].GetValue<string>() == "#456789", "identity redo retains colour");
        d.Undo();
        identity["MapIndex"] = 9999;
        try { d.ReplaceEntity(true, kor.MapIndex, identity.ToJsonString()); throw new Exception("identity change accepted"); }
        catch (InvalidDataException) { Check(!d.Dirty, "identity IDs protected without dirtying draft"); }
        var regional = JsonNode.Parse(d.ReadEntity(false, kr.MapIndex)).AsObject(); var regionalName = regional["Name"].GetValue<string>();
        regional["Name"] = "Scenario region"; d.ReplaceEntity(false, kr.MapIndex, regional.ToJsonString());
        Check(d.Regions.First(r => r.MapIndex == kr.MapIndex).Name == "Scenario region", "region inspector updates browser data");
        d.Undo(); Check(d.Regions.First(r => r.MapIndex == kr.MapIndex).Name == regionalName, "region undo refreshes browser");
        Check(CampaignFingerprint.Of(c) == before, "metadata edits do not touch live campaign");
        var originalSettings = d.Settings;
        var originalWorld = JsonNode.Parse(d.Export());
        var setup = originalSettings with { Name = "Alternative opening", Description = "Independent historical scenario", Year = 1836, Month = 1, Day = 1, Hour = 6 };
        d.UpdateSettings(setup);
        var configured = JsonNode.Parse(d.Export());
        Check(d.Settings == setup && configured["Date"]["Year"].GetValue<int>() == 1836 && configured["Date"]["Hour"].GetValue<int>() == 6,
            "scenario settings update the exported clock");
        Check(JsonNode.DeepEquals(configured["ElapsedDays"], originalWorld["ElapsedDays"]) && JsonNode.DeepEquals(configured["RandomState"], originalWorld["RandomState"]),
            "date and presentation edits preserve elapsed-day timers and random stream");
        d.Undo(); Check(d.Settings == originalSettings && !d.Dirty, "settings undo restores clean clock and presentation");
        d.Redo(); Check(d.Settings == setup && d.Dirty, "settings redo restores the whole operation"); d.Undo();
        foreach (var invalid in new[] { originalSettings with { Name = " " }, originalSettings with { Name = new string('x', 161) },
                     originalSettings with { Description = new string('x', 4001) }, originalSettings with { Seed = 0 },
                     originalSettings with { Hour = 24 }, originalSettings with { Year = 1900, Month = 2, Day = 29 },
                     originalSettings with { Year = 2024, Month = 4, Day = 31 }, originalSettings with { Year = 10000 } })
        {
            try { d.UpdateSettings(invalid); throw new Exception("invalid scenario settings accepted"); }
            catch (InvalidDataException) { Check(d.Settings == originalSettings && !d.Dirty && d.CanRedo, "invalid settings leave the draft and redo intact"); }
        }
        d.UpdateSettings(originalSettings with { Seed = 98765, Year = 2024, Month = 2, Day = 29 });
        Check(JsonNode.Parse(d.Export())["RandomState"].GetValue<uint>() == new SimRandom(98765).State, "seed change creates the deterministic initial stream");
        d.Undo(); Check(JsonNode.DeepEquals(JsonNode.Parse(d.Export())["RandomState"], originalWorld["RandomState"]), "seed undo restores the precise previous stream");
        Check(CampaignFingerprint.Of(c) == before, "scenario setup never mutates the source world");
        foreach (var (year, leap) in new[] { (2024, true), (1900, false), (2000, true), (2100, false) })
        {
            var date = new GameDate(year, 2, 28) { Hour = 23 }; date.Advance(1);
            Check(date.Hour == 0 && date.Month == (leap ? 2 : 3) && date.Day == (leap ? 29 : 1), "Gregorian leap rule " + year);
            if (leap) { date.Advance(24); Check(date.Month == 3 && date.Day == 1, "leap day advances into March " + year); }
        }
        var unchanged = d.ReadEntity(true, kor.MapIndex);
        var edited = JsonNode.Parse(unchanged).AsObject();
        foreach (var (key, value) in new (string, JsonNode)[] { ("Population", JsonValue.Create(-1)), ("Population", JsonValue.Create(1.5)),
                     ("Stability", JsonValue.Create(1.01)), ("TaxRate", JsonValue.Create(-.1)), ("Treasury", JsonValue.Create("NaN")),
                     ("Gdp", JsonValue.Create(1e200)), ("Id", JsonValue.Create(kor.Id + 1000)), ("Government", JsonValue.Create("unknown_government")) })
        {
            var invalid = edited.DeepClone().AsObject(); invalid[key] = value;
            try { d.ReplaceEntity(true, kor.MapIndex, invalid.ToJsonString()); throw new Exception("invalid inspector value accepted: " + key); }
            catch (InvalidDataException) { Check(d.ReadEntity(true, kor.MapIndex) == unchanged && !d.Dirty, "invalid " + key + " leaves draft and undo unchanged"); }
        }
        var missing = edited.DeepClone().AsObject(); missing.Remove("Market");
        try { d.ReplaceEntity(true, kor.MapIndex, missing.ToJsonString()); throw new Exception("stored field silently removed"); }
        catch (InvalidDataException) { Check(d.ReadEntity(true, kor.MapIndex) == unchanged, "stored fields cannot silently disappear"); }
        var extension = new JsonObject { ["description"] = "custom mod data", ["precise"] = 123456789012345L };
        edited["ModExtension"] = extension; edited["Treasury"] = -3;
        d.ReplaceEntity(true, kor.MapIndex, edited.ToJsonString());
        Check(JsonNode.DeepEquals(JsonNode.Parse(d.ReadEntity(true, kor.MapIndex))["ModExtension"], extension), "unknown mod fields survive inspector edits");
        Check(JsonNode.Parse(d.ReadEntity(true, kor.MapIndex))["Treasury"].GetValue<double>() == -3, "negative treasury remains a valid debt position");
        d.Undo(); Check(!d.Dirty, "validated entity change remains undoable");
        var malformed = "{ \"Name\": \"Unfinished";
        try { ScenarioEntityDocument.Parse(malformed, true); throw new Exception("broken JSON accepted"); }
        catch (System.Text.Json.JsonException) { Check(malformed == "{ \"Name\": \"Unfinished" && d.ReadEntity(true, kor.MapIndex) == unchanged, "invalid JSON never replaces a draft"); }
        var controls = ScenarioInspectorCatalog.Fields(true).ToArray();
        Check(controls.Any(f => f.Key == "Population") && controls.Single(f => f.Key == "Stability").Scale == 100, "shipped inspector controls expose percentages and population");
        ContentWorkbench.Validate("common/editor/99_test.json", "{\"country\":{\"Stability\":{\"label\":\"Custom stability\"}}}");
        Check(ScenarioInspectorCatalog.Fields(true).SequenceEqual(controls), "schema validation cannot change live editor controls");
        foreach (var invalidRules in new[] { "{\"country\":{\"MapIndex\":{}}}", "{\"country\":{\"Population\":{\"scale\":0}}}", "{\"region\":{\"Radicalism\":{\"step\":-1}}}", "{\"country\":[]}",
                     "{\"country\":{\"Population\":{\"order\":1e100}}}", "{\"country\":{\"Population\":{\"order\":1.5}}}", "{\"country\":{\"Population\":{\"hidden\":\"yes\"}}}" })
        {
            try { ContentWorkbench.Validate("common/editor/99_test.json", invalidRules); throw new Exception("invalid control accepted"); }
            catch (InvalidDataException) { Check(ScenarioInspectorCatalog.Fields(true).SequenceEqual(controls), "invalid control declaration rejected without live mutation"); }
        }
        using (var declaration = System.Text.Json.JsonDocument.Parse("{\"country\":{\"Stability\":{\"label\":\"Modded\",\"order\":-1},\"WarSupport\":{\"hidden\":true}}}")) ScenarioInspectorCatalog.Merge(declaration.RootElement);
        Check(ScenarioInspectorCatalog.Fields(true).First().Label == "Modded" && !ScenarioInspectorCatalog.Fields(true).Any(f => f.Key == "WarSupport"), "mods can relabel reorder and hide inspector controls");
        ScenarioInspectorCatalog.ResetCatalog();
        Check(ContentWorkbench.AddValue("{\"a/b\":[]}", "/a~1b", "", "{\"x\":1}").Contains("\"x\""), "content editor appends structured array elements");
        Check(JsonNode.Parse(ContentWorkbench.RemoveValue("{\"a/b\":[1,2]}", "/a~1b/0"))["a/b"][0].GetValue<int>() == 2, "deletion supports escaped array pointers");
        try { ContentWorkbench.AddValue("{\"x\":1}", "", "x", "2"); throw new Exception("duplicate property overwritten"); }
        catch (InvalidDataException) { Check(true, "duplicate property is not overwritten"); }
        try { ContentWorkbench.SetValue("{\"x\":1}", "", "2"); throw new Exception("root replaced"); }
        catch (InvalidDataException) { Check(true, "document root protected"); }
        try { ContentWorkbench.SetValue("{\"existing\":1}", "/missing", "2"); throw new Exception("stale property created implicitly"); }
        catch (InvalidDataException) { Check(true, "stale property path cannot silently create a field"); }
        Check(DealOrForce.Modding.ModCodePaths.AssemblyPath("corecode/mod.dll", "csharp/old.dll") == "corecode/mod.dll", "corecode manifest takes priority");
        Check(DealOrForce.Modding.ModCodePaths.AssemblyPath("", "csharp/old.dll") == "csharp/old.dll", "legacy code manifest remains compatible");
        foreach (var unsafePath in new[] { "../escape.dll", "csharp/new.dll", "corecode/../escape.dll", "corecode/code.cs", "C:/escape.dll" })
        {
            try { DealOrForce.Modding.ModCodePaths.AssemblyPath(unsafePath, "csharp/old.dll"); throw new Exception("invalid path accepted"); }
            catch (InvalidDataException) { Check(true, "invalid modern code path rejected: " + unsafePath); }
        }
        Check(d.Paint(p, jpn.MapIndex, jr.MapIndex), "province changes");
        d.EndStroke(); Check(d.Dirty && d.CanUndo, "stroke recorded");
        Check(CampaignFingerprint.Of(c) == before && c.ProvinceMap.ProvinceToNation[p] == originalOwner, "live campaign and topology untouched");
        d.Undo(); Check(d.OwnerAt(p) == originalOwner && d.StateAt(p) == originalState && !d.Dirty, "undo restores all fields and clean revision");
        d.Redo(); Check(d.OwnerAt(p) == jpn.MapIndex && d.Dirty, "redo restores stroke");
        var json = JsonNode.Parse(d.Export());
        Check(json["ProvinceController"][p].GetValue<int>() == jpn.MapIndex && json["ProvinceOccupation"][p].GetValue<float>() == 0, "controller follows ownership");
        Check(json["ScenarioTerritories"][jpn.Tag].AsArray().Any(v => v.GetValue<int>() == p), "formation territories rebuilt");
        Check(json["Chronicle"].AsArray().Count == 0, "new scenario has no campaign chronicle");
        try { d.Paint(p, kor.MapIndex, jr.MapIndex); throw new Exception("invalid region accepted"); }
        catch (InvalidDataException) { Check(true, "foreign target region rejected"); }
        d.Paint(p, kor.MapIndex, kr.MapIndex); d.CancelStroke(); Check(d.OwnerAt(p) == jpn.MapIndex, "cancel gesture restores draft");
        var grouped = new ScenarioMapDraft(SaveSystem.ExportScenarioJson(c, "grouped"), c.ProvinceMap.ProvinceCount, "grouped");
        Check(grouped.ProvinceCount >= 2, "fixture supports multi-province gestures");
        grouped.Paint(1, jpn.MapIndex, jr.MapIndex); grouped.Paint(2, jpn.MapIndex, jr.MapIndex); grouped.EndStroke();
        grouped.Undo(); Check(grouped.OwnerAt(1) == kor.MapIndex && grouped.OwnerAt(2) == kor.MapIndex && !grouped.CanUndo, "one undo restores entire stroke");
        grouped.Redo(); Check(grouped.OwnerAt(1) == jpn.MapIndex && grouped.OwnerAt(2) == jpn.MapIndex, "one redo restores entire stroke");
        grouped.Undo(); grouped.Paint(1, jpn.MapIndex, jr.MapIndex); grouped.EndStroke();
        Check(!grouped.CanRedo, "new edit discards redo branch");
        grouped.Undo(); grouped.Paint(1, jpn.MapIndex, jr.MapIndex); grouped.Paint(1, kor.MapIndex, kr.MapIndex); grouped.EndStroke();
        Check(!grouped.CanUndo && !grouped.Dirty, "net-zero stroke adds no history");
        var root = Path.Combine(Path.GetTempPath(), "dof_map_editor_" + Guid.NewGuid().ToString("N"));
        try
        {
            d.Save(root, "Editor test"); Check(!d.Dirty, "save establishes clean revision");
            var entry = ScenarioLibrary.Discover(Array.Empty<string>(), Path.Combine(root, "scenarios")).Single(e => e.Id == d.Id);
            var loaded = ScenarioLibrary.Create(entry, 1, c.ProvinceMap.ProvinceCount);
            loaded.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
            Check(loaded.ProvinceMap.ProvinceToNation[p] == jpn.MapIndex && loaded.ProvinceMap.ProvinceToState[p] == jr.MapIndex, "export rediscovered and loaded with edited political layer");
            d.Undo(); Check(d.Dirty, "undo after save marks dirty"); d.Redo(); Check(!d.Dirty, "redo to saved revision clean");
            Check(CampaignFingerprint.Of(c) == before, "loading exported copy never modifies source");
            var authored = new ScenarioMapDraft(SaveSystem.ExportScenarioJson(c, "settings_copy"), c.ProvinceMap.ProvinceCount, "settings_copy");
            authored.UpdateSettings(setup with { Seed = 445566 }); authored.Save(root);
            var authoredEntry = ScenarioLibrary.Discover(Array.Empty<string>(), Path.Combine(root, "scenarios")).Single(e => e.Id == authored.Id);
            var authoredCampaign = ScenarioLibrary.Create(authoredEntry, 778899, c.ProvinceMap.ProvinceCount);
            Check(authoredEntry.Name == setup.Name && authoredEntry.Description == setup.Description && authoredCampaign.Date.Year == 1836 && authoredCampaign.Date.Hour == 6,
                "saved scenario rediscovered with its name description and independent date");
            Check(authoredCampaign.Seed == 778899 && authoredCampaign.Random.State == new SimRandom(778899).State, "campaign picker can override authored seed without changing date");
            authored.Undo(); Check(authored.Dirty, "undo scenario settings after saving marks dirty");
            authored.Redo(); Check(!authored.Dirty, "redo returns to saved scenario setup revision");
            var worldPath = authoredEntry.WorldFile; var manifestPath = authoredEntry.ManifestFile;
            var modWorld = JsonNode.Parse(File.ReadAllText(worldPath)).AsObject();
            modWorld["ModExtension"] = new JsonObject { ["precision"] = 0.123456789, ["note"] = "keep me" };
            modWorld["History"][kor.Id.ToString()] = new JsonArray(new JsonObject { ["Day"] = 0, ["ModSample"] = "keep sample" });
            var modManifest = JsonNode.Parse(File.ReadAllText(manifestPath)).AsObject();
            modManifest["ModExtension"] = new JsonObject { ["author"] = "modder", ["nested"] = new JsonArray(1, 2, 3) };
            File.WriteAllText(worldPath, modWorld.ToJsonString()); File.WriteAllText(manifestPath, modManifest.ToJsonString());
            var reopened = ScenarioMapDraft.OpenForEditing(authoredEntry, c.ProvinceMap.ProvinceCount, root, "unused_copy");
            Check(reopened.Id == authored.Id && reopened.HasSavedVersion && !reopened.Dirty && !reopened.CanUndo,
                "reopening local scenario retains identity and starts clean without artificial undo");
            Check(reopened.Settings == setup with { Seed = 445566 }, "reopening retains name description authored clock and seed");
            var oldWorld = File.ReadAllText(worldPath); var oldManifest = File.ReadAllText(manifestPath);
            reopened.UpdateSettings(reopened.Settings with { Name = "Reopened scenario" }); reopened.Save(root);
            Check(File.ReadAllText(worldPath + ".bak") == oldWorld && File.ReadAllText(manifestPath + ".bak") == oldManifest,
                "resaving creates exact backups for both scenario documents");
            var savedWorld = JsonNode.Parse(File.ReadAllText(worldPath)); var savedManifest = JsonNode.Parse(File.ReadAllText(manifestPath));
            Check(JsonNode.DeepEquals(savedWorld["ModExtension"], modWorld["ModExtension"]) &&
                JsonNode.DeepEquals(savedManifest["ModExtension"], modManifest["ModExtension"]) &&
                JsonNode.DeepEquals(savedWorld["RandomState"], modWorld["RandomState"]) &&
                JsonNode.DeepEquals(savedWorld["History"], modWorld["History"]), "roundtrip preserves unknown world and manifest fields history and random state");
            var twice = ScenarioMapDraft.OpenForEditing(authoredEntry, c.ProvinceMap.ProvinceCount, root, "unused_copy");
            Check(twice.Settings.Name == "Reopened scenario", "reopen reads latest metadata rather than stale library card");
            var copiedRoot = Path.Combine(root, "copy_workspace");
            var copy = ScenarioMapDraft.OpenForEditing(authoredEntry, c.ProvinceMap.ProvinceCount, copiedRoot, "external_copy");
            Check(copy.Id == "external_copy" && !copy.HasSavedVersion && !copy.Dirty, "external scenario opens detached with a new identity");
            copy.Save(copiedRoot);
            Check(File.ReadAllText(worldPath) == savedWorld.ToJsonString() && File.ReadAllText(manifestPath) == savedManifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                "saving external copy leaves original documents untouched");
            var untouched = File.ReadAllText(worldPath);
            File.AppendAllText(manifestPath, "\n ");
            reopened.UpdateSettings(reopened.Settings with { Name = "Conflicting edit" });
            var rejected = false;
            try { reopened.Save(root); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && reopened.Dirty && File.ReadAllText(worldPath) == untouched && File.ReadAllText(manifestPath).EndsWith("\n "),
                "external edits reject save before overwriting either document and keep dirty draft");
            var collision = new ScenarioMapDraft(SaveSystem.ExportScenarioJson(c, authored.Id), c.ProvinceMap.ProvinceCount, authored.Id);
            rejected = false; try { collision.Save(root); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && !collision.HasSavedVersion && File.ReadAllText(worldPath) == untouched, "new draft cannot overwrite an existing scenario ID");
            var locked = ScenarioMapDraft.OpenForEditing(authoredEntry, c.ProvinceMap.ProvinceCount, root, "unused_copy");
            locked.UpdateSettings(locked.Settings with { Name = "Read-only edit" });
            File.SetAttributes(manifestPath, File.GetAttributes(manifestPath) | FileAttributes.ReadOnly);
            try
            {
                rejected = false; try { locked.Save(root); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && File.ReadAllText(worldPath) == untouched, "read-only manifest cannot partially update world document");
            }
            finally { File.SetAttributes(manifestPath, File.GetAttributes(manifestPath) & ~FileAttributes.ReadOnly); }
            var batchRoot = Path.Combine(root, "common", "batch_fixture"); Directory.CreateDirectory(batchRoot);
            var first = Path.Combine(batchRoot, "first.json"); var blocked = Path.Combine(batchRoot, "blocked.json");
            File.WriteAllText(first, "{\"old\":1}"); File.WriteAllText(first + ".bak", "previous backup");
            // A directory with a JSON filename forces the second atomic publication to
            // fail after the first succeeded, exercising real rollback (no test hook).
            Directory.CreateDirectory(blocked);
            rejected = false;
            try
            {
                ContentWorkbench.WriteBatch(root,
                    new System.Collections.Generic.Dictionary<string, string> { ["common/batch_fixture/first.json"] = "{\"new\":2}", ["common/batch_fixture/blocked.json"] = "{}" },
                    new System.Collections.Generic.Dictionary<string, string> { ["common/batch_fixture/first.json"] = "{\"old\":1}", ["common/batch_fixture/blocked.json"] = null });
            }
            catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(first) == "{\"old\":1}" && File.ReadAllText(first + ".bak") == "previous backup",
                "failure on second document rolls back first document and its previous backup exactly");
            Check(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "failed and successful saves leave no staged temporary files");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
