using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation.Controls
{
    public enum BatchActionKind
    {
        Construction,
        Recruitment,
        Budget
    }

    public sealed class RecruitmentUndoItem
    {
        public int OrderId { get; set; }
        public int StateMapIndex { get; set; }
        public int CreatedDay { get; set; }
        public bool WasStockpilesSeeded { get; set; }
        public Dictionary<string, float> StockpileRefunds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public float StabilityDelta { get; set; }
        public float RadicalismDelta { get; set; }
        public float LivingDelta { get; set; }
    }

    public sealed class PolicyRateUndoItem
    {
        public string LineId { get; set; } = "";
        public float PreviousRate { get; set; }
    }

    public sealed class BatchUndoRecord
    {
        public int IssuerId { get; init; }
        public BatchActionKind Kind { get; init; }
        public int RegionCount { get; init; }
        public int ItemCount { get; init; }
        public float TotalCost { get; init; }
        public string LabelKey { get; init; } = "";
        public string SummaryText { get; init; } = "";
        public List<int> ConstructionIds { get; init; } = new();
        public List<RecruitmentUndoItem> RecruitmentItems { get; init; } = new();
        public List<PolicyRateUndoItem> PolicyRates { get; init; } = new();
    }

    /// <summary>
    /// Deterministic command that reverses the unstarted portion of the player's last mass action
    /// (construction, recruitment, or zero-budget adjustment).
    /// </summary>
    public sealed class UndoBatchActionCommand : ICampaignCommand
    {
        public int IssuerId { get; init; }
        public BatchActionKind Kind { get; init; }
        public List<int> ConstructionIds { get; init; } = new();
        public List<RecruitmentUndoItem> RecruitmentItems { get; init; } = new();
        public List<PolicyRateUndoItem> PolicyRates { get; init; } = new();

        public int UndoneCount { get; private set; }
        public float RefundedCash { get; private set; }
        public int RefundedManpower { get; private set; }

        public void Apply(Campaign campaign)
        {
            if (campaign is null || !MechanicFeatures.Allows(campaign, this)) return;
            var nation = campaign.NationById(IssuerId);
            if (nation is null) return;

            var undone = 0;
            foreach (var projectId in ConstructionIds)
            {
                var project = campaign.Construction.Find(p => p.Id == projectId && p.NationId == IssuerId);
                if (project is null) continue;
                // Only unstarted queued sites are undone; started ones remain untouched.
                if (project.Paid > 0.0001f || project.Progress > 0.0001f) continue;
                campaign.Construction.Remove(project);
                undone++;
            }

            for (var idx = RecruitmentItems.Count - 1; idx >= 0; idx--)
            {
                var item = RecruitmentItems[idx];
                var order = campaign.Recruitment.Find(o => o.Id == item.OrderId && o.NationId == IssuerId);
                if (order is null) continue;
                // Only unstarted orders (TrainedDays == 0) refund 100%.
                if (order.TrainedDays > 0) continue;

                var beforeTreasury = nation.Treasury;
                nation.Treasury += order.PaidCost;
                MoneyLedger.Book(nation, MoneyFlow.Military, nation.Treasury - beforeTreasury);
                RefundedCash += order.PaidCost;

                var manpower = order.PersonnelPerDivision * order.Count;
                nation.ManpowerReserve += manpower;
                RefundedManpower += manpower;

                if (item.StockpileRefunds is { Count: > 0 })
                {
                    foreach (var (goodId, delta) in item.StockpileRefunds)
                    {
                        if (delta == 0f) continue;
                        nation.Stockpiles[goodId] = MathF.Max(0f, nation.Stockpiles.GetValueOrDefault(goodId) + delta);
                    }
                }
                if (!item.WasStockpilesSeeded && campaign.ElapsedDays == item.CreatedDay &&
                    campaign.Recruitment.All(o => o.NationId != IssuerId || o.Id == order.Id))
                {
                    nation.Stockpiles.Clear();
                    nation.StockpilesSeeded = false;
                }

                if (item.StabilityDelta != 0f)
                    nation.Stability = M.Clamp(nation.Stability + item.StabilityDelta, 0.05f, 1f);
                if (item.RadicalismDelta != 0f || item.LivingDelta != 0f)
                {
                    var state = campaign.StateByMapIndex(item.StateMapIndex);
                    if (state is not null)
                    {
                        if (item.RadicalismDelta != 0f)
                            state.Radicalism = M.Clamp01(state.Radicalism + item.RadicalismDelta);
                        if (item.LivingDelta != 0f)
                            state.StandardOfLiving = M.Clamp01(state.StandardOfLiving + item.LivingDelta);
                    }
                }

                campaign.Recruitment.Remove(order);
                undone++;
            }

            foreach (var rate in PolicyRates)
            {
                var line = BudgetCore.Find(rate.LineId);
                if (line is null) continue;
                BudgetCore.SetRate(nation, line, rate.PreviousRate);
                undone++;
            }

            UndoneCount = undone;
            campaign.PendingBatchUndo = null;
            if (undone > 0) campaign.MarkStrategicChange();
        }
    }

    /// <summary>
    /// Timeless Undo coordinator: keeps the last mass action ready for Ctrl+Z across any number
    /// of simulation ticks until the player issues their next state-changing command.
    /// Does not alter CampaignFingerprint while pending.
    /// </summary>
    public static class BatchActionUndo
    {
        public static event Action<Campaign> Changed;

        public static BatchUndoRecord Peek(Campaign campaign, int playerId)
        {
            if (campaign?.PendingBatchUndo is not { } rec || rec.IssuerId != playerId) return null;
            return HasUndoableEntries(campaign, rec) ? rec : null;
        }

        public static bool CanUndo(Campaign campaign, int playerId) => Peek(campaign, playerId) is not null;

        public static void Clear(Campaign campaign)
        {
            if (campaign is null || campaign.PendingBatchUndo is null) return;
            campaign.PendingBatchUndo = null;
            Changed?.Invoke(campaign);
        }

        private static bool HasUndoableEntries(Campaign campaign, BatchUndoRecord rec)
        {
            foreach (var id in rec.ConstructionIds)
                if (campaign.Construction.Any(p => p.Id == id && p.NationId == rec.IssuerId &&
                                                   p.Paid <= 0.0001f && p.Progress <= 0.0001f))
                    return true;
            foreach (var item in rec.RecruitmentItems)
                if (campaign.Recruitment.Any(o => o.Id == item.OrderId && o.NationId == rec.IssuerId && o.TrainedDays == 0))
                    return true;
            return rec.PolicyRates.Count > 0;
        }

        /// <summary>
        /// Called from CampaignCommandQueue.ApplyAll after each command applies.
        /// Ignores AI/foreign commands and commands executed during batch capture.
        /// Clears the pending batch undo as soon as the owning player performs any other state change.
        /// </summary>
        public static void OnCommandApplied(Campaign campaign, ICampaignCommand command)
        {
            if (campaign is null || command is null) return;
            if (campaign.CapturingBatchUndo) return;
            if (command is UndoBatchActionCommand) return;
            if (campaign.PendingBatchUndo is { } rec && command.IssuerId == rec.IssuerId)
            {
                campaign.PendingBatchUndo = null;
                Changed?.Invoke(campaign);
            }
        }

        public static UndoBatchActionCommand CreateUndoCommand(Campaign campaign, int playerId)
        {
            var rec = Peek(campaign, playerId);
            if (rec is null) return null;
            return new UndoBatchActionCommand
            {
                IssuerId = rec.IssuerId,
                Kind = rec.Kind,
                ConstructionIds = new List<int>(rec.ConstructionIds),
                RecruitmentItems = rec.RecruitmentItems.Select(i => new RecruitmentUndoItem
                {
                    OrderId = i.OrderId,
                    StateMapIndex = i.StateMapIndex,
                    CreatedDay = i.CreatedDay,
                    WasStockpilesSeeded = i.WasStockpilesSeeded,
                    StockpileRefunds = new Dictionary<string, float>(i.StockpileRefunds, StringComparer.OrdinalIgnoreCase),
                    StabilityDelta = i.StabilityDelta,
                    RadicalismDelta = i.RadicalismDelta,
                    LivingDelta = i.LivingDelta
                }).ToList(),
                PolicyRates = rec.PolicyRates.Select(r => new PolicyRateUndoItem
                {
                    LineId = r.LineId,
                    PreviousRate = r.PreviousRate
                }).ToList()
            };
        }

        public static bool TryUndo(Campaign campaign, int playerId, out UndoBatchActionCommand executed)
        {
            executed = CreateUndoCommand(campaign, playerId);
            if (campaign is null || executed is null) return false;
            campaign.Commands.Enqueue(executed);
            campaign.Commands.ApplyAll(campaign);
            Changed?.Invoke(campaign);
            return executed.UndoneCount > 0;
        }

        public static EverywhereBuildPlan ExecuteEverywhereConstruction(Campaign campaign, Nation nation,
            IReadOnlyList<ScenarioState> regions, BuildingKind kind, int multiplierPerRegion)
        {
            var plan = QuickAmountRules.PlanEverywhereConstruction(campaign, nation, regions, kind, multiplierPerRegion);
            if (campaign is null || nation is null || plan.Items.Count == 0) return plan;

            var existingIds = campaign.Construction.Select(p => p.Id).ToHashSet();
            var createdIds = new List<int>();

            campaign.CapturingBatchUndo = true;
            try
            {
                foreach (var item in plan.Items)
                {
                    for (var i = 0; i < item.Count; i++)
                    {
                        campaign.Commands.Enqueue(new QueueConstructionCommand
                        {
                            IssuerId = nation.Id,
                            StateMapIndex = item.Region.MapIndex,
                            Building = kind
                        });
                    }
                }
                campaign.Commands.ApplyAll(campaign);

                foreach (var project in campaign.Construction)
                    if (project.NationId == nation.Id && !existingIds.Contains(project.Id))
                        createdIds.Add(project.Id);

                if (createdIds.Count > 0)
                {
                    var defName = BuildingCatalog.Get(kind).Name;
                    campaign.PendingBatchUndo = new BatchUndoRecord
                    {
                        IssuerId = nation.Id,
                        Kind = BatchActionKind.Construction,
                        RegionCount = plan.Items.Count,
                        ItemCount = createdIds.Count,
                        TotalCost = plan.TotalCost,
                        LabelKey = defName,
                        SummaryText = Loc.Format("{0} × {1}", createdIds.Count, Loc.Get(defName)),
                        ConstructionIds = createdIds
                    };
                    Changed?.Invoke(campaign);
                }
            }
            finally
            {
                campaign.CapturingBatchUndo = false;
            }
            return plan;
        }

        public static EverywhereRecruitPlan ExecuteEverywhereRecruitment(Campaign campaign, Nation nation,
            IReadOnlyList<ScenarioState> regions, string templateId, MobilisationMode mode,
            int quarterOrCount, bool isQuarter, bool allowDeficit = false)
        {
            var plan = QuickAmountRules.PlanEverywhereRecruitment(campaign, nation, regions, templateId, mode,
                quarterOrCount, isQuarter, allowDeficit);
            if (campaign is null || nation is null || plan.Items.Count == 0) return plan;

            var createdItems = new List<RecruitmentUndoItem>();
            campaign.CapturingBatchUndo = true;
            try
            {
                foreach (var item in plan.Items)
                {
                    var beforeIds = campaign.Recruitment.Select(o => o.Id).ToHashSet();
                    var seededBefore = nation.StockpilesSeeded;
                    var stockBefore = new Dictionary<string, float>(nation.Stockpiles, StringComparer.OrdinalIgnoreCase);
                    var stabBefore = nation.Stability;
                    var radBefore = item.Region.Radicalism;
                    var livBefore = item.Region.StandardOfLiving;

                    campaign.Commands.Enqueue(new RaiseArmyCommand
                    {
                        IssuerId = nation.Id,
                        StateMapIndex = item.Region.MapIndex,
                        TemplateId = templateId,
                        Divisions = item.Divisions,
                        Mode = mode,
                        AllowDeficit = allowDeficit
                    });
                    campaign.Commands.ApplyAll(campaign);

                    var added = campaign.Recruitment.FirstOrDefault(o => o.NationId == nation.Id && !beforeIds.Contains(o.Id));
                    if (added is null) continue;

                    var stockRefunds = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    if (seededBefore)
                    {
                        foreach (var (goodId, beforeVal) in stockBefore)
                        {
                            var afterVal = nation.Stockpiles.GetValueOrDefault(goodId);
                            var diff = beforeVal - afterVal;
                            if (MathF.Abs(diff) > 0.0001f) stockRefunds[goodId] = diff;
                        }
                    }

                    createdItems.Add(new RecruitmentUndoItem
                    {
                        OrderId = added.Id,
                        StateMapIndex = item.Region.MapIndex,
                        CreatedDay = campaign.ElapsedDays,
                        WasStockpilesSeeded = seededBefore,
                        StockpileRefunds = stockRefunds,
                        StabilityDelta = stabBefore - nation.Stability,
                        RadicalismDelta = radBefore - item.Region.Radicalism,
                        LivingDelta = livBefore - item.Region.StandardOfLiving
                    });
                }

                if (createdItems.Count > 0)
                {
                    var template = UnitTypeCatalog.All.FirstOrDefault(t => t.Id == templateId);
                    var unitName = template?.Name ?? templateId;
                    campaign.PendingBatchUndo = new BatchUndoRecord
                    {
                        IssuerId = nation.Id,
                        Kind = BatchActionKind.Recruitment,
                        RegionCount = createdItems.Count,
                        ItemCount = plan.TotalDivisions,
                        TotalCost = plan.TotalCost,
                        LabelKey = unitName,
                        SummaryText = Loc.Format("{0} × {1}", plan.TotalDivisions, Loc.Get(unitName)),
                        RecruitmentItems = createdItems
                    };
                    Changed?.Invoke(campaign);
                }
            }
            finally
            {
                campaign.CapturingBatchUndo = false;
            }
            return plan;
        }

        public static ZeroBudgetPlan ExecuteZeroBudget(Campaign campaign, Nation nation, bool adjustTaxes)
        {
            var plan = QuickAmountRules.PlanBalanceToZero(campaign, nation, adjustTaxes);
            if (campaign is null || nation is null || plan.Changes.Count == 0) return plan;

            var undoRates = new List<PolicyRateUndoItem>();
            campaign.CapturingBatchUndo = true;
            try
            {
                foreach (var change in plan.Changes)
                {
                    undoRates.Add(new PolicyRateUndoItem
                    {
                        LineId = change.LineId,
                        PreviousRate = change.OldRate
                    });
                    campaign.Commands.Enqueue(new SetPolicyRateCommand
                    {
                        IssuerId = nation.Id,
                        LineId = change.LineId,
                        Value = change.NewRate
                    });
                }
                campaign.Commands.ApplyAll(campaign);

                campaign.PendingBatchUndo = new BatchUndoRecord
                {
                    IssuerId = nation.Id,
                    Kind = BatchActionKind.Budget,
                    RegionCount = 1,
                    ItemCount = undoRates.Count,
                    TotalCost = 0f,
                    LabelKey = "Balance to zero",
                    SummaryText = plan.Summary,
                    PolicyRates = undoRates
                };
                Changed?.Invoke(campaign);
            }
            finally
            {
                campaign.CapturingBatchUndo = false;
            }
            return plan;
        }
    }
}

namespace DealOrForce.Simulation
{
    public sealed partial class Campaign
    {
        internal Controls.BatchUndoRecord PendingBatchUndo;
        internal bool CapturingBatchUndo;
    }
}
