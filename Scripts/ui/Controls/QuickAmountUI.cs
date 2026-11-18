using System;
using System.Collections.Generic;
using System.Globalization;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;
using Godot;

namespace DealOrForce;

/// <summary>
/// Tracks whichever QuickAmount or SplitMove popup currently owns physical A S D F quarter keys
/// so WASD map panning pauses cleanly while a popup is open and resumes immediately on Enter,
/// Esc, focus loss, hide, or map click.
/// </summary>
public static class QuickAmountManager
{
    private static readonly List<Action> DismissCallbacks = new();
    private static Control _focusedControl;

    public static bool HasActiveFocus =>
        _focusedControl is not null && GodotObject.IsInstanceValid(_focusedControl) && _focusedControl.IsVisibleInTree();

    public static void SetFocused(Control control, Action onDismiss = null)
    {
        _focusedControl = control;
        if (onDismiss is not null && !DismissCallbacks.Contains(onDismiss))
            DismissCallbacks.Add(onDismiss);
    }

    public static void ClearFocused(Control control = null)
    {
        if (control is null || _focusedControl == control)
            _focusedControl = null;
    }

    public static void DismissAll()
    {
        _focusedControl = null;
        foreach (var cb in DismissCallbacks.ToArray())
        {
            try { cb?.Invoke(); }
            catch { /* Widget already freed. */ }
        }
        DismissCallbacks.Clear();
    }

    public static void RegisterDismiss(Action onDismiss)
    {
        if (onDismiss is not null && !DismissCallbacks.Contains(onDismiss))
            DismissCallbacks.Add(onDismiss);
    }

    public static void UnregisterDismiss(Action onDismiss)
    {
        if (onDismiss is not null)
            DismissCallbacks.Remove(onDismiss);
    }
}

/// <summary>
/// Unified reusable QuickAmount control (§6):
/// - Physical A S D F quarters (25% / 50% / 75% / 100%) active ONLY while focused (blue ring + hint)
/// - HSlider supporting mouse-wheel ±step stepping without accidental parent scrolling
/// - Direct value LineEdit accepting integers, decimals, "%", "B" / "млрд", "M" / "млн"
/// - Contextual smart preset chips + live consequence preview + visible touch-ready "Everywhere" chip
/// </summary>
public sealed partial class QuickAmountWidget : PanelContainer
{
    private readonly HSlider _slider;
    private readonly LineEdit _entry;
    private readonly Button[] _quarterButtons = new Button[4];
    private readonly HFlowContainer _presetsFlow;
    private readonly Label _titleLabel;
    private readonly Label _hintLabel;
    private readonly Label _valueLabel;
    private readonly Label _previewLabel;
    private readonly Button _applyButton;
    private readonly Button _everywhereButton;
    private readonly Button _closeButton;
    private readonly StyleBoxFlat _normalStyle;
    private readonly StyleBoxFlat _focusedStyle;

    private float _min;
    private float _max = 100f;
    private float _step = 1f;
    private float _value = 1f;
    private bool _integerOnly = true;
    private bool _syncing;
    private bool _hasFocusHighlight;

    private Func<float, string> _valueFormatter;
    private Func<float, string> _previewProvider;
    private Func<string> _everywhereTooltipProvider;
    private Action<float> _onApply;
    private Action<float> _onEverywhere;
    private Action _onClose;

    public int CurrentIntValue => Math.Max(0, (int)MathF.Round(_value, MidpointRounding.AwayFromZero));

    public QuickAmountWidget(bool showCloseButton = true)
    {
        Name = "QuickAmountWidget";
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        CustomMinimumSize = new Vector2(Ui.Metric("controls.quick_amount_width", 344), 0);

        _normalStyle = BuildCardStyle(focused: false);
        _focusedStyle = BuildCardStyle(focused: true);
        AddThemeStyleboxOverride("panel", _normalStyle);

        var root = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 6);
        AddChild(root);

        // Header row: title + live figure + close
        var head = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        head.AddThemeConstantOverride("separation", 6);
        root.AddChild(head);

        _titleLabel = new Label
        {
            Text = Loc.Get("Quick recruit"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 14);
        _titleLabel.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
        head.AddChild(_titleLabel);

        _valueLabel = new Label
        {
            Text = "1",
            HorizontalAlignment = HorizontalAlignment.Right,
            AutowrapMode = TextServer.AutowrapMode.Off
        };
        _valueLabel.AddThemeFontOverride("font", Ui.NumberFont);
        _valueLabel.AddThemeFontSizeOverride("font_size", 15);
        _valueLabel.AddThemeColorOverride("font_color", Ui.Colour("text.bright"));
        head.AddChild(_valueLabel);

        _closeButton = CreateStyledButton("×", () => RequestClose(), 28, 24);
        _closeButton.TooltipText = Ui.Tip(Loc.Get("Close"));
        _closeButton.Visible = showCloseButton;
        head.AddChild(_closeButton);

        // Quarters: 25% (A) · 50% (S) · 75% (D) · 100% (F)
        var quarters = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        quarters.AddThemeConstantOverride("separation", 4);
        root.AddChild(quarters);
        string[] keys = { "A", "S", "D", "F" };
        int[] percents = { 25, 50, 75, 100 };
        for (var i = 0; i < 4; i++)
        {
            var qIdx = i + 1;
            var btn = CreateStyledButton($"{percents[i]}% ({keys[i]})", () => SelectQuarter(qIdx), 0, 28);
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.Name = $"QuickQuarter_{percents[i]}";
            quarters.AddChild(btn);
            _quarterButtons[i] = btn;
        }

        // Slider + direct numeric / % / B entry
        var sliderRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sliderRow.AddThemeConstantOverride("separation", 6);
        root.AddChild(sliderRow);

        _slider = new HSlider
        {
            MinValue = 0,
            MaxValue = 100,
            Step = 1,
            Value = 1,
            Scrollable = false, // We handle WheelUp/WheelDown explicitly with step ±1.
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(140, 24)
        };
        _slider.ValueChanged += val =>
        {
            if (_syncing) return;
            SetValue((float)val, updateEntry: true);
        };
        _slider.GuiInput += OnSliderWheel;
        sliderRow.AddChild(_slider);

        _entry = new LineEdit
        {
            PlaceholderText = Loc.Get("e.g. 50%"),
            CustomMinimumSize = new Vector2(88, 28),
            Alignment = HorizontalAlignment.Right
        };
        _entry.TooltipText = Ui.Tip(Loc.Get("Enter value: e.g. 50%, 1.5B, or 8"));
        _entry.AddThemeFontOverride("font", Ui.NumberFont);
        _entry.TextSubmitted += raw =>
        {
            if (QuickAmountRules.TryParseDirectValue(raw, _min, _max, _step, _integerOnly, out var parsed))
                SetValue(parsed, updateEntry: true);
            else
                SyncTextEntry();
            _entry.ReleaseFocus();
            TakeKeyboardFocus();
        };
        _entry.FocusEntered += () => SetFocusHighlight(true);
        _entry.FocusExited += () =>
        {
            if (QuickAmountRules.TryParseDirectValue(_entry.Text, _min, _max, _step, _integerOnly, out var parsed))
                SetValue(parsed, updateEntry: true);
            else
                SyncTextEntry();
        };
        sliderRow.AddChild(_entry);

        // Contextual smart preset chips (populated dynamically)
        _presetsFlow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
        _presetsFlow.AddThemeConstantOverride("h_separation", 4);
        _presetsFlow.AddThemeConstantOverride("v_separation", 4);
        root.AddChild(_presetsFlow);

        // Consequence preview line
        _previewLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _previewLabel.AddThemeFontSizeOverride("font_size", 12);
        _previewLabel.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
        root.AddChild(_previewLabel);

        // Action row: Apply + visible touch-ready "Everywhere" chip
        var actions = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddThemeConstantOverride("separation", 6);
        root.AddChild(actions);

        _applyButton = CreateStyledButton(Loc.Get("Apply here"), TriggerApply, 0, 30, primary: true);
        _applyButton.Name = "QuickAmountApply";
        _applyButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(_applyButton);

        _everywhereButton = CreateStyledButton(Loc.Get("Do everywhere"), TriggerEverywhere, 0, 30, everywhereTint: true);
        _everywhereButton.Name = "QuickAmountEverywhere";
        _everywhereButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _everywhereButton.Visible = false;
        actions.AddChild(_everywhereButton);

        // Focus hint bar
        _hintLabel = new Label
        {
            Text = Loc.Get("A S D F — quarters · Enter — apply · Esc — close"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _hintLabel.AddThemeFontSizeOverride("font_size", 10);
        _hintLabel.AddThemeColorOverride("font_color", Ui.Colour("text.caption"));
        root.AddChild(_hintLabel);

        FocusEntered += () => SetFocusHighlight(true);
        VisibilityChanged += () =>
        {
            if (!IsVisibleInTree())
            {
                SetFocusHighlight(false);
                QuickAmountManager.UnregisterDismiss(RequestClose);
            }
            else
            {
                QuickAmountManager.RegisterDismiss(RequestClose);
            }
        };
    }

    public void Configure(
        string title,
        float min,
        float max,
        float step,
        float initialValue,
        bool integerOnly,
        Func<float, string> valueFormatter = null,
        Func<float, string> previewProvider = null,
        Action<float> onApply = null,
        Action<float> onEverywhere = null,
        string everywhereCaption = null,
        Func<string> everywhereTooltip = null,
        Action onClose = null,
        string applyCaption = null)
    {
        _titleLabel.Text = title ?? "";
        _min = min;
        _max = Math.Max(min, max);
        _step = Math.Max(0.0001f, step);
        _integerOnly = integerOnly;
        _valueFormatter = valueFormatter;
        _previewProvider = previewProvider;
        _onApply = onApply;
        _onEverywhere = onEverywhere;
        _everywhereTooltipProvider = everywhereTooltip;
        _onClose = onClose;

        _applyButton.Text = string.IsNullOrEmpty(applyCaption) ? Loc.Get("Apply here") : applyCaption;
        _applyButton.Visible = _onApply is not null;

        _everywhereButton.Visible = _onEverywhere is not null;
        if (!string.IsNullOrEmpty(everywhereCaption))
            _everywhereButton.Text = everywhereCaption;

        _syncing = true;
        _slider.MinValue = _min;
        _slider.MaxValue = Math.Max(_min + _step, _max);
        _slider.Step = _step;
        _slider.Editable = _max > _min;
        _syncing = false;

        SetValue(Math.Clamp(initialValue, _min, _max), updateEntry: true);
        UpdateEverywhereTooltip();
    }

    public void SetPresets(IEnumerable<(string Caption, float Value, string Tooltip)> presets)
    {
        foreach (var child in _presetsFlow.GetChildren())
            child.QueueFree();

        var count = 0;
        if (presets is not null)
        {
            foreach (var (caption, val, tip) in presets)
            {
                var captured = val;
                var chip = CreateStyledButton(caption, () =>
                {
                    TakeKeyboardFocus();
                    SetValue(captured, updateEntry: true);
                }, 0, 24);
                if (!string.IsNullOrEmpty(tip))
                    chip.TooltipText = Ui.Tip(tip);
                _presetsFlow.AddChild(chip);
                count++;
            }
        }
        _presetsFlow.Visible = count > 0;
    }

    public void TakeKeyboardFocus()
    {
        if (!IsVisibleInTree()) return;
        GrabFocus();
        SetFocusHighlight(true);
    }

    public void SetFocusHighlight(bool active)
    {
        _hasFocusHighlight = active && IsVisibleInTree();
        AddThemeStyleboxOverride("panel", _hasFocusHighlight ? _focusedStyle : _normalStyle);
        if (_hasFocusHighlight)
            QuickAmountManager.SetFocused(this, RequestClose);
        else
            QuickAmountManager.ClearFocused(this);
    }

    public void SelectQuarter(int quarterIndex)
    {
        var computed = _integerOnly
            ? QuickAmountRules.QuarterInteger((int)MathF.Round(_max, MidpointRounding.AwayFromZero), quarterIndex)
            : QuickAmountRules.QuarterFloat(_max, quarterIndex, _step);
        SetValue(Math.Clamp(computed, _min, _max), updateEntry: true);
    }

    public void SetValue(float value, bool updateEntry)
    {
        var clamped = Math.Clamp(value, _min, _max);
        if (_integerOnly)
            clamped = MathF.Round(clamped, MidpointRounding.AwayFromZero);
        else
            clamped = QuickAmountRules.RoundToStep(clamped, _step);
        _value = Math.Clamp(clamped, _min, _max);

        _syncing = true;
        _slider.Value = _value;
        _syncing = false;

        _valueLabel.Text = _valueFormatter is not null
            ? _valueFormatter(_value)
            : _integerOnly
                ? ((int)MathF.Round(_value)).ToString("N0", CultureInfo.CurrentCulture)
                : _value.ToString("0.##", CultureInfo.CurrentCulture);

        if (updateEntry) SyncTextEntry();
        RefreshPreview();
        UpdateEverywhereTooltip();
    }

    public bool HandleKeyboardShortcut(InputEventKey key)
    {
        if (!IsVisibleInTree() || (!_hasFocusHighlight && !HasFocus() && !_entry.HasFocus()))
            return false;

        if (key.Keycode == Key.Escape)
        {
            RequestClose();
            return true;
        }

        // While typing inside LineEdit, let digits/decimal characters go to LineEdit; only Enter/Esc exit.
        if (GetViewport().GuiGetFocusOwner() == _entry)
            return false;

        if (key.Keycode is Key.Enter or Key.KpEnter)
        {
            TriggerApply();
            return true;
        }

        if (key.CtrlPressed || key.AltPressed || key.MetaPressed)
            return false;

        var physical = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
        switch (physical)
        {
            case Key.A: SelectQuarter(1); return true;
            case Key.S: SelectQuarter(2); return true;
            case Key.D: SelectQuarter(3); return true;
            case Key.F: SelectQuarter(4); return true;
        }
        return false;
    }

    public void RefreshPreview()
    {
        if (_previewProvider is null)
        {
            _previewLabel.Visible = false;
            return;
        }
        var text = _previewProvider(_value) ?? "";
        _previewLabel.Text = text;
        _previewLabel.Visible = text.Length > 0;
    }

    public void UpdateEverywhereTooltip()
    {
        if (!_everywhereButton.Visible || _everywhereTooltipProvider is null) return;
        _everywhereButton.TooltipText = Ui.Tip(_everywhereTooltipProvider() ?? "");
    }

    private void SyncTextEntry()
    {
        _entry.Text = _integerOnly
            ? ((int)MathF.Round(_value)).ToString(CultureInfo.InvariantCulture)
            : _value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void OnSliderWheel(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { Pressed: true } wheel) return;
        if (wheel.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown)) return;
        var dir = wheel.ButtonIndex == MouseButton.WheelUp ? 1f : -1f;
        var mult = wheel.ShiftPressed ? 5f : 1f;
        SetValue(_value + dir * _step * mult, updateEntry: true);
        _slider.AcceptEvent();
    }

    private void TriggerApply()
    {
        if (_applyButton.Disabled) return;
        var val = _value;
        _onApply?.Invoke(val);
    }

    private void TriggerEverywhere()
    {
        if (_everywhereButton.Disabled) return;
        var val = _value;
        _onEverywhere?.Invoke(val);
    }

    public void RequestClose()
    {
        SetFocusHighlight(false);
        if (_onClose is not null)
            _onClose.Invoke();
        else
            Visible = false;
    }

    public override void _ExitTree()
    {
        QuickAmountManager.ClearFocused(this);
        QuickAmountManager.UnregisterDismiss(RequestClose);
        base._ExitTree();
    }

    private static StyleBoxFlat BuildCardStyle(bool focused)
    {
        var box = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.background"),
            BorderColor = focused ? Ui.Colour("controls.focus_border") : Ui.Colour("shell.border"),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
            ShadowColor = new Color(0, 0, 0, focused ? 0.36f : 0.24f),
            ShadowSize = focused ? 6 : 4
        };
        box.SetBorderWidthAll(focused ? 2 : 1);
        box.SetCornerRadiusAll(4);
        return box;
    }

    private static Button CreateStyledButton(string caption, Action onPressed, int width, int height,
        bool primary = false, bool everywhereTint = false)
    {
        var btn = new Button
        {
            Text = caption,
            CustomMinimumSize = new Vector2(width, height),
            ClipText = true,
            FocusMode = FocusModeEnum.None
        };
        btn.AddThemeFontSizeOverride("font_size", 12);
        var normal = new StyleBoxFlat
        {
            BgColor = everywhereTint
                ? Ui.Colour("controls.everywhere_fill")
                : primary ? Ui.Colour("button.primary_fill") : Ui.Colour("button.fill"),
            BorderColor = everywhereTint
                ? Ui.Colour("controls.everywhere_border")
                : primary ? Ui.Colour("button.primary_border") : Ui.Colour("button.border"),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 3,
            ContentMarginBottom = 3
        };
        normal.SetBorderWidthAll(1);
        normal.SetCornerRadiusAll(3);
        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = normal.BgColor.Lightened(0.12f);
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", hover);
        btn.AddThemeColorOverride("font_color", Ui.Colour(primary || everywhereTint ? "text.bright" : "text.body"));
        btn.Pressed += onPressed;
        return btn;
    }
}

/// <summary>
/// Attaches pointer-drift-guarded Shift+LMB (and optional RMB when rmb_everywhere is enabled)
/// "Do Everywhere" behavior to any quick-action button without double-firing the single click.
/// </summary>
public static class EverywhereButtonHelper
{
    public static void Attach(Button button, Action singleAction, Action everywhereAction, Func<string> tooltipBuilder = null)
    {
        if (button is null) return;
        var hasPress = false;
        var pressX = 0f;
        var pressY = 0f;

        void RefreshTip()
        {
            if (tooltipBuilder is null) return;
            var text = tooltipBuilder();
            if (!string.IsNullOrEmpty(text))
                button.TooltipText = Ui.Tip(text);
        }

        if (tooltipBuilder is not null)
        {
            button.MouseEntered += RefreshTip;
            button.FocusEntered += RefreshTip;
            RefreshTip();
        }

        button.GuiInput += inputEvent =>
        {
            if (button.Disabled || inputEvent is not InputEventMouseButton mb) return;
            if (mb.ButtonIndex is not (MouseButton.Left or MouseButton.Right)) return;

            if (mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.Right && !ControlSchemeRules.RmbEverywhereEnabled)
                    return;
                if (mb.ButtonIndex == MouseButton.Left && !mb.ShiftPressed)
                    return;
                hasPress = true;
                pressX = mb.GlobalPosition.X;
                pressY = mb.GlobalPosition.Y;
                button.AcceptEvent();
                return;
            }

            if (!hasPress) return;
            hasPress = false;
            var drift = ControlSchemeRules.Distance(pressX, pressY, mb.GlobalPosition.X, mb.GlobalPosition.Y);
            var isShiftLmb = mb.ButtonIndex == MouseButton.Left && mb.ShiftPressed;
            var isRmb = mb.ButtonIndex == MouseButton.Right;
            if (ControlSchemeRules.ShouldTriggerEverywhere(isShiftLmb, isRmb, ControlSchemeRules.RmbEverywhereEnabled, drift))
                everywhereAction?.Invoke();
            button.AcceptEvent();
        };
    }
}
