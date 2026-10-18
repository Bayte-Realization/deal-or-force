using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>One thing about a country that its flag is allowed to depend on.</summary>
public sealed class FlagQualifier
{
    public string Id = "";

    /// <summary>
    /// Where the value comes from. The code understands a fixed handful - see
    /// <see cref="FlagCatalog.ValueOf"/> - because each one is a question only the
    /// simulation can answer. Everything else about a qualifier is data.
    /// </summary>
    public string Source = "";

    /// <summary>
    /// How specific this is, low first. It decides two things at once: the order the parts
    /// appear in a file name, and which of two flags wins when both exist.
    /// </summary>
    public int Order;

    /// <summary>
    /// A value this qualifier is not worth mentioning for. A country that is not at war
    /// should look for `flag_fra`, not `flag_fra_at_peace`, so the peacetime value is
    /// declared as silent and simply drops out of the name.
    /// </summary>
    public readonly HashSet<string> Silent = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Which flag a country flies.
///
/// Every country had exactly one flag for the length of a campaign, which is a strange
/// thing for a game whose entire subject is countries changing. Russia going communist flew
/// the tricolour; a restored Irish monarchy flew the tricolour; a country founded this
/// morning by people who had just seceded flew nothing at all, because no file was named
/// after it.
///
/// The fix keeps the rule the rest of the art already follows: <b>the file is the
/// declaration</b>. A flag is bound by its name and nothing else. Drop
/// `flag_rus_communism.png` into any `gfx/flags/` folder and Russia flies it while it is
/// communist; drop `flag_irl_absolute_monarchy.png` and an Irish monarchy has its harp.
/// There is no list to add the file to and no id to invent - naming it is the whole of the
/// work.
///
/// What this file adds is the part a file name cannot carry: <i>which</i> facts about a
/// country may appear in a name, and which of two candidates wins. Both are data, so a mod
/// can make a flag depend on something the base game never thought of - being in a
/// coalition, holding a particular region, a scripted era - by declaring one qualifier.
///
/// The search runs most specific first:
///
///     flag_rus_communism_one_party_state   both, because both were declared
///     flag_rus_communism                   the more specific qualifier alone
///     flag_rus_one_party_state             the less specific alone
///     flag_rus                             the country's own flag
///     flag_ideology_communism              a generic banner for anyone who has nothing
///     flag_culture_chechen                 or a people's flag, for a country just founded
///
/// The last two are what make a seceded country look like a country: it has no file named
/// after it and never will, and falling back to the flag of the people who founded it is a
/// better answer than a grey rectangle.
/// </summary>
public static partial class FlagCatalog
{
    private static readonly List<FlagQualifier> Qualifiers = new();
    private static readonly List<string> Fallbacks = new();
    private static bool PreferMovementBase = true;
    private static readonly Dictionary<string, string[]> GenericCultures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string[]> GenericTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Families that outrank the country's own flag rather than standing in for it.
    ///
    /// A fallback answers "this country has no flag of its own"; an identity answers "this
    /// country is no longer the country whose flag that is". Proclaiming a formable state is
    /// the second: the Soviet Union flying the Russian tricolour because Russia has one is
    /// the whole of what was wrong, and it is wrong in exactly the way the qualifier order
    /// already says it is - `formable` is declared first in common/flags because it is the
    /// most specific thing there is to know about a flag.
    /// </summary>
    private static readonly List<string> Identities = new();

    /// <summary>
    /// What each value will answer to besides itself, in order of preference.
    ///
    /// This is what makes the whole thing affordable. Three hundred countries, twelve
    /// ideologies and eleven forms of government is forty thousand things a country can
    /// be, and there are a few thousand flags in the world - but most of those conditions
    /// do not want a different flag from each other. A socialist republic and a communist
    /// one fly the same red banner; a kingdom and an empire fly the same crown; a one-party
    /// state looking for a flag is looking for the one the communist state found.
    ///
    /// So one file covers a class of conditions. The exact value is always tried first, so
    /// a country that does have its own flag for a condition still gets it.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, List<string>>> Aliases =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What each country's own shipped flag is the flag OF.
    ///
    /// A country's flag is not a neutral thing that happens to belong to it. The flag of
    /// the People's Republic is the flag of a communist one-party state, adopted by the
    /// government still flying it; so is North Korea's, Cuba's, Saudi Arabia's, Iran's
    /// since 1980. Treating `flag_chn` as the bottom of the chain - the answer whenever
    /// nothing better is found - meant a conservative presidential China went on flying the
    /// red banner with five stars, which is the one thing it certainly would not do.
    ///
    /// A country named here flies its own flag while it is still what that flag is for, and
    /// drops to the bottom of everything the moment it is not.
    /// </summary>
    private static readonly Dictionary<string, (string Source, string Value)[]> Native =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Between the values of one condition that accepts any of several. Never part of a slug.</summary>
    private const char AnyOf = '|';

    /// <summary>
    /// And what such a country flies instead, once its own flag is disqualified.
    ///
    /// Without this the search runs out: a conservative China has no conservative flag, no
    /// traditionalist one to borrow, and its own flag has just been ruled out - so it falls
    /// all the way through to the flag it was ruled out of, and flies the red banner after
    /// all. Naming the obvious answer is one line, and for most of these countries the
    /// obvious answer is the flag the other half of their own history uses.
    /// </summary>
    private static readonly Dictionary<string, string> Otherwise =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many qualifiers may appear in one name at once.
    ///
    /// Every combination is tried, so the cost of another qualifier doubles. Four is
    /// already sixteen candidates for one country, which is more distinctions than any
    /// flag set has ever wanted; a fifth qualifier still works, it is simply tried on its
    /// own rather than crossed with the other four.
    /// </summary>
    public const int MaxCombined = 4;

    public static IReadOnlyList<FlagQualifier> All => Qualifiers;

    public static IReadOnlyList<string> FallbackSources => Fallbacks;

    /// <summary>Families that stand above a country's own flag. See <see cref="Identities"/>.</summary>
    public static IReadOnlyList<string> IdentitySources => Identities;

    static FlagCatalog() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Qualifiers.Clear();
        Fallbacks.Clear();
        PreferMovementBase = true;
        GenericCultures.Clear();
        GenericTags.Clear();
        ContextRules.Clear();
        Identities.Clear();
        Aliases.Clear();
        Native.Clear();
        Otherwise.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "flags"))
            using (document)
                Merge(document.RootElement, errors);
        Qualifiers.Sort((left, right) => left.Order.CompareTo(right.Order));
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
        Qualifiers.Sort((left, right) => left.Order.CompareTo(right.Order));
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        MergeContextRules(root, errors);
        if (root.TryGetProperty("prefer_movement_base", out var movementBase) &&
            movementBase.ValueKind is JsonValueKind.True or JsonValueKind.False)
            PreferMovementBase = movementBase.GetBoolean();

        if (root.TryGetProperty("qualifiers", out var qualifiers) &&
            qualifiers.ValueKind == JsonValueKind.Object)
            foreach (var entry in qualifiers.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    errors?.Add($"common/flags: qualifier '{entry.Name}' is not a declaration.");
                    continue;
                }

                var qualifier = Find(entry.Name) ?? Add(entry.Name);
                if (entry.Value.TryGetProperty("from", out var source) &&
                    source.ValueKind == JsonValueKind.String)
                {
                    var id = source.GetString() ?? "";
                    if (!IsKnownSource(id))
                        errors?.Add($"common/flags: qualifier '{entry.Name}' reads '{id}', which " +
                                    "is not something the game can answer.");
                    qualifier.Source = id;
                }
                if (entry.Value.TryGetProperty("order", out var order) &&
                    order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out var rank))
                    qualifier.Order = rank;
                if (entry.Value.TryGetProperty("silent", out var silent) &&
                    silent.ValueKind == JsonValueKind.Array)
                {
                    qualifier.Silent.Clear();
                    foreach (var value in silent.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.String)
                            qualifier.Silent.Add(value.GetString() ?? "");
                }
            }

        if (root.TryGetProperty("also_answers_to", out var aliases) &&
            aliases.ValueKind == JsonValueKind.Object)
            foreach (var family in aliases.EnumerateObject())
            {
                // Grouped by the source the values come from, so an alias can only ever
                // name something of the same kind. A form of government standing in for an
                // ideology is the mistake this shape exists to make unwritable.
                if (!IsKnownSource(family.Name))
                {
                    errors?.Add($"common/flags: '{family.Name}' is not something the game can " +
                                "answer, so nothing can answer to it.");
                    continue;
                }
                if (family.Value.ValueKind != JsonValueKind.Object)
                {
                    errors?.Add($"common/flags: the aliases for '{family.Name}' are not a table.");
                    continue;
                }

                var table = Aliases.TryGetValue(family.Name, out var known)
                    ? known
                    : Aliases[family.Name] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in family.Value.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Array)
                    {
                        errors?.Add($"common/flags: '{entry.Name}' answers to something that is not a list.");
                        continue;
                    }
                    var chain = new List<string>();
                    foreach (var value in entry.Value.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.String) continue;
                        var id = Slug(value.GetString());
                        if (id.Length > 0 && !chain.Contains(id, StringComparer.OrdinalIgnoreCase))
                            chain.Add(id);
                    }
                    // An explicit empty list removes inherited aliases. Otherwise a mod
                    // cannot stop socialism borrowing a communist flag, for example.
                    table[Slug(entry.Name)] = chain;
                }
            }

        if (root.TryGetProperty("own_flag_is_for", out var native) &&
            native.ValueKind == JsonValueKind.Object)
            foreach (var entry in native.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    errors?.Add($"common/flags: what '{entry.Name}' flies is not a declaration.");
                    continue;
                }
                var conditions = new List<(string, string)>();
                foreach (var field in entry.Value.EnumerateObject())
                {
                    // A list is "any of these": the Saudi flag is the flag of the kingdom
                    // whether the game calls its government traditionalist or theocratic.
                    var values = field.Value.ValueKind switch
                    {
                        JsonValueKind.String => new[] { field.Value.GetString() ?? "" },
                        JsonValueKind.Array => field.Value.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString() ?? "").ToArray(),
                        _ => null
                    };
                    if (values is null) continue;
                    if (string.Equals(field.Name, "otherwise", StringComparison.OrdinalIgnoreCase))
                    {
                        Otherwise[Slug(entry.Name)] = values.FirstOrDefault() ?? "";
                        continue;
                    }
                    if (!IsKnownSource(field.Name))
                    {
                        errors?.Add($"common/flags: '{entry.Name}' names '{field.Name}', which " +
                                    "is not something the game can answer.");
                        continue;
                    }
                    var accepted = string.Join(AnyOf, values.Select(Slug).Where(value => value.Length > 0));
                    if (accepted.Length > 0) conditions.Add((field.Name, accepted));
                }
                // An explicit empty object removes the base regime restriction. Clear the
                // inherited alternative as well unless this declaration supplies its own.
                Native[Slug(entry.Name)] = conditions.ToArray();
                if (!entry.Value.TryGetProperty("otherwise", out _)) Otherwise.Remove(Slug(entry.Name));
            }

        ReadFamilies(root, "identities", Identities, errors);
        ReadFamilies(root, "fallbacks", Fallbacks, errors);
        if (root.TryGetProperty("generic_flag_tags", out var tagRules) && tagRules.ValueKind == JsonValueKind.Object)
            foreach (var rule in tagRules.EnumerateObject())
                if (rule.Value.ValueKind == JsonValueKind.Array)
                    GenericTags[rule.Name] = rule.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => Slug(v.GetString())).ToArray();
        if (root.TryGetProperty("generic_flag_cultures", out var rules) && rules.ValueKind == JsonValueKind.Object)
            foreach (var rule in rules.EnumerateObject())
                if (rule.Value.ValueKind == JsonValueKind.Array)
                    GenericCultures[rule.Name] = rule.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => Slug(v.GetString())).ToArray();
    }

    /// <summary>One declared list of families, replacing whatever was there.</summary>
    private static void ReadFamilies(JsonElement root, string key, List<string> into, List<string> errors)
    {
        if (!root.TryGetProperty(key, out var declared) || declared.ValueKind != JsonValueKind.Array) return;
        into.Clear();
        foreach (var value in declared.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            var id = value.GetString() ?? "";
            if (!IsKnownSource(id))
            {
                errors?.Add($"common/flags: {key} entry '{id}' is not something the game can answer.");
                continue;
            }
            into.Add(id);
        }
    }

    private static FlagQualifier Find(string id)
    {
        foreach (var qualifier in Qualifiers)
            if (string.Equals(qualifier.Id, id, StringComparison.OrdinalIgnoreCase)) return qualifier;
        return null;
    }

    private static FlagQualifier Add(string id)
    {
        var qualifier = new FlagQualifier { Id = id, Order = Qualifiers.Count * 10 };
        Qualifiers.Add(qualifier);
        return qualifier;
    }

    /// <summary>
    /// The questions a flag may be asked to depend on.
    ///
    /// Fixed, because each one is something only the simulation can answer, and a source a
    /// mod invents would have nowhere to get its answer from. Adding one is a line here and
    /// a line in <see cref="ValueOf"/>; everything else about flags is already data.
    /// </summary>
    public static bool IsKnownSource(string source) => source switch
    {
        "ideology" or "government" or "culture" or "continent" or
        "war" or "tag" or "focus" or "formable" or "revolt" or "subject" or "overlord" => true,
        _ => false
    };

    /// <summary>
    /// What one qualifier says about one country right now. An empty answer means the
    /// qualifier does not apply and drops out of every name.
    /// </summary>
    public static string ValueOf(string source, Campaign campaign, Nation nation)
    {
        if (nation is null) return "";
        var value = source switch
        {
            "ideology" => nation.Ideology,
            "government" => nation.Government,
            "culture" => DemographyCatalog.PrimaryCulture(nation.Tag),
            "continent" => nation.Continent,
            "tag" => nation.Tag,
            "focus" => nation.GovernmentFocus.ToString(),
            // A country that has proclaimed itself something. The most specific thing there
            // is to know about a flag - Yugoslavia is not Serbia with a different cabinet -
            // which is why it is declared first in common/flags.
            "formable" => nation.FormedNationId,
            "war" => AtWar(campaign, nation) ? "at_war" : "at_peace",
            // What kind of revolt founded it, for the countries a campaign creates. Empty
            // for every country that was on the map when it started, which is what keeps
            // this out of the way of the three hundred flags that do not need it.
            "revolt" => nation.FoundedInRevolt,
            // What it answers to somebody else as. This is where Vichy, Manchukuo and every
            // other government installed from outside belongs: not a thing the country
            // believes, and not how it is arranged, but who it is arranged BY.
            "subject" => nation.IsSubject ? nation.SubjectType : "",
            "overlord" => nation.IsSubject ? campaign?.NationById(nation.OverlordId)?.Tag ?? "" : "",
            _ => ""
        };
        return Slug(value);
    }

    private static bool AtWar(Campaign campaign, Nation nation)
    {
        if (campaign is null) return false;
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nation.Id)) return true;
        return false;
    }

    /// <summary>
    /// Every sprite name that could be this country's flag, best first.
    ///
    /// The caller asks the art whether each one exists and takes the first that does, so
    /// this never needs to know what has actually been drawn - which is what lets a mod add
    /// a flag by adding a file.
    ///
    /// "Best" is the total number of steps taken away from what the country actually is,
    /// across every mixture of the chosen qualifiers' chains, so
    /// `flag_rus_communism_one_party_state` is asked for before
    /// `flag_rus_socialism_one_party_state`, and both before anything two steps out.
    /// Ordering by distance rather than by enumeration order is what stops an alias beating
    /// the real thing.
    /// </summary>
    public static List<string> Candidates(Campaign campaign, Nation nation)
    {
        var names = new List<string>();
        if (nation is null) return names;

        var tag = Slug(nation.Tag);
        if (tag.Length == 0) return names;

        // A landless organisation's banner is its identity, not the hypothetical regime it
        // might establish. Procedural government/ideology variants must not replace it.
        // After taking territory or proclaiming a formable, ordinary dynamic rules resume.
        if (PreferMovementBase && nation.Movement is { Landless: true, Exile: false } &&
            string.IsNullOrEmpty(nation.FormedNationId))
            names.Add($"flag_{tag}");

        // A country brought back flies its own flag again, in the form its politics give it,
        // ahead of anything the country that restored it ever flew.
        if (FormableRestorations.RestoredTag(nation.FormedNationId) is { } restored)
        {
            var old = Slug(restored);
            names.Add($"flag_{old}_{Slug(nation.Government)}");
            names.Add($"flag_{old}_{Slug(nation.Ideology)}");
            names.Add($"flag_{old}");
        }

        // The qualifiers that have something to say about this country today, each as the
        // chain of values it will answer to: what it actually is, then whatever that value
        // has been declared to stand in for.
        var live = new List<(FlagQualifier Qualifier, List<string> Chain)>();
        foreach (var qualifier in Qualifiers)
        {
            var value = ValueOf(qualifier.Source, campaign, nation);
            if (value.Length == 0 || qualifier.Silent.Contains(value)) continue;
            live.Add((qualifier, Chain(qualifier.Source, value)));
        }

        // Combinations of the first few, most parts first: a flag that answers to both the
        // ideology and the form of government beats one that answers to either alone.
        //
        // Exact first, every one of them, and only then anything borrowed. That order is the
        // whole rule and it was wrong once: a flag reached one alias away used to beat the
        // country's own, so Russia - which is nationalist in 2024 and has a tricolour - flew
        // the Imperial black-yellow-white the moment a traditionalist flag existed to borrow.
        // A country's own flag beats a borrowed one. Always.
        var combined = Math.Min(live.Count, MaxCombined);

        // Which of the live qualifiers say the country has become something else. A name
        // that mentions one of them describes what it is now; a name that does not describes
        // what it used to be, and the second must not beat the first - which is exactly how
        // Greece proclaiming Byzantium went on flying flag_grc_parliamentary_republic.
        var identityMask = 0;
        for (var index = 0; index < combined; index++)
            if (Identities.Contains(live[index].Qualifier.Source, StringComparer.OrdinalIgnoreCase))
                identityMask |= 1 << index;

        // This country, as the thing it has proclaimed itself: the most specific answer there
        // is, so it goes above everything - above a generic flag for that proclaimed state,
        // and above every flag of the country that proclaimed it.
        if (identityMask != 0)
            for (var size = combined; size >= 1; size--)
                for (var mask = 0; mask < 1 << combined; mask++)
                {
                    if (System.Numerics.BitOperations.PopCount((uint)mask) != size) continue;
                    if ((mask & identityMask) == 0) continue;
                    Spread(names, "flag_" + tag, live, combined, mask, 0, 0);
                }

        // Then the proclaimed state's own flag, whoever proclaimed it.
        foreach (var source in Identities) Family(names, source, campaign, nation, live, combined);

        // And only then what the country was: every remaining combination of its qualifiers.
        for (var size = combined; size >= 1; size--)
            for (var mask = 0; mask < 1 << combined; mask++)
            {
                if (System.Numerics.BitOperations.PopCount((uint)mask) != size) continue;
                if ((mask & identityMask) != 0) continue;      // already named above
                Spread(names, "flag_" + tag, live, combined, mask, 0, 0);
            }
        for (var index = MaxCombined; index < live.Count; index++)
            names.Add($"flag_{tag}_{live[index].Chain[0]}");

        // The country's own flag, unless that flag is for something the country has stopped
        // being - in which case it goes to the very bottom, after everything else has had a
        // chance, because a communist banner over a conservative republic is worse than no
        // flag at all.
        var ownFlagStillFits = Fits(campaign, nation, tag);
        if (ownFlagStillFits) names.Add($"flag_{tag}");
        else if (Otherwise.TryGetValue(tag, out var instead) && instead.Length > 0)
            names.Add(instead);

        // And now what may be borrowed: the same combinations again, one or more steps out
        // along the alias chains. A socialist country with no socialist flag reaches the
        // communist one here, and a country that HAS its own flag has already taken it.
        for (var size = combined; size >= 1; size--)
            for (var mask = 0; mask < 1 << combined; mask++)
            {
                if (System.Numerics.BitOperations.PopCount((uint)mask) != size) continue;
                Spread(names, "flag_" + tag, live, combined, mask, 1, int.MaxValue);
            }
        for (var index = MaxCombined; index < live.Count; index++)
            for (var step = 1; step < live[index].Chain.Count; step++)
                names.Add($"flag_{tag}_{live[index].Chain[step]}");

        // And what a country with no flag of its own flies. This is the whole reason a
        // country founded mid-campaign does not show up as a grey rectangle.
        //
        // A fallback family takes the qualifiers too, and for the same reason the country
        // does: a Kurdish state that has gone communist is not flying the same thing as a
        // Kurdish monarchy, and a mechanic that produces new countries constantly is
        // exactly where that variety is worth having. `flag_culture_kurdish_communism`
        // beats `flag_culture_kurdish`, which beats nothing.
        // A nationwide opposition challenges its parent government, not the national
        // identity of whichever occupied/minority region happened to seed the rising.
        // Explicit artwork for the new tag still wins. Existing saves carry the parent
        // in their language-independent rising name, so this repairs their flags too.
        var civilParent = CivilWarParentTag(nation);
        if (civilParent.Length > 0)
        {
            for (var size = combined; size >= 1; size--)
                for (var mask = 1; mask < 1 << combined; mask++)
                    if (System.Numerics.BitOperations.PopCount((uint)mask) == size)
                        Spread(names, "flag_" + civilParent + "_civil_war", live, combined, mask, 0, 0);
            names.Add($"flag_{civilParent}_civil_war_{Slug(nation.Ideology)}");
            names.Add($"flag_{civilParent}_civil_war");
            for (var size = combined; size >= 1; size--)
                for (var mask = 1; mask < 1 << combined; mask++)
                    if (System.Numerics.BitOperations.PopCount((uint)mask) == size)
                        Spread(names, "flag_" + civilParent, live, combined, mask, 0, 0);
            if (Fits(campaign, nation, civilParent)) names.Add("flag_" + civilParent);
            else if (Otherwise.TryGetValue(civilParent, out var alternative)) names.Add(alternative);
        }
        foreach (var source in Fallbacks)
            if (civilParent.Length == 0 || source != "culture") Family(names, source, campaign, nation, live, combined);

        // Last of all, for a country whose own flag no longer fits and which nothing else
        // answered for: better the wrong flag than a grey rectangle, and a player can see
        // that nothing has been drawn for this yet.
        if (!ownFlagStillFits) names.Add($"flag_{tag}");
        // A specific historical emblem is not a universal ideology. Empty lists lift a mod's restriction.
        var culture = ValueOf("culture", campaign, nation);
        names.RemoveAll(name => GenericCultures.TryGetValue(name, out var accepted) && accepted.Length > 0 &&
            !accepted.Contains(culture, StringComparer.OrdinalIgnoreCase));
        names.RemoveAll(name => GenericTags.TryGetValue(name, out var accepted) && accepted.Length > 0 &&
            !accepted.Contains(tag, StringComparer.OrdinalIgnoreCase));
        return ApplyContextRules(campaign, nation, names);
    }

    public static string CivilWarParentTag(Nation nation)
    {
        if (nation is null || !string.IsNullOrEmpty(nation.FormedNationId) ||
            !nation.Name.StartsWith("rising:", StringComparison.Ordinal)) return "";
        var parts = nation.Name.Split(':', 4);
        return parts.Length >= 3 ? Slug(parts[2]) : "";
    }

    /// <summary>
    /// Whether a country's own flag is still the flag of what that country is.
    ///
    /// True for every country that has not declared otherwise, which is nearly all of them:
    /// a tricolour is a tricolour whoever is governing. False for the ones whose flag was
    /// adopted BY a regime and means it - and those are the countries where falling back to
    /// the shipped flag is not a safe default but a wrong answer.
    /// </summary>
    public static bool Fits(Campaign campaign, Nation nation, string tag)
    {
        if (!Native.TryGetValue(tag, out var conditions)) return true;
        foreach (var (source, accepted) in conditions)
        {
            var now = ValueOf(source, campaign, nation);
            var chain = Chain(source, now);
            // Held if it matches one of the values, or if what it is now is something that
            // answers to one - a socialist China is near enough to the flag a communist one flies.
            var held = false;
            foreach (var value in accepted.Split(AnyOf))
                if (string.Equals(now, value, StringComparison.OrdinalIgnoreCase) ||
                    chain.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    held = true;
                    break;
                }
            if (!held) return false;
        }
        return true;
    }

    /// <summary>
    /// Every value one qualifier will answer to, the real one first.
    ///
    /// Only ever values of the same source: what a government may stand in for is another
    /// government, never an ideology. A one-party state can be communist and can just as
    /// easily be fascist, so the form of government is not evidence about the banner - the
    /// two are separate parts of the name and the search puts them together itself.
    /// </summary>
    public static List<string> Chain(string source, string value)
    {
        var chain = new List<string> { value };
        if (!Aliases.TryGetValue(source ?? "", out var table)) return chain;
        if (!table.TryGetValue(value, out var also)) return chain;
        foreach (var alias in also)
            if (!chain.Contains(alias, StringComparer.OrdinalIgnoreCase)) chain.Add(alias);
        return chain;
    }

    /// <summary>
    /// The names one family answers to: `flag_&lt;source&gt;_&lt;value&gt;`, and that stem
    /// carrying the country's live qualifiers as well.
    ///
    /// A family takes the qualifiers for the same reason a country does: a Kurdish state
    /// that has gone communist is not flying what a Kurdish monarchy flies, and a mechanic
    /// that produces new countries constantly is exactly where that variety is worth having.
    /// `flag_culture_kurdish_communism` beats `flag_culture_kurdish`, which beats nothing.
    ///
    /// Used for both rungs - the identities above a country's own flag and the fallbacks
    /// below it - so the two cannot drift apart.
    /// </summary>
    private static void Family(List<string> names, string source, Campaign campaign, Nation nation,
        List<(FlagQualifier Qualifier, List<string> Chain)> live, int combined)
    {
        var family = ValueOf(source, campaign, nation);
        if (family.Length == 0) return;
        var stem = $"flag_{Slug(source)}_{family}";

        // Not the qualifier this family already is: `flag_ideology_communism` should not
        // spawn a search for `flag_ideology_communism_communism`.
        var skip = 0;
        for (var index = 0; index < combined; index++)
            if (string.Equals(live[index].Qualifier.Source, source, StringComparison.OrdinalIgnoreCase))
                skip |= 1 << index;

        for (var size = combined; size >= 1; size--)
            for (var mask = 0; mask < 1 << combined; mask++)
            {
                if ((mask & skip) != 0) continue;
                if (System.Numerics.BitOperations.PopCount((uint)mask) != size) continue;
                Spread(names, stem, live, combined, mask, 0, int.MaxValue);
            }
        names.Add(stem);
    }

    private static void Spread(List<string> into, string stem,
        List<(FlagQualifier Qualifier, List<string> Chain)> live, int combined, int mask,
        int nearest, int furthest)
    {
        var chosen = new List<int>();
        for (var index = 0; index < combined; index++)
            if ((mask & (1 << index)) != 0) chosen.Add(index);
        if (chosen.Count == 0) return;

        var longest = 0;
        foreach (var index in chosen) longest = Math.Max(longest, live[index].Chain.Count);

        var limit = Math.Min(furthest, (longest - 1) * chosen.Count);
        for (var distance = nearest; distance <= limit; distance++)
            Walk(into, stem, live, chosen, 0, distance, "");
    }

    /// <summary>One mixture, built left to right, spending exactly `budget` steps.</summary>
    private static void Walk(List<string> into, string stem,
        List<(FlagQualifier Qualifier, List<string> Chain)> live, List<int> chosen,
        int at, int budget, string name)
    {
        if (at == chosen.Count)
        {
            if (budget == 0) into.Add(stem + name);
            return;
        }
        var chain = live[chosen[at]].Chain;
        for (var step = 0; step < chain.Count && step <= budget; step++)
            Walk(into, stem, live, chosen, at + 1, budget - step, name + "_" + chain[step]);
    }

    /// <summary>
    /// The first candidate the art actually has, or an empty string.
    ///
    /// Here rather than in the interface so that the bench can ask the same question the
    /// game asks, which is the only way a check on flags means anything.
    /// </summary>
    public static string Resolve(Campaign campaign, Nation nation)
    {
        foreach (var name in Candidates(campaign, nation))
            if (SpriteCatalog.Has(name)) return name;
        return "";
    }

    /// <summary>Exactly the normal resolver, applied to a non-mutating political projection.</summary>
    public static string ResolvePreview(Campaign campaign, Nation nation, string government = "", string ideology = "") =>
        nation is null ? "" : Resolve(campaign, nation.FlagPoliticsPreview(government, ideology));

    public static string ResolveFormationPreview(Campaign campaign, Nation nation, string formableId)
    {
        if (nation is null) return "";
        var preview = nation.FlagPoliticsPreview("", "");
        preview.FormedNationId = formableId;
        return Resolve(campaign, preview);
    }

    /// <summary>
    /// A value as it is spelled in a file name: lower case, and anything that is not a
    /// letter or a digit becomes an underscore. So "Middle East" is `middle_east` and a
    /// mod does not have to guess.
    /// </summary>
    public static string Slug(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        var lastWasBreak = true;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
                lastWasBreak = false;
                continue;
            }
            if (lastWasBreak) continue;
            buffer[length++] = '_';
            lastWasBreak = true;
        }
        while (length > 0 && buffer[length - 1] == '_') length--;
        return new string(buffer[..length]);
    }
}
