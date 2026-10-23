using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void BuildElectionSection(VBoxContainer parent, Nation nation)
    {
        if (!ElectoralPolitics.Enabled(_campaign) || !ElectoralPolitics.Eligible(nation)) return;
        var panel = new VBoxContainer { Name = "ElectionPanel", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel.AddThemeConstantOverride("separation", 5); parent.AddChild(panel);
        panel.AddChild(RowLabel("ELECTIONS & SUCCESSION", 12, Ui.Colour("text.accent")));
        var e = nation.Elections;
        if (e is null)
        { panel.AddChild(Title(Tr("Electoral calendar will be established on the next simulation day."), 12, Ui.Colour("text.body_soft"))); return; }
        var due = e.Suspended ? Tr("Elections suspended") : Loc.Format("Next election in {0} day(s)", Math.Max(0, nation.NextElectionDay - _campaign.ElapsedDays));
        panel.AddChild(Title(due + " · " + Loc.Format("Institutional control: {0:0}%", e.Control * 100), 13, Ui.Colour("text.body_soft")));
        panel.AddChild(Title(Loc.Format("Government approval: {0:0}%", ElectoralPolitics.Approval(_campaign, nation) * 100), 12, Ui.Colour("text.body_soft")));
        if (e.Result.Length > 0)
            panel.AddChild(Title(Tr(e.Result) + (e.LastDay >= 0 ? " · " + Loc.Format("Incumbent vote: {0:0.0}%", e.IncumbentVote * 100) : ""), 12, Ui.Colour("text.body_soft")));
        if (e.TransferOnDay >= 0)
            panel.AddChild(Title(Loc.Format("Winner: {0}. Handover in {1} day(s).", e.Winner, Math.Max(0, e.TransferOnDay - _campaign.ElapsedDays)), 12, Ui.Colour("text.accent")));
        var actions = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddThemeConstantOverride("h_separation", 6); actions.AddThemeConstantOverride("v_separation", 5); panel.AddChild(actions);
        foreach (var action in Enum.GetValues<ElectionAction>())
        {
            var captured = action;
            if (e.TransferOnDay >= 0 ? action is not (ElectionAction.AcceptResult or ElectionAction.RefuseResult) :
                action is ElectionAction.AcceptResult or ElectionAction.RefuseResult) continue;
            if (e.Suspended ? action != ElectionAction.Resume : action == ElectionAction.Resume) continue;
            var may = ElectoralPolitics.MayAct(_campaign, nation, action, out var cost, out var why);
            var label = action switch { ElectionAction.AcceptResult => "Accept result", ElectionAction.RefuseResult => "Refuse to leave",
                ElectionAction.Postpone => "Postpone election", ElectionAction.Suspend => "Suspend / cancel elections", _ => "Restore elections" };
            var button = ActionButton(Tr(label) + Loc.Format(" · {0:0.#} PP", cost), () =>
            {
                _campaign.Commands.Enqueue(new ElectionCommand { IssuerId = nation.Id, Action = captured });
                _campaign.Commands.ApplyAll(_campaign); RefreshStateAffairs(); Refresh();
            }, 0, 30);
            button.Name = "Election" + action; button.Disabled = !may;
            button.TooltipText = Ui.Tip(may ? Tr("Rejecting or restricting elections raises unrest and lowers government approval. Ignoring a defeat transfers power at the deadline.") : why);
            actions.AddChild(button);
        }
    }
}
