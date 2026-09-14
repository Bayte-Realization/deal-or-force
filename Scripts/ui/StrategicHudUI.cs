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

        var armies = _campaign.Armies.Where(army => army.NationId == player.Id).ToArray();
        var lowSupply = armies.Count(army => army.SupplyLevel < 0.45f || army.CutOff);
        var construction = _campaign.Construction.Where(project => project.NationId == player.Id).ToArray();

        if (_campaign.PendingEvents.Count > 0)
            AddAlert("! EVENT", () => RefreshEventInterface(), "A campaign event is waiting for a decision.", true);
        var peaceOffers = PeaceOffers.AwaitingAnswer(_campaign, player.Id).Count;
        if (peaceOffers > 0)
            AddAlert($"\u262e PEACE OFFER{(peaceOffers == 1 ? "" : "S")} {peaceOffers}",
                () => RefreshPeaceOfferInterface(),
                "Another government has put a settlement to you and is waiting on an answer.", true);
        if (playerBattles > 0)
            AddAlert($"⚔ {playerBattles} BATTLE{(playerBattles == 1 ? "" : "S")}", OpenFirstPlayerBattle,
                "Active battles involving your country.", true);
        if (lowSupply > 0)
            AddAlert($"△ SUPPLY {lowSupply}", () => FocusArmy(armies.First(army => army.SupplyLevel < 0.45f || army.CutOff)),
                "Formations are undersupplied or cut off.");
        if (player.ShortageIndex > 0.10f)
            AddAlert(Simulation.Loc.Format("Market access {0:P0}", player.MarketAccess), () => OpenEconomyTab("Market"),
                "Essential goods are in shortage. Open Economy for production, demand, imports and prices.", true);
        if (construction.Length == 0)
            AddAlert("No construction", () => OpenEconomyTab("Construction"), "Construction capacity is idle.");
        if (availableDecisions > 0)
            AddAlert(Simulation.Loc.Format("Decisions: {0}", availableDecisions), () => OpenScreen("Decisions"), "National decisions are available.");

        AddOutlinerHeader("WARS");
        var wars = _campaign.Wars.Where(war => !war.Ended && war.Involves(player.Id)).ToArray();
        if (wars.Length == 0) AddOutlinerHint("At peace");
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
        if (battles.Length == 0) AddOutlinerHint("No active engagements");
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
                AddOutlinerButton($"{fleet.Name}  {state}  {fleet.Hulls:N0}", () => FocusFleet(fleet),
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
        if (worstGood is null) AddOutlinerHint("No market data");
        else
        {
            var definition = CommodityCatalog.Get(worstGood.Commodity);
            AddOutlinerButton($"{Tr(definition.Name)} · {worstGood.Satisfaction:P0} · {Fmt.Money(worstGood.Price)}",
                () => OpenEconomyTab("Market"),
                $"Production {worstGood.Production:0.0}, demand {worstGood.Demand:0.0}, imports {worstGood.Imports:0.0}, exports {worstGood.Exports:0.0}");
        }

        AddOutlinerHeader("CONSTRUCTION");
        if (construction.Length == 0) AddOutlinerHint("Idle — open Economy to queue a project");
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
    }

    private float PlayerWarSupport(StrategicWar war) =>
        war.AttackerId == _campaign.Player.Id ? war.AttackerWarSupport : war.DefenderWarSupport;

    private void AddAlert(string text, Action action, string tooltip, bool critical = false)
    {
        var button = ActionButton(text, action, 0, 30, critical);
        button.Alignment = HorizontalAlignment.Left;
        button.ClipText = true;
        button.TooltipText = Ui.Tip(tooltip);
        _alertRows.AddChild(button);
    }

    private void AddOutlinerHeader(string text)
    {
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
