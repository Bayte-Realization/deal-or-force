using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce.Modding;

public sealed class ModManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("supported_game_version")] public string SupportedGameVersion { get; set; } = "*";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("load_order")] public int LoadOrder { get; set; }
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();
    [JsonPropertyName("gdscript_entry")] public string GdscriptEntry { get; set; } = "";
    [JsonPropertyName("csharp_assembly")] public string CsharpAssembly { get; set; } = "";
}

public sealed class ModPackage
{
    public ModManifest Manifest { get; init; }
    public string PhysicalRoot { get; init; } = "";
    public string VirtualRoot { get; init; } = "";
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Paradox-style layered loader. Later packages override earlier JSON values. Code is
/// optional: most balance/content mods only need manifest.json and common/**/*.json.
/// </summary>
public sealed class ModLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly List<ModPackage> _packages = new();
    private readonly List<(IGlobalityMod Instance, ModContext Context)> _csharp = new();
    private readonly List<(GodotObject Instance, string ModId)> _gdscript = new();
    private readonly List<string> _log = new();
    private ModApi _api;

    public IReadOnlyList<ModPackage> Packages => _packages;
    public IReadOnlyList<string> Log => _log;
    public int EnabledCount => _packages.Count;
    public int ErrorCount => _packages.Sum(package => package.Errors.Count);
    public string StatusLine => EnabledCount == 0
        ? "No external mods loaded · open user://mods or res://Mods"
        : $"{EnabledCount} mod(s) loaded" + (ErrorCount > 0 ? $" · {ErrorCount} warning(s)" : "");

    public void Discover()
    {
        _packages.Clear();
        _log.Clear();

        // Development mods ship beside the project. Installed/user mods with the same
        // id win, allowing local updates without touching the base installation.
        var candidates = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        DiscoverRoot("res://Mods", candidates);
        DiscoverRoot("user://mods", candidates);

        var enabled = candidates.Values.Where(package => package.Manifest.Enabled).ToList();
        foreach (var package in enabled.ToArray())
        {
            var missing = package.Manifest.Dependencies
                .Where(dependency => !enabled.Any(candidate => candidate.Manifest.Id.Equals(dependency, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (missing.Length == 0) continue;
            package.Errors.Add($"Missing dependencies: {string.Join(", ", missing)}");
            enabled.Remove(package);
        }

        _packages.AddRange(TopologicalOrder(enabled));
        Write($"Discovery complete: {_packages.Count} enabled mod(s).");
    }

    public void ApplyCampaignData(Campaign campaign)
    {
        // Reset mutable catalogues before every campaign so disabling a mod cannot
        // leak its map overrides into the next session in the same process.
        TerritorialTopology.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        MapRasterCatalog.ResetCatalog();
        Loc.ResetCatalog();
        InterfaceSkin.ResetCatalog();
        ProvinceOwnership.ResetCatalog();
        BuildingCatalog.ResetCatalog();
        CommodityCatalog.ResetCatalog();
        DecisionCatalog.ResetCatalog();
        EventCatalog.ResetCatalog();
        ModifierCatalog.ResetCatalog();
        AiStrategyCatalog.ResetCatalog();
        foreach (var package in _packages)
        {
            ApplyDefines(package, campaign.Defines);
            ApplyUnits(package);
            ApplyGoods(package);
            ApplyBuildings(package);
            ApplyDecisions(package);
            ApplyEvents(package);
            ApplyModifiers(package);
            ApplyAiStrategies(package);
            ApplyMapData(package);
            ApplyLocalization(package);
            ApplySkin(package);
            ApplyCountries(package, campaign);
            ApplyProvinceOwnership(package);
            ApplyHistory(package, campaign);
        }
    }

    private void ApplyBuildings(ModPackage package)
    {
        foreach (var path in JsonFiles(package, "common", "buildings"))
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!Enum.TryParse<BuildingKind>(entry.Name, true, out var kind) || entry.Value.ValueKind != JsonValueKind.Object)
                {
                    package.Errors.Add($"Unknown building '{entry.Name}' in {Path.GetFileName(path)}");
                    continue;
                }
                var value = entry.Value;
                string Text(string key) => value.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                float? Number(string key) => value.TryGetProperty(key, out var e) && e.TryGetSingle(out var n) ? n : null;
                int? Integer(string key) => value.TryGetProperty(key, out var e) && e.TryGetInt32(out var n) ? n : null;
                if (!BuildingCatalog.Override(kind, Text("name"), Number("cost"), Integer("days"),
                        Number("gdp_gain"), Number("capacity_gain"), Text("description")))
                    package.Errors.Add($"Invalid building values for '{entry.Name}' in {Path.GetFileName(path)}");
            }
        }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyDecisions(ModPackage package)
    {
        foreach (var path in JsonFiles(package, "common", "decisions"))
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var value = entry.Value;
                string Text(string key) => value.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                float? Number(string key) => value.TryGetProperty(key, out var e) && e.TryGetSingle(out var n) ? n : null;
                int? Integer(string key) => value.TryGetProperty(key, out var e) && e.TryGetInt32(out var n) ? n : null;
                if (!Enum.TryParse<DecisionKind>(Text("kind"), true, out var kind) ||
                    !DecisionCatalog.Override(entry.Name, Text("name"), Text("description"),
                        Integer("cooldown_days"), Number("gdp_cost_share"), kind))
                    package.Errors.Add($"Invalid decision '{entry.Name}' in {Path.GetFileName(path)}");
            }
        }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyEvents(ModPackage package)
    {
        foreach (var path in JsonFiles(package, "events"))
        try { EventCatalog.Load(File.ReadAllText(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyModifiers(ModPackage package)
    {
        foreach (var path in JsonFiles(package, "common", "modifiers"))
        try { ModifierCatalog.Load(File.ReadAllText(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyAiStrategies(ModPackage package)
    {
        foreach (var path in JsonFiles(package, "common", "ai_strategy"))
        try { AiStrategyCatalog.Load(File.ReadAllText(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyMapData(ModPackage package)
    {
        MapRasterCatalog.ApplyDirectory(Path.Combine(package.PhysicalRoot, "map_data"), package.Errors);
        foreach (var path in JsonFiles(package, "map_data"))
        try
        {
            var json = File.ReadAllText(path);
            if (HydrologyCatalog.LooksLikeGeoJson(json)) HydrologyCatalog.LoadGeoJson(json, package.Errors);
            else TerritorialTopology.LoadOverrides(json, package.Errors);
        }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    /// <summary>
    /// A translation mod is just a folder of localization/&lt;language&gt;.json files - no
    /// script, no manifest entry, no rebuild. Loading them in the same layered pass as
    /// everything else means a later mod can correct an earlier mod's wording.
    /// </summary>
    private void ApplyLocalization(ModPackage package)
    {
        try { Loc.LoadDirectory(Path.Combine(package.PhysicalRoot, "localization"), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"localization: {exception.Message}"); }
    }

    /// <summary>
    /// A reskin is a folder of interface/*.json files overriding named colour tokens. It is
    /// the same layered pass as everything else, so a later mod can retint an earlier one -
    /// and a mod that only changes three tokens keeps the rest of the game's palette.
    /// </summary>
    private void ApplySkin(ModPackage package)
    {
        try
        {
            InterfaceSkin.LoadDirectory(Path.Combine(package.PhysicalRoot, "interface"), package.Errors);
            InterfaceSkin.LoadDirectory(Path.Combine(package.PhysicalRoot, "gui"), package.Errors);
        }
        catch (Exception exception) { package.Errors.Add($"interface: {exception.Message}"); }
    }

    /// <summary>
    /// A mod's own political map. It lists only the provinces it moves, layered over the
    /// base game's in load order, so redrawing one border is one line rather than a fork
    /// of the whole world.
    /// </summary>
    private void ApplyProvinceOwnership(ModPackage package)
    {
        try { ProvinceOwnership.LoadDirectory(Path.Combine(package.PhysicalRoot, "history", "provinces"), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"history/provinces: {exception.Message}"); }
    }

    private void ApplyHistory(ModPackage package, Campaign campaign)
    {
        foreach (var path in JsonFiles(package, "history", "countries"))
        try { HistoryDataLoader.ApplyCountries(campaign, File.ReadAllText(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in JsonFiles(package, "history", "states"))
        try { HistoryDataLoader.ApplyStates(campaign, File.ReadAllText(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyUnits(ModPackage package)
    {
        foreach (var path in JsonFiles(package, "common", "units"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                foreach (var unit in document.RootElement.EnumerateObject())
                {
                    if (unit.Value.ValueKind != JsonValueKind.Object)
                    {
                        package.Errors.Add($"Invalid unit type '{unit.Name}' in {Path.GetFileName(path)}");
                        continue;
                    }
                    // The whole object is handed over, so a mod can set any stat a template
                    // has - including the ones added after this loader was written.
                    if (!UnitTypeCatalog.Override(unit.Name, unit.Value))
                        package.Errors.Add($"Invalid values for unit type '{unit.Name}' in {Path.GetFileName(path)}");
                }
            }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    public void StartCode(ModApi api, Campaign campaign)
    {
        _api = api;
        _api.Bind(campaign);
        _csharp.Clear();
        foreach (var (instance, _) in _gdscript) instance.Dispose();
        _gdscript.Clear();

        foreach (var package in _packages)
        {
            LoadCsharp(package, campaign);
            LoadGdscript(package, campaign);
        }
    }

    public void OnDayAdvanced(Campaign campaign)
    {
        foreach (var plugin in _csharp)
        {
            plugin.Context.Campaign = campaign;
            Try(packageId: plugin.Context.ModId, () => plugin.Instance.OnDayAdvanced(plugin.Context));
        }

        foreach (var plugin in _gdscript)
            if (plugin.Instance.HasMethod("on_day_advanced"))
                Try(plugin.ModId, () => plugin.Instance.Call("on_day_advanced", _api));
    }

    private void DiscoverRoot(string virtualRoot, Dictionary<string, ModPackage> candidates)
    {
        var physicalRoot = ProjectSettings.GlobalizePath(virtualRoot);
        if (!Directory.Exists(physicalRoot)) return;

        foreach (var directory in Directory.GetDirectories(physicalRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(directory).StartsWith("_", StringComparison.Ordinal)) continue;
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    Write($"Ignored {manifestPath}: manifest has no id.");
                    continue;
                }

                manifest.Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name;
                var folder = Path.GetFileName(directory);
                candidates[manifest.Id] = new ModPackage
                {
                    Manifest = manifest,
                    PhysicalRoot = directory,
                    VirtualRoot = $"{virtualRoot}/{folder}"
                };
            }
            catch (Exception exception)
            {
                Write($"Ignored {manifestPath}: {exception.Message}");
            }
        }
    }

    private static IEnumerable<ModPackage> TopologicalOrder(IReadOnlyCollection<ModPackage> packages)
    {
        var result = new List<ModPackage>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byId = packages.ToDictionary(package => package.Manifest.Id, StringComparer.OrdinalIgnoreCase);

        void Visit(ModPackage package)
        {
            if (!visited.Add(package.Manifest.Id)) return;
            visiting.Add(package.Manifest.Id);
            foreach (var dependency in package.Manifest.Dependencies)
                if (byId.TryGetValue(dependency, out var required) && !visiting.Contains(dependency))
                    Visit(required);
            visiting.Remove(package.Manifest.Id);
            result.Add(package);
        }

        foreach (var package in packages.OrderBy(package => package.Manifest.LoadOrder).ThenBy(package => package.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            Visit(package);
        return result;
    }

    private void ApplyDefines(ModPackage package, Defines defines)
    {
        foreach (var path in JsonFiles(package, "common", "defines"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    var property = typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(candidate => Normalize(candidate.Name) == Normalize(entry.Name));
                    if (property is null || !property.CanWrite || !TryConvert(entry.Value, property.PropertyType, out var value))
                    {
                        package.Errors.Add($"Unknown or invalid define '{entry.Name}' in {Path.GetFileName(path)}");
                        continue;
                    }
                    property.SetValue(defines, value);
                }
            }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    private void ApplyCountries(ModPackage package, Campaign campaign)
    {
        foreach (var path in JsonFiles(package, "common", "countries"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var country in document.RootElement.EnumerateObject())
                {
                    var nation = campaign.NationByTag(country.Name);
                    if (nation is null || country.Value.ValueKind != JsonValueKind.Object)
                    {
                        package.Errors.Add($"Unknown country '{country.Name}' in {Path.GetFileName(path)}");
                        continue;
                    }
                    foreach (var entry in country.Value.EnumerateObject())
                        if (!ApplyNationValue(nation, entry.Name, entry.Value))
                            package.Errors.Add($"Unknown or invalid country field '{country.Name}.{entry.Name}'");
                }
            }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    private void ApplyGoods(ModPackage package)
    {
        foreach (var path in JsonFiles(package, "common", "goods"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var good in document.RootElement.EnumerateObject())
                {
                    if (good.Value.ValueKind != JsonValueKind.Object)
                    {
                        package.Errors.Add($"Invalid good '{good.Name}' in {Path.GetFileName(path)}");
                        continue;
                    }
                    var value = good.Value;
                    string Text(string key) => value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                    float? Number(string key) => value.TryGetProperty(key, out var element) && element.TryGetSingle(out var number) ? number : null;
                    if (!CommodityCatalog.Override(good.Name, Text("name"), Text("icon"), Number("base_price"), Number("essential_weight"), Text("description")))
                        package.Errors.Add($"Unknown good '{good.Name}' in {Path.GetFileName(path)}");
                }
            }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    private static bool ApplyNationValue(Nation nation, string key, JsonElement value)
    {
        switch (Normalize(key))
        {
            case "name" when value.ValueKind == JsonValueKind.String: nation.Name = value.GetString() ?? nation.Name; return true;
            case "color" when value.ValueKind == JsonValueKind.String: nation.Color = value.GetString() ?? nation.Color; return true;
            case "capitalname" when value.ValueKind == JsonValueKind.String: nation.CapitalName = value.GetString() ?? nation.CapitalName; return true;
            case "population" when value.TryGetInt64(out var population): nation.Population = Math.Max(0, population); return true;
            case "treasury" when value.TryGetSingle(out var treasury): nation.Treasury = treasury; return true;
            case "gdp" when value.TryGetSingle(out var gdp): nation.Gdp = Math.Max(0, gdp); return true;
            case "militaryspending" when value.TryGetSingle(out var spending): nation.MilitarySpending = Math.Clamp(spending, 0f, 1f); return true;
            case "taxrate" when value.TryGetSingle(out var tax): nation.TaxRate = Math.Clamp(tax, 0f, 1f); return true;
            case "stability" when value.TryGetSingle(out var stability): nation.Stability = Math.Clamp(stability, 0f, 1f); return true;
            case "warsupport" when value.TryGetSingle(out var support): nation.WarSupport = Math.Clamp(support, 0f, 1f); return true;
            case "manpower" when value.TryGetSingle(out var manpower): nation.ManpowerReserve = Math.Max(0f, manpower); return true;
            default: return false;
        }
    }

    private void LoadCsharp(ModPackage package, Campaign campaign)
    {
        if (string.IsNullOrWhiteSpace(package.Manifest.CsharpAssembly)) return;
        var path = SafePath(package, package.Manifest.CsharpAssembly);
        if (path is null || !File.Exists(path))
        {
            package.Errors.Add($"C# assembly not found: {package.Manifest.CsharpAssembly}");
            return;
        }

        Try(package.Manifest.Id, () =>
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            var types = assembly.GetTypes().Where(type => !type.IsAbstract && typeof(IGlobalityMod).IsAssignableFrom(type));
            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is not IGlobalityMod instance) continue;
                var context = new ModContext(package.Manifest.Id, message => Write($"[{package.Manifest.Id}] {message}")) { Campaign = campaign };
                instance.OnLoad(context);
                instance.OnCampaignCreated(context);
                _csharp.Add((instance, context));
            }
        });
    }

    private void LoadGdscript(ModPackage package, Campaign campaign)
    {
        if (string.IsNullOrWhiteSpace(package.Manifest.GdscriptEntry)) return;
        if (!IsSafeRelativePath(package.Manifest.GdscriptEntry))
        {
            package.Errors.Add("GDScript entry must stay inside the mod directory.");
            return;
        }
        var resourcePath = $"{package.VirtualRoot}/{package.Manifest.GdscriptEntry.Replace('\\', '/')}";

        Try(package.Manifest.Id, () =>
        {
            var script = ResourceLoader.Load<GDScript>(resourcePath);
            if (script is null || !script.CanInstantiate())
                throw new InvalidOperationException($"Cannot instantiate {resourcePath}");
            var instance = script.New().AsGodotObject();
            if (instance is null) throw new InvalidOperationException($"{resourcePath} returned no instance");
            _gdscript.Add((instance, package.Manifest.Id));
            if (instance.HasMethod("on_load")) instance.Call("on_load", _api);
            if (instance.HasMethod("on_campaign_created")) instance.Call("on_campaign_created", _api);
        });
    }

    private static IEnumerable<string> JsonFiles(ModPackage package, params string[] parts)
    {
        var directory = parts.Aggregate(package.PhysicalRoot, Path.Combine);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();
    }

    private static string SafePath(ModPackage package, string relative)
    {
        if (!IsSafeRelativePath(relative)) return null;
        var root = Path.GetFullPath(package.PhysicalRoot) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(package.PhysicalRoot, relative));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Split('/', '\\').Contains("..");

    private static bool TryConvert(JsonElement element, Type type, out object value)
    {
        value = null;
        try
        {
            if (type == typeof(string)) value = element.GetString();
            else if (type == typeof(bool)) value = element.GetBoolean();
            else if (type == typeof(int)) value = element.GetInt32();
            else if (type == typeof(long)) value = element.GetInt64();
            else if (type == typeof(float)) value = element.GetSingle();
            else if (type == typeof(double)) value = element.GetDouble();
            else if (type == typeof(List<float>)) value = element.EnumerateArray().Select(item => item.GetSingle()).ToList();
            else return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Try(string packageId, Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            var package = _packages.FirstOrDefault(candidate => candidate.Manifest.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            package?.Errors.Add(exception.Message);
            Write($"[{packageId}] {exception}");
        }
    }

    private void Write(string line)
    {
        _log.Add(line);
        GD.Print($"[mods] {line}");
    }

    private static string Normalize(string value) =>
        (value ?? "").Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
