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
