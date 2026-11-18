using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Second pass over the strict dead-code candidates: whole-word occurrences in every place a
// name can be reached from outside the C# compile set, including running branch worktrees.
static class Verify
{
    public static void Run(string root)
    {
        var outDir = Path.Combine(root, "tmp_logs", "code-audit", "out");
        var candidatesPath = Path.Combine(outDir, "dead_candidates.csv");
        var rows = File.ReadAllLines(candidatesPath).Skip(1)
            .Select(SplitCsv).Where(c => c[^1] == "dead" || c[^1] == "test-or-tool-only").ToList();
        var names = rows.Select(r => r[1]).ToHashSet();
        bool Skip(string p) => p.Contains(@"\bin\") || p.Contains(@"\obj\") || p.Contains(@"\.godot\") || p.Contains(@"\node_modules\") || p.Contains(@"\.git\");
        IEnumerable<string> Enum(string dir, string pattern) => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(p => !Skip(p) && Regex.IsMatch(p, pattern, RegexOptions.IgnoreCase) && new FileInfo(p).Length < 30_000_000)
            : Enumerable.Empty<string>();
        var corpora = new Dictionary<string, List<string>>
        {
            ["game_cs"] = Enum(Path.Combine(root, "Scripts"), @"\.cs$").Concat(Enum(Path.Combine(root, "Tools", "ExportVerification"), @"\.cs$")).ToList(),
            ["tools"] = Enum(Path.Combine(root, "Tools"), @"\.(cs|gd|ps1|mjs|js|py|bat|cmd|fs)$").Where(p => !p.Contains(@"\ExportVerification\")).ToList(),
            ["mods_addons"] = Enum(Path.Combine(root, "mods"), @"\.(cs|fs|json|lua|luau|gd|txt|yaml|yml|kt|java)$").Concat(Enum(Path.Combine(root, "addons"), @"\.(gd|cs|tscn|json)$")).Concat(Enum(Path.Combine(root, "LanguageKit"), @"\.(cs|fs|gd|json|lua|luau)$")).ToList(),
            ["data_scenes"] = Enum(Path.Combine(root, "game"), @"\.(json|yaml|yml|txt|csv)$").Concat(Enum(Path.Combine(root, "Scenes"), @"\.(tscn|tres)$"))
                .Concat(Directory.EnumerateFiles(root, "*.*").Where(p => Regex.IsMatch(p, @"\.(tscn|tres|godot|cfg|json)$"))).ToList(),
            ["docs"] = Enum(Path.Combine(root, "docs"), @"\.md$").ToList(),
        };

        var branchFiles = new List<string>();
        var parentDir = Path.GetDirectoryName(root);
        if (parentDir is not null)
        {
            foreach (var wt in new[] { "dof-conquest", "dof-longrange", "dof-pockets", "dof-deals", "dof-fw-saves", "dof-perf3", "dof-mapart", "dof-newscreen", "dof-refactor-split" })
            {
                var dir = Path.Combine(parentDir, wt);
                if (!Directory.Exists(dir)) continue;
                string Git(string a)
                {
                    var psi = new ProcessStartInfo("git", $"-C \"{dir}\" {a}") { RedirectStandardOutput = true, UseShellExecute = false };
                    using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o;
                }
                var mb = Git("merge-base HEAD EntertAlpha").Trim();
                if (string.IsNullOrEmpty(mb)) continue;
                var changed = Git($"diff --name-only {mb}").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Concat(Git("ls-files --others --exclude-standard").Split('\n', StringSplitOptions.RemoveEmptyEntries));
                branchFiles.AddRange(changed.Select(c => Path.Combine(dir, c.Trim())).Where(f => File.Exists(f) && Regex.IsMatch(f, @"\.(cs|json|gd|fs)$")));
            }
        }
        corpora["branches"] = branchFiles;

        var tokenRx = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
        var counts = new Dictionary<string, ConcurrentDictionary<string, ConcurrentBag<string>>>();
        foreach (var (cname, list) in corpora)
        {
            var c = new ConcurrentDictionary<string, ConcurrentBag<string>>();
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
            {
                string text; try { text = File.ReadAllText(f); } catch { return; }
                int line = 1, last = 0;
                foreach (Match m in tokenRx.Matches(text))
                {
                    if (!names.Contains(m.Value)) continue;
                    for (int i = last; i < m.Index; i++) if (text[i] == '\n') line++;
                    last = m.Index;
                    var rel = parentDir is not null ? Path.GetRelativePath(parentDir, f) : f;
                    c.GetOrAdd(m.Value, _ => new()).Add($"{rel.Replace('\\', '/')}:{line}");
                }
            });
            counts[cname] = c;
            Console.WriteLine($"{cname}: {list.Count} files");
        }
        var sb = new StringBuilder("kind,name,container,file,line,verdict,game_cs,tools,mods_addons,data_scenes,branches,docs,where\n");
        foreach (var r in rows)
        {
            var n = r[1];
            int k(string c) => counts[c].TryGetValue(n, out var b) ? b.Count : 0;
            var where = string.Join(" | ", new[] { "tools", "mods_addons", "data_scenes", "branches" }
                .SelectMany(c => counts[c].TryGetValue(n, out var b) ? b.Take(3) : Enumerable.Empty<string>()));
            sb.AppendLine($"{r[0]},{n},\"{r[2]}\",{r[4]},{r[5]},{r[^1]},{k("game_cs")},{k("tools")},{k("mods_addons")},{k("data_scenes")},{k("branches")},{k("docs")},\"{where}\"");
        }
        File.WriteAllText(Path.Combine(outDir, "dead_verify.csv"), sb.ToString());
        Console.WriteLine("written " + Path.Combine(outDir, "dead_verify.csv"));
    }

    static string[] SplitCsv(string line)
    {
        var res = new List<string>(); var cur = new StringBuilder(); bool q = false;
        foreach (var ch in line)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ',' && !q) { res.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        res.Add(cur.ToString());
        return res.ToArray();
    }
}
