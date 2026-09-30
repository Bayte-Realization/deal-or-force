using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Paradox-style scenario history. Common catalogues define what may exist; history
/// chooses the 2024 starting values. Files are ordinary JSON and mods use the same schema.
/// </summary>
public static class HistoryDataLoader
{
    /// <summary>
    /// What a country was in 2024, before anything is worked out from it.
    ///
    /// This has to run before the population is dealt out, because it is the input to that
    /// arithmetic rather than a correction to it: how urban a country is and how much of it
    /// works the land decide what trades its regions hold, and a figure applied afterwards
    /// would be a figure the regions never saw.
    /// </summary>
    public static void ApplyBaseCountries(Campaign campaign)
    {
        // A bookmark is a folder, so a mod that adds a hundred countries to 2024 adds a
        // file beside the base game's rather than editing it.
        foreach (var file in GameDataLoader.DataFiles("history", "countries", "2024"))
            ApplyOptional(campaign, file, true, null);
    }

    /// <summary>
    /// What particular regions were, which can only be said once the regions have people:
    /// these are corrections to the deal, so they come after it.
    /// </summary>
    public static void ApplyBaseStates(Campaign campaign)
    {
        foreach (var file in GameDataLoader.DataFiles("history", "states", "2024"))
            ApplyOptional(campaign, file, false, null);
    }

    /// <summary>
    /// What the world had already signed when the scenario opens.
    ///
    /// Without this, 1 January 2024 is a world of three hundred countries that have never
    /// met: no NATO, no CSTO, nobody guaranteeing anybody. Every alliance a campaign had
    /// was one the computer grew from nothing over years, which is a world that runs but
    /// is not the world the scenario is about.
    ///
    /// An alliance here is a list of members and a name, and it is written out pairwise,
    /// because that is what an alliance IS in this engine: an attack on one is answered by
    /// everybody allied to it. The name is for the file to read like what it describes -
    /// nothing in the simulation needs it.
    /// </summary>
    public static void ApplyBaseDiplomacy(Campaign campaign)
    {
        foreach (var file in GameDataLoader.DataFiles("history", "diplomacy", "2024"))
        {
            if (file is null || !File.Exists(file)) continue;
            try { ApplyDiplomacy(campaign, File.ReadAllText(file), null); }
            catch (Exception exception)
            {
                DiagnosticLogs.Write("simulation",
                    $"HISTORY_DIPLOMACY_FAILED file={Path.GetFileName(file)} error={exception.Message}");
            }
        }
    }

    public static void ApplyDiplomacy(Campaign campaign, string json, List<string> errors)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("alliances", out var alliances) && alliances.ValueKind == JsonValueKind.Array)
            foreach (var bloc in alliances.EnumerateArray())
            {
                if (!bloc.TryGetProperty("members", out var members) ||
                    members.ValueKind != JsonValueKind.Array) continue;
                var signatories = new List<Nation>();
                foreach (var member in members.EnumerateArray())
                {
                    var nation = campaign.NationByTag(member.GetString() ?? "");
                    if (nation is null) errors?.Add($"Unknown country '{member.GetString()}' in an alliance.");
                    else signatories.Add(nation);
                }
                for (var one = 0; one < signatories.Count; one++)
                for (var two = one + 1; two < signatories.Count; two++)
                {
                    var relation = CampaignDiplomacy.Get(campaign, signatories[one].Id, signatories[two].Id);
                    relation.Stance = DiplomaticStance.Allied;
                    relation.AllianceSinceDay = campaign.ElapsedDays;
                    OpinionSystem.Remember(campaign, relation, "alliance");
                }
            }

        if (root.TryGetProperty("guarantees", out var guarantees) && guarantees.ValueKind == JsonValueKind.Array)
            foreach (var entry in guarantees.EnumerateArray())
            {
                if (!entry.TryGetProperty("by", out var byTag)) continue;
                var guarantor = campaign.NationByTag(byTag.GetString() ?? "");
                if (guarantor is null)
                {
                    errors?.Add($"Unknown country '{byTag.GetString()}' guaranteeing somebody.");
                    continue;
                }
                if (!entry.TryGetProperty("to", out var targets) || targets.ValueKind != JsonValueKind.Array) continue;
                foreach (var target in targets.EnumerateArray())
                {
                    var sheltered = campaign.NationByTag(target.GetString() ?? "");
                    if (sheltered is null || sheltered.Id == guarantor.Id)
                    {
                        errors?.Add($"Unknown country '{target.GetString()}' being guaranteed.");
                        continue;
                    }
                    var relation = CampaignDiplomacy.Get(campaign, guarantor.Id, sheltered.Id);
                    if (guarantor.Id == relation.A) relation.GuaranteeByA = true;
                    else relation.GuaranteeByB = true;
                    OpinionSystem.Remember(campaign, relation, "guaranteed_us");
                }
            }

        if (root.TryGetProperty("pacts", out var pacts) && pacts.ValueKind == JsonValueKind.Array)
            foreach (var pair in pacts.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array) continue;
                var tags = new List<Nation>();
                foreach (var tag in pair.EnumerateArray())
                {
                    var nation = campaign.NationByTag(tag.GetString() ?? "");
                    if (nation is null) errors?.Add($"Unknown country '{tag.GetString()}' in a pact.");
                    else tags.Add(nation);
                }
                if (tags.Count != 2) continue;
                var relation = CampaignDiplomacy.Get(campaign, tags[0].Id, tags[1].Id);
                relation.NonAggressionPact = true;
                OpinionSystem.Remember(campaign, relation, "non_aggression_pact");
            }
    }

    public static void ApplyCountries(Campaign campaign, string json, List<string> errors)
    {
        using var document = Parse(json);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var nation = campaign.NationByTag(entry.Name);
            if (nation is null || entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"Unknown history country '{entry.Name}'.");
                continue;
            }
            var value = entry.Value;
            SetFloat(value, "treasury", number => nation.Treasury = number);
            SetFloat(value, "gdp", number => nation.Gdp = Math.Max(0.01f, number));
            SetLong(value, "population", number => nation.Population = Math.Max(0, number));
            SetFloat(value, "tax_rate", number => nation.TaxRate = M.Clamp(number, 0.05f, 0.55f));
            SetFloat(value, "military_spending", number => nation.MilitarySpending = M.Clamp01(number));
            SetFloat(value, "literacy", number => { nation.Literacy = M.Clamp01(number); nation.StatedFigures.Add("literacy"); });
            SetFloat(value, "urbanisation", number => nation.Urbanisation = M.Clamp01(number));
            SetFloat(value, "agriculture", number => nation.AgricultureShare = M.Clamp01(number));
            SetFloat(value, "standard_of_living", number => { nation.StandardOfLiving = M.Clamp01(number); nation.StatedFigures.Add("standard_of_living"); });
            SetFloat(value, "stability", number => { nation.Stability = M.Clamp01(number); nation.StatedFigures.Add("stability"); });
            SetFloat(value, "war_support", number => nation.WarSupport = M.Clamp01(number));
            SetFloat(value, "manpower", number => nation.ManpowerReserve = Math.Max(0f, number));
            if (value.TryGetProperty("trade_policy", out var policy) && policy.ValueKind == JsonValueKind.String &&
                Enum.TryParse<TradePolicy>(policy.GetString(), true, out var parsed)) nation.TradePolicy = parsed;
            // An id the catalogue does not have is a scenario naming a government or an
            // ideology that a mod removed, and is worth complaining about rather than
            // silently leaving the country as whatever it was.
            if (value.TryGetProperty("government", out var government) && government.ValueKind == JsonValueKind.String)
            {
                var id = government.GetString() ?? "";
                if (GovernmentCatalog.Knows(id)) nation.Government = id;
                else errors?.Add($"{entry.Name}: no form of government called '{id}'.");
            }
            if (value.TryGetProperty("ideology", out var ideology) && ideology.ValueKind == JsonValueKind.String)
            {
                var id = ideology.GetString() ?? "";
                if (IdeologyCatalog.Knows(id)) nation.Ideology = id;
                else errors?.Add($"{entry.Name}: no ideology called '{id}'.");
            }
            // Whose state it is. The base game's are read with the catalogue of peoples, which
            // has to know them before any campaign exists; this is for a mod that says it again.
            if (value.TryGetProperty("primary_cultures", out var primary) && primary.ValueKind == JsonValueKind.Array)
                DemographyCatalog.Declare(nation.Tag, primary.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()));
        }
    }

    /// <summary>
    /// Every region history/states gives a head-count for, by map index, the later file winning.
    ///
    /// Read by the deal itself (EconomySystem.Seed), before anybody is placed, rather than only
    /// applied afterwards: a figure applied after the deal is one the region's peoples, trades
    /// and buildings were never dealt from, and it moved the country's total by whatever it
    /// added. ApplyStates still sets the same number once the deal is done, and finds nothing
    /// left to change.
    /// </summary>
    public static Dictionary<int, long> StatedPopulations(Campaign campaign) =>
        StatedRegions(campaign).Where(pair => pair.Value.Population.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Population.Value);

    /// <summary>
    /// What history/states writes down about a region that the deal needs before it starts: the
    /// head-count and how much of it lives in towns, who they are, what is built there and what
    /// lies under the ground. A field left null was not written, and the deal works it out from
    /// the country; one that was written is what the region has.
    /// </summary>
    public sealed class RegionFigures
    {
        public long? Population;
        public float? Urbanisation;
        /// <summary>The peoples as written, normalised later; a later file's list replaces an earlier one.</summary>
        public List<(string Culture, float Share)> Cultures;
        /// <summary>Building counts, merged key by key across files; a zero removes one.</summary>
        public Dictionary<BuildingKind, int> Buildings;
        /// <summary>Deposits, merged key by key across files; a zero removes one.</summary>
        public Dictionary<Commodity, float> Resources;
    }

    /// <summary>
    /// Everything history/states says about a region that the deal has to know before it deals
    /// it, the later file winning field by field. How urban a region is decides which trades its
    /// people follow and whether its share of the country's buildings is farms or factories -
    /// otherwise Moscow was dealt as a region of the national average, three quarters urban,
    /// with farms to match. Who lives there has to be known before the trades are crossed with
    /// the peoples; what is built and dug there before the region's books are first drawn up.
    /// </summary>
    public static Dictionary<int, RegionFigures> StatedRegions(Campaign campaign)
    {
        var stated = new Dictionary<int, RegionFigures>();
        var regions = RegionLookup(campaign);
        foreach (var file in GameDataLoader.DataFiles("history", "states", "2024"))
        {
            try
            {
                using var document = Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object || !regions.TryGetValue(entry.Name, out var state))
                        continue;
                    if (!stated.TryGetValue(state.MapIndex, out var figures)) figures = new RegionFigures();
                    var value = entry.Value;
                    if (value.TryGetProperty("population", out var population) &&
                        population.ValueKind == JsonValueKind.Number && population.TryGetInt64(out var people))
                        figures.Population = Math.Max(0, people);
                    if (value.TryGetProperty("urbanization", out var urbanisation) &&
                        urbanisation.ValueKind == JsonValueKind.Number && urbanisation.TryGetSingle(out var share))
                        figures.Urbanisation = M.Clamp01(share);
                    if (value.TryGetProperty("cultures", out var cultures) && cultures.ValueKind == JsonValueKind.Object)
                    {
                        var mix = new List<(string Culture, float Share)>();
                        foreach (var culture in cultures.EnumerateObject())
                            if (!culture.Name.StartsWith("//", StringComparison.Ordinal) &&
                                culture.Value.ValueKind == JsonValueKind.Number &&
                                culture.Value.TryGetSingle(out var part) && part > 0f)
                                mix.Add((CultureId(culture.Name), part));
                        if (mix.Count > 0) figures.Cultures = mix;
                    }
                    if (value.TryGetProperty("buildings", out var buildings) && buildings.ValueKind == JsonValueKind.Object)
                    {
                        figures.Buildings ??= new Dictionary<BuildingKind, int>();
                        foreach (var building in buildings.EnumerateObject())
                            if (BuildingCatalog.TryParse(building.Name, out var kind) &&
                                building.Value.ValueKind == JsonValueKind.Number && building.Value.TryGetInt32(out var level))
                                figures.Buildings[kind] = Math.Max(0, level);
                    }
                    if (value.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
                    {
                        figures.Resources ??= new Dictionary<Commodity, float>();
                        foreach (var resource in resources.EnumerateObject())
                            if (CommodityCatalog.TryParse(resource.Name, out var commodity) &&
                                resource.Value.ValueKind == JsonValueKind.Number && resource.Value.TryGetSingle(out var amount))
                                figures.Resources[commodity] = Math.Max(0f, amount);
                    }
                    stated[state.MapIndex] = figures;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // ApplyStates reports the same file when it reaches it; saying so twice helps nobody.
            }
        }
        return stated;
    }

    /// <summary>
    /// Regions by the names a history file may use for them: the id, or the ISO code. An id
    /// wins over an ISO code of the same spelling. Built once per file rather than searching
    /// eighteen hundred regions for each of the sixteen hundred a population file names.
    /// </summary>
    private static Dictionary<string, ScenarioState> RegionLookup(Campaign campaign)
    {
        var regions = new Dictionary<string, ScenarioState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in campaign.States)
            if (!string.IsNullOrEmpty(state.Iso)) regions.TryAdd(state.Iso, state);
        foreach (var state in campaign.States)
            if (!string.IsNullOrEmpty(state.Id)) regions[state.Id] = state;
        return regions;
    }

    public static void ApplyStates(Campaign campaign, string json, List<string> errors)
    {
        using var document = Parse(json);
        var populationChanged = new HashSet<int>();
        var ownersChanged = false;
        var regions = RegionLookup(campaign);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            regions.TryGetValue(entry.Name, out var state);
            if (state is null || entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"Unknown history state '{entry.Name}'.");
                continue;
            }
            var value = entry.Value;
            // Land that belongs to nobody (ColonisationCore). Before the population, so that
            // the country it is taken from has its own figure recounted without it. Who owns
            // land is otherwise the province map's business (provinces/ownership.csv), and a
            // tag here would be a second answer to that question, so "none" is the only one.
            if (value.TryGetProperty("owner", out var owner))
            {
                if (owner.ValueKind == JsonValueKind.String &&
                    string.Equals(owner.GetString(), "none", StringComparison.OrdinalIgnoreCase))
                {
                    if (state.NationMapIndex != 0) populationChanged.Add(state.NationMapIndex);
                    state.Unclaimed = true;
                    state.NationMapIndex = 0;
                    state.NationId = "";
                    ownersChanged = true;
                }
                else errors?.Add($"State '{entry.Name}': \"owner\" can only be \"none\"; who owns a province is set in history/provinces/ownership.csv.");
            }
            // The kind first: TryGetInt64 throws rather than answering false on a string, and a
            // quoted number in a mod's file would otherwise cost the whole file.
            if (value.TryGetProperty("population", out var population) &&
                population.ValueKind == JsonValueKind.Number && population.TryGetInt64(out var people))
            {
                EconomySystem.AdjustStatePopulation(state, Math.Max(0, people) - state.Population);
                populationChanged.Add(state.NationMapIndex);
            }
            SetFloat(value, "literacy", number => state.Literacy = M.Clamp01(number));
            SetFloat(value, "standard_of_living", number => state.StandardOfLiving = M.Clamp01(number));
            SetFloat(value, "radicalism", number => state.Radicalism = M.Clamp01(number));
            SetFloat(value, "urbanization", number => state.Urbanization = M.Clamp01(number));
            // Who claims it without holding it (ColonisationCore): Antarctica's seven.
            if (value.TryGetProperty("claims", out var claims) && claims.ValueKind == JsonValueKind.Array)
            {
                state.Claims.Clear();
                foreach (var claim in claims.EnumerateArray())
                    if (claim.ValueKind == JsonValueKind.String && claim.GetString() is { Length: > 0 } tag)
                        state.Claims.Add(tag);
            }
            // A scenario may open on a region already in ruins (WarDamage).
            SetFloat(value, "war_damage", number => state.WarDamage = M.Clamp01(number));
            // Who holds it on the first day when that is not its owner (HeldGround): Gaza.
            if (value.TryGetProperty("controller", out var controller))
            {
                var tag = controller.ValueKind == JsonValueKind.String ? controller.GetString() ?? "" : null;
                if (tag is not null && (tag.Length == 0 || campaign.NationByTag(tag) is not null))
                    state.StartController = tag;
                else errors?.Add($"State '{entry.Name}': \"controller\" must name a country by its tag.");
            }
            if (value.TryGetProperty("buildings", out var buildings) && buildings.ValueKind == JsonValueKind.Object)
                foreach (var building in buildings.EnumerateObject())
                    if (BuildingCatalog.TryParse(building.Name, out var kind) && building.Value.TryGetInt32(out var level))
                        state.Buildings[kind] = Math.Max(0, level);
                    else errors?.Add($"Invalid building '{building.Name}' in state '{entry.Name}'.");
            if (value.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
                foreach (var resource in resources.EnumerateObject())
                    if (CommodityCatalog.TryParse(resource.Name, out var commodity) && resource.Value.TryGetSingle(out var potential))
                        state.ResourceDeposits[commodity] = Math.Max(0f, potential);
                    else errors?.Add($"Invalid resource '{resource.Name}' in state '{entry.Name}'.");
            // The deal already crossed this very list with the region's trades; doing it again
            // would only spread every people over every trade evenly and undo the farm towns.
            if (value.TryGetProperty("cultures", out var cultures) && cultures.ValueKind == JsonValueKind.Object)
            {
                if (state.DealtCultures is null || state.DealtCultures != MixSignature(cultures))
                    ApplyCultureMix(state, cultures, errors);
            }
            else if (value.TryGetProperty("nationalities", out var nationalities) && nationalities.ValueKind == JsonValueKind.Object)
                ApplyCultureMix(state, nationalities, errors);
        }

        if (ownersChanged) campaign.InvalidateStateIndex();
        foreach (var mapIndex in populationChanged)
        {
            var nation = campaign.Nations.FirstOrDefault(item => item.MapIndex == mapIndex);
            if (nation is not null)
                nation.Population = campaign.States.Where(state => state.NationMapIndex == mapIndex).Sum(state => state.Population);
        }
    }

    /// <summary>
    /// A culture id as a file may write it: the id, or - because scenarios written before there
    /// were cultures said "IND" - a country's tag for that country's own people.
    /// </summary>
    private static string CultureId(string key) =>
        DemographyCatalog.Culture(key) is not null
            ? key
            : DemographyCatalog.PrimaryCultures(key).FirstOrDefault() ?? key.ToLowerInvariant();

    /// <summary>A mix of peoples, written the same way whichever order a file listed it in.</summary>
    internal static string MixSignature(IEnumerable<(string Culture, float Share)> mix)
    {
        var list = mix.Where(entry => entry.Share > 0f).ToList();
        var total = list.Sum(entry => entry.Share);
        if (total <= 0f) return "";
        return string.Join(";", list.OrderBy(entry => entry.Culture, StringComparer.Ordinal)
            .Select(entry => entry.Culture + "=" + Math.Round(entry.Share / total, 4)
                .ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string MixSignature(JsonElement cultures)
    {
        var mix = new List<(string, float)>();
        foreach (var entry in cultures.EnumerateObject())
            if (!entry.Name.StartsWith("//", StringComparison.Ordinal) && entry.Value.ValueKind == JsonValueKind.Number &&
                entry.Value.TryGetSingle(out var share) && share > 0f)
                mix.Add((CultureId(entry.Name), share));
        return MixSignature(mix);
    }

    /// <summary>
    /// Replace who lives in one region, keeping what they do for a living.
    ///
    /// A scenario that says Kirkuk is half Kurdish is making a claim about peoples, not
    /// about trades: the fields and the workshops are where they were, and the same share
    /// of the region still works them. So the trades are measured before the swap and the
    /// new peoples are spread across exactly the same ones - which also means a region
    /// rewritten by history still draws a sensible population screen.
    ///
    /// Keys are culture ids. A nation tag is accepted too and resolves to that country's
    /// own people, because scenarios written before there were cultures said "IND".
    /// </summary>
    private static void ApplyCultureMix(ScenarioState state, JsonElement cultures, List<string> errors)
    {
        var shares = new List<(string Culture, float Share)>();
        foreach (var entry in cultures.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!entry.Value.TryGetSingle(out var share) || share <= 0f)
            {
                errors?.Add($"Invalid culture share '{entry.Name}' in state '{state.Id}'.");
                continue;
            }
            shares.Add((CultureId(entry.Name), share));
        }
        var total = shares.Sum(item => item.Share);
        if (total <= 0f) return;

        // The trades as they stand, so the swap changes who works rather than what work exists.
        var trades = state.PopulationGroups
            .Where(group => group.Size > 0)
            .GroupBy(group => (group.Profession, group.Class, group.Sector))
            .Select(group => (group.Key.Profession, group.Key.Class, group.Key.Sector,
                Weight: group.Sum(entry => entry.Size) / (double)Math.Max(1, state.Population),
                Employment: group.Sum(entry => entry.Size * entry.Employment) / group.Sum(entry => entry.Size),
                Wealth: group.Sum(entry => entry.Size * entry.Wealth) / group.Sum(entry => entry.Size)))
            .ToList();
        if (trades.Count == 0)
            trades.Add(("labourers", SocialClass.Lower, Sector.Industry, 1.0, 0.9f, 0.38f));

        state.PopulationGroups.Clear();
        long assigned = 0;
        foreach (var (culture, share) in shares)
        {
            var faith = DemographyCatalog.FaithOf(culture);
            foreach (var trade in trades)
            foreach (var (religion, faithShare) in faith)
            {
                var size = (long)Math.Round(state.Population * (share / total) * trade.Weight * faithShare);
                if (size <= 0) continue;
                assigned += size;
                state.PopulationGroups.Add(new PopulationGroup
                {
                    Culture = culture,
                    Religion = religion,
                    Profession = trade.Profession,
                    Class = trade.Class,
                    Sector = trade.Sector,
                    Size = size,
                    Employment = trade.Employment,
                    Wealth = trade.Wealth,
                    StandardOfLiving = state.StandardOfLiving,
                    Radicalism = state.Radicalism
                });
            }
        }
        if (state.PopulationGroups.Count > 0)
            state.PopulationGroups[^1].Size += state.Population - assigned;
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json, new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    });

    private static void ApplyOptional(Campaign campaign, string path, bool countries, List<string> errors)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            if (countries) ApplyCountries(campaign, json, errors); else ApplyStates(campaign, json, errors);
        }
        catch (Exception exception) { errors?.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private static void SetFloat(JsonElement value, string name, Action<float> assign)
    {
        if (value.TryGetProperty(name, out var element) && element.TryGetSingle(out var number)) assign(number);
    }

    private static void SetLong(JsonElement value, string name, Action<long> assign)
    {
        if (value.TryGetProperty(name, out var element) && element.TryGetInt64(out var number)) assign(number);
    }
}

public sealed partial class ScenarioState
{
    /// <summary>
    /// The mix of peoples the start-of-game deal took from history/states for this region, as
    /// <see cref="HistoryDataLoader.MixSignature"/> writes it - so the corrections that follow
    /// the deal do not rebuild a region from the list it was just built from. Not saved.
    /// </summary>
    internal string DealtCultures;
}

public sealed partial class Nation
{
    /// <summary>
    /// Which of the country's own figures history/countries wrote, so the start-of-game deal
    /// works out only the ones it did not. Not saved: it matters on the day the world is dealt.
    /// </summary>
    internal readonly HashSet<string> StatedFigures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How far the country's human capital sits from its literacy: the literacy its economy's
    /// development implies on the first day, less the literacy history wrote. Negative for
    /// nearly everybody - the world is more literate than it is developed. Zero for a save
    /// from before literacy was read from history, whose literacy was that figure already.
    /// </summary>
    public float LiteracyOffset;

    /// <summary>
    /// What productivity, research and a region's stability answer to: how developed the
    /// economy is, moved by literacy as literacy moves. Literacy itself is the share of people
    /// who can read, and 99 % of Russians and Ukrainians can - which is not what makes one
    /// country's factories and laboratories out-produce another's. The balance of the world
    /// was set against this figure when it was the only literacy the game had.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float HumanCapital => Literacy + LiteracyOffset;
}
