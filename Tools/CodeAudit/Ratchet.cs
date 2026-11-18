using System.Text.Json;

sealed class AuditBaseline
{
    public int CompileErrors { get; set; }
    public int GodotInSimulationUses { get; set; }
    public int MaxFileLines { get; set; }
    public int MaxMethodLines { get; set; }
    public int MaxCyclomatic { get; set; }
    public int SimulationStaticMutable { get; set; }
    public int DeadCandidates { get; set; }
    public int TotalFiles { get; set; }
    public int TotalLines { get; set; }
    public SortedDictionary<string, FileBaseline> Files { get; set; } = new(StringComparer.Ordinal);
}

sealed class FileBaseline
{
    public int Lines { get; set; }
    public int MaxMethodLines { get; set; }
    public int MaxCyclomatic { get; set; }
    public int StaticMutable { get; set; }
    public int MagicNumbers { get; set; }
}

static class Ratchet
{
    private const int NewFileLineCeiling = 800;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string BaselinePath(string root) =>
        Path.Combine(root, "Tools", "CodeAudit", "baseline.json");

    public static void Accept(string root, AuditBaseline current)
    {
        var path = BaselinePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(current, JsonOptions) + "\n", new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Accepted baseline -> {path}");
        Console.WriteLine(
            $"  files={current.TotalFiles} lines={current.TotalLines} compileErrors={current.CompileErrors} " +
            $"godotInSim={current.GodotInSimulationUses} maxFileLines={current.MaxFileLines} " +
            $"maxMethodLines={current.MaxMethodLines} maxCyclomatic={current.MaxCyclomatic} " +
            $"simStaticMutable={current.SimulationStaticMutable} deadCandidates={current.DeadCandidates}");
    }

    public static int Check(string root, AuditBaseline current)
    {
        var path = BaselinePath(root);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"FAIL CodeAudit --ratchet: missing baseline file '{path}'. Run with --accept first.");
            return 1;
        }

        var baseline = JsonSerializer.Deserialize<AuditBaseline>(File.ReadAllText(path), JsonOptions);
        if (baseline is null)
        {
            Console.Error.WriteLine($"FAIL CodeAudit --ratchet: could not parse '{path}'.");
            return 1;
        }

        var failures = new List<string>();
        if (current.CompileErrors > Math.Max(0, baseline.CompileErrors))
            failures.Add($"compileErrors grew {baseline.CompileErrors} -> {current.CompileErrors}");
        if (current.GodotInSimulationUses > Math.Max(0, baseline.GodotInSimulationUses))
            failures.Add($"godotInSimulationUses grew {baseline.GodotInSimulationUses} -> {current.GodotInSimulationUses}");
        if (current.MaxFileLines > baseline.MaxFileLines)
            failures.Add($"maxFileLines grew {baseline.MaxFileLines} -> {current.MaxFileLines}");
        if (current.MaxMethodLines > baseline.MaxMethodLines)
            failures.Add($"maxMethodLines grew {baseline.MaxMethodLines} -> {current.MaxMethodLines}");
        if (current.MaxCyclomatic > baseline.MaxCyclomatic)
            failures.Add($"maxCyclomatic grew {baseline.MaxCyclomatic} -> {current.MaxCyclomatic}");
        if (current.SimulationStaticMutable > baseline.SimulationStaticMutable)
            failures.Add($"simulationStaticMutable grew {baseline.SimulationStaticMutable} -> {current.SimulationStaticMutable}");
        if (current.DeadCandidates > baseline.DeadCandidates)
            failures.Add($"deadCandidates grew {baseline.DeadCandidates} -> {current.DeadCandidates}");

        foreach (var (file, cur) in current.Files)
        {
            if (baseline.Files.TryGetValue(file, out var prev))
            {
                if (cur.Lines > prev.Lines)
                    failures.Add($"{file}: lines grew {prev.Lines} -> {cur.Lines}");
                if (cur.MaxMethodLines > prev.MaxMethodLines)
                    failures.Add($"{file}: maxMethodLines grew {prev.MaxMethodLines} -> {cur.MaxMethodLines}");
                if (cur.MaxCyclomatic > prev.MaxCyclomatic)
                    failures.Add($"{file}: maxCyclomatic grew {prev.MaxCyclomatic} -> {cur.MaxCyclomatic}");
                if (cur.StaticMutable > prev.StaticMutable)
                    failures.Add($"{file}: staticMutable grew {prev.StaticMutable} -> {cur.StaticMutable}");
                if (cur.MagicNumbers > prev.MagicNumbers)
                    failures.Add($"{file}: magicNumbers grew {prev.MagicNumbers} -> {cur.MagicNumbers}");
            }
            else if (cur.Lines > NewFileLineCeiling)
            {
                failures.Add($"{file}: new file has {cur.Lines} lines (exceeds {NewFileLineCeiling})");
            }
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"FAIL CodeAudit --ratchet: {failures.Count} regression(s):");
            foreach (var f in failures.Take(50)) Console.Error.WriteLine("  - " + f);
            if (failures.Count > 50) Console.Error.WriteLine($"  ... and {failures.Count - 50} more.");
            return 1;
        }

        Console.WriteLine(
            $"PASS CodeAudit --ratchet: 0 regressions " +
            $"(files={current.TotalFiles}, maxFileLines={current.MaxFileLines}/{baseline.MaxFileLines}, " +
            $"maxMethodLines={current.MaxMethodLines}/{baseline.MaxMethodLines}, " +
            $"maxCyclomatic={current.MaxCyclomatic}/{baseline.MaxCyclomatic}, " +
            $"simStaticMutable={current.SimulationStaticMutable}/{baseline.SimulationStaticMutable}, " +
            $"deadCandidates={current.DeadCandidates}/{baseline.DeadCandidates})");
        return 0;
    }
}
