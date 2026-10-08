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

/// <summary>
/// What a cabinet priority is called and what it does, asked of `common/government_focus`.
///
/// These four used to be four switch statements here and five more scattered through the
/// weekly economic tick, which meant retuning the cabinet was a hunt through two files for
/// nine numbers that had to agree. They are one row each in the data now, and this class is
/// what the rest of the game asks instead of switching on the enum itself - which is also
/// what lets a mod declare a sixth cabinet and have it work everywhere at once.
/// </summary>
public static class GovernmentFocusRules
{
    public static string Name(GovernmentFocus focus) =>
        Loc.Get(GovernmentFocusCatalog.Of(focus).Name);

    public static string ShortName(GovernmentFocus focus) =>
        Loc.Get(GovernmentFocusCatalog.Of(focus).ShortName);

    /// <summary>What the priority is, and under it what it adds while it holds (NationalModifiers).</summary>
    public static string Description(GovernmentFocus focus)
    {
        var definition = GovernmentFocusCatalog.Of(focus);
        var modifiers = NationalModifiers.Block(definition.Modifiers);
        return Loc.Get(definition.Description) + (modifiers.Length > 0 ? "\n\n" + modifiers : "");
    }

    public static float AnnualGrowth(GovernmentFocus focus) =>
        GovernmentFocusCatalog.Of(focus).Growth;
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
        // The catalogue decides what a real focus is, not the enum: a cabinet a mod declared
        // has a perfectly good enum value that `Enum.IsDefined` has never heard of.
        if (nation is null || !GovernmentFocusCatalog.Knows(focus)) return;
        nation.GovernmentFocus = focus;
        NationalModifiers.Invalidate(nation);
        Changed?.Invoke();
    }

    /// <summary>
    /// Set any budget line by the id the data gave it.
    ///
    /// One command for every line there will ever be, the ones a mod invents included,
    /// rather than a command per lever. The four levers that already have their own command
    /// keep it, because three sliders and a good deal of the AI name them; this routes to
    /// the same fields through <see cref="BudgetCore.SetRate"/>, so both roads end in one
    /// place and a rate can only be set one way.
    /// </summary>
    public void SetPolicyRate(int nationId, string lineId, float value)
    {
        var nation = NationById(nationId);
        var line = BudgetCore.Find(lineId);
        if (nation is null || line is null || !line.PlayerSet) return;
        BudgetCore.SetRate(nation, line, value);
        Changed?.Invoke();
    }

    /// <summary>
    /// Rearrange the state, or change what it is trying to do.
    ///
    /// Both go through one method because both cost the same thing: a country does not
    /// reorganise itself for free, and a country that has just reorganised itself is in no
    /// condition to do it again next week. The price is stability, multiplied by how hard
    /// the form being left is to leave - an election and a palace are not the same afternoon
    /// - and it is charged whether the change was wise or not.
    ///
    /// Returns false when the change was refused, so a screen can say why rather than
    /// leaving a button that appears to do nothing.
    /// </summary>
    public bool ChangePolitics(int nationId, string government, string ideology, out string refusal)
    {
        var nation = NationById(nationId);
        refusal = PoliticsCore.Refusal(this, nation, government, ideology);
        if (refusal.Length > 0) return false;

        var wantsGovernment = !string.IsNullOrEmpty(government) &&
                              !string.Equals(government, nation.Government, StringComparison.OrdinalIgnoreCase);
        var wantsIdeology = !string.IsNullOrEmpty(ideology) &&
                            !string.Equals(ideology, nation.Ideology, StringComparison.OrdinalIgnoreCase);
        var cost = PoliticsCore.ChangeCost(nation, government, ideology);

        if (wantsGovernment) nation.Government = government;
        if (wantsIdeology) nation.Ideology = ideology;
        nation.PoliticsChangedDay = ElapsedDays;
        nation.Stability = M.Clamp(nation.Stability - cost, 0.05f, 0.98f);
        foreach (var state in StatesOf(nation))
            state.Radicalism = M.Clamp01(state.Radicalism + cost * 0.5f);

        NationalModifiers.Invalidate(nation);
        Changed?.Invoke();
        return true;
    }
}

public sealed class ChangePoliticsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string Government { get; init; } = "";
    public string Ideology { get; init; } = "";
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        campaign.ChangePolitics(IssuerId, Government, Ideology, out _);
    }
}

public sealed class SetPolicyRateCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string LineId { get; init; } = "";
    public float Value { get; init; }
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        campaign.SetPolicyRate(IssuerId, LineId, Value);
    }
}

public sealed class SetWelfareSpendingCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float Value { get; init; }
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        campaign.SetWelfareSpending(IssuerId, Value);
    }
}

public sealed class SetEducationSpendingCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float Value { get; init; }
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        campaign.SetEducationSpending(IssuerId, Value);
    }
}

public sealed class SetGovernmentFocusCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public GovernmentFocus Focus { get; init; }
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        campaign.SetGovernmentFocus(IssuerId, Focus);
    }
}
