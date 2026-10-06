using System.Reflection;
using System.Runtime.Loader;
using DealOrForce.Modding;
using DealOrForce.Simulation;

var path = Path.Combine(AppContext.BaseDirectory, "ExampleFSharpMod.dll");
var loadContext = new ManagedModLoadContext(path);
var assembly = loadContext.LoadFromAssemblyPath(path);
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
if (AssemblyLoadContext.GetLoadContext(entry.Assembly) != loadContext)
    throw new Exception("F# plugin did not load in its private context");
Console.WriteLine("PASS F#: private dependency context preserves the host contract");
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
