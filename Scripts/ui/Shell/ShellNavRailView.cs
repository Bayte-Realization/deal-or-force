using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// 64px vertical navigation rail along the left arm of the L-corner: 11 workspace doors grouped by
/// engraved hairline dividers, active left-underline well, informative counts/dots, and slide-out hover tag.
/// </summary>
internal sealed class ShellNavRailView
{
    private readonly ShellContext _ctx;
    private readonly List<RailItemNodes> _railItems = new();
    private readonly List<Control> _railRules = new();
    private readonly List<PanelContainer> _keycaps = new();

    private PanelContainer _hoverTag;
    private Control _hoverKeyBox;
    private Label _hoverTitle;
    private Label _hoverKey;
    private Label _hoverSubtitle;
    private ShellLayoutProfile _profile;
    private StyleBoxEmpty _emptyStyle;
    private (Campaign World, int Day, int PlayerId, int PpFloor, long ChronicleSeq, int Count) _decisionsCache;

    private sealed class RailItemNodes
    {
        public string Screen;
        public string TitleKey;
        public string SubKey;
        public string Hotkey;
        public Button Button;
        public TextureRect Icon;
        public PanelContainer Badge;
        public Label BadgeText;
        public PanelContainer Keycap;
        public int GroupBreakAfter;
        public bool? LastActive;
        public int LastBadgeCount = -1;
        public bool LastBadgeDot;
        public string LastLang = "";
    }

    public ShellNavRailView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _emptyStyle ??= new StyleBoxEmpty();
        var specs = new (string Screen, string Title, string Sub, string Key, string Icon, int BreakAfter)[]
        {
            ("State Affairs", "shell.rail_gov", "shell.sub_gov", "E", "icon_laws", 0),
            ("Decisions", "Decisions", "shell.sub_decisions", "", "icon_decisions", 1),
            ("Economy", "Economy", "shell.sub_eco", "R", "icon_economy", 0),
            ("Trade", "Trade", "shell.sub_trade", "T", "icon_trade", 0),
            ("Production", "Industry", "shell.sub_ind", "Y", "icon_industry", 0),
            ("Research", "Science", "shell.sub_sci", "U", "icon_research", 1),
            ("Military", "Army", "shell.sub_army", "I", "icon_military", 0),
            ("Diplomacy", "Diplomacy", "shell.sub_dip", "O", "icon_diplomacy", 0),
            ("Wars", "Wars", "shell.sub_wars", "P", "icon_wars", 1),
            ("Ledger", "Reports", "shell.sub_reports", "L", "icon_ledger", 1),
            ("Search", "Search", "shell.sub_search", "Ctrl+K", "icon_search", 0)
        };

        var hover = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.08f) };
        hover.SetCornerRadiusAll(3);
        foreach (var s in specs)
        {
            var capturedScreen = s.Screen;
            var btn = new Button
            {
                Name = "ShellRail_" + s.Screen.Replace(" ", "", StringComparison.Ordinal),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            btn.AddThemeStyleboxOverride("normal", _emptyStyle);
            btn.AddThemeStyleboxOverride("hover", hover);
            btn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle(underlineLeft: true));

            btn.Pressed += () =>
            {
                HideHoverTag(); _ctx.PlayCue?.Invoke("shell_plate_tap");
                if (capturedScreen == "Search") _ctx.ToggleNavigator?.Invoke();
                else _ctx.ToggleScreen?.Invoke(capturedScreen);
            };

            var icon = new TextureRect
            {
                Texture = _ctx.Icon(s.Icon),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ShellContext.ApplyIconRelief(icon, Ui.Colour("shell.icon"), 0.65f);
            btn.AddChild(icon);

            var badge = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            var badgeBox = new StyleBoxFlat
            {
                BgColor = Ui.Colour("shell.icon"),
                BorderColor = Ui.Colour("shell.border"),
                ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 0, ContentMarginBottom = 0
            };
            badgeBox.SetBorderWidthAll(1);
            badgeBox.SetCornerRadiusAll(9);
            badge.AddThemeStyleboxOverride("panel", badgeBox);

            var badgeLbl = new Label
            {
                Text = "1",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            badgeLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            badgeLbl.AddThemeFontSizeOverride("font_size", 11);
            badgeLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
            badge.AddChild(badgeLbl);
            btn.AddChild(badge);

            PanelContainer kbd = null;
            if (!string.IsNullOrEmpty(s.Key))
            {
                kbd = _ctx.CreateKeycapBadge(s.Key == "Ctrl+K" ? "/" : s.Key);
                btn.AddChild(kbd);
                _keycaps.Add(kbd);
            }

            var item = new RailItemNodes
            {
                Screen = s.Screen, TitleKey = s.Title, SubKey = s.Sub, Hotkey = s.Key,
                Button = btn, Icon = icon, Badge = badge, BadgeText = badgeLbl, Keycap = kbd,
                GroupBreakAfter = s.BreakAfter
            };
            btn.MouseEntered += () => ShowHoverTag(
                Loc.Get(item.TitleKey), item.Hotkey, Loc.Get(item.SubKey),
                btn.Position.Y + 2f, _profile.LeftRail.Right + 8f);
            btn.MouseExited += HideHoverTag;

            _ctx.Root.AddChild(btn);
            _railItems.Add(item);

            if (s.BreakAfter > 0)
            {
                var rule = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
                rule.Draw += () =>
                {
                    var w = rule.Size.X;
                    rule.DrawLine(new Vector2(0, 0), new Vector2(w, 0), Ui.Colour("shell.sep_dark"), 1f);
                    rule.DrawLine(new Vector2(0, 1), new Vector2(w, 1), Ui.Colour("shell.sep_light"), 1f);
                };
                _ctx.Root.AddChild(rule);
                _railRules.Add(rule);
            }
        }

        BuildHoverTag();
    }

    private void BuildHoverTag()
    {
        _hoverTag = new PanelContainer
        {
            Name = "ShellRailHoverTag",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 45
        };
        _hoverTag.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 12, 12, 8, 12, 9, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_hoverTag);

        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 3);
        _hoverTag.AddChild(col);

        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 10);
        col.AddChild(head);

        _hoverTitle = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _hoverTitle.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _hoverTitle.AddThemeFontSizeOverride("font_size", 15);
        _hoverTitle.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        head.AddChild(_hoverTitle);

        var keyBox = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var kbStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.paper"),
            ContentMarginLeft = 5, ContentMarginRight = 5, ContentMarginTop = 1, ContentMarginBottom = 1
        };
        kbStyle.SetCornerRadiusAll(2);
        keyBox.AddThemeStyleboxOverride("panel", kbStyle);
        head.AddChild(keyBox);
        _hoverKeyBox = keyBox;

        _hoverKey = new Label();
        _hoverKey.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _hoverKey.AddThemeFontSizeOverride("font_size", 11);
        _hoverKey.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
        keyBox.AddChild(_hoverKey);

        _hoverSubtitle = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(220, 0) };
        _hoverSubtitle.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _hoverSubtitle.AddThemeFontSizeOverride("font_size", 12);
        _hoverSubtitle.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        col.AddChild(_hoverSubtitle);
    }

    public void ShowHoverTag(string title, string hotkey, string subtitle, float globalY, float globalX)
    {
        _hoverTitle.Text = title;
        var hasKey = !string.IsNullOrEmpty(hotkey);
        _hoverKeyBox.Visible = hasKey;
        if (hasKey) _hoverKey.Text = hotkey;
        _hoverSubtitle.Text = subtitle;
        _hoverSubtitle.Visible = !string.IsNullOrWhiteSpace(subtitle);
        _hoverTag.ResetSize();
        _hoverTag.Position = new Vector2(globalX, globalY);
        _hoverTag.Visible = true;
        _hoverTag.MoveToFront();
    }

    public void HideHoverTag()
    {
        if (_hoverTag is not null) _hoverTag.Visible = false;
    }

    public void Layout(ShellLayoutProfile profile)
    {
        _profile = profile;
        var compact = profile.Compact;
        var curY = compact ? 92f : 136f;
        var btnH = compact ? 37f : 49f;
        var btnW = compact ? 42f : 56f;
        const float btnX = 4f;
        var iconDim = compact ? 20f : 26f;
        var gap = compact ? 2f : 3f;
        var ruleStep = compact ? 5f : 6f;
        var ruleIdx = 0;

        for (var i = 0; i < _railItems.Count; i++)
        {
            var it = _railItems[i];
            ShellContext.PlaceRect(it.Button, new ShellRect(btnX, curY, btnW, btnH));
            it.Icon.Position = new Vector2((btnW - iconDim) * 0.5f, (btnH - iconDim) * 0.5f);
            it.Icon.Size = new Vector2(iconDim, iconDim);
            it.Badge.Position = new Vector2(btnW - (compact ? 17f : 21f), compact ? 2f : 3f);
            if (it.Keycap is not null) it.Keycap.Position = new Vector2(btnW - 20f, btnH - 18f);

            curY += btnH + gap;
            if (it.GroupBreakAfter > 0 && ruleIdx < _railRules.Count)
            {
                var rule = _railRules[ruleIdx++];
                ShellContext.PlaceRect(rule, new ShellRect(compact ? 6f : 8f, curY + (compact ? 1f : 2f), profile.LeftRail.W - (compact ? 12f : 16f), 2f));
                rule.QueueRedraw();
                curY += ruleStep;
            }
        }
    }

    public void Refresh(Campaign world, Nation player, IReadOnlyList<StrategicWar> openWars, string openScreen)
    {
        var incomingDeals = 0;
        for (var i = 0; i < world.DealOffers.Count; i++)
        {
            var d = world.DealOffers[i];
            if (d.Pending && d.ToId == player.Id) incomingDeals++;
        }
        var pendingEvents = 0;
        for (var i = 0; i < world.PendingEvents.Count; i++)
            if (world.PendingEvents[i].NationId == player.Id) pendingEvents++;

        var researchIdle = string.IsNullOrEmpty(player.CurrentResearch);
        var idleArmies = 0;
        if (openWars.Count > 0)
        {
            for (var i = 0; i < world.Armies.Count; i++)
            {
                var a = world.Armies[i];
                if (a.NationId == player.Id && a.FrontLineId < 0 && a.AssignedWarId < 0 && !a.DirectOrder && !a.Training && !a.InBattle)
                    idleArmies++;
            }
        }
        var doctrineReady = player.ArmyExperience >= 20f || player.AirExperience >= 20f || player.NavalExperience >= 20f;
        var availDecisions = ResolveAvailableDecisionsCount(world, player);
        var lang = Loc.Language;
        _emptyStyle ??= new StyleBoxEmpty();

        for (var i = 0; i < _railItems.Count; i++)
        {
            var it = _railItems[i];
            var active = string.Equals(openScreen, it.Screen, StringComparison.Ordinal);
            if (it.LastActive != active)
            {
                it.LastActive = active;
                it.Button.AddThemeStyleboxOverride("normal", active ? _ctx.PressedWellStyle(underlineLeft: true) : _emptyStyle);
            }
            if (it.LastLang != lang)
            {
                it.LastLang = lang;
                var keySuffix = string.IsNullOrEmpty(it.Hotkey) ? "" : $" ({it.Hotkey})";
                it.Button.TooltipText = Ui.Tip(it.Screen == "Search"
                    ? HotkeyCatalog.TipWithShortcut("navigator", it.SubKey)
                    : $"{Loc.Get(it.TitleKey)}{keySuffix}\n{Loc.Get(it.SubKey)}");
            }

            var count = it.Screen switch
            {
                "Wars" => openWars.Count,
                "Diplomacy" => incomingDeals,
                "State Affairs" => pendingEvents,
                "Decisions" => availDecisions,
                "Military" when openWars.Count > 0 => idleArmies,
                _ => 0
            };
            var dot = (it.Screen == "Research" && researchIdle) || (it.Screen == "Military" && count == 0 && doctrineReady);
            if (it.LastBadgeCount != count || it.LastBadgeDot != dot)
            {
                it.LastBadgeCount = count;
                it.LastBadgeDot = dot;
                it.Badge.Visible = count > 0 || dot;
                if (count > 0)
                {
                    it.BadgeText.Visible = true;
                    it.BadgeText.Text = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
                    it.Badge.CustomMinimumSize = new Vector2(18, 18);
                }
                else if (dot)
                {
                    it.BadgeText.Visible = false;
                    it.Badge.CustomMinimumSize = new Vector2(9, 9);
                }
            }
        }
    }

    private int ResolveAvailableDecisionsCount(Campaign world, Nation player)
    {
        var ppFloor = (int)MathF.Floor(player.PoliticalPoints);
        var entries = world.Chronicle.Entries;
        var tailSeq = entries.Count > 0 ? entries[^1].Sequence : 0L;
        if (ReferenceEquals(_decisionsCache.World, world) &&
            _decisionsCache.Day == world.ElapsedDays &&
            _decisionsCache.PlayerId == player.Id &&
            _decisionsCache.PpFloor == ppFloor &&
            _decisionsCache.ChronicleSeq == tailSeq)
            return _decisionsCache.Count;

        var count = 0;
        var all = DecisionCatalog.All;
        for (var i = 0; i < all.Count; i++)
        {
            if (!DecisionCatalog.IsBlocked(world, player, all[i]))
                count++;
        }
        _decisionsCache = (world, world.ElapsedDays, player.Id, ppFloor, tailSeq, count);
        return count;
    }

    public void SyncKeycapVisibility(bool visible)
    {
        for (var i = 0; i < _keycaps.Count; i++)
        {
            var k = _keycaps[i];
            if (k.Visible != visible) k.Visible = visible;
        }
    }
}
