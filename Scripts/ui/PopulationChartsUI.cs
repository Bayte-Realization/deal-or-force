using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>One slice of a composition: what it is, how many, and what colour it is drawn.</summary>
    private readonly record struct Slice(string Name, long Size, Color Colour, string Tooltip);

    /// <summary>
    /// A composition, as a bar and then as a list.
    ///
    /// The bar is the answer to "what is this place" in one glance; the rows underneath are
    /// the answer to "how much of it", which is the question you ask second. Drawing only
    /// the rows makes the reader do the addition; drawing only the bar makes them guess at
    /// the numbers. Both, in that order, is the whole trick.
    ///
    /// Built from ColorRects with stretch ratios rather than a custom canvas: the layout
    /// engine already knows how to divide a width in proportion, and a bar that is really a
    /// row of controls can carry a tooltip on every segment.
    /// </summary>
    private void DetailComposition(string heading, IReadOnlyList<Slice> slices, long total, int rows = 8)
    {
        if (_sideDetail is null) return;
        var kept = slices.Where(slice => slice.Size > 0).ToList();
        if (kept.Count == 0) return;
        DetailHeading(heading);

        var bar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 18) };
        bar.AddThemeConstantOverride("separation", 1);
        _sideDetail.AddChild(bar);
        var shown = Math.Max(1L, total);
        foreach (var slice in kept)
        {
            var segment = new ColorRect
            {
                Color = slice.Colour,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = Math.Max(0.004f, slice.Size / (float)shown),
                CustomMinimumSize = new Vector2(2, 18),
                TooltipText = Ui.Tip(Simulation.Loc.Format("{0} · {1} ({2:P1})", slice.Name, Fmt.People(slice.Size), slice.Size / (double)shown))
            };
            bar.AddChild(segment);
        }
        // Whatever the listed slices do not cover. Without it a bar of the top eight peoples
        // reads as if they were everybody.
        var covered = kept.Sum(slice => slice.Size);
        if (covered < shown * 0.999f)
            bar.AddChild(new ColorRect
            {
                Color = Ui.Colour("surface.line"),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = Math.Max(0.004f, (shown - covered) / (float)shown),
                CustomMinimumSize = new Vector2(2, 18),
                TooltipText = Ui.Tip(Simulation.Loc.Format("Everyone else · {0}", Fmt.People(shown - covered)))
            });

        foreach (var slice in kept.Take(rows))
            DetailSwatch(slice, shown);
        if (kept.Count > rows)
            DetailRow(Simulation.Loc.Format("…and {0} more", kept.Count - rows),
                Fmt.People(kept.Skip(rows).Sum(slice => slice.Size)),
                "Groups too small to list separately. They are in the bar above.", "text.muted");
    }

    /// <summary>One legend row: a square of the slice's colour, its name, and its share.</summary>
    private void DetailSwatch(Slice slice, long total)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.CustomMinimumSize = new Vector2(0, 20);
        row.TooltipText = Ui.Tip(slice.Tooltip);
        _sideDetail.AddChild(row);

        row.AddChild(new ColorRect
        {
            Color = slice.Colour,
            CustomMinimumSize = new Vector2(11, 11),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        });
        var name = Title(slice.Name, 13, Ui.Colour("text.body"));
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(name);
        var value = Title($"{slice.Size / (double)Math.Max(1, total):P1}", 13, Ui.Colour("text.bright"));
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.CustomMinimumSize = new Vector2(52, 0);
        row.AddChild(value);
        var count = Title(Fmt.People(slice.Size), 13, Ui.Colour("text.muted"));
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.CustomMinimumSize = new Vector2(64, 0);
        row.AddChild(count);
    }

    /// <summary>A colour from the data, falling back to something legible rather than to black.</summary>
    private static Color Swatch(string hex, string fallbackToken = "text.muted")
    {
        if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith("#", StringComparison.Ordinal))
        {
            var colour = Color.FromString(hex, new Color(0, 0, 0, 0));
            if (colour.A > 0f) return colour;
        }
        return Ui.Colour(fallbackToken);
    }

    private IReadOnlyList<Slice> CultureSlices(ScenarioState state, Nation owner)
    {
        var primary = DemographyCatalog.PrimaryCultures(owner?.Tag ?? "");
        return EconomySystem.Cultures(state).Select(entry =>
        {
            var culture = DemographyCatalog.Culture(entry.Culture);
            var ours = primary.Contains(entry.Culture, StringComparer.OrdinalIgnoreCase);
            var name = (ours ? "★ " : "") + DemographyCatalog.CultureName(entry.Culture);
            var group = string.IsNullOrWhiteSpace(culture?.Group)
                ? ""
                : Simulation.Loc.Format("{0} peoples", Titled(culture.Group)) + "\n";
            return new Slice(name, entry.Size, Swatch(culture?.Colour), group + (ours
                ? "One of the peoples this state is for. They are the calm everything else is measured against."
                : "A minority here: governed by a state that is not theirs. More restive than their " +
                  "neighbours, and assimilating slowly - faster where people are schooled and comfortable."));
        }).ToList();
    }

    private static IReadOnlyList<Slice> ReligionSlices(ScenarioState state) =>
        EconomySystem.Religions(state).Select(entry =>
        {
            var religion = DemographyCatalog.Religion(entry.Religion);
            var group = string.IsNullOrWhiteSpace(religion?.Group) ? "" : $"{Titled(religion.Group)}\n";
            return new Slice(DemographyCatalog.ReligionName(entry.Religion), entry.Size,
                Swatch(religion?.Colour),
                group + Simulation.Loc.Format("{0} people", Fmt.PeopleExact(entry.Size)));
        }).ToList();

    /// <summary>
    /// A region's people cut by what they believe rather than by who they are.
    ///
    /// The shares are the same ideologies the government itself is chosen from, read from the
    /// region's own conditions, the laws it lives under and the form of government it lives in
    /// - so a rich city and a poor countryside in one country are not told they agree. The
    /// government's own position is starred, because the interesting question about a region is
    /// not which position is largest but whether the largest one is the one in power.
    /// </summary>
    private IReadOnlyList<Slice> PoliticalSlices(ScenarioState state, Nation owner)
    {
        if (owner is null) return Array.Empty<Slice>();
        var people = Math.Max(1L, state.Population);
        var governing = IdeologyCatalog.Knows(owner.Ideology) ? owner.Ideology : "";
        var slices = new List<Slice>();
        foreach (var share in PoliticalMood.RegionShares(_campaign, state, owner))
        {
            if (share.Share <= 0f) continue;
            IdeologyDefinition ideology = null;
            foreach (var candidate in IdeologyCatalog.All)
                if (string.Equals(candidate.Id, share.Ideology, StringComparison.OrdinalIgnoreCase))
                { ideology = candidate; break; }
            var mine = string.Equals(share.Ideology, governing, StringComparison.OrdinalIgnoreCase);
            var name = (mine ? "★ " : "") + Simulation.Loc.Get(ideology?.Name ?? share.Name);
            var size = (long)Math.Round(share.Share * people);
            var tooltip = Simulation.Loc.Format("{0} people lean this way", Fmt.PeopleExact(size)) + "\n" +
                (mine
                    ? "The position the government itself holds. What it holds here is the mandate it governs this region with."
                    : "An opposition position. A region whose largest leaning is not the government's is a region nobody in the capital is speaking for.");
            slices.Add(new Slice(name, size, Swatch(ideology?.Colour), tooltip));
        }
        return slices;
    }

    private static IReadOnlyList<Slice> ProfessionSlices(ScenarioState state) =>
        EconomySystem.Professions(state).Select(entry =>
        {
            var profession = DemographyCatalog.Profession(entry.Profession);
            // Both axes, on the trade itself. The two lists below the trades are these
            // two fields added up, and saying so here is what makes that legible.
            var social = profession is null
                ? ""
                : $"{ClassName(profession.Class)} · {SectorName(profession.Sector)}\n";
            return new Slice(DemographyCatalog.ProfessionName(entry.Profession), entry.Size,
                Swatch(profession?.Colour),
                social + Simulation.Loc.Format("{0} people", Fmt.PeopleExact(entry.Size)) +
                (profession is null ? "" : "\n" + Simulation.Loc.Format(
                    "Wealth {0:P0} · employed {1:P0}", profession.Wealth, profession.Employment)));
        }).ToList();

    private static string Titled(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" :
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
}
