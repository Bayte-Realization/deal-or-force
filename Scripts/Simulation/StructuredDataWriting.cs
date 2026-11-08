using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DealOrForce.Simulation;

public static partial class StructuredData
{
    private static readonly JsonSerializerOptions YamlStringOptions = new()
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Structured edits normalise formatting. Raw-source saves never call this.</summary>
    public static string FormatJson(string path, string json)
    {
        if (!IsDocument(path)) throw new InvalidDataException($"Unsupported data format: {path}");
        return IsYaml(path) ? JsonToYaml(json) : json;
    }

    /// <summary>
    /// A deliberately small writer for our JSON-compatible YAML subset, not object
    /// serialisation. Quoted strings/keys cannot turn IDs, colours or dates into types;
    /// numbers are emitted from their original JSON tokens, never through a double.
    /// </summary>
    public static string JsonToYaml(string json)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var output = new StringBuilder();
        AppendYaml(output, document.RootElement, 0);
        var text = output.ToString();
        if (Encoding.UTF8.GetByteCount(text) > MaxYamlBytes)
            throw new InvalidDataException($"YAML exceeds {MaxYamlBytes} bytes.");
        return text;
    }

    private static bool ExpandedYaml(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().MoveNext() ||
        element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0;

    private static void AppendYaml(StringBuilder output, JsonElement element, int indent)
    {
        if (element.ValueKind == JsonValueKind.Object && ExpandedYaml(element))
        {
            foreach (var property in element.EnumerateObject())
            {
                output.Append(' ', indent).Append(JsonSerializer.Serialize(property.Name, YamlStringOptions)).Append(':');
                AppendYamlValue(output, property.Value, indent);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array && ExpandedYaml(element))
        {
            foreach (var value in element.EnumerateArray())
            {
                output.Append(' ', indent).Append('-');
                AppendYamlValue(output, value, indent);
            }
        }
        else output.Append(' ', indent).Append(YamlScalar(element)).Append('\n');
    }

    private static void AppendYamlValue(StringBuilder output, JsonElement value, int indent)
    {
        if (ExpandedYaml(value))
        { output.Append('\n'); AppendYaml(output, value, indent + 2); }
        else output.Append(' ').Append(YamlScalar(value)).Append('\n');
    }

    private static string YamlScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString(), YamlStringOptions),
        JsonValueKind.Object => "{}",
        JsonValueKind.Array => "[]",
        _ => value.GetRawText()
    };
}
