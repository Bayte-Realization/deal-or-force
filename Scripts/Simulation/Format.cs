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
/// </summary>
public static class Fmt
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A sum given in billions of dollars, at the largest unit that keeps it short.</summary>
    public static string Money(float billions)
    {
        // Three or four significant figures at every scale: $21.43T and $176.8B carry the
        // same amount of information, and $176.76B carries two digits nobody reads.
        // A debt reads as one: −$1.29B, signed the way MoneyDelta signs a loss, not $-1.29B -
        // the treasury said the second while the weekly balance beside it said the first.
        if (billions < -0.0000005f) return "−" + Money(-billions);
        var magnitude = MathF.Abs(billions);
        if (magnitude >= 1000f) return "$" + (billions / 1000f).ToString("0.##", Invariant) + "T";
        if (magnitude >= 100f) return "$" + billions.ToString("0.#", Invariant) + "B";
        if (magnitude >= 1f) return "$" + billions.ToString("0.##", Invariant) + "B";
        if (magnitude >= 0.1f) return "$" + (billions * 1000f).ToString("0", Invariant) + "M";
        if (magnitude >= 0.001f) return "$" + (billions * 1000f).ToString("0.#", Invariant) + "M";
        if (magnitude <= 0.0000005f) return "$0";
        return "$" + (billions * 1_000_000f).ToString("0.#", Invariant) + "k";
    }

    /// <summary>The same sum written out in full, for a tooltip or a ledger row.</summary>
    public static string MoneyExact(float billions) =>
        (billions < 0f ? "−$" : "$") + MathF.Abs(billions).ToString("N2", CultureInfo.CurrentCulture) + "B";

    /// <summary>A head count, at the largest unit that keeps it short.</summary>
    public static string People(double count)
    {
        var magnitude = Math.Abs(count);
        if (magnitude >= 1_000_000_000d) return (count / 1_000_000_000d).ToString("0.##", Invariant) + "B";
        if (magnitude >= 1_000_000d) return (count / 1_000_000d).ToString("0.#", Invariant) + "M";
        if (magnitude >= 10_000d) return (count / 1000d).ToString("0", Invariant) + "k";
        if (magnitude >= 1000d) return (count / 1000d).ToString("0.#", Invariant) + "k";
        return count.ToString("N0", CultureInfo.CurrentCulture);
    }

    public static string PeopleExact(double count) => count.ToString("N0", CultureInfo.CurrentCulture);

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
    /// A plain number, in the same invariant form as every other figure on screen. Without
    /// this the interface mixes "1.09" and "1,09" depending on the operating system's
    /// locale, in the middle of an English sentence.
    /// </summary>
    public static string Number(float value, string format = "0.00") =>
        value.ToString(format, Invariant);

    /// <summary>A share of one, as whole percent.</summary>
    public static string Percent(float share) => (share * 100f).ToString("0.#", Invariant) + "%";

    /// <summary>A change in a share, in percentage points, sign always shown.</summary>
    public static string PercentDelta(float share)
    {
        var points = share * 100f;
        if (MathF.Abs(points) < 0.05f) return "—";
        return (points > 0f ? "+" : "−") + MathF.Abs(points).ToString("0.#", Invariant) + " pp";
    }

    /// <summary>-1, 0 or +1, for choosing which of the semantic colours a delta is drawn in.</summary>
    public static int Direction(double delta, double deadband) =>
        delta > deadband ? 1 : delta < -deadband ? -1 : 0;
}
