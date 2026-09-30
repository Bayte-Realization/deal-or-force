using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Schematic editor canvas, not a second instance of the live gameplay UI.</summary>
public partial class LayoutPreview : Control
{
    public event Action<string> Selected;
    public event Action<InterfaceSkin.PanelRect> Committed;
    public Dictionary<string, InterfaceSkin.PanelRect> Panels = new();
    public string Selection = "hud.side_panel";
    public Vector2 ReferenceSize = new(1920, 1080);
    public bool Snap = true;
    private bool _dragging, _resize;
    public bool IsDragging => _dragging;
    private Vector2 _start;
    private InterfaceSkin.PanelRect _original, _draft;
    private static readonly string[] HudContext = { "hud.top", "hud.navigation", "hud.side_panel", "hud.outliner", "hud.map_controls", "hud.alerts" };
    private static readonly string[] MapSelectionContext = { "hud.top", "hud.navigation", "hud.country_card", "hud.region_card", "hud.outliner", "hud.map_controls" };
    public static string PanelName(string id) => Loc.GetOr("layout.panel." + id, id);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        ClipContents = true;
        CustomMinimumSize = new Vector2(220, 220);
        Resized += () => { CancelDrag(); QueueRedraw(); };
        VisibilityChanged += CancelDrag;
    }

    public void CancelDrag() { _dragging = false; QueueRedraw(); }
    private float Zoom => Math.Max(.01f, Math.Min((Size.X - 24) / ReferenceSize.X, (Size.Y - 36) / ReferenceSize.Y));
    private Vector2 Origin => (Size - ReferenceSize * Zoom) * .5f;
    private Rect2 Screen => new(Origin, ReferenceSize * Zoom);
    private Rect2 Rectangle(InterfaceSkin.PanelRect r)
    {
        var b = LayoutWorkbench.Measure(r, ReferenceSize.X, ReferenceSize.Y);
        return new Rect2(Origin + new Vector2(b.X, b.Y) * Zoom, new Vector2(Math.Max(1, b.Width), Math.Max(1, b.Height)) * Zoom);
    }
    private IEnumerable<string> VisiblePanels()
    {
        IEnumerable<string> context = Selection is "hud.country_card" or "hud.region_card" ? MapSelectionContext :
            Selection.StartsWith("hud.") ? HudContext :
            Selection.StartsWith("picker.") ? Panels.Keys.Where(k => k.StartsWith("picker.")) : Array.Empty<string>();
        return context.Where(k => k != Selection && Panels.ContainsKey(k)).Append(Selection).Where(Panels.ContainsKey);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Colour("surface.background"));
        DrawRect(Screen, Ui.Colour("surface.inset"));
        DrawRect(Screen, Ui.Colour("surface.line"), false, 1);
        for (var i = 1; i < 8; i++)
        {
            var x = Origin.X + Screen.Size.X * i / 8;
            var y = Origin.Y + Screen.Size.Y * i / 8;
            DrawLine(new(x, Origin.Y), new(x, Screen.End.Y), Ui.Colour("surface.line_faint"));
            DrawLine(new(Origin.X, y), new(Screen.End.X, y), Ui.Colour("surface.line_faint"));
        }
        foreach (var key in VisiblePanels())
        {
            var selected = key == Selection;
            var r = Rectangle(selected && _dragging ? _draft : Panels[key]);
            var accent = selected ? Ui.Colour("text.accent") : Ui.Colour("surface.line");
            DrawRect(r, Ui.Colour(selected ? "button.primary_fill" : "button.fill", .95f));
            DrawRect(r, accent, false, selected ? 2 : 1);
            if (r.Size.X > 35 && r.Size.Y > 18)
                DrawString(ThemeDB.FallbackFont, r.Position + new Vector2(5, 15), PanelName(key), HorizontalAlignment.Left, r.Size.X - 10, 12,
                    Ui.Colour(selected ? "text.bright" : "text.muted"));
            if (selected)
            {
                DrawRect(new Rect2(r.End - new Vector2(7, 7), new Vector2(10, 10)), accent);
                var a = LayoutWorkbench.Anchors(_dragging ? _draft : Panels[key]);
                foreach (var p in new[] { new Vector2(a[0], a[1]), new Vector2(a[2], a[3]) })
                    DrawCircle(Origin + p * Screen.Size, 4, Ui.Colour("state.info"));
            }
        }
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        { CancelDrag(); AcceptEvent(); return; }
        // Only the focused canvas receives these keys: editing text and navigating the
        // panel list must never move a panel. Keep each non-repeat press undoable.
        if (input is InputEventKey { Pressed: true } arrow && !arrow.CtrlPressed && !arrow.MetaPressed && !arrow.AltPressed)
        {
            var direction = arrow.Keycode switch
            {
                Key.Left => Vector2.Left, Key.Right => Vector2.Right,
                Key.Up => Vector2.Up, Key.Down => Vector2.Down, _ => Vector2.Zero
            };
            if (direction != Vector2.Zero && Panels.TryGetValue(Selection, out var rect))
            {
                if (!_dragging && !arrow.Echo)
                {
                    var delta = direction * (arrow.ShiftPressed ? 8 : 1);
                    Committed?.Invoke(LayoutWorkbench.Move(rect, delta.X, delta.Y));
                }
                AcceptEvent(); return;
            }
        }
        if (input is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left)
        {
            if (button.Pressed)
            {
                GrabFocus();
                foreach (var id in VisiblePanels().Reverse())
                {
                    var rectangle = Rectangle(Panels[id]);
                    if (!rectangle.Grow(5).HasPoint(button.Position)) continue;
                    Selection = id; Selected?.Invoke(id);
                    _resize = button.Position.DistanceTo(rectangle.End) <= 14;
                    _original = _draft = Panels[id]; _start = button.Position; _dragging = true;
                    QueueRedraw(); AcceptEvent(); break;
                }
            }
            else if (_dragging)
            {
                _dragging = false;
                if (_draft != _original) Committed?.Invoke(_draft);
                QueueRedraw(); AcceptEvent();
            }
        }
        else if (input is InputEventMouseMotion motion && _dragging)
        {
            var delta = (motion.Position - _start) / Zoom;
            if (Snap && !motion.ShiftPressed) delta = delta.Snapped(new Vector2(8, 8));
            _draft = _resize ? LayoutWorkbench.Resize(_original, delta.X, delta.Y, ReferenceSize.X, ReferenceSize.Y)
                : LayoutWorkbench.Move(_original, delta.X, delta.Y);
            QueueRedraw(); AcceptEvent();
        }
    }
}
