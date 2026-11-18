using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Developer console commands for console meta, modding/content inspection, map navigation,
/// performance diagnostics, saves, and fun/curse routes.
/// </summary>
internal static class ConsoleDebugCommands
{
    public static void RegisterConsole(DeveloperConsole console, ConsoleContext ctx, RegisterConsoleCommand add)
    {
        add("help", "CONSOLE", "help [command]",
            "Every command, or what one command does and what it takes.",
            console.Help, "?", "h");
        add("commands", "CONSOLE", "commands [text]",
            "Every command name on one line, for finding one you half remember.",
            console.CommandList);
        add("clear", "CONSOLE", "clear", "Empty the console.", _ => console.Clear(), "cls");
        add("selftest", "CONSOLE", "selftest",
            "Run every command once with no arguments and report the ones that threw. " +
            "It touches the world - it pauses, raises an event, writes a save - so run it on " +
            "a campaign you are willing to lose, not on the one you care about.",
            _ => console.SelfTest());

        add("modaction", "CONTENT", "modaction [list|package:action]",
            "List declared actions from enabled mods, or explicitly run one loaded action.",
            text =>
            {
                var loader = ctx.GetModLoader();
                var id = text.Trim();
                if (id.Length == 0 || id.Equals("list", StringComparison.OrdinalIgnoreCase))
                    return string.Join("\n", loader.FrontendActions().Select(a => a.FullId + " · " + ctx.Tr(a.Caption)));
                loader.TryRunFrontendAction(id, out var note);
                return ctx.Tr(note);
            });
        add("feature", "MODDING", "feature · feature aviation off · feature aviation on",
            "Inspect or switch global scenario mechanics. Disabled content stays dormant, not deleted.",
            a => Feature(ctx, a));
    }

    public static void RegisterTools(DeveloperConsole console, ConsoleContext ctx, RegisterConsoleCommand add)
    {
        // MAP
        add("mapmode", "MAP", "mapmode pol|dip|war|pop|gdp", "What the globe is coloured by.",
            a => MapMode(ctx, a), "mm");
        add("camera", "MAP", "camera 55.7 37.6", "Point the camera at a latitude and longitude.",
            a => Focus(ctx, a), "focus");
        add("goto", "MAP", "goto bavaria", "Fly to a country or a region by name, and select it.",
            a => Goto(ctx, a), "fly");
        add("province", "MAP", "province [ID]",
            "Everything about one tactical province: region, owner, holder, ground, who is standing on it.",
            a => Province(ctx, a));
        add("region", "MAP", "region [name]",
            "Everything about one region: people, wealth, anger, buildings, what it might secede over.",
            a => Region(ctx, a), "state");
        add("freecam", "MAP", "freecam [on|off]", "The camera that flies, and its controls.",
            a => FreeCamera(ctx, a));

        // CONTENT
        add("effects", "CONTENT", "effects [filter]",
            "Every key content can write to, which is the vocabulary a modder has to guess at otherwise.",
            EffectVocabulary);
        add("loc", "CONTENT", "loc [russian] [template]",
            "Switch language, and report what that language has not translated yet. 'loc german template' starts a translation mod in user://mods with every line to translate.",
            a => LocalizationCmd(ctx, a), "localization", "language");
        add("skin", "CONTENT", "skin [filter]",
            "What the interface is painted with, and which tokens a mod has moved.",
            Skin, "interface");
        add("flags", "CONTENT", "flags [DEU]",
            "Which flag a country flies and why: every name the resolver tries, in order, " +
            "marked with whether the art exists.", a => Flags(ctx, a), "flag");
        add("defines", "CONTENT", "defines [filter]",
            "Every tunable number the simulation runs on, and what it is set to.",
            a => DefinesCmd(ctx, a));
        add("settings", "CONTENT", "settings [filter]",
            "Every setting the game declares, and what it is currently set to.", Settings);
        add("decisions", "CONTENT", "decisions",
            "What you could enact, what it costs, and the reason for each one you cannot.",
            _ => Decisions(ctx));
        add("decision", "CONTENT", "decision ID", "Enact one by id, cost and blocks ignored.",
            a => Decision(ctx, a));
        add("events", "CONTENT", "events [filter]", "The event catalogue, by id.", Events);
        add("event", "CONTENT", "event [ID]",
            "Raise an event now - a named one, or whatever the catalogue offers this country.",
            a => EventCmd(ctx, a));
        add("formable", "CONTENT", "formable",
            "Every state this country could proclaim itself, and for each one it cannot, what is missing.",
            _ => Formable(ctx));
        add("secession", "CONTENT", "secession [DEU]",
            "Which regions are pulling away, how hard, and which are close to going.",
            a => Secession(ctx, a), "discontent");
        add("secede", "CONTENT", "secede [region]",
            "Make a region declare independence now - the selected one, or the one named - " +
            "by the same road a secession takes on its own: a new country, its militia, and an " +
            "answer from the one it left.", a => Secede(ctx, a));
        add("reload", "CONTENT", "reload",
            "Reload interface/, gui/ and localization/ from game and the active packages. " +
            "Campaign state, gameplay rules, art and code are not reloaded. Ctrl+Shift+R.",
            _ => Reload(ctx));
        add("validate", "CONTENT", "validate",
            "Every complaint the data made while loading, mod by mod.",
            _ => Validate(ctx), "errors");

        // DEBUG & DIAGNOSTICS
        add("fps", "DEBUG", "fps", "Frames, frame time, draw calls and what the renderer is holding.",
            _ => Fps());
        add("tags", "DEBUG", "tags [on|off]", "Show or hide country IDs on the map and in country headers; no argument toggles.",
            a => ctx.ToggleCountryTags(a), "tdebug");
        add("sim", "DEBUG", "sim [filter]", "Where a simulated day is actually spent, slowest pass first.",
            SimulationCmd);
        add("mem", "DEBUG", "mem", "What the process is holding, and a collection on request.",
            Memory, "gc");
        add("logs", "DEBUG", "logs", "Where the diagnostic logs are and whether they are being written.",
            _ => Logs());
        add("saves", "DEBUG", "saves", "Every save on disk, newest first.", _ => Saves(ctx));
        add("save", "DEBUG", "save [slot]", "Write a save without leaving the map.", a => Save(ctx, a));
        add("load", "DEBUG", "load SLOT", "Load a save by slot.", a => Load(console, ctx, a));
        add("layout", "DEBUG", "layout",
            "Look at the interface on screen and name every caption that has been squeezed to " +
            "nothing. The bug that hid four headings in this project, findable in one command.",
            _ => ctx.MeasureLayout(), "captions");
        add("cadence", "DEBUG", "cadence [filter]",
            "How often each pass has run, per step - the quickest way to see a system that " +
            "thinks in days running on every step instead.", a => Cadence(ctx, a));
        add("plural", "DEBUG", "plural [key]",
            "The forms a counted sentence takes in the language that is loaded. One, two, five, " +
            "eleven, twenty-one - which is where a translation usually breaks.", Plural);
        add("report", "DEBUG", "report",
            "Write a bug report: build, machine, logs, settings and the newest save in one zip " +
            "under reports/. The button in Settings does the same thing.", _ => Report(ctx));
        add("memory", "DIAGNOSTICS", "memory [count]",
            "Where the game's memory is: the process, the managed heap, the renderer and the largest textures.",
            args => MemoryReport.Build(int.TryParse((args ?? "").Trim(), out var count) ? Math.Clamp(count, 1, 200) : 25));

        // FUN
        add("touchgrass", "FUN", "touchgrass",
            "A verse, and eight seconds later the game closes. It does not save first. " +
            "The text is in game/localization.", _ => ctx.TouchGrass());
        add("snowgrave", "FUN", "snowgrave",
            "The world freezes under a thick sheet of ice. The calendar goes on; nothing else ever " +
            "moves again, and the music stops for good. Saved with the world - there is no way back.",
            _ => ctx.CastSnowgrave());
        add("weirdroute", "FUN", "weirdroute",
            "The music stops, and every army in the world - and every one raised after - marches over " +
            "any border to the nearest water and walks into it. A world with no water gets static " +
            "instead. Saved with the world - there is no way back.",
            _ => ctx.CastWeirdRoute());
    }

    private static string Feature(ConsoleContext ctx, string arguments)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var words = ConsoleContext.Words(arguments);
        if (words.Length == 0)
            return ConsoleContext.EscapeBb(string.Join("\n", campaign.Defines.Features.Keys.Concat(campaign.FeatureOverrides.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id)
                .Select(id => $"{id}: {(campaign.FeatureEnabled(id) ? "on" : "off")}")));
        if (words.Length == 1) return ConsoleContext.EscapeBb($"{words[0]}: {(campaign.FeatureEnabled(words[0]) ? "on" : "off")}");
        if (words.Length != 2 || (words[1] != "on" && words[1] != "off")) return "Usage: feature aviation on|off";
        if (ctx.GetNetworkConnected()) return "Mechanic switches must be changed by deterministic scenario scripts in multiplayer.";
        if (!campaign.SetFeature(words[0], words[1] == "on")) return "Invalid mechanic id.";
        ctx.Refresh();
        return ConsoleContext.EscapeBb($"{words[0]}: {(campaign.FeatureEnabled(words[0]) ? "on" : "off")}");
    }

    private static string MapMode(ConsoleContext ctx, string value)
    {
        var mode = value.ToLowerInvariant() switch
        {
            "pol" or "political" => StrategicMapMode.Political,
            "dip" or "diplomatic" => StrategicMapMode.Diplomatic,
            "war" or "wars" => StrategicMapMode.Wars,
            "pop" or "population" => StrategicMapMode.Population,
            "gdp" or "economy" => StrategicMapMode.Economy,
            _ => (StrategicMapMode)(-1)
        };
        if (!Enum.IsDefined(mode)) return "Usage: mapmode pol/dip/war/pop/gdp";
        ctx.Globe?.SetMapMode(mode);
        return $"Map mode: {mode}.";
    }

    private static string Focus(ConsoleContext ctx, string value)
    {
        var values = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 2 || !ConsoleContext.TryFloat(values[0], out var latitude) || !ConsoleContext.TryFloat(values[1], out var longitude))
            return "Usage: focus LATITUDE LONGITUDE";
        ctx.Globe?.FocusOn(Math.Clamp(latitude, -89f, 89f), longitude, 7f);
        return $"Camera focused on {latitude:0.00}, {longitude:0.00}.";
    }

    private static string Goto(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        var globe = ctx.Globe;
        if (campaign is null || globe is null) return "No campaign.";
        var text = argument.Trim();
        if (text.Length == 0) return "Usage: goto bavaria";

        var state = ctx.ResolveState(text);
        if (state is not null)
        {
            var owner = campaign.Nations.FirstOrDefault(nation => nation.MapIndex == state.NationMapIndex);
            ctx.SetSelectedState(state);
            if (owner is not null) ctx.SetSelectedNation(owner);
            var seat = campaign.ProvinceMap is null ? (0f, 0f) : ctx.StateCentre(state);
            globe.FocusOn(Math.Clamp(seat.Item1, -89f, 89f), seat.Item2, 9f);
            ctx.Refresh();
            return ConsoleContext.EscapeBb($"{state.Name} · {owner?.Tag ?? "unowned"} · {Fmt.People(state.Population)}");
        }

        var nation = ctx.ResolveNation(text);
        if (nation is null) return $"Nothing called '{ConsoleContext.EscapeBb(text)}' on the map.";
        ctx.SetSelectedNation(nation);
        globe.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude, 8f);
        ctx.Refresh();
        return ConsoleContext.EscapeBb($"{nation.Name} ({nation.Tag}) · capital {nation.CapitalName}");
    }

    private static string Province(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.ProvinceMap is null) return "No campaign.";
        var province = ctx.GetSelectedProvinceId();
        if ((argument ?? "").Trim().Length > 0 && !int.TryParse(argument.Trim(), out province))
            return "Usage: province [ID]";
        var map = campaign.ProvinceMap;
        if (province <= 0 || province > map.ProvinceCount) return "Select a province, or give its number.";

        var stateIndex = map.ProvinceToState[province];
        var state = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == stateIndex);
        var owner = campaign.Nations.FirstOrDefault(nation => nation.MapIndex == map.ProvinceToNation[province]);
        var holderIndex = campaign.ProvinceController?[province] ?? 0;
        var holder = campaign.Nations.FirstOrDefault(nation => nation.MapIndex == holderIndex);
        var centre = map.CenterOf(province);
        var standing = campaign.Armies.Where(army => army.ProvinceId == province).ToArray();
        var battle = campaign.Battles.FirstOrDefault(item => !item.Ended && item.ProvinceId == province);

        return ConsoleContext.EscapeBb(
            $"Province #{province:N0} · region {state?.Name ?? stateIndex.ToString()} · " +
            $"{centre.Latitude:0.00}, {centre.Longitude:0.00}\n" +
            $"  owner {owner?.Tag ?? "none"} · held by {holder?.Tag ?? "none"}" +
            $" · occupation {campaign.ProvinceOccupation?[province] ?? 0f:P0}\n" +
            $"  ground {map.TerrainAt(province)?.Name ?? "unknown"}\n" +
            $"  standing here: {(standing.Length == 0 ? "nobody" : string.Join(", ", standing.Take(8).Select(army => army.Name)))}" +
            (battle is null ? "" : $"\n  a battle is being fought here, round {battle.Rounds.Count}"));
    }

    private static string Region(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var state = ctx.ResolveState(argument);
        if (state is null) return "Select a region, or name one: region bavaria";
        var owner = campaign.Nations.FirstOrDefault(nation => nation.MapIndex == state.NationMapIndex);
        var built = state.Buildings.Count == 0
            ? "nothing built"
            : string.Join(", ", state.Buildings.Select(entry => $"{entry.Key} ×{entry.Value}"));
        var building = campaign.Construction.Count(project => project.StateMapIndex == state.MapIndex);
        var pressure = owner is null ? 0f : SecessionCore.Pressure(campaign, owner, state);
        return ConsoleContext.EscapeBb(
            $"{state.Name} ({state.Iso}) · map index {state.MapIndex} · {owner?.Name ?? "unowned"}\n" +
            $"  people     {Fmt.People(state.Population)} · urban {state.Urbanization:P0} · " +
            $"literacy {state.Literacy:P0} · living {state.StandardOfLiving:P0}\n" +
            $"  temper     stability {state.Stability:P0} · radicalism {state.Radicalism:P0} · " +
            $"secession pressure {pressure:P0}\n" +
            $"  built      {built}{(building > 0 ? $" · {building} under construction" : "")}\n" +
            $"  groups     {state.PopulationGroups.Count:N0} population group(s) - `pops` breaks them down");
    }

    private static string FreeCamera(ConsoleContext ctx, string argument)
    {
        var enabled = argument.ToLowerInvariant() switch
        {
            "on" or "1" => true,
            "off" or "0" => false,
            _ => !ctx.GetFreeCameraMode()
        };
        if (ctx.Globe is not null && ctx.Globe.FreeCamera != enabled) ctx.ToggleFreeCamera();
        return enabled
            ? "Free camera on. Mouse looks, WASD flies, Space and Ctrl climb and descend, " +
              "Shift is faster and Alt is slower, the wheel sets the throttle.\n" +
              "Q and E roll the horizon, R levels it. Z and X are the lens, F puts it back.\n" +
              "G hangs the camera on a turntable around whatever it is looking at; on it, " +
              "A and D go round, W and S close and back off, Space and Ctrl raise and lower.\n" +
              "T sets the turntable turning on its own. C is the smoothing that makes it " +
              "footage rather than a hand on a mouse. H hides the readout. F10 or Escape lands it."
            : "Free camera off; the campaign HUD is back.";
    }

    private static string EffectVocabulary(string filter)
    {
        var wanted = (filter ?? "").Trim();
        var rows = ScriptedEffects.Vocabulary
            .Where(item => wanted.Length == 0 ||
                           item.Key.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                           item.Help.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .Select(item => $"  {item.Key} - {item.Help}")
            .ToArray();
        return rows.Length == 0
            ? $"No effect key matches '{ConsoleContext.EscapeBb(wanted)}'."
            : ConsoleContext.EscapeBb(string.Join(System.Environment.NewLine, rows.Prepend(
                $"{rows.Length} scripted effect key(s) (suffix _percent, _mult or _set to change the operation):")));
    }

    private static string LocalizationCmd(ConsoleContext ctx, string argument)
    {
        var words = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var template = words.Any(word => string.Equals(word, "template", StringComparison.OrdinalIgnoreCase));
        var named = words.FirstOrDefault(word => !string.Equals(word, "template", StringComparison.OrdinalIgnoreCase));
        if (template) return WriteTranslationTemplate((named ?? "").Trim().ToLowerInvariant());
        var language = string.IsNullOrWhiteSpace(named) ? Loc.Language : named.Trim();
        if (!Loc.Languages.Any(item => string.Equals(item, language, StringComparison.OrdinalIgnoreCase)))
            return $"Unknown language '{language}'. Loaded: {string.Join(", ", Loc.Languages)}";

        Loc.Language = language;
        ctx.RefreshLocalisedText();
        ctx.UpdateLanguageButton();
        var missing = Loc.MissingKeys(language);
        var header = $"Language: {Loc.DisplayName(language)} ({language}) · " +
                     $"{Loc.Keys(language).Count()} key(s) translated";
        return missing.Count == 0
            ? ConsoleContext.EscapeBb(header)
            : ConsoleContext.EscapeBb(header + $"\nUntranslated ({missing.Count}): " + string.Join(", ", missing.Take(12)));
    }

    private static string WriteTranslationTemplate(string language)
    {
        if (language.Length == 0 || language.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_' && ch != '-'))
            return "Usage: loc <language> template - one word for the language, as its folder is named: loc german template";
        var root = System.IO.Path.Combine(ProjectSettings.GlobalizePath("user://mods"), "translation_" + language);
        var folder = System.IO.Path.Combine(root, "localization", language);
        var file = System.IO.Path.Combine(folder, "00_" + language + ".json");
        if (System.IO.File.Exists(file))
            return ConsoleContext.EscapeBb($"{file} already exists; it is not overwritten. Move it aside to start again.");
        System.IO.Directory.CreateDirectory(folder);

        var written = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var manifest = System.IO.Path.Combine(root, "manifest.json");
        if (!System.IO.File.Exists(manifest))
            System.IO.File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["id"] = "translation_" + language,
                    ["name"] = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(language) + " translation",
                    ["version"] = "0.1.0",
                    ["supported_game_version"] = "*",
                    ["enabled"] = true,
                    ["load_order"] = 100,
                    ["dependencies"] = Array.Empty<string>()
                }, written), new System.Text.UTF8Encoding(false));

        var table = new Dictionary<string, string>
        {
            ["language.name"] = Loc.TextIn(language, "language.name") ??
                                System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(language),
            ["language.plural"] = Loc.TextIn(language, "language.plural") ?? "one-other",
            ["language.culture"] = Loc.TextIn(language, "language.culture") ?? ""
        };
        var untranslated = 0;
        foreach (var key in Loc.Keys(Loc.DefaultLanguage))
        {
            if (key.StartsWith("language.", StringComparison.Ordinal)) continue;
            var own = Loc.TextIn(language, key);
            if (own is null) untranslated++;
            table[key] = own ?? Loc.TextIn(Loc.DefaultLanguage, key) ?? key;
        }
        System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(table, written),
            new System.Text.UTF8Encoding(false));
        return ConsoleContext.EscapeBb($"Wrote {table.Count:N0} line(s) to {file} - {untranslated:N0} of them still in English. " +
                        "Set language.name, language.plural (one-other, slavic or single) and language.culture " +
                        "(such as de-DE) at the top, translate the rest, and restart: the language is on the selector.");
    }

    private static string Skin(string argument)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            var filter = argument.Trim();
            var matches = InterfaceSkin.Tokens
                .Where(entry => entry.Token.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(40)
                .Select(entry => $"  {ConsoleContext.Bb(entry.Token)}■[/color] {entry.Token} = {entry.Value}")
                .ToList();
            matches.AddRange(InterfaceSkin.Panels
                .Where(entry => entry.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(24)
                .Select(entry => $"  {entry.Key}  anchors [{entry.Rect.AnchorLeft:0.##}, " +
                                 $"{entry.Rect.AnchorTop:0.##}, {entry.Rect.AnchorRight:0.##}, " +
                                 $"{entry.Rect.AnchorBottom:0.##}]  offsets [{entry.Rect.OffsetLeft:0}, " +
                                 $"{entry.Rect.OffsetTop:0}, {entry.Rect.OffsetRight:0}, {entry.Rect.OffsetBottom:0}]"));
            return matches.Count == 0
                ? $"Nothing in the interface matches '{filter}'."
                : string.Join("\n", matches);
        }

        var overridden = InterfaceSkin.Overridden;
        var total = InterfaceSkin.Tokens.Count();
        if (overridden.Count == 0)
            return $"{total} interface tokens, all at their shipped values. " +
                   "'skin text' lists a family; a mod overrides them from interface/skin.json.";

        return $"{total} interface tokens · {overridden.Count} overridden by mods:\n" +
               string.Join("\n", overridden.Take(30).Select(token =>
                   $"  {ConsoleContext.Bb(token)}■[/color] {token} = {InterfaceSkin.Colour(token)}"));
    }

    private static string Flags(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        var candidates = FlagCatalog.Candidates(campaign, nation);
        var resolved = FlagCatalog.Resolve(campaign, nation);
        var lines = candidates.Take(24).Select(name =>
        {
            var exists = SpriteCatalog.Has(name);
            var mark = !exists ? " " : name == resolved ? ">" : "·";
            var colour = name == resolved ? ConsoleContext.Bb("state.positive") : exists ? ConsoleContext.Bb("text.body") : ConsoleContext.Bb("text.muted");
            return $"  {mark} {colour}{name}[/color]{(exists ? "" : "   (no such picture)")}";
        });
        return $"{nation.Tag} as {nation.Ideology} · {nation.Government}" +
               (nation.FormedNationId.Length > 0 ? $" · formed {nation.FormedNationId}" : "") + "\n" +
               $"flies {(resolved.Length == 0 ? "nothing - a plain rectangle" : resolved)}, " +
               $"chosen from {candidates.Count} candidate(s):\n" + string.Join("\n", lines);
    }

    private static string DefinesCmd(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Defines is null) return "No campaign, so no tunables to report.";
        var filter = argument.Trim();
        var rows = typeof(Defines).GetProperties()
            .Where(property => property.CanRead &&
                               (filter.Length == 0 ||
                                property.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Take(60)
            .Select(property =>
            {
                var value = property.GetValue(campaign.Defines);
                return $"  {property.Name,-34} {value}";
            })
            .ToArray();
        return rows.Length == 0
            ? $"No define matches '{ConsoleContext.EscapeBb(filter)}'."
            : ConsoleContext.EscapeBb(string.Join("\n", rows));
    }

    private static string Settings(string argument)
    {
        var filter = argument.Trim();
        var rows = SettingsCatalog.All
            .Where(setting => filter.Length == 0 ||
                              setting.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                              setting.Category.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(setting => setting.Category, StringComparer.Ordinal)
            .ThenBy(setting => setting.Order)
            .Take(50)
            .Select(setting => $"  {setting.Category,-12} {setting.Id,-28} {setting.Kind} = " +
                               $"{SettingsCatalog.Chosen(setting.Id)}")
            .ToArray();
        return rows.Length == 0
            ? $"No setting matches '{ConsoleContext.EscapeBb(filter)}'."
            : ConsoleContext.EscapeBb(string.Join("\n", rows));
    }

    private static string Decisions(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var nation = campaign.Player;
        var rows = DecisionCatalog.All.Select(definition =>
        {
            var blocked = DecisionCatalog.BlockReason(campaign, nation, definition);
            var cost = DecisionCatalog.Cost(nation, definition);
            var resources = DecisionResources.PriceText(nation, definition);
            var resourceLabel = resources.Length > 0 ? " · " + ConsoleContext.EscapeBb(resources) : "";
            return blocked.Length == 0
                ? $"  {ConsoleContext.Bb("state.positive")}{definition.Id}[/color]  ${cost:0.00}B{resourceLabel}"
                : $"  {ConsoleContext.Bb("text.muted")}{definition.Id}[/color]  ${cost:0.00}B{resourceLabel}  - {ConsoleContext.EscapeBb(blocked)}";
        }).ToArray();
        return rows.Length == 0 ? "The decision catalogue is empty." : string.Join("\n", rows);
    }

    private static string Decision(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var id = argument.Trim();
        var found = DecisionCatalog.Find(id);
        if (found is null) return $"No decision called '{ConsoleContext.EscapeBb(id)}'. Try `decisions`.";
        var definition = found.Value;
        var blocked = DecisionCatalog.BlockReason(campaign, campaign.Player, definition);
        if (blocked.Length > 0) return $"Blocked: {ConsoleContext.EscapeBb(blocked)}";
        campaign.Commands.Enqueue(new TakeDecisionCommand
        {
            IssuerId = campaign.Player.Id,
            DecisionId = definition.Id
        });
        return $"Enacted '{definition.Id}'. It applies on the next step.";
    }

    private static string Events(string argument)
    {
        var filter = argument.Trim();
        var rows = EventCatalog.All
            .Where(definition => filter.Length == 0 ||
                                 definition.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                 definition.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(40)
            .Select(definition => $"  {definition.Id,-34} {definition.Title}")
            .ToArray();
        return rows.Length == 0
            ? $"No event matches '{ConsoleContext.EscapeBb(filter)}'."
            : ConsoleContext.EscapeBb($"{EventCatalog.All.Count} event(s) in the catalogue:\n" + string.Join("\n", rows));
    }

    private static string EventCmd(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var id = (argument ?? "").Trim();
        if (id.Length == 0)
        {
            var drawn = EventSystem.Raise(campaign, campaign.Player.Id);
            if (drawn is null) return "Nothing in the event catalogue matches this country right now.";
            ctx.RefreshEvents();
            return $"Raised '{ConsoleContext.EscapeBb(drawn.Title)}'.";
        }

        var definition = EventCatalog.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (definition is null) return $"No event called '{ConsoleContext.EscapeBb(id)}'. Try `events`.";
        campaign.PendingEvents.Add(new PendingCampaignEvent
        {
            Id = campaign.ElapsedDays * 1000 + campaign.Player.Id,
            NationId = campaign.Player.Id,
            Title = definition.Title,
            Description = definition.Description,
            CreatedDay = campaign.ElapsedDays,
            ExpiresDay = campaign.ElapsedDays + 182,
            Options = definition.Options
        });
        ctx.RefreshEvents();
        return $"Raised '{ConsoleContext.EscapeBb(definition.Title)}' past its own trigger.";
    }

    private static string Formable(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var nation = campaign.Player;
        var rows = FormableNations.All.Select(definition =>
        {
            var eligible = FormableNations.Eligible(nation, definition);
            var mark = eligible ? ConsoleContext.Bb("state.positive") + ">" : ConsoleContext.Bb("text.muted") + " ";
            return $"  {mark} {definition.Id}[/color] · {ConsoleContext.EscapeBb(definition.Name)}";
        }).ToArray();
        return rows.Length == 0
            ? "No formable states are declared."
            : $"{nation.Tag} against {rows.Length} formable state(s):\n" + string.Join("\n", rows);
    }

    private static string Secession(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        var rows = campaign.StatesOf(nation)
            .Select(state => (State: state, Pressure: SecessionCore.Pressure(campaign, nation, state)))
            .Where(row => row.Pressure > 0.01f)
            .OrderByDescending(row => row.Pressure)
            .Take(20)
            .Select(row => $"  {row.Pressure,6:P0}  {row.State.Name} · " +
                           $"radicalism {row.State.Radicalism:P0} · {Fmt.People(row.State.Population)}")
            .ToArray();
        return rows.Length == 0
            ? $"Nothing in {nation.Tag} is pulling away."
            : ConsoleContext.EscapeBb($"{nation.Tag}: regions by how hard they are pulling away\n" + string.Join("\n", rows));
    }

    private static string Secede(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        var state = ctx.ResolveState(argument);
        if (state is null || campaign is null) return "Usage: secede Crimea - or select a region first.";
        var report = SecessionEvents.Force(campaign, state);
        if (report?.Seceded is null)
            return $"{state.Name} cannot go on its own: a lake, a region with no owner, or its country's last.";
        campaign.MarkMapChanged();
        ctx.Refresh();
        return $"{report.Seceded.Name} ({report.Seceded.Tag}) has left {report.Parent.Name}, " +
               $"{report.Population:N0} people - {report.Outcome}.";
    }

    private static string Reload(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var errors = PresentationReload.Reload(ctx.GetModLoader().Packages.Select(package => package.PhysicalRoot)).ToList();
        ctx.ApplySkinOverlay(errors);
        clock.Stop();
        ctx.RepaintPresentation();
        ctx.RefreshLocalisedText();
        ctx.LayoutCampaignShell();
        ctx.Refresh();
        return $"Reloaded interface, GUI and localization in {clock.ElapsedMilliseconds:N0} ms. " +
               "Campaign state and package selection unchanged. Standard panels, buttons and captions updated in place.\n" +
               (errors.Count == 0 ? "No loading errors." :
                   ConsoleContext.EscapeBb($"{errors.Count} loading error(s); valid files applied, invalid values skipped:\n" +
                       string.Join("\n", errors.Take(10))));
    }

    private static string Validate(ConsoleContext ctx)
    {
        var loader = ctx.GetModLoader();
        var packages = loader.Packages.Where(package => package.Errors.Count > 0).ToArray();
        if (packages.Length == 0)
            return $"{loader.Packages.Count} package(s) loaded and none of them complained.";
        return ConsoleContext.EscapeBb(string.Join("\n", packages.Select(package =>
            $"  {package.Manifest?.Name ?? package.VirtualRoot} · {package.Errors.Count} complaint(s)\n" +
            string.Join("\n", package.Errors.Take(10).Select(error => $"      {error}")))));
    }

    private static string Fps() =>
        $"{Engine.GetFramesPerSecond()} fps · process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0:0.00} ms · " +
        $"physics {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0:0.00} ms\n" +
        $"draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):N0} · " +
        $"primitives {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame):N0} · " +
        $"video memory {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:N0} MB\n" +
        $"objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):N0} · " +
        $"nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):N0}";

    private static string SimulationCmd(string filter)
    {
        var wanted = (filter ?? "").Trim();
        var samples = SimProfiler.All
            .Where(sample => wanted.Length == 0 ||
                             sample.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sample => sample.AverageMilliseconds)
            .Take(wanted.Length == 0 ? 14 : 40)
            .ToArray();
        if (samples.Length == 0)
            return wanted.Length == 0
                ? "No profiler samples yet; resume time first."
                : $"No pass matches '{ConsoleContext.EscapeBb(wanted)}'.";
        var total = samples.Sum(sample => sample.AverageMilliseconds);
        return ConsoleContext.EscapeBb(string.Join("\n", samples.Select(sample =>
            $"  {sample.Name,-34} {sample.LastMilliseconds,8:0.000} ms last · " +
            $"{sample.AverageMilliseconds,8:0.000} ms avg · {sample.AverageMilliseconds / Math.Max(0.0001, total),5:P0}")));
    }

    private static string Memory(string argument)
    {
        var before = GC.GetTotalMemory(false);
        var collected = "";
        if (argument.Trim().Equals("gc", StringComparison.OrdinalIgnoreCase))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            collected = $" · freed {(before - GC.GetTotalMemory(true)) / 1048576.0:N1} MB";
        }
        return $"managed heap {GC.GetTotalMemory(false) / 1048576.0:N1} MB{collected}\n" +
               $"collections: gen0 {GC.CollectionCount(0):N0} · gen1 {GC.CollectionCount(1):N0} · " +
               $"gen2 {GC.CollectionCount(2):N0}\n" +
               $"Godot static memory {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:N1} MB. " +
               "`mem gc` forces a collection.";
    }

    private static string Logs() =>
        $"Logging: {(DiagnosticLogs.Enabled ? "active" : "inactive")}\n" +
        $"Folder: {DiagnosticLogs.DirectoryPath}\n" +
        $"Written lines: {DiagnosticLogs.WrittenLines}\n" +
        $"Last failure: {DiagnosticLogs.LastFailure}";

    private static string Saves(ConsoleContext ctx)
    {
        var saveRoot = ctx.GetSaveRoot();
        var saves = SaveSystem.List(saveRoot);
        return saves.Count == 0
            ? $"No saves in {saveRoot}."
            : ConsoleContext.EscapeBb(string.Join("\n", saves.Take(20).Select(save =>
                $"  {save.Slot,-24} {save.NationTag} · {save.GameDate} · day {save.ElapsedDays:N0} · " +
                $"{save.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}")));
    }

    private static string Save(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var slot = argument.Trim();
        if (slot.Length == 0) slot = $"console_{campaign.Player.Tag}_{campaign.ElapsedDays}";
        var metadata = SaveSystem.Save(ctx.GetSaveRoot(), slot, campaign, ctx.GetModFingerprint(), slot);
        return $"Saved to '{metadata.Slot}' at {metadata.GameDate}.";
    }

    private static string Load(DeveloperConsole console, ConsoleContext ctx, string argument)
    {
        var slot = argument.Trim();
        if (slot.Length == 0) return "Usage: load SLOT - `saves` lists them.";
        if (SaveSystem.List(ctx.GetSaveRoot()).All(save =>
                !string.Equals(save.Slot, slot, StringComparison.OrdinalIgnoreCase)))
            return $"No save called '{ConsoleContext.EscapeBb(slot)}'. `saves` lists them.";
        console.Close();
        _ = ctx.RestoreCampaignFromSlot(slot);
        return "";
    }

    private static string Cadence(ConsoleContext ctx, string argument)
    {
        var filter = argument.Trim();
        var samples = SimProfiler.All
            .Where(sample => filter.Length == 0 ||
                             sample.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sample => sample.Runs)
            .Take(20)
            .ToArray();
        if (samples.Length == 0) return "Nothing has been measured yet. Let the clock run first.";
        var campaign = ctx.Campaign;
        var steps = Math.Max(1, campaign?.ElapsedHours / Math.Max(1, campaign.TickHours) ?? 1);
        return $"Runs per pass, against {steps:N0} step(s) of {campaign?.TickHours ?? 24}h so far:\n" +
               string.Join("\n", samples.Select(sample =>
                   $"  {sample.Name,-18} ×{sample.Runs,-9:N0} {sample.Runs / (double)steps,6:0.##} per step  " +
                   $"{sample.AllocatedBytes / (double)Math.Max(1, sample.Runs) / 1024.0,7:N1} kB/run"));
    }

    private static string Plural(string argument)
    {
        var key = argument.Trim();
        if (key.Length == 0) key = "{0:N0} division selected.";
        var rule = Loc.GetOr("language.plural", "one-other");
        return $"'{Loc.Language}' counts by the '{rule}' rule.\n" +
               string.Join("\n", new[] { 1, 2, 5, 11, 21, 22, 101 }.Select(count =>
                   $"  {count,4} -> {Loc.Format(Simulation.Plural.Key(key, count), count)}"));
    }

    private static string Report(ConsoleContext ctx)
    {
        var path = ctx.CreateSupportReport();
        return path.Length > 0
            ? "Report written: " + path
            : "A report could not be written: " + SupportReport.LastFailure;
    }
}
