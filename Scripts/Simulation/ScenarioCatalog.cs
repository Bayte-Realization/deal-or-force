using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.HighPerformance.Buffers;

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
    /// The region's name as a player reads it, the same way a country has one. Asked for by
    /// id first - six regions are called "Southern" and four "Amazonas", and a flat table
    /// keyed by name gives all of them the same translation. See <see cref="RegionNames"/>.
    ///
    /// The table does not answer for every region yet: there are 1,730 of them and they are
    /// proper nouns, which is a translator's job rather than an afternoon's. The lookup is
    /// here so that the day somebody writes those rows they work, and so a mod that renames
    /// a region can ship its own translation the same day it ships the rename.
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
    /// </summary>
    public static Campaign CreateBaseCampaign(int seed, string manifestPath = "")
    {
        if (WorldMapTables.CountryRows.Length == 0 || WorldMapTables.StateRows.Length == 0)
            WorldMapTables.ResetCatalog();

        var campaign = new Campaign(seed);
        var playerTag = ScenarioPlayerTag(manifestPath);
        if (playerTag.Length == 0) playerTag = DefaultPlayerTag;

        var timings = BuildTimings;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Phase(string name)
        {
            if (timings is null) return;
            timings.Add((name, clock.Elapsed.TotalMilliseconds));
            clock.Restart();
        }

        var mergedGround = CountryMerges.GroundByMapIndex();
        var countryIdByMapIndex = SeedScenarioCountries(campaign, playerTag, mergedGround, out var readPast);
        Phase("nations");

        SeedScenarioRegions(campaign, mergedGround, countryIdByMapIndex);
        Phase("regions");

        ApplyScenarioTopology(campaign, readPast, Phase);
        campaign.InvalidateStateIndex();

        SeedHistoryAndMilitary(campaign, playerTag, Phase);
        CalibrateAndCaptureEconomy(campaign, Phase);
        return campaign;
    }

    internal static void PopulateBaseTerritoryShell(Campaign campaign)
    {
        var mergedGround = CountryMerges.GroundByMapIndex();
        var countryIdByMapIndex = SeedScenarioCountries(campaign, playerTag: "", mergedGround, out _);
        SeedScenarioRegions(campaign, mergedGround, countryIdByMapIndex);
    }

    private static Dictionary<int, string> SeedScenarioCountries(
        Campaign campaign,
        string playerTag,
        IReadOnlyDictionary<int, int> mergedGround,
        out int readPast)
    {
        var countryIdByMapIndex = new Dictionary<int, string>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        readPast = 0;

        foreach (var row in WorldMapTables.CountryRows)
        {
            var parts = row.Split('|');
            if (parts.Length < 10) continue;

            var mapIndex = ParseIntSpan(parts[0]);
            if (mergedGround.ContainsKey(mapIndex))
            {
                readPast++;
                continue;
            }
            var iso = CountryTag(parts);
            var id = iso.ToLowerInvariant();
            if (!usedIds.Add(id)) id = $"{id}_{mapIndex}";

            var palette = WorldMapTables.MapPalette[
                M.Clamp(ParseIntSpan(parts[7]), 0, WorldMapTables.MapPalette.Length - 1)];
            var variant = palette.Length <= 1 ? 0 : (int)(StableHash(id) % (uint)palette.Length);
            CapitalCatalog.TryGet(iso, out var capital);

            var reportedGdp = ParseFloatSpan(parts[6]);
            var population = ParseLongSpan(parts[5]);
            var gdp = reportedGdp > 0f
                ? reportedGdp / 1000f
                // Keep divisor inside parenthesised constant expression to preserve exact single float rounding.
                : MathF.Max(0.01f, population * (campaign.Defines.MicrostateGdpPerHead / 1_000_000_000f));
            var treasury = gdp * campaign.Defines.BaseTreasuryGdpShare;

            var nation = campaign.AddNation(new Nation
            {
                Tag = iso,
                Iso = iso,
                Name = parts[2],
                Continent = StringPool.Shared.GetOrAdd(parts[3]),
                Region = StringPool.Shared.GetOrAdd(parts[4]),
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
        return countryIdByMapIndex;
    }

    private static void SeedScenarioRegions(
        Campaign campaign,
        IReadOnlyDictionary<int, int> mergedGround,
        Dictionary<int, string> countryIdByMapIndex)
    {
        var usedStateIds = new HashSet<string>(WorldMapTables.StateRows.Length, StringComparer.Ordinal);
        foreach (var row in WorldMapTables.StateRows)
        {
            var span = row.AsSpan();
            var p0 = span.IndexOf('|');
            if (p0 < 0) continue;
            var s0 = span[..p0];
            span = span[(p0 + 1)..];
            var p1 = span.IndexOf('|');
            if (p1 < 0) continue;
            var s1 = span[..p1];
            span = span[(p1 + 1)..];
            var p2 = span.IndexOf('|');
            if (p2 < 0) continue;
            var s2 = span[..p2];
            span = span[(p2 + 1)..];
            var p3 = span.IndexOf('|');
            if (p3 < 0) continue;
            var s3 = span[..p3];
            span = span[(p3 + 1)..];
            var p4 = span.IndexOf('|');
            var s4 = p4 >= 0 ? span[..p4] : span;

            var mapIndex = ParseIntSpan(s0);
            var nationMapIndex = ParseIntSpan(s2);
            var nobodys = false;
            if (mergedGround.TryGetValue(nationMapIndex, out var holder))
            {
                nationMapIndex = holder;
                nobodys = holder == 0;
            }
            countryIdByMapIndex.TryGetValue(nationMapIndex, out var nationId);
            var name = s1.ToString();
            var iso = s3.ToString();
            var id = string.IsNullOrWhiteSpace(iso) || iso.Length < 4
                ? $"{nationId ?? "x"}_{Slug(name)}"
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
                Name = name,
                Iso = iso,
                Kind = StringPool.Shared.GetOrAdd(s4),
                Unclaimed = nobodys
            });
        }
    }

    private static void ApplyScenarioTopology(Campaign campaign, int readPast, Action<string> phase)
    {
        var importedStates = campaign.States.Count;
        if (TerritorialTopology.TryApplyPrebakedBoot(campaign, WorldMapSource.Width, WorldMapSource.Height))
        {
            phase("rasters read");
            phase("topology applied");
        }
        else
        {
            var controlRaster = WorldMapSource.Control;
            var stateRaster = WorldMapSource.States;
            phase("rasters read");
            TerritorialTopology.Apply(campaign, controlRaster, stateRaster, WorldMapSource.Width, WorldMapSource.Height);
            phase("topology applied");
        }

        var correctedStates = campaign.States.Count - importedStates;
        if (campaign.Nations.Count != WorldMapTables.CountryRows.Length - readPast ||
            importedStates != WorldMapTables.StateRows.Length ||
            correctedStates != TerritorialTopology.RegionRuleCount)
        {
            throw new InvalidOperationException(
                $"Base scenario import drifted: {campaign.Nations.Count} nations " +
                $"(expected {WorldMapTables.CountryRows.Length - readPast}), " +
                $"{importedStates} imported states (expected {WorldMapTables.StateRows.Length}), " +
                $"{correctedStates} region corrections (expected {TerritorialTopology.RegionRuleCount}).");
        }
    }

    private static void SeedHistoryAndMilitary(Campaign campaign, string playerTag, Action<string> phase)
    {
        CountryDefinitions.Apply(campaign);
        StateDefinitions.Apply(campaign);
        phase("definitions");

        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrEmpty(nation.Government)) nation.Government = GovernmentCatalog.Default;
            if (string.IsNullOrEmpty(nation.Ideology)) nation.Ideology = IdeologyCatalog.Default;
        }
        phase("governments");

        HistoryDataLoader.ApplyBaseCountries(campaign);
        phase("history figures");
        SovereignFinance.NormalizeAccounts(campaign);
        phase("finance");
        EconomySystem.Seed(campaign);
        phase("economy seed");
        HistoryDataLoader.ApplyBaseStates(campaign);
        phase("region facts");
        HistoryDataLoader.ApplyBaseDiplomacy(campaign);
        phase("diplomacy");
        campaign.SetPlayer((campaign.NationByTag(playerTag) ?? campaign.NationByTag(DefaultPlayerTag) ??
            campaign.Nations[0]).Id);
        phase("seat");
        MilitaryBootstrap.Seed(campaign);
        NavalBootstrap.Seed(campaign);
        WarBootstrap.Seed(campaign);
        phase("military");
        ConflictHistory.Apply(campaign);
        phase("conflicts");
    }

    private static void CalibrateAndCaptureEconomy(Campaign campaign, Action<string> phase)
    {
        MarketSystem.Seed(campaign);
        phase("market");
        EconomySystem.CalibrateOutput(campaign);
        phase("calibrate output");
        EconomySystem.Explain(campaign);
        phase("explain");
        PrestigeSystem.SeedInitial(campaign);
        campaign.History.Capture(campaign, true);
        phase("history capture");
    }

    /// <summary>The canonical tag for a row of the country table.</summary>
    internal static string CountryTag(string[] parts) =>
        IsCode(parts[1]) ? parts[1] : IsCode(parts[9]) ? parts[9] : Slug(parts[2]).ToUpperInvariant();

    private static bool IsCode(string value) =>
        value is { Length: 3 } && !value.StartsWith("-", StringComparison.Ordinal) && char.IsLetter(value[0]);

    private static int ParseIntSpan(ReadOnlySpan<char> value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static long ParseLongSpan(ReadOnlySpan<char> value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static float ParseFloatSpan(ReadOnlySpan<char> value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0f;

    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var character in value) hash = (hash ^ character) * 16777619u;
        return hash;
    }

    private static string Slug(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        char[] rented = null;
        var buf = value.Length <= 256
            ? stackalloc char[256]
            : (rented = ArrayPool<char>.Shared.Rent(value.Length));
        try
        {
            var len = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (char.IsLetterOrDigit(ch)) buf[len++] = char.ToLowerInvariant(ch);
                else if (len > 0 && buf[len - 1] != '_') buf[len++] = '_';
            }
            var start = 0;
            while (start < len && buf[start] == '_') start++;
            while (len > start && buf[len - 1] == '_') len--;
            return len <= start ? "" : StringPool.Shared.GetOrAdd(buf.Slice(start, len - start));
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }
}
