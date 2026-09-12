using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _decisionControls = null!;
    private Label _decisionStatus = null!;

    private void BuildDecisionControls(VBoxContainer parent)
    {
        _decisionControls = new VBoxContainer { Visible = false };
        _decisionControls.AddThemeConstantOverride("separation", 5);
        parent.AddChild(_decisionControls);
        _decisionControls.AddChild(Title("AVAILABLE ACTIONS", 12, Ui.Colour("text.caption")));
        foreach (var definition in DecisionCatalog.All)
        {
            var captured = definition;
            var button = ActionButton(definition.Name, () => TakeDecision(captured.Id), 318, 30,
                definition.Kind == DecisionKind.EconomicStimulus);
            button.TooltipText = Ui.Tip(definition.Description);
            _decisionControls.AddChild(button);
        }
        _decisionStatus = Title("", 12, Ui.Colour("text.accent"));
        _decisionStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _decisionControls.AddChild(_decisionStatus);
    }

    private void ShowDecisionsScreen()
    {
        var nation = _campaign.Player;
        var available = DecisionCatalog.All.Count(definition =>
            DecisionCatalog.BlockReason(_campaign, nation, definition).Length == 0);
        _sideBody.Text =
            $"NATIONAL DECISIONS\n\nTurn treasury and political opportunity into concrete change. " +
            $"Every decision has a real cost and cooldown.\n\nAvailable now: {available}/{DecisionCatalog.All.Count}\n" +
            $"Treasury: {Fmt.Money(nation.Treasury)} · stability: {nation.Stability:P0}\n" +
            $"Literacy: {nation.Literacy:P0} · living standard: {nation.StandardOfLiving:P0}";
        _decisionStatus.Text = string.Join("\n", DecisionCatalog.All.Select(definition =>
        {
            var block = DecisionCatalog.BlockReason(_campaign, nation, definition);
            return $"{definition.Name}: {Fmt.Money(DecisionCatalog.Cost(nation, definition))}" +
                   (block.Length == 0 ? " · READY" : $" · {block}");
        }));
    }

    private void TakeDecision(string id)
    {
        _campaign.Commands.Enqueue(new TakeDecisionCommand { IssuerId = _campaign.Player.Id, DecisionId = id });
        _campaign.Commands.ApplyAll(_campaign);
        ShowDecisionsScreen();
        Refresh();
    }
}
