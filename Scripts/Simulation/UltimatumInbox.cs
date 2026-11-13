using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public enum UltimatumPaperState { Pending, Yielded, Refused, Expired, Invalidated }

/// <summary>A demand waiting on a player's desk. Delivery does not move land or cash.</summary>
public sealed class UltimatumPaper
{
    public int Id, FromId, ToId, CreatedDay, ExpiresDay, ClosedDay = -1;
    public int[] Regions = Array.Empty<int>();
    public float Reparations;
    public bool LiftEmbargo, NonAggression;
    public bool Annex, Vassalise, Disarm, BreakAlliances, ReleaseSubjects, TechnologyTransfer, ChangeGovernment, FleetTransfer;
    public string SubjectKind = "";
    public bool TradeAgreement, EmbargoThird, BreakNamed, CancelGuarantee, JoinAlliance, AcceptGuarantee, MilitaryAccess;
    public bool WhitePeace, CedeOccupied, Humiliate, ReleaseNamed, Liberate;
    public int EmbargoOnId, BreakWithId, CancelGuaranteeId, ReleaseSubjectId, LiberateRegion;
    public UltimatumPaperState State;
    public string Reason = "";

    [JsonIgnore]
    public Ultimatum.Demand Terms => new()
    {
        Regions = Regions ?? Array.Empty<int>(),
        Reparations = Reparations,
        LiftEmbargo = LiftEmbargo,
        NonAggression = NonAggression,
        Annex = Annex, Vassalise = Vassalise, Disarm = Disarm, BreakAlliances = BreakAlliances,
        ReleaseSubjects = ReleaseSubjects, TechnologyTransfer = TechnologyTransfer,
        ChangeGovernment = ChangeGovernment, FleetTransfer = FleetTransfer,
        SubjectKind = SubjectKind ?? "",
        TradeAgreement = TradeAgreement, EmbargoThird = EmbargoThird, EmbargoOnId = EmbargoOnId,
        BreakNamed = BreakNamed, BreakWithId = BreakWithId, CancelGuarantee = CancelGuarantee,
        CancelGuaranteeId = CancelGuaranteeId, JoinAlliance = JoinAlliance, AcceptGuarantee = AcceptGuarantee,
        MilitaryAccess = MilitaryAccess, WhitePeace = WhitePeace, CedeOccupied = CedeOccupied,
        Humiliate = Humiliate, ReleaseNamed = ReleaseNamed, ReleaseSubjectId = ReleaseSubjectId,
        Liberate = Liberate, LiberateRegion = LiberateRegion
    };

    [JsonIgnore] public bool Pending => State == UltimatumPaperState.Pending;
}

public sealed partial class Campaign
{
    public readonly List<UltimatumPaper> UltimatumPapers = new();
    public int NextUltimatumId = 1;
}

public static partial class Ultimatum
{
    public static int Incoming(Campaign c, int nationId) => c?.UltimatumPapers.Count(paper =>
        paper.Pending && paper.ToId == nationId && paper.ExpiresDay > c.ElapsedDays) ?? 0;

    public static bool PendingBetween(Campaign c, int fromId, int toId) => c?.UltimatumPapers.Any(paper =>
        paper.Pending && paper.FromId == fromId && paper.ToId == toId && paper.ExpiresDay > c.ElapsedDays) == true;

    public static UltimatumPaper Copy(UltimatumPaper paper) => new()
    {
        Id = paper.Id, FromId = paper.FromId, ToId = paper.ToId,
        CreatedDay = paper.CreatedDay, ExpiresDay = paper.ExpiresDay, ClosedDay = paper.ClosedDay,
        Regions = (paper.Regions ?? Array.Empty<int>()).ToArray(),
        Reparations = paper.Reparations, LiftEmbargo = paper.LiftEmbargo, NonAggression = paper.NonAggression,
        Annex = paper.Annex, Vassalise = paper.Vassalise, Disarm = paper.Disarm, BreakAlliances = paper.BreakAlliances,
        ReleaseSubjects = paper.ReleaseSubjects, TechnologyTransfer = paper.TechnologyTransfer,
        ChangeGovernment = paper.ChangeGovernment, FleetTransfer = paper.FleetTransfer,
        SubjectKind = paper.SubjectKind ?? "",
        TradeAgreement = paper.TradeAgreement, EmbargoThird = paper.EmbargoThird, EmbargoOnId = paper.EmbargoOnId,
        BreakNamed = paper.BreakNamed, BreakWithId = paper.BreakWithId, CancelGuarantee = paper.CancelGuarantee,
        CancelGuaranteeId = paper.CancelGuaranteeId, JoinAlliance = paper.JoinAlliance,
        AcceptGuarantee = paper.AcceptGuarantee, MilitaryAccess = paper.MilitaryAccess,
        WhitePeace = paper.WhitePeace, CedeOccupied = paper.CedeOccupied, Humiliate = paper.Humiliate,
        ReleaseNamed = paper.ReleaseNamed, ReleaseSubjectId = paper.ReleaseSubjectId,
        Liberate = paper.Liberate, LiberateRegion = paper.LiberateRegion,
        State = paper.State, Reason = paper.Reason ?? ""
    };

    /// <summary>
    /// Put the demand on a player's desk. A government is not delivered a paper: the caller
    /// already has <see cref="Assess"/> and <see cref="Resolve"/> for that answer.
    /// </summary>
    public static Outcome Deliver(Campaign c, Nation from, Nation to, Demand demand)
    {
        if (!ValidateTerms(c, from, to, demand, out var reason)) return new(false, reason);
        if (to.IsHuman != true) return new(false, Loc.Get("They answer for themselves."));
        if (PendingBetween(c, from.Id, to.Id)) return new(false, Loc.Get("An ultimatum is already on their desk."));
        if (c.NextUltimatumId <= 0 || c.NextUltimatumId == int.MaxValue)
            return new(false, Loc.Get("No more ultimatums can be sent in this campaign."));
        var days = Math.Clamp(c.Defines.UltimatumAnswerDays, 1, 365);
        var paper = new UltimatumPaper
        {
            Id = c.NextUltimatumId++, FromId = from.Id, ToId = to.Id,
            CreatedDay = c.ElapsedDays,
            ExpiresDay = (int)Math.Min(int.MaxValue, (long)c.ElapsedDays + days),
            Regions = RegionIds(demand),
            Reparations = demand.Reparations, LiftEmbargo = demand.LiftEmbargo, NonAggression = demand.NonAggression,
            Annex = demand.Annex, Vassalise = demand.Vassalise, Disarm = demand.Disarm, BreakAlliances = demand.BreakAlliances,
            ReleaseSubjects = demand.ReleaseSubjects, TechnologyTransfer = demand.TechnologyTransfer,
            ChangeGovernment = demand.ChangeGovernment, FleetTransfer = demand.FleetTransfer,
            SubjectKind = demand.SubjectKind ?? "",
            TradeAgreement = demand.TradeAgreement, EmbargoThird = demand.EmbargoThird, EmbargoOnId = demand.EmbargoOnId,
            BreakNamed = demand.BreakNamed, BreakWithId = demand.BreakWithId, CancelGuarantee = demand.CancelGuarantee,
            CancelGuaranteeId = demand.CancelGuaranteeId, JoinAlliance = demand.JoinAlliance,
            AcceptGuarantee = demand.AcceptGuarantee, MilitaryAccess = demand.MilitaryAccess,
            WhitePeace = demand.WhitePeace, CedeOccupied = demand.CedeOccupied, Humiliate = demand.Humiliate,
            ReleaseNamed = demand.ReleaseNamed, ReleaseSubjectId = demand.ReleaseSubjectId,
            Liberate = demand.Liberate, LiberateRegion = demand.LiberateRegion,
            State = UltimatumPaperState.Pending
        };
        c.UltimatumPapers.Add(paper);
        c.DealInboxRevision++;
        CampaignDiplomacy.Get(c, from.Id, to.Id).LastActionDay = c.ElapsedDays;
        c.MarkStrategicChange();
        Trim(c);
        return new(true, Loc.Get("The ultimatum is on their desk."));
    }

    public static Outcome AnswerPaper(Campaign c, int issuerId, int paperId, Answer answer)
    {
        var paper = c?.UltimatumPapers.FirstOrDefault(candidate => candidate.Id == paperId);
        if (paper is null || !paper.Pending || !Enum.IsDefined(answer))
            return new(false, Loc.Get("This ultimatum is no longer awaiting a reply."));
        if (issuerId != paper.ToId)
            return new(false, Loc.Get("Only the addressed country may answer."));
        if (paper.ExpiresDay <= c.ElapsedDays)
        {
            CloseUnread(c, paper);
            return new(false, Loc.Get(paper.Reason));
        }
        var from = c.NationById(paper.FromId);
        var to = c.NationById(paper.ToId);
        var refusal = "";
        if (from is null || to is null || !ValidateTerms(c, from, to, paper.Terms, out refusal))
        {
            Close(c, paper, UltimatumPaperState.Invalidated, refusal.Length > 0 ? refusal : "The demand can no longer be met.");
            return new(false, Loc.Get(paper.Reason));
        }
        var result = ResolveAnswered(c, from, to, paper.Terms, answer);
        Close(c, paper, result.Accepted
            ? answer == Answer.Yield ? UltimatumPaperState.Yielded : UltimatumPaperState.Refused
            : UltimatumPaperState.Invalidated,
            result.Accepted ? result.Note : result.Note);
        return result.Accepted ? result : new(false, result.Note);
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same inbox sweep, yielded between papers.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null) yield break;
        var ticked = 0;
        foreach (var paper in c.UltimatumPapers.Where(candidate => candidate.Pending).ToArray())
        {
            if (++ticked % 32 == 0) yield return 0;
            if (paper.ExpiresDay <= c.ElapsedDays) CloseUnread(c, paper);
        }
        yield return 0;
        Trim(c);
    }

    public static void Restore(Campaign c, List<UltimatumPaper> papers, int nextId)
    {
        c.UltimatumPapers.Clear();
        var seen = new HashSet<int>();
        var highest = 0;
        foreach (var paper in papers ?? new())
        {
            if (paper is null || paper.Id <= 0 || !seen.Add(paper.Id)) continue;
            if (paper.Regions is null) paper.Regions = Array.Empty<int>();
            c.UltimatumPapers.Add(Copy(paper));
            if (paper.Id > highest) highest = paper.Id;
        }
        c.NextUltimatumId = Math.Max(Math.Max(1, nextId), highest + 1);
    }

    /// <summary>The days ran out. The land stays, and the refusal is the reason they asked for.</summary>
    private static void CloseUnread(Campaign c, UltimatumPaper paper)
    {
        var from = c.NationById(paper.FromId);
        var to = c.NationById(paper.ToId);
        var refusal = "";
        if (from is null || to is null || !ValidateTerms(c, from, to, paper.Terms, out refusal))
        {
            Close(c, paper, UltimatumPaperState.Invalidated, refusal.Length > 0 ? refusal : "The demand can no longer be met.");
            return;
        }
        var result = ResolveAnswered(c, from, to, paper.Terms, Answer.Refuse);
        Close(c, paper, result.Accepted ? UltimatumPaperState.Refused : UltimatumPaperState.Invalidated,
            result.Accepted ? "The days ran out. The demand was refused. The land stays." : result.Note);
    }

    private static void Close(Campaign c, UltimatumPaper paper, UltimatumPaperState state, string reason)
    {
        paper.State = state;
        paper.ClosedDay = c.ElapsedDays;
        paper.Reason = reason ?? "";
        c.DealInboxRevision++;
        c.MarkStrategicChange();
    }

    private static void Trim(Campaign c)
    {
        var keep = Math.Clamp(c.Defines.UltimatumHistoryLimit, 0, 256);
        var closed = c.UltimatumPapers.Where(paper => !paper.Pending).OrderByDescending(paper => paper.Id).Skip(keep).ToHashSet();
        if (closed.Count > 0) c.UltimatumPapers.RemoveAll(closed.Contains);
    }
}

/// <summary>The addressed country's own hand. Yielding is the transfer. Refusing leaves the land.</summary>
public sealed class AnswerUltimatumCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int PaperId { get; init; }
    public Ultimatum.Answer Reply { get; init; }
    public Ultimatum.Outcome Outcome { get; private set; } = new(false, "No ultimatum was answered.");

    public void Apply(Campaign campaign)
    {
        Outcome = new(false, Loc.Get("No ultimatum was answered."));
        if (Reply == Ultimatum.Answer.Yield && !MechanicFeatures.Allows(campaign, this))
        {
            Outcome = new(false, Loc.Get("This action is disabled in this campaign."));
            return;
        }
        Outcome = Ultimatum.AnswerPaper(campaign, IssuerId, PaperId, Reply);
    }
}
