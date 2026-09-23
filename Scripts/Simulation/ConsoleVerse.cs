using System;
using System.IO;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What the console's `touchgrass` says before it closes the game, read from common/console.
///
/// It was a constant in the interface code, which made the one line in the game written purely
/// for fun the one line nobody could rewrite without a compiler. Now it is data like the rest:
/// one entry per language, keyed by the language's folder name under game/localization, the
/// later file winning, so a mod replaces it by shipping a file beside the base game's. A
/// language with no entry of its own is given the English one, and a game with no file at all
/// is given the verse it shipped with.
/// </summary>
public static class ConsoleVerse
{
    /// <summary>What `touchgrass` says if common/console has nothing to say for it.</summary>
    public const string Fallback =
        "For a hundred years you steered the fate of states,\n" +
        "Carved up the seas and moved the borders round.\n" +
        "Outside, there is grass that no map ever drew.\n" +
        "It is real. Go out and feel it on the ground.";

    /// <summary>The verse in one language, read fresh each time - it is asked for once a session.</summary>
    public static string TouchGrass(string language)
    {
        string english = null, own = null;
        foreach (var file in GameDataLoader.DataFiles("common", "console"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("touchgrass", out var verse) ||
                    verse.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in verse.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String) continue;
                    var text = entry.Value.GetString()?.Trim() ?? "";
                    if (text.Length == 0) continue;
                    if (string.Equals(entry.Name, "english", StringComparison.OrdinalIgnoreCase)) english = text;
                    if (string.Equals(entry.Name, language, StringComparison.OrdinalIgnoreCase)) own = text;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                DiagnosticLogs.Write("errors",
                    $"CONSOLE_VERSE_REJECTED file={Path.GetFileName(file)} error={exception.Message}");
            }
        }
        return own ?? english ?? Fallback;
    }
}
