using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

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

    internal static int ComputeStandingMultiMask()
    {
        var mask = 0;
        if (OpinionCatalog.Find("shared_border") is { MaxStacks: > 1 }) mask |= 1;
        if (OpinionCatalog.Find("alliance") is { MaxStacks: > 1 }) mask |= 2;
        if (OpinionCatalog.Find("shared_state_faith") is { MaxStacks: > 1 }) mask |= 4;
        if (OpinionCatalog.Find("fear_of_strength") is { MaxStacks: > 1 }) mask |= 8;
        return mask;
    }

    internal static WeeklyPairPrecheck PrecheckWeeklyPair(Campaign campaign, WeeklyScanMemory memory,
        Nation nation, Nation other, bool borders, float myStrength, bool identityOn) =>
        PrecheckWeeklyPair(campaign, memory, nation, other, borders, myStrength, identityOn,
            campaign?.FeatureEnabled("intelligence") ?? false, -1);

    internal static WeeklyPairPrecheck PrecheckWeeklyPair(Campaign campaign, WeeklyScanMemory memory,
        Nation nation, Nation other, bool borders, float myStrength, bool identityOn,
        bool intelligenceOn, int standingMultiMask)
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
        if (!IsPairDueThisCycle(campaign, nation, other, peeked, bordersKnown: true, borders,
                respectStatecraftCalmCadence: TablesOf(campaign).InStatecraftTick))
        {
            result.Unchanged = true;
            return result;
        }
        if (peeked is null || memory is null) return result;
        if (!memory.Stamps.TryGetValue(WeeklyPairKey(nation.Id, other.Id), out var stamp)) return result;
        if (stamp.Borders != borders || stamp.Opinion != peeked.Opinion || stamp.Stance != peeked.Stance) return result;
        if (peeked.Stance == DiplomaticStance.AtWar)
        {
            result.Unchanged = true;
            return result;
        }
        result.Unchanged = !WeeklyPassWouldWriteSinglePass(campaign, nation, other, peeked, borders, myStrength,
            identityOn, intelligenceOn, standingMultiMask, stamp, ref result);
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
            WeeklyAdditives(campaign, nation, other, relation, out var warPolitics, out var warIdentity, out var warSocial, out var warClaims, out var warGeopolitics);
            RememberWeeklyPair(memory, nation.Id, other.Id, borders, relation, warPolitics, warIdentity, warSocial, warClaims, warGeopolitics);
            return true;
        }

        var reasons = relation.Reasons;
        bool hasBorder = false, hasAlliance = false, hasFaith = false, hasFear = false;
        for (var ri = 0; ri < reasons.Count; ri++)
        {
            var id = reasons[ri].Id;
            if (!hasBorder && string.Equals(id, "shared_border", System.StringComparison.OrdinalIgnoreCase)) hasBorder = true;
            else if (!hasAlliance && string.Equals(id, "alliance", System.StringComparison.OrdinalIgnoreCase)) hasAlliance = true;
            else if (!hasFaith && string.Equals(id, "shared_state_faith", System.StringComparison.OrdinalIgnoreCase)) hasFaith = true;
            else if (!hasFear && string.Equals(id, "fear_of_strength", System.StringComparison.OrdinalIgnoreCase)) hasFear = true;
        }

        if (borders)
            OpinionSystem.Remember(campaign, relation, "shared_border", 1f, settle: false);
        else if (hasBorder)
            OpinionSystem.Forget(campaign, relation, "shared_border", settle: false);
        var isAllied = relation.Stance == DiplomaticStance.Allied;
        if (isAllied)
            OpinionSystem.Remember(campaign, relation, "alliance", 1f, settle: false);
        else if (hasAlliance)
            OpinionSystem.Forget(campaign, relation, "alliance", settle: false);
        var faith = pre.HasComputed ? pre.Faith : (identityOn && StateIdentity.ShareDeclaredFaith(nation, other));
        if (faith)
            OpinionSystem.Remember(campaign, relation, "shared_state_faith", 1f, settle: false);
        else if (hasFaith)
            OpinionSystem.Forget(campaign, relation, "shared_state_faith", settle: false);

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
        else if (hasFear && (strength >= 0.95f || (!borders && !pre.Awake && fear < 0.25f)))
            OpinionSystem.Forget(campaign, relation, "fear_of_strength", settle: false);
        var explain = nation.IsHuman || other.IsHuman;
        OpinionSystem.Settle(campaign, relation, explain, out var politics, out var identity, out var social, out var claims, out var geopolitics);
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
        return WeeklyPassWouldWriteSinglePass(campaign, nation, other, relation, borders, myStrength, identityOn,
            campaign.FeatureEnabled("intelligence"), -1, stamp, ref dummy);
    }

    static bool WeeklyPassWouldWriteSinglePass(Campaign campaign, Nation nation, Nation other, DiplomaticRelation relation,
        bool borders, float myStrength, bool identityOn, bool intelligenceOn, int standingMultiMask,
        in WeeklyStamp stamp, ref WeeklyPairPrecheck pre)
    {
        if (relation.Stance == DiplomaticStance.AtWar) return false;
        var today = campaign.ElapsedDays;
        if (relation.Stance == DiplomaticStance.Truce && today >= relation.TruceUntil) return true;
        var allied = relation.Stance == DiplomaticStance.Allied;
        var faith = identityOn && StateIdentity.ShareDeclaredFaith(nation, other);
        var assessmentDue = WeeklyAssessmentDue(campaign, nation, other, intelligenceOn);

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
                if (r.ExpiresDay <= today)
                {
                    if (OpinionCatalog.Find(id) is not null)
                        anyDecayOrExpiry = true;
                }
                else if (OpinionCatalog.Find(id) is { Decays: true, Days: > 0 })
                {
                    anyDecayOrExpiry = true;
                }
            }
        }

        if (StandingReasonWouldWrite("shared_border", heldBorder, borders, 1f, 1, standingMultiMask)) return true;
        if (StandingReasonWouldWrite("alliance", heldAlliance, allied, 1f, 2, standingMultiMask)) return true;
        if (StandingReasonWouldWrite("shared_state_faith", heldFaith, faith, 1f, 4, standingMultiMask)) return true;
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
            if (StandingReasonWouldWrite("fear_of_strength", heldFear, true, fear, 8, standingMultiMask)) return true;
        }
        else if (strength >= 0.95f && heldFear is not null)
            return true;

        if (anyDecayOrExpiry) return true;

        WeeklyAdditives(campaign, nation, other, relation, out var politics, out var identity, out var social, out var claims, out var geopolitics);
        return stamp.Politics != politics || stamp.Identity != identity || stamp.Social != social ||
               stamp.Claims != claims || stamp.Geopolitics != geopolitics;
    }

    static bool StandingReasonWouldWrite(string id, OpinionReason held, bool present, float scale, int bit, int standingMultiMask)
    {
        if (!present) return held is not null;
        if (held is null || held.Scale != scale) return true;
        return standingMultiMask >= 0 ? (standingMultiMask & bit) != 0 : OpinionCatalog.Find(id) is { MaxStacks: > 1 };
    }

    static bool WeeklyAssessmentDue(Campaign campaign, Nation viewer, Nation target, bool intelligenceOn)
    {
        if (viewer is null || target is null || CampaignIntel.CanInspect(viewer.Id, target.Id)) return false;
        var network = intelligenceOn ? viewer.IntelNetworks.GetValueOrDefault(target.Id) : 0f;
        if (network >= campaign.Defines.IntelInspectStrength) return false;
        var every = System.Math.Max(1, campaign.Defines.IntelAssessmentDays);
        return target.AssessedDay < 0 || campaign.ElapsedDays < target.AssessedDay ||
               campaign.ElapsedDays - target.AssessedDay >= every;
    }

    static void WeeklyAdditives(Campaign campaign, Nation nation, Nation other, DiplomaticRelation relation,
        out float politics, out float identity, out float social, out float claims, out float geopolitics)
    {
        var left = relation.A == nation.Id ? nation : relation.A == other.Id ? other : campaign.NationById(relation.A);
        var right = relation.B == other.Id ? other : relation.B == nation.Id ? nation : campaign.NationById(relation.B);
        politics = PoliticsCore.Affinity(campaign, left, right);
        identity = StateIdentity.Affinity(campaign, left, right);
        social = SocialMemory.OpinionEffect(campaign, left, right);
        OpinionSystem.StandingClaimAndCycle(campaign, left, right, out claims, out geopolitics);
    }

    private const float LodMajorGdpThreshold = 400f;

    private readonly record struct StaticHarbourEntry(long CellKey, int Province, int Basin, float Lat, float Lon, float WrappedLon);

    private struct StaticSeaCell
    {
        public int Basin;
        public float MinLat;
        public float MaxLat;
        public float MinLon;
        public float MaxLon;
        public int HarbourStart;
        public int HarbourEnd;
        public bool SelfAllConnected;
    }

    private sealed class StaticSeaGraph
    {
        public readonly int ThresholdBits;
        public readonly int HarbourCount;
        public readonly int[] Provinces;
        public readonly StaticSeaCell[] Cells;
        public readonly int[] InteriorCellA;
        public readonly int[] InteriorCellB;
        public readonly int[] BoundaryHarbourA;
        public readonly int[] BoundaryHarbourB;

        public StaticSeaGraph(int thresholdBits, int harbourCount, int[] provinces, StaticSeaCell[] cells,
            int[] interiorCellA, int[] interiorCellB, int[] boundaryHarbourA, int[] boundaryHarbourB)
        {
            ThresholdBits = thresholdBits;
            HarbourCount = harbourCount;
            Provinces = provinces;
            Cells = cells;
            InteriorCellA = interiorCellA;
            InteriorCellB = interiorCellB;
            BoundaryHarbourA = boundaryHarbourA;
            BoundaryHarbourB = boundaryHarbourB;
        }
    }

    private sealed class StaticSeaGraphCache
    {
        public readonly object Sync = new();
        public readonly List<StaticSeaGraph> Entries = new(2);
    }

    private static readonly ConditionalWeakTable<SeaLanes.HarbourTable, StaticSeaGraphCache> StaticSeaGraphs = new();

    private sealed class DiplomaticLodState
    {
        public readonly object Sync = new();
        public DiplomaticLodSnapshot Snapshot;
        public int GpDay = int.MinValue;
        public int GpNationsCount = -1;
        public HashSet<int> GreatPowers;
        public int WarRevision = int.MinValue;
        public int WarCount = -1;
        public HashSet<int> NationsAtWar;
        public int OrgStamp = int.MinValue;
        public bool OrgEnabled;
        public HashSet<long> DefencePartners;
        public int PlayerSphereDay = int.MinValue;
        public int PlayerSphereOwnershipRevision = -1;
        public int PlayerSphereNationsCount = -1;
        public HashSet<int> PlayerSphere;
        public ProvinceTopology SeaMap;
        public int SeaOwnershipRevision = -1;
        public int SeaPassageRevision = -1;
        public int SeaNationsCount = -1;
        public float SeaThreshold = -1f;
        public HashSet<long> SeaZoneNeighbors;
        public StaticSeaGraph PrevSeaGraph;
        public int[] PrevHarbourNationIds;
        public int[] CurHarbourNationIds;
        public int[] CurHarbourDense;
        public int[] MapToDense;
        public int MaxMapIndexSeen = -1;
        public int[] DenseNationIds = Array.Empty<int>();
        public int[] CellSingleDense = Array.Empty<int>();
        public int[] CellOwnerStart = Array.Empty<int>();
        public int[] CellOwners = Array.Empty<int>();
        public ulong[] SeenMatrix = Array.Empty<ulong>();
    }

    internal sealed class DiplomaticLodSnapshot
    {
        public int ElapsedDays;
        public int OwnershipRevision;
        public int WarRevision;
        public int WarCount;
        public int NationsCount;
        public int StrategicRevision;
        public int AllianceRevision;
        public int OrgStamp;
        public bool OrgEnabled;
        public int SeaPassageRevision;
        public float SeaThreshold;
        public string LodMode = "balanced";
        public bool ObserverMode;
        public ProvinceTopology SeaMap;
        public HashSet<int> GreatPowers;
        public HashSet<int> NationsAtWar;
        public HashSet<long> DefencePartners;
        public HashSet<long> SeaZoneNeighbors;
        public HashSet<int> PlayerSphere;

        public bool MatchesFast(Campaign campaign, int warCount, float seaThreshold, string lodMode) =>
            campaign.ElapsedDays == ElapsedDays &&
            campaign.OwnershipRevision == OwnershipRevision &&
            campaign.WarRevision == WarRevision &&
            warCount == WarCount &&
            campaign.Nations.Count == NationsCount &&
            campaign.StrategicRevision == StrategicRevision &&
            campaign.AllianceRevision == AllianceRevision &&
            SeaPassageCatalog.Revision == SeaPassageRevision &&
            ReferenceEquals(campaign.ProvinceMap, SeaMap) &&
            ObserverMode == campaign.ObserverMode &&
            string.Equals(LodMode, lodMode, StringComparison.Ordinal) &&
            MathF.Abs(seaThreshold - SeaThreshold) <= 0.0001f;

        public bool Matches(Campaign campaign, int orgStamp, bool orgEnabled, int warCount, float seaThreshold, string lodMode) =>
            campaign.ElapsedDays == ElapsedDays &&
            campaign.OwnershipRevision == OwnershipRevision &&
            campaign.WarRevision == WarRevision &&
            warCount == WarCount &&
            campaign.Nations.Count == NationsCount &&
            orgStamp == OrgStamp &&
            orgEnabled == OrgEnabled &&
            SeaPassageCatalog.Revision == SeaPassageRevision &&
            ReferenceEquals(campaign.ProvinceMap, SeaMap) &&
            ObserverMode == campaign.ObserverMode &&
            string.Equals(LodMode, lodMode, StringComparison.Ordinal) &&
            MathF.Abs(seaThreshold - SeaThreshold) <= 0.0001f;
    }

    private static readonly ConditionalWeakTable<Campaign, DiplomaticLodState> LodStates = new();

    private static int ComputeOrganizationsStamp(Campaign campaign)
    {
        var orgs = campaign.Organizations;
        var stamp = orgs.Count * 1000003;
        for (var i = 0; i < orgs.Count; i++)
        {
            var o = orgs[i];
            stamp ^= (o.Members.Count * 31) + (o.Dissolved ? 1 : 0) + o.Cohesion.GetHashCode();
        }
        return stamp;
    }

    private static float ComputeSeaZoneThreshold(Campaign campaign) =>
        MathF.Max(1f, MathF.Max(campaign.Defines?.AiInvasionSearchDegrees ?? 15f,
            (campaign.Defines?.WitnessNearKm ?? 1500f) / 111.32f));

    private static StaticSeaGraph GetStaticSeaGraph(SeaLanes.HarbourTable harbours, float seaThreshold)
    {
        var thresholdBits = BitConverter.SingleToInt32Bits(seaThreshold);
        var cache = StaticSeaGraphs.GetOrCreateValue(harbours);
        lock (cache.Sync)
        {
            for (var i = 0; i < cache.Entries.Count; i++)
            {
                if (cache.Entries[i].ThresholdBits == thresholdBits)
                    return cache.Entries[i];
            }

            var built = BuildStaticSeaGraph(harbours, seaThreshold, thresholdBits);
            cache.Entries.Add(built);
            return built;
        }
    }

    private static StaticSeaGraph BuildStaticSeaGraph(SeaLanes.HarbourTable harbours, float seaThreshold, int thresholdBits)
    {
        const float CellDeg = 2f;
        const int NumLonBins = 180;
        var count = harbours.Province.Length;
        var entries = new List<StaticHarbourEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var basin = harbours.Basin[i];
            if (basin < 0) continue;
            var province = harbours.Province[i];
            if (province <= 0) continue;
            var lat = harbours.Latitude[i];
            var lon = harbours.Longitude[i];
            var wLon = lon;
            while (wLon > 180f) wLon -= 360f;
            while (wLon < -180f) wLon += 360f;
            var latBin = Math.Clamp((int)MathF.Floor((lat + 90f) / CellDeg), 0, 90);
            var lonBin = Math.Clamp((int)MathF.Floor((wLon + 180f) / CellDeg), 0, NumLonBins - 1);
            var cellKey = ((long)basin << 32) | (uint)((latBin << 8) | lonBin);
            entries.Add(new StaticHarbourEntry(cellKey, province, basin, lat, lon, wLon));
        }

        var h = entries.Count;
        if (h < 2)
        {
            var smallProvs = h == 1 ? new[] { entries[0].Province } : Array.Empty<int>();
            return new StaticSeaGraph(thresholdBits, h, smallProvs, Array.Empty<StaticSeaCell>(),
                Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
        }

        entries.Sort(static (left, right) =>
        {
            var cmp = left.CellKey.CompareTo(right.CellKey);
            return cmp != 0 ? cmp : left.Province.CompareTo(right.Province);
        });

        var provinces = new int[h];
        var lats = new float[h];
        var lons = new float[h];
        var cellsList = new List<StaticSeaCell>(Math.Max(64, h / 4));

        var curStart = 0;
        var curKey = entries[0].CellKey;
        var curBasin = entries[0].Basin;
        float minLat = entries[0].Lat, maxLat = entries[0].Lat;
        float minLon = entries[0].WrappedLon, maxLon = entries[0].WrappedLon;

        for (var i = 0; i < h; i++)
        {
            var e = entries[i];
            provinces[i] = e.Province;
            lats[i] = e.Lat;
            lons[i] = e.Lon;

            if (i > 0 && e.CellKey != curKey)
            {
                cellsList.Add(new StaticSeaCell
                {
                    Basin = curBasin,
                    MinLat = minLat,
                    MaxLat = maxLat,
                    MinLon = minLon,
                    MaxLon = maxLon,
                    HarbourStart = curStart,
                    HarbourEnd = i
                });
                curStart = i;
                curKey = e.CellKey;
                curBasin = e.Basin;
                minLat = maxLat = e.Lat;
                minLon = maxLon = e.WrappedLon;
            }
            else if (i > 0)
            {
                if (e.Lat < minLat) minLat = e.Lat;
                if (e.Lat > maxLat) maxLat = e.Lat;
                if (e.WrappedLon < minLon) minLon = e.WrappedLon;
                if (e.WrappedLon > maxLon) maxLon = e.WrappedLon;
            }
        }
        cellsList.Add(new StaticSeaCell
        {
            Basin = curBasin,
            MinLat = minLat,
            MaxLat = maxLat,
            MinLon = minLon,
            MaxLon = maxLon,
            HarbourStart = curStart,
            HarbourEnd = h
        });

        var cells = cellsList.ToArray();
        Array.Sort(cells, static (left, right) =>
        {
            var cmp = left.Basin.CompareTo(right.Basin);
            if (cmp != 0) return cmp;
            cmp = left.MinLat.CompareTo(right.MinLat);
            return cmp != 0 ? cmp : left.MinLon.CompareTo(right.MinLon);
        });

        var cCount = cells.Length;
        var interiorA = new List<int>(cCount * 8);
        var interiorB = new List<int>(cCount * 8);
        var boundaryA = new List<int>(Math.Max(64, h * 4));
        var boundaryB = new List<int>(Math.Max(64, h * 4));

        var threshLow = MathF.Max(0f, seaThreshold - 1e-3f);
        var threshHigh = seaThreshold + 1e-3f;
        var threshLowSq = threshLow * threshLow;
        var threshHighSq = threshHigh * threshHigh;
        const float DegToRad = MathF.PI / 180f;

        for (var i = 0; i < cCount; i++)
        {
            ref var c1 = ref cells[i];
            var midLonA = 0.5f * (c1.MinLon + c1.MaxLon);
            var halfLonA = 0.5f * (c1.MaxLon - c1.MinLon);

            for (var j = i; j < cCount; j++)
            {
                ref readonly var c2 = ref cells[j];
                if (c2.Basin != c1.Basin || c2.MinLat - c1.MaxLat > threshHigh) break;

                var dLatMin = MathF.Max(0f, MathF.Max(c1.MinLat - c2.MaxLat, c2.MinLat - c1.MaxLat));
                if (dLatMin > threshHigh) continue;

                var rawMid = midLonA - 0.5f * (c2.MinLon + c2.MaxLon);
                while (rawMid > 180f) rawMid -= 360f;
                while (rawMid < -180f) rawMid += 360f;
                var midDiff = MathF.Abs(rawMid);
                var halfSpan = halfLonA + 0.5f * (c2.MaxLon - c2.MinLon);

                var midLatLo = 0.5f * (c1.MinLat + c2.MinLat);
                var midLatHi = 0.5f * (c1.MaxLat + c2.MaxLat);
                var cosMin = MathF.Max(0f, MathF.Cos(MathF.Max(MathF.Abs(midLatLo), MathF.Abs(midLatHi)) * DegToRad) - 1e-4f);

                var dLonMin = MathF.Max(0f, midDiff - halfSpan - 1e-3f);
                var scaledMinLon = dLonMin * cosMin;
                if (scaledMinLon > threshHigh || dLatMin * dLatMin + scaledMinLon * scaledMinLon > threshHighSq)
                    continue;

                var dLatMax = MathF.Max(MathF.Abs(c1.MaxLat - c2.MinLat), MathF.Abs(c2.MaxLat - c1.MinLat));
                if (dLatMax <= threshLow)
                {
                    var dLonMax = MathF.Min(180f, midDiff + halfSpan + 1e-3f);
                    var cosMax = midLatLo <= 0f && midLatHi >= 0f
                        ? 1f
                        : MathF.Min(1f, MathF.Cos(MathF.Min(MathF.Abs(midLatLo), MathF.Abs(midLatHi)) * DegToRad) + 1e-4f);
                    var scaledMaxLon = dLonMax * cosMax;
                    if (scaledMaxLon <= threshLow && dLatMax * dLatMax + scaledMaxLon * scaledMaxLon <= threshLowSq)
                    {
                        if (i == j)
                            c1.SelfAllConnected = true;
                        else
                        {
                            interiorA.Add(i);
                            interiorB.Add(j);
                        }
                        continue;
                    }
                }

                for (var p = c1.HarbourStart; p < c1.HarbourEnd; p++)
                {
                    var qStart = i == j ? p + 1 : c2.HarbourStart;
                    var latP = lats[p];
                    var lonP = lons[p];
                    for (var q = qStart; q < c2.HarbourEnd; q++)
                    {
                        if (NavalOperations.Distance(latP, lonP, lats[q], lons[q]) <= seaThreshold)
                        {
                            boundaryA.Add(p);
                            boundaryB.Add(q);
                        }
                    }
                }
            }
        }

        return new StaticSeaGraph(thresholdBits, h, provinces, cells,
            interiorA.ToArray(), interiorB.ToArray(), boundaryA.ToArray(), boundaryB.ToArray());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordSeaZonePair(int u, int v, int liveCount, int[] denseNationIds, ulong[] seen, HashSet<long> pairs)
    {
        if (seen is not null)
        {
            var a = u < v ? u : v;
            var b = u < v ? v : u;
            var bit = a * liveCount + b;
            var word = bit >> 6;
            var mask = 1UL << (bit & 63);
            if ((seen[word] & mask) != 0) return;
            seen[word] |= mask;
            pairs.Add(WeeklyPairKey(denseNationIds[u], denseNationIds[v]));
        }
        else
        {
            var key = WeeklyPairKey(denseNationIds[u], denseNationIds[v]);
            if (!pairs.Contains(key))
                pairs.Add(key);
        }
    }

    private static HashSet<long> RefreshSeaZoneNeighbors(Campaign campaign, DiplomaticLodState state, float seaThreshold)
    {
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (map is null || controllers is null || map.Width <= 1 || map.Height <= 1 ||
            map.CellToProvince is null || map.CellToProvince.Length <= 1 || map.ProvinceCount <= 0)
        {
            state.PrevSeaGraph = null;
            state.PrevHarbourNationIds = null;
            return state.SeaZoneNeighbors is { Count: 0 } emptyReuse
                ? emptyReuse
                : new HashSet<long>(PairKeyComparer.Instance);
        }

        var harbours = SeaLanes.Harbours(map);
        if (harbours.Province.Length == 0)
        {
            state.PrevSeaGraph = null;
            state.PrevHarbourNationIds = null;
            return state.SeaZoneNeighbors is { Count: 0 } emptyReuse
                ? emptyReuse
                : new HashSet<long>(PairKeyComparer.Instance);
        }

        var graph = GetStaticSeaGraph(harbours, seaThreshold);
        var h = graph.HarbourCount;
        if (h < 2)
        {
            state.PrevSeaGraph = graph;
            state.PrevHarbourNationIds = null;
            return state.SeaZoneNeighbors is { Count: 0 } emptyReuse
                ? emptyReuse
                : new HashSet<long>(PairKeyComparer.Instance);
        }

        const int MapDenseLimit = 4096;
        if (state.MapToDense is null)
        {
            state.MapToDense = new int[MapDenseLimit];
            Array.Fill(state.MapToDense, -1);
            state.MaxMapIndexSeen = -1;
        }
        else if (state.MaxMapIndexSeen >= 0)
        {
            Array.Fill(state.MapToDense, -1, 0, Math.Min(MapDenseLimit, state.MaxMapIndexSeen + 1));
            state.MaxMapIndexSeen = -1;
        }

        var mapToDense = state.MapToDense;
        var denseNationIds = state.DenseNationIds;
        var maxMiSeen = -1;
        var liveCount = 0;
        Dictionary<int, int> overflowToDense = null;

        foreach (var kv in campaign.NationsByMapIndex())
        {
            var owner = kv.Value;
            if (owner.Population <= 0 || kv.Key == 0) continue;
            var dense = liveCount++;
            if (dense >= denseNationIds.Length)
            {
                var nextCap = Math.Max(64, Math.Max(liveCount, denseNationIds.Length * 2));
                Array.Resize(ref denseNationIds, nextCap);
                state.DenseNationIds = denseNationIds;
            }
            denseNationIds[dense] = owner.Id;

            if ((uint)kv.Key < (uint)mapToDense.Length)
            {
                mapToDense[kv.Key] = dense;
                if (kv.Key > maxMiSeen) maxMiSeen = kv.Key;
            }
            else
            {
                (overflowToDense ??= new Dictionary<int, int>())[kv.Key] = dense;
            }
        }
        state.MaxMapIndexSeen = maxMiSeen;

        if (liveCount < 2)
        {
            state.PrevSeaGraph = graph;
            state.PrevHarbourNationIds = null;
            return state.SeaZoneNeighbors is { Count: 0 } emptyReuse
                ? emptyReuse
                : new HashSet<long>(PairKeyComparer.Instance);
        }

        var canReusePrev = state.SeaZoneNeighbors is not null && ReferenceEquals(state.PrevSeaGraph, graph) &&
                           state.PrevHarbourNationIds is not null && state.PrevHarbourNationIds.Length == h;
        if (state.CurHarbourNationIds is null || state.CurHarbourNationIds.Length != h)
            state.CurHarbourNationIds = new int[h];
        if (state.CurHarbourDense is null || state.CurHarbourDense.Length != h)
            state.CurHarbourDense = new int[h];

        var provinces = graph.Provinces;
        var prevOwners = state.PrevHarbourNationIds;
        var curOwners = state.CurHarbourNationIds;
        var curDense = state.CurHarbourDense;
        var anyChanged = !canReusePrev;
        var ctrlLen = controllers.Length;

        for (var k = 0; k < h; k++)
        {
            var province = provinces[k];
            var nationId = -1;
            var dense = -1;
            if (province > 0 && province < ctrlLen)
            {
                var ownerMapIndex = controllers[province];
                if (ownerMapIndex != 0)
                {
                    if ((uint)ownerMapIndex < (uint)mapToDense.Length)
                    {
                        dense = mapToDense[ownerMapIndex];
                        if (dense >= 0) nationId = denseNationIds[dense];
                    }
                    else if (overflowToDense is not null && overflowToDense.TryGetValue(ownerMapIndex, out dense))
                    {
                        nationId = denseNationIds[dense];
                    }
                }
            }
            curOwners[k] = nationId;
            curDense[k] = dense;
            if (!anyChanged && prevOwners[k] != nationId)
                anyChanged = true;
        }

        if (!anyChanged)
            return state.SeaZoneNeighbors;

        var pairs = new HashSet<long>(PairKeyComparer.Instance);
        const int DenseMatrixLimit = 2048;
        ulong[] seen = null;
        if (liveCount <= DenseMatrixLimit)
        {
            var wordCount = (liveCount * liveCount + 63) >> 6;
            if (state.SeenMatrix.Length < wordCount)
                state.SeenMatrix = new ulong[Math.Max(wordCount, state.SeenMatrix.Length * 2)];
            seen = state.SeenMatrix;
            Array.Clear(seen, 0, wordCount);
        }

        var cells = graph.Cells;
        var cCount = cells.Length;
        if (state.CellSingleDense.Length < cCount)
            state.CellSingleDense = new int[cCount];
        if (state.CellOwnerStart.Length < cCount + 1)
            state.CellOwnerStart = new int[cCount + 1];
        if (state.CellOwners.Length < h)
            state.CellOwners = new int[h];

        var cellSingle = state.CellSingleDense;
        var cellOwnerStart = state.CellOwnerStart;
        var cellOwners = state.CellOwners;
        var cellOwnerWrite = 0;

        for (var c = 0; c < cCount; c++)
        {
            cellOwnerStart[c] = cellOwnerWrite;
            ref readonly var cell = ref cells[c];
            var hEnd = cell.HarbourEnd;
            var startWrite = cellOwnerWrite;
            for (var p = cell.HarbourStart; p < hEnd; p++)
            {
                var d = curDense[p];
                if (d < 0) continue;
                var duplicate = false;
                for (var q = startWrite; q < cellOwnerWrite; q++)
                {
                    if (cellOwners[q] == d) { duplicate = true; break; }
                }
                if (!duplicate)
                    cellOwners[cellOwnerWrite++] = d;
            }

            var uniqueCount = cellOwnerWrite - startWrite;
            if (uniqueCount == 0)
                cellSingle[c] = -1;
            else if (uniqueCount == 1)
                cellSingle[c] = cellOwners[startWrite];
            else
            {
                cellSingle[c] = -2;
                if (cell.SelfAllConnected)
                {
                    for (var i = startWrite; i < cellOwnerWrite; i++)
                    for (var j = i + 1; j < cellOwnerWrite; j++)
                        RecordSeaZonePair(cellOwners[i], cellOwners[j], liveCount, denseNationIds, seen, pairs);
                }
            }
        }
        cellOwnerStart[cCount] = cellOwnerWrite;

        var interiorA = graph.InteriorCellA;
        var interiorB = graph.InteriorCellB;
        for (var p = 0; p < interiorA.Length; p++)
        {
            var c1 = interiorA[p];
            var s1 = cellSingle[c1];
            if (s1 == -1) continue;
            var c2 = interiorB[p];
            var s2 = cellSingle[c2];
            if (s2 == -1) continue;

            if (s1 >= 0 && s2 >= 0)
            {
                if (s1 != s2)
                    RecordSeaZonePair(s1, s2, liveCount, denseNationIds, seen, pairs);
                continue;
            }

            if (s1 >= 0)
            {
                for (int k = cellOwnerStart[c2], kEnd = cellOwnerStart[c2 + 1]; k < kEnd; k++)
                {
                    var v = cellOwners[k];
                    if (v != s1)
                        RecordSeaZonePair(s1, v, liveCount, denseNationIds, seen, pairs);
                }
            }
            else if (s2 >= 0)
            {
                for (int k = cellOwnerStart[c1], kEnd = cellOwnerStart[c1 + 1]; k < kEnd; k++)
                {
                    var u = cellOwners[k];
                    if (u != s2)
                        RecordSeaZonePair(u, s2, liveCount, denseNationIds, seen, pairs);
                }
            }
            else
            {
                int iStart = cellOwnerStart[c1], iEnd = cellOwnerStart[c1 + 1];
                int jStart = cellOwnerStart[c2], jEnd = cellOwnerStart[c2 + 1];
                for (var i = iStart; i < iEnd; i++)
                {
                    var u = cellOwners[i];
                    for (var j = jStart; j < jEnd; j++)
                    {
                        var v = cellOwners[j];
                        if (u != v)
                            RecordSeaZonePair(u, v, liveCount, denseNationIds, seen, pairs);
                    }
                }
            }
        }

        var boundaryA = graph.BoundaryHarbourA;
        var boundaryB = graph.BoundaryHarbourB;
        for (var e = 0; e < boundaryA.Length; e++)
        {
            var u = curDense[boundaryA[e]];
            if (u < 0) continue;
            var v = curDense[boundaryB[e]];
            if (v < 0 || u == v) continue;
            RecordSeaZonePair(u, v, liveCount, denseNationIds, seen, pairs);
        }

        if (state.PrevHarbourNationIds is null || state.PrevHarbourNationIds.Length != h)
            state.PrevHarbourNationIds = new int[h];
        state.CurHarbourNationIds = prevOwners is not null && prevOwners.Length == h ? prevOwners : new int[h];
        state.PrevHarbourNationIds = curOwners;
        state.PrevSeaGraph = graph;
        return pairs;
    }

    private static DiplomaticLodSnapshot GetLodSnapshot(Campaign campaign, bool fastInTick = false)
    {
        var state = LodStates.GetOrCreateValue(campaign);
        var warCount = campaign.Wars.Count;
        var seaThreshold = ComputeSeaZoneThreshold(campaign);
        var lodMode = NormalizeDiplomaticLodMode(campaign.Defines?.AiDiplomacyLodMode);
        var current = Volatile.Read(ref state.Snapshot);
        if (fastInTick && current is not null && current.MatchesFast(campaign, warCount, seaThreshold, lodMode))
            return current;

        var orgEnabled = campaign.FeatureEnabled("organizations");
        var orgStamp = orgEnabled ? ComputeOrganizationsStamp(campaign) : 0;
        if (current is not null && current.Matches(campaign, orgStamp, orgEnabled, warCount, seaThreshold, lodMode))
            return current;

        lock (state.Sync)
        {
            current = state.Snapshot;
            if (fastInTick && current is not null && current.MatchesFast(campaign, warCount, seaThreshold, lodMode))
                return current;
            if (current is not null && current.Matches(campaign, orgStamp, orgEnabled, warCount, seaThreshold, lodMode))
                return current;

            if (campaign.ProvinceMap is not null)
                _ = CampaignDiplomacy.SharesLandBorder(campaign, 0, 0);

            if (state.GreatPowers is null || state.GpDay != campaign.ElapsedDays || state.GpNationsCount != campaign.Nations.Count)
            {
                state.GreatPowers = campaign.Nations.Count > 0
                    ? new HashSet<int>(AllianceSystem.GreatPowers(campaign))
                    : new HashSet<int>();
                state.GpDay = campaign.ElapsedDays;
                state.GpNationsCount = campaign.Nations.Count;
            }

            if (state.NationsAtWar is null || state.WarRevision != campaign.WarRevision || state.WarCount != warCount)
            {
                var atWar = new HashSet<int>();
                var liveWars = Warfare.LiveWars(campaign);
                for (var wi = 0; wi < liveWars.Count; wi++)
                {
                    var w = liveWars[wi];
                    if (w.Ended) continue;
                    atWar.Add(w.AttackerId);
                    atWar.Add(w.DefenderId);
                    foreach (var p in w.AttackerAllies) atWar.Add(p);
                    foreach (var p in w.DefenderAllies) atWar.Add(p);
                }
                state.NationsAtWar = atWar;
                state.WarRevision = campaign.WarRevision;
                state.WarCount = warCount;
            }

            if (state.DefencePartners is null || state.OrgStamp != orgStamp || state.OrgEnabled != orgEnabled)
            {
                InternationalOrganizations.WarmDefencePartners(campaign);
                var defencePartners = new HashSet<long>(PairKeyComparer.Instance);
                if (orgEnabled)
                {
                    var orgs = campaign.Organizations;
                    for (var oi = 0; oi < orgs.Count; oi++)
                    {
                        var o = orgs[oi];
                        if (o.Dissolved || InternationalOrganizations.Charter(o.CharterId) is not { Defence: true } || o.Cohesion < .25f)
                            continue;
                        var members = o.Members;
                        for (var i = 0; i < members.Count; i++)
                        for (var j = i + 1; j < members.Count; j++)
                            defencePartners.Add(WeeklyPairKey(members[i], members[j]));
                    }
                }
                state.DefencePartners = defencePartners;
                state.OrgStamp = orgStamp;
                state.OrgEnabled = orgEnabled;
            }

            var map = campaign.ProvinceMap;
            var seaRevision = SeaPassageCatalog.Revision;
            if (state.SeaZoneNeighbors is null || !ReferenceEquals(state.SeaMap, map) ||
                state.SeaOwnershipRevision != campaign.OwnershipRevision ||
                state.SeaPassageRevision != seaRevision ||
                state.SeaNationsCount != campaign.Nations.Count ||
                MathF.Abs(state.SeaThreshold - seaThreshold) > 0.0001f)
            {
                state.SeaZoneNeighbors = RefreshSeaZoneNeighbors(campaign, state, seaThreshold);
                state.SeaMap = map;
                state.SeaOwnershipRevision = campaign.OwnershipRevision;
                state.SeaPassageRevision = seaRevision;
                state.SeaNationsCount = campaign.Nations.Count;
                state.SeaThreshold = seaThreshold;
            }

            var needPlayerSphere = lodMode == "player_focus" && !campaign.ObserverMode;
            if (needPlayerSphere &&
                (state.PlayerSphere is null || state.PlayerSphereDay != campaign.ElapsedDays ||
                 state.PlayerSphereOwnershipRevision != campaign.OwnershipRevision ||
                 state.PlayerSphereNationsCount != campaign.Nations.Count))
            {
                state.PlayerSphere = RefreshPlayerSphere(campaign, state);
                state.PlayerSphereDay = campaign.ElapsedDays;
                state.PlayerSphereOwnershipRevision = campaign.OwnershipRevision;
                state.PlayerSphereNationsCount = campaign.Nations.Count;
            }

            var built = new DiplomaticLodSnapshot
            {
                ElapsedDays = campaign.ElapsedDays,
                OwnershipRevision = campaign.OwnershipRevision,
                WarRevision = campaign.WarRevision,
                WarCount = warCount,
                NationsCount = campaign.Nations.Count,
                StrategicRevision = campaign.StrategicRevision,
                AllianceRevision = campaign.AllianceRevision,
                OrgStamp = orgStamp,
                OrgEnabled = orgEnabled,
                SeaPassageRevision = seaRevision,
                SeaThreshold = seaThreshold,
                LodMode = lodMode,
                ObserverMode = campaign.ObserverMode,
                SeaMap = map,
                GreatPowers = state.GreatPowers,
                NationsAtWar = state.NationsAtWar,
                DefencePartners = state.DefencePartners,
                SeaZoneNeighbors = state.SeaZoneNeighbors,
                PlayerSphere = needPlayerSphere ? state.PlayerSphere : null
            };
            Volatile.Write(ref state.Snapshot, built);
            return built;
        }
    }

    internal static DiplomaticLodSnapshot AcquireDiplomaticLod(Campaign campaign) =>
        campaign is not null ? GetLodSnapshot(campaign) : null;

    internal static void WarmDiplomaticLod(Campaign campaign)
    {
        if (campaign is not null) _ = GetLodSnapshot(campaign);
    }

    internal static string NormalizeDiplomaticLodMode(string value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "full" => "full",
            "world" => "world",
            "player_focus" => "player_focus",
            _ => "balanced"
        };

    private static HashSet<int> RefreshPlayerSphere(Campaign campaign, DiplomaticLodState state)
    {
        var sphere = new HashSet<int>();
        if (campaign is null || campaign.ObserverMode) return sphere;
        var humans = new List<Nation>(2);
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var n = nations[i];
            if (n is not null && (campaign.IsHumanControlled(n) || n.IsHuman || n.IsPlayer))
            {
                humans.Add(n);
                sphere.Add(n.Id);
            }
        }
        if (humans.Count == 0) return sphere;

        var nearKm = MathF.Max(2500f, campaign.Defines?.WitnessNearKm ?? 1500f);
        var sea = state.SeaZoneNeighbors;
        for (var i = 0; i < nations.Count; i++)
        {
            var other = nations[i];
            if (other is null || other.Population <= 0 || sphere.Contains(other.Id)) continue;
            for (var h = 0; h < humans.Count; h++)
            {
                var human = humans[h];
                if (other.AnswersTo(human.Id) || human.AnswersTo(other.Id) ||
                    (!string.IsNullOrEmpty(human.Region) && string.Equals(human.Region, other.Region, StringComparison.OrdinalIgnoreCase)) ||
                    (campaign.ProvinceMap is not null && CampaignDiplomacy.SharesLandBorder(campaign, human.MapIndex, other.MapIndex)) ||
                    ((human.CapitalLatitude != 0f || human.CapitalLongitude != 0f) &&
                     (other.CapitalLatitude != 0f || other.CapitalLongitude != 0f) &&
                     AllianceSystem.CapitalKm(human, other) <= nearKm) ||
                    (sea is not null && sea.Contains(WeeklyPairKey(human.Id, other.Id))))
                {
                    sphere.Add(other.Id);
                    break;
                }
            }
        }
        return sphere;
    }

    internal static bool IsBackgroundSlotDueForWeek(int week, int idA, int idB, int outerCadenceWeeks = 1)
    {
        var slot = (int)(((uint)(idA + idB)) & 3u);
        var w = week < 0 ? 0 : week;
        if (outerCadenceWeeks <= 1) return (w & 3) == slot;
        if (outerCadenceWeeks == 2) return ((w >> 1) & 1) == ((slot >> 1) & 1);
        return true;
    }

    internal static bool IsActiveDiplomacyTier(Campaign campaign, Nation a, Nation b,
        DiplomaticRelation relation, bool bordersKnown, bool borders) =>
        IsActiveDiplomacyTierCore(campaign, a, b, relation, bordersKnown, borders, lodSnapshot: null, fastInTick: false);

    internal static bool IsActiveDiplomacyTier(Campaign campaign, Nation a, Nation b,
        DiplomaticRelation relation, bool bordersKnown, bool borders, DiplomaticLodSnapshot lodSnapshot) =>
        IsActiveDiplomacyTierCore(campaign, a, b, relation, bordersKnown, borders, lodSnapshot, fastInTick: false);

    private static bool IsActiveDiplomacyTierCore(Campaign campaign, Nation a, Nation b,
        DiplomaticRelation relation, bool bordersKnown, bool borders, DiplomaticLodSnapshot lodSnapshot, bool fastInTick)
    {
        if (campaign is null || a is null || b is null || a.Id == b.Id) return false;
        var mode = NormalizeDiplomaticLodMode(campaign.Defines?.AiDiplomacyLodMode);
        if (mode == "full") return true;
        var effectiveMode = (mode == "player_focus" && campaign.ObserverMode) ? "world" : mode;
        var isHumanPair = campaign.IsHumanControlled(a) || campaign.IsHumanControlled(b) ||
            (!campaign.ObserverMode && (a.IsHuman || b.IsHuman || a.IsPlayer || b.IsPlayer));
        if (effectiveMode != "world" && isHumanPair)
            return true;
        if (a.AnswersTo(b.Id) || b.AnswersTo(a.Id))
            return true;
        if ((a.TradePartners.Count > 0 && a.TradePartners.GetValueOrDefault(b.Id) > 0f) ||
            (b.TradePartners.Count > 0 && b.TradePartners.GetValueOrDefault(a.Id) > 0f))
            return true;

        relation ??= CampaignDiplomacy.Peek(campaign, a.Id, b.Id);
        if (relation is not null &&
            (relation.Stance is DiplomaticStance.Allied or DiplomaticStance.AtWar or DiplomaticStance.Truce ||
             relation.TruceUntil > campaign.ElapsedDays ||
             relation.GuaranteeByA || relation.GuaranteeByB ||
             relation.EmbargoByA || relation.EmbargoByB ||
             relation.NonAggressionPact || relation.TradeAgreement ||
             relation.Trade is not null ||
             relation.MilitaryAccessByA || relation.MilitaryAccessByB ||
             relation.MissionByA is not null || relation.MissionByB is not null ||
             relation.AllianceOfferedBy > 0 || relation.AgreementOfferedBy >= 0 ||
             relation.GuaranteeRequestedBy >= 0 || relation.MilitaryAccessRequestedBy >= 0 ||
             relation.TensionStage > 0 || relation.LoanOutstanding > 0f))
            return true;

        var sharesLandBorder = bordersKnown
            ? borders
            : (campaign.ProvinceMap is not null && CampaignDiplomacy.SharesLandBorder(campaign, a.MapIndex, b.MapIndex));
        if (sharesLandBorder)
            return true;

        if (effectiveMode == "player_focus")
        {
            var focusSnap = lodSnapshot ?? GetLodSnapshot(campaign, fastInTick);
            if (focusSnap.GreatPowers.Contains(a.Id) && focusSnap.GreatPowers.Contains(b.Id))
                return true;
            if (focusSnap.NationsAtWar.Contains(a.Id) && focusSnap.NationsAtWar.Contains(b.Id) &&
                CampaignDiplomacy.AtWar(campaign, a.Id, b.Id))
                return true;
            if (focusSnap.DefencePartners.Contains(WeeklyPairKey(a.Id, b.Id)))
                return true;
            var sphere = focusSnap.PlayerSphere;
            return sphere is not null &&
                   ((sphere.Contains(a.Id) && (sphere.Contains(b.Id) || focusSnap.GreatPowers.Contains(b.Id) ||
                                               b.Gdp >= LodMajorGdpThreshold || focusSnap.NationsAtWar.Contains(b.Id))) ||
                    (sphere.Contains(b.Id) && (focusSnap.GreatPowers.Contains(a.Id) ||
                                               a.Gdp >= LodMajorGdpThreshold || focusSnap.NationsAtWar.Contains(a.Id))));
        }

        if (effectiveMode == "world" && (a.Notoriety >= 8f || b.Notoriety >= 8f || a.Stability < 0.30f || b.Stability < 0.30f))
            return true;

        if (a.Gdp >= LodMajorGdpThreshold || b.Gdp >= LodMajorGdpThreshold)
            return true;

        if (!string.IsNullOrEmpty(a.Region) && string.Equals(a.Region, b.Region, StringComparison.OrdinalIgnoreCase))
            return true;

        if ((a.CapitalLatitude != 0f || a.CapitalLongitude != 0f) &&
            (b.CapitalLatitude != 0f || b.CapitalLongitude != 0f) &&
            AllianceSystem.CapitalKm(a, b) <= MathF.Max(1500f, campaign.Defines.WitnessNearKm))
            return true;

        var snap = lodSnapshot ?? GetLodSnapshot(campaign, fastInTick);
        if (snap.GreatPowers.Contains(a.Id) || snap.GreatPowers.Contains(b.Id))
            return true;
        if (snap.NationsAtWar.Contains(a.Id) && snap.NationsAtWar.Contains(b.Id) &&
            CampaignDiplomacy.AtWar(campaign, a.Id, b.Id))
            return true;
        if (effectiveMode == "world" &&
            ((snap.NationsAtWar.Contains(a.Id) && (b.Gdp >= LodMajorGdpThreshold * 0.25f || snap.NationsAtWar.Contains(b.Id))) ||
             (snap.NationsAtWar.Contains(b.Id) && a.Gdp >= LodMajorGdpThreshold * 0.25f)))
            return true;

        var key = WeeklyPairKey(a.Id, b.Id);
        return snap.DefencePartners.Contains(key) || snap.SeaZoneNeighbors.Contains(key);
    }

    internal static bool IsPairDueThisCycle(Campaign campaign, Nation actor, Nation other,
        DiplomaticRelation relation, bool bordersKnown, bool borders, bool respectStatecraftCalmCadence)
    {
        if (NormalizeDiplomaticLodMode(campaign?.Defines?.AiDiplomacyLodMode) == "full")
            return true;
        var week = campaign.ElapsedDays >= 0 ? campaign.ElapsedDays / 7 : 0;
        if (!respectStatecraftCalmCadence && IsBackgroundSlotDueForWeek(week, actor.Id, other.Id))
            return true;
        if (IsActiveDiplomacyTierCore(campaign, actor, other, relation, bordersKnown, borders,
                lodSnapshot: null, fastInTick: respectStatecraftCalmCadence))
            return true;
        var outerCadenceWeeks = 1;
        if (respectStatecraftCalmCadence && campaign.Defines.AiCalmCadenceDays > 1)
        {
            var inWar = GetLodSnapshot(campaign, fastInTick: true).NationsAtWar.Contains(actor.Id);
            if (IsCalmAiFast(campaign, actor, inWar))
            {
                CalmCadences(campaign, actor, out var diplomacyDays, out _);
                outerCadenceWeeks = Math.Max(1, diplomacyDays / 7);
            }
        }
        return IsBackgroundSlotDueForWeek(week, actor.Id, other.Id, outerCadenceWeeks);
    }

    /// <summary>
    /// The friendly move is not repeated when the pair is the one last walked and none of
    /// its branches can write. A warm pair, a pair at a threshold, or a pair that has
    /// never been walked is still asked.
    /// </summary>
    internal static bool SkipUnchangedFriendly(Campaign campaign, Nation nation, Nation friend, bool borders)
    {
        if (campaign is null || nation is null || friend is null) return false;
        var bond = CampaignDiplomacy.Peek(campaign, nation.Id, friend.Id);
        if (!IsPairDueThisCycle(campaign, nation, friend, bond, bordersKnown: true, borders,
                respectStatecraftCalmCadence: TablesOf(campaign).InStatecraftTick))
            return true;
        if (bond is null) return false;
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
