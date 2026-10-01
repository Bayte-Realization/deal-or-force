using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>
/// A bar of shares, drawn by hand: the two sides of a war pulling a front line across it, the
/// killed, wounded and captured of one side laid end to end, or one figure leaning one way.
///
/// Laid out with containers it would be five rectangles whose widths are argued over by a
/// box that does not know they are meant to add up to one; drawn, it is the shares and
/// nothing else. A mark can be put anywhere along it - the front line of a war - and the
/// middle can be ticked, so that "ahead" is read against "even" rather than guessed at.
/// </summary>
public partial class WarBar : Control
{
    public readonly List<(float Share, Color Colour)> Segments = new();

    /// <summary>Where along the bar, 0 to 1, a front line is drawn; negative for none.</summary>
    public float Marker = -1f;
    public bool CentreTick;
    public Color Track = new(0.09f, 0.15f, 0.21f);
    public Color MarkerColour = new(0.91f, 0.79f, 0.40f);
    public Color TickColour = new(0.93f, 0.95f, 0.98f, 0.35f);

    /// <summary>Where along the bar, 0 to 1, the first segment starts.</summary>
    private float _start;

    public WarBar()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Two sides of a war: the attacker's ground from the left, up to the front line.</summary>
    public void Tug(float lean, Color attacker, Color defender)
    {
        var line = Mathf.Clamp(0.5f + 0.5f * lean, 0f, 1f);
        Segments.Clear();
        Segments.Add((line, attacker));
        Segments.Add((1f - line, defender));
        Marker = line;
        CentreTick = true;
        _start = 0f;
        QueueRedraw();
    }

    /// <summary>
    /// One figure that can go either way: a length from the middle towards whoever it favours,
    /// in their colour - the part of a war's score, read as "this much, that way".
    /// </summary>
    public void Lean(float lean, Color forward, Color backward)
    {
        lean = Mathf.Clamp(lean, -1f, 1f);
        var length = Mathf.Abs(lean) * 0.5f;
        Segments.Clear();
        Segments.Add((length, lean >= 0f ? forward : backward));
        Marker = -1f;
        CentreTick = true;
        _start = lean >= 0f ? 0.5f : 0.5f - length;
        QueueRedraw();
    }

    /// <summary>Shares of a whole, left to right; what is not shared is left as the track.</summary>
    public void Shares(params (float Share, Color Colour)[] shares)
    {
        Segments.Clear();
        Segments.AddRange(shares);
        Marker = -1f;
        CentreTick = false;
        _start = 0f;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        if (size.X < 6f || size.Y <= 0f) return;
        // The marker stands proud of the bar, so the bar itself is inset to leave it room.
        var proud = Marker >= 0f || CentreTick ? Mathf.Min(3f, size.Y * 0.2f) : 0f;
        var bar = new Rect2(0f, proud, size.X, size.Y - 2f * proud);
        DrawRect(bar, Track);

        var x = bar.Position.X + bar.Size.X * _start;
        for (var index = 0; index < Segments.Count; index++)
        {
            var (share, colour) = Segments[index];
            var width = Mathf.Min(Mathf.Max(0f, share) * bar.Size.X, bar.End.X - x);
            if (width >= 0.5f) DrawRect(new Rect2(x, bar.Position.Y, width, bar.Size.Y), colour);
            x += width;
            // A hairline of the ground between neighbours, so two countries painted in the same
            // red still read as two.
            if (index < Segments.Count - 1 && width >= 0.5f && x < bar.End.X - 0.5f)
                DrawRect(new Rect2(x - 0.5f, bar.Position.Y, 1f, bar.Size.Y), new Color(0f, 0f, 0f, 0.55f));
        }

        if (CentreTick)
            DrawRect(new Rect2(bar.Position.X + bar.Size.X * 0.5f - 0.5f, 0f, 1f, size.Y), TickColour);
        if (Marker >= 0f)
        {
            var at = Mathf.Clamp(bar.Position.X + bar.Size.X * Marker, 2.5f, size.X - 2.5f);
            DrawRect(new Rect2(at - 2.5f, 0f, 5f, size.Y), new Color(0f, 0f, 0f, 0.6f));
            DrawRect(new Rect2(at - 1.5f, 0f, 3f, size.Y), MarkerColour);
        }
    }
}
