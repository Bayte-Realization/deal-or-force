using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _shellTopPlate;
    private PanelContainer _shellRailPlate;
    private Button _shellFlagButton;
    private Control _shellFlagInner;
    private TextureRect _shellFlagTexture;
    private Label _shellRankLabel;
    private HBoxContainer _shellStatRow;
    private VBoxContainer _shellRailRow;
    private PanelContainer _shellRailHoverTag;
    private Label _shellRailHoverTitle;
    private Label _shellRailHoverKey;

    private readonly Dictionary<string, ShellStatCell> _shellStats = new(StringComparer.Ordinal);
    private readonly List<ShellRailItem> _shellRailItems = new();
    private string _shellCornerLastTag = "";
    private (Campaign World, int Day, int Rev, int Rank) _shellRankCache;

    private sealed class ShellStatCell
    {
        public PanelContainer Box;
        public TextureRect Icon;
        public Label Value;
        public string LastText = "";
        public string LastColor = "";
    }

    private sealed class ShellRailItem
    {
        public string Screen;
        public string Label;
        public string Hotkey;
        public Button Button;
        public ColorRect ActiveBar;
        public Panel Badge;
    }

    private void BuildShellCorner()
    {
        var topPlate = new PanelContainer
        {
            Name = "ShellTopPlate",
            MouseFilter = MouseFilterEnum.Stop
        };
        topPlate.AddThemeStyleboxOverride("panel", ShellPlateStyle(cornerPlate: true));
        AttachBrassCornerOverlay(topPlate, topLeftOnly: true);
        _newShellRoot.AddChild(topPlate);
        _shellTopPlate = topPlate;

        var topMargin = new MarginContainer();
        topMargin.AddThemeConstantOverride("margin_left", 8);
        topMargin.AddThemeConstantOverride("margin_right", 10);
        topMargin.AddThemeConstantOverride("margin_top", 6);
        topMargin.AddThemeConstantOverride("margin_bottom", 6);
        topPlate.AddChild(topMargin);

        var topHBox = new HBoxContainer();
        topHBox.AddThemeConstantOverride("separation", 8);
        topMargin.AddChild(topHBox);

        // Recessed natural-aspect flag frame (Q -> Country)
        var flagBtn = new Button
        {
            Name = "ShellFlagButton",
            Flat = false,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        flagBtn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        flagBtn.AddThemeStyleboxOverride("hover", ShellWellStyle(true));
        flagBtn.AddThemeStyleboxOverride("pressed", ShellWellStyle(true));
        flagBtn.Pressed += () => ToggleCampaignScreen("Nation");
        flagBtn.MouseEntered += () => ShowShellRailHoverTag(Tr("Country"), "Q", flagBtn.GetGlobalRect().Position.Y + 4f, flagBtn.GetGlobalRect().End.X + 6f);
        flagBtn.MouseExited += HideShellRailHoverTag;
        topHBox.AddChild(flagBtn);
        _shellFlagButton = flagBtn;

        var flagInner = new Control
        {
            Name = "ShellFlagInner",
            MouseFilter = MouseFilterEnum.Ignore
        };
        flagInner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        flagBtn.AddChild(flagInner);
        _shellFlagInner = flagInner;

        var flagTex = new TextureRect
        {
            Name = "ShellFlagTexture",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        flagInner.AddChild(flagTex);
        _shellFlagTexture = flagTex;

        var statRow = new HBoxContainer
        {
            Name = "ShellStatRow",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        statRow.AddThemeConstantOverride("separation", 6);
        topHBox.AddChild(statRow);
        _shellStatRow = statRow;

        AddShellStatCell("Treasury", "icon_treasury", () => OpenScreen("State Affairs"));
        AddShellStatCell("GDP", "icon_gdp", () => OpenScreen("Economy"));
        AddShellStatCell("Debt", "icon_public_debt", OpenSovereignFinance);
        AddShellStatCell("Inflation", "icon_inflation", () => OpenScreen("State Affairs"));
        AddShellStatCell("Stability", "icon_stability", () => OpenScreen("Laws"));
        AddShellStatCell("Manpower", "icon_manpower", () => OpenRecruitment());
        AddShellStatCell("Capital", "icon_shell_capital", () => OpenScreen("Decisions"));
        AddShellStatCell("War", "icon_war", OpenWarLedger);

        // 64px vertical icon rail
        var railPlate = new PanelContainer
        {
            Name = "ShellRailPlate",
            MouseFilter = MouseFilterEnum.Stop
        };
        railPlate.AddThemeStyleboxOverride("panel", ShellPlateStyle(cornerPlate: true));
        _newShellRoot.AddChild(railPlate);
        _shellRailPlate = railPlate;

        var railMargin = new MarginContainer();
        railMargin.AddThemeConstantOverride("margin_left", 6);
        railMargin.AddThemeConstantOverride("margin_right", 6);
        railMargin.AddThemeConstantOverride("margin_top", 6);
        railMargin.AddThemeConstantOverride("margin_bottom", 6);
        railPlate.AddChild(railMargin);

        var railRow = new VBoxContainer { Name = "ShellRailRow" };
        railRow.AddThemeConstantOverride("separation", 4);
        railMargin.AddChild(railRow);
        _shellRailRow = railRow;

        // Dedicated rank plaque directly under the flag well (zero overlap with flag pixels)
        var rankPlate = new PanelContainer
        {
            Name = "ShellRankPlate",
            CustomMinimumSize = new Vector2(0, 22),
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        rankPlate.AddThemeStyleboxOverride("panel", ShellWellStyle(false));
        rankPlate.MouseEntered += () => rankPlate.AddThemeStyleboxOverride("panel", ShellWellStyle(true));
        rankPlate.MouseExited += () => rankPlate.AddThemeStyleboxOverride("panel", ShellWellStyle(false));
        rankPlate.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                PlayStrategicCue("ui_select");
                OpenCountryRankings(_campaign?.Player?.Id ?? -1);
                rankPlate.AcceptEvent();
            }
        };
        Tip(rankPlate, "World power rank — click to open Country Rankings.");
        railRow.AddChild(rankPlate);

        var rankBadge = new Label
        {
            Name = "ShellRankBadge",
            Text = "#1",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        rankBadge.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        rankBadge.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
        rankBadge.AddThemeFontSizeOverride("font_size", 11);
        rankBadge.AddThemeColorOverride("font_color", Ui.Colour("shell.brass_hi"));
        rankPlate.AddChild(rankBadge);
        _shellRankLabel = rankBadge;

        AddShellRailButton("State Affairs", "State Affairs", "E", "icon_state_affairs", () => ToggleCampaignScreen("State Affairs"));
        AddShellRailButton("Economy", "Economy", "R", "icon_economy", () => ToggleCampaignScreen("Economy"));
        AddShellRailButton("Trade", "Trade", "T", "icon_trade", () => ToggleCampaignScreen("Trade"));
        AddShellRailButton("Production", "Industry", "Y", "icon_industry", () => ToggleCampaignScreen("Production"));
        AddShellRailButton("Research", "Science", "U", "icon_research", () => ToggleCampaignScreen("Research"));
        AddShellRailButton("Military", "Army", "I", "icon_military", () => ToggleCampaignScreen("Military"));
        AddShellRailButton("Diplomacy", "Diplomacy", "O", "icon_diplomacy", () => ToggleCampaignScreen("Diplomacy"));
        AddShellRailButton("Wars", "Wars", "P", "icon_wars", () => ToggleCampaignScreen("Wars"));
        AddShellRailButton("Ledger", "Reports", "L", "icon_ledger", () => ToggleCampaignScreen("Ledger"));
        AddShellRailButton("Search", "Search", "Ctrl+K", "icon_search", ToggleNavigator);

        // Floating single slide-out hover tag
        var hoverTag = new PanelContainer
        {
            Name = "ShellRailHoverTag",
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore
        };
        hoverTag.AddThemeStyleboxOverride("panel", ShellPaperTagStyle(active: false));
        _newShellRoot.AddChild(hoverTag);
        _shellRailHoverTag = hoverTag;

        var hm = new MarginContainer();
        hm.AddThemeConstantOverride("margin_left", 10);
        hm.AddThemeConstantOverride("margin_right", 10);
        hm.AddThemeConstantOverride("margin_top", 5);
        hm.AddThemeConstantOverride("margin_bottom", 5);
        hoverTag.AddChild(hm);

        var hRow = new HBoxContainer();
        hRow.AddThemeConstantOverride("separation", 8);
        hm.AddChild(hRow);

        _shellRailHoverTitle = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.Off,
            VerticalAlignment = VerticalAlignment.Center
        };
        _shellRailHoverTitle.AddThemeFontOverride("font", ShellFont("shell.serif_semibold"));
        _shellRailHoverTitle.AddThemeFontSizeOverride("font_size", 14);
        _shellRailHoverTitle.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
        hRow.AddChild(_shellRailHoverTitle);

        _shellRailHoverKey = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.Off,
            VerticalAlignment = VerticalAlignment.Center
        };
        _shellRailHoverKey.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
        _shellRailHoverKey.AddThemeFontSizeOverride("font_size", 12);
        _shellRailHoverKey.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_muted"));
        hRow.AddChild(_shellRailHoverKey);
    }

    private void AddShellStatCell(string id, string iconName, Action onPress)
    {
        var box = new PanelContainer
        {
            Name = "ShellStat_" + id,
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        box.AddThemeStyleboxOverride("panel", ShellWellStyle(false));
        box.MouseEntered += () => box.AddThemeStyleboxOverride("panel", ShellWellStyle(true));
        box.MouseExited += () => box.AddThemeStyleboxOverride("panel", ShellWellStyle(false));
        box.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                PlayStrategicCue("ui_select");
                onPress();
                box.AcceptEvent();
            }
        };
        _shellStatRow.AddChild(box);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 5);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        box.AddChild(row);

        var pad = new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(pad);

        var icon = new TextureRect
        {
            Texture = ShellIconTexture(iconName),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.paper"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddChild(icon);

        var val = new Label
        {
            Text = "--",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        val.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
        val.AddThemeFontSizeOverride("font_size", 14);
        val.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(val);

        _shellStats[id] = new ShellStatCell { Box = box, Icon = icon, Value = val };
    }

    private void AddShellRailButton(string screen, string label, string hotkey, string iconName, Action onPress)
    {
        var btn = new Button
        {
            Name = "ShellRail_" + screen.Replace(" ", ""),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        btn.AddThemeStyleboxOverride("normal", ShellWellStyle(false));
        btn.AddThemeStyleboxOverride("hover", ShellPlateStyle(hover: true));
        btn.AddThemeStyleboxOverride("pressed", ShellWellStyle(true));
        btn.Pressed += () => { PlayStrategicCue("ui_select"); onPress(); RefreshShellRailHighlight(); };
        _shellRailRow.AddChild(btn);

        var icon = new TextureRect
        {
            Texture = ShellIconTexture(iconName),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.paper"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        icon.OffsetLeft = 10;
        icon.OffsetRight = -10;
        icon.OffsetTop = 10;
        icon.OffsetBottom = -10;
        btn.AddChild(icon);

        var activeBar = new ColorRect
        {
            Color = Ui.Colour("shell.paper"),
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore
        };
        activeBar.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        activeBar.OffsetRight = 3;
        btn.AddChild(activeBar);

        var badge = new Panel
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore
        };
        var badgeStyle = new StyleBoxFlat { BgColor = Ui.Colour("shell.state.warning") };
        badgeStyle.SetCornerRadiusAll(4);
        badge.AddThemeStyleboxOverride("panel", badgeStyle);
        badge.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
        badge.OffsetLeft = -11;
        badge.OffsetTop = 5;
        badge.OffsetRight = -5;
        badge.OffsetBottom = 11;
        btn.AddChild(badge);

        var item = new ShellRailItem
        {
            Screen = screen,
            Label = label,
            Hotkey = hotkey,
            Button = btn,
            ActiveBar = activeBar,
            Badge = badge
        };
        btn.MouseEntered += () => ShowShellRailHoverTag(Tr(item.Label), item.Hotkey, btn.GetGlobalRect().Position.Y + 4f,
            (_shellRailPlate?.GetGlobalRect().End.X ?? 80f) + 6f);
        btn.MouseExited += HideShellRailHoverTag;
        _shellRailItems.Add(item);
    }

    private void ShowShellRailHoverTag(string title, string hotkey, float topY, float leftX)
    {
        if (_shellRailHoverTag is null || !IsInstanceValid(_shellRailHoverTag)) return;
        _shellRailHoverTitle.Text = title;
        _shellRailHoverKey.Text = hotkey;
        _shellRailHoverTag.Visible = true;
        _shellRailHoverTag.ResetSize();
        var size = _shellRailHoverTag.GetCombinedMinimumSize();
        PlaceShellRect(_shellRailHoverTag, new ShellRect(leftX, topY, MathF.Max(110f, size.X), MathF.Max(30f, size.Y)));
    }

    private void HideShellRailHoverTag()
    {
        if (_shellRailHoverTag is not null && IsInstanceValid(_shellRailHoverTag))
            _shellRailHoverTag.Visible = false;
    }

    private void LayoutShellCorner(ShellLayoutProfile profile, bool atWar)
    {
        if (_shellTopPlate is null || _shellRailPlate is null) return;
        PlaceShellRect(_shellTopPlate, profile.TopPlate);
        PlaceShellRect(_shellRailPlate, profile.LeftRail);

        if (_shellFlagButton is not null)
            _shellFlagButton.CustomMinimumSize = new Vector2(profile.FlagWellWidth, profile.FlagWellHeight);

        foreach (var (key, cell) in _shellStats)
        {
            var visible = key != "War" || atWar;
            cell.Box.Visible = visible;
            cell.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cell.Box.CustomMinimumSize = new Vector2(0, profile.StatCellHeight);
            cell.Value.AddThemeFontSizeOverride("font_size", profile.Compact ? 12 : 14);
        }

        foreach (var item in _shellRailItems)
        {
            var sz = profile.RailButtonSize;
            item.Button.CustomMinimumSize = new Vector2(sz - 4f, sz - 4f);
        }

        FitShellFlag();
    }

    private void FitShellFlag()
    {
        if (_shellFlagTexture is null || _shellFlagButton is null) return;
        var tex = _shellFlagTexture.Texture;
        var maxW = MathF.Max(20f, _shellFlagButton.CustomMinimumSize.X - 6f);
        var maxH = MathF.Max(14f, _shellFlagButton.CustomMinimumSize.Y - 6f);
        var texW = tex is not null ? tex.GetWidth() : 3f;
        var texH = tex is not null ? tex.GetHeight() : 2f;
        var tag = _campaign?.Player?.Tag ?? "";
        var fitted = ShellLayout.FitFlag(texW, texH, maxW, maxH, tag);
        _shellFlagTexture.Position = new Vector2(3f + fitted.OffsetX, 3f + fitted.OffsetY);
        _shellFlagTexture.Size = new Vector2(fitted.Width, fitted.Height);
    }

    private void RefreshShellCorner()
    {
        var player = _campaign?.Player;
        if (player is null) return;

        if (!string.Equals(_shellCornerLastTag, player.Tag, StringComparison.Ordinal))
        {
            _shellCornerLastTag = player.Tag ?? "";
            _shellFlagTexture.Texture = FlagTexture(player.Tag);
            FitShellFlag();
            _shellFlagButton.TooltipText = Ui.Tip($"{player.DisplayName} [{player.Tag}] — {Tr("Country")} (Q)");
        }

        if (_shellRankCache.World != _campaign || _shellRankCache.Day != _campaign.ElapsedDays || _shellRankCache.Rev != _campaign.OwnershipRevision)
        {
            var rows = CountryRankings.Read(_campaign, CountryRankings.Weights.Default);
            var rank = rows.FirstOrDefault(r => r.NationId == player.Id)?.OverallRank ?? 0;
            _shellRankCache = (_campaign, _campaign.ElapsedDays, _campaign.OwnershipRevision, rank);
        }
        _shellRankLabel.Text = _shellRankCache.Rank > 0 ? $"#{_shellRankCache.Rank}" : "";

        SetShellStat("Treasury", Fmt.Money(player.Treasury),
            ShellStateColour(HudResourcePlate.StateOf("TREASURY", player.Treasury, player.Gdp, player.Population)),
            $"{Tr("Treasury")}: {Fmt.MoneyExact(player.Treasury)}\n{Tr("Net balance")}: {Fmt.MoneyDelta(player.WeeklyBalance)}");

        SetShellStat("GDP", Fmt.Money(player.Gdp), "shell.paper",
            $"{Tr("GDP")}: {Fmt.MoneyExact(player.Gdp)}");

        var debt = SovereignFinance.Outstanding(player);
        var debtShare = player.Gdp > 0f ? debt / player.Gdp : 0f;
        var debtColour = player.DebtCondition switch
        {
            DebtCondition.Default or DebtCondition.Insolvent => "shell.state.negative",
            DebtCondition.Stressed => "shell.state.warning",
            _ => "shell.paper"
        };
        SetShellStat("Debt", Fmt.Money(debt), debtColour,
            $"{Tr("Public debt")}: {Fmt.MoneyExact(debt)} ({Fmt.Percent(debtShare)} {Tr("of GDP")})");

        SetShellStat("Inflation", Fmt.Percent(player.Inflation),
            ShellStateColour(HudResourcePlate.StateOf("INFLATION", player.Inflation, player.Gdp, player.Population)),
            $"{Tr("Inflation")}: {Fmt.Percent(player.Inflation)}");

        SetShellStat("Stability", Fmt.Percent(player.Stability),
            ShellStateColour(HudResourcePlate.StateOf("STABILITY", player.Stability, player.Gdp, player.Population)),
            $"{Tr("Stability")}: {Fmt.Percent(player.Stability)}");

        SetShellStat("Manpower", Fmt.People(player.ManpowerReserve),
            ShellStateColour(HudResourcePlate.StateOf("MANPOWER", player.ManpowerReserve, player.Gdp, player.Population)),
            $"{Tr("Manpower reserve")}: {player.ManpowerReserve:N0}");

        SetShellStat("Capital", player.PoliticalPoints.ToString("0"), "shell.paper",
            $"{Tr("Political capital")}: {player.PoliticalPoints:0.#}");

        var wars = Warfare.OpenWars(_campaign).Where(w => !w.Ended && w.Involves(player.Id)).ToArray();
        var atWar = wars.Length > 0;
        if (_shellStats.TryGetValue("War", out var warCell))
            warCell.Box.Visible = atWar;
        if (atWar)
        {
            var mainWar = wars[0];
            var score = mainWar.IsAttacking(player.Id) ? mainWar.WarScore : -mainWar.WarScore;
            var warColour = score < -10f ? "shell.state.negative" : (score > 10f ? "shell.state.positive" : "shell.state.warning");
            SetShellStat("War", $"{score:+#;-#;0}%", warColour,
                $"{Tr("Active wars")}: {wars.Length} · {Tr("War score")}: {score:0.#}%");
        }

        RefreshShellRailHighlight();
    }

    private void SetShellStat(string id, string text, string colourToken, string tooltip)
    {
        if (!_shellStats.TryGetValue(id, out var cell)) return;
        if (!string.Equals(cell.LastText, text, StringComparison.Ordinal))
        {
            cell.LastText = text;
            cell.Value.Text = text;
        }
        if (!string.Equals(cell.LastColor, colourToken, StringComparison.Ordinal))
        {
            cell.LastColor = colourToken;
            cell.Value.AddThemeColorOverride("font_color", Ui.Colour(colourToken));
        }
        cell.Box.TooltipText = Ui.Tip(tooltip);
    }

    private static string ShellStateColour(HudResourcePlate.State state) => state switch
    {
        HudResourcePlate.State.Bad => "shell.state.negative",
        HudResourcePlate.State.Warning => "shell.state.warning",
        HudResourcePlate.State.Ok => "shell.state.positive",
        _ => "shell.paper"
    };

    private void RefreshShellRailHighlight()
    {
        var player = _campaign?.Player;
        var activeWars = player is not null && Warfare.OpenWars(_campaign).Any(w => !w.Ended && w.Involves(player.Id));
        var idleResearch = player is not null && _campaign.FeatureEnabled("research") && string.IsNullOrEmpty(player.CurrentResearch);

        foreach (var item in _shellRailItems)
        {
            if (item.Screen != "Search")
                item.Button.Visible = NavigationDestinationVisible(item.Screen) && NavigationDestinationEnabled(item.Screen);

            var active = !string.IsNullOrEmpty(_openScreen) && string.Equals(_openScreen, item.Screen, StringComparison.OrdinalIgnoreCase);
            item.ActiveBar.Visible = active;
            item.Button.AddThemeStyleboxOverride("normal", ShellWellStyle(active));
            item.Badge.Visible = (item.Screen == "Wars" && activeWars) || (item.Screen == "Research" && idleResearch);
        }
    }
}
