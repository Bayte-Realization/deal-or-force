using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Somebody has put a settlement to you.
///
/// The peace conference is the table you sit down at. This is the other direction, and it
/// did not exist: the AI's negotiation step began by checking whether the other side was a
/// person and giving up if it was, so a war involving a player could only ever end when the
/// player ended it. A country being beaten by a player fought to its last division without
/// once asking to stop, and a country beating a player took nothing and said nothing.
///
/// What arrives here is the same treaty that country would have signed with another AI,
/// with the border it is asking for drawn on the map behind this panel, so the decision is
/// made while looking at the thing being decided rather than at a number.
///
/// It arrives as a letter, not as a summons. The first version threw this window over the
/// whole screen the moment an offer existed and stopped the clock until it was answered, and
/// a player at war with a weaker neighbour had it in their face every few weeks - an
/// interruption no other piece of diplomacy makes, and one that made a war against the
/// computer feel like an argument with a door-to-door salesman. Now a new offer only sounds a
/// cue and shows up where every other incoming paper does: the alert on the strategic HUD,
/// the situation desk, the brief. The map keeps running. The full terms open here when the
/// player clicks one of those, and can be put down again unanswered ("Later"); an offer left
/// alone lapses on its own after the days it stands for (PeaceOffers).
/// </summary>
public partial class DealOrForceGame
{
    private Control _peaceOfferLayer = null!;
    private Label _peaceOfferTitle = null!;
    private Label _peaceOfferBody = null!;
    private Label _peaceOfferDeadline = null!;
    private Button _peaceOfferAccept = null!;
    private Button _peaceOfferRefuse = null!;
    private int _shownPeaceOfferWar = -1;

    /// <summary>The war whose terms the player asked to read, or -1 while the window is shut.</summary>
    private int _requestedPeaceOfferWar = -1;

    /// <summary>
    /// The offers already announced to this seat, as "war:day it was made", so the cue sounds
    /// once per letter and not on every refresh. A load or a change of seat primes this
    /// quietly, as the diplomatic inbox does: receiving the same paper again is not news.
    /// </summary>
    private HashSet<string> _peaceOfferNoticeKeys = new();
    private int _peaceOfferNoticeViewer = -1;

    private void BuildPeaceOfferInterface()
    {
        _peaceOfferLayer = FullLayer("PeaceOffer");
        _peaceOfferLayer.Visible = false;
        _peaceOfferLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_peaceOfferLayer);

        // No veil over the map. The border being asked for is drawn out there while this is
        // open, and dimming it would hide the only part of the offer that is not a sentence.
        var panel = TexturedPanel(_peaceOfferLayer, PanelSurface.Modal);
        Ui.Place(panel, "modal.peace_offer");
        var rows = PanelRows(panel, 30, 26, 12);

        var offerHead = ScreenHeader(rows, "PEACE OFFER",
            "Review the proposed settlement border on the map before signing or refusing.");
        _peaceOfferTitle = HeaderTitle(offerHead);

        _peaceOfferBody = Title("", 15, Ui.Colour("text.body_soft"));
        _peaceOfferBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _peaceOfferBody.SizeFlagsVertical = SizeFlags.ExpandFill;
        rows.AddChild(_peaceOfferBody);

        _peaceOfferDeadline = Title("", 12, Ui.Colour("text.faint"));
        _peaceOfferDeadline.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_peaceOfferDeadline);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        rows.AddChild(actions);
        _peaceOfferAccept = ActionButton("Sign it", () => AnswerPeaceOffer(true), 190, 46, true);
        _peaceOfferAccept.TooltipText = Ui.Tip(Tr("Accept the terms as written. The war ends today."));
        actions.AddChild(_peaceOfferAccept);
        _peaceOfferRefuse = ActionButton("Refuse", () => AnswerPeaceOffer(false), 190, 46);
        _peaceOfferRefuse.TooltipText = Ui.Tip(Tr("Send them away. The war goes on and they will not ask again for a while."));
        actions.AddChild(_peaceOfferRefuse);
        var later = ActionButton("Later", ClosePeaceOffer, 190, 46);
        later.TooltipText = Ui.Tip(Tr("Put the terms down without answering. The offer waits in your alerts until it lapses."));
        actions.AddChild(later);
    }

    /// <summary>
    /// Called on every interface refresh. Announces offers that have just arrived - a cue and
    /// a line in the feedback strip, nothing that takes the screen or the clock - and keeps the
    /// terms window, if the player opened it, showing an offer that still stands.
    /// </summary>
    private void RefreshPeaceOfferInterface()
    {
        if (_peaceOfferLayer is null) return;
        if (_campaign?.Player is null)
        {
            HidePeaceOffer();
            return;
        }

        var waiting = PeaceOffers.AwaitingAnswer(_campaign, _campaign.Player.Id);
        AnnounceNewPeaceOffers(waiting);

        if (_requestedPeaceOfferWar < 0 || !_hudLayer.Visible || _peaceLayer is { Visible: true })
        {
            HidePeaceOffer();
            return;
        }

        // The letter the player opened may have been answered elsewhere or lapsed; fall back
        // to the next one only while the window is already open, never by opening it.
        var war = waiting.Find(candidate => candidate.Id == _requestedPeaceOfferWar);
        if (war is null)
        {
            if (waiting.Count == 0)
            {
                ClosePeaceOffer();
                return;
            }
            war = waiting[0];
            _requestedPeaceOfferWar = war.Id;
        }
        ShowPeaceOffer(war, waiting.Count);
    }

    /// <summary>
    /// Open the terms of one offer (or the oldest one waiting when no war is named), because
    /// the player clicked its alert, desk entry or brief line. This is the only way the window
    /// opens. It does not pause the game.
    /// </summary>
    internal void OpenPeaceOffer(int warId = -1)
    {
        if (_campaign?.Player is null) return;
        var waiting = PeaceOffers.AwaitingAnswer(_campaign, _campaign.Player.Id);
        if (waiting.Count == 0) return;
        var war = warId >= 0 ? waiting.Find(candidate => candidate.Id == warId) : null;
        _requestedPeaceOfferWar = (war ?? waiting[0]).Id;
        _shownPeaceOfferWar = -1;
        RefreshPeaceOfferInterface();
    }

    private void AnnounceNewPeaceOffers(List<StrategicWar> waiting)
    {
        var keys = new HashSet<string>();
        foreach (var war in waiting) keys.Add($"{war.Id}:{war.PendingOfferDay}");
        var primed = _peaceOfferNoticeViewer == _campaign.Player.Id && !_observerMode;
        StrategicWar arrived = null;
        if (primed)
            foreach (var war in waiting)
                if (!_peaceOfferNoticeKeys.Contains($"{war.Id}:{war.PendingOfferDay}")) { arrived = war; break; }
        _peaceOfferNoticeKeys = keys;
        _peaceOfferNoticeViewer = _campaign.Player.Id;
        if (arrived is null) return;
        PlayStrategicCue("deal_received");
        var proposer = _campaign.NationById(arrived.PendingOfferFromId);
        SetArmyFeedback(Simulation.Loc.Format("{0} has put peace terms to you. Open them from the alerts when you are ready.",
            proposer?.DisplayName ?? Tr("The other side")));
    }

    private void ShowPeaceOffer(StrategicWar war, int waitingCount)
    {
        var days = war.PendingOfferExpiresDay - _campaign.ElapsedDays;
        _peaceOfferDeadline.Text = waitingCount > 1
            ? $"They will wait {days:N0} more day(s). {waitingCount - 1} other offer(s) are behind this one."
            : $"They will wait {days:N0} more day(s); after that the offer lapses.";
        if (_shownPeaceOfferWar == war.Id && _peaceOfferLayer.Visible) return;
        _shownPeaceOfferWar = war.Id;

        var proposer = _campaign.NationById(war.PendingOfferFromId);
        _peaceOfferTitle.Text = Simulation.Loc.Format("{0} OFFER TERMS",
            proposer?.Name?.ToUpperInvariant() ?? Tr("THEY"));
        _peaceOfferBody.Text = Tr("The border shown on the map is a proposal. Nothing changes ownership until you sign it.") +
            "\n\n" + PeaceOffers.Describe(_campaign, war);

        // The ground they are asking for, in their colours, on the map behind the panel.
        if (war.PendingOfferIsWritten && war.PendingOffer is { ReleaseRegions: false } && proposer is not null)
            _globe.SetPeacePreview(war.PendingOffer.Provinces, proposer.MapIndex);
        else
            _globe.ClearPeacePreview();

        _peaceOfferLayer.Visible = true;
        _peaceOfferLayer.MoveToFront();
    }

    /// <summary>Put the terms down unanswered. The offer stays in the alerts until it lapses.</summary>
    private void ClosePeaceOffer()
    {
        _requestedPeaceOfferWar = -1;
        HidePeaceOffer();
    }

    private void HidePeaceOffer()
    {
        if (_peaceOfferLayer is null || !_peaceOfferLayer.Visible) return;
        _peaceOfferLayer.Visible = false;
        _shownPeaceOfferWar = -1;
        _globe.ClearPeacePreview();
    }

    private void AnswerPeaceOffer(bool accept)
    {
        if (_campaign?.Player is null || _shownPeaceOfferWar < 0) return;
        _campaign.Commands.Enqueue(new AnswerPeaceOfferCommand
        {
            IssuerId = _campaign.Player.Id, WarId = _shownPeaceOfferWar, Accept = accept
        });
        _campaign.Commands.ApplyAll(_campaign);

        var war = _campaign.Wars.Find(candidate => candidate.Id == _shownPeaceOfferWar);
        ClosePeaceOffer();
        if (accept && war is { Ended: true }) SetArmyFeedback(war.PeaceSummary);
        else if (!accept) SetArmyFeedback(Tr("The offer was refused. The war continues."));
        Refresh();
    }
}
