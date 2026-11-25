using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>
/// Seed- and context-driven strategic doctrine across the Taiwan Strait, ensuring distinct
/// campaigns diverge between amphibious crossing bids, Pacific fortified containment, peaceful
/// diplomatic coexistence, and asymmetric crisis polarization.
/// </summary>
public enum CrossStraitPosture
{
    AmphibiousReclamation = 0,
    FortifiedContainment = 1,
    DiplomaticDetente = 2,
    AsymmetricCrisis = 3
}

public sealed partial class Defines
{
    /// <summary>Master switch for cross-strait / divided-nation mainland reclamation mechanics.</summary>
    public bool MainlandReclamationEnabled { get; set; } = true;

    /// <summary>Volunteer divisions rallying immediately when a reunification contender establishes a mainland bridgehead.</summary>
    public int MainlandBridgeheadRallyDivisions { get; set; } = 2;

    /// <summary>Baseline volunteer divisions rallying when a kindred mainland region is liberated.</summary>
    public int MainlandRegionRallyDivisions { get; set; } = 1;

    /// <summary>Maximum total mainland volunteer divisions a single contender may rally in one war.</summary>
    public int MainlandRallyMaxDivisionsPerWar { get; set; } = 18;

    /// <summary>Rival war-support loss when a mainland region is liberated by a reunification contender.</summary>
    public float MainlandLiberationWarSupportHit { get; set; } = 0.018f;

    /// <summary>Rival stability loss when a mainland region is liberated by a reunification contender.</summary>
    public float MainlandLiberationStabilityHit { get; set; } = 0.010f;

    /// <summary>Rival domestic coup-pressure bump when a mainland region is liberated.</summary>
    public float MainlandLiberationCoupPressure { get; set; } = 0.020f;

    /// <summary>Radicalism surge across contested rival regions when a mainland bridgehead or region falls.</summary>
    public float MainlandLiberationRadicalismSurge { get; set; } = 0.015f;

    /// <summary>Base monthly frontline defection chance for rival garrisons in a reunification liberation war.</summary>
    public float MainlandDefectionChanceBonus { get; set; } = 0.06f;

    /// <summary>Sea distance in degrees treated as a narrow strait for amphibious crossing odds.</summary>
    public float AiNarrowStraitInvasionDegrees { get; set; } = 7.5f;

    /// <summary>Allowed local enemy naval odds across a narrow strait before the AI refuses to embark.</summary>
    public float AiNarrowStraitInvasionOdds { get; set; } = 1.35f;

    /// <summary>Earliest campaign day an AI maritime reunification contender considers launching a mainland return.</summary>
    public int AiMainlandReturnEarliestDay { get; set; } = 540;

    /// <summary>Days between AI mainland-return readiness reviews.</summary>
    public int AiMainlandReturnReviewDays { get; set; } = 21;

    /// <summary>Base per-review chance an AI maritime contender launches its prepared mainland return when a window opens.</summary>
    public float AiMainlandReturnChance { get; set; } = 0.12f;

    /// <summary>Minimum ready field divisions required before an AI launches an overseas mainland reclamation war.</summary>
    public int AiMainlandReturnMinimumDivisions { get; set; } = 22;

    /// <summary>Minimum naval hulls required before an AI launches an overseas mainland reclamation war.</summary>
    public int AiMainlandReturnMinimumHulls { get; set; } = 45;
}

/// <summary>
/// Cross-strait and divided-nation Mainland Reclamation ("Return to the Mainland").
///
/// A smaller maritime republic facing a giant rival of the same people (such as Taipei across
/// the Taiwan Strait) cannot match the mainland's population division-for-division on day one.
/// What turns a landing into a genuine historical campaign is momentum on kindred soil:
///
/// 1. Establishing a mainland bridgehead shatters the rival regime's aura of invincibility,
///    inflicting bridgehead shock and rallying initial local liberation divisions.
/// 2. Each liberated kindred mainland region swells the returning republic's armies with local
///    volunteers and manpower while deepening instability, radicalism, and frontline defections
///    inside the rival regime.
/// 3. An AI maritime contender (such as Taiwan) prepares its fleet, marine corps, and Project
///    National Glory doctrine, waits for a strategic window (mainland overstretch, crisis,
///    strait escalation, or Pacific backing), launches the crossing, petitions allies, and
///    pushes inland toward full national reunification.
///
/// All persistent per-war markers live in <see cref="Nation.DecisionAvailableDay"/> as future
/// expiry stamps so legacy saves remain 100% compatible and campaign fingerprints stay exact.
/// </summary>
public static class MainlandReclamation
{
    private const string BridgeheadPrefix = "mr_bh/";
    private const string RegionPrefix = "mr_reg/";
    private const string DefectionPrefix = "mr_def/";
    private const string UnitPrefix = "mr_u/";
    private const string AiReviewKey = "mr_ai_review";
    private const string AllyPleaPrefix = "mr_ally/";

    /// <summary>Session diagnostic counters (not campaign state, not saved).</summary>
    public static readonly Dictionary<string, int> Tally = new(StringComparer.Ordinal);
    private static void Count(string key) => Tally[key] = Tally.TryGetValue(key, out var n) ? n + 1 : 1;

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null) yield break;
        if (!campaign.Defines.MainlandReclamationEnabled) yield break;
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(MainlandReclamation))) yield break;

        foreach (var _ in ProcessActiveReclamationWars(campaign)) yield return 0;
        foreach (var _ in ReviewAiMainlandReturn(campaign)) yield return 0;
    }

    /// <summary>
    /// True when <paramref name="nation"/> owns at least one land province and NONE of its owned
    /// land provinces share a dry-land border (<see cref="ProvinceTopology.LandNeighbors"/>) with
    /// any province owned by <paramref name="rival"/>. Ferry crossings (such as Kinmen–Xiamen) do
    /// not erase the Taiwan Strait, while annexing any Mainland state bordering the rival
    /// immediately establishes a dry-land border and returns false.
    /// </summary>
    public static bool IsSeparatedBySea(Campaign campaign, Nation nation, Nation rival)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || nation is null || rival is null) return false;
        if (nation.MapIndex <= 0 || rival.MapIndex <= 0) return false;
        var own = (ushort)nation.MapIndex;
        var target = (ushort)rival.MapIndex;
        var hasOwnLand = false;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.IsWater(p) || map.ProvinceToNation[p] != own) continue;
            hasOwnLand = true;
            foreach (var next in map.LandNeighbors[p])
            {
                if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                if (map.ProvinceToNation[next] == target) return false;
            }
        }
        return hasOwnLand;
    }

    private sealed class CampaignReclamationScratch
    {
        public readonly List<StrategicWar> OpenWars = new();
        public readonly List<Nation> OuterNations = new();
        public readonly List<Nation> CandidateNations = new();
        public readonly List<Nation> AllyDonors = new();
        public readonly HashSet<string> OurCultures = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SideScratch
    {
        public readonly HashSet<int> ActiveBattleProvinces = new();
        public readonly Dictionary<int, int> TotalByState = new();
        public readonly Dictionary<int, int> HeldByState = new();
        public readonly Dictionary<int, int> SampleProvinceByState = new();
        public readonly Dictionary<int, int> SafeProvinceByState = new();
        public readonly List<ScenarioState> SortedStates = new();
        public readonly List<ScenarioState> DefectionStates = new();
        public readonly HashSet<string> Cultures = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> AdjacentStates = new();
        public readonly HashSet<int> CandidateProvinces = new();
        public readonly HashSet<int> LoyalProvinces = new();
    }

    private static readonly ConditionalWeakTable<Campaign, CampaignReclamationScratch> Scratches = new();
    [ThreadStatic] private static SideScratch _sideScratch;
    [ThreadStatic] private static HashSet<string> _pendingCultures;
    [ThreadStatic] private static HashSet<string> _pairCultures;
    [ThreadStatic] private static HashSet<string> _liberatorCultures;
    private static readonly string[] BridgeheadKeys = BuildPrefixKeys(BridgeheadPrefix, 256);

    private static string[] BuildPrefixKeys(string prefix, int count)
    {
        var keys = new string[count];
        for (var i = 0; i < count; i++) keys[i] = prefix + i;
        return keys;
    }

    private static string BridgeheadKey(int warId) =>
        (uint)warId < (uint)BridgeheadKeys.Length ? BridgeheadKeys[warId] : BridgeheadPrefix + warId;

    private static CampaignReclamationScratch GetScratch(Campaign campaign) => Scratches.GetOrCreateValue(campaign);
    private static SideScratch GetSideScratch() => _sideScratch ??= new SideScratch();

    private static readonly Comparison<Nation> CompareNationById = static (a, b) => a.Id.CompareTo(b.Id);
    private static readonly Comparison<ScenarioState> CompareStateByMapIndex = static (a, b) => a.MapIndex.CompareTo(b.MapIndex);
    private static readonly Comparison<ScenarioState> CompareStateForDefection = static (a, b) =>
    {
        var cmp = a.Population.CompareTo(b.Population);
        return cmp != 0 ? cmp : a.MapIndex.CompareTo(b.MapIndex);
    };

    private static void FillCultures(Nation nation, HashSet<string> target)
    {
        target.Clear();
        if (nation is null) return;
        foreach (var people in DemographyCatalog.PrimaryCultures(nation.Tag)) target.Add(people);
        for (var i = 0; i < nation.FoundedCultures.Count; i++) target.Add(nation.FoundedCultures[i]);
    }

    private static bool SharesCultureWith(HashSet<string> ourCultures, Nation candidate)
    {
        if (ourCultures.Count == 0 || candidate is null) return false;
        foreach (var people in DemographyCatalog.PrimaryCultures(candidate.Tag))
            if (ourCultures.Contains(people)) return true;
        for (var i = 0; i < candidate.FoundedCultures.Count; i++)
            if (ourCultures.Contains(candidate.FoundedCultures[i])) return true;
        return false;
    }

    private static bool HasAnyHomeland(Campaign campaign, Nation nation, Nation holder)
    {
        if (nation is null || holder is null || nation.Id == holder.Id ||
            holder.AnswersTo(nation.Id) || nation.AnswersTo(holder.Id))
            return false;
        var states = campaign.StatesOf(holder);
        for (var i = 0; i < states.Length; i++)
            if (TerritorialClaims.TitleOf(campaign, nation, states[i]) == TerritorialTitle.Homeland)
                return true;
        return false;
    }

    private static ScenarioState BestHomelandGoal(Campaign campaign, Nation nation, Nation holder)
    {
        if (nation is null || holder is null || nation.Id == holder.Id ||
            holder.AnswersTo(nation.Id) || nation.AnswersTo(holder.Id))
            return null;
        ScenarioState best = null;
        var states = campaign.StatesOf(holder);
        for (var i = 0; i < states.Length; i++)
        {
            var state = states[i];
            if (TerritorialClaims.TitleOf(campaign, nation, state) != TerritorialTitle.Homeland) continue;
            if (best is null || state.Population > best.Population ||
                (state.Population == best.Population && state.MapIndex < best.MapIndex))
                best = state;
        }
        return best;
    }

    private static void CopySortedNations(Campaign campaign, List<Nation> target)
    {
        target.Clear();
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++) target.Add(nations[i]);
        target.Sort(CompareNationById);
    }

    /// <summary>Whether <paramref name="liberator"/> has already established a mainland bridgehead in <paramref name="warId"/>.</summary>
    public static bool HasBridgehead(Nation liberator, int warId) =>
        liberator is not null && liberator.DecisionAvailableDay.Count > 0 &&
        liberator.DecisionAvailableDay.ContainsKey(BridgeheadKey(warId));

    private static bool HasAnySpirit(Nation nation, string genericA, string genericB, string legacyId) =>
        ModifierSystem.Has(nation, genericA) ||
        ModifierSystem.Has(nation, genericB) ||
        ModifierSystem.Has(nation, legacyId);

    private static bool HasGlorySpirit(Nation nation) =>
        HasAnySpirit(nation, "reclamation_glory_spirit", "reclamation_national_glory_spirit", "twn_project_national_glory_spirit");

    private static bool HasArmadaSpirit(Nation nation) =>
        HasAnySpirit(nation, "reclamation_armada_spirit", "reclamation_strait_armada_spirit", "twn_strait_armada_spirit");

    private static bool HasUndergroundSpirit(Nation nation) =>
        HasAnySpirit(nation, "reclamation_underground_spirit", "reclamation_mainland_underground_spirit", "twn_mainland_underground_spirit");

    private static bool IsDividedRivalCore(Campaign campaign, Nation contender, Nation holder, int contenderStatesCount, HashSet<string> contenderCultures)
    {
        if (contender is null || holder is null || contender.Id == holder.Id) return false;
        if (contender.Population <= 0 || holder.Population <= 0 || contender.Population >= holder.Population) return false;
        if (contender.IsSubject || contender.Landless || contender.Movement is not null || WarAims.IsRebel(contender)) return false;
        if (holder.IsSubject || holder.Landless || holder.Movement is not null || WarAims.IsRebel(holder)) return false;
        if (contenderStatesCount <= 0) return false;
        var holderStates = campaign.StatesOf(holder).Length;
        if (holderStates < 4 || holderStates <= contenderStatesCount) return false;
        if (PeacefulFormation.CulturalAffinity(contender, holder) < 0.75f) return false;
        if (!SharesCultureWith(contenderCultures, holder)) return false;
        return HasAnyHomeland(campaign, contender, holder);
    }

    /// <summary>
    /// Allocation-free check identifying whether <paramref name="a"/> and <paramref name="b"/> form
    /// an asymmetric divided-homeland pair, outputting the smaller <paramref name="contender"/>
    /// and the larger homeland <paramref name="holder"/>.
    /// </summary>
    public static bool TryGetDividedRivalPair(Campaign campaign, Nation a, Nation b, out Nation contender, out Nation holder)
    {
        contender = null;
        holder = null;
        if (campaign is null || a is null || b is null || a.Id == b.Id) return false;
        var cultures = _pairCultures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (a.Population > 0 && b.Population > a.Population)
        {
            FillCultures(a, cultures);
            if (cultures.Count > 0 && IsDividedRivalCore(campaign, a, b, campaign.StatesOf(a).Length, cultures))
            {
                contender = a;
                holder = b;
                return true;
            }
        }
        if (b.Population > 0 && a.Population > b.Population)
        {
            FillCultures(b, cultures);
            if (cultures.Count > 0 && IsDividedRivalCore(campaign, b, a, campaign.StatesOf(b).Length, cultures))
            {
                contender = b;
                holder = a;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="nation"/> still has an unreclaimed larger reunification rival
    /// holding its homeland cores.
    /// </summary>
    public static bool HasPendingReclamation(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || nation.Population <= 0 || nation.IsSubject ||
            nation.Landless || nation.Movement is not null || WarAims.IsRebel(nation))
            return false;
        var ownStates = campaign.StatesOf(nation).Length;
        if (ownStates <= 0) return false;
        var ourCultures = _pendingCultures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FillCultures(nation, ourCultures);
        if (ourCultures.Count == 0) return false;
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            if (IsDividedRivalCore(campaign, nation, nations[i], ownStates, ourCultures))
                return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="nation"/> is currently fighting as a Mainland liberator.</summary>
    public static bool InReclamationWar(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !campaign.Defines.MainlandReclamationEnabled) return false;
        foreach (var war in Warfare.OpenWars(campaign))
        {
            if (war is null || war.Ended || war.Limited || !war.Involves(nation.Id)) continue;
            var rival = campaign.NationById(war.EnemyLeaderOf(nation.Id));
            if (rival is not null && IsMainlandLiberator(campaign, war, nation, rival)) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="nation"/> is currently facing an active Mainland liberator.</summary>
    public static bool FacingReclamationWar(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !campaign.Defines.MainlandReclamationEnabled) return false;
        foreach (var war in Warfare.OpenWars(campaign))
        {
            if (war is null || war.Ended || war.Limited || !war.Involves(nation.Id)) continue;
            var enemy = campaign.NationById(war.EnemyLeaderOf(nation.Id));
            if (enemy is not null && IsMainlandLiberator(campaign, war, enemy, nation)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="liberator"/> is conducting an asymmetric divided-homeland
    /// reunification war onto <paramref name="rival"/>'s larger territory in <paramref name="war"/>
    /// (both across sea and across divided land borders). Excludes civil conflicts and wars where
    /// the liberator has already grown larger than its rival.
    /// </summary>
    public static bool IsMainlandLiberator(Campaign campaign, StrategicWar war, Nation liberator, Nation rival)
    {
        if (campaign is null || war is null || war.Ended || war.Limited || war.CivilConflict || liberator is null || rival is null)
            return false;
        if (liberator.Id == rival.Id || liberator.Population <= 0 || rival.Population <= 0) return false;
        if (liberator.Population >= rival.Population) return false;
        if (liberator.Movement is not null || liberator.Landless || liberator.IsSubject || WarAims.IsRebel(liberator)) return false;
        if (rival.Movement is not null || rival.Landless || rival.IsSubject || WarAims.IsRebel(rival)) return false;
        if (!war.Opposes(liberator.Id, rival.Id)) return false;

        var rivalStates = campaign.StatesOf(rival);
        if (rivalStates.Length < 4) return false;
        var liberatorStates = campaign.StatesOf(liberator);
        // Always require genuine underdog asymmetry: once the returning republic owns more states
        // or population than its rival, it is already the dominant homeland power.
        if (liberatorStates.Length <= 0 || rivalStates.Length <= liberatorStates.Length) return false;
        if (PeacefulFormation.CulturalAffinity(liberator, rival) < 0.75f) return false;

        var cultures = _liberatorCultures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FillCultures(liberator, cultures);
        if (!SharesCultureWith(cultures, rival)) return false;

        var hasBridgehead = HasBridgehead(liberator, war.Id);
        if (!hasBridgehead && !HasAnyHomeland(campaign, liberator, rival)) return false;
        return Reintegration.IsReunificationTarget(campaign, war, liberator, rival);
    }

    /// <summary>How many mainland volunteer divisions <paramref name="liberator"/> has already rallied in <paramref name="warId"/>.</summary>
    public static int RalliedInWar(Nation liberator, int warId)
    {
        if (liberator is null || liberator.DecisionAvailableDay.Count == 0) return 0;
        var prefix = UnitPrefix + warId + "/";
        var count = 0;
        foreach (var key in liberator.DecisionAvailableDay.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) count++;
        return count;
    }

    private static IEnumerable<int> ProcessActiveReclamationWars(Campaign campaign)
    {
        var scratch = GetScratch(campaign);
        var open = scratch.OpenWars;
        open.Clear();
        foreach (var openWar in Warfare.OpenWars(campaign)) open.Add(openWar);
        if (open.Count == 0) yield break;

        var map = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        var defines = campaign.Defines;
        var maxRally = Math.Max(0, defines.MainlandRallyMaxDivisionsPerWar);

        for (var i = 0; i < open.Count; i++)
        {
            var war = open[i];
            if (war is null || war.Ended || war.Limited) continue;
            var attacker = campaign.NationById(war.AttackerId);
            var defender = campaign.NationById(war.DefenderId);
            if (attacker is null || defender is null) continue;

            // Check both directions (Taiwan attacking China OR Taiwan counter-invading after China attacks).
            var attackerIsLib = CheckSide(campaign, war, attacker, defender, map, control, defines, maxRally);
            var defenderIsLib = CheckSide(campaign, war, defender, attacker, map, control, defines, maxRally);

            // Wartime allied intervention petitions for AI reclamation contenders.
            if (!attacker.IsHuman && attackerIsLib)
                PetitionRegionalAllies(campaign, scratch, war, attacker, defender);
            else if (!defender.IsHuman && defenderIsLib)
                PetitionRegionalAllies(campaign, scratch, war, defender, attacker);

            yield return 0;
        }
    }

    private static bool CheckSide(Campaign campaign, StrategicWar war, Nation liberator, Nation rival,
        ProvinceTopology map, ushort[] control, Defines defines, int maxRally)
    {
        if (!IsMainlandLiberator(campaign, war, liberator, rival)) return false;

        var libMap = (ushort)liberator.MapIndex;
        var rivalMap = (ushort)rival.MapIndex;
        var rivalStates = campaign.StatesOf(rival);
        if (rivalStates.Length == 0) return true;

        var scratch = GetSideScratch();
        var activeBattleProvinces = scratch.ActiveBattleProvinces;
        activeBattleProvinces.Clear();
        var battles = campaign.Battles;
        for (var i = 0; i < battles.Count; i++)
        {
            var battle = battles[i];
            if (battle is not null && !battle.Ended && battle.ProvinceId > 0)
                activeBattleProvinces.Add(battle.ProvinceId);
        }

        // Count controlled provinces per rival state in one clean pass.
        var totalByState = scratch.TotalByState;
        var heldByState = scratch.HeldByState;
        var sampleProvinceByState = scratch.SampleProvinceByState;
        var safeProvinceByState = scratch.SafeProvinceByState;
        totalByState.Clear();
        heldByState.Clear();
        sampleProvinceByState.Clear();
        safeProvinceByState.Clear();

        var firstHeldProvince = -1;
        var firstSafeProvince = -1;
        var firstHeldState = -1;
        var firstSafeState = -1;
        var totalHeld = 0;

        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.IsWater(p) || map.ProvinceToNation[p] != rivalMap) continue;
            var stateIdx = map.ProvinceToState[p];
            if (stateIdx <= 0) continue;
            totalByState[stateIdx] = totalByState.GetValueOrDefault(stateIdx) + 1;
            if (control[p] != libMap) continue;
            heldByState[stateIdx] = heldByState.GetValueOrDefault(stateIdx) + 1;
            totalHeld++;
            sampleProvinceByState.TryAdd(stateIdx, p);
            if (firstHeldProvince < 0)
            {
                firstHeldProvince = p;
                firstHeldState = stateIdx;
            }
            if (!activeBattleProvinces.Contains(p))
            {
                safeProvinceByState.TryAdd(stateIdx, p);
                if (firstSafeProvince < 0)
                {
                    firstSafeProvince = p;
                    firstSafeState = stateIdx;
                }
            }
        }

        if (firstHeldProvince <= 0) return true;

        var changed = false;
        var expire = campaign.ElapsedDays + 3650;
        var shockDays = rival.IsHuman ? 90 : 180;

        // 1. Bridgehead established on the rival's mainland!
        var bhKey = BridgeheadKey(war.Id);
        if (!liberator.DecisionAvailableDay.ContainsKey(bhKey))
        {
            liberator.DecisionAvailableDay[bhKey] = expire;
            ModifierSystem.Grant(campaign, liberator, "mainland_return_momentum", 365);
            ModifierSystem.Grant(campaign, rival, "mainland_regime_shock", shockDays);

            // Immediate shock to rival morale and stability.
            rival.WarSupport = M.Clamp(rival.WarSupport - defines.MainlandLiberationWarSupportHit * 1.35f, 0.05f, 1f);
            rival.Stability = M.Clamp(rival.Stability - defines.MainlandLiberationStabilityHit * 1.25f, 0.10f, 0.98f);
            if (rival.DomesticCrisis is not null)
                rival.DomesticCrisis.CoupPressure = M.Clamp01(rival.DomesticCrisis.CoupPressure + defines.MainlandLiberationCoupPressure);
            SurgeRivalRadicalism(campaign, rival, heldByState, defines.MainlandLiberationRadicalismSurge);

            // Rally initial local liberation volunteers at the beachhead so it can hold and expand.
            var spawnProv = PickSpawnProvince(campaign, firstSafeState > 0 ? firstSafeState : firstHeldState,
                libMap, rivalMap, activeBattleProvinces, firstSafeProvince > 0 ? firstSafeProvince : firstHeldProvince);
            var spawnState = spawnProv > 0 ? map.ProvinceToState[spawnProv] : firstHeldState;
            var already = RalliedInWar(liberator, war.Id);
            var want = Math.Min(Math.Max(0, defines.MainlandBridgeheadRallyDivisions), Math.Max(0, maxRally - already));
            if (HasUndergroundSpirit(liberator))
                want = Math.Min(want + 1, Math.Max(0, maxRally - already));
            SpawnVolunteerDivisions(campaign, war, liberator, spawnProv, spawnState, want, already);

            // Only emit the "crossed the sea and secured a mainland bridgehead" headline when the
            // two belligerents are genuinely separated by water (never on a dry-land border).
            if (IsSeparatedBySea(campaign, liberator, rival))
            {
                var stateName = campaign.StateByMapIndex(spawnState)?.Name ?? "";
                campaign.Chronicle.Record(campaign, "mainland_bridgehead", liberator.Id, rival.Id, subject: stateName);
            }
            Count("bridgehead_secured");
            changed = true;
        }

        // 2. Liberated kindred mainland regions rally local volunteers and shake the rival regime.
        var liberatorCultures = scratch.Cultures;
        FillCultures(liberator, liberatorCultures);
        var sortedStates = scratch.SortedStates;
        sortedStates.Clear();
        sortedStates.AddRange(rivalStates);
        sortedStates.Sort(CompareStateByMapIndex);

        for (var i = 0; i < sortedStates.Count; i++)
        {
            var state = sortedStates[i];
            if (!heldByState.TryGetValue(state.MapIndex, out var held) || held <= 0) continue;
            if (!totalByState.TryGetValue(state.MapIndex, out var total) || total <= 0) continue;
            // Liberating a clear majority (>= 60%) of a mainland region
            // sparks a local popular rally to help break out of the coastal bridgehead.
            if (held / (float)total < 0.60f) continue;

            var regKey = RegionPrefix + war.Id + "/" + state.MapIndex;
            if (liberator.DecisionAvailableDay.ContainsKey(regKey)) continue;

            var title = TerritorialClaims.TitleOf(campaign, liberator, state);
            var kinShare = liberatorCultures.Count > 0 ? WarAims.CultureShare(state, liberatorCultures) : 0f;
            if (title == TerritorialTitle.None && kinShare < 0.35f) continue;

            liberator.DecisionAvailableDay[regKey] = expire;
            // Refresh offensive momentum while liberation continues.
            ModifierSystem.Grant(campaign, liberator, "mainland_return_momentum", 365);
            ModifierSystem.Grant(campaign, rival, "mainland_regime_shock", shockDays);

            var already = RalliedInWar(liberator, war.Id);
            var remaining = Math.Max(0, maxRally - already);
            var baseCount = Math.Max(1, defines.MainlandRegionRallyDivisions);
            var popBonus = state.Population >= 55_000_000 ? 2 : state.Population >= 25_000_000 ? 1 : 0;
            var undergroundBonus = HasUndergroundSpirit(liberator) && state.Population >= 25_000_000 ? 1 : 0;
            var toSpawn = Math.Clamp(baseCount + popBonus + undergroundBonus, 1, 5);
            toSpawn = Math.Min(toSpawn, remaining);

            var fallbackProv = safeProvinceByState.GetValueOrDefault(state.MapIndex,
                sampleProvinceByState.GetValueOrDefault(state.MapIndex, firstSafeProvince > 0 ? firstSafeProvince : firstHeldProvince));
            var spawnProvince = PickSpawnProvince(campaign, state.MapIndex, libMap, rivalMap, activeBattleProvinces, fallbackProv);
            if (toSpawn > 0 && spawnProvince > 0)
                SpawnVolunteerDivisions(campaign, war, liberator, spawnProvince, state.MapIndex, toSpawn, already);

            // Liberated mainland citizens swell manpower reserves and refresh frontline forces.
            var manpowerGain = MathF.Min(220_000f, state.Population * 0.0035f);
            liberator.ManpowerReserve += manpowerGain;
            liberator.WarSupport = M.Clamp(liberator.WarSupport + 0.03f, 0f, 1f);

            // Each major mainland province lost hits Beijing's war support, stability, and cohesion.
            var weight = state.MapIndex == rival.CapitalStateMapIndex ? 1.8f
                : state.Population >= 50_000_000 ? 1.3f : 1.0f;
            rival.WarSupport = M.Clamp(rival.WarSupport - defines.MainlandLiberationWarSupportHit * weight, 0.05f, 1f);
            rival.Stability = M.Clamp(rival.Stability - defines.MainlandLiberationStabilityHit * weight, 0.10f, 0.98f);
            if (rival.DomesticCrisis is not null)
                rival.DomesticCrisis.CoupPressure = M.Clamp01(rival.DomesticCrisis.CoupPressure + defines.MainlandLiberationCoupPressure * weight);
            SurgeRivalRadicalism(campaign, rival, heldByState, defines.MainlandLiberationRadicalismSurge * 0.70f);

            campaign.Chronicle.Record(campaign, "mainland_rally", liberator.Id, rival.Id, subject: state.Name);
            Count("region_rallied");
            changed = true;
        }

        // 3. Sustained bridgehead momentum: every 30 days while holding a real mainland foothold
        // (>= 3 provinces, never a single isolated pixel), refresh regime shock and rally a modest
        // trickle of underground volunteers if under cap. When the rival regime's stability collapses
        // (<= 0.18, or <= 0.22 in AsymmetricCrisis) while a broad mainland foothold (>= 8 provinces
        // across >= 6 states) stands, an adjacent smaller kindred region can rise up and declare for
        // the returning republic (at most once per state/war).
        if (campaign.ElapsedDays % 30 == 15)
        {
            if (totalHeld >= 3)
            {
                ModifierSystem.Grant(campaign, liberator, "mainland_return_momentum", 365);
                ModifierSystem.Grant(campaign, rival, "mainland_regime_shock", shockDays);
                var already = RalliedInWar(liberator, war.Id);
                var trickle = Math.Min(1, Math.Max(0, maxRally - already));
                var trickleProv = firstSafeProvince > 0 ? firstSafeProvince : firstHeldProvince;
                var trickleState = firstSafeState > 0 ? firstSafeState : firstHeldState;
                if (trickle > 0 && trickleProv > 0)
                {
                    SpawnVolunteerDivisions(campaign, war, liberator, trickleProv, trickleState, trickle, already);
                    changed = true;
                }
            }
            var defectionCeiling = EvaluateStraitPosture(campaign, liberator, rival) == CrossStraitPosture.AsymmetricCrisis
                ? 0.22f
                : 0.18f;
            if (totalHeld >= 8 && heldByState.Count >= 6 && rival.Stability <= defectionCeiling)
            {
                if (TriggerProvincialDefection(campaign, scratch, war, liberator, rival, map, control, liberatorCultures, heldByState, activeBattleProvinces))
                    changed = true;
            }
        }

        if (changed)
        {
            Warfare.MarkIndexStale(campaign);
            FrontPlanner.ReassignAi(campaign, war);
            campaign.MarkStrategicChange();
        }
        return true;
    }

    private static int PickSpawnProvince(Campaign campaign, int stateMapIndex, ushort libMap, ushort rivalMap,
        HashSet<int> activeBattleProvinces, int fallbackProvince)
    {
        var map = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        if (map is null || control is null || stateMapIndex <= 0) return fallbackProvince;
        var (seatProvince, _) = MilitiaCore.Seat(map, control, libMap, (ushort)stateMapIndex);
        if (seatProvince > 0 && seatProvince <= map.ProvinceCount &&
            map.ProvinceToNation[seatProvince] == rivalMap &&
            control[seatProvince] == libMap &&
            !activeBattleProvinces.Contains(seatProvince))
            return seatProvince;
        return fallbackProvince;
    }

    private static void SpawnVolunteerDivisions(Campaign campaign, StrategicWar war, Nation liberator,
        int provinceId, int stateMapIndex, int count, int alreadyRallied)
    {
        if (count <= 0 || provinceId <= 0 || campaign.ProvinceMap is null) return;
        var pos = campaign.ProvinceMap.CenterOf(provinceId);
        var armies = campaign.Armies;
        var maxId = int.MinValue;
        for (var i = 0; i < armies.Count; i++)
            if (armies[i].Id > maxId) maxId = armies[i].Id;
        var nextId = armies.Count == 0 ? 1 : maxId + 1;
        var expire = campaign.ElapsedDays + 3650;
        var armyGroupId = -1;
        var groups = campaign.ArmyGroups;
        for (var i = 0; i < groups.Count; i++)
            if (groups[i].NationId == liberator.Id) { armyGroupId = groups[i].Id; break; }

        for (var i = 0; i < count; i++)
        {
            var slot = alreadyRallied + i + 1;
            var template = (slot % 4 == 0) ? "mechanized_corps" : (slot % 3 == 0) ? "motorized_corps" : "line_infantry";
            var personnel = Math.Max(5000, UnitTypeCatalog.Get(template).RecruitmentPersonnel);
            campaign.Armies.Add(new Army
            {
                Id = nextId + i,
                NationId = liberator.Id,
                StateMapIndex = stateMapIndex,
                ProvinceId = provinceId,
                ArmyGroupId = armyGroupId,
                AssignedWarId = war.Id,
                Name = $"{slot}. {liberator.Tag} Liberation Corps",
                TemplateId = template,
                Divisions = 1f,
                Personnel = personnel,
                MaxPersonnel = personnel,
                Strength = 1f,
                Organisation = 0.90f,
                Equipment = 0.92f,
                Experience = 0.24f,
                Cohesion = 0.86f,
                SupplyLevel = 0.95f,
                OrderKind = ArmyOrder.AdvanceFront,
                PlanActive = false,
                Latitude = pos.Latitude,
                Longitude = pos.Longitude
            });
            liberator.DecisionAvailableDay[UnitPrefix + war.Id + "/" + slot] = expire;
            Count("volunteer_division_rallied");
        }
    }

    /// <summary>
    /// When a rival regime's domestic stability has collapsed (&lt;= 0.18, or &lt;= 0.22 in
    /// AsymmetricCrisis) while the returning republic holds a broad mainland foothold
    /// (&gt;= 8 provinces across &gt;= 6 states), one adjacent smaller kindred province/region
    /// rises up — wavering garrisons may defect, and provinces with no active battle and no
    /// remaining loyal defenders flip to the liberator. Fires at most once per state per war.
    /// </summary>
    private static bool TriggerProvincialDefection(Campaign campaign, SideScratch scratch, StrategicWar war, Nation liberator, Nation rival,
        ProvinceTopology map, ushort[] control, HashSet<string> liberatorCultures, Dictionary<int, int> heldByState,
        HashSet<int> activeBattleProvinces)
    {
        var heldStateCount = heldByState.Count;
        if (heldStateCount < 6) return false;
        var libMap = (ushort)liberator.MapIndex;
        var rivalMap = (ushort)rival.MapIndex;
        // Find rival states directly bordering provinces already held by the liberator.
        var adjacentStates = scratch.AdjacentStates;
        adjacentStates.Clear();
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (control[p] != libMap || map.IsWater(p)) continue;
            foreach (var next in map.LandNeighbors[p])
            {
                if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                if (map.ProvinceToNation[next] != rivalMap || control[next] != rivalMap) continue;
                var st = map.ProvinceToState[next];
                if (st > 0) adjacentStates.Add(st);
            }
        }
        if (adjacentStates.Count == 0) return false;

        var defectionStates = scratch.DefectionStates;
        defectionStates.Clear();
        defectionStates.AddRange(campaign.StatesOf(rival));
        defectionStates.Sort(CompareStateForDefection);

        ScenarioState chosen = null;
        for (var i = 0; i < defectionStates.Count; i++)
        {
            var state = defectionStates[i];
            if (!adjacentStates.Contains(state.MapIndex)) continue;
            // Each province can declare provincial defection at most once per war (no repeat spam).
            if (liberator.DecisionAvailableDay.ContainsKey(DefectionPrefix + war.Id + "/" + state.MapIndex)) continue;
            // Keep the rival's capital for the final push unless 4+ mainland states are already contested.
            if (state.MapIndex == rival.CapitalStateMapIndex && heldStateCount < 4) continue;
            var kin = liberatorCultures.Count > 0 ? WarAims.CultureShare(state, liberatorCultures) : 0f;
            var claimed = TerritorialClaims.TitleOf(campaign, liberator, state) != TerritorialTitle.None;
            if (!claimed && kin < 0.45f) continue;
            chosen = state;
            break;
        }
        if (chosen is null) return false;

        // Deterministic probability scales with how deeply the rival's stability has collapsed.
        var collapseDepth = M.Clamp01((0.30f - rival.Stability) / 0.20f);
        var chance = 0.28f + collapseDepth * 0.42f;
        if (HasUndergroundSpirit(liberator)) chance += 0.12f;
        var posture = EvaluateStraitPosture(campaign, liberator, rival);
        if (posture == CrossStraitPosture.AsymmetricCrisis || posture == CrossStraitPosture.AmphibiousReclamation)
            chance += 0.08f;
        else if (posture == CrossStraitPosture.DiplomaticDetente)
            chance -= 0.06f;
        if (rival.IsHuman) chance *= 0.65f;
        if (Roll(campaign.Seed, chosen.MapIndex, war.Id, campaign.ElapsedDays) >= MathF.Min(0.85f, chance))
            return false;

        // Gather rival-controlled provinces of the defecting region without active battles.
        var candidateProvinces = scratch.CandidateProvinces;
        candidateProvinces.Clear();
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.IsWater(p) || map.ProvinceToState[p] != chosen.MapIndex ||
                map.ProvinceToNation[p] != rivalMap || control[p] != rivalMap) continue;
            if (activeBattleProvinces.Contains(p)) continue;
            candidateProvinces.Add(p);
        }
        if (candidateProvinces.Count == 0) return false;

        // 1. Resolve garrison defections FIRST so loyal garrisons are never stranded on flipped tiles.
        var garrisonOdds = rival.IsHuman ? 0.18f : 0.38f;
        var maxGarrisonDefections = rival.IsHuman ? 2 : 4;
        var defected = 0;
        var loyalProvinces = scratch.LoyalProvinces;
        loyalProvinces.Clear();
        var armies = campaign.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var army = armies[i];
            if (army.EmbarkedFleetId >= 0 || army.Strength <= 0.05f || !candidateProvinces.Contains(army.ProvinceId))
                continue;
            if (!war.Opposes(liberator.Id, army.NationId)) continue;
            var canDefect = army.NationId == rival.Id && !army.InBattle &&
                            defected < maxGarrisonDefections &&
                            (rival.Stability <= 0.22f || army.Cohesion < 0.70f || army.SupplyLevel < 0.75f);
            if (canDefect && Roll(campaign.Seed, army.Id, liberator.Id, campaign.ElapsedDays) < garrisonOdds)
            {
                army.NationId = liberator.Id;
                army.AssignedWarId = war.Id;
                army.ArmyGroupId = -1;
                army.FrontLineId = -1;
                army.OrderKind = ArmyOrder.AdvanceFront;
                army.Route.Clear();
                army.PlannedRoute.Clear();
                army.Cohesion = M.Clamp01(army.Cohesion - 0.10f);
                defected++;
            }
            else
            {
                loyalProvinces.Add(army.ProvinceId);
            }
        }

        // 2. Flip only provinces with no active battle AND no remaining loyal defenders.
        var flipped = 0;
        foreach (var p in candidateProvinces)
        {
            if (loyalProvinces.Contains(p)) continue;
            war.OriginalController.TryAdd(p, control[p]);
            if (campaign.SetProvinceController(p, libMap)) flipped++;
        }
        if (flipped == 0 && defected == 0) return false;

        liberator.DecisionAvailableDay[DefectionPrefix + war.Id + "/" + chosen.MapIndex] = campaign.ElapsedDays + 3650;
        war.FrontDirty = true;

        campaign.Chronicle.Record(campaign, "mainland_defection", liberator.Id, rival.Id, subject: chosen.Name);
        Count("provincial_defection");
        if (defected > 0) Count("provincial_garrison_defected");
        return true;
    }

    private static void SurgeRivalRadicalism(Campaign campaign, Nation rival, Dictionary<int, int> heldByState, float delta)
    {
        if (delta <= 0f) return;
        var states = campaign.StatesOf(rival);
        for (var i = 0; i < states.Length; i++)
        {
            var state = states[i];
            var scale = heldByState is not null && heldByState.ContainsKey(state.MapIndex) ? 1f : 0.20f;
            state.Radicalism = M.Clamp01(state.Radicalism + delta * scale);
        }
    }

    /// <summary>
    /// AI strategic orchestration for smaller divided-homeland contenders (across sea or divided land
    /// borders): waits until readiness is sufficient and a strategic crisis window opens, then
    /// launches the homeland reclamation campaign through standard diplomacy rules.
    /// </summary>
    private static IEnumerable<int> ReviewAiMainlandReturn(Campaign campaign)
    {
        var defines = campaign.Defines;
        if (!defines.AiMayDeclareWar) yield break;
        if (campaign.ElapsedDays < Math.Max(30, defines.AiMainlandReturnEarliestDay)) yield break;
        var cadence = Math.Max(7, defines.AiMainlandReturnReviewDays);

        var scratch = GetScratch(campaign);
        var outer = scratch.OuterNations;
        CopySortedNations(campaign, outer);

        var candidatesPrepared = false;
        var candidateNations = scratch.CandidateNations;
        var ourCultures = scratch.OurCultures;

        var ticked = 0;
        for (var i = 0; i < outer.Count; i++)
        {
            if (++ticked % 16 == 0 || Campaign.StageOverdue()) yield return 0;
            var nation = outer[i];
            if (nation.IsHuman || nation.Population <= 0 || nation.IsSubject || nation.Landless ||
                nation.Movement is not null || WarAims.IsRebel(nation))
                continue;
            if ((campaign.ElapsedDays + nation.Id * 5) % cadence != 0) continue;
            if (nation.DecisionAvailableDay.GetValueOrDefault(AiReviewKey) > campaign.ElapsedDays) continue;
            if (AiStatecraft.InAnyWar(campaign, nation.Id)) continue;
            if (nation.Stability < 0.50f || nation.WarSupport < 0.55f) continue;

            FillCultures(nation, ourCultures);
            if (ourCultures.Count == 0) continue;

            if (!candidatesPrepared || candidateNations.Count != campaign.Nations.Count)
            {
                CopySortedNations(campaign, candidateNations);
                candidatesPrepared = true;
            }

            // Find any larger kindred rival holding our homeland cores (sea-separated or land-bordered).
            var ownStateCount = campaign.StatesOf(nation).Length;
            if (ownStateCount <= 0) continue;
            Nation rival = null;
            ScenarioState recovery = null;
            for (var j = 0; j < candidateNations.Count; j++)
            {
                var candidate = candidateNations[j];
                if (candidate.Id == nation.Id || candidate.Population <= nation.Population ||
                    candidate.IsSubject || candidate.Landless || candidate.Movement is not null || WarAims.IsRebel(candidate))
                    continue;
                if (candidate.IsHuman && !defines.AiMayAttackPlayer) continue;
                var candStatesCount = campaign.StatesOf(candidate).Length;
                if (candStatesCount < 4 || candStatesCount <= ownStateCount) continue;
                if (PeacefulFormation.CulturalAffinity(nation, candidate) < 0.75f) continue;
                if (!SharesCultureWith(ourCultures, candidate)) continue;
                var goal = BestHomelandGoal(campaign, nation, candidate);
                if (goal is null) continue;
                if (!CampaignDiplomacy.CanDeclareWarQuiet(campaign, nation.Id, candidate.Id)) continue;
                rival = candidate;
                recovery = goal;
                break;
            }
            if (rival is null || recovery is null) continue;

            nation.DecisionAvailableDay[AiReviewKey] = campaign.ElapsedDays + cadence;

            var separatedBySea = IsSeparatedBySea(campaign, nation, rival);
            if (separatedBySea)
            {
                var hulls = 0;
                var fleets = campaign.Fleets;
                for (var f = 0; f < fleets.Count; f++)
                {
                    var fleet = fleets[f];
                    if (fleet.NationId == nation.Id && fleet.Hulls > 0) hulls += fleet.Hulls;
                }
                if (hulls < Math.Max(20, defines.AiMainlandReturnMinimumHulls)) continue;
            }

            var readyDivisions = 0;
            var armies = campaign.Armies;
            for (var a = 0; a < armies.Count; a++)
            {
                var army = armies[a];
                if (army.NationId == nation.Id && army.TemplateId != MilitiaCore.TemplateId &&
                    army.ProvinceId > 0 && army.EmbarkedFleetId < 0 && army.Strength > 0.35f)
                    readyDivisions++;
            }
            if (readyDivisions < Math.Max(4, defines.AiMainlandReturnMinimumDivisions)) continue;
            if (!AiStatecraft.CanStageWar(campaign, nation, rival)) continue;

            // Evaluate posture and strategic window without allocating: seeded FNV-1a archetype
            // branches across four divergent postures while answering live strain.
            var chance = AiLaunchChance(campaign, nation, rival, out var posture);
            var rivalAtWar = AiStatecraft.InAnyWar(campaign, rival.Id);
            var rivalStrained = IsRivalStrained(campaign, rival);
            var relation = CampaignDiplomacy.Peek(campaign, nation.Id, rival.Id);
            var preparedDoctrine = HasGlorySpirit(nation);

            if (separatedBySea &&
                posture == CrossStraitPosture.FortifiedContainment &&
                !HasArmadaSpirit(nation) &&
                DecisionCatalog.Find("twn_strait_armada") is { } armadaDecision &&
                !DecisionCatalog.IsBlocked(campaign, nation, armadaDecision))
            {
                new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = armadaDecision.Id }.Apply(campaign);
            }
            else if (posture == CrossStraitPosture.AsymmetricCrisis)
            {
                if (!HasUndergroundSpirit(nation) &&
                    DecisionCatalog.Find("twn_mainland_underground") is { } underDecision &&
                    !DecisionCatalog.IsBlocked(campaign, nation, underDecision))
                {
                    new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = underDecision.Id }.Apply(campaign);
                }
                if (!rivalAtWar && relation is not null && relation.Tension < 76f)
                    relation.Tension = MathF.Min(78f, relation.Tension + 0.45f);
            }
            else if (posture == CrossStraitPosture.DiplomaticDetente && !rivalAtWar && !rivalStrained && relation is not null)
            {
                if (relation.Tension > 24f)
                    relation.Tension = MathF.Max(15f, relation.Tension - 0.35f);
            }

            if (chance <= 0f) continue;

            if (Roll(campaign.Seed, nation.Id, rival.Id, campaign.ElapsedDays) >= chance) continue;

            // Take reclamation doctrine through the normal paid decision command if available.
            if (!preparedDoctrine && DecisionCatalog.Find("twn_project_national_glory") is { } gloryDecision &&
                !DecisionCatalog.IsBlocked(campaign, nation, gloryDecision))
            {
                new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = gloryDecision.Id }.Apply(campaign);
            }

            var war = CampaignDiplomacy.DeclareWar(campaign, nation.Id, rival.Id, recovery.MapIndex, "reconquest");
            if (war is not null)
            {
                campaign.Chronicle.Record(campaign, "mainland_campaign_launched", nation.Id, rival.Id, subject: recovery.Name);
                Count("ai_campaign_launched");
            }
        }
    }

    private static bool HasFriendlyPacificPartner(Campaign campaign, Nation nation, Nation rival)
    {
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var other = nations[i];
            if (other.Id == nation.Id || other.Id == rival.Id || other.Population <= 0 || other.IsSubject) continue;
            var toUs = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            if (toUs is null) continue;
            if (toUs.Stance == DiplomaticStance.Allied || toUs.GuaranteedBy(other.Id) || toUs.Opinion >= 35f)
            {
                var toRival = CampaignDiplomacy.Peek(campaign, other.Id, rival.Id);
                if (toRival is { Stance: DiplomaticStance.Rival } || (toRival?.Opinion ?? 0f) <= -20f)
                    return true;
            }
        }
        return false;
    }

    private static void PetitionRegionalAllies(Campaign campaign, CampaignReclamationScratch scratch,
        StrategicWar war, Nation requester, Nation rival)
    {
        if (campaign.ElapsedDays % 14 != (requester.Id % 14)) return;
        var donors = scratch.AllyDonors;
        CopySortedNations(campaign, donors);
        for (var i = 0; i < donors.Count; i++)
        {
            var donor = donors[i];
            if (donor.Id == requester.Id || donor.Id == rival.Id || donor.IsHuman || donor.Population <= 0 || donor.IsSubject)
                continue;
            if (war.Involves(donor.Id)) continue;
            var pleaKey = AllyPleaPrefix + war.Id + "/" + donor.Id;
            if (requester.DecisionAvailableDay.GetValueOrDefault(pleaKey) > campaign.ElapsedDays) continue;

            var assessment = WarInterventions.CanSubmit(campaign, requester.Id, donor.Id, war.Id);
            if (!assessment.Allowed || !assessment.AiWouldGrant) continue;

            requester.DecisionAvailableDay[pleaKey] = campaign.ElapsedDays + 90;
            new RequestWarInterventionCommand
            {
                IssuerId = requester.Id,
                DonorId = donor.Id,
                WarId = war.Id
            }.Apply(campaign);
            Count("ally_intervention_requested");
            break;
        }
    }

    /// <summary>Read-only diagnostic explaining why an AI contender is or is not ready to launch.</summary>
    public static string ExplainAiReadiness(Campaign campaign, Nation nation, Nation rival)
    {
        if (campaign is null || nation is null || rival is null) return "null";
        var defines = campaign.Defines;
        if (!defines.MainlandReclamationEnabled) return "disabled";
        if (!defines.AiMayDeclareWar) return "ai_war_off";
        if (campaign.ElapsedDays < Math.Max(30, defines.AiMainlandReturnEarliestDay)) return "too_early";
        if (AiStatecraft.InAnyWar(campaign, nation.Id)) return "in_war";
        if (nation.Stability < 0.50f) return $"low_stab({nation.Stability:0.00})";
        if (nation.WarSupport < 0.55f) return $"low_ws({nation.WarSupport:0.00})";
        if (nation.Population <= 0 || rival.Population <= 0 || nation.Population >= rival.Population) return "not_smaller";
        var ownStates = campaign.StatesOf(nation).Length;
        var rivalStates = campaign.StatesOf(rival).Length;
        if (ownStates <= 0 || rivalStates < 4 || rivalStates <= ownStates) return "rival_too_small";
        if (PeacefulFormation.CulturalAffinity(nation, rival) < 0.75f) return "low_affinity";
        var cultures = _pairCultures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FillCultures(nation, cultures);
        if (!SharesCultureWith(cultures, rival)) return "no_shared_culture";
        if (!HasAnyHomeland(campaign, nation, rival)) return "no_homeland";
        if (!AnnexationAmbition.IsReunification(campaign, nation, rival)) return "not_reunification";
        if (!CampaignDiplomacy.CanDeclareWar(campaign, nation.Id, rival.Id, out var why)) return $"cant_declare({why})";
        var separatedBySea = IsSeparatedBySea(campaign, nation, rival);
        var hulls = 0;
        var fleets = campaign.Fleets;
        for (var i = 0; i < fleets.Count; i++)
        {
            var f = fleets[i];
            if (f.NationId == nation.Id && f.Hulls > 0) hulls += f.Hulls;
        }
        if (separatedBySea && hulls < Math.Max(20, defines.AiMainlandReturnMinimumHulls)) return $"few_hulls({hulls})";
        var divs = 0;
        var armies = campaign.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var a = armies[i];
            if (a.NationId == nation.Id && a.TemplateId != MilitiaCore.TemplateId &&
                a.ProvinceId > 0 && a.EmbarkedFleetId < 0 && a.Strength > 0.35f)
                divs++;
        }
        if (divs < Math.Max(4, defines.AiMainlandReturnMinimumDivisions)) return $"few_divs({divs})";
        if (!AiStatecraft.CanStageWar(campaign, nation, rival)) return "cant_stage";
        var rivalAtWar = AiStatecraft.InAnyWar(campaign, rival.Id);
        var rivalStrained = IsRivalStrained(campaign, rival);
        var rel = CampaignDiplomacy.Peek(campaign, nation.Id, rival.Id);
        var straitCrisis = rel is not null && (rel.Tension >= 70f || Escalation.StageOf(rel) >= TensionStage.Crisis);
        var glory = HasGlorySpirit(nation);
        var pacific = HasFriendlyPacificPartner(campaign, nation, rival);
        var launch = AiLaunchChance(campaign, nation, rival, out var posture);
        return $"READY(posture={posture},launch={launch:0.00},sea={separatedBySea},rivalWar={rivalAtWar},strain={rivalStrained},crisis={straitCrisis},tension={rel?.Tension ?? 0f:0.0},glory={glory},pacific={pacific},divs={divs},hulls={hulls})";
    }

    private static bool IsRivalStrained(Campaign campaign, Nation rival) =>
        rival.Stability < 0.38f || rival.WarSupport < 0.38f ||
        (rival.DomesticCrisis?.CoupPressure ?? 0f) > 0.55f ||
        (campaign.FeatureEnabled("public_debt") && rival.DebtCondition >= DebtCondition.Default);

    /// <summary>
    /// Classifies the current Cross-Strait trajectory across four seeded and reactive paths
    /// without heap allocations. Re-evaluated deterministically over decades and centuries.
    /// </summary>
    public static CrossStraitPosture EvaluateStraitPosture(Campaign campaign, Nation liberator, Nation rival)
    {
        if (campaign is null || liberator is null || rival is null)
            return CrossStraitPosture.FortifiedContainment;

        var decade = GeopoliticalCycle.DecadeIndex(campaign.ElapsedDays);
        var century = GeopoliticalCycle.CenturyIndex(campaign.ElapsedDays);
        var baseHash = GeopoliticalCycle.HashPairEpoch(campaign.Seed, liberator, rival, decade + century * 13, 0x57A1);
        var primaryBucket = (int)(baseHash % 4u);

        var sReclaim = (primaryBucket == 0 ? 0.48f : 0f)
                     + GeopoliticalCycle.HashToUnit(GeopoliticalCycle.Fnv1aMixInt(baseHash, 1)) * 0.18f;
        var sContain = (primaryBucket == 1 ? 0.48f : 0f)
                     + GeopoliticalCycle.HashToUnit(GeopoliticalCycle.Fnv1aMixInt(baseHash, 2)) * 0.18f;
        var sDetente = (primaryBucket == 2 ? 0.48f : 0f)
                     + GeopoliticalCycle.HashToUnit(GeopoliticalCycle.Fnv1aMixInt(baseHash, 3)) * 0.18f;
        var sCrisis = (primaryBucket == 3 ? 0.48f : 0f)
                    + GeopoliticalCycle.HashToUnit(GeopoliticalCycle.Fnv1aMixInt(baseHash, 4)) * 0.18f;

        var rivalAtWar = AiStatecraft.InAnyWar(campaign, rival.Id);
        var rivalStrained = IsRivalStrained(campaign, rival);
        var pacificBacking = HasFriendlyPacificPartner(campaign, liberator, rival);
        var relation = CampaignDiplomacy.Peek(campaign, liberator.Id, rival.Id);
        var straitCrisis = relation is not null &&
                           (relation.Tension >= 70f || Escalation.StageOf(relation) >= TensionStage.Crisis);
        var coupPressure = liberator.DomesticCrisis?.CoupPressure ?? 0f;
        var temp = AiTemperament.Of(campaign, liberator);
        var era = GeopoliticalCycle.CurrentEraRegime(campaign);

        if (rivalAtWar || rivalStrained)
        {
            sReclaim += 0.24f;
            sCrisis += 0.14f;
            sDetente -= 0.22f;
        }
        if (pacificBacking)
            sContain += 0.16f + (temp.Foresight - 0.5f) * 0.18f;
        if (straitCrisis || coupPressure > 0.25f)
        {
            sCrisis += 0.26f;
            sDetente -= 0.20f;
        }
        if (era == GlobalEraRegime.RevisionistTide) sReclaim += 0.12f;
        else if (era == GlobalEraRegime.PostImperialTurbulence) sCrisis += 0.12f;
        else if (era == GlobalEraRegime.MultipolarConcert && !rivalAtWar && !straitCrisis) sDetente += 0.18f;
        else if (era == GlobalEraRegime.ArmedDetente) sContain += 0.12f;

        if (!rivalAtWar && !rivalStrained && !straitCrisis && GeopoliticalCycle.IsDetenteEra(campaign))
            sDetente += 0.22f;
        sReclaim += (temp.Opportunism - 0.5f) * 0.18f;

        var best = CrossStraitPosture.AmphibiousReclamation;
        var bestScore = sReclaim;
        if (sContain > bestScore) { bestScore = sContain; best = CrossStraitPosture.FortifiedContainment; }
        if (sDetente > bestScore) { bestScore = sDetente; best = CrossStraitPosture.DiplomaticDetente; }
        if (sCrisis > bestScore) { best = CrossStraitPosture.AsymmetricCrisis; }
        return best;
    }

    /// <summary>
    /// Evaluates AI Cross-Strait offensive launch probability (0..0.90) under the active seeded
    /// <see cref="CrossStraitPosture"/> and live geopolitical conditions.
    /// </summary>
    public static float AiLaunchChance(Campaign campaign, Nation nation, Nation rival, out CrossStraitPosture posture)
    {
        posture = EvaluateStraitPosture(campaign, nation, rival);
        if (campaign is null || nation is null || rival is null) return 0f;

        var defines = campaign.Defines;
        var rivalAtWar = AiStatecraft.InAnyWar(campaign, rival.Id);
        var rivalStrained = IsRivalStrained(campaign, rival);
        var relation = CampaignDiplomacy.Peek(campaign, nation.Id, rival.Id);
        var straitCrisis = relation is not null &&
                           (relation.Tension >= 70f || Escalation.StageOf(relation) >= TensionStage.Crisis);
        var clashesReached = relation is not null && Escalation.StageOf(relation) >= TensionStage.Clashes;
        var preparedDoctrine = HasGlorySpirit(nation);

        var windowOpen = posture switch
        {
            CrossStraitPosture.AmphibiousReclamation =>
                rivalStrained || (rivalAtWar && rival.Stability < 0.48f) || (straitCrisis && rival.Stability < 0.45f),
            CrossStraitPosture.FortifiedContainment =>
                rivalStrained || (rivalAtWar && rival.Stability < 0.48f),
            CrossStraitPosture.DiplomaticDetente =>
                (rivalAtWar && rival.Stability < 0.48f) || (rivalStrained && clashesReached),
            CrossStraitPosture.AsymmetricCrisis =>
                rivalStrained || (rivalAtWar && rival.Stability < 0.48f) || (straitCrisis && rival.Stability < 0.45f) ||
                ((nation.DomesticCrisis?.CoupPressure ?? 0f) > 0.28f && rival.Stability < 0.45f),
            _ => false
        };
        if (!windowOpen) return 0f;

        var chance = Math.Clamp(defines.AiMainlandReturnChance, 0f, 1f);
        if (rivalAtWar) chance += 0.28f;
        if (rivalStrained) chance += 0.22f;
        if (preparedDoctrine) chance += 0.16f;
        if (HasUndergroundSpirit(nation)) chance += 0.10f;
        if (straitCrisis) chance += 0.10f;

        chance += posture switch
        {
            CrossStraitPosture.AmphibiousReclamation => 0.14f,
            CrossStraitPosture.FortifiedContainment => -0.14f,
            CrossStraitPosture.DiplomaticDetente => -0.22f,
            CrossStraitPosture.AsymmetricCrisis => 0.08f,
            _ => 0f
        };
        chance *= AiTemper.EraAggressionModifier(campaign, nation);
        return Math.Clamp(chance, 0f, 0.90f);
    }

    private static float Roll(int seed, int a, int b, int day)
    {
        unchecked
        {
            var hash = (uint)seed * 2654435761u ^ (uint)a * 2246822519u ^ (uint)b * 3266489917u ^ (uint)day * 668265263u;
            hash ^= hash >> 15; hash *= 2246822519u;
            hash ^= hash >> 13; hash *= 3266489917u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
