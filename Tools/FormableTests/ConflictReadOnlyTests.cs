using System;
using System.Linq;
using DealOrForce.Simulation;

static class ConflictReadOnlyTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS conflict preview: " + label); }
    public static void Run()
    {
        foreach (var seed in new[] { 20260830, 20261015 })
        {
            var c = Campaign.CreateBootstrap(seed);
            var a = c.NationByTag("POL"); var b = c.NationByTag("RUS");
            foreach (var n in c.Nations) { n.AssessedDay = -1; n.AssessedStrength = 0; }
            var before = CampaignFingerprint.Of(c);
            var preview = CampaignIntel.EstimatedStrength(c, a.Id, b, refreshAssessment: false);
            Check(preview >= 0 && float.IsFinite(preview), "unassessed estimate is finite: " + seed);
            Check(CampaignFingerprint.Of(c) == before && b.AssessedDay == -1, "unassessed preview leaves saved intelligence untouched: " + seed);
            var hypothetical = Escalation.Deterred(c, a, b, refreshAssessment: false);
            Check(CampaignFingerprint.Of(c) == before && c.Nations.All(n => n.AssessedDay == -1), "deterrence preview leaves all allies and patrons untouched: " + seed);
            var actual = CampaignIntel.EstimatedStrength(c, a.Id, b);
            Check(preview == actual && b.AssessedDay == c.ElapsedDays, "default planner still commits the identical assessment: " + seed);
            Check(hypothetical == Escalation.Deterred(c, a, b), "preview and planner agree on deterrence: " + seed);
            b.AssessedDay = -1000; b.AssessedStrength = 1;
            before = CampaignFingerprint.Of(c);
            preview = CampaignIntel.EstimatedStrength(c, a.Id, b, refreshAssessment: false);
            Check(CampaignFingerprint.Of(c) == before && b.AssessedDay == -1000 && b.AssessedStrength == 1,
                "expired preview is read-only: " + seed);
            Check(preview == CampaignIntel.EstimatedStrength(c, a.Id, b), "expired preview matches the next committed estimate: " + seed);
        }
        var treaty = Campaign.CreateBootstrap(20261016);
        treaty.Organizations.Clear();
        foreach (var r in treaty.Relations.Values)
        { r.Stance = DiplomaticStance.Neutral; r.GuaranteeByA = r.GuaranteeByB = false; }
        var viewer = treaty.NationByTag("POL"); var target = treaty.NationByTag("RUS"); var patron = treaty.NationByTag("USA");
        var withoutAlly = AiStatecraft.Deterrence(treaty, viewer, target, refreshAssessment: false);
        CampaignDiplomacy.Get(treaty, target.Id, patron.Id).Stance = DiplomaticStance.Allied;
        var withAlly = AiStatecraft.Deterrence(treaty, viewer, target);
        Check(withAlly > withoutAlly, "a read-only preview does not prime a stale planner cache before a same-day alliance");
    }
}
