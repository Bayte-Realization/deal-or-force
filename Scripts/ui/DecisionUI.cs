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
            var card = WorkbenchCard(_decisionControls);
            card.AddChild(Title(definition.Name, 15, Ui.Colour("text.bright")));
            card.AddChild(Title(definition.Description, 12, Ui.Colour("text.body_soft")));
            card.AddChild(Title(Simulation.Loc.Format("Cost {0} · cooldown {1} days",
                Fmt.Money(DecisionCatalog.Cost(nation, definition)), definition.CooldownDays), 12, Ui.Colour("text.accent_dim")));
            var captured = definition.Id;
            var button = ActionButton("Enact decision", () => TakeDecision(captured), 0, 32, block.Length == 0);
            button.Name = "Decision_" + captured;
            button.Disabled = block.Length > 0;
            // The description, then exactly what enacting it does - the same lines an event
            // option shows - or, while it cannot be enacted, why not.
            var effects = ScriptedEffects.Block(definition.Effect);
            Tip(button, block.Length > 0 ? block
                : effects.Length == 0 ? definition.Description
                : Tr(definition.Description) + "\n\n" + effects);
            card.AddChild(button);
            if (block.Length > 0) card.AddChild(Title(block, 12, Ui.Colour("state.warning")));
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
