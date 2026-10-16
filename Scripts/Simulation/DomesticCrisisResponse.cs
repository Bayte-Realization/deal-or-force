using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float CrisisDialogueRadicalismPerDay { get; set; } = .00045f;
    public float CrisisReviewInstitutionsPerDay { get; set; } = .00025f;
}

public static partial class DomesticCrises
{
    public static readonly string[] ResponseActions = { "public_dialogue", "institutional_review" };

    public static float Cost(Campaign c, Nation n, string actionId) => c is null || n is null ||
        Array.IndexOf(ResponseActions, actionId) < 0 ? 0f : MathF.Max(0f,
            MathF.Max(c.Defines.CrisisResponseCostFloor, n.Gdp * c.Defines.CrisisResponseCostShare));

    public static string Refusal(Campaign c, Nation n, string actionId)
    {
        if (c is null || n is null) return Loc.Get("Choose a country.");
        if (!c.FeatureEnabled("domestic_crises") || !c.FeatureEnabled("politics"))
            return Loc.Get("Institutional crises are disabled in this scenario.");
        if (Array.IndexOf(ResponseActions, actionId) < 0) return Loc.Get("Unknown institutional response.");
        if (c.StatesOf(n).Length == 0) return Loc.Get("A landless organisation has no domestic administration to review.");
        var memory = n.DomesticCrisis;
        if (memory?.ResponseUntilDay > c.ElapsedDays) return Loc.Get("An institutional response is already underway.");
        if (memory?.ResponseCooldownUntilDay > c.ElapsedDays)
            return Loc.Format("Institutions need {0} more day(s) before another response.", memory.ResponseCooldownUntilDay - c.ElapsedDays);
        if (!float.IsFinite(n.Treasury) || !float.IsFinite(Cost(c, n, actionId)) || n.Treasury < Cost(c, n, actionId))
            return Loc.Get("The treasury cannot fund this institutional response.");
        return "";
    }

    internal static void ReviewResponse(Campaign c, Nation n, int days)
    {
        var memory = n.DomesticCrisis;
        if (memory.ResponseUntilDay < 0) return;
        // Apply only the part of a multi-day step during which the paid programme was active.
        var activeDays = Math.Max(0, Math.Min(days, memory.ResponseUntilDay - (c.ElapsedDays - days)));
        if (memory.ResponseId == "public_dialogue")
            foreach (var region in c.StatesOf(n))
                region.Radicalism = M.Clamp01(region.Radicalism - activeDays * MathF.Max(0f, c.Defines.CrisisDialogueRadicalismPerDay));
        if (memory.ResponseId == "institutional_review")
            n.StabilityGovernance = M.Clamp(n.StabilityGovernance + activeDays * MathF.Max(0f, c.Defines.CrisisReviewInstitutionsPerDay),
                -StabilityCore.Weights.GovernanceLimit, StabilityCore.Weights.GovernanceLimit);
        if (memory.ResponseUntilDay <= c.ElapsedDays)
        {
            memory.ResponseUntilDay = -1;
            c.Chronicle.Record(c, "institutional_response_completed", n.Id);
            c.MarkStrategicChange();
        }
    }

    public static float ConciliationCost(Campaign c, Nation n, ScenarioState region)
    {
        if (c is null || n is null || region is null || n.Population <= 0) return 0f;
        var share = Math.Clamp(region.Population / (float)n.Population, 0f, 1f);
        return MathF.Max(0f, MathF.Max(c.Defines.CrisisConciliationCostFloor, n.Gdp * share * c.Defines.CrisisConciliationCostShare));
    }

    public static string ConciliationRefusal(Campaign c, Nation n, ScenarioState region)
    {
        if (c is null || n is null) return Loc.Get("Choose a country.");
        if (!c.FeatureEnabled("domestic_crises") || !c.FeatureEnabled("politics"))
            return Loc.Get("Institutional crises are disabled in this scenario.");
        if (region is null || region.NationMapIndex != n.MapIndex)
            return Loc.Get("That region is not governed by this country.");
        if (region.Population <= 0) return Loc.Get("Nobody lives there to settle with.");
        var memory = n.DomesticCrisis;
        if (memory is { ConciliationUntilDay: >= 0 } && memory.ConciliationUntilDay > c.ElapsedDays)
            return memory.ConciliationRegion == region.MapIndex
                ? Loc.Format("This region is being settled for {0} more days.", memory.ConciliationUntilDay - c.ElapsedDays)
                : Loc.Get("Another region is already being settled.");
        var hot = PoliticalCohorts.UnrestHeat(region) >= c.Defines.CrisisConciliationHeat || region.SecessionPressure >= .12f;
        if (!hot) return Loc.Get("This region is not asking for the money.");
        var cost = ConciliationCost(c, n, region);
        if (!float.IsFinite(n.Treasury) || !float.IsFinite(cost) || n.Treasury < cost)
            return Loc.Get("The treasury cannot fund settling this region.");
        return "";
    }

    internal static void ReviewConciliation(Campaign c, Nation n, int days)
    {
        var memory = n.DomesticCrisis;
        if (memory is null || memory.ConciliationUntilDay < 0) return;
        var active = Math.Max(0, Math.Min(days, memory.ConciliationUntilDay - (c.ElapsedDays - days)));
        var region = c.States.Find(state => state.MapIndex == memory.ConciliationRegion);
        if (region is not null && region.NationMapIndex == n.MapIndex && active > 0)
        {
            var d = c.Defines;
            region.SecessionPressure = MathF.Max(0f, region.SecessionPressure - active * MathF.Max(0f, d.CrisisConciliationPressurePerDay));
            region.Radicalism = M.Clamp01(region.Radicalism - active * MathF.Max(0f, d.CrisisConciliationRadicalismPerDay));
            if (region.PoliticalDivergence >= 0f && region.GovernmentApproval >= 0f)
            {
                region.PoliticalDivergence = MathF.Max(0f, region.PoliticalDivergence - active * MathF.Max(0f, d.CrisisConciliationDivergencePerDay));
                region.GovernmentApproval = M.Clamp01(region.GovernmentApproval + active * MathF.Max(0f, d.CrisisConciliationApprovalPerDay));
            }
        }
        if (memory.ConciliationUntilDay <= c.ElapsedDays)
        {
            memory.ConciliationUntilDay = -1;
            memory.ConciliationRegion = -1;
            c.Chronicle.Record(c, "conciliation_completed", n.Id);
            c.MarkStrategicChange();
        }
    }

    internal static void ConsiderConciliation(Campaign c, Nation n)
    {
        if (n.IsHuman) return;
        var memory = n.DomesticCrisis;
        if (memory is { ConciliationUntilDay: >= 0 } && memory.ConciliationUntilDay > c.ElapsedDays) return;
        var loud = PoliticalCohorts.Loudest(c, n);
        if (loud is null || loud.Heat < c.Defines.CrisisConciliationAiHeat) return;
        var region = c.States.Find(state => state.MapIndex == loud.MapIndex);
        if (ConciliationRefusal(c, n, region).Length > 0) return;
        if (n.Treasury < ConciliationCost(c, n, region) * MathF.Max(1f, c.Defines.CrisisConciliationAiReserve)) return;
        new ConciliateRegionCommand { IssuerId = n.Id, StateMapIndex = region.MapIndex }.Apply(c);
    }
}

public sealed class StartCrisisResponseCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string ActionId { get; init; } = "";

    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var n = c.NationById(IssuerId);
        if (DomesticCrises.Refusal(c, n, ActionId).Length > 0) return;
        var memory = n.DomesticCrisis ??= new();
        n.Treasury -= DomesticCrises.Cost(c, n, ActionId);
        memory.ResponseId = ActionId;
        memory.ResponseUntilDay = c.ElapsedDays + Math.Max(1, c.Defines.CrisisResponseDurationDays);
        memory.ResponseCooldownUntilDay = memory.ResponseUntilDay + Math.Max(0, c.Defines.CrisisResponseCooldownDays);
        c.Chronicle.Record(c, "institutional_response_started", n.Id);
        c.MarkStrategicChange();
    }
}

/// <summary>
/// Pay to cool one governed region for a season: the pull to leave, the anger, and
/// the political distance. It does not give the region back a border, and it does not
/// run in two places at once.
/// </summary>
public sealed class ConciliateRegionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }

    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var nation = c.NationById(IssuerId);
        var region = c.States.Find(state => state.MapIndex == StateMapIndex);
        if (DomesticCrises.ConciliationRefusal(c, nation, region).Length > 0) return;
        var memory = nation.DomesticCrisis ??= new();
        nation.Treasury -= DomesticCrises.ConciliationCost(c, nation, region);
        memory.ConciliationRegion = region.MapIndex;
        memory.ConciliationUntilDay = c.ElapsedDays + Math.Max(1, c.Defines.CrisisConciliationDays);
        c.Chronicle.Record(c, "conciliation_started", nation.Id, 0, region.MapIndex.ToString());
        c.MarkStrategicChange();
    }
}
