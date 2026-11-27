using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// The other half of the scripted layer: whether a piece of content may fire at all.
/// Same vocabulary as the effects, plus read-only world/diplomacy/megaproject/chokepoint/era
/// conditions, written as <c>"trigger": { "min_stability": 0.4, "has_feature": "aviation", "at_war": true }</c>.
/// </summary>
public sealed class ScriptedTrigger
{
    private enum BoundScale : byte { Raw, Gdp, Population, Points }

    private static BoundScale ClassifyScale(string key) =>
        key.Trim().Replace('-', '_').ToLowerInvariant() switch
        {
            "treasury" or "public_debt" or "debt_arrears" or "payment_arrears" or "gdp" => BoundScale.Gdp,
            "manpower" or "population" or "state.population" => BoundScale.Population,
            "army_experience" or "air_experience" or "naval_experience" or "political_points" => BoundScale.Points,
            _ => BoundScale.Raw
        };

    private readonly List<(string Key, float Value, bool IsMinimum, BoundScale Scale)> _bounds = new();
    private readonly List<string> _tags = new();
    private readonly List<OfficeholderCondition> _people = new();
    private readonly List<string> _requiredFeatures = new();
    private readonly List<string> _forbiddenFeatures = new();
    private readonly List<string> _requiredGrandProjects = new();
    private readonly List<string> _completedGrandProjects = new();
    private readonly List<string> _squeezedChokepoints = new();
    private readonly List<NuclearDoctrine> _nuclearDoctrines = new();
    private bool _invalidPeople;
    private bool? _atWar;
    private bool? _isMovement;

    internal bool HasTagFilter => _tags.Count != 0;

    public bool IsEmpty =>
        _bounds.Count == 0 && _atWar is null && _isMovement is null && _tags.Count == 0 &&
        _people.Count == 0 && _requiredFeatures.Count == 0 && _forbiddenFeatures.Count == 0 &&
        _requiredGrandProjects.Count == 0 && _completedGrandProjects.Count == 0 &&
        _squeezedChokepoints.Count == 0 && _nuclearDoctrines.Count == 0 && !_invalidPeople;

    public static ScriptedTrigger Parse(JsonElement element, string context, List<string> errors, int depth = 0)
    {
        var trigger = new ScriptedTrigger();
        if (element.ValueKind != JsonValueKind.Object) return trigger;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            var name = property.Name.Trim().Replace('-', '_');

            if (name == "officeholders")
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                { errors?.Add(context + ": officeholders must be an object of office conditions"); trigger._invalidPeople = true; }
                else foreach (var role in property.Value.EnumerateObject())
                    if (OfficeholderCondition.Parse(role.Name, role.Value, context, errors) is { } condition) trigger._people.Add(condition);
                    else trigger._invalidPeople = true;
                continue;
            }

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

            if (name.Equals("is_movement", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    trigger._isMovement = property.Value.GetBoolean();
                else errors?.Add($"{context}: 'is_movement' must be true or false.");
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

            if (name.Equals("has_feature", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("missing_feature", StringComparison.OrdinalIgnoreCase))
            {
                var targetList = name.Equals("has_feature", StringComparison.OrdinalIgnoreCase)
                    ? trigger._requiredFeatures
                    : trigger._forbiddenFeatures;
                var items = ScriptReferences.Names(property.Value).ToList();
                if (items.Count == 0)
                {
                    errors?.Add($"{context}: '{name}' must be a feature id or array of feature ids.");
                    continue;
                }
                foreach (var raw in items)
                {
                    if (!EraFeatureRegistry.TryExpand(raw, out var expanded))
                    {
                        var sug = ScriptedEffects.SuggestClosestKey(raw, MechanicFeatures.BuiltIns.Concat(EraFeatureRegistry.AliasTable.Keys));
                        var hint = string.IsNullOrEmpty(sug) ? "" : $" Did you mean '{sug}'?";
                        errors?.Add($"{context}: unknown feature '{raw}' in '{name}'.{hint}");
                        continue;
                    }
                    targetList.AddRange(expanded);
                }
                continue;
            }

            if (name.Equals("has_grand_project", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("completed_grand_project", StringComparison.OrdinalIgnoreCase))
            {
                var targetList = name.Equals("has_grand_project", StringComparison.OrdinalIgnoreCase)
                    ? trigger._requiredGrandProjects
                    : trigger._completedGrandProjects;
                var items = ScriptReferences.Names(property.Value).ToList();
                if (items.Count == 0)
                    errors?.Add($"{context}: '{name}' must be a grand project id or array of ids.");
                else
                    targetList.AddRange(items.Where(s => !string.IsNullOrWhiteSpace(s)));
                continue;
            }

            if (name.Equals("squeezed_chokepoint", StringComparison.OrdinalIgnoreCase))
            {
                var items = ScriptReferences.Names(property.Value).ToList();
                if (items.Count == 0)
                {
                    errors?.Add($"{context}: 'squeezed_chokepoint' must be a commodity id or array of commodity ids.");
                    continue;
                }
                foreach (var raw in items)
                {
                    if (!CommodityCatalog.TryParse(raw, out _))
                    {
                        errors?.Add($"{context}: unknown commodity '{raw}' in 'squeezed_chokepoint'.");
                        continue;
                    }
                    trigger._squeezedChokepoints.Add(raw.Trim());
                }
                continue;
            }

            if (name.Equals("nuclear_doctrine", StringComparison.OrdinalIgnoreCase))
            {
                var items = ScriptReferences.Names(property.Value).ToList();
                if (items.Count == 0)
                {
                    errors?.Add($"{context}: 'nuclear_doctrine' must be a doctrine name or array (retaliatory_triad, no_first_use, escalation_to_deescalate).");
                    continue;
                }
                foreach (var raw in items)
                {
                    if (!TryParseNuclearDoctrine(raw, out var doctrine))
                    {
                        var sug = ScriptedEffects.SuggestClosestKey(raw, new[] { "retaliatory_triad", "no_first_use", "escalation_to_deescalate" });
                        var hint = string.IsNullOrEmpty(sug) ? "" : $" Did you mean '{sug}'?";
                        errors?.Add($"{context}: unknown nuclear_doctrine '{raw}'.{hint}");
                        continue;
                    }
                    trigger._nuclearDoctrines.Add(doctrine);
                }
                continue;
            }

            var isMinimum = name.StartsWith("min_", StringComparison.OrdinalIgnoreCase);
            var isMaximum = name.StartsWith("max_", StringComparison.OrdinalIgnoreCase);
            if (!isMinimum && !isMaximum)
            {
                var directives = new[]
                {
                    "at_war", "is_movement", "tags", "officeholders", "use",
                    "has_feature", "missing_feature", "has_grand_project",
                    "completed_grand_project", "squeezed_chokepoint", "nuclear_doctrine"
                };
                var sug = ScriptedEffects.SuggestClosestKey(name, directives);
                var hint = string.IsNullOrEmpty(sug) ? "" : $" Did you mean '{sug}'?";
                errors?.Add($"{context}: trigger '{property.Name}' must be min_<key>, max_<key>, at_war, is_movement, tags, has_feature, missing_feature, has_grand_project, completed_grand_project, squeezed_chokepoint, or officeholders.{hint}");
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetSingle(out var value) || !float.IsFinite(value))
            {
                errors?.Add($"{context}: trigger '{property.Name}' must be a number.");
                continue;
            }

            var key = name[4..];
            if (!ScriptedEffects.IsKnown(key) && !ScriptedValues.IsReadOnly(key))
            {
                var sug = ScriptedEffects.SuggestClosestKey(key, ScriptedValues.AllReadableKeys());
                var hint = string.IsNullOrEmpty(sug) ? "" : $" Did you mean '{sug}'?";
                errors?.Add($"{context}: unknown trigger key '{key}'.{hint}");
                continue;
            }
            var normalizedKey = key.ToLowerInvariant();
            trigger._bounds.Add((normalizedKey, value, isMinimum, ClassifyScale(normalizedKey)));
        }
        return trigger;
    }

    /// <summary>Accumulate bounds and discrete conditions; keep an already specified war/movement flag.</summary>
    internal void Absorb(ScriptedTrigger other)
    {
        if (other is null) return;
        _bounds.AddRange(other._bounds);
        _tags.AddRange(other._tags);
        _people.AddRange(other._people);
        _requiredFeatures.AddRange(other._requiredFeatures);
        _forbiddenFeatures.AddRange(other._forbiddenFeatures);
        _requiredGrandProjects.AddRange(other._requiredGrandProjects);
        _completedGrandProjects.AddRange(other._completedGrandProjects);
        _squeezedChokepoints.AddRange(other._squeezedChokepoints);
        _nuclearDoctrines.AddRange(other._nuclearDoctrines);
        _invalidPeople |= other._invalidPeople;
        _atWar ??= other._atWar;
        _isMovement ??= other._isMovement;
    }

    public bool Matches(Campaign campaign, Nation nation)
    {
        if (nation is null) return false;
        if (!MatchesDiscrete(campaign, nation)) return false;

        for (var i = 0; i < _bounds.Count; i++)
        {
            var (key, value, isMinimum, _) = _bounds[i];
            if (!ScriptedValues.TryRead(campaign, nation, key, out var current)) continue;
            if (isMinimum && current < value) return false;
            if (!isMinimum && current > value) return false;
        }
        return true;
    }

    /// <summary>
    /// Dimensionless distance from <paramref name="nation"/>'s current state to satisfying this
    /// trigger: 0 when <see cref="Matches"/> is already true, positive when numeric bounds are
    /// not yet met, and <see cref="float.PositiveInfinity"/> when a discrete condition fails.
    /// </summary>
    public float Shortfall(Campaign campaign, Nation nation)
    {
        if (nation is null || !MatchesDiscrete(campaign, nation)) return float.PositiveInfinity;

        var total = 0f;
        for (var i = 0; i < _bounds.Count; i++)
        {
            var (key, target, isMinimum, scale) = _bounds[i];
            if (!ScriptedValues.TryRead(campaign, nation, key, out var current)) return float.PositiveInfinity;
            var raw = isMinimum ? target - current : current - target;
            if (raw <= 0f) continue;
            var normalized = scale switch
            {
                BoundScale.Gdp => raw / MathF.Max(0.1f, nation.Gdp),
                BoundScale.Population => raw / MathF.Max(1f, nation.Population * 0.02f),
                BoundScale.Points => raw / 100f,
                _ => raw
            };
            total += normalized;
        }
        return total;
    }

    private bool MatchesDiscrete(Campaign campaign, Nation nation)
    {
        if (_invalidPeople) return false;
        for (var i = 0; i < _people.Count; i++)
            if (!_people[i].Matches(campaign, nation)) return false;
        if (_tags.Count > 0)
        {
            var tagMatched = false;
            for (var i = 0; i < _tags.Count; i++)
                if (string.Equals(_tags[i], nation.Tag, StringComparison.OrdinalIgnoreCase)) { tagMatched = true; break; }
            if (!tagMatched) return false;
        }
        if (_isMovement.HasValue && (nation.Movement is not null) != _isMovement.Value)
            return false;
        if (_nuclearDoctrines.Count > 0 && !_nuclearDoctrines.Contains(nation.NuclearDoctrine))
            return false;
        if (_atWar.HasValue && campaign is not null)
        {
            var atWar = false;
            var liveWars = Warfare.LiveWars(campaign);
            for (var wi = 0; wi < liveWars.Count; wi++)
            {
                var war = liveWars[wi];
                if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
            }
            if (atWar != _atWar.Value) return false;
        }
        if (campaign is not null)
        {
            for (var i = 0; i < _requiredFeatures.Count; i++)
                if (!campaign.FeatureEnabled(_requiredFeatures[i])) return false;
            for (var i = 0; i < _forbiddenFeatures.Count; i++)
                if (campaign.FeatureEnabled(_forbiddenFeatures[i])) return false;
            for (var i = 0; i < _requiredGrandProjects.Count; i++)
                if (!HasGrandProject(campaign, nation.Id, _requiredGrandProjects[i], completedOnly: false)) return false;
            for (var i = 0; i < _completedGrandProjects.Count; i++)
                if (!HasGrandProject(campaign, nation.Id, _completedGrandProjects[i], completedOnly: true)) return false;
            if (_squeezedChokepoints.Count > 0)
            {
                for (var i = 0; i < _squeezedChokepoints.Count; i++)
                {
                    if (!CommodityCatalog.TryParse(_squeezedChokepoints[i], out var good)) return false;
                    if (!StrategicChokepoints.IsCommoditySqueezed(campaign, nation, good)) return false;
                }
            }
        }
        return true;
    }

    private static bool TryParseNuclearDoctrine(string raw, out NuclearDoctrine doctrine)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", ""))
        {
            case "retaliatorytriad": doctrine = NuclearDoctrine.RetaliatoryTriad; return true;
            case "nofirstuse": doctrine = NuclearDoctrine.NoFirstUse; return true;
            case "escalationtodeescalate": doctrine = NuclearDoctrine.EscalationToDeescalate; return true;
            default: doctrine = NuclearDoctrine.RetaliatoryTriad; return false;
        }
    }

    private static bool HasGrandProject(Campaign campaign, int nationId, string defId, bool completedOnly)
    {
        for (var i = 0; i < campaign.GrandProjects.Count; i++)
        {
            var p = campaign.GrandProjects[i];
            if (p.HostNationId != nationId) continue;
            if (!string.Equals(p.DefinitionId, defId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!completedOnly || p.Status == GrandProjectStatus.Completed) return true;
        }
        return false;
    }
}

/// <summary>A `use` or list entry is one name or a list of them.</summary>
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

/// <summary>Reads the same keys the effects write, plus read-only strategic telemetry, for triggers and the console.</summary>
public static class ScriptedValues
{
    private static readonly HashSet<string> ReadOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "worst_war_damage", "available_air_formations", "available_fleet_hulls",
        "nuclear_tier", "deterrence_tier", "nuclear_posture", "nuclear_doctrine", "defcon",
        "active_grand_projects", "completed_grand_projects",
        "squeezed_chokepoints", "chokepoint_exposures", "healthy_supply_anchors",
        "ally_count", "active_wars", "organization_count", "subject_count", "is_subject",
        "is_movement", "active_movements",
        "weekly_balance", "tax_rate", "military_spending", "welfare_spending", "education_spending",
        "mainland_reclamation_pending", "mainland_reclamation_war", "facing_mainland_reclamation"
    };

    public static IEnumerable<string> AllReadableKeys() =>
        ReadOnly.Concat(ScriptedEffects.Vocabulary.Select(v => v.Key)).Concat(new[] { "year" });

    public static bool IsReadOnly(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var norm = key.Trim().Replace('-', '_');
        if (ReadOnly.Contains(norm) || norm.Equals("year", StringComparison.OrdinalIgnoreCase)) return true;
        if (norm.StartsWith("feature.", StringComparison.OrdinalIgnoreCase) && MechanicFeatures.ValidId(norm[8..])) return true;
        if (norm.StartsWith("technology.", StringComparison.OrdinalIgnoreCase) && MechanicFeatures.ValidId(norm[11..])) return true;
        if (norm.StartsWith("grand_project.", StringComparison.OrdinalIgnoreCase) && MechanicFeatures.ValidId(norm[14..])) return true;
        if (norm.StartsWith("stockpile.", StringComparison.OrdinalIgnoreCase) && ScriptedEffects.TryResolveStockpileGood(norm[10..], out _)) return true;
        if (norm.StartsWith("stockpile_coverage.", StringComparison.OrdinalIgnoreCase) && ScriptedEffects.TryResolveStockpileGood(norm[19..], out _)) return true;
        return false;
    }

    public static bool TryRead(Campaign campaign, Nation nation, string key, out float value)
    {
        value = 0f;
        if (nation is null || string.IsNullOrWhiteSpace(key)) return false;
        var trimmed = (char.IsWhiteSpace(key[0]) || char.IsWhiteSpace(key[^1])) ? key.Trim() : key;

        if (trimmed.StartsWith("feature.", StringComparison.OrdinalIgnoreCase))
        { value = campaign is not null && campaign.FeatureEnabled(trimmed[8..]) ? 1f : 0f; return true; }
        if (trimmed.StartsWith("technology.", StringComparison.OrdinalIgnoreCase))
        { value = nation.HasTechnology(trimmed[11..]) ? 1f : 0f; return true; }
        if (trimmed.StartsWith("grand_project.", StringComparison.OrdinalIgnoreCase))
        { value = GrandProjectStateValue(campaign, nation.Id, trimmed[14..]); return true; }
        if (trimmed.StartsWith("stockpile.", StringComparison.OrdinalIgnoreCase) &&
            ScriptedEffects.TryResolveStockpileGood(trimmed[10..], out var stockGood))
        { value = nation.Stockpiles.GetValueOrDefault(stockGood, 0f); return true; }
        if (trimmed.StartsWith("stockpile_coverage.", StringComparison.OrdinalIgnoreCase) &&
            ScriptedEffects.TryResolveStockpileGood(trimmed[19..], out var covGood))
        { value = nation.StockpileCoverage.GetValueOrDefault(covGood, 1f); return true; }

        var normalized = NormalizeKeyIfNeeded(trimmed);
        switch (normalized)
        {
            case "year": value = campaign?.Date.Year ?? 0; return true;
            case "treasury": value = nation.Treasury; return true;
            case "public_debt": value = nation.PublicDebt; return true;
            case "credit_confidence": value = nation.CreditConfidence; return true;
            case "debt_arrears": value = nation.DebtArrears; return true;
            case "payment_arrears": value = nation.PaymentArrears; return true;
            case "weekly_balance": value = nation.WeeklyBalance; return true;
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
            case "notoriety": value = nation.Notoriety; return true;
            case "prestige": value = nation.Prestige; return true;
            case "union_cohesion": value = nation.UnionCohesion; return true;
            case "imperial_overstretch": value = nation.ImperialOverstretch; return true;
            case "available_air_formations": value = AvailableAirFormations(campaign, nation); return true;
            case "available_fleet_hulls": value = AvailableFleetHulls(campaign, nation); return true;
            case "state.brain_drain": value = AverageState(campaign, nation, 0); return true;
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
            case "state.standard_of_living": value = AverageState(campaign, nation, 1); return true;
            case "state.radicalism": value = AverageState(campaign, nation, 2); return true;
            case "state.literacy": value = AverageState(campaign, nation, 3); return true;
            case "state.war_damage": value = AverageState(campaign, nation, 4); return true;
            case "worst_war_damage": value = WarDamage.Worst(campaign, nation); return true;
            case "nuclear_tier":
            case "deterrence_tier": value = Deterrence.TierOf(campaign, nation); return true;
            case "nuclear_posture": value = nation.NuclearPosture; return true;
            case "nuclear_doctrine": value = (int)nation.NuclearDoctrine; return true;
            case "defcon": value = DefconSystem.CurrentLevel(campaign); return true;
            case "active_grand_projects": value = GrandProjects.CountActive(campaign, nation.Id); return true;
            case "completed_grand_projects": value = GrandProjects.CountCompleted(campaign, nation.Id); return true;
            case "squeezed_chokepoints": value = StrategicChokepoints.CountExposures(campaign, nation, ChokepointStatus.Squeezed); return true;
            case "healthy_supply_anchors": value = StrategicChokepoints.CountExposures(campaign, nation, ChokepointStatus.HealthyAnchor); return true;
            case "chokepoint_exposures": value = StrategicChokepoints.CountExposures(campaign, nation); return true;
            case "ally_count": value = campaign is null ? 0f : AllianceSystem.AllyCount(campaign, nation); return true;
            case "active_wars": value = CountOpenWars(campaign, nation.Id); return true;
            case "organization_count": value = CountOrganizations(campaign, nation.Id); return true;
            case "subject_count": value = campaign is null ? 0f : SubjectSystem.SubjectCount(campaign, nation); return true;
            case "is_subject": value = nation.IsSubject ? 1f : 0f; return true;
            case "is_movement": value = nation.Movement is not null ? 1f : 0f; return true;
            case "active_movements": value = CountActiveMovements(campaign); return true;
            case "movement.cadres": value = nation.Movement?.Cadres ?? 0f; return true;
            case "movement.legitimacy": value = nation.Movement?.Legitimacy ?? 0f; return true;
            case "movement.heat": value = nation.Movement?.Heat ?? 0f; return true;
            case "movement.support": value = MaxMovementSupport(nation.Movement); return true;
            case "state.urbanization": value = AverageState(campaign, nation, 5); return true;
            case "state.population": value = SumStatePopulation(campaign, nation); return true;
            case "mainland_reclamation_pending": value = MainlandReclamation.HasPendingReclamation(campaign, nation) ? 1f : 0f; return true;
            case "mainland_reclamation_war": value = MainlandReclamation.InReclamationWar(campaign, nation) ? 1f : 0f; return true;
            case "facing_mainland_reclamation": value = MainlandReclamation.FacingReclamationWar(campaign, nation) ? 1f : 0f; return true;
            default: return false;
        }
    }

    private static float GrandProjectStateValue(Campaign campaign, int nationId, string defId)
    {
        if (campaign is null || string.IsNullOrWhiteSpace(defId)) return 0f;
        var active = false;
        for (var i = 0; i < campaign.GrandProjects.Count; i++)
        {
            var p = campaign.GrandProjects[i];
            if (p.HostNationId != nationId || !string.Equals(p.DefinitionId, defId, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Status == GrandProjectStatus.Completed) return 1f;
            active = true;
        }
        return active ? 0.5f : 0f;
    }

    private static string NormalizeKeyIfNeeded(string key)
    {
        var hasDash = false;
        var hasUpper = false;
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c == '-') hasDash = true;
            else if (c is >= 'A' and <= 'Z') hasUpper = true;
        }
        if (!hasDash && !hasUpper) return key;
        if (hasDash) key = key.Replace('-', '_');
        return hasUpper ? key.ToLowerInvariant() : key;
    }

    private static int CountOpenWars(Campaign campaign, int nationId)
    {
        if (campaign is null) return 0;
        var count = 0;
        var wars = Warfare.LiveWars(campaign);
        for (var i = 0; i < wars.Count; i++)
        {
            var war = wars[i];
            if (!war.Ended && war.Involves(nationId)) count++;
        }
        return count;
    }

    private static float MaxMovementSupport(MovementState movement)
    {
        if (movement is null || movement.Support.Count == 0) return 0f;
        var best = float.MinValue;
        foreach (var v in movement.Support.Values)
            if (v > best) best = v;
        return best;
    }

    private static float SumStatePopulation(Campaign campaign, Nation nation)
    {
        if (campaign is null) return 0f;
        var owned = campaign.StatesOf(nation);
        var sum = 0f;
        for (var i = 0; i < owned.Length; i++) sum += owned[i].Population;
        return sum;
    }

    private static float AverageState(Campaign campaign, Nation nation, int metric)
    {
        if (campaign is null) return 0f;
        var owned = campaign.StatesOf(nation);
        if (owned.Length == 0) return 0f;
        double sum = 0;
        for (var i = 0; i < owned.Length; i++)
        {
            var state = owned[i];
            sum += metric switch
            {
                0 => state.BrainDrainPressure,
                1 => state.StandardOfLiving,
                2 => state.Radicalism,
                3 => state.Literacy,
                4 => state.WarDamage,
                _ => state.Urbanization
            };
        }
        return (float)(sum / owned.Length);
    }

    private static int CountOrganizations(Campaign campaign, int nationId)
    {
        if (campaign is null) return 0;
        var count = 0;
        for (var i = 0; i < campaign.Organizations.Count; i++)
        {
            var org = campaign.Organizations[i];
            if (!org.Dissolved && org.Members.Contains(nationId)) count++;
        }
        return count;
    }

    private static int CountActiveMovements(Campaign campaign)
    {
        if (campaign is null) return 0;
        var count = 0;
        var living = campaign.ActiveNations;
        for (var i = 0; i < living.Count; i++)
            if (living[i].Movement is not null) count++;
        return count;
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
}
