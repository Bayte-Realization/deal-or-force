using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace DealOrForce.Simulation;

/// <summary>
/// One data boundary for JSON and a safe, JSON-compatible subset of YAML. Catalogues
/// still receive JsonElement: format does not change schemas, merge rules or simulation.
/// No object deserialization, executable tags, alias expansion or implicit date objects.
/// Save files, protocol messages and package manifests deliberately do not use this reader.
/// </summary>
public static class StructuredData
{
    public const int MaxYamlBytes = 32 * 1024 * 1024;
    private const int MaxDepth = 64;
    private const int MaxNodes = 1_000_000;
    private static readonly Regex Number = new(
        @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsDocument(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".json" or ".yaml" or ".yml";

    /// <summary>
    /// True for a #rgb or #rrggbb colour, which is what a colour written in data has to be.
    ///
    /// Checked rather than trusted because a colour is not read back until something is drawn: a
    /// typo here is invisible until it is a screen the wrong colour, and by then nothing points at
    /// the file that caused it.
    /// </summary>
    public static bool IsHexColour(string text)
    {
        if (text is null || (text.Length != 4 && text.Length != 7) || text[0] != '#') return false;
        for (var i = 1; i < text.Length; i++)
            if (!Uri.IsHexDigit(text[i])) return false;
        return true;
    }

    public static string[] Files(string directory, SearchOption search = SearchOption.TopDirectoryOnly) =>
        !Directory.Exists(directory) ? Array.Empty<string>() :
        Directory.EnumerateFiles(directory, "*", search).Where(IsDocument)
            .OrderBy(path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path.Replace('\\', '/'), StringComparer.Ordinal).ToArray();

    public static JsonDocument ReadDocument(string path) =>
        JsonDocument.Parse(ReadJson(path), GameDataLoader.ForgivingJson);

    public static string ReadJson(string path)
    {
        if (!IsDocument(path)) throw new InvalidDataException($"Unsupported data format: {path}");
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path);
        try
        {
            if (new FileInfo(path).Length > MaxYamlBytes)
                throw new InvalidDataException($"YAML exceeds {MaxYamlBytes} bytes.");
            return YamlToJson(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is YamlException or InvalidDataException)
        {
            throw new InvalidDataException($"{path}: {exception.Message}", exception);
        }
    }

    public static string YamlToJson(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxYamlBytes)
            throw new InvalidDataException($"YAML exceeds {MaxYamlBytes} bytes.");
        var parser = new Parser(new StringReader(text));
        var nodes = 0;
        parser.Consume<StreamStart>();
        if (parser.Accept<StreamEnd>(out _)) throw new InvalidDataException("YAML document is empty.");
        parser.Consume<DocumentStart>();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = MaxDepth }))
        {
            WriteNode(parser, writer, 0, ref nodes);
            parser.Consume<DocumentEnd>();
            if (!parser.Accept<StreamEnd>(out _))
                throw new InvalidDataException("Only one YAML document per file is supported.");
            parser.Consume<StreamEnd>();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteNode(IParser parser, Utf8JsonWriter writer, int depth, ref int nodes)
    {
        if (depth >= MaxDepth) throw new InvalidDataException($"YAML nesting exceeds {MaxDepth} levels.");
        if (++nodes > MaxNodes) throw new InvalidDataException($"YAML exceeds {MaxNodes} nodes.");
        if (parser.Accept<AnchorAlias>(out var alias))
            throw Reject(alias, "YAML aliases are not supported; use numbered overlay files instead.");
        if (parser.Accept<Scalar>(out var scalar))
        {
            ValidateNode(scalar, null);
            parser.Consume<Scalar>();
            WriteScalar(writer, scalar);
        }
        else if (parser.Accept<MappingStart>(out var mapping))
        {
            ValidateNode(mapping, "map");
            parser.Consume<MappingStart>();
            writer.WriteStartObject();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (!parser.Accept<MappingEnd>(out _))
            {
                if (!parser.Accept<Scalar>(out var key))
                    throw new InvalidDataException("YAML mapping keys must be scalar strings, not collections or aliases.");
                ValidateNode(key, "str");
                parser.Consume<Scalar>();
                if (++nodes > MaxNodes) throw Reject(key, "YAML node limit exceeded.");
                if (key.Value == "<<") throw Reject(key, "YAML merge keys are not supported; use overlay files instead.");
                if (!keys.Add(key.Value)) throw Reject(key, $"Duplicate YAML key '{key.Value}'.");
                writer.WritePropertyName(key.Value);
                WriteNode(parser, writer, depth + 1, ref nodes);
            }
            parser.Consume<MappingEnd>();
            writer.WriteEndObject();
        }
        else if (parser.Accept<SequenceStart>(out var sequence))
        {
            ValidateNode(sequence, "seq");
            parser.Consume<SequenceStart>();
            writer.WriteStartArray();
            while (!parser.Accept<SequenceEnd>(out _)) WriteNode(parser, writer, depth + 1, ref nodes);
            parser.Consume<SequenceEnd>();
            writer.WriteEndArray();
        }
        else throw new InvalidDataException("Expected a YAML mapping, sequence or scalar.");
    }

    private static void ValidateNode(NodeEvent node, string collectionTag)
    {
        if (!node.Anchor.IsEmpty) throw Reject(node, "YAML anchors are not supported; use overlay files instead.");
        if (node.Tag.IsEmpty) return;
        var tag = node.Tag.Value;
        var allowed = collectionTag is null
            ? tag is "tag:yaml.org,2002:str" or "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:null"
                or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float"
            : tag == "tag:yaml.org,2002:" + collectionTag;
        if (!allowed) throw Reject(node, $"Unsupported YAML tag '{tag}'.");
    }

    private static void WriteScalar(Utf8JsonWriter writer, Scalar scalar)
    {
        var value = scalar.Value;
        var tag = scalar.Tag.IsEmpty ? "" : scalar.Tag.Value;
        if (tag == "tag:yaml.org,2002:str" || (tag.Length == 0 && scalar.Style != ScalarStyle.Plain))
        { writer.WriteStringValue(value); return; }
        if (tag is "" or "tag:yaml.org,2002:null")
        {
            if (value.Length == 0 || value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase))
            { writer.WriteNullValue(); return; }
            if (tag.Length != 0) throw Reject(scalar, "Invalid !!null scalar.");
        }
        if (tag is "" or "tag:yaml.org,2002:bool")
        {
            if (bool.TryParse(value, out var boolean)) { writer.WriteBooleanValue(boolean); return; }
            if (tag.Length != 0) throw Reject(scalar, "Use true or false for !!bool, not YAML 1.1 yes/no/on/off.");
        }
        if (tag is "" or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float")
        {
            if (Number.IsMatch(value) && (tag != "tag:yaml.org,2002:int" || !value.Contains('.') && !value.Contains('e') && !value.Contains('E')))
            { writer.WriteRawValue(value); return; }
            if (tag.Length != 0) throw Reject(scalar, "Use a JSON-compatible decimal number for !!int/!!float.");
        }
        writer.WriteStringValue(value);
    }

    private static InvalidDataException Reject(ParsingEvent item, string message) =>
        new($"{message} At {item.Start}.");
}
