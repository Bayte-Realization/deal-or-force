using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    public readonly List<RecruitmentOrder> Recruitment = new();
    public int NextRecruitmentId = 1;
}

public sealed class RecruitmentOrder
{
    public int Id;
    public int NationId;
    public int StateMapIndex;
    public string TemplateId = "line_infantry";
    public int Count;
    public int PersonnelPerDivision;
    public float PaidCost;
    public int DurationDays;
    public int TrainedDays;
    public int LastProcessedDay;
    public MobilisationMode Mode;
}

public sealed partial class Defines
{
    /// <summary>Baseline training batches one country may have in the queue at once in peace.</summary>
    public int RecruitmentBatchCap { get; set; } = 12;

    /// <summary>Baseline training batches in wartime.</summary>
    public int RecruitmentWartimeBatchCap { get; set; } = 24;

    /// <summary>Bound long-campaign peacetime AI 1-division army bloat while keeping full wartime mobilization.</summary>
    public bool AiPeacetimeForceLimitEnabled { get; set; } = true;

    /// <summary>Maximum standing divisions an AI nation raises or holds in peace (war removes this ceiling).</summary>
    public int AiPeacetimeMaxDivisions { get; set; } = 64;
}

public static class RecruitmentSystem
{
    /// <summary>The command's moddable feature binding, without the command-side refusal log.</summary>
    public static bool Enabled(Campaign c) => c is not null &&
        (!c.Defines.FeatureCommands.TryGetValue(nameof(RaiseArmyCommand), out var feature) ||
         string.IsNullOrWhiteSpace(feature) || c.FeatureEnabled(feature));

    /// <summary>
    /// What a barracks does for recruitment: divisions one order may raise in its own region,
    /// and anywhere in the country; and divisions the national queue holds, at peace and at war.
    /// </summary>
    public const int BatchPerLocalBarracks = 4;
    public const float BatchPerBarracks = 1.5f;
    public const int QueuePerBarracks = 2;
    public const int QueuePerBarracksAtWar = 3;

    /// <summary>
    /// Maximum divisions in one order is a training-throughput figure, not a fixed political
    /// ceiling. It grows with the region's barracks, the country's barracks and its people;
    /// the real limits are always manpower, treasury and the physical depot bill.
    /// </summary>
    public static int MaxDivisionsPerBatch(Campaign c, Nation nation, ScenarioState state, MobilisationMode mode = MobilisationMode.Standard)
    {
        if (c is null || nation is null) return 1;

        var barracks = state?.Buildings.GetValueOrDefault(BuildingKind.Barracks) ?? 0;
        var nationalBarracks = 0;
        foreach (var st in c.StatesOf(nation))
            nationalBarracks += st.Buildings.GetValueOrDefault(BuildingKind.Barracks);

        var popMillions = MathF.Max(1f, (state?.Population ?? nation.Population) / 1_000_000f);
        var dynamicCap = 2 + barracks * BatchPerLocalBarracks + nationalBarracks * BatchPerBarracks + popMillions * 1.2f;
        if (mode == MobilisationMode.RapidCallUp) dynamicCap *= 1.5f;
        else if (mode == MobilisationMode.TotalMobilisation) dynamicCap *= 2.25f;
        return Math.Clamp((int)MathF.Ceiling(dynamicCap), 1, 400);
    }

    /// <summary>
    /// Dynamic queue capacity based on peace/war, barracks, population and mobilisation mode.
    /// Preserves exact 12 / 24 cap on bare test fixtures with 0 barracks in Standard mode.
    /// </summary>
    public static int DynamicQueueCap(Campaign c, Nation nation, MobilisationMode mode = MobilisationMode.Standard)
    {
        if (c is null || nation is null) return 12;
        var atWar = Fighting(c, nation.Id);
        var baseCap = atWar ? c.Defines.RecruitmentWartimeBatchCap : c.Defines.RecruitmentBatchCap;
        var barracks = 0;
        foreach (var st in c.StatesOf(nation))
            barracks += st.Buildings.GetValueOrDefault(BuildingKind.Barracks);
        var bonus = barracks * (atWar ? QueuePerBarracksAtWar : QueuePerBarracks);
        if (mode == MobilisationMode.RapidCallUp) bonus += 12;
        else if (mode == MobilisationMode.TotalMobilisation) bonus += 36;
        return baseCap + bonus;
    }

    /// <summary>A war still being fought. An ended war does not keep the wartime queue.</summary>
    static bool Fighting(Campaign c, int nationId)
    {
        var live = Warfare.LiveWars(c);
        for (var index = 0; index < live.Count; index++)
            if (live[index].Involves(nationId)) return true;
        return false;
    }

    public static string Validate(Campaign c, int issuer, int stateId, string templateId, int count,
        bool allowDeficit = false, MobilisationMode mode = MobilisationMode.Standard)
    {
        if (c is null) return "Campaign map is not ready.";
        if (!Enabled(c)) return Loc.Get("Recruitment is disabled in this campaign.");
        var nation = c.NationById(issuer);
        var state = c.StateByMapIndex(stateId);
        UnitTypeDefinition template = null;
        foreach (var candidate in UnitTypeCatalog.All)
            if (candidate.Id == templateId) { template = candidate; break; }
        if (nation is null || c.ProvinceMap is null) return "Campaign map is not ready.";
        if (state is null || state.NationMapIndex != nation.MapIndex) return "Choose an owned training region.";
        var maxBatch = MaxDivisionsPerBatch(c, nation, state, mode);
        if (template is null || count < 1 || count > maxBatch)
            return Loc.Format("Choose a valid template and 1–{0} divisions.", maxBatch);
        if (DeploymentProvince(c, issuer, stateId) <= 0) return "No controlled province in this region.";

        var atWar = Fighting(c, issuer);
        var cap = DynamicQueueCap(c, nation, mode);
        var activeForIssuer = 0;
        var queuedDivisions = 0;
        var orders = c.Recruitment;
        for (var i = 0; i < orders.Count; i++)
        {
            if (orders[i].NationId == issuer)
            {
                activeForIssuer++;
                queuedDivisions += orders[i].Count;
            }
        }
        if (activeForIssuer >= cap)
            return Loc.Format("Training capacity reached: {0} batches.", cap);
        if (c.Defines.AiPeacetimeForceLimitEnabled && !nation.IsHuman && nation.Id != (c.Player?.Id ?? -1) &&
            !atWar && nation.WarPlanTargetId < 0 && c.Armies.Count + queuedDivisions + count >= 12)
        {
            var limit = PeacetimeAiForceLimit(c, nation);
            var standing = 0f;
            var armies = c.Armies;
            for (var i = 0; i < armies.Count; i++)
                if (armies[i].NationId == issuer) standing += armies[i].Divisions;
            if (standing + queuedDivisions + count > limit)
                return Loc.Format("Peacetime force limit reached: {0} divisions.", (int)MathF.Ceiling(limit));
        }

        var cost = TotalBatchCost(c, nation, template, count, mode, out _);
        if (!allowDeficit && nation.Treasury < cost)
            return "Insufficient treasury.";
        if (allowDeficit && nation.Treasury < cost)
        {
            var borrowed = cost - MathF.Max(0, nation.Treasury);
            var projected = SovereignFinance.Outstanding(nation) + borrowed;
            var limit = MathF.Max(0, nation.Gdp * c.Defines.WarDebtLimitShare);
            if (!atWar || !float.IsFinite(projected) || projected > limit)
                return "Wartime recruitment credit limit reached.";
        }
        if (nation.ManpowerReserve < template.RecruitmentPersonnel * count) return "Insufficient manpower reserve.";
        return "";
    }

    /// <summary>What one division of this template costs this country to raise (recruitment_cost).</summary>
    public static float CostOf(Nation nation, UnitTypeDefinition template, MobilisationMode mode = MobilisationMode.Standard)
    {
        var mult = mode switch
        {
            MobilisationMode.RapidCallUp => 1.35f,
            MobilisationMode.TotalMobilisation => 1.75f,
            _ => 1f
        };
        return template.RecruitmentCost * NationalModifiers.Factor(nation, "recruitment_cost") * mult;
    }

    /// <summary>Read-only procurement quote; admission consumes the physical bill in RaiseArmyCommand.</summary>
    public static float TotalBatchCost(Campaign c, Nation nation, UnitTypeDefinition template, int count,
        MobilisationMode mode, out float stockpileSurcharge)
    {
        stockpileSurcharge = 0f;
        var baseCost = CostOf(nation, template, mode) * count;
        if (c is not null && nation is not null)
        {
            // The depot bill is part of every posture, not a special wartime extra. A country
            // that has no ammunition or vehicles can still raise the division, but it pays
            // the emergency-procurement premium and drains what is physically in the depot.
            var bill = StrategicStockpiles.DivisionMaterialBill(template, count, mode);
            stockpileSurcharge = StrategicStockpiles.SurchargeForMissingStockpile(c, nation, bill, out _);
        }
        return baseCost + stockpileSurcharge;
    }

    /// <summary>Days one batch of this template takes this country to train (recruitment_speed).</summary>
    public static int TrainingDaysOf(Nation nation, UnitTypeDefinition template, MobilisationMode mode = MobilisationMode.Standard)
    {
        var baseDays = template.TrainingDays / NationalModifiers.Factor(nation, "recruitment_speed");
        return mode switch
        {
            MobilisationMode.RapidCallUp => Math.Max(7, (int)MathF.Round(baseDays * 0.40f)),
            MobilisationMode.TotalMobilisation => Math.Max(4, (int)MathF.Round(baseDays * 0.15f)),
            _ => Math.Max(1, (int)MathF.Round(baseDays))
        };
    }

    public static int DeploymentProvince(Campaign c, int issuer, int stateId)
    {
        var nation = c.NationById(issuer);
        var map = c.ProvinceMap;
        if (nation is null || map is null) return -1;
        // The state must be the nation's own: training pauses under occupation and
        // does not deploy into ground merely controlled.
        var state = c.StateByMapIndex(stateId);
        if (state is null || state.NationMapIndex != nation.MapIndex) return -1;
        // The state→provinces index turns a 35 000-province scan into a read of the
        // handful of provinces that state actually has.
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        foreach (var p in map.ProvincesOfState(stateId))
            if (controllers[p] == nation.MapIndex) return p;
        return -1;
    }

    internal static float PeacetimeAiForceLimit(Campaign c, Nation nation)
    {
        var popM = MathF.Max(1f, nation.Population / 1_000_000f);
        var gdp = MathF.Max(1f, nation.Gdp);
        return Math.Clamp(6f + MathF.Sqrt(popM) * 3.2f + MathF.Sqrt(gdp) * 0.9f,
            12f, Math.Max(16f, c.Defines.AiPeacetimeMaxDivisions));
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    private sealed class TickOrdersScratch
    {
        public readonly List<RecruitmentOrder> Orders = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, TickOrdersScratch> TickOrdersScratches = new();
    [ThreadStatic] private static Dictionary<int, float> _standingScratch;
    [ThreadStatic] private static HashSet<int> _atWarScratch;

    /// <summary>
    /// Gentle weekly peacetime equilibrium once world armies grow large in long campaigns:
    /// demobilises one idle weakest garrison per over-limit peaceful AI nation (men return to
    /// manpower reserve cleanly, never disrupting ongoing wars or war plans).
    /// </summary>
    private static void DemobilizePeacetimeSurplus(Campaign c)
    {
        if (!c.Defines.AiPeacetimeForceLimitEnabled || c.ElapsedDays < 90) return;
        if (c.Armies.Count < 1000 || c.ElapsedDays % 7 != 5) return;
        var standing = _standingScratch ??= new Dictionary<int, float>();
        var atWar = _atWarScratch ??= new HashSet<int>();
        standing.Clear();
        atWar.Clear();
        var live = Warfare.LiveWars(c);
        for (var wi = 0; wi < live.Count; wi++)
        {
            var w = live[wi];
            if (w.Ended) continue;
            atWar.Add(w.AttackerId);
            atWar.Add(w.DefenderId);
            foreach (var p in w.AttackerAllies) atWar.Add(p);
            foreach (var p in w.DefenderAllies) atWar.Add(p);
        }
        var armies = c.Armies;
        for (var ai = 0; ai < armies.Count; ai++)
        {
            var army = armies[ai];
            standing[army.NationId] = standing.GetValueOrDefault(army.NationId) + army.Divisions;
        }
        var active = c.ActiveNations;
        var playerId = c.Player?.Id ?? -1;
        for (var ni = 0; ni < active.Count; ni++)
        {
            var nation = active[ni];
            if (nation.IsHuman || nation.Id == playerId || nation.Population <= 0 || nation.Movement is not null) continue;
            if (nation.WarPlanTargetId >= 0 || atWar.Contains(nation.Id)) continue;
            var have = standing.GetValueOrDefault(nation.Id);
            if (have <= 0f) continue;
            var limit = PeacetimeAiForceLimit(c, nation);
            if (have <= limit * 1.25f) continue;
            Army weakestIdle = null;
            for (var ai = 0; ai < armies.Count; ai++)
            {
                var army = armies[ai];
                if (army.NationId != nation.Id || army.InBattle || army.AssignedWarId >= 0 || army.FrontLineId >= 0) continue;
                if (army.PlanActive && (army.OrderProvinceId >= 0 || army.Route.Count > 0 || army.PlannedRoute.Count > 0)) continue;
                if (weakestIdle is null || army.Power < weakestIdle.Power) weakestIdle = army;
            }
            if (weakestIdle is not null)
                Demobilisation.StandDown(c, nation, weakestIdle, "peacetime_demobilisation");
        }
        standing.Clear();
        atWar.Clear();
    }

    /// <summary>The same recruitment pass, yielded between orders.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (!MechanicFeatures.SystemEnabled(c, nameof(RecruitmentSystem))) yield break;
        DemobilizePeacetimeSurplus(c);
        if (c.Recruitment.Count == 0) yield break;
        var orders = TickOrdersScratches.GetOrCreateValue(c).Orders;
        orders.Clear();
        for (var i = 0; i < c.Recruitment.Count; i++) orders.Add(c.Recruitment[i]);
        var ticked = 0;
        for (var oi = 0; oi < orders.Count; oi++)
        {
            var order = orders[oi];
            if (++ticked % 16 == 0 || Campaign.StageOverdue()) yield return 0;
            if (order.LastProcessedDay >= c.ElapsedDays) continue;
            order.LastProcessedDay = c.ElapsedDays;
            var province = DeploymentProvince(c, order.NationId, order.StateMapIndex);
            var nation = c.NationById(order.NationId);
            if (province <= 0 || nation is null) continue; // Training pauses under occupation.
            order.TrainedDays++;
            if (order.TrainedDays < order.DurationDays) continue;
            var map = c.ProvinceMap;
            var position = map.CenterOf(province);
            var maxArmyId = 0;
            var serialCount = 0;
            for (var ai = 0; ai < c.Armies.Count; ai++)
            {
                var army = c.Armies[ai];
                if (army.Id > maxArmyId) maxArmyId = army.Id;
                if (army.NationId == nation.Id) serialCount++;
            }
            var id = c.Armies.Count == 0 ? 1 : maxArmyId + 1;
            var serial = serialCount + 1;
            var (initOrg, initEquip, initExp, initCoh) = order.Mode switch
            {
                MobilisationMode.RapidCallUp => (0.55f, 0.78f, 0.06f, 0.62f),
                MobilisationMode.TotalMobilisation => (0.45f, 0.70f, 0.04f, 0.52f),
                _ => (0.65f, 0.85f, 0.10f, 0.70f)
            };
            for (var i = 0; i < order.Count; i++)
                c.Armies.Add(new Army
                {
                    Id = id + i, NationId = nation.Id, StateMapIndex = order.StateMapIndex,
                    ProvinceId = province, Name = $"{serial + i}. {nation.Tag} Division", TemplateId = order.TemplateId,
                    Divisions = 1, Personnel = order.PersonnelPerDivision, MaxPersonnel = order.PersonnelPerDivision,
                    Organisation = initOrg, Strength = 1, Equipment = initEquip, Experience = initExp,
                    Cohesion = initCoh, SupplyLevel = 0.85f, OrderKind = ArmyOrder.Manual, PlanActive = false,
                    Latitude = position.Latitude, Longitude = position.Longitude
                });
            c.Recruitment.Remove(order);
            DiagnosticLogs.Write("simulation", $"RECRUITMENT_COMPLETE order={order.Id} nation={order.NationId} template={order.TemplateId} count={order.Count} mode={order.Mode} province={province} first_army_id={id}");
            c.MarkStrategicChange();
        }
    }
}

public sealed class CancelRecruitmentCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int OrderId { get; init; }
    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var order = c.Recruitment.Find(o => o.Id == OrderId && o.NationId == IssuerId);
        var nation = c.NationById(IssuerId);
        if (order is null || nation is null) return;
        nation.ManpowerReserve += order.PersonnelPerDivision * order.Count;
        { var ledgerBefore = nation.Treasury; nation.Treasury += order.PaidCost * 0.75f * (1f - order.TrainedDays / (float)Math.Max(1, order.DurationDays)); MoneyLedger.Book(nation, MoneyFlow.Military, nation.Treasury - ledgerBefore); }
        c.Recruitment.Remove(order);
        c.MarkStrategicChange();
    }
}
