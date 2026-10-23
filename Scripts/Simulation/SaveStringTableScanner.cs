using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Find the save's id table without keeping its whole JSON in RAM.
/// Memory follows the largest individual token plus the table, not the payload.
/// A late table and old files without a table are both accepted.</summary>
internal static class SaveStringTableScanner
{
    internal static string[] Read(Stream source)
    {
        var buffer = new byte[32 * 1024];
        var count = 0;
        // Stream deserialization accepts a UTF-8 BOM, so the preliminary scan must too.
        while (count < 3)
        {
            var read = source.Read(buffer, count, 3 - count);
            if (read == 0) break;
            count += read;
        }
        if (count == 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf) count = 0;
        var state = new JsonReaderState();
        var expecting = false;
        var tableDepth = -1;
        List<string> table = null;
        while (true)
        {
            if (count == buffer.Length) Array.Resize(ref buffer, checked(buffer.Length * 2));
            var read = source.Read(buffer, count, buffer.Length - count);
            var final = read == 0;
            count += read;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, count), final, state);
            while (reader.Read())
            {
                if (expecting)
                {
                    if (reader.TokenType != JsonTokenType.StartArray) return Array.Empty<string>();
                    expecting = false;
                    tableDepth = reader.CurrentDepth;
                    table = new List<string>();
                    continue;
                }
                if (table is not null)
                {
                    if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == tableDepth)
                        return table.ToArray();
                    if (reader.TokenType == JsonTokenType.String && reader.CurrentDepth == tableDepth + 1)
                        table.Add(reader.GetString() ?? "");
                    continue;
                }
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 &&
                    reader.ValueTextEquals("StringTable")) expecting = true;
            }
            if (final) return Array.Empty<string>();
            var consumed = checked((int)reader.BytesConsumed);
            state = reader.CurrentState;
            count -= consumed;
            if (count > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, count);
        }
    }
}
