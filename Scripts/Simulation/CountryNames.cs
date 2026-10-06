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

    public static string Resolve(Nation nation, bool map)
    {
        var identity = string.IsNullOrEmpty(nation.FormedNationId) ? "country." + nation.Tag : "formable." + nation.FormedNationId;
        var role = map ? ".map" : ".name";
        Variants.TryGetValue(nation.Tag ?? "", out var variant);
        foreach (var key in new[] { identity + ".government." + nation.Government + ".ideology." + nation.Ideology + role,
                     identity + ".government." + nation.Government + role, identity + ".ideology." + nation.Ideology + role,
                     identity + role })
        {
            // The variant is asked at each level rather than ahead of all of them, so a
            // country that has become something else is still called what it became: a
            // spelling preference is about the name, not about the state.
            if (!string.IsNullOrEmpty(variant) && Loc.Has(key + "." + variant)) return Loc.Get(key + "." + variant);
            if (Loc.Has(key)) return Loc.Get(key);
        }
        return map ? Resolve(nation, false) : Loc.Get(nation.Name);
    }

    /// <summary>Optional localized short identity for narrow HUD columns. A formed
    /// state's identity wins over its founder; full names remain in dossiers/tooltips.</summary>
    public static string Compact(Nation nation)
    {
        if (nation is null) return "";
        var identity = string.IsNullOrEmpty(nation.FormedNationId) ? "country." + nation.Tag : "formable." + nation.FormedNationId;
        return Loc.GetOr(identity + ".short_name", nation.DisplayName);
    }
}
