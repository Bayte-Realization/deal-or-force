using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The Godot side of <see cref="InterfaceSkin"/>: turns a named token into a real
/// <see cref="Color"/> and caches the result, so drawing code can ask for a role by name
/// on every frame without re-parsing a hex string each time.
///
/// The split matters. <see cref="InterfaceSkin"/> holds the data and knows nothing about
/// the engine, which is what lets the headless bench load and validate a mod's skin without
/// a renderer; this class is the only place that turns that data into engine types.
/// </summary>
internal static class Ui
{
    private static readonly Dictionary<string, Color> Cache = new();

    static Ui() => InterfaceSkin.Changed += Invalidate;

    /// <summary>A token's colour. Unknown tokens come back magenta, loudly and without throwing.</summary>
    internal static Color Colour(string token)
    {
        if (Cache.TryGetValue(token, out var cached)) return cached;
        var colour = new Color(InterfaceSkin.Colour(token));
        Cache[token] = colour;
        return colour;
    }

    /// <summary>A token's colour at a different opacity, without inventing a second token for it.</summary>
    internal static Color Colour(string token, float alpha)
    {
        var colour = Colour(token);
        colour.A = alpha;
        return colour;
    }

    internal static float Metric(string token, float fallback) => InterfaceSkin.Metric(token, fallback);

    /// <summary>
    /// A tooltip, broken into lines a person can read.
    ///
    /// Godot draws a tooltip one line per line it is handed and never wraps on its own, so a
    /// sentence of any length arrives as a thousand pixels of text running off the side of
    /// the screen. Every tooltip in the game goes through here for that reason.
    ///
    /// The measure is counted in characters rather than in pixels because a tooltip is
    /// composed long before it is given a font, and because it has to survive translation:
    /// the Russian of one of these lines runs about a third longer than the English, so a
    /// measure tuned in pixels on the English would break in the language being read.
    /// </summary>
    /// <summary>
    /// How wide a tooltip may get before it wraps, in characters. A preference, because
    /// what is comfortable depends on the display and on the reader.
    /// </summary>
    internal static int TooltipMeasure { get; set; } = 84;

    internal static string Tip(string text) => Tip(text, TooltipMeasure);

    internal static string Tip(string text, int measure)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Looked up before it is wrapped, and only here: a good half of the tooltips in
        // the game are handed a string that arrived as a variable and so never passed a
        // Tr of their own. Text already translated, or composed at runtime out of several
        // pieces, misses the table and comes back unchanged.
        text = Loc.Get(text);
        var wrapped = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length <= measure) { wrapped.Add(paragraph); continue; }
            var line = new System.Text.StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > measure)
                {
                    wrapped.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            wrapped.Add(line.ToString());
        }
        return string.Join("\n", wrapped);
    }

    /// <summary>
    /// Put a panel where the layout says it goes. Sixty-four anchor and offset assignments
    /// used to be spread across a dozen files; a panel is a name now, and the rectangle
    /// behind the name is data a mod can move.
    /// </summary>
    internal static T Place<T>(T control, string key) where T : Control
    {
        control.SetMeta("skin_layout", key);
        var rect = InterfaceSkin.Layout(key);
        control.AnchorLeft = rect.AnchorLeft;
        control.AnchorTop = rect.AnchorTop;
        control.AnchorRight = rect.AnchorRight;
        control.AnchorBottom = rect.AnchorBottom;
        control.OffsetLeft = rect.OffsetLeft;
        control.OffsetTop = rect.OffsetTop;
        control.OffsetRight = rect.OffsetRight;
        control.OffsetBottom = rect.OffsetBottom;
        return control;
    }

    internal static string ColourToken(Color colour)
    {
        foreach (var entry in InterfaceSkin.Tokens)
            if (new Color(entry.Value).IsEqualApprox(colour)) return entry.Token;
        return "";
    }

    /// <summary>Drop every cached brush. Called automatically when a mod reloads the skin.</summary>
    internal static void Invalidate() => Cache.Clear();
}
