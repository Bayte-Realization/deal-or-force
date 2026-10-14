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
    [JsonPropertyName("frontend_actions")] public List<ModFrontendActionDeclaration> FrontendActions { get; set; } = new();
    [JsonPropertyName("gdscript_entry")] public string GdscriptEntry { get; set; } = "";
    [JsonPropertyName("engine_script_entry")] public string EngineScriptEntry { get; set; } = "";
    [JsonPropertyName("csharp_assembly")] public string CsharpAssembly { get; set; } = "";
    [JsonPropertyName("corecode_assembly")] public string CorecodeAssembly { get; set; } = "";
    [JsonPropertyName("worker_executable")] public string WorkerExecutable { get; set; } = "";
    [JsonPropertyName("worker_script")] public string WorkerScript { get; set; } = "";
    [JsonPropertyName("worker_timeout_ms")] public int WorkerTimeoutMs { get; set; } = 500;
    [JsonPropertyName("worker_interval_days")] public int WorkerIntervalDays { get; set; } = 7;
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
    private readonly ModFrontendActionCatalog _frontendActions = new();
    private readonly List<(IGlobalityMod Instance, ModContext Context)> _csharp = new();
    private readonly List<(GodotObject Instance, string ModId)> _gdscript = new();
    private readonly List<(RefCounted Instance, string ModId, Godot.Collections.Dictionary Api)> _engineScripts = new();
    private readonly List<ManagedModLoadContext> _managedContexts = new();
    private readonly List<WorkerModBackend> _workers = new();
    private readonly List<string> _log = new();
    private ModApi _api;

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
    /// <summary>Metadata only, from enabled dependency-resolved mods, never content packs.</summary>
    public IReadOnlyList<ModFrontendActionDescriptor> FrontendActions() => _frontendActions.Actions;
    /// <summary>Explicit UI action; handlers exist only after StartCode, not on discovery.</summary>
    public bool TryRunFrontendAction(string fullActionId, out string note) => _frontendActions.TryRun(fullActionId, out note);
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
            var mods = EnabledCount == 0
                ? Simulation.Loc.Get("no external mods · open user://mods or res://mods")
                : Simulation.Loc.Format("{0} mod(s) loaded", EnabledCount);
            return packs + mods +
                (ErrorCount > 0 ? " · " + Simulation.Loc.Format("{0} warning(s)", ErrorCount) : "");
        }
    }

    public void Discover()
    {
        _frontendActions.ResetDeclarations(Array.Empty<(string, IEnumerable<ModFrontendActionDeclaration>)>());
        _packages.Clear();
        _log.Clear();

        // Development mods ship beside the project. Installed/user mods with the same
        // id win, allowing local updates without touching the base installation.
        //
        // An installed game has more than one folder called mods - the one that travelled
        // with the data and the one beside the executable, which is the one a player can
        // actually find - and they arrive here nearest-first. Later wins by id, which is
        // the right way round: what the player put there outranks what shipped.
        var candidates = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GameDataLoader.InstallFolders("mods"))
            DiscoverRoot(root, "res://mods", candidates);
        DiscoverRoot(ProjectSettings.GlobalizePath("user://mods"), "user://mods", candidates);

        var selection = LauncherSelection.Read();
        var orderedMods = candidates.Values
            .OrderBy(p => selection.Order(p.Manifest.Id, p.Manifest.LoadOrder))
            .ThenBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var enabledIds = ContentDependencies.Resolve(orderedMods.Select(p => new ContentRequirement(
            p.Manifest.Id, selection.IsEnabled(p.Manifest.Id, false, p.Manifest.Enabled),
            p.Manifest.Dependencies ?? new())), out var dependencyErrors);
        foreach (var error in dependencyErrors) Write($"Mod {error.Key} skipped: {error.Value}");

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

        _packages.AddRange(enabledIds.Select(id => candidates[id]));
        _frontendActions.ResetDeclarations(_packages.Where(p => !p.IsContentPack)
            .Select(p => (p.Manifest.Id, (IEnumerable<ModFrontendActionDeclaration>)p.Manifest.FrontendActions)),
            (id, error) => _packages.FirstOrDefault(p => !p.IsContentPack && string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase))?.Errors.Add(error));
        Write($"Discovery complete: {ContentPackCount} content pack(s), {EnabledCount} enabled mod(s).");
    }

    public void ApplyCampaignData(Campaign campaign)
    {
        // Reset mutable catalogues before every campaign so disabling a mod cannot
        // leak its map overrides into the next session in the same process.
        TerritorialTopology.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        MapRasterCatalog.ResetCatalog();
        TerrainCatalog.ResetCatalog();
        Loc.ResetCatalog();
        InterfaceSkin.ResetCatalog();
        SpriteCatalog.Load();
        SoundCatalog.Load();
        MusicCatalog.Load();
        MediaLibraryCatalog.Load();
        FontCatalog.Load();
        SubjectCatalog.ResetCatalog();
        TechnologyCatalog.ResetCatalog();
        SeaPassageCatalog.ResetCatalog();
        ProvinceOwnership.ResetCatalog();
        BuildingCatalog.ResetCatalog();
        CommodityCatalog.ResetCatalog();
        ProductionChains.ResetCatalog();
        BudgetCore.ResetCatalog();
        GovernmentFocusCatalog.ResetCatalog();
        TradePolicyCatalog.ResetCatalog();
        TradeDesk.ResetCatalog();
        PeaceTermCatalog.ResetCatalog();
        InflationCore.ResetCatalog();
        CentralBank.ResetCatalog();
        foreach (var (_, reset, _) in Tunables) reset();
        GovernmentCatalog.ResetCatalog();
        IdeologyCatalog.ResetCatalog();
        ScriptLibrary.ResetCatalog();
        DecisionCatalog.ResetCatalog();
        EventCatalog.ResetCatalog();
        ModifierCatalog.ResetCatalog();
        InternationalOrganizations.ResetCatalog();
        OpinionCatalog.ResetCatalog();
        DemographyCatalog.ResetCatalog();
        AiStrategyCatalog.ResetCatalog();
        MilitaryBootstrap.ResetPersonnel();
        var reseed = false;
        foreach (var package in _packages)
        {
            ApplyScriptLibraries(package);
            ApplyDefines(package, campaign.Defines);
            ApplyUnits(package);
            ApplyGoods(package);
            ApplyBuildings(package);
            ApplyProduction(package);
            ApplyBudget(package);
            ApplyPolicies(package);
            ApplyDecisions(package);
            ApplyEvents(package);
            ApplyModifiers(package);
            ApplyOpinionModifiers(package);
            // A package may declare a belief and refer to it in a foundation in the same overlay.
            reseed |= ApplyDemography(package);
            ApplyTunables(package);
            ApplyAiStrategies(package);
            ApplyMapData(package);
            ApplyLocalization(package);
            ApplySkin(package);
            ApplySprites(package);
            ApplyAudio(package);
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
    }

    private void ApplyBuildings(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "buildings"))
        try
        {
            using var document = JsonDocument.Parse(StructuredData.ReadJson(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    package.Errors.Add($"Building '{entry.Name}' in {Path.GetFileName(path)} is not an object");
                    continue;
                }
                var value = entry.Value;
                string Text(string key) => value.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                float? Number(string key) => value.TryGetProperty(key, out var e) && e.TryGetSingle(out var n) ? n : null;
                int? Integer(string key) => value.TryGetProperty(key, out var e) && e.TryGetInt32(out var n) ? n : null;
                // Declare rather than override: a name this catalog has not seen is a new
                // building, provided it says what it costs and how long it takes. Without
                // those it is a typo, and refusing typos is most of what a catalog is for.
                if (!BuildingCatalog.Declare(entry.Name, Text("name"), Number("cost"), Integer("days"),
                        Number("gdp_gain"), Number("capacity_gain"), Text("description")))
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
            using var document = JsonDocument.Parse(StructuredData.ReadJson(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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
        try { EventCatalog.Load(StructuredData.ReadJson(path), package.Errors); }
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
        ("editor", () => ScenarioInspectorCatalog.ResetCatalog(), ScenarioInspectorCatalog.Merge),
        ("governance", () => GovernanceEvents.ResetCatalog(), GovernanceEvents.Merge),
        ("formable_nations", () => FormableNations.ResetCatalog(), FormableNations.Merge),
        ("social_memory", () => SocialIncidentCatalog.ResetCatalog(), SocialIncidentCatalog.Merge),
        ("public_outreach", () => OutreachCatalog.ResetCatalog(), OutreachCatalog.Merge),
        ("alliance_programs", () => AllianceProgramCatalog.ResetCatalog(), AllianceProgramCatalog.Merge),
        ("laws", () => PoliticalPolicyCatalog.ResetCatalog(), PoliticalPolicyCatalog.MergeLaws),
        ("agendas", () => { }, PoliticalPolicyCatalog.MergeAgendas),
        ("national_courses", () => NationalCourseCatalog.ResetCatalog(), NationalCourseCatalog.Merge),
        ("news", () => NewsFeed.ResetCatalog(), NewsFeed.Merge),
        ("handbook", () => HandbookCatalog.ResetCatalog(), HandbookCatalog.Merge),
        ("escalation", () => IncidentCatalog.ResetCatalog(), (root, errors) => IncidentCatalog.Merge(root.GetRawText(), errors)),
        ("factions", () => Factions.ResetCatalog(), Factions.Merge),
        ("coups", () => Coups.ResetCatalog(), Coups.Merge),
        ("addresses", () => AddressToNation.ResetCatalog(), AddressToNation.Merge)
    };

    /// <summary>One mod's copy of each of those folders, layered over what is loaded.</summary>
    private void ApplyTunables(ModPackage package)
    {
        foreach (var (folder, _, merge) in Tunables)
        foreach (var path in DataFiles(package, "common", folder))
        try
        {
            using var document = JsonDocument.Parse(StructuredData.ReadJson(path), new JsonDocumentOptions
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
            using var document = JsonDocument.Parse(StructuredData.ReadJson(path), new JsonDocumentOptions
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
        return read;
    }

    private void ApplyModifiers(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "modifiers"))
        try { ModifierCatalog.Load(StructuredData.ReadJson(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyAiStrategies(ModPackage package)
    {
        foreach (var path in DataFiles(package, "common", "ai_strategy"))
        try { AiStrategyCatalog.Load(StructuredData.ReadJson(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private void ApplyMapData(ModPackage package)
    {
        TerrainCatalog.ApplyDirectory(package.PhysicalRoot, package.Errors);
        MapRasterCatalog.ApplyDirectory(Path.Combine(package.PhysicalRoot, "map_data"), package.Errors);
        foreach (var path in DataFiles(package, "map_data"))
        try
        {
            var json = StructuredData.ReadJson(path);
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
        foreach (var path in DataFiles(package, "history", "countries"))
        try { HistoryDataLoader.ApplyCountries(campaign, StructuredData.ReadJson(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        foreach (var path in DataFiles(package, "history", "states"))
        try { HistoryDataLoader.ApplyStates(campaign, StructuredData.ReadJson(path), package.Errors); }
        catch (Exception exception) { package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }

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
                using var document = JsonDocument.Parse(StructuredData.ReadJson(path), new JsonDocumentOptions
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

    public void StopCode()
    {
        _frontendActions.StopRuntime();
        foreach (var plugin in _csharp)
        {
            plugin.Context.FrontendActions?.Dispose();
            if (plugin.Instance is IDisposable disposable) Try(plugin.Context.ModId, disposable.Dispose);
        }
        _csharp.Clear();
        foreach (var context in _managedContexts) context.Unload();
        _managedContexts.Clear();
        _workers.Clear();
        foreach (var (instance, _) in _gdscript) instance.Dispose();
        _gdscript.Clear();
        foreach (var plugin in _engineScripts)
        {
            plugin.Instance.Dispose();
            plugin.Api.Dispose();
        }
        _engineScripts.Clear();
        _api = null;
    }

    public void StartCode(ModApi api, Campaign campaign)
    {
        StopCode();
        _api = api;
        _api.Bind(campaign);
        _frontendActions.BeginRuntime();

        foreach (var package in _packages)
        {
            LoadCsharp(package, campaign);
            LoadGdscript(package, campaign);
            LoadEngineScript(package, campaign);
            LoadWorker(package, campaign);
        }
    }

    public void OnDayAdvanced(Campaign campaign)
    {
        foreach (var worker in _workers) worker.Hook(campaign, "day_advanced");
        foreach (var plugin in _csharp)
        {
            plugin.Context.Campaign = campaign;
            Try(packageId: plugin.Context.ModId, () => plugin.Instance.OnDayAdvanced(plugin.Context));
        }

        foreach (var plugin in _gdscript)
            if (plugin.Instance.HasMethod("on_day_advanced"))
                Try(plugin.ModId, () => plugin.Instance.Call("on_day_advanced", _api));

        for (var index = 0; index < _engineScripts.Count;)
        {
            var plugin = _engineScripts[index];
            var healthy = false;
            Try(plugin.ModId, () =>
            {
                EngineScriptMod.Invoke(plugin.Instance, "on_day_advanced", plugin.Api);
                healthy = true;
            });
            if (healthy) { index++; continue; }
            plugin.Instance.Dispose();
            plugin.Api.Dispose();
            _engineScripts.RemoveAt(index);
        }
    }

    private void DiscoverRoot(string physicalRoot, string virtualRoot, Dictionary<string, ModPackage> candidates)
    {
        if (string.IsNullOrEmpty(physicalRoot) || !Directory.Exists(physicalRoot)) return;

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
        foreach (var path in DataFiles(package, "common", "defines"))
        {
            try
            {
                using var document = JsonDocument.Parse(StructuredData.ReadJson(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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

    private void ApplyCountries(ModPackage package, Campaign campaign)
    {
        foreach (var path in DataFiles(package, "common", "countries"))
        {
            try
            {
                using var document = JsonDocument.Parse(StructuredData.ReadJson(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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
                using var document = JsonDocument.Parse(StructuredData.ReadJson(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
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
            try { ProductionChains.Merge(StructuredData.ReadJson(path), package.Errors); }
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
            try { GovernmentFocusCatalog.Merge(StructuredData.ReadJson(path), package.Errors); }
            catch (Exception exception)
            {
                package.Errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }
        foreach (var path in DataFiles(package, "common", "trade_policies"))
        {
            try { TradePolicyCatalog.Merge(StructuredData.ReadJson(path), package.Errors); }
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
            try { BudgetCore.Merge(StructuredData.ReadJson(path), package.Errors); }
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
            var types = assembly.GetTypes().Where(type => !type.IsAbstract && typeof(IGlobalityMod).IsAssignableFrom(type));
            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is not IGlobalityMod instance) continue;
                var context = new ModContext(package.Manifest.Id, message => Write($"[{package.Manifest.Id}] {message}"))
                { Campaign = campaign, FrontendActions = _frontendActions.RegisterFor(package.Manifest.Id) };
                var retained = false;
                try
                {
                    instance.OnLoad(context);
                    instance.OnCampaignCreated(context);
                    _csharp.Add((instance, context));
                    retained = true;
                }
                finally
                {
                    if (!retained)
                    {
                        context.FrontendActions.Dispose();
                        if (instance is IDisposable disposable) Try(package.Manifest.Id, disposable.Dispose);
                    }
                }
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

    private void LoadEngineScript(ModPackage package, Campaign campaign)
    {
        if (string.IsNullOrWhiteSpace(package.Manifest.EngineScriptEntry)) return;
        if (campaign.HumanSeats.Count > 1)
        { package.Errors.Add("Experimental engine-script mods are single-player only."); return; }
        var path = SafePath(package, package.Manifest.EngineScriptEntry);
        if (path is null || !File.Exists(path))
        { package.Errors.Add("Engine script must exist inside the mod directory."); return; }
        Try(package.Manifest.Id, () =>
        {
            var instance = EngineScriptMod.Load(path);
            var retained = false;
            Godot.Collections.Dictionary bridge = null;
            try
            {
                bridge = EngineScriptMod.CreateApi(_api);
                EngineScriptMod.Invoke(instance, "on_load", bridge);
                EngineScriptMod.Invoke(instance, "on_campaign_created", bridge);
                _engineScripts.Add((instance, package.Manifest.Id, bridge));
                retained = true;
            }
            finally
            {
                if (!retained)
                {
                    instance.Dispose();
                    bridge?.Dispose();
                }
            }
            Write($"[{package.Manifest.Id}] engine script active: {Path.GetFileName(path)}");
        });
    }

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

    private static IEnumerable<string> DataFiles(ModPackage package, params string[] parts)
    {
        var directory = parts.Aggregate(package.PhysicalRoot, Path.Combine);
        return StructuredData.Files(directory, SearchOption.AllDirectories);
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
        GD.Print($"[mods] {line}");
    }

    private static string Normalize(string value) =>
        (value ?? "").Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
