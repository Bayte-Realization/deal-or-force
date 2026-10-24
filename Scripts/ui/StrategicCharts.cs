using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public enum StrategicChartFormat { Number, MoneyBillions, Millions, Percent, Index }

public sealed record StrategicChartSeries(string Name, Color Color, IReadOnlyList<float> Values);
public sealed record StrategicBarEntry(string Name, float Value, Color Color, string Detail = "");

/// <summary>
/// One recorded event on a history chart's timeline: where it falls as a fraction of the plot's
/// width, what colour its kind of news is, and the sentence that explains it when hovered.
/// The fractions come from <see cref="Simulation.ChartMarkers.Position"/>, never from the panel,
/// because history is sampled by age rather than evenly and a fraction of elapsed time would
/// drift away from the curve beside it.
/// </summary>
public sealed record StrategicChartMark(float At, Color Colour, string Detail = "");

/// <summary>A lightweight, allocation-conscious strategy ledger line chart.</summary>
public sealed partial class StrategicLineChart : Control
{
    private string _title = "Trend";
    private string _subtitle = "";
    private IReadOnlyList<int> _days = Array.Empty<int>();
    private IReadOnlyList<StrategicChartSeries> _series = Array.Empty<StrategicChartSeries>();
    private IReadOnlyList<StrategicChartMark> _marks = Array.Empty<StrategicChartMark>();
    private StrategicChartFormat _format;
    private int _hoverIndex = -1;
    private Rect2 _plot;

    public StrategicLineChart()
    {
        CustomMinimumSize = new Vector2(480, 225);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        MouseExited += () => { _hoverIndex = -1; QueueRedraw(); };
    }

    public void SetData(string title, string subtitle, IReadOnlyList<int> days,
        IReadOnlyList<StrategicChartSeries> series, StrategicChartFormat format)
    {
        _title = title;
        _subtitle = subtitle;
        _days = days ?? Array.Empty<int>();
        _series = series ?? Array.Empty<StrategicChartSeries>();
        _format = format;
        _hoverIndex = -1;
        TooltipText = Ui.Tip(subtitle);
        QueueRedraw();
    }

    /// <summary>
    /// The events this chart draws on its timeline. Kept apart from <see cref="SetData"/> because
    /// the marks outlive a tab's series choice: the same war explains the GDP curve, the budget
    /// curve and the population curve, and every one of them should say so.
    /// </summary>
    public void SetMarkers(IReadOnlyList<StrategicChartMark> marks)
    {
        _marks = marks ?? Array.Empty<StrategicChartMark>();
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseMotion motion || _days.Count == 0 || !_plot.HasPoint(motion.Position))
            return;
        var ratio = Mathf.Clamp((motion.Position.X - _plot.Position.X) / MathF.Max(1f, _plot.Size.X), 0f, 1f);
        _hoverIndex = Mathf.Clamp(Mathf.RoundToInt(ratio * (_days.Count - 1)), 0, _days.Count - 1);
        var reading = Simulation.Loc.Format("Day {0:N0}\n", _days[_hoverIndex]) +
                      string.Join("\n", _series.Select(series => Simulation.Loc.Format(
                          "{0}: {1}", series.Name, Format(ValueAt(series, _hoverIndex), _format))));
        // A mark is explained where the eye is: the column the pointer is in, not a legend
        // somewhere else on the panel that would have to be counted against tick marks.
        var window = MathF.Max(0.01f, _days.Count <= 1 ? 1f : 0.6f / (_days.Count - 1));
        if (_marks.Count > 0)
        {
            var noted = _marks.Where(mark => mark.At >= 0f && MathF.Abs(mark.At - ratio) <= window)
                .Select(mark => mark.Detail).Where(detail => !string.IsNullOrWhiteSpace(detail)).ToArray();
            if (noted.Length > 0) reading += "\n" + string.Join("\n", noted.Select(detail => "• " + detail));
        }
        TooltipText = Ui.Tip(reading);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Colour("surface.overlay"), true);
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Colour("surface.line"), false, 1f);
        DrawString(font, new Vector2(15, 24), _title, HorizontalAlignment.Left, -1, 16, Ui.Colour("text.accent"));
        DrawString(font, new Vector2(15, 43), _subtitle, HorizontalAlignment.Left, -1, 11, Ui.Colour("text.caption"));

        var legendFont = 12;
        var legendLine = 18f;
        var names = new List<(string Name, Color Color, float Width)>();
        foreach (var series in _series)
            names.Add((series.Name, series.Color, font.GetStringSize(series.Name, HorizontalAlignment.Left, -1, legendFont).X + 28f));
        var rows = 1;
        var used = 0f;
        var room = MathF.Max(80f, Size.X - 24f);
        foreach (var name in names)
        {
            if (used > 0f && used + name.Width > room) { rows++; used = 0f; }
            used += name.Width;
        }
        var legendHeight = rows * legendLine + 8f;
        _plot = new Rect2(58, 56, MathF.Max(40, Size.X - 74), MathF.Max(36, Size.Y - 64f - legendHeight));
        if (_days.Count == 0 || _series.Count == 0 || _series.All(series => series.Values.Count == 0))
        {
            DrawString(font, _plot.GetCenter(), "History will appear after the first simulation week.",
                HorizontalAlignment.Center, -1, 12, Ui.Colour("text.hint"));
            return;
        }

        var values = _series.SelectMany(series => series.Values).Where(float.IsFinite).ToArray();
        if (values.Length == 0) return;
        var minimum = values.Min();
        var maximum = values.Max();
        if (MathF.Abs(maximum - minimum) < 0.0001f)
        {
            var padding = MathF.Max(1f, MathF.Abs(maximum) * 0.05f);
            minimum -= padding;
            maximum += padding;
        }
        else
        {
            var padding = (maximum - minimum) * 0.08f;
            minimum -= padding;
            maximum += padding;
        }

        // The axis a reader can actually read: the range rounded out to a step of 1, 2, 2.5 or 5
        // times a power of ten. Every label is then a round number with the same number of
        // decimals, instead of five independent roundings of whatever the data happened to be.
        var (axisLow, axisHigh, decimals) = ChartScale.Axis(minimum, maximum);
        minimum = axisLow;
        maximum = axisHigh;

        for (var row = 0; row <= 4; row++)
        {
            var ratio = row / 4f;
            var y = _plot.Position.Y + _plot.Size.Y * ratio;
            DrawLine(new Vector2(_plot.Position.X, y), new Vector2(_plot.End.X, y), Ui.Colour("surface.line_faint"), 1f);
            var value = Mathf.Lerp(maximum, minimum, ratio);
            DrawString(font, new Vector2(5, y + 4), Format(value, _format, decimals), HorizontalAlignment.Right, 44, 10,
                Ui.Colour("text.caption"));
        }

        foreach (var series in _series)
        {
            var count = Math.Min(_days.Count, series.Values.Count);
            for (var index = 1; index < count; index++)
            {
                var before = Point(index - 1, count, series.Values[index - 1], minimum, maximum);
                var after = Point(index, count, series.Values[index], minimum, maximum);
                DrawLine(before, after, series.Color, 2.2f, true);
            }
            // One reading is not an empty chart. A three-pixel dot in a field this size reads as
            // nothing at all, so a lone value is drawn as what it is: a marker big enough to see,
            // a faint line tying it to its own axis label, and the figure written beside it.
            if (count == 1)
            {
                var point = Point(0, 1, series.Values[0], minimum, maximum);
                DrawLine(new Vector2(_plot.Position.X, point.Y), new Vector2(point.X, point.Y),
                    Ui.Colour("surface.line_faint"), 1f);
                DrawCircle(point, 6f, Ui.Colour("surface.overlay"));
                DrawCircle(point, 4f, series.Color);
                DrawString(font, new Vector2(point.X + 12, point.Y + 4),
                    Format(series.Values[0], _format, decimals), HorizontalAlignment.Left, -1, 11, series.Color);
            }
        }

        DrawMarks(font);

        var legendX = 12f;
        var legendY = Size.Y - legendHeight + 14f;
        foreach (var name in names)
        {
            if (legendX > 12f && legendX + name.Width > Size.X - 8f)
            {
                legendX = 12f;
                legendY += legendLine;
            }
            DrawRect(new Rect2(legendX, legendY - 8, 12, 5), name.Color, true);
            DrawString(font, new Vector2(legendX + 16, legendY), name.Name,
                HorizontalAlignment.Left, -1, legendFont, Ui.Colour("text.body"));
            legendX += name.Width;
        }

        DrawString(font, new Vector2(_plot.Position.X, _plot.End.Y + 15), $"day {_days[0]:N0}",
            HorizontalAlignment.Left, -1, 9, Ui.Colour("chart.tick"));
        DrawString(font, new Vector2(_plot.End.X - 90, _plot.End.Y + 15), $"day {_days[^1]:N0}",
            HorizontalAlignment.Right, 90, 9, Ui.Colour("chart.tick"));

        if (_hoverIndex < 0) return;
        var hoverX = _plot.Position.X + _plot.Size.X * (_days.Count == 1 ? 0f : _hoverIndex / (float)(_days.Count - 1));
        DrawLine(new Vector2(hoverX, _plot.Position.Y), new Vector2(hoverX, _plot.End.Y), Ui.Colour("chart.hover"), 1f);
        foreach (var series in _series)
        {
            var value = ValueAt(series, _hoverIndex);
            DrawCircle(new Vector2(hoverX, ValueY(value, minimum, maximum)), 3.2f, series.Color);
        }
    }

    /// <summary>
    /// Recorded events, as a faint line down the plot and a tab at its top edge.
    ///
    /// A vertical line on its own reads as another grid line, which is exactly the confusion a
    /// timeline mark must not cause; the tab says a thing happened here, and the tooltip says
    /// which thing. Nothing is written across the plot: two events a week apart would otherwise
    /// overprint each other, and the reader would be reading a fog of captions.
    /// </summary>
    private void DrawMarks(Font font)
    {
        if (_marks.Count == 0) return;
        var drawn = 0;
        foreach (var mark in _marks)
        {
            if (mark.At < 0f || mark.At > 1f) continue;
            var x = _plot.Position.X + _plot.Size.X * mark.At;
            DrawLine(new Vector2(x, _plot.Position.Y), new Vector2(x, _plot.End.Y),
                new Color(mark.Colour, 0.32f), 1f);
            DrawRect(new Rect2(x - 1.5f, _plot.Position.Y - 4f, 3f, 4f), mark.Colour, true);
            drawn++;
        }
        if (drawn == 0) return;
        DrawString(font, new Vector2(_plot.Position.X + 44, _plot.End.Y + 15),
            drawn == 1 ? "▲ one recorded event" : $"▲ {drawn} recorded events",
            HorizontalAlignment.Left, -1, 9, Ui.Colour("chart.tick"));
    }

    private Vector2 Point(int index, int count, float value, float minimum, float maximum) => new(
        _plot.Position.X + _plot.Size.X * (count <= 1 ? 0f : index / (float)(count - 1)),
        ValueY(value, minimum, maximum));

    private float ValueY(float value, float minimum, float maximum) =>
        _plot.End.Y - (value - minimum) / MathF.Max(0.0001f, maximum - minimum) * _plot.Size.Y;

    private static float ValueAt(StrategicChartSeries series, int index) =>
        series.Values.Count == 0 ? 0f : series.Values[Math.Min(index, series.Values.Count - 1)];

    /// <summary>One value in the chart's own units. `decimals` below zero keeps the short form.</summary>
    internal static string Format(float value, StrategicChartFormat format, int decimals) => decimals < 0
        ? Format(value, format)
        : format switch
        {
            StrategicChartFormat.MoneyBillions => $"{(value < 0f ? "−" : "")}${MathF.Abs(value).ToString("F" + decimals)}B",
            StrategicChartFormat.Millions => value.ToString("F" + decimals) + "M",
            StrategicChartFormat.Percent => (value * 100f).ToString("F" + decimals) + "%",
            _ => value.ToString("F" + decimals)
        };

    internal static string Format(float value, StrategicChartFormat format) => format switch
    {
        // A debt reads −$0.34B, as it does everywhere else (Fmt.Money), not $-0.34B.
        StrategicChartFormat.MoneyBillions => $"{(value < 0f ? "−" : "")}${MathF.Abs(value):0.##}B",
        StrategicChartFormat.Millions => $"{value:0.##}M",
        StrategicChartFormat.Percent => $"{value * 100f:0.#}%",
        StrategicChartFormat.Index => $"{value:0.#}",
        _ => $"{value:0.##}"
    };
}

/// <summary>Interactive current-state comparison chart used beside time series.</summary>
public sealed partial class StrategicBarChart : Control
{
    private string _title = "Breakdown";
    private string _subtitle = "";
    private IReadOnlyList<StrategicBarEntry> _entries = Array.Empty<StrategicBarEntry>();
    private StrategicChartFormat _format;
    private int _hover = -1;
    private float _scroll;
    private readonly List<Rect2> _barRects = new();
    private const float PlotTop = 56f;
    private const float RowHeight = 26f;

    public StrategicBarChart()
    {
        CustomMinimumSize = new Vector2(480, 225);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        MouseExited += () => { _hover = -1; QueueRedraw(); };
    }

    public void SetData(string title, string subtitle, IReadOnlyList<StrategicBarEntry> entries,
        StrategicChartFormat format)
    {
        _title = title;
        _subtitle = subtitle;
        _entries = entries ?? Array.Empty<StrategicBarEntry>();
        _format = format;
        TooltipText = Ui.Tip(subtitle);
        _hover = -1;
        _scroll = 0f;
        QueueRedraw();
    }

    private float MaxScroll => MathF.Max(0f, _entries.Count * RowHeight - MathF.Max(40f, Size.Y - PlotTop - 10f));

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton wheel && wheel.Pressed &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            var step = wheel.ButtonIndex == MouseButton.WheelUp ? -RowHeight : RowHeight;
            _scroll = Mathf.Clamp(_scroll + step, 0f, MaxScroll);
            _hover = -1;
            AcceptEvent();
            QueueRedraw();
            return;
        }
        if (inputEvent is not InputEventMouseMotion motion) return;
        _hover = _barRects.FindIndex(rect => rect.HasPoint(motion.Position));
        if (_hover >= 0 && _hover < _entries.Count)
        {
            var entry = _entries[_hover];
            TooltipText = Ui.Tip(Simulation.Loc.Format("{0}: {1}", entry.Name,
                              StrategicLineChart.Format(entry.Value, _format)) +
                          (string.IsNullOrWhiteSpace(entry.Detail) ? "" : "\n" + entry.Detail));
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Colour("surface.overlay"), true);
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Colour("surface.line"), false, 1f);
        DrawString(font, new Vector2(15, 24), _title, HorizontalAlignment.Left, -1, 16, Ui.Colour("text.accent"));
        DrawString(font, new Vector2(15, 43), _subtitle, HorizontalAlignment.Left, MathF.Max(40f, Size.X - 180f), 12, Ui.Colour("text.caption"));
        if (MaxScroll > 1f)
            DrawString(font, new Vector2(Size.X - 158, 24), "Wheel for the rest", HorizontalAlignment.Right, 146, 12, Ui.Colour("text.hint"));
        _barRects.Clear();
        if (_entries.Count == 0) return;

        var maximum = MathF.Max(0.0001f, _entries.Max(entry => MathF.Abs(entry.Value)));
        var labelWidth = Mathf.Clamp(Size.X * 0.38f, 128f, 210f);
        var barWidth = MathF.Max(40f, Size.X - labelWidth - 78f);
        for (var index = 0; index < _entries.Count; index++)
        {
            var y = PlotTop - _scroll + index * RowHeight;
            if (y + RowHeight < PlotTop || y > Size.Y - 6f) continue;
            var entry = _entries[index];
            DrawString(font, new Vector2(14, y + 17), entry.Name, HorizontalAlignment.Left, labelWidth - 18, 14,
                index == _hover ? Ui.Colour("chart.row_hover") : Ui.Colour("text.body"));
            var background = new Rect2(labelWidth, y + 5, barWidth, 16);
            DrawRect(background, Ui.Colour("surface.inset"), true);
            var fill = new Rect2(background.Position, new Vector2(background.Size.X * MathF.Abs(entry.Value) / maximum,
                background.Size.Y));
            DrawRect(fill, index == _hover ? entry.Color.Lightened(0.16f) : entry.Color, true);
            DrawString(font, new Vector2(background.End.X + 8, y + 17), StrategicLineChart.Format(entry.Value, _format),
                HorizontalAlignment.Left, 68, 13, Ui.Colour("text.body_soft"));
            _barRects.Add(new Rect2(8, y, Size.X - 16, RowHeight));
        }
    }
}
