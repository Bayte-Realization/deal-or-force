using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _developerConsoleLayer = null!;
    private RichTextLabel _developerConsoleOutput = null!;
    private LineEdit _developerConsoleInput = null!;
    private bool _observerMode;
    private bool _freeCameraMode;

    /// <summary>
    /// What has been typed, oldest first, and where Up and Down currently are in it.
    ///
    /// A console without this is one where testing the same command with a different number
    /// means typing the whole line again, which is most of what a developer console is used
    /// for. The index sits one past the end when nothing is being recalled.
    /// </summary>
    private readonly List<string> _consoleHistory = new();

    private int _consoleHistoryAt;

    private void BuildDeveloperConsole()
    {
        _developerConsoleLayer = FullLayer("DeveloperConsole");
        _developerConsoleLayer.Visible = false;
        _developerConsoleLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_developerConsoleLayer);

        var shade = new ColorRect { Color = new Color(0.01f, 0.016f, 0.025f, 0.48f) };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _developerConsoleLayer.AddChild(shade);

        var panel = TexturedPanel(_developerConsoleLayer);
        panel.AnchorLeft = 0.04f;
        panel.AnchorRight = 0.72f;
        panel.AnchorTop = 0.03f;
        panel.AnchorBottom = 0.48f;
        var rows = PanelRows(panel, 18, 14, 7);
        var header = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
            Alignment = BoxContainer.AlignmentMode.Begin
        };
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        var title = Title("COMMAND CONSOLE", 18, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(title);
        var closeHint = Title("~ / Esc to close", 12, Ui.Colour("text.hint"));
        closeHint.CustomMinimumSize = new Vector2(112, 28);
        closeHint.VerticalAlignment = VerticalAlignment.Center;
        closeHint.HorizontalAlignment = HorizontalAlignment.Right;
        header.AddChild(closeHint);
        var close = ActionButton("×", CloseDeveloperConsole, 28, 28);
        close.TooltipText = Ui.Tip(Tr("Close console (~ or Esc)"));
        header.AddChild(close);

        _developerConsoleOutput = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 260),
            Text = Bb("text.accent") + "Deal or Force console[/color]\n" +
                   Bb("state.info") + "help[/color] lists everything · " +
                   Bb("state.info") + "help NAME[/color] explains one · Tab completes · " +
                   "Up and Down are the history\nCommands are local and deterministic."
        };
        _developerConsoleOutput.AddThemeFontSizeOverride("normal_font_size", 14);
        rows.AddChild(_developerConsoleOutput);

        // Through Placeholder rather than straight into the field, so the hint follows a change
        // of language like every other caption instead of staying in the one it was built in.
        _developerConsoleInput = Placeholder(new LineEdit
        {
            CustomMinimumSize = new Vector2(0, 38),
            ClearButtonEnabled = true
        }, "Enter command…");
        _developerConsoleInput.TextSubmitted += ExecuteDeveloperCommand;
        _developerConsoleInput.GuiInput += ConsoleInputKey;
        rows.AddChild(_developerConsoleInput);
        EnsureConsoleCommands();
    }

    private void ToggleDeveloperConsole()
    {
        if (_developerConsoleLayer is null) return;
        if (_developerConsoleLayer.Visible) CloseDeveloperConsole();
        else
        {
            _developerConsoleLayer.Visible = true;
            _developerConsoleLayer.MoveToFront();
            _developerConsoleInput.GrabFocus();
        }
        SyncFreeCameraPointer();
    }

    private void CloseDeveloperConsole()
    {
        if (_developerConsoleLayer is null) return;
        _developerConsoleLayer.Visible = false;
        _developerConsoleInput.ReleaseFocus();
        SyncFreeCameraPointer();
    }

    private void ExecuteDeveloperCommand(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return;
        _developerConsoleOutput.AppendText($"\n{Bb("text.caption")}> {EscapeBb(raw)}[/color]");
        _developerConsoleInput.Clear();
        // Remembered before it is run, so a command that throws is still one Up away.
        if (_consoleHistory.Count == 0 || _consoleHistory[^1] != raw) _consoleHistory.Add(raw);
        if (_consoleHistory.Count > 200) _consoleHistory.RemoveAt(0);
        _consoleHistoryAt = _consoleHistory.Count;

        EnsureConsoleCommands();
        var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : "";
        if (!_consoleByName.TryGetValue(command, out var entry))
        {
            _developerConsoleOutput.AppendText(
                $"\n{Bb("state.warning")}No command '{EscapeBb(command)}'.[/color]{ConsoleSuggestion(command)}");
            _developerConsoleInput.GrabFocus();
            return;
        }

        try
        {
            var response = entry.Run(argument);
            if (!string.IsNullOrEmpty(response)) _developerConsoleOutput.AppendText($"\n{response}");
        }
        catch (Exception exception)
        {
            DiagnosticLogs.Write("errors", $"CONSOLE_EXCEPTION command={command} error={exception}");
            _developerConsoleOutput.AppendText(
                $"\n{Bb("state.negative")}{EscapeBb(entry.Name)} failed: {EscapeBb(exception.Message)}[/color]" +
                $"\n{Bb("text.muted")}{EscapeBb(entry.Usage)}[/color]");
        }
        _developerConsoleInput.GrabFocus();
    }

    /// <summary>
    /// Tab, Up and Down. Godot hands the line edit every key first, so these three have to be
    /// taken here and swallowed, or Tab moves the focus out of the console and Up walks the
    /// caret instead of the history.
    /// </summary>
    private void ConsoleInputKey(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key) return;
        switch (key.Keycode)
        {
            case Key.Tab:
                ConsoleComplete();
                _developerConsoleInput.AcceptEvent();
                break;
            case Key.Up:
                ConsoleRecall(-1);
                _developerConsoleInput.AcceptEvent();
                break;
            case Key.Down:
                ConsoleRecall(1);
                _developerConsoleInput.AcceptEvent();
                break;
        }
    }

    private void ConsoleRecall(int step)
    {
        if (_consoleHistory.Count == 0) return;
        _consoleHistoryAt = Math.Clamp(_consoleHistoryAt + step, 0, _consoleHistory.Count);
        _developerConsoleInput.Text = _consoleHistoryAt >= _consoleHistory.Count
            ? ""
            : _consoleHistory[_consoleHistoryAt];
        _developerConsoleInput.CaretColumn = _developerConsoleInput.Text.Length;
    }

    /// <summary>
    /// Finish the command being typed. One match completes it; several print themselves and
    /// fill in as far as they agree, which is the behaviour every shell has trained everybody
    /// to expect.
    /// </summary>
    private void ConsoleComplete()
    {
        var typed = _developerConsoleInput.Text ?? "";
        if (typed.Contains(' ')) return;
        var matches = ConsoleCompletions(typed);
        if (matches.Count == 0) return;
        if (matches.Count == 1)
        {
            _developerConsoleInput.Text = matches[0] + " ";
            _developerConsoleInput.CaretColumn = _developerConsoleInput.Text.Length;
            return;
        }

        var shared = matches[0];
        foreach (var match in matches)
        {
            var length = 0;
            while (length < shared.Length && length < match.Length &&
                   char.ToLowerInvariant(shared[length]) == char.ToLowerInvariant(match[length])) length++;
            shared = shared[..length];
        }
        _developerConsoleInput.Text = shared;
        _developerConsoleInput.CaretColumn = shared.Length;
        _developerConsoleOutput.AppendText($"\n{Bb("text.muted")}{string.Join("  ", matches)}[/color]");
    }

    /// <summary>BBCode markers that follow the skin, so the console recolours with the game.</summary>
    private static string Bb(string token) => "[color=" + Simulation.InterfaceSkin.Colour(token) + "]";

    /// <summary>Open the peace conference for whatever war the player is fighting.</summary>
    /// <summary>
    /// The free camera captures the pointer, and a console you cannot click is not a
    /// console. Opening it hands the mouse back and closing it takes it again.
    /// </summary>
    private void SyncFreeCameraPointer()
    {
        if (_globe is null || !_globe.FreeCamera) return;
        Input.MouseMode = _developerConsoleLayer is { Visible: true }
            ? Input.MouseModeEnum.Visible
            : Input.MouseModeEnum.Captured;
    }

    private string ConsolePeaceConference()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var war = _campaign.Wars.FirstOrDefault(candidate => !candidate.Ended && candidate.Involves(_campaign.Player.Id));
        if (war is null) return $"{_campaign.Player.Name} is not at war.";
        CloseDeveloperConsole();
        OpenPeaceConference(war);
        return "";
    }

    /// <summary>
    /// What the interface is actually painted with. With no argument it reports which
    /// tokens a mod has moved off their shipped values - the fastest way to tell a broken
    /// reskin from a broken layout.
    /// </summary>
    private static string ConsoleSkin(string argument)
    {
        var skin = typeof(Simulation.InterfaceSkin);
        if (!string.IsNullOrWhiteSpace(argument))
        {
            var filter = argument.Trim();
            var matches = Simulation.InterfaceSkin.Tokens
                .Where(entry => entry.Token.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(40)
                .Select(entry => $"  {Bb(entry.Token)}■[/color] {entry.Token} = {entry.Value}")
                .ToList();
            matches.AddRange(Simulation.InterfaceSkin.Panels
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

        var overridden = Simulation.InterfaceSkin.Overridden;
        var total = Simulation.InterfaceSkin.Tokens.Count();
        if (overridden.Count == 0)
            return $"{total} interface tokens, all at their shipped values. " +
                   "'skin text' lists a family; a mod overrides them from interface/skin.json.";

        return $"{total} interface tokens · {overridden.Count} overridden by mods:\n" +
               string.Join("\n", overridden.Take(30).Select(token =>
                   $"  {Bb(token)}■[/color] {token} = {Simulation.InterfaceSkin.Colour(token)}"));
    }

    /// <summary>Proves the emergent economy still reproduces the authored 2024 figures.</summary>
    private string ConsoleEconomy()
    {
        if (_campaign is null) return "No campaign.";
        var nation = _selectedNation ?? _campaign.Player;
        var lines = new System.Text.StringBuilder(Simulation.EconomySystem.LastCalibrationReport);
        if (nation is not null)
        {
            var emergent = nation.GrossOutput * 52f * nation.OutputScale;
            lines.Append($"\n{nation.Tag} now: GDP {nation.Gdp:N1}B, emergent target {emergent:N1}B, " +
                         $"staffed {nation.EmployedShare:P0}, productivity {nation.Productivity:0.###}");
        }
        return EscapeBb(lines.ToString());
    }

    /// <summary>Lists every key content can write to, so a modder never has to guess.</summary>
    private static string ConsoleEffectVocabulary(string filter)
    {
        var wanted = (filter ?? "").Trim();
        var rows = Simulation.ScriptedEffects.Vocabulary
            .Where(item => wanted.Length == 0 ||
                           item.Key.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                           item.Help.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .Select(item => $"  {item.Key} - {item.Help}")
            .ToArray();
        return rows.Length == 0
            ? $"No effect key matches '{EscapeBb(wanted)}'."
            : EscapeBb(string.Join(System.Environment.NewLine, rows.Prepend(
                $"{rows.Length} scripted effect key(s) (suffix _percent, _mult or _set to change the operation):")));
    }

    /// <summary>Translation coverage, the way a translator wants to see it.</summary>
    private string ConsoleLocalization(string argument)
    {
        var words = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var template = words.Any(word => string.Equals(word, "template", StringComparison.OrdinalIgnoreCase));
        var named = words.FirstOrDefault(word => !string.Equals(word, "template", StringComparison.OrdinalIgnoreCase));
        if (template) return WriteTranslationTemplate((named ?? "").Trim().ToLowerInvariant());
        var language = string.IsNullOrWhiteSpace(named) ? Simulation.Loc.Language : named.Trim();
        if (!Simulation.Loc.Languages.Any(item => string.Equals(item, language, StringComparison.OrdinalIgnoreCase)))
            return $"Unknown language '{language}'. Loaded: {string.Join(", ", Simulation.Loc.Languages)}";

        Simulation.Loc.Language = language;
        RefreshLocalisedText();
        if (_languageButton is not null && GodotObject.IsInstanceValid(_languageButton))
        {
            var caption = LanguageButtonLabel();
            _languageButton.Text = caption;
            _languageButton.SetMeta(LocAppliedMeta, caption);
        }
        var missing = Simulation.Loc.MissingKeys(language);
        var header = $"Language: {Simulation.Loc.DisplayName(language)} ({language}) · " +
                     $"{Simulation.Loc.Keys(language).Count()} key(s) translated";
        return missing.Count == 0
            ? EscapeBb(header)
            : EscapeBb(header + $"\nUntranslated ({missing.Count}): " + string.Join(", ", missing.Take(12)));
    }

    /// <summary>
    /// Start a translation: a mod in user://mods holding every string the game has, ready to be
    /// written over. What the language already has is kept; the rest arrives in English.
    ///
    /// The whole of what makes a translation "like an official one" is in that folder - the
    /// language's name for itself, its plural rule and its culture at the top, then every line -
    /// and the game picks it up at the next start with nothing compiled: the language selector,
    /// the fallback to English for a line not done yet, the bench check that no line breaks.
    /// </summary>
    private static string WriteTranslationTemplate(string language)
    {
        if (language.Length == 0 || language.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_' && ch != '-'))
            return "Usage: loc <language> template - one word for the language, as its folder is named: loc german template";
        var root = System.IO.Path.Combine(ProjectSettings.GlobalizePath("user://mods"), "translation_" + language);
        var folder = System.IO.Path.Combine(root, "localization", language);
        var file = System.IO.Path.Combine(folder, "00_" + language + ".json");
        if (System.IO.File.Exists(file))
            return EscapeBb($"{file} already exists; it is not overwritten. Move it aside to start again.");
        System.IO.Directory.CreateDirectory(folder);

        var written = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var manifest = System.IO.Path.Combine(root, "manifest.json");
        if (!System.IO.File.Exists(manifest))
            System.IO.File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["id"] = "translation_" + language,
                    ["name"] = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(language) + " translation",
                    ["version"] = "0.1.0",
                    ["supported_game_version"] = "*",
                    ["enabled"] = true,
                    ["load_order"] = 100,
                    ["dependencies"] = System.Array.Empty<string>()
                }, written), new System.Text.UTF8Encoding(false));

        // What the language says about itself first, then every line the English table holds.
        var table = new System.Collections.Generic.Dictionary<string, string>
        {
            ["language.name"] = Simulation.Loc.TextIn(language, "language.name") ??
                                System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(language),
            ["language.plural"] = Simulation.Loc.TextIn(language, "language.plural") ?? "one-other",
            ["language.culture"] = Simulation.Loc.TextIn(language, "language.culture") ?? ""
        };
        var untranslated = 0;
        foreach (var key in Simulation.Loc.Keys(Simulation.Loc.DefaultLanguage))
        {
            if (key.StartsWith("language.", StringComparison.Ordinal)) continue;
            var own = Simulation.Loc.TextIn(language, key);
            if (own is null) untranslated++;
            table[key] = own ?? Simulation.Loc.TextIn(Simulation.Loc.DefaultLanguage, key) ?? key;
        }
        System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(table, written),
            new System.Text.UTF8Encoding(false));
        return EscapeBb($"Wrote {table.Count:N0} line(s) to {file} - {untranslated:N0} of them still in English. " +
                        "Set language.name, language.plural (one-other, slavic or single) and language.culture " +
                        "(such as de-DE) at the top, translate the rest, and restart: the language is on the selector.");
    }

    private string ClearConsole()
    {
        _developerConsoleOutput.Text = Bb("text.accent") + "Console cleared.[/color]";
        return "";
    }

    private string ConsoleTag(string tag)
    {
        var nation = _campaign?.NationByTag(tag);
        if (nation is null) return "Usage: tag KOR — unknown country tag.";
        _observerMode = false;
        _campaign.ConfigureSinglePlayer(nation.Id);
        ClearArmySelectionAndClose();
        _selectedNation = nation;
        _globe.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude, 8f);
        Refresh();
        return $"Player country changed to {nation.Name} ({nation.Tag}).";
    }

    private string ConsoleObserve()
    {
        if (_campaign is null) return "No campaign.";
        _observerMode = true;
        foreach (var nation in _campaign.Nations) nation.IsHuman = false;
        _campaign.HumanSeats.Clear();
        ClearArmySelectionAndClose();
        FrontPlanner.AssignAi(_campaign);
        _identityTag.Text = Tr("OBS · observer mode");
        return "Observer mode enabled. Country AI controls every nation; use tag TAG to return.";
    }

    private string ConsolePause()
    {
        TogglePause();
        return _campaign.IsPaused ? "Game paused." : "Game resumed.";
    }

    private string ConsoleSpeed(string value)
    {
        if (!int.TryParse(value, out var speed) || speed is < 1 or > 5) return "Usage: speed 1-5";
        SetSpeed(speed);
        return $"Game speed set to {speed}.";
    }

    private string ConsoleMoney(string value)
    {
        if (_campaign?.Player is null || !TryFloat(value, out var amount)) return "Usage: money 100";
        _campaign.Player.Treasury += amount;
        Refresh();
        return $"Treasury changed by ${amount:0.##}B.";
    }

    private string ConsoleManpower(string value)
    {
        if (_campaign?.Player is null || !TryFloat(value, out var amount)) return "Usage: manpower 100000";
        _campaign.Player.ManpowerReserve = MathF.Max(0, _campaign.Player.ManpowerReserve + amount);
        Refresh();
        return $"Manpower reserve changed by {amount:N0}.";
    }

    /// <summary>
    /// Raise an event now, rather than playing until one turns up.
    ///
    /// With an id it raises that event whether or not its trigger is satisfied, which is the
    /// whole point: an event you can only see by arranging the world it fires in is an event
    /// nobody tests. Without one it draws from what this country actually qualifies for.
    /// </summary>
    private string ConsoleEvent(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var id = (argument ?? "").Trim();
        if (id.Length == 0)
        {
            var drawn = Simulation.EventSystem.Raise(_campaign, _campaign.Player.Id);
            if (drawn is null) return "Nothing in the event catalogue matches this country right now.";
            RefreshEventInterface();
            return $"Raised '{EscapeBb(drawn.Title)}'.";
        }

        var definition = Simulation.EventCatalog.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (definition is null) return $"No event called '{EscapeBb(id)}'. Try `events`.";
        _campaign.PendingEvents.Add(new Simulation.PendingCampaignEvent
        {
            Id = _campaign.ElapsedDays * 1000 + _campaign.Player.Id,
            NationId = _campaign.Player.Id,
            Title = definition.Title,
            Description = definition.Description,
            CreatedDay = _campaign.ElapsedDays,
            ExpiresDay = _campaign.ElapsedDays + 182,
            Options = definition.Options
        });
        RefreshEventInterface();
        return $"Raised '{EscapeBb(definition.Title)}' past its own trigger.";
    }

    private string ConsoleStability(string value)
    {
        if (_campaign?.Player is null || !TryFloat(value, out var amount)) return "Usage: stability 75";
        _campaign.Player.Stability = Math.Clamp(amount > 1f ? amount / 100f : amount, 0f, 1f);
        Refresh();
        return $"Stability set to {_campaign.Player.Stability:P0}.";
    }

    private string ConsoleFreeCamera(string argument)
    {
        var enabled = argument.ToLowerInvariant() switch
        {
            "on" or "1" => true,
            "off" or "0" => false,
            _ => !_freeCameraMode
        };
        if (_globe is not null && _globe.FreeCamera != enabled) ToggleFreeCamera();
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

    private string ConsoleMapMode(string value)
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
        _globe.SetMapMode(mode);
        return $"Map mode: {mode}.";
    }

    private string ConsoleFocus(string value)
    {
        var values = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 2 || !TryFloat(values[0], out var latitude) || !TryFloat(values[1], out var longitude))
            return "Usage: focus LATITUDE LONGITUDE";
        _globe.FocusOn(Math.Clamp(latitude, -89f, 89f), longitude, 7f);
        return $"Camera focused on {latitude:0.00}, {longitude:0.00}.";
    }

    private string ConsoleTeleport(string value)
    {
        if (_campaign?.ProvinceMap is null || !int.TryParse(value, out var province) ||
            province <= 0 || province > _campaign.ProvinceMap.ProvinceCount)
            return "Usage: teleport PROVINCE_ID";
        var targets = _selectedArmies.Where(army => army.NationId == _campaign.Player.Id).ToArray();
        if (targets.Length == 0) return "Select one or more divisions first.";
        var center = _campaign.ProvinceMap.CenterOf(province);
        foreach (var division in targets)
        {
            division.ProvinceId = province;
            division.StateMapIndex = _campaign.ProvinceMap.ProvinceToState[province];
            division.Latitude = center.Latitude;
            division.Longitude = center.Longitude;
            division.Route.Clear();
            division.PlannedRoute.Clear();
            division.MoveProgress = 0f;
            division.OrderRouteSteps = 0;
            division.OrderStepsCompleted = 0;
        }
        _campaign.MarkStrategicChange();
        return $"Teleported {targets.Length} division(s) to province #{province:N0}.";
    }

    private string ConsoleArmies(string argument)
    {
        var nation = ConsoleNation(argument);
        if (nation is null || _campaign is null) return "No such country.";
        var divisions = _campaign.Armies.Where(army => army.NationId == nation.Id).ToArray();
        if (divisions.Length == 0) return $"{nation.Tag} has nothing under arms.";

        // Where the weight of an army actually is, which is the question "how many divisions"
        // never answers: a country with forty divisions in one region and four spread over
        // the rest of itself is two different countries to fight.
        var byRegion = divisions.GroupBy(army => army.StateMapIndex)
            .OrderByDescending(group => group.Sum(army => army.Personnel))
            .Take(6)
            .Select(group =>
            {
                var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == group.Key);
                return $"      {state?.Name ?? "unknown"} · {group.Count()} division(s), " +
                       $"{group.Sum(army => army.Personnel):N0} men";
            });

        return EscapeBb(
            $"{nation.Tag}: {divisions.Length:N0} division(s) · {divisions.Sum(army => army.Personnel):N0} men · " +
            $"{_campaign.ArmyGroups.Count(group => group.NationId == nation.Id)} headquarters\n" +
            $"  fighting {divisions.Count(army => army.InBattle)} · marching {divisions.Count(army => army.IsMoving)} · " +
            $"cut off {divisions.Count(army => army.CutOff)} · recovering {divisions.Count(army => army.Recovering > 0f)}\n" +
            $"  average organisation {divisions.Average(army => army.Organisation):P0} · " +
            $"strength {divisions.Average(army => army.Strength):P0} · " +
            $"supply {divisions.Average(army => army.SupplyLevel):P0}\n" +
            "  heaviest regions:\n" + string.Join("\n", byRegion));
    }

    private string ConsoleFronts()
    {
        if (_campaign is null) return "No campaign.";
        var running = _campaign.Wars.Where(war => !war.Ended).ToArray();
        if (running.Length == 0) return "There are no fronts; the world is at peace.";
        return string.Join("\n", running.Select(war =>
        {
            // A drawn line belongs to a country rather than to a war, so the honest count
            // is the lines either belligerent is holding.
            var drawn = _campaign.FrontLines.Count(line =>
                line.NationId == war.AttackerId || line.NationId == war.DefenderId);
            return EscapeBb($"{war.Name}: {war.AttackerFront.Count:N0}/{war.DefenderFront.Count:N0} front " +
                            $"province(s) · {drawn} drawn line(s) · score {war.WarScore:0.0} · " +
                            $"last rebuilt day {war.LastFrontDay:N0}");
        }));
    }

    private string ConsoleProvince(string argument)
    {
        if (_campaign?.ProvinceMap is null) return "No campaign.";
        var province = _selectedProvinceId;
        if ((argument ?? "").Trim().Length > 0 && !int.TryParse(argument.Trim(), out province))
            return "Usage: province [ID]";
        var map = _campaign.ProvinceMap;
        if (province <= 0 || province > map.ProvinceCount) return "Select a province, or give its number.";

        var stateIndex = map.ProvinceToState[province];
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == stateIndex);
        var owner = _campaign.Nations.FirstOrDefault(nation => nation.MapIndex == map.ProvinceToNation[province]);
        var holderIndex = _campaign.ProvinceController?[province] ?? 0;
        var holder = _campaign.Nations.FirstOrDefault(nation => nation.MapIndex == holderIndex);
        var centre = map.CenterOf(province);
        var standing = _campaign.Armies.Where(army => army.ProvinceId == province).ToArray();
        var battle = _campaign.Battles.FirstOrDefault(item => !item.Ended && item.ProvinceId == province);

        return EscapeBb(
            $"Province #{province:N0} · region {state?.Name ?? stateIndex.ToString()} · " +
            $"{centre.Latitude:0.00}, {centre.Longitude:0.00}\n" +
            $"  owner {owner?.Tag ?? "none"} · held by {holder?.Tag ?? "none"}" +
            $" · occupation {_campaign.ProvinceOccupation?[province] ?? 0f:P0}\n" +
            $"  ground {map.TerrainAt(province)?.Name ?? "unknown"}\n" +
            $"  standing here: {(standing.Length == 0 ? "nobody" : string.Join(", ", standing.Take(8).Select(army => army.Name)))}" +
            (battle is null ? "" : $"\n  a battle is being fought here, round {battle.Rounds.Count}"));
    }

    private static string ConsoleSimulation(string filter)
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
                : $"No pass matches '{EscapeBb(wanted)}'.";
        var total = samples.Sum(sample => sample.AverageMilliseconds);
        return EscapeBb(string.Join("\n", samples.Select(sample =>
            $"  {sample.Name,-34} {sample.LastMilliseconds,8:0.000} ms last · " +
            $"{sample.AverageMilliseconds,8:0.000} ms avg · {sample.AverageMilliseconds / Math.Max(0.0001, total),5:P0}")));
    }

    private static bool TryFloat(string value, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
        float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);

    private static string EscapeBb(string value) => (value ?? "").Replace("[", "[​");
    private static double _ProcessTimeMs() => Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
}
