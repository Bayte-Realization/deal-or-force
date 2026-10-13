using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum LibraryMediaKind { Music, Sound, Image, Video }
public sealed record LibraryMedia(string Id, string Name, string Purpose, LibraryMediaKind Kind, string File, string Source);

/// <summary>Presentation-only inventory. Loading or browsing never touches campaign state.</summary>
public static class MediaLibraryCatalog
{
    private static readonly Dictionary<string, LibraryMedia> Items = new(StringComparer.OrdinalIgnoreCase);
    public static IEnumerable<LibraryMedia> All => Items.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id, StringComparer.Ordinal);
    public static LibraryMedia Find(string id) => id is not null ? Items.GetValueOrDefault(id) : null;
    public static int Revision { get; private set; }
    public static void Load()
    {
        Items.Clear(); Revision++;
        var root = GameDataLoader.DataFolder("presentation");
        if (root is not null) LoadTree(root, "res://game", null);
    }
    public static void LoadTree(string directory, string assetRoot, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        foreach (var file in StructuredData.Files(directory, SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            try { Merge(StructuredData.ReadJson(file), assetRoot, file, errors); }
            catch (Exception ex) { errors?.Add($"media: {ex.Message}"); }
    }
    public static void Merge(string json, string assetRoot, string origin, List<string> errors)
    {
        using var doc = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object)
        { errors?.Add(origin + ": assets must be an object"); return; }
        foreach (var entry in assets.EnumerateObject())
        {
            try
            {
                var e = entry.Value;
                if (!MechanicFeatures.ValidId(entry.Name) || e.ValueKind != JsonValueKind.Object) throw new FormatException("invalid asset id or body");
                if (e.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.True) { Items.Remove(entry.Name); continue; }
                var old = Find(entry.Name);
                string Text(string key, string fallback) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;
                var kindText = Text("kind", old?.Kind.ToString() ?? "");
                if (!Enum.TryParse<LibraryMediaKind>(kindText, true, out var kind) || !Enum.IsDefined(kind)) throw new FormatException("unknown media kind");
                var file = Text("file", old?.File ?? "").Replace('\\', '/');
                if (file.Length == 0 || file.Contains('\0') || file.Split('/').Any(p => p == "..") ||
                    file.Contains("://") && !file.StartsWith("res://", StringComparison.Ordinal) && !file.StartsWith("user://", StringComparison.Ordinal) ||
                    !file.Contains("://") && (file.StartsWith('/') || file.Contains(':'))) throw new FormatException("asset must be a local package path");
                var resolved = e.TryGetProperty("file", out _) || old is null ? GameDataLoader.ResolveAssetPath(file, assetRoot) : old.File;
                var extension = Path.GetExtension(resolved).ToLowerInvariant();
                var supported = kind switch
                {
                    LibraryMediaKind.Music or LibraryMediaKind.Sound => new[] { ".ogg", ".wav", ".mp3" }.Contains(extension),
                    LibraryMediaKind.Image => new[] { ".png", ".jpg", ".jpeg", ".svg", ".webp", ".bmp", ".tga" }.Contains(extension),
                    LibraryMediaKind.Video => extension == ".ogv", _ => false
                };
                if (!supported) throw new FormatException("unsupported local format for this media kind");
                Items[entry.Name] = new(entry.Name, Text("name", old?.Name ?? entry.Name), Text("purpose", old?.Purpose ?? ""), kind,
                    resolved, Text("source", old?.Source ?? ""));
            }
            catch (Exception ex) { errors?.Add(origin + ": " + entry.Name + ": " + ex.Message); }
        }
        Revision++;
    }
}
