using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly List<StatReadout> _hudStats = new();

    private void BuildTopBar()
    {
        var top = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        _hudTopPanel = top;
        top.Name = "CampaignTopBar";
        Ui.Place(top, "hud.top");

        var margin = Margin(top, 12, 6);
        _hudTopStack = new VBoxContainer(); _hudTopStack.AddThemeConstantOverride("separation", 4);
        margin.AddChild(_hudTopStack);
        var row = new HBoxContainer();
        _hudTopRow = row;
        row.AddThemeConstantOverride("separation", 12);
        _hudTopStack.AddChild(row);

        // Whose war this is, said in the one way that needs no reading.
        _identityFlag = FlagBadge(_campaign?.Player?.Tag ?? "", 34);
        row.AddChild(_identityFlag);

        var identityBox = new VBoxContainer { CustomMinimumSize = new Vector2(154, 0) };
        _hudIdentityBox = identityBox;
        identityBox.AddThemeConstantOverride("separation", 2);
        row.AddChild(identityBox);
        _identity = Title("South Korea", 18, Ui.Colour("text.accent"));
        _identity.ClipText = true;
        _identity.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        // The bar is two lines tall and no more. A name too wide for the column has to
        // widen the column, not wrap into the line underneath it - which is what
        // "South Korea" did the moment it became "Южная Корея".
        _identity.AutowrapMode = TextServer.AutowrapMode.Off;
        identityBox.AddChild(_identity);
        _identityTag = Title("KOR · player nation", 13, Ui.Colour("text.muted"));
        _identityTag.AutowrapMode = TextServer.AutowrapMode.Off;
        identityBox.AddChild(_identityTag);

        var stats = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _hudStatRow = stats;
        _hudStats.Clear();
        stats.AddThemeConstantOverride("separation", 7);
        row.AddChild(stats);
        foreach (var field in CampaignUiCatalog.TopStats)
            switch (field)
            {
                case "TREASURY": _treasury = Stat(stats, field, "Cash on hand. The line below is the change over the last week of campaign time."); break;
                case "DEBT":
                    _publicDebt = Stat(stats, field, "Borrowed principal, separate from cash. Open sovereign finance to inspect interest, overdue bills and credit access.");
                    BindDebtHeadline();
                    break;
                case "BALANCE": _balance = Stat(stats, field, "What the treasury gains or loses each week, and everything that goes into it.", () => _campaign?.Player?.Budget); break;
                case "GDP": _gdp = Stat(stats, field, "Annual gross domestic product, as it emerges from what your economy actually produces."); break;
                case "POPULATION": _population = Stat(stats, field, "Everyone living in your states, and how that has moved over the last week.", () => _campaign?.Player is { } p ? HouseholdEconomy.ExplainNationPopulation(_campaign, p) : null); break;
                case "STABILITY": _stability = Stat(stats, field, "How settled the country is. Unrest costs money to police and output to bear.", () => _campaign?.Player?.StabilityDrivers); break;
                case "MANPOWER": _manpower = Stat(stats, field, "People left to put in uniform. A war that outruns this is a war that ends."); break;
                // What the army has learned, and what the government can push through: the
                // currencies of the war doctrines (MilitaryExperience, TacticalDoctrines).
                case "ARMY XP": _armyExperience = Stat(stats, field, "What the army has learned from fighting, drilling and being funded. Its line below is the price of the next article open to it."); break;
                case "AIR XP": _airExperience = Stat(stats, field, "What the air service has learned from flying real missions. It is what a doctrine of the sky is paid for in."); break;
                case "NAVY XP": _navyExperience = Stat(stats, field, "What the fleet has learned from sea time and action. It is what a doctrine of the coast is paid for in."); break;
                case "POLITICAL CAPITAL": _politicalPoints = Stat(stats, field, "What a government can push through besides money: rewriting a doctrine, or forcing a reform through the staff. Its line below is the weekly gain."); break;
                // Hover for the reasons, the same as stability: a rate with nothing behind
                // it cannot answer the question a player actually asks about it.
                case "INFLATION": _inflation = Stat(stats, field, "How fast prices are rising. Caused by what the government spends, what the market cannot supply and what the war costs; answered with the policy rate in the budget.", () => _campaign?.Player?.InflationDrivers); break;
            }

        var clock = new VBoxContainer { CustomMinimumSize = new Vector2(224, 0) };
        _hudClockBox = clock;
        clock.AddThemeConstantOverride("separation", 2);
        row.AddChild(clock);
        _date = Title("1 January 2024", 15, Ui.Colour("text.bright"));
        _date.HorizontalAlignment = HorizontalAlignment.Right;
        clock.AddChild(_date);

        var speeds = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        speeds.AddThemeConstantOverride("separation", 5);
        clock.AddChild(speeds);

        // Labelled rather than drawn. This was a hamburger glyph, and the one tester who
        // plays grand strategy for an audience reported that the game had no way out to the
        // main menu - it had two, Escape and this, and neither of them said so.
        var handbook = ActionButton("Handbook", () => OpenHandbook(), 0, 28);
        handbook.Name = "OpenHandbook";
        handbook.TooltipText = Ui.Tip(Tr("What every screen is for and what its figures mean (F1)."));
        speeds.AddChild(handbook);
        _dealInboxHudButton = ActionButton("Deals", OpenDealInbox, 0, 28);
        _dealInboxHudButton.Name = "PendingDiplomaticOffers";
        _dealInboxHudButton.Visible = false;
        speeds.AddChild(_dealInboxHudButton);
        var menu = ActionButton("Menu", ShowPauseMenu, 62, 28);
        menu.TooltipText = Ui.Tip(Tr("Pause menu (Esc): save, load, settings, and the way back to the main menu. F5 quick-saves, F9 loads, F10 flies the camera."));
        speeds.AddChild(menu);
        _pause = ActionButton("II", TogglePause, 32, 28, true);
        _pause.TooltipText = Ui.Tip(Tr("Pause or resume (Space)."));
        speeds.AddChild(_pause);
        _speedButtons.Clear();
        for (var speed = 1; speed <= 5; speed++)
        {
            var captured = speed;
            var button = ActionButton(speed.ToString(), () => SetSpeed(captured), 27, 28);
            _speedButtons.Add(button);
            speeds.AddChild(button);
        }
        UpdateSpeedButtons();

        _mediaButton = MusicShortcut();
        speeds.AddChild(_mediaButton);
        var window = GetWindow();
        window.SizeChanged += LayoutCampaignShell;
        TreeExiting += () => window.SizeChanged -= LayoutCampaignShell;
    }

    private void RefreshTopBar(Nation player)
    {
        _identity.Text = CountryNames.Compact(player);
        _identity.TooltipText = Ui.Tip(player.DisplayName);
        var identityFont = _identity.GetThemeFont("font");
        var identitySize = 18;
        while (identitySize > 12 && identityFont.GetStringSize(_identity.Text, HorizontalAlignment.Left, -1, identitySize).X >
               System.Math.Max(154, _identity.Size.X)) identitySize--;
        _identity.AddThemeFontSizeOverride("font_size", identitySize);
        // No tag for nobody: "OBS" read in Russian as "НАБ", a word nobody could place.
        _identityTag.Text = _observerMode
            ? Tr("Observer mode")
            : player.Tag + " · " + Tr("player nation");
        SetFlag(_identityFlag, player.Tag, 34);
        // The clock, whenever the campaign is running on something shorter than a day. At a
        // whole-day step the hour is always midnight and printing it would be noise; below
        // that it is the whole point, and it moves.
        var season = Simulation.WaterConditions.SeasonCaption(_campaign, player.CapitalLatitude);
        var date = _campaign.Date.Display(_campaign.TickHours < 24);
        _date.Text = season.Length == 0 ? date : date + " · " + Tr(season);
        _date.TooltipText = Ui.Tip(season.Length == 0
            ? "The calendar. Seasons are switched off, so the roads do not care what month it is."
            : "The season follows the calendar and this capital's latitude. Winter slows roads away from the equator.");
        _date.MouseFilter = MouseFilterEnum.Stop;

        var day = _campaign.ElapsedDays;
        _treasuryTrend.Sample(day, player.Treasury);
        _gdpTrend.Sample(day, player.Gdp);
        _populationTrend.Sample(day, player.Population);

        WriteStat(_treasury, Fmt.Money(player.Treasury), Fmt.MoneyExact(player.Treasury),
            Fmt.MoneyDelta((float)_treasuryTrend.Delta), Fmt.Direction(_treasuryTrend.Delta, 0.005d));
        RefreshDebtHeadline(player);
        WriteStat(_gdp, Fmt.Money(player.Gdp), Fmt.MoneyExact(player.Gdp),
            Fmt.MoneyDelta((float)_gdpTrend.Delta), Fmt.Direction(_gdpTrend.Delta, 0.005d));
        // One explanation per refresh: this walks the country's vital summary, and the tooltip
        // and the stat line used to ask for it twice on every redraw.
        var popExplain = DemographicCycle.Explain(_campaign, player);
        WriteStat(_population, Fmt.People(player.Population),
            popExplain,
            Fmt.PeopleDelta(_populationTrend.Delta), Fmt.Direction(_populationTrend.Delta, 1d));
        // A number that moves without saying why is a number a player cannot act on: the births,
        // deaths, migration and age structure behind it are read out where the number is
        // (DemographicCycle.Explain).
        if (_population?.Box is not null && _campaign?.Defines is not null)
        {
            _population.Box.Plain = popExplain;
            _population.Box.TooltipText = Ui.Tip(
                Tr("Everyone living in your states, and how that has moved over the last week.") +
                "\n\n" + popExplain);
        }

        // A stock without its flow is half a number: a treasury that reads the same two
        // weeks running is a very different country depending on which way it is heading.
        var balanced = player.Budget.HasEntries;
        WriteStat(_balance,
            balanced ? Fmt.MoneyDelta(player.WeeklyBalance) : "—",
            balanced
                ? Simulation.Loc.Format("{0} a week", Fmt.MoneyExact(player.WeeklyBalance))
                : Tr("The first economic week has not closed yet."),
            balanced ? Tr("a week") : "", balanced ? Fmt.Direction(player.WeeklyBalance, 0.005d) : 0);
        if (_stability is not null)
        {
            var rear = player.WarSupport > 0f
                ? Simulation.WarWeariness.BesideWarSupport(_campaign, player).Text
                : "";
            _stability.DeltaHover = rear.Length == 0
                ? ""
                : Simulation.Loc.Format("war support {0}", Fmt.Percent(player.WarSupport)) + "\n" + rear;
        }
        WriteStat(_stability, Fmt.Percent(player.Stability),
            Simulation.Loc.Format("Stability {0:P1}", player.Stability),
            player.WarSupport > 0f
                ? Simulation.Loc.Format("war support {0}", Fmt.Percent(player.WarSupport))
                : "", 0, Simulation.Loc.Format("War sup. {0}", Fmt.Percent(player.WarSupport)));
        WriteStat(_manpower, Fmt.People(player.ManpowerReserve), Fmt.PeopleExact((long)player.ManpowerReserve),
            Simulation.Loc.Format("{0}/day", Fmt.People(player.ReinforcementCapacity)), 0);

        // The four currencies the doctrines are paid in. A stock without its flow is half a
        // number here too: the army's line is the price of the next article open to it, the
        // wings' and the fleet's are how many of their own are earning right now, and the
        // government's is what a week of it adds.
        var nextArticle = NextArticlePrice(player);
        WriteStat(_armyExperience, Fmt.Number(player.ArmyExperience),
            Simulation.Loc.Format("Army experience {0}", Fmt.Number(player.ArmyExperience)),
            nextArticle > 0f
                ? Simulation.Loc.Format("next article {0}", Fmt.Number(nextArticle))
                : Tr("no article open"), 0,
            nextArticle > 0f ? Simulation.Loc.Format("Next: {0}", Fmt.Number(nextArticle)) : Tr("hud.footer.no_article"));
        WriteStat(_airExperience, Fmt.Number(player.AirExperience),
            Simulation.Loc.Format("Air experience {0}", Fmt.Number(player.AirExperience)),
            Simulation.Loc.Format("{0} flying", WingsFlying(player.Id)), 0);
        WriteStat(_navyExperience, Fmt.Number(player.NavalExperience),
            Simulation.Loc.Format("Naval experience {0}", Fmt.Number(player.NavalExperience)),
            Simulation.Loc.Format("{0} at sea", ShipsAtSea(player.Id)), 0);
        var weekly = _campaign.Defines.PoliticalPointsPerWeek +
            _campaign.Defines.PoliticalPointsStabilityWeight * player.Stability;
        WriteStat(_politicalPoints, Fmt.Number(player.PoliticalPoints),
            Simulation.Loc.Format("Political capital {0}", Fmt.Number(player.PoliticalPoints)),
            Simulation.Loc.Format("{0}/week", Fmt.Number(weekly)), 0);

        // Rising prices are bad news, so the arrow points the other way from every other
        // stat on this bar: up is not good here.
        if (_inflation is not null)
        {
            var comfort = Simulation.InflationCore.Weights.Comfort;
            var rate = player.Inflation;
            WriteStat(_inflation, rate.ToString("P1"),
                Simulation.Loc.Format("Prices are rising at {0} a year", rate.ToString("P2")),
                rate < 0f ? Tr("prices falling")
                    : rate > comfort ? Tr("above the comfortable rate")
                    : Tr("comfortable"),
                rate > comfort ? -1 : rate < 0f ? -1 : 0,
                rate < 0f ? Tr("hud.footer.prices_falling") : rate > comfort
                    ? Tr("hud.footer.above_comfort") : Tr("hud.footer.comfortable"));
        }

        // The half-year threads under the figures.
        _treasury?.Spark?.Push(day, player.Treasury);
        _gdp?.Spark?.Push(day, player.Gdp);
        _population?.Spark?.Push(day, player.Population);
        if (balanced) _balance?.Spark?.Push(day, player.WeeklyBalance);
        _stability?.Spark?.Push(day, player.Stability);
        _manpower?.Spark?.Push(day, player.ManpowerReserve);
        _inflation?.Spark?.Push(day, player.Inflation);
        WriteMovementTopBar(player);
        foreach (var stat in _hudStats) FitHudStat(stat);

        _militarySpending.SetValueNoSignal(player.MilitarySpending);
        UpdateSpendingReadout(player);
        UpdateSpeedButtons();
        UpdateScreenButtons();
    }

    /// <summary>
    /// One headline figure in the top bar: its caption, the compact value, and the week's
    /// movement underneath. A number with no direction is only half the information a
    /// player needs - "$40B" answers nothing that "$40B, −$1.2B this week" does not.
    /// </summary>
    private sealed class StatReadout
    {
        internal Label Value = null!;
        internal Label Delta = null!;
        internal Label Caption = null!;
        internal TextureRect Glyph;
        internal string CountryCaption = "";
        internal string CaptionKey = "";
        internal string FullDelta = "";
        internal string ShortDelta = "";
        /// <summary>Tooltip for the movement line when it already shows a war-support percent.</summary>
        internal string DeltaHover = "";
        internal ExplainedStat Box;
        /// <summary>Half a year of the figure under it (Sparkline).</summary>
        internal Sparkline Spark;
    }

    /// <summary>
    /// The cheapest article the country's book has open right now: the number a player needs
    /// under the army's experience to know what it is saving for. Nought when nothing is open.
    /// </summary>
    private float NextArticlePrice(Nation player)
    {
        if (!_campaign.FeatureEnabled("tactical_doctrines")) return 0f;
        var branch = player.TacticalDoctrine.Branch.Length > 0
            ? player.TacticalDoctrine.Branch
            : TacticalDoctrines.PreferredBranch(_campaign, player);
        var price = 0f;
        foreach (var node in TacticalDoctrines.Available(player, branch))
            if (price == 0f || node.Cost < price) price = node.Cost;
        return price;
    }

    /// <summary>Wings of this country earning experience today, by the same rule MilitaryExperience uses.</summary>
    private int WingsFlying(int nationId)
    {
        var count = 0;
        foreach (var formation in _campaign.SupportFormations)
            if (formation.NationId == nationId && formation.Strength > 0.05f &&
                !string.IsNullOrEmpty(formation.Mission) &&
                formation.Mission != SupportCore.MissionStandby) count++;
        return count;
    }

    /// <summary>Hulls of this country earning experience today, by the same rule MilitaryExperience uses.</summary>
    private int ShipsAtSea(int nationId)
    {
        var count = 0;
        foreach (var fleet in _campaign.Fleets)
            if (fleet.NationId == nationId && fleet.Hulls > 0 &&
                (fleet.InAction || fleet.Mission != FleetMission.Anchored)) count++;
        return count;
    }

    /// <summary>
    /// Which symbol stands for a figure on the top bar.
    ///
    /// Defaults live here; campaign.icons lets a mod remap roles without replacing art
    /// shared by other controls. A missing sprite keeps the text caption.
    /// </summary>
    private static string StatIcon(string caption) => CampaignUiCatalog.Icon("hud." + caption, caption switch
    {
        "TREASURY" => "treasury",
        "DEBT" => "coins",
        "BALANCE" => "balance",
        "GDP" => "gdp",
        "INFLATION" => "inflation",
        "POPULATION" => "population",
        "STABILITY" => "stability",
        "MANPOWER" => "manpower",
        "ARMY XP" => "military",
        "AIR XP" => "air",
        "NAVY XP" => "navy",
        "POLITICAL CAPITAL" => "agenda",
        _ => ""
    });

    private StatReadout Stat(Container parent, string caption, string tooltip,
        System.Func<Simulation.Breakdown> explain = null)
    {
        // A figure that can be hovered for the parts it is made of, when there are parts to
        // show; an ordinary tooltip when there are not.
        var box = new ExplainedStat
        {
            Name = "HudStat_" + caption.Replace(' ', '_'),
            CustomMinimumSize = new Vector2(68, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Explain = explain,
            Plain = explain is null ? "" : Tr(tooltip)
        };
        // The caption is in the tooltip now rather than gone: a symbol a player has not
        // learned yet has to be able to say what it is.
        Tip(box, Tr(caption) + " \u00b7 " + Tr(tooltip));
        box.AddThemeConstantOverride("separation", 1);
        box.MouseFilter = MouseFilterEnum.Stop;
        parent.AddChild(box);
        // Keep a short label beside the symbol: unfamiliar icons must not hide meaning.
        var heading = new HBoxContainer { Name = "StatHeading", MouseFilter = MouseFilterEnum.Ignore };
        heading.AddThemeConstantOverride("separation", 5);
        box.AddChild(heading);
        TextureRect glyph = null;
        var symbol = StatIcon(caption);
        if (symbol.Length > 0 && SpriteCatalog.Has("icon_" + symbol))
        {
            glyph = new TextureRect
            {
                Texture = Art("icon_" + symbol, $"res://game/gfx/interface/icons/icon_{symbol}.png"),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                // Both dimensions. A minimum height with a width of zero is a rect with no
                // width, which is a texture nobody ever sees - and the alert rail's icons
                // rendered fine the whole time, which is what said it was the size and not
                // the sprite.
                CustomMinimumSize = new Vector2(16, 16),
                MouseFilter = MouseFilterEnum.Ignore,
                Modulate = Ui.Colour("text.accent_dim")
            };
            glyph.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            heading.AddChild(glyph);
        }
        var statCaption = Title(caption, 10, Ui.Colour("text.caption"));
        statCaption.Name = "StatCaption";
        statCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        statCaption.ClipText = true;
        statCaption.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        statCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        statCaption.Visible = InterfaceSkin.Metric("hud.stat_labels", 1) > 0 || glyph is null;
        heading.AddChild(statCaption);

        var readout = new StatReadout
        {
            Caption = statCaption,
            Glyph = glyph,
            CountryCaption = caption,
            CaptionKey = caption,
            Box = box,
            Value = Title("—", 17, Ui.Colour("text.bright")),
            Delta = Title("", 11, Ui.Colour("text.faint"))
        };
        // Neither of these may wrap. The bar is one row tall and the column is sized to the
        // widest thing in it, so a line that wraps grows downwards out of the bar instead of
        // widening its column - which is what "war support 63 %" did the moment it became
        // "поддержка войны 63 %".
        readout.Value.AutowrapMode = TextServer.AutowrapMode.Off;
        readout.Delta.AutowrapMode = TextServer.AutowrapMode.Off;
        readout.Value.ClipText = true;
        readout.Delta.ClipText = true;
        readout.Delta.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        readout.Value.AddThemeFontOverride("font", PresentationFonts.Tabular());
        readout.Delta.AddThemeFontOverride("font", PresentationFonts.Tabular());
        box.AddChild(readout.Value);
        // The week's movement and, beside it, the half-year it belongs to.
        var movement = new HBoxContainer { Name = "StatMovement", MouseFilter = MouseFilterEnum.Ignore };
        movement.AddThemeConstantOverride("separation", 4);
        readout.Spark = new Sparkline { Name = "StatSpark", Flash = readout.Value,
            HigherIsBetter = caption is not ("INFLATION" or "DEBT") };
        movement.AddChild(readout.Spark);
        readout.Delta.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        movement.AddChild(readout.Delta);
        box.AddChild(movement);
        _hudStats.Add(readout);
        readout.Value.Resized += () => FitHudStat(readout);
        readout.Delta.Resized += () => FitHudStat(readout);
        return readout;
    }

    /// <summary>
    /// Write one headline figure and its movement. The exact figure goes into the tooltip:
    /// the compact form is for scanning, and the moment a player wants the real number
    /// they want every digit of it.
    /// </summary>
    private static void WriteStat(StatReadout stat, string compact, string exact, string delta, int direction,
        string shortDelta = null)
    {
        if (stat is null) return;
        stat.Value.Text = compact;
        stat.Value.TooltipText = Ui.Tip(exact);
        stat.Value.MouseFilter = MouseFilterEnum.Stop;
        stat.FullDelta = delta;
        stat.ShortDelta = shortDelta ?? delta;
        stat.Delta.TooltipText = Ui.Tip(delta);
        stat.Delta.MouseFilter = MouseFilterEnum.Stop;
        stat.Delta.AddThemeColorOverride("font_color", direction switch
        {
            > 0 => Ui.Colour("state.positive"),
            < 0 => Ui.Colour("state.negative"),
            _ => Ui.Colour("text.faint")
        });
        FitHudStat(stat);
    }

    private static void SetHudCaption(StatReadout stat, string key)
    {
        stat.CaptionKey = key;
        FitHudStat(stat);
    }

    // Text has priority over the optional history thread. Choose using the whole
    // cell width, not the previous layout's delta width, to avoid resize oscillation.
    private static void FitHudStat(StatReadout stat)
    {
        if (stat?.Box is null || !GodotObject.IsInstanceValid(stat.Box)) return;
        var width = stat.Box.Size.X;
        if (width <= 1) return; // Resized runs again after the first real layout.
        var caption = Tr(stat.CaptionKey);
        var shortKey = "hud.stat.short." + stat.CaptionKey;
        var shortCaption = Simulation.Loc.Has(shortKey) ? Tr(shortKey) : caption;
        var headingWidth = width - (stat.Glyph is { Visible: true } ? 21 : 0);
        FitHudLine(stat.Caption, caption, shortCaption, headingWidth, 10, 10);
        FitHudLine(stat.Value, stat.Value.Text, stat.Value.Text, width, 17, 11);
        var full = stat.FullDelta;
        var shorter = stat.ShortDelta;
        var font = stat.Delta.GetThemeFont("font");
        var fullWidth = font.GetStringSize(full, HorizontalAlignment.Left, -1, 11).X;
        var shortWidth = font.GetStringSize(shorter, HorizontalAlignment.Left, -1, 11).X;
        var graphWidth = stat.Spark.GetCombinedMinimumSize().X + 4;
        var graph = stat.Spark.HasHistory && fullWidth + graphWidth <= width;
        var shown = full;
        if (fullWidth > width)
        {
            shown = shorter;
            graph = stat.Spark.HasHistory && shortWidth + graphWidth <= width;
        }
        stat.Spark.AllowLayout(graph);
        FitHudLine(stat.Delta, shown, shown, width - (graph ? graphWidth : 0), 11, 10);
        stat.Delta.TooltipText = Ui.Tip(string.IsNullOrEmpty(stat.DeltaHover) ? full : stat.DeltaHover);
        stat.Caption.TooltipText = Ui.Tip(caption);
        stat.Caption.MouseFilter = MouseFilterEnum.Stop;
    }

    private static void FitHudLine(Label label, string full, string shorter, float width, int preferred, int minimum)
    {
        var font = label.GetThemeFont("font");
        var shown = font.GetStringSize(full, HorizontalAlignment.Left, -1, preferred).X <= width ? full : shorter;
        var size = preferred;
        while (size > minimum && font.GetStringSize(shown, HorizontalAlignment.Left, -1, size).X > width) size--;
        if (label.Text != shown) label.Text = shown;
        if (label.GetThemeFontSize("font_size") != size) label.AddThemeFontSizeOverride("font_size", size);
    }

    /// <summary>
    /// A rolling week of daily samples of one figure, so the top bar can show real movement
    /// rather than a number that simply is what it is. Kept to eight days: the campaign
    /// advances one day at a time, and a week is the horizon a player plans against.
    /// </summary>
    private sealed class Trend
    {
        private readonly System.Collections.Generic.List<(int Day, double Value)> _samples = new();

        internal void Sample(int day, double value)
        {
            if (_samples.Count > 0 && _samples[^1].Day == day)
            {
                _samples[^1] = (day, value);
                return;
            }
            _samples.Add((day, value));
            while (_samples.Count > 1 && day - _samples[0].Day > 7) _samples.RemoveAt(0);
        }

        /// <summary>Movement across the sampled window; exactly zero until a second day exists.</summary>
        internal double Delta => _samples.Count < 2 ? 0d : _samples[^1].Value - _samples[0].Value;

        internal void Reset() => _samples.Clear();
    }

    /// <summary>
    /// What the military slider is actually setting. The slider itself carries no units,
    /// and "0.34" means nothing until it is named as a share of output and priced.
    /// </summary>
    private void UpdateSpendingReadout(Nation player)
    {
        if (_militarySpendingReadout is null || player is null) return;
        var annual = player.Gdp * player.MilitarySpending;
        _militarySpendingReadout.Text =
            Simulation.Loc.Format("{0} of output · about {1} a year", Fmt.Percent(player.MilitarySpending), Fmt.Money(annual));
    }

    /// <summary>Forget the week of history behind the top bar - a new campaign has none.</summary>
    private void ResetTrends()
    {
        _treasuryTrend.Reset();
        _gdpTrend.Reset();
        _populationTrend.Reset();
        foreach (var stat in new[] { _treasury, _gdp, _population, _balance, _stability, _manpower, _inflation })
            stat?.Spark?.Reset();
    }

    /// <summary>The speed the campaign is actually running at, lit so the player can see it.</summary>
    private void UpdateSpeedButtons()
    {
        var running = _campaign is not null && !_campaign.IsPaused;
        for (var index = 0; index < _speedButtons.Count; index++)
        {
            var button = _speedButtons[index];
            if (!GodotObject.IsInstanceValid(button)) continue;
            StyleButton(button, running && index + 1 == _speed);
            button.TooltipText = Ui.Tip(_campaign is null
                ? Simulation.Loc.Format("Campaign speed {0}", index + 1)
                : Simulation.Loc.Format("Campaign speed {0} · {1:0.#} campaign hours a second, {2} hour(s) a step",
                    index + 1, 24f / Mathf.Max(0.001f, _campaign.Defines.SecondsForSpeed(index + 1)), _campaign.TickHours));
        }
    }

    /// <summary>The screen currently open, lit in the bottom bar.</summary>
    private void UpdateScreenButtons()
    {
        var open =
            _situationLayer is { Visible: true } ? "Situation" :
            _sovereignFinanceLayer is { Visible: true } ? "Economy" :
            _stateAffairsLayer is { Visible: true } ? "State Affairs" :
            _ledgerLayer is { Visible: true } ? "Ledger" :
            _warLedgerLayer is { Visible: true } ? "Wars" :
            _supportLayer is { Visible: true } ? "Support" :
            _productionLayer is { Visible: true } ? "Production" :
            _researchLayer is { Visible: true } ? "Research" :
            _tradeScreenLayer is { Visible: true } ? "Trade" :
            _diplomacyLayer is { Visible: true } ? "Diplomacy" :
            _conflictsLayer is { Visible: true } ? "Conflicts" :
            _policyLayer is { Visible: true } ? "Laws" :
            _sidePanel is { Visible: true } ? _openScreen : "";
        foreach (var (screen, button) in _screenButtons)
        {
            if (!GodotObject.IsInstanceValid(button)) continue;
            StyleButton(button, screen == open);
        }
        RefreshMapVisualToggles();
    }
}
