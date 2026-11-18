using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _newShellRoot;
    private bool _newShellBuilt;
    private readonly Dictionary<string, FontVariation> _shellFontCache = new(StringComparer.Ordinal);
    private int _newShellRefreshDay = int.MinValue;
    private int _newShellRefreshRevision = int.MinValue;
    private Campaign _newShellWorld;
    private bool _newShellLastPaused;
    private int _newShellLastSpeed = -1;

    internal bool IsNewShellActive =>
        string.Equals(SettingsCatalog.Chosen("ui_shell"), "new", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ClassicChromeNodeNames =
    {
        "CampaignTopBar",
        "HudFlagMount",
        "HudResourceDrawer",
        "CampaignNavigation",
        "DatePlate",
        "AlertList",
        "CampaignAlertPopup",
        "Outliner",
        "SituationDesk",
        "SituationDeskTab",
        "MapControls",
        "MapModeDrawer",
        "ArmyBar",
        "TeletypeBand",
        "HeadlinePopups"
    };

    private Font ShellFont(string role, bool tabular = false)
    {
        var key = role + (tabular ? "|tnum" : "");
        if (_shellFontCache.TryGetValue(key, out var cached)) return cached;

        var baseFont = PresentationFonts.For(role);
        var variation = new FontVariation { BaseFont = baseFont };
        if (tabular)
        {
            var features = new Godot.Collections.Dictionary
            {
                ["tnum"] = 1,
                ["lnum"] = 1
            };
            variation.OpentypeFeatures = features;
        }
        _shellFontCache[key] = variation;
        return variation;
    }

    private static StyleBoxFlat ShellPlateStyle(bool cornerPlate = false, bool hover = false)
    {
        var bg = hover ? Ui.Colour("shell.plate_hover") : Ui.Colour("shell.plate_bottom");
        var border = cornerPlate ? Ui.Colour("shell.brass") : Ui.Colour("shell.border");
        var style = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ShadowColor = new Color(0f, 0f, 0f, 0.35f),
            ShadowSize = 4,
            ShadowOffset = new Vector2(0f, 2f)
        };
        var radius = (int)InterfaceSkin.Metric("shell.corner_radius", 3f);
        style.SetCornerRadiusAll(radius);
        style.SetContentMarginAll(0);
        return style;
    }

    private static StyleBoxFlat ShellWellStyle(bool active = false)
    {
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.well_top"),
            BorderColor = active ? Ui.Colour("shell.brass_hi") : Ui.Colour("shell.brass_shadow"),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        style.SetCornerRadiusAll(2);
        style.SetContentMarginAll(0);
        return style;
    }

    private static StyleBoxFlat ShellPaperTagStyle(bool active = false, bool hover = false)
    {
        var bg = hover ? Ui.Colour("shell.highlight") : Ui.Colour("shell.paper_tag_bottom");
        var style = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = active ? Ui.Colour("shell.player_blue_dark") : Ui.Colour("shell.paper_tag_edge"),
            BorderWidthLeft = active ? 3 : 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ShadowColor = new Color(0f, 0f, 0f, 0.25f),
            ShadowSize = 3,
            ShadowOffset = new Vector2(0f, 1f)
        };
        style.SetCornerRadiusAll(3);
        style.SetContentMarginAll(0);
        return style;
    }

    private static StyleBoxFlat ShellRoundButtonStyle(bool active = false, bool hover = false)
    {
        var bg = active ? Ui.Colour("shell.paper") : (hover ? Ui.Colour("shell.plate_hover") : Ui.Colour("shell.plate_top"));
        var border = active ? Ui.Colour("shell.brass_hi") : (hover ? Ui.Colour("shell.brass") : Ui.Colour("shell.border"));
        var style = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = active ? 2 : 1,
            BorderWidthTop = active ? 2 : 1,
            BorderWidthRight = active ? 2 : 1,
            BorderWidthBottom = active ? 2 : 1,
            ShadowColor = new Color(0f, 0f, 0f, 0.35f),
            ShadowSize = 3,
            ShadowOffset = new Vector2(0f, 2f)
        };
        style.SetCornerRadiusAll(24);
        style.SetContentMarginAll(0);
        return style;
    }

    private static void AttachBrassCornerOverlay(Control target, bool topLeftOnly = false)
    {
        if (target is null) return;
        target.Draw += () =>
        {
            var len = InterfaceSkin.Metric("shell.brass_corner_len", 14f);
            var thick = InterfaceSkin.Metric("shell.brass_corner_thick", 2f);
            var hi = Ui.Colour("shell.brass_hi");
            var w = target.Size.X;
            var h = target.Size.Y;
            target.DrawRect(new Rect2(0f, 0f, MathF.Min(len, w), thick), hi);
            target.DrawRect(new Rect2(0f, 0f, thick, MathF.Min(len, h)), hi);
            if (!topLeftOnly)
            {
                target.DrawRect(new Rect2(MathF.Max(0f, w - len), 0f, MathF.Min(len, w), thick), hi);
                target.DrawRect(new Rect2(MathF.Max(0f, w - thick), 0f, thick, MathF.Min(len, h)), hi);
                target.DrawRect(new Rect2(0f, MathF.Max(0f, h - thick), MathF.Min(len, w), thick), hi);
                target.DrawRect(new Rect2(MathF.Max(0f, w - len), MathF.Max(0f, h - thick), MathF.Min(len, w), thick), hi);
            }
        };
    }

    private static Texture2D ShellIconTexture(string spriteName)
    {
        var fallback = $"res://game/gfx/interface/icons/{spriteName}.png";
        var path = SpriteCatalog.TextureOf(spriteName, fallback);
        if (string.IsNullOrWhiteSpace(path) || !ResourceLoader.Exists(path)) return null;
        return GD.Load<Texture2D>(path);
    }

    private static void PlaceShellRect(Control control, ShellRect rect)
    {
        if (control is null) return;
        control.SetAnchorsPreset(LayoutPreset.TopLeft);
        control.OffsetLeft = rect.X;
        control.OffsetTop = rect.Y;
        control.OffsetRight = rect.Right;
        control.OffsetBottom = rect.Bottom;
        control.Position = new Vector2(rect.X, rect.Y);
        control.Size = new Vector2(rect.W, rect.H);
    }

    private void BuildNewShell()
    {
        if (_hudLayer is null || _newShellBuilt) return;
        _newShellBuilt = true;

        var root = new Control
        {
            Name = "NewCampaignShell",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            ProcessMode = ProcessModeEnum.Disabled
        };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _hudLayer.AddChild(root);
        _newShellRoot = root;

        // Place NewCampaignShell right after classic rest chrome (HeadlinePopups) and strictly below
        // side dossiers, full-screen workspaces, and modal layers so workspaces always cover it.
        var headlineNode = _hudLayer.FindChild("HeadlinePopups", true, false);
        if (headlineNode is not null)
        {
            var idx = Math.Min(_hudLayer.GetChildCount() - 1, headlineNode.GetIndex() + 1);
            _hudLayer.MoveChild(root, idx);
        }

        BuildShellCorner();
        BuildShellDatePlate();
        BuildShellAlertColumn();
        BuildShellMapBar();
        BuildShellStrip();
        ApplyUiShellSetting();
    }

    private void ApplyUiShellSetting()
    {
        if (_hudLayer is null) return;
        if (!_newShellBuilt) BuildNewShell();

        var isNew = IsNewShellActive;
        SetClassicChromeVisible(!isNew);

        if (_newShellRoot is not null && IsInstanceValid(_newShellRoot))
        {
            _newShellRoot.Visible = isNew;
            _newShellRoot.ProcessMode = isNew ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }

        if (_globe is not null)
        {
            if (!isNew) _globe.NewShellVisibleArmyIds = null;
        }

        if (isNew)
        {
            _newShellRefreshDay = int.MinValue;
            LayoutNewShell();
            RefreshNewShell(force: true);
        }
        else
        {
            CloseShellTransientPopovers();
            LayoutCampaignShell();
            Refresh();
        }
    }

    private void SetClassicChromeVisible(bool visible)
    {
        if (_hudLayer is null) return;
        foreach (var nodeName in ClassicChromeNodeNames)
        {
            var node = _hudLayer.FindChild(nodeName, true, false) as Control;
            if (node is null || !IsInstanceValid(node)) continue;
            node.Visible = visible && nodeName is not "MapModeDrawer" and not "HudResourceDrawer" and not "SituationDesk";
            node.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_headlineStack is not null && IsInstanceValid(_headlineStack))
        {
            _headlineStack.Visible = visible;
            _headlineStack.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_alertListPanel is not null && IsInstanceValid(_alertListPanel))
        {
            _alertListPanel.Visible = visible;
            _alertListPanel.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_outliner is not null && IsInstanceValid(_outliner))
        {
            _outliner.Visible = visible;
            _outliner.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_hudTopPanel is not null && IsInstanceValid(_hudTopPanel))
        {
            _hudTopPanel.Visible = visible;
            _hudTopPanel.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_hudDatePlate is not null && IsInstanceValid(_hudDatePlate))
        {
            _hudDatePlate.Visible = visible;
            _hudDatePlate.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_mapControlsPanel is not null && IsInstanceValid(_mapControlsPanel))
        {
            _mapControlsPanel.Visible = visible;
            _mapControlsPanel.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (_armyBar is not null && IsInstanceValid(_armyBar))
        {
            _armyBar.Visible = visible;
            _armyBar.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
        if (!visible && _mapModeDrawerLayer is not null && IsInstanceValid(_mapModeDrawerLayer))
            _mapModeDrawerLayer.Visible = false;
    }

    private void LayoutNewShell()
    {
        if (!_newShellBuilt || !IsNewShellActive || _hudLayer is null) return;
        var vpSize = _hudLayer.Size;
        if (vpSize.X < 320f || vpSize.Y < 240f) vpSize = GetViewportRect().Size;
        var atWar = _campaign?.Player is { } player &&
            Warfare.OpenWars(_campaign).Any(w => !w.Ended && w.Involves(player.Id));
        var profile = ShellLayout.ForViewport(vpSize.X, vpSize.Y, atWar);

        LayoutShellCorner(profile, atWar);
        LayoutShellDatePlate(profile);
        LayoutShellAlertColumn(profile);
        LayoutShellMapBar(profile);
        LayoutShellStrip(profile);
    }

    private void RefreshNewShell(bool force = false)
    {
        if (!_newShellBuilt || !IsNewShellActive || _campaign is null || _hudLayer is not { Visible: true }) return;

        var worldChanged = !ReferenceEquals(_newShellWorld, _campaign);
        if (worldChanged)
        {
            _newShellWorld = _campaign;
            ResetShellTransientCaches();
        }

        var pausedChanged = _newShellLastPaused != _campaign.IsPaused;
        var speedChanged = _newShellLastSpeed != _speed;
        var dayOrRevChanged = force || worldChanged
            || _newShellRefreshDay != _campaign.ElapsedDays
            || _newShellRefreshRevision != _campaign.MapRevision;

        _newShellLastPaused = _campaign.IsPaused;
        _newShellLastSpeed = _speed;

        if (dayOrRevChanged)
        {
            _newShellRefreshDay = _campaign.ElapsedDays;
            _newShellRefreshRevision = _campaign.MapRevision;
            LayoutNewShell();
            RefreshShellCorner();
            RefreshShellAlertColumn();
            RefreshShellStrip();
        }

        if (dayOrRevChanged || pausedChanged || speedChanged)
            RefreshShellDatePlate();

        RefreshShellMapBar();
    }

    private void TickNewShell(double delta)
    {
        if (!_newShellBuilt || !IsNewShellActive || _campaign is null || _hudLayer is not { Visible: true }) return;
        TickShellNewsAndHover(delta);
        TickShellCounterMerge(delta);
    }

    private void RefreshNewShellLocalised()
    {
        if (!_newShellBuilt) return;
        _newShellRefreshDay = int.MinValue;
        RefreshNewShell(force: true);
        RefreshShellMapBarLabels();
    }

    private void CloseShellTransientPopovers()
    {
        CloseShellMapMorePopover();
        CloseShellRecentNewsModal();
        HideShellRailHoverTag();
    }

    private void ResetShellTransientCaches()
    {
        _shellViewedAlerts.Clear();
        _shellAlertSnoozes.Clear();
        _shellRecentNews.Clear();
        _shellNewsSeenSeq = _campaign?.Chronicle.Entries.Count > 0 ? _campaign.Chronicle.Entries[^1].Sequence : 0;
        _shellActiveNewsCard = null;
        _shellNewsPinned = false;
        _shellCornerLastTag = "";
    }
}
