using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

// Save rows: the records a save holds tens of thousands of, written as rows of values rather
// than as objects that repeat every field's name for every record. Reading takes either form,
// so every save written before them still loads. SimBench's "a save row carries every field"
// checks each of them against the type it writes, so a field added to one of these types
// without being added here fails the bench instead of silently not being saved.

/// <summary>
/// A week of a country's history as a row of numbers rather than an object.
///
/// The history is most of a save: twenty-two figures a week for every country, fifteen years
/// deep. Written as objects, every one of those figures carried its own name -
/// "IndustrialPriceIndex" sixty-eight thousand times over in a five-year save - which made the
/// history thirty-five of the file's fifty-nine megabytes, and made writing the file the
/// half-second freeze that came back every month and grew every year. As a row it is the same
/// numbers, in the order the record declares them, at well under half the size.
///
/// Reading takes either form, so every save written before this still loads.
/// </summary>
public sealed class NationHistorySampleConverter : JsonConverter<NationHistorySample>
{
    public override NationHistorySample Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.StartArray => ReadRow(ref reader),
            JsonTokenType.StartObject => ReadObject(ref reader),
            _ => throw new JsonException("A history sample is a row of numbers or an object.")
        };

    private static NationHistorySample ReadRow(ref Utf8JsonReader reader)
    {
        Span<float> values = stackalloc float[21];
        values.Clear();
        var day = 0;
        var index = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number) throw new JsonException("A history row holds numbers only.");
            if (index == 0) day = reader.GetInt32();
            else if (index <= values.Length) values[index - 1] = reader.GetSingle();
            index++;
        }
        if (reader.TokenType != JsonTokenType.EndArray || index != values.Length + 1)
            throw new JsonException("A history row must contain its day and twenty-one figures.");
        return Build(day, values);
    }

    private static NationHistorySample ReadObject(ref Utf8JsonReader reader)
    {
        Span<float> values = stackalloc float[21];
        values.Clear();
        var day = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType != JsonTokenType.Number)
            {
                reader.Skip();
                continue;
            }
            if (string.Equals(name, "Day", StringComparison.OrdinalIgnoreCase))
            {
                day = reader.GetInt32();
                continue;
            }
            var slot = Array.FindIndex(Fields, field => string.Equals(field, name, StringComparison.OrdinalIgnoreCase));
            if (slot >= 0) values[slot] = reader.GetSingle();
        }
        return Build(day, values);
    }

    public override void Write(Utf8JsonWriter writer, NationHistorySample sample, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(sample.Day);
        SaveNumbers.Write(writer, sample.Gdp, options);
        SaveNumbers.Write(writer, sample.Treasury, options);
        SaveNumbers.Write(writer, sample.PopulationMillions, options);
        SaveNumbers.Write(writer, sample.WeeklyIncome, options);
        SaveNumbers.Write(writer, sample.WeeklyExpenses, options);
        SaveNumbers.Write(writer, sample.WeeklyBalance, options);
        SaveNumbers.Write(writer, sample.Productivity, options);
        SaveNumbers.Write(writer, sample.StandardOfLiving, options);
        SaveNumbers.Write(writer, sample.Literacy, options);
        SaveNumbers.Write(writer, sample.Stability, options);
        SaveNumbers.Write(writer, sample.MarketAccess, options);
        SaveNumbers.Write(writer, sample.ShortageIndex, options);
        SaveNumbers.Write(writer, sample.TradeBalance, options);
        SaveNumbers.Write(writer, sample.MilitaryPower, options);
        SaveNumbers.Write(writer, sample.ManpowerMillions, options);
        SaveNumbers.Write(writer, sample.WarSupport, options);
        SaveNumbers.Write(writer, sample.FoodPriceIndex, options);
        SaveNumbers.Write(writer, sample.ConsumerPriceIndex, options);
        SaveNumbers.Write(writer, sample.IndustrialPriceIndex, options);
        SaveNumbers.Write(writer, sample.EnergyPriceIndex, options);
        SaveNumbers.Write(writer, sample.ArmsPriceIndex, options);
        writer.WriteEndArray();
    }

    /// <summary>The figures after the day, in the order the record declares them and a row carries them.</summary>
    private static readonly string[] Fields =
    {
        "Gdp", "Treasury", "PopulationMillions", "WeeklyIncome", "WeeklyExpenses", "WeeklyBalance",
        "Productivity", "StandardOfLiving", "Literacy", "Stability", "MarketAccess", "ShortageIndex",
        "TradeBalance", "MilitaryPower", "ManpowerMillions", "WarSupport", "FoodPriceIndex",
        "ConsumerPriceIndex", "IndustrialPriceIndex", "EnergyPriceIndex", "ArmsPriceIndex"
    };

    private static NationHistorySample Build(int day, ReadOnlySpan<float> v) => new(
        day, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14],
        v[15], v[16], v[17], v[18], v[19], v[20]);
}

/// <summary>
/// The catalogue ids a save's population rows refer to by number.
///
/// A row carries the index into this table instead of three strings, and the table is
/// written once at the front of the file. Reading accepts both forms: an index when the
/// table is present, and the literal id every save written before this one carries.
/// </summary>
public static class SaveStringTable
{
    private static string[] _table = Array.Empty<string>();
    private static readonly System.Collections.Generic.Dictionary<string, int> Index =
        new(StringComparer.Ordinal);

    public static void Use(string[] table)
    {
        _table = table ?? Array.Empty<string>();
        Index.Clear();
        for (var i = 0; i < _table.Length; i++) Index[_table[i] ?? ""] = i;
    }

    public static string[] Build(System.Collections.Generic.IEnumerable<string> values)
    {
        var set = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal) { "" };
        foreach (var value in values) if (!string.IsNullOrEmpty(value)) set.Add(value);
        return System.Linq.Enumerable.ToArray(set);
    }

    public static int IndexOf(string value)
    {
        if (!InternIds || _table.Length == 0 || string.IsNullOrEmpty(value)) return -1;
        return Index.TryGetValue(value, out var index) ? index : -1;
    }

    /// <summary>Text saves remain readable; the compact table is for the binary save path.</summary>
    public static bool InternIds { get; set; } = true;

    public static string At(int index) =>
        index >= 0 && index < _table.Length ? _table[index] ?? "" : "";
}

/// <summary>
/// One people in one region as a row: class, sector, culture, religion, profession, size,
/// employment, wealth, standard of living, radicalism - in that order.
///
/// After the history, the peoples were the rest of a save: some eighty thousand groups, each
/// naming its ten fields, sixteen of a five-year save's thirty-seven megabytes.
/// </summary>
public sealed class PopulationGroupConverter : JsonConverter<PopulationGroup>
{
    public override PopulationGroup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var group = new PopulationGroup();
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (index++)
                {
                    case 0: group.Class = (SocialClass)reader.GetInt32(); break;
                    case 1: group.Sector = (Sector)reader.GetInt32(); break;
                    case 2: group.Culture = ReadId(ref reader); break;
                    case 3: group.Religion = ReadId(ref reader); break;
                    case 4: group.Profession = ReadId(ref reader); break;
                    case 5: group.Size = reader.GetInt64(); break;
                    case 6: group.Employment = reader.GetSingle(); break;
                    case 7: group.Wealth = reader.GetSingle(); break;
                    case 8: group.StandardOfLiving = reader.GetSingle(); break;
                    case 9: group.Radicalism = reader.GetSingle(); break;
                    default: reader.Skip(); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndArray || index != 10)
                throw new JsonException("A population row must contain ten values.");
            return group;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A people is a row of values or an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = (reader.GetString() ?? "").ToLowerInvariant();
            reader.Read();
            switch (name)
            {
                case "class": group.Class = (SocialClass)ReadEnum(ref reader); break;
                case "sector": group.Sector = (Sector)ReadEnum(ref reader); break;
                case "culture": group.Culture = ReadId(ref reader); break;
                case "religion": group.Religion = ReadId(ref reader); break;
                case "profession": group.Profession = ReadId(ref reader); break;
                case "size": group.Size = reader.GetInt64(); break;
                case "employment": group.Employment = reader.GetSingle(); break;
                case "wealth": group.Wealth = reader.GetSingle(); break;
                case "standardofliving": group.StandardOfLiving = reader.GetSingle(); break;
                case "radicalism": group.Radicalism = reader.GetSingle(); break;
                default: reader.Skip(); break;
            }
        }
        return group;
    }

    /// <summary>An id as an index into the save's table, or the literal text of an older row.</summary>
    private static string ReadId(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number ? SaveStringTable.At(reader.GetInt32())
        : reader.GetString() ?? "";

    /// <summary>A class or sector as the old object form wrote it: its number, or - from a writer that named enums - its name.</summary>
    private static int ReadEnum(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.String
            ? Enum.TryParse<SocialClass>(reader.GetString(), true, out var rung) ? (int)rung
            : Enum.TryParse<Sector>(reader.GetString(), true, out var sector) ? (int)sector : 0
            : reader.GetInt32();

    public override void Write(Utf8JsonWriter writer, PopulationGroup group, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue((int)group.Class);
        writer.WriteNumberValue((int)group.Sector);
        WriteId(writer, group.Culture);
        WriteId(writer, group.Religion);
        WriteId(writer, group.Profession);
        writer.WriteNumberValue(group.Size);
        SaveNumbers.Write(writer, group.Employment, options);
        SaveNumbers.Write(writer, group.Wealth, options);
        SaveNumbers.Write(writer, group.StandardOfLiving, options);
        SaveNumbers.Write(writer, group.Radicalism, options);
        writer.WriteEndArray();
    }

    private static void WriteId(Utf8JsonWriter writer, string value)
    {
        var index = SaveStringTable.IndexOf(value);
        if (index >= 0) writer.WriteNumberValue(index);
        else writer.WriteStringValue(value ?? "");
    }
}

/// <summary>Ordinary saves stay strict; only the separate diagnostic writer opts into named values.</summary>
internal static class SaveNumbers
{
    internal static void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
    {
        if (!float.IsFinite(value) && (options.NumberHandling & JsonNumberHandling.AllowNamedFloatingPointLiterals) != 0)
            writer.WriteStringValue(float.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");
        else writer.WriteNumberValue(value);
    }
}

/// <summary>
/// A public image as a row of ten values: Esteem, Incident, Familiarity, Entrenchment, Target,
/// LastReviewDay, ConsistentDays, Status, LastReason, LastIncidentDay.
/// Trailing default fields are omitted when writing and restored when reading; legacy object form is also read.
/// </summary>
public sealed class PublicImageConverter : JsonConverter<PublicImage>
{
    public override PublicImage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var image = new PublicImage();
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (index++)
                {
                    case 0: image.Esteem = reader.GetSingle(); break;
                    case 1: image.Incident = reader.GetSingle(); break;
                    case 2: image.Familiarity = reader.GetSingle(); break;
                    case 3: image.Entrenchment = reader.GetSingle(); break;
                    case 4: image.Target = reader.GetSingle(); break;
                    case 5: image.LastReviewDay = reader.GetInt32(); break;
                    case 6: image.ConsistentDays = reader.GetInt32(); break;
                    case 7: image.Status = reader.GetInt32(); break;
                    case 8: image.LastReason = reader.GetString() ?? ""; break;
                    case 9: image.LastIncidentDay = reader.GetInt32(); break;
                    default: reader.Skip(); break;
                }
            }
            return image;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A public image is a row of values or an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = (reader.GetString() ?? "").ToLowerInvariant();
            reader.Read();
            switch (name)
            {
                case "esteem": image.Esteem = reader.GetSingle(); break;
                case "incident": image.Incident = reader.GetSingle(); break;
                case "familiarity": image.Familiarity = reader.GetSingle(); break;
                case "entrenchment": image.Entrenchment = reader.GetSingle(); break;
                case "target": image.Target = reader.GetSingle(); break;
                case "lastreviewday": image.LastReviewDay = reader.GetInt32(); break;
                case "consistentdays": image.ConsistentDays = reader.GetInt32(); break;
                case "status": image.Status = reader.GetInt32(); break;
                case "lastreason": image.LastReason = reader.GetString() ?? ""; break;
                case "lastincidentday": image.LastIncidentDay = reader.GetInt32(); break;
                default: reader.Skip(); break;
            }
        }
        return image;
    }

    public override void Write(Utf8JsonWriter writer, PublicImage image, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        SaveNumbers.Write(writer, image.Esteem, options);
        SaveNumbers.Write(writer, image.Incident, options);
        SaveNumbers.Write(writer, image.Familiarity, options);
        SaveNumbers.Write(writer, image.Entrenchment, options);
        SaveNumbers.Write(writer, image.Target, options);
        writer.WriteNumberValue(image.LastReviewDay);
        var has9 = image.LastIncidentDay != -1;
        var has8 = has9 || !string.IsNullOrEmpty(image.LastReason);
        var has7 = has8 || image.Status != 0;
        var has6 = has7 || image.ConsistentDays != 0;
        if (has6) writer.WriteNumberValue(image.ConsistentDays);
        if (has7) writer.WriteNumberValue(image.Status);
        if (has8) writer.WriteStringValue(image.LastReason ?? "");
        if (has9) writer.WriteNumberValue(image.LastIncidentDay);
        writer.WriteEndArray();
    }
}

/// <summary>
/// A market quote as a row of seven values: Commodity, Production, Demand, Imports, Exports, Price, Satisfaction.
/// Reads both row and legacy object forms.
/// </summary>
public sealed class MarketQuoteConverter : JsonConverter<MarketQuote>
{
    public override MarketQuote Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var quote = new MarketQuote();
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (index++)
                {
                    case 0: quote.Commodity = (Commodity)reader.GetInt32(); break;
                    case 1: quote.Production = reader.GetSingle(); break;
                    case 2: quote.Demand = reader.GetSingle(); break;
                    case 3: quote.Imports = reader.GetSingle(); break;
                    case 4: quote.Exports = reader.GetSingle(); break;
                    case 5: quote.Price = reader.GetSingle(); break;
                    case 6: quote.Satisfaction = reader.GetSingle(); break;
                    default: reader.Skip(); break;
                }
            }
            return quote;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A market quote is a row of values or an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = (reader.GetString() ?? "").ToLowerInvariant();
            reader.Read();
            switch (name)
            {
                case "commodity":
                    quote.Commodity = reader.TokenType == JsonTokenType.String && Enum.TryParse<Commodity>(reader.GetString(), true, out var parsed)
                        ? parsed : (Commodity)reader.GetInt32();
                    break;
                case "production": quote.Production = reader.GetSingle(); break;
                case "demand": quote.Demand = reader.GetSingle(); break;
                case "imports": quote.Imports = reader.GetSingle(); break;
                case "exports": quote.Exports = reader.GetSingle(); break;
                case "price": quote.Price = reader.GetSingle(); break;
                case "satisfaction": quote.Satisfaction = reader.GetSingle(); break;
                default: reader.Skip(); break;
            }
        }
        return quote;
    }

    public override void Write(Utf8JsonWriter writer, MarketQuote quote, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue((int)quote.Commodity);
        SaveNumbers.Write(writer, quote.Production, options);
        SaveNumbers.Write(writer, quote.Demand, options);
        SaveNumbers.Write(writer, quote.Imports, options);
        SaveNumbers.Write(writer, quote.Exports, options);
        SaveNumbers.Write(writer, quote.Price, options);
        SaveNumbers.Write(writer, quote.Satisfaction, options);
        writer.WriteEndArray();
    }
}

/// <summary>
/// An opinion reason as a row: Id, GrantedDay, ExpiresDay, Stacks, Scale (trailing defaults omitted).
/// Reads both row and legacy object forms.
/// </summary>
public sealed class OpinionReasonConverter : JsonConverter<OpinionReason>
{
    public override OpinionReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var reason = new OpinionReason();
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (index++)
                {
                    case 0: reason.Id = reader.GetString() ?? ""; break;
                    case 1: reason.GrantedDay = reader.GetInt32(); break;
                    case 2: reason.ExpiresDay = reader.GetInt32(); break;
                    case 3: reason.Stacks = reader.GetInt32(); break;
                    case 4: reason.Scale = reader.GetSingle(); break;
                    default: reader.Skip(); break;
                }
            }
            return reason;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("An opinion reason is a row of values or an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = (reader.GetString() ?? "").ToLowerInvariant();
            reader.Read();
            switch (name)
            {
                case "id": reason.Id = reader.GetString() ?? ""; break;
                case "grantedday": reason.GrantedDay = reader.GetInt32(); break;
                case "expiresday": reason.ExpiresDay = reader.GetInt32(); break;
                case "stacks": reason.Stacks = reader.GetInt32(); break;
                case "scale": reason.Scale = reader.GetSingle(); break;
                default: reader.Skip(); break;
            }
        }
        return reason;
    }

    public override void Write(Utf8JsonWriter writer, OpinionReason reason, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(reason.Id ?? "");
        writer.WriteNumberValue(reason.GrantedDay);
        var hasScale = reason.Scale != 1f;
        var hasStacks = hasScale || reason.Stacks != 1;
        var hasExpires = hasStacks || reason.ExpiresDay != 0;
        if (hasExpires) writer.WriteNumberValue(reason.ExpiresDay);
        if (hasStacks) writer.WriteNumberValue(reason.Stacks);
        if (hasScale) SaveNumbers.Write(writer, reason.Scale, options);
        writer.WriteEndArray();
    }
}

/// <summary>
/// A chronicle entry as a row of fourteen values: Sequence, Day, Year, Month, DateDay, Hour,
/// Kind, ActorId, TargetId, ActorName, TargetName, Subject, Place, Public.
/// Reads both row and legacy object forms.
/// </summary>
public sealed class ChronicleEntryConverter : JsonConverter<ChronicleEntry>
{
    public override ChronicleEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        long sequence = 0;
        int day = 0, year = 2024, month = 1, dateDay = 1, hour = 0, actorId = 0, targetId = -1;
        string kind = "", actorName = "", targetName = "", subject = "", place = "";
        var isPublic = true;
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (index++)
                {
                    case 0: sequence = reader.GetInt64(); break;
                    case 1: day = reader.GetInt32(); break;
                    case 2: year = reader.GetInt32(); break;
                    case 3: month = reader.GetInt32(); break;
                    case 4: dateDay = reader.GetInt32(); break;
                    case 5: hour = reader.GetInt32(); break;
                    case 6: kind = reader.GetString() ?? ""; break;
                    case 7: actorId = reader.GetInt32(); break;
                    case 8: targetId = reader.GetInt32(); break;
                    case 9: actorName = reader.GetString() ?? ""; break;
                    case 10: targetName = reader.GetString() ?? ""; break;
                    case 11: subject = reader.GetString() ?? ""; break;
                    case 12: place = reader.GetString() ?? ""; break;
                    case 13: isPublic = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() != 0 : reader.GetBoolean(); break;
                    default: reader.Skip(); break;
                }
            }
            return new ChronicleEntry(sequence, day, new GameDate { Year = year, Month = month, Day = dateDay, Hour = hour }, kind,
                actorId, targetId, actorName, targetName, subject, place, isPublic);
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A chronicle entry is a row of values or an object.");
        GameDate date = new() { Year = 2024, Month = 1, Day = 1, Hour = 0 };
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = (reader.GetString() ?? "").ToLowerInvariant();
            reader.Read();
            switch (name)
            {
                case "sequence": sequence = reader.GetInt64(); break;
                case "day": day = reader.GetInt32(); break;
                case "date": date = JsonSerializer.Deserialize<GameDate>(ref reader, options); break;
                case "kind": kind = reader.GetString() ?? ""; break;
                case "actorid": actorId = reader.GetInt32(); break;
                case "targetid": targetId = reader.GetInt32(); break;
                case "actorname": actorName = reader.GetString() ?? ""; break;
                case "targetname": targetName = reader.GetString() ?? ""; break;
                case "subject": subject = reader.GetString() ?? ""; break;
                case "place": place = reader.GetString() ?? ""; break;
                case "public": isPublic = reader.GetBoolean(); break;
                default: reader.Skip(); break;
            }
        }
        return new ChronicleEntry(sequence, day, date, kind, actorId, targetId, actorName, targetName, subject, place, isPublic);
    }

    public override void Write(Utf8JsonWriter writer, ChronicleEntry entry, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(entry.Sequence);
        writer.WriteNumberValue(entry.Day);
        writer.WriteNumberValue(entry.Date.Year);
        writer.WriteNumberValue(entry.Date.Month);
        writer.WriteNumberValue(entry.Date.Day);
        writer.WriteNumberValue(entry.Date.Hour);
        writer.WriteStringValue(entry.Kind ?? "");
        writer.WriteNumberValue(entry.ActorId);
        writer.WriteNumberValue(entry.TargetId);
        writer.WriteStringValue(entry.ActorName ?? "");
        writer.WriteStringValue(entry.TargetName ?? "");
        writer.WriteStringValue(entry.Subject ?? "");
        writer.WriteStringValue(entry.Place ?? "");
        writer.WriteBooleanValue(entry.Public);
        writer.WriteEndArray();
    }
}

/// <summary>
/// A battle round as a row of fourteen values: Day, AttackerOrganisation, DefenderOrganisation,
/// AttackerDamage, DefenderDamage, AttackerLosses, DefenderLosses, AttackerPower, DefenderPower,
/// AttackerPlanning, AttackerOrganisationSpent, DefenderOrganisationSpent, HasOrganisationTelemetry, Note.
/// Reads both row and legacy object forms.
/// </summary>
public sealed class LandBattleRoundConverter : JsonConverter<LandBattle.Round>
{
    public override LandBattle.Round Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var round = new LandBattle.Round();
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (index++)
                {
                    case 0: round.Day = reader.GetInt32(); break;
                    case 1: round.AttackerOrganisation = reader.GetSingle(); break;
                    case 2: round.DefenderOrganisation = reader.GetSingle(); break;
                    case 3: round.AttackerDamage = reader.GetSingle(); break;
                    case 4: round.DefenderDamage = reader.GetSingle(); break;
                    case 5: round.AttackerLosses = reader.GetSingle(); break;
                    case 6: round.DefenderLosses = reader.GetSingle(); break;
                    case 7: round.AttackerPower = reader.GetSingle(); break;
                    case 8: round.DefenderPower = reader.GetSingle(); break;
                    case 9: round.AttackerPlanning = reader.GetSingle(); break;
                    case 10: round.AttackerOrganisationSpent = reader.GetSingle(); break;
                    case 11: round.DefenderOrganisationSpent = reader.GetSingle(); break;
                    case 12: round.HasOrganisationTelemetry = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() != 0 : reader.GetBoolean(); break;
                    case 13: round.Note = reader.GetString() ?? ""; break;
                    default: reader.Skip(); break;
                }
            }
            return round;
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A battle round is a row of values or an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = (reader.GetString() ?? "").ToLowerInvariant();
            reader.Read();
            switch (name)
            {
                case "day": round.Day = reader.GetInt32(); break;
                case "attackerorganisation": round.AttackerOrganisation = reader.GetSingle(); break;
                case "defenderorganisation": round.DefenderOrganisation = reader.GetSingle(); break;
                case "attackerdamage": round.AttackerDamage = reader.GetSingle(); break;
                case "defenderdamage": round.DefenderDamage = reader.GetSingle(); break;
                case "attackerlosses": round.AttackerLosses = reader.GetSingle(); break;
                case "defenderlosses": round.DefenderLosses = reader.GetSingle(); break;
                case "attackerpower": round.AttackerPower = reader.GetSingle(); break;
                case "defenderpower": round.DefenderPower = reader.GetSingle(); break;
                case "attackerplanning": round.AttackerPlanning = reader.GetSingle(); break;
                case "attackerorganisationspent": round.AttackerOrganisationSpent = reader.GetSingle(); break;
                case "defenderorganisationspent": round.DefenderOrganisationSpent = reader.GetSingle(); break;
                case "hasorganisationtelemetry": round.HasOrganisationTelemetry = reader.GetBoolean(); break;
                case "note": round.Note = reader.GetString() ?? ""; break;
                default: reader.Skip(); break;
            }
        }
        return round;
    }

    public override void Write(Utf8JsonWriter writer, LandBattle.Round round, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(round.Day);
        SaveNumbers.Write(writer, round.AttackerOrganisation, options);
        SaveNumbers.Write(writer, round.DefenderOrganisation, options);
        SaveNumbers.Write(writer, round.AttackerDamage, options);
        SaveNumbers.Write(writer, round.DefenderDamage, options);
        SaveNumbers.Write(writer, round.AttackerLosses, options);
        SaveNumbers.Write(writer, round.DefenderLosses, options);
        SaveNumbers.Write(writer, round.AttackerPower, options);
        SaveNumbers.Write(writer, round.DefenderPower, options);
        SaveNumbers.Write(writer, round.AttackerPlanning, options);
        SaveNumbers.Write(writer, round.AttackerOrganisationSpent, options);
        SaveNumbers.Write(writer, round.DefenderOrganisationSpent, options);
        writer.WriteBooleanValue(round.HasOrganisationTelemetry);
        if (!string.IsNullOrEmpty(round.Note)) writer.WriteStringValue(round.Note);
        writer.WriteEndArray();
    }
}
