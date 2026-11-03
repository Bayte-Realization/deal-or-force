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

    /// <summary>
    /// A country of the starting world that still stands but has broken apart - a rump holding
    /// less than half of its own people - may be put back together by one of its pieces: Texas
    /// or California proclaiming the United States again while a rump in Washington still
    /// flies the flag. Ids are "reunite_" and the old tag.
    /// </summary>
    public const string ReunionPrefix = "reunite_";

    /// <summary>The tag a restoration brings back, or null when the id is not one.</summary>
    public static string RestoredTag(string formableId)
    {
        if (string.IsNullOrEmpty(formableId)) return null;
        if (FormableNations.Find(formableId) is { Restores.Length: > 0 } authored)
            return authored.Restores.ToUpperInvariant();
        if (formableId.StartsWith(ReunionPrefix, StringComparison.OrdinalIgnoreCase) && formableId.Length > ReunionPrefix.Length)
            return formableId[ReunionPrefix.Length..].ToUpperInvariant();
        return formableId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && formableId.Length > Prefix.Length
            ? formableId[Prefix.Length..].ToUpperInvariant()
            : null;
    }

    private static Campaign _cachedFor;
    private static int _cachedDay = int.MinValue;
    private static int _cachedRevision = int.MinValue;
    private static int _cachedMovementRevision = int.MinValue;
    private static int _cachedNationCount = -1;
    private static readonly List<FormableNation> Cached = new();

    /// <summary>Every restoration open in this campaign today.</summary>
    public static IReadOnlyList<FormableNation> In(Campaign campaign)
    {
        if (campaign is null) return Array.Empty<FormableNation>();
        if (ReferenceEquals(_cachedFor, campaign) && _cachedDay == campaign.ElapsedDays && _cachedRevision == campaign.MapRevision &&
            _cachedMovementRevision == campaign.MovementRevision && _cachedNationCount == campaign.Nations.Count)
            return Cached;
        _cachedFor = campaign;
        _cachedDay = campaign.ElapsedDays;
        _cachedRevision = campaign.MapRevision;
        _cachedMovementRevision = campaign.MovementRevision;
        _cachedNationCount = campaign.Nations.Count;
        Cached.Clear();
        foreach (var gone in campaign.Nations)
        {
            if (!Vanished(campaign, gone)) continue;
            if (Restoration(campaign, gone) is { } definition) Cached.Add(definition);
        }
        foreach (var broken in campaign.Nations)
            if (BrokenApart(campaign, broken) && Restoration(campaign, broken, reunion: true) is { } reunion)
                Cached.Add(reunion);
        return Cached;
    }

    /// <summary>One restoration by id, if that country has vanished.</summary>
    public static FormableNation Find(Campaign campaign, string formableId)
    {
        var tag = RestoredTag(formableId);
        if (tag is null || campaign is null) return null;
        var gone = campaign.NationByTag(tag);
        if (gone is null) return null;
        if (formableId.StartsWith(ReunionPrefix, StringComparison.OrdinalIgnoreCase))
            return BrokenApart(campaign, gone) ? Restoration(campaign, gone, reunion: true) : null;
        return Vanished(campaign, gone) ? Restoration(campaign, gone) : null;
    }

    /// <summary>
    /// Whether a starting-world identity has gone: it has authored ground but no land,
    /// no surviving government in exile and no living successor bearing that identity.
    /// A historical proclamation or a dissolved exile is not an eternal lock.
    /// </summary>
    public static bool Vanished(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || string.IsNullOrEmpty(nation.Tag)) return false;
        // A surviving exile is still this country's government. Once that government has
        // actually dissolved, however, its abandoned rolls must not veto every later return.
        if (nation.Movement is { } movement && (!movement.Exile || !movement.Dissolved)) return false;
        if (campaign.HasOwnedLand(nation)) return false;
        if (Authored(campaign, nation.Tag).Length == 0) return false;
        return !HasLivingIdentity(campaign, nation.Tag);
    }

    /// <summary>Historical proclamations stay in the ledger, not as an eternal veto.
    /// Land or a surviving government in exile keeps the identity alive. Check the restored
    /// tag too, so authored restoration ids cannot create a duplicate of a dynamic one.</summary>
    public static bool HasLivingIdentity(Campaign campaign, string tag) =>
        campaign is not null && !string.IsNullOrEmpty(tag) && campaign.Nations.Any(other =>
            (string.Equals(other.Tag, tag, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(RestoredTag(other.FormedNationId), tag, StringComparison.OrdinalIgnoreCase)) &&
            (campaign.HasOwnedLand(other) || other.Movement is { Exile: true, Dissolved: false }));

    internal static void Invalidate(Campaign campaign)
    {
        if (ReferenceEquals(_cachedFor, campaign)) _cachedFor = null;
    }

    /// <summary>
    /// Whether a country of the starting world still stands but has lost most of itself: of the
    /// people living today on the ground the bookmark gave it, the share still under its own flag
    /// is below ReunionRumpShare, and nobody has reunited it already.
    /// </summary>
    public static bool BrokenApart(Campaign campaign, Nation nation)
    {
        if (campaign?.ProvinceMap is null || nation is null || nation.Movement is not null || string.IsNullOrEmpty(nation.Tag)) return false;
        if (!campaign.HasOwnedLand(nation)) return false;
        var id = ReunionPrefix + nation.Tag.ToLowerInvariant();
        if (campaign.Nations.Any(other => string.Equals(other.FormedNationId, id, StringComparison.OrdinalIgnoreCase))) return false;
        var authored = Authored(campaign, nation.Tag);
        if (authored.Length == 0) return false;
        var toState = campaign.ProvinceMap.ProvinceToState;
        var regions = new HashSet<int>();
        foreach (var province in authored)
            if (province > 0 && province < toState.Length) regions.Add(toState[province]);
        long all = 0, own = 0;
        foreach (var state in campaign.States)
        {
            if (!regions.Contains(state.MapIndex) && !regions.Contains(state.ParentMapIndex)) continue;
            all += Math.Max(0L, state.Population);
            if (state.NationMapIndex == nation.MapIndex) own += Math.Max(0L, state.Population);
        }
        return all > 0 && own < all * (double)campaign.Defines.ReunionRumpShare;
    }

    /// <summary>The ground the bookmark wrote for a tag.</summary>
    public static int[] Authored(Campaign campaign, string tag) =>
        campaign.ScenarioId == "2024" ? ProvinceOwnership.ProvincesOf(tag) :
            campaign.ScenarioTerritories.GetValueOrDefault(tag, Array.Empty<int>());

    /// <summary>
    /// Repair the old first-proclamation bug, once on the disk-loading host after its
    /// saved borders have been attached. Sharing a primary culture used to let an intact
    /// large country call itself a tiny annexed country. Do not apply this to network
    /// snapshots, legitimate local/rump successors, or countries with a prior union whose
    /// previous identity cannot be recovered unambiguously. No borders, cash, rulers,
    /// historical events, or random state are rewritten by this identity-only repair.
    /// </summary>
    internal static int RepairDiminishedIdentities(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null) return 0;
        var repaired = 0;
        foreach (var nation in campaign.Nations)
        {
            var id = nation.FormedNationId;
            var restored = RestoredTag(id);
            if (restored is null || string.Equals(nation.Tag, restored, StringComparison.OrdinalIgnoreCase) ||
                nation.FormedNationHistory.Any(old => !string.Equals(old, id, StringComparison.OrdinalIgnoreCase)) ||
                !FormableNations.SmallerThanCountry(campaign, nation, Authored(campaign, restored), id)) continue;

            // Only use an authored, language-independent identity; never invent a name for
            // a mod's unknown tag or turn its custom user-facing name into translated state.
            string name = null, colour = null;
            foreach (var document in GameDataLoader.DataDocuments("common", "countries"))
                using (document)
                    foreach (var entry in document.RootElement.EnumerateObject())
                    {
                        if (!string.Equals(entry.Name, nation.Tag, StringComparison.OrdinalIgnoreCase) ||
                            entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        if (entry.Value.TryGetProperty("name", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String)
                            name = text.GetString();
                        if (entry.Value.TryGetProperty("color", out text) && text.ValueKind == System.Text.Json.JsonValueKind.String)
                            colour = text.GetString();
                    }
            if (string.IsNullOrWhiteSpace(name)) continue;
            var oldCountry = campaign.NationByTag(restored);
            // An intervening player repaint is not the old proclamation's colour to undo.
            if (!string.IsNullOrWhiteSpace(colour) && oldCountry is not null &&
                string.Equals(nation.Color, oldCountry.Color, StringComparison.OrdinalIgnoreCase)) nation.Color = colour;
            nation.FormedNationId = "";
            nation.FormedNationDay = -1;
            nation.FormedNationHistory.Remove(id);
            nation.Name = name;
            nation.NameProclaimed = false;
            if (string.Equals(nation.ConsolidationProject, id, StringComparison.OrdinalIgnoreCase))
            {
                nation.ConsolidationProject = "";
                nation.ConsolidationFocusId = -1;
                nation.ConsolidationAmbition = 0f;
            }
            campaign.MarkStrategicChange();
            repaired++;
        }
        return repaired;
    }

    /// <summary>
    /// The ground the bookmark wrote for the tag is home whether or not it was taken back.
    /// At a restoration the lost parts go into the country's homelands, so the claims
    /// machinery - disputes, recovery goals, the staff's preference for lost ground - sees
    /// a country with an unfinished sentence at its border, not a stranger's line. Without
    /// this a restored country sat quietly inside whatever it first retook: it had no
    /// homeland titles beyond the ground in hand, and nothing in the world told it the rest
    /// was still its own.
    /// </summary>
    public static void SeedLostHomelands(Campaign campaign, Nation nation, string restoredTag)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null || nation is null || string.IsNullOrEmpty(restoredTag)) return;
        var toState = topology.ProvinceToState;
        var held = new HashSet<int>(campaign.StatesOf(nation).Select(state => state.MapIndex));
        if (!campaign.Homelands.TryGetValue(nation.Id, out var home))
            campaign.Homelands[nation.Id] = home = new List<int>();
        var grew = false;
        foreach (var province in Authored(campaign, restoredTag))
        {
            if (province <= 0 || province >= toState.Length) continue;
            var state = toState[province];
            if (held.Contains(state) || home.Contains(state)) continue;
            home.Add(state);
            grew = true;
        }
        if (grew) campaign.MarkStrategicChange();
    }

    private static FormableNation Restoration(Campaign campaign, Nation gone, bool reunion = false)
    {
        var name = CountryNames.Of(gone.Tag);
        if (string.IsNullOrEmpty(name) || name == gone.Tag) name = Loc.Get(gone.Name);
        var cultures = DemographyCatalog.PrimaryCultures(gone.Tag).ToArray();
        return new FormableNation((reunion ? ReunionPrefix : Prefix) + gone.Tag.ToLowerInvariant(), name,
            reunion
                ? Loc.Format("{0} put back together by one of its pieces: the successor that holds most of the old ground takes the name and the flag, and the rump that still calls itself {0} becomes a rival government to be brought in by treaty or by force.", name)
                : Loc.Format("{0} again, on the ground it held when the world began - proclaimed by its own people, by a rising on its old soil, or by its government come home from exile.", name),
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
    ///
    /// Reunion is not a licence to take the smaller name. A holder that is already a larger
    /// state, or is pursuing one, is refused by <see cref="FormableNations.SmallerThanCountry"/>.
    /// </summary>
    public static bool MayRestore(Campaign campaign, Nation founder, string tag)
    {
        if (founder is null || string.IsNullOrEmpty(tag) || string.Equals(founder.Tag, tag, StringComparison.OrdinalIgnoreCase)) return false;
        var cultures = DemographyCatalog.PrimaryCultures(tag);
        foreach (var culture in cultures)
            if (DemographyCatalog.IsPrimary(founder.Tag, culture) ||
                founder.FoundedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase)) return true;
        if (campaign is null || campaign.ProvinceMap is null) return false;
        // Founded on its ground: a revolt or successor state whose regions stand inside the old borders.
        if (!string.IsNullOrEmpty(founder.FoundedInRevolt) || founder.Movement is { Landless: false } || founder.SecededFromId >= 0)
        {
            // And the old ground has to be a real part of it: a breakaway of a hundred and sixty
            // million Indians holding one valley of Bhutan proclaimed itself Bhutan. A share of its
            // people (RestorationGroundShare) must live on the ground the country held.
            var toState = campaign.ProvinceMap.ProvinceToState;
            var held = campaign.StatesOf(founder);
            var oldGround = new HashSet<int>();
            foreach (var province in Authored(campaign, tag))
                if (province > 0 && province < toState.Length) oldGround.Add(toState[province]);
            long onIt = 0, all = 0;
            foreach (var state in held)
            {
                all += Math.Max(0L, state.Population);
                if (oldGround.Contains(state.MapIndex) || oldGround.Contains(state.ParentMapIndex)) onIt += Math.Max(0L, state.Population);
            }
            if (onIt > 0 && onIt >= all * (double)campaign.Defines.RestorationGroundShare) return true;
        }
        return false;
    }
}

public sealed partial class Defines
{
    /// <summary>The share of its own people below which a country of the starting world counts as broken apart (FormableRestorations.BrokenApart).</summary>
    public float ReunionRumpShare { get; set; } = 0.5f;
    /// <summary>How settled a country must be to proclaim a vanished one again.</summary>
    public float RestorationMinimumStability { get; set; } = 0.40f;
    /// <summary>What it costs, as a share of the founder's output.</summary>
    public float RestorationCostShare { get; set; } = 0.015f;
    /// <summary>The least share of a breakaway's people living on a vanished country's old ground for it to restore that country.</summary>
    public float RestorationGroundShare { get; set; } = 0.30f;
    /// <summary>
    /// How far a people's own state's capital may be from its ground in another country for that
    /// ground to count as the state's lost land; beyond it the people may secede as a new nation.
    /// </summary>
    public float DiasporaReachKm { get; set; } = 2500f;
    /// <summary>The administrative dividend of being a recognised state again.</summary>
    public float RestorationGovernanceGain { get; set; } = 0.04f;
    public float RestorationProductivityGain { get; set; } = 0.01f;
}
