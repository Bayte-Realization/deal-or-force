using System;
using System.Globalization;

namespace DealOrForce.Simulation;

/// <summary>
/// A civil date for content, including dates before the common era. The campaign clock is
/// an int year, so a mod that opens in 300 BCE must not be forced through <see cref="DateOnly"/>,
/// whose range starts at year 1. The arithmetic is the proleptic Gregorian calendar and is
/// deliberately small: content needs ordering and a day window, not time zones.
/// </summary>
public readonly record struct HistoricalDate(int Year, int Month, int Day)
{
    /// <summary>Days since 1970-01-01, valid for the whole int year range this game uses.</summary>
    public int DayNumber
    {
        get
        {
            var year = Year - (Month <= 2 ? 1 : 0);
            var era = (year >= 0 ? year : year - 399) / 400;
            var yearOfEra = year - era * 400;
            var dayOfYear = (153 * (Month + (Month > 2 ? -3 : 9)) + 2) / 5 + Day - 1;
            var dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
            return era * 146097 + dayOfEra - 719468;
        }
    }

    public static bool IsLeapYear(int year) =>
        year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    public static int DaysInMonth(int year, int month) => month switch
    {
        1 or 3 or 5 or 7 or 8 or 10 or 12 => 31,
        4 or 6 or 9 or 11 => 30,
        2 => IsLeapYear(year) ? 29 : 28,
        _ => 0
    };

    /// <summary>
    /// Parse `YYYY-MM-DD`, or `-YYYY-MM-DD` for a year before the common era. A year is
    /// astronomical here: `-0300` is 301 BCE, which is the ordering content needs and the
    /// convention every proleptic calendar uses.
    /// </summary>
    public static bool TryParse(string text, out HistoricalDate date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var negative = text.StartsWith("-", StringComparison.Ordinal);
        if (negative) text = text[1..];
        var parts = text.Split('-');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            return false;
        if (negative) year = -year;
        return TryCreate(year, month, day, out date);
    }

    public static bool TryCreate(int year, int month, int day, out HistoricalDate date)
    {
        date = default;
        if (year < -9999 || year > 9999 || month < 1 || month > 12 ||
            day < 1 || day > DaysInMonth(year, month)) return false;
        date = new HistoricalDate(year, month, day);
        return true;
    }

    public override string ToString() =>
        $"{(Year < 0 ? "-" : "")}{Math.Abs(Year):0000}-{Month:00}-{Day:00}";
}
