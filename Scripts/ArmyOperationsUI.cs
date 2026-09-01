using System.Collections.Generic;
using System.Linq;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

public partial class GlobalityGame
{
    private VBoxContainer _armyOrders = null!;
    private Army _selectedArmy;
    private readonly List<Army> _selectedArmies = new();
    private readonly Dictionary<ReinforcementPriority, Button> _armyPriorityButtons = new();
    private Label _armyCommandFeedback = null!;

    private void BuildArmyOrderControls(VBoxContainer parent)
    {
        _armyOrders = new VBoxContainer { Visible = false };
        _armyOrders.AddThemeConstantOverride("separation", 6);
        parent.AddChild(_armyOrders);
        _armyOrders.AddChild(Title("OPERATIONAL ORDER", 12, Ui.Colour("text.caption")));
        _armyCommandFeedback = Title("RMB: march / attack · Shift+RMB: append waypoint · Shift+click: add to selection",
            11, Ui.Colour("text.body_soft"));
        _armyCommandFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _armyOrders.AddChild(_armyCommandFeedback);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(row);
        var halt = ActionButton("Halt", () => IssueArmyOrder(ArmyOrder.Manual), 96, 34);
        halt.TooltipText = "Cancel the current route or battle plan and hold the present province.";
        row.AddChild(halt);
        row.AddChild(ActionButton("Defend", () => IssueArmyOrder(ArmyOrder.DefendFront), 96, 34));
        row.AddChild(ActionButton("Advance", () => IssueArmyOrder(ArmyOrder.AdvanceFront), 96, 34, true));

        var doctrine = new HBoxContainer();
        doctrine.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(doctrine);
        var reserve = ActionButton("Reserve", () => IssueArmyOrder(ArmyOrder.ReserveFront), 145, 32);
        reserve.TooltipText = "Deploy behind the contact line, recover faster and reinforce nearby defensive battles.";
        doctrine.AddChild(reserve);
        var spearhead = ActionButton("Spearhead", () => IssueArmyOrder(ArmyOrder.Spearhead), 160, 32, true);
        spearhead.TooltipText = "Concentrate on the last selected enemy region, or on the war goal if no region was selected.";
        doctrine.AddChild(spearhead);

        _armyOrders.AddChild(Title("COMBAT POSTURE", 11, Ui.Colour("text.caption")));
        var posture = new HBoxContainer();
        posture.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(posture);
        var cautious = ActionButton("Cautious", () => IssueArmyStance(ArmyStance.Cautious), 96, 30);
        cautious.TooltipText = "Slower movement and planning, but better recovery, entrenchment, defence and fewer casualties.";
        posture.AddChild(cautious);
        var balanced = ActionButton("Balanced", () => IssueArmyStance(ArmyStance.Balanced), 96, 30, true);
        balanced.TooltipText = "No special modifier. Recommended when supply and local superiority are uncertain.";
        posture.AddChild(balanced);
        var aggressive = ActionButton("Aggressive", () => IssueArmyStance(ArmyStance.Aggressive), 96, 30);
        aggressive.TooltipText = "Faster movement and stronger attacks, at the cost of recovery, defence and heavier casualties.";
        posture.AddChild(aggressive);

        _armyOrders.AddChild(Title("REINFORCEMENT PRIORITY", 11, Ui.Colour("text.caption")));
        var priority = new HBoxContainer();
        priority.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(priority);
        AddReinforcementPriorityButton(priority, "Low", ReinforcementPriority.Low,
            "Receives 0.45 reinforcement weight. Useful for quiet sectors and training formations.");
        AddReinforcementPriorityButton(priority, "Normal", ReinforcementPriority.Normal,
            "Receives standard reinforcement weight. The default sustainable allocation.", true);
        AddReinforcementPriorityButton(priority, "High", ReinforcementPriority.High,
            "Receives 1.90 reinforcement weight. Front-line formations recover first, starving lower priorities if reserves are scarce.");

        _armyOrders.AddChild(Title("ARMY ORGANISATION", 11, Ui.Colour("text.caption")));
        var grouping = new HBoxContainer();
        grouping.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(grouping);
        var newGroup = ActionButton("New army", CreateGroupFromSelection, 96, 30, true);
        newGroup.TooltipText = "Create a new organisational army headquarters and attach every selected division to it.";
        grouping.AddChild(newGroup);
        var nextGroup = ActionButton("Next HQ", AssignSelectionToNextGroup, 96, 30);
        nextGroup.TooltipText = "Move selected independent divisions to the next existing army headquarters.";
        grouping.AddChild(nextGroup);
        var ungroup = ActionButton("Ungroup", () => AssignSelectionToGroup(-1), 96, 30);
        ungroup.TooltipText = "Remove selected divisions from their current army while keeping them on the map.";
        grouping.AddChild(ungroup);

        var execution = new HBoxContainer();
        execution.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(execution);
        var prepare = ActionButton("Prepare plan", () => IssuePlanExecution(false), 145, 32);
        prepare.TooltipText = "Hold offensive formations on their assigned front so they entrench and accumulate planning.";
        execution.AddChild(prepare);
        var execute = ActionButton("Execute plan", () => IssuePlanExecution(true), 160, 32, true);
        execute.TooltipText = "Activate selected Advance and Spearhead plans. Defend and Reserve orders remain in place.";
        execution.AddChild(execute);

        var selection = new HBoxContainer();
        selection.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(selection);
        var selectAll = ActionButton("Select all", SelectAllPlayerArmies, 145, 32);
        selectAll.TooltipText = "Select every field army of your current country.";
        selection.AddChild(selectAll);
        var autoFront = ActionButton("Auto-deploy front", AutoDeployFront, 160, 32, true);
        autoFront.TooltipText = "Distribute the current selection across unique sectors of the active front. With no group selection, uses all armies.";
        selection.AddChild(autoFront);

        var formation = new HBoxContainer();
        formation.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(formation);
        var removeLead = ActionButton("Remove lead", RemoveLeadFromSelection, 145, 32);
        removeLead.TooltipText = "Remove the lead division from the current selection without disbanding or moving it.";
        formation.AddChild(removeLead);
        var region = ActionButton("Region details", ShowSelectedArmyRegion, 160, 32);
        region.TooltipText = "Open the demographic and economic inspector for this army's current region.";
        formation.AddChild(region);

        var selectionTools = new HBoxContainer();
        selectionTools.AddThemeConstantOverride("separation", 5);
        _armyOrders.AddChild(selectionTools);
        var selectArmy = ActionButton("Select HQ", SelectCurrentArmyGroup, 145, 30);
        selectArmy.TooltipText = "Select every independent division assigned to the lead division's army headquarters.";
        selectionTools.AddChild(selectArmy);
        var clear = ActionButton("Deselect", ClearArmySelectionAndClose, 160, 30);
        clear.TooltipText = "Clear the operational selection. Left-clicking the map also clears it.";
        selectionTools.AddChild(clear);
    }

    private void OnArmyPicked(Army army)
    {
        if (!_hudLayer.Visible || army is null) return;
        if (army.NationId == _campaign.Player.Id && Input.IsKeyPressed(Key.Shift))
        {
            if (_selectedArmies.Contains(army))
                _selectedArmies.Remove(army);
            else
                _selectedArmies.Add(army);
            if (_selectedArmies.Count == 0)
            {
                ClearArmySelectionAndClose();
                return;
            }
            _selectedArmy = _selectedArmies[0];
            _selectedNation = _campaign.Player;
            _globe.SelectArmies(_selectedArmies.Select(candidate => candidate.Id));
            ShowSelectedArmies();
            SetArmyFeedback("Selection updated. Shift-click another friendly division to add or remove it.");
            return;
        }
        _selectedArmy = army;
        _selectedArmies.Clear();
        _selectedArmies.Add(army);
        _selectedNation = _campaign.NationById(army.NationId);
        _globe.SelectArmy(army.Id);
        ShowSelectedArmy();
        SetArmyFeedback(army.NationId == _campaign.Player.Id
            ? "RMB: march or attack · Shift+RMB: append waypoint · Shift+click: add another division."
            : "Foreign formation: it can be inspected, but only its owner may issue orders.");
    }

    private void OnArmiesBoxPicked(IReadOnlyList<Army> armies)
    {
        if (!_hudLayer.Visible) return;
        var playerArmies = armies.Where(army => army.NationId == _campaign.Player.Id).Distinct().ToArray();
        if (playerArmies.Length == 0) return;
        if (!Input.IsKeyPressed(Key.Shift)) _selectedArmies.Clear();
        foreach (var army in playerArmies)
            if (!_selectedArmies.Contains(army)) _selectedArmies.Add(army);
        _selectedArmy = playerArmies[0];
        _selectedNation = _campaign.Player;
        _globe.SelectArmies(_selectedArmies.Select(army => army.Id));
        ShowSelectedArmies();
        SetArmyFeedback($"{_selectedArmies.Count} division(s) selected. RMB gives one shared destination; Shift+RMB queues a waypoint.");
    }

    private void ShowSelectedArmy()
    {
        if (_selectedArmy is null) return;
        _openScreen = "";
        var owner = _campaign.NationById(_selectedArmy.NationId);
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == _selectedArmy.StateMapIndex);
        var supplySource = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == _selectedArmy.SupplySourceStateMapIndex);
        var objective = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == _selectedArmy.ObjectiveStateMapIndex);
        var template = UnitTypeCatalog.Get(_selectedArmy.TemplateId);
        var armyGroup = _campaign.ArmyGroups.FirstOrDefault(group => group.Id == _selectedArmy.ArmyGroupId);
        _sidePanel.Visible = true;
        _militarySpending.Visible = false;
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
        _warControls.Visible = false;
        _battleDetailControls.Visible = false;
        _armyOrders.Visible = owner == _campaign.Player;
        _sideTitle.Text = _selectedArmy.Name;
        _sideBody.Text =
            $"{owner?.Name ?? "Unknown"} independent division\n" +
            $"{state?.Name ?? "Unmapped region"} · province #{_selectedArmy.ProvinceId:N0}\n\n" +
            $"Formation: {template.Name}\n" +
            $"Personnel: {_selectedArmy.Personnel:N0} / {_selectedArmy.MaxPersonnel:N0}\n" +
            $"Army: {armyGroup?.Name ?? "unassigned"}\n" +
            $"Organisation: {_selectedArmy.Organisation:P0}\n" +
            $"Strength: {_selectedArmy.Strength:P0}\n" +
            $"Equipment: {_selectedArmy.Equipment:P0} · experience: {_selectedArmy.Experience:P0}\n" +
            $"Cohesion: {_selectedArmy.Cohesion:P0} · recon: {_selectedArmy.Recon:P0}\n" +
            $"Engineers: {_selectedArmy.Engineers:P0} · artillery: {_selectedArmy.Artillery:P0}\n" +
            $"Supply: {_selectedArmy.SupplyLevel:P0}\n" +
            $"Supply hub: {supplySource?.Name ?? "none"} · distance {_selectedArmy.SupplyDistance}\n" +
            $"Throughput: {_selectedArmy.SupplyCongestion:P0}" + (_selectedArmy.CutOff ? " · CUT OFF\n" : "\n") +
            (_selectedArmy.LastReinforced > 0f ? $"Reinforced today: {_selectedArmy.LastReinforced:N0}\n" : "") +
            $"Entrenchment: {_selectedArmy.Entrenchment:P0}\n\n" +
            $"Planning: {_selectedArmy.Planning:P0}\n" +
            $"Posture: {StanceName(_selectedArmy.Stance)}\n\n" +
            $"Reinforcement priority: {ReinforcementDoctrine.Name(_selectedArmy.ReinforcementPriority)}\n\n" +
            (_selectedArmy.InBattle ? $"IN BATTLE #{_selectedArmy.BattleId}\n" : "") +
            $"Order: {OrderName(_selectedArmy.OrderKind)}\n" +
            (IsOffensive(_selectedArmy) ? $"Plan: {(_selectedArmy.PlanActive ? "EXECUTING" : "PREPARING")}\n" : "") +
            (objective is null ? "" : $"Offensive objective: {objective.Name}\n") +
            (_selectedArmy.IsMoving
                ? $"Route: {_selectedArmy.Route.Count} province(s) · leg {_selectedArmy.MoveProgress:P0}"
                : _selectedArmy.InBattle
                    ? "Movement orders can be queued and begin after the current battle."
                    : "Right-click friendly land to march or an enemy province to attack.");
        ApplyArmyPriorityButtonStates();
    }

    private void ShowSelectedArmies()
    {
        if (_selectedArmies.Count <= 1)
        {
            ShowSelectedArmy();
            return;
        }

        _openScreen = "";
        _sidePanel.Visible = true;
        _militarySpending.Visible = false;
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
        _warControls.Visible = false;
        _battleDetailControls.Visible = false;
        _armyOrders.Visible = true;
        _sideTitle.Text = $"{_selectedArmies.Count} DIVISIONS SELECTED";
        var groupCount = _selectedArmies.Select(army => army.ArmyGroupId).Distinct().Count();
        _sideBody.Text =
            $"{_campaign.Player.Name} operational selection\n\n" +
            $"Personnel: {_selectedArmies.Sum(army => army.Personnel):N0}\n" +
            $"Army headquarters represented: {groupCount}\n" +
            $"Average organisation: {_selectedArmies.Average(army => army.Organisation):P0}\n" +
            $"Average strength: {_selectedArmies.Average(army => army.Strength):P0}\n" +
            $"Average equipment: {_selectedArmies.Average(army => army.Equipment):P0}\n" +
            $"Average experience: {_selectedArmies.Average(army => army.Experience):P0}\n" +
            $"Average supply: {_selectedArmies.Average(army => army.SupplyLevel):P0}\n\n" +
            $"Average planning: {_selectedArmies.Average(army => army.Planning):P0}\n" +
            $"Reinforcement: {_selectedArmies.Count(army => army.ReinforcementPriority == ReinforcementPriority.High)} high · " +
            $"{_selectedArmies.Count(army => army.ReinforcementPriority == ReinforcementPriority.Normal)} normal · " +
            $"{_selectedArmies.Count(army => army.ReinforcementPriority == ReinforcementPriority.Low)} low\n" +
            $"Postures: {_selectedArmies.Count(army => army.Stance == ArmyStance.Cautious)} cautious · " +
            $"{_selectedArmies.Count(army => army.Stance == ArmyStance.Balanced)} balanced · " +
            $"{_selectedArmies.Count(army => army.Stance == ArmyStance.Aggressive)} aggressive\n\n" +
            $"Plans: {_selectedArmies.Count(army => IsOffensive(army) && !army.PlanActive)} preparing · " +
            $"{_selectedArmies.Count(army => IsOffensive(army) && army.PlanActive)} executing\n" +
            $"Cut off: {_selectedArmies.Count(army => army.CutOff)} · reserve formations: {_selectedArmies.Count(army => army.OrderKind == ArmyOrder.ReserveFront)}\n\n" +
            "Every entry is an independent division. Issue one order to the selection, attach it to an army, or right-click a province to move it.";
        ApplyArmyPriorityButtonStates();
    }

    private void CreateGroupFromSelection()
    {
        var targets = SelectedPlayerDivisions();
        if (targets.Length == 0) return;
        _campaign.Commands.Enqueue(new CreateArmyGroupCommand { IssuerId = _campaign.Player.Id });
        _campaign.Commands.ApplyAll(_campaign);
        var group = _campaign.ArmyGroups.Where(candidate => candidate.NationId == _campaign.Player.Id)
            .OrderByDescending(candidate => candidate.Id).FirstOrDefault();
        if (group is not null) AssignSelectionToGroup(group.Id);
    }

    private void AssignSelectionToNextGroup()
    {
        var targets = SelectedPlayerDivisions();
        var groups = _campaign.ArmyGroups.Where(group => group.NationId == _campaign.Player.Id)
            .OrderBy(group => group.Id).ToArray();
        if (targets.Length == 0 || groups.Length == 0) return;
        var current = System.Array.FindIndex(groups, group => group.Id == targets[0].ArmyGroupId);
        AssignSelectionToGroup(groups[(current + 1 + groups.Length) % groups.Length].Id);
    }

    private void AssignSelectionToGroup(int groupId)
    {
        foreach (var division in SelectedPlayerDivisions())
            _campaign.Commands.Enqueue(new AssignArmyGroupCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = division.Id,
                ArmyGroupId = groupId
            });
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedArmies();
    }

    private void RemoveLeadFromSelection()
    {
        if (_selectedArmies.Count <= 1) return;
        _selectedArmies.Remove(_selectedArmy);
        _selectedArmy = _selectedArmies[0];
        _globe.SelectArmies(_selectedArmies.Select(army => army.Id));
        ShowSelectedArmies();
    }

    private Army[] SelectedPlayerDivisions() => (_selectedArmies.Count > 0
            ? _selectedArmies : new List<Army> { _selectedArmy })
        .Where(army => army is not null && army.NationId == _campaign.Player.Id).Distinct().ToArray();

    private void AddReinforcementPriorityButton(HBoxContainer row, string caption, ReinforcementPriority priority,
        string tooltip, bool primary = false)
    {
        var button = ActionButton(caption, () => IssueReinforcementPriority(priority), 96, 30, primary);
        button.TooltipText = tooltip;
        row.AddChild(button);
        _armyPriorityButtons[priority] = button;
    }

    private void IssueReinforcementPriority(ReinforcementPriority priority)
    {
        var targets = _selectedArmies.Count > 0 ? _selectedArmies : new List<Army> { _selectedArmy };
        foreach (var army in targets.Where(army => army is not null && army.NationId == _campaign.Player.Id))
            _campaign.Commands.Enqueue(new SetArmyReinforcementPriorityCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = army.Id,
                Priority = priority
            });
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedArmies();
    }

    private void ApplyArmyPriorityButtonStates()
    {
        var targets = (_selectedArmies.Count > 0 ? _selectedArmies : new List<Army> { _selectedArmy })
            .Where(army => army is not null).ToArray();
        foreach (var (priority, button) in _armyPriorityButtons)
            ApplyLedgerButtonState(button, targets.Length > 0 &&
                targets.All(army => army.ReinforcementPriority == priority));
    }

    private void IssueArmyStance(ArmyStance stance)
    {
        var targets = _selectedArmies.Count > 0 ? _selectedArmies : new List<Army> { _selectedArmy };
        foreach (var army in targets.Where(army => army is not null && army.NationId == _campaign.Player.Id))
            _campaign.Commands.Enqueue(new SetArmyStanceCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = army.Id,
                Stance = stance
            });
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedArmies();
    }

    private void IssueArmyOrder(ArmyOrder order)
    {
        var targets = _selectedArmies.Count > 0 ? _selectedArmies : new List<Army> { _selectedArmy };
        foreach (var army in targets.Where(army => army is not null && army.NationId == _campaign.Player.Id))
        {
            _campaign.Commands.Enqueue(new SetArmyOrderCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = army.Id,
                Order = order,
                ObjectiveStateMapIndex = order == ArmyOrder.Spearhead &&
                    _selectedState is not null && _selectedState.NationMapIndex != _campaign.Player.MapIndex
                    ? _selectedState.MapIndex
                    : -1
            });
            if (order == ArmyOrder.AdvanceFront || order == ArmyOrder.Spearhead)
                _campaign.Commands.Enqueue(new SetArmyPlanExecutionCommand
                {
                    IssuerId = _campaign.Player.Id,
                    ArmyId = army.Id,
                    Active = true
                });
        }
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedArmies();
    }

    private void IssuePlanExecution(bool active)
    {
        var targets = _selectedArmies.Count > 0 ? _selectedArmies : new List<Army> { _selectedArmy };
        foreach (var army in targets.Where(army => army is not null && army.NationId == _campaign.Player.Id && IsOffensive(army)))
            _campaign.Commands.Enqueue(new SetArmyPlanExecutionCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = army.Id,
                Active = active
            });
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedArmies();
    }

    private void OnProvinceCommanded(GlobePick pick)
    {
        if (!_hudLayer.Visible || pick.ProvinceId <= 0) return;
        if (_selectedArmy is null)
            return;
        if (_selectedArmy.NationId != _campaign.Player.Id)
        {
            SetArmyFeedback("That formation is foreign. Select one of your own divisions before issuing an order.");
            return;
        }

        var targets = SelectedPlayerDivisions();
        var append = Input.IsKeyPressed(Key.Shift);
        var results = new Dictionary<ArmyMoveResult, int>();
        foreach (var army in targets)
        {
            var command = new MoveArmyCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = army.Id,
                TargetProvinceId = pick.ProvinceId,
                AppendWaypoint = append
            };
            _campaign.Commands.Enqueue(command);
            _campaign.Commands.ApplyAll(_campaign);
            results[command.Result] = results.GetValueOrDefault(command.Result) + 1;
        }
        ShowSelectedArmies();
        if (IsNetworkClient)
        {
            SetArmyFeedback($"Order for {targets.Length} division(s) submitted to the host. The authoritative route will appear after validation.");
            return;
        }
        SetArmyFeedback(MoveResultSummary(results, pick, append));
    }

    private string MoveResultSummary(IReadOnlyDictionary<ArmyMoveResult, int> results, GlobePick pick, bool append)
    {
        var destination = pick.State?.Name ?? $"province #{pick.ProvinceId:N0}";
        var parts = new List<string>();
        AddResult(parts, results, ArmyMoveResult.Issued, append ? "waypoint appended" : "moving");
        AddResult(parts, results, ArmyMoveResult.AttackIssued, "attacking through the nearest friendly front province");
        AddResult(parts, results, ArmyMoveResult.QueuedAfterBattle, "queued until battle ends");
        AddResult(parts, results, ArmyMoveResult.AlreadyAtDestination, "already there");
        AddResult(parts, results, ArmyMoveResult.TargetNotControlled, "destination is not controlled");
        AddResult(parts, results, ArmyMoveResult.NoLandRoute, "no connected friendly land route");
        AddResult(parts, results, ArmyMoveResult.NotControlled, "not under your control");
        AddResult(parts, results, ArmyMoveResult.InvalidTarget, "invalid destination");
        AddResult(parts, results, ArmyMoveResult.ArmyNotFound, "formation no longer exists");
        return $"Order to {destination}: " + (parts.Count == 0 ? "no eligible divisions." : string.Join(" · ", parts));
    }

    private static void AddResult(List<string> parts, IReadOnlyDictionary<ArmyMoveResult, int> results,
        ArmyMoveResult result, string caption)
    {
        if (results.TryGetValue(result, out var count) && count > 0) parts.Add($"{count} {caption}");
    }

    private void SetArmyFeedback(string text)
    {
        if (_armyCommandFeedback is not null) _armyCommandFeedback.Text = text;
    }

    private void SelectCurrentArmyGroup()
    {
        if (_selectedArmy is null || _selectedArmy.NationId != _campaign.Player.Id) return;
        var divisions = _selectedArmy.ArmyGroupId < 0
            ? new[] { _selectedArmy }
            : _campaign.Armies.Where(army => army.NationId == _campaign.Player.Id &&
                army.ArmyGroupId == _selectedArmy.ArmyGroupId).OrderBy(army => army.Id).ToArray();
        _selectedArmies.Clear();
        _selectedArmies.AddRange(divisions);
        _selectedArmy = divisions[0];
        _globe.SelectArmies(divisions.Select(army => army.Id));
        ShowSelectedArmies();
        SetArmyFeedback($"Selected all {divisions.Length} division(s) assigned to this headquarters.");
    }

    private void ClearArmySelectionAndClose()
    {
        _selectedArmy = null;
        _selectedArmies.Clear();
        _globe.SelectArmy(-1);
        _sidePanel.Visible = false;
        _openScreen = "";
    }

    private void SelectAllPlayerArmies()
    {
        OnArmiesBoxPicked(_campaign.Armies.Where(army => army.NationId == _campaign.Player.Id).ToArray());
    }

    private void AutoDeployFront()
    {
        var selected = _selectedArmies.Where(army => army.NationId == _campaign.Player.Id).Distinct().ToArray();
        var armies = (selected.Length > 0 ? selected : _campaign.Armies.Where(army => army.NationId == _campaign.Player.Id))
            .OrderByDescending(army => army.Power).ThenBy(army => army.Id).ToArray();
        if (armies.Length == 0) return;
        var war = _campaign.Wars.FirstOrDefault(candidate => !candidate.Ended && candidate.Involves(_campaign.Player.Id));
        var front = war?.FriendlyFront(_campaign.Player.Id).OrderBy(value => value).ToArray() ?? System.Array.Empty<int>();
        if (war is null || front.Length == 0)
        {
            ShowSelectedArmies();
            _sideBody.Text += "\n\nNo active land front is available for this country.";
            return;
        }
        _selectedArmies.Clear();
        _selectedArmies.AddRange(armies);
        _selectedArmy = armies[0];
        var spearheads = System.Math.Max(1, (int)System.MathF.Round(armies.Length * 0.12f));
        var advances = System.Math.Max(spearheads, (int)System.MathF.Round(armies.Length * 0.58f));
        var defenders = System.Math.Max(advances, (int)System.MathF.Round(armies.Length * 0.84f));
        for (var index = 0; index < armies.Length; index++)
        {
            var targetIndex = System.Math.Min(front.Length - 1,
                (int)System.MathF.Floor((index + 0.5f) / armies.Length * front.Length));
            var order = index < spearheads ? ArmyOrder.Spearhead :
                index < advances ? ArmyOrder.AdvanceFront :
                index < defenders ? ArmyOrder.DefendFront : ArmyOrder.ReserveFront;
            _campaign.Commands.Enqueue(new SetArmyOrderCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = armies[index].Id,
                ObjectiveStateMapIndex = war.ClaimedStateMapIndex,
                PreferredProvinceId = front[targetIndex],
                Order = order
            });
            _campaign.Commands.Enqueue(new SetArmyStanceCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = armies[index].Id,
                Stance = order == ArmyOrder.Spearhead ? ArmyStance.Aggressive :
                    order == ArmyOrder.DefendFront ? ArmyStance.Cautious : ArmyStance.Balanced
            });
            if (order == ArmyOrder.AdvanceFront || order == ArmyOrder.Spearhead)
                _campaign.Commands.Enqueue(new SetArmyPlanExecutionCommand
                {
                    IssuerId = _campaign.Player.Id,
                    ArmyId = armies[index].Id,
                    Active = false
                });
        }
        _campaign.Commands.ApplyAll(_campaign);
        _globe.SelectArmies(armies.Select(army => army.Id));
        ShowSelectedArmies();
    }

    private void SplitSelectedArmy()
    {
        if (_selectedArmy is null || _selectedArmy.NationId != _campaign.Player.Id) return;
        var before = _campaign.Armies.Count;
        _campaign.Commands.Enqueue(new SplitArmyCommand
        {
            IssuerId = _campaign.Player.Id,
            ArmyId = _selectedArmy.Id
        });
        _campaign.Commands.ApplyAll(_campaign);
        var detached = _campaign.Armies.Count > before
            ? _campaign.Armies.OrderByDescending(army => army.Id).FirstOrDefault()
            : null;
        if (detached is not null)
        {
            _selectedArmies.Clear();
            _selectedArmies.Add(_selectedArmy);
            _selectedArmies.Add(detached);
            _globe.SelectArmies(_selectedArmies.Select(army => army.Id));
            ShowSelectedArmies();
            _sideBody.Text += "\n\nFormation split successfully. Both independent halves remain selected and visible.";
        }
        else
        {
            ShowSelectedArmies();
            _sideBody.Text += "\n\nCannot split: at least two divisions and no active battle are required.";
        }
    }

    private void ShowSelectedArmyRegion()
    {
        if (_selectedArmy is null) return;
        _selectedState = _campaign.States.FirstOrDefault(state => state.MapIndex == _selectedArmy.StateMapIndex);
        _selectedProvinceId = _selectedArmy.ProvinceId;
        _selectedNation = _campaign.NationById(_selectedArmy.NationId);
        if (_selectedState is null || _selectedNation is null) return;
        _armyOrders.Visible = false;
        ShowRegionOverview();
    }

    private static string OrderName(ArmyOrder order) => order switch
    {
        ArmyOrder.DefendFront => "Defend front",
        ArmyOrder.AdvanceFront => "Advance front",
        ArmyOrder.ReserveFront => "Front reserve",
        ArmyOrder.Spearhead => "Spearhead",
        _ => "Manual"
    };

    private static string StanceName(ArmyStance stance) => stance switch
    {
        ArmyStance.Cautious => "Cautious",
        ArmyStance.Aggressive => "Aggressive",
        _ => "Balanced"
    };

    private static bool IsOffensive(Army army) => army is not null &&
        (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead);
}
