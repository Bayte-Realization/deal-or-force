using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The parts every serious screen is made of: a document head, a section rule, a figure with a
/// unit and a delta, and a fact row with a status edge.
///
/// Why these four and not a new theme. The interface had colour, spacing and panels, and no
/// vocabulary: every screen invented its own heading, its own way of showing a number, and its
/// own way of saying "this is unavailable". That is what makes a set of panels read as a set of
/// panels rather than as one document. A screen built from these parts looks like the last
/// screen because it is made of the same things - and a change to the head is a change to every
/// screen at once.
/// </summary>
public partial class DealOrForceGame
{
    /// <summary>
    /// The head of a workspace: the title in the display face, a sentence of purpose under it,
    /// the figures that matter on the right, and a rule under the lot.
    ///
    /// The rule is the point. A panel that starts with a title floating in it has no edge; a
    /// panel that starts with a title, a rule and a figure has a masthead, and a masthead is
    /// what a reader recognises as "this is a page".
    /// </summary>
    private PanelContainer ScreenHeader(VBoxContainer rows, string title, string subtitle,
        bool upper = false, params Control[] right)
    {
        var header = new PanelContainer { Name = "ScreenHeader" };
        header.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        rows.AddChild(header);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 0);
        margin.AddThemeConstantOverride("margin_right", 0);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        header.AddChild(margin);
        var line = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        line.AddThemeConstantOverride("separation", Ui.TypeStep(16));
        margin.AddChild(line);

        var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        words.AddThemeConstantOverride("separation", 4);
        line.AddChild(words);
        var head = Title(title, (int)Ui.Metric("ui.font.display", 28), Ui.Colour("text.bright"), upper);
        head.Name = "ScreenHeaderTitle";
        // A page title is read, not scanned: it may take the width it needs and wrap once.
        head.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        words.AddChild(head);
        if (!string.IsNullOrEmpty(subtitle))
        {
            var purpose = Title(subtitle, (int)Ui.Metric("ui.font.body", 12), Ui.Colour("text.muted"));
            purpose.Name = "ScreenHeaderSubtitle";
            purpose.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            words.AddChild(purpose);
        }

        if (right.Length > 0)
        {
            var figures = new HBoxContainer { Name = "ScreenHeaderFigures" };
            figures.AddThemeConstantOverride("separation", Ui.TypeStep(16));
            foreach (var control in right)
            {
                control.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                figures.AddChild(control);
            }
            line.AddChild(figures);
        }

        rows.AddChild(ChromeRule("ScreenHeaderRule", "surface.line"));
        return header;
    }

    private static HSeparator ChromeRule(string name, string colourToken = "surface.line_faint")
    {
        var rule = new HSeparator { Name = name };
        rule.AddThemeStyleboxOverride("separator", new StyleBoxLine
        {
            Color = Ui.Colour(colourToken),
            Thickness = 1
        });
        return rule;
    }

    /// <summary>The display-face title a <see cref="ScreenHeader"/> created, for a head whose words change.</summary>
    private static Label HeaderTitle(Control header) =>
        header.FindChild("ScreenHeaderTitle", true, false) as Label;

    /// <summary>
    /// A row of tabs with the one geometry every workspace uses.
    ///
    /// Three screens had grown their own: the ledger spaced its tabs 6 by 6, the conflict board
    /// 6 by 4, the settings screen 6 by 5, and each picked its own height. None of that is
    /// visible in any single screenshot and all of it is visible when the screens are seen one
    /// after another, which is the whole of what makes an interface look assembled rather than
    /// designed. The numbers live in `63_spacing_grid.json` now, so the rhythm can be retuned
    /// without touching the screens.
    /// </summary>
    private HFlowContainer TabStrip(VBoxContainer rows, string name = "TabStrip")
    {
        var strip = new HFlowContainer { Name = name };
        strip.AddThemeConstantOverride("h_separation", (int)Ui.Metric("tab.gap", 6));
        strip.AddThemeConstantOverride("v_separation", (int)Ui.Metric("tab.gap", 6));
        rows.AddChild(strip);
        return strip;
    }

    /// <summary>One tab in a <see cref="TabStrip"/>: same height, same type step, caller's width.</summary>
    private Button TabButton(Container strip, string label, System.Action action, int width, bool selected = false)
    {
        var button = ActionButton(label, action, width, (int)Ui.Metric("tab.height", 32), selected);
        button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.tab", 13));
        strip.AddChild(button);
        return button;
    }

    /// <summary>Insert a leading badge or flag before the words column inside a <see cref="ScreenHeader"/>.</summary>
    private static void PrependToScreenHeader(PanelContainer header, Control leading)
    {
        if (header?.GetChild(0) is MarginContainer margin && margin.GetChild(0) is HBoxContainer line && leading is not null)
        {
            leading.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            line.AddChild(leading);
            line.MoveChild(leading, 0);
        }
    }

    /// <summary>
    /// A section inside a page: a tracked caption, a hairline drawn to the far edge, and
    /// an optional count on the right. Sections are what makes a long screen navigable, and the
    /// count is what makes it honest - "3 of 12" says more than a list of twelve.
    /// </summary>
    private VBoxContainer SectionRule(VBoxContainer rows, string caption, string rightText = null)
    {
        var block = new VBoxContainer { Name = "SectionRule_" + caption };
        block.AddThemeConstantOverride("separation", 4);
        rows.AddChild(block);
        var line = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        line.AddThemeConstantOverride("separation", 8);
        block.AddChild(line);
        var label = Title(caption, (int)Ui.Metric("ui.font.caption", 11), Ui.Colour("text.caption"), upper: true);
        label.Name = "SectionCaption";
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(label);
        if (!string.IsNullOrEmpty(rightText))
        {
            var count = Title(rightText, (int)Ui.Metric("ui.font.caption", 11), Ui.Colour("text.body_soft"));
            count.Name = "SectionCount";
            count.AddThemeFontOverride("font", Ui.NumberFont);
            line.AddChild(count);
        }
        block.AddChild(ChromeRule("SectionRuleLine"));
        return block;
    }

    /// <summary>
    /// One row of a list: a status edge, a title, one line of meta, and the action that belongs
    /// to the row.
    ///
    /// Rows and not buttons, because a button can say one thing. A row says what the thing is,
    /// what it costs, whether it is available, and what pressing it does - and a column of rows
    /// with the same edges, the same meta line and the same action on the right is what makes a
    /// list read as a table of options rather than as a tray of buttons.
    /// </summary>
    private (HBoxContainer Row, Panel Edge, Label Meta, Button Action) ActionRow(
        string name, string title, string meta, System.Action action, string actionCaption,
        string statusToken = "surface.line_faint", string tooltip = "")
    {
        var row = new StructuredFactRow
        {
            Name = name,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AccentToken = statusToken
        };
        row.AddThemeConstantOverride("separation", Ui.Gap(8));
        var edge = new Panel
        {
            Name = name + "_Edge",
            CustomMinimumSize = new Vector2(3, 0),
            MouseFilter = MouseFilterEnum.Ignore
        };
        var edgeStyle = new StyleBoxFlat { BgColor = Ui.Colour(statusToken) };
        edgeStyle.SetCornerRadiusAll(1);
        edge.AddThemeStyleboxOverride("panel", edgeStyle);
        row.AddChild(edge);

        var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        words.AddThemeConstantOverride("separation", 0);
        row.AddChild(words);
        var caption = Title(title, (int)Ui.Metric("ui.font.body", 12), Ui.Colour("text.body"));
        caption.Name = name + "_Title";
        caption.AutowrapMode = TextServer.AutowrapMode.Off;
        caption.ClipText = true;
        words.AddChild(caption);
        var line = Title(meta, (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"));
        line.Name = name + "_Meta";
        line.AutowrapMode = TextServer.AutowrapMode.Off;
        line.ClipText = true;
        words.AddChild(line);

        var button = ActionButton(actionCaption, action, 0, 30);
        button.Name = name + "_Action";
        row.AddChild(button);
        if (!string.IsNullOrEmpty(tooltip)) button.TooltipText = Ui.Tip(tooltip);
        return (row, edge, line, button);
    }

    /// <summary>Repaint a row's status edge and its line of meta, without rebuilding the row.</summary>
    private static void PaintRow(Panel edge, string statusToken)
    {
        if (edge is null || !GodotObject.IsInstanceValid(edge)) return;
        if (edge.GetThemeStylebox("panel") is not StyleBoxFlat style) return;
        style.BgColor = Ui.Colour(statusToken);
    }

    /// <summary>
    /// A figure: its caption above in tracked caps, the number itself in tabular digits, and a
    /// delta under it. Digit columns that line up are half of what makes a screen look measured
    /// rather than typed.
    /// </summary>
    private VBoxContainer NumberCell(string caption, string value, string delta = "",
        string colourToken = "text.bright", string deltaToken = "")
    {
        var cell = new StructuredMetricCell
        {
            Name = "Figure_" + caption,
            AccentToken = colourToken
        };
        cell.AddThemeConstantOverride("separation", 4);
        var cap = Title(caption, (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"), upper: true);
        cap.Name = "FigureCaption";
        cap.AutowrapMode = TextServer.AutowrapMode.Off;
        cell.AddChild(cap);
        var number = Title(value, (int)Ui.Metric("ui.font.title", 20), Ui.Colour(colourToken));
        number.Name = "FigureValue";
        number.AddThemeFontOverride("font", Ui.NumberFont);
        number.AutowrapMode = TextServer.AutowrapMode.Off;
        cell.AddChild(number);
        if (!string.IsNullOrEmpty(delta))
        {
            var change = Title(delta, (int)Ui.Metric("ui.font.caption", 11),
                Ui.Colour(string.IsNullOrEmpty(deltaToken) ? "text.muted" : deltaToken));
            change.Name = "FigureDelta";
            change.AddThemeFontOverride("font", Ui.NumberFont);
            cell.AddChild(change);
        }
        return cell;
    }

    /// <summary>
    /// One fact in a dossier: a status edge on the left, the label, and the value set in
    /// tabular digits against the right edge.
    ///
    /// The edge is what carries the state - ours, theirs, warning, at war - so a column of facts
    /// can be read at a glance without reading the words. Colour alone was not enough: on a dark
    /// panel a red and an amber caption are the same shape.
    /// </summary>
    private HBoxContainer FactRow(string label, string value, string statusToken = "surface.line_faint",
        string valueToken = "text.body")
    {
        var row = new StructuredFactRow
        {
            Name = "Fact_" + label,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 24),
            AccentToken = statusToken
        };
        row.AddThemeConstantOverride("separation", 8);
        var edge = new Panel { CustomMinimumSize = new Vector2(2, 0), MouseFilter = MouseFilterEnum.Ignore };
        var edgeStyle = new StyleBoxFlat { BgColor = Ui.Colour(statusToken) };
        edgeStyle.SetCornerRadiusAll(1);
        edge.AddThemeStyleboxOverride("panel", edgeStyle);
        row.AddChild(edge);
        var name = Title(label, (int)Ui.Metric("ui.font.body", 12), Ui.Colour("text.muted"));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.AutowrapMode = TextServer.AutowrapMode.Off;
        name.ClipText = true;
        row.AddChild(name);
        var amount = Title(value, (int)Ui.Metric("ui.font.button", 13), Ui.Colour(valueToken));
        amount.AddThemeFontOverride("font", Ui.NumberFont);
        amount.HorizontalAlignment = HorizontalAlignment.Right;
        amount.AutowrapMode = TextServer.AutowrapMode.Off;
        row.AddChild(amount);
        return row;
    }
}

/// <summary>
/// Recessed/alternating fact row with a subtle bottom rule, left status bar, and hover glow.
/// </summary>
public partial class StructuredFactRow : HBoxContainer
{
    public bool Alternate;
    public string AccentToken = "surface.line_faint";
    private bool _hot;

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationMouseEnter) { _hot = true; QueueRedraw(); }
        else if (what == NotificationMouseExit) { _hot = false; QueueRedraw(); }
    }

    public override void _Draw()
    {
        if (Size.X <= 1f || Size.Y <= 1f) return;
        var bg = _hot
            ? Ui.Colour("button.fill_hover", 0.52f)
            : Ui.Colour(Alternate ? "shell.row_alternate" : "shell.row", Alternate ? 0.56f : 0.38f);
        DrawRect(new Rect2(0, 1, Size.X, MathF.Max(0f, Size.Y - 2f)), bg, true);
        DrawLine(new Vector2(0, Size.Y - 0.5f), new Vector2(Size.X, Size.Y - 0.5f),
            Ui.Colour("surface.line_faint", _hot ? 0.65f : 0.36f), 1f);
        var stripe = Ui.Colour(InterfaceSkin.Has(AccentToken) ? AccentToken : "surface.line_faint", _hot ? 0.90f : 0.55f);
        DrawRect(new Rect2(0, 2, _hot ? 3f : 2f, MathF.Max(0f, Size.Y - 4f)), stripe, true);
    }
}

/// <summary>
/// Quiet inset KPI cell with a hairline top light.
/// </summary>
public partial class StructuredMetricCell : VBoxContainer
{
    public string AccentToken = "text.bright";

    public override void _Draw()
    {
        if (Size.X <= 1f || Size.Y <= 1f) return;
        // Paint inside the cell; negative extents used to spill over neighbouring labels.
        DrawRect(new Rect2(Vector2.Zero, Size), Ui.Colour("surface.inset", 0.28f), true);
        DrawLine(new Vector2(0, 0.5f), new Vector2(Size.X, 0.5f), Ui.Colour("surface.line_faint", .45f), 1);
    }
}
