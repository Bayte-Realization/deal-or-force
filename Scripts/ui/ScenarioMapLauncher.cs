using System;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void OpenScenarioMapEditor()
    {
        if (_networkConnected || _isLoading || _campaign?.ProvinceMap is null) return;
        // Serialize before replacing the workshop: failure leaves its navigation intact.
        var id = "map_" + Guid.NewGuid().ToString("N")[..12];
        ScenarioMapDraft draft;
        try { draft = new ScenarioMapDraft(SaveSystem.ExportScenarioJson(_campaign, id), _campaign.ProvinceMap.ProvinceCount, id); }
        catch (Exception e) { GD.PushWarning("Map editor: " + e.Message); return; }
        var editor = GD.Load<PackedScene>("res://Scenes/ScenarioMapEditor.tscn").Instantiate<ScenarioMapEditor>();
        editor.Draft = draft; editor.Topology = _campaign.ProvinceMap; editor.ModRoot = AuthorRoot;
        editor.ExitRequested = OpenScenarioLibrary;
        var rows = AuthorWindow("Map & scenario editor", editor.RequestClose, "Back to scenarios");
        editor.SizeFlagsVertical = SizeFlags.ExpandFill; editor.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rows.AddChild(editor);
    }
}
