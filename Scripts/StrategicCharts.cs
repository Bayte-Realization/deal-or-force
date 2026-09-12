using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

public enum StrategicChartFormat { Number, MoneyBillions, Millions, Percent, Index }

public sealed record StrategicChartSeries(string Name, Color Color, IReadOnlyList<float> Values);
public sealed record StrategicBarEntry(string Name, float Value, Color Color, string Detail = "");

/// <summary>A lightweight, allocation-conscious strategy ledger line chart.</summary>
public sealed partial class StrategicLineChart : Control
{
    private string _title = "Trend";
    private string _subtitle = "";
    private IReadOnlyList<int> _days = Array.Empty<int>();
    private IReadOnlyList<StrategicChartSeries> _series = Array.Empty<StrategicChartSeries>();
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

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseMotion motion || _days.Count == 0 || !_plot.HasPoint(motion.Position))
            return;
        var ratio = Mathf.Clamp((motion.Position.X - _plot.Position.X) / MathF.Max(1f, _plot.Size.X), 0f, 1f);
        _hoverIndex = Mathf.Clamp(Mathf.RoundToInt(ratio * (_days.Count - 1)), 0, _days.Count - 1);
        TooltipText = Ui.Tip(Simulation.Loc.Format("Day {0:N0}\n", _days[_hoverIndex]) +
                      string.Join("\n", _series.Select(series => Simulation.Loc.Format(
                          "{0}: {1}", series.Name, Format(ValueAt(series, _hoverIndex), _format)))));
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Colour("surface.overlay"), true);
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Colour("surface.line"), false, 1f);
        DrawString(font, new Vector2(15, 24), _title, HorizontalAlignment.Left, -1, 16, Ui.Colour("text.accent"));
        DrawString(font, new Vector2(15, 43), _subtitle, HorizontalAlignment.Left, -1, 11, Ui.Colour("text.caption"));

        _plot = new Rect2(54, 56, MathF.Max(40, Size.X - 70), MathF.Max(50, Size.Y - 88));
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

        for (var row = 0; row <= 4; row++)
        {
            var ratio = row / 4f;
            var y = _plot.Position.Y + _plot.Size.Y * ratio;
            DrawLine(new Vector2(_plot.Position.X, y), new Vector2(_plot.End.X, y), Ui.Colour("surface.line_faint"), 1f);
            var value = Mathf.Lerp(maximum, minimum, ratio);
            DrawString(font, new Vector2(5, y + 4), Format(value, _format), HorizontalAlignment.Right, 44, 10,
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
            if (count == 1)
                DrawCircle(Point(0, 1, series.Values[0], minimum, maximum), 3f, series.Color);
        }

        var legendX = _plot.Position.X;
        foreach (var series in _series)
        {
            DrawRect(new Rect2(legendX, Size.Y - 25, 10, 4), series.Color, true);
            DrawString(font, new Vector2(legendX + 15, Size.Y - 18), series.Name,
                HorizontalAlignment.Left, -1, 10, Ui.Colour("text.body_soft"));
            legendX += 20 + font.GetStringSize(series.Name, HorizontalAlignment.Left, -1, 10).X;
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

    private Vector2 Point(int index, int count, float value, float minimum, float maximum) => new(
        _plot.Position.X + _plot.Size.X * (count <= 1 ? 0f : index / (float)(count - 1)),
        ValueY(value, minimum, maximum));

    private float ValueY(float value, float minimum, float maximum) =>
        _plot.End.Y - (value - minimum) / MathF.Max(0.0001f, maximum - minimum) * _plot.Size.Y;

    private static float ValueAt(StrategicChartSeries series, int index) =>
        series.Values.Count == 0 ? 0f : series.Values[Math.Min(index, series.Values.Count - 1)];

    internal static string Format(float value, StrategicChartFormat format) => format switch
    {
        StrategicChartFormat.MoneyBillions => $"${value:0.##}B",
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
    private readonly List<Rect2> _barRects = new();

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
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
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
        DrawString(font, new Vector2(15, 43), _subtitle, HorizontalAlignment.Left, -1, 11, Ui.Colour("text.caption"));
        _barRects.Clear();
        if (_entries.Count == 0) return;

        var maximum = MathF.Max(0.0001f, _entries.Max(entry => MathF.Abs(entry.Value)));
        var available = MathF.Max(80f, Size.Y - 64f);
        var rowHeight = MathF.Min(28f, available / _entries.Count);
        var labelWidth = MathF.Min(150f, Size.X * 0.34f);
        var barWidth = MathF.Max(40f, Size.X - labelWidth - 90f);
        for (var index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            var y = 57f + index * rowHeight;
            DrawString(font, new Vector2(14, y + 15), entry.Name, HorizontalAlignment.Left, labelWidth - 16, 11,
                index == _hover ? Ui.Colour("chart.row_hover") : Ui.Colour("text.body_soft"));
            var background = new Rect2(labelWidth, y + 4, barWidth, MathF.Max(8f, rowHeight - 9));
            DrawRect(background, Ui.Colour("surface.inset"), true);
            var fill = new Rect2(background.Position, new Vector2(background.Size.X * MathF.Abs(entry.Value) / maximum,
                background.Size.Y));
            DrawRect(fill, index == _hover ? entry.Color.Lightened(0.16f) : entry.Color, true);
            DrawString(font, new Vector2(background.End.X + 7, y + 15), StrategicLineChart.Format(entry.Value, _format),
                HorizontalAlignment.Left, 72, 10, Ui.Colour("text.muted"));
            _barRects.Add(new Rect2(8, y, Size.X - 16, rowHeight));
        }
    }
}
