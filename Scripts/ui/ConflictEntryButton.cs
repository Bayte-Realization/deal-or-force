using System;
using Godot;

namespace DealOrForce;

/// <summary>The expensive explanation is read only when a tooltip is actually requested.
/// The provider resolves the current campaign object, not a captured day-old snapshot.</summary>
public partial class ConflictEntryButton : Button
{
    public Func<string> Explain;
    public override GodotObject _MakeCustomTooltip(string forText) => new Label
    {
        Text = Explain?.Invoke() ?? forText,
        CustomMinimumSize = new Vector2(360, 0),
        AutowrapMode = TextServer.AutowrapMode.WordSmart
    };
}
