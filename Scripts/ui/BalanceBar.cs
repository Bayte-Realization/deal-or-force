using Godot;

namespace DealOrForce;

/// <summary>
/// Who is winning, as a line along the foot of a row.
///
/// A war row in the situation report said "score +12" and a battle row "61 %" - figures a
/// player has to read and then turn into a sense of the fight. This is the sense directly:
/// our share of the line in our colour from the left, theirs from the right, a notch at the
/// middle where it is even. Hung on the row it belongs to, so it costs no height.
/// </summary>
public sealed partial class BalanceBar : Control
{
    private float _share = 0.5f;
    private bool _known = true;

    /// <summary>Our share of the fight, 0 to 1; unknown draws a plain track.</summary>
    public void Set(float share, bool known = true)
    {
        share = Mathf.Clamp(share, 0f, 1f);
        if (Mathf.IsEqualApprox(share, _share) && known == _known) return;
        _share = share;
        _known = known;
        QueueRedraw();
    }

    /// <summary>Lay one along the bottom edge of a row, inside its border.</summary>
    public static BalanceBar Under(Control row, float share, bool known = true)
    {
        var bar = new BalanceBar { Name = "Balance", MouseFilter = MouseFilterEnum.Ignore };
        bar.SetAnchorsPreset(LayoutPreset.BottomWide);
        bar.OffsetLeft = 6;
        bar.OffsetRight = -6;
        bar.OffsetTop = -4;
        bar.OffsetBottom = -2;
        row.AddChild(bar);
        bar.Set(share, known);
        return bar;
    }

    public override void _Draw()
    {
        var size = Size;
        if (size.X < 4f) return;
        var track = Ui.Colour("text.faint", 0.22f);
        DrawRect(new Rect2(Vector2.Zero, size), track);
        if (!_known) return;
        var split = size.X * _share;
        var ours = _share >= 0.55f ? Ui.Colour("state.positive") : _share <= 0.45f ? Ui.Colour("state.negative") : Ui.Colour("state.warning");
        DrawRect(new Rect2(0f, 0f, split, size.Y), new Color(ours, 0.85f));
        DrawRect(new Rect2(split, 0f, size.X - split, size.Y), Ui.Colour("text.muted", 0.35f));
        // Even, and where the line stands now.
        DrawRect(new Rect2(size.X * 0.5f - 0.5f, -1f, 1f, size.Y + 2f), Ui.Colour("text.bright", 0.55f));
        DrawRect(new Rect2(split - 1f, -1f, 2f, size.Y + 2f), ours.Lightened(0.35f));
    }
}
