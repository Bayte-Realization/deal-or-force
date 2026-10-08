using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Read-only save inspection followed by an isolated replay. Never writes to the source slot.</summary>
internal static class CampaignIntegrityReport
{
    private static readonly Dictionary<Type, FieldInfo[]> Fields = new();

    internal static List<string> Numbers(Campaign c)
    {
        var problems = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var n in c.Nations) Walk(n, "country " + n.Tag, problems, seen);
        foreach (var s in c.States) Walk(s, "region " + s.Id, problems, seen);
        foreach (var w in c.Wars) Walk(w, "war " + w.Id, problems, seen);
        return problems;
    }

    private static void Walk(object value, string path, List<string> problems, HashSet<object> seen)
    {
        if (value is null || problems.Count >= 40) return;
        if (value is float f) { if (!float.IsFinite(f)) problems.Add($"{path} = {f}"); return; }
        if (value is double d) { if (!double.IsFinite(d)) problems.Add($"{path} = {d}"); return; }
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string || value is decimal) return;
        if (!type.IsValueType && !seen.Add(value)) return;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) Walk(entry.Value, path + "[" + entry.Key + "]", problems, seen);
            return;
        }
        if (value is IEnumerable list)
        {
            var i = 0;
            foreach (var item in list) Walk(item, path + "[" + i++ + "]", problems, seen);
            return;
        }
        if (type.Namespace != typeof(Campaign).Namespace) return;
        if (!Fields.TryGetValue(type, out var fields)) Fields[type] = fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var field in fields) Walk(field.GetValue(value), path + "." + field.Name, problems, seen);
    }

    public static int Run(Campaign fresh, string path, int days)
    {
        var full = Path.GetFullPath(path);
        var slot = Path.GetFileName(full).Replace(".bdfsave.json", "").Replace(".bdfsave", "");
        var loaded = SaveSystem.Load(Path.GetDirectoryName(full), slot, "");
        if (!loaded.Success) { Console.WriteLine("LOAD FAILED: " + loaded.Warning); return 2; }
        var c = loaded.Campaign;
        Console.WriteLine($"Loaded {full}: {c.Date}, day={c.ElapsedDays}, seed={c.Seed}");
        var before = Numbers(c);
        foreach (var issue in before) Console.WriteLine("BEFORE MAP: " + issue);
        c.AttachProvinceTopology(fresh.ProvinceMap);
        foreach (var tag in new[] { "PMR", "JDL", "CHE", "KOS", "THA", "ATA" })
        {
            var n = c.NationByTag(tag);
            if (n is null) continue;
            var ground = c.StatesOf(n);
            Console.WriteLine($"{tag}: landless={n.Landless}, exile={n.Movement?.Exile}, regions={ground.Length}, " +
                $"capital={n.CapitalName}, GDP={n.Gdp}, output={n.GrossOutput}, scale={n.OutputScale}, pop={n.Population}, stability={n.Stability}, " +
                $"cadres={n.Movement?.Cadres}, homeland={string.Join(',', n.Movement?.Homeland ?? new())}");
        }
        c.IsPaused = false;
        var watch = Stopwatch.StartNew();
        for (var day = 0; day <= days; day++)
        {
            var problems = Numbers(c);
            if (problems.Count > 0)
            {
                Console.WriteLine($"FIRST INVALID: replay day {day}, campaign day {c.ElapsedDays}, {c.Date}");
                foreach (var issue in problems) Console.WriteLine("FAIL: " + issue);
                foreach (var n in c.Nations.Where(n => !float.IsFinite(n.Gdp) || n.Gdp > 1e25f))
                {
                    Console.WriteLine("COUNTRY DATA: " + JsonSerializer.Serialize(n, new JsonSerializerOptions
                    { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                    foreach (var s in c.StatesOf(n)) Console.WriteLine($"STATE {s.Id} pop={s.Population} " +
                        $"buildings={string.Join(',', s.Buildings)} SOL={s.StandardOfLiving} damage={s.WarDamage}");
                }
                return 1;
            }
            if (day % 90 == 0 || day == days)
                Console.WriteLine($"FINITE day {day}/{days}, {c.Date}, regions={c.States.Count}, " +
                    $"wars={c.Wars.Count(w => !w.Ended)}, time={watch.Elapsed.TotalSeconds:0.0}s");
            if (day < days) c.TickDay();
        }
        Console.WriteLine("PASS: all replayed country, movement, region, population-group and war numbers are finite.");
        return 0;
    }
}
