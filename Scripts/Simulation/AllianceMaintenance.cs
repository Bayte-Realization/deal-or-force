using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool DynamicAlliances { get; set; } = true;
    public int AllianceReviewDays { get; set; } = 7;
    public int AllianceGraceDays { get; set; } = 90;
    public int AllianceRuptureDays { get; set; } = 56;
    public float AllianceCohesionResponse { get; set; } = 0.12f;
    public float AllianceRuptureCohesion { get; set; } = 0.32f;
    public int AllianceReaffirmDays { get; set; } = 90;
    public float AllianceReaffirmBoost { get; set; } = 0.18f;
}

/// <summary>Review existing promises, not willingness to sign a new one. No bloc-size penalty.</summary>
public static class AllianceMaintenance
{
    public static float Target(Campaign c, DiplomaticRelation r, Breakdown why = null)
    {
        why?.Begin("Alliance cohesion", "share");
        var a = c.NationById(r.A); var b = c.NationById(r.B);
        if (a is null || b is null) return 0f;
        float total = 0f;
        void Term(string label, float value) { total += value; why?.Add(label, value); }
        Term("Existing commitment", 0.55f);
        var declaredPolicy = PoliticalPolicies.ForeignTrust(c, a, b) + PoliticalPolicies.ForeignTrust(c, b, a);
        if (declaredPolicy != 0) Term("Declared foreign policy", declaredPolicy);
        Term("Mutual opinion", M.Clamp(r.Opinion, -100f, 100f) * 0.004f);
        Term("Political differences", -PoliticsCore.IdeologicalDistance(a, b) * 0.22f);
        Term("Reputation risk", -M.Clamp(MathF.Max(a.Notoriety, b.Notoriety) / 100f, 0f, 1f) * 0.25f);
        if (r.TradeAgreement) Term("Shared trade", 0.08f);
        if (r.NonAggressionPact) Term("Mutual assurances", 0.05f);
        if (r.EmbargoByA || r.EmbargoByB) Term("Sanctions against an ally", -0.35f);
        if (c.Wars.Any(w => !w.Ended && w.Involves(a.Id) && w.Involves(b.Id) && !w.Opposes(a.Id, b.Id)))
            Term("Fighting together", 0.2f);
        foreach (var third in c.Nations)
        {
            if (third.Id == a.Id || third.Id == b.Id) continue;
            var ar = CampaignDiplomacy.Peek(c, a.Id, third.Id);
            var br = CampaignDiplomacy.Peek(c, b.Id, third.Id);
            if ((ar?.Stance == DiplomaticStance.Allied && (br?.Stance == DiplomaticStance.Rival || CampaignDiplomacy.AtWar(c, b.Id, third.Id))) ||
                (br?.Stance == DiplomaticStance.Allied && (ar?.Stance == DiplomaticStance.Rival || CampaignDiplomacy.AtWar(c, a.Id, third.Id))))
            { Term("Conflicting commitments", -0.25f); break; }
        }
        var result = M.Clamp(total, 0f, 0.98f);
        why?.Settle(result, "Bounded trust");
        return result;
    }

    public static void Tick(Campaign c)
    {
        if (!c.Defines.DynamicAlliances || !MechanicFeatures.SystemEnabled(c, nameof(AllianceMaintenance)))
        { AlliancePrograms.Pause(c); return; }
        AlliancePrograms.Tick(c);
        var interval = Math.Max(1, c.Defines.AllianceReviewDays);
        foreach (var r in c.Relations.Where(p => p.Value.Stance == DiplomaticStance.Allied)
                     .OrderBy(p => p.Key).Select(p => p.Value).ToArray())
        {
            if (r.Stance != DiplomaticStance.Allied) continue;
            if (r.AllianceAssessmentDay < 0) { r.AllianceAssessmentDay = c.ElapsedDays; continue; }
            if (c.ElapsedDays - r.AllianceAssessmentDay < interval) continue;
            r.AllianceAssessmentDay = c.ElapsedDays;
            r.AllianceCohesion = M.Clamp(r.AllianceCohesion + (Target(c, r) - r.AllianceCohesion) *
                M.Clamp(c.Defines.AllianceCohesionResponse, 0f, 1f), 0f, 1f);
            var wasStrained = r.AllianceStrainedDays > 0;
            var strained = r.AllianceCohesion < c.Defines.AllianceRuptureCohesion;
            r.AllianceStrainedDays = strained ? r.AllianceStrainedDays + interval : 0;
            if (strained && !wasStrained) c.Chronicle.Record(c, "alliance_strained", r.A, r.B);
            c.MarkStrategicChange();
            if (!strained || r.AllianceStrainedDays < Math.Max(interval, c.Defines.AllianceRuptureDays) ||
                c.ElapsedDays - r.AllianceSinceDay < c.Defines.AllianceGraceDays) continue;
            var a = c.NationById(r.A); var b = c.NationById(r.B);
            if (a is null || b is null || (a.IsHuman && b.IsHuman)) continue;
            // A human's decision is never automated. Their AI ally can still withdraw.
            var leaving = a.IsHuman ? b : a;
            var other = leaving.Id == a.Id ? b : a;
            if (AllianceSystem.Break(c, leaving, other, out _))
                c.Chronicle.Record(c, "alliance_ended", leaving.Id, other.Id);
        }
    }

    public static bool CanReaffirm(Campaign c, Nation a, Nation b) =>
        AlliancePrograms.CanStart(c, a, b, "coordination", out _);

    public static bool Reaffirm(Campaign c, Nation a, Nation b)
    {
        return AlliancePrograms.Start(c, a, b, "coordination");
    }
}

public sealed class ReaffirmAllianceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public string ProgramId { get; init; } = "coordination";
    public bool Cancel { get; init; }
    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var a = c.NationById(IssuerId); var b = c.NationById(TargetId);
        if (Cancel) AlliancePrograms.Cancel(c, a, b);
        else AlliancePrograms.Start(c, a, b, ProgramId);
    }
}
