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
        MountScenarioMapEditor(draft);
    }

    private void OpenAuthoredScenarioMapEditor(ScenarioEntry entry, Label error)
    {
        if (_networkConnected || _isLoading || _campaign?.ProvinceMap is null) return;
        try
        {
            var copyId = "map_" + Guid.NewGuid().ToString("N")[..12];
            var draft = ScenarioMapDraft.OpenForEditing(entry, _campaign.ProvinceMap.ProvinceCount, AuthorRoot, copyId);
            MountScenarioMapEditor(draft);
        }
        catch (Exception e) { error.Text = Tr("Could not open scenario") + ": " + e.Message; }
    }

    private void MountScenarioMapEditor(ScenarioMapDraft draft)
    {
        var editor = GD.Load<PackedScene>("res://Scenes/ScenarioMapEditor.tscn").Instantiate<ScenarioMapEditor>();
        editor.Draft = draft; editor.Topology = _campaign.ProvinceMap; editor.ModRoot = AuthorRoot;
        // Over the map editor rather than instead of it: its draft stays open and unsaved work survives.
        editor.OpenFlagEditor = OpenFlagEditorWindow;
        editor.ExitRequested = () =>
        {
            if (draft.HasSavedVersion) _authorScenarioId = draft.Id;
            OpenScenarioLibrary();
        };
        var rows = AuthorWindow("Map & scenario editor", editor.RequestClose, "Back to scenarios");
        _authorMap = editor;
        editor.SizeFlagsVertical = SizeFlags.ExpandFill; editor.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rows.AddChild(editor);
    }
}
