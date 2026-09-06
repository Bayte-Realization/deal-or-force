using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Content, not an enum: mods can introduce terrain without recompiling the game.</summary>
public sealed record TerrainDefinition(
    string Id, string Name, int Color, string Texture,
    float MovementSpeed = 1, float Attack = 1, float Defense = 1,
    float CombatWidth = 1, float Supply = 1, string TextureRelative = "");

public static class TerrainCatalog
{
    private static readonly Dictionary<string, TerrainDefinition> Definitions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> Overrides = new();
    public static string TerrainPath { get; private set; } = "";
    public static string HeightPath { get; private set; } = "";
    public static string ReliefColorPath { get; private set; } = "";
    public static float PoliticalOpacity { get; private set; } = .68f;
    public static float TileRepeat { get; private set; } = 180;
    public static float ReliefStrength { get; private set; } = .8f;
    public static float ReliefColorOpacity { get; private set; } = 0;
    public static IReadOnlyList<TerrainDefinition> All => Definitions.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    public static TerrainDefinition Get(string id) => Definitions.GetValueOrDefault(id ?? "") ?? Definitions["plains"];
    static TerrainCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Definitions.Clear();
        Overrides.Clear();
        Definitions["plains"] = new("plains", "Plains", 0x8DAB68, "");
        TerrainPath = HeightPath = ReliefColorPath = "";
        PoliticalOpacity = .68f;
        TileRepeat = 180;
        ReliefStrength = .8f;
        ReliefColorOpacity = 0;
        var root = GameDataLoader.DataFolder();
        if (root is not null) ApplyDirectory(root, new List<string>());
    }

    /// <summary>Ordered per-field overlays. Paths belong to the package that declares them.</summary>
    public static void ApplyDirectory(string root, List<string> errors)
    {
        foreach (var previous in Definitions.Values.ToArray())
            if (!string.IsNullOrEmpty(previous.TextureRelative) && File.Exists(Path.Combine(root, previous.TextureRelative)))
                Definitions[previous.Id] = previous with { Texture = Asset(root, previous.TextureRelative) };
        foreach (var path in Files(root, "common/terrain"))
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path), GameDataLoader.ForgivingJson);
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                var value = entry.Value;
                var previous = Definitions.GetValueOrDefault(entry.Name) ??
                    new TerrainDefinition(entry.Name, entry.Name, -1, "");
                var color = value.TryGetProperty("color", out var c)
                    ? int.Parse(c.GetString()!.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : previous.Color;
                if (color <= 0 || color > 0xffffff || Definitions.Values.Any(t => t.Id != entry.Name && t.Color == color))
                    throw new InvalidDataException($"Terrain {entry.Name} needs a unique, nonblack #RRGGBB color.");
                if (!Definitions.ContainsKey(entry.Name) && Definitions.Count >= 256)
                    throw new InvalidDataException("At most 256 terrain types are supported.");
                var texture = value.TryGetProperty("texture", out var t) ? Asset(root, t.GetString()!) : previous.Texture;
                if (value.TryGetProperty("texture", out _) && !File.Exists(texture))
                    throw new InvalidDataException($"Terrain texture not found: {texture}");
                Definitions[entry.Name] = previous with
                {
                    Color = color, Texture = texture,
                    TextureRelative = value.TryGetProperty("texture", out var relative) ? relative.GetString()! : previous.TextureRelative,
                    Name = value.TryGetProperty("name", out var n) ? n.GetString()! : previous.Name,
                    MovementSpeed = Number(value, "movement_speed", previous.MovementSpeed, .05f, 5),
                    Attack = Number(value, "attack", previous.Attack, .05f, 5),
                    Defense = Number(value, "defense", previous.Defense, .05f, 5),
                    CombatWidth = Number(value, "combat_width", previous.CombatWidth, .1f, 4),
                    Supply = Number(value, "supply", previous.Supply, .05f, 3)
                };
            }
        }
        catch (Exception e) { errors.Add($"{path}: {e.Message}"); }

        if (File.Exists(Path.Combine(root, "map_data/terrain.png"))) TerrainPath = Path.Combine(root, "map_data/terrain.png");
        if (File.Exists(Path.Combine(root, "map_data/heightmap.png"))) HeightPath = Path.Combine(root, "map_data/heightmap.png");
        if (File.Exists(Path.Combine(root, "map_data/relief_color.png"))) ReliefColorPath = Path.Combine(root, "map_data/relief_color.png");
        var settingsPath = Path.Combine(root, "map_data/terrain_settings.json");
        if (File.Exists(settingsPath))
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(settingsPath), GameDataLoader.ForgivingJson);
            var s = json.RootElement;
            TerrainPath = MapAsset(s, "terrain", root, TerrainPath);
            HeightPath = MapAsset(s, "heightmap", root, HeightPath);
            ReliefColorPath = MapAsset(s, "relief_color", root, ReliefColorPath);
            PoliticalOpacity = Number(s, "political_opacity", PoliticalOpacity, 0, 1);
            TileRepeat = Number(s, "tile_repeat", TileRepeat, 1, 1000);
            ReliefStrength = Number(s, "relief_strength", ReliefStrength, 0, 8);
            ReliefColorOpacity = Number(s, "relief_color_opacity", ReliefColorOpacity, 0, 1);
        }
        catch (Exception e) { errors.Add($"{settingsPath}: {e.Message}"); }
        foreach (var path in Files(root, "history/provinces/terrain"))
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path), GameDataLoader.ForgivingJson);
            foreach (var entry in json.RootElement.EnumerateObject())
            {
                var id = int.Parse(entry.Name, CultureInfo.InvariantCulture);
                var terrain = entry.Value.GetString()!;
                if (id <= 0 || !Definitions.ContainsKey(terrain)) throw new InvalidDataException($"Invalid terrain override {id}: {terrain}");
                Overrides[id] = terrain;
            }
        }
        catch (Exception e) { errors.Add($"{path}: {e.Message}"); }
    }

    public static TerrainDefinition[] Bind(ProvinceTopology topology)
    {
        var types = All;
        var result = Enumerable.Repeat(Get("plains"), topology.ProvinceCount + 1).ToArray();
        if (string.IsNullOrEmpty(TerrainPath)) return result; // tiny synthetic topology tests
        var map = DataMapPng.LoadRgb(TerrainPath);
        if (map.Width != topology.Width || map.Height != topology.Height)
            throw new InvalidDataException($"terrain.png must match provinces.png ({topology.Width}x{topology.Height}).");
        var slots = types.Select((t, index) => (t.Color, index)).ToDictionary(t => t.Color, t => t.index);
        var counts = new int[(topology.ProvinceCount + 1) * types.Count];
        var surface = new byte[topology.CellToProvince.Length];
        for (var cell = 0; cell < topology.CellToProvince.Length; cell++)
        {
            var province = topology.CellToProvince[cell];
            if (province == 0 || topology.IsWater(province)) continue;
            var p = cell * 3;
            var color = map.Pixels[p] << 16 | map.Pixels[p + 1] << 8 | map.Pixels[p + 2];
            if (!slots.TryGetValue(color, out var slot))
                throw new InvalidDataException($"Unknown terrain color #{color:X6} at {cell % map.Width},{cell / map.Width}, province {province}.");
            counts[province * types.Count + slot]++;
            surface[cell] = (byte)slot;
        }
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var best = 0;
            for (var slot = 1; slot < types.Count; slot++)
                if (counts[province * types.Count + slot] > counts[province * types.Count + best]) best = slot;
            if (!topology.IsWater(province)) result[province] = types[best];
        }
        foreach (var (province, terrain) in Overrides)
        {
            if (province >= result.Length) throw new InvalidDataException($"Terrain override references missing province {province}.");
            result[province] = Get(terrain);
        }
        if (Overrides.Count > 0)
            for (var cell = 0; cell < surface.Length; cell++)
                if (Overrides.ContainsKey(topology.CellToProvince[cell]))
                    surface[cell] = (byte)slots[result[topology.CellToProvince[cell]].Color];
        topology.TerrainSurfaceSlots = surface;
        return result;
    }

    private static IEnumerable<string> Files(string root, string folder) => Directory.Exists(Path.Combine(root, folder))
        ? Directory.GetFiles(Path.Combine(root, folder), "*.json").OrderBy(x => x, StringComparer.Ordinal) : Array.Empty<string>();
    private static string Asset(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Terrain asset paths must stay inside their package.");
        return full;
    }
    private static string MapAsset(JsonElement s, string key, string root, string fallback)
    {
        if (!s.TryGetProperty(key, out var p)) return fallback;
        var path = Asset(root, p.GetString()!);
        if (!File.Exists(path)) throw new InvalidDataException($"Missing {key}: {path}");
        return path;
    }
    private static float Number(JsonElement e, string name, float fallback, float min, float max)
    {
        if (!e.TryGetProperty(name, out var p)) return fallback;
        var value = p.GetSingle();
        if (!float.IsFinite(value) || value < min || value > max)
            throw new InvalidDataException($"{name} must be between {min} and {max}.");
        return value;
    }
}
