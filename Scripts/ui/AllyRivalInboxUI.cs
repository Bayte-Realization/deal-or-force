using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void AddAllyRivalInboxRows()
    {
        var host = _campaign.Player;
        if (host is null) return;
        foreach (var relation in _campaign.Relations.Values.OrderBy(relation => relation.A).ThenBy(relation => relation.B))
        {
            if ((relation.A != host.Id && relation.B != host.Id) || !AllyRivalShift.Pending(_campaign, relation) ||
                relation.RivalShiftPaperBy == host.Id) continue;
            var asker = _campaign.NationById(relation.RivalShiftPaperBy);
            if (asker is null) continue;
            var column = new VBoxContainer { Name = "AllyRivalLetter_" + asker.Id };
            _dealInboxRows.AddChild(column);
            var sentence = AllyRivalShift.Line(_campaign, relation);
            var line = WrappedUltimatumText(sentence, 14);
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(line);
            var end = ActionButton("End the partnership", () => AnswerAllyRival(asker, true), 0, 32);
            end.Name = "AllyRivalEnd_" + asker.Id;
            end.TooltipText = Ui.Tip(sentence);
            column.AddChild(end);
            var keep = ActionButton("Keep the partnership", () => AnswerAllyRival(asker, false), 0, 32);
            keep.Name = "AllyRivalKeep_" + asker.Id;
            keep.TooltipText = Ui.Tip(Tr("The partnership stays. No land changed hands."));
            column.AddChild(keep);
        }
    }

    private void AnswerAllyRival(Nation asker, bool accept)
    {
        if (_campaign?.Player is null || asker is null || _observerMode) return;
        var command = new AnswerAllyRivalCommand
        {
            IssuerId = _campaign.Player.Id, TargetId = asker.Id, Accept = accept
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _dealInboxMessage = Tr(command.Result);
        RefreshDealInbox(force: true);
    }
}
