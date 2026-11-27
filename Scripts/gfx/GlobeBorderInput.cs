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

    private bool HandleBorderInput(InputEvent input) => HandleBattlePlanMouseInput(input);
}
