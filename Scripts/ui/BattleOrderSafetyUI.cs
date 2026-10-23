using System;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void ConfirmLastDefenderWithdrawal(int[] ids, float share, Action withdraw)
    {
        var issuer = _campaign.Player.Id;
        var campaign = _campaign;
        var yieldsPosition = share >= 1f && _campaign.Battles.Any(battle => !battle.Ended &&
            battle.Defenders.Any(ids.Contains) &&
            !_campaign.Armies.Any(army => army.Divisions >= .1f && army.ProvinceId == battle.ProvinceId &&
                WarCoalition.SameSide(_campaign.Wars.Find(war => war.Id == battle.WarId && !war.Ended),
                    army.NationId, battle.DefenderId) && !ids.Contains(army.Id)));
        if (!yieldsPosition) { withdraw(); return; }
        if (GetChildren().OfType<ConfirmationDialog>().Any(dialog => dialog.Visible)) return;
        var confirm = new ConfirmationDialog { Name = "ConfirmLastDefenderWithdrawal",
            Title = Tr("Withdraw the last defenders?"),
            DialogText = Tr("This leaves the province without our defenders and may give it to the enemy. H or RMB on the formation's current province also leaves battle. Orders to other provinces wait until combat ends."),
            Exclusive = true };
        var theme = ScenarioEditorTheme.Create();
        theme.SetStylebox("panel", "AcceptDialog", ScenarioEditorTheme.Surface("shell.background", "shell.border", 16));
        confirm.Theme = theme;
        confirm.GetLabel().AutowrapMode = TextServer.AutowrapMode.WordSmart;
        confirm.GetLabel().CustomMinimumSize = new Vector2(480, 90);
        confirm.GetOkButton().Text = Tr("Retreat");
        confirm.GetCancelButton().Text = Tr("Cancel");
        AddChild(confirm);
        confirm.Confirmed += () =>
        {
            confirm.QueueFree();
            // The user may have changed seats or loaded a different campaign while a
            // dialog was open. Do not issue an order for a newly selected country's units.
            if (ReferenceEquals(_campaign, campaign) && _campaign.Player.Id == issuer) withdraw();
        };
        confirm.Canceled += () => confirm.QueueFree();
        confirm.PopupCentered(new Vector2I(540, 190));
    }
}
