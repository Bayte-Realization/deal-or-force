namespace DealOrForce.Simulation;

/// <summary>
/// Which of a language's plural forms a count takes.
///
/// English has two - one division, two divisions - and the interface was written as though
/// that were a property of counting rather than a property of English: a literal "s" stitched
/// onto the end of a word when the number was not one. Russian has three, and picks between
/// them by the last digit and the last two digits: 1 дивизия, 2 дивизии, 5 дивизий, 21
/// дивизия, 111 дивизий. No amount of care in the translation can fix a sentence whose plural
/// was decided in the English source.
///
/// So the count decides the KEY, and the table decides the words. "{0} divisions" is looked up
/// as three separate rows - the bare key, the key with "|few", the key with "|many" - and each
/// language fills in as many of them as it has forms. A language with one form fills one row
/// and the other two are never asked for.
///
/// Which rule a language uses is declared by the language, in its own table, under
/// <c>language.plural</c> - beside <c>language.name</c>, which is already the place a table
/// says things about itself rather than about the game. A mod adding Polish writes
/// <c>"language.plural": "slavic"</c> and needs no code.
/// </summary>
public static class Plural
{
    /// <summary>The three forms any of the supported rules can ask for.</summary>
    public enum Form { One, Few, Many }

    /// <summary>The key a table row carries for a count, given the singular key.</summary>
    public static string Key(string singular, long count) => Of(count) switch
    {
        Form.One => singular,
        Form.Few => singular + "|few",
        _ => singular + "|many"
    };

    /// <summary>The same, for the common case of a plain <see cref="int"/> count.</summary>
    public static string Key(string singular, int count) => Key(singular, (long)count);

    /// <summary>Which form this count takes in the language currently loaded.</summary>
    public static Form Of(long count)
    {
        var magnitude = count < 0 ? -count : count;
        return Loc.GetOr("language.plural", "one-other").Trim().ToLowerInvariant() switch
        {
            // Russian, Ukrainian, Polish, Czech and their neighbours. The eleven-to-fourteen
            // exception is the whole reason this cannot be done with a single modulo: 21 is
            // one, 11 is many, and both end in a 1.
            "slavic" => Slavic(magnitude),
            // Japanese, Chinese, Korean, Turkish: the noun does not change at all.
            "single" => Form.One,
            _ => magnitude == 1 ? Form.One : Form.Many
        };
    }

    private static Form Slavic(long magnitude)
    {
        var last = magnitude % 10;
        var pair = magnitude % 100;
        if (last == 1 && pair != 11) return Form.One;
        if (last >= 2 && last <= 4 && (pair < 12 || pair > 14)) return Form.Few;
        return Form.Many;
    }
}
