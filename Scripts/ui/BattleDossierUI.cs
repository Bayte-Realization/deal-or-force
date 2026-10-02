using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private const string BattleBalanceExplanation =
        "Endurance balance, not victory probability. Compares time left before organisation breaks at the recent loss rate. Reinforcements, supply and orders can change it.";
    private int _battleDossierFingerprint = int.MinValue;
    private string _battleWithdrawalFeedback = "";

    private void BuildBattleDetailControls(VBoxContainer parent)
    {
        _battleDetailControls = new VBoxContainer
        {
            Name = "BattleDossier", Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _battleDetailControls.AddThemeConstantOverride("separation", 9);
        parent.AddChild(_battleDetailControls);
    }

    private LandBattle SelectedBattle() => _campaign?.Battles.FirstOrDefault(battle => battle.Id == _selectedBattleId);

    private void ShowBattleDetails(LandBattle battle)
    {
        if (battle is null || !_hudLayer.Visible || !CampaignIntel.PlayerCanSeeBattle(_campaign, battle)) return;
        if (_supportLayer is { Visible: true }) CloseSupportScreen();
        CloseFleetPanel();
        PrepareDossierTabs("Battle");
        ApplyDossierTab();
        ClearDetail();
        _selectedBattleId = battle.Id;
        _battleWithdrawalFeedback = "";
        _openScreen = "";
        _sidePanel.Visible = true;
        SetArmyPanelVisible(false);
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = _decisionControls.Visible = _militarySpending.Visible = false;
        _battleDetailControls.Visible = true;
        _battleDossierFingerprint = int.MinValue;
        RefreshBattleDossier();
        FocusProvince(battle.ProvinceId, 6.5f);
    }

    private void RefreshBattleDossier()
    {
        if (_battleDetailControls is not { Visible: true } || !_sidePanel.Visible) return;
        var battle = SelectedBattle();
        if (battle is null || !CampaignIntel.PlayerCanSeeBattle(_campaign, battle))
        {
            _battleDetailControls.Visible = false;
            _sideBody.Text = Tr("Battle report is no longer available.");
            return;
        }
        var hash = HashCode.Combine(battle.Id, battle.Rounds.Count, battle.Ended,
            battle.AttackerLosses, battle.DefenderLosses, _campaign.ElapsedDays, Loc.Language);
        hash = HashCode.Combine(hash, battle.Attackers.Count, battle.Defenders.Count);
        if (hash == _battleDossierFingerprint) return;
        _battleDossierFingerprint = hash;
        foreach (var child in _battleDetailControls.GetChildren())
        {
            _battleDetailControls.RemoveChild(child);
            child.QueueFree();
        }
        _sideTitle.Text = Tr(battle.Ended ? "BATTLE REPORT" : "BATTLE IN PROGRESS");
        _sideBody.Text = Loc.Format("{0} · day {1}", BattlePlaceName(battle.ProvinceId),
            Math.Max(1, (battle.Rounds.LastOrDefault()?.Day ?? _campaign.ElapsedDays) - battle.StartedDay + 1));
        var attacker = _campaign.NationById(battle.AttackerId);
        var defender = _campaign.NationById(battle.DefenderId);
        var detailed = !_campaign.Player.IsHuman || battle.AttackerId == _campaign.Player.Id ||
                       battle.DefenderId == _campaign.Player.Id;
        if (!detailed)
        {
            _battleDetailControls.AddChild(BattleText(Loc.Format("{0} vs {1}",
                attacker?.DisplayName ?? "?", defender?.DisplayName ?? "?"), 15));
            _battleDetailControls.AddChild(BattleText(Tr("Observed battle between foreign countries. Detailed combat reports unavailable.")));
            AddBattleNavigation(battle);
            return;
        }
        var assessment = BattleReadout.Assess(battle, _campaign.Defines.BattleBreakOrganisation);
        var playerAttacks = !_campaign.Player.IsHuman || battle.AttackerId == _campaign.Player.Id;
        var share = assessment.ShareFor(playerAttacks);
        var tone = !assessment.Known ? "text.muted" : share > .55f ? "state.positive" :
            share < .45f ? "state.negative" : "state.warning";
        var hero = new HBoxContainer();
        _battleDetailControls.AddChild(hero);
        var icon = Symbol("battle", 26, tone);
        if (icon is not null) hero.AddChild(icon);
        var headline = BattleText(battle.Ended ? Loc.Format("Winner: {0}",
            _campaign.NationById(battle.WinnerId)?.DisplayName ?? Tr("Unknown")) :
            Tr(assessment.StatusFor(playerAttacks)), 18, tone);
        headline.Name = "BattleStatus";
        hero.AddChild(headline);
        if (!battle.Ended)
        {
            var caption = BattleText(assessment.Known
                ? Loc.Format(_campaign.Player.IsHuman ? "Our endurance balance: {0:P0}" : "Attacker endurance balance: {0:P0}", share)
                : Tr("The first combat round has not resolved yet."),
                12, "text.body_soft");
            caption.Name = "BattleAssessment";
            caption.TooltipText = Ui.Tip(Tr(BattleBalanceExplanation));
            caption.MouseFilter = MouseFilterEnum.Stop;
            _battleDetailControls.AddChild(caption);
            var balance = BattleGauge(assessment.Known ? share : .5f, tone, "BattleEndurance");
            balance.TooltipText = caption.TooltipText;
            _battleDetailControls.AddChild(balance);
        }
        var sideRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sideRow.AddThemeConstantOverride("separation", 12);
        _battleDetailControls.AddChild(sideRow);
        // Resolve ids once per displayed report, not on every frame or once per row.
        var armies = _campaign.Armies.ToDictionary(army => army.Id);
        var attacking = ResolveBattleSide(battle.Attackers, armies);
        var defending = ResolveBattleSide(battle.Defenders, armies);
        AddBattleSide(sideRow, battle, true, attacker, attacking);
        AddBattleSide(sideRow, battle, false, defender, defending);
        if (battle.WithdrawnAttackers.Count + battle.WithdrawnDefenders.Count > 0)
            _battleDetailControls.AddChild(BattleText(Loc.Format("Withdrawn formations: {0}/{1}",
                battle.WithdrawnAttackers.Count, battle.WithdrawnDefenders.Count), 12, "text.muted"));
        _battleDetailControls.AddChild(new HSeparator());
        _battleDetailControls.AddChild(BattleText(Tr("Ground and fire"), 13, "text.accent"));
        var terrain = _campaign.ProvinceMap?.TerrainAt(battle.ProvinceId);
        _battleDetailControls.AddChild(BattleText(Loc.Format("Terrain: {0} · frontage {1:0}/{2:0}",
            Tr(terrain?.Name ?? "Unknown"), battle.AttackerWidth, battle.DefenderWidth)));
        if (battle.RiverCrossingStrength > 0)
            _battleDetailControls.AddChild(BattleText(Tr(battle.RiverCrossingStrength > .66f
                ? "River crossing: major · defender advantage active"
                : "River crossing: ordinary · defender advantage active"), 12, "state.warning"));
        _battleDetailControls.AddChild(BattleText(Loc.Format("Long-range fire: {0:0.#}/{1:0.#}",
            battle.AttackerSupport, battle.DefenderSupport)));
        var latest = battle.Rounds.LastOrDefault();
        if (latest is not null)
        {
            _battleDetailControls.AddChild(BattleText(Tr(latest.Note), 12, "text.accent_dim"));
            var history = string.Join("\n", battle.Rounds.TakeLast(3).Reverse().Select(round =>
                Loc.Format("Day {0}: losses {1:N0}/{2:N0} · organisation {3:P0}/{4:P0}",
                    round.Day - battle.StartedDay + 1, round.AttackerLosses, round.DefenderLosses,
                    round.AttackerOrganisation, round.DefenderOrganisation)));
            _battleDetailControls.AddChild(BattleText(history, 11, "text.muted"));
        }
        else _battleDetailControls.AddChild(BattleText(Tr("Assessing battle"), 12, "text.muted"));
        if (battle.Ended) _battleDetailControls.AddChild(BattleText(Tr(battle.Outcome), 13, tone));
        AddBattleNavigation(battle);
        if (_battleWithdrawalFeedback.Length > 0)
            _battleDetailControls.AddChild(BattleText(_battleWithdrawalFeedback, 12, "state.warning"));
    }

    private static List<Army> ResolveBattleSide(IEnumerable<int> ids, Dictionary<int, Army> armies)
    {
        var result = new List<Army>();
        foreach (var id in ids) if (armies.TryGetValue(id, out var army) && army.Divisions >= .1f) result.Add(army);
        return result;
    }

    private void AddBattleSide(HBoxContainer parent, LandBattle battle, bool attacking, Nation nation, List<Army> armies)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1,
            CustomMinimumSize = Vector2.Zero };
        column.AddThemeConstantOverride("separation", 5);
        parent.AddChild(column);
        column.AddChild(BattleText(Tr(attacking ? "ATTACKER" : "DEFENDER"), 11, "text.caption"));
        var name = BattleText(nation?.DisplayName ?? "?", 15, "text.accent");
        column.AddChild(name);
        var state = BattleReadout.Summarise(armies);
        var last = battle.Rounds.LastOrDefault();
        // Completed reports must not silently use today's recovered troops as yesterday's battle.
        var org = battle.Ended && last is not null
            ? attacking ? last.AttackerOrganisation : last.DefenderOrganisation : state.Organisation;
        AddBattleMeter(column, "Organisation", org, "combat.organisation", "organisation");
        if (!battle.Ended)
        {
            AddBattleMeter(column, "Equipment", state.Equipment, "combat.equipment", "equipment");
            AddBattleMeter(column, "Fighting strength", state.Strength, "combat.strength", "strength");
            AddBattleMeter(column, "Supply", state.Supply, "combat.supply", "supply");
            AddBattleMeter(column, "Cohesion", state.Cohesion, "combat.cohesion", "cohesion");
            column.AddChild(BattleText(Loc.Format("{0:N0} men · {1} formations", state.Personnel, state.Formations), 11));
        }
        column.AddChild(BattleText(Loc.Format("Losses: {0:N0}", attacking ? battle.AttackerLosses : battle.DefenderLosses),
            12, "state.warning"));
        if (battle.Ended) return;
        var limit = Math.Clamp((int)InterfaceSkin.Metric("combat.roster_rows", 6), 1, 30);
        foreach (var army in armies.Take(limit))
        {
            var card = new VBoxContainer();
            var caption = BattleText(army.Name, 11);
            caption.MaxLinesVisible = 2;
            caption.TooltipText = Ui.Tip(DivisionTooltip(army));
            caption.MouseFilter = MouseFilterEnum.Stop;
            card.AddChild(caption);
            var bars = new HBoxContainer();
            bars.AddThemeConstantOverride("separation", 3);
            card.AddChild(bars);
            bars.AddChild(BattleGauge(army.Organisation, "combat.organisation"));
            bars.AddChild(BattleGauge(army.Equipment, "combat.equipment"));
            bars.TooltipText = Ui.Tip(Loc.Format("Organisation {0:P0} · equipment {1:P0}", army.Organisation, army.Equipment));
            bars.MouseFilter = MouseFilterEnum.Stop;
            column.AddChild(card);
            if (army.CutOff || army.SupplyLevel < .45f || army.Organisation < .35f)
                column.AddChild(BattleText(Tr(army.CutOff ? "CUT OFF" : army.Organisation < .35f ?
                    "Organisation is spent" : "Short of supply"), 10, "state.negative"));
        }
        if (armies.Count > limit) column.AddChild(BattleText(Loc.Format("…and {0:N0} more", armies.Count - limit), 11));
    }

    private void AddBattleMeter(VBoxContainer parent, string title, float value, string tone, string iconName)
    {
        var row = new HBoxContainer();
        var icon = Symbol(iconName, 14, tone);
        if (icon is not null) row.AddChild(icon);
        var label = BattleText(Tr(title), 11, "text.body_soft");
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        row.AddChild(BattleText($"{BattleReadout.Unit(value):P0}", 11));
        parent.AddChild(row);
        parent.AddChild(BattleGauge(value, tone));
    }

    private Label BattleText(string text, int size = 12, string tone = "text.body") =>
        new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, Theme = null }
            .WithBattleStyle(size, Ui.Colour(tone));

    private ProgressBar BattleGauge(float value, string tone, string name = "")
    {
        var bar = new ProgressBar { MinValue = 0, MaxValue = 1, Value = BattleReadout.Unit(value),
            ShowPercentage = false, CustomMinimumSize = new Vector2(0, 10),
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        if (name.Length > 0) bar.Name = name;
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.inset"),
            CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2 });
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour(tone),
            CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2 });
        return bar;
    }

    private void AddBattleNavigation(LandBattle battle)
    {
        if (!battle.Ended && _campaign.Player.IsHuman &&
            _campaign.Armies.Any(army => army.BattleId == battle.Id && army.NationId == _campaign.Player.Id))
        {
            var leave = ActionButton("Leave this battle", () =>
            {
                var targets = _campaign.Armies.Where(army => army.BattleId == battle.Id &&
                    army.NationId == _campaign.Player.Id).Select(army => army.Id).ToArray();
                var commands = targets.Select(id => new WithdrawArmyFromBattleCommand
                    { IssuerId = _campaign.Player.Id, ArmyId = id }).ToArray();
                foreach (var command in commands) _campaign.Commands.Enqueue(command);
                _campaign.Commands.ApplyAll(_campaign);
                _battleWithdrawalFeedback = Loc.Format("Left battle: {0} · stopped: {1} · no retreat route: {2}",
                    commands.Count(command => command.Withdrawn), 0, commands.Count(command => !command.Withdrawn));
                _battleDossierFingerprint = int.MinValue;
                RefreshBattleDossier();
            }, 160, 32);
            leave.Name = "WithdrawBattleSide";
            leave.TooltipText = Ui.Tip(Tr("Withdraw our participants only. Defenders without a friendly retreat route remain in combat."));
            _battleDetailControls.AddChild(leave);
        }
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        _battleDetailControls.AddChild(row);
        row.AddChild(ActionButton("Focus battle", () => FocusProvince(battle.ProvinceId, 6.5f), 130, 30, true));
        row.AddChild(ActionButton("War dossier", () => OpenScreen("Wars"), 130, 30));
    }
}

internal static class BattleLabelStyle
{
    internal static Label WithBattleStyle(this Label label, int size, Color color)
    {
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
