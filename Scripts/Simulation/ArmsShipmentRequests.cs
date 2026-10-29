using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// The share of one day's depot intake sent when a shipment request is granted.
    /// Zero leaves the papers unfiled. This is not the weekly price of a proxy sponsorship.
    /// </summary>
    public float ArmsShipmentShare { get; set; } = 0.25f;
    /// <summary>How many days of that intake one granted shipment draws.</summary>
    public int ArmsShipmentDays { get; set; } = 14;
    /// <summary>How long a paper waits for the donor.</summary>
    public int ArmsShipmentOfferDays { get; set; } = 21;
    /// <summary>Days before the same pair may ask again.</summary>
    public int ArmsShipmentCooldownDays { get; set; } = 90;
    /// <summary>How often a belligerent may put one paper on the human's desk.</summary>
    public int ArmsShipmentReviewDays { get; set; } = 14;
}

public enum ArmsShipmentState { Pending, Granted, Declined, Withdrawn, Expired, Invalidated }
public enum ArmsShipmentReply { Accept, Decline, Withdraw }

/// <summary>
/// One finite request for ammunition, arms, artillery and drones from somebody else's depots.
/// A standing subsidy remains <see cref="ProxyWars"/>. Cash remains <see cref="ForeignAssistance"/>.
/// </summary>
public sealed class ArmsShipmentRequest
{
    public int Id;
    public int RequesterId;
    public int DonorId;
    public int WarId = -1;
    public int CreatedDay;
    public int ExpiresDay;
    public int ClosedDay = -1;
    public ArmsShipmentState State;
    /// <summary>English key chosen by the same stakes a sponsorship already recognises. Empty when there is none.</summary>
    public string Pretext = "";
    /// <summary>Pinned when the paper is filed, so a later define edit does not change a letter already on the desk.</summary>
    public float Share;
    public int Days;
    public float Shipped;
    public bool HumanConsent;
    public string Reason = "";
}

public sealed class ArmsShipmentCooldown
{
    public int RequesterId;
    public int DonorId;
    public int ReadyOnDay;
}

public readonly record struct ArmsShipmentAssessment(bool Allowed, float Share, int Days, float Quoted, string Pretext, string Reason);
public readonly record struct ArmsShipmentOutcome(int RequestId, ArmsShipmentState State, float Shipped, string Reason);

public sealed partial class Campaign
{
    public readonly List<ArmsShipmentRequest> ArmsShipmentRequests = new();
    public readonly List<ArmsShipmentCooldown> ArmsShipmentCooldowns = new();
    public int NextArmsShipmentRequestId = 1;
}

/// <summary>
/// A country at war asks for one shipment out of a depot. The human answers on paper.
/// Accepting calls <see cref="StrategicStockpiles.Ship"/> once. It does not open a sponsorship,
/// spend the treasury, take land, or rewrite an army order. A great power's wealth is not
/// itself a pretext: the stake has to be one the sponsorship rules already name.
/// </summary>
public static class ArmsShipmentRequests
{
    public const int MaxActive = 8, MaxHistory = 24, MaxCooldowns = 256;

    public static bool Enabled(Campaign c) => c is not null && c.Defines.StrategicStockpilesEnabled &&
        c.Defines.ArmsShipmentShare > 0f && c.FeatureEnabled("proxy_wars") &&
        MechanicFeatures.SystemEnabled(c, nameof(ProxyWars));

    public static int Incoming(Campaign c, int nationId) => c?.ArmsShipmentRequests.Count(r =>
        r.State == ArmsShipmentState.Pending && r.DonorId == nationId && r.ExpiresDay > c.ElapsedDays) ?? 0;

    public static ArmsShipmentRequest Copy(ArmsShipmentRequest request) => request is null ? null : new()
    {
        Id = request.Id, RequesterId = request.RequesterId, DonorId = request.DonorId, WarId = request.WarId,
        CreatedDay = request.CreatedDay, ExpiresDay = request.ExpiresDay, ClosedDay = request.ClosedDay,
        State = request.State, Pretext = request.Pretext ?? "", Share = request.Share, Days = request.Days,
        Shipped = request.Shipped, HumanConsent = request.HumanConsent, Reason = request.Reason ?? ""
    };

    public static ArmsShipmentCooldown Copy(ArmsShipmentCooldown clock) => clock is null ? null : new()
    {
        RequesterId = clock.RequesterId, DonorId = clock.DonorId, ReadyOnDay = clock.ReadyOnDay
    };

    public static void Restore(Campaign c, IEnumerable<ArmsShipmentRequest> requests, IEnumerable<ArmsShipmentCooldown> cooldowns, int nextId)
    {
        if (c is null) return;
        c.ArmsShipmentRequests.Clear();
        if (requests is not null)
            foreach (var request in requests)
                if (request is not null) c.ArmsShipmentRequests.Add(Copy(request));
        c.ArmsShipmentCooldowns.Clear();
        if (cooldowns is not null)
            foreach (var clock in cooldowns)
                if (clock is not null) c.ArmsShipmentCooldowns.Add(Copy(clock));
        var minimum = c.ArmsShipmentRequests.Count == 0 ? 1 : c.ArmsShipmentRequests.Max(r => r.Id) + 1;
        c.NextArmsShipmentRequestId = Math.Max(minimum, Math.Max(1, nextId));
    }

    public static void Tick(Campaign c)
    {
        if (c is null || !c.StepOpensNewDay || c.ElapsedDays <= 0) return;
        for (var i = 0; i < c.ArmsShipmentRequests.Count; i++)
        {
            var request = c.ArmsShipmentRequests[i];
            if (request.State != ArmsShipmentState.Pending) continue;
            if (!Enabled(c))
                Close(c, request, ArmsShipmentState.Invalidated, "Arms shipments are not available.");
            else if (request.ExpiresDay <= c.ElapsedDays)
                Close(c, request, ArmsShipmentState.Expired, "The shipment request has expired. Nothing left the depots.");
            else if (request.HumanConsent && c.NationById(request.DonorId)?.IsHuman != true)
                Close(c, request, ArmsShipmentState.Invalidated, "The addressed donor is no longer controlled by a player.");
            else
            {
                var war = WarFor(c, request.RequesterId, request.WarId);
                var requester = c.NationById(request.RequesterId);
                if (war is null || war.Ended || requester is null || !war.Involves(requester.Id))
                    Close(c, request, ArmsShipmentState.Invalidated, requester?.IsHuman == true
                        ? "You are not fighting a war." : "They are not fighting a war.");
                else if (war.Involves(request.DonorId))
                    Close(c, request, ArmsShipmentState.Invalidated, "You are fighting in that war yourself.");
                else if (CampaignDiplomacy.AtWar(c, request.DonorId, request.RequesterId))
                    Close(c, request, ArmsShipmentState.Invalidated, "You are at war with them.");
            }
        }
        if (!Enabled(c)) return;
        var cadence = Math.Clamp(c.Defines.ArmsShipmentReviewDays, 1, 365);
        if (c.ElapsedDays % cadence != 0) return;
        Nation human = null;
        foreach (var nation in c.Nations)
            if (nation.IsHuman && nation.Population > 0) { human = nation; break; }
        if (human is null) return;
        Nation asker = null;
        StrategicWar askerWar = null;
        foreach (var requester in c.Nations.OrderBy(n => n.Id))
        {
            if (requester.IsHuman || requester.Id == human.Id || requester.Population <= 0) continue;
            var war = WarFor(c, requester.Id, -1);
            if (war is null) continue;
            var assessment = CanSubmit(c, requester.Id, human.Id, war.Id);
            if (!assessment.Allowed) continue;
            asker = requester;
            askerWar = war;
            break;
        }
        if (asker is null) return;
        Submit(c, asker.Id, human.Id, askerWar.Id);
    }

    /// <summary>
    /// Why a donor would send arms into this war, or "" when the only case is being large.
    /// Wealth alone is not a pretext. Reading this changes nothing.
    /// </summary>
    public static string Pretext(Campaign c, Nation donor, Nation requester, Nation enemy, StrategicWar war)
    {
        if (c is null || donor is null || requester is null || enemy is null || war is null) return "";
        if (requester.AnswersTo(donor.Id))
            return "They answer to you, and the war has reached them.";
        var toRequester = CampaignDiplomacy.Peek(c, donor.Id, requester.Id);
        if (war.IsDefending(requester.Id) && toRequester is not null &&
            (toRequester.Stance == DiplomaticStance.Allied || toRequester.GuaranteedBy(donor.Id)))
            return "An ally is on the defensive, and you already promised to stand with them.";
        if (CampaignDiplomacy.SharesLandBorder(c, donor.MapIndex, requester.MapIndex) ||
            CampaignDiplomacy.SharesLandBorder(c, donor.MapIndex, enemy.MapIndex))
            return "The war is on your border.";
        var toEnemy = CampaignDiplomacy.Peek(c, donor.Id, enemy.Id);
        if (toEnemy is not null && (toEnemy.Stance == DiplomaticStance.Rival || toEnemy.Tension >= c.Defines.TensionCrisisAt))
            return "You are already at odds with the other side.";
        foreach (var backing in ProxyWars.Backing(c, war, !war.IsAttacking(requester.Id)))
            if (CampaignDiplomacy.Peek(c, donor.Id, backing.SponsorId) is { Stance: DiplomaticStance.Rival })
                return "A rival of yours is arming the other side.";
        return "";
    }

    public static ArmsShipmentAssessment CanSubmit(Campaign c, int requesterId, int donorId, int warId)
    {
        var share = c is null ? 0f : Math.Clamp(c.Defines.ArmsShipmentShare, 0f, 1f);
        var days = c is null ? 0 : Math.Clamp(c.Defines.ArmsShipmentDays, 1, 90);
        var assessment = Assess(c, requesterId, donorId, warId, share, days);
        if (!assessment.Allowed) return assessment;
        string refusal = null;
        if (c.ArmsShipmentRequests.Any(r => r.State == ArmsShipmentState.Pending && r.ExpiresDay > c.ElapsedDays &&
            ((r.RequesterId == requesterId && r.DonorId == donorId) || (r.RequesterId == donorId && r.DonorId == requesterId))))
            refusal = "A shipment between these countries already awaits a reply.";
        else if (c.ArmsShipmentCooldowns.Any(x => x.RequesterId == requesterId && x.DonorId == donorId && x.ReadyOnDay > c.ElapsedDays))
            refusal = "A shipment was asked for recently. Wait before asking again.";
        else if (c.ArmsShipmentRequests.Count(r => r.State == ArmsShipmentState.Pending) >= MaxActive)
            refusal = "Too many shipment requests are already open.";
        else if (c.NationById(donorId).IsHuman &&
            Incoming(c, donorId) + ForeignAssistance.Incoming(c, donorId) + DealNegotiations.Incoming(c, donorId) >=
            Math.Clamp(c.Defines.DealInboxLimit, 1, 16))
            refusal = "The diplomatic request inbox is full. Wait for a reply.";
        else if (c.ArmsShipmentCooldowns.Count >= MaxCooldowns)
            refusal = "Too many shipment requests are already open.";
        else if (c.NextArmsShipmentRequestId <= 0 || c.NextArmsShipmentRequestId == int.MaxValue)
            refusal = "Too many shipment requests are already open.";
        return refusal is null ? assessment : assessment with { Allowed = false, Reason = refusal };
    }

    public static ArmsShipmentAssessment Assess(Campaign c, int requesterId, int donorId, int warId, float share, int days)
    {
        ArmsShipmentAssessment Not(string reason) => new(false, share, days, 0f, "", reason);
        if (!Enabled(c)) return Not("Arms shipments are not available.");
        var requester = c.NationById(requesterId);
        var donor = c.NationById(donorId);
        if (requester is null || donor is null || requesterId == donorId)
            return Not("Choose two countries.");
        if (requester.Population <= 0 && requester.Movement is null || donor.Population <= 0 && donor.Movement is null)
            return Not("A participant is no longer active.");
        if (donor.Movement is not null) return Not("A movement cannot send a shipment.");
        if (CampaignDiplomacy.AtWar(c, requesterId, donorId))
            return Not(donor.IsHuman ? "You are at war with them." : "They are at war with you.");
        var war = WarFor(c, requesterId, warId);
        if (war is null || war.Ended || !war.Involves(requesterId))
            return Not(requester.IsHuman ? "You are not fighting a war." : "They are not fighting a war.");
        if (war.Involves(donorId))
            return Not(donor.IsHuman ? "You are fighting in that war yourself." : "They are fighting in that war themselves.");
        var enemy = c.NationById(war.EnemyLeaderOf(requesterId));
        var pretext = Pretext(c, donor, requester, enemy, war);
        if (pretext.Length == 0) return Not("There is no pretext for sending arms into that war.");
        if (!float.IsFinite(share) || share <= 0f || days <= 0) return Not("Arms shipments are not available.");
        if (!donor.StockpilesSeeded || !requester.StockpilesSeeded)
            return new(false, share, days, 0f, pretext, "The depots have not been opened, so there is nothing to send.");
        if (!CouldSpare(c, donor, requester))
            return new(false, share, days, 0f, pretext, "The depots could not spare a shipment of ammunition, arms, artillery or drones.");
        return new(true, share, days, 0f, pretext, "A finite shipment can leave the depots.");
    }

    /// <summary>
    /// Whether a depot that is already open has any of the goods <see cref="StrategicStockpiles.Ship"/>
    /// draws, and the recipient has room. It does not open a depot and does not move stock.
    /// The exact amount is whatever Ship moves on consent; the letter prices the share and the days.
    /// </summary>
    static bool CouldSpare(Campaign c, Nation from, Nation to)
    {
        foreach (var good in new[] { "Ammunition", "Arms", "Artillery", "Drones" })
        {
            if (StrategicStockpiles.Coverage(from, good) < 1f) continue;
            var have = from.Stockpiles.GetValueOrDefault(good);
            var room = StrategicStockpiles.Capacity(c, to, good) - to.Stockpiles.GetValueOrDefault(good);
            if (have > 0f && room > 0f) return true;
        }
        return false;
    }

    public static ArmsShipmentOutcome Submit(Campaign c, int requesterId, int donorId, int warId = -1)
    {
        var assessment = CanSubmit(c, requesterId, donorId, warId);
        if (!assessment.Allowed) return new(0, ArmsShipmentState.Invalidated, 0, assessment.Reason);
        var war = WarFor(c, requesterId, warId);
        var donor = c.NationById(donorId);
        var request = new ArmsShipmentRequest
        {
            Id = c.NextArmsShipmentRequestId++, RequesterId = requesterId, DonorId = donorId, WarId = war.Id,
            CreatedDay = c.ElapsedDays,
            ExpiresDay = (int)Math.Min(int.MaxValue, (long)c.ElapsedDays + Math.Clamp(c.Defines.ArmsShipmentOfferDays, 1, 365)),
            Share = assessment.Share, Days = assessment.Days, Pretext = assessment.Pretext,
            HumanConsent = donor.IsHuman, Reason = "Awaiting the donor's reply."
        };
        c.ArmsShipmentRequests.Add(request);
        Remember(c, requesterId, donorId);
        c.MarkStrategicChange();
        if (request.HumanConsent) return new(request.Id, request.State, 0, request.Reason);
        return Grant(c, request);
    }

    public static ArmsShipmentOutcome Respond(Campaign c, int issuerId, int requestId, ArmsShipmentReply reply)
    {
        var request = c?.ArmsShipmentRequests.FirstOrDefault(r => r.Id == requestId);
        if (request is null || request.State != ArmsShipmentState.Pending || !Enum.IsDefined(reply) ||
            (reply == ArmsShipmentReply.Withdraw ? issuerId != request.RequesterId : issuerId != request.DonorId))
            return new(0, ArmsShipmentState.Invalidated, 0, "Only the addressed donor may answer this pending request.");
        if (request.ExpiresDay <= c.ElapsedDays)
            return Close(c, request, ArmsShipmentState.Expired, "The shipment request has expired. Nothing left the depots.");
        if (reply == ArmsShipmentReply.Decline)
            return Close(c, request, ArmsShipmentState.Declined, "The donor declined the shipment. Nothing left the depots.");
        if (reply == ArmsShipmentReply.Withdraw)
            return Close(c, request, ArmsShipmentState.Withdrawn, "The shipment request was withdrawn. Nothing left the depots.");
        if (!request.HumanConsent || c.NationById(request.DonorId)?.IsHuman != true)
            return Close(c, request, ArmsShipmentState.Invalidated, "The addressed donor is no longer controlled by a player.");
        var assessment = Assess(c, request.RequesterId, request.DonorId, request.WarId, request.Share, request.Days);
        if (!assessment.Allowed)
            return Close(c, request, ArmsShipmentState.Invalidated, assessment.Reason);
        return Grant(c, request);
    }

    private static ArmsShipmentOutcome Grant(Campaign c, ArmsShipmentRequest request)
    {
        var donor = c.NationById(request.DonorId);
        var requester = c.NationById(request.RequesterId);
        var shipped = StrategicStockpiles.Ship(c, donor, requester, request.Share, request.Days);
        if (shipped <= 0f)
            return Close(c, request, ArmsShipmentState.Invalidated, "The depots could not spare a shipment of ammunition, arms, artillery or drones.");
        request.Shipped = shipped;
        var war = WarFor(c, request.RequesterId, request.WarId);
        OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, donor.Id, requester.Id), "armed_us");
        c.Chronicle.Record(c, "arms_shipment", donor.Id, requester.Id, war?.Name ?? "");
        return Close(c, request, ArmsShipmentState.Granted,
            "The shipment was sent. It is drawn from depots, not from the treasury, and it does not enter the war.");
    }

    private static ArmsShipmentOutcome Close(Campaign c, ArmsShipmentRequest request, ArmsShipmentState state, string reason)
    {
        request.State = state;
        request.ClosedDay = c.ElapsedDays;
        request.Reason = reason;
        Remember(c, request.RequesterId, request.DonorId);
        Trim(c);
        c.MarkStrategicChange();
        return new(request.Id, state, request.Shipped, reason);
    }

    private static void Remember(Campaign c, int requesterId, int donorId)
    {
        var clock = c.ArmsShipmentCooldowns.FirstOrDefault(x => x.RequesterId == requesterId && x.DonorId == donorId);
        if (clock is null)
        {
            if (c.ArmsShipmentCooldowns.Count >= MaxCooldowns) return;
            clock = new ArmsShipmentCooldown { RequesterId = requesterId, DonorId = donorId };
            c.ArmsShipmentCooldowns.Add(clock);
            c.ArmsShipmentCooldowns.Sort((a, b) => a.RequesterId != b.RequesterId
                ? a.RequesterId.CompareTo(b.RequesterId) : a.DonorId.CompareTo(b.DonorId));
        }
        clock.ReadyOnDay = (int)Math.Min(int.MaxValue, (long)c.ElapsedDays + Math.Clamp(c.Defines.ArmsShipmentCooldownDays, 1, 3650));
    }

    private static void Trim(Campaign c)
    {
        var old = c.ArmsShipmentRequests.Where(r => r.State != ArmsShipmentState.Pending)
            .OrderByDescending(r => r.Id).Skip(MaxHistory).Select(r => r.Id).ToHashSet();
        if (old.Count > 0) c.ArmsShipmentRequests.RemoveAll(r => old.Contains(r.Id));
    }

    private static StrategicWar WarFor(Campaign c, int requesterId, int warId)
    {
        if (warId >= 0)
        {
            foreach (var war in c.Wars)
                if (war.Id == warId) return war;
            return null;
        }
        StrategicWar found = null;
        foreach (var war in c.Wars)
        {
            if (war.Ended || !war.Involves(requesterId)) continue;
            if (found is null || war.Id < found.Id) found = war;
        }
        return found;
    }
}

public sealed class RequestArmsShipmentCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int DonorId { get; init; }
    public int WarId { get; init; } = -1;
    [System.Text.Json.Serialization.JsonIgnore] public ArmsShipmentOutcome Outcome { get; private set; }

    public void Apply(Campaign campaign) =>
        Outcome = MechanicFeatures.Allows(campaign, this)
            ? ArmsShipmentRequests.Submit(campaign, IssuerId, DonorId, WarId)
            : new(0, ArmsShipmentState.Invalidated, 0, "Arms shipments are not available.");
}

public sealed class RespondArmsShipmentCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int RequestId { get; init; }
    public ArmsShipmentReply Reply { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public ArmsShipmentOutcome Outcome { get; private set; }

    public void Apply(Campaign campaign) =>
        Outcome = MechanicFeatures.Allows(campaign, this)
            ? ArmsShipmentRequests.Respond(campaign, IssuerId, RequestId, Reply)
            : new(0, ArmsShipmentState.Invalidated, 0, "Arms shipments are not available.");
}
