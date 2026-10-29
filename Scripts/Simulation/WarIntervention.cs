using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How long a plea for military intervention waits for a decision.</summary>
    public int InterventionOfferDays { get; set; } = 21;
    /// <summary>Days before the same pair may petition for intervention again.</summary>
    public int InterventionCooldownDays { get; set; } = 60;
    /// <summary>How often a belligerent reviews whether to petition a human ally/guarantor for intervention.</summary>
    public int InterventionReviewDays { get; set; } = 14;
    /// <summary>Willingness score threshold above which an AI government agrees to intervene in a foreign war.</summary>
    public float AiInterventionWillingness { get; set; } = 0.45f;
}

public enum WarInterventionState { Pending, Joined, Declined, Withdrawn, Expired, Invalidated }
public enum WarInterventionReply { Accept, Decline, Withdraw }

/// <summary>
/// A formal plea from a nation fighting a war asking a foreign power to intervene militarily as a co-belligerent.
/// Cash assistance remains <see cref="ForeignAssistance"/>; depot shipments remain <see cref="ArmsShipmentRequests"/>.
/// </summary>
public sealed class WarInterventionRequest
{
    public int Id;
    public int RequesterId;
    public int DonorId;
    public int WarId = -1;
    public int CreatedDay;
    public int ExpiresDay;
    public int ClosedDay = -1;
    public WarInterventionState State;
    /// <summary>Legitimate pretext key recognized by treaty, hierarchy, or border threat. Empty if none.</summary>
    public string Pretext = "";
    public bool HumanConsent;
    public string Reason = "";
}

public sealed class WarInterventionCooldown
{
    public int RequesterId;
    public int DonorId;
    public int ReadyOnDay;
}

public readonly record struct WarInterventionAssessment(bool Allowed, string Pretext, float Willingness, bool AiWouldGrant, string Reason);
public readonly record struct WarInterventionOutcome(int RequestId, WarInterventionState State, string Reason);

public sealed partial class Campaign
{
    public readonly List<WarInterventionRequest> InterventionRequests = new();
    public readonly List<WarInterventionCooldown> InterventionCooldowns = new();
    public int NextInterventionRequestId = 1;
}

/// <summary>
/// A country in a war asks a partner to intervene militarily.
/// An answering player decides through the diplomatic inbox. Accepting calls <see cref="CampaignDiplomacy.JoinWar"/>.
/// Declining an ally in a defensive war breaks the alliance and dishonours the treaty.
/// </summary>
public static class WarInterventions
{
    public const int MaxActive = 8, MaxHistory = 24, MaxCooldowns = 256;

    public static bool Enabled(Campaign c) => c is not null && c.FeatureEnabled("diplomacy") &&
        MechanicFeatures.SystemEnabled(c, nameof(CampaignDiplomacy));

    public static int Incoming(Campaign c, int nationId) => c?.InterventionRequests.Count(r =>
        r.State == WarInterventionState.Pending && r.DonorId == nationId && r.ExpiresDay > c.ElapsedDays) ?? 0;

    public static WarInterventionRequest Copy(WarInterventionRequest r) => r is null ? null : new()
    {
        Id = r.Id, RequesterId = r.RequesterId, DonorId = r.DonorId, WarId = r.WarId,
        CreatedDay = r.CreatedDay, ExpiresDay = r.ExpiresDay, ClosedDay = r.ClosedDay,
        State = r.State, Pretext = r.Pretext ?? "", HumanConsent = r.HumanConsent, Reason = r.Reason ?? ""
    };

    public static WarInterventionCooldown Copy(WarInterventionCooldown c) => c is null ? null : new()
    {
        RequesterId = c.RequesterId, DonorId = c.DonorId, ReadyOnDay = c.ReadyOnDay
    };

    public static void Restore(Campaign c, IEnumerable<WarInterventionRequest> requests,
        IEnumerable<WarInterventionCooldown> cooldowns, int nextId)
    {
        if (c is null) return;
        c.InterventionRequests.Clear();
        if (requests is not null)
            foreach (var r in requests.Take(4096))
                if (r is not null && r.Id > 0 && Enum.IsDefined(r.State))
                    c.InterventionRequests.Add(Copy(r));
        c.InterventionCooldowns.Clear();
        if (cooldowns is not null)
            foreach (var clock in cooldowns.Take(MaxCooldowns * 4))
                if (clock is not null)
                    c.InterventionCooldowns.Add(Copy(clock));
        var minimum = c.InterventionRequests.Count == 0 ? 1 : c.InterventionRequests.Max(r => r.Id) + 1;
        c.NextInterventionRequestId = Math.Max(minimum, Math.Max(1, nextId));
    }

    public static void Tick(Campaign c)
    {
        if (c is null || !c.StepOpensNewDay || c.ElapsedDays <= 0) return;
        for (var i = 0; i < c.InterventionRequests.Count; i++)
        {
            var r = c.InterventionRequests[i];
            if (r.State != WarInterventionState.Pending) continue;
            if (!Enabled(c))
                Close(c, r, WarInterventionState.Invalidated, "Military intervention is not available.");
            else if (r.ExpiresDay <= c.ElapsedDays)
                Close(c, r, WarInterventionState.Expired, "The intervention plea has expired. No armies entered the war.");
            else if (r.HumanConsent && c.NationById(r.DonorId)?.IsHuman != true)
                Close(c, r, WarInterventionState.Invalidated, "The addressed government is no longer controlled by a player.");
            else
            {
                var war = WarFor(c, r.RequesterId, r.WarId);
                var requester = c.NationById(r.RequesterId);
                if (war is null || war.Ended || requester is null || !war.Involves(requester.Id))
                    Close(c, r, WarInterventionState.Invalidated, requester?.IsHuman == true
                        ? "You are not fighting a war." : "They are not fighting a war.");
                else if (war.Involves(r.DonorId))
                    Close(c, r, WarInterventionState.Invalidated, "They are fighting in that war themselves.");
                else if (CampaignDiplomacy.AtWar(c, r.DonorId, r.RequesterId))
                    Close(c, r, WarInterventionState.Invalidated, "They are at war with you.");
            }
        }
        if (!Enabled(c)) return;
        var cadence = Math.Clamp(c.Defines.InterventionReviewDays, 1, 365);
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
            if (war is null || war.Ended) continue;
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
    /// Why a donor would commit armies into this war. Mere size or wealth is not a pretext.
    /// </summary>
    public static string Pretext(Campaign c, Nation donor, Nation requester, Nation enemy, StrategicWar war)
    {
        if (c is null || donor is null || requester is null || enemy is null || war is null) return "";
        if (requester.AnswersTo(donor.Id))
            return "They answer to you, and the war has reached them.";
        var toRequester = CampaignDiplomacy.Peek(c, donor.Id, requester.Id);
        var defending = war.IsDefending(requester.Id);
        if (defending && toRequester is not null &&
            (toRequester.Stance == DiplomaticStance.Allied || toRequester.GuaranteedBy(donor.Id)))
            return "An ally is on the defensive, and you already promised to stand with them.";
        if (!defending && toRequester is { Stance: DiplomaticStance.Allied })
            return "An ally needs your forces to turn the tide of war.";
        if (defending && toRequester is not null && toRequester.GuaranteedBy(donor.Id))
            return "You guaranteed their sovereignty against foreign aggression.";
        if (defending && InternationalOrganizations.DefencePartners(c, requester.Id).Contains(donor.Id))
            return "You are bound by a mutual defence charter.";
        var toEnemy = CampaignDiplomacy.Peek(c, donor.Id, enemy.Id);
        if ((CampaignDiplomacy.SharesLandBorder(c, donor.MapIndex, requester.MapIndex) ||
             CampaignDiplomacy.SharesLandBorder(c, donor.MapIndex, enemy.MapIndex)) &&
            toEnemy is not null && (toEnemy.Stance == DiplomaticStance.Rival || toEnemy.Opinion <= c.Defines.AiRivalryThreshold))
            return "The war is on your border and the attacker is a dangerous rival.";
        return "";
    }

    public static WarInterventionAssessment CanSubmit(Campaign c, int requesterId, int donorId, int warId)
    {
        var assessment = Assess(c, requesterId, donorId, warId);
        if (!assessment.Allowed) return assessment;
        string refusal = null;
        if (c.InterventionRequests.Any(r => r.State == WarInterventionState.Pending && r.ExpiresDay > c.ElapsedDays &&
            ((r.RequesterId == requesterId && r.DonorId == donorId) || (r.RequesterId == donorId && r.DonorId == requesterId))))
            refusal = "An intervention plea between these countries already awaits a reply.";
        else if (c.InterventionCooldowns.Any(x => x.RequesterId == requesterId && x.DonorId == donorId && x.ReadyOnDay > c.ElapsedDays))
            refusal = "An intervention plea was made recently. Wait before asking again.";
        else if (c.InterventionRequests.Count(r => r.State == WarInterventionState.Pending) >= MaxActive)
            refusal = "Too many intervention pleas are already open.";
        else if (c.NationById(donorId)?.IsHuman == true &&
            Incoming(c, donorId) + ForeignAssistance.Incoming(c, donorId) +
            ArmsShipmentRequests.Incoming(c, donorId) + DealNegotiations.Incoming(c, donorId) >=
            Math.Clamp(c.Defines.DealInboxLimit, 1, 16))
            refusal = "The diplomatic request inbox is full. Wait for a reply.";
        else if (c.InterventionCooldowns.Count >= MaxCooldowns)
            refusal = "Too many intervention pleas are already open.";
        else if (c.NextInterventionRequestId <= 0 || c.NextInterventionRequestId == int.MaxValue)
            refusal = "Too many intervention pleas are already open.";
        return refusal is null ? assessment : assessment with { Allowed = false, Reason = refusal };
    }

    public static WarInterventionAssessment Assess(Campaign c, int requesterId, int donorId, int warId)
    {
        WarInterventionAssessment Not(string reason) => new(false, "", 0f, false, reason);
        if (!Enabled(c)) return Not("Military intervention is not available.");
        var requester = c.NationById(requesterId);
        var donor = c.NationById(donorId);
        if (requester is null || donor is null || requesterId == donorId)
            return Not("Choose two countries.");
        if ((requester.Population <= 0 && requester.Movement is null) ||
            (donor.Population <= 0 && donor.Movement is null))
            return Not("A participant is no longer active.");
        if (donor.Movement is not null) return Not("A movement cannot send military forces.");
        if (CampaignDiplomacy.AtWar(c, requesterId, donorId))
            return Not(donor.IsHuman ? "They are at war with you." : "You are at war with them.");
        var war = WarFor(c, requesterId, warId);
        if (war is null || war.Ended || !war.Involves(requesterId))
            return Not(requester.IsHuman ? "You are not fighting a war." : "They are not fighting a war.");
        if (war.Involves(donorId))
            return Not(donor.IsHuman ? "They are fighting in that war themselves." : "You are fighting in that war yourself.");
        var enemyId = war.EnemyLeaderOf(requesterId);
        var enemy = c.NationById(enemyId);
        if (enemy is null) return Not("No enemy leader in this war.");
        if (!CampaignDiplomacy.CanDeclareWar(c, donorId, enemyId, out var why, onOwnInitiative: false))
            return Not(why);
        var pretext = Pretext(c, donor, requester, enemy, war);
        if (pretext.Length == 0) return Not("There is no pretext for requesting military intervention in that war.");

        var bond = CampaignDiplomacy.Get(c, donorId, requesterId);
        var willingness = bond?.Opinion / 100f ?? 0f;
        var defending = war.IsDefending(requesterId);
        if (bond is not null && bond.Stance == DiplomaticStance.Allied)
            willingness += defending ? 0.55f : 0.25f;
        if (bond is not null && bond.GuaranteedBy(donorId))
            willingness += defending ? 0.45f : 0.15f;
        if (defending && InternationalOrganizations.DefencePartners(c, requesterId).Contains(donorId))
            willingness += 0.55f;
        willingness += Math.Clamp(donor.WarSupport - 0.5f, -0.4f, 0.4f);
        willingness += Math.Clamp(donor.Stability - 0.5f, -0.3f, 0.3f);
        var mine = AiStatecraft.StrengthOf(c, donor);
        var enemyStrength = CampaignIntel.EstimatedStrength(c, donorId, enemy);
        willingness += Math.Clamp(mine / MathF.Max(0.01f, enemyStrength) - 0.75f, -0.8f, 0.8f);
        if (c.Wars.Any(w => !w.Ended && w.Involves(donorId))) willingness -= 0.6f;
        var aiWouldGrant = !donor.IsHuman && willingness >= c.Defines.AiInterventionWillingness;

        return new(true, pretext, willingness, aiWouldGrant, "A valid pretext exists for requesting military intervention.");
    }

    public static WarInterventionOutcome Submit(Campaign c, int requesterId, int donorId, int warId = -1)
    {
        var assessment = CanSubmit(c, requesterId, donorId, warId);
        if (!assessment.Allowed)
            return new(0, WarInterventionState.Invalidated, assessment.Reason);
        var war = WarFor(c, requesterId, warId);
        var donor = c.NationById(donorId);
        var request = new WarInterventionRequest
        {
            Id = c.NextInterventionRequestId++,
            RequesterId = requesterId,
            DonorId = donorId,
            WarId = war.Id,
            CreatedDay = c.ElapsedDays,
            ExpiresDay = c.ElapsedDays + Math.Clamp(c.Defines.InterventionOfferDays, 1, 90),
            Pretext = assessment.Pretext,
            HumanConsent = donor.IsHuman,
            Reason = "Awaiting the addressed government's response."
        };
        c.InterventionRequests.Add(request);
        c.MarkStrategicChange();
        if (request.HumanConsent)
            return new(request.Id, WarInterventionState.Pending, request.Reason);
        return assessment.AiWouldGrant
            ? Grant(c, request)
            : Close(c, request, WarInterventionState.Declined, "The foreign government declined to intervene. No armies entered the war.");
    }

    public static WarInterventionOutcome Respond(Campaign c, int issuerId, int requestId, WarInterventionReply reply)
    {
        var request = c?.InterventionRequests.FirstOrDefault(r => r.Id == requestId);
        if (request is null || request.State != WarInterventionState.Pending || !Enum.IsDefined(reply) ||
            (reply == WarInterventionReply.Withdraw ? issuerId != request.RequesterId : issuerId != request.DonorId))
            return new(0, WarInterventionState.Invalidated, "Only the addressed government may answer this pending plea.");
        if (request.ExpiresDay <= c.ElapsedDays)
            return Close(c, request, WarInterventionState.Expired, "The intervention plea has expired. No armies entered the war.");
        if (reply == WarInterventionReply.Withdraw)
            return Close(c, request, WarInterventionState.Withdrawn, "The intervention plea was withdrawn. No armies entered the war.");
        if (reply == WarInterventionReply.Decline)
        {
            var donorNation = c.NationById(request.DonorId);
            var requesterNation = c.NationById(request.RequesterId);
            var war = WarFor(c, request.RequesterId, request.WarId);
            var bond = CampaignDiplomacy.Get(c, request.DonorId, request.RequesterId);
            if (donorNation is not null && requesterNation is not null && war is not null && bond is not null && war.IsDefending(request.RequesterId))
            {
                if (bond.Stance == DiplomaticStance.Allied || bond.GuaranteedBy(request.DonorId))
                {
                    OpinionSystem.Remember(c, bond, "abandoned_us");
                    bond.LastActionDay = c.ElapsedDays;
                    if (bond.Stance == DiplomaticStance.Allied)
                    {
                        bond.Stance = DiplomaticStance.Neutral;
                        OpinionSystem.Forget(c, bond, "alliance");
                    }
                    if (bond.GuaranteedBy(request.DonorId))
                    {
                        if (request.DonorId == bond.A) bond.GuaranteeByA = false;
                        else bond.GuaranteeByB = false;
                        OpinionSystem.Forget(c, bond, "guaranteed_us");
                    }
                    donorNation.Stability = Math.Clamp(donorNation.Stability - c.Defines.BrokenPromiseStabilityCost, 0f, 1f);
                }
                else
                {
                    OpinionSystem.Remember(c, bond, "refused_a_call");
                    bond.LastActionDay = c.ElapsedDays;
                }
            }
            return Close(c, request, WarInterventionState.Declined, "The foreign government declined to intervene. No armies entered the war.");
        }

        if (!request.HumanConsent || c.NationById(request.DonorId)?.IsHuman != true)
            return Close(c, request, WarInterventionState.Invalidated, "The addressed government is no longer controlled by a player.");
        var assessment = Assess(c, request.RequesterId, request.DonorId, request.WarId);
        if (!assessment.Allowed)
            return Close(c, request, WarInterventionState.Invalidated, assessment.Reason);
        return Grant(c, request);
    }

    private static WarInterventionOutcome Grant(Campaign c, WarInterventionRequest request)
    {
        var donor = c.NationById(request.DonorId);
        var requester = c.NationById(request.RequesterId);
        var war = WarFor(c, request.RequesterId, request.WarId);
        if (war is null || donor is null || requester is null)
            return Close(c, request, WarInterventionState.Invalidated, "The war or participants are no longer active.");

        var defending = war.IsDefending(requester.Id);
        if (!CampaignDiplomacy.JoinWar(c, war, donor.Id, defending, "military_intervention"))
            return Close(c, request, WarInterventionState.Invalidated, "Could not join the war.");

        var bond = CampaignDiplomacy.Get(c, donor.Id, requester.Id);
        OpinionSystem.Remember(c, bond, "stood_by_us");
        c.Chronicle.Record(c, "military_intervention", donor.Id, requester.Id, war.Name);
        return Close(c, request, WarInterventionState.Joined, "The nation entered the war as an active belligerent.");
    }

    private static WarInterventionOutcome Close(Campaign c, WarInterventionRequest request, WarInterventionState state, string reason)
    {
        request.State = state;
        request.ClosedDay = c.ElapsedDays;
        request.Reason = reason;
        Remember(c, request.RequesterId, request.DonorId);
        Trim(c);
        c.MarkStrategicChange();
        return new(request.Id, state, reason);
    }

    private static void Remember(Campaign c, int requesterId, int donorId)
    {
        var clock = c.InterventionCooldowns.FirstOrDefault(x => x.RequesterId == requesterId && x.DonorId == donorId);
        if (clock is null)
        {
            if (c.InterventionCooldowns.Count >= MaxCooldowns) return;
            clock = new WarInterventionCooldown { RequesterId = requesterId, DonorId = donorId };
            c.InterventionCooldowns.Add(clock);
            c.InterventionCooldowns.Sort((a, b) => a.RequesterId != b.RequesterId
                ? a.RequesterId.CompareTo(b.RequesterId) : a.DonorId.CompareTo(b.DonorId));
        }
        clock.ReadyOnDay = (int)Math.Min(int.MaxValue, (long)c.ElapsedDays + Math.Clamp(c.Defines.InterventionCooldownDays, 1, 3650));
    }

    private static void Trim(Campaign c)
    {
        var old = c.InterventionRequests.Where(r => r.State != WarInterventionState.Pending)
            .OrderByDescending(r => r.Id).Skip(MaxHistory).Select(r => r.Id).ToHashSet();
        if (old.Count > 0) c.InterventionRequests.RemoveAll(r => old.Contains(r.Id));
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

public sealed class RequestWarInterventionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int DonorId { get; init; }
    public int WarId { get; init; } = -1;
    [System.Text.Json.Serialization.JsonIgnore] public WarInterventionOutcome Outcome { get; private set; }

    public void Apply(Campaign campaign) =>
        Outcome = MechanicFeatures.Allows(campaign, this)
            ? WarInterventions.Submit(campaign, IssuerId, DonorId, WarId)
            : new(0, WarInterventionState.Invalidated, "Military intervention is not available.");
}

public sealed class RespondWarInterventionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int RequestId { get; init; }
    public WarInterventionReply Reply { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public WarInterventionOutcome Outcome { get; private set; }

    public void Apply(Campaign campaign) =>
        Outcome = MechanicFeatures.Allows(campaign, this)
            ? WarInterventions.Respond(campaign, IssuerId, RequestId, Reply)
            : new(0, WarInterventionState.Invalidated, "Military intervention is not available.");
}
