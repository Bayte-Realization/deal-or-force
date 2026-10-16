using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The deal composer: the ultimatum's table turned level. The ultimatum composer asks
/// "what do we take"; this one builds both halves of the table at once - what we put on
/// it, and what we ask the other side to put - and shows the answer they are expected to
    /// give before anything is sent, using the simulation's read-only assessment.
    /// Sending still revalidates ownership, funds and consent against current state.
/// </summary>
public partial class DealOrForceGame
{
    private Control _dealComposer;
    private readonly HashSet<int> _dealGiveRegions = new();
    private readonly HashSet<int> _dealAskRegions = new();
    private SpinBox _dealGiveCash, _dealAskCash;
    private CheckBox _dealGivePact, _dealGiveTrade, _dealGiveEmbargo;
    private CheckBox _dealAskPact, _dealAskTrade, _dealAskEmbargo;
    private CheckBox _dealGiveRelease, _dealAskRelease;
    private Label _dealAssessment, _dealTerms, _dealTreasuries, _dealValuation;
    private Control _dealReturnFocus;
    private Button _dealSend, _dealClose, _dealBalance, _dealAskMore;
    private WarBar _dealMeter;
    private DealTerritoryPreview _dealTerritoryPreview;
    private Label _dealMapHint;
    private float _dealBalanceCash, _dealAskMoreCash;
    private int _dealTarget = -1;
    private bool _dealConfirm, _dealCompleted;
    private int _dealReviewDay = -1;
    private int _dealCounterParentId;

    private void CloseDealComposer()
    {
        if (IsInstanceValid(_dealComposer))
        { _dealComposer.GetParent()?.RemoveChild(_dealComposer); _dealComposer.QueueFree(); }
        _dealComposer = null;
        _dealInboxVisible = false;
        _dealCounterParentId = 0;
        _dealGiveRegions.Clear();
        _dealAskRegions.Clear();
        _dealConfirm = false;
        _dealCompleted = false;
        if (IsInstanceValid(_dealReturnFocus) && _dealReturnFocus.IsVisibleInTree()) _dealReturnFocus.GrabFocus();
        _dealReturnFocus = null;
    }

    private Ultimatum.Side DealGiveSide() => new()
    {
        Regions = _dealGiveRegions.OrderBy(id => id).ToArray(),
        ReleaseRegions = _dealGiveRelease.ButtonPressed,
        Cash = (float)_dealGiveCash.Value,
        LiftEmbargo = _dealGiveEmbargo.ButtonPressed,
        NonAggression = _dealGivePact.ButtonPressed,
        TradeAgreement = _dealGiveTrade.ButtonPressed
    };

    private Ultimatum.Side DealAskSide() => new()
    {
        Regions = _dealAskRegions.OrderBy(id => id).ToArray(),
        ReleaseRegions = _dealAskRelease.ButtonPressed,
        Cash = (float)_dealAskCash.Value,
        LiftEmbargo = _dealAskEmbargo.ButtonPressed,
        NonAggression = _dealAskPact.ButtonPressed,
        TradeAgreement = _dealAskTrade.ButtonPressed
    };

    private Ultimatum.Deal ComposedDeal() => new(DealGiveSide(), DealAskSide());

    internal void OpenDealComposer(Nation target)
    {
        if (_campaign?.Player is null || target is null || target.Id == _campaign.Player.Id) return;
        CloseDealComposer();
        CloseUltimatumComposer();
        PlayStrategicCue("diplomacy_page");
        _dealReturnFocus = GetViewport().GuiGetFocusOwner();
        _dealTarget = target.Id;
        _dealComposer = FullLayer("DealComposer");
        _hudLayer.AddChild(_dealComposer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _dealComposer.AddChild(shade);
        var panel = TexturedPanel(_dealComposer);
        panel.Name = "DealPanel";
        Ui.Place(panel, "modal.deal");
        var rows = PanelRows(panel, 20, 16, 12);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        rows.AddChild(header);
        header.AddChild(FlagBadge(target.Tag, 30, target.DisplayName));
        var heading = Title(Loc.Format("Deal with {0}", target.DisplayName), 24, Ui.Colour("text.accent"));
        heading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        header.AddChild(heading);
        header.AddChild(HandbookButton("deals"));
        var close = ActionButton("×", CloseDealComposer, 38, 32);
        close.Name = "CloseDealComposer"; close.TooltipText = Ui.Tip(Tr("Close"));
        header.AddChild(close);
        rows.AddChild(WrappedUltimatumText("A voluntary exchange. Review what each side gives, then confirm once. A refusal transfers nothing.", 13));

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 20);
        rows.AddChild(body);
        var player = _campaign.Player;
        var relation = CampaignDiplomacy.Peek(_campaign, player.Id, target.Id);

        BuildDealHalf(body, Tr("You give"), player, ours: true, relation,
            _dealGiveRegions, out _dealGiveCash, out _dealGivePact, out _dealGiveTrade, out _dealGiveEmbargo, out _dealGiveRelease);
        BuildDealHalf(body, Tr("You ask"), target, ours: false, relation,
            _dealAskRegions, out _dealAskCash, out _dealAskPact, out _dealAskTrade, out _dealAskEmbargo, out _dealAskRelease);

        var reviewFrame = new PanelContainer { Name = "DealReviewPanel", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill };
        reviewFrame.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10)); body.AddChild(reviewFrame);
        var reviewScroll = new ScrollContainer { Name = "DealReviewScroll", CustomMinimumSize = new(220, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        reviewFrame.AddChild(reviewScroll);
        var review = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        review.AddThemeConstantOverride("separation", 10); reviewScroll.AddChild(review);
        review.AddChild(Title("REVIEW BEFORE SIGNING", 17, Ui.Colour("text.accent")));
        _dealAssessment = WrappedUltimatumText("", 20);
        _dealAssessment.Name = "DealAssessment";
        review.AddChild(_dealAssessment);
        // How far the table leans for them: right of the tick they sign, left they refuse.
        _dealMeter = new WarBar { Name = "DealMeter", CustomMinimumSize = new Vector2(0, 12),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, Track = Ui.Colour("war.track"),
            MarkerColour = Ui.Colour("war.front_line"), CentreTick = true,
            TooltipText = Ui.Tip(Tr("Right of the middle they sign; left of it they refuse. The further, the clearer their answer.")) };
        review.AddChild(_dealMeter);
        // The two moves of haggling, done for the player: pay what closes the gap, or ask
        // for what the other side would still give.
        _dealBalance = ActionButton("", () =>
        {
            if (_dealBalanceCash > 0f) _dealGiveCash.Value = Math.Min(_dealGiveCash.MaxValue, _dealGiveCash.Value + _dealBalanceCash);
        }, 0, 32);
        _dealBalance.Name = "DealBalanceWithCash";
        _dealBalance.TooltipText = Ui.Tip(Tr("Adds to your payment exactly what they would still need to sign."));
        review.AddChild(_dealBalance);
        _dealAskMore = ActionButton("", () =>
        {
            if (_dealAskMoreCash > 0f) _dealAskCash.Value = Math.Min(_dealAskCash.MaxValue, _dealAskCash.Value + _dealAskMoreCash);
        }, 0, 32);
        _dealAskMore.Name = "DealAskMoreCash";
        _dealAskMore.TooltipText = Ui.Tip(Tr("Asks them for the most they would still pay and sign."));
        review.AddChild(_dealAskMore);
        _dealTreasuries = WrappedUltimatumText("", 13); _dealTreasuries.Name = "DealTreasuryAfter";
        review.AddChild(_dealTreasuries);
        review.AddChild(new HSeparator());
        _dealValuation = WrappedUltimatumText("", 13); _dealValuation.Name = "DealValuationBreakdown";
        _dealValuation.TooltipText = Ui.Tip(Tr("Diplomatic value is how the other government weighs the deal, in points: not money, and not a forecast of profit."));
        review.AddChild(_dealValuation);
        review.AddChild(new HSeparator());
        review.AddChild(Title("TERRITORIAL PACKAGE", 14, Ui.Colour("text.accent")));
        _dealTerritoryPreview = new DealTerritoryPreview(); review.AddChild(_dealTerritoryPreview);
        _dealMapHint = WrappedUltimatumText("", 12); review.AddChild(_dealMapHint);
        review.AddChild(new HSeparator());
        _dealTerms = WrappedUltimatumText("", 13);
        _dealTerms.Name = "DealTerms";
        review.AddChild(_dealTerms);
        review.AddChild(WrappedUltimatumText(Loc.Format("Signed: {0} days of protected peace on both sides of the table.",
            Math.Clamp(_campaign.Defines.DealSettlementTruceDays, 0, 3650)), 13));
        rows.AddChild(new HSeparator());
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        rows.AddChild(actions);
        _dealClose = ActionButton("Cancel", CloseDealComposer, 130, 36);
        actions.AddChild(_dealClose);
        _dealSend = ActionButton("Review deal", SendComposedDeal, 240, 36, true);
        _dealSend.Name = "SendComposedDeal";
        actions.AddChild(_dealSend);
        RefreshDealComposer();
        _dealGiveCash.GetLineEdit().GrabFocus();
    }

    /// <summary>One half of the table: a cheque, the promises that side can honestly make,
    /// and the land it could actually part with. Which promises are greyed out is the
    /// relation's own state - a pact that exists buys nothing, and an embargo one side
    /// never imposed cannot be lifted by it.</summary>
    private void BuildDealHalf(HBoxContainer body, string caption, Nation owner, bool ours,
        DiplomaticRelation relation, HashSet<int> chosen,
        out SpinBox cash, out CheckBox pact, out CheckBox trade, out CheckBox embargo, out CheckBox release)
    {
        var frame = new PanelContainer { Name = ours ? "DealGivePanel" : "DealAskPanel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        frame.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10)); body.AddChild(frame);
        var half = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        half.AddThemeConstantOverride("separation", 7); frame.AddChild(half);
        half.AddChild(Title(caption, 17, Ui.Colour("text.accent")));
        var party = new HBoxContainer(); party.AddThemeConstantOverride("separation", 9); half.AddChild(party);
        party.AddChild(FlagBadge(owner.Tag, 24, owner.DisplayName));
        var country = Title(owner.DisplayName, 15, Ui.Colour("text.body"));
        country.SizeFlagsHorizontal = SizeFlags.ExpandFill; country.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        party.AddChild(country);
        half.AddChild(new HSeparator());
        half.AddChild(WrappedUltimatumText("One-time payment · billions of dollars", 13));
        cash = new SpinBox { Name = ours ? "DealGiveCash" : "DealAskCash", MinValue = 0, Step = .01,
            MaxValue = Math.Max(0, owner.Treasury) * (ours ? 1f : Math.Clamp(_campaign.Defines.DealMaxTreasuryShare, 0f, 1f)),
            TooltipText = Ui.Tip(Tr("0.01 means $10 million. Payments use existing treasury, not new debt. Cash moves only after agreement.")),
            CustomMinimumSize = new(0, 36) };
        half.AddChild(cash);
        half.AddChild(Title(Loc.Format("Maximum: {0}", Fmt.Money((float)cash.MaxValue)), 12, Ui.Colour("text.muted")));
        half.AddChild(Title("TREATIES", 13, Ui.Colour("text.accent")));
        pact = new CheckBox { Name = ours ? "DealGivePact" : "DealAskPact", Text = Tr("Non-aggression pact"),
            Disabled = relation?.NonAggressionPact == true,
            TooltipText = Ui.Tip(Tr("This is a mutual pact. Select it on one side only; an existing treaty cannot be sold again.")) };
        half.AddChild(pact);
        trade = new CheckBox { Name = ours ? "DealGiveTrade" : "DealAskTrade", Text = Tr("Trade agreement"),
            Disabled = relation?.TradeAgreement == true,
            TooltipText = Ui.Tip(Tr("This is a mutual pact. Select it on one side only; an existing treaty cannot be sold again.")) };
        half.AddChild(trade);
        embargo = new CheckBox
        {
            Name = ours ? "DealGiveEmbargo" : "DealAskEmbargo",
            Text = Tr(ours ? "Lift our embargo" : "Lift their embargo"),
            // Our half lifts an embargo we imposed; their half asks them to lift theirs.
            Disabled = relation?.EmbargoBy(owner.Id) != true,
            TooltipText = Ui.Tip(Tr("Only the country that imposed this embargo can lift it. Disabled means it has no embargo to remove."))
        };
        half.AddChild(embargo);
        half.AddChild(new HSeparator());
        half.AddChild(Title("REGIONS TO TRANSFER", 13, Ui.Colour("text.accent")));
        release = new CheckBox { Name = ours ? "DealGiveRelease" : "DealAskRelease", Text = Tr("Establish an independent country"),
            Disabled = !_campaign.FeatureEnabled("secession"), ClipText = true,
            TooltipText = Ui.Tip(Tr("Release selected whole, connected regions instead of transferring them to the partner. The new country receives its population share of treasury and debt. Its identity follows local peoples and the largest region. Voluntary release renounces the parent's homeland title to these original regions. One settlement per deal; the parent must retain land.")) };
        half.AddChild(release);
        var search = new LineEdit { Name = ours ? "DealGiveRegionSearch" : "DealAskRegionSearch",
            PlaceholderText = Tr("Search regions…"), ClearButtonEnabled = true,
            TooltipText = Ui.Tip(Tr("Filter by the displayed region name. Filtering does not remove your selected terms.")) };
        half.AddChild(search);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        half.AddChild(scroll);
        var regionRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        regionRows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(regionRows);
        foreach (var state in _campaign.States.Where(s => s.NationMapIndex == owner.MapIndex).OrderBy(s => s.Name))
        {
            var id = state.MapIndex;
            var capital = id == owner.CapitalStateMapIndex;
            var choice = new CheckBox { Name = $"{(ours ? "DealGiveRegion_" : "DealAskRegion_")}{id}",
                Text = state.DisplayName + "  ·  " + Fmt.People(state.Population) + (capital ? "  ·  " + Tr("capital") : ""),
                Disabled = capital,
                TooltipText = (capital ? Tr("A capital is not for sale.") + "\n" : "") +
                    state.DisplayName + " · " + Fmt.People(state.Population) + "\n" +
                    Loc.Format("Worth to {0}: {1} points", owner.DisplayName, Ultimatum.RegionWorth(_campaign, owner, state, receiving: false).ToString("0")) + "\n" +
                    Loc.Format("Largest people: {0}", Loc.Get(DemographyCatalog.CultureKey(EconomySystem.Cultures(state).FirstOrDefault().Culture ?? "Unknown"))) + "\n" +
                    Tr("Only fully owned and controlled regions may be exchanged. Each country must retain actual territory."), ClipText = true,
                CustomMinimumSize = new(150, 32) };
            choice.Toggled += selected =>
            {
                if (selected) chosen.Add(id); else chosen.Remove(id);
                RefreshDealComposer();
            };
            regionRows.AddChild(choice);
        }
        var empty = WrappedUltimatumText("No matching regions. Clear the search; monetary and treaty terms remain available.", 13);
        empty.Name = ours ? "DealGiveEmpty" : "DealAskEmpty";
        empty.Visible = regionRows.GetChildCount() == 0; regionRows.AddChild(empty);
        search.TextChanged += text =>
        {
            foreach (var choice in regionRows.GetChildren().OfType<CheckBox>())
                choice.Visible = choice.Text.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase);
            empty.Visible = !regionRows.GetChildren().OfType<CheckBox>().Any(choice => choice.Visible);
        };
        cash.ValueChanged += _ => RefreshDealComposer();
        pact.Toggled += _ => RefreshDealComposer();
        trade.Toggled += _ => RefreshDealComposer();
        embargo.Toggled += _ => RefreshDealComposer();
        release.Toggled += _ => RefreshDealComposer();
    }

    private void RefreshDealComposer()
    {
        if (_dealCompleted) return;
        _dealConfirm = false;
        var player = _campaign.Player;
        var target = _campaign.NationById(_dealTarget);
        var deal = ComposedDeal();
        var assessment = Ultimatum.AssessNegotiatedDeal(_campaign, player, target, deal);
        var valid = assessment.Valid;
        // The answer is previewed by the same arithmetic the target will use: what it
        // receives minus what it gives, its own hunger for cash, its own distrust of us.
        var acceptable = assessment.Acceptable;
        _dealSend.Disabled = !valid;
        var net = assessment.ToValuation.Total - assessment.GrudgePenalty;
        _dealMeter.Visible = valid && target?.IsHuman != true;
        if (valid) _dealMeter.Lean(Mathf.Clamp(net / 40f, -1f, 1f), Ui.Colour("state.positive"), Ui.Colour("state.negative"));
        _dealBalanceCash = valid && !acceptable ? Ultimatum.CashToBalance(_campaign, player, target, deal) : 0f;
        _dealBalance.Visible = valid && !acceptable && target?.IsHuman != true;
        _dealBalance.Disabled = _dealBalanceCash <= 0f;
        _dealBalance.Text = _dealBalanceCash > 0f
            ? Loc.Format("Pay what they still need: {0}", Fmt.Money(_dealBalanceCash))
            : Tr("Our treasury cannot close this gap");
        _dealAskMoreCash = valid && acceptable ? Ultimatum.CashToAskMore(_campaign, player, target, deal) : 0f;
        _dealAskMore.Visible = valid && acceptable && _dealAskMoreCash >= 0.01f && target?.IsHuman != true;
        _dealAskMore.Text = Loc.Format("They would still pay {0} more - ask for it", Fmt.Money(_dealAskMoreCash));
        _dealSend.Text = _dealConfirm ? Tr("CONFIRM AND SEND") : Tr("Review deal");
        _dealAssessment.Text = !valid ? Tr(assessment.Reason) : target?.IsHuman == true ? Tr("The other player decides; no automatic signature.") :
            Tr(acceptable ? "They are expected to agree" : "They are expected to refuse");
        _dealAssessment.LabelSettings.FontColor = Ui.Colour(!valid ? "state.warning"
            : target?.IsHuman == true ? "text.body" : acceptable ? "state.positive" : "state.warning");
        _dealTerms.Text = valid ? Ultimatum.DealSummary(_campaign, player, target, deal)
            : Tr("No terms have been sent. Change the package to resolve this restriction.");
        if (valid && (deal.From.ReleaseRegions || deal.To.ReleaseRegions))
        {
            var owner = deal.From.ReleaseRegions ? player : target;
            var side = deal.From.ReleaseRegions ? deal.From : deal.To;
            var share = Dismantlement.Share(_campaign, owner, side.Regions);
            _dealTerms.Text += "\n\n" + Loc.Format("Independent settlement: {0}% of the parent's population and debt. These regions do not become yours.", (share * 100).ToString("0.0"));
        }
        _dealTreasuries.Text = Loc.Format("Treasury after signing\nYou: {0}\nPartner: {1}",
            Fmt.Money(assessment.FromTreasuryAfter), Fmt.Money(assessment.ToTreasuryAfter));
        string Points(float value) => value.ToString("+0.0;-0.0;0.0");
        _dealValuation.Text = target?.IsHuman == true
            ? Tr("A player chooses whether to agree. No score predicts their signature. Sending places these exact terms in their inbox; cash and borders stay unchanged until acceptance.")
            : Loc.Format("How they weigh it, in points\nThe money: {0}\nThe land: {1}\nThe treaties: {2}\nBorder cohesion: {6}\nOld grievances: {3}\nIn all: {4} - they sign at 0 or more\n\nHow we weigh it: {5}",
            Points(assessment.ToValuation.Cash), Points(assessment.ToValuation.Land), Points(assessment.ToValuation.Treaties),
            Points(-assessment.GrudgePenalty), Points(assessment.ToValue), Points(assessment.FromValue), Points(assessment.ToValuation.Borders));
        _dealTerritoryPreview.Refresh(_campaign, deal.From.Regions, deal.To.Regions, deal.From.ReleaseRegions, deal.To.ReleaseRegions);
        _dealMapHint.Text = Tr(_dealGiveRegions.Count + _dealAskRegions.Count == 0
            ? "Select regions to see their actual location before signing."
            : "Gold: you give. Green: you ask. Hatching: independence, not annexation. Preview only; no borders change until agreement.");
    }

    private void SendComposedDeal()
    {
        if (_dealCompleted) return;
        var player = _campaign.Player;
        var target = _campaign.NationById(_dealTarget);
        var deal = ComposedDeal();
        if (!Ultimatum.ValidateNegotiatedDeal(_campaign, player, target, deal, out _)) { RefreshDealComposer(); return; }
        if (!_dealConfirm)
        { _dealConfirm = true; _dealReviewDay = _campaign.ElapsedDays; _dealSend.Text = Tr("CONFIRM AND SEND"); return; }
        if (_dealReviewDay != _campaign.ElapsedDays)
        { RefreshDealComposer(); _dealAssessment.Text = Tr("The world changed since your review. Review the current terms again before signing."); return; }
        // Dispatch through the authoritative queue: a preview is not permission to
        // bypass changed ownership, a feature switch or the partner's consent.
        var command = new ProposeDealCommand
        {
            IssuerId = player.Id, ToId = target.Id, ParentOfferId = _dealCounterParentId,
            FromRegions = deal.From.Regions, FromCash = deal.From.Cash,
            FromReleaseRegions = deal.From.ReleaseRegions,
            FromLiftEmbargo = deal.From.LiftEmbargo, FromNonAggression = deal.From.NonAggression,
            FromTradeAgreement = deal.From.TradeAgreement,
            ToRegions = deal.To.Regions, ToCash = deal.To.Cash,
            ToReleaseRegions = deal.To.ReleaseRegions,
            ToLiftEmbargo = deal.To.LiftEmbargo, ToNonAggression = deal.To.NonAggression,
            ToTradeAgreement = deal.To.TradeAgreement
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        ForceDiplomacyRefresh();
        RefreshCountryViews("Diplomacy");
        Refresh();
        if (_networkConnected && !_networkIsHost)
        {
            _dealCompleted = true;
            _dealSend.Disabled = true;
            _dealAssessment.Text = Tr("Offer sent to the host. Awaiting confirmation.");
            return;
        }
        if (command.Outcome.Accepted || command.Outcome.PendingOfferId > 0)
        {
            _dealCompleted = true;
            _dealGiveCash.Editable = _dealAskCash.Editable = false;
            foreach (var choice in _dealComposer.FindChildren("*", "CheckBox", true, false).OfType<CheckBox>())
                choice.Disabled = true;
            _dealSend.Disabled = true;
            _dealSend.Text = Tr("Deal delivered");
            _dealClose.Text = Tr("Close");
            _dealAssessment.Text = Tr(command.Outcome.Accepted ? "The bargain is signed" : "Offer delivered; awaiting a reply.");
            _dealAssessment.LabelSettings.FontColor = Ui.Colour("state.positive");
            _dealTerms.Text = command.Outcome.Note;
            return;
        }
        // Refused: the table stays open. The composer resets so the improved offer is a
        // new package, not a silent repeat of the one they just declined.
        _dealConfirm = false;
        _dealSend.Text = Tr("Review deal");
        _dealClose.Text = Tr("Close");
        _dealSend.Disabled = false;
        _dealAssessment.Text = Tr(command.Outcome.Note);
        _dealAssessment.LabelSettings.FontColor = Ui.Colour("state.warning");
        _dealTerms.Text = Tr("The table is still open - change the terms and send it again.");
    }
}
