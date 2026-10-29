using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    // One row per act: the act, what it costs, whether it can be asked now, and the button that
    // asks. The rows are held so a refresh can repaint the edge and the meta line without
    // rebuilding the diplomacy screen under the pointer.
    private readonly Dictionary<DiplomaticAct, (HBoxContainer Row, Panel Edge, Label Meta, Button Action)> _diplomaticActRows = new();

    private void AddDiplomaticActButtons()
    {
        if (_diplomacyActionRows is null) return;
        _diplomaticActRows.Clear();
        _diplomacyActionRows.AddChild(Title("COOPERATION", 12, Ui.Colour("text.caption")));
        foreach (var act in DiplomaticActs.All)
        {
            var captured = act;
            var (row, edge, meta, button) = ActionRow("DiplomaticAct_" + act, DiplomaticActs.Name(act), "",
                () => ProposeDiplomaticAct(captured), "Propose");
            // A narrow action rail cannot put a complete name and refusal beside a 104px
            // button. Read the paper first, then sign below it; keep the same command.
            var words = (VBoxContainer)meta.GetParent();
            row.RemoveChild(button); words.AddChild(button);
            button.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            button.SetMeta("diplomacy_search_name", DiplomaticActs.Name(act));
            foreach (var label in words.GetChildren())
                if (label is Label text)
                { text.AutowrapMode = TextServer.AutowrapMode.WordSmart; text.ClipText = false; }
            button.CustomMinimumSize = new Vector2(104, 30);
            _diplomacyActionRows.AddChild(row);
            _diplomaticActRows[act] = (row, edge, meta, button);
        }
    }

    private void RefreshDiplomaticActButtons(Nation target)
    {
        var player = _campaign?.Player;
        var valid = player is { IsHuman: true } && target is not null && target != player && !_observerMode;
        foreach (var (act, row) in _diplomaticActRows)
        {
            if (!GodotObject.IsInstanceValid(row.Row)) continue;
            row.Row.Visible = valid;
            if (!valid) continue;
            var refusal = DiplomaticActs.Refusal(_campaign, player, target, act);
            row.Meta.Text = refusal.Length > 0
                ? Tr(refusal)
                : Loc.Format("{0} from the treasury", Fmt.Money(DiplomaticActs.Cost(_campaign, act)));
            row.Meta.TooltipText = Ui.Tip(row.Meta.Text);
            PaintRow(row.Edge, refusal.Length > 0 ? "surface.line_faint" : "state.info");
            row.Action.Disabled = refusal.Length > 0;
            row.Action.TooltipText = Ui.Tip(refusal.Length > 0 ? Tr(refusal)
                : Tr(DiplomaticActs.Name(act)) + "\n" +
                  Tr("A human government signs before anything changes. Land is not on the paper."));
        }
    }

    private void ProposeDiplomaticAct(DiplomaticAct act)
    {
        var player = _campaign?.Player;
        var target = DiplomacyTarget();
        if (player is null || target is null || _observerMode) return;
        var command = new DiplomaticActCommand
        {
            IssuerId = player.Id, TargetId = target.Id, Act = act, Step = DiplomaticActStep.Propose
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        if (IsInstanceValid(_diplomacyFeedback)) _diplomacyFeedback.Text = Tr(command.Result);
        if (IsInstanceValid(_mapDiplomacyFeedback)) _mapDiplomacyFeedback.Text = Tr(command.Result);
        RefreshDiplomaticActionViews();
    }

    private void AddDiplomaticActInboxRows()
    {
        var host = _campaign.Player;
        foreach (var relation in _campaign.Relations.Values)
        {
            if ((relation.A != host.Id && relation.B != host.Id) || !DiplomaticActs.Pending(_campaign, relation) ||
                relation.DiplomaticActBy == host.Id) continue;
            var asker = _campaign.NationById(relation.DiplomaticActBy);
            if (asker is null || relation.DiplomaticActKind < 0) continue;
            var act = (DiplomaticAct)relation.DiplomaticActKind;
            var column = new VBoxContainer { Name = "DiplomaticActLetter_" + asker.Id };
            _dealInboxRows.AddChild(column);
            var line = WrappedUltimatumText(Loc.Format("{0} proposes {1}. Nothing is signed until you do. No land changes hands.",
                asker.DisplayName, Tr(DiplomaticActs.Name(act))), 14);
            line.AutowrapMode = TextServer.AutowrapMode.Off;
            line.ClipText = true;
            line.CustomMinimumSize = new Vector2(0, 22);
            column.AddChild(line);
            var accept = ActionButton("Sign", () => AnswerDiplomaticAct(asker, DiplomaticActStep.Accept), 0, 32);
            accept.ClipText = true;
            accept.AutowrapMode = TextServer.AutowrapMode.Off;
            column.AddChild(accept);
            var decline = ActionButton("Decline", () => AnswerDiplomaticAct(asker, DiplomaticActStep.Decline), 0, 32);
            decline.ClipText = true;
            decline.AutowrapMode = TextServer.AutowrapMode.Off;
            column.AddChild(decline);
        }
    }

    private void AnswerDiplomaticAct(Nation asker, DiplomaticActStep step)
    {
        if (_campaign?.Player is null || asker is null) return;
        var command = new DiplomaticActCommand
        {
            IssuerId = _campaign.Player.Id, TargetId = asker.Id, Step = step
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _dealInboxMessage = Tr(command.Result);
        RefreshDealInbox(force: true);
    }
}
