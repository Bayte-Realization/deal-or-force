using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public static partial class ScriptedEffects
{
    /// <summary>
    /// Parse an "effects" object. An unknown key costs that one line, not the whole file:
    /// one typo in a mod never silently drops — it reports a diagnostic with a suggested fix.
    /// </summary>
    public static Effect Parse(JsonElement element, string context, List<string> errors, int depth = 0)
    {
        var effect = new Effect();
        if (element.ValueKind != JsonValueKind.Object) return effect;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.StartsWith("//", StringComparison.Ordinal)) continue;

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
                    foreach (var person in borrowed.People) effect.Person(person);
                    foreach (var (share, days, _) in borrowed.IncomePenalties) effect.IncomePenalty(share, days, context);
                }
                continue;
            }

            if (property.Name is "head_of_state" or "head_of_government")
            {
                if (OfficeholderEffect.Parse(property.Name, property.Value, context, errors) is { } person) effect.Person(person);
                continue;
            }
            if (property.Name == "ministers")
            {
                if (property.Value.ValueKind != JsonValueKind.Object) errors?.Add(context + ": ministers must be an object of portfolios");
                else foreach (var minister in property.Value.EnumerateObject())
                    if (OfficeholderEffect.Parse(minister.Name, minister.Value, context, errors) is { } person) effect.Person(person);
                continue;
            }

            if (property.Name.Equals("unlock_feature", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("restrict_feature", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("disable_feature", StringComparison.OrdinalIgnoreCase))
            {
                var enable = property.Name.Equals("unlock_feature", StringComparison.OrdinalIgnoreCase);
                var names = ScriptReferences.Names(property.Value).ToList();
                if (names.Count == 0)
                {
                    errors?.Add($"{context}: '{property.Name}' expects a feature id or array of feature ids (e.g. \"aviation\", \"nukes\").");
                    continue;
                }
                foreach (var rawFeature in names)
                {
                    if (!EraFeatureRegistry.TryExpand(rawFeature, out var canonicals))
                    {
                        var suggestion = SuggestClosestKey(rawFeature, MechanicFeatures.BuiltIns.Concat(EraFeatureRegistry.AliasTable.Keys));
                        var hint = string.IsNullOrEmpty(suggestion) ? "" : $" Did you mean '{suggestion}'?";
                        errors?.Add($"{context}: unknown feature '{rawFeature}' in '{property.Name}'.{hint}");
                        continue;
                    }
                    foreach (var cid in canonicals)
                        effect.Add($"feature.{cid}_set", enable ? 1f : 0f);
                }
                continue;
            }

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
            if (property.Name.Equals("income_penalty", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Object ||
                    !property.Value.TryGetProperty("share", out var shareElement) || !shareElement.TryGetSingle(out var penaltyShare) ||
                    !property.Value.TryGetProperty("days", out var daysElement) || !daysElement.TryGetInt32(out var penaltyDays))
                {
                    errors?.Add($"{context}: income_penalty takes {{ \"share\": 0.08, \"days\": 120 }}.");
                    continue;
                }
                if (!float.IsFinite(penaltyShare) || penaltyShare <= 0f || penaltyShare > 1f || penaltyDays <= 0 || penaltyDays > 3650)
                {
                    errors?.Add($"{context}: income_penalty needs a share above 0 and at most 1, and days from 1 to 3650.");
                    continue;
                }
                effect.IncomePenalty(penaltyShare, penaltyDays, context);
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
                var suggestion = SuggestClosestKey(property.Name,
                    Fields.Keys.Concat(new[] { "income_share", "gdp_share", "grand_project_progress", "unlock_feature", "restrict_feature", "add_modifier", "remove_modifier", "income_penalty" }));
                var hint = string.IsNullOrEmpty(suggestion) ? "" : $" Did you mean '{suggestion}'?";
                errors?.Add($"{context}: unknown effect key '{property.Name}'.{hint} Known keys: " +
                            string.Join(", ", Vocabulary.Select(item => item.Key)));
                continue;
            }
            effect.Add(property.Name, value);
        }
        return effect;
    }

    /// <summary>
    /// What a player reads for an effect: one line per consequence, in the same shape
    /// everywhere - "Stability: +3%", "Treasury: -2%", "Manpower: +50,000".
    /// </summary>
    public static IReadOnlyList<string> Lines(Effect effect)
    {
        var lines = new List<string>();
        if (effect is null) return lines;
        lines.AddRange(SpiritLines(effect.Grants));
        lines.AddRange(IncomePenaltyLines(effect));
        lines.AddRange(effect.People.Select(person => person.Describe()));
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
            if (IsIncomeShareKey(entry.Key))
            {
                lines.Add(Loc.Format("Treasury: {0}% annual income",
                    (entry.Value * 100f).ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)));
                continue;
            }
            if (IsGdpShareKey(entry.Key))
            {
                lines.Add(Loc.Format("Treasury: {0}% GDP",
                    (entry.Value * 100f).ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)));
                continue;
            }
            if (IsGrandProjectProgressKey(entry.Key))
            {
                lines.Add(Loc.Format("Grand project progress: {0} days",
                    entry.Value.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)));
                continue;
            }
            if (!Resolve(entry.Key, out var field, out var operation)) continue;
            var narrative = field.Name.StartsWith("reputation.", StringComparison.Ordinal);
            var stockpile = field.Name.StartsWith("stockpile.", StringComparison.Ordinal);
            var label = narrative
                ? Loc.Format("Public narrative: {0}", field.Name[11..])
                : stockpile
                    ? Loc.Format("Stockpile ({0})", Loc.Get(field.Name[10..]))
                    : Loc.Get(Labels.GetValueOrDefault(field.Name, field.Name));
            if (label.Length > 4 && label == label.ToUpper(CultureInfo.CurrentCulture))
                label = label[..1] + label[1..].ToLower(CultureInfo.CurrentCulture);
            var fraction = narrative || Fractions.Contains(field.Name) || field.Name.StartsWith("social.", StringComparison.Ordinal);
            var culture = CultureInfo.CurrentCulture;
            var value = operation switch
            {
                Operation.Percent => (entry.Value * 100f).ToString("+0.#;-0.#", culture) + "%",
                Operation.Mult => "×" + entry.Value.ToString("0.##", culture),
                Operation.Set => "= " + (fraction
                    ? (entry.Value * 100f).ToString("0.#", culture) + "%"
                    : entry.Value.ToString("N0", culture)),
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

    /// <summary>"Income: −8% for 120 days" for each timed income penalty an effect imposes.</summary>
    public static IEnumerable<string> IncomePenaltyLines(Effect effect)
    {
        if (effect is null) yield break;
        foreach (var (share, days, _) in effect.IncomePenalties)
            yield return Loc.Format("Income: −{0}% for {1} days",
                (share * 100f).ToString("0.#", CultureInfo.CurrentCulture), days);
    }

    /// <summary>
    /// "Gains «War economy» for 365 days", and under it, indented, what the spirit does while it lasts.
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
        foreach (var (share, days, _) in effect.IncomePenalties)
        {
            var weekly = Simulation.IncomePenalties.WeeklyBase(campaign, nation) * share;
            lines.Add(Loc.Format("Income: −{0}% for {1} days (about {2} a week)",
                (share * 100f).ToString("0.#", CultureInfo.CurrentCulture), days, Fmt.Money(weekly)));
        }
        lines.AddRange(effect.People.Select(person => person.Describe()));
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
            if (IsIncomeShareKey(entry.Key))
            {
                lines.Add(Loc.Format("Treasury: {0}% annual income",
                    (entry.Value * 100f).ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)));
                continue;
            }
            if (IsGdpShareKey(entry.Key))
            {
                lines.Add(Loc.Format("Treasury: {0}% GDP",
                    (entry.Value * 100f).ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)));
                continue;
            }
            if (IsGrandProjectProgressKey(entry.Key))
            {
                lines.Add(Loc.Format("Grand project progress: {0} days",
                    entry.Value.ToString("+0.#;-0.#;0", CultureInfo.CurrentCulture)));
                continue;
            }
            if (!Resolve(entry.Key, out var field, out var operation)) continue;
            var label = field.Name.StartsWith("reputation.", StringComparison.Ordinal)
                ? Loc.Format("Public narrative: {0}", field.Name[11..])
                : field.Name.StartsWith("stockpile.", StringComparison.Ordinal)
                    ? Loc.Format("Stockpile ({0})", Loc.Get(field.Name[10..]))
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
        "credit_confidence", "union_cohesion", "imperial_overstretch",
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
        ["notoriety"] = "Notoriety", ["prestige"] = "Prestige", ["union_cohesion"] = "Union cohesion",
        ["imperial_overstretch"] = "Imperial overstretch",
        ["nuclear_posture"] = "Nuclear posture",
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
