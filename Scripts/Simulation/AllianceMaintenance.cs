using System;
using System.Collections.Generic;

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
    private static readonly List<(long Key, DiplomaticRelation Relation)> DueAlliances = new();

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
        var ideologicalGap = PoliticsCore.IdeologicalDistance(a, b);
        Term("Political differences", -ideologicalGap * 0.26f - (ideologicalGap >= 0.55f ? 0.12f : 0f));
        Term("Reputation risk", -M.Clamp(MathF.Max(a.Notoriety, b.Notoriety) / 100f, 0f, 1f) * 0.25f);
        var overstretch = MathF.Max(a.ImperialOverstretch, b.ImperialOverstretch);
        if (overstretch > 0.15f) Term("Imperial overstretch", -overstretch * 0.18f);
        if (OpinionSystem.Has(r, "refused_a_call")) Term("Refused call to arms", -0.22f);
        if (r.TradeAgreement) Term("Shared trade", 0.08f);
        if (r.NonAggressionPact) Term("Mutual assurances", 0.05f);
        if (r.EmbargoByA || r.EmbargoByB) Term("Sanctions against an ally", -0.35f);
        var fightingTogether = false;
        foreach (var w in c.Wars)
            if (!w.Ended && w.Involves(a.Id) && w.Involves(b.Id) && !w.Opposes(a.Id, b.Id))
            {
                fightingTogether = true;
                break;
            }
        if (fightingTogether) Term("Fighting together", 0.2f);
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
        DueAlliances.Clear();
        foreach (var pair in c.Relations)
        {
            var rel = pair.Value;
            if (rel.Stance != DiplomaticStance.Allied) continue;
            if (rel.AllianceAssessmentDay >= 0 && c.ElapsedDays - rel.AllianceAssessmentDay < interval) continue;
            DueAlliances.Add((pair.Key, rel));
        }
        if (DueAlliances.Count == 0) return;
        if (DueAlliances.Count > 1) DueAlliances.Sort((x, y) => x.Key.CompareTo(y.Key));

        for (var i = 0; i < DueAlliances.Count; i++)
        {
            var r = DueAlliances[i].Relation;
            if (r.Stance != DiplomaticStance.Allied) continue;
            if (r.AllianceAssessmentDay < 0) { r.AllianceAssessmentDay = c.ElapsedDays; continue; }
            r.AllianceAssessmentDay = c.ElapsedDays;
            r.AllianceCohesion = M.Clamp(r.AllianceCohesion + (Target(c, r) - r.AllianceCohesion) *
                M.Clamp(c.Defines.AllianceCohesionResponse, 0f, 1f), 0f, 1f);
            var wasStrained = r.AllianceStrainedDays > 0;
            var strained = r.AllianceCohesion < c.Defines.AllianceRuptureCohesion;
            r.AllianceStrainedDays = strained ? r.AllianceStrainedDays + interval : 0;
            if (strained && !wasStrained) c.Chronicle.Record(c, "alliance_strained", r.A, r.B);
            if (strained != wasStrained) c.MarkStrategicChange();
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
