using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

internal delegate void RegisterConsoleCommand(
    string name,
    string category,
    string usage,
    string help,
    Func<string, string> run,
    params string[] aliases);

/// <summary>
/// Standalone developer console UI, history, tab completion, and command dispatch table.
/// </summary>
internal sealed class DeveloperConsole
{
    internal sealed class ConsoleCommand
    {
        public string Name = "";
        public string[] Aliases = Array.Empty<string>();
        public string Category = "";
        public string Usage = "";
        public string Help = "";
        public Func<string, string> Run = _ => "";
    }

    /// <summary>The order categories are printed in: what you reach for most, first.</summary>
    private static readonly string[] ConsoleCategories =
    {
        "CONSOLE", "TIME", "PLAY", "COUNTRY", "PEOPLE", "WAR", "ARMIES",
        "MAP", "ECONOMY", "MONEY", "DIPLOMACY", "CONTENT", "DEBUG", "FUN"
    };

    private readonly ConsoleContext _context;
    private readonly List<string> _history = new();
    private int _historyAt;
    private List<ConsoleCommand> _commands;
    private Dictionary<string, ConsoleCommand> _byName;

    private Control _layer = null!;
    private RichTextLabel _output = null!;
    private LineEdit _input = null!;

    public Control Layer => _layer;

    public DeveloperConsole(ConsoleContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void Build()
    {
        _layer = _context.CreateFullLayer("DeveloperConsole");
        _layer.Visible = false;
        _layer.MouseFilter = Control.MouseFilterEnum.Stop;
        _context.AddChildNode(_layer);

        var shade = new ColorRect { Color = new Color(0.01f, 0.016f, 0.025f, 0.48f) };
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(shade);

        var panel = _context.CreateTexturedPanel(_layer);
        panel.AnchorLeft = 0.04f;
        panel.AnchorRight = 0.72f;
        panel.AnchorTop = 0.03f;
        panel.AnchorBottom = 0.48f;
        var rows = _context.CreatePanelRows(panel, 18, 14, 7);
        var header = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            Alignment = BoxContainer.AlignmentMode.Begin
        };
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);

        var title = _context.CreateTitle("COMMAND CONSOLE", 18, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(title);

        var closeHint = _context.CreateTitle("~ / Esc to close", 12, Ui.Colour("text.hint"));
        closeHint.CustomMinimumSize = new Vector2(112, 28);
        closeHint.VerticalAlignment = VerticalAlignment.Center;
        closeHint.HorizontalAlignment = HorizontalAlignment.Right;
        header.AddChild(closeHint);

        var close = _context.CreateActionButton("×", Close, 28, 28);
        close.TooltipText = Ui.Tip(_context.Tr("Close console (~ or Esc)"));
        header.AddChild(close);

        _output = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 260),
            Text = ConsoleContext.Bb("text.accent") + "Deal or Force console[/color]\n" +
                   ConsoleContext.Bb("state.info") + "help[/color] lists everything · " +
                   ConsoleContext.Bb("state.info") + "help NAME[/color] explains one · Tab completes · " +
                   "Up and Down are the history\nCommands are local and deterministic."
        };
        _output.AddThemeFontSizeOverride("normal_font_size", (int)Ui.Metric("ui.font.console", 14));
        rows.AddChild(_output);

        // Through Placeholder rather than straight into the field, so the hint follows a change
        // of language like every other caption instead of staying in the one it was built in.
        _input = _context.ApplyPlaceholder(new LineEdit
        {
            CustomMinimumSize = new Vector2(0, 38),
            ClearButtonEnabled = true
        }, "Enter command…");
        _input.TextSubmitted += Execute;
        _input.GuiInput += OnInputKey;
        rows.AddChild(_input);
        EnsureCommands();
    }

    public void Toggle()
    {
        if (_layer is null) return;
        if (_layer.Visible) Close();
        else
        {
            _layer.Visible = true;
            _layer.MoveToFront();
            _input.GrabFocus();
        }
        SyncFreeCameraPointer();
    }

    public void Close()
    {
        if (_layer is null) return;
        _layer.Visible = false;
        _input.ReleaseFocus();
        SyncFreeCameraPointer();
    }

    public void Execute(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return;
        _output.AppendText($"\n{ConsoleContext.Bb("text.caption")}> {ConsoleContext.EscapeBb(raw)}[/color]");
        _input.Clear();
        // Remembered before it is run, so a command that throws is still one Up away.
        if (_history.Count == 0 || _history[^1] != raw) _history.Add(raw);
        if (_history.Count > 200) _history.RemoveAt(0);
        _historyAt = _history.Count;

        EnsureCommands();
        var parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : "";
        if (!_byName.TryGetValue(command, out var entry))
        {
            _output.AppendText(
                $"\n{ConsoleContext.Bb("state.warning")}No command '{ConsoleContext.EscapeBb(command)}'.[/color]{Suggest(command)}");
            _input.GrabFocus();
            return;
        }

        try
        {
            var response = entry.Run(argument);
            if (!string.IsNullOrEmpty(response)) _output.AppendText($"\n{response}");
        }
        catch (Exception exception)
        {
            DiagnosticLogs.Write("errors", $"CONSOLE_EXCEPTION command={command} error={exception}");
            _output.AppendText(
                $"\n{ConsoleContext.Bb("state.negative")}{ConsoleContext.EscapeBb(entry.Name)} failed: {ConsoleContext.EscapeBb(exception.Message)}[/color]" +
                $"\n{ConsoleContext.Bb("text.muted")}{ConsoleContext.EscapeBb(entry.Usage)}[/color]");
        }
        _input.GrabFocus();
    }

    internal string Clear()
    {
        _output.Text = ConsoleContext.Bb("text.accent") + "Console cleared.[/color]";
        return "";
    }

    internal string Help(string argument)
    {
        EnsureCommands();
        var wanted = argument.Trim();
        if (wanted.Length > 0)
        {
            if (!_byName.TryGetValue(wanted, out var found))
                return $"No command called '{ConsoleContext.EscapeBb(wanted)}'.{Suggest(wanted)}";
            var alsoKnownAs = found.Aliases.Length == 0
                ? ""
                : $"\n  also: {string.Join(", ", found.Aliases)}";
            return $"{ConsoleContext.Bb("text.accent")}{found.Name}[/color]  ·  {found.Category}\n" +
                   $"  {ConsoleContext.EscapeBb(found.Usage)}\n  {ConsoleContext.EscapeBb(found.Help)}{alsoKnownAs}";
        }

        var lines = new List<string>
        {
            $"{_commands.Count} commands. " +
            $"{ConsoleContext.Bb("state.info")}help NAME[/color] explains one; Tab completes; Up and Down are the history."
        };
        foreach (var category in ConsoleCategories)
        {
            var names = _commands.Where(command => command.Category == category)
                .Select(command => command.Name).ToArray();
            if (names.Length == 0) continue;
            lines.Add($"{ConsoleContext.Bb("text.accent")}{category}[/color]  {string.Join(" · ", names)}");
        }
        return string.Join("\n", lines);
    }

    internal string CommandList(string argument)
    {
        EnsureCommands();
        var filter = argument.Trim();
        var names = _commands
            .Where(command => filter.Length == 0 ||
                              command.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                              command.Help.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(command => command.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0
            ? $"Nothing matches '{ConsoleContext.EscapeBb(filter)}'."
            : string.Join(" ", names);
    }

    internal string SelfTest()
    {
        EnsureCommands();
        var broken = new List<string>();
        var ran = 0;
        foreach (var command in _commands)
        {
            // Not itself, or it recurses; not `clear`, or the report is wiped as it is written;
            // not `touchgrass`, or the test closes the game it is testing; not the curses, or
            // it spoils the world it is testing for good.
            if (command.Name is "selftest" or "clear" or "touchgrass" or "snowgrave" or "weirdroute") continue;
            try
            {
                command.Run("");
                ran++;
            }
            catch (Exception exception)
            {
                broken.Add($"  {ConsoleContext.Bb("state.negative")}{command.Name}[/color] threw " +
                           $"{exception.GetType().Name}: {ConsoleContext.EscapeBb(exception.Message)}");
            }
        }
        return broken.Count == 0
            ? $"{ConsoleContext.Bb("state.positive")}All {ran} command(s) ran.[/color] " +
              "Whether each said something true is still your problem."
            : $"{ran} ran, {broken.Count} threw:\n" + string.Join("\n", broken);
    }

    private void EnsureCommands()
    {
        if (_commands is not null) return;
        var table = new List<ConsoleCommand>();
        void Add(string name, string category, string usage, string help,
            Func<string, string> run, params string[] aliases) =>
            table.Add(new ConsoleCommand
            {
                Name = name,
                Category = category,
                Usage = usage,
                Help = help,
                Run = run,
                Aliases = aliases
            });

        ConsoleDebugCommands.RegisterConsole(this, _context, Add);
        ConsoleWorldCommands.Register(_context, Add);
        ConsoleDiplomacyCommands.Register(this, _context, Add);
        ConsoleDebugCommands.RegisterTools(this, _context, Add);

        _commands = table;
        _byName = new Dictionary<string, ConsoleCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in table)
        {
            _byName[command.Name] = command;
            foreach (var alias in command.Aliases) _byName[alias] = command;
        }
    }

    /// <summary>
    /// Tab, Up and Down. Godot hands the line edit every key first, so these three have to be
    /// taken here and swallowed, or Tab moves the focus out of the console and Up walks the
    /// caret instead of the history.
    /// </summary>
    private void OnInputKey(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key) return;
        switch (key.Keycode)
        {
            case Key.Tab:
                Complete();
                _input.AcceptEvent();
                break;
            case Key.Up:
                Recall(-1);
                _input.AcceptEvent();
                break;
            case Key.Down:
                Recall(1);
                _input.AcceptEvent();
                break;
        }
    }

    private void Recall(int step)
    {
        if (_history.Count == 0) return;
        _historyAt = Math.Clamp(_historyAt + step, 0, _history.Count);
        _input.Text = _historyAt >= _history.Count
            ? ""
            : _history[_historyAt];
        _input.CaretColumn = _input.Text.Length;
    }

    private void Complete()
    {
        var typed = _input.Text ?? "";
        if (typed.Contains(' ')) return;
        var matches = Completions(typed);
        if (matches.Count == 0) return;
        if (matches.Count == 1)
        {
            _input.Text = matches[0] + " ";
            _input.CaretColumn = _input.Text.Length;
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
        _input.Text = shared;
        _input.CaretColumn = shared.Length;
        _output.AppendText($"\n{ConsoleContext.Bb("text.muted")}{string.Join("  ", matches)}[/color]");
    }

    private IReadOnlyList<string> Completions(string prefix)
    {
        EnsureCommands();
        return _commands
            .Select(command => command.Name)
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private string Suggest(string typed)
    {
        EnsureCommands();
        var close = _byName.Keys
            .Select(name => (Name: name, Distance: NameDistance(typed, name)))
            .Where(candidate => candidate.Distance <= 2)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.Name)
            .Take(4)
            .ToArray();
        return close.Length == 0 ? " Type help." : $" Did you mean {string.Join(", ", close)}?";
    }

    private static int NameDistance(string typed, string name)
    {
        if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 1;
        var previous = new int[name.Length + 1];
        var current = new int[name.Length + 1];
        for (var index = 0; index <= name.Length; index++) previous[index] = index;
        for (var left = 1; left <= typed.Length; left++)
        {
            current[0] = left;
            for (var right = 1; right <= name.Length; right++)
                current[right] = Math.Min(Math.Min(current[right - 1] + 1, previous[right] + 1),
                    previous[right - 1] + (char.ToLowerInvariant(typed[left - 1]) ==
                                           char.ToLowerInvariant(name[right - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[name.Length];
    }

    private void SyncFreeCameraPointer()
    {
        var globe = _context.Globe;
        if (globe is null || !globe.FreeCamera) return;
        Input.MouseMode = _layer is { Visible: true }
            ? Input.MouseModeEnum.Visible
            : Input.MouseModeEnum.Captured;
    }
}
