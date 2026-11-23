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

namespace DealOrForce.Modding;

/// <summary>
/// <c>manifest.json</c> at the root of one package. Data mods usually set only id, name, and enabled.
/// Code is optional and mutually independent: <c>gdscript_entry</c>, <c>engine_script_entry</c> (Luau),
/// <c>corecode_assembly</c> or the older <c>csharp_assembly</c> (C# or F#), and <c>worker_executable</c>.
/// Folders whose names start with <c>_</c> are templates and are never discovered.
/// A data overlay does not need any of the code fields. Naming a code file does not make the package a sandbox.
/// </summary>
public sealed class ModManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("supported_game_version")] public string SupportedGameVersion { get; set; } = "*";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("load_order")] public int LoadOrder { get; set; }
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();
    [JsonPropertyName("frontend_actions")] public List<ModFrontendActionDeclaration> FrontendActions { get; set; } = new();
    [JsonPropertyName("gdscript_entry")] public string GdscriptEntry { get; set; } = "";
    [JsonPropertyName("engine_script_entry")] public string EngineScriptEntry { get; set; } = "";
    [JsonPropertyName("csharp_assembly")] public string CsharpAssembly { get; set; } = "";
    [JsonPropertyName("corecode_assembly")] public string CorecodeAssembly { get; set; } = "";
    [JsonPropertyName("worker_executable")] public string WorkerExecutable { get; set; } = "";
    [JsonPropertyName("worker_script")] public string WorkerScript { get; set; } = "";
    [JsonPropertyName("worker_timeout_ms")] public int WorkerTimeoutMs { get; set; } = 500;
    [JsonPropertyName("worker_interval_days")] public int WorkerIntervalDays { get; set; } = 7;
    [JsonPropertyName("total_conversion")] public bool TotalConversion { get; set; }
    [JsonPropertyName("replace_paths")] public List<string> ReplacePaths { get; set; } = new();
    [JsonPropertyName("exclude_paths")] public List<string> ExcludePaths { get; set; } = new();
    [JsonPropertyName("scenario_id")] public string ScenarioId { get; set; } = "";
    [JsonPropertyName("era_name")] public string EraName { get; set; } = "";
    [JsonPropertyName("theme")] public string Theme { get; set; } = "";
    [JsonPropertyName("music_playlist")] public string MusicPlaylist { get; set; } = "";
    [JsonPropertyName("calendar")] public CalendarProfile Calendar { get; set; }
    [JsonPropertyName("disabled_features")] public List<string> DisabledFeatures { get; set; } = new();
    [JsonPropertyName("era_features")] public Dictionary<string, string> EraFeatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModPackage
{
    public ModManifest Manifest { get; init; }
    public string PhysicalRoot { get; init; } = "";
    public string VirtualRoot { get; init; } = "";

    /// <summary>Whether this package came from `dlc/` rather than from a mod folder.</summary>
    public bool IsContentPack { get; init; }

    public List<string> Errors { get; } = new();
}

/// <summary>
/// How a package lands on a campaign. Discovery reads manifests only. <see cref="ApplyCampaignData"/>
/// is the overlay pass, once per campaign, never per frame and never per day.
///
/// Order is base game, then content packs, then enabled mods. Inside that list, later wins.
/// Each category is reset to the base game first, then every package's files in that category
/// are merged. JSON, YAML, and YML share one reader (<c>StructuredData</c>): the format does not
/// add keys. Name only the keys you change. A forked copy of a whole base file goes stale on the next patch.
///
/// Scripted effects are parsed while the event or decision is read, from the libraries loaded
/// earlier in the same package loop. A trigger that matches does not grant permission to write
/// a field. Unknown effect keys are reported and skipped; they do not run.
///
/// Code starts in <see cref="StartCode"/>: compiled mods (C# and F# via <see cref="IGlobalityMod"/>),
/// then GDScript, then Luau, then an external worker. Workers and Luau are single-player.
/// A mod extends the economy, the war, and the diplomacy that already tick. It does not ship a second one.
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
    private readonly ModFrontendActionCatalog _frontendActions = new();
    private readonly List<ModPackage> _availableMods = new();
    private readonly List<(IGlobalityMod Instance, ModContext Context)> _csharp = new();
    private readonly List<ManagedModLoadContext> _managedContexts = new();
    private readonly List<WorkerModBackend> _workers = new();
    private readonly List<string> _log = new();
    private IModScriptRuntime _scriptRuntime;
    private object _api;

    /// <summary>
    /// The content packs that ship with the game.
    ///
    /// A DLC is loaded through this same loader rather than beside it, because there is one
    /// content pipeline in this game and a second one would drift from the first. It goes in
    /// front of every mod in the order, so a mod can correct a DLC exactly as it can correct
    /// the base game.
    /// </summary>
    public DlcLoader Dlc { get; } = new();

    public IReadOnlyList<ModPackage> Packages => _packages;
    public IReadOnlyList<ModFrontendActionDescriptor> FrontendActions() => _frontendActions.Actions;
    public bool TryRunFrontendAction(string fullActionId, out string note) => _frontendActions.TryRun(fullActionId, out note);
    public IReadOnlyList<ModPackage> AvailableMods => _availableMods;
    public IReadOnlyList<string> Log => _log;
    public int EnabledCount => _packages.Count(package => !package.IsContentPack);
    public int ContentPackCount => _packages.Count(package => package.IsContentPack);
    public int ErrorCount => _packages.Sum(package => package.Errors.Count);
    public string StatusLine
    {
        get
        {
            // On the main menu, under the campaign summary, and in English until now while
            // the rest of that menu was translated.
            var packs = Dlc.Packages.Count == 0 ? "" : $"{Dlc.StatusLine} · ";
            // The folder, not the engine's shorthand: "user://mods" is exact and means nothing
            // to a player looking for it. After the folder junction this path is inside the one
            // game folder under Documents, which is a place a person can open.
            var userFolder = ModPlatform.GlobalizePath("user://mods");
            if (string.IsNullOrWhiteSpace(userFolder) || userFolder.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackFolder = ModPlatform.UserModsDirectory();
                if (!string.IsNullOrWhiteSpace(fallbackFolder)) userFolder = fallbackFolder;
            }
            var mods = EnabledCount == 0
                ? Simulation.Loc.Format("no external mods · open {0} or res://mods", userFolder)
                : Simulation.Loc.Format("{0} mod(s) loaded", EnabledCount);
            return packs + mods +
                (ErrorCount > 0 ? " · " + Simulation.Loc.Format("{0} warning(s)", ErrorCount) : "");
        }
    }

    /// <summary>
    /// Find packages on disk and decide which are enabled. Does not read common/ and does not touch a campaign.
    /// user://mods with the same id replaces the copy that shipped beside the game. <c>_</c> folders are skipped.
    /// </summary>
    public void Discover() => Discover(null, null, true);

    /// <summary>
    /// Find packages under explicit root directories (when supplied) or the standard game/user mod folders.
    /// Keeps manifest parsing, load-order sorting, dependency resolution and virtual roots identical
    /// across the live game and headless test harnesses.
    /// </summary>
    public void Discover(IEnumerable<string> modRoots, ContentSelection selection = null, bool includeDlc = false)
    {
        _frontendActions.ResetDeclarations(Array.Empty<(string, IEnumerable<ModFrontendActionDeclaration>)>());
        _packages.Clear();
        _availableMods.Clear();
        _log.Clear();

        var candidates = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        if (modRoots is not null)
        {
            foreach (var root in modRoots)
                DiscoverRoot(root, "res://mods", candidates);
        }
        else
        {
            foreach (var root in GameDataLoader.InstallFolders("mods"))
                DiscoverRoot(root, "res://mods", candidates);
            var userMods = ModPlatform.GlobalizePath("user://mods");
            if (!string.IsNullOrWhiteSpace(userMods) && !userMods.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
                DiscoverRoot(userMods, "user://mods", candidates);
            // The workshop writes to the public Documents folder, not a possibly unrelated
            // Godot user-dir fallback. Prefer that same canonical location as the launcher.
            var folderMods = ModPlatform.UserModsDirectory();
            if (!string.IsNullOrWhiteSpace(folderMods) &&
                (string.IsNullOrWhiteSpace(userMods) || userMods.StartsWith("user://", StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(Path.GetFullPath(folderMods), Path.GetFullPath(userMods), StringComparison.OrdinalIgnoreCase)))
                DiscoverRoot(folderMods, "user://mods", candidates);
        }

        selection ??= ModPlatform.ReadSelection();
        var orderedMods = candidates.Values
            .OrderBy(p => selection.Order(p.Manifest.Id, p.Manifest.LoadOrder))
            .ThenBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        _availableMods.AddRange(orderedMods);
        var enabledIds = ContentDependencies.Resolve(_availableMods.Select(p => new ContentRequirement(
            p.Manifest.Id, selection.IsEnabled(p.Manifest.Id, false, p.Manifest.Enabled),
            p.Manifest.Dependencies ?? new())), out var dependencyErrors);
        foreach (var error in dependencyErrors) Write($"Mod {error.Key} skipped: {error.Value}");

        if (includeDlc)
        {
            // Base game, then content packs, then mods. Later wins, so a mod may overrule a
            // pack the player has paid for - which is the right way round: they own both.
            Dlc.Discover();
            foreach (var pack in Dlc.Active)
                _packages.Add(new ModPackage
                {
                    Manifest = new ModManifest
                    {
                        Id = pack.Manifest.Id,
                        Name = pack.Manifest.Name,
                        Version = pack.Manifest.Version
                    },
                    PhysicalRoot = pack.PhysicalRoot,
                    VirtualRoot = pack.VirtualRoot,
                    IsContentPack = true
                });
            foreach (var pack in Dlc.Packages)
                Write(pack.Active
                    ? $"Content pack {pack.Manifest.Name} {pack.Manifest.Version} active."
                    : $"Content pack {pack.Manifest.Name} present but not active" +
                      (pack.Errors.Count > 0 ? $" ({pack.Errors[0]})" : " (not owned)") + ".");
        }

        _packages.AddRange(enabledIds.Select(id => candidates[id]));
        _frontendActions.ResetDeclarations(_packages.Where(p => !p.IsContentPack)
            .Select(p => (p.Manifest.Id, (IEnumerable<ModFrontendActionDeclaration>)p.Manifest.FrontendActions)),
            (id, error) => _packages.FirstOrDefault(p => !p.IsContentPack && string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase))?.Errors.Add(error));
        Write($"Discovery complete: {ContentPackCount} content pack(s), {EnabledCount} enabled mod(s).");
    }

    /// <summary>
    /// Reset and layer enabled packages' localisation, interface skins/layouts, sprites, sound,
    /// music, media libraries and fonts without touching any campaign simulation data or history.
    /// Called both on the main menu and at the start of <see cref="ApplyCampaignData"/>.
    /// </summary>
    public void ApplyPresentation()
    {
        ConfigureOverlayPolicy();
        GameDataLoader.DrainWarnings();
        Loc.ResetCatalog();
        InterfaceSkin.ResetCatalog();
        SpriteCatalog.Load();
        SoundCatalog.Load();
        MusicCatalog.Load();
        MediaLibraryCatalog.Load();
        FontCatalog.Load();
        foreach (var package in _packages)
        {
            ApplyLocalization(package);
            ApplySkin(package);
            ApplySprites(package);
            ApplyAudio(package);
        }
        foreach (var warning in GameDataLoader.DrainWarnings()) Write(warning);
    }

    private void ConfigureOverlayPolicy()
    {
        var specs = _packages
            .Where(p => p.Manifest is not null && (p.Manifest.TotalConversion ||
                (p.Manifest.ReplacePaths is { Count: > 0 }) ||
                (p.Manifest.ExcludePaths is { Count: > 0 })))
            .Select(p => new ModLayerSpec(
                p.Manifest.Id,
                p.Manifest.TotalConversion,
                p.Manifest.ReplacePaths,
                p.Manifest.ExcludePaths))
            .ToArray();

        if (specs.Length == 0)
            ModOverlayPolicy.Reset();
        else
            foreach (var error in ModOverlayPolicy.Configure(specs)) Write(error);

        var patchSources = _packages
            .Where(p => p.Manifest is not null && !string.IsNullOrWhiteSpace(p.Manifest.Id))
            .Select(p => new ModPatchSource(p.Manifest.Id, p.PhysicalRoot))
            .ToArray();
        if (patchSources.Length == 0)
        {
            ModJsonPatcher.Reset();
            return;
        }
        foreach (var diag in ModJsonPatcher.Configure(patchSources))
            Write($"[{diag.Code}] {diag.File} ({diag.Path}): {diag.Message}");
    }

    private static string ReadPatchedModJson(ModPackage package, string category, string path)
    {
        var raw = StructuredData.ReadJson(path);
        return ModJsonPatcher.IsActive
            ? ModJsonPatcher.TransformModJson(category, Path.GetFileName(path), raw, package?.Manifest?.Id ?? "", package?.Errors)
            : raw;
    }

    public void ApplyCampaignData(Campaign campaign)
    {
        // Configure scoped VFS replace_paths/exclude_paths BEFORE resetting base catalogues so
        // total-conversion mods cleanly suppress replaced 2024 categories, and returning to
        // base 2024 resets ModOverlayPolicy cleanly in the same process.
        ConfigureOverlayPolicy();
        // МОДДЕРАМ: это загрузка содержимого для кампании, не её ежедневный tick.
        // Каталоги сбрасываются перед оверлеями; перенос ResetCatalog внутрь OnFrame
        // обнулит чужие переопределения и заново распарсит данные на каждом кадре.
        // Reset mutable catalogues before every campaign so disabling a mod cannot
        // leak its map overrides into the next session in the same process.
        //
        // The map's own tables come first, because everything below reads their indices and
        // because they were the one set of overridable map data missing from this list: a mod
        // that changed a country row, a region row or the map's colour families took effect on the
        // first campaign of a session and was silently ignored on the second, the rows having been
        // read once and kept. The catalogue itself reparses its file, so the cost is one read of
        // two hundred kilobytes per campaign.
        WorldMapTables.ResetCatalog();
        TerritorialTopology.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        LandCrossingCatalog.ResetCatalog();
        LakeRegions.ResetCatalog();
        MapRasterCatalog.ResetCatalog();
        TerrainCatalog.ResetCatalog();
        // The cities and the townspeople table are map data a mod lays its own files over
        // (ApplyMapData), so they start again from the base game's like the rest.
        CityCatalog.Reset();
        StatePartition.ResetTownspeople();
        ApplyPresentation();
        ProvinceOwnership.ResetCatalog();
        BuildingCatalog.ResetCatalog();
        CommodityCatalog.ResetCatalog();
        ProductionChains.ResetCatalog();
        BudgetCore.ResetCatalog();
        GovernmentFocusCatalog.ResetCatalog();
        TradePolicyCatalog.ResetCatalog();
        foreach (var (_, reset, _) in Tunables) reset();
        ScriptLibrary.ResetCatalog();
        DecisionCatalog.ResetCatalog();
        PromiseCatalog.ResetCatalog();
        GoalCatalog.ResetCatalog();
        EventCatalog.ResetCatalog();
        ModifierCatalog.ResetCatalog();
        OpinionCatalog.ResetCatalog();
        DemographyCatalog.ResetCatalog();
        AiStrategyCatalog.ResetCatalog();
        MilitaryBootstrap.ResetPersonnel();
        var reseed = false;
        foreach (var package in _packages)
        {
            // Скриптовые эффекты раскрываются при Parse, не при каждом Apply.
            // Поздняя замена библиотеки не переписывает уже разобранный эффект старого
            // события: при необходимости переопределите и запись события/решения.
            ApplyScriptLibraries(package);
            ApplyDefines(package, campaign.Defines);
            ApplyManifestScenarioRules(package, campaign);
            ApplyUnits(package);
            ApplyGoods(package);
            ApplyBuildings(package);
            ApplyProduction(package);
            ApplyBudget(package);
            ApplyPolicies(package);
            ApplyDecisions(package);
            ApplyPromises(package);
            ApplyGoals(package);
            ApplyEvents(package);
            ApplyModifiers(package);
            ApplyOpinionModifiers(package);
            // A package may declare a belief and refer to it in a foundation in the same overlay.
            reseed |= ApplyDemography(package);
            ApplyTunables(package);
            ApplyAiStrategies(package);
            ApplyMapData(package);
            ApplyCountries(package, campaign);
            ApplyProvinceOwnership(package);
        }

        // A mod that rewrote who lives where has to be believed, and the population was
        // already dealt out before any mod was read. Seeding again is the only honest
        // answer; it is skipped entirely when nothing touched the census, which is almost
        // always, so the cost falls on the mods that asked for it.
        if (reseed) EconomySystem.Seed(campaign);

        // History last, and after the reseed: `history/states` corrects particular regions,
        // and a correction applied before the deal is a correction dealt away.
        foreach (var package in _packages) ApplyHistory(package, campaign);
        foreach (var warning in GameDataLoader.DrainWarnings()) Write(warning);
    }

    private void ApplyBuildings(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "buildings"))
        try
        {
            using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/buildings", path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    package.Errors.Add($"Building '{entry.Name}' in {Path.GetFileName(path)} is not an object");
                    continue;
                }
                var value = entry.Value;
                if (GameDataLoader.IsRemoved(value, $"common/buildings:{entry.Name}"))
                {
                    BuildingCatalog.Remove(entry.Name);
                    continue;
                }
                string Text(string key) => value.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                float? Number(string key) => value.TryGetProperty(key, out var e) && e.TryGetSingle(out var n) ? n : null;
                int? Integer(string key) => value.TryGetProperty(key, out var e) && e.TryGetInt32(out var n) ? n : null;
                // Declare rather than override: a name this catalog has not seen is a new
                // building, provided it says what it costs and how long it takes. Without
                // those it is a typo, and refusing typos is most of what a catalog is for.
                if (!BuildingCatalog.Declare(entry.Name, Text("name"), Number("cost"), Integer("days"),
                        Number("capacity_gain"), Text("description"), jobs: Number("jobs")))
                    package.Errors.Add($"Building '{entry.Name}' in {Path.GetFileName(path)} is neither a " +
                                       "valid change to an existing one nor a complete new one " +
                                       "(a new building needs name, cost and days)");
            }
        }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    /// <summary>
    /// A mod's own named triggers, effects and scores. They load before everything else
    /// the mod brings, because its decisions and events refer to them by name while they
    /// are being read.
    /// </summary>
    private void ApplyScriptLibraries(ModPackage package)
    {
        try { ScriptLibrary.LoadDirectories(package.PhysicalRoot, package.Errors); }
        catch (Exception exception) { package.Errors.Add($"scripted libraries: {exception.Message}"); }
    }

    private void ApplyDecisions(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "decisions"))
        try
        {
            using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/decisions", path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                // The same reader the base game's own folder goes through. It used to be
                // a second, narrower copy here that required a compiled `kind` - so a mod
                // could rename the five decisions the game shipped and write no others.
                DecisionCatalog.Apply(entry.Name, entry.Value, package.Errors);
            }
        }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyEvents(ModPackage package)
    {
        foreach (var path in DataFiles(package, "events"))
        try { EventCatalog.Load(ReadPatchedModJson(package, "events", path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyPromises(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "promises"))
        try
        {
            using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/promises", path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in document.RootElement.EnumerateObject())
                PromiseCatalog.Apply(entry.Name, entry.Value, package.Errors);
        }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyGoals(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "goals"))
        try
        {
            using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/goals", path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in document.RootElement.EnumerateObject())
                GoalCatalog.Apply(entry.Name, entry.Value, package.Errors);
        }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    /// <summary>
    /// The catalogues that are a folder of numbers rather than a folder of things.
    ///
    /// Stability, militia, secession, the settings screen and how flags are bound all
    /// happen to have the same shape: a folder under common/, a Merge that takes one
    /// document, and a Reset that reads the base game's copy. Each had a hand-written
    /// loader, or - for the four newest - no loader at all, which meant a mod could put a
    /// file in common/stability and the game would silently read only its own.
    ///
    /// One table instead. Adding the next tunable catalogue is one line here, which is the
    /// whole point: the cost of making something moddable should not be three methods that
    /// differ only in a string.
    /// </summary>
    private static readonly (string Folder, Action Reset, Action<JsonElement, List<string>> Merge)[] Tunables =
    {
        ("programme", () => ProductionProgramme.ResetCatalog(), ProductionProgramme.Merge),
        ("stability", () => StabilityCore.ResetCatalog(), StabilityCore.Merge),
        ("movements", () => MovementCatalog.ResetCatalog(), MovementCatalog.Merge),
        ("militia", () => MilitiaCore.ResetCatalog(), MilitiaCore.Merge),
        ("secession", () => SecessionCore.ResetCatalog(), SecessionCore.Merge),
        ("settings", () => SettingsCatalog.ResetCatalog(), SettingsCatalog.Merge),
        ("flags", () => FlagCatalog.ResetCatalog(), FlagCatalog.Merge),
        // Flags written as data (background, stripes, emblems) rather than painted - the
        // in-game flag editor writes these, and a modder can type one by hand.
        ("flag_designs", () => FlagDesignCatalog.ResetCatalog(), FlagDesignCatalog.Merge),
        ("editor", () => ScenarioInspectorCatalog.ResetCatalog(), ScenarioInspectorCatalog.Merge),
        ("governance", () => GovernanceEvents.ResetCatalog(), GovernanceEvents.Merge),
        ("formable_nations", () => FormableNations.ResetCatalog(), FormableNations.Merge),
        ("social_memory", () => SocialIncidentCatalog.ResetCatalog(), SocialIncidentCatalog.Merge),
        ("public_outreach", () => OutreachCatalog.ResetCatalog(), OutreachCatalog.Merge),
        ("alliance_programs", () => AllianceProgramCatalog.ResetCatalog(), AllianceProgramCatalog.Merge),
        ("grand_projects", () => GrandProjectCatalog.ResetCatalog(), GrandProjectCatalog.Merge),
        // Kinds of incoming offer a player can leave a standing answer for (DealAnswer).
        ("deals", () => { DealAnswerKinds.ResetCatalog(); TradeTermsCatalog.ResetCatalog(); },
            (root, errors) => { DealAnswerKinds.Merge(root, errors); TradeTermsCatalog.MergePresets(root, errors); }),
        // What a deal term may say: the trade agreement's lengths, steps and exit (TradeTermsCatalog).
        ("deal_terms", () => TradeTermsCatalog.ResetCatalog(), TradeTermsCatalog.MergeTerms),
        ("laws", () => PoliticalPolicyCatalog.ResetCatalog(), PoliticalPolicyCatalog.MergeLaws),
        ("agendas", () => { }, PoliticalPolicyCatalog.MergeAgendas),
        ("national_courses", () => NationalCourseCatalog.ResetCatalog(), NationalCourseCatalog.Merge),
        ("formable_ai", () => FormableProclamationAi.ResetCatalog(), FormableProclamationAi.Merge),
        ("regional_demography", () => RegionalDemography.ResetCatalog(), RegionalDemography.Merge),
        ("news", () => NewsFeed.ResetCatalog(), NewsFeed.Merge),
        ("handbook", () => HandbookCatalog.ResetCatalog(), HandbookCatalog.Merge),
        ("escalation", () => IncidentCatalog.ResetCatalog(), (root, errors) => IncidentCatalog.Merge(root.GetRawText(), errors)),
        ("factions", () => Factions.ResetCatalog(), Factions.Merge),
        ("coups", () => Coups.ResetCatalog(), Coups.Merge),
        ("addresses", () => AddressToNation.ResetCatalog(), AddressToNation.Merge),
        ("covert_operations", () => CovertOperations.ResetCatalog(), CovertOperations.Merge),
        ("rulers", () => Rulers.ResetCatalog(), Rulers.MergeCommon),
        ("governments", () => GovernmentCatalog.ResetCatalog(), GovernmentCatalog.Merge),
        ("ideologies", () => IdeologyCatalog.ResetCatalog(), IdeologyCatalog.Merge),
        ("technologies", () => TechnologyCatalog.ResetCatalog(), TechnologyCatalog.Merge),
        ("tactical_doctrines", () => TacticalDoctrineCatalog.ResetCatalog(), TacticalDoctrineCatalog.Merge),
        ("strategic_doctrines", () => StrategicDoctrineCatalog.ResetCatalog(), StrategicDoctrineCatalog.Merge),
        ("support", () => SupportTypeCatalog.ResetCatalog(), SupportTypeCatalog.Merge),
        ("subjects", () => SubjectCatalog.ResetCatalog(), SubjectCatalog.Merge),
        ("organizations", () => InternationalOrganizations.ResetCatalog(), InternationalOrganizations.Merge),
        ("peace_terms", () => PeaceTermCatalog.ResetCatalog(), PeaceTermCatalog.Merge),
        ("trade", () => TradeDesk.ResetCatalog(), TradeDesk.Merge),
        ("inflation", () => { InflationCore.ResetCatalog(); CentralBank.ResetCatalog(); },
            (root, errors) => { InflationCore.Merge(root, errors); CentralBank.Merge(root, errors); }),
        ("sea_lanes", () => SeaPassageCatalog.ResetCatalog(), SeaPassageCatalog.Merge),
        ("country_merges", () => CountryMerges.ResetCatalog(), (root, _) => CountryMerges.Merge(root)),
        ("building_rules", () => BuildingRules.ResetCatalog(), BuildingRules.Merge),
        ("character_names", () => PersonNameCatalog.ResetCatalog(), PersonNameCatalog.Merge),
        ("dynasties", () => DynastyCatalog.ResetCatalog(), DynastyCatalog.Merge),
        ("generals", () => Generals.ResetCatalog(), Generals.Merge),
        ("elections", () => ElectoralPolitics.ResetCatalog(), ElectoralPolitics.Merge),
        ("event_pictures", () => EventPictures.ResetCatalog(), EventPictures.Merge),
        ("naming", () => LandAtlas.ResetCatalog(), LandAtlas.Merge),
        ("lands", () => { }, LandAtlas.Merge),
        ("world_layers", () => WorldLayers.ResetCatalog(), WorldLayers.Merge),
        ("economic_shocks", () => TreasuryDynamics.ResetCatalog(), TreasuryDynamics.Merge),
        ("deterrence", () => Deterrence.ResetCatalog(), Deterrence.Merge),
        ("diagnostics", () => DiagnosticLogs.ResetCatalog(), DiagnosticLogs.Merge),
        ("performance", () => QualityProfiles.ResetCatalog(), QualityProfiles.Merge)
    };

    /// <summary>One mod's copy of each of those folders, layered over what is loaded.</summary>
    private void ApplyTunables(ModPackage package)
    {
        foreach (var (folder, _, merge) in Tunables)
        foreach (var path in DataFiles(package, "common", folder))
        try
        {
            using var document = JsonDocument.Parse(ReadPatchedModJson(package, $"common/{folder}", path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            merge(document.RootElement, package.Errors);
        }
        catch (Exception exception) { package.Errors.Add($"{folder}/{Path.GetFileName(path)}: {exception.Message}"); }
    }

    /// <summary>A mod's own reasons for one country to think ill of another.</summary>
    private void ApplyOpinionModifiers(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "opinion_modifiers"))
        try
        {
            using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/opinion_modifiers", path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            OpinionCatalog.Merge(document.RootElement, package.Errors);
        }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    /// <summary>
    /// A mod's own peoples, faiths, trades and censuses.
    ///
    /// Four folders in one pass because they are one subject: adding a people usually means
    /// putting them somewhere, and a mod that splits a culture in two will want to say which
    /// regions each half lives in. Each is layered like everything else, so a mod that only
    /// wants to correct one country's census writes that one country.
    /// </summary>
    private bool ApplyDemography(ModPackage package)
    {
        var read = false;
        foreach (var path in DataFiles(package, "common", "religions"))
        try { DemographyCatalog.LoadReligions(StructuredData.ReadJson(path), package.Errors); read = true; }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in DataFiles(package, "common", "cultures"))
        try { DemographyCatalog.LoadCultures(StructuredData.ReadJson(path), package.Errors); read = true; }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in DataFiles(package, "common", "professions"))
        try { DemographyCatalog.LoadProfessions(StructuredData.ReadJson(path), package.Errors); read = true; }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in DataFiles(package, "history", "pops"))
        try { DemographyCatalog.LoadPops(StructuredData.ReadJson(path), package.Errors); read = true; }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in DataFiles(package, "history", "faiths"))
        try { DemographyCatalog.LoadFaithSettings(StructuredData.ReadJson(path), package.Errors); read = true; }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        return read;
    }

    private void ApplyModifiers(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "modifiers"))
        try { ModifierCatalog.Load(ReadPatchedModJson(package, "common/modifiers", path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyAiStrategies(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "ai_strategy"))
        try { AiStrategyCatalog.Load(ReadPatchedModJson(package, "common/ai_strategy", path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyMapData(ModPackage package)
    {
        TerrainCatalog.ApplyDirectory(package.PhysicalRoot, package.Errors);
        var mapRoot = Path.Combine(package.PhysicalRoot, "map_data");
        MapRasterCatalog.ApplyDirectory(mapRoot, package.Errors);
        foreach (var path in GameDataLoader.TopLevelDataFiles(mapRoot, "map_data", package.Errors,
                     "cities", "townspeople", "state_regions", "crossings", "lakes", "hydrography", "water_settings"))
        try
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
            if (folder.Equals("water_settings", StringComparison.OrdinalIgnoreCase)) continue;
            if (folder.Equals("map_data", StringComparison.OrdinalIgnoreCase) &&
                (Path.GetFileNameWithoutExtension(path).Equals("terrain_settings", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileNameWithoutExtension(path).Equals("water_settings", StringComparison.OrdinalIgnoreCase)))
                continue;
            var json = StructuredData.ReadJson(path);
            if (folder.Equals("cities", StringComparison.OrdinalIgnoreCase)) CityCatalog.Merge(json, package.Errors);
            else if (folder.Equals("townspeople", StringComparison.OrdinalIgnoreCase)) StatePartition.MergeTownspeople(json, package.Errors);
            else if (folder.Equals("crossings", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
                LandCrossingCatalog.Merge(doc.RootElement, package.Errors);
            }
            else if (folder.Equals("lakes", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
                LakeRegions.Merge(doc.RootElement, package.Errors);
            }
            else if (folder.Equals("hydrography", StringComparison.OrdinalIgnoreCase) || HydrologyCatalog.LooksLikeGeoJson(json))
                HydrologyCatalog.LoadGeoJson(json, package.Errors);
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
    /// A mod's own artwork. The declarations are layered like every other category, and a
    /// mod's relative texture paths resolve inside the mod's own folder - so it ships the
    /// picture beside the line that names it, and neither has to know where the player
    /// installed it.
    /// </summary>
    private void ApplySprites(ModPackage package)
    {
        try
        {
            SpriteCatalog.LoadTree(Path.Combine(package.PhysicalRoot, "gfx"),
                $"{package.VirtualRoot}/gfx", package.Errors);
        }
        catch (Exception exception) { package.Errors.Add($"gfx: {exception.Message}"); }
    }

    /// <summary>
    /// A mod's own sound and its own soundtrack, layered like everything else. Music is
    /// the one category where the base game declares nothing at all, so a mod's list is
    /// usually the only list there is.
    /// </summary>
    private void ApplyAudio(ModPackage package)
    {
        try
        {
            // Each rooted at its own folder, so a path a mod writes means the same thing a
            // path the base game writes means - which is how gfx/ has always worked and is
            // why a mod could replace a picture but not a sound.
            SoundCatalog.LoadTree(Path.Combine(package.PhysicalRoot, "sound"),
                $"{package.VirtualRoot}/sound", package.Errors);
            MusicCatalog.LoadTree(Path.Combine(package.PhysicalRoot, "music"),
                $"{package.VirtualRoot}/music", package.Errors);
            MediaLibraryCatalog.LoadTree(Path.Combine(package.PhysicalRoot, "presentation"),
                package.VirtualRoot, package.Errors);
            FontCatalog.LoadTree(Path.Combine(package.PhysicalRoot, "fonts"),
                $"{package.VirtualRoot}/fonts", package.Errors);
        }
        catch (Exception exception) { package.Errors.Add($"sound: {exception.Message}"); }
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
        foreach (var path in DataFiles(package, "common", "states"))
        try
        {
            using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/states", path), GameDataLoader.ForgivingJson);
            StateDefinitions.Apply(campaign, document.RootElement);
        }
        catch (Exception exception) { package.Errors.Add($"states/{Path.GetFileName(path)}: {exception.Message}"); }

        foreach (var path in BookmarkDataFiles(package, "countries", campaign))
        try { HistoryDataLoader.ApplyCountries(campaign, ReadPatchedModJson(package, "history/countries", path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in BookmarkDataFiles(package, "states", campaign))
        try { HistoryDataLoader.ApplyStates(campaign, ReadPatchedModJson(package, "history/states", path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in BookmarkDataFiles(package, "diplomacy", campaign))
        try { HistoryDataLoader.ApplyDiplomacy(campaign, ReadPatchedModJson(package, "history/diplomacy", path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"diplomacy/{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in BookmarkDataFiles(package, "conflicts", campaign))
        try { ConflictHistory.Apply(campaign, ReadPatchedModJson(package, "history/conflicts", path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"conflicts/{Path.GetFileName(path)}: {exception.Message}"); }

        var mergedFactions = false;
        foreach (var path in BookmarkDataFiles(package, "factions", campaign))
        try
        {
            using var document = JsonDocument.Parse(ReadPatchedModJson(package, "history/factions", path), GameDataLoader.ForgivingJson);
            Factions.Merge(document.RootElement, package.Errors);
            mergedFactions = true;
        }
        catch (Exception exception) { package.Errors.Add($"factions/{Path.GetFileName(path)}: {exception.Message}"); }
        if (mergedFactions) Factions.Found(campaign);

        // The order of battle: how many soldiers a country keeps, and the ships it starts
        // the bookmark with. Both were readable only from the base game's own folder, so a
        // total conversion could redraw the whole map and still be fought by the armies and
        // navies of 2024.
        var orderOfBattle = Path.Combine(package.PhysicalRoot, "history", "units");
        try { MilitaryBootstrap.MergePersonnel(orderOfBattle, package.Errors); }
        catch (Exception exception) { package.Errors.Add($"history/units: {exception.Message}"); }
        try { FleetHistory.ApplyDirectory(campaign, orderOfBattle, package.Errors); }
        catch (Exception exception) { package.Errors.Add($"history/units: {exception.Message}"); }
    }

    private void ApplyUnits(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "units"))
        {
            try
            {
                using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/units", path), new JsonDocumentOptions
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

    /// <summary>Drop compiled mods, scripts, and workers before the next campaign. Safe to call when nothing was started.</summary>
    public void StopCode()
    {
        _frontendActions.StopRuntime();
        // A managed mod may own a local bridge or worker; release it before unloading its assembly.
        foreach (var plugin in _csharp)
        {
            plugin.Context.ReleaseSimulationPasses();
            plugin.Context.FrontendActions?.Dispose();
            if (plugin.Instance is IDisposable disposable) Try(plugin.Context.ModId, disposable.Dispose);
        }
        _csharp.Clear();
        foreach (var context in _managedContexts) context.Unload();
        _managedContexts.Clear();
        _workers.Clear();
        _scriptRuntime?.Stop();
        _api = null;
    }

    /// <summary>
    /// Construct code mods after <see cref="ApplyCampaignData"/>. Each package may load all four kinds.
    /// A throw inside one package is recorded on that package and does not cancel the next package.
    /// </summary>
    public void StartCode(object api, Campaign campaign)
    {
        StopCode();
        _api = api;
        ModPlatform.BindApi(_api, campaign);
        _frontendActions.BeginRuntime();
        foreach (var package in _packages)
        {
            LoadCsharp(package, campaign);
            LoadWorker(package, campaign);
        }
        _scriptRuntime ??= ModPlatform.CreateScriptRuntime();
        _scriptRuntime?.Start(_packages, _api, campaign, Try, Write);
    }

    public Func<byte[]> CaptureCrossoverSurface { get; set; }
    public Func<byte[]> CaptureCrossoverHydrology { get; set; }
    public Action<CrossoverVoxel[]> PresentCrossoverVoxels { get; set; }
    public Action<CrossoverWork[]> PresentCrossoverWorks { get; set; }
    public Action<bool> SetCrossoverPresentation { get; set; }

    /// <summary>UI-thread heartbeat for compiled mods only. Must not be used to re-read data files. A frame is not a campaign day.</summary>
    public void OnFrame(Campaign campaign, double delta)
    {
        foreach (var plugin in _csharp)
        {
            plugin.Context.Campaign = campaign;
            plugin.Context.CaptureSurface = CaptureCrossoverSurface;
            plugin.Context.CaptureHydrology = CaptureCrossoverHydrology;
            plugin.Context.PresentVoxels = PresentCrossoverVoxels;
            plugin.Context.PresentWorks = PresentCrossoverWorks;
            Try(plugin.Context.ModId, () => plugin.Instance.OnFrame(plugin.Context, delta));
        }
    }

    /// <summary>
    /// One campaign day, after the simulation has advanced it. Workers first, then compiled mods, GDScript, Luau.
    /// A Luau hook that fails is removed so it cannot throw again every day. The others stay loaded and log the error.
    /// </summary>
    public void OnDayAdvanced(Campaign campaign)
    {
        foreach (var worker in _workers) worker.Hook(campaign, "day_advanced");
        foreach (var plugin in _csharp)
        {
            plugin.Context.Campaign = campaign;
            Try(packageId: plugin.Context.ModId, () => plugin.Instance.OnDayAdvanced(plugin.Context));
        }

        _scriptRuntime?.OnDayAdvanced(Try);
    }

    private void DiscoverRoot(string physicalRoot, string virtualRoot, Dictionary<string, ModPackage> candidates)
    {
        if (string.IsNullOrEmpty(physicalRoot) || !Directory.Exists(physicalRoot)) return;

        foreach (var directory in Directory.GetDirectories(physicalRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(directory).StartsWith("_", StringComparison.Ordinal) || Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
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
                var scripts = Path.Combine(directory, "Scripts");
                if (Directory.Exists(scripts) && Directory.EnumerateFiles(scripts, "*.cs", new EnumerationOptions
                    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 16 }).Any())
                    Write($"[{manifest.Id}] Scripts/*.cs are source files, not host replacements. Compile an IGlobalityMod assembly under corecode/ and declare corecode_assembly. Use game data IDs or feature-system gates to replace existing behavior.");
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

    /// <summary>
    /// Overlay <c>common/defines</c> onto the live <see cref="Defines"/> object. A key must already
    /// exist as a public property. Unknown keys are package errors, not new mechanics.
    /// Feature tables merge by key. A file that omits a key leaves the compiled value, which is
    /// why a balance mod lists three numbers and does not fork the folder.
    /// Later files in name order, and later packages, win.
    /// </summary>
    private void ApplyDefines(ModPackage package, Defines defines)
    {
        foreach (var path in DataFiles(package, "common", "defines"))
        {
            try
            {
                using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/defines", path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    var property = typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(candidate => Normalize(candidate.Name) == Normalize(entry.Name));
                    if (property is null || !property.CanWrite || !TryConvert(entry.Value, property.PropertyType, out var value))
                    {
                        package.Errors.Add($"Unknown or invalid define '{entry.Name}' in {Path.GetFileName(path)}");
                        continue;
                    }
                    if (value is Dictionary<string, bool> flags) MechanicFeatures.Merge(defines.Features, flags);
                    else if (value is Dictionary<string, string> rules)
                    {
                        if (property.Name == nameof(Defines.FeatureUnlocks)) MechanicFeatures.Merge(defines.FeatureUnlocks, rules);
                        else if (property.GetValue(defines) is Dictionary<string, string> bindings) MechanicFeatures.MergeBindings(bindings, rules);
                    }
                    else property.SetValue(defines, value);
                }
            }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Overlay <c>common/countries</c> by tag onto countries that already exist.
    /// This does not found a nation: an unknown tag is an error. New countries come from history
    /// and from the systems that already create them (secession, formables), not from a parallel list.
    /// The fields here are the same aggregates <see cref="ModScalarAccess"/> can write later.
    /// </summary>
    private void ApplyCountries(ModPackage package, Campaign campaign)
    {
        foreach (var path in DataFiles(package, "common", "countries"))
        {
            try
            {
                using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/countries", path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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
        foreach (var path in DataFiles(package, "common", "goods"))
        {
            try
            {
                using var document = JsonDocument.Parse(ReadPatchedModJson(package, "common/goods", path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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
                    else { CommodityCatalog.ReadLeverage(good.Name, value); CommodityCatalog.ReadWorldPrice(good.Name, value); }
                }
            }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// What this mod says things are made of.
    ///
    /// Merged rather than replaced, recipe by recipe: a mod that wants electronics to need
    /// uranium names `electronics_manufacture` and inherits the other thirty-nine chains,
    /// and does not go stale when the base game retunes one of them next patch.
    /// </summary>
    private void ApplyProduction(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "production"))
        {
            try { ProductionChains.Merge(ReadPatchedModJson(package, "common/production", path), package.Errors); }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// What this mod says a government may raise and must spend.
    ///
    /// Merged line by line, like the production chains: a mod that thinks income tax should
    /// yield less names `income_tax` and inherits the other sixteen lines, and a mod that
    /// wants a tax the game never had gives it an id nothing else uses and gets a slider
    /// for it without touching a line of code.
    /// </summary>
    /// <summary>
    /// What this mod says a cabinet may pursue and how a country may trade.
    ///
    /// Both catalogues keep enum values clear for entries the game has never heard of, so a
    /// mod may retune the five cabinets and three stances that ship, or declare its own.
    /// </summary>
    private void ApplyPolicies(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "government_focus"))
        {
            try { GovernmentFocusCatalog.Merge(ReadPatchedModJson(package, "common/government_focus", path), package.Errors); }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
        foreach (var path in DataFiles(package, "common", "trade_policies"))
        {
            try { TradePolicyCatalog.Merge(ReadPatchedModJson(package, "common/trade_policies", path), package.Errors); }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
    }

    private void ApplyBudget(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "budget"))
        {
            try { BudgetCore.Merge(ReadPatchedModJson(package, "common/budget", path), package.Errors); }
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
            case "capitalcity" when value.ValueKind == JsonValueKind.String: CapitalCore.Author(nation, value.GetString()); return true;
            case "capitalname" when value.ValueKind == JsonValueKind.String: nation.CapitalName = value.GetString() ?? nation.CapitalName; return true;
            // The base game's common/countries reads these two (CountryDefinitions); a mod's copy
            // of the same folder reported them as unknown fields and dropped them, so a mod could
            // rename a capital but never move one.
            case "capitallatitude" when value.TryGetSingle(out var latitude) && float.IsFinite(latitude):
                nation.CapitalLatitude = Math.Clamp(latitude, -90f, 90f); return true;
            case "capitallongitude" when value.TryGetSingle(out var longitude) && float.IsFinite(longitude):
                nation.CapitalLongitude = Math.Clamp(longitude, -180f, 180f); return true;
            case "population" when value.TryGetInt64(out var population): nation.Population = Math.Max(0, population); return true;
            case "treasury" when value.TryGetSingle(out var treasury): nation.Treasury = treasury; return true;
            case "publicdebt" when value.TryGetSingle(out var debt) && float.IsFinite(debt): nation.PublicDebt = Math.Max(0, debt); return true;
            case "debtarrears" when value.TryGetSingle(out var arrears) && float.IsFinite(arrears): nation.DebtArrears = Math.Max(0, arrears); return true;
            case "paymentarrears" when value.TryGetSingle(out var primary) && float.IsFinite(primary): nation.PaymentArrears = Math.Max(0, primary); return true;
            case "creditconfidence" when value.TryGetSingle(out var confidence) && float.IsFinite(confidence): nation.CreditConfidence = Math.Clamp(confidence, 0, 1); return true;
            case "gdp" when value.TryGetSingle(out var gdp): nation.Gdp = Math.Max(0, gdp); return true;
            case "militaryspending" when value.TryGetSingle(out var spending): nation.MilitarySpending = Math.Clamp(spending, 0f, 1f); return true;
            case "taxrate" when value.TryGetSingle(out var tax): nation.TaxRate = Math.Clamp(tax, 0f, 1f); return true;
            case "stability" when value.TryGetSingle(out var stability): nation.Stability = Math.Clamp(stability, 0f, 1f); return true;
            case "warsupport" when value.TryGetSingle(out var support): nation.WarSupport = Math.Clamp(support, 0f, 1f); return true;
            case "manpower" when value.TryGetSingle(out var manpower): nation.ManpowerReserve = Math.Max(0f, manpower); return true;
            default: return false;
        }
    }

    /// <summary>
    /// Load a C# or F# assembly that implements <see cref="IGlobalityMod"/>.
    /// Each concrete type is constructed, then <c>OnLoad</c> and <c>OnCampaignCreated</c> run.
    /// A type that throws is disposed and not kept. The load context is collectible.
    /// Compiled mods are not refused in multiplayer; a mod that is not deterministic will desync.
    /// </summary>
    private void LoadCsharp(ModPackage package, Campaign campaign)
    {
        string relative;
        try { relative = ModCodePaths.AssemblyPath(package.Manifest.CorecodeAssembly, package.Manifest.CsharpAssembly); }
        catch (InvalidDataException e) { package.Errors.Add(e.Message); return; }
        if (relative.Length == 0) return;
        var path = SafePath(package, relative);
        if (path is null || !File.Exists(path))
        {
            package.Errors.Add($"Code assembly not found: {relative}");
            return;
        }

        Try(package.Manifest.Id, () =>
        {
            var loadContext = new ManagedModLoadContext(path);
            _managedContexts.Add(loadContext);
            var assembly = loadContext.LoadFromAssemblyPath(path);
            var types = assembly.GetTypes().Where(type => !type.IsAbstract && !type.IsInterface && !type.ContainsGenericParameters &&
                typeof(IGlobalityMod).IsAssignableFrom(type)).OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
            if (types.Length == 0)
                throw new InvalidDataException("Code assembly contains no concrete IGlobalityMod entry point. Copying or renaming a game class does not replace the host script.");
            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is not IGlobalityMod instance) continue;
                var context = new ModContext(package.Manifest.Id, message => Write($"[{package.Manifest.Id}] {message}"))
                {
                    Campaign = campaign,
                    FrontendActions = _frontendActions.RegisterFor(package.Manifest.Id),
                    SetCrossoverPresentation = SetCrossoverPresentation
                };
                var retained = false;
                try
                {
                    instance.OnLoad(context);
                    instance.OnCampaignCreated(context);
                    context.SealSimulationPasses();
                    _csharp.Add((instance, context));
                    retained = true;
                }
                finally
                {
                    if (!retained)
                    {
                        context.ReleaseSimulationPasses();
                        context.FrontendActions.Dispose();
                        if (instance is IDisposable disposable) Try(package.Manifest.Id, disposable.Dispose);
                    }
                }
            }
        });
    }

    /// <summary>
    /// Start one external worker (Go, Rust, or anything that speaks protocol 1 on stdin).
    /// The executable and the optional script must both sit inside the package.
    /// The worker is not a lockstep peer: the first multiplayer seat disables it.
    /// </summary>
    private void LoadWorker(ModPackage package, Campaign campaign)
    {
        if (string.IsNullOrWhiteSpace(package.Manifest.WorkerExecutable)) return;
        var path = SafePath(package, package.Manifest.WorkerExecutable);
        var script = string.IsNullOrEmpty(package.Manifest.WorkerScript) ? "" : SafePath(package, package.Manifest.WorkerScript);
        if (path is null || !File.Exists(path) || script is null || (script.Length > 0 && !File.Exists(script)))
        { package.Errors.Add("Worker executable/script must exist inside the mod directory."); return; }
        var worker = new WorkerModBackend(path, script.Length == 0 ? Array.Empty<string>() : new[] { script },
            package.Manifest.WorkerTimeoutMs, package.Manifest.WorkerIntervalDays,
            message => { Write($"[{package.Manifest.Id}] {message}"); if (message.StartsWith("Worker disabled:")) package.Errors.Add(message); });
        _workers.Add(worker);
        worker.Hook(campaign, "load");
        worker.Hook(campaign, "campaign_created");
    }

    /// <summary>
    /// Top-level JSON, YAML, and YML files inside one category folder.
    /// Files inside deeper subfolders are ignored with a warning on the package so modders see
    /// immediately why a nested folder did not load.
    /// </summary>
    private static IEnumerable<string> DataFiles(ModPackage package, params string[] parts)
    {
        var directory = parts.Aggregate(package.PhysicalRoot, Path.Combine);
        return GameDataLoader.TopLevelDataFiles(directory, string.Join("/", parts), package.Errors);
    }

    private static void ApplyManifestScenarioRules(ModPackage package, Campaign campaign)
    {
        var manifest = package?.Manifest;
        if (manifest is null || campaign is null) return;

        if (manifest.Calendar is not null && !manifest.Calendar.IsDefault)
        {
            var calErrors = manifest.Calendar.Validate();
            if (calErrors.Count == 0)
                campaign.Calendar = manifest.Calendar.Clone();
            else
                foreach (var err in calErrors) package.Errors.Add($"manifest.calendar: {err}");
        }
        else if (!string.IsNullOrWhiteSpace(manifest.EraName))
        {
            campaign.Calendar ??= new CalendarProfile();
            if (string.IsNullOrWhiteSpace(campaign.Calendar.EraName))
                campaign.Calendar.EraName = manifest.EraName;
        }

        if ((manifest.DisabledFeatures is { Count: > 0 }) || (manifest.EraFeatures is { Count: > 0 }))
            ScenarioRulesApplier.ApplyEraFeatures(campaign, manifest.DisabledFeatures, manifest.EraFeatures, package.Errors);

        if (!string.IsNullOrWhiteSpace(manifest.Theme) && string.IsNullOrWhiteSpace(campaign.SkinOverlay))
            campaign.SkinOverlay = manifest.Theme;

        if (manifest.TotalConversion || !string.IsNullOrWhiteSpace(manifest.EraName) ||
            !string.IsNullOrWhiteSpace(manifest.Theme) || !string.IsNullOrWhiteSpace(manifest.MusicPlaylist))
        {
            campaign.ScenarioMeta ??= new ScenarioMetadata();
            if (string.IsNullOrWhiteSpace(campaign.ScenarioMeta.EraName) && !string.IsNullOrWhiteSpace(manifest.EraName))
                campaign.ScenarioMeta.EraName = manifest.EraName;
            if (string.IsNullOrWhiteSpace(campaign.ScenarioMeta.Theme) && !string.IsNullOrWhiteSpace(manifest.Theme))
                campaign.ScenarioMeta.Theme = manifest.Theme;
            if (string.IsNullOrWhiteSpace(campaign.ScenarioMeta.MusicPlaylist) && !string.IsNullOrWhiteSpace(manifest.MusicPlaylist))
                campaign.ScenarioMeta.MusicPlaylist = manifest.MusicPlaylist;
            if (manifest.TotalConversion)
                campaign.ScenarioMeta.TotalConversion = true;
            if (ModOverlayPolicy.IsActive && !string.IsNullOrWhiteSpace(ModOverlayPolicy.ContentSignature))
                campaign.ScenarioMeta.ContentSignature = ModOverlayPolicy.ContentSignature;
        }
    }

    private static IEnumerable<string> BookmarkDataFiles(ModPackage package, string folder, Campaign campaign)
    {
        var directory = Path.Combine(package.PhysicalRoot, "history", folder);
        var scenarioId = string.IsNullOrWhiteSpace(campaign?.ScenarioId) ? "2024" : campaign.ScenarioId;
        var yearFolder = campaign is not null ? campaign.Date.Year.ToString(CultureInfo.InvariantCulture) : "2024";
        if (string.Equals(scenarioId, "2024", StringComparison.OrdinalIgnoreCase))
            return GameDataLoader.TopLevelDataFiles(directory, $"history/{folder}", package.Errors, "2024");
        return string.Equals(scenarioId, yearFolder, StringComparison.OrdinalIgnoreCase)
            ? GameDataLoader.TopLevelDataFiles(directory, $"history/{folder}", package.Errors, scenarioId)
            : GameDataLoader.TopLevelDataFiles(directory, $"history/{folder}", package.Errors, scenarioId, yearFolder);
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
            else if (type == typeof(Dictionary<string, bool>) || type == typeof(Dictionary<string, string>)) value = element.Deserialize(type);
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
        ModPlatform.Print($"[mods] {line}");
    }

    private static string Normalize(string value) =>
        (value ?? "").Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
