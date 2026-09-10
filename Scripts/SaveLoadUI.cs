using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private static readonly string[] ManualSaveSlots = { "slot_1", "slot_2", "slot_3", "slot_4", "slot_5" };
    private Control _saveLoadLayer = null!;
    private Label _saveStatus = null!;
    private readonly Dictionary<string, Label> _saveSlotLabels = new(StringComparer.OrdinalIgnoreCase);

    private string SaveRoot => ProjectSettings.GlobalizePath("user://save_games");
    private string ModFingerprint => string.Join(";", _modLoader.Packages.Select(package =>
        $"{package.Manifest.Id}@{package.Manifest.Version}"));

    private void BuildSaveLoadInterface()
    {
        _saveLoadLayer = FullLayer("SaveLoad");
        _saveLoadLayer.Visible = false;
        _saveLoadLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_saveLoadLayer);

        var veil = new ColorRect { Color = new Color(0.01f, 0.02f, 0.035f, 0.84f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _saveLoadLayer.AddChild(veil);

        var panel = TexturedPanel(_saveLoadLayer);
        Ui.Place(panel, "modal.save_load");
        var rows = PanelRows(panel, 28, 24, 10);
        rows.AddChild(Title("CAMPAIGN ARCHIVE", 28, Ui.Colour("text.accent")));
        rows.AddChild(Title("Atomic compressed saves · scenario/mod compatibility checks · F5 quicksave / F9 quickload",
            13, Ui.Colour("text.muted")));
        _saveStatus = Title("Choose a slot.", 13, Ui.Colour("text.accent_dim"));
        rows.AddChild(_saveStatus);

        AddSaveSlotRow(rows, SaveSystem.AutoSlot, "AUTOSAVE", false);
        AddSaveSlotRow(rows, SaveSystem.QuickSlot, "QUICKSAVE", true);
        for (var index = 0; index < ManualSaveSlots.Length; index++)
            AddSaveSlotRow(rows, ManualSaveSlots[index], $"CAMPAIGN {index + 1}", true);
        rows.AddChild(ActionButton("Close", HideSaveLoadInterface, 160, 38));
    }

    private void AddSaveSlotRow(Container parent, string slot, string title, bool allowSave)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        parent.AddChild(row);
        var info = Title(title, 13, Ui.Colour("text.body_soft"));
        info.CustomMinimumSize = new Vector2(405, 42);
        info.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(info);
        _saveSlotLabels[slot] = info;
        if (allowSave) row.AddChild(ActionButton("Save", () => SaveToSlot(slot), 84, 36, slot == SaveSystem.QuickSlot));
        row.AddChild(ActionButton("Load", () => LoadFromSlot(slot), 84, 36));
        row.AddChild(ActionButton("Delete", () => DeleteSave(slot), 84, 36));
    }

    private void ShowSaveLoadInterface()
    {
        if (_campaign is not null) _campaign.IsPaused = true;
        if (_pause is not null) _pause.Text = Tr("▶");
        if (_pauseMenuLayer is not null) _pauseMenuLayer.Visible = false;
        RefreshSaveSlots();
        _saveLoadLayer.Visible = true;
        _saveLoadLayer.MoveToFront();
    }

    private void HideSaveLoadInterface() => _saveLoadLayer.Visible = false;

    private void RefreshSaveSlots()
    {
        var saves = SaveSystem.List(SaveRoot).ToDictionary(item => item.Slot, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _saveSlotLabels)
        {
            var title = entry.Key switch
            {
                SaveSystem.AutoSlot => "AUTOSAVE",
                SaveSystem.QuickSlot => "QUICKSAVE",
                _ => $"CAMPAIGN {Array.IndexOf(ManualSaveSlots, entry.Key) + 1}"
            };
            entry.Value.Text = saves.TryGetValue(entry.Key, out var save)
                ? $"{title}\n{save.NationName} · {save.GameDate} · {save.SavedUtc.ToLocalTime():g} · {save.CompressedBytes / 1024f:0} KiB"
                : $"{title}\nEmpty slot";
        }
    }

    private void SaveToSlot(string slot, bool autosave = false)
    {
        if (_campaign?.Player is null) return;
        try
        {
            var metadata = SaveSystem.Save(SaveRoot, slot, _campaign, ModFingerprint,
                $"{_campaign.Player.Name} · {_campaign.Date}", autosave);
            _saveStatus.Text = Simulation.Loc.Format("Saved {0} on {1}.", metadata.NationName, metadata.GameDate);
            RefreshSaveSlots();
        }
        catch (Exception exception)
        {
            DiagnosticLogs.Write("errors", $"SAVE_EXCEPTION {exception}");
            _saveStatus.Text = Simulation.Loc.Format("Save failed: {0}", exception.Message);
            GD.PushError(_saveStatus.Text);
        }
    }

    private void LoadFromSlot(string slot)
    {
        var result = SaveSystem.Load(SaveRoot, slot, ModFingerprint);
        DiagnosticLogs.Write(result.Success ? "system" : "errors", $"LOAD_RESULT slot={slot} success={result.Success} detail={result.Warning}");
        if (!result.Success)
        {
            _saveStatus.Text = result.Warning;
            return;
        }
        if (_campaign is not null) _campaign.Changed -= Refresh;
        _campaign = result.Campaign;
        _campaign.Changed += Refresh;

        // Everything the interface was pointing at belonged to the campaign that just
        // ended. An army, a state or a screen carried across would be a live reference
        // into a dead object graph, so selection is dropped wholesale rather than remapped.
        _selectedNation = _campaign.Player;
        _selectedState = _campaign.States.FirstOrDefault(state => state.NationMapIndex == _campaign.Player.MapIndex);
        _pickerNation = _campaign.Player;
        _selectedArmy = null;
        _selectedArmies.Clear();
        _selectedProvinceId = 0;
        _openScreen = "";
        _globe.SelectArmy(-1);
        ClearDetail();
        ResetTrends();
        _lastAutosaveDay = -1;

        _modApi.Bind(_campaign);
        _globe.BindCampaign(_campaign);
        ResetStrategicAudioState();
        _menuLayer.Visible = false;
        _pickerLayer.Visible = false;
        _pauseMenuLayer.Visible = false;
        _saveLoadLayer.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_eventLayer is not null) _eventLayer.Visible = false;
        _hudLayer.Visible = true;
        _sidePanel.Visible = false;
        SetArmyPanelVisible(false);
        _pause.Text = _campaign.IsPaused ? "▶" : "II";
        StyleButton(_pause, _campaign.IsPaused);
        _saveStatus.Text = string.IsNullOrWhiteSpace(result.Warning) ? "Campaign restored." : result.Warning;
        Refresh();
    }

    private void DeleteSave(string slot)
    {
        _saveStatus.Text = SaveSystem.Delete(SaveRoot, slot) ? "Save deleted." : "Slot was already empty.";
        RefreshSaveSlots();
    }

    private void QuickSave() => SaveToSlot(SaveSystem.QuickSlot);
    private void QuickLoad() => LoadFromSlot(SaveSystem.QuickSlot);

    private void LoadLatestSave()
    {
        var latest = SaveSystem.List(SaveRoot).FirstOrDefault();
        if (latest is null) { ShowSaveLoadInterface(); _saveStatus.Text = Tr("No saves exist yet."); return; }
        LoadFromSlot(latest.Slot);
    }

    private void MaybeAutosave()
    {
        if (_campaign?.Defines.AutosaveIntervalDays is not > 0 || _campaign.ElapsedDays <= 0 ||
            _campaign.ElapsedDays == _lastAutosaveDay || _campaign.ElapsedDays % _campaign.Defines.AutosaveIntervalDays != 0)
            return;
        _lastAutosaveDay = _campaign.ElapsedDays;
        SaveToSlot(SaveSystem.AutoSlot, true);
    }
}
