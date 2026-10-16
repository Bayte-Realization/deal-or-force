using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How far a region's opinion moves toward today's conditions, per day.</summary>
    public float CohortDriftPerDay { get; set; } = 0.0012f;
    /// <summary>Divergence at which a region of the country's own people can start to leave.</summary>
    public float CenterRegionIdeologySplit { get; set; } = 0.55f;
    /// <summary>A region more accepting than this does not take the federal road.</summary>
    public float FederalGovernmentApprovalCeiling { get; set; } = 0.42f;
    /// <summary>A centre more legitimate than this still holds its own political regions.</summary>
    public float FederalStabilityCeiling { get; set; } = 0.50f;
    /// <summary>National shortage that counts as the economic half of a federal break.</summary>
    public float FederalShortageStress { get; set; } = 0.28f;
    /// <summary>How much poorer than the country a region must be to count as that stress.</summary>
    public float FederalLivingGap { get; set; } = 0.12f;
    /// <summary>Scales weekly secession pressure on the federal road. Zero leaves the opinion, and removes the break.</summary>
    public float FederalPressureScale { get; set; } = 1f;
    /// <summary>Population-weighted acceptance below this, in an orderly state, is a political scandal.</summary>
    public float ScandalApproval { get; set; } = 0.36f;
    /// <summary>Stability above this means the laws are holding. Below it, the crisis is already a collapse.</summary>
    public float ScandalOrder { get; set; } = 0.62f;
    public int ScandalCooldownDays { get; set; } = 480;
    public float ScandalStabilityCost { get; set; } = 0.015f;
    /// <summary>How much rejected government adds to coup pressure. It does not replace low stability.</summary>
    public float CoupGovernmentDisapprovalWeight { get; set; } = 0.28f;
}

public sealed partial class ScenarioState
{
    /// <summary>How far this region's politics sit from the centre, 0 to 1. Negative until the first review.</summary>
    public float PoliticalDivergence = -1f;
    /// <summary>Share that accepts the laws it lives under. Negative until the first review.</summary>
    public float LawApproval = -1f;
    /// <summary>Share that accepts the government itself. Negative until the first review.</summary>
    public float GovernmentApproval = -1f;
}

public sealed partial class Nation
{
    /// <summary>The last day an orderly, unloved government was noticed. Negative when it has not been.</summary>
    public int LastPoliticalScandalDay = -1;
}

/// <summary>What a country thinks of its own government, counted by people rather than by regions.</summary>
public readonly record struct CohortMood(float Law, float Government, float Divergence, bool Reviewed);

/// <summary>
/// People do not only belong to a culture and a faith. They also accept or reject the laws,
/// the government, and the idea that the centre still speaks for them.
///
/// The numbers live on the region and move a little every day toward what that region is
/// living through: shortages, a poorer life than the rest of the country, a lost war, a
/// government that has just changed its mind. They do not replace radicalism, which is the
/// economic anger, and they do not invent a second country out of a tag. A settled, solvent,
/// legitimate state keeps its regions. A state that has been unloved and insolvent for years
/// can lose a region of its own people — that road is <see cref="SecessionCore"/>.
/// </summary>
public static class PoliticalCohorts
{
    public static void Review(Campaign c, Nation n, int days)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return;
        var span = Math.Max(0, days);
        var debt = span > 0 && InDebt(c, n);
        var losing = span > 0 && Losing(c, n);
        var recentPolitics = span > 0 && n.PoliticsChangedDay >= 0 && c.ElapsedDays - n.PoliticsChangedDay < 400;
        var step = MathF.Max(0f, c.Defines.CohortDriftPerDay) * span;
        var shortage = n.ShortageIndex;
        var stability = n.Stability;
        var nationalLiving = n.StandardOfLiving;
        foreach (var state in c.StatesOf(n))
        {
            if (state is null || state.Population <= 0) continue;
            var fresh = state.PoliticalDivergence < 0f || state.LawApproval < 0f || state.GovernmentApproval < 0f;
            if (fresh) Seed(n, state);
            if (span == 0 || fresh) continue;
            Drift(state, step, debt, losing, recentPolitics, shortage, stability, nationalLiving, state.MapIndex == n.CapitalStateMapIndex);
        }
        if (span > 0) Notice(c, n);
    }

    /// <summary>
    /// One sentence for the dossier. Empty when there is nothing worth saying.
    /// A country people still like gets a sentence too: comfort is not only a list of alarms.
    /// </summary>
    public static string StrainCaption(Campaign c, Nation n)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return "";
        var mood = Mood(c, n);
        if (!mood.Reviewed) return "";
        foreach (var state in c.StatesOf(n))
            if (SecessionCore.IsFederalBreak(c, n, state))
                return "A region of its own has stopped belonging to the centre.";
        if (mood.Government < 0.42f && mood.Divergence >= 0.35f)
            return "The laws still hold. The government is losing the room.";
        if (mood.Government >= 0.62f && n.Stability >= 0.55f && mood.Divergence < 0.25f)
            return "Liked enough to argue in the open.";
        return "";
    }

    /// <summary>
    /// What the unrest map paints for one region. Radicalism, the pull to leave, and —
    /// once the region has been read — how far its politics have left the government.
    /// An unread region stays on the first two, so the opening map is not a revolt.
    /// </summary>
    public static float UnrestHeat(ScenarioState state)
    {
        if (state is null) return 0f;
        var heat = MathF.Max(M.Clamp01(state.Radicalism), state.SecessionPressure < 0f ? 0f : M.Clamp01(state.SecessionPressure));
        if (state.PoliticalDivergence >= 0f && state.GovernmentApproval >= 0f)
            heat = MathF.Max(heat, M.Clamp01(state.PoliticalDivergence) * (1f - M.Clamp01(state.GovernmentApproval)));
        return heat;
    }

    /// <summary>
    /// One region, read and not changed: how hot it is, and the single pressure on the books
    /// that is pushing its opinion. The sentence is an English key. Empty when nothing
    /// reviewed is pushing.
    /// </summary>
    public sealed record RegionAccount(int MapIndex, string Name, float Heat, string Cause, string Chip);

    /// <summary>
    /// The governed region whose politics have left the government furthest.
    /// Radicalism alone does not win this: the unrest map already paints that.
    /// Null when no region has been read yet.
    /// </summary>
    public static RegionAccount Loudest(Campaign c, Nation n)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return null;
        ScenarioState best = null;
        var bestScore = -1f;
        foreach (var state in c.StatesOf(n))
        {
            if (state is null || state.Population <= 0 || state.PoliticalDivergence < 0f || state.GovernmentApproval < 0f)
                continue;
            var score = M.Clamp01(state.PoliticalDivergence) * (1f - M.Clamp01(state.GovernmentApproval));
            if (score < bestScore || (score == bestScore && best is not null && state.MapIndex > best.MapIndex)) continue;
            best = state;
            bestScore = score;
        }
        if (best is null) return null;
        var (cause, chip) = CauseOf(c, n, best);
        return new RegionAccount(best.MapIndex, best.DisplayName ?? "", UnrestHeat(best), cause, chip);
    }

    /// <summary>
    /// Why this region's opinion is where it is, from the same pressures the daily drift uses.
    /// One sentence, the strongest pressure on the books. Reading it changes nothing.
    /// </summary>
    public static (string Cause, string Chip) CauseOf(Campaign c, Nation n, ScenarioState state)
    {
        if (c is null || n is null || state is null || state.PoliticalDivergence < 0f || state.GovernmentApproval < 0f)
            return ("", "");
        if (InDebt(c, n))
            return ("The centre is in debt, and this region is paying for it.", "the centre's debt");
        if (Losing(c, n))
            return ("A war is being lost, and this region can tell.", "a lost war");
        if (n.PoliticsChangedDay >= 0 && c.ElapsedDays - n.PoliticsChangedDay < 400)
            return ("The government changed its mind, and this region has not forgiven it.", "a new government");
        if (n.ShortageIndex >= c.Defines.FederalShortageStress)
            return ("The shelves are short, and the blame is sitting on the government.", "short shelves");
        if (state.StandardOfLiving + c.Defines.FederalLivingGap < n.StandardOfLiving)
            return ("This region lives worse than the country that taxes it.", "poorer than the country");
        if (state.MapIndex == n.CapitalStateMapIndex)
            return ("The capital still belongs. The grievance is quieter here.", "");
        if (state.PoliticalDivergence >= 0.35f)
            return ("The opinion here is its own. Nothing else on the books is pushing it today.", "");
        return ("", "");
    }

    /// <summary>The people's verdict, or not reviewed when no governed region has been read yet.</summary>
    public static CohortMood Mood(Campaign c, Nation n)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return default;
        double people = 0, law = 0, government = 0, divergence = 0;
        foreach (var state in c.StatesOf(n))
        {
            if (state is null || state.Population <= 0 || state.PoliticalDivergence < 0f) continue;
            people += state.Population;
            law += state.Population * (double)M.Clamp01(state.LawApproval);
            government += state.Population * (double)M.Clamp01(state.GovernmentApproval);
            divergence += state.Population * (double)M.Clamp01(state.PoliticalDivergence);
        }
        if (people <= 0) return default;
        return new CohortMood((float)(law / people), (float)(government / people), (float)(divergence / people), true);
    }

    /// <summary>
    /// An orderly country whose people have stopped accepting it becomes news. A country
    /// already falling apart is a crisis, not a scandal, and is left to the coup and
    /// secession roads.
    /// </summary>
    public static bool Notice(Campaign c, Nation n)
    {
        if (c is null || n is null || !On(c, "political_cohorts")) return false;
        if (n.Stability < c.Defines.ScandalOrder) return false;
        if (n.LastPoliticalScandalDay >= 0 && c.ElapsedDays - n.LastPoliticalScandalDay < Math.Max(1, c.Defines.ScandalCooldownDays))
            return false;
        var mood = Mood(c, n);
        if (!mood.Reviewed || mood.Government >= c.Defines.ScandalApproval) return false;
        n.LastPoliticalScandalDay = c.ElapsedDays;
        n.Stability = M.Clamp01(n.Stability - MathF.Max(0f, c.Defines.ScandalStabilityCost));
        c.Chronicle.Record(c, "political_scandal", n.Id);
        return true;
    }

    private static void Seed(Nation n, ScenarioState s)
    {
        s.LawApproval = M.Clamp01(0.48f + s.StandardOfLiving * 0.25f);
        s.GovernmentApproval = M.Clamp01(0.40f + n.Stability * 0.35f);
        s.PoliticalDivergence = s.MapIndex == n.CapitalStateMapIndex ? 0.04f : 0.08f;
    }

    private static void Drift(ScenarioState s, float step, bool debt, bool losing, bool recentPolitics,
        float shortage, float stability, float nationalLiving, bool capital)
    {
        var debtPenalty = debt ? 0.12f : 0f;
        var radical = M.Clamp01(s.Radicalism);
        var law = M.Clamp01(0.40f + s.StandardOfLiving * 0.40f + stability * 0.15f - shortage * 0.40f - debtPenalty * 0.5f - radical * 0.25f);
        var government = 0.42f + stability * 0.35f + s.StandardOfLiving * 0.18f - shortage * 0.35f - debtPenalty - radical * 0.22f;
        if (losing) government -= 0.10f;
        if (recentPolitics) government -= 0.08f;
        government = M.Clamp01(government);
        s.LawApproval = Move(M.Clamp01(s.LawApproval), law, step);
        s.GovernmentApproval = Move(M.Clamp01(s.GovernmentApproval), government, step);
        var rejection = M.Clamp01((1f - government) * 0.55f + (1f - law) * 0.35f + radical * 0.25f);
        var poorer = nationalLiving > s.StandardOfLiving ? nationalLiving - s.StandardOfLiving : 0f;
        if (poorer > 1f) poorer = 1f;
        var divergence = M.Clamp01(rejection + poorer * 0.20f);
        if (capital) divergence *= 0.45f;
        s.PoliticalDivergence = Move(M.Clamp01(s.PoliticalDivergence), divergence, step * 0.65f);
    }

    private static bool InDebt(Campaign c, Nation n)
    {
        if (!On(c, "public_debt")) return false;
        var condition = n.DebtCondition;
        return condition == DebtCondition.Stressed || condition == DebtCondition.Default
            || condition == DebtCondition.Insolvent || condition == DebtCondition.Restructuring;
    }

    private static bool Losing(Campaign c, Nation n)
    {
        foreach (var war in c.Wars)
        {
            if (war.Ended || !war.Involves(n.Id)) continue;
            if ((war.IsAttacking(n.Id) ? war.WarScore : -war.WarScore) <= -20f) return true;
        }
        return false;
    }

    /// <summary>
    /// The same answer as <see cref="Campaign.FeatureEnabled"/> for a known id.
    /// The public check validates the id with a LINQ scan, and this runs for every country every day.
    /// </summary>
    private static bool On(Campaign c, string id)
    {
        if (c.FeatureOverrides.TryGetValue(id, out var changed)) return changed;
        if (c.Defines.Features is not null && c.Defines.Features.TryGetValue(id, out var initial)) return initial;
        return MechanicFeatures.BuiltIns.Contains(id);
    }

    private static float Move(float value, float target, float amount) => value < target
        ? MathF.Min(target, value + amount) : MathF.Max(target, value - amount);
}
