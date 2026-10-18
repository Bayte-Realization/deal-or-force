using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Whether a country can end by simply running out of people. Off by default: the rule
    /// removes a state from the map, and a rule like that enters a campaign only once its
    /// neighbours' inheritance of the empty ground has been decided and measured.
    /// </summary>
    public bool DemographicExtinctionEnabled { get; set; } = false;

    /// <summary>People below which a country is counted as collapsing, review after review.</summary>
    public float DemographicExtinctionFloor { get; set; } = 20000f;

    /// <summary>Weekly reviews under the floor before the collapse is called final.</summary>
    public int DemographicExtinctionReviews { get; set; } = 26;
}

public sealed partial class Nation
{
    /// <summary>
    /// Consecutive weekly reviews this country has spent under the extinction floor. Zeroed the
    /// moment it climbs back out, so a famine that recovers leaves no residue - a country is
    /// dying only while it keeps dying.
    /// </summary>
    public int CollapseReviews;

    /// <summary>Set once the collapse has been called final, so it is announced exactly once.</summary>
    public bool CollapseAnnounced;
}

/// <summary>
/// The end a country meets when nobody is left, rather than when somebody conquers it.
///
/// DemographicCycle already counts the births, the deaths and the people who leave; this reads
/// their sum and asks the only question they cannot answer themselves - whether there is still
/// a country at the end of them. Detection and announcement live here; what happens to the
/// empty ground is deliberately not here yet, because that is a decision about inheritance
/// (see docs/DEMOGRAPHIC_EXTINCTION_DESIGN_RU.md) and must not be smuggled in inside a counter.
///
/// The decision itself is a pure function of a nation and the defines, so a test can hold a
/// dying country in its hands without building a world around it.
/// </summary>
public static class DemographicExtinction
{
    /// <summary>
    /// One weekly review of one country: how many weeks in a row it has now spent under the
    /// floor. Pure - the caller stores the answer - so the rule and its test cannot drift apart.
    /// </summary>
    public static int Review(Nation nation, Defines defines, int people)
    {
        if (nation is null || defines is null) return 0;
        if (!defines.DemographicExtinctionEnabled) return 0;
        if (people >= defines.DemographicExtinctionFloor) return 0;
        return nation.CollapseReviews + 1;
    }

    /// <summary>Whether the collapse has been going on long enough to be called final.</summary>
    public static bool IsFinal(Nation nation, Defines defines) =>
        nation is not null && defines is not null && defines.DemographicExtinctionEnabled &&
        nation.CollapseReviews >= Math.Max(1, defines.DemographicExtinctionReviews);

    /// <summary>Whether a country is currently under the floor and counting the weeks.</summary>
    public static bool IsCollapsing(Nation nation) => nation is not null && nation.CollapseReviews > 0;

    /// <summary>
    /// The weekly pass. Movements are skipped - they already have their own way to end
    /// (MovementFall) - and so is whoever a person plays: a player's country dying of old age
    /// under them, with no hand on the levers, is a cutscene, not a game.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign is null || !campaign.Defines.DemographicExtinctionEnabled) return;
        if (campaign.ElapsedDays % 7 != 4) return;
        foreach (var nation in campaign.Nations)
        {
            if (nation is null || nation.IsHuman || nation.Movement is not null) continue;
            nation.CollapseReviews = Review(nation, campaign.Defines, (int)nation.Population);
            if (!IsFinal(nation, campaign.Defines)) continue;
            if (!nation.CollapseAnnounced)
            {
                nation.CollapseAnnounced = true;
                DiagnosticLogs.Write("simulation",
                    $"DEMOGRAPHIC_COLLAPSE nation={nation.Tag} people={nation.Population:N0} " +
                    $"weeks={nation.CollapseReviews} day={campaign.ElapsedDays}");
                campaign.Chronicle.Record(campaign, "demographic_collapse", nation.Id);
            }
            Relinquish(campaign, nation);
        }
    }

    /// <summary>
    /// The ground a finally collapsing country empties out of, region by region.
    ///
    /// A region whose last inhabitant is gone becomes nobody's: its provinces lose owner and
    /// controller, and the region's own record follows its ground into the unclaimed ledger
    /// the map draws as ruin. The capital is the exception that makes this a death rather
    /// than a disappearance - the seat and its region stay the country's own while a single
    /// inhabitant is left anywhere, so the country dies in its capital. When even that last
    /// count reaches zero the seat goes too, and the country stands landless: whatever army
    /// it still had is then surrendered by the same rule that surrenders any country whose
    /// ground is gone (CapitalCore), because a country must not die twice, by two causes.
    ///
    /// Only a country the reviews have called final relinquishes anything. A region emptied
    /// by war while its country lives is war's business, not demography's.
    /// </summary>
    private static void Relinquish(Campaign campaign, Nation nation)
    {
        var capital = nation.CapitalStateMapIndex;
        // StatesOf hands back a snapshot array, so relinquishing a region mid-iteration
        // cannot pull the rug out from under the loop.
        foreach (var state in campaign.StatesOf(nation))
        {
            if (state.MapIndex == capital || state.Population > 0) continue;
            if (!Abandon(campaign, state)) continue;
            DiagnosticLogs.Write("simulation",
                $"DEMOGRAPHIC_ABANDONED nation={nation.Tag} region={state.Id} day={campaign.ElapsedDays}");
        }
        if (nation.Population > 0) return;
        var seat = campaign.States.Find(candidate => candidate.MapIndex == capital);
        if (seat is null || seat.NationMapIndex != nation.MapIndex) return;
        if (!Abandon(campaign, seat)) return;
        DiagnosticLogs.Write("simulation",
            $"DEMOGRAPHIC_EXTINCTION nation={nation.Tag} day={campaign.ElapsedDays}");
        campaign.Chronicle.Record(campaign, "demographic_extinction", nation.Id);
    }

    /// <summary>
    /// Give one region back to nobody. Provinces lose owner and controller; the region's
    /// record is then put in step with its ground by the same repartitioning every border
    /// change goes through, so an abandoned region is unclaimed exactly the way a region
    /// nobody ever claimed is - one ledger, not two.
    /// </summary>
    public static bool Abandon(Campaign campaign, ScenarioState state)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null || state is null) return false;
        var moved = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != (ushort)state.MapIndex) continue;
            if (topology.ProvinceToNation[province] == 0) continue;
            topology.ProvinceToNation[province] = 0;
            campaign.SetProvinceController(province, 0);
            moved++;
        }
        if (moved == 0) return false;
        StatePartition.Repartition(campaign, state, separatePockets: false);
        campaign.InvalidateStateIndex();
        campaign.MarkMapChanged();
        return true;
    }
}
