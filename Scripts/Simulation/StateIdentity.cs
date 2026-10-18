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
    /// The share of the country a faith or a people must already be before the state may
    /// declare itself theirs. Not a fixed constant: a mod running a diaspora state lowers
    /// it, a nation of rooted majorities raises it.
    /// </summary>
    public float StateIdentityShare { get; set; } = 0.15f;

    /// <summary>The stability a declaration costs the country, flat.</summary>
    public float StateIdentityStabilityCost { get; set; } = 0.02f;

    /// <summary>Days after a declaration before the state may declare again.</summary>
    public int StateIdentityCooldownDays { get; set; } = 1825;
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

    public static bool IsSecular(string id) => !string.IsNullOrWhiteSpace(id) && Secular.Contains(id);

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

    /// <summary>Demographic faiths plus available secular policies, once per identity, not once per source.</summary>
    public static List<(string Id, float Share)> ReligionChoices(Campaign campaign, Nation nation)
    {
        var result = Candidates(campaign, nation, true).Take(8).ToList();
        var seen = new HashSet<string>(result.Select(choice => choice.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var id in Secular.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (seen.Contains(id)) continue;
            var share = ReligionShare(campaign, nation, id);
            if (share <= 0 && DemographyCatalog.Religion(id) is null) continue;
            seen.Add(id);
            result.Add((id, share));
        }
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
        if (campaign is null || !campaign.FeatureEnabled("state_identity")) return;
        foreach (var nation in campaign.Nations)
        {
            if (nation.DeclaredReligion.Length > 0 || nation.DeclaredCulture.Length > 0) continue;
            var faiths = Candidates(campaign, nation, true);
            var peoples = Candidates(campaign, nation, false);
            nation.DeclaredReligion = faiths.Count > 0 ? faiths[0].Id : "";
            nation.DeclaredCulture = peoples.Count > 0 ? peoples[0].Id : "";
            nation.IdentityChangedDay = campaign.ElapsedDays;
        }
    }

    /// <summary>One dossier line for each declaration, where the player reads a country.</summary>
    public static string Caption(Campaign campaign, Nation nation, bool religion)
    {
        var id = religion ? nation.DeclaredReligion : nation.DeclaredCulture;
        if (string.IsNullOrWhiteSpace(id)) return Loc.Get("none declared");
        return religion ? DemographyCatalog.ReligionName(id) : DemographyCatalog.CultureName(id);
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
            subject: faithChange && !cultureChange ? DemographyCatalog.ReligionName(Religion)
                : cultureChange && !faithChange ? DemographyCatalog.CultureName(Culture)
                : DemographyCatalog.CultureName(Culture));
        campaign.MarkStrategicChange();
    }
}
