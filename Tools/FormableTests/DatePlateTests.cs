using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Step 3 of docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md: the date plate at the top right.
///
/// The clock's rules live in Scripts/ui/DatePlateClock.cs, which this project does not compile (it
/// is game code beside Godot controls). As in HudResourceTests, they are copied below and a source
/// check holds the copy to the game's, word for word.
///
/// What it pins: "slower" at 1 and "faster" at 5 stay inside the engine's speeds, and those speeds
/// are five; the marks follow every pause, wherever it comes from; a click lands on the right mark;
/// every rule is one the game calls; every button the top bar
/// gave up is reachable in its new place - a table from action to place, each row checked against
/// the source - and none is still built on the top bar; the keys are where they were; hud.date_plate
/// resolves, sits top right clear of the other always-visible panels, is named and drawn in the
/// layout editor, and the top bar stops before it; and the twelve figures, the country and DEFCON
/// fit one row on the 1920 canvas (which a 1280x720 window also has) and the second row on a canvas
/// 1280 wide. None of it touches the world.
/// </summary>
static class DatePlateTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("date plate: " + label);
        Console.WriteLine("PASS date plate: " + label);
    }

    private static string Repo => Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Repo, relative));

    public static void Run()
    {
        InterfaceSkin.ResetCatalog();
        try
        {
            Speeds();
            Pause();
            Marks();
            RulesInStep();
            EveryFormerButtonIsReachable();
            KeysAreWhereTheyWere();
            Layout();
            FiguresFitOneRow();
        }
        finally { InterfaceSkin.ResetCatalog(); }
    }

    // ---- the rule, as Scripts/ui/DatePlateClock.cs writes it ---------------------------------
    // Copied verbatim; RulesInStep compares each member with the game's.

    internal const int Slowest = 1;
    internal const int Fastest = 5;

    internal static int Clamp(int speed) => Math.Clamp(speed, Slowest, Fastest);

    internal static int Slower(int speed) => Clamp(speed - 1);

    internal static int Faster(int speed) => Clamp(speed + 1);

    internal static bool Lit(int pip, int speed) => pip >= Slowest && pip <= Clamp(speed);

    internal static int PipAt(float x, float size, float gap)
    {
        var step = Math.Max(1f, size + gap);
        if (!float.IsFinite(x) || x < 0f) return Slowest;
        return Clamp((int)MathF.Floor(x / step) + 1);
    }

    // ---- the checks -------------------------------------------------------------------------

    private static void Speeds()
    {
        Check(Slower(1) == 1, "\"slower\" at speed 1 stays at 1");
        Check(Faster(5) == 5, "\"faster\" at speed 5 stays at 5");
        Check(Slower(3) == 2 && Faster(3) == 4, "in between, each is one step");
        var speed = 1;
        for (var i = 0; i < 10; i++) speed = Faster(speed);
        Check(speed == Fastest, "ten presses of \"faster\" end at the fastest speed, not past it");
        for (var i = 0; i < 10; i++) speed = Slower(speed);
        Check(speed == Slowest, "ten presses of \"slower\" end at the slowest, not below it");
        Check(Clamp(0) == 1 && Clamp(-4) == 1 && Clamp(6) == 5 && Clamp(int.MaxValue) == 5,
            "a speed from anywhere (a save, a console command) is brought inside 1..5");

        // The engine has as many speeds as the plate has marks, and each is faster than the last.
        var campaign = Campaign.CreateBootstrap(73);
        var before = CampaignFingerprint.Of(campaign);
        Check(campaign.Defines.MaxSpeed == Fastest, $"the engine has {campaign.Defines.MaxSpeed} speeds, the plate {Fastest} marks");
        var seconds = Enumerable.Range(Slowest, Fastest).Select(campaign.Defines.SecondsForSpeed).ToArray();
        Check(seconds.All(s => float.IsFinite(s) && s > 0f), "every speed is a real pace");
        Check(seconds.Zip(seconds.Skip(1)).All(pair => pair.Second < pair.First),
            "each mark to the right runs a day in fewer seconds: " + string.Join(", ", seconds.Select(s => s.ToString("0.###"))));
        Check(before == CampaignFingerprint.Of(campaign), "reading the clock never changes the world fingerprint");
    }

    private static void Pause()
    {
        // Pause is the game's own one line, checked where it is: a copied rule nobody called
        // passed here while proving nothing about the button.
        var game = Source("Scripts/DealOrForceGame.cs");
        var toggle = HudResourceTests.MemberText(game, "void TogglePause()");
        Check(toggle.Contains("_campaign.IsPaused = !_campaign.IsPaused;"), "the game's pause turns the campaign's own flag over");
        Check(game.Contains("_speed = Mathf.Clamp(speed, 1, 5);"), "the game's SetSpeed keeps the speed inside 1..5 whatever it is handed");

        // The marks are dimmer while paused, and the clock is paused from many places that never
        // call UpdateSpeedButtons (an event with pause_on_event, a peace conference, the save list,
        // a seat handover, a simulation fault). So the marks read the clock every frame themselves.
        var plate = Source("Scripts/ui/DatePlateUI.cs");
        var watch = HudResourceTests.MemberText(plate, "void _Process(double delta)");
        Check(watch.Contains("Clock()") && watch.Contains("QueueRedraw();") &&
              plate.Contains("Clock = () => (DatePlateClock.Clamp(_speed), _campaign?.IsPaused ?? true)"),
            "the speed marks follow the campaign's pause and speed every frame, whoever changed them");
        // A network client's clock is the host's heartbeat; "−" and "+" and their tooltips follow it too.
        var heartbeat = HudResourceTests.MemberText(Source("Scripts/ui/NetworkMultiplayerUI.cs"),
            "void ClientReceiveNetworkClock(int elapsedDays, bool paused, int speed, int hostTickHours)");
        Check(heartbeat.Contains("UpdateSpeedButtons();"), "the host's heartbeat updates the client's date plate");
    }

    private static void Marks()
    {
        Check(Lit(1, 3) && Lit(3, 3) && !Lit(4, 3) && !Lit(5, 3), "at speed 3 the first three marks are lit, not the last two");
        Check(Lit(1, 1) && !Lit(2, 1) && Enumerable.Range(1, 5).All(pip => Lit(pip, 5)), "speed 1 lights one mark, speed 5 all five");
        Check(!Lit(0, 5) && !Lit(6, 5) && Lit(1, 0), "there is no mark 0 or 6, and a speed below 1 still lights the first");
        // Marks 8 px wide with 4 px between them (hud.speed_pips = 8, the gap half of it).
        Check(PipAt(0, 8, 4) == 1 && PipAt(7.9f, 8, 4) == 1 && PipAt(11.9f, 8, 4) == 1, "a click on the first mark, or in the gap after it, is speed 1");
        Check(PipAt(12, 8, 4) == 2 && PipAt(48, 8, 4) == 5 && PipAt(55, 8, 4) == 5, "the second mark starts at 12 px and the fifth at 48");
        Check(PipAt(400, 8, 4) == 5 && PipAt(-3, 8, 4) == 1 && PipAt(float.NaN, 8, 4) == 1, "a click outside the row lands on the nearest end");
        Check(InterfaceSkin.Metric("hud.speed_pips", -1f) is >= 5f and <= 16f, "the marks' size is data (hud.speed_pips) and inside the range the plate clamps to");
    }

    private static void RulesInStep()
    {
        var rules = Source("Scripts/ui/DatePlateClock.cs");
        var self = Source("Tools/FormableTests/DatePlateTests.cs");
        foreach (var member in new[] { "const int Slowest", "const int Fastest", "int Clamp(int speed)", "int Slower(int speed)",
                     "int Faster(int speed)", "bool Lit(int pip, int speed)", "int PipAt(float x, float size, float gap)" })
        {
            var game = HudResourceTests.MemberText(rules, member);
            Check(game.Length > member.Length && game == HudResourceTests.MemberText(self, member),
                $"the test's copy of {member} is the game's, word for word");
        }
        var plate = Source("Scripts/ui/DatePlateUI.cs");
        Check(plate.Contains("DatePlateClock.Slower(_speed)") && plate.Contains("DatePlateClock.Faster(_speed)") &&
              plate.Contains("resume: false"), "slower and faster step through the rule and leave a paused clock paused, as minus and plus do");
        Check(plate.Contains("DatePlateClock.Lit(pip, Speed)") && plate.Contains("DatePlateClock.PipAt(click.Position.X, PipSize, PipGap)"),
            "the marks are drawn and clicked through the rule");
        Check(plate.Contains("Ui.Metric(\"hud.speed_pips\""), "and sized by hud.speed_pips");

        // A rule the game does not call is a test of the test's own copy. Every member of the
        // rule has to be called from the plate.
        var members = Regex.Matches(rules, @"internal static \w+ (\w+)\(").Select(match => match.Groups[1].Value).ToList();
        Check(members.Count == 5, $"the rule has the five members copied above ({string.Join(", ", members)})");
        foreach (var name in members)
            Check(plate.Contains("DatePlateClock." + name + "(", StringComparison.Ordinal), $"the game calls DatePlateClock.{name}");
    }

    /// <summary>One row per button the top bar used to carry: where it is now, and how that is known.</summary>
    private sealed record Move(string Action, string WasOnTopBar, string NowAt, (string File, string Needle)[] Proof);

    private static readonly Move[] Moves =
    {
        new("Handbook", "\"OpenHandbook\" in the clock row", "pause menu (beside Find) and F1", new[]
        {
            ("Scripts/ui/PauseMenuUI.cs", "\"PauseOpenHandbook\""),
            ("Scripts/ui/PauseMenuUI.cs", "OpenHandbook(); }"),
            ("Scripts/DealOrForceGame.cs", "key.Keycode == Key.F1"),
            ("Scripts/DealOrForceGame.cs", "ToggleHandbook();")
        }),
        new("Find", "\"OpenNavigator\" in the clock row", "pause menu, the rail's search (Enter and \"Find all\") and Ctrl+K", new[]
        {
            ("Scripts/ui/PauseMenuUI.cs", "\"PauseOpenNavigator\""),
            ("Scripts/ui/PauseMenuUI.cs", "OpenNavigator(); }"),
            ("Scripts/ui/ResponsiveCampaignNavigationUI.cs", "TextSubmitted += _ => OpenNavigatorFromRail();"),
            ("Scripts/ui/ResponsiveCampaignNavigationUI.cs", "\"RailOpenNavigator\""),
            ("Scripts/DealOrForceGame.cs", "Letter(key) is Key.K or Key.P"),
            ("Scripts/DealOrForceGame.cs", "ToggleNavigator();")
        }),
        new("Deals", "\"PendingDiplomaticOffers\" in the clock row", "a chip with its count on the date plate, while offers wait", new[]
        {
            ("Scripts/ui/DatePlateUI.cs", "\"DatePlateDeals\""),
            ("Scripts/ui/DatePlateUI.cs", "ActionButton(\"Deals\", OpenDealInbox"),
            ("Scripts/ui/DealInboxUI.cs", "RefreshDatePlateDeals(count);")
        }),
        new("Menu", "\"Menu\" in the clock row", "the square \"Menu\" on the date plate, and Esc", new[]
        {
            ("Scripts/ui/DatePlateUI.cs", "ActionButton(\"Menu\", ShowPauseMenu"),
            ("Scripts/ui/DatePlateUI.cs", "\"OpenPauseMenu\""),
            ("Scripts/DealOrForceGame.cs", "ShowPauseMenu();")
        }),
        new("Pause", "\"II\" in the clock row", "the date plate, and Space", new[]
        {
            ("Scripts/ui/DatePlateUI.cs", "_pause = ActionButton(\"II\", TogglePause"),
            ("Scripts/DealOrForceGame.cs", "TogglePause();")
        }),
        new("Speeds 1 to 5", "five numbered buttons", "a click on one of the five marks on the date plate, and keys 1 to 5", new[]
        {
            ("Scripts/ui/DatePlateUI.cs", "SetSpeed(speed);"),
            ("Scripts/DealOrForceGame.cs", "Key.Key1 or Key.Kp1 => 1"),
            ("Scripts/DealOrForceGame.cs", "Key.Key5 or Key.Kp5 => 5")
        }),
        new("Slower / faster", "keys only", "\"−\" and \"+\" on the date plate, and the minus and plus keys", new[]
        {
            ("Scripts/ui/DatePlateUI.cs", "ActionButton(\"−\", () => StepSpeed(-1)"),
            ("Scripts/ui/DatePlateUI.cs", "ActionButton(\"+\", () => StepSpeed(1)"),
            ("Scripts/DealOrForceGame.cs", "Key.Equal or Key.Plus or Key.KpAdd => 6"),
            ("Scripts/DealOrForceGame.cs", "Key.Minus or Key.KpSubtract => -1")
        }),
        new("Music", "the note in the clock row", "\"Music\" in the pause menu beside Settings, and Settings' own \"Sound and artwork\"", new[]
        {
            ("Scripts/ui/PauseMenuUI.cs", "_mediaButton = MusicShortcut("),
            ("Scripts/ui/HudDockLayoutUI.cs", "ActionButton(\"Music\", OpenSoundtrackLibrary"),
            ("Scripts/ui/HudDockLayoutUI.cs", "button.Name = \"OpenMediaLibrary\";"),
            ("Scripts/ui/SettingsUI.cs", "\"OpenMediaLibrarySettings\"")
        }),
        new("Brief / full figures", "the end of the resource plate (step 2)", "unchanged: the end of the resource plate", new[]
        {
            ("Scripts/ui/TopBarHudUI.cs", "_hudFiguresButton.Name = \"HudFiguresForm\";"),
            ("Scripts/ui/TopBarHudUI.cs", "_hudStatRow.AddChild(_hudFiguresButton);")
        })
    };

    private static void EveryFormerButtonIsReachable()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var move in Moves)
        {
            var missing = move.Proof.Where(proof =>
            {
                if (!sources.TryGetValue(proof.File, out var text)) sources[proof.File] = text = Source(proof.File);
                return !text.Contains(proof.Needle, StringComparison.Ordinal);
            }).Select(proof => proof.File + ": " + proof.Needle).ToList();
            Check(missing.Count == 0, $"{move.Action} (was: {move.WasOnTopBar}) is reached from {move.NowAt}" +
                (missing.Count == 0 ? "" : " - missing " + string.Join("; ", missing)));
        }
        Check(Moves.Length == 9 && Moves.Select(move => move.Action).Distinct().Count() == 9,
            "the table names all nine things the clock row and the plate's end carried");

        // Nothing is built twice: the top bar no longer makes any of them.
        var top = Source("Scripts/ui/TopBarHudUI.cs");
        foreach (var gone in new[] { "\"OpenHandbook\"", "\"OpenNavigator\"", "\"PendingDiplomaticOffers\"", "ShowPauseMenu",
                     "TogglePause", "SetSpeed(", "MusicShortcut(", "\"speeds\"", "_hudClockBox" })
            Check(!top.Contains(gone, StringComparison.Ordinal), "the top bar no longer builds " + gone);
        Check(top.Contains("BuildDatePlate();") && top.Contains("RefreshDatePlate(player);"),
            "the top bar builds and refreshes the date plate instead");

        // The single soundtrack entry: one button named OpenMediaLibrary (the release probe counts them).
        var named = Directory.GetFiles(Path.Combine(Repo, "Scripts", "ui"), "*.cs")
            .Sum(file => Regex.Matches(File.ReadAllText(file), "Name = \"OpenMediaLibrary\"").Count);
        Check(named == 1, $"one control in the game is the soundtrack entry ({named})");

        // The pause menu keeps its rows: the two new entries share rows with old ones, so it still
        // fits a 720-pixel screen.
        var menu = HudResourceTests.MemberText(Source("Scripts/ui/PauseMenuUI.cs"), "void BuildPauseMenu()");
        var rows = Regex.Matches(menu, @"rows\.AddChild\(").Count + Regex.Matches(menu, @"PauseMenuPair\(rows\)").Count;
        Check(rows <= 10, $"the pause menu has {rows} rows, no more than before Find and Music joined it");
    }

    private static void KeysAreWhereTheyWere()
    {
        var game = Source("Scripts/DealOrForceGame.cs");
        var keys = HudResourceTests.MemberText(game, "int SpeedKey(InputEventKey key)");
        for (var speed = 1; speed <= 5; speed++)
            Check(keys.Contains($"Key.Key{speed} or Key.Kp{speed} => {speed}"), $"key {speed} still sets speed {speed}");
        Check(game.Contains("if (step is >= 1 and <= 5) { SetSpeed(step);") && game.Contains("SetSpeed(_speed + (step > 0 ? 1 : -1), resume: false);"),
            "digits set the speed and let time run; plus and minus step it without starting the clock");
        Check(game.Contains("if (key.Keycode != Key.Space && key.PhysicalKeycode != Key.Space) return;"), "Space still pauses");
        Check(game.Contains("else if (_hudLayer.Visible)\n            ShowPauseMenu();") ||
              game.Contains("else if (_hudLayer.Visible)\r\n            ShowPauseMenu();"), "Escape on the bare map still opens the pause menu");
    }

    private static void Layout()
    {
        Check(InterfaceSkin.HasLayout("hud.date_plate"), "hud.date_plate resolves");
        var plate = InterfaceSkin.Layout("hud.date_plate");
        var top = InterfaceSkin.Layout("hud.top");
        Check(plate.AnchorLeft == 1 && plate.AnchorRight == 1 && plate.AnchorTop == 0 && plate.AnchorBottom == 0,
            "the plate is pinned to the top right corner");
        Check(plate.OffsetRight <= -4 && plate.OffsetTop >= 4, "with a margin from the screen's edges");
        Check(InterfaceSkin.DeclaredLayoutMaterial("hud.date_plate") == "plate" && InterfaceSkin.LayoutMaterial("hud.date_plate") is null,
            "it is a plate, drawn as one only while ui.materials is on; with materials off it keeps the HUD frame");
        Check(top.AnchorRight == 1 && top.OffsetRight <= plate.OffsetLeft - 4 && top.OffsetTop == plate.OffsetTop,
            "the resource plate stops short of the date plate, on the same top line");
        Check(plate.OffsetBottom <= 88 && InterfaceSkin.Layout("hud.outliner").OffsetTop >= plate.OffsetBottom + 4,
            "the plate ends above the docks and the report under it");

        foreach (var (w, h) in new[] { (1280f, 720f), (1920f, 1080f) })
        {
            var mine = Pixels(plate, w, h);
            Check(mine.Left >= 0 && mine.Right <= w && mine.Bottom <= h, $"the plate is on screen at {w:0}x{h:0}");
            foreach (var other in new[] { "hud.top", "hud.flag_mount", "hud.navigation", "hud.outliner", "hud.map_controls" })
            {
                if (!InterfaceSkin.HasLayout(other)) continue;
                var theirs = Pixels(InterfaceSkin.Layout(other), w, h);
                var wide = Math.Min(mine.Right, theirs.Right) - Math.Max(mine.Left, theirs.Left);
                var tall = Math.Min(mine.Bottom, theirs.Bottom) - Math.Max(mine.Top, theirs.Top);
                Check(wide <= 0 || tall <= 0, $"the plate leaves {other} its room at {w:0}x{h:0}");
            }
        }

        // What the plate holds fits the width the data gives it, so the deals chip coming and going
        // moves nothing: frame 2 + margins 16, "Menu" 58 and 8 beside it, then "−" 28, "II" 34,
        // "+" 28 and five marks with four gaps, a 4 px separation between each of those four
        // (three), and one more before the chip. The controls row has exactly those children: an
        // extra spacer would be one more separation nobody counted.
        var src = Source("Scripts/ui/DatePlateUI.cs");
        Check(src.Contains("ActionButton(\"Menu\", ShowPauseMenu, 58, 58)") && src.Contains("ActionButton(\"II\", TogglePause, 34, 28, true)") &&
              src.Contains("StepSpeed(-1), 28, 28)") && src.Contains("StepSpeed(1), 28, 28)") &&
              src.Contains("controls.AddThemeConstantOverride(\"separation\", 4);"),
            "the plate's buttons and gaps are the sizes this check adds up");
        var build = HudResourceTests.MemberText(src, "void BuildDatePlate()");
        var children = Regex.Matches(build, @"controls\.AddChild\(").Count;
        Check(children == 5, $"the controls row holds slower, pause, faster, the marks and the chip, and nothing else ({children})");
        var size = InterfaceSkin.Metric("hud.speed_pips", 8f);
        var marks = size * 5 + MathF.Round(size * .5f) * 4;
        var width = plate.OffsetRight - plate.OffsetLeft;
        var fixedPart = 2 + 16 + 58 + 8 + 28 + 34 + 28 + marks + 3 * 4;
        var chipRoom = width - fixedPart - 4;
        // "Сделки (12)" at 12 pt is about 66 px of text, and the HUD button's margins are 8 a side.
        Check(chipRoom >= 66 + 16, $"the deals chip has {chipRoom:0} px left of \"Menu\" for \"Сделки (12)\" (about 82)");
        // A longer count or another language trims the caption instead of widening the plate: the
        // chip's minimum width does not include its text, and it takes what room there is.
        Check(build.Contains("_datePlateDeals.ClipText = true;") && build.Contains("_datePlateDeals.SizeFlagsHorizontal = SizeFlags.ExpandFill;"),
            "the deals chip takes the room it is given and never asks the plate for more");

        // The layout editor names the plate and draws it beside the top bar, so a modder who moves
        // either one sees why the bar stops where it does.
        foreach (var language in new[] { "english", "russian" })
        {
            var names = Source($"game/localization/{language}/zz_380_date_plate.json");
            Check(names.Contains("\"layout.panel.hud.date_plate\":"), $"the layout editor has a {language} name for hud.date_plate");
        }
        var preview = Source("Scripts/ui/LayoutPreview.cs");
        foreach (var context in new[] { "HudContext", "MapSelectionContext" })
        {
            var line = preview.Split('\n').FirstOrDefault(text => text.Contains("string[] " + context + " =")) ?? "";
            Check(line.Contains("\"hud.top\"") && line.Contains("\"hud.date_plate\""), $"the layout editor draws the date plate with the top bar ({context})");
        }
    }

    /// <summary>
    /// The reason for the step: with the clock gone from the bar, the country, DEFCON, the twelve
    /// figures at their long width and the "Brief" toggle stand in one row at 1920. The numbers are
    /// read from the source and the data, so a wider cell or a new control on the bar fails here.
    /// </summary>
    private static void FiguresFitOneRow()
    {
        var bar = Source("Scripts/ui/TopBarHudUI.cs");
        var identity = Number(bar, @"identityBox = new StructuredMetricCell \{ CustomMinimumSize = new Vector2\((\d+), 0\)");
        var defcon = Number(bar, @"ActionButton\(""DEFCON 5"", OpenDefconOverview, (\d+), 28\)");
        var cellGap = Number(bar, @"stats\.AddThemeConstantOverride\(""separation"", (\d+)\)");
        var rowGap = Number(bar, @"row\.AddThemeConstantOverride\(""separation"", (\d+)\)");
        var margin = Number(bar, @"Margin\(top, (\d+), 4\)");
        Check(identity > 0 && defcon > 0 && cellGap > 0 && rowGap > 0 && margin > 0,
            $"the bar's sizes are read from the source: country {identity}, DEFCON {defcon}, gaps {cellGap}/{rowGap}, margin {margin}");
        var cells = InterfaceSkin.Metric("hud.resource_slots", 0f);
        var cell = InterfaceSkin.Metric("hud.resource_cell_width", 0f);
        Check(cells == 12 && cell > 0, $"twelve figures stand on a desktop plate, {cell:0} px each");
        // "Brief" / "Кратко" and, once pressed, "Full" / "Подробно": at 13 pt with 8 px margins the
        // widest is about 72 px.
        const float toggle = 76f;
        const float frame = 2f;
        var figures = cells * cell + cells * cellGap + toggle;
        var oneRow = frame + 2 * margin + identity + rowGap + defcon + rowGap + figures;

        var top = InterfaceSkin.Layout("hud.top");
        var plate = InterfaceSkin.Layout("hud.date_plate");
        var room1920 = (1920 + top.OffsetRight) - top.OffsetLeft;
        Check(oneRow <= room1920, $"at 1920 the country, DEFCON and all twelve figures need {oneRow:0} px and the bar has {room1920:0}: one row");
        Check(InterfaceSkin.Metric("hud.two_row_below", 1600f) <= 1920f, "and 1920 is wide enough for the one-row form at all");

        // The bar is laid out on the canvas, not the window. The project stretches a 1920x1080
        // canvas (canvas_items, expand), so a 1280x720 or 1366x768 window at the default interface
        // size has the 1920 canvas above and its one row; gating on the window's own 1280 put the
        // figures on a second row of the same width under a nearly empty first row.
        var project = Source("project.godot");
        Check(project.Contains("window/size/viewport_width=1920") && project.Contains("window/size/viewport_height=1080") &&
              project.Contains("window/stretch/mode=\"canvas_items\"") && project.Contains("window/stretch/aspect=\"expand\""),
            "the game lays out on a 1920x1080 canvas stretched to the window");
        foreach (var (w, h) in new[] { (1280f, 720f), (1366f, 768f) })
        {
            var scale = Math.Min(w / 1920f, h / 1080f);
            var canvas = w / scale;
            Check(canvas >= 1919f && oneRow <= (canvas + top.OffsetRight) - top.OffsetLeft,
                $"a {w:0}x{h:0} window at interface size 100 % has a {canvas:0} px canvas, and the figures stand in one row on it");
        }
        var resolve = HudResourceTests.MemberText(Source("Scripts/ui/HudReadabilityUI.cs"), "bool ResolveHudFigures()");
        Check(resolve.Contains("var windowWidth = Size.X;") && !resolve.Contains("GetWindow()"),
            "the one-row or two-row choice is made on the canvas width, not the window's pixels");

        // A canvas 1280 wide (interface size 150 % in a 1920 window, or a small phone): two rows (it
        // is below hud.two_row_below); the second runs below the date plate to its right end, and
        // the twelve long cells fit it.
        Check(InterfaceSkin.Metric("hud.two_row_below", 1600f) > 1280f, "on a 1280 canvas the figures take a second row");
        var secondRow = (1280 + plate.OffsetRight) - top.OffsetLeft;
        var figuresRow = frame + 2 * margin + figures;
        Check(figuresRow <= secondRow, $"on a 1280 canvas the second row needs {figuresRow:0} px and has {secondRow:0}");
        Check(InterfaceSkin.Metric("hud.resource_drawer_below", 1100f) <= 1280f, "and keeps all twelve: the drawer behind \"…\" is for phone widths only");
        var first = (1280 + top.OffsetRight) - top.OffsetLeft;
        Check(frame + 2 * margin + identity + rowGap + defcon <= first, $"the first row on a 1280 canvas holds the country and DEFCON left of the date plate ({first:0} px)");

        // The fallback is code that measures; it has to exist where the plan says.
        var fit = Source("Scripts/ui/HudReadabilityUI.cs");
        var plateUi = Source("Scripts/ui/DatePlateUI.cs");
        Check(fit.Contains("var available = TopPlateRoom(false);") && fit.Contains("<= TopPlateRoom(true)") &&
              fit.Contains("SecondRowHeaderExtra()") && fit.Contains("SecondRowClearance()"),
            "the bar is fitted against the room before the date plate, and a second row is made tall enough to clear it");
        Check(plateUi.Contains("Mathf.Min(right, width + plateLeft - DatePlateGap)") && Source("Scripts/ui/ResourcePlateUI.cs").Contains("DatePlateSpan(out var plateLeft, out var plateRight, out _)"),
            "on one row the bar stops before wherever the plate has grown to");
    }

    private static int Number(string source, string pattern)
    {
        var match = Regex.Match(source, pattern);
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    private static (float Left, float Top, float Right, float Bottom) Pixels(InterfaceSkin.PanelRect rect, float width, float height) =>
        (rect.AnchorLeft * width + rect.OffsetLeft, rect.AnchorTop * height + rect.OffsetTop,
         rect.AnchorRight * width + rect.OffsetRight, rect.AnchorBottom * height + rect.OffsetBottom);
}
