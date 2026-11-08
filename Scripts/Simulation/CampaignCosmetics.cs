using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Bounded, campaign-local identity data. Reads never register designs globally.</summary>
public static class CampaignCosmetics
{
    public const int MaximumNameCharacters = 96;
    public const int MaximumFlagDesignBytes = 64 * 1024;
    public const int MaximumEmblems = 32;
    public const int MaximumJsonValues = 512;
    public const int MaximumJsonDepth = 16;

    public static bool CanEdit(Campaign campaign, Nation issuer, Nation target) =>
        campaign is not null && issuer is not null && target is not null &&
        ReferenceEquals(campaign.NationById(issuer.Id), issuer) &&
        ReferenceEquals(campaign.NationById(target.Id), target) &&
        (target.Id == issuer.Id || target.AnswersTo(issuer.Id));

    public static bool IsPrintableName(string text)
    {
        text ??= "";
        if (text.Length > MaximumNameCharacters * 2) return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                return false;
            i++;
        }
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (++count > MaximumNameCharacters) return false;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return false;
        }
        return true;
    }

    /// <summary>A catalogue ID, never a path/URI; unknown mod IDs retain the legacy fallback behaviour.</summary>
    public static bool IsSafeSpriteId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 96 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static bool TryNormaliseFlagDesign(string document, int nationId, out string canonical, out string refusal) =>
        TryRead(document, nationId, out canonical, out _, out refusal);

    /// <summary>Returns an independent design, not a mutable catalogue instance or saved-state cache.</summary>
    public static FlagDesign ReadFlagDesign(Nation nation) => nation is not null &&
        TryRead(nation.CustomFlagDesignJson, nation.Id, out _, out var design, out _) ? design : null;

    private static bool TryRead(string document, int nationId, out string canonical,
        out FlagDesign design, out string refusal)
    {
        canonical = "";
        design = null;
        refusal = "";
        if (string.IsNullOrEmpty(document)) return true;
        if (nationId < 0) { refusal = "Invalid custom flag design."; return false; }
        if (document.Length > MaximumFlagDesignBytes || Encoding.UTF8.GetByteCount(document) > MaximumFlagDesignBytes)
        { refusal = "Custom flag designs must fit within 64 KiB."; return false; }
        try
        {
            using var json = JsonDocument.Parse(document, new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = MaximumJsonDepth });
            var root = json.RootElement;
            if (!Fields(root, "flag_designs") || !root.TryGetProperty("flag_designs", out var envelope) ||
                envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Count() != 1)
            { refusal = "A custom flag must contain exactly one design in a flag_designs object."; return false; }
            var values = 0;
            if (!WithinBudget(root, ref values))
            { refusal = "The custom flag contains invalid or excessive design elements."; return false; }
            var entry = envelope.EnumerateObject().First();
            if (entry.Name.Length > 96 || !entry.Name.StartsWith("flag_", StringComparison.Ordinal) ||
                !entry.Name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') || !ValidBody(entry.Value))
            { refusal = "The custom flag contains invalid or excessive design elements."; return false; }
            var errors = new List<string>();
            design = FlagDesign.Parse(entry.Name, entry.Value, errors);
            if (design is null || errors.Count > 0 ||
                design.Emblems.Any(emblem => emblem.Sprite.Length > 0 && !IsSafeSpriteId(emblem.Sprite)))
            { design = null; refusal = "Invalid custom flag design."; return false; }
            design.Id = "flag_campaign_cosmetic_" + nationId.ToString(CultureInfo.InvariantCulture);
            canonical = design.ToJson();
            // ToJson rounds numbers. Reject tiny stripe weights that would round to
            // invalid zero rather than storing a document that fails its next read.
            using var normal = JsonDocument.Parse(canonical);
            errors.Clear();
            design = FlagDesign.Parse(design.Id, normal.RootElement.GetProperty("flag_designs").EnumerateObject().Single().Value, errors);
            if (design is null || errors.Count > 0 || Encoding.UTF8.GetByteCount(canonical) > MaximumFlagDesignBytes)
            { canonical = ""; design = null; refusal = "Invalid custom flag design."; return false; }
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            canonical = "";
            design = null;
            refusal = "Invalid custom flag design.";
            return false;
        }
    }

    private static bool Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal)) return false;
        return true;
    }

    private static bool OptionalType(JsonElement value, string key, JsonValueKind kind) =>
        !value.TryGetProperty(key, out var field) || field.ValueKind == kind;

    private static bool ValidBody(JsonElement body)
    {
        if (!Fields(body, "ratio", "background", "emblems") ||
            !OptionalType(body, "ratio", JsonValueKind.Number) ||
            !body.TryGetProperty("background", out var background) ||
            !Fields(background, "pattern", "colours", "colors", "weights", "accent") ||
            !OptionalType(background, "pattern", JsonValueKind.String) ||
            !OptionalType(background, "accent", JsonValueKind.String) ||
            background.TryGetProperty("colours", out _) && background.TryGetProperty("colors", out _)) return false;
        foreach (var key in new[] { "colours", "colors", "weights" })
            if (background.TryGetProperty(key, out var list) &&
                (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 5)) return false;
        if (!body.TryGetProperty("emblems", out var emblems)) return true;
        if (emblems.ValueKind != JsonValueKind.Array || emblems.GetArrayLength() > MaximumEmblems) return false;
        foreach (var emblem in emblems.EnumerateArray())
        {
            if (!Fields(emblem, "shape", "sprite", "colour", "color", "x", "y", "size", "rotation", "count") ||
                emblem.TryGetProperty("colour", out _) && emblem.TryGetProperty("color", out _)) return false;
            foreach (var key in new[] { "shape", "sprite", "colour", "color" })
                if (!OptionalType(emblem, key, JsonValueKind.String)) return false;
            foreach (var key in new[] { "x", "y", "size", "rotation", "count" })
                if (!OptionalType(emblem, key, JsonValueKind.Number)) return false;
        }
        return true;
    }

    private static bool WithinBudget(JsonElement value, ref int count)
    {
        if (++count > MaximumJsonValues) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in value.EnumerateObject())
                if (!WithinBudget(field.Value, ref count)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (!WithinBudget(item, ref count)) return false;
        return true;
    }
}
