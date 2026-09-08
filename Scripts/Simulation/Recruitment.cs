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
}

public static class RecruitmentSystem
{
    public static string Validate(Campaign c, int issuer, int stateId, string templateId, int count)
    {
        var nation = c.NationById(issuer);
        var state = c.States.Find(s => s.MapIndex == stateId);
        var template = UnitTypeCatalog.All.FirstOrDefault(t => t.Id == templateId);
        if (nation is null || c.ProvinceMap is null) return "Campaign map is not ready.";
        if (state is null || state.NationMapIndex != nation.MapIndex) return "Choose an owned training region.";
        if (template is null || count < 1 || count > 6) return "Choose a valid template and 1–6 divisions.";
        if (DeploymentProvince(c, issuer, stateId) <= 0) return "No controlled province in this region.";
        if (c.Recruitment.Count(o => o.NationId == issuer) >= 12) return "Training capacity reached: 12 batches.";
        if (nation.Treasury < template.RecruitmentCost * count) return "Insufficient treasury.";
        if (nation.ManpowerReserve < template.RecruitmentPersonnel * count) return "Insufficient manpower reserve.";
        return "";
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
            for (var i = 0; i < order.Count; i++)
                c.Armies.Add(new Army
                {
                    Id = id + i, NationId = nation.Id, StateMapIndex = order.StateMapIndex,
                    ProvinceId = province, Name = $"{serial + i}. {nation.Tag} Division", TemplateId = order.TemplateId,
                    Divisions = 1, Personnel = order.PersonnelPerDivision, MaxPersonnel = order.PersonnelPerDivision,
                    Organisation = 0.65f, Strength = 1, Equipment = 0.85f, Experience = 0.10f,
                    Cohesion = 0.70f, SupplyLevel = 0.85f, OrderKind = ArmyOrder.Manual, PlanActive = false,
                    Latitude = position.Latitude, Longitude = position.Longitude
                });
            c.Recruitment.Remove(order);
            DiagnosticLogs.Write("simulation", $"RECRUITMENT_COMPLETE order={order.Id} nation={order.NationId} template={order.TemplateId} count={order.Count} province={province} first_army_id={id}");
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
        var order = c.Recruitment.Find(o => o.Id == OrderId && o.NationId == IssuerId);
        var nation = c.NationById(IssuerId);
        if (order is null || nation is null) return;
        nation.ManpowerReserve += order.PersonnelPerDivision * order.Count;
        nation.Treasury += order.PaidCost * 0.75f * (1f - order.TrainedDays / (float)order.DurationDays);
        c.Recruitment.Remove(order);
        c.MarkStrategicChange();
    }
}
