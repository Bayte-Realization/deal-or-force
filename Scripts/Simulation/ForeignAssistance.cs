using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public enum ForeignAssistanceState { Pending, Granted, Declined, Withdrawn, Expired, Invalidated }
public enum ForeignAssistanceReply { Accept, Decline, Withdraw }
public sealed partial class Defines
{
    public int AidRequestCooldownDays { get; set; } = 45;
    public int AidRequestOfferDays { get; set; } = 21;
    public int AiAidRequestReviewDays { get; set; } = 14;
    public float AiAidRequestWillingness { get; set; } = .45f;
}

public sealed class RequestForeignAssistanceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int DonorId { get; init; }
    public int WarId { get; init; } = -1;
    public float Amount { get; init; }
    [JsonIgnore] public ForeignAssistanceOutcome Outcome { get; private set; }
    public void Apply(Campaign campaign)
    {
        Outcome = MechanicFeatures.Allows(campaign, this) ? ForeignAssistance.Submit(campaign, IssuerId, DonorId, Amount, WarId) :
            new(0, ForeignAssistanceState.Invalidated, 0, "Foreign assistance is disabled.");
    }
}

public sealed class RespondForeignAssistanceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int RequestId { get; init; }
    public ForeignAssistanceReply Reply { get; init; }
    public float ApprovedAmount { get; init; } = float.MaxValue;
    [JsonIgnore] public ForeignAssistanceOutcome Outcome { get; private set; }
    public void Apply(Campaign campaign)
    {
        Outcome = MechanicFeatures.Allows(campaign, this) ? ForeignAssistance.Respond(campaign, IssuerId, RequestId, Reply, ApprovedAmount) :
            new(0, ForeignAssistanceState.Invalidated, 0, "Foreign assistance is disabled.");
    }
}
public sealed class ForeignAssistanceCooldown
{
    public int RequesterId, DonorId, ReadyOnDay;
}
public sealed class ForeignAssistanceRequest
{
    public int Id, RequesterId, DonorId, WarId = -1, CreatedDay, ExpiresDay, ClosedDay = -1;
    public float Amount, GrantedAmount;
    public bool HumanConsent;
    public ForeignAssistanceState State;
    public string Reason = "";
}
public readonly record struct ForeignAssistanceOutcome(int RequestId, ForeignAssistanceState State, float GrantedAmount, string Reason);
public readonly record struct ForeignAssistanceAssessment(bool Allowed, float PayableAmount, float Willingness, bool AiWouldGrant, string Reason);
public sealed partial class Campaign
{
    public readonly List<ForeignAssistanceRequest> AssistanceRequests = new();
    public readonly List<ForeignAssistanceCooldown> AssistanceCooldowns = new();
    public int NextAssistanceRequestId = 1;
    public int AssistanceLastTickDay = -1;
}
public static partial class ForeignAssistance
{
    public static ForeignAssistanceRequest Copy(ForeignAssistanceRequest r) => new()
    {
        Id = r.Id, RequesterId = r.RequesterId, DonorId = r.DonorId, WarId = r.WarId,
        CreatedDay = r.CreatedDay, ExpiresDay = r.ExpiresDay, ClosedDay = r.ClosedDay,
        Amount = r.Amount, GrantedAmount = r.GrantedAmount, HumanConsent = r.HumanConsent, State = r.State, Reason = r.Reason ?? ""
    };
    public static ForeignAssistanceCooldown Copy(ForeignAssistanceCooldown c) => new()
    { RequesterId = c.RequesterId, DonorId = c.DonorId, ReadyOnDay = c.ReadyOnDay };
    public static void Restore(Campaign c, IEnumerable<ForeignAssistanceRequest> requests, IEnumerable<ForeignAssistanceCooldown> cooldowns,
        int nextId, int lastTickDay)
    {
        if (c is null) return;
        // Even a damaged payload is bounded before sorting or copying; valid saves carry <=48 papers.
        var papers = (requests ?? Array.Empty<ForeignAssistanceRequest>()).Take(4096).Where(r => r is not null).ToArray();
        var clocks = (cooldowns ?? Array.Empty<ForeignAssistanceCooldown>()).Take(MaxCooldowns * 4).Where(x => x is not null).ToArray();
        c.AssistanceRequests.Clear(); c.AssistanceCooldowns.Clear();
        var ids = new HashSet<int>(); var pendingPairs = new HashSet<(int, int)>(); var pending = 0;
        foreach (var source in papers.OrderBy(r => r.Id))
        {
            if (source.Id <= 0 || source.Id == int.MaxValue || !Enum.IsDefined(source.State) ||
                c.NationById(source.RequesterId) is null || c.NationById(source.DonorId) is null || source.RequesterId == source.DonorId ||
                !float.IsFinite(source.Amount) || source.Amount <= 0 || !float.IsFinite(source.GrantedAmount) ||
                source.GrantedAmount < 0 || source.GrantedAmount > source.Amount || source.WarId < -1 ||
                source.CreatedDay < 0 || source.CreatedDay > c.ElapsedDays || source.ExpiresDay <= source.CreatedDay ||
                (long)source.ExpiresDay - source.CreatedDay > 365 ||
                (source.State == ForeignAssistanceState.Pending ? source.ClosedDay != -1 || source.GrantedAmount != 0 :
                    source.ClosedDay < source.CreatedDay || source.ClosedDay > c.ElapsedDays ||
                    (source.State == ForeignAssistanceState.Granted ? source.GrantedAmount <= 0 : source.GrantedAmount != 0)) ||
                !ids.Add(source.Id)) continue;
            var r = Copy(source); if (r.Reason.Length > 512) r.Reason = r.Reason[..512];
            if (r.State == ForeignAssistanceState.Pending)
            {
                if (r.ExpiresDay <= c.ElapsedDays)
                { r.State = ForeignAssistanceState.Expired; r.ClosedDay = c.ElapsedDays; r.Reason = "The assistance request has expired. No payment was made."; }
                else if (!r.HumanConsent || c.NationById(r.DonorId).IsHuman != true || !Enabled(c))
                { r.State = ForeignAssistanceState.Invalidated; r.ClosedDay = c.ElapsedDays; r.Reason = "The restored request is no longer available. No payment was made."; }
                else if (pending >= MaxActiveRequests || !pendingPairs.Add((Math.Min(r.RequesterId, r.DonorId), Math.Max(r.RequesterId, r.DonorId)))) continue;
                else pending++;
            }
            c.AssistanceRequests.Add(r);
        }
        TrimHistory(c);
        // Reserve pending pairs first, so malformed unrelated clocks cannot steal their anti-spam slots.
        var table = new Dictionary<(int, int), ForeignAssistanceCooldown>();
        foreach (var r in c.AssistanceRequests.Where(r => r.State == ForeignAssistanceState.Pending))
        {
            // A later tuning change must not rewrite a request's already recorded clock.
            // Even an elapsed clock still owns a slot while its paper awaits consent.
            var recorded = clocks.Where(x => x.RequesterId == r.RequesterId && x.DonorId == r.DonorId &&
                x.ReadyOnDay >= 0 && (long)x.ReadyOnDay - c.ElapsedDays <= 365).Select(x => x.ReadyOnDay).DefaultIfEmpty(-1).Max();
            table[(r.RequesterId, r.DonorId)] = new ForeignAssistanceCooldown { RequesterId = r.RequesterId, DonorId = r.DonorId,
                ReadyOnDay = recorded >= 0 ? recorded : (int)Math.Min(int.MaxValue,
                    (long)r.CreatedDay + Math.Clamp(c.Defines.AidRequestCooldownDays, 1, 365)) };
        }
        foreach (var x in clocks.OrderBy(x => x.RequesterId).ThenBy(x => x.DonorId))
        {
            if (x.RequesterId == x.DonorId || c.NationById(x.RequesterId) is null || c.NationById(x.DonorId) is null ||
                x.ReadyOnDay < 0 || (long)x.ReadyOnDay - c.ElapsedDays > 365 ||
                x.ReadyOnDay <= c.ElapsedDays && !table.ContainsKey((x.RequesterId, x.DonorId))) continue;
            if (table.TryGetValue((x.RequesterId, x.DonorId), out var prior)) prior.ReadyOnDay = Math.Max(prior.ReadyOnDay, x.ReadyOnDay);
            else if (table.Count < MaxCooldowns) table[(x.RequesterId, x.DonorId)] = Copy(x);
        }
        c.AssistanceCooldowns.AddRange(table.Values.OrderBy(x => x.RequesterId).ThenBy(x => x.DonorId));
        var minimumNext = c.AssistanceRequests.Count == 0 ? 1 : (int)Math.Min(int.MaxValue, (long)c.AssistanceRequests.Max(r => r.Id) + 1);
        c.NextAssistanceRequestId = Math.Max(minimumNext, Math.Max(1, nextId));
        c.AssistanceLastTickDay = lastTickDay >= -1 && lastTickDay <= c.ElapsedDays ? lastTickDay : -1;
    }
    public static void Tick(Campaign c)
    {
        if (c is null) return;
        foreach (var request in c.AssistanceRequests.Where(r => r.State == ForeignAssistanceState.Pending).OrderBy(r => r.Id).ToArray())
        {
            if (request.ExpiresDay <= c.ElapsedDays)
                Close(c, request, ForeignAssistanceState.Expired, "The assistance request has expired. No payment was made.");
            else if (!Enabled(c))
                Close(c, request, ForeignAssistanceState.Invalidated, "Foreign assistance is disabled.");
            else if (!request.HumanConsent || c.NationById(request.DonorId)?.IsHuman != true)
                Close(c, request, ForeignAssistanceState.Invalidated, "The addressed donor is no longer controlled by a player.");
            else
            {
                var assessment = Assess(c, request.RequesterId, request.DonorId, request.Amount, request.WarId);
                if (!assessment.Allowed) Close(c, request, ForeignAssistanceState.Invalidated, assessment.Reason);
            }
        }
        c.AssistanceCooldowns.RemoveAll(x => !CooldownReserved(c, x));
        TrimHistory(c);
        if (c.AssistanceLastTickDay == c.ElapsedDays) return;
        c.AssistanceLastTickDay = c.ElapsedDays;
        if (!Enabled(c)) return;
        var cadence = Math.Clamp(c.Defines.AiAidRequestReviewDays, 1, 365);
        var threshold = float.IsFinite(c.Defines.AiAidRequestWillingness) ? Math.Clamp(c.Defines.AiAidRequestWillingness, 0, 3) : .45f;
        foreach (var requester in c.Nations.Where(n => !n.IsHuman).OrderBy(n => n.Id))
        {
            if (((long)c.ElapsedDays + requester.Id) % cadence != 0 ||
                c.AssistanceRequests.Any(r => r.RequesterId == requester.Id && r.State == ForeignAssistanceState.Pending)) continue;
            var amount = (double)requester.Gdp * c.Defines.AidShareOfRecipientGdp;
            if (!double.IsFinite(amount) || amount < .01 || amount > float.MaxValue) continue;
            var warId = c.Wars.Where(w => !w.Ended && w.Involves(requester.Id)).OrderBy(w => w.Id).FirstOrDefault()?.Id ?? -1;
            Nation best = null; var bestScore = float.NegativeInfinity;
            foreach (var donor in c.Nations.OrderBy(n => n.Id))
            {
                var assessment = CanSubmit(c, requester.Id, donor.Id, (float)amount, warId);
                if (!assessment.Allowed || assessment.Willingness < threshold || assessment.Willingness <= bestScore) continue;
                best = donor; bestScore = assessment.Willingness;
            }
            if (best is not null) Submit(c, requester.Id, best.Id, (float)amount, warId);
        }
    }
    public const int MaxActiveRequests = 16, MaxHistory = 32, MaxCooldowns = 1024;
    public static int Incoming(Campaign c, int nationId) => c?.AssistanceRequests.Count(r =>
        r.State == ForeignAssistanceState.Pending && r.DonorId == nationId && r.ExpiresDay > c.ElapsedDays) ?? 0;

    /// <summary>Submission gates are separate from the live payment preview: a request's own
    /// cooldown must never prevent its addressed donor from answering it.</summary>
    public static ForeignAssistanceAssessment CanSubmit(Campaign c, int requesterId, int donorId, float amount, int warId = -1)
    {
        var assessment = Assess(c, requesterId, donorId, amount, warId);
        if (!assessment.Allowed) return assessment;
        string refusal = null;
        if (c.AssistanceRequests.Any(r => r.State == ForeignAssistanceState.Pending && r.ExpiresDay > c.ElapsedDays &&
            ((r.RequesterId == requesterId && r.DonorId == donorId) || (r.RequesterId == donorId && r.DonorId == requesterId))))
            refusal = "A request between these countries already awaits a reply.";
        else if (c.AssistanceCooldowns.Any(x => x.RequesterId == requesterId && x.DonorId == donorId && x.ReadyOnDay > c.ElapsedDays))
            refusal = "This donor was asked recently. Wait before asking again.";
        else if (c.AssistanceRequests.Count(r => r.State == ForeignAssistanceState.Pending) >= MaxActiveRequests ||
            c.NationById(donorId).IsHuman && Incoming(c, donorId) + DealNegotiations.Incoming(c, donorId) >= Math.Clamp(c.Defines.DealInboxLimit, 1, 16))
            refusal = "The diplomatic request inbox is full. Wait for a reply.";
        else if (c.AssistanceCooldowns.Count(x => CooldownReserved(c, x)) >= MaxCooldowns)
            refusal = "The assistance request ledger is full. Wait for earlier requests to clear.";
        else if (c.NextAssistanceRequestId <= 0 || c.NextAssistanceRequestId == int.MaxValue)
            refusal = "No further assistance request numbers are available.";
        return refusal is null ? assessment : assessment with { Allowed = false, Reason = refusal };
    }
    private static void RememberRequest(Campaign c, int requesterId, int donorId)
    {
        var clock = c.AssistanceCooldowns.FirstOrDefault(x => x.RequesterId == requesterId && x.DonorId == donorId);
        if (clock is null)
        {
            if (c.AssistanceCooldowns.Count >= MaxCooldowns) return;
            clock = new ForeignAssistanceCooldown { RequesterId = requesterId, DonorId = donorId };
            c.AssistanceCooldowns.Add(clock);
        }
        clock.ReadyOnDay = (int)Math.Min(int.MaxValue, (long)c.ElapsedDays + Math.Clamp(c.Defines.AidRequestCooldownDays, 1, 365));
    }
    private static bool CooldownReserved(Campaign c, ForeignAssistanceCooldown clock) => clock.ReadyOnDay > c.ElapsedDays ||
        c.AssistanceRequests.Any(r => r.State == ForeignAssistanceState.Pending && r.RequesterId == clock.RequesterId && r.DonorId == clock.DonorId);
    private static ForeignAssistanceOutcome Close(Campaign c, ForeignAssistanceRequest request, ForeignAssistanceState state, string reason)
    {
        request.State = state; request.ClosedDay = c.ElapsedDays; request.Reason = reason;
        RememberRequest(c, request.RequesterId, request.DonorId);
        TrimHistory(c);
        c.MarkStrategicChange();
        return new(request.Id, state, request.GrantedAmount, reason);
    }
    private static void TrimHistory(Campaign c)
    {
        var old = c.AssistanceRequests.Where(r => r.State != ForeignAssistanceState.Pending)
            .OrderByDescending(r => r.Id).Skip(MaxHistory).Select(r => r.Id).ToHashSet();
        c.AssistanceRequests.RemoveAll(r => old.Contains(r.Id));
    }
    public static bool Enabled(Campaign c) => c is not null && c.FeatureEnabled("foreign_assistance") && MechanicFeatures.SystemEnabled(c, nameof(ForeignAssistance)) &&
        (!(c.Defines.Features.ContainsKey("diplomacy") || c.FeatureOverrides.ContainsKey("diplomacy")) || c.FeatureEnabled("diplomacy"));
    public static ForeignAssistanceAssessment Assess(Campaign c, int requesterId, int donorId, float amount, int warId = -1)
    {
        var requester = c?.NationById(requesterId); var donor = c?.NationById(donorId);
        if (!Enabled(c)) return new(false, 0, 0, false, "Foreign assistance is disabled.");
        if (requester is null || donor is null || requesterId == donorId || !float.IsFinite(amount) || amount <= 0)
            return new(false, 0, 0, false, "Invalid assistance request.");
        if (requester.Population <= 0 && requester.Movement is null || donor.Population <= 0 && donor.Movement is null)
            return new(false, 0, 0, false, "A participant is no longer active.");
        var relationship = CampaignDiplomacy.Peek(c, requesterId, donorId);
        if (relationship?.Stance == DiplomaticStance.AtWar)
            return new(false, 0, 0, false, "The requested donor is at war with us.");
        if (relationship is not null && (long)c.ElapsedDays - relationship.AidSentDay < Math.Clamp(c.Defines.AidIntervalDays, 1, 3650))
            return new(false, 0, 0, false, "These countries exchanged financial aid recently. Wait for the aid interval.");
        if (!float.IsFinite(requester.Treasury) || !float.IsFinite(donor.Treasury) ||
            !float.IsFinite(requester.Gdp) || !float.IsFinite(donor.Gdp) || requester.Gdp < 0 || donor.Gdp < 0 ||
            !float.IsFinite(c.Defines.AidShareOfRecipientGdp) || c.Defines.AidShareOfRecipientGdp < 0 ||
            !float.IsFinite(c.Defines.AidMaxShareOfTreasury) || c.Defines.AidMaxShareOfTreasury < 0 ||
            !float.IsFinite(c.Defines.AiAidReserveOfGdp) || c.Defines.AiAidReserveOfGdp < 0)
            return new(false, 0, 0, false, "The financial inputs are invalid.");
        if (!float.IsFinite(c.Defines.AiAidCrisisTreasuryOfGdp) || c.Defines.AiAidCrisisTreasuryOfGdp < 0 ||
            !float.IsFinite(requester.WeeklyBalance) || !float.IsFinite(requester.PaymentArrears) || !float.IsFinite(requester.DebtArrears))
            return new(false, 0, 0, false, "The financial inputs are invalid.");
        var war = warId < 0 ? c.Wars.FirstOrDefault(w => !w.Ended && w.Involves(requesterId)) : c.Wars.FirstOrDefault(w => w.Id == warId);
        if (warId >= 0 && (war is null || war.Ended || !war.Involves(requesterId)))
            return new(false, 0, 0, false, "The requested war is no longer active.");
        var lowCash = requester.Treasury < Math.Max(.01, (double)requester.Gdp * c.Defines.AiAidCrisisTreasuryOfGdp);
        var fiscalTrouble = requester.WeeklyBalance < 0 || requester.PaymentArrears > 0 || requester.DebtArrears > 0;
        if (!lowCash || war is null && !fiscalTrouble)
            return new(false, 0, 0, false, "There is no current financial need for assistance.");
        var reserve = (double)donor.Gdp * c.Defines.AiAidReserveOfGdp;
        var payable = Math.Min(amount, Math.Min((double)requester.Gdp * c.Defines.AidShareOfRecipientGdp,
            Math.Min(Math.Max(0, donor.Treasury) * Math.Min(1d, c.Defines.AidMaxShareOfTreasury), Math.Max(0, donor.Treasury - reserve))));
        if (!double.IsFinite(payable) || payable < .01f || payable > float.MaxValue ||
            (double)requester.Treasury + payable > float.MaxValue || (double)requester.Treasury + payable < -float.MaxValue)
            return new(false, 0, 0, false, "The donor cannot spare an affordable package.");
        // Both ledgers are floats. Round down to a shared representable quantum;
        // subtracting a small amount from a huge treasury must never silently do nothing.
        var quantum = Math.Max(Math.Max(Quantum(donor.Treasury), Quantum(requester.Treasury)),
            Math.Max(Quantum((float)(donor.Treasury - payable)), Quantum((float)(requester.Treasury + payable))));
        var payment = (float)(Math.Floor(payable / quantum) * quantum);
        if ((double)payment > payable) payment = MathF.BitDecrement(payment);
        var debit = (double)donor.Treasury - (float)((double)donor.Treasury - payment);
        var credit = (double)(float)((double)requester.Treasury + payment) - requester.Treasury;
        // Crossing an exponent boundary changes a float ledger's spacing. Two
        // genuinely moving accounts can differ by a recipient rounding fraction;
        // reject no-op transfers and bound that error to one quantum AND 10 ppm.
        var roundingLimit = Math.Min(quantum, (double)payment * .00001);
        if (!float.IsFinite(payment) || payment <= 0 || debit <= 0 || credit <= 0 ||
            debit > payable || Math.Abs(debit - credit) > roundingLimit)
            return new(false, 0, 0, false, "The payment is too small for the current ledger precision.");
        var willingness = Willingness(c, requester, donor, war, relationship);
        return new(true, payment, willingness,
            !donor.IsHuman && willingness >= (float.IsFinite(c.Defines.AiAidRequestWillingness) ?
                Math.Clamp(c.Defines.AiAidRequestWillingness, 0, 3) : .45f), "An affordable financial package is available.");
    }
    private static double Quantum(float value)
    {
        var magnitude = MathF.Abs(value);
        var next = MathF.BitIncrement(magnitude);
        return float.IsFinite(next) ? (double)next - magnitude : (double)magnitude - MathF.BitDecrement(magnitude);
    }
    private static float Willingness(Campaign c, Nation requester, Nation donor, StrategicWar war, DiplomaticRelation relation)
    {
        var opinion = relation?.Opinion ?? 0;
        if (!float.IsFinite(opinion) || !float.IsFinite(donor.Stability)) return -2;
        var score = Math.Clamp(opinion / 100, -1, 1) +
            (relation?.Stance == DiplomaticStance.Allied ? .6f : relation?.Stance == DiplomaticStance.Friendly ? .25f : 0) +
            (relation?.GuaranteedBy(donor.Id) == true ? .3f : 0) +
            (requester.OverlordId == donor.Id && !string.IsNullOrEmpty(requester.SubjectType) ? .3f : 0) +
            (Math.Clamp(donor.Stability, 0, 1) - .5f) * .1f;
        if (c.Wars.Any(w => !w.Ended && w.Involves(donor.Id))) score -= .3f;
        if (war is not null)
        {
            if (war.AttackerId != requester.Id) score += .15f;
            var opponent = war.EnemiesOf(requester.Id).OrderBy(id => id).ToArray();
            foreach (var enemy in opponent)
            {
                var stance = CampaignDiplomacy.Peek(c, donor.Id, enemy)?.Stance;
                if (stance is DiplomaticStance.Rival or DiplomaticStance.AtWar) score += .45f;
                if (stance == DiplomaticStance.Allied) score -= 1;
            }
        }
        return Math.Clamp(score, -2, 3);
    }
    public static ForeignAssistanceOutcome Respond(Campaign c, int issuerId, int requestId, ForeignAssistanceReply reply, float approvedAmount = float.MaxValue)
    {
        var request = c?.AssistanceRequests.FirstOrDefault(r => r.Id == requestId);
        if (request is null || request.State != ForeignAssistanceState.Pending || !Enum.IsDefined(reply) ||
            (reply == ForeignAssistanceReply.Withdraw ? issuerId != request.RequesterId : issuerId != request.DonorId))
            return new(0, ForeignAssistanceState.Invalidated, 0, "Only the addressed donor may answer this pending request.");
        if (request.ExpiresDay <= c.ElapsedDays)
            return Close(c, request, ForeignAssistanceState.Expired, "The assistance request has expired. No payment was made.");
        if (reply != ForeignAssistanceReply.Accept)
        {
            return Close(c, request, reply == ForeignAssistanceReply.Withdraw ? ForeignAssistanceState.Withdrawn : ForeignAssistanceState.Declined,
                reply == ForeignAssistanceReply.Withdraw ? "The assistance request was withdrawn. No payment was made." : "The donor declined the request. No payment was made.");
        }
        var donor = c.NationById(request.DonorId); var requester = c.NationById(request.RequesterId);
        if (!request.HumanConsent || donor?.IsHuman != true)
            return Close(c, request, ForeignAssistanceState.Invalidated, "The addressed donor is no longer controlled by a player.");
        if (!float.IsFinite(approvedAmount) || approvedAmount <= 0)
            return Close(c, request, ForeignAssistanceState.Invalidated, "The approved assistance amount is invalid.");
        var assessment = Assess(c, request.RequesterId, request.DonorId, Math.Min(request.Amount, approvedAmount), request.WarId);
        if (!assessment.Allowed)
            return Close(c, request, ForeignAssistanceState.Invalidated, assessment.Reason);
        return Grant(c, request, assessment);
    }
    private static ForeignAssistanceOutcome Grant(Campaign c, ForeignAssistanceRequest request, ForeignAssistanceAssessment assessment)
    {
        var donor = c.NationById(request.DonorId); var requester = c.NationById(request.RequesterId);
        donor.Treasury = (float)((double)donor.Treasury - assessment.PayableAmount);
        requester.Treasury = (float)((double)requester.Treasury + assessment.PayableAmount);
        var relationship = CampaignDiplomacy.Get(c, request.RequesterId, request.DonorId);
        relationship.AidSentDay = c.ElapsedDays;
        var full = Math.Max(.0001, (double)requester.Gdp * c.Defines.AidShareOfRecipientGdp);
        OpinionSystem.Remember(c, relationship, "economic_aid", (float)Math.Min(1, assessment.PayableAmount / full));
        c.Chronicle.Record(c, "aid_sent", donor.Id, requester.Id);
        request.GrantedAmount = assessment.PayableAmount;
        return Close(c, request, ForeignAssistanceState.Granted, "The financial assistance was granted.");
    }
    public static ForeignAssistanceOutcome Submit(Campaign c, int requesterId, int donorId, float amount, int warId = -1)
    {
        var assessment = CanSubmit(c, requesterId, donorId, amount, warId);
        if (!assessment.Allowed) return new(0, ForeignAssistanceState.Invalidated, 0, assessment.Reason);
        c.AssistanceCooldowns.RemoveAll(x => !CooldownReserved(c, x));
        var request = new ForeignAssistanceRequest
        {
            Id = c.NextAssistanceRequestId++, RequesterId = requesterId, DonorId = donorId,
            WarId = warId, Amount = amount, CreatedDay = c.ElapsedDays,
            ExpiresDay = (int)Math.Min(int.MaxValue, (long)c.ElapsedDays + Math.Clamp(c.Defines.AidRequestOfferDays, 1, 365)),
            HumanConsent = c.NationById(donorId).IsHuman, Reason = "Awaiting the donor's reply."
        };
        c.AssistanceRequests.Add(request); c.MarkStrategicChange();
        RememberRequest(c, requesterId, donorId);
        if (request.HumanConsent) return new(request.Id, request.State, 0, request.Reason);
        return assessment.AiWouldGrant ? Grant(c, request, assessment) :
            Close(c, request, ForeignAssistanceState.Declined, "The donor government is unwilling to finance this request.");
    }
}
