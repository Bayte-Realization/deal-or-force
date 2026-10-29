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
        hash = HashCode.Combine(hash, battle.Attackers.Count, battle.Defenders.Count,
            _campaign.ElapsedHours, _campaign.Player.Id, _campaign.Player.IsHuman);
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
            if (!battle.Ended)
            {
                var observed = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                observed.AddThemeConstantOverride("separation", 12);
                _battleDetailControls.AddChild(observed);
                var byId = _campaign.Armies.ToDictionary(army => army.Id);
                AddObservedBattleSide(observed, true, attacker, ResolveBattleSide(battle.Attackers, byId));
                AddObservedBattleSide(observed, false, defender, ResolveBattleSide(battle.Defenders, byId));
            }
            AddBattleNavigation(battle);
            return;
        }
        var assessment = BattleReadout.Assess(battle, _campaign.Defines.BattleBreakOrganisation);
        var playerAttacks = !_campaign.Player.IsHuman || battle.AttackerId == _campaign.Player.Id;
        var share = assessment.ShareFor(playerAttacks);
        var tone = !assessment.Known ? "text.muted" : share > .55f ? "state.positive" :
            share < .45f ? "state.negative" : "state.warning";
        var heroPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        heroPanel.SetMeta("atlas_section_ground", "surface.inset");
        heroPanel.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(heroPanel, AtlasSurfaceSkin.Level.Raised, "surface.inset");
        _battleDetailControls.AddChild(heroPanel);

        var heroBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        heroBox.AddThemeConstantOverride("separation", 4);
        heroPanel.AddChild(heroBox);

        var hero = new HBoxContainer();
        heroBox.AddChild(hero);
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
            heroBox.AddChild(caption);
            var balance = BattleGauge(assessment.Known ? share : .5f, tone, "BattleEndurance");
            balance.TooltipText = caption.TooltipText;
            heroBox.AddChild(balance);
        }
        var sideRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sideRow.AddThemeConstantOverride("separation", 12);
        _battleDetailControls.AddChild(sideRow);
        // Resolve ids once per displayed report, not on every frame or once per row.
        var armies = _campaign.Armies.ToDictionary(army => army.Id);
        var attacking = ResolveBattleSide(battle.Attackers, armies);
        var defending = ResolveBattleSide(battle.Defenders, armies);
        if (!battle.Ended)
            defending = defending.Where(army => army.ProvinceId == battle.ProvinceId).ToList();
        AddBattleSide(sideRow, battle, true, attacker, attacking);
        AddBattleSide(sideRow, battle, false, defender, defending);
        if (battle.WithdrawnAttackers.Count + battle.WithdrawnDefenders.Count > 0)
            _battleDetailControls.AddChild(FactRow(Tr("Withdrawn formations"),
                $"{battle.WithdrawnAttackers.Count}/{battle.WithdrawnDefenders.Count}", "surface.line_faint", "text.muted"));
        SectionRule(_battleDetailControls, "Ground and fire");
        var terrain = _campaign.ProvinceMap?.TerrainAt(battle.ProvinceId);
        // A report that says "19 formations" without saying how many of them are in the line
        // is the report that hid the Kharkiv problem: the province held one formation, the
        // panel counted nineteen, and four of them were shooting. The frontage line says
        // which of the two numbers the fire belongs to.
        var known = battle.AttackerInLine + battle.DefenderInLine > 0;
        _battleDetailControls.AddChild(FactRow(Tr("Terrain"), Tr(terrain?.Name ?? "Unknown"), "button.primary_border"));
        _battleDetailControls.AddChild(FactRow(Tr("Frontage"),
            known
                ? Loc.Format("{0:0}/{1:0} · in line {2}/{3} vs {4}/{5}",
                    battle.AttackerWidth, battle.DefenderWidth,
                    battle.AttackerInLine, battle.Attackers.Count, battle.DefenderInLine, battle.Defenders.Count)
                : $"{battle.AttackerWidth:0}/{battle.DefenderWidth:0}",
            "surface.line_faint"));
        if (battle.RiverCrossingStrength > 0)
        {
            _battleDetailControls.AddChild(FactRow(Tr("River crossing"),
                Tr(battle.RiverCrossingStrength > .66f ? "Major · defender advantage" : "Ordinary · defender advantage"),
                "state.warning", "state.warning"));
            if (!battle.Ended)
                _battleDetailControls.AddChild(FactRow(Tr("River assault power retained"),
                    $"{WaterConditions.AttackMultiplier(_campaign, battle.RiverCrossingStrength, battle.ProvinceId):P0}",
                    "state.warning", "text.body_soft"));
        }
        // The day's manoeuvres. A countered manoeuvre names itself in the colour of the side
        // that countered it, because that is the fact of the morning; the tooltip says what
        // each manoeuvre is, in the same words for both languages.
        var attackerTactic = Simulation.CombatTactics.Find(battle.AttackerTactic);
        var defenderTactic = Simulation.CombatTactics.Find(battle.DefenderTactic);
        if (attackerTactic is not null || defenderTactic is not null)
        {
            var nameOf = new Func<Simulation.CombatTactic, string>(tactic =>
                tactic is null ? Tr("No manoeuvre") : Tr(tactic.Name));
            var tacticTone = attackerTactic is not null && attackerTactic.Counters(defenderTactic)
                ? "state.positive"
                : defenderTactic is not null && defenderTactic.Counters(attackerTactic)
                    ? "state.warning"
                    : "text.body_soft";
            var manoeuvres = FactRow(Tr("Manoeuvres"),
                $"{nameOf(attackerTactic)} vs {nameOf(defenderTactic)}", tacticTone, tacticTone);
            manoeuvres.TooltipText = Ui.Tip(
                (attackerTactic is null ? "" : Tr(attackerTactic.Name) + " — " + Tr(attackerTactic.Description)) +
                (attackerTactic is not null && defenderTactic is not null ? "\n" : "") +
                (defenderTactic is null ? "" : Tr(defenderTactic.Name) + " — " + Tr(defenderTactic.Description)));
            _battleDetailControls.AddChild(manoeuvres);
        }
        _battleDetailControls.AddChild(FactRow(Tr("Long-range fire"),
            $"{battle.AttackerSupport:0.#}/{battle.DefenderSupport:0.#}", "surface.line_faint"));
        var latest = battle.Rounds.LastOrDefault();
        if (latest is not null)
        {
            SectionRule(_battleDetailControls, "Combat log", Loc.Format("{0} d.", battle.Rounds.Count));
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
        var sidePanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1,
            CustomMinimumSize = Vector2.Zero
        };
        sidePanel.SetMeta("atlas_section_ground", "surface.inset");
        sidePanel.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(sidePanel, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        parent.AddChild(sidePanel);

        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 5);
        sidePanel.AddChild(column);
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
            // The pile in the province is the whole force; the line is what is shooting. Say
            // both when they differ, or the panel reports a reserve as if it were fighting.
            var inLine = attacking ? battle.AttackerInLine : battle.DefenderInLine;
            column.AddChild(BattleText(inLine > 0 && inLine < state.Formations
                ? Loc.Format("{0:N0} men · {1} formations · {2} in the line",
                    state.Personnel, state.Formations, inLine)
                : Loc.Format("{0:N0} men · {1} formations", state.Personnel, state.Formations), 11));
        }
        column.AddChild(BattleText(Loc.Format("Losses: {0:N0}", attacking ? battle.AttackerLosses : battle.DefenderLosses),
            12, "state.warning"));
        if (battle.Ended) return;
        var limit = Math.Clamp((int)InterfaceSkin.Metric("combat.roster_rows", 6), 1, 30);
        foreach (var army in armies.Take(limit))
        {
            var line = attacking ? battle.LastAttackerLine : battle.LastDefenderLine;
            if (line.Count > 0)
                column.AddChild(BattleText(Tr(line.Contains(army.Id) ? "In the firing line" : "Battle reserve"),
                    10, line.Contains(army.Id) ? "text.accent" : "text.muted"));
            if (army.PostBattleWaypoints.Count > 0)
                column.AddChild(BattleText(Loc.Format("After battle: {0} waypoint(s). Alt+H cancels the queue; H leaves battle.",
                    army.PostBattleWaypoints.Count), 10, "state.warning"));
            var card = new VBoxContainer();
            var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var caption = BattleText(army.Name, 11);
            caption.MaxLinesVisible = 2;
            caption.TooltipText = Ui.Tip(CanInspectNation(army.NationId) ? DivisionTooltip(army) :
                Loc.Format("Organisation {0:P0} · equipment {1:P0}",
                    BattleReadout.Unit(army.Organisation), BattleReadout.Unit(army.Equipment)) + "\n" +
                Tr("Foreign formation — orders and route are not available."));
            caption.MouseFilter = MouseFilterEnum.Stop;
            caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            header.AddChild(caption);
            card.AddChild(header);
            if (!battle.Ended && army.NationId == _campaign.Player.Id && _campaign.Player.IsHuman)
            {
                // Stack actions beneath the caption: two long localized buttons in the
                // roster header otherwise squeeze the opposing side to half its width.
                var actions = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                card.AddChild(actions);
                var armyId = army.Id;
                var withdrawBtn = ActionButton(Tr("Retreat"), () =>
                {
                    WithdrawBattleFormation(armyId, 1f);
                }, 0, 20);
                withdrawBtn.Name = "WithdrawFormation_" + army.Id;
                withdrawBtn.TooltipText = Ui.Tip(Tr("Withdraw only this formation from the battle to a friendly province."));
                withdrawBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                actions.AddChild(withdrawBtn);
                if (army.Divisions >= 2f)
                {
                    var half = ActionButton("Withdraw half", () => WithdrawBattleFormation(armyId, .5f), 0, 20);
                    half.Name = "WithdrawHalf_" + army.Id;
                    half.TooltipText = Ui.Tip(Tr("Split off half the divisions to friendly ground; the rest keep fighting. No healing or free equipment."));
                    half.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                    actions.AddChild(half);
                }
            }
            var bars = new HBoxContainer();
            bars.AddThemeConstantOverride("separation", 3);
            card.AddChild(bars);
            bars.AddChild(BattleGauge(army.Organisation, "combat.organisation"));
            bars.AddChild(BattleGauge(army.Equipment, "combat.equipment"));
            bars.TooltipText = Ui.Tip(Loc.Format("Organisation {0:P0} · equipment {1:P0}", army.Organisation, army.Equipment) +
                (army.NationId == _campaign.Player.Id ? "\n" + EquipmentLogistics.Description(_campaign, army) : ""));
            bars.MouseFilter = MouseFilterEnum.Stop;
            // Bars are for scanning; the actual percentages must also be readable
            // without hovering two narrow, previously unlabelled strips.
            var numbers = BattleText(Loc.Format("Organisation {0:P0} · equipment {1:P0}",
                BattleReadout.Unit(army.Organisation), BattleReadout.Unit(army.Equipment)), 11, "text.body_soft");
            numbers.Name = "FormationReadiness_" + army.Id;
            card.AddChild(numbers);
            column.AddChild(card);
            if (army.CutOff || army.SupplyLevel < .45f || army.Organisation < .35f)
                column.AddChild(BattleText(Tr(army.CutOff ? "CUT OFF" : army.Organisation < .35f ?
                    "Organisation is spent" : "Short of supply"), 10, "state.negative"));
        }
        if (armies.Count > limit) column.AddChild(BattleText(Loc.Format("…and {0:N0} more", armies.Count - limit), 11));
    }

    private void AddObservedBattleSide(HBoxContainer parent, bool attacking, Nation nation, List<Army> armies)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1, CustomMinimumSize = Vector2.Zero };
        column.AddThemeConstantOverride("separation", 5);
        parent.AddChild(column);
        column.AddChild(BattleText(Tr(attacking ? "ATTACKER" : "DEFENDER"), 11, "text.caption"));
        column.AddChild(BattleText(nation?.DisplayName ?? "?", 15, "text.accent"));
        var condition = MilitaryConditionReadout.ForPlayer(_campaign, armies);
        if (!condition.Known)
        {
            column.AddChild(BattleText(Tr("No formations currently observed."), 12, "text.muted"));
            return;
        }
        column.AddChild(BattleText(Tr("Observed combat readiness"), 11, "text.caption"));
        AddBattleMeter(column, "Organisation", condition.Organisation, "combat.organisation", "organisation");
        AddBattleMeter(column, "Equipment", condition.Equipment, "combat.equipment", "equipment");
        column.AddChild(BattleText(Tr("Observed formations only; unseen reinforcements are not included."),
            11, "text.muted"));
        var limit = Math.Clamp((int)InterfaceSkin.Metric("combat.roster_rows", 6), 1, 30);
        var shown = 0;
        foreach (var army in armies)
        {
            if (!MilitaryConditionReadout.CanRead(_campaign, _campaign.Player.Id, army)) continue;
            if (shown++ >= limit) continue;
            column.AddChild(BattleText(army.Name, 11));
            var numbers = BattleText(Loc.Format("Organisation {0:P0} · equipment {1:P0}",
                BattleReadout.Unit(army.Organisation), BattleReadout.Unit(army.Equipment)), 11, "text.body_soft");
            numbers.Name = "ObservedFormationReadiness_" + army.Id;
            column.AddChild(numbers);
        }
        if (shown > limit) column.AddChild(BattleText(Loc.Format("…and {0:N0} more", shown - limit), 11));
    }

    private void WithdrawBattleFormation(int armyId, float share)
        => ConfirmLastDefenderWithdrawal(new[] { armyId }, share, () => ApplyBattleWithdrawal(armyId, share));

    private void ApplyBattleWithdrawal(int armyId, float share)
    {
        var army = _campaign.Armies.Find(a => a.Id == armyId);
        if (army is null || army.NationId != _campaign.Player.Id) return;
        var command = new WithdrawArmyFromBattleCommand { IssuerId = _campaign.Player.Id, ArmyId = armyId, Share = share };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _battleWithdrawalFeedback = !command.Applied ? Tr("Order sent to the host. Awaiting confirmation.") :
            command.Withdrawn ? Loc.Format("Withdrawn: {0}", army.Name) :
            Loc.Format("Cannot retreat: {0}", Tr(command.Result));
        _battleDossierFingerprint = int.MinValue;
        RefreshBattleDossier();
    }

    private void AddBattleMeter(VBoxContainer parent, string title, float value, string tone, string iconName)
    {
        var row = new HBoxContainer();
        var icon = Symbol(iconName, 14, tone);
        if (icon is not null) row.AddChild(icon);
        var label = BattleText(Tr(title), 11, "text.body_soft");
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        var amount = BattleText($"{BattleReadout.Unit(value):P0}", 11);
        // The value needs its natural width, not half of the row: the latter
        // split Russian captions into single trailing letters even at 1080p.
        amount.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        amount.AutowrapMode = TextServer.AutowrapMode.Off;
        row.AddChild(amount);
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
                ConfirmLastDefenderWithdrawal(targets, 1f, () =>
                {
                var commands = targets.Select(id => new WithdrawArmyFromBattleCommand
                    { IssuerId = _campaign.Player.Id, ArmyId = id }).ToArray();
                foreach (var command in commands) _campaign.Commands.Enqueue(command);
                _campaign.Commands.ApplyAll(_campaign);
                _battleWithdrawalFeedback = commands.Any(command => !command.Applied)
                    ? Tr("Order sent to the host. Awaiting confirmation.")
                    : Loc.Format("Left battle: {0} · stopped: {1} · no retreat route: {2}",
                        commands.Count(command => command.Withdrawn), 0, commands.Count(command => !command.Withdrawn));
                _battleDossierFingerprint = int.MinValue;
                RefreshBattleDossier();
                });
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
