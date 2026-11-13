using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public static partial class AiStatecraft
{
    /// <summary>
    /// One campaign's memory of the weekly diplomacy pass. A second campaign in the same
    /// process has its own: the stamp is not saved and is not part of the world.
    /// </summary>
    sealed class WeeklyScanMemory
    {
        internal int Walks;
        internal readonly Dictionary<long, WeeklyStamp> Stamps = new();
    }

    struct WeeklyStamp
    {
        internal bool Borders;
        internal float Opinion;
        internal DiplomaticStance Stance;
        internal float Politics;
        internal float Identity;
        internal float Social;
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
        if (peeked is null) return false;
        if (!SameWeeklyPair(campaign, nation.Id, other.Id, borders, peeked.Opinion, peeked.Stance)) return false;
        return !WeeklyPassWouldWrite(campaign, nation, other, peeked, borders, myStrength, identityOn);
    }

    /// <summary>
    /// The weekly opinion pass for one pair. Returns true when the pair was walked.
    /// A pair whose border, opinion and stance are unchanged, and whose pass would write
    /// the same opinion and the same strength estimate, is not walked. The check reads.
    /// It does not open a relation; the walk opens one only when it is about to write,
    /// which is the same moment the pass used to call <see cref="CampaignDiplomacy.Get"/>.
    /// </summary>
    internal static bool ReviewWeeklyPair(Campaign campaign, Nation nation, Nation other, bool borders,
        float myStrength, bool identityOn, out DiplomaticRelation relation)
    {
        relation = null;
        if (campaign is null || nation is null || other is null || nation.Id == other.Id) return false;
        if (UnchangedWeeklyPair(campaign, nation, other, borders, myStrength, identityOn))
        {
            relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            return false;
        }

        relation = CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
        if (relation is null) return false;
        if (relation.Stance == DiplomaticStance.AtWar)
        {
            RememberWeeklyPair(campaign, nation.Id, other.Id, borders, relation);
            return true;
        }

        // Where this relationship naturally sits, written as the same named reasons a
        // war or a denunciation is written in - so the standing part of an opinion can
        // be explained to the player and retuned by a mod, rather than being five
        // numbers in Defines.cs that nothing on any screen accounts for.
        if (borders)
            OpinionSystem.Remember(campaign, relation, "shared_border", 1f, settle: false);
        else OpinionSystem.Forget(campaign, relation, "shared_border", settle: false);
        var isAllied = relation.Stance == DiplomaticStance.Allied;
        if (isAllied)
            OpinionSystem.Remember(campaign, relation, "alliance", 1f, settle: false);
        else OpinionSystem.Forget(campaign, relation, "alliance", settle: false);
        if (identityOn && StateIdentity.ShareDeclaredFaith(nation, other))
            OpinionSystem.Remember(campaign, relation, "shared_state_faith", 1f, settle: false);
        else OpinionSystem.Forget(campaign, relation, "shared_state_faith", settle: false);

        var strength = CampaignIntel.EstimatedStrength(campaign, nation.Id, other) / myStrength;
        // And only as far as the stronger one can reach. Fear of strength was the same at
        // every distance, so the strongest country in the world was feared at full weight
        // by every government in its circle - France and Brazil held minus thirty-two
        // against China for being large - and that one line was most of everything the
        // world held against anybody. A neighbour fears it in full, a region less, the far
        // side of the world hardly at all; an ally a quarter as much, because a strong
        // friend is a worry and not a threat.
        var reach = OpinionWitnesses.FearReach(campaign, nation, other);
        if (isAllied) reach *= 0.25f;
        var fear = M.Clamp(strength - 1f, 0f, 2f) * reach;
        // The pair shares one relation, and only the weaker of the two is afraid. The
        // stronger one leaves the entry alone: when each wrote its own view in turn, the
        // weaker remembered the fear and the stronger forgot it, every week, and the
        // reason flickered on and off the diplomacy page.
        if (fear > 0.05f) OpinionSystem.Remember(campaign, relation, "fear_of_strength", fear, settle: false);
        else if (strength >= 0.95f) OpinionSystem.Forget(campaign, relation, "fear_of_strength", settle: false);
        OpinionSystem.Settle(campaign, relation);
        RememberWeeklyPair(campaign, nation.Id, other.Id, borders, relation);
        return true;
    }

    static void RememberWeeklyPair(Campaign campaign, int a, int b, bool borders, DiplomaticRelation relation)
    {
        var memory = WeeklyScanOf(campaign);
        WeeklyAdditives(campaign, relation, out var politics, out var identity, out var social);
        memory.Stamps[WeeklyPairKey(a, b)] = new WeeklyStamp
        {
            Borders = borders,
            Opinion = relation.Opinion,
            Stance = relation.Stance,
            Politics = politics,
            Identity = identity,
            Social = social
        };
        memory.Walks++;
    }

    /// <summary>
    /// True when walking the pair would change a saved number: the opinion, a standing
    /// reason, a fading grudge, or the strength estimate. The three remembered facts can
    /// still match while one of those is due.
    /// </summary>
    static bool WeeklyPassWouldWrite(Campaign campaign, Nation nation, Nation other, DiplomaticRelation relation,
        bool borders, float myStrength, bool identityOn)
    {
        if (relation.Stance == DiplomaticStance.AtWar) return false;
        if (StandingWouldWrite(relation, "shared_border", borders, 1f)) return true;
        var allied = relation.Stance == DiplomaticStance.Allied;
        if (StandingWouldWrite(relation, "alliance", allied, 1f)) return true;
        var faith = identityOn && StateIdentity.ShareDeclaredFaith(nation, other);
        if (StandingWouldWrite(relation, "shared_state_faith", faith, 1f)) return true;
        if (WeeklyAssessmentDue(campaign, nation.Id, other)) return true;

        var strength = CampaignIntel.EstimatedStrength(campaign, nation.Id, other, refreshAssessment: false) / myStrength;
        var reach = OpinionWitnesses.FearReach(campaign, nation, other);
        if (allied) reach *= 0.25f;
        var fear = M.Clamp(strength - 1f, 0f, 2f) * reach;
        if (fear > 0.05f)
        {
            if (StandingWouldWrite(relation, "fear_of_strength", true, fear)) return true;
        }
        else if (strength >= 0.95f && OpinionSystem.Has(relation, "fear_of_strength"))
            return true;

        for (var index = 0; index < relation.Reasons.Count; index++)
        {
            var reason = relation.Reasons[index];
            if (OpinionCatalog.Find(reason.Id) is not { } definition) continue;
            if (reason.ExpiresDay > 0 && reason.ExpiresDay <= campaign.ElapsedDays) return true;
            if (definition.Decays && definition.Days > 0 && reason.ExpiresDay > 0) return true;
        }

        if (!WeeklyScans.TryGetValue(campaign, out var memory) ||
            !memory.Stamps.TryGetValue(WeeklyPairKey(nation.Id, other.Id), out var stamp))
            return true;
        WeeklyAdditives(campaign, relation, out var politics, out var identity, out var social);
        return stamp.Politics != politics || stamp.Identity != identity || stamp.Social != social;
    }

    static bool StandingWouldWrite(DiplomaticRelation relation, string id, bool present, float scale)
    {
        OpinionReason held = null;
        for (var index = 0; index < relation.Reasons.Count; index++)
        {
            if (!string.Equals(relation.Reasons[index].Id, id, System.StringComparison.OrdinalIgnoreCase)) continue;
            held = relation.Reasons[index];
            break;
        }
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

    static void WeeklyAdditives(Campaign campaign, DiplomaticRelation relation, out float politics, out float identity, out float social)
    {
        var left = campaign.NationById(relation.A);
        var right = campaign.NationById(relation.B);
        politics = PoliticsCore.Affinity(campaign, left, right);
        identity = StateIdentity.Affinity(campaign, left, right);
        social = SocialMemory.OpinionEffect(campaign, left, right);
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
