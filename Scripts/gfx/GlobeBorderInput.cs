using System;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    public bool BorderPaintMode { get; set; }
    public bool ObjectivePaintMode { get; set; }
    public event Action<GlobePick, bool, bool, bool> BorderStrokeStarted;
    public event Action<GlobePick> BorderStrokeMoved;
    public event Action BorderStrokeFinished;
    /// <summary>The front tool is in hand and the pointer came over another province, no button down.</summary>
    public event Action<GlobePick> BorderHovered;
    private bool _borderPainting;
    private int _borderHoverProvince = -1;

    private bool HandleBorderInput(InputEvent input)
    {
        if (input is InputEventMouseButton button && button.ButtonIndex is MouseButton.Left or MouseButton.Right)
        {
            if (button.Pressed)
            {
                if (ObjectivePaintMode && button.ButtonIndex == MouseButton.Left) { AcceptEvent(); return true; }
                _borderPainting = false;
                GlobePick pick = default;
                var picked = false;
                if (TryBeginActionRead(out var c, 150))
                {
                    try { picked = TryPick(button.Position, out pick, updateSelection: false); }
                    finally { c.ExitRead(); }
                }
                if (picked)
                {
                    _borderPainting = button.ButtonIndex == MouseButton.Right;
                    BorderStrokeStarted?.Invoke(pick, !_borderPainting, button.AltPressed, button.ShiftPressed);
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
            GlobePick pick = default;
            var picked = false;
            if (TryBeginActionRead(out var c, 25))
            {
                try { picked = TryPick(motion.Position, out pick, updateSelection: false); }
                finally { c.ExitRead(); }
            }
            if (picked) BorderStrokeMoved?.Invoke(pick);
            AcceptEvent();
            return true;
        }
        // Before any button: say which border a click would take. Once a province, not once a
        // pixel - the answer is a walk along a border and the pointer crosses a province in
        // dozens of events. The camera keeps its own handling of the motion.
        if (input is InputEventMouseMotion hover && !_dragging && BorderHovered is not null &&
            _campaign is { } hoverCampaign && hoverCampaign.TryEnterRead(0, true))
        {
            GlobePick under = default;
            var picked = false;
            try { picked = TryPick(hover.Position, out under, updateSelection: false); }
            finally { hoverCampaign.ExitRead(); }
            if (picked && under.ProvinceId != _borderHoverProvince)
            {
                _borderHoverProvince = under.ProvinceId;
                BorderHovered.Invoke(under);
            }
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
