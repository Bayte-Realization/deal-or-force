using System;

namespace DealOrForce.Simulation;

/// <summary>
/// A national cabinet priority. Unlike one-shot decisions, this is a persistent,
/// moddable strategic direction with restrained weekly trade-offs.
/// </summary>
public enum GovernmentFocus
{
    Balanced,
    EconomicExpansion,
    SocialCohesion,
    EducationAndScience,
    NationalSecurity
}

public static class GovernmentFocusRules
{
    public static string Name(GovernmentFocus focus) => focus switch
    {
        GovernmentFocus.EconomicExpansion => "Economic expansion",
        GovernmentFocus.SocialCohesion => "Social cohesion",
        GovernmentFocus.EducationAndScience => "Education & science",
        GovernmentFocus.NationalSecurity => "National security",
        _ => "Balanced cabinet"
    };

    public static string ShortName(GovernmentFocus focus) => focus switch
    {
        GovernmentFocus.EconomicExpansion => "Growth",
        GovernmentFocus.SocialCohesion => "Cohesion",
        GovernmentFocus.EducationAndScience => "Science",
        GovernmentFocus.NationalSecurity => "Security",
        _ => "Balanced"
    };

    public static string Description(GovernmentFocus focus) => focus switch
    {
        GovernmentFocus.EconomicExpansion =>
            "+0.4 percentage points annual GDP growth; a small weekly living-standard and stability cost.",
        GovernmentFocus.SocialCohesion =>
            "Improves living standards and suppresses radicalism; -0.2 percentage points annual GDP growth.",
        GovernmentFocus.EducationAndScience =>
            "Accelerates literacy and raises the productivity target; benefits compound gradually.",
        GovernmentFocus.NationalSecurity =>
            "Builds stability and war support; -0.2 percentage points annual GDP growth and slightly more tension.",
        _ => "No special modifier. Institutions pursue a broad, predictable compromise."
    };

    public static float AnnualGrowth(GovernmentFocus focus) => focus switch
    {
        GovernmentFocus.EconomicExpansion => 0.004f,
        GovernmentFocus.SocialCohesion => -0.002f,
        GovernmentFocus.NationalSecurity => -0.002f,
        _ => 0f
    };
}

public sealed partial class Campaign
{
    public void SetWelfareSpending(int nationId, float value)
    {
        var nation = NationById(nationId);
        if (nation is null) return;
        nation.WelfareSpending = M.Clamp(value, 0f, 0.18f);
        Changed?.Invoke();
    }

    public void SetEducationSpending(int nationId, float value)
    {
        var nation = NationById(nationId);
        if (nation is null) return;
        nation.EducationSpending = M.Clamp(value, 0f, 0.15f);
        Changed?.Invoke();
    }

    public void SetGovernmentFocus(int nationId, GovernmentFocus focus)
    {
        var nation = NationById(nationId);
        if (nation is null || !Enum.IsDefined(focus)) return;
        nation.GovernmentFocus = focus;
        Changed?.Invoke();
    }
}

public sealed class SetWelfareSpendingCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float Value { get; init; }
    public void Apply(Campaign campaign) => campaign.SetWelfareSpending(IssuerId, Value);
}

public sealed class SetEducationSpendingCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float Value { get; init; }
    public void Apply(Campaign campaign) => campaign.SetEducationSpending(IssuerId, Value);
}

public sealed class SetGovernmentFocusCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public GovernmentFocus Focus { get; init; }
    public void Apply(Campaign campaign) => campaign.SetGovernmentFocus(IssuerId, Focus);
}
