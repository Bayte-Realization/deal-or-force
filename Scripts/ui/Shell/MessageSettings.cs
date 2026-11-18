using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly List<NewsFeed.Line> _shellRecentNews = new();
    private PanelContainer _shellRecentNewsPanel;
    private VBoxContainer _shellRecentNewsRows;
    private readonly Dictionary<ShellMessagePreset, Button> _shellPresetButtons = new();

    internal ShellMessagePreset CurrentShellMessagePreset =>
        ShellMessages.ParsePreset(SettingsCatalog.Chosen(ShellMessages.PresetSetting));

    internal ShellMessageDelivery ResolveShellMessage(ShellMessageEvent evt, bool involvesPlayer) =>
        ShellMessages.Resolve(CurrentShellMessagePreset, evt, involvesPlayer, _speed);

    private static ShellMessageEvent ClassifyNewsKind(string kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "war" or "war_declared" or "invasion" => ShellMessageEvent.WarDeclared,
        "ultimatum" or "demand" => ShellMessageEvent.UltimatumReceived,
        "defcon" or "nuclear_alert" => ShellMessageEvent.DefconChanged,
        "nuclear" or "nuclear_strike" => ShellMessageEvent.NuclearStrike,
        "peace" or "peace_treaty" or "truce" => ShellMessageEvent.PeaceOffer,
        "encirclement" => ShellMessageEvent.ArmyEncircled,
        "battle_lost" or "defeat" => ShellMessageEvent.BattleLost,
        "election" => ShellMessageEvent.ElectionSoon,
        "research" or "technology" => ShellMessageEvent.ResearchFinished,
        "shortage" => ShellMessageEvent.CommodityShortage,
        "construction" => ShellMessageEvent.ConstructionFinished,
        _ => ShellMessageEvent.WorldNews
    };

    private void ToggleShellRecentNewsModal()
    {
        if (_shellRecentNewsPanel is null || !IsInstanceValid(_shellRecentNewsPanel))
            BuildShellRecentNewsModal();

        _shellRecentNewsPanel!.Visible = !_shellRecentNewsPanel.Visible;
        if (_shellRecentNewsPanel.Visible)
        {
            _shellRecentNewsPanel.MoveToFront();
            RefreshShellRecentNewsModal();
        }
    }

    private void CloseShellRecentNewsModal()
    {
        if (_shellRecentNewsPanel is not null && IsInstanceValid(_shellRecentNewsPanel))
            _shellRecentNewsPanel.Visible = false;
    }

    private void BuildShellRecentNewsModal()
    {
        var panel = new PanelContainer
        {
            Name = "ShellRecentNewsModal",
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.AddThemeStyleboxOverride("panel", ShellPlateStyle(cornerPlate: true));
        AttachBrassCornerOverlay(panel, topLeftOnly: false);
        _newShellRoot.AddChild(panel);
        _shellRecentNewsPanel = panel;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        panel.AddChild(margin);

        var vBox = new VBoxContainer();
        vBox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vBox);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        vBox.AddChild(header);

        var title = new Label
        {
            Text = Tr("Recent world news"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.AddThemeFontOverride("font", ShellFont("shell.serif_bold"));
        title.AddThemeFontSizeOverride("font_size", 17);
        title.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        header.AddChild(title);

        var close = ActionButton("×", CloseShellRecentNewsModal, 32, 28);
        header.AddChild(close);

        // Quick preset bar: Calm (default) / Focus / Dispatch
        var presetRow = new HBoxContainer();
        presetRow.AddThemeConstantOverride("separation", 6);
        vBox.AddChild(presetRow);

        var presetLbl = new Label
        {
            Text = Tr("Message preset:"),
            VerticalAlignment = VerticalAlignment.Center
        };
        presetLbl.AddThemeFontOverride("font", ShellFont("shell.sans"));
        presetLbl.AddThemeFontSizeOverride("font_size", 12);
        presetLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        presetRow.AddChild(presetLbl);

        AddPresetBtn(presetRow, ShellMessagePreset.Calm, "calm", "Calm");
        AddPresetBtn(presetRow, ShellMessagePreset.Focus, "focus", "Focus (auto-pause)");
        AddPresetBtn(presetRow, ShellMessagePreset.Dispatch, "dispatch", "Dispatch (map feed)");

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(440, 260),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        vBox.AddChild(scroll);

        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 5);
        scroll.AddChild(rows);
        _shellRecentNewsRows = rows;
    }

    private void AddPresetBtn(HBoxContainer parent, ShellMessagePreset preset, string value, string caption)
    {
        var btn = ActionButton(caption, () =>
        {
            SettingsCatalog.Store(ShellMessages.PresetSetting, value);
            ApplySetting(ShellMessages.PresetSetting);
            RefreshShellRecentNewsModal();
        }, 0, 26);
        parent.AddChild(btn);
        _shellPresetButtons[preset] = btn;
    }

    private void RefreshShellRecentNewsModal()
    {
        if (_shellRecentNewsPanel is null || !_shellRecentNewsPanel.Visible) return;

        var activePreset = CurrentShellMessagePreset;
        foreach (var (preset, btn) in _shellPresetButtons)
            StyleButton(btn, preset == activePreset);

        if (_shellRecentNewsRows is null || _campaign is null) return;
        foreach (var child in _shellRecentNewsRows.GetChildren()) child.QueueFree();

        var lines = _shellRecentNews.Count > 0
            ? (IEnumerable<NewsFeed.Line>)_shellRecentNews
            : NewsFeed.Headlines(_campaign, 16);

        var any = false;
        foreach (var line in lines)
        {
            any = true;
            var captured = line;
            var rowBtn = new Button
            {
                Alignment = HorizontalAlignment.Left,
                CustomMinimumSize = new Vector2(0, 32),
                MouseDefaultCursorShape = CursorShape.PointingHand
            };
            rowBtn.AddThemeStyleboxOverride("normal", ShellPaperTagStyle());
            rowBtn.AddThemeStyleboxOverride("hover", ShellPaperTagStyle(hover: true));
            rowBtn.AddThemeFontOverride("font", ShellFont("shell.serif"));
            rowBtn.AddThemeFontSizeOverride("font_size", 13);
            rowBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
            rowBtn.Text = $"{line.Text}";
            rowBtn.ClipText = true;
            rowBtn.Pressed += () => { FocusNews(captured); CloseShellRecentNewsModal(); };
            _shellRecentNewsRows.AddChild(rowBtn);
        }

        if (!any)
        {
            var empty = new Label
            {
                Text = Tr("No dispatches recorded yet."),
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            empty.AddThemeFontOverride("font", ShellFont("shell.sans"));
            empty.AddThemeFontSizeOverride("font_size", 13);
            empty.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
            _shellRecentNewsRows.AddChild(empty);
        }

        var vp = GetViewportRect().Size;
        PlaceShellRect(_shellRecentNewsPanel, new ShellRect(
            MathF.Max(16f, vp.X - 500f),
            MathF.Min(132f, vp.Y - 380f),
            480f,
            340f));
    }
}
