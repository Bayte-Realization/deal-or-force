using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool ParliamentEnabled { get; set; } = true;
    public int ParliamentReviewDays { get; set; } = 30;
    public float ParliamentAntiWarThreshold { get; set; } = 0.40f;
    public float ParliamentPeaceMandateFatigue { get; set; } = 0.45f;
    public float ParliamentBudgetCutShare { get; set; } = 0.15f;
    public float ParliamentDiscontentStabilityPenalty { get; set; } = 0.015f;
}

public sealed class ParliamentState
{
    public float RulingBlocShare { get; set; } = 0.55f;
    public float OppositionShare { get; set; } = 0.35f;
    public float DovishShare { get; set; } = 0.10f;
    public bool PeaceMandateActive { get; set; }
    public int PeaceMandateDay { get; set; } = -1;
    public bool MilitaryBudgetCutActive { get; set; }
    public int LastReviewDay { get; set; } = -1;
    public float FactionDiscontent { get; set; }
    public string FavoredPortfolio { get; set; } = "";
    public string FavoredLawCategory { get; set; } = "";
}

public sealed partial class Nation
{
    public ParliamentState Parliament;
}

/// <summary>
/// Legislative balance of power, anti-war parliamentary coalitions, and factional lobbying.
/// In constitutional and representative governments, foreign and military decisions must
/// navigate legislative caucuses. When war exhaustion, debt, or casualty burdens mount,
/// the dovish anti-war caucus expands, enacting military spending restraints and forcing
/// peace mandates upon the executive.
/// </summary>
public static class ParliamentaryPolitics
{
    public static bool Enabled(Campaign campaign) => campaign?.Defines?.ParliamentEnabled == true;

    public static bool Eligible(Nation nation)
    {
        if (nation is null || nation.Movement is not null || nation.Population <= 0) return false;
        return nation.Government is "parliamentary_republic" or "presidential_republic"
            or "federation" or "constitutional_monarchy" or "direct_democracy";
    }

    public static void Review(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return;
        if (!Eligible(nation))
        {
            if (nation.Parliament is not null)
            {
                nation.Parliament.PeaceMandateActive = false;
                nation.Parliament.MilitaryBudgetCutActive = false;
                nation.Parliament.LastReviewDay = -1;
            }
            return;
        }

        var p = nation.Parliament ??= new ParliamentState();
        var approval = ElectoralPolitics.Approval(campaign, nation);
        var atWar = campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id));

        if (!atWar)
        {
            p.RulingBlocShare = Math.Clamp(0.25f + approval * 0.55f, 0.20f, 0.85f);
            var remaining = 1.0f - p.RulingBlocShare;
            p.DovishShare = Math.Clamp(remaining * 0.20f, 0.05f, 0.30f);
            p.OppositionShare = Math.Max(0.05f, 1.0f - p.RulingBlocShare - p.DovishShare);
            p.PeaceMandateActive = false;
            p.PeaceMandateDay = -1;
            p.MilitaryBudgetCutActive = false;
        }
        else
        {
            var fatigue = WarWeariness.NationalFatigue(campaign, nation);
            var debtStress = nation.DebtCondition >= DebtCondition.Stressed ? 0.15f : 0f;
            var shortageStress = nation.ShortageIndex > 0.20f ? 0.15f : 0f;

            var targetDovish = Math.Clamp(0.12f + fatigue * 0.75f + debtStress + shortageStress, 0.05f, 0.88f);
            p.DovishShare = Math.Clamp(p.DovishShare + (targetDovish - p.DovishShare) * 0.65f, 0.05f, 0.88f);
            var rem = 1.0f - p.DovishShare;
            // A caucus above 85% leaves less than the usual 10% ruling floor plus
            // 5% opposition. Keep the anti-war vote and opposition reserve; the
            // ruling floor must fit the actual seats, not reverse Clamp's bounds.
            var rulingCeiling = rem - 0.05f;
            var rulingFloor = Math.Min(0.10f, rulingCeiling);
            var safeCeiling = Math.Max(rulingFloor, rulingCeiling);
            p.RulingBlocShare = Math.Clamp(rem * approval, rulingFloor, safeCeiling);
            p.OppositionShare = Math.Max(0.05f, 1.0f - p.RulingBlocShare - p.DovishShare);

            p.MilitaryBudgetCutActive = p.DovishShare >= campaign.Defines.ParliamentAntiWarThreshold;

            if (p.DovishShare >= 0.50f || (p.DovishShare + p.OppositionShare >= 0.65f && fatigue >= campaign.Defines.ParliamentPeaceMandateFatigue))
            {
                if (!p.PeaceMandateActive)
                {
                    p.PeaceMandateActive = true;
                    p.PeaceMandateDay = campaign.ElapsedDays;
                    campaign.Chronicle.Record(campaign, "parliament_peace_mandate", nation.Id);
                }
            }
            else if (fatigue < 0.20f)
            {
                p.PeaceMandateActive = false;
                p.PeaceMandateDay = -1;
            }
        }

        // Factional lobbying priorities
        if (nation.PublicDebt / MathF.Max(0.01f, nation.Gdp) > 0.60f || nation.DebtCondition >= DebtCondition.Stressed)
        {
            p.FavoredPortfolio = "finance";
            p.FavoredLawCategory = "fiscal";
        }
        else if (atWar && p.DovishShare >= 0.35f)
        {
            p.FavoredPortfolio = "defense";
            p.FavoredLawCategory = "peace";
        }
        else if (nation.Stability < 0.45f || (nation.DomesticCrisis is not null && nation.DomesticCrisis.CoupPressure > 0.35f))
        {
            p.FavoredPortfolio = "interior";
            p.FavoredLawCategory = "rights";
        }
        else
        {
            p.FavoredPortfolio = "foreign";
            p.FavoredLawCategory = "trade";
        }

        p.FactionDiscontent = Math.Max(0f, p.FactionDiscontent - 0.02f);
        if (p.FactionDiscontent > 0.60f)
        {
            nation.Stability = Math.Clamp(nation.Stability - campaign.Defines.ParliamentDiscontentStabilityPenalty / 12f, 0.10f, 0.98f);
        }

        p.LastReviewDay = campaign.ElapsedDays;
        campaign.MarkStrategicChange();
        NationalModifiers.Invalidate(nation);
    }

    public static void Tick(Campaign campaign)
    {
        if (!Enabled(campaign)) return;
        var interval = Math.Max(1, campaign.Defines.ParliamentReviewDays);
        var reviewDay = campaign.ElapsedDays > 0 && campaign.ElapsedDays % interval == 19;

        foreach (var nation in campaign.Nations)
        {
            if (nation.Movement is not null || nation.Population <= 0 || campaign.Fallen.Contains(nation.Id)) continue;
            if (reviewDay || nation.Parliament is null || nation.Parliament.LastReviewDay < 0)
            {
                Review(campaign, nation);
            }
        }
    }

    public static void AddTo(Dictionary<string, float> totals, Nation nation)
    {
        if (nation?.Parliament is null) return;
        if (nation.Parliament.MilitaryBudgetCutActive)
        {
            totals["reinforcement_rate"] = totals.GetValueOrDefault("reinforcement_rate") - 0.15f;
            totals["recruitment_speed"] = totals.GetValueOrDefault("recruitment_speed") - 0.10f;
        }
        if (nation.Parliament.PeaceMandateActive)
        {
            totals["war_support_weekly"] = totals.GetValueOrDefault("war_support_weekly") - 0.0005f;
        }
    }

    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        if (nation?.Parliament is null) yield break;

        if (nation.Parliament.MilitaryBudgetCutActive)
        {
            yield return ("modifier.parliament_anti_war_budget", new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["reinforcement_rate"] = -0.15f,
                ["recruitment_speed"] = -0.10f
            });
        }

        if (nation.Parliament.PeaceMandateActive)
        {
            yield return ("modifier.parliament_peace_mandate", new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["war_support_weekly"] = -0.0005f
            });
        }
    }

    public static void OnMinisterAppointed(Campaign campaign, Nation nation, string portfolio)
    {
        if (campaign is null || nation?.Parliament is null || string.IsNullOrEmpty(portfolio)) return;
        if (string.Equals(portfolio, nation.Parliament.FavoredPortfolio, StringComparison.OrdinalIgnoreCase))
        {
            nation.Parliament.FactionDiscontent = Math.Max(0f, nation.Parliament.FactionDiscontent - 0.20f);
            nation.Stability = Math.Clamp(nation.Stability + 0.005f, 0f, 1f);
            campaign.Chronicle.Record(campaign, "parliament_minister_supported", nation.Id, place: portfolio);
        }
        else if (nation.Parliament.DovishShare > 0.40f && string.Equals(portfolio, "defense", StringComparison.OrdinalIgnoreCase))
        {
            // Hawkish defense appointment against dovish legislature increases parliamentary friction
            nation.Parliament.FactionDiscontent = Math.Min(1f, nation.Parliament.FactionDiscontent + 0.15f);
        }
    }

    public static void OnLawAdopted(Campaign campaign, Nation nation, PoliticalPolicy policy)
    {
        if (campaign is null || nation?.Parliament is null || policy is null) return;
        if (MatchesFavoredLawCategory(policy.Category, nation.Parliament.FavoredLawCategory))
        {
            nation.Parliament.FactionDiscontent = Math.Max(0f, nation.Parliament.FactionDiscontent - 0.20f);
            nation.Stability = Math.Clamp(nation.Stability + 0.01f, 0f, 1f);
            campaign.Chronicle.Record(campaign, "parliament_law_supported", nation.Id, place: policy.Name);
        }
    }

    public static bool MatchesFavoredLawCategory(string policyCategory, string favored)
    {
        if (string.IsNullOrWhiteSpace(policyCategory) || string.IsNullOrWhiteSpace(favored)) return false;
        if (string.Equals(policyCategory, favored, StringComparison.OrdinalIgnoreCase)) return true;

        return favored.ToLowerInvariant() switch
        {
            "fiscal" => policyCategory.Equals("Fiscal mandate", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Currency regime", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Ownership regime", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Energy and climate", StringComparison.OrdinalIgnoreCase),
            "peace" => policyCategory.Equals("Military service", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Security mandate", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Post-conflict recovery", StringComparison.OrdinalIgnoreCase),
            "rights" => policyCategory.Equals("Civil rights", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Social guarantees", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Judiciary", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Media and press", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Labour relations", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Nationalities", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Local autonomy", StringComparison.OrdinalIgnoreCase),
            "trade" => policyCategory.Equals("Trade policy", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Industrial policy", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Migration", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Foreign stance", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Bilateral stance", StringComparison.OrdinalIgnoreCase)
                || policyCategory.Equals("Regional development", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
