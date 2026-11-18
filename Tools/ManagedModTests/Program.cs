using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using DealOrForce.Modding;
using DealOrForce.Simulation;

// Two ways to run this, same checks:
//
//   dotnet run --project Tools/ManagedModTests/ManagedModTests.csproj -c Release
//     builds the game project and the F# template next to this test (competes with an open
//     editor, because it compiles DealOrForce.csproj).
//
//   powershell -File Tools/ManagedModTests/run-corecode-load.ps1   (prints CORECODE_LOAD_PASS)
//     the --corecode-load-only path: the F# template is compiled against an exported game's
//     DealOrForce.dll (-p:GameAssembly), laid out as a mod package with its manifest, and this
//     test loads it the way ModLoader.LoadCsharp does - manifest path through ModCodePaths,
//     a private ManagedModLoadContext, IGlobalityMod discovery - then unloads it and proves the
//     context was collected. Nothing here starts Godot.
static class Program
{
    static string _gameDirectory;

    static int Main(string[] args)
    {
        var gameAssembly = Value(args, "--game-assembly") ?? typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "GameAssembly")?.Value;
        if (!string.IsNullOrEmpty(gameAssembly))
        {
            // The exported game is the host: its own DealOrForce.dll, GodotSharp.dll and their
            // dependencies come from its data folder, as they do in the shipped executable.
            _gameDirectory = Path.GetDirectoryName(Path.GetFullPath(gameAssembly));
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                var candidate = Path.Combine(_gameDirectory, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };
        }
        var corecodeOnly = args.Contains("--corecode-load-only");
        var package = Value(args, "--package");
        if (corecodeOnly && package is null) throw new ArgumentException("--corecode-load-only needs --package <mod directory>.");
        var weak = Run(package);
        for (var i = 0; weak.IsAlive && i < 20; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        if (weak.IsAlive) throw new Exception("F# mod load context was not collected after Unload: something still holds a mod type");
        Console.WriteLine("PASS F#: unloaded load context is collected (no host reference keeps the mod alive)");
        if (corecodeOnly) Console.WriteLine($"CORECODE_LOAD_PASS language=fsharp host={HostLabel()}");
        return 0;
    }

    static string Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static string HostLabel() => _gameDirectory is null ? "project-reference" : "exported:" + _gameDirectory;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference Run(string package)
    {
        string path;
        if (package is null) path = Path.Combine(AppContext.BaseDirectory, "ExampleFSharpMod.dll");
        else
        {
            // The same path rules the loader applies: corecode_assembly must stay inside corecode/.
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "manifest.json")));
            var root = manifest.RootElement;
            string Field(string name) => root.TryGetProperty(name, out var value) ? value.GetString() : "";
            var relative = ModCodePaths.AssemblyPath(Field("corecode_assembly"), Field("csharp_assembly"));
            path = Path.GetFullPath(Path.Combine(package, relative));
            if (!File.Exists(path)) throw new FileNotFoundException("manifest names a dll the package does not contain", path);
            if (File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "DealOrForce.dll")))
                throw new Exception("the package ships its own DealOrForce.dll; IGlobalityMod would become a second type");
            Console.WriteLine($"PASS F#: manifest corecode_assembly '{relative}' accepted by ModCodePaths");
        }

        var loadContext = new ManagedModLoadContext(path);
        var assembly = loadContext.LoadFromAssemblyPath(path);
        var entry = assembly.GetTypes().Single(t => !t.IsAbstract && !t.IsInterface && typeof(IGlobalityMod).IsAssignableFrom(t));
        var plugin = (IGlobalityMod)Activator.CreateInstance(entry);
        var log = new List<string>();
        var constructor = typeof(ModContext).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var context = (ModContext)constructor.Invoke(new object[] { "example_fsharp_mod", (Action<string>)log.Add });
        typeof(ModContext).GetProperty(nameof(ModContext.Campaign)).SetValue(context, Campaign.CreateBootstrap(101));
        plugin.OnLoad(context);
        plugin.OnCampaignCreated(context);
        plugin.OnDayAdvanced(context);
        if (log.Count != 3 || log[0] != "F# corecode loaded" || !log[1].StartsWith("F# campaign ready:") || !log[2].StartsWith("F# monthly observation:"))
            throw new Exception("F# hooks did not execute through the managed mod contract: " + string.Join(" | ", log));
        Console.WriteLine("PASS F#: reflection discovers and instantiates IGlobalityMod from compiled DLL");
        Console.WriteLine("PASS F#: OnLoad, OnCampaignCreated and OnDayAdvanced execute with the actual ModContext");
        if (AssemblyLoadContext.GetLoadContext(entry.Assembly) != loadContext)
            throw new Exception("F# plugin did not load in its private context");
        if (AssemblyLoadContext.GetLoadContext(typeof(IGlobalityMod).Assembly) == loadContext)
            throw new Exception("the host contract was loaded inside the mod context");
        Console.WriteLine("PASS F#: private dependency context preserves the host contract");
        var privateCore = loadContext.Assemblies.FirstOrDefault(a => a.GetName().Name == "FSharp.Core");
        Console.WriteLine(privateCore is null
            ? "INFO F#: FSharp.Core was not needed by the hooks run so far"
            : $"INFO F#: FSharp.Core {privateCore.GetName().Version} loaded privately from {Path.GetFileName(Path.GetDirectoryName(privateCore.Location))}");
        var helpers = assembly.GetType("ExampleFSharpMod.GovernanceHelpers")!;
        var korea = context.Nation("KOR"); var before = korea.PublicDebt;
        if (!(bool)helpers.GetMethod("Borrow")!.Invoke(null, new object[] { context, "KOR", .1 }) ||
            korea.PublicDebt != before) throw new Exception("F# borrowing did not enqueue without premature mutation");
        context.Campaign.Commands.ApplyAll(context.Campaign);
        if (korea.PublicDebt <= before) throw new Exception("F# borrowing did not reach the sovereign finance command");
        Console.WriteLine("PASS F#: typed helper queues and applies the host debt operation");
        if (!(bool)helpers.GetMethod("EnactLaw")!.Invoke(null, new object[] { context, "KOR", "fiscal_discipline" }))
            throw new Exception("F# policy could not enqueue");
        context.Campaign.Commands.ApplyAll(context.Campaign);
        if (!korea.Laws.Values.Any(l => l.Id == "fiscal_discipline")) throw new Exception("F# policy did not reach host reform rules");
        Console.WriteLine("PASS F#: typed helper enacts a policy through host rules");
        loadContext.Unload();
        return new WeakReference(loadContext);
    }
}
