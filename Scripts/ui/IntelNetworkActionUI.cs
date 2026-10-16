using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The diplomacy screen's intelligence shelf: found a network in the selected country, see how
/// far it has grown, recall it (IntelNetworks).
/// </summary>
public partial class DealOrForceGame
{
    private Button _diplomacyIntelButton = null!;

    private void AddIntelNetworkAction()
    {
        _diplomacyIntelButton = AddDiplomacyAction("INTELLIGENCE", "Found an intelligence network", ToggleIntelNetwork);
        _diplomacyIntelButton.Name = "IntelNetworkWide";
    }

    private void RefreshIntelNetworkAction(Nation player, Nation nation)
    {
        if (_diplomacyIntelButton is null || player is null || nation is null) return;
        _diplomacyIntelButton.Visible = nation.Id != player.Id && nation.Movement is null;
        var strength = IntelNetworks.Strength(_campaign, player.Id, nation.Id);
        if (player.IntelNetworks.ContainsKey(nation.Id))
        {
            _diplomacyIntelButton.Text = Simulation.Loc.Format("Recall the network ({0})", Fmt.Percent(strength));
            _diplomacyIntelButton.Disabled = false;
            _diplomacyIntelButton.TooltipText = Ui.Tip(Simulation.Loc.Format(
                "Our agents there have reached {0}. They show that share of the country through the fog, " +
                "a truer and fresher estimate of its forces, and from {1} its dossier. " +
                "Their counter-intelligence: {2}. The stronger the network, the likelier it is found.",
                Fmt.Percent(strength), Fmt.Percent(_campaign.Defines.IntelInspectStrength),
                Fmt.Percent(IntelNetworks.CounterIntelligence(_campaign, nation))));
            return;
        }
        var refusal = IntelNetworks.Refusal(_campaign, player, nation);
        _diplomacyIntelButton.Text = Tr("Found an intelligence network");
        _diplomacyIntelButton.Disabled = refusal.Length > 0;
        _diplomacyIntelButton.TooltipText = Ui.Tip(refusal.Length > 0 ? refusal : Simulation.Loc.Format(
            "Agents in their country, for {0}. The network grows a week at a time - slower against a state " +
            "that watches its own - and as it grows it shows more. If it is found, it is lost, and they will " +
            "not forget it.", Fmt.Money(IntelNetworks.Cost(_campaign, player))));
    }

    private void ToggleIntelNetwork()
    {
        if (!ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        var player = _campaign.Player;
        var recall = player.IntelNetworks.ContainsKey(target.Id);
        var command = new IntelNetworkCommand { IssuerId = player.Id, TargetId = target.Id, Action = recall ? "recall" : "found" };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _diplomacyPendingWarTarget = -1;
        _diplomacyFeedback.Text = recall
            ? Tr("The network is recalled. What it learned stays in the files.")
            : player.IntelNetworks.ContainsKey(target.Id)
                ? Tr("The first agents are in place. The network will grow week by week.")
                : Tr(command.Refusal.Length > 0 ? command.Refusal : "The network could not be founded.");
        ForceDiplomacyRefresh();
    }
}
