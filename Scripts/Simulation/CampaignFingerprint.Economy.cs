using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class CampaignFingerprint
{
    private static void MixTradeTerms(ref ulong hash, TradeTerms t)
    {
        Mix(ref hash, t.SignedDay); Mix(ref hash, t.EndsDay); Mix(ref hash, t.Years);
        MixFloat(ref hash, t.CutA); MixFloat(ref hash, t.CutB);
        foreach (var g in t.GoodsA ?? Array.Empty<string>()) MixText(ref hash, g);
        Mix(ref hash, -121);
        foreach (var g in t.GoodsB ?? Array.Empty<string>()) MixText(ref hash, g);
        Mix(ref hash, -122);
        Mix(ref hash, t.GuarantorId); MixText(ref hash, t.GuaranteeGood); MixFloat(ref hash, t.GuaranteeShare);
        MixFloat(ref hash, t.Delivered); Mix(ref hash, t.ShortWeeks); Mix(ref hash, t.GuarantorShortWeeks);
        MixText(ref hash, t.Preset); Mix(ref hash, t.RenewalOfferedDay); Mix(ref hash, t.Renewal ? 1 : 0);
    }

    private static void MixEconomyAiRules(Campaign campaign, ref ulong hash)
    {
        if (campaign.Defines.AiFiscalReserveWeeks != 8)
        {
            MixText(ref hash, "ai_fiscal_runway");
            Mix(ref hash, campaign.Defines.AiFiscalReserveWeeks);
        }
        if (!campaign.Defines.AiCrisisGrants || campaign.Defines.AiCrisisGrantRetryDays != 360)
        {
            MixText(ref hash, "crisis_grant_rule");
            Mix(ref hash, campaign.Defines.AiCrisisGrants ? 1 : 0);
            Mix(ref hash, campaign.Defines.AiCrisisGrantRetryDays);
        }
        if (!campaign.Defines.AiSupplyHedges || campaign.Defines.AiSupplyHedgeShare != 0.75f ||
            campaign.Defines.AiSupplyHedgeSatisfaction != 0.875f || campaign.Defines.AiSupplyHedgeRetryDays != 180)
        {
            MixText(ref hash, "supply_hedge_rule");
            Mix(ref hash, campaign.Defines.AiSupplyHedges ? 1 : 0);
            MixFloat(ref hash, campaign.Defines.AiSupplyHedgeShare);
            MixFloat(ref hash, campaign.Defines.AiSupplyHedgeSatisfaction);
            Mix(ref hash, campaign.Defines.AiSupplyHedgeRetryDays);
        }
    }

    private static void MixWorldCreditCycle(Campaign campaign, ref ulong hash)
    {
        // The world credit cycle (TreasuryDynamics); a campaign that has not started it carries no bytes.
        if (campaign.WorldLeverage >= 0f || campaign.LastFinancialCrisisDay != -100000)
        { MixText(ref hash, "credit_cycle"); MixFloat(ref hash, campaign.WorldLeverage); Mix(ref hash, campaign.LastFinancialCrisisDay); }
    }

    private static void MixLandArrangementsAndLeases(Campaign campaign, ref ulong hash)
    {
        // Leases and troops in without a war, and the offers of leases on a player's desk.
        if (campaign.LandArrangements.Count > 0 || campaign.NextLandArrangementId != 1)
        {
            MixText(ref hash, "land_arrangements"); Mix(ref hash, campaign.NextLandArrangementId); Mix(ref hash, campaign.LandArrangements.Count);
            foreach (var arrangement in campaign.LandArrangements.OrderBy(a => a.Id))
            {
                Mix(ref hash, arrangement.Id); MixText(ref hash, arrangement.Kind); Mix(ref hash, arrangement.OwnerId);
                Mix(ref hash, arrangement.HolderId); Mix(ref hash, arrangement.StartDay); Mix(ref hash, arrangement.EndDay);
                MixFloat(ref hash, arrangement.RentPerYear); Mix(ref hash, arrangement.UnpaidWeeks);
                MixText(ref hash, arrangement.Purpose); MixText(ref hash, arrangement.Origin); Mix(ref hash, arrangement.LeavingOnDay);
                foreach (var region in arrangement.Regions) Mix(ref hash, region);
                Mix(ref hash, -105);
            }
        }
        if (campaign.LeaseOffers.Count > 0 || campaign.NextLeaseOfferId != 1)
        {
            MixText(ref hash, "lease_offers"); Mix(ref hash, campaign.NextLeaseOfferId); Mix(ref hash, campaign.LeaseOffers.Count);
            foreach (var offer in campaign.LeaseOffers.OrderBy(o => o.Id))
            {
                Mix(ref hash, offer.Id); Mix(ref hash, offer.FromId); Mix(ref hash, offer.ToId); Mix(ref hash, offer.Region);
                Mix(ref hash, offer.Years); Mix(ref hash, offer.CreatedDay); Mix(ref hash, offer.ExpiresDay);
                MixFloat(ref hash, offer.RentPerYear); Mix(ref hash, offer.Renewal ? 1 : 0);
                Mix(ref hash, offer.ArrangementId); MixText(ref hash, offer.Purpose); MixText(ref hash, offer.State);
                Mix(ref hash, -106);
            }
        }
    }

    private static void MixDealOffersAndAssistance(Campaign campaign, ref ulong hash)
    {
        if (campaign.DealOffers.Count > 0 || campaign.NextDealOfferId != 1)
        {
            MixText(ref hash, "deal_negotiations"); Mix(ref hash, campaign.NextDealOfferId); Mix(ref hash, campaign.DealOffers.Count);
            foreach (var paper in campaign.DealOffers.OrderBy(o => o.Id))
            {
                Mix(ref hash, paper.Id); Mix(ref hash, paper.FromId); Mix(ref hash, paper.ToId);
                Mix(ref hash, paper.CreatedDay); Mix(ref hash, paper.ExpiresDay);
                // Reason is presentation text; locale must not diverge authoritative state.
                Mix(ref hash, paper.ClosedDay); Mix(ref hash, paper.ParentOfferId); Mix(ref hash, paper.CounterDepth); Mix(ref hash, (int)paper.State);
                if (paper.ByRule) Mix(ref hash, -104);
                foreach (var province in paper.FromProvinces ?? Array.Empty<int>()) Mix(ref hash, province);
                Mix(ref hash, -102);
                foreach (var province in paper.ToProvinces ?? Array.Empty<int>()) Mix(ref hash, province);
                Mix(ref hash, -103);
                if (paper.Trade is not null) { MixText(ref hash, "deal_trade_terms_v1"); MixTradeTerms(ref hash, paper.Trade); }
                foreach (var side in new[] { paper.From, paper.To })
                {
                    MixFloat(ref hash, side.Cash); Mix(ref hash, side.ReleaseRegions ? 1 : 0); Mix(ref hash, side.LiftEmbargo ? 1 : 0);
                    Mix(ref hash, side.NonAggression ? 1 : 0); Mix(ref hash, side.TradeAgreement ? 1 : 0);
                    foreach (var region in side.Regions ?? Array.Empty<int>()) Mix(ref hash, region);
                    Mix(ref hash, -101);
                }
            }
        }
        MixForeignAssistance(campaign, ref hash);
    }

    private static void MixForeignAssistance(Campaign campaign, ref ulong hash)
    {
        MixText(ref hash, "foreign_assistance"); Mix(ref hash, campaign.NextAssistanceRequestId); Mix(ref hash, campaign.AssistanceLastTickDay);
        Mix(ref hash, campaign.AssistanceRequests.Count);
        foreach (var request in campaign.AssistanceRequests.OrderBy(r => r.Id))
        {
            Mix(ref hash, request.Id); Mix(ref hash, request.RequesterId); Mix(ref hash, request.DonorId); Mix(ref hash, request.WarId);
            Mix(ref hash, request.CreatedDay); Mix(ref hash, request.ExpiresDay); Mix(ref hash, request.ClosedDay); Mix(ref hash, (int)request.State);
            MixFloat(ref hash, request.Amount); MixFloat(ref hash, request.GrantedAmount); Mix(ref hash, request.HumanConsent ? 1 : 0);
        }
        Mix(ref hash, campaign.AssistanceCooldowns.Count);
        foreach (var clock in campaign.AssistanceCooldowns.OrderBy(x => x.RequesterId).ThenBy(x => x.DonorId))
        { Mix(ref hash, clock.RequesterId); Mix(ref hash, clock.DonorId); Mix(ref hash, clock.ReadyOnDay); }
        Mix(ref hash, campaign.Defines.AidRequestCooldownDays); Mix(ref hash, campaign.Defines.AidRequestOfferDays);
        Mix(ref hash, campaign.Defines.AiAidRequestReviewDays); MixFloat(ref hash, campaign.Defines.AiAidRequestWillingness);
        Mix(ref hash, campaign.Defines.AidIntervalDays); MixFloat(ref hash, campaign.Defines.AidShareOfRecipientGdp);
        MixFloat(ref hash, campaign.Defines.AidMaxShareOfTreasury); MixFloat(ref hash, campaign.Defines.AiAidReserveOfGdp);
        MixFloat(ref hash, campaign.Defines.AiAidCrisisTreasuryOfGdp);
    }

    private static void MixNationFinances(Nation nation, ref ulong hash)
    {
        Mix(ref hash, nation.Population);
        MixFloat(ref hash, nation.Treasury);
        Mix(ref hash, nation.DebtInitialized ? 1 : 0); Mix(ref hash, nation.DebtMechanicActive ? 1 : 0);
        MixFloat(ref hash, nation.PublicDebt); MixFloat(ref hash, nation.DebtArrears); MixFloat(ref hash, nation.PaymentArrears);
        MixFloat(ref hash, nation.CreditConfidence); MixFloat(ref hash, nation.DebtSpreadAnnual);
        MixFloat(ref hash, nation.DebtIssuedWeekly); MixFloat(ref hash, nation.DebtInterestWeekly);
        Mix(ref hash, nation.DebtMissedWeeks); Mix(ref hash, nation.DebtCrisisWeeks); Mix(ref hash, (int)nation.DebtCondition);
        Mix(ref hash, nation.DebtRestructuringProgress); Mix(ref hash, nation.DebtRestructuringLastDay);
        // Timed income penalties (IncomePenalties). A country carrying none adds no bytes, so
        // every campaign without one keeps the fingerprint it had before they existed.
        if (nation.IncomePenalties is { Count: > 0 } incomePenalties)
        {
            Mix(ref hash, -0x1C0);
            foreach (var penalty in incomePenalties)
            { MixFloat(ref hash, penalty.Share); Mix(ref hash, penalty.StartDay); Mix(ref hash, penalty.UntilDay); MixText(ref hash, penalty.Source); }
        }
    }

    private static void MixNationOutputSummary(Nation nation, ref ulong hash)
    {
        MixFloat(ref hash, nation.Gdp);
        MixFloat(ref hash, nation.WeeklyBalance);
        MixFloat(ref hash, nation.LivingOffset);
        MixText(ref hash, nation.StartGovernment); MixText(ref hash, nation.StartIdeology);
    }

    private static void MixNationStockpiles(Nation nation, ref ulong hash)
    {
        if (nation.HasCustomStockpiles)
        {
            MixText(ref hash, "strategic_stockpiles");
            Mix(ref hash, nation.StockpilesSeeded ? 1 : 0);
            Mix(ref hash, nation.Stockpiles.Count);
            foreach (var entry in nation.Stockpiles.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            { MixText(ref hash, entry.Key.ToLowerInvariant()); MixFloat(ref hash, entry.Value); }
        }
        if (nation.StockpileCoverage.Count > 0)
        {
            MixText(ref hash, "stockpile_coverage");
            foreach (var entry in nation.StockpileCoverage.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            { MixText(ref hash, entry.Key.ToLowerInvariant()); MixFloat(ref hash, entry.Value); }
        }
    }

    private static void MixStateEconomyAndLivelihood(ScenarioState state, ref ulong hash)
    {
        Mix(ref hash, state.Population); MixFloat(ref hash, state.StandardOfLiving); MixFloat(ref hash, state.Radicalism); MixFloat(ref hash, state.WarDamage);
        MixFloat(ref hash, state.NonBuildingJobs); Mix(ref hash, state.LastHouseholdReviewDay); Mix(ref hash, state.ReviewBirths); Mix(ref hash, state.ReviewDeaths);
        MixFloat(ref hash, state.BasicNeedsCoverage); MixFloat(ref hash, state.EmploymentTarget); MixFloat(ref hash, state.LivelihoodStress);
        if (state.ReviewNetMigration != 0 || state.BrainDrainPressure != 0f)
        {
            MixText(ref hash, "state_migration"); Mix(ref hash, state.ReviewNetMigration); MixFloat(ref hash, state.BrainDrainPressure);
        }
    }

    private static void MixTradeAttribution(Campaign campaign, ref ulong hash)
    {
        // Previous-market attribution is input to supply diplomacy between weekly
        // settlements. An unknown legacy week adds no bytes; a known (even empty)
        // week and its original summation order are authoritative campaign state.
        if (campaign.TradeRoutesDay >= 0 || campaign.TradeRoutes.Count > 0)
        {
            MixText(ref hash, "trade_attribution_v1"); Mix(ref hash, campaign.TradeRoutesDay);
            Mix(ref hash, campaign.TradeRoutes.Count);
            foreach (var route in campaign.TradeRoutes)
            {
                Mix(ref hash, route.SellerId); Mix(ref hash, route.BuyerId); Mix(ref hash, (int)route.Commodity);
                MixFloat(ref hash, route.Volume); MixFloat(ref hash, route.Value);
            }
            // The week's imports that came in under a tariff cut: they set next week's markup and
            // this week's duty. Mixed only where there were any.
            foreach (var nation in campaign.Nations.OrderBy(n => n.Id))
                foreach (var quote in nation.Market.Values.OrderBy(q => (int)q.Commodity))
                    if (quote.Relieved != 0f) { Mix(ref hash, nation.Id); Mix(ref hash, (int)quote.Commodity); MixFloat(ref hash, quote.Relieved); }
        }
    }
}
