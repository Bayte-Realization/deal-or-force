using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _decisionControls = null!;

    private void BuildDecisionControls(VBoxContainer parent)
    {
        _decisionControls = new VBoxContainer { Visible = false };
        _decisionControls.AddThemeConstantOverride("separation", 8);
        parent.AddChild(_decisionControls);
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
            var cost = Simulation.Loc.Format("Cost {0} · cooldown {1} days",
                Fmt.Money(DecisionCatalog.Cost(nation, definition)), definition.CooldownDays);
            // One card for every action: what it does, what it costs, what it will change, and -
            // when it cannot be taken - why not. The result lines are the same ones the decision
            // applies when it runs, not a separate promise.
            var button = ActionCard(_decisionControls, definition.Name, definition.Description, cost,
                ScriptedEffects.Lines(definition.Effect), block, "Enact decision", block.Length == 0,
                () => TakeDecision(captured), "Decision_" + captured);
            // The description, then exactly what enacting it does - the same lines an event
            // option shows - or, while it cannot be enacted, why not.
            var effects = ScriptedEffects.Block(definition.Effect);
            Tip(button, block.Length > 0 ? block
                : effects.Length == 0 ? definition.Description
                : Tr(definition.Description) + "\n\n" + effects);
        }
    }

    private void TakeDecision(string id)
    {
        _campaign.Commands.Enqueue(new TakeDecisionCommand { IssuerId = _campaign.Player.Id, DecisionId = id });
        _campaign.Commands.ApplyAll(_campaign);
        ShowDecisionsScreen();
        Refresh();
    }
}
