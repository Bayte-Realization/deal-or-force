using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _decisionControls = null!;
    private VBoxContainer _promiseControls = null!;
    private VBoxContainer _goalControls = null!;
    private readonly HashSet<string> _goalsSeenComplete = new();

    private void BuildDecisionControls(VBoxContainer parent)
    {
        _decisionControls = new VBoxContainer { Visible = false };
        _decisionControls.AddThemeConstantOverride("separation", 8);
        parent.AddChild(_decisionControls);
        _promiseControls = new VBoxContainer { Visible = false };
        _promiseControls.AddThemeConstantOverride("separation", 8);
        parent.AddChild(_promiseControls);
        _goalControls = new VBoxContainer { Visible = false };
        _goalControls.AddThemeConstantOverride("separation", 8);
        parent.AddChild(_goalControls);
    }

    private void ShowDecisionsScreen()
    {
        if (_dossierTab == "Opportunities") { ShowOpportunityAtlas(); return; }
        if (_dossierTab == "Formables") { ShowFormableNations(); return; }
        _formationConfirmation = "";
        var nation = _campaign.Player;
        var available = DecisionCatalog.All.Count(definition =>
            DecisionCatalog.BlockReason(_campaign, nation, definition).Length == 0);
        _sideBody.Text = Simulation.Loc.Format("Available decisions: {0}/{1} · treasury {2}",
            available, DecisionCatalog.All.Count, Fmt.Money(nation.Treasury));
        ClearRuntimeChildren(_decisionControls);
        foreach (var definition in DecisionCatalog.All)
        {
            var block = DecisionCatalog.BlockReason(_campaign, nation, definition);
            var captured = definition.Id;
            var price = DecisionCatalog.Cost(nation, definition);
            var cost = Simulation.Loc.Format("Cost {0} · cooldown {1} days",
                Fmt.Money(price), definition.CooldownDays);
            // One card for every action: what it does, what it costs, what it will change, and -
            // when it cannot be taken - why not. PreviewForNation shows exact before → after
            // transitions on the current nation without mutating it.
            var previewLines = ScriptedEffects.PreviewForNation(_campaign, nation, definition.Effect, price);
            var button = ActionCard(_decisionControls, definition.Name, definition.Description, cost,
                previewLines, block, "Enact decision", block.Length == 0,
                () => TakeDecision(captured), "Decision_" + captured);
            var effects = previewLines.Count == 0
                ? ""
                : Tr("Effects:") + "\n" + string.Join("\n", previewLines.Select(line => "  " + line));
            Tip(button, block.Length > 0 ? block
                : effects.Length == 0 ? definition.Description
                : Tr(definition.Description) + "\n\n" + effects);
        }
        ShowPromiseCards(nation);
        ShowGoalCards(nation);
    }

    // A promise is a different kind of card: nothing is paid to make it and nothing happens
    // when it is made. What the card sells is the date - what keeping it will bring, what
    // missing it will cost, and how long the government has to make the outcome true.
    private void ShowPromiseCards(Nation nation)
    {
        ClearRuntimeChildren(_promiseControls);
        _promiseControls.Visible = _campaign.FeatureEnabled("government_promises") && PromiseCatalog.All.Count > 0;
        if (!_promiseControls.Visible) return;

        var openPromises = PromiseCatalog.All.Count(definition =>
            GovernmentPromises.BlockReason(_campaign, nation, definition).Length == 0);
        _sideBody.Text += "\n" + Simulation.Loc.Format("Promises: {0} active · {1} available",
            nation.Promises.Count, openPromises);

        foreach (var active in nation.Promises.OrderBy(promise => promise.DueDay))
        {
            if (PromiseCatalog.Find(active.Id) is not { } definition) continue;
            _promiseControls.AddChild(CardLine(
                Simulation.Loc.Format("{0} · {1}", Tr(definition.Name),
                    Simulation.Loc.Format("Days left: {0}", Math.Max(0, active.DueDay - _campaign.ElapsedDays))),
                active.DueDay - _campaign.ElapsedDays <= 0 ? "state.warning" : "text.bright"));
        }

        foreach (var definition in PromiseCatalog.All)
        {
            var block = GovernmentPromises.BlockReason(_campaign, nation, definition);
            var captured = definition.Id;
            var kept = ScriptedEffects.PreviewForNation(_campaign, nation, definition.Kept);
            var broken = ScriptedEffects.PreviewForNation(_campaign, nation, definition.Broken);
            var what = definition.Description
                + (kept.Count == 0 ? "" : "\n" + Tr("If kept:") + "\n" + string.Join("\n", kept.Select(line => "  " + line)))
                + (broken.Count == 0 ? "" : "\n" + Tr("If missed:") + "\n" + string.Join("\n", broken.Select(line => "  " + line)));
            var button = ActionCard(_promiseControls, definition.Name, what,
                Simulation.Loc.Format("Deadline {0} days", definition.DurationDays),
                Array.Empty<string>(), block, "Make promise", block.Length == 0,
                () => TakePromise(captured), "Promise_" + captured);
            Tip(button, block.Length > 0 ? block : Tr(
                "The promise is concluded the day its outcome is true, even before the deadline. Past the deadline it is broken whatever the trend."));
        }
    }

    private void TakeDecision(string id)
    {
        _campaign.Commands.Enqueue(new TakeDecisionCommand { IssuerId = _campaign.Player.Id, DecisionId = id });
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("save_complete");
        ShowDecisionsScreen();
        Refresh();
    }

    private void TakePromise(string id)
    {
        _campaign.Commands.Enqueue(new MakePromiseCommand { IssuerId = _campaign.Player.Id, PromiseId = id });
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("promise_seal");
        ShowDecisionsScreen();
        Refresh();
    }

    // A goal is the quietest card on the desk: it is not owed to anybody, so adopting and
    // abandoning it cost nothing and change nothing - what it sells is the checklist, and
    // the day the last milestone stands, the record says so and the desk clicks once.
    private void ShowGoalCards(Nation nation)
    {
        ClearRuntimeChildren(_goalControls);
        _goalControls.Visible = _campaign.FeatureEnabled("strategic_goals") && GoalCatalog.All.Count > 0;
        if (!_goalControls.Visible) return;

        var pinned = nation.Goals.Count(goal => goal.CompletedDay < 0);
        _sideBody.Text += "\n" + Simulation.Loc.Format("Goals: {0} pinned · {1} completed",
            pinned, nation.Goals.Count - pinned);

        foreach (var active in nation.Goals)
        {
            if (GoalCatalog.Find(active.Id) is not { } definition) continue;
            var progress = StrategicGoals.Progress(_campaign, nation, definition);
            var lines = new List<string>();
            for (var index = 0; index < definition.Milestones.Count; index++)
                lines.Add((progress[index] ? "✓ " : "· ") + Tr(definition.Milestones[index].Text));
            var completedNow = active.CompletedDay >= 0;
            var card = ActionCard(_goalControls, Tr(definition.Name), Tr(definition.Description),
                completedNow ? Tr("Completed") : Simulation.Loc.Format("Adopted {0} days ago",
                    Math.Max(0, _campaign.ElapsedDays - active.AdoptedDay)),
                lines, "", completedNow ? "Completed" : "Abandon goal",
                !completedNow,
                () => AbandonGoal(definition.Id), "Goal_" + definition.Id);
            if (completedNow && _goalsSeenComplete.Add(definition.Id)) PlayStrategicCue("goal_complete");
            else if (!completedNow) _goalsSeenComplete.Remove(definition.Id);
            Tip(card, Tr("A goal is private: nothing in the world changes when it is pinned, dropped or met. The record is the point."));
        }

        foreach (var definition in GoalCatalog.All)
        {
            if (nation.Goals.Any(goal => goal.Id.Equals(definition.Id, StringComparison.Ordinal))) continue;
            var block = StrategicGoals.BlockReason(_campaign, nation, definition);
            var captured = definition.Id;
            var lines = definition.Milestones.Select(milestone => "· " + Tr(milestone.Text)).ToList();
            var button = ActionCard(_goalControls, Tr(definition.Name), Tr(definition.Description),
                Simulation.Loc.Format("{0} milestones", definition.Milestones.Count),
                lines, block, "Pin goal", block.Length == 0,
                () => AdoptGoal(captured), "PinGoal_" + captured);
            Tip(button, block.Length > 0 ? block : Tr(
                "A goal is private: nothing in the world changes when it is pinned, dropped or met. The record is the point."));
        }
    }

    private void AdoptGoal(string id)
    {
        _campaign.Commands.Enqueue(new AdoptGoalCommand { IssuerId = _campaign.Player.Id, GoalId = id });
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("goal_pinned");
        ShowDecisionsScreen();
        Refresh();
    }

    private void AbandonGoal(string id)
    {
        _campaign.Commands.Enqueue(new AbandonGoalCommand { IssuerId = _campaign.Player.Id, GoalId = id });
        _campaign.Commands.ApplyAll(_campaign);
        ShowDecisionsScreen();
        Refresh();
    }
}
