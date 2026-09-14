using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Safe, engine-neutral UI composition. Data chooses existing capabilities, never CLR methods.</summary>
public static class CampaignUiCatalog
{
    public sealed record Navigation(string Screen, string Label, string Icon, string Help);
    public sealed record Tab(string Id, string Label, string[] Groups);
    public sealed record Link(string Label, string Action, string Context, string Tab, string Help);
    public sealed record Screen(Tab[] Tabs, Link[] Links);
    public static IReadOnlyList<Navigation> NavigationItems => NavigationRows;
    public static string[] TopStats { get; private set; } = Array.Empty<string>();
    public static string[] OutlinerOrder { get; private set; } = Array.Empty<string>();
    public static string[] HiddenOutliner { get; private set; } = Array.Empty<string>();
    private static readonly List<Navigation> NavigationRows = new();
    private static readonly Dictionary<string, Screen> Screens = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> DetailOrders = new();
    private static readonly Dictionary<string, string[]> HiddenDetails = new();
    private static readonly Dictionary<string, Dictionary<string, string>> DetailLabels = new();
    public static string[] DetailOrder(string screen) => DetailOrders.GetValueOrDefault(screen) ?? Array.Empty<string>();
    public static bool DetailHidden(string screen, string field) => HiddenDetails.TryGetValue(screen, out var hidden) && hidden.Contains(field);
    public static string DetailLabel(string screen, string field, string fallback) =>
        DetailLabels.TryGetValue(screen, out var labels) ? labels.GetValueOrDefault(field, fallback) : fallback;
    public static readonly HashSet<string> Destinations = new(StringComparer.Ordinal)
        { "Nation", "Economy", "Military", "Diplomacy", "Wars", "Decisions", "State Affairs", "Ledger" };
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
        { "government", "ledger", "wars", "diplomacy" };
    private static readonly Dictionary<string, string[]> AllowedTabs = new()
    {
        ["Nation"] = new[] { "Overview", "Government", "Society" },
        ["Economy"] = new[] { "Budget", "Market", "Construction" },
        ["Region"] = new[] { "Overview", "Population", "Province" },
        ["Decisions"] = new[] { "Decisions", "Formables" }
    };
    public static Screen For(string screen) => Screens.GetValueOrDefault(screen) ?? new Screen(Array.Empty<Tab>(), Array.Empty<Link>());
    public static void Reset() { NavigationRows.Clear(); Screens.Clear(); DetailOrders.Clear(); HiddenDetails.Clear(); DetailLabels.Clear(); TopStats = Array.Empty<string>(); OutlinerOrder = Array.Empty<string>(); HiddenOutliner = Array.Empty<string>(); }
    private static string Text(JsonElement e, string key, string fallback = "") =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;
    private static string[] Strings(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Distinct().ToArray() : Array.Empty<string>();
    public static bool LinkVisible(Link link, string tab, bool foreign) =>
        (link.Tab.Length == 0 || link.Tab == tab) && (link.Context == "always" || (link.Context == "own" && !foreign) || (link.Context == "foreign" && foreign));

    // Arrays replace, omitted properties inherit. [] explicitly removes links/tabs/navigation.
    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("campaign: expected object"); return; }
        if (root.TryGetProperty("top_stats", out _)) TopStats = Strings(root, "top_stats");
        if (root.TryGetProperty("outliner_order", out _)) OutlinerOrder = Strings(root, "outliner_order");
        if (root.TryGetProperty("hidden_outliner", out _)) HiddenOutliner = Strings(root, "hidden_outliner");
        if (root.TryGetProperty("navigation", out var nav))
        {
            if (nav.ValueKind != JsonValueKind.Array) errors?.Add("campaign.navigation: expected array");
            else
            {
                NavigationRows.Clear();
                foreach (var e in nav.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) { errors?.Add("navigation: expected object"); continue; }
                    var id = Text(e, "screen");
                    if (!Destinations.Contains(id) || NavigationRows.Any(n => n.Screen == id)) { errors?.Add($"navigation: invalid/duplicate screen '{id}'"); continue; }
                    NavigationRows.Add(new(id, Text(e, "label", id), Text(e, "icon"), Text(e, "help", id)));
                }
            }
        }
        if (!root.TryGetProperty("screens", out var screens)) return;
        if (screens.ValueKind != JsonValueKind.Object) { errors?.Add("campaign.screens: expected object"); return; }
        foreach (var entry in screens.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) { errors?.Add($"screen {entry.Name}: expected object"); continue; }
            var current = For(entry.Name);
            if (entry.Value.TryGetProperty("detail_order", out _)) DetailOrders[entry.Name] = Strings(entry.Value, "detail_order");
            if (entry.Value.TryGetProperty("hidden_details", out _)) HiddenDetails[entry.Name] = Strings(entry.Value, "hidden_details");
            if (entry.Value.TryGetProperty("detail_labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            {
                if (!DetailLabels.TryGetValue(entry.Name, out var table)) DetailLabels[entry.Name] = table = new();
                foreach (var label in labels.EnumerateObject())
                    if (label.Value.ValueKind == JsonValueKind.String) table[label.Name] = label.Value.GetString();
            }
            var tabs = current.Tabs; var links = current.Links;
            if (entry.Value.TryGetProperty("tabs", out var ts))
            {
                var list = new List<Tab>();
                if (ts.ValueKind != JsonValueKind.Array) { errors?.Add($"{entry.Name}.tabs: expected array"); continue; }
                foreach (var e in ts.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var id = Text(e, "id");
                    if (!AllowedTabs.TryGetValue(entry.Name, out var allowed) || !allowed.Contains(id) || list.Any(t => t.Id == id))
                    { errors?.Add($"{entry.Name}: invalid/duplicate tab '{id}'"); continue; }
                    list.Add(new(id, Text(e, "label", id), Strings(e, "groups")));
                }
                tabs = list.ToArray();
            }
            if (entry.Value.TryGetProperty("links", out var ls))
            {
                var list = new List<Link>();
                if (ls.ValueKind != JsonValueKind.Array) { errors?.Add($"{entry.Name}.links: expected array"); continue; }
                foreach (var e in ls.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var action = Text(e, "action"); var context = Text(e, "context", "always");
                    if (!Actions.Contains(action) || context is not ("always" or "own" or "foreign"))
                    { errors?.Add($"{entry.Name}: invalid link '{action}'/'{context}'"); continue; }
                    list.Add(new(Text(e, "label", action), action, context, Text(e, "tab"), Text(e, "help")));
                }
                links = list.ToArray();
            }
            Screens[entry.Name] = new(tabs, links);
        }
    }
}
