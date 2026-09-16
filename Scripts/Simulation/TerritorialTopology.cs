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
        string NationTag, string CarveFromIso, RegionPiece[] Pieces);

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
        string CarveFromIso = ""
    );

    private readonly record struct GeoPoint(float Longitude, float Latitude);
    private readonly record struct RegionSeed(float Longitude, float Latitude, int RadiusCells);

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
            foreach (var row in EarthData.StateRows)
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
        LakeRegions.ResetCatalog();
        var file = GameDataLoader.DataFiles("map_data", "state_regions").FirstOrDefault();
        if (file is null) return;
        try { LoadOverrides(File.ReadAllText(file), null); }
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
                    value.TryGetProperty("carve_from_iso", out var carveFrom) ? carveFrom.GetString() ?? "" : "");
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
                Partitions.Add(new RegionPartition(
                    value.GetProperty("nation").GetString() ?? "",
                    value.GetProperty("carve_from_iso").GetString() ?? "",
                    pieces.ToArray()));
            }
            catch (Exception exception) { errors?.Add($"Invalid map_data partition: {exception.Message}"); }
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
                var donor = string.IsNullOrWhiteSpace(rule.DonorIso)
                    ? campaign.States.FirstOrDefault(candidate =>
                        candidate.MapIndex == DominantState(control, states, width, height,
                            (ushort)sourceNation.MapIndex, rule))
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
            foreach (var row in EarthData.StateRows)
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
        var ground = new List<int>();
        for (var cell = 0; cell < states.Length; cell++)
            if (shares.Contains(states[cell])) ground.Add(cell);
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
            else if (piece.MapIndex == donorIndex && !string.IsNullOrWhiteSpace(piece.Name))
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
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var cell = y * width + x;
            if ((control[cell] != sourceNation && control[cell] != nation) || states[cell] == state) continue;
            if (carveFrom != 0 && states[cell] != carveFrom) continue;
            if (rule.Polygon.Length >= 3 && !Contains(rule.Polygon,
                    XToLongitude(x, width), YToLatitude(y, height)))
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
