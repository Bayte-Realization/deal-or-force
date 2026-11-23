using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// The chance a month that two movements who could share a cause join into one, and that a
    /// movement folds into the state it shares a people and a creed with (MovementMergers).
    /// 0 turns either off.
    /// </summary>
    public float MovementMergeChance { get; set; } = 0.05f;
    public float MovementAbsorbChance { get; set; } = 0.03f;
    /// <summary>The least opinion two movements, or a movement and a state, need of each other to join.</summary>
    public float MovementMergeOpinion { get; set; } = 0f;
    /// <summary>
    /// What a merger is worth and what it costs: the joined following grows by this share of the
    /// smaller's (a single front draws people neither drew alone), and the legitimacy is the
    /// average - a coalition is less sure of what it is than either part was.
    /// </summary>
    public float MovementMergeFollowingBonus { get; set; } = 0.15f;
}

/// <summary>
/// Movements do not only rise and fall; they join. The Kurdish parties made one front in 2015,
/// Syria's rebel brigades merged and merged again into Hayat Tahrir al-Sham, the PLO was a roof
/// over a dozen organisations - and a movement whose people already have a state of their own
/// often ends inside that state's own politics or its army. Both here, for any movement and any
/// country the same way:
/// - two landless movements sharing a people or a creed, with followings in the same regions and
///   no quarrel between them, may become one: the larger takes the smaller's cadres, money,
///   sponsors and recognition, the following grows, the legitimacy averages;
/// - a movement whose people are the people of a neighbouring or hosting state of the same creed
///   may fold into it: its money and fighters go to that state, which takes up its cause as a
///   claim of its own - and the government it was fighting knows exactly who carries it now.
/// </summary>
public static class MovementMergers
{
    [ThreadStatic] private static List<Nation> _activeScratch;
    [ThreadStatic] private static HashSet<int> _goneScratch;
    [ThreadStatic] private static HashSet<int> _arenaScratch;
    [ThreadStatic] private static List<Army> _transferScratch;
    [ThreadStatic] private static List<int> _tensionRegionsScratch;

    private static int CompareNationById(Nation a, Nation b) => a.Id.CompareTo(b.Id);

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same monthly merge/absorb pass, yielded between its halves.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (!c.StepOpensNewDay || c.ElapsedDays % 30 != 17 || !c.FeatureEnabled("movements")) yield break;
        var active = _activeScratch ??= new List<Nation>();
        active.Clear();
        var nations = c.Nations;
        for (var ni = 0; ni < nations.Count; ni++)
        {
            var n = nations[ni];
            if (n.Movement is { Dissolved: false, Landless: true, Exile: false } && !n.IsHuman)
                active.Add(n);
        }
        if (active.Count == 0) yield break;
        active.Sort(CompareNationById);
        if (c.Defines.MovementMergeChance > 0f && active.Count > 1)
        {
            foreach (var _ in MergeStages(c, active)) yield return 0;
            yield return 0;
        }
        if (c.Defines.MovementAbsorbChance > 0f)
        {
            var write = 0;
            for (var i = 0; i < active.Count; i++)
                if (active[i].Movement is { Dissolved: false }) active[write++] = active[i];
            if (write < active.Count) active.RemoveRange(write, active.Count - write);
            if (active.Count > 0) Absorb(c, active);
        }
    }

    private static IEnumerable<int> MergeStages(Campaign c, List<Nation> active)
    {
        var gone = _goneScratch ??= new HashSet<int>();
        gone.Clear();
        for (var i = 0; i < active.Count; i++)
        {
            if (i > 0 && (i % 16 == 0 || Campaign.StageOverdue())) yield return 0;
            for (var j = i + 1; j < active.Count; j++)
            {
                var a = active[i]; var b = active[j];
                if (gone.Contains(a.Id) || gone.Contains(b.Id)) continue;
                if (!Kindred(a, b) || !Overlap(a.Movement, b.Movement)) continue;
                if (MilitaryAccess.Hostile(c, a.Id, b.Id)) continue;
                if ((CampaignDiplomacy.Peek(c, a.Id, b.Id)?.Opinion ?? 0f) < c.Defines.MovementMergeOpinion) continue;
                if (MovementRules.Roll(c.Seed, a.Id * 1013 + b.Id, c.ElapsedDays, 0x4D52) >= c.Defines.MovementMergeChance) continue;
                var (big, small) = Weight(a) >= Weight(b) ? (a, b) : (b, a);
                Join(c, big, small);
                gone.Add(small.Id);
            }
        }
    }

    private static float Weight(Nation n)
    {
        var maxSupport = 0f;
        foreach (var v in n.Movement.Support.Values)
            if (v > maxSupport) maxSupport = v;
        return n.Movement.Cadres + maxSupport * 100_000f;
    }

    /// <summary>The same people or the same creed: what makes a common front thinkable.</summary>
    private static bool Kindred(Nation a, Nation b)
    {
        var ac = a.FoundedCultures;
        var bc = b.FoundedCultures;
        for (var i = 0; i < ac.Count; i++)
        {
            var cult = ac[i];
            for (var j = 0; j < bc.Count; j++)
                if (string.Equals(cult, bc[j], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return !string.IsNullOrEmpty(a.Ideology) && string.Equals(a.Ideology, b.Ideology, StringComparison.OrdinalIgnoreCase) &&
               (ac.Count == 0 || bc.Count == 0);
    }

    private static bool Overlap(MovementState a, MovementState b)
    {
        foreach (var pair in a.Support)
            if (pair.Value >= 0.03f && b.Support.GetValueOrDefault(pair.Key) >= 0.03f) return true;
        return false;
    }

    private static void ReassignArmies(Campaign c, int fromNationId, int toNationId)
    {
        var transfer = _transferScratch ??= new List<Army>();
        transfer.Clear();
        var armies = c.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var army = armies[i];
            if (army.NationId == fromNationId) transfer.Add(army);
        }
        for (var i = 0; i < transfer.Count; i++)
        {
            transfer[i].NationId = toNationId;
            transfer[i].ArmyGroupId = -1;
        }
    }

    private static void Join(Campaign c, Nation big, Nation small)
    {
        var to = big.Movement; var from = small.Movement;
        to.Cadres += from.Cadres;
        big.Treasury += MathF.Max(0f, small.Treasury);
        big.ManpowerReserve += small.ManpowerReserve;
        foreach (var (region, share) in from.Support)
            to.Support[region] = M.Clamp01(MathF.Max(to.Support.GetValueOrDefault(region), share) +
                                           MathF.Min(to.Support.GetValueOrDefault(region), share) * c.Defines.MovementMergeFollowingBonus);
        foreach (var (sponsor, level) in from.SponsoredBy)
            to.SponsoredBy[sponsor] = MathF.Max(to.SponsoredBy.GetValueOrDefault(sponsor), level);
        foreach (var recogniser in from.RecognisedBy) to.RecognisedBy.Add(recogniser);
        to.Legitimacy = (to.Legitimacy + from.Legitimacy) * 0.5f;
        // Its formations change their badges, not their places.
        ReassignArmies(c, small.Id, big.Id);
        c.Chronicle.Record(c, "movement_merged", big.Id, small.Id, small.DisplayName);
        MovementFall.Dissolve(c, small, Loc.Format("It has joined {0}: one front instead of two.", big.DisplayName), record: false);
        to.LastReport = Loc.Format("{0} has joined us: its people, money and fighters are ours now.", small.DisplayName);
        DiagnosticLogs.Write("simulation", $"MOVEMENT_MERGED {small.Tag} into {big.Tag}");
    }

    private static bool InAnyOpenWar(Campaign c, int nationId)
    {
        var openWars = Warfare.OpenWars(c);
        for (var i = 0; i < openWars.Count; i++)
        {
            var w = openWars[i];
            if (!w.Ended && w.Involves(nationId)) return true;
        }
        return false;
    }

    private static bool HasPrimaryCulture(Nation candidate, List<string> foundedCultures)
    {
        for (var i = 0; i < foundedCultures.Count; i++)
            if (DemographyCatalog.IsPrimary(candidate.Tag, foundedCultures[i])) return true;
        return false;
    }

    private static bool TouchesAnySupport(Campaign c, Nation candidate, Dictionary<int, float> support)
    {
        foreach (var region in support.Keys)
            if (TerritorialCoherence.TouchesOwnedGround(c, candidate, region)) return true;
        return false;
    }

    private static void Absorb(Campaign c, List<Nation> active)
    {
        var arena = _arenaScratch ??= new HashSet<int>();
        var tensionRegions = _tensionRegionsScratch ??= new List<int>();
        var activeNations = c.ActiveNations;
        var byMapIndex = c.NationsByMapIndex();
        for (var ai = 0; ai < active.Count; ai++)
        {
            var movement = active[ai];
            if (movement.Movement is not { Dissolved: false } state || movement.FoundedCultures.Count == 0) continue;
            if (InAnyOpenWar(c, movement.Id)) continue;   // not in the middle of its own war
            var host = Movements.HostOf(c, movement);
            // The governments it stands against: whoever rules the ground its following lives on.
            // A movement never folds into the state it is fighting - the TPLF does not become
            // Ethiopia's army by agreeing with it about nothing.
            arena.Clear();
            foreach (var (region, share) in state.Support)
            {
                if (share < 0.03f) continue;
                var ground = c.StateByMapIndex(region);
                if (ground is not null) arena.Add(ground.NationMapIndex);
            }
            // A movement with no following anywhere has no cause to hand over.
            if (arena.Count == 0) continue;
            Nation home = null;
            for (var ni = 0; ni < activeNations.Count; ni++)
            {
                var state2 = activeNations[ni];
                if (state2.Movement is not null || state2.Population <= 0 || state2.IsHuman || arena.Contains(state2.MapIndex)) continue;
                if (!HasPrimaryCulture(state2, movement.FoundedCultures)) continue;
                if (!string.Equals(state2.Ideology, movement.Ideology, StringComparison.OrdinalIgnoreCase) && state2 != host) continue;
                if ((CampaignDiplomacy.Peek(c, state2.Id, movement.Id)?.Opinion ?? 0f) < c.Defines.MovementMergeOpinion) continue;
                // Its people's own state, next to the ground the movement is about, or the one hosting it.
                var near = state2 == host || TouchesAnySupport(c, state2, state.Support);
                if (!near) continue;
                home = state2;
                break;
            }
            if (home is null) continue;
            if (MovementRules.Roll(c.Seed, movement.Id * 389 + home.Id, c.ElapsedDays, 0x4142) >= c.Defines.MovementAbsorbChance) continue;
            home.Treasury += MathF.Max(0f, movement.Treasury);
            home.ManpowerReserve += state.Cadres + movement.ManpowerReserve;
            ReassignArmies(c, movement.Id, home.Id);
            // The cause becomes the state's own: every government the movement wanted ground from
            // now has a neighbour that wants it, and remembers who took the cause up.
            tensionRegions.Clear();
            foreach (var pair in state.Support)
                if (pair.Value >= 0.1f) tensionRegions.Add(pair.Key);
            for (var ri = 0; ri < tensionRegions.Count; ri++)
            {
                var region2 = c.StateByMapIndex(tensionRegions[ri]);
                var owner = region2 is null ? null : byMapIndex.GetValueOrDefault((ushort)region2.NationMapIndex);
                if (owner is null || owner.Id == home.Id) continue;
                if (CampaignDiplomacy.Get(c, owner.Id, home.Id) is { } wary)
                    wary.Tension = MathF.Min(100f, wary.Tension + 10f);
            }
            c.Chronicle.Record(c, "movement_absorbed", home.Id, movement.Id, movement.DisplayName);
            MovementFall.Dissolve(c, movement, Loc.Format("Its people and its fighters have gone over to {0}.", home.DisplayName), record: false);
            DiagnosticLogs.Write("simulation", $"MOVEMENT_ABSORBED {movement.Tag} into {home.Tag}");
        }
    }
}
