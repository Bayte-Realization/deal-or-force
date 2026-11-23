using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// Presentation calendar attached to a scenario or total-conversion campaign.
///
/// Internal simulation time remains proleptic Gregorian (<see cref="GameDate"/> and
/// <see cref="Campaign.ElapsedHours"/>) so timers, seasons, leap-year math, saves and
/// multiplayer lockstep never drift. This profile only governs how a date is formatted
/// for players: custom era names ("Age of Iron", "Third Sun"), year offsets, custom
/// 12-month names, and custom format templates.
///
/// A default/empty profile produces the exact legacy Gregorian formatting and emits
/// zero bytes into base-2024 saves and fingerprints.
/// </summary>
public sealed class CalendarProfile
{
    public const int RequiredMonthCount = 12;

    private static readonly string[] DefaultMonthNames =
    {
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December"
    };

    [JsonPropertyName("id")]
    public string CalendarId { get; set; } = "";

    [JsonPropertyName("era_name")]
    public string EraName { get; set; } = "";

    [JsonPropertyName("era_short")]
    public string EraShort { get; set; } = "";

    /// <summary>
    /// Offset added to the internal astronomical year for display.
    /// Zero means the internal year is displayed directly (with BCE handling for year <= 0
    /// when no custom era is specified).
    /// </summary>
    [JsonPropertyName("year_offset")]
    public int YearOffset { get; set; }

    /// <summary>
    /// Optional date format template supporting {day}, {month}, {year}, {era}, {era_short}.
    /// Example: "{day} {month}, Year {year} of the {era}" or "{day} {month} {year} {era_short}".
    /// </summary>
    [JsonPropertyName("format")]
    public string DateFormat { get; set; } = "";

    /// <summary>
    /// Optional year-only format template supporting {year}, {era}, {era_short}.
    /// Example: "Year {year} of the {era}".
    /// </summary>
    [JsonPropertyName("year_format")]
    public string YearFormat { get; set; } = "";

    /// <summary>Optional 12 custom month names (1 = first month .. 12 = twelfth month).</summary>
    [JsonPropertyName("month_names")]
    public string[] MonthNames { get; set; }

    [JsonPropertyName("show_era_in_date")]
    public bool ShowEraInDate { get; set; } = true;

    [JsonIgnore]
    public bool IsDefault =>
        string.IsNullOrWhiteSpace(CalendarId) &&
        string.IsNullOrWhiteSpace(EraName) &&
        string.IsNullOrWhiteSpace(EraShort) &&
        YearOffset == 0 &&
        string.IsNullOrWhiteSpace(DateFormat) &&
        string.IsNullOrWhiteSpace(YearFormat) &&
        (MonthNames is null || MonthNames.Length == 0);

    public CalendarProfile Clone() => new()
    {
        CalendarId = CalendarId ?? "",
        EraName = EraName ?? "",
        EraShort = EraShort ?? "",
        YearOffset = YearOffset,
        DateFormat = DateFormat ?? "",
        YearFormat = YearFormat ?? "",
        MonthNames = MonthNames is { Length: > 0 } ? (string[])MonthNames.Clone() : null,
        ShowEraInDate = ShowEraInDate
    };

    public string FormatMonth(int month, bool localized = true)
    {
        var index = Math.Clamp(month, 1, RequiredMonthCount) - 1;
        var raw = MonthNames is { Length: RequiredMonthCount } && !string.IsNullOrWhiteSpace(MonthNames[index])
            ? MonthNames[index]
            : DefaultMonthNames[index];
        return localized ? Loc.Get(raw) : raw;
    }

    public string FormatYear(int astronomicalYear, bool localized = true)
    {
        var era = localized && !string.IsNullOrWhiteSpace(EraName) ? Loc.Get(EraName) : (EraName ?? "");
        var eraShort = localized && !string.IsNullOrWhiteSpace(EraShort) ? Loc.Get(EraShort) : (EraShort ?? "");

        if (IsDefault)
        {
            return astronomicalYear <= 0
                ? (localized ? Loc.Format("{0} BCE", 1L - astronomicalYear) : $"{1L - astronomicalYear} BCE")
                : astronomicalYear.ToString(CultureInfo.InvariantCulture);
        }

        var displayYear = (long)astronomicalYear + YearOffset;
        var yearText = displayYear <= 0 && string.IsNullOrWhiteSpace(era) && string.IsNullOrWhiteSpace(eraShort)
            ? (localized ? Loc.Format("{0} BCE", 1L - displayYear) : $"{1L - displayYear} BCE")
            : displayYear.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(YearFormat))
        {
            var template = localized ? Loc.Get(YearFormat) : YearFormat;
            return ApplyTokens(template, day: 0, month: "", year: yearText, era: era, eraShort: eraShort).Trim();
        }

        if (ShowEraInDate && !string.IsNullOrWhiteSpace(eraShort))
            return $"{yearText} {eraShort}";
        if (ShowEraInDate && !string.IsNullOrWhiteSpace(era))
            return $"{yearText} ({era})";
        return yearText;
    }

    public string FormatDate(GameDate date, bool withTime = false, bool localized = true)
    {
        if (IsDefault)
            return localized ? date.Display(withTime) : (date.Hour == 0 && !withTime ? date.ToString() : $"{date}, {date.Hour:00}:00");

        var monthText = FormatMonth(date.Month, localized);
        var era = localized && !string.IsNullOrWhiteSpace(EraName) ? Loc.Get(EraName) : (EraName ?? "");
        var eraShort = localized && !string.IsNullOrWhiteSpace(EraShort) ? Loc.Get(EraShort) : (EraShort ?? "");
        var displayYear = (long)date.Year + YearOffset;
        var rawYear = displayYear <= 0 && string.IsNullOrWhiteSpace(era) && string.IsNullOrWhiteSpace(eraShort)
            ? (localized ? Loc.Format("{0} BCE", 1L - displayYear) : $"{1L - displayYear} BCE")
            : displayYear.ToString(CultureInfo.InvariantCulture);

        string body;
        if (!string.IsNullOrWhiteSpace(DateFormat))
        {
            var template = localized ? Loc.Get(DateFormat) : DateFormat;
            body = ApplyTokens(template, date.Day, monthText, rawYear, era, eraShort).Trim();
        }
        else
        {
            body = $"{date.Day} {monthText} {FormatYear(date.Year, localized)}";
        }

        return date.Hour == 0 && !withTime ? body : $"{body}, {date.Hour:00}:00";
    }

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (MonthNames is { Length: > 0 })
        {
            if (MonthNames.Length != RequiredMonthCount)
                problems.Add($"calendar.month_names must contain exactly {RequiredMonthCount} entries (found {MonthNames.Length}).");
            else
            {
                for (var i = 0; i < MonthNames.Length; i++)
                    if (string.IsNullOrWhiteSpace(MonthNames[i]))
                        problems.Add($"calendar.month_names[{i}] is empty.");
            }
        }
        if (YearOffset < -100000 || YearOffset > 100000)
            problems.Add($"calendar.year_offset ({YearOffset}) is outside supported range [-100000, 100000].");
        return problems;
    }

    public bool EqualsProfile(CalendarProfile other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return IsDefault;
        if (!string.Equals(CalendarId ?? "", other.CalendarId ?? "", StringComparison.Ordinal) ||
            !string.Equals(EraName ?? "", other.EraName ?? "", StringComparison.Ordinal) ||
            !string.Equals(EraShort ?? "", other.EraShort ?? "", StringComparison.Ordinal) ||
            YearOffset != other.YearOffset ||
            !string.Equals(DateFormat ?? "", other.DateFormat ?? "", StringComparison.Ordinal) ||
            !string.Equals(YearFormat ?? "", other.YearFormat ?? "", StringComparison.Ordinal) ||
            ShowEraInDate != other.ShowEraInDate)
            return false;

        var leftMonths = MonthNames ?? Array.Empty<string>();
        var rightMonths = other.MonthNames ?? Array.Empty<string>();
        if (leftMonths.Length != rightMonths.Length) return false;
        for (var i = 0; i < leftMonths.Length; i++)
            if (!string.Equals(leftMonths[i] ?? "", rightMonths[i] ?? "", StringComparison.Ordinal))
                return false;
        return true;
    }

    private static string ApplyTokens(string template, int day, string month, string year, string era, string eraShort)
    {
        var result = template
            .Replace("{day}", day > 0 ? day.ToString(CultureInfo.InvariantCulture) : "", StringComparison.OrdinalIgnoreCase)
            .Replace("{month}", month ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", year ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{era_short}", eraShort ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{era}", era ?? "", StringComparison.OrdinalIgnoreCase);
        while (result.Contains("  ", StringComparison.Ordinal))
            result = result.Replace("  ", " ", StringComparison.Ordinal);
        return result;
    }
}
