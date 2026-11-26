using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How often, in days, a country at peace looks at where its army stands.</summary>
    public int ThreatPostureReviewDays { get; set; } = 30;

    /// <summary>The share of the army a country keeps on its borders with nobody to fear, and the most it ever puts there.</summary>
    public float ThreatPostureFloor { get; set; } = 0.25f;
    public float ThreatPostureCeiling { get; set; } = 0.7f;

    /// <summary>How much more of the army each point of threat sends to the borders.</summary>
    public float ThreatPosturePerThreat { get; set; } = 0.15f;

    /// <summary>A neighbour has to be at least this threatening to be watched at all.</summary>
    public float ThreatPostureMinimum { get; set; } = 0.25f;

    /// <summary>The most divisions moved at one look: an army shifts over months, not in an afternoon.</summary>
    public int ThreatPostureMovesPerLook { get; set; } = 4;
}

/// <summary>
/// Where a country at peace keeps its army.
///
/// It used to be wherever the opening deployment had spread it along every border it has, or
/// wherever its last war had ended - so Poland faced Germany as hard as it faced Russia, and a
/// country that had just won a war kept its whole army standing on the old front for good.
/// A general staff watches the neighbours it has reason to fear and puts its divisions on
/// those borders, and moves them when the reasons move.
///
/// Once a month a government nobody plays weighs each neighbour: how bad the standoff is (the
/// tension stage), how hostile, whether they are rivals - times how strong the neighbour is
/// against us. The more there is to fear, the more of the army goes to the borders, shared
/// out between the threats by how threatening each is and spread evenly along each border.
/// Divisions already standing on a post stay; a few others are moved each month. At war the
/// front planners take over (FrontPlanner, FrontManager).
/// </summary>
public static class ThreatPosture
{
    private sealed class Board
    {
        public readonly List<(Nation Neighbour, float Threat)> Threats = new();
        public readonly List<int> Desired = new();
        public readonly HashSet<int> DesiredSet = new();
        public readonly List<Army> Free = new();
        public readonly List<int> BorderScratch = new();
        public readonly Dictionary<int, int> CoveredScratch = new();
        public readonly Dictionary<int, List<Army>> ArmiesByNation = new();
        public readonly Stack<List<Army>> ArmyListPool = new();
        public readonly HashSet<int> LiveWarNations = new();
        public readonly Dictionary<ushort, List<int>> BordersByNeighbour = new();
        public readonly Stack<List<int>> BorderListPool = new();
        public readonly HashSet<ushort> SeenNeighbours = new();
        public bool ArmiesIndexed;
    }

    private static readonly ConditionalWeakTable<Campaign, Board> Boards = new();

    /// <summary>Moves made since the campaign began, for the bench.</summary>
    public static int Moves { get; private set; }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>Monthly border posture review, yielding between countries.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(ThreatPosture))) yield break;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (map is null || controllers is null || campaign.ElapsedDays <= 0) yield break;
        var every = Math.Max(1, campaign.Defines.ThreatPostureReviewDays);
        var board = Boards.GetOrCreateValue(campaign);

        board.LiveWarNations.Clear();
        var liveWars = Warfare.LiveWars(campaign);
        for (var wi = 0; wi < liveWars.Count; wi++)
        {
            var war = liveWars[wi];
            board.LiveWarNations.Add(war.AttackerId);
            board.LiveWarNations.Add(war.DefenderId);
            foreach (var ally in war.AttackerAllies) board.LiveWarNations.Add(ally);
            foreach (var ally in war.DefenderAllies) board.LiveWarNations.Add(ally);
        }

        board.ArmiesIndexed = false;
        try
        {
            for (var i = 0; i < campaign.Nations.Count; i++)
            {
                var nation = campaign.Nations[i];
                if ((campaign.ElapsedDays + nation.Id) % every != 0) continue;
                if (nation.Population <= 0 || nation.Movement is not null || nation.Landless) continue;
                if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Command)) continue;
                if (board.LiveWarNations.Contains(nation.Id)) continue;
                Review(campaign, board, nation);
                yield return 0;
            }
        }
        finally
        {
            ClearArmies(board);
        }
    }

    /// <summary>How threatening one neighbour is to this country, nought for one it has no reason to fear.</summary>
    public static float ThreatOf(Campaign campaign, Nation nation, Nation neighbour)
    {
        if (nation is null || neighbour is null || neighbour.Id == nation.Id) return 0f;
        if (neighbour.Population <= 0 || neighbour.Movement is not null) return 0f;
        if (nation.AnswersTo(neighbour.Id) || neighbour.AnswersTo(nation.Id)) return 0f;
        var relation = CampaignDiplomacy.Peek(campaign, nation.Id, neighbour.Id);
        if (relation is null || relation.Stance == DiplomaticStance.Allied) return 0f;
        var reasons = Escalation.StageOf(relation) switch
        {
            TensionStage.Clashes => 1.8f,
            TensionStage.Crisis => 1.0f,
            TensionStage.Friction => 0.35f,
            _ => 0f
        };
        reasons += M.Clamp(-relation.Opinion / 100f, 0f, 1f) * 0.6f;
        if (relation.Stance == DiplomaticStance.Rival) reasons += 0.4f;
        if (reasons <= 0f) return 0f;
        var ours = MathF.Max(1f, AiStatecraft.CachedStrength(campaign, nation));
        var theirs = CampaignIntel.EstimatedStrength(campaign, nation.Id, neighbour);
        return reasons * MathF.Sqrt(M.Clamp(theirs / ours, 0.25f, 3f));
    }

    private static void Review(Campaign campaign, Board board, Nation nation)
    {
        var defines = campaign.Defines;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var ours = CampaignIntel.ProvincesHeldBy(campaign, controllers, nation.MapIndex);
        if (ours.Length == 0) return;

        // Who there is to fear.
        var threats = board.Threats;
        threats.Clear();
        var total = 0f;
        if (CampaignDiplomacy.Neighbours(campaign, nation.MapIndex) is HashSet<int> neighbourSet)
        {
            foreach (var mapIndex in neighbourSet)
            {
                var neighbour = campaign.NationByMapIndex(mapIndex);
                var threat = ThreatOf(campaign, nation, neighbour);
                if (threat < defines.ThreatPostureMinimum) continue;
                threats.Add((neighbour, threat));
                total += threat;
            }
        }
        if (threats.Count == 0) return;
        threats.Sort((left, right) =>
        {
            var order = right.Threat.CompareTo(left.Threat);
            return order != 0 ? order : left.Neighbour.Id.CompareTo(right.Neighbour.Id);
        });

        // The army it has to move: formations at liberty, not militia, not at sea, not under a
        // player's own hand.
        var free = board.Free;
        free.Clear();
        var nationArmies = ArmiesOf(campaign, board, nation.Id);
        for (var i = 0; i < nationArmies.Count; i++)
        {
            var army = nationArmies[i];
            if (army.TemplateId == MilitiaCore.TemplateId) continue;
            if (army.InBattle || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0) continue;
            if (army.DirectOrder || army.AssignedWarId >= 0 || army.FrontLineId >= 0 ||
                army.PlanActive && army.OrderKind != ArmyOrder.Manual) continue;
            free.Add(army);
        }
        if (free.Count == 0) return;

        var share = M.Clamp(defines.ThreatPostureFloor + total * defines.ThreatPosturePerThreat,
            defines.ThreatPostureFloor, defines.ThreatPostureCeiling);
        var onBorders = Math.Max(1, (int)MathF.Round(free.Count * share));

        // Each threat's share of the border army, and its border's posts spread evenly.
        var desired = board.Desired;
        var desiredSet = board.DesiredSet;
        desired.Clear();
        desiredSet.Clear();
        Dictionary<ushort, List<int>> bordersByNeighbour = threats.Count > 1
            ? BuildThreatBorders(board, map, controllers, ours, threats)
            : null;
        var given = 0;
        for (var ti = 0; ti < threats.Count; ti++)
        {
            if (given >= onBorders) break;
            var (neighbour, threat) = threats[ti];
            var count = Math.Max(1, (int)MathF.Round(onBorders * threat / total));
            count = Math.Min(count, onBorders - given);
            var border = bordersByNeighbour is not null
                ? (bordersByNeighbour.TryGetValue((ushort)neighbour.MapIndex, out var cachedBorder) ? cachedBorder : null)
                : BorderWith(board, map, controllers, ours, (ushort)neighbour.MapIndex);
            if (border is null || border.Count == 0) continue;
            var strategicPost = -1;
            var importance = 0f;
            if (defines.ExclaveDefenceWeight > 0f)
                for (var bi = 0; bi < border.Count; bi++)
                {
                    var post = border[bi];
                    var value = StrategicExclaves.Importance(campaign, post);
                    if (value <= importance) continue;
                    importance = value; strategicPost = post;
                }
            for (var index = 0; index < count; index++)
            {
                var post = index == 0 && strategicPost > 0 ? strategicPost :
                    border[(int)((index + 0.5f) * border.Count / count) % border.Count];
                desired.Add(post);
                desiredSet.Add(post);
            }
            given += count;
        }
        if (desired.Count == 0) return;

        // Who is already where they should be.
        var covered = board.CoveredScratch;
        covered.Clear();
        for (var fi = 0; fi < free.Count; fi++)
        {
            var army = free[fi];
            var at = army.OrderProvinceId > 0 && army.Route.Count > 0 ? army.OrderProvinceId : army.ProvinceId;
            if (desiredSet.Contains(at)) covered[at] = covered.GetValueOrDefault(at) + 1;
        }

        var moves = 0;
        for (var di = 0; di < desired.Count; di++)
        {
            if (moves >= defines.ThreatPostureMovesPerLook) break;
            var post = desired[di];
            if (covered.TryGetValue(post, out var there) && there > 0)
            {
                covered[post] = there - 1;
                continue;
            }
            // The nearest formation that is not itself holding a post.
            Army best = null;
            var bestDistance = float.MaxValue;
            var target = map.CenterOf(post);
            for (var fi = 0; fi < free.Count; fi++)
            {
                var army = free[fi];
                if (army.Route.Count > 0 || army.ReadyDay > campaign.ElapsedDays || army.LentById >= 0) continue;
                var at = army.ProvinceId;
                if (desiredSet.Contains(at)) continue;
                var distance = NavalOperations.Distance(army.Latitude, army.Longitude, target.Latitude, target.Longitude);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = army;
            }
            if (best is null) break;
            var order = new MoveArmyCommand { IssuerId = nation.Id, ArmyId = best.Id, TargetProvinceId = post };
            order.Apply(campaign);
            // Its own staff's posting, not a player's: the front planners may still take it the
            // day a war begins.
            best.DirectOrder = false;
            if (best.OrderProvinceId == post) { moves++; Moves++; }
            desiredSet.Add(best.ProvinceId); // never pick it twice in one look
        }
    }

    private static Dictionary<ushort, List<int>> BuildThreatBorders(Board board, ProvinceTopology map, ushort[] controllers,
        ReadOnlySpan<int> ours, List<(Nation Neighbour, float Threat)> threats)
    {
        var byNeighbour = board.BordersByNeighbour;
        foreach (var list in byNeighbour.Values)
        {
            list.Clear();
            board.BorderListPool.Push(list);
        }
        byNeighbour.Clear();
        for (var i = 0; i < threats.Count; i++)
        {
            var key = (ushort)threats[i].Neighbour.MapIndex;
            if (!byNeighbour.ContainsKey(key))
                byNeighbour[key] = board.BorderListPool.Count > 0 ? board.BorderListPool.Pop() : new List<int>();
        }
        var seen = board.SeenNeighbours;
        var neighbors = map.Neighbors;
        var water = map.WaterByProvince;
        for (var i = 0; i < ours.Length; i++)
        {
            var province = ours[i];
            if (province <= 0 || province >= water.Length || water[province]) continue;
            seen.Clear();
            var adj = neighbors[province];
            for (var j = 0; j < adj.Length; j++)
            {
                var next = adj[j];
                if (next < controllers.Length)
                {
                    var ctrl = controllers[next];
                    if (byNeighbour.TryGetValue(ctrl, out var list) && seen.Add(ctrl))
                        list.Add(province);
                }
            }
        }
        return byNeighbour;
    }

    private static List<int> BorderWith(Board board, ProvinceTopology map, ushort[] controllers, ReadOnlySpan<int> ours, ushort neighbour)
    {
        var borderScratch = board.BorderScratch;
        borderScratch.Clear();
        var neighbors = map.Neighbors;
        var water = map.WaterByProvince;
        for (var i = 0; i < ours.Length; i++)
        {
            var province = ours[i];
            if (province <= 0 || province >= water.Length || water[province]) continue;
            var adj = neighbors[province];
            for (var j = 0; j < adj.Length; j++)
            {
                var next = adj[j];
                if (next < controllers.Length && controllers[next] == neighbour) { borderScratch.Add(province); break; }
            }
        }
        return borderScratch;
    }

    private static IReadOnlyList<Army> ArmiesOf(Campaign campaign, Board board, int nationId)
    {
        if (!board.ArmiesIndexed)
        {
            ClearArmies(board);
            var armies = campaign.Armies;
            for (var i = 0; i < armies.Count; i++)
            {
                var army = armies[i];
                if (!board.ArmiesByNation.TryGetValue(army.NationId, out var list))
                    board.ArmiesByNation[army.NationId] = list = board.ArmyListPool.Count > 0 ? board.ArmyListPool.Pop() : new List<Army>();
                list.Add(army);
            }
            board.ArmiesIndexed = true;
        }
        return board.ArmiesByNation.TryGetValue(nationId, out var owned) ? owned : Array.Empty<Army>();
    }

    private static void ClearArmies(Board board)
    {
        foreach (var list in board.ArmiesByNation.Values)
        {
            list.Clear();
            board.ArmyListPool.Push(list);
        }
        board.ArmiesByNation.Clear();
        board.ArmiesIndexed = false;
    }
}
