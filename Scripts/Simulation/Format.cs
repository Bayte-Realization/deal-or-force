using System;
using System.Globalization;

namespace DealOrForce.Simulation;

/// <summary>
/// How figures are written for a reader rather than for a debugger.
///
/// The United States' economy is 21 433.2 billion dollars. Printed that way it is nine
/// glyphs the eye has to count before it means anything; printed as $21.43T it is a
/// quantity. A HUD is scanned, not read, so everything in it is compact - and every
/// compact figure carries the exact one in its tooltip, because the moment a player
/// wants the real number they want all of it.
///
/// In the reader's own language. The digits follow the language's culture (Loc.Culture) and
/// the units its table ("fmt.money.B" and the rest): a Russian top bar read "$131.1B" and
/// "69.6%" beside "1,8 %", where a Russian reader writes "$131,1 млрд" and "69,6 %". English
/// reads exactly as it always has; a table that names no unit falls back to the English one.
/// </summary>
public static class Fmt
{
    private static CultureInfo Digits => Loc.Culture;

    /// <summary>The language's way of writing a figure with a unit, "{0}" standing for the digits.</summary>
    private static string Unit(string key, string english, string digits)
    {
        var template = Loc.Get(key);
        if (string.IsNullOrEmpty(template) || template == key || !template.Contains("{0}")) template = english;
        return template.Replace("{0}", digits);
    }

    /// <summary>A sum given in billions of dollars, at the largest unit that keeps it short.</summary>
    public static string Money(float billions)
    {
        // Three or four significant figures at every scale: $21.43T and $176.8B carry the
        // same amount of information, and $176.76B carries two digits nobody reads.
        // A debt reads as one: −$1.29B, signed the way MoneyDelta signs a loss, not $-1.29B -
        // the treasury said the second while the weekly balance beside it said the first.
        if (billions < -0.0000005f) return "−" + Money(-billions);
        var magnitude = MathF.Abs(billions);
        if (magnitude >= 1000f) return Unit("fmt.money.T", "${0}T", (billions / 1000f).ToString("0.##", Digits));
        if (magnitude >= 100f) return Unit("fmt.money.B", "${0}B", billions.ToString("0.#", Digits));
        if (magnitude >= 1f) return Unit("fmt.money.B", "${0}B", billions.ToString("0.##", Digits));
        if (magnitude >= 0.1f) return Unit("fmt.money.M", "${0}M", (billions * 1000f).ToString("0", Digits));
        if (magnitude >= 0.001f) return Unit("fmt.money.M", "${0}M", (billions * 1000f).ToString("0.#", Digits));
        if (magnitude <= 0.0000005f) return "$0";
        return Unit("fmt.money.k", "${0}k", (billions * 1_000_000f).ToString("0.#", Digits));
    }

    /// <summary>The same sum written out in full, for a tooltip or a ledger row.</summary>
    public static string MoneyExact(float billions) =>
        (billions < 0f ? "−" : "") + Unit("fmt.money.B", "${0}B", MathF.Abs(billions).ToString("N2", Digits));

    /// <summary>A head count, at the largest unit that keeps it short.</summary>
    public static string People(double count)
    {
        var magnitude = Math.Abs(count);
        if (magnitude >= 1_000_000_000d) return Unit("fmt.people.B", "{0}B", (count / 1_000_000_000d).ToString("0.##", Digits));
        if (magnitude >= 1_000_000d) return Unit("fmt.people.M", "{0}M", (count / 1_000_000d).ToString("0.#", Digits));
        if (magnitude >= 10_000d) return Unit("fmt.people.k", "{0}k", (count / 1000d).ToString("0", Digits));
        if (magnitude >= 1000d) return Unit("fmt.people.k", "{0}k", (count / 1000d).ToString("0.#", Digits));
        return count.ToString("N0", Digits);
    }

    public static string PeopleExact(double count) => count.ToString("N0", Digits);

    /// <summary>
    /// A change, with its own sign always shown. A delta that reads "+0" when it is really
    /// a rounded-down rise is a lie the player will notice, so anything smaller than the
    /// smallest displayable step comes back as an em dash instead.
    /// </summary>
    public static string MoneyDelta(float billions)
    {
        if (MathF.Abs(billions) < 0.005f) return "—";
        return (billions > 0f ? "+" : "−") + Money(MathF.Abs(billions));
    }

    public static string PeopleDelta(double count)
    {
        if (Math.Abs(count) < 1d) return "—";
        return (count > 0d ? "+" : "−") + People(Math.Abs(count));
    }

    /// <summary>
    /// A plain number, in the reader's language like every other figure on screen. Without
    /// this the interface mixed "1.09" and "1,09" depending on the operating system's locale,
    /// in the middle of an English sentence.
    /// </summary>
    public static string Number(float value, string format = "0.00") =>
        value.ToString(format, Digits);

    /// <summary>A share of one, as whole percent.</summary>
    public static string Percent(float share) => Unit("fmt.percent", "{0}%", (share * 100f).ToString("0.#", Digits));

    /// <summary>A change in a share, in percentage points, sign always shown.</summary>
    public static string PercentDelta(float share)
    {
        var points = share * 100f;
        if (MathF.Abs(points) < 0.05f) return "—";
        return (points > 0f ? "+" : "−") + Unit("fmt.points", "{0} pp", MathF.Abs(points).ToString("0.#", Digits));
    }

    /// <summary>-1, 0 or +1, for choosing which of the semantic colours a delta is drawn in.</summary>
    public static int Direction(double delta, double deadband) =>
        delta > deadband ? 1 : delta < -deadband ? -1 : 0;
}
