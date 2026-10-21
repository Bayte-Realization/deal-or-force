using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Names are content, not a second population or politician simulation.</summary>
public static class PersonNameCatalog
{
    public readonly record struct PersonName(string FullName, string Given, string Family, string PoolId, bool Female, string Middle = "");
    public readonly record struct PoolInfo(string Id, int MaleNames, int FemaleNames, int Families);
    private sealed class Pool
    {
        public string Id;
        public string[] Male = Array.Empty<string>(), Female = Array.Empty<string>(), Family = Array.Empty<string>(), FemaleFamily = Array.Empty<string>();
        public string[] MaleMiddle = Array.Empty<string>(), FemaleMiddle = Array.Empty<string>();
        public string Pattern = "{given} {family}";
        public string FemalePattern;
    }

    private static readonly Dictionary<string, Pool> Pools = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Cultures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Groups = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static IReadOnlyList<PoolInfo> All
    {
        get { EnsureLoaded(); return Pools.Values.OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new PoolInfo(p.Id, p.Male.Length, p.Female.Length, p.Family.Length)).ToArray(); }
    }

    public static void ResetCatalog()
    { _loaded = false; Pools.Clear(); Countries.Clear(); Cultures.Clear(); Groups.Clear(); }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var document in GameDataLoader.DataDocuments("common", "character_names"))
            using (document) Merge(document.RootElement);
    }

    /// <summary>Later files replace supplied arrays; absent fields inherit. Reject broken rows atomically.</summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (entry.Value.ValueKind != JsonValueKind.Object)
            { errors?.Add("character_names: " + entry.Name + " must be an object"); continue; }
            var value = entry.Value; Pools.TryGetValue(entry.Name, out var old);
            if (value.TryGetProperty("inherits", out var parent) && parent.ValueKind == JsonValueKind.String)
            {
                if (!Pools.TryGetValue(parent.GetString() ?? "", out old))
                { errors?.Add("character_names: missing parent in " + entry.Name); continue; }
            }
            var pool = new Pool { Id = entry.Name,
                Male = Read(value, "male", old?.Male), Female = Read(value, "female", old?.Female),
                Family = Read(value, "family", old?.Family), FemaleFamily = Read(value, "female_family", old?.FemaleFamily),
                MaleMiddle = Read(value, "male_middle", old?.MaleMiddle), FemaleMiddle = Read(value, "female_middle", old?.FemaleMiddle),
                Pattern = value.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String
                    ? pattern.GetString() ?? "" : old?.Pattern ?? "{given} {family}",
                FemalePattern = value.TryGetProperty("female_pattern", out var femalePattern) && femalePattern.ValueKind == JsonValueKind.String
                    ? femalePattern.GetString() ?? "" : old?.FemalePattern };
            if (pool.Male.Length == 0 || pool.Female.Length == 0 || pool.Family.Length == 0 ||
                !ValidPattern(pool.Pattern) || pool.FemalePattern is not null && !ValidPattern(pool.FemalePattern) ||
                pool.FemaleFamily.Length > 0 && pool.FemaleFamily.Length != pool.Family.Length)
            { errors?.Add("character_names: invalid name arrays or pattern in " + entry.Name); continue; }
            Pools[entry.Name] = pool;
            Bind(value, "countries", Countries, entry.Name);
            Bind(value, "cultures", Cultures, entry.Name);
            Bind(value, "groups", Groups, entry.Name);
        }
    }

    private static bool ValidPattern(string pattern)
    {
        if (pattern.Length > 160 || !pattern.Contains("{given}", StringComparison.Ordinal)) return false;
        var literal = pattern.Replace("{given}", "").Replace("{family}", "").Replace("{middle}", "");
        return !literal.Contains('{') && !literal.Contains('}');
    }

    private static string[] Read(JsonElement row, string key, string[] inherited = null)
    {
        if (!row.TryGetProperty(key, out var data)) return inherited ?? Array.Empty<string>();
        if (data.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return data.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()?.Trim() ?? "").Where(v => v.Length is > 0 and <= 120)
            .Take(4096).ToArray();
    }

    private static void Bind(JsonElement row, string key, Dictionary<string, string> map, string pool)
    { foreach (var alias in Read(row, key)) map[alias] = pool; }

    /// <summary>Declared/founded culture outranks a country tag. Dynamic R01 states therefore keep local names.</summary>
    public static string PoolFor(Nation nation)
    {
        if (nation is null) return "";
        var culture = nation.DeclaredCulture;
        if (string.IsNullOrEmpty(culture)) culture = nation.FoundedCultures.FirstOrDefault() ?? "";
        return PoolFor(nation.Tag, culture);
    }

    public static string PoolFor(string tag, string culture = "")
    {
        EnsureLoaded();
        if (!string.IsNullOrEmpty(culture) && Cultures.TryGetValue(culture, out var byCulture)) return byCulture;
        if (!string.IsNullOrEmpty(tag) && Countries.TryGetValue(tag, out var byCountry)) return byCountry;
        if (string.IsNullOrEmpty(culture)) culture = DemographyCatalog.PrimaryCulture(tag);
        if (Cultures.TryGetValue(culture ?? "", out byCulture)) return byCulture;
        var group = DemographyCatalog.Culture(culture)?.Group;
        return group is not null && Groups.TryGetValue(group, out var byGroup) ? byGroup : "";
    }

    /// <summary>Pure seeded draw; no world RNG, day-dependent reroll, locale dependence, or state writes.</summary>
    public static bool TryGenerate(string tag, string poolId, int serial, int seed, int stream,
        out PersonName name, bool? female = null)
    {
        EnsureLoaded(); name = default;
        if (poolId is null || !Pools.TryGetValue(poolId, out var pool)) return false;
        var roll = Hash(tag, unchecked((uint)seed ^ (uint)serial * 0x9e3779b9u ^ (uint)stream * 0x85ebca6bu));
        var woman = female ?? ((roll & 1u) != 0);
        var givenNames = woman ? pool.Female : pool.Male;
        var given = givenNames[Hash(pool.Id, roll ^ 0xD1B54A35u) % (uint)givenNames.Length];
        var familyIndex = (int)(Hash(tag, roll ^ 0x94D049BBu) % (uint)pool.Family.Length);
        var family = woman && pool.FemaleFamily.Length > 0 ? pool.FemaleFamily[familyIndex] : pool.Family[familyIndex];
        var middles = woman ? pool.FemaleMiddle : pool.MaleMiddle;
        var middle = middles.Length == 0 ? "" : middles[Hash(pool.Id, roll ^ 0xA5A5A5A5u) % (uint)middles.Length];
        var full = (woman ? pool.FemalePattern ?? pool.Pattern : pool.Pattern)
            .Replace("{given}", given).Replace("{family}", family).Replace("{middle}", middle).Trim();
        name = new(full, given, family, pool.Id, woman, middle);
        return true;
    }

    private static uint Hash(string value, uint salt)
    {
        unchecked
        {
            var hash = 2166136261u ^ salt;
            if (value is not null) foreach (var ch in value) hash = (hash ^ char.ToUpperInvariant(ch)) * 16777619u;
            hash ^= hash >> 16; hash *= 0x7feb352du; hash ^= hash >> 15; hash *= 0x846ca68bu;
            return hash ^ (hash >> 16);
        }
    }
}
