using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// The layout of a text save: small things on one line, large things broken into lines.
///
/// A text save is meant to be opened in an editor, found in, and changed. Written with the
/// serializer's own indentation, every field of every relation, country and region sat on a
/// line of its own: a campaign sixty years in was 162 MB on disk for 81 MB of JSON, half of
/// it spaces and line breaks. Here an object or array that fits on a line - a relation's
/// opinion reason, a public image, a market quote - is written on one, in the compact form;
/// an array of plain values (a province column, a row of history) is always one line however
/// long; and whatever is larger is broken, one member to a line, indented two spaces a level.
/// The result is still ordinary JSON that any editor and any parser reads, and the file a
/// person scrolls through has a line for each thing worth a line.
///
/// Two passes over the compact bytes: the first measures every container, the second writes.
/// Nothing here reads the campaign - it is bytes in, bytes out - so it runs wherever the save's
/// writing does, off the game's thread.
/// </summary>
public static class SaveTextLayout
{
    /// <summary>The longest container still written on one line, in bytes of compact JSON.</summary>
    public const int InlineLimit = 160;

    /// <summary>Lay out JSON (indented or not) as a text save. Invalid input comes back unchanged.</summary>
    public static byte[] Relayout(byte[] json)
    {
        if (json is null || json.Length == 0) return json;
        byte[] compact;
        try { compact = Compact(json); }
        catch (JsonException) { return json; }

        // Pass one: every container's compact length, and whether it holds nothing but values.
        var lengths = new List<int>();
        var plain = new List<bool>();
        var open = new Stack<(int Index, long Start)>();
        var reader = new Utf8JsonReader(compact, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (open.Count > 0) plain[open.Peek().Index] = false;
                    open.Push((lengths.Count, reader.TokenStartIndex));
                    lengths.Add(0);
                    plain.Add(reader.TokenType == JsonTokenType.StartArray);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    var (index, start) = open.Pop();
                    lengths[index] = (int)Math.Min(int.MaxValue, reader.TokenStartIndex + 1 - start);
                    break;
            }
        }

        // Pass two: write it.
        var output = new ArrayBufferWriter<byte>(compact.Length + compact.Length / 8);
        var levels = new Stack<(bool Inline, bool First, bool IsObject)>();
        var container = 0;
        reader = new Utf8JsonReader(compact, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        var afterName = false;
        while (reader.Read())
        {
            var token = reader.TokenType;
            if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                var (inline, first, _) = levels.Pop();
                if (!inline && !first) { Write(output, "\n"); Indent(output, levels.Count); }
                Write(output, token == JsonTokenType.EndObject ? "}" : "]");
                afterName = false;
                continue;
            }

            // Where this token goes: after a name it follows on the same line; otherwise it is
            // the next member of its container and needs a separator and, in a broken
            // container, its own line.
            if (!afterName && levels.Count > 0)
            {
                var (inline, first, isObject) = levels.Pop();
                if (!first) Write(output, ",");
                if (!inline) { Write(output, "\n"); Indent(output, levels.Count + 1); }
                levels.Push((inline, false, isObject));
            }
            afterName = false;

            switch (token)
            {
                case JsonTokenType.PropertyName:
                    Raw(output, compact, ref reader, quoted: true);
                    Write(output, levels.Count > 0 && levels.Peek().Inline ? ":" : ": ");
                    afterName = true;
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                {
                    var parentInline = levels.Count > 0 && levels.Peek().Inline;
                    var inline = parentInline || lengths[container] <= InlineLimit || plain[container];
                    container++;
                    Write(output, token == JsonTokenType.StartObject ? "{" : "[");
                    levels.Push((inline, true, token == JsonTokenType.StartObject));
                    break;
                }
                case JsonTokenType.String:
                    Raw(output, compact, ref reader, quoted: true);
                    break;
                default:
                    Raw(output, compact, ref reader, quoted: false);
                    break;
            }
        }
        Write(output, "\n");
        return output.WrittenSpan.ToArray();
    }

    /// <summary>The same JSON without whitespace, so every container's length is its compact length.</summary>
    private static byte[] Compact(byte[] json)
    {
        var buffer = new ArrayBufferWriter<byte>(json.Length);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = true,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject: writer.WriteStartObject(); break;
                    case JsonTokenType.EndObject: writer.WriteEndObject(); break;
                    case JsonTokenType.StartArray: writer.WriteStartArray(); break;
                    case JsonTokenType.EndArray: writer.WriteEndArray(); break;
                    case JsonTokenType.PropertyName: writer.WritePropertyName(Escaped(ref reader)); break;
                    case JsonTokenType.String: writer.WriteStringValue(Escaped(ref reader)); break;
                    case JsonTokenType.Number: writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true); break;
                    case JsonTokenType.True: writer.WriteBooleanValue(true); break;
                    case JsonTokenType.False: writer.WriteBooleanValue(false); break;
                    case JsonTokenType.Null: writer.WriteNullValue(); break;
                }
            }
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A string token as the text it stands for, so the writer escapes it once.</summary>
    private static string Escaped(ref Utf8JsonReader reader) => reader.GetString() ?? "";

    /// <summary>A token copied as it stands in the compact bytes: strings with their quotes.</summary>
    private static void Raw(ArrayBufferWriter<byte> output, byte[] source, ref Utf8JsonReader reader, bool quoted)
    {
        if (!quoted) { output.Write(reader.ValueSpan); return; }
        var start = (int)reader.TokenStartIndex;
        // The token starts at its opening quote; the value is the escaped bytes between them.
        var length = reader.ValueSpan.Length + 2;
        output.Write(source.AsSpan(start, length));
    }

    private static void Indent(ArrayBufferWriter<byte> output, int level)
    {
        var span = output.GetSpan(level * 2);
        span[..(level * 2)].Fill((byte)' ');
        output.Advance(level * 2);
    }

    private static void Write(ArrayBufferWriter<byte> output, string text)
    {
        var span = output.GetSpan(text.Length);
        for (var i = 0; i < text.Length; i++) span[i] = (byte)text[i];
        output.Advance(text.Length);
    }
}
