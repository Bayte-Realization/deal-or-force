using System;
using System.Collections.Generic;
using System.Threading;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    /// <summary>
    /// Instance-owned transient caches derived from this campaign's state and topology.
    /// Keeping derived tables on the <see cref="Campaign"/> instance prevents closed
    /// campaigns from remaining pinned in static globals and isolates concurrent worlds.
    /// </summary>
    internal readonly CampaignCaches Caches = new();
}

/// <summary>
/// Container of lazily initialized derived caches owned by a single <see cref="Campaign"/>.
/// Every slot is non-persisted and safe to drop or rebuild from authoritative campaign data.
/// </summary>
internal sealed class CampaignCaches
{
    private AiStrategyPostureCache _aiStrategyPosture;
    private AiTemperamentCache _aiTemperament;
    private StateLandProvinceTotalsCache _stateLandTotals;
    private FormableFoundationCache _formableFoundation;
    private FormableRestorationCache _formableRestorations;
    private ArmyGroupIndexCache _armyGroupIndex;
    private MovementStateIndexCache _movementStates;
    private PeacefulFormationCache _peacefulFormation;
    private SecularFrontierCache _secularFrontier;
    private ExclaveSurveyCache _exclaves;
    private WaterfrontRegionCache _waterfrontRegions;
    private PeaceShapeNearCache _peaceShapeNear;
    private ArmyDispositionCache _armyDisposition;
    private CorridorPoreCache _corridorPores;
    private CoupNationMapCache _coupNationMap;
    private SecessionAdjacencyCache _secession;
    private ShipbuildingSlipwaysCache _shipbuildingSlipways;
    private SupportEnabledMemoCache _supportEnabledMemo;
    private SupportStepCache _supportStep;
    private SupportPassCache _supportPass;
    private ProtectorIndex _protectors;

    public AiStrategyPostureCache AiStrategyPosture => LazyInitializer.EnsureInitialized(ref _aiStrategyPosture)!;
    public AiTemperamentCache AiTemperament => LazyInitializer.EnsureInitialized(ref _aiTemperament)!;
    public StateLandProvinceTotalsCache StateLandTotals => LazyInitializer.EnsureInitialized(ref _stateLandTotals)!;
    public FormableFoundationCache FormableFoundation => LazyInitializer.EnsureInitialized(ref _formableFoundation)!;
    public FormableRestorationCache FormableRestorations => LazyInitializer.EnsureInitialized(ref _formableRestorations)!;
    public ArmyGroupIndexCache ArmyGroupIndex => LazyInitializer.EnsureInitialized(ref _armyGroupIndex)!;
    public MovementStateIndexCache MovementStates => LazyInitializer.EnsureInitialized(ref _movementStates)!;
    public PeacefulFormationCache PeacefulFormation => LazyInitializer.EnsureInitialized(ref _peacefulFormation)!;
    public SecularFrontierCache SecularFrontier => LazyInitializer.EnsureInitialized(ref _secularFrontier)!;
    public ExclaveSurveyCache Exclaves => LazyInitializer.EnsureInitialized(ref _exclaves)!;
    public WaterfrontRegionCache WaterfrontRegions => LazyInitializer.EnsureInitialized(ref _waterfrontRegions)!;
    public PeaceShapeNearCache PeaceShapeNear => LazyInitializer.EnsureInitialized(ref _peaceShapeNear)!;
    public ArmyDispositionCache ArmyDisposition => LazyInitializer.EnsureInitialized(ref _armyDisposition)!;
    public CorridorPoreCache CorridorPores => LazyInitializer.EnsureInitialized(ref _corridorPores)!;
    public CoupNationMapCache CoupNationMap => LazyInitializer.EnsureInitialized(ref _coupNationMap)!;
    public SecessionAdjacencyCache Secession => LazyInitializer.EnsureInitialized(ref _secession)!;
    public ShipbuildingSlipwaysCache ShipbuildingSlipways => LazyInitializer.EnsureInitialized(ref _shipbuildingSlipways)!;
    public SupportEnabledMemoCache SupportEnabledMemo => LazyInitializer.EnsureInitialized(ref _supportEnabledMemo)!;
    public SupportStepCache SupportStep => LazyInitializer.EnsureInitialized(ref _supportStep)!;
    public SupportPassCache SupportPass => LazyInitializer.EnsureInitialized(ref _supportPass)!;
    public ProtectorIndex Protectors => LazyInitializer.EnsureInitialized(ref _protectors)!;
    public LongRangeMovement.RepositionScratch RepositionScratch { get; } = new();
    public int NextKinScanRunId;

    internal sealed class AiStrategyPostureCache
    {
        public readonly object Gate = new();
        public long Hour = long.MinValue;
        public AiStrategyCatalog.Posture[] Postures = Array.Empty<AiStrategyCatalog.Posture>();
    }

    internal sealed class AiTemperamentCache
    {
        private readonly object _gate = new();
        private int _day = int.MinValue;
        private readonly Dictionary<int, DealOrForce.Simulation.AiTemperament.Character> _characters = new();

        public bool TryGet(int day, int nationId, out DealOrForce.Simulation.AiTemperament.Character character)
        {
            lock (_gate)
            {
                if (_day != day)
                {
                    _day = day;
                    _characters.Clear();
                }
                return _characters.TryGetValue(nationId, out character);
            }
        }

        public void Store(int day, int nationId, DealOrForce.Simulation.AiTemperament.Character character)
        {
            lock (_gate)
            {
                if (_day == day) _characters[nationId] = character;
            }
        }
    }

    internal sealed class StateLandProvinceTotalsCache
    {
        public readonly object Gate = new();
        public ProvinceTopology Map;
        public int OwnershipRevision = int.MinValue;
        public Dictionary<int, int> Totals;
    }

    internal sealed class FormableFoundationCache
    {
        private readonly object _gate = new();
        private int _ownershipRevision = int.MinValue;
        private readonly Dictionary<string, (HashSet<int> Provinces, Dictionary<int, int> Foundation)> _entries = new();

        public bool TryGet(int ownershipRevision, string definitionId, out (HashSet<int> Provinces, Dictionary<int, int> Foundation) entry)
        {
            lock (_gate)
            {
                if (_ownershipRevision != ownershipRevision)
                {
                    _ownershipRevision = ownershipRevision;
                    _entries.Clear();
                }
                return _entries.TryGetValue(definitionId, out entry);
            }
        }

        public void Store(int ownershipRevision, string definitionId, (HashSet<int> Provinces, Dictionary<int, int> Foundation) entry)
        {
            lock (_gate)
            {
                if (_ownershipRevision == ownershipRevision) _entries[definitionId] = entry;
            }
        }
    }

    internal sealed class FormableRestorationCache
    {
        public readonly object Gate = new();
        public int Day = int.MinValue;
        public int OwnershipRevision = int.MinValue;
        public int MovementRevision = int.MinValue;
        public int NationCount = -1;
        public int FallenCount = -1;
        public readonly List<FormableNation> Building = new();
        public readonly HashSet<string> LivingTags = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> FormedIds = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, List<ScenarioState>> StatesByRegion = new();
        public readonly Stack<List<ScenarioState>> StateListPool = new();
        public readonly HashSet<int> BrokenRegions = new();
        public readonly HashSet<int> SeenStates = new();
        public FormableNation[] Published = Array.Empty<FormableNation>();
    }

    internal sealed class ArmyGroupIndexCache
    {
        public readonly object Gate = new();
        public int GroupCount = -1;
        public readonly Dictionary<int, ArmyGroup> ById = new();

        public void Invalidate()
        {
            lock (Gate) { GroupCount = -1; }
        }
    }

    internal sealed class MovementStateIndexCache
    {
        public readonly object Gate = new();
        public int MapRevision = -1;
        public int StateCount = -1;
        public readonly Dictionary<int, ScenarioState> ByMapIndex = new();
    }

    internal sealed class PeacefulFormationCache
    {
        public readonly object TerritoryGate = new();
        public int TerritoryOwnershipRevision = int.MinValue;
        public int TerritoryStateCount = -1;
        public readonly Dictionary<string, int[]> Territory = new();
        public readonly Dictionary<string, HashSet<int>> LandTerritory = new();

        public readonly object RegionGate = new();
        public int RegionOwnershipRevision = int.MinValue;
        public int RegionStateCount = -1;
        public readonly Dictionary<string, int[]> Regions = new();
    }

    internal sealed class SecularFrontierCache
    {
        public readonly object Gate = new();
        public int Day = -1;
        public double Value;
        public readonly List<double> PerHeadScratch = new();
    }

    internal sealed class ExclaveSurveyCache
    {
        private readonly object _gate = new();
        private ProvinceTopology _map;
        private int _mapRevision = -1;
        private int _capitalRevision = -1;
        private readonly Dictionary<int, StrategicExclaves.Survey> _surveys = new();

        public bool TryGet(ProvinceTopology map, int mapRevision, int capitalRevision, int nationMapIndex, out StrategicExclaves.Survey survey)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_map, map) || _mapRevision != mapRevision || _capitalRevision != capitalRevision)
                {
                    _surveys.Clear();
                    _map = map;
                    _mapRevision = mapRevision;
                    _capitalRevision = capitalRevision;
                }
                return _surveys.TryGetValue(nationMapIndex, out survey);
            }
        }

        public void Store(ProvinceTopology map, int mapRevision, int capitalRevision, int nationMapIndex, StrategicExclaves.Survey survey)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_map, map) && _mapRevision == mapRevision && _capitalRevision == capitalRevision)
                    _surveys[nationMapIndex] = survey;
            }
        }
    }

    internal sealed class WaterfrontRegionCache
    {
        public readonly object Gate = new();
        public ProvinceTopology Map;
        public int StateCount = -1;
        public int MapRevision = -1;
        public readonly Dictionary<int, WaterAccess> ByRegion = new();
    }

    internal sealed class PeaceShapeNearCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, (int Revision, bool Near)> _entries = new();

        public bool TryGet(long key, int mapRevision, out bool near)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var known) && known.Revision == mapRevision)
                {
                    near = known.Near;
                    return true;
                }
                near = false;
                return false;
            }
        }

        public void Store(long key, int mapRevision, bool near)
        {
            lock (_gate)
            {
                _entries[key] = (mapRevision, near);
            }
        }
    }

    internal sealed class ArmyDispositionCache
    {
        public int Day = -1;
        public int Count = -1;
        public readonly Dictionary<int, List<Army>> ByNation = new();
        public readonly Stack<List<Army>> ListPool = new();

        public void Clear()
        {
            foreach (var list in ByNation.Values)
            {
                list.Clear();
                ListPool.Push(list);
            }
            ByNation.Clear();
            Day = -1;
            Count = -1;
        }
    }

    internal sealed class CorridorPoreCache
    {
        public int Revision = -1;
        public ProvinceTopology Map;
        public int Limit = -1;
        public ushort[] Owners = Array.Empty<ushort>();
        public ushort[] States = Array.Empty<ushort>();
        public bool[] Seen = Array.Empty<bool>();
        public readonly Queue<int> Queue = new();
        public readonly HashSet<int> Rim = new();
        public readonly Dictionary<int, HashSet<int>> Pores = new();
        public readonly Dictionary<int, List<HashSet<int>>> Components = new();
        public readonly Dictionary<int, int> FirstProvince = new();
        public readonly Dictionary<uint, int> OwnerStateCounts = new();
        public bool Ready;
    }

    internal sealed class CoupNationMapCache
    {
        private readonly object _gate = new();
        private int _day = int.MinValue;
        private int _count = -1;
        private readonly Dictionary<int, Nation> _byMapIndex = new();

        public Nation NationAt(Campaign campaign, int mapIndex)
        {
            lock (_gate)
            {
                if (_day != campaign.ElapsedDays || _count != campaign.Nations.Count)
                {
                    _day = campaign.ElapsedDays;
                    _count = campaign.Nations.Count;
                    _byMapIndex.Clear();
                    foreach (var nation in campaign.Nations)
                        if (nation.MapIndex >= 0) _byMapIndex[nation.MapIndex] = nation;
                }
                return _byMapIndex.GetValueOrDefault(mapIndex);
            }
        }
    }

    internal sealed class SecessionAdjacencyCache
    {
        public ProvinceTopology Map;
        public int OwnershipRevision = int.MinValue;
        public int StateCount = -1;
        public readonly Dictionary<int, List<int>> GlobalAdjacent = new();
        public readonly Dictionary<int, ScenarioState> ByRegion = new();
        public readonly Dictionary<int, List<int>> Adjacent = new();
    }

    internal sealed class ShipbuildingSlipwaysCache
    {
        public readonly object Gate = new();
        public int Day = -1;
        public readonly Dictionary<int, float> ByNation = new();
    }

    internal sealed class SupportEnabledMemoCache
    {
        public bool Active;
        public readonly List<(string TypeId, bool Enabled)> Entries = new();
    }

    internal sealed class SupportPassCache
    {
        public readonly struct CutOffSearchKey : IEquatable<CutOffSearchKey>
        {
            public readonly int NationId;
            public readonly int At;
            public readonly int MovementMode;
            public readonly long Shut;
            public readonly int Seat;
            public readonly int FerryBits;

            public CutOffSearchKey(int nationId, int at, int movementMode, long shut, int seat, int ferryBits)
            {
                NationId = nationId;
                At = at;
                MovementMode = movementMode;
                Shut = shut;
                Seat = seat;
                FerryBits = ferryBits;
            }

            public bool Equals(CutOffSearchKey other) =>
                NationId == other.NationId && At == other.At && MovementMode == other.MovementMode &&
                Shut == other.Shut && Seat == other.Seat && FerryBits == other.FerryBits;

            public override bool Equals(object obj) => obj is CutOffSearchKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(NationId, At, MovementMode, Shut, Seat, FerryBits);
        }

        public readonly struct PocketCutOffKey : IEquatable<PocketCutOffKey>
        {
            public readonly int NationId;
            public readonly int At;
            public readonly int MovementMode;
            public readonly long Shut;
            public readonly int Seat;

            public PocketCutOffKey(int nationId, int at, int movementMode, long shut, int seat)
            {
                NationId = nationId;
                At = at;
                MovementMode = movementMode;
                Shut = shut;
                Seat = seat;
            }

            public bool Equals(PocketCutOffKey other) =>
                NationId == other.NationId && At == other.At && MovementMode == other.MovementMode &&
                Shut == other.Shut && Seat == other.Seat;

            public override bool Equals(object obj) => obj is PocketCutOffKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(NationId, At, MovementMode, Shut, Seat);
        }

        public readonly struct PocketCutOffEntry
        {
            public readonly ulong PassableHash;
            public readonly ulong HostileHash;
            public readonly bool HadDepartures;
            public readonly int GlobalEpoch;
            public readonly int[] WatchProvinces;
            public readonly ulong WatchStateHash;

            public PocketCutOffEntry(ulong passableHash, ulong hostileHash, bool hadDepartures,
                int globalEpoch, int[] watchProvinces, ulong watchStateHash)
            {
                PassableHash = passableHash;
                HostileHash = hostileHash;
                HadDepartures = hadDepartures;
                GlobalEpoch = globalEpoch;
                WatchProvinces = watchProvinces;
                WatchStateHash = watchStateHash;
            }
        }

        public const int MaxCutOffEntries = 512;

        public int LastWithdrawChanged;
        public readonly List<SupportFormation> MovementOrdered = new();
        public readonly List<SupportOrder> TrainOrders = new();
        public readonly Dictionary<int, float> RecoverCrowding = new();
        public readonly Dictionary<int, float> RecoverUsedByState = new();
        public readonly HashSet<int> CommandWars = new();
        public readonly Dictionary<int, bool> CommandEnemyFlies = new();
        public readonly List<SupportFormation> ExposeFlights = new();
        public readonly List<(int Province, float Latitude)> ExposeEnemyProvs = new();
        public readonly HashSet<int> ExposeOwn = new();
        public readonly HashSet<int> ExposeEnemy = new();
        public readonly HashSet<int> ExposeEnemyMaps = new();
        public readonly SupportCore.SideReach ExposeOwnReach = new();
        public readonly SupportCore.SideReach ExposeEnemyReach = new();
        public readonly List<SupportFormation> FireDue = new();
        public readonly Dictionary<int, LongRangeStrikes.Sides> FireSides = new();
        public readonly HashSet<int> ProcureAtWar = new();
        public readonly Dictionary<int, float> ProcureUsedByState = new();
        public readonly List<ScenarioState> ProcureTopRegions = new(12);
        public readonly LongRangeStrikes.Sides ProcureSides = new();

        // Cross-day memoization of failed cut-off withdrawal searches:
        // - CutOffFailed covers Air wings and large (>256 province) searches within the same global epoch.
        // - PocketCutOffFailed stores pocket-local WatchProvinces + hashes so landlocked pockets survive
        //   unrelated global wars/battles/captures cleanly with zero stale hits.
        public readonly HashSet<CutOffSearchKey> CutOffFailed = new();
        public readonly Queue<CutOffSearchKey> CutOffOrder = new();
        public readonly Dictionary<PocketCutOffKey, PocketCutOffEntry> PocketCutOffFailed = new();
        public readonly Queue<PocketCutOffKey> PocketCutOffOrder = new();
        public int CutOffGlobalEpoch;
        public ProvinceTopology CutOffMap;
        public int CutOffMapRevision = int.MinValue;
        public int CutOffWarRevision = int.MinValue;
        public int CutOffAllianceRevision = int.MinValue;
        public int CutOffStrategicRevision = int.MinValue;
        public int CutOffProtectorVersion = int.MinValue;
        public int CutOffTreatyVersion = int.MinValue;
        public int CutOffSeaPassageRevision = int.MinValue;
        public int CutOffNationCount = -1;
        public int CutOffRelationCount = -1;
        public int CutOffDestroyedCount = -1;
        public int CutOffPortDays;
        public float CutOffSeaKm;
        public float CutOffAirKm;
        public float CutOffFerryFactor;
        public float CutOffFerryMin;
        public float CutOffFerryMax;
        public float CutOffSlackKm;
        public ulong[] CutOffBattleWords = Array.Empty<ulong>();
        public int CutOffBattleWordCount;
    }
}
