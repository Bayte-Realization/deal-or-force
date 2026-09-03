using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _armyOrders = null!;
    private Army _selectedArmy;
    private readonly List<Army> _selectedArmies = new();
    private readonly Dictionary<ReinforcementPriority, Button> _armyPriorityButtons = new();
    private Label _armyCommandFeedback = null!;

    private void OnArmyPicked(Army army)
    {
        if (!_hudLayer.Visible || army is null) return;
        CloseFleetPanel();
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

    /// <summary>
    /// One division selected. The roster shows it alone; the region inspector steps aside,
    /// because a player looking at a division wants the division, not the province it is
    /// standing in with the division mentioned underneath.
    /// </summary>
    private void ShowSelectedArmy()
    {
        if (_selectedArmy is null) return;
        _openScreen = "";
        _sidePanel.Visible = false;
        _militarySpending.Visible = false;
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
        _warControls.Visible = false;
        _battleDetailControls.Visible = false;
        UpdateScreenButtons();
        RefreshArmyPanel();
    }

    private void ShowSelectedArmies()
    {
        if (_selectedArmies.Count <= 1 && _selectedArmy is not null)
        {
            ShowSelectedArmy();
            return;
        }
        _openScreen = "";
        _sidePanel.Visible = false;
        _militarySpending.Visible = false;
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
        _warControls.Visible = false;
        _battleDetailControls.Visible = false;
        UpdateScreenButtons();
        RefreshArmyPanel();
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
        if (!_hudLayer.Visible) return;

        // A fleet is ordered to a place rather than to a province - most of the sea is not
        // one - so it is handled before the province guard below rejects open water.
        if (_selectedFleet is not null && SendSelectedFleet(pick.Latitude, pick.Longitude)) return;

        if (pick.ProvinceId <= 0) return;
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
