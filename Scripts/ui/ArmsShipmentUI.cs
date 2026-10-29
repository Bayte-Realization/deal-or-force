using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Button _diplomacyAskArms;
    private int _armsInboxSelected = -1, _armsConfirm = -1;
    private string _armsConfirmQuote = "";
    private System.Collections.Generic.HashSet<int> _armsNoticeIds = new();

    private void ClearArmsInboxSelection()
    {
        _armsInboxSelected = -1;
        _armsConfirm = -1;
    }

    private void RefreshArmsRequestAction(Nation donor)
    {
        if (_diplomacyAskArms is null || _campaign?.Player is not { } player) return;
        _diplomacyAskArms.Visible = ArmsShipmentRequests.Enabled(_campaign);
        if (donor is null) return;
        var preview = ArmsShipmentRequests.CanSubmit(_campaign, player.Id, donor.Id, -1);
        _diplomacyAskArms.Disabled = _observerMode || !preview.Allowed;
        var percent = (int)System.MathF.Round(preview.Share * 100f);
        _diplomacyAskArms.TooltipText = Ui.Tip(preview.Allowed
            ? Loc.Format("Ask for one finite shipment from their depots: {0}% of {1} days of intake, of goods they still cover. They may refuse. This does not hire them into the war and does not start a standing subsidy.", percent, preview.Days)
            : Tr(preview.Reason));
    }

    private void RequestArmsFrom(Nation donor)
    {
        if (_observerMode || donor is null || _campaign?.Player is not { IsHuman: true } player) return;
        var command = new RequestArmsShipmentCommand { IssuerId = player.Id, DonorId = donor.Id };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        var reason = Tr(command.Outcome.Reason ?? "");
        if (IsInstanceValid(_diplomacyFeedback)) _diplomacyFeedback.Text = reason;
        if (command.Outcome.RequestId > 0)
        {
            _armsInboxSelected = command.Outcome.RequestId;
            _dealInboxSelected = -1;
            _ultimatumInboxSelected = -1;
            _assistanceInboxSelected = -1;
            OpenDealInbox();
        }
        ForceDiplomacyRefresh();
        Refresh();
    }

    private string ArmsInboxKey() => _armsInboxSelected + "|" + string.Join(";", _campaign.ArmsShipmentRequests
        .Where(r => r.RequesterId == _campaign.Player.Id || r.DonorId == _campaign.Player.Id)
        .Select(r => $"{r.Id}:{r.State}:{r.Share:R}:{r.Days}:{r.Shipped:R}:{r.ExpiresDay}:{r.Pretext}"));

    private static string ArmsStateKey(ArmsShipmentState state) => state switch
    {
        ArmsShipmentState.Pending => "Awaiting reply",
        ArmsShipmentState.Granted => "Shipment sent",
        ArmsShipmentState.Declined => "Declined",
        ArmsShipmentState.Withdrawn => "Withdrawn",
        ArmsShipmentState.Expired => "Expired",
        _ => "No longer valid"
    };

    private string ArmsQuoteKey(ArmsShipmentRequest letter)
    {
        var live = ArmsShipmentRequests.Assess(_campaign, letter.RequesterId, letter.DonorId, letter.WarId, letter.Share, letter.Days);
        var donor = _campaign.NationById(letter.DonorId);
        var days = StrategicStockpiles.DaysOnHand(donor, "Ammunition");
        return $"{letter.Id}|{letter.State}|{live.Allowed}|{live.Pretext}|{letter.Share:R}|{letter.Days}|{days}";
    }

    private bool AddArmsInboxRows(bool showDetail = true)
    {
        var player = _campaign.Player;
        var letters = _campaign.ArmsShipmentRequests.Where(r => r.RequesterId == player.Id || r.DonorId == player.Id)
            .OrderBy(r => r.State != ArmsShipmentState.Pending).ThenByDescending(r => r.Id).ToArray();
        foreach (var letter in letters)
        {
            var id = letter.Id;
            var counterpart = _campaign.NationById(letter.RequesterId == player.Id ? letter.DonorId : letter.RequesterId);
            var button = ActionButton(Loc.Format("Arms request · {0}\n{1}", counterpart?.DisplayName ?? "?", Tr(ArmsStateKey(letter.State))), () =>
            {
                _armsInboxSelected = id;
                _dealInboxSelected = -1;
                _ultimatumInboxSelected = -1;
                _assistanceInboxSelected = -1;
                _armsConfirm = -1;
                _dealInboxMessage = "";
                RefreshDealInbox(force: true);
            }, 0, 54, id == _armsInboxSelected);
            button.Name = "ArmsShipment_" + id;
            button.ClipText = true;
            _dealInboxRows.AddChild(button);
        }
        var selected = letters.FirstOrDefault(r => r.Id == _armsInboxSelected);
        if (!showDetail) return false;
        if (selected is null && _dealInboxSelected < 0 && _ultimatumInboxSelected < 0 && _assistanceInboxSelected < 0)
            selected = letters.FirstOrDefault();
        if (selected is null) { _armsInboxSelected = -1; return false; }
        _armsInboxSelected = selected.Id;
        ShowArmsLetter(selected);
        return true;
    }

    private void ShowArmsLetter(ArmsShipmentRequest letter)
    {
        var requester = _campaign.NationById(letter.RequesterId);
        var donor = _campaign.NationById(letter.DonorId);
        var pending = letter.State == ArmsShipmentState.Pending && letter.ExpiresDay > _campaign.ElapsedDays;
        var assessment = ArmsShipmentRequests.Assess(_campaign, letter.RequesterId, letter.DonorId, letter.WarId, letter.Share, letter.Days);
        var quote = ArmsQuoteKey(letter);
        if (_armsConfirm == letter.Id && quote != _armsConfirmQuote)
        {
            _armsConfirm = -1;
            _dealInboxMessage = Tr("The depots changed. Review the shipment before confirming.");
        }
        var title = Title(Loc.Format("Arms request #{0} · {1}", letter.Id, Tr(ArmsStateKey(letter.State))), 20, Ui.Colour("text.accent"));
        title.Name = "ArmsShipmentHeader";
        _dealInboxDetail.AddChild(title);
        var countries = new HBoxContainer();
        countries.AddThemeConstantOverride("separation", 10);
        _dealInboxDetail.AddChild(countries);
        countries.AddChild(FlagBadge(requester?.Tag ?? "", 26));
        var names = WrappedUltimatumText(Loc.Format("{0} asks {1} for a shipment of ammunition and arms.", requester?.DisplayName ?? "?", donor?.DisplayName ?? "?"), 16);
        names.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        countries.AddChild(names);
        countries.AddChild(FlagBadge(donor?.Tag ?? "", 26));
        if ((pending ? assessment.Pretext : letter.Pretext).Length > 0)
            _dealInboxDetail.AddChild(WrappedUltimatumText(Loc.Format("Pretext: {0}", Tr(pending ? assessment.Pretext : letter.Pretext)), 15));
        var percent = (int)System.MathF.Round(letter.Share * 100f);
        _dealInboxDetail.AddChild(WrappedUltimatumText(Loc.Format(
            "Price: {0}% of {1} days of intake, and only of ammunition, arms, artillery and drones the depots still cover.",
            percent, letter.Days), 15));
        var ammoDays = StrategicStockpiles.DaysOnHand(donor, "Ammunition");
        _dealInboxDetail.AddChild(WrappedUltimatumText(ammoDays is int days
            ? Loc.Format("Ammunition on hand: {0} days.", days)
            : Tr("Depot days are not on the books yet. Consent still sends only what the depots can cover."), 14));
        _dealInboxDetail.AddChild(WrappedUltimatumText(
            "Nothing is reserved by reading this letter. A standing subsidy is a separate act. This letter does not move an army or take land.", 13));
        if (letter.WarId >= 0)
        {
            var war = _campaign.Wars.FirstOrDefault(w => w.Id == letter.WarId);
            _dealInboxDetail.AddChild(WrappedUltimatumText(Loc.Format("Related war: {0}", Tr(war?.Name ?? "?")), 14));
        }
        if (pending)
            _dealInboxDetail.AddChild(WrappedUltimatumText(Loc.Format("Reply within {0} days", letter.ExpiresDay - _campaign.ElapsedDays), 14));
        if (pending && !assessment.Allowed)
            _dealInboxDetail.AddChild(WrappedUltimatumText(Tr(assessment.Reason), 14));
        else if (!pending && letter.Reason.Length > 0)
            _dealInboxDetail.AddChild(WrappedUltimatumText(Tr(letter.Reason), 14));
        _dealInboxActions.Visible = pending;
        if (pending && letter.DonorId == _campaign.Player.Id)
        {
            var accept = ActionButton(_armsConfirm == letter.Id ? "Confirm the shipment" : "Send the shipment", () =>
            {
                var live = ArmsQuoteKey(letter);
                if (_armsConfirm != letter.Id || _armsConfirmQuote != live)
                {
                    _armsConfirm = letter.Id;
                    _armsConfirmQuote = live;
                    RefreshDealInbox(force: true);
                    return;
                }
                ReplyToArms(letter.Id, ArmsShipmentReply.Accept);
            }, 190, 36, true);
            accept.Name = "AcceptArmsShipment";
            accept.Disabled = !assessment.Allowed;
            accept.TooltipText = Ui.Tip(Tr("First click reviews the shipment. Second click sends it. Depots are checked again."));
            _dealInboxActions.AddChild(accept);
            var decline = ActionButton("Decline", () => ReplyToArms(letter.Id, ArmsShipmentReply.Decline), 110, 36);
            decline.Name = "DeclineArmsShipment";
            _dealInboxActions.AddChild(decline);
        }
        else if (pending && letter.RequesterId == _campaign.Player.Id)
            _dealInboxActions.AddChild(ActionButton("Withdraw request", () => ReplyToArms(letter.Id, ArmsShipmentReply.Withdraw), 180, 36));
        if (_dealInboxMessage.Length > 0 && _dealInboxMessage != Tr(letter.Reason))
            _dealInboxDetail.AddChild(WrappedUltimatumText(_dealInboxMessage, 14));
    }

    private void ReplyToArms(int id, ArmsShipmentReply reply)
    {
        var command = new RespondArmsShipmentCommand { IssuerId = _campaign.Player.Id, RequestId = id, Reply = reply };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _armsConfirm = -1;
        ForceDiplomacyRefresh();
        Refresh();
        _dealInboxMessage = Tr(command.Outcome.Reason ?? "");
        RefreshDealInbox(force: true);
    }
}
