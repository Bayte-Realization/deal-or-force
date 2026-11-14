using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DealOrForce.Launcher;

/// <summary>
/// The release notes as a page: the changelog is Markdown, and the launcher reads the small
/// part of it the notes actually use - headings, paragraphs, bullet lists (with their wrapped
/// continuation lines), **bold**, `code` and [links](...) shown as their text. Anything else is
/// shown as the text it is. No package: a Markdown library would be the largest thing in the
/// launcher for one page of notes.
/// </summary>
public static partial class LauncherNotes
{
    public static FlowDocument Render(string markdown, Brush heading, Brush body, Brush muted, Brush code)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5, Foreground = body,
            PagePadding = new Thickness(2, 0, 12, 8), LineHeight = 21, TextAlignment = TextAlignment.Left,
        };
        List? list = null;
        Paragraph? paragraph = null;
        var buffer = new List<string>();
        var first = true;

        void Flush()
        {
            if (buffer.Count == 0) return;
            var text = string.Join(" ", buffer.Select(b => b.Trim()));
            buffer.Clear();
            if (list is not null)
            {
                var item = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                Inlines(item, text, code);
                list.ListItems.Add(new ListItem(item));
            }
            else
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                Inlines(paragraph, text, code);
                document.Blocks.Add(paragraph);
            }
        }
        void EndList() { Flush(); list = null; }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) { Flush(); EndList(); continue; }
            var level = line.TakeWhile(c => c == '#').Count();
            if (level is > 0 and <= 4 && line.Length > level && line[level] == ' ')
            {
                EndList();
                var title = new Paragraph
                {
                    FontSize = level == 1 ? 22 : level == 2 ? 16.5 : 14.5,
                    FontWeight = level == 1 ? FontWeights.Normal : FontWeights.SemiBold,
                    FontFamily = level == 1 ? new FontFamily("Georgia") : new FontFamily("Segoe UI"),
                    Foreground = heading,
                    Margin = new Thickness(0, first ? 0 : level == 2 ? 16 : 10, 0, level == 1 ? 4 : 6),
                };
                Inlines(title, line[(level + 1)..].Trim(), code);
                document.Blocks.Add(title);
                first = false;
                continue;
            }
            first = false;
            var bullet = Regex.Match(line, @"^\s*[-*]\s+(.*)$");
            if (bullet.Success)
            {
                Flush();
                if (list is null)
                {
                    list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(18, 0, 0, 0) };
                    document.Blocks.Add(list);
                }
                buffer.Add(bullet.Groups[1].Value);
                continue;
            }
            // A line under a heading that is all emphasis - the date line of a release - reads
            // quieter than the body.
            if (document.Blocks.LastBlock is Paragraph { FontSize: 22 } && buffer.Count == 0 && list is null)
            {
                var date = new Paragraph { Foreground = muted, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 12) };
                Inlines(date, line.Trim(), code);
                document.Blocks.Add(date);
                continue;
            }
            buffer.Add(line);
        }
        Flush();
        return document;
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Span();

    private static void Inlines(Paragraph target, string text, Brush code)
    {
        var at = 0;
        foreach (Match match in Span().Matches(text))
        {
            if (match.Index > at) target.Inlines.Add(new Run(text[at..match.Index]));
            if (match.Groups[1].Success) target.Inlines.Add(new Bold(new Run(match.Groups[1].Value)));
            else if (match.Groups[2].Success) target.Inlines.Add(new Run(match.Groups[2].Value) { Foreground = code, FontFamily = new FontFamily("Consolas") });
            else target.Inlines.Add(new Run(match.Groups[3].Value));
            at = match.Index + match.Length;
        }
        if (at < text.Length) target.Inlines.Add(new Run(text[at..]));
    }
}
