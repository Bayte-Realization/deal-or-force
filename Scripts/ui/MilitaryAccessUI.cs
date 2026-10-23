using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private sealed record AccessControls(VBoxContainer Box, Button Ask, Button Grant, Label Status, Func<Nation> Target);
    private readonly List<AccessControls> _militaryAccessControls = new();

    private void AddMilitaryAccessControls(VBoxContainer parent, Func<Nation> target, string prefix)
    {
        var box = new VBoxContainer { Name = prefix + "MilitaryPassage" };
        parent.AddChild(box);
        var ask = ActionButton("Request military access", () =>
        {
            var nation = target();
            var player = _campaign?.Player;
            if (player is null || nation is null) return;
            SendMilitaryAccess(nation, MilitaryAccess.Granted(_campaign, player.Id, nation.Id)
                ? MilitaryAccessAction.Renounce : MilitaryAccessAction.Request);
        }, 0, 32);
        ask.Name = prefix + "RequestMilitaryAccess"; box.AddChild(ask);
        var grant = ActionButton("Grant military access", () =>
        {
            var nation = target();
            var player = _campaign?.Player;
            if (player is null || nation is null) return;
            SendMilitaryAccess(nation, MilitaryAccess.Granted(_campaign, nation.Id, player.Id)
                ? MilitaryAccessAction.Revoke : MilitaryAccessAction.Grant);
        }, 0, 32);
        grant.Name = prefix + "GrantMilitaryAccess"; box.AddChild(grant);
        var status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        status.AddThemeFontSizeOverride("font_size", 11); box.AddChild(status);
        _militaryAccessControls.Add(new(box, ask, grant, status, target));
        RefreshMilitaryAccessControls();
    }

    private void RefreshMilitaryAccessControls()
    {
        _militaryAccessControls.RemoveAll(row => !IsInstanceValid(row.Box));
        foreach (var row in _militaryAccessControls)
        {
            var player = _campaign?.Player; var target = row.Target();
            var valid = !_observerMode && player is { IsHuman: true } && target is not null && target != player;
            row.Box.Visible = valid;
            if (!valid) continue;
            var ours = MilitaryAccess.Granted(_campaign, player.Id, target.Id);
            var theirs = MilitaryAccess.Granted(_campaign, target.Id, player.Id);
            var refusal = MilitaryAccess.Refusal(_campaign, player, target);
            row.Ask.Text = Tr(ours ? "Renounce military access" : "Request military access");
            row.Ask.Disabled = !ours && refusal.Length > 0;
            row.Grant.Text = Tr(theirs ? "Revoke military access" : "Grant military access");
            row.Grant.Disabled = !theirs && (MilitaryAccess.Hostile(_campaign, player.Id, target.Id) ||
                target.Movement is not null || player.Movement is not null || target.Population <= 0 || player.Population <= 0);
            row.Ask.TooltipText = Ui.Tip(!ours && refusal.Length > 0 ? Tr(refusal)
                : Tr("Military access permits transit, not occupation or an alliance. A human government must explicitly approve a request."));
            row.Grant.TooltipText = Ui.Tip(Tr("Let their troops cross your land. Revocation sends unauthorised visitors home safely; it does not destroy them."));
            row.Status.Text = Loc.Format("Your access: {0} · Their access: {1}",
                Tr(MilitaryAccess.CanEnter(_campaign, player.Id, (ushort)target.MapIndex) ? "Passage allowed" : "Passage unavailable"),
                Tr(MilitaryAccess.CanEnter(_campaign, target.Id, (ushort)player.MapIndex) ? "Passage allowed" : "Passage unavailable"));
        }
    }

    private void SendMilitaryAccess(Nation target, MilitaryAccessAction action)
    {
        if (_campaign?.Player is null || target is null || _observerMode) return;
        var command = new MilitaryAccessCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id, Action = action };
        _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        var feedback = Tr(command.Result);
        if (IsInstanceValid(_diplomacyFeedback)) _diplomacyFeedback.Text = feedback;
        if (IsInstanceValid(_countryActionFeedback)) _countryActionFeedback.Text = feedback;
        if (IsInstanceValid(_mapDiplomacyFeedback)) _mapDiplomacyFeedback.Text = feedback;
        RefreshMilitaryAccessControls(); RefreshDiplomaticActionViews();
    }

    private void AddMilitaryAccessInboxRows()
    {
        var host = _campaign.Player;
        foreach (var r in _campaign.Relations.Values.OrderBy(r => r.A).ThenBy(r => r.B))
        {
            if ((r.A != host.Id && r.B != host.Id) || !MilitaryAccess.Pending(_campaign, r) || r.MilitaryAccessRequestedBy == host.Id) continue;
            var guest = _campaign.NationById(r.MilitaryAccessRequestedBy);
            if (guest is null) continue;
            var column = new VBoxContainer { Name = "MilitaryAccessLetter_" + guest.Id };
            _dealInboxRows.AddChild(column);
            column.AddChild(WrappedUltimatumText(Loc.Format("{0} requests military access.", guest.DisplayName), 14));
            column.AddChild(ActionButton("Grant military access", () => ReplyMilitaryAccess(guest, MilitaryAccessAction.Grant), 0, 32));
            column.AddChild(ActionButton("Decline", () => ReplyMilitaryAccess(guest, MilitaryAccessAction.Decline), 0, 32));
        }
    }

    private void ReplyMilitaryAccess(Nation guest, MilitaryAccessAction action)
    {
        SendMilitaryAccess(guest, action);
        RefreshDealInbox(force: true);
    }
}
