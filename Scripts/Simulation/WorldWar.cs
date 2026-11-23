using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class StrategicWar
{
    /// <summary>The day this war became a world war - great powers on both sides of it - or -1.</summary>
    public int WorldWarDay = -1;
}

public sealed partial class Defines
{
    /// <summary>How many great powers a war must hold, all sides together, to be a world war (at least one on each side).</summary>
    public int WorldWarMinimumPowers { get; set; } = 3;
    /// <summary>Or how many countries in it, with at least two great powers on each side.</summary>
    public int WorldWarMinimumBelligerents { get; set; } = 10;
    /// <summary>A strong army counts as a world power only with a deterrent or an economy among this many largest.</summary>
    public int WorldWarEconomyRank { get; set; } = 10;
    /// <summary>The world tension (0..100) from which an incident between the blocs can happen.</summary>
    public float WorldIncidentTension { get; set; } = 45f;
    /// <summary>The weekly chance of an incident at that tension; it doubles towards a tension of 100.</summary>
    public float WorldIncidentChance { get; set; } = 0.02f;
    /// <summary>The grievance an incident leaves on both sides of the pair it happens between.</summary>
    public float WorldIncidentGrievance { get; set; } = 22f;
    /// <summary>How likely a great power's ally or bloc partner is to answer its call once the war has become a world war.</summary>
    public float BlocCallChance { get; set; } = 0.55f;
    /// <summary>What a world war costs every country outside it, in stability each week: fear, prices, refugees.</summary>
    public float WorldWarFearPerWeek { get; set; } = 0.0015f;
}

/// <summary>
/// The war between the blocs: rare, never out of nothing, and felt everywhere.
///
/// The defensive cascade already existed - attack a country and its allies, its guarantors,
/// its defence organisation and its overlord come in on its side - so a world war was always
/// possible; what was missing was anything that makes great powers walk towards one, and
/// anything that says it has happened. Three things here:
///
/// - World tension, worked out each week from what the great powers are doing to one another:
///   shooting, rivalry, open hostility, arming opposite sides of the same wars, crises between
///   them. Nothing is stored; it is read off the world as it stands.
/// - Incidents. When that tension is high, now and then something happens between a great power
///   and a country of the other bloc on its border - a shot-down aircraft, a seized ship, an
///   artillery exchange - and both remember it as a grievance. Grievance is one of the reasons
///   tension between two countries rises (Escalation): the crisis, the clashes and the border
///   war that may follow are the ordinary machinery's, and so is the chance that it all calms.
/// - The war itself. A war with great powers on both sides is named as what it is - the Third
///   World War, if the first two are anything to go by - the chronicle says so, the great powers
///   in it call their allies and bloc partners to their side, and every country outside it pays
///   in fear while it lasts. The music already knows a systemic war when it hears one.
/// </summary>
public static class WorldWar
{
    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same world-war pass, yielded between its phases.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(WorldWar))) yield break;
        foreach (var _ in RecogniseStages(c)) yield return 0;
        yield return 0;
        if (c.ElapsedDays % 7 != 0) yield break;
        foreach (var _ in FearStages(c)) yield return 0;
        yield return 0;
        Incidents(c);
    }

    /// <summary>How many wars one stage of the recognition works through.</summary>
    private const int RecogniseStageWars = 32;

    /// <summary>Whether any world war is being fought.</summary>
    public static bool Active(Campaign c)
    {
        if (c is null) return false;
        var open = Warfare.OpenWars(c);
        for (var i = 0; i < open.Count; i++)
            if (!open[i].Ended && open[i].WorldWarDay >= 0) return true;
        return false;
    }

    private static IEnumerable<int> RecogniseStages(Campaign c)
    {
        HashSet<int> powers = null;
        var openWars = Warfare.OpenWars(c);
        var count = openWars.Count;
        var seen = 0;
        for (var index = 0; index < count; index++)
        {
            var war = openWars[index];
            if (!war.Ended && !war.CivilConflict && war.WorldWarDay < 0 && war.BelligerentCount >= 2)
            {
                powers ??= WorldPowers(c);
                var attackers = CountPowersOnSide(war, true, powers);
                var defenders = CountPowersOnSide(war, false, powers);
                if (attackers != 0 && defenders != 0)
                {
                    var plural = attackers + defenders >= c.Defines.WorldWarMinimumPowers;
                    var crowd = war.BelligerentCount >= c.Defines.WorldWarMinimumBelligerents &&
                                attackers >= 2 && defenders >= 2;
                    if (plural || crowd)
                        foreach (var _ in DeclareStages(c, war, powers)) yield return 0;
                }
            }
            if (++seen % RecogniseStageWars == 0) yield return 0;
        }
    }

    private static int CountPowersOnSide(StrategicWar war, bool attacking, HashSet<int> powers)
    {
        var count = 0;
        foreach (var id in war.Side(attacking))
            if (powers.Contains(id)) count++;
        return count;
    }

    [ThreadStatic] private static List<Nation> _worldPowerCandidatesScratch;
    [ThreadStatic] private static HashSet<int> _worldPowerRichScratch;

    internal static HashSet<int> WorldPowers(Campaign c)
    {
        var strong = AllianceSystem.GreatPowers(c);
        var rankLimit = Math.Max(1, c.Defines.WorldWarEconomyRank);
        var candidates = _worldPowerCandidatesScratch ??= new List<Nation>(c.Nations.Count);
        candidates.Clear();
        var nations = c.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var n = nations[i];
            if (n.Population > 0 && n.Gdp > 0) candidates.Add(n);
        }
        candidates.Sort(static (a, b) =>
        {
            var cmp = b.Gdp.CompareTo(a.Gdp);
            return cmp != 0 ? cmp : a.Id.CompareTo(b.Id);
        });
        var rich = _worldPowerRichScratch ??= new HashSet<int>(Math.Min(rankLimit, candidates.Count));
        rich.Clear();
        var take = Math.Min(rankLimit, candidates.Count);
        for (var i = 0; i < take; i++) rich.Add(candidates[i].Id);

        var powers = new HashSet<int>();
        foreach (var id in strong)
            if (rich.Contains(id) || Deterrence.TierOf(c, c.NationById(id)) >= 1) powers.Add(id);
        candidates.Clear();
        rich.Clear();
        return powers;
    }

    /// <summary>
    /// Staged world-war proclamation: yields between bloc call-to-arms joins so a massive
    /// alliance cascade (measured at 520 ms in a single frame on seed 42 day 321) spreads
    /// smoothly across frames without altering join order or world fingerprint.
    /// </summary>
    private static IEnumerable<int> DeclareStages(Campaign c, StrategicWar war, HashSet<int> powers)
    {
        war.WorldWarDay = c.ElapsedDays;
        // The first two were in the twentieth century.
        var wwCount = 0;
        for (var i = 0; i < c.Wars.Count; i++)
            if (c.Wars[i].WorldWarDay >= 0) wwCount++;
        var number = 2 + wwCount;
        war.Name = Loc.Has($"world_war.name.{number}") ? $"world_war.name.{number}" : Loc.Format("world_war.name", number);
        c.Chronicle.Record(c, "world_war_began", war.AttackerId, war.DefenderId, subject: WarNames.Of(c, war));

        var sortedFriends = new List<int>();
        var friendSet = new HashSet<int>();

        // Each great power in it calls its own: allies, and partners in a defence organisation.
        for (var side = 0; side < 2; side++)
        {
            var attacking = side == 0;
            var leaders = new List<int>();
            foreach (var id in war.Side(attacking))
                if (powers.Contains(id)) leaders.Add(id);
            for (var li = 0; li < leaders.Count; li++)
            {
                var leaderId = leaders[li];
                var leader = c.NationById(leaderId);
                if (leader is null) continue;
                friendSet.Clear();
                var allies = c.Caches.Protectors.AlliesOf(c, leaderId);
                for (var ai = 0; ai < allies.Length; ai++) friendSet.Add(allies[ai]);
                foreach (var org in InternationalOrganizations.Memberships(c, leaderId))
                    if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true)
                        foreach (var member in org.Members)
                            if (member != leaderId) friendSet.Add(member);
                sortedFriends.Clear();
                foreach (var fid in friendSet) sortedFriends.Add(fid);
                sortedFriends.Sort();

                for (var fi = 0; fi < sortedFriends.Count; fi++)
                {
                    var friendId = sortedFriends[fi];
                    var friend = c.NationById(friendId);
                    if (friend is null || friend.IsHuman || friend.Population <= 0 || friend.Movement is not null) continue;
                    if (war.Involves(friendId)) continue;
                    // Not into a war against a friend of its own.
                    var alliedWithOpposite = false;
                    foreach (var enemy in war.Side(!attacking))
                        if (CampaignDiplomacy.Peek(c, friendId, enemy) is { Stance: DiplomaticStance.Allied })
                        { alliedWithOpposite = true; break; }
                    if (alliedWithOpposite) continue;
                    var opinion = CampaignDiplomacy.Peek(c, friendId, leaderId)?.Opinion ?? 0f;
                    var chance = c.Defines.BlocCallChance * M.Clamp(0.5f + opinion / 200f, 0.2f, 1f) *
                                 M.Clamp(friend.WarSupport + 0.3f, 0.3f, 1.2f);
                    if (MovementRules.Roll(c.Seed, friendId, c.ElapsedDays, 4401 + leaderId) >= chance) continue;
                    CampaignDiplomacy.JoinWar(c, war, friendId, defending: !attacking, attacking ? null : "declared_war_on_us");
                    if (Campaign.StageOverdue()) yield return 0;
                }
            }
        }
        c.MarkStrategicChange();
    }

    /// <summary>Every country outside a world war is afraid while it lasts.</summary>
    private static void Fear(Campaign c)
    {
        foreach (var _ in FearStages(c)) { }
    }

    /// <summary>The same weekly fear pass, yielded between batches of countries.</summary>
    private static IEnumerable<int> FearStages(Campaign c)
    {
        if (!Active(c)) yield break;
        var shock = M.Clamp(c.Defines.WorldWarFearPerWeek, 0f, 0.05f);
        var worldWars = new List<StrategicWar>();
        var open = Warfare.OpenWars(c);
        for (var i = 0; i < open.Count; i++)
            if (!open[i].Ended && open[i].WorldWarDay >= 0) worldWars.Add(open[i]);
        var ticked = 0;
        for (var ni = 0; ni < c.Nations.Count; ni++)
        {
            var nation = c.Nations[ni];
            if (++ticked % 16 == 0 || Campaign.StageOverdue()) yield return 0;
            if (nation.Population <= 0 || nation.Movement is not null) continue;
            var involved = false;
            for (var wi = 0; wi < worldWars.Count; wi++)
                if (worldWars[wi].Involves(nation.Id)) { involved = true; break; }
            if (involved) continue;
            nation.Stability = M.Clamp01(nation.Stability - shock);
        }
    }

    /// <summary>
    /// How close the great powers are to one another's throats, 0 to 100: the worst pair, and
    /// a third of the next and a sixth of the one after - a world with one bad rivalry is tense,
    /// one with three is tenser, and only great powers shooting at one another fill the scale.
    /// </summary>
    public static float Tension(Campaign c)
    {
        var pairs = Pairs(c);
        var tension = 0f;
        if (pairs.Count > 0) tension += pairs[0].Tension;
        if (pairs.Count > 1) tension += pairs[1].Tension / 3f;
        if (pairs.Count > 2) tension += pairs[2].Tension / 6f;
        return M.Clamp(tension, 0f, 100f);
    }

    /// <summary>Every pair of great powers with something between them, the most hostile first.</summary>
    public static List<(int A, int B, float Tension)> Pairs(Campaign c)
    {
        var powers = AllianceSystem.GreatPowers(c).OrderBy(id => id).ToList();
        var pairs = new List<(int A, int B, float Tension)>();
        for (var i = 0; i < powers.Count; i++)
            for (var j = i + 1; j < powers.Count; j++)
            {
                var pair = PairTension(c, powers[i], powers[j]);
                if (pair > 0f) pairs.Add((powers[i], powers[j], pair));
            }
        pairs.Sort((x, y) => y.Tension != x.Tension ? y.Tension.CompareTo(x.Tension) : (x.A, x.B).CompareTo((y.A, y.B)));
        return pairs;
    }

    /// <summary>
    /// One pair, 0 to 100: shooting at each other most of all, then rivalry, open hostility,
    /// arming opposite sides of the same wars and the crisis between them (Escalation).
    /// </summary>
    private static float PairTension(Campaign c, int a, int b)
    {
        var relation = CampaignDiplomacy.Peek(c, a, b);
        if (relation is null || relation.Stance == DiplomaticStance.Allied) return 0f;
        var first = c.NationById(a);
        var second = c.NationById(b);
        if (first is null || second is null) return 0f;
        var tension = 0f;
        if (relation.Stance == DiplomaticStance.AtWar) tension += 45f;
        if (relation.Stance == DiplomaticStance.Rival) tension += 12f;
        tension += M.Clamp(-relation.Opinion, 0f, 100f) * 0.15f;
        tension += MathF.Min(15f, ProxyWars.Opposition(c, first, second) * 5f);
        tension += M.Clamp(relation.Tension, 0f, 100f) * 0.13f;
        return M.Clamp(tension, 0f, 100f);
    }

    /// <summary>
    /// Now and then, when the world is tense enough: an incident between the most hostile pair
    /// of great powers - between one of them and the other itself, or a country of its bloc on
    /// the first's border - remembered as a grievance by both.
    /// </summary>
    private static void Incidents(Campaign c)
    {
        var tension = Tension(c);
        var floor = c.Defines.WorldIncidentTension;
        if (tension < floor) return;
        var chance = c.Defines.WorldIncidentChance * (1f + (tension - floor) / MathF.Max(1f, 100f - floor));
        if (MovementRules.Roll(c.Seed, 0, c.ElapsedDays, 4409) >= chance) return;

        var pairs = Pairs(c);
        if (pairs.Count == 0) return;
        for (var i = 0; i < Math.Min(pairs.Count, 8); i++)
        {
            var worst = (pairs[i].A, pairs[i].B);
            // Which of the two is the one at the other's border: try both ways round.
            foreach (var (power, rival) in new[] { (worst.A, worst.B), (worst.B, worst.A) })
            {
                var source = c.NationById(power);
                var front = FrontierPartner(c, power, rival) ?? c.NationById(rival);
                if (source is null || front is null || source.Id == front.Id) continue;
                var relation = CampaignDiplomacy.Get(c, power, front.Id);
                if (relation is null || relation.Stance is DiplomaticStance.AtWar or DiplomaticStance.Allied) continue;
                relation.GrievanceA = MathF.Max(relation.GrievanceA, 0f) + c.Defines.WorldIncidentGrievance;
                relation.GrievanceB = MathF.Max(relation.GrievanceB, 0f) + c.Defines.WorldIncidentGrievance;
                c.Chronicle.Record(c, "world_incident", power, front.Id);
                c.MarkStrategicChange();
                return;
            }
        }
    }

    /// <summary>A great power's allies and its partners in a defence organisation.</summary>
    private static List<int> Friends(Campaign c, int nationId)
    {
        var friends = new HashSet<int>();
        var allies = c.Caches.Protectors.AlliesOf(c, nationId);
        for (var i = 0; i < allies.Length; i++) friends.Add(allies[i]);
        foreach (var org in InternationalOrganizations.Memberships(c, nationId))
            if (InternationalOrganizations.Charter(org.CharterId)?.Defence == true)
                foreach (var member in org.Members)
                    if (member != nationId) friends.Add(member);
        var sorted = new List<int>(friends.Count);
        foreach (var id in friends) sorted.Add(id);
        sorted.Sort();
        return sorted;
    }

    /// <summary>The rival itself if it borders the power, or the largest of its friends that does.</summary>
    private static Nation FrontierPartner(Campaign c, int powerId, int rivalId)
    {
        var power = c.NationById(powerId);
        var rival = c.NationById(rivalId);
        if (power is null || rival is null) return null;
        if (CampaignDiplomacy.SharesLandBorder(c, power.MapIndex, rival.MapIndex)) return rival;
        return Friends(c, rivalId).Select(c.NationById)
            .Where(n => n is { Population: > 0, Movement: null } && CampaignDiplomacy.SharesLandBorder(c, power.MapIndex, n.MapIndex) &&
                        CampaignDiplomacy.Peek(c, powerId, n.Id) is not { Stance: DiplomaticStance.Allied })
            .OrderByDescending(n => n.Gdp).ThenBy(n => n.Id).FirstOrDefault();
    }
}
