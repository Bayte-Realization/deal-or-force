using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

// The separate-peace block of the war screen (SeparatePeace): leaving a coalition, splitting the
// other side's, keeping one's own allies in, and who has already left.
public partial class DealOrForceGame
{
    private void BuildSeparatePeaceActions(StrategicWar war, Nation player)
    {
        if (_campaign?.Defines is not { SeparatePeaceEnabled: true } || war is null || war.Ended || !war.Involves(player.Id)) return;
        var attacking = war.IsAttacking(player.Id);
        var leader = SeparatePeace.IsLeader(war, player.Id);
        var enemyMembers = war.EnemiesOf(player.Id).Where(id => !SeparatePeace.IsLeader(war, id)).ToList();
        var ownMembers = war.Side(attacking).Where(id => id != player.Id && !SeparatePeace.IsLeader(war, id)).ToList();
        if (leader && enemyMembers.Count == 0 && ownMembers.Count == 0 && war.SeparatePeaces.Count == 0) return;

        _warDetailActions.AddChild(RowLabel("SEPARATE PEACE", 12, Ui.Colour("text.caption")));

        // ---- our own way out, when we came into somebody else's war.
        if (!leader)
        {
            var refusal = SeparatePeace.Refusal(_campaign, war, player.Id);
            var wish = SeparatePeace.Wish(_campaign, war, player);
            var line = RowLabel(Simulation.Loc.Format("Leave the war on our own. Allies we leave behind will remember it. Our people's wish to be out: {0}%.",
                wish.ToString("0")), 12, Ui.Colour("text.body"));
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _warDetailActions.AddChild(line);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _warDetailActions.AddChild(row);
            foreach (var terms in new[] { SeparatePeaceTerms.WhiteExit, SeparatePeaceTerms.Indemnity, SeparatePeaceTerms.Concede })
                row.AddChild(SeparatePeaceButton(war, player.Id, player.Id, terms, false, refusal));
            row.AddChild(SeparatePeaceButton(war, player.Id, player.Id, SeparatePeaceTerms.WhiteExit, true, refusal));
        }

        // ---- the other side's coalition, one member at a time.
        foreach (var memberId in enemyMembers)
            {
                var member = _campaign.NationById(memberId);
                if (member is null || war.EnemyLeaderOf(memberId) != player.Id) continue;
                var refusal = SeparatePeace.Refusal(_campaign, war, memberId);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                _warDetailActions.AddChild(row);
                var name = RowLabel(Simulation.Loc.Format("{0} · wavering {1}%", member.DisplayName,
                    SeparatePeace.Wish(_campaign, war, member).ToString("0")), 12, Ui.Colour("text.body"), 220f);
                name.TooltipText = Ui.Tip(Tr("How much this member of the other side wants out of the war: a tired public, a losing side, its own land held, a long war, little binding it to its leader."));
                name.MouseFilter = Control.MouseFilterEnum.Stop;
                row.AddChild(name);
                row.AddChild(SeparatePeaceButton(war, player.Id, memberId, SeparatePeaceTerms.WhiteExit, false, refusal, "Let them go"));
                row.AddChild(SeparatePeaceButton(war, player.Id, memberId, SeparatePeaceTerms.Bought, false, refusal, "Pay them to leave"));
            }

        // ---- our own allies: who is wavering, and money to keep them.
        if (leader)
            foreach (var memberId in ownMembers)
            {
                var member = _campaign.NationById(memberId);
                if (member is null) continue;
                var wish = SeparatePeace.Wish(_campaign, war, member);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                _warDetailActions.AddChild(row);
                var shaky = wish >= _campaign.Defines.SeparatePeaceAiThreshold - 15f;
                row.AddChild(RowLabel(Simulation.Loc.Format(shaky ? "{0} · wavering {1}%" : "{0} · steadfast ({1}%)", member.DisplayName, wish.ToString("0")),
                    12, Ui.Colour(shaky ? "state.warning" : "text.body"), 220f));
                var cost = MathF.Max(0.05f, player.Gdp * _campaign.Defines.SeparatePeaceSubsidyShare);
                var captured = memberId;
                var pay = ActionButton(Simulation.Loc.Format("Subsidise · ${0:0.00}B", cost), () => SubsidiseAlly(war, captured), 180, 28);
                pay.TooltipText = Ui.Tip(Tr("Pay this ally to stay in the war: money for its treasury and a little resolve for its public. Once in 90 days."));
                pay.Disabled = player.Treasury < cost ||
                               member.DecisionAvailableDay.GetValueOrDefault("separate_peace_subsidy/" + war.Id) > _campaign.ElapsedDays;
                row.AddChild(pay);
            }

        // ---- who has already left.
        foreach (var left in war.SeparatePeaces)
        {
            var who = _campaign.NationById(left.NationId)?.DisplayName ?? "?";
            var with = _campaign.NationById(left.WithId)?.DisplayName ?? "?";
            var text = left.SwitchedSides
                ? Simulation.Loc.Format("{0} changed sides on {1}.", who, LandArrangementSystem.DateOfDay(_campaign, left.Day))
                : Simulation.Loc.Format("{0} made a separate peace with {1} on {2}: {3}.", who, with,
                    LandArrangementSystem.DateOfDay(_campaign, left.Day), SeparatePeace.TermsName(left.Terms));
            var label = RowLabel(text, 11, Ui.Colour("text.caption"));
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _warDetailActions.AddChild(label);
        }
    }

    private Button SeparatePeaceButton(StrategicWar war, int issuerId, int memberId, SeparatePeaceTerms terms, bool switchSides,
        string refusal, string caption = null)
    {
        var text = caption ?? (switchSides ? Tr("Change sides") : SeparatePeace.TermsName(terms));
        var button = ActionButton(text, () => SignSeparatePeace(war, issuerId, memberId, terms, switchSides), 190, 30);
        var acceptance = SeparatePeace.Acceptance(_campaign, war, memberId, terms);
        button.Disabled = refusal.Length > 0;
        var tip = refusal.Length > 0
            ? refusal
            : SeparatePeace.TermsSentence(terms) + "\n" + Simulation.Loc.Format("Acceptance {0}%.", acceptance.ToString("0"));
        if (switchSides)
            tip += "\n" + Tr("We leave this war and join the other side of it at once. The allies we leave behind become our enemies, and will not forget it.");
        button.TooltipText = Ui.Tip(tip);
        return button;
    }

    private void SignSeparatePeace(StrategicWar war, int issuerId, int memberId, SeparatePeaceTerms terms, bool switchSides)
    {
        if (_campaign?.Player is null || war is null) return;
        var wasIn = war.Involves(_campaign.Player.Id);
        _campaign.Commands.Enqueue(new SeparatePeaceCommand
            { IssuerId = issuerId, WarId = war.Id, MemberId = memberId, Terms = terms, SwitchSides = switchSides });
        _campaign.Commands.ApplyAll(_campaign);
        if (wasIn && !war.Involves(_campaign.Player.Id)) PlayStrategicCue("peace_signed");
        _warDetailBuiltFor = -1;
        _warDetailDay = int.MinValue;
        RefreshWarLedger();
    }

    private void SubsidiseAlly(StrategicWar war, int memberId)
    {
        if (_campaign?.Player is null || war is null) return;
        _campaign.Commands.Enqueue(new SubsidiseAllyCommand { IssuerId = _campaign.Player.Id, WarId = war.Id, MemberId = memberId });
        _campaign.Commands.ApplyAll(_campaign);
        _warDetailBuiltFor = -1;
        _warDetailDay = int.MinValue;
        RefreshWarLedger();
    }
}
