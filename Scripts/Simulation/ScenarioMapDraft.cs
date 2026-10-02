using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Simulation;

/// <summary>Detached political-layer editor. Geometry is read-only; a stroke is one undo operation.</summary>
public sealed class ScenarioMapDraft
{
    public sealed record Country(int MapIndex, string Tag, string Name, string Color);
    public sealed record Region(int MapIndex, int Owner, string Name);
    public sealed record ScenarioSettings(string Name, string Description, int Year, int Month, int Day, int Hour, int Seed);
    private readonly JsonObject _world;
    private readonly int[] _owners, _states, _controllers;
    private readonly float[] _occupation;
    private readonly Dictionary<int, Cell> _stroke = new();
    private readonly List<Edit> _undo = new(), _redo = new();
    private long _revision, _savedRevision, _nextRevision;
    private record Cell(int Owner, int State, int Controller, float Occupation);
    private record Edit(Dictionary<int, Cell> Before, Dictionary<int, Cell> After, long BeforeRevision, long AfterRevision,
        string Collection = "", int Index = -1, JsonNode MetadataBefore = null, JsonNode MetadataAfter = null);
    public Country[] Countries { get; private set; }
    public Region[] Regions { get; private set; }
    public int ProvinceCount => _owners.Length - 1;
    public bool Dirty => _stroke.Count > 0 || _revision != _savedRevision;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int StrokeCount => _stroke.Count;
    public string Id { get; }
    public ScenarioSettings Settings { get; private set; }
    public bool HasSavedVersion { get; private set; }
    private JsonObject _manifest = new();
    private string _saveRoot, _savedWorld, _savedManifest;

    public ScenarioMapDraft(string json, int expectedProvinceCount, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 96 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
            throw new InvalidDataException("Scenario ID must contain only letters, numbers, underscores or hyphens.");
        // The same validator used by the game must accept a draft before it can be edited.
        SaveSystem.ImportScenarioJson(json, id, expectedProvinceCount);
        _world = JsonNode.Parse(json, documentOptions: GameDataLoader.ForgivingJson).AsObject(); Id = id;
        Settings = ReadSettings(new JsonObject { ["Name"] = "My scenario", ["Description"] = "",
            ["Date"] = _world["Date"].DeepClone(), ["Seed"] = _world["Seed"].DeepClone() });
        _owners = ReadArray<int>("ProvinceOwner"); _states = ReadArray<int>("ProvinceState");
        _controllers = ReadArray<int>("ProvinceController"); _occupation = ReadArray<float>("ProvinceOccupation");
        RefreshCatalogs();
    }
    /// <summary>Reopen a workspace document, or detach a base/mod document into a new copy.</summary>
    public static ScenarioMapDraft OpenForEditing(ScenarioEntry entry, int expectedProvinceCount, string modRoot, string copyId)
    {
        if (string.IsNullOrEmpty(entry.ManifestFile) || string.IsNullOrEmpty(entry.WorldFile))
            throw new InvalidDataException("This scenario is generated at startup. Create a copy of the current world instead.");
        var manifestText = File.ReadAllText(entry.ManifestFile);
        var manifest = JsonNode.Parse(manifestText, documentOptions: GameDataLoader.ForgivingJson).AsObject();
        var manifestWorld = ScenarioLibrary.SafePath(Path.GetDirectoryName(entry.ManifestFile), manifest["world"]?.GetValue<string>());
        if (manifest["id"]?.GetValue<string>() != entry.Id || !SamePath(manifestWorld, entry.WorldFile))
            throw new InvalidDataException("Scenario files changed. Reopen the scenario library before editing.");
        // Only the canonical document pair in our own workspace may be updated in place.
        // Other layouts and every base/mod scenario are copied, even when their IDs collide.
        var local = SamePath(entry.ManifestFile, ScenarioLibrary.SafePath(modRoot, $"scenarios/{entry.Id}/scenario.json")) &&
            SamePath(entry.WorldFile, ScenarioLibrary.SafePath(modRoot, $"scenarios/{entry.Id}/world.json"));
        var worldText = File.ReadAllText(entry.WorldFile);
        var draft = new ScenarioMapDraft(worldText, expectedProvinceCount, local ? entry.Id : copyId);
        var name = manifest["name"]?.GetValue<string>();
        var description = manifest["description"]?.GetValue<string>() ?? "";
        draft.UpdateSettings(draft.Settings with { Name = name, Description = description });
        draft._undo.Clear(); draft._redo.Clear(); draft._revision = draft._savedRevision = draft._nextRevision = 0;
        draft._manifest = manifest.DeepClone().AsObject();
        if (local)
        {
            draft.HasSavedVersion = true; draft._saveRoot = Path.GetFullPath(modRoot);
            draft._savedWorld = worldText; draft._savedManifest = manifestText;
        }
        return draft;
    }
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private void RefreshCatalogs()
    {
        Countries = _world["Nations"].AsArray().Select(n => new Country(n["MapIndex"].GetValue<int>(),
            n["Tag"].GetValue<string>(), n["Name"].GetValue<string>(), n["Color"].GetValue<string>())).ToArray();
        Regions = _world["States"].AsArray().Select(n => new Region(n["MapIndex"].GetValue<int>(),
            n["NationMapIndex"].GetValue<int>(), n["Name"].GetValue<string>())).ToArray();
    }
    private T[] ReadArray<T>(string key) => _world[key].Deserialize<T[]>();
    private static ScenarioSettings ReadSettings(JsonObject data) => new(data["Name"].GetValue<string>(),
        data["Description"].GetValue<string>(), data["Date"]["Year"].GetValue<int>(), data["Date"]["Month"].GetValue<int>(),
        data["Date"]["Day"].GetValue<int>(), data["Date"]["Hour"].GetValue<int>(), data["Seed"].GetValue<int>());
    private JsonObject SettingsSnapshot() => new() { ["Name"] = Settings.Name, ["Description"] = Settings.Description,
        ["Date"] = _world["Date"].DeepClone(), ["Seed"] = _world["Seed"].DeepClone(), ["RandomState"] = _world["RandomState"].DeepClone() };
    private void RestoreSettings(JsonObject data)
    {
        Settings = ReadSettings(data);
        foreach (var key in new[] { "Date", "Seed", "RandomState" }) _world[key] = data[key].DeepClone();
    }
    /// <summary>Change scenario presentation and clock without rebasing elapsed-day orders or touching the live game.</summary>
    public void UpdateSettings(ScenarioSettings settings)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Trim().Length > 160 ||
            (settings.Description?.Length ?? 0) > 4000 || settings.Seed <= 0 || settings.Hour is < 0 or > 23)
            throw new InvalidDataException("Enter a name (up to 160 characters), description (up to 4000), positive seed and valid hour.");
        try { _ = new DateTime(settings.Year, settings.Month, settings.Day); }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Choose a valid calendar date (years 1–9999)."); }
        settings = settings with { Name = settings.Name.Trim(), Description = settings.Description ?? "" };
        if (settings == Settings) return;
        EndStroke();
        var before = SettingsSnapshot();
        var after = before.DeepClone().AsObject();
        after["Name"] = settings.Name; after["Description"] = settings.Description;
        after["Date"]["Year"] = settings.Year; after["Date"]["Month"] = settings.Month;
        after["Date"]["Day"] = settings.Day; after["Date"]["Hour"] = settings.Hour;
        after["Seed"] = settings.Seed;
        // Renaming or moving the calendar must not reset an already advanced random stream.
        if (settings.Seed != Settings.Seed) after["RandomState"] = new SimRandom(settings.Seed).State;
        RestoreSettings(after);
        try { Export(); }
        catch { RestoreSettings(before); throw; }
        var next = ++_nextRevision;
        _undo.Add(new(new(), new(), _revision, next, "$scenario", -1, before, after.DeepClone()));
        if (_undo.Count > 128) _undo.RemoveAt(0);
        _revision = next; _redo.Clear();
    }
    public string ReadEntity(bool country, int mapIndex)
    {
        var list = _world[country ? "Nations" : "States"].AsArray();
        var entity = list.FirstOrDefault(n => n["MapIndex"].GetValue<int>() == mapIndex)
            ?? throw new InvalidDataException("Select an existing country or region.");
        return entity.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    /// <summary>Edit detached scenario data, with bounded per-entity undo and the game's import validation.</summary>
    public void ReplaceEntity(bool country, int mapIndex, string json)
    {
        EndStroke();
        var collection = country ? "Nations" : "States";
        var list = _world[collection].AsArray();
        var index = Enumerable.Range(0, list.Count).FirstOrDefault(i => list[i]["MapIndex"].GetValue<int>() == mapIndex, -1);
        if (index < 0) throw new InvalidDataException("Select an existing country or region.");
        var before = list[index].DeepClone().AsObject();
        var after = ScenarioEntityDocument.Parse(json, country);
        ScenarioEntityDocument.ProtectStoredFields(before, after, country);
        if (JsonNode.DeepEquals(before, after)) return;
        list[index] = after;
        try { Export(); }
        catch { list[index] = before; throw; }
        var next = ++_nextRevision;
        _undo.Add(new(new(), new(), _revision, next, collection, index, before, after.DeepClone()));
        if (_undo.Count > 128) _undo.RemoveAt(0);
        _revision = next; _redo.Clear(); RefreshCatalogs();
    }
    public int OwnerAt(int p) => p > 0 && p <= ProvinceCount ? _owners[p] : 0;
    public int StateAt(int p) => p > 0 && p <= ProvinceCount ? _states[p] : 0;
    private Cell Read(int p) => new(_owners[p], _states[p], _controllers[p], _occupation[p]);
    private void Put(int p, Cell c) { _owners[p] = c.Owner; _states[p] = c.State; _controllers[p] = c.Controller; _occupation[p] = c.Occupation; }
    public bool Paint(int province, int owner, int state)
    {
        if (province <= 0 || province > ProvinceCount) return false;
        if (!Countries.Any(c => c.MapIndex == owner) || !Regions.Any(r => r.MapIndex == state && r.Owner == owner))
            throw new InvalidDataException("Choose a region belonging to the target country.");
        var before = Read(province); var after = new Cell(owner, state, owner, 0);
        if (before == after) return false;
        _stroke.TryAdd(province, before); Put(province, after); return true;
    }
    public void EndStroke()
    {
        var changed = _stroke.Where(p => p.Value != Read(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        _stroke.Clear(); if (changed.Count == 0) return;
        var next = ++_nextRevision;
        _undo.Add(new(changed, changed.Keys.ToDictionary(p => p, Read), _revision, next));
        if (_undo.Count > 128) _undo.RemoveAt(0);
        _revision = next; _redo.Clear();
    }
    public void CancelStroke() { foreach (var p in _stroke) Put(p.Key, p.Value); _stroke.Clear(); }
    public void Undo()
    {
        EndStroke(); if (!CanUndo) return;
        var e = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        foreach (var p in e.Before) Put(p.Key, p.Value);
        if (e.Index >= 0) { _world[e.Collection].AsArray()[e.Index] = e.MetadataBefore.DeepClone(); RefreshCatalogs(); }
        else if (e.Collection == "$scenario") RestoreSettings(e.MetadataBefore.AsObject());
        _revision = e.BeforeRevision; _redo.Add(e);
    }
    public void Redo()
    {
        EndStroke(); if (!CanRedo) return;
        var e = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        foreach (var p in e.After) Put(p.Key, p.Value);
        if (e.Index >= 0) { _world[e.Collection].AsArray()[e.Index] = e.MetadataAfter.DeepClone(); RefreshCatalogs(); }
        else if (e.Collection == "$scenario") RestoreSettings(e.MetadataAfter.AsObject());
        _revision = e.AfterRevision; _undo.Add(e);
    }
    public string Export()
    {
        EndStroke(); _world["ScenarioId"] = Id; _world["IsPaused"] = true;
        _world["ProvinceOwner"] = JsonSerializer.SerializeToNode(_owners);
        _world["ProvinceState"] = JsonSerializer.SerializeToNode(_states);
        _world["ProvinceController"] = JsonSerializer.SerializeToNode(_controllers);
        _world["ProvinceOccupation"] = JsonSerializer.SerializeToNode(_occupation);
        // Fresh campaign snapshots already reset history in ExportScenarioJson. Reopening
        // a hand-authored scenario must not silently discard its stored history/extensions.
        var territories = new JsonObject();
        var groups = Enumerable.Range(1, ProvinceCount).GroupBy(p => _owners[p]).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var c in Countries) territories[c.Tag] = JsonSerializer.SerializeToNode(groups.GetValueOrDefault(c.MapIndex, Array.Empty<int>()));
        _world["ScenarioTerritories"] = territories;
        var json = _world.ToJsonString(); SaveSystem.ImportScenarioJson(json, Id, ProvinceCount); return json;
    }
    public string Save(string modRoot, string name = null)
    {
        if (name is not null) UpdateSettings(Settings with { Name = name });
        if (HasSavedVersion && !SamePath(modRoot, _saveRoot))
            throw new InvalidDataException("The save destination changed. Reopen this scenario from its workspace.");
        // Validate both documents before publishing the discoverable manifest last.
        var world = Export();
        var metadata = _manifest.DeepClone().AsObject();
        metadata["id"] = Id; metadata["name"] = Settings.Name;
        metadata["description"] = Settings.Description; metadata["world"] = "world.json";
        var manifest = metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        ContentWorkbench.Validate($"scenarios/{Id}/scenario.json", manifest);
        ContentWorkbench.EnsureMod(modRoot);
        var worldPath = $"scenarios/{Id}/world.json"; var manifestPath = $"scenarios/{Id}/scenario.json";
        ContentWorkbench.WriteBatch(modRoot, new Dictionary<string, string> { [worldPath] = world, [manifestPath] = manifest },
            new Dictionary<string, string> { [worldPath] = _savedWorld, [manifestPath] = _savedManifest });
        _savedWorld = world; _savedManifest = manifest; _saveRoot = Path.GetFullPath(modRoot);
        HasSavedVersion = true; _savedRevision = _revision;
        return ScenarioLibrary.SafePath(modRoot, manifestPath);
    }
}
