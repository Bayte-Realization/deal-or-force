using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>
/// Half a year of one figure, drawn as a thread under it.
///
/// The top bar said what a figure was and how it moved this week; it could not say whether
/// this week was the trend or the exception, which is the question a player is actually
/// asking of the treasury and of stability. A sample a week for twenty-six weeks, a hairline
/// in the colour of where it is heading, the last week a brighter point - and when a figure
/// that was rising turns down, the number above it flashes brass once, so the turn is seen
/// at the moment it happens rather than discovered later.
/// </summary>
public sealed partial class Sparkline : Control
{
    private const int Weeks = 26;
    private readonly List<double> _values = new();
    private int _lastDay = int.MinValue;
    private int _lastDirection;
    private bool _layoutAllows = true;
    internal bool HasHistory => _values.Count >= 2;

    internal void AllowLayout(bool allowed)
    {
        _layoutAllows = allowed;
        Visible = allowed && HasHistory;
    }

    /// <summary>The label to flash when the figure turns.</summary>
    public Label Flash;

    /// <summary>Whether a fall is bad news (treasury, stability) or good (inflation, debt).</summary>
    public bool HigherIsBetter = true;

    public Sparkline()
    {
        CustomMinimumSize = new Vector2(34, 11);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        // Nothing to draw takes no room: a figure with no history keeps its text in line.
        Visible = false;
    }

    public void Reset()
    {
        _values.Clear();
        _lastDay = int.MinValue;
        _lastDirection = 0;
        Visible = false;
        QueueRedraw();
    }

    /// <summary>One reading; kept once a week, the latest always replacing the week's last.</summary>
    public void Push(int day, double value)
    {
        if (!double.IsFinite(value)) return;
        if (_values.Count > 0 && day - _lastDay < 7)
        {
            _values[^1] = value;
        }
        else
        {
            _values.Add(value);
            _lastDay = day;
            while (_values.Count > Weeks) _values.RemoveAt(0);
            Turned();
        }
        if (Visible != (_layoutAllows && HasHistory)) Visible = _layoutAllows && HasHistory;
        QueueRedraw();
    }

    private void Turned()
    {
        if (_values.Count < 3) return;
        var change = _values[^1] - _values[^2];
        var scale = Math.Max(1e-9, Math.Abs(_values[^2]));
        var direction = Math.Abs(change) / scale < 0.002 ? 0 : Math.Sign(change);
        var worse = HigherIsBetter ? direction < 0 && _lastDirection > 0 : direction > 0 && _lastDirection < 0;
        if (direction != 0) _lastDirection = direction;
        if (!worse || Flash is null || !IsInstanceValid(Flash) || !Flash.IsInsideTree()) return;
        var tween = Flash.CreateTween();
        Flash.Modulate = Ui.Colour("text.accent").Lightened(0.25f);
        tween.TweenProperty(Flash, "modulate", Colors.White, 0.9f).SetTrans(Tween.TransitionType.Sine);
    }

    public override void _Draw()
    {
        var size = Size;
        if (_values.Count < 2 || size.X < 4 || size.Y < 4) return;
        double low = double.MaxValue, high = double.MinValue;
        foreach (var value in _values) { low = Math.Min(low, value); high = Math.Max(high, value); }
        var span = high - low;
        if (span < Math.Max(1e-9, Math.Abs(high) * 0.0005)) span = Math.Max(1e-9, Math.Abs(high) * 0.0005);
        var rising = _values[^1] >= _values[0];
        var good = rising == HigherIsBetter;
        var flat = Math.Abs(_values[^1] - _values[0]) <= span * 0.05;
        var colour = flat ? Ui.Colour("text.faint") : good ? Ui.Colour("state.positive") : Ui.Colour("state.negative");
        var points = new Vector2[_values.Count];
        for (var i = 0; i < _values.Count; i++)
        {
            var x = i * (size.X - 3f) / (Weeks - 1) + (Weeks - _values.Count) * (size.X - 3f) / (Weeks - 1);
            var y = (float)(size.Y - 2f - (_values[i] - low) / span * (size.Y - 4f));
            points[i] = new Vector2(x + 1f, y);
        }
        // A faint floor, the thread, and the latest week as a point.
        DrawLine(new Vector2(points[0].X, size.Y - 1f), new Vector2(size.X - 1f, size.Y - 1f), Ui.Colour("text.faint", 0.25f), 1f);
        var fill = new Vector2[points.Length + 2];
        Array.Copy(points, fill, points.Length);
        fill[^2] = new Vector2(points[^1].X, size.Y - 1f);
        fill[^1] = new Vector2(points[0].X, size.Y - 1f);
        var shade = colour; shade.A = 0.12f;
        DrawColoredPolygon(fill, shade);
        DrawPolyline(points, new Color(colour, 0.85f), 1.2f, true);
        DrawCircle(points[^1], 1.8f, colour.Lightened(0.2f));
    }
}
