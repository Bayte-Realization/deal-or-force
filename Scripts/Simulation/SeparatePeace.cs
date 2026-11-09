using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>What a country that leaves a coalition war early leaves behind it.</summary>
public enum SeparatePeaceTerms
{
    /// <summary>It goes home, and every province between it and the other side goes back to its owner.</summary>
    WhiteExit,
    /// <summary>The other side keeps what it holds of the leaver - the peace of Brest-Litovsk.</summary>
    Concede,
    /// <summary>The leaver pays the other side's leader to let it go.</summary>
    Indemnity,
    /// <summary>The other side pays the leaver to go: a coalition split with money.</summary>
    Bought
}

/// <summary>One member that left a war before it ended, and on what terms.</summary>
public sealed class SeparatePeaceRecord
{
    public int NationId;
    public int WithId;
    public int Day;
    public SeparatePeaceTerms Terms;
    public bool SwitchedSides;
    public float Payment;
    public int ProvincesCeded;
}

public sealed partial class StrategicWar
{
    /// <summary>
    /// Members that made their own peace and left while the war went on (SeparatePeace). Public
    /// readonly, like the allies, because the save populates rather than replaces.
    /// </summary>
    public readonly List<SeparatePeaceRecord> SeparatePeaces = new();
}

public sealed partial class Defines
{
    public bool SeparatePeaceEnabled { get; set; } = true;
    /// <summary>Days a war must have lasted before a member may leave it on its own.</summary>
    public int SeparatePeaceMinimumDays { get; set; } = 60;
    public int SeparatePeaceTruceDays { get; set; } = 1825;
    /// <summary>How badly a member must want out (0-100, SeparatePeace.Wish) before a government nobody plays asks.</summary>
    public float SeparatePeaceAiThreshold { get; set; } = 50f;
    /// <summary>And past this, with its side losing and the other side closer to it, it changes sides.</summary>
    public float SeparatePeaceSwitchThreshold { get; set; } = 82f;
    /// <summary>Share of the payer's GDP an indemnity or a bribe costs.</summary>
    public float SeparatePeaceIndemnityShare { get; set; } = .02f;
    public float SeparatePeaceBribeShare { get; set; } = .015f;
    /// <summary>The side it left loses this much war support for every whole of its own strength that left.</summary>
    public float SeparatePeaceCoalitionMorale { get; set; } = .25f;
    /// <summary>A war-weary country is glad of the peace; a resolved one resents it.</summary>
    public float SeparatePeaceStabilityShift { get; set; } = .04f;
    /// <summary>Share of GDP a war leader pays a wavering ally to stay, and the support it buys.</summary>
    public float SeparatePeaceSubsidyShare { get; set; } = .004f;
    public float SeparatePeaceSubsidySupport { get; set; } = .06f;
    public int SeparatePeaceCooldownDays { get; set; } = 120;
}

/// <summary>
/// Leaving a coalition war before it ends.
///
/// A war joined through an alliance used to hold every member until the leaders settled it:
/// Romania in a war Russia was losing had no way out but Russia's peace. Now a member - never a
/// leader, whose peace is the war's own (PeaceNegotiation) - can make its own with the other
/// side, on four kinds of terms, and the war goes on without it. What follows is the politics of
/// it: the allies left behind remember (`separate_peace_betrayal`), the alliance breaks, a defence
/// pact loses cohesion, the coalition's war support drops by the share of its strength that left,
/// and a weary home front is glad. The other side can buy a member out; a leader can pay a
/// wavering ally to stay; and a member losing badly beside a coalition it no longer believes in
/// can change sides. One path for players and for governments nobody plays (AI parity).
/// </summary>
public static class SeparatePeace
{
    public static bool IsLeader(StrategicWar war, int nationId) => war.AttackerId == nationId || war.DefenderId == nationId;

    /// <summary>Why this member cannot leave this war on its own, or an empty string when it can.</summary>
    public static string Refusal(Campaign c, StrategicWar war, int memberId)
    {
        if (c?.Defines is not { SeparatePeaceEnabled: true } d) return Loc.Get("Separate peace is switched off.");
        if (war is null || war.Ended) return Loc.Get("That war is over.");
        var member = c.NationById(memberId);
        if (member is null || !war.Involves(memberId)) return Loc.Get("Not in that war.");
        if (IsLeader(war, memberId)) return Loc.Get("A war's leader ends it with a peace for its whole side.");
        if (c.ElapsedDays - war.StartedDay < Math.Max(0, d.SeparatePeaceMinimumDays))
            return Loc.Format("The war has not lasted {0} days", d.SeparatePeaceMinimumDays);
        // A dependency that came on its overlord's orders goes home on them too.
        if (member.IsSubject && war.Involves(member.OverlordId) && !war.Opposes(member.Id, member.OverlordId) &&
            SubjectCatalog.Of(member.SubjectType) is { } kind && (kind.DraggedIntoWars || kind.JoinsWars))
            return Loc.Format("It answers to {0}, who is still fighting.", c.NationById(member.OverlordId)?.DisplayName ?? "?");
        return "";
    }

    /// <summary>How much a member's own strength is of its side's (0-1): what leaving takes away.</summary>
    public static float Weight(Campaign c, StrategicWar war, int memberId)
    {
        var attacking = war.IsAttacking(memberId);
        var side = new HashSet<int>(war.Side(attacking));
        float own = 0f, total = 0f;
        foreach (var army in c.Armies)
        {
            if (!side.Contains(army.NationId)) continue;
            total += army.Divisions;
            if (army.NationId == memberId) own += army.Divisions;
        }
        if (total <= 0f)
        {
            long people = 0, ownPeople = 0;
            foreach (var id in side) people += c.NationById(id)?.Population ?? 0;
            ownPeople = c.NationById(memberId)?.Population ?? 0;
            return people <= 0 ? 0f : Math.Clamp(ownPeople / (float)people, 0f, 1f);
        }
        return Math.Clamp(own / total, 0f, 1f);
    }

    /// <summary>Share of the member's own provinces the other side holds.</summary>
    public static float OccupiedShare(Campaign c, StrategicWar war, Nation member)
    {
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        if (map is null || controls is null || member.MapIndex <= 0) return 0f;
        var own = 0;
        var held = 0;
        var byMap = c.NationsByMapIndex();
        for (var p = 1; p <= map.ProvinceCount && p < controls.Length; p++)
        {
            if (map.ProvinceToNation[p] != member.MapIndex) continue;
            own++;
            var controller = controls[p];
            if (controller != member.MapIndex && byMap.TryGetValue(controller, out var holder) && war.Opposes(member.Id, holder.Id)) held++;
        }
        return own == 0 ? 0f : held / (float)own;
    }

    /// <summary>
    /// How badly a member wants out of the war, 0-100: a tired public, a losing side, its own land
    /// under the enemy, a long war, and little left binding it to the leader it came in behind.
    /// </summary>
    public static float Wish(Campaign c, StrategicWar war, Nation member)
    {
        if (member is null || war is null || war.Ended || !war.Involves(member.Id) || IsLeader(war, member.Id)) return 0f;
        var attacking = war.IsAttacking(member.Id);
        var sideScore = attacking ? war.WarScore : -war.WarScore;
        var leaderId = attacking ? war.AttackerId : war.DefenderId;
        var bond = CampaignDiplomacy.Peek(c, member.Id, leaderId);
        var years = (c.ElapsedDays - war.StartedDay) / 365f;
        var wish = (1f - Math.Clamp(member.WarSupport, 0f, 1f)) * 40f
                 + Math.Clamp(-sideScore * .5f, 0f, 30f)
                 + Math.Clamp(OccupiedShare(c, war, member) * 60f, 0f, 30f)
                 + Math.Clamp(years * 5f, 0f, 15f)
                 + WarWeariness.Weariness(c, war) * .2f
                 - Math.Clamp((bond?.Opinion ?? 0f) / 4f, -10f, 25f);
        // Somebody else's war far from home: no border with any enemy and none of its own ground lost.
        if (!war.EnemiesOf(member.Id).Any(enemy => c.NationById(enemy) is { } e &&
                CampaignDiplomacy.SharesLandBorder(c, member.MapIndex, e.MapIndex)) && OccupiedShare(c, war, member) <= 0f)
            wish += 12f;
        // And a war it can no longer pay for.
        if (member.Treasury < 0f) wish += 10f;
        if (bond?.Stance == DiplomaticStance.Allied) wish -= 10f;
        if (InternationalOrganizations.DefencePartners(c, member.Id).Contains(leaderId)) wish -= 8f;
        // A war the member is fighting for its own ground is not somebody else's war.
        if (war.ClaimedStateMapIndex >= 0 && c.States.Find(s => s.MapIndex == war.ClaimedStateMapIndex) is { } goal &&
            goal.NationMapIndex == member.MapIndex) wish -= 25f;
        return Math.Clamp(wish, 0f, 100f);
    }

    /// <summary>How willing the other side's leader is to let this member go on these terms, 0-100.</summary>
    public static float Acceptance(Campaign c, StrategicWar war, int memberId, SeparatePeaceTerms terms)
    {
        if (Refusal(c, war, memberId).Length > 0) return 0f;
        var member = c.NationById(memberId);
        var enemyLeader = c.NationById(war.EnemyLeaderOf(memberId));
        if (member is null || enemyLeader is null) return 0f;
        var holds = OccupiedShare(c, war, member);
        // Splitting a coalition is worth something in itself, the more the stronger the member.
        var value = 40f + Weight(c, war, memberId) * 60f + terms switch
        {
            SeparatePeaceTerms.Concede => holds > 0.001f ? 25f + holds * 30f : 2f,
            SeparatePeaceTerms.Indemnity => 15f,
            SeparatePeaceTerms.Bought => -15f,
            _ => 0f
        };
        // Letting go an enemy that holds nothing of ours and has nothing of ours to return costs
        // nothing: an island state in a world war on the far side of the planet is waved out.
        if (terms != SeparatePeaceTerms.Bought && !HoldsEnemyGround(c, war, member)) value += 15f;
        // A war the other side started to take this member's ground is not given up for nothing.
        if (war.ClaimedStateMapIndex >= 0 && c.States.Find(s => s.MapIndex == war.ClaimedStateMapIndex) is { } goal &&
            goal.NationMapIndex == member.MapIndex && terms != SeparatePeaceTerms.Concede) value -= 30f;
        var relation = CampaignDiplomacy.Peek(c, memberId, enemyLeader.Id);
        value += Math.Clamp((relation?.Opinion ?? 0f) / 5f, -10f, 10f);
        value = Math.Clamp(value, 0f, 100f);
        return enemyLeader.IsHuman ? value : AiTemper.Accepting(c, value);
    }

    /// <summary>Whether the member's armies hold any ground of the side it is fighting in this war.</summary>
    public static bool HoldsEnemyGround(Campaign c, StrategicWar war, Nation member)
    {
        var controls = c.ProvinceController;
        if (controls is null) return false;
        foreach (var province in war.OriginalController.Keys)
            if (province > 0 && province < controls.Length && controls[province] == member.MapIndex) return true;
        return false;
    }

    /// <summary>What a member gets out of these terms on top of wanting out, for a member nobody plays deciding an offer.</summary>
    public static float MemberGain(SeparatePeaceTerms terms) => terms switch
    {
        SeparatePeaceTerms.Bought => 20f,
        SeparatePeaceTerms.WhiteExit => 5f,
        SeparatePeaceTerms.Indemnity => -10f,
        _ => -20f
    };

    /// <summary>
    /// The member leaves the war on these terms; with <paramref name="switchSides"/> it joins the other
    /// side of the same war straight away.
    /// </summary>
    public static bool Sign(Campaign c, StrategicWar war, int memberId, SeparatePeaceTerms terms, bool switchSides = false)
    {
        if (Refusal(c, war, memberId).Length > 0) return false;
        var d = c.Defines;
        var member = c.NationById(memberId);
        var attacking = war.IsAttacking(memberId);
        var enemyLeader = c.NationById(war.EnemyLeaderOf(memberId));
        var formerLeaderId = attacking ? war.AttackerId : war.DefenderId;
        if (member is null || enemyLeader is null) return false;
        var enemies = war.EnemiesOf(memberId).ToArray();
        var allies = war.Side(attacking).Where(id => id != memberId).ToArray();
        var weight = Weight(c, war, memberId);
        var record = new SeparatePeaceRecord
            { NationId = memberId, WithId = enemyLeader.Id, Day = c.ElapsedDays, Terms = terms, SwitchedSides = switchSides };

        // ---- the ground between it and the other side.
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        if (map is not null && controls is not null)
        {
            var byMap = c.NationsByMapIndex();
            var kept = new Dictionary<int, List<int>>();
            foreach (var (province, was) in war.OriginalController.ToArray())
            {
                if (province <= 0 || province >= controls.Length) continue;
                var owner = map.ProvinceToNation[province];
                var controller = controls[province];
                byMap.TryGetValue(controller, out var holder);
                byMap.TryGetValue(owner, out var ownerNation);
                var leaverHolds = controller == member.MapIndex && ownerNation is not null && war.Opposes(memberId, ownerNation.Id);
                var leaverLand = owner == member.MapIndex && controller != member.MapIndex;
                if (!leaverHolds && !leaverLand) continue;
                if (leaverLand && terms == SeparatePeaceTerms.Concede && holder is not null && war.Opposes(memberId, holder.Id))
                {
                    if (!kept.TryGetValue(holder.Id, out var list)) kept[holder.Id] = list = new List<int>();
                    list.Add(province);
                    continue;
                }
                c.SetProvinceController(province, was);
                war.OriginalController.Remove(province);
            }
            foreach (var (holderId, held) in kept)
            {
                var holder = c.NationById(holderId);
                var tidied = PeaceShape.SettleProvinces(c, war, PeaceConference.Tidy(c, war, held, holder.MapIndex), holder);
                foreach (var province in held)
                {
                    if (!tidied.Contains(province) && war.OriginalController.TryGetValue(province, out var was))
                        c.SetProvinceController(province, was);
                    war.OriginalController.Remove(province);
                }
                if (tidied.Count == 0) continue;
                StatePartition.Annex(c, tidied, holder);
                record.ProvincesCeded += tidied.Count;
            }
        }

        // ---- money.
        if (terms is SeparatePeaceTerms.Indemnity or SeparatePeaceTerms.Bought)
        {
            var payer = terms == SeparatePeaceTerms.Indemnity ? member : enemyLeader;
            var receiver = terms == SeparatePeaceTerms.Indemnity ? enemyLeader : member;
            var share = terms == SeparatePeaceTerms.Indemnity ? d.SeparatePeaceIndemnityShare : d.SeparatePeaceBribeShare;
            var payment = MathF.Min(MathF.Max(0f, payer.Treasury), MathF.Max(0.05f, payer.Gdp * share));
            payer.Treasury -= payment;
            receiver.Treasury += payment;
            record.Payment = payment;
        }

        // ---- out of the war.
        war.AttackerAllies.Remove(memberId);
        war.DefenderAllies.Remove(memberId);
        war.SeparatePeaces.Add(record);
        var truceUntil = c.ElapsedDays + Math.Max(30, d.SeparatePeaceTruceDays);
        foreach (var enemyId in enemies)
        {
            if (enemyId == memberId) continue;
            var relation = CampaignDiplomacy.Get(c, memberId, enemyId);
            relation.Stance = DiplomaticStance.Truce;
            relation.TruceUntil = truceUntil;
            relation.LastActionDay = c.ElapsedDays;
            relation.Opinion = Math.Min(relation.Opinion, -10f);
            OpinionSystem.Remember(c, relation, "left_the_war_against_us");
        }

        // ---- the allies it left.
        foreach (var allyId in allies)
        {
            var bond = CampaignDiplomacy.Get(c, memberId, allyId);
            OpinionSystem.Remember(c, bond, switchSides ? "switched_sides_against_us" : "separate_peace_betrayal");
            if (bond.Stance == DiplomaticStance.Allied)
            {
                bond.Stance = DiplomaticStance.Neutral;
                OpinionSystem.Forget(c, bond, "alliance");
            }
            if (bond.GuaranteedBy(memberId))
            {
                if (memberId == bond.A) bond.GuaranteeByA = false; else bond.GuaranteeByB = false;
                OpinionSystem.Forget(c, bond, "guaranteed_us");
            }
            bond.LastActionDay = c.ElapsedDays;
        }
        foreach (var org in InternationalOrganizations.Memberships(c, memberId))
            if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true && allies.Any(org.Members.Contains))
                org.Cohesion = Math.Max(0f, org.Cohesion - .06f);
        if (attacking) war.AttackerWarSupport = Math.Clamp(war.AttackerWarSupport - weight * d.SeparatePeaceCoalitionMorale, 0f, 1f);
        else war.DefenderWarSupport = Math.Clamp(war.DefenderWarSupport - weight * d.SeparatePeaceCoalitionMorale, 0f, 1f);
        // A home front that wanted out is glad of it; one that still believed resents it.
        member.Stability = Math.Clamp(member.Stability + (member.WarSupport < .5f ? d.SeparatePeaceStabilityShift : -d.SeparatePeaceStabilityShift * .5f), 0f, 1f);

        // ---- its armies leave the war; those standing on ground they may no longer be on go home (MilitaryAccess.Tick).
        var released = new List<Army>();
        var leavingArmies = new HashSet<int>();
        foreach (var army in c.Armies)
        {
            if (army.NationId != memberId) continue;
            leavingArmies.Add(army.Id);
            if (army.AssignedWarId != war.Id) continue;
            army.AssignedWarId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.MoveProgress = 0f;
            released.Add(army);
        }
        foreach (var battle in c.Battles.Where(b => b.WarId == war.Id && !b.Ended).ToArray())
        {
            battle.Attackers.RemoveAll(leavingArmies.Contains);
            battle.Defenders.RemoveAll(leavingArmies.Contains);
            foreach (var army in c.Armies)
                if (leavingArmies.Contains(army.Id) && army.BattleId == battle.Id) army.BattleId = -1;
            if (battle.Attackers.Count == 0 || battle.Defenders.Count == 0)
                Warfare.EndBattleWithoutVictory(c, battle, "Separate peace");
        }
        Warfare.RebuildFronts(c);
        FrontPlanner.ReassignAi(c, war);
        BorderFronts.ReturnToWholeFronts(c, released);

        c.Chronicle.Record(c, switchSides ? "switched_sides" : "separate_peace", memberId, enemyLeader.Id,
            subject: c.NationById(formerLeaderId)?.DisplayName ?? "");
        if (switchSides)
            CampaignDiplomacy.JoinWar(c, war, memberId, defending: attacking, "switched_sides_against_us");
        c.MarkWarsChanged();
        c.MarkStrategicChange();
        c.MarkMapChanged();
        return true;
    }

    /// <summary>A war leader pays a wavering ally to stay in: cash for its treasury, a little resolve for its public.</summary>
    public static string Subsidise(Campaign c, StrategicWar war, int payerId, int memberId)
    {
        if (war is null || war.Ended || !war.Involves(payerId) || !war.Involves(memberId) || war.Opposes(payerId, memberId) || payerId == memberId)
            return Loc.Get("Not allies in that war.");
        var payer = c.NationById(payerId);
        var member = c.NationById(memberId);
        var key = "separate_peace_subsidy/" + war.Id;
        if (member.DecisionAvailableDay.GetValueOrDefault(key) > c.ElapsedDays) return Loc.Get("Paid recently.");
        var amount = MathF.Max(0.05f, payer.Gdp * c.Defines.SeparatePeaceSubsidyShare);
        if (payer.Treasury < amount) return Loc.Get("Not enough money in the treasury.");
        payer.Treasury -= amount;
        member.Treasury += amount;
        member.WarSupport = Math.Clamp(member.WarSupport + c.Defines.SeparatePeaceSubsidySupport, 0f, 1f);
        member.DecisionAvailableDay[key] = c.ElapsedDays + 90;
        OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, payerId, memberId), "kept_us_in_the_war");
        c.Chronicle.Record(c, "war_subsidy", payerId, memberId, isPublic: false);
        c.MarkStrategicChange();
        return "";
    }

    // ------------------------------------------------------------------ governments nobody plays

    public static void Tick(Campaign c)
    {
        if (c?.Defines is not { SeparatePeaceEnabled: true } d || !c.StepOpensNewDay) return;
        foreach (var war in c.Wars.ToArray())
        {
            if (war.Ended || war.AttackerAllies.Count + war.DefenderAllies.Count == 0) continue;
            // Weekly, each war on its own day.
            if ((c.ElapsedDays + war.Id) % 7 != 0) continue;
            if (c.ElapsedDays - war.StartedDay < Math.Max(0, d.SeparatePeaceMinimumDays)) continue;
            foreach (var memberId in war.AttackerAllies.Concat(war.DefenderAllies).OrderBy(id => id).ToArray())
            {
                if (war.Ended || !war.Involves(memberId)) continue;
                var member = c.NationById(memberId);
                if (member is null || Refusal(c, war, memberId).Length > 0) continue;
                var wish = Wish(c, war, member);
                var attacking = war.IsAttacking(memberId);
                var leader = c.NationById(attacking ? war.AttackerId : war.DefenderId);
                var enemyLeader = c.NationById(war.EnemyLeaderOf(memberId));
                if (leader is null || enemyLeader is null) continue;
                var cooldownKey = "separate_peace/" + war.Id;

                // The leader holds a wavering ally with money before it goes.
                if (!leader.IsHuman && wish >= d.SeparatePeaceAiThreshold - 15f && leader.Treasury > leader.Gdp * .02f)
                    Subsidise(c, war, leader.Id, memberId);

                if (member.DecisionAvailableDay.GetValueOrDefault(cooldownKey) > c.ElapsedDays) continue;

                // The other side buys out a member that is close to going anyway.
                if (!enemyLeader.IsHuman && wish >= d.SeparatePeaceAiThreshold - 20f &&
                    enemyLeader.Treasury > enemyLeader.Gdp * d.SeparatePeaceBribeShare * 2f &&
                    Acceptance(c, war, memberId, SeparatePeaceTerms.Bought) >= 50f)
                {
                    member.DecisionAvailableDay[cooldownKey] = c.ElapsedDays + d.SeparatePeaceCooldownDays;
                    if (member.IsHuman && !c.NobodyPlays) { Offer(c, war, member, enemyLeader, SeparatePeaceTerms.Bought, fromMember: false); continue; }
                    if (wish + MemberGain(SeparatePeaceTerms.Bought) >= d.SeparatePeaceAiThreshold)
                    { Sign(c, war, memberId, SeparatePeaceTerms.Bought); continue; }
                }

                if (member.IsHuman && !c.NobodyPlays) continue;
                wish = Wish(c, war, member);
                if (wish < d.SeparatePeaceAiThreshold) continue;
                member.DecisionAvailableDay[cooldownKey] = c.ElapsedDays + d.SeparatePeaceCooldownDays;

                // Changing sides: the side it is on losing badly, and the other one nearer to it.
                var sideScore = attacking ? war.WarScore : -war.WarScore;
                var toLeader = CampaignDiplomacy.Peek(c, memberId, leader.Id)?.Opinion ?? 0f;
                var toEnemy = CampaignDiplomacy.Peek(c, memberId, enemyLeader.Id)?.Opinion ?? 0f;
                var switchSides = wish >= d.SeparatePeaceSwitchThreshold && sideScore < -35f && toEnemy > toLeader + 30f &&
                                  PoliticsCore.IdeologicalDistance(member, enemyLeader) < PoliticsCore.IdeologicalDistance(member, leader);

                foreach (var terms in PreferredTerms(c, war, member))
                {
                    if (enemyLeader.IsHuman && !c.NobodyPlays) { Offer(c, war, enemyLeader, member, terms, fromMember: true); break; }
                    if (Acceptance(c, war, memberId, terms) < 50f) continue;
                    Sign(c, war, memberId, terms, switchSides);
                    break;
                }
            }
        }
    }

    /// <summary>The terms a member asks for, in its order: free if it can, paying if it must, ground only when desperate.</summary>
    private static IEnumerable<SeparatePeaceTerms> PreferredTerms(Campaign c, StrategicWar war, Nation member)
    {
        yield return SeparatePeaceTerms.WhiteExit;
        if (member.Treasury > member.Gdp * c.Defines.SeparatePeaceIndemnityShare) yield return SeparatePeaceTerms.Indemnity;
        if (Wish(c, war, member) >= 75f && OccupiedShare(c, war, member) > .05f) yield return SeparatePeaceTerms.Concede;
    }

    /// <summary>
    /// A separate peace put to a player as a question: either a member asking the player's side to
    /// let it go, or the player's enemy offering to let the player go.
    /// </summary>
    internal static void Offer(Campaign c, StrategicWar war, Nation to, Nation from, SeparatePeaceTerms terms, bool fromMember)
    {
        var memberId = fromMember ? from.Id : to.Id;
        if (c.PendingEvents.Any(e => e.NationId == to.Id && e.Options.Any(o => o.SeparatePeaceWarId == war.Id && o.SeparatePeaceMemberId == memberId)))
            return;
        var id = c.PendingEvents.Count == 0 ? 1 : c.PendingEvents.Max(e => e.Id) + 1;
        var described = TermsSentence(terms);
        c.PendingEvents.Add(new PendingCampaignEvent
        {
            Id = id,
            NationId = to.Id,
            CreatedDay = c.ElapsedDays,
            ExpiresDay = c.ElapsedDays + 20,
            Title = fromMember
                ? Loc.Format("{0} asks for a separate peace", from.DisplayName)
                : Loc.Format("{0} offers us a separate peace", from.DisplayName),
            Description = fromMember
                ? Loc.Format("{0} wants to leave the war against us on its own, while its allies fight on. {1}", from.DisplayName, described)
                : Loc.Format("{0} offers to let us leave the war while our allies fight on. They will remember it. {1}", from.DisplayName, described),
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = fromMember ? "Let them go" : "Accept and leave the war",
                    Consequence = described,
                    SeparatePeaceWarId = war.Id,
                    SeparatePeaceMemberId = memberId,
                    SeparatePeaceTermsId = (int)terms
                },
                new CampaignEventOption
                {
                    Title = fromMember ? "They stay in this war to its end" : "Stand by our allies",
                    Consequence = "The war goes on as it was."
                }
            }
        });
        c.MarkStrategicChange();
    }

    public static string TermsSentence(SeparatePeaceTerms terms) => Loc.Get(terms switch
    {
        SeparatePeaceTerms.Concede => "The leaver gives up the ground the other side holds of it.",
        SeparatePeaceTerms.Indemnity => "The leaver pays an indemnity; every occupation between them goes back.",
        SeparatePeaceTerms.Bought => "The other side pays the leaver to go; every occupation between them goes back.",
        _ => "Every occupation between them goes back, and a five-year truce."
    });

    public static string TermsName(SeparatePeaceTerms terms) => Loc.Get(terms switch
    {
        SeparatePeaceTerms.Concede => "Cede the occupied ground",
        SeparatePeaceTerms.Indemnity => "Pay an indemnity",
        SeparatePeaceTerms.Bought => "Paid to leave",
        _ => "Leave on the old lines"
    });
}

/// <summary>
/// A separate peace, from either end: the member asking the other side to let it go, or the other
/// side offering to let a member go. A government nobody plays answers at once; a player is asked.
/// </summary>
public sealed class SeparatePeaceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int WarId { get; init; }
    public int MemberId { get; init; }
    public SeparatePeaceTerms Terms { get; init; }
    public bool SwitchSides { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var war = campaign.Wars.FirstOrDefault(candidate => candidate.Id == WarId);
        if (war is null || SeparatePeace.Refusal(campaign, war, MemberId).Length > 0) return;
        var member = campaign.NationById(MemberId);
        var enemyLeader = campaign.NationById(war.EnemyLeaderOf(MemberId));
        if (member is null || enemyLeader is null) return;
        if (IssuerId == MemberId)
        {
            if (enemyLeader.IsHuman && !campaign.NobodyPlays) { SeparatePeace.Offer(campaign, war, enemyLeader, member, Terms, fromMember: true); return; }
            if (SeparatePeace.Acceptance(campaign, war, MemberId, Terms) >= 50f) SeparatePeace.Sign(campaign, war, MemberId, Terms, SwitchSides);
            else war.LastNegotiation = Loc.Format("{0} refused to let {1} go on those terms.", enemyLeader.DisplayName, member.DisplayName);
            return;
        }
        if (!war.Opposes(IssuerId, MemberId)) return;
        if (member.IsHuman && !campaign.NobodyPlays) { SeparatePeace.Offer(campaign, war, member, campaign.NationById(IssuerId), Terms, fromMember: false); return; }
        if (SeparatePeace.Wish(campaign, war, member) + SeparatePeace.MemberGain(Terms) >= campaign.Defines.SeparatePeaceAiThreshold &&
            SeparatePeace.Acceptance(campaign, war, MemberId, Terms) >= 50f)
            SeparatePeace.Sign(campaign, war, MemberId, Terms);
        else war.LastNegotiation = Loc.Format("{0} will not leave its allies.", member.DisplayName);
    }
}

/// <summary>A war leader's subsidy to a wavering ally.</summary>
public sealed class SubsidiseAllyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int WarId { get; init; }
    public int MemberId { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var war = campaign.Wars.FirstOrDefault(candidate => candidate.Id == WarId);
        var refused = SeparatePeace.Subsidise(campaign, war, IssuerId, MemberId);
        if (war is not null && refused.Length > 0) war.LastNegotiation = refused;
    }
}
