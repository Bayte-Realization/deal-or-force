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
    private readonly JsonObject _world;
    private readonly int[] _owners, _states, _controllers;
    private readonly float[] _occupation;
    private readonly Dictionary<int, Cell> _stroke = new();
    private readonly List<Edit> _undo = new(), _redo = new();
    private long _revision, _savedRevision, _nextRevision;
    private record Cell(int Owner, int State, int Controller, float Occupation);
    private record Edit(Dictionary<int, Cell> Before, Dictionary<int, Cell> After, long BeforeRevision, long AfterRevision);
    public Country[] Countries { get; }
    public Region[] Regions { get; }
    public int ProvinceCount => _owners.Length - 1;
    public bool Dirty => _stroke.Count > 0 || _revision != _savedRevision;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int StrokeCount => _stroke.Count;
    public string Id { get; }

    public ScenarioMapDraft(string json, int expectedProvinceCount, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 96 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
            throw new InvalidDataException("Scenario ID must contain only letters, numbers, underscores or hyphens.");
        // The same validator used by the game must accept a draft before it can be edited.
        SaveSystem.ImportScenarioJson(json, id, expectedProvinceCount);
        _world = JsonNode.Parse(json).AsObject(); Id = id;
        _owners = ReadArray<int>("ProvinceOwner"); _states = ReadArray<int>("ProvinceState");
        _controllers = ReadArray<int>("ProvinceController"); _occupation = ReadArray<float>("ProvinceOccupation");
        Countries = _world["Nations"].AsArray().Select(n => new Country(n["MapIndex"].GetValue<int>(),
            n["Tag"].GetValue<string>(), n["Name"].GetValue<string>(), n["Color"].GetValue<string>())).ToArray();
        Regions = _world["States"].AsArray().Select(n => new Region(n["MapIndex"].GetValue<int>(),
            n["NationMapIndex"].GetValue<int>(), n["Name"].GetValue<string>())).ToArray();
    }
    private T[] ReadArray<T>(string key) => _world[key].Deserialize<T[]>();
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
        _revision = e.BeforeRevision; _redo.Add(e);
    }
    public void Redo()
    {
        EndStroke(); if (!CanRedo) return;
        var e = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        foreach (var p in e.After) Put(p.Key, p.Value);
        _revision = e.AfterRevision; _undo.Add(e);
    }
    public string Export()
    {
        EndStroke(); _world["ScenarioId"] = Id; _world["IsPaused"] = true;
        _world["ProvinceOwner"] = JsonSerializer.SerializeToNode(_owners);
        _world["ProvinceState"] = JsonSerializer.SerializeToNode(_states);
        _world["ProvinceController"] = JsonSerializer.SerializeToNode(_controllers);
        _world["ProvinceOccupation"] = JsonSerializer.SerializeToNode(_occupation);
        _world["History"] = new JsonObject(); _world["Chronicle"] = new JsonArray();
        var territories = new JsonObject();
        var groups = Enumerable.Range(1, ProvinceCount).GroupBy(p => _owners[p]).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var c in Countries) territories[c.Tag] = JsonSerializer.SerializeToNode(groups.GetValueOrDefault(c.MapIndex, Array.Empty<int>()));
        _world["ScenarioTerritories"] = territories;
        var json = _world.ToJsonString(); SaveSystem.ImportScenarioJson(json, Id, ProvinceCount); return json;
    }
    public string Save(string modRoot, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Enter a scenario name.");
        // Validate both documents before publishing the discoverable manifest last.
        var world = Export();
        var manifest = JsonSerializer.Serialize(new { id = Id, name = name.Trim(), description = "Map editor scenario", world = "world.json" });
        ContentWorkbench.Validate($"scenarios/{Id}/scenario.json", manifest);
        ContentWorkbench.EnsureMod(modRoot);
        ContentWorkbench.Write(modRoot, $"scenarios/{Id}/world.json", world);
        var path = ContentWorkbench.Write(modRoot, $"scenarios/{Id}/scenario.json", manifest);
        _savedRevision = _revision; return path;
    }
}
