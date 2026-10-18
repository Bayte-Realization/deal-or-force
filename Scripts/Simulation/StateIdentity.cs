using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// What the state says it is, over what its people are: a declared state religion and a
/// declared state culture, chosen on State Affairs the way a form of government is.
///
/// The rule the player asked for, made data: a group may only be declared the state's own
/// if that group actually lives in the country, and in a share large enough to carry the
/// name - how large is a define, not a constant, because a 5% state church and a 40%
/// national movement are different countries. The exceptions are the secular answers:
/// declaring the state atheistic or secular (or dropping a culture declaration) is a act of
/// government and needs no demographic permission.
///
/// Declaring costs stability and starts a cooldown, like changing politics; the declaration
/// is public news. What it does NOT do is convert anybody: the people's faiths and tongues
/// are the demography's facts, and the declaration is the state flying its own colours over
/// them - which is exactly why a declaration against the majority costs and a declaration
/// the country already lives by does not.
/// </summary>
public sealed partial class Defines
{
    /// <summary>
    /// <summary>The share of the country a faith or a people must already be before the state may
    /// declare itself theirs. Not a fixed constant: a mod running a diaspora state lowers
    /// it, a nation of rooted majorities raises it.</summary>
    public float StateIdentityShare { get; set; } = 0.15f;

    /// <summary>The stability a declaration costs the country, flat.</summary>
    public float StateIdentityStabilityCost { get; set; } = 0.02f;

    /// <summary>Days after a declaration before the state may declare again.</summary>
    public int StateIdentityCooldownDays { get; set; } = 1825;

    /// <summary>
    /// How much two states' declared identities move their opinion of each other, in opinion
    /// points: the faith counted at full weight, the people at half. Zero switches the driver
    /// off, the way <see cref="Defines.IdeologyOpinionWeight"/> does for politics.
    /// </summary>
    public float IdentityOpinionWeight { get; set; } = 9f;

    /// <summary>
    /// Stability points a week a state bleeds while its declared faith stands against a
    /// majority of its own people: a throne speaking for a creed the country no longer prays
    /// to pays for the gap every week it insists. Scaled by how far short of half the
    /// country the faith falls; secular declarations pay nothing.
    /// </summary>
    public float StateIdentityStrainWeekly { get; set; } = 0.0012f;
}

public sealed partial class Nation
{
    /// <summary>The faith the state declares, or "" for none declared yet.</summary>
    public string DeclaredReligion = "";

    /// <summary>The people the state declares itself of, or "" for none declared yet.</summary>
    public string DeclaredCulture = "";

    /// <summary>The last day any declaration was made.</summary>
    public int IdentityChangedDay = -9999;
}

public static class StateIdentity
{
    /// <summary>The answers a government may always take: no church, no nation named.</summary>
    public static readonly HashSet<string> Secular = new(StringComparer.OrdinalIgnoreCase)
    { "irreligion", "atheism", "state_atheism", "secularism" };

    /// <summary>
    /// The one secular answer the interface offers.
    ///
    /// The four ids above are synonyms a mod or an old save may use; presenting all of them
    /// gave a player two or three buttons that all meant "no state church". They stay valid
    /// in <see cref="IsSecular"/> so an existing declaration still reads as secular, but the
    /// choice list shows this one.
    /// </summary>
    public const string SecularChoice = "irreligion";

    public static bool IsSecular(string id) => !string.IsNullOrWhiteSpace(id) && Secular.Contains(id);

    /// <summary>The name of an identity as the state declares it, not as a census counts it.</summary>
    public static string Name(string id, bool religion)
    {
        if (string.IsNullOrWhiteSpace(id)) return Loc.Get("none declared");
        if (religion && IsSecular(id)) return Loc.Get("Secular state");
        return religion ? DemographyCatalog.ReligionName(id) : DemographyCatalog.CultureName(id);
    }

    /// <summary>
    /// What the country is, declared or - before anything is declared - what most of it is.
    ///
    /// A dossier line and a list row ask the same question, and both must answer with the
    /// share the declaration is judged by rather than with a name alone.
    /// </summary>
    public static (string Id, float Share) Headline(Campaign campaign, Nation nation, bool religion)
    {
        if (campaign is null || nation is null) return (null, 0f);
        var declared = religion ? nation.DeclaredReligion : nation.DeclaredCulture;
        if (!string.IsNullOrWhiteSpace(declared))
            return (declared, religion
                ? ReligionShare(campaign, nation, declared)
                : CultureShare(campaign, nation, declared));
        var largest = Candidates(campaign, nation, religion);
        return largest.Count > 0 ? largest[0] : (null, 0f);
    }

    /// <summary>
    /// One compact line for every place a country is read at a glance: faith and people,
    /// each with the exact share of the country behind it. A secular declaration is named as
    /// a policy rather than as a congregation.
    /// </summary>
    public static string Brief(Campaign campaign, Nation nation)
    {
        var faith = Headline(campaign, nation, true);
        var culture = Headline(campaign, nation, false);
        return Loc.Format("Faith: {0} · Culture: {1}", Short(faith, true), Short(culture, false));

        string Short((string Id, float Share) entry, bool religion)
        {
            if (string.IsNullOrWhiteSpace(entry.Id)) return Loc.Get("unknown");
            if (religion && IsSecular(entry.Id)) return Name(entry.Id, true);
            return Loc.Format("{0} {1:0.0%}", Name(entry.Id, religion), entry.Share);
        }
    }

    /// <summary>How much of the country a faith is, by the people actually holding it.</summary>
    public static float ReligionShare(Campaign campaign, Nation nation, string religionId)
    {
        var total = 0L;
        var counted = 0L;
        foreach (var state in campaign.StatesOf(nation))
        {
            foreach (var (religion, size) in EconomySystem.Religions(state))
            {
                total += size;
                if (religion.Equals(religionId, StringComparison.OrdinalIgnoreCase)) counted += size;
            }
        }
        return total <= 0 ? 0f : counted / (float)total;
    }

    /// <summary>How much of the country a people is, by the people actually living it.</summary>
    public static float CultureShare(Campaign campaign, Nation nation, string cultureId)
    {
        var total = 0L;
        var counted = 0L;
        foreach (var state in campaign.StatesOf(nation))
        {
            foreach (var (culture, size) in EconomySystem.Cultures(state))
            {
                total += size;
                if (culture.Equals(cultureId, StringComparison.OrdinalIgnoreCase)) counted += size;
            }
        }
        return total <= 0 ? 0f : counted / (float)total;
    }

    /// <summary>The faiths and peoples of the country worth a button, largest first.</summary>
    public static List<(string Id, float Share)> Candidates(Campaign campaign, Nation nation, bool religion)
    {
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var total = 0L;
        foreach (var state in campaign.StatesOf(nation))
        {
            foreach (var (id, size) in religion ? EconomySystem.Religions(state) : EconomySystem.Cultures(state))
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                totals[id] = totals.GetValueOrDefault(id) + size;
                total += size;
            }
        }
        return total <= 0
            ? new List<(string, float)>()
            : totals.Select(pair => (pair.Key, pair.Value / (float)total))
                .OrderByDescending(entry => entry.Item2).ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>Demographic faiths plus the one secular policy, once per identity, not once per source.</summary>
    public static List<(string Id, float Share)> ReligionChoices(Campaign campaign, Nation nation)
    {
        // A person who is irreligious is a demographic fact; a state with no church is a
        // policy. Both used to arrive as buttons, and the synonyms behind them could arrive
        // twice. The faith list now carries faiths only, and exactly one secular answer
        // follows it, whatever a mod or an old save calls that answer.
        var result = Candidates(campaign, nation, true)
            .Where(choice => !IsSecular(choice.Id))
            .Take(8)
            .ToList();
        result.Add((SecularChoice, ReligionShare(campaign, nation, SecularChoice)));
        return result;
    }

    public static string BlockReason(Campaign campaign, Nation nation, string religionId, string cultureId)
    {
        if (!campaign.FeatureEnabled("state_identity"))
            return Loc.Get("This mechanic is disabled in this scenario.");
        if (campaign.ElapsedDays - nation.IdentityChangedDay < campaign.Defines.StateIdentityCooldownDays)
            return Loc.Format("The last declaration is still fresh: {0} day(s) to go",
                campaign.Defines.StateIdentityCooldownDays - (campaign.ElapsedDays - nation.IdentityChangedDay));
        if (!string.IsNullOrWhiteSpace(religionId) &&
            !religionId.Equals(nation.DeclaredReligion, StringComparison.OrdinalIgnoreCase))
        {
            if (DemographyCatalog.Religion(religionId) is null && !IsSecular(religionId))
                return Loc.Get("No such faith is known");
            // A secular answer needs nobody's permission; a church needs a congregation.
            if (!IsSecular(religionId) &&
                ReligionShare(campaign, nation, religionId) < campaign.Defines.StateIdentityShare)
                return Loc.Format("That faith is too small in the country to be declared: under {0}",
                    campaign.Defines.StateIdentityShare.ToString("0%"));
        }
        if (!string.IsNullOrWhiteSpace(cultureId) &&
            !cultureId.Equals(nation.DeclaredCulture, StringComparison.OrdinalIgnoreCase))
        {
            if (DemographyCatalog.Culture(cultureId) is null)
                return Loc.Get("No such people is known");
            if (CultureShare(campaign, nation, cultureId) < campaign.Defines.StateIdentityShare)
                return Loc.Format("That people is too few in the country to be declared: under {0}",
                    campaign.Defines.StateIdentityShare.ToString("0%"));
        }
        return "";
    }

    /// <summary>
    /// First sight of a country: what it declares is what it already is, the plurality of
    /// its own people - silently, and at nobody's cost, because nobody chose it.
    /// </summary>
    public static void EnsureDeclared(Campaign campaign)
    {
        if (campaign is null) return;
        if (campaign.FeatureOverrides.TryGetValue("state_identity", out var over) ? !over
            : campaign.Defines.Features is not null && campaign.Defines.Features.TryGetValue("state_identity", out var def) && !def)
            return;
        var strain = campaign.Defines.StateIdentityStrainWeekly / 7f;
        foreach (var nation in campaign.Nations)
        {
            if (nation.Population <= 0 || nation.DeclaredReligion.Length > 0 || nation.DeclaredCulture.Length > 0)
            {
                // A declaration against the grain is not a one-off cost: while the throne
                // speaks for a creed or a people short of half the country, the gap pays
                // every week in stability, scaled by how far short it falls. A secular
                // answer never strains - declining a church costs nothing to keep declined.
                if (strain > 0f && !IsSecular(nation.DeclaredReligion))
                {
                    var faithShare = ReligionShare(campaign, nation, nation.DeclaredReligion);
                    if (faithShare < 0.5f)
                        nation.Stability = M.Clamp01(nation.Stability -
                            strain * (0.5f - faithShare) / 0.5f);
                }
                continue;
            }
            var faiths = Candidates(campaign, nation, true);
            var peoples = Candidates(campaign, nation, false);
            nation.DeclaredReligion = faiths.Count > 0 ? faiths[0].Id : "";
            nation.DeclaredCulture = peoples.Count > 0 ? peoples[0].Id : "";
            // First sight is not a declaration by anybody: nobody chose it, so it must not
            // start the five-year cooldown. It used to set today's date here, and every
            // campaign opened with its state identity already locked - a player could not
            // change the church or the people of their own country until 2029.
            nation.IdentityChangedDay = -9999;
        }
    }

    /// <summary>
    /// What two declared identities add to the countries' opinion of each other, recomputed
    /// from what both are right now - the same continuous driver politics uses. The same
    /// faith warms most, the same people warms less, and a difference warms nobody: sharing
    /// nothing is not a grievance, it is just distance.
    /// </summary>
    public static float Affinity(Campaign campaign, int leftId, int rightId)
    {
        if (campaign is null) return 0f;
        var weight = campaign.Defines.IdentityOpinionWeight;
        if (weight <= 0f) return 0f;
        var left = campaign.NationById(leftId);
        var right = campaign.NationById(rightId);
        if (left is null || right is null) return 0f;
        var total = 0f;
        if (left.DeclaredReligion.Length > 0 &&
            left.DeclaredReligion.Equals(right.DeclaredReligion, StringComparison.OrdinalIgnoreCase)) total += weight;
        if (left.DeclaredCulture.Length > 0 &&
            left.DeclaredCulture.Equals(right.DeclaredCulture, StringComparison.OrdinalIgnoreCase)) total += weight * 0.5f;
        return total;
    }

    /// <summary>One dossier line for each declaration, where the player reads a country.</summary>
    public static string Caption(Campaign campaign, Nation nation, bool religion)
    {
        var id = religion ? nation.DeclaredReligion : nation.DeclaredCulture;
        if (string.IsNullOrWhiteSpace(id)) return Loc.Get("none declared");
        return Name(id, religion);
    }

    /// <summary>Whether two states have both declared the same non-secular state faith.</summary>
    public static bool ShareDeclaredFaith(Nation a, Nation b) =>
        a is not null && b is not null &&
        !string.IsNullOrWhiteSpace(a.DeclaredReligion) &&
        !IsSecular(a.DeclaredReligion) &&
        a.DeclaredReligion.Equals(b.DeclaredReligion, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How far a region's people sit outside the state's declared faith or culture (0 to 1).
    /// Zero when the mechanic is off, when nothing is declared, or when the declared identity
    /// is already the majority of the region.
    /// </summary>
    public static float RegionAlienation(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (campaign is null || nation is null || state is null || state.Population <= 0) return 0f;
        var hasReligion = !string.IsNullOrWhiteSpace(nation.DeclaredReligion) && !IsSecular(nation.DeclaredReligion);
        var hasCulture = !string.IsNullOrWhiteSpace(nation.DeclaredCulture);
        if (!hasReligion && !hasCulture) return 0f;
        if (campaign.FeatureOverrides.TryGetValue("state_identity", out var over) ? !over
            : campaign.Defines.Features is not null && campaign.Defines.Features.TryGetValue("state_identity", out var def) && !def)
            return 0f;

        var groups = state.PopulationGroups;
        long total = 0, faithMatch = 0, cultureMatch = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group.Size <= 0) continue;
            total += group.Size;
            if (hasReligion && string.Equals(group.Religion, nation.DeclaredReligion, StringComparison.OrdinalIgnoreCase))
                faithMatch += group.Size;
            if (hasCulture && string.Equals(group.Culture, nation.DeclaredCulture, StringComparison.OrdinalIgnoreCase))
                cultureMatch += group.Size;
        }
        if (total <= 0) return 0f;

        var faithGap = hasReligion && faithMatch * 2 < total ? 0.50f - faithMatch / (float)total : 0f;
        var cultureGap = hasCulture && cultureMatch * 2 < total ? 0.50f - cultureMatch / (float)total : 0f;
        return M.Clamp01(MathF.Max(faithGap, cultureGap) * 2f);
    }
}

public sealed class DeclareStateIdentityCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string Religion { get; init; } = "";
    public string Culture { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        if (StateIdentity.BlockReason(campaign, nation, Religion, Culture).Length > 0) return;

        var changed = false;
        var faithChange = !string.IsNullOrWhiteSpace(Religion) &&
                          !Religion.Equals(nation.DeclaredReligion, StringComparison.OrdinalIgnoreCase);
        var cultureChange = !string.IsNullOrWhiteSpace(Culture) &&
                            !Culture.Equals(nation.DeclaredCulture, StringComparison.OrdinalIgnoreCase);
        if (faithChange) { nation.DeclaredReligion = Religion; changed = true; }
        if (cultureChange) { nation.DeclaredCulture = Culture; changed = true; }
        if (!changed) return;
        nation.IdentityChangedDay = campaign.ElapsedDays;
        // A declaration against the grain costs the calm of the country; confirming what
        // the country already is costs almost nothing. The secular answers still cost - an
        // act against every church at once is not a free one - but need no congregation.
        var against = (faithChange && !StateIdentity.IsSecular(Religion) &&
                          StateIdentity.ReligionShare(campaign, nation, Religion) < 0.5f) ||
                      (cultureChange && StateIdentity.CultureShare(campaign, nation, Culture) < 0.5f);
        var cost = campaign.Defines.StateIdentityStabilityCost * (against ? 1f : 0.25f);
        if (cost > 0f) nation.Stability = M.Clamp01(nation.Stability - cost);
        campaign.Chronicle.Record(campaign, "state_identity_declared", nation.Id, isPublic: true,
            subject: faithChange && !cultureChange ? StateIdentity.Name(Religion, true)
                : cultureChange && !faithChange ? DemographyCatalog.CultureName(Culture)
                : DemographyCatalog.CultureName(Culture));
        campaign.MarkStrategicChange();
    }
}
