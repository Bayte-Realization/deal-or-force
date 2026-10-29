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
public static class TerritorialTopology
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
            _shippedStateIndices = new HashSet<int>();
            foreach (var row in WorldMapTables.StateRows)
            {
                var parts = row.Split('|');
                if (parts.Length > 0 && int.TryParse(parts[0], out var index)) _shippedStateIndices.Add(index);
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

    public static void LoadOverrides(string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (document.RootElement.TryGetProperty("regions", out var regions) && regions.ValueKind == JsonValueKind.Array)
        foreach (var value in regions.EnumerateArray())
        {
            try
            {
                var rule = new RegionRule(
                    value.GetProperty("map_index").GetInt32(), value.GetProperty("nation").GetString() ?? "",
                    value.TryGetProperty("source_nation", out var sourceNation) ? sourceNation.GetString() ?? "" : "",
                    value.GetProperty("name").GetString() ?? "", value.GetProperty("iso").GetString() ?? "",
                    value.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "Region" : "Region",
                    value.TryGetProperty("donor_iso", out var donor) ? donor.GetString() ?? "" : "",
                    value.TryGetProperty("population", out var population) ? population.GetInt64() : 0,
                    value.GetProperty("min_longitude").GetSingle(), value.GetProperty("max_longitude").GetSingle(),
                    value.GetProperty("min_latitude").GetSingle(), value.GetProperty("max_latitude").GetSingle(),
                    value.TryGetProperty("polygon", out var polygon) ? ParsePolygon(polygon) : Array.Empty<GeoPoint>(),
                    value.TryGetProperty("seed_points", out var seeds) ? ParseSeeds(seeds) : Array.Empty<RegionSeed>(),
                    value.TryGetProperty("carve_from_iso", out var carveFrom) ? carveFrom.GetString() ?? "" : "",
                    value.TryGetProperty("replace_existing", out var replace) && replace.GetBoolean());
                var index = RegionRules.FindIndex(item => item.MapIndex == rule.MapIndex);
                if (index >= 0) RegionRules[index] = rule; else RegionRules.Add(rule);
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data region override: {exception.Message}"); }
        }
        if (document.RootElement.TryGetProperty("partitions", out var partitions) &&
            partitions.ValueKind == JsonValueKind.Array)
        foreach (var value in partitions.EnumerateArray())
        {
            try
            {
                var pieces = new List<RegionPiece>();
                if (value.TryGetProperty("pieces", out var listed) && listed.ValueKind == JsonValueKind.Array)
                    foreach (var piece in listed.EnumerateArray())
                    {
                        var seed = piece.GetProperty("seed");
                        pieces.Add(new RegionPiece(
                            piece.GetProperty("map_index").GetInt32(),
                            piece.GetProperty("name").GetString() ?? "",
                            piece.TryGetProperty("iso", out var iso) ? iso.GetString() ?? "" : "",
                            piece.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "Region" : "Region",
                            piece.TryGetProperty("population", out var people) ? people.GetInt64() : 0,
                            seed[0].GetSingle(), seed[1].GetSingle()));
                    }
                if (pieces.Count < 2) continue;
                var also = new List<string>();
                if (value.TryGetProperty("also_carve_from_iso", out var more) && more.ValueKind == JsonValueKind.Array)
                    foreach (var iso in more.EnumerateArray())
                        if (iso.GetString() is { Length: > 0 } code) also.Add(code);
                Partitions.Add(new RegionPartition(
                    value.GetProperty("nation").GetString() ?? "",
                    value.GetProperty("carve_from_iso").GetString() ?? "",
                    pieces.ToArray(),
                    also.ToArray(),
                    value.TryGetProperty("polar", out var polar) && polar.ValueKind == JsonValueKind.True));
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data partition: {exception.Message}"); }
        }

        if (document.RootElement.TryGetProperty("cell_transfers", out var transfers) && transfers.ValueKind == JsonValueKind.Array)
        foreach (var value in transfers.EnumerateArray())
        {
            try
            {
                var points = value.GetProperty("points").EnumerateArray().Select(p =>
                    new GeoPoint(p[0].GetSingle(), p[1].GetSingle())).ToArray();
                if (points.Length == 0 || points.Any(p => !float.IsFinite(p.Longitude) ||
                    !float.IsFinite(p.Latitude) || Math.Abs(p.Longitude) > 180 || Math.Abs(p.Latitude) > 90))
                    throw new InvalidDataException("cell transfers require finite geographic points");
                var rule = new CellTransfer(value.GetProperty("id").GetString() ?? "",
                    value.GetProperty("from_iso").GetString() ?? "", value.GetProperty("to_iso").GetString() ?? "",
                    value.GetProperty("from_nation").GetString() ?? "", value.GetProperty("to_nation").GetString() ?? "", points);
                if (string.IsNullOrWhiteSpace(rule.Id)) throw new InvalidDataException("cell transfer needs an id");
                CellTransfers.RemoveAll(r => r.Id == rule.Id);
                CellTransfers.Add(rule);
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data cell transfer: {exception.Message}"); }
        }

        if (document.RootElement.TryGetProperty("adjacency_repairs", out var adjacency) && adjacency.ValueKind == JsonValueKind.Array)
        foreach (var value in adjacency.EnumerateArray())
        {
            try
            {
                var rule = new AdjacencyRule(
                    value.GetProperty("subject").GetString() ?? "", value.GetProperty("neighbor").GetString() ?? "",
                    value.GetProperty("through").GetString() ?? "", value.GetProperty("min_longitude").GetSingle(),
                    value.GetProperty("max_longitude").GetSingle(), value.GetProperty("min_latitude").GetSingle(),
                    value.GetProperty("max_latitude").GetSingle(), value.GetProperty("corridor_radius").GetInt32());
                var index = BaseRules.FindIndex(item => item.SubjectTag == rule.SubjectTag && item.NeighborTag == rule.NeighborTag);
                if (index >= 0) BaseRules[index] = rule; else BaseRules.Add(rule);
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data adjacency repair: {exception.Message}"); }
        }
    }

    public static IReadOnlyList<RepairSummary> Apply(
        Campaign campaign,
        ushort[] control,
        ushort[] states,
        int width,
        int height)
    {
        var summaries = new List<RepairSummary>(BaseRules.Count + RegionRules.Count);
        foreach (var rule in RegionRules)
        {
            var nation = campaign.NationByTag(rule.NationTag);
            if (nation is null) continue;
            var sourceNation = string.IsNullOrWhiteSpace(rule.SourceNationTag)
                ? nation
                : campaign.NationByTag(rule.SourceNationTag);
            if (sourceNation is null) continue;
            var state = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == rule.MapIndex);
            if (state is null)
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
                    ? campaign.States.FirstOrDefault(candidate => candidate.MapIndex == dominant)
                    : campaign.States.FirstOrDefault(candidate =>
                        string.Equals(candidate.Iso, rule.DonorIso, StringComparison.OrdinalIgnoreCase));
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

        foreach (var partition in Partitions)
            ApplyPartition(campaign, control, states, width, height, partition, summaries);

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
            var subjectState = campaign.States.FirstOrDefault(state => state.NationMapIndex == subject.MapIndex);
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

        return summaries;
    }

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
            _stateIndexByIso = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in WorldMapTables.StateRows)
            {
                var parts = row.Split('|');
                if (parts.Length < 4) continue;
                if (!int.TryParse(parts[0], out var index) || index <= 0 || index > ushort.MaxValue) continue;
                _stateIndexByIso[parts[3]] = (ushort)index;
            }
        }
        return _stateIndexByIso.TryGetValue(iso, out var found) ? found : (ushort)0;
    }

    /// <summary>
    /// Split one region into several, growing each share out of its own seed until the
    /// shares meet. Every cell goes to whichever seed reaches it first, so a share can only
    /// take ground it can actually walk to - a piece cut off by a bay belongs to whoever is
    /// on its side of the water, not to whoever is nearer as the crow flies.
    /// </summary>
    private static void ApplyPartition(Campaign campaign, ushort[] control, ushort[] states,
        int width, int height, RegionPartition partition, List<RepairSummary> summaries)
    {
        var nation = campaign.NationByTag(partition.NationTag);
        var donorIndex = StateIndexByIso(partition.CarveFromIso);
        if (nation is null || donorIndex == 0) return;

        // The ground being divided is the region plus everything already divided out of
        // it. Taking only what still carries the original index would re-divide the
        // leftovers on a second pass, and this runs twice - once as the campaign is built
        // and once when the map is repaired - so the map compiler and the game would
        // disagree about where the borders are, and the game would refuse its own map.
        var shares = new HashSet<ushort> { donorIndex };
        foreach (var piece in partition.Pieces) shares.Add((ushort)piece.MapIndex);
        // Other regions whose ground is divided with it, each kept by the share that has its index.
        var carved = new HashSet<ushort> { donorIndex };
        foreach (var iso in partition.AlsoCarveFromIso ?? Array.Empty<string>())
        {
            var extra = StateIndexByIso(iso);
            if (extra == 0) continue;
            shares.Add(extra);
            carved.Add(extra);
        }
        if (partition.Polar)
        {
            // Growing a million and a half cells is not free, and the game asks twice: once as
            // the campaign is built and once when the globe binds it, both on the one raster.
            ApplyPartitionRegions(campaign, nation, donorIndex, carved, partition);
            var polarCounts = AppliedPolar.TryGetValue(states, out var done) && done.Contains(partition.CarveFromIso)
                ? CountShares(states, shares)
                : GrowPolar(states, width, height, shares, partition.Pieces);
            AppliedPolar.GetValue(states, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(partition.CarveFromIso);
            foreach (var piece in partition.Pieces)
                summaries.Add(new RepairSummary(piece.Name, "REGION", polarCounts.GetValueOrDefault((ushort)piece.MapIndex)));
            return;
        }
        // A table of the shares rather than the set: this asks it of every cell in the world.
        var sharing = new bool[ushort.MaxValue + 1];
        foreach (var share in shares) sharing[share] = true;
        var ground = new List<int>();
        for (var cell = 0; cell < states.Length; cell++)
            if (sharing[states[cell]]) ground.Add(cell);
        if (ground.Count == 0) return;

        var donor = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == donorIndex);

        // Each share is a region of its own, except the one that keeps the index of what is
        // being split - that one is the same region under a truer name.
        foreach (var piece in partition.Pieces)
        {
            var state = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == piece.MapIndex);
            if (state is null)
            {
                state = new ScenarioState
                {
                    MapIndex = piece.MapIndex,
                    NationMapIndex = nation.MapIndex,
                    NationId = nation.Tag,
                    Id = piece.Iso.ToLowerInvariant().Replace('-', '_'),
                    Name = piece.Name,
                    Iso = piece.Iso,
                    Kind = piece.Kind
                };
                campaign.States.Add(state);
                if (donor is not null && piece.Population > 0)
                    EconomySystem.AdjustStatePopulation(donor, -piece.Population);
                EconomySystem.SeedState(campaign, state, nation, piece.Population);
            }
            else if (carved.Contains((ushort)piece.MapIndex) && !string.IsNullOrWhiteSpace(piece.Name))
            {
                state.Name = piece.Name;
                if (!string.IsNullOrWhiteSpace(piece.Iso))
                {
                    state.Iso = piece.Iso;
                    state.Id = piece.Iso.ToLowerInvariant().Replace('-', '_');
                }
                if (!string.IsNullOrWhiteSpace(piece.Kind)) state.Kind = piece.Kind;
            }
        }

        // Where each share starts: the piece of ground nearest the point it was given.
        var owner = new Dictionary<int, ushort>(ground.Count);
        var reached = new Dictionary<int, int>(ground.Count);
        var frontier = new PriorityQueue<int, int>();
        foreach (var piece in partition.Pieces)
        {
            var seedX = LongitudeToX(piece.Longitude, width);
            var seedY = LatitudeToY(piece.Latitude, height);
            var best = -1;
            var bestDistance = long.MaxValue;
            foreach (var cell in ground)
            {
                var dy = (long)(cell / width) - seedY;
                var dx = (long)(cell % width) - seedX;
                if (dx > width / 2) dx -= width;
                if (dx < -width / 2) dx += width;
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = cell;
            }
            if (best < 0 || owner.ContainsKey(best)) continue;
            owner[best] = (ushort)piece.MapIndex;
            reached[best] = 0;
            frontier.Enqueue(best, 0);
        }
        if (frontier.Count == 0) return;

        // Ground does not cost the same everywhere.
        //
        // Growing at one step a cell puts the border exactly halfway between two seeds,
        // and halfway between two points is a straight line - which is what a ruled border
        // looks like, and the whole thing this was meant to avoid. Two seeds on the same
        // latitude produced a dead vertical edge a hundred cells long.
        //
        // So each cell costs a little more or a little less, from smooth noise fixed to the
        // map rather than to the run. The front advances faster through some country than
        // others and the border wanders, the way a border between two places that grew
        // towards each other actually wanders. It is the same wander every time: the
        // compiler and the game have to agree about it to the cell.
        var inside = new HashSet<int>(ground);
        while (frontier.Count > 0)
        {
            if (!frontier.TryDequeue(out var cell, out var cost)) break;
            if (reached.TryGetValue(cell, out var known) && cost > known) continue;
            var mine = owner[cell];
            var y = cell / width;
            var x = cell % width;
            for (var side = 0; side < 4; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                if (ny < 0 || ny >= height) continue;
                nx = (nx + width) % width;
                var next = ny * width + nx;
                if (!inside.Contains(next)) continue;
                var step = cost + GroundCost(nx, ny);
                if (reached.TryGetValue(next, out var seen) && seen <= step) continue;
                reached[next] = step;
                owner[next] = mine;
                frontier.Enqueue(next, step);
            }
        }

        TidyStrayPieces(states, width, height, ground, owner);

        var counts = new Dictionary<ushort, int>();
        foreach (var (cell, index) in owner)
        {
            // A stray with no neighbour to give it to keeps what it had. Writing the
            // sentinel would put a region number on the map that no region answers to,
            // and a province grown on it belongs to nowhere.
            if (index == ushort.MaxValue) continue;
            states[cell] = index;
            counts[index] = counts.GetValueOrDefault(index) + 1;
        }
        foreach (var piece in partition.Pieces)
            summaries.Add(new RepairSummary(piece.Name, "REGION",
                counts.GetValueOrDefault((ushort)piece.MapIndex)));
    }

    private static readonly ConditionalWeakTable<ushort[], HashSet<string>> AppliedPolar = new();

    /// <summary>The regions of a partition, made or renamed - the part every campaign needs, whoever grew the raster.</summary>
    private static void ApplyPartitionRegions(Campaign campaign, Nation nation, ushort donorIndex,
        HashSet<ushort> carved, RegionPartition partition)
    {
        var donor = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == donorIndex);
        foreach (var piece in partition.Pieces)
        {
            var state = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == piece.MapIndex);
            if (state is null)
            {
                state = new ScenarioState
                {
                    MapIndex = piece.MapIndex,
                    NationMapIndex = nation.MapIndex,
                    NationId = nation.Tag,
                    Id = piece.Iso.ToLowerInvariant().Replace('-', '_'),
                    Name = piece.Name,
                    Iso = piece.Iso,
                    Kind = piece.Kind
                };
                campaign.States.Add(state);
                if (donor is not null && piece.Population > 0)
                    EconomySystem.AdjustStatePopulation(donor, -piece.Population);
                EconomySystem.SeedState(campaign, state, nation, piece.Population);
            }
            else if (carved.Contains((ushort)piece.MapIndex) && !string.IsNullOrWhiteSpace(piece.Name))
            {
                state.Name = piece.Name;
                if (!string.IsNullOrWhiteSpace(piece.Kind)) state.Kind = piece.Kind;
            }
        }
    }

    private static Dictionary<ushort, int> CountShares(ushort[] states, HashSet<ushort> shares)
    {
        var member = new bool[ushort.MaxValue + 1];
        foreach (var share in shares) member[share] = true;
        var counts = new Dictionary<ushort, int>();
        foreach (var index in states)
            if (member[index]) counts[index] = counts.GetValueOrDefault(index) + 1;
        return counts;
    }

    /// <summary>
    /// A partition grown on the ground rather than on the raster, for land near a pole.
    ///
    /// A cell of the raster is a sixteenth of a degree each way, and a sixteenth of a degree
    /// of longitude at eighty degrees south is a sixth of one at the equator. Grown a cell a
    /// step, a share near the pole runs round it along its circle of latitude, and the borders
    /// come out as the spokes and rings of a dartboard: Antarctica cut into sectors, which is
    /// what its claims look like on paper and what a border on the ground never does. Here a
    /// step costs the distance it covers, diagonals included, and the wander that keeps two
    /// shares from meeting on a straight line is noise laid over the pole itself - so it
    /// wanders as much at the pole as at the coast, rather than turning to static near it.
    ///
    /// What is left over: a scrap of one share only reachable through another goes to the one it
    /// touches most, as anywhere; ground no share could walk to at all - an island, an ice rise
    /// in a shelf - goes to the share nearest across the water, which is the one whose coast it
    /// lies off.
    /// </summary>
    private static Dictionary<ushort, int> GrowPolar(ushort[] states, int width, int height,
        HashSet<ushort> shares, RegionPiece[] pieces)
    {
        var counts = new Dictionary<ushort, int>();
        var member = new bool[ushort.MaxValue + 1];
        foreach (var share in shares) member[share] = true;
        int top = height, bottom = -1;
        for (var cell = 0; cell < states.Length; cell++)
        {
            if (!member[states[cell]]) continue;
            var row = cell / width;
            if (row < top) top = row;
            if (row > bottom) bottom = row;
        }
        if (bottom < 0) return counts;

        // The band of rows the ground lies in, as its own little grid.
        var rows = bottom - top + 1;
        var size = rows * width;
        var south = top + bottom >= height;
        var ground = new bool[size];
        var cosine = new float[rows];
        for (var row = 0; row < rows; row++)
            cosine[row] = MathF.Max(0.0005f, MathF.Cos(YToLatitude(top + row, height) * MathF.PI / 180f));
        for (var local = 0; local < size; local++) ground[local] = member[states[top * width + local]];

        // Where a cell is, seen from above the pole, in cells of latitude.
        (float X, float Y) Projected(int local)
        {
            var row = local / width;
            var column = local % width;
            var fromPole = south ? height - (top + row) - 0.5f : top + row + 0.5f;
            var angle = (column + 0.5f) / width * MathF.Tau;
            return (fromPole * MathF.Sin(angle), fromPole * MathF.Cos(angle));
        }

        // Where every cell of the ground is, worked out once: the seeds, the costs and the bends
        // all ask it, and the seeds asked it of the whole band once for every share.
        var projectedX = new float[size];
        var projectedY = new float[size];
        for (var local = 0; local < size; local++)
            if (ground[local]) (projectedX[local], projectedY[local]) = Projected(local);

        var price = new int[size];
        for (var local = 0; local < size; local++)
        {
            if (!ground[local]) continue;
            var x = projectedX[local];
            var y = projectedY[local];
            // Two scales of wander: a border that swings over a few hundred kilometres, and
            // is ragged over a few dozen.
            var wander = 0.55f * PolarNoise(x, y, 60f, 0x51A7u) + 0.30f * PolarNoise(x, y, 20f, 0x2F0Du) +
                         0.15f * PolarNoise(x, y, 7f, 0x7C3Bu);
            price[local] = (int)(100f * MathF.Exp(2.4f * (wander - 0.5f)));
        }

        // Each share starts from the piece of ground nearest the point it was given.
        var owner = new ushort[size];
        var reached = new int[size];
        Array.Fill(reached, int.MaxValue);
        var frontier = new PriorityQueue<int, int>();
        foreach (var piece in pieces)
        {
            var seedRow = LatitudeToY(piece.Latitude, height) - top;
            var seedColumn = LongitudeToX(piece.Longitude, width);
            var seedFromPole = south ? height - (top + seedRow) - 0.5f : top + seedRow + 0.5f;
            var seedAngle = (seedColumn + 0.5f) / width * MathF.Tau;
            var sx = seedFromPole * MathF.Sin(seedAngle);
            var sy = seedFromPole * MathF.Cos(seedAngle);
            var best = -1;
            var bestDistance = float.MaxValue;
            for (var local = 0; local < size; local++)
            {
                if (!ground[local] || owner[local] != 0) continue;
                var x = projectedX[local];
                var y = projectedY[local];
                var distance = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = local;
            }
            if (best < 0) continue;
            owner[best] = (ushort)piece.MapIndex;
            reached[best] = 0;
            frontier.Enqueue(best, 0);
        }

        while (frontier.TryDequeue(out var local, out var cost))
        {
            if (cost > reached[local]) continue;
            var row = local / width;
            var column = local % width;
            for (var dy = -1; dy <= 1; dy++)
            {
                var nextRow = row + dy;
                if (nextRow < 0 || nextRow >= rows) continue;
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var next = nextRow * width + (column + dx + width) % width;
                    if (!ground[next]) continue;
                    // The distance the step covers: a degree of longitude shrinks with the latitude.
                    var across = dx == 0 ? 0f : 0.5f * (cosine[row] + cosine[nextRow]);
                    var length = dy == 0 ? across : dx == 0 ? 1f : MathF.Sqrt(1f + across * across);
                    var step = cost + Math.Max(1, (int)(price[next] * length));
                    if (step >= reached[next]) continue;
                    reached[next] = step;
                    owner[next] = owner[local];
                    frontier.Enqueue(next, step);
                }
            }
        }

        // And then bent. Grown ground meets along lines that are straight for hundreds of
        // kilometres at a time however the cost wanders, which is how a cell of a Voronoi
        // diagram looks and how no border between two places does. Each cell takes the share of
        // a point a little way off, in a direction that turns slowly across the map - at three
        // scales, so the border swings, meanders and frays - and the straight lines come out as
        // the kind of line a river or a watershed draws.
        var bent = new ushort[size];
        for (var local = 0; local < size; local++)
        {
            if (!ground[local]) continue;
            bent[local] = owner[local];
            var x = projectedX[local];
            var y = projectedY[local];
            var shiftX = 0f;
            var shiftY = 0f;
            foreach (var (lattice, reach, salt) in Bends)
            {
                shiftX += reach * (PolarNoise(x, y, lattice, salt) * 2f - 1f);
                shiftY += reach * (PolarNoise(x, y, lattice, salt ^ 0x9E37u) * 2f - 1f);
            }
            var sx = x + shiftX;
            var sy = y + shiftY;
            var fromPole = MathF.Sqrt(sx * sx + sy * sy);
            var row = (south ? height - fromPole - 0.5f : fromPole - 0.5f) - top;
            var angle = MathF.Atan2(sx, sy);
            if (angle < 0f) angle += MathF.Tau;
            var column = (int)(angle / MathF.Tau * width - 0.5f + width) % width;
            var nearRow = (int)MathF.Round(row);
            if (nearRow < 0 || nearRow >= rows) continue;
            var source = nearRow * width + column;
            if (ground[source] && owner[source] != 0) bent[local] = owner[source];
        }
        owner = bent;

        // The pieces of each share, four-connected: a share joined to itself only at a corner
        // is two pieces on the screen.
        var pieceOf = new int[size];
        Array.Fill(pieceOf, -1);
        var pieceOwner = new List<ushort>();
        var pieceCells = new List<List<int>>();
        var largest = new Dictionary<ushort, int>();
        var queue = new Queue<int>();
        for (var start = 0; start < size; start++)
        {
            if (!ground[start] || pieceOf[start] >= 0) continue;
            var id = pieceCells.Count;
            var mine = owner[start];
            var cells = new List<int>();
            pieceOf[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var local = queue.Dequeue();
                cells.Add(local);
                var row = local / width;
                var column = local % width;
                for (var side = 0; side < 4; side++)
                {
                    var nextRow = row + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (nextRow < 0 || nextRow >= rows) continue;
                    var next = nextRow * width + (column + (side == 0 ? -1 : side == 1 ? 1 : 0) + width) % width;
                    if (!ground[next] || pieceOf[next] >= 0 || owner[next] != mine) continue;
                    pieceOf[next] = id;
                    queue.Enqueue(next);
                }
            }
            pieceOwner.Add(mine);
            pieceCells.Add(cells);
            if (mine != 0 && cells.Count > largest.GetValueOrDefault(mine)) largest[mine] = cells.Count;
        }

        // A stray that touches another share goes to the one it touches most.
        var islands = new List<int>();
        for (var id = 0; id < pieceCells.Count; id++)
        {
            var mine = pieceOwner[id];
            if (mine != 0 && pieceCells[id].Count >= largest.GetValueOrDefault(mine)) continue;
            var shared = new Dictionary<ushort, int>();
            foreach (var local in pieceCells[id])
            {
                var row = local / width;
                var column = local % width;
                for (var side = 0; side < 4; side++)
                {
                    var nextRow = row + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (nextRow < 0 || nextRow >= rows) continue;
                    var next = nextRow * width + (column + (side == 0 ? -1 : side == 1 ? 1 : 0) + width) % width;
                    if (!ground[next] || owner[next] == mine || owner[next] == 0) continue;
                    shared[owner[next]] = shared.GetValueOrDefault(owner[next]) + 1;
                }
            }
            if (shared.Count == 0)
            {
                islands.Add(id);
                continue;
            }
            ushort best = 0;
            var bestEdge = 0;
            foreach (var (neighbour, edge) in shared)
                if (edge > bestEdge || edge == bestEdge && neighbour < best)
                {
                    best = neighbour;
                    bestEdge = edge;
                }
            foreach (var local in pieceCells[id]) owner[local] = best;
        }

        // And an island to whoever is nearest across the water: a flood out from every coast,
        // over the sea as well as the land, and the first share to wash up on it has it.
        if (islands.Count > 0)
        {
            var island = new bool[size];
            foreach (var id in islands)
                foreach (var local in pieceCells[id]) island[local] = true;
            var tide = new ushort[size];
            var flood = new Queue<int>();
            for (var local = 0; local < size; local++)
                if (ground[local] && !island[local] && owner[local] != 0)
                {
                    tide[local] = owner[local];
                    flood.Enqueue(local);
                }
            while (flood.Count > 0)
            {
                var local = flood.Dequeue();
                var row = local / width;
                var column = local % width;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var nextRow = row + dy;
                    if (nextRow < 0 || nextRow >= rows) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var next = nextRow * width + (column + dx + width) % width;
                        if (tide[next] != 0) continue;
                        tide[next] = tide[local];
                        flood.Enqueue(next);
                    }
                }
            }
            foreach (var id in islands)
            {
                // The island goes whole, to whoever reached most of it.
                var landed = new Dictionary<ushort, int>();
                foreach (var local in pieceCells[id])
                    if (tide[local] != 0) landed[tide[local]] = landed.GetValueOrDefault(tide[local]) + 1;
                ushort best = 0;
                var most = 0;
                foreach (var (share, cells) in landed)
                    if (cells > most || cells == most && share < best)
                    {
                        best = share;
                        most = cells;
                    }
                if (best == 0) continue;
                foreach (var local in pieceCells[id]) owner[local] = best;
            }
        }

        for (var local = 0; local < size; local++)
        {
            if (!ground[local] || owner[local] == 0) continue;
            states[top * width + local] = owner[local];
            counts[owner[local]] = counts.GetValueOrDefault(owner[local]) + 1;
        }
        return counts;
    }

    /// <summary>How a polar border is bent: the scale of each turn, how far it reaches, in cells of latitude.</summary>
    private static readonly (float Lattice, float Reach, uint Salt)[] Bends =
    {
        (90f, 30f, 0xB5E1u), (30f, 13f, 0x4D2Bu), (10f, 4f, 0x1F77u)
    };

    /// <summary>Smooth value noise at any point, fixed to the map rather than to the run.</summary>
    private static float PolarNoise(float x, float y, float lattice, uint salt)
    {
        var gx = x / lattice;
        var gy = y / lattice;
        var cellX = (int)MathF.Floor(gx);
        var cellY = (int)MathF.Floor(gy);
        var fx = gx - cellX;
        var fy = gy - cellY;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        float Corner(int cx, int cy)
        {
            var hash = (uint)(cx * 374761393 + cy * 668265263) ^ salt * 2246822519u;
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return ((hash ^ (hash >> 16)) & 0xFFFF) / 65535f;
        }

        var upper = Corner(cellX, cellY) + (Corner(cellX + 1, cellY) - Corner(cellX, cellY)) * fx;
        var lower = Corner(cellX, cellY + 1) + (Corner(cellX + 1, cellY + 1) - Corner(cellX, cellY + 1)) * fx;
        return upper + (lower - upper) * fy;
    }

    /// <summary>
    /// What one step across this cell costs a growing region.
    ///
    /// Smooth value noise on a coarse lattice, hashed off the cell's own coordinates so it
    /// is the same on every machine and every run. Sixteen cells to a lattice square makes
    /// the border wander over tens of kilometres rather than jitter pixel by pixel, which
    /// reads as a border rather than as static.
    /// </summary>
    private static int GroundCost(int x, int y)
    {
        const int lattice = 8;
        var cellX = x / lattice;
        var cellY = y / lattice;
        var fx = (x % lattice) / (float)lattice;
        var fy = (y % lattice) / (float)lattice;
        // Smoothstep, so the lattice itself never shows as a grid.
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        static float Corner(int cx, int cy)
        {
            var hash = (uint)(cx * 374761393 + cy * 668265263);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return ((hash ^ (hash >> 16)) & 0xFFFF) / 65535f;
        }

        var top = Corner(cellX, cellY) + (Corner(cellX + 1, cellY) - Corner(cellX, cellY)) * fx;
        var bottom = Corner(cellX, cellY + 1) + (Corner(cellX + 1, cellY + 1) - Corner(cellX, cellY + 1)) * fx;
        var value = top + (bottom - top) * fy;
        return 100 + (int)(value * 130f);
    }

    /// <summary>
    /// Hand every stray scrap to a neighbour.
    ///
    /// A share can end up holding ground it cannot reach - the enclave east of Beijing is
    /// the standing example, a fragment of one province wedged between two others that has
    /// to be walked around to be reached at all. Left alone it draws as a speck of the
    /// wrong colour sitting on its own, which reads as a mistake because it looks like one.
    /// Each fragment goes to whichever region it shares the most border with, which is the
    /// neighbour it already looks like part of.
    /// </summary>
    private static void TidyStrayPieces(ushort[] states, int width, int height,
        List<int> ground, Dictionary<int, ushort> owner)
    {
        var visited = new HashSet<int>();
        var component = new List<int>();
        var queue = new Queue<int>();
        var largest = new Dictionary<ushort, int>();
        var components = new List<(ushort Owner, List<int> Cells)>();

        // Ground the growth never reached is ground no share can walk to - an enclave, an
        // island, a scrap on the far side of somebody else's territory. It has no owner at
        // all, and leaving it be is what left a speck of Hebei sitting alone east of
        // Beijing. Treated as its own fragment, it goes to a neighbour like any other.
        const ushort unreached = ushort.MaxValue;
        foreach (var cell in ground)
            if (!owner.ContainsKey(cell)) owner[cell] = unreached;

        foreach (var start in ground)
        {
            if (!owner.TryGetValue(start, out var mine) || !visited.Add(start)) continue;
            component.Clear();
            queue.Clear();
            queue.Enqueue(start);
            component.Add(start);
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                var y = cell / width;
                var x = cell % width;
                for (var side = 0; side < 4; side++)
                {
                    var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                    var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (ny < 0 || ny >= height) continue;
                    nx = (nx + width) % width;
                    var next = ny * width + nx;
                    if (!owner.TryGetValue(next, out var theirs) || theirs != mine) continue;
                    if (!visited.Add(next)) continue;
                    queue.Enqueue(next);
                    component.Add(next);
                }
            }
            components.Add((mine, new List<int>(component)));
            if (component.Count > largest.GetValueOrDefault(mine)) largest[mine] = component.Count;
        }

        foreach (var (mine, cells) in components)
        {
            // Unreached ground is always a stray, however much of it there is.
            if (mine != unreached && cells.Count >= largest.GetValueOrDefault(mine)) continue;

            // Whoever this scrap already touches most.
            var shared = new Dictionary<ushort, int>();
            foreach (var cell in cells)
            {
                var y = cell / width;
                var x = cell % width;
                for (var side = 0; side < 4; side++)
                {
                    var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                    var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (ny < 0 || ny >= height) continue;
                    nx = (nx + width) % width;
                    var next = ny * width + nx;
                    var neighbour = owner.TryGetValue(next, out var inside) ? inside : states[next];
                    if (neighbour == 0 || neighbour == mine || neighbour == unreached) continue;
                    shared[neighbour] = shared.GetValueOrDefault(neighbour) + 1;
                }
            }
            if (shared.Count == 0) continue;

            var best = (ushort)0;
            var bestEdge = 0;
            foreach (var (neighbour, edge) in shared)
                if (edge > bestEdge || (edge == bestEdge && neighbour < best))
                {
                    bestEdge = edge;
                    best = neighbour;
                }
            if (best == 0) continue;
            foreach (var cell in cells) owner[cell] = best;
        }
    }

    /// <summary>
    /// Which region currently holds most of the ground a rule is about to take. Used to
    /// find the donor when a rule does not name one, so authoring a new region is a
    /// bounding box and a population rather than a bounding box, a population, and a
    /// correct guess about what is underneath it.
    /// </summary>
    private static int DominantState(ushort[] control, ushort[] states, int width, int height,
        ushort sourceNation, RegionRule rule)
    {
        var minX = LongitudeToX(rule.MinLongitude, width);
        var maxX = LongitudeToX(rule.MaxLongitude, width);
        var minY = LatitudeToY(rule.MaxLatitude, height);
        var maxY = LatitudeToY(rule.MinLatitude, height);
        var tally = new Dictionary<ushort, int>();

        void Count(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            var cell = y * width + x;
            if (control[cell] != sourceNation) return;
            if (rule.Polygon.Length >= 3 && !Contains(rule.Polygon, XToLongitude(x, width), YToLatitude(y, height)))
                return;
            var state = states[cell];
            if (state == 0) return;
            tally[state] = tally.GetValueOrDefault(state) + 1;
        }

        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            Count(x, y);

        foreach (var seed in rule.Seeds)
        {
            var centerX = LongitudeToX(seed.Longitude, width);
            var centerY = LatitudeToY(seed.Latitude, height);
            var radius = Math.Clamp(seed.RadiusCells, 0, 4);
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
                Count(centerX + dx, centerY + dy);
        }

        return tally.Count == 0 ? -1 : tally.OrderByDescending(entry => entry.Value).First().Key;
    }

    private static int PaintRegion(
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        ushort nation,
        ushort sourceNation,
        ushort state,
        RegionRule rule,
        ushort carveFrom,
        out int connectedComponents,
        out string clippedAway)
    {
        // Ground the boundary rejected that the bounding box would have taken. A traced
        // border is only right if what it leaves out is the ground that is not the region:
        // reporting the leftovers as separate pieces with their coordinates is the
        // difference between drawing a border and guessing at one.
        var rejected = new HashSet<int>();
        var minX = LongitudeToX(rule.MinLongitude, width);
        var maxX = LongitudeToX(rule.MaxLongitude, width);
        var minY = LatitudeToY(rule.MaxLatitude, height);
        var maxY = LatitudeToY(rule.MinLatitude, height);
        var changed = 0;
        // The polygon, rasterised once for this box. Asking `Contains` per cell is what made a
        // campaign load take minutes; the mask is the same answer from the same float crossings,
        // and `VerifyPolygonMasks` proves the two agree over every rule in the catalogue.
        var maskWidth = maxX - minX + 1;
        var polygonMask = rule.Polygon.Length >= 3
            ? PolygonMask(rule.Polygon, minX, maxX, minY, maxY, width, height)
            : null;
        bool Inside(int x, int y) => polygonMask is null || polygonMask[(y - minY) * maskWidth + (x - minX)];
        // Replacing a placeholder contour must remove its old outside pixels, not union
        // a traced polygon with the circle. Never take land from unrelated neighbours.
        if (rule.ReplaceExisting && rule.Polygon.Length >= 3 && sourceNation != nation)
        {
            var donors = new List<(int X, int Y, ushort State)>();
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                if (control[y * width + x] == sourceNation && states[y * width + x] != state && states[y * width + x] != 0)
                    donors.Add((x, y, states[y * width + x]));
            // The donors as a grid over the box, so the nearest is found ring by ring around the
            // cell rather than by measuring every one of them for every cell: the same donor the
            // whole list would give, ties to the first in the list, in a fraction of the time.
            var boxWidth = maxX - minX + 1;
            var boxHeight = maxY - minY + 1;
            var donorGrid = donors.Count == 0 ? null : new ushort[boxWidth * boxHeight];
            foreach (var donor in donors) donorGrid[(donor.Y - minY) * boxWidth + (donor.X - minX)] = donor.State;
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
            {
                var cell = y * width + x;
                if (control[cell] != nation || donors.Count == 0 || Inside(x, y)) continue;
                control[cell] = sourceNation; states[cell] = NearestDonor(donorGrid, boxWidth, boxHeight, x - minX, y - minY); changed++;
            }
        }
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var cell = y * width + x;
            if ((control[cell] != sourceNation && control[cell] != nation) || states[cell] == state) continue;
            if (carveFrom != 0 && states[cell] != carveFrom) continue;
            if (!Inside(x, y))
            {
                rejected.Add(cell);
                continue;
            }
            control[cell] = nation;
            states[cell] = state;
            changed++;
        }
        if (rule.Polygon.Length >= 3)
            changed += BridgeRegionComponents(control, states, width, height, nation, sourceNation, state,
                minX, maxX, minY, maxY, 14);
        changed += PaintSeeds(control, states, width, height, nation, sourceNation, state, rule.Seeds);
        connectedComponents = rule.Polygon.Length >= 3
            ? RegionComponents(states, width, height, state, minX, maxX, minY, maxY).Count
            : 0;
        rejected.RemoveWhere(cell => states[cell] == state);
        clippedAway = Describe(rejected, width, height);
        return changed;
    }

    /// <summary>
    /// The donor nearest a cell of the box, as the scan-ordered list's first minimum would have
    /// it: least squared distance, then the lower row, then the lower column. A donor on the
    /// ring r cells out is at least r away, so the search stops at the first ring beyond the
    /// best distance found - after taking in that ring, which can hold an equal one.
    /// </summary>
    private static ushort NearestDonor(ushort[] grid, int boxWidth, int boxHeight, int x, int y)
    {
        var best = int.MaxValue;
        int bestX = 0, bestY = 0;
        ushort found = 0;
        void Consider(int cx, int cy)
        {
            if (cx < 0 || cx >= boxWidth || cy < 0 || cy >= boxHeight) return;
            var state = grid[cy * boxWidth + cx];
            if (state == 0) return;
            var distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (distance > best || distance == best && (cy > bestY || cy == bestY && cx >= bestX)) return;
            best = distance; bestX = cx; bestY = cy; found = state;
        }
        for (var r = 0; ; r++)
        {
            if (found != 0 && (long)r * r > best) break;
            if (x - r < 0 && x + r >= boxWidth && y - r < 0 && y + r >= boxHeight) break;
            if (r == 0) { Consider(x, y); continue; }
            for (var cx = x - r; cx <= x + r; cx++) { Consider(cx, y - r); Consider(cx, y + r); }
            for (var cy = y - r + 1; cy <= y + r - 1; cy++) { Consider(x - r, cy); Consider(x + r, cy); }
        }
        return found;
    }

    /// <summary>
    /// Break a loose set of cells into touching pieces and name each one by its size and
    /// the box it occupies, so a border can be judged against what it excluded.
    /// </summary>
    private static string Describe(HashSet<int> cells, int width, int height)
    {
        if (cells.Count == 0) return "";
        var seen = new HashSet<int>();
        var pieces = new List<(int Count, float West, float East, float South, float North)>();
        var queue = new Queue<int>();
        foreach (var start in cells)
        {
            if (!seen.Add(start)) continue;
            queue.Clear();
            queue.Enqueue(start);
            var count = 0;
            float west = 999f, east = -999f, south = 999f, north = -999f;
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                count++;
                var longitude = XToLongitude(cell % width, width);
                var latitude = YToLatitude(cell / width, height);
                west = MathF.Min(west, longitude);
                east = MathF.Max(east, longitude);
                south = MathF.Min(south, latitude);
                north = MathF.Max(north, latitude);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var y = cell / width + dy;
                    if (y < 0 || y >= height) continue;
                    var neighbour = y * width + (cell % width + dx + width) % width;
                    if (cells.Contains(neighbour) && seen.Add(neighbour)) queue.Enqueue(neighbour);
                }
            }
            pieces.Add((count, west, east, south, north));
        }
        pieces.Sort((a, b) => b.Count.CompareTo(a.Count));
        return string.Join(", ", pieces.Take(6).Select(piece =>
            $"{piece.Count} cell(s) at {piece.West:0.00}-{piece.East:0.00}E {piece.South:0.00}-{piece.North:0.00}N"));
    }

    private static int PaintSeeds(ushort[] control, ushort[] states, int width, int height,
        ushort nation, ushort sourceNation, ushort state, IReadOnlyList<RegionSeed> seeds)
    {
        var changed = 0;
        foreach (var seed in seeds)
        {
            var centerX = LongitudeToX(seed.Longitude, width);
            var centerY = LatitudeToY(seed.Latitude, height);
            var radius = Math.Clamp(seed.RadiusCells, 0, 4);
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius + 1 || centerY + dy < 0 || centerY + dy >= height)
                    continue;
                var x = (centerX + dx + width) % width;
                var cell = (centerY + dy) * width + x;
                if (control[cell] != 0 && control[cell] != sourceNation && control[cell] != nation) continue;
                if (control[cell] == nation && states[cell] == state) continue;
                control[cell] = nation;
                states[cell] = state;
                changed++;
            }
        }
        return changed;
    }

    private static int BridgeRegionComponents(
        ushort[] control, ushort[] states, int width, int height,
        ushort nation, ushort sourceNation, ushort state,
        int minX, int maxX, int minY, int maxY, int maximumGap)
    {
        var changed = 0;
        for (var pass = 0; pass < 8; pass++)
        {
            var components = RegionComponents(states, width, height, state, minX, maxX, minY, maxY);
            if (components.Count <= 1) break;
            components.Sort((a, b) => b.Count.CompareTo(a.Count));
            var main = components[0];
            var bestA = -1;
            var bestB = -1;
            var bestDistance = int.MaxValue;
            for (var componentIndex = 1; componentIndex < components.Count; componentIndex++)
            foreach (var a in main)
            foreach (var b in components[componentIndex])
            {
                var dx = a % width - b % width;
                var dy = a / width - b / width;
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestA = a;
                bestB = b;
            }
            if (bestA < 0 || bestDistance > maximumGap * maximumGap) break;

            var x0 = bestA % width;
            var y0 = bestA / width;
            var x1 = bestB % width;
            var y1 = bestB / width;
            var dxLine = Math.Abs(x1 - x0);
            var sx = x0 < x1 ? 1 : -1;
            var dyLine = -Math.Abs(y1 - y0);
            var sy = y0 < y1 ? 1 : -1;
            var error = dxLine + dyLine;
            while (true)
            {
                var cell = y0 * width + x0;
                if ((control[cell] == sourceNation || control[cell] == nation) && states[cell] != state)
                {
                    control[cell] = nation;
                    states[cell] = state;
                    changed++;
                }
                if (x0 == x1 && y0 == y1) break;
                var doubled = error * 2;
                if (doubled >= dyLine) { error += dyLine; x0 += sx; }
                if (doubled <= dxLine) { error += dxLine; y0 += sy; }
            }
        }
        return changed;
    }

    private static List<List<int>> RegionComponents(
        ushort[] states, int width, int height, ushort state,
        int minX, int maxX, int minY, int maxY)
    {
        var boxWidth = maxX - minX + 1;
        var visited = new bool[boxWidth * (maxY - minY + 1)];
        var result = new List<List<int>>();
        var queue = new Queue<int>();
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var local = (y - minY) * boxWidth + x - minX;
            var cell = y * width + x;
            if (visited[local] || states[cell] != state) continue;
            visited[local] = true;
            queue.Enqueue(cell);
            var component = new List<int>();
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                var cx = current % width;
                var cy = current / width;
                Try(cx - 1, cy);
                Try(cx + 1, cy);
                Try(cx, cy - 1);
                Try(cx, cy + 1);
            }
            result.Add(component);

            void Try(int nextX, int nextY)
            {
                if (nextX < minX || nextX > maxX || nextY < minY || nextY > maxY) return;
                var nextLocal = (nextY - minY) * boxWidth + nextX - minX;
                var nextCell = nextY * width + nextX;
                if (visited[nextLocal] || states[nextCell] != state) return;
                visited[nextLocal] = true;
                queue.Enqueue(nextCell);
            }
        }
        return result;
    }

    private static GeoPoint[] ParsePolygon(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<GeoPoint>();
        var points = new List<GeoPoint>();
        foreach (var point in value.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) continue;
            points.Add(new GeoPoint(point[0].GetSingle(), point[1].GetSingle()));
        }
        return points.Count >= 3 ? points.ToArray() : Array.Empty<GeoPoint>();
    }

    private static RegionSeed[] ParseSeeds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return Array.Empty<RegionSeed>();
        var seeds = new List<RegionSeed>();
        foreach (var seed in value.EnumerateArray())
        {
            if (seed.ValueKind != JsonValueKind.Array || seed.GetArrayLength() < 2) continue;
            seeds.Add(new RegionSeed(seed[0].GetSingle(), seed[1].GetSingle(),
                seed.GetArrayLength() >= 3 ? seed[2].GetInt32() : 1));
        }
        return seeds.ToArray();
    }

    private static bool Contains(IReadOnlyList<GeoPoint> polygon, float longitude, float latitude)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            if ((a.Latitude > latitude) == (b.Latitude > latitude)) continue;
            var crossing = (b.Longitude - a.Longitude) * (latitude - a.Latitude) /
                (b.Latitude - a.Latitude) + a.Longitude;
            if (longitude < crossing) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// The same inside/outside test as <see cref="Contains"/>, asked once per pixel of the rule's
    /// box instead of once per cell per edge.
    ///
    /// `Contains` walks every edge of the polygon for every cell, so a traced border with a few
    /// hundred points costs hundreds of float operations per cell - and a campaign carries a few
    /// hundred of these rules, each over a box of tens of thousands of cells. That is where the
    /// minutes at load went.
    ///
    /// The arithmetic is not approximated, it is reordered. For a fixed latitude the crossings
    /// are the same numbers whatever the longitude, and the answer only depends on the parity of
    /// how many of them lie to the right of the cell. So the crossings are computed once per row,
    /// sorted, and each cell then costs a walk along them. Every `crossing` is the same float
    /// expression as in `Contains`, and every comparison is `longitude < crossing` against the
    /// same `XToLongitude(x, width)`, so the mask is the polygon the old test described - which
    /// `VerifyPolygonMasks` proves over every rule in the catalogue.
    /// </summary>
    private static bool[] PolygonMask(IReadOnlyList<GeoPoint> polygon, int minX, int maxX, int minY, int maxY,
        int width, int height)
    {
        var boxWidth = maxX - minX + 1;
        var boxHeight = maxY - minY + 1;
        var mask = new bool[boxWidth * boxHeight];
        if (boxWidth <= 0 || boxHeight <= 0) return mask;
        var crossings = new List<float>(polygon.Count);
        for (var y = minY; y <= maxY; y++)
        {
            var latitude = YToLatitude(y, height);
            crossings.Clear();
            for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
            {
                var a = polygon[current];
                var b = polygon[previous];
                if ((a.Latitude > latitude) == (b.Latitude > latitude)) continue;
                crossings.Add((b.Longitude - a.Longitude) * (latitude - a.Latitude) /
                    (b.Latitude - a.Latitude) + a.Longitude);
            }
            if (crossings.Count == 0) continue;
            crossings.Sort();
            var row = (y - minY) * boxWidth;
            var next = 0;   // crossings[next] is the first one still to the right of the cell
            for (var x = minX; x <= maxX; x++)
            {
                var longitude = XToLongitude(x, width);
                while (next < crossings.Count && !(longitude < crossings[next])) next++;
                // Parity of the crossings to the right: exactly what the toggling loop counts.
                mask[row + (x - minX)] = ((crossings.Count - next) & 1) == 1;
            }
        }
        return mask;
    }

    /// <summary>
    /// Compares the rasterised mask against the per-cell test for every region rule that has a
    /// polygon, over its whole box. Returns the number of cells that disagree.
    ///
    /// This is the price of the speed-up: a reordered polygon test is easy to get subtly wrong,
    /// and a border that moves by one cell is a border that moved. The test is in the product
    /// rather than in a bench script because the rules are data: a mod that adds a region adds
    /// a box that nobody has checked until this runs.
    /// </summary>
    internal static (int Mismatches, int Cells, int Rules) VerifyPolygonMasks(int width, int height)
    {
        var mismatches = 0;
        var cells = 0;
        var rules = 0;
        foreach (var rule in RegionRules)
        {
            if (rule.Polygon is null || rule.Polygon.Length < 3) continue;
            rules++;
            var minX = LongitudeToX(rule.MinLongitude, width);
            var maxX = LongitudeToX(rule.MaxLongitude, width);
            var minY = LatitudeToY(rule.MaxLatitude, height);
            var maxY = LatitudeToY(rule.MinLatitude, height);
            var mask = PolygonMask(rule.Polygon, minX, maxX, minY, maxY, width, height);
            var boxWidth = maxX - minX + 1;
            for (var y = minY; y <= maxY; y++)
            {
                var latitude = YToLatitude(y, height);
                for (var x = minX; x <= maxX; x++)
                {
                    cells++;
                    var expected = Contains(rule.Polygon, XToLongitude(x, width), latitude);
                    if (mask[(y - minY) * boxWidth + (x - minX)] != expected) mismatches++;
                }
            }
        }
        return (mismatches, cells, rules);
    }


    private static int Connect(
        ushort[] control,
        ushort[] states,
        int width,
        int height,
        ushort subject,
        ushort neighbor,
        ushort through,
        ushort subjectState,
        AdjacencyRule rule)
    {
        var minX = LongitudeToX(rule.MinLongitude, width);
        var maxX = LongitudeToX(rule.MaxLongitude, width);
        var minY = LatitudeToY(rule.MaxLatitude, height);
        var maxY = LatitudeToY(rule.MinLatitude, height);
        var boxWidth = maxX - minX + 1;
        var boxHeight = maxY - minY + 1;
        if (boxWidth <= 0 || boxHeight <= 0) return 0;

        var count = boxWidth * boxHeight;
        var parents = new int[count];
        Array.Fill(parents, int.MinValue);
        var queue = new Queue<int>();

        for (var localY = 0; localY < boxHeight; localY++)
        {
            var row = (minY + localY) * width;
            for (var localX = 0; localX < boxWidth; localX++)
            {
                if (control[row + minX + localX] != subject) continue;
                var local = localY * boxWidth + localX;
                parents[local] = -1;
                queue.Enqueue(local);
            }
        }

        if (queue.Count == 0) return 0;

        var end = -1;
        ReadOnlySpan<int> dx = stackalloc[] { -1, 1, 0, 0 };
        ReadOnlySpan<int> dy = stackalloc[] { 0, 0, -1, 1 };

        while (queue.Count > 0 && end < 0)
        {
            var current = queue.Dequeue();
            var cx = current % boxWidth;
            var cy = current / boxWidth;

            for (var direction = 0; direction < 4; direction++)
            {
                var nx = cx + dx[direction];
                var ny = cy + dy[direction];
                if (nx < 0 || ny < 0 || nx >= boxWidth || ny >= boxHeight) continue;

                var mapCell = (minY + ny) * width + minX + nx;
                var value = control[mapCell];
                if (value == neighbor)
                {
                    end = current;
                    break;
                }

                var next = ny * boxWidth + nx;
                if (parents[next] != int.MinValue || (value != subject && value != through)) continue;
                parents[next] = current;
                queue.Enqueue(next);
            }
        }

        if (end < 0) return 0;

        // Include the contact cell itself. If an earlier map already contains only a
        // one-pixel ABK-RUS contact, the through-only path is empty; expanding around
        // the contact removes the Georgian halo while remaining inside the rule box.
        var corridor = new HashSet<int> { end };
        for (var cursor = end; cursor >= 0; cursor = parents[cursor])
        {
            var x = cursor % boxWidth;
            var y = cursor / boxWidth;
            var mapCell = (minY + y) * width + minX + x;
            if (control[mapCell] == through) corridor.Add(cursor);
        }

        var changed = 0;
        foreach (var local in corridor)
        {
            var cx = local % boxWidth;
            var cy = local / boxWidth;
            for (var oy = -rule.CorridorRadius; oy <= rule.CorridorRadius; oy++)
            for (var ox = -rule.CorridorRadius; ox <= rule.CorridorRadius; ox++)
            {
                if (Math.Abs(ox) + Math.Abs(oy) > rule.CorridorRadius) continue;
                var nx = cx + ox;
                var ny = cy + oy;
                if (nx < 0 || ny < 0 || nx >= boxWidth || ny >= boxHeight) continue;
                var mapCell = (minY + ny) * width + minX + nx;
                if (control[mapCell] != through) continue;
                control[mapCell] = subject;
                states[mapCell] = subjectState;
                changed++;
            }
        }

        // The old shortest-path bridge guaranteed graph adjacency but could leave a
        // visible one-cell Georgian ribbon along the rest of Abkhazia's northern edge.
        // Remove every thin GEO barrier that is simultaneously close to ABK and RUS.
        // This remains bounded to the rule box and never repaints sea or third countries.
        changed += ClearSharedBarrier(control, states, width, minX, minY, boxWidth, boxHeight,
            subject, neighbor, through, subjectState, rule.CorridorRadius);

        return changed;
    }

    private static int ClearSharedBarrier(
        ushort[] control,
        ushort[] states,
        int width,
        int minX,
        int minY,
        int boxWidth,
        int boxHeight,
        ushort subject,
        ushort neighbor,
        ushort through,
        ushort subjectState,
        int radius)
    {
        var maximumDistance = Math.Max(4, radius * 2 + 2);
        var fromSubject = ThroughDistance(control, width, minX, minY, boxWidth, boxHeight,
            subject, through, maximumDistance);
        var fromNeighbor = ThroughDistance(control, width, minX, minY, boxWidth, boxHeight,
            neighbor, through, maximumDistance);
        var changed = 0;
        for (var local = 0; local < fromSubject.Length; local++)
        {
            if (fromSubject[local] <= 0 || fromNeighbor[local] <= 0 ||
                fromSubject[local] + fromNeighbor[local] > maximumDistance)
                continue;
            var x = local % boxWidth;
            var y = local / boxWidth;
            var mapCell = (minY + y) * width + minX + x;
            if (control[mapCell] != through) continue;
            control[mapCell] = subject;
            states[mapCell] = subjectState;
            changed++;
        }
        return changed;
    }

    private static int[] ThroughDistance(
        ushort[] control,
        int width,
        int minX,
        int minY,
        int boxWidth,
        int boxHeight,
        ushort source,
        ushort through,
        int maximumDistance)
    {
        var distance = new int[boxWidth * boxHeight];
        Array.Fill(distance, -1);
        var queue = new Queue<int>();
        for (var y = 0; y < boxHeight; y++)
        for (var x = 0; x < boxWidth; x++)
        {
            var local = y * boxWidth + x;
            if (control[(minY + y) * width + minX + x] != source) continue;
            distance[local] = 0;
            queue.Enqueue(local);
        }

        ReadOnlySpan<int> dx = stackalloc[] { -1, 1, 0, 0 };
        ReadOnlySpan<int> dy = stackalloc[] { 0, 0, -1, 1 };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (distance[current] >= maximumDistance) continue;
            var cx = current % boxWidth;
            var cy = current / boxWidth;
            for (var direction = 0; direction < 4; direction++)
            {
                var nx = cx + dx[direction];
                var ny = cy + dy[direction];
                if (nx < 0 || ny < 0 || nx >= boxWidth || ny >= boxHeight) continue;
                var next = ny * boxWidth + nx;
                if (distance[next] >= 0) continue;
                var value = control[(minY + ny) * width + minX + nx];
                if (value != through) continue;
                distance[next] = distance[current] + 1;
                queue.Enqueue(next);
            }
        }
        return distance;
    }

    private static int LongitudeToX(float longitude, int width) =>
        Math.Clamp((int)((longitude + 180f) / 360f * width), 0, width - 1);

    private static int LatitudeToY(float latitude, int height) =>
        Math.Clamp((int)((90f - latitude) / 180f * height), 0, height - 1);

    private static float XToLongitude(int x, int width) => -180f + (x + 0.5f) * 360f / width;
    private static float YToLatitude(int y, int height) => 90f - (y + 0.5f) * 180f / height;
}
