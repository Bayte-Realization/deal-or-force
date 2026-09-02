using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// Whitelisted polymorphic command wire format. Network peers exchange the same
/// engine-neutral commands used by UI, AI and replays; arbitrary CLR types are never loaded.
/// </summary>
public static class NetworkCommandCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly IReadOnlyDictionary<string, Type> Types = typeof(ICampaignCommand).Assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(ICampaignCommand).IsAssignableFrom(type))
        .ToDictionary(type => type.Name, StringComparer.Ordinal);

    public static bool TryEncode(ICampaignCommand command, out string typeName, out string json)
    {
        typeName = "";
        json = "";
        if (command is null || !Types.TryGetValue(command.GetType().Name, out var allowed) || allowed != command.GetType())
            return false;
        typeName = allowed.Name;
        json = JsonSerializer.Serialize(command, allowed, Options);
        return true;
    }

    public static bool TryDecode(string typeName, string json, out ICampaignCommand command)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(json) ||
            !Types.TryGetValue(typeName, out var type)) return false;
        try
        {
            command = JsonSerializer.Deserialize(json, type, Options) as ICampaignCommand;
            return command is not null;
        }
        catch
        {
            return false;
        }
    }
}
