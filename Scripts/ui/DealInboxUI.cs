using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Button _dealInboxHudButton, _diplomacyInboxButton;
    private VBoxContainer _dealInboxRows, _dealInboxDetail;
    private ScrollContainer _dealInboxScroll;
    private HFlowContainer _dealInboxActions;
    private bool _dealInboxVisible;
    private int _dealInboxSelected = -1, _dealInboxConfirm = -1;
    private string _dealInboxKey = "", _dealInboxConfirmKey = "", _dealInboxMessage = "";
    private int _dealNoticeViewer = -1;
    private bool _dealNoticeObserver;
    private System.Collections.Generic.HashSet<int> _dealNoticeIds = new();

    private string DealInboxKey()
    {
        var paper = _campaign.DealOffers.FirstOrDefault(o => o.Id == _dealInboxSelected);
        return $"{Loc.Language}|{_campaign.Player.Id}|{_campaign.ElapsedDays}|{_campaign.MapRevision}|" +
            $"{_campaign.DealInboxRevision}|{_campaign.NationById(paper?.FromId ?? -1)?.Treasury:R}|" +
            $"{_campaign.NationById(paper?.ToId ?? -1)?.Treasury:R}|{AssistanceInboxKey()}";
    }

    private void RefreshNegotiationNotifications()
    {
        var count = _observerMode ? 0 : DealNegotiations.Incoming(_campaign, _campaign.Player.Id) + ForeignAssistance.Incoming(_campaign, _campaign.Player.Id);
        var incoming = _observerMode ? new System.Collections.Generic.HashSet<int>() : _campaign.DealOffers
            .Where(o => o.Pending && o.ToId == _campaign.Player.Id && o.ExpiresDay > _campaign.ElapsedDays).Select(o => o.Id).ToHashSet();
        var assistance = _observerMode ? new System.Collections.Generic.HashSet<int>() : _campaign.AssistanceRequests
            .Where(r => r.State == ForeignAssistanceState.Pending && r.DonorId == _campaign.Player.Id && r.ExpiresDay > _campaign.ElapsedDays).Select(r => r.Id).ToHashSet();
        // A load or hotseat change primes quietly; receipt still is not a signature.
        if (!_observerMode && !_dealNoticeObserver && _dealNoticeViewer == _campaign.Player.Id &&
            (incoming.Except(_dealNoticeIds).Any() || assistance.Except(_assistanceNoticeIds).Any())) PlayStrategicCue("deal_received");
        _assistanceNoticeIds = assistance;
        _dealNoticeViewer = _campaign.Player.Id; _dealNoticeObserver = _observerMode; _dealNoticeIds = incoming;
        if (IsInstanceValid(_dealInboxHudButton))
        {
            var visibilityChanged = _dealInboxHudButton.Visible != (count > 0);
            _dealInboxHudButton.Visible = count > 0;
            _dealInboxHudButton.Text = Loc.Format("Diplomatic inbox ({0})", count);
            _dealInboxHudButton.TooltipText = Ui.Tip(Tr("Incoming offers await your choice. Opening the inbox never signs or spends anything."));
            if (visibilityChanged) Callable.From(LayoutCampaignShell).CallDeferred();
        }
        if (IsInstanceValid(_diplomacyInboxButton))
            _diplomacyInboxButton.Text = count > 0 ? Loc.Format("Diplomatic inbox ({0})", count) : Tr("Diplomatic inbox");
        if (_dealInboxVisible && IsInstanceValid(_dealComposer)) RefreshDealInbox();
    }

    internal void OpenDealInbox()
    {
        if (_campaign?.Player is null || _observerMode) return;
        CloseDealComposer();
        CloseUltimatumComposer();
        _dealReturnFocus = GetViewport().GuiGetFocusOwner();
        _dealInboxVisible = true;
        _dealInboxKey = ""; _dealInboxMessage = ""; _dealInboxConfirm = -1;
        _dealComposer = FullLayer("DiplomaticInbox");
        _hudLayer.AddChild(_dealComposer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _dealComposer.AddChild(shade);
        var panel = TexturedPanel(_dealComposer); panel.Name = "DiplomaticInboxPanel";
        Ui.Place(panel, "modal.deal");
        var rows = PanelRows(panel, 20, 16, 12);
        var header = new HBoxContainer(); rows.AddChild(header);
        var title = Title("DIPLOMATIC INBOX", 24, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        header.AddChild(HandbookButton("deals"));
        header.AddChild(ActionButton("×", CloseDealComposer, 38, 32));
        rows.AddChild(WrappedUltimatumText("Diplomatic letters await your decision. Reading is not consent; review the live terms before approving any transfer.", 13));
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16); rows.AddChild(body);
        var listFrame = new PanelContainer { Name = "OfferListFrame", CustomMinimumSize = new(290, 0) };
        listFrame.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10)); body.AddChild(listFrame);
        _dealInboxScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill, FollowFocus = true };
        listFrame.AddChild(_dealInboxScroll);
        _dealInboxRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _dealInboxRows.AddThemeConstantOverride("separation", 6); _dealInboxScroll.AddChild(_dealInboxRows);
        var detailFrame = new PanelContainer { Name = "OfferDetailFrame", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        detailFrame.AddThemeStyleboxOverride("panel", WarInsetStyle(14, 12)); body.AddChild(detailFrame);
        var detailColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill };
        detailFrame.AddChild(detailColumn);
        var detailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, FollowFocus = true };
        detailColumn.AddChild(detailScroll);
        _dealInboxDetail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _dealInboxDetail.AddThemeConstantOverride("separation", 10); detailScroll.AddChild(_dealInboxDetail);
        // Keep consent actions visible even while a small window scrolls a long letter.
        _dealInboxActions = new HFlowContainer { Name = "DealInboxActions", Visible = false };
        _dealInboxActions.AddThemeConstantOverride("h_separation", 10);
        detailColumn.AddChild(_dealInboxActions);
        if (!_campaign.DealOffers.Any(o => o.Id == _dealInboxSelected && (o.FromId == _campaign.Player.Id || o.ToId == _campaign.Player.Id)))
            _dealInboxSelected = _campaign.DealOffers.Where(o => o.ToId == _campaign.Player.Id && o.Pending)
                .OrderBy(o => o.ExpiresDay).Select(o => o.Id).DefaultIfEmpty(-1).First();
        RefreshDealInbox(force: true);
    }

    private static string OfferStateKey(DealOffer paper) => paper.State switch
    {
        DealOfferState.Pending => "Awaiting reply", DealOfferState.Accepted => "Signed",
        DealOfferState.Declined => "Declined", DealOfferState.Withdrawn => "Withdrawn",
        DealOfferState.Expired => "Expired", DealOfferState.Invalidated => "No longer valid",
        _ => "Counterproposal sent"
    };

    private void RefreshDealInbox(bool force = false)
    {
        if (!_dealInboxVisible || !IsInstanceValid(_dealInboxRows)) return;
        var key = DealInboxKey();
        if (!force && (key == _dealInboxKey || InterfaceInteractionGuard.Busy(_dealComposer))) return;
        if (key != _dealInboxKey) { _dealInboxConfirm = -1; _dealInboxMessage = ""; }
        _dealInboxKey = key;
        var scroll = _dealInboxScroll.ScrollVertical;
        foreach (var child in _dealInboxRows.GetChildren()) { _dealInboxRows.RemoveChild(child); child.QueueFree(); }
        foreach (var child in _dealInboxDetail.GetChildren()) { _dealInboxDetail.RemoveChild(child); child.QueueFree(); }
        foreach (var child in _dealInboxActions.GetChildren()) { _dealInboxActions.RemoveChild(child); child.QueueFree(); }
        _dealInboxActions.Visible = false;
        var player = _campaign.Player;
        var papers = _campaign.DealOffers.Where(o => o.FromId == player.Id || o.ToId == player.Id)
            .OrderBy(o => !o.Pending).ThenBy(o => o.Pending ? o.ExpiresDay : -o.Id).ToArray();
        foreach (var paper in papers)
        {
            var counterpart = _campaign.NationById(paper.FromId == player.Id ? paper.ToId : paper.FromId);
            var id = paper.Id;
            var row = ActionButton(Loc.Format("{0} · {1}\n{2}", paper.ToId == player.Id ? Tr("Incoming") : Tr("Outgoing"),
                counterpart?.DisplayName ?? "?", Tr(OfferStateKey(paper))), () =>
            {
                _dealInboxSelected = id; _assistanceInboxSelected = -1; _dealInboxMessage = ""; _dealInboxConfirm = -1;
                RefreshDealInbox(force: true);
            }, 0, 54, id == _dealInboxSelected);
            row.Name = $"DiplomaticOffer_{id}"; row.TooltipText = Loc.Format("Offer #{0}", id);
            _dealInboxRows.AddChild(row);
        }
        var assistanceSelected = AddAssistanceInboxRows();
        if (papers.Length == 0 && _dealInboxRows.GetChildCount() == 0)
            _dealInboxRows.AddChild(WrappedUltimatumText("No offers yet. Propose a deal from a country's diplomacy page.", 14));
        _dealInboxScroll.SetDeferred("scroll_vertical", scroll);
        if (assistanceSelected) { _dealInboxKey = DealInboxKey(); return; }
        var selected = papers.FirstOrDefault(o => o.Id == _dealInboxSelected) ?? papers.FirstOrDefault();
        if (selected is null)
        {
            _dealInboxDetail.AddChild(WrappedUltimatumText("Countries may offer cash, treaties or whole regions. Your consent is never inferred from an opinion score.", 16));
            return;
        }
        _dealInboxSelected = selected.Id;
        // Selection is part of the cache key; avoid a redundant rebuild next frame.
        _dealInboxKey = DealInboxKey();
        var from = _campaign.NationById(selected.FromId); var to = _campaign.NationById(selected.ToId);
        _dealInboxDetail.AddChild(Title(Loc.Format("Offer #{0} · {1}", selected.Id, Tr(OfferStateKey(selected))), 20, Ui.Colour("text.accent")));
        var countries = new HBoxContainer(); countries.AddThemeConstantOverride("separation", 10);
        _dealInboxDetail.AddChild(countries);
        countries.AddChild(FlagBadge(from?.Tag ?? "", 26));
        var names = WrappedUltimatumText(Loc.Format("{0} → {1}", from?.DisplayName ?? "?", to?.DisplayName ?? "?"), 17);
        countries.AddChild(names); countries.AddChild(FlagBadge(to?.Tag ?? "", 26));
        var remaining = Math.Max(0, selected.ExpiresDay - _campaign.ElapsedDays);
        _dealInboxDetail.AddChild(WrappedUltimatumText(selected.Pending
            ? Loc.Format("Reply within {0} days · offered on campaign day {1}", remaining, selected.CreatedDay)
            : Loc.Format("Closed on campaign day {0}", selected.ClosedDay), 13));
        _dealInboxDetail.AddChild(WrappedUltimatumText("Offered money is not reserved. Funds, territorial control and treaties are checked again when signing.", 13));
        _dealInboxDetail.AddChild(new HSeparator());
        _dealInboxDetail.AddChild(WrappedUltimatumText(Ultimatum.DealSummary(_campaign, from, to, selected.Terms), 16));
        var preview = new DealTerritoryPreview(); _dealInboxDetail.AddChild(preview);
        var ours = selected.FromId == player.Id ? selected.From : selected.To;
        var theirs = selected.FromId == player.Id ? selected.To : selected.From;
        preview.Refresh(_campaign, ours.Regions, theirs.Regions, ours.ReleaseRegions, theirs.ReleaseRegions);
        _dealInboxDetail.AddChild(WrappedUltimatumText("Gold: you give. Green: you receive. Hatching: independence, not annexation. This is the current map, not a historical border snapshot.", 12));
        var legal = Ultimatum.ValidateNegotiatedDeal(_campaign, from, to, selected.Terms, out var refusal);
        if (legal && !DealNegotiations.SameGround(_campaign, selected))
        { legal = false; refusal = Tr("The selected regions changed shape. Send a new offer for the current territory."); }
        if (selected.Pending && !legal) _dealInboxDetail.AddChild(WrappedUltimatumText(refusal, 14));
        else if (!selected.Pending && selected.Reason.Length > 0) _dealInboxDetail.AddChild(WrappedUltimatumText(Tr(selected.Reason), 14));
        if (legal && selected.Pending)
        {
            var assessment = Ultimatum.AssessNegotiatedDeal(_campaign, from, to, selected.Terms);
            _dealInboxDetail.AddChild(WrappedUltimatumText(Loc.Format("Treasury after signing\nYou: {0}\nPartner: {1}",
                Fmt.Money(selected.FromId == player.Id ? assessment.FromTreasuryAfter : assessment.ToTreasuryAfter),
                Fmt.Money(selected.FromId == player.Id ? assessment.ToTreasuryAfter : assessment.FromTreasuryAfter)), 15));
        }
        var actions = _dealInboxActions;
        actions.Visible = selected.Pending;
        if (selected.Pending && selected.ToId == player.Id)
        {
            var accept = ActionButton(_dealInboxConfirm == selected.Id ? "CONFIRM SIGNATURE" : "Review and accept", () =>
            {
                if (_dealInboxConfirm != selected.Id || _dealInboxConfirmKey != DealInboxKey())
                {
                    _dealInboxConfirm = selected.Id; _dealInboxConfirmKey = DealInboxKey();
                    RefreshDealInbox(force: true); return;
                }
                ReplyToDeal(selected.Id, DealReply.Accept);
            }, 180, 36, true);
            accept.Name = "AcceptDiplomaticOffer"; accept.Disabled = !legal || remaining == 0;
            accept.TooltipText = Ui.Tip(!legal ? refusal : Tr("Two clicks confirm this exact offer. Opening or reading it never counts as acceptance."));
            actions.AddChild(accept);
            actions.AddChild(ActionButton("Decline", () => ReplyToDeal(selected.Id, DealReply.Decline), 120, 36));
            var counter = ActionButton("Counterproposal", () => CounterDeal(selected), 170, 36);
            counter.Name = "CounterDiplomaticOffer"; counter.Disabled = remaining == 0 || !_campaign.FeatureEnabled("deals");
            counter.TooltipText = Ui.Tip(Tr("Swap the sides into an editable draft. A refused counterproposal leaves the original offer available."));
            actions.AddChild(counter);
        }
        else if (selected.Pending && selected.FromId == player.Id)
            actions.AddChild(ActionButton("Withdraw offer", () => ReplyToDeal(selected.Id, DealReply.Withdraw), 170, 36));
        if (_dealInboxMessage.Length > 0) _dealInboxDetail.AddChild(WrappedUltimatumText(_dealInboxMessage, 14));
    }

    private void ReplyToDeal(int id, DealReply reply)
    {
        var command = new RespondDealCommand { IssuerId = _campaign.Player.Id, OfferId = id, Reply = reply };
        _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        ForceDiplomacyRefresh(); Refresh();
        _dealInboxMessage = _networkConnected && !_networkIsHost
            ? Tr("Answer sent to the host. Awaiting confirmation.") : command.Outcome.Note;
        _dealInboxConfirm = -1;
        RefreshDealInbox(force: true);
    }

    private void CounterDeal(DealOffer paper)
    {
        if (!paper.Pending || paper.ToId != _campaign.Player.Id || paper.ExpiresDay <= _campaign.ElapsedDays) return;
        var target = _campaign.NationById(paper.FromId);
        OpenDealComposer(target);
        _dealCounterParentId = paper.Id;
        void LoadHalf(Ultimatum.Side half, bool give)
        {
            var chosen = give ? _dealGiveRegions : _dealAskRegions;
            chosen.UnionWith(half.Regions ?? Array.Empty<int>());
            foreach (var choice in _dealComposer.FindChildren(give ? "DealGiveRegion_*" : "DealAskRegion_*", "CheckBox", true, false).OfType<CheckBox>())
                if (int.TryParse(choice.Name.ToString().Split('_').Last(), out var region)) choice.SetPressedNoSignal(chosen.Contains(region));
            (give ? _dealGivePact : _dealAskPact).SetPressedNoSignal(half.NonAggression);
            (give ? _dealGiveTrade : _dealAskTrade).SetPressedNoSignal(half.TradeAgreement);
            (give ? _dealGiveEmbargo : _dealAskEmbargo).SetPressedNoSignal(half.LiftEmbargo);
            (give ? _dealGiveRelease : _dealAskRelease).SetPressedNoSignal(half.ReleaseRegions);
            (give ? _dealGiveCash : _dealAskCash).Value = half.Cash;
        }
        LoadHalf(paper.To, give: true); LoadHalf(paper.From, give: false);
        RefreshDealComposer();
    }
}
