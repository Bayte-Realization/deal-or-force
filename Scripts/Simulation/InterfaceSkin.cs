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

        // The production page's own palette: the card its lines sit on, the two economies
        // the war-industry slider moves between, and the states a factory cell can be in.
        // Mirrors game/gui/28_production_units.json, which ships the same values.
        ("production.card",           "#172630"),
        ("production.border",         "#374a54"),
        ("production.inset",          "#0c151b"),
        ("production.empty_border",  "#4d626b"),
        ("production.empty_icon",    "#37454c"),
        ("production.blocked",        "#252e34"),
        ("production.civilian",       "#a5c588"),
        ("production.civilian_fill",  "#293b2f"),
        ("production.military",       "#dac082"),
        ("production.military_fill",  "#443b26"),
        ("production.hover",          "#394749"),
        ("production.focus",          "#f5dfa2"),
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

        // The campaign shell - the frame the screen sits in and the striped rows inside it.
        // These were named in game/gui/20_campaign_structure.json and never added here, so
        // every one of them counted as a mod override of a default that did not exist. A
        // token the shipped data defines and the defaults do not is also a token a modder
        // copying the defaults never sees.
        //
        // shell.space is what the globe hangs against when a skin supplies no sky of its
        // own, which is why it is a shade deeper than globe.space rather than equal to it.
        ("shell.background",    "#101d28f7"),
        ("shell.border",        "#536775"),
        ("shell.row",           "#152532"),
        ("shell.row_alternate", "#1b2d3b"),
        ("shell.space",         "#080f18"),

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
        ("globe.city",                   "#eef2f4"),
        ("globe.city_outline",           "#0b1117"),
        ("globe.city_capital",           "#f7df92"),

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

        // The Wars screen - its cards, the bar with the front line on it, and what a war has
        // cost. Mirrors game/gui/36_war_ledger.json, which ships the same values.
        ("war.card",              "#132230"),
        ("war.card_hover",        "#1a2d3d"),
        ("war.card_border",       "#2b3e4f"),
        ("war.card_border_hover", "#9c8540"),
        ("war.card_mine",         "#c7a94e"),
        ("war.track",             "#0a131c"),
        ("war.front_line",        "#f0d575"),
        ("war.inset",             "#0e1a25"),
        ("war.inset_border",      "#273848"),
        ("war.killed",            "#c2474f"),
        ("war.wounded",           "#cf9446"),
        ("war.captured",          "#7e93aa"),
    };

    private static readonly (string Key, float Value)[] DefaultMetrics =
    {
        ("button.corner_radius",  3f),
        ("button.border_width",   1f),
        ("button.focus_width",    2f),
        ("panel.texture_margin", 16f),
    };

    /// <summary>
    /// Where a panel sits, in Godot's own terms: four anchors as fractions of the screen
    /// and four offsets in pixels from them. Left, top, right, bottom, in that order.
    /// </summary>
    public readonly record struct PanelRect(
        float AnchorLeft, float AnchorTop, float AnchorRight, float AnchorBottom,
        float OffsetLeft, float OffsetTop, float OffsetRight, float OffsetBottom);

    private static readonly Dictionary<string, PanelRect> Layouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where every panel the game builds sits.
    ///
    /// This is the second half of the gui folder. Colour was already data; geometry was
    /// sixty-four anchor and offset assignments spread across a dozen files, so a mod that
    /// wanted a wider ledger or the army list on the right had to fork the interface. A
    /// panel is a name and a rectangle now, and a mod overrides the rectangle.
    ///
    /// Only placement is data. What goes inside a panel is still code - a layout file
    /// cannot invent a new screen - but everything the base game builds can be moved,
    /// resized or pushed to the other side of the map without touching it.
    /// </summary>
    private static readonly (string Key, PanelRect Rect)[] DefaultLayouts =
    {
        // The campaign HUD
        //
        // Eight of these had drifted from what game/gui/10_campaign_shell.json actually
        // ships - the HUD was rearranged in the data, where the layout editor writes, and
        // the defaults were never brought along. Data wins at runtime, so the player saw
        // the new arrangement and only a modder copying the defaults would have met the
        // old one. They are the shipped numbers now.
        ("hud.top",            new PanelRect(0f, 0f, 1f, 0f,       8f, 6f, -8f, 80f)),
        ("hud.navigation",     new PanelRect(0f, 0f, 0f, 1f,       8f, 90f, 74f, -60f)),
        ("hud.side_panel",     new PanelRect(0f, 0f, 0f, 1f,      84f, 90f, 526f, -60f)),
        ("hud.map_controls",   new PanelRect(1f, 1f, 1f, 1f,    -620f, -52f, -8f, -8f)),
        ("hud.alerts",         new PanelRect(0.5f, 0f, 0.5f, 0f, -390f, 110f, 390f, 168f)),
        ("hud.outliner",       new PanelRect(1f, 0f, 1f, 0f,    -300f, 90f, -8f, 620f)),
        ("hud.army_panel",     new PanelRect(0f, 0f, 0f, 1f,      84f, 90f, 590f, -60f)),
        // Top-anchored and as tall as what it holds, rather than stretched to the bottom of
        // the screen: a fleet panel is a dozen lines, and the rest of the column was empty.
        ("hud.fleet_panel",    new PanelRect(0f, 0f, 0f, 0f,      84f, 90f, 526f, 610f)),
        // What a left click on the map selects: the country, as tall as what it holds, and
        // its region and province along the bottom, clear of the map controls.
        ("hud.country_card",   new PanelRect(0f, 0f, 0f, 0f,      84f, 90f, 526f, 760f)),
        ("hud.region_card",    new PanelRect(0f, 1f, 0f, 1f,     530f, -262f, 1350f, -8f)),
        ("hud.peace_panel",    new PanelRect(1f, 0f, 1f, 1f,    -470f, 100f, -18f, -100f)),
        ("hud.hotseat",        new PanelRect(0f, 1f, 0f, 1f,      84f, -54f, 365f, -6f)),
        ("hud.network",        new PanelRect(0f, 1f, 0f, 1f,      84f, -54f, 425f, -6f)),

        // Full-screen and modal panels
        ("menu.main",          new PanelRect(0.5f, 0.5f, 0.5f, 0.5f, -270f, -400f, 270f, 400f)),
        ("picker.heading",     new PanelRect(0f, 0f, 0f, 0f,      18f, 18f, 455f, 106f)),
        ("picker.country",     new PanelRect(1f, 0.5f, 1f, 0.5f, -420f, -338f, -18f, 338f)),
        ("picker.actions",     new PanelRect(0f, 1f, 0f, 1f,      18f, -126f, 638f, -18f)),
        ("modal.pause",        new PanelRect(0.5f, 0.5f, 0.5f, 0.5f, -210f, -245f, 210f, 245f)),
        ("modal.event",        new PanelRect(0.5f, 0.5f, 0.5f, 0.5f, -310f, -225f, 310f, 225f)),
        ("modal.peace_offer",  new PanelRect(0.5f, 0.5f, 0.5f, 0.5f, -330f, -200f, 330f, 200f)),
        ("modal.settings",     new PanelRect(0.5f, 0.5f, 0.5f, 0.5f, -440f, -350f, 440f, 350f)),
        ("modal.save_load",    new PanelRect(0.5f, 0.5f, 0.5f, 0.5f, -420f, -360f, 420f, 360f)),
        ("modal.handover",     new PanelRect(0.5f, 0.5f, 0.5f, 0.5f, -250f, -160f, 250f, 160f)),
        ("modal.multiplayer",  new PanelRect(0.5f, 0.5f, 0.5f, 0.5f, -285f, -340f, 285f, 340f)),
        ("modal.ledger",       new PanelRect(0.055f, 0.115f, 0.945f, 0.90f, 0f, 0f, 0f, 0f)),
        ("modal.war_ledger",   new PanelRect(0.055f, 0.115f, 0.945f, 0.90f, 0f, 0f, 0f, 0f)),

        // The three full pages of the campaign - diplomacy, trade and production - take
        // the same rectangle the ledger and the war ledger take: most of the screen, with
        // the map still visible behind the shade and the top bar still readable above it.
        ("modal.diplomacy",    new PanelRect(0.055f, 0.115f, 0.945f, 0.90f, 0f, 0f, 0f, 0f)),
        ("modal.trade",        new PanelRect(0.055f, 0.115f, 0.945f, 0.90f, 0f, 0f, 0f, 0f)),
        // The production page's own file (game/gui/28_production_units.json) ships the panel it
        // finished at: wider than the other three pages, and sitting higher. Mirrored here,
        // which is the invariant the bench holds - a default a modder cannot reproduce is a
        // default that does not exist.
        ("modal.production",   new PanelRect(0.044f, 0.084f, 0.044f, 0.944f, 0f, 0f, 850f, 0f)),
        ("modal.support",      new PanelRect(0.045f, 0.115f, 0.795f, 0.73f, 0f, 0f, 0f, 0f)),
        // The long-range forces panel docks beside the map, the way the army panel does: its
        // orders are given looking at the bases and their reach, so the map stays in view.
        ("hud.support_panel",  new PanelRect(0f, 0f, 0f, 1f,      84f, 90f, 690f, -60f)),
        ("modal.research",     new PanelRect(0.055f, 0.10f, 0.975f, 0.94f, 0f, 0f, 0f, 0f)),

        // The lobby and the workshop. These three were laid out in game/gui only - named in
        // 22_multiplayer.json and 24_workshop.json and never given a compiled default - so
        // the code's answer for them was the blank full-screen rectangle Layout() falls back
        // to, and a mod that removed the data file would have got that instead of a panel.
        ("multiplayer.connection", new PanelRect(0f, 0f, 0f, 1f,    12f, 12f, 368f, -12f)),
        ("multiplayer.countries",  new PanelRect(1f, 0f, 1f, 1f,  -324f, 12f, -12f, -12f)),
        ("workshop.panel",         new PanelRect(0f, 0f, 1f, 1f,    40f, 30f, -40f, -30f)),
    };

    /// <summary>A panel's rectangle, or a sane full-screen one if the name is unknown.</summary>
    public static PanelRect Layout(string key) =>
        key is not null && Layouts.TryGetValue(key, out var rect)
            ? rect
            : new PanelRect(0f, 0f, 1f, 1f, 40f, 40f, -40f, -40f);

    public static bool HasLayout(string key) => key is not null && Layouts.ContainsKey(key);

    /// <summary>Every panel name and its rectangle, for the console and for a drift check.</summary>
    public static IEnumerable<(string Key, PanelRect Rect)> Panels =>
        Layouts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value));

    /// <summary>Panels whose rectangle a mod has moved off the shipped one.</summary>
    public static IReadOnlyList<string> MovedPanels
    {
        get
        {
            var defaults = DefaultLayouts.ToDictionary(entry => entry.Key, entry => entry.Rect,
                StringComparer.OrdinalIgnoreCase);
            return Layouts
                .Where(pair => !defaults.TryGetValue(pair.Key, out var shipped) || !shipped.Equals(pair.Value))
                .Select(pair => pair.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// Merge one layout section. Anchors and offsets are independent, so a mod that only
    /// wants a panel forty pixels wider writes the offsets and inherits the anchors.
    /// </summary>
    private static void MergeLayouts(JsonElement element, string context, List<string> errors)
    {
        foreach (var entry in element.EnumerateObject())
        {
            if (IsComment(entry.Name)) continue;
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"{context}: panel '{entry.Name}' must be an object with anchors and offsets.");
                continue;
            }

            var current = Layout(entry.Name);
            var anchors = Numbers(entry.Value, "anchors");
            var offsets = Numbers(entry.Value, "offsets");
            if (anchors is null && offsets is null)
            {
                errors?.Add($"{context}: panel '{entry.Name}' sets neither anchors nor offsets.");
                continue;
            }

            Layouts[entry.Name] = new PanelRect(
                anchors?[0] ?? current.AnchorLeft, anchors?[1] ?? current.AnchorTop,
                anchors?[2] ?? current.AnchorRight, anchors?[3] ?? current.AnchorBottom,
                offsets?[0] ?? current.OffsetLeft, offsets?[1] ?? current.OffsetTop,
                offsets?[2] ?? current.OffsetRight, offsets?[3] ?? current.OffsetBottom);
        }
    }

    /// <summary>Four numbers, or nothing. A partial rectangle is a mistake, not a shorthand.</summary>
    private static float[] Numbers(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        var values = new float[4];
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (index >= 4 || !item.TryGetSingle(out var number)) return null;
            values[index++] = number;
        }
        return index == 4 ? values : null;
    }

    /// <summary>Reload the base-game skin. Mods layer on top afterwards via <see cref="Merge"/>.</summary>
    public static void ResetCatalog()
    {
        CampaignUiCatalog.Reset();
        Colours.Clear();
        Metrics.Clear();
        foreach (var (key, value) in DefaultColours) Colours[key] = value;
        foreach (var (key, value) in DefaultMetrics) Metrics[key] = value;
        Layouts.Clear();
        foreach (var (key, rect) in DefaultLayouts) Layouts[key] = rect;

        // Colour lives in interface/, placement lives in gui/, the way a Paradox title
        // splits them - but both are the same kind of file and both are read the same way,
        // so a mod may put either in either and nothing breaks.
        foreach (var folder in new[] { "interface", "gui" })
        {
            GameDataLoader.Asked.Add(folder);
            var root = FindSkinRoot(folder);
            if (root is not null) LoadDirectory(root, null);
        }

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
        if (root.TryGetProperty("campaign", out var campaign))
        {
            sectioned = true;
            CampaignUiCatalog.Merge(campaign, errors);
        }
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

        if (root.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
        {
            sectioned = true;
            MergeLayouts(layout, context, errors);
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

    private static string FindSkinRoot(string folder = "interface")
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "game", folder);
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
