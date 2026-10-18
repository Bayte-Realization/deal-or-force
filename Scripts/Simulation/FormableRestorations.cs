using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Countries that were on the map in January 2024 and are not any more, as states that can be
/// proclaimed again.
///
/// The formable catalogue is written by hand, and nobody writes "Montenegro, again" before
/// Montenegro has gone. So these are made from the campaign itself: every country of the
/// starting world that has lost all its ground is offered back - under its own name, its own
/// colour and its own flag - to whoever its people now are. A state of its people, a rising on
/// its old ground, a government in exile that has come home: the same three that bring a
/// vanished country back in history. What it costs is what any proclamation costs, and the
/// ground must be held first; a restoration grants nothing, it names what has been won.
///
/// Ids are "restore_" and the old tag, in lower case. They are made fresh for each question
/// rather than stored, because whether a country has vanished is a fact about today.
/// </summary>
public static class FormableRestorations
{
    public const string Prefix = "restore_";

    /// <summary>The tag a restoration brings back, or null when the id is not one.</summary>
    public static string RestoredTag(string formableId)
    {
        if (string.IsNullOrEmpty(formableId)) return null;
        if (FormableNations.Find(formableId) is { Restores.Length: > 0 } authored)
            return authored.Restores.ToUpperInvariant();
        return formableId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && formableId.Length > Prefix.Length
            ? formableId[Prefix.Length..].ToUpperInvariant()
            : null;
    }

    private static Campaign _cachedFor;
    private static int _cachedDay = int.MinValue;
    private static int _cachedRevision = int.MinValue;
    private static readonly List<FormableNation> Cached = new();

    /// <summary>Every restoration open in this campaign today.</summary>
    public static IReadOnlyList<FormableNation> In(Campaign campaign)
    {
        if (campaign is null) return Array.Empty<FormableNation>();
        if (ReferenceEquals(_cachedFor, campaign) && _cachedDay == campaign.ElapsedDays && _cachedRevision == campaign.MapRevision)
            return Cached;
        _cachedFor = campaign;
        _cachedDay = campaign.ElapsedDays;
        _cachedRevision = campaign.MapRevision;
        Cached.Clear();
        foreach (var gone in campaign.Nations)
        {
            if (!Vanished(campaign, gone)) continue;
            if (Restoration(campaign, gone) is { } definition) Cached.Add(definition);
        }
        return Cached;
    }

    /// <summary>One restoration by id, if that country has vanished.</summary>
    public static FormableNation Find(Campaign campaign, string formableId)
    {
        var tag = RestoredTag(formableId);
        if (tag is null || campaign is null) return null;
        var gone = campaign.NationByTag(tag);
        return gone is not null && Vanished(campaign, gone) ? Restoration(campaign, gone) : null;
    }

    /// <summary>
    /// Whether a country of the starting world has gone: a state (not a movement), with ground
    /// written for it in the bookmark, that holds none of anything now and has not already
    /// been brought back by somebody.
    /// </summary>
    public static bool Vanished(Campaign campaign, Nation nation)
    {
        if (nation is null || nation.Movement is not null || string.IsNullOrEmpty(nation.Tag)) return false;
        if (campaign.HasOwnedLand(nation)) return false;
        if (Authored(campaign, nation.Tag).Length == 0) return false;
        var id = Prefix + nation.Tag.ToLowerInvariant();
        return !campaign.Nations.Any(other => string.Equals(other.FormedNationId, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The ground the bookmark wrote for a tag.</summary>
    public static int[] Authored(Campaign campaign, string tag) =>
        campaign.ScenarioId == "2024" ? ProvinceOwnership.ProvincesOf(tag) :
            campaign.ScenarioTerritories.GetValueOrDefault(tag, Array.Empty<int>());

    private static FormableNation Restoration(Campaign campaign, Nation gone)
    {
        var name = CountryNames.Of(gone.Tag);
        if (string.IsNullOrEmpty(name) || name == gone.Tag) name = Loc.Get(gone.Name);
        var cultures = DemographyCatalog.PrimaryCultures(gone.Tag).ToArray();
        return new FormableNation(Prefix + gone.Tag.ToLowerInvariant(), name,
            Loc.Format("{0} again, on the ground it held when the world began - proclaimed by its own people, by a rising on its old soil, or by its government come home from exile.", name),
            Array.Empty<string>(), new[] { gone.Tag }, Array.Empty<int>(),
            campaign.Defines.RestorationMinimumStability, campaign.Defines.RestorationCostShare,
            campaign.Defines.RestorationGovernanceGain, campaign.Defines.RestorationProductivityGain,
            Array.Empty<string>(), Array.Empty<string>(), gone.Color ?? "")
        {
            EligibleCultures = cultures,
            Restores = gone.Tag,
            AiFoundation = true
        };
    }

    /// <summary>
    /// Whether a country may bring this one back: its own people are this country's people;
    /// or it rose on this country's old ground (a rebel state, a breakaway); or it is the
    /// government in exile of it. Never the country that swallowed it, unless that country
    /// is - by culture - the same nation, which is the one case a restoration is a reunion.
    /// </summary>
    public static bool MayRestore(Campaign campaign, Nation founder, string tag)
    {
        if (founder is null || string.IsNullOrEmpty(tag) || string.Equals(founder.Tag, tag, StringComparison.OrdinalIgnoreCase)) return false;
        var cultures = DemographyCatalog.PrimaryCultures(tag);
        foreach (var culture in cultures)
            if (DemographyCatalog.IsPrimary(founder.Tag, culture) ||
                founder.FoundedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase)) return true;
        if (campaign is null || campaign.ProvinceMap is null) return false;
        // Founded on its ground: a revolt whose capital stands inside the old borders.
        if (!string.IsNullOrEmpty(founder.FoundedInRevolt) || founder.Movement is { Landless: false })
        {
            var toState = campaign.ProvinceMap.ProvinceToState;
            foreach (var province in Authored(campaign, tag))
                if (province > 0 && province < toState.Length && toState[province] == founder.CapitalStateMapIndex)
                    return true;
        }
        return false;
    }
}

public sealed partial class Defines
{
    /// <summary>How settled a country must be to proclaim a vanished one again.</summary>
    public float RestorationMinimumStability { get; set; } = 0.40f;
    /// <summary>What it costs, as a share of the founder's output.</summary>
    public float RestorationCostShare { get; set; } = 0.015f;
    /// <summary>The administrative dividend of being a recognised state again.</summary>
    public float RestorationGovernanceGain { get; set; } = 0.04f;
    public float RestorationProductivityGain { get; set; } = 0.01f;
}
