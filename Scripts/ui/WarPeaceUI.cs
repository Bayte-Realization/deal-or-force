using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    // Involvement alone includes allies on our own side: only opponents can negotiate here.
    private StrategicWar DiplomaticPeaceWar(Nation target) => _campaign?.Player is null || target is null
        ? null
        : _campaign.Wars.FirstOrDefault(war => !war.Ended && war.Opposes(_campaign.Player.Id, target.Id));

    private void RefreshDiplomaticPeaceButton(Button button, Nation target)
    {
        var war = DiplomaticPeaceWar(target);
        button.Visible = war is not null;
        button.TooltipText = war is null ? "" : Ui.Tip(Loc.Format(
            "Open negotiations for {0}. Review the terms before proposing peace; opening this window does not end the war.", war.Name));
    }

    private void OpenDiplomaticPeace(Nation target)
    {
        var war = DiplomaticPeaceWar(target);
        if (war is null) return;
        CloseDiplomacyScreen();
        HideMapSelection(false);
        OpenPeaceConference(war);
        _peaceLayer.MoveToFront();
    }

    /// <summary>
    /// The war a shortcut means when the player has not named one. Fine while there is a
    /// single war; the war ledger names the war it is settling, which is why the fixed
    /// terms live there now rather than in a panel that could only ever mean this one.
    /// </summary>
    private StrategicWar PlayerWar() => _campaign.Wars.FirstOrDefault(war => !war.Ended && war.Involves(_campaign.Player.Id));

    private void ProposePeace(PeaceTerms terms) => ProposePeace(PlayerWar(), terms);

    /// <summary>
    /// Settle one named war.
    ///
    /// The shortcut buttons used to settle whichever war the player had entered first,
    /// which is right while there is one and quietly wrong the moment there are two - a
    /// player looking at their war with Peru could sign away their war with Chile.
    /// </summary>
    private void ProposePeace(StrategicWar war, PeaceTerms terms)
    {
        if (war is null) return;
        var wasEnded = war.Ended;
        _campaign.Commands.Enqueue(new ProposePeaceCommand { IssuerId = _campaign.Player.Id, WarId = war.Id, Terms = terms });
        _campaign.Commands.ApplyAll(_campaign);
        if (!wasEnded && war.Ended) PlayStrategicCue("peace_signed");
        if (war.Ended) ShowWarLedgerList();
        Refresh();
        RefreshWarLedger();
    }

    private string PeaceHistory()
    {
        var ended = _campaign.Wars.Where(war => war.Ended).OrderByDescending(war => war.EndedDay).Take(4).ToArray();
        if (ended.Length == 0) return "";
        return "\n\nRECENT PEACE SETTLEMENTS\n\n" + string.Join("\n\n", ended.Select(war =>
            $"{war.Name} · day {war.EndedDay:N0}\n{war.PeaceSummary}"));
    }
}
