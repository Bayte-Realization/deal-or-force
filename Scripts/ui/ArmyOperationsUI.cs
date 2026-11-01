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

    /// <summary>
    /// The divisions whose orders the region dossier was opened from. Asking where the lead
    /// division stands is not the end of giving it orders, so the dossier carries a way back
    /// to them - the button in its header, or Escape - for as long as it shows that ground.
    /// </summary>
    private int[] _dossierReturnArmyIds;
    private int _dossierReturnLeadId = -1;
    private Button _sideBackToOrders;

    /// <summary>Shift means "these as well".</summary>
    private static bool AddingToSelection =>
        Input.IsKeyPressed(Key.Shift) && !Input.IsKeyPressed(Key.Ctrl);

    /// <summary>Control means "not these".</summary>
    private static bool RemovingFromSelection => Input.IsKeyPressed(Key.Ctrl);

    /// <summary>
    /// Whether the next right-click puts the selection on the railways.
    ///
    /// A mode rather than a modifier because Control is already spoken for on the right
    /// button - it means "help with this attack" - and because redeployment is a decision
    /// about a whole move rather than about one click. Ctrl+B arms it, the next order
    /// spends it, Escape or a second Ctrl+B puts it away.
    /// </summary>
    private bool _redeploymentArmed;

    private void OnArmyPicked(Army army)
    {
        if (!_hudLayer.Visible || !ArmyInteraction.CanSelect(_campaign, army)) return;
        CloseFleetPanel();
        if (_supportLayer is { Visible: true }) CloseSupportScreen();
        _globe.SelectSupportFormation(-1);
        // Shift adds, control takes away - the Paradox reading, and the one that lets a
        // player build a selection without fear. A single key that toggled did both, so
        // shift-clicking a stack you had half-selected removed the ones you wanted and
        // added the ones you did not, and there was no way to say "this one, definitely".
        if (army.NationId == _campaign.Player.Id && (AddingToSelection || RemovingFromSelection))
        {
            if (RemovingFromSelection) _selectedArmies.Remove(army);
            else if (!_selectedArmies.Contains(army)) _selectedArmies.Add(army);
            if (_selectedArmies.Count == 0)
            {
                ClearArmySelectionAndClose();
                return;
            }
            _selectedArmy = _selectedArmies[0];
            _selectedNation = _campaign.Player;
            _globe.SelectArmies(_selectedArmies.Select(candidate => candidate.Id));
            ShowSelectedArmies();
            SetArmyFeedback(Simulation.Loc.Format(
                                Simulation.Plural.Key("{0:N0} division selected.", _selectedArmies.Count),
                                _selectedArmies.Count) + " " +
                            Simulation.Loc.Get("Shift-click adds, Ctrl-click removes."));
            return;
        }
        _selectedArmy = army;
        _selectedArmies.Clear();
        _selectedArmies.Add(army);
        _selectedNation = _campaign.NationById(army.NationId);
        _globe.SelectArmy(army.Id);
        ShowSelectedArmy();
        SetArmyFeedback((army.InBattle ? Tr("In combat: H or RMB on the formation's current province leaves battle immediately. Other map orders wait; Alt+H cancels only the queue.") + " " : "") + Tr("RMB march or attack · Shift+RMB append a waypoint · Ctrl+RMB support the attack without taking " +
              "the ground · Shift+click adds a division, Ctrl+click removes one.  " +
              "H halt · Ctrl+H off the plan · Shift+H back on it · Ctrl+B rail them there · Ctrl+K consolidate · " +
              "Ctrl+G guard an area · Ctrl+F plan cohesion."));
    }

    private void OnArmiesBoxPicked(IReadOnlyList<Army> armies)
    {
        if (!_hudLayer.Visible) return;
        var playerArmies = armies.Where(army => ArmyInteraction.CanSelect(_campaign, army)).Distinct().ToArray();
        if (playerArmies.Length == 0) return;
        // Aggregated-card clicks arrive here too, not through OnArmyPicked.
        // They must replace the previous military inspector just like a single click.
        CloseFleetPanel();
        if (_supportLayer is { Visible: true }) CloseSupportScreen();
        _globe.SelectSupportFormation(-1);

        // A box does the same three things a click does: replace, add, or take away.
        if (RemovingFromSelection)
        {
            foreach (var army in playerArmies) _selectedArmies.Remove(army);
            if (_selectedArmies.Count == 0)
            {
                ClearArmySelectionAndClose();
                return;
            }
        }
        else
        {
            if (!AddingToSelection) _selectedArmies.Clear();
            foreach (var army in playerArmies)
                if (!_selectedArmies.Contains(army)) _selectedArmies.Add(army);
        }
        _selectedArmy = _selectedArmies[0];
        _selectedNation = _campaign.Player;
        _globe.SelectArmies(_selectedArmies.Select(army => army.Id));
        ShowSelectedArmies();
        SetArmyFeedback(Simulation.Loc.Format(
                            Simulation.Plural.Key("{0:N0} division selected.", _selectedArmies.Count),
                            _selectedArmies.Count) + " " +
                        Simulation.Loc.Get("RMB gives one shared destination; Shift+RMB queues a waypoint."));
    }

    /// <summary>
    /// One division selected. The roster shows it alone; the region inspector steps aside,
    /// because a player looking at a division wants the division, not the province it is
    /// standing in with the division mentioned underneath.
    /// </summary>
    private void ShowSelectedArmy()
    {
        if (_selectedArmy is null) return;
        ForgetDossierReturn();
        _openScreen = "";
        _sidePanel.Visible = false;
        _militarySpending.Visible = false;
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
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
        ForgetDossierReturn();
        _openScreen = "";
        _sidePanel.Visible = false;
        _militarySpending.Visible = false;
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
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
        .Where(army => ArmyInteraction.CanSelect(_campaign, army)).Distinct().ToArray();

    private void AddReinforcementPriorityButton(HBoxContainer row, string caption, ReinforcementPriority priority,
        string tooltip, bool primary = false)
    {
        var button = ActionButton(caption, () => IssueReinforcementPriority(priority), 96, 30, primary);
        Tip(button, tooltip);
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
        if (order == ArmyOrder.Manual) { HaltSelectedDivisions(); return; }
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

    /// <summary>
    /// Open the diplomacy screen with whoever owns a province, if that is somebody else.
    ///
    /// Right-clicking a country to see where you stand with it is the thing a player reaches
    /// for. With no formation selected, own ground opens one's own dossier too.
    /// A selected formation keeps the right button for movement, never diplomacy.
    /// </summary>
    private void OpenDiplomacyWithOwnerOf(int provinceId)
    {
        var topology = _campaign?.ProvinceMap;
        var player = _campaign?.Player;
        if (topology is null || player is null) return;
        if (provinceId <= 0 || provinceId > topology.ProvinceCount) return;

        var owner = topology.ProvinceToNation[provinceId];
        // Unowned water still has no country dossier.
        if (owner == 0)
        {
            _globe?.HighlightNation(-1);
            return;
        }

        var nation = _campaign.Nations.Find(candidate => candidate.MapIndex == owner);
        if (nation is null) return;
        // Say which country, on the map, before saying anything about it in a window. A
        // screen that has just changed to name a country without showing where it is has
        // answered a question the player did not ask.
        _globe?.HighlightNation(nation.MapIndex);
        _selectedNation = nation;
        OpenScreen("Diplomacy");
    }

    private void OnProvinceCommanded(GlobePick pick)
    {
        if (!_hudLayer.Visible) return;

        if (_supportLayer is { Visible: true })
        {
            if (_supportSelected.Count > 0 && pick.State is not null) RebaseSupportSelection(pick.State);
            return;
        }

        // A fleet is ordered to a place rather than to a province - most of the sea is not
        // one - so it is handled before the province guard below rejects open water.
        if (_selectedFleet is not null &&
            SendSelectedFleet(pick.Latitude, pick.Longitude, pick.ProvinceId)) return;

        if (pick.ProvinceId <= 0) return;

        // Nothing selected to command, so the right button is free to mean the other
        // obvious thing: what can be done about the ground under the cursor - its region,
        // its owner's diplomacy, a network, a reconnaissance flight (ProvinceContextMenuUI).
        //
        // It cannot mean both. An army or a fleet selected keeps the order - that is the
        // convention every game of this shape uses and the one a player's hands already
        // know - and this only takes the case where the click did nothing at all.
        if (_selectedArmy is null)
        {
            ShowProvinceContextMenu(pick);
            return;
        }
        if (_selectedArmy.NationId != _campaign.Player.Id)
        {
            SetArmyFeedback(Tr("That formation is foreign. Select one of your own divisions before issuing an order."));
            return;
        }

        var targets = SelectedPlayerDivisions();
        var append = Input.IsKeyPressed(Key.Shift);
        // Control on the right button is the support attack: fight for the province and
        // stay where you are. It cannot also mean "append", so a modified click is one
        // order rather than a queued one.
        var supporting = Input.IsKeyPressed(Key.Ctrl) && !append;
        var railing = _redeploymentArmed && !append && !supporting;
        var withdrawalFeedback = "";
        // Returning an order to the formation's own position is an explicit stop, like
        // H, not a new destination. Only the selected formations on that tile leave;
        // ordinary orders elsewhere still wait for combat to finish.
        if (!append && !supporting && !railing)
        {
            var leaving = targets.Where(army => army.InBattle && army.ProvinceId == pick.ProvinceId)
                .Select(army => army.Id).ToHashSet();
            if (leaving.Count > 0)
            {
                ProcessHaltTargets(leaving.ToArray(), true);
                withdrawalFeedback = _armyCommandFeedback?.Text ?? "";
                targets = targets.Where(army => !leaving.Contains(army.Id)).ToArray();
                if (targets.Length == 0) return;
            }
        }
        var results = new Dictionary<ArmyMoveResult, int>();
        foreach (var army in targets)
        {
            if (railing)
            {
                var rail = new StrategicRedeploymentCommand
                {
                    IssuerId = _campaign.Player.Id,
                    ArmyId = army.Id,
                    TargetProvinceId = pick.ProvinceId
                };
                _campaign.Commands.Enqueue(rail);
                _campaign.Commands.ApplyAll(_campaign);
                results[rail.Result] = results.GetValueOrDefault(rail.Result) + 1;
                continue;
            }

            var command = new MoveArmyCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = army.Id,
                TargetProvinceId = pick.ProvinceId,
                AppendWaypoint = append,
                SupportAttack = supporting
            };
            _campaign.Commands.Enqueue(command);
            _campaign.Commands.ApplyAll(_campaign);
            results[command.Result] = results.GetValueOrDefault(command.Result) + 1;

            // Support is applied atomically by MoveArmyCommand after validation. A
            // rejected click must not change the previously executing order's mode.
        }
        if (railing) _redeploymentArmed = false;
        ShowSelectedArmies();
        if (IsNetworkClient)
        {
            SetArmyFeedback(Simulation.Loc.Format(
                Simulation.Plural.Key("Order for {0:N0} division submitted to the host.", targets.Length),
                targets.Length) + " " +
                Simulation.Loc.Get("The host will confirm the route."));
            return;
        }
        SetArmyFeedback((withdrawalFeedback.Length > 0 ? withdrawalFeedback + " · " : "") +
                        MoveResultSummary(results, pick, append) +
                        (supporting && targets.Any(army => army.SupportOnly)
                            ? " · " + Simulation.Loc.Get("supporting: they will fight and hold their ground.") : "") +
                        (railing
                            ? " · " + Simulation.Loc.Get("by rail: fast, and they will arrive shaken.") : ""));
    }

    private string MoveResultSummary(IReadOnlyDictionary<ArmyMoveResult, int> results, GlobePick pick, bool append)
    {
        var destination = pick.State is null
            ? Simulation.Loc.Format("Province #{0:N0}", pick.ProvinceId)
            : pick.State.DisplayName;
        var parts = new List<string>();
        AddResult(parts, results, ArmyMoveResult.Issued, append ? "waypoint appended" : "moving");
        AddResult(parts, results, ArmyMoveResult.AttackIssued, "advancing along the marked route; defended provinces require a battle");
        AddResult(parts, results, ArmyMoveResult.WithdrawnAndMoving, "disengaged from battle and moving");
        AddResult(parts, results, ArmyMoveResult.RouteInterrupted, "route blocked");
        AddResult(parts, results, ArmyMoveResult.QueuedAfterBattle, "queued until battle ends");
        AddResult(parts, results, ArmyMoveResult.OrderCancelled, "order cancelled");
        AddResult(parts, results, ArmyMoveResult.AlreadyAtDestination, "already there");
        AddResult(parts, results, ArmyMoveResult.TargetNotControlled, "neutral territory: no military access or active war");
        AddResult(parts, results, ArmyMoveResult.NoLandRoute, "no way through by land");
        AddResult(parts, results, ArmyMoveResult.NotControlled, "not under your control");
        AddResult(parts, results, ArmyMoveResult.InvalidTarget, "invalid destination");
        AddResult(parts, results, ArmyMoveResult.ArmyNotFound, "formation no longer exists");
        AddResult(parts, results, ArmyMoveResult.QueueFull, "post-battle queue is full (32 waypoints)");
        return Simulation.Loc.Format("Order to {0}: {1}", destination,
            parts.Count == 0 ? Simulation.Loc.Get("no eligible divisions.") : string.Join(" · ", parts));
    }

    private static void AddResult(List<string> parts, IReadOnlyDictionary<ArmyMoveResult, int> results,
        ArmyMoveResult result, string caption)
    {
        // The caption arrives as the English key its call site spells, so the ten reasons a
        // move can fail are looked up here rather than translated ten times over.
        if (results.TryGetValue(result, out var count) && count > 0)
            parts.Add(count.ToString("N0") + " " + Simulation.Loc.Get(caption));
    }

    /// <summary>
    /// Stop marching, come off the plan, go back on it - three separate things.
    ///
    /// They used to be one button whose effect the player could not predict. "Halt" that
    /// also silently took a division off its front is how a carefully drawn line loses a
    /// quarter of its strength without anybody noticing; "halt" that also handed it back to
    /// the planner is how a hand-driven attack gets marched away from its objective on the
    /// next tick. So each is its own key and each says what it did.
    /// </summary>
    private void HaltSelectedDivisions()
        => ProcessHaltSelected(false);

    private void WithdrawSelectedDivisions()
        => ProcessHaltSelected(true);

    private void CancelSelectedQueuedOrders()
    {
        var cancelled = 0;
        foreach (var army in SelectedPlayerDivisions().Where(army => army.InBattle))
        {
            var command = new HaltArmyCommand { IssuerId = _campaign.Player.Id, ArmyId = army.Id };
            _campaign.Commands.Enqueue(command);
            _campaign.Commands.ApplyAll(_campaign);
            if (command.Halted) cancelled++;
        }
        ShowSelectedArmies();
        RefreshBattleDossier();
        SetArmyFeedback(IsNetworkClient ? Tr("Order sent to the host. Awaiting confirmation.") :
            Loc.Format("Cancelled post-battle orders: {0}. Engaged formations keep fighting.", cancelled));
    }

    private void ProcessHaltSelected(bool battlesOnly)
    {
        var targets = SelectedPlayerDivisions().Where(army => !battlesOnly || army.InBattle).ToArray();
        if (battlesOnly)
        {
            var ids = targets.Select(army => army.Id).ToArray();
            ConfirmLastDefenderWithdrawal(ids, 1f, () => ProcessHaltTargets(ids, true));
            return;
        }
        ProcessHaltTargets(targets.Select(army => army.Id).ToArray(), false);
    }

    private void ProcessHaltTargets(int[] ids, bool battlesOnly)
    {
        var targets = _campaign.Armies.Where(army => ids.Contains(army.Id) &&
            army.NationId == _campaign.Player.Id && (!battlesOnly || army.InBattle)).ToArray();
        if (targets.Length == 0) return;
        var stopped = 0;
        var withdrawn = 0;
        var blocked = 0;
        var pending = 0;
        foreach (var army in targets)
        {
            // H/Stop is the player's explicit request to stop this fight. Keep the
            // simulation's internal Halt command non-destructive: planners and the
            // queue-only shortcut still use it without accidentally abandoning combat.
            if (army.InBattle)
            {
                var withdrawal = new WithdrawArmyFromBattleCommand { IssuerId = _campaign.Player.Id, ArmyId = army.Id };
                _campaign.Commands.Enqueue(withdrawal);
                _campaign.Commands.ApplyAll(_campaign);
                if (!withdrawal.Applied) { pending++; continue; }
                if (withdrawal.Withdrawn) withdrawn++; else blocked++;
                continue;
            }
            var command = new HaltArmyCommand { IssuerId = _campaign.Player.Id, ArmyId = army.Id };
            _campaign.Commands.Enqueue(command);
            _campaign.Commands.ApplyAll(_campaign);
            if (command.Halted) stopped++;
        }
        ShowSelectedArmies();
        if (pending > 0)
        {
            SetArmyFeedback(Tr("Order sent to the host. Awaiting confirmation."));
            return;
        }
        if (withdrawn > 0 || blocked > 0)
        {
            SetArmyFeedback(Loc.Format("Left battle: {0} · stopped: {1} · no retreat route: {2}", withdrawn, stopped, blocked));
            RefreshBattleDossier();
            return;
        }
        SetArmyFeedback(stopped == 0
            ? Simulation.Loc.Format(
                Simulation.Plural.Key("{0:N0} division was already standing still. They keep their plan.",
                    targets.Length), targets.Length)
            : Simulation.Loc.Format(
                Simulation.Plural.Key("{0:N0} division stopped where they stand.", stopped), stopped) + " " +
              Simulation.Loc.Get("They are still on their plan - Ctrl+H takes them off it."));
    }

    private void DetachSelectedDivisions()
    {
        var targets = SelectedPlayerDivisions();
        if (targets.Length == 0) return;
        var taken = 0;
        foreach (var army in targets)
        {
            var command = new DetachArmyFromPlanCommand { IssuerId = _campaign.Player.Id, ArmyId = army.Id };
            _campaign.Commands.Enqueue(command);
            _campaign.Commands.ApplyAll(_campaign);
            if (command.Detached) taken++;
        }
        ShowSelectedArmies();
        SetArmyFeedback(taken == 0
            ? Simulation.Loc.Format(
                Simulation.Plural.Key("{0:N0} division was already under hand control.", targets.Length),
                targets.Length)
            : Simulation.Loc.Format(
                Simulation.Plural.Key("{0:N0} division taken off their plan.", taken), taken) + " " +
              Simulation.Loc.Get("Nothing will move them but you, and the front no longer counts them - Shift+H hands them back."));
    }

    private void ReturnSelectedDivisionsToPlan()
    {
        var targets = SelectedPlayerDivisions();
        if (targets.Length == 0) return;
        foreach (var army in targets)
        {
            _campaign.Commands.Enqueue(new ReturnArmyToPlanCommand
            {
                IssuerId = _campaign.Player.Id, ArmyId = army.Id
            });
            _campaign.Commands.ApplyAll(_campaign);
        }
        ShowSelectedArmies();
        SetArmyFeedback(Simulation.Loc.Format(
                            Simulation.Plural.Key("{0:N0} division handed back to the plan.", targets.Length),
                            targets.Length) + " " +
                        Simulation.Loc.Get("They will hold the line until the operation orders them forward."));
    }

    /// <summary>
    /// Step the cohesion of the line the selection is standing on.
    ///
    /// One setting per plan rather than per formation, so this reads the line the first
    /// selected division is on and moves everybody on it together - which is what the
    /// setting means. A player who wants one division exempt takes that division off the
    /// plan, which is Ctrl+H and a different idea.
    /// </summary>
    private void CyclePlanCohesion()
    {
        var army = SelectedPlayerDivisions().FirstOrDefault(candidate => candidate.FrontLineId >= 0);
        if (army is null)
        {
            SetArmyFeedback(Tr("None of the selected divisions is on a drawn plan, so there is no cohesion to set."));
            return;
        }
        var line = _campaign.FrontLines.Find(candidate => candidate.Id == army.FrontLineId);
        if (line is null) return;

        var next = line.Cohesion switch
        {
            PlanCohesion.Flexible => PlanCohesion.Balanced,
            PlanCohesion.Balanced => PlanCohesion.Rigid,
            PlanCohesion.Rigid => PlanCohesion.Immobile,
            _ => PlanCohesion.Flexible
        };
        _campaign.Commands.Enqueue(new SetPlanCohesionCommand
        {
            IssuerId = _campaign.Player.Id,
            FrontLineId = line.Id,
            Cohesion = next
        });
        _campaign.Commands.ApplyAll(_campaign);
        SetArmyFeedback(Simulation.Loc.Format("Plan cohesion: {0}.", CohesionName(next)) + " " +
            Simulation.Loc.Get(next switch
            {
                PlanCohesion.Flexible => "Whoever is best, from anywhere on the line - it will close holes fast and its divisions will spend a lot of the war walking sideways.",
                PlanCohesion.Balanced => "The planner prefers a formation's neighbours. Right nearly always.",
                PlanCohesion.Rigid => "Only the next post along, and only when it is empty. Dispositions hold; a hole two provinces away stays open.",
                _ => "Nobody moves. Where they are put is where they stay, and the plan will not shuffle them."
            }));
    }

    /// <summary>Arm or disarm the railways for the next order.</summary>
    private void ToggleStrategicRedeployment()
    {
        _redeploymentArmed = !_redeploymentArmed;
        SetArmyFeedback(_redeploymentArmed
            ? "Strategic redeployment armed: the next right-click rails the selection there through your own " +
              "territory - about three times a marching pace, paid for in organisation. Ctrl+B again to cancel."
            : "Strategic redeployment cancelled. The next right-click is an ordinary march.");
    }

    /// <summary>
    /// Fold the selected formations into each other.
    ///
    /// The point is not a bigger division - it is fewer of them. Supply is charged per
    /// formation, so six divisions at a third strength eat like six and fight like two;
    /// consolidating makes them two that eat like two.
    /// </summary>
    private void ConsolidateSelectedDivisions()
    {
        var targets = SelectedPlayerDivisions();
        if (targets.Length < 2)
        {
            SetArmyFeedback(Tr("Select at least two formations of the same kind, standing together, to consolidate."));
            return;
        }
        var command = new ConsolidateArmiesCommand
        {
            IssuerId = _campaign.Player.Id,
            ArmyIds = targets.Select(army => army.Id).ToArray()
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);

        // The selection may have just lost members to the merge.
        _selectedArmies.RemoveAll(army => !_campaign.Armies.Contains(army));
        if (_selectedArmies.Count == 0)
        {
            ClearArmySelectionAndClose();
        }
        else
        {
            _selectedArmy = _selectedArmies[0];
            _globe.SelectArmies(_selectedArmies.Select(candidate => candidate.Id));
            ShowSelectedArmies();
        }
        SetArmyFeedback(command.Merged > 0
            ? Simulation.Loc.Format(
                Simulation.Plural.Key("{0:N0} formation folded in; {1:N0} men kept.", command.Merged),
                command.Merged, command.PersonnelKept) + " " +
              Simulation.Loc.Get("They are now in fewer divisions, which is fewer mouths for the supply network to reach.")
            : command.Refusal);
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
        SetArmyFeedback(Simulation.Loc.Format(
            Simulation.Plural.Key("Selected all {0:N0} division assigned to this headquarters.", divisions.Length),
            divisions.Length));
    }

    private void ClearArmySelectionAndClose()
    {
        _selectedArmy = null;
        _selectedArmies.Clear();
        _globe.SelectArmy(-1);
        SetArmyPanelVisible(false);
        _redeploymentArmed = false;
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
        // Every war's stretch of our own front, not only the first war's: a country fighting an
        // alliance in two wars spreads the selection over both, and each division is told which
        // war its sector belongs to so the order does not snap it back to the nearest one.
        var player = _campaign.Player;
        var controllers = _campaign.ProvinceController;
        var sectorWar = new Dictionary<int, StrategicWar>();
        foreach (var candidate in _campaign.Wars)
        {
            if (candidate.Ended || !candidate.Involves(player.Id)) continue;
            foreach (var province in candidate.FriendlyFront(player.Id))
                if (controllers is not null && province < controllers.Length &&
                    controllers[province] == player.MapIndex) sectorWar.TryAdd(province, candidate);
        }
        var front = sectorWar.Keys.OrderBy(value => value).ToArray();
        var war = front.Length > 0 ? sectorWar[front[0]] : null;
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
            var sector = sectorWar[front[targetIndex]];
            var order = index < spearheads ? ArmyOrder.Spearhead :
                index < advances ? ArmyOrder.AdvanceFront :
                index < defenders ? ArmyOrder.DefendFront : ArmyOrder.ReserveFront;
            _campaign.Commands.Enqueue(new SetArmyOrderCommand
            {
                IssuerId = _campaign.Player.Id,
                ArmyId = armies[index].Id,
                WarId = sector.Id,
                ObjectiveStateMapIndex = sector.ClaimedStateMapIndex,
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
            _sideBody.Text += "\n\n" + Simulation.Loc.Get("Split in two.");
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
        // This is an inspector shortcut, not an army order. The dossier lives in the
        // generic side panel, so hand the corner over to it instead of hiding the
        // command tabs inside a still-visible army panel (which left a blank deck).
        var returnIds = (_selectedArmies.Count > 0 ? _selectedArmies : new List<Army> { _selectedArmy })
            .Where(army => army is not null).Select(army => army.Id).ToArray();
        var leadId = _selectedArmy.Id;
        OpenSelectedRegionDossier();
        _dossierReturnArmyIds = returnIds;
        _dossierReturnLeadId = leadId;
        if (_sideBackToOrders is not null) _sideBackToOrders.Visible = true;
    }

    /// <summary>Back from the region dossier to the orders of the divisions it was opened from.</summary>
    private void ReturnToArmyOrders()
    {
        var ids = _dossierReturnArmyIds;
        var leadId = _dossierReturnLeadId;
        ForgetDossierReturn();
        if (ids is null || _campaign is null) return;
        var armies = ids.Select(id => _campaign.Armies.FirstOrDefault(army => army.Id == id))
            .Where(army => ArmyInteraction.CanSelect(_campaign, army)).ToList();
        // Every one of them gone while the dossier was read - destroyed, merged, disbanded:
        // there are no orders to go back to, so the dossier simply closes.
        if (armies.Count == 0) { CloseSidePanel(); return; }
        _selectedArmies.Clear();
        _selectedArmies.AddRange(armies);
        _selectedArmy = armies.FirstOrDefault(army => army.Id == leadId) ?? armies[0];
        _globe.SelectArmies(armies.Select(army => army.Id));
        ShowSelectedArmies();
    }

    /// <summary>The dossier is showing something else now: the way back no longer leads anywhere.</summary>
    private void ForgetDossierReturn()
    {
        _dossierReturnArmyIds = null;
        _dossierReturnLeadId = -1;
        if (_sideBackToOrders is not null) _sideBackToOrders.Visible = false;
    }

    /// <summary>How tightly a plan is allowed to shuffle its divisions, in words.</summary>
    private static string CohesionName(PlanCohesion cohesion) => Simulation.Loc.Get(cohesion switch
    {
        PlanCohesion.Flexible => "Flexible",
        PlanCohesion.Balanced => "Balanced plan",
        PlanCohesion.Rigid => "Rigid",
        _ => "Static"
    });

    // The words themselves live with the orders, in Simulation/ArmyVocabulary.cs, so that
    // the map tooltip and this panel cannot disagree about what a division is doing.
    private static string OrderName(ArmyOrder order) => ArmyVocabulary.OrderName(order);

    private static string StanceName(ArmyStance stance) => ArmyVocabulary.StanceName(stance);

    private static bool IsOffensive(Army army) => army is not null &&
        (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead);
}
