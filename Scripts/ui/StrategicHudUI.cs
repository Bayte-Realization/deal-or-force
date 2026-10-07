using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _outliner = null!;
    private VBoxContainer _outlinerRows = null!;
    private VBoxContainer _alertRows = null!;
    private VBoxContainer _outlinerGroupRows;
    private ScrollContainer _outlinerScroll;
    private readonly HashSet<string> _collapsedOutlinerGroups = new();
    private VBoxContainer _battleDetailControls = null!;
    private int _selectedBattleId = -1;
    private int _strategicHudFingerprint = int.MinValue;
    private bool _outlinerCollapsed;

    private void BuildStrategicHud()
    {
        _outliner = TexturedPanel(_hudLayer, PanelSurface.Command);
        _outliner.Name = "StrategicOutliner";
        Ui.Place(_outliner, "hud.outliner");
        // One scrollable rail keeps alerts and dossiers inside the available viewport.
        var outer = Margin(_outliner, 10, 8);
        outer.ClipContents = true;
        var column = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        column.AddThemeConstantOverride("separation", 6);
        outer.AddChild(column);
        var toggle = ActionButton("Situation report  −", ToggleOutliner, 0, 30);
        toggle.Name = "OutlinerToggle";
        toggle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        toggle.ClipText = true;
        column.AddChild(toggle);
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true,
            ClipContents = true
        };
        scroll.Name = "OutlinerScroll";
        _outlinerScroll = scroll;
        column.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);
        _alertRows = new VBoxContainer { Name = "StrategicAlerts" };
        _alertRows.AddThemeConstantOverride("separation", 4);
        body.AddChild(_alertRows);
        _outlinerRows = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = Vector2.Zero,
            ClipContents = true
        };
        _outlinerRows.AddThemeConstantOverride("separation", 4);
        body.AddChild(_outlinerRows);
    }

    private void ToggleOutliner()
    {
        _outlinerCollapsed = !_outlinerCollapsed;
        _outlinerScroll.Visible = !_outlinerCollapsed;
        LayoutCampaignShell();
        var toggle = _outliner.FindChild("OutlinerToggle", true, false) as Button;
        if (toggle is not null) toggle.Text = Tr("Situation report") + (_outlinerCollapsed ? "  +" : "  −");
    }

    private void RefreshStrategicHud()
    {
        if (_campaign?.Player is null || _outlinerRows is null) return;
        var player = _campaign.Player;
        var playerBattles = _campaign.Battles.Count(battle => !battle.Ended &&
            (battle.AttackerId == player.Id || battle.DefenderId == player.Id));
        var availableDecisions = DecisionCatalog.All.Count(definition =>
            DecisionCatalog.BlockReason(_campaign, player, definition).Length == 0);
        var fingerprint = HashCode.Combine(
            _campaign.ElapsedDays,
            _campaign.MapRevision,
            player.Id,
            _campaign.Wars.Count(war => !war.Ended && war.Involves(player.Id)),
            playerBattles,
            _campaign.Construction.Count(project => project.NationId == player.Id),
            _campaign.PendingEvents.Count,
            (availableDecisions, _campaign.FeatureEnabled("navy"), _campaign.FeatureEnabled("construction"), _campaign.Chronicle.Entries.Count == 0
                ? 0
                : _campaign.Chronicle.Entries[^1].Sequence));
        if (fingerprint == _strategicHudFingerprint) return;
        _strategicHudFingerprint = fingerprint;
        if (_outliner.FindChild("OutlinerToggle", true, false) is Button toggle)
            toggle.Text = Tr("Situation report") + (_outlinerCollapsed ? "  +" : "  −");

        ClearRuntimeChildren(_alertRows);
        ClearRuntimeChildren(_outlinerRows);
        _pendingOutlinerHeader = null;
        _outlinerHasRows = false;

        var armies = _campaign.Armies.Where(army => army.NationId == player.Id).ToArray();
        var lowSupply = armies.Count(army => army.SupplyLevel < 0.45f || army.CutOff);
        var construction = _campaign.Construction.Where(project => project.NationId == player.Id).ToArray();

        // Every alert is a level, an icon, a count and a sentence kept for the tooltip. They
        // are sorted before any of them is drawn, so the worst news is always at the top and
        // a new emergency pushes the rest down where the movement is seen before it is read.
        if (_campaign.PendingEvents.Count > 0)
            AddAlert(AlertLevel.Critical, "alert_event", Tr("Event"), () => RefreshEventInterface(),
                Tr("A campaign event is waiting for a decision."));

        var peaceOffers = PeaceOffers.AwaitingAnswer(_campaign, player.Id).Count;
        if (peaceOffers > 0)
            AddAlert(AlertLevel.Critical, "alert_peace",
                Simulation.Loc.Format("Peace offer: {0}", peaceOffers), () => RefreshPeaceOfferInterface(),
                Tr("Another government has put a settlement to you and is waiting on an answer."));

        // The army is the one opponent a player cannot see on the map.
        if (player.CoupPlotDay >= 0)
            AddAlert(AlertLevel.Critical, "unrest", Tr("Coup plot"), () => OpenScreen("Nation"),
                Simulation.Loc.Format("Officers are plotting against your government. Raise stability above {0} within {1} days - spending, laws, a war ended - or the army takes power.",
                    Fmt.Percent(_campaign.Defines.CoupStabilityThreshold),
                    Math.Max(0, _campaign.Defines.CoupPlotDays - (_campaign.ElapsedDays - player.CoupPlotDay))));
        else if (Simulation.Coups.Outlook(_campaign, player) == "high")
            AddAlert(AlertLevel.Warning, "unrest", Tr("Coup risk"), () => OpenScreen("Nation"),
                Simulation.Loc.Format("Stability is low enough for officers to start plotting. Above {0} nobody plots.",
                    Fmt.Percent(_campaign.Defines.CoupStabilityThreshold)));

        // The standoff likeliest to become our next war, while it is still a standoff.
        var standoff = Simulation.Escalation.Hottest(_campaign, _campaign.Defines.TensionCrisisAt)
            .FirstOrDefault(relation => (relation.A == player.Id || relation.B == player.Id) &&
                                        !Simulation.CampaignDiplomacy.AtWar(_campaign, relation.A, relation.B));
        if (standoff is not null && _campaign.NationById(standoff.A == player.Id ? standoff.B : standoff.A) is { } rival)
        {
            var clashing = (Simulation.TensionStage)standoff.TensionStage >= Simulation.TensionStage.Clashes;
            AddAlert(clashing ? AlertLevel.Critical : AlertLevel.Warning, "unrest",
                Simulation.Loc.Format(clashing ? "Clashes with {0}" : "Crisis with {0}", CountryNames.Compact(rival)),
                () => { _conflictTab = 2; OpenConflictsScreen(); },
                clashing
                    ? Tr("Shooting on the border every week. A border that keeps shooting for months can become a border war; talks in the Conflicts screen cool it.")
                    : Tr("A crisis between us. It can grow into armed clashes; talks in the Conflicts screen cool it."));
        }

        if (player.ShortageIndex > 0.10f)
            AddAlert(AlertLevel.Critical, "trade",
                Simulation.Loc.Format("Shortages: {0}", Fmt.Percent(player.ShortageIndex)),
                () => OpenEconomyTab("Market"),
                Tr("Essential goods are in shortage. Open Economy for production, demand, imports and prices."));

        if (lowSupply > 0)
            AddAlert(AlertLevel.Warning, "alert_supply",
                Simulation.Loc.Format("Out of supply: {0}", lowSupply),
                () => FocusArmy(armies.First(army => army.SupplyLevel < 0.45f || army.CutOff)),
                Tr("Formations are undersupplied or cut off."));

        if (playerBattles > 0)
            AddAlert(AlertLevel.Warning, "alert_battle",
                Simulation.Loc.Format("Battles: {0}", playerBattles), OpenFirstPlayerBattle,
                Tr("Active battles involving your country."));

        // Prices are the one number on this rail that is a slow emergency rather than a
        // fast one, and it belongs here for the same reason the shortage does: by the time
        // a player goes looking for it in the economy screen it has already cost them.
        if (player.Inflation > Simulation.InflationCore.Weights.Comfort * 2f)
            AddAlert(AlertLevel.Warning, "inflation",
                Simulation.Loc.Format("Inflation: {0}", player.Inflation.ToString("P0")),
                () => OpenEconomyTab("Budget"),
                Tr("Prices are rising well past the comfortable rate. The policy rate in the budget is the lever."));

        if (construction.Length == 0 && _campaign.FeatureEnabled("construction"))
            AddAlert(AlertLevel.Notice, "alert_construction", Tr("Nothing being built"),
                () => OpenEconomyTab("Construction"), Tr("Construction capacity is idle."));

        if (availableDecisions > 0)
            AddAlert(AlertLevel.Chance, "decision",
                Simulation.Loc.Format("Decisions: {0}", availableDecisions), () => OpenScreen("Decisions"),
                Tr("National decisions are available."));

        FlushAlerts();

        AddFirstStepsToOutliner();

        AddOutlinerHeader("WORLD");
        // This rail is rebuilt, not appended. Read a bounded snapshot rather than a delta:
        // otherwise collapsing a group or changing language erases the already read news.
        // What matters, not everything: aid sent and buildings finished stay in the reports.
        foreach (var line in NewsFeed.Headlines(_campaign,
                     (int)Math.Clamp(InterfaceSkin.Metric("news.feed_rows", 6), 1, 20)))
        {
            var actor = line.ActorId > 0 ? _campaign.NationById(line.ActorId) : null;
            NewsFeed.Line captured = line;
            AddOutlinerButton(line.Text, actor is null ? null : () =>
                {
                    _selectedNation = actor;
                    OpenDiplomacyScreen();
                },
                line.Text + System.Environment.NewLine + Simulation.Loc.Format("Campaign day {0}", captured.Day));
            if (_outlinerGroupRows.GetChild(_outlinerGroupRows.GetChildCount() - 1) is Button news)
            {
                news.Name = "WorldNews_" + line.Sequence;
                news.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                news.AddThemeFontSizeOverride("font_size", 12);
                news.CustomMinimumSize = new Vector2(0, 48);
            }
        }

        AddOutlinerHeader("WARS");
        var wars = _campaign.Wars.Where(war => !war.Ended && war.Involves(player.Id)).ToArray();
        foreach (var war in wars)
        {
            var enemy = _campaign.NationById(war.AttackerId == player.Id ? war.DefenderId : war.AttackerId);
            var score = war.AttackerId == player.Id ? war.WarScore : -war.WarScore;
            AddOutlinerButton(Simulation.Loc.Format("{0}  score {1:+0;-0;0}  support {2:P0}",
                    enemy?.Tag ?? "?", score, PlayerWarSupport(war)),
                () => FocusWar(war), "Open the war dossier and focus its frontline.");
        }

        AddOutlinerHeader("BATTLES");
        var battles = _campaign.Battles.Where(battle => !battle.Ended &&
                (battle.AttackerId == player.Id || battle.DefenderId == player.Id))
            .OrderByDescending(battle => battle.StartedDay).Take(6).ToArray();
        foreach (var battle in battles)
        {
            var enemyId = battle.AttackerId == player.Id ? battle.DefenderId : battle.AttackerId;
            var enemy = _campaign.NationById(enemyId);
            // Where the fight is, in words: a province number is a coordinate, and a place
            // is the town it is at - four battles in one region used to read as the same
            // line four times - or the region when no town of it is near.
            var assessment = BattleReadout.Assess(battle, _campaign.Defines.BattleBreakOrganisation);
            var attacking = battle.AttackerId == player.Id;
            var indicator = assessment.Known ? $"{assessment.ShareFor(attacking):P0}" : "—";
            var battleRow = AddOutlinerButton(Simulation.Loc.Format("{0} · {1} · {2}",
                    BattlePlaceName(battle.ProvinceId), enemy?.Tag ?? "?", indicator),
                () => ShowBattleDetails(battle), "Open the battle report and focus the engagement.");
            battleRow.Name = "BattleOutliner_" + battle.Id;
            battleRow.TooltipText = Ui.Tip(Tr(assessment.StatusFor(attacking)) + "\n" + Tr(BattleBalanceExplanation));
            battleRow.AddThemeColorOverride("font_color", Ui.Colour(!assessment.Known ? "text.muted" :
                assessment.ShareFor(attacking) > .55f ? "state.positive" :
                assessment.ShareFor(attacking) < .45f ? "state.negative" : "state.warning"));
        }

        // The navy, in full. The armies list was dropped because it showed seven divisions
        // of however many a country has, which is not a list of anything - but a country has
        // one or two fleets, so this one is complete, and hunting a counter across an ocean
        // is no way to find your own navy. Every Paradox game has this list for that reason.
        var fleets = _campaign.Fleets.Where(fleet => fleet.NationId == player.Id)
            .OrderByDescending(fleet => fleet.Hulls).ToArray();
        if (fleets.Length > 0 && _campaign.FeatureEnabled("navy"))
        {
            AddOutlinerHeader("NAVY");
            foreach (var fleet in fleets)
            {
                var state = fleet.InAction ? Tr("IN ACTION")
                    : fleet.IsMoving ? "→"
                    : MissionName(fleet.Mission).ToUpperInvariant();
                AddOutlinerButton($"{Loc.Get(fleet.Name)}  {state}  {fleet.Hulls:N0}", () => FocusFleet(fleet),
                    Simulation.Loc.Format(
                        "{0:N0} hulls · strength {1:P0} · cohesion {2:P0} · stores {3:P0}",
                        fleet.Hulls, fleet.Strength, fleet.Organisation, fleet.Supply) +
                    System.Environment.NewLine + MissionHelp(fleet.Mission));
            }
        }

        var watched = player.Market.Values.Where(quote => PlayerSettings.Bool("watch.good." + quote.Commodity, false)).ToArray();
        if (watched.Length > 0)
        {
            AddOutlinerHeader("WATCHLIST");
            foreach (var quote in watched)
            {
                var good = quote.Commodity;
                AddOutlinerButton($"{Tr(CommodityCatalog.Get(good).Name)} · {quote.Satisfaction:P0}", () =>
                {
                    OpenScreen("Economy");
                    _marketSearch.Text = Tr(CommodityCatalog.Get(good).Name);
                    _shortagesOnly.ButtonPressed = false;
                    SelectDossierTab("Market");
                }, Simulation.Loc.Format("Supply {0:P0}", quote.Satisfaction));
            }
        }
        AddOutlinerHeader("MARKET");
        var worstGood = player.Market.Values.OrderBy(quote => quote.Satisfaction).FirstOrDefault();
        if (worstGood is not null)
        {
            var definition = CommodityCatalog.Get(worstGood.Commodity);
            // The name and how short we are, and nothing else - the same shape as a watchlist
            // row above. The price used to sit on the end of this line and it is what a longer
            // language cut off first: "Rare earths / 65 % / $145M" arrived in Russian as
            // "... / 65 % / $14", a price that is wrong rather than missing. It reads in the
            // tooltip with everything else about the good, where nothing is fighting for room.
            AddOutlinerButton($"{Tr(definition.Name)} · {worstGood.Satisfaction:P0}",
                () => OpenEconomyTab("Market"),
                Simulation.Loc.Format("Price {0}", Fmt.Money(worstGood.Price)) +
                System.Environment.NewLine + Simulation.Loc.Format(
                    "Production {0:0.0}, demand {1:0.0}, imports {2:0.0}, exports {3:0.0}",
                    worstGood.Production, worstGood.Demand, worstGood.Imports, worstGood.Exports));
        }

        // One row a kind of building, not one a site: five "Civilian industry · 0 %" rows in a
        // column read as the same row five times. The count, the furthest along of them, and
        // every site in the tooltip; a click opens the furthest along.
        AddOutlinerHeader("CONSTRUCTION");
        foreach (var kind in construction.GroupBy(project => project.Building)
                     .OrderByDescending(group => group.Max(project => project.Progress))
                     .ThenBy(group => group.Min(project => project.Id)))
        {
            var lead = kind.OrderByDescending(project => project.Progress).ThenBy(project => project.Id).First();
            var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == lead.StateMapIndex);
            var count = kind.Count();
            var name = Tr(BuildingCatalog.Get(lead.Building).Name);
            var sites = string.Join(System.Environment.NewLine, kind.OrderBy(project => project.Id).Select(project =>
                $"{_campaign.States.FirstOrDefault(candidate => candidate.MapIndex == project.StateMapIndex)?.DisplayName ?? Tr("Construction project")} · {Fmt.Percent(project.Progress)}"));
            AddOutlinerButton(count > 1 ? $"{name} ×{count} · {Fmt.Percent(lead.Progress)}" : $"{name} · {Fmt.Percent(lead.Progress)}", () =>
            {
                _selectedState = state;
                OpenScreen("Economy");
                SelectDossierTab("Construction");
            }, sites);
        }

        // One line where there used to be four. A country at peace, with no battles, no
        // market trouble and nothing on the stocks used to be told so four separate times
        // under four separate headings; it is told once, quietly, and the rail stays empty
        // for the news that matters.
        if (!_outlinerHasRows && !_alertsDrawn)
        {
            _pendingOutlinerHeader = null;
            var quiet = Title(Tr("Nothing needs attention."), 12, Ui.Colour("text.faint"));
            quiet.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            quiet.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _outlinerRows.AddChild(quiet);
        }
        // Once the rows have been laid out and know how tall they are.
        Callable.From(FitOutliner).CallDeferred();
    }

    private float PlayerWarSupport(StrategicWar war) =>
        war.AttackerId == _campaign.Player.Id ? war.AttackerWarSupport : war.DefenderWarSupport;

    /// <summary>
    /// How much an alert matters. The order is the order they appear in, because the one
    /// thing an alert rail has to get right is that the worst news is at the top.
    /// </summary>
    private enum AlertLevel
    {
        Critical,
        Warning,
        Notice,
        Chance
    }

    private readonly List<(AlertLevel Level, string Icon, string Text, string Tooltip, Action Action)>
        _pendingAlerts = new();

    /// <summary>
    /// Remember an alert. Nothing is drawn until <see cref="FlushAlerts"/>, because they
    /// have to be sorted before any of them can be placed.
    /// </summary>
    private void AddAlert(AlertLevel level, string icon, string text, Action action, string tooltip) =>
        _pendingAlerts.Add((level, icon, text, tooltip, action));

    /// <summary>The colour of each level. Named in the skin, so a reskin moves them together.</summary>
    private static string AlertColour(AlertLevel level) => level switch
    {
        AlertLevel.Critical => "state.critical",
        AlertLevel.Warning => "state.warning",
        AlertLevel.Notice => "state.info",
        _ => "state.positive"
    };

    /// <summary>
    /// Draw them, worst first.
    ///
    /// Each is an icon, a very short line, and a border in its level's colour; the sentence
    /// explaining it is in the tooltip. A tooltip is a trade - screen space for a click -
    /// and it is worth making here precisely because this rail is the one part of the
    /// screen that is always short of room.
    /// </summary>
    private void FlushAlerts()
    {
        _alertsDrawn = _pendingAlerts.Count > 0;
        _pendingAlerts.Sort((left, right) => left.Level.CompareTo(right.Level));
        foreach (var alert in _pendingAlerts)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _alertRows.AddChild(row);

            var button = ActionButton(alert.Text, alert.Action, 0, 30,
                alert.Level == AlertLevel.Critical,
                SpriteCatalog.TextureOf($"icon_{alert.Icon}",
                    $"res://game/gfx/interface/icons/icon_{alert.Icon}.png"));
            button.Alignment = HorizontalAlignment.Left;
            button.ClipText = true;
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.TooltipText = Ui.Tip(alert.Tooltip);

            // The severity, in the one place a player's eye is already going: the edge of
            // the thing they are reading.
            var colour = Ui.Colour(AlertColour(alert.Level));
            button.AddThemeColorOverride("font_color", colour);
            button.AddThemeColorOverride("font_hover_color", colour.Lightened(0.25f));
            row.AddChild(button);
        }
        _pendingAlerts.Clear();
    }

    /// <summary>
    /// The heading this group would have, if anything turns up to go under it.
    ///
    /// Buffered rather than drawn, because a heading with nothing beneath it is two lines of
    /// the narrowest column on the screen spent saying that there is no news - and the alert
    /// rail directly above exists to say when there is. NAVY and WATCHLIST already worked
    /// this way by writing the condition out by hand at each call site; this is the same
    /// rule, once, for all of them.
    /// </summary>
    private string _pendingOutlinerHeader;

    /// <summary>Whether anything at all has been listed, so the empty case can say so once.</summary>
    private bool _outlinerHasRows;

    /// <summary>
    /// And whether the alert rail above it drew anything. Recorded rather than counted,
    /// because the alerts are flushed before the groups below are built and the list they
    /// came from is empty by then.
    /// </summary>
    private bool _alertsDrawn;

    private void AddOutlinerHeader(string text) => _pendingOutlinerHeader = text;

    /// <summary>Draw the buffered heading, now that there is something to put under it.</summary>
    private void OpenOutlinerGroup()
    {
        var text = _pendingOutlinerHeader;
        _pendingOutlinerHeader = null;
        _outlinerHasRows = true;
        if (text is null) return;

        var collapsed = _collapsedOutlinerGroups.Contains(text);
        var hidden = CampaignUiCatalog.HiddenOutliner.Contains(text);
        var group = new VBoxContainer { Visible = !collapsed && !hidden };
        group.SetMeta("outliner_section", text);
        group.AddThemeConstantOverride("separation", 3);
        var button = ActionButton((collapsed ? "▸ " : "▾ ") + Tr(text), () =>
        {
            if (!_collapsedOutlinerGroups.Add(text)) _collapsedOutlinerGroups.Remove(text);
            _strategicHudFingerprint = int.MinValue;
            RefreshStrategicHud();
        }, 0, 28);
        button.Name = "Group" + text.Replace(" ", "");
        button.SetMeta("outliner_section", text);
        button.Visible = !hidden;
        button.Alignment = HorizontalAlignment.Left;
        button.ClipText = true;
        button.AddThemeFontSizeOverride("font_size", 12);
        _outlinerRows.AddChild(button);
        _outlinerRows.AddChild(group);
        var position = 0;
        foreach (var section in CampaignUiCatalog.OutlinerOrder)
            foreach (var node in _outlinerRows.GetChildren().Where(n => n.HasMeta("outliner_section") && n.GetMeta("outliner_section").AsString() == section))
                _outlinerRows.MoveChild(node, position++);
        _outlinerGroupRows = group;
    }

    private void AddOutlinerHint(string text)
    {
        OpenOutlinerGroup();
        var label = Title(text, 12, Ui.Colour("text.hint"));
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.ClipText = true;
        label.MaxLinesVisible = 2;
        label.CustomMinimumSize = new Vector2(0, 18);
        _outlinerGroupRows.AddChild(label);
    }

    private Button AddOutlinerButton(string text, Action action, string tooltip)
    {
        OpenOutlinerGroup();
        var button = ActionButton(text, action, 0, 29);
        button.Alignment = HorizontalAlignment.Left;
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.CustomMinimumSize = new Vector2(0, 29);
        button.ClipText = true;
        // A row that does not fit says so. The rail is a fixed 270 pixels and a row's text is
        // a name plus its figures, so a longer language simply runs out of room - and a clip
        // with no ellipsis turns "Rare earths / 65 % / $145M" into "...65 % / $14", which is
        // not a shortened row, it is a wrong number. The ellipsis costs one character and
        // makes the cut visible; the whole line is in the tooltip either way.
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        button.TooltipText = Ui.Tip(tooltip);
        _outlinerGroupRows.AddChild(button);
        return button;
    }

    private static void ClearRuntimeChildren(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    private void FocusWar(StrategicWar war)
    {
        OpenScreen("Wars");
        var front = war.FriendlyFront(_campaign.Player.Id).FirstOrDefault();
        if (front > 0) FocusProvince(front, 7.0f);
    }

    private void FocusArmy(Army army)
    {
        OnArmyPicked(army);
        _globe.FocusOn(army.Latitude, army.Longitude, 6.8f);
    }

    private void FocusFleet(Fleet fleet)
    {
        OnFleetPicked(fleet);
        _globe.FocusOn(fleet.Latitude, fleet.Longitude, 6.8f);
    }

    private void OpenFirstPlayerBattle()
    {
        var battle = _campaign.Battles.FirstOrDefault(candidate => !candidate.Ended &&
            (candidate.AttackerId == _campaign.Player.Id || candidate.DefenderId == _campaign.Player.Id));
        if (battle is not null) ShowBattleDetails(battle);
    }

    private void FocusProvince(int provinceId, float distance)
    {
        if (_campaign.ProvinceMap is null || provinceId <= 0) return;
        var center = _campaign.ProvinceMap.CenterOf(provinceId);
        _globe.FocusOn(center.Latitude, center.Longitude, distance);
    }

}
