using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed class StrategicWar
{
    public int Id;
    public string Name = "";
    public int AttackerId;
    public int DefenderId;
    public int StartedDay;
    public float AttackerWarSupport = 1f;
    public float DefenderWarSupport = 1f;
    public float AttackerLosses;
    public float DefenderLosses;
    public float OccupationScore;
    public float BattleScore;
    public float ObjectiveScore;
    public float SupportScore;
    public float WarScore;
    public float Reparations;

    /// <summary>Provinces that changed owner - not merely occupier - when this war ended.</summary>
    public int AnnexedProvinces;

    /// <summary>Regions the new border cut in two, and so brought into existence.</summary>
    public int CreatedStates;
    public int ClaimedStateMapIndex = -1;

    /// <summary>
    /// The day before which neither AI belligerent will sit down. A war a scenario opens
    /// with is a war both governments have already refused to end; leaving that to the
    /// ordinary appetite for peace let the third year of the Russo-Ukrainian war be settled
    /// in its first campaign month by whichever side happened to be marginally ahead.
    /// A player may always propose; this only governs what the AI is willing to offer.
    /// </summary>
    public int AiNegotiationUnlockDay;
    public bool Ended;
    public bool FrontDirty = true;
    public int LastFrontDay = -1;
    public int LastPeaceProposalDay = -1;
    public string LastNegotiation = "";

    /// <summary>
    /// A settlement the other government has put to a player and is waiting on.
    ///
    /// Two countries run by the computer settle a war between themselves in one step: the
    /// demand is written and the answer is worked out in the same breath. That cannot happen
    /// to a person, so when one side of a war is being played, what would have been a treaty
    /// waits here for an answer instead. It lives on the war because that is where its
    /// lifetime already is - the war ending takes it with it, and a save that restores the
    /// war restores what was on the table.
    /// </summary>
    public PeaceDemand PendingOffer;

    /// <summary>Whether the offer is a written settlement or one of the standing terms.</summary>
    public bool PendingOfferIsWritten;
    public PeaceTerms PendingOfferTerms = PeaceTerms.WhitePeace;

    /// <summary>Who is waiting on an answer, or -1 when nobody is.</summary>
    public int PendingOfferFromId = -1;
    public int PendingOfferDay = -1;
    public int PendingOfferExpiresDay = -1;
    public int EndedDay = -1;
    public int WinnerId = -1;
    public string PeaceSummary = "";
    public readonly HashSet<int> AttackerFront = new();
    public readonly HashSet<int> DefenderFront = new();
    public readonly Dictionary<int, ushort> OriginalController = new();
    public readonly Dictionary<int, float> UnopposedProgress = new();

    public bool Involves(int nationId) => nationId == AttackerId || nationId == DefenderId;
    public IReadOnlySet<int> FriendlyFront(int nationId) =>
        nationId == AttackerId ? AttackerFront : nationId == DefenderId ? DefenderFront : EmptyFront;

    private static readonly HashSet<int> EmptyFront = new();
}

public static class WarBootstrap
{
    public static void Seed(Campaign campaign)
    {
        var russia = campaign.NationByTag("RUS");
        var ukraine = campaign.NationByTag("UKR");
        if (russia is null || ukraine is null) return;
        campaign.Wars.Add(new StrategicWar
        {
            Id = 0,
            Name = "Russo-Ukrainian War",
            AttackerId = russia.Id,
            DefenderId = ukraine.Id,
            StartedDay = campaign.ElapsedDays,
            AiNegotiationUnlockDay = campaign.ElapsedDays + campaign.Defines.ScenarioWarNegotiationLockDays,
            ClaimedStateMapIndex = campaign.States.FirstOrDefault(state =>
                state.NationMapIndex == ukraine.MapIndex &&
                state.Name.Contains("Kyiv", StringComparison.OrdinalIgnoreCase))?.MapIndex ??
                campaign.States.Where(state => state.NationMapIndex == ukraine.MapIndex)
                    .OrderByDescending(state => state.Population).FirstOrDefault()?.MapIndex ?? -1
        });
        var relation = CampaignDiplomacy.Get(campaign, russia.Id, ukraine.Id);
        relation.Stance = DiplomaticStance.AtWar;
        relation.Opinion = -80f;
    }
}

public static partial class Warfare
{
    /// <summary>
    /// Rebuild every front in one pass over the map.
    ///
    /// It used to walk all 37 807 provinces and their neighbours once per war. With a
    /// single scripted war that was invisible; once countries started declaring their own
    /// and seventy were running at a time it became two and a half million province visits
    /// per rebuild and the largest cost in the simulation. The map is walked once, and each
    /// pair of hostile neighbours is filed against whichever wars those two countries are
    /// fighting each other in.
    /// </summary>
    public static void RebuildFronts(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController ?? topology?.ProvinceToNation;
        if (topology is null || controllers is null) return;

        // Which war, if any, two countries are fighting each other in.
        //
        // The province walk below is the single most expensive thing the simulation does -
        // thirty-eight thousand provinces and all their neighbours, every time any war's
        // front goes stale. Hashing a country index twice per neighbour was most of it, so
        // membership is a flat array indexed by map index instead, and the attacker's index
        // is worked out once per war rather than once per pair of touching provinces.
        var byPair = Pairs;
        byPair.Clear();
        var belligerents = Belligerents;
        if (belligerents.Length <= campaign.Nations.Count + 8)
            belligerents = Belligerents = new bool[campaign.Nations.Count + 512];
        Array.Clear(belligerents);
        foreach (var war in campaign.Wars)
        {
            war.AttackerFront.Clear();
            war.DefenderFront.Clear();
            war.FrontDirty = false;
            war.LastFrontDay = campaign.ElapsedDays;
            if (war.Ended) continue;
            var attacker = campaign.NationById(war.AttackerId);
            var defender = campaign.NationById(war.DefenderId);
            if (attacker is null || defender is null) continue;
            byPair[PairKey(attacker.MapIndex, defender.MapIndex)] = (war, (ushort)attacker.MapIndex);
            if (attacker.MapIndex < belligerents.Length) belligerents[attacker.MapIndex] = true;
            if (defender.MapIndex < belligerents.Length) belligerents[defender.MapIndex] = true;
        }
        if (byPair.Count == 0) return;

        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var owner = controllers[province];
            if (owner == 0 || owner >= belligerents.Length || !belligerents[owner]) continue;
            // Nobody holds a line in the middle of a lake. Water can be owned and drawn,
            // and it can be the thing two armies are on opposite sides of, but it is never
            // itself a place a division is asked to stand.
            if (topology.IsWater(province)) continue;
            foreach (var neighbour in topology.Neighbors[province])
            {
                var other = controllers[neighbour];
                if (other == 0 || other == owner || other >= belligerents.Length || !belligerents[other]) continue;
                if (topology.IsWater(neighbour)) continue;
                if (!byPair.TryGetValue(PairKey(owner, other), out var found)) continue;

                if (owner == found.AttackerMap)
                {
                    found.War.AttackerFront.Add(province);
                    found.War.DefenderFront.Add(neighbour);
                }
                else
                {
                    found.War.DefenderFront.Add(province);
                    found.War.AttackerFront.Add(neighbour);
                }
            }
        }
    }

    private static readonly Dictionary<long, (StrategicWar War, ushort AttackerMap)> Pairs = new();
    private static bool[] Belligerents = Array.Empty<bool>();

    /// <summary>Order-independent key for a pair of countries.</summary>
    private static long PairKey(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
}

public static partial class FrontPlanner
{
    /// <summary>
    /// The provinces a reserve stands on: everything of ours that touches our own front
    /// without being on it.
    ///
    /// This is the same answer for every formation of a nation in a war, and it was being
    /// worked out separately for each of them - twice, because issuing the order asked for
    /// it again - by walking the whole front and all its neighbours and building a fresh
    /// set each time. That was most of the garbage the war pass made. It is worked out once
    /// and kept until the front is redrawn or a province changes hands, which is exactly
    /// when the answer can change.
    /// </summary>
    private static readonly Dictionary<long, (int Stamp, HashSet<int> Line)> ReserveLines = new();

    public static HashSet<int> ReserveLine(Campaign campaign, StrategicWar war, int nationId)
    {
        var topology = campaign.ProvinceMap;
        var nation = campaign.NationById(nationId);
        var controllers = campaign.ProvinceController;
        if (topology is null || nation is null || controllers is null) return new HashSet<int>();

        var key = ((long)war.Id << 32) | (uint)nationId;
        var stamp = HashCode.Combine(war.LastFrontDay, campaign.MapRevision,
            war.AttackerFront.Count, war.DefenderFront.Count);
        if (ReserveLines.TryGetValue(key, out var cached) && cached.Stamp == stamp) return cached.Line;

        var result = new HashSet<int>();
        var front = war.FriendlyFront(nationId);
        foreach (var province in front)
        foreach (var neighbor in topology.Neighbors[province])
            if (!front.Contains(neighbor) && controllers[neighbor] == nation.MapIndex)
                result.Add(neighbor);
        if (result.Count == 0) result = new HashSet<int>(front);
        ReserveLines[key] = (stamp, result);
        return result;
    }

    public static void RefreshAssignments(Campaign campaign, StrategicWar war)
    {
        foreach (var army in campaign.Armies)
        {
            if (army.AssignedWarId != war.Id || army.OrderKind == ArmyOrder.Manual || army.InBattle)
                continue;
            if (army.DirectOrder && army.OrderProvinceId > 0) continue;
            if (army.EmbarkedFleetId >= 0) continue;

            // A division that was given somewhere to be, and is still on its way there, is
            // left to get there.
            //
            // The test below asks whether the target is still on the front, and the front is
            // rebuilt every time a province changes hands - so on a moving front a marching
            // division fails that test almost every day and is handed a new target it will
            // also never reach. That is measurable: three years of unbroken marching, and a
            // hundred and twelve formations re-entering the same province five times or more.
            //
            // What still excuses an immediate re-task is the objective going away: ground we
            // now hold ourselves is not somewhere to march to.
            if (army.OrderProvinceId > 0 &&
                campaign.ElapsedDays - army.OrderIssuedDay < campaign.Defines.OrderSettleDays &&
                !OwnGround(campaign, army)) continue;
            var expected = BattlePlanning.Posts(campaign, war, army);
            if (expected.Count == 0 || expected.Contains(army.ProvinceId) ||
                (army.OrderProvinceId > 0 && expected.Contains(army.OrderProvinceId)))
                continue;
            new SetArmyOrderCommand
            {
                IssuerId = army.NationId,
                ArmyId = army.Id,
                Order = army.OrderKind,
                ObjectiveStateMapIndex = army.ObjectiveStateMapIndex
            }.Apply(campaign);
        }
    }

    /// <summary>
    /// Whether what a formation is marching towards is already its own side's ground, which
    /// is the one thing that makes a march pointless rather than merely unfinished.
    /// </summary>
    private static bool OwnGround(Campaign campaign, Army army)
    {
        var control = campaign.ProvinceController;
        var nation = campaign.NationById(army.NationId);
        if (control is null || nation is null) return false;
        if (army.OrderProvinceId <= 0 || army.OrderProvinceId >= control.Length) return false;
        // Only for the orders that are about taking ground. A division posted to defend or
        // to sit in reserve is supposed to be marching to somewhere its own side holds.
        if (army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead)
            return false;
        return control[army.OrderProvinceId] == (ushort)nation.MapIndex;
    }

    public static void AssignAi(Campaign campaign)
    {
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            AssignSide(campaign, war, war.AttackerId, true);
            AssignSide(campaign, war, war.DefenderId, false);
        }
    }

    public static void ReassignAi(Campaign campaign, StrategicWar war)
    {
        if (war.Ended) return;
        AssignSide(campaign, war, war.AttackerId, true);
        AssignSide(campaign, war, war.DefenderId, false);
    }

    public static void UpdateAiExecution(Campaign campaign, StrategicWar war)
    {
        // Filtered by hand: the lambda captured the war, so this was a fresh closure and a
        // fresh enumerator for every one of the world's wars, every day.
        foreach (var army in campaign.Armies)
        {
            if (army.AssignedWarId != war.Id || army.PlanActive) continue;
            if (army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) continue;
            var nation = campaign.NationById(army.NationId);
            if (nation is null || nation.IsHuman) continue;
            var profile = AiStrategyCatalog.Resolve(campaign, war, nation);
            if (army.Planning < profile.ExecutePlanningThreshold) continue;
            new SetArmyPlanExecutionCommand { IssuerId = nation.Id, ArmyId = army.Id, Active = true }.Apply(campaign);
        }
    }

    private static void AssignSide(Campaign campaign, StrategicWar war, int nationId, bool attacking)
    {
        var nation = campaign.NationById(nationId);
        if (nation is null || nation.IsHuman) return;
        var profile = AiStrategyCatalog.Resolve(campaign, war, nation);
        // Territorial defence is not part of the field army's order of battle. Militia holds
        // the region it stood up in; sweeping it into the front shares distorted them badly -
        // the counts here are shares of how many formations there are, so twenty-seven
        // militia pushed real divisions out of defending and into attacking, and Ukraine lost
        // two thirds of its ground to a mechanic written to help it hold.
        var armies = campaign.Armies.FindAll(army =>
            army.NationId == nationId && army.TemplateId != MilitiaCore.TemplateId);
        // Strongest first.
        //
        // Worth recording what does NOT work here, because it looks obviously right: Power
        // moves every day with losses and recovery, so similar divisions trade places
        // constantly, and roles are handed out by POSITION in this list - so a swap across
        // the spearhead/advance boundary flips two divisions' orders and restarts both their
        // marches. Bucketing the power to damp that was tried at two coarsenesses and made
        // the war worse both times: the ties it creates are broken by id, which is unrelated
        // to strength, so weak divisions lead attacks. Ukraine lost thirty provinces at 0.1
        // and thirty at 0.02 where it gained eighteen without it.
        //
        // The churn is real - 8,000-odd orders in three years against 2,900 arrivals - but
        // the cure is not here. It is that a division should be re-posted when its post has
        // stopped being useful, not when the list it came from was rebuilt.
        armies.Sort((a, b) => b.Power.CompareTo(a.Power));

        var advanceShare = attacking ? profile.AdvanceShareAttacker : profile.AdvanceShareDefender;
        var spearhead = armies.Count == 0 ? 0 : Math.Min((int)MathF.Round(armies.Count * advanceShare),
            Math.Max(1, (int)MathF.Round(armies.Count * profile.SpearheadShare)));
        var advance = (int)MathF.Round(armies.Count * advanceShare);
        var defend = (int)MathF.Round(armies.Count * profile.DefendShare);
        for (var index = 0; index < armies.Count; index++)
        {
            var army = armies[index];
            var order = index < spearhead ? ArmyOrder.Spearhead :
                index < advance ? ArmyOrder.AdvanceFront :
                index < advance + defend ? ArmyOrder.DefendFront : ArmyOrder.ReserveFront;
            var stance = order == ArmyOrder.Spearhead || order == ArmyOrder.AdvanceFront
                ? profile.OffensiveStance
                : order == ArmyOrder.DefendFront ? profile.DefensiveStance : ArmyStance.Balanced;

            // Re-issuing an order a division already has is not free: it clears the route,
            // picks a fresh target from a front list that shifts every time a province
            // changes hands, and sends the division marching sideways - which costs
            // planning. Doing that to the whole army every time the line moved meant an AI
            // that spent its war walking up and down its own front and never attacked.
            // A division is only re-tasked when what is being asked of it actually changed.
            if (army.OrderKind != order || (order == ArmyOrder.Spearhead &&
                    army.ObjectiveStateMapIndex != war.ClaimedStateMapIndex))
                new SetArmyOrderCommand
                {
                    IssuerId = nationId,
                    ArmyId = army.Id,
                    ObjectiveStateMapIndex = war.ClaimedStateMapIndex,
                    Order = order
                }.Apply(campaign);

            if (army.Stance != stance)
                new SetArmyStanceCommand { IssuerId = nationId, ArmyId = army.Id, Stance = stance }.Apply(campaign);

            var execute = army.Planning >= profile.ExecutePlanningThreshold;
            if (army.PlanActive != execute)
                new SetArmyPlanExecutionCommand
                {
                    IssuerId = nationId,
                    ArmyId = army.Id,
                    Active = execute
                }.Apply(campaign);
        }
    }
}
