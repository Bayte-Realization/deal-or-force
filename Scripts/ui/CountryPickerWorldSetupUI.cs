using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly System.Collections.Generic.List<(string MapId, Button Button)> _pickerMapButtons = new();
    private Label _pickerWorldFormatSummary;
    private Button _pickerWorldFormatToggle;
    private VBoxContainer _pickerWorldFormatDrawer;
    private Label _pickerMapSummaryLabel;
    private HFlowContainer _pickerMapButtonsRow;

    private void BuildPickerWorldFormatCard(VBoxContainer actionStack, bool touchBtn)
    {
        var card = new PanelContainer { Name = "PickerWorldFormatCard", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", ChromePlateArt.BuildCardWrapperStyle(8, 5, 8, 5) ?? MenuChromeTheme.WellBox(8, 5, highlighted: true));
        actionStack.AddChild(card);

        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 5);
        card.AddChild(col);

        var header = new HBoxContainer
        {
            Name = "PickerWorldFormatHeader",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = Ui.Tip(Tr("picker.format.toggle_tip"))
        };
        header.AddThemeConstantOverride("separation", 6);
        header.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                PlayStrategicCue("ui_select");
                TogglePickerWorldFormatDrawer();
            }
        };
        col.AddChild(header);

        var worldCaption = Title("picker.format.short_label", 11, MenuChromeTheme.BrassHi);
        worldCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        worldCaption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        worldCaption.MouseFilter = Control.MouseFilterEnum.Ignore;
        header.AddChild(worldCaption);

        _pickerWorldFormatSummary = new Label
        {
            Name = "PickerWorldFormatSummary",
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        MenuChromeTheme.StyleExistingLabel(_pickerWorldFormatSummary, 12, MenuChromeTheme.Paper, tabular: true);
        header.AddChild(_pickerWorldFormatSummary);

        _pickerWorldFormatToggle = ActionButton("picker.format.change", TogglePickerWorldFormatDrawer,
            touchBtn ? 96 : 88, touchBtn ? 28 : 24);
        _pickerWorldFormatToggle.Name = "PickerWorldFormatToggle";
        _pickerWorldFormatToggle.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _pickerWorldFormatToggle.TooltipText = Ui.Tip(Tr("picker.format.toggle_tip"));
        MenuChromeTheme.StylePlateButton(_pickerWorldFormatToggle, false, 11);
        header.AddChild(_pickerWorldFormatToggle);

        _pickerWorldFormatDrawer = new VBoxContainer
        {
            Name = "PickerWorldFormatDrawer",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Visible = false
        };
        _pickerWorldFormatDrawer.AddThemeConstantOverride("separation", 5);
        col.AddChild(_pickerWorldFormatDrawer);

        _pickerWorldFormatDrawer.AddChild(MenuChromeTheme.CreateHairlineRule("PickerWorldFormatRule", 1f));

        _pickerMapButtonsRow = new HFlowContainer { Name = "PickerMapButtonsFlow", ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _pickerMapButtonsRow.AddThemeConstantOverride("h_separation", 5);
        _pickerMapButtonsRow.AddThemeConstantOverride("v_separation", 5);
        _pickerWorldFormatDrawer.AddChild(_pickerMapButtonsRow);

        _pickerMapButtons.Clear();
        var btnH = touchBtn ? 36 : 30;
        var btnMinW = touchBtn ? 148 : 138;
        foreach (var mapId in new[] { MapCatalog.StandardId, MapCatalog.TacticalId, MapCatalog.CompactId })
        {
            if (!MapCatalog.IsAvailable(mapId)) continue;
            var btn = ActionButton(BuiltInMapButtonText(mapId), () => SelectPickerMapPack(mapId), btnMinW, btnH);
            btn.Name = $"PickerMapBtn_{mapId}";
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.ClipText = true;
            btn.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            btn.TooltipText = Ui.Tip(BuiltInMapSummaryText(mapId));
            MenuChromeTheme.StylePlateButton(btn, false, 12);
            _pickerMapButtonsRow.AddChild(btn);
            _pickerMapButtons.Add((mapId, btn));
        }

        _pickerMapSelect = new OptionButton
        {
            Name = "PickerMapSelector",
            CustomMinimumSize = new Vector2(touchBtn ? 184 : 168, btnH),
            FitToLongestItem = false,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Visible = false
        };
        _pickerMapSelect.TooltipText = Ui.Tip(Tr("Choose which province subdivision pack is built for new campaigns."));
        MenuChromeTheme.StyleOptionButton(_pickerMapSelect);
        _pickerMapSelect.ItemSelected += idx => OnPickerMapSelected((int)idx);
        _pickerMapButtonsRow.AddChild(_pickerMapSelect);

        _pickerMapSummaryLabel = new Label
        {
            Name = "PickerMapSummaryLabel",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        MenuChromeTheme.StyleExistingLabel(_pickerMapSummaryLabel, 11, MenuChromeTheme.PaperDim);
        _pickerWorldFormatDrawer.AddChild(_pickerMapSummaryLabel);

        var tempoRow = new HBoxContainer { Name = "PickerTempoRow", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tempoRow.AddThemeConstantOverride("separation", 6);
        _pickerWorldFormatDrawer.AddChild(tempoRow);

        var tempoCaption = Title("TEMPO", 11, MenuChromeTheme.PaperFaint);
        tempoCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        tempoCaption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        tempoRow.AddChild(tempoCaption);

        _pickerTempoSelect = new OptionButton
        {
            Name = "PickerTempoSelector",
            CustomMinimumSize = new Vector2(touchBtn ? 240 : 220, touchBtn ? 36 : 30),
            FitToLongestItem = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _pickerTempoSelect.TooltipText = Ui.Tip(Tr("Choose how frequently the simulation steps and evaluates calm AI / economy cycles."));
        MenuChromeTheme.StyleOptionButton(_pickerTempoSelect);
        _pickerTempoSelect.ItemSelected += idx => OnPickerTempoSelected((int)idx);
        tempoRow.AddChild(_pickerTempoSelect);

        SyncPickerMapSelection();
        SyncPickerTempoSelection();
    }

    private void TogglePickerWorldFormatDrawer()
    {
        if (_pickerWorldFormatDrawer is null || !IsInstanceValid(_pickerWorldFormatDrawer)) return;
        var next = !_pickerWorldFormatDrawer.Visible;
        if (next) CollapsePickerAdvanced();
        SetPickerWorldFormatExpanded(next);
    }

    private void CollapsePickerWorldFormat() => SetPickerWorldFormatExpanded(false);

    private void SetPickerWorldFormatExpanded(bool expanded)
    {
        if (_pickerWorldFormatDrawer is null || !IsInstanceValid(_pickerWorldFormatDrawer)) return;
        _pickerWorldFormatDrawer.Visible = expanded;
        SyncPickerWorldFormatToggle();
        QueuePickerActionsLayout();
    }

    private void SyncPickerWorldFormatToggle()
    {
        if (_pickerWorldFormatToggle is null || !IsInstanceValid(_pickerWorldFormatToggle)) return;
        var expanded = _pickerWorldFormatDrawer is { Visible: true };
        var key = expanded ? "picker.format.hide" : "picker.format.change";
        var caption = Tr(key);
        _pickerWorldFormatToggle.Text = caption;
        _pickerWorldFormatToggle.SetMeta(LocSourceMeta, key);
        _pickerWorldFormatToggle.SetMeta(LocAppliedMeta, caption);
        _pickerWorldFormatToggle.TooltipText = Ui.Tip(Tr("picker.format.toggle_tip"));
        MenuChromeTheme.StylePlateButton(_pickerWorldFormatToggle, expanded, 11);
    }

    private void RefreshPickerWorldFormatSummary()
    {
        if (_pickerWorldFormatSummary is null || !IsInstanceValid(_pickerWorldFormatSummary)) return;
        var activeId = MapCatalog.ActiveMapId;
        var mapShort = BuiltInMapButtonText(activeId);
        if (string.Equals(mapShort, activeId, StringComparison.Ordinal) &&
            MapCatalog.TryGet(activeId, out var customEntry))
            mapShort = FormatMapPackLabel(customEntry);
        var tempoShort = ShortTempoLabel(SettingsCatalog.Chosen("sim_tempo_preset"));
        _pickerWorldFormatSummary.Text = string.IsNullOrEmpty(tempoShort)
            ? $"· {mapShort}"
            : $"· {mapShort} · {tempoShort}";
        SyncPickerWorldFormatToggle();
    }

    private static string ShortTempoLabel(string preset) => preset switch
    {
        "detailed" => Tr("tempo.short.detailed"),
        "balanced" => Tr("tempo.short.balanced"),
        "fast_mobile" => Tr("tempo.short.fast_mobile"),
        "grand_rts" => Tr("tempo.short.grand_rts"),
        _ => Tr("tempo.short.detailed")
    };

    private string BuiltInMapButtonText(string mapId) => mapId switch
    {
        MapCatalog.StandardId => Tr("Standard · 34k"),
        MapCatalog.TacticalId => Tr("Tactical · 18.5k"),
        MapCatalog.CompactId => Tr("Compact · 10.4k"),
        _ => mapId
    };

    private string BuiltInMapSummaryText(string mapId) => mapId switch
    {
        MapCatalog.CompactId => Tr("10,370 prov. · coarsened Siberia & poles · recommended for Android & fast runs"),
        MapCatalog.TacticalId => Tr("18,493 prov. · 45,386 edges · balanced front detail and speed"),
        MapCatalog.StandardId => Tr("34,138 prov. · maximum world detail · 100% legacy save compatibility"),
        _ => ""
    };

    private void SyncPickerMapSelection()
    {
        SyncMapPackSettingChoices();
        var activeId = MapCatalog.ActiveMapId;

        foreach (var (mapId, btn) in _pickerMapButtons)
        {
            if (!IsInstanceValid(btn)) continue;
            btn.Text = BuiltInMapButtonText(mapId);
            btn.TooltipText = Ui.Tip(BuiltInMapSummaryText(mapId));
            MenuChromeTheme.StylePlateButton(btn, string.Equals(mapId, activeId, StringComparison.Ordinal), 12);
        }

        if (_pickerMapSummaryLabel is not null && IsInstanceValid(_pickerMapSummaryLabel))
        {
            var builtInSummary = BuiltInMapSummaryText(activeId);
            if (builtInSummary.Length > 0) _pickerMapSummaryLabel.Text = builtInSummary;
            else if (MapCatalog.TryGet(activeId, out var customEntry))
            {
                var desc = customEntry.LocalizedDescription(Loc.Language);
                _pickerMapSummaryLabel.Text = !string.IsNullOrWhiteSpace(desc) ? desc : FormatMapPackLabel(customEntry);
            }
            else _pickerMapSummaryLabel.Text = "";
        }

        if (_pickerMapSelect is not null && IsInstanceValid(_pickerMapSelect))
        {
            _syncingPickerSelectors = true;
            try
            {
                _pickerMapSelect.Clear();
                var selectedIdx = 0;
                var customCount = 0;
                foreach (var entry in MapCatalog.AvailableMaps)
                {
                    if (!entry.IsPlayable || (entry.IsBuiltIn && !entry.IsMod &&
                        (entry.Id == MapCatalog.StandardId || entry.Id == MapCatalog.TacticalId || entry.Id == MapCatalog.CompactId)))
                        continue;
                    if (customCount == 0)
                    {
                        _pickerMapSelect.AddItem(Tr("Custom maps…"), 0);
                        _pickerMapSelect.SetItemDisabled(0, true);
                        _pickerMapSelect.SetItemMetadata(0, "");
                    }
                    customCount++;
                    var itemIdx = _pickerMapSelect.ItemCount;
                    _pickerMapSelect.AddItem(FormatMapPackLabel(entry), itemIdx);
                    _pickerMapSelect.SetItemMetadata(itemIdx, entry.Id);
                    if (string.Equals(entry.Id, activeId, StringComparison.Ordinal)) selectedIdx = itemIdx;
                }
                _pickerMapSelect.Visible = customCount > 0;
                if (_pickerMapSelect.Visible && _pickerMapSelect.ItemCount > 0) _pickerMapSelect.Selected = selectedIdx;
            }
            finally { _syncingPickerSelectors = false; }
        }
        RefreshPickerWorldFormatSummary();
        QueuePickerActionsLayout();
    }

    private void SyncPickerTempoSelection()
    {
        if (_pickerTempoSelect is null || !IsInstanceValid(_pickerTempoSelect)) return;
        _syncingPickerSelectors = true;
        try
        {
            _pickerTempoSelect.Clear();
            var def = SettingsCatalog.Of("sim_tempo_preset");
            var current = SettingsCatalog.Chosen("sim_tempo_preset");
            if (def is not null && def.Values.Count > 0)
            {
                var selectedIdx = 0;
                for (var i = 0; i < def.Values.Count; i++)
                {
                    var value = def.Values[i];
                    var rawLabel = i < def.Labels.Count && def.Labels[i].Length > 0 ? def.Labels[i] : value;
                    _pickerTempoSelect.AddItem(Tr(rawLabel), i);
                    _pickerTempoSelect.SetItemMetadata(i, value);
                    if (string.Equals(value, current, StringComparison.Ordinal)) selectedIdx = i;
                }
                if (_pickerTempoSelect.ItemCount > 0) _pickerTempoSelect.Selected = selectedIdx;
            }
        }
        finally { _syncingPickerSelectors = false; }
        RefreshPickerWorldFormatSummary();
    }

    private async void SelectPickerMapPack(string chosenId)
    {
        if (_syncingPickerSelectors || _isLoading || string.IsNullOrWhiteSpace(chosenId) ||
            string.Equals(chosenId, MapCatalog.ActiveMapId, StringComparison.Ordinal))
            return;
        var keptSettings = _campaign?.Settings is not null
            ? new System.Collections.Generic.Dictionary<string, string>(_campaign.Settings, StringComparer.Ordinal)
            : null;
        if (keptSettings is not null &&
            keptSettings.TryGetValue("sim_tempo_preset", out var keptPreset) &&
            !string.IsNullOrWhiteSpace(keptPreset))
            PersistTempoSelection(keptPreset);
        var previousId = MapCatalog.ActiveMapId;
        SettingsCatalog.Store("map_pack", chosenId);
        if (!MapCatalog.SetActiveMap(chosenId))
        {
            SettingsCatalog.Store("map_pack", previousId);
            SyncPickerMapSelection();
            return;
        }
        var rebuilt = _pickerScenarioEntry is null
            ? await RebuildCampaignWithLoading()
            : await RebuildAuthoredScenarioWithLoading(_pickerScenarioEntry);
        if (!rebuilt)
        {
            MapCatalog.SetActiveMap(previousId);
            SettingsCatalog.Store("map_pack", previousId);
            SyncPickerMapSelection();
            return;
        }
        if (keptSettings is not null && _campaign is not null)
        {
            foreach (var pair in keptSettings)
            {
                if (string.Equals(pair.Key, "map_pack", StringComparison.Ordinal)) continue;
                _campaign.Settings[pair.Key] = pair.Value;
            }
            ApplyAllSettings();
        }
        _pickerNation = null;
        _pickerSubheading.Text = _pickerHotseatMode
            ? Tr("Choose two or more countries · one shared world")
            : $"{_campaign.Date.Display()} · {Tr("Click the map or search")}";
        SetPickerNation(_campaign.Player, true);
        RebuildPickerResults();
        RefreshPickerSeedNote();
        SyncPickerMapSelection();
        SyncPickerTempoSelection();
    }

    private void OnPickerMapSelected(int index)
    {
        if (_syncingPickerSelectors || _isLoading || _pickerMapSelect is null ||
            index < 0 || index >= _pickerMapSelect.ItemCount) return;
        SelectPickerMapPack(_pickerMapSelect.GetItemMetadata(index).AsString());
    }

    private static void PersistTempoSelection(string chosenPreset)
    {
        var (hours, ecoDays, aiDays) = chosenPreset switch
        {
            "balanced" => ("6", "7", "14"),
            "fast_mobile" => ("12", "14", "30"),
            "grand_rts" => ("24", "30", "60"),
            _ => ("1", "7", "7")
        };
        PlayerSettings.Set("sim_tempo_preset", chosenPreset);
        PlayerSettings.Set("hours_per_tick", hours);
        PlayerSettings.Set("economy_tick_days", ecoDays);
        PlayerSettings.Set("ai_calm_cadence", aiDays);
    }

    private void OnPickerTempoSelected(int index)
    {
        if (_syncingPickerSelectors || _pickerTempoSelect is null ||
            index < 0 || index >= _pickerTempoSelect.ItemCount) return;
        var chosenPreset = _pickerTempoSelect.GetItemMetadata(index).AsString();
        if (string.IsNullOrWhiteSpace(chosenPreset)) return;
        PersistTempoSelection(chosenPreset);
        ApplySettingChoice("sim_tempo_preset", chosenPreset);
        SyncPickerTempoSelection();
    }
}
