using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// How a country is governed: who decides, and by what right.
///
/// A form of government is not an ideology and the two are deliberately separate axes. A
/// parliamentary republic can be liberal or socialist; a one-party state can be socialist or
/// fascist; an absolute monarchy has an ideology whether or not it admits to one. Games that
/// fold the two together end up with a list of twenty things that are half each, and a mod
/// that wants a conservative constitutional monarchy has nowhere to put it.
/// </summary>
public sealed class GovernmentDefinition
{
    public string Id = "";
    public string Name = "";
    public string Description = "";

    /// <summary>Where a slider or a button for it appears among the others.</summary>
    public int Order;

    /// <summary>
    /// How much of the decision rests with the centre, from a federation of provinces at 0
    /// to one office at 1.
    ///
    /// A centralised state acts faster and is resented more; that is the whole trade and it
    /// is one number, so a mod can invent a government by choosing where on it to sit.
    /// </summary>
    public float Centralisation = 0.5f;

    // Weekly, like the cabinet's, except growth which is annual because growth is quoted so.
    public float Growth;
    public float Stability;
    public float WarSupport;
    public float Radicalism;
    public float Living;
    public float Literacy;

    /// <summary>
    /// How hard it is to stop being this, as a multiple of the base cost of a change.
    ///
    /// Walking out of a parliamentary republic is an election; walking out of an absolute
    /// monarchy is a different kind of afternoon.
    /// </summary>
    public float ChangeCost = 1f;

    /// <summary>What the arrangement adds for as long as it lasts (<see cref="NationalModifiers"/>).</summary>
    public Dictionary<string, float> Modifiers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How readily this country's people take up arms without being told to, as a
    /// multiple. One is the ordinary case. See <see cref="MilitiaCore"/>.
    /// </summary>
    public float Militia = 1f;

    /// <summary>
    /// How hard it is for this kind of government to start a war of choice - parliaments,
    /// courts, elections and a press, or a junta that answers to nobody. The AI prices it into
    /// every war it considers, in full when there is no reason it could give the world and in
    /// part when there is (AiRestraintWeight, AiRestraintWithCause); and two governments both
    /// at or above AiDemocraticPeaceRestraint hardly ever fight each other at all.
    /// </summary>
    public float WarRestraint = 0.5f;
}

/// <summary>
/// What a country's government believes, placed on two axes rather than listed as a set of
/// mutually hostile boxes.
///
/// `Economic` runs from a planned economy at -1 to an unregulated one at +1; `Authority`
/// from liberty at -1 to the state's word at +1. Everything that needs to know how two
/// countries feel about each other's politics asks the distance between their positions,
/// which means a mod can add an ideology by placing it, and every other ideology's opinion
/// of it exists the moment it is placed. A table of hand-written opinions between fifteen
/// ideologies is two hundred and ten numbers nobody will keep in step.
/// </summary>
public sealed class IdeologyDefinition
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public int Order;

    public float Economic;
    public float Authority;

    /// <summary>The colour this ideology is drawn in, for a map mode and a panel.</summary>
    public string Colour = "#8A8F98";

    // The same weekly channels a government has. An ideology is what the state is trying to
    // do; a government is how it is arranged to do it.
    public float Growth;
    public float Stability;
    public float WarSupport;
    public float Radicalism;
    public float Living;
    public float Literacy;

    /// <summary>
    /// How readily this country's people take up arms without being told to, as a
    /// multiple. One is the ordinary case. See <see cref="MilitiaCore"/>.
    /// </summary>
    public float Militia = 1f;

    /// <summary>
    /// The forms of government this ideology is comfortable in.
    ///
    /// Empty means it does not mind. Being outside the list is not forbidden - a liberal
    /// absolute monarchy is a real thing that happened - it simply costs stability every
    /// week, which is the honest way to say "this is an unstable arrangement".
    /// </summary>
    public List<string> PrefersGovernments = new();

    public float MismatchStability = -0.0004f;

    /// <summary>What the ideology adds for as long as the country holds it (<see cref="NationalModifiers"/>).</summary>
    public Dictionary<string, float> Modifiers = new(StringComparer.OrdinalIgnoreCase);
}

public static class GovernmentCatalog
{
    private static readonly List<GovernmentDefinition> Definitions = new();

    private static readonly GovernmentDefinition Unknown = new()
    {
        Id = "unknown",
        Name = "Unrecognised government",
        Description = "A form of government nothing in the data describes."
    };

    public static IReadOnlyList<GovernmentDefinition> All => Definitions;

    private static readonly Dictionary<string, GovernmentDefinition> ById = new(StringComparer.OrdinalIgnoreCase);

    public static GovernmentDefinition Of(string id) =>
        id is not null && ById.TryGetValue(id, out var definition) ? definition : Unknown;

    public static bool Knows(string id)
    {
        foreach (var definition in Definitions)
            if (string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>What a country is when the data has not said otherwise.</summary>
    public static string Default => Definitions.Count > 0 ? Definitions[0].Id : "unknown";

    static GovernmentCatalog() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Definitions.Clear();
        ById.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "governments"))
            using (document)
                Merge(document.RootElement, errors);
        Sort();
        if (Definitions.Count == 0)
            errors?.Add("common/governments describes no form of government, so no country has one.");
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
        Sort();
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var definition = Read(entry.Name, entry.Value, errors);
            if (definition is null) continue;
            Definitions.RemoveAll(existing =>
                string.Equals(existing.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            Definitions.Add(definition);
        }
    }

    public static void Validate(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.Object)
                Read(entry.Name, entry.Value, problems);
    }

    private static void Sort()
    {
        Definitions.Sort((left, right) =>
            left.Order != right.Order
                ? left.Order.CompareTo(right.Order)
                : string.CompareOrdinal(left.Id, right.Id));
        ById.Clear();
        foreach (var definition in Definitions) ById[definition.Id] = definition;
    }

    private static GovernmentDefinition Read(string id, JsonElement element, List<string> errors)
    {
        var definition = new GovernmentDefinition { Id = id.Trim(), Name = id.Trim() };
        foreach (var field in element.EnumerateObject())
        {
            if (field.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            switch (field.Name.ToLowerInvariant())
            {
                case "name": definition.Name = Text(field.Value, definition.Name); break;
                case "description": definition.Description = Text(field.Value, ""); break;
                case "order": definition.Order = (int)Number(field.Value, definition.Order); break;
                case "centralisation" or "centralization":
                    definition.Centralisation = Math.Clamp(Number(field.Value, 0.5f), 0f, 1f);
                    break;
                case "change_cost": definition.ChangeCost = MathF.Max(0f, Number(field.Value, 1f)); break;
                case "militia": definition.Militia = MathF.Max(0f, Number(field.Value, 1f)); break;
                case "war_restraint": definition.WarRestraint = MathF.Max(0f, Number(field.Value, 0.5f)); break;
                case "modifiers":
                    definition.Modifiers = NationalModifiers.Read(field.Value, $"government `{id}`", errors);
                    break;
                case "effects" when field.Value.ValueKind == JsonValueKind.Object:
                    foreach (var effect in field.Value.EnumerateObject())
                    {
                        if (effect.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                        var value = Number(effect.Value, 0f);
                        switch (effect.Name.ToLowerInvariant())
                        {
                            case "growth": definition.Growth = value; break;
                            case "stability": definition.Stability = value; break;
                            case "war_support": definition.WarSupport = value; break;
                            case "radicalism": definition.Radicalism = value; break;
                            case "living": definition.Living = value; break;
                            case "literacy": definition.Literacy = value; break;
                            default:
                                errors?.Add($"government `{id}`: `{effect.Name}` is not an effect. Use " +
                                            "growth, stability, war_support, radicalism, living or literacy.");
                                break;
                        }
                    }
                    break;
                default:
                    errors?.Add($"government `{id}`: `{field.Name}` is not a field a government has.");
                    break;
            }
        }
        return definition;
    }

    internal static string Text(JsonElement element, string fallback) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? fallback : fallback;

    internal static float Number(JsonElement element, float fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var value) ? value : fallback;
}

public static class IdeologyCatalog
{
    private static readonly List<IdeologyDefinition> Definitions = new();

    private static readonly IdeologyDefinition Unknown = new()
    {
        Id = "unknown",
        Name = "No stated ideology",
        Description = "A politics nothing in the data describes."
    };

    public static IReadOnlyList<IdeologyDefinition> All => Definitions;

    private static readonly Dictionary<string, IdeologyDefinition> ById = new(StringComparer.OrdinalIgnoreCase);

    public static IdeologyDefinition Of(string id) =>
        id is not null && ById.TryGetValue(id, out var definition) ? definition : Unknown;

    public static bool Knows(string id)
    {
        foreach (var definition in Definitions)
            if (string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string Default => Definitions.Count > 0 ? Definitions[0].Id : "unknown";

    static IdeologyCatalog() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Definitions.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "ideologies"))
            using (document)
                Merge(document.RootElement, errors);
        Sort();
        if (Definitions.Count == 0)
            errors?.Add("common/ideologies describes no ideology, so no government believes anything.");
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
        Sort();
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var definition = Read(entry.Name, entry.Value, errors);
            if (definition is null) continue;
            Definitions.RemoveAll(existing =>
                string.Equals(existing.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            Definitions.Add(definition);
        }
    }

    public static void Validate(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.Object)
                Read(entry.Name, entry.Value, problems);
    }

    private static void Sort()
    {
        Definitions.Sort((left, right) =>
            left.Order != right.Order
                ? left.Order.CompareTo(right.Order)
                : string.CompareOrdinal(left.Id, right.Id));
        ById.Clear();
        foreach (var definition in Definitions) ById[definition.Id] = definition;
    }

    private static IdeologyDefinition Read(string id, JsonElement element, List<string> errors)
    {
        var definition = new IdeologyDefinition { Id = id.Trim(), Name = id.Trim() };
        foreach (var field in element.EnumerateObject())
        {
            if (field.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            switch (field.Name.ToLowerInvariant())
            {
                case "name": definition.Name = GovernmentCatalog.Text(field.Value, definition.Name); break;
                case "description": definition.Description = GovernmentCatalog.Text(field.Value, ""); break;
                case "colour" or "color": definition.Colour = GovernmentCatalog.Text(field.Value, definition.Colour); break;
                case "order": definition.Order = (int)GovernmentCatalog.Number(field.Value, definition.Order); break;
                case "economic":
                    definition.Economic = Math.Clamp(GovernmentCatalog.Number(field.Value, 0f), -1f, 1f);
                    break;
                case "authority":
                    definition.Authority = Math.Clamp(GovernmentCatalog.Number(field.Value, 0f), -1f, 1f);
                    break;
                case "mismatch_stability":
                    definition.MismatchStability = GovernmentCatalog.Number(field.Value, definition.MismatchStability);
                    break;
                case "militia":
                    definition.Militia = MathF.Max(0f, GovernmentCatalog.Number(field.Value, 1f));
                    break;
                case "modifiers":
                    definition.Modifiers = NationalModifiers.Read(field.Value, $"ideology `{id}`", errors);
                    break;
                case "prefers_governments" when field.Value.ValueKind == JsonValueKind.Array:
                    definition.PrefersGovernments.Clear();
                    foreach (var item in field.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            definition.PrefersGovernments.Add(item.GetString() ?? "");
                    break;
                case "effects" when field.Value.ValueKind == JsonValueKind.Object:
                    foreach (var effect in field.Value.EnumerateObject())
                    {
                        if (effect.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                        var value = GovernmentCatalog.Number(effect.Value, 0f);
                        switch (effect.Name.ToLowerInvariant())
                        {
                            case "growth": definition.Growth = value; break;
                            case "stability": definition.Stability = value; break;
                            case "war_support": definition.WarSupport = value; break;
                            case "radicalism": definition.Radicalism = value; break;
                            case "living": definition.Living = value; break;
                            case "literacy": definition.Literacy = value; break;
                            default:
                                errors?.Add($"ideology `{id}`: `{effect.Name}` is not an effect. Use " +
                                            "growth, stability, war_support, radicalism, living or literacy.");
                                break;
                        }
                    }
                    break;
                default:
                    errors?.Add($"ideology `{id}`: `{field.Name}` is not a field an ideology has.");
                    break;
            }
        }
        return definition;
    }
}

/// <summary>
/// What a country's politics do to it, and what other countries make of them.
/// </summary>
public static class PoliticsCore
{
    /// <summary>Days a government must live with a change before it may make another.</summary>
    public const int ChangeCooldownDays = 180;

    /// <summary>Stability a country pays for rearranging itself, before the form's multiplier.</summary>
    public const float BaseChangeCost = 0.18f;

    /// <summary>
    /// Why a change would be refused, or an empty string if it would not.
    ///
    /// A pure question, so a screen can grey a button out and say why before anybody presses
    /// it, and the command can refuse with the same words afterwards. Two copies of this
    /// would be a panel that offers what the campaign then declines, which is the worst kind
    /// of interface: one that appears to be broken while working exactly as written.
    /// </summary>
    public static string Refusal(Campaign campaign, Nation nation, string government, string ideology)
    {
        if (campaign is null || nation is null) return "No such country.";

        var wantsGovernment = !string.IsNullOrEmpty(government) &&
                              !string.Equals(government, nation.Government, StringComparison.OrdinalIgnoreCase);
        var wantsIdeology = !string.IsNullOrEmpty(ideology) &&
                            !string.Equals(ideology, nation.Ideology, StringComparison.OrdinalIgnoreCase);
        if (!wantsGovernment && !wantsIdeology) return "That is what it already is.";
        if (wantsGovernment && !GovernmentCatalog.Knows(government)) return "No such form of government.";
        if (wantsIdeology && !IdeologyCatalog.Knows(ideology)) return "No such ideology.";

        var since = campaign.ElapsedDays - nation.PoliticsChangedDay;
        if (since < ChangeCooldownDays)
            return Loc.Format("The country is still settling after the last change. {0} day(s) to go.",
                ChangeCooldownDays - since);
        return "";
    }

    /// <summary>What rearranging the state would cost in stability, before it is paid.</summary>
    public static float ChangeCost(Nation nation, string government, string ideology)
    {
        if (nation is null) return 0f;
        var wantsGovernment = !string.IsNullOrEmpty(government) &&
                              !string.Equals(government, nation.Government, StringComparison.OrdinalIgnoreCase);
        var wantsIdeology = !string.IsNullOrEmpty(ideology) &&
                            !string.Equals(ideology, nation.Ideology, StringComparison.OrdinalIgnoreCase);
        if (!wantsGovernment && !wantsIdeology) return 0f;

        // Charged against the form being left, not the one being arrived at: what it costs
        // to stop being an absolute monarchy is a fact about absolute monarchy.
        var cost = BaseChangeCost * (wantsGovernment ? GovernmentOf(nation).ChangeCost : 0.45f);
        if (wantsGovernment && wantsIdeology) cost *= 1.35f;
        return cost;
    }

    /// <summary>Whatever the country is, corrected for anything the data no longer has.</summary>
    public static GovernmentDefinition GovernmentOf(Nation nation) =>
        GovernmentCatalog.Of(nation?.Government ?? "");

    public static IdeologyDefinition IdeologyOf(Nation nation) =>
        IdeologyCatalog.Of(nation?.Ideology ?? "");

    /// <summary>
    /// Whether the ideology is comfortable in the form of government it finds itself in.
    ///
    /// Not forbidden - a liberal absolute monarchy is a thing that has happened - but it
    /// costs stability every week it goes on, which is how a rule of thumb becomes a
    /// pressure rather than a prohibition.
    /// </summary>
    public static bool IsMismatched(Nation nation)
    {
        var ideology = IdeologyOf(nation);
        if (ideology.PrefersGovernments.Count == 0) return false;
        foreach (var preferred in ideology.PrefersGovernments)
            if (string.Equals(preferred, nation?.Government, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// How far apart two countries' politics are, from 0 for identical to 1 for opposite.
    ///
    /// Both axes, as a fraction of the furthest two ideologies could be. This is what every
    /// diplomatic consequence of ideology is built from, which is why it is one function
    /// rather than a table: a mod that adds an ideology gets every other ideology's opinion
    /// of it for nothing, and it cannot go stale.
    /// </summary>
    public static float IdeologicalDistance(Nation left, Nation right)
    {
        if (left is null || right is null) return 0f;
        var first = IdeologyOf(left);
        var second = IdeologyOf(right);
        var economic = first.Economic - second.Economic;
        var authority = first.Authority - second.Authority;
        // The furthest apart two positions can be is the diagonal of a 2x2 square.
        return Math.Clamp(MathF.Sqrt(economic * economic + authority * authority) / 2.8284f, 0f, 1f);
    }

    /// <summary>
    /// What two countries' politics are worth to what they make of each other.
    ///
    /// Positive for politics close enough to recognise, negative for politics far enough
    /// apart to distrust. The neutral point sits deliberately short of halfway: most pairs
    /// of countries in the world are somewhat unlike each other, and a world where almost
    /// every pair is drifting towards hostility is a world at permanent war.
    /// </summary>
    public static float Affinity(Campaign campaign, int leftId, int rightId)
    {
        if (campaign is null) return 0f;
        var weight = campaign.Defines.IdeologyOpinionWeight;
        if (weight <= 0f) return 0f;
        var distance = IdeologicalDistance(campaign.NationById(leftId), campaign.NationById(rightId));
        return (0.38f - distance) * weight;
    }
}
