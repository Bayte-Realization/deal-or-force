using System;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class NationNaming
{
    /// <summary>
    /// The adjective a people wears in the reader's language: "Ukrainian", "Украинская".
    /// A translation row of its own wins; then the authored English word; then the people's
    /// own name, which in this table is already an adjective for most of the world.
    /// </summary>
    public static string CultureAdjective(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "";
        if (Loc.Has("name.adj." + culture)) return Loc.Get("name.adj." + culture);
        var definition = DemographyCatalog.Culture(culture);
        if (definition is null) return "";
        if (!string.IsNullOrWhiteSpace(definition.Adjective)) return Loc.Get(definition.Adjective);
        return Loc.Get(definition.Name);
    }

    /// <summary>Whether the tables can name a state after this people at all.</summary>
    private static bool HasIdentity(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return false;
        return Loc.Has("name.adj." + culture) ||
            DemographyCatalog.Culture(culture) is { } definition &&
            (!string.IsNullOrWhiteSpace(definition.Country) ||
             !string.IsNullOrWhiteSpace(definition.Adjective) ||
             !string.IsNullOrWhiteSpace(definition.Name));
    }

    /// <summary>Whether two tags hold the same people. Identity, not the language family.</summary>
    private static bool SamePeople(string culture, Nation parent)
    {
        if (string.IsNullOrWhiteSpace(culture) || parent is null) return false;
        // A country can be several peoples at once - Russia is Russian and Tatar and Chechen in
        // the same legal sense - so a state carved out of it by one of its own peoples is a
        // region leaving, not a nation being born. Only a people outside that set is new.
        foreach (var people in DemographyCatalog.PrimaryCultures(parent.Tag))
            if (string.Equals(culture, people, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A base built on an adjective rather than a noun: "the Ukrainian Republic".</summary>
    private static bool IsAdjectiveBase(Nation nation) =>
        nation is not null && nation.FoundNameKind is "culture_adj" or "land";

    /// <summary>
    /// Take an administrative word off the end of a region's name.
    ///
    /// "Greater Poland Voivodeship" is what a province is called; a country is not called the
    /// Republic of Greater Poland Voivodeship. The same goes for a prefecture, a state, an
    /// oblast, a governorate: they are the shape of a subdivision, not the name of a nation,
    /// and a player reading them hears a spreadsheet. Only a trailing word is removed, so
    /// "West Virginia" keeps its name.
    /// </summary>
    public static string StripAdministrativeSuffix(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        var trimmed = StripRepublicPrefix(name.TrimEnd());
        foreach (var (suffix, cut) in AdministrativeSuffixes)
        {
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var inner = trimmed[..^cut].TrimEnd();
            // Only when something is left and it is a name rather than the word alone: a
            // region actually called "Province" keeps it, and "West Virginia" is not touched.
            if (inner.Length >= 3) return inner;
        }
        return trimmed;
    }

    /// <summary>Trailing administrative words, in both shipped languages, and how long each is.</summary>
    private static readonly (string Suffix, int Cut)[] AdministrativeSuffixes =
    {
        (" Voivodeship", 12), (" Province", 9), (" Prefecture", 11), (" Governorate", 12),
        (" Department", 11), (" Oblast", 7), (" Region", 7), (" County", 7),
        (" State", 6), (" District", 9), (" Territory", 10), (" Municipality", 13),
        (" Воеводство", 10), (" Провинция", 10), (" Префектура", 11), (" Губерния", 9),
        (" Область", 8), (" Регион", 7), (" Округ", 6), (" Район", 6),
        // Russia's republics are regions called "Mari El Republic" or "Republic of Dagestan":
        // a state founded on one was "Republic of Mari El Republic".
        (" Republic", 9), (" Республика", 11)
    };

    /// <summary>A leading "Republic of" ("Республика"), which the state's own form will say again.</summary>
    public static string StripRepublicPrefix(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        foreach (var prefix in RepublicPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length - prefix.Length >= 3)
                return name[prefix.Length..].TrimStart();
        return name;
    }

    /// <summary>The short name of a people's state for a map label: "Ukraine", "Kurdistan".</summary>
    private static string ShortPeopleName(string culture, string adjective)
    {
        var definition = DemographyCatalog.Culture(culture);
        return !string.IsNullOrWhiteSpace(definition?.Country)
            ? Loc.Get(definition.Country)
            : adjective;
    }

    /// <summary>
    /// Whether a region name is itself an adjective, as Russian oblast names are. Such a name
    /// takes the form of state after it, not in front of it.
    /// </summary>
    private static bool LooksAdjectival(string name)
    {
        var word = (name ?? "").Trim();
        return word.Length >= 4 &&
            (word.EndsWith("ая", StringComparison.OrdinalIgnoreCase) ||
             word.EndsWith("яя", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The grammatical gender a form of state has, in a language whose adjectives agree with
    /// it. A khanate is neuter and an emirate masculine, so "Казахское ханство" and "Аварский
    /// имамат" are both the adjective the tables hold, wearing the right ending. A language
    /// without agreement ignores the question entirely.
    /// </summary>
    private static char GenderOf(string form) => form switch
    {
        "kingdom" or "khanate" or "holy_state" or "junta" or "provisional" or "host" => 'n',
        "emirate" or "imamate" or "sultanate" or "patriarchate" => 'm',
        _ => 'f'
    };

    /// <summary>The feminine adjective a people or land carries, put into another gender.</summary>
    private static string Agree(string adjective, char gender)
    {
        if (!string.Equals(Loc.Language, "russian", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(adjective) || gender == 'f') return adjective;
        var word = adjective.Trim();
        if (word.Length < 4) return adjective;
        var stem = word[..^2];
        if (!word.EndsWith("ая", StringComparison.OrdinalIgnoreCase) &&
            !word.EndsWith("яя", StringComparison.OrdinalIgnoreCase)) return adjective;
        return gender == 'm' ? stem + "ий" : stem + "ое";
    }

    /// <summary>
    /// Turn a Russian city or regional seat noun (e.g. "Ростов", "Самара", "Одесса", "Новосибирск")
    /// into a clean adjective ("Ростовская", "Самарская", "Одесская", "Новосибирская") so that
    /// the full title reads naturally: "Ростовская Республика", not "Республика Ростов".
    /// </summary>
    public static string MakeRussianRegionAdjective(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var trimmed = name.Trim();
        if (LooksAdjectival(trimmed)) return trimmed;
        var hasCyrillic = false;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if ((c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я')) { hasCyrillic = true; break; }
        }
        if (!hasCyrillic) return "";

        var lower = trimmed.ToLowerInvariant();
        if (lower == "москва") return "Московская";
        if (lower is "санкт-петербург" or "петербург") return "Петербургская";
        if (lower == "уфа") return "Уфимская";
        if (lower == "чита") return "Читинская";
        if (lower == "тверь") return "Тверская";
        if (lower == "пермь") return "Пермская";
        if (lower == "вологда") return "Вологодская";
        if (lower == "калуга") return "Калужская";
        if (lower == "казань") return "Казанская";
        if (lower == "рязань") return "Рязанская";
        if (lower == "астрахань") return "Астраханская";
        if (lower == "тюмень") return "Тюменская";
        if (lower == "псков") return "Псковская";

        if (lower.EndsWith("ск") || lower.EndsWith("цк") || lower.EndsWith("шк"))
            return trimmed + "ая";
        if (lower.EndsWith("град") || lower.EndsWith("город") || lower.EndsWith("бург"))
            return trimmed + "ская";
        if (lower.EndsWith("ань") || lower.EndsWith("ень") || lower.EndsWith("онь") || lower.EndsWith("ь"))
            return trimmed[..^1] + "ская";
        if (lower.EndsWith("а") || lower.EndsWith("я") || lower.EndsWith("о") || lower.EndsWith("е"))
            return trimmed[..^1] + "ская";

        return trimmed + "ская";
    }

    /// <summary>
    /// The form of state the politics give a name: what turns "Kurdistan" into a republic,
    /// a kingdom or an Islamic republic.
    /// </summary>
    public static string FormFor(string government, string ideology, string religion, string culture = "")
    {
        var faith = string.IsNullOrWhiteSpace(religion) || StateIdentity.IsSecular(religion) ? "" : religion;
        var muslim = faith is "sunni" or "shia" or "ibadi" or "sufi";
        var christian = faith is "orthodox" or "catholic" or "protestant";
        // A throne is not called the same thing on the steppe, in the Caucasus and in Arabia.
        // The culture decides which word for a ruler a state wears, the way "Kazakh Khanate"
        // and "Sultanate of Oman" are facts about those countries rather than about monarchy.
        var group = DemographyCatalog.Culture(culture)?.Group ?? "";
        var steppe = group is "turkic" or "mongolic" or "tungusic" or "siberian";
        var caucasian = group is "caucasian";
        var arabian = group is "arabic";
        return government switch
        {
            "absolute_monarchy" => muslim
                ? steppe ? "khanate" : arabian ? "sultanate" : "emirate"
                : steppe ? "khanate" : "kingdom",
            "constitutional_monarchy" => steppe ? "khanate" : "kingdom",
            // A theocracy is rule by the faith. In the Caucasus that has a name of its own -
            // the Imamate - and elsewhere the modern word is an Islamic republic. A Christian
            // theocracy is a patriarchate.
            "theocracy" => muslim
                ? caucasian ? "imamate" : "islamic_republic"
                : christian ? "patriarchate" : "holy_state",
            "one_party_state" when ideology == "communism" => "people_republic",
            "one_party_state" => "republic",
            "military_junta" => "junta",
            "federation" => "federation",
            "direct_democracy" or "anarchism" => "commune",
            "technocracy" => "technate",
            "provisional_government" => "republic",
            _ => ideology == "communism" ? "people_republic" : "republic"
        };
    }

    /// <summary>The base of a country's name, in the reader's language.</summary>
    public static string BaseName(Campaign campaign, Nation nation)
    {
        if (nation is null) return "";
        switch (nation.FoundNameKind)
        {
            case "culture":
                var definition = DemographyCatalog.Culture(nation.FoundNameId);
                return !string.IsNullOrWhiteSpace(definition?.Country)
                    ? Loc.Get(definition.Country)
                    : DemographyCatalog.CultureName(nation.FoundNameId);
            case "culture_adj":
                var adjective = CultureAdjective(nation.FoundNameId);
                if (adjective.Length > 0) return adjective;
                return DemographyCatalog.Culture(nation.FoundNameId) is { Country.Length: > 0 } people
                    ? Loc.Get(people.Country)
                    : nation.FoundNameBase;
            case "land":
                return LandAtlas.Find(nation.FoundNameId) is { } land
                    ? LandAtlas.Name(land)
                    : nation.FoundNameBase;
            case "free_tag":
                var freeCountry = CountryNames.Of(nation.FoundNameId);
                return Loc.Has("name.found.free")
                    ? Loc.Format("name.found.free", freeCountry)
                    : "Free " + freeCountry;
            case "region":
                if (FindStateById(campaign, nation.FoundNameId) is { } region)
                {
                    var resolved = RegionNames.Resolve(region);
                    // A subdivision's administrative word is not part of a country's name: a
                    // state founded on the Greater Poland Voivodeship is the Republic of
                    // Greater Poland, not of Greater Poland Voivodeship.
                    resolved = StripAdministrativeSuffix(resolved);
                    // "Eastern" is a side of a country, not a country. A save written by the
                    // older composer can still hold that recipe, so it is repaired on the way
                    // out: the side, anchored on the country it was a side of.
                    if (CompassDirection(resolved) is { Length: > 0 } compass &&
                        campaign.NationById(nation.ReleasedFromId) is { } parent)
                    {
                        var source = IdentitySource(parent);
                        var anchor = SourceName(source.Kind, source.Id, "");
                        if (anchor.Length > 0) return Loc.Format("name.direction." + compass, anchor).Trim();
                    }
                    return resolved;
                }
                return nation.FoundNameBase;
            case "tag":
                return CountryNames.Of(nation.FoundNameId);
            case "formable":
                return FormableNations.Find(nation.FoundNameId) is { Name.Length: > 0 } catalogued
                    ? Loc.GetOr(catalogued.Name, catalogued.Name)
                    : nation.FoundNameBase;
            case "literal":
                // A recipe nothing defines is not a name. The formable's own row in the data is.
                if (TryTranslate(nation.FoundNameId, out var literal)) return literal;
                if (FormableIdOf(nation.FoundNameId) is { Length: > 0 } fId &&
                    FormableNations.Find(fId) is { Name.Length: > 0 } fDef)
                    return Loc.GetOr(fDef.Name, fDef.Name);
                return nation.FoundNameBase;
            case "direction":
                if (!ReadDirection(nation.FoundNameId, out var direction, out var kind, out var id)) return nation.FoundNameBase;
                var sided = SourceName(kind, id, nation.FoundNameBase);
                // A source that already names a side takes no second one: "Northern
                // Matabeleland South" was a state founded on Matabeleland South.
                return NamesASide(sided) ? sided : Loc.Format("name.direction." + direction, sided);
            default:
                // An unknown recipe from an older save: its stored name is a name unless it
                // is a key, in which case the country's own name is the only honest answer.
                return LooksLikeKey(nation.Name) ? CountryNames.Of(nation.Tag) : nation.Name ?? "";
        }
    }

    /// <summary>
    /// The base of a country's name with no form of state in it.
    ///
    /// A base is an ingredient: `Render` wraps it in the form the politics call for, so a base
    /// that already carries one produces "Republic of South Republic of India". This is the
    /// one place that guarantee is made, and every route into a base goes through here.
    /// </summary>
    public static string BaseOf(Campaign campaign, Nation nation)
    {
        var basis = BaseName(campaign, nation);
        return string.IsNullOrWhiteSpace(basis) ? basis : WithoutStackedWrappers(basis);
    }

    private static readonly string[] CompassSides =
        { "Northeast", "Northwest", "Southeast", "Southwest", "North", "South", "East", "West" };

    /// <summary>
    /// Peel every form of state a previous composition put on, and nothing else.
    ///
    /// Recomposing a form over a base that already wears one produced "Republic of South
    /// Republic of India", so the form comes off here - repeatedly, because a save written by
    /// the older composer can carry two.
    ///
    /// A compass side is NOT a wrapper. "South India" is a name: a country released from
    /// India's south is South India, and peeling the "South" leaves a one-region state called
    /// India, which is the same name as the country it left and says nothing about where it
    /// is. Only the form of state comes off.
    /// </summary>
    private static string WithoutStackedWrappers(string basis)
    {
        var current = basis;
        for (var pass = 0; pass < 4; pass++)
        {
            var before = current;
            current = WithoutFormOfState(current);
            if (current.Equals(before, StringComparison.Ordinal)) break;
        }
        return current.Trim();
    }

    // A tag is an identifier, not necessarily a localisation key: released countries and
    // movements have tags too. Keep a translatable identity recipe, not "West SHA" or a
    // frozen English sentence. Strip a predecessor's directional wrapper on later splits.
    private static (string Kind, string Id) IdentitySource(Nation parent)
    {
        if (!string.IsNullOrWhiteSpace(parent.CustomName)) return ("literal", parent.CustomName);
        var restored = FormableRestorations.RestoredTag(parent.FormedNationId);
        if (restored is not null) return ("tag", restored);
        if (!string.IsNullOrEmpty(parent.FormedNationId))
        {
            // The catalogue row is the name — always. A raw "formable.<id>.name" key is a
            // recipe, not a name: whether a table has the row depends on the language, and
            // handing the key onward printed "formable.gulf_union.name" on a country card.
            // When no row exists at all, the key still carries history for the render path
            // (TryTranslate → the old formable's data), so it is kept as a recipe.
            if (FormableNations.Find(parent.FormedNationId) is not null)
                return ("formable", parent.FormedNationId);
            return ("literal", "formable." + parent.FormedNationId + ".name");
        }
        if (parent.FoundNameKind == "direction" &&
            ReadDirection(parent.FoundNameId, out _, out var kind, out var id)) return (kind, id);
        if (parent.FoundNameKind == "culture") return ("culture", parent.FoundNameId);
        if (parent.FoundNameKind == "region") return ("literal", "region." + parent.FoundNameId + ".name");
        if (parent.FoundNameKind == "literal") return ("literal", parent.FoundNameId);
        if (parent.FoundNameKind == "tag" && Loc.Has("country." + parent.FoundNameId + ".name"))
            return ("tag", parent.FoundNameId);
        // A movement's tag row is the movement's label ("Belarusian democratic forces"), not a
        // country: what it founds is called by the state name the movement fights for.
        if (parent.Movement is not null && MovementCatalog.Of(parent.Movement.Id)?.StateName is { Length: > 0 } stateName)
            return ("literal", stateName);
        if (Loc.Has("country." + parent.Tag + ".name")) return ("tag", parent.Tag);
        // Civil opposition's raw name is a recipe of its own. Prefer its country base,
        // not a new state called "Republic of National Salvation Front of ...".
        if (parent.Name?.StartsWith("rising:", StringComparison.Ordinal) == true)
        {
            var parts = parent.Name.Split(':', 4);
            if (parts.Length == 4)
                return Loc.Has("country." + parts[2] + ".name") ? ("tag", parts[2]) : ("literal", parts[3]);
        }
        // A split is named for what it split from, so the ingredient has to be the parent's
        // base and not its displayed name: India's own name reads "Republic of India" once its
        // politics have changed, and a south that took that as its base would be the Republic
        // of South Republic of India.
        return ("literal", WithoutFormOfState(parent.Name ?? ""));
    }

    private static bool ReadDirection(string recipe, out string direction, out string kind, out string id)
    {
        var parts = (recipe ?? "").Split(':', 3);
        direction = parts.Length > 0 ? parts[0] : "";
        kind = parts.Length > 1 ? parts[1] : "";
        id = parts.Length > 2 ? parts[2] : "";
        return direction is "north" or "south" or "east" or "west" &&
            kind is "tag" or "culture" or "literal" or "formable" && id.Length > 0;
    }

    private static string SourceName(string kind, string id, string fallback = "")
    {
        if (kind == "tag") return CountryNames.Of(id);
        if (kind == "culture")
            return DemographyCatalog.Culture(id)?.Country is { Length: > 0 } name
                ? Loc.Get(name) : DemographyCatalog.CultureName(id);
        if (kind == "formable")
            return FormableNations.Find(id) is { Name.Length: > 0 } catalogued
                ? Loc.GetOr(catalogued.Name, catalogued.Name) : fallback;
        // A recipe nothing defines is not a name. The formable's own row in the data is the
        // honest answer: all eighty-six of them carry one, and eighty-one of those have no
        // localisation row, which is how a country came to be called "formable.gulf_union.name".
        if (TryTranslate(id, out var translated)) return translated;
        if (FormableIdOf(id) is { Length: > 0 } formableId &&
            FormableNations.Find(formableId) is { Name.Length: > 0 } formable)
            return Loc.GetOr(formable.Name, formable.Name);
        return fallback;
    }

    /// <summary>The formable id a recipe names, or nothing when it names something else.</summary>
    private static string FormableIdOf(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("formable.", StringComparison.Ordinal)) return "";
        var body = id["formable.".Length..];
        var cut = body.LastIndexOf(".name", StringComparison.Ordinal);
        return cut > 0 ? body[..cut] : "";
    }

    /// <summary>
    /// Translate a recipe, or say plainly that it is not one.
    ///
    /// `Loc.Get` answers a key it does not have with the key itself, which is the right
    /// behaviour for a screen - a missing label should be visible to whoever is testing - and
    /// the wrong one for a name, because a country called "formable.gulf_union.name" is a
    /// country nobody can read. Every name in the game is resolved through here, so a recipe
    /// nothing defines can no longer reach the map, a dossier or a chronicle line as raw text.
    /// </summary>
    public static bool TryTranslate(string id, out string name)
    {
        name = "";
        if (string.IsNullOrEmpty(id)) return false;
        // A recipe is a key; a literal is a sentence. A literal that happens to contain a dot
        // is still a literal, so only something shaped like a key is asked of the tables.
        if (!LooksLikeKey(id)) { name = id; return true; }
        if (!Loc.Has(id)) return false;
        var text = Loc.Get(id);
        if (text.Length == 0 || string.Equals(text, id, StringComparison.Ordinal)) return false;
        name = text;
        return true;
    }

    /// <summary>Whether a string is shaped like one of this game's localisation keys.</summary>
    public static bool LooksLikeKey(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('.')) return false;
        foreach (var prefix in KeyPrefixes)
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] KeyPrefixes =
    {
        "formable.", "country.", "region.", "name.", "city.", "culture.", "religion.",
        "government.", "ideology.", "rising.", "subject.", "war.", "good.", "building.",
        "technology.", "decision.", "event.", "modifier.", "handbook."
    };

    /// <summary>
    /// Take a form of state back out of a name, so it can be put in exactly once.
    ///
    /// A base that already reads "Republic of India" composed with "Republic of {0}" gives
    /// "Republic of South Republic of India", which is the naming equivalent of "ATM machine".
    /// The form is a wrapper the politics add at the end, so anything used as an ingredient
    /// has to arrive without one - and a name a mod wrote that legitimately contains the word
    /// "republic" in the middle is left alone, because only a wrapper is stripped.
    /// </summary>
    public static string WithoutFormOfState(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        var current = name.Trim();
        bool changed;
        do
        {
            changed = false;
            foreach (var (prefix, cut) in FormPrefixes)
            {
                if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var inner = current[cut..].Trim();
                    if (inner.Length > 0 && inner.Length <= 64)
                    {
                        current = inner;
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        // Also check if a directional prefix was attached to a country that carried a state form:
        // e.g. "South Republic of India" -> "South India", "Южная Республика Индия" -> "Южная Индия".
        foreach (var dir in CompassSides)
        {
            var prefix = dir + " ";
            if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = current[prefix.Length..].Trim();
                var cleanedRest = WithoutFormOfState(rest);
                if (!cleanedRest.Equals(rest, StringComparison.OrdinalIgnoreCase))
                    current = prefix + cleanedRest;
            }
        }
        foreach (var (prefix, _) in LegacyDirections)
        {
            if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = current[prefix.Length..].Trim();
                var cleanedRest = WithoutFormOfState(rest);
                if (!cleanedRest.Equals(rest, StringComparison.OrdinalIgnoreCase))
                    current = prefix + cleanedRest;
            }
        }

        return current.Trim();
    }

    /// <summary>Every form of state a name can be wrapped in, and how long the wrapper is.</summary>
    private static readonly (string Prefix, int Cut)[] FormPrefixes =
    {
        ("Republic of ", 12), ("Kingdom of ", 11), ("Islamic Republic of ", 20),
        ("Holy State of ", 14), ("People's Republic of ", 21), ("Military Government of ", 22),
        ("Federation of ", 14), ("Commune of ", 12), ("Technate of ", 12),
        ("Provisional Government of ", 25), ("Free ", 5), ("Islamic Emirate of ", 19),
        ("Cossack Host of ", 16), ("Patriarchate of ", 15), ("Military Command of ", 20),
        ("Provisional Council of ", 23), ("Liberation Front of ", 20), ("Военное Командование ", 21),
        ("Республика ", 11), ("Королевство ", 12), ("Исламская Республика ", 21),
        ("Святое Государство ", 19), ("Священное Государство ", 22), ("Народная Республика ", 21),
        ("Военное Правительство ", 23), ("Федерация ", 11), ("Коммуна ", 9), ("Технат ", 8),
        ("Вольная ", 7), ("Вольное ", 8), ("Вольный ", 8), ("Исламский Эмират ", 17),
        ("Всевеликое Войско ", 18), ("Патриархат ", 11), ("Временное Правительство ", 24)
    };

    /// <summary>The same wrappers as plain phrases, for asking whether a name already has one.</summary>
    private static readonly string[] FormPhrases = FormPrefixes.Select(pair => pair.Prefix.Trim()).ToArray();
}
