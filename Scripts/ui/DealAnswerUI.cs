using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Standing answers in the diplomatic inbox (DealAnswer): a strip of rules under the header, the
/// game's own count on every incoming offer, agreement offers with a Decline button, and the list
/// of offers the rules answered. Everything is a press, nothing is only on hover, and every target
/// is at least <see cref="DealAnswerTouch"/> tall - the inbox has to work on a phone.
/// </summary>
public partial class DealOrForceGame
{
    private const int DealAnswerTouch = 48;
    private VBoxContainer _dealAnswerStrip;
    private bool _dealAnswersOpen, _dealRuledOpen;
    private string _dealAnswerConfirmKind = "";

    /// <summary>What changes the strip: the rules, the list and the agreement offers waiting.</summary>
    private string DealAnswerKey()
    {
        var player = _campaign?.Player;
        if (player is null) return "";
        var rules = string.Join(",", player.AutoAnswer.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        var offers = string.Join(",", AgreementSystem.OfferedTo(_campaign, player).Select(o => o.Proposer.Id + ":" + (int)o.Kind));
        return $"{rules}|{player.AutoAnswerLog.Count}|{player.AutoAnswerLog.LastOrDefault()?.Day}|{offers}|" +
               $"{_dealAnswersOpen}|{_dealRuledOpen}|{_dealAnswerConfirmKind}";
    }

    private static string DealAnswerKindName(string id) =>
        Simulation.Loc.GetOr("deal_answer.kind." + id, id.Replace('_', ' '));

    private static string DealAnswerModeName(AutoAnswerMode mode) => mode switch
    {
        AutoAnswerMode.Beneficial => Simulation.Loc.Get("Sign if beneficial"),
        AutoAnswerMode.Decline => Simulation.Loc.Get("Decline"),
        _ => Simulation.Loc.Get("Ask me")
    };

    private void BuildDealAnswerStrip(VBoxContainer rows)
    {
        _dealAnswerStrip = new VBoxContainer { Name = "StandingAnswers" };
        _dealAnswerStrip.AddThemeConstantOverride("separation", 6);
        rows.AddChild(_dealAnswerStrip);
    }

    private void RefreshDealAnswerStrip()
    {
        if (!IsInstanceValid(_dealAnswerStrip) || _campaign?.Player is null) return;
        foreach (var child in _dealAnswerStrip.GetChildren()) { _dealAnswerStrip.RemoveChild(child); child.QueueFree(); }
        var player = _campaign.Player;
        var toggles = new HFlowContainer { Name = "StandingAnswerToggles" };
        toggles.AddThemeConstantOverride("h_separation", 10);
        _dealAnswerStrip.AddChild(toggles);
        var set = DealAnswerKinds.All.Count(kind => DealAnswer.ModeFor(player, kind) != AutoAnswerMode.Ask);
        var answers = ActionButton(set == 0 ? Tr("Standing answers: none set")
                : Simulation.Loc.Format("Standing answers: {0} set", set),
            () => { _dealAnswersOpen = !_dealAnswersOpen; _dealRuledOpen = false; _dealAnswerConfirmKind = ""; RefreshDealInbox(force: true); },
            0, DealAnswerTouch, _dealAnswersOpen);
        answers.Name = "StandingAnswersToggle";
        toggles.AddChild(answers);
        var ruled = ActionButton(Simulation.Loc.Format("Decided by rule ({0})", player.AutoAnswerLog.Count),
            () => { _dealRuledOpen = !_dealRuledOpen; _dealAnswersOpen = false; _dealAnswerConfirmKind = ""; RefreshDealInbox(force: true); },
            0, DealAnswerTouch, _dealRuledOpen);
        ruled.Name = "DecidedByRuleToggle";
        toggles.AddChild(ruled);
        if (!_dealAnswersOpen && !_dealRuledOpen) return;

        var scroll = new ScrollContainer
        {
            Name = "StandingAnswersScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 260), FollowFocus = true
        };
        _dealAnswerStrip.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(list);
        if (_dealAnswersOpen) FillStandingAnswers(list, player);
        else FillDecidedByRule(list, player);
    }

    private void FillStandingAnswers(VBoxContainer list, Nation player)
    {
        list.AddChild(WrappedUltimatumText(Simulation.Loc.Format(
            "Your foreign ministry can answer some offers for you. \"Sign if beneficial\" signs only when the game's own count puts the offer at least {0} points in your favour - the same count shown on every offer below.",
            DealAnswer.Margin(_campaign).ToString("0.#", Simulation.Loc.Culture)), 13));
        list.AddChild(WrappedUltimatumText("Ultimatums and peace are never answered by a rule.", 13));
        foreach (var kind in DealAnswerKinds.All)
        {
            var current = DealAnswer.ModeFor(player, kind);
            var row = new VBoxContainer { Name = "StandingAnswer_" + kind.Id };
            row.AddThemeConstantOverride("separation", 4);
            list.AddChild(row);
            var line = new HFlowContainer();
            line.AddThemeConstantOverride("h_separation", 8);
            row.AddChild(line);
            var name = Title(DealAnswerKindName(kind.Id), 15, Ui.Colour("text.bright"));
            name.CustomMinimumSize = new Vector2(220, DealAnswerTouch);
            name.VerticalAlignment = VerticalAlignment.Center;
            line.AddChild(name);
            foreach (var mode in new[] { AutoAnswerMode.Ask, AutoAnswerMode.Beneficial, AutoAnswerMode.Decline })
            {
                var confirming = mode == AutoAnswerMode.Beneficial && _dealAnswerConfirmKind == kind.Id;
                var chosen = mode == current;
                var button = ActionButton(confirming ? Tr("Confirm: sign if beneficial") : DealAnswerModeName(mode),
                    () => ChooseStandingAnswer(kind, mode), 0, DealAnswerTouch, chosen || confirming);
                button.Name = $"StandingAnswer_{kind.Id}_{DealAnswerKinds.ModeId(mode)}";
                button.Disabled = !kind.Allows(mode);
                if (chosen) button.Text = "✓ " + button.Text;
                line.AddChild(button);
            }
            var description = Simulation.Loc.GetOr("deal_answer.kind." + kind.Id + ".desc", "");
            if (description.Length > 0) row.AddChild(WrappedUltimatumText(description, 12));
            if (!kind.Allows(AutoAnswerMode.Beneficial))
                row.AddChild(WrappedUltimatumText("No rule gives land away: these offers wait for you, or are declined.", 12));
            if (_dealAnswerConfirmKind == kind.Id)
            {
                var warning = WrappedUltimatumText(Simulation.Loc.Format(
                    "Your ministry will sign such offers by itself whenever the game's count puts you at least {0} points ahead. A signed deal cannot be undone.",
                    DealAnswer.Margin(_campaign).ToString("0.#", Simulation.Loc.Culture)), 13);
                warning.LabelSettings.FontColor = Ui.Colour("state.warning");
                row.AddChild(warning);
            }
        }
    }

    private void ChooseStandingAnswer(DealAnswerKind kind, AutoAnswerMode mode)
    {
        var player = _campaign?.Player;
        if (player is null || kind is null || DealAnswer.ModeFor(player, kind) == mode) { _dealAnswerConfirmKind = ""; RefreshDealInbox(force: true); return; }
        // The first "sign if beneficial" is asked twice: it is the one answer that signs.
        if (mode == AutoAnswerMode.Beneficial && _dealAnswerConfirmKind != kind.Id)
        {
            _dealAnswerConfirmKind = kind.Id;
            RefreshDealInbox(force: true);
            return;
        }
        _dealAnswerConfirmKind = "";
        var command = new SetAutoAnswerCommand { IssuerId = player.Id, Kind = kind.Id, Mode = mode };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        ForceDiplomacyRefresh();
        ShowDealInboxOutcome(_networkConnected && !_networkIsHost
            ? Tr("Answer sent to the host. Awaiting confirmation.")
            : command.Applied ? Simulation.Loc.Format("{0}: {1}.", DealAnswerKindName(kind.Id), DealAnswerModeName(mode)) : command.Reason);
    }

    private void FillDecidedByRule(VBoxContainer list, Nation player)
    {
        if (player.AutoAnswerLog.Count == 0)
        {
            list.AddChild(WrappedUltimatumText("No offer has been answered by a rule yet.", 13));
            return;
        }
        var index = 0;
        foreach (var note in player.AutoAnswerLog.AsEnumerable().Reverse())
        {
            var proposer = _campaign.NationById(note.ProposerId);
            var what = note.Agreement >= 0
                ? ((AgreementKind)note.Agreement == AgreementKind.TradeAgreement ? Tr("Trade agreement") : Tr("Non-aggression pact"))
                : _campaign.DealOffers.FirstOrDefault(o => o.Id == note.OfferId) is { } paper
                    ? Ultimatum.DealSummary(_campaign, proposer, player, paper.Terms) : DealAnswerKindName(note.Kind);
            var line = new HFlowContainer { Name = "DecidedByRule_" + index++ };
            line.AddThemeConstantOverride("h_separation", 8);
            list.AddChild(line);
            var text = WrappedUltimatumText(Simulation.Loc.Format("Day {0} · {1}: {2} — {3} under \"{4}\"{5}",
                note.Day, proposer?.DisplayName ?? "?", what,
                note.Accepted ? Tr("signed") : DealAnswer.CouldNotSign(note) ? Tr("could not be signed") : Tr("declined"),
                DealAnswerModeName(note.Mode),
                note.Counted ? " · " + Simulation.Loc.Format("worth {0} by your foreign ministry's count",
                    note.Benefit.ToString("+0.#;-0.#;0", Simulation.Loc.Culture)) : ""), 13);
            text.CustomMinimumSize = new Vector2(320, 0);
            line.AddChild(text);
            var kind = DealAnswerKinds.Find(note.Kind);
            if (kind is null) continue;
            var change = ActionButton(Simulation.Loc.Format("Change rule: {0}", DealAnswerKindName(kind.Id)),
                () => { _dealRuledOpen = false; _dealAnswersOpen = true; RefreshDealInbox(force: true); }, 0, DealAnswerTouch);
            line.AddChild(change);
        }
    }

    /// <summary>One line for an offer in the list: what the game's count makes of it for the player.</summary>
    private string DealWorthLine(DealOffer paper)
    {
        var player = _campaign.Player;
        if (paper is null || !paper.Pending || paper.ToId != player.Id) return "";
        if (DealAnswer.RecipientGivesLand(paper.Terms)) return Tr("Land: you decide");
        var from = _campaign.NationById(paper.FromId);
        var assessment = Ultimatum.AssessNegotiatedDeal(_campaign, from, player, paper.Terms);
        if (!assessment.Valid) return "";
        var worth = assessment.ToValue.ToString("+0.#;-0.#;0", Simulation.Loc.Culture);
        return assessment.ToValue >= DealAnswer.Margin(_campaign)
            ? Simulation.Loc.Format("Beneficial {0}", worth)
            : Simulation.Loc.Format("Not beneficial {0}", worth);
    }

    /// <summary>The count itself, term by term, under every incoming offer - the rule's own reading.</summary>
    private void AddDealWorth(DealOffer paper, Nation from, Nation to)
    {
        var player = _campaign.Player;
        if (paper is null || to?.Id != player.Id || from is null) return;
        var decision = DealAnswer.Decide(_campaign, player, from, paper.Terms,
            Ultimatum.AssessNegotiatedDeal(_campaign, from, player, paper.Terms));
        var assessment = decision.Assessment;
        if (assessment is not { Valid: true }) return;
        SectionRule(_dealInboxDetail, "BY YOUR FOREIGN MINISTRY'S COUNT");
        var value = assessment.ToValuation;
        var parts = new (string Name, float Points)[]
        {
            ("Money", value.Cash), ("Land", value.Land), ("Treaties", value.Treaties), ("Borders", value.Borders),
            ("Old grievances", -assessment.GrudgePenalty)
        };
        var columns = new HFlowContainer { Name = "DealWorthColumns" };
        columns.AddThemeConstantOverride("h_separation", 24);
        _dealInboxDetail.AddChild(columns);
        foreach (var positive in new[] { true, false })
        {
            var column = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0) };
            columns.AddChild(column);
            column.AddChild(Title(positive ? "FOR" : "AGAINST", 13, Ui.Colour("text.caption")));
            var entries = parts.Where(p => Math.Abs(p.Points) >= .05f && (p.Points > 0) == positive).ToArray();
            if (entries.Length == 0) column.AddChild(WrappedUltimatumText("Nothing", 13));
            foreach (var (name, points) in entries)
            {
                var row = new HBoxContainer();
                column.AddChild(row);
                var label = WrappedUltimatumText(name, 14);
                label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(label);
                row.AddChild(Title(points.ToString("+0.#;-0.#;0", Simulation.Loc.Culture), 14,
                    Ui.Colour(points < 0 ? "state.negative" : "state.positive")));
            }
        }
        var margin = DealAnswer.Margin(_campaign);
        var total = assessment.ToValue;
        _dealInboxDetail.AddChild(Title(Simulation.Loc.Format("Total {0} · a rule signs at {1} or more",
            total.ToString("+0.#;-0.#;0", Simulation.Loc.Culture), margin.ToString("+0.#", Simulation.Loc.Culture)),
            15, Ui.Colour(total >= margin ? "state.positive" : "text.body")));
        var kind = decision.Kind;
        var rule = kind is null
            ? Tr("No standing answer covers this offer: it always waits for you.")
            : DealAnswer.RecipientGivesLand(paper.Terms)
                ? Simulation.Loc.Format("Kind: {0}. No rule gives land away: this offer waits for you.", DealAnswerKindName(kind.Id))
                : Simulation.Loc.Format("Kind: {0}. Your standing answer: {1}.", DealAnswerKindName(kind.Id),
                    DealAnswerModeName(DealAnswer.ModeFor(player, kind)));
        _dealInboxDetail.AddChild(WrappedUltimatumText(rule, 13));
        _dealInboxDetail.AddChild(WrappedUltimatumText("This is the game's own count, not a forecast of profit.", 12));
    }

    /// <summary>Agreement offers on the player's table, in the inbox, each with Sign and Decline.</summary>
    private void AddAgreementInboxRows()
    {
        var player = _campaign.Player;
        foreach (var (proposer, kind) in AgreementSystem.OfferedTo(_campaign, player).OrderBy(o => o.Proposer.Id))
        {
            var column = new VBoxContainer { Name = "AgreementOffer_" + proposer.Id };
            column.AddThemeConstantOverride("separation", 4);
            _dealInboxRows.AddChild(column);
            var name = kind == AgreementKind.TradeAgreement ? Tr("trade agreement") : Tr("non-aggression pact");
            column.AddChild(WrappedUltimatumText(Simulation.Loc.Format("{0} offers a {1}.", proposer.DisplayName, name), 14));
            var deal = DealAnswer.AgreementDeal(kind);
            var assessment = Ultimatum.AssessNegotiatedDeal(_campaign, proposer, player, deal);
            if (assessment.Valid)
            {
                var worth = assessment.ToValue.ToString("+0.#;-0.#;0", Simulation.Loc.Culture);
                column.AddChild(WrappedUltimatumText(assessment.ToValue >= DealAnswer.Margin(_campaign)
                    ? Simulation.Loc.Format("Beneficial {0}", worth) : Simulation.Loc.Format("Not beneficial {0}", worth), 13));
            }
            var relation = CampaignDiplomacy.ReadOrNeutral(_campaign, player.Id, proposer.Id);
            var left = Math.Max(0, _campaign.Defines.AgreementReplyDays - (_campaign.ElapsedDays - relation.AgreementOfferedDay));
            column.AddChild(WrappedUltimatumText(Simulation.Loc.Format("Lapses unanswered in {0} day(s).", left), 12));
            var buttons = new HFlowContainer();
            buttons.AddThemeConstantOverride("h_separation", 8);
            column.AddChild(buttons);
            var sign = ActionButton("Sign", () => AnswerAgreementOffer(proposer, true), 0, DealAnswerTouch, true);
            sign.Name = "SignAgreementOffer_" + proposer.Id;
            buttons.AddChild(sign);
            var decline = ActionButton("Decline", () => AnswerAgreementOffer(proposer, false), 0, DealAnswerTouch);
            decline.Name = "DeclineAgreementOffer_" + proposer.Id;
            buttons.AddChild(decline);
        }
    }

    private void AnswerAgreementOffer(Nation proposer, bool accept)
    {
        if (_campaign?.Player is null || proposer is null) return;
        var answer = new AnswerAgreementCommand { IssuerId = _campaign.Player.Id, TargetId = proposer.Id, Accept = accept };
        _campaign.Commands.Enqueue(answer);
        _campaign.Commands.ApplyAll(_campaign);
        ForceDiplomacyRefresh();
        Refresh();
        ShowDealInboxOutcome(_networkConnected && !_networkIsHost
            ? Tr("Answer sent to the host. Awaiting confirmation.") : answer.Reason);
    }
}
