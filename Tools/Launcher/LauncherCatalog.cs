using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DealOrForce.Modding;

namespace DealOrForce.Launcher;

public sealed class Package
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
    public string Entitlement { get; set; } = "";
    public string SupportedVersion { get; set; } = "*";
    public string[] Dependencies { get; set; } = [];
    public bool IsDlc { get; set; }
    public bool Owned { get; set; } = true;
    public bool Enabled { get; set; }
    public bool Active { get; set; }
    public bool Checked => Enabled && Owned;
    public bool HasCode { get; set; }
    public int Order { get; set; }
    public string Error { get; set; } = "";
    public string Status => !Owned ? L.T("Not owned") : !Enabled ? L.T("Off") : Error.Length > 0 ? Error : L.T("Will load");
    /// <summary>What the row's colour says: "ready", "off", "locked" (not owned) or "problem" (on, but cannot load).</summary>
    public string Kind => !Owned ? "locked" : !Enabled ? "off" : Active && Error.Length == 0 ? "ready" : "problem";
}

public sealed class LauncherCatalog
{
    // The game owns the publisher/game folder names; do not duplicate that policy here.
    public static string ProfileInDocuments(string documents) =>
        DealOrForce.Simulation.GameFolders.For(documents)?.Root ??
        throw new DirectoryNotFoundException(L.T("Windows could not locate the Documents folder."));

    public string Root { get; }
    public string Profile { get; }
    public string SelectionPath => System.IO.Path.Combine(Profile, "content_selection.json");
    public ContentSelection Selection { get; private set; } = new();
    public List<Package> Packages { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> DiscoveryNotes { get; } = [];
    public string SettingsError { get; private set; } = "";
    public bool IsProject => File.Exists(System.IO.Path.Combine(Root, "project.godot"));

    public LauncherCatalog(string root, string profile)
    {
        Root = System.IO.Path.GetFullPath(root);
        Profile = System.IO.Path.GetFullPath(profile);
    }

    public static string FindRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "project.godot")) ||
                File.Exists(System.IO.Path.Combine(directory.FullName, "Deal or Force.exe"))) return directory.FullName;
        throw new DirectoryNotFoundException(L.T("Put the launcher beside Deal or Force.exe or project.godot, or give --root <folder>."));
    }

    // Same precedence as GameDataLoader: published managed data, installation, then user data.
    public IEnumerable<string> InstallRoots()
    {
        if (!IsProject && Directory.Exists(Root))
            foreach (var directory in Directory.GetDirectories(Root, "data_DealOrForce*").Order())
                if (Directory.Exists(System.IO.Path.Combine(directory, "game"))) yield return directory;
        yield return Root;
    }

    public void Discover()
    {
        Packages.Clear(); Warnings.Clear(); DiscoveryNotes.Clear(); SettingsError = "";
        try { Selection = ContentSelection.Load(SelectionPath); }
        catch (Exception e) { SettingsError = e.Message; Warnings.Add("Settings are damaged: " + SelectionPath + "\n" + e.Message); }
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool unlock = false;
        var entitlementFile = InstallRoots().Select(p => System.IO.Path.Combine(p, "dlc", "entitlements.json")).FirstOrDefault(File.Exists);
        foreach (var path in new[] { entitlementFile, System.IO.Path.Combine(Profile, "entitlements.json") })
        {
            if (path is null || !File.Exists(path)) continue;
            try
            {
                using var doc = Parse(path);
                if (doc.RootElement.TryGetProperty("unlock_everything", out var u)) unlock = u.GetBoolean();
                foreach (var id in Strings(doc.RootElement, "owned")) owned.Add(id);
            }
            catch (Exception e) { Warnings.Add(path + ": " + e.Message); }
        }
        foreach (bool dlc in new[] { true, false })
        {
            var found = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in InstallRoots().Append(Profile))
            {
                var folder = System.IO.Path.Combine(root, dlc ? "dlc" : "mods");
                if (!Directory.Exists(folder)) continue;
                foreach (var directory in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (System.IO.Path.GetFileName(directory).StartsWith('_')) continue;
                    var manifest = System.IO.Path.Combine(directory, dlc ? "dlc.json" : "manifest.json");
                    if (!File.Exists(manifest)) continue;
                    try
                    {
                        using var doc = Parse(manifest);
                        var value = doc.RootElement;
                        var id = Text(value, "id");
                        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException(L.T("The manifest has no id."));
                        var entitlement = Text(value, "entitlement");
                        var package = new Package
                        {
                            Id = id, Name = Text(value, "name", id), Version = Text(value, "version", "1.0.0"),
                            Description = Text(value, "description"), Path = directory, IsDlc = dlc,
                            Entitlement = entitlement, SupportedVersion = Text(value, "supported_game_version", "*"),
                            Dependencies = Strings(value, dlc ? "requires" : "dependencies"),
                            Order = value.TryGetProperty("load_order", out var o) ? o.GetInt32() : 0,
                            Owned = !dlc || string.IsNullOrWhiteSpace(entitlement) || unlock || owned.Contains(entitlement),
                            Enabled = Selection.IsEnabled(id, dlc, dlc || !value.TryGetProperty("enabled", out var en) || en.GetBoolean()),
                            HasCode = Text(value, "gdscript_entry").Length > 0 || Text(value, "corecode_assembly").Length > 0 || Text(value, "csharp_assembly").Length > 0
                        };
                        if (found.TryGetValue(id, out var previous)) DiscoveryNotes.Add($"{id}: using {directory}; it replaces {previous.Path}");
                        found[id] = package;
                    }
                    catch (Exception e) { Warnings.Add(manifest + ": " + e.Message); }
                }
            }
            Packages.AddRange(found.Values.OrderBy(p => dlc ? p.Order : Selection.Order(p.Id, p.Order))
                .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase));
        }
        Resolve();
    }

    public void Resolve()
    {
        foreach (var p in Packages) { p.Active = false; p.Error = ""; }
        foreach (bool dlc in new[] { true, false })
        {
            var group = Packages.Where(p => p.IsDlc == dlc).ToList();
            var ids = ContentDependencies.Resolve(group.Select(p => new ContentRequirement(p.Id, p.Enabled && p.Owned, p.Dependencies)), out var errors);
            foreach (var p in group)
            {
                p.Active = ids.Contains(p.Id, StringComparer.OrdinalIgnoreCase);
                if (errors.TryGetValue(p.Id, out var error)) p.Error = error;
            }
        }
    }

    public List<string> EffectiveOrder(bool dlc) => ContentDependencies.Resolve(
        Packages.Where(p => p.IsDlc == dlc).Select(p => new ContentRequirement(p.Id, p.Enabled && p.Owned, p.Dependencies)), out _);

    public void Save()
    {
        if (SettingsError.Length > 0) throw new InvalidDataException(L.T("Fix content_selection.json, or restore the .bak, before saving."));
        foreach (var p in Packages) (p.IsDlc ? Selection.Dlc : Selection.Mods)[p.Id] = p.Enabled;
        Selection.ModOrder = Packages.Where(p => !p.IsDlc).Select(p => p.Id).ToList();
        Selection.Save(SelectionPath);
    }

    public ProcessStartInfo LaunchInfo(string? godot)
    {
        ProcessStartInfo info;
        if (IsProject)
        {
            if (string.IsNullOrWhiteSpace(godot) || !File.Exists(godot)) throw new FileNotFoundException(L.T("Choose Godot .NET to run the project."));
            info = new(godot);
            info.ArgumentList.Add("--path"); info.ArgumentList.Add(Root);
        }
        else
        {
            var exe = System.IO.Path.Combine(Root, "Deal or Force.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException(L.T("Deal or Force.exe was not found beside the launcher."));
            info = new(exe);
        }
        info.UseShellExecute = false;
        info.WorkingDirectory = Root;
        info.Environment["DOF_CONTENT_SELECTION"] = SelectionPath;
        return info;
    }

    private static JsonDocument Parse(string path) => JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    private static string Text(JsonElement root, string key, string fallback = "") =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static string[] Strings(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToArray() : [];
}
