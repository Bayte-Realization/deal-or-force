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
    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same monthly merge/absorb pass, yielded between its halves.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (!c.StepOpensNewDay || c.ElapsedDays % 30 != 17 || !c.FeatureEnabled("movements")) yield break;
        var active = c.Nations.Where(n => n.Movement is { Dissolved: false, Landless: true, Exile: false } && !n.IsHuman)
            .OrderBy(n => n.Id).ToList();
        if (c.Defines.MovementMergeChance > 0f)
        {
            Merge(c, active);
            yield return 0;
        }
        if (c.Defines.MovementAbsorbChance > 0f) Absorb(c, active.Where(n => n.Movement is { Dissolved: false }).ToList());
    }

    private static void Merge(Campaign c, List<Nation> active)
    {
        var gone = new HashSet<int>();
        for (var i = 0; i < active.Count; i++)
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

    private static float Weight(Nation n) => n.Movement.Cadres + n.Movement.Support.Values.DefaultIfEmpty(0f).Max() * 100_000f;

    /// <summary>The same people or the same creed: what makes a common front thinkable.</summary>
    private static bool Kindred(Nation a, Nation b)
    {
        if (a.FoundedCultures.Intersect(b.FoundedCultures, StringComparer.OrdinalIgnoreCase).Any()) return true;
        return !string.IsNullOrEmpty(a.Ideology) && string.Equals(a.Ideology, b.Ideology, StringComparison.OrdinalIgnoreCase) &&
               (a.FoundedCultures.Count == 0 || b.FoundedCultures.Count == 0);
    }

    private static bool Overlap(MovementState a, MovementState b) =>
        a.Support.Any(pair => pair.Value >= 0.03f && b.Support.GetValueOrDefault(pair.Key) >= 0.03f);

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
        foreach (var army in c.Armies.Where(x => x.NationId == small.Id)) { army.NationId = big.Id; army.ArmyGroupId = -1; }
        c.Chronicle.Record(c, "movement_merged", big.Id, small.Id, small.DisplayName);
        MovementFall.Dissolve(c, small, Loc.Format("It has joined {0}: one front instead of two.", big.DisplayName), record: false);
        to.LastReport = Loc.Format("{0} has joined us: its people, money and fighters are ours now.", small.DisplayName);
        DiagnosticLogs.Write("simulation", $"MOVEMENT_MERGED {small.Tag} into {big.Tag}");
    }

    private static void Absorb(Campaign c, List<Nation> active)
    {
        foreach (var movement in active)
        {
            if (movement.Movement is not { Dissolved: false } state || movement.FoundedCultures.Count == 0) continue;
            if (c.Wars.Any(w => !w.Ended && w.Involves(movement.Id))) continue;   // not in the middle of its own war
            var host = Movements.HostOf(c, movement);
            // The governments it stands against: whoever rules the ground its following lives on.
            // A movement never folds into the state it is fighting - the TPLF does not become
            // Ethiopia's army by agreeing with it about nothing.
            var arena = new HashSet<int>();
            foreach (var (region, share) in state.Support)
            {
                if (share < 0.03f) continue;
                var ground = c.States.Find(s => s.MapIndex == region);
                if (ground is not null) arena.Add(ground.NationMapIndex);
            }
            // A movement with no following anywhere has no cause to hand over.
            if (arena.Count == 0) continue;
            Nation home = null;
            foreach (var state2 in c.Nations)
            {
                if (state2.Movement is not null || state2.Population <= 0 || state2.IsHuman || arena.Contains(state2.MapIndex)) continue;
                if (!movement.FoundedCultures.Any(p => DemographyCatalog.IsPrimary(state2.Tag, p))) continue;
                if (!string.Equals(state2.Ideology, movement.Ideology, StringComparison.OrdinalIgnoreCase) && state2 != host) continue;
                if ((CampaignDiplomacy.Peek(c, state2.Id, movement.Id)?.Opinion ?? 0f) < c.Defines.MovementMergeOpinion) continue;
                // Its people's own state, next to the ground the movement is about, or the one hosting it.
                var near = state2 == host || state.Support.Keys.Any(region => TerritorialCoherence.TouchesOwnedGround(c, state2, region));
                if (!near) continue;
                home = state2;
                break;
            }
            if (home is null) continue;
            if (MovementRules.Roll(c.Seed, movement.Id * 389 + home.Id, c.ElapsedDays, 0x4142) >= c.Defines.MovementAbsorbChance) continue;
            home.Treasury += MathF.Max(0f, movement.Treasury);
            home.ManpowerReserve += state.Cadres + movement.ManpowerReserve;
            foreach (var army in c.Armies.Where(x => x.NationId == movement.Id)) { army.NationId = home.Id; army.ArmyGroupId = -1; }
            // The cause becomes the state's own: every government the movement wanted ground from
            // now has a neighbour that wants it, and remembers who took the cause up.
            foreach (var region in state.Support.Where(pair => pair.Value >= 0.1f).Select(pair => pair.Key))
            {
                var region2 = c.States.Find(s => s.MapIndex == region);
                var owner = region2 is null ? null : c.NationsByMapIndex().GetValueOrDefault((ushort)region2.NationMapIndex);
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
