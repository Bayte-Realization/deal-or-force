using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// A direct, engine-neutral representation of one administrative unit in the shipped
/// 2024 scenario.  Geometry stays in the map layer; this record is the authoritative
/// moddable political/economic identity of the state.
/// </summary>
public sealed partial class ScenarioState
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
    /// <summary>
    /// What a player should call this region. Asked for by id first - six regions are called
    /// "Southern" and four "Amazonas", and a flat table keyed by name gives all of them the
    /// same translation. See <see cref="RegionNames"/>.
    /// </summary>
    public string DisplayName => RegionNames.Resolve(this);

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
    // The map's colour families used to be listed here, and were the last of the world's tables
    // to be spoken in code rather than in data. They are in game/map_data/source_tables.json now,
    // beside the country and region tables that always were, and WorldMapTables.MapPalette is
    // what reads them.

    // The countries whose colour is a fact about them rather than a shade off the map's palette
    // were listed here, as a table only this file could change. It turned out to be dead weight:
    // every one of these colours was already in common/countries/00_countries.json, which
    // CountryDefinitions.Apply reads a hundred lines further down and which therefore overwrote
    // each of them. Two copies of one fact, and the unreachable one was here. A world's appearance
    // belongs with the rest of what the world is, in the data, where a mod can repaint it.

    /// <summary>
    /// The seat a campaign opens on when its scenario does not name one.
    ///
    /// Kept as a named constant rather than a literal in the two places that used to spell
    /// "KOR": a headless run and a benchmark never open the country picker, so they measure
    /// whoever this is, and anyone reading a balance number deserves to find the answer by
    /// searching for a name rather than by spotting a tag in an if.
    /// </summary>
    private const string DefaultPlayerTag = "KOR";

    /// <summary>
    /// The seat a scenario's manifest says to open on, or empty if it does not say.
    ///
    /// A manifest that is missing or unreadable is not worth failing a campaign over: a scenario
    /// whose world is sound but whose manifest is malformed should still play, so the reason is
    /// logged and the run opens on the default seat.
    /// </summary>
    private static string ScenarioPlayerTag(string manifestPath)
    {
        if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath)) return "";
        try
        {
            // Read with the same forgiving reader the scenario index uses, because a manifest is
            // a hand-written file and this one carries comments explaining what it is.
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath), GameDataLoader.ForgivingJson);
            return document.RootElement.TryGetProperty("player_country", out var tag) &&
                   tag.ValueKind == JsonValueKind.String
                ? tag.GetString() ?? ""
                : "";
        }
        catch (Exception e)
        {
            DiagnosticLogs.Write("errors", $"SCENARIO_MANIFEST {manifestPath}: {e.Message}");
            return "";
        }
    }

    /// <summary>
    /// Where the time went when the last base campaign was built, or null when nobody asked.
    ///
    /// The world takes seconds to assemble and the loading screen shows every one of them, so
    /// "which phase is the slow one" is a question that should be answerable without a profiler and
    /// without guessing. A bench sets this to an empty list before building, reads it afterwards
    /// and prints it; the game leaves it null, and one comparison per phase is the whole cost of
    /// leaving it null.
    /// </summary>
    public static List<(string Phase, double Milliseconds)> BuildTimings;

    /// <summary>
    /// Build the base world, taking the seat the player opens on from the scenario's manifest.
    ///
    /// Which country a scenario starts you as is a scenario-shaped fact, not a world-shaped one,
    /// so it lives in game/scenarios/2024/scenario.json beside the name and the description
    /// rather than in this method. Nothing else in the world depends on the answer: the country
    /// picker overrides it the moment a player chooses, and a run that never chooses anybody
    /// gets the manifest's seat - which is what it used to get from the literal this replaces.
    /// </summary>
    public static Campaign CreateBaseCampaign(int seed, string manifestPath = "")
    {
        var campaign = new Campaign(seed);
        var playerTag = ScenarioPlayerTag(manifestPath);
        if (playerTag.Length == 0) playerTag = DefaultPlayerTag;
        var countryIdByMapIndex = new Dictionary<int, string>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        // Phase timings, taken only when something asked for them. Restarting the clock on every
        // call is why each entry is the cost of that phase rather than of everything before it.
        var timings = BuildTimings;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Phase(string name)
        {
            if (timings is null) return;
            timings.Add((name, clock.Elapsed.TotalMilliseconds));
            clock.Restart();
        }

        foreach (var row in WorldMapTables.CountryRows)
        {
            var parts = row.Split('|');
            if (parts.Length < 10) continue;

            var mapIndex = ParseInt(parts[0]);
            var iso = IsCode(parts[1]) ? parts[1] : IsCode(parts[9]) ? parts[9] : Slug(parts[2]).ToUpperInvariant();
            var id = iso.ToLowerInvariant();
            if (!usedIds.Add(id)) id = $"{id}_{mapIndex}";

            var palette = WorldMapTables.MapPalette[
                M.Clamp(ParseInt(parts[7]), 0, WorldMapTables.MapPalette.Length - 1)];
            var variant = palette.Length <= 1 ? 0 : (int)(StableHash(id) % (uint)palette.Length);
            CapitalCatalog.TryGet(iso, out var capital);

            // This opening balance preserves the source unit convention (billions in this shell).
            // The ported Economy pass will replace it with the source's full initial calibration,
            // rather than locking in a second economy model.
            // Source table stores GDP in USD millions; campaign/UI use billions.
            //
            // -99 is the source table's "no figure published" sentinel, not an amount: the
            // Vatican, Antarctica and two dozen dependencies carry it. Importing it literally
            // handed those entries a NEGATIVE economy, which then produced negative treasuries
            // and nonsense ledger lines. A microstate with no published GDP gets a plausible one
            // derived from its population instead - wrong in detail, but the right order of
            // magnitude and never negative. How much per head is Defines.MicrostateGdpPerHead,
            // because it is the one number in that derivation that is a judgement rather than a
            // figure.
            var reportedGdp = ParseFloat(parts[6]);
            var population = ParseLong(parts[5]);
            var gdp = reportedGdp > 0f
                ? reportedGdp / 1000f
                // The parentheses are load-bearing, and so is the divisor being 1e9 rather than
                // the figure written as 0.000000015. `population * perHead / 1e9` rounds twice -
                // once for the multiply and once for the divide - and lands a few units in the
                // last place away from `population * 0.000000015f`, which rounds once. Countries
                // with no published income would have moved by a fraction of a pound, which is
                // nothing in a ledger and everything in a fingerprint, and the fingerprint guard
                // exists to notice exactly that. Fifteen divided by a billion is the same float
                // as the literal it replaces, so with this default every figure in the shipped
                // world is unchanged to the bit.
                : MathF.Max(0.01f, population * (campaign.Defines.MicrostateGdpPerHead / 1_000_000_000f));
            // The opening balance is a fallback too: a country that states one in history/countries
            // keeps it, and the base game's Korea states one - which is where its particular
            // figure lives now, rather than in an if here.
            var treasury = gdp * campaign.Defines.BaseTreasuryGdpShare;

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
                Color = palette[variant],
                CapitalName = capital.Name ?? "",
                CapitalLatitude = capital.Latitude,
                CapitalLongitude = capital.Longitude,
                MapIndex = mapIndex
            }, string.Equals(iso, playerTag, StringComparison.OrdinalIgnoreCase));

            countryIdByMapIndex[mapIndex] = nation.Tag;
        }
        Phase("nations");

        var usedStateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in WorldMapTables.StateRows)
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
        Phase("regions");

        // Apply authoritative political/state corrections before economy, armies,
        // markets and UI consume the scenario. This makes breakaway states such as
        // Abkhazia, Transnistria and Vatican City real simulation entities from tick zero.
        var importedStates = campaign.States.Count;
        // Hoisted out of the call so that reading the rasters and applying them can be timed
        // apart: they are two different jobs - decoding sixteen million pixels twice, and then
        // doing geometry over them - and only one of them is the interesting one.
        var controlRaster = WorldMapSource.Control;
        var stateRaster = WorldMapSource.States;
        Phase("rasters read");
        TerritorialTopology.Apply(campaign, controlRaster, stateRaster,
            WorldMapSource.Width, WorldMapSource.Height);
        Phase("topology applied");

        // The guard is arithmetic rather than a remembered total: the source table must
        // still import whole, and every region correction must still have produced exactly
        // one state. Adding a correction is then an ordinary edit, while losing half the
        // source table is still caught before anything consumes the scenario.
        var correctedStates = campaign.States.Count - importedStates;
        if (campaign.Nations.Count != WorldMapTables.CountryRows.Length || importedStates != WorldMapTables.StateRows.Length ||
            correctedStates != TerritorialTopology.RegionRuleCount)
            throw new InvalidOperationException(
                $"Base scenario import drifted: {campaign.Nations.Count} nations, " +
                $"{importedStates} imported states (expected {WorldMapTables.StateRows.Length}), " +
                $"{correctedStates} region corrections (expected {TerritorialTopology.RegionRuleCount}).");

        // Data-specific unit checks belong in fixtures, not runtime guards: a valid
        // map package may change Korea's GDP or replace the countries altogether.

        campaign.InvalidateStateIndex();
        // Names, then the country's own figures, then the deal, then the corrections to it.
        CountryDefinitions.Apply(campaign);
        StateDefinitions.Apply(campaign);
        Phase("definitions");
        // Every country has a government and an ideology before history is read, so
        // the scenario only has to name the ones that are not the ordinary case.
        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrEmpty(nation.Government)) nation.Government = GovernmentCatalog.Default;
            if (string.IsNullOrEmpty(nation.Ideology)) nation.Ideology = IdeologyCatalog.Default;
        }
        Phase("governments");
        HistoryDataLoader.ApplyBaseCountries(campaign);
        Phase("history figures");
        SovereignFinance.NormalizeAccounts(campaign);
        Phase("finance");
        EconomySystem.Seed(campaign);
        Phase("economy seed");
        HistoryDataLoader.ApplyBaseStates(campaign);
        Phase("region facts");
        // Last of the history, because an alliance is between countries and they all have to
        // exist first.
        HistoryDataLoader.ApplyBaseDiplomacy(campaign);
        Phase("diplomacy");
        campaign.SetPlayer((campaign.NationByTag(playerTag) ?? campaign.NationByTag(DefaultPlayerTag) ??
            campaign.Nations[0]).Id);
        Phase("seat");
        MilitaryBootstrap.Seed(campaign);
        NavalBootstrap.Seed(campaign);
        WarBootstrap.Seed(campaign);
        Phase("military");
        // The tensions and the backers the world opens with, once its wars exist.
        ConflictHistory.Apply(campaign);
        Phase("conflicts");
        MarketSystem.Seed(campaign);
        Phase("market");

        // Last, because it needs states, buildings, pops and armies to already exist: it
        // prices one settled market pass and records what turns that output into each
        // nation's authored 2024 GDP. Everything the player sees afterwards is emergent.
        EconomySystem.CalibrateOutput(campaign);
        Phase("calibrate output");

        // The world is finished, so its books can be written. The weekly tick that normally
        // writes them explicitly skips day zero, which left a new campaign's budget screen
        // reading nothing at all until its first Sunday.
        EconomySystem.Explain(campaign);
        Phase("explain");

        campaign.History.Capture(campaign, true);
        Phase("history capture");
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
