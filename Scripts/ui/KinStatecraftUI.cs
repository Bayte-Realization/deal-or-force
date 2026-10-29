using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The region panel's block for a country's own people across a border (KinStatecraft):
/// how many of ours live in this foreign region, and what may be done about them - work
/// them, or send volunteers to a rising of theirs. A refused order keeps its button and
/// says why in the tooltip, and the answer to a click is the sentence the simulation gave.
/// </summary>
public partial class DealOrForceGame
{
    private VBoxContainer _kinBox;
    private Label _kinStatus;
    private HFlowContainer _kinButtons;
    private Label _kinFeedback;

    private void EnsureKinBox()
    {
        if (_kinBox is not null || _regionControls is null) return;
        _kinBox = new VBoxContainer { Visible = false };
        _kinBox.AddThemeConstantOverride("separation", 5);
        _regionControls.AddChild(_kinBox);
        _kinBox.AddChild(Title("OUR PEOPLE HERE", 12, Ui.Colour("text.caption")));
        _kinStatus = Title("", 12, Ui.Colour("text.bright"));
        _kinStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _kinBox.AddChild(_kinStatus);
        _kinButtons = new HFlowContainer();
        _kinButtons.AddThemeConstantOverride("h_separation", 5);
        _kinButtons.AddThemeConstantOverride("v_separation", 5);
        _kinBox.AddChild(_kinButtons);
        _kinFeedback = Title("", 12, Ui.Colour("text.accent_dim"));
        _kinFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _kinBox.AddChild(_kinFeedback);
    }

    private void RefreshKinActions()
    {
        EnsureKinBox();
        if (_kinBox is null) return;
        foreach (var child in _kinButtons.GetChildren()) child.QueueFree();
        var player = _campaign?.Player;
        var region = _selectedState;
        if (player is null || region is null || _observerMode || !_campaign.FeatureEnabled("movements"))
        {
            _kinBox.Visible = false;
            return;
        }
        var owner = _campaign.NationByMapIndex(region.NationMapIndex);
        if (owner is null || owner.Id == player.Id || owner.Movement is { Landless: true } || player.Movement is not null)
        {
            _kinBox.Visible = false;
            return;
        }
        var (ours, total, share) = KinStatecraft.Kin(_campaign, player, region);
        // Nothing of ours there: the block has nothing to say and stays out of the way.
        if (ours <= 0)
        {
            _kinBox.Visible = false;
            return;
        }
        _kinStatus.Text = Simulation.Loc.Format("Ours: {0} of {1} ({2}).", Fmt.People(ours), Fmt.People(total), Fmt.Percent(share));
        // Who runs the paperwork here, if it is not the flag above the door - ours or somebody
        // else's. It belongs on the same line as the people, because that is what it is about.
        var offices = ParallelAdministration.Describe(_campaign, region);
        if (offices.Length > 0) _kinStatus.Text += "\n" + offices;
        foreach (var kind in KinStatecraft.Kinds)
        {
            var refusal = KinStatecraft.Refusal(_campaign, player, region, kind.Id);
            var cost = KinStatecraft.Cost(_campaign, player, region, kind.Id);
            var button = ActionButton(Simulation.Loc.Get(kind.Name), () => IssueKin(kind.Id), 0, 30);
            var price = cost > 0f ? Simulation.Loc.Format("Cost: {0}.", Fmt.Money(cost)) : "";
            button.Disabled = refusal.Length > 0;
            button.TooltipText = Ui.Tip(Tr(kind.Help) + (price.Length > 0 ? "\n\n" + price : "") +
                                        (refusal.Length > 0 ? "\n\n" + refusal : ""));
            _kinButtons.AddChild(button);
        }
        _kinBox.Visible = true;
        _regionControls.Visible = true;
    }

    private void IssueKin(string action)
    {
        if (_campaign?.Player is null || _selectedState is null) return;
        var command = new KinStatecraftCommand
        {
            IssuerId = _campaign.Player.Id, Action = action, RegionMapIndex = _selectedState.MapIndex
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _kinFeedback.Text = command.Result.Length > 0 ? command.Result : command.Done ? "✓" : "";
        if (command.Done && action == "volunteers") PlayStrategicCue("war_declared");
        RefreshKinActions();
    }
}
