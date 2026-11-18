using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// For the big partial classes: which file's members are used only by that file (extractable as
// they stand), and how many members of other files each file leans on (the cost of extracting it).
static class Partials
{
    public static void Run(string root)
    {
        var outDir = Path.Combine(root, "tmp_logs", "code-audit", "out");
        Directory.CreateDirectory(outDir);
        bool Skip(string p) => p.Contains(@"\bin\") || p.Contains(@"\obj\") || p.Contains(@"\.fennara\") || p.Contains(@"\.godot\");
        var files = Directory.EnumerateFiles(Path.Combine(root, "Scripts"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Tools", "ExportVerification"), "*.cs", SearchOption.AllDirectories))
            .Where(p => !Skip(p)).OrderBy(p => p).ToList();
        string Rel(string p) => Path.GetRelativePath(root, p).Replace('\\', '/');
        var parse = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[] { "GODOT", "GODOT4", "GODOT4_OR_GREATER", "GODOT4_8", "GODOT_WINDOWS", "GODOT_PC", "DEBUG", "TOOLS" });
        var trees = files.AsParallel().AsOrdered().Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, path: f)).ToList();
        var refs = AuditCompilation.ResolveReferences(root);
        var comp = CSharpCompilation.Create("Audit", trees, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var targets = new[] { "DealOrForceGame", "GlobePresentation", "Campaign", "Nation", "Warfare", "AiStatecraft" };

        var declFile = new ConcurrentDictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var users = new ConcurrentDictionary<ISymbol, ConcurrentDictionary<string, byte>>(SymbolEqualityComparer.Default);
        Parallel.ForEach(trees, t =>
        {
            var model = comp.GetSemanticModel(t);
            var rel = Rel(t.FilePath);
            foreach (var n in t.GetRoot().DescendantNodes())
            {
                if (n is MemberDeclarationSyntax md && md is not BaseTypeDeclarationSyntax && md.Parent is TypeDeclarationSyntax td && targets.Contains(td.Identifier.Text))
                {
                    IEnumerable<ISymbol> syms = md is FieldDeclarationSyntax fd ? fd.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v))
                        : md is EventFieldDeclarationSyntax ef ? ef.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v))
                        : new[] { model.GetDeclaredSymbol(md) };
                    foreach (var s in syms) if (s != null) declFile.TryAdd(s.OriginalDefinition, rel);
                }
                if (n is SimpleNameSyntax sn)
                {
                    var s = model.GetSymbolInfo(sn).Symbol;
                    if (s == null || s.ContainingType == null || !targets.Contains(s.ContainingType.Name)) continue;
                    users.GetOrAdd(s.OriginalDefinition, _ => new())[rel] = 0;
                }
            }
        });
        var sb = new StringBuilder();
        foreach (var target in targets)
        {
            var mine = declFile.Where(kv => kv.Key.ContainingType.Name == target).ToList();
            var byFile = mine.GroupBy(kv => kv.Value).Select(g =>
            {
                int total = g.Count();
                int local = g.Count(kv => !users.TryGetValue(kv.Key, out var u) || u.Keys.All(f => f == g.Key));
                int exported = total - local;
                int imports = users.Count(kv => kv.Key.ContainingType.Name == target && kv.Value.ContainsKey(g.Key) && declFile.TryGetValue(kv.Key, out var df) && df != g.Key);
                return (file: g.Key, total, local, exported, imports);
            }).ToList();
            int fields = mine.Count(kv => kv.Key is IFieldSymbol);
            var sharedFields = mine.Where(kv => kv.Key is IFieldSymbol && users.TryGetValue(kv.Key, out var u) && u.Count >= 5).Select(kv => (kv.Key.Name, users[kv.Key].Count)).OrderByDescending(x => x.Count).Take(25);
            sb.AppendLine($"=== {target}: {byFile.Count} files, {mine.Count} members, {fields} fields; members used outside their own file: {byFile.Sum(x => x.exported)}");
            sb.AppendLine($"  files whose members are all file-local: {byFile.Count(x => x.exported == 0)}; median imports from sibling files: {byFile.Select(x => x.imports).OrderBy(x => x).ElementAtOrDefault(byFile.Count / 2)}");
            sb.AppendLine("  most-shared fields (files using them): " + string.Join(", ", sharedFields.Select(x => $"{x.Name}:{x.Count}")));
            sb.AppendLine("  easiest to extract (few imports, few exports):");
            foreach (var x in byFile.OrderBy(x => x.imports + 3 * x.exported).Take(15)) sb.AppendLine($"    {x.file}  members={x.total} exported={x.exported} imports={x.imports}");
            sb.AppendLine("  most entangled:");
            foreach (var x in byFile.OrderByDescending(x => x.imports + 3 * x.exported).Take(10)) sb.AppendLine($"    {x.file}  members={x.total} exported={x.exported} imports={x.imports}");
        }
        File.WriteAllText(Path.Combine(outDir, "partials.txt"), sb.ToString());
        Console.WriteLine(sb.ToString());
    }
}
