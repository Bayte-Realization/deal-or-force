using System;
using System.Collections.Generic;
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
    /// <summary>
    /// What each paid week of a mission forgives of the grievances between the two (OpinionWitnesses.Forgive):
    /// this share of the relation's own fading grievances, and the larger share of those held as a bystander -
    /// over other countries' quarrels - which patient diplomacy is best at talking down.
    /// </summary>
    public float DiplomaticMissionWeeklyForgiveness { get; set; } = 0.03f;
    public float DiplomaticMissionWeeklyThirdPartyForgiveness { get; set; } = 0.08f;
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
    /// <summary>One read-only AI candidate scan, ending at its first successful move.
    /// The number of missions is the campaign's own census, not a fresh walk of every
    /// relation for every embassy. A second campaign keeps its own census.</summary>
    internal sealed class OfferScan
    {
        private readonly Campaign _campaign;
        private readonly int _issuer;
        private int? _count;

        internal OfferScan(Campaign campaign, int issuer) { _campaign = campaign; _issuer = issuer; }

        internal int Count(Campaign campaign, int issuer) =>
            ReferenceEquals(campaign, _campaign) && issuer == _issuer
                ? (_count ??= CountMissions(campaign, issuer))
                : CountMissions(campaign, issuer);
    }

    sealed class MissionCensus
    {
        internal bool Built;
        internal readonly Dictionary<int, int> Count = new();
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, MissionCensus> MissionCensuses = new();

    static MissionCensus CensusOf(Campaign campaign) =>
        MissionCensuses.GetValue(campaign, static _ => new MissionCensus());

    /// <summary>
    /// How many live missions this country has out. The table is read once per campaign;
    /// opening, closing, sleeping and waking a mission adjusts that count. A probe that
    /// only wants the number does not insert a relation.
    /// </summary>
    internal static void WarmCensus(Campaign c) => CountMissions(c, 0);

    private static int CountMissions(Campaign c, int issuer)
    {
        if (c is null) return 0;
        var census = CensusOf(c);
        if (!census.Built)
        {
            lock (census)
            {
                if (!census.Built)
                {
                    foreach (var relation in c.Relations.Values)
                    {
                        if (relation.MissionByA is not null) AdjustCensus(census, relation.A, 1);
                        if (relation.MissionByB is not null) AdjustCensus(census, relation.B, 1);
                    }
                    census.Built = true;
                }
            }
        }
        return census.Count.GetValueOrDefault(issuer);
    }

    static void NoteIssuer(Campaign c, int issuer, int delta)
    {
        if (c is null || delta == 0 || !MissionCensuses.TryGetValue(c, out var census) || !census.Built) return;
        AdjustCensus(census, issuer, delta);
    }

    /// <summary>A relation left or rejoined the live table, missions and all.</summary>
    internal static void NoteMissionTable(Campaign c, DiplomaticRelation relation, int delta)
    {
        if (c is null || relation is null || delta == 0 ||
            !MissionCensuses.TryGetValue(c, out var census) || !census.Built) return;
        if (relation.MissionByA is not null) AdjustCensus(census, relation.A, delta);
        if (relation.MissionByB is not null) AdjustCensus(census, relation.B, delta);
    }

    static void AdjustCensus(MissionCensus census, int issuer, int delta)
    {
        var next = census.Count.GetValueOrDefault(issuer) + delta;
        if (next <= 0) census.Count.Remove(issuer);
        else census.Count[issuer] = next;
    }

    public static DiplomaticMission Of(DiplomaticRelation relation, int issuer) => relation is null ? null :
        issuer == relation.A ? relation.MissionByA : issuer == relation.B ? relation.MissionByB : null;

    public static string Refusal(Campaign c, int issuer, int target, bool active = true) =>
        Refusal(c, issuer, target, active, null);

    internal static string Refusal(Campaign c, int issuer, int target, bool active, OfferScan scan)
    {
        var n = c?.NationById(issuer); var other = c?.NationById(target);
        if (n is null || other is null || n.Id == other.Id || c.Fallen.Contains(issuer) || c.Fallen.Contains(target) ||
            n.Population <= 0 || other.Population <= 0) return Loc.Get("Choose an existing foreign country.");
        var relation = CampaignDiplomacy.Peek(c, issuer, target);
        if (!active) return Of(relation, issuer) is null ? Loc.Get("There is no diplomatic mission to recall.") : "";
        if (Of(relation, issuer) is not null) return Loc.Get("A diplomatic mission is already working here.");
        if (CampaignDiplomacy.AtWar(c, issuer, target)) return Loc.Get("End the war before sending a goodwill mission.");
        // Only our own embargo stands in the way. Theirs used to as well, which closed the one
        // patient road back to exactly the countries that had turned against us: a mission is
        // how a government talks its way out of a sanction, not something a sanction forbids.
        if (relation is not null && relation.EmbargoBy(issuer))
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
        if ((scan?.Count(c, issuer) ?? CountMissions(c, issuer)) >= Math.Clamp(d.DiplomaticMissionLimit, 1, 12))
            return Loc.Get("Recall a diplomatic mission before opening another.");
        if (!float.IsFinite(n.PoliticalPoints) || n.PoliticalPoints < d.DiplomaticMissionPoliticalCost)
            return Loc.Format("Insufficient political credit: need {0:0.##}, available {1:0.##}.", d.DiplomaticMissionPoliticalCost, n.PoliticalPoints);
        if (!float.IsFinite(n.Treasury) || n.Treasury < DiplomacyPrice.Of(c, n, d.DiplomaticMissionStartCash))
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
        var payer = c.NationById(issuer);
        return Loc.Format("A {0}-day mission, not an instant opinion boost. Start: {1:0.##} political credit and {2}. Weekly: {3:0.##} political credit and {4}. Goodwill ceiling: +{5:0.#}; tension and political distance slow progress. At most {6} concurrent missions.",
            Math.Clamp(d.DiplomaticMissionDays, 14, 730), d.DiplomaticMissionPoliticalCost, Fmt.Money(DiplomacyPrice.Of(c, payer, d.DiplomaticMissionStartCash)),
            d.DiplomaticMissionWeeklyPoliticalCost, Fmt.Money(DiplomacyPrice.Of(c, payer, d.DiplomaticMissionWeeklyCash)), d.DiplomaticMissionOpinionCap,
            Math.Clamp(d.DiplomaticMissionLimit, 1, 12)) + " " +
            Loc.Get("Every week it also wears down old grievances between us, most of all those held over other countries' quarrels.");
    }

    internal static bool Set(Campaign c, int issuer, int target, bool active)
    {
        if (Refusal(c, issuer, target, active).Length != 0) return false;
        var r = CampaignDiplomacy.Get(c, issuer, target); var n = c.NationById(issuer);
        if (!active)
        {
            var had = Of(r, issuer) is not null;
            if (issuer == r.A) r.MissionByA = null; else r.MissionByB = null;
            if (had) NoteIssuer(c, issuer, -1);
            c.Chronicle.Record(c, "diplomatic_mission_ended", issuer, target,isPublic:false); c.MarkStrategicChange(); return true;
        }
        var d = c.Defines;
        { var ledgerBefore = n.Treasury; n.PoliticalPoints -= d.DiplomaticMissionPoliticalCost; n.Treasury -= DiplomacyPrice.Of(c, n, d.DiplomaticMissionStartCash); MoneyLedger.Book(n, MoneyFlow.Diplomacy, n.Treasury - ledgerBefore); }
        var mission = new DiplomaticMission { StartedDay = c.ElapsedDays, LastPaidDay = c.ElapsedDays,
            EndsDay = c.ElapsedDays + Math.Clamp(d.DiplomaticMissionDays, 14, 730),
            WeeklyPoliticalCost = d.DiplomaticMissionWeeklyPoliticalCost, WeeklyCash = DiplomacyPrice.Of(c, n, d.DiplomaticMissionWeeklyCash),
            WeeklyOpinion = d.DiplomaticMissionWeeklyOpinion, OpinionCap = Math.Clamp(d.DiplomaticMissionOpinionCap, 0, 50) };
        if (issuer == r.A) r.MissionByA = mission; else r.MissionByB = mission;
        NoteIssuer(c, issuer, 1);
        r.LastActionDay = c.ElapsedDays;
        c.Chronicle.Record(c, "diplomatic_mission_started", issuer, target,isPublic:false); c.MarkStrategicChange(); return true;
    }

    // Called inside the existing opinion scan. Only active assignments check their
    // weekly due date; there is no second traversal of the relationship table.
    internal static void Tick(Campaign c, DiplomaticRelation r)
    {
        if (r.MissionByA is not null && !Advance(c,r,r.A,r.MissionByA)) { r.MissionByA = null; NoteIssuer(c, r.A, -1); }
        if (r.MissionByB is not null && !Advance(c,r,r.B,r.MissionByB)) { r.MissionByB = null; NoteIssuer(c, r.B, -1); }
    }

    private static bool Advance(Campaign c, DiplomaticRelation r, int issuer, DiplomaticMission mission)
    {
        var n = c.NationById(issuer); var other = c.NationById(r.Other(issuer));
        var ended = !c.Defines.DiplomaticMissionsEnabled || n is null || other is null || c.Fallen.Contains(issuer) ||
            n.Population<=0 || other.Population<=0 ||
            c.Fallen.Contains(other.Id) || CampaignDiplomacy.AtWar(c,r.A,r.B) || r.EmbargoBy(issuer) ||
            c.ElapsedDays >= mission.EndsDay;
        if (!ended && c.ElapsedDays - mission.LastPaidDay < 7) return true;
        ended |= n is null || !Valid(n.PoliticalPoints) || !Valid(n.Treasury) ||
            n.PoliticalPoints < mission.WeeklyPoliticalCost || n.Treasury < mission.WeeklyCash ||
            !Valid(mission.WeeklyPoliticalCost) || !Valid(mission.WeeklyCash) || !Valid(mission.WeeklyOpinion) || !Valid(mission.OpinionCap);
        if (ended)
        { c.Chronicle.Record(c,"diplomatic_mission_ended",issuer,r.Other(issuer),isPublic:false); c.MarkStrategicChange(); return false; }
        { var ledgerBefore = n.Treasury; n.PoliticalPoints -= mission.WeeklyPoliticalCost; n.Treasury -= mission.WeeklyCash; MoneyLedger.Book(n, MoneyFlow.Diplomacy, n.Treasury - ledgerBefore); }
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
        // And the old grievances wear down while the diplomats work: a little of the relation's
        // own every week, more of what the two hold over other countries' quarrels. A mission of
        // half a year leaves about a tenth of a bystander's grudge and half of a real one.
        OpinionWitnesses.Forgive(c, r, c.Defines.DiplomaticMissionWeeklyForgiveness * friction,
            c.Defines.DiplomaticMissionWeeklyThirdPartyForgiveness * friction);
        if (r.Opinion >= 35 && r.Stance is DiplomaticStance.Neutral or DiplomaticStance.Rival) r.Stance= DiplomaticStance.Friendly;
        c.MarkStrategicChange(); return true;
    }
}
