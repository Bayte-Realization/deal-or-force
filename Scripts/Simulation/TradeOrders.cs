using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// One thing a country has decided about its own trade.
///
/// The settings are declared in `common/trade` rather than written here, because which
/// numbers a player may set is a content question: a mod that wants a sixth control, or
/// wants the import ceiling to stop at eight tenths, changes a file rather than this class.
/// What a setting MEANS is code - the weekly pass reads five of these by name - so a setting
/// a mod invents is stored, saved, shown and reachable from script, and moves nothing on its
/// own until something is taught to read it. That is the honest boundary and it is worth
/// saying out loud rather than implying that data can grow behaviour.
/// </summary>
public sealed class TradeSettingDefinition
{
    /// <summary>What it is set on: one good, or one other country.</summary>
    public string Scope { get; init; } = TradeDesk.GoodScope;

    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>What every country is taken to have chosen until it chooses otherwise.</summary>
    public float Default { get; init; }

    public float Minimum { get; init; }
    public float Maximum { get; init; } = 1f;

    /// <summary>The coarseness a control should offer. Zero is a free number.</summary>
    public float Step { get; init; } = 0.05f;

    /// <summary>How to read the number on screen: a share, a multiplier or a plain figure.</summary>
    public string Shape { get; init; } = "share";

    /// <summary>
    /// What this setting is DOING, at whatever it is currently set to, in a sentence.
    ///
    /// A number and its name are not an instruction. "Price floor 0.4x" is a fact about a
    /// control; "we will not sell this below two fifths of what it is worth" is the decision
    /// the player actually made, and it is the one they can act on. Declared here rather
    /// than written into the screen so that a mod adding a control can say what its control
    /// means - and so a translator sees four short sentences instead of inferring them.
    /// </summary>
    public TradeSettingReads Reads { get; init; } = new();
}

/// <summary>
/// The four things a trade setting can be, and the sentence for each.
///
/// Every one is a localisation key like any other declared name, and every one may be left
/// out - a setting with nothing to say falls back to its own description, which is what the
/// screen showed before any of this existed.
/// </summary>
public sealed class TradeSettingReads
{
    /// <summary>At the declared default: this instruction is not doing anything.</summary>
    public string Resting { get; init; } = "";

    /// <summary>At the bottom of its range, where that means "none" rather than "a little".</summary>
    public string Off { get; init; } = "";

    /// <summary>For a flag, when it is on.</summary>
    public string On { get; init; } = "";

    /// <summary>Anywhere else. May carry {0}, which is the value as the screen prints it.</summary>
    public string Set { get; init; } = "";
}

/// <summary>
/// The controls a country has over its own trade, and the country's answers.
///
/// A country's answers live on the nation as flat string keys rather than as a tree of
/// objects, for two reasons. They are sparse - almost every country has decided nothing
/// about almost every good, and storing a default two hundred and sixty-one times over
/// twenty goods is five thousand rows saying nothing. And a good is an enum, which is to say
/// a number whose value depends on the order mods were read; the save file already carries a
/// name table to survive that, and a key written as the good's ID cannot be got wrong by a
/// mod being installed later.
/// </summary>
public static class TradeDesk
{
    public const string GoodScope = "good";
    public const string PartnerScope = "partner";

    // The five the weekly pass actually reads. Named here so a typo is a compile error
    // rather than a setting that silently does nothing.
    public const string ImportPriority = "import_priority";
    public const string ImportCeiling = "import_ceiling";
    public const string ExportReserve = "export_reserve";
    public const string PriceCeiling = "price_ceiling";
    public const string PriceFloor = "price_floor";
    public const string Preference = "preference";
    public const string Forbid = "forbid";

    private static readonly Dictionary<string, TradeSettingDefinition> Settings = new(StringComparer.Ordinal);

    private static readonly (string Id, TradeSettingDefinition Definition)[] Defaults =
    {
        (ImportPriority, new TradeSettingDefinition
        {
            Scope = GoodScope, Shape = "multiplier", Default = 1f, Minimum = 0f, Maximum = 5f, Step = 0.25f,
            Name = "Import priority",
            Description = "How far up the queue this country stands when the world is short of this good. " +
                          "Buyers are served in order of how badly they need it; this weighs that need."
        }),
        (ImportCeiling, new TradeSettingDefinition
        {
            Scope = GoodScope, Shape = "share", Default = 1f, Minimum = 0f, Maximum = 1f, Step = 0.05f,
            Name = "Import ceiling",
            Description = "The most of a shortfall this country is willing to cover by buying it in. " +
                          "Below one it goes short on purpose rather than depend on anybody."
        }),
        (ExportReserve, new TradeSettingDefinition
        {
            Scope = GoodScope, Shape = "share", Default = 0f, Minimum = 0f, Maximum = 1f, Step = 0.05f,
            Name = "Export reserve",
            Description = "The share of a surplus kept at home before any of it is sold. " +
                          "At one nothing leaves the country, whatever anybody offers."
        }),
        (PriceCeiling, new TradeSettingDefinition
        {
            Scope = GoodScope, Shape = "multiplier", Default = 0f, Minimum = 0f, Maximum = 5f, Step = 0.1f,
            Name = "Price ceiling",
            Description = "Refuse to buy above this multiple of the good's base price. Nought is no limit."
        }),
        (PriceFloor, new TradeSettingDefinition
        {
            Scope = GoodScope, Shape = "multiplier", Default = 0f, Minimum = 0f, Maximum = 5f, Step = 0.1f,
            Name = "Price floor",
            Description = "Refuse to sell below this multiple of the good's base price. Nought is no limit."
        }),
        (Preference, new TradeSettingDefinition
        {
            Scope = PartnerScope, Shape = "multiplier", Default = 1f, Minimum = 0f, Maximum = 3f, Step = 0.25f,
            Name = "Preference",
            Description = "How much this country favours dealing with that one, over and above what " +
                          "distance and diplomacy already say."
        }),
        (Forbid, new TradeSettingDefinition
        {
            Scope = PartnerScope, Shape = "flag", Default = 0f, Minimum = 0f, Maximum = 1f, Step = 1f,
            Name = "Forbidden",
            Description = "No trade with that country - in everything, or in one good if the rule names one."
        }),
    };

    static TradeDesk() => ResetCatalog();

    public static IReadOnlyDictionary<string, TradeSettingDefinition> All => Settings;

    public static TradeSettingDefinition Of(string id) =>
        id is not null && Settings.TryGetValue(id, out var definition) ? definition : null;

    public static void ResetCatalog() => ResetCatalog(null);

    public static void ResetCatalog(List<string> errors)
    {
        Settings.Clear();
        foreach (var (id, definition) in Defaults) Settings[id] = definition;

        foreach (var document in GameDataLoader.DataDocuments("common", "trade"))
            using (document)
            {
                try { Merge(document.RootElement); }
                catch (Exception problem) { errors?.Add($"common/trade: {problem.Message}"); }
            }
    }

    public static void Merge(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var known = Of(entry.Name);
            Settings[entry.Name] = new TradeSettingDefinition
            {
                Scope = Text(entry.Value, "scope") ?? known?.Scope ?? GoodScope,
                Shape = Text(entry.Value, "shape") ?? known?.Shape ?? "share",
                Name = Text(entry.Value, "name") ?? known?.Name ?? entry.Name,
                Description = Text(entry.Value, "description") ?? known?.Description ?? "",
                Default = Number(entry.Value, "default") ?? known?.Default ?? 0f,
                Minimum = Number(entry.Value, "minimum") ?? known?.Minimum ?? 0f,
                Maximum = Number(entry.Value, "maximum") ?? known?.Maximum ?? 1f,
                Step = Number(entry.Value, "step") ?? known?.Step ?? 0.05f,
                Reads = ReadsOf(entry.Value, known?.Reads)
            };
        }
    }

    /// <summary>The four sentences, or whatever of them a file troubled to give.</summary>
    private static TradeSettingReads ReadsOf(JsonElement element, TradeSettingReads known)
    {
        if (!element.TryGetProperty("reads", out var reads) || reads.ValueKind != JsonValueKind.Object)
            return known ?? new TradeSettingReads();
        return new TradeSettingReads
        {
            Resting = Text(reads, "resting") ?? known?.Resting ?? "",
            Off = Text(reads, "off") ?? known?.Off ?? "",
            On = Text(reads, "on") ?? known?.On ?? "",
            Set = Text(reads, "set") ?? known?.Set ?? ""
        };
    }

    /// <summary>
    /// What this setting currently says, in the player's language.
    ///
    /// At rest first, because "this is not doing anything" is the most useful thing a screen
    /// of seven controls can tell you about six of them. Then the ends of the range, then
    /// anything else - and if the file declared none of it, the setting's own description,
    /// which is at least true.
    /// </summary>
    public static string Reading(TradeSettingDefinition definition, float value, string formatted)
    {
        if (definition is null) return "";
        var reads = definition.Reads;
        var flag = string.Equals(definition.Shape, "flag", StringComparison.OrdinalIgnoreCase);

        if (flag)
        {
            var on = value >= (definition.Minimum + definition.Maximum) * 0.5f;
            var said = on ? reads.On : reads.Off.Length > 0 ? reads.Off : reads.Resting;
            return said.Length > 0 ? Loc.Get(said) : Loc.Get(definition.Description);
        }

        if (MathF.Abs(value - definition.Default) < 0.0001f && reads.Resting.Length > 0)
            return Loc.Get(reads.Resting);
        if (MathF.Abs(value - definition.Minimum) < 0.0001f && reads.Off.Length > 0)
            return Loc.Get(reads.Off);
        if (MathF.Abs(value - definition.Maximum) < 0.0001f && reads.On.Length > 0)
            return Loc.Get(reads.On);
        if (reads.Set.Length > 0) return Loc.Format(reads.Set, formatted);
        if (reads.Resting.Length > 0) return Loc.Get(reads.Resting);
        return Loc.Get(definition.Description);
    }

    /// <summary>Whether this country has actually told its traders anything by this control.</summary>
    public static bool InForce(TradeSettingDefinition definition, float value) =>
        definition is not null && MathF.Abs(value - definition.Default) > 0.0001f;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static float? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetSingle(out var number) ? number : null;

    // ------------------------------------------------------------------ the keys ----
    //
    // Written out of the good's ID and the other country's TAG rather than out of their
    // numbers. Both numbers move when mods are installed in a different order; neither name
    // does, so a save carries these across a changed mod list without a translation table.

    public static string GoodKey(Commodity commodity, string setting) =>
        "good/" + CommodityCatalog.IdOf(commodity) + "/" + setting;

    public static string PartnerKey(string tag, string setting) =>
        "partner/" + tag + "/" + setting;

    public static string PartnerKey(string tag, Commodity commodity, string setting) =>
        "partner/" + tag + "/" + CommodityCatalog.IdOf(commodity) + "/" + setting;

    /// <summary>
    /// Read a stored key back: which setting it is about.
    ///
    /// The keys are written to be durable across a changed mod list, which makes them
    /// machine-shaped - `good/Food/import_priority`. Anything that wants to LIST what a
    /// country has decided has to be able to go the other way, and until there was a screen
    /// that listed them, nothing did.
    /// </summary>
    public static string SettingOf(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        var at = key.LastIndexOf('/');
        return at < 0 ? "" : key[(at + 1)..];
    }

    /// <summary>
    /// The same key as a sentence: what it is about, in the player's language.
    ///
    /// "Food" for a good, "Iran" for a country, "Iran, food" for an instruction about one
    /// good with one country - which is the whole of what the key can say.
    /// </summary>
    public static string Describe(Nation nation, string key)
    {
        var definition = Of(SettingOf(key));
        var name = definition is null ? SettingOf(key) : Loc.Get(definition.Name);
        var parts = (key ?? "").Split('/');

        if (parts.Length >= 3 && parts[0] == "good")
            return Loc.Format("{0} · {1}", GoodName(parts[1]), name);

        if (parts.Length == 4 && parts[0] == "partner")
            return Loc.Format("{0} · {1} · {2}", CountryName(nation, parts[1]),
                GoodName(parts[2]), name);

        if (parts.Length == 3 && parts[0] == "partner")
            return Loc.Format("{0} · {1}", CountryName(nation, parts[1]), name);

        return key ?? "";
    }

    private static string GoodName(string id) =>
        CommodityCatalog.TryParse(id, out var commodity)
            ? Loc.Get(CommodityCatalog.Get(commodity)?.Name ?? id)
            : id;

    /// <summary>
    /// The other country's own name, translated, with the tag as the fallback for a country
    /// a mod removed after the instruction about it was written down.
    /// </summary>
    private static string CountryName(Nation nation, string tag) =>
        CountryNames.Of(tag) is { Length: > 0 } name ? name : tag;

    // ------------------------------------------------------------------ reading ----

    /// <summary>What this country has decided about this good, or the declared default.</summary>
    ///
    /// The empty check is not a micro-optimisation, it is the difference between this
    /// feature and a stutter. Almost every country has decided nothing about almost every
    /// good, and the weekly pass asks this question for every country, every good and every
    /// setting - so building a key first meant tens of thousands of strings a week, which
    /// the bench measured at twelve times the whole day's allocation budget. A country that
    /// has said nothing is answered without touching a key at all.
    public static float ForGood(Nation nation, Commodity commodity, string setting)
    {
        if (nation is null || nation.TradeSettings.Count == 0) return Of(setting)?.Default ?? 0f;
        if (nation.TradeSettings.TryGetValue(GoodKey(commodity, setting), out var chosen)) return chosen;
        return Of(setting)?.Default ?? 0f;
    }

    /// <summary>
    /// What this country has decided about that one - for this good if it said something
    /// about this good, and otherwise whatever it said about them in general.
    /// </summary>
    public static float ForPartner(Nation nation, string partnerTag, Commodity commodity, string setting)
    {
        if (nation is null || string.IsNullOrEmpty(partnerTag) || nation.TradeSettings.Count == 0)
            return Of(setting)?.Default ?? 0f;
        if (nation.TradeSettings.TryGetValue(PartnerKey(partnerTag, commodity, setting), out var forThisGood))
            return forThisGood;
        if (nation.TradeSettings.TryGetValue(PartnerKey(partnerTag, setting), out var forEverything))
            return forEverything;
        return Of(setting)?.Default ?? 0f;
    }

    /// <summary>Whether either side has forbidden this particular trade.</summary>
    public static bool Forbidden(Nation buyer, Nation seller, Commodity commodity) =>
        (buyer.TradeSettings.Count > 0 && ForPartner(buyer, seller.Tag, commodity, Forbid) >= 0.5f) ||
        (seller.TradeSettings.Count > 0 && ForPartner(seller, buyer.Tag, commodity, Forbid) >= 0.5f);

    /// <summary>Both sides' preference for each other, as one multiplier.</summary>
    public static float PreferenceBetween(Nation buyer, Nation seller, Commodity commodity)
    {
        if (buyer.TradeSettings.Count == 0 && seller.TradeSettings.Count == 0) return 1f;
        return ForPartner(buyer, seller.Tag, commodity, Preference) *
               ForPartner(seller, buyer.Tag, commodity, Preference);
    }

    /// <summary>
    /// Whether a price is one the buyer will pay and the seller will accept, as multiples of
    /// the good's base price. A limit of nought is no limit, which is what every country
    /// starts with: the ceiling only exists once somebody sets it.
    /// </summary>
    public static bool PriceIsAcceptable(Nation buyer, Nation seller, Commodity commodity, float price)
    {
        if (buyer.TradeSettings.Count == 0 && seller.TradeSettings.Count == 0) return true;
        var basePrice = MathF.Max(0.0001f, CommodityCatalog.Get(commodity).BasePrice);
        var multiple = price / basePrice;
        var ceiling = ForGood(buyer, commodity, PriceCeiling);
        if (ceiling > 0.0001f && multiple > ceiling) return false;
        var floor = ForGood(seller, commodity, PriceFloor);
        return !(floor > 0.0001f) || !(multiple < floor);
    }

    // ------------------------------------------------------------------ writing ----

    /// <summary>
    /// Store a decision, or forget it when it is the default again.
    ///
    /// Forgetting matters: a save that writes down every country's agreement with the
    /// default is a save full of nothing, and a table full of defaults cannot be told apart
    /// from a table of deliberate choices when a mod later changes what the default is.
    /// </summary>
    public static void Set(Nation nation, string key, string setting, float value)
    {
        if (nation is null || string.IsNullOrEmpty(key)) return;
        var definition = Of(setting);
        if (definition is null) return;
        var bounded = M.Clamp(value, definition.Minimum, definition.Maximum);
        if (MathF.Abs(bounded - definition.Default) < 0.0005f) nation.TradeSettings.Remove(key);
        else nation.TradeSettings[key] = bounded;
    }
}

/// <summary>What a country has told its own traders to do. Empty is "whatever is normal".</summary>
public sealed partial class Nation
{
    /// <summary>
    /// Sparse on purpose - see TradeDesk. A country that has decided nothing carries nothing,
    /// and the keys are written out of names rather than numbers so a changed mod list cannot
    /// move somebody's oil policy onto their fertiliser.
    /// </summary>
    public readonly Dictionary<string, float> TradeSettings = new(StringComparer.Ordinal);
}

/// <summary>Set one trade instruction: a good's, or one about another country.</summary>
public sealed class SetTradeInstructionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    /// <summary>The good this is about, or empty when the instruction is about a country.</summary>
    public string GoodId { get; init; } = "";

    /// <summary>The other country this is about, or empty when it is about a good alone.</summary>
    public string PartnerTag { get; init; } = "";

    public string Setting { get; init; } = "";
    public float Value { get; init; }

    /// <summary>What it now reads, so the interface can show the clamped answer.</summary>
    public float Applied { get; private set; }

    public bool Accepted { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var definition = TradeDesk.Of(Setting);
        if (nation is null || definition is null) return;

        var commodity = default(Commodity);
        var hasGood = !string.IsNullOrEmpty(GoodId) && CommodityCatalog.TryParse(GoodId, out commodity);

        string key;
        if (!string.IsNullOrEmpty(PartnerTag))
        {
            if (definition.Scope != TradeDesk.PartnerScope) return;
            key = hasGood
                ? TradeDesk.PartnerKey(PartnerTag, commodity, Setting)
                : TradeDesk.PartnerKey(PartnerTag, Setting);
        }
        else
        {
            if (definition.Scope != TradeDesk.GoodScope || !hasGood) return;
            key = TradeDesk.GoodKey(commodity, Setting);
        }

        TradeDesk.Set(nation, key, Setting, Value);
        Applied = nation.TradeSettings.TryGetValue(key, out var stored) ? stored : definition.Default;
        Accepted = true;
        DiagnosticLogs.Write("orders",
            $"TRADE_SET nation={nation.Tag} key={key} value={Applied:0.###}");
    }
}

/// <summary>Forget every instruction a country has given about its trade.</summary>
public sealed class ClearTradeInstructionsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    /// <summary>How many were dropped, so the interface can say so.</summary>
    public int Cleared { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        Cleared = nation.TradeSettings.Count;
        nation.TradeSettings.Clear();
    }
}
