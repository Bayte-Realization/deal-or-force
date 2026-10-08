using System;
using System.Collections.Generic;

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
    private static readonly Dictionary<ushort, List<int>> Held = new();
    private static readonly List<(Nation Neighbour, float Threat)> Threats = new();
    private static readonly List<int> Desired = new();
    private static readonly HashSet<int> DesiredSet = new();
    private static readonly List<Army> Free = new();
    private static int _heldDay = -1;
    private static Campaign _heldFor;

    /// <summary>Moves made since the campaign began, for the bench.</summary>
    public static int Moves { get; private set; }

    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(ThreatPosture))) return;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (map is null || controllers is null || campaign.ElapsedDays <= 0) return;
        var every = Math.Max(1, campaign.Defines.ThreatPostureReviewDays);

        foreach (var nation in campaign.Nations)
        {
            if ((campaign.ElapsedDays + nation.Id) % every != 0) continue;
            if (nation.Population <= 0 || nation.Movement is not null || nation.Landless) continue;
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Army)) continue;
            if (AtWar(campaign, nation)) continue;
            Review(campaign, nation);
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

    private static void Review(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        IndexHeld(campaign);
        if (!Held.TryGetValue((ushort)nation.MapIndex, out var ours) || ours.Count == 0) return;

        // Who there is to fear.
        Threats.Clear();
        var total = 0f;
        foreach (var mapIndex in CampaignDiplomacy.Neighbours(campaign, nation.MapIndex))
        {
            var neighbour = NationAt(campaign, mapIndex);
            var threat = ThreatOf(campaign, nation, neighbour);
            if (threat < defines.ThreatPostureMinimum) continue;
            Threats.Add((neighbour, threat));
            total += threat;
        }
        if (Threats.Count == 0) return;
        Threats.Sort((left, right) =>
        {
            var order = right.Threat.CompareTo(left.Threat);
            return order != 0 ? order : left.Neighbour.Id.CompareTo(right.Neighbour.Id);
        });

        // The army it has to move: formations at liberty, not militia, not at sea, not under a
        // player's own hand.
        Free.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.TemplateId == MilitiaCore.TemplateId) continue;
            if (army.InBattle || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0) continue;
            if (army.DirectOrder || army.AssignedWarId >= 0 || army.FrontLineId >= 0 || army.PlanActive) continue;
            Free.Add(army);
        }
        if (Free.Count == 0) return;

        var share = M.Clamp(defines.ThreatPostureFloor + total * defines.ThreatPosturePerThreat,
            defines.ThreatPostureFloor, defines.ThreatPostureCeiling);
        var onBorders = Math.Max(1, (int)MathF.Round(Free.Count * share));

        // Each threat's share of the border army, and its border's posts spread evenly.
        Desired.Clear();
        DesiredSet.Clear();
        var given = 0;
        foreach (var (neighbour, threat) in Threats)
        {
            if (given >= onBorders) break;
            var count = Math.Max(1, (int)MathF.Round(onBorders * threat / total));
            count = Math.Min(count, onBorders - given);
            var border = BorderWith(map, controllers, ours, (ushort)neighbour.MapIndex);
            if (border.Count == 0) continue;
            for (var index = 0; index < count; index++)
            {
                var post = border[(int)((index + 0.5f) * border.Count / count) % border.Count];
                Desired.Add(post);
                DesiredSet.Add(post);
            }
            given += count;
        }
        if (Desired.Count == 0) return;

        // Who is already where they should be.
        var covered = new Dictionary<int, int>();
        foreach (var army in Free)
        {
            var at = army.OrderProvinceId > 0 && army.Route.Count > 0 ? army.OrderProvinceId : army.ProvinceId;
            if (DesiredSet.Contains(at)) covered[at] = covered.GetValueOrDefault(at) + 1;
        }

        var moves = 0;
        foreach (var post in Desired)
        {
            if (moves >= defines.ThreatPostureMovesPerLook) break;
            if (covered.TryGetValue(post, out var there) && there > 0)
            {
                covered[post] = there - 1;
                continue;
            }
            // The nearest formation that is not itself holding a post.
            Army best = null;
            var bestDistance = float.MaxValue;
            var target = map.CenterOf(post);
            foreach (var army in Free)
            {
                var at = army.OrderProvinceId > 0 && army.Route.Count > 0 ? army.OrderProvinceId : army.ProvinceId;
                if (DesiredSet.Contains(at)) continue;
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
            DesiredSet.Add(best.ProvinceId); // never pick it twice in one look
        }
    }

    private static List<int> BorderWith(ProvinceTopology map, ushort[] controllers, List<int> ours, ushort neighbour)
    {
        var border = new List<int>();
        foreach (var province in ours)
        {
            if (map.IsWater(province)) continue;
            foreach (var next in map.Neighbors[province])
                if (next < controllers.Length && controllers[next] == neighbour) { border.Add(province); break; }
        }
        return border;
    }

    private static void IndexHeld(Campaign campaign)
    {
        if (ReferenceEquals(_heldFor, campaign) && _heldDay == campaign.ElapsedDays) return;
        _heldFor = campaign;
        _heldDay = campaign.ElapsedDays;
        foreach (var list in Held.Values) list.Clear();
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        for (var province = 1; province <= map.ProvinceCount && province < controllers.Length; province++)
        {
            var owner = controllers[province];
            if (owner == 0) continue;
            if (!Held.TryGetValue(owner, out var list)) Held[owner] = list = new List<int>();
            list.Add(province);
        }
    }

    private static bool AtWar(Campaign campaign, Nation nation)
    {
        foreach (var war in campaign.Wars)
            if (!war.Ended && !war.Limited && war.Involves(nation.Id)) return true;
        return false;
    }

    private static Nation NationAt(Campaign campaign, int mapIndex)
    {
        foreach (var nation in campaign.Nations)
            if (nation.MapIndex == mapIndex) return nation;
        return null;
    }
}
