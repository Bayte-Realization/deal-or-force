using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    // One row per act: the act, what it costs, whether it can be asked now, and the button that
    // asks. The rows are held so a refresh can repaint the edge and the meta line without
    // rebuilding the diplomacy screen under the pointer.
    private readonly Dictionary<DiplomaticAct, Button> _diplomaticActRows = new();
    private readonly Dictionary<DiplomaticAct, Button> _countryDiplomaticActRows = new();
    private VBoxContainer _countryCooperationSection;

    private void AddDiplomaticActButtons()
    {
        if (_diplomacyActionRows is null) return;
        _diplomaticActRows.Clear();
        foreach (var act in DiplomaticActs.All)
        {
            var captured = act;
            // Same geometry, search and keyboard focus as Agreements/Influence, not a
            // second card-within-a-card design with an unexplained tiny Propose button.
            var button = AddDiplomacyAction("COOPERATION", DiplomaticActs.Name(act),
                () => ProposeDiplomaticAct(captured));
            button.Name = "DiplomaticAct_" + act;
            button.SetMeta("diplomacy_search_name", DiplomaticActs.Name(act));
            _diplomaticActRows[act] = button;
        }
    }

    private void AddCountryDiplomaticActButtons(VBoxContainer parent)
    {
        _countryCooperationSection = new VBoxContainer
        {
            Name = "CountryCooperation", SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _countryCooperationSection.AddThemeConstantOverride("separation", 5);
        parent.AddChild(_countryCooperationSection);
        _countryCooperationSection.AddChild(Title("COOPERATION", 12, Ui.Colour("text.caption")));
        _countryDiplomaticActRows.Clear();
        foreach (var act in DiplomaticActs.All)
        {
            var captured = act;
            var button = ActionButton(DiplomaticActs.Name(act),
                () => ProposeDiplomaticAct(captured, _selectedNation), 0, 34);
            button.Name = "CountryDiplomaticAct_" + act;
            button.ClipText = true;
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _countryCooperationSection.AddChild(button);
            _countryDiplomaticActRows[act] = button;
        }
    }

    private void RefreshDiplomaticActButtons(Nation target)
        => RefreshDiplomaticActRows(_diplomaticActRows, target);

    private void RefreshCountryDiplomaticActButtons(Nation target)
    {
        var valid = RefreshDiplomaticActRows(_countryDiplomaticActRows, target);
        if (IsInstanceValid(_countryCooperationSection)) _countryCooperationSection.Visible = valid;
    }

    private bool RefreshDiplomaticActRows(Dictionary<DiplomaticAct, Button> rows, Nation target)
    {
        var player = _campaign?.Player;
        var valid = player is { IsHuman: true } && target is not null && target != player && !_observerMode;
        foreach (var (act, row) in rows)
        {
            if (!GodotObject.IsInstanceValid(row)) continue;
            row.Visible = valid;
            if (!valid) continue;
            var refusal = DiplomaticActs.Refusal(_campaign, player, target, act);
            var details = refusal.Length > 0
                ? Tr(refusal)
                : Loc.Format("{0} from the treasury", Fmt.Money(DiplomaticActs.Cost(_campaign, act)));
            row.Disabled = refusal.Length > 0;
            row.TooltipText = Ui.Tip(Tr(DiplomaticActs.Name(act)) + "\n" + details + "\n" +
                Tr("A human government signs before anything changes. Land is not on the paper."));
        }
        return valid;
    }

    private void ProposeDiplomaticAct(DiplomaticAct act)
        => ProposeDiplomaticAct(act, DiplomacyTarget());

    private void ProposeDiplomaticAct(DiplomaticAct act, Nation target)
    {
        var player = _campaign?.Player;
        if (player is not { IsHuman: true } || target is null || target == player || _observerMode) return;
        var command = new DiplomaticActCommand
        {
            IssuerId = player.Id, TargetId = target.Id, Act = act, Step = DiplomaticActStep.Propose
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        if (IsInstanceValid(_diplomacyFeedback)) _diplomacyFeedback.Text = Tr(command.Result);
        if (IsInstanceValid(_mapDiplomacyFeedback)) _mapDiplomacyFeedback.Text = Tr(command.Result);
        if (IsInstanceValid(_countryActionFeedback)) _countryActionFeedback.Text = Tr(command.Result);
        RefreshDiplomaticActionViews();
    }

    private void AddDiplomaticActInboxRows()
    {
        var host = _campaign.Player;
        foreach (var relation in _campaign.Relations.Values)
        {
            if ((relation.A != host.Id && relation.B != host.Id) || !DiplomaticActs.Pending(_campaign, relation) ||
                relation.DiplomaticActBy == host.Id) continue;
            var asker = _campaign.NationById(relation.DiplomaticActBy);
            if (asker is null || relation.DiplomaticActKind < 0) continue;
            var act = (DiplomaticAct)relation.DiplomaticActKind;
            var column = new VBoxContainer { Name = "DiplomaticActLetter_" + asker.Id };
            _dealInboxRows.AddChild(column);
            var line = WrappedUltimatumText(Loc.Format("{0} proposes {1}. Nothing is signed until you do. No land changes hands.",
                asker.DisplayName, Tr(DiplomaticActs.Name(act))), 14);
            line.AutowrapMode = TextServer.AutowrapMode.Off;
            line.ClipText = true;
            line.CustomMinimumSize = new Vector2(0, 22);
            column.AddChild(line);
            var accept = ActionButton("Sign", () => AnswerDiplomaticAct(asker, DiplomaticActStep.Accept), 0, 32);
            accept.ClipText = true;
            accept.AutowrapMode = TextServer.AutowrapMode.Off;
            column.AddChild(accept);
            var decline = ActionButton("Decline", () => AnswerDiplomaticAct(asker, DiplomaticActStep.Decline), 0, 32);
            decline.ClipText = true;
            decline.AutowrapMode = TextServer.AutowrapMode.Off;
            column.AddChild(decline);
        }
    }

    private void AnswerDiplomaticAct(Nation asker, DiplomaticActStep step)
    {
        if (_campaign?.Player is null || asker is null) return;
        var command = new DiplomaticActCommand
        {
            IssuerId = _campaign.Player.Id, TargetId = asker.Id, Step = step
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _dealInboxMessage = Tr(command.Result);
        RefreshDealInbox(force: true);
    }
}
