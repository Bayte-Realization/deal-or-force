using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Loads human-editable common/ data before a campaign is created. The layout mirrors
/// Paradox titles, while the values still end in a typed C# Defines object used by the
/// deterministic simulation. Invalid keys are ignored and invalid values keep their
/// compiled fallback, so a balancing mod cannot make the executable unbootable.
/// </summary>
public static class GameDataLoader
{
    public static void ApplyDefines(Defines defines)
    {
        var file = FindDataFile("common", "defines", "military.json");
        if (file is null) return;

        try
        {
            // The shipped file documents itself with comments, and every other loader in
            // the project already accepts them.
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            var properties = typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanWrite)
                .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!properties.TryGetValue(entry.Name, out var property)) continue;
                var value = ConvertValue(entry.Value, property.PropertyType);
                if (value is not null) property.SetValue(defines, value);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[dof] common defines ignored: {exception.Message}");
        }
    }

    public static Dictionary<string, float> LoadNumberMap(params string[] relativePath)
    {
        var suffix = Path.Combine(relativePath);
        var file = FindDataFile(relativePath);
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (file is null) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var entry in document.RootElement.EnumerateObject())
                if (entry.Value.TryGetSingle(out var number)) result[entry.Name] = number;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[dof] data table {suffix} ignored: {exception.Message}");
        }
        return result;
    }

    public static string FindDataFile(params string[] relativePath)
    {
        var suffix = Path.Combine(relativePath);
        return CandidateRoots().Select(root => Path.Combine(root, "game", suffix)).FirstOrDefault(File.Exists);
    }

    private static object ConvertValue(JsonElement value, Type type)
    {
        if (type == typeof(float) && value.TryGetSingle(out var single)) return single;
        if (type == typeof(double) && value.TryGetDouble(out var real)) return real;
        if (type == typeof(int) && value.TryGetInt32(out var integer)) return integer;
        if (type == typeof(bool) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            return value.GetBoolean();
        if (type == typeof(string) && value.ValueKind == JsonValueKind.String) return value.GetString();
        if (type.IsEnum && value.ValueKind == JsonValueKind.String &&
            Enum.TryParse(type, value.GetString(), true, out var enumeration)) return enumeration;
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
            yield return directory.FullName;
    }
}
