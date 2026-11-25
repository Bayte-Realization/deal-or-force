using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public static partial class AiStatecraft
{
    /// <summary>
    /// One campaign's memory of the weekly diplomacy pass. A second campaign in the same
    /// process has its own: the stamp is not saved and is not part of the world.
    /// </summary>
    internal sealed class WeeklyScanMemory
    {
        internal int Walks;
        internal readonly Dictionary<long, WeeklyStamp> Stamps = new(PairKeyComparer.Instance);
    }

    internal struct WeeklyStamp
    {
        internal bool Borders;
        internal float Opinion;
        internal DiplomaticStance Stance;
        internal float Politics;
        internal float Identity;
        internal float Social;
        internal float Claims;
        internal float Geopolitics;
    }

    static readonly ConditionalWeakTable<Campaign, WeeklyScanMemory> WeeklyScans = new();

    static WeeklyScanMemory WeeklyScanOf(Campaign campaign) =>
        WeeklyScans.GetValue(campaign, static _ => new WeeklyScanMemory());

    internal static int WeeklyPairWalks(Campaign campaign) =>
        campaign is not null && WeeklyScans.TryGetValue(campaign, out var memory) ? memory.Walks : 0;

    static long WeeklyPairKey(int a, int b)
    {
        var low = a < b ? a : b;
        var high = a < b ? b : a;
        return ((long)low << 32) | (uint)high;
    }

    /// <summary>
    /// The border, opinion and stance recorded at the last walk of this pair. A pair that
    /// has never been walked does not match.
    /// </summary>
    internal static bool SameWeeklyPair(Campaign campaign, int a, int b, bool borders, float opinion, DiplomaticStance stance)
    {
        if (campaign is null || !WeeklyScans.TryGetValue(campaign, out var memory)) return false;
        return memory.Stamps.TryGetValue(WeeklyPairKey(a, b), out var stamp) &&
               stamp.Borders == borders && stamp.Opinion == opinion && stamp.Stance == stance;
    }

    /// <summary>
    /// Whether this pair's border, opinion and stance are the ones last walked, and the
    /// walk would write nothing. A read: it does not open a relation, change a stance, or
    /// record a chronicle line. A pair that has never been walked is not unchanged.
    /// </summary>
    internal static bool UnchangedWeeklyPair(Campaign campaign, Nation nation, Nation other, bool borders,
        float myStrength, bool identityOn)
    {
        if (campaign is null || nation is null || other is null || nation.Id == other.Id) return false;
        var peeked = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
        return UnchangedWeeklyPair(campaign, nation, other, peeked, borders, myStrength, identityOn);
    }

    /// <summary>
    /// The same question over a relation already in hand. The stamp is read once and passed
    /// down: the three remembered facts and the three standing terms sit in one entry, and
    /// the pass used to fetch the memory of the campaign and that entry twice for every pair
    /// it left alone.
    /// </summary>
    static bool UnchangedWeeklyPair(Campaign campaign, WeeklyScanMemory memory, Nation nation, Nation other,
        DiplomaticRelation peeked, bool borders, float myStrength, bool identityOn)
    {
        if (peeked is null || memory is null) return false;
        if (!memory.Stamps.TryGetValue(WeeklyPairKey(nation.Id, other.Id), out var stamp)) return false;
        if (stamp.Borders != borders || stamp.Opinion != peeked.Opinion || stamp.Stance != peeked.Stance) return false;
        return !WeeklyPassWouldWrite(campaign, nation, other, peeked, borders, myStrength, identityOn, stamp);
    }

    static bool UnchangedWeeklyPair(Campaign campaign, Nation nation, Nation other, DiplomaticRelation peeked,
        bool borders, float myStrength, bool identityOn)
    {
        if (peeked is null) return false;
        if (!WeeklyScans.TryGetValue(campaign, out var memory)) return false;
        return UnchangedWeeklyPair(campaign, memory, nation, other, peeked, borders, myStrength, identityOn);
    }

    /// <summary>
    /// The weekly opinion pass for one pair. Returns true when the pair was walked.
    /// </summary>
    internal static bool ReviewWeeklyPair(Campaign campaign, Nation nation, Nation other, bool borders,
        float myStrength, bool identityOn, out DiplomaticRelation relation) =>
        ReviewWeeklyPair(campaign, WeeklyScanOf(campaign), nation, other, borders, myStrength, identityOn, out relation);

    internal struct WeeklyPairPrecheck
    {
        internal DiplomaticRelation Peeked;
        internal bool Awake;
        internal bool Borders;
        internal bool Unchanged;
        internal bool HasComputed;
        internal bool Faith;
        internal bool AssessmentDue;
        internal float Strength;
        internal float Fear;
    }

    internal static WeeklyPairPrecheck PrecheckWeeklyPair(Campaign campaign, WeeklyScanMemory memory,
        Nation nation, Nation other, bool borders, float myStrength, bool identityOn)
    {
        var result = new WeeklyPairPrecheck { Borders = borders };
        if (campaign is null || nation is null || other is null || nation.Id == other.Id)
        {
            result.Unchanged = true;
            return result;
        }
        var peeked = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id, out var awake);
        result.Peeked = peeked;
        result.Awake = awake;
        if (peeked is null || memory is null) return result;
        if (!memory.Stamps.TryGetValue(WeeklyPairKey(nation.Id, other.Id), out var stamp)) return result;
        if (stamp.Borders != borders || stamp.Opinion != peeked.Opinion || stamp.Stance != peeked.Stance) return result;
        if (peeked.Stance == DiplomaticStance.AtWar)
        {
            result.Unchanged = true;
            return result;
        }
        result.Unchanged = !WeeklyPassWouldWriteSinglePass(campaign, nation, other, peeked, borders, myStrength, identityOn, stamp, ref result);
        return result;
    }

    internal static bool CommitWeeklyPair(Campaign campaign, WeeklyScanMemory memory, Nation nation, Nation other,
        float myStrength, bool identityOn, in WeeklyPairPrecheck pre, out DiplomaticRelation relation)
    {
        relation = null;
        if (campaign is null || nation is null || other is null || nation.Id == other.Id) return false;
        if (pre.Unchanged)
        {
            relation = pre.Peeked;
            return false;
        }
        var borders = pre.Borders;
        relation = pre.Awake ? pre.Peeked : CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
        if (relation is null) return false;
        if (relation.Stance == DiplomaticStance.AtWar)
        {
            WeeklyAdditives(campaign, relation, out var warPolitics, out var warIdentity, out var warSocial, out var warClaims, out var warGeopolitics);
            RememberWeeklyPair(memory, nation.Id, other.Id, borders, relation, warPolitics, warIdentity, warSocial, warClaims, warGeopolitics);
            return true;
        }

        if (borders)
            OpinionSystem.Remember(campaign, relation, "shared_border", 1f, settle: false);
        else OpinionSystem.Forget(campaign, relation, "shared_border", settle: false);
        var isAllied = relation.Stance == DiplomaticStance.Allied;
        if (isAllied)
            OpinionSystem.Remember(campaign, relation, "alliance", 1f, settle: false);
        else OpinionSystem.Forget(campaign, relation, "alliance", settle: false);
        var faith = pre.HasComputed ? pre.Faith : (identityOn && StateIdentity.ShareDeclaredFaith(nation, other));
        if (faith)
            OpinionSystem.Remember(campaign, relation, "shared_state_faith", 1f, settle: false);
        else OpinionSystem.Forget(campaign, relation, "shared_state_faith", settle: false);

        float strength, fear;
        if (pre.HasComputed && !pre.AssessmentDue)
        {
            strength = pre.Strength;
            fear = pre.Fear;
        }
        else
        {
            strength = CampaignIntel.EstimatedStrength(campaign, nation.Id, other) / myStrength;
            var reach = borders ? 1f : OpinionWitnesses.FearReach(campaign, nation, other);
            if (isAllied) reach *= 0.25f;
            fear = M.Clamp(strength - 1f, 0f, 2f) * reach;
        }
        if (fear > 0.05f && (borders || pre.Awake || fear >= 0.25f))
            OpinionSystem.Remember(campaign, relation, "fear_of_strength", fear, settle: false);
        else if (strength >= 0.95f || (!borders && !pre.Awake && fear < 0.25f))
            OpinionSystem.Forget(campaign, relation, "fear_of_strength", settle: false);
        OpinionSystem.Settle(campaign, relation, out var politics, out var identity, out var social, out var claims, out var geopolitics);
        CampaignDiplomacy.RefreshExpiredTruce(campaign, relation);
        RememberWeeklyPair(memory, nation.Id, other.Id, borders, relation, politics, identity, social, claims, geopolitics);
        return true;
    }

    internal static bool ReviewWeeklyPair(Campaign campaign, WeeklyScanMemory memory, Nation nation, Nation other, bool borders,
        float myStrength, bool identityOn, out DiplomaticRelation relation)
    {
        var pre = PrecheckWeeklyPair(campaign, memory, nation, other, borders, myStrength, identityOn);
        return CommitWeeklyPair(campaign, memory, nation, other, myStrength, identityOn, pre, out relation);
    }

    static void RememberWeeklyPair(WeeklyScanMemory memory, int a, int b, bool borders, DiplomaticRelation relation,
        float politics, float identity, float social, float claims, float geopolitics)
    {
        memory.Stamps[WeeklyPairKey(a, b)] = new WeeklyStamp
        {
            Borders = borders,
            Opinion = relation.Opinion,
            Stance = relation.Stance,
            Politics = politics,
            Identity = identity,
            Social = social,
            Claims = claims,
            Geopolitics = geopolitics
        };
        memory.Walks++;
    }

    /// <summary>
    /// True when walking the pair would change a saved number: the opinion, a standing
    /// reason, a fading grudge, or the strength estimate. Scans <c>relation.Reasons</c> once
    /// instead of five separate passes.
    /// </summary>
    static bool WeeklyPassWouldWrite(Campaign campaign, Nation nation, Nation other, DiplomaticRelation relation,
        bool borders, float myStrength, bool identityOn, in WeeklyStamp stamp)
    {
        var dummy = new WeeklyPairPrecheck { Borders = borders };
        return WeeklyPassWouldWriteSinglePass(campaign, nation, other, relation, borders, myStrength, identityOn, stamp, ref dummy);
    }

    static bool WeeklyPassWouldWriteSinglePass(Campaign campaign, Nation nation, Nation other, DiplomaticRelation relation,
        bool borders, float myStrength, bool identityOn, in WeeklyStamp stamp, ref WeeklyPairPrecheck pre)
    {
        if (relation.Stance == DiplomaticStance.AtWar) return false;
        var today = campaign.ElapsedDays;
        if (relation.Stance == DiplomaticStance.Truce && today >= relation.TruceUntil) return true;
        var allied = relation.Stance == DiplomaticStance.Allied;
        var faith = identityOn && StateIdentity.ShareDeclaredFaith(nation, other);
        var assessmentDue = WeeklyAssessmentDue(campaign, nation.Id, other);

        // Single pass over relation.Reasons to locate all 4 standing reasons AND check expiry/decay.
        OpinionReason heldBorder = null, heldAlliance = null, heldFaith = null, heldFear = null;
        var reasons = relation.Reasons;
        var anyDecayOrExpiry = false;
        for (var i = 0; i < reasons.Count; i++)
        {
            var r = reasons[i];
            var id = r.Id;
            if (heldBorder is null && string.Equals(id, "shared_border", System.StringComparison.OrdinalIgnoreCase)) heldBorder = r;
            else if (heldAlliance is null && string.Equals(id, "alliance", System.StringComparison.OrdinalIgnoreCase)) heldAlliance = r;
            else if (heldFaith is null && string.Equals(id, "shared_state_faith", System.StringComparison.OrdinalIgnoreCase)) heldFaith = r;
            else if (heldFear is null && string.Equals(id, "fear_of_strength", System.StringComparison.OrdinalIgnoreCase)) heldFear = r;

            if (!anyDecayOrExpiry && r.ExpiresDay > 0)
            {
                if (OpinionCatalog.Find(id) is { } def)
                {
                    if (r.ExpiresDay <= today || (def.Decays && def.Days > 0))
                        anyDecayOrExpiry = true;
                }
            }
        }

        if (StandingReasonWouldWrite("shared_border", heldBorder, borders, 1f)) return true;
        if (StandingReasonWouldWrite("alliance", heldAlliance, allied, 1f)) return true;
        if (StandingReasonWouldWrite("shared_state_faith", heldFaith, faith, 1f)) return true;
        if (assessmentDue)
        {
            pre.Faith = faith;
            pre.AssessmentDue = true;
            pre.HasComputed = true;
            return true;
        }

        var strength = CampaignIntel.EstimatedStrength(campaign, nation.Id, other, refreshAssessment: false) / myStrength;
        var reach = borders ? 1f : OpinionWitnesses.FearReach(campaign, nation, other);
        if (allied) reach *= 0.25f;
        var fear = M.Clamp(strength - 1f, 0f, 2f) * reach;
        pre.Faith = faith;
        pre.AssessmentDue = false;
        pre.Strength = strength;
        pre.Fear = fear;
        pre.HasComputed = true;

        if (fear > 0.05f)
        {
            if (StandingReasonWouldWrite("fear_of_strength", heldFear, true, fear)) return true;
        }
        else if (strength >= 0.95f && heldFear is not null)
            return true;

        if (anyDecayOrExpiry) return true;

        WeeklyAdditives(campaign, relation, out var politics, out var identity, out var social, out var claims, out var geopolitics);
        return stamp.Politics != politics || stamp.Identity != identity || stamp.Social != social ||
               stamp.Claims != claims || stamp.Geopolitics != geopolitics;
    }

    static bool StandingReasonWouldWrite(string id, OpinionReason held, bool present, float scale)
    {
        if (!present) return held is not null;
        if (held is null || held.Scale != scale) return true;
        return OpinionCatalog.Find(id) is { MaxStacks: > 1 };
    }

    static bool WeeklyAssessmentDue(Campaign campaign, int viewerId, Nation target)
    {
        if (target is null || CampaignIntel.CanInspect(viewerId, target.Id)) return false;
        if (IntelNetworks.Strength(campaign, viewerId, target.Id) >= campaign.Defines.IntelInspectStrength) return false;
        var every = System.Math.Max(1, campaign.Defines.IntelAssessmentDays);
        return target.AssessedDay < 0 || campaign.ElapsedDays < target.AssessedDay ||
               campaign.ElapsedDays - target.AssessedDay >= every;
    }

    static void WeeklyAdditives(Campaign campaign, DiplomaticRelation relation,
        out float politics, out float identity, out float social, out float claims, out float geopolitics)
    {
        var left = campaign.NationById(relation.A);
        var right = campaign.NationById(relation.B);
        politics = PoliticsCore.Affinity(campaign, left, right);
        identity = StateIdentity.Affinity(campaign, left, right);
        social = SocialMemory.OpinionEffect(campaign, left, right);
        OpinionSystem.StandingClaimAndCycle(campaign, left, right, out claims, out geopolitics);
    }

    /// <summary>
    /// The friendly move is not repeated when the pair is the one last walked and none of
    /// its branches can write. A warm pair, a pair at a threshold, or a pair that has
    /// never been walked is still asked.
    /// </summary>
    internal static bool SkipUnchangedFriendly(Campaign campaign, Nation nation, Nation friend, bool borders)
    {
        var bond = CampaignDiplomacy.Peek(campaign, nation.Id, friend.Id);
        if (bond is null || nation is null || friend is null) return false;
        if (!SameWeeklyPair(campaign, nation.Id, friend.Id, borders, bond.Opinion, bond.Stance)) return false;
        return !FriendlyMoveCanWrite(campaign, nation, friend, bond, borders);
    }

    static bool FriendlyMoveCanWrite(Campaign campaign, Nation nation, Nation friend, DiplomaticRelation relation, bool borders)
    {
        if (relation is null || relation.Stance == DiplomaticStance.AtWar) return false;
        var opinion = relation.Opinion;
        var defines = campaign.Defines;
        if (opinion >= 0f) return true;
        if (relation.EmbargoBy(nation.Id) && opinion >= defines.AiEmbargoReconcileOpinion) return true;
        if (relation.Stance == DiplomaticStance.Rival && opinion >= 35f &&
            campaign.ElapsedDays - relation.LastActionDay >= 14 && nation.Treasury >= 0.1f)
            return true;
        var reach = M.Clamp(AiStrategyCatalog.Of(campaign, nation).Sociability, 0.25f, 2.5f);
        var tradeThreshold = defines.AiTradeThreshold / reach;
        var pactThreshold = defines.AiPactThreshold / reach;
        var allianceThreshold = defines.AiAllianceThreshold / reach;
        if (GeopoliticalCycle.IsDetenteEra(campaign))
        {
            tradeThreshold *= 0.88f;
            pactThreshold *= 0.90f;
        }
        else if (GeopoliticalCycle.IsFrictionEra(campaign))
        {
            tradeThreshold *= 1.10f;
            pactThreshold *= 1.10f;
        }
        if (defines.DiplomaticMissionsEnabled && nation.PoliticalPoints >= 18f && nation.Treasury >= 0.2f &&
            opinion < allianceThreshold && DiplomaticOutreach.Of(relation, nation.Id) is null &&
            (nation.TradeDependenceOn(friend.Id) > 0.02f || borders || opinion >= 10f))
            return true;
        if (opinion >= tradeThreshold || opinion >= pactThreshold) return true;
        if (relation.Stance != DiplomaticStance.Allied && opinion >= allianceThreshold) return true;
        if (relation.Stance == DiplomaticStance.Allied || opinion >= defines.AiAidFriendshipThreshold) return true;
        if (opinion >= 15f && relation.Stance != DiplomaticStance.Allied) return true;
        if (!defines.DiplomaticMissionsEnabled && relation.Stance == DiplomaticStance.Neutral &&
            opinion >= 35f && campaign.ElapsedDays - relation.LastActionDay >= 14 && nation.Treasury >= 0.1f)
            return true;
        return false;
    }
}
