using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public readonly record struct WarCallAssessment(bool Allowed, bool WouldJoin, float Score, string Reason);
public sealed record WarSupportCandidate(int NationId, bool Defending, bool Automatic, bool CanInvite,
    bool ExpectedJoin, bool HumanDecision, string Role, string Reason, float Willingness);
public sealed record WarDeclarationPreview(bool Allowed, string Refusal, string Cause,
    IReadOnlyList<WarSupportCandidate> Support);

/// <summary>Read-only cabinet briefing and bounded, revalidated opening invitations.
/// No random rolls, temporary wars in the live list, extra armies or hidden automatic declarations.</summary>
public static class WarDeclarationPlanning
{
    public static WarDeclarationPreview Preview(Campaign c, int attackerId, int defenderId)
    {
        var refusal = "";
        if (c is null || !CampaignDiplomacy.CanDeclareWar(c, attackerId, defenderId, out refusal))
            return new(false, c is null ? Loc.Get("Invalid target.") : refusal, "", Array.Empty<WarSupportCandidate>());
        var attacker = c.NationById(attackerId); var defender = c.NationById(defenderId);
        var cause = Statecraft.CasusBelli(c, attacker, defender);
        var war = new StrategicWar { Id = -1, AttackerId = attackerId, DefenderId = defenderId,
            CivilConflict = cause is "secession" or "uprising" or "civil_war" or "revolution" ||
                Reintegration.IsBreakawayOf(attacker, defender) || Reintegration.IsBreakawayOf(defender, attacker) };
        var support = new List<WarSupportCandidate>();
        foreach (var defending in new[] { false, true })
        {
            var caller = defending ? defender : attacker; var enemy = defending ? attacker : defender;
            var formal = (defending ? Statecraft.DefensiveCallIds(c, war) : Statecraft.CallableAllies(c, war, caller.Id)).ToHashSet();
            var coalition = defending && !war.CivilConflict ? Statecraft.CoalitionAgainst(c, attackerId) : null;
            if (coalition is not null) formal.UnionWith(coalition.MemberIds);
            if (defending && defender.IsSubject && SubjectCatalog.Of(defender.SubjectType) is { OverlordDefends: true })
                formal.Add(defender.OverlordId);
            foreach (var nation in c.Nations.OrderBy(n => n.Id))
            {
                if (nation.Id == caller.Id || nation.Id == enemy.Id || !c.HasOwnedLand(nation) || nation.Landless) continue;
                var bond = CampaignDiplomacy.Peek(c, nation.Id, caller.Id);
                var forced = !defending && nation.AnswersTo(caller.Id) &&
                    SubjectCatalog.Of(nation.SubjectType) is { DraggedIntoWars: true } ||
                    defending && caller.AnswersTo(nation.Id) &&
                    SubjectCatalog.Of(caller.SubjectType) is { OverlordDefends: true };
                if (war.CivilConflict && !CampaignDiplomacy.MayJoinCivilWar(c, war, nation, defending)) continue;
                if (formal.Contains(nation.Id))
                {
                    var assessment = Statecraft.AssessCall(c, nation, caller, enemy,
                        bond ?? new DiplomaticRelation { A = nation.Id, B = caller.Id }, favour: !defending);
                    var role = forced ? "Subject obligation" : bond?.Stance == DiplomaticStance.Allied ? "Ally"
                        : bond?.GuaranteedBy(nation.Id) == true ? "Guarantor"
                        : coalition?.MemberIds.Contains(nation.Id) == true ? "Containment coalition" : "Collective defence";
                    support.Add(new(nation.Id, defending, (defending || forced) && !nation.IsHuman,
                        !defending && (!forced || nation.IsHuman) && assessment.Allowed,
                        !nation.IsHuman && (forced || assessment.WouldJoin), nation.IsHuman, role,
                        forced && !nation.IsHuman ? "Enters under the existing subject treaty." : assessment.Reason, assessment.Score));
                }
                else
                {
                    // A rival on the border can have a real intervention pretext without
                    // being allied. A large/wealthy country alone is not a candidate.
                    var interest = WarInterventions.AssessForWar(c, caller.Id, nation.Id, war);
                    if (!interest.Allowed) continue;
                    support.Add(new(nation.Id, defending, false, !defending, interest.AiWouldGrant,
                        nation.IsHuman, "Potential intervention", interest.Pretext, interest.Willingness));
                }
            }
        }
        return new(true, "", cause, support);
    }

    public static void InviteSelected(Campaign c, StrategicWar war, int callerId, IEnumerable<int> invited)
    {
        if (c is null || war is null || war.Ended || !war.Involves(callerId)) return;
        foreach (var id in (invited ?? Array.Empty<int>()).Distinct().OrderBy(id => id).Take(64))
        {
            if (war.Involves(id)) continue;
            var nation = c.NationById(id);
            if (nation is null || !c.HasOwnedLand(nation) || nation.Landless) continue;
            if (!nation.IsHuman && Statecraft.CallableAllies(c, war, callerId).Contains(id))
                Statecraft.AskAllyToJoin(c, war, callerId, id, out _);
            else
                WarInterventions.Submit(c, callerId, id, war.Id);
        }
    }

    public static void InviteAiAllies(Campaign c, StrategicWar war)
    {
        if (war.CivilConflict || war.Limited) return;
        var caller = c.NationById(war.AttackerId); var enemy = c.NationById(war.DefenderId);
        if (caller is null || enemy is null || caller.IsHuman) return;
        var invitations = Statecraft.CallableAllies(c, war, caller.Id).Where(id =>
        {
            var friend = c.NationById(id);
            return friend is not null && (friend.IsHuman || Statecraft.AssessCall(c, friend, caller, enemy,
                CampaignDiplomacy.Peek(c, id, caller.Id) ?? new DiplomaticRelation(), favour: true).WouldJoin);
        }).Take(8).ToArray();
        InviteSelected(c, war, caller.Id, invitations);
    }
}
