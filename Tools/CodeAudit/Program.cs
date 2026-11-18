using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

static class AuditCompilation
{
    public static string FindRepoRoot(string[] args)
    {
        var explicitRoot = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.GetFullPath(explicitRoot);
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    public static List<MetadataReference> ResolveReferences(string root)
    {
        var refs = new List<MetadataReference>();
        var refPacksRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "packs", "Microsoft.NETCore.App.Ref");
        string refPack = null;
        if (Directory.Exists(refPacksRoot))
        {
            refPack = Directory.GetDirectories(refPacksRoot)
                .Select(d => Path.Combine(d, "ref", "net10.0"))
                .Where(Directory.Exists)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        refPack ??= Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        refs.AddRange(Directory.GetFiles(refPack, "*.dll").Select(d => MetadataReference.CreateFromFile(d)));

        var seenDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var probeDirs = new List<string>
        {
            Path.Combine(root, ".godot", "mono", "temp", "bin", "Debug"),
            Path.Combine(root, ".godot", "mono", "temp", "bin", "Release"),
            Path.Combine(root, "Scripts", "Rules", "bin", "Release", "net8.0"),
            Path.Combine(root, "Scripts", "Rules", "bin", "Debug", "net8.0"),
        };
        var tmpLogs = Path.Combine(root, "tmp_logs");
        if (Directory.Exists(tmpLogs))
        {
            foreach (var dir in Directory.GetDirectories(tmpLogs))
            {
                var rulesRel = Path.Combine(dir, "bin", "DealOrForce.Rules", "release");
                if (Directory.Exists(rulesRel)) probeDirs.Add(rulesRel);
                var rulesDbg = Path.Combine(dir, "bin", "DealOrForce.Rules", "debug");
                if (Directory.Exists(rulesDbg)) probeDirs.Add(rulesDbg);
            }
        }

        foreach (var bin in probeDirs)
        {
            if (!Directory.Exists(bin)) continue;
            foreach (var d in Directory.GetFiles(bin, "*.dll"))
            {
                var n = Path.GetFileName(d);
                if (n.Equals("DealOrForce.dll", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("GodotSharpEditor", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenDlls.Add(n)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(d)); } catch { }
            }
        }
        return refs;
    }
}

static class CodeAuditRunner
{
    public static int Main(string[] args)
    {
        var root = AuditCompilation.FindRepoRoot(args);
        if (args.Contains("--partials")) { Partials.Run(root); return 0; }
        if (args.Contains("--verify-only")) { Verify.Run(root); return 0; }

        bool runVerify = args.Contains("--verify");
        bool runRatchet = args.Contains("--ratchet");
        bool runAccept = args.Contains("--accept");

        var baseline = RunFullAnalysis(root);
        if (runVerify) Verify.Run(root);
        if (runAccept) Ratchet.Accept(root, baseline);
        if (runRatchet) return Ratchet.Check(root, baseline);
        return 0;
    }

    static AuditBaseline RunFullAnalysis(string root)
    {
        var outDir = Path.Combine(root, "tmp_logs", "code-audit", "out");
        Directory.CreateDirectory(outDir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Log(string s) { Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] {s}"); }

        bool Skip(string p) => p.Contains(@"\bin\") || p.Contains(@"\obj\") || p.Contains(@"\.fennara\") || p.Contains(@"\.godot\");
        var files = Directory.EnumerateFiles(Path.Combine(root, "Scripts"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Tools", "ExportVerification"), "*.cs", SearchOption.AllDirectories))
            .Where(p => !Skip(p)).OrderBy(p => p).ToList();
        string Rel(string p) => Path.GetRelativePath(root, p).Replace('\\', '/');
        Log($"compile set: {files.Count} files");

        var parse = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[] { "GODOT", "GODOT4", "GODOT4_OR_GREATER", "GODOT4_8", "GODOT_WINDOWS", "GODOT_PC", "DEBUG", "TOOLS" });
        var texts = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(files, f => texts[f] = File.ReadAllText(f));
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(texts[f], parse, path: f)).ToList();
        Log("parsed");

        var refs = AuditCompilation.ResolveReferences(root);
        var comp = CSharpCompilation.Create("Audit", trees, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        var diags = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Log($"compile errors: {diags.Count}");
        File.WriteAllLines(Path.Combine(outDir, "compile_errors.txt"), diags.GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}  e.g. {g.First().GetMessage()} @ {Rel(g.First().Location.SourceTree?.FilePath ?? "")}"));

        var fileRows = new List<string> { "file,lines,types,methods,maxMethodLines,maxCyclomatic" };
        var perFileMetrics = new SortedDictionary<string, FileBaseline>(StringComparer.Ordinal);
        var methodRows = new ConcurrentBag<(string file, string type, string name, int lines, int cc, int nest, int pars, int startLine)>();

        static int Cyclomatic(SyntaxNode body)
        {
            int cc = 1;
            foreach (var n in body.DescendantNodes(x => x is not LambdaExpressionSyntax && x is not LocalFunctionStatementSyntax || x == body))
            {
                switch (n)
                {
                    case IfStatementSyntax: case ConditionalExpressionSyntax: case ForStatementSyntax: case ForEachStatementSyntax:
                    case WhileStatementSyntax: case DoStatementSyntax: case CatchClauseSyntax: case CasePatternSwitchLabelSyntax:
                    case CaseSwitchLabelSyntax: case ForEachVariableStatementSyntax:
                        cc++; break;
                    case SwitchExpressionArmSyntax arm when arm.Pattern is not DiscardPatternSyntax: cc++; break;
                    case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression) || b.IsKind(SyntaxKind.CoalesceExpression): cc++; break;
                }
            }
            return cc;
        }
        static int Nesting(SyntaxNode node, int depth = 0)
        {
            int best = depth;
            foreach (var c in node.ChildNodes())
            {
                if (c is LambdaExpressionSyntax || c is LocalFunctionStatementSyntax) continue;
                bool nests = c is IfStatementSyntax ifs && ifs.Parent is not ElseClauseSyntax || c is ForStatementSyntax || c is ForEachStatementSyntax || c is WhileStatementSyntax
                    || c is DoStatementSyntax || c is SwitchStatementSyntax || c is TryStatementSyntax || c is UsingStatementSyntax || c is LockStatementSyntax || c is ForEachVariableStatementSyntax;
                best = Math.Max(best, Nesting(c, nests ? depth + 1 : depth));
            }
            return best;
        }
        static int Lines(SyntaxNode n) { var s = n.GetLocation().GetLineSpan(); return s.EndLinePosition.Line - s.StartLinePosition.Line + 1; }

        int maxFileLines = 0;
        foreach (var t in trees)
        {
            var rootNode = t.GetRoot();
            var rel = Rel(t.FilePath);
            var types = rootNode.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Count();
            int mcount = 0, maxL = 0, maxC = 0;
            foreach (var m in rootNode.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().Cast<SyntaxNode>()
                .Concat(rootNode.DescendantNodes().OfType<AccessorDeclarationSyntax>().Where(a => a.Body != null || a.ExpressionBody != null))
                .Concat(rootNode.DescendantNodes().OfType<LocalFunctionStatementSyntax>()))
            {
                SyntaxNode body = m switch
                {
                    BaseMethodDeclarationSyntax b => (SyntaxNode)b.Body ?? b.ExpressionBody,
                    AccessorDeclarationSyntax a => (SyntaxNode)a.Body ?? a.ExpressionBody,
                    LocalFunctionStatementSyntax l => (SyntaxNode)l.Body ?? l.ExpressionBody,
                    _ => null
                };
                if (body == null) continue;
                mcount++;
                string name = m switch
                {
                    MethodDeclarationSyntax md => md.Identifier.Text,
                    ConstructorDeclarationSyntax cd => ".ctor",
                    AccessorDeclarationSyntax ad => (ad.Parent?.Parent as PropertyDeclarationSyntax)?.Identifier.Text + "." + ad.Keyword.Text,
                    LocalFunctionStatementSyntax lf => "local:" + lf.Identifier.Text,
                    OperatorDeclarationSyntax od => "op" + od.OperatorToken.Text,
                    _ => m.Kind().ToString()
                };
                var typeName = m.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "?";
                int lines = Lines(m), cc = Cyclomatic(body), nest = Nesting(body);
                int pars = m is BaseMethodDeclarationSyntax bm ? bm.ParameterList.Parameters.Count : m is LocalFunctionStatementSyntax lfs ? lfs.ParameterList.Parameters.Count : 0;
                maxL = Math.Max(maxL, lines); maxC = Math.Max(maxC, cc);
                methodRows.Add((rel, typeName, name, lines, cc, nest, pars, m.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
            int fileLines = t.GetText().Lines.Count;
            maxFileLines = Math.Max(maxFileLines, fileLines);
            fileRows.Add($"{rel},{fileLines},{types},{mcount},{maxL},{maxC}");
            perFileMetrics[rel] = new FileBaseline { Lines = fileLines, MaxMethodLines = maxL, MaxCyclomatic = maxC };
        }
        File.WriteAllLines(Path.Combine(outDir, "files.csv"), fileRows);
        var mlist = methodRows.ToList();
        File.WriteAllLines(Path.Combine(outDir, "methods.csv"), new[] { "file,line,type,method,lines,cyclomatic,nesting,params" }
            .Concat(mlist.OrderByDescending(x => x.lines).Select(x => $"{x.file},{x.startLine},{x.type},{x.name},{x.lines},{x.cc},{x.nest},{x.pars}")));

        var dist = new StringBuilder();
        dist.AppendLine($"methods with bodies: {mlist.Count}");
        foreach (var lim in new[] { 50, 100, 200, 400 }) dist.AppendLine($"  > {lim} lines: {mlist.Count(x => x.lines > lim)}");
        foreach (var lim in new[] { 10, 20, 40, 80 }) dist.AppendLine($"  cyclomatic > {lim}: {mlist.Count(x => x.cc > lim)}");
        foreach (var lim in new[] { 4, 6, 8 }) dist.AppendLine($"  nesting > {lim}: {mlist.Count(x => x.nest > lim)}");
        dist.AppendLine($"  params > 7: {mlist.Count(x => x.pars > 7)}");
        dist.AppendLine("\nTOP 40 BY CYCLOMATIC:");
        foreach (var x in mlist.OrderByDescending(x => x.cc).Take(40)) dist.AppendLine($"  cc={x.cc,4} lines={x.lines,4} nest={x.nest,2}  {x.type}.{x.name}  {x.file}:{x.startLine}");
        dist.AppendLine("\nTOP 40 BY LINES:");
        foreach (var x in mlist.OrderByDescending(x => x.lines).Take(40)) dist.AppendLine($"  lines={x.lines,4} cc={x.cc,4} nest={x.nest,2}  {x.type}.{x.name}  {x.file}:{x.startLine}");
        dist.AppendLine("\nTOP 25 BY NESTING:");
        foreach (var x in mlist.OrderByDescending(x => x.nest).ThenByDescending(x => x.cc).Take(25)) dist.AppendLine($"  nest={x.nest,2} cc={x.cc,4} lines={x.lines,4}  {x.type}.{x.name}  {x.file}:{x.startLine}");
        dist.AppendLine("\nTOP 20 BY PARAMS:");
        foreach (var x in mlist.OrderByDescending(x => x.pars).Take(20)) dist.AppendLine($"  params={x.pars,2} lines={x.lines,4}  {x.type}.{x.name}  {x.file}:{x.startLine}");
        File.WriteAllText(Path.Combine(outDir, "methods_summary.txt"), dist.ToString());
        Log("method metrics done");

        var declared = new ConcurrentDictionary<ISymbol, (string file, int line, SyntaxNode node)>(SymbolEqualityComparer.Default);
        var referencedFrom = new ConcurrentDictionary<ISymbol, ConcurrentBag<(SyntaxTree tree, TextSpan span)>>(SymbolEqualityComparer.Default);
        var godotUse = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();
        var typeDecl = new ConcurrentDictionary<INamedTypeSymbol, ConcurrentBag<(string file, int lines)>>(SymbolEqualityComparer.Default);
        var staticRows = new ConcurrentBag<string>();
        var staticCountByFile = new ConcurrentDictionary<string, int>();
        var magic = new ConcurrentDictionary<string, int>();
        var magicValues = new ConcurrentDictionary<string, int>();

        void AddRef(ISymbol s, SyntaxNode at)
        {
            if (s == null) return;
            var list = new List<ISymbol> { s.OriginalDefinition };
            if (s is IMethodSymbol ms)
            {
                if (ms.ReducedFrom != null) list.Add(ms.ReducedFrom.OriginalDefinition);
                if (ms.PartialDefinitionPart != null) list.Add(ms.PartialDefinitionPart);
                if (ms.PartialImplementationPart != null) list.Add(ms.PartialImplementationPart);
                if (ms.AssociatedSymbol != null) list.Add(ms.AssociatedSymbol.OriginalDefinition);
                if (ms.ConstructedFrom != null) list.Add(ms.ConstructedFrom.OriginalDefinition);
            }
            if (s is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor) list.Add(ctor.ContainingType.OriginalDefinition);
            foreach (var x in list)
                referencedFrom.GetOrAdd(x, _ => new()).Add((at.SyntaxTree, at.Span));
        }

        Parallel.ForEach(trees, t =>
        {
            var model = comp.GetSemanticModel(t);
            var rootNode = t.GetRoot();
            var rel = Rel(t.FilePath);
            bool isSim = rel.StartsWith("Scripts/Simulation/");
            foreach (var n in rootNode.DescendantNodes())
            {
                switch (n)
                {
                    case BaseTypeDeclarationSyntax td:
                        if (model.GetDeclaredSymbol(td) is INamedTypeSymbol nts)
                        {
                            declared.TryAdd(nts, (rel, td.GetLocation().GetLineSpan().StartLinePosition.Line + 1, td));
                            typeDecl.GetOrAdd(nts, _ => new()).Add((rel, Lines(td)));
                        }
                        break;
                    case DelegateDeclarationSyntax dd:
                        if (model.GetDeclaredSymbol(dd) is ISymbol dsy) declared.TryAdd(dsy, (rel, dd.GetLocation().GetLineSpan().StartLinePosition.Line + 1, dd));
                        break;
                    case MethodDeclarationSyntax md:
                        if (model.GetDeclaredSymbol(md) is ISymbol msy) declared.TryAdd(msy, (rel, md.GetLocation().GetLineSpan().StartLinePosition.Line + 1, md));
                        break;
                    case PropertyDeclarationSyntax pd:
                        if (model.GetDeclaredSymbol(pd) is ISymbol psy) declared.TryAdd(psy, (rel, pd.GetLocation().GetLineSpan().StartLinePosition.Line + 1, pd));
                        if (pd.Modifiers.Any(SyntaxKind.StaticKeyword) && pd.AccessorList != null && pd.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) && !a.Modifiers.Any(SyntaxKind.InitKeyword)) && pd.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null))
                        {
                            staticRows.Add($"{rel},{pd.GetLocation().GetLineSpan().StartLinePosition.Line + 1},{pd.Ancestors().OfType<BaseTypeDeclarationSyntax>().First().Identifier.Text},{pd.Identifier.Text},static auto-property with setter,\"{pd.Type}\"");
                            staticCountByFile.AddOrUpdate(rel, 1, (_, v) => v + 1);
                        }
                        break;
                    case FieldDeclarationSyntax fd:
                        foreach (var v in fd.Declaration.Variables)
                            if (model.GetDeclaredSymbol(v) is IFieldSymbol fsy)
                            {
                                declared.TryAdd(fsy, (rel, v.GetLocation().GetLineSpan().StartLinePosition.Line + 1, v));
                                if (fsy.IsStatic && !fsy.IsConst)
                                {
                                    var tn = fsy.Type.ToDisplayString();
                                    bool mutableColl = fsy.Type is IArrayTypeSymbol || Regex.IsMatch(tn, @"(Dictionary|List|HashSet|Queue|Stack|SortedSet|SortedDictionary|StringBuilder|Random|ConcurrentBag|ConcurrentQueue)\b");
                                    string kind = !fsy.IsReadOnly ? "static non-readonly field" : mutableColl ? "static readonly mutable container" : null;
                                    if (kind != null)
                                    {
                                        var attrs = string.Join(" ", fsy.GetAttributes().Select(a => a.AttributeClass?.Name));
                                        staticRows.Add($"{rel},{v.GetLocation().GetLineSpan().StartLinePosition.Line + 1},{fsy.ContainingType.Name},{fsy.Name},{kind}{(attrs.Length > 0 ? " [" + attrs + "]" : "")},\"{tn.Replace("\"", "'")}\"");
                                        staticCountByFile.AddOrUpdate(rel, 1, (_, c) => c + 1);
                                    }
                                }
                            }
                        break;
                    case EventFieldDeclarationSyntax ef:
                        foreach (var v in ef.Declaration.Variables)
                            if (model.GetDeclaredSymbol(v) is IEventSymbol esy && esy.IsStatic)
                            {
                                staticRows.Add($"{rel},{v.GetLocation().GetLineSpan().StartLinePosition.Line + 1},{esy.ContainingType.Name},{esy.Name},static event,\"{esy.Type}\"");
                                staticCountByFile.AddOrUpdate(rel, 1, (_, c) => c + 1);
                            }
                        break;
                    case SimpleNameSyntax sn:
                        {
                            var si = model.GetSymbolInfo(sn);
                            var s = si.Symbol ?? si.CandidateSymbols.FirstOrDefault();
                            if (s != null)
                            {
                                AddRef(s, sn);
                                foreach (var c in si.CandidateSymbols) AddRef(c, sn);
                                if (isSim)
                                {
                                    var ns = (s as INamespaceSymbol)?.ToDisplayString() ?? s.ContainingNamespace?.ToDisplayString() ?? "";
                                    if (ns == "Godot" || ns.StartsWith("Godot."))
                                        godotUse.GetOrAdd(rel, _ => new())[s.Name] = godotUse[rel].GetValueOrDefault(s.Name) + 1;
                                }
                            }
                        }
                        break;
                    case BaseObjectCreationExpressionSyntax oc:
                        AddRef(model.GetSymbolInfo(oc).Symbol, oc); break;
                    case ConstructorInitializerSyntax ci:
                        AddRef(model.GetSymbolInfo(ci).Symbol, ci); break;
                    case PrimaryConstructorBaseTypeSyntax pc:
                        AddRef(model.GetSymbolInfo(pc).Symbol, pc); break;
                    case ElementAccessExpressionSyntax ea:
                        AddRef(model.GetSymbolInfo(ea).Symbol, ea); break;
                    case ForEachStatementSyntax fe:
                        { var info = model.GetForEachStatementInfo(fe); AddRef(info.GetEnumeratorMethod, fe); } break;
                    case BinaryExpressionSyntax be:
                        AddRef(model.GetSymbolInfo(be).Symbol, be); break;
                    case LiteralExpressionSyntax lit when isSim && lit.IsKind(SyntaxKind.NumericLiteralExpression):
                        {
                            var txt = lit.Token.ValueText;
                            var val = Convert.ToDouble(lit.Token.Value);
                            if (val == 0 || val == 1 || val == 2) break;
                            var inDefines = lit.Ancestors().OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.Text == "Defines");
                            var inConst = lit.Ancestors().OfType<FieldDeclarationSyntax>().Any(f => f.Modifiers.Any(SyntaxKind.ConstKeyword) || f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) && f.Modifiers.Any(SyntaxKind.StaticKeyword))
                                || lit.Ancestors().OfType<LocalDeclarationStatementSyntax>().Any(l => l.IsConst) || lit.Ancestors().OfType<AttributeSyntax>().Any()
                                || lit.Ancestors().OfType<EnumMemberDeclarationSyntax>().Any();
                            if (inDefines || inConst) break;
                            if (val > 100000) break;
                            magic.AddOrUpdate(rel, 1, (_, c) => c + 1);
                            magicValues.AddOrUpdate(txt, 1, (_, c) => c + 1);
                        }
                        break;
                }
            }
        });
        Log($"semantic walk done: declared={declared.Count} referenced={referencedFrom.Count}");

        foreach (var (rel, fb) in perFileMetrics)
        {
            fb.StaticMutable = staticCountByFile.GetValueOrDefault(rel);
            fb.MagicNumbers = magic.GetValueOrDefault(rel);
        }

        File.WriteAllLines(Path.Combine(outDir, "statics.csv"), new[] { "file,line,type,member,kind,clrType" }.Concat(staticRows.OrderBy(x => x)));
        File.WriteAllLines(Path.Combine(outDir, "godot_in_simulation.csv"), new[] { "file,uses,symbols" }.Concat(godotUse.OrderByDescending(kv => kv.Value.Values.Sum()).Select(kv => $"{kv.Key},{kv.Value.Values.Sum()},\"{string.Join(" ", kv.Value.OrderByDescending(x => x.Value).Select(x => x.Key + ":" + x.Value))}\"")));
        File.WriteAllLines(Path.Combine(outDir, "magic_numbers.csv"), new[] { "file,count" }.Concat(magic.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key},{kv.Value}"))
            .Append("").Append("value,count").Concat(magicValues.OrderByDescending(kv => kv.Value).Take(60).Select(kv => $"{kv.Key},{kv.Value}")));

        var typeRows = typeDecl.Select(kv => (name: kv.Key.ToDisplayString(), files: kv.Value.Select(x => x.file).Distinct().Count(), lines: kv.Value.Sum(x => x.lines),
                members: kv.Key.GetMembers().Count(m => !m.IsImplicitlyDeclared), dirs: string.Join(" ", kv.Value.Select(x => Path.GetDirectoryName(x.file)?.Replace('\\', '/')).Distinct())))
            .Where(x => x.name.Count(c => c == '.') <= 2).OrderByDescending(x => x.lines).ToList();
        File.WriteAllLines(Path.Combine(outDir, "types.csv"), new[] { "type,files,lines,members,dirs" }.Concat(typeRows.Take(200).Select(x => $"{x.name},{x.files},{x.lines},{x.members},{x.dirs}")));

        bool IsUsed(ISymbol s, SyntaxNode declNode)
        {
            if (!referencedFrom.TryGetValue(s, out var bag)) return false;
            foreach (var (tree, span) in bag)
            {
                if (tree == declNode.SyntaxTree && declNode.Span.Contains(span)) continue;
                return true;
            }
            return false;
        }
        bool ImplementsInterface(ISymbol s)
        {
            var t = s.ContainingType; if (t == null) return false;
            foreach (var i in t.AllInterfaces)
                foreach (var im in i.GetMembers())
                    if (SymbolEqualityComparer.Default.Equals(t.FindImplementationForInterfaceMember(im), s)) return true;
            return false;
        }
        static bool HasAttr(ISymbol s, params string[] names) =>
            s.GetAttributes().Any(a =>
            {
                var n = a.AttributeClass?.Name;
                return n != null && names.Contains(n, StringComparer.Ordinal);
            });
        static bool IsToolOrRuntimeEntry(ISymbol s)
        {
            if (HasAttr(s, "ToolHookAttribute", "ToolHook", "ModuleInitializerAttribute", "ModuleInitializer")) return true;
            if (s is INamedTypeSymbol nt && nt.GetMembers().Any(m => HasAttr(m, "ToolHookAttribute", "ToolHook", "ModuleInitializerAttribute", "ModuleInitializer")))
                return true;
            return false;
        }

        var dead = new List<(string kind, string name, string container, string access, string file, int line, string flags)>();
        foreach (var (s, d) in declared)
        {
            if (s.IsImplicitlyDeclared) continue;
            if (IsToolOrRuntimeEntry(s)) continue;
            if (IsUsed(s, d.node)) continue;
            var flags = new List<string>();
            var attrs = s.GetAttributes().Select(a => a.AttributeClass?.Name ?? "").Where(a => a.Length > 0).ToList();
            if (attrs.Count > 0) flags.Add("attr:" + string.Join("|", attrs));
            switch (s)
            {
                case IMethodSymbol m:
                    if (m.IsOverride || m.IsExtern || m.MethodKind != MethodKind.Ordinary || m.Name.StartsWith("_") || m.Name == "Main") continue;
                    if (ImplementsInterface(m)) continue;
                    if (m.PartialImplementationPart != null || m.PartialDefinitionPart != null) flags.Add("partial");
                    if (m.IsVirtual || m.IsAbstract) flags.Add("virtual");
                    dead.Add(("method", m.Name, m.ContainingType.ToDisplayString(), m.DeclaredAccessibility.ToString(), d.file, d.line, string.Join(";", flags)));
                    break;
                case INamedTypeSymbol t:
                    dead.Add(("type", t.Name, t.ContainingSymbol.ToDisplayString(), t.DeclaredAccessibility.ToString(), d.file, d.line, string.Join(";", flags.Append(t.BaseType?.Name ?? ""))));
                    break;
                case IPropertySymbol p:
                    if (p.IsOverride || ImplementsInterface(p)) continue;
                    dead.Add(("property", p.Name, p.ContainingType.ToDisplayString(), p.DeclaredAccessibility.ToString(), d.file, d.line, string.Join(";", flags)));
                    break;
                case IFieldSymbol f:
                    if (f.ContainingType.TypeKind == TypeKind.Enum) continue;
                    dead.Add(("field", f.Name, f.ContainingType.ToDisplayString(), f.DeclaredAccessibility.ToString() + (f.IsConst ? " const" : ""), d.file, d.line, string.Join(";", flags)));
                    break;
                default:
                    dead.Add((s.Kind.ToString(), s.Name, s.ContainingSymbol?.ToDisplayString() ?? "", s.DeclaredAccessibility.ToString(), d.file, d.line, string.Join(";", flags)));
                    break;
            }
        }
        Log($"dead candidates (semantic): {dead.Count}");

        var names = dead.Select(x => x.name).ToHashSet();
        var tokenRx = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
        var corpora = new Dictionary<string, Func<IEnumerable<string>>>
        {
            ["scriptsText"] = () => files,
            ["tools"] = () => Directory.EnumerateFiles(Path.Combine(root, "Tools"), "*.cs", SearchOption.AllDirectories).Where(p => !Skip(p) && !p.Contains(@"\ExportVerification\")),
            ["mods"] = () => Directory.Exists(Path.Combine(root, "mods")) ? Directory.EnumerateFiles(Path.Combine(root, "mods"), "*.*", SearchOption.AllDirectories).Where(p => !Skip(p) && Regex.IsMatch(p, @"\.(cs|fs|json|lua|luau|gd|txt|yaml|yml)$")) : Enumerable.Empty<string>(),
            ["data"] = () => Directory.Exists(Path.Combine(root, "game")) ? Directory.EnumerateFiles(Path.Combine(root, "game"), "*.*", SearchOption.AllDirectories).Where(p => Regex.IsMatch(p, @"\.(json|yaml|yml|txt|csv)$") && new FileInfo(p).Length < 30_000_000) : Enumerable.Empty<string>(),
            ["scenes"] = () => Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly).Where(p => p.EndsWith(".godot") || p.EndsWith(".cfg"))
                .Concat(new[] { "Scenes", "addons", "Scripts" }.Where(d => Directory.Exists(Path.Combine(root, d))).SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.*", SearchOption.AllDirectories)).Where(p => !Skip(p) && Regex.IsMatch(p, @"\.(tscn|tres|gd|gdshader)$"))),
        };
        var hits = new Dictionary<string, ConcurrentDictionary<string, int>>();
        foreach (var (cname, enumerate) in corpora)
        {
            var counter = new ConcurrentDictionary<string, int>();
            var list = enumerate().ToList();
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 8 }, p =>
            {
                string text; try { text = texts.TryGetValue(p, out var tx) ? tx : File.ReadAllText(p); } catch { return; }
                if (cname == "scriptsText")
                {
                    foreach (Match q in Regex.Matches(text, "\"[^\"\\r\\n]*\""))
                        foreach (Match m in tokenRx.Matches(q.Value)) if (names.Contains(m.Value)) counter.AddOrUpdate(m.Value, 1, (_, v) => v + 1);
                    return;
                }
                foreach (Match m in tokenRx.Matches(text)) if (names.Contains(m.Value)) counter.AddOrUpdate(m.Value, 1, (_, v) => v + 1);
                if (cname is "data" or "mods")
                    foreach (Match m in Regex.Matches(text, @"[a-z][a-z0-9]*(_[a-z0-9]+)+"))
                    {
                        var pascal = string.Concat(m.Value.Split('_').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : ""));
                        if (names.Contains(pascal)) counter.AddOrUpdate(pascal, 1, (_, v) => v + 1);
                    }
            });
            hits[cname] = counter;
            Log($"corpus {cname}: {list.Count} files");
        }

        var reflection = new StringBuilder();
        foreach (var f in files)
        {
            var lines = texts[f].Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"Activator\.CreateInstance|Type\.GetType\(|\.GetMethod\(|\.GetProperty\(|\.GetField\(|GetCustomAttribute|Assembly\.GetTypes|\.GetTypes\(\)|nameof\(|Callable\(this,|\.Call\(""|HasMethod\(|Connect\(""|new StringName\("))
                    if (!lines[i].Contains("nameof(")) reflection.AppendLine($"{Rel(f)}:{i + 1}: {lines[i].Trim()}");
        }
        File.WriteAllText(Path.Combine(outDir, "reflection_uses.txt"), reflection.ToString());

        var deadRows = new List<string> { "kind,name,container,access,file,line,flags,strInScripts,tools,mods,data,scenes,verdict" };
        int deadOnlyCount = 0;
        foreach (var x in dead.OrderBy(x => x.file).ThenBy(x => x.line))
        {
            int g(string c) => hits[c].GetValueOrDefault(x.name);
            int sS = g("scriptsText"), tl = g("tools"), md = g("mods"), da = g("data"), sc = g("scenes");
            string verdict = (sS + md + da + sc) > 0 ? "data-or-string-use" : tl > 0 ? "test-or-tool-only" : x.flags.Contains("attr:") ? "attributed-check" : "dead";
            if (verdict == "dead") deadOnlyCount++;
            deadRows.Add($"{x.kind},{x.name},\"{x.container}\",{x.access},{x.file},{x.line},\"{x.flags}\",{sS},{tl},{md},{da},{sc},{verdict}");
        }
        File.WriteAllLines(Path.Combine(outDir, "dead_candidates.csv"), deadRows);
        Log("dead code report written");

        const int W = 100;
        var tokId = new ConcurrentDictionary<string, int>();
        var fileToks = new List<(string file, int[] ids, int[] lines)>();
        foreach (var t in trees)
        {
            var toks = t.GetRoot().DescendantTokens().Where(k => !k.IsKind(SyntaxKind.None)).ToList();
            toks = toks.Where(k => k.Parent is not UsingDirectiveSyntax && k.Parent?.Parent is not UsingDirectiveSyntax && k.Parent is not QualifiedNameSyntax || k.Parent.Ancestors().All(a => a is not UsingDirectiveSyntax)).ToList();
            var ids = new int[toks.Count]; var lns = new int[toks.Count];
            for (int i = 0; i < toks.Count; i++)
            {
                var k = toks[i];
                string norm = k.Kind() switch
                {
                    SyntaxKind.NumericLiteralToken => "#num",
                    SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken => "#str",
                    _ => k.Text
                };
                ids[i] = tokId.GetOrAdd(norm, _ => tokId.Count);
                lns[i] = k.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            }
            fileToks.Add((Rel(t.FilePath), ids, lns));
        }
        var windows = new Dictionary<ulong, List<(int f, int pos)>>();
        for (int f = 0; f < fileToks.Count; f++)
        {
            var ids = fileToks[f].ids;
            if (ids.Length < W) continue;
            const ulong B = 1_000_003UL; ulong pow = 1; for (int i = 0; i < W - 1; i++) pow *= B;
            ulong h = 0;
            for (int i = 0; i < W; i++) h = h * B + (ulong)ids[i];
            for (int i = 0; ; i++)
            {
                if (!windows.TryGetValue(h, out var l)) windows[h] = l = new();
                l.Add((f, i));
                if (i + W >= ids.Length) break;
                h = (h - (ulong)ids[i] * pow) * B + (ulong)ids[i + W];
            }
        }
        var clones = new List<(int fa, int a, int fb, int b, int len)>();
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var (h, l) in windows)
        {
            if (l.Count < 2 || l.Count > 40) continue;
            for (int i = 0; i < l.Count; i++)
                for (int j = i + 1; j < l.Count; j++)
                {
                    var (fa, a) = l[i]; var (fb, b) = l[j];
                    if (fa == fb && Math.Abs(a - b) < W) continue;
                    var ia = fileToks[fa].ids; var ib = fileToks[fb].ids;
                    if (a > 0 && b > 0 && ia[a - 1] == ib[b - 1]) continue;
                    int len = 0; while (a + len < ia.Length && b + len < ib.Length && ia[a + len] == ib[b + len] && !(fa == fb && a + len == b)) len++;
                    if (len < W) continue;
                    if (seen.Add((fa, a, fb, b))) clones.Add((fa, a, fb, b, len));
                }
        }
        var perFileDup = new Dictionary<string, HashSet<int>>();
        var dupOut = new StringBuilder();
        foreach (var c in clones.OrderByDescending(c => c.len))
        {
            var A = fileToks[c.fa]; var Bf = fileToks[c.fb];
            int la0 = A.lines[c.a], la1 = A.lines[c.a + c.len - 1], lb0 = Bf.lines[c.b], lb1 = Bf.lines[c.b + c.len - 1];
            if (la1 - la0 + 1 < 10) continue;
            dupOut.AppendLine($"{c.len,5} tok {la1 - la0 + 1,4} lines  {A.file}:{la0}-{la1}  ==  {Bf.file}:{lb0}-{lb1}");
            foreach (var (fn, s0, s1) in new[] { (A.file, la0, la1), (Bf.file, lb0, lb1) })
            {
                if (!perFileDup.TryGetValue(fn, out var set)) perFileDup[fn] = set = new();
                for (int z = s0; z <= s1; z++) set.Add(z);
            }
        }
        int dupLinesTotal = perFileDup.Values.Sum(s => s.Count);
        File.WriteAllText(Path.Combine(outDir, "duplicates.txt"), $"clone pairs (>= {W} identical tokens, literals normalised, >= 10 lines): {dupOut.ToString().Split('\n').Length - 1}\nlines inside some clone: {dupLinesTotal}\n\nPER FILE (lines in clones):\n"
            + string.Join("\n", perFileDup.OrderByDescending(kv => kv.Value.Count).Take(40).Select(kv => $"{kv.Value.Count,5}  {kv.Key}")) + "\n\nPAIRS:\n" + dupOut);
        Log("duplicates done");

        var naming = new StringBuilder();
        int fUnderscore = 0, fCamel = 0, fPascal = 0, fM = 0;
        var mismatched = new List<string>();
        var nsCount = new Dictionary<string, int>();
        var lowerMethods = new List<string>();
        foreach (var t in trees)
        {
            var r = t.GetRoot();
            var rel = Rel(t.FilePath);
            foreach (var fd in r.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (fd.Modifiers.Any(SyntaxKind.PublicKeyword) || fd.Modifiers.Any(SyntaxKind.InternalKeyword) || fd.Modifiers.Any(SyntaxKind.ConstKeyword) || fd.Modifiers.Any(SyntaxKind.ProtectedKeyword)) continue;
                bool isStaticRo = fd.Modifiers.Any(SyntaxKind.StaticKeyword) && fd.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
                if (isStaticRo) continue;
                foreach (var v in fd.Declaration.Variables)
                {
                    var n = v.Identifier.Text;
                    if (n.StartsWith("m_")) fM++; else if (n.StartsWith("_")) fUnderscore++; else if (char.IsLower(n[0])) fCamel++; else fPascal++;
                }
            }
            foreach (var md in r.DescendantNodes().OfType<MethodDeclarationSyntax>())
                if (char.IsLower(md.Identifier.Text[0])) lowerMethods.Add($"{rel}:{md.Identifier.Text}");
            var top = r.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(x => x.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax);
            var fileStem = Path.GetFileNameWithoutExtension(t.FilePath);
            if (top != null && !fileStem.StartsWith(top.Identifier.Text) && !(top.Modifiers.Any(SyntaxKind.PartialKeyword)))
                mismatched.Add($"{rel} -> {top.Identifier.Text}");
            var ns = r.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "(global)";
            nsCount[ns] = nsCount.GetValueOrDefault(ns) + 1;
        }
        naming.AppendLine($"private instance/mutable fields: _camel={fUnderscore} camel={fCamel} Pascal={fPascal} m_={fM}");
        naming.AppendLine($"methods starting lowercase: {lowerMethods.Count}"); foreach (var x in lowerMethods.Take(30)) naming.AppendLine("  " + x);
        naming.AppendLine("namespaces:"); foreach (var kv in nsCount.OrderByDescending(k => k.Value)) naming.AppendLine($"  {kv.Value,4} {kv.Key}");
        naming.AppendLine($"non-partial files whose first type is not the file name: {mismatched.Count}"); foreach (var x in mismatched) naming.AppendLine("  " + x);
        var suffixes = new Dictionary<string, int>();
        foreach (var f in files.Select(Path.GetFileNameWithoutExtension))
        {
            var m = Regex.Match(f, @"(Core|Manager|System|Catalog|Service|Rules|Registry|Helper|Helpers|Util|Utils|Controller|Presentation|UI|Screen|Panel|Data|State|Ledger|Report|Engine|Director|Planner|Kernel)$");
            if (m.Success) suffixes[m.Value] = suffixes.GetValueOrDefault(m.Value) + 1;
        }
        naming.AppendLine("file-name suffix vocabulary:"); foreach (var kv in suffixes.OrderByDescending(k => k.Value)) naming.AppendLine($"  {kv.Value,4} {kv.Key}");
        File.WriteAllText(Path.Combine(outDir, "naming.txt"), naming.ToString());

        int godotUsesTotal = godotUse.Values.Sum(v => v.Values.Sum());
        int simStaticMutable = staticRows.Count(r => r.StartsWith("Scripts/Simulation/", StringComparison.Ordinal));
        int totalLines = trees.Sum(t => t.GetText().Lines.Count);
        var sum = new StringBuilder();
        sum.AppendLine($"files {files.Count}, lines {totalLines}, compile errors {diags.Count}");
        sum.AppendLine($"types (merged partials) top 30 by lines:");
        foreach (var x in typeRows.Take(30)) sum.AppendLine($"  {x.lines,6} lines {x.files,4} files {x.members,5} members  {x.name}  [{x.dirs}]");
        sum.AppendLine($"partial types spread over >= 5 files: {typeRows.Count(x => x.files >= 5)}");
        sum.AppendLine($"dead candidates by verdict: " + string.Join(", ", deadRows.Skip(1).GroupBy(r => r.Split(',').Last()).Select(g => g.Key + "=" + g.Count())));
        sum.AppendLine($"dead candidates by kind (verdict=dead): " + string.Join(", ", deadRows.Skip(1).Where(r => r.EndsWith(",dead")).GroupBy(r => r.Split(',')[0]).Select(g => g.Key + "=" + g.Count())));
        sum.AppendLine($"static mutable rows: {staticRows.Count}; Simulation: {simStaticMutable}");
        sum.AppendLine($"Godot symbol uses in Scripts/Simulation: files={godotUse.Count} uses={godotUsesTotal}");
        sum.AppendLine($"magic numbers in Simulation outside Defines/const: {magic.Values.Sum()} in {magic.Count} files");
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), sum.ToString());
        Log("all done");

        return new AuditBaseline
        {
            CompileErrors = diags.Count,
            GodotInSimulationUses = godotUsesTotal,
            MaxFileLines = maxFileLines,
            MaxMethodLines = mlist.Count == 0 ? 0 : mlist.Max(m => m.lines),
            MaxCyclomatic = mlist.Count == 0 ? 0 : mlist.Max(m => m.cc),
            SimulationStaticMutable = simStaticMutable,
            DeadCandidates = deadOnlyCount,
            TotalFiles = files.Count,
            TotalLines = totalLines,
            Files = perFileMetrics
        };
    }
}
