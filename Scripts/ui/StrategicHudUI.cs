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
    private ProgressBar _attackerOrganisation = null!;
    private ProgressBar _defenderOrganisation = null!;
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
            _campaign.ElapsedDays / 3,
            _campaign.MapRevision,
            player.Id,
            _campaign.Wars.Count(war => !war.Ended && war.Involves(player.Id)),
            playerBattles,
            _campaign.Construction.Count(project => project.NationId == player.Id),
            _campaign.PendingEvents.Count,
            availableDecisions);
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

        if (construction.Length == 0)
            AddAlert(AlertLevel.Notice, "alert_construction", Tr("Nothing being built"),
                () => OpenEconomyTab("Construction"), Tr("Construction capacity is idle."));

        if (availableDecisions > 0)
            AddAlert(AlertLevel.Chance, "decision",
                Simulation.Loc.Format("Decisions: {0}", availableDecisions), () => OpenScreen("Decisions"),
                Tr("National decisions are available."));

        FlushAlerts();

        AddOutlinerHeader("WARS");
        var wars = _campaign.Wars.Where(war => !war.Ended && war.Involves(player.Id)).ToArray();
        foreach (var war in wars)
        {
            var enemy = _campaign.NationById(war.AttackerId == player.Id ? war.DefenderId : war.AttackerId);
            var score = war.AttackerId == player.Id ? war.WarScore : -war.WarScore;
            AddOutlinerButton($"{enemy?.Tag ?? "?"}  score {score:+0;-0;0}  support {PlayerWarSupport(war):P0}",
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
            AddOutlinerButton($"⚔ province {battle.ProvinceId:N0} · {enemy?.Tag ?? "?"}",
                () => ShowBattleDetails(battle), "Open the battle report and focus the engagement.");
        }

        // The navy, in full. The armies list was dropped because it showed seven divisions
        // of however many a country has, which is not a list of anything - but a country has
        // one or two fleets, so this one is complete, and hunting a counter across an ocean
        // is no way to find your own navy. Every Paradox game has this list for that reason.
        var fleets = _campaign.Fleets.Where(fleet => fleet.NationId == player.Id)
            .OrderByDescending(fleet => fleet.Hulls).ToArray();
        if (fleets.Length > 0)
        {
            AddOutlinerHeader("NAVY");
            foreach (var fleet in fleets)
            {
                var state = fleet.InAction ? "IN ACTION"
                    : fleet.IsMoving ? "→"
                    : MissionName(fleet.Mission).ToUpperInvariant();
                AddOutlinerButton($"{Loc.Get(fleet.Name)}  {state}  {fleet.Hulls:N0}", () => FocusFleet(fleet),
                    $"{fleet.Hulls:N0} hulls · strength {fleet.Strength:P0} · cohesion {fleet.Organisation:P0} · " +
                    $"stores {fleet.Supply:P0}" + System.Environment.NewLine + MissionHelp(fleet.Mission));
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
            AddOutlinerButton($"{Tr(definition.Name)} · {worstGood.Satisfaction:P0} · {Fmt.Money(worstGood.Price)}",
                () => OpenEconomyTab("Market"),
                $"Production {worstGood.Production:0.0}, demand {worstGood.Demand:0.0}, imports {worstGood.Imports:0.0}, exports {worstGood.Exports:0.0}");
        }

        AddOutlinerHeader("CONSTRUCTION");
        foreach (var project in construction.OrderBy(project => project.Id))
        {
            var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == project.StateMapIndex);
            AddOutlinerButton($"{Tr(BuildingCatalog.Get(project.Building).Name)} · {project.Progress:P0}", () =>
            {
                _selectedState = state;
                OpenScreen("Economy");
                SelectDossierTab("Construction");
            }, state?.Name ?? "Construction project");
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

    private void AddOutlinerButton(string text, Action action, string tooltip)
    {
        OpenOutlinerGroup();
        var button = ActionButton(text, action, 0, 29);
        button.Alignment = HorizontalAlignment.Left;
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.CustomMinimumSize = new Vector2(0, 29);
        button.ClipText = true;
        button.TooltipText = Ui.Tip(tooltip);
        _outlinerGroupRows.AddChild(button);
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

    private void BuildBattleDetailControls(VBoxContainer parent)
    {
        _battleDetailControls = new VBoxContainer { Visible = false };
        _battleDetailControls.AddThemeConstantOverride("separation", 5);
        parent.AddChild(_battleDetailControls);
        _battleDetailControls.AddChild(Title("BATTLE COHESION", 11, Ui.Colour("text.caption")));
        _attackerOrganisation = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = true };
        _defenderOrganisation = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = true };
        _battleDetailControls.AddChild(_attackerOrganisation);
        _battleDetailControls.AddChild(_defenderOrganisation);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        _battleDetailControls.AddChild(row);
        row.AddChild(ActionButton("Focus battle", () => FocusProvince(SelectedBattle()?.ProvinceId ?? -1, 6.5f), 145, 32, true));
        row.AddChild(ActionButton("War dossier", () => OpenScreen("Wars"), 145, 32));
    }

    private LandBattle SelectedBattle() => _campaign?.Battles.FirstOrDefault(battle => battle.Id == _selectedBattleId);

    private void ShowBattleDetails(LandBattle battle)
    {
        if (battle is null || !_hudLayer.Visible) return;
        if (!CampaignIntel.PlayerCanSeeBattle(_campaign, battle)) return;
        PrepareDossierTabs("Battle");
        ApplyDossierTab();
        ClearDetail();
        _selectedBattleId = battle.Id;
        _openScreen = "";
        _sidePanel.Visible = true;
        SetArmyPanelVisible(false);
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
        _militarySpending.Visible = false;
        _battleDetailControls.Visible = true;
        var attacker = _campaign.NationById(battle.AttackerId);
        var defender = _campaign.NationById(battle.DefenderId);
        var stateIndex = _campaign.ProvinceMap is null || battle.ProvinceId <= 0
            ? 0 : _campaign.ProvinceMap.ProvinceToState[battle.ProvinceId];
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == stateIndex);
        var latest = battle.Rounds.LastOrDefault();
        var attackerFormations = battle.Attackers.Select(id => _campaign.Armies.Find(army => army.Id == id))
            .Where(army => army is not null).ToArray();
        var defenderFormations = battle.Defenders.Select(id => _campaign.Armies.Find(army => army.Id == id))
            .Where(army => army is not null).ToArray();
        var attackerTypes = string.Join(", ", attackerFormations.GroupBy(army => UnitTypeCatalog.Get(army!.TemplateId).Name)
            .Select(group => $"{group.Count()}× {group.Key}"));
        var defenderTypes = string.Join(", ", defenderFormations.GroupBy(army => UnitTypeCatalog.Get(army!.TemplateId).Name)
            .Select(group => $"{group.Count()}× {group.Key}"));
        _sideTitle.Text = battle.Ended ? "BATTLE REPORT" : "BATTLE IN PROGRESS";
        _attackerOrganisation.Value = (latest?.AttackerOrganisation ?? 1f) * 100f;
        _defenderOrganisation.Value = (latest?.DefenderOrganisation ?? 1f) * 100f;
        var recent = string.Join("\n", battle.Rounds.TakeLast(6).Reverse().Select(round =>
            $"Day {round.Day - battle.StartedDay + 1}: {round.Note} · losses {round.AttackerLosses:N0}/{round.DefenderLosses:N0}"));
        _sideBody.Text =
            Simulation.Loc.Format("{0}  vs  {1}\n{2} · province #{3:N0}\nDay {4} · {5}\n\n",
                attacker?.Name ?? "?", defender?.Name ?? "?",
                state?.Name ?? Tr("Unmapped theatre"), battle.ProvinceId,
                _campaign.ElapsedDays - battle.StartedDay + 1,
                battle.Ended ? battle.Outcome : Tr("Fighting")) +
            $"Combat width: {battle.AttackerWidth:0}/{battle.DefenderWidth:0}\n" +
            (battle.RiverCrossingStrength > 0f
                ? $"River crossing: {(battle.RiverCrossingStrength > 0.66f ? "major" : "ordinary")} · defender advantage active\n"
                : "") +
            $"Committed formations: {battle.Attackers.Count}/{battle.Defenders.Count}\n" +
            $"Attacker: {(attackerTypes.Length == 0 ? "none" : attackerTypes)}\n" +
            $"Defender: {(defenderTypes.Length == 0 ? "none" : defenderTypes)}\n" +
            (latest is null ? "" : $"Effective power: {latest.AttackerPower:0.0}/{latest.DefenderPower:0.0} · planning {latest.AttackerPlanning:P0}\n") +
            $"Reinforcements: +{battle.AttackerReinforcements}/+{battle.DefenderReinforcements}\n" +
            $"Losses: {battle.AttackerLosses:N0}/{battle.DefenderLosses:N0}\n\n" +
            (recent.Length == 0 ? "The first combat round has not resolved yet." : recent);
        if (_campaign.Player.IsHuman && battle.AttackerId != _campaign.Player.Id && battle.DefenderId != _campaign.Player.Id)
        {
            _attackerOrganisation.Visible = false;
            _defenderOrganisation.Visible = false;
            _sideBody.Text = Simulation.Loc.Format(
                    "{0} vs {1}\n{2} · province #{3:N0}\n{4} · Day {5}\n\n" +
                    "Observed battle between foreign countries. Detailed combat reports unavailable.\n",
                    attacker?.Name, defender?.Name, state?.Name, battle.ProvinceId,
                    battle.Ended ? battle.Outcome : Tr("Fighting"),
                    _campaign.ElapsedDays - battle.StartedDay + 1) +
                (battle.RiverCrossingStrength > 0 ? Tr("River crossing — defender advantage.") : "");
        }
        else _attackerOrganisation.Visible = _defenderOrganisation.Visible = true;
        FocusProvince(battle.ProvinceId, 6.5f);
    }
}
