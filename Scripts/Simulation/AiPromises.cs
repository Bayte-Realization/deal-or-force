using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Public promises made by the governments the player does not run - and by any cabinet the
/// player has handed to the AI (Delegation), which already takes decisions through the same
/// portfolio.
///
/// A promise is a gamble on your own performance, and both the choice of pledge and the
/// appetite for making it read how far the promised outcome is from being true
/// (<see cref="ScriptedTrigger.Shortfall"/>) alongside the government's character
/// (<see cref="AiTemperament"/>): a cunning cabinet pledges only what is within reach of its
/// deadline and steers its own policy toward keeping what it promised.
/// </summary>
public static class AiPromises
{
    /// <summary>One pass a day; each country keeps its own day of the week, the spread the
    /// rest of the cabinet already uses, so the whole world does not promise on one morning.</summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same pass, yielded between cabinets so a promise-heavy morning does not
    /// land in one frame.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || !campaign.FeatureEnabled("government_promises")) yield break;
        var day = campaign.ElapsedDays;
        var scanned = 0;
        foreach (var nation in campaign.Nations)
        {
            if (++scanned % 16 == 0) yield return 0;
            // The player's own government decides for itself, unless its cabinet has been
            // handed to the AI (Delegation) - then it speaks like any other cabinet, because
            // it already spends like one. Movements are not a government any electorate holds
            // to a promise, and an entity without residents has nobody to promise anything to.
            if (!Delegation.RunByAi(nation) || nation.Movement is not null || nation.Population <= 0)
                continue;
            if ((day + nation.Id) % 7 != 0) continue;
            WorkTowardOpenPromises(campaign, nation);
            if (nation.Promises.Count >= Math.Max(1, campaign.Defines.MaxConcurrentPromises)) continue;
            var (promise, shortfall) = Shortest(campaign, nation);
            if (promise is null) continue;
            var appetite = Appetite(campaign, nation, promise.Value, shortfall);
            if (appetite <= 0f || !campaign.Random.Chance(appetite)) continue;
            new MakePromiseCommand { IssuerId = nation.Id, PromiseId = promise.Value.Id }.Apply(campaign);
            Count("promise_made");
        }
    }

    private static void Count(string what) =>
        AiStatecraft.Tally[what] = AiStatecraft.Tally.GetValueOrDefault(what) + 1;

    /// <summary>
    /// The available promise closest to fulfilment (<see cref="ScriptedTrigger.Shortfall"/>),
    /// with ties broken by shortest deadline and then catalogue order so two machines of one
    /// seed choose identically. Availability is the mechanic's own question
    /// (<see cref="GovernmentPromises.BlockReason"/>): whatever refuses the player refuses the AI.
    /// </summary>
    public static (PromiseDefinition? Promise, float Shortfall) Shortest(Campaign campaign, Nation nation)
    {
        PromiseDefinition? best = null;
        var bestShortfall = float.PositiveInfinity;
        foreach (var definition in PromiseCatalog.All)
        {
            if (GovernmentPromises.BlockReason(campaign, nation, definition).Length > 0) continue;
            var shortfall = definition.Check?.Shortfall(campaign, nation) ?? float.PositiveInfinity;
            if (!float.IsFinite(shortfall)) continue;
            if (best is null ||
                shortfall + 0.0001f < bestShortfall ||
                (MathF.Abs(shortfall - bestShortfall) <= 0.0001f && definition.DurationDays < best.Value.DurationDays))
            {
                best = definition;
                bestShortfall = shortfall;
            }
        }
        return (best, bestShortfall);
    }

    /// <summary>
    /// How likely this government is to promise, scaled by how close the outcome already is.
    ///
    /// An outcome that is already met (<paramref name="shortfall"/> = 0) gets full appetite;
    /// a small gap within the deadline's realistic horizon is scaled by closeness; a gap beyond
    /// what the deadline can close (narrowed further by <see cref="AiTemperament.CunningOf"/>)
    /// returns zero so governments do not make impossible pledges.
    /// </summary>
    public static float Appetite(Campaign campaign, Nation nation, PromiseDefinition definition, float shortfall)
    {
        if (!float.IsFinite(shortfall) || shortfall < 0f) return 0f;
        var baseAppetite = M.Clamp(
            M.Max(0f, campaign.Defines.AiPromiseAppetite) *
            AiTemperament.OpportunismOf(campaign, nation) *
            AiTemperament.VolatilityOf(campaign, nation) /
            M.Max(0.05f, AiTemperament.ForesightOf(campaign, nation)),
            0f, .5f);
        if (shortfall <= 0.0001f) return baseAppetite;

        var years = MathF.Max(0.25f, definition.DurationDays / 365f);
        var cunning = MathF.Max(0.25f, AiTemperament.CunningOf(campaign, nation));
        var ceiling = 0.075f * years / MathF.Sqrt(cunning);
        if (shortfall > ceiling) return 0f;
        var closeness = 1f - 0.75f * (shortfall / MathF.Max(0.0001f, ceiling));
        return M.Clamp(baseAppetite * closeness, 0f, .5f);
    }

    /// <summary>
    /// A cabinet that has made a public pledge uses its own ordinary levers to help deliver it.
    /// </summary>
    private static void WorkTowardOpenPromises(Campaign campaign, Nation nation)
    {
        if (nation.Promises.Count == 0) return;
        foreach (var active in nation.Promises)
        {
            switch (active.Id)
            {
                case "mend_the_war_scars":
                    foreach (var state in campaign.StatesOf(nation).OrderByDescending(s => s.WarDamage))
                    {
                        if (state.WarDamage <= 0.05f) break;
                        if (WarDamage.RebuildRefusal(campaign, nation, state).Length == 0)
                        {
                            new RebuildRegionCommand { IssuerId = nation.Id, StateMapIndex = state.MapIndex }.Apply(campaign);
                            break;
                        }
                    }
                    break;
                case "settle_the_schools":
                    if (nation.Treasury > nation.Gdp * 0.02f)
                        nation.Literacy = M.Clamp01(nation.Literacy + 0.002f);
                    break;
                case "raise_the_floor":
                case "restore_order":
                    if (nation.Treasury > nation.Gdp * 0.02f)
                        nation.Stability = M.Clamp01(nation.Stability + 0.002f);
                    break;
                case "refill_the_treasury":
                    ModifierSystem.Grant(campaign, nation, "revenue_compliance_audit", 90);
                    break;
            }
        }
    }
}
