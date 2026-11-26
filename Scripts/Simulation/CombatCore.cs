using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How far one side's day of fighting may run above or below what its numbers say, as a
    /// share: the fortunes of war. 0.18 lets a lucky day hit about a fifth harder and a bad one
    /// a fifth softer, rarely both at once; zero makes every battle a sum, as it was.
    /// </summary>
    public float CombatFortune { get; set; } = 0.18f;

    /// <summary>
    /// The fortune of a whole battle for one side, 1 ± this, triangular, rolled once when it
    /// begins: the weather that holds, the commander who reads the ground, the bridge that was
    /// not blown. A day's fortune averages out over a battle of twenty days; this does not, and
    /// it is what lets an outnumbered side win a battle it should have lost.
    /// </summary>
    public float CombatBattleFortune { get; set; } = 0.22f;

    /// <summary>The share of battles that turn on one decisive moment - a brilliant stroke or a blunder.</summary>
    public float CombatDecisiveChance { get; set; } = 0.06f;

    /// <summary>How far that moment swings the side it falls to, up or down.</summary>
    public float CombatDecisiveSwing { get; set; } = 0.35f;
}

public static partial class Warfare
{
    /// <summary>
    /// How land battles have ended this session, by outcome. Not campaign state and not
    /// saved - it exists so the bench and the console can answer "is the defence holding?"
    /// with a number instead of an impression.
    /// </summary>
    public static readonly Dictionary<string, int> OutcomeTally = new(StringComparer.Ordinal);

    /// <summary>
    /// The same tally split by war. The worldwide one turned out to be no use for judging
    /// any single front: with a hundred and forty wars running, most of them between small
    /// states with no divisions at all, it says far more about how the rest of the world
    /// fights than about the front anybody is looking at.
    /// </summary>
    public static readonly Dictionary<int, Dictionary<string, int>> OutcomeTallyByWar = new();

    private static void Tally(int warId, string outcome)
    {
        OutcomeTally[outcome] = OutcomeTally.GetValueOrDefault(outcome) + 1;
        if (!OutcomeTallyByWar.TryGetValue(warId, out var perWar))
            OutcomeTallyByWar[warId] = perWar = new Dictionary<string, int>(StringComparer.Ordinal);
        perWar[outcome] = perWar.GetValueOrDefault(outcome) + 1;
    }

    // One campaign's army index. Warfare made three megabytes of garbage a campaign day,
    // nearly all of it these indexes and the little arrays LINQ handed back. The lists are
    // kept and emptied instead of rebuilt. They used to be one set for the process: the
    // next campaign's rebuild cleared the first campaign's war buckets, province slots and
    // id table, and a fresh flag left by that rebuild let the first campaign read the
    // wrong armies. Two peers could run for years before a quiet day and a landing met,
    // and then one division's rounding never came back together. Each campaign now owns
    // its containers. Inside one campaign they are still cleared and filled again.
    // The live tick stays single-threaded. ActiveIndex only names the index of the path
    // now running; it does not allow Parallel.For around the tick.
    // A capture during the day's battles patches the province slots those formations
    // left and entered. It does not rebuild this whole index until those battles have
    // read who is standing where. The battles themselves stay in order: they still
    // share this campaign's attacker and defender lists and its support exposure.
    //
    // Province slots are an array, not a dictionary: the index is rebuilt every step, and
    // a dictionary rebuilt every step was a hash lookup per division plus an emptying of
    // every list any division had ever stood in. Only the slots filled last time are
    // emptied. ById is filled when a battle first asks, from the armies this index saw,
    // so the day's battles share one table.
    private sealed class ArmyIndex
    {
        public readonly Dictionary<int, List<Army>> ByWar = new();
        public List<Army>[] ByProvince = Array.Empty<List<Army>>();
        public readonly List<int> ProvincesHeld = new();
        public readonly Dictionary<int, Army> ById = new();
        public readonly List<Army> IndexedArmies = new();
        public readonly Dictionary<int, StrategicWar> WarById = new();
        public readonly Dictionary<ushort, int> HomeProvinces = new();
        public readonly Dictionary<long, int> OccupiedPairs = new();
        public readonly HashSet<ushort> AttackerMaps = new();
        public readonly HashSet<ushort> DefenderMaps = new();
        public readonly HashSet<ushort> BelligerentMaps = new();
        // One membership snapshot for the failed searches of this campaign's current pass.
        // Another campaign keeps its own and cannot replace this one.
        public ushort[] BelligerentSnapshot;
        public readonly Dictionary<long, (float Power, float Weight)> CoverCache = new();
        public long CoverStep = -1;
        public int CoverWarId = -1;
        public int CoverMapRevision = -1;
        // Whether a war's enemy holds a defending country's own ground, per war and country,
        // for one step (CombatOpportunitySelection.GroundToTakeBack).
        public readonly Dictionary<long, bool> LostGround = new();
        public long LostGroundStep = -1;
        public int LostGroundRevision = -1;
        public readonly Dictionary<int, ScenarioState> RegionByIndex = new();
        public int RegionsCount = -1;
        public int RegionsRevision = -1;
        public readonly Dictionary<(int WarId, int ProvinceId), LandBattle> ActiveBattlesByKey = new();
        public int ActiveBattlesCount = -1;
        public bool ActiveBattlesDirty = true;
        public readonly Dictionary<int, FailedOperationalRoute> NoRoute = new();
        public readonly Dictionary<int, HeldAdvanceRoute> AdvanceRoutes = new();
        public readonly List<StrategicWar> Stale = new();
        public readonly HashSet<int> Moved = new();
        public bool ByIdStale = true;
        public bool ProvinceStale = true;
        // While the day's battles are reading occupancy, a capture must not rebuild
        // the war buckets and every province slot. Province slots are patched instead.
        public bool DeferProvinceRebuild;
        public bool ProvinceSlotsCurrent;
        public int FullRebuilds;
        public readonly Dictionary<int, List<Army>> ProvinceScratch = new();
        public readonly HashSet<int> ProvinceScratchFilled = new();
    }

    private static readonly ConditionalWeakTable<Campaign, ArmyIndex> ArmyIndexes = new();
    private static ArmyIndex ActiveIndex;
    private static readonly Dictionary<int, Army> NoArmyById = new();

    private static ArmyIndex UseArmyIndex(Campaign campaign) =>
        ActiveIndex = ArmyIndexes.GetOrCreateValue(campaign);

    private static ArmyIndex IndexOf(Campaign campaign) =>
        campaign is not null && ArmyIndexes.TryGetValue(campaign, out var index) ? index : null;

    /// <summary>A capture moved formations in this campaign. Another campaign's capture does not set the flag.</summary>
    internal static void MarkIndexStale(Campaign campaign)
    {
        if (campaign is not null && ArmyIndexes.TryGetValue(campaign, out var index))
        {
            index.ProvinceStale = true;
            index.ProvinceSlotsCurrent = false;
            index.ActiveBattlesDirty = true;
        }
    }

    // Battle buckets, reused across the battles of ONE campaign. The sides and the
    // presence list are still one set for that campaign: the next battle clears them.
    // The line, the reserve, the reinforcement queue, the frontage scratch and the
    // damage rows are not. Those live on a scratch keyed by battle id, the same way
    // the country sets do. The next battle clears its own scratch. Another campaign's
    // board cannot clear this one. These lists used to be one set for the process.
    // The live tick stays single-threaded. ActiveBuckets names the board of the path
    // now running. ActiveRows names that path's battle, and nothing else.
    private sealed class BattleScratch
    {
        public readonly List<Army> Reinforcements = new();
        public readonly List<Army> LineCandidates = new();
        public readonly List<Army> AttackerLine = new();
        public readonly List<Army> AttackerReserve = new();
        public readonly List<Army> DefenderLine = new();
        public readonly List<Army> DefenderReserve = new();
        public readonly List<(float Personnel, float Casualties)> Damage = new();
    }

    private sealed class BattleBuckets
    {
        public readonly List<Army> Present = new();
        public readonly List<Army> Attacking = new();
        public readonly List<Army> Defending = new();
        // One scratch per battle id. The next battle of this campaign does not clear it.
        public readonly Dictionary<int, BattleScratch> ByBattle = new();
        // Countries standing in one battle. Keyed by battle id so the next battle of this
        // campaign clears its own pair, and another campaign's board cannot clear this one.
        public readonly Dictionary<int, HashSet<int>> AttackerNations = new();
        public readonly Dictionary<int, HashSet<int>> DefenderNations = new();
        public readonly List<int> DroppedNationSets = new();
    }

    private static readonly ConditionalWeakTable<Campaign, BattleBuckets> BattleBoards = new();
    private static BattleBuckets ActiveBuckets;
    private static BattleScratch ActiveRows;

    private static void UseBattleBuckets(Campaign campaign) =>
        ActiveBuckets = BattleBoards.GetOrCreateValue(campaign);

    private static BattleBuckets Board(Campaign campaign) =>
        campaign is not null && BattleBoards.TryGetValue(campaign, out var board) ? board : null;

    private static BattleScratch RowsOf(BattleBuckets board, int battleId)
    {
        if (!board.ByBattle.TryGetValue(battleId, out var rows))
            board.ByBattle[battleId] = rows = new BattleScratch();
        return rows;
    }

    // Points the line, reserve, reinforcement and damage properties at this battle.
    // It does not clear another battle's scratch, and it does not shoot.
    private static void UseBattleRows(Campaign campaign, LandBattle battle)
    {
        UseBattleBuckets(campaign);
        ActiveRows = RowsOf(ActiveBuckets, battle.Id);
    }

    private static BattleScratch Rows(Campaign campaign, int battleId)
    {
        var board = Board(campaign);
        return board is not null && board.ByBattle.TryGetValue(battleId, out var rows) ? rows : null;
    }

    private static List<Army> Present => ActiveBuckets.Present;
    private static List<Army> AttackingSide => ActiveBuckets.Attacking;
    private static List<Army> DefendingSide => ActiveBuckets.Defending;
    private static List<Army> Reinforcements => ActiveRows.Reinforcements;
    private static List<Army> LineCandidates => ActiveRows.LineCandidates;
    private static List<Army> AttackerLine => ActiveRows.AttackerLine;
    private static List<Army> AttackerReserve => ActiveRows.AttackerReserve;
    private static List<Army> DefenderLine => ActiveRows.DefenderLine;
    private static List<Army> DefenderReserve => ActiveRows.DefenderReserve;

    /// <summary>Armies the last battle look found standing against the attacker in this campaign. Empty when it has not looked.</summary>
    internal static IReadOnlyList<Army> BattlePresent(Campaign campaign) =>
        Board(campaign) is { } board ? board.Present : Array.Empty<Army>();

    /// <summary>Armies the last battle look placed on the attacking side. Empty when it has not looked.</summary>
    internal static IReadOnlyList<Army> BattleAttackers(Campaign campaign) =>
        Board(campaign) is { } board ? board.Attacking : Array.Empty<Army>();

    /// <summary>Armies the last battle look placed on the defending side. Empty when it has not looked.</summary>
    internal static IReadOnlyList<Army> BattleDefenders(Campaign campaign) =>
        Board(campaign) is { } board ? board.Defending : Array.Empty<Army>();

    /// <summary>Armies the last look at this battle found waiting in its province, not yet in the battle.</summary>
    internal static IReadOnlyList<Army> BattleReinforcements(Campaign campaign, int battleId) =>
        Rows(campaign, battleId) is { } rows ? rows.Reinforcements : Array.Empty<Army>();

    /// <summary>The last side this battle's frontage split sorted. Empty when this battle has not split a side.</summary>
    internal static IReadOnlyList<Army> BattleLineCandidates(Campaign campaign, int battleId) =>
        Rows(campaign, battleId) is { } rows ? rows.LineCandidates : Array.Empty<Army>();

    /// <summary>Attackers standing in this battle's line. The same battle reuses the list. Another battle does not clear it.</summary>
    internal static IReadOnlyList<Army> BattleAttackerLine(Campaign campaign, int battleId) =>
        Rows(campaign, battleId) is { } rows ? rows.AttackerLine : Array.Empty<Army>();

    /// <summary>Attackers waiting behind this battle's line. Another campaign cannot clear the list.</summary>
    internal static IReadOnlyList<Army> BattleAttackerReserve(Campaign campaign, int battleId) =>
        Rows(campaign, battleId) is { } rows ? rows.AttackerReserve : Array.Empty<Army>();

    /// <summary>Defenders standing in this battle's line.</summary>
    internal static IReadOnlyList<Army> BattleDefenderLine(Campaign campaign, int battleId) =>
        Rows(campaign, battleId) is { } rows ? rows.DefenderLine : Array.Empty<Army>();

    /// <summary>Defenders waiting behind this battle's line.</summary>
    internal static IReadOnlyList<Army> BattleDefenderReserve(Campaign campaign, int battleId) =>
        Rows(campaign, battleId) is { } rows ? rows.DefenderReserve : Array.Empty<Army>();

    private static readonly IReadOnlyList<(float Personnel, float Casualties)> NoDamageRows =
        Array.Empty<(float Personnel, float Casualties)>();

    /// <summary>Damage rows sampled for this battle and not yet replaced by its next pass. Empty when it has not sampled.</summary>
    internal static IReadOnlyList<(float Personnel, float Casualties)> BattleDamageRows(Campaign campaign, int battleId) =>
        Rows(campaign, battleId) is { } rows ? rows.Damage : NoDamageRows;

    private static readonly HashSet<int> NoBattleNations = new();

    /// <summary>Countries on the attacking side of this battle, from the last fill. Empty when this campaign has not filled it.</summary>
    internal static IReadOnlySet<int> BattleAttackerNations(Campaign campaign, int battleId)
    {
        var board = Board(campaign);
        if (board is not null && board.AttackerNations.TryGetValue(battleId, out var set)) return set;
        return NoBattleNations;
    }

    /// <summary>Countries on the defending side of this battle. Another campaign's fill cannot clear the set.</summary>
    internal static IReadOnlySet<int> BattleDefenderNations(Campaign campaign, int battleId)
    {
        var board = Board(campaign);
        if (board is not null && board.DefenderNations.TryGetValue(battleId, out var set)) return set;
        return NoBattleNations;
    }

    /// <summary>
    /// Writes the countries standing in this battle onto this campaign's board, under this
    /// battle's id. The next battle clears its own pair. Another campaign cannot clear
    /// this one. It does not join a formation, shoot, or rewrite an order.
    /// </summary>
    internal static void FillBattleNationSets(Campaign campaign, LandBattle battle,
        IReadOnlyList<Army> attacking, IReadOnlyList<Army> defending)
    {
        if (campaign is null || battle is null || attacking is null || defending is null) return;
        UseBattleBuckets(campaign);
        var board = Board(campaign);
        var attackerNations = Nations(board.AttackerNations, battle.Id);
        attackerNations.Clear();
        for (var i = 0; i < attacking.Count; i++) attackerNations.Add(attacking[i].NationId);
        var defenderNations = Nations(board.DefenderNations, battle.Id);
        defenderNations.Clear();
        for (var i = 0; i < defending.Count; i++) defenderNations.Add(defending[i].NationId);
    }

    private static HashSet<int> Nations(Dictionary<int, HashSet<int>> table, int battleId)
    {
        if (!table.TryGetValue(battleId, out var set))
            table[battleId] = set = new HashSet<int>();
        return set;
    }

    // Ended battles leave the campaign after a month. Their country sets and their
    // line, reserve, reinforcement and damage scratch leave with them. A day whose
    // tables still name a live battle does not walk them.
    private static void ForgetEndedBattleNations(Campaign campaign)
    {
        var board = Board(campaign);
        if (board is null) return;
        var battles = campaign.Battles;
        if (board.AttackerNations.Count > battles.Count || board.DefenderNations.Count > battles.Count)
        {
            DropMissing(board.AttackerNations, battles, board.DroppedNationSets);
            DropMissing(board.DefenderNations, battles, board.DroppedNationSets);
        }
        if (board.ByBattle.Count > battles.Count)
            DropMissing(board.ByBattle, battles, board.DroppedNationSets);
    }

    private static void DropMissing<T>(Dictionary<int, T> table, List<LandBattle> battles, List<int> dropped)
    {
        dropped.Clear();
        foreach (var id in table.Keys)
        {
            var live = false;
            for (var i = 0; i < battles.Count; i++)
                if (battles[i].Id == id) { live = true; break; }
            if (!live) dropped.Add(id);
        }
        for (var i = 0; i < dropped.Count; i++) table.Remove(dropped[i]);
    }

    /// <summary>
    /// This campaign's index. Rebuilt only when a capture in this campaign has moved
    /// someone, which keeps it as truthful as walking every division and costs one pass
    /// over the army list instead of one per battle. Another campaign's rebuild does not
    /// clear these containers. A fresh index starts stale, so the first look builds it.
    /// While the day's battles are still reading occupancy, a stale index patches the
    /// province slots instead of rebuilding the war buckets and every other list.
    /// </summary>
    private static void EnsureIndexed(Campaign campaign)
    {
        var index = UseArmyIndex(campaign);
        if (!index.ProvinceStale) return;
        if (index.DeferProvinceRebuild)
        {
            if (!index.ProvinceSlotsCurrent) SyncProvinceMembership(campaign, index);
            index.ProvinceSlotsCurrent = true;
            return;
        }
        IndexArmies(campaign);
    }

    // The provinces formations actually left or entered, in the order the army list
    // would give a full rebuild. Other province lists are left alone, so a later battle
    // of this day still reads the same slot it would have read after a rebuild.
    private static void SyncProvinceMembership(Campaign campaign, ArmyIndex index)
    {
        foreach (var list in index.ProvinceScratch.Values) list.Clear();
        index.ProvinceScratchFilled.Clear();
        var armies = campaign.Armies;
        for (var i = 0; i < armies.Count; i++)
        {
            var army = armies[i];
            var province = army.ProvinceId;
            if (province <= 0) continue;
            if (!index.ProvinceScratch.TryGetValue(province, out var desired))
                index.ProvinceScratch[province] = desired = new List<Army>();
            if (desired.Count == 0) index.ProvinceScratchFilled.Add(province);
            desired.Add(army);
        }

        foreach (var province in index.ProvinceScratchFilled)
        {
            var desired = index.ProvinceScratch[province];
            if (province >= index.ByProvince.Length)
                Array.Resize(ref index.ByProvince, Math.Max(province + 1, (campaign.ProvinceMap?.ProvinceCount ?? 0) + 1));
            var here = index.ByProvince[province] ??= new List<Army>();
            if (!SameSlot(here, desired))
            {
                here.Clear();
                for (var i = 0; i < desired.Count; i++) here.Add(desired[i]);
            }
            if (here.Count > 0 && !index.ProvincesHeld.Contains(province)) index.ProvincesHeld.Add(province);
        }

        for (var i = index.ProvincesHeld.Count - 1; i >= 0; i--)
        {
            var province = index.ProvincesHeld[i];
            if (index.ProvinceScratchFilled.Contains(province)) continue;
            if ((uint)province < (uint)index.ByProvince.Length && index.ByProvince[province] is { } slot)
                slot.Clear();
            index.ProvincesHeld.RemoveAt(i);
        }
    }

    private static bool SameSlot(List<Army> live, List<Army> desired)
    {
        if (live.Count != desired.Count) return false;
        for (var i = 0; i < live.Count; i++)
            if (!ReferenceEquals(live[i], desired[i])) return false;
        return true;
    }

    private static Dictionary<(int WarId, int ProvinceId), LandBattle> EnsureActiveBattles(Campaign campaign)
    {
        var index = UseArmyIndex(campaign);
        var battles = campaign.Battles;
        if (!index.ActiveBattlesDirty && index.ActiveBattlesCount == battles.Count)
            return index.ActiveBattlesByKey;
        var map = index.ActiveBattlesByKey;
        map.Clear();
        for (var i = 0; i < battles.Count; i++)
        {
            var candidate = battles[i];
            if (!candidate.Ended) map.TryAdd((candidate.WarId, candidate.ProvinceId), candidate);
        }
        index.ActiveBattlesCount = battles.Count;
        index.ActiveBattlesDirty = false;
        return map;
    }

    // How much ground each country originally owned, and how much of it somebody else is
    // standing on. Every war used to work this out for itself by walking all thirty-eight
    // thousand provinces - with fifty wars going stale on the same day that was two million
    // province reads for a table that is the same table every time. Kept per campaign so
    // a second world's census cannot overwrite the first world's home and occupied counts.
    // The side maps of a war (who holds ground for the attacker, the defender, and either)
    // live on that same index. The countries in a battle live on that campaign's battle
    // board, one pair per battle id: the next battle clears its own pair, and a second
    // campaign cannot clear the first. The line, the reserve, the reinforcement queue
    // and the damage rows are the same kind of scratch, one per battle id. Two battles
    // are still resolved one after another. A hit writes that war's losses, the
    // region's damage and this campaign's support exposure. A capture marks the province
    // slots stale. While this day's battles are still being read, a province slot is
    // rewritten only when its membership no longer matches the army list, in that
    // list's order. The whole index is rebuilt after that read. The attacker and
    // defender lists are still one pair for the campaign, so one battle does not run
    // beside another.
    private static readonly Army[] SingleAdvancing = new Army[1];

    [ThreadStatic] private static int[] _homeCountsScratch;

    internal static void MeasureOccupation(Campaign campaign)
    {
        if (campaign is null) return;
        var index = UseArmyIndex(campaign);
        index.HomeProvinces.Clear();
        index.OccupiedPairs.Clear();
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return;
        var homeToNation = topology.ProvinceToNation;
        var count = topology.ProvinceCount;
        var maxOrig = 0;
        for (var p = 1; p <= count; p++)
        {
            var orig = homeToNation[p];
            if (orig > maxOrig) maxOrig = orig;
        }
        var homes = _homeCountsScratch;
        if (homes is null || homes.Length <= maxOrig)
            _homeCountsScratch = homes = new int[Math.Max(maxOrig + 1, 2048)];
        else
            Array.Clear(homes, 0, maxOrig + 1);
        for (var province = 1; province <= count; province++)
        {
            var original = homeToNation[province];
            if (original == 0) continue;
            homes[original]++;
            var controller = controllers[province];
            if (controller == 0 || controller == original) continue;
            var key = ((long)original << 32) | controller;
            index.OccupiedPairs[key] = index.OccupiedPairs.GetValueOrDefault(key) + 1;
        }
        for (var orig = 1; orig <= maxOrig; orig++)
            if (homes[orig] > 0) index.HomeProvinces[(ushort)orig] = homes[orig];
    }

    private static int Occupied(Campaign campaign, ushort original, ushort by) =>
        IndexOf(campaign)?.OccupiedPairs.GetValueOrDefault(((long)original << 32) | by) ?? 0;

    private static void IndexWars(Campaign campaign)
    {
        var index = UseArmyIndex(campaign);
        index.WarById.Clear();
        var live = LiveWars(campaign);
        for (var indexWar = 0; indexWar < live.Count; indexWar++)
        {
            var war = live[indexWar];
            if (war.Ended) continue;
            index.WarById[war.Id] = war;
        }
    }

    private static void IndexArmies(Campaign campaign)
    {
        var index = UseArmyIndex(campaign);
        InvalidateCoverCache(campaign);
        foreach (var province in index.ProvincesHeld) index.ByProvince[province].Clear();
        index.ProvincesHeld.Clear();
        foreach (var list in index.ByWar.Values) list.Clear();
        index.IndexedArmies.Clear();
        index.ByIdStale = true;
        index.ActiveBattlesDirty = true;
        foreach (var army in campaign.Armies)
        {
            index.IndexedArmies.Add(army);
            var province = army.ProvinceId;
            if (province > 0)
            {
                if (province >= index.ByProvince.Length)
                    Array.Resize(ref index.ByProvince, Math.Max(province + 1, (campaign.ProvinceMap?.ProvinceCount ?? 0) + 1));
                var here = index.ByProvince[province] ??= new List<Army>();
                if (here.Count == 0) index.ProvincesHeld.Add(province);
                here.Add(army);
            }
            if (army.AssignedWarId < 0) continue;
            if (!index.ByWar.TryGetValue(army.AssignedWarId, out var fighting))
                index.ByWar[army.AssignedWarId] = fighting = new List<Army>();
            fighting.Add(army);
        }
        index.ProvinceStale = false;
        index.ProvinceSlotsCurrent = true;
        index.FullRebuilds++;
    }

    /// <summary>
    /// Rebuilds this campaign's army index. It does not move a formation or rewrite an
    /// order. A second campaign's rebuild cannot clear these lists.
    /// </summary>
    internal static void RebuildArmyIndex(Campaign campaign)
    {
        if (campaign is null) return;
        IndexArmies(campaign);
        IndexWars(campaign);
    }

    /// <summary>How many times this campaign's whole army index has been rebuilt.</summary>
    internal static int ArmyIndexRebuilds(Campaign campaign) => IndexOf(campaign)?.FullRebuilds ?? 0;

    /// <summary>
    /// The day's battles are about to read who stands where. A capture patches province
    /// slots and does not rebuild the whole index until <see cref="ReleaseBattleOccupancy"/>.
    /// </summary>
    internal static void HoldBattleOccupancy(Campaign campaign)
    {
        if (campaign is null) return;
        UseArmyIndex(campaign).DeferProvinceRebuild = true;
    }

    /// <summary>
    /// The day's battles have been read. A capture that was held back rebuilds the whole
    /// index once. It does not move a formation or rewrite an order.
    /// </summary>
    internal static void ReleaseBattleOccupancy(Campaign campaign)
    {
        if (campaign is null) return;
        var index = UseArmyIndex(campaign);
        index.DeferProvinceRebuild = false;
        if (index.ProvinceStale) IndexArmies(campaign);
    }

    /// <summary>A capture moved formations in this campaign. Another campaign's capture does not set the flag.</summary>
    internal static void NoteFormationsMoved(Campaign campaign) => MarkIndexStale(campaign);

    /// <summary>Who a battle of this day would see standing in the province, in index order.</summary>
    internal static int[] BattleOccupancy(Campaign campaign, int province)
    {
        if (campaign is null) return Array.Empty<int>();
        EnsureIndexed(campaign);
        var here = InProvince(province);
        var ids = new int[here.Count];
        for (var i = 0; i < here.Count; i++) ids[i] = here[i].Id;
        return ids;
    }

    /// <summary>Wars by id from the last rebuild of this campaign. Null when it has not rebuilt.</summary>
    internal static IReadOnlyDictionary<int, StrategicWar> IndexedWarById(Campaign campaign) => IndexOf(campaign)?.WarById;

    /// <summary>Home province counts by map index from the last occupation census of this campaign.</summary>
    internal static IReadOnlyDictionary<ushort, int> IndexedHomeProvinces(Campaign campaign) => IndexOf(campaign)?.HomeProvinces;

    /// <summary>Occupied province pair counts from the last occupation census of this campaign.</summary>
    internal static IReadOnlyDictionary<long, int> IndexedOccupiedPairs(Campaign campaign) => IndexOf(campaign)?.OccupiedPairs;

    /// <summary>Map flags on the attacking side of the last side-map fill for this campaign.</summary>
    internal static IReadOnlySet<ushort> IndexedAttackerMaps(Campaign campaign) => IndexOf(campaign)?.AttackerMaps;

    /// <summary>Map flags on the defending side of the last side-map fill for this campaign.</summary>
    internal static IReadOnlySet<ushort> IndexedDefenderMaps(Campaign campaign) => IndexOf(campaign)?.DefenderMaps;

    /// <summary>Map flags on either side of the last side-map fill for this campaign.</summary>
    internal static IReadOnlySet<ushort> IndexedBelligerentMaps(Campaign campaign) => IndexOf(campaign)?.BelligerentMaps;

    /// <summary>
    /// Whose ground counts as each side's, for this campaign and this war. The next war of
    /// this campaign reuses the sets. Another campaign's fill cannot clear them.
    /// </summary>
    internal static void IndexSideMaps(Campaign campaign, StrategicWar war)
    {
        if (campaign is null || war is null) return;
        var index = UseArmyIndex(campaign);
        index.AttackerMaps.Clear();
        index.DefenderMaps.Clear();
        WarCoalition.FillControllers(campaign, war, true, index.AttackerMaps);
        WarCoalition.FillControllers(campaign, war, false, index.DefenderMaps);
        index.BelligerentMaps.Clear();
        index.BelligerentMaps.UnionWith(index.AttackerMaps);
        index.BelligerentMaps.UnionWith(index.DefenderMaps);
        index.BelligerentSnapshot = null;
    }

    /// <summary>War buckets from the last rebuild of this campaign. Null when it has not rebuilt.</summary>
    internal static Dictionary<int, List<Army>> IndexedWars(Campaign campaign) => IndexOf(campaign)?.ByWar;

    /// <summary>Armies the last rebuild placed in this war. Null when this campaign has no such bucket.</summary>
    internal static IReadOnlyList<Army> IndexedWarArmies(Campaign campaign, int warId)
    {
        var index = IndexOf(campaign);
        if (index is null || !index.ByWar.TryGetValue(warId, out var list)) return null;
        return list;
    }

    /// <summary>Province slots from the last rebuild. Null when this campaign has not rebuilt.</summary>
    internal static List<Army>[] IndexedProvinceSlots(Campaign campaign) => IndexOf(campaign)?.ByProvince;

    /// <summary>Armies the last rebuild placed in this province. Null when that slot was never filled.</summary>
    internal static IReadOnlyList<Army> IndexedProvinceArmies(Campaign campaign, int province)
    {
        var slots = IndexOf(campaign)?.ByProvince;
        if (slots is null || province <= 0 || province >= slots.Length) return null;
        return slots[province];
    }

    /// <summary>Divisions by id for this campaign. Null when it has not rebuilt. Fills the table on first ask.</summary>
    internal static Dictionary<int, Army> IndexedById(Campaign campaign)
    {
        var index = IndexOf(campaign);
        if (index is null) return null;
        FillById(index);
        return index.ById;
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>
    /// The same passes in the same order as <see cref="Tick"/>, yielding between wars so a
    /// frame can stop inside the step's heaviest pass rather than after all of it. Order is
    /// unchanged; only the frame boundary moves.
    /// </summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(Warfare))) yield break;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) yield break;
        if (campaign.StepOpensNewDay) ReconcileBattleAssignments(campaign);

        // RebuildFronts redraws every front in the world, so asking for it from inside the
        // per-war loop walked the whole map once per stale war. With one scripted war that
        // was one pass; with seventy it was seventy, and it made warfare the most expensive
        // system in the simulation. Decide once whether the map needs redrawing, redraw it
        // once, and then let each war do its own follow-up.
        var warBoard = ArmyIndexes.GetOrCreateValue(campaign);
        var stale = warBoard.Stale;
        var moved = warBoard.Moved;
        stale.Clear();
        var live = LiveWars(campaign);
        for (var index = 0; index < live.Count; index++)
        {
            var war = live[index];
            if (war.Ended) continue;
            if (war.FrontDirty || war.LastFrontDay < 0 || campaign.ElapsedDays - war.LastFrontDay >= 5)
                stale.Add(war);
        }

        if (stale.Count > 0)
        {
            moved.Clear();
            foreach (var war in stale) if (war.FrontDirty) moved.Add(war.Id);
            using (SimProfiler.Measure("    fr:rebuild")) RebuildFronts(campaign);
            yield return 0;
            using (SimProfiler.Measure("    fr:census")) MeasureOccupation(campaign);
            yield return 0;
            foreach (var war in stale)
            {
                using (SimProfiler.Measure("    fr:metrics")) UpdateWarMetrics(campaign, war);
                if (Campaign.StageOverdue()) yield return 0;
            }
            yield return 0;
            foreach (var war in stale)
            {
                using var refresh = FrontPlanner.RefreshAssignmentStages(campaign, war).GetEnumerator();
                while (true)
                {
                    bool more;
                    using (SimProfiler.Measure("    fr:refresh")) more = refresh.MoveNext();
                    if (!more) break;
                    yield return 0;
                }
                if (Campaign.StageOverdue()) yield return 0;
            }
            yield return 0;
            foreach (var war in stale)
            {
                bool needsStaff;
                using (SimProfiler.Measure("    fr:reassign"))
                    needsStaff = moved.Contains(war.Id) || FrontPlanner.NeedsEmergencyStaff(campaign, war);
                if (needsStaff)
                {
                    using var assign = FrontPlanner.ReassignAiStages(campaign, war).GetEnumerator();
                    while (true)
                    {
                        bool more;
                        using (SimProfiler.Measure("    fr:reassign")) more = assign.MoveNext();
                        if (!more) break;
                        yield return 0;
                    }
                }
                if (Campaign.StageOverdue()) yield return 0;
            }
        }
        yield return 0;

        // The staff keeps each front covered: every post held before any post is held twice.
        // Once a day for every front, and at once for a front that has just moved, because a
        // line that changed this hour has holes in it this hour.
        if (campaign.StepOpensNewDay)
        {
            using (var stages = FrontManager.TickStages(campaign).GetEnumerator())
            {
                while (true)
                {
                    bool hasNext;
                    using (SimProfiler.Measure("  war:staff"))
                        hasNext = stages.MoveNext();
                    if (!hasNext) break;
                    yield return 0;
                }
            }
            using (var stages = ThreatPosture.TickStages(campaign).GetEnumerator())
            {
                while (true)
                {
                    bool hasNext;
                    using (SimProfiler.Measure("  war:staff"))
                    using (SimProfiler.Measure("  war:posture"))
                        hasNext = stages.MoveNext();
                    if (!hasNext) break;
                    yield return 0;
                }
            }
            using (var stages = ArmyDisposition.TickStages(campaign).GetEnumerator())
            {
                while (true)
                {
                    bool hasNext;
                    using (SimProfiler.Measure("  war:staff"))
                    using (SimProfiler.Measure("  war:disposition"))
                        hasNext = stages.MoveNext();
                    if (!hasNext) break;
                    yield return 0;
                }
            }
        }
        else
        {
            for (var wi = 0; wi < stale.Count; wi++)
            {
                var war = stale[wi];
                if (!moved.Contains(war.Id)) continue;
                using (SimProfiler.Measure("  war:staff"))
                    FrontManager.Balance(campaign, war);
                yield return 0;
            }
        }

        // Two indexes, built once. ResolveAdvances used to walk every division in the world
        // for every war - seventy wars against fourteen hundred divisions - and then walk
        // them again for each province it was weighing up. Both questions are lookups now.
        using (SimProfiler.Measure("  war:index"))
        {
            IndexArmies(campaign);
            IndexWars(campaign);
        }

        // One war at a time with a frame boundary between them: a front-heavy war used to
        // land its whole advance resolution - routing, targets, cover - in a single frame.
        var advancing = AdvanceWars(campaign);
        for (var index = 0; index < advancing.Count; index++)
        {
            var war = advancing[index];
            if (war.Ended) { yield return 0; continue; }
            // A capture in the preceding war invalidates province/war indexes.
            // Refresh BEFORE obtaining its formation list: rebuilding that list
            // from inside the opportunity predicate would mutate an active foreach.
            EnsureIndexed(campaign);
            using (SimProfiler.Measure("  war:advances"))
            using (SimProfiler.Measure("    adv:execution")) FrontPlanner.UpdateAiExecution(campaign, war, InWar(war.Id));
            // A war with nobody ordered forward resolves nothing, yet its side maps were
            // rebuilt from the province controllers every step anyway - nine and a half
            // thousand rebuilds a day on a forty-year save. The order kinds are a necessary
            // condition of every branch inside the resolve, so an empty war skips it whole.
            if (AnyAdvancingOrder(campaign, war))
                foreach (var _ in ResolveAdvancesStages(campaign, war)) yield return 0;
            yield return 0;
        }

        // A round is a DAY of fighting - it carries a date, a note and a line in the battle
        // report - so it resolves on a day rather than on a step. At the shipped hourly step
        // this ran twenty-four times a day: twenty-four days of casualties every day, and a
        // war decided by one side running out of people instead of by the fighting.
        //
        // Gated inside the scope rather than around it, so the profiler still counts one
        // entry per step and the cadence check still reads what it is written to read.
        if (campaign.StepOpensNewDay)
        {
            using var battles = ResolveBattlesStages(campaign).GetEnumerator();
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("  war:battles")) more = battles.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }
        else
        {
            using (SimProfiler.Measure("  war:battles")) { }
        }
        using (SimProfiler.Measure("  war:occupation")) SecureOccupation(campaign);
        campaign.Battles.RemoveAll(battle => battle.Ended && campaign.ElapsedDays - battle.StartedDay > 30);
        ForgetEndedBattleNations(campaign);
    }

    private static readonly List<Army> NoArmies = new();

    private static List<Army> InWar(int warId) =>
        ActiveIndex is not null && ActiveIndex.ByWar.TryGetValue(warId, out var list) ? list : NoArmies;

    /// <summary>The cheap necessary condition of every branch inside the resolve: no army
    /// carrying an advance order means the resolve would walk the war and change nothing.</summary>
    private static bool AnyAdvancingOrder(Campaign campaign, StrategicWar war)
    {
        foreach (var army in InWar(war.Id))
            if ((army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead) && army.PlanActive)
                return true;
        return false;
    }

    /// <summary>Everyone the active campaign's index has standing in this province, in the order the world lists them.</summary>
    private static List<Army> InProvince(int province)
    {
        var slots = ActiveIndex?.ByProvince;
        return slots is not null && province > 0 && province < slots.Length && slots[province] is { } here ? here : NoArmies;
    }

    private static void FillById(ArmyIndex index)
    {
        if (!index.ByIdStale) return;
        index.ById.Clear();
        foreach (var army in index.IndexedArmies) index.ById[army.Id] = army;
        index.ByIdStale = false;
    }

    private static Dictionary<int, Army> ArmiesById()
    {
        if (ActiveIndex is null) return NoArmyById;
        FillById(ActiveIndex);
        return ActiveIndex.ById;
    }

    private static IEnumerable<int> ResolveAdvancesStages(Campaign campaign, StrategicWar war)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) yield break;
        using (SimProfiler.Measure("  war:advances"))
        using (SimProfiler.Measure("    adv:resolve"))
        {
            UseBattleBuckets(campaign);

            // Whose ground counts as the enemy's, per side. In a war with allies in it this is
            // a set rather than one country, and it is the same set for every formation on a
            // side, so it is built once per war rather than once per formation. The sets belong
            // to this campaign; the next war reuses them, and another campaign does not.
            IndexSideMaps(campaign, war);
        }
        var attackerMaps = IndexOf(campaign).AttackerMaps;
        var defenderMaps = IndexOf(campaign).DefenderMaps;

        // One war's resolve used to be a single 38 ms stage, the heaviest piece of a fighting
        // day. The seams sit between armies, so captures still land in the order the
        // formations made them and the scopes stay closed across every seam.
        var seen = 0;
        var warArmies = InWar(war.Id);
        for (var ai = 0; ai < warArmies.Count; ai++)
        {
            var army = warArmies[ai];
            // A formation with nothing to resolve - holding, in reserve, marching, fighting, not
            // yet ready - is passed before the measuring starts and without bouncing the iterator.
            if ((army.OrderKind != ArmyOrder.AdvanceFront && army.OrderKind != ArmyOrder.Spearhead) || !army.PlanActive ||
                army.InBattle || army.IsMoving || army.ReadyDay > campaign.ElapsedDays)
                continue;
            if (++seen % 12 == 0 || Campaign.StageOverdue()) yield return 0;
            using (SimProfiler.Measure("  war:advances"))
            using (SimProfiler.Measure("    adv:resolve"))
            {
            if (!war.Involves(army.NationId)) continue;
            var armyNation = campaign.NationById(army.NationId);
            if (armyNation is null || army.ProvinceId <= 0) continue;
            // Fast path: a formation on its own ground cannot trigger MilitaryAccess.MustReturn.
            if (army.ProvinceId < controllers.Length)
            {
                var standingOn = controllers[army.ProvinceId];
                if (standingOn != 0 && standingOn != armyNation.MapIndex && MilitaryAccess.MustReturn(campaign, army))
                    continue;
            }
            else if (MilitaryAccess.MustReturn(campaign, army))
                continue;
            // A cut-off or stranded formation does not begin an attack on its own (StaffReasons),
            // but it still marches into ground nobody holds: that is how a pocket breaks out, and
            // holding it still there froze its escape.
            var heldBack = StaffReasons.SupplyHoldsAttack(campaign, army);

            var enemyMaps = war.IsAttacking(army.NationId) ? defenderMaps : attackerMaps;
            var friendlyMaps = war.IsAttacking(army.NationId) ? attackerMaps : defenderMaps;
            var enemyId = war.EnemyLeaderOf(army.NationId);
            var enemy = campaign.NationById(enemyId);
            if (enemy is null || army.ProvinceId <= 0) continue;
            bool routed;
            using (SimProfiler.Measure("      adv:route"))
                routed = !heldBack && PrepareOperationalRoute(campaign, war, army, controllers, friendlyMaps);
            if (routed) continue;
            var target = ChooseAssaultTarget(campaign, topology, war, army, controllers, enemyMaps);
            if (target < 0) continue;
            // Everyone standing in the target province, including anyone already fighting
            // there. Excluding divisions that were in a battle meant a province being
            // actively defended looked empty to the next attacker, which walked into it
            // unopposed while the defenders were busy holding it. Ten province captures in
            // eleven came through that hole rather than through winning anything.
            Present.Clear();
            foreach (var other in InProvince(target))
                if (war.Opposes(army.NationId, other.NationId) && other.Divisions >= 0.1f) Present.Add(other);
            if (heldBack && Present.Count > 0) continue;

            if (Present.Count == 0)
            {
                // Support fire does not occupy empty ground. Wait for an actual battle;
                // an ordinary RMB order can turn this into a march explicitly.
                if (army.SupportOnly) continue;
                // Ground nobody stands on is still on the far side of the water: a strait is
                // crossed at the bridge's or the ferry's pace whether or not anyone is shooting.
                var riverAdvance = WaterConditions.MovementMultiplier(campaign, army.ProvinceId, target) *
                    LandCrossings.MarchMultiplier(campaign, army.ProvinceId, target);
                // An empty province is not open ground. The divisions holding the provinces
                // around it cover the gap by fire, which is what keeps a front a front rather
                // than a race: a breach has to be widened and held, not strolled through
                // because nobody happened to be standing in it.
                //
                // Letting cover merely slow an advance instead of stopping it, and flooring
                // that slowdown at a third, is what turned this front into a walkover: over
                // two years with nobody playing, Ukraine went from losing eight provinces to
                // losing a hundred and nineteen, and gap captures on the front went from six
                // to sixty-one. Cover is a wall to a force smaller than what covers the gap,
                // and no obstacle at all to one that is larger - which is the difference
                // between an offensive and a walk.
                var coveringPower = 0f;
                var covering = 0f;
                Cover(campaign, topology, target, war, army.NationId, ref coveringPower, ref covering);
                // ...but the formation that refuses is one advancing on its own judgement.
                // A player who marched a division here by hand has already decided, with the
                // map in front of them, that this attack is worth making. The simulation gets
                // to make that expensive and slow. It does not get to quietly decline: an
                // order that produces no movement, no battle and no explanation is the single
                // worst thing a strategy game can do, and this one did it every day forever.
                //
                // The measurement that produced the rule above was of the front planner
                // exploiting gaps with nobody playing, so leaving it in force for exactly
                // that case keeps what it bought.
                var ordered = army.DirectOrder;
                var urgent = war.IsDefending(army.NationId) && (war.DefenderOccupiedValue > 0.005f || war.WarScore < -2f);
                var advantage = urgent ? campaign.Defines.GapExploitAdvantage * 2.0f : campaign.Defines.GapExploitAdvantage;
                if (!ordered && coveringPower > PushingOn(campaign, topology, war, army, target) * advantage) continue;
                var contested = 1f / (1f + covering * campaign.Defines.ZoneOfControlResistance);
                // Covered ground still costs: an ordered advance into a fully covered gap runs
                // at a fraction of the pace of one into open country, and the floor is what
                // makes it a fraction rather than a standstill. Without it the resistance term
                // alone turns a week into four months, which the player cannot tell apart from
                // the refusal this replaces.
                if (ordered) contested = M.Max(contested, campaign.Defines.OrderedAdvanceFloor);
                // Ground nobody is within reach to cover is not a gap in a line - it is the
                // country behind one. It is taken at the pace of a march, on the stores and
                // roads it is taken with, rather than at the crawl a covered gap costs: an
                // army through the line used to spend three weeks on each empty district
                // behind it, which read as the whole army stuck.
                var open = covering <= 0f;
                var pace = open
                    ? MathF.Max(campaign.Defines.UnopposedCapturePerDay, campaign.Defines.OpenCountryCapturePerDay)
                    : campaign.Defines.UnopposedCapturePerDay;
                if (ordered) pace = MathF.Max(pace, campaign.Defines.OrderedCapturePerDay);
                var supplyFloor = open
                    ? MathF.Max(campaign.Defines.MarchSupplyFloor, campaign.Defines.OpenCountrySupplyFloor)
                    : campaign.Defines.MarchSupplyFloor;
                if (ordered) supplyFloor = MathF.Max(supplyFloor, campaign.Defines.OrderedCaptureSupplyFloor);
                // Progress belongs to the division, not a pooled province counter: a
                // hundred divisions must not teleport one another across the border.
                army.MoveProgress += pace * campaign.TickScale *
                    UnitTypeCatalog.Get(army.TemplateId).Speed * Researched(campaign, army, "army_speed") *
                    MilitaryDoctrine.Movement(army.Stance, campaign.Defines) *
                    M.Clamp(army.SupplyLevel, supplyFloor, 1f) *
                    topology.TerrainAt(target).MovementSpeed * riverAdvance * contested * MovementPace.Roads(campaign, target);
                war.UnopposedProgress[target] = MathF.Max(war.UnopposedProgress.GetValueOrDefault(target), army.MoveProgress);
                if (army.MoveProgress >= 1f)
                {
                    SingleAdvancing[0] = army;
                    Capture(campaign, war, target, army.NationId, SingleAdvancing, Array.Empty<Army>(), "walked into a gap");
                    SingleAdvancing[0] = null;
                }
                continue;
            }

            // Reinforcements already waited for organisation and stores; the formation
            // initiating the same assault did not. Apply one live minimum to both paths.
            // This comes after empty-ground movement so a hungry formation can still
            // march out of a pocket or take undefended ground. Explicit direct orders
            // keep the player's choice to accept risk, rather than silently refusing it.
            if (!AssaultReadiness.Assess(campaign, army).Ready) continue;

            // Found via campaign-scoped O(1) active battle index rather than walking all
            // campaign.Battles on every division advance.
            var activeBattles = EnsureActiveBattles(campaign);
            activeBattles.TryGetValue((war.Id, target), out var battle);
            if (battle is null)
            {
                battle = new LandBattle
                {
                    Id = campaign.NextBattleId++,
                    WarId = war.Id,
                    ProvinceId = target,
                    AttackerId = army.NationId,
                    DefenderId = Present[0].NationId,
                    StartedDay = campaign.ElapsedDays,
                    RiverCrossingStrength = topology.RiverCrossingStrength(army.ProvinceId, target)
                };
                campaign.Battles.Add(battle);
                activeBattles[(war.Id, target)] = battle;
                UseArmyIndex(campaign).ActiveBattlesCount = campaign.Battles.Count;
                foreach (var defending in Present) Join(battle, defending, false);
            }
            Join(battle, army, true);
            }
        }
    }

    /// <summary>
    /// An army has just come off a ship onto somebody else's beach. An undefended coast
    /// changes hands; a defended one is a battle, fought by the ordinary combat model with
    /// the landing party as the attacker - which is the point, because an amphibious
    /// assault should be a fight a player already knows how to read.
    /// </summary>
    public static string LandInvasion(Campaign campaign, StrategicWar war, int provinceId,
        IReadOnlyList<Army> landed)
    {
        if (landed.Count == 0) return "nothing came ashore";
        var controllers = campaign.ProvinceController;
        if (controllers is null) return "no map";

        UseBattleBuckets(campaign);
        EnsureIndexed(campaign);
        var invaderId = landed[0].NationId;

        DefendingSide.Clear();
        foreach (var army in InProvince(provinceId))
            if (war.Opposes(invaderId, army.NationId) && army.Divisions >= 0.1f) DefendingSide.Add(army);

        if (DefendingSide.Count == 0)
        {
            Capture(campaign, war, provinceId, invaderId, landed, Array.Empty<Army>(), "came ashore unopposed");
            return $"{landed.Count:N0} formation(s) landed against no opposition and took the province";
        }

        var activeBattles = EnsureActiveBattles(campaign);
        activeBattles.TryGetValue((war.Id, provinceId), out var battle);
        if (battle is null)
        {
            battle = new LandBattle
            {
                Id = campaign.NextBattleId++,
                WarId = war.Id,
                ProvinceId = provinceId,
                AttackerId = invaderId,
                DefenderId = DefendingSide[0].NationId,
                StartedDay = campaign.ElapsedDays
            };
            campaign.Battles.Add(battle);
            activeBattles[(war.Id, provinceId)] = battle;
            UseArmyIndex(campaign).ActiveBattlesCount = campaign.Battles.Count;
            foreach (var defending in DefendingSide) Join(battle, defending, false);
        }
        foreach (var army in landed) Join(battle, army, true);
        MarkIndexStale(campaign);
        return $"{landed.Count:N0} formation(s) landed into a contested beach";
    }

    /// <summary>One side's fortune for today's fighting: the day's, 1 ± Defines.CombatFortune, triangular, times the battle's; from the seed.</summary>
    private static float Fortune(Campaign campaign, LandBattle battle, int side)
    {
        // The day's rolls change daily; the battle's (Defines.CombatBattleFortune) and its one
        // decisive moment (CombatDecisiveChance) are rolled on the day it began. The formula is
        // WarRhythm's.
        var defines = campaign.Defines;
        var day0 = Escalation.Roll(campaign.Seed, battle.Id, side, campaign.ElapsedDays, 0x6F7);
        var day1 = Escalation.Roll(campaign.Seed, battle.Id, side, campaign.ElapsedDays, 0x6F8);
        var b0 = Escalation.Roll(campaign.Seed, battle.Id, side, battle.StartedDay, 0x6F9);
        var b1 = Escalation.Roll(campaign.Seed, battle.Id, side, battle.StartedDay, 0x6FA);
        var b2 = Escalation.Roll(campaign.Seed, battle.Id, side, battle.StartedDay, 0x6FB);
        var b3 = Escalation.Roll(campaign.Seed, battle.Id, side, battle.StartedDay, 0x6FC);
        return WarRhythm.Fortune(defines.CombatFortune, day0, day1,
            defines.CombatBattleFortune, b0, b1,
            defines.CombatDecisiveChance, b2, defines.CombatDecisiveSwing, b3);
    }

    private static void ResolveBattles(Campaign campaign)
    {
        foreach (var _ in ResolveBattlesStages(campaign)) { }
    }

    private static IEnumerable<int> ResolveBattlesStages(Campaign campaign)
    {
        var battleCount = campaign.Battles.Count;
        if (battleCount == 0) yield break;
        UseBattleBuckets(campaign);
        // A capture marks province slots stale. While this day's battles are still
        // being read, that mark patches the slots whose membership changed. It does
        // not rebuild the war buckets or every other province. The whole index is
        // rebuilt after the last battle has been read, before occupation asks who
        // is standing anywhere. Two battles still do not run together: they share
        // this campaign's attacker and defender lists and its support exposure.
        var battleIndex = UseArmyIndex(campaign);
        battleIndex.DeferProvinceRebuild = true;
        try
        {
        for (var bi = 0; bi < battleCount; bi++)
        {
            var battle = campaign.Battles[bi];
            if (battle.Ended) continue;
            var warsById = IndexedWarById(campaign);
            var war = warsById is not null && warsById.TryGetValue(battle.WarId, out var foundWar) ? foundWar : null;
            if (war is null || war.Ended) { Close(campaign, battle, -1, "War ended"); continue; }

            // The line, reserve, reinforcement queue and damage rows belong to this
            // battle. The next battle will point ActiveRows at its own scratch. This
            // one stays. Battles are still taken one at a time. A capture patches the
            // province slots the formations left and entered; it does not rebuild the
            // whole index while a later battle of this day is still reading them.
            // The hit still writes the war, the region and the support exposure, and
            // the attacker and defender lists are still this campaign's one pair.
            UseBattleRows(campaign, battle);
            ReinforceBattle(campaign, battle);

            // Whoever is standing in the province and not already committed joins the
            // defence. This used to walk every division in the world once per battle -
            // three hundred and sixty battles against fourteen hundred divisions, half a
            // million comparisons a day - to answer a question the province index already
            // answers. A capture earlier today patched the provinces that changed, in
            // the same order a full rebuild would have listed them.
            EnsureIndexed(campaign);
            foreach (var army in InProvince(battle.ProvinceId))
                if (!army.InBattle && WarCoalition.SameSide(war, army.NationId, battle.DefenderId) && army.Divisions >= 0.1f)
                    Join(battle, army, false);

            var attacking = Side(war, battle.Attackers, battle.AttackerId, AttackingSide);
            var defending = Side(war, battle.Defenders, battle.DefenderId, DefendingSide);
            if (attacking.Count == 0) { Close(campaign, battle, battle.DefenderId, "Attackers withdrew"); continue; }
            for (var i = defending.Count - 1; i >= 0; i--)
            {
                var army = defending[i];
                // A rising's formation among its own people with no ground of its side behind
                // it slips away past the enemy (Partisans.SlipAwayProvince): it stays in the
                // battle like any formation with a retreat and leaves when the province falls.
                if (army.Organisation <= 0.005f &&
                    WarCoalition.SpreadRetreatProvince(campaign, war, army, battle.ProvinceId, beaten: true, slipAway: true, out _) <= 0)
                {
                    // One with nowhere at all to slip to scatters instead (Partisans).
                    if (!Partisans.TryGoToGround(campaign, war, army, battle.ProvinceId))
                    {
                        if (DiagnosticLogs.Enabled)
                            DiagnosticLogs.Write("simulation", $"SURRENDER army={army.Id} nation={army.NationId} province={battle.ProvinceId} war={war.Id} reason=zero_morale_in_pocket");
                        WarLosses.Surrender(campaign, army);
                        Attrition.Destroy(campaign, army, "destroyed: zero morale in pocket");
                    }
                    defending.RemoveAt(i);
                    battle.Defenders.Remove(army.Id);
                }
            }
            if (defending.Count == 0)
            {
                var occupied = Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending,
                    "defenders left the province");
                Close(campaign, battle, battle.AttackerId, occupied ? "Province taken unopposed" : "Support ended; province not occupied");
                continue;
            }

            // ----------------------------------------------------------------------
            // Frontage. A province only has so much room, so numbers past the width
            // wait their turn - which is what lets a smaller, better force hold a line
            // against a larger one. Attacking from several provinces widens the front,
            // the way it does in Hearts of Iron.
            //
            // This is a filter now, and it used to be a multiplier. The multiplier scaled
            // a whole side's fire by the share of it that fitted, which meant nineteen
            // divisions in a province all fought, all took losses and all counted as
            // being in the battle, while the fifth one made the first four shoot worse
            // than four would have. What is past the width is simply not in the fight: it
            // stands in reserve, takes the place of whoever is spent, and the next day's
            // split decides that again.
            // ----------------------------------------------------------------------
            var defines = campaign.Defines;
            var terrain = campaign.ProvinceMap?.TerrainAt(battle.ProvinceId) ?? TerrainCatalog.Get("plains");
            var baseWidth = defines.CombatWidth * terrain.CombatWidth;
            var directions = CountAttackDirections(attacking);
            var usableWidth = baseWidth + defines.CombatWidthPerDirection * terrain.CombatWidth *
                M.Clamp(directions - 1, 0, M.Max(0, defines.MaxAttackDirections - 1));

            var attackerWantedWidth = WantedWidth(attacking);
            FrontageReady(attacking, usableWidth, AttackerLine, AttackerReserve, defines.BattleBreakOrganisation, campaign, battle, true);
            FrontageReady(defending, baseWidth, DefenderLine, DefenderReserve, defines.BattleBreakOrganisation, campaign, battle, false);
            battle.LastAttackerLine.Clear(); battle.LastDefenderLine.Clear();
            foreach (var army in AttackerLine) battle.LastAttackerLine.Add(army.Id);
            foreach (var army in DefenderLine) battle.LastDefenderLine.Add(army.Id);

            // The ground each side actually holds, and how many formations hold it. The
            // report reads these; everything below is fought over the line rather than over
            // the province, so a reserve shoots at nothing and is shot at by almost nothing.
            battle.AttackerWidth = WantedWidth(AttackerLine);
            battle.DefenderWidth = WantedWidth(DefenderLine);
            battle.AttackerInLine = AttackerLine.Count;
            battle.DefenderInLine = DefenderLine.Count;
            var attackerEngagement = OverWidth(battle.AttackerWidth, usableWidth, defines);
            var defenderEngagement = OverWidth(battle.DefenderWidth, baseWidth, defines);

            // ----------------------------------------------------------------------
            // What each side is made of. Hardness decides which of an enemy's two
            // attack values it gets to use; armour against piercing decides how much
            // of that lands at all.
            // ----------------------------------------------------------------------
            ComputeLineProfile(AttackerLine, out var attackerHardness, out var attackerArmour, out var attackerPiercing, out var attackerSupply, out var attackerRecon);
            ComputeLineProfile(DefenderLine, out var defenderHardness, out var defenderArmour, out var defenderPiercing, out var defenderSupply, out var defenderRecon);
            var attackerPierces = ArmourFactor(attackerPiercing, defenderArmour, defines);
            var defenderPierces = ArmourFactor(defenderPiercing, attackerArmour, defines);

            var attackerPlanning = WeightedPlanning(AttackerLine);
            var riverAttackMultiplier = WaterConditions.AttackMultiplier(campaign, battle.RiverCrossingStrength, battle.ProvinceId);
            // An assault over a bridge is one column wide and one from ferries arrives by
            // boatloads. Charged on the share of the attack that is coming that way today,
            // from where the attackers stand, so nothing about it has to be saved.
            var straitAttackMultiplier = LandCrossings.AssaultMultiplier(campaign, battle.ProvinceId, AttackerLine);

            // ----------------------------------------------------------------------
            // Both sides shoot. The attacker's fire is measured against the defender's
            // defence; the defender's return fire is measured against the attacker's
            // breakthrough. That second half is the whole point: a division built to
            // attack survives a counter-attack it would not survive on defence, and a
            // dug-in division survives an assault it could never have launched.
            // ----------------------------------------------------------------------
            // The long-range forces over the ground: artillery, attack aircraft, drones,
            // the sweep overhead. Reaches the battle from bases within range, saturating,
            // and the enemy's sky and missile brigades take their cut of whatever the
            // other side managed to contribute - a wing over a defended front pays first.
            // Support is asked of the countries in the battle, not of the formations in the
            // line: a wing or a gun brigade is based far behind the province and reaches the
            // battle whoever happens to be standing in the front rank today. The sets belong
            // to this battle. The next battle of this campaign does not clear them.
            FillBattleNationSets(campaign, battle, attacking, defending);
            var nationBoard = Board(campaign);
            var attackerNations = nationBoard.AttackerNations[battle.Id];
            var defenderNations = nationBoard.DefenderNations[battle.Id];
            var (attackerFire, defenderFire, defenderStripped, attackerStripped,
                    attackerSupportShown, defenderSupportShown) =
                SupportCore.Engagement(campaign, battle.ProvinceId, attackerNations, defenderNations);
            battle.AttackerSupport = attackerSupportShown;
            battle.DefenderSupport = defenderSupportShown;

            var envelopmentBonus = CombatTempo.EnvelopmentFactor(defines, directions);
            var attackerCombinedArms = BattleTelemetry.CombinedArmsMultiplier(defines, AttackerLine, attackerFire);
            var defenderCombinedArms = BattleTelemetry.CombinedArmsMultiplier(defines, DefenderLine, defenderFire);
            battle.LastAttackDirections = directions;
            battle.LastAttackerCombinedArms = attackerCombinedArms;
            battle.LastDefenderCombinedArms = defenderCombinedArms;

            _battleNationFactors?.Clear();
            var atkCache = _atkLineUnits ??= new List<BattleLineUnit>(16);
            atkCache.Clear();
            double attackerOffenceSum = 0d;
            double atkPowerWeight = 0d, atkTrainSum = 0d, atkStanceSum = 0d, atkCmdSum = 0d, atkResSum = 0d, atkEncSum = 0d;
            for (var ai = 0; ai < AttackerLine.Count; ai++)
            {
                var army = AttackerLine[ai];
                var nf = GetBattleNationFactors(campaign, war, army.NationId);
                var power = M.Max(0.01f, army.Power);
                var train = DivisionRules.Attack(army);
                var stance = MilitaryDoctrine.Attack(army.Stance);
                var res = nf.AtkRes;
                var mun = nf.Munitions;
                var enc = CombatTempo.EncirclementFactor(defines, army.CutOff, army.DaysCutOff);
                var spirit = nf.Spirit;
                var cmd = Generals.Factors(campaign, army);
                atkCache.Add(new BattleLineUnit
                {
                    Power = power,
                    Enc = enc,
                    DefRes = nf.DefRes,
                    Spirit = spirit,
                    CmdDef = cmd.Defence
                });
                attackerOffenceSum += power * Offence(army, defenderHardness) * train * stance * res * mun * enc * spirit * cmd.Attack;
                double w = power;
                atkPowerWeight += w;
                atkTrainSum += w * train;
                atkStanceSum += w * stance;
                atkCmdSum += w * cmd.Attack;
                atkResSum += w * res;
                atkEncSum += w * enc;
            }
            var attackerOffence = (float)attackerOffenceSum *
                attackerEngagement * attackerPierces * riverAttackMultiplier * straitAttackMultiplier * terrain.Attack *
                (1f + attackerPlanning * defines.MaxPlanningAttackBonus) * envelopmentBonus * attackerFire * attackerCombinedArms;

            var defCache = _defLineUnits ??= new List<BattleLineUnit>(16);
            defCache.Clear();
            double defenderOffenceSum = 0d;
            for (var di = 0; di < DefenderLine.Count; di++)
            {
                var army = DefenderLine[di];
                var nf = GetBattleNationFactors(campaign, war, army.NationId);
                var power = M.Max(0.01f, army.Power);
                var enc = CombatTempo.EncirclementFactor(defines, army.CutOff, army.DaysCutOff);
                var cmd = Generals.Factors(campaign, army);
                defCache.Add(new BattleLineUnit
                {
                    Power = power,
                    Enc = enc,
                    DefRes = nf.DefRes,
                    Spirit = nf.Spirit,
                    CmdDef = cmd.Defence
                });
                defenderOffenceSum += power *
                    Offence(army, attackerHardness) * DivisionRules.Attack(army) *
                    MilitaryDoctrine.Attack(army.Stance) * nf.AtkRes *
                    nf.Munitions *
                    enc *
                    nf.Spirit *
                    cmd.Attack;
            }
            var defenderOffence = (float)defenderOffenceSum *
                defenderEngagement * defenderPierces * defines.DefenderCounterFire * defenderFire * defenderCombinedArms;

            double dugInSum = 0d;
            for (var di = 0; di < DefenderLine.Count; di++) dugInSum += DefenderLine[di].Entrenchment;
            var dugIn = DefenderLine.Count > 0 ? (float)(dugInSum / DefenderLine.Count) : 0f;
            var attackerOrgBefore = AverageOrganisation(AttackerLine);
            var defenderOrgBefore = AverageOrganisation(DefenderLine);

            // The owner of a province is its country's map number; DefenderId is its place in the
            // nation list. Compared with each other they matched almost nowhere, so the homeland
            // bonus a defender is owed on its own soil was paid to nobody - or to whoever a
            // coincidence of numbers put on another country's ground.
            var defenderNation = campaign.NationById(battle.DefenderId);
            var isHomeProvince = defenderNation is not null && campaign.ProvinceMap?.ProvinceToNation != null &&
                battle.ProvinceId > 0 && battle.ProvinceId < campaign.ProvinceMap.ProvinceToNation.Length &&
                campaign.ProvinceMap.ProvinceToNation[battle.ProvinceId] == defenderNation.MapIndex;

            var isCapitalState = defenderNation != null && defenderNation.CapitalStateMapIndex >= 0 &&
                campaign.ProvinceMap?.ProvinceToState != null &&
                battle.ProvinceId > 0 && battle.ProvinceId < campaign.ProvinceMap.ProvinceToState.Length &&
                campaign.ProvinceMap.ProvinceToState[battle.ProvinceId] == defenderNation.CapitalStateMapIndex;

            var homelandBonus = isHomeProvince ? defines.HomelandDefenseBonus : 1f;
            if (isCapitalState) homelandBonus *= defines.CapitalDefenseBonus;
            var frontalBonus = directions == 1 ? defines.FrontalDefenseBonus : 1f;

            var effectiveDefenderStripped = M.Lerp(defenderStripped, 1f, dugIn * defines.EntrenchmentShellingResistance);

            double defenderProtectionSum = 0d;
            double defPowerWeight = 0d, defTrainSum = 0d, defStanceSum = 0d, defCmdSum = 0d, defResSum = 0d, defEncSum = 0d;
            for (var di = 0; di < DefenderLine.Count; di++)
            {
                var army = DefenderLine[di];
                var unit = defCache[di];
                var power = unit.Power;
                var train = DivisionRules.Defense(army);
                var stance = MilitaryDoctrine.Defense(army.Stance);
                var res = unit.DefRes;
                var enc = unit.Enc;
                var spirit = unit.Spirit;
                var cmd = unit.CmdDef;
                defenderProtectionSum += power * UnitTypeCatalog.Get(army.TemplateId).Defense * train * stance * res * enc * spirit * cmd;
                double w = power;
                defPowerWeight += w;
                defTrainSum += w * train;
                defStanceSum += w * stance;
                defCmdSum += w * cmd;
                defResSum += w * res;
                defEncSum += w * enc;
            }
            var flankRetention = BattleTelemetry.FlankingEntrenchmentRetention(defines, directions);
            battle.LastFlankingBypass = 1f - flankRetention;
            var effectiveEntrenchmentBonus = 1f + dugIn * flankRetention * defines.EntrenchmentBonus;
            var defenderProtection = (float)defenderProtectionSum *
                defenderEngagement * defines.DefenderAdvantage * terrain.Defense *
                effectiveEntrenchmentBonus * effectiveDefenderStripped *
                homelandBonus * frontalBonus * defenderCombinedArms;

            double attackerProtectionSum = 0d;
            for (var ai = 0; ai < AttackerLine.Count; ai++)
            {
                var army = AttackerLine[ai];
                var unit = atkCache[ai];
                attackerProtectionSum += unit.Power *
                    UnitTypeCatalog.Get(army.TemplateId).Breakthrough * DivisionRules.Defense(army) *
                    MilitaryDoctrine.Defense(army.Stance) * unit.DefRes *
                    unit.Enc *
                    unit.Spirit *
                    unit.CmdDef;
            }
            var attackerProtection = (float)attackerProtectionSum *
                attackerEngagement * attackerStripped * attackerCombinedArms;

            // The war of the weak (GuerrillaWar): a rising or a small country defending rough
            // ground of its own against a far stronger enemy stays in the fight longer, and gives
            // ground without dying for it - it melts away and fights again.
            var guerrillaCover = GuerrillaWar.Cover(campaign, defenderNation, campaign.NationById(battle.AttackerId),
                battle.ProvinceId, terrain);
            defenderProtection *= 1f + defines.GuerrillaDefence * guerrillaCover;

            // The fortunes of war (Defines.CombatFortune): each side's day runs a little above or
            // below what its numbers say - the weather, a commander's call, a bridge that held -
            // in its fire and in its staying power alike. Drawn from the campaign seed, the battle
            // and the day, so a seed replays its war exactly and another seed fights it otherwise;
            // centred on one, so fortune decides battles without tilting the world.
            var attackerFortune = Fortune(campaign, battle, 1);
            var defenderFortune = Fortune(campaign, battle, 2);
            attackerOffence *= attackerFortune; attackerProtection *= attackerFortune;
            defenderOffence *= defenderFortune; defenderProtection *= defenderFortune;

            // The day's manoeuvres (CombatTactics): each staff's answer to this morning,
            // drawn from the seed like fortune is and weighted by what its army is made
            // of, its doctrine book, and its recon/commander initiative.
            var attackerNation = campaign.NationById(battle.AttackerId);
            var attackerTactic = CombatTactics.Pick(campaign, battle.Id, true, campaign.ElapsedDays,
                attackerArmour, 0f, attackerOrgBefore, directions, terrain.Id, attackerPlanning,
                attackerNation, 0f, attackerRecon);
            var defenderTactic = CombatTactics.Pick(campaign, battle.Id, false, campaign.ElapsedDays,
                defenderArmour, dugIn, defenderOrgBefore, 1, terrain.Id, 0f,
                defenderNation, 0f, defenderRecon);
            battle.AttackerTactic = attackerTactic?.Id ?? "";
            battle.DefenderTactic = defenderTactic?.Id ?? "";
            attackerOffence *= CombatTactics.Attack(attackerTactic, defenderTactic, defines) *
                CombatTactics.EnemyAttack(defenderTactic);
            defenderProtection *= CombatTactics.Defence(defenderTactic) * CombatTactics.EnemyDefence(attackerTactic);
            defenderOffence *= CombatTactics.Attack(defenderTactic, attackerTactic, defines) *
                CombatTactics.EnemyAttack(attackerTactic);
            attackerProtection *= CombatTactics.Defence(attackerTactic) * CombatTactics.EnemyDefence(defenderTactic);

            // The reasons, for the report (CombatFactors): read back, never fed into the round.
            {
                var attack = battle.AttackFactors; attack.Clear();
                var defence = battle.DefenceFactors; defence.Clear();
                var attackerStats = GetBattleNationFactors(campaign, war, battle.AttackerId);
                var defenderStats = GetBattleNationFactors(campaign, war, battle.DefenderId);
                CombatFactors.Add(attack, "Terrain", terrain.Attack);
                CombatFactors.Add(attack, "River crossing", riverAttackMultiplier);
                CombatFactors.Add(attack, "Strait crossing", straitAttackMultiplier);
                CombatFactors.Add(attack, "Frontage", attackerEngagement);
                CombatFactors.Add(attack, "Armour and piercing", attackerPierces);
                CombatFactors.Add(attack, "Guns and aircraft", attackerFire);
                CombatFactors.Add(attack, "Combined arms", attackerCombinedArms);
                CombatFactors.Add(attack, "Plan prepared", 1f + attackerPlanning * defines.MaxPlanningAttackBonus);
                CombatFactors.Add(attack, "Attack from several sides", envelopmentBonus);
                CombatFactors.Add(attack, "Training and equipment", atkPowerWeight <= 0 ? 1f : (float)(atkTrainSum / atkPowerWeight));
                CombatFactors.Add(attack, "Stance", atkPowerWeight <= 0 ? 1f : (float)(atkStanceSum / atkPowerWeight));
                CombatFactors.Add(attack, "Commander", atkPowerWeight <= 0 ? 1f : (float)(atkCmdSum / atkPowerWeight));
                CombatFactors.Add(attack, "Research", atkPowerWeight <= 0 ? 1f : (float)(atkResSum / atkPowerWeight));
                CombatFactors.Add(attack, "Munitions", attackerStats.Munitions);
                CombatFactors.Add(attack, "Encircled", atkPowerWeight <= 0 ? 1f : (float)(atkEncSum / atkPowerWeight));
                CombatFactors.Add(attack, "Fighting spirit", attackerStats.Spirit);
                CombatFactors.Add(attack, "Manoeuvre of the day", CombatTactics.Attack(attackerTactic, defenderTactic, defines) * CombatTactics.EnemyAttack(defenderTactic));
                CombatFactors.Add(attack, "Fortune", attackerFortune);

                CombatFactors.Add(defence, "Terrain", terrain.Defense);
                CombatFactors.Add(defence, "Entrenchment", effectiveEntrenchmentBonus);
                if (directions >= 2 && dugIn > 0.05f) CombatFactors.Add(defence, "Flanking bypass", flankRetention);
                CombatFactors.Add(defence, "Combined arms", defenderCombinedArms);
                CombatFactors.Add(defence, "Shelled", effectiveDefenderStripped);
                CombatFactors.Add(defence, "Homeland", isHomeProvince ? defines.HomelandDefenseBonus : 1f);
                CombatFactors.Add(defence, "Capital", isCapitalState ? defines.CapitalDefenseBonus : 1f);
                CombatFactors.Add(defence, "Attacked from one side", frontalBonus);
                CombatFactors.Add(defence, "Frontage", defenderEngagement);
                CombatFactors.Add(defence, "Training and equipment", defPowerWeight <= 0 ? 1f : (float)(defTrainSum / defPowerWeight));
                CombatFactors.Add(defence, "Stance", defPowerWeight <= 0 ? 1f : (float)(defStanceSum / defPowerWeight));
                CombatFactors.Add(defence, "Commander", defPowerWeight <= 0 ? 1f : (float)(defCmdSum / defPowerWeight));
                CombatFactors.Add(defence, "Research", defPowerWeight <= 0 ? 1f : (float)(defResSum / defPowerWeight));
                CombatFactors.Add(defence, "Encircled", defPowerWeight <= 0 ? 1f : (float)(defEncSum / defPowerWeight));
                CombatFactors.Add(defence, "Fighting spirit", defenderStats.Spirit);
                CombatFactors.Add(defence, "Guerrilla country", 1f + defines.GuerrillaDefence * guerrillaCover);
                CombatFactors.Add(defence, "Manoeuvre of the day", CombatTactics.Defence(defenderTactic) * CombatTactics.EnemyDefence(attackerTactic));
                CombatFactors.Add(defence, "Fortune", defenderFortune);
            }

            var pressureOnDefender = M.Clamp(attackerOffence / M.Max(0.01f, defenderProtection), 0.05f, 14f);
            var pressureOnAttacker = M.Clamp(defenderOffence / M.Max(0.01f, attackerProtection), 0.05f, 14f);
            var ratio = pressureOnDefender;

            var terrainBreakthrough = 1f;
            if (string.Equals(terrain.Id, "urban", StringComparison.OrdinalIgnoreCase))
                terrainBreakthrough = defines.UrbanBreakthroughPenalty;
            else if (string.Equals(terrain.Id, "mountains", StringComparison.OrdinalIgnoreCase))
                terrainBreakthrough = 0.60f;
            else if (string.Equals(terrain.Id, "marsh", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(terrain.Id, "jungle", StringComparison.OrdinalIgnoreCase))
                terrainBreakthrough = 0.75f;

            var isDesperateDefense = isHomeProvince && defenderOrgBefore < 0.38f && defenderOrgBefore > defines.BattleBreakOrganisation;

            // A prepared, supplied attack can unhinge a line; a dug-in, supplied defence can
            // turn a spent one back. Both read only what the battle already knows.
            var breakthrough = CombatTempo.Breakthrough(defines, pressureOnDefender, attackerPlanning, attackerSupply,
                dugIn, directions, terrainBreakthrough);
            var counterattack = CombatTempo.Counterattack(defines, pressureOnAttacker, dugIn, defenderSupply,
                attackerOrgBefore, defenderOrgBefore, isDesperateDefense);

            var defenderOrgLoss = M.Clamp(defines.BaseOrganisationDamage * MathF.Sqrt(pressureOnDefender) *
                CombatTactics.OrgDamage(attackerTactic), defines.MinOrganisationDamage, defines.MaxOrganisationDamage);
            var attackerOrgLoss = M.Clamp(defines.BaseOrganisationDamage * MathF.Sqrt(pressureOnAttacker) *
                CombatTactics.OrgDamage(defenderTactic), defines.MinOrganisationDamage, defines.MaxOrganisationDamage);

            // Encircled zero-supply pocket collapse: trapped formations with exhausted depots lose
            // organisation decisively instead of stalling indefinitely as 1-man zombie stacks.
            var defCollapseCount = 0;
            for (var di = 0; di < DefenderLine.Count; di++)
                if (BattleTelemetry.IsInPocketCollapse(defines, DefenderLine[di])) defCollapseCount++;
            var defCollapseShare = DefenderLine.Count > 0 ? (float)defCollapseCount / DefenderLine.Count : 0f;
            if (defCollapseShare > 0f && defines.PocketCollapseOrgMultiplier > 1f)
            {
                var collapseMult = M.Lerp(1f, defines.PocketCollapseOrgMultiplier, defCollapseShare);
                defenderOrgLoss *= collapseMult;
                CombatFactors.Add(battle.DefenceFactors, "Pocket collapse", 1f / collapseMult);
            }
            var atkCollapseCount = 0;
            for (var ai = 0; ai < AttackerLine.Count; ai++)
                if (BattleTelemetry.IsInPocketCollapse(defines, AttackerLine[ai])) atkCollapseCount++;
            var atkCollapseShare = AttackerLine.Count > 0 ? (float)atkCollapseCount / AttackerLine.Count : 0f;
            if (atkCollapseShare > 0f && defines.PocketCollapseOrgMultiplier > 1f)
            {
                var collapseMult = M.Lerp(1f, defines.PocketCollapseOrgMultiplier, atkCollapseShare);
                attackerOrgLoss *= collapseMult;
                CombatFactors.Add(battle.AttackFactors, "Pocket collapse", 1f / collapseMult);
            }
            battle.LastPhase = BattleTelemetry.ClassifyPhase(terrain.Id, directions, defCollapseShare > 0.25f,
                attackerArmour, attackerPlanning, dugIn, attackerFire, defenderFire, breakthrough, counterattack);
            if (breakthrough > 0f)
            {
                defenderOrgLoss *= 1f + breakthrough * defines.BreakthroughOrganisationBonus;
                attackerOrgLoss *= 1f - breakthrough * defines.BreakthroughAttackerRelief;
            }
            if (counterattack > 0f)
            {
                attackerOrgLoss *= 1f + counterattack * defines.CounterattackOrganisationBonus;
                defenderOrgLoss *= 1f - counterattack * defines.CounterattackDefenderRelief;
            }
            // Ambushes: an attack into guerrilla cover is bled on its way in, by the side roads,
            // the mines and the villages it has to pass (GuerrillaWar).
            attackerOrgLoss *= 1f + defines.GuerrillaAmbush * guerrillaCover;
            // However badly a battle goes, it is still two sides shooting at each other: the
            // worse-off side's daily loss per formation is at most this many times the other's.
            // The bounds above allowed seventeen to one, and ten years of it put seven million
            // North Korean casualties against ninety thousand South Korean ones.
            var exchange = MathF.Max(1f, defines.MaxOrganisationLossRatio);
            attackerOrgLoss = MathF.Min(attackerOrgLoss, defenderOrgLoss * exchange);
            defenderOrgLoss = MathF.Min(defenderOrgLoss, attackerOrgLoss * exchange);
            // And the same for the dead. Thin templates, a broken line and an aggressive stance
            // each multiply a side's casualties after the cap above, so the share of its men a
            // side loses in a day is held to at most this many times the other side's share.
            var defenderScale = 1f - M.Clamp01(defines.GuerrillaEvasion * guerrillaCover);
            var attackerScale = defines.AttackerCasualtyMultiplier;
            var (defenderMen, defenderDead) = CasualtyTotals(campaign, DefenderLine, defenderOrgLoss, defenderScale);
            var (attackerMen, attackerDead) = CasualtyTotals(campaign, AttackerLine, attackerOrgLoss, attackerScale);
            var defenderRate = defenderMen > 1f ? defenderDead / defenderMen : 0f;
            var attackerRate = attackerMen > 1f ? attackerDead / attackerMen : 0f;
            var bleed = MathF.Max(1f, defines.MaxCasualtyRateRatio);
            if (attackerRate > defenderRate * bleed && attackerRate > 0f)
            {
                var cut = defenderRate * bleed / attackerRate;
                attackerScale *= cut; attackerDead *= cut;
            }
            else if (defenderRate > attackerRate * bleed && defenderRate > 0f)
            {
                var cut = attackerRate * bleed / defenderRate;
                defenderScale *= cut; defenderDead *= cut;
            }
            // And in men, not only in shares. What a side can kill is bounded by what is
            // shooting at it, however many stand in front of it: in one bench war China lost
            // twenty-seven men for each Burmese one. The frontage filter does most of that
            // work now, since a stack can only bring its line to bear, but the bound stays:
            // a line of four against one is still four times the men.
            var trade = MathF.Max(1f, defines.MaxCasualtyExchange);
            if (defenderDead > 0f && attackerDead > defenderDead * trade)
                attackerScale *= defenderDead * trade / attackerDead;
            else if (attackerDead > 0f && defenderDead > attackerDead * trade)
                defenderScale *= attackerDead * trade / defenderDead;
            var defenderLoss = ApplyDamage(campaign, DefenderLine, defenderOrgLoss, defenderScale, out var defenderKilled);
            var attackerLoss = ApplyDamage(campaign, AttackerLine, attackerOrgLoss, attackerScale, out var attackerKilled);
            // The reserve is behind the line, not out of the war: the shelling and the
            // mortars that miss the front find the queue standing in the same province. It
            // costs a share (Defines.ReserveExposure), because nothing in that queue is
            // answering back - which is why nineteen divisions waiting are worth far less
            // than nineteen divisions in the line.
            if (DefenderReserve.Count > 0)
            {
                defenderLoss += ApplyDamage(campaign, DefenderReserve,
                    defenderOrgLoss * defines.ReserveExposure, defenderScale, out var defenderReserveKilled);
                defenderKilled += defenderReserveKilled;
            }
            if (AttackerReserve.Count > 0)
            {
                attackerLoss += ApplyDamage(campaign, AttackerReserve,
                    attackerOrgLoss * defines.ReserveExposure, attackerScale, out var attackerReserveKilled);
                attackerKilled += attackerReserveKilled;
            }
            // Planning is spent by the formations that attacked, and only by them: a reserve
            // that never left its start line still has its plan.
            foreach (var army in AttackerLine)
                army.Planning = M.Clamp01(army.Planning - campaign.Defines.PlanningBurnPerCombatDay);
            battle.AttackerLosses += attackerLoss;
            battle.DefenderLosses += defenderLoss;
            // And the ground they fought over: a day of battle wrecks a share of the region.
            WarDamage.Fought(campaign, battle.ProvinceId, attackerLoss + defenderLoss);

            // By side rather than by leader. This compared the battle's attacker with the
            // war's, so an ally's assault in a coalition war put each side's dead on the other.
            WarLosses.Record(war, battle.AttackerId, attackerKilled, attackerLoss - attackerKilled, 0f);
            WarLosses.Record(war, battle.DefenderId, defenderKilled, defenderLoss - defenderKilled, 0f);

            for (var i = defending.Count - 1; i >= 0; i--)
            {
                var army = defending[i];
                if (army.Organisation <= 0.005f &&
                    WarCoalition.SpreadRetreatProvince(campaign, war, army, battle.ProvinceId, beaten: true, slipAway: true, out _) <= 0)
                {
                    if (!Partisans.TryGoToGround(campaign, war, army, battle.ProvinceId))
                    {
                        if (DiagnosticLogs.Enabled)
                            DiagnosticLogs.Write("simulation", $"SURRENDER army={army.Id} nation={army.NationId} province={battle.ProvinceId} war={war.Id} reason=zero_morale_in_pocket");
                        WarLosses.Surrender(campaign, army);
                        Attrition.Destroy(campaign, army, "destroyed: zero morale in pocket");
                    }
                    defending.RemoveAt(i);
                    battle.Defenders.Remove(army.Id);
                    DefenderLine.Remove(army);
                    DefenderReserve.Remove(army);
                }
            }

            var attackerOrg = AverageOrganisation(AttackerLine);
            var defenderOrg = AverageOrganisation(DefenderLine);
            if (DiagnosticLogs.Enabled)
                DiagnosticLogs.Write("simulation", $"BATTLE_ROUND battle={battle.Id} war={battle.WarId} province={battle.ProvinceId} attacker={battle.AttackerId} defender={battle.DefenderId} attack_power={DiagnosticLogs.N(attackerOffence)} defense_power={DiagnosticLogs.N(defenderProtection)} attack_org={DiagnosticLogs.N(attackerOrg)} defense_org={DiagnosticLogs.N(defenderOrg)} attack_losses={DiagnosticLogs.N(attackerLoss)} defense_losses={DiagnosticLogs.N(defenderLoss)} river={DiagnosticLogs.N(battle.RiverCrossingStrength)} planning={DiagnosticLogs.N(attackerPlanning)}");
            battle.Rounds.Add(new LandBattle.Round
            {
                Day = campaign.ElapsedDays,
                AttackerOrganisation = attackerOrg,
                DefenderOrganisation = defenderOrg,
                AttackerDamage = defenderOrgLoss,
                DefenderDamage = attackerOrgLoss,
                AttackerLosses = attackerLoss,
                DefenderLosses = defenderLoss,
                AttackerPower = attackerOffence,
                DefenderPower = defenderProtection,
                AttackerPlanning = attackerPlanning,
                AttackerOrganisationSpent = M.Max(0f, attackerOrgBefore - attackerOrg),
                HasOrganisationTelemetry = true,
                DefenderOrganisationSpent = M.Max(0f, defenderOrgBefore - defenderOrg),
                Note = breakthrough > .45f ? "Breakthrough: the prepared offensive is unhinging the line" :
                    counterattack > .45f ? (isDesperateDefense ? "Desperate counter-attack: defenders thrust into overextended assault" : "Counter-attack: the defence is punishing a spent assault") :
                    attackerPierces < 0.99f ? "Armour is shrugging off the attack" :
                    defenderPierces < 0.99f ? "Defenders cannot pierce the assault" :
                    attackerWantedWidth > usableWidth * 1.35f ? "Too many divisions for the frontage" :
                    directions > 1 ? DirectionWidensNote(directions) :
                    directions == 1 && dugIn > 0.5f ? "Entrenched defenders hold the narrow frontal approach" :
                    isCapitalState && isHomeProvince ? "Stubborn last stand for the capital" :
                    isHomeProvince && dugIn > 0.4f ? "Defenders stubbornly hold their homeland" :
                    attackerPlanning > 0.65f ? "Prepared offensive plan committed" :
                    battle.RiverCrossingStrength > 0.66f ? "Major river crossing favours the defenders" :
                    battle.RiverCrossingStrength > 0f ? "River crossing slows the assault" :
                    CountAggressive(attacking) * 2 >= attacking.Count
                        ? "Aggressive assault accepts heavier losses" :
                    dugIn > 0.55f ? "Defenders are entrenched" :
                    ratio > 1.6f ? "Attacker has local superiority" :
                    ratio < 0.65f ? "Attack is understrength" : "Contested fighting"
            });
            if (battle.Rounds.Count > 90) battle.Rounds.RemoveAt(0);

            var defendersSpent = defending.Count == 0 ||
                (defenderOrg <= campaign.Defines.BattleBreakOrganisation &&
                !HasReadyReserve(DefenderReserve, campaign.Defines.BattleBreakOrganisation));
            var pocketResists = defendersSpent && defending.Count > 0 &&
                PocketResistance.CanContinue(campaign, war, battle.ProvinceId, defending);
            if (pocketResists) battle.Rounds[^1].Note = PocketResistance.BattleNote;
            if (defendersSpent && !pocketResists)
            {
                var occupied = Capture(campaign, war, battle.ProvinceId, battle.AttackerId, attacking, defending,
                    "won the battle");
                Close(campaign, battle, battle.AttackerId, occupied ? "Defenders broke" : "Defenders broke; support did not occupy");
            }
            else if (attackerOrg <= campaign.Defines.BattleBreakOrganisation &&
                !HasReadyReserve(AttackerReserve, campaign.Defines.BattleBreakOrganisation))
            {
                var extraRest = counterattack > 0.30f ? 2 : 0;
                var minRest = Math.Max(campaign.Defines.RestAfterSpentAttackDays, campaign.Defines.RetreatReorgMinDays);
                foreach (var army in attacking)
                {
                    army.ReadyDay = Math.Max(army.ReadyDay, campaign.ElapsedDays + minRest + extraRest);
                    if (counterattack > 0.30f)
                    {
                        army.Planning = 0f;
                        army.Cohesion = M.Clamp01(army.Cohesion - 0.12f);
                    }
                }
                var repelled = counterattack > 0.30f;
                var outcomeMsg = repelled ? "Attack repelled by counter-attack" : "Attack spent";
                Close(campaign, battle, battle.DefenderId, outcomeMsg);
                // A repelled assault does not loiter at the gates: the counter-attack
                // shoves the spent formations back across the line they came from, which
                // is what turns a defended province into a recovered one instead of a
                // siege that resumes the next morning. A merely exhausted attack stops
                // where it stands.
                if (repelled)
                {
                    var map = campaign.ProvinceMap;
                    var controls = campaign.ProvinceController;
                    if (map is not null && controls is not null)
                        foreach (var army in attacking)
                        {
                            if (army.ProvinceId != battle.ProvinceId) continue;
                            var back = WarCoalition.RetreatProvince(campaign, war, army, battle.ProvinceId, lowestId: true);
                            if (back <= 0 || back == battle.ProvinceId) continue;
                            army.ProvinceId = back;
                            army.StateMapIndex = map.ProvinceToState[back];
                            (army.Latitude, army.Longitude) = map.CenterOf(back);
                        }
                    // The formations were pushed back. Patch the province slots they left
                    // and entered. The whole index waits until this day's battles have
                    // all been read.
                    MarkIndexStale(campaign);
                    EnsureIndexed(campaign);
                }
            }
            if (bi + 1 < battleCount && Campaign.StageOverdue()) yield return 0;
        }
        }
        finally
        {
            battleIndex.DeferProvinceRebuild = false;
            if (battleIndex.ProvinceStale) IndexArmies(campaign);
        }
    }

    /// <summary>Everyone standing in one province who could still join the fight next door.</summary>
    private static void Consider(Campaign campaign, LandBattle battle, int provinceId)
    {
        foreach (var army in InProvince(provinceId))
        {
            if (army.InBattle || army.AssignedWarId != battle.WarId ||
                battle.WithdrawnAttackers.Contains(army.Id) || battle.WithdrawnDefenders.Contains(army.Id) ||
                army.ReadyDay > campaign.ElapsedDays) continue;
            Reinforcements.Add(army);
        }
    }

    // Strongest first, and by id where two are equally strong: a battle joined in a
    // different order on two machines is a battle that ends differently on them.
    private static void GatherReinforcements(Campaign campaign, LandBattle battle)
    {
        Reinforcements.Clear();
        Consider(campaign, battle, battle.ProvinceId);
        Reinforcements.Sort(static (left, right) =>
        {
            var byPower = right.Power.CompareTo(left.Power);
            return byPower != 0 ? byPower : left.Id.CompareTo(right.Id);
        });
    }

    private static StrategicWar FindLiveWar(Campaign campaign, int warId)
    {
        foreach (var candidate in Warfare.OpenWars(campaign))
            if (candidate.Id == warId && !candidate.Ended) return candidate;
        return null;
    }

    /// <summary>
    /// Fills this battle's line, reserve and reinforcement scratch. It does not join,
    /// shoot, or write the battle. The day's fighting uses the same lists and then goes
    /// on to the losses. A second campaign's look cannot clear them. Another battle of
    /// this campaign clears only its own.
    /// </summary>
    internal static void FillBattleBuckets(Campaign campaign, LandBattle battle)
    {
        if (campaign is null || battle is null) return;
        UseBattleBuckets(campaign);
        var war = FindLiveWar(campaign, battle.WarId);
        if (war is null) return;
        UseBattleRows(campaign, battle);
        EnsureIndexed(campaign);

        Present.Clear();
        foreach (var other in InProvince(battle.ProvinceId))
            if (war.Opposes(battle.AttackerId, other.NationId) && other.Divisions >= 0.1f) Present.Add(other);

        GatherReinforcements(campaign, battle);
        var attacking = Side(war, battle.Attackers, battle.AttackerId, AttackingSide);
        var defending = Side(war, battle.Defenders, battle.DefenderId, DefendingSide);
        var defines = campaign.Defines;
        var terrain = campaign.ProvinceMap?.TerrainAt(battle.ProvinceId) ?? TerrainCatalog.Get("plains");
        var baseWidth = defines.CombatWidth * terrain.CombatWidth;
        var directions = CountAttackDirections(attacking);
        var usableWidth = baseWidth + defines.CombatWidthPerDirection * terrain.CombatWidth *
            M.Clamp(directions - 1, 0, M.Max(0, defines.MaxAttackDirections - 1));
        FrontageReady(attacking, usableWidth, AttackerLine, AttackerReserve, defines.BattleBreakOrganisation, campaign, battle, true);
        FrontageReady(defending, baseWidth, DefenderLine, DefenderReserve, defines.BattleBreakOrganisation, campaign, battle, false);
    }

    [ThreadStatic] private static HashSet<int> _attackDirectionSet;

    private static int CountAttackDirections(List<Army> attacking)
    {
        if (attacking.Count <= 1) return attacking.Count;
        var set = _attackDirectionSet ??= new HashSet<int>();
        set.Clear();
        for (var i = 0; i < attacking.Count; i++) set.Add(attacking[i].ProvinceId);
        return set.Count;
    }

    private static int CountAggressive(List<Army> armies)
    {
        var count = 0;
        for (var i = 0; i < armies.Count; i++)
            if (armies[i].Stance == ArmyStance.Aggressive) count++;
        return count;
    }

    private static bool HasReadyReserve(List<Army> reserve, float threshold)
    {
        for (var i = 0; i < reserve.Count; i++)
            if (reserve[i].Organisation > threshold) return true;
        return false;
    }

    private static void ReinforceBattle(Campaign campaign, LandBattle battle)
    {
        if (campaign.ProvinceMap is null) return;
        var war = FindLiveWar(campaign, battle.WarId);
        if (war is null) return;
        UseBattleRows(campaign, battle);
        EnsureIndexed(campaign);
        var attackerAdded = 0;
        var defenderAdded = 0;

        // Reinforcements come from the province being fought over, and from nowhere else.
        //
        // A formation standing in a province next door used to join the battle there without
        // marching - two a day, every day, for as long as the fight lasted. Nine days into an
        // assault the defender of Kharkiv was nineteen formations strong in a province that
        // had held one, and the attacker paid a day's casualties for the privilege of
        // shooting into a queue that refilled itself behind his back.
        //
        // Help still arrives: it marches into the province and joins on the day it gets
        // there, which is the road the attacker has to take as well. What is gone is the free
        // passage into somebody else's battle. It also keeps this pass cheap - one province
        // index instead of the province and each of its neighbours.
        GatherReinforcements(campaign, battle);

        foreach (var army in Reinforcements)
        {
            // A formation under a direct order joins the battle it was sent to and no
            // other - being dragged into a fight next door is exactly what a player who
            // pointed at a province did not ask for.
            //
            // A marching formation may still support the neighbouring assault, but an
            // explicitly paused offensive and a formation below the live assault minimum
            // are not free reserves for it. The same readout gates starting and joining,
            // rather than allowing a second path to override the staff's waiting decision.
            var assessment = AssaultReadiness.Assess(campaign, army);
            var attackerReady = assessment.Ready ||
                (assessment.Reason == AssaultReadiness.Reason.PlanPaused &&
                 army.FrontLineId < 0 && army.Planning > 0f &&
                 !campaign.IsHumanControlled(campaign.NationById(army.NationId)) &&
                 assessment.Organisation > assessment.MinimumOrganisation &&
                 assessment.Supply > assessment.MinimumSupply);
            if (WarCoalition.SameSide(war, army.NationId, battle.AttackerId) &&
                (army.OrderKind == ArmyOrder.AdvanceFront || army.OrderKind == ArmyOrder.Spearhead) &&
                attackerReady &&
                (!army.DirectOrder ||
                 (army.PlannedRoute.Count > 0 ? army.PlannedRoute[0] : army.OrderProvinceId) == battle.ProvinceId) &&
                attackerAdded < 2)
            {
                Join(battle, army, true);
                attackerAdded++;
                battle.AttackerReinforcements++;
            }
            else if (WarCoalition.SameSide(war, army.NationId, battle.DefenderId) &&
                     (army.OrderKind == ArmyOrder.DefendFront || army.OrderKind == ArmyOrder.ReserveFront) &&
                     // Defensive reserves need condition, not an active offensive plan.
                     army.Organisation > .16f && army.SupplyLevel > .20f && defenderAdded < 2)
            {
                Join(battle, army, false);
                defenderAdded++;
                battle.DefenderReinforcements++;
            }
        }
    }

    /// <summary>
    /// The formations on one side of a battle, in the order the battle listed them.
    ///
    /// This used to resolve every id with a linear search of the whole army list, once per
    /// side per battle per day: three hundred and sixty battles times fourteen hundred
    /// divisions times the ids on each side. It is a dictionary lookup now, and the result
    /// goes into a buffer the caller owns rather than a fresh list.
    /// </summary>
    private static List<Army> Side(StrategicWar war, List<int> ids, int nationId, List<Army> into)
    {
        into.Clear();
        var byId = ArmiesById();
        for (var i = 0; i < ids.Count; i++)
            if (byId.TryGetValue(ids[i], out var army) && WarCoalition.SameSide(war, army.NationId, nationId) && army.Divisions >= 0.1f)
                into.Add(army);
        return into;
    }

    private static void ComputeLineProfile(List<Army> side, out float hardness, out float armour,
        out float piercing, out float supply, out float recon)
    {
        var divisions = 0f;
        for (var i = 0; i < side.Count; i++) divisions += M.Max(0.01f, side[i].Divisions);
        if (divisions <= 0f)
        {
            hardness = armour = piercing = supply = recon = 0f;
            return;
        }
        var sumHardness = 0f;
        var sumArmour = 0f;
        var sumPiercing = 0f;
        var sumSupply = 0f;
        var sumRecon = 0f;
        for (var i = 0; i < side.Count; i++)
        {
            var army = side[i];
            var w = M.Max(0.01f, army.Divisions);
            var unit = UnitTypeCatalog.Get(army.TemplateId);
            sumHardness += unit.Hardness * w;
            sumArmour += unit.Armour * w;
            sumPiercing += unit.Piercing * w;
            sumSupply += M.Clamp01(army.SupplyLevel) * w;
            sumRecon += army.Recon * w;
        }
        hardness = sumHardness / divisions;
        armour = sumArmour / divisions;
        piercing = sumPiercing / divisions;
        supply = sumSupply / divisions;
        recon = sumRecon / divisions;
    }

    private static void Join(LandBattle battle, Army army, bool attacker)
    {
        var list = attacker ? battle.Attackers : battle.Defenders;
        if (!list.Contains(army.Id)) list.Add(army.Id);
        army.BattleId = battle.Id;
        army.Route.Clear();
        army.MoveProgress = 0f;
    }

    private static void Close(Campaign campaign, LandBattle battle, int winnerId, string outcome)
    {
        if (DiagnosticLogs.Enabled)
            DiagnosticLogs.Write("simulation", $"BATTLE_END battle={battle.Id} war={battle.WarId} province={battle.ProvinceId} winner={winnerId} outcome={outcome}");
        Tally(battle.WarId, outcome);
        battle.Ended = true;
        if (ArmyIndexes.TryGetValue(campaign, out var index)) index.ActiveBattlesDirty = true;
        battle.WinnerId = winnerId;
        battle.Outcome = outcome;
        // Who won it, who lost it, and whether the world will remember its name (Generals).
        Generals.AfterBattle(campaign, battle, winnerId,
            outcome.StartsWith("Defenders broke", StringComparison.Ordinal) || outcome.StartsWith("Attack repelled", StringComparison.Ordinal));
        var armies = campaign.Armies;
        for (var i = 0; i < armies.Count; i++)
            if (armies[i].BattleId == battle.Id) armies[i].BattleId = -1;
    }

    private struct BattleNationFactors
    {
        public Nation Nation;
        public float Munitions;
        public float Spirit;
        public float AtkRes;
        public float DefRes;
    }

    private struct BattleLineUnit
    {
        public float Power;
        public float Enc;
        public float DefRes;
        public float Spirit;
        public float CmdDef;
    }

    [ThreadStatic] private static Dictionary<int, BattleNationFactors> _battleNationFactors;
    [ThreadStatic] private static List<BattleLineUnit> _atkLineUnits;
    [ThreadStatic] private static List<BattleLineUnit> _defLineUnits;

    private static BattleNationFactors GetBattleNationFactors(Campaign campaign, StrategicWar war, int nationId)
    {
        var map = _battleNationFactors ??= new Dictionary<int, BattleNationFactors>(4);
        if (map.TryGetValue(nationId, out var cached)) return cached;
        var nation = campaign.NationById(nationId);
        cached = new BattleNationFactors
        {
            Nation = nation,
            Munitions = StrategicStockpiles.CombatMunitionsFactor(campaign, nation),
            Spirit = GuerrillaWar.Spirit(campaign, war, nation),
            AtkRes = NationalModifiers.Factor(nation, "army_attack"),
            DefRes = NationalModifiers.Factor(nation, "army_defense")
        };
        map[nationId] = cached;
        return cached;
    }

    private static readonly string[] DirectionWidensNotes =
    {
        "", "",
        "Attacking from 2 directions widens the front",
        "Attacking from 3 directions widens the front",
        "Attacking from 4 directions widens the front",
        "Attacking from 5 directions widens the front",
        "Attacking from 6 directions widens the front",
        "Attacking from 7 directions widens the front",
        "Attacking from 8 directions widens the front",
        "Attacking from 9 directions widens the front",
        "Attacking from 10 directions widens the front",
        "Attacking from 11 directions widens the front",
        "Attacking from 12 directions widens the front"
    };

    private static string DirectionWidensNote(int directions) =>
        (uint)directions < (uint)DirectionWidensNotes.Length
            ? DirectionWidensNotes[directions]
            : $"Attacking from {directions} directions widens the front";
}
