using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The agreements short of an alliance, and aid (AgreementSystem): a trade agreement, a
/// non-aggression pact, a package of money and an embargo, beside the alliance and the
/// guarantee. Each button says what it would do before it is pressed - whether they would
/// sign and why, what walking out costs, what a package is worth - so none of them is a guess.
/// </summary>
public partial class DealOrForceGame
{
    private Button _tradeAgreementButton = null!;
    private Button _pactButton = null!;
    private Button _aidButton = null!;
    private Button _embargoButton = null!;
    private Button _guaranteeRequestButton = null!;

    private void BuildAgreementControls(VBoxContainer parent)
    {
        var row = new VBoxContainer { Name = "AgreementActions" };
        row.AddThemeConstantOverride("separation", 5);
        parent.AddChild(row);
        _tradeAgreementButton = ActionButton("Trade agreement", () => ToggleAgreement(AgreementKind.TradeAgreement), 0, 32);
        _pactButton = ActionButton("Non-aggression pact", () => ToggleAgreement(AgreementKind.NonAggressionPact), 0, 32);
        _aidButton = ActionButton("Send aid", SendSelectedAid, 0, 32);
        _embargoButton = ActionButton("Embargo", ToggleSelectedEmbargo, 0, 32);
        row.AddChild(_tradeAgreementButton);
        row.AddChild(_pactButton);
        AddMilitaryAccessControls(row, () => _selectedNation, "Quick");
        // A deal is the treaty a player writes: with the others, before aid and sanctions, in
        // the dossier as in the full diplomacy screen.
        var deal = ActionButton("Propose a deal", ProposeDealToSelected, 0, 32);
        deal.Name = "ProposeDealQuick";
        deal.TooltipText = Ui.Tip(Tr("Lay a table both sides put something on: cash, provinces, promises. Review before sending."));
        row.AddChild(deal);
        row.AddChild(_aidButton);
        row.AddChild(_embargoButton);
        // The half of a guarantee only the computer used to be able to ask for: finding a patron.
        _guaranteeRequestButton = ActionButton("Request guarantee", RequestSelectedGuarantee, 0, 32);
        _guaranteeRequestButton.Name = "RequestGuaranteeQuick";
        row.AddChild(_guaranteeRequestButton);
    }

    private void ProposeDealToSelected()
    {
        var target = _selectedNation;
        if (target is null || _campaign?.Player is null || target == _campaign.Player || target.IsHuman) return;
        if (!Ultimatum.CanOfferDeal(_campaign, _campaign.Player, target, out var reason))
        {
            if (_countryActionFeedback is not null) _countryActionFeedback.Text = reason;
            return;
        }
        OpenDealComposer(target);
    }

    private void RefreshAgreementButtons(Nation nation)
        => RefreshAgreementButtons(nation, _tradeAgreementButton, _pactButton, _aidButton, _embargoButton);

    /// <summary>
    /// Ask somebody to promise to fight for you - the move a small country actually needs, and
    /// the one only the computer used to have (Guarantee.Ask). It is answered on the spot by a
    /// country the computer speaks for, and left on their table when it is a player's.
    /// </summary>
    private void RequestSelectedGuarantee() => RequestGuaranteeFrom(_selectedNation, _countryActionFeedback);

    private void RequestGuaranteeFrom(Nation target, Label feedback)
    {
        if (_campaign?.Player is null || target is null || target == _campaign.Player) return;
        var request = new RequestGuaranteeCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id };
        _campaign.Commands.Enqueue(request);
        _campaign.Commands.ApplyAll(_campaign);
        feedback.Text = request.Granted
            ? Simulation.Loc.Format("{0} guarantees {1}.", target.DisplayName, _campaign.Player.DisplayName)
            : request.Answer;
        RefreshDiplomaticActionViews();
    }

    /// <summary>Says what they would answer before the player asks, the way every other button does.</summary>
    private void RefreshGuaranteeRequestButton(Button button, Nation nation)
    {
        if (button is null || _campaign?.Player is null || nation is null || nation == _campaign.Player) return;
        var player = _campaign.Player;
        var relation = CampaignDiplomacy.ReadOrNeutral(_campaign, player.Id, nation.Id);
        if (relation.GuaranteedBy(nation.Id))
        {
            button.Text = Tr("Guaranteed by them");
            button.Disabled = true;
            button.TooltipText = Ui.Tip(Tr("They have already promised to fight for you."));
            return;
        }
        button.Text = Tr("Request guarantee");
        var refusal = Guarantee.Refusal(_campaign, player, nation);
        button.Disabled = refusal.Length != 0;
        if (refusal.Length != 0)
        {
            button.TooltipText = Ui.Tip(refusal);
            return;
        }
        var why = new Simulation.Breakdown();
        var willingness = Guarantee.Willingness(_campaign, nation, player, why);
        var needed = _campaign.Defines.GuaranteeWillingness;
        var parts = why.Entries
            .OrderByDescending(entry => System.Math.Abs(entry.Value))
            .Take(4)
            .Select(entry => $"{Tr(entry.Source)} {entry.Value:+0.00;-0.00;0.00}");
        button.TooltipText = Ui.Tip(
            (willingness >= needed
                ? Simulation.Loc.Format("They would promise: {0} against {1} needed.", $"{willingness:0.00}", $"{needed:0.00}")
                : Simulation.Loc.Format("They would refuse: {0} against {1} needed.", $"{willingness:0.00}", $"{needed:0.00}")) +
            "\n" + string.Join("\n", parts));
    }

    private void RefreshAgreementButtons(Nation nation, Button _tradeAgreementButton, Button _pactButton,
        Button _aidButton, Button _embargoButton)
    {
        RefreshMilitaryAccessControls();
        if (_tradeAgreementButton is null || _campaign?.Player is null || nation == _campaign.Player) return;
        var player = _campaign.Player;
        var relation = CampaignDiplomacy.ReadOrNeutral(_campaign, player.Id, nation.Id);
        RefreshGuaranteeRequestButton(_guaranteeRequestButton, nation);

        RefreshAgreementButton(_tradeAgreementButton, nation, relation, AgreementKind.TradeAgreement,
            "Propose trade agreement", "Cancel trade agreement",
            Tr("Walk out of it. Their routes to you lose what the agreement bought them, and they will remember it for two years."));
        RefreshAgreementButton(_pactButton, nation, relation, AgreementKind.NonAggressionPact,
            "Propose non-aggression pact", "Break non-aggression pact",
            Simulation.Loc.Format(
                "Break the promise. Without it you may declare war on them - and everybody learns what your word is worth: {0} notoriety, and they will not forget it for five years.",
                _campaign.Defines.NonAggressionPactBreakNotoriety.ToString("0")));

        var aid = AgreementSystem.AidAmount(_campaign, player, nation, out var why, preview: true);
        _aidButton.Text = aid > 0f
            ? Simulation.Loc.Format("Send aid ({0})", Fmt.Money(aid))
            : Tr("Send aid");
        _aidButton.Disabled = aid <= 0f;
        _aidButton.TooltipText = Ui.Tip(aid > 0f
            ? Simulation.Loc.Format(
                "Send {0} from the treasury: a hundredth of what they make in a year. It buys goodwill that is spent over two years, and it can be sent again in {1} days.",
                Fmt.Money(aid), _campaign.Defines.AidIntervalDays.ToString("N0"))
            : why);

        var embargo = relation.EmbargoBy(player.Id);
        _embargoButton.Text = embargo ? Tr("Lift embargo") : Tr("Embargo");
        _embargoButton.Disabled = false;
        _embargoButton.TooltipText = Ui.Tip(embargo
            ? Tr("Let their goods across your border again.")
            : Tr("Nothing of theirs crosses your border, and nothing of yours crosses theirs. The world's surplus of any one good is finite, so a large trading partner refusing is felt within the month."));
    }

    private void RefreshAgreementButton(Button button, Nation nation, DiplomaticRelation relation, AgreementKind kind,
        string propose, string cancel, string cancelTip)
    {
        var player = _campaign.Player;
        if (AgreementSystem.Signed(relation, kind))
        {
            button.Text = Tr(cancel);
            button.Disabled = false;
            button.TooltipText = Ui.Tip(cancelTip);
            return;
        }
        // They asked. The button is the answer, and leaving it alone is also an answer: the
        // offer lapses on its own and nothing is signed either way.
        if (AgreementSystem.OfferedBy(_campaign, player, nation.Id, out var offered) && offered == kind)
        {
            var waited = _campaign.ElapsedDays - relation.AgreementOfferedDay;
            var left = System.Math.Max(0, _campaign.Defines.AgreementReplyDays - waited);
            button.Text = kind == AgreementKind.TradeAgreement
                ? Tr("Accept trade agreement")
                : Tr("Accept non-aggression pact");
            button.Disabled = false;
            button.TooltipText = Ui.Tip(Simulation.Loc.Format(
                "They asked {0} day(s) ago. Signing it takes effect at once; leaving it unanswered lets it lapse in {1} day(s).",
                waited, left));
            return;
        }
        button.Text = Tr(propose);
        var can = AgreementSystem.CanPropose(_campaign, player, nation, kind, out var refusal, preview: true);
        button.Disabled = !can;
        if (!can)
        {
            button.TooltipText = Ui.Tip(refusal);
            return;
        }
        var why = new Simulation.Breakdown();
        var willingness = AgreementSystem.Willingness(_campaign, nation, player, kind, why, preview: true);
        var needed = AgreementSystem.Needed(_campaign, kind);
        var parts = why.Entries
            .OrderByDescending(entry => System.Math.Abs(entry.Value))
            .Take(4)
            .Select(entry => $"{Tr(entry.Source)} {entry.Value:+0.00;-0.00;0.00}");
        button.TooltipText = Ui.Tip(
            (willingness >= needed
                ? Simulation.Loc.Format("They would sign: {0} against {1} needed.", $"{willingness:0.00}", $"{needed:0.00}")
                : Simulation.Loc.Format("They would refuse: {0} against {1} needed.", $"{willingness:0.00}", $"{needed:0.00}")) +
            "\n" + string.Join("\n", parts));
    }

    private void ToggleAgreement(AgreementKind kind)
        => ToggleAgreement(kind, _selectedNation, _countryActionFeedback);

    private void ToggleAgreement(AgreementKind kind, Nation target, Label feedback)
    {
        if (_campaign?.Player is null || target is null || target == _campaign.Player) return;
        var player = _campaign.Player;
        var relation = CampaignDiplomacy.Get(_campaign, player.Id, target.Id);
        var name = kind == AgreementKind.TradeAgreement ? Tr("trade agreement") : Tr("non-aggression pact");
        if (AgreementSystem.Signed(relation, kind))
        {
            var cancel = new CancelAgreementCommand { IssuerId = player.Id, TargetId = target.Id, Kind = kind };
            _campaign.Commands.Enqueue(cancel);
            _campaign.Commands.ApplyAll(_campaign);
            feedback.Text = cancel.Cancelled
                ? Simulation.Loc.Format("The {0} with {1} is over.", name, target.DisplayName)
                : Tr("Nothing to cancel.");
        }
        else
        {
            // An offer waiting on this player is answered, not proposed over: two offers on one
            // tie at once is a conversation neither side can follow.
            if (AgreementSystem.OfferedBy(_campaign, player, target.Id, out var offered) && offered == kind)
            {
                var answer = new AnswerAgreementCommand { IssuerId = player.Id, TargetId = target.Id, Accept = true };
                _campaign.Commands.Enqueue(answer);
                _campaign.Commands.ApplyAll(_campaign);
                feedback.Text = answer.Reason;
            }
            else
            {
                var propose = new ProposeAgreementCommand { IssuerId = player.Id, TargetId = target.Id, Kind = kind };
                _campaign.Commands.Enqueue(propose);
                _campaign.Commands.ApplyAll(_campaign);
                feedback.Text = propose.Result switch
                {
                    AgreementResult.Signed => Simulation.Loc.Format("{0} signed the {1}.", target.DisplayName, name),
                    AgreementResult.Refused => Simulation.Loc.Format("{0} declined the {1}.", target.DisplayName, name),
                    _ => propose.Reason
                };
            }
        }
        RefreshDiplomaticActionViews();
    }

    private void SendSelectedAid()
        => SendAidTo(_selectedNation, _countryActionFeedback);

    private void SendAidTo(Nation target, Label feedback)
    {
        if (_campaign?.Player is null || target is null || target == _campaign.Player) return;
        var send = new SendAidCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id };
        _campaign.Commands.Enqueue(send);
        _campaign.Commands.ApplyAll(_campaign);
        feedback.Text = send.Sent > 0f
            ? Simulation.Loc.Format("{0} of aid is on its way to {1}.", Fmt.Money(send.Sent), target.DisplayName)
            : Tr("The package could not be sent.");
        RefreshDiplomaticActionViews();
    }

    private void ToggleSelectedEmbargo()
        => ToggleEmbargoOn(_selectedNation, _countryActionFeedback);

    private void ToggleEmbargoOn(Nation target, Label feedback)
    {
        if (_campaign?.Player is null || target is null || target == _campaign.Player) return;
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, target.Id);
        var on = !relation.EmbargoBy(_campaign.Player.Id);
        _campaign.Commands.Enqueue(new SetEmbargoCommand
        {
            IssuerId = _campaign.Player.Id, TargetId = target.Id, Embargo = on
        });
        _campaign.Commands.ApplyAll(_campaign);
        feedback.Text = on
            ? Simulation.Loc.Format("Embargo on {0} in force.", target.DisplayName)
            : Simulation.Loc.Format("Embargo on {0} lifted.", target.DisplayName);
        RefreshDiplomaticActionViews();
    }

    private void RefreshDiplomaticActionViews()
    {
        RefreshCountryViews("Diplomacy");
        ForceDiplomacyRefresh();
        Refresh();
    }
}
