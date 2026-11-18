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
}
