using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// What a country is called: stable keys, specific politics before the neutral identity,
/// and the player's own preference before either.
///
/// Three questions are asked in order, and they are different questions.
///
/// WHAT HAS THIS COUNTRY BECOME. A republic that proclaims an empire is not called what it
/// was called, and a proclaimed union is not called after its founder, so the government,
/// the ideology and the formable identity are asked first and the most specific row wins.
///
/// HOW DOES THIS PLAYER SPELL IT. Some countries have two names in the same language and no
/// amount of research settles which is right, because the argument is not about research.
/// Belarus and Белоруссия are the same place; which one appears on the map is a matter of
/// who is looking at it. So a country may have a <em>variant</em> - an option on the settings
/// screen, `country_name_&lt;tag&gt;`, whose value is a suffix on the localisation key:
///
///     country_name_blr = "belarus"      ->  country.BLR.name.belarus
///     country_name_blr = "belorussia"   ->  country.BLR.name.belorussia
///
/// It is data all the way down. A mod that wants the same for Ireland, Macedonia, Côte
/// d'Ivoire or the Persian Gulf adds one setting to `common/settings` and two rows to its
/// localisation, and writes no code. A variant with no row behind it changes nothing, which
/// is a feature rather than an oversight: an option can exist purely so that somebody can be
/// told their feelings have been taken into account.
///
/// AND FAILING BOTH, the plain name - which is what nearly every country uses, because
/// nearly every country's name is not an argument.
/// </summary>
public static class CountryNames
{
    /// <summary>The settings id that declares a naming variant: `country_name_blr`.</summary>
    public const string VariantPrefix = "country_name_";

    private static Dictionary<string, string> _variants;

    /// <summary>The spelling chosen for each country that offers a choice, by tag.</summary>
    public static IReadOnlyDictionary<string, string> Variants => _variants ??= Build();

    /// <summary>
    /// Read the choices again. Called when a naming option changes and when the settings
    /// catalogue is rebuilt, so the map redraws under the new spelling immediately rather
    /// than at the next start.
    /// </summary>
    public static void Refresh() => _variants = Build();

    private static Dictionary<string, string> Build()
    {
        // Built once and swapped in whole rather than asked per label. `Resolve` runs for
        // every country on the map every time the map is relabelled, and a per-call lookup
        // would be 261 settings scans and 261 lowercase allocations for an answer that only
        // changes when a player clicks something.
        var built = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in SettingsCatalog.All)
        {
            if (definition.Id.Length <= VariantPrefix.Length ||
                !definition.Id.StartsWith(VariantPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var tag = definition.Id[VariantPrefix.Length..];
            var chosen = SettingsCatalog.Chosen(definition.Id);
            if (chosen.Length > 0) built[tag] = chosen;
        }
        return built;
    }

    /// <summary>
    /// A country's name from its tag alone, for the places that hold a tag rather than a
    /// country - a stored trade instruction, a saved war goal, a line in a log.
    ///
    /// Goes through the same rows as everything else, so a country renamed in data or spelled
    /// differently by a player's own setting reads the same here as it does on the map.
    /// </summary>
    public static string Of(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "";
        Variants.TryGetValue(tag, out var variant);
        if (!string.IsNullOrEmpty(variant) && Loc.Has($"country.{tag}.name.{variant}"))
            return Loc.Get($"country.{tag}.name.{variant}");
        return Loc.Has($"country.{tag}.name") ? Loc.Get($"country.{tag}.name") : tag;
    }

    /// <summary>
    /// Whose rows a country's name is read from: its own, the state it has proclaimed itself -
    /// or, for a country brought back (FormableRestorations), the country it brought back, so
    /// a restored Montenegro is called Montenegro in every form its politics can give it.
    /// </summary>
    private static string Identity(Nation nation)
    {
        var restored = FormableRestorations.RestoredTag(nation.FormedNationId);
        if (restored is not null) return "country." + restored;
        return string.IsNullOrEmpty(nation.FormedNationId) ? "country." + nation.Tag : "formable." + nation.FormedNationId;
    }

    public static string Resolve(Nation nation, bool map)
    {
        if (nation is not null && !string.IsNullOrWhiteSpace(nation.CustomName))
            return Loc.Get(nation.CustomName);
        // A state the campaign founded carries the name its people, ground and politics
        // call for. A proclaimed formable or a restoration still wins over it: the new
        // proclamation is what the country has become, and the name it took at birth is
        // what it was.
        if (nation is not null && string.IsNullOrEmpty(nation.FormedNationId) &&
            NationNaming.Render(null, nation, map) is { Length: > 0 } founded)
            return founded;
        var identity = Identity(nation);
        var role = map ? ".map" : ".name";
        Variants.TryGetValue(nation.Tag ?? "", out var variant);
        foreach (var key in new[] { identity + ".government." + nation.Government + ".ideology." + nation.Ideology + role,
                     identity + ".government." + nation.Government + role, identity + ".ideology." + nation.Ideology + role })
        {
            // The variant is asked at each level rather than ahead of all of them, so a
            // country that has become something else is still called what it became: a
            // spelling preference is about the name, not about the state.
            if (!string.IsNullOrEmpty(variant) && Loc.Has(key + "." + variant)) return Loc.Get(key + "." + variant);
            if (Loc.Has(key)) return Loc.Get(key);
        }
        // No name written for what it has become: the name it had, in the form its new
        // politics give a state - a France gone communist is a People's Republic. Not on the
        // map, where the short name is the label.
        var plain = Plain(nation, map, identity + role, variant);
        // A name that already says what kind of state it is does not need a second coat of
        // paint: "Republic of United Republic of Tanzania" is the same joke as "ATM machine".
        if (!map && Form(nation) is { Length: > 0 } form && !NationNaming.CarriesStateForm(plain))
            return Loc.Format(form, plain);
        return plain;
    }

    /// <summary>The name with no form of state in it: the country's row, or its own name.</summary>
    private static string Plain(Nation nation, bool map, string key, string variant)
    {
        if (!string.IsNullOrEmpty(variant) && Loc.Has(key + "." + variant)) return Loc.Get(key + "." + variant);
        if (Loc.Has(key)) return Loc.Get(key);
        if (map) return Plain(nation, false, key[..^".map".Length] + ".name", variant);
        // A proclaimed state whose identity row was never written is still the state its
        // catalogue names. Eighty-one of the eighty-six formables carry a name in their data
        // and no localisation row, and the row's absence used to put the key itself on the
        // map: "formable.gulf_union.name" where a country's name belonged.
        if (FormableNameOf(key) is { Length: > 0 } formable) return formable;
        // An armed opposition is named for what it wants and the country it wants it in:
        // "rising:salvation_front:ETH" reads "National Salvation Front of Ethiopia" in the
        // reader's own language, with the country's own translated name (CivilWarOutbreak).
        if (nation.Name.StartsWith("rising:", StringComparison.Ordinal))
        {
            var parts = nation.Name.Split(':', 4);
            if (parts.Length == 4)
            {
                var country = Loc.Has($"country.{parts[2]}.name") ? Loc.Get($"country.{parts[2]}.name") : Loc.Get(parts[3]);
                return Loc.Format("rising." + parts[1], country);
            }
        }
        return NameOfRecord(nation);
    }

    /// <summary>
    /// The name stored on the nation, translated where it can be and never handed back as a
    /// key. A save written while the name had been stored as a recipe - the old Gulf Union
    /// card - must open as a country, not as "formable.gulf_union.name".
    /// </summary>
    private static string NameOfRecord(Nation nation)
    {
        var raw = nation.Name ?? "";
        if (NationNaming.TryTranslate(raw, out var translated)) return translated;
        if (FormableNameOf(raw) is { Length: > 0 } formable) return formable;
        // Nothing left but the country's own row, and failing that its tag. A tag is short,
        // ugly and true; a key is none of those things.
        var own = Loc.Has($"country.{nation.Tag}.name") ? Loc.Get($"country.{nation.Tag}.name") : "";
        if (own.Length > 0) return own;
        return string.IsNullOrWhiteSpace(nation.Tag) ? raw : nation.Tag;
    }

    /// <summary>The catalogue name behind a "formable.&lt;id&gt;.name" key, or nothing.</summary>
    private static string FormableNameOf(string key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith("formable.", StringComparison.Ordinal)) return "";
        var id = key["formable.".Length..];
        if (id.EndsWith(".name", StringComparison.Ordinal)) id = id[..^".name".Length];
        else if (id.EndsWith(".map", StringComparison.Ordinal)) id = id[..^".map".Length];
        var definition = FormableNations.Find(id);
        return definition is { Name.Length: > 0 } ? Loc.GetOr(definition.Name, definition.Name) : "";
    }

    /// <summary>
    /// A name written into history or a war's record, read back for the player: translated
    /// where a row exists, the catalogue's name where the record holds a key, and nothing
    /// rather than a key when neither can answer. Chronicle entries outlive the language and
    /// the catalogue version that wrote them, so both routes are needed at read time.
    /// </summary>
    public static string Recorded(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (Loc.Has(text)) return Loc.Get(text);
        if (FormableNameOf(text) is { Length: > 0 } formable) return formable;
        return NationNaming.LooksLikeKey(text) ? "" : text;
    }

    /// <summary>
    /// The form of state a change of politics gives a name ("name.form.*" rows), or nothing.
    /// Only a change: a country is called what it was called in 2024 until it becomes
    /// something else, and Spain is not renamed the Kingdom of Spain on the first morning.
    /// </summary>
    public static string Form(Nation nation)
    {
        var restored = FormableRestorations.RestoredTag(nation?.FormedNationId);
        if (nation is null || nation.Movement is not null ||
            (!string.IsNullOrEmpty(nation.FormedNationId) && restored is null) ||
            nation.Name.StartsWith("rising:", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(nation.StartGovernment)) return "";
        string Row(string form) => Loc.Has("name.form." + form) ? "name.form." + form : "";
        if (nation.Ideology != nation.StartIdeology)
        {
            if (nation.Ideology == "communism") return Row("communism");
            if (nation.Ideology == "fascism") return Row("fascism");
        }
        if (nation.Government == nation.StartGovernment) return "";
        static bool Monarchy(string government) => government is "absolute_monarchy" or "constitutional_monarchy";
        static bool Republic(string government) => government is "parliamentary_republic" or "presidential_republic" or "direct_democracy";
        if (Monarchy(nation.Government)) return Monarchy(nation.StartGovernment) ? "" : Row("kingdom");
        if (Monarchy(nation.StartGovernment) && Republic(nation.Government)) return Row("republic");
        return nation.Government switch
        {
            "theocracy" => Row(Muslim(nation) ? "islamic_republic" : "holy_state"),
            "federation" => Row("federation"),
            "technocracy" => Row("technate"),
            "provisional_government" => Row("provisional"),
            _ => ""
        };
    }

    private static bool Muslim(Nation nation)
    {
        if (!string.IsNullOrWhiteSpace(nation.DeclaredReligion) && !StateIdentity.IsSecular(nation.DeclaredReligion))
            return nation.DeclaredReligion is "sunni" or "shia" or "ibadi" or "sufi";
        var tag = FormableRestorations.RestoredTag(nation.FormedNationId) ?? nation.Tag;
        var culture = DemographyCatalog.Culture(DemographyCatalog.PrimaryCulture(tag));
        if (culture is null) return false;
        var share = 0f;
        foreach (var (faith, weight) in culture.Religions)
            if (faith is "sunni" or "shia" or "ibadi" or "sufi") share += weight;
        return share >= 0.5f;
    }

    /// <summary>What every country was on the day it was first seen, for <see cref="Form"/>.</summary>
    public static void Remember(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            if (!string.IsNullOrEmpty(nation.StartGovernment)) continue;
            nation.StartGovernment = nation.Government ?? "";
            nation.StartIdeology = nation.Ideology ?? "";
        }
    }

    private static Dictionary<string, CityDefinition> _citiesByName;
    private static IReadOnlyList<CityDefinition> _citiesFrom;

    /// <summary>
    /// A country's capital in the reader's language: its own row ("capital.&lt;name&gt;"), else the
    /// city of that name, whose translation the map already reads (city.&lt;id&gt;.name), else the
    /// name as a key. The dossier said "Warsaw" under a map that said "Варшава", and the capital
    /// of Guernsey stood on the Russian map as "Saint Peter Port".
    /// </summary>
    public static string Capital(Nation nation, Campaign campaign = null)
    {
        if (nation is null) return "";
        var name = campaign is null ? nation.CapitalName : CapitalCore.EffectiveName(campaign, nation);
        if (string.IsNullOrWhiteSpace(name)) return campaign is null ? "" : Loc.Get("No government seat");
        if (Loc.Has("capital." + name)) return Loc.Get("capital." + name);
        var cities = CityCatalog.All;
        if (!ReferenceEquals(cities, _citiesFrom) || _citiesByName is null)
        {
            var byName = new Dictionary<string, CityDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var city in cities)
                if (!byName.TryGetValue(city.Name, out var known) || !known.IsNationalCapital && city.IsNationalCapital)
                    byName[city.Name] = city;
            _citiesByName = byName;
            _citiesFrom = cities;
        }
        // The city's translation when it has one; otherwise the name itself as a key, which is
        // how the capitals' own table (41_capitals) translates the rest.
        return _citiesByName.TryGetValue(name, out var found) && Loc.Has($"city.{found.Id}.name")
            ? found.DisplayName
            : Loc.Get(name);
    }

    public static string CapitalTitle(Campaign campaign, Nation nation) =>
        campaign is not null && !CapitalCore.HasSeat(campaign, nation) ? "Government seat" :
        nation?.GovernmentSeatProvinceId > 0 ? "Temporary government seat" : "Capital";

    /// <summary>Optional localized short identity for narrow HUD columns. A formed
    /// state's identity wins over its founder; full names remain in dossiers/tooltips.</summary>
    public static string Compact(Nation nation)
    {
        if (nation is null) return "";
        var identity = Identity(nation);
        return Loc.GetOr(identity + ".short_name", nation.DisplayName);
    }
}

public sealed partial class Nation
{
    /// <summary>The government and ideology the country had when first seen (CountryNames.Form).</summary>
    public string StartGovernment = "";
    public string StartIdeology = "";
    /// <summary>Optional player or restoration custom name override.</summary>
    public string CustomName = "";
    /// <summary>Optional arbitrary flag sprite override (e.g. flag_rus_communism).</summary>
    public string CustomFlag = "";
}

public sealed class SetNationalCosmeticsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetNationId { get; init; } = -1;
    public string CustomName { get; init; } = "";
    public string CustomFlag { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (campaign is null) return;
        var issuer = campaign.NationById(IssuerId);
        var target = TargetNationId >= 0 ? campaign.NationById(TargetNationId) : issuer;
        if (issuer is null || target is null) return;
        // Player may customize their own nation, a direct subject of theirs, or a political movement
        if (target.Id != issuer.Id && !target.AnswersTo(issuer.Id) && target.Movement is null) return;
        target.CustomName = (CustomName ?? "").Trim();
        target.CustomFlag = (CustomFlag ?? "").Trim();
        campaign.MarkStrategicChange();
        campaign.MarkMapChanged();
    }
}
