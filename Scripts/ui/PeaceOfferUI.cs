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

        rows.AddChild(Title("PEACE OFFER", 12, Ui.Colour("text.caption")));
        _peaceOfferTitle = Title("", 24, Ui.Colour("text.accent"));
        _peaceOfferTitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_peaceOfferTitle);

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
        _peaceOfferAccept = ActionButton("Sign it", () => AnswerPeaceOffer(true), 220, 46, true);
        _peaceOfferAccept.TooltipText = Ui.Tip(Tr("Accept the terms as written. The war ends today."));
        actions.AddChild(_peaceOfferAccept);
        _peaceOfferRefuse = ActionButton("Refuse", () => AnswerPeaceOffer(false), 220, 46);
        _peaceOfferRefuse.TooltipText = Ui.Tip(Tr("Send them away. The war goes on and they will not ask again for a while."));
        actions.AddChild(_peaceOfferRefuse);
    }

    /// <summary>Show whatever is waiting on the player, and draw it on the map behind.</summary>
    private void RefreshPeaceOfferInterface()
    {
        if (_peaceOfferLayer is null) return;
        if (_campaign?.Player is null || !_hudLayer.Visible || _peaceLayer is { Visible: true })
        {
            HidePeaceOffer();
            return;
        }

        var waiting = PeaceOffers.AwaitingAnswer(_campaign, _campaign.Player.Id);
        if (waiting.Count == 0)
        {
            HidePeaceOffer();
            return;
        }

        var war = waiting[0];
        if (_shownPeaceOfferWar == war.Id && _peaceOfferLayer.Visible) return;
        var newlyShown = _shownPeaceOfferWar != war.Id;
        _shownPeaceOfferWar = war.Id;

        var proposer = _campaign.NationById(war.PendingOfferFromId);
        _campaign.IsPaused = true;
        _pause.Text = Tr("▶");
        _peaceOfferTitle.Text = Simulation.Loc.Format("{0} OFFER TERMS",
            proposer?.Name?.ToUpperInvariant() ?? Tr("THEY"));
        _peaceOfferBody.Text = PeaceOffers.Describe(_campaign, war);

        var days = war.PendingOfferExpiresDay - _campaign.ElapsedDays;
        _peaceOfferDeadline.Text = waiting.Count > 1
            ? $"They will wait {days:N0} more day(s). {waiting.Count - 1} other offer(s) are behind this one."
            : $"They will wait {days:N0} more day(s); after that the offer lapses.";

        // The ground they are asking for, in their colours, on the map behind the panel.
        if (war.PendingOfferIsWritten && war.PendingOffer is { ReleaseRegions: false } && proposer is not null)
            _globe.SetPeacePreview(war.PendingOffer.Provinces, proposer.MapIndex);
        else
            _globe.ClearPeacePreview();

        _peaceOfferLayer.Visible = true;
        _peaceOfferLayer.MoveToFront();
        if (newlyShown) PlayStrategicCue("event_alert");
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
        HidePeaceOffer();
        if (accept && war is { Ended: true }) SetArmyFeedback(war.PeaceSummary);
        else if (!accept) SetArmyFeedback(Tr("The offer was refused. The war continues."));
        Refresh();
    }
}
