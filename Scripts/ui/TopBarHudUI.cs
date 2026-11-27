using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly List<StatReadout> _hudStats = new();
    private VBoxContainer _identityPortraitSlot;
    private string _identityPortraitLeaderKey = "";

    private static int HudRhythm(string key, float fallback) =>
        Math.Max(4, (int)Math.Round(Ui.Metric(key, fallback) / 4f, MidpointRounding.AwayFromZero) * 4);

    private static void ApplyHudFrame(PanelContainer panel, bool floating = false)
    {
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.background"),
            BorderColor = Ui.Colour("shell.border"),
            ShadowColor = Ui.Colour(floating ? "surface.background" : "text.body", floating ? 0.42f : 0.06f),
            ShadowSize = floating ? 6 : 1,
            ShadowOffset = floating ? new Vector2(0, 3) : new Vector2(0, -1)
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(3);
        style.SetContentMarginAll(1);
        panel.AddThemeStyleboxOverride("panel", style);
        if (panel.GetNodeOrNull<AtlasPanelFinish>("AtlasMaterial") is { } finish) finish.Visible = true;
    }

    private static StyleBoxFlat HudButtonStyle(bool primary, int state, bool compact, bool quiet = false)
    {
        var fill = Ui.Colour(state switch
        {
            1 => primary ? "button.primary_fill_hover" : "button.fill_hover",
            2 => primary ? "button.primary_fill_pressed" : "button.fill_pressed",
            3 => primary ? "button.primary_fill" : "button.fill_disabled",
            _ => primary ? "button.primary_fill" : "button.fill"
        });
        var edge = Ui.Colour(state switch
        {
            1 => "button.border_hover", 2 => "button.border_pressed",
            3 => primary ? "button.primary_border" : "button.border_disabled",
            _ => primary ? "button.primary_border" : "button.border"
        });
        if (quiet && state == 0 && !primary)
        {
            fill = Ui.Colour("surface.inset", 0f);
            edge = Ui.Colour("surface.line_faint", 0f);
        }
        var style = new StyleBoxFlat
        {
            BgColor = fill, BorderColor = edge,
            ContentMarginLeft = compact ? 2 : 8, ContentMarginRight = compact ? 2 : 8,
            ContentMarginTop = compact ? 2 : 4, ContentMarginBottom = compact ? 2 : 4
        };
        style.SetBorderWidthAll(quiet && state == 0 && !primary ? 0 : 1);
        style.SetCornerRadiusAll(3);
        return style;
    }

    private static void StyleHudButton(Button button, bool primary, bool quiet = false)
    {
        StyleButton(button, primary);
        var iconOnly = button.Icon is not null && string.IsNullOrEmpty(button.Text);
        var above = button.HasMeta("icon_above") && button.GetMeta("icon_above").AsBool();
        var compact = iconOnly || above || button.HasMeta("navigation_short");
        var normal = HudButtonStyle(primary, 0, compact, quiet);
        button.AddThemeStyleboxOverride("normal", quiet && !primary ? normal : InstrumentMaterials.Face(normal));
        button.AddThemeStyleboxOverride("hover", InstrumentMaterials.Face(HudButtonStyle(primary, 1, compact, quiet)));
        button.AddThemeStyleboxOverride("pressed", InstrumentMaterials.Face(HudButtonStyle(primary, 2, compact, quiet), inset: true));
        button.AddThemeStyleboxOverride("hover_pressed", InstrumentMaterials.Face(HudButtonStyle(primary, 2, compact, quiet), inset: true));
        button.AddThemeStyleboxOverride("disabled", InstrumentMaterials.Face(HudButtonStyle(primary, 3, compact, quiet), disabled: true));
    }

    private static void StyleCampaignNavigationButton(Button button, bool selected)
    {
        // Navigation always looks clickable, including its resting state.
        StyleHudButton(button, selected, false);
        button.AddThemeColorOverride("font_color", Ui.Colour(selected ? "button.text_hover" : "text.body_soft"));
        button.AddThemeColorOverride("icon_normal_color", Ui.Colour(selected ? "text.accent" : "text.muted"));
        button.AddThemeColorOverride("icon_hover_color", Ui.Colour("text.accent"));
        button.AddThemeColorOverride("icon_pressed_color", Ui.Colour("text.accent_dim"));
        button.AddThemeColorOverride("icon_disabled_color", Ui.Colour("button.text_disabled"));
    }

    private void BuildTopBar()
    {
        BuildFlagMount();
        var top = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        _hudTopPanel = top;
        top.Name = "CampaignTopBar";
        ApplyHudFrame(top);
        PlaceTopPlate();

        var margin = Margin(top, 12, 4);
        _hudTopStack = new VBoxContainer(); _hudTopStack.AddThemeConstantOverride("separation", 4);
        margin.AddChild(_hudTopStack);
        var row = new HBoxContainer();
        _hudTopRow = row;
        row.AddThemeConstantOverride("separation", 12);
        _hudTopStack.AddChild(row);

        // A leader's face is left to modders for now: the slot is kept and shown only when a
        // skin asks for it (hud.portrait_slot), so a mod with portraits needs no code.
        _identityPortraitSlot = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _identityPortraitSlot.Visible = Ui.Metric("hud.portrait_slot", 0f) > 0f;
        row.AddChild(_identityPortraitSlot);

        var identityBox = new StructuredMetricCell { CustomMinimumSize = new Vector2(158, 0), AccentToken = "text.accent" };
        _hudIdentityBox = identityBox;
        identityBox.AddThemeConstantOverride("separation", 2);
        identityBox.MouseFilter = MouseFilterEnum.Stop;
        identityBox.MouseDefaultCursorShape = CursorShape.PointingHand;
        identityBox.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                PlayStrategicCue("ui_select");
                OpenScreen("State Affairs");
                identityBox.AcceptEvent();
            }
        };
        row.AddChild(identityBox);
        _identity = Title("South Korea", 18, Ui.Colour("text.accent"));
        _identity.ClipText = true;
        _identity.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _identity.MouseFilter = MouseFilterEnum.Pass;
        // The bar is two lines tall and no more. A name too wide for the column has to
        // widen the column, not wrap into the line underneath it - which is what
        // "South Korea" did the moment it became "Южная Корея".
        _identity.AutowrapMode = TextServer.AutowrapMode.Off;
        identityBox.AddChild(_identity);
        _identityTag = Title("KOR · player nation", 12, Ui.Colour("text.body_soft"));
        _identityTag.ClipText = true;
        _identityTag.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _identityTag.AutowrapMode = TextServer.AutowrapMode.Off;
        _identityTag.MouseFilter = MouseFilterEnum.Pass;
        identityBox.AddChild(_identityTag);

        // DEFCON was a badge here, the door to its screen (DefconOverviewUI). It is a row of the
        // alert list under the date plate now (CampaignAlertsUI, step 4 of the interface plan),
        // ranked with the other alerts, and the plate keeps its room for the figures.

        // The resource plate: the figures that matter most, one row, and "…" at its edge for the
        // rest (BuildResourcePlate). Every figure is built here; the plate arranges them.
        var stats = new HBoxContainer { Name = "ResourcePlate", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _hudStatRow = stats;
        _hudStats.Clear();
        stats.AddThemeConstantOverride("separation", 6);
        row.AddChild(stats);
        foreach (var field in CampaignUiCatalog.TopStats)
            switch (field)
            {
                case "TREASURY":
                    _treasury = Stat(stats, field, "Cash on hand. The line below is the change over the last week of campaign time.");
                    BindStatShortcut(_treasury, () => OpenScreen("State Affairs"));
                    break;
                case "DEBT":
                    _publicDebt = Stat(stats, field, "Borrowed principal, separate from cash. Open sovereign finance to inspect interest, overdue bills and credit access.");
                    BindDebtHeadline();
                    break;
                case "BALANCE":
                    _balance = Stat(stats, field, "What the treasury gains or loses each week, and everything that goes into it.", () => _campaign?.Player?.Budget);
                    BindStatShortcut(_balance, () => OpenScreen("State Affairs"));
                    break;
                case "GDP":
                    _gdp = Stat(stats, field, "Annual gross domestic product, as it emerges from what your economy actually produces.");
                    BindStatShortcut(_gdp, () => OpenScreen("Economy"));
                    break;
                case "POPULATION":
                    _population = Stat(stats, field, "Everyone living in your states, and how that has moved over the last week.", () => _campaign?.Player is { } p ? HouseholdEconomy.ExplainNationPopulation(_campaign, p) : null);
                    BindStatShortcut(_population, () => OpenScreen("Nation"));
                    break;
                case "STABILITY":
                    _stability = Stat(stats, field, "How settled the country is. Unrest costs money to police and output to bear.", () => _campaign?.Player?.StabilityDrivers);
                    BindStatShortcut(_stability, () => OpenScreen("Laws"));
                    break;
                case "MANPOWER":
                    _manpower = Stat(stats, field, "People left to put in uniform. A war that outruns this is a war that ends.");
                    BindStatShortcut(_manpower, () => OpenRecruitment());
                    break;
                // What the army has learned, and what the government can push through: the
                // currencies of the war doctrines (MilitaryExperience, TacticalDoctrines).
                case "ARMY XP":
                    _armyExperience = Stat(stats, field, "What the army has learned from fighting, drilling and being funded. Its line below is the price of the next article open to it.");
                    BindStatShortcut(_armyExperience, OpenDoctrineScreen);
                    break;
                case "AIR XP":
                    _airExperience = Stat(stats, field, "What the air service has learned from flying real missions. It is what a doctrine of the sky is paid for in.");
                    BindStatShortcut(_airExperience, OpenDoctrineScreen);
                    break;
                case "NAVY XP":
                    _navyExperience = Stat(stats, field, "What the fleet has learned from sea time and action. It is what a doctrine of the coast is paid for in.");
                    BindStatShortcut(_navyExperience, OpenDoctrineScreen);
                    break;
                case "POLITICAL CAPITAL":
                    _politicalPoints = Stat(stats, field, "What a government can push through besides money: rewriting a doctrine, or forcing a reform through the staff. Its line below is the weekly gain.");
                    BindStatShortcut(_politicalPoints, () => OpenScreen("Laws"));
                    break;
                // Hover for the reasons, the same as stability: a rate with nothing behind
                // it cannot answer the question a player actually asks about it.
                case "INFLATION":
                    _inflation = Stat(stats, field, "How fast prices are rising. Caused by what the government spends, what the market cannot supply and what the war costs; answered with the policy rate in the budget.", () => _campaign?.Player?.InflationDrivers);
                    BindStatShortcut(_inflation, () => OpenScreen("State Affairs"));
                    break;
            }
        BuildResourcePlate();

        // Short captions, or the week's sentence under each figure. The tooltip names
        // which form the click opens. Reading either form does not write the campaign.
        // It stands at the end of the figures it changes, not among the clock's buttons, where a
        // clipped caption left a thin empty button the owner kept asking about.
        _hudFiguresButton = ActionButton("hud.figures.brief", ToggleHudFigures, 0, 28);
        _hudFiguresButton.Name = "HudFiguresForm";
        _hudFiguresButton.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        StyleHudButton(_hudFiguresButton, false);
        _hudStatRow.AddChild(_hudFiguresButton);

        // The clock and its buttons have a plate of their own at the top right (DatePlateUI, step 3
        // of the interface plan). This bar now carries the country and its figures and nothing else,
        // which is what lets all twelve stand in one row at 1920.
        BuildDatePlate();
        var window = GetWindow();
        window.SizeChanged += LayoutCampaignShell;
        TreeExiting += () => window.SizeChanged -= LayoutCampaignShell;
    }

    private void RefreshTopBar(Nation player)
    {
        // The week and the half-year behind the figures belong to one country. A hotseat pass or a
        // network seat change hands the bar to another on the same day, and without this the
        // "week's change" would be country B today minus country A last week.
        if (!ReferenceEquals(_trendsNation, player))
        {
            ResetTrends();
            _trendsNation = player;
        }
        var day = _campaign.ElapsedDays;
        if (player is null)
        {
            RefreshNullPlayerTopBar();
            return;
        }
        _treasuryTrend.Sample(day, player.Treasury);
        _gdpTrend.Sample(day, player.Gdp);
        _populationTrend.Sample(day, player.Population);
        _stabilityTrend.Sample(day, player.Stability);
        _manpowerTrend.Sample(day, player.ManpowerReserve);
        if (IsNewShellActive) return;

        _identity.Text = CountryNames.Compact(player);
        _identity.TooltipText = Ui.Tip(player.DisplayName);
        var identityFont = _identity.GetThemeFont("font");
        var identitySize = 18;
        while (identitySize > 12 && identityFont.GetStringSize(_identity.Text, HorizontalAlignment.Left, -1, identitySize).X >
               System.Math.Max(154, _identity.Size.X)) identitySize--;
        _identity.AddThemeFontSizeOverride("font_size", identitySize);
        // No tag for nobody: "OBS" read in Russian as "НАБ", a word nobody could place.
        var leader = Rulers.HeadOfState(_campaign, player);
        _identityTag.Text = WatchingWorld
            ? Tr("Observer mode")
            : _observerMode
                ? Loc.Format("Watching · {0}", string.IsNullOrEmpty(leader.Name) ? Tr("run by the computer") : leader.Name)
            : !string.IsNullOrEmpty(leader.Name)
                ? leader.Name
                : Tr("player nation");
        if (SettingsCatalog.Flag("show_country_tags")) _identityTag.Text = player.Tag + " · " + _identityTag.Text;
        _identityTag.TooltipText = _observerMode ? "" : Ui.Tip($"{Tr(leader.Title)}: {leader.Name}\n{Tr("Trait")}: {Tr(leader.TraitName)}\n" +
            Loc.Format("Country code (saves and mods): {0}", player.Tag));
        _identity.TooltipText = Ui.Tip(player.DisplayName + "\n" + Loc.Format("Country code (saves and mods): {0}", player.Tag));
        SetFlag(_identityFlag, player.Tag, MountFlagHeight(player.Tag));
        RefreshCountryRankBadges();
        if (_identityPortraitSlot is { Visible: true })
        {
            var portraitKey = _observerMode ? "obs" : $"{player.Tag}:{leader.Name}:{leader.Age}:{player.Color}";
            if (portraitKey != _identityPortraitLeaderKey)
            {
                _identityPortraitLeaderKey = portraitKey;
                ClearRuntimeChildren(_identityPortraitSlot);
                if (!_observerMode && !string.IsNullOrEmpty(leader.Name))
                {
                    var badge = BuildLeaderPortraitBadge(leader, player.Color, 34, 42);
                    badge.TooltipText = _identityTag.TooltipText;
                    _identityPortraitSlot.AddChild(badge);
                }
            }
        }
        RefreshDatePlate(player);

        // The treasury's line is what the week does to it - the weekly balance, the number a player
        // acts on - and the bare movement of the last seven days only until the first week has closed.
        var balanced = player.Budget.HasEntries;
        if (balanced)
            WriteStat(_treasury, Fmt.Money(player.Treasury), Fmt.MoneyExact(player.Treasury),
                Simulation.Loc.Format("{0} a week", Fmt.MoneyDelta(player.WeeklyBalance)),
                Fmt.Direction(player.WeeklyBalance, 0.005d),
                Simulation.Loc.Format("{0}/week", Fmt.MoneyDelta(player.WeeklyBalance)));
        else
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
        // Under stability and manpower, the week's change first - the plate's line is the week -
        // and what the line used to say (war support, the daily replacements) after it while the
        // long form has the room; the short form keeps the week alone.
        var settling = (float)_stabilityTrend.Delta;
        var support = player.WarSupport > 0f ? Simulation.Loc.Format("war support {0}", Fmt.Percent(player.WarSupport)) : "";
        if (MathF.Abs(settling) >= 0.0005f)
            WriteStat(_stability, Fmt.Percent(player.Stability),
                Simulation.Loc.Format("Stability {0:P1}", player.Stability),
                Simulation.Loc.Format("{0} a week", Fmt.PercentDelta(settling)) + (support.Length > 0 ? " · " + support : ""),
                Fmt.Direction(settling, 0.0005d),
                Simulation.Loc.Format("{0}/week", Fmt.PercentDelta(settling)));
        else
            WriteStat(_stability, Fmt.Percent(player.Stability),
                Simulation.Loc.Format("Stability {0:P1}", player.Stability),
                support, 0, Simulation.Loc.Format("War sup. {0}", Fmt.Percent(player.WarSupport)));
        var drafted = _manpowerTrend.Delta;
        var replacements = Simulation.Loc.Format("{0}/day", Fmt.People(player.ReinforcementCapacity));
        if (Math.Abs(drafted) >= 1d)
            WriteStat(_manpower, Fmt.People(player.ManpowerReserve), Fmt.PeopleExact((long)player.ManpowerReserve),
                Simulation.Loc.Format("{0} a week", Fmt.PeopleDelta(drafted)) + " · " + replacements,
                Fmt.Direction(drafted, 1d), Simulation.Loc.Format("{0}/week", Fmt.PeopleDelta(drafted)));
        else
            WriteStat(_manpower, Fmt.People(player.ManpowerReserve), Fmt.PeopleExact((long)player.ManpowerReserve),
                replacements, 0);

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
        _politicalPoints?.Spark?.Push(day, player.PoliticalPoints);
        _publicDebt?.Spark?.Push(day, SovereignFinance.Outstanding(player));
        if (WatchingWorld) WriteObserverTopBar();
        else WriteMovementTopBar(player);
        // After the modes have borrowed their places: a figure saying something else has no state,
        // and a borrowed figure has to stand on the plate whatever the data put there.
        PaintResourceStates(player);
        ArrangeResourcePlate();
        if (_hudFlagMount is not null) _hudFlagMount.Visible = !IsNewShellActive && _identityFlag is { Visible: true };
        foreach (var stat in _hudStats) FitHudStat(stat);
        NoteFigureTips();

        _militarySpending.SetValueNoSignal(player.MilitarySpending);
        UpdateSpendingReadout(player);
        UpdateSpeedButtons();
        UpdateScreenButtons();
        if (ResolveHudFigures())
        {
            PlaceHudChrome();
            _strategicHudFingerprint = int.MinValue;
        }
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
        internal string Exact = "";
        internal string FullDelta = "";
        internal string ShortDelta = "";
        /// <summary>Tooltip for the movement line when it already shows a war-support percent.</summary>
        internal string DeltaHover = "";
        internal ExplainedStat Box;
        /// <summary>Half a year of the figure under it (Sparkline).</summary>
        internal Sparkline Spark;
        /// <summary>Where the figure stands against its thresholds (HudResourcePlate), painted on number and thread.</summary>
        internal HudResourcePlate.State State;
    }

    /// <summary>
    /// The cheapest article one of the country's books has open right now: the number a player
    /// needs under a service's experience to know what it is saving for. Nought when nothing is
    /// open on that shelf.
    /// </summary>
    private float NextArticlePrice(Nation player) => NextArticlePrice(player, DoctrineSlots.Ground);

    private float NextArticlePrice(Nation player, string slot)
    {
        if (!_campaign.FeatureEnabled("tactical_doctrines")) return 0f;
        var book = TacticalDoctrines.BookOn(player, slot);
        var branch = book?.Id ?? TacticalDoctrines.PreferredBranch(_campaign, player, slot);
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

    private void BindStatShortcut(StatReadout stat, System.Action open)
    {
        if (stat?.Box is null || open is null) return;
        stat.Box.FocusMode = FocusModeEnum.All;
        stat.Box.MouseDefaultCursorShape = CursorShape.PointingHand;
        stat.Box.SizeFlagsVertical = SizeFlags.ExpandFill;
        // One hit target for the whole instrument, including caption, blank
        // padding and sparkline. A nested Control must not swallow its click.
        foreach (var child in stat.Box.FindChildren("*", "Control", true, false))
            if (child is Control control) control.MouseFilter = MouseFilterEnum.Ignore;
        stat.Box.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } ||
                input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter })
            {
                PlayStrategicCue("ui_select");
                open();
                stat.Box.AcceptEvent();
            }
        };
        stat.Box.FocusEntered += () => stat.Caption.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
        stat.Box.FocusExited += () => stat.Caption.AddThemeColorOverride("font_color", Ui.Colour("text.caption"));
    }

    private StatReadout Stat(Container parent, string caption, string tooltip,
        System.Func<Simulation.Breakdown> explain = null)
    {
        // A figure that can be hovered for the parts it is made of, when there are parts to
        // show; an ordinary tooltip when there are not.
        var box = new ExplainedStat
        {
            Name = "HudStat_" + caption.Replace(' ', '_'),
            // One width for every cell of the plate, so the plate is a row of equal instruments
            // rather than a strip stretched to whatever room the bar has left.
            CustomMinimumSize = new Vector2(ResourceCellWidth(false), 0),
            SizeFlagsHorizontal = SizeFlags.Fill,
            ClipContents = true,
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
        heading.AddThemeConstantOverride("separation", 4);
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
                Modulate = Ui.Colour("text.muted")
            };
            glyph.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            heading.AddChild(glyph);
        }
        var statCaption = Title(caption, 10, Ui.Colour("text.caption"), upper: true);
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
            Value = Title("—", 18, Ui.Colour("text.bright")),
            Delta = Title("", 11, Ui.Colour("text.faint"))
        };
        // Neither of these may wrap. The bar is one row tall and the column is sized to the
        // widest thing in it, so a line that wraps grows downwards out of the bar instead of
        // widening its column - which is what "war support 63 %" did the moment it became
        // "поддержка войны 63 %".
        readout.Value.AutowrapMode = TextServer.AutowrapMode.Off;
        readout.Delta.AutowrapMode = TextServer.AutowrapMode.Off;
        readout.Value.ClipText = true;
        readout.Value.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        readout.Delta.ClipText = true;
        readout.Delta.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        readout.Value.AddThemeFontOverride("font", PresentationFonts.Tabular());
        readout.Delta.AddThemeFontOverride("font", PresentationFonts.Tabular());
        box.AddChild(readout.Value);
        // The week's movement and, under it, the half-year it belongs to: a thread the width of
        // the cell, in the colour of the figure's state. It keeps its room before it has a
        // history, so the plate does not grow a line taller in the second week.
        var movement = new VBoxContainer { Name = "StatMovement", MouseFilter = MouseFilterEnum.Ignore };
        movement.AddThemeConstantOverride("separation", 1);
        readout.Delta.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        movement.AddChild(readout.Delta);
        readout.Spark = new Sparkline { Name = "StatSpark", Flash = readout.Value,
            HigherIsBetter = caption is not ("INFLATION" or "DEBT"), KeepRoom = true,
            StateColour = Ui.Colour("text.muted") };
        readout.Spark.CustomMinimumSize = new Vector2(0, Mathf.Clamp(Ui.Metric("hud.spark_height", 6f), 4f, 24f));
        readout.Spark.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        readout.Spark.SizeFlagsVertical = SizeFlags.Fill;
        movement.AddChild(readout.Spark);
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
        // Figures are set in the tabular face, so a column of them lines up digit for digit and
        // a changing number does not shift the labels beside it.
        stat.Value.AddThemeFontOverride("font", Ui.NumberFont);
        stat.Delta.AddThemeFontOverride("font", Ui.NumberFont);
        stat.Value.Text = compact;
        stat.Exact = exact ?? "";
        stat.Value.TooltipText = Ui.Tip(stat.Exact);
        stat.Value.MouseFilter = MouseFilterEnum.Ignore;
        stat.FullDelta = delta;
        stat.ShortDelta = shortDelta ?? delta;
        stat.Delta.TooltipText = Ui.Tip(delta);
        stat.Delta.MouseFilter = MouseFilterEnum.Ignore;
        stat.Box.Plain = Tr(stat.CountryCaption) + "\n" + stat.Exact +
            (string.IsNullOrEmpty(delta) ? "" : "\n" + delta);
        stat.Box.TooltipText = Ui.Tip(stat.Box.Plain);
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

    // The number first: it shrinks before anything is cut. The week's line chooses its long or
    // short form from the whole cell width, not the previous layout's, to avoid resize
    // oscillation. The half-year thread has a line of its own under it and competes with nothing.
    private static void FitHudStat(StatReadout stat)
    {
        if (stat?.Box is null || !GodotObject.IsInstanceValid(stat.Box)) return;
        var width = stat.Box.Size.X - 4; // the cell's recessed well is drawn two pixels in on each side
        if (width <= 1) return; // Resized runs again after the first real layout.
        var caption = Tr(stat.CaptionKey);
        var shortKey = "hud.stat.short." + stat.CaptionKey;
        var shortCaption = Simulation.Loc.Has(shortKey) ? Tr(shortKey) : caption;
        var headingWidth = width - (stat.Glyph is { Visible: true } ? 21 : 0);
        FitHudLine(stat.Caption, caption, shortCaption, headingWidth, 10, 10);
        FitHudLine(stat.Value, stat.Value.Text, stat.Value.Text, width, 18, 11);
        var full = stat.FullDelta;
        var font = stat.Delta.GetThemeFont("font");
        var shown = font.GetStringSize(full, HorizontalAlignment.Left, -1, 11).X <= width ? full : stat.ShortDelta;
        stat.Spark.AllowLayout(true);
        FitHudLine(stat.Delta, shown, shown, width, 11, 10);
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
        _stabilityTrend.Reset();
        _manpowerTrend.Reset();
        foreach (var stat in new[] { _treasury, _gdp, _population, _balance, _stability, _manpower, _inflation,
                     _politicalPoints, _publicDebt })
            stat?.Spark?.Reset();
    }

    /// <summary>The screen currently open, highlighted in the navigation rail.</summary>
    private void UpdateScreenButtons()
    {
        if (IsNewShellActive)
        {
            if (_newShellBuilt && _shell is not null)
            {
                SyncNewShellModalState();
                SyncNewShellLeftAndBottomDock();
                _shell.Corner.Refresh();
            }
            return;
        }
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
            StyleCampaignNavigationButton(button, screen == open);
        }
        RefreshMapVisualToggles();
    }
}
