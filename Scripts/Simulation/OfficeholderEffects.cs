using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    // An appearance belongs to a person, NOT to a chair: a successor must never
    // inherit the previous president's photograph. Empty in old/unmodified saves.
    public Dictionary<string, string> PersonPortraits = new(StringComparer.Ordinal);
}

/// <summary>
/// Data-only changes to the existing officeholders, shared by events/decisions.
/// This is not a second character lifecycle or an unrestricted reflection setter.
/// A full persistent cast, relationships and off-map characters remain separate work.
/// </summary>
public sealed class OfficeholderEffect
{
    public string Role { get; set; } = "";
    public string Name { get; set; }
    public int? BirthYear { get; set; }
    public int? Age { get; set; }
    public string Trait { get; set; }
    public string Portrait { get; set; }
    public string Option { get; set; }
    public bool Dismiss { get; set; }
    public bool Death { get; set; }

    public static string PersonKey(string name, int birthYear) =>
        (name ?? "") + "|" + birthYear.ToString(CultureInfo.InvariantCulture);

    public static string PortraitFor(Nation n, string name, int birthYear, string fallback = "") =>
        n?.PersonPortraits.TryGetValue(PersonKey(name, birthYear), out var sprite) == true ? sprite : fallback;

    public static bool IsRole(string role) => role is "head_of_state" or "head_of_government" ||
        Rulers.AllPortfolios.Any(p => p.Id.Equals(role, StringComparison.OrdinalIgnoreCase));

    public static LeaderProfile MinisterProfile(Campaign c, Nation n, string role)
    {
        var name = Rulers.MinisterPersonName(n, role); var born = Rulers.MinisterBirthYear(c, n, role);
        var authored = Rulers.AuthoredMinister(n.Tag, role);
        var traitId = n.CabinetPeople.TryGetValue(role, out var person) ? person?.TraitId ?? "" : authored?.Trait ?? "";
        var trait = Rulers.TraitOf(traitId);
        return new LeaderProfile(name, Rulers.AllPortfolios.FirstOrDefault(p => p.Id == role)?.Name ?? role,
            Rulers.MinisterPersonAge(c, n, role), born, traitId, trait?.Name ?? "", Rulers.StableHash(name, born), false,
            PortraitFor(n, name, born, authored?.Name == name ? authored.Portrait : ""));
    }

    /// <summary>One malformed person is rejected atomically; valid neighbouring entries survive.</summary>
    public static OfficeholderEffect Parse(string role, JsonElement body, string origin, List<string> errors)
    {
        OfficeholderEffect Bad(string why) { errors?.Add(origin + ": " + role + ": " + why); return null; }
        if (!IsRole(role)) return Bad("unknown office/portfolio");
        if (body.ValueKind != JsonValueKind.Object) return Bad("expected an object");
        var effect = new OfficeholderEffect { Role = role };
        foreach (var property in body.EnumerateObject())
        {
            if (property.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (property.Name is "name" or "trait" or "portrait" or "option")
            {
                if (property.Value.ValueKind != JsonValueKind.String) return Bad(property.Name + " must be text");
                var text = property.Value.GetString()?.Trim() ?? "";
                if (text.Length > 200 || text.Any(char.IsControl)) return Bad(property.Name + " is too long or contains control characters");
                switch (property.Name)
                {
                    case "name": if (text.Length == 0) return Bad("name cannot be blank"); effect.Name = text; break;
                    case "trait":
                        if (text.Length > 0 && Rulers.TraitOf(text) is null) return Bad("unknown ruler trait '" + text + "'");
                        effect.Trait = text; break;
                    case "portrait":
                        if (text.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return Bad("portrait must be a sprite id, not a file path/URL");
                        effect.Portrait = text; break;
                    case "option":
                        if (role is "head_of_state" or "head_of_government" || !Rulers.AllPortfolios.Any(p =>
                            p.Id.Equals(role, StringComparison.OrdinalIgnoreCase) && p.Options.Any(o => o.Id == text)))
                            return Bad("unknown policy option for this portfolio");
                        effect.Option = text; break;
                }
                continue;
            }
            if (property.Name is "age" or "birth_year")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number)) return Bad(property.Name + " must be an integer");
                if (property.Name == "age")
                { if (number < 0 || number > 10000) return Bad("age must be between 0 and 10000"); effect.Age = number; }
                else
                { if (number < -999999 || number > 999999 || number == 0) return Bad("birth_year must be a non-zero historical year"); effect.BirthYear = number; }
                continue;
            }
            if (property.Name is "dismiss" or "death")
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Bad(property.Name + " must be true or false");
                if (property.Name == "dismiss") effect.Dismiss = property.Value.GetBoolean(); else effect.Death = property.Value.GetBoolean();
                continue;
            }
            return Bad("unknown field '" + property.Name + "'");
        }
        if (effect.BirthYear.HasValue && effect.Age.HasValue) return Bad("use age OR birth_year, not both");
        if (effect.Dismiss && effect.Death) return Bad("use dismiss OR death, not both");
        if ((effect.Dismiss || effect.Death) &&
            (effect.Name is not null || effect.Trait is not null || effect.Portrait is not null || effect.Option is not null || effect.Age.HasValue || effect.BirthYear.HasValue))
            return Bad("dismiss/death cannot be combined with edits");
        if (!(effect.Dismiss || effect.Death) && effect.Name is null && effect.Trait is null && effect.Portrait is null && effect.Option is null && !effect.Age.HasValue && !effect.BirthYear.HasValue)
            return Bad("no changes specified");
        return effect;
    }

    public void Apply(Campaign c, Nation n)
    {
        if (c is null || n is null || !IsRole(Role)) return;
        var state = Role == "head_of_state";
        var government = Role == "head_of_government";
        if (government && !(Dismiss || Death) && Name is null && !Rulers.HasDualExecutive(n))
        {
            DiagnosticLogs.Write("errors", "OFFICEHOLDER_EFFECT_REFUSED no head of government to edit: " + n.Tag);
            return;
        }
        if (Dismiss || Death)
        {
            // A vacant head of state means succession in today's model. Do not
            // silently kill the head of state when a non-existent PM is targeted.
            if (state) Rulers.ReplaceHeadOfState(c, n, Death ? "ruler_died" : "ruler_changed");
            else if (government)
            { if (Rulers.HasDualExecutive(n)) Rulers.ReplaceHeadOfGovernment(c, n, Death ? "ruler_died" : "ruler_changed"); }
            else
            {
                var name = Rulers.MinisterPersonName(n, Role);
                var removed = n.Cabinet.Remove(Role); n.CabinetPeople.Remove(Role);
                if (!removed) return;
                if (Death) c.Chronicle.Record(c, "minister_died", n.Id, place: name);
                NationalModifiers.Invalidate(n); c.MarkStrategicChange();
            }
            return;
        }

        string oldName, oldTrait; int oldBirth;
        if (state || government)
        {
            // Editing/appointing a PM explicitly creates a dual executive, rather
            // than overwriting the president through HeadOfGovernment's fallback.
            var person = government ? Rulers.HeadOfGovernment(c, n) : Rulers.HeadOfState(c, n);
            oldName = person.Name; oldTrait = person.TraitId; oldBirth = person.BirthYear;
        }
        else
        {
            if (!n.Cabinet.ContainsKey(Role) && Option is null) return;
            oldName = Rulers.MinisterPersonName(n, Role);
            oldBirth = Rulers.MinisterBirthYear(c, n, Role);
            oldTrait = n.CabinetPeople.TryGetValue(Role, out var minister) ? minister?.TraitId ?? "" : Rulers.AuthoredMinister(n.Tag, Role)?.Trait ?? "";
        }
        var nameNow = Name ?? oldName;
        var born = BirthYear ?? (Age.HasValue ? c.Date.Year - Age.Value : oldBirth);
        // The campaign uses astronomical years (0 = 1 BCE), as HistoricalDate
        // documents. The existing ruler fields still reserve birth year 0 as
        // unknown, so that one birth year is not supported by this officeholder slice.
        if (born == 0 || born < -999999 || born > 999999 || born > c.Date.Year) return;
        var traitNow = Trait ?? oldTrait;
        if (!string.IsNullOrEmpty(traitNow) && Rulers.TraitOf(traitNow) is null) return;
        var newKey = PersonKey(nameNow, born);
        if (Portrait is not null)
        {
            if (Portrait.Length == 0) n.PersonPortraits.Remove(newKey);
            else n.PersonPortraits[newKey] = Portrait;
        }

        if (state)
        {
            n.HeadOfStateName = nameNow; n.HeadOfStateBirthYear = born; n.HeadOfStateTrait = traitNow;
            if (nameNow != oldName) n.HeadOfStateSinceDay = c.ElapsedDays;
        }
        else if (government)
        {
            n.HeadOfGovernmentName = nameNow; n.HeadOfGovernmentBirthYear = born; n.HeadOfGovernmentTrait = traitNow;
            if (nameNow != oldName) n.HeadOfGovernmentSinceDay = c.ElapsedDays;
        }
        else
        {
            if (Option is not null) n.Cabinet[Role] = Option;
            n.CabinetPeople[Role] = new MinisterPerson { Name = nameNow, BirthYear = born, TraitId = traitNow,
                AppointedDay = nameNow == oldName && n.CabinetPeople.TryGetValue(Role, out var previous) ? previous.AppointedDay : c.ElapsedDays };
            n.LastCabinetChangeDay = c.ElapsedDays;
        }
        NationalModifiers.Invalidate(n); c.MarkStrategicChange();
    }

    public string Describe() => Loc.Format(Death ? "Death and succession: {0}" : Dismiss ? "Remove officeholder: {0}" : "Update officeholder: {0}",
        Loc.Get(Role == "head_of_state" ? "Head of state" : Role == "head_of_government" ? "Head of government" : Rulers.AllPortfolios.FirstOrDefault(p => p.Id == Role)?.Name ?? Role)) +
        (Name is null ? "" : " · " + Name) + (Portrait is null ? "" : " · " + Loc.Format("Portrait: {0}", Portrait.Length == 0 ? Loc.Get("Automatic") : Portrait));
}

/// <summary>Read-only officeholder predicates for the normal scripted event trigger.</summary>
public sealed class OfficeholderCondition
{
    private string _role, _name, _trait, _portrait;
    private int? _minAge, _maxAge;

    public static OfficeholderCondition Parse(string role, JsonElement body, string origin, List<string> errors)
    {
        OfficeholderCondition Bad(string why) { errors?.Add(origin + ": " + role + ": " + why); return null; }
        if (!OfficeholderEffect.IsRole(role) || body.ValueKind != JsonValueKind.Object) return Bad("expected a known office and a condition object");
        var condition = new OfficeholderCondition { _role = role };
        foreach (var p in body.EnumerateObject())
        {
            if (p.Name is "name" or "trait" or "portrait")
            {
                if (p.Value.ValueKind != JsonValueKind.String) return Bad(p.Name + " must be text");
                switch (p.Name) { case "name": condition._name = p.Value.GetString(); break;
                    case "trait": condition._trait = p.Value.GetString(); break;
                    case "portrait": condition._portrait = p.Value.GetString(); break; }
            }
            else if (p.Name is "min_age" or "max_age")
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var age) || age < 0 || age > 10000) return Bad(p.Name + " must be an age from 0 to 10000");
                if (p.Name == "min_age") condition._minAge = age; else condition._maxAge = age;
            }
            else return Bad("unknown condition '" + p.Name + "'");
        }
        return condition;
    }

    public bool Matches(Campaign c, Nation n)
    {
        if (c is null || n is null) return false;
        if (_role == "head_of_government" && !Rulers.HasDualExecutive(n)) return false;
        if (_role is not ("head_of_state" or "head_of_government") && Rulers.ActiveMinister(n, _role) is null) return false;
        var p = _role == "head_of_state" ? Rulers.HeadOfState(c, n) : _role == "head_of_government" ? Rulers.HeadOfGovernment(c, n) : OfficeholderEffect.MinisterProfile(c, n, _role);
        return (_name is null || p.Name == _name) && (_trait is null || p.TraitId == _trait) &&
            (_portrait is null || p.PortraitId == _portrait) && (!_minAge.HasValue || p.Age >= _minAge) && (!_maxAge.HasValue || p.Age <= _maxAge);
    }
}
