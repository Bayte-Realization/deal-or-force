using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Rare, deterministic diplomatic projects, not an hourly annexation lottery.
/// No static campaign cache or random draws: save/load and lockstep use the same ledger.</summary>
public static class PeacefulFormationAi
{
    private const string ReviewKey = "union_ai_review";

    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(PeacefulFormationAi))) return;
        var interval = campaign.Defines.UnionAiReviewDays;
        if (interval <= 0 || campaign.ElapsedDays < campaign.Defines.UnionAiEarliestDay ||
            !float.IsFinite(campaign.Defines.UnionAiMinimumOwnedShare) ||
            !float.IsFinite(campaign.Defines.UnionAiTreasuryReserveShare) ||
            campaign.ProvinceMap is null || campaign.ProvinceController is null) return;
        var definitions = FormableNations.All.Where(d => d.AiFoundation &&
            (d.PeacefulAccession || d.SubjectIntegration)).ToArray();
        if (definitions.Length == 0) return;
        foreach (var founder in campaign.Nations.OrderBy(n => n.Id))
        {
            if (founder.IsHuman || (campaign.ElapsedDays + (long)founder.Id) % interval != 0 ||
                founder.DecisionAvailableDay.GetValueOrDefault(ReviewKey) > campaign.ElapsedDays ||
                !CanLead(campaign, founder)) continue;
            // Stamp even an unsuccessful review: repeated calls today must be idempotent.
            founder.DecisionAvailableDay[ReviewKey] = campaign.ElapsedDays + interval;
            foreach (var definition in definitions)
            {
                if (!FormableNations.Eligible(founder, definition) ||
                    !FormableNations.ReligionMandate(campaign, founder, definition) ||
                    founder.Stability < definition.MinimumStability ||
                    (definition.RequiredIdeology.Length > 0 && !definition.RequiredIdeology.Contains(founder.Ideology, StringComparer.OrdinalIgnoreCase)) ||
                    (definition.RequiredGovernment.Length > 0 && !definition.RequiredGovernment.Contains(founder.Government, StringComparer.OrdinalIgnoreCase)) ||
                    campaign.Nations.Any(n => n.FormedNationId == definition.Id)) continue;
                var reserve = founder.Gdp * Math.Max(0f, campaign.Defines.UnionAiTreasuryReserveShare);
                if (founder.Treasury < Math.Max(.05f, founder.Gdp * definition.CostShare) + reserve) continue;

                var map = campaign.ProvinceMap;
                var owners = new Dictionary<int, int>();
                var total = 0;
                var complete = true;
                foreach (var tag in definition.RequiredTags)
                    if (!(campaign.ScenarioId == "2024" ? ProvinceOwnership.ProvincesOf(tag) :
                        campaign.ScenarioTerritories.GetValueOrDefault(tag, Array.Empty<int>())).Any()) complete = false;
                foreach (var province in PeacefulFormation.Territory(campaign, definition))
                {
                    if (province <= 0 || province > map.ProvinceCount) { complete = false; break; }
                    if (map.WaterByProvince[province]) continue;
                    if (province >= campaign.ProvinceController.Length || map.ProvinceToNation[province] == 0 ||
                        campaign.ProvinceController[province] != map.ProvinceToNation[province]) { complete = false; break; }
                    var owner = map.ProvinceToNation[province];
                    owners[owner] = owners.GetValueOrDefault(owner) + 1;
                    total++;
                }
                if (!complete || total == 0 || owners.GetValueOrDefault(founder.MapIndex) / (float)total <
                    M.Clamp01(campaign.Defines.UnionAiMinimumOwnedShare)) continue;

                // The largest eligible, viable founding country has precedence, including
                // humans. Two AI founders must not approach the same partner in parallel.
                var leader = campaign.Nations.Where(n => owners.ContainsKey(n.MapIndex) &&
                    FormableNations.Eligible(n, definition) && CanLead(campaign, n) &&
                    FormableNations.ReligionMandate(campaign, n, definition) &&
                    n.Stability >= definition.MinimumStability &&
                    n.Treasury >= Math.Max(.05f, n.Gdp * definition.CostShare) +
                        n.Gdp * Math.Max(0f, campaign.Defines.UnionAiTreasuryReserveShare) &&
                    (definition.RequiredIdeology.Length == 0 || definition.RequiredIdeology.Contains(n.Ideology, StringComparer.OrdinalIgnoreCase)) &&
                    (definition.RequiredGovernment.Length == 0 || definition.RequiredGovernment.Contains(n.Government, StringComparer.OrdinalIgnoreCase)))
                    .OrderByDescending(n => owners[n.MapIndex]).ThenByDescending(n => n.Gdp).ThenBy(n => n.Id).FirstOrDefault();
                if (leader?.Id != founder.Id) continue;

                var status = FormableNations.Evaluate(campaign, founder, definition);
                if (status.CanForm)
                {
                    new FormNationCommand { IssuerId = founder.Id, FormableId = definition.Id }.Apply(campaign);
                    break;
                }
                var partner = campaign.Nations.Where(n => owners.ContainsKey(n.MapIndex) && n.Id != founder.Id &&
                        !PeacefulFormation.Participates(campaign, founder, n, definition) &&
                        !PromisedElsewhere(campaign, founder, n) &&
                        PeacefulFormation.Refusal(campaign, founder, n, definition).Length == 0 &&
                        PeacefulFormation.Willingness(campaign, founder, n) >= campaign.Defines.UnionAiAcceptance)
                    .OrderByDescending(n => PeacefulFormation.Willingness(campaign, founder, n))
                    .ThenByDescending(n => owners[n.MapIndex]).ThenBy(n => n.Id).FirstOrDefault();
                if (partner is null) continue;
                PeacefulFormation.Invite(campaign, founder, partner, definition);
                // At most one invitation OR proclamation per founding country per review.
                // Acceptance is a treaty; proclamation waits for a later review.
                break;
            }
        }
    }

    private static bool CanLead(Campaign campaign, Nation nation) => nation.Population > 0 &&
        nation.Movement is null && !nation.IsSubject && string.IsNullOrEmpty(nation.FormedNationId) &&
        !campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id));

    private static bool PromisedElsewhere(Campaign campaign, Nation founder, Nation partner)
    {
        // A country cannot sign overlapping foundations with competing governments.
        foreach (var other in campaign.Nations)
        {
            if (other.Id == founder.Id || !CanLead(campaign, other)) continue;
            var prefix = "union_consent/" + other.Id + "/";
            if (partner.DecisionAvailableDay.Any(p => p.Key.StartsWith(prefix, StringComparison.Ordinal) && p.Value > campaign.ElapsedDays)) return true;
        }
        return campaign.PendingEvents.Any(e => e.NationId == partner.Id && e.ExpiresDay > campaign.ElapsedDays &&
            e.Options.Any(o => o.UnionFounderId != founder.Id && !string.IsNullOrEmpty(o.UnionFormableId)));
    }
}
