using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Small, reviewable corrections for topological errors in the baked political raster.
/// Rules operate on country map indices, so render, picking, AI adjacency and future saves
/// all see the same result. Mods can replace this catalogue without touching the renderer.
/// </summary>
public static partial class TerritorialTopology
{
    private static readonly ConditionalWeakTable<ushort[], HashSet<string>> AppliedRules = new();
    public readonly record struct RepairSummary(
        string SubjectTag,
        string NeighborTag,
        int ChangedCells,
        int ConnectedComponents = 0,
        string ClippedAway = "");

    /// <summary>One share of a region that is being split, and where its centre is.</summary>
    private readonly record struct RegionPiece(
        int MapIndex, string Name, string Iso, string Kind, long Population,
        float Longitude, float Latitude);

    /// <summary>
    /// A region split into several, by growing outwards from a point in each.
    ///
    /// A bounding box cuts along a parallel, and a border drawn along a parallel looks
    /// exactly like what it is - a line somebody ruled across a country. Growing instead
    /// puts the border where the two shares meet, which follows the shape of the ground:
    /// around a bay, along the far side of a mountain, and never straight for long.
    ///
    /// One share may keep the map index of the region being split, which renames it rather
    /// than leaving an empty region behind with nothing in it.
    /// </summary>
    private readonly record struct RegionPartition(
        string NationTag, string CarveFromIso, RegionPiece[] Pieces,
        string[] AlsoCarveFromIso = null, bool Polar = false);

    private readonly record struct AdjacencyRule(
        string SubjectTag,
        string NeighborTag,
        string ThroughTag,
        float MinLongitude,
        float MaxLongitude,
        float MinLatitude,
        float MaxLatitude,
        int CorridorRadius
    );

    private readonly record struct RegionRule(
        int MapIndex,
        string NationTag,
        string SourceNationTag,
        string Name,
        string Iso,
        string Kind,
        string DonorIso,
        long Population,
        float MinLongitude,
        float MaxLongitude,
        float MinLatitude,
        float MaxLatitude,
        GeoPoint[] Polygon,
        RegionSeed[] Seeds,

        /// <summary>
        /// Take ground only from this region, by ISO code. Empty means "any ground the
        /// source country holds inside the box", which is right for a region being pulled
        /// out of open country and wrong for one being split off a province that wraps
        /// around somebody else: a box over Hebei contains Beijing and Tianjin, and
        /// without this the rule would quietly eat two municipalities.
        /// </summary>
        string CarveFromIso = "",
        bool ReplaceExisting = false
    );

    private readonly record struct GeoPoint(float Longitude, float Latitude);
    private readonly record struct RegionSeed(float Longitude, float Latitude, int RadiusCells);
    private readonly record struct CellTransfer(string Id, string FromIso, string ToIso,
        string FromNation, string ToNation, GeoPoint[] Points);
    private static readonly List<CellTransfer> CellTransfers = new();

    private static readonly AdjacencyRule[] DefaultAdjacencyRules =
    {
        // The source admin-1 raster leaves a narrow Georgian artefact between Abkhazia
        // and Russia. In reality Abkhazia reaches the Russian border at the Psou/Caucasus.
        new("ABK", "RUS", "GEO", 39.7f, 42.3f, 42.0f, 44.1f, 8)
    };

    // Simplified OSM relation 65335 (ODbL), sampled near the source raster's
    // 0.0625-degree cell size. It follows the Dniester corridor and includes
    // the Bender/Tighina west-bank pocket instead of repainting a bounding box.
    private static readonly GeoPoint[] TransnistriaBoundary =
    {
        new(29.39072f, 47.37976f), new(29.42705f, 47.29215f), new(29.57406f, 47.37047f),
        new(29.57992f, 47.13576f), new(29.49475f, 47.13152f), new(29.53020f, 47.07545f),
        new(29.61240f, 47.09960f), new(29.57209f, 46.94025f), new(29.97993f, 46.81971f),
        new(29.93517f, 46.80578f), new(29.96328f, 46.58376f), new(29.87597f, 46.54671f),
        new(29.72349f, 46.59912f), new(29.76346f, 46.66829f), new(29.60161f, 46.67928f),
        new(29.65367f, 46.74936f), new(29.56823f, 46.70467f), new(29.51037f, 46.79283f),
        new(29.39328f, 46.75915f), new(29.43278f, 46.84884f), new(29.49008f, 46.85380f),
        new(29.47127f, 46.94954f), new(29.29133f, 46.98543f), new(29.37569f, 46.99451f),
        new(29.39162f, 47.05921f), new(29.11237f, 47.28642f), new(29.17156f, 47.32897f),
        new(29.12353f, 47.32200f), new(29.16122f, 47.35530f), new(29.07571f, 47.41474f),
        new(29.11869f, 47.46349f), new(29.01914f, 47.52503f), new(28.96587f, 47.71888f),
        new(29.02372f, 47.84931f), new(28.82621f, 47.90131f), new(28.69745f, 48.02413f),
        new(28.53715f, 47.99958f), new(28.63523f, 48.07891f), new(28.50294f, 48.15079f),
        new(28.84112f, 48.12554f), new(28.83945f, 48.03495f), new(28.92585f, 47.96014f),
        new(29.17532f, 47.99378f), new(29.20064f, 47.88437f), new(29.28157f, 47.88795f),
        new(29.19854f, 47.81851f), new(29.27639f, 47.80208f), new(29.20436f, 47.71907f),
        new(29.21766f, 47.61229f), new(29.11695f, 47.55442f), new(29.18967f, 47.51397f),
        new(29.18950f, 47.42607f), new(29.31749f, 47.44920f), new(29.33593f, 47.37723f),
        new(29.16769f, 47.30719f), new(29.39072f, 47.37976f)
    };


    // Northern Cyprus: everything on the island north of the Green Line. The southern
    // edge follows the UN buffer-zone alignment; the other three sides are drawn far out
    // to sea on purpose. The painter only ever converts cells the source nation already
    // holds, so overshooting water costs nothing, while a coastline traced by hand at
    // 0.0625-degree resolution left the Karpas peninsula in four disconnected pieces.
    private static readonly GeoPoint[] NorthernCyprusBoundary =
    {
        new(32.20f, 35.10f), new(32.90f, 35.06f), new(33.15f, 35.16f), new(33.35f, 35.19f),
        new(33.45f, 35.16f), new(33.70f, 35.04f), new(33.95f, 34.99f), new(34.15f, 34.99f),
        new(34.95f, 35.95f), new(32.20f, 35.95f), new(32.20f, 35.10f)
    };


    // Nakhchivan. Its border with Armenia runs diagonally from Sadarak in the north-west
    // down to the Aras at Ordubad, so a bounding box cannot describe it: one wide enough
    // to reach Ordubad in the south-east also reaches over Armenia and picks up Azerbaijani
    // ground on the far side. The line below follows the border instead; the southern side
    // is drawn a little into the Aras, which costs nothing because only cells Azerbaijan
    // already holds are ever taken.
    // Nakhchivan. Its border with Armenia runs diagonally from Sadarak in the north-west
    // down to the Aras at Ordubad, so a bounding box cannot describe it: one wide enough to
    // reach Ordubad in the south-east also reaches over Armenia and takes a detached piece
    // of Azerbaijani ground at 46.0-46.2E, 39.6-39.8N that is not Nakhchivan at all. The
    // north-eastern side below follows the border itself and passes some seven cells
    // south-west of that piece. The southern side is drawn out into the Aras, which costs
    // nothing because only cells Azerbaijan already holds are ever taken.
    private static readonly GeoPoint[] NakhchivanBoundary =
    {
        new(44.70f, 39.85f), new(45.42f, 39.72f), new(46.20f, 38.94f), new(46.26f, 38.86f),
        new(45.60f, 38.74f), new(45.00f, 38.90f), new(44.62f, 39.28f), new(44.70f, 39.85f)
    };

    // Stable post-source IDs. These corrections are authoritative state identities,
    // not display aliases: construction, demographics, war goals and province picking
    // all consume the same repaired state raster.
    private static readonly RegionRule[] DefaultRegionRules =
    {
        new(1711, "ABK", "", "Abkhazia", "ABK-X01", "Disputed region", "", 245_000,
            39.7f, 42.4f, 42.0f, 44.1f, Array.Empty<GeoPoint>(), Array.Empty<RegionSeed>()),
        new(1712, "OST", "", "South Ossetia", "OST-X01", "Disputed region", "", 56_500,
            42.8f, 44.9f, 41.6f, 43.1f, Array.Empty<GeoPoint>(), Array.Empty<RegionSeed>()),
        new(1713, "IND", "", "Andaman and Nicobar Islands", "IN-AN", "Union Territory", "IN-RJ", 434_192,
            91.4f, 94.7f, 6.0f, 14.8f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(92.9f, 13.2f, 1), new RegionSeed(92.75f, 11.65f, 1),
                new RegionSeed(92.75f, 9.2f, 1), new RegionSeed(93.85f, 7.0f, 1)
            }),
        new(1714, "IND", "", "Lakshadweep", "IN-LD", "Union Territory", "IN-RJ", 64_473,
            71.0f, 74.2f, 7.5f, 13.8f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(72.19f, 10.85f, 1), new RegionSeed(72.64f, 10.57f, 1),
                new RegionSeed(73.05f, 8.28f, 1)
            }),
        new(1715, "PMR", "MDA", "Transnistria", "PMR-X01", "De facto republic", "", 367_776,
            28.45f, 29.98f, 46.54f, 48.19f, TransnistriaBoundary, Array.Empty<RegionSeed>()),
        new(1716, "MDV", "", "Maldives", "MV-X01", "Atoll republic", "", 530_953,
            72.3f, 73.8f, -0.8f, 7.4f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(73.15f, 6.75f, 1), new RegionSeed(73.48f, 4.18f, 1),
                new RegionSeed(73.10f, 1.95f, 1), new RegionSeed(73.18f, -0.30f, 1)
            }),

        // Vatican City. The source admin-1 raster has no cell for it, so the smallest
        // sovereign state on Earth had a seat in the country table and no ground under it.
        // One seed cell of radius zero, carved out of Rome: a 0.0625-degree cell is about
        // 36 km2 against the real 0.49, so this is the finest the raster can express - but
        // an over-large Vatican that exists, borders Italy and can be picked, negotiated
        // over and fought around is truer to the game than an exact one that does not.
        new(1717, "VAT", "ITA", "Vatican City", "VA-X01", "Ecclesiastical state", "IT-RM", 825,
            12.4534f, 12.4534f, 41.9029f, 41.9029f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(12.4534f, 41.9029f, 0)
            }),

        // Northern Cyprus. 326 000 people, a capital, an army and a de facto border, and
        // no cell of its own in the source raster - the admin-1 layer draws Cyprus whole.
        new(1718, "CYN", "CYP", "Northern Cyprus", "CYN-X01", "De facto republic", "CY-01", 326_000,
            32.20f, 34.95f, 34.99f, 35.95f, NorthernCyprusBoundary, Array.Empty<RegionSeed>()),

        // Nakhchivan. An exclave of Azerbaijan with Armenia between it and the rest of the
        // country - and the source admin-1 table gives Azerbaijan only four districts, so
        // it had been swept into whichever of them the raster happened to reach it with.
        // A region you cannot walk to from the rest of its own region is not one region.
        //
        // A box wide enough to reach Ordubad in the south-east also reached over Armenia
        // and swept in Azerbaijani ground on the far side, so the border is traced instead.
        // The two scraps of Azerbaijani ground the border trace leaves out on the Armenian
        // side - seven and eleven cells north-east of Nakhchivan proper. Without them they
        // fall back to whichever district the source raster reached them with, which turned
        // out to be Salyan: a Caspian district three and a half degrees east of them.
        //
        // Seeds and not a wider polygon. The polygon IS the border, and opening it far
        // enough to reach these also takes the twenty-three-cell piece at 46.0-46.2E that
        // is mainland Azerbaijan rather than Nakhchivan. A seed takes the cells it names
        // and only cells Azerbaijan already holds.
        new(1720, "AZE", "AZE", "Nakhchivan", "AZ-NX", "Autonomous Republic", "", 462_500,
            44.62f, 46.26f, 38.74f, 39.85f, NakhchivanBoundary, new[]
            {
                new RegionSeed(45.71875f, 39.53125f, 1), new RegionSeed(45.90625f, 39.28125f, 1)
            }),

        // Gibraltar. Same story at 6.8 km2: a seed cell taken from Cadiz, over-large for
        // the same reason Vatican City is.
        new(1719, "GIB", "ESP", "Gibraltar", "GI-X01", "Overseas territory", "ES-CA", 33_701,
            -5.3536f, -5.3536f, 36.1408f, 36.1408f, Array.Empty<GeoPoint>(), new[]
            {
                new RegionSeed(-5.3536f, 36.1408f, 0)
            })
    };

    /// <summary>
    /// The population a region correction claims for itself, if any. The economy seeds
    /// nations by spreading their whole population across their regions, which silently
    /// discarded every authored figure for a region belonging to a country that has others:
    /// the Andaman Islands came out with fifty million people in them.
    /// </summary>
    public static bool AuthoredPopulation(int mapIndex, out long population)
    {
        foreach (var rule in RegionRules)
            if (rule.MapIndex == mapIndex && rule.Population > 0)
            {
                population = rule.Population;
                return true;
            }
        // A share of a split region is authored the same way a corrected one is. Without
        // this every prefecture of Hebei came out with the national average living in it -
        // forty million apiece, which is what a country gets when nobody says otherwise.
        foreach (var partition in Partitions)
        foreach (var piece in partition.Pieces)
            if (piece.MapIndex == mapIndex && piece.Population > 0)
            {
                population = piece.Population;
                return true;
            }
        population = 0;
        return false;
    }

    /// <summary>How many authoritative region corrections are loaded, so the scenario
    /// import can assert its own arithmetic instead of a hard-coded total.</summary>
    public static int RegionRuleCount =>
        RegionRules.Count + Partitions.Sum(partition =>
            partition.Pieces.Count(piece => !ShippedStateIndices.Contains(piece.MapIndex))) +
        LakeRegions.CreatedCount;

    private static HashSet<int> _shippedStateIndices;
    public static void InvalidateSourceIndices() => _shippedStateIndices = null;

    /// <summary>
    /// Every region the state table already ships. A partition piece that reuses one of
    /// these renames a region rather than adding one, and must not be counted as a
    /// correction - the scenario import checks that its own arithmetic agrees with this.
    /// </summary>
    private static HashSet<int> ShippedStateIndices
    {
        get
        {
            if (_shippedStateIndices is not null) return _shippedStateIndices;
            _shippedStateIndices = new HashSet<int>(WorldMapTables.StateRows.Length);
            foreach (var row in WorldMapTables.StateRows)
            {
                var sep = row.IndexOf('|');
                var span = sep >= 0 ? row.AsSpan(0, sep) : row.AsSpan();
                if (int.TryParse(span, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
                    _shippedStateIndices.Add(index);
            }
            return _shippedStateIndices;
        }
    }

    private static readonly List<AdjacencyRule> BaseRules = new();
    private static readonly List<RegionRule> RegionRules = new();
    private static readonly List<RegionPartition> Partitions = new();

    static TerritorialTopology() => ResetCatalog();

    public static void ResetCatalog()
    {
        BaseRules.Clear();
        BaseRules.AddRange(DefaultAdjacencyRules);
        RegionRules.Clear();
        RegionRules.AddRange(DefaultRegionRules);
        Partitions.Clear();
        CellTransfers.Clear();
        LakeRegions.ResetCatalog();
        var file = GameDataLoader.DataFiles("map_data", "state_regions").FirstOrDefault();
        if (file is null) return;
        try { LoadOverrides(StructuredData.ReadJson(file), null); }
        catch { /* Compiled corrections deliberately remain bootable. */ }
    }


    public static IReadOnlyList<RepairSummary> Apply(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height)
    {
        var summaries = new List<RepairSummary>(BaseRules.Count + RegionRules.Count);
        var stateByMapIndex = new Dictionary<int, ScenarioState>(campaign.States.Count + RegionRules.Count);
        var stateByIso = new Dictionary<string, ScenarioState>(campaign.States.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < campaign.States.Count; i++)
        {
            var existing = campaign.States[i];
            stateByMapIndex.TryAdd(existing.MapIndex, existing);
            if (!string.IsNullOrEmpty(existing.Iso)) stateByIso.TryAdd(existing.Iso, existing);
        }

        foreach (var rule in RegionRules)
        {
            var nation = campaign.NationByTag(rule.NationTag);
            if (nation is null) continue;
            var sourceNation = string.IsNullOrWhiteSpace(rule.SourceNationTag)
                ? nation
                : campaign.NationByTag(rule.SourceNationTag);
            if (sourceNation is null) continue;
            if (!stateByMapIndex.TryGetValue(rule.MapIndex, out var state))
            {
                state = new ScenarioState
                {
                    MapIndex = rule.MapIndex,
                    NationMapIndex = nation.MapIndex,
                    NationId = nation.Tag,
                    Id = rule.Iso.ToLowerInvariant().Replace('-', '_'),
                    Name = rule.Name,
                    Iso = rule.Iso,
                    Kind = rule.Kind
                };
                campaign.States.Add(state);
                stateByMapIndex[rule.MapIndex] = state;
                if (!string.IsNullOrEmpty(state.Iso)) stateByIso.TryAdd(state.Iso, state);

                // Somebody has to lose the people this region is about to have. A rule may
                // name the region they come from, but naming it by hand is a thing to get
                // wrong: the answer is already on the map, in whichever region currently
                // holds the ground the rule is about to take.
                // Counted once: asked inside the search, the whole box was counted again for
                // every region looked at before the one it named.
                var dominant = string.IsNullOrWhiteSpace(rule.DonorIso)
                    ? DominantState(control, states, width, height, (ushort)sourceNation.MapIndex, rule)
                    : -1;
                var donor = string.IsNullOrWhiteSpace(rule.DonorIso)
                    ? stateByMapIndex.GetValueOrDefault(dominant)
                    : stateByIso.GetValueOrDefault(rule.DonorIso);
                if (donor is not null && donor != state)
                    EconomySystem.AdjustStatePopulation(donor, -rule.Population);
                EconomySystem.SeedState(campaign, state, nation, rule.Population);
            }
            // Which region the ground has to be coming out of, if the rule insists on one.
            //
            // Resolved from the shipped state table rather than from the campaign's live
            // list. The list is still being filled while these rules run - the map compiler
            // reached this line before Hebei was in it and skipped the rule, while the game
            // reached it after and applied it, so the compiler baked provinces that
            // straddled a border the game then drew. A table that is the same at every
            // moment cannot do that.
            var carveFrom = StateIndexByIso(rule.CarveFromIso);

            var changed = PaintRegion(control, states, width, height, (ushort)nation.MapIndex,
                (ushort)sourceNation.MapIndex, (ushort)rule.MapIndex, rule, carveFrom,
                out var connectedComponents, out var clipped);
            summaries.Add(new RepairSummary(rule.Name, "REGION", changed, connectedComponents, clipped));
        }

        if (Partitions.Count > 0)
        {
            var stateToSlot = System.Buffers.ArrayPool<int>.Shared.Rent(ushort.MaxValue + 1);
            Array.Clear(stateToSlot, 0, ushort.MaxValue + 1);
            var batchBuckets = new List<int>[Partitions.Count];
            var canBatch = new bool[Partitions.Count];
            var anyBatch = false;
            try
            {
                for (var pi = 0; pi < Partitions.Count; pi++)
                {
                    var partition = Partitions[pi];
                    if (partition.Polar) continue;
                    var nation = campaign.NationByTag(CountryMerges.OwnerOf(partition.NationTag));
                    var donorIndex = StateIndexByIso(partition.CarveFromIso);
                    if ((nation is null && !CountryMerges.IsMerged(partition.NationTag)) || donorIndex == 0) continue;

                    canBatch[pi] = true;
                    void MarkShare(ushort idx)
                    {
                        var existing = stateToSlot[idx];
                        if (existing == 0)
                            stateToSlot[idx] = pi + 1;
                        else if (existing != pi + 1)
                        {
                            canBatch[existing - 1] = false;
                            canBatch[pi] = false;
                        }
                    }

                    MarkShare(donorIndex);
                    foreach (var piece in partition.Pieces) MarkShare((ushort)piece.MapIndex);
                    foreach (var iso in partition.AlsoCarveFromIso ?? Array.Empty<string>())
                    {
                        var extra = StateIndexByIso(iso);
                        if (extra != 0) MarkShare(extra);
                    }
                    if (canBatch[pi])
                    {
                        batchBuckets[pi] = new List<int>();
                        anyBatch = true;
                    }
                }

                if (anyBatch)
                {
                    for (var cell = 0; cell < states.Length; cell++)
                    {
                        var slot = stateToSlot[states[cell]];
                        if (slot > 0 && canBatch[slot - 1])
                            batchBuckets[slot - 1].Add(cell);
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<int>.Shared.Return(stateToSlot);
            }

            for (var pi = 0; pi < Partitions.Count; pi++)
                ApplyPartition(campaign, control, states, width, height, Partitions[pi], summaries,
                    stateByMapIndex, canBatch[pi] ? batchBuckets[pi] : null);
        }

        // Exact source-checked cells, not a bounding box that could absorb a neighbour.
        // Existing regions only: this does not create regions or renumber provinces.
        foreach (var rule in CellTransfers)
        {
            var from = StateIndexByIso(rule.FromIso);
            var to = StateIndexByIso(rule.ToIso);
            var source = campaign.NationByTag(rule.FromNation);
            var destination = campaign.NationByTag(rule.ToNation);
            if (from == 0 || to == 0 || source is null || destination is null) continue;
            var changed = 0;
            foreach (var point in rule.Points)
            {
                var cell = LatitudeToY(point.Latitude, height) * width + LongitudeToX(point.Longitude, width);
                if (states[cell] != from || control[cell] != source.MapIndex) continue;
                states[cell] = to;
                control[cell] = (ushort)destination.MapIndex;
                changed++;
            }
            if (changed > 0) summaries.Add(new RepairSummary(rule.Id, "CELL_TRANSFER", changed));
        }

        // Water last, so a lake is carved out of whatever region ends up around it rather
        // than being divided along with the land. Every caller gets this - the map
        // compiler and the game reach the map through here, and the two of them drawing
        // different lakes is the one way this could go badly wrong.
        var lakes = LakeRegions.Apply(campaign, control, states, width, height);
        if (lakes > 0) summaries.Add(new RepairSummary($"{lakes:N0} lake(s)", "LAKES", lakes));

        var applied = AppliedRules.GetValue(control, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var rule in BaseRules)
        {
            var ruleKey = $"{rule.SubjectTag}>{rule.NeighborTag}:{rule.CorridorRadius}";
            if (!applied.Add(ruleKey)) continue;
            var subject = campaign.NationByTag(rule.SubjectTag);
            var neighbor = campaign.NationByTag(rule.NeighborTag);
            var through = campaign.NationByTag(rule.ThroughTag);
            if (subject is null || neighbor is null || through is null) continue;
            ScenarioState subjectState = null;
            for (var si = 0; si < campaign.States.Count; si++)
                if (campaign.States[si].NationMapIndex == subject.MapIndex) { subjectState = campaign.States[si]; break; }
            if (subjectState is null) continue;

            var changed = Connect(
                control,
                states,
                width,
                height,
                (ushort)subject.MapIndex,
                (ushort)neighbor.MapIndex,
                (ushort)through.MapIndex,
                (ushort)subjectState.MapIndex,
                rule);

            summaries.Add(new RepairSummary(rule.SubjectTag, rule.NeighborTag, changed));
        }

        // On non-Windows-x64 architectures (Android ARM64, Linux, macOS ARM64), C libm / FMA
        // rounding in polar growth can shift boundary cells by 1 ULP relative to the shipped
        // provinces.png stamp. Reconcile before callers spawn concurrent anchor/topology jobs.
        MapRasterCatalog.ReconcileCrossArchitectureRegions(states, control, width, height);

        return summaries;
    }

    /// <summary>
    /// Heal detached 1–2 province border slivers (including Abkhazia's coastal Samegrelo slivers)
    /// deterministically on a constructed <see cref="ProvinceTopology"/> without invalidating
    /// <c>compiled.json</c>'s source region fingerprint. Idempotent.
    /// </summary>
    public static IReadOnlyList<SliverHealSummary> ApplyProvinceSliverHealing(ProvinceTopology topology) =>
        HealBorderSlivers(topology);

    private static Dictionary<string, ushort> _stateIndexByIso;

    /// <summary>
    /// A region's map index from its ISO code, out of the shipped state table.
    ///
    /// Zero for "no restriction" - both when a rule names nothing and when it names
    /// something that does not exist, because a rule that quietly does nothing at one
    /// moment and everything at another is worse than one that simply always paints.
    /// </summary>
    private static ushort StateIndexByIso(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return 0;
        if (_stateIndexByIso is null)
        {
            _stateIndexByIso = new Dictionary<string, ushort>(WorldMapTables.StateRows.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var row in WorldMapTables.StateRows)
            {
                var span = row.AsSpan();
                var p0 = span.IndexOf('|');
                if (p0 <= 0) continue;
                var s0 = span[..p0];
                span = span[(p0 + 1)..];
                var p1 = span.IndexOf('|');
                if (p1 < 0) continue;
                span = span[(p1 + 1)..];
                var p2 = span.IndexOf('|');
                if (p2 < 0) continue;
                span = span[(p2 + 1)..];
                var p3 = span.IndexOf('|');
                var s3 = p3 >= 0 ? span[..p3] : span;
                if (!int.TryParse(s0, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index) ||
                    index <= 0 || index > ushort.MaxValue || s3.IsEmpty) continue;
                _stateIndexByIso[s3.ToString()] = (ushort)index;
            }
        }
        return _stateIndexByIso.TryGetValue(iso, out var found) ? found : (ushort)0;
    }



    private static int LongitudeToX(float longitude, int width) =>
        Math.Clamp((int)((longitude + 180f) / 360f * width), 0, width - 1);

    private static int LatitudeToY(float latitude, int height) =>
        Math.Clamp((int)((90f - latitude) / 180f * height), 0, height - 1);

    private static float XToLongitude(int x, int width) => -180f + (x + 0.5f) * 360f / width;
    private static float YToLatitude(int y, int height) => 90f - (y + 0.5f) * 180f / height;
}
