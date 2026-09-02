using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Every colour and measurement the interface draws with, as named tokens in one table,
/// loaded the layered way all content is: base game first, then each enabled mod in load
/// order, later wins.
///
/// This is the first half of the `gui/` folder Paradox titles ship. It does not yet let a
/// mod move a panel or add a screen - the layout is still built in code - but it does mean
/// no colour in the game is compiled in any more. A mod that wants a parchment interface
/// instead of a dark one drops `interface/skin.json` into its own root and overrides the
/// eight text tokens; it does not fork twenty-one UI files.
///
/// Tokens are named by role, not by appearance: `text.caption`, not `text.grey`. That is
/// what makes an override coherent - a modder retinting `text.caption` moves every
/// section heading at once, because the base game had already agreed with itself about
/// what a section heading is.
///
/// Every token has a compiled-in default, so a missing or malformed file costs the offending
/// keys and nothing else - the interface still draws.
/// </summary>
public static class InterfaceSkin
{
    private static readonly Dictionary<string, string> Colours = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> Metrics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever the table is rebuilt, so cached brushes can be dropped.</summary>
    public static event Action Changed;

    static InterfaceSkin() => ResetCatalog();

    /// <summary>
    /// The palette the game ships with. Deliberately small: five steps of text, one accent,
    /// one set of button states, five semantic colours and the map-mode ramps. Anything the
    /// interface needs that is not in here is a token that has not been named yet.
    /// </summary>
    private static readonly (string Key, string Value)[] DefaultColours =
    {
        // Text - a five-step ramp from the brightest figure to the faintest footnote,
        // plus the gold that marks a heading and the caption grey above a control.
        ("text.accent",         "#e7c966"),
        ("text.accent_dim",     "#d3bf72"),
        ("text.bright",         "#eef4fb"),
        ("text.body",           "#e9eef5"),
        ("text.body_soft",      "#c8d3e0"),
        ("text.muted",          "#9dabbc"),
        ("text.hint",           "#8190a2"),
        ("text.faint",          "#718299"),
        ("text.caption",        "#8e9daf"),

        // Semantic - reserved for meaning, never for decoration. Anything drawn in
        // state.negative is genuinely bad news for the player.
        ("state.positive",      "#4fb477"),
        ("state.negative",      "#c94f55"),
        ("state.warning",       "#e7b058"),
        ("state.info",          "#77c9e8"),
        ("state.critical",      "#ef8c62"),

        // Surfaces
        ("surface.background",  "#060b13"),
        ("surface.panel_tint",  "#c7dbf0fa"),
        ("surface.overlay",     "#0d1722e8"),
        ("surface.inset",       "#172635"),
        ("surface.line",        "#50677d"),
        ("surface.line_faint",  "#33455688"),

        // Buttons
        ("button.text",             "#edf2f7"),
        ("button.text_hover",       "#fff0b0"),
        ("button.text_pressed",     "#d7c581"),
        ("button.text_disabled",    "#667586"),
        ("button.fill",             "#1a2735"),
        ("button.fill_hover",       "#273647"),
        ("button.fill_pressed",     "#101923"),
        ("button.fill_disabled",    "#111923"),
        ("button.border",           "#60758a"),
        ("button.border_hover",     "#edcf68"),
        ("button.border_pressed",   "#9f8845"),
        ("button.border_disabled",  "#354351"),
        ("button.primary_fill",         "#55451f"),
        ("button.primary_fill_hover",   "#6b5620"),
        ("button.primary_fill_pressed", "#382f19"),
        ("button.primary_border",       "#c7a94e"),

        // Map modes. A total conversion repainting the political map does it here.
        ("map.player",          "#4fb477"),
        ("map.at_war",          "#c94f55"),
        ("map.allied",          "#4e83c7"),
        ("map.friendly",        "#4ea6a0"),
        ("map.rival",           "#c77943"),
        ("map.neutral",         "#49515d"),
        ("map.war_self",        "#d3b34e"),
        ("map.war_attacker",    "#b85443"),
        ("map.war_defender",    "#4b75aa"),
        ("map.war_bystander",   "#7b687f"),
        ("map.value_low",       "#173b56"),
        ("map.value_mid",       "#3aa6a0"),
        ("map.value_high",      "#f0c85b"),
        ("map.terrain_low",     "#3c294f"),
        ("map.terrain_mid",     "#477f73"),
        ("map.terrain_high",    "#d6c45d"),

        // Globe
        ("globe.space",                  "#030914"),
        ("globe.ambient",                "#9eb7d4"),
        ("globe.label",                  "#f2f5f7"),
        ("globe.label_outline",          "#111821"),
        ("globe.label_minor",            "#d9e4e9"),
        ("globe.label_minor_outline",    "#10161d"),
        ("globe.label_province",         "#e8f5f7"),
        ("globe.label_province_outline", "#10202a"),
        ("globe.selection",              "#f4d66b"),

        // Formation counters
        ("military.routed",         "#ef8c62"),
        ("military.shaken",         "#e7b058"),
        ("military.planning",       "#f3d36c"),
        ("military.ready",          "#d9e4e9"),
        ("military.battle",         "#ffb34d"),
        ("military.battle_outline", "#631f1b"),
        ("military.clash",          "#fff1cf"),
        ("military.clash_outline",  "#291613"),

        // Chart series. Six hues that stay apart from each other at small sizes; a mod
        // adding a seventh good picks its colour from here rather than inventing one.
        ("series.gold",         "#e6c760"),
        ("series.blue",         "#65a9df"),
        ("series.green",        "#55c58a"),
        ("series.red",          "#e06b6b"),
        ("series.violet",       "#a98be3"),
        ("series.orange",       "#e49a56"),

        // Charts
        ("chart.tick",      "#687a8c"),
        ("chart.hover",     "#f3d46fcc"),
        ("chart.row_hover", "#fff0b0"),

        // Goods and buildings. Content colours, so a mod adding a good picks its own.
        ("goods.industrial_materials", "#aeb8c2"),
        ("goods.coal",                 "#5f6871"),
        ("goods.oil",                  "#403a52"),
        ("goods.iron_ore",             "#a65f45"),
        ("goods.rare_earths",          "#4ec8b6"),
        ("building.infrastructure",    "#9eb0c2"),
        ("building.extraction",        "#a65f45"),
        ("building.power_plant",       "#dfb14a"),
        ("building.arms_industry",     "#b9414b"),
    };

    private static readonly (string Key, float Value)[] DefaultMetrics =
    {
        ("button.corner_radius",  3f),
        ("button.border_width",   1f),
        ("button.focus_width",    2f),
        ("panel.texture_margin", 16f),
    };

    /// <summary>Reload the base-game skin. Mods layer on top afterwards via <see cref="Merge"/>.</summary>
    public static void ResetCatalog()
    {
        Colours.Clear();
        Metrics.Clear();
        foreach (var (key, value) in DefaultColours) Colours[key] = value;
        foreach (var (key, value) in DefaultMetrics) Metrics[key] = value;

        var root = FindSkinRoot();
        if (root is not null) LoadDirectory(root, null);

        Changed?.Invoke();
    }

    /// <summary>Layer every `*.json` in one `interface/` directory over what is already loaded.</summary>
    public static void LoadDirectory(string directory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try { Merge(File.ReadAllText(file), $"interface/{Path.GetFileName(file)}", errors); }
            catch (Exception exception) { errors?.Add($"interface/{Path.GetFileName(file)}: {exception.Message}"); }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Merge one skin file. Both spellings of the section are accepted, and a bare
    /// `{ "text.accent": "#fff" }` works too - a modder retinting one token should not have
    /// to remember which envelope it lives in.
    /// </summary>
    public static void Merge(string json, string context, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{context} must be an object of token/value pairs.");
            return;
        }

        var sectioned = false;
        foreach (var section in new[] { "colors", "colours" })
            if (root.TryGetProperty(section, out var element) && element.ValueKind == JsonValueKind.Object)
            {
                sectioned = true;
                MergeColours(element, context, errors);
            }

        if (root.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
        {
            sectioned = true;
            MergeMetrics(metrics, context, errors);
        }

        if (!sectioned) MergeColours(root, context, errors);
    }

    private static void MergeColours(JsonElement element, string context, List<string> errors)
    {
        foreach (var entry in element.EnumerateObject())
        {
            if (IsComment(entry.Name)) continue;
            if (entry.Value.ValueKind == JsonValueKind.Number)
            {
                Metrics[entry.Name] = (float)entry.Value.GetDouble();
                continue;
            }
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                errors?.Add($"{context}: '{entry.Name}' must be a colour string such as \"#e7c966\".");
                continue;
            }

            var value = entry.Value.GetString() ?? "";
            if (!IsColour(value))
            {
                errors?.Add($"{context}: '{entry.Name}' = \"{value}\" is not #rgb, #rrggbb or #rrggbbaa.");
                continue;
            }
            Colours[entry.Name] = value;
        }
    }

    private static void MergeMetrics(JsonElement element, string context, List<string> errors)
    {
        foreach (var entry in element.EnumerateObject())
        {
            if (IsComment(entry.Name)) continue;
            if (entry.Value.ValueKind != JsonValueKind.Number)
            {
                errors?.Add($"{context}: metric '{entry.Name}' must be a number.");
                continue;
            }
            Metrics[entry.Name] = (float)entry.Value.GetDouble();
        }
    }

    /// <summary>
    /// A token's colour as a hex string. An unknown token returns a deliberately loud
    /// magenta rather than throwing: a typo in a mod should be obvious on screen, not fatal.
    /// </summary>
    public static string Colour(string token) =>
        token is not null && Colours.TryGetValue(token, out var value) ? value : "#ff00ff";

    public static bool Has(string token) => token is not null && Colours.ContainsKey(token);

    public static float Metric(string token, float fallback) =>
        token is not null && Metrics.TryGetValue(token, out var value) ? value : fallback;

    /// <summary>Every token and its current value, for the console and for documentation.</summary>
    public static IEnumerable<(string Token, string Value)> Tokens =>
        Colours.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value));

    /// <summary>Tokens whose value differs from the shipped default - what a mod actually changed.</summary>
    public static IReadOnlyList<string> Overridden
    {
        get
        {
            var defaults = DefaultColours.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            return Colours
                .Where(pair => !defaults.TryGetValue(pair.Key, out var shipped) ||
                               !string.Equals(shipped, pair.Value, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// The shipped skin uses real `//` line comments, which the parser is already told to
    /// skip. A key that opens with the same marker is honoured too, for the editors that
    /// refuse to save JSON with comments in it.
    /// </summary>
    private static bool IsComment(string name) =>
        string.IsNullOrEmpty(name) || name.StartsWith("//", StringComparison.Ordinal) || name[0] == '#';

    private static bool IsColour(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '#') return false;
        var digits = value.Length - 1;
        if (digits is not (3 or 4 or 6 or 8)) return false;
        for (var index = 1; index < value.Length; index++)
            if (!Uri.IsHexDigit(value[index]))
                return false;
        return true;
    }

    private static string FindSkinRoot()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "game", "interface");
            if (Directory.Exists(directory)) return directory;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
            yield return directory.FullName;
    }
}
