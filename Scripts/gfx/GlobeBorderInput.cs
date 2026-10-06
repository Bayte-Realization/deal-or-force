using System;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    public bool BorderPaintMode { get; set; }
    public bool ObjectivePaintMode { get; set; }
    public event Action<GlobePick, bool, bool> BorderStrokeStarted;
    public event Action<GlobePick> BorderStrokeMoved;
    public event Action BorderStrokeFinished;
    private bool _borderPainting;

    private bool HandleBorderInput(InputEvent input)
    {
        if (input is InputEventMouseButton button && button.ButtonIndex is MouseButton.Left or MouseButton.Right)
        {
            if (button.Pressed)
            {
                if (ObjectivePaintMode && button.ButtonIndex == MouseButton.Left) { AcceptEvent(); return true; }
                _borderPainting = false;
                if (TryPick(button.Position, out var pick, updateSelection: false))
                {
                    _borderPainting = button.ButtonIndex == MouseButton.Right;
                    BorderStrokeStarted?.Invoke(pick, !_borderPainting, button.AltPressed);
                    if (!_borderPainting) BorderStrokeFinished?.Invoke();
                }
            }
            else if (_borderPainting && button.ButtonIndex == MouseButton.Right)
            {
                _borderPainting = false;
                BorderStrokeFinished?.Invoke();
            }
            AcceptEvent();
            return true;
        }
        if (input is InputEventMouseMotion motion && _borderPainting)
        {
            if (TryPick(motion.Position, out var pick, updateSelection: false)) BorderStrokeMoved?.Invoke(pick);
            AcceptEvent();
            return true;
        }
        // Middle-drag pans while the front tool owns RMB; wheel zoom remains available.
        if (input is InputEventMouseButton middle && middle.ButtonIndex == MouseButton.Middle)
        {
            if (middle.Pressed) BeginCameraDrag(middle.Position, middle.ButtonIndex);
            else _dragging = false;
            AcceptEvent();
            return true;
        }
        return false;
    }
}
