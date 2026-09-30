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
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
