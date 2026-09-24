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
        var day = 0;
        var index = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number) throw new JsonException("A history row holds numbers only.");
            if (index == 0) day = reader.GetInt32();
            else if (index <= values.Length) values[index - 1] = reader.GetSingle();
            index++;
        }
        return Build(day, values);
    }

    private static NationHistorySample ReadObject(ref Utf8JsonReader reader)
    {
        Span<float> values = stackalloc float[21];
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
        writer.WriteNumberValue(sample.Gdp);
        writer.WriteNumberValue(sample.Treasury);
        writer.WriteNumberValue(sample.PopulationMillions);
        writer.WriteNumberValue(sample.WeeklyIncome);
        writer.WriteNumberValue(sample.WeeklyExpenses);
        writer.WriteNumberValue(sample.WeeklyBalance);
        writer.WriteNumberValue(sample.Productivity);
        writer.WriteNumberValue(sample.StandardOfLiving);
        writer.WriteNumberValue(sample.Literacy);
        writer.WriteNumberValue(sample.Stability);
        writer.WriteNumberValue(sample.MarketAccess);
        writer.WriteNumberValue(sample.ShortageIndex);
        writer.WriteNumberValue(sample.TradeBalance);
        writer.WriteNumberValue(sample.MilitaryPower);
        writer.WriteNumberValue(sample.ManpowerMillions);
        writer.WriteNumberValue(sample.WarSupport);
        writer.WriteNumberValue(sample.FoodPriceIndex);
        writer.WriteNumberValue(sample.ConsumerPriceIndex);
        writer.WriteNumberValue(sample.IndustrialPriceIndex);
        writer.WriteNumberValue(sample.EnergyPriceIndex);
        writer.WriteNumberValue(sample.ArmsPriceIndex);
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
                    case 2: group.Culture = reader.GetString() ?? ""; break;
                    case 3: group.Religion = reader.GetString() ?? ""; break;
                    case 4: group.Profession = reader.GetString() ?? ""; break;
                    case 5: group.Size = reader.GetInt64(); break;
                    case 6: group.Employment = reader.GetSingle(); break;
                    case 7: group.Wealth = reader.GetSingle(); break;
                    case 8: group.StandardOfLiving = reader.GetSingle(); break;
                    case 9: group.Radicalism = reader.GetSingle(); break;
                    default: reader.Skip(); break;
                }
            }
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
                case "culture": group.Culture = reader.GetString() ?? ""; break;
                case "religion": group.Religion = reader.GetString() ?? ""; break;
                case "profession": group.Profession = reader.GetString() ?? ""; break;
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
        writer.WriteStringValue(group.Culture ?? "");
        writer.WriteStringValue(group.Religion ?? "");
        writer.WriteStringValue(group.Profession ?? "");
        writer.WriteNumberValue(group.Size);
        writer.WriteNumberValue(group.Employment);
        writer.WriteNumberValue(group.Wealth);
        writer.WriteNumberValue(group.StandardOfLiving);
        writer.WriteNumberValue(group.Radicalism);
        writer.WriteEndArray();
    }
}
