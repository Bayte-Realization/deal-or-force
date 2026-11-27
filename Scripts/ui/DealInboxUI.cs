using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Button _diplomacyInboxButton;
    private VBoxContainer _dealInboxRows, _dealInboxDetail;
    private ScrollContainer _dealInboxScroll;
    private HFlowContainer _dealInboxActions;
    private bool _dealInboxVisible;
    private int _dealInboxSelected = -1, _dealInboxConfirm = -1, _ultimatumInboxSelected = -1, _ultimatumInboxConfirm = -1;
    private string _dealInboxKey = "", _dealInboxConfirmKey = "", _dealInboxMessage = "";
    private int _dealNoticeViewer = -1;
    // Whose standing answers the strip last showed: an armed "sign if beneficial" is the press of
    // that player in that campaign, never a step another seat or another save inherits.
    private int _dealAnswerSeat = -1;
    private WeakReference<Campaign> _dealAnswerCampaign;
    private bool _dealNoticeObserver;
    private System.Collections.Generic.HashSet<int> _dealNoticeIds = new();
    private System.Collections.Generic.HashSet<int> _ultimatumNoticeIds = new();

    private string DealInboxKey()
    {
        var paper = _campaign.DealOffers.FirstOrDefault(o => o.Id == _dealInboxSelected);
        var demand = _campaign.UltimatumPapers.FirstOrDefault(candidate => candidate.Id == _ultimatumInboxSelected);
        return $"{Loc.Language}|{_campaign.Player?.Id ?? -1}|{_campaign.ElapsedDays}|{_campaign.MapRevision}|" +
            $"{_campaign.DealInboxRevision}|{_campaign.NationById(paper?.FromId ?? -1)?.Treasury:R}|" +
            $"{_campaign.NationById(paper?.ToId ?? -1)?.Treasury:R}|u:{_ultimatumInboxSelected}|{demand?.State}|{demand?.ExpiresDay}|{AssistanceInboxKey()}|{ArmsInboxKey()}|{LeaseInboxKey()}|{DealAnswerKey()}";
    }

    private int _dealInboxLastShellCount = -1;

    internal int CountIncomingDiplomaticInbox()
    {
        if (_observerMode || _campaign?.Player is null) return 0;
        var pid = _campaign.Player.Id;
        return DealNegotiations.Incoming(_campaign, pid)
            + ForeignAssistance.Incoming(_campaign, pid)
            + ArmsShipmentRequests.Incoming(_campaign, pid)
            + Ultimatum.Incoming(_campaign, pid)
            + MilitaryAccess.Incoming(_campaign, pid)
            + LeaseOffersIncoming()
            + DiplomaticActs.Incoming(_campaign, pid)
            + AllyRivalShift.Incoming(_campaign, pid)
            + AgreementSystem.OfferedTo(_campaign, _campaign.Player).Count;
    }

    private readonly System.Collections.Generic.HashSet<int> _dealNoticeScratch = new();
    private readonly System.Collections.Generic.HashSet<int> _ultimatumNoticeScratch = new();
    private readonly System.Collections.Generic.HashSet<int> _assistanceNoticeScratch = new();
    private readonly System.Collections.Generic.HashSet<int> _armsNoticeScratch = new();

    private static bool SyncNoticeSet(System.Collections.Generic.HashSet<int> previous, System.Collections.Generic.HashSet<int> next)
    {
        var added = false;
        foreach (var id in next)
            if (!previous.Contains(id)) { added = true; break; }
        previous.Clear();
        foreach (var id in next) previous.Add(id);
        return added;
    }

    private void RefreshNegotiationNotifications(bool allowShellRefresh = true)
    {
        var count = CountIncomingDiplomaticInbox();
        var playerId = _campaign?.Player?.Id ?? -1;
        var day = _campaign.ElapsedDays;
        _dealNoticeScratch.Clear();
        _ultimatumNoticeScratch.Clear();
        _assistanceNoticeScratch.Clear();
        _armsNoticeScratch.Clear();
        if (!_observerMode && playerId > 0)
        {
            var offers = _campaign.DealOffers;
            for (var i = 0; i < offers.Count; i++)
            {
                var o = offers[i];
                if (o.Pending && o.ToId == playerId && o.ExpiresDay > day) _dealNoticeScratch.Add(o.Id);
            }
            var papers = _campaign.UltimatumPapers;
            for (var i = 0; i < papers.Count; i++)
            {
                var o = papers[i];
                if (o.Pending && o.ToId == playerId && o.ExpiresDay > day) _ultimatumNoticeScratch.Add(o.Id);
            }
            var assistanceReqs = _campaign.AssistanceRequests;
            for (var i = 0; i < assistanceReqs.Count; i++)
            {
                var r = assistanceReqs[i];
                if (r.State == ForeignAssistanceState.Pending && r.DonorId == playerId && r.ExpiresDay > day) _assistanceNoticeScratch.Add(r.Id);
            }
            var armsReqs = _campaign.ArmsShipmentRequests;
            for (var i = 0; i < armsReqs.Count; i++)
            {
                var r = armsReqs[i];
                if (r.State == ArmsShipmentState.Pending && r.DonorId == playerId && r.ExpiresDay > day) _armsNoticeScratch.Add(r.Id);
            }
        }
        var shouldNotify = !_observerMode && !_dealNoticeObserver && _dealNoticeViewer == playerId;
        var hasNewDeal = SyncNoticeSet(_dealNoticeIds, _dealNoticeScratch);
        var hasNewUltimatum = SyncNoticeSet(_ultimatumNoticeIds, _ultimatumNoticeScratch);
        var hasNewAssistance = SyncNoticeSet(_assistanceNoticeIds, _assistanceNoticeScratch);
        var hasNewArms = SyncNoticeSet(_armsNoticeIds, _armsNoticeScratch);
        // A load or hotseat change primes quietly; receipt still is not a signature.
        if (shouldNotify && (hasNewDeal || hasNewUltimatum || hasNewAssistance || hasNewArms))
            PlayStrategicCue("deal_received");
        _dealNoticeViewer = playerId;
        _dealNoticeObserver = _observerMode;
        // The one way to the inbox on the campaign screen while offers wait: the chip on the date
        // plate, seen without opening anything. The report's own inbox button said the same and
        // went in step 4 of the interface plan. The chip keeps the plate's width (DatePlateUI), so
        // its coming and going moves nothing else.
        RefreshDatePlateDeals(count);
        if (IsNewShellActive && _dealInboxLastShellCount != count)
        {
            _dealInboxLastShellCount = count;
            if (allowShellRefresh) RefreshNewShell();
            else _shell?.MarkInboxAndAlertsDirty();
        }
        if (IsInstanceValid(_diplomacyInboxButton))
        {
            var label = count > 0 ? Loc.Format("Diplomatic inbox ({0})", count) : Tr("Diplomatic inbox");
            if (_diplomacyInboxButton.Text != label) _diplomacyInboxButton.Text = label;
        }
        if (_dealInboxVisible && IsInstanceValid(_dealComposer)) RefreshDealInbox();
    }

    internal void OpenDealInbox()
    {
        if (_campaign?.Player is null || _observerMode) return;
        CloseDealComposer();
        CloseUltimatumComposer();
        _dealReturnFocus = GetViewport().GuiGetFocusOwner();
        _dealInboxVisible = true;
        _dealInboxKey = ""; _dealInboxMessage = ""; _dealInboxConfirm = -1; _dealAnswerConfirmKind = "";
        _dealComposer = FullLayer("DiplomaticInbox");
        _hudLayer.AddChild(_dealComposer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _dealComposer.AddChild(shade);
        var panel = TexturedPanel(_dealComposer);
        ChromePlateArt.AttachSurfaceTexture(panel, "laid_paper");
        panel.Name = "DiplomaticInboxPanel";
        Ui.Place(panel, "modal.deal");
        if (IsNewShellActive)
        {
            OnNewShellWorkspaceRaised(_dealComposer);
            FitNewShellWorkspaceLayer(_dealComposer);
        }
        var rows = PanelRows(panel, 20, 16, 12);
        ScreenHeader(rows, "DIPLOMATIC INBOX",
            "Diplomatic letters await your decision. Reading is not consent; review the live terms before approving any transfer.",
            false, HandbookButton("deals"), ActionButton("×", CloseDealComposer, 38, 32));
        BuildDealAnswerStrip(rows);
        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        body.AddThemeConstantOverride("separation", 16); rows.AddChild(body);
        var listFrame = new PanelContainer { Name = "OfferListFrame", CustomMinimumSize = new(290, 0), SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        listFrame.SetMeta("atlas_section_ground", "surface.inset");
        listFrame.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(listFrame, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        body.AddChild(listFrame);
        _dealInboxScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true, FollowFocus = true };
        listFrame.AddChild(_dealInboxScroll);
        _dealInboxRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _dealInboxRows.AddThemeConstantOverride("separation", 6); _dealInboxScroll.AddChild(_dealInboxRows);
        var detailFrame = new PanelContainer { Name = "OfferDetailFrame", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        detailFrame.SetMeta("atlas_section_ground", "surface.inset");
        detailFrame.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(detailFrame, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        body.AddChild(detailFrame);
        var detailColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        detailFrame.AddChild(detailColumn);
        var detailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true, FollowFocus = true };
        detailColumn.AddChild(detailScroll);
        _dealInboxDetail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _dealInboxDetail.AddThemeConstantOverride("separation", 10); detailScroll.AddChild(_dealInboxDetail);
        // Keep consent actions visible even while a small window scrolls a long letter.
        _dealInboxActions = new HFlowContainer { Name = "DealInboxActions", Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkEnd, ClipContents = true };
        _dealInboxActions.AddThemeConstantOverride("h_separation", 10);
        _dealInboxActions.AddThemeConstantOverride("v_separation", 6);
        detailColumn.AddChild(_dealInboxActions);
        var player = _campaign?.Player;
        if (player is not null)
        {
            if (!_campaign.UltimatumPapers.Any(paper => paper.Id == _ultimatumInboxSelected && paper.ToId == player.Id && paper.Pending))
                _ultimatumInboxSelected = -1;
            if (!_campaign.DealOffers.Any(o => o.Id == _dealInboxSelected && (o.FromId == player.Id || o.ToId == player.Id)))
                _dealInboxSelected = _campaign.DealOffers.Where(o => o.ToId == player.Id && o.Pending)
                    .OrderBy(o => o.ExpiresDay).Select(o => o.Id).DefaultIfEmpty(-1).First();
            if (_ultimatumInboxSelected < 0 && _dealInboxSelected < 0 && _assistanceInboxSelected < 0)
                _ultimatumInboxSelected = _campaign.UltimatumPapers.Where(paper => paper.Pending && paper.ToId == player.Id && paper.ExpiresDay > _campaign.ElapsedDays)
                    .OrderBy(paper => paper.ExpiresDay).Select(paper => paper.Id).DefaultIfEmpty(-1).First();
            if (_ultimatumInboxSelected >= 0) _dealInboxSelected = -1;
        }
        else
        {
            _ultimatumInboxSelected = -1;
            _dealInboxSelected = -1;
        }
        RefreshDealInbox(force: true);
    }

    private static string OfferStateKey(DealOffer paper) => paper.State switch
    {
        DealOfferState.Pending => "Awaiting reply", DealOfferState.Accepted => "Signed",
        DealOfferState.Declined => "Declined", DealOfferState.Withdrawn => "Withdrawn",
        DealOfferState.Expired => "Expired", DealOfferState.Invalidated => "No longer valid",
        _ => "Counterproposal sent"
    };

    /// <summary>
    /// The answer to a press. A rebuild for a changed key clears the message with every armed
    /// confirmation, and the press itself changed the key, so the message is set after that rebuild.
    /// </summary>
    private void ShowDealInboxOutcome(string message)
    {
        if (_dealInboxVisible && IsInstanceValid(_dealInboxRows) && DealInboxKey() != _dealInboxKey)
            RefreshDealInbox(force: true);
        _dealInboxMessage = message;
        RefreshDealInbox(force: true);
    }

    private void RefreshDealInbox(bool force = false)
    {
        if (!_dealInboxVisible || !IsInstanceValid(_dealInboxRows)) return;
        var player = _campaign?.Player;
        var playerId = player?.Id ?? -1;
        // Before the key, which holds the armed kind: the other player's strip starts unarmed.
        if (_dealAnswerSeat != playerId || _dealAnswerCampaign is null ||
            !_dealAnswerCampaign.TryGetTarget(out var shown) || !ReferenceEquals(shown, _campaign))
        {
            _dealAnswerConfirmKind = "";
            _dealAnswerSeat = playerId;
            _dealAnswerCampaign = new WeakReference<Campaign>(_campaign);
        }
        var key = DealInboxKey();
        if (!force && (key == _dealInboxKey || InterfaceInteractionGuard.Busy(_dealComposer))) return;
        if (key != _dealInboxKey) { _dealInboxConfirm = -1; _ultimatumInboxConfirm = -1; _dealInboxMessage = ""; }
        _dealInboxKey = key;
        var scroll = _dealInboxScroll.ScrollVertical;
        foreach (var child in _dealInboxRows.GetChildren()) { _dealInboxRows.RemoveChild(child); child.QueueFree(); }
        foreach (var child in _dealInboxDetail.GetChildren()) { _dealInboxDetail.RemoveChild(child); child.QueueFree(); }
        foreach (var child in _dealInboxActions.GetChildren()) { _dealInboxActions.RemoveChild(child); child.QueueFree(); }
        _dealInboxActions.Visible = false;
        if (player is null)
        {
            _dealInboxRows.AddChild(WrappedUltimatumText(Tr("obs.ui.inbox.no_player"), 14));
            _dealInboxDetail.AddChild(WrappedUltimatumText(Tr("obs.ui.inbox.no_player"), 14));
            return;
        }
        RefreshDealAnswerStrip();
        AddMilitaryAccessInboxRows();
        AddAllyRivalInboxRows();
        AddDiplomaticActInboxRows();
        AddAgreementInboxRows();
        var papers = _campaign.DealOffers.Where(o => o.FromId == player.Id || o.ToId == player.Id)
            .OrderBy(o => !o.Pending).ThenBy(o => o.Pending ? o.ExpiresDay : -o.Id).ToArray();
        foreach (var paper in papers)
        {
            var counterpart = _campaign.NationById(paper.FromId == player.Id ? paper.ToId : paper.FromId);
            var id = paper.Id;
            var worth = DealWorthLine(paper);
            var state = Tr(OfferStateKey(paper));
            if (paper.ByRule) state = Loc.Format("{0} by rule", state);
            // A renewal is the same agreement asked again at its term's end, not a new question.
            if (paper.Trade is { Renewal: true }) state = Loc.Format("Renewal · {0}", state);
            if (worth.Length > 0) state += "\n" + worth;
            var row = ActionButton(Loc.Format("{0} · {1}\n{2}", paper.ToId == player.Id ? Tr("Incoming") : Tr("Outgoing"),
                counterpart?.DisplayName ?? "?", state), () =>
            {
                _dealInboxSelected = id; _ultimatumInboxSelected = -1; _assistanceInboxSelected = -1; ClearArmsInboxSelection();
                _dealInboxMessage = ""; _dealInboxConfirm = -1;
                RefreshDealInbox(force: true);
            }, 0, worth.Length > 0 ? 76 : 54, id == _dealInboxSelected && _ultimatumInboxSelected < 0);
            row.Name = $"DiplomaticOffer_{id}"; row.TooltipText = Loc.Format("Offer #{0}", id);
            _dealInboxRows.AddChild(row);
        }
        var ultimatumPapers = _campaign.UltimatumPapers.Where(paper => paper.FromId == player.Id || paper.ToId == player.Id)
            .OrderBy(paper => !paper.Pending).ThenBy(paper => paper.Pending ? paper.ExpiresDay : -paper.Id).ToArray();
        foreach (var demand in ultimatumPapers)
        {
            var id = demand.Id;
            var demander = _campaign.NationById(demand.FromId);
            var row = ActionButton(Loc.Format("Ultimatum · {0}\n{1}", demander?.DisplayName ?? "?",
                Tr(demand.Pending ? "Awaiting reply" : demand.State == UltimatumPaperState.Yielded ? "Yielded" : "Refused")), () =>
            {
                _ultimatumInboxSelected = id; _dealInboxSelected = -1; _assistanceInboxSelected = -1; ClearArmsInboxSelection();
                _dealInboxMessage = ""; _ultimatumInboxConfirm = -1;
                RefreshDealInbox(force: true);
            }, 0, 54, id == _ultimatumInboxSelected);
            row.Name = $"Ultimatum_{id}";
            row.TooltipText = Loc.Format("Ultimatum #{0}", id);
            _dealInboxRows.AddChild(row);
        }
        var showUltimatum = ultimatumPapers.Any(paper => paper.Id == _ultimatumInboxSelected);
        if (!showUltimatum) _ultimatumInboxSelected = -1;
        playerId = player.Id;
        var armsExplicit = _campaign.ArmsShipmentRequests.Any(r => r.Id == _armsInboxSelected && (r.RequesterId == playerId || r.DonorId == playerId));
        var assistanceSelected = AddAssistanceInboxRows(showDetail: !showUltimatum && !armsExplicit);
        var armsSelected = AddArmsInboxRows(showDetail: !showUltimatum && !assistanceSelected);
        AddLeaseInboxRows();
        if (papers.Length == 0 && _dealInboxRows.GetChildCount() == 0)
            _dealInboxRows.AddChild(WrappedUltimatumText("No offers yet. Propose a deal from a country's diplomacy page.", 14));
        _dealInboxScroll.SetDeferred("scroll_vertical", scroll);
        if (showUltimatum)
        {
            ShowUltimatumPaper(ultimatumPapers.First(paper => paper.Id == _ultimatumInboxSelected));
            _dealInboxKey = DealInboxKey();
            return;
        }
        if (assistanceSelected) { _dealInboxKey = DealInboxKey(); return; }
        if (armsSelected) { _dealInboxKey = DealInboxKey(); return; }
        var selected = papers.FirstOrDefault(o => o.Id == _dealInboxSelected) ?? papers.FirstOrDefault();
        if (selected is null)
        {
            _dealInboxDetail.AddChild(WrappedUltimatumText("Offers of cash, treaties and regions from other governments.", 16));
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
        _dealInboxDetail.AddChild(WrappedUltimatumText("Gold: you give. Green: you receive. Hatched: becomes independent.", 12));
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
            AddDealWorth(selected, from, to);
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
            }, 180, DealAnswerTouch, true);
            accept.Name = "AcceptDiplomaticOffer"; accept.Disabled = !legal || remaining == 0;
            accept.TooltipText = Ui.Tip(!legal ? refusal : Tr("Press twice to sign."));
            actions.AddChild(accept);
            actions.AddChild(ActionButton("Decline", () => ReplyToDeal(selected.Id, DealReply.Decline), 120, DealAnswerTouch));
            var counter = ActionButton("Counterproposal", () => CounterDeal(selected), 170, DealAnswerTouch);
            counter.Name = "CounterDiplomaticOffer"; counter.Disabled = remaining == 0 || !_campaign.FeatureEnabled("deals");
            counter.TooltipText = Ui.Tip(Tr("Swap the sides into an editable draft. A refused counterproposal leaves the original offer available."));
            actions.AddChild(counter);
        }
        else if (selected.Pending && selected.FromId == player.Id)
            actions.AddChild(ActionButton("Withdraw offer", () => ReplyToDeal(selected.Id, DealReply.Withdraw), 170, DealAnswerTouch));
        if (_dealInboxMessage.Length > 0) _dealInboxDetail.AddChild(WrappedUltimatumText(_dealInboxMessage, 14));
    }

    private void ShowUltimatumPaper(UltimatumPaper demand)
    {
        var from = _campaign.NationById(demand.FromId);
        var to = _campaign.NationById(demand.ToId);
        var state = demand.State switch
        {
            UltimatumPaperState.Pending => "Awaiting reply",
            UltimatumPaperState.Yielded => "Yielded",
            UltimatumPaperState.Refused => "Refused",
            UltimatumPaperState.Expired => "Expired",
            _ => "No longer valid"
        };
        _dealInboxDetail.AddChild(Title(Loc.Format("Ultimatum #{0} · {1}", demand.Id, Tr(state)), 20, Ui.Colour("text.accent")));
        var countries = new HBoxContainer();
        countries.AddThemeConstantOverride("separation", 10);
        _dealInboxDetail.AddChild(countries);
        countries.AddChild(FlagBadge(from?.Tag ?? "", 26));
        var names = WrappedUltimatumText(Loc.Format("{0} → {1}", from?.DisplayName ?? "?", to?.DisplayName ?? "?"), 17);
        countries.AddChild(names);
        countries.AddChild(FlagBadge(to?.Tag ?? "", 26));
        var remaining = Math.Max(0, demand.ExpiresDay - _campaign.ElapsedDays);
        _dealInboxDetail.AddChild(WrappedUltimatumText(demand.Pending
            ? Loc.Format("Reply within {0} days · offered on campaign day {1}", remaining, demand.CreatedDay)
            : Loc.Format("Closed on campaign day {0}", demand.ClosedDay), 13));
        _dealInboxDetail.AddChild(WrappedUltimatumText("Nothing has been handed over. Yielding gives them exactly what this paper asks and starts a truce. Refusing keeps everything and gives them a reason for war.", 13));
        _dealInboxDetail.AddChild(new HSeparator());
        _dealInboxDetail.AddChild(WrappedUltimatumText(Ultimatum.Summary(_campaign, demand.Terms), 16));
        var preview = new DealTerritoryPreview();
        _dealInboxDetail.AddChild(preview);
        preview.Refresh(_campaign, System.Array.Empty<int>(), demand.Regions, false, false);
        _dealInboxDetail.AddChild(WrappedUltimatumText("Green: they are asking you to give this. Gold is not used on an ultimatum.", 12));
        var refusal = "";
        var legal = demand.Pending && from is not null && to is not null &&
            Ultimatum.ValidateTerms(_campaign, from, to, demand.Terms, out refusal);
        if (demand.Pending && !legal) _dealInboxDetail.AddChild(WrappedUltimatumText(refusal, 14));
        else if (!demand.Pending && demand.Reason.Length > 0) _dealInboxDetail.AddChild(WrappedUltimatumText(Tr(demand.Reason), 14));
        var actingPlayer = _observerMode ? null : _campaign?.Player;
        _dealInboxActions.Visible = actingPlayer is not null && demand.Pending && demand.ToId == actingPlayer.Id;
        if (!_dealInboxActions.Visible)
        {
            if (_dealInboxMessage.Length > 0) _dealInboxDetail.AddChild(WrappedUltimatumText(_dealInboxMessage, 14));
            return;
        }
        var yield = ActionButton(_ultimatumInboxConfirm == demand.Id ? "CONFIRM YIELD" : "Yield", () =>
        {
            if (_ultimatumInboxConfirm != demand.Id || _dealInboxConfirmKey != DealInboxKey())
            {
                _ultimatumInboxConfirm = demand.Id;
                _dealInboxConfirmKey = DealInboxKey();
                RefreshDealInbox(force: true);
                return;
            }
            ReplyToUltimatum(demand.Id, Ultimatum.Answer.Yield);
        }, 180, 36, true);
        yield.Name = "YieldUltimatum";
        yield.Disabled = !legal || remaining == 0;
        yield.TooltipText = Ui.Tip(!legal ? refusal : Tr("Two clicks hand over exactly what this paper asks. Opening or reading it never counts as yielding."));
        _dealInboxActions.AddChild(yield);
        var refuse = ActionButton("Refuse", () => ReplyToUltimatum(demand.Id, Ultimatum.Answer.Refuse), 120, 36);
        refuse.Name = "RefuseUltimatum";
        refuse.TooltipText = Ui.Tip(Tr("The land stays. They remember the refusal and may go to war after the short truce."));
        _dealInboxActions.AddChild(refuse);
        if (_dealInboxMessage.Length > 0) _dealInboxDetail.AddChild(WrappedUltimatumText(_dealInboxMessage, 14));
    }

    private void ReplyToUltimatum(int id, Ultimatum.Answer reply)
    {
        if (_observerMode || _campaign?.Player is not { } player) return;
        var command = new AnswerUltimatumCommand { IssuerId = player.Id, PaperId = id, Reply = reply };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        ForceDiplomacyRefresh();
        Refresh();
        _dealInboxMessage = _networkConnected && !_networkIsHost
            ? Tr("Answer sent to the host. Awaiting confirmation.") : command.Outcome.Note;
        _ultimatumInboxConfirm = -1;
        RefreshDealInbox(force: true);
    }

    private void ReplyToDeal(int id, DealReply reply)
    {
        if (_observerMode || _campaign?.Player is not { } player) return;
        var command = new RespondDealCommand { IssuerId = player.Id, OfferId = id, Reply = reply };
        _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        ForceDiplomacyRefresh(); Refresh();
        _dealInboxMessage = _networkConnected && !_networkIsHost
            ? Tr("Answer sent to the host. Awaiting confirmation.") : command.Outcome.Note;
        _dealInboxConfirm = -1;
        RefreshDealInbox(force: true);
    }

    private void CounterDeal(DealOffer paper)
    {
        if (_observerMode || _campaign?.Player is not { } player) return;
        if (!paper.Pending || paper.ToId != player.Id || paper.ExpiresDay <= _campaign.ElapsedDays) return;
        var target = _campaign.NationById(paper.FromId);
        // The trade terms are the pair's, not a half's: they open in the shared strip as written.
        OpenDealComposer(target, Ultimatum.TradeOf(paper.Terms));
        _dealCounterParentId = paper.Id;
        void LoadHalf(Ultimatum.Side half, bool give)
        {
            var chosen = give ? _dealGiveRegions : _dealAskRegions;
            chosen.UnionWith(half.Regions ?? Array.Empty<int>());
            foreach (var choice in _dealComposer.FindChildren(give ? "DealGiveRegion_*" : "DealAskRegion_*", "CheckBox", true, false).OfType<CheckBox>())
                if (int.TryParse(choice.Name.ToString().Split('_').Last(), out var region)) choice.SetPressedNoSignal(chosen.Contains(region));
            (give ? _dealGivePact : _dealAskPact).SetPressedNoSignal(half.NonAggression);
            (give ? _dealGiveEmbargo : _dealAskEmbargo).SetPressedNoSignal(half.LiftEmbargo);
            (give ? _dealGiveRelease : _dealAskRelease).SetPressedNoSignal(half.ReleaseRegions);
            (give ? _dealGiveCash : _dealAskCash).Value = half.Cash;
        }
        LoadHalf(paper.To, give: true); LoadHalf(paper.From, give: false);
        RefreshDealComposer();
    }
}
