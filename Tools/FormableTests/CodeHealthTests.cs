using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

static class CodeHealthTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL code health: " + label);
        Console.WriteLine("PASS code health: " + label);
    }

    [TestSuite("--code-health-only", Description = "JIT warmup target resolution, zero Godot in Simulation, and .cs.uid integrity.")]
    public static void Run()
    {
        var root = FindRepoRoot();
        Check(Directory.Exists(Path.Combine(root, "Scripts", "Simulation")), "located repository root");

        // 1. Every JitWarmup target must resolve (0 silent warmup misses).
        var targets = JitWarmup.GetTargets();
        Check(targets.Length > 0, "JitWarmup targets registered");
        foreach (var (type, methodName) in targets)
        {
            var (methods, moveNexts) = JitWarmup.ResolveTargetMethods(type, methodName);
            Check(methods.Length > 0, $"JitWarmup target {type.Name}.{methodName} resolves");
            var returnsIterator = methods.Any(m =>
                m.ReturnType.IsGenericType &&
                (m.ReturnType.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
                 m.ReturnType.GetGenericTypeDefinition() == typeof(IEnumerator<>)));
            if (returnsIterator)
                Check(moveNexts.Length > 0, $"JitWarmup iterator {type.Name}.{methodName} MoveNext resolves");
        }
        JitWarmup.PrepareWeeklyPasses();
        Check(true, "JitWarmup.PrepareWeeklyPasses executes without throwing");

        // 2. Zero `using Godot` in Scripts/Simulation.
        var simDir = Path.Combine(root, "Scripts", "Simulation");
        var godotViolations = new List<string>();
        var usingGodotRx = new Regex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?Godot(?:\s*\.\s*[A-Za-z0-9_]+)*\s*;", RegexOptions.Multiline);
        foreach (var cs in Directory.EnumerateFiles(simDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsIgnoredBuildPath(cs)) continue;
            var text = File.ReadAllText(cs);
            if (usingGodotRx.IsMatch(text) || Regex.IsMatch(text, @"\bGodot\.[A-Za-z_]"))
                godotViolations.Add(Path.GetRelativePath(root, cs));
        }
        Check(godotViolations.Count == 0,
            $"0 Godot dependencies in Scripts/Simulation (found {godotViolations.Count}: {string.Join(", ", godotViolations.Take(5))})");

        // 3. Zero missing .cs.uid sidecars and zero duplicate UIDs under Scripts/.
        var scriptsDir = Path.Combine(root, "Scripts");
        var missingUids = new List<string>();
        var duplicateUids = new List<string>();
        var seenUids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cs in Directory.EnumerateFiles(scriptsDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsIgnoredBuildPath(cs)) continue;
            var rel = Path.GetRelativePath(root, cs);
            var uidPath = cs + ".uid";
            if (!File.Exists(uidPath))
            {
                missingUids.Add(rel);
                continue;
            }
            var uid = File.ReadAllText(uidPath).Trim();
            if (!uid.StartsWith("uid://", StringComparison.Ordinal) || uid.Length < 10)
            {
                missingUids.Add($"{rel} (malformed uid '{uid}')");
                continue;
            }
            if (!seenUids.TryAdd(uid, rel))
                duplicateUids.Add($"{uid}: {seenUids[uid]} vs {rel}");
        }
        Check(missingUids.Count == 0,
            $"0 missing Scripts/**/*.cs.uid sidecars (missing {missingUids.Count}: {string.Join(", ", missingUids.Take(5))})");
        Check(duplicateUids.Count == 0,
            $"0 duplicate Scripts/**/*.cs.uid values (duplicates {duplicateUids.Count}: {string.Join(", ", duplicateUids.Take(5))})");

        // 4. Zero strong static Campaign fields in Scripts/Simulation (prevents memory-pinning old worlds).
        var staticCampaignFields = new List<string>();
        foreach (var type in typeof(Campaign).Assembly.GetTypes())
        {
            if (type.Namespace is null || !type.Namespace.StartsWith("DealOrForce.Simulation", StringComparison.Ordinal)) continue;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(Campaign))
                    staticCampaignFields.Add($"{type.Name}.{field.Name}");
            }
        }
        Check(staticCampaignFields.Count == 0,
            $"0 static Campaign fields in DealOrForce.Simulation (found {staticCampaignFields.Count}: {string.Join(", ", staticCampaignFields)})");
    }

    static bool IsIgnoredBuildPath(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static string FindRepoRoot()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate DealOrForce.csproj from current or app base directory.");
    }
}
