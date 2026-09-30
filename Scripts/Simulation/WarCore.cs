using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class StrategicWar
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

    /// <summary>
    /// What those losses were made of. The two totals above stay the totals - the war score
    /// and home support read them - and these break the same men down into the three things
    /// that happen to a soldier a war takes: killed, wounded, or taken. See WarLosses.
    /// </summary>
    public float AttackerKilled;
    public float AttackerWounded;
    public float AttackerCaptured;
    public float DefenderKilled;
    public float DefenderWounded;
    public float DefenderCaptured;

    /// <summary>
    /// Prisoners still held, by the country they were taken from. An exchange sends some
    /// home during the war; the peace sends the rest.
    /// </summary>
    public readonly Dictionary<int, float> Prisoners = new();

    /// <summary>The day of the last prisoner exchange in this war, or -1.</summary>
    public int LastPrisonerExchangeDay = -1;
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

    /// <summary>
    /// Everybody else fighting on each side, the two leaders excepted.
    ///
    /// A war used to be a pair of countries and an alliance answering one produced a fresh
    /// war per member: a NATO war was thirty wars, thirty war scores and thirty separate
    /// peace deals, which is what a player sees and what they reported. The leaders stay
    /// where they were - the country that declared and the country it declared on - so
    /// everything that already asked who started it still gets the right answer, and
    /// everybody who comes afterwards stands beside one of them.
    ///
    /// Public readonly collections, like the fronts above, because the save serialises the
    /// nation list whole with fields included and populates rather than replaces.
    /// </summary>
    public readonly HashSet<int> AttackerAllies = new();

    public readonly HashSet<int> DefenderAllies = new();

    public bool Involves(int nationId) => IsAttacking(nationId) || IsDefending(nationId);

    public bool IsAttacking(int nationId) =>
        nationId == AttackerId || (AttackerAllies.Count > 0 && AttackerAllies.Contains(nationId));

    public bool IsDefending(int nationId) =>
        nationId == DefenderId || (DefenderAllies.Count > 0 && DefenderAllies.Contains(nationId));

    /// <summary>Whether these two are on opposite sides of this war.</summary>
    public bool Opposes(int first, int second) =>
        (IsAttacking(first) && IsDefending(second)) || (IsDefending(first) && IsAttacking(second));

    /// <summary>
    /// The country on the other side that this war is about.
    ///
    /// Most of the simulation wants one enemy rather than a list - somewhere to march, a
    /// coast to blockade, a name for the war - and the leader is the right answer for all
    /// of them. Anything that has to be right about every belligerent asks for the side.
    /// </summary>
    public int EnemyLeaderOf(int nationId) =>
        IsAttacking(nationId) ? DefenderId : IsDefending(nationId) ? AttackerId : -1;

    /// <summary>Everybody on one side of it, leader first.</summary>
    public IEnumerable<int> Side(bool attacking)
    {
        yield return attacking ? AttackerId : DefenderId;
        foreach (var member in attacking ? AttackerAllies : DefenderAllies) yield return member;
    }

    /// <summary>Everybody this country is fighting, leader first. Empty if it is not in the war.</summary>
    public IEnumerable<int> EnemiesOf(int nationId)
    {
        if (IsAttacking(nationId)) return Side(false);
        if (IsDefending(nationId)) return Side(true);
        return Array.Empty<int>();
    }

    /// <summary>How many countries are in it. Two is the ordinary war.</summary>
    public int BelligerentCount => 2 + AttackerAllies.Count + DefenderAllies.Count;

    public IReadOnlySet<int> FriendlyFront(int nationId) =>
        IsAttacking(nationId) ? AttackerFront : IsDefending(nationId) ? DefenderFront : EmptyFront;

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
        // Remembered, the way a declared war is, not written into the opinion: opinion is the
        // sum of its reasons, and the minus eighty that used to be set here was summed away at
        // the next settle - Ukraine thought +11 of Russia a week into the war. The ground Russia
        // holds of Ukraine's is a reason of its own.
        OpinionSystem.Remember(campaign, relation, "declared_war_on_us");
        OpinionSystem.Remember(campaign, relation, "fought_a_war");
        OpinionSystem.Remember(campaign, relation, "took_our_land");
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
        // Map indices are sparse: breakaways start at 1000, and mods may use
        // still higher values. Country COUNT silently excluded their entire fronts.
        var largestMapIndex = 0;
        foreach (var nation in campaign.Nations) largestMapIndex = Math.Max(largestMapIndex, nation.MapIndex);
        if (belligerents.Length <= largestMapIndex)
            belligerents = Belligerents = new bool[largestMapIndex + 1];
        Array.Clear(belligerents);
        foreach (var war in campaign.Wars)
        {
            war.AttackerFront.Clear();
            war.DefenderFront.Clear();
            war.FrontDirty = false;
            war.LastFrontDay = campaign.ElapsedDays;
            if (war.Ended) continue;
            // Every pair across the two sides, not just the two leaders. A coalition war
            // has a front wherever any of its members touches any of the other side's, and
            // filing only the leaders' pair left everybody else's border unmanned.
            foreach (var attackingId in war.Side(true))
            {
                var attacker = campaign.NationById(attackingId);
                if (attacker is null) continue;
                if (attacker.MapIndex < belligerents.Length) belligerents[attacker.MapIndex] = true;
                foreach (var defendingId in war.Side(false))
                {
                    var defender = campaign.NationById(defendingId);
                    if (defender is null) continue;
                    if (defender.MapIndex < belligerents.Length) belligerents[defender.MapIndex] = true;
                    byPair[PairKey(attacker.MapIndex, defender.MapIndex)] = (war, (ushort)attacker.MapIndex);
                }
            }
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
                WarId = war.Id,
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
            foreach (var nationId in war.Side(true)) AssignSide(campaign, war, nationId, true);
            foreach (var nationId in war.Side(false)) AssignSide(campaign, war, nationId, false);
        }
    }

    public static void ReassignAi(Campaign campaign, StrategicWar war)
    {
        if (war.Ended) return;
        foreach (var nationId in war.Side(true)) AssignSide(campaign, war, nationId, true);
        foreach (var nationId in war.Side(false)) AssignSide(campaign, war, nationId, false);
    }

    public static void UpdateAiExecution(Campaign campaign, StrategicWar war) =>
        UpdateAiExecution(campaign, war, campaign.Armies);

    /// <param name="assigned">
    /// The divisions assigned to this war, in the order the world lists them - the advance
    /// pass has them indexed already. Walking every division in the world for every war, every
    /// hour, to find the same few hundred was a third of what the advance pass cost.
    /// </param>
    public static void UpdateAiExecution(Campaign campaign, StrategicWar war, List<Army> assigned)
    {
        // Filtered by hand: the lambda captured the war, so this was a fresh closure and a
        // fresh enumerator for every one of the world's wars, every day.
        foreach (var army in assigned)
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

    /// <summary>What the shares ask of the division standing at this place in the line.</summary>
    private static ArmyOrder RoleAt(int index, int spearhead, int advance, int defend) =>
        index < spearhead ? ArmyOrder.Spearhead :
        index < advance ? ArmyOrder.AdvanceFront :
        index < advance + defend ? ArmyOrder.DefendFront : ArmyOrder.ReserveFront;

    private static int AdvanceCount(int count, float share, bool attacking) =>
        Math.Min(count, Math.Max(attacking && share > 0 && count > 0 ? 1 : 0, (int)MathF.Round(count * share)));

    /// <summary>Fill missing roles with a replacement without reordering formations already marching.</summary>
    internal static void GiveIdleRole(Campaign c, Army army)
    {
        var war = c.Wars.Find(w => !w.Ended && w.Id == army.AssignedWarId);
        var nation = c.NationById(army.NationId);
        if (war is null || nation is null || nation.IsHuman) return;
        var profile = AiStrategyCatalog.Resolve(c, war, nation);
        var peers = c.Armies.Where(a => a.NationId == nation.Id && a.AssignedWarId == war.Id &&
            a.TemplateId != MilitiaCore.TemplateId).ToArray();
        var attacking = war.IsAttacking(nation.Id);
        var share = attacking ? profile.AdvanceShareAttacker : profile.AdvanceShareDefender;
        var wanted = AdvanceCount(peers.Length, share, attacking);
        var have = peers.Count(a => a != army && a.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead);
        if (have >= wanted) return;
        new SetArmyOrderCommand { IssuerId = nation.Id, ArmyId = army.Id, WarId = war.Id,
            Order = ArmyOrder.AdvanceFront }.Apply(c);
        new SetArmyStanceCommand { IssuerId = nation.Id, ArmyId = army.Id, Stance = profile.OffensiveStance }.Apply(c);
        new SetArmyPlanExecutionCommand { IssuerId = nation.Id, ArmyId = army.Id,
            Active = army.Planning >= profile.ExecutePlanningThreshold }.Apply(c);
    }

    /// <summary>
    /// Which of a country's divisions serve which of its wars.
    ///
    /// Every war took the whole army. Each war's pass sorted all of the country's divisions and
    /// handed them roles, and the order carrying them out sent every one to the first war in the
    /// list - so a country fighting two wars fought one. Over two years, on four worlds, seven in
    /// ten of the second fronts had none of the country's own army on them: South Korea's
    /// thirty-seven divisions sat on an ally's front while the North Korean one was held by
    /// nobody, and Nepal fought China with everything and India with nothing.
    ///
    /// Each war now gets a share of the army in proportion to what it needs - the country's own
    /// stretch of that front and the enemy divisions put into it; a war where it holds no ground
    /// of its own, an ally's, asks for little. A division stays with the war it serves while that
    /// war is not over its share, so divisions change fronts when the need moves and not when the
    /// list is rebuilt, and the war that is short takes the divisions nearest to its front.
    /// </summary>
    private static List<Army> ArmiesFor(Campaign campaign, StrategicWar war, int nationId)
    {
        var armies = campaign.Armies.FindAll(army =>
            army.NationId == nationId && army.TemplateId != MilitiaCore.TemplateId);
        SplitWars.Clear();
        var stamp = armies.Count;
        foreach (var candidate in campaign.Wars)
            if (!candidate.Ended && candidate.Involves(nationId))
            {
                SplitWars.Add(candidate);
                stamp = HashCode.Combine(stamp, candidate.Id);
            }
        if (SplitWars.Count < 2) return armies;

        // Once a day: the passes for each war, and every redraw of a front in between, read the
        // same split, so what one war's pass decides is not undone by the next war's.
        if (!Splits.TryGetValue(nationId, out var split)) Splits[nationId] = split = new ArmySplit();
        if (!ReferenceEquals(split.Campaign, campaign) || split.Day != campaign.ElapsedDays || split.Stamp != stamp)
        {
            split.Campaign = campaign;
            split.Day = campaign.ElapsedDays;
            split.Stamp = stamp;
            Split(campaign, nationId, SplitWars, armies, split.ByWar);
        }
        return split.ByWar.TryGetValue(war.Id, out var share) ? share : new List<Army>();
    }

    private sealed class ArmySplit
    {
        public Campaign Campaign;
        public int Day = -1;
        public int Stamp;
        public readonly Dictionary<int, List<Army>> ByWar = new();
    }

    private static readonly Dictionary<int, ArmySplit> Splits = new();
    private static readonly List<StrategicWar> SplitWars = new();

    private static void Split(Campaign campaign, int nationId, List<StrategicWar> wars, List<Army> armies,
        Dictionary<int, List<Army>> byWar)
    {
        byWar.Clear();
        var nation = campaign.NationById(nationId);
        var controllers = campaign.ProvinceController ?? campaign.ProvinceMap?.ProvinceToNation;
        var map = campaign.ProvinceMap;
        var count = wars.Count;
        var weight = new float[count];
        var fronts = new List<int>[count];
        for (var w = 0; w < count; w++)
        {
            var war = wars[w];
            byWar[war.Id] = new List<Army>();
            var front = war.FriendlyFront(nationId);
            var own = new List<int>();
            if (controllers is not null && nation is not null)
                foreach (var province in front)
                    if (controllers[province] == (ushort)nation.MapIndex) own.Add(province);
            var threat = 0;
            foreach (var enemy in campaign.Armies)
                if (enemy.AssignedWarId == war.Id && enemy.TemplateId != MilitiaCore.TemplateId &&
                    war.Opposes(nationId, enemy.NationId)) threat++;
            weight[w] = own.Count > 0 ? own.Count + 0.5f * threat : 0.1f * Math.Min(front.Count, threat);
            fronts[w] = own.Count > 0 ? own : new List<int>(front);
        }

        var total = 0f;
        foreach (var value in weight) total += value;
        if (total <= 0f || map is null || armies.Count == 0)
        {
            byWar[wars[0].Id].AddRange(armies);
            return;
        }

        // Whole divisions, largest remainders first, adding up to the army.
        var quota = new int[count];
        var given = 0;
        for (var w = 0; w < count; w++) given += quota[w] = (int)(armies.Count * weight[w] / total);
        while (given < armies.Count)
        {
            var most = 0;
            var remainder = float.MinValue;
            for (var w = 0; w < count; w++)
            {
                var left = armies.Count * weight[w] / total - quota[w];
                if (left > remainder) { remainder = left; most = w; }
            }
            quota[most]++;
            given++;
        }
        // A front of its own is never left to nobody while the army has a division to spare.
        for (var w = 0; w < count; w++)
        {
            if (quota[w] > 0 || fronts[w].Count == 0 || weight[w] < 1f) continue;
            var largest = 0;
            for (var v = 1; v < count; v++) if (quota[v] > quota[largest]) largest = v;
            if (quota[largest] <= 1) break;
            quota[largest]--;
            quota[w]++;
        }

        var distance = new float[armies.Count * count];
        for (var i = 0; i < armies.Count; i++)
        {
            var (latitude, longitude) = map.CenterOf(armies[i].ProvinceId);
            for (var w = 0; w < count; w++)
                distance[i * count + w] = SetArmyOrderCommand.FrontDistance(map, fronts[w], latitude, longitude);
        }

        var serving = new int[armies.Count];
        var taken = new int[count];
        var order = new List<int>(armies.Count);
        for (var i = 0; i < armies.Count; i++)
        {
            serving[i] = wars.FindIndex(candidate => candidate.Id == armies[i].AssignedWarId);
            order.Add(i);
        }
        // Those already serving a war stay with it, nearest to its front first, up to one over
        // its share - one either way is not worth a march across the country.
        order.Sort((a, b) => (serving[a] < 0 ? float.MaxValue : distance[a * count + serving[a]])
            .CompareTo(serving[b] < 0 ? float.MaxValue : distance[b * count + serving[b]]));
        var free = new List<int>();
        foreach (var i in order)
        {
            var w = serving[i];
            if (w >= 0 && quota[w] > 0 && taken[w] < quota[w] + 1) taken[w]++;
            else
            {
                serving[i] = -1;
                free.Add(i);
            }
        }
        // The rest go where divisions are short, the nearest first.
        while (free.Count > 0)
        {
            var shortest = -1;
            var need = 0;
            for (var w = 0; w < count; w++)
                if (quota[w] - taken[w] > need) { need = quota[w] - taken[w]; shortest = w; }
            var pick = 0;
            if (shortest < 0)
            {
                // Nobody is short: the nearest front.
                var i = free[0];
                var nearest = 0;
                for (var w = 1; w < count; w++)
                    if (distance[i * count + w] < distance[i * count + nearest]) nearest = w;
                serving[i] = nearest;
                taken[nearest]++;
                free.RemoveAt(0);
                continue;
            }
            for (var f = 1; f < free.Count; f++)
                if (distance[free[f] * count + shortest] < distance[free[pick] * count + shortest]) pick = f;
            serving[free[pick]] = shortest;
            taken[shortest]++;
            free.RemoveAt(pick);
        }

        for (var i = 0; i < armies.Count; i++) byWar[wars[serving[i]].Id].Add(armies[i]);
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
        var armies = ArmiesFor(campaign, war, nationId);
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
        var advance = AdvanceCount(armies.Count, advanceShare, attacking);
        var spearhead = armies.Count == 0 || profile.SpearheadShare <= 0 ? 0 : Math.Min(advance,
            Math.Max(1, (int)MathF.Round(armies.Count * profile.SpearheadShare)));
        var defend = (int)MathF.Round(armies.Count * profile.DefendShare);
        for (var index = 0; index < armies.Count; index++)
        {
            var army = armies[index];
            // A division on a march it was sent on directly - railed somewhere, called to a port
            // for the admiralty's landing - finishes it first. Handing it a role mid-journey
            // threw the journey away and left it riding the rails to a post it was never sent
            // to; when it arrives it is free again and gets its role then.
            if (army.DirectOrder && (army.Route.Count > 0 || army.PlannedRoute.Count > 0)) continue;
            var order = RoleAt(index, spearhead, advance, defend);

            // Damping this is the wrong cure, and both attempts at it proved so. Bucketing
            // the power made weak divisions lead attacks; holding a division in its job
            // until it had moved two places in the order of battle cut the re-tasking by a
            // tenth and cost Ukraine seventy-eight provinces, because an AI that answers a
            // moving front slowly is an AI that loses.
            //
            // The churn is load-bearing. What is wrong is not that a division's job changes
            // - it is that changing the job used to throw away the march, which is handled
            // where the post is chosen.

            var stance = order == ArmyOrder.Spearhead || order == ArmyOrder.AdvanceFront
                ? profile.OffensiveStance
                : order == ArmyOrder.DefendFront ? profile.DefensiveStance : ArmyStance.Balanced;

            // Re-issuing an order a division already has is not free: it clears the route,
            // picks a fresh target from a front list that shifts every time a province
            // changes hands, and sends the division marching sideways - which costs
            // planning. Doing that to the whole army every time the line moved meant an AI
            // that spent its war walking up and down its own front and never attacked.
            // A division is only re-tasked when what is being asked of it actually changed.
            //
            // Or when it is being handed to this war from another one the country is fighting.
            if (army.OrderKind != order || (order == ArmyOrder.Spearhead &&
                    army.ObjectiveStateMapIndex != war.ClaimedStateMapIndex) ||
                army.AssignedWarId != war.Id)
                new SetArmyOrderCommand
                {
                    IssuerId = nationId,
                    ArmyId = army.Id,
                    WarId = war.Id,
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
