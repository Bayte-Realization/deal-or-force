using System;
using System.Collections.Generic;
using System.Globalization;

namespace DealOrForce.Simulation;

/// <summary>
/// A direct, engine-neutral representation of one administrative unit in the shipped
/// 2024 scenario.  Geometry stays in the map layer; this record is the authoritative
/// moddable political/economic identity of the state.
/// </summary>
public sealed class ScenarioState
{
    public int MapIndex;
    public int NationMapIndex;
    public string Id = "";
    public string Name = "";
    public string NationId = "";
    public string Iso = "";
    public string Kind = "Region";

    /// <summary>
    /// How settled this region is, 0 to 1. Written every economic week by
    /// <see cref="StabilityCore"/> out of what the region lives on, thinks and is.
    /// </summary>
    public float Stability = 0.62f;

    /// <summary>
    /// The region's name as a player reads it, the same way a country has one.
    ///
    /// No table answers for these yet: there are 1,730 of them and they are proper nouns,
    /// which is a translator's job rather than an afternoon's. The lookup is here so that
    /// the day somebody writes those rows they work, and so a mod that renames a region can
    /// ship its own translation the same day it ships the rename.
    /// </summary>
    public string DisplayName => Loc.Get(Name);

    /// <summary>
    /// The region this one was cut out of, or 0 for a region the scenario authored.
    ///
    /// A border that cuts a region in two makes two regions; a border that goes away again
    /// has to be able to make one region back out of them, and that needs the pieces to
    /// remember where they came from. Always the original rather than the immediate
    /// parent, so a region cut three times still collapses in one step.
    /// </summary>
    public int ParentMapIndex;

    public long Population;
    public float Literacy;
    public float StandardOfLiving;
    public float Radicalism;

    /// <summary>
    /// How far this region has gone towards leaving the country, 0 to 1. Written weekly by
    /// <see cref="SecessionCore"/>, and zero for the overwhelming majority of the world.
    ///
    /// It accumulates rather than being read fresh each week, which is the difference
    /// between a mechanic a player can see coming and one that happens to them: a region
    /// angry for three running years is nearly gone, and the map can say so before it is.
    /// </summary>
    public float SecessionPressure;

    public float Urbanization;
    public readonly List<PopulationGroup> PopulationGroups = new();
    public readonly Dictionary<BuildingKind, int> Buildings = new();
    /// <summary>Geological/agricultural potential consumed by the live market simulation.</summary>
    public readonly Dictionary<Commodity, float> ResourceDeposits = new();
}

/// <summary>
/// Imports the exact country and administrative-table rows currently used by the
/// s&box build.  Keeping the source rows in code rather than a hand-maintained copy
/// makes the transfer reviewable and gives mods a stable ID/ISO contract from day one.
/// </summary>
public static class ScenarioCatalog
{
    private static readonly string[][] MapPalette =
    {
        new[] { "#949494" },
        new[] { "#c42b40", "#ce4b4d", "#ce3a2c", "#d26f56" },
        new[] { "#bf4f27", "#ce7640", "#c97627", "#d39d4b" },
        new[] { "#ad7228", "#c59639", "#b69629", "#cabb43" },
        new[] { "#8e9136", "#98a649", "#809937", "#8db24c" },
        new[] { "#5e8f37", "#68a44b", "#4d9739", "#58b04f" },
        new[] { "#32814c", "#459669", "#348963", "#48a284" },
        new[] { "#2d867c", "#3e9d9d", "#2e848f", "#4292a9" },
        new[] { "#3596b0", "#4f9abf", "#3680ba", "#5989c4" },
        new[] { "#3573bb", "#547cc4", "#3659c4", "#5e6cc9" },
        new[] { "#4642b2", "#7063ba", "#6244bb", "#8b6dc0" },
        new[] { "#6f3ea7", "#8f58b6", "#8d40b0", "#aa62bc" },
        new[] { "#b13ba1", "#bd58a3", "#ba3c8d", "#c26294" },
        new[] { "#b33262", "#c24c6b", "#bc3348", "#c7575a" }
    };

    private static readonly Dictionary<string, string> SignatureColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USA"] = "#4c7fb3", ["CAN"] = "#c84038", ["MEX"] = "#3f9350",
        ["BRA"] = "#4caf3f", ["ARG"] = "#79b9e0", ["GBR"] = "#8a2f42",
        ["FRA"] = "#4f83c4", ["DEU"] = "#5a5a5a", ["ITA"] = "#5a9c4a",
        ["ESP"] = "#d9a22e", ["PRT"] = "#2f6b4f", ["RUS"] = "#3f6b45",
        ["CHN"] = "#d9b32e", ["JPN"] = "#c9505f", ["PRK"] = "#a33b3b",
        ["KOR"] = "#5a9bc9", ["IND"] = "#c9622e", ["AUS"] = "#c9a13a",
        ["TUR"] = "#b8383a", ["IRN"] = "#3f7a4a", ["SAU"] = "#2f6b3f",
        ["EGY"] = "#c9a23a", ["ZAF"] = "#8fae3d", ["NGA"] = "#4a9c4a",
        ["POL"] = "#a33b40", ["UKR"] = "#d4c034", ["SWE"] = "#4a6fbf",
        ["NOR"] = "#a33b3f", ["FIN"] = "#4a9ba0", ["NLD"] = "#d4762e",
        ["CHE"] = "#c9403f", ["IDN"] = "#a33b3f"
        , ["PMR"] = "#a84655"
    };

    public static Campaign CreateBaseCampaign(int seed)
    {
        var campaign = new Campaign(seed);
        var countryIdByMapIndex = new Dictionary<int, string>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in EarthData.CountryRows)
        {
            var parts = row.Split('|');
            if (parts.Length < 10) continue;

            var mapIndex = ParseInt(parts[0]);
            var iso = IsCode(parts[1]) ? parts[1] : IsCode(parts[9]) ? parts[9] : Slug(parts[2]).ToUpperInvariant();
            var id = iso.ToLowerInvariant();
            if (!usedIds.Add(id)) id = $"{id}_{mapIndex}";

            var palette = MapPalette[M.Clamp(ParseInt(parts[7]), 0, MapPalette.Length - 1)];
            var variant = palette.Length <= 1 ? 0 : (int)(StableHash(id) % (uint)palette.Length);
            CapitalCatalog.TryGet(iso, out var capital);

            // This temporary cash estimate preserves the source unit convention (billions
            // in this shell).  The ported Economy pass will replace it with the source's
            // full initial calibration, rather than locking in a second economy model.
            // Source table stores GDP in USD millions; campaign/UI use billions.
            //
            // -99 is the source table's "no figure published" sentinel, not an amount:
            // the Vatican, Antarctica and two dozen dependencies carry it. Importing it
            // literally handed those entries a NEGATIVE economy, which then produced
            // negative treasuries and nonsense ledger lines. A microstate with no
            // published GDP gets a plausible one derived from its population instead -
            // wrong in detail, but the right order of magnitude and never negative.
            var reportedGdp = ParseFloat(parts[6]);
            var population = ParseLong(parts[5]);
            var gdp = reportedGdp > 0f
                ? reportedGdp / 1000f
                : MathF.Max(0.01f, population * 0.000000015f);
            var treasury = string.Equals(iso, "KOR", StringComparison.OrdinalIgnoreCase)
                ? 184.7997f
                : gdp * 0.10f;

            var nation = campaign.AddNation(new Nation
            {
                Tag = iso,
                Iso = iso,
                Name = parts[2],
                Continent = parts[3],
                Region = parts[4],
                Population = population,
                Gdp = gdp,
                Treasury = treasury,
                Color = SignatureColors.TryGetValue(iso, out var signature) ? signature : palette[variant],
                CapitalName = capital.Name ?? "",
                CapitalLatitude = capital.Latitude,
                CapitalLongitude = capital.Longitude,
                MapIndex = mapIndex
            }, string.Equals(iso, "KOR", StringComparison.OrdinalIgnoreCase));

            countryIdByMapIndex[mapIndex] = nation.Tag;
        }

        var usedStateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in EarthData.StateRows)
        {
            var parts = row.Split('|');
            if (parts.Length < 5) continue;

            var mapIndex = ParseInt(parts[0]);
            var nationMapIndex = ParseInt(parts[2]);
            countryIdByMapIndex.TryGetValue(nationMapIndex, out var nationId);
            var iso = parts[3];
            var id = string.IsNullOrWhiteSpace(iso) || iso.Length < 4
                ? $"{nationId ?? "x"}_{Slug(parts[1])}"
                : Slug(iso);

            if (!usedStateIds.Add(id))
            {
                id = $"{id}_{mapIndex}";
                usedStateIds.Add(id);
            }

            campaign.States.Add(new ScenarioState
            {
                MapIndex = mapIndex,
                NationMapIndex = nationMapIndex,
                NationId = nationId ?? "",
                Id = id,
                Name = parts[1],
                Iso = iso,
                Kind = parts[4]
            });
        }

        // Apply authoritative political/state corrections before economy, armies,
        // markets and UI consume the scenario. This makes breakaway states such as
        // Abkhazia, Transnistria and Vatican City real simulation entities from tick zero.
        var importedStates = campaign.States.Count;
        TerritorialTopology.Apply(campaign, Globality.EarthData.Control, Globality.EarthData.States,
            Globality.EarthData.Width, Globality.EarthData.Height);

        // The guard is arithmetic rather than a remembered total: the source table must
        // still import whole, and every region correction must still have produced exactly
        // one state. Adding a correction is then an ordinary edit, while losing half the
        // source table is still caught before anything consumes the scenario.
        var correctedStates = campaign.States.Count - importedStates;
        if (campaign.Nations.Count != 261 || importedStates != EarthData.StateRows.Length ||
            correctedStates != TerritorialTopology.RegionRuleCount)
            throw new InvalidOperationException(
                $"Base scenario import drifted: {campaign.Nations.Count} nations, " +
                $"{importedStates} imported states (expected {EarthData.StateRows.Length}), " +
                $"{correctedStates} region corrections (expected {TerritorialTopology.RegionRuleCount}).");

        var southKorea = campaign.NationByTag("KOR");
        if (southKorea is null || string.IsNullOrWhiteSpace(southKorea.CapitalName))
            throw new InvalidOperationException("Base scenario import is missing South Korea or its capital.");
        if (Math.Abs(southKorea.Gdp - 1646.739f) > 0.01f)
            throw new InvalidOperationException($"Base scenario GDP units drifted for KOR: {southKorea.Gdp}B.");

        campaign.InvalidateStateIndex();
        // Names, then the country's own figures, then the deal, then the corrections to it.
        CountryDefinitions.Apply(campaign);
        StateDefinitions.Apply(campaign);
        // Every country has a government and an ideology before history is read, so
        // the scenario only has to name the ones that are not the ordinary case.
        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrEmpty(nation.Government)) nation.Government = GovernmentCatalog.Default;
            if (string.IsNullOrEmpty(nation.Ideology)) nation.Ideology = IdeologyCatalog.Default;
        }
        HistoryDataLoader.ApplyBaseCountries(campaign);
        EconomySystem.Seed(campaign);
        HistoryDataLoader.ApplyBaseStates(campaign);
        // Last of the history, because an alliance is between countries and they all have to
        // exist first.
        HistoryDataLoader.ApplyBaseDiplomacy(campaign);
        campaign.SetPlayer(southKorea.Id);
        MilitaryBootstrap.Seed(campaign);
        NavalBootstrap.Seed(campaign);
        WarBootstrap.Seed(campaign);
        MarketSystem.Seed(campaign);

        // Last, because it needs states, buildings, pops and armies to already exist: it
        // prices one settled market pass and records what turns that output into each
        // nation's authored 2024 GDP. Everything the player sees afterwards is emergent.
        EconomySystem.CalibrateOutput(campaign);

        campaign.History.Capture(campaign, true);
        return campaign;
    }

    private static bool IsCode(string value) =>
        value is { Length: 3 } && !value.StartsWith("-", StringComparison.Ordinal) && char.IsLetter(value[0]);

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static float ParseFloat(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0f;

    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value) hash = (hash ^ character) * 16777619u;
        return hash;
    }

    private static string Slug(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }
        return builder.ToString().Trim('_');
    }
}
