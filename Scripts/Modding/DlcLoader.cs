using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce.Modding;

/// <summary>
/// What a downloadable content pack says about itself.
///
/// Deliberately the same shape as a mod manifest, because a DLC is a mod - the same folders,
/// the same layered override rules, the same loader. What separates them is provenance and
/// permission: a DLC ships with the game, is signed for by an entitlement, and loads before
/// every mod so that a mod can still correct it.
/// </summary>
public sealed class DlcManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>
    /// The thing the player has to own. Empty means the pack is free with the base game -
    /// a patch pack, a translation, a bundled scenario.
    /// </summary>
    [JsonPropertyName("entitlement")] public string Entitlement { get; set; } = "";

    /// <summary>Order among DLC. Mods always load after all of it, whatever this says.</summary>
    [JsonPropertyName("load_order")] public int LoadOrder { get; set; }

    /// <summary>Other DLC this one is written against.</summary>
    [JsonPropertyName("requires")] public List<string> Requires { get; set; } = new();

    /// <summary>
    /// Whether owning this pack changes the rules rather than only the presentation.
    ///
    /// Paradox calls this affecting the checksum, and it decides one thing that matters:
    /// two players can play together while their cosmetic packs differ, and cannot while
    /// their rule packs differ. Say so honestly and multiplayer can check it.
    /// </summary>
    [JsonPropertyName("affects_rules")] public bool AffectsRules { get; set; } = true;
}

public sealed class DlcPackage
{
    public DlcManifest Manifest { get; init; } = new();
    public string PhysicalRoot { get; init; } = "";
    public string VirtualRoot { get; init; } = "";
    public bool Owned { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> Errors { get; } = new();

    /// <summary>Content only counts if it is owned; a locked pack is listed and not read.</summary>
    public bool Active => Owned && Enabled && Errors.Count == 0;
}

/// <summary>
/// What the player has bought.
///
/// There is no store yet, and pretending otherwise would mean writing a fake one. What
/// exists is the shape the store will plug into: a list of entitlements, read from the
/// player's own profile, that decides which packs are active. Until a store writes that
/// file, `dlc/entitlements.json` beside the packs is the development answer, and a pack
/// with no entitlement named is free to everybody.
/// </summary>
public static class DlcEntitlements
{
    private static readonly HashSet<string> Owned = new(StringComparer.OrdinalIgnoreCase);
    private static bool _unlockEverything;

    public static IReadOnlyCollection<string> All => Owned;

    /// <summary>Development switch: treat every pack found on disk as owned.</summary>
    public static bool UnlockEverything => _unlockEverything;

    public static void Reload()
    {
        Owned.Clear();
        _unlockEverything = false;
        // The shipped file first, then the player's - so a real purchase always wins over
        // whatever a development build happened to leave lying about.
        Read(GameDataLoader.InstallFile("dlc", "entitlements.json"));
        Read(ProjectSettings.GlobalizePath("user://entitlements.json"));
    }

    private static void Read(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            var root = document.RootElement;
            if (root.TryGetProperty("unlock_everything", out var unlock) &&
                unlock.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _unlockEverything = unlock.GetBoolean();
            if (!root.TryGetProperty("owned", out var owned) || owned.ValueKind != JsonValueKind.Array) return;
            foreach (var entry in owned.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.String) Owned.Add(entry.GetString() ?? "");
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[dlc] {path}: {exception.Message}");
        }
    }

    public static bool Has(string entitlement) =>
        string.IsNullOrWhiteSpace(entitlement) || _unlockEverything || Owned.Contains(entitlement);
}

/// <summary>
/// Finds the content packs that ship with the game.
///
/// A pack is a folder under `dlc/` holding a `dlc.json` and the same content folders a mod
/// uses - `common/`, `history/`, `gfx/`, `map_data/`, `localization/`, and the rest. It is
/// read by the same layered loader, which is the whole point: there is one content pipeline
/// in this game, and DLC is not a second one bolted alongside it.
///
/// Order is base game, then DLC, then mods. A mod that disagrees with a DLC wins, the same
/// way a mod that disagrees with the base game wins.
/// </summary>
public sealed class DlcLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly List<DlcPackage> _packages = new();

    public IReadOnlyList<DlcPackage> Packages => _packages;
    public IEnumerable<DlcPackage> Active => _packages.Where(package => package.Active);
    public int OwnedCount => _packages.Count(package => package.Owned);

    public string StatusLine => _packages.Count == 0
        ? ""
        : Simulation.Loc.Format("{0} of {1} content pack(s) active", Active.Count(), _packages.Count);

    /// <summary>
    /// What the packs together amount to, for multiplayer to compare.
    ///
    /// Only packs that change the rules count: two players may sit down together with
    /// different flag packs and may not with different rule packs, and the difference has
    /// to be visible before the game starts rather than discovered when it desyncs.
    /// </summary>
    public string RulesFingerprint =>
        string.Join("+", Active.Where(package => package.Manifest.AffectsRules)
            .Select(package => $"{package.Manifest.Id}@{package.Manifest.Version}")
            .OrderBy(entry => entry, StringComparer.Ordinal));

    /// <summary>
    /// Find <c>dlc.json</c> packs. A folder starting with <c>_</c> is skipped, same as mods.
    /// Ownership comes from entitlements; the launcher can still disable an owned pack.
    /// Active packs are later inserted in front of mods by <c>ModLoader</c>, so a mod can still correct a pack.
    /// This is the same content pipeline as a mod, not a second one.
    /// </summary>
    public void Discover()
    {
        _packages.Clear();
        DlcEntitlements.Reload();

        // Same reason as mods: `res://dlc` is a real folder only in the editor, and a pack
        // is a folder of loose files rather than a resource, so it has to be found on disk.
        var found = new Dictionary<string, DlcPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GameDataLoader.InstallFolders("dlc"))
            DiscoverRoot(root, "res://dlc", found);
        DiscoverRoot(ProjectSettings.GlobalizePath("user://dlc"), "user://dlc", found);

        var selection = LauncherSelection.Read();
        foreach (var package in found.Values)
        {
            package.Owned = DlcEntitlements.Has(package.Manifest.Entitlement);
            package.Enabled = selection.IsEnabled(package.Manifest.Id, true);
            if (!package.Enabled) package.Errors.Add("Disabled in launcher");
        }
        var ordered = found.Values
            .OrderBy(package => package.Manifest.LoadOrder)
            .ThenBy(package => package.Manifest.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var activeIds = ContentDependencies.Resolve(ordered.Select(p => new ContentRequirement(
            p.Manifest.Id, p.Owned && p.Enabled, p.Manifest.Requires ?? new())), out var dependencyErrors);
        foreach (var error in dependencyErrors) found[error.Key].Errors.Add(error.Value);
        _packages.AddRange(activeIds.Select(id => found[id]));
        _packages.AddRange(ordered.Where(p => !_packages.Contains(p)));
    }

    private void DiscoverRoot(string physicalRoot, string virtualRoot, Dictionary<string, DlcPackage> found)
    {
        if (string.IsNullOrEmpty(physicalRoot) || !Directory.Exists(physicalRoot)) return;

        foreach (var directory in Directory.GetDirectories(physicalRoot)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(directory).StartsWith("_", StringComparison.Ordinal)) continue;
            var descriptor = Path.Combine(directory, "dlc.json");
            if (!File.Exists(descriptor)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<DlcManifest>(File.ReadAllText(descriptor), JsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    GD.PushWarning($"[dlc] ignored {descriptor}: no id.");
                    continue;
                }
                manifest.Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name;
                found[manifest.Id] = new DlcPackage
                {
                    Manifest = manifest,
                    PhysicalRoot = directory,
                    VirtualRoot = $"{virtualRoot}/{Path.GetFileName(directory)}"
                };
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[dlc] ignored {descriptor}: {exception.Message}");
            }
        }
    }
}
