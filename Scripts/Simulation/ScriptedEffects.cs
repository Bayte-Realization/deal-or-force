using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// The scripted layer every piece of content writes its consequences in: events,
/// decisions and mod scripts all resolve to the same small effect vocabulary instead of
/// a closed C# enum a modder cannot extend.
///
/// Paradox content works this way for a reason - a modder who can only pick from six
/// pre-written outcomes cannot express a new idea, only re-skin an existing one. Here a
/// JSON object of key/value pairs is the whole language:
///
///   "effects": { "treasury_percent": -0.02, "stability": 0.018, "state.radicalism": -0.01 }
///
/// A bare number adds. Suffix `_percent` scales by (1 + value), `_mult` multiplies
/// outright, `_set` assigns. A `state.` prefix applies the entry to every state the
/// nation owns rather than to the nation itself. Unknown keys never fail silently: they
/// are reported alongside the list of keys that would have worked.
/// </summary>
public static class ScriptedEffects
{
    internal enum Operation { Add, Percent, Mult, Set }

    private sealed class Field
    {
        public string Name = "";
        public Func<Nation, float> GetNation;
        public Action<Nation, float> SetNation;
        public Func<ScenarioState, float> GetState;
        public Action<ScenarioState, float> SetState;
        public Func<Army, float> GetArmy;
        public Action<Army, float> SetArmy;
        public Func<Fleet, float> GetFleet;
        public Action<Fleet, float> SetFleet;
        public float Min = float.NegativeInfinity;
        public float Max = float.PositiveInfinity;
        public string Help = "";
    }

    private static readonly Dictionary<string, Field> Fields = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Suffix, Operation Operation)[] Suffixes =
    {
        ("_percent", Operation.Percent),
        ("_mult", Operation.Mult),
        ("_set", Operation.Set)
    };

    static ScriptedEffects()
    {
        NationField("treasury", n => n.Treasury, (n, v) => n.Treasury = v, float.NegativeInfinity, float.PositiveInfinity,
            "Cash on hand. An overdrawn balance is imported into sovereign principal after the complete scripted effect.");
        NationField("public_debt", n => n.PublicDebt, (n, v) => n.PublicDebt = v, 0f, float.PositiveInfinity,
            "Outstanding sovereign principal, separate from cash. Changing this does not issue cash.");
        NationField("credit_confidence", n => n.CreditConfidence, (n, v) => n.CreditConfidence = v, 0f, 1f,
            "Lenders' confidence, 0-1; affects credit access and the risk spread.");
        NationField("debt_arrears", n => n.DebtArrears, (n, v) => n.DebtArrears = v, 0f, float.PositiveInfinity,
            "Unpaid contractual interest, distinct from outstanding principal.");
        NationField("payment_arrears", n => n.PaymentArrears, (n, v) => n.PaymentArrears = v, 0f, float.PositiveInfinity,
            "Unpaid primary public spending; does not alone mean a contractual debt default.");
        // The services' experience and the government's political credit (MilitaryExperience).
        // They are earned by doing - battle, missions, sea time, funding, drill - and spent on the
        // war Doctrine (TacticalDoctrines); a decision or event may also grant them, which is how content
        // says \"this exercise taught the army something\" without a line of code.
        NationField("army_experience", n => n.ArmyExperience, (n, v) => n.ArmyExperience = v, 0f, 100000f,
            "Army experience: what the army Doctrines are paid for in. Earned by fighting, drilling and funding.");
        NationField("air_experience", n => n.AirExperience, (n, v) => n.AirExperience = v, 0f, 100000f,
            "Air experience: what the Doctrine of the sky is paid for in. Earned by flying real missions.");
        NationField("naval_experience", n => n.NavalExperience, (n, v) => n.NavalExperience = v, 0f, 100000f,
            "Naval experience: what the Doctrine of the coast is paid for in. Earned at sea and in action.");
        NationField("political_points", n => n.PoliticalPoints, (n, v) => n.PoliticalPoints = v, 0f, 100000f,
            "Political credit: what a government can push through besides money. Spent on rewriting a war Doctrine.");
        // A movement's own figures (Movements). On a country that is not one they read nought
        // and writing them does nothing, so an event can be written for "all" without asking.
        NationField("movement.cadres", n => n.Movement?.Cadres ?? 0f,
            (n, v) => { if (n.Movement is not null) n.Movement.Cadres = v; }, 0f, float.PositiveInfinity,
            "A movement's organisers and volunteers, in people.");
        NationField("movement.legitimacy", n => n.Movement?.Legitimacy ?? 0f,
            (n, v) => { if (n.Movement is not null) n.Movement.Legitimacy = v; }, 0f, 1f,
            "A movement's standing, 0-1.");
        NationField("movement.heat", n => n.Movement?.Heat ?? 0f,
            (n, v) => { if (n.Movement is not null) n.Movement.Heat = v; }, 0f, 1f,
            "The temper of the country hosting a movement's headquarters, 0-1; at the expel line it is thrown out.");
        NationField("movement.support", n => n.Movement is null || n.Movement.Support.Count == 0 ? 0f : n.Movement.Support.Values.Max(),
            (n, v) =>
            {
                if (n.Movement is null || n.Movement.Support.Count == 0) return;
                // Every region's support moves by the same share the strongest one did.
                var strongest = n.Movement.Support.Values.Max();
                var scale = strongest <= 0f ? 1f : v / strongest;
                foreach (var region in n.Movement.Support.Keys.ToList())
                    n.Movement.Support[region] = Math.Clamp(n.Movement.Support[region] * scale, 0f, 0.92f);
            }, 0f, 0.92f,
            "A movement's support in its strongest region, 0-1; changing it moves every region by the same share.");
        NationField("gdp", n => n.Gdp, (n, v) => n.Gdp = v, 0.01f, float.PositiveInfinity,
            "Annual gross domestic product.");
        NationField("productivity", n => n.Productivity, (n, v) => n.Productivity = v, 0.05f, 8f,
            "Output multiplier; the long-run driver of GDP growth.");
        NationField("stability", n => n.Stability, (n, v) => n.Stability = v, 0.10f, 0.98f,
            "Domestic political stability, 0-1.");
        NationField("war_support", n => n.WarSupport, (n, v) => n.WarSupport = v, 0f, 1f,
            "Public willingness to sustain a war, 0-1.");
        NationField("standard_of_living", n => n.StandardOfLiving, (n, v) => n.StandardOfLiving = v, 0f, 1f,
            "National average standard of living, 0-1.");
        NationField("social.expected_living", n => n.ExpectedLivingStandard,
            (n, v) => n.ExpectedLivingStandard = v, 0f, 1f, "The population's established living-standard expectation, 0-1.");
        NationField("social.domestic_incident", n => n.DomesticImage?.Incident ?? 0f,
            (n, v) => { n.DomesticImage ??= new PublicImage(); n.DomesticImage.Incident = v; }, -1f, 1f,
            "Pending domestic reputation impulse, -1 to 1; it is absorbed gradually, not an instant status label.");
        NationField("literacy", n => n.Literacy, (n, v) => n.Literacy = v, 0.05f, 0.999f,
            "Share of the population that is literate, 0-1.");
        NationField("brain_drain", n => n.BrainDrainRate, (n, v) => n.BrainDrainRate = v, -1f, 1f,
            "Signed human-capital migration flow, -1 (severe brain drain) to +1 (strong talent inflow).");
        // Money, which a script could previously neither read nor move - so a mod could add
        // a decision that emptied the treasury and none that noticed what it did to prices.
        // Bounded by what the model itself allows rather than by a number written here, so a
        // mod that widens the floor and ceiling in common/inflation widens these too.
        // Bounded loosely on purpose. This runs once, in a static constructor, before any
        // mod has been read - so a limit taken from `common/inflation` here would be the
        // base game's for ever, and would quietly overrule a mod that widened it. Both of
        // these are clamped again where they are stored: inflation by the weekly tick
        // against the declared floor and ceiling, and the key rate by the budget line it
        // lives on. What is left here is only a guard against a script writing nonsense.
        NationField("inflation", n => n.Inflation, (n, v) => n.Inflation = v, -1f, 100f,
            "Annual inflation. 0.04 is four per cent. Re-clamped to the floor and ceiling in common/inflation.");
        NationField("key_rate", CentralBank.KeyRate, CentralBank.SetKeyRate, 0f, 100f,
            "The central bank's key rate, per annum. Clamped to what the debt_interest budget line allows. Writing it does not take the rate off the bank - see government_sets_key_rate.");
        NationField("government_sets_key_rate",
            n => n.GovernmentSetsKeyRate ? 1f : 0f,
            (n, v) => n.GovernmentSetsKeyRate = v >= 0.5f, 0f, 1f,
            "1 when the government has taken the key rate off its own central bank, 0 when the bank is independent.");
        NationField("construction_capacity", n => n.ConstructionCapacity, (n, v) => n.ConstructionCapacity = v,
            0f, float.PositiveInfinity, "Parallel construction throughput.");
        NationField("manpower", n => n.ManpowerReserve, (n, v) => n.ManpowerReserve = v, 0f, float.PositiveInfinity,
            "Trained manpower available for reinforcement.");
        NationField("tax_rate", n => n.TaxRate, (n, v) => n.TaxRate = v, 0f, 0.75f,
            "Headline tax rate, 0-1.");
        NationField("military_spending", n => n.MilitarySpending, (n, v) => n.MilitarySpending = v, 0f, 0.5f,
            "Share of output spent on the armed forces.");
        NationField("welfare_spending", n => n.WelfareSpending, (n, v) => n.WelfareSpending = v, 0f, 0.5f,
            "Share of output spent on social welfare.");
        NationField("education_spending", n => n.EducationSpending, (n, v) => n.EducationSpending = v, 0f, 0.5f,
            "Share of output spent on education and science.");
        NationField("market_access", n => n.MarketAccess, (n, v) => n.MarketAccess = v, 0f, 1f,
            "Access to the world market, 0-1.");
        NationField("imperial_overstretch", n => n.ImperialOverstretch, (n, v) => n.ImperialOverstretch = v, 0f, 1f,
            "Imperial overstretch of a state governing distant or divergent territories, 0-1.");
        NationField("notoriety", n => n.Notoriety, (n, v) => n.Notoriety = v, 0f, 100f,
            "International notoriety from conquests and broken treaties, 0-100.");
        NationField("union_cohesion", n => n.UnionCohesion, (n, v) => n.UnionCohesion = v, 0f, 1f,
            "Cohesion of a proclaimed union or restored state, 0-1.");
        NationField("population", n => n.Population, (n, v) => n.Population = (long)MathF.Max(0f, v),
            0f, float.PositiveInfinity, "National population headcount.");

        StateField("state.standard_of_living", s => s.StandardOfLiving, (s, v) => s.StandardOfLiving = v, 0f, 1f,
            "Standard of living in every state the nation owns.");
        StateField("state.radicalism", s => s.Radicalism, (s, v) => s.Radicalism = v, 0f, 1f,
            "Radicalism in every state the nation owns.");
        StateField("state.literacy", s => s.Literacy, (s, v) => s.Literacy = v, 0f, 1f,
            "Literacy in every state the nation owns.");
        StateField("state.urbanization", s => s.Urbanization, (s, v) => s.Urbanization = v, 0f, 1f,
            "Urbanisation in every state the nation owns.");
        StateField("state.population", s => s.Population, (s, v) => s.Population = (long)MathF.Max(0f, v),
            0f, float.PositiveInfinity, "Population of every state the nation owns.");
        StateField("state.stability", s => s.Stability, (s, v) => s.Stability = v, 0f, 1f,
            "Stability in every state the nation owns.");
        StateField("state.war_damage", s => s.WarDamage, (s, v) => s.WarDamage = v, 0f, 1f,
            "War damage in every state the nation owns: 0 untouched, 1 ruins.");
        StateField("state.brain_drain", s => s.BrainDrainPressure, (s, v) => s.BrainDrainPressure = v, 0f, 1f,
            "Brain drain emigration pressure in every state the nation owns, 0-1.");

        // The country's forces, every division and every fleet at once: an army rested, drilled
        // or re-equipped, a navy back from a refit.
        ArmyField("army.organisation", a => a.Organisation, (a, v) => a.Organisation = v, 0f, 1f,
            "Organisation of every division the nation has, 0-1.");
        ArmyField("army.entrenchment", a => a.Entrenchment, (a, v) => a.Entrenchment = v, 0f, 1f,
            "Entrenchment of every division the nation has, 0-1.");
        ArmyField("army.experience", a => a.Experience, (a, v) => a.Experience = v, 0f, 1f,
            "Experience of every division the nation has, 0-1.");
        ArmyField("army.equipment", a => a.Equipment, (a, v) => a.Equipment = v, 0f, 1f,
            "Equipment of every division the nation has, 0-1.");
        FleetField("fleet.readiness", f => f.Readiness, (f, v) => f.Readiness = v, 0f, 1f,
            "Readiness of every fleet the nation has, 0-1.");
        FleetField("fleet.supply", f => f.Supply, (f, v) => f.Supply = v, 0f, 1f,
            "Stores of every fleet the nation has, 0-1.");
        // Research points on whatever the country is researching now; nothing when it is not.
        NationField("research",
            n => n.ResearchProgress.GetValueOrDefault(n.CurrentResearch ?? ""),
            (n, v) => { if (!string.IsNullOrEmpty(n.CurrentResearch)) n.ResearchProgress[n.CurrentResearch] = v; },
            0f, float.PositiveInfinity, "Research points on the technology being researched now.");
    }

    private static void ArmyField(string name, Func<Army, float> get, Action<Army, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetArmy = get, SetArmy = set, Min = min, Max = max, Help = help };

    private static void FleetField(string name, Func<Fleet, float> get, Action<Fleet, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetFleet = get, SetFleet = set, Min = min, Max = max, Help = help };

    private static void NationField(string name, Func<Nation, float> get, Action<Nation, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetNation = get, SetNation = set, Min = min, Max = max, Help = help };

    private static void StateField(string name, Func<ScenarioState, float> get, Action<ScenarioState, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetState = get, SetState = set, Min = min, Max = max, Help = help };

    /// <summary>Every key content may write to, for documentation and console help.</summary>
    public static IEnumerable<(string Key, string Help)> Vocabulary =>
        Fields.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal).Select(entry => (entry.Name, entry.Help))
            .Concat(new[] { ("reputation.culture.ID", "Audience-scoped cultural narrative impulse, -1 to 1. Also reputation.nation.TAG and reputation.coalition.TARGET:DAY; suffixes work as usual.") });

    private static bool FeatureEffect(string key, out string id)
    {
        id = "";
        if (key is null || !key.StartsWith("feature.", StringComparison.OrdinalIgnoreCase) ||
            !key.EndsWith("_set", StringComparison.OrdinalIgnoreCase)) return false;
        id = key[8..^4].ToLowerInvariant();
        return MechanicFeatures.ValidId(id);
    }

    public static bool IsKnown(string key) => FeatureEffect(key, out _) || Resolve(key, out _, out _);

    private static bool Resolve(string rawKey, out Field field, out Operation operation)
    {
        field = null;
        operation = Operation.Add;
        if (string.IsNullOrWhiteSpace(rawKey)) return false;

        // Subject ids retain their spelling: a mod may declare a culture with a hyphen.
        var key = rawKey.Trim();
        foreach (var (suffix, candidate) in Suffixes)
        {
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var stem = key[..^suffix.Length];
            if (!FindField(stem, out field)) continue;
            operation = candidate;
            return true;
        }
        return FindField(key, out field);
    }

    private static bool FindField(string key, out Field field)
    {
        if (Fields.TryGetValue(key.Replace('-', '_'), out field)) return true;
        if (!key.StartsWith("reputation.", StringComparison.OrdinalIgnoreCase) || key.Length > 128) return false;
        var parts = key.Split('.', 3);
        if (parts.Length != 3 || parts[2].Length == 0 ||
            !parts[2].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or ':')) return false;
        var kind = parts[1].ToLowerInvariant();
        if (kind is not ("culture" or "nation" or "coalition")) return false;
        var subject = kind + ":" + (kind == "nation" ? parts[2].ToUpperInvariant() : parts[2]);
        field = new Field { Name = "reputation." + subject, Min = -1f, Max = 1f,
            GetNation = n => SocialMemory.Image(n, subject)?.Incident ?? 0f };
        return true;
    }

    /// <summary>One parsed entry, kept flat so applying an effect allocates nothing.</summary>
    public readonly struct Entry
    {
        internal Entry(string key, float value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }
        public float Value { get; }
    }

    [JsonConverter(typeof(ScriptedEffectSaveConverter))]
    public sealed class Effect
    {
        private readonly List<Entry> _entries = new();
        private readonly List<(string Id, int Days)> _grants = new();
        private readonly List<string> _revokes = new();

        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>National spirits the effect hangs on the country, and for how long (0: their own length).</summary>
        public IReadOnlyList<(string Id, int Days)> Grants => _grants;

        /// <summary>National spirits the effect takes off the country.</summary>
        public IReadOnlyList<string> Revokes => _revokes;

        public bool IsEmpty => _entries.Count == 0 && _grants.Count == 0 && _revokes.Count == 0;

        internal void Add(string key, float value) => _entries.Add(new Entry(key, value));
        internal void Grant(string id, int days) => _grants.Add((id, days));
        internal void Revoke(string id) => _revokes.Add(id);

        public void Apply(Campaign campaign, Nation nation)
        {
            if (nation is null) return;
            foreach (var id in _revokes) ModifierSystem.Revoke(nation, id);
            foreach (var (id, days) in _grants) ModifierSystem.Grant(campaign, nation, id, days);
            List<ScenarioState> owned = null;

            foreach (var entry in _entries)
            {
                if (!float.IsFinite(entry.Value)) continue;
                if (FeatureEffect(entry.Key, out var feature))
                {
                    campaign.SetFeature(feature, entry.Value >= .5f);
                    continue;
                }
                if (!Resolve(entry.Key, out var field, out var operation)) continue;
                if (field.Name == "social.expected_living" && !SocialMemory.HabitsEnabled(campaign)) continue;
                if (field.Name == "social.domestic_incident" && !SocialMemory.ImagesEnabled(campaign)) continue;

                if (field.Name.StartsWith("reputation.", StringComparison.Ordinal))
                {
                    if (!SocialMemory.ImagesEnabled(campaign)) continue;
                    var subject = field.Name[11..];
                    if (subject.StartsWith("nation:", StringComparison.Ordinal) && campaign.NationByTag(subject[7..]) is null) continue;
                    if (subject.StartsWith("culture:", StringComparison.Ordinal) && DemographyCatalog.Culture(subject[8..]) is null) continue;
                    if (subject.StartsWith("coalition:", StringComparison.Ordinal) &&
                        !campaign.Coalitions.Any(group => SocialMemory.CoalitionKey(group) == subject)) continue;
                    var image = SocialMemory.Image(nation, subject, true);
                    var pending = Combine(image.Incident, entry.Value, operation, field);
                    image.Incident = pending;
                    image.LastReason = "Scripted public narrative";
                    image.LastIncidentDay = campaign.ElapsedDays;
                    continue;
                }

                if (field.SetNation is not null)
                {
                    field.SetNation(nation, Combine(field.GetNation(nation), entry.Value, operation, field));
                    continue;
                }
                if (field.SetArmy is not null)
                {
                    foreach (var army in campaign.Armies)
                        if (army.NationId == nation.Id) field.SetArmy(army, Combine(field.GetArmy(army), entry.Value, operation, field));
                    continue;
                }
                if (field.SetFleet is not null)
                {
                    foreach (var fleet in campaign.Fleets)
                        if (fleet.NationId == nation.Id) field.SetFleet(fleet, Combine(field.GetFleet(fleet), entry.Value, operation, field));
                    continue;
                }

                owned ??= campaign.StatesOf(nation).ToList();
                foreach (var state in owned)
                    field.SetState(state, Combine(field.GetState(state), entry.Value, operation, field));
            }
            // Keep compound scripts ordered: charges and refunds in one effect cancel
            // before legacy overdrafts are imported. This is a balance-sheet adjustment,
            // not an ordinary bond issue and not a weekly settlement.
            SovereignFinance.NormalizeAccounts(campaign, nation);
        }

        internal static float PreviewCombine(float current, float value, Operation operation, string fieldName, float min, float max)
        {
            var result = operation switch
            {
                Operation.Percent when fieldName == "treasury" => current + MathF.Abs(current) * value,
                Operation.Percent => current * (1f + value),
                Operation.Mult => current * value,
                Operation.Set => value,
                _ => current + value
            };
            return M.Clamp(result, min, max);
        }

        private static float Combine(float current, float value, Operation operation, Field field) =>
            PreviewCombine(current, value, operation, field.Name, field.Min, field.Max);

        /// <summary>Plain-language summary, so the UI can describe a modded option that shipped no text.</summary>
        public string Describe()
        {
            if (_entries.Count == 0) return "No effect.";
            return string.Join(", ", _entries.Select(entry =>
            {
                var sign = entry.Value >= 0f ? "+" : "";
                return $"{entry.Key} {sign}{entry.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
            }));
        }
    }

    /// <summary>Pending event options must keep their actual effects in a save. A plain
    /// deserializer cannot repopulate the private lists behind the read-only views.</summary>
    public sealed class ScriptedEffectSaveConverter : JsonConverter<Effect>
    {
        public override Effect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a scripted effect.");
            var effect = new Effect();
            if (root.TryGetProperty("Entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("Key", out var key) && entry.TryGetProperty("Value", out var value))
                        effect.Add(key.GetString() ?? "", value.GetSingle());
            if (root.TryGetProperty("Grants", out var grants) && grants.ValueKind == JsonValueKind.Array)
                foreach (var grant in grants.EnumerateArray())
                {
                    if (grant.ValueKind != JsonValueKind.Object) continue;
                    var id = grant.TryGetProperty("Id", out var named) ? named.GetString()
                        : grant.TryGetProperty("Item1", out var old) ? old.GetString() : "";
                    var days = grant.TryGetProperty("Days", out var duration) ? duration.GetInt32()
                        : grant.TryGetProperty("Item2", out var oldDays) ? oldDays.GetInt32() : 0;
                    if (!string.IsNullOrWhiteSpace(id)) effect.Grant(id, days);
                }
            if (root.TryGetProperty("Revokes", out var revokes) && revokes.ValueKind == JsonValueKind.Array)
                foreach (var revoke in revokes.EnumerateArray())
                    if (revoke.ValueKind == JsonValueKind.String) effect.Revoke(revoke.GetString() ?? "");
            return effect;
        }

        public override void Write(Utf8JsonWriter writer, Effect value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Entries");
            writer.WriteStartArray();
            foreach (var entry in value.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("Key", entry.Key);
                writer.WriteNumber("Value", entry.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("Grants");
            writer.WriteStartArray();
            foreach (var (id, days) in value.Grants)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", id);
                writer.WriteNumber("Days", days);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("Revokes");
            JsonSerializer.Serialize(writer, value.Revokes, options);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Parse an "effects" object. An unknown key costs that one line, not the whole file:
    /// one typo in a mod should never take the rest of the mod down with it.
    /// </summary>
    public static Effect Parse(JsonElement element, string context, List<string> errors, int depth = 0)
    {
        var effect = new Effect();
        if (element.ValueKind != JsonValueKind.Object) return effect;

        foreach (var property in element.EnumerateObject())
        {
            // A named effect out of `common/scripted_effects`, folded in before this
            // block's own entries so the block can override what it borrowed.
            if (property.Name.Equals("use", StringComparison.OrdinalIgnoreCase))
            {
                if (depth > 8)
                {
                    errors?.Add($"{context}: scripted effects reference each other in a circle.");
                    continue;
                }
                foreach (var reference in ScriptReferences.Names(property.Value))
                {
                    if (!ScriptLibrary.TryEffect(reference, out var block))
                    {
                        errors?.Add($"{context}: unknown scripted effect '{reference}'.");
                        continue;
                    }
                    var borrowed = Parse(block, $"scripted_effects/{reference}", errors, depth + 1);
                    foreach (var entry in borrowed.Entries) effect.Add(entry.Key, entry.Value);
                    foreach (var (id, days) in borrowed.Grants) effect.Grant(id, days);
                    foreach (var id in borrowed.Revokes) effect.Revoke(id);
                }
                continue;
            }

            // A national spirit: { "add_modifier": { "war_economy": 365 } } hangs it on the
            // country for that many days (0 for its own length), and "remove_modifier" takes
            // one or a list of them off. The whole of how an event or a decision says "for a
            // year, this country is on a war footing".
            if (property.Name.Equals("add_modifier", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    errors?.Add($"{context}: add_modifier takes {{ \"modifier\": days }}.");
                    continue;
                }
                foreach (var grant in property.Value.EnumerateObject())
                {
                    if (grant.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (ModifierCatalog.Get(grant.Name) is null)
                    {
                        errors?.Add($"{context}: add_modifier names '{grant.Name}', which common/modifiers does not declare.");
                        continue;
                    }
                    var days = grant.Value.ValueKind == JsonValueKind.Number && grant.Value.TryGetInt32(out var parsed) ? parsed : 0;
                    effect.Grant(grant.Name, Math.Max(0, days));
                }
                continue;
            }
            if (property.Name.Equals("remove_modifier", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var name in ScriptReferences.Names(property.Value)) effect.Revoke(name);
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetSingle(out var value) || !float.IsFinite(value))
            {
                errors?.Add($"{context}: effect '{property.Name}' must be a number.");
                continue;
            }
            if (!IsKnown(property.Name))
            {
                errors?.Add($"{context}: unknown effect key '{property.Name}'. Known keys: " +
                            string.Join(", ", Vocabulary.Select(item => item.Key)));
                continue;
            }
            effect.Add(property.Name, value);
        }
        return effect;
    }

    /// <summary>
    /// What a player reads for an effect: one line per consequence, in the same shape
    /// everywhere - "Stability: +3%", "Treasury: -2%", "Manpower: +50,000" (item 67).
    ///
    /// Events and decisions used to show only their prose - "the country is smaller, and
    /// every other region has watched it work" - with the numbers underneath it nowhere, so a
    /// player choosing between two options was choosing between two paragraphs. The lines
    /// are read out of the effect itself, so they cannot disagree with what it does, and a
    /// mod's effect is described in the same words as the game's own without writing any.
    /// </summary>
    public static IReadOnlyList<string> Lines(Effect effect)
    {
        var lines = new List<string>();
        if (effect is null) return lines;
        // The spirits first: they are usually the point of the option.
        lines.AddRange(SpiritLines(effect.Grants));
        foreach (var id in effect.Revokes)
            if (ModifierCatalog.Get(id) is { } spirit)
                lines.Add(Loc.Format("Loses «{0}»", Loc.Get(spirit.Name)));
        foreach (var entry in effect.Entries)
        {
            if (FeatureEffect(entry.Key, out var feature))
            {
                lines.Add(Loc.Format(entry.Value >= .5f ? "Enable mechanic: {0}" : "Disable mechanic: {0}", feature));
                continue;
            }
            if (!Resolve(entry.Key, out var field, out var operation)) continue;
            var narrative = field.Name.StartsWith("reputation.", StringComparison.Ordinal);
            var label = narrative ? Loc.Format("Public narrative: {0}", field.Name[11..]) : Loc.Get(Labels.GetValueOrDefault(field.Name, field.Name));
            // The same word is a heading elsewhere - the top bar's STABILITY - and a table that
            // capitalised it there shouts it here. Put back into sentence case, unless it is a
            // short acronym like GDP, which is capitals in every language.
            if (label.Length > 4 && label == label.ToUpper(CultureInfo.CurrentCulture))
                label = label[..1] + label[1..].ToLower(CultureInfo.CurrentCulture);
            var fraction = narrative || Fractions.Contains(field.Name) || field.Name.StartsWith("social.", StringComparison.Ordinal);
            var culture = CultureInfo.CurrentCulture;
            var value = operation switch
            {
                // A share of what is there now.
                Operation.Percent => (entry.Value * 100f).ToString("+0.#;-0.#", culture) + "%",
                Operation.Mult => "×" + entry.Value.ToString("0.##", culture),
                Operation.Set => "= " + (fraction
                    ? (entry.Value * 100f).ToString("0.#", culture) + "%"
                    : entry.Value.ToString("N0", culture)),
                // A 0-to-1 quantity moves in percentage points; everything else in its own units.
                _ => fraction
                    ? (entry.Value * 100f).ToString("+0.#;-0.#", culture) + "%"
                    : Math.Abs(entry.Value) >= 1000f
                        ? entry.Value.ToString("+#,0;-#,0", culture)
                        : entry.Value.ToString("+0.##;-0.##", culture)
            };
            lines.Add(Loc.Format("{0}: {1}", label, value));
        }
        return lines;
    }

    /// <summary>
    /// "Gains «War economy» for 365 days", and under it, indented, what the spirit does while it
    /// lasts - so a choice that hangs a spirit on the country says what the spirit is.
    /// </summary>
    public static IReadOnlyList<string> SpiritLines(IEnumerable<(string Id, int Days)> grants)
    {
        var lines = new List<string>();
        if (grants is null) return lines;
        foreach (var (id, days) in grants)
        {
            var spirit = ModifierCatalog.Get(id);
            if (spirit is null) continue;
            var length = days > 0 ? days : spirit.Days;
            lines.Add(length > 0
                ? Loc.Format("Gains «{0}» for {1} days", Loc.Get(spirit.Name), length)
                : Loc.Format("Gains «{0}»", Loc.Get(spirit.Name)));
            foreach (var line in NationalModifiers.Lines(spirit.Modifiers)) lines.Add("  " + line);
        }
        return lines;
    }

    /// <summary>The same lines under a heading, ready to go under a tooltip's prose. Empty when there are none.</summary>
    public static string Block(Effect effect)
    {
        var lines = Lines(effect);
        return lines.Count == 0 ? "" : Loc.Get("Effects:") + "\n" + string.Join("\n", lines.Select(line => "  " + line));
    }

    /// <summary>
    /// Preview the exact direct state transitions an effect will apply to <paramref name="nation"/>
    /// right now ("Stability: 72.0% → 73.5% (+1.5%)"), without mutating the campaign or nation.
    /// Separates immediate guaranteed state changes from ongoing national spirits.
    /// </summary>
    public static IReadOnlyList<string> PreviewForNation(Campaign campaign, Nation nation, Effect effect, float treasuryCost = 0f)
    {
        var lines = new List<string>();
        if (campaign is null || nation is null) return lines;
        if (treasuryCost > 0.0001f)
        {
            var afterCash = nation.Treasury - treasuryCost;
            lines.Add(Loc.Format("{0}: {1} → {2} ({3})",
                Loc.Get("Treasury"), Fmt.Money(nation.Treasury), Fmt.Money(afterCash), Fmt.MoneyDelta(-treasuryCost)));
        }
        if (effect is null) return lines;
        lines.AddRange(SpiritLines(effect.Grants));
        foreach (var id in effect.Revokes)
            if (ModifierCatalog.Get(id) is { } spirit)
                lines.Add(Loc.Format("Loses «{0}»", Loc.Get(spirit.Name)));

        var owned = campaign.StatesOf(nation);
        foreach (var entry in effect.Entries)
        {
            if (FeatureEffect(entry.Key, out var feature))
            {
                lines.Add(Loc.Format(entry.Value >= .5f ? "Enable mechanic: {0}" : "Disable mechanic: {0}", feature));
                continue;
            }
            if (!Resolve(entry.Key, out var field, out var operation)) continue;
            var label = field.Name.StartsWith("reputation.", StringComparison.Ordinal)
                ? Loc.Format("Public narrative: {0}", field.Name[11..])
                : Loc.Get(Labels.GetValueOrDefault(field.Name, field.Name));
            if (label.Length > 4 && label == label.ToUpper(CultureInfo.CurrentCulture))
                label = label[..1] + label[1..].ToLower(CultureInfo.CurrentCulture);

            float before;
            if (field.GetNation is not null) before = field.GetNation(nation);
            else if (field.GetState is not null && owned.Length > 0) before = owned.Average(s => field.GetState(s));
            else if (field.GetArmy is not null)
            {
                var armies = campaign.Armies.Where(a => a.NationId == nation.Id).ToArray();
                if (armies.Length == 0) continue;
                before = armies.Average(a => field.GetArmy(a));
            }
            else if (field.GetFleet is not null)
            {
                var fleets = campaign.Fleets.Where(f => f.NationId == nation.Id).ToArray();
                if (fleets.Length == 0) continue;
                before = fleets.Average(f => field.GetFleet(f));
            }
            else continue;

            var after = Effect.PreviewCombine(before, entry.Value, operation, field.Name, field.Min, field.Max);
            var delta = after - before;
            lines.Add(Loc.Format("{0}: {1} → {2} ({3})",
                label,
                FormatPreviewValue(field.Name, before),
                FormatPreviewValue(field.Name, after),
                FormatPreviewDelta(field.Name, delta)));
        }
        return lines;
    }

    private static string FormatPreviewValue(string fieldName, float value)
    {
        if (fieldName is "treasury" or "public_debt" or "debt_arrears" or "payment_arrears" or "gdp")
            return Fmt.Money(value);
        if (fieldName is "manpower" or "population" or "state.population" or "movement.cadres")
            return Fmt.People((long)MathF.Round(value));
        if (Fractions.Contains(fieldName) || fieldName.StartsWith("social.", StringComparison.Ordinal) ||
            fieldName.StartsWith("reputation.", StringComparison.Ordinal))
            return Fmt.Percent(value);
        return value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private static string FormatPreviewDelta(string fieldName, float delta)
    {
        if (fieldName is "treasury" or "public_debt" or "debt_arrears" or "payment_arrears" or "gdp")
            return Fmt.MoneyDelta(delta);
        if (fieldName is "manpower" or "population" or "state.population" or "movement.cadres")
            return Fmt.PeopleDelta(Math.Round(delta));
        if (Fractions.Contains(fieldName) || fieldName.StartsWith("social.", StringComparison.Ordinal) ||
            fieldName.StartsWith("reputation.", StringComparison.Ordinal))
            return (delta * 100f).ToString("+0.#;-0.#;0.#", CultureInfo.CurrentCulture) + "%";
        return delta.ToString("+0.##;-0.##;0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>Which fields are a share of one, so that +0.03 reads as three percentage points.</summary>
    private static readonly HashSet<string> Fractions = new(StringComparer.OrdinalIgnoreCase)
    {
        "stability", "war_support", "standard_of_living", "literacy", "inflation", "key_rate",
        "credit_confidence",
        "tax_rate", "military_spending", "welfare_spending", "education_spending", "market_access",
        "movement.legitimacy", "movement.heat", "movement.support",
        "state.standard_of_living", "state.radicalism", "state.literacy", "state.urbanization", "state.stability",
        "state.war_damage", "worst_war_damage", "brain_drain", "state.brain_drain",
        "army.organisation", "army.entrenchment", "army.experience", "army.equipment", "fleet.readiness", "fleet.supply"
    };

    /// <summary>What each field is called on screen - an English key the tables translate.</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["treasury"] = "Treasury", ["public_debt"] = "Public debt",
        ["credit_confidence"] = "Credit confidence",
        ["debt_arrears"] = "Debt arrears", ["payment_arrears"] = "Public payment arrears",
        ["gdp"] = "GDP", ["productivity"] = "Productivity",
        ["stability"] = "Stability", ["war_support"] = "War support",
        ["standard_of_living"] = "Standard of living", ["literacy"] = "Literacy",
        ["inflation"] = "Inflation", ["key_rate"] = "Key rate",
        ["government_sets_key_rate"] = "The government sets the key rate",
        ["construction_capacity"] = "Construction capacity", ["manpower"] = "Manpower",
        ["tax_rate"] = "Tax rate", ["military_spending"] = "Military spending",
        ["welfare_spending"] = "Welfare spending", ["education_spending"] = "Education spending",
        ["market_access"] = "Market access", ["population"] = "Population",
        ["state.standard_of_living"] = "Standard of living in every region",
        ["state.radicalism"] = "Radicalism in every region",
        ["state.literacy"] = "Literacy in every region",
        ["state.urbanization"] = "Urbanisation in every region",
        ["state.population"] = "Population of every region",
        ["state.stability"] = "Stability in every region",
        ["state.war_damage"] = "War damage in every region",
        ["worst_war_damage"] = "War damage in the worst-hit region",
        ["army.organisation"] = "Organisation of every division",
        ["army.entrenchment"] = "Entrenchment of every division",
        ["army.experience"] = "Experience of every division",
        ["army.equipment"] = "Equipment of every division",
        ["fleet.readiness"] = "Readiness of every fleet",
        ["fleet.supply"] = "Stores of every fleet",
        ["research"] = "Research points",
        ["brain_drain"] = "Brain drain",
        ["state.brain_drain"] = "Brain drain pressure in every region",
        ["movement.cadres"] = "Cadres",
        ["movement.legitimacy"] = "Standing",
        ["movement.heat"] = "The host's temper",
        ["movement.support"] = "Support in every region"
    };

    /// <summary>Build an effect in code, for the compiled-in defaults.</summary>
    public static Effect From(params (string Key, float Value)[] entries)
    {
        var effect = new Effect();
        foreach (var (key, value) in entries) effect.Add(key, value);
        return effect;
    }
}

/// <summary>
/// The other half of the scripted layer: whether a piece of content may fire at all.
/// Same vocabulary as the effects, so anything a mod can change it can also test, written
/// as "trigger": { "min_stability": 0.4, "max_treasury": 500, "at_war": true }.
/// </summary>
public sealed class ScriptedTrigger
{
    private readonly List<(string Key, float Value, bool IsMinimum)> _bounds = new();
    private readonly List<string> _tags = new();
    private bool? _atWar;

    public bool IsEmpty => _bounds.Count == 0 && _atWar is null && _tags.Count == 0;

    public static ScriptedTrigger Parse(JsonElement element, string context, List<string> errors,
        int depth = 0)
    {
        var trigger = new ScriptedTrigger();
        if (element.ValueKind != JsonValueKind.Object) return trigger;

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.Trim().Replace('-', '_');

            // A named condition out of `common/scripted_triggers`. Its clauses join this
            // block's, and every clause still has to hold - a trigger is an "and".
            if (name.Equals("use", StringComparison.OrdinalIgnoreCase))
            {
                if (depth > 8)
                {
                    errors?.Add($"{context}: scripted triggers reference each other in a circle.");
                    continue;
                }
                foreach (var reference in ScriptReferences.Names(property.Value))
                {
                    if (!ScriptLibrary.TryTrigger(reference, out var block))
                    {
                        errors?.Add($"{context}: unknown scripted trigger '{reference}'.");
                        continue;
                    }
                    trigger.Absorb(Parse(block, $"scripted_triggers/{reference}", errors, depth + 1));
                }
                continue;
            }

            if (name.Equals("at_war", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    trigger._atWar = property.Value.GetBoolean();
                else errors?.Add($"{context}: 'at_war' must be true or false.");
                continue;
            }

            if (name.Equals("tags", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in property.Value.EnumerateArray())
                        if (tag.ValueKind == JsonValueKind.String)
                            trigger._tags.Add(tag.GetString() ?? "");
                }
                else errors?.Add($"{context}: 'tags' must be an array of country tags.");
                continue;
            }

            var isMinimum = name.StartsWith("min_", StringComparison.OrdinalIgnoreCase);
            var isMaximum = name.StartsWith("max_", StringComparison.OrdinalIgnoreCase);
            if (!isMinimum && !isMaximum)
            {
                errors?.Add($"{context}: trigger '{property.Name}' must be min_<key>, max_<key>, at_war or tags.");
                continue;
            }
            if (!property.Value.TryGetSingle(out var value))
            {
                errors?.Add($"{context}: trigger '{property.Name}' must be a number.");
                continue;
            }

            var key = name[4..];
            if (!ScriptedEffects.IsKnown(key) && !ScriptedValues.IsReadOnly(key))
            {
                errors?.Add($"{context}: unknown trigger key '{key}'.");
                continue;
            }
            trigger._bounds.Add((key, value, isMinimum));
        }
        return trigger;
    }

    /// <summary>Take another trigger's clauses into this one. Both then have to hold.</summary>
    internal void Absorb(ScriptedTrigger other)
    {
        if (other is null) return;
        _bounds.AddRange(other._bounds);
        _tags.AddRange(other._tags);
        _atWar ??= other._atWar;
    }

    public bool Matches(Campaign campaign, Nation nation)
    {
        if (nation is null) return false;
        if (_tags.Count > 0 && !_tags.Any(tag => string.Equals(tag, nation.Tag, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (_atWar.HasValue && campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)) != _atWar.Value)
            return false;

        foreach (var (key, value, isMinimum) in _bounds)
        {
            if (!ScriptedValues.TryRead(campaign, nation, key, out var current)) continue;
            if (isMinimum && current < value) return false;
            if (!isMinimum && current > value) return false;
        }
        return true;
    }

    /// <summary>
    /// Dimensionless distance from <paramref name="nation"/>'s current state to satisfying this
    /// trigger: 0 when <see cref="Matches"/> is already true, positive when numeric bounds are
    /// not yet met, and <see cref="float.PositiveInfinity"/> when a discrete condition (such as
    /// <c>at_war</c> or <c>tags</c>) fails or a value cannot be read.
    /// </summary>
    public float Shortfall(Campaign campaign, Nation nation)
    {
        if (nation is null) return float.PositiveInfinity;
        if (_tags.Count > 0 && !_tags.Any(tag => string.Equals(tag, nation.Tag, StringComparison.OrdinalIgnoreCase)))
            return float.PositiveInfinity;
        if (_atWar.HasValue && campaign is not null &&
            campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)) != _atWar.Value)
            return float.PositiveInfinity;

        var total = 0f;
        foreach (var (key, target, isMinimum) in _bounds)
        {
            if (!ScriptedValues.TryRead(campaign, nation, key, out var current)) return float.PositiveInfinity;
            var raw = isMinimum ? target - current : current - target;
            if (raw <= 0f) continue;
            var normalized = key.Trim().Replace('-', '_').ToLowerInvariant() switch
            {
                "treasury" or "public_debt" or "debt_arrears" or "payment_arrears" or "gdp" =>
                    raw / MathF.Max(0.1f, nation.Gdp),
                "manpower" or "population" or "state.population" =>
                    raw / MathF.Max(1f, nation.Population * 0.02f),
                "army_experience" or "air_experience" or "naval_experience" or "political_points" =>
                    raw / 100f,
                _ => raw
            };
            total += normalized;
        }
        return total;
    }
}

/// <summary>A `use` entry is one name or a list of them.</summary>
internal static class ScriptReferences
{
    public static IEnumerable<string> Names(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            yield return value.GetString() ?? "";
            yield break;
        }
        if (value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var element in value.EnumerateArray())
            if (element.ValueKind == JsonValueKind.String) yield return element.GetString() ?? "";
    }
}

/// <summary>Reads the same keys the effects write, for triggers and for the console.</summary>
public static class ScriptedValues
{
    /// <summary>
    /// What a trigger may ask about and no effect may set, because it is worked out rather than
    /// kept: the worst-hit region's war damage is a question about every region at once.
    /// </summary>
    private static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
        { "worst_war_damage", "available_air_formations", "available_fleet_hulls" };

    public static bool IsReadOnly(string key) => key is not null &&
        (ReadOnly.Contains(key.Trim().Replace('-', '_')) || key.Equals("year", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("feature.", StringComparison.OrdinalIgnoreCase) && MechanicFeatures.ValidId(key[8..]) ||
         key.StartsWith("technology.", StringComparison.OrdinalIgnoreCase) && MechanicFeatures.ValidId(key[11..]));

    public static bool TryRead(Campaign campaign, Nation nation, string key, out float value)
    {
        value = 0f;
        if (nation is null) return false;
        if (key.StartsWith("feature.", StringComparison.OrdinalIgnoreCase))
        { value = campaign.FeatureEnabled(key[8..]) ? 1f : 0f; return true; }
        if (key.StartsWith("technology.", StringComparison.OrdinalIgnoreCase))
        { value = nation.HasTechnology(key[11..]) ? 1f : 0f; return true; }

        switch (key.Trim().Replace('-', '_').ToLowerInvariant())
        {
            case "year": value = campaign.Date.Year; return true;
            case "treasury": value = nation.Treasury; return true;
            case "public_debt": value = nation.PublicDebt; return true;
            case "credit_confidence": value = nation.CreditConfidence; return true;
            case "debt_arrears": value = nation.DebtArrears; return true;
            case "payment_arrears": value = nation.PaymentArrears; return true;
            case "gdp": value = nation.Gdp; return true;
            case "productivity": value = nation.Productivity; return true;
            case "stability": value = nation.Stability; return true;
            case "war_support": value = nation.WarSupport; return true;
            case "standard_of_living": value = nation.StandardOfLiving; return true;
            case "literacy": value = nation.Literacy; return true;
            case "brain_drain": value = nation.BrainDrainRate; return true;
            case "army_experience": value = nation.ArmyExperience; return true;
            case "air_experience": value = nation.AirExperience; return true;
            case "naval_experience": value = nation.NavalExperience; return true;
            case "political_points": value = nation.PoliticalPoints; return true;
            case "available_air_formations": value = AvailableAirFormations(campaign, nation); return true;
            case "available_fleet_hulls": value = AvailableFleetHulls(campaign, nation); return true;
            case "state.brain_drain": value = Average(campaign, nation, state => state.BrainDrainPressure); return true;
            case "inflation": value = nation.Inflation; return true;
            case "key_rate": value = CentralBank.KeyRate(nation); return true;
            case "government_sets_key_rate": value = nation.GovernmentSetsKeyRate ? 1f : 0f; return true;
            case "construction_capacity": value = nation.ConstructionCapacity; return true;
            case "manpower": value = nation.ManpowerReserve; return true;
            case "tax_rate": value = nation.TaxRate; return true;
            case "military_spending": value = nation.MilitarySpending; return true;
            case "welfare_spending": value = nation.WelfareSpending; return true;
            case "education_spending": value = nation.EducationSpending; return true;
            case "market_access": value = nation.MarketAccess; return true;
            case "population": value = nation.Population; return true;
            case "state.standard_of_living": value = Average(campaign, nation, state => state.StandardOfLiving); return true;
            case "state.radicalism": value = Average(campaign, nation, state => state.Radicalism); return true;
            case "state.literacy": value = Average(campaign, nation, state => state.Literacy); return true;
            case "state.war_damage": value = Average(campaign, nation, state => state.WarDamage); return true;
            case "worst_war_damage": value = WarDamage.Worst(campaign, nation); return true;
            case "state.urbanization": value = Average(campaign, nation, state => state.Urbanization); return true;
            case "state.population":
                value = campaign.StatesOf(nation)
                    .Sum(state => (float)state.Population);
                return true;
            default: return false;
        }
    }

    private static int AvailableAirFormations(Campaign campaign, Nation nation)
    {
        var map = campaign?.ProvinceMap;
        if (map is null) return 0;
        var controllers = campaign.ProvinceController ?? map.ProvinceToNation;
        var count = 0;
        foreach (var formation in campaign.SupportFormations)
        {
            if (formation.NationId != nation.Id || !(formation.Strength > .05f) ||
                !SupportTypeCatalog.TryGet(formation.TypeId, out var type) || type.Kind != SupportKind.Air ||
                !SupportCore.Enabled(campaign, type)) continue;
            var province = formation.BaseProvinceId;
            if (province > 0 && province < controllers.Length && province < map.ProvinceToState.Length &&
                controllers[province] == nation.MapIndex && map.ProvinceToState[province] == formation.StateMapIndex)
                count++;
        }
        return count;
    }

    private static float AvailableFleetHulls(Campaign campaign, Nation nation)
    {
        if (campaign is null || !campaign.FeatureEnabled("navy")) return 0;
        var count = 0f;
        foreach (var fleet in campaign.Fleets)
            if (fleet.NationId == nation.Id && fleet.Hulls > 0 && fleet.Strength > .05f &&
                fleet.Organisation > .05f && fleet.Supply > .05f && fleet.ReadyDay <= campaign.ElapsedDays &&
                !fleet.InAction && !fleet.IsLoaded)
                count += fleet.Hulls;
        return count;
    }

    private static float Average(Campaign campaign, Nation nation, Func<ScenarioState, float> read)
    {
        var owned = campaign.StatesOf(nation);
        return owned.Length == 0 ? 0f : owned.Average(read);
    }
}
