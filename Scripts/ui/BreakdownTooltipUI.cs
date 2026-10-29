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

    /// <summary>
    /// Set when hovered by the mouse pointer, providing subtle tactile feedback
    /// that this top-bar instrument gauge is interactive and can be inspected.
    /// </summary>
    public bool Hot;

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationMouseEnter)
        {
            Hot = true;
            QueueRedraw();
        }
        else if (what == NotificationMouseExit)
        {
            Hot = false;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        // Always draw a subtle recessed telemetry cell with a left vertical divider so the
        // top bar reads as a structured instrument strip rather than floating numbers.
        DrawRect(new Rect2(2f, 1f, MathF.Max(0f, Size.X - 3f), MathF.Max(0f, Size.Y - 2f)),
            Ui.Colour(Hot ? "button.fill_hover" : "surface.inset", Hot ? 0.58f : 0.42f), true);
        DrawLine(new Vector2(0.5f, 3f), new Vector2(0.5f, MathF.Max(3f, Size.Y - 3f)),
            Ui.Colour("surface.line", 0.65f), 1f);
        DrawLine(new Vector2(2f, 1.5f), new Vector2(MathF.Max(2f, Size.X - 2f), 1.5f),
            Ui.Colour(Hot ? "button.border_hover" : "surface.line_faint", Hot ? 0.85f : 0.45f), Hot ? 1.5f : 1f);
        DrawLine(new Vector2(2f, Size.Y - 1.5f), new Vector2(MathF.Max(2f, Size.X - 2f), Size.Y - 1.5f),
            Ui.Colour("surface.background", 0.55f), 1f);
    }

    public override GodotObject _MakeCustomTooltip(string forText) => BreakdownPanel.Build(Explain?.Invoke(), Plain);
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
    /// DetailRow gives this share a separate three-pixel track underneath the text. Its
    /// fill and endpoint never cross the caption, including long wrapped translations.
    /// </summary>
    public float Share = -1f;

    /// <summary>The colour to fill with - the row's own value colour, so the two agree.</summary>
    public Color Fill = new(1, 1, 1);

    /// <summary>
    /// Set when the row is alive to the pointer, so a figure can say "hover me" the way a
    /// button does. A dense dossier is a wall of numbers; the one under the cursor is the
    /// one being asked about, and until it answered back the only way to tell was to move
    /// the mouse and see whether anything happened.
    /// </summary>
    public bool Hot;

    public override GodotObject _MakeCustomTooltip(string forText) => BreakdownPanel.Build(Explain?.Invoke(), Plain);

    public override void _Draw()
    {
        var width = Size.X;
        var height = Size.Y;
        if (width <= 1f || height <= 1f) return;

        // Subtle recessed strip + bottom hairline + left status edge so every dossier table
        // reads as a structured instrument ledger rather than floating text.
        DrawRect(new Rect2(0, 1, width, MathF.Max(0f, height - 2f)),
            Ui.Colour(Hot ? "button.fill_hover" : "shell.row", Hot ? 0.52f : 0.42f));
        DrawLine(new Vector2(0, height - 0.5f), new Vector2(width, height - 0.5f),
            Ui.Colour("surface.line_faint", Hot ? 0.65f : 0.38f), 1f);
        // A percentage is drawn in its own layout slot by DetailRow, never across a name.
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseMotion motion) return;
        var hot = GetGlobalRect().HasPoint(motion.Position);
        if (hot == Hot) return;
        Hot = hot;
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        // The pointer left the row entirely - a mouse-exit is not delivered to a control whose
        // filter is Ignore, and this one is Stop only when it has something to explain.
        if (what == NotificationMouseExit && Hot)
        {
            Hot = false;
            QueueRedraw();
        }
    }
}

public static class BreakdownPanel
{
    public static Control Build(Breakdown breakdown, string plain)
    {
        if (breakdown is null || !breakdown.HasEntries) return null;

        // The frame is the tooltip's own (ControlSkin): a second one inside it drew a box in a box.
        var panel = new PanelContainer();
        var style = new StyleBoxEmpty();
        style.SetContentMarginAll(3);
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

    public override void _Ready()
    {
        var spacing = new StyleBoxEmpty { ContentMarginBottom = 5 };
        AddThemeStyleboxOverride("normal", spacing);
    }

    public override void _Draw()
    {
        if (Rule.A <= 0.001f) return;
        // Subtle header wash + two-stage brass rule along the baseline so sections inside
        // a dossier read as distinct architectural compartments.
        var y = Size.Y - 2f;
        DrawRect(new Rect2(0, y, Size.X * 0.38f, 1.5f), new Color(Rule, 0.68f));
        DrawRect(new Rect2(Size.X * 0.38f, y, Size.X * 0.62f, 1f), new Color(Rule, 0.22f));
    }
}
