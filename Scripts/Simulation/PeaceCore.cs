using System;
using System.Linq;

namespace ByDealOrForce.Simulation;

public enum PeaceTerms
{
    Ceasefire,
    WhitePeace,
    WarReparations,
    TradeConcessions,
    DemilitarizedSettlement,
    EnforceWarGoal,
    EnforceDemands,
    Capitulate
}

public static class PeaceNegotiation
{
    public static float DemandCost(PeaceTerms terms) => terms switch
    {
        PeaceTerms.Ceasefire => 0f,
        PeaceTerms.WhitePeace => 5f,
        PeaceTerms.WarReparations => 24f,
        PeaceTerms.TradeConcessions => 30f,
        PeaceTerms.DemilitarizedSettlement => 46f,
        PeaceTerms.EnforceWarGoal => 55f,
        PeaceTerms.EnforceDemands => 78f,
        _ => 100f
    };

    public static string Description(PeaceTerms terms) => terms switch
    {
        PeaceTerms.Ceasefire => "A one-year armistice; all forces return to the pre-war control map.",
        PeaceTerms.WhitePeace => "Pre-war borders and a five-year truce, with no victor or indemnity.",
        PeaceTerms.WarReparations => "Pre-war borders plus an immediate economy-scaled indemnity.",
        PeaceTerms.TradeConcessions => "Pre-war borders, an indemnity and a compelled bilateral trade agreement.",
        PeaceTerms.DemilitarizedSettlement => "Pre-war borders while the defeated armed forces surrender equipment and cohesion.",
        PeaceTerms.EnforceWarGoal => "Keep secured provinces inside the declared strategic objective.",
        PeaceTerms.EnforceDemands => "Keep every secured occupation, evacuating exposed advances.",
        _ => "Unconditional surrender under the current occupation map."
    };

    public static float Acceptance(Campaign campaign, StrategicWar war, int proposerId, PeaceTerms terms)
    {
        if (war is null || war.Ended || !war.Involves(proposerId)) return 0f;
        if (terms == PeaceTerms.Capitulate) return 100f;
        var proposerScore = proposerId == war.AttackerId ? war.WarScore : -war.WarScore;
        var responderSupport = proposerId == war.AttackerId ? war.DefenderWarSupport : war.AttackerWarSupport;
        var duration = campaign.ElapsedDays - war.StartedDay;
        var exhaustion = (1f - responderSupport) * 42f;
        var timePressure = Math.Min(18f, duration / 25f);
        var value = terms switch
        {
            PeaceTerms.Ceasefire => 62f - proposerScore * 0.45f + exhaustion + timePressure,
            PeaceTerms.WhitePeace => 50f - proposerScore * 0.65f + exhaustion + timePressure,
            PeaceTerms.WarReparations => 14f + proposerScore * 0.90f + exhaustion + timePressure,
            PeaceTerms.TradeConcessions => 8f + proposerScore * 0.98f + exhaustion + timePressure,
            PeaceTerms.DemilitarizedSettlement => proposerScore * 1.04f + exhaustion + timePressure,
            PeaceTerms.EnforceWarGoal => 6f + proposerScore * 1.08f + exhaustion + timePressure,
            _ => -4f + proposerScore * 1.22f + exhaustion + timePressure
        };
        return M.Clamp(value, 0f, 100f);
    }

    public static string BlockReason(Campaign campaign, StrategicWar war, int nationId, PeaceTerms terms)
    {
        if (war is null || war.Ended || !war.Involves(nationId)) return "No active war";
        var acceptance = Acceptance(campaign, war, nationId, terms);
        return terms switch
        {
            PeaceTerms.Ceasefire when campaign.ElapsedDays - war.StartedDay < 30 => "War has not lasted 30 days",
            PeaceTerms.Ceasefire when acceptance < 50f => $"Opponent acceptance {acceptance:0}% (requires 50%)",
            PeaceTerms.WhitePeace when campaign.ElapsedDays - war.StartedDay < 120 => "War has not lasted 120 days",
            PeaceTerms.WhitePeace when acceptance < 50f => $"Opponent acceptance {acceptance:0}% (requires 50%)",
            PeaceTerms.WarReparations when campaign.ElapsedDays - war.StartedDay < 60 => "War has not lasted 60 days",
            PeaceTerms.WarReparations when acceptance < 50f => $"Opponent acceptance {acceptance:0}% (requires 50%)",
            PeaceTerms.TradeConcessions when campaign.ElapsedDays - war.StartedDay < 90 => "War has not lasted 90 days",
            PeaceTerms.TradeConcessions when acceptance < 50f => $"Opponent acceptance {acceptance:0}% (requires 50%)",
            PeaceTerms.DemilitarizedSettlement when acceptance < 50f => $"Opponent acceptance {acceptance:0}% (requires 50%)",
            PeaceTerms.EnforceWarGoal when war.ClaimedStateMapIndex < 0 => "This war has no territorial objective",
            PeaceTerms.EnforceWarGoal when acceptance < 50f => $"Opponent acceptance {acceptance:0}% (requires 50%)",
            PeaceTerms.EnforceDemands when acceptance < 50f => $"Opponent acceptance {acceptance:0}% (requires 50%)",
            _ => ""
        };
    }

    public static void Resolve(Campaign campaign, StrategicWar war, int proposerId, PeaceTerms terms)
    {
        var block = BlockReason(campaign, war, proposerId, terms);
        war.LastNegotiation = block.Length == 0 ? "Agreement reached" : block;
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        if (block.Length > 0) return;

        if ((terms == PeaceTerms.Ceasefire || terms == PeaceTerms.WhitePeace || terms == PeaceTerms.WarReparations ||
             terms == PeaceTerms.TradeConcessions || terms == PeaceTerms.DemilitarizedSettlement) &&
            campaign.ProvinceController is not null)
            foreach (var pair in war.OriginalController)
                campaign.SetProvinceController(pair.Key, pair.Value);

        if (terms is PeaceTerms.WarReparations or PeaceTerms.TradeConcessions)
        {
            var payer = campaign.NationById(proposerId == war.AttackerId ? war.DefenderId : war.AttackerId);
            var receiver = campaign.NationById(proposerId);
            if (payer is not null && receiver is not null)
            {
                var share = terms == PeaceTerms.TradeConcessions ? 0.015f : 0.03f;
                var payment = MathF.Min(MathF.Max(0f, payer.Treasury), MathF.Max(0.10f, payer.Gdp * share));
                payer.Treasury -= payment;
                receiver.Treasury += payment;
                war.Reparations = payment;
            }
        }

        if (terms == PeaceTerms.TradeConcessions)
        {
            var tradeRelation = CampaignDiplomacy.Get(campaign, war.AttackerId, war.DefenderId);
            tradeRelation.TradeAgreement = true;
        }

        if (terms == PeaceTerms.DemilitarizedSettlement)
        {
            var winnerId = proposerId;
            var loserId = proposerId == war.AttackerId ? war.DefenderId : war.AttackerId;
            foreach (var division in campaign.Armies.Where(division => division.NationId == loserId))
            {
                division.Equipment = MathF.Min(division.Equipment, 0.45f);
                division.Cohesion = MathF.Min(division.Cohesion, 0.55f);
                division.Strength = MathF.Min(division.Strength, 0.72f);
            }
            var winner = campaign.NationById(winnerId);
            if (winner is not null) winner.WarSupport = MathF.Min(1f, winner.WarSupport + 0.04f);
        }

        if (terms == PeaceTerms.EnforceWarGoal && campaign.ProvinceController is not null &&
            campaign.ProvinceOccupation is not null)
        {
            foreach (var pair in war.OriginalController)
            {
                var inGoal = campaign.ProvinceMap?.ProvinceToState[pair.Key] == war.ClaimedStateMapIndex;
                var secured = campaign.ProvinceOccupation[pair.Key] >= campaign.Defines.OccupationSecureThreshold;
                if (!inGoal || !secured) campaign.SetProvinceController(pair.Key, pair.Value);
            }
        }

        if (terms == PeaceTerms.EnforceDemands && campaign.ProvinceController is not null &&
            campaign.ProvinceOccupation is not null)
        {
            foreach (var pair in war.OriginalController)
            {
                var secured = campaign.ProvinceOccupation[pair.Key] >= campaign.Defines.OccupationSecureThreshold;
                if (!secured) campaign.SetProvinceController(pair.Key, pair.Value);
            }
        }

        war.Ended = true;
        war.EndedDay = campaign.ElapsedDays;
        var sideScore = proposerId == war.AttackerId ? war.WarScore : -war.WarScore;
        war.WinnerId = terms == PeaceTerms.Capitulate
            ? (proposerId == war.AttackerId ? war.DefenderId : war.AttackerId)
            : sideScore > 0f ? proposerId : -1;
        war.PeaceSummary = terms switch
        {
            PeaceTerms.Ceasefire => "A one-year ceasefire restored the pre-war control map.",
            PeaceTerms.WhitePeace => "White peace restored the pre-war control map.",
            PeaceTerms.WarReparations => $"Pre-war borders were restored and ${war.Reparations:0.00}B in reparations changed hands.",
            PeaceTerms.TradeConcessions => $"Pre-war borders were restored; a trade agreement and ${war.Reparations:0.00}B concession were imposed.",
            PeaceTerms.DemilitarizedSettlement => "Pre-war borders were restored and the defeated armed forces were partially demobilised.",
            PeaceTerms.EnforceWarGoal => "Secured provinces inside the declared war goal were retained; all other advances were evacuated.",
            PeaceTerms.EnforceDemands => "Secured occupations were retained; unsecured advances were evacuated.",
            _ => $"{campaign.NationById(proposerId)?.Name ?? "A belligerent"} capitulated."
        };
        war.AttackerFront.Clear();
        war.DefenderFront.Clear();
        war.UnopposedProgress.Clear();
        var relation = CampaignDiplomacy.Get(campaign, war.AttackerId, war.DefenderId);
        relation.Stance = DiplomaticStance.Truce;
        relation.TruceUntil = campaign.ElapsedDays + (terms == PeaceTerms.Ceasefire ? 365 : 365 * 5);
        relation.LastActionDay = campaign.ElapsedDays;
        relation.Opinion = Math.Min(relation.Opinion, -35f);
        foreach (var army in campaign.Armies.Where(army => army.AssignedWarId == war.Id))
        {
            army.AssignedWarId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.MoveProgress = 0f;
        }
        foreach (var battle in campaign.Battles.Where(battle => battle.WarId == war.Id && !battle.Ended))
        {
            battle.Ended = true;
            battle.Outcome = "Peace agreement";
        }
        campaign.MarkStrategicChange();
    }
}

public sealed class ProposePeaceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int WarId { get; init; }
    public PeaceTerms Terms { get; init; }
    public void Apply(Campaign campaign)
    {
        var war = campaign.Wars.FirstOrDefault(candidate => candidate.Id == WarId);
        if (war is not null) PeaceNegotiation.Resolve(campaign, war, IssuerId, Terms);
    }
}
