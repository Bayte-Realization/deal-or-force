using Godot;

namespace DealOrForce;

/// <summary>
/// Tooltips that survive the clock.
///
/// Godot shows a tooltip for the control under a resting mouse and takes it away when that
/// control goes - and half this interface is rebuilt on every tick: the top bar, the lists,
/// the panels redraw their rows from the simulation. A tooltip lasted until the next hour of
/// game time, which at speed is a fraction of a second; the only way to read one was to
/// pause, and in a game shared with other people nobody may pause.
///
/// So the engine's own tooltip is suppressed the moment it is made, and this shows the same
/// thing itself: the same text or the control's own custom tooltip, the same theme
/// (TooltipPanel, TooltipLabel), the same delay and placement. What it does differently is
/// what it follows. It follows the place, not the control: when the row under a resting
/// mouse is rebuilt it asks the viewport which control is there now and keeps the tooltip
/// open, refreshed from the new row, so a number that changed under it changes in it. It
/// goes when the mouse moves to something else, a button is pressed, or nothing with a
/// tooltip is under the mouse any more.
/// </summary>
public sealed partial class TooltipKeeper : Node
{
    private const double Delay = 0.5;
    private const double Grace = 0.6;
    private const double ProbeSeconds = 0.1;
    private static readonly Vector2 Offset = new(10f, 10f);
    private static SceneTree _installed;

    private PopupPanel _panel;
    private ulong _sourceId;
    private string _text = "";
    private Vector2 _mouse;
    private double _rest;
    private double _lost;
    private double _sinceProbe;

    private bool BelongsTo(Control owner)
    {
        var source = _sourceId == 0 ? null : GodotObject.InstanceFromId(_sourceId) as Control;
        return IsInstanceValid(source) && (ReferenceEquals(source, owner) || owner.IsAncestorOf(source));
    }

    /// <summary>A modal blocks the map immediately, not after the row-rebuild grace
    /// period. Only dismiss this owner's tooltip; do not suppress the modal's help.</summary>
    internal static void DismissFor(Control owner)
    {
        if (!IsInstanceValid(owner) || !owner.IsInsideTree()) return;
        var keeper = owner.GetTree().Root.GetNodeOrNull<TooltipKeeper>("TooltipKeeper");
        if (keeper is null || !keeper.BelongsTo(owner)) return;
        keeper.Hide();
        keeper._sourceId = 0;
        keeper._text = "";
        keeper._rest = keeper._sinceProbe = 0;
    }

    // Runtime observation of the real persistent tooltip, not a test-only overlay.
    internal static string VisibleTextFor(Control owner)
    {
        if (!IsInstanceValid(owner) || !owner.IsInsideTree()) return "";
        var keeper = owner.GetTree().Root.GetNodeOrNull<TooltipKeeper>("TooltipKeeper");
        return keeper is not null && keeper.BelongsTo(owner) && IsInstanceValid(keeper._panel) && keeper._panel.Visible
            ? keeper._text : "";
    }

    public static void Install(SceneTree tree)
    {
        if (tree is null || ReferenceEquals(tree, _installed)) return;
        _installed = tree;
        var keeper = new TooltipKeeper { Name = "TooltipKeeper", ProcessMode = ProcessModeEnum.Always };
        tree.NodeAdded += keeper.OnAdded;
        tree.Root.CallDeferred(Node.MethodName.AddChild, keeper);
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(GetTree(), _installed))
        {
            GetTree().NodeAdded -= OnAdded;
            _installed = null;
        }
        Hide();
    }

    /// <summary>The engine's own tooltip panel, as it is made: gone before it is drawn.</summary>
    private void OnAdded(Node node)
    {
        if (node is PopupPanel panel && !ReferenceEquals(panel, _panel))
            Callable.From(() => Suppress(panel)).CallDeferred();
    }

    private void Suppress(PopupPanel panel)
    {
        if (!IsInstanceValid(panel) || ReferenceEquals(panel, _panel)) return;
        if (panel.ThemeTypeVariation != "TooltipPanel") return;
        panel.QueueFree();
    }

    public override void _Process(double delta)
    {
        var root = GetTree().Root;
        var mouse = root.GetMousePosition();
        var moved = mouse.DistanceSquaredTo(_mouse) > 0.25f;
        _mouse = mouse;

        if (Input.GetMouseButtonMask() != 0)
        {
            Hide();
            _rest = 0;
            _sourceId = 0;
            return;
        }

        var (source, text) = Find(root, mouse);
        if (source is null && !moved && (_panel is not null || _rest > 0))
        {
            // The control under a resting mouse was rebuilt: the viewport only looks again
            // when the mouse moves, so tell it the mouse is where it is.
            _sinceProbe += delta;
            if (_sinceProbe >= ProbeSeconds)
            {
                _sinceProbe = 0;
                root.PushInput(new InputEventMouseMotion
                {
                    Position = mouse, GlobalPosition = mouse, ButtonMask = Input.GetMouseButtonMask()
                });
                (source, text) = Find(root, mouse);
            }
        }

        if (source is null)
        {
            if (_panel is not null && !moved && (_lost += delta) < Grace) return;
            Hide();
            _rest = 0;
            _sourceId = 0;
            return;
        }
        _lost = 0;

        var id = source.GetInstanceId();
        if (_panel is not null)
        {
            // Open: the same place rebuilt, or the same words under a moving mouse, keep it.
            if (id == _sourceId && text == _text) return;
            if (!moved || text == _text)
            {
                _sourceId = id;
                _text = text;
                Show(source, text, keepPlace: true);
                return;
            }
            Hide();
        }
        if (id != _sourceId || text != _text || moved)
        {
            _sourceId = id;
            _text = text;
            _rest = 0;
            return;
        }
        _rest += delta;
        if (_rest >= Delay) Show(source, text, keepPlace: false);
    }

    /// <summary>The control under the mouse that has something to say, topmost window first.</summary>
    private (Control Source, string Text) Find(Window root, Vector2 mouse)
    {
        var windows = root.GetEmbeddedSubwindows();
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var window = windows[i];
            if (window is null || ReferenceEquals(window, _panel) || !window.Visible) continue;
            if (!new Rect2(window.Position, window.Size).HasPoint(mouse)) continue;
            return Speaker(window.GuiGetHoveredControl(), mouse - window.Position);
        }
        return Speaker(root.GuiGetHoveredControl(), mouse);
    }

    private static (Control, string) Speaker(Control hovered, Vector2 mouse)
    {
        for (var control = hovered; control is not null; control = control.GetParent() as Control)
        {
            if (!IsInstanceValid(control) || !control.IsVisibleInTree()) return (null, "");
            var local = control.GetGlobalTransformWithCanvas().AffineInverse() * mouse;
            var text = control.GetTooltip(local);
            if (!string.IsNullOrEmpty(text)) return (control, text);
            if (control.TopLevel) break;
        }
        return (null, "");
    }

    private void Show(Control source, string text, bool keepPlace)
    {
        var place = keepPlace && _panel is not null ? _panel.Position : (Vector2I?)null;
        Hide();
        // Only a control that makes its own tooltip is asked for one: calling the virtual on a plain
        // Label or the globe is an engine error each time, logged every frame the mouse rests.
        var custom = source.HasMethod(Control.MethodName._MakeCustomTooltip)
            ? source.Call(Control.MethodName._MakeCustomTooltip, text).AsGodotObject() as Control
            : null;
        Control content = custom ?? new Label { Text = text, ThemeTypeVariation = "TooltipLabel" };
        _panel = new PopupPanel
        {
            ThemeTypeVariation = "TooltipPanel",
            Transient = true,
            WrapControls = true,
            ProcessMode = ProcessModeEnum.Always
        };
        _panel.SetFlag(Window.Flags.NoFocus, true);
        _panel.SetFlag(Window.Flags.Popup, false);
        _panel.SetFlag(Window.Flags.MousePassthrough, true);
        _panel.AddChild(content);
        GetTree().Root.AddChild(_panel);
        var size = _panel.GetContentsMinimumSize();
        var visible = GetTree().Root.GetVisibleRect();
        var at = place is { } kept ? new Vector2(kept.X, kept.Y) : _mouse + Offset;
        if (at.X + size.X > visible.End.X) at.X = visible.End.X - size.X;
        if (at.Y + size.Y > visible.End.Y) at.Y = _mouse.Y - size.Y - Offset.Y;
        at = at.Max(visible.Position);
        _panel.Position = (Vector2I)at.Round();
        _panel.Size = (Vector2I)size.Ceil();
        _panel.Show();
    }

    private void Hide()
    {
        if (_panel is null) return;
        if (IsInstanceValid(_panel)) { _panel.Hide(); _panel.QueueFree(); }
        _panel = null;
        _lost = 0;
    }
}
