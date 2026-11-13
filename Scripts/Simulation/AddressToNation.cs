using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>One kind of address a government can make, as common/addresses states it.</summary>
public sealed class AddressDefinition
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    /// <summary>What it gives at once, before weariness: stability and war support.</summary>
    public float Stability;
    public float WarSupport;
    /// <summary>Only a country at war can make it.</summary>
    public bool RequiresWar;
    /// <summary>What it promises - "victory", "prosperity", "order" - or nothing.</summary>
    public string Promise = "";
    public int PromiseDays = 180;
    /// <summary>What a broken promise costs, and what a kept one is worth.</summary>
    public float BrokenStability;
    public float BrokenWarSupport;
    public float KeptStability;
}

public sealed partial class Nation
{
    public int LastAddressDay = -9999;
    /// <summary>How tired the country is of being spoken to, as of the last address (AddressToNation.Weariness).</summary>
    public float AddressWeariness;
    /// <summary>What the last address promised, when it falls due, and where things stood when it was made.</summary>
    public string AddressPromise = "";
    public int AddressPromiseDueDay = -1;
    public float AddressPromiseMark;
}

public sealed partial class Defines
{
    /// <summary>The least time between two addresses, in days.</summary>
    public int AddressCooldownDays { get; set; } = 30;
    /// <summary>How fast a country stops being tired of its government's speeches: weariness halves in this many days.</summary>
    public int AddressWearinessHalfLifeDays { get; set; } = 180;
}

/// <summary>
/// An address to the nation: a government speaking to its own people.
///
/// It works at once - a call for unity steadies a country, a promise of victory rallies one at
/// war - and it works less each time: a people addressed every month stops listening, and the
/// weariness takes half a year to wear off. And a promise is remembered. Promise victory and lose
/// ground, promise prosperity and grow poorer, promise order and let it slip, and the day the
/// promise falls due costs more than the speech ever gave; keep it, and the government is believed
/// a little more. The kinds of address are data (common/addresses): a mod adds a new one, or
/// changes what an old one gives, without code.
///
/// Governments nobody plays speak too, when the war is going badly or the country is restless.
/// </summary>
public static class AddressToNation
{
    private static readonly Dictionary<string, AddressDefinition> Entries = new(StringComparer.Ordinal);

    static AddressToNation() => ResetCatalog();

    public static IEnumerable<AddressDefinition> All => Entries.Values.OrderBy(d => d.Id, StringComparer.Ordinal);
    public static AddressDefinition Find(string id) => id is not null ? Entries.GetValueOrDefault(id) : null;

    public static void ResetCatalog()
    {
        Entries.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "addresses"))
            using (document) Merge(document.RootElement);
    }

    /// <summary>Key by key, like every catalogue: a mod naming an existing address changes only what it names.</summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object || !MechanicFeatures.ValidId(property.Name))
            {
                errors?.Add($"addresses: {property.Name} is not an address");
                continue;
            }
            var entry = Entries.GetValueOrDefault(property.Name) ?? new AddressDefinition { Id = property.Name, Name = property.Name };
            string Text(string key, string fallback) =>
                value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : fallback;
            float Number(string key, float fallback) =>
                value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var figure) && float.IsFinite(figure) ? figure : fallback;
            entry.Name = Text("name", entry.Name);
            entry.Description = Text("description", entry.Description);
            entry.Stability = Math.Clamp(Number("stability", entry.Stability), -0.2f, 0.2f);
            entry.WarSupport = Math.Clamp(Number("war_support", entry.WarSupport), -0.3f, 0.3f);
            entry.RequiresWar = value.TryGetProperty("requires_war", out var war) ? war.ValueKind == JsonValueKind.True : entry.RequiresWar;
            entry.Promise = Text("promise", entry.Promise);
            entry.PromiseDays = Math.Clamp((int)Number("promise_days", entry.PromiseDays), 30, 1825);
            entry.BrokenStability = Math.Clamp(Number("broken_stability", entry.BrokenStability), 0f, 0.3f);
            entry.BrokenWarSupport = Math.Clamp(Number("broken_war_support", entry.BrokenWarSupport), 0f, 0.5f);
            entry.KeptStability = Math.Clamp(Number("kept_stability", entry.KeptStability), 0f, 0.2f);
            Entries[property.Name] = entry;
        }
    }

    /// <summary>How tired the country is of speeches today: what was left after the last one, halving as time passes.</summary>
    public static float Weariness(Campaign c, Nation nation) =>
        nation.AddressWeariness * MathF.Pow(0.5f, (c.ElapsedDays - nation.LastAddressDay) / (float)Math.Max(1, c.Defines.AddressWearinessHalfLifeDays));

    /// <summary>The share of an address's effect that still lands: all of it the first time, half of it the next week.</summary>
    public static float Effectiveness(Campaign c, Nation nation) => 1f / (1f + Weariness(c, nation));

    /// <summary>Where the thing promised stands now: ground held, the economy, the country's order.</summary>
    private static float Measure(Campaign c, Nation nation, string promise) => promise switch
    {
        "victory" => c.HasOwnedLand(nation) ? ControlledProvinces(c, nation) : 0f,
        "prosperity" => nation.Gdp,
        "order" => nation.Stability,
        _ => 0f
    };

    private static int ControlledProvinces(Campaign c, Nation nation)
    {
        var map = c.ProvinceMap;
        if (map is null) return 0;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        var held = 0;
        for (var p = 1; p < controllers.Length && p <= map.ProvinceCount; p++)
            if (controllers[p] == nation.MapIndex && !map.WaterByProvince[p]) held++;
        return held;
    }

    public static string Refusal(Campaign c, Nation nation, AddressDefinition address)
    {
        if (nation is null || address is null) return Loc.Get("There is no such address.");
        if (nation.Movement is not null) return Loc.Get("A movement speaks to its followers, not to a nation.");
        var wait = nation.LastAddressDay + Math.Max(1, c.Defines.AddressCooldownDays) - c.ElapsedDays;
        if (wait > 0) return Loc.Format("The last address was too recent: {0} day(s) to wait.", wait);
        if (address.RequiresWar && !c.Wars.Any(w => !w.Ended && w.Involves(nation.Id))) return Loc.Get("Only a country at war can be promised victory.");
        if (address.Promise.Length > 0 && nation.AddressPromise.Length > 0 && nation.AddressPromiseDueDay > c.ElapsedDays)
            return Loc.Format("An earlier promise is still to be kept: {0} day(s) left.", nation.AddressPromiseDueDay - c.ElapsedDays);
        return "";
    }

    internal static void Deliver(Campaign c, Nation nation, AddressDefinition address)
    {
        var share = Effectiveness(c, nation);
        nation.Stability = M.Clamp01(nation.Stability + address.Stability * share);
        nation.WarSupport = M.Clamp01(nation.WarSupport + address.WarSupport * share);
        nation.AddressWeariness = Weariness(c, nation) + 1f;
        nation.LastAddressDay = c.ElapsedDays;
        if (address.Promise.Length > 0)
        {
            nation.AddressPromise = address.Promise;
            nation.AddressPromiseDueDay = c.ElapsedDays + address.PromiseDays;
            nation.AddressPromiseMark = Measure(c, nation, address.Promise);
        }
        c.Chronicle.Record(c, "address_to_nation", nation.Id, -1, address.Name, isPublic: Heard(c, nation));
        c.MarkStrategicChange();
    }

    /// <summary>Whose speeches the world hears of: the player's own, and the great powers'.</summary>
    private static bool Heard(Campaign c, Nation nation) => nation.IsHuman || AllianceSystem.GreatPowers(c).Contains(nation.Id);

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same daily pass, yielded between countries.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(AddressToNation))) yield break;
        var scanned = 0;
        foreach (var nation in c.Nations)
        {
            if (++scanned % 32 == 0) yield return 0;
            if (nation.AddressPromise.Length == 0 || nation.AddressPromiseDueDay > c.ElapsedDays) continue;
            Settle(c, nation);
        }
        if (c.ElapsedDays % 30 == 17) Ai(c);
    }

    /// <summary>The day a promise falls due: kept or broken, and remembered either way.</summary>
    private static void Settle(Campaign c, Nation nation)
    {
        var promise = nation.AddressPromise;
        var address = All.FirstOrDefault(a => a.Promise == promise);
        var now = Measure(c, nation, promise);
        var mark = nation.AddressPromiseMark;
        var kept = promise switch
        {
            // Victory: no ground lost since the promise, and the war over or going our way.
            "victory" => now >= mark,
            "prosperity" => now >= mark * 1.01f,
            "order" => now >= mark - 0.02f,
            _ => true
        };
        nation.AddressPromise = "";
        nation.AddressPromiseDueDay = -1;
        if (address is null) return;
        if (kept)
        {
            nation.Stability = M.Clamp01(nation.Stability + address.KeptStability);
            c.Chronicle.Record(c, "promise_kept", nation.Id, -1, address.Name, isPublic: Heard(c, nation));
        }
        else
        {
            nation.Stability = M.Clamp01(nation.Stability - address.BrokenStability);
            nation.WarSupport = M.Clamp01(nation.WarSupport - address.BrokenWarSupport);
            c.Chronicle.Record(c, "promise_broken", nation.Id, -1, address.Name, isPublic: Heard(c, nation));
        }
        c.MarkStrategicChange();
    }

    /// <summary>
    /// A government nobody plays speaks when it needs to: promises victory to a country at war
    /// that is losing heart, order to one that is coming apart, unity to one that is restless -
    /// and not again until the people are listening.
    /// </summary>
    private static void Ai(Campaign c)
    {
        foreach (var nation in c.Nations)
        {
            if (nation.IsHuman || nation.Movement is not null || nation.Population <= 0) continue;
            // Not again until the people have nearly forgotten the last one: about a year.
            if (Effectiveness(c, nation) < 0.8f) continue;
            var atWar = c.Wars.Any(w => !w.Ended && w.Involves(nation.Id));
            string id = null;
            if (atWar && nation.WarSupport < 0.35f) id = "victory";
            else if (nation.Stability < 0.3f) id = "order";
            else if (nation.Stability < 0.4f) id = "unity";
            if (id is null || Find(id) is not { } address || Refusal(c, nation, address).Length > 0) continue;
            new AddressNationCommand { IssuerId = nation.Id, AddressId = id }.Apply(c);
        }
    }
}

/// <summary>Make an address to the nation.</summary>
public sealed class AddressNationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string AddressId { get; init; } = "";
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var address = AddressToNation.Find(AddressId);
        Refusal = AddressToNation.Refusal(campaign, nation, address);
        if (Refusal.Length > 0) return;
        AddressToNation.Deliver(campaign, nation, address);
    }
}
