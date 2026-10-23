using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void RefreshOutreachButton(Button button, Nation target)
    {
        if (button is null || target is null) return;
        var issuer = _campaign.Player.Id;
        var active = DiplomaticOutreach.Of(CampaignDiplomacy.Peek(_campaign,issuer,target.Id),issuer) is not null;
        var refusal = DiplomaticOutreach.Refusal(_campaign,issuer,target.Id,!active);
        button.Text = Tr(active ? "Recall diplomatic mission" : "Improve relations");
        button.Disabled = refusal.Length != 0;
        button.TooltipText = Ui.Tip(DiplomaticOutreach.Description(_campaign,issuer,target.Id) +
            (refusal.Length == 0 ? "" : "\n\n"+refusal));
    }

    private string SendOrRecallDiplomaticMission(Nation target)
    {
        var issuer = _campaign.Player.Id;
        var command = new ImproveRelationsCommand { IssuerId = issuer, TargetId = target.Id,
            Active = DiplomaticOutreach.Of(CampaignDiplomacy.Peek(_campaign,issuer,target.Id),issuer) is null };
        _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        return command.Changed ? Tr(command.Active ? "Diplomatic mission dispatched. Goodwill grows gradually, not immediately."
            : "Diplomatic mission recalled. No further weekly payments.") : command.Refusal;
    }
}
