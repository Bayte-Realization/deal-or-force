using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Button _diplomacyRequestAssistance;
    private int _assistanceInboxSelected = -1, _assistanceConfirm = -1;
    private string _assistanceConfirmQuote = "";
    private System.Collections.Generic.HashSet<int> _assistanceNoticeIds = new();

    private float RequestedAssistanceAmount() => _campaign?.Player is { } n && float.IsFinite(n.Gdp) &&
        float.IsFinite(_campaign.Defines.AidShareOfRecipientGdp)
            ? (float)Math.Clamp((double)n.Gdp * _campaign.Defines.AidShareOfRecipientGdp, 0, float.MaxValue) : 0;

    private void RefreshAssistanceAction(Nation donor)
    {
        if (_diplomacyRequestAssistance is null) return;
        var preview = ForeignAssistance.CanSubmit(_campaign, _campaign.Player.Id, donor.Id, RequestedAssistanceAmount());
        _diplomacyRequestAssistance.Visible = ForeignAssistance.Enabled(_campaign);
        _diplomacyRequestAssistance.Disabled = _observerMode || !preview.Allowed;
        _diplomacyRequestAssistance.TooltipText = Ui.Tip(preview.Allowed
            ? Loc.Format("Ask for up to {0}. This is a request, not a transfer. The donor may refuse.", Fmt.Money(RequestedAssistanceAmount()))
            : Tr(preview.Reason));
    }

    private void RequestAssistanceFrom(Nation donor)
    {
        if (_observerMode || donor is null || _campaign?.Player is not { IsHuman: true } player) return;
        var command = new RequestForeignAssistanceCommand { IssuerId = player.Id, DonorId = donor.Id, Amount = RequestedAssistanceAmount() };
        _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        _diplomacyFeedback.Text = _networkConnected && !_networkIsHost
            ? Tr("Request sent to the host. Awaiting confirmation.") : Tr(command.Outcome.Reason ?? "");
        if (command.Outcome.RequestId > 0)
        { _assistanceInboxSelected = command.Outcome.RequestId; _dealInboxSelected = -1; OpenDealInbox(); }
        ForceDiplomacyRefresh(); Refresh();
    }

    private string AssistanceInboxKey() => _assistanceInboxSelected + "|" + string.Join(";", _campaign.AssistanceRequests
        .Where(r => r.RequesterId == _campaign.Player.Id || r.DonorId == _campaign.Player.Id)
        .Select(r => $"{r.Id}:{r.State}:{r.Amount:R}:{r.GrantedAmount:R}:{r.ExpiresDay}:{r.Reason}:" +
            $"{_campaign.NationById(r.DonorId)?.Treasury:R}:{_campaign.NationById(r.RequesterId)?.Treasury:R}"));

    private static string AssistanceStateKey(ForeignAssistanceState state) => state switch
    {
        ForeignAssistanceState.Pending => "Awaiting reply", ForeignAssistanceState.Granted => "Assistance granted",
        ForeignAssistanceState.Declined => "Declined", ForeignAssistanceState.Withdrawn => "Withdrawn",
        ForeignAssistanceState.Expired => "Expired", _ => "No longer valid"
    };

    private string AssistanceQuoteKey(ForeignAssistanceRequest letter)
    {
        var live = ForeignAssistance.Assess(_campaign, letter.RequesterId, letter.DonorId, letter.Amount, letter.WarId);
        return $"{letter.Id}|{letter.State}|{live.Allowed}|{live.PayableAmount:R}|" +
            $"{_campaign.NationById(letter.DonorId)?.Treasury:R}|{_campaign.NationById(letter.RequesterId)?.Treasury:R}";
    }

    private bool AddAssistanceInboxRows()
    {
        var player = _campaign.Player;
        var letters = _campaign.AssistanceRequests.Where(r => r.RequesterId == player.Id || r.DonorId == player.Id)
            .OrderBy(r => r.State != ForeignAssistanceState.Pending).ThenByDescending(r => r.Id).ToArray();
        foreach (var letter in letters)
        {
            var id = letter.Id;
            var counterpart = _campaign.NationById(letter.RequesterId == player.Id ? letter.DonorId : letter.RequesterId);
            var button = ActionButton(Loc.Format("Financial request · {0}\n{1}", counterpart?.DisplayName ?? "?", Tr(AssistanceStateKey(letter.State))), () =>
            {
                _assistanceInboxSelected = id; _dealInboxSelected = -1; _assistanceConfirm = -1; _dealInboxMessage = "";
                RefreshDealInbox(force: true);
            }, 0, 54, id == _assistanceInboxSelected);
            button.Name = "AssistanceRequest_" + id; button.ClipText = true;
            _dealInboxRows.AddChild(button);
        }
        var selected = letters.FirstOrDefault(r => r.Id == _assistanceInboxSelected);
        if (selected is null && _dealInboxSelected < 0) selected = letters.FirstOrDefault();
        if (selected is null) { _assistanceInboxSelected = -1; return false; }
        _assistanceInboxSelected = selected.Id;
        ShowAssistanceLetter(selected);
        return true;
    }

    private void ShowAssistanceLetter(ForeignAssistanceRequest letter)
    {
        var requester = _campaign.NationById(letter.RequesterId); var donor = _campaign.NationById(letter.DonorId);
        var pending = letter.State == ForeignAssistanceState.Pending && letter.ExpiresDay > _campaign.ElapsedDays;
        var assessment = ForeignAssistance.Assess(_campaign, letter.RequesterId, letter.DonorId, letter.Amount, letter.WarId);
        var quote = AssistanceQuoteKey(letter);
        if (_assistanceConfirm == letter.Id && quote != _assistanceConfirmQuote)
        { _assistanceConfirm = -1; _dealInboxMessage = Tr("The financial situation changed. Review the current amount before confirming."); }
        var title = Title(Loc.Format("Financial request #{0} · {1}", letter.Id, Tr(AssistanceStateKey(letter.State))), 20, Ui.Colour("text.accent"));
        title.Name = "AssistanceRequestHeader"; _dealInboxDetail.AddChild(title);
        var countries = new HBoxContainer(); countries.AddThemeConstantOverride("separation", 10); _dealInboxDetail.AddChild(countries);
        countries.AddChild(FlagBadge(requester?.Tag ?? "", 26));
        var names = WrappedUltimatumText(Loc.Format("{0} asks {1} for financial assistance.", requester?.DisplayName ?? "?", donor?.DisplayName ?? "?"), 16);
        names.SizeFlagsHorizontal = SizeFlags.ExpandFill; countries.AddChild(names); countries.AddChild(FlagBadge(donor?.Tag ?? "", 26));
        _dealInboxDetail.AddChild(WrappedUltimatumText(Loc.Format(pending ? "Requested: {0}\nAvailable package now: {1}" :
            "Requested: {0}\nGranted: {1}", Fmt.Money(letter.Amount), Fmt.Money(pending ? assessment.PayableAmount : letter.GrantedAmount)), 17));
        _dealInboxDetail.AddChild(WrappedUltimatumText("No money is reserved or transferred by reading this letter. This is a cash grant, not a loan, arms shipment or entry into a war.", 13));
        if (letter.WarId >= 0)
        {
            var war = _campaign.Wars.FirstOrDefault(w => w.Id == letter.WarId);
            _dealInboxDetail.AddChild(WrappedUltimatumText(Loc.Format("Related war: {0}", Tr(war?.Name ?? "?")), 14));
        }
        if (pending) _dealInboxDetail.AddChild(WrappedUltimatumText(Loc.Format("Reply within {0} days", letter.ExpiresDay - _campaign.ElapsedDays), 14));
        if (pending && assessment.Allowed)
        {
            var preview = WrappedUltimatumText(Loc.Format("After this grant\nDonor's cash: {0}\nRecipient's cash: {1}",
                Fmt.Money((donor?.Treasury ?? 0) - assessment.PayableAmount), Fmt.Money((requester?.Treasury ?? 0) + assessment.PayableAmount)), 15);
            preview.Name = "AssistanceQuote"; _dealInboxDetail.AddChild(preview);
        }
        else if (pending) _dealInboxDetail.AddChild(WrappedUltimatumText(Tr(assessment.Reason), 14));
        else if (letter.Reason.Length > 0) _dealInboxDetail.AddChild(WrappedUltimatumText(Tr(letter.Reason), 14));
        _dealInboxActions.Visible = pending;
        if (pending && letter.DonorId == _campaign.Player.Id)
        {
            var accept = ActionButton(_assistanceConfirm == letter.Id ? "Confirm financial grant" : "Review financial grant", () =>
            {
                var liveQuote = AssistanceQuoteKey(letter);
                if (_assistanceConfirm != letter.Id || _assistanceConfirmQuote != liveQuote)
                { _assistanceConfirm = letter.Id; _assistanceConfirmQuote = liveQuote; RefreshDealInbox(force: true); return; }
                var approved = ForeignAssistance.Assess(_campaign, letter.RequesterId, letter.DonorId, letter.Amount, letter.WarId).PayableAmount;
                ReplyToAssistance(letter.Id, ForeignAssistanceReply.Accept, approved);
            }, 190, 36, true);
            accept.Name = "AcceptFinancialAssistance"; accept.Disabled = !assessment.Allowed;
            accept.TooltipText = Ui.Tip(Tr("First click reviews the current package. Second click approves the payment; live resources are checked again."));
            _dealInboxActions.AddChild(accept);
            var decline = ActionButton("Decline", () => ReplyToAssistance(letter.Id, ForeignAssistanceReply.Decline), 110, 36);
            decline.Name = "DeclineFinancialAssistance"; _dealInboxActions.AddChild(decline);
        }
        else if (pending && letter.RequesterId == _campaign.Player.Id)
            _dealInboxActions.AddChild(ActionButton("Withdraw request", () => ReplyToAssistance(letter.Id, ForeignAssistanceReply.Withdraw), 180, 36));
        if (_dealInboxMessage.Length > 0 && _dealInboxMessage != Tr(letter.Reason))
            _dealInboxDetail.AddChild(WrappedUltimatumText(_dealInboxMessage, 14));
    }

    private void ReplyToAssistance(int id, ForeignAssistanceReply reply, float approved = float.MaxValue)
    {
        var command = new RespondForeignAssistanceCommand { IssuerId = _campaign.Player.Id, RequestId = id, Reply = reply, ApprovedAmount = approved };
        _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        _assistanceConfirm = -1; ForceDiplomacyRefresh(); Refresh();
        _dealInboxMessage = _networkConnected && !_networkIsHost
            ? Tr("Answer sent to the host. Awaiting confirmation.") : Tr(command.Outcome.Reason ?? "");
        RefreshDealInbox(force: true);
    }
}
