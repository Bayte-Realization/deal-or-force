using System;
using System.Globalization;
using System.Linq;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

public partial class GlobalityGame
{
    private Control _developerConsoleLayer = null!;
    private RichTextLabel _developerConsoleOutput = null!;
    private LineEdit _developerConsoleInput = null!;
    private bool _observerMode;
    private bool _freeCameraMode;

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
        var title = Title("COMMAND CONSOLE", 18, new Color("#e7c966"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(title);
        var closeHint = Title("~ / Esc to close", 12, new Color("#7e8fa3"));
        closeHint.CustomMinimumSize = new Vector2(112, 28);
        closeHint.VerticalAlignment = VerticalAlignment.Center;
        closeHint.HorizontalAlignment = HorizontalAlignment.Right;
        header.AddChild(closeHint);
        var close = ActionButton("×", CloseDeveloperConsole, 28, 28);
        close.TooltipText = "Close console (~ or Esc)";
        header.AddChild(close);

        _developerConsoleOutput = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 260),
            Text = "[color=#e7c966]By Deal or Force console[/color]\nType [color=#a8c9e8]help[/color] for commands. Commands are local and deterministic."
        };
        _developerConsoleOutput.AddThemeFontSizeOverride("normal_font_size", 14);
        rows.AddChild(_developerConsoleOutput);

        _developerConsoleInput = new LineEdit
        {
            PlaceholderText = "Enter command…",
            CustomMinimumSize = new Vector2(0, 38),
            ClearButtonEnabled = true
        };
        _developerConsoleInput.TextSubmitted += ExecuteDeveloperCommand;
        rows.AddChild(_developerConsoleInput);
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
    }

    private void CloseDeveloperConsole()
    {
        if (_developerConsoleLayer is null) return;
        _developerConsoleLayer.Visible = false;
        _developerConsoleInput.ReleaseFocus();
    }

    private void ExecuteDeveloperCommand(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return;
        _developerConsoleOutput.AppendText($"\n[color=#8798aa]> {EscapeBb(raw)}[/color]");
        _developerConsoleInput.Clear();
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : "";
        try
        {
            var response = command switch
            {
                "help" or "?" => ConsoleHelp(),
                "clear" or "cls" => ClearConsole(),
                "tag" => ConsoleTag(argument),
                "observe" => ConsoleObserve(),
                "pause" => ConsolePause(),
                "speed" => ConsoleSpeed(argument),
                "money" or "cash" => ConsoleMoney(argument),
                "manpower" => ConsoleManpower(argument),
                "stability" => ConsoleStability(argument),
                "freecam" => ConsoleFreeCamera(argument),
                "mapmode" => ConsoleMapMode(argument),
                "focus" => ConsoleFocus(argument),
                "teleport" or "tp" => ConsoleTeleport(argument),
                "armies" or "divisions" => ConsoleArmies(),
                "fronts" => ConsoleFronts(),
                "province" => ConsoleProvince(),
                "fps" => $"FPS: {Engine.GetFramesPerSecond()} · process {_ProcessTimeMs():0.00} ms",
                "sim" => ConsoleSimulation(),
                "economy" or "gdp" => ConsoleEconomy(),
                "loc" or "localization" => ConsoleLocalization(argument),
                "effects" => ConsoleEffectVocabulary(),
                "date" => _campaign?.Date.ToString() ?? "No campaign",
                _ => $"Unknown command '{command}'. Type help."
            };
            if (!string.IsNullOrEmpty(response)) _developerConsoleOutput.AppendText($"\n{response}");
        }
        catch (Exception exception)
        {
            _developerConsoleOutput.AppendText($"\n[color=#e06b6b]Command failed: {EscapeBb(exception.Message)}[/color]");
        }
        _developerConsoleInput.GrabFocus();
    }

    private static string ConsoleHelp() =>
        "[color=#e7c966]CONTROL[/color]  tag TAG · observe · pause · speed 1-5 · freecam [on/off]\n" +
        "[color=#e7c966]MAP[/color]  mapmode pol/dip/war/pop/gdp · focus LAT LON · teleport PROVINCE\n" +
        "[color=#e7c966]STATE[/color]  money AMOUNT · manpower AMOUNT · stability 0-100\n" +
        "[color=#e7c966]DEBUG[/color]  fps · sim · armies · fronts · province · date · clear\n" +
        "[color=#e7c966]CONTENT[/color]  economy · effects · loc [language]";

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
    private static string ConsoleEffectVocabulary() => EscapeBb(string.Join(System.Environment.NewLine,
        Simulation.ScriptedEffects.Vocabulary.Select(item => $"  {item.Key} - {item.Help}")
            .Prepend("Scripted effect keys (suffix _percent, _mult or _set to change the operation):")));

    /// <summary>Translation coverage, the way a translator wants to see it.</summary>
    private static string ConsoleLocalization(string argument)
    {
        var language = string.IsNullOrWhiteSpace(argument) ? Simulation.Loc.Language : argument.Trim();
        if (!Simulation.Loc.Languages.Any(item => string.Equals(item, language, StringComparison.OrdinalIgnoreCase)))
            return $"Unknown language '{language}'. Loaded: {string.Join(", ", Simulation.Loc.Languages)}";

        Simulation.Loc.Language = language;
        var missing = Simulation.Loc.MissingKeys(language);
        var header = $"Language: {Simulation.Loc.DisplayName(language)} ({language}) · " +
                     $"{Simulation.Loc.Keys(language).Count()} key(s) translated";
        return missing.Count == 0
            ? EscapeBb(header)
            : EscapeBb(header + $"\nUntranslated ({missing.Count}): " + string.Join(", ", missing.Take(12)));
    }

    private string ClearConsole()
    {
        _developerConsoleOutput.Text = "[color=#e7c966]Console cleared.[/color]";
        return "";
    }

    private string ConsoleTag(string tag)
    {
        var nation = _campaign?.NationByTag(tag);
        if (nation is null) return "Usage: tag KOR — unknown country tag.";
        _observerMode = false;
        _campaign.ConfigureSinglePlayer(nation.Id);
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
        FrontPlanner.AssignAi(_campaign);
        _identityTag.Text = "OBS · observer mode";
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
        _freeCameraMode = enabled;
        if (_hudLayer is not null) _hudLayer.Visible = !enabled;
        if (_menuLayer is not null) _menuLayer.Visible = false;
        if (_pickerLayer is not null) _pickerLayer.Visible = false;
        return enabled ? "Free camera enabled; HUD hidden. Reopen console and use freecam off to restore it."
            : "Free camera disabled; campaign HUD restored.";
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
            division.MoveProgress = 0f;
        }
        _campaign.MarkStrategicChange();
        return $"Teleported {targets.Length} division(s) to province #{province:N0}.";
    }

    private string ConsoleArmies()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var divisions = _campaign.Armies.Where(army => army.NationId == _campaign.Player.Id).ToArray();
        return $"{_campaign.Player.Tag}: {divisions.Length} independent divisions · " +
               $"{divisions.Sum(army => army.Personnel):N0} personnel · " +
               $"{_campaign.ArmyGroups.Count(group => group.NationId == _campaign.Player.Id)} army headquarters.";
    }

    private string ConsoleFronts() => _campaign is null ? "No campaign." :
        string.Join("\n", _campaign.Wars.Where(war => !war.Ended).Select(war =>
            $"{EscapeBb(war.Name)}: {war.AttackerFront.Count}/{war.DefenderFront.Count} front provinces · score {war.WarScore:0.0}"));

    private string ConsoleProvince() => _selectedProvinceId > 0 && _campaign?.ProvinceMap is not null
        ? $"Province #{_selectedProvinceId:N0} · state {_campaign.ProvinceMap.ProvinceToState[_selectedProvinceId]} · " +
          $"controller {_campaign.ProvinceController?[_selectedProvinceId]}"
        : "No tactical province selected.";

    private static string ConsoleSimulation()
    {
        var samples = SimProfiler.All.OrderByDescending(sample => sample.AverageMilliseconds).Take(8).ToArray();
        return samples.Length == 0 ? "No profiler samples yet; resume time first." : string.Join("\n",
            samples.Select(sample => $"{sample.Name}: {sample.LastMilliseconds:0.000} ms last · {sample.AverageMilliseconds:0.000} ms avg"));
    }

    private static bool TryFloat(string value, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
        float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);

    private static string EscapeBb(string value) => (value ?? "").Replace("[", "[​");
    private static double _ProcessTimeMs() => Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
}
