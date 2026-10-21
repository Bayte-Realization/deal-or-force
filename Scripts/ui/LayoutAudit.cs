using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

/// <summary>
/// Look at the interface that is actually on screen and find the labels that were never
/// given room for their own text.
///
/// WHY THIS EXISTS. Four separate times in this project a caption silently collapsed to about
/// one pixel wide, and each time it looked like something else entirely: a column of single
/// letters on the construction card, an empty space where the key rate's name should be, a
/// missing "PRINTING MONEY", and two stray dots at the top of the trade screen that turned
/// out to be the first pixel of "ABOUT ONE GOOD" and "ABOUT ONE COUNTRY". Nobody reported the
/// first three as a layout bug, because a bug you cannot see does not look like a bug - it
/// looks like a design decision you do not understand.
///
/// THE CAUSE IS ALWAYS THE SAME. A label built through the `Fixed` helper is clipped and has
/// no minimum width. Put one in a row beside a sibling that takes `ExpandFill` and the
/// sibling takes everything: the label is laid out at zero and clips its own text away. It
/// is a correct use of every control involved and it produces an invisible caption.
///
/// AND THE SAME BUG HAS A SECOND FORM, which is how the fifth one was found: a clipped label
/// that is not at zero but is still too narrow for its sentence. A left-aligned column that
/// loses its tail is an ordinary compromise and reads fine - "United States of Ameri...". A
/// right-aligned one loses its HEAD instead, and "Our claim weighs 2.25x when the world is
/// short of it." arrives on screen as "r claim weighs 2,25x when the world is short of it."
/// Nobody reads that as a clipped label either; they read it as a typo in the text.
///
/// WHY IT IS NOT A BENCH CHECK. The bench has no Godot in it - that is the point of the
/// bench - and this question can only be asked of a real layout after a real layout pass. So
/// it runs inside the game instead, over whatever is on screen, two ways: `layout` in the
/// console answers it on demand, and in a debug build <see cref="Watch"/> asks it once a
/// second on its own, so a broken caption speaks up the moment somebody opens the panel it
/// is on. Neither costs a shipped build anything.
/// </summary>
public static class LayoutAudit
{
    /// <summary>Under this many pixels wide, a label is not narrow, it is gone.</summary>
    private const float Vanished = 10f;

    /// <summary>
    /// How much of a left-aligned label's own text has to survive before the clip counts as
    /// a bug rather than an ellipsis. Losing the last word of a long name in a narrow column
    /// is what a column is for; losing half of it is a column that was measured wrong.
    /// </summary>
    private const float Legible = 0.6f;

    /// <summary>What went wrong with a caption, in the words the report uses.</summary>
    public enum Fault
    {
        /// <summary>Laid out at nothing: the text is not on screen at all.</summary>
        Squeezed,

        /// <summary>On screen, but too narrow for its sentence, and cut where it reads badly.</summary>
        Clipped
    }

    /// <summary>A label with text, on screen, that has been given no room to show it.</summary>
    public readonly record struct Finding(string Path, string Text, float Width, float Height,
        Fault Fault, float Wanted);

    /// <summary>
    /// Every visible label whose text cannot possibly be read, deepest first.
    ///
    /// Empty labels are skipped: a label with nothing in it is not a squeezed caption, it is
    /// a placeholder waiting for a refresh, and there are dozens of those at any moment.
    /// </summary>
    public static List<Finding> Run(Node root)
    {
        var found = new List<Finding>();
        Walk(root, found);
        found.Sort((left, right) => left.Width.CompareTo(right.Width));
        return found;
    }

    private static void Walk(Node node, List<Finding> found)
    {
        if (node is SubViewport or Node3D) return;
        if (node is Control { Visible: false }) return;

        if (node is Label label && !string.IsNullOrWhiteSpace(label.Text) && label.IsVisibleInTree())
        {
            var size = label.Size;
            // Height too: a label squeezed vertically is the same failure seen from the other
            // side, and it happens when a row is given no room rather than a column.
            if (size.X < Vanished || size.Y < Vanished)
                found.Add(new Finding(label.GetPath(), label.Text, size.X, size.Y,
                    Fault.Squeezed, 0f));
            else if (Cut(label, size, out var wanted))
                found.Add(new Finding(label.GetPath(), label.Text, size.X, size.Y,
                    Fault.Clipped, wanted));
        }

        var count = node.GetChildCount();
        for (var i = 0; i < count; i++) Walk(node.GetChild(i), found);
    }

    /// <summary>
    /// Is this label's text being cut somewhere a reader would notice?
    ///
    /// Only a clipped, unwrapped label can be: a wrapping label spends height instead of
    /// losing words, and an unclipped one simply overflows its box, which is a different
    /// (and visible) complaint.
    ///
    /// Then the alignment decides how strict to be. A right-aligned or centred label is cut
    /// at its start, and a sentence missing its first word is never acceptable, so any cut at
    /// all counts. A left-aligned one is cut at its end, which is the ordinary business of a
    /// narrow column, so it only counts once most of the text has gone.
    /// </summary>
    private static bool Cut(Label label, Vector2 size, out float wanted)
    {
        wanted = 0f;
        if (!label.ClipText || label.AutowrapMode != TextServer.AutowrapMode.Off) return false;

        var font = label.GetThemeFont("font");
        if (font is null) return false;
        var points = label.GetThemeFontSize("font_size");
        if (points <= 0) points = 16;

        // Measured unconstrained: what this string would take if nothing stopped it.
        wanted = font.GetStringSize(label.Text, HorizontalAlignment.Left, -1f, points).X;
        // A pixel of slack. A label sized exactly to its text rounds either way between the
        // layout pass and this measurement, and one rounded pixel is not a bug.
        if (wanted <= size.X + 1f) return false;

        return label.HorizontalAlignment is HorizontalAlignment.Right or HorizontalAlignment.Center
               || size.X / wanted < Legible;
    }

    /// <summary>The findings as a report, or a sentence saying there are none.</summary>
    public static string Report(Node root)
    {
        var found = Run(root);
        if (found.Count == 0)
            return DealOrForceGame.Tr("Every visible caption has room for its own text.");

        var lines = found.Take(12).Select(finding => "  " + Line(finding));
        return Simulation.Loc.Format("{0} caption(s) have not been given room for their text:",
                   found.Count.ToString("N0")) + "\n" + string.Join("\n", lines) +
               (found.Count > 12 ? $"\n  … and {found.Count - 12:N0} more" : "");
    }

    // ---- the watcher ----------------------------------------------------------------------
    //
    // The console command answers a question somebody thought to ask. The four captions this
    // file exists because of were never asked about, because nobody knew there was anything
    // to ask - which is why the same audit also runs on its own, once a second, in a debug
    // build, over whatever the developer happens to be looking at. Click through the game
    // normally and a squeezed caption announces itself the moment it appears on screen.
    //
    // It is silent in a shipped build: `OS.IsDebugBuild()` is false there, the walk never
    // happens, and a player never pays a frame for a developer's safety net.

    /// <summary>Seconds between sweeps. Slow enough to be free, quick enough to blame the
    /// panel you just opened rather than one from a minute ago.</summary>
    private const double WatchInterval = 2.5;

    private static double _sinceLastSweep;

    /// <summary>
    /// Findings already announced, keyed by the caption's own text.
    ///
    /// Keyed by text and not by path on purpose. Godot names a caption built in code
    /// `@Label@4387` and the number is different every time the panel is rebuilt, so a
    /// path-keyed memory would announce the same broken caption again on every refresh and
    /// the output would fill with a hundred copies of one bug.
    /// </summary>
    private static readonly HashSet<string> Announced = new(StringComparer.Ordinal);

    /// <summary>
    /// Watch the interface while the game runs. Call once per frame from a scene root; does
    /// nothing at all outside a debug build.
    /// </summary>
    public static void Watch(Node root, double delta)
    {
        if (!OS.IsDebugBuild() || root is null) return;
        _sinceLastSweep += delta;
        if (_sinceLastSweep < WatchInterval) return;
        _sinceLastSweep = 0d;

        foreach (var finding in Run(root))
        {
            if (!Announced.Add(finding.Text)) continue;
            var line = "[layout] " + Line(finding);
            // Both, and deliberately. The warning is what a developer notices without
            // looking for it; the print is what survives into the log a tool can read.
            GD.PushWarning(line);
            GD.Print(line);
        }
    }

    /// <summary>One finding as one line, the same wherever it is printed.</summary>
    private static string Line(Finding finding) =>
        finding.Fault == Fault.Squeezed
            ? $"\"{Shorten(finding.Text)}\" was given {finding.Width:0}x{finding.Height:0}px " +
              $"to show itself - {finding.Path}"
            : $"\"{Shorten(finding.Text)}\" is cut: {finding.Wanted:0}px of text in " +
              $"{finding.Width:0}px - {finding.Path}";

    private static string Shorten(string text) =>
        text.Length <= 40 ? text : text[..37] + "…";
}
