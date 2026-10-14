using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The momentum (0..1) from which a movement can call a general strike, march on a capital, and run its own administration.</summary>
    public float MovementStrikeMomentum { get; set; } = 0.4f;
    public float MovementMarchMomentum { get; set; } = 0.65f;
    public float MovementParallelMomentum { get; set; } = 0.5f;
    /// <summary>The support a region needs before the movement administers it instead of the state.</summary>
    public float MovementParallelSupport { get; set; } = 0.6f;
    /// <summary>What a parallel administration takes each week, as a share of the region's output, from the state to the movement.</summary>
    public float MovementParallelLevy { get; set; } = 0.0006f;
    /// <summary>How long a general strike lasts, in days.</summary>
    public int MovementStrikeDays { get; set; } = 42;
    /// <summary>The support it takes in the host's capital region to march on the capital.</summary>
    public float MovementMarchSupport { get; set; } = 0.5f;
}

/// <summary>
/// A movement that has grown into a force: general strikes, a march on the capital, and an
/// administration of its own where the state no longer governs.
///
/// Momentum is what the movement has become, 0 to 1: how much of its own people follows it, how
/// seriously the world takes it, how many countries recognise it, how many organisers it has and
/// how much money comes in. A movement with momentum can do what a small one cannot:
///
/// - Call a general strike in the country where its people live: six weeks in which the economy
///   produces less, taxes go uncollected and the government loses standing, while the movement
///   gains it. A government of the people tends to give ground afterwards; a hard one cracks down.
/// - March on the capital, once the capital region itself is with it. If the government is weak
///   enough the march brings it down - a political movement takes power and its creed with it; a
///   national one wins the concessions and the hearing it marched for. If not, it is broken up.
/// - Administer the regions where most of its people follow it: courts, taxes and services of its
///   own, paid for by a share of the region's output that no longer reaches the state.
///
/// The two actions are on the movement's own panel; governments nobody plays use them when they
/// can, and the administration runs by itself.
/// </summary>
public static class MovementPower
{
    /// <summary>
    /// What the movement has become, 0 to 1, from the current authoritative inputs.
    /// Panel reads must not fix the value for the rest of a day: recognition, recruiting
    /// and campaigning can change those inputs while paused, before another command.
    /// </summary>
    public static float Momentum(Campaign c, Nation nation)
    {
        if (c is null || nation?.Movement is not { Dissolved: false } state) return 0f;
        var definition = Movements.DefinitionOf(nation);
        long people = 0;
        var following = 0f;
        if (definition is not null)
            foreach (var region in c.States)
            {
                var own = MovementRules.PeopleOf(region, definition, out var faithful) + faithful;
                if (own <= 0) continue;
                people += own;
                following += own * state.Support.GetValueOrDefault(region.MapIndex);
            }
        var breadth = people > 0 ? following / people : 0f;
        var momentum = 0.35f * breadth + 0.25f * M.Clamp01(state.Legitimacy) + 0.15f * MathF.Min(1f, state.RecognisedBy.Count / 8f) +
                       0.15f * MathF.Min(1f, state.Cadres / 20000f) + 0.10f * MathF.Min(1f, state.IncomeWeekly * 52f / 0.5f);
        return M.Clamp01(momentum);
    }

    /// <summary>The country where most of its following lives - where a strike would be called.</summary>
    public static Nation StrikeTarget(Campaign c, Nation nation)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (state is null || definition is null) return null;
        var byHost = new Dictionary<int, float>();
        foreach (var region in c.States)
        {
            var support = state.Support.GetValueOrDefault(region.MapIndex);
            if (support < 0.3f || region.NationMapIndex == nation.MapIndex) continue;
            var own = MovementRules.PeopleOf(c, region, definition, out var faithful) + faithful;
            byHost[region.NationMapIndex] = byHost.GetValueOrDefault(region.NationMapIndex) + own * support;
        }
        if (byHost.Count == 0) return null;
        var best = byHost.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key).First().Key;
        return c.Nations.Find(n => n.MapIndex == best && n.Movement is null);
    }

    /// <summary>The country whose capital region follows it most - where it would march.</summary>
    public static (Nation Host, ScenarioState Capital, float Support) MarchTarget(Campaign c, Nation nation)
    {
        var state = nation.Movement;
        if (state is null) return (null, null, 0f);
        (Nation, ScenarioState, float) best = (null, null, 0f);
        foreach (var host in c.Nations)
        {
            if (host.Movement is not null || host.Population <= 0 || host.Id == nation.Id) continue;
            var capital = c.States.Find(s => s.MapIndex == host.CapitalStateMapIndex);
            if (capital is null || capital.NationMapIndex != host.MapIndex) continue;
            var support = state.Support.GetValueOrDefault(capital.MapIndex);
            if (support > best.Item3) best = (host, capital, support);
        }
        return best;
    }

    internal static string Refusal(Campaign c, Nation nation, string action)
    {
        var defines = c.Defines;
        var momentum = Momentum(c, nation);
        var definition = Movements.DefinitionOf(nation);
        if (definition?.Has("for_hire") == true) return Loc.Get("A company for hire does not lead a people.");
        switch (action)
        {
            case "strike":
            {
                if (momentum < defines.MovementStrikeMomentum)
                    return Loc.Format("Momentum is {0}; a general strike takes {1}.", Fmt.Percent(momentum), Fmt.Percent(defines.MovementStrikeMomentum));
                var target = StrikeTarget(c, nation);
                if (target is null) return Loc.Get("No country holds enough of its following to stop.");
                if (target.Id == nation.Id) return Loc.Get("A movement does not strike against itself.");
                return "";
            }
            case "march":
            {
                if (momentum < defines.MovementMarchMomentum)
                    return Loc.Format("Momentum is {0}; a march on the capital takes {1}.", Fmt.Percent(momentum), Fmt.Percent(defines.MovementMarchMomentum));
                var (host, capital, support) = MarchTarget(c, nation);
                if (host is null || capital is null) return Loc.Get("No capital would follow it into the streets.");
                if (support < defines.MovementMarchSupport)
                    return Loc.Format("Support in {0} is {1}; a march takes {2}.", capital.DisplayName, Fmt.Percent(support), Fmt.Percent(defines.MovementMarchSupport));
                if (CampaignDiplomacy.AtWar(c, host.Id, nation.Id)) return Loc.Get("It is at war with that government: the streets are not where this is decided.");
                return "";
            }
        }
        return "";
    }

    internal static void Do(Campaign c, Nation nation, string action)
    {
        var state = nation.Movement;
        var defines = c.Defines;
        switch (action)
        {
            case "strike":
            {
                var target = StrikeTarget(c, nation);
                if (target is null) return;
                ModifierSystem.Grant(c, target, "general_strike", Math.Max(7, defines.MovementStrikeDays));
                state.Legitimacy = M.Clamp01(state.Legitimacy + 0.04f);
                state.Heat = M.Clamp01(state.Heat + 0.12f);
                // A government of the people gives ground after a strike; a hard one answers it.
                var authority = PoliticsCore.IdeologyOf(target).Authority;
                foreach (var region in c.States)
                    if (region.NationMapIndex == target.MapIndex && state.Support.ContainsKey(region.MapIndex))
                        state.Support[region.MapIndex] = M.Clamp01(state.Support[region.MapIndex] + (authority < 0f ? 0.03f : -0.02f));
                state.LastReport = Loc.Format("A general strike brings {0} to a standstill.", target.DisplayName);
                c.Chronicle.Record(c, "general_strike", nation.Id, target.Id);
                break;
            }
            case "march":
            {
                var (host, capital, support) = MarchTarget(c, nation);
                if (host is null || capital is null) return;
                // The weaker the government and the fuller the streets, the likelier it falls.
                var chance = M.Clamp(support * (1f - host.Stability) * 1.4f + state.Legitimacy * 0.15f, 0.05f, 0.85f);
                var roll = MovementRules.Roll(c.Seed, nation.Id, c.ElapsedDays, 4711);
                if (roll < chance) Topple(c, nation, host, capital);
                else
                {
                    state.Cadres *= 0.6f;
                    state.Heat = M.Clamp01(state.Heat + 0.3f);
                    state.Support[capital.MapIndex] = M.Clamp01(support - 0.1f);
                    host.Stability = M.Clamp01(host.Stability - 0.03f);
                    state.LastReport = Loc.Format("The march on {0} was broken up.", capital.DisplayName);
                    c.Chronicle.Record(c, "march_failed", nation.Id, host.Id, capital.DisplayName);
                }
                break;
            }
        }
        c.MarkStrategicChange();
    }

    /// <summary>
    /// The government falls to the street. A political movement takes power and its creed with it;
    /// a national one - whose quarrel is with the borders rather than the government - wins a
    /// hearing: standing, its people's trust, and the government's fear.
    /// </summary>
    private static void Topple(Campaign c, Nation nation, Nation host, ScenarioState capital)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        host.Stability = M.Clamp01(host.Stability - 0.12f);
        host.WarSupport = M.Clamp01(host.WarSupport - 0.1f);
        var national = definition is not null && definition.Homeland.Count > 0;
        if (!national && IdeologyCatalog.All.Any(i => string.Equals(i.Id, nation.Ideology, StringComparison.OrdinalIgnoreCase)))
        {
            host.Ideology = nation.Ideology;
            host.PoliticsChangedDay = c.ElapsedDays;
        }
        Coups.RegisterOutcome(c, host, "Popular uprising");
        state.Legitimacy = M.Clamp01(state.Legitimacy + (national ? 0.15f : 0.25f));
        foreach (var key in state.Support.Keys.ToList()) state.Support[key] = M.Clamp01(state.Support[key] + 0.05f);
        state.Heat = M.Clamp01(state.Heat - 0.3f);
        state.LastReport = Loc.Format("The government in {0} has fallen to the street.", capital.DisplayName);
        c.Chronicle.Record(c, national ? "march_concessions" : "march_toppled", nation.Id, host.Id, capital.DisplayName);
        NationalModifiers.Invalidate(host);
    }

    /// <summary>
    /// Each week: the movements' own administrations take their levy, and governments nobody
    /// plays call a strike or a march when the movement can and the moment is right.
    /// </summary>
    public static void Tick(Campaign c)
    {
        if (c is null || !c.FeatureEnabled("movements") || !MechanicFeatures.SystemEnabled(c, nameof(MovementPower))) return;
        if (c.ElapsedDays % 7 != 3) return;
        var defines = c.Defines;
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not { Dissolved: false } state) continue;
            var momentum = Momentum(c, nation);
            if (momentum >= defines.MovementParallelMomentum) Administer(c, nation, state);
            if (nation.IsHuman) continue;
            foreach (var action in new[] { "march", "strike" })
            {
                if (MovementActions.Refusal(c, nation, action, -1, -1).Length > 0) continue;
                // A strike or a march is a decision, not a habit: one chance in four a week once possible.
                if (MovementRules.Roll(c.Seed, nation.Id, c.ElapsedDays, action == "march" ? 4721 : 4722) > 0.25f) continue;
                new MovementActionCommand { IssuerId = nation.Id, Action = action }.Apply(c);
                break;
            }
        }
    }

    private static void Administer(Campaign c, Nation nation, MovementState state)
    {
        var defines = c.Defines;
        var levy = 0f;
        foreach (var region in c.States)
        {
            if (region.NationMapIndex == nation.MapIndex) continue;
            if (state.Support.GetValueOrDefault(region.MapIndex) < defines.MovementParallelSupport) continue;
            var host = c.Nations.Find(n => n.MapIndex == region.NationMapIndex);
            if (host is null || host.Movement is not null) continue;
            // The region's share of the country's output, by its people.
            var output = MathF.Max(0f, host.Gdp) * region.Population / MathF.Max(1f, host.Population);
            var take = output * M.Clamp01(defines.MovementParallelLevy);
            if (take <= 0f || host.Treasury < take) continue;
            host.Treasury -= take;
            levy += take;
            region.Stability = M.Clamp01(region.Stability - 0.002f);
        }
        if (levy <= 0f) return;
        nation.Treasury += levy;
        state.IncomeWeekly += levy;
    }
}
