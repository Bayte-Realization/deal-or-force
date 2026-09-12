using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The campaign archive: as many saves as somebody cares to keep, in a folder they can find.
///
/// It used to be seven fixed slots - an autosave, a quicksave and five numbered ones - in
/// `user://save_games`, which on Windows is a folder five levels inside AppData that nobody
/// stumbles on. Both halves of that were wrong for a game meant to be played for a hundred
/// hours: a campaign is not something to overwrite because slot four was the least recently
/// used, and a save nobody can find is a save nobody can send to anybody.
///
/// So: any number of saves, each under a name of its own, listed newest first, in
/// `Documents/Deal or Force/save games`. The autosave and the quicksave keep reserved names,
/// because F5 has to land somewhere predictable.
///
/// The file is still named by a sanitised slug and the name a player typed lives in the
/// metadata beside it. That is not fussiness: names in this game arrive in Russian as often
/// as in English, and a file name is the one place that cannot be relied on to hold them.
/// </summary>
public partial class DealOrForceGame
{
    private Control _saveLoadLayer = null!;
    private Label _saveStatus = null!;
    private Label _saveFolderLine = null!;
    private LineEdit _saveNameField = null!;
    private VBoxContainer _saveList = null!;

    private string _saveRoot;

    /// <summary>
    /// Where the saves live, with the old folder emptied into it once.
    ///
    /// `Documents` is where a player looks and where a support request can point. If the
    /// system will not name a documents folder - it is allowed not to - this falls back to
    /// `user://save_games`, which is where saves have always been: never wrong, only hard
    /// to find.
    /// </summary>
    private string SaveRoot
    {
        get
        {
            if (!string.IsNullOrEmpty(_saveRoot)) return _saveRoot;

            var legacy = ProjectSettings.GlobalizePath("user://save_games");
            var documents = OS.GetSystemDir(OS.SystemDir.Documents);
            if (string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents))
            {
                _saveRoot = legacy;
                return _saveRoot;
            }

            // Godot hands back a path with forward slashes and Path.Combine adds backslashes,
            // so without this the folder is shown to the player as
            // `C:/Users/User/Documents\Deal or Force\save games`, which reads like a bug.
            var chosen = Path.GetFullPath(Path.Combine(documents, "Deal or Force", "save games"));
            try
            {
                Directory.CreateDirectory(chosen);
                MigrateSaves(legacy, chosen);
                _saveRoot = chosen;
            }
            catch (Exception exception)
            {
                // A documents folder that cannot be written to is a real situation - a
                // redirected profile, a locked-down machine - and not worth losing the
                // ability to save over.
                DiagnosticLogs.Write("errors", $"SAVE_ROOT_FALLBACK {exception.Message}");
                _saveRoot = legacy;
            }
            return _saveRoot;
        }
    }

    /// <summary>Copy what is already saved into the folder saves live in now, once.</summary>
    private static void MigrateSaves(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            // Never overwrite. If both folders hold a quicksave, the one already in the new
            // folder is the more recent decision.
            if (File.Exists(target)) continue;
            try { File.Copy(file, target); }
            catch (Exception exception)
            {
                DiagnosticLogs.Write("errors",
                    $"SAVE_MIGRATE_FAILED file={Path.GetFileName(file)} {exception.Message}");
            }
        }
    }

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
        var rows = ScrollablePanelRows(panel, 28, 24, 10);
        rows.AddChild(Title("CAMPAIGN ARCHIVE", 28, Ui.Colour("text.accent")));
        rows.AddChild(Title("As many campaigns as you care to keep · atomic compressed saves · F5 quicksaves, F9 loads it",
            13, Ui.Colour("text.muted")));

        var folderRow = new HBoxContainer();
        folderRow.AddThemeConstantOverride("separation", 8);
        rows.AddChild(folderRow);
        _saveFolderLine = RowLabel("", 11, Ui.Colour("text.faint"));
        _saveFolderLine.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        folderRow.AddChild(_saveFolderLine);
        var openFolder = ActionButton("Open folder", OpenSaveFolder, 128, 32);
        openFolder.TooltipText = Ui.Tip(Tr("Open the folder these are kept in, to copy a campaign somewhere or send one to somebody."));
        folderRow.AddChild(openFolder);

        var newRow = new HBoxContainer();
        newRow.AddThemeConstantOverride("separation", 8);
        rows.AddChild(newRow);
        _saveNameField = new LineEdit
        {
            PlaceholderText = Tr("Name this campaign…"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 34)
        };
        _saveNameField.TextSubmitted += _ => SaveAsNew();
        newRow.AddChild(_saveNameField);
        newRow.AddChild(ActionButton("Save", SaveAsNew, 110, 34, true));

        _saveStatus = Title("", 13, Ui.Colour("text.accent_dim"));
        rows.AddChild(_saveStatus);

        _saveList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _saveList.AddThemeConstantOverride("separation", 4);
        rows.AddChild(_saveList);

        rows.AddChild(ActionButton("Close", HideSaveLoadInterface, 160, 38));
    }

    private void OpenSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(SaveRoot);
            OS.ShellOpen(SaveRoot);
        }
        catch (Exception exception)
        {
            _saveStatus.Text = Simulation.Loc.Format("Could not open the folder: {0}", exception.Message);
        }
    }

    private void ShowSaveLoadInterface()
    {
        if (_campaign is not null) _campaign.IsPaused = true;
        if (_pause is not null) _pause.Text = Tr("▶");
        if (_pauseMenuLayer is not null) _pauseMenuLayer.Visible = false;
        if (_campaign?.Player is not null && string.IsNullOrWhiteSpace(_saveNameField.Text))
            _saveNameField.Text = $"{_campaign.Player.Name} · {_campaign.Date}";
        RefreshSaveSlots();
        _saveLoadLayer.Visible = true;
        _saveLoadLayer.MoveToFront();
    }

    private void HideSaveLoadInterface() => _saveLoadLayer.Visible = false;

    /// <summary>
    /// Rebuild the list from the folder.
    ///
    /// Rebuilt rather than updated, because how many saves there are is now whatever the
    /// player has made - and a fixed set of rows to update is exactly the assumption this
    /// screen was rewritten to stop making.
    /// </summary>
    private void RefreshSaveSlots()
    {
        _saveFolderLine.Text = SaveRoot;
        foreach (var child in _saveList.GetChildren())
        {
            _saveList.RemoveChild(child);
            child.QueueFree();
        }

        var saves = SaveSystem.List(SaveRoot);
        if (saves.Count == 0)
        {
            _saveList.AddChild(RowLabel("No campaigns saved yet.", 13, Ui.Colour("text.muted")));
            return;
        }

        foreach (var save in saves) AddSaveRow(save);
    }

    private void AddSaveRow(SaveMetadata save)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _saveList.AddChild(row);

        var title = save.Slot switch
        {
            SaveSystem.AutoSlot => Tr("AUTOSAVE"),
            SaveSystem.QuickSlot => Tr("QUICKSAVE"),
            _ => string.IsNullOrWhiteSpace(save.DisplayName) ? save.Slot : save.DisplayName
        };
        var info = RowLabel(Simulation.Loc.Format("{0}\n{1} · {2} · {3} · {4} KiB",
            title, save.NationName, save.GameDate,
            save.SavedUtc.ToLocalTime().ToString("g"),
            (save.CompressedBytes / 1024f).ToString("0")), 13, Ui.Colour("text.body_soft"));
        info.AutowrapMode = TextServer.AutowrapMode.Off;
        info.CustomMinimumSize = new Vector2(405, 42);
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(info);

        var slot = save.Slot;
        var kept = save.DisplayName;
        row.AddChild(ActionButton("Load", () => LoadFromSlot(slot), 84, 36));
        // Overwriting keeps the name it already had: a campaign saved over is the same
        // campaign later, and renaming it every time would turn the list into strangers.
        row.AddChild(ActionButton("Overwrite", () => SaveToSlot(slot, displayName: kept), 104, 36));
        row.AddChild(ActionButton("Delete", () => DeleteSave(slot), 84, 36));
    }

    /// <summary>
    /// Write a new save under a name of its own.
    ///
    /// The file gets a slug made from the clock and the typed name is kept in the metadata.
    /// Naming the file after what was typed would mean two campaigns called the same thing
    /// overwrite each other, and a campaign named in Russian getting a file name with
    /// nothing left in it.
    /// </summary>
    private void SaveAsNew()
    {
        if (_campaign?.Player is null) { _saveStatus.Text = Tr("No campaign."); return; }
        var name = _saveNameField.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = $"{_campaign.Player.Name} · {_campaign.Date}";
        SaveToSlot($"campaign_{DateTime.Now:yyyyMMdd_HHmmss}", displayName: name);
    }

    private void SaveToSlot(string slot, bool autosave = false, string displayName = null)
    {
        if (_campaign?.Player is null) return;
        try
        {
            var metadata = SaveSystem.Save(SaveRoot, slot, _campaign, ModFingerprint,
                string.IsNullOrWhiteSpace(displayName)
                    ? $"{_campaign.Player.Name} · {_campaign.Date}"
                    : displayName,
                autosave);
            _saveStatus.Text = Simulation.Loc.Format("Saved {0} on {1}.", metadata.NationName, metadata.GameDate);
            if (_saveLoadLayer is { Visible: true }) RefreshSaveSlots();
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
        // This is somebody's campaign now, so a new game started later has to build a new
        // world rather than carry this one into it.
        _campaignInProgress = true;

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
        _saveStatus.Text = string.IsNullOrWhiteSpace(result.Warning) ? Tr("Campaign restored.") : result.Warning;
        Refresh();
    }

    private void DeleteSave(string slot)
    {
        _saveStatus.Text = SaveSystem.Delete(SaveRoot, slot) ? Tr("Save deleted.") : Tr("Slot was already empty.");
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
