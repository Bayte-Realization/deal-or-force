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
    private string SaveRoot => string.IsNullOrEmpty(_saveRoot) ? _saveRoot = ResolveSaveRoot() : _saveRoot;

    /// <summary>
    /// The folder itself, worked out without a campaign - so the main menu, which is a scene
    /// of its own with no campaign behind it, lists the same saves the game writes.
    /// </summary>
    internal static string ResolveSaveRoot()
    {
        var legacy = ProjectSettings.GlobalizePath("user://save_games");
        var documents = OS.GetSystemDir(OS.SystemDir.Documents);
        if (string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents)) return legacy;

        // Godot hands back a path with forward slashes and Path.Combine adds backslashes,
        // so without this the folder is shown to the player as
        // `C:/Users/User/Documents\Deal or Force\save games`, which reads like a bug.
        var chosen = Path.GetFullPath(Path.Combine(documents, "Deal or Force", "save games"));
        try
        {
            Directory.CreateDirectory(chosen);
            MigrateSaves(legacy, chosen);
            return chosen;
        }
        catch (Exception exception)
        {
            // A documents folder that cannot be written to is a real situation - a
            // redirected profile, a locked-down machine - and not worth losing the
            // ability to save over.
            DiagnosticLogs.Write("errors", $"SAVE_ROOT_FALLBACK {exception.Message}");
            return legacy;
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

        var panel = TexturedPanel(_saveLoadLayer, PanelSurface.Modal);
        Ui.Place(panel, "modal.save_load");
        var rows = ScrollablePanelRows(panel, 28, 24, 10);
        rows.AddChild(Title("CAMPAIGN ARCHIVE", 28, Ui.Colour("text.accent")));
        rows.AddChild(Title("Editable JSON saves by default · F5 quicksave · F9 quickload",
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
            _saveNameField.Text = $"{_campaign.Player.DisplayName} · {_campaign.Date.Display()}";
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
        var seedSuffix = save.Seed > 0 ? Simulation.Loc.Format(" · seed {0}", save.Seed) : "";
        var info = RowLabel(Simulation.Loc.Format("{0}\n{1} · {2} · {3} · {4} KiB",
            title, save.NationName + seedSuffix, save.GameDate,
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
        if (string.IsNullOrWhiteSpace(name)) name = $"{_campaign.Player.DisplayName} · {_campaign.Date.Display()}";
        SaveToSlot($"campaign_{DateTime.Now:yyyyMMdd_HHmmss}", displayName: name);
    }

    /// <summary>The save still being written, if one is. Completed when there is none.</summary>
    private System.Threading.Tasks.Task _pendingSave = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>
    /// Take the save on this frame and write it on a worker.
    ///
    /// The snapshot has to be taken here, because it reads the live world; compressing,
    /// checksumming and writing it do not, and they were most of the cost - the frame that
    /// autosaved a five-year campaign froze for close to three seconds, and every month the
    /// freeze was longer. Now the frame pays for the snapshot only.
    ///
    /// One write at a time. An autosave that falls due while the last save is still being
    /// written is skipped - the next is a month away - and a save the player asked for waits
    /// for it, so two writes never race for one file.
    /// </summary>
    private async void SaveToSlot(string slot, bool autosave = false, string displayName = null)
    {
        if (_campaign?.Player is null) return;
        if (!_pendingSave.IsCompleted)
        {
            if (autosave) return;
            WaitForPendingSave();
        }

        SaveSystem.PreparedSave prepared;
        try
        {
            prepared = SaveSystem.Prepare(SaveRoot, slot, _campaign, ModFingerprint,
                string.IsNullOrWhiteSpace(displayName)
                    ? $"{_campaign.Player.DisplayName} · {_campaign.Date.Display()}"
                    : displayName,
                autosave);
        }
        catch (Exception exception)
        {
            ReportSaveFailure(exception);
            return;
        }

        if (IsInstanceValid(_saveStatus)) _saveStatus.Text = Tr("Saving…");
        var write = System.Threading.Tasks.Task.Run(prepared.Commit);
        _pendingSave = write;
        try
        {
            var metadata = await write;
            if (!IsInstanceValid(_saveStatus)) return;
            _saveStatus.Text = Simulation.Loc.Format("Saved {0} on {1}.", metadata.NationName, metadata.GameDate);
            if (!autosave) PlayStrategicCue("save_complete");
            if (_saveLoadLayer is { Visible: true }) RefreshSaveSlots();
        }
        catch (Exception exception)
        {
            ReportSaveFailure(exception);
        }
    }

    private void ReportSaveFailure(Exception exception)
    {
        DiagnosticLogs.Write("errors", $"SAVE_EXCEPTION {exception}");
        var message = Simulation.Loc.Format("Save failed: {0}", exception.Message);
        if (IsInstanceValid(_saveStatus)) _saveStatus.Text = message;
        GD.PushError(message);
    }

    /// <summary>
    /// Let a save that is still being written finish. Before a load, which may be reading the
    /// file it is writing, and before the game closes, which would otherwise leave the player
    /// with the save they asked for a moment ago never written.
    /// </summary>
    private void WaitForPendingSave()
    {
        try { _pendingSave.Wait(TimeSpan.FromSeconds(30)); }
        catch (Exception) { /* SaveToSlot has already reported it */ }
    }

    private async void LoadFromSlot(string slot)
    {
        var restored = await RunLoadingOperation("Restoring a campaign", async () =>
        {
            await LoadingPhase("Reading and checking the save", 20);
            await RestoreCampaignFromSlot(slot);
            await WaitForMapPresentation();
        }, canReturn: true);

        // A load that did not happen used to leave the player at the main menu, because the
        // menu was a panel drawn over the world and it was still there underneath. It is a
        // scene of its own now, so there is nothing behind a refused load - and a globe with
        // no interface on it is not an answer to anything. Back to the front door.
        if (!restored && _hudLayer is { Visible: false } && _pickerLayer is { Visible: false })
            ShowMainMenu();
    }

    private async System.Threading.Tasks.Task RestoreCampaignFromSlot(string slot)
    {
        WaitForPendingSave();
        var result = SaveSystem.Load(SaveRoot, slot, ModFingerprint);
        DiagnosticLogs.Write(result.Success ? "system" : "errors", $"LOAD_RESULT slot={slot} success={result.Success} detail={result.Warning}");
        if (!result.Success)
        {
            _saveStatus.Text = result.Warning;
            throw new System.InvalidOperationException(result.Warning);
        }
        await LoadingPhase("Restoring countries, armies and orders", 70);
        // After replacing the live world, a binding failure is not safe to dismiss.
        _loadingCanReturn = false;
        if (_campaign is not null) _campaign.Changed -= Refresh;
        _campaign = result.Campaign;
        _pickerScenarioEntry = null;
        _campaignSeed = _campaign.Seed;
        if (_pickerSeed is not null) _pickerSeed.Text = _campaignSeed.ToString();
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
        // Flags are chosen against the world they fly in; left on the last one, a country born
        // in this one - a secession, a rising - has no flag, only an empty frame.
        BindFlags(_campaign);
        _globe.BindCampaign(_campaign);
        // The save's own step and autosave, not whatever the player has set since - and for
        // a save written before campaigns kept their options, the player's current ones,
        // which the first read writes into it.
        foreach (var definition in SettingsCatalog.All)
            if (definition.Campaign) ApplySetting(definition.Id);
        ResetStrategicAudioState();
        _menuLayer.Visible = false;
        _pickerLayer.Visible = false;
        _pauseMenuLayer.Visible = false;
        _saveLoadLayer.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_researchLayer is not null) _researchLayer.Visible = false;
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

    private ulong _lastAutosaveMsec;

    /// <summary>
    /// Save when the calendar says so, but never twice inside
    /// <see cref="Defines.AutosaveMinimumSeconds"/> of real time. The first one of a session
    /// falls on the calendar's own day - the thirtieth, the sixtieth; after that it is the
    /// interval since the last one, so a save the clock held back is taken as soon as it may
    /// be rather than skipped until the calendar comes round again.
    /// </summary>
    private void MaybeAutosave()
    {
        var interval = _campaign?.Defines.AutosaveIntervalDays ?? 0;
        if (interval <= 0 || _campaign.ElapsedDays <= 0 || _campaign.ElapsedDays == _lastAutosaveDay) return;
        var due = _lastAutosaveDay < 0
            ? _campaign.ElapsedDays % interval == 0
            : _campaign.ElapsedDays - _lastAutosaveDay >= interval;
        if (!due) return;
        var now = Time.GetTicksMsec();
        var gap = (ulong)(Math.Max(0f, _campaign.Defines.AutosaveMinimumSeconds) * 1000f);
        if (_lastAutosaveMsec > 0 && now - _lastAutosaveMsec < gap) return;
        _lastAutosaveDay = _campaign.ElapsedDays;
        _lastAutosaveMsec = now;
        SaveToSlot(SaveSystem.AutoSlot, true);
    }
}
