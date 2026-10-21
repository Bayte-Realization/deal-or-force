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
}

public static class RecruitmentSystem
{
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
        var dynamicCap = 2 + barracks * 4 + nationalBarracks * 1.5f + popMillions * 1.2f;
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
        var atWar = false;
        foreach (var war in c.Wars)
            if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
        var baseCap = atWar ? c.Defines.RecruitmentWartimeBatchCap : c.Defines.RecruitmentBatchCap;
        var barracks = 0;
        foreach (var st in c.StatesOf(nation))
            barracks += st.Buildings.GetValueOrDefault(BuildingKind.Barracks);
        var bonus = barracks * (atWar ? 3 : 2);
        if (mode == MobilisationMode.RapidCallUp) bonus += 12;
        else if (mode == MobilisationMode.TotalMobilisation) bonus += 36;
        return baseCap + bonus;
    }

    public static string Validate(Campaign c, int issuer, int stateId, string templateId, int count,
        bool allowDeficit = false, MobilisationMode mode = MobilisationMode.Standard)
    {
        var nation = c.NationById(issuer);
        var state = c.States.Find(s => s.MapIndex == stateId);
        var template = UnitTypeCatalog.All.FirstOrDefault(t => t.Id == templateId);
        if (nation is null || c.ProvinceMap is null) return "Campaign map is not ready.";
        if (state is null || state.NationMapIndex != nation.MapIndex) return "Choose an owned training region.";
        var maxBatch = MaxDivisionsPerBatch(c, nation, state, mode);
        if (template is null || count < 1 || count > maxBatch)
            return Loc.Format("Choose a valid template and 1–{0} divisions.", maxBatch);
        if (DeploymentProvince(c, issuer, stateId) <= 0) return "No controlled province in this region.";

        var atWar = false;
        foreach (var war in c.Wars)
            if (!war.Ended && war.Involves(issuer)) { atWar = true; break; }
        var cap = DynamicQueueCap(c, nation, mode);
        if (c.Recruitment.Count(o => o.NationId == issuer) >= cap)
            return Loc.Format("Training capacity reached: {0} batches.", cap);

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
            StrategicStockpiles.EnsureSeeded(c, nation);
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
        if (!c.States.Any(s => s.MapIndex == stateId && s.NationMapIndex == nation.MapIndex)) return -1;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (map.ProvinceToState[p] == stateId && controllers[p] == nation.MapIndex) return p;
        return -1;
    }

    public static void Tick(Campaign c)
    {
        if (!MechanicFeatures.SystemEnabled(c, nameof(RecruitmentSystem))) return;
        foreach (var order in c.Recruitment.ToArray())
        {
            if (order.LastProcessedDay >= c.ElapsedDays) continue;
            order.LastProcessedDay = c.ElapsedDays;
            var province = DeploymentProvince(c, order.NationId, order.StateMapIndex);
            var nation = c.NationById(order.NationId);
            if (province <= 0 || nation is null) continue; // Training pauses under occupation.
            order.TrainedDays++;
            if (order.TrainedDays < order.DurationDays) continue;
            var map = c.ProvinceMap;
            var position = map.CenterOf(province);
            var id = c.Armies.Count == 0 ? 1 : c.Armies.Max(a => a.Id) + 1;
            var serial = c.Armies.Count(a => a.NationId == nation.Id) + 1;
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
        nation.Treasury += order.PaidCost * 0.75f * (1f - order.TrainedDays / (float)Math.Max(1, order.DurationDays));
        c.Recruitment.Remove(order);
        c.MarkStrategicChange();
    }
}
