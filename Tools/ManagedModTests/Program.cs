using System.Reflection;
using System.Runtime.Loader;
using DealOrForce.Modding;
using DealOrForce.Simulation;

var path = Path.Combine(AppContext.BaseDirectory, "ExampleFSharpMod.dll");
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
var entry = assembly.GetTypes().Single(t => !t.IsAbstract && typeof(IGlobalityMod).IsAssignableFrom(t));
var plugin = (IGlobalityMod)Activator.CreateInstance(entry);
var log = new List<string>();
var constructor = typeof(ModContext).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
var context = (ModContext)constructor.Invoke(new object[] { "example_fsharp_mod", (Action<string>)log.Add });
typeof(ModContext).GetProperty(nameof(ModContext.Campaign)).SetValue(context, Campaign.CreateBootstrap(101));
plugin.OnLoad(context);
plugin.OnCampaignCreated(context);
plugin.OnDayAdvanced(context);
if (log.Count != 3 || log[0] != "F# corecode loaded" || !log[2].StartsWith("F# monthly observation:"))
    throw new Exception("F# hooks did not execute through the managed mod contract");
Console.WriteLine("PASS F#: reflection discovers and instantiates IGlobalityMod from compiled DLL");
Console.WriteLine("PASS F#: load, campaign and day hooks execute with the actual ModContext");
