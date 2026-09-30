using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The three ways to make a country answer to yours without a war, beside the alliance and
/// the guarantee they grow out of (SubmissionSystem). Each button's tooltip is the answer
/// before the asking - what it costs, and the terms the other side would weigh - so an offer
/// is a decision rather than a dice roll.
/// </summary>
public partial class DealOrForceGame
{
    private VBoxContainer _submissionActions = null!;
    private readonly Dictionary<SubmissionRoute, Button> _submissionButtons = new();

    private void BuildSubmissionControls(VBoxContainer parent)
    {
        _submissionActions = new VBoxContainer { Name = "InfluenceActions", Visible = false };
        _submissionActions.AddThemeConstantOverride("separation", 5);
        parent.AddChild(_submissionActions);
        _submissionActions.AddChild(Title("Influence and protection", 12, Ui.Colour("text.caption")));
        foreach (var (route, caption) in new[]
                 {
                     (SubmissionRoute.Protection, "Offer protection"),
                     (SubmissionRoute.Economic, "Buy influence"),
                     (SubmissionRoute.Political, "Back a government")
                 })
        {
            var captured = route;
            var button = ActionButton(caption, () => OfferSubmission(captured), 0, 32);
            _submissionActions.AddChild(button);
            _submissionButtons[route] = button;
        }
    }

    private void RefreshSubmissionButtons(Nation nation)
        => RefreshSubmissionButtons(nation, _submissionActions, _submissionButtons);

    private void RefreshSubmissionButtons(Nation nation, VBoxContainer _submissionActions,
        Dictionary<SubmissionRoute, Button> _submissionButtons)
    {
        if (_submissionActions is null) return;
        var player = _campaign?.Player;
        _submissionActions.Visible = player is not null && nation is not null && nation != player &&
                                     !nation.IsHuman && !nation.IsSubject && !player.IsSubject;
        if (!_submissionActions.Visible) return;

        var needed = _campaign.Defines.SubmissionWillingness;
        foreach (var (route, button) in _submissionButtons)
        {
            var what = SubmissionExplanation(route, nation);
            var refusal = SubmissionSystem.Refusal(_campaign, player, nation, route);
            button.Disabled = refusal.Length > 0;
            if (refusal.Length > 0)
            {
                button.TooltipText = Ui.Tip(what + "\n\n" + refusal);
                continue;
            }
            var why = new Breakdown();
            var willingness = SubmissionSystem.Willingness(_campaign, nation, player, route, why);
            var parts = why.Entries
                .OrderByDescending(entry => System.Math.Abs(entry.Value))
                .Take(4)
                .Select(entry => $"{Tr(entry.Source)} {entry.Value:+0.00;-0.00;0.00}");
            button.TooltipText = Ui.Tip(what + "\n\n" +
                (willingness > needed
                    ? Loc.Format("They would accept: {0} against {1} needed.", $"{willingness:0.00}", $"{needed:0.00}")
                    : Loc.Format("They would refuse: {0} against {1} needed.", $"{willingness:0.00}", $"{needed:0.00}")) +
                "\n" + string.Join("\n", parts));
        }
    }

    private string SubmissionExplanation(SubmissionRoute route, Nation nation)
    {
        var cost = Fmt.Money(SubmissionSystem.Cost(_campaign, nation, route));
        return route switch
        {
            SubmissionRoute.Protection => Tr("Offer to keep them safe in return for their foreign policy: they become a protectorate. " +
                                            "It costs nothing, but a refusal is remembered."),
            SubmissionRoute.Economic => Loc.Format("Pay {0} for a client state. The money changes hands only if they accept.", cost),
            _ => Loc.Format("Spend {0} backing a friendly government, to make a satellite of them. " +
                            "It is spent whatever happens, and the world notices.", cost)
        };
    }

    private void OfferSubmission(SubmissionRoute route)
        => OfferSubmission(route, _selectedNation, _countryActionFeedback);

    private void OfferSubmission(SubmissionRoute route, Nation target, Label feedback)
    {
        if (_campaign?.Player is null || target is null || target == _campaign.Player) return;
        var command = new OfferSubmissionCommand
            { IssuerId = _campaign.Player.Id, TargetId = target.Id, Route = route };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        var answer = command.Answer;
        RefreshDiplomaticActionViews();
        feedback.Text = answer;
    }
}
