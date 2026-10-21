using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool DiplomaticMissionsEnabled { get; set; } = true;
    public float DiplomaticMissionPoliticalCost { get; set; } = 8;
    public float DiplomaticMissionWeeklyPoliticalCost { get; set; } = 1.5f;
    public float DiplomaticMissionStartCash { get; set; } = .1f;
    public float DiplomaticMissionWeeklyCash { get; set; } = .01f;
    public int DiplomaticMissionDays { get; set; } = 180;
    public int DiplomaticMissionLimit { get; set; } = 3;
    public float DiplomaticMissionWeeklyOpinion { get; set; } = 1.2f;
    public float DiplomaticMissionOpinionCap { get; set; } = 24;
}

/// <summary>Two bounded, directional assignments per existing relation. Prices are
/// captured at signature; a mod changing a price cannot rewrite an accepted commitment.</summary>
public sealed class DiplomaticMission
{
    public int StartedDay, EndsDay, LastPaidDay, PaidWeeks;
    public float WeeklyPoliticalCost, WeeklyCash, WeeklyOpinion, OpinionCap;
}

public sealed partial class DiplomaticRelation
{
    public DiplomaticMission MissionByA, MissionByB;
}

public static class DiplomaticOutreach
{
    public static DiplomaticMission Of(DiplomaticRelation relation, int issuer) => relation is null ? null :
        issuer == relation.A ? relation.MissionByA : issuer == relation.B ? relation.MissionByB : null;

    public static string Refusal(Campaign c, int issuer, int target, bool active = true)
    {
        var n = c?.NationById(issuer); var other = c?.NationById(target);
        if (n is null || other is null || n.Id == other.Id || c.Fallen.Contains(issuer) || c.Fallen.Contains(target) ||
            n.Population <= 0 || other.Population <= 0) return Loc.Get("Choose an existing foreign country.");
        var relation = CampaignDiplomacy.Peek(c, issuer, target);
        if (!active) return Of(relation, issuer) is null ? Loc.Get("There is no diplomatic mission to recall.") : "";
        if (Of(relation, issuer) is not null) return Loc.Get("A diplomatic mission is already working here.");
        if (CampaignDiplomacy.AtWar(c, issuer, target)) return Loc.Get("End the war before sending a goodwill mission.");
        if (relation is not null && (relation.EmbargoBy(issuer) || relation.EmbargoBy(target)))
            return Loc.Get("An active embargo blocks a goodwill mission.");
        if (relation is not null && c.ElapsedDays - relation.LastActionDay < 14)
            return Loc.Get("Diplomatic action is still on cooldown.");
        if (!c.Defines.DiplomaticMissionsEnabled)
            return n.Treasury >= .1f ? "" : Loc.Get("Insufficient treasury for a diplomatic mission.");
        var d = c.Defines;
        if (!Valid(d.DiplomaticMissionPoliticalCost) || !Valid(d.DiplomaticMissionWeeklyPoliticalCost) ||
            !Valid(d.DiplomaticMissionStartCash) || !Valid(d.DiplomaticMissionWeeklyCash) ||
            !Valid(d.DiplomaticMissionWeeklyOpinion) || !Valid(d.DiplomaticMissionOpinionCap))
            return Loc.Get("The campaign data is damaged, so this cannot be done.");
        if (c.Relations.Values.Count(r => Of(r, issuer) is not null) >= Math.Clamp(d.DiplomaticMissionLimit, 1, 12))
            return Loc.Get("Recall a diplomatic mission before opening another.");
        if (!float.IsFinite(n.PoliticalPoints) || n.PoliticalPoints < d.DiplomaticMissionPoliticalCost)
            return Loc.Format("Insufficient political credit: need {0:0.##}, available {1:0.##}.", d.DiplomaticMissionPoliticalCost, n.PoliticalPoints);
        if (!float.IsFinite(n.Treasury) || n.Treasury < d.DiplomaticMissionStartCash)
            return Loc.Get("Insufficient treasury for a diplomatic mission.");
        return "";
    }

    private static bool Valid(float value) => float.IsFinite(value) && value >= 0;

    public static string Description(Campaign c, int issuer, int target)
    {
        var mission = Of(CampaignDiplomacy.Peek(c, issuer, target), issuer);
        if (mission is not null) return Loc.Format(
            "Mission in progress: {0} days left, {1} paid weeks. Weekly: {2:0.##} political credit and {3}. Recall stops future payments; earned goodwill fades normally.",
            Math.Max(0, mission.EndsDay - c.ElapsedDays), mission.PaidWeeks, mission.WeeklyPoliticalCost, Fmt.Money(mission.WeeklyCash));
        if (!c.Defines.DiplomaticMissionsEnabled) return Loc.Get("Legacy diplomacy: an immediate treasury-funded overture.");
        var d = c.Defines;
        return Loc.Format("A {0}-day mission, not an instant opinion boost. Start: {1:0.##} political credit and {2}. Weekly: {3:0.##} political credit and {4}. Goodwill ceiling: +{5:0.#}; tension and political distance slow progress. At most {6} concurrent missions.",
            Math.Clamp(d.DiplomaticMissionDays, 14, 730), d.DiplomaticMissionPoliticalCost, Fmt.Money(d.DiplomaticMissionStartCash),
            d.DiplomaticMissionWeeklyPoliticalCost, Fmt.Money(d.DiplomaticMissionWeeklyCash), d.DiplomaticMissionOpinionCap,
            Math.Clamp(d.DiplomaticMissionLimit, 1, 12));
    }

    internal static bool Set(Campaign c, int issuer, int target, bool active)
    {
        if (Refusal(c, issuer, target, active).Length != 0) return false;
        var r = CampaignDiplomacy.Get(c, issuer, target); var n = c.NationById(issuer);
        if (!active)
        {
            if (issuer == r.A) r.MissionByA = null; else r.MissionByB = null;
            c.Chronicle.Record(c, "diplomatic_mission_ended", issuer, target,isPublic:false); c.MarkStrategicChange(); return true;
        }
        var d = c.Defines;
        n.PoliticalPoints -= d.DiplomaticMissionPoliticalCost; n.Treasury -= d.DiplomaticMissionStartCash;
        var mission = new DiplomaticMission { StartedDay = c.ElapsedDays, LastPaidDay = c.ElapsedDays,
            EndsDay = c.ElapsedDays + Math.Clamp(d.DiplomaticMissionDays, 14, 730),
            WeeklyPoliticalCost = d.DiplomaticMissionWeeklyPoliticalCost, WeeklyCash = d.DiplomaticMissionWeeklyCash,
            WeeklyOpinion = d.DiplomaticMissionWeeklyOpinion, OpinionCap = Math.Clamp(d.DiplomaticMissionOpinionCap, 0, 50) };
        if (issuer == r.A) r.MissionByA = mission; else r.MissionByB = mission;
        r.LastActionDay = c.ElapsedDays;
        c.Chronicle.Record(c, "diplomatic_mission_started", issuer, target,isPublic:false); c.MarkStrategicChange(); return true;
    }

    // Called inside the existing opinion scan. Only active assignments check their
    // weekly due date; there is no second traversal of the relationship table.
    internal static void Tick(Campaign c, DiplomaticRelation r)
    {
        if (r.MissionByA is not null && !Advance(c,r,r.A,r.MissionByA)) r.MissionByA = null;
        if (r.MissionByB is not null && !Advance(c,r,r.B,r.MissionByB)) r.MissionByB = null;
    }

    private static bool Advance(Campaign c, DiplomaticRelation r, int issuer, DiplomaticMission mission)
    {
        var n = c.NationById(issuer); var other = c.NationById(r.Other(issuer));
        var ended = !c.Defines.DiplomaticMissionsEnabled || n is null || other is null || c.Fallen.Contains(issuer) ||
            n.Population<=0 || other.Population<=0 ||
            c.Fallen.Contains(other.Id) || CampaignDiplomacy.AtWar(c,r.A,r.B) || r.EmbargoByA || r.EmbargoByB ||
            c.ElapsedDays >= mission.EndsDay;
        if (!ended && c.ElapsedDays - mission.LastPaidDay < 7) return true;
        ended |= n is null || !Valid(n.PoliticalPoints) || !Valid(n.Treasury) ||
            n.PoliticalPoints < mission.WeeklyPoliticalCost || n.Treasury < mission.WeeklyCash ||
            !Valid(mission.WeeklyPoliticalCost) || !Valid(mission.WeeklyCash) || !Valid(mission.WeeklyOpinion) || !Valid(mission.OpinionCap);
        if (ended)
        { c.Chronicle.Record(c,"diplomatic_mission_ended",issuer,r.Other(issuer),isPublic:false); c.MarkStrategicChange(); return false; }
        n.PoliticalPoints -= mission.WeeklyPoliticalCost; n.Treasury -= mission.WeeklyCash;
        mission.LastPaidDay = c.ElapsedDays; mission.PaidWeeks++;
        var friction = (1 - Math.Clamp(PoliticsCore.IdeologicalDistance(n,other),0,1) * .65f) *
            (1 - Math.Clamp(r.Tension / 100f,0,1) * .6f);
        var definition = OpinionCatalog.Find("diplomatic_outreach");
        if (definition is { Opinion: > 0 } reason)
        {
            var held = r.Reasons.FirstOrDefault(x=>x.Id=="diplomatic_outreach");
            var amount = Math.Min(mission.OpinionCap, (held is null ? 0 : OpinionSystem.Weight(c,held)) + mission.WeeklyOpinion * friction);
            if (amount > 0) OpinionSystem.Remember(c,r,reason.Id,amount / reason.Opinion);
        }
        if (r.Opinion >= 35 && r.Stance is DiplomaticStance.Neutral or DiplomaticStance.Rival) r.Stance= DiplomaticStance.Friendly;
        c.MarkStrategicChange(); return true;
    }
}
