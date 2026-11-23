using System;
using System.Globalization;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Permanent, prominent Diplomatic Inbox bar anchored at the top of the right-hand HUD column
/// directly beneath the Date/DEFCON plate so incoming deals, ultimatums, alliances and requests
/// are always one click away regardless of active alerts.
/// </summary>
internal sealed class ShellInboxBarView
{
    private readonly ShellContext _ctx;

    private VBoxContainer _container;
    private Button _button;
    private TextureRect _icon;
    private Label _titleLabel;
    private PanelContainer _badge;
    private StyleBoxFlat _badgeStyle;
    private Label _badgeLabel;
    private StyleBoxFlat _idleNormal;
    private StyleBoxFlat _pendingNormal;
    private StyleBoxFlat _hoverStyle;

    private int _lastCount = -1;
    private string _lastLang = "";
    private bool _lastCompact;

    public int LastCount => _lastCount < 0 ? 0 : _lastCount;
    public bool IsVisible => _container is { Visible: true };
    public static float HeightForLayout(bool compact) => compact ? 36f : 42f;

    public ShellInboxBarView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build(VBoxContainer parent)
    {
        _container = new VBoxContainer
        {
            Name = "ShellInboxBar",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        _container.AddThemeConstantOverride("separation", 0);
        parent.AddChild(_container);

        _idleNormal = Ui.ChromeBox("shell.plate_top", 0.56f, "shell.engrave", 0.72f, cornerRadius: 2, borderWidth: 1, padX: 10, padY: 3);
        _pendingNormal = Ui.ChromeBox("shell.state.warning", 0.18f, "shell.state.warning", 0.78f, cornerRadius: 2, borderWidth: 1, padX: 10, padY: 3, leftBorderWidth: 3);
        _hoverStyle = Ui.ChromeBox("shell.plate_hover", 0.88f, "shell.brass_hi", 0.84f, cornerRadius: 2, borderWidth: 1, padX: 10, padY: 3);

        _button = new Button
        {
            Name = "ShellDiplomaticInboxButton",
            CustomMinimumSize = new Vector2(0, 34),
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("shell.inbox_tooltip"))
        };
        _button.AddThemeStyleboxOverride("normal", _idleNormal);
        _button.AddThemeStyleboxOverride("hover", _hoverStyle);
        _button.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle(underlineLeft: true));
        _button.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _ctx.OpenDealInbox?.Invoke();
        };
        _container.AddChild(_button);

        var row = new HBoxContainer
        {
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddThemeConstantOverride("separation", 8);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 10;
        row.OffsetRight = -10;
        _button.AddChild(row);

        _icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = _ctx.Icon("icon_shell_treaty"),
            CustomMinimumSize = new Vector2(18, 18),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ShellContext.ApplyIconRelief(_icon, Ui.Colour("shell.icon"), 0.55f);
        row.AddChild(_icon);

        _titleLabel = new Label
        {
            Text = Loc.Get("Diplomatic inbox"),
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _titleLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _titleLabel.AddThemeFontSizeOverride("font_size", 14);
        _titleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(_titleLabel);

        _badgeStyle = Ui.ChromeBox("shell.well", 1f, "shell.engrave", 0.85f, cornerRadius: 9, borderWidth: 1, padX: 7, padY: 1);

        _badge = new PanelContainer
        {
            Name = "ShellDiplomaticInboxBadge",
            ClipContents = true,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _badge.AddThemeStyleboxOverride("panel", _badgeStyle);
        row.AddChild(_badge);

        _badgeLabel = new Label
        {
            Text = "0",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _badgeLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _badgeLabel.AddThemeFontSizeOverride("font_size", 11);
        _badgeLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        _badge.AddChild(_badgeLabel);

        var padRule = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        padRule.AddThemeConstantOverride("margin_left", 10);
        padRule.AddThemeConstantOverride("margin_right", 10);
        padRule.AddChild(ShellContext.CreateRuledLine(19, "shell.ruled_ink", 1.4f, 4f));
        _container.AddChild(padRule);
    }

    public void SetCompact(bool compact)
    {
        if (_lastCompact == compact && _button is not null) return;
        _lastCompact = compact;
        if (_button is not null) _button.CustomMinimumSize = new Vector2(0, compact ? 30 : 36);
        _titleLabel?.AddThemeFontSizeOverride("font_size", compact ? 12 : 14);
        if (_icon is not null)
        {
            var dim = compact ? 15f : 18f;
            _icon.CustomMinimumSize = new Vector2(dim, dim);
        }
    }

    public bool Refresh()
    {
        if (_container is null) return false;
        var observer = _ctx.IsObserverMode?.Invoke() ?? false;
        if (observer)
        {
            var was = _container.Visible;
            _container.Visible = false;
            return was;
        }
        var wasHidden = !_container.Visible;
        if (wasHidden) _container.Visible = true;

        var count = Math.Max(0, _ctx.GetIncomingDiplomaticCount?.Invoke() ?? 0);
        var lang = Loc.Language;
        var changed = wasHidden || _lastCount != count || _lastLang != lang;
        if (!changed) return false;

        _lastCount = count;
        _lastLang = lang;
        _titleLabel.Text = Loc.Get("Diplomatic inbox");

        var hasMail = count > 0;
        _button.TooltipText = Ui.Tip(hasMail
            ? $"{Loc.Format("shell.ws.shell.inbox_pending_tip_fmt", count)}\n{Loc.Get("shell.inbox_tooltip")}"
            : Loc.Get("shell.inbox_tooltip"));

        _button.AddThemeStyleboxOverride("normal", hasMail ? _pendingNormal : _idleNormal);
        _badgeLabel.Text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        _badgeStyle.BgColor = hasMail ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.well");
        _badgeStyle.BorderColor = hasMail ? Ui.Colour("shell.brass_hi", 0.85f) : Ui.Colour("shell.engrave", 0.85f);
        _badgeLabel.AddThemeColorOverride("font_color", Ui.Colour(hasMail ? "shell.ink" : "shell.paper_muted"));
        _titleLabel.AddThemeColorOverride("font_color", Ui.Colour(hasMail ? "shell.state.warning_text" : "shell.paper"));
        _icon.Modulate = hasMail ? Ui.Colour("shell.state.warning") : Ui.Colour("shell.icon");
        return true;
    }
}
