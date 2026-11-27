using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The top right of the campaign (docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md, step 3): one plate
/// for time. The date large, in the serif with figures that do not jump as the day changes; under
/// it slower, pause and faster, and five marks that show the speed; beside it a square "Menu".
///
/// It replaces the row of player buttons that stood at the end of the top bar (Handbook, Find,
/// Deals, Menu, pause, five numbered speeds and the music note). That row made the screen read as
/// an application, and it took the 470 pixels the twelve figures of the resource plate needed to
/// stand in one row at 1920. Where each of those buttons went is the table in
/// Tools/FormableTests/DatePlateTests.cs, which checks it against the source:
///   Handbook - the pause menu and F1;  Find - the pause menu, the rail's search (Enter, "Find all")
///   and Ctrl+K;  Deals - a chip on this plate, shown while offers wait;  Menu, pause - this plate,
///   Esc and Space;  speeds - the marks here (a click on one) and keys 1-5, plus and minus;
///   music - the pause menu, beside Settings.
///
/// The keys are where they were (DealOrForceGame._UnhandledInput). The rules - bounds of slower and
/// faster, which mark is lit, which mark a click lands on - are DatePlateClock's.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _hudDatePlate;
    private SpeedPips _speedPips;
    private Button _datePlateDeals;
    private string _dateFull = "", _dateShort = "";

    private void BuildDatePlate()
    {
        // Anchored to the right edge and growing leftwards: a longer caption in another language
        // widens the plate towards the figures, never off the screen.
        var plate = new PanelContainer { Name = "DatePlate", GrowHorizontal = GrowDirection.Begin };
        _hudDatePlate = plate;
        _hudLayer.AddChild(plate);
        ApplyHudFrame(plate);
        PlaceDatePlate();
        var margin = Margin(plate, 8, 4);
        // The clock and the menu, and under them the state of the world (WorldStripUI).
        var stack = new VBoxContainer { Name = "DatePlateStack" };
        stack.AddThemeConstantOverride("separation", 4);
        margin.AddChild(stack);
        var row = new HBoxContainer { Name = "DatePlateRow" };
        row.AddThemeConstantOverride("separation", 8);
        stack.AddChild(row);
        BuildWorldStrip(stack);

        var column = new VBoxContainer
        {
            Name = "DatePlateClock", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        column.AddThemeConstantOverride("separation", 2);
        row.AddChild(column);

        _date = Title("1 January 2024", 20, Ui.Colour("text.accent"));
        _date.Name = "DatePlateDate";
        // The heading serif with tabular figures: "28" and "11" take the same room, so the date
        // does not shift sideways every day the clock runs.
        _date.AddThemeFontOverride("font", PresentationFonts.Tabular("ui.display"));
        _date.AutowrapMode = TextServer.AutowrapMode.Off;
        _date.ClipText = true;
        _date.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _date.MouseFilter = MouseFilterEnum.Stop;
        _date.Resized += FitDatePlateDate;
        column.AddChild(_date);

        var controls = new HBoxContainer { Name = "DatePlateControls" };
        controls.AddThemeConstantOverride("separation", 4);
        column.AddChild(controls);

        // Minus and plus, the keys that do the same: one step, and a paused clock stays paused,
        // so the pace can be chosen before time is let go.
        var slower = ActionButton("−", () => StepSpeed(-1), 28, 28);
        slower.Name = "DatePlateSlower";
        slower.AccessibilityName = Tr("Slower");
        StyleHudButton(slower, false);
        controls.AddChild(slower);
        _pause = ActionButton("II", TogglePause, 34, 28, true);
        _pause.Name = "DatePlatePause";
        _pause.TooltipText = Ui.Tip(Tr("Pause or resume (Space). Keys 1 to 8 and the marks choose the speed without starting time; plus and minus make it one step faster or slower."));
        StyleHudButton(_pause, true);
        controls.AddChild(_pause);
        var faster = ActionButton("+", () => StepSpeed(1), 28, 28);
        faster.Name = "DatePlateFaster";
        faster.AccessibilityName = Tr("Faster");
        StyleHudButton(faster, false);
        controls.AddChild(faster);
        // The list once held the five numbered speed buttons; it now holds the two that step it.
        _speedButtons.Clear();
        _speedButtons.Add(slower);
        _speedButtons.Add(faster);

        // Five drawn marks, not five more buttons: the speed is read at a glance, and a click on a
        // mark still sets it in one move, the way the numbered buttons did.
        _speedPips = new SpeedPips
        {
            Name = "DatePlateSpeed", SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand,
            Choose = speed =>
            {
                PlayStrategicCue("ui_select");
                // As in Paradox games: a mark chooses the speed; only the pause button and Space start time.
                SetSpeed(speed, resume: false);
            },
            // Read every frame: the clock is paused from many places (an event, a peace
            // conference, the save list, a seat handover, the host's heartbeat), and marks that
            // waited for one of them to remember the plate would show a stopped clock as running.
            Clock = () => (DatePlateClock.Clamp(_speed), _campaign?.IsPaused ?? true)
        };
        controls.AddChild(_speedPips);

        // An offer waiting for an answer is a problem the player should see without looking for
        // it: a chip with its count, here where the eye goes for the date, and nothing at all when
        // nothing waits (DealInboxUI keeps it current). It takes the room left of "Menu" and trims
        // its caption rather than ask for more, so its coming and going never moves the plate.
        _datePlateDeals = ActionButton("Deals", OpenDealInbox, 0, 24);
        _datePlateDeals.Name = "DatePlateDeals";
        _datePlateDeals.Visible = false;
        _datePlateDeals.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _datePlateDeals.ClipText = true;
        _datePlateDeals.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _datePlateDeals.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _datePlateDeals.AddThemeFontSizeOverride("font_size", 12);
        StyleHudButton(_datePlateDeals, false);
        controls.AddChild(_datePlateDeals);

        // Square and captioned. The game had two ways out to the main menu, Escape and a drawn
        // glyph, and a tester reported it had none; the word is what tells a player.
        var menu = ActionButton("Menu", ShowPauseMenu, 58, 58);
        menu.Name = "OpenPauseMenu";
        menu.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        Tip(menu, "Pause menu (Esc): save, load, settings, the handbook, the index of every screen, the music and the way back to the main menu. F5 quick-saves, F9 loads, F10 flies the camera.");
        StyleHudButton(menu, false);
        row.AddChild(menu);
        UpdateSpeedButtons();
    }

    /// <summary>Where the layout says the plate goes; its material, when one is declared and switched on.</summary>
    private void PlaceDatePlate()
    {
        if (_hudDatePlate is null || !GodotObject.IsInstanceValid(_hudDatePlate)) return;
        Ui.Place(_hudDatePlate, "hud.date_plate");
    }

    private static float DatePlateGap => Mathf.Clamp(Ui.Metric("hud.dock_gap", 8), 4, 24);

    /// <summary>
    /// Where the date plate stands along the top edge, as offsets from the right edge of the screen:
    /// its left end (further left than the data says when its content needs more room), its right
    /// end and its bottom. False when the plate and the top bar do not share the top band - a mod
    /// that moved either one - and then neither is fitted around the other.
    /// </summary>
    private bool DatePlateSpan(out float left, out float right, out float bottom)
    {
        left = right = bottom = 0f;
        if (_hudDatePlate is null || !GodotObject.IsInstanceValid(_hudDatePlate) || !_hudDatePlate.Visible) return false;
        var plate = InterfaceSkin.Layout("hud.date_plate");
        var top = InterfaceSkin.Layout("hud.top");
        if (plate.AnchorLeft != 1f || plate.AnchorRight != 1f || plate.AnchorTop != 0f || plate.AnchorBottom != 0f ||
            top.AnchorRight != 1f || top.AnchorTop != 0f || plate.OffsetTop >= top.OffsetBottom) return false;
        var least = _hudDatePlate.GetCombinedMinimumSize();
        right = plate.OffsetRight;
        left = plate.OffsetRight - Mathf.Max(plate.OffsetRight - plate.OffsetLeft, least.X);
        bottom = Mathf.Max(plate.OffsetBottom, plate.OffsetTop + least.Y);
        return true;
    }

    /// <summary>
    /// The width the top bar has for its figures: up to the date plate on one row; on a second row,
    /// which starts below the plate, as far as the plate's right end.
    /// </summary>
    private float TopPlateRoom(bool secondRow)
    {
        var width = Size.X;
        var top = InterfaceSkin.Layout("hud.top");
        var left = top.AnchorLeft * width + top.OffsetLeft;
        var right = top.AnchorRight * width + top.OffsetRight;
        if (DatePlateSpan(out var plateLeft, out var plateRight, out _))
            right = secondRow ? Mathf.Max(right, width + plateRight) : Mathf.Min(right, width + plateLeft - DatePlateGap);
        return Mathf.Max(1f, right - left);
    }

    /// <summary>The top bar's own frame and margins, above and below what it holds.</summary>
    private float TopPlateChrome() => _hudTopPanel is null || _hudTopStack is null ? 0f
        : Mathf.Max(0f, _hudTopPanel.GetCombinedMinimumSize().Y - _hudTopStack.GetCombinedMinimumSize().Y);

    /// <summary>How tall the first row is made on a two-row bar, so the second starts below the date plate.</summary>
    private float SecondRowClearance()
    {
        if (_hudTopStack is null || !DatePlateSpan(out _, out _, out var bottom)) return 0f;
        var top = InterfaceSkin.Layout("hud.top");
        return Mathf.Max(0f, bottom + 2f - top.OffsetTop - TopPlateChrome() * .5f - _hudTopStack.GetThemeConstant("separation"));
    }

    /// <summary>The height a two-row bar needs beyond its layout: the first row down past the plate, then the figures.</summary>
    private float SecondRowHeaderExtra()
    {
        if (_hudTopRow is null || _hudStatRow is null || _hudTopStack is null) return 0f;
        var top = InterfaceSkin.Layout("hud.top");
        if (top.AnchorTop != 0f || top.AnchorBottom != 0f) return 0f;
        var first = SecondRowClearance();
        foreach (var child in _hudTopRow.GetChildren())
            if (child is Control control && control != _hudStatRow && control.Visible)
                first = Mathf.Max(first, control.GetCombinedMinimumSize().Y);
        _hudStatRow.UpdateMinimumSize();
        var needed = TopPlateChrome() + first + _hudTopStack.GetThemeConstant("separation") + _hudStatRow.GetCombinedMinimumSize().Y;
        return Mathf.Max(0f, Mathf.Ceil(needed - (top.OffsetBottom - top.OffsetTop)));
    }

    /// <summary>The calendar, and the season at this capital's latitude, written onto the plate.</summary>
    private void RefreshDatePlate(Nation player)
    {
        if (_date is null || _campaign is null) return;
        // Always show campaign hours (HH:00) alongside the date so hourly and sub-day progression is legible at a glance.
        var currentDate = _simRunner.ReadPresentationDate(_campaign);
        var season = player is null ? "" : Simulation.WaterConditions.SeasonCaption(_campaign, player.CapitalLatitude, currentDate);
        var date = currentDate.Display(withTime: true);
        _dateShort = date;
        _dateFull = season.Length == 0 ? date : date + " · " + Tr(season);
        _date.TooltipText = Ui.Tip((season.Length == 0
            ? Tr("The calendar. Seasons are switched off, so the roads do not care what month it is.")
            : Tr("The season follows the calendar and this capital's latitude. Winter slows roads away from the equator.")) +
            (season.Length == 0 ? "" : "\n" + _dateFull));
        FitDatePlateDate();
    }

    /// <summary>The date at 20 pt, smaller before it is cut, and without the season before that.</summary>
    private void FitDatePlateDate()
    {
        if (_date is null || !GodotObject.IsInstanceValid(_date) || _dateFull.Length == 0) return;
        var width = _date.Size.X > 1 ? _date.Size.X : 220f;
        FitHudLine(_date, _dateFull, _dateShort, width, 20, 13);
    }

    /// <summary>One step slower or faster, as minus and plus do: the clock is not started by it.</summary>
    private void StepSpeed(int direction)
    {
        if (_campaign is null) return;
        SetSpeed(direction < 0 ? DatePlateClock.Slower(_speed) : DatePlateClock.Faster(_speed), resume: false);
    }

    /// <summary>
    /// The speed the clock is set to, shown by the marks - while paused as well, a little dimmer.
    /// Plus and minus change the pace of a paused game, and a change nobody can see is a key that
    /// seems broken; whether time is running is the pause button's to say. "Slower" at the slowest
    /// speed and "faster" at the fastest are greyed: the end of the range is seen, not discovered.
    /// </summary>
    private void UpdateSpeedButtons()
    {
        var running = _campaign is not null;
        var speed = DatePlateClock.Clamp(_speed);
        var rate = running
            ? Simulation.Loc.Format("Campaign speed {0} (key {0}) · {1:0.#} campaign hours a second, {2} hour(s) a step",
                speed, 24f / Mathf.Max(0.001f, _campaign.Defines.SecondsForSpeed(speed)), _campaign.TickHours)
            : Simulation.Loc.Format("Campaign speed {0} (key {0})", speed);
        if (_speedPips is not null && GodotObject.IsInstanceValid(_speedPips))
        {
            var paused = _campaign?.IsPaused ?? true;
            if (_speedPips.Speed != speed || _speedPips.Paused != paused)
            {
                _speedPips.Speed = speed;
                _speedPips.Paused = paused;
                _speedPips.QueueRedraw();
            }
            _speedPips.TooltipText = Ui.Tip(rate + "\n" + SmoothTimePacer.FormatSpeedTooltipSuffix(ActiveSmoothTimeMode()) + "\n" +
                Tr("Keys 1 to 8, or a click on a mark, set the speed and let time run; plus and minus step it."));
            _speedPips.AccessibilityName = Simulation.Loc.Format("Campaign speed {0} (key {0})", speed);
        }
        if (_speedButtons.Count == 2)
        {
            var (slower, faster) = (_speedButtons[0], _speedButtons[1]);
            if (GodotObject.IsInstanceValid(slower))
            {
                slower.Disabled = !running || DatePlateClock.Slower(speed) == speed;
                slower.TooltipText = Ui.Tip(Tr("One step slower (minus key). A paused clock stays paused.") + "\n" + rate);
            }
            if (GodotObject.IsInstanceValid(faster))
            {
                faster.Disabled = !running || DatePlateClock.Faster(speed) == speed;
                faster.TooltipText = Ui.Tip(Tr("One step faster (plus key). A paused clock stays paused.") + "\n" + rate);
            }
        }
    }

    /// <summary>The deals chip: shown with its count while offers wait for an answer, gone when none do.</summary>
    private void RefreshDatePlateDeals(int count)
    {
        if (_datePlateDeals is null || !GodotObject.IsInstanceValid(_datePlateDeals)) return;
        var waiting = count > 0 && !_observerMode;
        if (_datePlateDeals.Visible != waiting) _datePlateDeals.Visible = waiting;
        if (!waiting) return;
        _datePlateDeals.Text = Loc.Format("Deals ({0})", count);
        _datePlateDeals.TooltipText = Ui.Tip(Tr("Incoming offers await your choice. Opening the inbox never signs or spends anything."));
        // Waiting is a warning, not a failure: amber, the way a figure on the plate turns amber.
        _datePlateDeals.AddThemeColorOverride("font_color", Ui.Colour("state.warning"));
    }

    /// <summary>
    /// Five marks for the five speeds, rising left to right, the reached ones in the accent. Drawn,
    /// not a row of buttons; a click on a mark sets that speed and lets time run, as its key does.
    /// The mark's size is `hud.speed_pips`.
    /// </summary>
    private sealed partial class SpeedPips : Control
    {
        public int Speed = 1;
        public bool Paused = true;
        public Action<int> Choose;
        public Func<(int Speed, bool Paused)> Clock;

        public override void _Process(double delta)
        {
            if (Clock is null) return;
            var (speed, paused) = Clock();
            if (speed == Speed && paused == Paused) return;
            Speed = speed;
            Paused = paused;
            QueueRedraw();
        }

        private static float PipSize => Mathf.Clamp(MathF.Round(Ui.Metric("hud.speed_pips", 8f) * .64f), 5f, 10f);
        private static float PipGap => Mathf.Clamp(MathF.Round(PipSize * .4f), 2f, 4f);

        public override Vector2 _GetMinimumSize() =>
            new(PipSize * DatePlateClock.Fastest + PipGap * (DatePlateClock.Fastest - 1), 28f);

        public override void _Draw()
        {
            var size = PipSize;
            var gap = PipGap;
            var tallest = Mathf.Round(size * (.9f + .25f * (DatePlateClock.Fastest - 1)));
            var floor = (Size.Y + tallest) * .5f;
            // Dimmer while paused: the speed is still read, and so is the stillness.
            var lit = Ui.Colour("button.primary_border", Paused ? .55f : 1f);
            var unlit = Ui.Colour("surface.inset");
            var edge = Ui.Colour("surface.line");
            for (var pip = DatePlateClock.Slowest; pip <= DatePlateClock.Fastest; pip++)
            {
                var height = Mathf.Round(size * (.9f + .25f * (pip - 1)));
                var rect = new Rect2((pip - 1) * (size + gap), floor - height, size, height);
                DrawRect(rect, DatePlateClock.Lit(pip, Speed) ? lit : unlit);
                DrawRect(rect, edge, false, 1f);
            }
        }

        public override void _GuiInput(InputEvent input)
        {
            if (input is not InputEventMouseButton { Pressed: true } click) return;
            if (click.ButtonIndex == MouseButton.Left)
            {
                Choose?.Invoke(DatePlateClock.PipAt(click.Position.X, PipSize, PipGap));
                AcceptEvent();
                return;
            }
            if (click.ButtonIndex == MouseButton.WheelUp)
            {
                Choose?.Invoke(DatePlateClock.Faster(Speed));
                AcceptEvent();
                return;
            }
            if (click.ButtonIndex == MouseButton.WheelDown)
            {
                Choose?.Invoke(DatePlateClock.Slower(Speed));
                AcceptEvent();
            }
        }
    }
}
