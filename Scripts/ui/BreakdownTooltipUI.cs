using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// A figure on the interface that can explain itself.
///
/// This is the habit that makes a Paradox interface dense without being frightening: the
/// screen shows a number, and hovering it shows the named parts that add up to it. Nothing
/// is hidden behind a wiki page, and nothing has to be discovered by experiment - a player
/// watching their money drain finds out why in one hover.
///
/// Godot's ordinary tooltip is a line of plain text, which cannot do this: the parts want
/// two columns, a sign, a colour and a rule above the total. So the readout builds its own
/// tooltip control, and the simulation hands it a <see cref="Breakdown"/> rather than a
/// finished string, so what is shown is always this week's arithmetic.
/// </summary>
public partial class ExplainedStat : VBoxContainer
{
    /// <summary>Fetched at hover time, so a paused game and a running one both tell the truth.</summary>
    public Func<Breakdown> Explain;

    /// <summary>What to say when there is nothing to explain yet - the first week, mostly.</summary>
    public string Plain = "";

    public override Control _MakeCustomTooltip(string forText) => BreakdownPanel.Build(Explain?.Invoke(), Plain);
}

/// <summary>
/// The same thing for a row in a menu. Every detail row in the game goes through one
/// helper, so giving that helper a breakdown gives every screen the same behaviour: the
/// figure on the right, and the arithmetic behind it under the cursor.
/// </summary>
public partial class ExplainedRow : HBoxContainer
{
    public Func<Breakdown> Explain;
    public string Plain = "";

    /// <summary>
    /// How full this row is, nought to one, or negative for a row that is not a proportion
    /// of anything.
    ///
    /// A row that knows this draws itself as a meter: the share fills its own background
    /// from the left, behind the text, at a few per cent opacity. It costs no height at all,
    /// which is the whole point - a column of figures becomes something whose shape can be
    /// read before any of the words are, and nothing had to grow to make room for it.
    /// </summary>
    public float Share = -1f;

    /// <summary>The colour to fill with - the row's own value colour, so the two agree.</summary>
    public Color Fill = new(1, 1, 1);

    public override Control _MakeCustomTooltip(string forText) => BreakdownPanel.Build(Explain?.Invoke(), Plain);

    public override void _Draw()
    {
        if (Share < 0f) return;
        var width = Size.X * Mathf.Clamp(Share, 0f, 1f);
        if (width <= 1f) return;

        // Quiet enough to sit behind a whole screen of rows without any of them shouting,
        // and a hairline at the head of the bar so the exact value is readable rather than
        // guessed from a gradient's edge.
        DrawRect(new Rect2(0, 2, width, Size.Y - 4), new Color(Fill, 0.10f));
        DrawRect(new Rect2(width - 1f, 2, 1f, Size.Y - 4), new Color(Fill, 0.38f));
    }
}

public static class BreakdownPanel
{
    public static Control Build(Breakdown breakdown, string plain)
    {
        if (breakdown is null || !breakdown.HasEntries) return null;

        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("surface.overlay"),
            BorderColor = Ui.Colour("surface.line"),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3
        };
        style.SetBorderWidthAll(1);
        style.SetContentMarginAll(12);
        panel.AddThemeStyleboxOverride("panel", style);

        var rows = new VBoxContainer { CustomMinimumSize = new Vector2(268, 0) };
        rows.AddThemeConstantOverride("separation", 3);
        panel.AddChild(rows);

        rows.AddChild(Caption(breakdown.Title.ToUpperInvariant(), 11, Ui.Colour("text.caption")));
        if (!string.IsNullOrWhiteSpace(plain))
        {
            var note = Caption(plain, 11, Ui.Colour("text.hint"));
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            note.CustomMinimumSize = new Vector2(268, 0);
            rows.AddChild(note);
        }
        rows.AddChild(Rule());

        foreach (var entry in breakdown.Entries)
            rows.AddChild(Row(entry.Source, Format(entry.Value, breakdown.Unit), Tint(entry.Value),
                Ui.Colour("text.body_soft"), 12));

        rows.AddChild(Rule());
        rows.AddChild(Row("Total", Format(breakdown.Total, breakdown.Unit), Tint(breakdown.Total),
            Ui.Colour("text.bright"), 13));
        return panel;
    }

    /// <summary>
    /// One part: what it is on the left, what it is worth on the right. The figures are
    /// tabular so the column reads as a column rather than as ragged text.
    /// </summary>
    private static HBoxContainer Row(string source, string value, Color valueColor, Color sourceColor, int size)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        var label = Caption(source, size, sourceColor);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(label);
        var amount = Caption(value, size, valueColor);
        amount.HorizontalAlignment = HorizontalAlignment.Right;
        amount.AddThemeConstantOverride("outline_size", 0);
        row.AddChild(amount);
        return row;
    }

    private static Label Caption(string text, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static Control Rule() => new ColorRect
    {
        Color = Ui.Colour("surface.line_faint"),
        CustomMinimumSize = new Vector2(0, 1)
    };

    private static Color Tint(float value) =>
        value > 0.0000005f ? Ui.Colour("state.positive")
        : value < -0.0000005f ? Ui.Colour("state.negative")
        : Ui.Colour("text.muted");

    /// <summary>
    /// A part is only legible in the unit its number is in, and the sign always shows:
    /// a budget line that reads "$1.2B" leaves the player working out whether it came in
    /// or went out.
    /// </summary>
    private static string Format(float value, string unit) => unit switch
    {
        "share" => $"{(value >= 0f ? "+" : "−")}{MathF.Abs(value) * 100f:0.###} %",
        "people" => $"{(value >= 0f ? "+" : "−")}{Fmt.People(MathF.Abs(value))}",
        _ => Fmt.MoneyDelta(value)
    };
}

/// <summary>
/// A section heading with a rule along its baseline.
///
/// A Label cannot draw anything of its own, and a heading followed by a separate one-pixel
/// panel is two nodes and a layout argument for every heading in the game. This is one node
/// that knows it is a heading.
/// </summary>
public partial class SectionHeading : Label
{
    /// <summary>The colour of the rule. Transparent draws nothing.</summary>
    public Color Rule = new(1, 1, 1, 0);

    public override void _Draw()
    {
        if (Rule.A <= 0.001f) return;
        // Under the text, not through it, and fading out to the right so it reads as an
        // underline for the words rather than a border on the panel.
        var y = Size.Y - 3f;
        DrawRect(new Rect2(0, y, Size.X * 0.34f, 1f), new Color(Rule, 0.55f));
        DrawRect(new Rect2(Size.X * 0.34f, y, Size.X * 0.66f, 1f), new Color(Rule, 0.14f));
    }
}
