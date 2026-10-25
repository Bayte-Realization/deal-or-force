using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void AddThirdPartyCrisisActions(Nation a, Nation b)
    {
        var player = _campaign.Player;
        _conflictsPage.AddChild(Caption("WHAT YOU CAN DO"));
        _conflictsPage.AddChild(WrappedUltimatumText(
            "A Great Power can mediate this crisis or deter an aggressor by backing either side. Intervention is not a declaration of war.", 14));
        void Add(string title, string name, Func<string> check, Func<ICampaignCommand> command, Func<ICampaignCommand, bool> succeeded, string success)
        {
            var refusal = _observerMode ? Tr("Observer mode cannot issue diplomatic orders.") : check();
            var button = ActionButton(title, () =>
            {
                var now = _observerMode ? Tr("Observer mode cannot issue diplomatic orders.") : check();
                if (now.Length > 0) { _conflictsFeedback.Text = now; return; }
                var order = command(); Submit(order);
                RefreshConflictsScreen(true);
                _conflictsFeedback.Text = Tr(succeeded(order) ? success : "Sent to the host.");
            }, 0, 36, iconPath: Icon("diplomacy"));
            button.Name = name; button.Disabled = refusal.Length > 0; button.TooltipText = Ui.Tip(refusal);
            _conflictsPage.AddChild(button);
            if (refusal.Length > 0) _conflictsPage.AddChild(WrappedUltimatumText(refusal, 12));
        }
        Add(Loc.Format("Offer mediation ({0})", Fmt.Money(_campaign.Defines.CrisisMediationCost)), "MediateForeignCrisis",
            () => Escalation.MediationBlock(_campaign, player, a, b),
            () => new MediateCrisisCommand { IssuerId = player.Id, MediatorId = player.Id, TargetAId = a.Id, TargetBId = b.Id },
            order => ((MediateCrisisCommand)order).Mediated, "Mediation opened. Its cost was paid and tensions were reduced.");
        foreach (var protege in new[] { a, b })
        {
            var aggressor = protege.Id == a.Id ? b : a;
            Add(Loc.Format("Deter aggression against {0}", protege.DisplayName), "InterveneFor_" + protege.Id,
                () => Escalation.InterventionBlock(_campaign, player, protege, aggressor),
                () => new InterveneCrisisCommand { IssuerId = player.Id, PowerId = player.Id, ProtegeId = protege.Id, AggressorId = aggressor.Id },
                order => ((InterveneCrisisCommand)order).Intervened, "Great Power intervention recorded. No war was declared.");
        }
    }
}
