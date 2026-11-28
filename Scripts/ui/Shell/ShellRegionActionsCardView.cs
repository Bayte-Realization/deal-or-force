using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Platform;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Renders the Sovereign Brass «Region actions» card on the Wave-2 Region Card Overview tab,
/// surfacing only context-applicable regional actions: Held-ground Referendum, Allied Occupation
/// Request &amp; Transfer, Land Arrangements (Lease / Send Troops / Withdraw / Proclaim Republic),
/// Kin Statecraft, Colonisation, Regional Autonomy, Conciliation, Reconstruction, Move Capital,
/// Movements, and Regional Laws. Strictly uses Ui.Colour tokens and ImmediateCampaignCommands.
/// </summary>
internal static class ShellRegionActionsCardView
{
    public static void AppendRegionActionsCard(
        ShellContext ctx, VBoxContainer bodyRows, Label feedbackLabel, Campaign world,
        Nation owner, ScenarioState state, bool isOwn, Action onRefresh)
    {
        if (ctx is null || bodyRows is null || world is null || state is null) return;
        var observer = ObserverUiRules.IsObserving(world, ctx.IsObserverMode?.Invoke() ?? world.ObserverMode);
        var player = world.Player;
        if (observer || player is null) return;

        var content = new VBoxContainer { Name = "ShellRegionActionsContent", ClipContents = true };
        content.AddThemeConstantOverride("separation", 6);

        var isTouch = ctx.IsTouchMode?.Invoke() ?? (PlatformProfile.Current?.IsTouchPrimary == true);
        var btnH = isTouch ? 38 : 28;
        var added = 0;

        if (ColonisationCore.IsUnclaimed(state))
        {
            added += AppendColonisationActions(ctx, content, feedbackLabel, world, player, state, btnH, onRefresh);
        }
        else if (isOwn && owner is not null)
        {
            added += AppendOwnRegionActions(ctx, content, feedbackLabel, world, player, state, isTouch, btnH, onRefresh);
            added += AppendMovementActions(ctx, content, feedbackLabel, world, player, state, isOwn: true, btnH, onRefresh);
        }
        else
        {
            added += AppendHeldAndOccupationActions(ctx, content, feedbackLabel, world, player, state, isTouch, btnH, onRefresh);
            added += AppendForeignArrangementActions(ctx, content, feedbackLabel, world, player, owner, state, isTouch, btnH, onRefresh);
            added += AppendKinStatecraftActions(ctx, content, feedbackLabel, world, player, owner, state, btnH, onRefresh);
            added += AppendMovementActions(ctx, content, feedbackLabel, world, player, state, isOwn: false, btnH, onRefresh);
        }

        if (added == 0)
        {
            content.QueueFree();
            return;
        }

        ShellRegionVisualsView.AddSectionHeader(ctx, bodyRows, Loc.Get("shell.reg_act.header"));
        var card = new PanelContainer
        {
            Name = "ShellRegionActionsCard",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.plate_bottom", "shell.brass", 1, 3, 8, 7, 8, 7));
        card.AddChild(content);
        bodyRows.AddChild(card);
    }

    private static int AppendHeldAndOccupationActions(
        ShellContext ctx, VBoxContainer parent, Label feedbackLabel, Campaign world,
        Nation player, ScenarioState state, bool isTouch, int btnH, Action onRefresh)
    {
        var count = 0;
        var flow = CreateButtonFlow();

        // 1. Held-ground Referendum (annex held ground during war)
        if (world.FeatureEnabled("land_arrangements") && state.HeldById == player.Id && HeldRegions.Administered(world, state))
        {
            var refusal = HeldRegions.ReferendumRefusal(world, player, state);
            var key = $"held_referendum:{state.MapIndex}";
            var explain = Loc.Get("A vote on joining us in the region our troops hold, in the middle of the war. Its owner will not recognise it and the world will count it against us; the region becomes ours in law.");
            flow.AddChild(MakeActionButton(ctx, Loc.Get("Hold a referendum"), refusal, explain, isTouch, btnH,
                confirmKey: key, primary: true, onClick: () =>
                {
                    var cmd = new LandArrangementCommand
                    {
                        IssuerId = player.Id, Action = "held_referendum",
                        RegionMapIndex = state.MapIndex, Years = world.Defines.LeaseDefaultYears
                    };
                    ExecuteCommand(ctx, world, cmd, wakeGlobe: true);
                    ShowFeedback(feedbackLabel, cmd.Result.Length > 0 ? cmd.Result : cmd.Done ? Loc.Get("shell.reg_act.done") : "");
                    onRefresh?.Invoke();
                }, onArmed: () => { ShowFeedback(feedbackLabel, ConfirmHint(isTouch)); onRefresh?.Invoke(); }));
            count++;
        }

        // 2. Allied Occupation Request (ally controls enemy provinces here)
        if (OccupationTransfer.IsEnabled(world))
        {
            var reqRefusal = OccupationTransfer.RequestRefusal(world, player, state, out var allyHolder);
            if (allyHolder is not null)
            {
                var score = OccupationTransfer.RequestAcceptance(world, player, allyHolder, state);
                var tip = score >= 0 && reqRefusal.Length == 0
                    ? Loc.Format("shell.reg_act.occ_request_accept_tip_fmt", allyHolder.DisplayName, score)
                    : reqRefusal.Length > 0
                        ? reqRefusal
                        : Loc.Format("shell.reg_act.occ_request_refuse_tip_fmt", allyHolder.DisplayName, score);
                var btn = MakeActionButton(ctx, Loc.Get("shell.reg_act.occ_request"), reqRefusal, tip, isTouch, btnH,
                    confirmKey: "", primary: score >= 0 && reqRefusal.Length == 0, onClick: () =>
                    {
                        var cmd = new RequestOccupationCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex };
                        ExecuteCommand(ctx, world, cmd, wakeGlobe: true);
                        ShowFeedback(feedbackLabel, cmd.Done
                            ? Loc.Format("shell.reg_act.occ_requested_ok_fmt", state.DisplayName, allyHolder.DisplayName)
                            : cmd.Refusal);
                        onRefresh?.Invoke();
                    });
                btn.Name = "ShellRegionAction_RequestOccupation";
                flow.AddChild(btn);
                count++;
            }

            // 3. Allied Occupation Transfer (player controls enemy provinces here and has war allies)
            var allies = OccupationTransfer.WarAlliesForTransfer(world, player, state);
            if (allies.Count > 0)
            {
                if (flow.GetChildCount() > 0) { parent.AddChild(flow); flow = CreateButtonFlow(); }
                parent.AddChild(MakeSubLabel(ctx, Loc.Get("shell.reg_act.occ_transfer_label"), "shell.brass_hi"));
                var transferFlow = CreateButtonFlow();
                foreach (var ally in allies.Take(6))
                {
                    var captured = ally;
                    var tip = Loc.Format("shell.reg_act.occ_transfer_tip_fmt", state.DisplayName, captured.DisplayName);
                    var btn = MakeActionButton(ctx, Loc.Format("shell.reg_act.occ_transfer_to_fmt", captured.DisplayName),
                        "", tip, isTouch, btnH, confirmKey: "", primary: false, onClick: () =>
                        {
                            var cmd = new TransferOccupationCommand
                            {
                                IssuerId = player.Id, RecipientId = captured.Id, StateMapIndex = state.MapIndex
                            };
                            ExecuteCommand(ctx, world, cmd, wakeGlobe: true);
                            ShowFeedback(feedbackLabel, cmd.Done
                                ? Loc.Format("shell.reg_act.occ_transferred_ok_fmt", state.DisplayName, captured.DisplayName)
                                : cmd.Refusal);
                            onRefresh?.Invoke();
                        });
                    btn.Name = "ShellRegionAction_TransferOccupation_" + captured.Id;
                    transferFlow.AddChild(btn);
                    count++;
                }
                parent.AddChild(transferFlow);
            }
        }

        if (flow.GetChildCount() > 0) parent.AddChild(flow);
        else flow.QueueFree();
        return count;
    }

    private static int AppendForeignArrangementActions(
        ShellContext ctx, VBoxContainer parent, Label feedbackLabel, Campaign world,
        Nation player, Nation owner, ScenarioState state, bool isTouch, int btnH, Action onRefresh)
    {
        if (owner is null || owner.Movement is not null || !world.FeatureEnabled("land_arrangements"))
            return 0;

        var arrangement = LandArrangementSystem.Covering(world, state.MapIndex);
        var desc = LandArrangementSystem.Describe(world, state);
        var mine = arrangement is not null && arrangement.HolderId == player.Id;
        var flow = CreateButtonFlow();
        var count = 0;

        void AddArr(string caption, string action, string refusal, string explain, bool confirm)
        {
            var key = confirm ? $"arr:{action}:{state.MapIndex}" : "";
            flow.AddChild(MakeActionButton(ctx, Loc.Get(caption), refusal, explain, isTouch, btnH,
                confirmKey: key, primary: false, onClick: () =>
                {
                    var cmd = new LandArrangementCommand
                    {
                        IssuerId = player.Id, Action = action,
                        RegionMapIndex = state.MapIndex, Years = world.Defines.LeaseDefaultYears
                    };
                    ExecuteCommand(ctx, world, cmd, wakeGlobe: true);
                    if (cmd.Done && action == "send_troops") ctx.PlayCue?.Invoke("war_declared");
                    ShowFeedback(feedbackLabel, cmd.Result.Length > 0 ? cmd.Result : cmd.Done ? Loc.Get("shell.reg_act.done") : "");
                    onRefresh?.Invoke();
                }, onArmed: () => { ShowFeedback(feedbackLabel, ConfirmHint(isTouch)); onRefresh?.Invoke(); }));
            count++;
        }

        if (arrangement is null)
        {
            var rent = LandArrangementSystem.FairRent(world, new[] { state });
            AddArr("Ask to lease it", "request_lease", LandArrangementSystem.LeaseRefusal(world, player, state),
                Loc.Format("Ask {0} to let us hold this region for {1} years at {2:0.000} bn a year. Ours to garrison and to base fleets in; theirs in law.",
                    owner.DisplayName, world.Defines.LeaseDefaultYears, rent), confirm: false);
        }
        if (mine && arrangement.Kind == LandArrangementSystem.Lease && arrangement.LeavingOnDay >= 0)
            AddArr("Keep the troops there", "stay", "",
                Loc.Get("The lease is over; our soldiers stay anyway. The world will call it what it is."), confirm: true);
        if (arrangement is null || (mine && arrangement.Kind == LandArrangementSystem.Lease))
            AddArr("Send troops in", "send_troops", LandArrangementSystem.PresenceRefusal(world, player, state),
                Loc.Format("Our soldiers take the region without a war - for our people there, from a base we hold, or because nobody is left to stop us. {0} may swallow it, or fight.", owner.DisplayName),
                confirm: true);
        if (mine && arrangement.Kind == LandArrangementSystem.Presence)
        {
            AddArr("Hold a referendum", "referendum", LandArrangementSystem.ReferendumRefusal(world, player, arrangement),
                Loc.Get("A vote on joining us, counted by our soldiers, and the region is ours. Who lives there decides how much of the world believes it."), confirm: true);
            AddArr("Proclaim a republic", "republic", LandArrangementSystem.ReferendumRefusal(world, player, arrangement),
                Loc.Get("The region declares itself a state, under our protection: theirs on paper, ours in practice. Its old country will call it a breakaway and may come back for it."), confirm: true);
        }
        if (mine)
            AddArr(arrangement.Kind == LandArrangementSystem.Lease ? "Give up the lease" : "Withdraw the troops", "withdraw", "",
                Loc.Get("Our soldiers come home and the region goes back to its owner."), confirm: true);

        if (count == 0 && desc.Length == 0) { flow.QueueFree(); return 0; }
        parent.AddChild(MakeSubLabel(ctx, Loc.Get("LAND AND TROOPS"), "shell.paper_muted"));
        if (desc.Length > 0) parent.AddChild(MakeBodyLabel(ctx, desc, "shell.paper"));
        if (count > 0) parent.AddChild(flow); else flow.QueueFree();
        return Math.Max(count, desc.Length > 0 ? 1 : 0);
    }

    private static int AppendKinStatecraftActions(
        ShellContext ctx, VBoxContainer parent, Label feedbackLabel, Campaign world,
        Nation player, Nation owner, ScenarioState state, int btnH, Action onRefresh)
    {
        if (owner is null || owner.Id == player.Id || owner.Movement is { Landless: true } ||
            player.Movement is not null || !world.FeatureEnabled("movements"))
            return 0;

        var (ours, total, share) = KinStatecraft.Kin(world, player, state);
        if (ours <= 0) return 0;

        parent.AddChild(MakeSubLabel(ctx, Loc.Get("OUR PEOPLE HERE"), "shell.paper_muted"));
        var status = Loc.Format("Ours: {0} of {1} ({2}).", Fmt.People(ours), Fmt.People(total), Fmt.Percent(share));
        var offices = ParallelAdministration.Describe(world, state);
        if (offices.Length > 0) status += "\n" + offices;
        parent.AddChild(MakeBodyLabel(ctx, status, "shell.paper"));

        var flow = CreateButtonFlow();
        var count = 0;
        foreach (var kind in KinStatecraft.Kinds)
        {
            var captured = kind;
            var refusal = KinStatecraft.Refusal(world, player, state, captured.Id);
            var cost = KinStatecraft.Cost(world, player, state, captured.Id);
            var price = cost > 0f ? Loc.Format("Cost: {0}.", Fmt.Money(cost)) : "";
            var help = Loc.Get(captured.Help) + (price.Length > 0 ? "\n\n" + price : "");
            flow.AddChild(MakeActionButton(ctx, Loc.Get(captured.Name), refusal, help, isTouch: false, btnH,
                confirmKey: "", primary: false, onClick: () =>
                {
                    var cmd = new KinStatecraftCommand { IssuerId = player.Id, Action = captured.Id, RegionMapIndex = state.MapIndex };
                    ExecuteCommand(ctx, world, cmd, wakeGlobe: true);
                    if (cmd.Done && captured.Id == "volunteers") ctx.PlayCue?.Invoke("war_declared");
                    ShowFeedback(feedbackLabel, cmd.Result.Length > 0 ? cmd.Result : cmd.Done ? Loc.Get("shell.reg_act.done") : "");
                    onRefresh?.Invoke();
                }));
            count++;
        }
        parent.AddChild(flow);
        return count;
    }

    private static int AppendColonisationActions(
        ShellContext ctx, VBoxContainer parent, Label feedbackLabel, Campaign world,
        Nation player, ScenarioState state, int btnH, Action onRefresh)
    {
        var colonist = ColonisationCore.Colonist(world, state);
        var cost = ColonisationCore.CostFor(world, player, state);
        var days = ColonisationCore.DaysFor(world, player, state);
        var ours = colonist == player;
        var summary = colonist is not null
            ? Loc.Format("shell.reg_act.colony_progress_fmt", colonist.DisplayName, Fmt.Percent(state.ColonisationProgress), Fmt.Money(cost), days)
            : Loc.Format("shell.reg_act.colony_quote_fmt", Fmt.Money(cost), days);
        parent.AddChild(MakeBodyLabel(ctx, summary, "shell.paper"));

        var flow = CreateButtonFlow();
        var refusal = ours ? "" : ColonisationCore.Refusal(world, player, state);
        var tip = ours
            ? Loc.Get("The settlers come home and the land stays nobody's. What was spent is spent.")
            : Loc.Format("Send settlers for {0}. In {1:N0} days the region is ours, with the people who went.", Fmt.Money(cost), days);
        flow.AddChild(MakeActionButton(ctx, Loc.Get(ours ? "Call the settlers home" : "Found a colony"),
            refusal, tip, isTouch: false, btnH, confirmKey: "", primary: !ours, onClick: () =>
            {
                if (ours)
                {
                    ExecuteCommand(ctx, world, new AbandonColonyCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex }, wakeGlobe: true);
                    ShowFeedback(feedbackLabel, Loc.Get("The settlers are coming home."));
                }
                else
                {
                    var cmd = new ColoniseCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex };
                    ExecuteCommand(ctx, world, cmd, wakeGlobe: true);
                    ShowFeedback(feedbackLabel, cmd.Started
                        ? Loc.Format("Our settlers set out for {0}.", state.DisplayName)
                        : ColonisationCore.Refusal(world, player, state));
                }
                onRefresh?.Invoke();
            }));
        parent.AddChild(flow);
        return 1;
    }

    private static int AppendOwnRegionActions(
        ShellContext ctx, VBoxContainer parent, Label feedbackLabel, Campaign world,
        Nation player, ScenarioState state, bool isTouch, int btnH, Action onRefresh)
    {
        var flow = CreateButtonFlow();
        var count = 0;

        // 1. End active foreign lease on own region
        if (world.FeatureEnabled("land_arrangements"))
        {
            var arr = LandArrangementSystem.Covering(world, state.MapIndex);
            if (arr is { Kind: LandArrangementSystem.Lease, LeavingOnDay: < 0 })
            {
                var desc = LandArrangementSystem.Describe(world, state);
                if (desc.Length > 0) parent.AddChild(MakeBodyLabel(ctx, desc, "shell.state.warning_text"));
                flow.AddChild(MakeActionButton(ctx, Loc.Get("End the lease"), "",
                    Loc.Get("The tenant has three months to leave - or to decide not to. They will not forget it."),
                    isTouch, btnH, confirmKey: $"end_lease:{state.MapIndex}", primary: false, onClick: () =>
                    {
                        var cmd = new LandArrangementCommand { IssuerId = player.Id, Action = "end_lease", RegionMapIndex = state.MapIndex };
                        ExecuteCommand(ctx, world, cmd, wakeGlobe: true);
                        ShowFeedback(feedbackLabel, cmd.Result.Length > 0 ? cmd.Result : cmd.Done ? Loc.Get("shell.reg_act.done") : "");
                        onRefresh?.Invoke();
                    }, onArmed: () => { ShowFeedback(feedbackLabel, ConfirmHint(isTouch)); onRefresh?.Invoke(); }));
                count++;
            }
        }

        // 2. Reconstruction (RebuildRegionCommand)
        var rebuilding = world.ElapsedDays <= state.RebuildingUntilDay;
        if (state.WarDamage > 0.005f || rebuilding)
        {
            var refusal = WarDamage.RebuildRefusal(world, player, state);
            var defs = world.Defines;
            var label = rebuilding
                ? (WarDamage.Safe(world, state)
                    ? Loc.Format("Rebuilding: {0} days left", state.RebuildingUntilDay - world.ElapsedDays)
                    : Loc.Get("Rebuilding stopped by the fighting"))
                : Loc.Get("Rebuild");
            var tip = Loc.Format("For {0} days the region is rebuilt {1} times as fast, while no battle is fought in it and as far as the market has industrial materials for it.",
                          defs.WarDamageRebuildDays, (1f + defs.WarDamageRebuildBoost).ToString("0.#", CultureInfo.InvariantCulture)) +
                      "\n" + Loc.Format("Costs {0}.", Fmt.Money(WarDamage.RebuildCost(world, player, state)));
            var btn = MakeActionButton(ctx, label, refusal, tip, isTouch, btnH, confirmKey: "", primary: !rebuilding && refusal.Length == 0, onClick: () =>
            {
                ExecuteCommand(ctx, world, new RebuildRegionCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex }, wakeGlobe: false);
                ShowFeedback(feedbackLabel, Loc.Get("shell.reg_act.rebuilding_started"));
                onRefresh?.Invoke();
            });
            btn.Name = "ShellRegionAction_Rebuild";
            flow.AddChild(btn);
            count++;
        }

        // 3. Regional Autonomy (Grant / Revoke)
        if (state.MapIndex != player.CapitalStateMapIndex &&
            (RegionalAutonomy.Autonomous(state) || ConquestAcceptance.Unentitled(world, player, state) ||
             state.Radicalism >= world.Defines.AiAutonomyRadicalism))
        {
            var d = world.Defines;
            if (!RegionalAutonomy.Autonomous(state))
            {
                var refusal = RegionalAutonomy.Refusal(world, player, state);
                var tip = Loc.Format("Costs {0} political points now and {1} a week: the region keeps {2} of its taxes. It weighs {3} as much in imperial overstretch, takes in less grievance and comes to accept the state twice as fast.",
                    d.AutonomyPoliticalCost.ToString("0"), Fmt.Money(RegionalAutonomy.WeeklyPrice(world, player, state)),
                    Fmt.Percent(d.AutonomyRevenueShare), Fmt.Percent(d.AutonomyOverstretchWeight));
                var btn = MakeActionButton(ctx, Loc.Get("Grant autonomy"), refusal.Length > 0 ? Loc.Get(refusal) : "", tip,
                    isTouch, btnH, confirmKey: $"grant_autonomy:{state.MapIndex}", primary: false, onClick: () =>
                    {
                        ExecuteCommand(ctx, world, new GrantRegionalAutonomyCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex }, wakeGlobe: false);
                        ShowFeedback(feedbackLabel, Loc.Get("shell.reg_act.done"));
                        onRefresh?.Invoke();
                    }, onArmed: () => { ShowFeedback(feedbackLabel, ConfirmHint(isTouch)); onRefresh?.Invoke(); });
                btn.Name = "ShellRegionAction_GrantAutonomy";
                flow.AddChild(btn);
            }
            else
            {
                var refusal = RegionalAutonomy.RevokeRefusal(world, player, state);
                var tip = Loc.Format("Free, but remembered: the region's radicalism rises by {0} points at once, its acceptance of the state halves, and it cannot be granted autonomy again for {1} days.",
                    (d.AutonomyRevokeRadicalism * 100f).ToString("0"), d.AutonomyRegrantDays);
                var btn = MakeActionButton(ctx, Loc.Get("Revoke autonomy"), refusal.Length > 0 ? Loc.Get(refusal) : "", tip,
                    isTouch, btnH, confirmKey: $"revoke_autonomy:{state.MapIndex}", primary: false, onClick: () =>
                    {
                        ExecuteCommand(ctx, world, new RevokeRegionalAutonomyCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex }, wakeGlobe: false);
                        ShowFeedback(feedbackLabel, Loc.Get("shell.reg_act.done"));
                        onRefresh?.Invoke();
                    }, onArmed: () => { ShowFeedback(feedbackLabel, ConfirmHint(isTouch)); onRefresh?.Invoke(); });
                btn.Name = "ShellRegionAction_RevokeAutonomy";
                flow.AddChild(btn);
            }
            count++;
        }

        // 4. Conciliate / Settle unrest in this region
        var settling = player.DomesticCrisis is { ConciliationUntilDay: >= 0 } mem &&
                       mem.ConciliationUntilDay > world.ElapsedDays && mem.ConciliationRegion == state.MapIndex;
        if (settling || PoliticalCohorts.UnrestHeat(state) >= world.Defines.CrisisConciliationHeat || state.SecessionPressure >= 0.12f)
        {
            var refusal = DomesticCrises.ConciliationRefusal(world, player, state);
            var tip = Loc.Format("Pay {0} to cool this region for {1} days: the pull to leave, the anger, and the distance from the government. One region at a time. A border is not redrawn.",
                Fmt.Money(DomesticCrises.ConciliationCost(world, player, state)), world.Defines.CrisisConciliationDays);
            flow.AddChild(MakeActionButton(ctx, Loc.Get(settling ? "Settlement under way" : "Settle this region"),
                refusal, tip, isTouch, btnH, confirmKey: "", primary: false, onClick: () =>
                {
                    ExecuteCommand(ctx, world, new ConciliateRegionCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex }, wakeGlobe: false);
                    ShowFeedback(feedbackLabel, Loc.Get("shell.reg_act.done"));
                    onRefresh?.Invoke();
                }));
            count++;
        }

        // 5. Move Capital Here
        var cities = CapitalCore.Candidates(world, player, state);
        if (state.MapIndex != player.CapitalStateMapIndex || cities.Count > 0)
        {
            var capRefusal = cities.Count > 0
                ? CapitalCore.Refusal(world, player, state, cities[0].Id)
                : CapitalCore.Refusal(world, player, state);
            var targetName = cities.Count == 1 ? cities[0].DisplayName : state.DisplayName;
            var capTip = Loc.Format("The government moves here. It costs {0} and a little order while it settles.",
                Fmt.Money(CapitalCore.Cost(world, player)));
            var btn = MakeActionButton(ctx, Loc.Format("Move the capital to {0}", targetName),
                capRefusal.Length > 0 ? Loc.Get(capRefusal) : "", capTip, isTouch, btnH, confirmKey: "", primary: false,
                onClick: () => ctx.OpenCapitalChooserForState?.Invoke(state));
            btn.Name = "ShellRegionAction_MoveCapital";
            flow.AddChild(btn);
            count++;
        }

        // 6. Regional laws shortcut
        if (ctx.OpenRegionalLawsForState is not null)
        {
            flow.AddChild(MakeActionButton(ctx, Loc.Get("Regional laws"), "", Loc.Get("Regional laws"),
                isTouch, btnH, confirmKey: "", primary: false, onClick: () => ctx.OpenRegionalLawsForState.Invoke(state)));
            count++;
        }

        if (flow.GetChildCount() > 0) parent.AddChild(flow);
        else flow.QueueFree();
        return count;
    }

    private static int AppendMovementActions(
        ShellContext ctx, VBoxContainer parent, Label feedbackLabel, Campaign world,
        Nation player, ScenarioState state, bool isOwn, int btnH, Action onRefresh)
    {
        if (!world.FeatureEnabled("movements")) return 0;
        var found = new List<(Nation Movement, float Support)>();
        for (var i = 0; i < world.Nations.Count; i++)
        {
            var n = world.Nations[i];
            if (n.Movement is not { Dissolved: false } mv) continue;
            var sup = mv.Support.GetValueOrDefault(state.MapIndex);
            if (sup >= 0.05f) found.Add((n, sup));
        }
        if (found.Count == 0) return 0;
        found.Sort((a, b) => b.Support != a.Support ? b.Support.CompareTo(a.Support) : a.Movement.Id.CompareTo(b.Movement.Id));

        var flow = CreateButtonFlow();
        var count = 0;
        foreach (var (mv, _) in found.Take(2))
        {
            var captured = mv;
            var action = isOwn ? "crack_down" : "sponsor";
            var label = isOwn
                ? Loc.Format("Crack down on {0}", captured.DisplayName)
                : Loc.Format("Fund {0}", captured.DisplayName);
            var refusal = MovementDealings.Refusal(world, player, captured, action);
            flow.AddChild(MakeActionButton(ctx, label, refusal, label, isTouch: false, btnH, confirmKey: "", primary: false, onClick: () =>
            {
                ExecuteCommand(ctx, world, new MovementDealingCommand { IssuerId = player.Id, MovementId = captured.Id, Action = action }, wakeGlobe: false);
                ShowFeedback(feedbackLabel, Loc.Get("shell.reg_act.done"));
                onRefresh?.Invoke();
            }));
            count++;
        }
        if (count > 0) parent.AddChild(flow); else flow.QueueFree();
        return count;
    }

    private static void ExecuteCommand(ShellContext ctx, Campaign world, ICampaignCommand command, bool wakeGlobe)
    {
        if (ctx.CommitImmediateCommand is not null)
        {
            ctx.CommitImmediateCommand.Invoke(command, wakeGlobe);
            return;
        }
        ImmediateCampaignCommands.Commit(world, command);
        ctx.RefreshAll?.Invoke();
    }

    private static Button MakeActionButton(
        ShellContext ctx, string caption, string refusal, string help, bool isTouch, int btnH,
        string confirmKey, bool primary, Action onClick, Action onArmed = null)
    {
        var armed = confirmKey.Length > 0 && string.Equals(ctx.PendingRegionActionKey, confirmKey, StringComparison.Ordinal);
        var disabled = refusal.Length > 0;
        var tip = help;
        if (disabled) tip = string.IsNullOrEmpty(tip) ? refusal : tip + "\n\n" + refusal;
        else if (armed) tip = string.IsNullOrEmpty(tip) ? ConfirmHint(isTouch) : tip + "\n\n" + ConfirmHint(isTouch);

        var btn = new Button
        {
            Text = armed ? Loc.Format("shell.reg_act.confirm_btn_fmt", caption) : caption,
            Disabled = disabled,
            CustomMinimumSize = new Vector2(0, btnH),
            TooltipText = Ui.Tip(tip),
            MouseDefaultCursorShape = disabled ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        var borderTok = armed ? "shell.state.warning" : primary && !disabled ? "shell.brass_hi" : "shell.engrave";
        var bgTok = armed ? "shell.well_top" : primary && !disabled ? "shell.plate_top" : "shell.well";
        btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox(bgTok, borderTok, 1, 2, 8, 3, 8, 3));
        btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 8, 3, 8, 3));
        btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 8, padY: 3));
        btn.AddThemeStyleboxOverride("disabled", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 8, 3, 8, 3));
        btn.AddThemeFontOverride("font", ctx.Font(primary && !disabled ? "shell.serif_semibold" : "shell.sans"));
        btn.AddThemeFontSizeOverride("font_size", 11);
        btn.AddThemeColorOverride("font_color", Ui.Colour(armed ? "shell.state.warning_text" : "shell.paper"));
        btn.AddThemeColorOverride("font_disabled_color", Ui.Colour("shell.paper_faint"));
        btn.Pressed += () =>
        {
            ctx.PlayCue?.Invoke("ui_select");
            if (confirmKey.Length > 0 && !armed)
            {
                ctx.PendingRegionActionKey = confirmKey;
                onArmed?.Invoke();
                return;
            }
            ctx.PendingRegionActionKey = "";
            onClick();
        };
        return btn;
    }

    private static string ConfirmHint(bool isTouch) =>
        Loc.Get(isTouch ? "shell.reg_act.confirm_touch" : "shell.reg_act.confirm_pc");

    private static HFlowContainer CreateButtonFlow()
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        flow.AddThemeConstantOverride("h_separation", 5);
        flow.AddThemeConstantOverride("v_separation", 5);
        return flow;
    }

    private static Label MakeSubLabel(ShellContext ctx, string text, string colorTok) =>
        ShellRegionVisualsView.MakeLabel(ctx, text, "shell.serif_semibold", 11, colorTok, clip: true);

    private static Label MakeBodyLabel(ShellContext ctx, string text, string colorTok)
    {
        var lbl = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        lbl.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        lbl.AddThemeFontSizeOverride("font_size", 11);
        lbl.AddThemeColorOverride("font_color", Ui.Colour(colorTok));
        return lbl;
    }

    private static void ShowFeedback(Label feedbackLabel, string text)
    {
        if (feedbackLabel is null || !GodotObject.IsInstanceValid(feedbackLabel)) return;
        feedbackLabel.Text = text ?? "";
        feedbackLabel.Visible = !string.IsNullOrEmpty(text);
    }
}
